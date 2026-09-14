using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Skeletons;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M664: the render state a champion submesh is drawn with.
///
/// <para>Reported as "Aatrox's wings ignore the mesh and show the black background". Three separate
/// faults sat behind it, and these tests pin the two that live in this assembly plus the renderer
/// ordering that the third depends on.</para>
/// </summary>
public sealed class ChampionRenderStateTests
{
    private const string Champions = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Champions";
    private static bool Installed => Directory.Exists(Champions);

    private static readonly Lazy<HashDatabase?> Database = new(() =>
    {
        try { return new HashSyncService().LoadLocal(_ => { }); }
        catch { return null; }
    });

    private static string Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        // M725: ReyEngine.slnx is the repo root marker; with "ReyEngine.sln" this walk never terminated on
        // a real directory and every guard in this file silently passed without reading anything.
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        return dir is null ? "" : File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
    }

    // ===================================================== the default profile is not a sibling's

    /// <summary>
    /// A skin with no default StaticMaterialDef used to borrow whichever one came FIRST in the file, and
    /// file order says nothing. Measured over the roster before the fix: 162 of 174 champions have no
    /// default StaticMaterialDef, and in 24 of them the first one is blended, so 92 plain body submeshes
    /// inherited depthWrite=false — Renata's body took her glass, Akali's took her kama, Locke's took his
    /// glasses. After: 0 and 0.
    /// </summary>
    [Fact]
    public void NoChampionsPlainSubmeshesInheritABlendedSiblingsState()
    {
        if (!Installed || Database.Value is null) return;   // no install here - nothing to census
        var database = Database.Value!;
        var resolver = new WadPathResolver(database);
        string? Bin(uint h) => database.TryGetBinName(h, out var n) ? n : null;
        string? Wad(ulong h) => database.TryGetPath(h, out var p) ? p : null;

        var offenders = new List<string>();
        int checkedSkins = 0;

        foreach (string wadPath in Directory.GetFiles(Champions, "*.wad.client").OrderBy(x => x))
        {
            string champ = Path.GetFileNameWithoutExtension(wadPath).Replace(".wad", "");
            string lower = champ.ToLowerInvariant();
            WadArchive archive;
            try { archive = WadArchive.Open(wadPath, resolver); } catch { continue; }
            using (archive)
            {
                ulong skin = HashAlgorithms.WadPath($"data/characters/{lower}/skins/skin0.bin");
                if (!archive.TryGetEntry(skin, out _)) continue;
                MaterialDocument doc;
                try { doc = MaterialDocument.Parse(archive.Extract(skin), Bin, Wad); }
                catch { continue; }
                checkedSkins++;

                // Only the INHERITED case. Where Riot marks a blended material as the skin's default that
                // is Riot's own statement and we keep it - Aatrox's default really is Aatrox_VFXBase_inst.
                if (doc.Materials.Any(m => m.IsDefault && m.IsStaticMaterialDef)) continue;
                if (!doc.DefaultProfile.DepthWrite) offenders.Add(champ);
            }
        }

        if (checkedSkins == 0) return;
        Assert.True(offenders.Count == 0,
            $"{offenders.Count} champion(s) inherit a non-depth-writing sibling as their default: "
            + string.Join(", ", offenders.Take(20)));
    }

    /// <summary>
    /// M666: the rule underneath the one above. A submesh with no material of its own must inherit the
    /// skin's OWN default — either the default StaticMaterialDef when it names one, or the
    /// skinMeshProperties block, which every champion has — and never some other material that merely
    /// happens to sit first in the file.
    ///
    /// <para>Both earlier attempts failed this. Taking the first StaticMaterialDef gave Renata's body her
    /// glass; skipping blended ones then left Locke, whose five materials are all blended, with nothing to
    /// borrow and a weapon smear that drew as a solid quad. Asserting the RESULT (depth write) missed
    /// both, because a wrong-but-opaque sibling looks identical to the right answer in that one bit.</para>
    /// </summary>
    [Fact]
    public void TheDefaultProfileIsAlwaysTheSkinsOwnAndNeverASiblings()
    {
        if (!Installed || Database.Value is null) return;
        var database = Database.Value!;
        var resolver = new WadPathResolver(database);
        string? Bin(uint h) => database.TryGetBinName(h, out var n) ? n : null;
        string? Wad(ulong h) => database.TryGetPath(h, out var p) ? p : null;

        var offenders = new List<string>();
        int checkedSkins = 0;

        foreach (string wadPath in Directory.GetFiles(Champions, "*.wad.client").OrderBy(x => x))
        {
            string champ = Path.GetFileNameWithoutExtension(wadPath).Replace(".wad", "");
            string lower = champ.ToLowerInvariant();
            WadArchive archive;
            try { archive = WadArchive.Open(wadPath, resolver); } catch { continue; }
            using (archive)
            {
                ulong skin = HashAlgorithms.WadPath($"data/characters/{lower}/skins/skin0.bin");
                if (!archive.TryGetEntry(skin, out _)) continue;
                MaterialDocument doc;
                try { doc = MaterialDocument.Parse(archive.Extract(skin), Bin, Wad); }
                catch { continue; }
                checkedSkins++;

                var authored = doc.Materials.Where(m => m.IsDefault).Select(m => m.Profile).ToList();
                var actual = doc.DefaultProfile;

                // Either it came from a binding the skin marks as ITS default, or the skin declared none
                // and the generic profile stands in. A non-default binding's profile is never acceptable.
                bool fromAuthored = authored.Any(a => a.RenderMode == actual.RenderMode
                                                      && a.DepthWrite == actual.DepthWrite
                                                      && a.DoubleSided == actual.DoubleSided);
                bool generic = authored.Count == 0 && actual.RenderMode == MaterialRenderMode.Opaque;
                if (!fromAuthored && !generic) offenders.Add($"{champ} ({actual.RenderMode})");
            }
        }

        if (checkedSkins == 0) return;
        Assert.True(offenders.Count == 0,
            $"{offenders.Count} champion(s) take their default from a material that is not the skin's own: "
            + string.Join(", ", offenders.Take(20)));
    }

    // ===================================================== the hide list is the loaded skin's own

    /// <summary>
    /// M669. The preview took initialSubmeshToHide from the first skins/*.bin in asset order that had one,
    /// not from the loaded skin. Locke's base skin declares VFX_Head, VFX_Hair and VFX_Smoke hidden - the
    /// overlay shells his W form uses - and the preview instead applied a later skin's list, hiding
    /// Recall_Page and Recall_Nail (which skin0 does not have) and drawing the three shells over his body
    /// as a second translucent copy of himself. Both readers of the same bin must agree, and the preview
    /// must read the SAME bin the D3D11 path reads.
    /// </summary>
    [Fact]
    public void TheTwoReadersOfASkinsHideListAgreeOnTheRealBins()
    {
        if (!Installed || Database.Value is null) return;
        var database = Database.Value!;
        var resolver = new WadPathResolver(database);
        string? Bin(uint h) => database.TryGetBinName(h, out var n) ? n : null;
        string? Wad(ulong h) => database.TryGetPath(h, out var p) ? p : null;

        string wadPath = Path.Combine(Champions, "Locke.wad.client");
        if (!File.Exists(wadPath)) return;
        using var archive = WadArchive.Open(wadPath, resolver);
        ulong skin0 = HashAlgorithms.WadPath("data/characters/locke/skins/skin0.bin");
        if (!archive.TryGetEntry(skin0, out _)) return;
        var bytes = archive.Extract(skin0);

        var viaPreview = ChampionAnimationData.ParseInitialHide(bytes);
        var viaDx11 = MaterialDocument.Parse(bytes, Bin, Wad).SkinMesh?.InitialSubmeshesToHide ?? Array.Empty<string>();

        Assert.Equal(
            viaDx11.OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
            viaPreview.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        // and the base skin does hide its VFX shells - that is Riot's data, the reason the fix matters
        Assert.Contains(viaDx11, x => x.Equals("VFX_Smoke", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ThePreviewReadsTheLoadedSkinsOwnHideListBeforeScanningSiblings()
    {
        var vm = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        if (vm.Length == 0) return;
        int own = vm.IndexOf("ParseInitialHide(GetAssetBytes(ownSkinBin))", StringComparison.Ordinal);
        int scan = vm.IndexOf("e.Path.Contains(skinDir, OIC) && hide.Count == 0", StringComparison.Ordinal);
        Assert.True(own > 0 && scan > own, "the loaded skin's own list must be read before any sibling is scanned");
    }

    // ===================================================== a champion is never river water

    /// <summary>
    /// M670. Locke's five Onsen materials all classified as Flowmap_River water, because Onsen authors a
    /// FlowmapTex sampler and FlowmapSpeed/FlowSpeed parameters for a surface distortion and those are
    /// the exact tokens the M44 river rule keys on. The GL viewport then drew his body through the water
    /// branch - a flat translucent cyan silhouette. Pinned on the real bin, with the real map water kept
    /// alive by the sibling test so the gate cannot quietly take the river with it.
    /// </summary>
    [Fact]
    public void AChampionSkinNeverClassifiesAsRiverWater()
    {
        if (!Installed || Database.Value is null) return;
        var database = Database.Value!;
        var resolver = new WadPathResolver(database);
        string? Bin(uint h) => database.TryGetBinName(h, out var n) ? n : null;
        string? Wad(ulong h) => database.TryGetPath(h, out var p) ? p : null;

        string wadPath = Path.Combine(Champions, "Locke.wad.client");
        if (!File.Exists(wadPath)) return;
        using var archive = WadArchive.Open(wadPath, resolver);
        ulong skin0 = HashAlgorithms.WadPath("data/characters/locke/skins/skin0.bin");
        if (!archive.TryGetEntry(skin0, out _)) return;

        var doc = MaterialDocument.Parse(archive.Extract(skin0), Bin, Wad);
        var statics = doc.Materials.Where(m => m.IsStaticMaterialDef).ToList();
        Assert.NotEmpty(statics);
        // the trigger really is there - this is not a test of a material that never looked like water
        Assert.Contains(statics, m => m.Slots.Any(sl => sl.SamplerName.Contains("Flow", StringComparison.OrdinalIgnoreCase)));
        foreach (var m in statics)
            Assert.False(m.Profile.IsFlowmap, $"{m.Name} classified as river water");
    }

    [Fact]
    public void MapRiverWaterStillClassifiesAsWater()
    {
        const string maps = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping";
        if (!Directory.Exists(maps) || Database.Value is null) return;
        var database = Database.Value!;
        var resolver = new WadPathResolver(database);
        string? Bin(uint h) => database.TryGetBinName(h, out var n) ? n : null;
        string? Wad(ulong h) => database.TryGetPath(h, out var p) ? p : null;

        // Measured (M670 water census): the Rift plays its river on the BLOOM variant bins - base_srx
        // carries none. Map12 bloom.materials.bin holds nine Flowmap_River materials, the most anywhere.
        string wadPath = Path.Combine(maps, "Map12.wad.client");
        if (!File.Exists(wadPath)) return;
        using var archive = WadArchive.Open(wadPath, resolver);
        var bin = archive.Entries.FirstOrDefault(e => e.IsResolved
            && e.Path.EndsWith("/bloom.materials.bin", StringComparison.OrdinalIgnoreCase));
        if (bin is null) return;

        var doc = MaterialDocument.Parse(archive.Extract(bin.PathHash), Bin, Wad);
        var names = doc.Materials.Select(m => m.Name).ToList();
        var profiles = MaterialProfiles.ForMapMaterials(archive.Extract(bin.PathHash), names, Bin, Wad);
        // M44 measured the Rift's river as Flowmap_River; the gate above must leave it as water
        Assert.Contains(profiles.Values, p => p.IsFlowmap);
    }

    // ===================================================== blending and occluding are separate questions

    [Fact]
    public void AMaterialBlendsWithoutWritingDepthUnlessItSaysOtherwise()
    {
        // The map rule stays the default: a decal must not stamp depth at its own plane (M279).
        Assert.False(Rendering.ViewportMeshRenderer.SubmeshMaterial.Default.BlendWritesDepth);
    }

    /// <summary>
    /// The transparent pass has to decide the depth mask per submesh. One DepthMask(false) for the whole
    /// pass is what made a champion whose default material is blended — 12 of 174, Aatrox among them —
    /// render with no depth writes at all, back to front through himself.
    /// </summary>
    [Fact]
    public void TheTransparentPassPicksItsDepthMaskPerSubmesh()
    {
        var renderer = Source("src", "ReyEngine.Rendering", "ViewportMeshRenderer.cs");
        if (renderer.Length == 0) return;
        Assert.Contains("_gl.DepthMask(s.BlendWritesDepth);", renderer);
        // and the champion path opts in
        var champion = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.CharacterMaterials.cs");
        Assert.Contains("with { BlendWritesDepth = true }", champion);
    }

    // ===================================================== the props draw before the mesh block

    /// <summary>
    /// M668. M664 drew the props BETWEEN the mesh's opaque and transparent passes, and that crashed the
    /// editor: DrawPropMeshes unbinds the VAO and overwrites the matrices and texture units, so the
    /// transparent pass ran DrawSubmesh against nothing - an access violation inside GL.DrawElements, the
    /// same stack in five Windows event-log entries. It fired only when props existed, which is why Locke's
    /// Q, E and R died (they need a target, and the target dummy is a prop) and W did not.
    ///
    /// <para>The props now draw before the mesh block, which sets up its own state afterwards. A call
    /// between the two passes is exactly the shape that crashed, so this pins its absence.</para>
    /// </summary>
    [Fact]
    public void PropsAreDrawnBeforeTheMeshBlockAndNeverBetweenItsPasses()
    {
        var src = Source("src", "ReyEngine.Rendering", "ViewportMeshRenderer.cs");
        if (src.Length == 0) return;

        int meshBlock = src.IndexOf("        if (_hasMesh)\n        {\n            _gl.Enable(EnableCap.DepthTest);", StringComparison.Ordinal);
        if (meshBlock < 0) meshBlock = src.IndexOf("        if (_hasMesh)\r\n        {\r\n            _gl.Enable(EnableCap.DepthTest);", StringComparison.Ordinal);
        int pass1 = src.IndexOf("// Pass 1: opaque + cutout", StringComparison.Ordinal);
        int pass2 = src.IndexOf("// Pass 2: transparent modes", StringComparison.Ordinal);
        int firstCall = src.IndexOf("DrawPropMeshes(false);", StringComparison.Ordinal);

        Assert.True(meshBlock > 0 && pass1 > meshBlock && pass2 > pass1, "the mesh block and its passes are no longer where this test expects");
        Assert.True(firstCall > 0 && firstCall < meshBlock, "the first prop draw must come before the mesh block");

        // and NO call may sit between the two passes - that is the interleave that crashed
        int between = src.IndexOf("DrawPropMeshes(", pass1, pass2 - pass1, StringComparison.Ordinal);
        Assert.True(between < 0, "a prop draw between the opaque and transparent passes is the M664 crash");

        // M678: the blended prop surfaces draw AFTER the mesh's transparent pass, never inside it
        int transparentProps = src.IndexOf("DrawPropMeshes(true);", StringComparison.Ordinal);
        Assert.True(transparentProps > pass2, "the transparent prop pass must follow the mesh's own");

        // A mirrored prop instance flips FrontFace; the mesh block that follows relies on CW.
        Assert.Contains("_gl.FrontFace(FrontFaceDirection.CW);", src);
    }
}
