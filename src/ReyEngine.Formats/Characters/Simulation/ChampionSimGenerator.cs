using System.Globalization;
using System.Text.RegularExpressions;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Skeletons;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.Formats.Characters.Simulation;

/// <summary>What the generator knows about the install besides the champion's own WAD.</summary>
public sealed class ChampionSimContext
{
    public string GameVersion { get; init; } = "unknown";
    public Func<uint, string?> ResolveBinName { get; init; } = _ => null;
    public Func<ulong, string?> ResolveWadPath { get; init; } = _ => null;
    /// <summary>Optional: the client's champion list, for the id and display name.</summary>
    public ClientNameCatalog? Client { get; init; }
    /// <summary>Optional: the client's ability names and icons.</summary>
    public ClientAbilityCatalog? ClientAbilities { get; init; }
}

/// <summary>
/// M867: builds a <see cref="ChampionSimDefinition"/> from a champion's shipped data. Deterministic: no timestamps,
/// invariant culture, every list in a fixed order (abilities P, Q, W, E, R, then the record's extra abilities, then the
/// composite-derived sub-casts; systems sorted by name then hash; clip events by frame).
///
/// <para>Where each fact comes from (this is also what <see cref="FieldSource"/> records per ability):</para>
/// <list type="bullet">
/// <item><b>authored</b>: the champion record - spell names, timing, targeting, range, missiles (effect key, hit key, speed or
/// duration, width), the clip a spell names, and the particle/sound events of that clip.</item>
/// <item><b>convention</b>: <see cref="ChampionEventBuilder"/>'s name-token composites (a system called <c>..._q_..._tar</c> is
/// Q's target effect), the SpellN / slot-letter clip names when a spell names no clip, and the scenario hint. Ability impact
/// effects are linked by compiled scripts that ship nowhere, so the name tokens are the only bridge.</item>
/// </list>
/// Nothing is invented: an unresolved key stays a reference with a null name and is listed in the ability's issues.
/// </summary>
public static class ChampionSimGenerator
{
    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

    /// <summary>Open the champion's inputs from <paramref name="wad"/> and generate its definition.</summary>
    public static ChampionSimDefinition Generate(WadArchive wad, string champion, ChampionSimContext context) =>
        Generate(ChampionSimSource.Load(wad, champion, context.ResolveBinName, context.ResolveWadPath), context);

    public static ChampionSimDefinition Generate(ChampionSimInputs inputs, ChampionSimContext context)
    {
        var g = new Run(inputs, context);
        return g.Build();
    }

    private static string Hex(uint h) => "0x" + h.ToString("x8", CultureInfo.InvariantCulture);

    private sealed class Run
    {
        private readonly ChampionSimInputs _in;
        private readonly ChampionSimContext _ctx;
        private readonly HashSet<string> _usedKeys = new(StringComparer.Ordinal);

        public Run(ChampionSimInputs inputs, ChampionSimContext context) { _in = inputs; _ctx = context; }

