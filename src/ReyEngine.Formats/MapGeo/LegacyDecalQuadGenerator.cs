using System.Numerics;

namespace ReyEngine.Formats.MapGeo;

/// <summary>One source triangle, as the pairing of world position and diffuse UV the fit needs.</summary>
public readonly record struct DecalSourceTriangle(
    Vector3 P0, Vector3 P1, Vector3 P2, Vector2 T0, Vector2 T1, Vector2 T2);

/// <summary>
/// A generated decal plane: four corners in winding order, carrying UV 0..1 across the quad.
/// </summary>
/// <param name="TileU">The UV tile this quad reproduces, kept so the caller can name the mesh.</param>
public readonly record struct DecalQuad(
    Vector3 A, Vector3 B, Vector3 C, Vector3 D, Vector3 Normal, int TileU, int TileV, int SourceTriangles);

/// <summary>
/// M550: rebuild legacy decals as flat planes carrying one whole texture each.
///
/// <para>M549 established that a ported legacy decal is not a stamp. It is an irregular patch of TERRAIN
/// - odd triangle counts, following the ground - carrying a UV field that tiles roughly twice across it,
/// and welding by position collapses a material's 1,036 patches into 90 continuous sheets covering up to
/// 82% of the map. There is no boundary in that geometry where one "full texture" ends, so no way to
/// SPLIT it into meshes that each hold a whole image.</para>
///
/// <para>What the geometry does have is a UV field, and the texture repeats once per integer UV tile.
/// That is the unit: one quad per occupied tile, its corners placed by mapping the tile's UV square back
/// into world space, its own UVs running a clean 0..1. Measured over the Map2 port that is 241 quads
/// against 1,036 patches, each 235-1,327 world units across.</para>
///
/// <para><b>This trades terrain conformance for a flat plane</b>, which is the point - a plane can be
/// selected and moved - but it means a quad over uneven ground no longer follows it. The caller lifts
/// them clear of the surface; anything steeper than that will clip.</para>
/// </summary>
public static class LegacyDecalQuadGenerator
{
    /// <summary>A tile needs three independent UV samples before an affine fit means anything.</summary>
    private const int MinimumSamples = 3;

    /// <summary>
    /// How much larger than the geometry it was fitted to a quad may be before the tile is rejected.
    ///
    /// <para>A quad is expected to be somewhat larger than its source: the tile is a whole texture and the
    /// patch under it often covers only part of one. What it must not be is UNBOUNDED - where a tile holds
    /// a few scattered triangles whose UVs do not describe a consistent mapping, least squares still
    /// returns an answer, and it is a plane at an angle through the ground.</para>
    ///
    /// <para><b>On Map2 this guard fires zero times</b>, and that is worth stating rather than implying
    /// otherwise: the largest quad the port produces is 12,422 units against a p50 of 1,028, and it is NOT
    /// an extrapolation artifact - its source triangles span the same distance. That is a seam whose UV
    /// stays inside one tile while the strip itself runs across the map, so the texture really is stretched
    /// over it and the quad reproduces that faithfully. The guard is here for the degenerate case, which
    /// this corpus happens not to contain.</para>
    ///
    /// <para>Measuring the fit RESIDUAL instead does not work, and the reason is worth keeping: these
    /// patches follow the TERRAIN, so their vertices genuinely do not lie on any plane. A residual test
    /// measures ground unevenness, which is precisely what flattening them is meant to discard - at a 15%
    /// tolerance it rejected 182 of the 241 tiles.</para>
    /// </summary>
    private const double MaxSizeRatio = 3.0;

    /// <summary>
    /// How much of a tile the source must actually cover before a plane is generated for it.
    ///
    /// <para>A patch's UV runs past its own edges, so it clips the corner of tiles it barely enters. A
    /// full-size plane there floats over ground the decal never touched. Comparing the source triangle
    /// area inside the tile against the tile's own area drops those.</para>
    /// </summary>
    private const double MinTileCoverage = 0.15;

