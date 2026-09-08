using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Materials;

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
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.sln"))) dir = dir.Parent;
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

    // ===================================================== the props draw between the two passes

    /// <summary>
    /// Order, not state. The arena floor is a PROP and the champion is the mesh, so props drawn after the
    /// whole mesh left a blend-mode transparency compositing against the cleared background with the floor
    /// painted in afterwards. Between the passes, the floor is already there to blend over.
    /// </summary>
    [Fact]
    public void PropsAreDrawnBetweenTheOpaqueAndTransparentPasses()
    {
        var src = Source("src", "ReyEngine.Rendering", "ViewportMeshRenderer.cs");
        if (src.Length == 0) return;

        int call = src.IndexOf("DrawPropMeshes();", StringComparison.Ordinal);
        int pass1 = src.IndexOf("// Pass 1: opaque + cutout", StringComparison.Ordinal);
        int pass2 = src.IndexOf("// Pass 2: transparent modes", StringComparison.Ordinal);

        Assert.True(pass1 > 0 && pass2 > pass1, "the two mesh passes are no longer where this test expects");
        Assert.True(call > pass1 && call < pass2,
            "the prop draw must sit between the opaque and transparent passes");

        // A mirrored prop instance flips FrontFace; the transparent pass reads it, so it has to be restored.
        Assert.Contains("_gl.FrontFace(FrontFaceDirection.CW);", src);
    }
}
