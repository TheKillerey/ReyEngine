using System.Text;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Characters.Simulation;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M867: the champion simulation definition, generated from the installed game's real champion WADs (League 16.22 at the time of
/// writing). Skipped with a message when the install is missing. These pin what the data says, not what we would like it to say:
/// every expectation below was measured on the shipped files before it was asserted.
/// </summary>
public sealed class ChampionSimTests(ITestOutputHelper output)
{
    private const string Game = @"C:\Riot Games\League of Legends\Game";
    private static readonly string Champions = Path.Combine(Game, "DATA", "FINAL", "Champions");

    private static readonly Lazy<(HashDatabase Db, WadPathResolver Resolver)> Hashes = new(() =>
    {
        var db = new HashSyncService().LoadLocal(_ => { });
        return (db, new WadPathResolver(db));
    });

    private static bool Installed => Directory.Exists(Champions);

    private static ChampionSimContext Context() => new()
    {
        GameVersion = ChampionSimSource.ReadGameVersion(Game),
        ResolveBinName = h => Hashes.Value.Db.TryGetBinName(h, out var n) ? n : null,
        ResolveWadPath = h => Hashes.Value.Db.TryGetPath(h, out var p) ? p : null,
    };

    private static ChampionSimDefinition Generate(string champion)
    {
        using var wad = WadArchive.Open(ChampionSimBatch.WadPathOf(Champions, champion), Hashes.Value.Resolver);
        return ChampionSimGenerator.Generate(wad, champion, Context());
    }

    private bool Skip()
    {
        if (Installed) return false;
        output.WriteLine("SKIPPED: League of Legends is not installed at " + Game);
        return true;
    }

    private static AbilitySim Ability(ChampionSimDefinition d, string key) => Assert.Single(d.Abilities, a => a.Key == key);

    [Fact]
    public void Kalista_has_passive_and_four_slots_and_the_Q_missile_resolves_to_its_system()
    {
        if (Skip()) return;
        var d = Generate("Kalista");

        Assert.Equal(new[] { "P", "Q", "W", "E", "R" }, d.Abilities.Where(a => a.ParentKey is null).Select(a => a.Key).Take(5));
        // the passive is the record's mCharacterPassiveSpell, not a name guess
        Assert.Equal("KalistaPassiveBuff", Ability(d, "P").ScriptName);
        Assert.Equal(4, Ability(d, "P").Slot);
        Assert.Equal("KalistaMysticShot", Ability(d, "Q").ScriptName);

        // measured: the Q missile spell KalistaMysticShotMisTrue authors effect key Kalista_Q_mis (0xf331b768), which the skin's
        // resource map resolves to the system Kalista_Base_Q_mis
        var q = Ability(d, "Q");
        var mis = Assert.Single(q.Missiles, m => m.SpellName == "KalistaMysticShotMisTrue");
        Assert.Equal("0xf331b768", mis.Effect!.KeyHash);
        Assert.Equal("Kalista_Base_Q_mis", mis.Effect.Name);
        Assert.True(mis.Effect.Visual);
        Assert.Equal("ConstantSpeed", mis.MotionKind);
        Assert.Equal(2400f, mis.Speed);
        Assert.Equal("authored", mis.Source);

        // her casts name Spell1/Spell3/Spell4_Call; the base graph has Spell1_Base and friends, found by the documented ladder
        Assert.Equal("Spell1", q.AuthoredClipName);
        Assert.NotNull(q.Clip);
        Assert.StartsWith("Spell1", q.Clip!.Name);
        Assert.Contains(Ability(d, "R").Missiles, m => m.SpellName == "KalistaRMis" && m.Effect?.Name == "Kalista_Base_R_aestheticmis");
        // affects flags are carried raw
        Assert.Equal(13327u, q.AffectsTypeFlags);
        Assert.All(d.Abilities, a => Assert.False(a.Scenario.NeedsAlly));   // never set without an adapter
    }

