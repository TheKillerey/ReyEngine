using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Build;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Meta;
using LogEntry = ReyEngine.Core.Diagnostics.LogEntry;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M815: a forced map skin ships BY REFERENCE. <see cref="MapSkinSwitcher.Switch"/> and <see cref="MapSkinSwitcher.BuildCompatibleContainer"/>
/// write whole bins; M814's declarations turned them into the values of the day, frozen at the project's patch. The reference sample
/// (Crauzer's winter-rift-2025_0.3.0.fantome) states the same switch as references to the source slot, which LTK resolves against the
/// INSTALLED game on every install. These tests pin what the plan emits for a synthetic Map11 - a base slot, a seasonal source with a
/// container of its own, a slot with no particles field, an unregistered alias, an audio profile each, shop placeables under other keys -
/// and that the emitted document gives the project's bin back. The strong check ran outside the suite on the real Map11 and Map12 bins:
/// league-mod's <c>ltk_game_data::apply</c> over the INSTALLED game's bins equals what the switcher writes, a patched source slot flows
/// through the references and not through the values, and <c>ltk_fantome</c> reads the export (see the M815 commit).
///
/// <para>The oracle here (<see cref="Oracle"/>) is the test's own small reading of the emitted JSON - references read from the untouched game,
/// <c>-key</c> before <c>+key</c> - written independently of the production code that built the document, so a document that does not
/// say what the plan thinks it says fails.</para>
/// </summary>
public sealed class MapSkinReferenceDeclarationsTests : IDisposable
{
    private const string MapPath = "data/maps/shipping/map11/map11.bin";
    private const string BasePath = "data/maps/mapgeometry/map11/base_srx.materials.bin";
    private const string SourcePath = "data/maps/mapgeometry/map11/milkshake_srs.materials.bin";
    private const uint ServerPlaceable = 0x25e3f5d0;
    private const uint CharacterList = 0x2d3285eb;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rey-skinref-" + Guid.NewGuid().ToString("N"));
    private int _n;

    public MapSkinReferenceDeclarationsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static uint H(string s) => HashAlgorithms.Fnv1a(s);
    private static uint Slot(string name) => H("Maps/Shipping/Map11/MapSkins/" + name);
    private static string SlotName(string name) => "Maps/Shipping/Map11/MapSkins/" + name;

    // ===================================================== names

    private sealed class KnownNames : IDeclarationNames
    {
        private readonly Dictionary<uint, string> _names = new[]
        {
            "name", "mapStringId", "mapSkins", "mMapContainerLink", "mMapObjectsCFG", "mWorldParticlesINI", "mGrassTintTexture",
            "mNavigationMesh", "mObjectSkinFallbacks", "mResourceResolvers", "bankUnits", "music", "feature", "items", "transform",
            "Character", "CharacterRecord", "Skin", "Maps/Shipping/Map11", "Audio/Default", "Audio/Milkshake_SRS",
            "Maps/Chunks/BaseShops", "Maps/Chunks/MilkShops", "Maps/Chunks/Decor", "Map", "MapSkin", "FeatureAudioDataProperties",
            "MapPlaceableContainer", "MapCharacter", "MapLocator", "Maps/Shipping/Map11/Extra", "Mods/New/Thing",
            "MusicAudioDataProperties", "LegacyMusicProperties",
            SlotName("Default"), SlotName("Odyssey"), SlotName("Milkshake_SRS"), SlotName("Arcade"), SlotName("SR_Seasonal_Map"),
        }.ToDictionary(H);
        private readonly Dictionary<ulong, string> _files = new[]
        {
            "assets/maps/info/map11/grasstint_srx.tex", "assets/maps/info/map11/grasstint.tex",
            "assets/maps/info/map11/grasstint_srx_milkshake_env.tex", "assets/mine.tex",
        }.ToDictionary(HashAlgorithms.WadPath);
        public string? Field(uint hash) => _names.GetValueOrDefault(hash);
        public string? Class(uint hash) => Field(hash);
        public string? Entry(uint hash) => Field(hash);
        public string? File(ulong hash) => _files.GetValueOrDefault(hash);
    }

    private static readonly KnownNames Names = new();
    private static string? Resolve(uint hash) => Names.Field(hash);

    // ===================================================== the game: a Map11 in miniature

    private sealed record SlotSpec(string Name, string? Container, string Cfg, string? Particles, string Grass,
        int Chars = 0, bool Fallbacks = false, bool Registered = true, bool Audio = false, int Resolvers = 1,
        string MusicClass = "MusicAudioDataProperties");

    private static readonly SlotSpec DefaultSlot = new("Default", "Maps/MapGeometry/Map11/Base_SRX", "ASSETS/Maps/CFG/objectcfg_SRX.cfg",
        "ASSETS/Maps/Particles_SRX.ini", "assets/maps/info/map11/grasstint_srx.tex", Audio: true);
    private static readonly SlotSpec OdysseySlot = new("Odyssey", "Maps/MapGeometry/Map11/Base_SRX", "ASSETS/Maps/CFG/ObjectCFG.cfg",
        "ASSETS/Maps/Particles.ini", "assets/maps/info/map11/grasstint.tex", Fallbacks: true);
    private static readonly SlotSpec MilkshakeSlot = new("Milkshake_SRS", "Maps/MapGeometry/Map11/Milkshake_SRS", "ASSETS/Maps/CFG/objectcfg_SRX.cfg",
        "ASSETS/Maps/Particles_SRX.ini", "assets/maps/info/map11/grasstint_srx_milkshake_env.tex", Chars: 3, Audio: true, Resolvers: 2);
    // no particles field: neither the switcher nor a reference adds a route field a slot shipped without
    private static readonly SlotSpec ArcadeSlot = new("Arcade", "Maps/MapGeometry/Map11/Arcade", "ASSETS/Maps/CFG/ObjectCFG_Arcade.cfg",
        null, "assets/maps/info/map11/grasstint.tex");
    // an alias: a MapSkin object the Map object does not register, which the client still resolves during StartSpawn
    private static readonly SlotSpec AliasSlot = new("SR_Seasonal_Map", "Maps/MapGeometry/Map11/Base_SRX", "ASSETS/Maps/CFG/objectcfg_SRX.cfg",
        "ASSETS/Maps/Particles_SRX.ini", "assets/maps/info/map11/grasstint_srx.tex", Registered: false);

