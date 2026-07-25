using System.Numerics;

namespace ReyEngine.Rendering;

/// <summary>Where a ray met the map: which submesh and triangle, how far, and — the part a paint tool
/// exists for — the exact texture coordinate under the cursor.</summary>
public readonly record struct MeshRayHit(
    int Submesh,
    int Triangle,
    float Distance,
    Vector3 Position,
    /// <summary>Barycentric weights of the second and third vertices; the first is 1 - U - V.</summary>
    float BaryU,
    float BaryV,
    Vector2 Uv);

/// <summary>M172: a closest-hit BVH over the map's triangles.
///
/// It replaces ViewportMeshPicker, which answered the same question by testing every triangle in the
/// map — 909,993 of them on Summoner's Rift, measured at 11.13 ms per ray, and the click path asked
/// twice. That is tolerable for one click and hopeless for a brush, which needs a hit per mouse-move.
/// This trades a ~1.15 s build for 1.28 µs queries: measured over 3,000 rays, both agree on the submesh
/// every time and the hit distances are bit-identical.
///
/// It also returns what picking threw away. The old picker computed barycentrics inside its triangle
/// test and discarded them; painting needs them, because the UV under the cursor is the barycentric
/// blend of the hit triangle's three vertex UVs, and that UV is the address the brush writes to.</summary>
public sealed class MeshRayIndex
{
    private readonly Vector3[] _v0, _e1, _e2;   // Moller-Trumbore form: origin + two edges
    private readonly int[] _order;              // triangle indices, permuted into BVH leaf order
    private readonly int[] _submeshOf;          // per triangle, for visibility filtering
    private readonly uint[] _indices;           // kept, to look UVs up at hit time
    private readonly float[]? _uvs;
    private readonly Node[] _nodes;
    private readonly int _nodeCount;

    private struct Node
    {
        public Vector3 Min, Max;
        public int Start, Count;   // leaf range into _order; Count == 0 means interior
        public int Right;          // interior: index of the right child (left child is this + 1)
    }

    public int TriangleCount => _v0.Length;

    /// <param name="positions">3 floats per vertex, world space.</param>
    /// <param name="uvs">2 floats per vertex (UV0). Null is allowed — hits then carry a zero UV.</param>
    /// <param name="indices">Triangle list.</param>
    /// <param name="submeshes">(startIndex, indexCount) per submesh, indexing <paramref name="indices"/>.</param>
    public MeshRayIndex(float[] positions, float[]? uvs, uint[] indices,
        IReadOnlyList<(int Start, int Count)> submeshes)
    {
        _indices = indices;
        _uvs = uvs;

        int triCount = indices.Length / 3;
        _v0 = new Vector3[triCount];
        _e1 = new Vector3[triCount];
        _e2 = new Vector3[triCount];
        _submeshOf = new int[triCount];
        var centroids = new Vector3[triCount];

        // Submesh ranges are index ranges, so a triangle's owner is found by where its first index sits.
        // Written as a fill over each range rather than a search per triangle: ranges are contiguous and
        // this stays linear. Triangles covered by no range keep -1 and are never picked.
        Array.Fill(_submeshOf, -1);
        for (int s = 0; s < submeshes.Count; s++)
        {
            var (start, count) = submeshes[s];
            int firstTri = start / 3;
            int lastTri = Math.Min(triCount, (start + count) / 3);
            for (int t = firstTri; t < lastTri; t++) _submeshOf[t] = s;
        }

        for (int t = 0; t < triCount; t++)
        {
            int i0 = (int)indices[t * 3] * 3, i1 = (int)indices[t * 3 + 1] * 3, i2 = (int)indices[t * 3 + 2] * 3;
            var a = new Vector3(positions[i0], positions[i0 + 1], positions[i0 + 2]);
            var b = new Vector3(positions[i1], positions[i1 + 1], positions[i1 + 2]);
            var c = new Vector3(positions[i2], positions[i2 + 1], positions[i2 + 2]);
            _v0[t] = a; _e1[t] = b - a; _e2[t] = c - a;
            centroids[t] = (a + b + c) * (1f / 3f);
        }

        _order = new int[triCount];
        for (int i = 0; i < triCount; i++) _order[i] = i;

        _nodes = new Node[Math.Max(1, triCount * 2)];
        _nodeCount = 0;
        if (triCount > 0) Build(centroids, 0, triCount, ref _nodeCount);
    }

