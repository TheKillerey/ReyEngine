using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.App.Services;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Meta;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M824: one body texture in the BODY RECOLOUR list - an include switch, what the texture is, and what the recolour can do
/// with it. A texture the writer cannot write back (DDS, BC5, BGRA8) or whose original cannot be read is listed with the
/// reason and cannot be switched on: nothing is skipped silently.
/// </summary>
public sealed partial class ChromaTextureRowViewModel : ObservableObject
{
    private readonly MeshPreviewViewModel _owner;

    public ChromaTextureRowViewModel(MeshPreviewViewModel owner, ChromaTarget target, string name, string detail, string role,
        bool isOutside, bool isOptIn, IReadOnlyList<string> sharedWith, bool include, string tip)
    {
        _owner = owner;
        Target = target;
        Name = name;
        Detail = detail;
        Role = role;
        IsOutside = isOutside;
        IsOptIn = isOptIn;
        SharedWith = sharedWith;
        Tip = tip;
        _isIncluded = include;
    }

    public ChromaTarget Target { get; }
    public ulong Hash => Target.Hash;
    public string Name { get; }
    public string Detail { get; }
    /// <summary>A short lower-case label: "diffuse", "emissive", "excluded"...</summary>
    public string Role { get; }
    public string Tip { get; }
    public bool IsOutside { get; }
    /// <summary>A sampler the inventory left out (a mask, a normal map, a data map): listed only on request, off until the user
    /// switches it on.</summary>
    public bool IsOptIn { get; }
    public IReadOnlyList<string> SharedWith { get; }
    public bool IsShared => SharedWith.Count > 0;
    public string SharedBadge => SharedWith.Count > 1 ? $"SHARED x{SharedWith.Count}" : "SHARED";
    public string SharedTip => SharedWith.Count == 0 ? "" : "Also used by: " + string.Join(", ", SharedWith.Take(ChromaRowViewModel.MaxNamedSharers))
        + (SharedWith.Count > ChromaRowViewModel.MaxNamedSharers ? $" and {SharedWith.Count - ChromaRowViewModel.MaxNamedSharers} more" : "")
        + ". Recolouring this file recolours it for all of them.";
    public string OutsideBadge => "OUTSIDE";

    [ObservableProperty] private bool _isIncluded;

    /// <summary>False when the texture cannot be written back or its original cannot be read.</summary>
    [ObservableProperty] private bool _canInclude = true;

    /// <summary>Why the texture cannot be recoloured, or how it stands ("recoloured in the project"), or empty.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasNote))] private string _note = "";

    public bool HasNote => Note.Length > 0;

    /// <summary>The project holds its own copy of this texture that is not Riot's and that no recolour record accounts for - somebody
    /// edited or replaced it. Left off by default; switching it on means saving replaces that edit.</summary>
    [ObservableProperty] private bool _isEditedOutside;

    partial void OnIsIncludedChanged(bool value) => _owner.OnChromaIncludeChanged(this);
}

/// <summary>
/// M824: Chroma Studio C3 - the Character window's BODY RECOLOUR.
///
/// <para>Sliders drive one <see cref="ColorTransform"/> (M813) over the body textures the user includes. While a slider moves,
/// the included textures are recoloured FROM THEIR ORIGINAL texels (never compounding) on a worker and pushed into the
/// renderer that is showing the character - the D3D11 texture pool (mips regenerated) or the GL viewport's textures. Save
/// writes each texture into the project through the Recolor Textures pipeline (re-derived from the pristine original, TEX
/// BC1/BC3) and records the transform in project.json, so a reload restores the sliders and the textures, and Build Package
/// and .fantome ship the files.</para>
///
/// <para><b>What this half is.</b> The numbers, the list, the preview loop and the commands. Everything that touches the project
/// - reading the pristine bytes, writing the files, the records, the mounts - is the host's, behind the hooks below
/// (<c>MainWindowViewModel.ChromaRecolour.cs</c>), the way <see cref="ScanSkinColours"/> is.</para>
///
/// <para><b>The material colour PARAMETERS</b> (<c>TintColor</c> and its like) are the second half of the same recolour (M825,
/// <c>MeshPreviewViewModel.ChromaParameters.cs</c>): the same sliders, the same pending state, saved by the same Apply &amp; Save / Ctrl+S,
/// but into the skin bin through the bin save path rather than into texture files.</para>
///
/// <para><b>Preview rate.</b> One request at a time is rendered and only the NEWEST is kept: a 2048 x 2048 texture takes ~130 ms
/// in Release (M813), so a slider drag updates the picture a few times a second on the biggest textures and at full rate on
/// small ones, and the last position is always drawn. The D3D11 pool texture is replaced at its own size, so the preview is
/// full resolution, not a reduced copy.</para>
/// </summary>
public sealed partial class MeshPreviewViewModel
{
    // ---- hooks (the host's and the window's) -------------------------------------------------------------

    /// <summary>The PRISTINE .tex bytes of a body texture - Riot's original, never the project's recoloured copy - or null when they
    /// cannot be read. Called on a worker.</summary>
    public Func<ChromaTarget, byte[]?>? ReadChromaOriginal { get; set; }

    /// <summary>Writes the recolour into the project: (skin bin, transform, textures to recolour, textures of this skin's earlier
    /// recipe that are no longer part of it) -> what happened. Throws when it cannot write at all.</summary>
    public Func<string, ColorTransform, IReadOnlyList<ChromaTarget>, IReadOnlyList<ChromaTarget>, Task<ChromaSaveResult>>? SaveChromaRecolour { get; set; }

    /// <summary>Puts these textures back to Riot's (files and records), returning how many it restored.</summary>
    public Func<string, IReadOnlyList<ChromaTarget>, int>? RevertChromaRecolour { get; set; }

    /// <summary>Takes the host's readers (the mounts and the archive) for as long as the returned object lives. Called on the UI thread BEFORE a
    /// worker reads originals, so a rebuild of the mounts waits for the read - as the colour scan does. Null: nothing to hold.</summary>
    public Func<IDisposable>? AcquireChromaReaders { get; set; }

    /// <summary>Whether the project's copy of this texture was edited outside the recolour (see <see cref="ChromaTextureRowViewModel.IsEditedOutside"/>).
    /// Called on a worker, under the readers' lease. Null: nothing is.</summary>
    public Func<ChromaTarget, bool>? IsChromaProjectEdited { get; set; }

    /// <summary>The recipe project.json holds for this skin bin, or null.</summary>
    public Func<string, ChromaSavedRecipe?>? ReadChromaSaved { get; set; }

