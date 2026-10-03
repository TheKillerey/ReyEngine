using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReyEngine.Core.Build;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M816: a layer's GameData document as TEXT (<see cref="GameDataDocumentText"/>) - where its modules are, so that an import can
/// keep every one as the package spelled it, an export can write ReyEngine's own after them, and Send to LTK Manager can turn a
/// module into a manifest module without re-spelling a value.
/// </summary>
public sealed class GameDataDocumentTextTests
{
    private const string Doc =
        "{\n  \"version\": 1,\n  \"modules\": [\n    {\n      \"name\": \"Baron\",\n      \"target\": \"data/a.bin\",\n"
        + "      \"edits\": [\n        {\n          \"A/b\": {\n            \"speed\": 1E-30,\n            \"k\": \"caf\\u00e9\"\n          }\n        }\n      ],\n"
        + "      \"origin\": {\n        \"manifest\": \"game_data.yaml\",\n        \"source\": null,\n        \"module\": 0\n      }\n    },\n"
        + "    {\"entries\":{\"A/c\":{\"x\":2.50}},\"origin\":{\"manifest\":\"game_data.yaml\",\"source\":null,\"module\":1}}\n  ]\n}";

    // ===================================================== reading

    [Fact]
    public void EveryModuleIsFoundAsTheTextItWasWrittenIn()
    {
        var doc = GameDataDocumentText.Read(Doc);

        Assert.True(doc.IsExpectedShape);
        Assert.Equal("1", doc.VersionText);
        Assert.Equal(2, doc.Modules.Count);
        Assert.Equal(new[] { 0, 1 }, doc.Modules.Select(m => m.Index));
        Assert.Equal("Baron", doc.Modules[0].Name);
        Assert.Equal("data/a.bin", doc.Modules[0].Target);
        Assert.True(doc.Modules[0].IsTarget);
        Assert.Null(doc.Modules[1].Name);
        Assert.Null(doc.Modules[1].Target);
        Assert.False(doc.Modules[1].IsTarget);

        // a slice of the document, whitespace and spellings and all
        Assert.Contains(doc.Modules[0].Text, Doc);
        Assert.StartsWith("{\n      \"name\": \"Baron\"", doc.Modules[0].Text);
        Assert.EndsWith("\"module\": 0\n      }\n    }", doc.Modules[0].Text);
        Assert.Contains("1E-30", doc.Modules[0].Text);
        Assert.Contains("caf\\u00e9", doc.Modules[0].Text);
        Assert.Equal("{\"entries\":{\"A/c\":{\"x\":2.50}},\"origin\":{\"manifest\":\"game_data.yaml\",\"source\":null,\"module\":1}}", doc.Modules[1].Text);
        Assert.Equal(Doc, doc.Text);
    }

    [Fact]
    public void ModulesInsideStringsAndNestedObjectsAreNotMistakenForModules()
    {
        var doc = GameDataDocumentText.Read("{\"version\":1,\"modules\":[{\"target\":\"a\",\"edits\":[{\"modules\":[{\"x\":1}],\"s\":\"}{,]\"}]}]}");
        Assert.Single(doc.Modules);
        Assert.Equal("a", doc.Modules[0].Target);
    }

    [Fact]
    public void AnEmptyModuleListIsAnExpectedDocumentWithNoModules()
    {
        var doc = GameDataDocumentText.Read("{\"version\":1,\"modules\":[]}");
        Assert.True(doc.IsExpectedShape);
        Assert.Empty(doc.Modules);
    }