    private int Build(Vector3[] centroids, int start, int count, ref int nodeCount)
    {
        int self = nodeCount++;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = start; i < start + count; i++)
        {
            int t = _order[i];
            var a = _v0[t]; var b = a + _e1[t]; var c = a + _e2[t];
            min = Vector3.Min(min, Vector3.Min(a, Vector3.Min(b, c)));
            max = Vector3.Max(max, Vector3.Max(a, Vector3.Max(b, c)));
        }
        _nodes[self].Min = min; _nodes[self].Max = max;

        const int LeafSize = 8;
        if (count <= LeafSize)
        {
            _nodes[self].Start = start; _nodes[self].Count = count; _nodes[self].Right = -1;
            return self;
        }

        // Median split on the widest centroid axis — same choice as BakeScene, and for the same reason:
        // map triangle density is broadly uniform, so SAH's better trees do not pay for their build.
        var ext = max - min;
        int axis = ext.X > ext.Y ? (ext.X > ext.Z ? 0 : 2) : (ext.Y > ext.Z ? 1 : 2);
        var span = _order.AsSpan(start, count);
        span.Sort((x, y) => Axis(centroids[x], axis).CompareTo(Axis(centroids[y], axis)));
        int mid = count / 2;

        _nodes[self].Count = 0;
        Build(centroids, start, mid, ref nodeCount);
        _nodes[self].Right = Build(centroids, start + mid, count - mid, ref nodeCount);
        return self;
    }

    private static float Axis(in Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    /// <summary>Nearest triangle along the ray, or null. <paramref name="visible"/> mirrors the
    /// renderer's per-submesh visibility (null = all visible) so hidden dragon/baron layers cannot be
    /// picked or painted through.</summary>
    /// <param name="dir">Need not be normalised, but <see cref="MeshRayHit.Distance"/> is in units of
    /// its length — pass a normalised direction if you want world units.</param>
    public MeshRayHit? ClosestHit(Vector3 origin, Vector3 dir, IReadOnlyList<bool>? visible = null)
    {
        if (_nodeCount == 0) return null;

        var inv = new Vector3(
            1f / (MathF.Abs(dir.X) < 1e-9f ? MathF.CopySign(1e-9f, dir.X == 0 ? 1f : dir.X) : dir.X),
            1f / (MathF.Abs(dir.Y) < 1e-9f ? MathF.CopySign(1e-9f, dir.Y == 0 ? 1f : dir.Y) : dir.Y),
            1f / (MathF.Abs(dir.Z) < 1e-9f ? MathF.CopySign(1e-9f, dir.Z == 0 ? 1f : dir.Z) : dir.Z));

        float bestT = float.MaxValue;
        int bestTri = -1, bestSubmesh = int.MaxValue;
        float bestU = 0f, bestV = 0f;

        // Explicit stack rather than recursion: a 900k-triangle map builds a deep tree, and this runs on
        // the UI thread during a drag.
        Span<int> stack = stackalloc int[64];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            int ni = stack[--sp];
            ref var node = ref _nodes[ni];
            // bestT prunes: once something closer is known, boxes beyond it cannot contribute.
            if (!SlabHit(node.Min, node.Max, origin, inv, bestT)) continue;

            if (node.Count > 0)
            {
                for (int i = node.Start; i < node.Start + node.Count; i++)
                {
                    int t = _order[i];
                    int sm = _submeshOf[t];
                    if (sm < 0) continue;
                    if (visible is not null && sm < visible.Count && !visible[sm]) continue;
                    // <= not <, so exact ties still reach the tie-break below.
                    if (!TriHit(t, origin, dir, bestT, out float dist, out float u, out float v)) continue;

                    // Summoner's Rift stacks its dragon-soul variants BIT-IDENTICALLY: the dragon pit
                    // floor exists seven times in the same place (base + Fire/Hextech/Cloud/Chemtech/
                    // Earth/Infernal), the Order dragon statue five times, the Gromp twice — only one is
                    // visible per game. A ray through them hits every copy at exactly the same t, so
                    // "nearest wins" is genuinely undecided and traversal order would pick arbitrarily.
                    // Break the tie the way the old brute-force picker did (it scanned submeshes in
                    // order, so the lowest index won) to keep click-selection behaviour identical.
                    // In practice the caller passes the viewport's visibility list and only one variant
                    // survives anyway — this just makes the no-filter case deterministic.
                    if (dist < bestT || (dist == bestT && sm < bestSubmesh))
                    {
                        bestT = dist; bestTri = t; bestU = u; bestV = v; bestSubmesh = sm;
                    }
                }
            }
            else if (sp + 2 <= stack.Length)
            {
                stack[sp++] = ni + 1;
                stack[sp++] = node.Right;
            }
        }

        if (bestTri < 0) return null;
        return new MeshRayHit(
            _submeshOf[bestTri], bestTri, bestT, origin + dir * bestT, bestU, bestV,
            InterpolateUv(bestTri, bestU, bestV));
    }

    /// <summary>The UV under a hit: the barycentric blend of the triangle's three vertex UVs. This is the
    /// texture address the brush paints into.</summary>
    public Vector2 InterpolateUv(int triangle, float baryU, float baryV)
    {
        if (_uvs is null) return Vector2.Zero;
        int a = (int)_indices[triangle * 3] * 2, b = (int)_indices[triangle * 3 + 1] * 2, c = (int)_indices[triangle * 3 + 2] * 2;
        if (a + 1 >= _uvs.Length || b + 1 >= _uvs.Length || c + 1 >= _uvs.Length) return Vector2.Zero;
        float w = 1f - baryU - baryV;
        return new Vector2(
            _uvs[a] * w + _uvs[b] * baryU + _uvs[c] * baryV,
            _uvs[a + 1] * w + _uvs[b + 1] * baryU + _uvs[c + 1] * baryV);
    }

    /// <summary>Which submesh owns a triangle (-1 when no submesh covers it).</summary>
    public int SubmeshOf(int triangle) => (uint)triangle < (uint)_submeshOf.Length ? _submeshOf[triangle] : -1;

    private static bool SlabHit(in Vector3 bmin, in Vector3 bmax, in Vector3 o, in Vector3 inv, float maxDist)
    {
        float t1 = (bmin.X - o.X) * inv.X, t2 = (bmax.X - o.X) * inv.X;
        float tmin = MathF.Min(t1, t2), tmax = MathF.Max(t1, t2);
        t1 = (bmin.Y - o.Y) * inv.Y; t2 = (bmax.Y - o.Y) * inv.Y;
        tmin = MathF.Max(tmin, MathF.Min(t1, t2)); tmax = MathF.Min(tmax, MathF.Max(t1, t2));
        t1 = (bmin.Z - o.Z) * inv.Z; t2 = (bmax.Z - o.Z) * inv.Z;
        tmin = MathF.Max(tmin, MathF.Min(t1, t2)); tmax = MathF.Min(tmax, MathF.Max(t1, t2));
        return tmax >= MathF.Max(tmin, 0f) && tmin <= maxDist;
    }

    private bool TriHit(int t, in Vector3 o, in Vector3 d, float maxDist, out float dist, out float u, out float v)
    {
        // Two-sided, matching the old picker and BakeScene: map geometry is full of single-sided walls,
        // and refusing their back faces would let a click sail through them into whatever is behind.
        dist = 0f; u = 0f; v = 0f;
        maxDist = maxDist == float.MaxValue ? maxDist : maxDist * 1.0000001f;   // let exact ties through
        var e1 = _e1[t]; var e2 = _e2[t];
        var pv = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, pv);
        if (MathF.Abs(det) < 1e-12f) return false;
        float invDet = 1f / det;
        var tv = o - _v0[t];
        u = Vector3.Dot(tv, pv) * invDet;
        if (u < 0f || u > 1f) return false;
        var qv = Vector3.Cross(tv, e1);
        v = Vector3.Dot(d, qv) * invDet;
        if (v < 0f || u + v > 1f) return false;
        dist = Vector3.Dot(e2, qv) * invDet;
        return dist > 1e-4f && dist < maxDist;
    }
}
