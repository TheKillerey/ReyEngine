using System.Numerics;
using ReyEngine.App.ViewModels;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Skeletons;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M637: a cast in control mode is aimed at the cursor by the spell's targeting kind, gated by its
/// cooldown, and walks into range when the game would. Driven through the view model the way
/// CharacterControlPanelTests drives it.
/// </summary>
public sealed class CastControlTests
{
    private static AnimClipInfo Clip(string name) => new(
        name, "animations/" + name.ToLowerInvariant() + ".anm",
        Array.Empty<string>(), Array.Empty<string>(), Array.Empty<uint>(), Array.Empty<uint>());

    private static AbilitySlot Slot(int index, string kind, float range, float cooldown, float mana = 0f) =>
        new(index, "QWER"[index].ToString(), "x/y", 0f, false, 0f, Array.Empty<AbilityMissile>())
        { TargetingKind = kind, CastRange = range, Cooldown = cooldown, Mana = mana };

    private static MeshPreviewViewModel Preview()
    {
        var preview = new MeshPreviewViewModel();
        preview.SetActions(CharacterActions.Build(
            new[] { "XQAbility/x", "XWAbility/x", "XEAbility/x", "XRAbility/x" },
            new[] { Clip("Spell1"), Clip("Spell2"), Clip("Spell3"), Clip("Spell4"), Clip("Attack1"), Clip("Run"), Clip("Idle1") }));
        preview.SetAbilities(new[]
        {
            Slot(0, "direction", 1200f, 5f, 28f),      // Ezreal's Q, in numbers
            Slot(1, "Location", 800f, 12f),
            Slot(2, "LocationClamped", 825f, 20f),
            Slot(3, "SelfAoe", 25000f, 120f),
        });
        return preview;
    }

    [Fact]
    public void ACastFacesTheCursorAndReportsItsAim()
    {
        var preview = Preview();
        preview.ControlMode = true;
        preview.CastAbility(0, new Vector3(500f, 0f, 0f));       // straight along +X from the origin

        Assert.NotNull(preview.SelectedAction);
        Assert.StartsWith("Q", preview.SelectedAction!.Label);
        Assert.Equal(MathF.PI / 2f, (float)preview.CharacterYaw, 3);   // atan2(500, 0)
        Assert.Contains("direction", preview.ControlStatus);
        Assert.Contains("28 mana", preview.ControlStatus);
        preview.StopControl();
    }

    [Fact]
    public void ACastOnCooldownIsRefusedAndSaysWhen()
    {
        var preview = Preview();
        preview.ControlMode = true;
        preview.CastAbility(0, new Vector3(500f, 0f, 0f));
        var first = preview.SelectedAction;

        preview.CastAbility(0, new Vector3(0f, 0f, 500f));
        Assert.Contains("ready in", preview.ControlStatus);
        Assert.Same(first, preview.SelectedAction);               // nothing re-fired
        Assert.Contains("Q ", preview.CooldownStatus);            // and the readout shows it counting
        Assert.Contains("W ready", preview.CooldownStatus);
        preview.StopControl();
    }

    [Fact]
    public void OtherSlotsAreNotBlockedByOneSlotsCooldown()
    {
        var preview = Preview();
        preview.ControlMode = true;
        preview.CastAbility(0, new Vector3(500f, 0f, 0f));
        preview.CastAbility(3, null);
        Assert.StartsWith("R", preview.SelectedAction!.Label);
        Assert.Contains("SelfAoe", preview.ControlStatus);
        preview.StopControl();
    }

    [Fact]
    public void AGroundCastBeyondRangeWalksInsteadOfCasting()
    {
        // W is a plain Location cast with 800 range; the cursor is 2000 away. The game walks first.
        var preview = Preview();
        preview.ControlMode = true;
        preview.CastAbility(1, new Vector3(2000f, 0f, 0f));

        Assert.Null(preview.SelectedAction);
        Assert.Contains("walking into range", preview.ControlStatus);
        Assert.Equal("", preview.CooldownStatus);                 // not cast yet, so not on cooldown
        preview.StopControl();
    }

    [Fact]
    public void AClampedCastBeyondRangeCastsAtTheRangeInstead()
    {
        var preview = Preview();
        preview.ControlMode = true;
        preview.CastAbility(2, new Vector3(0f, 0f, 2000f));

        Assert.NotNull(preview.SelectedAction);
        Assert.StartsWith("E", preview.SelectedAction!.Label);
        Assert.Contains("clamped", preview.ControlStatus);
        Assert.Equal(0f, (float)preview.CharacterYaw, 3);         // faces +Z, where the aim is
        preview.StopControl();
    }

    [Fact]
    public void WithNoCursorTheCastFallsBackToTheDummyOrForward()
    {
        var preview = Preview();
        preview.ControlMode = true;
        preview.TargetDummyEnabled = true;                        // the dummy defaults to (350, 0, 0)
        preview.CastAbility(0, null);
        Assert.Equal(MathF.PI / 2f, (float)preview.CharacterYaw, 3);   // turned toward the dummy
        preview.StopControl();
    }
}
