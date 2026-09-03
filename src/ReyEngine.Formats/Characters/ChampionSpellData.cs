using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Characters;

/// <summary>How a missile crosses the distance to its target.</summary>
public enum MissileMotionKind
{
    /// <summary>An authored speed in units per second.</summary>
    ConstantSpeed,
    /// <summary>An authored flight time in seconds, regardless of distance.</summary>
    FixedDuration,
    /// <summary>The movement component authored neither — 11 of 1,146 shipped specs, all circle-family.</summary>
    Unknown,
}

/// <param name="Value">Units per second, or seconds, depending on <paramref name="Kind"/>.</param>
public readonly record struct MissileMotion(MissileMotionKind Kind, float Value)
{
    public static readonly MissileMotion None = new(MissileMotionKind.Unknown, 0f);

    /// <summary>How long this missile takes to cross <paramref name="distance"/>, or null when the data
    /// does not say and the caller must fall back to a guess of its own.</summary>
    public float? SecondsFor(float distance) => Kind switch
    {
        MissileMotionKind.ConstantSpeed when Value > 1f => distance / Value,
        MissileMotionKind.FixedDuration when Value > 0f => Value,
        _ => null,
    };
}

/// <summary>One missile an ability launches. A slot can own several — Ahri's Q has an outbound and a
/// return missile.</summary>
public sealed record AbilityMissile(
    string SpellName,
    /// <summary>The VFX system, as an effect KEY — a hash resolved through the skin's
    /// <c>ResourceResolver.resourceMap</c>, which is the same indirection clip particle events use.
    /// Zero when the spell authors none.</summary>
    uint MissileEffectKey,
    uint HitEffectKey,
    MissileMotion Motion,
    float Width);

/// <summary>One of Q/W/E/R, and everything the spell records say about how it is cast.</summary>
public sealed record AbilitySlot(
    int Index,
    string Slot,
    string SpellEntry,
    /// <summary>The animation frame the cast fires on. Riot authors this rather than a delay in seconds;
    /// 675 of 692 slots have some authored timing and the editor's 0.15 s constant matched none of it.</summary>
    float CastFrame,
    bool UseAnimatorFramerate,
    float CastTimeSeconds,
    IReadOnlyList<AbilityMissile> Missiles)
{
    public bool HasMissile => Missiles.Count > 0;

    // ---- the casting envelope, read off the root spell ---------------------------------------------

    /// <summary>The class name of the spell's <c>mTargetingTypeData</c>, which is how Riot says HOW a
    /// spell is aimed: "direction", "Location", "Self", "SelfAoe", "Target", "Cone" and eight more - the
    /// struct has a class and no body. "" when the spell authors none (104 of 692 slots). A class this
    /// reader has never seen comes back as its hash in hex rather than being folded into "", so a new
    /// targeting type in a patch shows up instead of disappearing.</summary>
    public string TargetingKind { get; init; } = "";

    /// <summary><c>castRange</c> at rank 1, in units; 0 when absent (31 of 692). Riot authors 25,000 on a
    /// spell that is not range-limited - a dash, a self-buff, a global - see <see cref="IsUnboundedRange"/>.</summary>
    public float CastRange { get; init; }

    /// <summary><c>castRangeDisplayOverride</c> at rank 1, when authored (283 of 692): the range the
    /// indicator draws when it is not the range the spell uses. Ezreal's Q casts at 1200 and shows 1150;
    /// his E casts at 25,000 and shows 475.</summary>
    public float? CastRangeDisplayOverride { get; init; }

    /// <summary>The range to draw: the display override when authored, else <see cref="CastRange"/>.</summary>
    public float CastRangeDisplay => CastRangeDisplayOverride ?? CastRange;

    /// <summary><c>cooldownTime</c> at rank 1, in seconds; 0 when absent (12 of 692).</summary>
    public float Cooldown { get; init; }

    /// <summary><c>mana</c> at rank 1; 0 when absent, which is how a manaless champion's spells are
    /// authored (124 of 692) - Aatrox has no mana array at all.</summary>
    public float Mana { get; init; }

    /// <summary><c>castRadius</c> at rank 1; 0 when absent (163 of 692).</summary>
    public float CastRadius { get; init; }

    /// <summary><c>castConeAngle</c> as authored; 0 when absent, which is 622 of 692 - only cones author it.</summary>
    public float CastConeAngle { get; init; }

    /// <summary><c>castConeDistance</c> as authored; 0 when absent (131 of 692). Riot writes 100 on most
    /// non-cone spells as well, so a value here does not make the spell a cone - <see cref="TargetingKind"/>
    /// does.</summary>
    public float CastConeDistance { get; init; }

    /// <summary>The cast clip the spell names (<c>Spell1</c>), or null when it names none: absent (32 of
    /// 692), empty (85), or the literal "None"/"none" (33) that marks an ability whose sub-casts carry
    /// their own clips - Aatrox's Q is three swings, each a child spell with its own animation.</summary>
    public string? AnimationName { get; init; }

    /// <summary><c>mSpellTags</c>, Riot's trait strings: <c>Trait_Ultimate</c>,
    /// <c>Trait_PlayerSelectedDashDirection</c>, <c>Trait_Target_Directional</c>. Empty when absent (9 of 692).</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>Riot's "no range limit": 132 of 661 authored ranges are exactly 25,000, and everything at
    /// or above 20,000 (139) is a dash, a self-cast or a global, not a distance anyone measured.</summary>
    public bool IsUnboundedRange => CastRange >= 20000f;

    /// <summary>When the cast fires, in seconds. <c>castFrame</c> is in animation frames; without
    /// <c>useAnimatorFramerate</c> Riot's own convention is 30 fps, which is what the clip decoder
    /// assumes elsewhere in this codebase for a missing rate.</summary>
    public float CastSecondsAt(float clipFps)
    {
        if (CastTimeSeconds > 0f) return CastTimeSeconds;
        float fps = UseAnimatorFramerate && clipFps > 1f ? clipFps : 30f;
        return CastFrame > 0f ? CastFrame / fps : 0f;
    }
}

