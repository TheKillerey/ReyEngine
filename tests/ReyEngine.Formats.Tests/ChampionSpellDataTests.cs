using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M631: the champion's own spell records, so the VFX composite can stop inventing its numbers.
///
/// <para>Asserted against the shipped roster, because the whole justification for this reader is a claim
/// about what Riot actually authors — a fixture would only prove it agrees with my idea of a spell.</para>
/// </summary>
public sealed class ChampionSpellDataTests
{
    private const string Champions = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Champions";
    private static bool Installed => Directory.Exists(Champions);

    private static readonly Lazy<HashDatabase?> Database = new(() =>
    {
        try { return new HashSyncService().LoadLocal(_ => { }); }
        catch { return null; }
    });

    private static IReadOnlyList<AbilitySlot> Read(string champion)
    {
        string wad = Path.Combine(Champions, champion + ".wad.client");
        if (!File.Exists(wad) || Database.Value is not { } database) return Array.Empty<AbilitySlot>();

        using var archive = WadArchive.Open(wad, new WadPathResolver(database));
        ulong hash = HashAlgorithms.WadPath(ChampionRecord.PathFor(champion.ToLowerInvariant()));
        return archive.TryGetEntry(hash, out _)
            ? ChampionSpellData.Read(archive.Extract(hash), champion)
            : Array.Empty<AbilitySlot>();
    }

    // ===================================================== shape

    [Fact]
    public void OnThisMachineTheReaderActuallyRuns()
    {
        // Every test below skips without the install; this turns a silent skip into a failure.
        if (!Installed) return;
        Assert.NotNull(Database.Value);
        Assert.NotEmpty(Read("Ezreal"));
    }

    [Fact]
    public void AChampionHasFourSlotsInQwerOrder()
    {
        var slots = Read("Ezreal");
        if (slots.Count == 0) return;

        Assert.Equal(4, slots.Count);
        Assert.Equal(new[] { "Q", "W", "E", "R" }, slots.Select(s => s.Slot).ToArray());
        Assert.All(slots, s => Assert.NotEmpty(s.SpellEntry));
    }

    [Fact]
    public void ANonChampionRecordYieldsNothingRatherThanGuessing()
    {
        // TFTChampion ships a CharacterRecord with no spells at all.
        Assert.Empty(ChampionSpellData.Read(new byte[] { 1, 2, 3, 4 }));
        Assert.Empty(ChampionSpellData.Read(Array.Empty<byte>()));
    }

    // ===================================================== the numbers the composite needed

    [Fact]
    public void MissileSpeedsAreTheChampionsOwnNotEighteenHundred()
    {
        // The point of the whole milestone. Exactly 1800 occurs in 14 of 463 authored speeds across the
        // roster - it was one champion's number generalised to everyone, and these three prove it.
        var ezreal = Read("Ezreal");
        if (ezreal.Count == 0) return;

        var q = ezreal[0].Missiles.FirstOrDefault(m => m.Motion.Kind == MissileMotionKind.ConstantSpeed);
        Assert.NotNull(q);
        Assert.Equal(2000f, q!.Motion.Value, 0);

        var ahri = Read("Ahri");
        if (ahri.Count == 4)
        {
            var r = ahri[3].Missiles.FirstOrDefault(m => m.Motion.Kind == MissileMotionKind.ConstantSpeed);
            Assert.NotNull(r);
            Assert.Equal(1400f, r!.Motion.Value, 0);
        }
    }

    [Fact]
    public void TravelTimeFollowsFromSpeedAndDistance()
    {
        var ezreal = Read("Ezreal");
        if (ezreal.Count == 0) return;

        var motion = ezreal[0].Missiles.First(m => m.Motion.Kind == MissileMotionKind.ConstantSpeed).Motion;
        Assert.Equal(0.5f, motion.SecondsFor(1000f)!.Value, 3);
        Assert.Equal(1.0f, motion.SecondsFor(2000f)!.Value, 3);
    }

