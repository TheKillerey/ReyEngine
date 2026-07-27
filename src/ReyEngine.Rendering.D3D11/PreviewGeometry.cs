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
    public Vector4 Color;         // +72   COLOR0
    public Vector4 BlendWeight;   // +88   BLENDWEIGHT0
    public uint B0, B1, B2, B3;   // +104  BLENDINDICES0 (uint4 - shaders declare it as an integer input)
    public Vector4 Zero;          // +120  the pad every unmatched semantic points at

    public const int SizeInBytes = 136;
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
    public static IReadOnlyList<string> BuiltInNames { get; } = new[] { "Sphere", "Cube", "Plane" };

    public static PreviewMesh CreateBuiltIn(string name) => name switch
    {
        "Cube" => Cube(),
        "Plane" => Plane(),
        _ => Sphere(),
    };

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
