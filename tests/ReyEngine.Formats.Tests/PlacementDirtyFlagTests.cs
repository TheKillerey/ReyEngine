using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using ReyEngine.App.ViewModels;
using ReyEngine.Formats.MapGeo;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>M700: "there are placement edits to save" is one predicate. It used to be written out by hand
/// at each of the seven places that raise the flag, and most of those copies counted only particles - so a
/// moved prop was saved by nothing at all, because the copy at the end of a gizmo drag recomputed the flag
/// without props and the button went straight back to disabled.</summary>
public sealed class PlacementDirtyFlagTests
{
    private static MapContentViewModel Content()
    {
        var content = new MapContentViewModel();
        var group = new AnimatedPropGroupViewModel { CharacterName = "Golem" };
        group.Props.Add(new AnimatedPropViewModel
        {
            Prop = new MapAnimatedProp("Golem_1", new Vector3(100f, 0f, 100f),
                Matrix4x4.CreateTranslation(100f, 0f, 100f),
                "Characters/Golem/CharacterRecords/Root", "Characters/Golem/Skins/Skin0",
                VisibilityFlags: 255, HasVisibilityFlags: false, Id: new MapPlacementId(0x5000u, 0xBEEFu)),
        });
        content.PropGroups.Add(group);
        return content;
    }

    [Fact]
    public void AMovedPropIsSomethingToSave()
    {
        var content = Content();
        var prop = content.AllProps.Single();
        Assert.False(content.HasPlacementEdits);

        prop.Offset = new Vector3(25f, 0f, 0f);
        Assert.True(prop.IsMoved);
        Assert.True(content.HasPlacementEdits);

        prop.Offset = Vector3.Zero;
        Assert.False(content.HasPlacementEdits);

        // the other prop edits still count, as they did before
        prop.EditedVisibilityFlags = 0;
        Assert.True(content.HasPlacementEdits);
        prop.EditedVisibilityFlags = null;
        prop.IsRemoved = true;
        Assert.True(content.HasPlacementEdits);
    }

    [Fact]
    public void EveryPlaceThatRaisesTheFlagUsesTheOnePredicate()
    {
        var host = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        var content = Source("src", "ReyEngine.App", "ViewModels", "MapContentViewModel.cs");
        if (host is null || content is null) return;

        Assert.Contains("public bool HasPlacementEdits =>", content);
        Assert.Contains("private void RefreshPlacementDirtyFlag() => HasParticleMoves = MapContent.HasPlacementEdits;", host);

        // the flag is assigned in exactly three places: the one helper and the two post-save resets
        var assignments = Regex.Matches(host, @"HasParticleMoves = [^;]+;").Select(m => m.Value).ToList();
        Assert.Equal(3, assignments.Count);
        Assert.Single(assignments, a => a == "HasParticleMoves = MapContent.HasPlacementEdits;");
        Assert.Equal(2, assignments.Count(a => a == "HasParticleMoves = false;"));
        // and seven call sites raise it, the gizmo drag among them
        Assert.Equal(7, Regex.Matches(host, @"RefreshPlacementDirtyFlag\(\);").Count);
        Assert.Matches(@"public void EndPlacementDrag\(\)\s*\{\s*RefreshPlacementDirtyFlag\(\);", host);
    }

    private static string? Source(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        return null;
    }
}
