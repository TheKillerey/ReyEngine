using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.App.ViewModels;

public sealed partial class MapGeoInspectorViewModel : ViewModelBase
{
    [ObservableProperty] private bool _hasMap;
    [ObservableProperty] private string _warnings = "";
    [ObservableProperty] private bool _hasWarnings;

    /// <summary>M351f: the map facts as labelled rows instead of a preformatted monospace blob —
    /// same shape as <see cref="MeshInspectorViewModel.Rows"/>.</summary>
    public ObservableCollection<StatRow> Rows { get; } = new();

    public void Show(MapGeoAsset map, string sourcePath)
    {
        Rows.Clear();
        Rows.Add(new StatRow("Source", sourcePath));
        Rows.Add(new StatRow("Version", $"{map.Version}"));
        Rows.Add(new StatRow("Meshes", $"{map.MeshCount:n0}"));
        Rows.Add(new StatRow("Vertices", $"{map.VertexCount:n0}"));
        Rows.Add(new StatRow("Indices", $"{map.IndexCount:n0}"));
        Rows.Add(new StatRow("Triangles", $"{map.TriangleCount:n0}"));
        Rows.Add(new StatRow("Materials", $"{map.MaterialCount}"));
        Rows.Add(new StatRow("Bounds", $"{map.Size.X:0} × {map.Size.Y:0} × {map.Size.Z:0}"));

        HasWarnings = map.Warnings.Count > 0;
        Warnings = HasWarnings ? $"{map.Warnings.Count} decode warning(s): {map.Warnings[0]}" : "";
        HasMap = true;
    }

    public void Clear()
    {
        HasMap = false;
        Rows.Clear();
        Warnings = "";
        HasWarnings = false;
    }
}
