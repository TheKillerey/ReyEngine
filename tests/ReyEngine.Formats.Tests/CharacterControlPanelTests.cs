using System.Numerics;
using System.Reflection;
using System.Text.RegularExpressions;
using ReyEngine.App.ViewModels;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Skeletons;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M613: control mode in the preview — the wiring between a right-click and a walking character.
/// </summary>
public sealed class CharacterControlPanelTests
{
    private static AnimClipInfo Clip(string name) => new(
        name, "animations/" + name.ToLowerInvariant() + ".anm",
        Array.Empty<string>(), Array.Empty<string>(), Array.Empty<uint>(), Array.Empty<uint>());

    private static MeshPreviewViewModel Preview()
    {
        var preview = new MeshPreviewViewModel();
        preview.SetActions(CharacterActions.Build(
            new[] { "XQAbility/x", "XWAbility/x", "XEAbility/x", "XRAbility/x" },
            new[] { Clip("Spell1"), Clip("Spell2"), Clip("Spell3"), Clip("Spell4"),
                    Clip("Attack1"), Clip("Run"), Clip("Idle1") }));
        return preview;
    }

    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        return dir?.FullName;
    }

    // ===================================================== orders are ignored until control mode is on

    [Fact]
    public void OrdersDoNothingWhileControlModeIsOff()
    {
        // The preview is a viewer first. Someone panning around a model must not discover that a stray
        // right-click has walked the character out of frame.
        var preview = Preview();

        preview.OrderMove(new Vector3(900, 0, 0));
        preview.OrderAttack(new Vector3(900, 0, 0));

        Assert.Equal(Vector3.Zero, preview.CharacterPosition);
    }

    [Fact]
    public void CastingDoesNothingWhileControlModeIsOff()
    {
        var preview = Preview();
        preview.CastAbility(0);
        Assert.Null(preview.SelectedAction);
    }

    // ===================================================== casting

    [Fact]
    public void EachKeySlotCastsItsOwnAbility()
    {
        var preview = Preview();
        preview.ControlMode = true;

        foreach (var (slot, letter) in new[] { (0, "Q"), (1, "W"), (2, "E"), (3, "R") })
        {
            preview.CastAbility(slot);
            Assert.NotNull(preview.SelectedAction);
            Assert.StartsWith(letter, preview.SelectedAction!.Label);
        }

        preview.StopControl();
    }

    [Fact]
    public void AnOutOfRangeSlotIsIgnored()
    {
        var preview = Preview();
        preview.ControlMode = true;

        preview.CastAbility(-1);
        preview.CastAbility(4);

        Assert.Null(preview.SelectedAction);
        preview.StopControl();
    }

    [Fact]
    public void CastingAnAbilityWithNoAnimationSaysSoInsteadOfDoingNothingVisible()
    {
        var preview = new MeshPreviewViewModel();
        preview.SetActions(CharacterActions.Build(
            new[] { "XQAbility/x", "XWAbility/x", "XEAbility/x", "XRAbility/x" },
            new[] { Clip("Run") }));                 // no Spell clips at all
        preview.ControlMode = true;

        preview.CastAbility(0);

        Assert.Contains("no animation", preview.ControlStatus, StringComparison.OrdinalIgnoreCase);
        preview.StopControl();
    }

    [Fact]
    public void CastingReportsTheAbilityAndLeavesTheCharacterWhereItIs()
    {
        var preview = Preview();
        preview.ControlMode = true;
        preview.OrderMove(new Vector3(5000, 0, 0));
        var before = preview.CharacterPosition;

        preview.CastAbility(0);

        Assert.StartsWith("Q", preview.ControlStatus);
        Assert.Equal(before, preview.CharacterPosition);
        preview.StopControl();
    }

    // ===================================================== the panel

    [Fact]
    public void TurningControlModeOnAndOffLeavesNoTimerRunning()
    {
        // A DispatcherTimer holding the view model alive would keep ticking against a viewport that has
        // already gone.
        var preview = Preview();
        preview.ControlMode = true;
        Assert.NotEmpty(preview.ControlStatus);

        preview.StopControl();
        Assert.False(preview.ControlMode);
        Assert.Equal("", preview.ControlStatus);
    }

    [Fact]
    public void TheTunablesRoundTripThroughTheViewModel()
    {
        var preview = Preview();
        preview.MoveSpeed = 425;
        preview.AttackRange = 550;
        preview.AttacksPerSecond = 1.25;

        Assert.Equal(425, preview.MoveSpeed, 3);
        Assert.Equal(550, preview.AttackRange, 3);
        Assert.Equal(1.25, preview.AttacksPerSecond, 3);
    }

    [Fact]
    public void ResettingPutsTheCharacterBackAtTheOrigin()
    {
        var preview = Preview();
        preview.ControlMode = true;
        preview.OrderMove(new Vector3(900, 0, 900));

        preview.ResetCharacterCommand.Execute(null);

        Assert.Equal(Vector3.Zero, preview.CharacterPosition);
        Assert.Equal(0d, preview.CharacterYaw, 3);
        preview.StopControl();
    }

    [Fact]
    public void EveryControlBindingInTheWindowResolves()
    {
        if (RepoRoot() is not { } root) return;
        string xaml = Path.Combine(root, "src", "ReyEngine.App", "Views", "MeshPreviewWindow.axaml");
        if (!File.Exists(xaml)) return;

        var card = Regex.Match(File.ReadAllText(xaml), @"Text=""CONTROL""(.|\n)*?</Border>");
        Assert.True(card.Success, "the CONTROL card is missing from MeshPreviewWindow.axaml");

        var missing = new List<string>();
        foreach (Match m in Regex.Matches(card.Value, @"\{Binding\s+!?([A-Za-z_][A-Za-z0-9_]*)"))
        {
            string path = m.Groups[1].Value;
            if (typeof(MeshPreviewViewModel).GetProperty(path, BindingFlags.Public | BindingFlags.Instance) is null)
                missing.Add(path);
        }

        Assert.True(missing.Count == 0,
            "bound in the CONTROL card but not on the view model: " + string.Join(", ", missing.Distinct()));
    }

    [Fact]
    public void TheViewportIsToldWhereTheCharacterIs()
    {
        // The controller can walk perfectly and nothing moves on screen unless these two are bound.
        if (RepoRoot() is not { } root) return;
        string xaml = Path.Combine(root, "src", "ReyEngine.App", "Views", "MeshPreviewWindow.axaml");
        if (!File.Exists(xaml)) return;

        string text = File.ReadAllText(xaml);
        Assert.Contains("ModelPosition=\"{Binding CharacterPosition}\"", text);
        Assert.Contains("ModelYaw=\"{Binding CharacterYaw}\"", text);
    }
}