/// <summary>
/// M631: what the champion record says about casting a spell, so the preview's composite can stop
/// guessing.
///
/// <para>The composite used to invent all three of its parts: which VFX is the missile (from name
/// tokens), how fast it flies (1,800 units/s), and when it launches (0.15 s). Measured over the roster,
/// exactly 1,800 occurs in 14 of 462 authored speeds — it is one champion's number generalised to
/// everyone, and Aatrox happens to be that champion, which is almost certainly how it was picked.</para>
///
/// <para>WHAT THIS CAN AND CANNOT REPLACE, measured over all 692 ability slots:</para>
/// <list type="bullet">
/// <item>Missile: 279 of the 319 slots that own a missile spec name their effect, and 317 of 319 author
/// a speed or a flight time. Data wins here.</item>
/// <item>Timing: 675 of 692 author a cast frame or cast time. Data wins here.</item>
/// <item>Impact: only 101 of 692 name a hit effect, because 602 of the 816 <c>mHitEffectKey</c>
/// occurrences sit on basic attacks and crits rather than on abilities. The ability's impact effect is
/// linked by the compiled spell script, which ships in no bin at all — so the name-token heuristic is
/// not a workaround there, it is the only bridge that exists, and it stays.</item>
/// </list>
///
/// <para>Extended with the casting envelope a playable character needs - targeting kind, range, cooldown,
/// mana, radius, cone, clip name and tags - all read off the ROOT spell, because a sub-cast is a child
/// spell and owns none of it. Per-level arrays are read at rank 1, which is NOT element 0 of a 7-entry
/// array; see <see cref="Rank1"/>.</para>
/// </summary>
public static class ChampionSpellData
{
    private static readonly uint ClassCharacterRecord = HashAlgorithms.Fnv1a("CharacterRecord");
    private static readonly uint ClassAbility = HashAlgorithms.Fnv1a("AbilityObject");
    private static readonly uint FSpellNames = HashAlgorithms.Fnv1a("spellNames");
    private static readonly uint FSpells = HashAlgorithms.Fnv1a("spells");
    private static readonly uint FRootSpell = HashAlgorithms.Fnv1a("mRootSpell");
    private static readonly uint FChildSpells = HashAlgorithms.Fnv1a("mChildSpells");
    private static readonly uint FSpell = HashAlgorithms.Fnv1a("mSpell");
    private static readonly uint FCastFrame = HashAlgorithms.Fnv1a("castFrame");
    private static readonly uint FCastTime = HashAlgorithms.Fnv1a("mCastTime");
    private static readonly uint FUseAnimatorFps = HashAlgorithms.Fnv1a("useAnimatorFramerate");
    private static readonly uint FMissileEffectKey = HashAlgorithms.Fnv1a("mMissileEffectKey");
    private static readonly uint FHitEffectKey = HashAlgorithms.Fnv1a("mHitEffectKey");
    private static readonly uint FMissileSpec = HashAlgorithms.Fnv1a("mMissileSpec");
    private static readonly uint FMovement = HashAlgorithms.Fnv1a("movementComponent");
    private static readonly uint FSpeed = HashAlgorithms.Fnv1a("mSpeed");
    private static readonly uint FTravelTime = HashAlgorithms.Fnv1a("mTravelTime");
    private static readonly uint FMissileWidth = HashAlgorithms.Fnv1a("mMissileWidth");
    private static readonly uint FScriptName = HashAlgorithms.Fnv1a("mScriptName");
    private static readonly uint FTargetingTypeData = HashAlgorithms.Fnv1a("mTargetingTypeData");
    private static readonly uint FCastRange = HashAlgorithms.Fnv1a("castRange");
    private static readonly uint FCastRangeDisplayOverride = HashAlgorithms.Fnv1a("castRangeDisplayOverride");
    private static readonly uint FCooldownTime = HashAlgorithms.Fnv1a("cooldownTime");
    private static readonly uint FMana = HashAlgorithms.Fnv1a("mana");
    private static readonly uint FCastRadius = HashAlgorithms.Fnv1a("castRadius");
    private static readonly uint FCastConeAngle = HashAlgorithms.Fnv1a("castConeAngle");
    private static readonly uint FCastConeDistance = HashAlgorithms.Fnv1a("castConeDistance");
    private static readonly uint FAnimationName = HashAlgorithms.Fnv1a("mAnimationName");
    private static readonly uint FSpellTags = HashAlgorithms.Fnv1a("mSpellTags");