    [Fact]
    public void Ezreal_Q_missile_speed_is_the_authored_2000()
    {
        if (Skip()) return;
        var q = Ability(Generate("Ezreal"), "Q");
        var m = Assert.Single(q.Missiles);
        Assert.Equal("ConstantSpeed", m.MotionKind);
        Assert.Equal(2000f, m.Speed);
        Assert.Equal("mMissileSpec", m.MotionSource);
        Assert.Equal("Ezreal_Base_Q_mis", m.Effect!.Name);
        Assert.Equal("direction", q.TargetingKind);
        Assert.Equal(1200f, q.CastRange);
    }

    [Fact]
    public void Ahri_Q_has_an_outbound_and_a_return_missile()
    {
        if (Skip()) return;
        var q = Ability(Generate("Ahri"), "Q");
        var outbound = Assert.Single(q.Missiles, m => m.SpellName == "AhriQMissile");
        var back = Assert.Single(q.Missiles, m => m.SpellName == "AhriQReturnMissile");
        Assert.Equal("Ahri_Base_Q_mis", outbound.Effect!.Name);
        Assert.NotNull(back.Effect?.Name);
        Assert.NotEqual(outbound.Effect.Name, back.Effect!.Name);
        Assert.NotNull(q.Clip);
    }

    [Fact]
    public void Aatrox_Q_has_no_root_clip_and_three_sub_casts_with_their_own()
    {
        if (Skip()) return;
        var d = Generate("Aatrox");
        var q = Ability(d, "Q");
        Assert.Null(q.AuthoredClipName);
        Assert.Null(q.Clip);
        for (int n = 1; n <= 3; n++)
        {
            var sub = Ability(d, "Q" + n);
            Assert.Equal("Q", sub.ParentKey);
            Assert.NotNull(sub.Clip);
            Assert.Equal("convention", sub.Clip!.Source);
            Assert.Contains(sub.CasterSystems, s => s.Name == $"Aatrox_Base_Q_cas{n}");
        }
        // judged through its sub-casts, the wrapper is not Blocked
        var verdict = ChampionSimValidator.Validate(d);
        Assert.NotEqual(SimStatus.Blocked, verdict.Abilities.Single(a => a.Key == "Q").Status);
    }

    [Fact]
    public void Talon_E_is_present_with_its_targeting_kind()
    {
        if (Skip()) return;
        var e = Ability(Generate("Talon"), "E");
        Assert.Equal("TalonE", e.ScriptName);
        Assert.Equal("Location", e.TargetingKind);
        Assert.Equal(6000f, e.CastRange);
    }

    [Theory]
    [InlineData("Kalista")]
    [InlineData("Aatrox")]
    [InlineData("Ahri")]
    [InlineData("Ezreal")]
    public void Json_round_trip_is_byte_identical_and_generation_is_deterministic(string champion)
    {
        if (Skip()) return;
        var first = ChampionSimJson.Write(Generate(champion));
        var second = ChampionSimJson.Write(Generate(champion));
        Assert.Equal(first, second);                                   // two runs, same bytes

        var back = ChampionSimJson.Read(first);
        Assert.Equal(first, ChampionSimJson.Write(back));              // read then write, same bytes
        Assert.Equal(ChampionSimDefinition.CurrentSchemaVersion, back.SchemaVersion);
        Assert.DoesNotContain((byte)'\r', first);
        Assert.NotEqual(0xEF, first[0]);                               // no byte-order mark
    }

    [Fact]
    public void A_champion_is_never_complete_before_runtime_validation_exists()
    {
        if (Skip()) return;
        var verdict = ChampionSimValidator.Validate(Generate("Ezreal"));
        Assert.Equal(SimStatus.Incomplete, verdict.Status);
        Assert.Contains(verdict.Reasons, r => r.Contains("runtime and visual validation pending M869/M871"));
        Assert.Contains(verdict.Abilities, a => a.Status == SimStatus.Complete);   // measured: his Q passes every asset check
    }