        public ChampionSimDefinition Build()
        {
            var issues = new List<string>(_in.Issues);
            var records = _in.RecordBin is null
                ? Array.Empty<AbilityRecordData>()
                : ChampionSpellData.ReadAll(_in.RecordBin, _in.Champion);
            var attacks = _in.RecordBin is null
                ? Array.Empty<AttackSpell>()
                : ChampionSpellData.ReadAttacks(_in.RecordBin, _in.Champion);
            if (_in.RecordBin is not null && records.Count == 0) issues.Add("the champion record authors no abilities");

            // ---- client enrichment (never required)
            int? id = null; string? display = null; ClientChampionAbilities? clientAbilities = null;
            if (_ctx.Client?.Champion(_in.Champion) is { } cc)
            {
                id = cc.Id;
                display = cc.DisplayName.Length > 0 ? cc.DisplayName : null;
                clientAbilities = _ctx.ClientAbilities?.Get(cc.Id);
            }

            // ---- composites: the name-token bridge to ability VFX
            var authoredMissiles = records
                .Where(r => r.Key.Length == 1 && r.Key != "X")
                .ToDictionary(r => r.Key, r => (IReadOnlyList<uint>)r.Slot.Missiles
                        .Select(m => _in.ResourceMap.TryGetValue(m.MissileEffectKey, out var h) ? h : 0u)
                        .Where(h => _in.Systems.ContainsKey(h)).Distinct().ToList(),
                    StringComparer.OrdinalIgnoreCase);
            var events = ChampionEventBuilder.Build(_in.Systems, _in.Clips.ToList(), authoredMissiles);

            var spellEntries = new[] { "Q", "W", "E", "R" }
                .Select(k => records.FirstOrDefault(r => r.Key == k)?.SpellEntry ?? "").ToList();
            var actions = CharacterActions.Build(spellEntries, _in.Clips);

            var abilities = new List<AbilitySim>();
            _usedKeys.Clear();
            foreach (var rec in records)
                abilities.Add(BuildAbility(rec, events, actions, clientAbilities, _usedKeys));

            // sub-casts and form variants: composite events that are not a bare slot letter
            var subs = new List<(int Rank, string Name, ChampionEvent Event, string Parent)>();
            foreach (var ev in events)
            {
                var m = SubCast.Match(ev.Name);
                if (!m.Success) continue;
                string parent = m.Groups[1].Value.ToUpperInvariant();
                if (m.Groups[2].Length == 0 && m.Groups[3].Length == 0) continue;   // a bare slot letter is the ability itself
                subs.Add(("PQWER".IndexOf(parent[0], StringComparison.Ordinal), ev.Name, ev, parent));
            }
            foreach (var (_, name, ev, parent) in subs.OrderBy(s => s.Rank).ThenBy(s => s.Name, StringComparer.Ordinal))
                if (abilities.FirstOrDefault(a => a.Key == parent) is { } owner)
                    abilities.Add(BuildSubCast(name, ev, owner, records.First(r => r.Key == parent)));

            var attackSims = attacks.Select(BuildAttack).ToList();
            var pool = AttackSpell.Pool(attacks, _in.Champion);
            attackSims = attackSims.Select((a, i) => a with { InBasicPool = pool.Contains(attacks[i]) }).ToList();

            return new ChampionSimDefinition(
                ChampionSimDefinition.CurrentSchemaVersion,
                ChampionSimDefinition.CurrentGeneratorVersion,
                _ctx.GameVersion,
                _in.Champion,
                id,
                display,
                _in.BaseSkinBin,
                _in.AnimationGraph,
                abilities,
                attackSims,
                issues);
        }

        // ---- abilities ---------------------------------------------------------------------------------

