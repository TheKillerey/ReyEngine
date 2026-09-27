using System;
using System.Numerics;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Decoding;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Vfx;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M787: "fix the particle culling too so the penguin doesn't pop out". The camera gate tested only a
/// placement's ORIGIN, so 7yanniversary's penguin - one big mesh particle standing at (2000,0,2000) - switched
/// off whenever its centre left the frustum margin while most of it was still on screen. The gate now tests a
/// sphere around the origin (VfxCullBounds).
/// </summary>
public sealed class ParticleCullBoundsTests
{
    // The viewport's shape: world is unmirrored, the X flip lives in front of the view.
    private static readonly Vector3 Cam = new(0f, 3000f, -3000f);
    private static Matrix4x4 MirroredViewProj()
    {
        var view = Matrix4x4.CreateLookAt(new Vector3(-Cam.X, Cam.Y, Cam.Z), Vector3.Zero, Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4f, 16f / 9f, 10f, 100000f);
        return Matrix4x4.CreateScale(-1f, 1f, 1f) * view * proj;
    }

    private static readonly float MaxDistSq = VfxPlaybackSim.MaxDistanceSquared(4242f);

    /// <summary>The pre-M787 gate, verbatim.</summary>
    private static bool OldPointTest(Vector3 p, in Matrix4x4 viewProj)
    {
        if (Vector3.DistanceSquared(Cam, p) > MaxDistSq) return false;
        var clip = Vector4.Transform(new Vector4(p, 1f), viewProj);
        if (clip.W <= 0f) return false;
        float margin = clip.W * 1.25f;
        return !(MathF.Abs(clip.X) > margin || MathF.Abs(clip.Y) > margin || clip.Z < -margin || clip.Z > margin);
    }

    [Fact]
    public void A_vanishing_sphere_gives_exactly_the_old_point_answer()
    {
        var vp = MirroredViewProj();
        var rng = new Random(787);
        int checkedPoints = 0, active = 0;
        for (int i = 0; i < 20000; i++)
        {
            var p = new Vector3(rng.NextSingle() * 16000f - 8000f, rng.NextSingle() * 4000f - 1000f,
                rng.NextSingle() * 16000f - 8000f);
            bool old = OldPointTest(p, vp);
            Assert.Equal(old, VfxPlaybackSim.IsActive(p, 0f, Cam, MaxDistSq, vp));
            // a radius of a thousandth of a unit can only differ from the point test on a plane itself
            Assert.Equal(old, VfxPlaybackSim.IsActive(p, 1e-3f, Cam, MaxDistSq, vp));
            checkedPoints++;
            if (old) active++;
        }
        Assert.InRange(active, 500, checkedPoints - 500);   // both answers were actually exercised
    }

    [Fact]
    public void A_big_mesh_whose_origin_left_the_view_stays_on()
    {
        var vp = MirroredViewProj();
        // walk sideways until the ORIGIN is just outside the old margin
        var p = Vector3.Zero;
        while (OldPointTest(p, vp)) p.X += 50f;
        Assert.False(VfxPlaybackSim.IsActive(p, 0f, Cam, MaxDistSq, vp));     // the old pop-out
        Assert.True(VfxPlaybackSim.IsActive(p, 1500f, Cam, MaxDistSq, vp));   // a penguin-sized sphere reaches in

        // and a sphere wholly off screen is still culled: far away (the distance gate) ...
        Assert.False(VfxPlaybackSim.IsActive(p + new Vector3(20000f, 0f, 0f), 1500f, Cam, MaxDistSq, vp));
        // ... and 780 units BEHIND the camera, well inside the distance gate - the frustum planes alone
        var behind = Cam + new Vector3(0f, 500f, -600f);
        Assert.True(Vector3.DistanceSquared(Cam, behind) < MaxDistSq);
        Assert.False(VfxPlaybackSim.IsActive(behind, 100f, Cam, MaxDistSq, vp));
        Assert.True(VfxPlaybackSim.IsActive(behind, 2000f, Cam, MaxDistSq, vp));   // big enough to reach past the camera
    }

