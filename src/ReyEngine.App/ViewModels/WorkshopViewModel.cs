using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.App.Services;
using ReyEngine.Core.Decoding;

namespace ReyEngine.App.ViewModels;

public sealed partial class WorkshopMaterialViewModel : ObservableObject
{
    public required WorkshopMaterialTemplate Template { get; init; }
    [ObservableProperty] private Bitmap? _thumbnail;
    public string Name => Template.Shader.Split('/').LastOrDefault() ?? Template.Shader;
    public string Category => Template.Shader.Contains('/') ? Template.Shader[..Template.Shader.LastIndexOf('/')] : "Shader";
    public string Detail => $"{Template.Profile}  |  {Template.Samplers} textures  |  {Template.Parameters} params"
        + $"  |  common setup {Template.SetupUsageCount}/{Template.ShaderUsageCount}";
    public string Source => $"Most-used setup example: {Template.MaterialName}\n{Template.SourceBinPath}";
}

public sealed partial class WorkshopParticleViewModel : ObservableObject
{
    public required WorkshopParticleTemplate Template { get; init; }
    [ObservableProperty] private Bitmap? _thumbnail;
    public string Name => Template.Name;
    public string Path => string.IsNullOrWhiteSpace(Template.ParticlePath) ? $"0x{Template.SystemHash:x8}" : Template.ParticlePath;
    public string Detail => $"{Template.Emitters} emitter(s)  |  {Template.VisualEmitters} visual";
    public bool IsVisual => Template.VisualEmitters > 0;

    /// <summary>M419. Legacy effects are flagged in the list, not only on import.</summary>
    public bool IsLegacy => Template.IsLegacy;

    /// <summary>M655: brought in by the user rather than harvested out of the installed game.</summary>
    public bool IsUser => Template.IsUser;
    public bool IsMissingFile => Template.IsUser && !Template.UserFileExists;
    public string OriginNote => !Template.IsUser
        ? ""
        : Template.UserFileExists
            ? $"Yours - {System.IO.Path.GetFileName(Template.SourceBinPath)}"
            : $"Yours - FILE IS GONE ({Template.SourceBinPath})";

    /// <summary>M425: set by the host to describe THIS conversion, since a decoded file keeps its real
    /// rates and lifetimes while an undecodable one falls back to defaults. The old wording asserted
    /// "engine defaults" unconditionally, which understated every decoded effect.</summary>
    public static string LegacyPreviewStatusFor(bool decoded) => decoded
        ? "Live preview of the CONVERTED effect. Rates, lifetimes, scales, motion and every asset "
          + "binding were read from the legacy file."
        : "Live preview of the CONVERTED effect. This file's body could not be decoded, so timing and "
          + "physics are engine defaults.";

    public string LegacyTip => Template.IsDecoded
        ? "Recovered from a legacy .troybin, body decoded. Emitter names, textures, colour ramps, "
          + "rates, lifetimes, scales and motion all come from the original file."
        : "Recovered from a legacy .troybin whose body could not be decoded. Names, textures and colour "
          + "curves are faithful; rate, lifetime and scale are engine defaults — tune them in the "
          + "Particle Editor after adding.";
}

/// <summary>M655: one candidate of an in-progress import, waiting to be ticked. A champion skin bin or a
/// map's materials.bin can hold dozens of systems, and adding all of them because the user pointed at the
/// file is the same mistake Add Mesh made with a mapgeo (M654).</summary>
public sealed partial class WorkshopPendingImportViewModel : ObservableObject
{
    public required WorkshopUserEntry Entry { get; init; }
    [ObservableProperty] private bool _include = true;
    public Action? IncludeChanged;
    partial void OnIncludeChanged(bool value) => IncludeChanged?.Invoke();
    public string Name => Entry.DisplayName;
    public string Detail => $"{Entry.Emitters} emitter(s)  |  {Entry.VisualEmitters} visual"
        + (string.IsNullOrWhiteSpace(Entry.ParticlePath) ? "" : $"  |  {Entry.ParticlePath}");
}

