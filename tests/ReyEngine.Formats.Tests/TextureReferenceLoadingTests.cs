using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M592: a texture must load by the chunk it ADDRESSES, not by whether the dictionary can name it.
///
/// <para>M590 made a texture reference a WadChunkLink, and an unnameable one reads back as <c>0x…</c>.
/// Two things then went wrong in the OpenGL path and the affected material rendered COMPLETELY WHITE:</para>
///
/// <list type="number">
///   <item><see cref="MapGeoMaterialResolver"/> filtered candidates on a <c>.tex</c>/<c>.dds</c> suffix,
///   so the hex form was rejected and the material ended up with no diffuse at all.</item>
///   <item>The loader hashed the reference string blindly, turning <c>"0x1234…"</c> into
///   <c>WadPath("0x1234…")</c> — a chunk that does not exist — so even an accepted reference missed.</item>
/// </list>
///
/// <para>Measured on Riot's shipped <c>map12/bloom.materials.bin</c>: 210 diffuse references and 18 profile
/// textures, of which <b>0</b> were loadable the old way and <b>all</b> are loadable by hash.</para>
///
/// <para>Note what this is NOT: no fallback or placeholder texture is involved. The correct chunk is
/// loaded, addressed the way the client addresses it.</para>
/// </summary>
public sealed class TextureReferenceLoadingTests
{
    private const string Map12 = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping\Map12.wad.client";
    private const string Bloom = "data/maps/mapgeometry/map12/bloom.materials.bin";
    private const string SomeTex = "assets/maps/kitpieces/srs/base/textures/periph_bot_a_1bitalpha.tex";

    // ---- the primitive ---------------------------------------------------------------------------

    [Fact]
    public void AHexReferenceAddressesTheChunkItNames()
    {
        ulong hash = HashAlgorithms.WadPath(SomeTex);
        Assert.Equal(hash, BinTexturePath.HashOfReference(BinTexturePath.Hex(hash)));
    }

    [Fact]
    public void APathReferenceStillHashesAsAPath()
    {
        Assert.Equal(HashAlgorithms.WadPath(SomeTex), BinTexturePath.HashOfReference(SomeTex));
    }

    [Fact]
    public void HashingAHexReferenceAsAPathIsTheBugAndMustNotHappen()
    {
        // The exact defect: WadPath("0x…") is a different, non-existent chunk.
        string hex = BinTexturePath.Hex(HashAlgorithms.WadPath(SomeTex));
        Assert.NotEqual(HashAlgorithms.WadPath(hex), BinTexturePath.HashOfReference(hex));
    }

    [Fact]
    public void BothFormsAreRecognisedAsTextureReferences()
    {
        Assert.True(BinTexturePath.IsTextureReference(SomeTex));
        Assert.True(BinTexturePath.IsTextureReference("assets/x.dds"));
        Assert.True(BinTexturePath.IsTextureReference(BinTexturePath.Hex(1234)));
        Assert.False(BinTexturePath.IsTextureReference(""));
        Assert.False(BinTexturePath.IsTextureReference("assets/model.scb"));
        Assert.False(BinTexturePath.IsTextureReference(null));
    }

    // ---- against what Riot ships -----------------------------------------------------------------

    private static byte[]? ShippedBloom()
    {
        if (!File.Exists(Map12)) return null;
        using var wad = WadArchive.Open(Map12);
        ulong h = HashAlgorithms.WadPath(Bloom);
        return wad.TryGetEntry(h, out _) ? wad.Extract(h) : null;
    }

    [Fact]
    public void EveryDiffuseResolvesToARealChunkWithoutAnyDictionary()
    {
        if (ShippedBloom() is not { } bytes) return;   // no game install
        using var wad = WadArchive.Open(Map12);

        var names = MaterialDocument.Parse(bytes, _ => null).Materials.Select(m => m.Name).ToList();
        var m2t = MapGeoMaterialResolver.Resolve(bytes, names);   // deliberately NO wad resolver

        Assert.True(m2t.Count > 100, $"expected most materials to yield a diffuse, got {m2t.Count}");
        Assert.All(m2t.Values, reference =>
        {
            ulong hash = BinTexturePath.HashOfReference(reference);
            Assert.True(wad.TryGetEntry(hash, out ReyEngine.Core.Assets.WadAssetEntry _),
                $"'{reference}' does not address a chunk in the wad");
        });
    }

    [Fact]
    public void ProfileTexturesResolveToRealChunksToo()
    {
        // Defect (a): ForMapMaterials never received the wad resolver, so terrain and flowmap layers came
        // back as bare hashes. They must address real chunks either way.
        if (ShippedBloom() is not { } bytes) return;
        using var wad = WadArchive.Open(Map12);
        var names = MaterialDocument.Parse(bytes, _ => null).Materials.Select(m => m.Name).ToList();

        var profiles = MaterialProfiles.ForMapMaterials(bytes, names, _ => null);
        var layers = profiles.Values
            .SelectMany(p => new[] { p.TerrainBottomPath, p.TerrainMiddlePath, p.TerrainTopPath,
                                     p.TerrainMaskPath, p.FlowMapPath, p.FlowNormalPath })
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();

        Assert.NotEmpty(layers);   // bloom carries flowmap water; if this is empty the test proves nothing
        Assert.All(layers, t => Assert.True(
            wad.TryGetEntry(BinTexturePath.HashOfReference(t!), out ReyEngine.Core.Assets.WadAssetEntry _),
            $"profile texture '{t}' does not address a chunk"));
    }

    [Fact]
    public void ADictionaryOnlyChangesTheNameNotWhichChunkIsLoaded()
    {
        // The property that makes the fix correct rather than lucky: naming must not change addressing.
        if (ShippedBloom() is not { } bytes) return;
        using var wad = WadArchive.Open(Map12);
        var names = MaterialDocument.Parse(bytes, _ => null).Materials.Select(m => m.Name).ToList();

        var blind = MapGeoMaterialResolver.Resolve(bytes, names);
        var named = MapGeoMaterialResolver.Resolve(bytes, names,
            h => wad.TryGetEntry(h, out var e) && e.IsResolved ? e.Path : null);

        Assert.Equal(blind.Count, named.Count);
        foreach (var (material, reference) in blind)
            Assert.Equal(BinTexturePath.HashOfReference(reference),
                         BinTexturePath.HashOfReference(named[material]));
    }
}
