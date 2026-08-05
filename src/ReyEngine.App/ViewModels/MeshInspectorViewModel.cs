using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Skeletons;

namespace ReyEngine.App.ViewModels;

/// <summary>M351f: one labelled fact in an Overview section — the row form the whole redesign uses.</summary>
public sealed record StatRow(string Label, string Value);

public sealed partial class MeshInspectorViewModel : ViewModelBase
{
    [ObservableProperty] private bool _hasMesh;
    [ObservableProperty] private string _skeletonStatus = "No skeleton";

    /// <summary>M351f: the mesh facts as labelled rows. This data was already discrete right up until
    /// ShowMesh flattened it into a StringBuilder blob — the view could only render it as preformatted
    /// monospace text, which is why Overview looked like a terminal dump inside a card.</summary>
    public ObservableCollection<StatRow> Rows { get; } = new();

    /// <summary>Submesh material assignments, one row each (label = material, value = triangle count).</summary>
    public ObservableCollection<StatRow> SubMeshRows { get; } = new();

    public void ShowMesh(MeshAsset m, SkeletonAsset? skeleton)
    {
        Rows.Clear();
        Rows.Add(new StatRow("Vertices", $"{m.VertexCount:n0}"));
        Rows.Add(new StatRow("Indices", $"{m.IndexCount:n0}"));
        Rows.Add(new StatRow("Triangles", $"{m.TriangleCount:n0}"));
        Rows.Add(new StatRow("Submeshes", $"{m.SubMeshes.Count}"));
        Rows.Add(new StatRow("Bounds", $"{m.Size.X:0.#} × {m.Size.Y:0.#} × {m.Size.Z:0.#}"));

        SubMeshRows.Clear();
        foreach (var s in m.SubMeshes)
            SubMeshRows.Add(new StatRow(s.Material, $"{s.IndexCount / 3:n0} tris"));

        SetSkeleton(skeleton);
        HasMesh = true;
    }

    public void SetSkeleton(SkeletonAsset? skeleton) =>
        SkeletonStatus = skeleton is null ? "No skeleton paired" : $"Skeleton: {skeleton.BoneCount} bones";

    public void Clear()
    {
        HasMesh = false;
        Rows.Clear();
        SubMeshRows.Clear();
        SkeletonStatus = "No skeleton";
    }
}