    [Theory]
    [InlineData("{\"version\":1,\"modules\":[],\"extra\":true}")]            // the crate refuses unknown keys
    [InlineData("{\"modules\":[]}")]                                         // no version
    [InlineData("{\"version\":1}")]                                          // no modules
    [InlineData("{\"version\":\"1\",\"modules\":[]}")]                       // a version that is not a number
    [InlineData("{\"version\":1,\"modules\":{}}")]                           // modules that is not a list
    [InlineData("{\"version\":1,\"modules\":[1]}")]                          // a module that is not an object
    [InlineData("{\"version\":1,\"version\":2,\"modules\":[]}")]             // a repeated key
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    public void ADocumentThatIsNotVersionAndModulesIsCarriedAndReportsNoModules(string text)
    {
        var doc = GameDataDocumentText.Read(text);
        Assert.False(doc.IsExpectedShape);
        Assert.Empty(doc.Modules);
        Assert.Equal(text, doc.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{\"version\":1,\"modules\":[}")]
    [InlineData("{\"version\":1,\"modules\":[]} x")]
    [InlineData("{\"version\":1,\"modules\":[]}{}")]
    public void TextThatIsNotJsonIsRefused(string text) =>
        Assert.ThrowsAny<JsonException>(() => GameDataDocumentText.Read(text));

    [Fact]
    public void NestingDeeperThanAPackageCanHoldIsReadAndTooDeepIsRefused()
    {
        // info.json nests a document three levels deep already; league-mod's reader stops at 128 for the whole file
        string Nest(int n) => "{\"version\":1,\"modules\":[{\"target\":\"a\",\"v\":" + new string('[', n) + new string(']', n) + "}]}";
        Assert.Single(GameDataDocumentText.Read(Nest(100)).Modules);
        Assert.ThrowsAny<JsonException>(() => GameDataDocumentText.Read(Nest(300)));
    }

    [Fact]
    public void TheOverrideFilesADocumentNamesAreListedInOrderEachOnce()
    {
        var doc = GameDataDocumentText.Read("""
            {"version":1,"modules":[
              {"target":"a","edits":[{"overrides":["data/x.ptch","data/y.ptch"]},{"overrides":["data/x.ptch"]},{"A/b":{"overrides":["not/a/path.ptch"]}}]},
              {"entries":{"A/c":{"x":1}}},
              {"target":"b","edits":[{"overrides":["z.ptch",5,null]},{"overrides":"data/lone.ptch"},{"links":[]}]},
              {"target":"c"}
            ]}
            """);

        Assert.Equal(new[] { "data/x.ptch", "data/y.ptch", "z.ptch" }, doc.OverridePaths());
        Assert.Empty(GameDataDocumentText.Read("{\"version\":1,\"modules\":[]}").OverridePaths());
        Assert.Empty(GameDataDocumentText.Read("[]").OverridePaths());
    }

    // ===================================================== writing

    private static string WriteAt(GameDataDocumentText doc, IReadOnlyList<JsonNode> own, bool wrap = true)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            if (wrap) { w.WriteStartObject(); w.WritePropertyName("GameData"); }
            doc.WriteTo(w, own);
            if (wrap) w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static JsonNode Own(int index) => JsonNode.Parse(
        "{\"target\":\"data/own.bin\",\"edits\":[{\"X/y\":{\"speed\":" + (index + 1) + "}}],\"origin\":{\"manifest\":\"game_data.yaml\",\"source\":null,\"module\":" + index + "}}")!;

    [Fact]
    public void WithNothingToAddTheTextGoesOutAsItIs()
    {
        var doc = GameDataDocumentText.Read(Doc);
        string written = WriteAt(doc, Array.Empty<JsonNode>(), wrap: false);
        Assert.Equal(Doc, written);

        // and inside another document, at whatever depth the writer is at: the text is not re-indented
        Assert.Equal("{\n  \"GameData\": " + Doc + "\n}", WriteAt(doc, Array.Empty<JsonNode>()));
    }

    [Fact]
    public void OwnModulesFollowTheImportedOnesWhichKeepTheirTextByteForByte()
    {
        var doc = GameDataDocumentText.Read(Doc);

        string written = WriteAt(doc, new[] { Own(2), Own(3) }, wrap: false);

        using var parsed = JsonDocument.Parse(written);
        var modules = parsed.RootElement.GetProperty("modules");
        Assert.Equal(4, modules.GetArrayLength());
        Assert.Equal(new[] { 0, 1, 2, 3 }, modules.EnumerateArray().Select(m => m.GetProperty("origin").GetProperty("module").GetInt32()));
        Assert.Equal(1, parsed.RootElement.GetProperty("version").GetInt32());

        // the imported modules are in the output as their own text, in order, before the new ones
        int a = written.IndexOf(doc.Modules[0].Text, StringComparison.Ordinal);
        int b = written.IndexOf(doc.Modules[1].Text, StringComparison.Ordinal);
        int own = written.IndexOf("data/own.bin", StringComparison.Ordinal);
        Assert.True(a >= 0 && b > a && own > b);
    }

    /// <summary>The property that matters for a file a person reads: a document a pretty printer wrote, with ReyEngine's modules
    /// added at the depth it sits at in info.json, reads exactly as the pretty printer would have written all of them.</summary>
    [Fact]
    public void ADocumentOfPrettyPrintedModulesWithOwnModulesAddedIsWhatThePrettyPrinterWouldWriteAtThatDepth()
    {
        var pretty = new JsonSerializerOptions { WriteIndented = true, NewLine = "\n", Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        // the source: the document as the pretty printer lays it out three levels down, which is where an info.json keeps it
        var sourceInfo = new JsonObject { ["Layers"] = new JsonObject { ["base"] = new JsonObject { ["GameData"] = JsonNode.Parse(Doc), ["Name"] = "base" } } };
        string source = FantomeImporterLayersTests.GameDataTextIn(Encoding.UTF8.GetBytes(sourceInfo.ToJsonString(pretty)), "base")!;
        var doc = GameDataDocumentText.Read(source);
        var own = new[] { Own(2), Own(3) };

        // info.json holds the document three levels down: Layers.base.GameData
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n", Encoder = pretty.Encoder }))
        {
            w.WriteStartObject();
            w.WriteStartObject("Layers");
            w.WriteStartObject("base");
            w.WritePropertyName("GameData");
            doc.WriteTo(w, own);
            w.WriteString("Name", "base");
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        string written = Encoding.UTF8.GetString(stream.ToArray());

        var combined = JsonNode.Parse(source)!.AsObject();
        var modules = combined["modules"]!.AsArray();
        foreach (var m in own) modules.Add(m.DeepClone());
        var logical = new JsonObject { ["Layers"] = new JsonObject { ["base"] = new JsonObject { ["GameData"] = combined, ["Name"] = "base" } } };

        Assert.Equal(logical.ToJsonString(pretty), written);
    }

    [Fact]
    public void ACompactDocumentWrittenByACompactWriterStaysCompact()
    {
        var doc = GameDataDocumentText.Read("{\"version\":1,\"modules\":[{\"target\":\"a\",\"edits\":[]}]}");
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
            doc.WriteTo(w, new[] { Own(1) });
        string written = Encoding.UTF8.GetString(stream.ToArray());
        Assert.DoesNotContain("\n", written);
        Assert.StartsWith("{\"version\":1,\"modules\":[{\"target\":\"a\",\"edits\":[]},{\"target\":\"data/own.bin\"", written);
        using var parsed = JsonDocument.Parse(written);
        Assert.Equal(2, parsed.RootElement.GetProperty("modules").GetArrayLength());
    }

    [Fact]
    public void ADocumentOfAnotherShapeGetsTheNewModulesThroughItsParsedForm()
    {
        var doc = GameDataDocumentText.Read("{\"version\":1,\"modules\":[{\"target\":\"a\",\"edits\":[{\"X/y\":{\"n\":1.0}}]}],\"mystery\":true}");
        Assert.False(doc.IsExpectedShape);

        using var parsed = JsonDocument.Parse(WriteAt(doc, new[] { Own(1) }, wrap: false));

        Assert.Equal(2, parsed.RootElement.GetProperty("modules").GetArrayLength());
        Assert.True(parsed.RootElement.GetProperty("mystery").GetBoolean());                         // nothing the document held is dropped
        Assert.Contains("1.0", parsed.RootElement.GetProperty("modules")[0].GetRawText());          // and a number keeps its token
    }

    [Fact]
    public void WritingDoesNotChangeTheNodesItIsGiven()
    {
        var doc = GameDataDocumentText.Read(Doc);
        var own = Own(2);
        string before = own.ToJsonString();
        WriteAt(doc, new[] { own });
        WriteAt(doc, new[] { own });
        Assert.Equal(before, own.ToJsonString());
    }

    // ===================================================== a manifest module

    private static string Squashed(string json) => JsonNode.Parse(json)!.ToJsonString();

    [Theory]
    [InlineData("{\"target\":\"a\",\"edits\":[],\"origin\":{\"manifest\":\"game_data.yaml\",\"source\":null,\"module\":0}}", "{\"target\":\"a\",\"edits\":[]}")]
    [InlineData("{\"origin\":{\"module\":0},\"target\":\"a\",\"edits\":[]}", "{\"target\":\"a\",\"edits\":[]}")]
    [InlineData("{\"target\":\"a\",\"origin\":{\"module\":0},\"edits\":[]}", "{\"target\":\"a\",\"edits\":[]}")]
    [InlineData("{\"origin\":{\"module\":0}}", "{}")]
    [InlineData("{\"a\":1}", "{\"a\":1}")]
    [InlineData("{}", "{}")]
    public void TheOriginMemberLeavesWhereverItIs(string module, string expected) =>
        Assert.Equal(expected, GameDataDocumentText.ManifestModule(module));

    [Fact]
    public void EveryOtherTokenIsCopiedAsWrittenAndOnlyTheWhitespaceBetweenThemGoes()
    {
        string module = "{\r\n\t\"name\" : \"caf\\u00e9 \\/ \\\"q\\\" \\ud83d\\ude00 é \U0001F600\",\r\n\t\"target\": \"data/a.bin\",\r\n"
            + "\t\"edits\": [ { \"A/b\": { \"speed\": 1E-30, \"z\": -0.0, \"one\": 1.0, \"big\": 123456789012345678901234567890, \"t\": true, \"f\": false, \"n\": null, \"e\": {}, \"l\": [ ], \"m\": [ [1,2] , {\"k\":[]} ] } } ],\r\n"
            + "\t\"origin\": { \"manifest\": \"game_data.yaml\", \"source\": null, \"module\": 0 }\r\n}";

        string text = GameDataDocumentText.ManifestModule(module);

        Assert.DoesNotContain("origin", text);
        Assert.DoesNotContain("\r", text);
        Assert.DoesNotContain("\t", text);
        Assert.DoesNotContain(": ", text);                                    // no space after a colon: the whitespace between tokens is gone
        // escapes as written, raw characters as written - except the pair of surrogate escapes, which is the character it spells: a YAML
        // double-quoted string cannot take the pair (review)
        Assert.Contains("\"caf\\u00e9 \\/ \\\"q\\\" \U0001F600 é \U0001F600\"", text);
        Assert.DoesNotContain("ud83d", text);
        Assert.Contains("1E-30", text);
        Assert.Contains("-0.0", text);
        Assert.Contains("1.0", text);
        Assert.Contains("123456789012345678901234567890", text);
        Assert.Contains("\"t\":true,\"f\":false,\"n\":null,\"e\":{},\"l\":[],\"m\":[[1,2],{\"k\":[]}]", text);

        // and it is the same JSON: the module with its origin taken out
        var want = JsonNode.Parse(module)!.AsObject();
        want.Remove("origin");
        Assert.True(JsonNode.DeepEquals(want, JsonNode.Parse(text)));
    }

    /// <summary>Review: JSON spells a character outside the Basic Multilingual Plane as a PAIR of surrogate escapes, and a YAML double-quoted
    /// string reads each <c>\u</c> escape as a character of its own - a surrogate is not one - so a pair copied into a manifest is a line a YAML
    /// reader refuses. The manifest holds the character the pair spells; every other escape is as written.</summary>
    [Theory]
    [InlineData("\\ud83d\\ude00", "\U0001F600")]                                        // a pair is the character
    [InlineData("\\uD83D\\uDE00", "\U0001F600")]                                        // in either casing of the hex digits
    [InlineData("x\\ud83d\\ude00y\\ud83c\\udf0dz", "x\U0001F600y\U0001F30Dz")]          // several, among other text
    [InlineData("\\ud83d\\ude00\\ud83d\\ude00", "\U0001F600\U0001F600")]                // one after the other
    [InlineData("\\\\ud83d\\\\ude00", "\\\\ud83d\\\\ude00")]                            // an escaped backslash and letters are text, not an escape
    [InlineData("\\\\\\ud83d\\ude00", "\\\\\U0001F600")]                                // an escaped backslash, then a real pair
    [InlineData("\\u00e9\\u0041\\n\\t\\/\\\"\\\\", "\\u00e9\\u0041\\n\\t\\/\\\"\\\\")]    // every other escape stays as the package wrote it
    [InlineData("\U0001F600 é", "\U0001F600 é")]                                        // a character written raw stays raw
    public void ASurrogatePairEscapeBecomesTheCharacterInAManifestAndEveryOtherEscapeIsAsWritten(string inner, string expected)
    {
        string module = "{\"k" + inner + "\":\"v" + inner + "\",\"n\":{\"deep\":[\"" + inner + "\"]}}";

        string text = GameDataDocumentText.ManifestModule(module);

        Assert.Equal("{\"k" + expected + "\":\"v" + expected + "\",\"n\":{\"deep\":[\"" + expected + "\"]}}", text);
        // and it says the same thing: the pair and the character are one string to a JSON reader
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(module), JsonNode.Parse(text)));
    }

    [Theory]
    [InlineData("\\ud83d")]
    [InlineData("\\ude00")]
    [InlineData("\\ude00\\ud83d")]
    [InlineData("\\ud83dx\\ude00")]
    [InlineData("\\ud83d\\u0041")]
    public void ASurrogateEscapeThatIsNotHalfOfAPairIsNotACharacterAndIsNotInventedOne(string inner)
    {
        // serde_json refuses a lone surrogate, and so does the reader here: a document that holds one is not a document a package can
        // carry, so a manifest never has to write one. Whatever the outcome, no character is made up for it.
        try
        {
            string text = GameDataDocumentText.ManifestModule("{\"k\":\"" + inner + "\"}");
            Assert.Equal("{\"k\":\"" + inner + "\"}", text);
        }
        catch (JsonException) { }
    }

    [Fact]
    public void ManifestModulesOfARealisticDocumentEqualTheModulesWithoutTheirOrigins()
    {
        var doc = GameDataDocumentText.Read(Doc);
        foreach (var module in doc.Modules)
        {
            var want = JsonNode.Parse(module.Text)!.AsObject();
            want.Remove("origin");
            var got = JsonNode.Parse(GameDataDocumentText.ManifestModule(module.Text));
            Assert.True(JsonNode.DeepEquals(want, got));
            Assert.DoesNotContain('\n', GameDataDocumentText.ManifestModule(module.Text));
        }
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"x\"")]
    [InlineData("{")]
    [InlineData("{\"a\":1} x")]
    public void AManifestModuleIsAJsonObject(string text) =>
        Assert.ThrowsAny<JsonException>(() => GameDataDocumentText.ManifestModule(text));
}
