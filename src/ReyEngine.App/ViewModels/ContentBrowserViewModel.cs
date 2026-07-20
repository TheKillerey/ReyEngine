using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Formats.Materials;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// Windows/Unreal-style content browser: a folder tree on the left, the current folder's contents
/// (subfolders + files) as tiles on the right, with a breadcrumb path. Navigates the asset tree
/// produced by the mount layer; selecting a file forwards to the host for preview/edit.
/// </summary>
public sealed partial class ContentBrowserViewModel : ViewModelBase
{
    public ObservableCollection<AssetNodeViewModel> FolderRoots { get; } = new();
    public ObservableCollection<AssetNodeViewModel> Items { get; } = new();
    public ObservableCollection<AssetNodeViewModel> Breadcrumbs { get; } = new();

    [ObservableProperty] private AssetNodeViewModel? _currentFolder;
    [ObservableProperty] private AssetNodeViewModel? _selectedFolder;
    [ObservableProperty] private AssetNodeViewModel? _selectedItem;
    [ObservableProperty] private string _locationText = "";
    [ObservableProperty] private bool _canGoUp;
    [ObservableProperty] private string _filter = "";

    // M43: type filter + grid/list view
    /// <summary>Asset-type categories for the filter dropdown; "All" shows everything.</summary>
    public IReadOnlyList<string> TypeFilters { get; } =
        new[] { "All", "Maps", "Bins", "Textures", "Meshes", "Skeletons", "Animations", "Materials", "Shaders", "Audio" };
    [ObservableProperty] private string _typeFilter = "All";
    [ObservableProperty] private bool _listView;
    /// <summary>Count of items shown in the current folder after filtering (for the header).</summary>
    [ObservableProperty] private int _itemCount;

    /// <summary>The <see cref="AssetNodeViewModel.Kind"/> tag(s) a type-filter category accepts; null = all.</summary>
    private static string[]? KindsFor(string typeFilter) => typeFilter switch
    {
        "Maps" => new[] { "MAP" },
        "Bins" => new[] { "BIN" },
        "Textures" => new[] { "IMG" },
        "Meshes" => new[] { "MSH" },
        "Skeletons" => new[] { "SKL" },
        "Animations" => new[] { "ANM" },
        "Materials" => new[] { "MAT" },
        "Shaders" => new[] { "FX" },
        "Audio" => new[] { "SND" },
        _ => null,
    };

    /// <summary>Invoked when a file (non-folder) is chosen — the host loads it.</summary>
    public Action<AssetNodeViewModel?>? FileSelected { get; set; }

    // ---- Material virtual assets (M33) ----
    /// <summary>Materials contained in the currently-opened material library (.materials.bin / skin .bin).</summary>
    public ObservableCollection<MaterialAssetViewModel> Materials { get; } = new();
    [ObservableProperty] private bool _hasMaterials;
    [ObservableProperty] private string _materialsSource = "";

    /// <summary>Host hook: extract the material virtual-assets from a material-library node.</summary>
    public Func<AssetNodeViewModel, IReadOnlyList<MaterialAssetViewModel>>? ExtractMaterials { get; set; }
    /// <summary>Host hook: open a material virtual-asset in the Material Editor.</summary>
    public Action<MaterialAssetViewModel>? MaterialSelected { get; set; }

    /// <summary>Host hook: lazily load thumbnails for the items now shown (textures + material diffuse).</summary>
    public Action<IReadOnlyList<AssetNodeViewModel>>? RequestThumbnails { get; set; }

    /// <summary>Host hook: does this folder map to a writable directory on disk? (M107)</summary>
    public Func<AssetNodeViewModel?, bool>? CanImportInto { get; set; }

    /// <summary>Host hook: the selection or current folder changed — re-query command availability (M108).</summary>
    public Action? SelectionStateChanged { get; set; }

    /// <summary>True when Import can actually put files in the folder being shown — false at the tree
    /// root and anywhere under Riot References, which are read-only.</summary>
    [ObservableProperty] private bool _canImportHere;

