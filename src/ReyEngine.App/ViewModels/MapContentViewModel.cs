using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ReyEngine.App.ViewModels;

public sealed class MapPieceViewModel
{
    public required string Name { get; init; }
    public required string Info { get; init; }
}

/// <summary>A visibility layer group (one distinct dragon bitmask) and the meshes it contains.</summary>
public sealed class MapLayerGroupViewModel
{
    public required string Name { get; init; }   // e.g. "Ocean — 137 meshes"
    public required int Bit { get; init; }        // the distinct VisibilityFlags value
    public ObservableCollection<MapPieceViewModel> Meshes { get; } = new();
}

public sealed class RecentProjectViewModel
{
    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('/', '\\'));
    public RecentProjectViewModel(string path) => Path = path;
}

/// <summary>
/// Left "Map Content" panel (Unreal World-Outliner style): the project's map geometry files, and —
/// when one is loaded — its mesh groups as a scene outline.
/// </summary>
public sealed partial class MapContentViewModel : ViewModelBase
{
    public ObservableCollection<AssetNodeViewModel> Maps { get; } = new();
    public ObservableCollection<MapPieceViewModel> Pieces { get; } = new();
    public ObservableCollection<MapLayerGroupViewModel> LayerGroups { get; } = new();

    [ObservableProperty] private string _mapName = "";
    [ObservableProperty] private bool _hasMap;
    [ObservableProperty] private string _summary = "Open a project folder to list its maps.";

    /// <summary>Invoked when the user picks a map to load.</summary>
    public Action<AssetNodeViewModel>? OpenMap { get; set; }

    public void SetMaps(IEnumerable<AssetNodeViewModel> mapNodes)
    {
        Maps.Clear();
        foreach (var n in mapNodes) Maps.Add(n);
        Summary = Maps.Count == 0 ? "No .mapgeo maps in this project." : $"{Maps.Count} map(s) in project — double-click to load.";
    }

    public void ShowMap(string name, IReadOnlyList<MapPieceViewModel> pieces)
    {
        MapName = name;
        Pieces.Clear();
        foreach (var p in pieces) Pieces.Add(p);
        HasMap = true;
    }

    public void SetLayerGroups(IEnumerable<MapLayerGroupViewModel> groups)
    {
        LayerGroups.Clear();
        foreach (var g in groups) LayerGroups.Add(g);
    }

    public void ClearMap()
    {
        MapName = "";
        Pieces.Clear();
        LayerGroups.Clear();
        HasMap = false;
    }

    public void Clear()
    {
        Maps.Clear();
        ClearMap();
        Summary = "Open a project folder to list its maps.";
    }

    [RelayCommand]
    private void Open(AssetNodeViewModel? node)
    {
        if (node is not null) OpenMap?.Invoke(node);
    }
}
