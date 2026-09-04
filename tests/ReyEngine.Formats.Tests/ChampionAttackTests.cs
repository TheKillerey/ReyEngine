using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M638: the basic attacks, read off the champion record's own attack SpellObjects - the clip, the frame
/// the attack connects on, the hit effect, and the missile for a ranged champion. Asserted against the
/// shipped roster, because the claim is about what Riot authors.
/// </summary>
public sealed class ChampionAttackTests
{
    private const string Champions = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Champions";
    private static bool Installed => Directory.Exists(Champions);

    private static readonly Lazy<HashDatabase?> Database = new(() =>
    {
        try { return new HashSyncService().LoadLocal(_ => { }); }
        catch { return null; }
    });

    private static IReadOnlyList<AttackSpell> Read(string champion)
    {
        string wad = Path.Combine(Champions, champion + ".wad.client");
        if (!Installed || !File.Exists(wad) || Database.Value is not { } database) return Array.Empty<AttackSpell>();
        using var archive = WadArchive.Open(wad, new WadPathResolver(database));
        ulong hash = HashAlgorithms.WadPath(ChampionRecord.PathFor(champion.ToLowerInvariant()));
        return archive.TryGetEntry(hash, out _)
            ? ChampionSpellData.ReadAttacks(archive.Extract(hash), champion)
            : Array.Empty<AttackSpell>();
    }