    [RelayCommand]
    private void OpenMaterial(MaterialAssetViewModel? material)
    {
        if (material is not null) MaterialSelected?.Invoke(material);
    }

    private void PopulateMaterials(AssetNodeViewModel node)
    {
        Materials.Clear();
        if (ExtractMaterials is { } ex && node.Entry is { } e && MaterialLibraryExtractor.IsMaterialLibrary(e.Path))
        {
            foreach (var m in ex(node)) Materials.Add(m);
            MaterialsSource = node.Name;
        }
        HasMaterials = Materials.Count > 0;
    }

    public void SetRoots(IEnumerable<AssetNodeViewModel> roots)
    {
        FolderRoots.Clear();
        foreach (var r in roots) FolderRoots.Add(r);
        NavigateTo(null); // top level shows the roots
    }

    public void Clear()
    {
        FolderRoots.Clear();
        Items.Clear();
        ClearSelection();
        Breadcrumbs.Clear();
        Materials.Clear();
        HasMaterials = false;
        MaterialsSource = "";
        CurrentFolder = null;
        LocationText = "";
        CanGoUp = false;
    }

    /// <summary>Cap on flattened search results so a broad filter over a huge tree can't stall the UI.</summary>
    private const int SearchCap = 4000;
    /// <summary>True when a broad filter matched more files than <see cref="SearchCap"/> (results truncated).</summary>
    [ObservableProperty] private bool _searchTruncated;

    public void NavigateTo(AssetNodeViewModel? folder)
    {
        CurrentFolder = folder;
        var source = folder?.Children ?? (IEnumerable<AssetNodeViewModel>)FolderRoots;
        var kinds = KindsFor(TypeFilter);
        bool searching = !string.IsNullOrWhiteSpace(Filter) || kinds is not null;

        Items.Clear();
        ClearSelection();   // M100: the old selection points at items that are no longer listed
        SearchTruncated = false;
        if (searching)
        {
            // Filter/type search recurses the whole subtree and shows matching FILES flat (search-results
            // view) — otherwise a filter only ever sees the current folder's direct children.
            foreach (var f in EnumerateFiles(source))
            {
                if (!string.IsNullOrWhiteSpace(Filter) && !f.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase)) continue;
                if (kinds is not null && !kinds.Contains(f.Kind)) continue;
                if (Items.Count >= SearchCap) { SearchTruncated = true; break; }
                Items.Add(f);
            }
        }
        else
        {
            // Unfiltered: normal folder view — subfolders + files at this level.
            foreach (var c in source) Items.Add(c);
        }
        ItemCount = Items.Count;

        Breadcrumbs.Clear();
        for (var n = folder; n is not null; n = n.Parent) Breadcrumbs.Insert(0, n);
        LocationText = folder is null ? "/" : "/" + string.Join(" / ", Breadcrumbs.Select(b => b.Name));
        CanGoUp = folder is not null;

        CanImportHere = CanImportInto?.Invoke(folder) ?? false;
        SelectionStateChanged?.Invoke();