    [Fact]
    public void AMissileWithNoAuthoredMotionSaysSoRatherThanReturningZero()
    {
        // 20 of 692 slots author neither a speed nor a flight time - the circle-movement family. Zero
        // would read as "arrives instantly", so the caller has to be able to tell and fall back.
        Assert.Null(MissileMotion.None.SecondsFor(1000f));
        Assert.Null(new MissileMotion(MissileMotionKind.ConstantSpeed, 0f).SecondsFor(1000f));
        Assert.Equal(0.4f, new MissileMotion(MissileMotionKind.FixedDuration, 0.4f).SecondsFor(9999f)!.Value, 3);
    }

    [Fact]
    public void CastTimingComesFromTheRecordAndCastFrameIsInFrames()
    {
        // Ezreal's Q casts on frame 1.98, which at 30 fps is 0.066 s - not the 0.15 s constant.
        var ezreal = Read("Ezreal");
        if (ezreal.Count == 0) return;

        var q = ezreal[0];
        Assert.True(q.CastFrame > 0f, "Ezreal Q authors a cast frame");
        Assert.Equal(q.CastFrame / 30f, q.CastSecondsAt(30f), 3);

        // And the rate is the spell's choice, not ours. Ezreal Q authors useAnimatorFramerate, so the
        // same frame arrives sooner on a faster clip - which is the whole reason the flag exists and the
        // reason a delay in SECONDS could never have been right for every champion.
        Assert.True(q.UseAnimatorFramerate, "Ezreal Q authors useAnimatorFramerate");
        Assert.Equal(q.CastFrame / 60f, q.CastSecondsAt(60f), 3);

        // A spell that does NOT author it is pinned to 30 fps however fast the clip runs.
        var pinned = new AbilitySlot(0, "Q", "x/y", CastFrame: 15f, UseAnimatorFramerate: false,
            CastTimeSeconds: 0f, Array.Empty<AbilityMissile>());
        Assert.Equal(0.5f, pinned.CastSecondsAt(60f), 3);
    }

    [Fact]
    public void AnAuthoredCastTimeBeatsTheFrameCount()
    {
        // Both are authored on some spells; seconds are unambiguous and win.
        var slot = new AbilitySlot(0, "Q", "x/y", CastFrame: 30f, UseAnimatorFramerate: false,
            CastTimeSeconds: 0.25f, Array.Empty<AbilityMissile>());
        Assert.Equal(0.25f, slot.CastSecondsAt(30f), 3);
    }

    // ===================================================== the walk

    [Fact]
    public void MissilesAreFoundOnSiblingSpellsNotOnlyOnTheRoot()
    {
        // Ahri's Q missiles are separate SpellObjects reached through the ability's child list; only the
        // root spell carries her timing. A reader that stopped at the root would find no missile at all.
        var ahri = Read("Ahri");
        if (ahri.Count == 0) return;

        Assert.Contains(ahri[0].Missiles, m => m.SpellName.Contains("Missile", StringComparison.OrdinalIgnoreCase));
        Assert.True(ahri[0].Missiles.Count >= 2, "Ahri Q has an outbound and a return missile");
    }

    [Fact]
    public void ASpellThatIsItsOwnMissileIsStillFound()
    {
        // Ezreal's Q is one spell that both casts and flies - the opposite shape to Ahri's.
        var ezreal = Read("Ezreal");
        if (ezreal.Count == 0) return;
        Assert.NotEmpty(ezreal[0].Missiles);
    }

    [Fact]
    public void EffectKeysAreReadAsHashesBecauseThatIsWhatTheyAre()
    {
        // 1,852 of 1,852 are BinTreeHash. Reading them as strings is the M590 mistake in another field,
        // and the symptom is identical: nothing, silently.
        var ezreal = Read("Ezreal");
        if (ezreal.Count == 0) return;
        Assert.Contains(ezreal.SelectMany(s => s.Missiles), m => m.MissileEffectKey != 0);
    }

