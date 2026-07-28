using System.Numerics;

namespace ReyEngine.Rendering.D3D11;

/// <summary>M210: the "fat vertex" every preview mesh is expanded into.
///
/// <para>League vertex shaders declare whatever attribute set their material needs, and D3D fails
/// <c>CreateInputLayout</c> outright if the layout does not cover every element of the shader's input
/// signature. Rather than build a bespoke layout per shader, the preview expands any mesh into one fixed
/// interleaved vertex and generates the layout from the shader's own <c>ISGN</c>, aliasing any semantic it
/// does not carry onto a zero-filled pad.</para>
///
/// <para>That is a preview affordance, and an honest one: a shader that reads an attribute the test mesh
/// has no data for renders with zeros there, and the window says so rather than silently looking right.</para>
/// </summary>
public struct PreviewVertex
{
    public Vector3 Position;      // +0    POSITION0
    public Vector3 Normal;        // +12   NORMAL0
    public Vector4 Tangent;       // +24   TANGENT0
    public Vector2 Uv0;           // +40   TEXCOORD0
    public Vector2 Uv1;           // +48   TEXCOORD1
    public Vector2 Uv2;           // +56   TEXCOORD2
    public Vector2 Uv3;           // +64   TEXCOORD3
    /// <summary>M224: the LIGHTMAP UV. League's baked-lighting vertex shaders declare TEXCOORD7 - measured
    /// on defaultenv_flat, whose lightmapped permutation takes POSITION0 NORMAL0 TEXCOORD0 TEXCOORD7 while
    /// the NO_BAKED_LIGHTING one takes only the first three. Without a slot for it the semantic aliased onto
    /// the zero pad and every pixel sampled texel (0,0) of the lightmap atlas, which is black.</summary>
    public Vector2 Uv7;           // +72   TEXCOORD7
    public Vector4 Color;         // +80   COLOR0
    public Vector4 BlendWeight;   // +96   BLENDWEIGHT0
    public uint B0, B1, B2, B3;   // +112  BLENDINDICES0 (uint4 - shaders declare it as an integer input)
    /// <summary>M230: the grass clump pivot, which staticmesh/vertexdeform declares as TEXCOORD5 and reads as
    /// a position. Aliasing it onto the zero pad put every blade's pivot at the origin, and a zero-length
    /// vector into an <c>rsq</c> - see the distortion loop in ShaderPreviewRenderer's GrassDistortSpheres
    /// note. Non-grass meshes carry their own vertex position here, making the deform a no-op.</summary>
    public Vector3 GrassPivot;    // +128  TEXCOORD5
    private float _pad;           // +140  keeps Zero 16-byte aligned
    public Vector4 Zero;          // +144  the pad every unmatched semantic points at

    public const int SizeInBytes = 160;
}

/// <summary>A test mesh ready for upload.</summary>
public sealed class PreviewMesh
{
    public required string Name { get; init; }
    public required PreviewVertex[] Vertices { get; init; }
    public required uint[] Indices { get; init; }
    /// <summary>Radius of the bounding sphere about the origin — the camera frames on this.</summary>
    public float Radius { get; init; } = 1f;

    public int TriangleCount => Indices.Length / 3;
}

public static class PreviewGeometry
{
    public static IReadOnlyList<string> BuiltInNames { get; } = new[] { "Sphere", "Cube", "Plane", ParticleQuadName };

    /// <summary>M231: the test mesh for League's particle shaders.</summary>
    public const string ParticleQuadName = "Particle Quad";

    public static PreviewMesh CreateBuiltIn(string name) => name switch
    {
        "Cube" => Cube(),
        "Plane" => Plane(),
        ParticleQuadName => ParticleQuad(Vector3.UnitZ, Vector3.UnitY),
        _ => Sphere(),
    };