    /// <summary>Why a recolour cannot be saved right now ("Open or create a project..."), or null when it can.</summary>
    public Func<string?>? ChromaSaveBlocker { get; set; }

    /// <summary>The window's: replace pooled D3D11 textures (key, RGBA8, size), regenerate their mips and draw a frame.</summary>
    public Action<IReadOnlyList<(string Key, byte[] Rgba, int Width, int Height)>>? PushChromaDx11 { get; set; }

    /// <summary>The host's: copy these into the GL viewport's textures for the submeshes that draw them and queue the upload.</summary>
    public Action<IReadOnlyList<ChromaPushItem>>? PushChromaGl { get; set; }

    /// <summary>The window's: queue the upload of a GL image the host changed in place, and rebuild the GL mips after a push.</summary>
    public Action<TextureImage, Avalonia.PixelRect>? QueueGlTextureUpdate { get; set; }
    public Action? RebuildGlTextureMips { get; set; }

    /// <summary>The host's: called when a recolour becomes pending, so it can arm its auto-save (a flush saves it).</summary>
    public Action? ChromaEdited { get; set; }

    /// <summary>Runs the action on the thread the renderers live on and completes when it has run. The default is the Avalonia UI
    /// thread; a test with no dispatcher runs it inline.</summary>
    public Func<Action, Task> ChromaUiPost { get; set; } = action => Dispatcher.UIThread.InvokeAsync(action).GetTask();

    /// <summary>The host's cache of which GL images draw which chunk; dropped when the texture list is replaced.</summary>
    internal object? ChromaGlImageMap { get; set; }

    // ---- the sliders -------------------------------------------------------------------------------------

    [ObservableProperty] private double _chromaHue;
    [ObservableProperty] private double _chromaSaturation = 1;
    [ObservableProperty] private double _chromaBrightness = 1;
    [ObservableProperty] private bool _chromaColorize;
    [ObservableProperty] private double _chromaColorizeHue;
    [ObservableProperty] private bool _chromaRangeOn;
    [ObservableProperty] private double _chromaRangeCenter;
    [ObservableProperty] private double _chromaRangeWidth = ChromaRecolourSettings.DefaultRangeWidth;
    [ObservableProperty] private double _chromaRangeFeather = ChromaRecolourSettings.DefaultRangeFeather;
    [ObservableProperty] private double _chromaGreyThreshold = ChromaRecolourSettings.DefaultGreyThreshold;
    [ObservableProperty] private double _chromaGreyFeather = ChromaRecolourSettings.DefaultGreyFeather;
    [ObservableProperty] private double _chromaStrength = 1;

    partial void OnChromaHueChanged(double value) => OnChromaSettingChanged();
    partial void OnChromaSaturationChanged(double value) => OnChromaSettingChanged();
    partial void OnChromaBrightnessChanged(double value) => OnChromaSettingChanged();
    partial void OnChromaColorizeChanged(bool value) => OnChromaSettingChanged();
    partial void OnChromaColorizeHueChanged(double value) => OnChromaSettingChanged();
    partial void OnChromaRangeOnChanged(bool value) => OnChromaSettingChanged();
    partial void OnChromaRangeCenterChanged(double value) => OnChromaSettingChanged();
    partial void OnChromaRangeWidthChanged(double value) => OnChromaSettingChanged();
    partial void OnChromaRangeFeatherChanged(double value) => OnChromaSettingChanged();
    partial void OnChromaGreyThresholdChanged(double value) => OnChromaSettingChanged();
    partial void OnChromaGreyFeatherChanged(double value) => OnChromaSettingChanged();
    partial void OnChromaStrengthChanged(double value) => OnChromaSettingChanged();

    /// <summary>The sliders as one value. Setting it moves the sliders WITHOUT a change pass (a restore is not an edit).</summary>
    public ChromaRecolourSettings ChromaSettings
    {
        get => new()
        {
            HueShift = (float)ChromaHue, Saturation = (float)ChromaSaturation, Brightness = (float)ChromaBrightness,
            Colorize = ChromaColorize, ColorizeHue = (float)ChromaColorizeHue,
            RangeOn = ChromaRangeOn, RangeCenter = (float)ChromaRangeCenter, RangeWidth = (float)ChromaRangeWidth, RangeFeather = (float)ChromaRangeFeather,
            GreyThreshold = (float)ChromaGreyThreshold, GreyFeather = (float)ChromaGreyFeather, Strength = (float)ChromaStrength,
        };
        set
        {
            bool was = _chromaRestoring;
            _chromaRestoring = true;
            try
            {
                ChromaHue = value.HueShift; ChromaSaturation = value.Saturation; ChromaBrightness = value.Brightness;
                ChromaColorize = value.Colorize; ChromaColorizeHue = value.ColorizeHue;
                ChromaRangeOn = value.RangeOn; ChromaRangeCenter = value.RangeCenter; ChromaRangeWidth = value.RangeWidth; ChromaRangeFeather = value.RangeFeather;
                ChromaGreyThreshold = value.GreyThreshold; ChromaGreyFeather = value.GreyFeather; ChromaStrength = value.Strength;
            }
            finally { _chromaRestoring = was; }
        }
    }

    // ---- the list and the state --------------------------------------------------------------------------

    /// <summary>The textures the recolour can act on (and, with <see cref="ChromaShowExcluded"/>, the maps the inventory left out).</summary>
    public ObservableCollection<ChromaTextureRowViewModel> ChromaTextures { get; } = new();

    /// <summary>Show the masks, normal maps and data maps the inventory excluded, so one can be switched on. Off by default: hue
    /// shifting a mask corrupts the lighting it carries.</summary>
    [ObservableProperty] private bool _chromaShowExcluded;
    partial void OnChromaShowExcludedChanged(bool value) => RefreshChromaVisibleRows();

    /// <summary>The recolour section is usable: a skin is on screen and its textures are listed.</summary>
    [ObservableProperty] private bool _hasChromaTextures;

    /// <summary>What the card says about the recolour: what is saved, what is pending, what the last save did.</summary>
    [ObservableProperty] private string _chromaRecolourStatus = "";

    /// <summary>"This also changes 3 other skins..." - empty when nothing included is shared. See <see cref="BuildChromaShareWarning"/>.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChromaShareWarning))] private string _chromaShareWarning = "";
    public bool HasChromaShareWarning => ChromaShareWarning.Length > 0;