        private AbilitySim BuildAbility(AbilityRecordData rec, List<ChampionEvent> events,
            IReadOnlyList<CharacterAction> actions, ClientChampionAbilities? client, HashSet<string> used)
        {
            var slot = rec.Slot;
            var issues = new List<string>();
            var sources = new List<FieldSource>();
            string key = rec.Key == "X"
                ? "X:" + (rec.AbilityName.Length > 0 ? rec.AbilityName : rec.SpellEntry)
                : rec.Key;
            // two extra abilities can share a name; a key must stay unique inside one definition
            for (int n = 2; used.Contains(key); n++) key = $"{key}#{n}";
            used.Add(key);
            bool hasRoot = slot.RootSpellHash != 0 && slot.ScriptName.Length > 0;
            if (hasRoot) sources.Add(new FieldSource("spell", "authored"));
            else issues.Add("the record names no root spell for this ability");

            // ---- clip: the spell's own name first, a naming convention only when the spell names none
            SimClip? clip = null;
            string? authored = slot.AnimationName;
            bool hasSubCasts = events.Any(e => SubCast.Match(e.Name) is { Success: true } m
                && m.Groups[1].Value.Equals(rec.Key, OIC) && (m.Groups[2].Length > 0 || m.Groups[3].Length > 0));
            if (authored is not null)
            {
                if (ResolveAuthoredClip(authored) is { } found)
                {
                    clip = MakeClip(found.Clip, found.Source);
                    sources.Add(new FieldSource("clip", found.Source));
                }
                else issues.Add($"clip '{authored}' is not in the base animation graph");
            }
            else if (rec.Key.Length == 1 && "QWER".Contains(rec.Key[0]) && !hasSubCasts
                     && actions[slot.Index].Clip is { } conventional)
            {
                clip = MakeClip(conventional, "convention");
                sources.Add(new FieldSource("clip", "convention"));
            }
            else sources.Add(new FieldSource("clip", "none"));

            // ---- timing
            float? castSeconds = null;
            if (slot.CastFrame > 0f || slot.CastTimeSeconds > 0f)
            {
                if (slot.CastTimeSeconds <= 0f && slot.UseAnimatorFramerate && clip?.Fps is not > 1f)
                    issues.Add("cast frame counts at the clip's frame rate, which could not be read; 30 fps assumed");
                castSeconds = slot.CastSecondsAt(clip?.Fps ?? 0f);
            }
            else if (rec.Key != "P") issues.Add("the spell authors no cast frame or cast time");

            // ---- missiles: authored first
            var missiles = new List<MissileSim>();
            foreach (var m in slot.Missiles) missiles.Add(MakeMissile(m, issues));
            if (missiles.Count > 0) sources.Add(new FieldSource("missiles", "authored"));

            // ---- composite systems (name tokens)
            var casters = new List<SimSystemRef>(); var targets = new List<SimSystemRef>(); var returns = new List<SimSystemRef>();
            if (rec.Key.Length == 1)
            {
                var ev = events.FirstOrDefault(e => e.Name == rec.Key);
                if (ev is not null)
                {
                    casters.AddRange(ObjectRefs(ev.CasterSystems));
                    targets.AddRange(ObjectRefs(ev.TargetSystems));
                    returns.AddRange(ObjectRefs(ev.ReturnMissileSystems));
                    if (missiles.Count == 0)
                        foreach (var r in ObjectRefs(ev.MissileSystems))
                            missiles.Add(new MissileSim(null, r, null, "Unknown", null, null, null, 0f, "convention"));
                    if (casters.Count + targets.Count + returns.Count > 0 || ev.MissileSystems.Count > 0)
                        sources.Add(new FieldSource("systems", "convention"));
                }
            }

            var (particles, sounds) = ClipEvents(clip);
            if (clip is not null) sources.Add(new FieldSource("clipEvents", "authored"));

            // ---- display names from the client
            ClientAbilityInfo? info = null;
            if (client is not null)
                info = rec.Key == "P" ? client.Passive : client.Spells.GetValueOrDefault(rec.Key);
            if (info is not null) sources.Add(new FieldSource("display", "client"));

            var scenario = Scenario(slot, missiles, targets);
            sources.Add(new FieldSource("scenario", "convention"));

            return new AbilitySim(
                key, null, slot.Index, rec.SpellEntry, rec.AbilityName, slot.ScriptName,
                slot.RootSpellHash != 0 ? Hex(slot.RootSpellHash) : null,
                slot.RootSpellHash != 0 ? _ctx.ResolveBinName(slot.RootSpellHash) : null,
                rec.Children.Select(c => c.Name).Where(n => n.Length > 0).ToList(),
                info?.Name, info?.IconPath,
                slot.TargetingKind, slot.CastRange, slot.CastRadius, slot.CastConeAngle,
                slot.Tags, slot.AffectsTypeFlags,
                authored, clip, castSeconds, slot.SpellTotalTime, slot.SpellCastTime,
                missiles, SortRefs(casters), SortRefs(targets), SortRefs(returns),
                particles, sounds, scenario, sources, issues);
        }

