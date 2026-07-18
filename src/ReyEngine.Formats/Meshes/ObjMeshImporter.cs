using System.Globalization;

namespace ReyEngine.Formats.Meshes;

/// <summary>
/// M79: minimal Wavefront OBJ importer → flat float buffers (positions, normals, uvs, u16-friendly indices)
/// ready for the mapgeo mesh appender. Triangulates n-gons (fan), de-duplicates v/vt/vn triples into unique
/// vertices, synthesises flat normals when the file has none. Ignores materials/groups (one combined mesh).
/// League map space is Y-up like OBJ, so positions pass through unchanged. Never throws — null on failure.
/// </summary>
public static class ObjMeshImporter
{
    public sealed record ImportedMesh(float[] Positions, float[] Normals, float[] Uvs, int[] Indices, string Name);

    public static ImportedMesh? Import(string text, string name)
    {
        try
        {
            var v = new List<(float X, float Y, float Z)>();
            var vt = new List<(float U, float V)>();
            var vn = new List<(float X, float Y, float Z)>();
            var vertexMap = new Dictionary<(int, int, int), int>();
            var outPos = new List<float>();
            var outUv = new List<float>();
            var outNrm = new List<float>();
            var indices = new List<int>();
            bool anyNormal = false;

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var tok = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                switch (tok[0])
                {
                    case "v" when tok.Length >= 4:
                        v.Add((F(tok[1]), F(tok[2]), F(tok[3])));
                        break;
                    case "vt" when tok.Length >= 3:
                        vt.Add((F(tok[1]), F(tok[2])));
                        break;
                    case "vn" when tok.Length >= 4:
                        vn.Add((F(tok[1]), F(tok[2]), F(tok[3])));
                        anyNormal = true;
                        break;
                    case "f" when tok.Length >= 4:
                    {
                        // resolve each face vertex to a unique output vertex, then fan-triangulate
                        var faceVerts = new int[tok.Length - 1];
                        for (int i = 1; i < tok.Length; i++)
                            faceVerts[i - 1] = Resolve(tok[i], v, vt, vn, vertexMap, outPos, outUv, outNrm);
                        for (int i = 1; i + 1 < faceVerts.Length; i++)
                        {
                            indices.Add(faceVerts[0]); indices.Add(faceVerts[i]); indices.Add(faceVerts[i + 1]);
                        }
                        break;
                    }
                }
            }

            if (outPos.Count == 0 || indices.Count < 3) return null;
            var normals = outNrm.ToArray();
            if (!anyNormal) normals = ComputeFlatNormals(outPos, indices);
            return new ImportedMesh(outPos.ToArray(), normals, outUv.ToArray(), indices.ToArray(), name);
        }
        catch { return null; }
    }

    private static int Resolve(string face, List<(float, float, float)> v, List<(float, float)> vt,
        List<(float, float, float)> vn, Dictionary<(int, int, int), int> map,
        List<float> outPos, List<float> outUv, List<float> outNrm)
    {
        var parts = face.Split('/');
        int vi = Idx(parts[0], v.Count);
        int ti = parts.Length > 1 && parts[1].Length > 0 ? Idx(parts[1], vt.Count) : -1;
        int ni = parts.Length > 2 && parts[2].Length > 0 ? Idx(parts[2], vn.Count) : -1;
        var key = (vi, ti, ni);
        if (map.TryGetValue(key, out var existing)) return existing;

        int index = outPos.Count / 3;
        var p = v[vi];
        outPos.Add(p.Item1); outPos.Add(p.Item2); outPos.Add(p.Item3);
        var uv = ti >= 0 ? vt[ti] : (0f, 0f);
        outUv.Add(uv.Item1); outUv.Add(uv.Item2);
        var n = ni >= 0 ? vn[ni] : (0f, 1f, 0f);
        outNrm.Add(n.Item1); outNrm.Add(n.Item2); outNrm.Add(n.Item3);
        map[key] = index;
        return index;
    }

    private static float[] ComputeFlatNormals(List<float> pos, List<int> idx)
    {
        var n = new float[pos.Count];
        for (int t = 0; t + 2 < idx.Count; t += 3)
        {
            int a = idx[t], b = idx[t + 1], c = idx[t + 2];
            var ax = pos[a * 3]; var ay = pos[a * 3 + 1]; var az = pos[a * 3 + 2];
            var bx = pos[b * 3]; var by = pos[b * 3 + 1]; var bz = pos[b * 3 + 2];
            var cx = pos[c * 3]; var cy = pos[c * 3 + 1]; var cz = pos[c * 3 + 2];
            float ux = bx - ax, uy = by - ay, uz = bz - az;
            float vx = cx - ax, vy = cy - ay, vz = cz - az;
            float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            foreach (var vi in stackalloc[] { a, b, c })
            { n[vi * 3] += nx; n[vi * 3 + 1] += ny; n[vi * 3 + 2] += nz; }
        }
        for (int i = 0; i + 2 < n.Length; i += 3)
        {
            float len = MathF.Sqrt(n[i] * n[i] + n[i + 1] * n[i + 1] + n[i + 2] * n[i + 2]);
            if (len > 1e-6f) { n[i] /= len; n[i + 1] /= len; n[i + 2] /= len; }
            else { n[i] = 0; n[i + 1] = 1; n[i + 2] = 0; }
        }
        return n;
    }

    private static int Idx(string s, int count)
    {
        int i = int.Parse(s, CultureInfo.InvariantCulture);
        return i > 0 ? i - 1 : count + i;   // OBJ indices are 1-based; negative = relative to end
    }

    private static float F(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
}
