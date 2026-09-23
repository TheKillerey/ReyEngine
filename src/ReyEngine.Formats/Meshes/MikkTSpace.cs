// M758: a C# port of MikkTSpace, for triangle lists.
//
// Ported from bevy_mikktspace 1.0.0 (MIT/Apache-2.0), itself a Rust reimplementation of
// https://github.com/mmikk/MikkTSpace, with its DEFAULT features - the legacy vertex welding and the
// legacy (off-by-one) edge sort - because those are what LTK Manager's ltk_mesh links, and the bake is
// verified byte-for-byte against it. The original notice follows and must stay with this file:
//
//   Copyright (C) 2011 by Morten S. Mikkelsen
//
//   This software is provided 'as-is', without any express or implied
//   warranty.  In no event will the authors be held liable for any damages
//   arising from the use of this software.
//
//   Permission is granted to anyone to use this software for any purpose,
//   including commercial applications, and to alter it and redistribute it
//   freely, subject to the following restrictions:
//
//   1. The origin of this software must not be misrepresented; you must not
//      claim that you wrote the original software. If you use this software
//      in a product, an acknowledgment in the product documentation would be
//      appreciated but is not required.
//   2. Altered source versions must be plainly marked as such, and must not be
//      misrepresented as being the original software.
//   3. This notice may not be removed or altered from any source distribution.
//
// Altered: C#, triangles only (quads are never passed by the only caller), recursion run on a large stack.
//
// Every float operation below is written in the order the Rust performs it and no System.Numerics vector
// is used: a SIMD dot product may sum in another order, and one ulp of difference in one vertex changes
// how vertices weld and group, which changes the output of the whole mesh.

namespace ReyEngine.Formats.Meshes;

public static class MikkTSpace
{
    /// <summary>One corner's tangent: the unit tangent and whether the UV mapping preserves orientation
    /// (MikkTSpace's sign is +1 when it does).</summary>
    public readonly record struct Tangent(float X, float Y, float Z, bool OrientationPreserving);

    /// <summary>
    /// Tangents for a triangle list. <paramref name="corners"/> holds three vertex indices per face;
    /// the result holds one entry per corner, null where MikkTSpace cannot supply one (a degenerate
    /// triangle with no healthy neighbour at that vertex). Normals should be unit length.
    /// </summary>
    public static Tangent?[] Generate(IReadOnlyList<int> corners, IReadOnlyList<(float X, float Y, float Z)> positions,
        IReadOnlyList<(float X, float Y, float Z)> normals, IReadOnlyList<(float U, float V)> uvs)
    {
        if (corners.Count % 3 != 0) throw new ArgumentException("corners is not a triangle list", nameof(corners));
        Tangent?[]? result = null;
        Exception? failure = null;
        // Grouping recurses once per connected face; a champion body is tens of thousands deep.
        var thread = new Thread(() =>
        {
            try { result = new Generator(corners, positions, normals, uvs).Run(); }
            catch (Exception ex) { failure = ex; }
        }, 512 * 1024 * 1024);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("MikkTSpace failed: " + failure.Message, failure);
        return result!;
    }

    // ------------------------------------------------------------------ math, in the Rust's order

    private const float MinPositive = 1.17549435e-38f;   // f32::MIN_POSITIVE (the smallest NORMAL float)

    private static float Fabs(float x) => float.IsNegative(x) ? -x : x;
    private static bool NotZero(float x) => Fabs(x) > MinPositive;