        private AbilitySim BuildSubCast(string name, ChampionEvent ev, AbilitySim owner, AbilityRecordData rec)
        {
            var issues = new List<string>();
            var sources = new List<FieldSource> { new("systems", "convention") };
            string letter = owner.Key;

            SimClip? clip = null;
            if (ev.ClipAnmFile is { } file
                && _in.Clips.FirstOrDefault(c => Path.GetFileName(c.AnmPath.Replace('\\', '/')).Equals(file, OIC)) is { } c)
            {
                clip = MakeClip(c, "convention");
                sources.Add(new FieldSource("clip", "convention"));
            }
            else
            {
                sources.Add(new FieldSource("clip", "none"));
                issues.Add("no clip file is named by the composite");
            }

            // cast number N -> the child spell "<root script><N>" (AatroxQ2); N = 1 -> the root itself. A naming rule.
            float? castSeconds = null;
            string script = "";
            var number = Regex.Match(name, @"^[PQWER](\d)");
            if (number.Success)
            {
                string n = number.Groups[1].Value;
                var child = rec.Children.FirstOrDefault(ch => ch.Name.Equals(rec.Slot.ScriptName + n, StringComparison.Ordinal));
                if (child is not null)
                {
                    script = child.Name;
                    if (child.CastFrame > 0f || child.CastTimeSeconds > 0f)
                    {
                        float fps = clip?.Fps is > 1f ? clip.Fps.Value : 30f;
                        castSeconds = child.CastTimeSeconds > 0f ? child.CastTimeSeconds
                            : child.CastFrame / (child.UseAnimatorFramerate ? fps : 30f);
                        sources.Add(new FieldSource("spell", "convention"));
                    }
                }
            }
            if (castSeconds is null) issues.Add("no cast timing for this sub-cast");

            var casters = ObjectRefs(ev.CasterSystems).ToList();
            var targets = ObjectRefs(ev.TargetSystems).ToList();
            var returns = ObjectRefs(ev.ReturnMissileSystems).ToList();
            var missiles = ObjectRefs(ev.MissileSystems)
                .Select(r => new MissileSim(null, r, null, "Unknown", null, null, null, 0f, "convention")).ToList();

            var (particles, sounds) = ClipEvents(clip);
            if (clip is not null) sources.Add(new FieldSource("clipEvents", "authored"));
            sources.Add(new FieldSource("envelope", "authored"));
            sources.Add(new FieldSource("scenario", "convention"));

            return new AbilitySim(
                name, letter, -1, owner.SpellName, owner.AbilityName, script,
                null, null, Array.Empty<string>(), null, null,
                owner.TargetingKind, owner.CastRange, owner.CastRadius, owner.ConeAngle,
                owner.Tags, owner.AffectsTypeFlags,
                null, clip, castSeconds, null, null,
                missiles, SortRefs(casters), SortRefs(targets), SortRefs(returns),
                particles, sounds,
                new ScenarioHint(missiles.Count > 0 || targets.Count > 0 || owner.TargetingKind == "Cone", false,
                    owner.TargetingKind is "Target" or "TargetOrLocation", DistanceHint(owner.CastRange)),
                sources, issues);
        }

        private AttackSim BuildAttack(AttackSpell a)
        {
            var issues = new List<string>();
            SimClip? clip = ResolveAuthoredClip(a.ClipName) is { } found ? MakeClip(found.Clip, found.Source) : null;
            if (clip is null) issues.Add($"clip '{a.ClipName}' is not in the base animation graph");
            float? cast = a.CastFrame > 0f || a.CastTimeSeconds > 0f ? a.CastSecondsAt(clip?.Fps ?? 0f) : null;
            return new AttackSim(a.Name, a.IsCrit, false, a.Probability, a.ClipName, clip, cast,
                Ref(a.HitEffectKey, "hit effect", issues),
                a.Missiles.Select(m => MakeMissile(m, issues)).ToList(), issues);
        }

        // ---- pieces ------------------------------------------------------------------------------------