    private static byte[] RiotMap(params SlotSpec[] slots)
    {
        if (slots.Length == 0) slots = new[] { DefaultSlot, OdysseySlot, MilkshakeSlot, ArcadeSlot, AliasSlot };
        var objects = new List<BinTreeObject>
        {
            new(H("Maps/Shipping/Map11"), H("Map"), new BinTreeProperty[]
            {
                new BinTreeString(H("mapStringId"), "SR"),
                new BinTreeUnorderedContainer(H("mapSkins"), BinPropertyType.ObjectLink,
                    slots.Where(s => s.Registered).Select(s => new BinTreeObjectLink(0, Slot(s.Name)))),
            }),
        };
        foreach (var s in slots)
        {
            var props = new List<BinTreeProperty> { new BinTreeString(H("name"), s.Name) };
            if (s.Container is not null) props.Add(new BinTreeString(H("mMapContainerLink"), s.Container));
            props.Add(new BinTreeString(H("mMapObjectsCFG"), s.Cfg));
            if (s.Particles is not null) props.Add(new BinTreeString(H("mWorldParticlesINI"), s.Particles));
            props.Add(new BinTreeWadChunkLink(H("mGrassTintTexture"), HashAlgorithms.WadPath(s.Grass)));
            props.Add(new BinTreeString(H("mNavigationMesh"), $"ASSETS/Maps/NavGrid/Map11/{s.Name}.aimesh_ngrid"));
            props.Add(new BinTreeContainer(H("mResourceResolvers"), BinPropertyType.ObjectLink,
                Enumerable.Range(0, s.Resolvers).Select(i => (BinTreeProperty)new BinTreeObjectLink(0, H($"Resources/{s.Name}/{i}")))));
            if (s.Chars > 0)
                props.Add(new BinTreeUnorderedContainer(CharacterList, BinPropertyType.Embedded,
                    Enumerable.Range(0, s.Chars).Select(i => new BinTreeEmbedded(0, H("MapCharacterSkin"), new BinTreeProperty[]
                    {
                        new BinTreeHash(H("Character"), H($"Characters/Unit{i}")),
                        new BinTreeU32(H("SkinID"), (uint)(i + 1)),
                    }))));
            if (s.Fallbacks)
                props.Add(new BinTreeMap(H("mObjectSkinFallbacks"), BinPropertyType.Hash, BinPropertyType.I32, new[]
                {
                    new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, H("SRU_ChaosMinionMelee")), new BinTreeI32(0, 0)),
                }));
            objects.Add(new BinTreeObject(Slot(s.Name), H("MapSkin"), props));
            if (s.Audio)
                objects.Add(new BinTreeObject(H("Audio/" + s.Name), H("FeatureAudioDataProperties"), new BinTreeProperty[]
                {
                    new BinTreeUnorderedContainer(H("bankUnits"), BinPropertyType.String, new BinTreeProperty[]
                    {
                        new BinTreeString(0, $"ASSETS/Sounds/{s.Name}_events.bnk"),
                    }),
                    new BinTreeEmbedded(H("music"), H(s.MusicClass), new BinTreeProperty[]
                    {
                        new BinTreeString(H("themeMusicID"), $"Play_{s.Name}_select"),
                    }),
                    new BinTreeHash(H("feature"), H(s.Name)),
                }));
        }
        // an object no slot and no switch has anything to do with: a hand edit can remove it
        objects.Add(new BinTreeObject(H("Maps/Shipping/Map11/Extra"), H("MapLocator"), new BinTreeProperty[] { new BinTreeString(H("name"), "extra") }));
        return Write(new BinTree(objects, Array.Empty<string>()));
    }

    private static (uint Key, BinTreeProperty Value) Shop(uint key, string record, float x, string skin) =>
        (key, new BinTreeStruct(0, ServerPlaceable, new BinTreeProperty[]
        {
            new BinTreeMatrix44(H("transform"), Matrix4x4.CreateTranslation(x, 0, 0)),
            new BinTreeEmbedded(H("Character"), H("MapCharacter"), new BinTreeProperty[] { new BinTreeString(H("CharacterRecord"), record) }),
            new BinTreeString(H("Skin"), skin),
        }));

    private static (uint Key, BinTreeProperty Value) Locator(uint key, string name) =>
        (key, new BinTreeStruct(0, H("MapLocator"), new BinTreeProperty[] { new BinTreeString(H("name"), name) }));

    private static byte[] Container(string entry, params (uint Key, BinTreeProperty Value)[] items) => Write(new BinTree(new[]
    {
        new BinTreeObject(H(entry), H("MapPlaceableContainer"), new BinTreeProperty[]
        {
            new BinTreeMap(H("items"), BinPropertyType.Hash, BinPropertyType.Struct,
                items.Select(i => new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, i.Key), i.Value))),
        }),
        new BinTreeObject(H("Maps/Chunks/Decor"), H("MapPlaceableContainer"), new BinTreeProperty[]
        {
            new BinTreeMap(H("items"), BinPropertyType.Hash, BinPropertyType.Struct, new[] { Locator(1, "tree") }
                .Select(i => new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, i.Key), i.Value))),
        }),
    }, Array.Empty<string>()));

    private const string North = "Characters/sru_storekeepernorth/CharacterRecords/Root";
    private const string South = "Characters/sru_storekeepersouth/CharacterRecords/Root";

    /// <summary>The base slot's container: the shopkeepers the server asks for, under the keys it knows.</summary>
    private static byte[] BaseContainer() => Container("Maps/Chunks/BaseShops",
        Shop(0x1e1e8b6b, North, 100, "Skin0"), Shop(0x9f77d47f, South, 200, "Skin0"), Locator(0x11111111, "ShopArea"));

    /// <summary>The source slot's container: the same shopkeepers under keys of its own.</summary>
    private static byte[] SourceContainer() => Container("Maps/Chunks/MilkShops",
        Shop(0x4241132a, North, 100, "Skin4"), Shop(0xd0c80c35, South, 200, "Skin5"), Locator(0x22222222, "ShopArea1"));

    private static byte[] Thing(float speed) => Write(new BinTree(new[]
    {
        new BinTreeObject(H("Maps/Test/Thing"), H("Thing"), new BinTreeProperty[] { new BinTreeF32(H("speed"), speed) }),
    }, Array.Empty<string>()));

    private static byte[] Write(BinTree tree)
    {
        using var ms = new MemoryStream();
        tree.Write(ms);
        return ms.ToArray();
    }

    private static Func<ulong, string, byte[]?> Game(byte[] map, byte[]? baseContainer = null, byte[]? sourceContainer = null, bool noBase = false)
    {
        var bins = new Dictionary<ulong, byte[]>
        {
            [HashAlgorithms.WadPath(MapPath)] = map,
            [HashAlgorithms.WadPath(SourcePath)] = sourceContainer ?? SourceContainer(),
        };
        if (!noBase) bins[HashAlgorithms.WadPath(BasePath)] = baseContainer ?? BaseContainer();
        return (hash, _) => bins.GetValueOrDefault(hash);
    }

    // ===================================================== the project: what the switcher wrote

    private sealed record Switched(byte[] Riot, byte[] Map, byte[] Container, BinRecipeRecord Record);

    private static Switched Switch(bool carry, bool routeAudio = true, byte[]? riotMap = null)
    {
        byte[] riot = riotMap ?? RiotMap();
        var swap = MapSkinSwitcher.Switch(riot, 11, Slot("Default"), Slot("Milkshake_SRS"), Resolve, carry, routeAudio);
        var compat = MapSkinSwitcher.BuildCompatibleContainer(BaseContainer(), SourceContainer());
        var recipe = MapSkinForceRecipe.Record(riot, 11, Slot("Default"), Slot("Milkshake_SRS"), carry, Resolve);
        if (!routeAudio) recipe = recipe with { TargetName = null };
        return new Switched(riot, swap.Bytes, compat.Bytes, recipe.ToRecord(MapPath, "16.19", "switcher"));
    }

    private DeclarationFile Put(string rel, byte[] bytes, string layer = "base", string folder = "Map11")
    {
        string path = Path.Combine(_dir, (++_n).ToString(CultureInfo.InvariantCulture), rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return new DeclarationFile(layer, folder, rel, path);
    }

    private static DeclarationPlan Plan(IEnumerable<DeclarationFile> files, Func<ulong, string, byte[]?> game, params BinRecipeRecord[] recipes) =>
        BinDeclarationPlanner.Plan(files, game, Names, new MapSkinDeclarationOptions(recipes, Resolve));

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static JsonObject Bodies(DeclaredChunk module) =>
        BinDeclarations.GameDataDocument(new[] { module })["modules"]![0]!["edits"]![0]!.AsObject();

    private static string Json(JsonNode? node) => node!.ToJsonString(Compact);

    private static DeclaredChunk ModuleOf(DeclarationPlan plan, string target, int index = 0) =>
        plan.Modules["base"].Where(m => m.Target == target).ElementAt(index);

    // ===================================================== the oracle: the emitted document, read independently

    /// <summary>
    /// Applies the modules of one target, in order, over the game's bin the way <c>ltk_game_data</c> does for the shapes this plan
    /// emits: a reference is read from the UNTOUCHED game; a plain key sets the property (a literal takes the type the property
    /// already has); <c>-key</c> removes the listed map keys and <c>+key</c> adds the listed entries, removals first.
    /// </summary>
    private static class Oracle
    {
        public static BinTree Apply(byte[] game, IEnumerable<DeclaredChunk> modules)
        {
            var untouched = SafeBinTree.Parse(game);
            var tree = SafeBinTree.Parse(game);
            foreach (var module in modules)
            {
                foreach (var (entry, bodyNode) in Bodies(module))
                {
                    Assert.NotEqual("objects", entry);   // the plan under test creates no object here
                    Assert.NotEqual("links", entry);
                    var obj = tree.Objects[Hash32(entry)];
                    var body = bodyNode!.AsObject();
                    foreach (var (key, value) in body.Where(p => p.Key[0] is not ('+' or '-')))
                    {
                        uint field = Hash32(key);
                        obj.Properties[field] = Value(value!, obj.Properties.GetValueOrDefault(field), field, untouched);
                    }
                    foreach (var (key, value) in body.Where(p => p.Key[0] == '-'))
                    {
                        uint field = Hash32(key[1..]);
                        var map = (BinTreeMap)obj.Properties[field];
                        var entries = map.ToList();
                        foreach (var k in value!.AsArray())
                        {
                            uint wanted = Hash32(k!.GetValue<string>());
                            Assert.True(entries.RemoveAll(e => e.Key is BinTreeHash h && h.Value == wanted) > 0, $"-{key[1..]}: no entry 0x{wanted:x8}");
                        }
                        obj.Properties[field] = new BinTreeMap(field, map.KeyType, map.ValueType, entries);
                    }
                    foreach (var (key, value) in body.Where(p => p.Key[0] == '+'))
                    {
                        uint field = Hash32(key[1..]);
                        var map = (BinTreeMap)obj.Properties[field];
                        var entries = map.ToList();
                        foreach (var (k, v) in value!.AsObject())
                            entries.Add(new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, Hash32(k)), Value(v!, null, 0, untouched)));
                        obj.Properties[field] = new BinTreeMap(field, map.KeyType, map.ValueType, entries);
                    }
                }
            }
            return tree;
        }

        private static uint Hash32(string text) =>
            Regex.IsMatch(text, "^0x[0-9a-f]{8}$") ? uint.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : HashAlgorithms.Fnv1a(text);

        private static BinTreeProperty Value(JsonNode value, BinTreeProperty? current, uint field, BinTree untouched)
        {
            if (value is JsonObject o && o.Count == 1 && o.ContainsKey("ref")) return Reference(o["ref"]!.GetValue<string>(), field, untouched);
            return current switch
            {
                BinTreeString => new BinTreeString(field, value.GetValue<string>()),
                BinTreeWadChunkLink => new BinTreeWadChunkLink(field, HashAlgorithms.WadPath(value.GetValue<string>())),
                BinTreeHash => new BinTreeHash(field, Hash32(value.GetValue<string>())),
                null => throw new InvalidOperationException($"No property to give a literal its type: {value.ToJsonString()}"),
                _ => throw new NotSupportedException($"The oracle does not read a literal for a {current.GetType().Name}."),
            };
        }

        /// <summary><c>entry:path</c>, split at the first ':'; the path is <c>field</c> or <c>field{decimal}</c>.</summary>
        private static BinTreeProperty Reference(string text, uint field, BinTree untouched)
        {
            int colon = text.IndexOf(':');
            var obj = untouched.Objects[Hash32(text[..colon])];
            string path = text[(colon + 1)..];
            int brace = path.IndexOf('{');
            if (brace < 0) return BinTreeCloner.Clone(obj.Properties[Hash32(path)], field);
            var map = (BinTreeMap)obj.Properties[Hash32(path[..brace])];
            uint key = uint.Parse(path[(brace + 1)..path.IndexOf('}')], CultureInfo.InvariantCulture);
            var hit = map.First(e => e.Key is BinTreeHash h && h.Value == key);
            return BinTreeCloner.Clone(hit.Value, field);
        }
    }

    private static void AssertSameBin(byte[] expected, BinTree actual, string because)
    {
        var want = SafeBinTree.Parse(expected);
        Assert.True(want.Objects.Count == actual.Objects.Count, $"{because}: {want.Objects.Count} vs {actual.Objects.Count} objects");
        foreach (var (key, obj) in want.Objects)
            Assert.True(actual.Objects.TryGetValue(key, out var other) && BinPropEquality.ObjectsEqual(obj, other), $"{because}: object 0x{key:x8} differs");
        Assert.Equal(want.Dependencies, actual.Dependencies);
    }

    // ===================================================== the switch, by reference

    [Fact]
    public void ASwitchIsDeclaredAsReferencesToTheSourceSlotForEverySlotAndAlias()
    {
        var s = Switch(carry: true);
        var plan = Plan(new[] { Put(MapPath, s.Map), Put(SourcePath, s.Container) }, Game(s.Riot), s.Record);

        Assert.Equal(2, plan.Declared);
        Assert.Empty(plan.Whole);
        // modules are ordered by target: the container's before the map's
        Assert.Equal(new[] { SourcePath, MapPath }, plan.Modules["base"].Select(m => m.Target).ToArray());

        var map = ModuleOf(plan, MapPath);
        var bodies = Bodies(map);
        // the source slot itself is never written; the base slot, the others and the unregistered alias all are; so is the audio profile
        Assert.Equal(new[] { SlotName("Default"), SlotName("Odyssey"), SlotName("Arcade"), SlotName("SR_Seasonal_Map"), "Audio/Default" }.Order(),
            bodies.Select(b => b.Key).Order().ToArray());

        string source = SlotName("Milkshake_SRS");
        string Ref(string field) => $$"""{"ref":"{{source}}:{{field}}"}""";
        Assert.Equal(
            "{" + string.Join(",", new[] { "mMapContainerLink", "mMapObjectsCFG", "mWorldParticlesINI", "mGrassTintTexture", "0x2d3285eb" }
                .Select(f => $"\"{f}\":{Ref(f)}")) + "}",
            Json(bodies[SlotName("Default")]));
        // the route fields only where the slot has them: Arcade shipped without particles and is not given any
        Assert.Equal(new[] { "mMapContainerLink", "mMapObjectsCFG", "mGrassTintTexture", "0x2d3285eb" },
            bodies[SlotName("Arcade")]!.AsObject().Select(p => p.Key).ToArray());
        // the unit-skin list, which Odyssey lacked, is given to it: the one write Riot's data has no example of
        Assert.Contains("0x2d3285eb", bodies[SlotName("Odyssey")]!.AsObject().Select(p => p.Key));
        // mResourceResolvers and the fallbacks map are not route fields, and the source has no fallbacks to carry
        Assert.DoesNotContain(bodies.SelectMany(b => b.Value!.AsObject().Select(p => p.Key)), k => k is "mResourceResolvers" or "mObjectSkinFallbacks" or "mNavigationMesh");
        // the audio profile takes the source profile's properties, but keeps its own feature
        Assert.Equal("""{"bankUnits":{"ref":"Audio/Milkshake_SRS:bankUnits"},"music":{"ref":"Audio/Milkshake_SRS:music"}}""", Json(bodies["Audio/Default"]));

        // every value of the module is a reference: nothing of Riot's data is copied
        foreach (var (_, body) in bodies)
            foreach (var (field, value) in body!.AsObject())
                Assert.True(value is JsonObject { Count: 1 } v && v.ContainsKey("ref"), $"{field} is a value, not a reference");
        Assert.Equal(bodies.Sum(b => b.Value!.AsObject().Count), map.References);
        Assert.Equal(map.References + ModuleOf(plan, SourcePath).References, plan.References);

        // and the document gives the project's bin back
        AssertSameBin(s.Map, Oracle.Apply(s.Riot, new[] { map }), "the map bin");
    }

    [Fact]
    public void WithoutTheCharacterSkinOptInNoUnitSkinFieldIsReferenced()
    {
        var s = Switch(carry: false);
        var plan = Plan(new[] { Put(MapPath, s.Map), Put(SourcePath, s.Container) }, Game(s.Riot), s.Record);

        var map = ModuleOf(plan, MapPath);
        Assert.DoesNotContain("0x2d3285eb", Json(Bodies(map)));
        Assert.Equal(4, Bodies(map)[SlotName("Default")]!.AsObject().Count);
        AssertSameBin(s.Map, Oracle.Apply(s.Riot, new[] { map }), "the map bin");
    }

    [Fact]
    public void ASwitchThatRoutedNoAudioDeclaresNoAudioEdit()
    {
        var s = Switch(carry: true, routeAudio: false);
        var plan = Plan(new[] { Put(MapPath, s.Map), Put(SourcePath, s.Container) }, Game(s.Riot), s.Record);

        var map = ModuleOf(plan, MapPath);
        Assert.False(Bodies(map).ContainsKey("Audio/Default"));
        AssertSameBin(s.Map, Oracle.Apply(s.Riot, new[] { map }), "the map bin");
    }

    [Fact]
    public void TheServerShopKeysMoveByRemovalAndAdditionWithReferencesToTheOldEntries()
    {
        var s = Switch(carry: true);
        var plan = Plan(new[] { Put(MapPath, s.Map), Put(SourcePath, s.Container) }, Game(s.Riot), s.Record);

        var container = ModuleOf(plan, SourcePath);
        // the sample's second module: -items the source's keys, +items the server's keys with a reference to the old entry, keyed in decimal
        Assert.Equal(
            """{"Maps/Chunks/MilkShops":{"-items":["0x4241132a","0xd0c80c35"],"+items":{"0x1e1e8b6b":{"ref":"Maps/Chunks/MilkShops:items{1111561002}"},"0x9f77d47f":{"ref":"Maps/Chunks/MilkShops:items{3502771253}"}}}}""",
            Json(BinDeclarations.GameDataDocument(new[] { container })["modules"]![0]!["edits"]![0]));
        Assert.Equal(2, container.References);
        AssertSameBin(s.Container, Oracle.Apply(SourceContainer(), new[] { container }), "the source container");
        // 1111561002 is 0x4241132a and 3502771253 is 0xd0c80c35: the decimal of a path key, the hex of a map key
        Assert.Equal(0x4241132au, 1111561002u);
        Assert.Equal(0xd0c80c35u, 3502771253u);
    }

    [Fact]
    public void TheManifestSendToLtkManagerWritesCarriesTheSameReferences()
    {
        var s = Switch(carry: true);
        var plan = Plan(new[] { Put(MapPath, s.Map), Put(SourcePath, s.Container) }, Game(s.Riot), s.Record);

        string yaml = BinDeclarations.Manifest(plan.Modules["base"]);
        Assert.Contains($"      mGrassTintTexture: {{\"ref\": \"{SlotName("Milkshake_SRS")}:mGrassTintTexture\"}}\n", yaml);
        Assert.Contains("      \"0x2d3285eb\": {\"ref\": \"" + SlotName("Milkshake_SRS") + ":0x2d3285eb\"}\n", yaml);
        Assert.Contains("      \"-items\": [\"0x4241132a\", \"0xd0c80c35\"]\n", yaml);
        Assert.Contains("\"+items\": {\"0x1e1e8b6b\": {\"ref\": \"Maps/Chunks/MilkShops:items{1111561002}\"}", yaml);
        Assert.Equal(plan.References, Regex.Matches(yaml, "\\{\"ref\": ").Count);
        Assert.Equal(plan.References, Regex.Matches(Json(BinDeclarations.GameDataDocument(plan.Modules["base"])), "\"ref\":").Count);
    }

    // ===================================================== what the project holds beyond the switch

    [Fact]
    public void AHandEditBeyondTheSwitchIsAModuleAfterTheReferencesAndNotDuplicatedAsValues()
    {
        var s = Switch(carry: true);
        var edited = SafeBinTree.Parse(s.Map);
        // a runtime field the switch never touches, and a route field the user set to their own value after switching
        edited.Objects[Slot("Odyssey")].Properties[H("mNavigationMesh")] = new BinTreeString(H("mNavigationMesh"), "ASSETS/Maps/NavGrid/Custom.aimesh_ngrid");
        edited.Objects[Slot("Arcade")].Properties[H("mGrassTintTexture")] = new BinTreeWadChunkLink(H("mGrassTintTexture"), HashAlgorithms.WadPath("assets/mine.tex"));
        byte[] mine = Write(edited);

        var other = Put("data/aaa/other.bin", Thing(2f));      // an unrelated changed bin sorts before the map's modules
        var files = new[] { Put(MapPath, mine), Put(SourcePath, s.Container), other };
        var riot = Game(s.Riot);
        var plan = Plan(files, (hash, rel) => hash == HashAlgorithms.WadPath("data/aaa/other.bin") ? Thing(1f) : riot(hash, rel), s.Record);

        // modules by target, the two of the map bin in apply order: references, then values
        Assert.Equal(new[] { "data/aaa/other.bin", SourcePath, MapPath, MapPath }, plan.Modules["base"].Select(m => m.Target).ToArray());
        var maps = plan.Modules["base"].Where(m => m.Target == MapPath).ToList();
        var references = maps[0];
        var values = maps[1];
        Assert.True(references.References > 0);
        Assert.Equal(0, values.References);

        // The slot whose route field the user changed is decided as a whole: none of its route fields is a reference, because a slot with
        // the container and CFG of today's source but a tint of its own is a mixture the switcher never writes, and after the next patch
        // the references would pull the matching fields along and leave the edited one behind. Its character skins are another group.
        Assert.Equal(new[] { "0x2d3285eb" }, Bodies(references)[SlotName("Arcade")]!.AsObject().Select(p => p.Key).ToArray());
        // ... so the values are the Odyssey edit and ALL of Arcade's route fields (it ships without particles, so three)
        Assert.Equal(new[] { SlotName("Odyssey"), SlotName("Arcade") }.Order(), Bodies(values).Select(b => b.Key).Order().ToArray());
        Assert.Equal("""{"mNavigationMesh":"ASSETS/Maps/NavGrid/Custom.aimesh_ngrid"}""", Json(Bodies(values)[SlotName("Odyssey")]));
        Assert.Equal(new[] { "mGrassTintTexture", "mMapContainerLink", "mMapObjectsCFG" },
            Bodies(values)[SlotName("Arcade")]!.AsObject().Select(p => p.Key).Order().ToArray());
        Assert.Contains("assets/mine.tex", Json(Bodies(values)[SlotName("Arcade")]));

        // both modules, in order, give the project's bin back
        AssertSameBin(mine, Oracle.Apply(s.Riot, maps), "references then values");
        Assert.Equal(2, plan.DeclaredBins.Single(b => b.RelPath == MapPath).Chunks.Count);
        Assert.Equal(3, plan.Declared);
    }

    [Fact]
    public void ASwitchTheUserUndidIsNotClaimedByReference()
    {
        var s = Switch(carry: true);
        // the project holds Riot's own bins again: nothing to ship, and no reference to a switch that is not there
        var plan = Plan(new[] { Put(MapPath, s.Riot), Put(SourcePath, SourceContainer()) }, Game(s.Riot), s.Record);

        Assert.Equal(0, plan.Declared);
        Assert.Equal(2, plan.Unchanged);
        Assert.Empty(plan.Modules);
        Assert.Equal(0, plan.References);
        Assert.Empty(plan.SwitchNotes);   // a bin that is Riot's own needs no explanation
    }

    [Fact]
    public void ASlotTheProjectHoldsOnlyInPartIsDecidedWholeAndTheOthersStayByReference()
    {
        var s = Switch(carry: false);
        // the user undid the switch on the base slot alone: Default is Riot's own again
        var tree = SafeBinTree.Parse(s.Map);
        var original = SafeBinTree.Parse(s.Riot).Objects[Slot("Default")];
        tree.Objects[Slot("Default")] = new BinTreeObject(Slot("Default"), H("MapSkin"), original.Properties.Values.Select(p => BinTreeCloner.Clone(p, p.NameHash)));
        byte[] partial = Write(tree);

        var plan = Plan(new[] { Put(MapPath, partial), Put(SourcePath, s.Container) }, Game(s.Riot), s.Record);

        var map = ModuleOf(plan, MapPath);
        // Riot's Default shares the CFG and the particles with the source today, so a per-field rule would reference those two; they would
        // follow the source after the next patch while the container and tint stay Riot's. The slot is all or nothing: no references at all.
        Assert.False(Bodies(map).ContainsKey(SlotName("Default")));
        Assert.True(Bodies(map).ContainsKey(SlotName("Odyssey")));
        // and it is Riot's own, so there are no values for it either
        Assert.Single(plan.Modules["base"], m => m.Target == MapPath);
        AssertSameBin(partial, Oracle.Apply(s.Riot, new[] { map }), "the partly undone switch");
        Assert.Contains(plan.SwitchNotes, n => n.Contains("map11.bin") && n.Contains("by reference") && n.Contains("4 value(s) in 1 group(s)"));
    }

    [Fact]
    public void ASlotsCharacterSkinsAreDecidedApartFromItsRouteFields()
    {
        var s = Switch(carry: true);
        var edited = SafeBinTree.Parse(s.Map);
        // the user trimmed Odyssey's unit-skin list to one entry: not what the switch carried
        var list = (BinTreeUnorderedContainer)edited.Objects[Slot("Odyssey")].Properties[CharacterList];
        edited.Objects[Slot("Odyssey")].Properties[CharacterList] = new BinTreeUnorderedContainer(CharacterList, BinPropertyType.Embedded,
            list.Elements.Take(1).Select(e => BinTreeCloner.Clone(e, 0)));
        byte[] mine = Write(edited);

        var plan = Plan(new[] { Put(MapPath, mine), Put(SourcePath, s.Container) }, Game(s.Riot), s.Record);

        var maps = plan.Modules["base"].Where(m => m.Target == MapPath).ToList();
        Assert.Equal(2, maps.Count);
        // its route fields are still references; its unit-skin list is a value
        Assert.Equal(new[] { "mMapContainerLink", "mMapObjectsCFG", "mWorldParticlesINI", "mGrassTintTexture" },
            Bodies(maps[0])[SlotName("Odyssey")]!.AsObject().Select(p => p.Key).ToArray());
        Assert.Equal(new[] { "0x2d3285eb" }, Bodies(maps[1])[SlotName("Odyssey")]!.AsObject().Select(p => p.Key).ToArray());
        // the other slots keep theirs
        Assert.Contains("0x2d3285eb", Json(Bodies(maps[0])[SlotName("Arcade")]));
    }

    [Fact]
    public void AnAudioProfileTheProjectEditedInAFieldTheSwitchWritesGoesToValuesWhole()
    {
        var s = Switch(carry: false);
        var edited = SafeBinTree.Parse(s.Map);
        // the user changed the music of the base slot's profile after switching
        var audio = edited.Objects[H("Audio/Default")];
        audio.Properties[H("music")] = new BinTreeEmbedded(H("music"), H("MusicAudioDataProperties"),
            new BinTreeProperty[] { new BinTreeString(H("themeMusicID"), "Play_mine") });
        byte[] mine = Write(edited);

        var plan = Plan(new[] { Put(MapPath, mine), Put(SourcePath, s.Container) }, Game(s.Riot), s.Record);

        var maps = plan.Modules["base"].Where(m => m.Target == MapPath).ToList();
        Assert.Equal(2, maps.Count);
        // the profile is one group: neither bankUnits nor music is a reference now, both are values
        Assert.False(Bodies(maps[0]).ContainsKey("Audio/Default"));
        var valueKeys = Bodies(maps[1])["Audio/Default"]!.AsObject().Select(p => p.Key).ToList();
        Assert.Contains("bankUnits", valueKeys);
        Assert.Contains(valueKeys, k => k.StartsWith("music.", StringComparison.Ordinal));   // an embed of the same class is set field by field
        Assert.Contains("Play_mine", Json(Bodies(maps[1])["Audio/Default"]));
        // the slots are untouched by it
        Assert.True(Bodies(maps[0]).ContainsKey(SlotName("Odyssey")));
    }

    [Fact]
    public void AFieldOfTheAudioProfileTheSwitchDoesNotWriteIsAHandEditBeyondTheReferences()
    {
        var s = Switch(carry: false);
        var edited = SafeBinTree.Parse(s.Map);
        // `feature` is the one property of the profile the switch keeps: whatever the user does to it is theirs
        edited.Objects[H("Audio/Default")].Properties[H("feature")] = new BinTreeHash(H("feature"), H("Mine"));
        byte[] mine = Write(edited);

        var plan = Plan(new[] { Put(MapPath, mine), Put(SourcePath, s.Container) }, Game(s.Riot), s.Record);

        var maps = plan.Modules["base"].Where(m => m.Target == MapPath).ToList();
        Assert.Equal(2, maps.Count);
        Assert.Equal(new[] { "bankUnits", "music" }, Bodies(maps[0])["Audio/Default"]!.AsObject().Select(p => p.Key).ToArray());   // still references
        Assert.Equal(new[] { "feature" }, Bodies(maps[1])["Audio/Default"]!.AsObject().Select(p => p.Key).ToArray());               // plus the edit
        AssertSameBin(mine, Oracle.Apply(s.Riot, maps), "references then the edit");
    }

    [Fact]
    public void AnObjectAddedOrRemovedBeyondTheSwitchShipsInTheModuleOfValues()
    {
        var s = Switch(carry: true);
        var edited = SafeBinTree.Parse(s.Map);
        var objects = edited.Objects.Values
            .Where(o => o.PathHash != H("Maps/Shipping/Map11/Extra"))                                 // the user deleted an object ...
            .Append(new BinTreeObject(H("Mods/New/Thing"), H("MapLocator"),                           // ... and added one
                new BinTreeProperty[] { new BinTreeString(H("name"), "mine") }))
            .ToList();
        byte[] mine = Write(new BinTree(objects, edited.Dependencies));

        var plan = Plan(new[] { Put(MapPath, mine), Put(SourcePath, s.Container) }, Game(s.Riot), s.Record);

        var maps = plan.Modules["base"].Where(m => m.Target == MapPath).ToList();
        Assert.Equal(2, maps.Count);
        Assert.True(maps[0].References > 0);
        Assert.Equal(0, maps[1].References);
        Assert.Equal(1, maps[1].ObjectsAdded);
        Assert.Equal(1, maps[1].ObjectsRemoved);
        // the references are untouched by the object edits, and those travel after them
        Assert.Equal(new[] { "objects" }, Bodies(maps[1]).Select(p => p.Key).ToArray());
        var objectEdits = Bodies(maps[1])["objects"]!.AsObject();
        Assert.Equal("""{"class":"MapLocator","set":{"name":"mine"}}""", Json(objectEdits["Mods/New/Thing"]));
        Assert.Equal("""{"remove":true}""", Json(objectEdits["Maps/Shipping/Map11/Extra"]));
        Assert.DoesNotContain("objects", Bodies(maps[0]).Select(p => p.Key));
    }

    [Fact]
    public void AContainerEditedBeyondTheKeyMovesKeepsItsEditsAsValuesAfterTheMoves()
    {
        var s = Switch(carry: true);
        var edited = SafeBinTree.Parse(s.Container);
        // a new object (a hand edit beyond the switch) in the container
        var objects = edited.Objects.Values.Append(new BinTreeObject(H("Mods/New/Thing"), H("MapLocator"),
            new BinTreeProperty[] { new BinTreeString(H("name"), "mine") })).ToList();
        byte[] mine = Write(new BinTree(objects, edited.Dependencies));

        var plan = Plan(new[] { Put(MapPath, s.Map), Put(SourcePath, mine) }, Game(s.Riot), s.Record);

        var chunks = plan.Modules["base"].Where(m => m.Target == SourcePath).ToList();
        Assert.Equal(2, chunks.Count);
        Assert.Equal(2, chunks[0].References);      // the moves first ...
        Assert.Equal(1, chunks[1].ObjectsAdded);    // ... then what the project added
        Assert.Equal(0, chunks[1].References);
    }

    [Fact]
    public void AContainerWhoseItemsTheProjectChangedBeyondTheMovesIsLeftToTheDiffAlone()
    {
        var s = Switch(carry: true);
        // another item of the SAME map property changed: the diff sets a map whole, which would overwrite the moves, so it carries the lot
        var edited = SafeBinTree.Parse(s.Container);
        var shops = edited.Objects[H("Maps/Chunks/MilkShops")];
        var map = (BinTreeMap)shops.Properties[H("items")];
        var entries = map.Select(e => e.Key is BinTreeHash { Value: 0x22222222 }
            ? new KeyValuePair<BinTreeProperty, BinTreeProperty>(e.Key, Locator(0x22222222, "ShopArea1-renamed").Value)
            : e).ToList();
        shops.Properties[H("items")] = new BinTreeMap(H("items"), map.KeyType, map.ValueType, entries);
        byte[] mine = Write(edited);

        var plan = Plan(new[] { Put(MapPath, s.Map), Put(SourcePath, mine) }, Game(s.Riot), s.Record);

        var chunks = plan.Modules["base"].Where(m => m.Target == SourcePath).ToList();
        var only = Assert.Single(chunks);
        Assert.Equal(0, only.References);            // declared as values, whole: no module claims the moves twice
        Assert.Contains(plan.SwitchNotes, n => n.Contains("milkshake_srs.materials.bin") && n.Contains("as values"));
        Assert.True(ModuleOf(plan, MapPath).References > 0);   // the map bin is unaffected
    }

    [Fact]
    public void AContainerTheProjectLeftAsRiotShippedItIsUnchangedAndNeedsNoNote()
    {
        var s = Switch(carry: true);
        // the map bin is switched; the source container is Riot's own copy, shop keys and all
        var plan = Plan(new[] { Put(MapPath, s.Map), Put(SourcePath, SourceContainer()) }, Game(s.Riot), s.Record);

        Assert.Equal(1, plan.Declared);
        Assert.Equal(1, plan.Unchanged);
        Assert.DoesNotContain(plan.SwitchNotes, n => n.Contains("milkshake_srs.materials.bin"));
        Assert.Equal(MapPath, plan.DeclaredBins.Single().RelPath);
    }

    // ===================================================== when there is nothing to refer to, or nothing asked

    [Fact]
    public void WithoutOptionsEveryBinIsDeclaredAsValuesExactlyAsM814DidIt()
    {
        var s = Switch(carry: true);
        var files = new[] { Put(MapPath, s.Map), Put(SourcePath, s.Container) };
        var game = Game(s.Riot);

        var plan = BinDeclarationPlanner.Plan(files, game, Names);    // no options: the map skin switch is not looked for

        Assert.Equal(0, plan.References);
        Assert.Empty(plan.SwitchNotes);
        foreach (var bin in plan.DeclaredBins) Assert.Single(bin.Chunks);
        var map = ModuleOf(plan, MapPath);
        Assert.Equal(map.Module, BinDeclarations.Convert(MapPath, s.Riot, s.Map, Names).Module);
        Assert.DoesNotContain("\"ref\"", Json(BinDeclarations.GameDataDocument(plan.Modules["base"])));
        // the project keeps the value of the day, as before: the source slot's grass tint, by its path
        Assert.Contains("assets/maps/info/map11/grasstint_srx_milkshake_env.tex", Json(Bodies(map)[SlotName("Odyssey")]));
    }

    [Fact]
    public void ARecipeIsReadBackOutOfAForcedBinWhenTheProjectRecordedNone()
    {
        var s = Switch(carry: true);
        var recorded = Plan(new[] { Put(MapPath, s.Map), Put(SourcePath, s.Container) }, Game(s.Riot), s.Record);
        var inferred = Plan(new[] { Put(MapPath, s.Map), Put(SourcePath, s.Container) }, Game(s.Riot));   // a project older than recipes

        Assert.Equal(recorded.Modules["base"].Select(m => m.Module).ToArray(), inferred.Modules["base"].Select(m => m.Module).ToArray());
        Assert.Contains(inferred.SwitchNotes, n => n.Contains("read out of the bin"));
        Assert.DoesNotContain(recorded.SwitchNotes, n => n.Contains("read out of the bin"));
    }

    [Fact]
    public void ASourceSlotTheGameNoLongerShipsHasNothingToReferToSoItsValuesAreDeclared()
    {
        var s = Switch(carry: true);
        // Riot vaulted the seasonal slot: the installed game has no Milkshake_SRS
        byte[] vaulted = RiotMap(DefaultSlot, OdysseySlot, ArcadeSlot, AliasSlot);
        var plan = Plan(new[] { Put(MapPath, s.Map), Put(SourcePath, s.Container) }, Game(vaulted), s.Record);

        Assert.Equal(0, plan.References);
        Assert.Contains(plan.SwitchNotes, n => n.Contains("Milkshake_SRS") && n.Contains("not in the installed game"));
        Assert.All(plan.DeclaredBins, b => Assert.Single(b.Chunks));
    }

    [Fact]
    public void ARecipeTheInstalledGameCannotReplayLeavesTheBinsToTheDiffWithTheReason()
    {
        var s = Switch(carry: true);
        // the installed game's map bin identifies itself as TFT, which the switcher refuses for good
        var tft = SafeBinTree.Parse(s.Riot);
        tft.Objects[H("Maps/Shipping/Map11")].Properties[H("mapStringId")] = new BinTreeString(H("mapStringId"), "TFT");
        var plan = Plan(new[] { Put(MapPath, s.Map), Put(SourcePath, s.Container) }, Game(Write(tft)), s.Record);

        Assert.Equal(0, plan.References);
        Assert.Contains(plan.SwitchNotes, n => n.Contains("cannot be replayed") && n.Contains("paid cosmetics"));
        Assert.All(plan.DeclaredBins, b => Assert.Single(b.Chunks));
    }

    [Fact]
    public void AnUnreadableRecordLeavesTheBinsToTheDiffWithTheReason()
    {
        var s = Switch(carry: true);
        s.Record.SourceSnapshot = "this is not base64!";
        var plan = Plan(new[] { Put(MapPath, s.Map), Put(SourcePath, s.Container) }, Game(s.Riot), s.Record);

        Assert.Equal(0, plan.References);
        Assert.Contains(plan.SwitchNotes, n => n.Contains("recorded map skin switch is unreadable"));
        Assert.True(plan.Declared > 0);
    }

    [Fact]
    public void ANonSwitchMapBinEditedByHandTakesM814sPlainPathEndToEndWithAnEqualResult()
    {
        byte[] riot = RiotMap();
        var edited = SafeBinTree.Parse(riot);
        // two ordinary hand edits: no slot is routed to a common source, so this is not a switch
        edited.Objects[Slot("Odyssey")].Properties[H("mNavigationMesh")] = new BinTreeString(H("mNavigationMesh"), "ASSETS/Maps/NavGrid/Custom.aimesh_ngrid");
        edited.Objects[Slot("Arcade")].Properties[H("mGrassTintTexture")] = new BinTreeWadChunkLink(H("mGrassTintTexture"), HashAlgorithms.WadPath("assets/mine.tex"));
        byte[] mine = Write(edited);
        var files = new[] { Put(MapPath, mine) };

        var withOptions = Plan(files, Game(riot));                                  // declarations on, no recipe: nothing can be read out of a hand edit
        var m814 = BinDeclarationPlanner.Plan(files, Game(riot), Names);            // the planner as M814 left it

        Assert.Equal(0, withOptions.References);
        Assert.Empty(withOptions.SwitchNotes);
        Assert.Equal(m814.Modules["base"].Select(m => m.Module).ToArray(), withOptions.Modules["base"].Select(m => m.Module).ToArray());
        Assert.Equal(Json(BinDeclarations.GameDataDocument(m814.Modules["base"])), Json(BinDeclarations.GameDataDocument(withOptions.Modules["base"])));
        Assert.Equal(m814.Report().Select(r => r.Line).ToArray(), withOptions.Report().Select(r => r.Line).ToArray());
        Assert.Single(withOptions.DeclaredBins.Single().Chunks);
        AssertSameBin(mine, Oracle.Apply(riot, withOptions.Modules["base"]), "the hand-edited bin");

        // a recipe the project recorded for this bin makes no difference when the bin holds none of it
        var recorded = Plan(files, Game(riot), Switch(carry: true).Record);
        Assert.Equal(0, recorded.References);
        Assert.Equal(m814.Modules["base"].Select(m => m.Module).ToArray(), recorded.Modules["base"].Select(m => m.Module).ToArray());
        Assert.Contains(recorded.SwitchNotes, n => n.Contains("holds none of the switch"));
    }

    [Fact]
    public void ABaseContainerTheGameCopyLacksLeavesTheContainerToTheDiffWithTheReason()
    {
        var s = Switch(carry: true);
        var plan = Plan(new[] { Put(MapPath, s.Map), Put(SourcePath, s.Container) }, Game(s.Riot, noBase: true), s.Record);

        // the map bin is by reference; the container cannot match its shop keys without the base container, so it is values
        Assert.True(ModuleOf(plan, MapPath).References > 0);
        var container = Assert.Single(plan.Modules["base"], m => m.Target == SourcePath);
        Assert.Equal(0, container.References);
        Assert.Contains(plan.SwitchNotes, n => n.Contains("milkshake_srs.materials.bin") && n.Contains("is not in the game copy") && n.Contains("declared as values"));
    }

    [Fact]
    public void AContainerThatDoesNotMatchTheBaseLeavesTheContainerToTheDiffWithTheReason()
    {
        var s = Switch(carry: true);
        // the game's base container now holds a shopkeeper the source container has no twin for: BuildCompatibleContainer refuses
        byte[] otherBase = Container("Maps/Chunks/BaseShops",
            Shop(0x1e1e8b6b, "Characters/a_different_shopkeeper/CharacterRecords/Root", 100, "Skin0"), Locator(0x11111111, "ShopArea"));
        var plan = Plan(new[] { Put(MapPath, s.Map), Put(SourcePath, s.Container) }, Game(s.Riot, baseContainer: otherBase), s.Record);

        Assert.True(ModuleOf(plan, MapPath).References > 0);
        var container = Assert.Single(plan.Modules["base"], m => m.Target == SourcePath);
        Assert.Equal(0, container.References);
        Assert.Contains(plan.SwitchNotes, n => n.Contains("milkshake_srs.materials.bin") && n.Contains("could not be declared by reference")
                                               && n.Contains("not gameplay-compatible") && n.Contains("declared as values"));
    }

    [Fact]
    public void AnEmbedOfAnotherClassIsNotCarriedByReferenceBecauseTheLoaderWouldSkipIt()
    {
        // The base slot's profile holds its music as an embed of a legacy class; the source's is the current class. ltk_meta's patch type
        // rule compares an embed's class (ValueShape.class), so a reference over it is a TypeMismatch skip on the player's machine: the
        // audio group must not be claimed.
        byte[] riot = RiotMap(DefaultSlot with { MusicClass = "LegacyMusicProperties" }, OdysseySlot, MilkshakeSlot, ArcadeSlot, AliasSlot);
        var s = Switch(carry: false, riotMap: riot);
        var plan = Plan(new[] { Put(MapPath, s.Map), Put(SourcePath, s.Container) }, Game(s.Riot), s.Record);

        // The switch retyped the embed, which no declaration can express either (an embed pin cannot change class), so the bin ships whole
        // with that reason - and it was not shipped half by reference.
        var why = Assert.Single(plan.Whole, w => w.Contains(MapPath));
        Assert.Contains("embedded struct", why);
        Assert.DoesNotContain(plan.Modules["base"], m => m.Target == MapPath);
        Assert.Contains(plan.Kept, f => f.RelPath == MapPath);
        Assert.Contains(plan.SwitchNotes, n => n.Contains("map11.bin") && n.Contains("ships whole"));
        // the container is another file, and is still declared by reference
        Assert.Equal(2, plan.References);
        Assert.Contains(plan.Modules["base"], m => m.Target == SourcePath && m.References == 2);
    }

    [Fact]
    public void AnEmbedOfTheSameClassIsCarriedByReference()
    {
        // the control of the test above: the same profile with the current class in both slots
        var s = Switch(carry: false);
        var plan = Plan(new[] { Put(MapPath, s.Map), Put(SourcePath, s.Container) }, Game(s.Riot), s.Record);
        Assert.Equal(new[] { "bankUnits", "music" }, Bodies(ModuleOf(plan, MapPath))["Audio/Default"]!.AsObject().Select(p => p.Key).ToArray());
        Assert.Empty(plan.Whole);
    }

    [Fact]
    public void TheNotesSayWhatTheReferencesFollowWhatTheyMissAndWhatTheManagerMustKnow()
    {
        var s = Switch(carry: true);
        var plan = Plan(new[] { Put(MapPath, s.Map), Put(SourcePath, s.Container) }, Game(s.Riot), s.Record);

        var shipping = Assert.Single(plan.SwitchNotes, n => n.Contains("map11.bin"));
        // what the references follow, and what a later patch leaves unrouted
        Assert.Contains("follow Milkshake_SRS's values in the installed patch", shipping);
        Assert.Contains("map-skin slots a later patch adds or renames are not routed until the project is exported again", shipping);
        // the unit-skin field the carry added to slots that lacked it (Default, Odyssey, Arcade and the alias), which needs the manager's schema
        Assert.Contains("4 field(s) are added to slots that lack them, which LTK Manager types from its class schema", shipping);

        var container = Assert.Single(plan.SwitchNotes, n => n.Contains("milkshake_srs.materials.bin"));
        Assert.Contains("2 server shop key(s)", container);
        Assert.Contains("re-keys", container);                                   // the risk: -items fails, LTK skips the whole move
        Assert.Contains("export again after a patch", container);

        // without the carry no field is added to any slot
        var plain = Switch(carry: false);
        var plainPlan = Plan(new[] { Put(MapPath, plain.Map), Put(SourcePath, plain.Container) }, Game(plain.Riot), plain.Record);
        Assert.DoesNotContain(plainPlan.SwitchNotes, n => n.Contains("are added to slots that lack them"));
    }

    // ===================================================== through the real export

    private static JsonDocument Info(string fantome)
    {
        using var zip = ZipFile.OpenRead(fantome);
        using var s = zip.GetEntry("META/info.json")!.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return JsonDocument.Parse(ms.ToArray());
    }

    private sealed class NoProgress : IProgress<(double Frac, string Stage)>
    {
        public void Report((double Frac, string Stage) value) { }
    }

    private (string Fantome, List<LogEntry> Log, ReyProject Project, MainWindowViewModel Vm) Export(bool declare, Switched s)
    {
        string root = Path.Combine(_dir, "e2e" + (++_n));
        string riotFolder = Path.Combine(root, "riot");
        foreach (var (rel, bytes) in new[] { (MapPath, s.Riot), (BasePath, BaseContainer()), (SourcePath, SourceContainer()) })
        {
            string p = Path.Combine(riotFolder, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, bytes);
        }
        string refWad = Path.Combine(root, "Map11.wad.client");
        Assert.True(WadPackService.Pack(riotFolder, refWad).Success);

        string project = Path.Combine(root, "project");
        string folder = Path.Combine(project, "Map11");
        foreach (var (rel, bytes) in new[] { (MapPath, s.Map), (SourcePath, s.Container), ("assets/x.tex", new byte[] { 1, 2, 3 }) })
        {
            string p = Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, bytes);
        }
        var proj = new ReyProject
        {
            Name = "Skin",
            RootPath = project,
            OutputDirectory = Path.Combine(root, "build"),
            ShipBinEditsAsDeclarations = declare,
            ProjectFolders = { "Map11" },
            ReferenceWads = { refWad },
        };
        BinRecipeRecord.Upsert(proj.BinRecipes, s.Record);

        var vm = new MainWindowViewModel { Project = proj };
        // the plaintext a document spells: this machine's hash tables must not decide it
        typeof(MainWindowViewModel).GetField("_declarationNames", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, Names);
        var log = new List<LogEntry>();
        ((ReyEngine.Core.Diagnostics.Logger)typeof(MainWindowViewModel).GetField("_log", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!).Logged +=
            e => { lock (log) log.Add(e); };
        Directory.CreateDirectory(proj.OutputDirectory!);
        string output = Path.Combine(root, "out.fantome");
        var meta = new FantomeMeta { Name = "Skin", Author = "Test", Version = "1.0.0", Description = "d" };
        var method = typeof(MainWindowViewModel).GetMethod("ExportFantomeCore", BindingFlags.NonPublic | BindingFlags.Instance)!;
        try { method.Invoke(vm, new object?[] { output, meta, null, proj.OutputDirectory, new NoProgress(), "Export", "No WAD was produced.", "Zipping" }); }
        catch (TargetInvocationException ex) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException!).Throw(); }
        return (output, log, proj, vm);
    }

    private static HashSet<ulong> Chunks(string fantome, string entry, string scratch)
    {
        using var zip = ZipFile.OpenRead(fantome);
        string path = Path.Combine(scratch, Guid.NewGuid().ToString("N") + ".wad.client");
        using (var s = zip.GetEntry(entry)!.Open())
        using (var f = File.Create(path))
            s.CopyTo(f);
        using var wad = WadArchive.Open(path);
        return wad.Entries.Select(e => e.PathHash).ToHashSet();
    }

    [Fact]
    public void ExportFantomeShipsTheSwitchAsGameDataByReferenceAndPacksNeitherBin()
    {
        var s = Switch(carry: true);
        var (fantome, log, _, _) = Export(declare: true, s);

        var packed = Chunks(fantome, "WAD/Map11.wad.client", _dir);
        Assert.DoesNotContain(HashAlgorithms.WadPath(MapPath), packed);
        Assert.DoesNotContain(HashAlgorithms.WadPath(SourcePath), packed);
        Assert.Contains(HashAlgorithms.WadPath("assets/x.tex"), packed);

        using var info = Info(fantome);
        var modules = info.RootElement.GetProperty("Layers").GetProperty("base").GetProperty("GameData").GetProperty("modules").EnumerateArray().ToList();
        Assert.Equal(new[] { SourcePath, MapPath }, modules.Select(m => m.GetProperty("target").GetString()).ToArray());
        Assert.All(modules, m => Assert.Equal(new[] { "target", "edits", "origin" }, m.EnumerateObject().Select(p => p.Name).ToArray()));   // no names
        string json = info.RootElement.GetProperty("Layers").GetProperty("base").GetProperty("GameData").GetRawText();
        Assert.Contains("\"ref\":\"" + SlotName("Milkshake_SRS") + ":mMapContainerLink\"", json.Replace(" ", "").Replace("\n", "").Replace("\r", ""));

        // the log says what went by reference
        string all;
        lock (log) all = string.Join("\n", log.Where(l => l.Category == "Export").Select(l => l.Message));
        Assert.Contains("by reference", all);
        Assert.Contains("declared [base] " + MapPath, all);
        Assert.Contains("Layers - base (priority 0): 1 WAD(s), 2 declared bin(s).", all);   // bins, not modules
    }

    [Fact]
    public void SendToLtkManagerIsGivenTheSameReferencesAsExport()
    {
        var s = Switch(carry: true);
        var (fantome, _, project, vm) = Export(declare: true, s);

        // Send to LTK Manager's side: the YAML manifest over the project's own files
        var files = new List<(string Layer, string WadFolder, string RelPath, string AbsPath)>();
        foreach (var folder in project.ProjectFolders)
        {
            string abs = project.ResolveProjectPath(folder);
            foreach (var (_, path) in WadPackService.EnumerateChunkFiles(abs))
                files.Add((project.LayerOf(Path.GetFileName(abs)), Path.GetFileName(abs), Path.GetRelativePath(abs, path).Replace('\\', '/'), path));
        }
        dynamic sent = typeof(MainWindowViewModel).GetMethod("DeclareGameBins", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, new object[] { files })!;
        Dictionary<string, string> manifests = sent.Item2;
        List<(string Layer, string WadFolder, string RelPath, string AbsPath)> kept = sent.Item1;

        string yaml = manifests["base"];
        using var info = Info(fantome);
        string json = info.RootElement.GetProperty("Layers").GetProperty("base").GetProperty("GameData").GetRawText();
        Assert.Equal(Regex.Matches(json, "\"ref\"").Count, Regex.Matches(yaml, "\\{\"ref\": ").Count);
        Assert.True(Regex.Matches(yaml, "\\{\"ref\": ").Count > 10);
        Assert.Equal(new[] { SourcePath, MapPath }, Regex.Matches(yaml, "^  - target: \"(.+)\"$", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToArray());
        // neither bin is sent as a file, exactly as neither is packed
        Assert.DoesNotContain(kept, k => k.RelPath is MapPath or SourcePath);
        Assert.Contains(kept, k => k.RelPath == "assets/x.tex");
        var report = (List<(int Level, string Line)>)sent.Item3;
        Assert.Contains(report, r => r.Line.Contains("by reference") && r.Line.Contains("map11.bin"));
    }

    [Fact]
    public void WithTheSettingOffTheForcedBinsShipWholeAsBefore()
    {
        var s = Switch(carry: true);
        var (fantome, log, _, _) = Export(declare: false, s);

        var packed = Chunks(fantome, "WAD/Map11.wad.client", _dir);
        Assert.Contains(HashAlgorithms.WadPath(MapPath), packed);
        Assert.Contains(HashAlgorithms.WadPath(SourcePath), packed);
        using var info = Info(fantome);
        foreach (var layer in info.RootElement.GetProperty("Layers").EnumerateObject())
            Assert.False(layer.Value.TryGetProperty("GameData", out _), layer.Name);
        lock (log) Assert.DoesNotContain(log, l => l.Message.Contains("by reference"));
    }
}