    /// <summary>
    /// The 14 targeting classes Riot ships, by hash. Measured over all 692 slots: 588 author a
    /// <c>mTargetingTypeData</c>, every one is a pointer struct with an empty body, and every one of the
    /// 14 class hashes is in this table - there is no fifteenth. The hash database does not know these
    /// names, and the bin hash is case-folded, so the spelling is this table's; "direction" is lower-case
    /// because that is how Riot's own name list spells it.
    /// </summary>
    private static readonly IReadOnlyDictionary<uint, string> TargetingKinds = new[]
    {
        "Location", "Self", "direction", "Area", "SelfAoe", "LocationClamped", "TargetOrLocation", "Cone",
        "AreaClamped", "Target", "DragDirection", "WallDetection", "TerrainLocation", "TerrainType",
    }.ToDictionary(HashAlgorithms.Fnv1a, name => name);

    /// <summary>The four ability slots, in Q/W/E/R order. Empty for anything that is not a champion —
    /// a companion record, or TFTChampion, which ships a CharacterRecord with no spells at all.</summary>
    /// <param name="characterName">Used to pick the right record: several WADs ship more than one
    /// (Braum, Milio and Cassiopeia carry URF and SLIME twins), and taking the first is a coin toss.</param>
    public static IReadOnlyList<AbilitySlot> Read(byte[] recordBin, string? characterName = null)
    {
        try
        {
            var tree = new BinTree(new MemoryStream(recordBin));
            if (PickRecord(tree, characterName) is not { } record) return Array.Empty<AbilitySlot>();

            if (Get(record.Properties, FSpellNames) is not BinTreeContainer names) return Array.Empty<AbilitySlot>();
            var links = Get(record.Properties, FSpells) as BinTreeContainer;

            var slots = new List<AbilitySlot>();
            for (int i = 0; i < names.Elements.Count && i < 4; i++)
            {
                string entry = names.Elements[i] is BinTreeString s ? s.Value : "";
                // The ObjectLink is the address; the STRING is not. Deriving the object path from the
                // name works for 95% of entries and fails on bare names, on abilities with no ability
                // folder, and on the 132 links that point into another character entirely.
                uint root = links is not null && i < links.Elements.Count
                            && links.Elements[i] is BinTreeObjectLink link
                    ? link.Value
                    : 0u;

                slots.Add(ReadSlot(tree, i, entry, root));
            }
            return slots;
        }
        catch
        {
            // A record that will not parse costs the composite its data, not the preview its character.
            return Array.Empty<AbilitySlot>();
        }
    }

