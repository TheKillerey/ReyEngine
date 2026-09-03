using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Characters;

/// <summary>
/// One basic-attack variant from the champion record's <c>AttackSlotData</c>.
///
/// <para>Every field is Optional-wrapped in the bin and most are simply not authored: over 173 records,
/// the basic attack names its spell in 43, times its cast in 63 and states a probability in 152. Null
/// means "not authored", and the client's own default is NOT reconstructed here - the record does not
/// say what it is, and a number this reader made up would read exactly like one Riot wrote.</para>
/// </summary>
/// <param name="Name">The attack's spell script name (<c>EzrealBasicAttack2</c>), or null when the slot
/// runs the default attack cycle - the <c>Attack1..N</c> clips with nothing overridden, which is what 130
/// of 173 basic attacks do.</param>
/// <param name="CastTime"><c>mAttackCastTime</c> in seconds; null when not authored.</param>
/// <param name="TotalTime"><c>mAttackTotalTime</c> in seconds; null when not authored.</param>
/// <param name="DelayCastOffsetPercent"><c>mAttackDelayCastOffsetPercent</c>, the authored shift of the
/// hit point along the clip (Ezreal: -0.1116); null when not authored.</param>
/// <param name="Probability"><c>mAttackProbability</c>; 0 when not authored, which is the usual case
/// (207 of 216 crit attacks, 154 of 297 extra attacks) and must not be read as "never fires".</param>
public sealed record ChampionAttack(
    string? Name,
    float? CastTime,
    float? TotalTime,
    float? DelayCastOffsetPercent,
    float Probability);

/// <summary>
/// The gameplay numbers a playable character needs from its <c>CharacterRecord</c>: movement, the attack
/// envelope, the hit points it starts with, and how big it is to the pathfinder and the cursor.
///
/// <para>Absent fields stay 0 and this type does not fill them in: over 173 shipped records the six
/// <c>ModifiableFloat</c> stats are authored 173 times each, <c>pathfindingCollisionRadius</c>,
/// <c>selectionRadius</c> and <c>selectionHeight</c> likewise, but <c>acquisitionRange</c> is missing on
/// 12 and <c>healthBarHeight</c> on 51. A zero there is the record's silence, not a measurement.</para>
/// </summary>
/// <param name="MoveSpeed"><c>baseMoveSpeedModifiable.baseValue</c>, units per second (Aatrox 345).</param>
/// <param name="AttackRange"><c>attackRangeModifiable.baseValue</c> (Aatrox 175, Ezreal 550).</param>
/// <param name="AttackSpeed"><c>attackSpeedModifiable.baseValue</c>, attacks per second at level 1.</param>
/// <param name="AttackSpeedRatio"><c>attackSpeedRatioModifiable.baseValue</c>, the ratio bonus attack
/// speed scales against.</param>
/// <param name="BaseHealth"><c>baseHPModifiable.baseValue</c> (Aatrox 650).</param>
/// <param name="BaseDamage"><c>baseDamageModifiable.baseValue</c> (Aatrox 60).</param>
/// <param name="PathfindingRadius"><c>pathfindingCollisionRadius</c> (35 on both champions probed).</param>
/// <param name="SelectionRadius"><c>selectionRadius</c>, the cursor's pick radius.</param>
/// <param name="SelectionHeight"><c>selectionHeight</c>, the cursor's pick height.</param>
/// <param name="AcquisitionRange"><c>acquisitionRange</c>, how far the character looks for something to
/// attack on its own; 0 when the record does not say (12 of 173).</param>
/// <param name="BasicAttacks">The <c>basicAttack</c> slot first, then <c>extraAttacks</c> in authored
/// order. The first entry is kept even when it carries no name - a nameless basic attack IS the default
/// cycle, and dropping it would hide the attack every champion has.</param>
/// <param name="CritAttacks"><c>critAttacks</c> in authored order; empty when absent (3 of 173).</param>
public sealed record ChampionStats(
    float MoveSpeed,
    float AttackRange,
    float AttackSpeed,
    float AttackSpeedRatio,
    float BaseHealth,
    float BaseDamage,
    float PathfindingRadius,
    float SelectionRadius,
    float SelectionHeight,
    float AcquisitionRange,
    IReadOnlyList<ChampionAttack> BasicAttacks,
    IReadOnlyList<ChampionAttack> CritAttacks)
{
    /// <summary><c>healthBarHeight</c>; 0 when the record does not author it (51 of 173 do not).</summary>
    public float HealthBarHeight { get; init; }
}

