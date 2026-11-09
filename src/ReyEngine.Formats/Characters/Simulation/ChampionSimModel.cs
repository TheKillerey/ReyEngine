namespace ReyEngine.Formats.Characters.Simulation;

/// <summary>
/// M867: the champion simulation definition - everything the shipped data says about how a champion's abilities
/// look and time out, in one deterministic document per champion and game version.
///
/// <para>It is DATA ONLY: nothing here moves, spawns or draws. The player (M869) and the in-app validator (M871)
/// read it. Every group of fields carries the evidence it came from (see <see cref="FieldSource"/>): "authored" is
/// read off a Riot record, "convention" comes from a naming rule the editor already uses (the name-token spell
/// composite, the SpellN clip names), "adapter" is reserved for per-champion code that a later milestone adds and
/// that this generator never writes.</para>
///
/// <para>References are explicit about being unresolved. A system reference keeps the effect-key hash AND the
/// resolved system name; when the key does not resolve the name is null and the generator lists it in
/// <see cref="Issues"/> instead of guessing. Hashes are written as "0x" + 8 lower-case hex digits.</para>
/// </summary>
public sealed record ChampionSimDefinition(
    int SchemaVersion,
    string GeneratorVersion,
    /// <summary>The part of <c>content-metadata.json</c> before the "+", e.g. "16.22.8348910"; "unknown" when the install has none.</summary>
    string GameVersion,
    /// <summary>The WAD / character folder name (<c>MonkeyKing</c> for Wukong).</summary>
    string Champion,
    int? ChampionId,
    string? DisplayName,
    string BaseSkinBin,
    /// <summary>The base skin's animation graph bin, or null when skin0.bin names none that resolves.</summary>
    string? AnimationGraph,
    IReadOnlyList<AbilitySim> Abilities,
    IReadOnlyList<AttackSim> Attacks,
    IReadOnlyList<string> Issues)
{
    public const int CurrentSchemaVersion = 1;
    /// <summary>Bumped whenever the generator's output for the same data would change.</summary>
    public const string CurrentGeneratorVersion = "M891.1";
}

/// <summary>A reference to a VFX system. <see cref="Name"/> is null when it did not resolve.</summary>
/// <param name="KeyHash">The effect key (a resource-map key), or null for a system found by naming convention.</param>
/// <param name="KeyName">The key's name when the hash tables know it.</param>
/// <param name="ObjectHash">The VfxSystemDefinitionData object hash the key resolved to, or null.</param>
/// <param name="Name">The system's own name, or null when unresolved.</param>
/// <param name="Visual">Whether the system has at least one visual emitter; null when unresolved.</param>
public sealed record SimSystemRef(string? KeyHash, string? KeyName, string? ObjectHash, string? Name, bool? Visual)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsResolved => Name is not null;
}

/// <summary>Where one group of fields came from: "authored", "convention" or "adapter".</summary>
public sealed record FieldSource(string Group, string Source);

/// <summary>A missile of an ability. <see cref="Source"/> is "authored" (a spell record) or "convention" (a "_mis" system by name).</summary>
/// <param name="MotionKind">"ConstantSpeed", "FixedDuration" or "Unknown".</param>
/// <param name="MotionSource">"mMissileSpec" (the movement component), "missileSpeed" (the spell's older flat field) or null.</param>
public sealed record MissileSim(
    string? SpellName,
    SimSystemRef? Effect,
    SimSystemRef? HitEffect,
    string MotionKind,
    float? Speed,
    float? Duration,
    string? MotionSource,
    float Width,
    string Source);

/// <summary>The animation clip chosen for an ability. <see cref="Source"/>: "authored" (the spell names the clip), "authored-composite" (the spell names a graph
/// sequencer/selector/parametric/condition entry and this is the atomic clip it plays first) or "convention".</summary>
public sealed record SimClip(
    string Name,
    string? AnmPath,
    string Source,
    float? Fps,
    float? DurationSeconds,
    /// <summary>Seconds per event frame: "tickDuration" (the clip authors one), "anmFps" (the .anm header) or "assumed30fps".</summary>
    float FrameSeconds,
    string TimeBasis,
    /// <summary>M873: the composite clip names walked from the authored name down to this clip (empty/null for an atomic clip).</summary>
    IReadOnlyList<string>? Via = null);

public sealed record ClipParticleEventSim(
    float Frame,
    float Seconds,
    string EffectKeyHash,
    string EffectName,
    SimSystemRef? System,
    string Bone,
    float? EndSeconds,
    bool IsLoop);

public sealed record ClipSoundEventSim(float Frame, float Seconds, string SoundName, bool IsLoop);

