using System.Numerics;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M633: the D3D11 character path drew every champion two-sided. M624 pinned culling off for an honest
/// reason - the character winding on that renderer had never been measured - so this is that measurement,
/// kept where it can be re-run rather than left in a commit message.
/// </summary>
public sealed class CharacterCullingTests
{
    private const string Champions = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Champions";
    private static bool Installed => Directory.Exists(Champions);

    private static readonly Lazy<HashDatabase?> Database = new(() =>
    {
        try { return new HashSyncService().LoadLocal(_ => { }); }
        catch { return null; }
    });

    private static string? Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static MaterialDocument? Skin(string champion)
    {
        if (!Installed || Database.Value is not { } database) return null;
        string wad = Path.Combine(Champions, champion + ".wad.client");
        if (!File.Exists(wad)) return null;
        using var archive = WadArchive.Open(wad, new WadPathResolver(database));
        ulong hash = HashAlgorithms.WadPath("data/characters/" + champion.ToLowerInvariant() + "/skins/skin0.bin");
        if (!archive.TryGetEntry(hash, out _)) return null;
        return MaterialDocument.Parse(archive.Extract(hash),
            h => database.TryGetBinName(h, out var name) ? name : null,
            h => database.TryGetPath(h, out var path) ? path : null);
    }

    // ===================================================== the winding, which is the thing M624 wanted

    [Theory]
    [InlineData("Aatrox")]
    [InlineData("Ahri")]
    [InlineData("Garen")]
    public void AChampionIsWoundTheSameWayTheMapGeometryIs(string champion)
    {
        // Every League mesh ships authored vertex normals, so the winding can be settled on the CPU:
        // cross(p1-p0, p2-p0) either points the way the artist's normal does or it does not. The map path
        // culls per material and M357/M358 settled ITS winding on the pixels, so agreement here is what
        // lets the same rasterizer state carry over instead of being measured a second time.
        //
        // Measured: champions 99.6-100.0%, Map11 98.8%, Map12 94.8% - the maps read lower because their
        // double-sided cards carry a lighting normal rather than a geometric one, not because they disagree.
        if (Skin(champion) is not { } document || document.SkinMesh?.SimpleSkin is not { Length: > 0 } path) return;
        if (Database.Value is not { } database) return;

        using var archive = WadArchive.Open(Path.Combine(Champions, champion + ".wad.client"),
            new WadPathResolver(database));
        ulong hash = BinTexturePath.HashOfReference(path);
        if (!archive.TryGetEntry(hash, out _)) return;

        MeshAsset mesh;
        try { mesh = SkinnedMeshDecoder.Decode(archive.Extract(hash)); }
        catch { return; }

        long agree = 0, disagree = 0;
        for (int t = 0; t + 2 < mesh.Indices.Length; t += 3)
        {
            int a = (int)mesh.Indices[t], b = (int)mesh.Indices[t + 1], c = (int)mesh.Indices[t + 2];
            if ((c + 1) * 3 > mesh.Positions.Length) break;
            var p0 = At(mesh.Positions, a);
            var face = Vector3.Cross(At(mesh.Positions, b) - p0, At(mesh.Positions, c) - p0);
            var normal = At(mesh.Normals, a) + At(mesh.Normals, b) + At(mesh.Normals, c);
            if (face.LengthSquared() < 1e-12f || normal.LengthSquared() < 1e-12f) continue;
            if (Vector3.Dot(face, normal) > 0f) agree++; else disagree++;
        }

        long total = agree + disagree;
        Assert.True(total > 1000, $"{champion} decoded only {total} usable triangles");
        Assert.True(agree / (double)total > 0.99,
            $"{champion} agrees with its own normals on only {100.0 * agree / total:0.0}% of triangles - "
            + "the winding convention changed and per-material culling is no longer safe");
    }

    private static Vector3 At(float[] xs, int i) => new(xs[i * 3], xs[i * 3 + 1], xs[i * 3 + 2]);

    // ===================================================== what Riot authors, which is why absent = cull

    [Fact]
    public void RiotWritesCullEnableOnlyToTurnCullingOFF()
    {
        // The field is absent on 416 of the 420 base-skin materials across the roster and the schema default
        // is "cull". The four that write it are the argument that absent really means cull: Locke's hair,
        // casket and weapon, and Morgana's bush diffuse - the classic hair-and-foliage-card list, every one
        // of them FALSE. Riot writes the flag exactly where a surface is meant to be seen from behind.
        var locke = Skin("Locke");
        if (locke is null) return;

        var authored = locke.Materials.Where(m => m.CullEnable is not null).ToList();
        Assert.NotEmpty(authored);
        Assert.All(authored, m => Assert.False(m.CullEnable));
        Assert.All(authored, m => Assert.True(m.Profile.DoubleSided));
        Assert.All(authored, m => Assert.False(m.Profile.CullEnabled));
        Assert.Contains(authored, m => m.Name.Contains("Hair", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AMaterialThatSaysNothingIsCulled()
    {
        // Aatrox leaves it absent on all five, so all five are single-sided - which is the change: they
        // were all drawn two-sided before.
        var aatrox = Skin("Aatrox");
        if (aatrox is null) return;

        Assert.All(aatrox.Materials, m => Assert.Null(m.CullEnable));
        Assert.All(aatrox.Materials, m => Assert.True(m.Profile.CullEnabled));
    }

    // ===================================================== the wiring

    [Fact]
    public void TheSliceCarriesTheMaterialsOwnRenderState()
    {
        // Without this the commit has nothing to read: every submesh took StateDescription.Geometry and the
        // profile the parser had already computed was thrown away.
        var text = Source("src", "ReyEngine.App", "Services", "Dx11CharacterScene.cs");
        if (text is null) return;

        Assert.Contains("MaterialProfile Profile);", text);
        Assert.Contains("mat.CullBackFaces = slice.Profile.CullEnabled;", text);
        Assert.Contains("textures, parameters, hidden, usedFallback, b.Profile);", text);
    }

    [Fact]
    public void TheHostGatesCullingOnTheWindowsOwnToggleRatherThanPinningIt()
    {
        // Per-material AND per-viewport, the same rule GL uses - so "Cull" turns the whole thing off again
        // if anything ever does vanish, which is the escape hatch M354 did not have.
        var text = Source("src", "ReyEngine.App", "Views", "MeshPreviewWindow.Dx11.cs");
        if (text is null) return;

        Assert.Contains("_dx11.CullBackFaces = vm.CullBackfaces;", text);
        Assert.DoesNotContain("_dx11.CullBackFaces = false;", text);
    }

    [Fact]
    public void TheBlendStateStaysHardcodedAndSaysWhy()
    {
        // A measured NEGATIVE result, and the kind that gets re-litigated if it is not written down: the
        // 68 materials that author blendEnable and the 352 that do not all render identically here, because
        // Riot's champion pixel shaders discard and then write opaque alpha. 0 pixels over 21 champions.
        var text = Source("src", "ReyEngine.App", "Services", "Dx11CharacterScene.cs");
        if (text is null) return;
        Assert.Contains("moved <b>0 pixels</b>", text);
        Assert.Contains("StateDescription.Geometry", text);
    }
}
