using ReyEngine.Core.Projects;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M816: which layer a project folder ships in, now that a folder can be one WAD in a layer of its own.
///
/// <para>A folder's layer and WAD name have always come from the LEAF of its resolved path (<see cref="ReyProject.LayerOf"/>,
/// asked by M814's staging and by Send to LTK Manager). Two folders of one WAD name - <c>Map11</c> in base and
/// <c>layers/winter/Map11</c> in a layer - have one leaf, so a layer can also claim a whole <c>ProjectFolders</c> entry
/// (<see cref="ReyProject.LayerOfFolder"/>). These pin both halves: that every project written before M816 is answered exactly as
/// it was, and that a whole-entry claim tells two folders of one leaf apart.</para>
/// </summary>
public sealed class LayerOfFolderTests
{
    private static ReyProject Project(params (string Layer, string[] Folders)[] layers)
    {
        var p = new ReyProject { RootPath = @"C:\Projects\Mod" };
        foreach (var (name, folders) in layers) p.Layers.Add(new ProjectLayer { Name = name, Folders = folders.ToList() });
        return p;
    }

    /// <summary>The rule both callers applied before M816, spelled out: the layer the leaf of the resolved folder is claimed by.</summary>
    private static string Before(ReyProject p, string entry) =>
        p.LayerOf(Path.GetFileName(p.ResolveProjectPath(entry).TrimEnd('/', '\\')));

    // ===================================================== a project that predates M816 is answered as it was

    [Theory]
    [InlineData("Ahri", "fix")]                          // a flat entry, claimed by its name
    [InlineData("ahri", "fix")]                          // without regard to case
    [InlineData("mods/Ahri", "fix")]                     // a nested entry is claimed by its leaf
    [InlineData("mods\\Ahri", "fix")]                    // a backslash entry too
    [InlineData("a/b/c/Ahri", "fix")]
    [InlineData("Map453", "base")]                       // claimed by nobody: base
    [InlineData("mods/Map453", "base")]
    [InlineData("Ahri2", "base")]                        // a leaf that only starts like a claimed one
    public void ALeafNameClaimIsAnsweredByTheLeafAsItAlwaysWas(string entry, string expected)
    {
        var p = Project(("fix", new[] { "Ahri" }));
        Assert.Equal(expected, p.LayerOfFolder(entry));
        Assert.Equal(Before(p, entry), p.LayerOfFolder(entry));
    }

    [Theory]
    [InlineData("mods/Ahri")]
    [InlineData("other/mods/Ahri")]
    [InlineData("mods\\Ahri")]
    public void AWholeEntryInTheLayersFoldersIsNotWhatTheSettingsDialogEverWroteAndClaimsOnlyThatFolder(string entry)
    {
        // the dialog wrote leaf names only. A nested entry listed whole is the M816 form: it claims that folder and no other.
        var p = Project(("fix", new[] { "mods/Ahri" }));
        Assert.Equal(entry.Replace('\\', '/') == "mods/Ahri" ? "fix" : "base", p.LayerOfFolder(entry));
    }

    [Fact]
    public void EveryShapeAProjectCouldHoldGivesTheLeafAnswer()
    {
        var entries = new[] { "Ahri", "ahri", "mods/Ahri", "mods\\Ahri", "Map453", "content/Map453", "x/y/Lux", ".", "RAW" };
        var claims = new[]
        {
            Array.Empty<string>(), new[] { "Ahri" }, new[] { "Map453" }, new[] { "Lux", "Ahri" }, new[] { "AHRI" }, new[] { "RAW" },
            new[] { Path.GetFileName(@"C:\Projects\Mod") },          // the project's own folder, claimed by its leaf
        };
        foreach (var claim in claims)
        {
            var p = Project(("one", claim), ("two", new[] { "unrelated" }));
            foreach (var entry in entries)
                Assert.Equal(Before(p, entry), p.LayerOfFolder(entry));
        }
    }

    [Fact]
    public void AProjectWithNoLayersIsAllBase()
    {
        var p = new ReyProject();
        Assert.Equal("base", p.LayerOfFolder("Map11"));
        Assert.Equal("base", p.LayerOfFolder("layers/winter/Map11"));
        Assert.False(p.IsClaimedWhole("Map11"));
    }

    // ===================================================== a whole-entry claim tells two folders of one leaf apart

