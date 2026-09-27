using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M792: "No hash dictionary yet" at every start of an install whose names all come from Mimir's tables.
///
/// <para>LoadLocal skips the merged cache once a table is attached, so a Mimir install keeps its dictionaries
/// empty unless data/hashes holds a manual .txt - and the warning counted only the dictionaries. Measured on a
/// copy of this machine's 2026-09-21 tables with nothing else beside them: "Mimir hash tables: 6 table(s),
/// 3.320.761 entries." and the warning on the very next line. The main checkout never showed it only because two
/// map453 path lists sit in its data/hashes.</para>
/// </summary>
public sealed class HashStartupWarningTests
{
    [Fact]
    public void EitherLayerAloneMeansThereAreNamesToResolve()
    {
        Assert.True(new HashDatabase().IsEmpty);

        // A Mimir install: tables attached, dictionaries empty.
        string path = Path.Combine(Path.GetTempPath(), $"reyengine_mimir_{Guid.NewGuid():N}.hashdb");
        HashDbTestWriter.Write(path, new[] { (100ul, "assets/from/table.dds") }, keyWidth: 8);
        var tablesOnly = new HashDatabase();
        try
        {
            tablesOnly.AttachTable(MimirTableKind.Wad, HashDbFile.Open(path));

            Assert.Equal(0, tablesOnly.WadCount + tablesOnly.BinCount);   // all the warning used to ask
            Assert.True(tablesOnly.TryGetPath(100, out _));
            Assert.False(tablesOnly.IsEmpty);
        }
        finally
        {
            // Unmapped first: a mapped file cannot be deleted, and that IOException would replace a failed assert.
            tablesOnly.DetachTables();
            File.Delete(path);
        }

        // The CommunityDragon cache or a manual .txt: dictionaries filled, no table at all.
        var dictionaryOnly = new HashDatabase();
        dictionaryOnly.AddBin(7, "SomeBinField");
        Assert.Equal(0, dictionaryOnly.TableCount);
        Assert.False(dictionaryOnly.IsEmpty);
    }

    private static string? Source(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) continue;
            string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        return null;
    }

    [Fact]
    public void TheStartupWarningAsksTheWholeDatabase()
    {
        // The constructor reads the hash folder under the fixed data root, so which branch it takes depends on
        // the machine running the test. The gate is what can be pinned everywhere.
        string? main = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        if (main is null) return;
        int warning = main.IndexOf("_log.Warn(\"Hashes\", \"No hash dictionary yet.", StringComparison.Ordinal);
        Assert.True(warning > 0);
        int gate = main.LastIndexOf("if (", warning, StringComparison.Ordinal);
        Assert.StartsWith("if (db.IsEmpty)", main[gate..warning]);
    }
}
