using System.Numerics;

namespace ReyEngine.Rendering.D3D11;

/// <summary>
/// <para>M232: turns a particle simulator's packed instance floats into camera-facing quads for
/// <c>particlesystem/quad_vs</c>.</para>
///
/// <para>Takes raw arrays rather than a simulator, for two reasons: this assembly must not depend on the
/// OpenGL one, and the packing rules below are the part worth testing — they are where a wrong swizzle or a
/// wrong component silently produces a plausible-looking but incorrect image.</para>
///
/// <para>Three packing rules, all measured off <c>quad_vs</c> in M231/M232 rather than assumed:</para>
/// <list type="bullet">
/// <item>the shader does NOT billboard (<c>dp4 o0, r0, cb2[0..3]</c> and nothing else), so the orientation
/// is built here, exactly as League's own particle system builds it on the CPU;</item>
/// <item><c>COLOR0</c> is read <c>.zyxw</c>, so the vertex must hold <b>BGRA</b>;</item>
/// <item><c>TEXCOORD0</c> is a float4: <c>.xy</c> cell UV, <c>.z</c> flipbook frame index
/// (<c>round_ni r0.x, v2.z</c>), <c>.w</c> the alpha-erosion drive (<c>mov o3.z, v2.w</c>).</item>
/// </list>
/// </summary>
public static class ParticleQuadBuilder
{
    /// <summary>Field offsets inside one instance record, as VfxParticleSimulator.BuildInstances writes it.
    /// 19 floats: pos(0-2) sizeX(3) sizeY(4) rgba(5-8) rot(9) frame(10) age(11) vel(12-14) euler(15-17)
    /// erosionDrive(18).</summary>
    public const int Stride = 19;
    public const int OffPos = 0, OffSizeX = 3, OffSizeY = 4, OffColor = 5, OffRot = 9, OffFrame = 10, OffErosion = 18;

    /// <summary>An orthonormal billboard basis facing <paramref name="toCamera"/>. Falls back to fixed axes
    /// when the direction is degenerate or parallel to up, so a quad never collapses to a line.</summary>
    public static (Vector3 Right, Vector3 Up, Vector3 Normal) Basis(Vector3 toCamera, Vector3 worldUp)
    {
        var n = toCamera.LengthSquared() > 1e-8f ? Vector3.Normalize(toCamera) : Vector3.UnitZ;
        var right = Vector3.Cross(worldUp, n);
        if (right.LengthSquared() < 1e-6f) right = Vector3.Cross(Vector3.UnitX, n);
        if (right.LengthSquared() < 1e-6f) right = Vector3.UnitX;
        right = Vector3.Normalize(right);
        return (right, Vector3.Normalize(Vector3.Cross(n, right)), n);
    }

    /// <summary>
    /// Append <paramref name="count"/> quads from <paramref name="instances"/> into the vertex and index
    /// buffers at <paramref name="vertexCursor"/> / <paramref name="indexCursor"/>, advancing both.
    /// Writes nothing and returns 0 if the buffers cannot hold the request.
    /// </summary>
    public static int Append(
        float[] instances, int count,
        PreviewVertex[] verts, ref int vertexCursor,
        uint[] indices, ref int indexCursor,
        Vector3 right, Vector3 up, Vector3 normal)
    {
        int written = 0;
        for (int p = 0; p < count; p++)
        {
            int o = p * Stride;
            if (o + Stride > instances.Length) break;
            if (vertexCursor + 4 > verts.Length || indexCursor + 6 > indices.Length) break;

            var pos = new Vector3(instances[o + OffPos], instances[o + OffPos + 1], instances[o + OffPos + 2]);
            float hx = instances[o + OffSizeX] * 0.5f;
            float hy = instances[o + OffSizeY] * 0.5f;
            float rot = instances[o + OffRot];

            // BGRA. The simulator stores RGBA; quad_vs reads COLOR0.zyxw.
            var colour = new Vector4(
                instances[o + OffColor + 2], instances[o + OffColor + 1],
                instances[o + OffColor + 0], instances[o + OffColor + 3]);

            float frame = instances[o + OffFrame];
            float erosion = instances[o + OffErosion];

            // Per-particle roll about the view axis, applied to the basis rather than to each corner.
            float cs = MathF.Cos(rot), sn = MathF.Sin(rot);
            var rx = right * cs + up * sn;
            var ry = up * cs - right * sn;

            uint b = (uint)vertexCursor;
            for (int k = 0; k < 4; k++)
            {
                var (dx, dy, u, v) = Corner(k);
                ref var vert = ref verts[vertexCursor++];
                vert = default;
                vert.Position = pos + rx * (dx * hx) + ry * (dy * hy);
                vert.Normal = normal;
                vert.Tangent = new Vector4(rx, 1f);
                vert.Uv0 = new Vector4(u, v, frame, erosion);
                vert.Uv1 = new Vector2(u, v);
                vert.Color = colour;
            }

            indices[indexCursor++] = b;
            indices[indexCursor++] = b + 1;
            indices[indexCursor++] = b + 2;
            indices[indexCursor++] = b;
            indices[indexCursor++] = b + 2;
            indices[indexCursor++] = b + 3;
            written++;
        }
        return written;
    }

    /// <summary>Quad corner k: offset in [-1,1] and its UV, v increasing downward to match an atlas read
    /// from the top-left.</summary>
    private static (float dx, float dy, float u, float v) Corner(int k) => k switch
    {
        0 => (-1f, 1f, 0f, 0f),
        1 => (1f, 1f, 1f, 0f),
        2 => (1f, -1f, 1f, 1f),
        _ => (-1f, -1f, 0f, 1f),
    };

    /// <summary>The flipbook atlas descriptor <c>quad_vs</c> expects: (columns, 1/columns, 1/rows, 0).
    /// Derived from its cell arithmetic — see PreviewGeometry.ParticleQuad. A (0,0) or (1,1) texDiv is a
    /// single-frame sprite, and must pass the UV through untouched rather than crop it.</summary>
    public static float[] TextureInfo(Vector2 texDiv)
    {
        float cols = texDiv.X >= 1f ? texDiv.X : 1f;
        float rows = texDiv.Y >= 1f ? texDiv.Y : 1f;
        return new[] { cols, 1f / cols, 1f / rows, 0f };
    }
}