    /// <summary>
    /// <para>M231: one camera-facing particle quad, built the way the engine builds them.</para>
    ///
    /// <para><c>particlesystem/quad_vs</c> does NOT billboard - it only projects. Its whole body is:</para>
    /// <code>
    ///   add  r0.xyz, v0.xyzx, -cb2[4].xyzx   // POSITION - vCamera
    ///   rsq / mul                            // normalize that
    ///   mad  r0.xyz, r0.xyzx, cb1[1].xxxx, v0.xyzx   // += dir * PARTICLE_DEPTH_PUSH_PULL
    ///   dp4  o0, r0, cb2[0..3]               // mul(float4(pos,1), mProj)
    ///   mov  o1.xyzw, v1.zyxw                // COLOR0, swizzled
    ///   round_ni r0.x, v2.z                  // floor(TEXCOORD0.z) - a flipbook FRAME INDEX
    /// </code>
    ///
    /// <para>Three things follow, and all three are measured rather than assumed:</para>
    /// <list type="number">
    /// <item>POSITION is in WORLD space - it is differenced against <c>vCamera</c>, a world position - so the
    /// CPU orients the quad and <c>mProj</c> carries the full world-to-clip transform.</item>
    /// <item>COLOR0 is read <c>.zyxw</c>, so the buffer holds <b>BGRA</b>. Feeding RGBA swaps red and blue.</item>
    /// <item>TEXCOORD0.z is a flipbook frame index, not a UV component. The layout supplies TEXCOORD0 as
    /// two floats, so z defaults to 0 - frame 0 - which is what a still preview wants.</item>
    /// </list>
    /// </summary>
    public static PreviewMesh ParticleQuad(Vector3 toCamera, Vector3 up, float size = 1f)
    {
        // Orthonormal basis facing the camera. Falls back to a fixed axis when the view direction is
        // degenerate or parallel to up, so the quad never collapses to a line.
        var n = toCamera.LengthSquared() > 1e-8f ? Vector3.Normalize(toCamera) : Vector3.UnitZ;
        var right = Vector3.Cross(up, n);
        if (right.LengthSquared() < 1e-6f) right = Vector3.Cross(Vector3.UnitX, n);
        if (right.LengthSquared() < 1e-6f) right = Vector3.UnitX;
        right = Vector3.Normalize(right);
        var upv = Vector3.Normalize(Vector3.Cross(n, right));

        float h = size * 0.5f;
        var verts = new PreviewVertex[4];
        // (u,v) with v down, matching a texture atlas read top-left first
        var corners = new (float dx, float dy, float u, float v)[]
        {
            (-1f,  1f, 0f, 0f),
            ( 1f,  1f, 1f, 0f),
            ( 1f, -1f, 1f, 1f),
            (-1f, -1f, 0f, 1f),
        };
        for (int i = 0; i < 4; i++)
        {
            var (dx, dy, u, v) = corners[i];
            var p = right * (dx * h) + upv * (dy * h);
            verts[i] = Make(p, n, new Vector2(u, v), right);
            // BGRA, per the .zyxw read above. Opaque white either way, but the order is the point.
            verts[i].Color = new Vector4(1f, 1f, 1f, 1f);
            verts[i].Uv1 = new Vector2(u, v);   // the separate alpha UV, same mapping by default
        }

        return new PreviewMesh
        {
            Name = ParticleQuadName,
            Vertices = verts,
            Indices = new uint[] { 0, 1, 2, 0, 2, 3 },
            Radius = h * 1.4142f,
        };
    }

    /// <summary>UV sphere. Tangents follow the u direction so tangent-space shaders have something sane.</summary>
    public static PreviewMesh Sphere(int segments = 48, int rings = 32, float radius = 1f)
    {
        var verts = new List<PreviewVertex>((segments + 1) * (rings + 1));
        var idx = new List<uint>(segments * rings * 6);

        for (int y = 0; y <= rings; y++)
        {
            float v = (float)y / rings;
            float phi = v * MathF.PI;
            for (int x = 0; x <= segments; x++)
            {
                float u = (float)x / segments;
                float theta = u * MathF.PI * 2f;
                var n = new Vector3(
                    MathF.Sin(phi) * MathF.Cos(theta),
                    MathF.Cos(phi),
                    MathF.Sin(phi) * MathF.Sin(theta));
                var tangent = new Vector3(-MathF.Sin(theta), 0f, MathF.Cos(theta));
                verts.Add(Make(n * radius, n, new Vector2(u, v), tangent));
            }
        }
        for (int y = 0; y < rings; y++)
            for (int x = 0; x < segments; x++)
            {
                uint a = (uint)(y * (segments + 1) + x);
                uint b = (uint)(a + segments + 1);
                idx.Add(a); idx.Add(b); idx.Add(a + 1);
                idx.Add(a + 1); idx.Add(b); idx.Add(b + 1);
            }

        return new PreviewMesh { Name = "Sphere", Vertices = verts.ToArray(), Indices = idx.ToArray(), Radius = radius };
    }

