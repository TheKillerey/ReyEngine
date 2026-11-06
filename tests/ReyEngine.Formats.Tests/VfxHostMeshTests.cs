using System.Numerics;
using System.Reflection;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Shaders;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.D3D11;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M860: a VfxPrimitiveAttachedMesh that names no mesh file draws the HOST character's own skinned body with the emitter's
/// material (62,906 emitters; Ezreal's E Avatar is one). It used to become a 1-unit billboard. The device test no-ops when the
/// game install, the hash dictionary, the shader cache or a D3D11 device is absent.
/// </summary>
public sealed class VfxHostMeshTests
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private readonly ITestOutputHelper _output;
    public VfxHostMeshTests(ITestOutputHelper output) => _output = output;

    private static VfxEmitterDefinition HostEmitter(IReadOnlyList<uint>? mask = null) => new(
        Name: "avatar",
        Rate: VfxCurveF.Const(1f),
        ParticleLifetime: VfxCurveF.Const(5f),
        EmitterLifetime: null,
        ParticleLinger: 0f,
        TimeBeforeFirstEmission: 0f,
        IsSingleParticle: true,
        Disabled: false,
        BlendMode: 4,
        BirthScale: VfxCurve3.Const(new Vector3(1.02f, 1f, 1.02f)),
        ScaleOverLife: null,
        BirthColor: VfxCurve4.Const(Vector4.One),
        ColorOverLife: null,
        BirthVelocity: null,
        Acceleration: null,
        BirthRotationalVelocity: null,
        EmitterPosition: VfxCurve3.Const(Vector3.Zero),
        TexturePath: "ASSETS/Test/avatar.dds",
        TexDiv: new Vector2(1f, 1f),
        NumFrames: 1,
        RandomStartFrame: false,
        IsMeshPrimitive: false,
        PrimitiveClass: HashAlgorithms.Fnv1a("VfxPrimitiveAttachedMesh"),
        HostSubmeshes: mask);

    private static MeshAsset Body()
    {
        // two submeshes of one triangle each, standing at the origin
        float[] pos = { 0, 0, 0, 100, 0, 0, 0, 100, 0, 0, 0, 50, 100, 0, 50, 0, 100, 50 };
        return new MeshAsset
        {
            Positions = pos, Normals = new float[18], Uvs = new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1 },
            Indices = new uint[] { 0, 1, 2, 3, 4, 5 }, VertexCount = 6,
            SubMeshes = new[] { new SubMeshInfo("body", 0, 3, 3), new SubMeshInfo("hair", 3, 3, 3) },
        };
    }

    [Fact]
    public void AnAttachedMeshWithNoFileIsAHostMesh_AndOneWithAFileIsNot()
    {
        Assert.True(HostEmitter().IsHostMesh);
        Assert.False((HostEmitter() with { IsMeshPrimitive = true, MeshPath = "ASSETS/x.scb" }).IsHostMesh);
        Assert.False((HostEmitter() with { PrimitiveClass = 0 }).IsHostMesh);
    }

    [Fact]
    public void ASubmeshMaskLimitsTheTrianglesToTheListedSubmeshes()
    {
        var mesh = Body();
        var keep = VfxEmissionSurfaceLoader.SubmeshFilter(mesh, new[] { HashAlgorithms.Fnv1a("hair") })!;
        Assert.False(keep(0));
        Assert.True(keep(1));
        Assert.Null(VfxEmissionSurfaceLoader.SubmeshFilter(mesh, null));   // no mask: every triangle
    }

    [Fact]
    public void ThePlaybackAsksForAHostBecauseOfAHostMeshEmitter()
    {
        var system = new VfxSystemDefinition(PathHash: 1, Name: "avatar", ParticlePath: "", Emitters: new[] { HostEmitter() });
        Assert.True(VfxPlaybackSim.NeedsHost(new VfxPlayback(new[] { new VfxPlaybackItem(system, Vector3.Zero, new TextureImage?[1]) })));
    }

    [Fact]
    public void ADirect3D11TickDrawsTheHostsBodyOnlyWhenThereIsAHost()
    {
        if (!Directory.Exists(Final) || Database() is not { } db) return;
        using var cache = ShaderCacheReader.Open(Final, new WadPathResolver(db), out _);
        if (cache is null) { _output.WriteLine("SKIPPED: no shader cache"); return; }
        using var renderer = new ShaderPreviewRenderer();
        if (!renderer.Initialize(out _)) { _output.WriteLine("SKIPPED: no D3D11 device"); return; }

        var system = new VfxSystemDefinition(PathHash: 1, Name: "avatar", ParticlePath: "", Emitters: new[] { HostEmitter() });
        var white = new TextureImage(4, 4, Enumerable.Repeat((byte)255, 64).ToArray());
        var item = new VfxPlaybackItem(system, Vector3.Zero, new TextureImage?[] { white });
        var driver = new D3D11MapParticles(renderer, cache);
        ((ParticleWarmupQueue)typeof(D3D11MapParticles).GetField("_warmup", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(driver)!).BudgetMs = 1e9;
        var eye = new Vector3(0f, 200f, 400f);
        var view = Matrix4x4.CreateLookAt(eye, new Vector3(0f, 50f, 0f), Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(0.9f, 1f, 5f, 30000f);
        var mirrored = Matrix4x4.CreateScale(-1f, 1f, 1f) * view;

        driver.SetPlayback(new VfxPlayback(new[] { item }));
        for (int i = 0; i < 5; i++) driver.Tick(1f / 30f, mirrored, mirrored * proj, eye, 400f);
        Assert.Equal(0, driver.HostMeshEmittersDrawn);   // no character: nothing, and no dot

        driver.SetEmissionHost(VfxHostSurface.For(Body()));
        for (int i = 0; i < 3; i++) driver.Tick(1f / 30f, mirrored, mirrored * proj, eye, 400f);
        Assert.Equal(1, driver.HostMeshEmittersDrawn);
        driver.SetPlayback(null);
        driver.Tick(0f, mirrored, mirrored * proj, eye, 400f);
    }

    private static HashDatabase? Database()
    {
        try { return new HashSyncService().LoadLocal(_ => { }); } catch { return null; }
    }
}
