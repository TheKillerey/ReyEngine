using System.Text;
using System.Text.Json.Nodes;
using ReyEngine.Formats.LtkGameData;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M817: <see cref="LtkMetaSchema"/>, the port of LTK Manager's meta schema database and its <c>PatchSchema</c>, against LTK Manager's own tests
/// (<c>meta_schema/tests.rs</c>, v1.21.0): the same database text and the same questions, so what is expected is what the manager's code
/// answers, not what a reader of this port would expect. The whole engine's use of the schema is pinned separately, case by case, by
/// <see cref="LtkGameDataOracleTests"/> (rule 11).
/// </summary>
public class LtkMetaSchemaTests
{
    /// <summary>The build <c>FloatTextIconData.mIconFileName</c> was a <c>String</c> at (16.16), and the one it became a <c>File</c> at (16.17).</summary>
    private const uint BeforeRetype = 8_049_184, AfterRetype = 8_104_348;

    private const uint FloatTextIconData = 0x16d88f43, MIconFileName = 0x10537b0c, MOffset = 0x26dbcd4b, IconCircle = 0xe67284f4;
    private const uint UncensoredIconCircles = 0x8ce04c3d, MValues = 0x0a1b2c3d, MHoldsSomethingNew = 0x0badf00d, Unmappable = 0xdeadbeef;
    private const uint Derived = 0x00000abc;

    private const string Published = """
        {
          "formatVersion": 1,
          "hashSource": { "fetchedAt": "2026-08-24T03:56:00Z" },
          "latest": 8104348,
          "versions": [
            { "patch": "16.16", "build": 8049184 },
            { "patch": "16.17", "build": 8104348 }
          ],
          "classes": {
            "0x16d88f43": {
              "name": "FloatTextIconData",
              "properties": {
                "0x10537b0c": {
                  "name": "mIconFileName",
                  "revisions": [
                    { "from": 5229820, "to": 8049184, "type": ["String", "0x0", "0x0", "0x0"] },
                    { "from": 8104348, "type": ["File", "0x0", "0x0", "0x0"] }
                  ]
                },
                "0x26dbcd4b": {
                  "name": "mOffset",
                  "revisions": [
                    { "from": 5229820, "type": ["Vec2", "0x0", "0x0", "0x0"] }
                  ]
                },
                "0xdeadbeef": {
                  "name": "mUnnameable",
                  "revisions": [
                    { "from": 5229820, "type": ["SomethingNew", "0x0", "0x0", "0x0"] }
                  ]
                },
                "0xe67284f4": {
                  "name": "iconCircle",
                  "revisions": [
                    { "from": 5229820, "to": 8049184, "type": ["Option", "0x0", "String", "0x0"] },
                    { "from": 8104348, "type": ["Option", "0x0", "File", "0x0"] }
                  ]
                },
                "0x8ce04c3d": {
                  "name": "uncensoredIconCircles",
                  "revisions": [
                    { "from": 5229820, "type": ["Map", "Hash", "File", "0x0"] }
                  ]
                },
                "0x0a1b2c3d": {
                  "name": "mValues",
                  "revisions": [
                    { "from": 5229820, "type": ["List", "0x7", "F32", "0x0"] }
                  ]
                },
                "0x0badf00d": {
                  "name": "mHoldsSomethingNew",
                  "revisions": [
                    { "from": 5229820, "type": ["Option", "0x0", "SomethingNew", "0x0"] }
                  ]
                }
              }
            }
          }
        }
        """;

    private static LtkMetaSchema Parse(string json) => LtkMetaSchema.Parse(Encoding.UTF8.GetBytes(json));

    private static LtkMetaSchema Schema() => Parse(Published);

    /// <summary>The database with <paramref name="classes"/>, a comma-separated run of class entries, ahead of its own.</summary>
    private static LtkMetaSchema SchemaWith(string classes) => Parse(Published.Replace("\"classes\": {", "\"classes\": { " + classes + ","));

