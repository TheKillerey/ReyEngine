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
}

/// <summary>Searchable, de-duplicated library of one proven game material per shader and every unique VFX
/// system. The host performs map mutation; this view model owns indexing, filtering and hero previews.</summary>
public sealed partial class WorkshopViewModel : ObservableObject
{
    private readonly WorkshopCatalogService _catalogService;
    private readonly string _finalDirectory;
    private IReadOnlyList<WorkshopMaterialViewModel> _allMaterials = Array.Empty<WorkshopMaterialViewModel>();
    private IReadOnlyList<WorkshopParticleViewModel> _allParticles = Array.Empty<WorkshopParticleViewModel>();
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
            (_allMaterials, _allParticles) = await Task.Run(() =>
                ((IReadOnlyList<WorkshopMaterialViewModel>)catalog.Materials
                    .Select(x => new WorkshopMaterialViewModel { Template = x }).ToArray(),
                 (IReadOnlyList<WorkshopParticleViewModel>)catalog.Particles
                    .Select(x => new WorkshopParticleViewModel { Template = x }).ToArray()));
            CatalogSummary = $"{_allMaterials.Count:n0} unique shaders  |  {_allParticles.Count:n0} unique particles  |  built {catalog.BuiltUtc.ToLocalTime():g}";
            Status = rebuild ? "Catalog rebuilt from the installed patch." : "Workshop ready.";
            ApplyFilter();
        }
        catch (Exception ex) { Status = "Workshop unavailable: " + ex.Message; }
        finally { Running = false; RaiseCanAdd(); }
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
        RaiseCanAdd();
    }

    partial void OnNewMaterialNameChanged(string value) => RaiseCanAdd();
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
        if (item.Thumbnail is not null) return;
        var image = await DecodeAsync(item.Template.PreviewTexturePath);
        if (generation != _previewGeneration && SelectedParticle != item) return;
        item.Thumbnail = WorkshopThumbnailRenderer.Render(image, item.Path, particle: true);
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