    [Fact]
    public void TwoFoldersOfOneWadNameRideTheirOwnLayers()
    {
        var p = Project(("winter", new[] { "layers/winter/Map11" }));

        Assert.Equal("base", p.LayerOfFolder("Map11"));
        Assert.Equal("winter", p.LayerOfFolder("layers/winter/Map11"));
        Assert.Equal("winter", p.LayerOfFolder("layers\\winter\\Map11"));
        Assert.Equal("winter", p.LayerOfFolder("LAYERS/Winter/map11"));          // without regard to case
        Assert.Equal("base", p.LayerOfFolder("layers/other/Map11"));            // another folder of the same leaf is not claimed
        Assert.True(p.IsClaimedWhole("layers/winter/Map11"));
        Assert.False(p.IsClaimedWhole("layers/other/Map11"));
        Assert.False(p.IsClaimedWhole("Map11"));

        // both are the WAD their leaf names
        Assert.Equal("Map11", Path.GetFileName(p.ResolveProjectPath("layers/winter/Map11")));
    }

    [Fact]
    public void AWholeClaimWinsOverALeafClaimForThatFolderOnly()
    {
        var p = Project(("leaf", new[] { "Map11" }), ("whole", new[] { "layers/winter/Map11" }));

        Assert.Equal("leaf", p.LayerOfFolder("Map11"));
        Assert.Equal("leaf", p.LayerOfFolder("layers/other/Map11"));            // the leaf claim still covers every other folder of that leaf
        Assert.Equal("whole", p.LayerOfFolder("layers/winter/Map11"));
    }

    [Fact]
    public void ASingleSegmentEntryIsNeverClaimedWhole()
    {
        // only an entry with a path in it can be told apart from its leaf; a one-segment entry IS its leaf
        var p = Project(("fix", new[] { "Ahri" }));
        Assert.False(p.IsClaimedWhole("Ahri"));
        Assert.Equal("fix", p.LayerOfFolder("Ahri"));
    }

    [Theory]
    [InlineData("a/b", "a/b")]
    [InlineData("\\a\\b\\", "a/b")]
    [InlineData("/a/b/", "a/b")]
    [InlineData("Ahri", "Ahri")]
    public void EntriesAreComparedWithForwardSlashesAndNoOuterOnes(string entry, string expected) =>
        Assert.Equal(expected, ReyProject.NormalizeFolderEntry(entry));

    // ===================================================== review: two folders that would ship as one WAD of one layer

    private static ReyProject WithFolders(string[] folders, params (string Layer, string[] Folders)[] layers)
    {
        var p = Project(layers);
        foreach (string f in folders) p.ProjectFolders.Add(f);
        return p;
    }

    [Fact]
    public void TwoWholeClaimedFoldersOfOneWadInOneLayerCollide()
    {
        var p = WithFolders(new[] { "layers/winter/Map11", "layers/snow/Map11", "Map11" },
            ("winter", new[] { "layers/winter/Map11", "layers/snow/Map11" }));

        var clash = Assert.Single(p.WholeClaimCollisions());

        Assert.Equal(("winter", "Map11.wad.client"), (clash.Layer, clash.Wad));
        Assert.Equal(new[] { "layers/winter/Map11", "layers/snow/Map11" }, clash.Folders);       // base's Map11 is another layer's WAD
    }

    [Fact]
    public void AWholeClaimedFolderAndALeafClaimedOneOfTheSameWadCollide()
    {
        var p = WithFolders(new[] { "layers/winter/Map11", "other/Map11" },
            ("winter", new[] { "layers/winter/Map11", "Map11" }));

        Assert.Equal(new[] { "layers/winter/Map11", "other/Map11" }, Assert.Single(p.WholeClaimCollisions()).Folders);
    }

    [Fact]
    public void SameWadsInDifferentLayersDoNotCollideAndSoDoesAnImport()
    {
        var p = WithFolders(new[] { "Map11", "layers/winter/Map11", "layers/snow/Map11" },
            ("winter", new[] { "layers/winter/Map11" }), ("snow", new[] { "layers/snow/Map11" }));

        Assert.Empty(p.WholeClaimCollisions());
    }

    [Fact]
    public void ACollisionIsSeenWithoutRegardToCase()
    {
        var p = WithFolders(new[] { "layers/winter/Map11", "layers/snow/map11" },
            ("winter", new[] { "layers/winter/Map11", "layers/snow/map11" }));

        Assert.Single(p.WholeClaimCollisions());
    }

    [Fact]
    public void ProjectsThatNoLayerClaimsWholeNeverCollide()
    {
        // what M744's dialog wrote: two folders of one leaf in one layer (a send merges them, as it always has) are not this check's business
        var p = WithFolders(new[] { "mods/Ahri", "other/Ahri", "Lux" }, ("fix", new[] { "Ahri", "Lux" }));
        Assert.Empty(p.WholeClaimCollisions());
        Assert.Empty(WithFolders(new[] { "a/Map11", "b/Map11" }).WholeClaimCollisions());
        Assert.Empty(new ReyProject { RootPath = @"C:\Projects\Mod" }.WholeClaimCollisions());
    }
}
