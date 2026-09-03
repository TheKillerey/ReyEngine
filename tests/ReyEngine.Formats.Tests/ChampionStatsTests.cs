using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// The champion record's gameplay numbers - move speed, attack envelope, health, collision, the attack
/// slots - as <see cref="ChampionStatsReader"/> reads them.
///
/// <para>Asserted against the shipped roster, the same way <see cref="ChampionSpellDataTests"/> is: the
/// claim under test is what Riot authors, and a fixture would only agree with itself.</para>
/// </summary>
public sealed class ChampionStatsTests
{
    private const string Champions = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Champions";
    private static bool Installed => Directory.Exists(Champions);

    private static readonly Lazy<HashDatabase?> Database = new(() =>
    {
        try { return new HashSyncService().LoadLocal(_ => { }); }
        catch { return null; }
    });

    private static byte[]? Extract(string champion, string path)
    {
        string wad = Path.Combine(Champions, champion + ".wad.client");
        if (!File.Exists(wad) || Database.Value is not { } database) return null;

        using var archive = WadArchive.Open(wad, new WadPathResolver(database));
        ulong hash = HashAlgorithms.WadPath(path);
        return archive.TryGetEntry(hash, out _) ? archive.Extract(hash) : null;
    }

    private static ChampionStats? Read(string champion) =>
        Extract(champion, ChampionRecord.PathFor(champion)) is { } record
            ? ChampionStatsReader.Read(record, champion)
            : null;

    // ===================================================== shape

    [Fact]
    public void OnThisMachineTheReaderActuallyRuns()
    {
        // Every test below skips without the install; this turns a silent skip into a failure.
        if (!Installed) return;
        Assert.NotNull(Database.Value);
        Assert.NotNull(Read("Ezreal"));
    }

    [Fact]
    public void BytesWithNoCharacterRecordYieldNullRatherThanZeroes()
    {
        Assert.Null(ChampionStatsReader.Read(new byte[] { 1, 2, 3, 4 }));
        Assert.Null(ChampionStatsReader.Read(Array.Empty<byte>()));

        // A real, well-formed bin that simply is not a record: a stats object full of zeroes would be
        // indistinguishable from a champion that authored zeroes.
        if (Extract("Ezreal", "data/characters/ezreal/skins/skin0.bin") is { } skin)
            Assert.Null(ChampionStatsReader.Read(skin, "Ezreal"));
    }

    // ===================================================== the numbers Riot ships

    [Fact]
    public void AatroxReadsTheNumbersRiotShips()
    {
        var stats = Read("Aatrox");
        if (stats is null) return;

        Assert.Equal(345f, stats.MoveSpeed, 3);
        Assert.Equal(175f, stats.AttackRange, 3);
        Assert.Equal(0.651f, stats.AttackSpeed, 3);
        Assert.Equal(0.651f, stats.AttackSpeedRatio, 3);
        Assert.Equal(650f, stats.BaseHealth, 3);
        Assert.Equal(60f, stats.BaseDamage, 3);
        Assert.Equal(35f, stats.PathfindingRadius, 3);
        Assert.Equal(135f, stats.SelectionRadius, 3);
        Assert.Equal(180f, stats.SelectionHeight, 3);
        Assert.Equal(475f, stats.AcquisitionRange, 3);
        Assert.Equal(140f, stats.HealthBarHeight, 3);
    }

    [Fact]
    public void EzrealReadsTheNumbersRiotShips()
    {
        // A ranged champion, so the range and acquisition numbers are nothing like Aatrox's - which is
        // the point: a reader returning a plausible constant would pass one of these and not the other.
        var stats = Read("Ezreal");
        if (stats is null) return;

        Assert.Equal(325f, stats.MoveSpeed, 3);
        Assert.Equal(550f, stats.AttackRange, 3);
        Assert.Equal(0.625f, stats.AttackSpeed, 3);
        Assert.Equal(0.625f, stats.AttackSpeedRatio, 3);
        Assert.Equal(600f, stats.BaseHealth, 3);
        Assert.Equal(60f, stats.BaseDamage, 3);
        Assert.Equal(35f, stats.PathfindingRadius, 3);
        Assert.Equal(115f, stats.SelectionRadius, 3);
        Assert.Equal(170f, stats.SelectionHeight, 3);
        Assert.Equal(550f, stats.AcquisitionRange, 3);
        Assert.Equal(90f, stats.HealthBarHeight, 3);
    }

    // ===================================================== attack slots