    [Fact]
    public void Roster_skips_locale_wads_and_the_TFT_stub()
    {
        if (Skip()) return;
        var roster = ChampionSimBatch.Roster(Champions);
        output.WriteLine($"{roster.Count} champion WADs");
        Assert.DoesNotContain("TFTChampion", roster);
        Assert.All(roster, n => Assert.DoesNotMatch(@"\.[a-z]{2}_[A-Z]{2}$", n));
        Assert.Contains("Kalista", roster);
        Assert.True(roster.Count >= 150);
        Assert.Equal(roster.OrderBy(n => n, StringComparer.Ordinal), roster);
    }

    // ---- validator logic on a hand-made definition (no install needed) ---------------------------------

    private static SimSystemRef Sys(string? name, bool? visual = true) => new("0x00000001", null, "0x00000002", name, visual);

    private static AbilitySim Make(string key = "Q", SimClip? clip = null, IReadOnlyList<MissileSim>? missiles = null,
        IReadOnlyList<SimSystemRef>? casters = null, float? cast = 0.25f, string? authored = null) => new(
        key, null, 0, "X", "X", "X", null, null, Array.Empty<string>(), null, null, "direction", 1000, 0, 0, Array.Empty<string>(), null,
        authored, clip, cast, null, null, missiles ?? Array.Empty<MissileSim>(), casters ?? Array.Empty<SimSystemRef>(),
        Array.Empty<SimSystemRef>(), Array.Empty<SimSystemRef>(), Array.Empty<ClipParticleEventSim>(), Array.Empty<ClipSoundEventSim>(),
        new ScenarioHint(false, false, false, null), Array.Empty<FieldSource>(), Array.Empty<string>());

    private static readonly SimClip Clip = new("Spell1", "a/b.anm", "authored", 30f, 1f, 1f / 30f, "anmFps");

    [Fact]
    public void Validator_grades_complete_incomplete_and_blocked()
    {
        var flying = new MissileSim("M", Sys("Sys_mis"), null, "ConstantSpeed", 2000f, null, "mMissileSpec", 60f, "authored");
        Assert.Equal(SimStatus.Complete, ChampionSimValidator.Validate(Make(clip: Clip, missiles: new[] { flying })).Status);

        // a missile with no resolvable system and no motion: still has a clip to show, so Incomplete
        var broken = new MissileSim("M", Sys(null, null), null, "Unknown", null, null, null, 60f, "authored");
        var incomplete = ChampionSimValidator.Validate(Make(clip: Clip, missiles: new[] { broken }));
        Assert.Equal(SimStatus.Incomplete, incomplete.Status);
        Assert.Contains(incomplete.Checks.Single(c => c.Name == "missiles").Codes!, c => c == "missile.effectUnresolved");
        Assert.Contains(incomplete.Checks.Single(c => c.Name == "missiles").Codes!, c => c == "missile.noMotion");

        // a spell that names a clip the graph lacks, with no effect at all: nothing to show
        var blocked = ChampionSimValidator.Validate(Make(authored: "Spell9"));
        Assert.Equal(SimStatus.Blocked, blocked.Status);
        Assert.Contains(blocked.Reasons, r => r.StartsWith("visible:"));

        // an invisible caster system is a failed check, and with a clip the ability is Incomplete, not Blocked
        var invisible = ChampionSimValidator.Validate(Make(clip: Clip, casters: new[] { Sys("Ghost", visual: false) }));
        Assert.Equal(SimStatus.Incomplete, invisible.Status);
    }

    [Fact]
    public void Json_is_camel_case_indented_and_lists_unresolved_references_explicitly()
    {
        var def = new ChampionSimDefinition(1, "t", "16.0", "X", null, null, "data/characters/x/skins/skin0.bin", null,
            new[] { Make(missiles: new[] { new MissileSim("M", Sys(null, null), null, "Unknown", null, null, null, 0f, "authored") }) },
            Array.Empty<AttackSim>(), new[] { "i" });
        string text = Encoding.UTF8.GetString(ChampionSimJson.Write(def));
        Assert.Contains("\"schemaVersion\": 1", text);
        Assert.Contains("\"name\": null", text);          // unresolved is null, not omitted
        Assert.Contains("\n  \"abilities\": [", text);    // two-space indent
        Assert.DoesNotContain("isResolved", text);
    }
}
