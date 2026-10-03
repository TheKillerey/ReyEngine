using System.Numerics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Build;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M814: a game bin's edit as the JSON document a .fantome carries (<c>Layers.&lt;name&gt;.GameData</c>), rendered
/// from the SAME model as M757's YAML manifest. The strings here are golden: each is the spelling league-mod's
/// <c>ltk_game_data</c> 0.8 reads back to the same bits. That was also checked the strong way, outside the suite:
/// the export is read by <c>ltk_fantome</c>, each layer's document is applied over the untouched bin by
/// <c>ltk_game_data::apply</c>, and the result is compared with the project's bin (see the M814 commit).
/// </summary>
public sealed class BinDeclarationsJsonTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private sealed class Names : IDeclarationNames
    {
        private readonly Dictionary<uint, string> _bin = new[]
        {
            "Maps/Test/Thing", "Mods/Test/New", "Characters/Old", "NoSlash", "Thing", "0x12345678",
            "speed", "label", "inner", "count", "colour", "transform", "tags", "lookup", "maybe", "links",
            "hash", "ref", "InnerData", "objects", "Other/Thing",
        }.ToDictionary(H);
        public string? Field(uint hash) => hash == 0xdeadbeef ? "liar" : _bin.GetValueOrDefault(hash);
        public string? Class(uint hash) => Field(hash);
        public string? Entry(uint hash) => Field(hash);
        public string? File(ulong hash) => hash == HashAlgorithms.WadPath("assets/a.tex") ? "assets/a.tex" : null;
    }

    private static readonly Names N = new();

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static byte[] Bin(IEnumerable<BinTreeObject> objects, params string[] deps)
    {
        using var ms = new MemoryStream();
        new BinTree(objects, deps).Write(ms);
        return ms.ToArray();
    }

    private static BinTreeObject Thing(params BinTreeProperty[] props) => new(H("Maps/Test/Thing"), H("Thing"), props);

    private static DeclaredChunk Convert(byte[] riot, byte[] mod, string target = "data/test.bin") =>
        BinDeclarations.Convert(target, riot, mod, N);

    private static string Json(JsonNode? node) => node is null ? "null" : node.ToJsonString(Compact);

    /// <summary>The document for one bin that gained <paramref name="added"/> on its one object.</summary>
    private static string DocOf(params BinTreeProperty[] added)
    {
        var c = Convert(Bin(new[] { Thing() }), Bin(new[] { Thing(added) }));
        Assert.True(c.Declared, c.WhyNot);
        return Json(BinDeclarations.GameDataDocument(new[] { c }));
    }

    /// <summary>The spelling of one added property's value, found in the document.</summary>
    private static string ValueOf(BinTreeProperty added)
    {
        var c = Convert(Bin(new[] { Thing() }), Bin(new[] { Thing(added) }));
        Assert.True(c.Declared, c.WhyNot);
        var doc = BinDeclarations.GameDataDocument(new[] { c });
        var body = doc["modules"]![0]!["edits"]![0]!["Maps/Test/Thing"]!.AsObject();
        var only = Assert.Single(body);
        return Json(only.Value);
    }

    // ===================================================== the document

    [Fact]
    public void ADocumentIsVersionOneWithTargetModulesAndAnOriginEachAndNoNames()
    {
        var riot = Bin(new[] { Thing(new BinTreeF32(H("speed"), 1f)) });
        var mod = Bin(new[] { Thing(new BinTreeF32(H("speed"), 0.37f)) });
        Assert.Equal(
            """{"version":1,"modules":[{"target":"data/test.bin","edits":[{"Maps/Test/Thing":{"speed":0.37}}],"origin":{"manifest":"game_data.yaml","source":null,"module":0}}]}""",
            Json(BinDeclarations.GameDataDocument(new[] { Convert(riot, mod) })));
    }

    [Fact]
    public void ModuleNamesAreOffByDefaultBecauseLtkGameData06RefusesThem()
    {
        // ltk_game_data 0.6.0 (LTK Manager v1.21.0's pin) has deny_unknown_fields on a module and no `name` - the
        // field arrives in 0.7.0. Such a manager refuses the WHOLE layer's declarations, and the player silently
        // loses every bin edit. Measured against the 0.6.0 crate in M814; the constant is the single switch.
        Assert.False(FantomeLayers.ModuleNames);
        var c = Convert(Bin(new[] { Thing(new BinTreeF32(H("speed"), 1f)) }), Bin(new[] { Thing(new BinTreeF32(H("speed"), 2f)) }))
            with { Label = "DATA/Maps/Test.bin" };                // even a labelled chunk carries no name by default
        var module = BinDeclarations.GameDataDocument(new[] { c })["modules"]![0]!.AsObject();
        Assert.Equal(new[] { "target", "edits", "origin" }, module.Select(p => p.Key).ToArray());
        Assert.Equal(
            Json(module),
            Json(BinDeclarations.GameDataDocument(new[] { c }, moduleNames: false)["modules"]![0]));
    }

    [Fact]
    public void WithModuleNamesOnAModuleIsNamedAfterItsTargetWhenNothingLabelsIt()
    {
        // the renderer keeps its name support for the day no manager that pins 0.6 is worth supporting
        var riot = Bin(new[] { Thing(new BinTreeF32(H("speed"), 1f)) });
        var mod = Bin(new[] { Thing(new BinTreeF32(H("speed"), 0.37f)) });
        Assert.Equal(
            """{"version":1,"modules":[{"name":"data/test.bin","target":"data/test.bin","edits":[{"Maps/Test/Thing":{"speed":0.37}}],"origin":{"manifest":"game_data.yaml","source":null,"module":0}}]}""",
            Json(BinDeclarations.GameDataDocument(new[] { Convert(riot, mod) }, moduleNames: true)));
    }

    [Fact]
    public void WithModuleNamesOnAModuleIsLabelledWithTheProjectPathWhenOneIsGiven()
    {
        var riot = Bin(new[] { Thing(new BinTreeF32(H("speed"), 1f)) });
        var mod = Bin(new[] { Thing(new BinTreeF32(H("speed"), 2f)) });
        var c = Convert(riot, mod) with { Label = "DATA/Maps/Test.bin" };
        var module = BinDeclarations.GameDataDocument(new[] { c }, moduleNames: true)["modules"]![0]!;
        Assert.Equal("DATA/Maps/Test.bin", module["name"]!.GetValue<string>());
        Assert.Equal("data/test.bin", module["target"]!.GetValue<string>());
    }

    [Fact]
    public void ModulesAreNumberedInTheOrderGivenAndAnUndeclaredChunkHasNone()
    {
        BinTreeObject With(float v) => Thing(new BinTreeF32(H("speed"), v));
        var a = Convert(Bin(new[] { With(1f) }), Bin(new[] { With(2f) }), "data/a.bin");
        var unchanged = Convert(Bin(new[] { With(1f) }), Bin(new[] { With(1f) }), "data/b.bin");
        var whole = Convert(Bin(new[] { Thing(new BinTreeU32(H("count"), 1)) }), Bin(new[] { Thing() }), "data/c.bin");
        var d = Convert(Bin(new[] { With(1f) }), Bin(new[] { With(3f) }), "data/d.bin");
        Assert.True(unchanged.Unchanged);
        Assert.False(whole.Declared);

        var modules = BinDeclarations.GameDataDocument(new[] { a, unchanged, whole, d })["modules"]!.AsArray();
        Assert.Equal(2, modules.Count);
        Assert.Equal(new[] { "data/a.bin", "data/d.bin" }, modules.Select(m => m!["target"]!.GetValue<string>()).ToArray());
        Assert.Equal(new[] { 0, 1 }, modules.Select(m => m!["origin"]!["module"]!.GetValue<int>()).ToArray());
    }

    [Fact]
    public void AnEmptyChunkListIsAnEmptyButValidDocument()
    {
        Assert.Equal("""{"version":1,"modules":[]}""", Json(BinDeclarations.GameDataDocument(Array.Empty<DeclaredChunk>())));
    }

    [Fact]
    public void NoModuleHoldsAnEmptyEditOrAnEmptyBody()
    {
        // every shape of change: a value, an added object, a removed object, a link
        var old = new BinTreeObject(H("Characters/Old"), H("Thing"), new BinTreeProperty[] { new BinTreeU32(H("count"), 1) });
        var added = new BinTreeObject(H("Mods/Test/New"), H("Thing"), Array.Empty<BinTreeProperty>());
        var cases = new[]
        {
            Convert(Bin(new[] { Thing(new BinTreeU32(H("count"), 1)) }), Bin(new[] { Thing(new BinTreeU32(H("count"), 2)) })),
            Convert(Bin(new[] { Thing() }), Bin(new[] { Thing(), added })),
            Convert(Bin(new[] { Thing(), old }), Bin(new[] { Thing() })),
            Convert(Bin(new[] { Thing() }, "DATA/A.bin"), Bin(new[] { Thing() }, "DATA/B.bin")),
        };
        foreach (var c in cases)
        {
            Assert.True(c.Declared, c.WhyNot);
            var doc = BinDeclarations.GameDataDocument(new[] { c });
            var module = doc["modules"]![0]!.AsObject();
            Assert.False(module.ContainsKey("entries"));          // an `entries` module is never written
            var edits = module["edits"]!.AsArray();
            Assert.Single(edits);
            Assert.NotEmpty(edits[0]!.AsObject());                 // the one edit holds at least one binding
            foreach (var (key, value) in edits[0]!.AsObject())
            {
                if (value is JsonObject o) Assert.NotEmpty(o);
                else if (value is JsonArray a) Assert.NotEmpty(a);
                else Assert.Fail($"{key} is neither a mapping nor a list");
            }
        }
    }

    [Fact]
    public void ABodyListsObjectsThenLinksThenEntriesLikeLeagueModWritesIt()
    {
        // ltk_game_data's Bindings serialises overrides, objects, links, -links, then the entries
        var added = new BinTreeObject(H("Mods/Test/New"), H("Thing"), new BinTreeProperty[] { new BinTreeF32(H("speed"), 2f) });
        var riot = Bin(new[] { Thing(new BinTreeF32(H("speed"), 1f)) }, "DATA/A.bin");
        var mod = Bin(new[] { Thing(new BinTreeF32(H("speed"), 3f)), added }, "DATA/B.bin");
        var body = BinDeclarations.GameDataDocument(new[] { Convert(riot, mod) })["modules"]![0]!["edits"]![0]!.AsObject();
        Assert.Equal(new[] { "objects", "links", "-links", "Maps/Test/Thing" }, body.Select(p => p.Key).ToArray());
        Assert.Equal("""{"Mods/Test/New":{"class":"Thing","set":{"speed":2}}}""", Json(body["objects"]));
        Assert.Equal("""["DATA/B.bin"]""", Json(body["links"]));
        Assert.Equal("""["DATA/A.bin"]""", Json(body["-links"]));
    }

    [Fact]
    public void AnObjectTheProjectRemovedIsRemoveTrue()
    {
        var old = new BinTreeObject(H("Characters/Old"), H("Thing"), new BinTreeProperty[] { new BinTreeU32(H("count"), 1) });
        var c = Convert(Bin(new[] { Thing(), old }), Bin(new[] { Thing() }));
        Assert.Equal("""{"version":1,"modules":[{"target":"data/test.bin","edits":[{"objects":{"Characters/Old":{"remove":true}}}],"origin":{"manifest":"game_data.yaml","source":null,"module":0}}]}""",
            Json(BinDeclarations.GameDataDocument(new[] { c })));
    }

    [Fact]
    public void AnAddedObjectWithNoFieldsHasNoSet()
    {
        var added = new BinTreeObject(H("Mods/Test/New"), H("Thing"), Array.Empty<BinTreeProperty>());
        var body = BinDeclarations.GameDataDocument(new[] { Convert(Bin(new[] { Thing() }), Bin(new[] { Thing(), added })) })
            ["modules"]![0]!["edits"]![0]!;
        Assert.Equal("""{"objects":{"Mods/Test/New":{"class":"Thing"}}}""", Json(body));
    }

    // ===================================================== every value type, spelled

    [Fact] public void I8() => Assert.Equal("-128", ValueOf(new BinTreeI8(H("count"), sbyte.MinValue)));
    [Fact] public void U8() => Assert.Equal("255", ValueOf(new BinTreeU8(H("count"), byte.MaxValue)));
    [Fact] public void I16() => Assert.Equal("-32768", ValueOf(new BinTreeI16(H("count"), short.MinValue)));
    [Fact] public void U16() => Assert.Equal("65535", ValueOf(new BinTreeU16(H("count"), ushort.MaxValue)));
    [Fact] public void I32() => Assert.Equal("-2147483648", ValueOf(new BinTreeI32(H("count"), int.MinValue)));
    [Fact] public void U32() => Assert.Equal("4294967295", ValueOf(new BinTreeU32(H("count"), uint.MaxValue)));
    [Fact] public void I64() => Assert.Equal("-9223372036854775808", ValueOf(new BinTreeI64(H("count"), long.MinValue)));

    /// <summary>A u64 past the i64 range is still an integer token: <c>Value::Integer(i128)</c> holds it.</summary>
    [Fact] public void U64() => Assert.Equal("18446744073709551615", ValueOf(new BinTreeU64(H("count"), ulong.MaxValue)));

    [Fact] public void Bool() => Assert.Equal("true", ValueOf(new BinTreeBool(H("count"), true)));
    [Fact] public void Flag() => Assert.Equal("false", ValueOf(new BinTreeBitBool(H("count"), false)));

    /// <summary>An integer is NEVER spelled with a point: the loader reads an integer only from an integer.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-7)]
    public void AnIntegerHasNoDecimalPoint(int value) =>
        Assert.DoesNotContain('.', ValueOf(new BinTreeI32(H("count"), value)));

    [Theory]
    [InlineData(0.37f, "0.37")]
    [InlineData(1f, "1")]
    [InlineData(-3.5f, "-3.5")]
    [InlineData(1e-30f, "1E-30")]
    [InlineData(3.4028235E+38f, "3.4028235E+38")]
    [InlineData(16777216f, "16777216")]
    public void AnF32IsItsShortestRoundTripToken(float value, string expected) =>
        Assert.Equal(expected, ValueOf(new BinTreeF32(H("speed"), value)));

    [Fact]
    public void NegativeZeroKeepsItsSignBit() =>
        Assert.Equal("-0.0", ValueOf(new BinTreeF32(H("speed"), -0f)));

    /// <summary>.NET prints 123456792f as 123456790, which the loader reads as an INTEGER and refuses for an
    /// f32 (<c>single()</c> accepts an integer only when the f32 represents it exactly - the property would be
    /// skipped). A whole-number spelling that is not the exact number gets a point.</summary>
    [Fact]
    public void AWholeNumberThatIsNotExactReadsAsAFloat()
    {
        Assert.Equal("123456790.0", ValueOf(new BinTreeF32(H("speed"), 123456792f)));
        Assert.Equal("-123456790.0", ValueOf(new BinTreeF32(H("speed"), -123456792f)));
        Assert.Equal("123456790.0", BinDeclarations.Float(123456792f));
        Assert.Equal("100000000", BinDeclarations.Float(100000000f));   // exact, so still a plain integer token
    }

    [Fact]
    public void ANumberTokenIsWrittenAsTheDiffChoseIt()
    {
        // the token must survive the pretty-printer: JSON numbers are not re-formatted on the way out
        var doc = DocOf(new BinTreeF32(H("speed"), 1e-30f), new BinTreeF32(H("count"), -0f));
        Assert.Contains("\"speed\":1E-30", doc);
        Assert.Contains("\"count\":-0.0", doc);
    }

    [Fact] public void Vec2() => Assert.Equal("[1,2.5]", ValueOf(new BinTreeVector2(H("count"), new Vector2(1, 2.5f))));
    [Fact] public void Vec3() => Assert.Equal("[1,2,3]", ValueOf(new BinTreeVector3(H("count"), new Vector3(1, 2, 3))));
    [Fact] public void Vec4() => Assert.Equal("[1,2,3,4]", ValueOf(new BinTreeVector4(H("count"), new Vector4(1, 2, 3, 4))));

    /// <summary>Sixteen numbers in the file's row order: <c>ltk_meta</c> reads a mtx44 row-major, which is the
    /// order <see cref="Matrix4x4"/> holds it, and coercion reads sixteen numbers as rows.</summary>
    [Fact]
    public void Mtx44IsSixteenNumbersInFileOrder() =>
        Assert.Equal("[1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16]",
            ValueOf(new BinTreeMatrix44(H("count"), new Matrix4x4(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16))));

    [Fact]
    public void RgbaIsFourIntegersOfZeroTo255() =>
        Assert.Equal("[255,128,0,64]", ValueOf(new BinTreeColor(H("count"), new LeagueToolkit.Core.Primitives.Color((byte)255, (byte)128, (byte)0, (byte)64))));

    [Fact]
    public void AStringIsEscapedAsJsonEscapesIt()
    {
        // the text: say "hi" then a backslash and an n, a tab, "tab", U+0001, "end". ASCII on purpose: LeagueToolkit
        // 4.1.0-beta.53's BinTreeString writer puts the CHARACTER count in the length prefix, so a bin written
        // with a non-ASCII string does not read back (measured: "caf" + U+FFFD) and no declaration test can use one.
        string text = "say \"hi\"\\n\ttab \u0001 end";
        // as JSON: the quotes and the backslash escaped, then \t and \u0001
        Assert.Equal("\"say \\\"hi\\\"\\\\n\\ttab \\u0001 end\"", ValueOf(new BinTreeString(H("label"), text)));
    }

    [Fact]
    public void AHashIsItsNameWhereTheNameHashesBackElseTheHexForm()
    {
        Assert.Equal("\"speed\"", ValueOf(new BinTreeHash(H("count"), H("speed"))));
        Assert.Equal("\"0x12345678\"", ValueOf(new BinTreeHash(H("count"), 0x12345678)));
        Assert.Equal("\"0x00000000\"", ValueOf(new BinTreeHash(H("count"), 0)));
    }

    /// <summary>A name that merely LOOKS like a hash is not used as a name: the loader reads "0x12345678" as
    /// the hash 0x12345678, not as the text, so the name's own hash would be lost.</summary>
    [Fact]
    public void ANameThatLooksLikeAHashIsNotUsedAsAName() =>
        Assert.Equal($"\"0x{H("0x12345678"):x8}\"", ValueOf(new BinTreeHash(H("count"), H("0x12345678"))));

    [Fact]
    public void ALinkIsAnEntryNameOrHex()
    {
        Assert.Equal("\"Maps/Test/Thing\"", ValueOf(new BinTreeObjectLink(H("count"), H("Maps/Test/Thing"))));
        Assert.Equal("\"0x0badf00d\"", ValueOf(new BinTreeObjectLink(H("count"), 0x0badf00d)));
    }

    [Fact]
    public void AFileIsAPathWhereThePathHashesBackElseSixteenDigits()
    {
        Assert.Equal("\"assets/a.tex\"", ValueOf(new BinTreeWadChunkLink(H("lookup"), HashAlgorithms.WadPath("assets/a.tex"))));
        Assert.Equal("\"0x0123456789abcdef\"", ValueOf(new BinTreeWadChunkLink(H("lookup"), 0x0123456789abcdef)));
    }

    [Fact]
    public void AListIsAnArray() =>
        Assert.Equal("""["speed","0x12345678"]""",
            ValueOf(new BinTreeContainer(H("tags"), BinPropertyType.Hash,
                new BinTreeProperty[] { new BinTreeHash(0, H("speed")), new BinTreeHash(0, 0x12345678) })));

    [Fact]
    public void AnOptionIsNullTheElementOrAOneElementListWhereTheElementIsAList()
    {
        Assert.Equal("5", ValueOf(new BinTreeOptional(H("maybe"), new BinTreeU32(0, 5))));
        Assert.Equal("[[1,2]]", ValueOf(new BinTreeOptional(H("maybe"), new BinTreeVector2(0, new Vector2(1, 2)))));
    }

    [Fact]
    public void AMapIsAnObjectKeyedByTheKeyRule()
    {
        var byString = new BinTreeMap(H("lookup"), BinPropertyType.String, BinPropertyType.U32, new[]
        {
            new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeString(0, "a"), new BinTreeU32(0, 1)),
            new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeString(0, "b"), new BinTreeU32(0, 2)),
        });
        Assert.Equal("""{"a":1,"b":2}""", ValueOf(byString));

        // a hash key is its name or "0x%08x" - a decimal string would be wrong here (the key rule reads hex)
        var byHash = new BinTreeMap(H("lookup"), BinPropertyType.Hash, BinPropertyType.U32, new[]
        {
            new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, 0x0badf00d), new BinTreeU32(0, 1)),
            new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, H("speed")), new BinTreeU32(0, 2)),
        });
        Assert.Equal("""{"0x0badf00d":1,"speed":2}""", ValueOf(byHash));

        // an integer key is its decimal text, and a file key is a path or "0x%016x"
        var byInt = new BinTreeMap(H("lookup"), BinPropertyType.U32, BinPropertyType.String, new[]
            { new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeU32(0, 7), new BinTreeString(0, "x")) });
        Assert.Equal("""{"7":"x"}""", ValueOf(byInt));
        var byFile = new BinTreeMap(H("lookup"), BinPropertyType.WadChunkLink, BinPropertyType.U32, new[]
            { new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeWadChunkLink(0, 0xff), new BinTreeU32(0, 1)) });
        Assert.Equal("""{"0x00000000000000ff":1}""", ValueOf(byFile));
    }

    [Fact]
    public void AnEmbedIsAOneKeyMappingNotATag()
    {
        var inner = new BinTreeEmbedded(H("inner"), H("InnerData"),
            new BinTreeProperty[] { new BinTreeF32(H("speed"), 2.5f), new BinTreeU32(H("count"), 3) });
        Assert.Equal("""{"embed":{"class":"InnerData","set":{"speed":2.5,"count":3}}}""", ValueOf(inner));
        // and with no fields: the set is left out
        Assert.Equal("""{"embed":{"class":"InnerData"}}""",
            ValueOf(new BinTreeEmbedded(H("inner"), H("InnerData"), Array.Empty<BinTreeProperty>())));
    }

    [Fact]
    public void APointerIsAOneKeyMappingAndTheNullPointerIsNull()
    {
        Assert.Equal("""{"pointer":{"class":"InnerData","set":{"count":3}}}""",
            ValueOf(new BinTreeStruct(H("inner"), H("InnerData"), new BinTreeProperty[] { new BinTreeU32(H("count"), 3) })));
        Assert.Equal("null", ValueOf(new BinTreeStruct(H("inner"), 0, Array.Empty<BinTreeProperty>())));
    }

    [Fact]
    public void StructsNestInsideListsAndStructs()
    {
        var leaf = new BinTreeEmbedded(0, H("InnerData"), new BinTreeProperty[] { new BinTreeU32(H("count"), 1) });
        var list = new BinTreeContainer(H("tags"), BinPropertyType.Embedded, new BinTreeProperty[] { leaf });
        Assert.Equal("""[{"embed":{"class":"InnerData","set":{"count":1}}}]""", ValueOf(list));

        var outer = new BinTreeEmbedded(H("inner"), H("InnerData"), new BinTreeProperty[]
            { new BinTreeStruct(H("lookup"), H("Thing"), new BinTreeProperty[] { new BinTreeU32(H("count"), 9) }) });
        Assert.Equal("""{"embed":{"class":"InnerData","set":{"lookup":{"pointer":{"class":"Thing","set":{"count":9}}}}}}""", ValueOf(outer));
    }

    /// <summary>A field of a struct that is the same class on both sides is its own dotted key, in JSON as in YAML.</summary>
    [Fact]
    public void AFieldInsideAStructOfTheSameClassIsADottedKey()
    {
        BinTreeObject With(float v) => Thing(new BinTreeEmbedded(H("inner"), H("InnerData"), new BinTreeProperty[]
            { new BinTreeF32(H("speed"), v), new BinTreeU32(H("count"), 3) }));
        var c = Convert(Bin(new[] { With(1f) }), Bin(new[] { With(2.5f) }));
        var edit = BinDeclarations.GameDataDocument(new[] { c })["modules"]![0]!["edits"]![0]!;
        Assert.Equal("""{"Maps/Test/Thing":{"inner.speed":2.5}}""", Json(edit));
    }

    // ===================================================== keys and names

    [Fact]
    public void AnUnknownFieldIsItsHexForm()
    {
        var doc = DocOf(new BinTreeU32(0x0badf00d, 2), new BinTreeU32(0xdeadbeef, 1));
        Assert.Contains("\"0x0badf00d\":2", doc);
        Assert.Contains("\"0xdeadbeef\":1", doc);   // the table's "liar" does not hash to it
    }

    [Fact]
    public void AnEntryAtTheBodyRootCarriesASlashOrIsHashForm()
    {
        var riot = Bin(new[] { new BinTreeObject(H("NoSlash"), H("Thing"), new BinTreeProperty[] { new BinTreeU32(H("count"), 1) }) });
        var mod = Bin(new[] { new BinTreeObject(H("NoSlash"), H("Thing"), new BinTreeProperty[] { new BinTreeU32(H("count"), 2) }) });
        var edit = BinDeclarations.GameDataDocument(new[] { Convert(riot, mod) })["modules"]![0]!["edits"]![0]!.AsObject();
        Assert.Equal($"0x{H("NoSlash"):x8}", Assert.Single(edit).Key);
    }

    [Fact]
    public void AnEntryOrFieldSpellingABindingKeywordIsWrittenAsItsHash()
    {
        var doc = DocOf(new BinTreeU32(H("links"), 5), new BinTreeU32(H("objects"), 6));
        Assert.Contains($"\"0x{H("links"):x8}\":5", doc);
        Assert.Contains($"\"0x{H("objects"):x8}\":6", doc);
        // an OBJECT called "objects" would swallow the binding of that name
        var o = new BinTreeObject(H("objects"), H("Thing"), Array.Empty<BinTreeProperty>());
        var added = BinDeclarations.GameDataDocument(new[] { Convert(Bin(new[] { Thing() }), Bin(new[] { Thing(), o })) });
        Assert.Contains($"\"0x{H("objects"):x8}\":{{\"class\":\"Thing\"}}", Json(added));
    }

    // ===================================================== what ships whole

    [Fact]
    public void ANonFiniteValueShipsTheBinWholeAndHasNoModule()
    {
        var c = Convert(Bin(new[] { Thing(new BinTreeF32(H("speed"), 1f)) }), Bin(new[] { Thing(new BinTreeF32(H("speed"), float.NaN)) }));
        Assert.False(c.Declared);
        Assert.Contains("non-finite", c.WhyNot);
        Assert.Empty(BinDeclarations.GameDataDocument(new[] { c })["modules"]!.AsArray());
    }

    /// <summary>Found by applying the export with league-mod's own engine: an <c>embed</c> pin over an embedded
    /// struct of ANOTHER class is a <c>PinMismatch</c>, so the edit was skipped and the field kept the game's
    /// value. The bin ships whole instead, with the reason.</summary>
    [Fact]
    public void AnEmbeddedStructWhoseClassChangedShipsTheBinWhole()
    {
        BinTreeObject With(string cls) => Thing(new BinTreeEmbedded(H("inner"), H(cls), new BinTreeProperty[] { new BinTreeU32(H("count"), 1) }));
        var c = Convert(Bin(new[] { With("InnerData") }), Bin(new[] { With("Thing") }));
        Assert.False(c.Declared);
        Assert.Contains("changes the class of the embedded struct", c.WhyNot);
        Assert.Contains("inner", c.WhyNot);
    }

    /// <summary>A pointer may change class (the pin names it and the schema must know it), and a struct inside a
    /// list or an option has no base to disagree with, so those are still set whole.</summary>
    [Fact]
    public void APointerMayChangeClassAndAListOfEmbedsIsSetWhole()
    {
        BinTreeObject Pointer(string cls) => Thing(new BinTreeStruct(H("ptr"), H(cls), new BinTreeProperty[] { new BinTreeU32(H("count"), 1) }));
        var p = Convert(Bin(new[] { Pointer("InnerData") }), Bin(new[] { Pointer("Thing") }));
        Assert.True(p.Declared, p.WhyNot);
        Assert.Contains("\"pointer\":{\"class\":\"Thing\"", Json(BinDeclarations.GameDataDocument(new[] { p })));

        BinTreeObject List(string cls) => Thing(new BinTreeContainer(H("tags"), BinPropertyType.Embedded,
            new BinTreeProperty[] { new BinTreeEmbedded(0, H(cls), new BinTreeProperty[] { new BinTreeU32(H("count"), 1) }) }));
        var l = Convert(Bin(new[] { List("InnerData") }), Bin(new[] { List("Thing") }));
        Assert.True(l.Declared, l.WhyNot);
    }

    [Fact]
    public void ARemovedPropertyStillShipsTheBinWhole()
    {
        var c = Convert(Bin(new[] { Thing(new BinTreeU32(H("count"), 3)) }), Bin(new[] { Thing() }));
        Assert.False(c.Declared);
        Assert.Contains("no declaration removes a property", c.WhyNot);
    }

    // ===================================================== two renderings of one model

    /// <summary>The YAML manifest and the JSON document are rendered from the same <see cref="DeclaredEdit"/>:
    /// every key, object and link the model holds appears in both, and nothing else does.</summary>
    [Fact]
    public void YamlAndJsonAreTwoRenderingsOfOneModel()
    {
        var added = new BinTreeObject(H("Mods/Test/New"), H("Thing"), new BinTreeProperty[]
            { new BinTreeF32(H("speed"), 2f), new BinTreeString(H("label"), "new") });
        var old = new BinTreeObject(H("Characters/Old"), H("Thing"), new BinTreeProperty[] { new BinTreeU32(H("count"), 1) });
        var riot = Bin(new[] { Thing(new BinTreeF32(H("speed"), 1f), new BinTreeU32(H("count"), 3)), old }, "DATA/A.bin");
        var mod = Bin(new[]
        {
            Thing(new BinTreeF32(H("speed"), 0.37f), new BinTreeU32(H("count"), 3), new BinTreeString(H("label"), "x")),
            added,
        }, "DATA/B.bin");

        var c = Convert(riot, mod);
        Assert.True(c.Declared, c.WhyNot);
        var model = Assert.IsType<DeclaredEdit>(c.Edit);
        string yaml = BinDeclarations.Manifest(new[] { c });
        string json = Json(BinDeclarations.GameDataDocument(new[] { c }));

        foreach (var body in model.Bodies)
        {
            Assert.Contains(body.Entry, yaml);
            Assert.Contains($"\"{body.Entry}\"", json);
            foreach (var edit in body.Edits)
            {
                Assert.Contains(edit.Key + ":", yaml);
                Assert.Contains($"\"{edit.Key}\"", json);
            }
        }
        foreach (var o in model.Objects)
        {
            Assert.Contains(o.Name, yaml);
            Assert.Contains($"\"{o.Name}\"", json);
        }
        foreach (var link in model.AddLinks.Concat(model.DropLinks))
        {
            Assert.Contains($"\"{link}\"", yaml);
            Assert.Contains($"\"{link}\"", json);
        }
        Assert.Equal(c.Properties, model.Bodies.Sum(b => b.Edits.Count));
        Assert.Equal(c.ObjectsAdded, model.Objects.Count(o => !o.Remove));
        Assert.Equal(c.ObjectsRemoved, model.Objects.Count(o => o.Remove));
    }

    /// <summary>M757's manifest text did not change when the diff began building a model: the YAML renderer
    /// reproduces the earlier text exactly.</summary>
    [Fact]
    public void TheYamlManifestIsUnchangedByTheModel()
    {
        var riot = Bin(new[] { Thing(new BinTreeF32(H("speed"), 1f), new BinTreeString(H("label"), "a")) });
        var mod = Bin(new[] { Thing(new BinTreeF32(H("speed"), 0.37f), new BinTreeString(H("label"), "a")) });
        Assert.Equal(
            "  - target: \"data/test.bin\"\n" +
            "    Maps/Test/Thing:\n" +
            "      speed: 0.37\n", Convert(riot, mod).Module);
    }
}
