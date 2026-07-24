using System.Numerics;

namespace ReyEngine.Formats.Baking;

/// <summary>The result of unwrapping one mesh / vertex buffer into lightmap UV space.</summary>
public sealed class UnwrapResult
{
    /// <summary>UV per OUTPUT vertex, already packed into [0,1] for this mesh.</summary>
    public required Vector2[] Uvs { get; init; }
    /// <summary>Output vertex i is a copy of source vertex <c>VertexRemap[i]</c>. Longer than the source
    /// list wherever a vertex sits on a chart boundary and had to be split.</summary>
    public required int[] VertexRemap { get; init; }
    /// <summary>Triangle indices rewritten against the output vertices.</summary>
    public required int[] Indices { get; init; }
    public int ChartCount { get; init; }
    /// <summary>Fraction of the packed [0,1] square actually covered by charts. Low = wasted atlas.</summary>
    public float PackEfficiency { get; init; }
    /// <summary>Triangles whose projected winding is opposite their chart's majority — i.e. the chart
    /// folded over itself and those UVs OVERLAP. Must be ~0; a non-zero count means the smoothing angle
    /// is too permissive for this geometry and light will bleed between unrelated surfaces.</summary>
    public int FoldedTriangles { get; init; }
}

/// <summary>M147: generates a second UV set (the lightmap channel) for geometry that has none.
///
/// Approach — chart-based planar unwrap, which is what map geometry wants: segment the mesh into
/// near-planar connected charts, project each onto its own average plane, then pack the charts into the
/// unit square. It is not a conformal/LSCM unwrapper; for architecture (largely flat faces meeting at
/// hard angles) planar projection per chart introduces almost no distortion and is far more predictable.
///
/// Two properties matter for League specifically:
///  - It runs in LOCAL space, on the vertex buffer. Lightmapped meshes are INSTANCES sharing one buffer,
///    so the unwrap is done ONCE and every instance reuses it; instances differ only by the per-mesh
///    BakedLight Scale/Bias that places them in different atlas regions. This is exactly the layout Riot
///    ships, and it is what keeps buffer sharing intact (see MapGeoBinary).
///  - A vertex on a chart seam needs a different UV per chart, so it is SPLIT. That is why the result
///    carries a VertexRemap and rewritten indices: the caller must rebuild the vertex buffers through
///    that remap, not just append a UV channel.</summary>
public static class LightmapUnwrapper
{
    /// <param name="positions">3 floats per vertex (local space).</param>
    /// <param name="indices">Triangle list into <paramref name="positions"/>.</param>
    /// <param name="smoothingAngleDegrees">Triangles join a chart while their normal stays within this
    /// angle of the chart's average. This is a CORRECTNESS limit, not just a quality knob: measured on
    /// Map11, area distortion is 2.4x at 40 deg but 69x at 60 deg and >1000x at 75+, because a chart that
    /// wraps around curvature FOLDS OVER itself under planar projection and its UVs overlap. 40 is the
    /// default for that reason.</param>
    /// <param name="gutterFraction">Padding between charts as a fraction of the unit square. Must be
    /// small: it is paid once PER CHART, so the 0.02 first tried here left only 4% of the square for
    /// actual charts; 0.004 yields 61% coverage on the same meshes.</param>
    public static UnwrapResult Unwrap(
        float[] positions, int[] indices,
        float smoothingAngleDegrees = 40f,
        float gutterFraction = 0.004f)
    {
        int triCount = indices.Length / 3;
        if (triCount == 0)
            return new UnwrapResult { Uvs = Array.Empty<Vector2>(), VertexRemap = Array.Empty<int>(), Indices = Array.Empty<int>() };

        var triNormal = new Vector3[triCount];
        var triArea = new float[triCount];
        for (int t = 0; t < triCount; t++)
        {
            var a = V(positions, indices[t * 3]);
            var b = V(positions, indices[t * 3 + 1]);
            var c = V(positions, indices[t * 3 + 2]);
            var n = Vector3.Cross(b - a, c - a);
            float len = n.Length();
            triArea[t] = len * 0.5f;
            triNormal[t] = len > 1e-9f ? n / len : Vector3.UnitY;
        }

        // Weld by POSITION before building adjacency. Map geometry splits vertices for normals/UV0, so
        // geometrically adjacent triangles usually do NOT share vertex indices — matching edges by index
        // finds almost no neighbours and shatters the mesh into thousands of ~4-triangle charts
        // (measured: 7980 charts on a 30k-vertex mesh). Welding fixes the adjacency at the source.
        var weld = BuildWeldMap(positions);
        var charts = BuildCharts(indices, triCount, triNormal, triArea, smoothingAngleDegrees, weld);

        // Project each chart to 2D and measure it in WORLD units, so every chart can later be scaled to
        // the same texel density regardless of how big it is.
        var chartUv = new List<Vector2[]>(charts.Count);      // per chart, per corner (3 per triangle)
        var chartSize = new List<Vector2>(charts.Count);
        foreach (var chart in charts)
        {
            var normal = Vector3.Zero;
            foreach (int t in chart) normal += triNormal[t] * MathF.Max(triArea[t], 1e-6f);
            normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY;
            BuildBasis(normal, out var tangent, out var bitangent);

            var uvs = new Vector2[chart.Count * 3];
            float minU = float.MaxValue, minV = float.MaxValue, maxU = float.MinValue, maxV = float.MinValue;
            for (int i = 0; i < chart.Count; i++)
            {
                int t = chart[i];
                for (int c = 0; c < 3; c++)
                {
                    var p = V(positions, indices[t * 3 + c]);
                    var uv = new Vector2(Vector3.Dot(p, tangent), Vector3.Dot(p, bitangent));
                    uvs[i * 3 + c] = uv;
                    minU = MathF.Min(minU, uv.X); maxU = MathF.Max(maxU, uv.X);
                    minV = MathF.Min(minV, uv.Y); maxV = MathF.Max(maxV, uv.Y);
                }
            }
            // Rebase to the chart's own origin; keep WORLD scale for now (packing normalises later).
            var origin = new Vector2(minU, minV);
            for (int i = 0; i < uvs.Length; i++) uvs[i] -= origin;
            chartUv.Add(uvs);
            chartSize.Add(new Vector2(MathF.Max(maxU - minU, 1e-4f), MathF.Max(maxV - minV, 1e-4f)));
        }

        float efficiency = PackCharts(chartSize, gutterFraction, out var chartOrigin, out float packScale);

        // Emit vertices. Every (chart, source vertex) pair becomes its own output vertex, which is what
        // splits seam vertices automatically.
        var remap = new List<int>(triCount * 3);
        var outUv = new List<Vector2>(triCount * 3);
        var outIndices = new int[triCount * 3];
        var seen = new Dictionary<(int chart, int vertex), int>();

        for (int ci = 0; ci < charts.Count; ci++)
        {
            var chart = charts[ci];
            var uvs = chartUv[ci];
            var origin = chartOrigin[ci];
            for (int i = 0; i < chart.Count; i++)
            {
                int t = chart[i];
                for (int c = 0; c < 3; c++)
                {
                    int srcVertex = indices[t * 3 + c];
                    var uv = uvs[i * 3 + c] * packScale + origin;
                    var key = (ci, srcVertex);
                    if (!seen.TryGetValue(key, out int outVertex))
                    {
                        outVertex = remap.Count;
                        seen[key] = outVertex;
                        remap.Add(srcVertex);
                        outUv.Add(uv);
                    }
                    outIndices[t * 3 + c] = outVertex;
                }
            }
        }

        // Fold-over check: within a chart every triangle should project with the same winding. Any that
        // do not are lying on top of their neighbours in UV space.
        int folded = 0;
        for (int ci = 0; ci < charts.Count; ci++)
        {
            var chart = charts[ci];
            int pos2 = 0, neg = 0;
            for (int i = 0; i < chart.Count; i++)
            {
                int t = chart[i];
                var a = outUv[outIndices[t * 3]]; var b = outUv[outIndices[t * 3 + 1]]; var c = outUv[outIndices[t * 3 + 2]];
                float cross = (b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y);
                if (cross > 0f) pos2++; else if (cross < 0f) neg++;
            }
            folded += Math.Min(pos2, neg);
        }

        return new UnwrapResult
        {
            Uvs = outUv.ToArray(),
            VertexRemap = remap.ToArray(),
            Indices = outIndices,
            ChartCount = charts.Count,
            PackEfficiency = efficiency,
            FoldedTriangles = folded,
        };
    }

