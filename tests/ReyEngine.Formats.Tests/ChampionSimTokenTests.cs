using System.Numerics;
using ReyEngine.Formats.Skeletons;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>M875: where a name-matched composite system plays, and which alternatives are dropped.</summary>
public sealed class ChampionSimTokenTests
{
    private static Dictionary<uint, VfxSystemDefinition> Skin(params string[] names)
    {
        var emitter = new VfxEmitterDefinition("e", VfxCurveF.Const(1), VfxCurveF.Const(0.25f),
            0.2f, 0, 0, true, false, 3, VfxCurve3.Const(new Vector3(50)), null,
            VfxCurve4.Const(Vector4.One), null, null, null, null, VfxCurve3.Const(Vector3.Zero),
            "assets/test/white.dds", Vector2.One, 1, false, false);
        var d = new Dictionary<uint, VfxSystemDefinition>();
        uint h = 1;
        foreach (var n in names) d[h] = new VfxSystemDefinition(h++, n, "", new[] { emitter });
        return d;
    }

    private static ChampionEvent EventOf(Dictionary<uint, VfxSystemDefinition> s, string spell,
        IReadOnlyDictionary<string, IReadOnlyList<uint>>? authored = null)
        => ChampionEventBuilder.Build(s, Array.Empty<AnimClipInfo>(), authored).First(e => e.Name == spell);

    private static List<string> Names(Dictionary<uint, VfxSystemDefinition> s, IEnumerable<uint> hs) => hs.Select(h => s[h].Name).ToList();

    [Fact]
    public void LocationTargetStateAndCasterTokensSplit()
    {
        var s = Skin("Diana_Base_Q_End", "Diana_Base_Q_Moonlight", "Diana_Base_Q_Moonlight_Self", "Diana_Base_Q_Swipe_01",
            "Diana_Base_Q_Tar", "Diana_Base_Q_Trail");
        var ev = EventOf(s, "Q");
        Assert.Equal(new[] { "Diana_Base_Q_End" }, Names(s, ev.AimSystems));
        Assert.Equal(new[] { "Diana_Base_Q_Moonlight", "Diana_Base_Q_Moonlight_Self", "Diana_Base_Q_Tar" }, Names(s, ev.TargetSystems).OrderBy(n => n));
        Assert.Contains("Diana_Base_Q_Swipe_01", Names(s, ev.CasterSystems));
        Assert.DoesNotContain("Diana_Base_Q_End", Names(s, ev.CasterSystems));
    }

    [Fact]
    public void AlternativesAndTimedStatesAreDroppedAndListed()
    {
        var s = Skin("Fiora_Base_R_Mark_NE", "Fiora_Base_R_Mark_NW", "Fiora_Base_R_Mark_NE_FioraOnly", "Fiora_Base_R_NE_Timeout",
            "Fiora_Base_R_Heal_Zone", "Fiora_Base_R_indicator_ring", "Jhin_Base_R_Trap_activate", "Jhin_Base_R_Trap_Activate_enemy",
            "Fiora_Base_R_Pulse_Ally", "Fiora_Base_R_Pulse", "Fiora_Base_R_Ring_Blue", "Fiora_Base_R_Ring_Red");
        var ev = EventOf(s, "R");
        var played = Names(s, ev.CasterSystems.Concat(ev.TargetSystems).Concat(ev.AimSystems));
        Assert.Contains("Fiora_Base_R_Mark_NE", played);
        Assert.DoesNotContain("Fiora_Base_R_Mark_NW", played);
        Assert.DoesNotContain("Fiora_Base_R_Mark_NE_FioraOnly", played);
        Assert.DoesNotContain("Fiora_Base_R_NE_Timeout", played);
        Assert.DoesNotContain("Fiora_Base_R_indicator_ring", played);
        Assert.DoesNotContain("Jhin_Base_R_Trap_Activate_enemy", played);
        Assert.Contains("Jhin_Base_R_Trap_activate", Names(s, ev.AimSystems));
        Assert.DoesNotContain("Fiora_Base_R_Pulse_Ally", played);
        Assert.Contains("Fiora_Base_R_Ring_Blue", played);
        Assert.DoesNotContain("Fiora_Base_R_Ring_Red", played);
        Assert.Contains("Fiora_Base_R_Heal_Zone", Names(s, ev.AimSystems));
        Assert.Contains(ev.DroppedSystemNames, n => n.StartsWith("Fiora_Base_R_Mark_NW (quadrant"));
        Assert.Contains(ev.DroppedSystemNames, n => n.StartsWith("Fiora_Base_R_indicator_ring (timed"));
    }

    [Fact]
    public void UnresolvedAuthoredMissileDoesNotHideNameMatchedMissile()
    {
        var s = Skin("Lux_Base_R_cas", "Lux_Base_R_mis_beam", "Lux_Base_R_tar");
        var authored = new Dictionary<string, IReadOnlyList<uint>> { ["R"] = new uint[] { 999 } };   // not a system of this skin
        var ev = EventOf(s, "R", authored);
        Assert.Equal(new[] { "Lux_Base_R_mis_beam" }, Names(s, ev.MissileSystems));
    }

    [Fact]
    public void PassiveKeepsItsSelfStatesOnTheCasterAndPutsTheStatesOfTheEnemyOnTheTarget()
    {
        // M903: a passive's own effects stay with the caster (M873) - except the ones whose name says they are on the enemy (a debuff, a mark)
        var s = Skin("Lux_Base_P_debuff", "Lux_Base_P_tar", "Lux_Base_P_Idle_glow", "Lux_Base_P_buf");
        var ev = EventOf(s, "P");
        Assert.Equal(new[] { "Lux_Base_P_buf" }, Names(s, ev.CasterSystems));
        Assert.Equal(new[] { "Lux_Base_P_debuff", "Lux_Base_P_tar" }, Names(s, ev.TargetSystems).OrderBy(n => n, StringComparer.Ordinal));
    }
}
