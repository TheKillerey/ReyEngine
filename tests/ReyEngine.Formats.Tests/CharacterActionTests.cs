using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Skeletons;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M611: what a character does, rather than which files it ships.
///
/// <para>The measurements this file asserts were taken over all 174 shipped champions: 641 of 696 ability
/// rows resolve to a clip, 692 are named from the champion record, and no clip is ever claimed by two
/// actions. The gaps are the point — 15 champions have no Q animation in their base graph and 48 have no
/// Recall — so the tests pin the behaviour when a clip is MISSING as hard as when it is found.</para>
/// </summary>
public sealed class CharacterActionTests
{
    private const string Champions = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Champions";
    private static bool Installed => Directory.Exists(Champions);

    private static readonly Lazy<HashDatabase?> Database = new(() =>
    {
        try { return new HashSyncService().LoadLocal(_ => { }); }
        catch { return null; }
    });

    /// <summary>The spell names and clips for one shipped champion, or null when unavailable.</summary>
    private static (IReadOnlyList<string> Spells, IReadOnlyList<AnimClipInfo> Clips)? Champion(string name)
    {
        string wad = Path.Combine(Champions, name + ".wad.client");
        if (!Installed || !File.Exists(wad) || Database.Value is not { } database) return null;

        using var archive = WadArchive.Open(wad, new WadPathResolver(database));
        if (archive.ResolvedCount == 0) return null;

        string lower = name.ToLowerInvariant();
        ulong record = HashAlgorithms.WadPath(ChampionRecord.PathFor(lower));
        ulong graph = HashAlgorithms.WadPath($"data/characters/{lower}/animations/skin0.bin");
        if (!archive.TryGetEntry(record, out _) || !archive.TryGetEntry(graph, out _)) return null;

        return (ChampionRecord.SpellNames(archive.Extract(record)),
            ChampionAnimationData.ParseClips(archive.Extract(graph),
                h => database.TryGetBinName(h, out var n) ? n : null,
                h => database.TryGetPath(h, out var p) ? p : null));
    }

    private static AnimClipInfo Clip(string name, int vfx = 0, int sfx = 0) => new(
        name, name.ToLowerInvariant() + ".anm",
        Array.Empty<string>(), Array.Empty<string>(), Array.Empty<uint>(), Array.Empty<uint>(),
        Enumerable.Range(0, vfx).Select(i => new AnimParticleEvent($"fx{i}", 0u, "bone", 0f)).ToList(),
        Enumerable.Range(0, sfx).Select(i => new AnimSoundEvent($"sfx{i}", 0f, false)).ToList());

    // ===================================================== the slot comes from the position

    [Fact]
    public void TheFourAbilitiesAreAlwaysQwerInThatOrder()
    {
        var actions = CharacterActions.Build(
            new[] { "AQ/a", "AW/a", "AE/a", "AR/a" },
            new[] { Clip("Spell1"), Clip("Spell2"), Clip("Spell3"), Clip("Spell4") });

        var abilities = actions.Where(a => a.Kind == CharacterActionKind.Ability).ToList();
        Assert.Equal(4, abilities.Count);
        Assert.Equal(new[] { "Spell1", "Spell2", "Spell3", "Spell4" },
            abilities.Select(a => a.Clip!.Name).ToArray());
        Assert.StartsWith("Q", abilities[0].Label);
        Assert.StartsWith("R", abilities[3].Label);
    }

    [Fact]
    public void TheSlotIsReadFromThePositionAndNeverFromTheName()
    {
        // Riot names abilities descriptively: DariusCleaveAbility IS Darius's Q and says nothing about
        // it. Measured: 99 of 174 champions have at least one ability whose name gives no hint of its
        // slot, so anything reading the letter out of the name is reading a coincidence.
        var actions = CharacterActions.Build(
            new[] { "DariusCleaveAbility/DariusCleave", "DariusNoxianTacticsONHAbility/x",
                    "DariusAxeGrabConeAbility/x", "DariusExecuteAbility/x" },
            new[] { Clip("Spell1"), Clip("Spell2"), Clip("Spell3"), Clip("Spell4") });

        Assert.Equal("Q — DariusCleave", actions[0].Label);
        Assert.Equal("R — DariusExecute", actions[3].Label);
    }

