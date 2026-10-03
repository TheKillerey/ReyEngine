using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M815: the <c>ref</c> value and the signed property key, in both renderers. The strings are golden: each is the spelling
/// league-mod's <c>ltk_game_data</c> (0.4+, where references begin) reads - <c>{"ref": "&lt;entry&gt;:&lt;path&gt;"}</c>, the text split
/// at the FIRST ':' (<c>reference.rs</c>), path segments <c>name</c>, <c>name[index]</c> and <c>name{key}</c> with the DECIMAL value of a
/// hash key (<c>path/resolve.rs</c> <c>key_as</c>), and <c>+</c> / <c>-</c> in front of the key (<c>property.rs</c> <c>Sign::of</c>). They were
/// also checked outside the suite the strong way: the export is read by <c>ltk_fantome</c> and applied over the installed game's bins
/// by <c>ltk_game_data::apply</c> (see the M815 commit for the real switches and counts).
/// </summary>
public sealed class BinDeclarationRefTests
{
    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Json(JsonNode? node) => node is null ? "null" : node.ToJsonString(Compact);

    private static DeclaredChunk Module(string entry, params DeclEntry[] edits) =>
        BinDeclarations.FromModel("data/test.bin",
            new DeclaredEdit(Array.Empty<string>(), Array.Empty<string>(), new[] { new DeclaredBody(entry, edits) }, Array.Empty<DeclaredObject>()),
            edits.Length);

    /// <summary>The edit body of the module's one entry, as the document spells it.</summary>
    private static string BodyJson(DeclaredChunk c, string entry) =>
        Json(BinDeclarations.GameDataDocument(new[] { c })["modules"]![0]!["edits"]![0]![entry]);

    private static DeclRef Ref(string entry, DeclPath path) => new(entry, path);

    // ===================================================== the value

    [Fact]
    public void ARefIsAOneKeyMappingOfEntryColonPathInTheDocument()
    {
        var c = Module("Maps/Test/Thing", new DeclEntry("speed", Ref("Maps/Other/Thing", DeclPath.Field("speed"))));
        Assert.Equal("""{"speed":{"ref":"Maps/Other/Thing:speed"}}""", BodyJson(c, "Maps/Test/Thing"));
        Assert.Equal("""{"version":1,"modules":[{"target":"data/test.bin","edits":[{"Maps/Test/Thing":{"speed":{"ref":"Maps/Other/Thing:speed"}}}],"origin":{"manifest":"game_data.yaml","source":null,"module":0}}]}""",
            Json(BinDeclarations.GameDataDocument(new[] { c })));
        Assert.Equal(1, c.References);
    }

    [Fact]
    public void ARefIsTheDocumentFormInYamlToo()
    {
        var c = Module("Maps/Test/Thing", new DeclEntry("speed", Ref("Maps/Other/Thing", DeclPath.Field("speed"))));
        Assert.Equal("  - target: \"data/test.bin\"\n    Maps/Test/Thing:\n      speed: {\"ref\": \"Maps/Other/Thing:speed\"}\n", c.Module);
        Assert.EndsWith(c.Module!, BinDeclarations.Manifest(new[] { c }));
    }

    [Fact]
    public void AnEntryNoNameIsKnownForIsSpelledAsItsHash()
    {
        // 0x and eight lowercase hex digits is the one spelling the loader reads as a hash (coerce.rs hex32)
        var c = Module("0xfe261f60", new DeclEntry("0x2d3285eb", Ref("0x976314f0", DeclPath.Field("0x2d3285eb"))));
        Assert.Equal("""{"0x2d3285eb":{"ref":"0x976314f0:0x2d3285eb"}}""", BodyJson(c, "0xfe261f60"));
        Assert.Contains("\"0xfe261f60\":\n      \"0x2d3285eb\": {\"ref\": \"0x976314f0:0x2d3285eb\"}\n", c.Module);
    }

    // ===================================================== the path

