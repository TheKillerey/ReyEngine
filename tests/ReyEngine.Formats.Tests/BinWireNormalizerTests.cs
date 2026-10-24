using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.LtkGameData;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// Patch 16.21: <c>StaticMaterialDef.name</c> is type 0x11 followed by an 8-byte XXH3-64 of the lowercased name.
/// Synthetic tests prove the framing logic; the real-data tests read the installed game and are no-ops (not
/// failures) when it is absent, like <see cref="LinkedMaterialResolutionTests"/>.
/// </summary>
public sealed class BinWireNormalizerTests
{
    private const string Map11 = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping\Map11.wad.client";
    private const string Aatrox = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Champions\Aatrox.wad.client";
    private const string BaseSrx = "data/maps/mapgeometry/map11/base_srx.materials.bin";
    private const string Legacy1620 = @"D:\ProjectReyMaps\_ModUpdate\_update_16.20\riot_16.20\data\maps\mapgeometry\map11\base_srx.materials.bin";

    private static uint H(string s) => HashAlgorithms.Fnv1a(s);
    private static readonly uint NameField = H("name");
    private static readonly uint MaterialClass = H("StaticMaterialDef");

    private static ulong Xxh3Lower(string s) => XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(s.ToLowerInvariant()));

    private static byte[] Write(params BinTreeObject[] objects)
    {
        var ms = new MemoryStream();
        new BinTree(objects, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    private static BinTreeObject Material(string path, string name, BinTreeProperty? extra = null)
    {
        var props = new List<BinTreeProperty> { new BinTreeString(NameField, name), new BinTreeU32(H("type"), 0) };
        if (extra is not null) props.Add(extra);
        return new BinTreeObject(H(path), MaterialClass, props);
    }

    /// <summary>The name field of every StaticMaterialDef in <paramref name="bytes"/> as (typeByte, 8 value bytes or empty).</summary>
    private static List<(byte Type, byte[] Value)> NameFields(byte[] bytes)
    {
        var found = new List<(byte, byte[])>();
        var needle = new byte[] { 0xe6, 0xbd, 0x39, 0x8d };
        for (int i = 0; i + 13 <= bytes.Length; i++)
        {
            if (!bytes.AsSpan(i, 4).SequenceEqual(needle)) continue;
            if (bytes[i + 4] == 0x11) found.Add((0x11, bytes.AsSpan(i + 5, 8).ToArray()));
        }
        return found;
    }

    [Fact]
    public void WritingAStringNameProducesTheHashFormAndReadingItBackIsStrictAndStable()
    {
        var inner = new BinTreeEmbedded(H("inner"), MaterialClass, new BinTreeProperty[] { new BinTreeString(NameField, "Maps/Nested/Mat") });
        var bytes = BinTreeIo.Serialize(new BinTree(new[]
        {
            Material("Maps/Test/Alpha", "Maps/Test/Alpha", new BinTreeContainer(H("list"), BinPropertyType.Embedded, new BinTreeProperty[] { inner })),
            // a different class with a String "name" must be left alone
            new BinTreeObject(H("other"), H("MapSkin"), new BinTreeProperty[] { new BinTreeString(NameField, "Keep/Me") }),
        }, Array.Empty<string>()));

        var fields = NameFields(bytes);
        Assert.Equal(2, fields.Count);
        Assert.Equal(Xxh3Lower("Maps/Test/Alpha"), BinaryPrimitives.ReadUInt64LittleEndian(fields[0].Value));
        Assert.Equal(Xxh3Lower("Maps/Nested/Mat"), BinaryPrimitives.ReadUInt64LittleEndian(fields[1].Value));
        Assert.Contains("Keep/Me", Encoding.ASCII.GetString(bytes));

        var tree = SafeBinTree.Parse(bytes, out var issues);
        Assert.Empty(issues);
        Assert.False(SafeBinTree.IsLossy(tree, out _));
        var name = Assert.IsType<BinTreeU64>(tree.Objects[H("Maps/Test/Alpha")].Properties[NameField]);
        Assert.Equal(Xxh3Lower("Maps/Test/Alpha"), name.Value);
        Assert.IsType<BinTreeString>(tree.Objects[H("other")].Properties[NameField]);

        // and the hash form survives another trip unchanged
        Assert.Equal(bytes, BinTreeIo.Serialize(tree));
        Assert.Equal(bytes, BinTreeIo.Serialize(BinTreeIo.Read(bytes)));
    }

    [Fact]
    public void ABinWithoutTheMaterialNameFieldIsReturnedAsTheSameArray()
    {
        var bytes = Write(new BinTreeObject(H("x"), H("MapSkin"), new BinTreeProperty[] { new BinTreeString(NameField, "n") }));
        Assert.Same(bytes, BinWireNormalizer.Normalize(bytes));
        Assert.Same(bytes, BinWireNormalizer.Denormalize(bytes));
    }

    [Fact]
    public void GarbageIsHandedBackUntouched()
    {
        var bytes = new byte[64];
        new byte[] { 0xe6, 0xbd, 0x39, 0x8d, 0x09, 0x34, 0x9d, 0xff }.CopyTo(bytes, 8);
        Assert.Same(bytes, BinWireNormalizer.Normalize(bytes));
    }

    [Fact]
    public void ClonedMaterialNameIsTheHashOfTheLowercasedNewPath()
    {
        var hashForm = BinTreeIo.Read(BinTreeIo.Serialize(new BinTree(new[] { Material("Maps/Test/Alpha", "Maps/Test/Alpha") }, Array.Empty<string>())));
        var material = hashForm.Objects[H("Maps/Test/Alpha")];
        MapMaterialFactory.SetMaterialName(material, "Maps/Test/NEW_Clone");
        Assert.Equal(Xxh3Lower("Maps/Test/NEW_Clone"), Assert.IsType<BinTreeU64>(material.Properties[NameField]).Value);
        var bytes = BinTreeIo.Serialize(new BinTree(new[] { material }, Array.Empty<string>()));
        Assert.Equal(Xxh3Lower("maps/test/new_clone"), BinaryPrimitives.ReadUInt64LittleEndian(NameFields(bytes).Single().Value));

        var stringForm = Material("Maps/Test/Beta", "Maps/Test/Beta");
        MapMaterialFactory.SetMaterialName(stringForm, "Maps/Test/Gamma");
        var written = BinTreeIo.Serialize(new BinTree(new[] { stringForm }, Array.Empty<string>()));
        Assert.Equal(Xxh3Lower("Maps/Test/Gamma"), BinaryPrimitives.ReadUInt64LittleEndian(NameFields(written).Single().Value));
    }

    [Fact]
    public void RemoveGeneratedMaterialsFindsHashNamedMaterialsThroughTheirPath()
    {
        var bin = BinTreeIo.Serialize(new BinTree(new[]
        {
            Material("LegacyPort/Test/A", "LegacyPort/Test/A"),
            Material("Maps/Test/Keep", "Maps/Test/Keep"),
        }, Array.Empty<string>()));
        var names = new Dictionary<uint, string> { [H("LegacyPort/Test/A")] = "LegacyPort/Test/A", [H("Maps/Test/Keep")] = "Maps/Test/Keep" };
        var cleaned = MapMaterialFactory.RemoveGeneratedMaterials(bin, "LegacyPort/", out int removed, out var error,
            h => names.TryGetValue(h, out var n) ? n : null);
        Assert.Null(error);
        Assert.Equal(1, removed);
        Assert.Single(SafeBinTree.Parse(cleaned!).Objects);
    }

    [Fact]
    public void PropCodecRoundTripsTheEightByteNameBytesForBytes()
    {
        var bytes = BinTreeIo.Serialize(new BinTree(new[]
        {
            Material("Maps/Test/Alpha", "Maps/Test/Alpha"),
            Material("Maps/Test/Beta", "Maps/Test/Beta"),
        }, Array.Empty<string>()));
        var bin = PropCodec.ReadProp(bytes);
        Assert.Equal(bytes, PropCodec.WriteProp(bin));
    }

    // ------------------------------------------------------------------------------------ real data

    private static byte[]? ReadWad(string wadPath, string entry)
    {
        if (!File.Exists(wadPath)) return null;
        try
        {
            using var wad = WadArchive.Open(wadPath);
            ulong h = HashAlgorithms.WadPath(entry);
            return wad.TryGetEntry(h, out _) ? wad.Extract(h) : null;
        }
        catch { return null; }
    }

    /// <summary>Each object's whole record (size field included) keyed by path hash.</summary>
    private static Dictionary<uint, byte[]> Entries(byte[] d)
    {
        int p = 4;
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p)); p += 4;
        if (version >= 2)
        {
            uint deps = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p)); p += 4;
            for (uint i = 0; i < deps; i++) p += 2 + BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p));
        }
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p)); p += 4 + (int)count * 4;
        var map = new Dictionary<uint, byte[]>();
        for (uint i = 0; i < count; i++)
        {
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p));
            map[BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p + 4))] = d.AsSpan(p, size + 4).ToArray();
            p += size + 4;
        }
        return map;
    }

    [Fact]
    public void RealBaseSrx1621ParsesStrictlyAndSerialisesBackToRiotsBytesPerEntry()
    {
        var riot = ReadWad(Map11, BaseSrx);
        if (riot is null || NameFields(riot).Count == 0) return;   // not a 16.21 install

        var tree = SafeBinTree.Parse(riot, out var issues);
        Assert.Empty(issues);
        Assert.False(SafeBinTree.IsLossy(tree, out _));
        Assert.All(tree.Objects.Values.Where(o => o.ClassHash == MaterialClass), o => Assert.IsType<BinTreeU64>(o.Properties[NameField]));

        var strict = BinTreeIo.Read(riot);   // throws if LeagueToolkit is not satisfied
        var again = BinTreeIo.Serialize(strict);
        var before = Entries(riot);
        var after = Entries(again);
        Assert.Equal(before.Count, after.Count);
        foreach (var (path, body) in before) Assert.True(body.AsSpan().SequenceEqual(after[path]), $"entry {path:x8} differs after the round trip");
    }

    [Fact]
    public void RealMaterialNamesAreTheHashOfTheLowercasedEntryPath()
    {
        var riot = ReadWad(Map11, BaseSrx);
        if (riot is null || NameFields(riot).Count == 0) return;
        var tree = BinTreeIo.Read(riot);
        var legacy = File.Exists(Legacy1620) ? File.ReadAllBytes(Legacy1620) : null;
        if (legacy is null) return;
        // every 16.20 material: its String name must hash to what 16.21 stores for the same object
        var old = SafeBinTree.Parse(legacy);
        int compared = 0;
        foreach (var (key, obj) in old.Objects)
        {
            if (obj.ClassHash != MaterialClass || obj.Properties[NameField] is not BinTreeString s) continue;
            if (!tree.Objects.TryGetValue(key, out var current) || current.Properties[NameField] is not BinTreeU64 u) continue;
            Assert.Equal(Xxh3Lower(s.Value), u.Value);
            compared++;
        }
        Assert.True(compared > 100, $"only {compared} materials compared");

        // and the 16.20 file, written by ReyEngine, comes out in the 16.21 form
        var written = BinTreeIo.Serialize(old);
        Assert.True(NameFields(written).Count >= old.Objects.Values.Count(o => o.ClassHash == MaterialClass));   // nested material structs add more
        Assert.Empty(SafeBinTree.Parse(written, out var issues).Objects.Values.Where(o => o.Properties.GetValueOrDefault(NameField) is BinTreeString && o.ClassHash == MaterialClass));
        Assert.Empty(issues);
    }

    [Fact]
    public void RealChampionSkinBinWithMaterialsParsesStrictly()
    {
        if (!File.Exists(Aatrox)) return;
        byte[]? skin = null;
        for (int n = 0; n < 40 && skin is null; n++)
        {
            var candidate = ReadWad(Aatrox, $"data/characters/aatrox/skins/skin{n}.bin");
            if (candidate is not null && NameFields(candidate).Count > 0) skin = candidate;
        }
        if (skin is null) return;   // not a 16.21 install (no Aatrox skin carries a hashed material name)
        Assert.NotEmpty(NameFields(skin));
        var tree = SafeBinTree.Parse(skin, out var issues);
        Assert.Empty(issues);
        Assert.False(SafeBinTree.IsLossy(tree, out _));
        BinTreeIo.Read(skin);
        Assert.Equal(Entries(skin).Count, Entries(BinTreeIo.Serialize(tree)).Count);
    }

    [Fact]
    public void RealBaseSrxSurvivesPropCodecByteForByte()
    {
        var riot = ReadWad(Map11, BaseSrx);
        if (riot is null || NameFields(riot).Count == 0) return;
        var written = PropCodec.WriteProp(PropCodec.ReadProp(riot));
        Assert.Equal(riot.Length, written.Length);
        Assert.True(riot.AsSpan().SequenceEqual(written));
    }

    // ------------------------------------------------------------------------------------ framing corners

    private static BinTreeEmbedded InnerMaterial(string name) =>
        new(H("inner"), MaterialClass, new BinTreeProperty[] { new BinTreeString(NameField, name) });

    private static BinTree NestedTree() => new(new[]
    {
        new BinTreeObject(H("holder"), H("MapContainer"), new BinTreeProperty[]
        {
            new BinTreeMap(H("byName"), BinPropertyType.String, BinPropertyType.Embedded, new[]
            {
                new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeString(0, "k1"), InnerMaterial("Maps/A/One")),
                new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeString(0, "k2"), InnerMaterial("Maps/A/Two")),
            }),
            new BinTreeOptional(H("maybe"), InnerMaterial("Maps/A/Three")),
            new BinTreeOptional(H("none"), null),
            new BinTreeStruct(H("nothing"), 0, Array.Empty<BinTreeProperty>()),   // a null pointer: four bytes, nothing follows
            new BinTreeString(H("tail"), "after the null"),
        }),
    }, Array.Empty<string>());

    [Fact]
    public void MaterialsNestedInAMapAndAnOptionalBesideANullPointerRoundTrip()
    {
        var bytes = BinTreeIo.Serialize(NestedTree());
        var fields = NameFields(bytes);
        Assert.Equal(3, fields.Count);
        Assert.Equal(new[] { Xxh3Lower("Maps/A/One"), Xxh3Lower("Maps/A/Two"), Xxh3Lower("Maps/A/Three") },
            fields.Select(f => BinaryPrimitives.ReadUInt64LittleEndian(f.Value)).ToArray());

        var tree = SafeBinTree.Parse(bytes, out var issues);
        Assert.Empty(issues);
        var holder = tree.Objects[H("holder")];
        Assert.Equal("after the null", Assert.IsType<BinTreeString>(holder.Properties[H("tail")]).Value);
        var map = Assert.IsType<BinTreeMap>(holder.Properties[H("byName")]);
        Assert.All(map, kv => Assert.IsType<BinTreeU64>(((BinTreeEmbedded)kv.Value).Properties[NameField]));
        Assert.Equal(bytes, BinTreeIo.Serialize(tree));
    }

    [Fact]
    public void DenormalizingBytesThatAlreadyHoldTheWireFormChangesNothing()
    {
        var bytes = BinTreeIo.Serialize(NestedTree());
        Assert.Same(bytes, BinWireNormalizer.Denormalize(bytes));
    }

    [Fact]
    public void RemoveGeneratedMaterialsFindsAHashNamedMaterialWhoseInventedPathNoTableKnows()
    {
        const string generated = "LegacyPort/map11/Grass_31861292fa7a";
        var bin = BinTreeIo.Serialize(new BinTree(new[]
        {
            Material(generated, generated),
            Material("Maps/Test/Keep", "Maps/Test/Keep"),
        }, Array.Empty<string>()));
        // the real resolver knows neither path; the map's mesh strings still carry the generated one
        var mapgeo = Encoding.ASCII.GetBytes("  " + generated + " Maps/Test/Keep ");
        var cleaned = MapMaterialFactory.RemoveGeneratedMaterials(bin, "LegacyPort/", out int removed, out var error, _ => null,
            MapMaterialFactory.HarvestPaths(mapgeo, "LegacyPort/"));
        Assert.Null(error);
        Assert.Equal(1, removed);
        Assert.True(SafeBinTree.Parse(cleaned!).Objects.ContainsKey(H("Maps/Test/Keep")));
        // and with no path knowledge at all nothing is guessed
        MapMaterialFactory.RemoveGeneratedMaterials(bin, "LegacyPort/", out int none, out _, _ => null);
        Assert.Equal(0, none);
    }

    [Fact]
    public void ABindingAnswersToItsRealPathEvenWhenNoTableNamesIt()
    {
        var bin = BinTreeIo.Serialize(new BinTree(new[] { Material("Maps/Test/Unlisted_MAT", "Maps/Test/Unlisted_MAT") }, Array.Empty<string>()));
        var binding = Assert.Single(MaterialDocument.Parse(bin, h => h == MaterialClass ? "StaticMaterialDef" : null).Materials);
        Assert.StartsWith("0x", binding.Name, StringComparison.Ordinal);
        Assert.True(binding.IsNamed("Maps/Test/Unlisted_MAT"));
        Assert.True(binding.IsNamed("maps/test/unlisted_mat"));
        Assert.False(binding.IsNamed("Maps/Test/Other"));
        Assert.Equal(Xxh3Lower("Maps/Test/Unlisted_MAT"), binding.NameHash);
    }
}
