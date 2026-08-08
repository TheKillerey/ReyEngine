using ReyEngine.Formats.MapGeo;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M394: the environment crossfade's timing. Pure logic, no renderer — both viewports read the same
/// Interp, so getting this wrong desyncs them identically rather than visibly, which is why it is pinned
/// here rather than checked by eye.
/// </summary>
public class MapStateTransitionTests
{
    private const string Base = "grasstint_srx.tex";
    private const string Infernal = "grasstint_srx_infernal.tex";
    private const string Ocean = "grasstint_srx_ocean.tex";

    private static MapStateTransition AtBase()
    {
        var t = new MapStateTransition();
        t.ResetTo(Base);
        return t;
    }

    [Fact]
    public void AFreshTransitionShowsWhatItWasResetTo()
    {
        var t = AtBase();
        Assert.Equal(Base, t.SettledPath);
        Assert.False(t.IsRunning);
    }

    // ---- Riot's durations ----

    /// <summary>Infernal authors TransitionTime 8, so the fade takes 8 seconds and is half done at 4.</summary>
    [Fact]
    public void TheFadeFollowsRiotsAuthoredDuration()
    {
        var t = AtBase();
        Assert.True(t.Begin(Infernal, 8f));
        Assert.Equal(0f, t.Interp);
        Assert.True(t.IsRunning);

        t.Advance(4f);
        Assert.Equal(0.5f, t.Interp, 3);

        t.Advance(4f);
        Assert.Equal(1f, t.Interp, 3);
        Assert.False(t.IsRunning);
        Assert.Equal(Infernal, t.SettledPath);
    }

    /// <summary>Hextech and Chemtech author NO TransitionTime. That means instant - not "use a default".
    /// Interp is 1 immediately so a renderer reading only Interp lands on the right texture.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0f)]
    public void AStateWithNoAuthoredTimeIsInstant(float? duration)
    {
        var t = AtBase();
        t.Begin(Infernal, duration);
        Assert.False(t.IsRunning);
        Assert.Equal(1f, t.Interp);
        Assert.Equal(Infernal, t.SettledPath);
    }

    [Fact]
    public void InterpNeverLeavesZeroToOne()
    {
        var t = AtBase();
        t.Begin(Infernal, 4f);
        t.Advance(100f);
        Assert.Equal(1f, t.Interp);
        Assert.Equal(4f, t.Elapsed);      // clamped, not accumulated past the end
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void NonPositiveDeltasDoNotRewind(float dt)
    {
        var t = AtBase();
        t.Begin(Infernal, 8f);
        t.Advance(2f);
        float before = t.Interp;
        t.Advance(dt);
        Assert.Equal(before, t.Interp, 5);
    }

    // ---- interruption: clicking through dragon states ----

    /// <summary>The behaviour worth having: interrupting mid-fade continues from what is ON SCREEN.
    /// Restarting from the original base would jump backwards visibly.</summary>
    [Fact]
    public void InterruptingMidFadeStartsFromWhatIsCurrentlyShowing()
    {
        var t = AtBase();
        t.Begin(Infernal, 8f);
        t.Advance(4f);                    // half way to Infernal

        t.Begin(Ocean, 9f);
        Assert.Equal(Infernal, t.FromPath);   // NOT Base
        Assert.Equal(Ocean, t.ToPath);
        Assert.Equal(0f, t.Interp);
    }

    [Fact]
    public void ReturningToBaseMidFadeIsItselfAFade()
    {
        var t = AtBase();
        t.Begin(Infernal, 8f);
        t.Advance(8f);
        t.Settle();

        Assert.True(t.Begin(Base, 8f));
        Assert.Equal(Infernal, t.FromPath);
        Assert.Equal(Base, t.ToPath);
    }

    // ---- no-op detection ----

    /// <summary>Re-selecting the state already showing must not restart a fade — the visibility hook can
    /// fire for unrelated reasons (Baron pit, layer toggles) and each one would otherwise re-fade.</summary>
    [Fact]
    public void BeginningATransitionToTheCurrentTextureIsANoOp()
    {
        var t = AtBase();
        Assert.False(t.Begin(Base, 8f));
        Assert.False(t.IsRunning);
    }

    /// <summary>But re-targeting the state a RUNNING fade is already heading to must not be dropped: the
    /// fade is not there yet, so it still needs to be reissued from the current point.</summary>
    [Fact]
    public void RetargetingTheDestinationOfARunningFadeIsNotANoOp()
    {
        var t = AtBase();
        t.Begin(Infernal, 8f);
        t.Advance(2f);
        Assert.True(t.Begin(Infernal, 8f));
    }

    [Fact]
    public void SettleCollapsesToTheIncomingTexture()
    {
        var t = AtBase();
        t.Begin(Infernal, 8f);
        t.Advance(8f);
        t.Settle();

        Assert.Equal(Infernal, t.FromPath);
        Assert.Equal(Infernal, t.SettledPath);
        Assert.False(t.IsRunning);
    }

    /// <summary>Opening a different map must not fade from the old map's texture.</summary>
    [Fact]
    public void ResetToDropsAnyRunningFade()
    {
        var t = AtBase();
        t.Begin(Infernal, 8f);
        t.Advance(3f);
        t.ResetTo("other_map_tint.tex");

        Assert.False(t.IsRunning);
        Assert.Equal("other_map_tint.tex", t.SettledPath);
        Assert.Equal("other_map_tint.tex", t.FromPath);
    }

    [Fact]
    public void ANullDestinationIsHandled()
    {
        var t = AtBase();
        Assert.True(t.Begin(null, 8f));
        t.Advance(8f);
        Assert.Null(t.SettledPath);
    }
}
