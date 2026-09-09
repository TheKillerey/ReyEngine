using System;
using System.IO;
using System.Linq;
using ReyEngine.App.ViewModels;
using ReyEngine.Formats.MapGeo;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M677: a placed prop plays the idle unless a clip is chosen for it, and can be opened in the character
/// window. The choice is viewer state - the map cannot hold it and the game plays the idle - so it is
/// never an edit and never saved.
/// </summary>
public sealed class PropAnimationChoiceTests
{
    private static AnimatedPropViewModel Node() => new()
    {
        Prop = new MapAnimatedProp("Camp", System.Numerics.Vector3.Zero, System.Numerics.Matrix4x4.Identity,
            "Characters/SRU_Krug/CharacterRecords/Root", "Characters/SRU_Krug/Skins/Skin0"),
    };

    [Fact]
    public void TheIdleIsTheDefaultAndTheIdleEntryMeansTheSame()
    {
        var node = Node();
        Assert.Null(node.EffectiveAnimation);
        node.EditedAnimation = AnimatedPropViewModel.IdleChoice;
        Assert.Null(node.EffectiveAnimation);
        node.EditedAnimation = "  Attack1 ";
        Assert.Equal("Attack1", node.EffectiveAnimation);
        node.EditedAnimation = "";
        Assert.Null(node.EffectiveAnimation);
    }

    [Fact]
    public void AClipChoiceIsNotAMapEditButDoesRepose()
    {
        var node = Node();
        int changed = 0;
        node.StateChanged = _ => changed++;
        node.EditedAnimation = "Attack1";
        Assert.Equal(1, changed);          // the viewport rebuilds its prop set off this
        Assert.False(node.HasEdits);       // and nothing asks to be saved
    }

    [Fact]
    public void TheCardOffersTheClipsAndTheWayToTheCharacterWindow()
    {
        var card = Source("src", "ReyEngine.App", "Views", "SceneObjectInspectorView.axaml");
        var main = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        if (card is null || main is null) return;
        Assert.Contains("SelectedItem=\"{Binding SelectedPropNode.EditedAnimation}\"", card);
        Assert.Contains("Command=\"{Binding OpenPropInCharacterEditorCommand}\"", card);
        // the render set is keyed by skin AND clip, so two placements with different clips get two poses
        Assert.Contains("string key = clip is null ? p.Skin : p.Skin + \"|\" + clip;", main);
        // the idle stays the fallback when the chosen name resolves to nothing
        Assert.Contains("(clip is not null ? TryFindClip(skin, clip) : null) ?? TryFindIdleClip(skin)", main);
        // the idle entry is offered first
        Assert.Contains("PropAnimationChoices.Add(AnimatedPropViewModel.IdleChoice);", main);
    }

    /// <summary>The inspector view binds by reflection (no x:DataType), so a misspelt member passes the
    /// build and fails only when the card is shown. These are the three names the card binds.</summary>
    [Fact]
    public void TheCardsBindingsResolveByReflection()
    {
        Assert.NotNull(typeof(MainWindowViewModel).GetProperty("PropAnimationChoices"));
        Assert.NotNull(typeof(MainWindowViewModel).GetProperty("OpenPropInCharacterEditorCommand"));
        Assert.NotNull(typeof(AnimatedPropViewModel).GetProperty("EditedAnimation"));
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
