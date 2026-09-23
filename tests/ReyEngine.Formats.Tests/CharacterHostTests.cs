using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M755: the Character host - emitters born on "the character they are attached to" sample a posed body in
/// WORLD space, the viewports only pay for it when something needs it, and the particle editor borrows the
/// character window's model.
/// </summary>
public sealed class CharacterHostTests
{
    private const int Stride = 19;   // VfxParticleRenderer.Stride; position is the first three floats
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    /// <summary>One triangle standing at x 500..510, z 0..10.</summary>
    private static MeshAsset Body() => new()
    {
        Positions = new float[] { 500, 0, 0, 510, 0, 0, 500, 0, 10 },
        Normals = new float[9],
        Uvs = new float[6],
        Indices = new uint[] { 0, 1, 2 },
        SubMeshes = new[] { new SubMeshInfo("Body", 0, 3, 3) },
        VertexCount = 3,
    };

    private static VfxEmitterDefinition Emitter(VfxEmissionSurface? surface) => new(
        Name: "e", Rate: VfxCurveF.Const(200f), ParticleLifetime: VfxCurveF.Const(5f), EmitterLifetime: null,
        ParticleLinger: 0f, TimeBeforeFirstEmission: 0f, IsSingleParticle: false, Disabled: false, BlendMode: 1,
        BirthScale: VfxCurve3.Const(new Vector3(10f)), ScaleOverLife: null, BirthColor: VfxCurve4.Const(Vector4.One),
        ColorOverLife: null, BirthVelocity: null, Acceleration: null, BirthRotationalVelocity: null,
        EmitterPosition: VfxCurve3.Const(new Vector3(0, 0, 100)), TexturePath: "ASSETS/t.dds", TexDiv: Vector2.One,
        NumFrames: 1, RandomStartFrame: false, IsMeshPrimitive: false, EmissionSurface: surface);

    private static VfxSystemDefinition System(VfxEmissionSurface? surface) =>
        new(PathHash: 1, Name: "s", ParticlePath: "", Emitters: new[] { Emitter(surface) });

    private static List<Vector3> Run(VfxSystemDefinition system, VfxHostSurface? host, Matrix4x4 placement)
    {
        var item = new VfxPlaybackItem(system, placement, new TextureImage?[1]);
        var sim = VfxPlaybackSim.Create(item)!;
        VfxPlaybackSim.AttachHost(sim, host);
        sim.Update(0.25f);
        var e = sim.Emitters.Single();
        return Enumerable.Range(0, e.InstanceCount)
            .Select(i => new Vector3(e.Instances[i * Stride], e.Instances[i * Stride + 1], e.Instances[i * Stride + 2])).ToList();
    }

    private static readonly VfxEmissionSurface HostSurface = new(VfxEmissionSurfaceKind.Host);

    [Fact]
    public void AHostEmitterIsBornOnTheBodyWhereverTheEffectStands()
    {
        var host = VfxHostSurface.For(Body())!;
        // the effect is placed far away and lifted, as a rig does; the body is where the body is
        var at = Run(System(HostSurface), host, Matrix4x4.CreateTranslation(-3000, 100, 0));
        Assert.NotEmpty(at);
        Assert.All(at, p =>
        {
            Assert.InRange(p.X, 500f, 510.001f);
            Assert.InRange(p.Z, 0f, 10.001f);
            Assert.Equal(0f, p.Y, 3);
        });
    }

    [Fact]
    public void WithoutAHostItEmitsFromItsPointAsBefore()
    {
        var at = Run(System(HostSurface), null, Matrix4x4.CreateTranslation(-3000, 100, 0));
        Assert.All(at, p => Assert.Equal(new Vector3(-3000, 100, 100), p));
    }

    [Fact]
    public void AHostNeverReachesAnEmitterThatNamesItsOwnSurfaceOrNone()
    {
        var host = VfxHostSurface.For(Body())!;
        Assert.All(Run(System(null), host, Matrix4x4.Identity), p => Assert.Equal(new Vector3(0, 0, 100), p));
    }

    [Fact]
    public void ThePlacementMovesTheBody()
    {
        var host = VfxHostSurface.For(Body())!;
        host.Pose(null, null, 0f, Matrix4x4.CreateTranslation(0, 250, 0));   // the character walked up a step
        var at = Run(System(HostSurface), host, Matrix4x4.Identity);
        Assert.All(at, p => Assert.Equal(250f, p.Y, 3));
    }

    [Fact]
    public void OnlyAPlaybackWithAHostEmitterAsksForOne()
    {
        TextureImage?[] none = new TextureImage?[1];
        var plain = new VfxPlaybackItem(System(null), Vector3.Zero, none);
        var hosted = new VfxPlaybackItem(System(HostSurface), Vector3.Zero, none);
        Assert.False(VfxPlaybackSim.NeedsHost(new VfxPlayback(new[] { plain })));
        Assert.True(VfxPlaybackSim.NeedsHost(new VfxPlayback(new[] { plain, hosted })));
        Assert.False(VfxPlaybackSim.NeedsHost(null));

        // a child system born on the body counts too - it is spawned mid-run and must find the host
        var parent = plain with { EmitterChildren = new IReadOnlyList<VfxPlaybackItem>?[] { new[] { hosted } } };
        Assert.True(VfxPlaybackSim.NeedsHost(new VfxPlayback(new[] { parent })));
    }

