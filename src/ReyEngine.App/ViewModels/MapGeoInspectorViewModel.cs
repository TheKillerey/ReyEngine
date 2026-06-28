using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.App.ViewModels;

public sealed partial class MapGeoInspectorViewModel : ViewModelBase
{
    [ObservableProperty] private bool _hasMap;
    [ObservableProperty] private string _stats = "";
    [ObservableProperty] private string _warnings = "";
    [ObservableProperty] private bool _hasWarnings;

    public void Show(MapGeoAsset map, string sourcePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Source      {sourcePath}");
        sb.AppendLine($"Version     {map.Version}");
        sb.AppendLine($"Meshes      {map.MeshCount:n0}");
        sb.AppendLine($"Vertices    {map.VertexCount:n0}");
        sb.AppendLine($"Indices     {map.IndexCount:n0}");
        sb.AppendLine($"Triangles   {map.TriangleCount:n0}");
        sb.AppendLine($"Materials   {map.MaterialCount}");
        sb.AppendLine($"Bounds size {map.Size.X:0} × {map.Size.Y:0} × {map.Size.Z:0}");
        Stats = sb.ToString();

        HasWarnings = map.Warnings.Count > 0;
        Warnings = HasWarnings ? $"{map.Warnings.Count} decode warning(s): {map.Warnings[0]}" : "";
        HasMap = true;
    }

    public void Clear()
    {
        HasMap = false;
        Stats = "";
        Warnings = "";
        HasWarnings = false;
    }
}
