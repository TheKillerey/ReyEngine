using System.Text;
using ReyEngine.Core.Cleanup;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>M302: Cleanup Project. Every test here is about the same thing - the tool must never be
/// confident about deleting something it cannot prove is safe.</summary>
public class CleanupTests
{
    // ---------- the reference index ----------

    private static ProjectReferenceIndex IndexOf(params string[] refs)
    {
        var ix = new ProjectReferenceIndex();
        foreach (var r in refs) ix.AddReference(r);
        return ix;
    }

    private static bool Ref(ProjectReferenceIndex ix, string rel) =>
        ix.IsReferenced(rel, HashAlgorithms.WadPath(rel), out _);

    [Theory]
    // The spelling in the bin  ->  the file on disk. Every one of these pairs is the SAME asset, and the
    // pre-M302 exact-string rule would have called each file unreferenced and offered it for deletion.
    [InlineData("ASSETS/Maps/Tex/Ground.dds", "assets/maps/tex/ground.dds")]      // case
    [InlineData(@"assets\maps\tex\ground.dds", "assets/maps/tex/ground.dds")]     // separator
    [InlineData("/assets/maps/tex/ground.dds", "assets/maps/tex/ground.dds")]     // leading slash
    [InlineData("assets/maps/tex/ground.dds", "assets/maps/tex/ground.tex")]      // converted extension
    [InlineData("assets/maps/tex/ground.tex", "assets/maps/tex/ground.dds")]      // and back
    [InlineData("assets/maps/mesh/tower.scb", "assets/maps/mesh/tower.sco")]      // binary vs text mesh
    public void AReferenceIsFoundThroughEverySpellingItCanTake(string inBin, string onDisk)
        => Assert.True(Ref(IndexOf(inBin), onDisk), $"'{inBin}' should have covered '{onDisk}'");

    [Fact]
    public void AnUnrelatedFileIsStillNotReferenced()
    {
        var ix = IndexOf("assets/maps/tex/ground.dds");
        Assert.False(Ref(ix, "assets/maps/tex/sky.dds"));
        Assert.False(Ref(ix, "assets/characters/x/y.skn"));
    }

    [Fact]
    public void ABareNameInABinCoversTheFileItNames()
    {
        // Map bins name characters and systems by bare identifier, never as a path.
        var ix = IndexOf("SRU_Baron");
        Assert.True(Ref(ix, "assets/characters/sru_baron/sru_baron.skn"));
    }

    [Fact]
    public void AssetNameAtTheLastByteIsIndexed()
    {
        var ix = new ProjectReferenceIndex();
        ix.AddAssetNames(Encoding.ASCII.GetBytes("binary\0assets/x/at_eof.dds"));
        Assert.True(Ref(ix, "assets/x/at_eof.dds"));
    }

    [Fact]
    public void AShortStemDoesNotMatchOnNameAlone()
    {
        // "sky" is too generic to be evidence about assets/.../sky.dds; requiring 4+ chars keeps the
        // name rule from turning into a match-everything rule.
        var ix = IndexOf("sky");
        Assert.False(Ref(ix, "assets/maps/tex/sky.dds"));
    }

    [Fact]
    public void ChunksThatAreNotBinsAreNotCountedAsFailures()
    {
        var ix = new ProjectReferenceIndex();
        ix.AddBin(Encoding.ASCII.GetBytes("DDS not a bin at all"));
        Assert.Equal(1, ix.NotBins);
        Assert.Equal(0, ix.BinsFailed);
        Assert.True(ix.IsComplete);          // an unidentifiable chunk is not a coverage hole
    }

    [Fact]
    public void ARealBinThatWillNotParseIsACoverageHole()
    {
        var ix = new ProjectReferenceIndex();
        ix.AddBin(Encoding.ASCII.GetBytes("PROPtruncated garbage"));
        Assert.Equal(1, ix.BinsFailed);
        Assert.False(ix.IsComplete);         // references we cannot see must make the caller cautious
    }

    // ---------- the scanner ----------

    private sealed class FakeIndex(params string[] referenced) : IReferenceIndex
    {
        private readonly HashSet<string> _refs = new(referenced, StringComparer.OrdinalIgnoreCase);
        public bool IsReferenced(string relPath, ulong pathHash, out string how)
        { how = "test"; return _refs.Contains(relPath); }
    }

