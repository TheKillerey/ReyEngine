using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Skeletons;

namespace ReyEngine.App.ViewModels;

public sealed partial class MeshInspectorViewModel : ViewModelBase
{
    [ObservableProperty] private bool _hasMesh;
    [ObservableProperty] private string _stats = "";
    [ObservableProperty] private string _skeletonStatus = "No skeleton";
    [ObservableProperty] private string _subMeshes = "";

    public void ShowMesh(MeshAsset m, SkeletonAsset? skeleton)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Vertices    {m.VertexCount:n0}");
        sb.AppendLine($"Indices     {m.IndexCount:n0}");
        sb.AppendLine($"Triangles   {m.TriangleCount:n0}");
        sb.AppendLine($"Submeshes   {m.SubMeshes.Count}");
        sb.AppendLine($"Bounds size {m.Size.X:0.#} × {m.Size.Y:0.#} × {m.Size.Z:0.#}");
        Stats = sb.ToString();

        var msb = new StringBuilder();
        foreach (var s in m.SubMeshes)
            msb.AppendLine($"• {s.Material}   ({s.IndexCount / 3:n0} tris)");
        SubMeshes = msb.ToString().TrimEnd();

        SetSkeleton(skeleton);
        HasMesh = true;
    }

    public void SetSkeleton(SkeletonAsset? skeleton) =>
        SkeletonStatus = skeleton is null ? "No skeleton paired" : $"Skeleton: {skeleton.BoneCount} bones";

    public void Clear()
    {
        HasMesh = false;
        Stats = "";
        SubMeshes = "";
        SkeletonStatus = "No skeleton";
    }
}