/// <summary>Searchable, de-duplicated library of one proven game material per shader and every unique VFX
/// system. The host performs map mutation; this view model owns indexing, filtering and hero previews.</summary>
public sealed partial class WorkshopViewModel : ObservableObject
{
    private readonly WorkshopCatalogService _catalogService;
    private readonly string _finalDirectory;
    private IReadOnlyList<WorkshopMaterialViewModel> _allMaterials = Array.Empty<WorkshopMaterialViewModel>();
    private IReadOnlyList<WorkshopParticleViewModel> _allParticles = Array.Empty<WorkshopParticleViewModel>();
    /// <summary>M655: the installed game's half, kept apart so adding or deleting one of the user's own
    /// effects does not mean re-indexing 200 wads.</summary>
    private IReadOnlyList<WorkshopParticleViewModel> _catalogParticles = Array.Empty<WorkshopParticleViewModel>();
    private int _previewGeneration;

    [ObservableProperty] private IReadOnlyList<WorkshopMaterialViewModel> _materials = Array.Empty<WorkshopMaterialViewModel>();
    [ObservableProperty] private IReadOnlyList<WorkshopParticleViewModel> _particles = Array.Empty<WorkshopParticleViewModel>();

    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _visualParticlesOnly;
    [ObservableProperty] private WorkshopMaterialViewModel? _selectedMaterial;
    [ObservableProperty] private WorkshopParticleViewModel? _selectedParticle;
    [ObservableProperty] private string _newMaterialName = "Workshop_Material";
    [ObservableProperty] private string _newParticleName = "Workshop_Particle";
    [ObservableProperty] private string _status = "Opening the whole-game Workshop catalog...";
    [ObservableProperty] private bool _running;
    [ObservableProperty] private int _progressPercent;
    [ObservableProperty] private string _catalogSummary = "";

    public Func<WorkshopMaterialTemplate, string, Task<string>>? AddMaterial;
    public Func<WorkshopParticleTemplate, string, Task<string>>? AddParticle;

    /// <summary>M655: the user's own shelf. Null leaves the Workshop exactly as it was - a read-only
    /// census of the installed game.</summary>
    public WorkshopUserLibrary? UserLibrary { get; init; }
    /// <summary>Host file pickers. Multi-select for .troybin because importing a folder's worth one at a
    /// time is the thing that makes people not bother.</summary>
    public Func<Task<IReadOnlyList<string>>>? PickTroyBins;
    public Func<Task<string?>>? PickBin;

    public bool CanImport => UserLibrary is not null;
    public ObservableCollection<WorkshopPendingImportViewModel> PendingImports { get; } = new();
    [ObservableProperty] private bool _isChoosingImports;
    [ObservableProperty] private string _pendingSource = "";
    public int PendingIncluded => PendingImports.Count(x => x.Include);
    public string PendingSummary => PendingImports.Count == 0
        ? ""
        : $"{PendingIncluded:n0} of {PendingImports.Count:n0} ticked in {PendingSource}";

    /// <summary>M420: host-supplied builder for the animated preview. The host owns asset resolution,
    /// so the window itself never touches WADs.</summary>
    public Func<WorkshopParticleTemplate, VfxPlayback?>? BuildParticlePreview;

    /// <summary>Non-null while a template is playable; the hero panel falls back to the thumbnail when
    /// it is null.</summary>
    [ObservableProperty] private VfxPlayback? _playback;
    [ObservableProperty] private string _previewStatus = "";
    [ObservableProperty] private bool _previewPaused;
    [ObservableProperty] private float _previewSpeed = 1f;

    public bool HasLivePreview => Playback is not null;
    partial void OnPlaybackChanged(VfxPlayback? value) => OnPropertyChanged(nameof(HasLivePreview));

    [RelayCommand] private void TogglePreviewPause() => PreviewPaused = !PreviewPaused;

    [RelayCommand]
    private void RestartPreview()
    {
        if (SelectedParticle is { } item) _ = LoadLivePreviewAsync(item, _previewGeneration);
    }

    public bool IsMaterialsTab => SelectedTab == 0;
    public bool IsParticlesTab => SelectedTab == 1;
    public bool CanAddMaterial => !Running && SelectedMaterial is not null && !string.IsNullOrWhiteSpace(NewMaterialName);
    public bool CanAddParticle => !Running && SelectedParticle is not null && !string.IsNullOrWhiteSpace(NewParticleName);

    public WorkshopViewModel(WorkshopCatalogService catalogService, string finalDirectory)
    { _catalogService = catalogService; _finalDirectory = finalDirectory; }

