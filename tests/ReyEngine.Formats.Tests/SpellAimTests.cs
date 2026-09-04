using System.Numerics;
using ReyEngine.Formats.Characters;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M637: a cast is aimed from the spell's authored targeting kind and the cursor, the way the game aims
/// it. Pure rules, so every kind is pinned here without an install.
/// </summary>
public sealed class SpellAimTests
{
    private static readonly Vector3 Caster = new(1000f, 50f, 1000f);

    private static AbilitySlot Slot(string kind, float range, float radius = 0f) =>
        new(0, "Q", "x/y", 0f, false, 0f, Array.Empty<AbilityMissile>())
        { TargetingKind = kind, CastRange = range, CastRadius = radius };

    [Fact]
    public void ASelfCastNeedsNoAimAndFlightsNothing()
    {
        var plan = SpellAim.Plan(Slot("SelfAoe", 25000f, 550f), Caster, Caster + new Vector3(900f, 0f, 0f), dummy: null);
        Assert.Equal(Caster, plan.Aim);
        Assert.Null(plan.TravelTo);
        Assert.Null(plan.WalkTo);
        Assert.True(plan.IsSelfCast);
    }

    [Fact]
    public void ASelfAoeHitsTheDummyOnlyInsideItsRadius()
    {
        var near = SpellAim.Plan(Slot("SelfAoe", 25000f, 550f), Caster, Caster, Caster + new Vector3(500f, 0f, 0f));
        var far = SpellAim.Plan(Slot("SelfAoe", 25000f, 550f), Caster, Caster, Caster + new Vector3(700f, 0f, 0f));
        Assert.NotNull(near.HitAt);
        Assert.Null(far.HitAt);
    }

    [Fact]
    public void ADirectionCastFliesTheRangeAlongTheCursorNotToTheCursor()
    {
        // Ezreal Q: direction, 1200. Cursor 300 units away along +X; the bolt still flies 1200.
        var plan = SpellAim.Plan(Slot("direction", 1200f, 60f), Caster, Caster + new Vector3(300f, 0f, 0f), dummy: null);
        Assert.NotNull(plan.TravelTo);
        Assert.Equal(1200f, Vector3.Distance(new Vector3(Caster.X, 0f, Caster.Z), new Vector3(plan.TravelTo!.Value.X, 0f, plan.TravelTo.Value.Z)), 2);
        Assert.Equal(Caster.X + 1200f, plan.TravelTo.Value.X, 2);
        Assert.Null(plan.HitAt);
        Assert.Null(plan.WalkTo);
    }

    [Fact]
    public void ADirectionCastHitsTheDummyOnItsLaneAndMissesBesideIt()
    {
        var slot = Slot("direction", 1200f, 60f);
        var cursor = Caster + new Vector3(500f, 0f, 0f);
        var onLane = SpellAim.Plan(slot, Caster, cursor, Caster + new Vector3(800f, 0f, 40f));
        var beside = SpellAim.Plan(slot, Caster, cursor, Caster + new Vector3(800f, 0f, 400f));
        var beyond = SpellAim.Plan(slot, Caster, cursor, Caster + new Vector3(1500f, 0f, 0f));
        Assert.NotNull(onLane.HitAt);
        Assert.Null(beside.HitAt);
        Assert.Null(beyond.HitAt);
    }

    [Fact]
    public void AnUnboundedDirectionCastFliesTheArenaConstant()
    {
        // Aatrox Q authors 25000: "aim anywhere". The arena flies UnboundedFlight and says so.
        var plan = SpellAim.Plan(Slot("direction", 25000f), Caster, Caster + new Vector3(0f, 0f, 10f), dummy: null);
        Assert.Equal(SpellAim.UnboundedFlight, Vector3.Distance(new Vector3(Caster.X, 0f, Caster.Z), new Vector3(plan.TravelTo!.Value.X, 0f, plan.TravelTo.Value.Z)), 2);
        Assert.Contains("unbounded", plan.Note);
    }