    public static PreviewMesh Cube(float half = 1f)
    {
        var verts = new List<PreviewVertex>(24);
        var idx = new List<uint>(36);
        (Vector3 n, Vector3 t)[] faces =
        {
            (new(0, 0, 1), new(1, 0, 0)),  (new(0, 0, -1), new(-1, 0, 0)),
            (new(1, 0, 0), new(0, 0, -1)), (new(-1, 0, 0), new(0, 0, 1)),
            (new(0, 1, 0), new(1, 0, 0)),  (new(0, -1, 0), new(1, 0, 0)),
        };
        foreach (var (n, t) in faces)
        {
            var b = Vector3.Cross(n, t);
            uint bas = (uint)verts.Count;
            verts.Add(Make((n - t - b) * half, n, new Vector2(0, 1), t));
            verts.Add(Make((n + t - b) * half, n, new Vector2(1, 1), t));
            verts.Add(Make((n + t + b) * half, n, new Vector2(1, 0), t));
            verts.Add(Make((n - t + b) * half, n, new Vector2(0, 0), t));
            idx.Add(bas); idx.Add(bas + 1); idx.Add(bas + 2);
            idx.Add(bas); idx.Add(bas + 2); idx.Add(bas + 3);
        }
        return new PreviewMesh
        {
            Name = "Cube", Vertices = verts.ToArray(), Indices = idx.ToArray(),
            Radius = half * MathF.Sqrt(3f),
        };
    }

    /// <summary>A ground quad in the XZ plane — the natural subject for terrain and water shaders.</summary>
    public static PreviewMesh Plane(float half = 1.5f, int subdiv = 32)
    {
        var verts = new List<PreviewVertex>();
        var idx = new List<uint>();
        for (int z = 0; z <= subdiv; z++)
            for (int x = 0; x <= subdiv; x++)
            {
                float u = (float)x / subdiv, v = (float)z / subdiv;
                verts.Add(Make(
                    new Vector3((u * 2f - 1f) * half, 0f, (v * 2f - 1f) * half),
                    Vector3.UnitY, new Vector2(u, v), Vector3.UnitX));
            }
        for (int z = 0; z < subdiv; z++)
            for (int x = 0; x < subdiv; x++)
            {
                uint a = (uint)(z * (subdiv + 1) + x);
                uint b = (uint)(a + subdiv + 1);
                idx.Add(a); idx.Add(b); idx.Add(a + 1);
                idx.Add(a + 1); idx.Add(b); idx.Add(b + 1);
            }
        return new PreviewMesh
        {
            Name = "Plane", Vertices = verts.ToArray(), Indices = idx.ToArray(),
            Radius = half * MathF.Sqrt(2f),
        };
    }

    /// <summary>Wrap externally supplied geometry (an imported .scb/.skn) in the fat vertex. Normals and
    /// UVs are optional; anything missing stays zero and is reported rather than invented.</summary>
    public static PreviewMesh FromArrays(string name, Vector3[] positions, Vector3[]? normals,
        Vector2[]? uv0, Vector2[]? uv1, uint[] indices)
    {
        var verts = new PreviewVertex[positions.Length];
        float r = 0f;
        for (int i = 0; i < positions.Length; i++)
        {
            verts[i] = Make(
                positions[i],
                normals is not null && i < normals.Length ? normals[i] : Vector3.UnitY,
                uv0 is not null && i < uv0.Length ? uv0[i] : Vector2.Zero,
                Vector3.UnitX);
            if (uv1 is not null && i < uv1.Length) verts[i].Uv1 = uv1[i];
            r = MathF.Max(r, positions[i].Length());
        }
        return new PreviewMesh
        {
            Name = name, Vertices = verts, Indices = indices,
            Radius = r > 1e-4f ? r : 1f,
        };
    }