    public Task InitializeAsync() => LoadAsync(false);

    [RelayCommand] private Task RebuildCatalog() => LoadAsync(true);
    [RelayCommand] private void ShowMaterials() => SelectedTab = 0;
    [RelayCommand] private void ShowParticles() => SelectedTab = 1;

    private async Task LoadAsync(bool rebuild)
    {
        if (Running) return;
        Running = true;
        ProgressPercent = 0;
        try
        {
            Status = rebuild ? "Rebuilding the Workshop from every installed game WAD..." : "Loading the Workshop catalog...";
            var progress = new Progress<WorkshopCatalogProgress>(p =>
            {
                ProgressPercent = p.Percent;
                Status = $"{p.CompletedWads:n0}/{p.TotalWads:n0} WADs  |  {p.Materials:n0} shaders  |  {p.Particles:n0} particles  |  {p.Current}";
            });
            // Fingerprinting and deserializing the large all-particle cache are deliberately off the UI
            // thread. The installed corpus is large enough that even a cache hit would otherwise freeze
            // the Workshop while its JSON is read.
            var catalog = await Task.Run(() => _catalogService.LoadAsync(_finalDirectory, rebuild, progress));
            (_allMaterials, _catalogParticles) = await Task.Run(() =>
                ((IReadOnlyList<WorkshopMaterialViewModel>)catalog.Materials
                    .Select(x => new WorkshopMaterialViewModel { Template = x }).ToArray(),
                 (IReadOnlyList<WorkshopParticleViewModel>)catalog.Particles
                    .Select(x => new WorkshopParticleViewModel { Template = x }).ToArray()));
            RebuildParticleList();
            CatalogSummary = $"{_allMaterials.Count:n0} unique shaders  |  {_catalogParticles.Count:n0} unique particles"
                + (UserLibrary is { Entries.Count: > 0 } lib ? $"  |  {lib.Entries.Count:n0} of yours" : "")
                + $"  |  built {catalog.BuiltUtc.ToLocalTime():g}";
            Status = rebuild ? "Catalog rebuilt from the installed patch." : "Workshop ready.";
        }
        catch (Exception ex) { Status = "Workshop unavailable: " + ex.Message; }
        finally { Running = false; RaiseCanAdd(); }
    }

    /// <summary>The user's own effects sit FIRST: a shelf you can only reach by scrolling past 40,000
    /// shipped systems is not a shelf.</summary>
    private void RebuildParticleList()
    {
        var mine = UserLibrary is null
            ? Array.Empty<WorkshopParticleViewModel>()
            : UserLibrary.ToTemplates().Select(t => new WorkshopParticleViewModel { Template = t }).ToArray();
        _allParticles = mine.Concat(_catalogParticles).ToArray();
        ApplyFilter();
        OnPropertyChanged(nameof(CanDeleteParticle));
    }

    [RelayCommand]
    private async Task ImportTroyBins()
    {
        if (UserLibrary is null || PickTroyBins is null) return;
        var paths = await PickTroyBins();
        if (paths.Count == 0) return;
        var found = UserLibrary.ScanTroyBins(paths, out var failures);
        if (found.Count == 0)
        {
            Status = failures.Count > 0
                ? "Nothing imported: " + string.Join("; ", failures.Take(3))
                : "Nothing imported - those files hold no readable effect.";
            return;
        }
        // A .troybin IS one effect, so there is nothing to choose: add them and say what happened.
        var (added, replaced) = UserLibrary.Add(found);
        RebuildParticleList();
        SelectedTab = 1;
        SelectedParticle = Particles.FirstOrDefault(x => x.Template.UserEntryId == found[0].Id) ?? SelectedParticle;
        Status = $"Imported {added:n0} .troybin effect(s)"
            + (replaced > 0 ? $", refreshed {replaced:n0} already on the shelf" : "")
            + (failures.Count > 0 ? $". {failures.Count:n0} could not be read: {string.Join("; ", failures.Take(2))}" : ".");
    }