    /// <summary>Region-grow connected triangles into near-planar charts.</summary>
    /// <summary>Vertex -> canonical id for coincident positions (1/16 unit grid).</summary>
    private static int[] BuildWeldMap(float[] positions)
    {
        int count = positions.Length / 3;
        var map = new int[count];
        var canonical = new Dictionary<(int, int, int), int>(count);
        for (int v = 0; v < count; v++)
        {
            var key = ((int)MathF.Round(positions[v * 3] * 16f),
                       (int)MathF.Round(positions[v * 3 + 1] * 16f),
                       (int)MathF.Round(positions[v * 3 + 2] * 16f));
            if (!canonical.TryGetValue(key, out int id)) canonical[key] = id = v;
            map[v] = id;
        }
        return map;
    }

    private static List<List<int>> BuildCharts(
        int[] indices, int triCount, Vector3[] triNormal, float[] triArea, float smoothingAngleDegrees,
        int[] weld)
    {
        // Adjacency across shared EDGES (by vertex index pair) — two triangles are neighbours only if they
        // share a full edge, so charts stay genuinely connected surfaces.
        var edgeOwner = new Dictionary<(int, int), int>(triCount * 3);
        var neighbours = new List<int>[triCount];
        for (int t = 0; t < triCount; t++) neighbours[t] = new List<int>(3);
        for (int t = 0; t < triCount; t++)
        {
            for (int c = 0; c < 3; c++)
            {
                int v0 = weld[indices[t * 3 + c]], v1 = weld[indices[t * 3 + (c + 1) % 3]];
                var key = v0 < v1 ? (v0, v1) : (v1, v0);
                if (edgeOwner.TryGetValue(key, out int other))
                {
                    neighbours[t].Add(other);
                    neighbours[other].Add(t);
                }
                else edgeOwner[key] = t;
            }
        }

        float cosLimit = MathF.Cos(Math.Clamp(smoothingAngleDegrees, 1f, 179f) * MathF.PI / 180f);
        var chartOf = new int[triCount];
        Array.Fill(chartOf, -1);
        var charts = new List<List<int>>();
        var queue = new Queue<int>();

        for (int seed = 0; seed < triCount; seed++)
        {
            if (chartOf[seed] >= 0) continue;
            int id = charts.Count;
            var chart = new List<int> { seed };
            charts.Add(chart);
            chartOf[seed] = id;

            var accum = triNormal[seed] * MathF.Max(triArea[seed], 1e-6f);
            queue.Clear();
            queue.Enqueue(seed);
            while (queue.Count > 0)
            {
                int t = queue.Dequeue();
                foreach (int n in neighbours[t])
                {
                    if (chartOf[n] >= 0) continue;
                    var avg = accum.LengthSquared() > 1e-12f ? Vector3.Normalize(accum) : triNormal[t];
                    if (Vector3.Dot(triNormal[n], avg) < cosLimit) continue;
                    chartOf[n] = id;
                    chart.Add(n);
                    accum += triNormal[n] * MathF.Max(triArea[n], 1e-6f);
                    queue.Enqueue(n);
                }
            }
        }
        return charts;
    }

