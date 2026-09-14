using System;
using System.IO;
using System.Linq;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M725: the Character Viewer's preview environment is Dominion or Twisted Treeline, named once, placed
/// per map, and drawn by both renderers.
/// </summary>
public sealed class BackdropEnvironmentTests
{
    [Fact]
    public void ThePreviewEnvironmentsAreExactlyTheTwoAndAreNamedOnce()
    {
        var packs = SetupService.LegacyMapPacks;
        Assert.Equal(2, packs.Count);
        Assert.Equal(new[] { SetupService.BackdropDominion, SetupService.BackdropTwistedTreeline },
            packs.Select(p => p.Name));

        // The Map8 pack was "Dominion" in the wizard and the README and "Crystal Scar (Map8)" in Settings -
        // one download wearing two names reads as two maps, one of which you are missing.
        Assert.Contains("Dominion", SetupService.BackdropDominion, StringComparison.Ordinal);
        Assert.DoesNotContain("Crystal Scar", string.Join("|", packs.Select(p => p.Name)), StringComparison.Ordinal);
    }

    [Fact]
    public void APackNameRoundTripsToItsFolderAndBack()
    {
        foreach (var (name, dir, _, _) in SetupService.LegacyMapPacks)
        {
            Assert.Equal(dir, SetupService.BackdropFolderFor(name));
            Assert.Equal(name, SetupService.BackdropNameFor(dir));
            // trailing separators and case must not make a folder stop being one of the two
            Assert.Equal(name, SetupService.BackdropNameFor(dir + Path.DirectorySeparatorChar));
            Assert.Equal(name, SetupService.BackdropNameFor(dir.ToUpperInvariant()));
        }

        // anything else is not a preview backdrop - an arena and a legacy map opened directly both reach
        // this code, and neither should be mistaken for one of the two packs
        Assert.Null(SetupService.BackdropFolderFor("Howling Abyss"));
        Assert.Null(SetupService.BackdropNameFor(@"C:\Riot Games\League of Legends"));
        Assert.Null(SetupService.BackdropNameFor(""));
        Assert.Null(SetupService.BackdropNameFor(null));
    }

    [Fact]
    public void EachMapGetsItsOwnPlacementAndTwistedTreelineNoLongerInheritsDominions()
    {
        var dominion = MeshPreviewViewModel.BackdropPlacement("Map8");
        Assert.Equal((-6400d, -60d, 2000d, 180d), dominion);   // the hand-tuned hero shot, unchanged since M89

        // MapPreviewLoader anchors every backdrop so the team spawn sits at the world origin, so zero is
        // the honest default for a map nobody has framed by hand. Twisted Treeline used to silently take
        // Dominion's numbers: 6,400 units away and turned 180 degrees.
        var treeline = MeshPreviewViewModel.BackdropPlacement("Map10");
        Assert.Equal((0d, 0d, 0d, 0d), treeline);
        Assert.NotEqual(dominion, treeline);

        Assert.Equal(treeline, MeshPreviewViewModel.BackdropPlacement(null));
        Assert.Equal(dominion, MeshPreviewViewModel.BackdropPlacement("map8"));   // case-insensitive
    }

    /// <summary>
    /// The repo root marker is <c>ReyEngine.slnx</c>; there is no <c>.sln</c>. Two test classes looked for
    /// the wrong one, so their <c>Source()</c> walk ran off the top of the drive, returned null, and every
    /// source guard in them early-returned GREEN without reading a file - 16 assertions that had never once
    /// executed. This stops it coming back.
    /// </summary>
    [Fact]
    public void NoTestLooksForASolutionFileThatDoesNotExist()
    {
        string? root = RepoRoot();
        if (root is null) return;

        // The real shape is Path.Combine(dir.FullName, "ReyEngine.sln") - matched precisely so this guard
        // does not trip over its own message, and skipping this file so it cannot trip over itself either.
        const string Bad = ", \"ReyEngine.sln\")";
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Equals(nameof(BackdropEnvironmentTests) + ".cs", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains(Bad, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0,
            "these look for ReyEngine.sln, which does not exist, so their source guards never run: "
            + string.Join(", ", offenders));
        Assert.True(File.Exists(Path.Combine(root, "ReyEngine.slnx")), "the marker itself must exist");
    }

    [Fact]
    public void TheBackdropIsDrawnByBothRenderersAndComposedTheSameWay()
    {
        string? arena = Source("src", "ReyEngine.App", "ViewModels", "MeshPreviewViewModel.Arena.cs");
        string? gl = Source("src", "ReyEngine.App", "Views", "ViewportControl.cs");
        if (arena is null || gl is null) return;

        // D3D11 gets the backdrop as a prop, the way the arena floor has since M665
        Assert.Contains("BackdropProp() is { } backdrop", arena);
        Assert.Contains("new PropInstanceData(backdrop, BackdropWorld)", arena);

        // and with the SAME composition GL uses - translate, then rotate about world Y. A different order
        // would put one map in two places depending on which renderer is on.
        Assert.Contains("Matrix4x4.CreateTranslation(BackgroundOffset)", arena);
        Assert.Contains("Matrix4x4.CreateTranslation(BackgroundOffset)", gl);
    }

    [Fact]
    public void UnloadingAnArenaPutsTheConfiguredBackdropBack()
    {
        string? arena = Source("src", "ReyEngine.App", "ViewModels", "MeshPreviewViewModel.Arena.cs");
        string? host = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.Characters.cs");
        if (arena is null || host is null) return;

        // the view model asks; the host, which owns the loader and the cache, answers
        Assert.Contains("public Func<Task>? ReapplyBackdrop;", arena);
        Assert.Contains("await reapply()", arena);
        Assert.Contains("MeshPreview.ReapplyBackdrop = ApplyPreviewBackgroundAsync;", host);
    }

    [Fact]
    public void TheArenaHostIsConfiguredEvenForASubjectWithNoAnimations()
    {
        string? host = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.Characters.cs");
        if (host is null) return;

        // ConfigureArena must come BEFORE the clips early-return: whether you can walk around a map is a
        // property of the install, not of whether the last thing you clicked had animations.
        int configure = host.IndexOf("MeshPreview.ConfigureArena", StringComparison.Ordinal);
        int earlyReturn = host.IndexOf("if (allClips is not { Count: > 0 }", StringComparison.Ordinal);
        Assert.True(configure > 0 && earlyReturn > 0, "both landmarks must still exist");
        Assert.True(configure < earlyReturn,
            "ConfigureArena runs after the clips early-return, so clipless subjects get no arena");
    }

    private static string? RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) return dir.FullName;
        return null;
    }

    private static string? Source(params string[] parts)
    {
        string? root = RepoRoot();
        if (root is null) return null;
        string path = Path.Combine(new[] { root }.Concat(parts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }
}
