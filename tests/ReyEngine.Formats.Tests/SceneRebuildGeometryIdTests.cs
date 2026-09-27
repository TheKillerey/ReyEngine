using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Shaders;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.D3D11;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M782: a D3D11 scene rebuild (a map change, a material edit - Dx11SceneBuilder.Commit, whose ClearMaterials
/// releases every mesh geometry) left D3D11MapParticles holding the geometry ids of its mesh emitters. On the
/// next frame the props uploaded first, into the freshly emptied stores, and the particle rebuild then
/// "released" its old ids - which by then named the props' new geometry - and re-used those slots for its own
/// meshes. Measured on 7yanniversary: 2 of 14 prop meshes drew particle meshes after a rebuild.
///
/// <para>Both geometry stores are covered: Riot's (a placed prop on its skin's shaders, a static mesh emitter on
/// mesh_vs/mesh_ps) and the renderer's own mesh pipeline (a diffuse-only prop, an M283 mesh emitter). The
/// sequence is the viewport's own: particles playing before the props, then ClearMaterials, then
/// NotifySceneRebuilt's Invalidate + props Load, then frames in Dx11ViewportSurface.Render's order - props
/// tick (and upload) before particles tick (and rebuild).</para>
/// </summary>
public sealed class SceneRebuildGeometryIdTests
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private const int Size = 256;
    private static readonly Vector4 Magenta = new(1f, 0f, 1f, 1f);

    private static readonly Lazy<HashDatabase?> Database = new(() =>
    {
        try { return new HashSyncService().LoadLocal(_ => { }); }
        catch { return null; }
    });

    // ===================================================== Riot's store: a skin prop and a mesh_vs emitter

    [Fact]
    public void AfterASceneRebuildThePropStillDrawsItsOwnSkinnedMesh()
    {
        string wad = Path.Combine(Final, "Champions", "Locke.wad.client");
        if (!File.Exists(wad) || Database.Value is not { } database) return;
        var resolver = new WadPathResolver(database);
        using var archive = WadArchive.Open(wad, resolver);
        using var cache = ShaderCacheReader.Open(Final, resolver, out _);
        if (cache is null) return;
        byte[]? Read(ulong h) => archive.TryGetEntry(h, out _) ? archive.Extract(h) : null;
        var skn = Read(HashAlgorithms.WadPath("assets/characters/locke/skins/base/locke_base.skn"));
        var bin = Read(HashAlgorithms.WadPath("data/characters/locke/skins/skin0.bin"));
        if (skn is null || bin is null) return;
        var perms = new ShaderPermutationIndex(Final);

        var decoded = SkinnedMeshDecoder.Decode(skn);
        var mesh = new PropMesh("locke", decoded.Positions, decoded.Normals, decoded.Uvs, decoded.Indices,
            decoded.SubMeshes.Select(s => new PropSubmesh(s.StartIndex, s.IndexCount, null)).ToList())
        { SknMesh = decoded, SknBytes = skn, SkinBinBytes = bin };
        PreparedCharacterScene? Prepare(PropMesh m) => Dx11CharacterScene.Prepare(m.SknBytes!, m.SkinBinBytes, cache, perms,
            Read, h => database.TryGetBinName(h, out var n) ? n : null, h => database.TryGetPath(h, out var p) ? p : null,
            Dx11CharacterScene.DefaultCharacterShader, decodedMesh: m.SknMesh);

        var centre = (decoded.BoundsMin + decoded.BoundsMax) * 0.5f;
        float radius = (decoded.BoundsMax - decoded.BoundsMin).Length() * 0.5f;
        var eye = new Vector3(0f, centre.Y, radius * 3f);
        var view = Matrix4x4.CreateLookAt(eye, new Vector3(0f, centre.Y, 0f), Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(0.9f, 1f, radius * 0.02f, radius * 40f);

        var result = RebuildAndCompare(cache, riotMeshShaders: true, eye, view, proj,
            new PropRenderSet(new[] { PropInstanceData.Place(mesh, Matrix4x4.Identity) }), Prepare);
        if (result is not { } r) return;   // no D3D11 device on this machine
        Assert.Equal(1, r.RiotEmitters);    // the emitter really is on Riot's store, the one the prop uses
        Assert.Equal(1, r.RiotProps);
        Assert.True(r.Before > 500, $"the prop should cover the frame before the rebuild ({r.Before}px)");
        Assert.True(Math.Abs(r.After - r.Before) <= r.Before / 100 + 16,
            $"after the scene rebuild the prop should draw exactly as before ({r.Before}px -> {r.After}px); a particle "
            + "rebuild that released its pre-rebuild geometry ids freed the prop's new mesh and put its own in the slot");
        Assert.True(Math.Abs(r.AfterUncommitted - r.Before) <= r.Before / 100 + 16,
            $"after an uncommitted rebuild the prop should draw exactly as before ({r.Before}px -> {r.AfterUncommitted}px)");
    }

    // ===================================================== the renderer's store: a diffuse-only prop and an M283 emitter

    [Fact]
    public void AfterASceneRebuildTheDiffuseOnlyPropKeepsItsGeometry()
    {
        if (!Directory.Exists(Final) || Database.Value is not { } database) return;
        using var cache = ShaderCacheReader.Open(Final, new WadPathResolver(database), out _);
        if (cache is null) return;

        // a 200-unit card facing the camera, both windings (props cull their back faces)
        float[] positions = { -100, -100, 0, 100, -100, 0, 100, 100, 0, -100, 100, 0 };
        float[] uvs = { 0, 1, 1, 1, 1, 0, 0, 0 };
        uint[] indices = { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 };
        var white = new TextureImage(1, 1, new byte[] { 255, 255, 255, 255 });
        var card = new PropMesh("card", positions, new float[positions.Length], uvs, indices,
            new[] { new PropSubmesh(0, indices.Length, white) });   // no .skn bytes: the diffuse-only draw

        var eye = new Vector3(0f, 0f, 400f);
        var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(0.9f, 1f, 1f, 5000f);

        var result = RebuildAndCompare(cache, riotMeshShaders: false, eye, view, proj,
            new PropRenderSet(new[] { PropInstanceData.Place(card, Matrix4x4.Identity) }), prepare: null);
        if (result is not { } r) return;
        Assert.Equal(0, r.RiotEmitters);   // the M283 path: the emitter shares the renderer's mesh store with the card
        Assert.Equal(0, r.RiotProps);
        Assert.Equal(2, r.GeometriesBefore);
        Assert.Equal(2, r.GeometriesAfter);   // the card's and the emitter's - not one slot fought over by both
        Assert.True(r.Before > 500, $"the card should cover the frame before the rebuild ({r.Before}px)");
        Assert.True(Math.Abs(r.After - r.Before) <= r.Before / 100 + 16,
            $"after the scene rebuild the card should draw exactly as before ({r.Before}px -> {r.After}px)");
        // NotifySceneRebuilt also runs when the scene build returned BEFORE its commit (no map, no cache): the
        // emitter's ids are still live then, and its rebuild must release them rather than leak a copy
        Assert.Equal(2, r.GeometriesAfterUncommitted);
        Assert.True(Math.Abs(r.AfterUncommitted - r.Before) <= r.Before / 100 + 16,
            $"after an uncommitted rebuild the card should draw exactly as before ({r.Before}px -> {r.AfterUncommitted}px)");
    }

    // ===================================================== the viewport's sequence

    private readonly record struct Outcome(int Before, int After, int GeometriesBefore, int GeometriesAfter,
        int RiotEmitters, int RiotProps, int AfterUncommitted, int GeometriesAfterUncommitted);

    private static Outcome? RebuildAndCompare(ShaderCacheReader cache, bool riotMeshShaders,
        Vector3 eye, Matrix4x4 view, Matrix4x4 proj, PropRenderSet props, Func<PropMesh, PreparedCharacterScene?>? prepare)
    {
        using var renderer = new ShaderPreviewRenderer();
        if (!renderer.Initialize(out _)) return null;
        var particles = new D3D11MapParticles(renderer, cache) { UseRiotMeshShaders = riotMeshShaders };
        var propDriver = new D3D11MapProps(renderer, cache);

        // a small static mesh emitter, placed well out of view: only its GEOMETRY takes part
        float[] tri = { 0, 0, 0, 10, 0, 0, 0, 10, 0 };
        var particleMesh = new StaticMeshData(tri, new float[] { 0, 0, 1, 0, 0, 1 }, new uint[] { 0, 1, 2 }, "tri");
        var emitter = new VfxEmitterDefinition(
            Name: "meshEmitter", Rate: VfxCurveF.Const(1f), ParticleLifetime: VfxCurveF.Const(10f),
            EmitterLifetime: null, ParticleLinger: 0f, TimeBeforeFirstEmission: 0f, IsSingleParticle: true,
            Disabled: false, BlendMode: 3,
            BirthScale: VfxCurve3.Const(Vector3.One), ScaleOverLife: null,
            BirthColor: VfxCurve4.Const(Vector4.One), ColorOverLife: null,
            BirthVelocity: null, Acceleration: null, BirthRotationalVelocity: null,
            EmitterPosition: VfxCurve3.Const(Vector3.Zero),
            TexturePath: "ASSETS/Test/m782.dds", TexDiv: Vector2.One, NumFrames: 1, RandomStartFrame: false,
            IsMeshPrimitive: true, MeshPath: "ASSETS/Test/m782.scb");
        var system = new VfxSystemDefinition(0x782, "SceneRebuildProbe", "test/m782", new[] { emitter });
        var sprite = new TextureImage(1, 1, new byte[] { 255, 255, 255, 255 });
        particles.SetPlayback(new VfxPlayback(new[] { new VfxPlaybackItem(system,
            Matrix4x4.CreateTranslation(0f, 0f, 1_000_000f), new TextureImage?[] { sprite },
            new StaticMeshData?[] { particleMesh }) }));

        var mirroredView = Matrix4x4.CreateScale(-1f, 1f, 1f) * view;
        int Frame()
        {
            // Dx11ViewportSurface.Render's order: props first (the upload pump), then particles (the rebuild)
            propDriver.Tick(1f, true, eye, VfxPlaybackSim.MaxDistanceSquared(1000f));
            particles.Tick(1f / 60f, mirroredView, mirroredView * proj, eye, 1000f);
            var frame = renderer.RenderFrame(Size, Size, new PreviewSettings
            {
                SuppliedView = view, SuppliedProjection = proj, SuppliedCameraPosition = eye,
                AlphaBlend = true, DepthTest = true, CullBackFaces = true, SortByPipeline = true,
                MirrorX = true, TransposeMatrices = true, Bloom = false, Shadows = false,
                ClearColor = Magenta, TimeSeconds = 1f,
            }, out var error);
            Assert.True(frame is not null, "RenderFrame failed: " + error);
            var px = frame!.ToArray();
            int covered = 0;
            for (int i = 0; i + 3 < px.Length; i += 4)   // BGRA; the clear is (255, 0, 255)
                if (Math.Abs(px[i] - 255) > 12 || px[i + 1] > 12 || Math.Abs(px[i + 2] - 255) > 12) covered++;
            return covered;
        }
        void Settle(int n) { for (int i = 0; i < n; i++) Frame(); }

        // the particles play first, so their mesh geometry takes the first slot of its store
        Settle(2);
        propDriver.Load(props, prepare);
        for (int guard = 0; propDriver.UploadsPending > 0 && guard < 20; guard++) Frame();
        Settle(2);
        int before = Frame();
        int geometriesBefore = renderer.MeshGeometryCount;

        // Dx11SceneBuilder.Commit's ClearMaterials, then Dx11ViewportSurface.NotifySceneRebuilt, in its order
        renderer.ClearMaterials();
        particles.Invalidate();
        propDriver.Load(props, prepare);
        for (int guard = 0; propDriver.UploadsPending > 0 && guard < 20; guard++) Frame();
        Settle(2);
        int after = Frame();
        int geometriesAfter = renderer.MeshGeometryCount;
        int riotEmitters = particles.RiotMeshEmitters, riotProps = propDriver.RiotShaderMeshes;

        // NotifySceneRebuilt with NO commit before it - BuildDx11SceneAsync returns early without one
        particles.Invalidate();
        propDriver.Load(props, prepare);
        for (int guard = 0; propDriver.UploadsPending > 0 && guard < 20; guard++) Frame();
        Settle(2);
        int afterUncommitted = Frame();

        return new Outcome(before, after, geometriesBefore, geometriesAfter, riotEmitters, riotProps,
            afterUncommitted, renderer.MeshGeometryCount);
    }
}