    /// <summary>
    /// One quad per occupied UV tile. Tiles whose UV-to-world mapping is degenerate are skipped rather
    /// than guessed at, and reported through <paramref name="skippedTiles"/>.
    /// </summary>
    /// <param name="lift">World units to raise each quad along its normal, clear of the ground it sits on.</param>
    public static IReadOnlyList<DecalQuad> Generate(
        IReadOnlyList<DecalSourceTriangle> triangles, float lift, out int skippedTiles)
    {
        skippedTiles = 0;
        var byTile = new Dictionary<(int U, int V), List<DecalSourceTriangle>>();
        foreach (var t in triangles)
        {
            // The CENTROID picks the tile, so a triangle contributes to exactly one and none is dropped.
            Vector2 centre = (t.T0 + t.T1 + t.T2) / 3f;
            if (!float.IsFinite(centre.X) || !float.IsFinite(centre.Y)) continue;
            var key = ((int)MathF.Floor(centre.X), (int)MathF.Floor(centre.Y));
            if (!byTile.TryGetValue(key, out var list)) byTile[key] = list = new();
            list.Add(t);
        }

        var result = new List<DecalQuad>(byTile.Count);
        foreach (var (tile, group) in byTile.OrderBy(kv => kv.Key.U).ThenBy(kv => kv.Key.V))
        {
            if (group.Count * 3 < MinimumSamples) { skippedTiles++; continue; }
            if (!TryFitAffine(group, out Vector3 origin, out Vector3 du, out Vector3 dv))
            { skippedTiles++; continue; }

            // The tile's own UV square, mapped back through the fit. Corner order follows UV so the quad's
            // texture is upright: (0,0) (1,0) (1,1) (0,1).
            Vector3 At(float u, float v) => origin + du * (tile.U + u) + dv * (tile.V + v);
            Vector3 a = At(0, 0), b = At(1, 0), c = At(1, 1), d = At(0, 1);

            Vector3 normal = Vector3.Cross(b - a, d - a);
            if (normal.LengthSquared() <= 1e-12f) { skippedTiles++; continue; }
            normal = Vector3.Normalize(normal);

            // Match the source's facing. A decal lies on the ground, so its normal points up; a fit that
            // came out inverted would make the quad invisible under backface culling.
            Vector3 sourceNormal = Vector3.Zero;
            foreach (var t in group) sourceNormal += Vector3.Cross(t.P1 - t.P0, t.P2 - t.P0);
            if (Vector3.Dot(normal, sourceNormal) < 0f)
            { (b, d) = (d, b); normal = -normal; }

            // Reject a fit whose quad and source are wildly different sizes, in either direction.
            double diagonal = Vector3.Distance(a, c);
            double source = SourceDiagonal(group);
            if (diagonal <= 1e-3 || source <= 1e-3) { skippedTiles++; continue; }
            if (diagonal > source * MaxSizeRatio || source > diagonal * MaxSizeRatio)
            { skippedTiles++; continue; }

            // Reject a tile the source barely enters, which would be a plane floating over bare ground.
            double tileArea = Vector3.Cross(b - a, d - a).Length();
            if (tileArea <= 1e-6 || SourceArea(group) / tileArea < MinTileCoverage)
            { skippedTiles++; continue; }

            Vector3 offset = normal * lift;
            result.Add(new DecalQuad(a + offset, b + offset, c + offset, d + offset, normal,
                tile.U, tile.V, group.Count));
        }
        return result;
    }

    /// <summary>Total world area of the triangles a tile was fitted from.</summary>
    private static double SourceArea(List<DecalSourceTriangle> group)
    {
        double sum = 0;
        foreach (var t in group) sum += 0.5 * Vector3.Cross(t.P1 - t.P0, t.P2 - t.P0).Length();
        return sum;
    }

    /// <summary>Diagonal of the world bounding box of the triangles a tile was fitted from.</summary>
    private static double SourceDiagonal(List<DecalSourceTriangle> group)
    {
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        foreach (var t in group)
        {
            lo = Vector3.Min(lo, Vector3.Min(t.P0, Vector3.Min(t.P1, t.P2)));
            hi = Vector3.Max(hi, Vector3.Max(t.P0, Vector3.Max(t.P1, t.P2)));
        }
        return Vector3.Distance(lo, hi);
    }

    /// <summary>
    /// Least squares fit of world = origin + du*u + dv*v over every vertex in the tile.
    ///
    /// <para>Fitted per tile rather than once per material: the UV field is planar but the ground under it
    /// is not, so a local fit puts each quad at its own patch's height and slope instead of averaging the
    /// whole map into one plane.</para>
    /// </summary>
    private static bool TryFitAffine(List<DecalSourceTriangle> group,
        out Vector3 origin, out Vector3 du, out Vector3 dv)
    {
        origin = du = dv = Vector3.Zero;

        // Normal equations for [u v 1] -> world, accumulated once and solved per world axis.
        double suu = 0, suv = 0, su = 0, svv = 0, sv = 0, sn = 0;
        Vector3 swu = Vector3.Zero, swv = Vector3.Zero, sw = Vector3.Zero;
        void Add(Vector2 t, Vector3 p)
        {
            suu += (double)t.X * t.X; suv += (double)t.X * t.Y; su += t.X;
            svv += (double)t.Y * t.Y; sv += t.Y; sn += 1;
            swu += p * t.X; swv += p * t.Y; sw += p;
        }
        foreach (var t in group) { Add(t.T0, t.P0); Add(t.T1, t.P1); Add(t.T2, t.P2); }

        // 3x3 inverse by cofactors. The matrix is symmetric positive semi-definite; a small determinant
        // means the tile's UVs are collinear and no plane is recoverable from them.
        double m00 = suu, m01 = suv, m02 = su, m11 = svv, m12 = sv, m22 = sn;
        double c00 = m11 * m22 - m12 * m12;
        double c01 = m02 * m12 - m01 * m22;
        double c02 = m01 * m12 - m02 * m11;
        double det = m00 * c00 + m01 * c01 + m02 * c02;
        if (Math.Abs(det) < 1e-6) return false;

        double c11 = m00 * m22 - m02 * m02;
        double c12 = m02 * m01 - m00 * m12;
        double c22 = m00 * m11 - m01 * m01;
        double inv = 1.0 / det;

        Vector3 Solve(double r0, double r1, double r2) => new(
            (float)((c00 * r0 + c01 * r1 + c02 * r2) * inv),
            (float)((c01 * r0 + c11 * r1 + c12 * r2) * inv),
            (float)((c02 * r0 + c12 * r1 + c22 * r2) * inv));

        var x = Solve(swu.X, swv.X, sw.X);
        var y = Solve(swu.Y, swv.Y, sw.Y);
        var z = Solve(swu.Z, swv.Z, sw.Z);
        du     = new Vector3(x.X, y.X, z.X);
        dv     = new Vector3(x.Y, y.Y, z.Y);
        origin = new Vector3(x.Z, y.Z, z.Z);
        return du.LengthSquared() > 1e-12f && dv.LengthSquared() > 1e-12f;
    }
}
