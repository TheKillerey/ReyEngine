using ReyEngine.App.Services;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Shaders;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M617: resolving a champion skin into a D3D11 scene.
///
/// <para>Everything up to <c>Commit</c> is asserted here, because everything up to Commit is where the
/// answers are decided — which material draws which submesh, which cooked permutation, which texture
/// feeds which sampler. Commit itself needs a D3D device and does no deciding.</para>
///
/// <para>These run against the real shader cache and a real champion. A fixture could only prove the
/// resolver agrees with my idea of a skin bin, and the whole risk in a second implementation is that it
/// quietly disagrees with the shipped data.</para>
/// </summary>
public sealed class Dx11CharacterSceneTests
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private const string Champions = Final + @"\Champions";

    private static readonly Lazy<HashDatabase?> Database = new(() =>
    {
        try { return new HashSyncService().LoadLocal(_ => { }); }
        catch { return null; }
    });

    private sealed record Fixture(
        byte[] Skn, byte[] SkinBin, ShaderCacheReader Cache, WadArchive Archive) : IDisposable
    {
        public void Dispose() { Cache.Dispose(); Archive.Dispose(); }
    }

    /// <summary>Ahri's base skin plus the shader cache, or null when the install is unavailable.</summary>
    private static Fixture? Ahri()
    {
        string wad = Path.Combine(Champions, "Ahri.wad.client");
        if (!Directory.Exists(Final) || !File.Exists(wad) || Database.Value is not { } database) return null;

        var resolver = new WadPathResolver(database);
        var archive = WadArchive.Open(wad, resolver);
        byte[]? Read(string p) =>
            archive.TryGetEntry(HashAlgorithms.WadPath(p), out _) ? archive.Extract(HashAlgorithms.WadPath(p)) : null;

        var cache = ShaderCacheReader.Open(Final, resolver, out _);
        var skn = Read("assets/characters/ahri/skins/base/ahri_base.skn");
        var bin = Read("data/characters/ahri/skins/skin0.bin");

        if (cache is null || skn is null || bin is null) { archive.Dispose(); cache?.Dispose(); return null; }
        return new Fixture(skn, bin, cache, archive);
    }

    private static PreparedCharacterScene? Prepare(Fixture f, string? fallback = null)
    {
        var database = Database.Value!;
        return Dx11CharacterScene.Prepare(
            f.Skn, f.SkinBin, f.Cache, perms: null,
            readAsset: h => f.Archive.TryGetEntry(h, out _) ? f.Archive.Extract(h) : null,
            resolveBinName: h => database.TryGetBinName(h, out var n) ? n : null,
            resolveWadPath: h => database.TryGetPath(h, out var p) ? p : null,
            fallbackShader: fallback);
    }

    [Fact]
    public void OnThisMachineTheResolverActuallyRuns()
    {
        // Every test here skips without the install, which is right on a build machine and dangerous on
        // this one: a skip and a pass look identical. This turns a silent skip into a visible failure,
        // and prints what actually resolved so the numbers are on the record rather than assumed.
        if (!Directory.Exists(Final)) return;
        Assert.NotNull(Database.Value);

        var f = Ahri();
        Assert.NotNull(f);
        using (f!)
        {
            var scene = Prepare(f);
            Assert.NotNull(scene);
            Assert.True(scene!.Slices.Count > 0,
                $"Ahri resolved 0 of {scene.SubmeshCount} submeshes. "
                + string.Join(" | ", scene.Failures.Take(4)));
        }
    }

    // ===================================================== which material draws which submesh

    [Fact]
    public void AMaterialThatNamesAShaderBeatsOneThatDoesNot()
    {
        // The Kayn case, and it is why the rule runs twice. A skin's "(skin default texture)"
        // pseudo-binding carries textures and no shader; matched first it swallows every submesh and the
        // whole champion draws through a stand-in.
        if (Ahri() is not { } f) return;
        using (f)
        {
            var document = MaterialDocument.Parse(f.SkinBin,
                h => Database.Value!.TryGetBinName(h, out var n) ? n : null,
                h => Database.Value!.TryGetPath(h, out var p) ? p : null);

            var withShader = document.Materials.Where(m => !string.IsNullOrWhiteSpace(m.RenderShader)).ToList();
            if (withShader.Count == 0) return;      // nothing to prefer; the rule is untestable on this skin

            foreach (var binding in withShader)
                foreach (var submesh in binding.Submeshes)
                {
                    var picked = Dx11CharacterScene.MaterialFor(document.Materials, submesh);
                    Assert.NotNull(picked);
                    Assert.False(string.IsNullOrWhiteSpace(picked!.RenderShader),
                        $"{submesh} resolved to {picked.Name}, which authors no shader, "
                        + "while a shader-bearing material claims it");
                }
        }
    }

    [Fact]
    public void EverySubmeshOfARealSkinResolvesToSomething()
    {
        if (Ahri() is not { } f) return;
        using (f)
        {
            var document = MaterialDocument.Parse(f.SkinBin,
                h => Database.Value!.TryGetBinName(h, out var n) ? n : null);
            var mesh = Meshes.SkinnedMeshDecoder.Decode(f.Skn);

            Assert.NotEmpty(mesh.SubMeshes);
            foreach (var sub in mesh.SubMeshes)
                Assert.True(Dx11CharacterScene.MaterialFor(document.Materials, sub.Material) is not null,
                    $"{sub.Material} resolved to no material at all");
        }
    }

    [Fact]
    public void NoBindingsMeansNoMaterialRatherThanAThrow() =>
        Assert.Null(Dx11CharacterScene.MaterialFor(Array.Empty<MaterialBinding>(), "Body"));

    // ===================================================== the scene

    [Fact]
    public void ARealSkinPreparesIntoDrawableSlices()
    {
        if (Ahri() is not { } f) return;
        using (f)
        {
            var scene = Prepare(f);
            Assert.NotNull(scene);

            Assert.True(scene!.Mesh.Vertices.Length > 0);
            Assert.True(scene.SubmeshCount > 0);
            Assert.NotEmpty(scene.Slices);
            // Every slice that survived resolution is drawable: it has both stages and a real index range.
            Assert.All(scene.Slices, s =>
            {
                Assert.NotNull(s.Vs);
                Assert.NotNull(s.Ps);
                Assert.True(s.Count > 0, $"{s.Submesh} has an empty index range");
            });
        }
    }

    [Fact]
    public void TheSlicesCoverTheMeshTheyCameFrom()
    {
        // A slice whose range runs off the end of the index buffer is a GPU read out of bounds, and the
        // symptom is a hang or a driver reset rather than a wrong picture.
        if (Ahri() is not { } f) return;
        using (f)
        {
            var scene = Prepare(f);
            if (scene is null) return;
            int indices = Meshes.SkinnedMeshDecoder.Decode(f.Skn).Indices.Length;

            Assert.All(scene.Slices, s =>
            {
                Assert.InRange(s.Start, 0, indices);
                Assert.InRange(s.Start + s.Count, 0, indices);
            });
        }
    }

    [Fact]
    public void TexturesAreDecodedOncePerDistinctPath()
    {
        // Several submeshes share one diffuse. Decoding per slice rather than per path is how a champion
        // load turns into seconds of duplicated BC decompression.
        if (Ahri() is not { } f) return;
        using (f)
        {
            var scene = Prepare(f);
            if (scene is null || scene.Slices.Count == 0) return;

            var asked = scene.Slices.SelectMany(s => s.Textures).Select(t => t.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Assert.True(scene.Textures.Count <= asked.Count);
            Assert.All(scene.Textures.Keys, k => Assert.Contains(k, asked, StringComparer.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void TheSkinsOwnHiddenSubmeshesComeBackHiddenRatherThanMissing()
    {
        // Ahri hides Tail_Large and Body_Proxy. Dropping them would make them unrecoverable; the host
        // needs to be able to switch them on.
        if (Ahri() is not { } f) return;
        using (f)
        {
            var scene = Prepare(f);
            if (scene?.SkinMesh is not { InitialSubmeshesToHide.Count: > 0 } skin) return;

            foreach (string hidden in skin.InitialSubmeshesToHide)
            {
                var slice = scene.Slices.FirstOrDefault(s =>
                    s.Submesh.Equals(hidden, StringComparison.OrdinalIgnoreCase));
                if (slice is null) continue;      // not every hidden name is a submesh of this mesh
                Assert.True(slice.Hidden, $"{hidden} should be hidden by initialSubmeshToHide");
            }
        }
    }

    [Fact]
    public void AMaterialWithNoShaderIsReportedRatherThanGuessedAt()
    {
        // 42% of the material corpus authors no renderShader. Drawing those with an invented shader
        // would be asserting something about the game that has not been measured.
        if (Ahri() is not { } f) return;
        using (f)
        {
            var scene = Prepare(f, fallback: null);
            if (scene is null) return;

            Assert.All(scene.Slices, s => Assert.False(s.UsedFallbackShader));
            // Anything unresolved has to say so; silence would read as "this champion has 3 submeshes".
            if (scene.Slices.Count < scene.SubmeshCount) Assert.NotEmpty(scene.Failures);
        }
    }

    [Fact]
    public void ABrokenMeshIsNullRatherThanAnException()
    {
        if (Ahri() is not { } f) return;
        using (f)
        {
            Assert.Null(Dx11CharacterScene.Prepare(new byte[] { 1, 2, 3, 4 }, f.SkinBin, f.Cache, null,
                _ => null, _ => null));
        }
    }

    [Fact]
    public void AMissingSkinBinStillGivesAMeshRatherThanNothing()
    {
        // The geometry is worth showing even when its materials cannot be found - an untextured champion
        // is a diagnosable state, a blank window is not.
        if (Ahri() is not { } f) return;
        using (f)
        {
            var scene = Dx11CharacterScene.Prepare(f.Skn, skinBinBytes: null, f.Cache, null,
                _ => null, _ => null);

            Assert.NotNull(scene);
            Assert.True(scene!.Mesh.Vertices.Length > 0);
            Assert.Empty(scene.Slices);
            Assert.NotEmpty(scene.Failures);
        }
    }

    [Fact]
    public void TheReportSaysWhatWasResolved()
    {
        if (Ahri() is not { } f) return;
        using (f)
        {
            var scene = Prepare(f);
            if (scene is null) return;
            Assert.Contains("vertices", scene.Report, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("slice", scene.Report, StringComparison.OrdinalIgnoreCase);
        }
    }
}