    private static string ClassEntry(string hash, uint from, uint? to, string[] bases, string properties)
    {
        string end = to is { } t ? $", \"to\": {t}" : "";
        string baseList = string.Join(", ", bases.Select(b => $"\"{b}\""));
        return $$"""
            "{{hash}}": {
              "name": "Class{{hash}}",
              "revisions": [{ "from": {{from}}{{end}}, "bases": [{{baseList}}], "interface": false, "value": false }],
              "properties": { {{properties}} }
            }
            """;
    }

    private static LtkMetaSchema DerivedSchema() => SchemaWith(ClassEntry("0x00000abc", 1, null, new[] { "0x16d88f43" }, ""));

    private static GameDataShape? Shape(PropKind kind, PropKind? key = null, PropKind? item = null) => new GameDataShape(kind, key, item);

    [Fact]
    public void A_patch_schema_types_an_edit_by_the_revision_at_its_build()
    {
        var schema = Schema();
        Assert.Equal(Shape(PropKind.Optional, null, PropKind.String), schema.At(BeforeRetype).Expected(FloatTextIconData, IconCircle));
        Assert.Equal(Shape(PropKind.Optional, null, PropKind.WadChunkLink), schema.At(AfterRetype).Expected(FloatTextIconData, IconCircle));
        Assert.Equal(Shape(PropKind.Map, PropKind.Hash, PropKind.WadChunkLink), schema.At(AfterRetype).Expected(FloatTextIconData, UncensoredIconCircles));
        Assert.Equal(Shape(PropKind.String), schema.At(BeforeRetype).Expected(FloatTextIconData, MIconFileName));
        Assert.Equal(Shape(PropKind.WadChunkLink), schema.At(AfterRetype).Expected(FloatTextIconData, MIconFileName));
    }

    [Fact]
    public void A_patch_schema_says_nothing_where_the_database_is_silent()
    {
        var schema = Schema();
        var past = schema.At(9_000_000u);         // a game newer than the database
        var unbuilt = schema.At((uint?)null);     // an install that did not say
        var at = schema.At(AfterRetype);

        Assert.Null(past.Expected(FloatTextIconData, MOffset));
        Assert.Null(unbuilt.Expected(FloatTextIconData, MOffset));
        Assert.Null(at.Expected(FloatTextIconData, Unmappable));   // a type this reader cannot map
        Assert.Null(at.Expected(FloatTextIconData, 0x1));
        Assert.Null(at.Expected(0x1, MOffset));
    }

    [Fact]
    public void A_patch_schema_types_a_field_a_base_declares()
    {
        var schema = DerivedSchema();
        Assert.Equal(Shape(PropKind.Optional, null, PropKind.WadChunkLink), schema.At(AfterRetype).Expected(Derived, IconCircle));
        Assert.Null(schema.At(9_000_000u).Expected(Derived, IconCircle));
    }

    [Fact]
    public void A_patch_schema_knows_every_class_the_database_holds_named_or_not()
    {
        var schema = Parse(Published.Replace("\"name\": \"FloatTextIconData\",", "")).At(AfterRetype);
        Assert.True(schema.HasClass(FloatTextIconData));
        Assert.False(schema.HasClass(0x1));
    }

    [Fact]
    public void A_patch_schema_knows_every_class_at_a_build_the_database_does_not_describe()
    {
        // a class new on patch day is one the database has not taken yet; refusing a pin to it would be a refusal with no ground
        var schema = Schema();
        Assert.True(schema.At(9_000_000u).HasClass(0x1));
        Assert.True(schema.At((uint?)null).HasClass(0x1));
    }

    [Fact]
    public void A_nearer_class_hides_the_field_on_its_base()
    {
        var schema = SchemaWith(ClassEntry("0x00000abc", 1, null, new[] { "0x16d88f43" },
            "\"0x10537b0c\": { \"name\": \"mIconFileName\", \"revisions\": [{ \"from\": 1, \"type\": [\"String\", \"0x0\", \"0x0\", \"0x0\"] }] }"));
        Assert.Equal(Shape(PropKind.String), schema.At(AfterRetype).Expected(Derived, MIconFileName));
    }

