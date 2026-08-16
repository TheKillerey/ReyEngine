using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M495: how Mimir tables sit underneath HashDatabase's own dictionaries.
///
/// <para>The ordering is the load-bearing part. Mimir's tables are a published snapshot; the dictionaries
/// hold what the editor discovered locally — hashes read out of an open WAD, names typed into a project,
/// entries merged from a manual .txt. If a table could shadow those, a name the user just established would
/// silently revert to whatever CommunityDragon knew weeks ago. Mimir's own LayeredHashDb resolves the same
/// way: overlay first, then each base in push order.</para>
/// </summary>
public sealed class MimirLayeringTests
{
    private static string WriteTable(IEnumerable<(ulong Key, string Value)> entries, int keyWidth)
    {
        string path = Path.Combine(Path.GetTempPath(), $"reyengine_mimir_{Guid.NewGuid():N}.hashdb");
        HashDbTestWriter.Write(path, entries, keyWidth);
        return path;
    }

    [Fact]
    public void TableAnswersWhatTheDictionariesDoNot()
    {
        string path = WriteTable(new[] { (100ul, "assets/from/table.dds") }, keyWidth: 8);
        try
        {
            var db = new HashDatabase();
            Assert.False(db.TryGetPath(100, out _));

            db.AttachTable(MimirTableKind.Wad, HashDbFile.Open(path));
            Assert.True(db.TryGetPath(100, out var got));
            Assert.Equal("assets/from/table.dds", got);
            Assert.Equal("assets/from/table.dds", db.ResolvePath(100));

            db.DetachTables();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LocallyDiscoveredNamesWinOverThePublishedTable()
    {
        string path = WriteTable(new[] { (100ul, "assets/stale/name.dds") }, keyWidth: 8);
        try
        {
            var db = new HashDatabase();
            db.AttachTable(MimirTableKind.Wad, HashDbFile.Open(path));
            db.AddWad(100, "assets/discovered/right_now.dds");

            Assert.True(db.TryGetPath(100, out var got));
            Assert.Equal("assets/discovered/right_now.dds", got);

            db.DetachTables();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void BinAndWadTablesDoNotAnswerEachOther()
    {
        // Both lookups take a hash and return a string, so a table attached under the wrong kind would
        // resolve happily and hand back nonsense instead of missing.
        string wad = WriteTable(new[] { (7ul, "assets/wad/path.dds") }, keyWidth: 8);
        string bin = WriteTable(new[] { (7ul, "SomeBinField") }, keyWidth: 4);
        try
        {
            var db = new HashDatabase();
            db.AttachTable(MimirTableKind.Wad, HashDbFile.Open(wad));
            db.AttachTable(MimirTableKind.Bin, HashDbFile.Open(bin));

            Assert.True(db.TryGetPath(7, out var path));
            Assert.Equal("assets/wad/path.dds", path);
            Assert.True(db.TryGetBinName(7, out var name));
            Assert.Equal("SomeBinField", name);

            db.DetachTables();
        }
        finally { File.Delete(wad); File.Delete(bin); }
    }

    [Fact]
    public void UnknownHashStillReportsUnknown()
    {
        string path = WriteTable(new[] { (1ul, "one") }, keyWidth: 8);
        try
        {
            var db = new HashDatabase();
            db.AttachTable(MimirTableKind.Wad, HashDbFile.Open(path));

            Assert.False(db.TryGetPath(999, out _));
            Assert.Equal("0x00000000000003e7.unknown", db.ResolvePath(999));
            Assert.Empty(db.WadCandidates(999));

            db.DetachTables();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CandidatesSurfaceTableHitsToo()
    {
        string path = WriteTable(new[] { (42ul, "assets/only/in/table.dds") }, keyWidth: 8);
        try
        {
            var db = new HashDatabase();
            db.AttachTable(MimirTableKind.Wad, HashDbFile.Open(path));

            var candidates = db.WadCandidates(42);
            Assert.Single(candidates);
            Assert.Equal("assets/only/in/table.dds", candidates[0]);

            db.DetachTables();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RstTablesAreNotConsumedAsPathsOrNames()
    {
        // rst / rst-xxh3 hold translation-string hashes, not WAD paths or bin names. HashDatabase's text
        // loader has always skipped them; the same decision has to survive the move to Mimir, or every
        // translation key becomes a candidate "path".
        Assert.Equal(MimirTableKind.Ignored, MimirManifest.KindOf("rst"));
        Assert.Equal(MimirTableKind.Ignored, MimirManifest.KindOf("rst-xxh3"));
        Assert.Equal(MimirTableKind.Wad, MimirManifest.KindOf("game"));
        Assert.Equal(MimirTableKind.Wad, MimirManifest.KindOf("lcu"));
        foreach (string bin in new[] { "binentries", "binfields", "binhashes", "bintypes" })
            Assert.Equal(MimirTableKind.Bin, MimirManifest.KindOf(bin));

        // An unrecognised future table must be ignored rather than guessed at.
        Assert.Equal(MimirTableKind.Ignored, MimirManifest.KindOf("something-new"));
    }

    [Fact]
    public void ManifestRoundTripsAndListsOnlyUsableTables()
    {
        // Shape taken from the published 2026-08-14 manifest.
        const string json = """
        {
          "schema": 1,
          "generated_at": "2026-08-14T23:44:11Z",
          "source": { "repo": "CommunityDragon/Data", "commit": "36ae5d4e", "inputs_sha256": "f40bac83" },
          "tables": {
            "bintypes": { "file": "bintypes-2026-08-14.lhdb", "sha256": "aa", "entries": 3711, "key_width": 4 },
            "game":     { "file": "game-2026-08-14.lhdb",     "sha256": "bb", "entries": 2291324, "key_width": 8 },
            "rst":      { "file": "rst-2026-08-14.lhdb",      "sha256": "cc", "entries": 108709, "key_width": 8 }
          }
        }
        """;

        var manifest = MimirManifest.Parse(json);
        Assert.NotNull(manifest);
        Assert.Equal(1, manifest!.Schema);
        Assert.Equal("CommunityDragon/Data", manifest.Source?.Repo);
        Assert.Equal(3, manifest.Tables.Count);
        Assert.Equal(2291324, manifest.Tables["game"].Entries);

        var usable = manifest.UsableTables().ToList();
        Assert.Equal(2, usable.Count);                       // rst dropped
        Assert.DoesNotContain(usable, u => u.Name == "rst");

        // The release tag is ours, not the published document's, and has to survive a save/load.
        manifest.ReleaseTag = "hashes-2026-08-14";
        var again = MimirManifest.Parse(manifest.ToJson());
        Assert.Equal("hashes-2026-08-14", again!.ReleaseTag);
        Assert.Equal(3711, again.Tables["bintypes"].Entries);
    }

    [Fact]
    public void MalformedManifestIsRejectedRatherThanThrowing()
    {
        Assert.Null(MimirManifest.Parse("not json at all"));
        Assert.Null(MimirManifest.Load(Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.json")));
    }
}
