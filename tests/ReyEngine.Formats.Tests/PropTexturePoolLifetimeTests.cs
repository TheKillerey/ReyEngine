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
/// M781: switching props off and on, or dragging one, crashed the editor inside the NVIDIA D3D11 driver
/// (nvwgf2umx, access violation) on 7yanniversary.
///
/// <para>The texture pool is keyed by ASSET PATH and every scene owner binds through TryBindCached first,
/// so the map, the particles and the props share one view per file. D3D11MapProps.Clear handed every key
/// of its character scenes to RemoveCachedTextures, which disposed the views outright - including the
/// map's <c>assets/shared/materials/black.tex</c> and the particles' boat and pengu sprites, still bound
/// by 42 map and particle material slots. The next PSSetShaderResources read freed memory (measured
/// headless on the real map: d3d11 SetShaderResources on the 0xFEEEFEEE fill, the view the props had just
/// released).</para>
///
/// <para>Real device, real skin (Locke stands in for a prop, as in PropRiotShaderTests) and the real
/// particle pipeline, with the particle's sprite pointed at one of the skin's own textures - in both
/// orders, because either owner can be the one that created the shared view.</para>
/// </summary>
public sealed class PropTexturePoolLifetimeTests
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";

    private static readonly Lazy<HashDatabase?> Database = new(() =>
    {
        try { return new HashSyncService().LoadLocal(_ => { }); }
        catch { return null; }
    });

    private sealed record Fixture(byte[] Skn, byte[] SkinBin, ShaderCacheReader Cache, WadArchive Archive,
        HashDatabase Database, ShaderPermutationIndex Perms) : IDisposable
    {
        public string? BinName(uint h) => Database.TryGetBinName(h, out var n) ? n : null;
        public string? WadPath(ulong h) => Database.TryGetPath(h, out var p) ? p : null;
        public byte[]? Read(ulong h) => Archive.TryGetEntry(h, out _) ? Archive.Extract(h) : null;
        public void Dispose() { Cache.Dispose(); Archive.Dispose(); }
    }

    private static Fixture? Locke()
    {
        string wad = Path.Combine(Final, "Champions", "Locke.wad.client");
        if (!File.Exists(wad) || Database.Value is not { } database) return null;
        var resolver = new WadPathResolver(database);
        var archive = WadArchive.Open(wad, resolver);
        var cache = ShaderCacheReader.Open(Final, resolver, out _);
        ulong sknHash = HashAlgorithms.WadPath("assets/characters/locke/skins/base/locke_base.skn");
        ulong binHash = HashAlgorithms.WadPath("data/characters/locke/skins/skin0.bin");
        byte[]? skn = archive.TryGetEntry(sknHash, out _) ? archive.Extract(sknHash) : null;
        byte[]? bin = archive.TryGetEntry(binHash, out _) ? archive.Extract(binHash) : null;
        if (cache is null || skn is null || bin is null) { archive.Dispose(); cache?.Dispose(); return null; }
        return new Fixture(skn, bin, cache, archive, database, new ShaderPermutationIndex(Final));
    }

    [Fact]
    public void PropsBorrowingAParticlesViewLeaveItPooledThroughOffOnAndAMove()
        => OffOnMove(particlesFirst: true);

    [Fact]
    public void AParticleBorrowingThePropsViewKeepsItThroughOffOnAndAMove()
        => OffOnMove(particlesFirst: false);

    private static void OffOnMove(bool particlesFirst)
    {
        if (Locke() is not { } f) return;
        using (f)
        {
            var decoded = SkinnedMeshDecoder.Decode(f.Skn);
            PreparedCharacterScene? Prepare(PropMesh m) => Dx11CharacterScene.Prepare(m.SknBytes!, m.SkinBinBytes,
                f.Cache, f.Perms, f.Read, f.BinName, f.WadPath, Dx11CharacterScene.DefaultCharacterShader, decodedMesh: m.SknMesh);

            var mesh = new PropMesh("locke", decoded.Positions, decoded.Normals, decoded.Uvs, decoded.Indices,
                decoded.SubMeshes.Select(s => new PropSubmesh(s.StartIndex, s.IndexCount, null)).ToList())
            { SknMesh = decoded, SknBytes = f.Skn, SkinBinBytes = f.SkinBin };
            var probe = Prepare(mesh);
            Assert.NotNull(probe);
            var keys = probe!.Textures.Keys.Where(k => !k.StartsWith("reyengine://", StringComparison.Ordinal)).ToList();
            Assert.True(keys.Count >= 2, $"Locke's scene should decode at least two textures ({keys.Count})");
            string shared = keys[0];      // also the particle's sprite
            string propsOnly = keys[1];   // nobody else binds it

            // a billboard whose sprite is the SAME asset path, as 7yanniversary's boat and pengu sprites are
            var emitter = new VfxEmitterDefinition(
                Name: "sharedSprite", Rate: VfxCurveF.Const(1f), ParticleLifetime: VfxCurveF.Const(10f),
                EmitterLifetime: null, ParticleLinger: 0f, TimeBeforeFirstEmission: 0f, IsSingleParticle: true,
                Disabled: false, BlendMode: 3,
                BirthScale: VfxCurve3.Const(new Vector3(50f)), ScaleOverLife: null,
                BirthColor: VfxCurve4.Const(Vector4.One), ColorOverLife: null,
                BirthVelocity: null, Acceleration: null, BirthRotationalVelocity: null,
                EmitterPosition: VfxCurve3.Const(Vector3.Zero),
                TexturePath: shared, TexDiv: Vector2.One, NumFrames: 1, RandomStartFrame: false,
                IsMeshPrimitive: false);
            var system = new VfxSystemDefinition(0x781, "SharedSpriteProbe", "test/m781", new[] { emitter });
            var sprite = new TextureImage(1, 1, new byte[] { 255, 255, 255, 255 });
            var playback = new VfxPlayback(new[] { new VfxPlaybackItem(system, Matrix4x4.Identity, new TextureImage?[] { sprite }) });

            PropRenderSet At(float x) => new(new[] { PropInstanceData.Place(mesh, Matrix4x4.CreateTranslation(x, 0f, 0f)) });

            using var renderer = new ShaderPreviewRenderer();
            if (!renderer.Initialize(out _)) return;   // no D3D11 device on this machine
            var particles = new D3D11MapParticles(renderer, f.Cache);
            var props = new D3D11MapProps(renderer, f.Cache);

            var eye = new Vector3(0f, 150f, 600f);
            var view = Matrix4x4.CreateLookAt(eye, new Vector3(0f, 100f, 0f), Vector3.UnitY);
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(0.9f, 1f, 1f, 5000f);
            void Frames(int n)
            {
                for (int i = 0; i < n; i++)
                {
                    props.Tick(i / 60f, true, eye, VfxPlaybackSim.MaxDistanceSquared(600f));
                    particles.Tick(1f / 60f, view, view * proj, eye, 600f);
                    var frame = renderer.RenderFrame(160, 160, new PreviewSettings
                    {
                        SuppliedView = view, SuppliedProjection = proj, SuppliedCameraPosition = eye,
                        AlphaBlend = true, DepthTest = true, CullBackFaces = true, SortByPipeline = true,
                        ClearColor = new Vector4(0.039f, 0.051f, 0.075f, 1f), TimeSeconds = i / 60f,
                    }, out var error);
                    Assert.True(frame is not null, "RenderFrame failed: " + error);
                }
            }
            void LoadProps(PropRenderSet? set)
            {
                props.Load(set, Prepare);
                for (int guard = 0; props.UploadsPending > 0 && guard < 50; guard++) Frames(1);
                Assert.Equal(0, props.UploadsPending);
            }

            if (particlesFirst)
            {
                particles.SetPlayback(playback);
                Frames(3);
                Assert.True(renderer.IsCached(shared), "the particle should have pooled its sprite first");
                LoadProps(At(0f));   // CommitSlices binds the particle's view by TryBindCached
            }
            else
            {
                LoadProps(At(0f));
                Assert.True(renderer.IsCached(shared), "the props should have pooled the texture first");
                particles.SetPlayback(playback);   // the rebuild binds the props' view by TryBindCached
                Frames(3);
            }
            Assert.Equal(1, props.RiotShaderMeshes);
            Assert.True(particles.DrawSlices > 0, "the particle should have built its draw");
            Frames(2);

            // props OFF: the particle still binds the shared view, so it must stay pooled - checked BEFORE a
            // frame is drawn, because before M781 that frame bound a freed view and could take the process
            // down instead of failing. The props' own texture is still released, as it always was.
            LoadProps(null);
            Assert.True(renderer.IsCached(shared), $"props OFF released '{shared}', which the particle still binds");
            Assert.False(renderer.IsCached(propsOnly), $"props OFF should still release its own '{propsOnly}'");
            Frames(3);

            // props ON again, then a drag: every drag frame republishes the set (Load -> Clear -> re-upload)
            LoadProps(At(0f));
            Assert.True(renderer.IsCached(propsOnly), "props ON should upload their own texture again");
            Frames(2);
            for (int step = 1; step <= 3; step++)
            {
                props.Load(At(40f * step), Prepare);
                Assert.True(renderer.IsCached(shared), $"drag step {step} released '{shared}', which the particle still binds");
                Frames(2);
            }
            LoadProps(At(200f));
            Assert.Equal(1, props.PropInstanceCount);
            Assert.True(renderer.IsCached(shared));

            // and a particle move (EndPlacementDrag -> RebuildParticlePlayback) with props on
            particles.SetPlayback(new VfxPlayback(new[] { new VfxPlaybackItem(system,
                Matrix4x4.CreateTranslation(30f, 0f, 0f), new TextureImage?[] { sprite }) }));
            Frames(3);
            LoadProps(null);
            Assert.True(renderer.IsCached(shared), "the rebuilt particle binds the shared view too");
            Frames(2);
        }
    }
}
