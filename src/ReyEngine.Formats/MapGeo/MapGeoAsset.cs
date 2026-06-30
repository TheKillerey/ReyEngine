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