        // Lazily load thumbnails only for what's now on screen.
        if (RequestThumbnails is { } req)
            req(Items.Where(i => i.WantsThumbnail && !i.HasThumbnail).ToList());
    }

    /// <summary>All non-folder descendants of the given nodes, depth-first.</summary>
    private static IEnumerable<AssetNodeViewModel> EnumerateFiles(IEnumerable<AssetNodeViewModel> nodes)
    {
        foreach (var n in nodes)
        {
            if (n.IsFolder)
            {
                foreach (var f in EnumerateFiles(n.Children)) yield return f;
            }
            else yield return n;
        }
    }

    // ---- M100: multi-selection ------------------------------------------
    // Single click selects (Ctrl toggles, Shift extends), double click opens. The bulk file
    // operations — import / copy / move / delete — all act on SelectedItems.

    /// <summary>Everything currently selected in the item view. Order follows <see cref="Items"/>.</summary>
    public ObservableCollection<AssetNodeViewModel> SelectedItems { get; } = new();
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private bool _hasMultiSelection;
    /// <summary>Header text for the current selection ("" when nothing is selected).</summary>
    [ObservableProperty] private string _selectionText = "";

    /// <summary>Anchor for Shift-extend — the last item picked without Shift.</summary>
    private AssetNodeViewModel? _anchor;

    private void SelectionChanged()
    {
        HasSelection = SelectedItems.Count > 0;
        HasMultiSelection = SelectedItems.Count > 1;
        SelectionText = SelectedItems.Count switch
        {
            0 => "",
            1 => SelectedItems[0].Name,
            _ => $"{SelectedItems.Count} selected",
        };
        // The focused item drives the existing single-asset commands.
        SelectedItem = SelectedItems.Count > 0 ? SelectedItems[^1] : SelectedItem;
        SelectionStateChanged?.Invoke();
    }

    public void ClearSelection()
    {
        foreach (var n in SelectedItems) n.IsSelected = false;
        SelectedItems.Clear();
        _anchor = null;
        SelectionChanged();
    }

    /// <summary>Plain click: this item becomes the whole selection.</summary>
    public void SelectOnly(AssetNodeViewModel node)
    {
        foreach (var n in SelectedItems) n.IsSelected = false;
        SelectedItems.Clear();
        node.IsSelected = true;
        SelectedItems.Add(node);
        _anchor = node;
        SelectionChanged();
    }

    /// <summary>Ctrl-click: add/remove one item without disturbing the rest.</summary>
    public void ToggleSelection(AssetNodeViewModel node)
    {
        if (node.IsSelected) { node.IsSelected = false; SelectedItems.Remove(node); }
        else { node.IsSelected = true; SelectedItems.Add(node); _anchor = node; }
        SelectionChanged();
    }

    /// <summary>Shift-click: select everything between the anchor and this item.</summary>
    public void SelectRange(AssetNodeViewModel node)
    {
        int to = Items.IndexOf(node);
        int from = _anchor is null ? to : Items.IndexOf(_anchor);
        if (to < 0) return;
        if (from < 0) from = to;
        foreach (var n in SelectedItems) n.IsSelected = false;
        SelectedItems.Clear();
        for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++)
        {
            Items[i].IsSelected = true;
            SelectedItems.Add(Items[i]);
        }
        SelectionChanged();   // anchor deliberately kept, so repeated Shift-clicks re-extend from it
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var n in SelectedItems) n.IsSelected = false;
        SelectedItems.Clear();
        foreach (var i in Items) { i.IsSelected = true; SelectedItems.Add(i); }
        SelectionChanged();
    }

    /// <summary>Right-click: keep an existing multi-selection if the item is part of it, otherwise
    /// make it the selection — so the context menu always acts on what's highlighted.</summary>
    public void SelectForContextMenu(AssetNodeViewModel node)
    {
        if (!node.IsSelected) SelectOnly(node);
    }

    /// <summary>Double click / Enter — open the item (navigate into folders, load files).</summary>
    public void Activate(AssetNodeViewModel? node) => Open(node);

    [RelayCommand]
    private void Open(AssetNodeViewModel? node)
    {
        if (node is null) return;
        if (node.MaterialAsset is { } mat) { SelectedItem = node; MaterialSelected?.Invoke(mat); return; }
        if (node.IsFolder) { SelectedFolder = node; NavigateTo(node); }
        else { SelectedItem = node; PopulateMaterials(node); FileSelected?.Invoke(node); }
    }

    [RelayCommand]
    private void GoUp()
    {
        if (CurrentFolder is null) return;
        NavigateTo(CurrentFolder.Parent);
    }

    [RelayCommand]
    private void NavigateCrumb(AssetNodeViewModel? node) => NavigateTo(node);

    partial void OnSelectedFolderChanged(AssetNodeViewModel? value)
    {
        if (value is { IsFolder: true } && !ReferenceEquals(value, CurrentFolder)) NavigateTo(value);
    }

    partial void OnFilterChanged(string value) => NavigateTo(CurrentFolder);
    partial void OnTypeFilterChanged(string value) => NavigateTo(CurrentFolder);

    [RelayCommand]
    private void ToggleView() => ListView = !ListView;
}