/// <summary>What a scenario needs to stage this ability. Only set from evidence; see the generator for the rules.</summary>
public sealed record ScenarioHint(bool NeedsEnemy, bool NeedsAlly, bool NeedsTarget, float? DistanceHint);

public sealed record AbilitySim(
    /// <summary>"P", "Q", "W", "E", "R"; a sub-cast ("Q2"), a form variant ("Q (assassin)") or an extra ability ("X:Name").</summary>
    string Key,
    /// <summary>The slot this entry belongs to ("Q" for "Q2"), or null for a top-level entry.</summary>
    string? ParentKey,
    /// <summary>0..3 for Q..R, 4 for the passive, 5+ for extra abilities, -1 for a sub-cast.</summary>
    int Slot,
    /// <summary>The record's spell entry (<c>AhriQAbility/AhriQ</c>), or the passive's lua name.</summary>
    string SpellName,
    string AbilityName,
    string ScriptName,
    string? RootSpellHash,
    string? RootSpellPath,
    IReadOnlyList<string> ChildSpellNames,
    string? DisplayName,
    string? IconPath,
    string TargetingKind,
    float CastRange,
    float CastRadius,
    float ConeAngle,
    IReadOnlyList<string> Tags,
    /// <summary>Raw <c>mAffectsTypeFlags</c>. Not decoded - which bit means enemy or ally has not been researched.</summary>
    uint? AffectsTypeFlags,
    /// <summary>Authored clip name of the spell (<c>Spell1</c>), null when it names none or "None".</summary>
    string? AuthoredClipName,
    SimClip? Clip,
    float? CastSeconds,
    float? TotalSeconds,
    /// <summary>The spell's separate <c>spellCastTime</c> field, raw; not used to fill <see cref="CastSeconds"/>.</summary>
    float? SpellCastTime,
    IReadOnlyList<MissileSim> Missiles,
    IReadOnlyList<SimSystemRef> CasterSystems,
    IReadOnlyList<SimSystemRef> TargetSystems,
    IReadOnlyList<SimSystemRef> ReturnSystems,
    IReadOnlyList<ClipParticleEventSim> ClipParticleEvents,
    IReadOnlyList<ClipSoundEventSim> ClipSoundEvents,
    ScenarioHint Scenario,
    IReadOnlyList<FieldSource> Sources,
    IReadOnlyList<string> Issues,
    /// <summary>M873: when the authored clip is a sequencer, every clip it plays in order (the first is <see cref="Clip"/>); null for a single clip.</summary>
    IReadOnlyList<SimClip>? ClipChain = null,
    /// <summary>M875: location / terminal composite systems (end, explosion, trap, zone...), played at the aim or hit point rather than on the caster.</summary>
    IReadOnlyList<SimSystemRef>? AimSystems = null,
    /// <summary>M882: a root spell that authors no clip of its own but has a child <c>*Attack</c> spell that does (Blitzcrank E PowerFistAttack, Nasus Q): the root plays no cast clip and the
    /// simulation plays this attack - the child's clip, cast time and hit effect - on the basic attack that follows. Null otherwise.</summary>
    AttackSim? FollowUpAttack = null,
    /// <summary>M891: characters the ability's <c>.preload</c> chain names (BBPreloadCharacter) - the units it summons. Null when no game-file reader was given; empty when the scripts name none.</summary>
    IReadOnlyList<string>? PreloadCharacters = null,
    /// <summary>M891: helper spells the preload chain names (BBPreloadSpell), in discovery order, not including the ability's own scripts.</summary>
    IReadOnlyList<string>? PreloadSpells = null,
    /// <summary>M891: buff effects the chain's <c>.luabin64</c> scripts author (AutoBuffActivateEffect and its attach bone).</summary>
    IReadOnlyList<BuffEffectSim>? BuffEffects = null,
    /// <summary>M891: the game paths of the preload and luabin64 files that were read for the three fields above.</summary>
    IReadOnlyList<string>? ScriptFiles = null);

/// <summary>M891: an effect a spell script attaches while its buff is active. Authored in the script's property table; <see cref="AttachBone"/> is null when the script names none.</summary>
public sealed record BuffEffectSim(string Spell, string? BuffName, string Effect, string? AttachBone, string File);

/// <summary>A basic attack (or crit) from the champion record.</summary>
public sealed record AttackSim(
    string Name,
    bool IsCrit,
    /// <summary>Whether <see cref="AttackSpell.Pool"/> would pick it for a plain swing.</summary>
    bool InBasicPool,
    float Probability,
    string ClipName,
    SimClip? Clip,
    float? CastSeconds,
    SimSystemRef? HitEffect,
    IReadOnlyList<MissileSim> Missiles,
    IReadOnlyList<string> Issues);