    [RelayCommand]
    private async Task ImportBin()
    {
        if (UserLibrary is null || PickBin is null) return;
        var path = await PickBin();
        if (path is null) return;
        var found = UserLibrary.ScanBin(path, out var failure);
        if (found.Count == 0)
        {
            Status = $"{System.IO.Path.GetFileName(path)}: {failure ?? "no VFX systems in this .bin."}";
            return;
        }
        PendingImports.Clear();
        foreach (var entry in found)
            PendingImports.Add(new WorkshopPendingImportViewModel
            { Entry = entry, IncludeChanged = () => OnPropertyChanged(nameof(PendingSummary)) });
        PendingSource = System.IO.Path.GetFileName(path);
        IsChoosingImports = true;
        RaisePending();
        Status = $"{PendingSource} holds {found.Count:n0} effect(s) - pick the ones you want.";
    }

    [RelayCommand] private void SelectAllPending() => SetPending(true);
    [RelayCommand] private void SelectNoPending() => SetPending(false);

    private void SetPending(bool include)
    {
        foreach (var row in PendingImports) row.Include = include;
        RaisePending();
    }

    [RelayCommand]
    private void ConfirmPendingImport()
    {
        if (UserLibrary is null) return;
        var chosen = PendingImports.Where(x => x.Include).Select(x => x.Entry).ToArray();
        if (chosen.Length == 0) { Status = "Nothing ticked, so nothing was imported."; return; }
        var (added, replaced) = UserLibrary.Add(chosen);
        IsChoosingImports = false;
        PendingImports.Clear();
        RebuildParticleList();
        SelectedTab = 1;
        SelectedParticle = Particles.FirstOrDefault(x => x.Template.UserEntryId == chosen[0].Id) ?? SelectedParticle;
        Status = $"Imported {added:n0} effect(s) from {PendingSource}"
            + (replaced > 0 ? $", refreshed {replaced:n0} already on the shelf." : ".");
    }

    [RelayCommand]
    private void CancelPendingImport()
    {
        IsChoosingImports = false;
        PendingImports.Clear();
        Status = "Import cancelled.";
    }

    /// <summary>Only the user's own rows can go. A shipped effect is a fact about the installed game and
    /// removing it from the list would only make the Workshop lie about what is there.</summary>
    public bool CanDeleteParticle => UserLibrary is not null && SelectedParticle is { IsUser: true };

    [RelayCommand]
    private void DeleteSelectedParticle()
    {
        if (UserLibrary is null || SelectedParticle is not { Template.UserEntryId: { } id } victim) return;
        string name = victim.Name;
        if (!UserLibrary.Remove(id)) { Status = $"'{name}' was already gone from the shelf."; return; }
        RebuildParticleList();
        Status = $"Removed '{name}' from your Workshop shelf. The file itself was not touched.";
    }

    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnVisualParticlesOnlyChanged(bool value) => ApplyFilter();
    partial void OnSelectedTabChanged(int value)
    {
        OnPropertyChanged(nameof(IsMaterialsTab));
        OnPropertyChanged(nameof(IsParticlesTab));
    }

