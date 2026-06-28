using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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

    /// <summary>Invoked when a file (non-folder) is chosen — the host loads it.</summary>
    public Action<AssetNodeViewModel?>? FileSelected { get; set; }

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
        Breadcrumbs.Clear();
        CurrentFolder = null;
        LocationText = "";
        CanGoUp = false;
    }

    public void NavigateTo(AssetNodeViewModel? folder)
    {
        CurrentFolder = folder;
        var source = folder?.Children ?? (IEnumerable<AssetNodeViewModel>)FolderRoots;

        Items.Clear();
        foreach (var c in source)
        {
            if (!string.IsNullOrWhiteSpace(Filter) && !c.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase)) continue;
            Items.Add(c);
        }

        Breadcrumbs.Clear();
        for (var n = folder; n is not null; n = n.Parent) Breadcrumbs.Insert(0, n);
        LocationText = folder is null ? "/" : "/" + string.Join(" / ", Breadcrumbs.Select(b => b.Name));
        CanGoUp = folder is not null;
    }

    [RelayCommand]
    private void Open(AssetNodeViewModel? node)
    {
        if (node is null) return;
        if (node.IsFolder) { SelectedFolder = node; NavigateTo(node); }
        else { SelectedItem = node; FileSelected?.Invoke(node); }
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
}