    [Fact]
    public void ANonMissileAbilityCarriesNoMissile()
    {
        // 370 of 692 slots launch nothing. Inventing one would put a flying effect on a self-buff.
        var slots = Read("Aatrox");
        if (slots.Count == 0) return;
        Assert.Contains(slots, s => !s.HasMissile);
    }

    // ===================================================== the casting envelope

    [Fact]
    public void TargetingKindIsTheClassOfTheTargetingStruct()
    {
        // mTargetingTypeData has a class and no body; the class IS the data. Four different kinds on one
        // champion, so a reader returning any one constant fails three of them.
        var aatrox = Read("Aatrox");
        if (aatrox.Count == 0) return;

        Assert.Equal("direction", aatrox[0].TargetingKind);
        Assert.Equal("LocationClamped", aatrox[1].TargetingKind);
        Assert.Equal("Location", aatrox[2].TargetingKind);
        Assert.Equal("SelfAoe", aatrox[3].TargetingKind);
    }

    [Fact]
    public void RangeCooldownRadiusAndTagsAreTheChampionsOwn()
    {
        var aatrox = Read("Aatrox");
        if (aatrox.Count == 0) return;

        var w = aatrox[1];
        Assert.Equal(825f, w.CastRange, 3);
        Assert.Equal(20f, w.Cooldown, 3);
        Assert.False(w.IsUnboundedRange);

        var e = aatrox[2];
        Assert.Equal(285f, e.CastRadius, 3);
        Assert.Contains("Trait_PlayerSelectedDashDirection", e.Tags);

        var r = aatrox[3];
        Assert.Equal(550f, r.CastRadius, 3);
        Assert.Equal(120f, r.Cooldown, 3);
        Assert.Contains("Trait_Ultimate", r.Tags);

        // Aatrox is manaless: no mana array anywhere, and the reader says 0 rather than guessing.
        Assert.All(aatrox, s => Assert.Equal(0f, s.Mana));
    }

    [Fact]
    public void EzrealQCarriesRangeDisplayManaAndClip()
    {
        var ezreal = Read("Ezreal");
        if (ezreal.Count == 0) return;

        var q = ezreal[0];
        Assert.Equal(1200f, q.CastRange, 3);
        Assert.Equal(1150f, q.CastRangeDisplayOverride!.Value, 3);
        Assert.Equal(1150f, q.CastRangeDisplay, 3);
        Assert.Equal(28f, q.Mana, 3);
        Assert.Equal("Spell1", q.AnimationName);
        Assert.Equal("direction", q.TargetingKind);
    }

    [Fact]
    public void RankOneOfASevenEntryArrayIsElementOneNotElementZero()
    {
        // The 7-entry arrays are indexed by spell LEVEL 0..6 and level 0 is the unlearned spell. Ezreal's
        // Q authors cooldownTime [5.75, 5.5, 5.25, 5, 4.75, 4.5, 3.5]: the game shows 5.5 at rank 1, and
        // 5.75 is a number no player ever sees. His R makes it unmissable: [10, 120, 105, 90, 10, 10, 10].
        // The 6-entry mana array starts at level 1, so its element 0 IS rank 1 (Q: 28, R: 100).
        var ezreal = Read("Ezreal");
        if (ezreal.Count == 0) return;

        Assert.Equal(5.5f, ezreal[0].Cooldown, 3);
        Assert.Equal(120f, ezreal[3].Cooldown, 3);
        Assert.Equal(100f, ezreal[3].Mana, 3);
    }

    [Fact]
    public void AnAnimationNameOfNoneIsNoClip()
    {
        // Aatrox authors "None" on Q and "none" on W: each is an ability whose sub-casts carry their own
        // clips. E names "Spell3" outright.
        var aatrox = Read("Aatrox");
        if (aatrox.Count == 0) return;

        Assert.Null(aatrox[0].AnimationName);
        Assert.Null(aatrox[1].AnimationName);
        Assert.Equal("Spell3", aatrox[2].AnimationName);
        Assert.Equal("Spell4", aatrox[3].AnimationName);
    }

