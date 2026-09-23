using System.Numerics;

namespace ReyEngine.Formats.Vfx;

/// <summary>What an emitter's particles are born ON, when it names a surface rather than a point.</summary>
public enum VfxEmissionSurfaceKind
{
    /// <summary><c>emissionMeshName</c>: a static <c>.scb</c>/<c>.sco</c>. 6,582 of 1,581,956 shipped
    /// emitters, the commonest form by far.</summary>
    LegacyMesh,
    /// <summary><c>VfxEmissionMeshData</c> inside <c>emissionSurfaceDefinition</c>: a <c>.skn</c> with its
    /// <c>.skl</c>, optionally an animation and a submesh list. About 130 emitters.</summary>
    Mesh,
    /// <summary><c>VfxEmissionSkeletonData</c>: the joints of a <c>.skl</c>, filtered by a joint mask.
    /// 10 emitters, all Shyvana's dragon body flames.</summary>
    Skeleton,
    /// <summary>An <c>emissionSurfaceDefinition</c> that names no surface - empty, or holding the one
    /// unnamed surface class <c>0x526478f0</c> with no fields. About 1,190 emitters (Akali W, Aatrox E, the
    /// ward pads). They name no mesh, so what they are born on can only be the character the effect is
    /// attached to: the preview needs a character HOST to show them.</summary>
    Host,
}

/// <summary>
/// M754: an emitter's emission surface, as the file states it.
///
/// <para>Measured over 360 WADs and 1,581,956 emitters. <c>EmissionSource</c> and <c>useAvatarPose</c> are
/// never authored, so they are not modelled. 105 emitters carry BOTH a legacy <c>emissionMeshName</c> and an
/// empty surface definition; the named mesh wins, because it is the only one of the two that names
/// anything.</para>
///
/// <para><b>The normal flag is read and NOT applied.</b> <c>useEmissionMeshNormalForBirth</c> (default
/// true) and <c>useSurfaceNormalForBirthPhysics</c> presumably turn birth velocity to the surface normal, but
/// how is unmeasured - the mesh emitters that use it mostly author no birth velocity at all (253 of 747 in
/// 80 champion WADs), and the rest point every which way. Particles are born on the surface; their velocity
/// is what the file says, unturned.</para>
/// </summary>
/// <param name="Kind">Which form the file uses.</param>
/// <param name="MeshPath">The mesh file, for <see cref="VfxEmissionSurfaceKind.LegacyMesh"/> and
/// <see cref="VfxEmissionSurfaceKind.Mesh"/>.</param>
/// <param name="SkeletonPath">The <c>.skl</c> for Mesh and Skeleton surfaces.</param>
/// <param name="AnimationName">A Mesh surface's animation, when it names one.</param>
/// <param name="Submeshes">Hashes of the submeshes a Mesh surface is limited to; empty for all.</param>
/// <param name="Joints">Hashes of the joints a Skeleton surface emits from; empty for all.</param>
/// <param name="Scale">emissionMeshScale / meshScale, default 1.</param>
/// <param name="UseNormalForBirth">The normal flag, default true. Read, not applied - see the remarks.</param>
public sealed record VfxEmissionSurface(
    VfxEmissionSurfaceKind Kind,
    string? MeshPath = null,
    string? SkeletonPath = null,
    string? AnimationName = null,
    IReadOnlyList<uint>? Submeshes = null,
    IReadOnlyList<uint>? Joints = null,
    float Scale = 1f,
    bool UseNormalForBirth = true)
{
    /// <summary>True when only a character host can supply the surface.</summary>
    public bool NeedsHost => Kind == VfxEmissionSurfaceKind.Host;
}

/// <summary>
/// M754: picks birth points on an emission surface - a triangle list or a set of points.
///
/// <para><b>Uniform over AREA is an assumption.</b> Every triangle is chosen with probability proportional
/// to its area and the point is uniform inside it, which spreads particles evenly over the surface
/// whatever its tessellation. How League picks is unmeasured; a vertex-uniform pick would crowd particles
/// onto the dense parts of a mesh (a face, a hand), and would be visible there first if it is what the game
/// does.</para>
///
/// <para>The triangle weights are fixed when the sampler is built; <see cref="UpdatePositions"/> swaps the
/// vertex positions under them, so an animated host (M755) moves the points without re-weighting every
/// frame.</para>
/// </summary>
public sealed class VfxSurfaceSampler
{
    private readonly int[] _tri;      // vertex indices, three per triangle; empty for a point set
    private readonly float[] _cdf;    // cumulative area (or count, for points), normalised to 1
    private Vector3[] _positions;