    [Fact]
    public void AMeleeChampionAuthorsACycleOfAttacksWithHitsAndNoMissile()
    {
        // Aatrox's record lists SIX non-crit attack slots - BasicAttack, BasicAttack2, BasicAttack3, and the
        // state attacks RAttack1, RAttack2, PassiveAttack - every one weighted 0, because his Attack1/2/3
        // cycle is a script (AatroxAttackAnimationCycle). The pool is the name rule's three: Attack1 (the
        // unnamed slot, frame 11), Attack2 (9), Attack3 (7.5). Every one names a hit; none flies anything.
        var attacks = Read("Aatrox");
        if (attacks.Count == 0) return;

        var basics = attacks.Where(a => !a.IsCrit).ToList();
        Assert.Equal(6, basics.Count);
        Assert.All(basics, a => Assert.Equal(0f, a.Probability));

        var pool = AttackSpell.Pool(attacks, "Aatrox");
        Assert.Equal(new[] { "Attack1", "Attack2", "Attack3" }, pool.Select(a => a.ClipName).ToArray());
        Assert.Null(pool[0].AnimationName);                 // the unnamed first slot
        Assert.Equal(11f, pool[0].CastFrame, 2);
        Assert.Equal(9f, pool[1].CastFrame, 2);
        Assert.Equal(7.5f, pool[2].CastFrame, 2);
        Assert.All(pool, a => Assert.NotEqual(0u, a.HitEffectKey));
        Assert.All(pool, a => Assert.False(a.IsRanged));
        Assert.DoesNotContain(pool, a => a.Name.Contains("RAttack", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(attacks, a => a.IsCrit && a.ClipName == "Crit");
    }

    [Fact]
    public void MostChampionsWeightARandomPoolAndTheWeightsAreRead()
    {
        // Garen: BasicAttack 0.5 / BasicAttack2 0.5. Ashe: 0.75 / 0.25. A weighted slot is in the pool;
        // nothing else is, whatever it is called.
        var garen = Read("Garen");
        if (garen.Count == 0) return;
        var pool = AttackSpell.Pool(garen, "Garen");
        Assert.Equal(2, pool.Count);
        Assert.All(pool, a => Assert.Equal(0.5f, a.Probability, 3));
        Assert.DoesNotContain(pool, a => a.Name.Contains("QAttack", StringComparison.OrdinalIgnoreCase));

        var ashe = Read("Ashe");
        if (ashe.Count == 0) return;
        var ashePool = AttackSpell.Pool(ashe, "Ashe");
        Assert.Equal(2, ashePool.Count);
        Assert.Equal(1f, ashePool.Sum(a => a.Probability), 3);
        Assert.Contains(ashePool, a => Math.Abs(a.Probability - 0.75f) < 0.001f);
        Assert.All(ashePool, a => Assert.True(a.IsRanged));   // a bow: every basic attack flies
    }

    [Theory]
    [InlineData("AatroxBasicAttack", "Aatrox", true)]
    [InlineData("AatroxBasicAttack3", "Aatrox", true)]
    [InlineData("AatroxRAttack1", "Aatrox", false)]
    [InlineData("AatroxPassiveAttack", "Aatrox", false)]
    [InlineData("AkaliBasicAttackPassive", "Akali", false)]
    [InlineData("JinxQAttack", "Jinx", false)]
    [InlineData("GarenBasicAttack2", null, true)]
    [InlineData("ItemHurricaneAsheAttack", "Ashe", false)]
    public void ThePlainNameRuleKeepsBasicAttacksAndDropsStateAttacks(string name, string? champion, bool plain) =>
        Assert.Equal(plain, AttackSpell.IsPlainBasicName(name, champion));

    [Fact]
    public void ThePoolNeverComesBackEmptyForAChampionWithAttacks()
    {
        var only = new[] { new AttackSpell("XyzSpecialSwing", null, 9f, false, 0f, 0, Array.Empty<AbilityMissile>(), false) };
        Assert.Single(AttackSpell.Pool(only, "Xyz"));         // the first basic slot, whatever its name
        Assert.Empty(AttackSpell.Pool(Array.Empty<AttackSpell>()));
    }

    [Fact]
    public void ARangedChampionAuthorsTheMissileAndItsSpeed()
    {
        // Ezreal: BasicAttack and BasicAttack2 both fly Ezreal_Base_BA_mis at 2000, connecting on frame 9.5.
        var attacks = Read("Ezreal");
        if (attacks.Count == 0) return;

        var first = attacks.First(a => !a.IsCrit);
        Assert.True(first.IsRanged);
        Assert.Equal(9.5f, first.CastFrame, 2);
        var missile = first.Missiles.First(m => m.MissileEffectKey != 0);
        Assert.Equal(MissileMotionKind.ConstantSpeed, missile.Motion.Kind);
        Assert.Equal(2000f, missile.Motion.Value, 0);
        Assert.Equal(0.275f, missile.Motion.SecondsFor(550f)!.Value, 3);   // his attack range
        Assert.NotEqual(0u, first.HitEffectKey);
    }

    [Fact]
    public void TheWindupComesFromTheFrameAtTheClipsRate()
    {
        var a = new AttackSpell("x", null, 9f, false, 0f, 0, Array.Empty<AbilityMissile>(), false);
        Assert.Equal(0.3f, a.CastSecondsAt(60f), 4);        // pinned to 30 fps
        var b = a with { UseAnimatorFramerate = true };
        Assert.Equal(0.15f, b.CastSecondsAt(60f), 4);       // follows the clip
        var c = a with { CastTimeSeconds = 0.25f };
        Assert.Equal(0.25f, c.CastSecondsAt(30f), 4);       // seconds win
        Assert.Equal("Attack1", a.ClipName);
        Assert.Equal("Attack3", (a with { AnimationName = "Attack3" }).ClipName);
    }

    [Fact]
    public void ANonRecordYieldsNothingRatherThanGuessing()
    {
        Assert.Empty(ChampionSpellData.ReadAttacks(new byte[] { 1, 2, 3, 4 }));
        Assert.Empty(ChampionSpellData.ReadAttacks(Array.Empty<byte>()));
    }

    [Fact]
    public void EveryChampionOnTheRosterHasAnAttackPool()
    {
        // The composite rests on the pool never being empty. What is NOT universal, measured: a hit
        // effect key - 32 champions author none on their basic attacks (Ashe, Tristana, Annie carry the
        // impact in the missile's own system; Akali, Fiora, Irelia are melee with no hit VFX at all), so
        // that is reported here and not asserted. TFTChampion is the stub.
        if (!Installed || Database.Value is null) return;
        int read = 0, withHit = 0, ranged = 0;
        var empty = new List<string>();
        foreach (var package in CharacterCatalog.Champions(Champions))
        {
            if (package.Name.Equals("TFTChampion", StringComparison.OrdinalIgnoreCase)) continue;
            IReadOnlyList<AttackSpell> attacks;
            try { attacks = Read(package.Name); } catch { continue; }
            read++;
            var pool = AttackSpell.Pool(attacks, package.Name);
            if (pool.Count == 0) { empty.Add(package.Name); continue; }
            if (pool.Any(a => a.HitEffectKey != 0)) withHit++;
            if (pool.Any(a => a.IsRanged)) ranged++;
        }
        Assert.True(read >= 150, $"only {read} records read");
        Assert.True(empty.Count == 0, $"{empty.Count} champion(s) have no attack pool: {string.Join(", ", empty)}"
                                      + $" ({withHit} with a hit effect, {ranged} ranged, of {read})");
    }
}
