using System.IO;
using System.Linq;
using ReyEngine.Core.Meta;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M367: the meta-class database reader. Fixtures are synthetic and inline - the real dump is ~3.6 MB,
/// downloaded on demand and gitignored, so a test that needed it would fail on any clean checkout and in
/// CI. What is asserted here is the SCHEMA CONTRACT documented in docs/meta-db-format.md: hex hashes,
/// build-ranged revisions, the 4-tuple type, and inheritance through bases.
/// </summary>
public class MetaClassDatabaseTests
{
    private const string Fixture = """
    {
      "formatVersion": 1,
      "latest": 200,
      "versions": [ { "patch": "14.1", "build": 100 }, { "patch": "14.2", "build": 200 } ],
      "externalTypeNames": { "0x0000beef": "SomeExternalType" },
      "classes": {
        "0x00000001": {
          "name": "BaseThing",
          "revisions": [ { "from": 100, "bases": [], "interface": false, "value": false } ],
          "properties": {
            "0x000000aa": {
              "name": "inheritedField",
              "revisions": [ { "from": 100, "type": ["f32", "", "", ""], "default": 1.5 } ]
            }
          }
        },
        "0x00000002": {
          "name": "DerivedThing",
          "revisions": [ { "from": 100, "bases": ["0x00000001"], "interface": false, "value": false } ],
          "properties": {
            "0x000000bb": {
              "name": "ownField",
              "revisions": [ { "from": 100, "type": ["string", "", "", ""], "default": "hi" } ]
            },
            "0x000000cc": {
              "name": "structField",
              "revisions": [ { "from": 100, "type": ["struct", "", "", "0x00000001"] } ]
            },
            "0x000000dd": {
              "name": "removedField",
              "revisions": [ { "from": 100, "to": 100, "type": ["u32", "", "", ""], "default": 7 } ]
            },
            "0x000000ee": {
              "name": "retypedField",
              "revisions": [
                { "from": 100, "to": 100, "type": ["u32", "", "", ""] },
                { "from": 200, "type": ["f32", "", "", ""] }
              ]
            }
          }
        },
        "0x00000003": {
          "revisions": [ { "from": 100, "bases": [], "interface": false, "value": false } ],
          "properties": {}
        }
      }
    }
    """;

    /// <summary>The build ranges of the real dump, in miniature: 'to' is the LAST build a revision describes, so
    /// one that stops before build 200 reads "to": 100, and a revision of one build has from == to.</summary>
    private const string RangeFixture = """
    {
      "formatVersion": 1,
      "latest": 300,
      "versions": [ { "patch": "1", "build": 100 }, { "patch": "2", "build": 200 }, { "patch": "3", "build": 300 } ],
      "classes": {
        "0x00000010": {
          "name": "Ranged",
          "revisions": [ { "from": 100, "bases": [], "interface": false, "value": false } ],
          "properties": {
            "0x00000001": { "name": "toIsInclusive", "revisions": [ { "from": 100, "to": 200, "type": ["u32", "", "", ""] } ] },
            "0x00000002": { "name": "oneBuildOnly", "revisions": [ { "from": 200, "to": 200, "type": ["u32", "", "", ""] } ] },
            "0x00000003": { "name": "stillCurrent", "revisions": [ { "from": 200, "type": ["string", "", "", ""] } ] },
            "0x00000004": {
              "name": "overlapping",
              "revisions": [
                { "from": 100, "to": 300, "type": ["u32", "", "", ""] },
                { "from": 150, "type": ["f32", "", "", ""] }
              ]
            }
          }
        },
        "0x00000011": {
          "name": "RetiredClass",
          "revisions": [ { "from": 100, "to": 200, "bases": [], "interface": false, "value": false } ],
          "properties": {}
        },
        "0x00000012": {
          "name": "OneBuildClass",
          "revisions": [ { "from": 200, "to": 200, "bases": [], "interface": false, "value": false } ],
          "properties": {}
        }
      }
    }
    """;

    private static MetaClassDatabase Load(int? build = null) => LoadJson(Fixture, build);

