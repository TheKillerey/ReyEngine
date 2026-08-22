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
    /// One plane for the patch handed in, carrying its texture exactly ONCE.
    ///
    /// <para>The caller must scope this to a single decal patch. Handed a whole material it averages
    /// unrelated patches together - see <see cref="MaxSizeRatio"/>, which is what makes that return
    /// nothing rather than a plausible plane in the wrong place.</para>
    /// </summary>
    /// <param name="lift">World units to raise the plane along its normal, clear of the ground it sits on.</param>
    public static DecalQuad? GeneratePlane(IReadOnlyList<DecalSourceTriangle> triangles, float lift)
    {
        var group = new List<DecalSourceTriangle>(triangles.Count);
        var uvLo = new Vector2(float.MaxValue);
        var uvHi = new Vector2(float.MinValue);
        foreach (var t in triangles)
        {
            if (!float.IsFinite(t.T0.X) || !float.IsFinite(t.T0.Y)
                || !float.IsFinite(t.T1.X) || !float.IsFinite(t.T1.Y)
                || !float.IsFinite(t.T2.X) || !float.IsFinite(t.T2.Y)) continue;
            group.Add(t);
            uvLo = Vector2.Min(uvLo, Vector2.Min(t.T0, Vector2.Min(t.T1, t.T2)));
            uvHi = Vector2.Max(uvHi, Vector2.Max(t.T0, Vector2.Max(t.T1, t.T2)));
        }
        if (group.Count * 3 < MinimumSamples) return null;
        if (uvHi.X - uvLo.X <= 1e-4f || uvHi.Y - uvLo.Y <= 1e-4f) return null;
        if (!TryFitAffine(group, out Vector3 origin, out Vector3 du, out Vector3 dv)) return null;

        // The patch's OWN UV extent, mapped back through the fit. Not the integer tile grid: a patch
        // typically spans about 2.2 tiles, so one plane per tile drew the image two or three times over
        // the same decal - "double pasted meshes ... repeated images". Its whole extent becomes one 0..1.
        Vector3 At(float u, float v) => origin + du * u + dv * v;
        Vector3 a = At(uvLo.X, uvLo.Y), b = At(uvHi.X, uvLo.Y);
        Vector3 c = At(uvHi.X, uvHi.Y), d = At(uvLo.X, uvHi.Y);

        Vector3 normal = Vector3.Cross(b - a, d - a);
        if (normal.LengthSquared() <= 1e-12f) return null;
        normal = Vector3.Normalize(normal);

        // Match the source's facing. A decal lies on the ground, so its normal points up; a fit that came
        // out inverted would make the plane invisible under backface culling.
        Vector3 sourceNormal = Vector3.Zero;
        foreach (var t in group) sourceNormal += Vector3.Cross(t.P1 - t.P0, t.P2 - t.P0);
        if (Vector3.Dot(normal, sourceNormal) < 0f) { (b, d) = (d, b); normal = -normal; }

        // Reject a fit whose plane and source are wildly different sizes, in either direction.
        double diagonal = Vector3.Distance(a, c);
        double source = SourceDiagonal(group);
        if (diagonal <= 1e-3 || source <= 1e-3) return null;
        if (diagonal > source * MaxSizeRatio || source > diagonal * MaxSizeRatio) return null;

        Vector3 offset = normal * lift;
        return new DecalQuad(a + offset, b + offset, c + offset, d + offset, normal,
            (int)MathF.Floor(uvLo.X), (int)MathF.Floor(uvLo.Y), group.Count);
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