    [Theory]
    [InlineData("a", "a")]
    [InlineData("a.b", "a.b")]
    [InlineData("a[0]", "a[0]")]
    [InlineData("a[12].b", "a[12].b")]
    [InlineData("items{505318251}", "items{505318251}")]
    [InlineData("items{1111561002}.name", "items{1111561002}.name")]
    [InlineData("a.b[3].c{7}", "a.b[3].c{7}")]
    public void ASegmentIsANameWithAnOptionalIndexOrDecimalKey(string expected, string text)
    {
        Assert.Equal(expected, Build(text).Text);

        static DeclPath Build(string t)
        {
            DeclPath? path = null;
            foreach (var segment in t.Split('.'))
            {
                string name = segment.Split('[', '{')[0];
                path = path is null ? DeclPath.Field(name) : path.Then(name);
                if (segment.Contains('[')) path = path.At(int.Parse(segment[(segment.IndexOf('[') + 1)..segment.IndexOf(']')]));
                if (segment.Contains('{')) path = path.Keyed(ulong.Parse(segment[(segment.IndexOf('{') + 1)..segment.IndexOf('}')]));
            }
            return path!;
        }
    }

    [Fact]
    public void AHashKeyIsSpelledByItsDecimalValueAndAnIndexHasNoLeadingZero()
    {
        // {0x...} is not a key literal; and 010 would read as octal 8 (parse.rs parse_int)
        Assert.Equal("items{4294967295}", DeclPath.Field("items").Keyed(uint.MaxValue).Text);
        Assert.Equal("items{18446744073709551615}", DeclPath.Field("items").Keyed(ulong.MaxValue).Text);
        Assert.Equal("v{-5}", DeclPath.Field("v").Keyed(-5L).Text);
        Assert.Equal("a[0]", DeclPath.Field("a").At(0).Text);
        Assert.Equal("a[10]", DeclPath.Field("a").At(10).Text);
    }

    [Fact]
    public void APathRefusesWhatThePathParserRefuses()
    {
        foreach (var bad in new[] { "", "a.b", "a[0]", "a{1}", "a(b)", "a]", "a\nb", "-a", "+a" })
            Assert.Throws<ArgumentException>(() => DeclPath.Field(bad));
        Assert.Throws<ArgumentOutOfRangeException>(() => DeclPath.Field("a").At(-1));
        // two subscripts on one segment are a DoubleSubscript
        Assert.Throws<InvalidOperationException>(() => DeclPath.Field("a").At(1).Keyed(2UL));
        Assert.Throws<InvalidOperationException>(() => DeclPath.Field("a").Keyed(2UL).At(1));
        // a subscript on a later segment is fine
        Assert.Equal("a[1].b[2]", DeclPath.Field("a").At(1).Then("b").At(2).Text);
    }

    [Fact]
    public void AnEntryIsAPathWithASlashOrAHashAndHoldsNoColon()
    {
        var path = DeclPath.Field("x");
        foreach (var bad in new[] { "", "Thing", "A:B/C", "0X12345678", "0x1234567", "0x123456789", "0xzzzzzzzz", "links", "a\nb/c" })
            Assert.Throws<ArgumentException>(() => new DeclRef(bad, path));
        Assert.Equal("Maps/A/B:x", new DeclRef("Maps/A/B", path).Text);
        Assert.Equal("0x0badf00d:x", new DeclRef("0x0badf00d", path).Text);
        Assert.Throws<ArgumentNullException>(() => new DeclRef("Maps/A/B", null!));
    }

    [Fact]
    public void ThePathMayHoldAColonAndTheTextSplitsAtTheFirstOne()
    {
        // the entry holds none; the path may (a text key would), and the loader splits at the first (reference.rs)
        var r = new DeclRef("Maps/A/B", DeclPath.Field("a:b"));
        Assert.Equal("Maps/A/B:a:b", r.Text);
    }

    // ===================================================== the sign