    [Fact]
    public void A_base_the_class_takes_after_a_build_answers_nothing_at_it()
    {
        var schema = SchemaWith(ClassEntry("0x00000abc", 8_104_348, null, new[] { "0x16d88f43" }, ""));
        Assert.Null(schema.At(BeforeRetype).Expected(Derived, MOffset));
        Assert.Equal(Shape(PropKind.Vector2), schema.At(AfterRetype).Expected(Derived, MOffset));
    }

    [Fact]
    public void A_cycle_in_the_bases_ends_the_walk()
    {
        var schema = SchemaWith(ClassEntry("0x00000abc", 1, null, new[] { "0x00000abd" }, "") + ", " + ClassEntry("0x00000abd", 1, null, new[] { "0x00000abc" }, ""));
        Assert.Null(schema.At(AfterRetype).Expected(Derived, MOffset));
    }

    [Fact]
    public void A_complex_type_answers_with_its_subtypes()
    {
        // iconCircle is an Option before and after Riot retyped what it holds, so the kind alone cannot tell the builds apart
        var schema = Schema();
        Assert.True(schema.TryExpected(FloatTextIconData, IconCircle, BeforeRetype, out var before));
        Assert.True(schema.TryExpected(FloatTextIconData, IconCircle, AfterRetype, out var after));
        Assert.Equal(Shape(PropKind.Optional, null, PropKind.String), before);
        Assert.Equal(Shape(PropKind.Optional, null, PropKind.WadChunkLink), after);
    }

    [Fact]
    public void A_fixed_size_list_answers_its_item_kind_and_no_key()
    {
        // a list writes its fixed size where a map writes its key kind, and a count is not a type name to refuse the revision over
        Assert.True(Schema().TryExpected(FloatTextIconData, MValues, AfterRetype, out var shape));
        Assert.Equal(Shape(PropKind.Container, null, PropKind.F32), shape);
    }

    [Fact]
    public void An_unmappable_type_name_or_subtype_answers_without_a_type_but_the_revision_is_found()
    {
        var schema = Schema();
        Assert.True(schema.TryExpected(FloatTextIconData, Unmappable, AfterRetype, out var wrapper));
        Assert.Null(wrapper);
        Assert.True(schema.TryExpected(FloatTextIconData, MHoldsSomethingNew, AfterRetype, out var subtype));
        Assert.Null(subtype);
    }

    [Fact]
    public void The_end_of_a_revision_is_inclusive()
    {
        // 8049184 is the last build of the String revision, not the first of the File one
        Assert.True(Schema().TryExpected(FloatTextIconData, MIconFileName, BeforeRetype, out var shape));
        Assert.Equal(Shape(PropKind.String), shape);
    }

    [Fact]
    public void A_property_with_one_revision_answers_everywhere()
    {
        foreach (uint build in new[] { BeforeRetype, AfterRetype })
        {
            Assert.True(Schema().TryExpected(FloatTextIconData, MOffset, build, out var shape));
            Assert.Equal(Shape(PropKind.Vector2), shape);
        }
    }

    [Fact]
    public void What_the_database_does_not_describe_is_silence()
    {
        var schema = Schema();
        Assert.False(schema.TryExpected(0x1, MIconFileName, AfterRetype, out _));                       // a class it does not hold
        Assert.False(schema.TryExpected(FloatTextIconData, 0x1, AfterRetype, out _));                   // a property it does not hold
        Assert.False(schema.TryExpected(FloatTextIconData, MIconFileName, 5_000_000, out _));           // a build older than every revision
    }

    [Fact]
    public void A_build_past_the_database_is_one_it_does_not_describe()
    {
        var schema = Schema();
        Assert.True(schema.Describes(AfterRetype));
        Assert.True(schema.Describes(BeforeRetype));
        Assert.False(schema.Describes(8_200_000));
    }

    [Fact]
    public void A_database_at_another_layout_is_refused()
        => Assert.Throws<FormatException>(() => Parse(Published.Replace("\"formatVersion\": 1", "\"formatVersion\": 2")));

