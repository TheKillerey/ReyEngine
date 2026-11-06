using System;
using System.Linq;
using System.Numerics;
using ReyEngine.Formats.Meshes;

namespace ReyEngine.App.Services;

/// <summary>
/// M861: what a mesh emitter's <c>CustomMaterial</c> needs on the way to Riot's shader path. The skin path
/// (<see cref="Dx11CharacterScene"/>) resolves a material by SUBMESH NAME, and a particle mesh is a .scb soup whose
/// submesh name has nothing to do with the material - so the emitter's mesh becomes a one-submesh <see cref="MeshAsset"/>
/// named for the material it links, and the scene resolves that binding like any other.
/// </summary>
public static class VfxCustomMaterial
{
    /// <summary>The emitter's mesh as a single-submesh asset named <paramref name="materialName"/>. The .scb soup has no
    /// normals, so each triangle takes its flat face normal - lit materials read them.</summary>
    public static MeshAsset MeshOf(StaticMeshData mesh, string materialName)
    {
        int vertices = mesh.Positions.Length / 3;
        var normals = new float[vertices * 3];
        for (int t = 0; t + 2 < mesh.Indices.Length; t += 3)
        {
            uint a = mesh.Indices[t], b = mesh.Indices[t + 1], c = mesh.Indices[t + 2];
            if (a >= vertices || b >= vertices || c >= vertices) continue;
            var pa = new Vector3(mesh.Positions[a * 3], mesh.Positions[a * 3 + 1], mesh.Positions[a * 3 + 2]);
            var pb = new Vector3(mesh.Positions[b * 3], mesh.Positions[b * 3 + 1], mesh.Positions[b * 3 + 2]);
            var pc = new Vector3(mesh.Positions[c * 3], mesh.Positions[c * 3 + 1], mesh.Positions[c * 3 + 2]);
            var n = Vector3.Cross(pb - pa, pc - pa);
            n = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY;
            foreach (uint v in new[] { a, b, c }) { normals[v * 3] = n.X; normals[v * 3 + 1] = n.Y; normals[v * 3 + 2] = n.Z; }
        }
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        for (int v = 0; v < vertices; v++)
        {
            var p = new Vector3(mesh.Positions[v * 3], mesh.Positions[v * 3 + 1], mesh.Positions[v * 3 + 2]);
            min = Vector3.Min(min, p); max = Vector3.Max(max, p);
        }
        return new MeshAsset
        {
            Positions = mesh.Positions, Normals = normals, Uvs = mesh.Uvs, Indices = mesh.Indices, Colors = mesh.Colors,
            VertexCount = vertices,
            SubMeshes = new[] { new SubMeshInfo(materialName, 0, mesh.Indices.Length, vertices) },
            BoundsMin = vertices > 0 ? min : Vector3.Zero, BoundsMax = vertices > 0 ? max : Vector3.Zero,
        };
    }

    /// <summary>The world matrix of one mesh particle, composed as the particle mesh path composes it (scale, then the birth
    /// Euler - Z, X, Y - then the spin about Y, then the emitter's placement basis, then the position): the mesh shader's
    /// <c>vsmain</c> and <c>DrawRiotMeshInstances</c>. Row-vector convention.</summary>
    public static Matrix4x4 ParticleWorld(float[] instances, int index, Vector3 right, Vector3 up, Vector3 forward)
    {
        int o = index * ReyEngine.Rendering.D3D11.ParticleQuadBuilder.Stride;
        float scale = instances[o + ReyEngine.Rendering.D3D11.ParticleQuadBuilder.OffSizeX];
        if (MathF.Abs(scale) < 0.01f) scale = MathF.CopySign(0.01f, scale == 0f ? 1f : scale);
        var position = new Vector3(instances[o + ReyEngine.Rendering.D3D11.ParticleQuadBuilder.OffPos],
            instances[o + ReyEngine.Rendering.D3D11.ParticleQuadBuilder.OffPos + 1], instances[o + ReyEngine.Rendering.D3D11.ParticleQuadBuilder.OffPos + 2]);
        float spin = instances[o + ReyEngine.Rendering.D3D11.ParticleQuadBuilder.OffRot];
        float ex = instances[o + ReyEngine.Rendering.D3D11.ParticleQuadBuilder.OffEuler];
        float ey = instances[o + ReyEngine.Rendering.D3D11.ParticleQuadBuilder.OffEuler + 1];
        float ez = instances[o + ReyEngine.Rendering.D3D11.ParticleQuadBuilder.OffEuler + 2];
        var basis = new Matrix4x4(right.X, right.Y, right.Z, 0f, up.X, up.Y, up.Z, 0f, forward.X, forward.Y, forward.Z, 0f,
            position.X, position.Y, position.Z, 1f);
        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationZ(ez) * Matrix4x4.CreateRotationX(ex)
            * Matrix4x4.CreateRotationY(ey) * Matrix4x4.CreateRotationY(-spin) * basis;
    }
}