    [Fact]
    public void TheBasicAttackSlotComesFirstEvenWhenItHasNoName()
    {
        // Aatrox's basicAttack authors a cast time and a total time but no mAttackName: it runs the
        // default attack cycle. It is still the attack he has, so it is still entry 0.
        var stats = Read("Aatrox");
        if (stats is null) return;

        Assert.Equal(6, stats.BasicAttacks.Count);
        var basic = stats.BasicAttacks[0];
        Assert.Null(basic.Name);
        Assert.Equal(0.3f, basic.CastTime!.Value, 3);
        Assert.Equal(1.52f, basic.TotalTime!.Value, 3);
        Assert.Null(basic.DelayCastOffsetPercent);

        // extraAttacks follow in authored order.
        Assert.Equal(
            new[] { "AatroxBasicAttack2", "AatroxBasicAttack3", "AatroxRAttack1", "AatroxRAttack2", "AatroxPassiveAttack" },
            stats.BasicAttacks.Skip(1).Select(a => a.Name).ToArray());
        Assert.Equal(0f, stats.BasicAttacks[5].CastTime!.Value, 3);

        Assert.Single(stats.CritAttacks);
        Assert.Equal("AatroxCritAttack", stats.CritAttacks[0].Name);
    }

    [Fact]
    public void AttackTimingAndProbabilityAreReadThroughTheOptionalWrapper()
    {
        // Every AttackSlotData field is Optional-wrapped; a reader matching the bare type sees nothing.
        var stats = Read("Ezreal");
        if (stats is null) return;

        Assert.Equal(2, stats.BasicAttacks.Count);
        var basic = stats.BasicAttacks[0];
        Assert.Equal("EzrealBasicAttack", basic.Name);
        Assert.Equal(0.75f, basic.Probability, 3);
        Assert.Equal(-0.1116f, basic.DelayCastOffsetPercent!.Value, 3);
        Assert.Null(basic.CastTime);
        Assert.Null(basic.TotalTime);

        Assert.Equal("EzrealBasicAttack2", stats.BasicAttacks[1].Name);
        Assert.Equal(0.25f, stats.BasicAttacks[1].Probability, 3);

        // An unauthored probability is reported as 0, not invented.
        var crit = Assert.Single(stats.CritAttacks);
        Assert.Equal("EzrealCritAttack", crit.Name);
        Assert.Equal(0f, crit.Probability, 3);
        Assert.Equal(-0.1116f, crit.DelayCastOffsetPercent!.Value, 3);
    }

    // ===================================================== roster

    [Fact]
    public void EveryChampionRecordOnTheRosterCarriesTheCoreStats()
    {
        // The whole justification for reading these from the record is that Riot authors them everywhere.
        // Measured 173 of 173 for the six ModifiableFloat stats and the collision radius; this keeps it so.
        if (!Installed || Database.Value is null) return;

        int read = 0;
        var missing = new List<string>();
        var incomplete = new List<string>();
        foreach (var package in CharacterCatalog.Champions(Champions))
        {
            // TFTChampion is the Teamfight Tactics stand-in: a CharacterRecord with no HP, no attacks and no
            // spells (ChampionSpellDataTests records the same). It is in the Champions folder but it is not
            // a champion, and the "173 of 173" this test keeps is the roster without it.
            if (package.Name.Equals("TFTChampion", StringComparison.OrdinalIgnoreCase)) continue;

            ChampionStats? stats;
            try { stats = Read(package.Name); }
            catch { continue; }
            if (stats is null) { missing.Add(package.Name); continue; }
            read++;

            if (stats.MoveSpeed <= 0f || stats.AttackRange <= 0f || stats.AttackSpeed <= 0f
                || stats.BaseHealth <= 0f || stats.BaseDamage <= 0f || stats.PathfindingRadius <= 0f
                || stats.SelectionRadius <= 0f || stats.BasicAttacks.Count == 0)
                incomplete.Add($"{package.Name} (ms {stats.MoveSpeed}, range {stats.AttackRange}, hp {stats.BaseHealth}, attacks {stats.BasicAttacks.Count})");
        }

        Assert.True(read >= 150, $"only {read} champion records read; missing: {string.Join(", ", missing)}");
        Assert.True(missing.Count <= 2, $"{missing.Count} champion WADs have no readable record: {string.Join(", ", missing)}");
        Assert.True(incomplete.Count == 0, $"{incomplete.Count} record(s) lack a core stat: {string.Join("; ", incomplete)}");
    }
}