        private MissileSim MakeMissile(AbilityMissile m, List<string> issues)
        {
            string kind = "Unknown"; float? speed = null, duration = null; string? motionSource = null;
            if (m.Motion.Kind == MissileMotionKind.ConstantSpeed && m.Motion.Value > 0f)
            { kind = "ConstantSpeed"; speed = m.Motion.Value; motionSource = "mMissileSpec"; }
            else if (m.Motion.Kind == MissileMotionKind.FixedDuration && m.Motion.Value > 0f)
            { kind = "FixedDuration"; duration = m.Motion.Value; motionSource = "mMissileSpec"; }
            else if (m.LegacySpeed > 1f)
            { kind = "ConstantSpeed"; speed = m.LegacySpeed; motionSource = "missileSpeed"; }

            var effect = Ref(m.MissileEffectKey, $"missile {m.SpellName} effect", issues);
            if (m.MissileEffectKey == 0) issues.Add($"missile {m.SpellName} authors no effect key");
            var hit = Ref(m.HitEffectKey, $"missile {m.SpellName} hit effect", issues);
            if (kind == "Unknown") issues.Add($"missile {m.SpellName} authors no speed or flight time");
            return new MissileSim(m.SpellName, effect, hit, kind, speed, duration, motionSource, m.Width, "authored");
        }

        /// <summary>An effect key through the resource map to a system; null for key 0 (nothing authored).</summary>
        private SimSystemRef? Ref(uint key, string what, List<string> issues)
        {
            if (key == 0) return null;
            string? keyName = _ctx.ResolveBinName(key);
            if (_in.ResourceMap.TryGetValue(key, out var obj))
            {
                if (_in.Systems.TryGetValue(obj, out var def))
                    return new SimSystemRef(Hex(key), keyName, Hex(obj), def.Name, def.Emitters.Any(e => e.IsVisual));
                issues.Add($"{what}: key {Hex(key)} maps to system {Hex(obj)}, which is not in the skin's VFX closure");
                return new SimSystemRef(Hex(key), keyName, Hex(obj), null, null);
            }
            issues.Add($"{what}: effect key {Hex(key)}{(keyName is null ? "" : " (" + keyName + ")")} is not in the skin's resource map");
            return new SimSystemRef(Hex(key), keyName, null, null, null);
        }

        /// <summary>Composite systems are addressed by object hash, with no effect key.</summary>
        private IEnumerable<SimSystemRef> ObjectRefs(IEnumerable<uint> objects)
        {
            foreach (var obj in objects)
                yield return _in.Systems.TryGetValue(obj, out var def)
                    ? new SimSystemRef(null, null, Hex(obj), def.Name, def.Emitters.Any(e => e.IsVisual))
                    : new SimSystemRef(null, null, Hex(obj), null, null);
        }

        private static IReadOnlyList<SimSystemRef> SortRefs(IEnumerable<SimSystemRef> refs) =>
            refs.OrderBy(r => r.Name ?? "￿", StringComparer.Ordinal).ThenBy(r => r.ObjectHash, StringComparer.Ordinal).ToList();

        private AnimClipInfo? FindClip(string name) => _in.Clips.FirstOrDefault(c => c.Name.Equals(name, OIC));

        /// <summary>
        /// The graph clip a spell's <c>mAnimationName</c> plays. Measured on Kalista (16.22): the spell names "Spell1", "Spell3" and
        /// "Spell4_Call", and the base graph has NONE of those as a resolved clip name - it has <c>Spell1_Base</c>, <c>Spell1_Up</c>,
        /// <c>Spell3_Base</c>, <c>Spell4_Call_BASE</c> and a clip whose key is the bare hash 0x25539770. So an exact-name test alone
        /// would call three of her four casts clip-less. The ladder, most to least authored:
        /// <list type="number">
        /// <item>the exact clip name (authored);</item>
        /// <item>a clip whose UNRESOLVED key hash is the hash of the authored name - the hash table simply lacks the name (authored);</item>
        /// <item>a <c>&lt;name&gt;_Base</c> clip, then the shortest other <c>&lt;name&gt;_...</c> variant by name (convention: the same
        /// prefix-and-separator rule <see cref="CharacterActions"/> uses, with the ordering made explicit).</item>
        /// </list>
        /// </summary>
        private (AnimClipInfo Clip, string Source)? ResolveAuthoredClip(string name)
        {
            if (FindClip(name) is { } exact) return (exact, "authored");
            string hashName = "0x" + ReyEngine.Core.Hashing.HashAlgorithms.Fnv1a(name).ToString("x8", CultureInfo.InvariantCulture);
            if (_in.Clips.FirstOrDefault(c => c.Name.Equals(hashName, OIC)) is { } byHash) return (byHash, "authored");

            var variants = _in.Clips
                .Where(c => c.Name.Length > name.Length && c.Name.StartsWith(name, OIC) && c.Name[name.Length] is '_' or '-' or ' ')
                .OrderBy(c => c.Name.Equals(name + "_Base", OIC) ? 0 : 1)
                .ThenBy(c => c.Name.Length)
                .ThenBy(c => c.Name, StringComparer.Ordinal)
                .ToList();
            return variants.Count > 0 ? (variants[0], "convention") : null;
        }

