using System.IO.Hashing;
using System.Text;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M850: ritobin text with 8-byte hashes, as ritobin-tools 0.2.3 / ltk_ritobin 0.11.0 (PR LeagueToolkit/ritobin-tools#23) prints and reads
/// it. <c>StaticMaterialDef.name</c> of patch 16.21 is the one 8-byte hash ReyEngine holds (a U64 stand-in, <see cref="BinWireNormalizer"/>);
/// the text shows it as <c>hash[u64] = 0x...</c>, and the reader takes that, <c>hash[u64,xxh3]</c> / <c>hash[u64,xxh64]</c> with a string, and
/// <c>hash[u32,fnv1a]</c> / <c>hash[u32]</c> for the four-byte hash.
/// </summary>
public sealed class RitobinWideHashTests
{
    private const string Map11 = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping\Map11.wad.client";
    private const string BaseSrx = "data/maps/mapgeometry/map11/base_srx.materials.bin";

    private static uint H(string s) => HashAlgorithms.Fnv1a(s);
    private static readonly uint NameField = H("name");
    private static readonly uint MaterialClass = H("StaticMaterialDef");
    private const string ZacPath = "Characters/Zac/Skins/Skin31/Materials/ult";

    private static string File1(string body) =>
        "#PROP_text\ntype: string = \"PROP\"\nversion: u32 = 3\nlinked: list[string] = {\n}\nentries: map[hash,embed] = {\n"
        + "  \"Test/Mat\" = StaticMaterialDef {\n" + body + "  }\n}\n";

    private static BinTree? Read(string text, out IReadOnlyList<RitobinError> errors) => RitobinTextReader.Read(text, out errors);

    private static BinTreeProperty Field(BinTree tree, uint field) => tree.Objects[H("Test/Mat")].Properties[field];

    private static ulong Xxh3Lower(string s) => XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(s.ToLowerInvariant()));

    // ===================================================== writing

    [Fact]
    public void AMaterialNameIsWrittenAsAnEightByteHashWithSixteenLowercaseDigits()
    {
        var material = new BinTreeObject(H("Test/Mat"), MaterialClass, new BinTreeProperty[]
        {
            new BinTreeU64(NameField, 0x1AUL),
            new BinTreeU32(H("type"), 0),
        });
        var tree = new BinTree(new[] { material }, Array.Empty<string>());

        string text = RitobinText.Write(tree);

        Assert.Contains($"{NameLiteral()}: hash[u64] = 0x000000000000001a", text);
        Assert.DoesNotContain(": u64 = 26", text);
    }

    private static string NameLiteral() => $"0x{NameField:x8}";

    [Fact]
    public void AU64AnywhereElseIsStillAPlainU64()
    {
        var other = new BinTreeObject(H("Test/Other"), H("SomethingElse"), new BinTreeProperty[] { new BinTreeU64(NameField, 5) });
        var materialWithOtherU64 = new BinTreeObject(H("Test/Mat"), MaterialClass, new BinTreeProperty[] { new BinTreeU64(H("seed"), 7) });
        string text = RitobinText.Write(new BinTree(new[] { other, materialWithOtherU64 }, Array.Empty<string>()));

        Assert.DoesNotContain("hash[u64]", text);
        Assert.Contains(": u64 = 5", text);
        Assert.Contains(": u64 = 7", text);
    }

    [Fact]
    public void TheNameInsideANestedStructOfTheMaterialClassIsFoundByItsOwner()
    {
        // a StaticMaterialDef reached as a struct value, not as an entry
        var inner = new BinTreeStruct(H("holder"), MaterialClass, new BinTreeProperty[] { new BinTreeU64(NameField, 0xABCD) });
        var host = new BinTreeObject(H("Test/Host"), H("HostClass"), new BinTreeProperty[] { inner });
        string text = RitobinText.Write(new BinTree(new[] { host }, Array.Empty<string>()));
        Assert.Contains("hash[u64] = 0x000000000000abcd", text);
    }

    // ===================================================== reading

    [Fact]
    public void AHexLiteralOfTheMaterialNameBecomesTheStandInThatWritesBack()
    {
        var tree = Read(File1("    name: hash[u64] = 0x5d07ca0d22ff9588\n"), out var errors);
        Assert.Empty(errors);
        var name = Assert.IsType<BinTreeU64>(Field(tree!, NameField));
        Assert.Equal(0x5d07ca0d22ff9588UL, name.Value);

        // and the write path turns the stand-in into 0x11 + eight bytes
        byte[] wire = BinWireNormalizer.Denormalize(BinTreeIo.Serialize(tree!));
        Assert.Equal(new[] { (byte)0x11 }, NameFieldBytes(wire).Select(f => f.Type).ToArray());
        Assert.Equal(BitConverter.GetBytes(0x5d07ca0d22ff9588UL), NameFieldBytes(wire).Single().Value);
    }

    private static List<(byte Type, byte[] Value)> NameFieldBytes(byte[] bytes)
    {
        var found = new List<(byte, byte[])>();
        var needle = BitConverter.GetBytes(NameField);
        for (int i = 0; i + 13 <= bytes.Length; i++)
            if (bytes.AsSpan(i, 4).SequenceEqual(needle) && bytes[i + 4] == 0x11) found.Add((0x11, bytes.AsSpan(i + 5, 8).ToArray()));
        return found;
    }

    [Fact]
    public void TheHandWrittenXxh3FormHashesTheLowercasedPathLikeTheGameDoes()
    {
        var tree = Read(File1($"    name: hash[u64,xxh3] = \"{ZacPath}\"\n"), out var errors);
        Assert.Empty(errors);
        ulong value = Assert.IsType<BinTreeU64>(Field(tree!, NameField)).Value;

        Assert.Equal(BinWireNormalizer.NameHash(ZacPath), value);                 // the 16.21 name hash
        Assert.Equal(Xxh3Lower(ZacPath), value);
        // the same path in other casing is the same hash (ltk_ritobin's hasher lowercases ASCII)
        Assert.Equal(value, RitobinTextReader.HashText(ZacPath.ToUpperInvariant(), "xxh3", 8));
        // ritobin-tools' README example: this path and 0x5d07ca0d22ff9588
        Assert.Equal(0x5d07ca0d22ff9588UL, value);
    }

    [Fact]
    public void Xxh64AndTheFourByteFormsHashTheSameWayLtkRitobinDoes()
    {
        var tree = Read(File1(
            "    name: hash[u64,xxh64] = \"Some/Path\"\n"
            + "    a: hash[u32,fnv1a] = \"Some/Path\"\n"
            + "    b: hash[u32] = 0x0000beef\n"
            + "    c: hash[u32,xxh3] = \"Some/Path\"\n"
            + "    d: hash = \"Some/Path\"\n"), out var errors);
        Assert.Empty(errors);
        var props = tree!.Objects[H("Test/Mat")].Properties;

        Assert.Equal(XxHash64.HashToUInt64(Encoding.UTF8.GetBytes("some/path"), 0), Assert.IsType<BinTreeU64>(props[NameField]).Value);
        Assert.Equal(H("Some/Path"), Assert.IsType<BinTreeHash>(props[H("a")]).Value);
        Assert.Equal(0xbeefu, Assert.IsType<BinTreeHash>(props[H("b")]).Value);
        Assert.Equal((uint)Xxh3Lower("Some/Path"), Assert.IsType<BinTreeHash>(props[H("c")]).Value);   // the low 32 bits
        Assert.Equal(H("Some/Path"), Assert.IsType<BinTreeHash>(props[H("d")]).Value);               // plain hash is hash[u32,fnv1a]
    }

    [Fact]
    public void AWidthWithNoFunctionTakesAHexLiteralOnly()
    {
        foreach (string bad in new[] { "name: hash[u64] = \"Some/Path\"", "a: hash[u32] = \"Some/Path\"", "name: hash[u64] = SomePath" })
        {
            Assert.Null(Read(File1("    " + bad + "\n"), out var errors));
            Assert.Contains("hex literal only", Assert.Single(errors).Message);
        }
        Assert.NotNull(Read(File1("    a: hash[u32] = 0xffffffff\n"), out _));
    }

    [Fact]
    public void ALiteralThatDoesNotFitTheWidthOrAnEightByteHashAnywhereElseIsRefused()
    {
        Assert.Null(Read(File1("    a: hash[u32] = 0x100000000\n"), out var tooWide));
        Assert.Contains("does not fit", Assert.Single(tooWide).Message);

        // the tree has no eight-byte hash to hold it, so it cannot be written back: refused with the reason
        Assert.Null(Read(File1("    other: hash[u64] = 0x1\n"), out var elsewhere));
        Assert.Contains("StaticMaterialDef.name", Assert.Single(elsewhere).Message);

        Assert.Null(Read(File1("    a: hash[u16] = 0x1\n"), out var width));
        Assert.Contains("u32 or u64", Assert.Single(width).Message);
        Assert.Null(Read(File1("    a: hash[u32,md5] = \"x\"\n"), out var function));
        Assert.Contains("fnv1a, xxh64 or xxh3", Assert.Single(function).Message);
    }

    [Fact]
    public void ATypeArgumentTakesTheSameFormsAndPlainU64OfTheNameStillReads()
    {
        var tree = Read(File1(
            "    list: list[hash[u32,fnv1a]] = {\n      \"a\"\n      0x5\n    }\n"
            + "    m: map[hash[u32,fnv1a],hash] = {\n      \"k\" = \"v\"\n    }\n"
            + "    name: u64 = 99\n"), out var errors);
        Assert.Empty(errors);
        var props = tree!.Objects[H("Test/Mat")].Properties;
        var list = Assert.IsType<BinTreeContainer>(props[H("list")]);
        Assert.Equal(new[] { H("a"), 5u }, list.Elements.Cast<BinTreeHash>().Select(h => h.Value));
        Assert.Equal(99UL, Assert.IsType<BinTreeU64>(props[NameField]).Value);   // the older text for this field still reads
    }

    [Fact]
    public void PrintingThenReadingGivesTheSameTree()
    {
        var material = new BinTreeObject(H("Test/Mat"), MaterialClass, new BinTreeProperty[]
        {
            new BinTreeU64(NameField, 0xFEDCBA9876543210UL),
            new BinTreeHash(H("a"), 0x12345678),
        });
        var tree = new BinTree(new[] { material }, Array.Empty<string>());
        string text = RitobinText.Write(tree);

        var again = Read(text, out var errors);
        Assert.Empty(errors);
        Assert.Equal(BinTreeIo.Serialize(tree), BinTreeIo.Serialize(again!));
        Assert.Equal(text, RitobinText.Write(again!));
    }

    // ===================================================== real data

    [Fact]
    public void RealBaseSrxPrintsAndParsesBackToTheSameBin()
    {
        if (!File.Exists(Map11)) return;
        byte[]? riot;
        try
        {
            using var wad = WadArchive.Open(Map11);
            ulong h = HashAlgorithms.WadPath(BaseSrx);
            riot = wad.TryGetEntry(h, out _) ? wad.Extract(h) : null;
        }
        catch { return; }
        if (riot is null) return;
        var tree = SafeBinTree.Parse(riot, out var issues);
        Assert.Empty(issues);
        var names = tree.Objects.Values.Where(o => o.ClassHash == MaterialClass).Select(o => o.Properties[NameField]).ToList();
        if (names.Count == 0 || names[0] is not BinTreeU64) return;   // not a 16.21 install

        string text = RitobinText.Write(tree);

        // every material name prints as a hash[u64] and none as a plain u64
        // (more lines than entries: a StaticMaterialDef nested in another object is a material too, and its name is the same eight bytes)
        Assert.True(text.Split('\n').Count(l => l.Contains("hash[u64] = 0x") && l.TrimStart().StartsWith("0x8d39bde6:", StringComparison.Ordinal)) >= names.Count);
        Assert.DoesNotContain("0x8d39bde6: u64", text);

        var again = Read(text, out var errors);
        Assert.Empty(errors);
        byte[] before = BinWireNormalizer.Denormalize(BinTreeIo.Serialize(tree));
        byte[] after = BinWireNormalizer.Denormalize(BinTreeIo.Serialize(again!));
        Assert.Equal(before.Length, after.Length);
        // every material name is the same type byte and the same eight bytes, in the same places
        var namesBefore = NameFieldBytes(before);
        Assert.True(namesBefore.Count >= names.Count);
        Assert.Equal(namesBefore.Select(n => Convert.ToHexString(n.Value)), NameFieldBytes(after).Select(n => Convert.ToHexString(n.Value)));
        // and the text is a fixed point
        Assert.Equal(text, RitobinText.Write(again!));
        // (Not asserted: the whole bin byte for byte. A bin's empty option<f32> comes back from ritobin text with element type None - the
        // reader builds BinTreeOptional from its value alone - which is older than this change and has nothing to do with hashes.)
        Assert.Equal(names.Cast<BinTreeU64>().Select(n => n.Value),
            again!.Objects.Values.Where(o => o.ClassHash == MaterialClass).Select(o => ((BinTreeU64)o.Properties[NameField]).Value));
    }
}