    [Fact]
    public void Bytes_that_are_not_the_database_are_refused()
        => Assert.Throws<FormatException>(() => Parse("not json"));

    [Theory]
    [InlineData("{\"formatVersion\":1,\"latest\":1,\"classes\":{\"0x1\":5}}")]                                                       // a class that is a number
    [InlineData("{\"formatVersion\":1,\"latest\":1,\"classes\":{\"0x1\":[]}}")]                                                      // a class that is an array
    [InlineData("{\"formatVersion\":1,\"latest\":1,\"classes\":{\"0x1\":{\"revisions\":[5]}}}")]                                      // a class revision that is a number
    [InlineData("{\"formatVersion\":1,\"latest\":1,\"classes\":{\"0x1\":{\"properties\":{\"0x2\":\"x\"}}}}")]                          // a property that is a string
    [InlineData("{\"formatVersion\":1,\"latest\":1,\"classes\":{\"0x1\":{\"properties\":{\"0x2\":{\"revisions\":[null]}}}}}")]          // a property revision that is null
    [InlineData("{\"formatVersion\":1,\"latest\":1,\"classes\":{\"0x1\":{\"properties\":{\"0x2\":{\"revisions\":[{\"from\":\"1\"}]}}}}}")]  // a build that is a string
    public void A_database_whose_json_has_the_wrong_shape_is_a_format_exception(string json)
        => Assert.Throws<FormatException>(() => Parse(json));

    /// <summary>The kinds of JSON value put, one at a time, in every place of a database.</summary>
    private static readonly Func<JsonNode?>[] Confusions =
    {
        () => null,
        () => JsonValue.Create(true),
        () => JsonValue.Create(0),
        () => JsonValue.Create(-1),
        () => JsonValue.Create(4_294_967_296L),
        () => JsonValue.Create(1.5),
        () => JsonValue.Create("x"),
        () => JsonValue.Create("0x1"),
        () => new JsonArray(),
        () => new JsonArray(JsonValue.Create(1)),
        () => new JsonObject(),
        () => new JsonObject { ["a"] = JsonValue.Create(1) },
    };

    private static IEnumerable<string[]> Places(JsonNode? node, string[] path)
    {
        yield return path;
        if (node is JsonObject obj)
        {
            foreach (var entry in obj)
                foreach (var place in Places(entry.Value, path.Append(entry.Key).ToArray())) yield return place;
        }
        else if (node is JsonArray array)
        {
            for (int i = 0; i < array.Count; i++)
                foreach (var place in Places(array[i], path.Append(i.ToString()).ToArray())) yield return place;
        }
    }

    [Fact]
    public void JSON_of_the_wrong_shape_in_any_place_is_refused_with_the_one_exception_and_nothing_else()
    {
        // every place of the published database takes every kind of value in turn: what comes of it is a database or a FormatException, whatever the place
        var failures = new List<string>();
        int refused = 0, read = 0;
        foreach (var place in Places(JsonNode.Parse(Published), Array.Empty<string>()).ToList())
        {
            foreach (var confusion in Confusions)
            {
                JsonNode? root = JsonNode.Parse(Published);
                JsonNode? confused = confusion();
                if (place.Length == 0) root = confused;
                else
                {
                    JsonNode? parent = root;
                    for (int i = 0; i < place.Length - 1; i++) parent = parent is JsonArray inner ? inner[int.Parse(place[i])] : parent![place[i]];
                    if (parent is JsonArray holder) holder[int.Parse(place[^1])] = confused;
                    else ((JsonObject)parent!)[place[^1]] = confused;
                }

                string text = root?.ToJsonString() ?? "null";
                try
                {
                    LtkMetaSchema.Parse(Encoding.UTF8.GetBytes(text));
                    read++;
                }
                catch (FormatException) { refused++; }
                catch (Exception e) { failures.Add($"{string.Join("/", place)} = {confused?.ToJsonString() ?? "null"}: {e.GetType().Name}: {e.Message}"); }
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(10)));
        Assert.True(refused > 100 && read > 100, $"{refused} refused, {read} read: the sweep did not reach both outcomes");
    }
}