    [Fact]
    public void AClampedLocationCastIsPulledBackToTheRange()
    {
        // Aatrox W: LocationClamped, 825. Cursor 1500 away lands the chain 825 away on the same bearing.
        var plan = SpellAim.Plan(Slot("LocationClamped", 825f), Caster, Caster + new Vector3(0f, 0f, 1500f), dummy: null);
        Assert.Equal(Caster.Z + 825f, plan.Aim.Z, 2);
        Assert.Equal(Caster.X, plan.Aim.X, 2);
        Assert.Equal(plan.Aim, plan.TravelTo);
        Assert.Equal(plan.Aim, plan.HitAt);
        Assert.Null(plan.WalkTo);
        Assert.Contains("clamped", plan.Note);
    }

    [Fact]
    public void APlainLocationCastBeyondRangeWalksIntoRangeFirst()
    {
        var target = Caster + new Vector3(1500f, 0f, 0f);
        var plan = SpellAim.Plan(Slot("Location", 800f), Caster, target, dummy: null);
        Assert.NotNull(plan.WalkTo);
        Assert.Null(plan.TravelTo);
        // The walk stops inside the range, on the line toward the target.
        float remaining = Vector3.Distance(new Vector3(plan.WalkTo!.Value.X, 0f, plan.WalkTo.Value.Z), new Vector3(target.X, 0f, target.Z));
        Assert.True(remaining < 800f, $"still {remaining:0} from the target after the walk");
        Assert.Equal(Caster.Z, plan.WalkTo.Value.Z, 2);
    }

    [Fact]
    public void ALocationCastInRangeLandsWhereTheCursorIsAndHitsADummyStandingThere()
    {
        var cursor = Caster + new Vector3(300f, 0f, 200f);
        var plan = SpellAim.Plan(Slot("Location", 25000f, 285f), Caster, cursor, cursor + new Vector3(100f, 0f, 0f));
        Assert.Equal(cursor, plan.Aim);
        Assert.Equal(cursor, plan.TravelTo);
        Assert.Equal(cursor + new Vector3(100f, 0f, 0f), plan.HitAt);   // inside the 285 radius
        var miss = SpellAim.Plan(Slot("Location", 25000f, 100f), Caster, cursor, cursor + new Vector3(400f, 0f, 0f));
        Assert.Equal(cursor, miss.HitAt);                                 // nothing there: the hit plays at the ground point
    }

    [Fact]
    public void ATargetCastTakesTheDummyInRangeAndWalksWhenItIsNot()
    {
        var near = SpellAim.Plan(Slot("Target", 600f), Caster, Caster, Caster + new Vector3(400f, 0f, 0f));
        Assert.Equal(Caster + new Vector3(400f, 0f, 0f), near.Aim);
        Assert.NotNull(near.HitAt);
        Assert.Null(near.WalkTo);

        var far = SpellAim.Plan(Slot("Target", 600f), Caster, Caster, Caster + new Vector3(1400f, 0f, 0f));
        Assert.NotNull(far.WalkTo);
        Assert.Null(far.TravelTo);

        var none = SpellAim.Plan(Slot("Target", 600f), Caster, Caster + new Vector3(100f, 0f, 0f), dummy: null);
        Assert.True(none.IsSelfCast);
        Assert.Contains("needs a unit", none.Note);
    }

    [Fact]
    public void TargetOrLocationPrefersAUnitUnderTheCursorAndFallsBackToTheGround()
    {
        var dummy = Caster + new Vector3(500f, 0f, 0f);
        var onUnit = SpellAim.Plan(Slot("TargetOrLocation", 900f), Caster, dummy + new Vector3(20f, 0f, 0f), dummy);
        Assert.Equal(dummy, onUnit.HitAt);

        var ground = SpellAim.Plan(Slot("TargetOrLocation", 900f), Caster, Caster + new Vector3(0f, 0f, 400f), dummy);
        Assert.Equal(Caster + new Vector3(0f, 0f, 400f), ground.Aim);
    }

    [Fact]
    public void NoTargetingDataStillCastsAtTheCursorRatherThanRefusing()
    {
        var plan = SpellAim.Plan(null, Caster, Caster + new Vector3(0f, 0f, 300f), dummy: null);
        Assert.Equal(Caster + new Vector3(0f, 0f, 300f), plan.Aim);
        Assert.Contains("no targeting data", plan.Note);
    }
}
