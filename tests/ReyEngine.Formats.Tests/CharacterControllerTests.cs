using System.Numerics;
using ReyEngine.Formats.Characters;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M613: click-to-move and click-to-attack.
///
/// <para>Every failure here is one you would otherwise chase in a viewport: overshooting on a long
/// frame, spinning to face a target underfoot, a swing cadence that drifts slower the worse the frame
/// rate gets. All of them are arithmetic, so all of them are asserted rather than eyeballed.</para>
/// </summary>
public sealed class CharacterControllerTests
{
    private static CharacterController Walker(float speed = 100f) =>
        new() { MoveSpeed = speed, TurnSpeed = 1000f };   // instant turning unless a test says otherwise

    /// <summary>Run for a while at a fixed step, returning how many attacks were started.</summary>
    private static int Run(CharacterController c, float seconds, float step = 1f / 60f)
    {
        int swings = 0;
        for (float t = 0; t < seconds; t += step)
            if (c.Tick(step).StartedAttack) swings++;
        return swings;
    }

    // ===================================================== moving

    [Fact]
    public void ClickingTheGroundWalksThereAndStops()
    {
        var c = Walker();
        c.MoveTo(new Vector3(500, 0, 0));

        Run(c, 10f);

        Assert.Equal(CharacterStance.Idle, c.Stance);
        Assert.Equal(500f, c.Position.X, 1);
        Assert.Null(c.Destination);
    }

    [Fact]
    public void ALongFrameDoesNotOvershootTheDestination()
    {
        // The classic: a 2-second hitch at 340 units/s teleports the character 680 units past the click.
        var c = Walker(speed: 340f);
        c.MoveTo(new Vector3(100, 0, 0));

        c.Tick(5f);

        Assert.Equal(100f, c.Position.X, 1);
        Assert.Equal(0f, c.Position.Z, 1);
        Assert.Equal(CharacterStance.Moving, c.Stance);   // arrival is confirmed on the next tick
        c.Tick(1f / 60f);
        Assert.Equal(CharacterStance.Idle, c.Stance);
    }

    [Fact]
    public void MovingNeverChangesHeight()
    {
        // A click on sloped ground must not lift or sink the character - the preview has no terrain to
        // stand on, and a Y that drifts is invisible until the model is halfway through the floor.
        var c = Walker();
        c.Teleport(new Vector3(0, 25, 0));
        c.MoveTo(new Vector3(300, -400, 300));

        Run(c, 10f);

        Assert.Equal(25f, c.Position.Y, 3);
    }

    [Fact]
    public void ANewClickReplacesTheOldDestination()
    {
        var c = Walker();
        c.MoveTo(new Vector3(1000, 0, 0));
        Run(c, 0.5f);
        c.MoveTo(new Vector3(0, 0, 1000));
        Run(c, 30f);

        Assert.Equal(1000f, c.Position.Z, 1);
        Assert.Equal(CharacterStance.Idle, c.Stance);
    }

    // ===================================================== facing

    [Fact]
    public void TheCharacterFacesWhereItIsWalking()
    {
        // Yaw 0 is +Z. Measured in the viewport: with the -Z convention, left and right aimed correctly
        // and forward and back aimed exactly backwards - which only a wrong sign on Z can produce.
        var c = Walker();
        c.MoveTo(new Vector3(0, 0, 500));
        c.Tick(0.1f);
        Assert.Equal(0f, c.Facing, 2);

        // Teleport back first: measuring the second heading from wherever the first leg happened to
        // leave the character measures the walk, not the facing.
        c.Teleport(Vector3.Zero);
        c.MoveTo(new Vector3(500, 0, 0));     // to the right is +X, a quarter turn
        c.Tick(0.1f);
        Assert.Equal(MathF.PI / 2f, c.Facing, 2);

        c.Teleport(Vector3.Zero);
        c.MoveTo(new Vector3(0, 0, -500));    // behind, half a turn
        c.Tick(0.1f);
        Assert.Equal(MathF.PI, MathF.Abs(c.Facing), 2);

        c.Teleport(Vector3.Zero);
        c.MoveTo(new Vector3(-500, 0, 0));    // and left is the other quarter
        c.Tick(0.1f);
        Assert.Equal(-MathF.PI / 2f, c.Facing, 2);
    }

    [Fact]
    public void TurningTakesTheShortWayRound()
    {
        // Turning 350 degrees to end up 10 degrees away is the same visual bug as a rotation that slerps
        // the long way: correct final pose, absurd path.
        var c = new CharacterController { MoveSpeed = 100f, TurnSpeed = 1f };
        c.Teleport(Vector3.Zero, facing: 3.0f);            // just under pi
        c.MoveTo(new Vector3(0, 0, -500));                 // yaw pi: a short turn away from 3.0

        c.Tick(0.05f);

        Assert.True(c.Facing > 3.0f, $"expected to keep turning the short way, got {c.Facing}");
        Assert.InRange(c.Facing, -MathF.PI, MathF.PI);
    }