    /// <summary>The sliders or the included set differ from what the project holds, so a save would write something. Ctrl+S, the
    /// autosave and Build Package / Export flush it.</summary>
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ApplyChromaRecolourCommand)), NotifyPropertyChangedFor(nameof(HasPendingChromaRecolour))]
    private bool _chromaRecolourDirty;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ApplyChromaRecolourCommand)), NotifyCanExecuteChangedFor(nameof(RevertChromaRecolourCommand)),
     NotifyPropertyChangedFor(nameof(HasPendingChromaRecolour))]
    private bool _chromaSaving;

    /// <summary>The project holds a body recolour of this skin (a saved recipe), so Revert has something to put back.</summary>
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RevertChromaRecolourCommand))] private bool _hasSavedChromaRecolour;

    /// <summary>A recolour is waiting to be saved.</summary>
    public bool HasPendingChromaRecolour => ChromaRecolourDirty && !ChromaSaving;

    private readonly ChromaPreviewBuffers _chromaBuffers = new();
    private readonly object _chromaGate = new();
    private readonly List<ChromaTextureRowViewModel> _chromaRows = new();
    private bool _chromaRestoring;
    private int _chromaGeneration;
    private ColorTransform _chromaSavedTransform = ColorTransform.Identity;
    private IReadOnlyList<ChromaTarget> _chromaSavedTargets = Array.Empty<ChromaTarget>();
    private HashSet<ulong> _chromaSavedHashes = new();

    // the newest preview request and the loop that serves it (see RequestChromaPreview). All of it under _chromaGate.
    private (ColorTransform Transform, ulong[] Included, HashSet<ulong> Saved, int Generation, ChromaParamPreview Params) _chromaRequest;
    private int _chromaSerial;
    private int _chromaRunning;
    private HashSet<ulong> _chromaShown = new();       // chunks the renderers draw as a recolour right now
    private HashSet<ulong> _chromaRestore = new();     // chunks to draw as their originals once (a revert)
    private Task _chromaLoop = Task.CompletedTask;
    private Task _chromaPrepare = Task.CompletedTask;
    private CancellationTokenSource _chromaCts = new();

    /// <summary>For a test: completes when the texture list has been classified and the preview loop has served every request made so
    /// far.</summary>
    public async Task ChromaIdleAsync()
    {
        await _chromaPrepare;
        await _chromaParamPrepare;
        for (int i = 0; i < 2000; i++)
        {
            await _chromaLoop;
            if (Volatile.Read(ref _chromaRunning) == 0) return;
            await Task.Delay(5);
        }
    }

    // ---- the card's lifecycle ----------------------------------------------------------------------------

    /// <summary>The skin changed (or the card was switched off): forget the previous skin's recolour, then restore what the project
    /// holds for the new one. Called by <see cref="SetChromaSkin"/>.</summary>
    private void ResetChromaRecolour(string? skinBin)
    {
        _chromaCts.Cancel();
        _chromaCts = new CancellationTokenSource();
        Interlocked.Increment(ref _chromaGeneration);
        _chromaBuffers.Clear();
        _chromaRows.Clear();
        ChromaTextures.Clear();
        lock (_chromaGate) { _chromaShown = new HashSet<ulong>(); _chromaRestore = new HashSet<ulong>(); }
        _chromaPrepare = Task.CompletedTask;
        HasChromaTextures = false;
        ChromaShareWarning = "";

        _chromaSavedTransform = ColorTransform.Identity;
        _chromaSavedTargets = Array.Empty<ChromaTarget>();
        _chromaSavedHashes = new HashSet<ulong>();
        ChromaSettings = ChromaRecolourSettings.Default;
        ChromaRecolourDirty = false;
        HasSavedChromaRecolour = false;
        ChromaRecolourStatus = "";
        ChromaGlImageMap = null;

        var saved = skinBin is null ? null : ReadChromaSaved?.Invoke(skinBin);
        ResetChromaParameters(saved);   // M825
        if (saved is null) return;
        if (saved.Targets.Count > 0)
        {
            _chromaSavedTransform = saved.Transform;
            _chromaSavedTargets = saved.Targets;
            _chromaSavedHashes = saved.Targets.Select(t => t.Hash).ToHashSet();
        }
        ChromaSettings = ChromaRecolourSettings.From(saved.Transform);
        HasSavedChromaRecolour = saved.Targets.Count > 0 || saved.SavedParameters.Count > 0;
        ChromaRecolourStatus = $"Saved in the project: {saved.Targets.Count} texture(s) and {saved.SavedParameters.Count} colour parameter(s) recoloured. Press Scan colours to see and change them.";
    }

    /// <summary>The inventory arrived: list its body textures (and, hidden, the maps it left out), switch on the ones the recolour
    /// should act on, and read and classify each original on a worker.</summary>
    private void SetChromaRecolourTextures(SkinColorInventory inventory)
    {
        _chromaCts.Cancel();
        _chromaCts = new CancellationTokenSource();
        int generation = Interlocked.Increment(ref _chromaGeneration);
        _chromaBuffers.Clear();
        lock (_chromaGate) { _chromaShown = new HashSet<ulong>(); _chromaRestore = new HashSet<ulong>(); }
        _chromaRows.Clear();

        var seen = new HashSet<ulong>();
        bool recipe = _chromaSavedHashes.Count > 0 || _chromaSavedParamKeys.Count > 0;   // M825: a recipe of parameters alone is a recipe too
        foreach (var t in inventory.BodyTextures)
        {
            if (!seen.Add(t.Hash)) continue;
            bool outside = SkinColorInventory.IsOutsideCharacter(t.Path, inventory.CharacterFolder);
            // the textures of a saved recipe, else the body's own: what lies outside the character is also used by others and is opt-in
            bool include = recipe ? _chromaSavedHashes.Contains(t.Hash) : !outside;
            string role = t.Role.ToString().ToLowerInvariant();
            _chromaRows.Add(new ChromaTextureRowViewModel(this, new ChromaTarget(t.Hash, t.Path), FileName(t.Path),
                $"{role} · {t.Source}" + (outside ? " · outside this character" : ""), role, outside, isOptIn: false, t.SharedWith, include,
                $"{t.Path}\n0x{t.Hash:x16}"));
        }
        foreach (var e in inventory.ExcludedSamplers)
        {
            if (string.IsNullOrWhiteSpace(e.Path)) continue;
            ulong hash = BinTexturePath.HashOfReference(e.Path);
            if (hash == 0 || !seen.Add(hash)) continue;
            bool outside = SkinColorInventory.IsOutsideCharacter(e.Path, inventory.CharacterFolder);
            _chromaRows.Add(new ChromaTextureRowViewModel(this, new ChromaTarget(hash, e.Path), FileName(e.Path),
                $"left out: {e.Reason} · {ShortMaterialName(e.Material)} ({e.Sampler})", "excluded", outside, isOptIn: true,
                Array.Empty<string>(), _chromaSavedHashes.Contains(hash), $"{e.Path}\n0x{hash:x16}\nNot compared with other skins."));
        }

        RefreshChromaVisibleRows();
        HasChromaTextures = _chromaRows.Count > 0;
        UpdateChromaDirty();
        _chromaPrepare = PrepareChromaTexturesAsync(generation);
        _chromaParamPrepare = PrepareChromaParametersAsync(generation);   // M825: the skin bin's colour parameters, beside the textures
    }

    private void RefreshChromaVisibleRows()
    {
        ChromaTextures.Clear();
        foreach (var r in _chromaRows)
            if (!r.IsOptIn || ChromaShowExcluded || r.IsIncluded) ChromaTextures.Add(r);
    }

    private sealed record ChromaClassified(ChromaTextureRowViewModel Row, bool Supported, string Reason, TextureImage? Decoded, bool Edited = false);

    /// <summary>Read every listed original once, say which can be written back, and decode the included ones for the preview.</summary>
    private async Task PrepareChromaTexturesAsync(int generation)
    {
        var read = ReadChromaOriginal;
        var rows = _chromaRows.ToArray();
        if (rows.Length == 0) return;
        if (read is null)
        {
            foreach (var r in rows) { r.CanInclude = false; r.Note = "The original texture cannot be read here."; }
            return;
        }

        var wanted = rows.Where(r => r.IsIncluded).Select(r => r.Hash).ToHashSet();
        var saved = _chromaSavedHashes;
        var isEdited = IsChromaProjectEdited;
        using var readers = AcquireChromaReaders?.Invoke();   // on this thread, before the worker starts
        var token = _chromaCts.Token;
        List<ChromaClassified> classified;
        try
        {
            classified = await Task.Run(() =>
            {
                var list = new List<ChromaClassified>(rows.Length);
                foreach (var row in rows)
                {
                    if (token.IsCancellationRequested) break;
                    byte[]? bytes;
                    try { bytes = read(row.Target); }
                    catch (Exception ex) { list.Add(new ChromaClassified(row, false, "The original could not be read: " + ex.Message, null)); continue; }
                    if (bytes is null) { list.Add(new ChromaClassified(row, false, "The original texture was not found in the game files or the project.", null)); continue; }
                    var verdict = TextureRecolor.Classify(bytes);
                    if (!verdict.Ok) { list.Add(new ChromaClassified(row, false, DescribeUnsupported(bytes, verdict), null)); continue; }
                    bool edited = false;
                    try { edited = !saved.Contains(row.Hash) && isEdited is not null && isEdited(row.Target); } catch { /* unknown is not edited */ }
                    TextureImage? image = null;
                    if (wanted.Contains(row.Hash) && !edited)
                    {
                        try { image = TextureDecoder.Decode(bytes); }
                        catch (Exception ex) { list.Add(new ChromaClassified(row, false, "The texture could not be decoded: " + ex.Message, null)); continue; }
                    }
                    list.Add(new ChromaClassified(row, true, "", image, edited));
                }
                return list;
            }, token);
        }
        catch (OperationCanceledException) { return; }

        if (generation != Volatile.Read(ref _chromaGeneration) || token.IsCancellationRequested) return;

        bool was = _chromaRestoring;
        _chromaRestoring = true;   // the switches below are the classification speaking, not the user
        try
        {
            foreach (var c in classified)
            {
                if (!c.Supported)
                {
                    c.Row.CanInclude = false;
                    c.Row.IsIncluded = false;
                    c.Row.Note = c.Reason;
                    continue;
                }
                if (c.Edited)
                {
                    // the project's own copy, not Riot's and not ours: not part of the recolour unless the user says so
                    c.Row.IsEditedOutside = true;
                    c.Row.IsIncluded = false;
                    c.Row.Note = "The project's copy of this texture was edited outside the recolour. Switch it on only to replace that edit.";
                    continue;
                }
                if (c.Decoded is { } image) _chromaBuffers.Set(c.Row.Hash, image);
                if (_chromaSavedHashes.Contains(c.Row.Hash)) c.Row.Note = "recoloured in the project";
            }
        }
        finally { _chromaRestoring = was; }

        RefreshChromaVisibleRows();
        UpdateChromaDirty();
        // sliders restored from the project, or moved before the originals were in: draw what they say
        RequestChromaPreview();
    }

    private static string DescribeUnsupported(byte[] bytes, RecolorOutcome verdict)
    {
        if (bytes.Length >= 4 && bytes[0] == 'D' && bytes[1] == 'D' && bytes[2] == 'S' && bytes[3] == ' ')
            return "A DDS file (a reflection cubemap, say): the project writer handles TEX BC1/BC3 only, so it is left as it is.";
        return verdict.Skip switch
        {
            RecolorSkip.NotATexture => "Not a Riot .tex container, so it is left as it is.",
            RecolorSkip.UnsupportedFormat => "A pixel format that can be read but not written back (BC5 or BGRA8): left as it is.",
            _ => verdict.Detail,
        };
    }

    /// <summary>A texture was switched on or off (by the user; the classification's own changes are silent).</summary>
    internal void OnChromaIncludeChanged(ChromaTextureRowViewModel row)
    {
        if (_chromaRestoring) return;
        UpdateChromaDirty();
        if (row.IsIncluded && row.CanInclude && !_chromaBuffers.Has(row.Hash))
        {
            // decode it now; the preview is requested when it is in
            _chromaPrepare = DecodeChromaOriginalAsync(row, Volatile.Read(ref _chromaGeneration));
        }
        else RequestChromaPreview();
    }

    private async Task DecodeChromaOriginalAsync(ChromaTextureRowViewModel row, int generation)
    {
        var read = ReadChromaOriginal;
        if (read is null) return;
        using var readers = AcquireChromaReaders?.Invoke();
        var token = _chromaCts.Token;
        TextureImage? image = null;
        string? error = null;
        try
        {
            image = await Task.Run(() => read(row.Target) is { } bytes ? TextureDecoder.Decode(bytes) : null, token);
            if (image is null) error = "The original texture was not found.";
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { error = "The texture could not be decoded: " + ex.Message; }
        if (generation != Volatile.Read(ref _chromaGeneration)) return;

        if (image is null)
        {
            bool was = _chromaRestoring;
            _chromaRestoring = true;
            try { row.CanInclude = false; row.IsIncluded = false; row.Note = error ?? ""; }
            finally { _chromaRestoring = was; }
            UpdateChromaDirty();
            return;
        }
        _chromaBuffers.Set(row.Hash, image);
        RequestChromaPreview();
    }

    // ---- the state: pending or saved ---------------------------------------------------------------------

    private void OnChromaSettingChanged()
    {
        if (_chromaRestoring) return;
        UpdateChromaDirty();
        RequestChromaPreview();
    }

    private IEnumerable<ChromaTextureRowViewModel> IncludedChromaRows => _chromaRows.Where(r => r.IsIncluded && r.CanInclude);

    /// <summary>
    /// The textures of the saved recolour that this card cannot show an include switch for: the scan did not list them, or their original
    /// could not be read or written back here. They are CARRIED - part of the saved state, never targets and never stale - because an
    /// absent switch is not a switched-off one: dropping them would delete the file and the record the next time anything is flushed
    /// (Ctrl+S, an export, a build) and the recolour would vanish without the user having asked. Only an explicit switch-off, a Reset of
    /// the sliders that is then saved, or Revert removes a saved texture.
    /// </summary>
    private List<ChromaTarget> CarriedChromaTargets() =>
        _chromaSavedTargets.Where(t => !_chromaRows.Any(r => r.Hash == t.Hash && r.CanInclude)).ToList();

    /// <summary>What a save would write, in two parts that are saved (and compared with what is saved) on their own: the textures - the
    /// transform and the included ones plus the carried ones - and the colour parameters (M825) the same way. A part with nothing to write
    /// is (identity, none), so "no recolour" is one state however the switches stand.</summary>
    private readonly record struct ChromaState(ColorTransform Transform, HashSet<ulong> Hashes, ColorTransform ParamTransform, HashSet<SkinColorParamKey> Params)
    {
        public bool TexturesDiffer(ColorTransform savedTransform, HashSet<ulong> savedHashes) => !(Transform.Equals(savedTransform) && Hashes.SetEquals(savedHashes));
        public bool ParamsDiffer(ColorTransform savedTransform, HashSet<SkinColorParamKey> savedKeys) => !(ParamTransform.Equals(savedTransform) && Params.SetEquals(savedKeys));
        public bool SameAs(ChromaState other) =>
            Transform.Equals(other.Transform) && Hashes.SetEquals(other.Hashes) && ParamTransform.Equals(other.ParamTransform) && Params.SetEquals(other.Params);
    }

    private ChromaState ChromaStateNow()
    {
        var transform = ChromaSettings.ToTransform();
        var hashes = IncludedChromaRows.Select(r => r.Hash).ToHashSet();
        if (!transform.IsIdentity) foreach (var carried in CarriedChromaTargets()) hashes.Add(carried.Hash);
        var keys = IncludedChromaParamRows.Select(r => r.Key).ToHashSet();
        if (!transform.IsIdentity) foreach (var carried in CarriedChromaParams()) keys.Add(KeyOf(carried));
        bool noTextures = transform.IsIdentity || hashes.Count == 0, noParams = transform.IsIdentity || keys.Count == 0;
        return new ChromaState(noTextures ? ColorTransform.Identity : transform, noTextures ? new HashSet<ulong>() : hashes,
            noParams ? ColorTransform.Identity : transform, noParams ? new HashSet<SkinColorParamKey>() : keys);
    }

    private void UpdateChromaDirty()
    {
        // nothing listed (the skin was not scanned yet): there is nothing to recolour, so nothing is pending
        bool dirty = false;
        if (_chromaRows.Count > 0 || _chromaParamRows.Count > 0)
        {
            var state = ChromaStateNow();
            dirty = state.TexturesDiffer(_chromaSavedTransform, _chromaSavedHashes) || state.ParamsDiffer(_chromaSavedParamTransform, _chromaSavedParamKeys);
        }
        ChromaRecolourDirty = dirty;
        if (dirty) ChromaEdited?.Invoke();
        ChromaShareWarning = BuildChromaShareWarning(IncludedChromaRows.ToArray());
        ChromaRecolourStatus = DescribeChromaState();
    }

    private string DescribeChromaState()
    {
        if (!HasChromaTextures && !HasChromaParameters) return ChromaRecolourStatus;
        int included = IncludedChromaRows.Count(), includedParams = IncludedChromaParamRows.Count();
        bool identity = ChromaSettings.ToTransform().IsIdentity;
        string what = $"{included} texture(s)" + (_chromaParamRows.Count > 0 ? $" and {includedParams} colour parameter(s)" : "");
        if (ChromaRecolourDirty)
            return identity || (included == 0 && includedParams == 0)
                ? (HasSavedChromaRecolour ? "Pending: put the saved recolour back to Riot's textures and colours. Press Apply & Save, or Ctrl+S." : "")
                : $"Pending: {what} will be recoloured. Press Apply & Save, or Ctrl+S.";
        int carried = CarriedChromaTargets().Count + CarriedChromaParams().Count;
        if (HasSavedChromaRecolour)
            return $"Saved in the project: {_chromaSavedTargets.Count} texture(s)" + (_chromaSavedParamRefs.Count > 0 ? $" and {_chromaSavedParamRefs.Count} colour parameter(s)" : "") + " recoloured."
                   + (carried > 0 ? $" {carried} of them are not listed here or cannot be read now; they are kept as they are." : "");
        return identity ? "" : $"{what} selected.";
    }

    /// <summary>The "this also changes N other skins" text: the union of the other skins of this character that use an included
    /// texture, and a word on textures that lie outside the character.</summary>
    internal static string BuildChromaShareWarning(IReadOnlyList<ChromaTextureRowViewModel> included)
    {
        var skins = included.SelectMany(r => r.SharedWith).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int outside = included.Count(r => r.IsOutside);
        int edited = included.Count(r => r.IsEditedOutside);
        var parts = new List<string>();
        if (skins.Count > 0)
        {
            int more = skins.Count - ChromaRowViewModel.MaxNamedSharers;
            parts.Add($"This also changes {skins.Count} other skin(s) or chroma(s) of this character that use the same texture files: "
                      + string.Join(", ", skins.Take(ChromaRowViewModel.MaxNamedSharers)) + (more > 0 ? $" and {more} more" : "")
                      + ". The recolour is in place; a copy for this skin only comes in a later step.");
        }
        if (outside > 0)
            parts.Add($"{outside} included texture(s) lie outside this character's own folder: other characters, maps or global assets that use them "
                      + "change too, and they were not compared.");
        if (edited > 0)
            parts.Add($"{edited} included texture(s) are project files somebody edited outside the recolour: saving replaces that edit with the "
                      + "recolour of Riot's original.");
        return string.Join("\n", parts);
    }

    // ---- the live preview --------------------------------------------------------------------------------

    /// <summary>Recolour the included textures from their originals and draw them, soon. Safe to call on every slider tick: the
    /// requests collapse into the newest.</summary>
    internal void RequestChromaPreview()
    {
        if (_chromaRestoring) return;
        var transform = ChromaSettings.ToTransform();
        var included = IncludedChromaRows.Select(r => r.Hash).ToArray();
        RefreshChromaParamSwatches(transform);   // M825: the swatches follow the sliders at once
        var paramPreview = CurrentChromaParamPreview();
        lock (_chromaGate)
        {
            _chromaRequest = (transform, included, _chromaSavedHashes, Volatile.Read(ref _chromaGeneration), paramPreview);
            _chromaSerial++;
        }
        if (Interlocked.CompareExchange(ref _chromaRunning, 1, 0) != 0) return;   // the running loop will see the new serial
        _chromaLoop = Task.Run(RunChromaPreviewAsync);
    }

    private async Task RunChromaPreviewAsync()
    {
        while (true)
        {
            (ColorTransform Transform, ulong[] Included, HashSet<ulong> Saved, int Generation, ChromaParamPreview Params) request;
            HashSet<ulong> shown, restore;
            HashSet<SkinColorParamKey> paramShown, paramRestore;
            int serial;
            lock (_chromaGate)
            {
                request = _chromaRequest;
                serial = _chromaSerial;
                shown = _chromaShown;
                restore = _chromaRestore;
                _chromaRestore = new HashSet<ulong>();
                paramShown = _chromaParamShown;
                paramRestore = _chromaParamRestore;
                _chromaParamRestore = new HashSet<SkinColorParamKey>();
            }

            try
            {
                if (request.Generation == Volatile.Read(ref _chromaGeneration))
                {
                    var batch = RenderChromaBatch(request.Transform, request.Included, request.Saved, shown, restore, out var nowShown, _chromaCts.Token);
                    var paramBatch = RenderChromaParams(request.Transform, request.Params, paramShown, paramRestore, out var paramNowShown);   // M825: four floats each - no work
                    lock (_chromaGate)
                        if (request.Generation == Volatile.Read(ref _chromaGeneration)) { _chromaShown = nowShown; _chromaParamShown = paramNowShown; }
                    if (batch.Count > 0 || paramBatch.Count > 0)
                        await ChromaUiPost(() => { if (batch.Count > 0) PushChromaBatch(batch, request.Generation); PushChromaParamBatch(paramBatch, request.Generation); });
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { LogDx11?.Invoke("Chroma", "Preview failed: " + ex.Message); }

            lock (_chromaGate)
            {
                if (serial == _chromaSerial) { Volatile.Write(ref _chromaRunning, 0); return; }
            }
        }
    }

    /// <summary>The included textures recoloured, plus the ones that just left the set (or are being reverted) drawn as they were.
    /// Textures whose original is not decoded yet are left out - the decode requests another preview when it lands.</summary>
    private List<ChromaPushItem> RenderChromaBatch(ColorTransform transform, ulong[] included, HashSet<ulong> saved, HashSet<ulong> shown,
        HashSet<ulong> restore, out HashSet<ulong> nowShown, CancellationToken cancellationToken)
    {
        var batch = new List<ChromaPushItem>();
        var inSet = included.ToHashSet();
        foreach (ulong hash in included)
        {
            // Under no change a texture is drawn only to take a recolour OFF the screen: one this card showed, one being reverted, one the
            // project's saved recolour is showing. Otherwise the renderer already holds what the project serves - and drawing Riot's
            // original over a texture somebody edited in the project would show a picture the project does not have.
            if (transform.IsIdentity && !shown.Contains(hash) && !restore.Contains(hash) && !saved.Contains(hash)) continue;
            Render(hash, transform);
        }
        foreach (ulong hash in shown.Concat(restore).Distinct())
            if (!inSet.Contains(hash)) Render(hash, ColorTransform.Identity);

        // what is on screen now as a recolour: the included ones, under a transform that changes something
        nowShown = new HashSet<ulong>();
        if (!transform.IsIdentity)
            foreach (ulong hash in included) if (_chromaBuffers.Has(hash)) nowShown.Add(hash);
        return batch;

        void Render(ulong hash, ColorTransform t)
        {
            if (_chromaBuffers.Render(hash, t, cancellationToken) is not { } rgba || !_chromaBuffers.TrySize(hash, out int w, out int h)) return;
            batch.Add(new ChromaPushItem(hash, rgba, w, h));
        }
    }

    private void PushChromaBatch(List<ChromaPushItem> batch, int generation)
    {
        if (generation != Volatile.Read(ref _chromaGeneration)) return;
        if (UseDx11Preview && PushChromaDx11 is { } dx && Dx11Scene is { } scene)
        {
            var items = new List<(string Key, byte[] Rgba, int Width, int Height)>();
            var keys = Dx11KeysByHash(scene);
            foreach (var item in batch)
            {
                if (!keys.TryGetValue(item.Hash, out var names)) continue;
                foreach (var key in names)
                {
                    // the pool texture is replaced in place at its own size: a different size is a different file
                    if (scene.Textures.TryGetValue(key, out var pooled) && (pooled.Width != item.Width || pooled.Height != item.Height)) continue;
                    items.Add((key, item.Rgba, item.Width, item.Height));
                }
            }
            if (items.Count > 0) dx(items);
        }
        if (!UseDx11Preview || Dx11Scene is null) PushChromaGl?.Invoke(batch);
    }

    private (Services.PreparedCharacterScene Scene, Dictionary<ulong, List<string>> Keys)? _dx11KeyMap;

    /// <summary>The pool keys (the lower-cased texture path the scene was built with) that draw each chunk. Hashed the way the scene's
    /// own loader hashes them, so an unnamed <c>0x...</c> link finds its texture too.</summary>
    private Dictionary<ulong, List<string>> Dx11KeysByHash(Services.PreparedCharacterScene scene)
    {
        if (_dx11KeyMap is { } cached && ReferenceEquals(cached.Scene, scene)) return cached.Keys;
        var map = new Dictionary<ulong, List<string>>();
        foreach (var key in scene.Textures.Keys)
        {
            ulong hash = BinTexturePath.HashOfReference(key);
            if (!map.TryGetValue(hash, out var list)) map[hash] = list = new List<string>();
            list.Add(key);
        }
        _dx11KeyMap = (scene, map);
        return map;
    }

    /// <summary>The renderer that shows the character changed (a new D3D11 scene was committed, the GL texture list was replaced, or
    /// the GL/D3D11 switch moved): what is on screen is the project's files again, so a recolour that is not saved yet is drawn
    /// again.</summary>
    internal void ReapplyChromaPreview()
    {
        if (!ChromaRecolourDirty) return;
        lock (_chromaGate) { _chromaShown = new HashSet<ulong>(); _chromaParamShown = new HashSet<SkinColorParamKey>(); }
        RequestChromaPreview();
    }

    partial void OnTexturesChanged(IReadOnlyList<TextureImage?>? value)
    {
        ChromaGlImageMap = null;
        ReapplyChromaPreview();
    }

    // ---- commands ----------------------------------------------------------------------------------------

    /// <summary>The state a save last failed on (transform and textures), or null. The auto-save skips a pending recolour in exactly that
    /// state - it would fail the same way, and each try rebuilds the mounts - until something changes; a manual save always tries.</summary>
    private ChromaState? _chromaFailedState;

    /// <summary>A recolour is pending and the auto-save has not already failed on exactly this state.</summary>
    public bool ChromaAutoSaveDue
    {
        get
        {
            if (!HasPendingChromaRecolour) return false;
            if (_chromaFailedState is not { } failed) return true;
            return !ChromaStateNow().SameAs(failed);
        }
    }

    private bool CanApplyChroma() => ChromaRecolourDirty && !ChromaSaving && _chromaSkinBin is not null && (SaveChromaRecolour is not null || SaveChromaParameters is not null);

    [RelayCommand(CanExecute = nameof(CanApplyChroma))]
    private async Task ApplyChromaRecolour()
    {
        try { await SaveChromaRecolourNowAsync(); }
        catch (Exception ex) { ChromaRecolourStatus = "The recolour was not saved: " + ex.Message; }
    }

    /// <summary>Save what is pending through the host: the Apply button, Ctrl+S, the autosave and an export all come here. Throws
    /// when the save failed, so the caller that flushes keeps the edit pending. The textures are saved first, then (M825) the colour
    /// parameters; each part only when it differs from what the project holds, so toggling a parameter does not re-encode a texture.</summary>
    public async Task SaveChromaRecolourNowAsync()
    {
        if (!ChromaRecolourDirty || _chromaSkinBin is not { } skinBin) return;
        if (SaveChromaRecolour is null && SaveChromaParameters is null) return;
        if (ChromaSaveBlocker?.Invoke() is { } blocked) throw new InvalidOperationException(blocked);

        var state = ChromaStateNow();
        bool texturesDirty = state.TexturesDiffer(_chromaSavedTransform, _chromaSavedHashes) && SaveChromaRecolour is not null;
        bool paramsDirty = state.ParamsDiffer(_chromaSavedParamTransform, _chromaSavedParamKeys) && SaveChromaParameters is not null;
        if (!texturesDirty && !paramsDirty) return;

        ChromaSaving = true;
        var notes = new List<string>();
        try
        {
            try
            {
                if (texturesDirty) notes.Add(await SaveChromaTexturesAsync(skinBin, state, SaveChromaRecolour!));
                if (paramsDirty) notes.Add(await SaveChromaParametersPartAsync(skinBin, state, SaveChromaParameters!));
            }
            catch
            {
                _chromaFailedState = state;   // the auto-save does not try this same state again every tick
                throw;
            }
            UpdateChromaDirty();
            ChromaRecolourStatus = "Saved: " + string.Join(" ", notes.Where(n => n.Length > 0));
        }
        finally { ChromaSaving = false; }
    }

    /// <summary>The textures half of a save (M824, unchanged in what it does). Returns the line the status shows.</summary>
    private async Task<string> SaveChromaTexturesAsync(string skinBin, ChromaState state,
        Func<string, ColorTransform, IReadOnlyList<ChromaTarget>, IReadOnlyList<ChromaTarget>, Task<ChromaSaveResult>> save)
    {
        var transform = state.Transform;
        var hashes = state.Hashes;
        var targets = IncludedChromaRows.Where(r => hashes.Contains(r.Hash)).Select(r => r.Target).ToList();
        // the saved textures this card has no switch for stay as they are (see CarriedChromaTargets)
        var carried = hashes.Count == 0 ? new List<ChromaTarget>() : CarriedChromaTargets();
        // the earlier recipe's textures that this one no longer covers go back to Riot's
        var stale = _chromaSavedTargets.Where(t => !hashes.Contains(t.Hash)).ToList();

        var result = await save(skinBin, transform, targets, stale);
        if (result.Failed > 0 && result.Written == 0 && targets.Count > 0)
            throw new InvalidOperationException(result.Summary + (result.Notes.Count > 0 ? ": " + result.Notes[0] : ""));
        _chromaFailedState = result.Failed > 0 ? state : null;   // a save that left a texture unwritten is not retried by every tick either

        // the settled textures are the saved recipe: a written one carries a record, an unchanged one needs none
        var settled = new HashSet<ulong>(result.Settled);
        var written = new HashSet<ulong>(result.WrittenHashes);
        foreach (var keep in carried) settled.Add(keep.Hash);
        _chromaSavedTransform = settled.Count == 0 ? ColorTransform.Identity : transform;
        _chromaSavedTargets = targets.Where(t => written.Contains(t.Hash)).Concat(carried).ToList();
        _chromaSavedHashes = settled;
        HasSavedChromaRecolour = _chromaSavedTargets.Count > 0 || _chromaSavedParamRefs.Count > 0;
        foreach (var row in _chromaRows)
            row.Note = written.Contains(row.Hash) ? "recoloured in the project" : (row.CanInclude ? "" : row.Note);
        return result.Summary + "." + (result.Notes.Count > 0 ? " " + result.Notes[0] : "");
    }

    /// <summary>The colour parameters half of a save (M825): the included parameters re-derived from Riot's value and written into the skin
    /// bin, the earlier recipe's parameters that are no longer part of it put back to Riot's.</summary>
    private async Task<string> SaveChromaParametersPartAsync(string skinBin, ChromaState state,
        Func<string, ColorTransform, IReadOnlyList<ChromaParameterRef>, IReadOnlyList<ChromaParameterRef>, Task<ChromaParamSaveResult>> save)
    {
        var transform = state.ParamTransform;
        var keys = state.Params;
        var targets = IncludedChromaParamRows.Where(r => keys.Contains(r.Key)).Select(r => r.ToRef()).ToList();
        // the saved parameters this card has no switch for stay as they are (see CarriedChromaParams)
        var carried = keys.Count == 0 ? new List<ChromaParameterRef>() : CarriedChromaParams();
        // the earlier recipe's parameters that this one no longer covers go back to Riot's
        var stale = _chromaSavedParamRefs.Where(p => !keys.Contains(KeyOf(p))).ToList();

        var result = await save(skinBin, transform, targets, stale);

        var settled = new HashSet<SkinColorParamKey>(result.Settled);
        foreach (var keep in carried) settled.Add(KeyOf(keep));
        bool complete = targets.All(t => settled.Contains(KeyOf(t)));
        if (!complete) _chromaFailedState = state;   // a parameter the bin could not take is not retried by every tick
        _chromaSavedParamTransform = settled.Count == 0 ? ColorTransform.Identity : transform;
        _chromaSavedParamRefs = targets.Where(t => settled.Contains(KeyOf(t))).Concat(carried).ToList();
        _chromaSavedParamKeys = settled;
        HasSavedChromaRecolour = _chromaSavedTargets.Count > 0 || _chromaSavedParamRefs.Count > 0;
        foreach (var row in _chromaParamRows)
        {
            bool owned = _chromaSavedParamKeys.Contains(row.Key);
            bool handled = owned && !row.KeepsEdit || stale.Any(p => KeyOf(p) == row.Key);   // written, or given back to Riot's; a kept edit stays as it is
            if (!handled) continue;
            row.Note = owned ? "recoloured in the project" : (row.CanInclude ? "" : row.Info.Reason);
            row.IsEditedOutside = false;
            row.OwnedEditedNow = false;
            // what the project holds now: the recoloured value of an owned parameter, Riot's value of one the recipe let go
            if (owned) { var made = transform.Apply(row.Info.Riot); row.CurrentValue = new System.Numerics.Vector4(made.X, made.Y, made.Z, row.CurrentValue.W); }
            else if (stale.Any(p => KeyOf(p) == row.Key)) row.CurrentValue = new System.Numerics.Vector4(row.Info.Riot.X, row.Info.Riot.Y, row.Info.Riot.Z, row.CurrentValue.W);
        }
        if (!complete && result.Notes.Count == 0) throw new InvalidOperationException(result.Summary);
        return result.Summary + "." + (result.Notes.Count > 0 ? " " + result.Notes[0] : "");
    }

    private bool CanRevertChroma() => HasSavedChromaRecolour && !ChromaSaving && (RevertChromaRecolour is not null || RevertChromaParameters is not null);

    /// <summary>Put every texture and colour parameter of this skin's saved recolour back to Riot's, forget the record and reset the sliders. The two
    /// halves are separate: a half that cannot be put back (the bin is refused, say) keeps its saved state and is reported, while the other is done.</summary>
    [RelayCommand(CanExecute = nameof(CanRevertChroma))]
    private async Task RevertChromaRecolourAsync()
    {
        if (_chromaSkinBin is not { } skinBin) return;
        var targets = _chromaSavedTargets.ToList();
        var paramRefs = _chromaSavedParamRefs.ToList();
        ChromaSaving = true;
        try
        {
            int restored = 0, restoredParams = 0;
            string? paramFailure = null;
            if (targets.Count > 0 && RevertChromaRecolour is { } revert)
                restored = revert(skinBin, targets);   // synchronous and touches the project: on this thread
            if (paramRefs.Count > 0 && RevertChromaParameters is { } revertParams)
            {
                try { restoredParams = await revertParams(skinBin, paramRefs); }
                catch (Exception ex) { paramFailure = ex.Message; }
            }
            bool paramsDone = paramFailure is null;

            // the renderers still show the recolour: draw the originals, then forget the state
            await EnsureChromaOriginalsAsync(targets);
            _chromaSavedTransform = ColorTransform.Identity;
            _chromaSavedTargets = Array.Empty<ChromaTarget>();
            _chromaSavedHashes = new HashSet<ulong>();
            foreach (var row in _chromaRows) if (row.CanInclude) row.Note = "";
            if (paramsDone)
            {
                _chromaSavedParamTransform = ColorTransform.Identity;
                _chromaSavedParamRefs = Array.Empty<ChromaParameterRef>();
                _chromaSavedParamKeys = new HashSet<SkinColorParamKey>();
                foreach (var row in _chromaParamRows)
                {
                    if (paramRefs.Any(p => KeyOf(p) == row.Key)) row.CurrentValue = new System.Numerics.Vector4(row.Info.Riot.X, row.Info.Riot.Y, row.Info.Riot.Z, row.CurrentValue.W);
                    row.Note = row.CanInclude ? "" : row.Info.Reason;
                }
            }
            HasSavedChromaRecolour = _chromaSavedTargets.Count > 0 || _chromaSavedParamRefs.Count > 0;
            if (paramsDone) ChromaSettings = ChromaRecolourSettings.Default;
            lock (_chromaGate)
            {
                _chromaRestore = new HashSet<ulong>(_chromaRestore.Concat(targets.Select(t => t.Hash)));
                if (paramsDone) _chromaParamRestore = new HashSet<SkinColorParamKey>(_chromaParamRestore.Concat(paramRefs.Select(KeyOf)));
            }
            UpdateChromaDirty();
            RequestChromaPreview();
            ChromaRecolourStatus = paramsDone
                ? $"Reverted: {restored} texture(s)" + (paramRefs.Count > 0 ? $" and {restoredParams} colour parameter(s)" : "") + " put back to the original."
                : $"Reverted {restored} texture(s), but the colour parameters were not put back: {paramFailure}";
        }
        finally { ChromaSaving = false; }
    }

    /// <summary>Make sure the originals of these textures are decoded (a reverted recolour of a skin that was never scanned has none).</summary>
    private async Task EnsureChromaOriginalsAsync(IReadOnlyList<ChromaTarget> targets)
    {
        var read = ReadChromaOriginal;
        if (read is null) return;
        var missing = targets.Where(t => !_chromaBuffers.Has(t.Hash)).ToList();
        if (missing.Count == 0) return;
        using var readers = AcquireChromaReaders?.Invoke();
        var decoded = await Task.Run(() =>
        {
            var list = new List<(ulong, TextureImage)>();
            foreach (var t in missing)
            {
                try { if (read(t) is { } bytes && TextureRecolor.Classify(bytes).Ok) list.Add((t.Hash, TextureDecoder.Decode(bytes))); }
                catch { /* a texture that cannot be read keeps what it shows until the scene is reloaded */ }
            }
            return list;
        });
        foreach (var (hash, image) in decoded) _chromaBuffers.Set(hash, image);
    }

    /// <summary>Sliders back to "no change". The renderers draw the originals; nothing is written until Apply &amp; Save, which then
    /// puts a saved recolour back to Riot's.</summary>
    [RelayCommand]
    private void ResetChromaSliders()
    {
        ChromaSettings = ChromaRecolourSettings.Default;   // silent: one change pass below, not twelve
        UpdateChromaDirty();
        RequestChromaPreview();
    }
}
