using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M757: a project's copy of a game bin written as LTK Manager game-data declarations against the game's
/// copy. The spelling here was also checked the strong way, outside the suite: 262 mutated Riot bins from
/// Map11, Map12, Common and five champions, each converted and then applied over the untouched bin by
/// league-mod's own ltk_game_data 0.6 - 262 came back equal, with no diagnostics.
/// </summary>
public sealed class BinDeclarationsTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    /// <summary>Names for the few hashes these tests use, plus one that lies about its hash.</summary>
    private sealed class Names : IDeclarationNames
    {
        private readonly Dictionary<uint, string> _bin = new[]
        {
            "Maps/Test/Thing", "Mods/Test/New", "Characters/Old", "NoSlash", "Thing",
            "speed", "label", "inner", "count", "colour", "transform", "tags", "lookup", "maybe", "links",
            "hash", "ref", "InnerData",
        }.ToDictionary(H);
        public string? Field(uint hash) => hash == 0xdeadbeef ? "liar" : _bin.GetValueOrDefault(hash);
        public string? Class(uint hash) => Field(hash);
        public string? Entry(uint hash) => Field(hash);
        public string? File(ulong hash) => hash == HashAlgorithms.WadPath("assets/a.tex") ? "assets/a.tex" : null;
    }

    private static readonly Names N = new();

    private static byte[] Bin(IEnumerable<BinTreeObject> objects, params string[] deps)
    {
        using var ms = new MemoryStream();
        new BinTree(objects, deps).Write(ms);
        return ms.ToArray();
    }

    private static BinTreeObject Thing(params BinTreeProperty[] props) => new(H("Maps/Test/Thing"), H("Thing"), props);

    private static DeclaredChunk Convert(byte[] riot, byte[] mod) => BinDeclarations.Convert("data/test.bin", riot, mod, N);

    [Fact]
    public void AnUntouchedBinDeclaresNothing()
    {
        var bin = Bin(new[] { Thing(new BinTreeF32(H("speed"), 1f)) });
        var c = Convert(bin, bin);
        Assert.True(c.Unchanged);
        Assert.Null(c.Module);
    }

    [Fact]
    public void AChangedValueIsOneKeyUnderItsEntryInATargetModule()
    {
        var riot = Bin(new[] { Thing(new BinTreeF32(H("speed"), 1f), new BinTreeString(H("label"), "a")) });
        var mod = Bin(new[] { Thing(new BinTreeF32(H("speed"), 0.37f), new BinTreeString(H("label"), "a")) });
        var c = Convert(riot, mod);
        Assert.True(c.Declared);
        Assert.Equal(1, c.Properties);
        Assert.Equal(
            "  - target: \"data/test.bin\"\n" +
            "    Maps/Test/Thing:\n" +
            "      speed: 0.37\n", c.Module);
        Assert.StartsWith("# Written by ReyEngine", BinDeclarations.Manifest(new[] { c }));
        Assert.Contains("version: 1\nmodules:\n  - target:", BinDeclarations.Manifest(new[] { c }));
    }

    [Fact]
    public void AFieldInsideAStructOfTheSameClassIsADottedKey()
    {
        BinTreeObject With(float v) => Thing(new BinTreeEmbedded(H("inner"), H("InnerData"), new BinTreeProperty[]
            { new BinTreeF32(H("speed"), v), new BinTreeU32(H("count"), 3) }));
        var c = Convert(Bin(new[] { With(1f) }), Bin(new[] { With(2.5f) }));
        Assert.Contains("      inner.speed: 2.5\n", c.Module);
        Assert.DoesNotContain("count", c.Module);
    }

    [Fact]
    public void AStructThatLostAFieldIsSetWhole()
    {
        var riot = Bin(new[] { Thing(new BinTreeEmbedded(H("inner"), H("InnerData"), new BinTreeProperty[]
            { new BinTreeF32(H("speed"), 1f), new BinTreeU32(H("count"), 3) })) });
        var mod = Bin(new[] { Thing(new BinTreeEmbedded(H("inner"), H("InnerData"), new BinTreeProperty[]
            { new BinTreeF32(H("speed"), 1f) })) });
        Assert.Contains("      inner: !embed(InnerData) {\"speed\": 1}\n", Convert(riot, mod).Module);
    }

    [Fact]
    public void RemovingATopLevelPropertyShipsTheBinWithTheReason()
    {
        var riot = Bin(new[] { Thing(new BinTreeF32(H("speed"), 1f), new BinTreeU32(H("count"), 3)) });
        var mod = Bin(new[] { Thing(new BinTreeF32(H("speed"), 1f)) });
        var c = Convert(riot, mod);
        Assert.False(c.Declared);
        Assert.Contains("no declaration removes a property", c.WhyNot);
        Assert.Contains("count", c.WhyNot);
    }

    [Fact]
    public void AnObjectThatChangedClassShipsTheBin()
    {
        var riot = Bin(new[] { Thing(new BinTreeF32(H("speed"), 1f)) });
        var mod = Bin(new[] { new BinTreeObject(H("Maps/Test/Thing"), H("InnerData"), new BinTreeProperty[] { new BinTreeF32(H("speed"), 1f) }) });
        Assert.Contains("changed class", Convert(riot, mod).WhyNot);
    }

    [Fact]
    public void ObjectsAddedAndRemovedAreObjectBindings()
    {
        var old = new BinTreeObject(H("Characters/Old"), H("Thing"), new BinTreeProperty[] { new BinTreeU32(H("count"), 1) });
        var added = new BinTreeObject(H("Mods/Test/New"), H("Thing"), new BinTreeProperty[]
            { new BinTreeF32(H("speed"), 2f), new BinTreeString(H("label"), "new") });
        var c = Convert(Bin(new[] { Thing(), old }), Bin(new[] { Thing(), added }));
        Assert.Equal(1, c.ObjectsAdded);
        Assert.Equal(1, c.ObjectsRemoved);
        Assert.Contains(
            "    objects:\n" +
            "      Mods/Test/New:\n" +
            "        class: \"Thing\"\n" +
            "        set: {\"speed\": 2, \"label\": \"new\"}\n" +
            "      Characters/Old:\n" +
            "        remove: true\n", c.Module);
    }

    [Fact]
    public void DependenciesAreLinksAndMinusLinks()
    {
        var c = Convert(Bin(new[] { Thing() }, "DATA/A.bin", "DATA/B.bin"), Bin(new[] { Thing() }, "data/b.bin", "DATA/C.bin"));
        Assert.Contains("    links: [\"DATA/C.bin\"]\n", c.Module);
        Assert.Contains("    -links: [\"DATA/A.bin\"]\n", c.Module);   // B only changed case, so it stays
        Assert.Equal(2, c.LinksChanged);
    }

    [Theory]
    [InlineData(0.37f, "0.37")]
    [InlineData(1f, "1")]
    [InlineData(-0f, "-0.0")]        // "-0" would read as the integer zero and lose the sign
    [InlineData(1e-30f, "1E-30")]
    public void AnF32IsItsShortestRoundTripSpelling(float value, string expected) =>
        Assert.Equal(expected, BinDeclarations.Float(value));

    [Fact]
    public void ANonFiniteValueShipsTheBin()
    {
        // league-mod's loader rejects .inf and .nan outright, so no declaration can carry one
        var mod = Bin(new[] { Thing(new BinTreeF32(H("speed"), float.PositiveInfinity)) });
        Assert.Contains("non-finite", Convert(Bin(new[] { Thing(new BinTreeF32(H("speed"), 1f)) }), mod).WhyNot);
        Assert.Throws<BinDeclarations.Refused>(() => BinDeclarations.Float(float.NaN));
    }

    [Fact]
    public void ValuesRenderByTheFormatsTable()
    {
        var riot = Bin(new[] { Thing() });
        var mod = Bin(new[] { Thing(
            new BinTreeColor(H("colour"), new LeagueToolkit.Core.Primitives.Color((byte)255, (byte)128, (byte)0, (byte)64)),
            new BinTreeMatrix44(H("transform"), new Matrix4x4(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16)),
            new BinTreeString(H("label"), "say \"hi\"\\n"),
            new BinTreeContainer(H("tags"), BinPropertyType.Hash, new BinTreeProperty[] { new BinTreeHash(0, H("speed")), new BinTreeHash(0, 0x12345678) }),
            new BinTreeWadChunkLink(H("lookup"), HashAlgorithms.WadPath("assets/a.tex")),
            new BinTreeOptional(H("maybe"), new BinTreeVector2(0, new Vector2(1, 2)))) });
        string m = Convert(riot, mod).Module!;
        Assert.Contains("      colour: [255, 128, 0, 64]\n", m);
        Assert.Contains("      transform: [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]\n", m);   // file order
        Assert.Contains("      label: \"say \\\"hi\\\"\\\\n\"\n", m);
        Assert.Contains("      tags: [\"speed\", \"0x12345678\"]\n", m);                  // unknown hash: hex form
        Assert.Contains("      lookup: \"assets/a.tex\"\n", m);
        Assert.Contains("      maybe: [[1, 2]]\n", m);   // an option whose element is a list is a one-element list
    }

    [Fact]
    public void NamesAreUsedOnlyWhenTheyHashBack()
    {
        var riot = Bin(new[] { Thing() });
        var mod = Bin(new[] { Thing(new BinTreeU32(0xdeadbeef, 1), new BinTreeU32(0x0badf00d, 2)) });
        string m = Convert(riot, mod).Module!;
        Assert.Contains("      \"0xdeadbeef\": 1\n", m);   // "liar" does not hash to 0xdeadbeef
        Assert.Contains("      \"0x0badf00d\": 2\n", m);   // nothing known
    }

    [Fact]
    public void AnEntryAtTheBodyRootCarriesASlashOrItsHash()
    {
        // league-mod D22: a known name without a slash is spelled as its hash there
        var riot = Bin(new[] { new BinTreeObject(H("NoSlash"), H("Thing"), new BinTreeProperty[] { new BinTreeU32(H("count"), 1) }) });
        var mod = Bin(new[] { new BinTreeObject(H("NoSlash"), H("Thing"), new BinTreeProperty[] { new BinTreeU32(H("count"), 2) }) });
        Assert.Contains($"    \"0x{H("NoSlash"):x8}\":\n", Convert(riot, mod).Module);
    }

    [Fact]
    public void AFieldSpellingABindingKeywordIsWrittenAsItsHash()
    {
        var mod = Bin(new[] { Thing(new BinTreeU32(H("links"), 5)) });
        Assert.Contains($"      \"0x{H("links"):x8}\": 5\n", Convert(Bin(new[] { Thing() }), mod).Module);
    }

    [Fact]
    public void AMapWhoseOneKeyReadsBackAsAPinShipsTheBin()
    {
        var map = new BinTreeMap(H("lookup"), BinPropertyType.String, BinPropertyType.U32,
            new[] { new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeString(0, "hash"), new BinTreeU32(0, 1)) });
        Assert.Contains("reads back as a pin", Convert(Bin(new[] { Thing() }), Bin(new[] { Thing(map) })).WhyNot);
    }

    [Fact]
    public void APatchBinIsNotAPropBin()
    {
        var ptch = new byte[] { (byte)'P', (byte)'T', (byte)'C', (byte)'H', 0, 0, 0, 0 };
        Assert.Contains("PTCH", BinDeclarations.Convert("x.bin", ptch, ptch, N).WhyNot);
    }

    [Fact]
    public void TheTargetIsThePathWhenItHashesBackElseTheHash()
    {
        Assert.Equal("data/maps/a.bin", BinDeclarations.TargetOf("DATA/Maps/A.bin", HashAlgorithms.WadPath("data/maps/a.bin")));
        Assert.Equal("00000000000000ff", BinDeclarations.TargetOf("00000000000000ff.bin", 0xff));
    }

    [Fact]
    public void RiotsOwnBinsConvertWithoutRefusal()
    {
        // every Map11 bin, with one value changed on its first object: all must be declarable
        const string wad = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping\Map11.wad.client";
        if (!File.Exists(wad)) return;
        var db = new HashSyncService().LoadLocal(_ => { });
        using var archive = ReyEngine.Core.Wad.WadArchive.Open(wad, new WadPathResolver(db));
        int declared = 0;
        foreach (var e in archive.Entries.Where(x => x.IsResolved && x.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)).Take(40))
        {
            byte[] riot = archive.Extract(e);
            BinTree tree;
            try { tree = SafeBinTree.Parse(riot, out var issues); if (issues.Count > 0) continue; } catch { continue; }
            var first = tree.Objects.Values.FirstOrDefault(o => o.Properties.Values.Any(p => p is BinTreeU32));
            if (first is null) continue;
            var u = (BinTreeU32)first.Properties.Values.First(p => p is BinTreeU32);
            first.Properties[u.NameHash] = new BinTreeU32(u.NameHash, u.Value + 1);
            using var ms = new MemoryStream();
            tree.Write(ms);
            var c = BinDeclarations.Convert(BinDeclarations.TargetOf(e.Path, e.PathHash), riot, ms.ToArray(), N);
            Assert.True(c.Declared, $"{e.Path}: {c.WhyNot}");
            Assert.Equal(1, c.Properties);
            declared++;
        }
        Assert.True(declared > 0);
    }
}