        private SimClip MakeClip(AnimClipInfo c, string source)
        {
            string? anm = string.IsNullOrWhiteSpace(c.AnmPath) ? null : c.AnmPath.Replace('\\', '/');
            var timing = anm is null ? null : _in.ReadClipTiming(anm);
            float frame; string basis;
            if (c.TickDuration is > 0.0001f and < 1f) { frame = c.TickDuration; basis = "tickDuration"; }
            else if (timing is { Fps: > 1f and < 240f } t) { frame = 1f / t.Fps; basis = "anmFps"; }
            else { frame = 1f / 30f; basis = "assumed30fps"; }
            return new SimClip(c.Name, anm, source, timing?.Fps, timing?.Duration, frame, basis);
        }

        private (IReadOnlyList<ClipParticleEventSim>, IReadOnlyList<ClipSoundEventSim>) ClipEvents(SimClip? sim)
        {
            if (sim is null || FindClip(sim.Name) is not { } clip)
                return (Array.Empty<ClipParticleEventSim>(), Array.Empty<ClipSoundEventSim>());
            var scratch = new List<string>();
            var particles = (clip.ParticleEvents ?? Array.Empty<AnimParticleEvent>())
                .Where(e => !e.IsKill)
                .Select(e => new ClipParticleEventSim(
                    e.StartFrame, e.StartFrame * sim.FrameSeconds, Hex(e.EffectHash), e.EffectName,
                    Ref(e.EffectHash, "clip event", scratch), e.BoneName,
                    e.EndFrame >= 0f ? e.EndFrame * sim.FrameSeconds : null, e.IsLoop))
                .OrderBy(e => e.Frame).ThenBy(e => e.EffectName, StringComparer.Ordinal).ToList();
            var sounds = (clip.SoundEvents ?? Array.Empty<AnimSoundEvent>())
                .Select(e => new ClipSoundEventSim(e.StartFrame, e.StartFrame * sim.FrameSeconds, e.SoundName, e.IsLoop))
                .OrderBy(e => e.Frame).ThenBy(e => e.SoundName, StringComparer.Ordinal).ToList();
            return (particles, sounds);
        }

        /// <summary>
        /// A scenario needs an enemy only on evidence that something lands on one: the ability flies a missile, owns a
        /// target-side system, or is a cone. It needs an ally never - <c>mAffectsTypeFlags</c> is not decoded, so no
        /// ally requirement can be read yet (an adapter sets it). "Target" targeting asks for a unit, not for a team.
        /// </summary>
        private static ScenarioHint Scenario(AbilitySlot slot, IReadOnlyList<MissileSim> missiles, IReadOnlyList<SimSystemRef> targets) =>
            new(missiles.Count > 0 || targets.Count > 0 || slot.TargetingKind == "Cone",
                false,
                slot.TargetingKind is "Target" or "TargetOrLocation",
                DistanceHint(slot.CastRangeDisplay));

        /// <summary>min(range, 900); null when the spell authors no range. 25,000 is Riot's "not range-limited", which is 900 here.</summary>
        private static float? DistanceHint(float range) => range > 0f ? MathF.Min(range, 900f) : null;
    }

    /// <summary>"Q2", "Q (assassin)", "Q1 (primary)" - group 1 the slot letter, 2 the cast number, 3 the form.</summary>
    private static readonly Regex SubCast = new(@"^([PQWER])(\d?)(?: \((.+)\))?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