    [Fact]
    public void ARealSkinPosedThroughARealClipMovesTheBirthPoints()
    {
        const string wad = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Champions\Akali.wad.client";
        if (!File.Exists(wad)) return;
        var db = new ReyEngine.Core.Hashing.HashSyncService().LoadLocal(_ => { });
        using var archive = ReyEngine.Core.Wad.WadArchive.Open(wad, new WadPathResolver(db));
        WadAssetEntry? Find(string suffix, string folder) => archive.Entries.FirstOrDefault(x => x.IsResolved
            && x.Path.Contains(folder, StringComparison.OrdinalIgnoreCase) && x.Path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        var skn = Find(".skn", "/skins/base/");
        var skl = Find(".skl", "/skins/base/");
        var anm = archive.Entries.FirstOrDefault(x => x.IsResolved && x.Path.Contains("/skins/base/animations/", StringComparison.OrdinalIgnoreCase)
            && x.Path.Contains("run", StringComparison.OrdinalIgnoreCase) && x.Path.EndsWith(".anm", StringComparison.OrdinalIgnoreCase));
        Assert.True(skn is not null && skl is not null && anm is not null, "Akali's base skin, skeleton and a run clip");

        var mesh = SkinnedMeshDecoder.Decode(archive.Extract(skn!));
        var skeleton = ReyEngine.Formats.Skeletons.SkeletonDecoder.Decode(archive.Extract(skl!));
        var clip = ReyEngine.Formats.Animation.AnimationDecoder.Decode(archive.Extract(anm!), Path.GetFileName(anm!.Path));
        Assert.True(mesh.CanSkin);

        var host = VfxHostSurface.For(mesh)!;
        (Vector3 Min, Vector3 Max) Bounds(int seed)
        {
            var rng = new Random(seed);
            var pts = Enumerable.Range(0, 4000).Select(_ => host.Sampler.Sample(rng)).ToList();
            return (pts.Aggregate(Vector3.Min), pts.Aggregate(Vector3.Max));
        }
        host.Pose(skeleton, null, 0f, Matrix4x4.Identity);
        var bind = Bounds(1);
        host.Pose(skeleton, clip, clip.Duration * 0.37f, Matrix4x4.CreateTranslation(1000, 0, 0));
        var running = Bounds(1);

        // the same body, a champion tall, carried to where the placement put it...
        Assert.InRange(running.Min.X, 800f, 1200f);
        Assert.InRange(bind.Max.Y - bind.Min.Y, 100f, 400f);
        // ...and in a different pose: the run's extent is not the bind pose's shifted by 1000
        var shifted = (bind.Min + new Vector3(1000, 0, 0), bind.Max + new Vector3(1000, 0, 0));
        Assert.True(Vector3.Distance(running.Min, shifted.Item1) + Vector3.Distance(running.Max, shifted.Item2) > 5f,
            $"bind {bind} vs run {running}");
    }

    // ------------------------------------------------------------------ the particle editor

    private static byte[] Bin()
    {
        var onHost = new BinTreeStruct(0, H("VfxEmitterDefinitionData"), new BinTreeProperty[]
        {
            new BinTreeString(H("emitterName"), "body"),
            new BinTreeString(H("texture"), "ASSETS/t.dds"),
            new BinTreeStruct(H("emissionSurfaceDefinition"), H("VfxEmissionSurfaceData"), Array.Empty<BinTreeProperty>()),
        });
        var system = new BinTreeObject(H("sys"), H("VfxSystemDefinitionData"), new BinTreeProperty[]
        {
            new BinTreeString(H("particleName"), "sys"),
            new BinTreeContainer(H("complexEmitterDefinitionData"), BinPropertyType.Struct, new BinTreeProperty[] { onHost }),
        });
        using var ms = new MemoryStream();
        new BinTree(new[] { system }, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    private static ParticleEditorViewModel Open()
    {
        var vm = new ParticleEditorViewModel();
        Assert.True(vm.Load(new WadAssetEntry { Path = "particles.bin" }, Bin(), editable: true));
        return vm;
    }

    [Fact]
    public void TheEditorBorrowsTheCharacterWindowsModel()
    {
        var vm = Open();
        var card = vm.Cards.Single(c => c.Name == "body");
        Assert.Equal(1, vm.HostEmitterCount);
        Assert.Contains("Pick a character host", card.EmissionNote);
        Assert.Contains("Pick a host", vm.HostHint);

        var body = Body();
        vm.ResolveHost = () => new ParticleHostModel("Akali", body, null, null, null, null);
        vm.UseCharacterWindowModelCommand.Execute(null);

        Assert.True(vm.HasHost);
        Assert.Same(body, vm.HostMesh);
        Assert.Equal("Akali - bind pose", vm.HostStatus);
        Assert.Contains("the host Akali", vm.Cards.Single(c => c.Name == "body").EmissionNote);
        Assert.Contains("emits from the host", vm.HostHint);

        vm.ClearHostCommand.Execute(null);
        Assert.False(vm.HasHost);
        Assert.Null(vm.HostMesh);
    }

    [Fact]
    public void AnEmptyCharacterWindowSaysSo()
    {
        var vm = Open();
        string? error = null;
        vm.Error = e => error = e;
        vm.ResolveHost = () => null;
        vm.UseCharacterWindowModelCommand.Execute(null);
        Assert.False(vm.HasHost);
        Assert.Contains("character window", error);
    }
}
