using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Shaders;
using ReyEngine.Rendering.D3D11;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M676: placed props (mobs, camps, Baron) drawn through Riot's own character shaders in the D3D11 map
/// viewport, once per placement, with the skin's scale applied.
///
/// <para>The renderer half is exercised on a real device with a real skin: the same scene the character
/// window prepares, registered over a skinned geometry of its own with two placements, must cover both
/// halves of the frame, and with one placement only one - the proof that the world is per placement and
/// not the frame's. The wiring half is pinned as text, because it is one line in each of four files and
/// any one of them missing reads as "the feature does nothing".</para>
/// </summary>
public sealed class PropRiotShaderTests
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";

    private static readonly Lazy<HashDatabase?> Database = new(() =>
    {
        try { return new HashSyncService().LoadLocal(_ => { }); }
        catch { return null; }
    });

    private sealed record Fixture(byte[] Skn, byte[] SkinBin, ShaderCacheReader Cache, WadArchive Archive, HashDatabase Database) : IDisposable
    {
        public string? BinName(uint h) => Database.TryGetBinName(h, out var n) ? n : null;
        public string? WadPath(ulong h) => Database.TryGetPath(h, out var p) ? p : null;
        public byte[]? Read(ulong h) => Archive.TryGetEntry(h, out _) ? Archive.Extract(h) : null;
        public void Dispose() { Cache.Dispose(); Archive.Dispose(); }
    }

    /// <summary>A champion stands in for a mob here: a mob IS a character skin, and Locke's is the one
    /// whose D3D11 scene the suite already pins (OnsenCharacterTests).</summary>
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
        return new Fixture(skn, bin, cache, archive, database);
    }

    // ===================================================== the picture

    [Fact]
    public void APropDrawsOncePerPlacementWithThatPlacementAsItsWorld()
    {
        if (Locke() is not { } f) return;
        using (f)
        {
            var scene = Dx11CharacterScene.Prepare(f.Skn, f.SkinBin, f.Cache, new ShaderPermutationIndex(Final),
                f.Read, f.BinName, f.WadPath, fallbackShader: Dx11CharacterScene.DefaultCharacterShader);
            Assert.NotNull(scene);
            Assert.NotEmpty(scene!.Slices);

            var mesh = SkinnedMeshDecoder.Decode(f.Skn);
            var centre = (mesh.BoundsMin + mesh.BoundsMax) * 0.5f;
            float radius = (mesh.BoundsMax - mesh.BoundsMin).Length() * 0.5f;
            float apart = radius * 1.2f;
            var eye = new Vector3(0f, centre.Y, radius * 4f);
            var view = Matrix4x4.CreateLookAt(eye, new Vector3(0f, centre.Y, 0f), Vector3.UnitY);
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(0.9f, 1f, radius * 0.02f, radius * 40f);
            const int Size = 320;

            (long Left, long Right)? Halves(IReadOnlyList<Matrix4x4> placements)
            {
                using var renderer = new ShaderPreviewRenderer();
                if (!renderer.Initialize(out _)) return null;
                int gid = renderer.CreateRiotMeshGeometry(scene.Mesh);
                Assert.True(gid >= 0, "the skinned geometry did not upload");
                var mats = Dx11CharacterScene.CommitSlices(renderer, scene, mat =>
                {
                    mat.RiotMeshGeometryId = gid;
                    mat.CharacterInstances = placements;
                });
                Assert.NotEmpty(mats);
                var s = new PreviewSettings
                {
                    SuppliedView = view, SuppliedProjection = proj, SuppliedCameraPosition = eye,
                    AlphaBlend = true, DepthTest = true, MirrorX = true, TransposeMatrices = true,
                    CullBackFaces = true, SortByPipeline = true,
                    ClearColor = new Vector4(0.039f, 0.051f, 0.075f, 1f), TimeSeconds = 1f,
                };
                var frame = renderer.RenderFrame(Size, Size, s, out _);
                if (frame is null) return null;
                long left = 0, right = 0;
                for (int y = 0; y < Size; y++)
                    for (int x = 0; x < Size; x++)
                    {
                        int i = (y * Size + x) * 4;
                        int a = frame[i], b = frame[i + 1], c = frame[i + 2];
                        bool clear = (Math.Abs(a - 10) <= 6 && Math.Abs(b - 13) <= 6 && Math.Abs(c - 19) <= 6)
                                     || (Math.Abs(a - 19) <= 6 && Math.Abs(b - 13) <= 6 && Math.Abs(c - 10) <= 6);
                        if (clear) continue;
                        if (x < Size / 2 - 8) left++; else if (x > Size / 2 + 8) right++;
                    }
                return (left, right);
            }

            var two = Halves(new[] { Matrix4x4.CreateTranslation(-apart, 0f, 0f), Matrix4x4.CreateTranslation(apart, 0f, 0f) });
            var one = Halves(new[] { Matrix4x4.CreateTranslation(-apart, 0f, 0f) });
            if (two is null || one is null) return;   // no D3D11 device on this machine

            Assert.True(two.Value.Left > 200 && two.Value.Right > 200, $"two placements should cover both halves: {two}");
            // one placement covers one half only: the empty half is the proof the world is per placement
            Assert.True((one.Value.Left > 200) != (one.Value.Right > 200), $"one placement should cover one half: {one}");
        }
    }

    // ===================================================== the scale

    [Fact]
    public void ThePlacementComposesTheSkinsOwnScaleUnderIt()
    {
        var geometry = (new float[] { 0, 0, 0 }, new float[] { 0, 1, 0 }, new float[] { 0, 0 }, new uint[] { 0, 0, 0 });
        var placement = Matrix4x4.CreateRotationY(0.5f) * Matrix4x4.CreateTranslation(100f, 0f, 50f);

        var scaled = new PropMesh("k", geometry.Item1, geometry.Item2, geometry.Item3, geometry.Item4, Array.Empty<PropSubmesh>()) { SkinScale = 0.8f };
        Assert.Equal(Matrix4x4.CreateScale(0.8f) * placement, PropInstanceData.Place(scaled, placement).Transform);

        var plain = new PropMesh("k", geometry.Item1, geometry.Item2, geometry.Item3, geometry.Item4, Array.Empty<PropSubmesh>());
        Assert.Equal(1f, plain.SkinScale);
        Assert.Equal(placement, PropInstanceData.Place(plain, placement).Transform);

        // an authored zero is nonsense, not a request to vanish
        var zero = new PropMesh("k", geometry.Item1, geometry.Item2, geometry.Item3, geometry.Item4, Array.Empty<PropSubmesh>()) { SkinScale = 0f };
        Assert.Equal(placement, PropInstanceData.Place(zero, placement).Transform);
    }

    // ===================================================== the wiring

    [Fact]
    public void TheRendererKeepsAPlacedPropsBuffersAndPaletteItsOwn()
    {
        if (Source("src", "ReyEngine.Rendering.D3D11", "ShaderPreviewRenderer.cs") is not { } text) return;
        // a placed prop never shares a constant buffer with the frame's World and palette
        Assert.Contains("bool materialSpecific = mat.CharacterInstances is not null || mat.BonePalette is not null;", text);
        // the bones fill reads the material's palette first, and the placement as its model transform
        Assert.Contains("var palette = mat?.BonePalette ?? s.BonePalette;", text);
        Assert.Contains("_ => _instanceWorld ?? s.World,", text);
        // the prop branch comes before the per-particle mesh branch that reads the same geometry id
        int prop = text.IndexOf("if (mat.CharacterInstances is { } placements && mat.RiotMeshGeometryId is { } propGeometry)", StringComparison.Ordinal);
        int emitter = text.IndexOf("if (mat.RiotMeshGeometryId is { } riotGeometry)", StringComparison.Ordinal);
        Assert.True(prop > 0 && emitter > prop, "the placed-prop branch must run ahead of the mesh-emitter branch");
    }

    [Fact]
    public void ThePropDriverPrefersRiotsShadersAndTheWindowHandsItTheWayThere()
    {
        var driver = Source("src", "ReyEngine.App", "Services", "D3D11MapProps.cs");
        var window = Source("src", "ReyEngine.App", "Views", "MainWindow.axaml.cs");
        var surface = Source("src", "ReyEngine.App", "Views", "Dx11ViewportSurface.cs");
        if (driver is null || window is null || surface is null) return;

        Assert.Contains("_renderer.CreateRiotMeshGeometry(scene.Mesh)", driver);
        Assert.Contains("Dx11CharacterScene.CommitSlices(_renderer, scene, mat =>", driver);
        Assert.Contains("BonePalette.Build(m.Skeleton!, clip, time)", driver);
        // the way to a scene is set BEFORE the prop set, whose setter loads with it
        int prepare = window.IndexOf("_dx11.PreparePropScene ??= vm.PreparePropDx11Scene;", StringComparison.Ordinal);
        int props = window.IndexOf("_dx11.PropMeshes = vm.CurrentPropMeshes;", StringComparison.Ordinal);
        Assert.True(prepare > 0 && props > prepare, "PreparePropScene must be supplied before PropMeshes is set");
        Assert.Contains("Props?.Load(value, PreparePropScene);", surface);
    }

    private static string? Source(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        return null;
    }
}
