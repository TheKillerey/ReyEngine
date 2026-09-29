using System.Numerics;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Shaders;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.D3D11;
using ReyEngine.Rendering.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M795: an emitter's scaleOverride and translationOverride. The report was a dark ring wall round a purple
/// dome in the middle of Map22 darkstar_blackhole: TFT_Skybox_Darkstar_A's sky domes, authored at birthScale
/// 0.2 with scaleOverride 17-18, drew at 1/18 of their size because neither override field was applied.
/// </summary>
public sealed class EmitterOverrideTests
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";

    private static VfxEmitterDefinition Emitter(Vector3 position, Vector3 scale, Vector3? velocity = null,
        VfxEmitterExtras? extras = null) => new(
        Name: "e",
        Rate: VfxCurveF.Const(1f),
        ParticleLifetime: VfxCurveF.Const(-1f),
        EmitterLifetime: null,
        ParticleLinger: 0f,
        TimeBeforeFirstEmission: 0f,
        IsSingleParticle: true,
        Disabled: false,
        BlendMode: 1,
        BirthScale: VfxCurve3.Const(scale),
        ScaleOverLife: null,
        BirthColor: VfxCurve4.Const(Vector4.One),
        ColorOverLife: null,
        BirthVelocity: velocity is { } v ? VfxCurve3.Const(v) : null,
        Acceleration: null,
        BirthRotationalVelocity: null,
        EmitterPosition: VfxCurve3.Const(position),
        TexturePath: "ASSETS/Test/p.dds",   // IsVisual needs a path; the file need not exist
        TexDiv: new Vector2(1f, 1f),
        NumFrames: 1,
        RandomStartFrame: false,
        IsMeshPrimitive: false,
        Extras: extras);

    private static VfxParticleSimulator.EmitterState Run(VfxEmitterDefinition e, Matrix4x4 placement, float seconds = 0.1f)
    {
        var sim = new VfxParticleSimulator(seed: 1234);
        sim.SetSystem(new VfxSystemDefinition(PathHash: 1, Name: "override_test", ParticlePath: "", Emitters: new[] { e }), placement);
        sim.Update(seconds);
        var state = sim.Emitters.Single();
        Assert.True(state.InstanceCount > 0, "no particle was emitted, so the check would be vacuous");
        return state;
    }

    private static Vector3 Position(VfxParticleSimulator.EmitterState s) => new(s.Instances[0], s.Instances[1], s.Instances[2]);
    private static Vector2 Size(VfxParticleSimulator.EmitterState s) => new(s.Instances[3], s.Instances[4]);

    private static readonly Matrix4x4 Placement =
        Matrix4x4.CreateRotationY(0.7f) * Matrix4x4.CreateTranslation(2000f, 0f, 2000f);

    [Fact]
    public void AnEmitterWithoutAnOverrideIsPlacedExactlyAsBefore()
    {
        var velocity = new Vector3(5f, 0f, 0f);
        var s = Run(Emitter(new Vector3(10f, 20f, 30f), new Vector3(7f, 7f, 7f), velocity), Placement);
        Assert.Equal(Matrix4x4.Identity, VfxEmitterOverride.Frame(s.Def));
        Assert.Equal(Vector3.Transform(new Vector3(10f, 20f, 30f), Placement), s.BasePos);
        Assert.Equal(new Vector2(7f, 7f), Size(s));
        // the pre-M795 path: the velocity turned by the placement alone
        var moved = Vector3.Normalize(Position(s) - s.BasePos);
        var expected = Vector3.Normalize(Vector3.TransformNormal(velocity, Placement));
        Assert.Equal(expected.X, moved.X, 4);
        Assert.Equal(expected.Z, moved.Z, 4);
    }

    [Fact]
    public void ScaleOverrideScalesTheEmittersSpaceAndTranslationOverrideMovesItUnscaled()
    {
        // darkstar's BlueClouds: EmitterPosition (0, 150, 0), birthScale 0.27, scaleOverride 17, translationOverride (0, -6000, 0)
        var extras = new VfxEmitterExtras { ScaleOverride = new Vector3(17f), TranslationOverride = new Vector3(0f, -6000f, 0f) };
        var velocity = new Vector3(1f, 0f, 0f);
        var e = Emitter(new Vector3(0f, 150f, 0f), new Vector3(0.27f), velocity, extras);
        var placement = Matrix4x4.CreateTranslation(2000f, 0f, 2000f);
        var s = Run(e, placement);
        var plain = Run(Emitter(new Vector3(0f, 150f, 0f), new Vector3(0.27f), velocity), placement);

        Assert.Equal(new Vector3(2000f, 150f * 17f - 6000f, 2000f), s.BasePos);   // -3,450
        Assert.Equal(0.27f * 17f, Size(s).X, 4);
        Assert.Equal(0.27f * 17f, Size(s).Y, 4);
        // the motion scales with the space: 17 times the unscaled twin's, over the same step
        float travelled = (Position(s) - s.BasePos).X, plainTravelled = (Position(plain) - plain.BasePos).X;
        Assert.True(plainTravelled > 0f);
        Assert.InRange(travelled / plainTravelled, 16.95f, 17.05f);   // float positions near 2,000: ~1e-4 per step
        Assert.Equal(new Vector3(0f, 150f * 17f - 6000f, 0f), Vector3.Transform(new Vector3(0f, 150f, 0f), VfxEmitterOverride.Frame(e)));
    }

    [Fact]
    public void AMirroringScaleOverrideMirrorsThePositionToo()
    {
        // 7yanniversary's Sunshine Godray1: authored at x = -1211 with scaleOverride (-1, 1, 1); every other sun
        // element of the system (burst, bokeh, glow) stands at x = +1,050..1,250.
        var extras = new VfxEmitterExtras { ScaleOverride = new Vector3(-1f, 1f, 1f) };
        var s = Run(Emitter(new Vector3(-1211.0496f, 328.46838f, 549.9996f), Vector3.One, extras: extras), Matrix4x4.Identity);
        Assert.Equal(1211.0496f, s.BasePos.X, 3);
        Assert.Equal(328.46838f, s.BasePos.Y, 3);
    }

    [Fact]
    public void TheParticleEditorNoLongerBadgesTheTwoAppliedFields()
    {
        foreach (var field in new[] { "scaleOverride", "translationOverride" })
        {
            Assert.DoesNotContain(field, VfxParkedEmitterFields.Names);
            Assert.True(VfxPreviewCoverage.IsParsed(HashAlgorithms.Fnv1a(field)), field);
            Assert.Null(VfxPreviewCoverage.IgnoredNote(HashAlgorithms.Fnv1a(field)));
        }
        // still parsed and not applied
        Assert.NotNull(VfxPreviewCoverage.IgnoredNote(HashAlgorithms.Fnv1a("rotationOverride")));
    }

    [Fact]
    public void ANonFiniteOverrideIsIgnored()
    {
        var extras = new VfxEmitterExtras { ScaleOverride = new Vector3(float.NaN, 1f, 1f), TranslationOverride = new Vector3(float.PositiveInfinity, 0f, 0f) };
        var e = Emitter(Vector3.Zero, Vector3.One, extras: extras);
        Assert.False(VfxEmitterOverride.IsAuthored(e));
        Assert.Equal(Matrix4x4.Identity, VfxEmitterOverride.Frame(e));
    }

    private sealed record Sky(VfxSystemDefinition System, MapParticlePlacement Placement, VfxEmitterDefinition Base,
        StaticMeshData Mesh, TextureImage? Texture, WadArchive Wad, WadPathResolver Resolver) : IDisposable
    {
        public void Dispose() => Wad.Dispose();
    }

    private static Sky? LoadSky()
    {
        string wadPath = Path.Combine(Final, @"Maps\Shipping\Map22.wad.client");
        if (!File.Exists(wadPath)) return null;
        var db = new HashSyncService().LoadLocal(_ => { });
        var resolver = new WadPathResolver(db);
        var wad = WadArchive.Open(wadPath, resolver);
        byte[]? Read(string path)
        {
            ulong h = BinTexturePath.HashOfReference(path);
            return wad.TryGetEntry(h, out _) ? wad.Extract(h) : null;
        }
        var bin = Read("data/maps/mapgeometry/map22/darkstar_blackhole.materials.bin");
        var system = bin is null ? null : VfxSystemResolver.ExtractAll(bin).Values.FirstOrDefault(s => s.Name == "TFT_Skybox_Darkstar_A");
        var placement = bin is null ? null : MapParticleExtractor.Extract(bin, h => db.TryGetBinName(h, out var n) ? n : null)
            .FirstOrDefault(p => p.Name == "TFT_Skybox_Darkstar_A1");
        if (system is null || placement is null) { wad.Dispose(); return null; }
        var sky = system.Emitters.Single(e => e.Name == "Base");
        var mesh = StaticObjectDecoder.Decode(Read(sky.MeshPath!)!, sky.MeshPath!)!;
        var texture = Read(sky.TexturePath!) is { } tb ? TextureDecoder.Decode(tb) : null;
        return new Sky(system, placement, sky, mesh, texture, wad, resolver);
    }

    private static VfxEmitterDefinition Stripped(VfxEmitterDefinition e) =>
        e with { Extras = e.Extras! with { ScaleOverride = null, TranslationOverride = null } };

    /// <summary>The real stars dome through the simulator: authored 0.2 x scaleOverride 18 it is a dome
    /// ~8,200 units in radius round the ~2,000-unit board; without the override it was a 460-unit ring on it.</summary>
    [Fact]
    public void DarkstarStarsDomeEnclosesTheBoardInsteadOfRingingItsCentre()
    {
        using var sky = LoadSky();
        if (sky is null) return;
        Assert.Equal(new Vector3(18f), sky.Base.Extras!.ScaleOverride);
        Assert.Equal(0.2f, sky.Base.BirthScale.Constant.X, 4);
        Assert.Equal(new Vector3(2000f, 0f, 2000f), sky.Placement.Transform.Translation);

        float meshRadius = 0f;
        for (int v = 0; v + 2 < sky.Mesh.Positions.Length; v += 3)
            meshRadius = MathF.Max(meshRadius, MathF.Sqrt(sky.Mesh.Positions[v] * sky.Mesh.Positions[v] + sky.Mesh.Positions[v + 2] * sky.Mesh.Positions[v + 2]));
        Assert.InRange(meshRadius, 2200f, 2400f);   // TFT_Skybox_BackgroundClouds.scb: 2,288 (measured)

        var after = Run(sky.Base, sky.Placement.Transform);
        var before = Run(Stripped(sky.Base), sky.Placement.Transform);

        Assert.True(meshRadius * Size(after).X > 8000f, $"the dome should enclose the board, radius {meshRadius * Size(after).X}");
        Assert.Equal(-150f * 18f, after.BasePos.Y, 2);
        Assert.True(meshRadius * Size(before).X < 1000f, $"the pre-M795 dome was a ring on the board, radius {meshRadius * Size(before).X}");
        Assert.Equal(-150f, before.BasePos.Y, 2);
    }

    /// <summary>The same dome on the real D3D11 map-particle path, seen from above the board: it covers the whole
    /// frame with the override and only a small ring in the middle without it (same frame, same particle).</summary>
    [Fact]
    public void DarkstarStarsDomeFillsTheFrameOnTheD3D11Path()
    {
        using var sky = LoadSky();
        if (sky is null) return;
        using var cache = ShaderCacheReader.Open(Final, sky.Resolver, out _);
        if (cache is null) return;

        (double Coverage, int Materials) Coverage(VfxEmitterDefinition e, string tag)
        {
            var one = sky.System with { PathHash = HashAlgorithms.Fnv1a("m795/" + tag), Emitters = new[] { e } };
            var item = new VfxPlaybackItem(one, sky.Placement.Transform, new[] { sky.Texture }, new StaticMeshData?[] { sky.Mesh });
            // the editor's board view: above and south of the board centre, looking at it
            var target = new Vector3(-2000f, 0f, 2000f);   // display space (mirrored X)
            var eye = target + Vector3.Normalize(new Vector3(0f, 1.25f, -1f)) * 4300f;
            var view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(0.785f, 16f / 9f, 10f, 40000f);
            var mirroredView = Matrix4x4.CreateScale(-1f, 1f, 1f) * view;
            const int W = 320, H = 180;
            var settings = new PreviewSettings
            {
                SuppliedView = view, SuppliedProjection = proj, SuppliedCameraPosition = eye,
                AlphaBlend = true, DepthTest = true, MirrorX = true, TransposeMatrices = true,
                CullBackFaces = false, SortByPipeline = false,
                ClearColor = new Vector4(0f, 0f, 0f, 1f), TimeSeconds = 1f,
            };
            using var renderer = new ShaderPreviewRenderer();
            if (!renderer.Initialize(out _)) return (-1, 0);   // no device: nothing to measure
            var driver = new D3D11MapParticles(renderer, cache);
            driver.SetPlayback(new VfxPlayback(new[] { item }));
            for (int i = 0; i < 10; i++) driver.Tick(1f / 30f, mirroredView, mirroredView * proj, eye, 4300f);
            var frame = renderer.RenderFrame(W, H, settings, out _)!;
            int lit = 0;
            for (int k = 0; k < W * H; k++)
                if (frame[k * 4] + frame[k * 4 + 1] + frame[k * 4 + 2] > 12) lit++;
            int mats = driver.RiotMeshEmitters;
            driver.StopAll();
            return ((double)lit / (W * H), mats);
        }

        var before = Coverage(Stripped(sky.Base), "before");
        if (before.Coverage < 0) return;
        var after = Coverage(sky.Base, "after");
        Assert.Equal(1, before.Materials);
        Assert.Equal(1, after.Materials);
        // measured: 2.3% of the frame before (the ring), 100% after
        Assert.True(before.Coverage < 0.10, $"the pre-M795 dome should be a small ring, covered {before.Coverage:P1}");
        Assert.True(after.Coverage > 0.95, $"the dome should fill the frame, covered {after.Coverage:P1}");
    }
}
