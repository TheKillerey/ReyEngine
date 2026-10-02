using System.Numerics;

namespace ReyEngine.App.Services;

/// <summary>M807: the camera a map thumbnail is drawn from, in the D3D11 viewport's MIRRORED display space (world X negated,
/// which is how <c>Dx11ViewportSurface</c> hands its camera over with <c>PreviewSettings.MirrorX</c>).</summary>
public sealed record MapThumbnailFrame(Matrix4x4 View, Matrix4x4 Projection, Vector3 Eye, Vector3 Target,
    float Distance, float Near, float Far, Vector3 BoxMin, Vector3 BoxMax)
{
    /// <summary>The largest |NDC| over the framed box's eight corners - what the fit solved for (0.92 by design).</summary>
    public float Extent { get; init; }
}

/// <summary>
/// <para>M807: where the Content Browser's map thumbnail looks from.</para>
///
/// <para><b>What is framed.</b> Not the mapgeo's own bounds: those include the sky bowl, the cloud meshes and out-of-bounds
/// decor, which would shrink the playable map to a postage stamp - and not the bounds of the drawn slices either, which
/// measured on Map22's 159 boards fail the same way (a backstage floor or a sky bowl is one slice of a few triangles and
/// 50 000 units across). The box is where the drawn TRIANGLES are: their centroids, with the outermost 2% trimmed off each end
/// in X and in Z, so a huge low-poly floor or a far tree line - few triangles however far it reaches - cannot move it. Its
/// floor is the median centroid height, and it is as tall as a tenth of its longer side: room for towers and monsters.</para>
///
/// <para><b>The view.</b> The editor's usual board angle: from above and behind, direction (0, 1.5, -1) - a 56 degree pitch -
/// at a 40 degree field of view. The distance is solved so the box's eight corners fill 0.92 of the frame (NDC), after the
/// target has been slid so the box is centred on screen; perspective makes that non-linear, so it iterates.</para>
/// </summary>
public static class MapThumbnailCamera
{
    /// <summary>The offset from the target to the eye, before it is scaled by the distance.</summary>
    public static readonly Vector3 Direction = Vector3.Normalize(new Vector3(0f, 1.5f, -1f));
    public const float FovDegrees = 40f;
    /// <summary>How much of the frame (NDC, centre to edge) the framed box fills.</summary>
    public const float Fill = 0.92f;
    /// <summary>The share of the longer side the box is tall.</summary>
    public const float HeightShare = 0.10f;
    /// <summary>The share of triangles trimmed off each end of X and of Z.</summary>
    public const float Trim = 0.02f;
    private const float MinSide = 100f;

    /// <summary>The centroids of up to <paramref name="maxSamples"/> of the triangles in <paramref name="ranges"/> (index
    /// ranges into <paramref name="indices"/>, three indices a triangle) - every n-th triangle when there are more, so a
    /// map of millions costs the same as one of thousands. A triangle that names a vertex outside
    /// <paramref name="positions"/> (x, y, z a vertex) or has a non-finite corner is skipped.</summary>
    public static List<Vector3> SampleCentroids(float[] positions, uint[] indices, IEnumerable<(int Start, int Count)> ranges,
        int maxSamples = 120_000)
    {
        var spans = new List<(int Start, int Count)>();
        long total = 0;
        foreach (var (start, count) in ranges)
        {
            if (start < 0 || count < 3 || start >= indices.Length) continue;
            int usable = Math.Min(count, indices.Length - start) / 3 * 3;
            if (usable < 3) continue;
            spans.Add((start, usable));
            total += usable / 3;
        }
        var points = new List<Vector3>((int)Math.Min(total, Math.Max(maxSamples, 0)));
        if (total == 0 || maxSamples <= 0) return points;

        long stride = Math.Max(1, (total + maxSamples - 1) / maxSamples);
        long vertexCount = positions.Length / 3;
        long seen = 0;
        foreach (var (start, count) in spans)
        {
            for (int t = 0; t < count; t += 3, seen++)
            {
                if (seen % stride != 0) continue;
                uint a = indices[start + t], b = indices[start + t + 1], c = indices[start + t + 2];
                if (a >= vertexCount || b >= vertexCount || c >= vertexCount) continue;
                var sum = new Vector3(
                    positions[a * 3] + positions[b * 3] + positions[c * 3],
                    positions[a * 3 + 1] + positions[b * 3 + 1] + positions[c * 3 + 1],
                    positions[a * 3 + 2] + positions[b * 3 + 2] + positions[c * 3 + 2]);
                if (!Finite(sum)) continue;
                points.Add(sum / 3f);
            }
        }
        return points;
    }

    /// <summary>The world-space box worth framing, or false when there is nothing to frame.</summary>
    public static bool TryBox(IReadOnlyList<Vector3> points, out Vector3 min, out Vector3 max)
    {
        min = max = default;
        var xs = new List<float>(points.Count);
        var ys = new List<float>(points.Count);
        var zs = new List<float>(points.Count);
        foreach (var p in points)
        {
            if (!Finite(p)) continue;
            xs.Add(p.X); ys.Add(p.Y); zs.Add(p.Z);
        }
        if (xs.Count == 0) return false;

        float x0 = Percentile(xs, Trim), x1 = Percentile(xs, 1f - Trim);
        float z0 = Percentile(zs, Trim), z1 = Percentile(zs, 1f - Trim);
        float floor = Percentile(ys, 0.5f);

        // a map that is one spot, or a line, still gets a box with sides to look at
        if (x1 - x0 < MinSide) { float mid = (x0 + x1) * 0.5f; x0 = mid - MinSide * 0.5f; x1 = mid + MinSide * 0.5f; }
        if (z1 - z0 < MinSide) { float mid = (z0 + z1) * 0.5f; z0 = mid - MinSide * 0.5f; z1 = mid + MinSide * 0.5f; }
        float side = MathF.Max(x1 - x0, z1 - z0);

        min = new Vector3(x0, floor, z0);
        max = new Vector3(x1, floor + side * HeightShare, z1);
        return true;
    }