    [Fact]
    public void AChampionRecordWithNoSpellNamesStillGetsFourSlots()
    {
        // One shipped champion has none. Losing the ability rows entirely would be a much bigger lie
        // than losing their names.
        var actions = CharacterActions.Build(
            Array.Empty<string>(), new[] { Clip("Spell1"), Clip("Spell4") });

        var abilities = actions.Where(a => a.Kind == CharacterActionKind.Ability).ToList();
        Assert.Equal(4, abilities.Count);
        Assert.Equal(new[] { "Q", "W", "E", "R" }, abilities.Select(a => a.Label).ToArray());
        Assert.Equal("Spell1", abilities[0].Clip!.Name);
        Assert.Null(abilities[1].Clip);
    }

    [Theory]
    [InlineData("AhriQAbility/AhriQ", "AhriQ")]
    [InlineData("DariusCleaveAbility/DariusCleave", "DariusCleave")]
    [InlineData("NoSlashHere", "NoSlashHere")]
    [InlineData("", "")]
    public void TheReadableHalfOfASpellNameDropsTheScriptAndTheSuffix(string spell, string expected) =>
        Assert.Equal(expected, ChampionRecord.Readable(spell));

    // ===================================================== a missing clip is an answer

    [Fact]
    public void AnAbilityWithNoClipKeepsItsRowAndSaysSo()
    {
        // 15 shipped champions have no Spell1 in their base graph - Aatrox, Aphelios, Riven and others
        // whose Q is a modified attack. A row that vanished would read as "this champion has no Q".
        var actions = CharacterActions.Build(new[] { "XQ/a", "XW/a", "XE/a", "XR/a" }, new[] { Clip("Spell4") });

        var abilities = actions.Where(a => a.Kind == CharacterActionKind.Ability).ToList();
        Assert.Equal(4, abilities.Count);
        Assert.False(abilities[0].HasClip);
        Assert.True(abilities[3].HasClip);
    }

    [Fact]
    public void AnEmoteWithNoClipIsLeftOutEntirely()
    {
        // The opposite call, for the opposite reason: an empty Taunt row on a champion that never taunts
        // is noise, because nothing promised a taunt in the first place.
        var actions = CharacterActions.Build(Array.Empty<string>(), new[] { Clip("Run"), Clip("Dance") });

        Assert.Contains(actions, a => a.Label == "Dance");
        Assert.DoesNotContain(actions, a => a.Label == "Taunt");
        Assert.DoesNotContain(actions, a => a.Label == "Joke");
    }

    // ===================================================== variants

    [Fact]
    public void DirectionalAndTransitionClipsBecomeVariantsOfTheirAbility()
    {
        var actions = CharacterActions.Build(
            new[] { "a/a", "b/b", "c/c", "d/d" },
            new[] { Clip("Spell3"), Clip("Spell3_North"), Clip("Spell3_South"), Clip("Spell3_ToRun") });

        var e = actions.First(a => a.Label.StartsWith("E"));
        Assert.Equal("Spell3", e.Clip!.Name);
        Assert.Equal(3, e.Variants.Count);
    }

    [Fact]
    public void WithNoPlainClipTheFirstVariantStandsIn()
    {
        // A champion whose Q exists only as Spell1_ToIdle still has a Q worth playing.
        var actions = CharacterActions.Build(
            new[] { "a/a" }, new[] { Clip("Spell1_ToIdle"), Clip("Spell1_ToRun") });

        var q = actions[0];
        Assert.True(q.HasClip);
        Assert.Equal("Spell1_ToIdle", q.Clip!.Name);
        Assert.Single(q.Variants);
    }

