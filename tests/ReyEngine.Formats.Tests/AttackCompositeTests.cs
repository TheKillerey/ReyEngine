using System.Numerics;
using System.Reflection;
using ReyEngine.App.ViewModels;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Skeletons;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M638: a swing in control mode plays the record's next basic attack - its clip, and beside it the
/// missile and hit the same SpellObject names. Driven through the view model with the control tick
/// invoked directly, since the dispatcher timer is not running under xunit.
/// </summary>
public sealed class AttackCompositeTests
{
    private static AnimClipInfo Clip(string name) => new(
        name, "animations/" + name.ToLowerInvariant() + ".anm",
        Array.Empty<string>(), Array.Empty<string>(), Array.Empty<uint>(), Array.Empty<uint>());

    private static AttackSpell Attack(string name, string? clip, float frame, float probability = 0f) =>
        new(name, clip, frame, false, 0f, 0x1234u, Array.Empty<AbilityMissile>(), false) { Probability = probability };

    private static MeshPreviewViewModel Preview(IReadOnlyList<AttackSpell> attacks)
    {
        var preview = new MeshPreviewViewModel();
        preview.SetActions(CharacterActions.Build(
            new[] { "XQAbility/x", "XWAbility/x", "XEAbility/x", "XRAbility/x" },
            new[] { Clip("Attack1"), Clip("Attack2"), Clip("Attack3"), Clip("Run"), Clip("Idle1") }));
        preview.SetAttacks(attacks);
        preview.AttackRange = 175;
        preview.AttacksPerSecond = 100;      // every tick may swing, so the cycle is observable
        return preview;
    }

    private static void Tick(MeshPreviewViewModel preview)
    {
        Thread.Sleep(3);                     // the tick measures wall time; a zero-length tick is ignored
        typeof(MeshPreviewViewModel).GetMethod("Advance", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(preview, null);
    }

    [Fact]
    public void AScriptCycledChampionSwingsThroughItsBasicAttacksInOrder()
    {
        // Aatrox's shape: three plain basic attacks, no weights, plus state attacks that stay out.
        var preview = Preview(new[]
        {
            Attack("XBasicAttack", null, 11f),
            Attack("XBasicAttack2", "Attack2", 9f),
            Attack("XBasicAttack3", "Attack3", 7.5f),
            Attack("XRAttack1", "Attack1_ULT", 12f),
        });
        preview.ControlMode = true;
        preview.OrderAttack(new Vector3(100f, 0f, 0f));      // inside the 175 range: swings at once

        // A swing happens on the attack cadence, not on every tick, so tick until the status changes.
        // The baseline is the control-mode greeting, not "" - the first change must be a swing.
        var seen = new List<string>();
        string last = preview.ControlStatus;
        for (int swing = 0; swing < 4; swing++)
        {
            for (int i = 0; i < 40 && preview.ControlStatus == last; i++) Tick(preview);
            Assert.NotEqual(last, preview.ControlStatus);
            last = preview.ControlStatus;
            seen.Add(last);
        }
        Assert.StartsWith("XBasicAttack: Attack1", seen[0]);
        Assert.StartsWith("XBasicAttack2: Attack2", seen[1]);
        Assert.StartsWith("XBasicAttack3: Attack3", seen[2]);
        Assert.StartsWith("XBasicAttack: Attack1", seen[3]);   // wraps; the R attack never appears
        Assert.All(seen, s => Assert.Contains("hit", s));
        preview.StopControl();
    }

    [Fact]
    public void AWeightedPoolOnlyEverPicksWeightedAttacks()
    {
        var preview = Preview(new[]
        {
            Attack("XBasicAttack", null, 9f, probability: 0.75f),
            Attack("XBasicAttack2", "Attack2", 9f, probability: 0.25f),
            Attack("XQAttack", "Spell1", 9f),                 // weight 0: script-only
        });
        preview.ControlMode = true;
        preview.OrderAttack(new Vector3(100f, 0f, 0f));
        for (int i = 0; i < 12; i++)
        {
            Tick(preview);
            Assert.DoesNotContain("XQAttack", preview.ControlStatus);
            Assert.True(preview.ControlStatus.StartsWith("XBasicAttack"), preview.ControlStatus);
        }
        preview.StopControl();
    }

    [Fact]
    public void WithNoRecordASwingStillPlaysTheAttackClip()
    {
        var preview = Preview(Array.Empty<AttackSpell>());
        preview.ControlMode = true;
        preview.OrderAttack(new Vector3(100f, 0f, 0f));
        Tick(preview);
        Assert.NotNull(preview.SelectedAction);
        Assert.Equal(CharacterActionKind.Attack, preview.SelectedAction!.Action.Kind);
        preview.StopControl();
    }

    [Fact]
    public void AnOutOfRangeTargetIsWalkedToBeforeAnySwing()
    {
        var preview = Preview(new[] { Attack("XBasicAttack", null, 11f) });
        preview.MoveSpeed = 1;                                 // it will not get there during the test
        preview.ControlMode = true;
        preview.OrderAttack(new Vector3(5000f, 0f, 0f));
        Tick(preview);
        Assert.DoesNotContain("XBasicAttack", preview.ControlStatus);
        preview.StopControl();
    }
}