    private static (string Root, string Folder) MakeProject(params (string Rel, byte[] Bytes)[] files)
    {
        string root = Path.Combine(Path.GetTempPath(), "reyclean-" + Guid.NewGuid().ToString("N")[..8]);
        string folder = Path.Combine(root, "Map11");
        foreach (var (rel, bytes) in files)
        {
            string p = Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, bytes);
        }
        return (root, folder);
    }

    private static CleanupScanOptions Opts(string root, string folder, IReferenceIndex index,
        IReadOnlySet<ulong>? game = null, Func<ulong, byte[]?>? riot = null, bool complete = true) => new()
        {
            ProjectRoot = root,
            Folders = new[] { ("Map11", folder) },
            References = index,
            GameWadHashes = game ?? new HashSet<ulong> { 1 },   // non-empty = the guard can be evaluated
            ReadRiot = riot,
            ScanRiotIdentical = riot is not null,
            ReferencesComplete = complete,
        };

    [Fact]
    public void AnUnreferencedFileNoGameWadShipsIsUnused()
    {
        var (root, folder) = MakeProject(("assets/x/dead.dds", new byte[] { 1, 2, 3 }));
        try
        {
            var r = CleanupScanner.Scan(Opts(root, folder, new FakeIndex()));
            var c = Assert.Single(r.Candidates);
            Assert.Equal(CleanupGroup.Unused, c.Group);
            Assert.True(c.SelectedByDefault);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void GeneratedOutputNestedInAProjectFolderIsExcluded()
    {
        var (root, folder) = MakeProject(
            ("assets/x/dead.dds", new byte[] { 1 }),
            ("Build/staged/Map11/assets/x/generated.dds", new byte[] { 2 }));
        try
        {
            var baseOptions = Opts(root, folder, new FakeIndex());
            var options = new CleanupScanOptions
            {
                ProjectRoot = baseOptions.ProjectRoot,
                Folders = baseOptions.Folders,
                References = baseOptions.References,
                GameWadHashes = baseOptions.GameWadHashes,
                ExcludedRoots = new[] { Path.Combine(folder, "Build") },
            };

            var candidate = Assert.Single(CleanupScanner.Scan(options).Candidates);
            Assert.Equal("assets/x/dead.dds", candidate.RelPath);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AReferencedFileIsNotACandidateAtAll()
    {
        var (root, folder) = MakeProject(("assets/x/live.dds", new byte[] { 1 }));
        try
        {
            var r = CleanupScanner.Scan(Opts(root, folder, new FakeIndex("assets/x/live.dds")));
            Assert.Empty(r.Candidates);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AFileTheGameShipsIsNeverUnusedEvenIfNothingReferencesIt()
    {
        var (root, folder) = MakeProject(("assets/x/override.dds", new byte[] { 1 }));
        try
        {
            // It overrides real game content, so the game can always ask for it.
            var game = new HashSet<ulong> { HashAlgorithms.WadPath("assets/x/override.dds") };
            var r = CleanupScanner.Scan(Opts(root, folder, new FakeIndex(), game));
            Assert.Empty(r.Candidates);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void WithoutAGameIndexUnusedBecomesUncertainAndUnticked()
    {
        // The real bug this guards: when the game folder failed to resolve, the scan indexed zero WADs
        // and cheerfully called shipped Riot textures unused.
        var (root, folder) = MakeProject(("assets/x/maybe.dds", new byte[] { 1 }));
        try
        {
            var r = CleanupScanner.Scan(Opts(root, folder, new FakeIndex(), game: new HashSet<ulong>()));
            var c = Assert.Single(r.Candidates);
            Assert.Equal(CleanupGroup.Protected, c.Group);
            Assert.False(c.SelectedByDefault);
            Assert.NotEmpty(r.Notes);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AnIncompleteReferenceIndexAlsoDowngradesUnused()
    {
        var (root, folder) = MakeProject(("assets/x/maybe.dds", new byte[] { 1 }));
        try
        {
            var r = CleanupScanner.Scan(Opts(root, folder, new FakeIndex(), complete: false));
            Assert.Equal(CleanupGroup.Protected, Assert.Single(r.Candidates).Group);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AFileMatchingTheRiotOriginalIsOfferedAsIdenticalToRiot()
    {
        var bytes = new byte[] { 9, 8, 7, 6 };
        var (root, folder) = MakeProject(("assets/x/same.dds", bytes));
        try
        {
            var r = CleanupScanner.Scan(Opts(root, folder, new FakeIndex(), riot: _ => bytes));
            var c = Assert.Single(r.Candidates);
            Assert.Equal(CleanupGroup.IdenticalToRiot, c.Group);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AFileThatDiffersFromRiotIsNotOfferedAsIdentical()
    {
        var (root, folder) = MakeProject(("assets/x/edited.dds", new byte[] { 1, 2, 3 }));
        try
        {
            // Same path, different bytes - this is the mod's actual edit and must never be reclaimed.
            var game = new HashSet<ulong> { HashAlgorithms.WadPath("assets/x/edited.dds") };
            var r = CleanupScanner.Scan(Opts(root, folder, new FakeIndex(), game, riot: _ => new byte[] { 4, 5, 6 }));
            Assert.Empty(r.Candidates);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AProjectWadCopyBlocksTheRiotFallbackClaim()
    {
        var bytes = new byte[] { 1, 2 };
        var (root, folder) = MakeProject(("assets/x/shadowed.dds", bytes));
        try
        {
            var o = new CleanupScanOptions
            {
                ProjectRoot = root,
                Folders = new[] { ("Map11", folder) },
                References = new FakeIndex(),
                GameWadHashes = new HashSet<ulong> { 1 },
                ReadRiot = _ => bytes,
                ProjectWadCopies = _ => 1,      // a packed project copy would win instead of Riot
            };
            var c = Assert.Single(CleanupScanner.Scan(o).Candidates);
            Assert.Equal(CleanupGroup.Protected, c.Group);
            Assert.False(c.SelectedByDefault);
        }
        finally { Directory.Delete(root, true); }
    }

    // ---------- review, round 3: identical to Riot, but another project folder provides the file too ----------

    /// <summary>A project of several folders (another layer's copy of a WAD, another folder of the same WAD), each holding the given files.</summary>
    private static (string Root, string[] Folders) MakeFolders(params (string Folder, (string Rel, byte[] Bytes)[] Files)[] folders)
    {
        string root = Path.Combine(Path.GetTempPath(), "reyclean-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new List<string>();
        foreach (var (folder, files) in folders)
        {
            string dir = Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));
            paths.Add(dir);
            Directory.CreateDirectory(dir);
            foreach (var (rel, bytes) in files)
            {
                string p = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllBytes(p, bytes);
            }
        }
        return (root, paths.ToArray());
    }

    private static CleanupScanOptions RiotScan(string root, IReadOnlyList<(string Name, string Root)> folders, Func<ulong, byte[]?>? riot,
        IReadOnlyList<string>? excluded = null, bool riotMode = true) => new()
        {
            ProjectRoot = root,
            Folders = folders,
            References = new FakeIndex(),
            GameWadHashes = new HashSet<ulong> { 1 },
            ReadRiot = riot,
            ScanRiotIdentical = riotMode && riot is not null,
            ExcludedRoots = excluded ?? Array.Empty<string>(),
        };

    [Fact]
    public void ACopyIdenticalToRiotThatAnotherProjectFolderAlsoProvidesIsProtectedNotTicked()
    {
        var bytes = new byte[] { 9, 8, 7, 6 };
        var (root, dirs) = MakeFolders(
            ("Map11", new[] { ("assets/x/same.dds", bytes) }),
            ("layers/winter/Map11", new[] { ("assets/x/same.dds", bytes) }));
        try
        {
            var r = CleanupScanner.Scan(RiotScan(root, new[] { ("Map11", dirs[0]), ("layers/winter/Map11", dirs[1]) }, _ => bytes));

            // taking out either copy would expose the other one, not Riot's: both are kept, unticked, and say why
            Assert.Equal(2, r.Candidates.Count);
            Assert.All(r.Candidates, c =>
            {
                Assert.Equal(CleanupGroup.Protected, c.Group);
                Assert.False(c.SelectedByDefault);
                Assert.Contains("another project folder also provides this file", c.Reason, StringComparison.OrdinalIgnoreCase);
            });
            Assert.Contains("(layers/winter/Map11)", r.Candidates.Single(c => c.Folder == "Map11").Reason);          // it names the other folder
            Assert.Contains("(Map11)", r.Candidates.Single(c => c.Folder == "layers/winter/Map11").Reason);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AProjectOfOneFolderOrOfFoldersThatShareNoPathIsJudgedAsItWas()
    {
        var bytes = new byte[] { 9, 8, 7, 6 };
        var (root, dirs) = MakeFolders(
            ("Map11", new[] { ("assets/x/one.dds", bytes) }),
            ("layers/winter/Map11", new[] { ("assets/x/two.dds", bytes) }));
        try
        {
            var two = CleanupScanner.Scan(RiotScan(root, new[] { ("Map11", dirs[0]), ("layers/winter/Map11", dirs[1]) }, _ => bytes));
            Assert.All(two.Candidates, c => { Assert.Equal(CleanupGroup.IdenticalToRiot, c.Group); Assert.True(c.SelectedByDefault); });
            Assert.Equal(2, two.Candidates.Count);

            var one = CleanupScanner.Scan(RiotScan(root, new[] { ("Map11", dirs[0]) }, _ => bytes));
            var c1 = Assert.Single(one.Candidates);
            Assert.Equal(CleanupGroup.IdenticalToRiot, c1.Group);
            Assert.Contains("removing it falls back to Riot", c1.Reason);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ACopyThatDiffersFromRiotIsNotListedBecauseAnotherFolderHasTheSamePath()
    {
        var (root, dirs) = MakeFolders(
            ("Map11", new[] { ("assets/x/edited.dds", new byte[] { 1 }) }),
            ("layers/winter/Map11", new[] { ("assets/x/edited.dds", new byte[] { 2 }) }));
        try
        {
            var o = RiotScan(root, new[] { ("Map11", dirs[0]), ("layers/winter/Map11", dirs[1]) }, _ => new byte[] { 3 });
            var game = new HashSet<ulong> { HashAlgorithms.WadPath("assets/x/edited.dds") };
            var r = CleanupScanner.Scan(new CleanupScanOptions
            {
                ProjectRoot = o.ProjectRoot, Folders = o.Folders, References = o.References, GameWadHashes = game, ReadRiot = o.ReadRiot, ScanRiotIdentical = true,
            });

            Assert.Empty(r.Candidates);                                                              // an edit of the game's file: never offered, as before
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AHashNamedChunkTwoFoldersBothHoldIsProtectedToo()
    {
        var bytes = new byte[] { 5, 5 };
        var (root, dirs) = MakeFolders(
            ("Map11", new[] { ("a1b2c3d4e5f60718.dds", bytes) }),
            ("layers/winter/Map11", new[] { ("a1b2c3d4e5f60718.dds", bytes) }));
        try
        {
            var r = CleanupScanner.Scan(RiotScan(root, new[] { ("Map11", dirs[0]), ("layers/winter/Map11", dirs[1]) }, _ => bytes));

            Assert.All(r.Candidates, c => Assert.Equal(CleanupGroup.Protected, c.Group));
            Assert.Equal(2, r.Candidates.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void TheSameFileReachedThroughTwoEntriesIsOneCopyNotTwo()
    {
        var bytes = new byte[] { 4, 4 };
        var (root, dirs) = MakeFolders(("Map11", new[] { ("assets/x/same.dds", bytes) }));
        try
        {
            // one folder listed twice, or a folder inside another: the path is provided once
            var r = CleanupScanner.Scan(RiotScan(root, new[] { ("Map11", dirs[0]), ("Map11 again", dirs[0]) }, _ => bytes));

            Assert.All(r.Candidates, c => Assert.Equal(CleanupGroup.IdenticalToRiot, c.Group));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AFolderTheScanExcludesIsNotAnotherFolderThatProvidesTheFile()
    {
        var bytes = new byte[] { 6, 6 };
        var (root, dirs) = MakeFolders(
            ("Map11", new[] { ("assets/x/same.dds", bytes) }),
            ("Build/staged/Map11", new[] { ("assets/x/same.dds", bytes) }));      // a build's staging copy of the same WAD, listed as a folder
        try
        {
            var r = CleanupScanner.Scan(RiotScan(root, new[] { ("Map11", dirs[0]), ("Build/staged/Map11", dirs[1]) }, _ => bytes,
                excluded: new[] { Path.Combine(root, "Build") }));

            var c = Assert.Single(r.Candidates);                                                     // generated output is neither scanned nor a provider
            Assert.Equal(CleanupGroup.IdenticalToRiot, c.Group);
            Assert.Equal("Map11", c.Folder);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void WithoutTheRiotModeTheFoldersAreJudgedAsTheyWere()
    {
        var bytes = new byte[] { 1, 1 };
        var (root, dirs) = MakeFolders(
            ("Map11", new[] { ("assets/x/same.dds", bytes) }),
            ("layers/winter/Map11", new[] { ("assets/x/same.dds", bytes) }));
        try
        {
            var r = CleanupScanner.Scan(RiotScan(root, new[] { ("Map11", dirs[0]), ("layers/winter/Map11", dirs[1]) }, _ => bytes, riotMode: false));

            Assert.All(r.Candidates, c => Assert.Equal(CleanupGroup.Unused, c.Group));              // nothing references them, the game does not ship them
            Assert.Equal(2, r.Candidates.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AHashNamedChunkIsJudgedByItsHash()
    {
        // Its NAME is the chunk's WAD path hash, and the engine can only load it by that hash - so both
        // conditions are answerable without a path. Treating these as unjudgeable stranded 560 MB.
        var (root, folder) = MakeProject(("a1b2c3d4e5f60718.dds", new byte[] { 1 }));
        try
        {
            var c = Assert.Single(CleanupScanner.Scan(Opts(root, folder, new FakeIndex())).Candidates);
            Assert.Equal(CleanupGroup.Unused, c.Group);
            Assert.Contains("hash", c.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AHashNamedChunkTheGameShipsIsKept()
    {
        const ulong h = 0xa1b2c3d4e5f60718;
        var (root, folder) = MakeProject(("a1b2c3d4e5f60718.dds", new byte[] { 1 }));
        try
        {
            var r = CleanupScanner.Scan(Opts(root, folder, new FakeIndex(), new HashSet<ulong> { h }));
            Assert.Empty(r.Candidates);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AHashNamedChunkAProjectBinReferencesIsKept()
    {
        const ulong h = 0xa1b2c3d4e5f60718;
        var (root, folder) = MakeProject(("a1b2c3d4e5f60718.dds", new byte[] { 1 }));
        try
        {
            // A bin naming the real path hashes to exactly this chunk key, which is how the match lands
            // even though the file on disk shows only the hash.
            var ix = new ProjectReferenceIndex();
            ix.AddHash(h);
            var r = CleanupScanner.Scan(Opts(root, folder, ix, new HashSet<ulong> { 1 }));
            Assert.Empty(r.Candidates);
        }
        finally { Directory.Delete(root, true); }
    }

    // ---------- the executor ----------

    [Fact]
    public void CleanupMovesFilesToBackupAndRestorePutsThemBackByteForByte()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var (root, folder) = MakeProject(("assets/x/dead.dds", payload));
        try
        {
            var report = CleanupScanner.Scan(Opts(root, folder, new FakeIndex()));
            var pick = report.Candidates.Where(c => c.SelectedByDefault).ToList();
            string abs = pick[0].AbsPath;

            var run = CleanupExecutor.Run(root, "run1", pick);
            Assert.Equal(1, run.Moved);
            Assert.False(File.Exists(abs));                  // gone from the project
            Assert.True(File.Exists(run.ManifestPath));      // recorded

            var (restored, failed, _) = CleanupExecutor.Restore(root, "run1");
            Assert.Equal(1, restored);
            Assert.Equal(0, failed);
            Assert.Equal(payload, File.ReadAllBytes(abs));   // and back, unchanged
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RestoreNeverOverwritesAFileThatCameBackOnItsOwn()
    {
        var (root, folder) = MakeProject(("assets/x/dead.dds", new byte[] { 1 }));
        try
        {
            var report = CleanupScanner.Scan(Opts(root, folder, new FakeIndex()));
            var pick = report.Candidates.ToList();
            string abs = pick[0].AbsPath;
            CleanupExecutor.Run(root, "run1", pick);

            // Something re-created the file after the cleanup - an undo must not clobber the newer copy.
            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            File.WriteAllBytes(abs, new byte[] { 42 });

            var (restored, failed, _) = CleanupExecutor.Restore(root, "run1");
            Assert.Equal(0, restored);
            Assert.Equal(1, failed);
            Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(abs));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void TheManifestRecordsEnoughToAuditARun()
    {
        var (root, folder) = MakeProject(("assets/x/dead.dds", new byte[] { 7, 7, 7 }));
        try
        {
            var pick = CleanupScanner.Scan(Opts(root, folder, new FakeIndex())).Candidates.ToList();
            CleanupExecutor.Run(root, "run1", pick);

            var runs = CleanupExecutor.ListRuns(root);
            var m = Assert.Single(runs);
            var e = Assert.Single(m.Entries);
            Assert.Equal("run1", m.Id);
            Assert.Equal(3, e.Bytes);
            Assert.NotEmpty(e.Sha256);
            Assert.NotEmpty(e.Reason);
            Assert.NotEmpty(e.RemovedUtc);
            Assert.NotEmpty(e.OriginalPath);
            Assert.NotEmpty(e.BackupPath);
        }
        finally { Directory.Delete(root, true); }
    }
}
