using System.Reflection;
using System.Text.RegularExpressions;
using ReyEngine.App.ViewModels;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Skeletons;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M612: the ACTIONS list in the model preview.
///
/// <para>The list is the one place a missing animation is visible to the user, so the tests care most
/// about what happens when an action has no clip: it stays, it is dimmed, and selecting it does nothing
/// rather than throwing.</para>
/// </summary>
public sealed class CharacterActionPanelTests
{
    private static AnimClipInfo Clip(string name, int vfx = 0, int sfx = 0) => new(
        name, "animations/" + name.ToLowerInvariant() + ".anm",
        Array.Empty<string>(), Array.Empty<string>(), Array.Empty<uint>(), Array.Empty<uint>(),
        Enumerable.Range(0, vfx).Select(i => new AnimParticleEvent($"fx{i}", 0u, "bone", 0f)).ToList(),
        Enumerable.Range(0, sfx).Select(i => new AnimSoundEvent($"sfx{i}", 0f, false)).ToList());

    private static IReadOnlyList<CharacterAction> Kit() => CharacterActions.Build(
        new[] { "XQAbility/x", "XWAbility/x", "XEAbility/x", "XRAbility/x" },
        new[] { Clip("Spell1", vfx: 2), Clip("Spell2"), Clip("Spell4"), Clip("Run"), Clip("Recall", sfx: 1), Clip("Dance") });

    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        return dir?.FullName;
    }

    // ===================================================== the rows

    [Fact]
    public void TheListShowsEveryActionIncludingTheOnesWithNoAnimation()
    {
        var preview = new MeshPreviewViewModel();
        preview.SetActions(Kit());

        Assert.True(preview.HasActions);
        // E has no Spell3 clip in this kit and must still be listed.
        var e = preview.Actions.Single(a => a.Label.StartsWith("E"));
        Assert.False(e.HasClip);
        Assert.Contains("no animation", e.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheSummarySaysHowManyActionsCanActuallyBePlayed()
    {
        // "13 actions" when three of them do nothing is the kind of quiet lie that costs an hour.
        var preview = new MeshPreviewViewModel();
        preview.SetActions(Kit());

        Assert.Contains("of", preview.ActionSummary);
        Assert.Contains("animation", preview.ActionSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RowsCarryWhatTheyWillPlay()
    {
        var preview = new MeshPreviewViewModel();
        preview.SetActions(Kit());

        var q = preview.Actions.First(a => a.Label.StartsWith("Q"));
        Assert.Contains("spell1", q.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vfx", q.Detail, StringComparison.OrdinalIgnoreCase);

        var recall = preview.Actions.First(a => a.Label == "Recall");
        Assert.Contains("sfx", recall.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AbilitiesComeFirstAndEveryRowHasARealGroup()
    {
        var preview = new MeshPreviewViewModel();
        preview.SetActions(Kit());

        Assert.Equal(4, preview.Actions.Count(a => a.Group == "Abilities"));
        Assert.All(preview.Actions.Take(4), a => Assert.Equal("Abilities", a.Group));

        // "Other" is a group name that tells the reader nothing. Every kind the builder emits has to
        // land somewhere real - Death fell through to it until this test said so.
        Assert.DoesNotContain(preview.Actions, a => a.Group == "Other");
    }

    // ===================================================== playing

    [Fact]
    public void SelectingAnActionWithNoAnimationDoesNothingRatherThanThrowing()
    {
        var preview = new MeshPreviewViewModel();
        preview.SetActions(Kit());

        preview.SelectedAction = preview.Actions.First(a => !a.HasClip);

        Assert.Null(preview.Animation.SelectedAnimation);
    }

    [Fact]
    public void SelectingAnActionWhoseClipIsNotInTheAnimationListDoesNothing()
    {
        // The clip is named by the animation graph; the .anm it points at may not be shipped in this
        // WAD. Playing nothing is right - guessing a different clip would be worse.
        var preview = new MeshPreviewViewModel();
        preview.SetActions(Kit());

        preview.SelectedAction = preview.Actions.First(a => a.Label.StartsWith("Q"));

        Assert.Null(preview.Animation.SelectedAnimation);
    }

    [Fact]
    public void LoadingANonCharacterClearsTheList()
    {
        // Four empty ability rows on a lamppost would be worse than no list at all.
        var preview = new MeshPreviewViewModel();
        preview.SetActions(Kit());
        preview.SetActions(Array.Empty<CharacterAction>());

        Assert.False(preview.HasActions);
        Assert.Empty(preview.Actions);
        Assert.Null(preview.SelectedAction);
        Assert.Equal("", preview.ActionSummary);
    }

    // ===================================================== the window

    [Fact]
    public void EveryActionBindingInThePreviewWindowResolves()
    {
        if (RepoRoot() is not { } root) return;
        string xaml = Path.Combine(root, "src", "ReyEngine.App", "Views", "MeshPreviewWindow.axaml");
        if (!File.Exists(xaml)) return;

        // Only the ACTIONS card - the rest of this window predates the test and is covered by the build.
        var card = Regex.Match(File.ReadAllText(xaml), @"Text=""ACTIONS""(.|\n)*?</Border>");
        Assert.True(card.Success, "the ACTIONS card is missing from MeshPreviewWindow.axaml");

        Type[] contexts = { typeof(MeshPreviewViewModel), typeof(CharacterActionRowViewModel) };
        var missing = new List<string>();
        foreach (Match m in Regex.Matches(card.Value, @"\{Binding\s+!?([A-Za-z_][A-Za-z0-9_]*)"))
        {
            string path = m.Groups[1].Value;
            if (!contexts.Any(t => t.GetProperty(path, BindingFlags.Public | BindingFlags.Instance) is not null))
                missing.Add(path);
        }

        Assert.True(missing.Count == 0, "bound in the ACTIONS card but on neither context: "
                                        + string.Join(", ", missing.Distinct()));
    }

    [Fact]
    public void TheReplayCommandExists() =>
        Assert.NotNull(typeof(MeshPreviewViewModel).GetProperty("ReplayActionCommand"));
}