    private static BinTreeObject? PickRecord(BinTree tree, string? characterName)
    {
        BinTreeObject? first = null;
        foreach (var o in tree.Objects.Values)
        {
            if (o.ClassHash != ClassCharacterRecord) continue;
            if (Get(o.Properties, FSpellNames) is not BinTreeContainer { Elements.Count: > 0 }) continue;
            first ??= o;

            if (characterName is { Length: > 0 }
                && o.PathHash == HashAlgorithms.Fnv1a($"Characters/{characterName}/CharacterRecords/Root"))
                return o;
        }
        return first;
    }

    private static AbilitySlot ReadSlot(BinTree tree, int index, string entry, uint rootSpell)
    {
        string slot = index is >= 0 and < 4 ? "QWER"[index].ToString() : "?";
        var missiles = new List<AbilityMissile>();
        float castFrame = 0f, castTime = 0f;
        bool animatorFps = false;
        BinTreeStruct? data = null;

        // The root spell carries the timing, and sometimes IS the missile - Ezreal's Q is one spell that
        // both casts and flies. Ahri's Q is not: her missiles are sibling spells under the ability.
        if (tree.Objects.TryGetValue(rootSpell, out var root) && Spell(root) is { } rootSpell2)
        {
            data = rootSpell2;
            castFrame = F32(rootSpell2, FCastFrame);
            castTime = F32(rootSpell2, FCastTime);
            animatorFps = Get(rootSpell2.Properties, FUseAnimatorFps) is BinTreeBool { Value: true };
            if (ReadMissile(root, rootSpell2) is { } m) missiles.Add(m);
        }

        // The ability groups the caster spell with its missiles, and it is the only authored place that
        // does. Found by its root link rather than by name: the name half of "AhriQAbility/AhriQ" does
        // not resolve for bare-name entries at all.
        foreach (var ability in tree.Objects.Values)
        {
            if (ability.ClassHash != ClassAbility) continue;
            if (Get(ability.Properties, FRootSpell) is not BinTreeObjectLink link || link.Value != rootSpell) continue;
            if (Get(ability.Properties, FChildSpells) is not BinTreeContainer children) continue;

            foreach (var element in children.Elements)
            {
                if (element is not BinTreeObjectLink child || child.Value == rootSpell) continue;
                if (!tree.Objects.TryGetValue(child.Value, out var spellObject)) continue;
                if (Spell(spellObject) is not { } childSpell) continue;
                if (ReadMissile(spellObject, childSpell) is { } m) missiles.Add(m);
            }
            break;
        }

        // The casting envelope is the root spell's as well: Aatrox's Q is three child swings, and none of
        // them owns the slot's range, cooldown or targeting.
        return new AbilitySlot(index, slot, entry, castFrame, animatorFps, castTime, missiles)
        {
            TargetingKind = TargetingKindOf(data),
            CastRange = Rank1(data, FCastRange) ?? 0f,
            CastRangeDisplayOverride = Rank1(data, FCastRangeDisplayOverride),
            Cooldown = Rank1(data, FCooldownTime) ?? 0f,
            Mana = Rank1(data, FMana) ?? 0f,
            CastRadius = Rank1(data, FCastRadius) ?? 0f,
            CastConeAngle = data is null ? 0f : F32(data, FCastConeAngle),
            CastConeDistance = data is null ? 0f : F32(data, FCastConeDistance),
            AnimationName = AnimationNameOf(data),
            Tags = StringsOf(data, FSpellTags),
        };
    }

    /// <summary>A missile, or null when this spell launches none. A spell with no missile spec and no
    /// missile effect is a self-cast or an aura and has nothing to fly.</summary>
    private static AbilityMissile? ReadMissile(BinTreeObject spellObject, BinTreeStruct spell)
    {
        uint missileKey = Hash(spell, FMissileEffectKey);
        uint hitKey = Hash(spell, FHitEffectKey);
        var spec = Get(spell.Properties, FMissileSpec) as BinTreeStruct;
        if (spec is null && missileKey == 0) return null;

        var motion = MissileMotion.None;
        float width = 0f;
        if (spec is not null)
        {
            width = F32(spec, FMissileWidth);
            if (Get(spec.Properties, FMovement) is BinTreeStruct movement)
                motion = ReadMotion(movement);
        }

        string name = Get(spellObject.Properties, FScriptName) is BinTreeString sn ? sn.Value : "";
        return new AbilityMissile(name, missileKey, hitKey, motion, width);
    }