    [Fact]
    public void ARemovalAndAnAdditionKeepTheirSignInTheKey()
    {
        // Crauzer's module 1, as ltk_game_data reads it: remove two keys, add them under the server's keys with references
        var edits = new[]
        {
            DeclEntry.Remove("items", new DeclList(new DeclValue[] { new DeclText("0x4241132a"), new DeclText("0xd0c80c35") })),
            DeclEntry.Add("items", new DeclMap(new[]
            {
                new DeclEntry("0x1e1e8b6b", Ref("0xfe261f60", DeclPath.Field("items").Keyed(1111561002UL))),
                new DeclEntry("0x9f77d47f", Ref("0xfe261f60", DeclPath.Field("items").Keyed(3502771253UL))),
            })),
        };
        var c = Module("0xfe261f60", edits);
        Assert.Equal("""{"-items":["0x4241132a","0xd0c80c35"],"+items":{"0x1e1e8b6b":{"ref":"0xfe261f60:items{1111561002}"},"0x9f77d47f":{"ref":"0xfe261f60:items{3502771253}"}}}""",
            BodyJson(c, "0xfe261f60"));
        Assert.Equal("  - target: \"data/test.bin\"\n    \"0xfe261f60\":\n"
                     + "      \"-items\": [\"0x4241132a\", \"0xd0c80c35\"]\n"
                     + "      \"+items\": {\"0x1e1e8b6b\": {\"ref\": \"0xfe261f60:items{1111561002}\"}, \"0x9f77d47f\": {\"ref\": \"0xfe261f60:items{3502771253}\"}}\n",
            c.Module);
        Assert.Equal(2, c.References);
    }

    [Fact]
    public void AMapKeyIsHexAndAPathKeyIsDecimalSoTheSameKeyHasTwoSpellings()
    {
        // 1111561002 == 0x4241132a: "-items" names it as hex text, the reference as a decimal key
        Assert.Equal(0x4241132aUL, 1111561002UL);
        var c = Module("0xfe261f60",
            DeclEntry.Remove("items", new DeclList(new DeclValue[] { new DeclText($"0x{0x4241132au:x8}") })),
            DeclEntry.Add("items", new DeclMap(new[] { new DeclEntry($"0x{0x1e1e8b6bu:x8}", Ref("0xfe261f60", DeclPath.Field("items").Keyed(0x4241132aUL))) })));
        string body = BodyJson(c, "0xfe261f60");
        Assert.Contains("\"-items\":[\"0x4241132a\"]", body);
        Assert.Contains("items{1111561002}", body);
        Assert.DoesNotContain("items{0x", body);
    }

    [Fact]
    public void ARefIsRefusedAsTheOperandOfARemoval()
    {
        // ltk_game_data reads the operands of a map removal as key text: a reference there is a KindMismatch, skipped on every machine
        var remove = DeclEntry.Remove("items", new DeclList(new DeclValue[] { Ref("0xfe261f60", DeclPath.Field("items").Keyed(1UL)) }));
        var ex = Assert.Throws<BinDeclarations.Refused>(() => Module("0xfe261f60", remove));
        Assert.Contains("removal", ex.Message);
        // nested inside the operand it is the same
        var nested = DeclEntry.Remove("list", new DeclList(new DeclValue[] { new DeclList(new DeclValue[] { Ref("Maps/A/B", DeclPath.Field("x")) }) }));
        Assert.Throws<BinDeclarations.Refused>(() => Module("0xfe261f60", nested));
        // an addition may carry one, and a plain set, and a list element, and a struct field
        var fine = Module("0xfe261f60",
            DeclEntry.Add("list", new DeclList(new DeclValue[] { Ref("Maps/A/B", DeclPath.Field("x")) })),
            new DeclEntry("one", Ref("Maps/A/B", DeclPath.Field("y"))),
            new DeclEntry("embedded", new DeclStruct("embed", "Thing", new[] { new DeclEntry("f", Ref("Maps/A/B", DeclPath.Field("z"))) })));
        Assert.Equal(3, fine.References);
        Assert.Equal("""{"+list":[{"ref":"Maps/A/B:x"}],"one":{"ref":"Maps/A/B:y"},"embedded":{"embed":{"class":"Thing","set":{"f":{"ref":"Maps/A/B:z"}}}}}""",
            BodyJson(fine, "0xfe261f60"));
    }

    // ===================================================== the diff writes neither

    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private sealed class DiffNames : IDeclarationNames
    {
        private readonly Dictionary<uint, string> _names = new[]
        {
            "Maps/Test/Thing", "Thing", "label", "lookup", "tags", "ref", "+items", "-items",
        }.ToDictionary(H);
        public string? Field(uint hash) => _names.GetValueOrDefault(hash);
        public string? Class(uint hash) => Field(hash);
        public string? Entry(uint hash) => Field(hash);
        public string? File(ulong hash) => null;
    }