    private static VfxEmitterDefinition Emitter(string name, Vector3 birthScale, bool mesh = false,
        Vector3? velocity = null, float lifetime = 2f, bool disabled = false, string? texture = "ASSETS/t.dds") => new(
        Name: name,
        Rate: VfxCurveF.Const(10f),
        ParticleLifetime: VfxCurveF.Const(lifetime),
        EmitterLifetime: null,
        ParticleLinger: 0f,
        TimeBeforeFirstEmission: 0f,
        IsSingleParticle: mesh,
        Disabled: disabled,
        BlendMode: 1,
        BirthScale: VfxCurve3.Const(birthScale),
        ScaleOverLife: null,
        BirthColor: VfxCurve4.Const(Vector4.One),
        ColorOverLife: null,
        BirthVelocity: velocity is { } v ? VfxCurve3.Const(v) : null,
        Acceleration: null,
        BirthRotationalVelocity: null,
        EmitterPosition: VfxCurve3.Const(Vector3.Zero),
        TexturePath: texture,
        TexDiv: new Vector2(1f, 1f),
        NumFrames: 1,
        RandomStartFrame: false,
        IsMeshPrimitive: mesh,
        MeshPath: mesh ? "ASSETS/pengu.skn" : null);

    private static VfxPlaybackItem Item(float visibilityRadius, params (VfxEmitterDefinition Def, StaticMeshData? Mesh)[] emitters)
    {
        var system = new VfxSystemDefinition(1u, "Sys", "Sys", Array.ConvertAll(emitters, e => e.Def),
            VisibilityRadius: visibilityRadius);
        return new VfxPlaybackItem(system, Matrix4x4.CreateTranslation(2000f, 0f, 2000f),
            new TextureImage?[emitters.Length], Array.ConvertAll(emitters, e => e.Mesh));
    }

    [Fact]
    public void The_radius_covers_a_mesh_a_travelling_sprite_and_the_authored_floor()
    {
        // a mesh particle reaching 1200 units, drawn at birthScale 2 -> 2400
        var pengu = new StaticMeshData(new[] { 0f, 0f, 0f, 1200f, 0f, 0f, 0f, 700f, 0f }, new float[6], new uint[] { 0, 1, 2 }, "pengu");
        float meshR = VfxCullBounds.Compute(Item(0f, (Emitter("roof", Vector3.One * 2f, mesh: true), pengu)));
        Assert.InRange(meshR, 2400f, 2401f);

        // a 50-unit sprite flying 300 u/s for 2 s -> 650
        float spriteR = VfxCullBounds.Compute(Item(0f, (Emitter("spark", new Vector3(50f, 50f, 1f), velocity: new Vector3(300f, 0f, 0f)), null)));
        Assert.InRange(spriteR, 650f, 651f);

        // the authored visibilityRadius is a floor; a disabled emitter adds nothing
        float floored = VfxCullBounds.Compute(Item(5000f,
            (Emitter("spark", new Vector3(50f, 50f, 1f)), null),
            (Emitter("huge", Vector3.One * 90000f, disabled: true), null)));
        Assert.Equal(5000f, floored);

        // a runaway value is capped rather than switching culling off for the whole map
        float capped = VfxCullBounds.Compute(Item(0f, (Emitter("rocket", Vector3.One, velocity: new Vector3(1e7f, 0f, 0f)), null)));
        Assert.Equal(VfxCullBounds.MaxRadius, capped);
    }

    [Fact]
    public void An_item_without_visuals_keeps_the_old_point_test()
    {
        var vp = MirroredViewProj();
        var item = Item(0f, (Emitter("logic", Vector3.One * 500f, texture: null), null));
        Assert.Equal(0f, VfxCullBounds.Radius(item));
        Assert.Equal(OldPointTest(item.WorldPos, vp), VfxPlaybackSim.IsActive(item, Cam, MaxDistSq, vp));
    }
}
