using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.App.Services;
using System.IO;
using ReyEngine.Core.Decoding;

namespace ReyEngine.App.ViewModels;

/// <summary>What kind of scene colour a row controls. Map surfaces, placed-character diffuse textures
/// and lightmaps all use the identical decode/adjust/encode path; the kind is for grouping and clear UI
/// labelling, not a different codec.</summary>
public enum RecolorTargetKind { Diffuse, MapAndPropDiffuse, PropDiffuse, Lightmap }

/// <summary>One texture in the recolour list.</summary>
public sealed partial class RecolorTargetViewModel : ObservableObject
{
    public required RecolorTarget Target { get; init; }
    public required string Name { get; init; }
    public required string Folder { get; init; }
    public RecolorTargetKind Kind { get; init; } = RecolorTargetKind.Diffuse;
    public bool IsLightmap => Kind == RecolorTargetKind.Lightmap;
    public bool IsProp => Kind is RecolorTargetKind.PropDiffuse or RecolorTargetKind.MapAndPropDiffuse;
    public bool HasKindBadge => IsLightmap || IsProp;
    public string KindBadge => IsLightmap ? "LM" : Kind == RecolorTargetKind.MapAndPropDiffuse ? "MAP+PROP" : "PROP";
    public string KindTip => IsLightmap
        ? "Baked lightmap atlas - recolouring this tints ambient and bounced light."
        : Kind == RecolorTargetKind.MapAndPropDiffuse
            ? "Diffuse colour used by both map geometry and placed mobs or animated props."
            : "Diffuse colour used by placed mobs or animated props.";
    public int MapUses { get; init; }
    public int PropUses { get; init; }
    public int LightmapUses { get; init; }
    /// <summary>Total material, placement and lightmap-group references, used for ranking.</summary>
    public int UsedBy => MapUses + PropUses + LightmapUses;
    /// <summary>Set when this texture already carries a saved recolour.</summary>
    [ObservableProperty] private bool _isRecolored;
    [ObservableProperty] private bool _isSelected = true;

    public string Subtitle
    {
        get
        {
            var uses = new List<string>();
            if (MapUses > 0) uses.Add($"{MapUses:n0} map material{(MapUses == 1 ? "" : "s")}");
            if (PropUses > 0) uses.Add($"{PropUses:n0} mob/prop placement{(PropUses == 1 ? "" : "s")}");
            if (LightmapUses > 0) uses.Add($"{LightmapUses:n0} lightmap group{(LightmapUses == 1 ? "" : "s")}");
            return uses.Count > 0 ? $"{Folder}  ·  {string.Join(" · ", uses)}" : Folder;
        }
    }
}