/// <summary>
/// Reads <see cref="ChampionStats"/> out of <c>data/characters/&lt;name&gt;/&lt;name&gt;.bin</c>.
///
/// <para>The six headline stats are <c>ModifiableFloat</c> structs with one <c>baseValue</c> - the
/// "modifiable" half is runtime state and the bin never authors anything but the base. The attack slots
/// are <c>AttackSlotData</c> with every field Optional-wrapped; <c>basicAttack</c> is embedded on the
/// record and <c>extraAttacks</c> / <c>critAttacks</c> are containers of the same struct. Measured over
/// 173 shipped records; the hash database does not know any of these class names, so they are matched by
/// their FNV-1a hash rather than looked up.</para>
/// </summary>
public static class ChampionStatsReader
{
    private static readonly uint ClassCharacterRecord = HashAlgorithms.Fnv1a("CharacterRecord");
    private static readonly uint FSpellNames = HashAlgorithms.Fnv1a("spellNames");
    private static readonly uint FBaseValue = HashAlgorithms.Fnv1a("baseValue");
    private static readonly uint FMoveSpeed = HashAlgorithms.Fnv1a("baseMoveSpeedModifiable");
    private static readonly uint FAttackRange = HashAlgorithms.Fnv1a("attackRangeModifiable");
    private static readonly uint FAttackSpeed = HashAlgorithms.Fnv1a("attackSpeedModifiable");
    private static readonly uint FAttackSpeedRatio = HashAlgorithms.Fnv1a("attackSpeedRatioModifiable");
    private static readonly uint FBaseHealth = HashAlgorithms.Fnv1a("baseHPModifiable");
    private static readonly uint FBaseDamage = HashAlgorithms.Fnv1a("baseDamageModifiable");
    private static readonly uint FPathfindingRadius = HashAlgorithms.Fnv1a("pathfindingCollisionRadius");
    private static readonly uint FSelectionRadius = HashAlgorithms.Fnv1a("selectionRadius");
    private static readonly uint FSelectionHeight = HashAlgorithms.Fnv1a("selectionHeight");
    private static readonly uint FAcquisitionRange = HashAlgorithms.Fnv1a("acquisitionRange");
    private static readonly uint FHealthBarHeight = HashAlgorithms.Fnv1a("healthBarHeight");
    private static readonly uint FBasicAttack = HashAlgorithms.Fnv1a("basicAttack");
    private static readonly uint FExtraAttacks = HashAlgorithms.Fnv1a("extraAttacks");
    private static readonly uint FCritAttacks = HashAlgorithms.Fnv1a("critAttacks");
    private static readonly uint FAttackName = HashAlgorithms.Fnv1a("mAttackName");
    private static readonly uint FAttackCastTime = HashAlgorithms.Fnv1a("mAttackCastTime");
    private static readonly uint FAttackTotalTime = HashAlgorithms.Fnv1a("mAttackTotalTime");
    private static readonly uint FAttackDelayCastOffsetPercent = HashAlgorithms.Fnv1a("mAttackDelayCastOffsetPercent");
    private static readonly uint FAttackProbability = HashAlgorithms.Fnv1a("mAttackProbability");

