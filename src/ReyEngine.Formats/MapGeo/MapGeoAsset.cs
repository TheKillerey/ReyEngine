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

    // Pristine copies of the baked vertex buffers, cloned lazily on the first edit so repeated moves/
    // rotations/scales recompute from the ORIGINAL geometry instead of compounding floating-point drift.
    private float[]? _originalPositions;
    private float[]? _originalNormals;

    /// <summary>Move a mesh to an absolute world-space translation delta (relative to its original position).</summary>
    public void TranslateMesh(MapGeoMesh mesh, Vector3 delta)
    {
        mesh.Offset = delta;
        ApplyMeshTransform(mesh);
    }

    /// <summary>Set a mesh's rotation (degrees, XYZ order) around its own pivot (local bbox center).</summary>
    public void RotateMesh(MapGeoMesh mesh, Vector3 eulerDegrees)
    {
        mesh.RotationDegrees = eulerDegrees;
        ApplyMeshTransform(mesh);
    }

    /// <summary>Set a mesh's scale around its own pivot (local bbox center).</summary>
    public void ScaleMesh(MapGeoMesh mesh, Vector3 scale)
    {
        mesh.Scale = scale;
        ApplyMeshTransform(mesh);
    }

    /// <summary>Recompute a mesh's baked vertices from its pristine original + its self transform + GroupMatrix.</summary>
    public void ApplyMeshTransform(MapGeoMesh mesh)
    {
        _originalPositions ??= (float[])Positions.Clone();
        _originalNormals ??= (float[])Normals.Clone();

        var sr = mesh.ScaleRotationMatrix;
        var group = mesh.GroupMatrix;
        var pivot = mesh.Pivot;
        var offset = mesh.Offset;
        int start = mesh.VertexStart * 3;
        int end = (mesh.VertexStart + mesh.VertexCount) * 3;

        for (int i = start; i < end; i += 3)
        {
            var op = new Vector3(_originalPositions[i], _originalPositions[i + 1], _originalPositions[i + 2]);
            var self = pivot + Vector3.Transform(op - pivot, sr) + offset; // single-select transform
            var p = Vector3.Transform(self, group);                        // then the batch/group affine
            Positions[i] = p.X; Positions[i + 1] = p.Y; Positions[i + 2] = p.Z;

            var on = new Vector3(_originalNormals[i], _originalNormals[i + 1], _originalNormals[i + 2]);
            var n = Vector3.Normalize(Vector3.TransformNormal(Vector3.TransformNormal(on, sr), group));
            Normals[i] = n.X; Normals[i + 1] = n.Y; Normals[i + 2] = n.Z;
        }
    }

    /// <summary>Undo all edits on a mesh (single + group), restoring its original baked vertices.</summary>
    public void ResetMesh(MapGeoMesh mesh)
    {
        mesh.Offset = Vector3.Zero;
        mesh.RotationDegrees = Vector3.Zero;
        mesh.Scale = Vector3.One;
        mesh.GroupMatrix = Matrix4x4.Identity;
        if (_originalPositions is null) return; // never edited — nothing to restore
        ApplyMeshTransform(mesh);
    }

    // ---- Batch (multi-select) transforms around a shared world-space center (M30) --------------------

    /// <summary>Translate a set of meshes together by a world-space delta.</summary>
    public void BatchTranslate(IEnumerable<MapGeoMesh> meshes, Vector3 delta)
        => ApplyGroup(meshes, Matrix4x4.CreateTranslation(delta));

    /// <summary>Rotate a set of meshes rigidly about <paramref name="center"/> (XYZ euler degrees).</summary>
    public void BatchRotate(IEnumerable<MapGeoMesh> meshes, Vector3 eulerDegrees, Vector3 center)
    {
        var r = Matrix4x4.CreateRotationX(eulerDegrees.X * MathF.PI / 180f)
              * Matrix4x4.CreateRotationY(eulerDegrees.Y * MathF.PI / 180f)
              * Matrix4x4.CreateRotationZ(eulerDegrees.Z * MathF.PI / 180f);
        ApplyGroup(meshes, AroundCenter(r, center));
    }

    /// <summary>Scale a set of meshes about <paramref name="center"/>.</summary>
    public void BatchScale(IEnumerable<MapGeoMesh> meshes, Vector3 scale, Vector3 center)
        => ApplyGroup(meshes, AroundCenter(Matrix4x4.CreateScale(scale), center));

    // world affine that applies m about the given center (translate to origin, m, translate back)
    private static Matrix4x4 AroundCenter(Matrix4x4 m, Vector3 center)
        => Matrix4x4.CreateTranslation(-center) * m * Matrix4x4.CreateTranslation(center);

    private void ApplyGroup(IEnumerable<MapGeoMesh> meshes, Matrix4x4 groupDelta)
    {
        foreach (var mesh in meshes)
        {
            mesh.GroupMatrix *= groupDelta; // W(p) = self(p) * GroupMatrix, so post-multiply to apply after
            ApplyMeshTransform(mesh);
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
    string Name = "", int VisibilityFlags = 255, uint ControllerHash = 0, int MeshIndex = -1);

/// <summary>One source mapgeo mesh: its baked vertex range + original transform, for selection/move.</summary>
public sealed class MapGeoMesh
{
    public required int Index { get; init; }            // index into EnvironmentAsset.Meshes (for write-back)
    public required string Name { get; init; }
    public required int VertexStart { get; init; }
    public required int VertexCount { get; init; }
    public required Matrix4x4 Transform { get; init; }  // original world transform (as read from the file)
    public required Vector3 Pivot { get; init; }         // local bbox center of the baked vertices — the rotate/scale origin
    public int VisibilityFlags { get; init; }
    public uint ControllerHash { get; init; }

    public Vector3 Offset;                                // accumulated single-select move (world space), default zero
    public Vector3 RotationDegrees;                        // accumulated single-select rotation (XYZ euler, degrees)
    public Vector3 Scale = Vector3.One;                     // accumulated single-select scale, default one

    /// <summary>
    /// World-space affine applied AFTER the self (pivot-relative euler/scale/offset) transform. This is where
    /// BATCH (multi-select) move/rotate/scale-around-a-shared-center accumulate — a single mesh's numeric fields
    /// stay meaningful while a group operation composes rigidly on top. Default identity.
    /// </summary>
    public Matrix4x4 GroupMatrix = Matrix4x4.Identity;

    public bool IsMoved =>
        Offset != Vector3.Zero || RotationDegrees != Vector3.Zero || Scale != Vector3.One
        || GroupMatrix != Matrix4x4.Identity;

    /// <summary>The combined scale-then-rotate matrix used both for the live vertex preview and the write-back.</summary>
    public Matrix4x4 ScaleRotationMatrix =>
        Matrix4x4.CreateScale(Scale)
        * Matrix4x4.CreateRotationX(RotationDegrees.X * MathF.PI / 180f)
        * Matrix4x4.CreateRotationY(RotationDegrees.Y * MathF.PI / 180f)
        * Matrix4x4.CreateRotationZ(RotationDegrees.Z * MathF.PI / 180f);
}