    private readonly record struct V3(float X, float Y, float Z)
    {
        public static readonly V3 Zero = new(0f, 0f, 0f);
        public float Dot(V3 r) => X * r.X + Y * r.Y + Z * r.Z;
        public static V3 operator +(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static V3 operator *(float s, V3 v) => new(s * v.X, s * v.Y, s * v.Z);
        public float LengthSquared() => Dot(this);
        public float Length() => MathF.Sqrt(LengthSquared());
        public V3 NormalizedOrZero()
        {
            if (NotZero(X) || NotZero(Y) || NotZero(Z)) return (1f / Length()) * this;
            return this;
        }
        public float this[int i] => i == 0 ? X : i == 1 ? Y : Z;
        // Rust compares [f32; 3] with ==, which says -0 == 0 and NaN != NaN; a record's Equals would not
        public bool Same(V3 o) => X == o.X && Y == o.Y && Z == o.Z;
    }

    /// <summary>f32::min and f32::max: a NaN argument yields the other one.</summary>
    private static float RMin(float a, float b) => float.IsNaN(a) ? b : float.IsNaN(b) ? a : (b < a ? b : a);
    private static float RMax(float a, float b) => float.IsNaN(a) ? b : float.IsNaN(b) ? a : (b > a ? b : a);

    /// <summary>f32::acos, computed in f64 and narrowed, as bevy's StdOps does.</summary>
    private static float Acos(float x) => (float)Math.Acos(x);

    private struct RawSpace
    {
        public V3 S, T;
        public float SMag, TMag;
        public static readonly RawSpace Zero = new() { S = V3.Zero, T = V3.Zero, SMag = 0f, TMag = 0f };
    }

    private struct Tri
    {
        public int N0, N1, N2;          // face neighbours per edge, -1 none
        public int G0, G1, G2;          // assigned group per vertex, -1 none
        public RawSpace Tangent;
        public int Face;                // original face index
        public int Offset;              // tangent_spaces_offset
        public bool Degenerate, GroupWithAny, OrientationPreserving;

        public int Neighbor(int e) => e == 0 ? N0 : e == 1 ? N1 : N2;
        public void SetNeighbor(int e, int v) { if (e == 0) N0 = v; else if (e == 1) N1 = v; else N2 = v; }
        public int Group(int i) => i == 0 ? G0 : i == 1 ? G1 : G2;
        public void SetGroup(int i, int g) { if (i == 0) G0 = g; else if (i == 1) G1 = g; else G2 = g; }
    }

    private sealed class Group
    {
        public int Id;
        public readonly List<int> Faces = new();
        public long Representative;
        public bool OrientationPreserving;
    }

    private readonly record struct Edge(long I0, long I1, int F) : IComparable<Edge>
    {
        public int CompareTo(Edge o)
        {
            int c = I0.CompareTo(o.I0);
            if (c != 0) return c;
            c = I1.CompareTo(o.I1);
            return c != 0 ? c : F.CompareTo(o.F);
        }
    }

    private sealed class Generator(IReadOnlyList<int> corners, IReadOnlyList<(float X, float Y, float Z)> positions,
        IReadOnlyList<(float X, float Y, float Z)> normals, IReadOnlyList<(float U, float V)> uvs)
    {
        private static long FV(int face, int vertex) => ((long)face << 2) | (uint)(vertex & 3);
        private static int FaceOf(long fv) => (int)(fv >> 2);
        private static int VertOf(long fv) => (int)(fv & 3);

        private V3 Pos(int face, int v) { var p = positions[corners[face * 3 + v]]; return new V3(p.X, p.Y, p.Z); }
        private V3 Nrm(int face, int v) { var n = normals[corners[face * 3 + v]]; return new V3(n.X, n.Y, n.Z); }
        private (float U, float V) Tex(int face, int v) => uvs[corners[face * 3 + v]];
        private V3 Pos(long fv) => Pos(FaceOf(fv), VertOf(fv));
        private V3 Nrm(long fv) => Nrm(FaceOf(fv), VertOf(fv));
        private (float U, float V) Tex(long fv) => Tex(FaceOf(fv), VertOf(fv));

        public Tangent?[] Run()
        {
            int faces = corners.Count / 3;
            var infos = TriangleInfos(faces);
            // partition good first then degenerate, each by offset (a stable sort by (degenerate, offset))
            var good = infos.Where(t => !t.Degenerate).ToArray();
            var bad = infos.Where(t => t.Degenerate).ToArray();

            var verts = new long[(good.Length + bad.Length) * 3];
            int k = 0;
            foreach (var t in good) for (int i = 0; i < 3; i++) verts[k++] = FV(t.Face, i);
            foreach (var t in bad) for (int i = 0; i < 3; i++) verts[k++] = FV(t.Face, i);
            Weld(verts);

            var spaces = GenerateSpaces(good, bad, verts, -1f, faces * 3);
            var result = new Tangent?[faces * 3];
            for (int i = 0; i < result.Length; i++)
                if (spaces[i] is { } s)
                    result[i] = new Tangent(s.Value.S.X, s.Value.S.Y, s.Value.S.Z, s.Orientation);
            return result;
        }

        private Tri[] TriangleInfos(int faces)
        {
            var list = new Tri[faces];
            for (int f = 0; f < faces; f++)
            {
                var info = new Tri { N0 = -1, N1 = -1, N2 = -1, G0 = -1, G1 = -1, G2 = -1, Tangent = RawSpace.Zero, Face = f, Offset = f * 3 };
                V3 p0 = Pos(f, 0), p1 = Pos(f, 1), p2 = Pos(f, 2);
                if (p0.Same(p1) || p1.Same(p2) || p2.Same(p0)) info.Degenerate = true;
                if (!info.Degenerate)
                {
                    var tx0 = Tex(f, 0); var tx1 = Tex(f, 1); var tx2 = Tex(f, 2);
                    float d10 = tx1.U - tx0.U, d11 = tx1.V - tx0.V;   // d_tx[0]
                    float d20 = tx2.U - tx0.U, d21 = tx2.V - tx0.V;   // d_tx[1]
                    V3 dv0 = p1 - p0, dv1 = p2 - p0;
                    float signedAreaDouble = d10 * d21 - d11 * d20;
                    float areaDouble = Fabs(signedAreaDouble);
                    V3 s = (d21 * dv0) - (d11 * dv1);    // eq 18
                    V3 t = (-d20 * dv0) + (d10 * dv1);   // eq 19
                    info.GroupWithAny = true;             // assumed bad
                    if (signedAreaDouble > 0f) info.OrientationPreserving = true;
                    if (NotZero(areaDouble))
                    {
                        float sign = info.OrientationPreserving ? 1f : -1f;
                        info.Tangent = new RawSpace
                        {
                            S = sign * s.NormalizedOrZero(),
                            T = sign * t.NormalizedOrZero(),
                            SMag = s.Length() / areaDouble,
                            TMag = t.Length() / areaDouble,
                        };
                        if (NotZero(info.Tangent.SMag) && NotZero(info.Tangent.TMag)) info.GroupWithAny = false;
                    }
                }
                list[f] = info;
            }
            return list;
        }

        // ------------------------------------------------------------ legacy welding

        private struct TempVertex { public V3 Position; public int Original; public ushort Bucket; }

        private void Weld(long[] vertices)
        {
            if (vertices.Length == 0) return;
            V3 min = default, max = default;
            bool first = true;
            foreach (var fv in vertices)
            {
                var v = Pos(fv);
                if (first) { min = v; max = v; first = false; }
                min = new V3(RMin(min.X, v.X), RMin(min.Y, v.Y), RMin(min.Z, v.Z));
                max = new V3(RMax(max.X, v.X), RMax(max.Y, v.Y), RMax(max.Z, v.Z));
            }
            var d = max - min;
            int cMax = d.Y > d.X && d.Y > d.Z ? 1 : d.Z > d.X ? 2 : 0;
            var temps = new TempVertex[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                var p = Pos(vertices[i]);
                float t = (p[cMax] - min[cMax]) / d[cMax];
                float clamped = float.IsNaN(t) ? t : t < 0f ? 0f : t > 1f ? 1f : t;
                float scaled = 2048f * clamped;
                ushort group = float.IsNaN(scaled) ? (ushort)0 : scaled >= 65535f ? ushort.MaxValue : scaled <= 0f ? (ushort)0 : (ushort)scaled;
                if (group > 2047) group = 2047;
                temps[i] = new TempVertex { Position = p, Original = i, Bucket = group };
            }
            // sort_by_key is stable
            temps = temps.OrderBy(t => t.Bucket).ToArray();
            int start = 0;
            for (int i = 1; i <= temps.Length; i++)
            {
                if (i < temps.Length && temps[i].Bucket == temps[start].Bucket) continue;
                MergeFast(vertices, temps, start, i - start);
                start = i;
            }
        }

        private void MergeFast(long[] vertices, TempVertex[] buf, int off, int len)
        {
            if (len < 2) return;
            V3 min = buf[off].Position, max = buf[off].Position;
            for (int i = off; i < off + len; i++)
            {
                var v = buf[i].Position;
                min = new V3(RMin(min.X, v.X), RMin(min.Y, v.Y), RMin(min.Z, v.Z));
                max = new V3(RMax(max.X, v.X), RMax(max.Y, v.Y), RMax(max.Z, v.Z));
            }
            var d = max - min;
            int c = d.Y > d.X && d.Y > d.Z ? 1 : d.Z > d.X ? 2 : 0;
            float sep = 0.5f * (max[c] + min[c]);
            if (!float.IsFinite(sep)) return;   // all NaN
            if (!(min[c] < sep && sep < max[c]))
            {
                // complete the weld
                for (int l = 0; l < len; l++)
                {
                    var va = buf[off + l];
                    int i = va.Original;
                    long ia = vertices[i];
                    V3 na = Nrm(ia); var ta = Tex(ia);
                    for (int m = 0; m < l; m++)
                    {
                        var vb = buf[off + m];
                        int j = vb.Original;
                        long ib = vertices[j];
                        V3 nb = Nrm(ib); var tb = Tex(ib);
                        if (va.Position.Same(vb.Position) && na.Same(nb) && ta.U == tb.U && ta.V == tb.V)
                        {
                            vertices[i] = vertices[j];
                            break;
                        }
                    }
                }
                return;
            }
            // separate into vertices either side of the plane by swapping pairs
            int us = 0, ue = len;   // unsorted range, relative to off
            while (ue - us >= 2)
            {
                int a = -1;
                for (int i = us; i < ue; i++) if (buf[off + i].Position[c] >= sep) { a = i; break; }
                if (a >= 0) us = a + 1; else us = ue;
                int b = -1;
                for (int i = ue - 1; i >= us; i--) if (buf[off + i].Position[c] < sep) { b = i; break; }
                if (b >= 0) ue = b; else ue = us;
                if (a >= 0 && b >= 0)
                {
                    (buf[off + a], buf[off + b]) = (buf[off + b], buf[off + a]);
                    us = a + 1; ue = b;
                }
                else if (a < 0 && b >= 0) { ue = b + 1; }
                else if (a >= 0 && b < 0) { us = a; ue = a + 1; }
            }
            int partition = ue > us && buf[off + us].Position[c] < sep ? us + 1 : us;
            MergeFast(vertices, buf, off, partition);
            MergeFast(vertices, buf, off + partition, len - partition);
        }

        // ------------------------------------------------------------ neighbours

        private const uint SortSeed = 39871946;

        private static void QuickSortBy(Span<Edge> buf, Func<Edge, long> key, uint seed)
        {
            if (buf.Length <= 2)
            {
                if (buf.Length == 2 && key(buf[1]) < key(buf[0])) (buf[0], buf[1]) = (buf[1], buf[0]);
                return;
            }
            uint t = seed & 31;
            t = (seed << (int)t) | (t == 0 ? seed : seed >> (int)(32 - t));
            seed = unchecked(seed + t + 3);
            int pivotIndex = (int)(seed % (uint)buf.Length);
            long pivot = key(buf[pivotIndex]);
            int a = 0, b = buf.Length - 1;
            while (a <= b)
            {
                while (key(buf[a]) < pivot) a++;
                while (key(buf[b]) > pivot) b--;
                if (a <= b)
                {
                    (buf[a], buf[b]) = (buf[b], buf[a]);
                    a++;
                    b = b > 0 ? b - 1 : 0;
                }
            }
            QuickSortBy(buf[..(b + 1)], key, seed);
            QuickSortBy(buf[a..], key, seed);
        }

        /// <summary>The C implementation's edge sort, off-by-one included: correct except within the
        /// run of the largest i0, whose i1 runs (all but the last) are sorted by face only.</summary>
        private static void SortEdges(Edge[] edges)
        {
            QuickSortBy(edges, e => e.I0, SortSeed);
            if (edges.Length == 0) return;
            int lastStart = edges.Length - 1;
            while (lastStart > 0 && edges[lastStart - 1].I0 == edges[^1].I0) lastStart--;
            Array.Sort(edges, 0, lastStart);   // a total order on unique-or-identical edges: any sort agrees
            var runs = new List<(int Start, int Length)>();
            int s = lastStart;
            for (int i = lastStart + 1; i <= edges.Length; i++)
            {
                if (i < edges.Length && edges[i].I1 == edges[i - 1].I1) continue;
                runs.Add((s, i - s));
                s = i;
            }
            for (int r = runs.Count - 2; r >= 0; r--)   // rev().skip(1): every run but the last
                QuickSortBy(edges.AsSpan(runs[r].Start, runs[r].Length), e => e.F, SortSeed);
        }

        private static (int N, long A, long B) GetEdge(long[] v, int f, long i0, long i1)
        {
            long lo = Math.Min(i0, i1), hi = Math.Max(i0, i1);
            for (int n = 0; n < 3; n++)
            {
                long a = v[f * 3 + n], b = v[f * 3 + (n + 1) % 3];
                if (Math.Min(a, b) == lo && Math.Max(a, b) == hi) return (n, a, b);
            }
            throw new InvalidOperationException("edge not on its face");
        }

        private static void BuildNeighbors(Tri[] tris, long[] v)
        {
            var edges = new Edge[tris.Length * 3];
            for (int f = 0; f < tris.Length; f++)
                for (int n = 0; n < 3; n++)
                {
                    long a = v[f * 3 + n], b = v[f * 3 + (n + 1) % 3];
                    edges[f * 3 + n] = new Edge(Math.Min(a, b), Math.Max(a, b), f);
                }
            SortEdges(edges);
            for (int index = 0; index < edges.Length; index++)
            {
                var ea = edges[index];
                var (na, i0a, i1a) = GetEdge(v, ea.F, ea.I0, ea.I1);
                if (tris[ea.F].Neighbor(na) >= 0) continue;
                for (int j = index + 1; j < edges.Length; j++)
                {
                    var eb = edges[j];
                    if (eb.I0 != ea.I0 || eb.I1 != ea.I1) break;
                    var (nb, x, y) = GetEdge(v, eb.F, eb.I0, eb.I1);
                    if (i0a == y && i1a == x && tris[eb.F].Neighbor(nb) < 0)
                    {
                        tris[ea.F].SetNeighbor(na, eb.F);
                        tris[eb.F].SetNeighbor(nb, ea.F);
                        break;
                    }
                }
            }
        }

        // ------------------------------------------------------------ groups

        private static List<Group> BuildGroups(Tri[] tris, long[] v)
        {
            var groups = new List<Group>();
            for (int f = 0; f < tris.Length; f++)
                for (int i = 0; i < 3; i++)
                {
                    if (tris[f].GroupWithAny || tris[f].Group(i) >= 0) continue;
                    var g = new Group { Id = groups.Count, Representative = v[f * 3 + i], OrientationPreserving = tris[f].OrientationPreserving };
                    g.Faces.Add(f);
                    tris[f].SetGroup(i, g.Id);
                    int e1 = (i + 2) % 3, e2 = i;
                    int n1 = tris[f].Neighbor(e1), n2 = tris[f].Neighbor(e2);
                    if (n1 >= 0) Assign(v, tris, n1, g);
                    if (n2 >= 0) Assign(v, tris, n2, g);
                    groups.Add(g);
                }
            return groups;
        }

        private static bool Assign(long[] v, Tri[] tris, int tri, Group g)
        {
            int i = -1;
            for (int n = 0; n < 3; n++) if (v[tri * 3 + n] == g.Representative) { i = n; break; }
            if (i < 0) throw new InvalidOperationException("representative not on its neighbour");
            if (tris[tri].Group(i) is var existing && existing >= 0) return existing == g.Id;
            if (tris[tri].GroupWithAny && tris[tri].G0 < 0 && tris[tri].G1 < 0 && tris[tri].G2 < 0)
                tris[tri].OrientationPreserving = g.OrientationPreserving;
            if (tris[tri].OrientationPreserving != g.OrientationPreserving) return false;
            g.Faces.Add(tri);
            tris[tri].SetGroup(i, g.Id);
            int n1 = tris[tri].Neighbor((i + 2) % 3), n2 = tris[tri].Neighbor(i);
            if (n1 >= 0) Assign(v, tris, n1, g);
            if (n2 >= 0) Assign(v, tris, n2, g);
            return true;
        }

        // ------------------------------------------------------------ spaces

        private struct Space { public RawSpace Value; public bool Orientation; }

        private Space?[] GenerateSpaces(Tri[] good, Tri[] bad, long[] verts, float threshold, int total)
        {
            var goodVerts = verts.AsSpan(0, good.Length * 3).ToArray();
            BuildNeighbors(good, goodVerts);
            var groups = BuildGroups(good, goodVerts);
            var spaces = new Space?[total];

            var subSpaces = new List<RawSpace>();
            var subGroups = new List<int[]>();
            for (int gi = 0; gi < groups.Count; gi++)
            {
                var group = groups[gi];
                foreach (int f in group.Faces)
                {
                    var a = good[f];
                    int index = a.G0 == gi ? 0 : a.G1 == gi ? 1 : 2;
                    long vertexIndex = goodVerts[f * 3 + index];
                    V3 n = Nrm(vertexIndex);
                    V3 sA = (a.Tangent.S - (n.Dot(a.Tangent.S) * n)).NormalizedOrZero();
                    V3 tA = (a.Tangent.T - (n.Dot(a.Tangent.T) * n)).NormalizedOrZero();
                    var tmp = new List<int>();
                    foreach (int t in group.Faces)
                    {
                        var b = good[t];
                        V3 sB = (b.Tangent.S - (n.Dot(b.Tangent.S) * n)).NormalizedOrZero();
                        V3 tB = (b.Tangent.T - (n.Dot(b.Tangent.T) * n)).NormalizedOrZero();
                        bool meets = sA.Dot(sB) > threshold && tA.Dot(tB) > threshold;
                        bool any = a.GroupWithAny || b.GroupWithAny;
                        bool sameFace = a.Face == b.Face;
                        if (any || sameFace || meets) tmp.Add(t);
                    }
                    tmp.Sort();
                    var key = tmp.ToArray();
                    int l = subGroups.FindIndex(s => s.AsSpan().SequenceEqual(key));
                    if (l < 0)
                    {
                        l = subSpaces.Count;
                        subSpaces.Add(Evaluate(key, goodVerts, good, group.Representative));
                        subGroups.Add(key);
                    }
                    int outIndex = a.Offset + index;
                    // triangles only: a corner is written once, so no combine
                    spaces[outIndex] = new Space { Value = subSpaces[l], Orientation = group.OrientationPreserving };
                }
                subSpaces.Clear();
                subGroups.Clear();
            }

            // degenerate triangles borrow from a healthy triangle sharing the welded vertex
            for (int d = 0; d < bad.Length; d++)
                for (int i = 0; i < 3; i++)
                {
                    long av = verts[(good.Length + d) * 3 + i];
                    for (int b = 0; b < good.Length; b++)
                    {
                        int j = -1;
                        for (int q = 0; q < 3; q++) if (goodVerts[b * 3 + q] == av) { j = q; break; }
                        if (j < 0) continue;
                        spaces[bad[d].Offset + i] = spaces[good[b].Offset + j];
                        goto next;
                    }
                    next:;
                }
            return spaces;
        }

        private RawSpace Evaluate(int[] faces, long[] v, Tri[] tris, long representative)
        {
            float angleSum = 0f;
            var res = RawSpace.Zero;
            foreach (int f in faces)
            {
                var info = tris[f];
                if (info.GroupWithAny) continue;
                int i = v[f * 3] == representative ? 0 : v[f * 3 + 1] == representative ? 1 : 2;
                V3 n = Nrm(v[f * 3 + i]);
                V3 p0 = Pos(v[f * 3 + (i + 1) % 3]), p1 = Pos(v[f * 3 + i]), p2 = Pos(v[f * 3 + (i + 2) % 3]);
                V3 e0 = p0 - p1, e1 = p2 - p1;
                e0 = (e0 - (n.Dot(e0) * n)).NormalizedOrZero();
                e1 = (e1 - (n.Dot(e1) * n)).NormalizedOrZero();
                float cos = e0.Dot(e1);
                cos = float.IsNaN(cos) ? cos : cos < -1f ? -1f : cos > 1f ? 1f : cos;
                float angle = Acos(cos);
                V3 ts = angle * (info.Tangent.S - (n.Dot(info.Tangent.S) * n)).NormalizedOrZero();
                V3 tt = angle * (info.Tangent.T - (n.Dot(info.Tangent.T) * n)).NormalizedOrZero();
                res.S = res.S + ts;
                res.T = res.T + tt;
                res.SMag += angle * info.Tangent.SMag;
                res.TMag += angle * info.Tangent.TMag;
                angleSum += angle;
            }
            res.S = res.S.NormalizedOrZero();
            res.T = res.T.NormalizedOrZero();
            if (angleSum > 0f)
            {
                res.SMag /= angleSum;
                res.TMag /= angleSum;
            }
            return res;
        }
    }
}