    /// <summary>Shelf-pack the charts into the unit square at a uniform scale, so texel density is the
    /// same everywhere. Returns the coverage achieved and, per chart, its origin in [0,1].</summary>
    private static float PackCharts(
        List<Vector2> sizes, float gutterFraction, out Vector2[] origins, out float scale)
    {
        int n = sizes.Count;
        origins = new Vector2[n];
        if (n == 0) { scale = 1f; return 0f; }

        // Order tallest-first: the classic shelf heuristic, which keeps rows tight.
        var order = Enumerable.Range(0, n).OrderByDescending(i => sizes[i].Y).ToArray();
        float totalArea = sizes.Sum(s => s.X * s.Y);

        // Start from the area-optimal scale and shrink until everything fits. Bounded and cheap: each
        // attempt is a linear shelf pass, and the 0.92 factor converges in a handful of steps.
        float gutter = Math.Clamp(gutterFraction, 0f, 0.2f);
        float attempt = totalArea > 0f ? MathF.Sqrt(1f / totalArea) : 1f;
        for (int guard = 0; guard < 64; guard++)
        {
            if (TryShelfPack(sizes, order, attempt, gutter, origins)) break;
            attempt *= 0.92f;
        }
        scale = attempt;

        float covered = 0f;
        foreach (var s in sizes) covered += s.X * attempt * s.Y * attempt;
        return Math.Clamp(covered, 0f, 1f);
    }

    private static bool TryShelfPack(List<Vector2> sizes, int[] order, float scale, float gutter, Vector2[] origins)
    {
        float x = gutter, y = gutter, shelfHeight = 0f;
        foreach (int i in order)
        {
            float w = sizes[i].X * scale, h = sizes[i].Y * scale;
            if (w > 1f - 2f * gutter || h > 1f - 2f * gutter) return false;   // single chart too large
            if (x + w + gutter > 1f)
            {
                x = gutter;
                y += shelfHeight + gutter;
                shelfHeight = 0f;
            }
            if (y + h + gutter > 1f) return false;
            origins[i] = new Vector2(x, y);
            x += w + gutter;
            shelfHeight = MathF.Max(shelfHeight, h);
        }
        return true;
    }

    private static Vector3 V(float[] p, int i) => new(p[i * 3], p[i * 3 + 1], p[i * 3 + 2]);

    private static void BuildBasis(Vector3 n, out Vector3 tangent, out Vector3 bitangent)
    {
        var up = MathF.Abs(n.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
        tangent = Vector3.Normalize(Vector3.Cross(up, n));
        bitangent = Vector3.Cross(n, tangent);
    }
}