    /// <summary>Frame <paramref name="points"/> (triangle centroids, world space) for a frame of <paramref name="aspect"/>
    /// (width over height).</summary>
    public static bool TryFrame(IReadOnlyList<Vector3> points, float aspect, out MapThumbnailFrame frame)
    {
        frame = null!;
        if (!(aspect > 0f) || !TryBox(points, out var min, out var max)) return false;

        // world -> display: X is mirrored, so the box's X range is negated and swapped
        var lo = new Vector3(-max.X, min.Y, min.Z);
        var hi = new Vector3(-min.X, max.Y, max.Z);
        var corners = new Vector3[8];
        for (int i = 0; i < 8; i++)
            corners[i] = new Vector3((i & 1) == 0 ? lo.X : hi.X, (i & 2) == 0 ? lo.Y : hi.Y, (i & 4) == 0 ? lo.Z : hi.Z);

        var target = (lo + hi) * 0.5f;
        float side = MathF.Max(hi.X - lo.X, hi.Z - lo.Z);
        float distance = MathF.Max(side * 1.3f, 1f);
        float fov = FovDegrees * MathF.PI / 180f;
        float halfTan = MathF.Tan(fov * 0.5f);

        for (int pass = 0; pass < 8; pass++)
        {
            var (view, proj) = Matrices(target, distance, fov, aspect);
            if (!Extents(corners, view, proj, out float minX, out float maxX, out float minY, out float maxY))
            { distance *= 1.5f; continue; }   // a corner is behind the camera: back off

            // slide the target so the box sits in the middle of the frame (NDC -> world units at the target's depth)
            float cxNdc = (minX + maxX) * 0.5f, cyNdc = (minY + maxY) * 0.5f;
            // CreateLookAt's own axes: z = eye - target = Direction, x (screen right) = up x z, y (screen up) = z x x
            var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, Direction));
            var up = Vector3.Normalize(Vector3.Cross(Direction, right));
            target += right * (cxNdc * distance * halfTan * aspect) + up * (cyNdc * distance * halfTan);

            // then size it: NDC falls off as 1/distance, so scale by how far over (or under) the fill it is
            float half = MathF.Max((maxX - minX) * 0.5f, (maxY - minY) * 0.5f);
            if (half <= 1e-6f) break;
            distance *= half / Fill;
        }

        var (v, p) = Matrices(target, distance, fov, aspect);
        float extent = 0f;
        // never end with the box poking out of the frame: grow until it fits (bounded)
        for (int guard = 0; guard < 24; guard++)
        {
            if (Extents(corners, v, p, out float a, out float b, out float c, out float d))
            {
                extent = MathF.Max(MathF.Max(MathF.Abs(a), MathF.Abs(b)), MathF.Max(MathF.Abs(c), MathF.Abs(d)));
                if (extent <= 0.96f) break;
            }
            distance *= 1.04f;
            (v, p) = Matrices(target, distance, fov, aspect);
        }
        if (!(extent > 0f) || extent > 1f) return false;

        frame = new MapThumbnailFrame(v, p, target + Direction * distance, target, distance,
            Near(distance), Far(distance), min, max) { Extent = extent };
        return true;
    }

    public static float Near(float distance) => MathF.Max(1f, distance * 0.05f);
    public static float Far(float distance) => distance * 8f;

    private static (Matrix4x4 View, Matrix4x4 Projection) Matrices(Vector3 target, float distance, float fov, float aspect)
    {
        var eye = target + Direction * distance;
        return (Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY),
                Matrix4x4.CreatePerspectiveFieldOfView(fov, aspect, Near(distance), Far(distance)));
    }

    /// <summary>The corners' NDC extents under the MIRRORED view the renderer applies (<c>PreviewSettings.MirrorX</c> scales
    /// X by -1 ahead of the supplied view), so the corners given in display space are measured the way they are drawn.
    /// False when a corner is at or behind the eye.</summary>
    private static bool Extents(Vector3[] displayCorners, Matrix4x4 view, Matrix4x4 proj,
        out float minX, out float maxX, out float minY, out float maxY)
    {
        minX = minY = float.MaxValue; maxX = maxY = float.MinValue;
        var viewProj = view * proj;
        foreach (var c in displayCorners)
        {
            var clip = Vector4.Transform(new Vector4(c, 1f), viewProj);
            if (!(clip.W > 1e-4f)) return false;
            float x = clip.X / clip.W, y = clip.Y / clip.W;
            minX = MathF.Min(minX, x); maxX = MathF.Max(maxX, x);
            minY = MathF.Min(minY, y); maxY = MathF.Max(maxY, y);
        }
        return true;
    }

    /// <summary>The value at fraction <paramref name="p"/> (0..1) of <paramref name="values"/>, which this sorts.</summary>
    internal static float Percentile(List<float> values, float p)
    {
        values.Sort();
        int i = (int)Math.Clamp(Math.Round((values.Count - 1) * (double)p), 0, values.Count - 1);
        return values[i];
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
