using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.App.Services;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Core.Wad;

namespace ReyEngine.App.ViewModels;

/// <summary>One template card in the New Project wizard (M73).</summary>
public sealed partial class TemplateCardViewModel : ObservableObject
{
    public required ProjectTemplate Template { get; init; }
    [ObservableProperty] private bool _isSelected;
    public string Icon => Template.Icon;
    public string Name => Template.Name;
    public string Description => Template.Description;
}

/// <summary>One detected game install (Live/PBE) in the wizard (M73).</summary>
public sealed partial class InstallCardViewModel : ObservableObject
{
    public required GameInstall Install { get; init; }
    [ObservableProperty] private bool _isSelected;
    public string Platform => Install.Platform;
    public string Path => Install.GameDirectory;
    public string Icon => Install.Platform == "PBE" ? "🧪" : "🟢";
}

/// <summary>One selectable WAD row (M73).</summary>
public sealed partial class WadPickViewModel : ObservableObject
{
    public required GameWad Wad { get; init; }
    [ObservableProperty] private bool _isChecked;
    public string Name => Wad.Name;
    public string Group => Wad.Group;
    public string Size => Wad.SizeDisplay;
    public event Action? CheckedChanged;
    partial void OnIsCheckedChanged(bool value) => CheckedChanged?.Invoke();
}

/// <summary>One content-category checkbox with live count/size for the checked WADs (M73).</summary>
public sealed partial class CategoryPickViewModel : ObservableObject
{
    public required string Name { get; init; }
    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private int _count;
    [ObservableProperty] private long _sizeBytes;
    public string Stats => Count == 0 ? "—" :
        $"{Count:n0} file(s) · {(SizeBytes >= 1L << 30 ? $"{SizeBytes / (double)(1 << 30):0.00} GB" : $"{SizeBytes / (double)(1 << 20):0.0} MB")}";
    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(Stats));
    partial void OnSizeBytesChanged(long value) => OnPropertyChanged(nameof(Stats));
}

/// <summary>
/// M73: Unreal-style New Project wizard — template → platform (Live/PBE) → WAD selection →
/// content categories → name/location → create. Creating extracts the chosen categories into
/// editable unpacked folders and keeps the WADs as read-only Riot references.
/// </summary>
public sealed partial class NewProjectViewModel : ObservableObject
{
    private readonly WadPathResolver _resolver;

    public ObservableCollection<TemplateCardViewModel> Templates { get; } = new();
    public ObservableCollection<InstallCardViewModel> Installs { get; } = new();
    public ObservableCollection<WadPickViewModel> FilteredWads { get; } = new();
    public ObservableCollection<CategoryPickViewModel> Categories { get; } = new();

    private List<WadPickViewModel> _allWads = new();

    // ---- wizard state ----
    [ObservableProperty] private int _step;                    // 0 template · 1 platform · 2 wads · 3 content · 4 create
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _projectName = "MyMod";
    [ObservableProperty] private string _location;
    [ObservableProperty] private string _author;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _busyText;
    [ObservableProperty] private string? _summary;

    public bool ShowTemplate => Step == 0;
    public bool ShowPlatform => Step == 1;
    public bool ShowWads => Step == 2;
    public bool ShowContent => Step == 3;
    public bool ShowCreate => Step == 4;
    public bool CanGoBack => Step > 0 && !IsBusy;
    public bool CanGoNext => Step < 4 && !IsBusy;
    public bool IsCreateStep => Step == 4;
    public int CheckedWadCount => _allWads.Count(w => w.IsChecked);

    public bool Created { get; private set; }
    public string? CreatedRoot { get; private set; }
    public event Action? CloseRequested;

