using System.Text.RegularExpressions;
using ReyEngine.App.ViewModels;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M583: the Save button and the save itself must agree on what "there is work to do" means.
///
/// <para>They have drifted apart twice. M572 was face edits — the button knew, the save did not.
/// M583 was Blender reshapes, the same way: the push queued, the button lit, and the save returned
/// "No map edits to save" and dropped the work on the floor. Neither failed; both silently did nothing,
/// which is the worst way for a save to behave.</para>
///
/// <para>So the two conditions are compared here rather than trusted to stay in step.</para>
/// </summary>
public sealed class MapGeoSaveGateTests
{
    private static string? ViewModelSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(dir.FullName, "src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>The kinds of pending work, as each side names them.</summary>
    private static readonly (string Property, string Gate, string What)[] Kinds =
    {
        ("HasMapMoves", "hasMoves", "mesh moves"),
        ("_faceEdits.Count > 0", "hasFaces", "face edits"),
        ("_faceGrows.Count > 0", "hasGrows", "extrude/inset"),
        ("_blenderReshapes.Count > 0", "hasReshapes", "Blender reshapes"),
        ("MapContent.AddedMeshes.Count > 0", "added.Count == 0", "added meshes"),
    };

    [Fact]
    public void EverythingTheSaveButtonCountsIsAlsoCountedByTheSave()
    {
        if (ViewModelSource() is not { } source) return;

        int at = source.IndexOf("public bool HasPendingMapGeoWork", StringComparison.Ordinal);
        Assert.True(at > 0, "HasPendingMapGeoWork has moved or been renamed");
        string property = source[at..source.IndexOf(';', at)];

        var gateMatch = Regex.Match(source, @"if \(!hasFaces[^)]*\)\s*\r?\n?\s*\{ _log\.Info\(""MapGeo"", ""No map edits to save",
            RegexOptions.Singleline);
        Assert.True(gateMatch.Success, "the save's early-return gate has moved");
        string gate = gateMatch.Value;

        foreach (var (propertyTerm, gateTerm, what) in Kinds)
        {
            Assert.True(property.Contains(propertyTerm, StringComparison.Ordinal),
                $"HasPendingMapGeoWork no longer counts {what} ('{propertyTerm}')");
            Assert.True(gate.Contains(gateTerm, StringComparison.Ordinal),
                $"the save's gate no longer counts {what} ('{gateTerm}') — a push of that kind would be dropped");
        }
    }

    [Fact]
    public void AQueuedReshapeMakesTheSaveButtonLight()
    {
        // The half that was already right, kept honest: the button has to notice.
        var vm = new MainWindowViewModel();
        Assert.False(vm.HasPendingMapGeoWork);
        Assert.False(vm.HasBlenderReshapes);
    }

    [Fact]
    public void TheReshapeQueueIsClearedOnlyWhereTheBytesAreWritten()
    {
        // M584: the auto-apply reads this queue to tell "written" from "refused", and a later pass can
        // still return early. Clearing it where the reshape is applied rather than where the file is
        // written would drop the edit AND report success.
        if (ViewModelSource() is not { } source) return;

        int clears = Regex.Matches(source, @"ClearBlenderReshapes\(\);").Count;
        Assert.True(clears == 1, $"ClearBlenderReshapes is called {clears} times; it belongs only at the write");

        int applied = source.IndexOf("MapGeoMeshReshaper.TryApply(bytes, PendingBlenderReshapes", StringComparison.Ordinal);
        Assert.True(applied > 0, "the save no longer applies reshapes");
        // Searched FORWARD from the apply: other save methods in this file declare savedTo too, and the
        // first match belongs to the lightgrid bake.
        int written = source.IndexOf("string savedTo;", applied, StringComparison.Ordinal);
        int cleared = source.IndexOf("ClearBlenderReshapes();", applied, StringComparison.Ordinal);
        Assert.True(written > applied, "the reshape is applied after the file is written");
        Assert.True(cleared > written, "the queue is cleared before the bytes are written");
    }

    [Fact]
    public void APushWritesAndReloadsWithoutBeingAsked()
    {
        // The complaint this answers: a reshape only exists in the FILE, so leaving it queued meant the
        // user saw nothing happen and had to know to press Save.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return;
        string bridge = Path.Combine(dir.FullName, "src", "ReyEngine.App", "ViewModels",
            "MainWindowViewModel.BlenderBridge.cs");
        if (!File.Exists(bridge)) return;
        string source = File.ReadAllText(bridge);

        Assert.Contains("AutoApplyBlenderReshapes", source, StringComparison.Ordinal);
        Assert.Contains("SaveMeshMovesCommand.ExecuteAsync", source, StringComparison.Ordinal);
        Assert.Contains("LoadMapGeoAsync", source, StringComparison.Ordinal);
        // and it must not reload over an edit the save refused
        Assert.Contains("if (_blenderReshapes.Count > 0)", source, StringComparison.Ordinal);
        Assert.NotNull(typeof(MainWindowViewModel).GetProperty("BlenderAutoApply"));
        Assert.True(new MainWindowViewModel().BlenderAutoApply, "auto-apply should be on by default");
    }

    [Fact]
    public void TheGateAndTheWriterUseTheSameFlag()
    {
        // The gate deciding there IS work and the block that does it must not test different things.
        if (ViewModelSource() is not { } source) return;
        Assert.Contains("bool hasReshapes = _blenderReshapes.Count > 0;", source, StringComparison.Ordinal);
        Assert.Contains("if (hasReshapes)", source, StringComparison.Ordinal);
        Assert.Contains("MapGeoMeshReshaper.TryApply(bytes, PendingBlenderReshapes", source, StringComparison.Ordinal);
    }
}
