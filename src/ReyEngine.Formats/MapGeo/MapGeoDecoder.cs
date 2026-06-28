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
        var indices = new List<uint>();
        var groups = new List<MapGeoGroup>();
        var materials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        int meshCount = 0;

        foreach (var mesh in env.Meshes)
        {
            try
            {
                var view = mesh.VerticesView;
                int vc = view.VertexCount;
                int baseVertex = positions.Count / 3;
                var transform = mesh.Transform;

                var meshPos = ReadVector3(view.GetAccessor(ElementName.Position), vc);
                var meshNrm = view.TryGetAccessor(ElementName.Normal, out var nAcc) ? ReadVector3(nAcc, vc) : null;
                var meshUv = view.TryGetAccessor(ElementName.Texcoord0, out var tAcc) ? ReadVector2(tAcc, vc) : null;

                for (int i = 0; i < vc; i++)
                {
                    Vector3 p = Vector3.Transform(meshPos[i], transform);
                    positions.Add(p.X); positions.Add(p.Y); positions.Add(p.Z);
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);

                    if (meshNrm is not null)
                    {
                        Vector3 n = Vector3.TransformNormal(meshNrm[i], transform);
                        normals.Add(n.X); normals.Add(n.Y); normals.Add(n.Z);
                    }
                    else { normals.Add(0f); normals.Add(1f); normals.Add(0f); }

                    if (meshUv is not null) { uvs.Add(meshUv[i].X); uvs.Add(meshUv[i].Y); }
                    else { uvs.Add(0f); uvs.Add(0f); }
                }

                var ia = mesh.Indices;
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
                        groups.Add(new MapGeoGroup(material, gStart, indices.Count - gStart));
                    }
                }
                else
                {
                    int gStart = indices.Count;
                    for (int k = 0; k < ia.Count; k++)
                        indices.Add((uint)(ia[k] + baseVertex));
                    groups.Add(new MapGeoGroup("", gStart, indices.Count - gStart));
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
            Indices = indices.ToArray(),
            Groups = groups,
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
