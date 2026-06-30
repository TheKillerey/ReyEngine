using System.Numerics;

namespace ReyEngine.Formats.MapGeo;

/// <summary>
/// A decoded .mapgeo: all environment meshes baked (with their world Transform applied)
/// into combined buffers, plus per-material index groups and stats. Renders through the
/// existing mesh renderer (it is, structurally, one big multi-material mesh).
/// </summary>
public sealed class MapGeoAsset
{
    public required float[] Positions { get; init; }
    public required float[] Normals { get; init; }
    public required float[] Uvs { get; init; }
    public required uint[] Indices { get; init; }
    public required IReadOnlyList<MapGeoGroup> Groups { get; init; }
    public IReadOnlyList<MapGeoMesh> Meshes { get; init; } = Array.Empty<MapGeoMesh>();

    /// <summary>Move a mesh by a world-space delta: shifts its baked vertices in <see cref="Positions"/>
    /// (call after this to re-upload) and accumulates the offset for write-back into the .mapgeo.</summary>
    public void TranslateMesh(MapGeoMesh mesh, Vector3 delta)
    {
        mesh.Offset += delta;
        int end = (mesh.VertexStart + mesh.VertexCount) * 3;
        for (int i = mesh.VertexStart * 3; i < end; i += 3)
        {
            Positions[i] += delta.X;
            Positions[i + 1] += delta.Y;
            Positions[i + 2] += delta.Z;
        }
    }

    public int Version { get; init; }
    public int VertexCount { get; init; }
    public int MeshCount { get; init; }
    public int MaterialCount { get; init; }
    public int IndexCount => Indices.Length;
    public int TriangleCount => Indices.Length / 3;

    public Vector3 BoundsMin { get; init; }
    public Vector3 BoundsMax { get; init; }
    public Vector3 Center => (BoundsMin + BoundsMax) * 0.5f;
    public Vector3 Size => BoundsMax - BoundsMin;
    public float Radius => MathF.Max(Size.Length() * 0.5f, 1f);

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// An index range in the combined buffer for one mesh-submesh: its material plus the source mesh's
/// name and visibility data (dragon-layer bitmask + baron visibility-controller hash) for the layer
/// system. <see cref="VisibilityFlags"/> defaults to 255 (visible on all dragon configurations).
/// </summary>
public sealed record MapGeoGroup(
    string Material, int StartIndex, int IndexCount,
    string Name = "", int VisibilityFlags = 255, uint ControllerHash = 0);

/// <summary>One source mapgeo mesh: its baked vertex range + original transform, for selection/move.</summary>
public sealed class MapGeoMesh
{
    public required int Index { get; init; }            // index into EnvironmentAsset.Meshes (for write-back)
    public required string Name { get; init; }
    public required int VertexStart { get; init; }
    public required int VertexCount { get; init; }
    public required Matrix4x4 Transform { get; init; }  // original world transform
    public int VisibilityFlags { get; init; }
    public uint ControllerHash { get; init; }
    public Vector3 Offset;                               // accumulated user move (world space)

    public bool IsMoved => Offset != Vector3.Zero;
}