    private static byte[] Bin(params BinTreeProperty[] props)
    {
        using var ms = new MemoryStream();
        new BinTree(new[] { new BinTreeObject(H("Maps/Test/Thing"), H("Thing"), props) }, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    private static KeyValuePair<BinTreeProperty, BinTreeProperty> Kv(string key, string value) => new(new BinTreeString(0, key), new BinTreeString(0, value));

    /// <summary>Every JSON object of a document.</summary>
    private static IEnumerable<JsonObject> AllObjects(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                yield return o;
                foreach (var child in o) foreach (var inner in AllObjects(child.Value)) yield return inner;
                break;
            case JsonArray a:
                foreach (var child in a) foreach (var inner in AllObjects(child)) yield return inner;
                break;
        }
    }

    private static BinTreeProperty[] LookAlikes(string text, string mapKey, string fieldName) => new BinTreeProperty[]
    {
        new BinTreeString(H("label"), text),
        new BinTreeMap(H("lookup"), BinPropertyType.String, BinPropertyType.String, new[] { Kv(mapKey, text), Kv("-items", "x") }),
        new BinTreeContainer(H("tags"), BinPropertyType.String, new BinTreeProperty[] { new BinTreeString(0, text), new BinTreeString(0, "ref") }),
        new BinTreeU32(H(fieldName), (uint)text.Length),   // a FIELD named like a sign: spelled by its hash
    };

    [Fact]
    public void ADiffNeverWritesAReferenceOrASignWhateverTheValuesLookLike()
    {
        // Values that LOOK like a reference or a sign are data and the diff spells them as data: a string holding "entry:path", a string
        // "ref", a string-keyed map with keys "+items" and "-items", a list of such strings, and a field NAMED "+items" - which is spelled by
        // its hash, because a key that starts with a sign would read as an add.
        var riot = Bin(LookAlikes("old", "plain", "-items"));
        var mod = Bin(LookAlikes("Maps/Other/Thing:speed", "+items", "-items").Append(new BinTreeU32(H("+items"), 7)).ToArray());
        var chunk = BinDeclarations.Convert("data/test.bin", riot, mod, new DiffNames());
        Assert.True(chunk.Declared, chunk.WhyNot);

        Assert.Equal(0, chunk.References);
        var doc = BinDeclarations.GameDataDocument(new[] { chunk });
        // no value of the document is a reference: nothing in it is an object holding the one key "ref" over a string ...
        Assert.DoesNotContain(AllObjects(doc), o => o.Count == 1 && o.ContainsKey("ref") && o["ref"] is JsonValue);
        // ... and no PROPERTY key is signed. The keys of a map value are data (a string-keyed map may hold "+items"), so only the
        // edit keys of each entry body are looked at.
        var body = doc["modules"]![0]!["edits"]![0]!.AsObject();
        foreach (var (entry, edits) in body)
        {
            if (entry is "objects" or "links" or "-links") continue;   // bindings, which the diff has always written
            foreach (var key in edits!.AsObject().Select(e => e.Key))
                Assert.False(key.StartsWith('+') || key.StartsWith('-'), $"{entry}: the edit key '{key}' is signed");
        }
        // the data that looks like signs and references is there, as data
        string json = doc.ToJsonString(Compact);
        Assert.Contains("\"Maps/Other/Thing:speed\"", json);
        Assert.Contains("\"+items\"", json);
        // the same text through the YAML renderer carries no reference either
        Assert.DoesNotContain("{\"ref\": ", chunk.Module);
        Assert.DoesNotContain("      \"+", chunk.Module);   // an indented key in the body of an entry, quoted: a signed edit
        Assert.DoesNotContain("      \"-", chunk.Module);
    }

    [Fact]
    public void AMapWhoseOnlyKeyIsRefIsRefusedByTheDiffInsteadOfReadingBackAsAReference()
    {
        // `{"ref": "a/b:c"}` as a map VALUE would be a reference to the loader; the diff has to refuse such a map, not write one
        var riot = Bin(new BinTreeMap(H("lookup"), BinPropertyType.String, BinPropertyType.String, new[] { Kv("other", "x") }));
        var mod = Bin(new BinTreeMap(H("lookup"), BinPropertyType.String, BinPropertyType.String, new[] { Kv("ref", "Maps/Other/Thing:speed") }));
        var chunk = BinDeclarations.Convert("data/test.bin", riot, mod, new DiffNames());
        Assert.False(chunk.Declared);
        Assert.Contains("ref", chunk.WhyNot);
        Assert.Equal(0, chunk.References);
    }
}
