using System.Buffers.Binary;
using System.Numerics;
using LeagueToolkit.Core.Environment;
using LeagueToolkit.Core.Memory;

namespace ReyEngine.Formats.MapGeo;

public static class MapGeoDecoder
{
    public static MapGeoAsset Decode(byte[] data)
    {
        int version = data.Length >= 8 ? (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4)) : 0;

        using var ms = new MemoryStream(data, writable: false);
        var env = new EnvironmentAsset(ms);

        var positions = new List<float>();
        var normals = new List<float>();
        var uvs = new List<float>();
        var colors = new List<float>();   // PrimaryColor RGBA 0..1 (white when a mesh has none)
        bool anyColor = false;
        var indices = new List<uint>();
        var groups = new List<MapGeoGroup>();
        var meshes = new List<MapGeoMesh>();
        var materials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        int meshCount = 0;
        int meshIndex = -1;

        foreach (var mesh in env.Meshes)
        {
            meshIndex++;
            try
            {
                var view = mesh.VerticesView;
                int vc = view.VertexCount;
                int baseVertex = positions.Count / 3;
                var transform = mesh.Transform;

                var meshPos = ReadVector3(view.GetAccessor(ElementName.Position), vc);
                var meshNrm = view.TryGetAccessor(ElementName.Normal, out var nAcc) ? ReadVector3(nAcc, vc) : null;
                var meshUv = view.TryGetAccessor(ElementName.Texcoord0, out var tAcc) ? ReadVector2(tAcc, vc) : null;
                var meshCol = view.TryGetAccessor(ElementName.PrimaryColor, out var cAcc) ? ReadColor(cAcc, vc) : null;
                if (meshCol is not null) anyColor = true;

                var meshMin = new Vector3(float.MaxValue);
                var meshMax = new Vector3(float.MinValue);

                for (int i = 0; i < vc; i++)
                {
                    Vector3 p = Vector3.Transform(meshPos[i], transform);
                    positions.Add(p.X); positions.Add(p.Y); positions.Add(p.Z);
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                    meshMin = Vector3.Min(meshMin, p);
                    meshMax = Vector3.Max(meshMax, p);

                    if (meshNrm is not null)
                    {
                        Vector3 n = Vector3.TransformNormal(meshNrm[i], transform);
                        normals.Add(n.X); normals.Add(n.Y); normals.Add(n.Z);
                    }
                    else { normals.Add(0f); normals.Add(1f); normals.Add(0f); }

                    if (meshUv is not null) { uvs.Add(meshUv[i].X); uvs.Add(meshUv[i].Y); }
                    else { uvs.Add(0f); uvs.Add(0f); }

                    if (meshCol is not null) { var c = meshCol[i]; colors.Add(c.X); colors.Add(c.Y); colors.Add(c.Z); colors.Add(c.W); }
                    else { colors.Add(1f); colors.Add(1f); colors.Add(1f); colors.Add(1f); }
                }

                var ia = mesh.Indices;
                int vis = (int)mesh.VisibilityFlags;
                uint ctrl = mesh.VisibilityControllerPathHash;
                string meshName = mesh.Name ?? "";
                meshes.Add(new MapGeoMesh
                {
                    Index = meshIndex, Name = meshName, VertexStart = baseVertex, VertexCount = vc,
                    Transform = transform, VisibilityFlags = vis, ControllerHash = ctrl,
                    Pivot = vc > 0 ? (meshMin + meshMax) * 0.5f : transform.Translation,
                });
                if (mesh.Submeshes.Count > 0)
                {
                    foreach (var sub in mesh.Submeshes)
                    {
                        string material = sub.Material ?? "";
                        materials.Add(material);
                        int gStart = indices.Count;
                        int end = sub.StartIndex + sub.IndexCount;
                        for (int k = sub.StartIndex; k < end && k < ia.Count; k++)
                            indices.Add((uint)(ia[k] + baseVertex));
                        groups.Add(new MapGeoGroup(material, gStart, indices.Count - gStart, meshName, vis, ctrl, meshIndex));
                    }
                }
                else
                {
                    int gStart = indices.Count;
                    for (int k = 0; k < ia.Count; k++)
                        indices.Add((uint)(ia[k] + baseVertex));
                    groups.Add(new MapGeoGroup("", gStart, indices.Count - gStart, meshName, vis, ctrl, meshIndex));
                }

                meshCount++;
            }
            catch (Exception ex)
            {
                warnings.Add($"mesh '{mesh.Name}': {ex.Message}");
            }
        }

        if (positions.Count == 0) { min = Vector3.Zero; max = Vector3.Zero; }

        return new MapGeoAsset
        {
            Positions = positions.ToArray(),
            Normals = normals.ToArray(),
            Uvs = uvs.ToArray(),
            Colors = anyColor ? colors.ToArray() : null,
            HasVertexColor = anyColor,
            Indices = indices.ToArray(),
            Groups = groups,
            Meshes = meshes,
            Version = version,
            VertexCount = positions.Count / 3,
            MeshCount = meshCount,
            MaterialCount = materials.Count,
            BoundsMin = min,
            BoundsMax = max,
            Warnings = warnings,
        };
    }

    // mapgeo attributes can be float32 OR packed 16-bit (half). Read whichever this accessor is.
    private static Vector3[] ReadVector3(VertexElementAccessor acc, int count)
    {
        var result = new Vector3[count];
        try
        {
            var arr = acc.AsVector3Array();
            for (int i = 0; i < count; i++) result[i] = arr[i];
        }
        catch
        {
            var arr = acc.AsXyzF16Array();
            for (int i = 0; i < count; i++)
            {
                var h = arr[i];
                result[i] = new Vector3((float)h.Item1, (float)h.Item2, (float)h.Item3);
            }
        }
        return result;
    }

    // PrimaryColor is BGRA_Packed8888 (bytes) — normalize to RGBA 0..1. Falls back to Vector4 float colors.
    private static Vector4[] ReadColor(VertexElementAccessor acc, int count)
    {
        var result = new Vector4[count];
        try
        {
            var arr = acc.AsBgraU8Array();
            for (int i = 0; i < count; i++)
            {
                var c = arr[i]; // (byte b, byte g, byte r, byte a)
                result[i] = new Vector4(c.r / 255f, c.g / 255f, c.b / 255f, c.a / 255f);
            }
        }
        catch
        {
            try { var arr = acc.AsVector4Array(); for (int i = 0; i < count; i++) result[i] = arr[i]; }
            catch { for (int i = 0; i < count; i++) result[i] = Vector4.One; }
        }
        return result;
    }

    private static Vector2[] ReadVector2(VertexElementAccessor acc, int count)
    {
        var result = new Vector2[count];
        try
        {
            var arr = acc.AsVector2Array();
            for (int i = 0; i < count; i++) result[i] = arr[i];
        }
        catch
        {
            var arr = acc.AsXyF16Array();
            for (int i = 0; i < count; i++)
            {
                var h = arr[i];
                result[i] = new Vector2((float)h.Item1, (float)h.Item2);
            }
        }
        return result;
    }
}
