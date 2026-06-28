using System.Numerics;

namespace ReyEngine.Formats.Meshes;

/// <summary>Render-ready mesh: de-interleaved attribute arrays + submesh ranges + bounds.</summary>
public sealed class MeshAsset
{
    public required float[] Positions { get; init; }  // 3 floats / vertex
    public required float[] Normals { get; init; }    // 3 floats / vertex (zeros if absent)
    public required float[] Uvs { get; init; }        // 2 floats / vertex (zeros if absent)
    public required uint[] Indices { get; init; }
    public required IReadOnlyList<SubMeshInfo> SubMeshes { get; init; }

    public int VertexCount { get; init; }
    public int IndexCount => Indices.Length;
    public int TriangleCount => Indices.Length / 3;

    public Vector3 BoundsMin { get; init; }
    public Vector3 BoundsMax { get; init; }
    public Vector3 Center => (BoundsMin + BoundsMax) * 0.5f;
    public Vector3 Size => BoundsMax - BoundsMin;
    public float Radius => MathF.Max(Size.Length() * 0.5f, 1f);
}

public sealed record SubMeshInfo(string Material, int StartIndex, int IndexCount, int VertexCount);