/// <summary>M171/M311: the Recolor Textures tool. Picks up map surfaces, placed mobs / animated props and
/// lightmaps, applies one colour adjustment across the chosen textures, and writes them into the project.
///
/// The whole tool is built around NOT editing its own output. The sliders are a description of the edit;
/// applying them always starts from the pristine Riot texture. That is what makes the tool safe to leave
/// open and fiddle with — the twentieth pass over a texture is exactly as sharp as the first, whereas
/// chaining edits destructively measures 28.6 dB after ten passes against 40.0 dB for re-derivation.</summary>
public sealed partial class TextureRecolorViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<RecolorTargetViewModel>> _gatherTargets;
    private readonly Func<RecolorTarget, byte[]?> _readBase;
    private readonly Func<TextureRecolorService?> _service;
    private readonly Action<TextureAdjustment, IReadOnlyList<RecolorTarget>> _persist;
    private readonly Func<IReadOnlyList<RecolorTarget>, int> _revert;
    private readonly Action<RecolorRunResult> _onDone;
    private readonly Func<Task<string?>>? _pickLutFile;

    private CancellationTokenSource? _cts;
    /// <summary>Decoded, downscaled ORIGINAL of the previewed texture. Kept so slider moves re-shade a
    /// small image instead of decoding a 2048^2 BC block every frame.</summary>
    private TextureImage? _previewSource;

    public TextureRecolorViewModel(
        Func<IReadOnlyList<RecolorTargetViewModel>> gatherTargets,
        Func<RecolorTarget, byte[]?> readBase,
        Func<TextureRecolorService?> service,
        Action<TextureAdjustment, IReadOnlyList<RecolorTarget>> persist,
        Func<IReadOnlyList<RecolorTarget>, int> revert,
        Action<RecolorRunResult> onDone,
        Func<Task<string?>>? pickLutFile = null)
    {
        _pickLutFile = pickLutFile;
        _gatherTargets = gatherTargets;
        _readBase = readBase;
        _service = service;
        _persist = persist;
        _revert = revert;
        _onDone = onDone;
        _ = RefreshAsync();
    }

    // ---------------------------------------------------------------- the list

    public ObservableCollection<RecolorTargetViewModel> Targets { get; } = new();
    private List<RecolorTargetViewModel> _allTargets = new();

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private RecolorTargetViewModel? _selected;
    [ObservableProperty] private string _listSummary = "";

    partial void OnFilterChanged(string value) => ApplyFilter();

    partial void OnSelectedChanged(RecolorTargetViewModel? value) => _ = LoadPreviewAsync(value);

    /// <summary>Decode the picked texture for preview off the UI thread — a 2048^2 BC3 decode is ~60 ms,
    /// which is a visible hitch when arrowing down the list. Late results are dropped so a quick scroll
    /// can't leave an older texture on screen.</summary>
    private async Task LoadPreviewAsync(RecolorTargetViewModel? value)
    {
        if (value is null)
        {
            _previewSource = null; BeforeImage = null; UpdatePreview();
            return;
        }
        var token = ++_previewToken;
        var img = await Task.Run(() => TextureRecolor.TryDecodePreview(SafeRead(value.Target), 320));
        if (token != _previewToken) return;
        _previewSource = img;
        BeforeImage = ToBitmap(_previewSource);
        UpdatePreview();
    }

    private int _previewToken;

    [ObservableProperty] private bool _isLoading;

    /// <summary>Rebuild the list. Off the UI thread on purpose: working out which of a map's textures can
    /// be recoloured means pulling each one out of the WAD, and on Map11 that is ~370 MB of zstd — plenty
    /// to freeze the window if it ran inline.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        ListSummary = "Reading the map's textures…";
        try
        {
            var gathered = await Task.Run(() => _gatherTargets().ToList());
            foreach (var old in _allTargets) old.PropertyChanged -= OnTargetPropertyChanged;
            _allTargets = gathered;
            foreach (var t in _allTargets) t.PropertyChanged += OnTargetPropertyChanged;
            ApplyFilter();
            Selected ??= Targets.FirstOrDefault();
        }
        catch (Exception ex) { ListSummary = "Could not read the texture list: " + ex.Message; }
        finally { IsLoading = false; }
    }

    /// <summary>Ticking a row's checkbox has to move the summary and re-arm Apply/Revert; watching the
    /// items is what keeps that working no matter how the view flips them (checkbox, keyboard, Select all).</summary>
    private void OnTargetPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RecolorTargetViewModel.IsSelected)
                           or nameof(RecolorTargetViewModel.IsRecolored)) UpdateSummary();
    }

    private void ApplyFilter()
    {
        var f = Filter?.Trim() ?? "";
        Targets.Clear();
        foreach (var t in _allTargets)
        {
            if (f.Length > 0
                && t.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0
                && t.Folder.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
            Targets.Add(t);
        }
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int chosen = _allTargets.Count(t => t.IsSelected);
        int recolored = _allTargets.Count(t => t.IsRecolored);
        int propTextures = _allTargets.Count(t => t.IsProp);
        int lightmaps = _allTargets.Count(t => t.IsLightmap);
        ListSummary = _allTargets.Count == 0
            ? "No textures — open a map first."
            : $"{chosen:n0} of {_allTargets.Count:n0} selected"
              + (Targets.Count != _allTargets.Count ? $"  ·  {Targets.Count:n0} shown" : "")
              + (propTextures > 0 ? $"  ·  {propTextures:n0} mob/prop" : "")
              + (lightmaps > 0 ? $"  ·  {lightmaps:n0} lightmap" : "")
              + (recolored > 0 ? $"  ·  {recolored:n0} already recoloured" : "");
        OnPropertyChanged(nameof(CanApply));
        ApplyCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
    }

    // Deliberately act on Targets (what is VISIBLE) rather than _allTargets: with a filter typed in,
    // "Select all" meaning "everything including the 3,000 rows you filtered out" would be a trap.
    [RelayCommand] private void SelectAll() { foreach (var t in Targets) t.IsSelected = true; UpdateSummary(); }
    [RelayCommand] private void SelectNone() { foreach (var t in Targets) t.IsSelected = false; UpdateSummary(); }

    private IReadOnlyList<RecolorTarget> ChosenTargets() =>
        _allTargets.Where(t => t.IsSelected).Select(t => t.Target).ToList();

    // ---------------------------------------------------------------- the sliders

    [ObservableProperty] private double _hueDegrees;
    [ObservableProperty] private double _saturation = 1;
    [ObservableProperty] private double _brightness = 1;
    [ObservableProperty] private double _contrast = 1;
    [ObservableProperty] private double _inputBlack;
    [ObservableProperty] private double _inputWhite = 1;
    [ObservableProperty] private double _gamma = 1;
    [ObservableProperty] private double _tintR = 1;
    [ObservableProperty] private double _tintG = 1;
    [ObservableProperty] private double _tintB = 1;
    [ObservableProperty] private double _strength = 1;

    partial void OnHueDegreesChanged(double value) => UpdatePreview();
    partial void OnSaturationChanged(double value) => UpdatePreview();
    partial void OnBrightnessChanged(double value) => UpdatePreview();
    partial void OnContrastChanged(double value) => UpdatePreview();
    partial void OnInputBlackChanged(double value) => UpdatePreview();
    partial void OnInputWhiteChanged(double value) => UpdatePreview();
    partial void OnGammaChanged(double value) => UpdatePreview();
    partial void OnTintRChanged(double value) => UpdatePreview();
    partial void OnTintGChanged(double value) => UpdatePreview();
    partial void OnTintBChanged(double value) => UpdatePreview();
    partial void OnStrengthChanged(double value) => UpdatePreview();

    public TextureAdjustment CurrentAdjustment => new TextureAdjustment(
        (float)HueDegrees, (float)Saturation, (float)Brightness, (float)Contrast,
        (float)InputBlack, (float)InputWhite, (float)Gamma,
        (float)TintR, (float)TintG, (float)TintB, (float)Strength)
    { Lut = _lut, LutStrength = (float)LutStrength };

    // ---------------------------------------------------------------- colour grade (.cube)

    private CubeLut? _lut;

    [ObservableProperty] private string _lutName = "";
    [ObservableProperty] private bool _hasLut;
    [ObservableProperty] private double _lutStrength = 1.0;

    partial void OnLutStrengthChanged(double value) => UpdatePreview();

    /// <summary>Load an Adobe/IRIDAS .cube grade. Applied AFTER the sliders, because a .cube is authored
    /// as a final look — it expects to see the image the way it would be delivered.</summary>
    [RelayCommand]
    private async Task LoadLutAsync()
    {
        if (_pickLutFile is null) return;
        var path = await _pickLutFile();
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var lut = await Task.Run(() => CubeLut.Load(path));
            _lut = lut;
            HasLut = true;
            LutName = $"{(lut.Title.Length > 0 ? lut.Title : Path.GetFileNameWithoutExtension(path))}"
                      + $"  ·  {lut.Size}{(lut.Is1D ? " (1D)" : "³")}"
                      + (lut.IsIdentity() ? "  ·  identity, no effect" : "");
            Status = "";
            UpdatePreview();
        }
        catch (Exception ex)
        {
            _lut = null; HasLut = false; LutName = "";
            Status = "Could not load that .cube: " + ex.Message;
            UpdatePreview();
        }
    }

    [RelayCommand]
    private void ClearLut()
    {
        _lut = null; HasLut = false; LutName = "";
        UpdatePreview();
    }

    [RelayCommand]
    private void ResetAdjustment()
    {
        HueDegrees = 0; Saturation = 1; Brightness = 1; Contrast = 1;
        InputBlack = 0; InputWhite = 1; Gamma = 1;
        TintR = 1; TintG = 1; TintB = 1; Strength = 1;
    }

    /// <summary>Starting points, not destinations — each one is just a slider position the user can keep
    /// tuning. Named for the look they produce on Summoner's Rift rather than for the maths.</summary>
    [RelayCommand]
    private void ApplyPreset(string? preset)
    {
        ResetAdjustment();
        switch (preset)
        {
            case "Night":      Brightness = 0.62; Saturation = 0.75; TintR = 0.72; TintG = 0.82; TintB = 1.15; break;
            case "Sunset":     HueDegrees = -12; Saturation = 1.25; TintR = 1.18; TintG = 0.95; TintB = 0.80; break;
            case "Frozen":     HueDegrees = 15; Saturation = 0.55; Brightness = 1.08; TintR = 0.86; TintG = 0.98; TintB = 1.20; break;
            case "Infernal":   HueDegrees = -25; Saturation = 1.35; Contrast = 1.15; TintR = 1.25; TintG = 0.82; TintB = 0.70; break;
            case "Desaturate": Saturation = 0.25; break;
            case "Contrast":   Contrast = 1.25; InputBlack = 0.04; InputWhite = 0.96; break;
        }
    }

    // ---------------------------------------------------------------- preview

    [ObservableProperty] private Bitmap? _beforeImage;
    [ObservableProperty] private Bitmap? _afterImage;

    private void UpdatePreview()
    {
        AfterImage = _previewSource is null ? null : ToBitmap(CurrentAdjustment.Apply(_previewSource));
        OnPropertyChanged(nameof(CanApply));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private byte[]? SafeRead(RecolorTarget t)
    {
        try { return _readBase(t); } catch { return null; }
    }

    /// <summary>RGBA8 → an Avalonia bitmap. Written by hand because TextureImage is already the exact
    /// memory layout WriteableBitmap wants, so this is a single copy with no encoder in the middle.</summary>
    public static Bitmap? ToBitmap(TextureImage? img)
    {
        if (img is null || img.Width <= 0 || img.Height <= 0) return null;
        var bmp = new WriteableBitmap(
            new PixelSize(img.Width, img.Height), new Vector(96, 96),
            PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using (var fb = bmp.Lock())
        {
            int stride = img.Width * 4;
            for (int y = 0; y < img.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(
                    img.Rgba, y * stride, fb.Address + y * fb.RowBytes, stride);
        }
        return bmp;
    }

    // ---------------------------------------------------------------- running

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _stage = "";
    [ObservableProperty] private string _status = "";

    public bool CanApply => !IsRunning && !CurrentAdjustment.IsIdentity && _allTargets.Any(t => t.IsSelected);

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanApply));
        ApplyCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (_service() is not { } service)
        {
            Status = "This project has nowhere to write to — save the project first.";
            return;
        }
        var targets = ChosenTargets();
        if (targets.Count == 0) { Status = "Nothing selected."; return; }

        _cts = new CancellationTokenSource();
        IsRunning = true;
        Progress = 0;
        Stage = "Recolouring";
        Status = "";
        try
        {
            var adjustment = CurrentAdjustment;
            var progress = new Progress<RecolorProgress>(p =>
            {
                Progress = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
                Stage = p.Current.Length > 0 ? $"Recolouring {System.IO.Path.GetFileName(p.Current)}" : "Recolouring";
            });

            var result = await service.RunAsync(targets, adjustment, progress, _cts.Token);
            _persist(adjustment, targets);

            foreach (var t in _allTargets)
                if (t.IsSelected) t.IsRecolored = true;

            Status = $"{result.Written:n0} texture(s) recoloured"
                     + (result.Skipped > 0 ? $", {result.Skipped:n0} skipped" : "")
                     + (result.Failed > 0 ? $", {result.Failed:n0} failed" : "")
                     + $"  ({result.BytesWritten / 1048576.0:F1} MB).";
            _onDone(result);
            UpdateSummary();
        }
        catch (OperationCanceledException) { Status = "Cancelled."; }
        catch (Exception ex) { Status = "Failed: " + ex.Message; }
        finally
        {
            IsRunning = false;
            Stage = "";
            Progress = 0;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private bool CanCancel => IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    public bool CanRevert => !IsRunning && _allTargets.Any(t => t.IsSelected && t.IsRecolored);

    /// <summary>Put the selected textures back to Riot's originals — deletes the project's recoloured
    /// copies and forgets the saved sliders. Without this a whole-map recolour would be a one-way door.</summary>
    [RelayCommand(CanExecute = nameof(CanRevert))]
    private void Revert()
    {
        var targets = _allTargets.Where(t => t.IsSelected && t.IsRecolored).ToList();
        if (targets.Count == 0) return;
        int n = _revert(targets.Select(t => t.Target).ToList());
        foreach (var t in targets) t.IsRecolored = false;
        Status = $"{n:n0} texture(s) restored to the original.";
        UpdateSummary();
        _ = LoadPreviewAsync(Selected);   // re-read the preview from the now-restored source
    }
}