    /// <summary>M214: wrap a decoded League mesh - a champion .skn or a .mapgeo - in the fat vertex.
    ///
    /// <para>The geometry is RECENTRED on its own bounds. Map geometry sits at world coordinates in the tens
    /// of thousands, and a camera that orbits the origin would be looking at empty space several map-widths
    /// away from it. The submesh index ranges are untouched, so material slices still line up.</para></summary>
    public static PreviewMesh FromLeagueArrays(
        string name, int vertexCount,
        float[] positions, float[]? normals, float[]? uvs, float[]? colors, float[]? lightmapUvs,
        uint[] indices, int[]? blendIndices = null, float[]? blendWeights = null,
        float[]? grassPivots = null)
    {
        var verts = new PreviewVertex[vertexCount];

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i < vertexCount; i++)
        {
            var pv = new Vector3(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]);
            min = Vector3.Min(min, pv);
            max = Vector3.Max(max, pv);
        }
        var centre = (min + max) * 0.5f;
        float radius = MathF.Max((max - min).Length() * 0.5f, 1e-3f);

        for (int i = 0; i < vertexCount; i++)
        {
            var pos = new Vector3(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]) - centre;
            var nrm = normals is not null && normals.Length >= (i + 1) * 3
                ? new Vector3(normals[i * 3], normals[i * 3 + 1], normals[i * 3 + 2])
                : Vector3.UnitY;
            if (nrm.LengthSquared() < 1e-8f) nrm = Vector3.UnitY;

            var uv = uvs is not null && uvs.Length >= (i + 1) * 2
                ? new Vector2(uvs[i * 2], uvs[i * 2 + 1]) : Vector2.Zero;

            verts[i] = Make(pos, nrm, uv, Vector3.UnitX);

            // M230: recentred by the SAME offset as the position. The pivot is only meaningful as a point in
            // the same space as the geometry - offsetting one and not the other would put every clump's
            // pivot a map-width away, which is the bug this fixes, just with a different constant.
            verts[i].GrassPivot = grassPivots is not null && grassPivots.Length >= (i + 1) * 3
                ? new Vector3(grassPivots[i * 3], grassPivots[i * 3 + 1], grassPivots[i * 3 + 2]) - centre
                : pos;

            // M224: onto TEXCOORD7, which is what the shaders actually read. The decoder has already
            // applied the per-mesh atlas scale/bias, so these are final atlas coordinates.
            if (lightmapUvs is not null && lightmapUvs.Length >= (i + 1) * 2)
                verts[i].Uv7 = new Vector2(lightmapUvs[i * 2], lightmapUvs[i * 2 + 1]);
            if (colors is not null && colors.Length >= (i + 1) * 4)
                verts[i].Color = new Vector4(colors[i * 4], colors[i * 4 + 1], colors[i * 4 + 2], colors[i * 4 + 3]);

            // M216: skinning inputs. Without these every vertex sits on bone 0 at full weight, so the whole
            // mesh takes one transform and the bone palette may as well be a single matrix - which is why
            // the 3-row and 4-row strides measured identically and told us nothing.
            if (blendIndices is not null && blendIndices.Length >= (i + 1) * 4)
            {
                verts[i].B0 = (uint)blendIndices[i * 4];
                verts[i].B1 = (uint)blendIndices[i * 4 + 1];
                verts[i].B2 = (uint)blendIndices[i * 4 + 2];
                verts[i].B3 = (uint)blendIndices[i * 4 + 3];
            }
            if (blendWeights is not null && blendWeights.Length >= (i + 1) * 4)
                verts[i].BlendWeight = new Vector4(blendWeights[i * 4], blendWeights[i * 4 + 1],
                    blendWeights[i * 4 + 2], blendWeights[i * 4 + 3]);
        }

        return new PreviewMesh { Name = name, Vertices = verts, Indices = indices, Radius = radius };
    }

    private static PreviewVertex Make(Vector3 pos, Vector3 normal, Vector2 uv, Vector3 tangent) => new()
    {
        Position = pos,
        Normal = Vector3.Normalize(normal),
        Tangent = new Vector4(Vector3.Normalize(tangent), 1f),
        Uv0 = uv, Uv1 = uv, Uv2 = uv, Uv3 = uv,
        Color = Vector4.One,
        BlendWeight = new Vector4(1f, 0f, 0f, 0f),
        B0 = 0, B1 = 0, B2 = 0, B3 = 0,
        Zero = Vector4.Zero,
    };
}
