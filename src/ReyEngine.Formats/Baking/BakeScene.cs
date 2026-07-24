using System.Numerics;

namespace ReyEngine.Formats.Baking;

/// <summary>M158: the occluder set a bake traces against — every triangle of the map flattened into a
/// BVH. Built once per bake and shared by every atlas, so the cost is paid a single time even when a
/// map needs 85 atlases (crepe).</summary>
public sealed class BakeScene
{
    private readonly Vector3[] _v0, _e1, _e2;      // Moller-Trumbore form: origin + two edges
    private readonly int[] _order;                 // triangle indices, permuted into BVH leaf order
    private readonly Node[] _nodes;
    private readonly int _nodeCount;

    private struct Node
    {
        public Vector3 Min, Max;
        public int Start, Count;   // leaf range into _order; Count == 0 means interior
        public int Right;          // interior: index of the right child (left child is this + 1)
    }

    public int TriangleCount => _v0.Length;
    public Vector3 BoundsMin { get; }
    public Vector3 BoundsMax { get; }

    /// <param name="positions">3 floats per vertex, world space.</param>
    /// <param name="indices">Triangle list.</param>
    public BakeScene(float[] positions, uint[] indices)
    {
        int triCount = indices.Length / 3;
        _v0 = new Vector3[triCount];
        _e1 = new Vector3[triCount];
        _e2 = new Vector3[triCount];
        var centroids = new Vector3[triCount];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        for (int t = 0; t < triCount; t++)
        {
            int i0 = (int)indices[t * 3] * 3, i1 = (int)indices[t * 3 + 1] * 3, i2 = (int)indices[t * 3 + 2] * 3;
            var a = new Vector3(positions[i0], positions[i0 + 1], positions[i0 + 2]);
            var b = new Vector3(positions[i1], positions[i1 + 1], positions[i1 + 2]);
            var c = new Vector3(positions[i2], positions[i2 + 1], positions[i2 + 2]);
            _v0[t] = a; _e1[t] = b - a; _e2[t] = c - a;
            centroids[t] = (a + b + c) * (1f / 3f);
            min = Vector3.Min(min, Vector3.Min(a, Vector3.Min(b, c)));
            max = Vector3.Max(max, Vector3.Max(a, Vector3.Max(b, c)));
        }
        BoundsMin = triCount > 0 ? min : Vector3.Zero;
        BoundsMax = triCount > 0 ? max : Vector3.Zero;

        _order = new int[triCount];
        for (int i = 0; i < triCount; i++) _order[i] = i;

        // 2 nodes per triangle is the worst case for a binary BVH with >=1 triangle per leaf.
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

        // Median split on the widest centroid axis. Cheaper to build than SAH and, for map geometry
        // (broadly uniform triangle density), close enough in trace cost.
        var ext = max - min;
        int axis = ext.X > ext.Y ? (ext.X > ext.Z ? 0 : 2) : (ext.Y > ext.Z ? 1 : 2);
        var span = _order.AsSpan(start, count);
        span.Sort((x, y) => Axis(centroids[x], axis).CompareTo(Axis(centroids[y], axis)));
        int mid = count / 2;

        _nodes[self].Count = 0;
        Build(centroids, start, mid, ref nodeCount);           // left child is always self + 1
        _nodes[self].Right = Build(centroids, start + mid, count - mid, ref nodeCount);
        return self;
    }

    private static float Axis(in Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    /// <summary>Any-hit query: is anything blocking the segment from <paramref name="origin"/> along
    /// <paramref name="dir"/> for <paramref name="maxDist"/>? Returns on the FIRST hit — a shadow ray
    /// does not care which occluder it found, only that one exists.</summary>
    public bool Occluded(Vector3 origin, Vector3 dir, float maxDist)
    {
        if (_nodeCount == 0) return false;
        var inv = new Vector3(
            1f / (MathF.Abs(dir.X) < 1e-9f ? MathF.CopySign(1e-9f, dir.X == 0 ? 1f : dir.X) : dir.X),
            1f / (MathF.Abs(dir.Y) < 1e-9f ? MathF.CopySign(1e-9f, dir.Y == 0 ? 1f : dir.Y) : dir.Y),
            1f / (MathF.Abs(dir.Z) < 1e-9f ? MathF.CopySign(1e-9f, dir.Z == 0 ? 1f : dir.Z) : dir.Z));

        // Explicit stack: the trace runs on many threads and recursion here would blow the stack on
        // deep trees for a 1.3M-triangle map.
        Span<int> stack = stackalloc int[64];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            int ni = stack[--sp];
            ref var node = ref _nodes[ni];
            if (!SlabHit(node.Min, node.Max, origin, inv, maxDist)) continue;
            if (node.Count > 0)
            {
                for (int i = node.Start; i < node.Start + node.Count; i++)
                    if (TriHit(_order[i], origin, dir, maxDist)) return true;
            }
            else if (sp + 2 <= stack.Length)
            {
                stack[sp++] = ni + 1;
                stack[sp++] = node.Right;
            }
        }
        return false;
    }

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

    private bool TriHit(int t, in Vector3 o, in Vector3 d, float maxDist)
    {
        // Moller-Trumbore, two-sided: map geometry is full of single-sided walls whose back faces still
        // occlude, so culling by winding here would leak light through them.
        var e1 = _e1[t]; var e2 = _e2[t];
        var pv = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, pv);
        if (MathF.Abs(det) < 1e-12f) return false;
        float invDet = 1f / det;
        var tv = o - _v0[t];
        float u = Vector3.Dot(tv, pv) * invDet;
        if (u < 0f || u > 1f) return false;
        var qv = Vector3.Cross(tv, e1);
        float v = Vector3.Dot(d, qv) * invDet;
        if (v < 0f || u + v > 1f) return false;
        float dist = Vector3.Dot(e2, qv) * invDet;
        return dist > 1e-4f && dist < maxDist;
    }
}