    private void ApplyFilter()
    {
        string search = Search.Trim();
        Materials = _allMaterials.Where(m => search.Length == 0
                    || m.Template.Shader.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || m.Template.MaterialName.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        Particles = _allParticles.Where(p => (!VisualParticlesOnly || p.IsVisual)
                    && (search.Length == 0 || p.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || p.Path.Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (SelectedMaterial is null || !Materials.Contains(SelectedMaterial)) SelectedMaterial = Materials.FirstOrDefault();
        if (SelectedParticle is null || !Particles.Contains(SelectedParticle)) SelectedParticle = Particles.FirstOrDefault();
    }

    partial void OnSelectedMaterialChanged(WorkshopMaterialViewModel? value)
    {
        if (value is not null)
        {
            NewMaterialName = UniqueName("Workshop_" + value.Name);
            _ = LoadMaterialPreviewAsync(value, ++_previewGeneration);
        }
        RaiseCanAdd();
    }

    partial void OnSelectedParticleChanged(WorkshopParticleViewModel? value)
    {
        if (value is not null)
        {
            NewParticleName = UniqueName("Workshop_" + value.Name);
            _ = LoadParticlePreviewAsync(value, ++_previewGeneration);
        }
        else
        {
            // M420: nothing selected means nothing to play - leaving the last system running would keep
            // the renderer busy and show an effect the user is no longer looking at
            _previewGeneration++;
            Playback = null;
            PreviewStatus = "";
        }
        RaiseCanAdd();
    }

    partial void OnNewMaterialNameChanged(string value) => RaiseCanAdd();
    partial void OnIsChoosingImportsChanged(bool value) => RaisePending();
    partial void OnPendingSourceChanged(string value) => RaisePending();

    private void RaisePending()
    {
        OnPropertyChanged(nameof(PendingIncluded));
        OnPropertyChanged(nameof(PendingSummary));
    }
    partial void OnNewParticleNameChanged(string value) => RaiseCanAdd();
    partial void OnRunningChanged(bool value) => RaiseCanAdd();

    private async Task LoadMaterialPreviewAsync(WorkshopMaterialViewModel item, int generation)
    {
        if (item.Thumbnail is not null) return;
        var image = await DecodeAsync(item.Template.TexturePaths.FirstOrDefault());
        if (generation != _previewGeneration && SelectedMaterial != item) return;
        item.Thumbnail = WorkshopThumbnailRenderer.Render(image, item.Template.Shader, particle: false);
    }

    private async Task LoadParticlePreviewAsync(WorkshopParticleViewModel item, int generation)
    {
        // M420: the animated preview first - it is what the user is waiting to see. The thumbnail is
        // still built because the list rows use it, and because it is the fallback when a system cannot
        // be played (no textures resolved, or an effect the renderer has no pipeline for).
        await LoadLivePreviewAsync(item, generation);
        if (item.Thumbnail is not null) return;
        var image = await DecodeAsync(item.Template.PreviewTexturePath);
        if (generation != _previewGeneration && SelectedParticle != item) return;
        item.Thumbnail = WorkshopThumbnailRenderer.Render(image, item.Path, particle: true);
    }

    /// <summary>M420: build and start the animated preview for the selected template. Built off the UI
    /// thread - a legacy template is converted and a modern one has its bin closure read and parsed,
    /// neither of which belongs on the render thread.</summary>
    private async Task LoadLivePreviewAsync(WorkshopParticleViewModel item, int generation)
    {
        if (BuildParticlePreview is null) { PreviewStatus = ""; return; }
        Playback = null;
        PreviewStatus = "Building preview…";
        var built = await Task.Run(() =>
        {
            try { return BuildParticlePreview(item.Template); }
            catch { return null; }
        });
        if (generation != _previewGeneration && SelectedParticle != item) return;

        Playback = built;
        PreviewStatus = built is null
            ? "No live preview for this system — showing its texture instead."
            : item.Template.IsLegacy
                ? WorkshopParticleViewModel.LegacyPreviewStatusFor(item.Template.IsDecoded)
                : "";
    }

    private async Task<TextureImage?> DecodeAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return await Task.Run(() =>
        {
            try { var bytes = _catalogService.ReadAsset(path); return bytes is null ? null : TextureDecoder.Decode(bytes); }
            catch { return null; }
        });
    }

    [RelayCommand]
    private async Task AddSelectedMaterial()
    {
        if (!CanAddMaterial || AddMaterial is null) return;
        Running = true;
        try { Status = await AddMaterial(SelectedMaterial!.Template, NewMaterialName.Trim()); }
        catch (Exception ex) { Status = "Material was not added: " + ex.Message; }
        finally { Running = false; }
    }

    [RelayCommand]
    private async Task AddSelectedParticle()
    {
        if (!CanAddParticle || AddParticle is null) return;
        Running = true;
        try { Status = await AddParticle(SelectedParticle!.Template, NewParticleName.Trim()); }
        catch (Exception ex) { Status = "Particle was not added: " + ex.Message; }
        finally { Running = false; }
    }

    private void RaiseCanAdd()
    {
        OnPropertyChanged(nameof(CanAddMaterial));
        OnPropertyChanged(nameof(CanAddParticle));
        OnPropertyChanged(nameof(CanDeleteParticle));
        AddSelectedMaterialCommand.NotifyCanExecuteChanged();
        AddSelectedParticleCommand.NotifyCanExecuteChanged();
    }

    private static string UniqueName(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c is '_' or '/' ? c : '_').ToArray();
        string result = new(chars);
        while (result.Contains("__", StringComparison.Ordinal)) result = result.Replace("__", "_", StringComparison.Ordinal);
        return result.Trim('_');
    }
}
