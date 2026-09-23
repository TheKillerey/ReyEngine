using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.Services;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M754: emitters born on a surface - a legacy .scb, a .skn surface, a skeleton's joints - and the one form
/// that needs a character host. What the file says, what the sampler picks, and that the simulator puts the
/// particles there.
/// </summary>
public sealed class EmissionSurfaceTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);
    private const int Stride = 19;   // VfxParticleRenderer.Stride; position is the first three floats

    // ------------------------------------------------------------------ what the file says

    private static VfxEmitterDefinition Resolve(params BinTreeProperty[] extra)
    {
        var props = new List<BinTreeProperty>
        {
            new BinTreeString(H("emitterName"), "e"),
            new BinTreeString(H("texture"), "ASSETS/t.dds"),
        };
        props.AddRange(extra);
        var emitter = new BinTreeStruct(0, H("VfxEmitterDefinitionData"), props);
        var system = new BinTreeObject(H("sys"), H("VfxSystemDefinitionData"), new BinTreeProperty[]
        {
            new BinTreeString(H("particleName"), "sys"),
            new BinTreeContainer(H("complexEmitterDefinitionData"), BinPropertyType.Struct, new BinTreeProperty[] { emitter }),
        });
        using var ms = new MemoryStream();
        new BinTree(new[] { system }, Array.Empty<string>()).Write(ms);
        return VfxSystemResolver.ExtractAll(ms.ToArray()).Values.Single().Emitters.Single();
    }

    private static BinTreeStruct SurfaceDef(BinTreeStruct? surface) => new(H("emissionSurfaceDefinition"), H("VfxEmissionSurfaceData"),
        surface is null ? Array.Empty<BinTreeProperty>() : new BinTreeProperty[] { surface });

    [Fact]
    public void AnEmitterThatNamesNoSurfaceHasNone() => Assert.Null(Resolve().EmissionSurface);

    [Fact]
    public void TheLegacyMeshNameIsAMeshSurfaceWithItsScaleAndNormalFlag()
    {
        var s = Resolve(new BinTreeString(H("emissionMeshName"), "ASSETS/trail.scb"),
            new BinTreeF32(H("emissionMeshScale"), 2.5f),
            new BinTreeBool(H("useEmissionMeshNormalForBirth"), false)).EmissionSurface!;
        Assert.Equal(VfxEmissionSurfaceKind.LegacyMesh, s.Kind);
        Assert.Equal("ASSETS/trail.scb", s.MeshPath);
        Assert.Equal(2.5f, s.Scale);
        Assert.False(s.UseNormalForBirth);
    }

    [Fact]
    public void AnEmptySurfaceDefinitionNeedsAHost()
    {
        // 757 shipped emitters (Akali W, Aatrox E): no surface named, so the character is the surface
        var s = Resolve(SurfaceDef(null)).EmissionSurface!;
        Assert.Equal(VfxEmissionSurfaceKind.Host, s.Kind);
        Assert.True(s.NeedsHost);

        // the ward pads' unnamed, fieldless surface class says the same
        var unnamed = new BinTreeStruct(H("EmissionSurface"), 0x526478f0, Array.Empty<BinTreeProperty>());
        Assert.True(Resolve(SurfaceDef(unnamed)).EmissionSurface!.NeedsHost);
    }

    [Fact]
    public void TheLegacyNameWinsOverAnEmptyDefinition()
    {
        // 105 emitters carry both; only the name names anything
        var s = Resolve(new BinTreeString(H("emissionMeshName"), "ASSETS/a.scb"), SurfaceDef(null)).EmissionSurface!;
        Assert.Equal(VfxEmissionSurfaceKind.LegacyMesh, s.Kind);
    }

    [Fact]
    public void AMeshSurfaceKeepsItsSkeletonAnimationAndSubmeshes()
    {
        var mesh = new BinTreeStruct(H("EmissionSurface"), H("VfxEmissionMeshData"), new BinTreeProperty[]
        {
            new BinTreeString(H("meshName"), "ASSETS/Hwei.skn"),
            new BinTreeString(H("skeletonName"), "ASSETS/Hwei.skl"),
            new BinTreeString(H("AnimationName"), "ASSETS/Idle.anm"),
            new BinTreeContainer(H("Submeshes"), BinPropertyType.Hash, new BinTreeProperty[] { new BinTreeHash(0, H("PaletteA")) }),
            new BinTreeF32(H("meshScale"), 1.5f),
        });
        var s = Resolve(SurfaceDef(mesh)).EmissionSurface!;
        Assert.Equal(VfxEmissionSurfaceKind.Mesh, s.Kind);
        Assert.Equal("ASSETS/Hwei.skn", s.MeshPath);
        Assert.Equal("ASSETS/Hwei.skl", s.SkeletonPath);
        Assert.Equal("ASSETS/Idle.anm", s.AnimationName);
        Assert.Equal(new[] { H("PaletteA") }, s.Submeshes);
        Assert.Equal(1.5f, s.Scale);
    }

    [Fact]
    public void ASkeletonSurfaceKeepsItsJointMask()
    {
        var skel = new BinTreeStruct(H("EmissionSurface"), H("VfxEmissionSkeletonData"), new BinTreeProperty[]
        {
            new BinTreeString(H("skeletonName"), "ASSETS/Dragon.skl"),
            new BinTreeContainer(H("JointMask"), BinPropertyType.Hash, new BinTreeProperty[] { new BinTreeHash(0, H("Head")), new BinTreeHash(0, H("Tail")) }),
        });
        var s = Resolve(SurfaceDef(skel)).EmissionSurface!;
        Assert.Equal(VfxEmissionSurfaceKind.Skeleton, s.Kind);
        Assert.Equal(new[] { H("Head"), H("Tail") }, s.Joints);
    }

    // ------------------------------------------------------------------ the sampler

    /// <summary>Two triangles side by side in XZ: the right one has three times the area of the left.</summary>
    private static VfxSurfaceSampler TwoTriangles(float scale = 1f) => VfxSurfaceSampler.FromMesh(new float[]
    {
        0, 0, 0,   1, 0, 0,   0, 0, 1,        // left: area 0.5
        10, 0, 0,  13, 0, 0,  10, 0, 1,       // right: area 1.5
    }, new uint[] { 0, 1, 2, 3, 4, 5 }, scale)!;

    [Fact]
    public void TrianglesAreChosenByArea()
    {
        var s = TwoTriangles();
        var rng = new Random(7);
        int right = 0;
        const int n = 20_000;
        for (int i = 0; i < n; i++) if (s.Sample(rng).X >= 10f) right++;
        Assert.InRange(right / (float)n, 0.73f, 0.77f);   // 1.5 of 2.0
    }

    [Fact]
    public void EveryPointLiesInsideItsTriangle()
    {
        var s = TwoTriangles();
        var rng = new Random(3);
        for (int i = 0; i < 2_000; i++)
        {
            var p = s.Sample(rng);
            Assert.Equal(0f, p.Y);
            if (p.X < 5f) Assert.True(p.X >= 0f && p.Z >= 0f && p.X + p.Z <= 1.0001f);
            else Assert.True(p.X >= 10f && p.Z >= 0f && (p.X - 10f) / 3f + p.Z <= 1.0001f);
        }
    }

    [Fact]
    public void ScaleAndMovedPositionsReachTheSamples()
    {
        var s = TwoTriangles(scale: 2f);
        var rng = new Random(1);
        Assert.All(Enumerable.Range(0, 200).Select(_ => s.Sample(rng)), p => Assert.True(p.X <= 26.001f));
        s.UpdatePositions(Enumerable.Repeat(new Vector3(5, 5, 5), 6).ToArray());   // an animated host moves
        Assert.True(Vector3.Distance(new Vector3(10, 10, 10), s.Sample(rng)) < 1e-3f);
        Assert.Throws<ArgumentException>(() => s.UpdatePositions(new Vector3[5]));
    }

    [Fact]
    public void DegenerateTrianglesAreDroppedAndAFlatMeshHasNoSurface()
    {
        Assert.Null(VfxSurfaceSampler.FromMesh(new float[] { 0, 0, 0, 1, 0, 0, 2, 0, 0 }, new uint[] { 0, 1, 2 }));
        Assert.Null(VfxSurfaceSampler.FromPoints(Array.Empty<Vector3>()));
        var pts = VfxSurfaceSampler.FromPoints(new[] { new Vector3(1, 2, 3) })!;
        Assert.Equal(new Vector3(1, 2, 3), pts.Sample(new Random(0)));
    }

    // ------------------------------------------------------------------ the loader

    [Fact]
    public void AHostSurfaceDoesNotLoadAndSaysWhy()
    {
        var r = VfxEmissionSurfaceLoader.Load(new VfxEmissionSurface(VfxEmissionSurfaceKind.Host), _ => null);
        Assert.Null(r.Sampler);
        Assert.Contains("character host", r.Why);
        var missing = VfxEmissionSurfaceLoader.Load(new VfxEmissionSurface(VfxEmissionSurfaceKind.LegacyMesh, MeshPath: "ASSETS/gone.scb"), _ => null);
        Assert.Null(missing.Sampler);
        Assert.Contains("gone.scb", missing.Why);
    }

    [Fact]
    public void HweisIdleDripsComeFromThePaletteNotTheWholeBody()
    {
        // Riot's own data: Hwei_Skin01_Idle_DrippingVFX lists one submesh, FNV-1a("PaletteA")
        const string wad = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Champions\Hwei.wad.client";
        if (!File.Exists(wad)) return;
        var db = new HashSyncService().LoadLocal(_ => { });
        using var archive = ReyEngine.Core.Wad.WadArchive.Open(wad, new WadPathResolver(db));
        byte[]? Read(string path) => archive.Entries.FirstOrDefault(x => x.PathHash == HashAlgorithms.WadPath(path)) is { } e ? archive.Extract(e) : null;
        VfxEmitterDefinition? drip = null;
        foreach (var entry in archive.Entries.Where(x => x.IsResolved && x.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                drip = VfxSystemResolver.ExtractAll(archive.Extract(entry)).Values
                    .Where(d => d.Name == "Hwei_Skin01_Idle_DrippingVFX")
                    .SelectMany(d => d.Emitters).FirstOrDefault(em => em.EmissionSurface?.Kind == VfxEmissionSurfaceKind.Mesh);
            }
            catch { }
            if (drip is not null) break;
        }
        Assert.NotNull(drip);
        var surface = drip!.EmissionSurface!;
        var palette = VfxEmissionSurfaceLoader.Load(surface, Read);
        var whole = VfxEmissionSurfaceLoader.Load(surface with { Submeshes = null }, Read);
        Assert.NotNull(palette.Sampler);
        Assert.True(palette.Sampler!.TriangleCount < whole.Sampler!.TriangleCount,
            $"PaletteA {palette.Sampler.TriangleCount} of {whole.Sampler.TriangleCount} triangles");

        // a list that names no submesh of the mesh is refused, not widened to the whole body
        var none = VfxEmissionSurfaceLoader.Load(surface with { Submeshes = new[] { H("NoSuchSubmesh") } }, Read);
        Assert.Null(none.Sampler);
        Assert.Contains("listed submesh", none.Why);
    }

    // ------------------------------------------------------------------ the simulator

    private static VfxEmitterDefinition Emitter() => new(
        Name: "e", Rate: VfxCurveF.Const(200f), ParticleLifetime: VfxCurveF.Const(5f), EmitterLifetime: null,
        ParticleLinger: 0f, TimeBeforeFirstEmission: 0f, IsSingleParticle: false, Disabled: false, BlendMode: 1,
        BirthScale: VfxCurve3.Const(new Vector3(10f)), ScaleOverLife: null, BirthColor: VfxCurve4.Const(Vector4.One),
        ColorOverLife: null, BirthVelocity: null, Acceleration: null, BirthRotationalVelocity: null,
        EmitterPosition: VfxCurve3.Const(new Vector3(0, 0, 100)), TexturePath: "ASSETS/t.dds", TexDiv: Vector2.One,
        NumFrames: 1, RandomStartFrame: false, IsMeshPrimitive: false);

    private static List<Vector3> Born(VfxSurfaceSampler? surface, Matrix4x4 placement)
    {
        var system = new VfxSystemDefinition(PathHash: 1, Name: "s", ParticlePath: "", Emitters: new[] { Emitter() });
        var item = new ReyEngine.App.ViewModels.VfxPlaybackItem(system, placement, new ReyEngine.Core.Decoding.TextureImage?[1])
            { EmitterEmissionSurfaces = new[] { surface } };
        var sim = VfxPlaybackSim.Create(item)!;
        sim.Update(0.25f);
        var e = sim.Emitters.Single();
        return Enumerable.Range(0, e.InstanceCount)
            .Select(i => new Vector3(e.Instances[i * Stride], e.Instances[i * Stride + 1], e.Instances[i * Stride + 2])).ToList();
    }

    [Fact]
    public void ParticlesAreBornOnTheSurfaceAroundTheEmitter()
    {
        var at = Born(TwoTriangles(), Matrix4x4.CreateTranslation(1000, 0, 0));
        Assert.NotEmpty(at);
        // the mesh is in the emitter's own space: the placement (1000,0,0) plus EmitterPosition (0,0,100)
        Assert.All(at, p =>
        {
            Assert.InRange(p.X, 1000f, 1013.001f);
            Assert.InRange(p.Z, 100f, 101.001f);
        });
        Assert.Contains(at, p => p.X >= 1010f);   // both triangles are used

        // without a surface the same emitter emits from its point, as before M754
        Assert.All(Born(null, Matrix4x4.CreateTranslation(1000, 0, 0)), p => Assert.Equal(new Vector3(1000, 0, 100), p));
    }
}
