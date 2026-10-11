using ReyEngine.App.Services;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Shaders;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M836. Riot's 16.20 base_srx: the Worlds_Ground_*_A_MAT materials link textures the hash tables cannot name, so
/// the path reads as "0x...". Dx11SceneBuilder hashed that text (WadPath("0x...")), missed the chunk and bound the
/// white stand-in. The chunk the reference carries must address the texture. Real data; skips without the game.
/// </summary>
public sealed class MapUnnamedTextureLinkTests
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private const string GeoPath = "data/maps/mapgeometry/map11/base_srx.mapgeo";
    private const string BinPath = "data/maps/mapgeometry/map11/base_srx.materials.bin";
    private const string Material = "Maps/KitPieces/SRS/Worlds/Materials/Default/Worlds_Ground_A5_OrderBase_A_MAT";

    [Fact]
    public void WorldsGroundDiffuse_DecodesThroughTheScenePath()
    {
        string wadPath = Path.Combine(Final, "Maps", "Shipping", "Map11.wad.client");
        if (!File.Exists(wadPath)) return;   // no game install on this machine
        var db = new HashSyncService().LoadLocal(_ => { });
        var resolver = new WadPathResolver(db);
        using var wad = WadArchive.Open(wadPath, resolver);
        string? BinName(uint h) => db.TryGetBinName(h, out var n) ? n : null;
        string? WadPathOf(ulong h) => db.TryGetPath(h, out var p) ? p : null;
        byte[]? Read(ulong h) => wad.TryGetEntry(h, out _) ? wad.Extract(h) : null;

        ulong geoHash = HashAlgorithms.WadPath(GeoPath), binHash = HashAlgorithms.WadPath(BinPath);
        if (!wad.TryGetEntry(geoHash, out _) || !wad.TryGetEntry(binHash, out _)) return;
        var matBin = wad.Extract(binHash);
        var doc = MaterialDocument.Parse(matBin, BinName, WadPathOf);
        var mat = doc.Materials.FirstOrDefault(m => m.Name.Equals(Material, StringComparison.OrdinalIgnoreCase));
        if (mat is null) return;
        var slot = mat.Slots.First(s => s.SamplerName.Equals("DiffuseTexture", StringComparison.OrdinalIgnoreCase));

        // the reference's own hash addresses a real chunk, whether or not the dictionary names it
        ulong chunk = BinTexturePath.HashOfReference(slot.Path);
        Assert.True(wad.TryGetEntry(chunk, out _), $"no chunk for {slot.Path}");
        Assert.True(TextureDecoder.Decode(wad.Extract(chunk)).Width > 0);

        // and the D3D11 scene builder loads it under the same key MaterialTextures gives
        var map = MapGeoDecoder.Decode(wad.Extract(geoHash), ExtendedChannelRule.From(matBin, BinName));
        using var cache = ShaderCacheReader.Open(Final, resolver, out _);
        if (cache is null) return;
        var prepared = Dx11SceneBuilder.Prepare(cache, new ShaderPermutationIndex(Final), map, doc.Materials, Read,
            GeoPath, null, false);
        Assert.True(prepared.Textures.ContainsKey(slot.Path.ToLowerInvariant()),
            $"scene did not load {slot.Path}");
    }
}