    [Fact]
    public void ATargetUnderfootDoesNotSpinTheModel()
    {
        // Zero direction has no angle. Anything that normalises it produces NaN and the model vanishes.
        var c = Walker();
        c.Teleport(new Vector3(100, 0, 100), facing: 1.25f);
        c.Attack(new Vector3(100, 0, 100));

        Run(c, 1f);

        Assert.Equal(1.25f, c.Facing, 3);
        Assert.False(float.IsNaN(c.Position.X));
    }

    [Fact]
    public void FacingStaysWithinASingleTurn()
    {
        var c = new CharacterController { MoveSpeed = 100f, TurnSpeed = 50f };
        for (int i = 0; i < 40; i++)
        {
            c.MoveTo(new Vector3(MathF.Sin(i) * 500f, 0, MathF.Cos(i) * 500f));
            Run(c, 0.2f);
            Assert.InRange(c.Facing, -MathF.PI, MathF.PI);
        }
    }

    // ===================================================== attacking

    [Fact]
    public void ClickingATargetWalksIntoRangeThenAttacks()
    {
        var c = Walker(speed: 200f);
        c.AttackRange = 150f;
        c.Attack(new Vector3(1000, 0, 0));

        c.Tick(0.1f);
        Assert.Equal(CharacterStance.Moving, c.Stance);

        Run(c, 10f);
        Assert.Equal(CharacterStance.Attacking, c.Stance);
        // It stops AT range rather than walking on top of the target.
        Assert.InRange(1000f - c.Position.X, 140f, 160f);
    }

    [Fact]
    public void AnAttackFiresOnceEachSwingNotOncePerFrame()
    {
        var c = Walker();
        c.AttackRange = 500f;
        c.AttacksPerSecond = 2f;
        c.Attack(new Vector3(100, 0, 0));

        int swings = Run(c, 5f);

        // 2/s for 5s: the first lands immediately, so 10 or 11 depending on where the last step falls.
        Assert.InRange(swings, 10, 11);
    }

    [Fact]
    public void TheSwingCadenceDoesNotDriftOnBadFrames()
    {
        // Resetting the timer to a full interval each swing loses whatever the frame overshot by, so a
        // slow machine attacks measurably slower. Carrying the remainder keeps the rate honest.
        var fast = Walker();
        var slow = Walker();
        foreach (var c in new[] { fast, slow }) { c.AttackRange = 500f; c.AttacksPerSecond = 3f; }

        fast.Attack(new Vector3(50, 0, 0));
        slow.Attack(new Vector3(50, 0, 0));

        int fastSwings = Run(fast, 4f, step: 1f / 120f);
        int slowSwings = Run(slow, 4f, step: 1f / 7f);      // a miserable 7 fps

        Assert.InRange(slowSwings, fastSwings - 1, fastSwings + 1);
    }

    [Fact]
    public void MovingAwayFromAnAttackTargetResumesTheApproach()
    {
        var c = Walker(speed: 300f);
        c.AttackRange = 100f;
        c.Attack(new Vector3(200, 0, 0));
        Run(c, 5f);
        Assert.Equal(CharacterStance.Attacking, c.Stance);

        c.MoveTo(new Vector3(-800, 0, 0));      // walk away: the attack order is dropped
        Run(c, 0.2f);
        Assert.Equal(CharacterStance.Moving, c.Stance);
        Assert.Null(c.Target);
    }

    [Fact]
    public void StoppingClearsBothOrders()
    {
        var c = Walker();
        c.Attack(new Vector3(900, 0, 0));
        c.Tick(0.1f);
        c.Stop();

        Assert.Null(c.Target);
        Assert.Null(c.Destination);
        Assert.Equal(CharacterStance.Idle, c.Tick(0.1f).Stance);
    }

    // ===================================================== the tick report

    [Fact]
    public void TheStanceChangeIsReportedOnceRatherThanEveryFrame()
    {
        // The caller switches animation clip on this flag. If it were true every frame the walk cycle
        // would restart 60 times a second and never visibly animate.
        var c = Walker();
        c.MoveTo(new Vector3(10000, 0, 0));

        Assert.True(c.Tick(1f / 60f).StanceChanged);
        for (int i = 0; i < 30; i++) Assert.False(c.Tick(1f / 60f).StanceChanged);
    }

    [Fact]
    public void AZeroLengthFrameChangesNothing()
    {
        var c = Walker();
        c.MoveTo(new Vector3(500, 0, 0));
        c.Tick(0.5f);
        var before = c.Position;

        var tick = c.Tick(0f);

        Assert.Equal(before, c.Position);
        Assert.False(tick.StanceChanged);
        Assert.False(tick.StartedAttack);
    }
}
