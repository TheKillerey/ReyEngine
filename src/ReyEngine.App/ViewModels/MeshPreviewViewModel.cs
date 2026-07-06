using CommunityToolkit.Mvvm.ComponentModel;
using ReyEngine.Core.Decoding;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Skeletons;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M50: character/mesh preview in its OWN window (separate viewport), so the main viewport stays
/// dedicated to the map like Unreal/Unity. Holds its own copies of the mesh/skeleton/textures —
/// no shared state with the map viewport.
/// </summary>
public sealed partial class MeshPreviewViewModel : ObservableObject
{
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _stats = "";
    [ObservableProperty] private MeshAsset? _mesh;
    [ObservableProperty] private SkeletonAsset? _skeleton;
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _textures;
    [ObservableProperty] private bool _showBones;
    [ObservableProperty] private bool _wireframe;
    [ObservableProperty] private bool _cullBackfaces = true;

    public void Show(string title, MeshAsset mesh, SkeletonAsset? skeleton, IReadOnlyList<TextureImage?>? textures)
    {
        Title = title;
        Mesh = mesh;
        Skeleton = skeleton;
        Textures = textures;
        ShowBones = skeleton is not null;
        Stats = $"{mesh.VertexCount:n0} verts · {mesh.TriangleCount:n0} tris · {mesh.SubMeshes.Count} submesh(es)" +
                (skeleton is not null ? $" · {skeleton.BoneCount} bones" : "");
    }
}
