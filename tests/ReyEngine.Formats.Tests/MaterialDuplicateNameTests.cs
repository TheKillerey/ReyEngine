using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M789: a StaticMaterialDef that names the same switch, parameter or sampler twice.
///
/// <para>Riot ships them: 16 materials repeat a switch, 34 a parameter and 26 a sampler (census over every
/// PROP bin in the install). <see cref="MaterialDocument.Parse"/> threw
/// <c>ArgumentException: An item with the same key has already been added</c> on every repeated switch,
/// from <c>MaterialBinding.Switches</c>' ToDictionary (reached through MaterialProfiles.Classify), so the
/// whole bin failed. That covered Map22's <c>thelastdrop.materials.bin</c>, Soraka skin53-61, and TFTSet18's
/// DA_18_Soraka. The shipped data does not show which copy the client honours (the evidence is on the
/// internal <c>MaterialBinding.FirstByName</c>), so the name-keyed views keep the FIRST. Nothing is dropped:
/// every entry keeps its row and element, and a save writes the lists back as read.</para>
///
/// <para>The real-data tests are no-ops (not failures) when the game install is absent, the same convention
/// as <see cref="LinkedMaterialResolutionTests"/>.</para>
/// </summary>
public sealed class MaterialDuplicateNameTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static readonly Dictionary<uint, string> Known = new()
    {
        [H("StaticMaterialDef")] = "StaticMaterialDef",
        [H("Shaders/SkinnedMesh/TFT_Flag_Wave")] = "Shaders/SkinnedMesh/TFT_Flag_Wave",
    };
    private static string? Resolve(uint h) => Known.TryGetValue(h, out var n) ? n : null;

    private const string FlagMaterial = "Maps/KitPieces/TFT/Set13/Materials/Default/TheLastDrop_Level9_Wave_TableA2";

    private static BinTreeEmbedded Switch(string name, bool? on)
    {
        var props = new List<BinTreeProperty> { new BinTreeString(H("name"), name) };
        if (on is { } value) props.Add(new BinTreeBool(H("on"), value));
        return new BinTreeEmbedded(0, H("StaticMaterialSwitchDef"), props);
    }

    private static BinTreeEmbedded Param(string name, float? x)
    {
        var props = new List<BinTreeProperty> { new BinTreeString(H("name"), name) };
        if (x is { } v) props.Add(new BinTreeVector4(H("value"), new System.Numerics.Vector4(v, 0f, 0f, 0f)));
        return new BinTreeEmbedded(0, H("StaticMaterialShaderParamDef"), props);
    }

    private static BinTreeEmbedded Sampler(string name, string path) =>
        new(0, 0x0904b150, new BinTreeProperty[]
        {
            new BinTreeString(H("TextureName"), name),
            new BinTreeString(H("texturePath"), path),
        });

    /// <summary>TheLastDrop_Level9_Wave_TableA2's switches in shipped order (an absent 'on' is enabled), with
    /// Soraka Body_Flowmap_inst's repeated MaxSpec / LQ_Lighting_Intensity / Specular_Mask beside them.</summary>
    private static BinTreeObject FlagObject(uint pathHash) => new(pathHash, H("StaticMaterialDef"), new BinTreeProperty[]
    {
        new BinTreeString(H("name"), FlagMaterial),
        new BinTreeUnorderedContainer(H("samplerValues"), BinPropertyType.Embedded, new BinTreeProperty[]
        {
            Sampler("Diffuse_Texture", "assets/maps/particles/tft/flag.tex"),
            Sampler("Specular_Mask", "assets/characters/soraka/skins/skin53/black.tex"),
            Sampler("Specular_Mask", "assets/shared/materials/white.tex"),
        }),
        new BinTreeUnorderedContainer(H("paramValues"), BinPropertyType.Embedded, new BinTreeProperty[]
        {
            Param("MaxSpec", 64f),
            Param("LQ_Lighting_Intensity", 1f),
            Param("LQ_Lighting_Intensity", null),   // the shipped second copy authors no value (M673: a zero)
            Param("MaxSpec", 43.2275f),
        }),
        new BinTreeUnorderedContainer(H("switches"), BinPropertyType.Embedded, new BinTreeProperty[]
        {
            Switch("USE_ALBEDO_REMAP", null),
            Switch("USE_RIM", null),
            Switch("UNLIT_MODE", false),
            Switch("USE_CUSTOM_OBJECT_NORMAL", null),
            Switch("USE_CUSTOM_OBJECT_NORMAL", false),
            Switch("UNLIT_MODE", null),
        }),
        new BinTreeContainer(H("techniques"), BinPropertyType.Embedded, new BinTreeProperty[]
        {
            new BinTreeEmbedded(0, H("StaticMaterialTechniqueDef"), new BinTreeProperty[]
            {
                new BinTreeContainer(H("passes"), BinPropertyType.Embedded, new BinTreeProperty[]
                {
                    new BinTreeEmbedded(0, H("StaticMaterialPassDef"), new BinTreeProperty[]
                    {
                        new BinTreeObjectLink(H("shader"), H("Shaders/SkinnedMesh/TFT_Flag_Wave")),
                    }),
                }),
            }),
        }),
    });

    private static byte[] Write(BinTree tree)
    {
        using var ms = new MemoryStream();
        tree.Write(ms);
        return ms.ToArray();
    }

    private static byte[] FlagBin() => Write(new BinTree(new[] { FlagObject(H(FlagMaterial)) }, Array.Empty<string>()));

    // ===================================================== synthetic: the parse itself

    [Fact]
    public void ARepeatedSwitchNameParsesAndItsFirstEntryWins()
    {
        var doc = MaterialDocument.Parse(FlagBin(), Resolve);   // threw ArgumentException before M789
        var m = Assert.Single(doc.Materials);

        Assert.False(m.Switches["UNLIT_MODE"]);                 // off, then (absent = on)
        Assert.True(m.Switches["USE_CUSTOM_OBJECT_NORMAL"]);    // (absent = on), then off
        Assert.True(m.Switches["USE_ALBEDO_REMAP"]);
        Assert.Equal(4, m.Switches.Count);
        // the permutation checks read ClientVisibleSwitches - same reading, same winner
        Assert.False(m.ClientVisibleSwitches["UNLIT_MODE"]);

        // ...and nothing is dropped: every entry keeps a row, in file order
        Assert.Equal(new[] { "USE_ALBEDO_REMAP", "USE_RIM", "UNLIT_MODE", "USE_CUSTOM_OBJECT_NORMAL", "USE_CUSTOM_OBJECT_NORMAL", "UNLIT_MODE" },
            m.AllSwitches.Select(s => s.Name));
        Assert.Equal(new[] { true, true, false, true, false, true }, m.AllSwitches.Select(s => s.On));
        Assert.Equal(2, m.Parameters.Count(p => p.Name == "MaxSpec"));
        Assert.Equal(2, m.Parameters.Count(p => p.Name == "LQ_Lighting_Intensity"));
        Assert.Equal(2, m.Slots.Count(s => s.SamplerName == "Specular_Mask"));
    }

    [Fact]
    public void TheShaderSetupCaptureTakesTheFirstOfEachName()
    {
        var m = Assert.Single(MaterialDocument.Parse(FlagBin(), Resolve).Materials);
        var setup = ShaderMaterialSetups.Capture(m);   // ToDictionary threw here too

        Assert.False(setup.Switches["UNLIT_MODE"]);
        Assert.True(setup.Switches["USE_CUSTOM_OBJECT_NORMAL"]);
        Assert.Equal(64f, setup.Parameters["MaxSpec"].X);
        Assert.Equal(1f, setup.Parameters["LQ_Lighting_Intensity"].X);
        Assert.NotEmpty(ShaderMaterialSetups.CanonicalSignature(setup));
    }

    [Fact]
    public void ARepeatedParameterAloneNoLongerBreaksTheSetupCapture()
    {
        // Quinn's Wings_Mat shape: no repeated switch, so the parse always worked, but Idle_Color twice made
        // Capture's ToDictionary throw. The Workshop catalog swallowed that and dropped the whole bin.
        var obj = new BinTreeObject(H("Wings"), H("StaticMaterialDef"), new BinTreeProperty[]
        {
            new BinTreeString(H("name"), "Characters/Quinn/Skins/Skin14/Materials/Wings_Mat"),
            new BinTreeUnorderedContainer(H("paramValues"), BinPropertyType.Embedded, new BinTreeProperty[]
            {
                Param("modelHeight", 200f),
                Param("Idle_Color", 1f),
                Param("Idle_Bloom", 2f),
                Param("Idle_Color", 0.1372549f),
            }),
        });
        var m = Assert.Single(MaterialDocument.Parse(Write(new BinTree(new[] { obj }, Array.Empty<string>())), Resolve).Materials);

        var setup = ShaderMaterialSetups.Capture(m);
        Assert.Equal(1f, setup.Parameters["Idle_Color"].X);
        Assert.Equal(3, setup.Parameters.Count);
        Assert.Equal(4, m.Parameters.Count);   // both rows still there
    }

    // ===================================================== synthetic: save / round trip

    [Fact]
    public void SavingWritesEveryRepeatBackExactlyAsRead()
    {
        byte[] original = FlagBin();
        var doc = MaterialDocument.Parse(original, Resolve);
        byte[] saved = doc.Serialize();

        var before = new BinTree(new MemoryStream(original, false));
        var after = new BinTree(new MemoryStream(saved, false));   // strict: still a well-formed bin
        var a = Assert.Single(before.Objects.Values);
        var b = Assert.Single(after.Objects.Values);
        Assert.True(BinPropEquality.ObjectsEqual(a, b), "saving a material with repeated names changed it");
        Assert.Equal(6, ((BinTreeContainer)b.Properties[H("switches")]).Elements.Count);
        Assert.Equal(4, ((BinTreeContainer)b.Properties[H("paramValues")]).Elements.Count);
        Assert.Equal(3, ((BinTreeContainer)b.Properties[H("samplerValues")]).Elements.Count);

        // a saved override is read back by the same parser, and a second save is a fixed point
        var reread = MaterialDocument.Parse(saved, Resolve);
        Assert.False(Assert.Single(reread.Materials).Switches["UNLIT_MODE"]);
        Assert.Equal(saved, reread.Serialize());
    }

    [Fact]
    public void EachRepeatStaysIndividuallyEditable()
    {
        var doc = MaterialDocument.Parse(FlagBin(), Resolve);
        var m = Assert.Single(doc.Materials);
        var unlit = m.AllSwitches.Where(s => s.Name == "UNLIT_MODE").ToList();
        Assert.Equal(2, unlit.Count);

        // toggling the SECOND row writes that element only; the first still decides the reading
        unlit[1].SetOn(false);
        Assert.False(m.Switches["UNLIT_MODE"]);
        unlit[0].SetOn(true);
        Assert.True(m.Switches["UNLIT_MODE"]);
        Assert.True(m.IsDirty);

        var saved = new BinTree(new MemoryStream(doc.Serialize(), false));
        var elements = ((BinTreeContainer)saved.Objects.Values.Single().Properties[H("switches")]).Elements
            .Cast<BinTreeStruct>().ToList();
        Assert.Equal(6, elements.Count);
        // an ON switch is written with no 'on' field (M588); an OFF one carries on=false
        Assert.False(elements[2].Properties.ContainsKey(HashAlgorithms.Fnv1aRaw("on")) || elements[2].Properties.ContainsKey(H("on")));
        var secondOn = elements[5].Properties.Values.OfType<BinTreeBool>().Single();
        Assert.False(secondOn.Value);
    }

    // ===================================================== synthetic: a LINKED material reads the same way

    [Fact]
    public void ALinkedMaterialReadsItsRepeatsTheSameWayAsALocalOne()
    {
        // M777 path: the skin names a StaticMaterialDef that lives only in a dependency bin. Such a binding has
        // no live object, so its Switches come from the parse-time snapshot - which used to keep the LAST
        // entry (dictionary indexer) while the live view threw. Both now keep the first.
        const string dependency = "DATA/Characters/Test/Test_Linked.bin";
        uint material = H(FlagMaterial);
        var skin = new BinTree(new[]
        {
            new BinTreeObject(H("Characters/Test/Skins/Skin0"), H("SkinCharacterDataProperties"), new BinTreeProperty[]
            {
                new BinTreeEmbedded(H("skinMeshProperties"), H("SkinMeshDataProperties"), new BinTreeProperty[]
                {
                    new BinTreeString(H("simpleSkin"), "ASSETS/Characters/Test/Skins/Base/Test.skn"),
                    new BinTreeContainer(H("materialOverride"), BinPropertyType.Embedded, new BinTreeProperty[]
                    {
                        new BinTreeEmbedded(0, H("SkinMeshDataProperties_MaterialOverride"), new BinTreeProperty[]
                        {
                            new BinTreeObjectLink(H("material"), material),
                            new BinTreeString(H("submesh"), "Flag"),
                        }),
                    }),
                }),
            }),
        }, new[] { dependency });
        byte[] linked = Write(new BinTree(new[] { FlagObject(material) }, Array.Empty<string>()));

        var doc = MaterialDocument.Parse(Write(skin), Resolve, null,
            p => string.Equals(p, dependency, StringComparison.OrdinalIgnoreCase) ? linked : null);
        var m = doc.Materials.Single(x => x.Name == FlagMaterial);

        Assert.True(m.IsLinked);
        Assert.False(m.Switches["UNLIT_MODE"]);
        Assert.True(m.Switches["USE_CUSTOM_OBJECT_NORMAL"]);
        Assert.Equal(6, m.SwitchEntries.Count);
        Assert.Contains("Flag", m.Submeshes);
    }

    // ===================================================== synthetic: shaderMacros

    [Fact]
    public void MacroKeysDifferingOnlyInCaseDoNotThrowAndTheFirstWins()
    {
        // LeagueToolkit's map is a Dictionary, so an EXACT repeat cannot load at all - but keys that differ only
        // in case load and round-trip, and the case-insensitive view collided on them.
        var obj = new BinTreeObject(H("Mat"), H("StaticMaterialDef"), new BinTreeProperty[]
        {
            new BinTreeString(H("name"), "Mat"),
            new BinTreeMap(H("shaderMacros"), BinPropertyType.String, BinPropertyType.String, new[]
            {
                new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeString(0, "NO_BAKED_LIGHTING"), new BinTreeString(0, "1")),
                new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeString(0, "no_baked_lighting"), new BinTreeString(0, "0")),
            }),
        });
        var m = Assert.Single(MaterialDocument.Parse(Write(new BinTree(new[] { obj }, Array.Empty<string>())), Resolve).Materials);

        Assert.Equal("1", m.Macros["NO_BAKED_LIGHTING"]);
        Assert.True(m.MacroOn(MaterialBinding.MacroNoBakedLighting));   // MacroOn already took the first
        Assert.Equal(2, m.AllMacros.Count());
        Assert.Equal("1", ShaderMaterialSetups.Capture(m).Macros["NO_BAKED_LIGHTING"]);
    }

    // ===================================================== real data

    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";

    private static readonly Lazy<HashDatabase?> Hashes = new(() =>
    {
        try { return new HashSyncService().LoadLocal(_ => { }); } catch { return null; }
    });

    /// <summary>The hash dictionary when there is one, and the few names Parse needs when there is not
    /// (a fresh worktree has no data/hashes) - so the crash path is exercised either way.</summary>
    private static string? Names(uint h) =>
        Hashes.Value is { } db && db.TryGetBinName(h, out var n) ? n : Resolve(h);

    private static byte[]? Read(WadArchive wad, string path)
    {
        ulong h = HashAlgorithms.WadPath(path);
        return wad.TryGetEntry(h, out _) ? wad.Extract(h) : null;
    }

    /// <summary>Every StaticMaterialDef's switch list as the FILE has it, so the assertions follow whatever
    /// the installed patch ships rather than pinning today's values.</summary>
    private static Dictionary<uint, List<(string Name, bool On)>> RawSwitches(byte[] bin)
    {
        var map = new Dictionary<uint, List<(string, bool)>>();
        foreach (var (hash, o) in new BinTree(new MemoryStream(bin, false)).Objects)
        {
            if (o.ClassHash != H("StaticMaterialDef")) continue;
            if (!o.Properties.TryGetValue(H("switches"), out var sp) || sp is not BinTreeContainer sw) continue;
            map[hash] = sw.Elements.OfType<BinTreeStruct>()
                .Where(s => s.Properties.TryGetValue(H("name"), out var n) && n is BinTreeString)
                .Select(s => (((BinTreeString)s.Properties[H("name")]).Value, s.Properties.GetValueOrDefault(H("on")) switch
                {
                    BinTreeBool b => b.Value,
                    BinTreeBitBool bb => bb.Value,
                    _ => true,   // an absent 'on' is enabled
                }))
                .ToList();
        }
        return map;
    }

    /// <summary>The M789 contract on one real bin: it parses, every repeated switch reads as its first
    /// entry with every entry still listed, the setup capture works, and a save changes nothing.
    /// Returns the document and how many repeated switch names it saw.</summary>
    private static (MaterialDocument Doc, int Repeats) AssertToleratedAndLossless(byte[] bin, Func<string, byte[]?>? readBin)
    {
        var doc = MaterialDocument.Parse(bin, Names, null, readBin);   // threw before M789

        int repeats = 0;
        var raw = RawSwitches(bin);
        foreach (var m in doc.Materials.Where(x => x.IsStaticMaterialDef && !x.IsLinked))
        {
            _ = ShaderMaterialSetups.Capture(m);   // threw on a repeated parameter, switch or macro
            if (!raw.TryGetValue(m.ObjectPathHash, out var list)) continue;
            Assert.Equal(list.Select(s => s.Name), m.AllSwitches.Select(s => s.Name));
            foreach (var g in list.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                repeats++;
                Assert.Equal(g.First().On, m.Switches[g.Key]);
            }
        }

        var before = new BinTree(new MemoryStream(bin, false));
        var after = new BinTree(new MemoryStream(doc.Serialize(), false));
        Assert.Equal(before.Objects.Count, after.Objects.Count);
        foreach (var (hash, o) in before.Objects)
        {
            Assert.True(after.Objects.TryGetValue(hash, out var saved), $"object 0x{hash:x8} was lost on save");
            Assert.True(BinPropEquality.ObjectsEqual(o, saved!), $"object 0x{hash:x8} changed on save");
        }
        return (doc, repeats);
    }

    [Fact]
    public void TheLastDropMaterialsBinParsesAndSavesWithoutLoss()
    {
        string wadPath = Path.Combine(Final, "Maps", "Shipping", "Map22.wad.client");
        if (!File.Exists(wadPath)) return;   // no game install on this machine - nothing to assert against
        using var wad = WadArchive.Open(wadPath);
        byte[]? bin = Read(wad, "data/maps/mapgeometry/map22/thelastdrop.materials.bin");
        if (bin is null) return;             // the asset moved - nothing to assert against

        var (doc, repeats) = AssertToleratedAndLossless(bin, null);
        if (repeats == 0) return;            // a later patch deduplicated it - the synthetic tests still hold

        // the conflicting pair as shipped today: UNLIT_MODE off-then-on, USE_CUSTOM_OBJECT_NORMAL on-then-off
        var flag = doc.Materials.SingleOrDefault(m => m.Name.Equals(FlagMaterial, StringComparison.OrdinalIgnoreCase));
        if (flag is null) return;
        var raw = RawSwitches(bin)[flag.ObjectPathHash];
        foreach (var name in new[] { "UNLIT_MODE", "USE_CUSTOM_OBJECT_NORMAL" })
            if (raw.Count(s => s.Name == name) == 2)
                Assert.Equal(raw.First(s => s.Name == name).On, flag.Switches[name]);
    }

    public static IEnumerable<object[]> SorakaSkins() =>
        Enumerable.Range(53, 9).Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(SorakaSkins))]
    public void TheSorakaSkinBinsParseWithTheirLinkedBinsAndSaveWithoutLoss(int skin)
    {
        string wadPath = Path.Combine(Final, "Champions", "Soraka.wad.client");
        if (!File.Exists(wadPath)) return;
        using var wad = WadArchive.Open(wadPath);
        byte[]? bin = Read(wad, $"data/characters/soraka/skins/skin{skin}.bin");
        if (bin is null) return;

        var (doc, repeats) = AssertToleratedAndLossless(bin, p => Read(wad, p));
        if (repeats == 0) return;

        // Body_Flowmap_inst: SPECULAR off twice, and its MaxSpec / Specular_Mask repeated with different values
        var body = doc.Materials.SingleOrDefault(m => m.Name.Equals($"Characters/Soraka/Skins/Skin{skin}/Materials/Body_Flowmap_inst",
            StringComparison.OrdinalIgnoreCase));
        if (body is null) return;
        Assert.False(body.Switches["SPECULAR"]);
        var maxSpec = body.Parameters.Where(p => p.Name == "MaxSpec").ToList();
        if (maxSpec.Count == 2 && maxSpec[0].TryGetVector4(out var first))
            Assert.Equal(first.X, ShaderMaterialSetups.Capture(body).Parameters["MaxSpec"].X);
    }

    [Fact]
    public void QuinnsWingsCaptureTheirFirstIdleColor()
    {
        // skin14-23 repeat Idle_Color on Wings_Mat and Wings_Mat_Mini_Valor. No switch repeats, so this is
        // the case that isolates ShaderMaterialSetups.Capture.
        string wadPath = Path.Combine(Final, "Champions", "Quinn.wad.client");
        if (!File.Exists(wadPath)) return;
        using var wad = WadArchive.Open(wadPath);
        byte[]? bin = Read(wad, "data/characters/quinn/skins/skin14.bin");
        if (bin is null) return;

        var (doc, _) = AssertToleratedAndLossless(bin, p => Read(wad, p));
        var wings = doc.Materials.Where(m => !m.IsLinked
            && m.Parameters.Count(p => p.Name.Equals("Idle_Color", StringComparison.OrdinalIgnoreCase)) > 1).ToList();
        if (wings.Count == 0) return;        // a later patch deduplicated it
        foreach (var m in wings)
        {
            var first = m.Parameters.First(p => p.Name.Equals("Idle_Color", StringComparison.OrdinalIgnoreCase));
            Assert.True(first.TryGetVector4(out var v));
            Assert.Equal(v, ShaderMaterialSetups.Capture(m).Parameters["Idle_Color"]);
        }
    }

    [Fact]
    public void TheTftSet18SorakaBinParses()
    {
        // the same Body_Flowmap_inst, carried into TFTSet18 - outside the M788 census, so found by the M789 one
        string wadPath = Path.Combine(Final, "TFTSet18.wad.client");
        if (!File.Exists(wadPath)) return;
        using var wad = WadArchive.Open(wadPath);
        byte[]? bin = Read(wad, "data/characters/da_18_soraka/skins/skin0.bin");
        if (bin is null) return;
        AssertToleratedAndLossless(bin, p => Read(wad, p));
    }
}
