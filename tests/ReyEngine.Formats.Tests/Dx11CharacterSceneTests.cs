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
    public void TheGeometryKeepsItsAuthoredCoordinatesBecauseSkinningDependsOnThem()
    {
        // The bug that made the D3D11 character invisible while its bones and VFX drew fine.
        //
        // A bone matrix maps BIND-POSE object space to posed space. Recentre the vertices first and
        // (p - c) * skin is not (p * skin) - c, so every bone rotates about a pivot the skeleton does not
        // know about. Measured below: the error is not a small offset, it is hundreds of units - the mesh
        // leaves the frustum, which is why it read as "nothing is drawn" rather than "the pose is wrong".
        if (Ahri() is not { } f) return;
        using (f)
        {
            var scene = Prepare(f);
            if (scene is null) return;

            var mesh = Meshes.SkinnedMeshDecoder.Decode(f.Skn);
            Assert.Equal(mesh.VertexCount, scene.Mesh.Vertices.Length);

            // What Prepare produced has to BE the authored positions.
            float worst = 0f;
            for (int v = 0; v < mesh.VertexCount; v += 97)
            {
                var authored = new System.Numerics.Vector3(
                    mesh.Positions[v * 3], mesh.Positions[v * 3 + 1], mesh.Positions[v * 3 + 2]);
                worst = MathF.Max(worst, System.Numerics.Vector3.Distance(scene.Mesh.Vertices[v].Position, authored));
            }
            Assert.True(worst < 0.01f, $"the geometry was moved {worst:0.00} units from where it was authored");
        }
    }

    [Fact]
    public void RecentringWouldHaveScatteredTheSkinnedMesh()
    {
        // The measurement behind the fix above, so the reasoning is on the record rather than asserted.
        // Skin the same vertices twice - once authored, once pre-shifted by the mesh centre - and compare
        // both against the CPU skinner. The authored one matches; the shifted one does not, and not by a
        // little.
        if (Ahri() is not { } f) return;
        using (f)
        {
            var mesh = Meshes.SkinnedMeshDecoder.Decode(f.Skn);
            if (mesh.BlendIndices is null || mesh.BlendWeights is null) return;

            string skl = "assets/characters/ahri/skins/base/ahri_base.skl";
            string anm = "assets/characters/ahri/skins/base/animations/spell1.anm";
            if (!f.Archive.TryGetEntry(HashAlgorithms.WadPath(skl), out _)) return;
            if (!f.Archive.TryGetEntry(HashAlgorithms.WadPath(anm), out _)) return;

            var skeleton = Skeletons.SkeletonDecoder.Decode(f.Archive.Extract(HashAlgorithms.WadPath(skl)));
            var clip = Animation.AnimationDecoder.Decode(f.Archive.Extract(HashAlgorithms.WadPath(anm)), "spell1");
            float t = clip.Duration * 0.5f;

            var palette = Animation.BonePalette.Build(skeleton, clip, t);
            var cpu = Animation.SkinnedMeshAnimator.Skin(mesh, skeleton, clip, t);

            // The centre PreviewGeometry would have subtracted.
            var min = new System.Numerics.Vector3(float.MaxValue);
            var max = new System.Numerics.Vector3(float.MinValue);
            for (int v = 0; v < mesh.VertexCount; v++)
            {
                var p = new System.Numerics.Vector3(mesh.Positions[v * 3], mesh.Positions[v * 3 + 1], mesh.Positions[v * 3 + 2]);
                min = System.Numerics.Vector3.Min(min, p);
                max = System.Numerics.Vector3.Max(max, p);
            }
            var centre = (min + max) * 0.5f;

            System.Numerics.Vector3 Skin(System.Numerics.Vector3 p, int v)
            {
                System.Numerics.Vector3 sum = default;
                float weight = 0f;
                for (int k = 0; k < 4; k++)
                {
                    float w = mesh.BlendWeights[v * 4 + k];
                    if (w <= 0f) continue;
                    int slot = mesh.BlendIndices[v * 4 + k];
                    if (slot < 0 || slot >= palette.Length) continue;
                    sum += w * System.Numerics.Vector3.Transform(p, palette[slot]);
                    weight += w;
                }
                return weight <= 0f ? p : sum;
            }

            float authoredWorst = 0f, shiftedWorst = 0f;
            for (int v = 0; v < mesh.VertexCount; v += 97)
            {
                var p = new System.Numerics.Vector3(mesh.Positions[v * 3], mesh.Positions[v * 3 + 1], mesh.Positions[v * 3 + 2]);
                var expected = new System.Numerics.Vector3(cpu.Positions[v * 3], cpu.Positions[v * 3 + 1], cpu.Positions[v * 3 + 2]);

                authoredWorst = MathF.Max(authoredWorst, System.Numerics.Vector3.Distance(Skin(p, v), expected));
                // What a recentred buffer would have fed the same shader, put back where it belongs.
                shiftedWorst = MathF.Max(shiftedWorst, System.Numerics.Vector3.Distance(Skin(p - centre, v) + centre, expected));
            }

            Assert.True(authoredWorst < 0.01f, $"authored vertices disagreed by {authoredWorst:0.00}");
            Assert.True(shiftedWorst > 10f,
                $"recentring was expected to break skinning badly; it moved vertices only {shiftedWorst:0.00} units");
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
    public void TheFallbackShaderResolvesTheSubmeshesThatWouldOtherwiseBeDropped()
    {
        // A skin bin's default diffuse and its inline per-submesh overrides carry TEXTURES and no shader.
        // With no stand-in they resolve to nothing, and the character draws with holes in it - measured
        // on Aatrox as 3 of 5 submeshes. The constant is the Shader Preview's, which is the one
        // configuration known to draw a champion through this renderer.
        if (Ahri() is not { } f) return;
        using (f)
        {
            var without = Prepare(f, fallback: null);
            var with = Prepare(f, fallback: Dx11CharacterScene.DefaultCharacterShader);
            if (without is null || with is null) return;

            Assert.True(with.Slices.Count >= without.Slices.Count,
                $"the stand-in resolved fewer slices ({with.Slices.Count}) than none ({without.Slices.Count})");
            // And every slice it added is honestly marked as using one.
            Assert.All(with.Slices.Where(x => x.UsedFallbackShader),
                x => Assert.True(x.Vs is not null && x.Ps is not null));
        }
    }

    [Fact]
    public void TheHostSuppliesTheStandInShader()
    {
        // Without this the constant exists and nothing passes it, which is the failure it was written for.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return;

        string host = System.IO.Path.Combine(dir.FullName, "src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.Characters.cs");
        if (!File.Exists(host)) return;
        Assert.Contains("Dx11CharacterScene.DefaultCharacterShader", File.ReadAllText(host));
    }

    [Fact]
    public void TheCharacterPreviewCullsPerMaterialUnderTheWindowsToggle()
    {
        // M624 pinned this OFF and this test held it there, because the character winding on this renderer
        // had never been measured and M354 is the precedent for guessing: it turned map culling on and
        // deleted the terrain.
        //
        // M633 measured it, so the guard now points the other way. The measurement lives in
        // CharacterCullingTests - champion triangles agree with their own authored normals 99.6-100.0% of
        // the time against the map's 94.8-98.8%, so M357's rasterizer state carries over; GL has culled
        // champions in this same window since M34; and 21 champions rendered headless on a real device
        // change pixels on 13 without the silhouette ever collapsing.
        //
        // What must not come back is the PIN. Per material AND per viewport, so "Cull" still turns the
        // whole thing off - that toggle is the escape hatch M354 did not have.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return;

        string window = System.IO.Path.Combine(dir.FullName, "src", "ReyEngine.App", "Views", "MeshPreviewWindow.Dx11.cs");
        if (!File.Exists(window)) return;
        string text = File.ReadAllText(window);

        Assert.Contains("_dx11.CullBackFaces = vm.CullBackfaces", text);
        Assert.DoesNotContain("_dx11.CullBackFaces = false", text);
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
    public void AMissingPermutationIndexIsReportedBecauseItRendersBlack()
    {
        // The failure this test exists for is not a crash, it is a character that draws perfectly and is
        // entirely black - indistinguishable, on a dark viewport, from not drawing at all. Without the
        // index there are no shader parameter DEFAULTS, so every parameter the material does not author
        // stays zero, and zero is a value the shader multiplies by (M255).
        if (Ahri() is not { } f) return;
        using (f)
        {
            var scene = Prepare(f);      // Prepare is called with perms: null
            Assert.NotNull(scene);
            Assert.Contains(scene!.Failures, x => x.Contains("permutation index", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void TheHostBuildsItsPermutationIndexFromTheSameLocatorAsEverythingElse()
    {
        // It built the path by hand from GameDirectory, so a folder the shared locator copes with but
        // Path.Combine does not gave a null index while the shader cache opened fine - two game-data
        // lookups disagreeing, with a black model as the only symptom.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return;

        string host = System.IO.Path.Combine(dir.FullName, "src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        if (!File.Exists(host)) return;
        string text = File.ReadAllText(host);

        int at = text.IndexOf("ShaderPermutationIndex? ShaderPerms()", StringComparison.Ordinal);
        Assert.True(at > 0, "ShaderPerms() is gone or renamed");
        string body = text.Substring(at, Math.Min(900, text.Length - at));
        Assert.Contains("GameReferenceLibrary.FindFinalDirectory", body);
    }

    // ===================================================== bloom (M763)

    [Fact]
    public void AChampionsGlowReachesTheFrame()
    {
        // M763: two defects hid this. The character window never loaded the bloom chain, and the chain's
        // composite bound its input while that texture was still the render target, so D3D11 bound NULL and
        // the screen blend returned the scene unchanged. BloomPasses counted 12 throughout; only a pixel
        // comparison can tell a chain that runs from one that shows.
        if (Ahri() is not { } f) return;
        using (f)
        {
            var scene = Prepare(f, Dx11CharacterScene.DefaultCharacterShader);
            if (scene is null || scene.Slices.Count == 0) return;
            Assert.NotNull(scene.BloomShaders);   // loaded with the scene, as the map path does

            using var renderer = new Rendering.D3D11.ShaderPreviewRenderer();
            if (!renderer.Initialize(out _)) return;   // no D3D11 here
            Dx11CharacterScene.Commit(renderer, scene, "");

            var mesh = Formats.Meshes.SkinnedMeshDecoder.Decode(f.Skn);
            var centre = (mesh.BoundsMin + mesh.BoundsMax) * 0.5f;
            float radius = (mesh.BoundsMax - mesh.BoundsMin).Length() * 0.5f;
            var eye = centre + new System.Numerics.Vector3(0f, radius * 0.1f, -radius * 1.4f);   // from behind: the tails
            byte[]? Frame(bool bloom) => renderer.RenderFrame(256, 256, new Rendering.D3D11.PreviewSettings
            {
                SuppliedView = System.Numerics.Matrix4x4.CreateLookAt(eye, centre, System.Numerics.Vector3.UnitY),
                SuppliedProjection = System.Numerics.Matrix4x4.CreatePerspectiveFieldOfView(0.9f, 1f, radius * 0.02f, radius * 40f),
                SuppliedCameraPosition = eye,
                AlphaBlend = true, DepthTest = true, MirrorX = true, TransposeMatrices = true,
                CullBackFaces = true, SortByPipeline = true, Shadows = false, Bloom = bloom, TimeSeconds = 1f,
            }, out _)?.ToArray();   // RenderFrame reuses its buffer

            var off = Frame(false);
            var on = Frame(true);
            if (off is null || on is null) return;
            Assert.True(renderer.BloomPasses > 0, "the bloom chain did not run: " + string.Join(" | ", renderer.Diagnostics.TakeLast(6)));
            int changed = 0;
            for (int i = 0; i < on.Length; i++) if (on[i] != off[i]) changed++;
            Assert.True(changed > 1000, $"bloom on changed {changed} byte(s) - the glow never reached the frame");
        }
    }

    // ===================================================== Commit, the seam everything stopped at

    [Fact]
    public void CommittingRegistersTheMaterialsWithTheRenderer()
    {
        // THE test this file was missing, and the gap is the whole reason M617-M625 happened: every test
        // here stopped at Prepare, which was always correct, while Commit built five materials and handed
        // none of them to the renderer. Eight milestones of symptoms downstream of one unregistered
        // object, and no assertion anywhere crossed the boundary.
        //
        // Needs a device, so it skips where there is none - the same rule the rest of the suite uses for
        // the game install.
        if (Ahri() is not { } f) return;
        using (f)
        {
            var scene = Prepare(f, Dx11CharacterScene.DefaultCharacterShader);
            if (scene is null || scene.Slices.Count == 0) return;

            using var renderer = new Rendering.D3D11.ShaderPreviewRenderer();
            if (!renderer.Initialize(out _)) return;      // no D3D11 here; nothing to assert

            int committed = Dx11CharacterScene.Commit(renderer, scene, "");

            Assert.True(renderer.MaterialCount > 0,
                $"Commit reported {committed} but the renderer holds {renderer.MaterialCount} - "
                + "the materials were built and never registered");
            Assert.Equal(renderer.MaterialCount, committed);
        }
    }

    [Fact]
    public void CommitReportsWhatTheRendererHoldsNotWhatItBuilt()
    {
        // The return value is what every caller drives its status and its HasScene flag from. While it
        // counted this method's own successes it could - and did - say "5 material(s) drawing" beside a
        // log line reading "after commit: 0 material(s)". A count taken FROM the renderer cannot.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return;

        string source = System.IO.Path.Combine(dir.FullName, "src", "ReyEngine.App", "Services", "Dx11CharacterScene.cs");
        if (!File.Exists(source)) return;
        string text = File.ReadAllText(source);

        Assert.Contains("renderer.AddMaterial(mat);", text);
        Assert.Contains("return renderer.MaterialCount;", text);
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
