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
              "revisions": [ { "from": 100, "to": 200, "type": ["u32", "", "", ""], "default": 7 } ]
            },
            "0x000000ee": {
              "name": "retypedField",
              "revisions": [
                { "from": 100, "to": 200, "type": ["u32", "", "", ""] },
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

    private static MetaClassDatabase Load(int? build = null)
    {
        string path = Path.Combine(Path.GetTempPath(), $"rey_meta_{System.Guid.NewGuid():N}.json");
        File.WriteAllText(path, Fixture);
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
        // 'to' is EXCLUSIVE: present at 100, gone at 200. Getting this backwards would show users fields
        // their patch does not have.
        Assert.True(Load(build: 100).TryGetProperty(0x00000002, 0x000000dd, out _));
        Assert.False(Load(build: 200).TryGetProperty(0x00000002, 0x000000dd, out _));
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
