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

    [Fact]
    public void AHashNamedChunkIsNeverJudgedUnused()
    {
        // A loose <hash>.ext file carries no path, so no path-shaped check can say anything about it.
        var (root, folder) = MakeProject(("a1b2c3d4e5f60718.dds", new byte[] { 1 }));
        try
        {
            var c = Assert.Single(CleanupScanner.Scan(Opts(root, folder, new FakeIndex())).Candidates);
            Assert.Equal(CleanupGroup.Protected, c.Group);
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