    /// <summary>
    /// Switched on the FIELDS, not on the movement class.
    ///
    /// <para>The component is polymorphic across 12 classes and only three of them carry
    /// <c>mSpeed</c> — a class table is easy to get wrong and its failure is silent: an earlier draft of
    /// this reader routed CircleMovement, SyncCircleMovement and DecelToLocationMovement to
    /// <c>mSpeed</c>, none of which has that field, and would have shipped a speed of zero for all 13.
    /// Asking what is authored cannot make that mistake, and it survives a class this reader has never
    /// heard of.</para>
    /// </summary>
    private static MissileMotion ReadMotion(BinTreeStruct movement)
    {
        if (F32(movement, FSpeed) is > 0f and var speed) return new MissileMotion(MissileMotionKind.ConstantSpeed, speed);
        if (F32(movement, FTravelTime) is > 0f and var seconds) return new MissileMotion(MissileMotionKind.FixedDuration, seconds);
        return MissileMotion.None;
    }

    // ---- property helpers -------------------------------------------------------------------------

    private static BinTreeProperty? Get(IReadOnlyDictionary<uint, BinTreeProperty> props, uint key) =>
        props.TryGetValue(key, out var value) ? value : null;

    /// <summary>mSpell is a POINTER struct (0x82), never Embedded and never Optional - measured
    /// 3,388 of 3,388.</summary>
    private static BinTreeStruct? Spell(BinTreeObject spellObject) =>
        Get(spellObject.Properties, FSpell) as BinTreeStruct;

    private static float F32(BinTreeStruct s, uint key) =>
        Get(s.Properties, key) is BinTreeF32 f ? f.Value : 0f;

    /// <summary>Effect keys are Hash in 1,852 of 1,852 - never a string. Reading them as strings is the
    /// M590 mistake in a different field, and its symptom is identical: nothing, silently.</summary>
    private static uint Hash(BinTreeStruct s, uint key) =>
        Get(s.Properties, key) is BinTreeHash h ? h.Value : 0u;

    private static BinTreeProperty? Unwrap(BinTreeProperty? property) =>
        property is BinTreeOptional optional ? optional.Value : property;

    /// <summary>
    /// The rank-1 entry of a per-level array, or null when the array is absent or empty.
    ///
    /// <para>Riot ships two shapes and they are NOT indexed alike. The 7-entry arrays (castRange,
    /// cooldownTime, castRadius, castRangeDisplayOverride) are indexed by spell LEVEL 0..6, and level 0 is
    /// the unlearned spell: Ezreal's R authors cooldownTime [10, 120, 105, 90, 10, 10, 10] - nobody would
    /// call 10 s the rank-1 cooldown of an ultimate - and his Q's [5.75, 5.5, 5.25, ...] puts the 5.5 the
    /// game shows at element 1. The 6-entry arrays (mana) start at level 1, so his Q's [28, 31, ...] is
    /// right at element 0. Taking element 0 of both is the obvious mistake, and it is silent for most
    /// spells because level 0 usually copies level 1.</para>
    /// </summary>
    private static float? Rank1(BinTreeStruct? s, uint key)
    {
        if (s is null || Unwrap(Get(s.Properties, key)) is not BinTreeContainer { Elements.Count: > 0 } levels) return null;
        int rank1 = levels.Elements.Count >= 7 ? 1 : 0;
        return levels.Elements[rank1] is BinTreeF32 f ? f.Value : null;
    }

    private static string TargetingKindOf(BinTreeStruct? spell)
    {
        if (spell is null || Unwrap(Get(spell.Properties, FTargetingTypeData)) is not BinTreeStruct t || t.ClassHash == 0)
            return "";
        return TargetingKinds.TryGetValue(t.ClassHash, out var name) ? name : $"0x{t.ClassHash:x8}";
    }

    private static string? AnimationNameOf(BinTreeStruct? spell) =>
        spell is not null
        && Get(spell.Properties, FAnimationName) is BinTreeString { Value: var clip }
        && !string.IsNullOrWhiteSpace(clip)
        && !clip.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? clip
            : null;

    private static IReadOnlyList<string> StringsOf(BinTreeStruct? spell, uint key)
    {
        if (spell is null || Unwrap(Get(spell.Properties, key)) is not BinTreeContainer { Elements.Count: > 0 } c)
            return Array.Empty<string>();

        var strings = new List<string>(c.Elements.Count);
        foreach (var element in c.Elements)
            if (element is BinTreeString { Value.Length: > 0 } s) strings.Add(s.Value);
        return strings;
    }
}
