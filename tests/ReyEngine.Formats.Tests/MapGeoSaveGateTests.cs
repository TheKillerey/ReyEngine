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
    public void TheGateAndTheWriterUseTheSameFlag()
    {
        // The gate deciding there IS work and the block that does it must not test different things.
        if (ViewModelSource() is not { } source) return;
        Assert.Contains("bool hasReshapes = _blenderReshapes.Count > 0;", source, StringComparison.Ordinal);
        Assert.Contains("if (hasReshapes)", source, StringComparison.Ordinal);
        Assert.Contains("MapGeoMeshReshaper.TryApply(bytes, PendingBlenderReshapes", source, StringComparison.Ordinal);
    }
}
