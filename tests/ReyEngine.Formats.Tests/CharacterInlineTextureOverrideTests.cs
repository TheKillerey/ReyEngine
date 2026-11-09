using ReyEngine.App.Services;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

/// <summary>M885: a submesh whose inline materialOverride carries only a texture, in a skin that has a default
/// static material, draws that material with the override's texture as its diffuse.</summary>
public sealed class CharacterInlineTextureOverrideTests
{
    private const string Champions = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Champions";

    [Fact]
    public void AatroxSkin33SwordAndShoulderTakeTheirOwnTexture()
    {
        string wad = Path.Combine(Champions, "Aatrox.wad.client");
        if (!File.Exists(wad)) return;
        HashDatabase db;
        try { db = new HashSyncService().LoadLocal(_ => { }); } catch { return; }
        using var archive = WadArchive.Open(wad, new WadPathResolver(db));
        ulong hash = HashAlgorithms.WadPath("data/characters/aatrox/skins/skin33.bin");
        if (!archive.TryGetEntry(hash, out _)) return;
        var doc = MaterialDocument.Parse(archive.Extract(hash),
            h => db.TryGetBinName(h, out var n) ? n : null, h => db.TryGetPath(h, out var p) ? p : null);

        var sword = Dx11CharacterScene.MaterialFor(doc.Materials, "Sword_01")!;
        Assert.True(sword.IsDefault);
        Assert.Equal("assets/characters/aatrox/skins/skin33/aatrox_skin33_sword_tx_cm.tex",
            Dx11CharacterScene.InlineTextureOverride(doc.Materials, sword, "Sword_01")?.ToLowerInvariant());
        var shoulder = Dx11CharacterScene.MaterialFor(doc.Materials, "Shoulder")!;
        Assert.EndsWith("aatrox_skin33_banner_tx_cm.tex",
            Dx11CharacterScene.InlineTextureOverride(doc.Materials, shoulder, "Shoulder")?.ToLowerInvariant());
        // the default material's own submesh keeps its own texture
        var head = Dx11CharacterScene.MaterialFor(doc.Materials, "Default_Head")!;
        Assert.Null(Dx11CharacterScene.InlineTextureOverride(doc.Materials, head, "Default_Head"));
    }
}