    /// <summary>The record's stats, or null when the bytes hold no <c>CharacterRecord</c> at all - a skin
    /// bin, an animation graph, or something that is not a bin.</summary>
    /// <param name="characterName">Picks the right record when a WAD ships more than one (Braum, Milio and
    /// Cassiopeia carry URF and SLIME twins). Without it the record that has spells wins, and failing that
    /// the first one - a companion such as annietibbers has no spells and still has a move speed.</param>
    public static ChampionStats? Read(byte[] recordBin, string? characterName = null)
    {
        try
        {
            var tree = new BinTree(new MemoryStream(recordBin));
            if (PickRecord(tree, characterName) is not { } record) return null;
            var p = record.Properties;

            var basicAttacks = new List<ChampionAttack>();
            if (Unwrap(Get(p, FBasicAttack)) is BinTreeStruct basicAttack) basicAttacks.Add(ReadAttack(basicAttack));
            basicAttacks.AddRange(ReadAttacks(Get(p, FExtraAttacks)));

            return new ChampionStats(
                Modifiable(p, FMoveSpeed),
                Modifiable(p, FAttackRange),
                Modifiable(p, FAttackSpeed),
                Modifiable(p, FAttackSpeedRatio),
                Modifiable(p, FBaseHealth),
                Modifiable(p, FBaseDamage),
                F32(p, FPathfindingRadius),
                F32(p, FSelectionRadius),
                F32(p, FSelectionHeight),
                F32(p, FAcquisitionRange),
                basicAttacks,
                ReadAttacks(Get(p, FCritAttacks)).ToList())
            {
                HealthBarHeight = F32(p, FHealthBarHeight),
            };
        }
        catch
        {
            // Bytes that are not a bin, or a bin that will not parse: no stats, not a crash.
            return null;
        }
    }

    private static BinTreeObject? PickRecord(BinTree tree, string? characterName)
    {
        uint wanted = characterName is { Length: > 0 }
            ? HashAlgorithms.Fnv1a($"Characters/{characterName}/CharacterRecords/Root")
            : 0u;

        BinTreeObject? first = null, withSpells = null;
        foreach (var o in tree.Objects.Values)
        {
            if (o.ClassHash != ClassCharacterRecord) continue;
            if (wanted != 0 && o.PathHash == wanted) return o;
            first ??= o;
            if (withSpells is null && Get(o.Properties, FSpellNames) is BinTreeContainer { Elements.Count: > 0 })
                withSpells = o;
        }
        return withSpells ?? first;
    }

    private static IEnumerable<ChampionAttack> ReadAttacks(BinTreeProperty? list)
    {
        if (Unwrap(list) is not BinTreeContainer container) yield break;
        foreach (var element in container.Elements)
            if (element is BinTreeStruct slot) yield return ReadAttack(slot);
    }

    private static ChampionAttack ReadAttack(BinTreeStruct slot) => new(
        Unwrap(Get(slot.Properties, FAttackName)) is BinTreeString { Value.Length: > 0 } name ? name.Value : null,
        OptionalF32(slot.Properties, FAttackCastTime),
        OptionalF32(slot.Properties, FAttackTotalTime),
        OptionalF32(slot.Properties, FAttackDelayCastOffsetPercent),
        OptionalF32(slot.Properties, FAttackProbability) ?? 0f);

    // ---- property helpers -------------------------------------------------------------------------

    private static BinTreeProperty? Get(IReadOnlyDictionary<uint, BinTreeProperty> props, uint key) =>
        props.TryGetValue(key, out var value) ? value : null;

    /// <summary>Every AttackSlotData field is Optional; the record's own scalars are not. Reading through
    /// the wrapper either way costs nothing and survives Riot moving a field between the two.</summary>
    private static BinTreeProperty? Unwrap(BinTreeProperty? property) =>
        property is BinTreeOptional optional ? optional.Value : property;

    private static float F32(IReadOnlyDictionary<uint, BinTreeProperty> props, uint key) =>
        Unwrap(Get(props, key)) is BinTreeF32 f ? f.Value : 0f;

    private static float? OptionalF32(IReadOnlyDictionary<uint, BinTreeProperty> props, uint key) =>
        Unwrap(Get(props, key)) is BinTreeF32 f ? f.Value : null;

    /// <summary>ModifiableFloat is an EMBEDDED struct (173 of 173) holding one F32, <c>baseValue</c>.
    /// BinTreeEmbedded derives from BinTreeStruct, so the pointer form would read the same way.</summary>
    private static float Modifiable(IReadOnlyDictionary<uint, BinTreeProperty> props, uint key) =>
        Unwrap(Get(props, key)) is BinTreeStruct s ? F32(s.Properties, FBaseValue) : 0f;
}
