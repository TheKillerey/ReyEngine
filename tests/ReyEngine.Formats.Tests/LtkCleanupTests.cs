using System.Reflection;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Cleanup;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M816 review: Cleanup Project lists a file as unused when nothing in the project points at it, and it learned what points from the
/// project's bins. An imported .fantome's GameData documents are references no bin holds - a module's edits name the textures, meshes and
/// particle systems the layer points at - so in an imported project a custom asset that ONLY a declaration uses was listed as unused,
/// and cleaning it out broke the layer. These pin the index (every string of a document is a reference, a document that cannot be
/// read is a hole) and the scan end to end, with the positive control: an asset a declaration names is NOT listed, one nothing names IS.
/// </summary>
public sealed class LtkCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rey-m816-cleanup-" + Guid.NewGuid().ToString("N"));

    public LtkCleanupTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    // ===================================================== the index

    [Fact]
    public void EveryStringOfADocumentIsAReferenceValuesAndKeysAlike()
    {
        var ix = new ProjectReferenceIndex();

        ix.AddGameData("{\"version\":1,\"modules\":[{\"target\":\"data/maps/a.bin\",\"edits\":[{\"Maps/A\":{\"texture\":\"ASSETS/Maps/Custom.dds\",\"assets/keyed.tex\":1}}]}]}");

        Assert.True(ix.IsReferenced("assets/maps/custom.dds", HashAlgorithms.WadPath("assets/maps/custom.dds"), out _));       // a value, in any casing
        Assert.True(ix.IsReferenced("assets/keyed.tex", 0, out _));                                                              // a key
        Assert.True(ix.IsReferenced("data/maps/a.bin", 0, out _));
        Assert.False(ix.IsReferenced("assets/maps/other.dds", HashAlgorithms.WadPath("assets/maps/other.dds"), out _));
        Assert.Equal(1, ix.GameDataRead);
        Assert.Equal(0, ix.GameDataFailed);
        Assert.True(ix.IsComplete);
    }

    [Fact]
    public void AHashSpelledTheWayDeclarationsSpellHashesIsAReferenceToo()
    {
        const string asset = "assets/maps/hashed.dds", entry = "Maps/Hashed/Thing";
        var ix = new ProjectReferenceIndex();

        ix.AddGameData($"{{\"version\":1,\"modules\":[{{\"target\":\"{HashAlgorithms.WadPath(asset):x16}\",\"edits\":[{{\"0x{HashAlgorithms.Fnv1a(entry):x8}\":{{\"n\":1}}}}]}}]}}");

        Assert.True(ix.IsReferenced(asset, HashAlgorithms.WadPath(asset), out _));                // the sixteen digits are a path hash
        Assert.True(ix.IsReferenced(entry, 0, out _));                                            // 0x and eight digits are a name hash
    }

    [Fact]
    public void ADocumentThatIsNotJsonAddsNothingAndLeavesTheIndexIncomplete()
    {
        var ix = new ProjectReferenceIndex();

        ix.AddGameData("{\"a\":\"assets/half.dds\",\"b\": }");                                   // a string read before the document turns out broken

        Assert.False(ix.IsReferenced("assets/half.dds", HashAlgorithms.WadPath("assets/half.dds"), out _));
        Assert.Equal((0, 1), (ix.GameDataRead, ix.GameDataFailed));
        Assert.False(ix.IsComplete);                                                              // "nothing references it" can no longer be said
    }

    [Fact]
    public void ADocumentNestedPastWhatTheReaderAllowsIsAHoleToo()
    {
        var ix = new ProjectReferenceIndex();

        ix.AddGameData(string.Concat(Enumerable.Repeat("[", 300)) + string.Concat(Enumerable.Repeat("]", 300)));

        Assert.False(ix.IsComplete);
    }

    [Fact]
    public void ADocumentThatCouldNotBeReadAtAllIsAHoleToo()
    {
        var ix = new ProjectReferenceIndex();
        ix.NoteGameDataUnreadable();
        Assert.False(ix.IsComplete);
        Assert.Equal(1, ix.GameDataFailed);
    }

    [Fact]
    public void AnIndexOfBinsAloneIsCompleteAsItWas()
    {
        Assert.True(new ProjectReferenceIndex().IsComplete);
    }

    // ===================================================== the scan

    private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>A game folder whose DATA.wad.client has a table of contents with one hash, so the scan has a game index and can prove "unused".</summary>
    private string GameFolder()
    {
        string final = Path.Combine(_root, "Game", "DATA", "FINAL");
        Directory.CreateDirectory(final);
        var wad = new byte[272 + 32];
        wad[0] = (byte)'R'; wad[1] = (byte)'W'; wad[2] = 3; wad[3] = 4;
        BitConverter.GetBytes(1u).CopyTo(wad, 268);                                                // one entry
        BitConverter.GetBytes(0x1122334455667788UL).CopyTo(wad, 272);                              // a hash no project file has
        File.WriteAllBytes(Path.Combine(final, "DATA.wad.client"), wad);
        return Path.Combine(_root, "Game");
    }

    private const string Doc =
        "{\"version\":1,\"modules\":[{\"target\":\"data/maps/a.bin\",\"edits\":[{\"Maps/A\":{\"texture\":\"ASSETS/Maps/Used-By-GameData.dds\"}}],"
        + "\"origin\":{\"manifest\":\"game_data.yaml\",\"source\":null,\"module\":0}}]}";

    /// <summary>An imported project: a WAD folder with an asset only the GameData names, one nothing names, and one the project's own
    /// bin-less metadata does not mention either.</summary>
    private ReyProject ImportedProject(string? document = Doc, bool withKey = true)
    {
        string root = Path.Combine(_root, "proj");
        foreach (string rel in new[] { "assets/maps/used-by-gamedata.dds", "assets/maps/orphan.dds" })
        {
            string path = Path.Combine(root, "Map11", rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        }
        var p = new ReyProject
        {
            Name = "P", RootPath = root, OutputDirectory = Path.Combine(_root, "Build"), GameDirectory = GameFolder(),
            ProjectFolders = { "Map11" },
        };
        p.Layers.Add(new ProjectLayer { Name = "winter", Priority = 1, DeclarationsKey = withKey ? "winter" : null });
        if (document is not null) LtkProjectStore.WriteDeclarations(root, "winter", document);
        return p;
    }

    private static CleanupReport Scan(ReyProject project)
    {
        var vm = new MainWindowViewModel { Project = project };
        var scan = typeof(MainWindowViewModel).GetMethod("ScanForCleanup", NonPublic)!;
        return (CleanupReport)scan.Invoke(vm, new object[] { true, false, false, new Progress<(double, string)>(_ => { }) })!;
    }

    [Fact]
    public void AnAssetOnlyAGameDataDocumentNamesIsNotListedAndOneNothingNamesIs()
    {
        var report = Scan(ImportedProject());

        var listed = report.Candidates.ToDictionary(c => c.RelPath, c => c.Group);
        Assert.DoesNotContain("assets/maps/used-by-gamedata.dds", listed.Keys);                   // a declaration points at it
        Assert.Equal(CleanupGroup.Unused, listed["assets/maps/orphan.dds"]);                      // the control: the scan can still prove "unused"
        Assert.Empty(report.Notes.Where(n => n.Contains("could not be read")));
    }

    [Fact]
    public void AnAssetOnlyAnEditOnTopOfTheGameDataNamesIsNotListedAsUnusedEither()
    {
        // M823: the person pointed a material at a texture of the project in an edit kept on top of the GameData; the edit is a module of the project's own, in a file beside the package's document
        var project = ImportedProject();
        LtkEditStore.Set(project, project.Layers[0], HashAlgorithms.WadPath("data/maps/b.bin"),
            "{\"target\":\"data/maps/b.bin\",\"edits\":[{\"Maps/B\":{\"texture\":\"ASSETS/Maps/Orphan.dds\"}}],\"origin\":{\"manifest\":\"game_data.yaml\",\"source\":null,\"module\":1}}");

        var report = Scan(project);

        var listed = report.Candidates.ToDictionary(c => c.RelPath, c => c.Group);
        Assert.DoesNotContain("assets/maps/orphan.dds", listed.Keys);                              // the edit points at it: spared, though nothing else does
        Assert.DoesNotContain("assets/maps/used-by-gamedata.dds", listed.Keys);                    // and the package's document still does
        Assert.Empty(report.Notes.Where(n => n.Contains("could not be read")));
    }

    [Fact]
    public void WithoutTheDocumentTheSameAssetIsListedAsItWasBeforeTheFix()
    {
        // the control of the control: with no stored document (a project that never imported one) the asset is unused, so it is the
        // document that spares it
        var report = Scan(ImportedProject(document: null, withKey: false));

        var listed = report.Candidates.ToDictionary(c => c.RelPath, c => c.Group);
        Assert.Equal(CleanupGroup.Unused, listed["assets/maps/used-by-gamedata.dds"]);
        Assert.Equal(CleanupGroup.Unused, listed["assets/maps/orphan.dds"]);
    }

    [Fact]
    public void ADocumentThatCannotBeReadMakesEveryUnusedVerdictUncertain()
    {
        var report = Scan(ImportedProject(document: "{ this is not json"));

        // the scan cannot see what the broken document names, so it proves nothing: both are listed, unticked and said to be unverified
        Assert.All(report.Candidates, c => Assert.Equal(CleanupGroup.Protected, c.Group));
        Assert.Equal(2, report.Candidates.Count);
        Assert.Contains(report.Notes, n => n.Contains("unreadable GameData document", StringComparison.Ordinal));
        Assert.All(report.Candidates, c => Assert.False(c.SelectedByDefault));
    }

    [Fact]
    public void AKeyThatIsNoFolderNameReadsNothingAndTheProjectIsStillScanned()
    {
        var project = ImportedProject();
        project.Layers[0].DeclarationsKey = "..\\..";                                              // project.json is a file a person can edit

        var report = Scan(project);

        Assert.Equal(CleanupGroup.Unused, report.Candidates.Single(c => c.RelPath == "assets/maps/used-by-gamedata.dds").Group);   // no document, no spare
    }
}