    /// <summary>The multiplier applied to every sampled point (emissionMeshScale).</summary>
    public float Scale { get; init; } = 1f;

    public int TriangleCount => _tri.Length / 3;
    public int PointCount => _tri.Length == 0 ? _positions.Length : 0;
    public int VertexCount => _positions.Length;

    private VfxSurfaceSampler(Vector3[] positions, int[] tri, float[] cdf)
    {
        _positions = positions;
        _tri = tri;
        _cdf = cdf;
    }

    /// <summary>A sampler over the triangles of a mesh. Degenerate triangles are dropped; null when nothing
    /// with area is left.</summary>
    public static VfxSurfaceSampler? FromMesh(float[] positions, IReadOnlyList<uint> indices, float scale = 1f,
        Func<int, bool>? keepTriangle = null)
    {
        int vc = positions.Length / 3;
        var pos = new Vector3[vc];
        for (int i = 0; i < vc; i++) pos[i] = new Vector3(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]);
        var tri = new List<int>();
        var areas = new List<float>();
        for (int t = 0; t + 2 < indices.Count; t += 3)
        {
            if (keepTriangle is not null && !keepTriangle(t / 3)) continue;
            int a = (int)indices[t], b = (int)indices[t + 1], c = (int)indices[t + 2];
            if (a >= vc || b >= vc || c >= vc) continue;
            float area = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]).Length() * 0.5f;
            if (!(area > 1e-9f) || float.IsInfinity(area)) continue;
            tri.Add(a); tri.Add(b); tri.Add(c);
            areas.Add(area);
        }
        if (areas.Count == 0) return null;
        return new VfxSurfaceSampler(pos, tri.ToArray(), Cumulative(areas)) { Scale = scale };
    }

    /// <summary>A sampler over a set of points, each equally likely - a skeleton's joints.</summary>
    public static VfxSurfaceSampler? FromPoints(IReadOnlyList<Vector3> points, float scale = 1f)
    {
        if (points.Count == 0) return null;
        return new VfxSurfaceSampler(points.ToArray(), Array.Empty<int>(), Cumulative(Enumerable.Repeat(1f, points.Count).ToList()))
            { Scale = scale };
    }

    private static float[] Cumulative(List<float> weights)
    {
        var cdf = new float[weights.Count];
        double sum = 0;
        for (int i = 0; i < weights.Count; i++) { sum += weights[i]; cdf[i] = (float)sum; }
        for (int i = 0; i < cdf.Length; i++) cdf[i] = (float)(cdf[i] / sum);
        cdf[^1] = 1f;
        return cdf;
    }

    /// <summary>Move the surface: same vertices, new places. A different vertex count is refused.</summary>
    public void UpdatePositions(Vector3[] positions)
    {
        if (positions.Length != _positions.Length)
            throw new ArgumentException($"The surface has {_positions.Length} vertices, not {positions.Length}.", nameof(positions));
        _positions = positions;
    }

    /// <summary>One birth point, in the surface's own space and already scaled.</summary>
    public Vector3 Sample(Random rng)
    {
        int i = Pick((float)rng.NextDouble());
        if (_tri.Length == 0) return _positions[i] * Scale;
        var a = _positions[_tri[i * 3]];
        var b = _positions[_tri[i * 3 + 1]];
        var c = _positions[_tri[i * 3 + 2]];
        // uniform inside the triangle: the square-root fold keeps it from bunching at a corner
        float r1 = MathF.Sqrt((float)rng.NextDouble()), r2 = (float)rng.NextDouble();
        return (a * (1f - r1) + b * (r1 * (1f - r2)) + c * (r1 * r2)) * Scale;
    }

    private int Pick(float u)
    {
        int lo = 0, hi = _cdf.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_cdf[mid] < u) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}
