using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using ReyEngine.App.ViewModels;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.App.Services;

/// <summary>
/// <para>M787: how far a placed system's visuals can reach from its placement origin - the radius of the
/// sphere the camera gate (<see cref="VfxPlaybackSim.IsActive"/>) tests instead of the origin alone.</para>
///
/// <para>The gate used to test only the origin, so a large system switched off as soon as its centre left
/// the 1.25x frustum margin while most of it was still on screen. 7yanniversary's penguin is one mesh
/// particle standing at (2000,0,2000); orbiting the camera made it pop out and back in.</para>
///
/// <para>Conservative by design - a system kept alive a little longer only costs simulation, a system culled
/// while visible is the bug. Per visual emitter: its offset (EmitterPosition and spawn shape), plus its
/// size (a mesh's own radius times birthScale x scale0, or a quad's birthScale), plus how far its birth
/// velocity carries a particle over its lifetime. The system's authored visibilityRadius, Riot's own figure,
/// is a floor. Capped at <see cref="MaxRadius"/> so one runaway value (sphere radii up to 3e8 ship) cannot
/// switch culling off for a whole map.</para>
/// </summary>
public static class VfxCullBounds
{
    public const float MaxRadius = 20_000f;

    /// <summary>Lifetimes are clamped to this for the travel term; a looping or unauthored lifetime would
    /// otherwise make every moving sprite look map-sized.</summary>
    private const float MaxTravelSeconds = 10f;

    private static readonly ConditionalWeakTable<VfxPlaybackItem, StrongBox<float>> Cache = new();

    /// <summary>The cached radius for this playback item (computed once per item).</summary>
    public static float Radius(VfxPlaybackItem item) =>
        Cache.GetValue(item, static i => new StrongBox<float>(Compute(i))).Value;

    public static float Compute(VfxPlaybackItem item)
    {
        float r = MathF.Max(0f, item.System.VisibilityRadius);
        var emitters = item.System.Emitters;
        for (int i = 0; i < emitters.Count; i++)
        {
            var e = emitters[i];
            if (!e.IsVisual) continue;

            float offset = MaxLength(e.EmitterPosition) + ShapeReach(e.SpawnShape);
            float size = MaxComponent(e.BirthScale) * (e.ScaleOverLife is { } sol ? MaxComponent(sol) : 1f);
            var mesh = item.EmitterMeshes is { } meshes && i < meshes.Count ? meshes[i] : null;
            float extent = e.IsMeshPrimitive && mesh is not null ? MeshRadius(mesh) * size : size;

            float life = MaxValue(e.ParticleLifetime);
            if (!(life > 0f) || life > MaxTravelSeconds) life = MaxTravelSeconds;
            float travel = MaxLength(e.BirthVelocity) * life;

            // M795: the emitter's own frame scales all of that and then moves it (VfxEmitterOverride).
            var so = VfxEmitterOverride.Scale(e);
            float reach = (offset + extent + travel) * MaxOf(Scaled(so, 1f, 1f, 1f))
                          + VfxEmitterOverride.Translation(e).Length();
            if (float.IsFinite(reach)) r = MathF.Max(r, reach);
            else r = MaxRadius;
        }

        // a travelling missile or a tether reaches its far end too (previews only - map placements set neither)
        if (item.TravelTo is { } to) r = MathF.Max(r, Vector3.Distance(item.WorldPos, to));
        if (item.BeamTarget is { } target) r = MathF.Max(r, Vector3.Distance(item.WorldPos, target));

        return MathF.Min(r * PlacementScale(item.Transform), MaxRadius);
    }

    /// <summary>The largest axis scale of the placement transform, so a scaled-up placement grows its sphere.</summary>
    private static float PlacementScale(in Matrix4x4 m)
    {
        float sx = new Vector3(m.M11, m.M12, m.M13).Length();
        float sy = new Vector3(m.M21, m.M22, m.M23).Length();
        float sz = new Vector3(m.M31, m.M32, m.M33).Length();
        float s = MathF.Max(sx, MathF.Max(sy, sz));
        return float.IsFinite(s) && s > 0f ? s : 1f;
    }

    private static float MeshRadius(StaticMeshData mesh)
    {
        var p = mesh.Positions;
        float best = 0f;
        for (int v = 0; v + 2 < p.Length; v += 3)
            best = MathF.Max(best, p[v] * p[v] + p[v + 1] * p[v + 1] + p[v + 2] * p[v + 2]);
        return MathF.Sqrt(best);
    }

    private static float ShapeReach(VfxSpawnShape? shape)
    {
        if (shape is null) return 0f;
        return MaxLength(shape.EmitOffset) + MathF.Abs(shape.Radius) + MathF.Abs(shape.Height) + shape.Size.Length();
    }

    // The simulator multiplies a birth value by each component's probability-table sample
    // (VfxCurve3.SampleBirth), so the reach of a curve is its largest key scaled per component by the
    // largest probability value.
    private static float MaxLength(VfxCurve3? curve)
    {
        if (curve is not { } c) return 0f;
        float px = ProbMax(c.Prob, 0), py = ProbMax(c.Prob, 1), pz = ProbMax(c.Prob, 2);
        float best = Scaled(c.Constant, px, py, pz).Length();
        if (c.Values is { } values)
            foreach (var v in values) best = MathF.Max(best, Scaled(v, px, py, pz).Length());
        return best;
    }

    private static float MaxComponent(VfxCurve3 c)
    {
        float px = ProbMax(c.Prob, 0), py = ProbMax(c.Prob, 1), pz = ProbMax(c.Prob, 2);
        float best = MaxOf(Scaled(c.Constant, px, py, pz));
        if (c.Values is { } values)
            foreach (var v in values) best = MathF.Max(best, MaxOf(Scaled(v, px, py, pz)));
        return best;
    }

    private static float MaxValue(VfxCurveF c)
    {
        float p = ProbMax(c.Prob, 0);
        float best = MathF.Abs(c.Constant) * p;
        if (c.Values is { } values)
            foreach (var v in values) best = MathF.Max(best, MathF.Abs(v) * p);
        return best;
    }

    private static Vector3 Scaled(Vector3 v, float px, float py, float pz) =>
        new(MathF.Abs(v.X) * px, MathF.Abs(v.Y) * py, MathF.Abs(v.Z) * pz);

    private static float MaxOf(Vector3 v) => MathF.Max(v.X, MathF.Max(v.Y, v.Z));

    private static float ProbMax(VfxProbTable[]? prob, int index)
    {
        if (prob is null || index >= prob.Length || prob[index].IsEmpty) return 1f;
        float best = 0f;
        foreach (var v in prob[index].Values) best = MathF.Max(best, MathF.Abs(v));
        return best;
    }
}