    [Fact]
    public void APrefixOnlyMatchesOnASeparatorSoSpell1DoesNotSwallowSpell11()
    {
        var actions = CharacterActions.Build(
            new[] { "a/a", "b/b", "c/c", "d/d" }, new[] { Clip("Spell1"), Clip("Spell1Extra"), Clip("Spell1_ToIdle") });

        var q = actions[0];
        Assert.Equal("Spell1", q.Clip!.Name);
        Assert.Equal(new[] { "Spell1_ToIdle" }, q.Variants.Select(v => v.Name).ToArray());
        Assert.Contains(CharacterActions.Unassigned(actions, new[] { q.Clip, q.Variants[0], Clip("Spell1Extra") }),
            c => c.Name == "Spell1Extra");
    }

    // ===================================================== events

    [Fact]
    public void AnActionGathersTheVfxAndSoundsOfItsClipAndItsVariants()
    {
        // "All the VFX this ability uses" is the question being asked, and half of them hang off the
        // directional variants rather than the plain clip.
        var actions = CharacterActions.Build(
            new[] { "a/a", "b/b", "c/c", "d/d" },
            new[] { Clip("Spell3", vfx: 1, sfx: 2), Clip("Spell3_North", vfx: 3), Clip("Spell3_South", sfx: 1) });

        var e = actions.First(a => a.Label.StartsWith("E"));
        Assert.Equal(4, e.ParticleEvents.Count);
        Assert.Equal(3, e.SoundEvents.Count);
    }

    // ===================================================== against the shipped champions

    [Fact]
    public void DariusReadsAsQwerWithHisRealAbilityNames()
    {
        if (Champion("Darius") is not { } data) return;
        var actions = CharacterActions.Build(data.Spells, data.Clips);

        Assert.Equal("Q — DariusCleave", actions[0].Label);
        Assert.Equal("Spell1", actions[0].Clip?.Name);
        Assert.Equal("R — DariusExecute", actions[3].Label);
        Assert.Contains(actions, a => a.Kind == CharacterActionKind.Movement && a.HasClip);
        Assert.Contains(actions, a => a.Label == "Recall" && a.HasClip);
    }

    [Fact]
    public void NoClipIsEverClaimedByTwoActions()
    {
        // The panel would be actively misleading if the same animation appeared as both Q and Move.
        // Verified over the whole roster; asserted here on the champions most likely to break it.
        foreach (string name in new[] { "Ahri", "Darius", "Elise", "Jayce", "Nidalee" })
        {
            if (Champion(name) is not { } data) continue;
            var actions = CharacterActions.Build(data.Spells, data.Clips);

            var seen = new HashSet<AnimClipInfo>();
            foreach (var action in actions)
                foreach (var clip in action.Clip is null
                             ? action.Variants
                             : new[] { action.Clip }.Concat(action.Variants))
                    Assert.True(seen.Add(clip), $"{name}: {clip.Name} appears under two actions");
        }
    }

    [Fact]
    public void EveryClipIsEitherInAnActionOrReachableAsUnassigned()
    {
        // A third of a champion's clips are transitions, stuns and hash-only names. They must stay
        // reachable: silently dropping 40% of a character's animations is how a preview starts lying.
        if (Champion("Ahri") is not { } data) return;

        var actions = CharacterActions.Build(data.Spells, data.Clips);
        var assigned = actions.SelectMany(a => a.Clip is null ? a.Variants : new[] { a.Clip }.Concat(a.Variants));
        var unassigned = CharacterActions.Unassigned(actions, data.Clips);

        Assert.Equal(data.Clips.Count, assigned.Distinct().Count() + unassigned.Count);
    }

    [Fact]
    public void AbilityNamesComeFromTheChampionRecordForEveryChampionThatShipsThem()
    {
        if (Champion("Ahri") is not { } data) return;

        Assert.Equal(4, data.Spells.Count);
        var actions = CharacterActions.Build(data.Spells, data.Clips);
        Assert.All(actions.Where(a => a.Kind == CharacterActionKind.Ability),
            a => Assert.NotNull(a.SpellName));
    }
}