    [Fact]
    public void TwentyFiveThousandIsRiotsUnboundedRange()
    {
        // Aatrox Q, E and R all author castRange 25000: a swing, a dash and a self-buff, none of which
        // is a distance. W is the one real range on the kit.
        var aatrox = Read("Aatrox");
        if (aatrox.Count == 0) return;

        Assert.Equal(25000f, aatrox[0].CastRange, 0);
        Assert.True(aatrox[0].IsUnboundedRange);
        Assert.True(aatrox[2].IsUnboundedRange);
        Assert.True(aatrox[3].IsUnboundedRange);
        Assert.False(aatrox[1].IsUnboundedRange);
    }

    [Fact]
    public void TheDisplayRangeFallsBackToTheCastRange()
    {
        // No override authored on Aatrox W, so the indicator draws the cast range itself.
        var aatrox = Read("Aatrox");
        if (aatrox.Count == 4)
        {
            Assert.Null(aatrox[1].CastRangeDisplayOverride);
            Assert.Equal(825f, aatrox[1].CastRangeDisplay, 3);
        }

        // And the same holds for a slot built by hand, where the envelope defaults are what a caller
        // that predates it sees: nothing aimed, nothing named, nothing tagged.
        var slot = new AbilitySlot(0, "Q", "x/y", CastFrame: 0f, UseAnimatorFramerate: false,
            CastTimeSeconds: 0f, Array.Empty<AbilityMissile>()) { CastRange = 600f };
        Assert.Equal(600f, slot.CastRangeDisplay, 3);
        Assert.Equal("", slot.TargetingKind);
        Assert.Null(slot.AnimationName);
        Assert.Empty(slot.Tags);
        Assert.False(slot.IsUnboundedRange);
    }

    [Fact]
    public void EveryTargetingKindOnTheRosterIsOneRiotShips()
    {
        // 14 classes across 692 slots, measured; a fifteenth would surface here as its hash in hex.
        if (!Installed || Database.Value is null) return;

        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            "", "Location", "Self", "direction", "Area", "SelfAoe", "LocationClamped", "TargetOrLocation",
            "Cone", "AreaClamped", "Target", "DragDirection", "WallDetection", "TerrainLocation", "TerrainType",
        };

        var histogram = new Dictionary<string, int>(StringComparer.Ordinal);
        var examples = new Dictionary<string, string>(StringComparer.Ordinal);
        int slots = 0;
        foreach (var package in CharacterCatalog.Champions(Champions))
        {
            IReadOnlyList<AbilitySlot> read;
            try { read = Read(package.Name); }
            catch { continue; }
            foreach (var slot in read)
            {
                slots++;
                histogram[slot.TargetingKind] = histogram.GetValueOrDefault(slot.TargetingKind) + 1;
                examples.TryAdd(slot.TargetingKind, $"{package.Name} {slot.Slot}");
            }
        }

        string report = string.Join(", ", histogram.OrderByDescending(kv => kv.Value)
            .Select(kv => $"{(kv.Key.Length == 0 ? "(none)" : kv.Key)}={kv.Value} e.g. {examples[kv.Key]}"));
        var unknown = histogram.Keys.Where(k => !known.Contains(k)).ToList();
        Assert.True(unknown.Count == 0, $"unknown targeting kind(s) {string.Join(", ", unknown)}; histogram: {report}");

        // A census that resolved nothing would also pass the subset check - this is what stops it.
        Assert.True(slots >= 600, $"only {slots} slots read; histogram: {report}");
        Assert.True(histogram.Count >= 12, $"only {histogram.Count} distinct kinds - resolution is broken; histogram: {report}");
        Assert.True(histogram.GetValueOrDefault("") < slots / 4, $"{histogram.GetValueOrDefault("")} of {slots} slots unresolved; histogram: {report}");
    }
}