    private static MetaClassDatabase LoadJson(string json, int? build)
    {
        string path = Path.Combine(Path.GetTempPath(), $"rey_meta_{System.Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        try { return MetaClassDatabase.Load(path, build); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadsVersionsAndLatest()
    {
        var db = Load();
        Assert.Equal(200, db.Latest);
        Assert.Equal(200, db.ResolvedBuild);
        Assert.Equal(2, db.Versions.Count);
        Assert.Equal(("14.2", 200), db.Versions[^1]);
    }

    [Fact]
    public void ResolvesClassAndPropertyNames()
    {
        var db = Load();
        Assert.True(db.TryGetName(0x00000002, out var cls));
        Assert.Equal("DerivedThing", cls);
        Assert.True(db.TryGetName(0x000000bb, out var prop));
        Assert.Equal("ownField", prop);
        Assert.True(db.TryGetName(0x0000beef, out var ext));
        Assert.Equal("SomeExternalType", ext);
    }

    [Fact]
    public void PropertiesOfIncludesInheritedFields()
    {
        var db = Load();
        var names = db.PropertiesOf(0x00000002).Select(p => p.Name).ToList();
        Assert.Contains("ownField", names);
        Assert.Contains("inheritedField", names);   // from BaseThing, via bases
    }

    [Fact]
    public void TryGetPropertyWalksBaseClasses()
    {
        var db = Load();
        Assert.True(db.TryGetProperty(0x00000002, 0x000000aa, out var inherited));
        Assert.Equal("inheritedField", inherited.Name);
        Assert.Equal("f32", inherited.FieldType);
    }

    [Fact]
    public void CarriesAuthoredDefaultsAsRawJson()
    {
        var db = Load();
        Assert.True(db.TryGetProperty(0x00000002, 0x000000bb, out var p));
        Assert.Equal("\"hi\"", p.Default);           // raw JSON, so a string keeps its quotes
        Assert.True(db.TryGetProperty(0x00000001, 0x000000aa, out var f));
        Assert.Equal("1.5", f.Default);
    }

    [Fact]
    public void PropertyWithNoDefaultReportsNull()
    {
        var db = Load();
        Assert.True(db.TryGetProperty(0x00000002, 0x000000cc, out var p));
        Assert.Null(p.Default);
    }

    [Fact]
    public void StructPropertyExposesReferencedClass()
    {
        var db = Load();
        Assert.True(db.TryGetProperty(0x00000002, 0x000000cc, out var p));
        Assert.True(p.TryGetReferencedClass(out uint referenced));
        Assert.Equal(0x00000001u, referenced);
    }

    [Fact]
    public void RemovedPropertyIsAbsentAtLaterBuild()
    {
        // 'to' is INCLUSIVE, the last build the revision describes: the field read "to": 100 because the patch at
        // 200 no longer has it. Getting this wrong either way shows users fields their patch does not have, or
        // hides fields it does.
        Assert.True(Load(build: 100).TryGetProperty(0x00000002, 0x000000dd, out _));
        Assert.False(Load(build: 200).TryGetProperty(0x00000002, 0x000000dd, out _));
    }

    [Fact]
    public void RevisionToIsInclusive()
    {
        var at200 = LoadJson(RangeFixture, 200);
        var at201 = LoadJson(RangeFixture, 201);
        Assert.False(LoadJson(RangeFixture, 99).TryGetProperty(0x00000010, 0x00000001, out _));
        Assert.True(LoadJson(RangeFixture, 100).TryGetProperty(0x00000010, 0x00000001, out _));
        Assert.True(at200.TryGetProperty(0x00000010, 0x00000001, out var last));   // the build the range ends on
        Assert.Equal("u32", last.FieldType);
        Assert.False(at201.TryGetProperty(0x00000010, 0x00000001, out _));
    }

    [Fact]
    public void ClassRevisionToIsInclusive()
    {
        Assert.True(LoadJson(RangeFixture, 200).TryGetClass(0x00000011, out _));
        Assert.False(LoadJson(RangeFixture, 201).TryGetClass(0x00000011, out _));
        Assert.False(LoadJson(RangeFixture, 99).TryGetClass(0x00000011, out _));
    }

    [Fact]
    public void RevisionOfOneBuildCoversExactlyThatBuild()
    {
        // The real dump holds 785 property and 234 class revisions with from == to; read as 'to' exclusive they
        // are empty ranges and never match.
        foreach (int build in new[] { 100, 199, 201, 300 })
        {
            Assert.False(LoadJson(RangeFixture, build).TryGetProperty(0x00000010, 0x00000002, out _), $"property at {build}");
            Assert.False(LoadJson(RangeFixture, build).TryGetClass(0x00000012, out _), $"class at {build}");
        }
        var db = LoadJson(RangeFixture, 200);
        Assert.True(db.TryGetProperty(0x00000010, 0x00000002, out _));
        Assert.True(db.TryGetClass(0x00000012, out _));
    }

    [Fact]
    public void RevisionWithoutToStaysCurrent()
    {
        foreach (int build in new[] { 200, 300, 9999999 })
            Assert.True(LoadJson(RangeFixture, build).TryGetProperty(0x00000010, 0x00000003, out var p) && p.FieldType == "string", $"build {build}");
        Assert.False(LoadJson(RangeFixture, 199).TryGetProperty(0x00000010, 0x00000003, out _));
    }

    [Fact]
    public void FirstCoveringRevisionInFileOrderWins()
    {
        // The dump never overlaps its own revisions (checked across all 255 builds), so this pins how a
        // malformed file reads: the first revision in the file that covers the build, not the one with the
        // highest 'from'. LTK Manager reads it the same way.
        Assert.True(LoadJson(RangeFixture, 120).TryGetProperty(0x00000010, 0x00000004, out var early));
        Assert.Equal("u32", early.FieldType);
        Assert.True(LoadJson(RangeFixture, 160).TryGetProperty(0x00000010, 0x00000004, out var overlap));
        Assert.Equal("u32", overlap.FieldType);   // covered by both; the first one is u32
        Assert.True(LoadJson(RangeFixture, 400).TryGetProperty(0x00000010, 0x00000004, out var late));
        Assert.Equal("f32", late.FieldType);      // only the open-ended second revision reaches it
    }

    [Fact]
    public void RetypedPropertyResolvesPerBuild()
    {
        Assert.True(Load(build: 100).TryGetProperty(0x00000002, 0x000000ee, out var older));
        Assert.Equal("u32", older.FieldType);
        Assert.True(Load(build: 200).TryGetProperty(0x00000002, 0x000000ee, out var newer));
        Assert.Equal("f32", newer.FieldType);
    }

    [Fact]
    public void UncrackedClassHashHasNoNameButStillLoads()
    {
        var db = Load();
        Assert.True(db.TryGetClass(0x00000003, out var cls));
        Assert.False(cls.HasName);
        Assert.Equal("0x00000003", cls.ToString());
    }

    [Fact]
    public void MissingFileDegradesToEmptyRatherThanThrowing()
    {
        var db = MetaClassDatabase.Load(Path.Combine(Path.GetTempPath(), "rey_meta_does_not_exist.json"));
        Assert.True(db.IsEmpty);
        Assert.Equal(0, db.ClassCount);
        Assert.False(db.TryGetName(0x00000001, out _));
    }

    [Fact]
    public void MalformedFileDegradesToEmptyRatherThanThrowing()
    {
        string path = Path.Combine(Path.GetTempPath(), $"rey_meta_bad_{System.Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ this is not json");
        try
        {
            var db = MetaClassDatabase.Load(path);
            Assert.True(db.IsEmpty);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("0x1003c990", 0x1003c990u)]
    [InlineData("1003c990", 0x1003c990u)]
    [InlineData("0XFFFFFFFF", 0xFFFFFFFFu)]
    public void ParsesHexHashes(string text, uint expected)
    {
        Assert.True(MetaClassDatabase.TryParseHexHash(text, out uint got));
        Assert.Equal(expected, got);
    }

    [Theory]
    [InlineData("")]
    [InlineData("zzzz")]
    public void RejectsNonHexHashes(string text)
        => Assert.False(MetaClassDatabase.TryParseHexHash(text, out _));
}