    public NewProjectViewModel(WadPathResolver resolver)
    {
        _resolver = resolver;
        _location = MainWindowViewModel.DefaultProjectsFolder;   // M133: host overrides with the configured folder
        _author = Environment.UserName;

        foreach (var t in ProjectTemplate.All)
            Templates.Add(new TemplateCardViewModel { Template = t, IsSelected = t.Id == "champion" });

        foreach (var i in GameInstallLocator.Discover())
            Installs.Add(new InstallCardViewModel { Install = i, IsSelected = Installs.Count == 0 });

        foreach (var c in AssetCategories.All)
            Categories.Add(new CategoryPickViewModel { Name = c });
    }

    public TemplateCardViewModel? SelectedTemplate => Templates.FirstOrDefault(t => t.IsSelected);
    public InstallCardViewModel? SelectedInstall => Installs.FirstOrDefault(i => i.IsSelected);

    partial void OnStepChanged(int value)
    {
        OnPropertyChanged(nameof(ShowTemplate));
        OnPropertyChanged(nameof(ShowPlatform));
        OnPropertyChanged(nameof(ShowWads));
        OnPropertyChanged(nameof(ShowContent));
        OnPropertyChanged(nameof(ShowCreate));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(IsCreateStep));
    }

    partial void OnSearchTextChanged(string value) => RefreshFilteredWads();
    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
    }

    [RelayCommand]
    private void SelectTemplate(TemplateCardViewModel? card)
    {
        if (card is null) return;
        foreach (var t in Templates) t.IsSelected = ReferenceEquals(t, card);
        // Seed the default categories for the picked template (user can still change them later).
        var defaults = new HashSet<string>(card.Template.DefaultCategories, StringComparer.OrdinalIgnoreCase);
        foreach (var c in Categories) c.IsChecked = defaults.Contains(c.Name);
        OnPropertyChanged(nameof(SelectedTemplate));
    }

    [RelayCommand]
    private void SelectInstall(InstallCardViewModel? card)
    {
        if (card is null) return;
        foreach (var i in Installs) i.IsSelected = ReferenceEquals(i, card);
        OnPropertyChanged(nameof(SelectedInstall));
        LoadWads();
    }

    /// <summary>Add a manually-browsed install (custom drive / nonstandard location).</summary>
    public void AddCustomInstall(string gameDirectory)
    {
        var install = new GameInstall(
            gameDirectory.Contains("PBE", StringComparison.OrdinalIgnoreCase) ? "PBE" : "Live", gameDirectory);
        var card = new InstallCardViewModel { Install = install };
        Installs.Add(card);
        SelectInstall(card);
    }

    private void LoadWads()
    {
        _allWads = new List<WadPickViewModel>();
        if (SelectedInstall is { } sel)
            foreach (var w in GameInstallLocator.ListWads(sel.Install.GameDirectory))
            {
                var row = new WadPickViewModel { Wad = w };
                row.CheckedChanged += () => OnPropertyChanged(nameof(CheckedWadCount));
                _allWads.Add(row);
            }
        RefreshFilteredWads();
        OnPropertyChanged(nameof(CheckedWadCount));
    }

    private void RefreshFilteredWads()
    {
        string preferred = SelectedTemplate?.Template.PreferredWadGroup ?? "";
        var query = _allWads.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(SearchText))
            query = query.Where(w => w.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                                  || w.Group.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        // Preferred group (from the template) floats to the top; checked rows stay visible regardless.
        var ordered = query
            .OrderByDescending(w => w.IsChecked)
            .ThenByDescending(w => preferred.Length > 0 && w.Group.StartsWith(preferred, StringComparison.OrdinalIgnoreCase))
            .ThenBy(w => w.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
            .Take(400);   // keep the list snappy; search narrows it
        FilteredWads.Clear();
        foreach (var w in ordered) FilteredWads.Add(w);
    }

    [RelayCommand]
    private void Back() { if (Step > 0) Step--; }

    [RelayCommand]
    private async Task Next()
    {
        if (Step == 1 && SelectedInstall is not null && _allWads.Count == 0) LoadWads();
        if (Step == 2) await ComputeCategoryStatsAsync();   // entering the content step: live counts
        if (Step == 3) BuildSummary();
        if (Step < 4) Step++;
    }

    /// <summary>Aggregate per-category chunk counts + uncompressed sizes across every checked WAD.</summary>
    private async Task ComputeCategoryStatsAsync()
    {
        var checkedWads = _allWads.Where(w => w.IsChecked).Select(w => w.Wad.Path).ToList();
        foreach (var c in Categories) { c.Count = 0; c.SizeBytes = 0; }
        if (checkedWads.Count == 0) return;

        IsBusy = true; BusyText = "Scanning selected WADs…";
        try
        {
            var totals = await Task.Run(() =>
            {
                var map = new Dictionary<string, (int Count, long Size)>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in checkedWads)
                {
                    try
                    {
                        using var wad = WadArchive.Open(path, _resolver);
                        foreach (var e in wad.Entries)
                        {
                            string cat = e.IsResolved ? AssetCategories.Classify(e.Path) : AssetCategories.Unresolved;
                            map[cat] = map.TryGetValue(cat, out var t)
                                ? (t.Count + 1, t.Size + e.UncompressedSize)
                                : (1, e.UncompressedSize);
                        }
                    }
                    catch { /* unreadable wad — skip its stats */ }
                }
                return map;
            });
            foreach (var c in Categories)
                if (totals.TryGetValue(c.Name, out var t)) { c.Count = t.Count; c.SizeBytes = t.Size; }
        }
        finally { IsBusy = false; BusyText = null; }
    }

    private void BuildSummary()
    {
        var wads = _allWads.Where(w => w.IsChecked).ToList();
        var cats = Categories.Where(c => c.IsChecked).ToList();
        long size = cats.Sum(c => c.SizeBytes);
        Summary = $"{SelectedTemplate?.Name ?? "Project"} · {SelectedInstall?.Platform ?? "?"} client\n" +
                  $"{wads.Count} WAD(s): {string.Join(", ", wads.Take(6).Select(w => w.Name))}{(wads.Count > 6 ? ", …" : "")}\n" +
                  (cats.Count == 0
                      ? "No content extracted — WADs are mounted as read-only references only."
                      : $"Extract {cats.Count} categorie(s) (~{size / (double)(1 << 20):0.0} MB): {string.Join(", ", cats.Select(c => c.Name))}");
    }

    public bool CanCreate => !IsBusy && !string.IsNullOrWhiteSpace(ProjectName) && !string.IsNullOrWhiteSpace(Location);
    partial void OnProjectNameChanged(string value) => OnPropertyChanged(nameof(CanCreate));
    partial void OnLocationChanged(string value) => OnPropertyChanged(nameof(CanCreate));

    [RelayCommand]
    private async Task Create()
    {
        if (!CanCreate) return;
        var categories = Categories.Where(c => c.IsChecked).Select(c => c.Name).ToArray();
        var spec = new ProjectCreationSpec(
            ProjectName.Trim(),
            Location.Trim(),
            string.IsNullOrWhiteSpace(Author) ? null : Author.Trim(),
            SelectedInstall?.Install.GameDirectory ?? "",
            _allWads.Where(w => w.IsChecked).Select(w => new WadSelection(w.Wad.Path, categories)).ToList());

        IsBusy = true; BusyText = "Creating project…";
        var progress = new Progress<string>(s => BusyText = s);
        try
        {
            var result = await Task.Run(() => ProjectCreator.Create(spec, _resolver, progress));
            CreatedRoot = result.RootPath;
            Created = true;
            BusyText = $"Done — {result.ExtractedFiles:n0} file(s) extracted" +
                       (result.FailedChunks > 0 ? $", {result.FailedChunks} chunk(s) skipped (subchunked/corrupt)" : "");
            CloseRequested?.Invoke();
        }
        catch (Exception ex)
        {
            BusyText = "Failed: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Cancel() { Created = false; CloseRequested?.Invoke(); }
}
