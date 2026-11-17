using System;
using System.Collections.Generic;
using System.Linq;
using ReyEngine.Formats.Skeletons;

namespace ReyEngine.Formats.Vfx;

/// <summary>
/// M116: one playable champion event — either a spell composite assembled from Riot's VFX naming
/// (caster systems on the champion, "_tar"/"_hit" on the target dummy, "_mis" travelling between),
/// or an authored animation sequence (recall/emote/transform clips that carry their own VFX/SFX).
/// </summary>
public sealed class ChampionEvent
{
    public required string Name { get; init; }
    /// <summary>The .anm file (by name) whose clip should play with this event; null = VFX only.</summary>
    public string? ClipAnmFile { get; init; }
    public IReadOnlyList<uint> CasterSystems { get; init; } = Array.Empty<uint>();
    public IReadOnlyList<uint> TargetSystems { get; init; } = Array.Empty<uint>();
    public IReadOnlyList<uint> MissileSystems { get; init; } = Array.Empty<uint>();
    public IReadOnlyList<uint> ReturnMissileSystems { get; init; } = Array.Empty<uint>();
    /// <summary>M875: location / terminal systems (end, explosion, aoe, zone, trap...) - they play at the aim or hit point, not on the caster.</summary>
    public IReadOnlyList<uint> AimSystems { get; init; } = Array.Empty<uint>();
    /// <summary>M875: systems the convention rules left out (alternatives, timed states), with the reason; the simulation lists them in the ability's issues.</summary>
    public IReadOnlyList<string> DroppedSystemNames { get; init; } = Array.Empty<string>();
    public bool NeedsTarget => TargetSystems.Count > 0 || MissileSystems.Count > 0;
    public bool HasSystems => CasterSystems.Count > 0 || AimSystems.Count > 0 || NeedsTarget;

    public string Description
    {
        get
        {
            var parts = new List<string>();
            if (CasterSystems.Count > 0) parts.Add($"{CasterSystems.Count} caster");
            if (AimSystems.Count > 0) parts.Add($"{AimSystems.Count} aim");
            if (MissileSystems.Count > 0) parts.Add($"{MissileSystems.Count} missile");
            if (ReturnMissileSystems.Count > 0) parts.Add($"{ReturnMissileSystems.Count} return");
            if (TargetSystems.Count > 0) parts.Add($"{TargetSystems.Count} target");
            if (ClipAnmFile is not null) parts.Add(System.IO.Path.GetFileNameWithoutExtension(ClipAnmFile));
            return parts.Count > 0 ? string.Join(" · ", parts) : "";
        }
    }
}

/// <summary>
/// M875: the one place the system-name token rules live. A name-matched system used to play on the caster at t=0 unless it said tar/hit/mis; that put
/// ground effects, target marks and every alternative of one effect on the champion together.
/// </summary>
public static class ChampionSystemTokens
{
    public enum Role { Caster, Target, Missile, Aim }

    /// <summary>Location or terminal effects: they happen where the spell lands.</summary>
    public static readonly HashSet<string> AimTokens = new(StringComparer.OrdinalIgnoreCase)
    { "end", "explosion", "explode", "detonate", "detonation", "aoe", "zone", "ground", "trap", "mine" };

    /// <summary>States an ability leaves ON the unit it hit.</summary>
    public static readonly HashSet<string> TargetStateTokens = new(StringComparer.OrdinalIgnoreCase)
    { "debuff", "mark", "marked", "moonlight", "root", "rooted", "tether", "tethered", "snare", "stun", "charm", "taunt", "fear", "sleep" };

    /// <summary>Timed states of a placed effect; they play when the game reaches that phase, not at the cast.</summary>
    public static readonly HashSet<string> TimedStateTokens = new(StringComparer.OrdinalIgnoreCase)
    { "idle", "ready", "indicator", "warning" };

    /// <summary>Alternatives of one effect that must not play together: (kept, dropped) team-colour pairs.</summary>
    private static readonly (string Keep, string Drop)[] TeamPairs =
        { ("ally", "enemy"), ("blue", "red"), ("order", "chaos"), ("friendly", "hostile") };
    private static readonly string[] QuadrantTokens = { "ne", "nw", "se", "sw" };

    /// <summary>The reason a system is not played, or null. <paramref name="names"/> holds every system name of the skin (case-insensitive).</summary>
    public static string? DropReason(string name, ISet<string> names)
    {
        var tokens = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in tokens)
        {
            string tk = raw.ToLowerInvariant();
            if (tk == "onkill") return "on-kill variant";
            if (tk == "timeout") return "timeout variant";
            if (tk.Length > 4 && tk.EndsWith("only", StringComparison.Ordinal)) return "single-champion variant";
            if (TimedStateTokens.Contains(tk)) return "timed state (" + tk + ")";
        }
        for (int i = 0; i < tokens.Length; i++)
        {
            string tk = tokens[i].ToLowerInvariant();
            foreach (var (keep, drop) in TeamPairs)
            {
                if (tk != keep && tk != drop) continue;
                // the plain form (token removed) wins; of a colour pair only the first half plays
                if (names.Contains(string.Join('_', tokens.Where((_, j) => j != i)))) return "team-colour alternative (" + tk + ")";
                if (tk == drop && names.Contains(string.Join('_', tokens.Select((t, j) => j == i ? keep : t)))) return "team-colour alternative (" + tk + ")";
            }
            if (Array.IndexOf(QuadrantTokens, tk) >= 0)
                foreach (var other in QuadrantTokens)
                    if (string.CompareOrdinal(other, tk) < 0
                        && names.Contains(string.Join('_', tokens.Select((t, j) => j == i ? other : t)))) return "quadrant sibling";
        }
        return null;
    }

    /// <summary>M903: tokens a PASSIVE's system carries when it is a state or an event on the enemy. The data has no field for it (the buff data authors no effect, the passive has no
    /// missile or hit; the effects are spawned by the compiled script), so it is the name: a state token or a token that ends in "mark" (FireMark, FireMark2), or a detonation.
    /// A ring or a timer counts only beside a mark of the same passive (<paramref name="names"/>): Brand's redring and Timer belong to his FireMarks, Singed's or Swain's own timer to themselves.
    /// Self states (buf / buff / self) and sounds stay on the caster.</summary>
    private static readonly HashSet<string> PassiveEnemyExtra = new(StringComparer.OrdinalIgnoreCase) { "detonate", "detonation" };
    private static readonly HashSet<string> PassiveMarkFamily = new(StringComparer.OrdinalIgnoreCase) { "redring", "ring", "timer" };
    private static readonly HashSet<string> PassiveSelfTokens = new(StringComparer.OrdinalIgnoreCase) { "buf", "buff", "self", "audio", "sound" };

    private static string Stem(string token) => token.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

    private static bool IsMarkToken(string token)
    {
        string stem = Stem(token);
        return stem.Length > 0 && (stem.Equals("mark", StringComparison.OrdinalIgnoreCase)
            || (stem.Length > 4 && stem.EndsWith("mark", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool PassiveIsOnEnemy(string[] tokens, ISet<string>? names)
    {
        if (tokens.Any(t => PassiveSelfTokens.Contains(Stem(t)))) return false;
        if (tokens.Any(t => TargetStateTokens.Contains(Stem(t)) || PassiveEnemyExtra.Contains(Stem(t)) || IsMarkToken(t))) return true;
        return names is not null && tokens.Any(t => PassiveMarkFamily.Contains(Stem(t)))
            && names.Any(n => n.Split('_', StringSplitOptions.RemoveEmptyEntries).Any(IsMarkToken));
    }

    /// <summary>Where a surviving system plays. tar/hit and mis keep their existing meaning and win over the new tables.</summary>
    public static Role Classify(string name, bool passive = false, ISet<string>? names = null)
    {
        var tokens = name.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(t => t.ToLowerInvariant()).ToArray();
        if (tokens.Any(t => t is "tar" or "hit")) return Role.Target;
        if (tokens.Contains("mis")) return Role.Missile;
        // a passive is not aimed and the planner gives it no hit: its states and placed effects stay with the caster (M873 behaviour) -
        // except the ones whose name says they are on the ENEMY (M903: Brand's FireMark / FireMark2 / FireMark3 stack marks, the detonation, the ring and the timer)
        if (passive) return PassiveIsOnEnemy(tokens, names) ? Role.Target : Role.Caster;
        if (tokens.Any(TargetStateTokens.Contains)) return Role.Target;
        if (tokens.Any(AimTokens.Contains)) return Role.Aim;
        return Role.Caster;
    }
}

/// <summary>
/// Builds the EVENTS list for a skin. Spell composites are name-driven — Riot's convention, verified
/// on Kayn's kit: role tokens tar/hit (target side), mis (missile), everything else caster; spell
/// tokens q/q1../w/e/r/p/ba1-3/crit; form tokens (primary/assassin/slayer for Kayn) keep multi-form
/// kits apart. Clip events are every clip that carries authored particle or sound events.
/// </summary>
public static class ChampionEventBuilder
{
    private static readonly string[] FormTokens = { "primary", "assassin", "slayer" };
    private static readonly HashSet<string> SpellTokens = new(StringComparer.OrdinalIgnoreCase)
    { "q", "q1", "q2", "q3", "w", "e", "r", "p", "passive", "ba1", "ba2", "ba3", "crit" };

    /// <summary>Spell → the clip-name prefix the game uses for its cast animation.</summary>
    private static string? ClipPrefixFor(string spell) => spell switch
    {
        "Q" => "spell1", "W" => "spell2", "E" => "spell3", "R" => "spell4",
        "BA1" => "attack1", "BA2" => "attack2", "BA3" => "attack3", "CRIT" => "crit",
        _ => null,   // passives have no cast animation
    };

    public static List<ChampionEvent> Build(
        IReadOnlyDictionary<uint, VfxSystemDefinition> systems,
        IReadOnlyCollection<AnimClipInfo> clips,
        IReadOnlyDictionary<string, IReadOnlyList<uint>>? authoredMissiles = null)
    {
        var events = new List<ChampionEvent>();

        // ---- spell composites from VFX naming ----
        var groups = new Dictionary<(string Form, string Spell), (List<uint> Cas, List<uint> Tar, List<uint> Mis, List<uint> Aim, List<string> Dropped)>();
        var allNames = systems.Values.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (hash, def) in systems)
        {
            if (!def.Emitters.Any(e => e.IsVisual)) continue;
            var tokens = def.Name.Split('_', StringSplitOptions.RemoveEmptyEntries);

            string? spell = null;
            string form = "";
            foreach (var raw in tokens)
            {
                var tk = raw.ToLowerInvariant();
                if (spell is null && SpellTokens.Contains(tk)) spell = tk;
                if (form.Length == 0 && Array.IndexOf(FormTokens, tk) >= 0) form = raw;
            }
            if (spell is null) continue;   // idles/transforms/etc. — covered by clip events

            // Group by ability first; numbered casts are split into separate playback events below.
            string spellKey = spell.ToUpperInvariant() switch
            {
                "Q1" or "Q2" or "Q3" => "Q",
                "PASSIVE" => "P",
                var s => s,
            };
            var key = (form, spellKey);
            if (!groups.TryGetValue(key, out var g)) groups[key] = g = (new List<uint>(), new List<uint>(), new List<uint>(), new List<uint>(), new List<string>());
            if (ChampionSystemTokens.DropReason(def.Name, allNames) is { } why) { g.Dropped.Add(def.Name + " (" + why + ")"); continue; }
            switch (ChampionSystemTokens.Classify(def.Name, spellKey == "P", allNames))
            {
                case ChampionSystemTokens.Role.Target: g.Tar.Add(hash); break;
                case ChampionSystemTokens.Role.Missile: g.Mis.Add(hash); break;
                case ChampionSystemTokens.Role.Aim: g.Aim.Add(hash); break;
                default: g.Cas.Add(hash); break;
            }
        }

        static int SpellRank(string s) => s switch
        { "P" => 0, "Q" => 1, "W" => 2, "E" => 3, "R" => 4, "BA1" => 5, "BA2" => 6, "BA3" => 7, "CRIT" => 8, _ => 9 };

        foreach (var (key, g) in groups
                     .OrderBy(kv => kv.Key.Form, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(kv => SpellRank(kv.Key.Spell)))
        {
            // Numbered casts are separate events, not simultaneous emitters. Require numbered
            // cast systems as evidence before interpreting indicator/trail suffixes as cast numbers.
            var casts = g.Cas.Select(h => CastVariant(systems[h].Name, key.Spell, castOnly: true))
                .Where(n => n > 0).Distinct().OrderBy(n => n).ToArray();
            if (casts.Length < 2) casts = new[] { 0 };
            foreach (int cast in casts)
            {
                string spell = cast > 0 ? key.Spell + cast : key.Spell;
                List<uint> Select(List<uint> hashes) => hashes.Where(h =>
                {
                    string name = systems[h].Name;
                    // The local-player indicator and the ally-view copy are alternatives.
                    if (name.EndsWith("_Ally", StringComparison.OrdinalIgnoreCase)
                        && systems.Values.Any(s => s.Name.Equals(name[..^5], StringComparison.OrdinalIgnoreCase))) return false;
                    int n = CastVariant(name, key.Spell, castOnly: false);
                    return cast == 0 || n == 0 || n == cast;
                }).ToList();
                // M875: authored missiles that resolve to no system of this skin must not hide the name-matched ones
                var ownMissiles = authoredMissiles is not null && authoredMissiles.TryGetValue(key.Spell, out var own)
                    ? own.Where(systems.ContainsKey).Distinct().ToList() : null;
                var missiles = ownMissiles is { Count: > 0 } ? ownMissiles : Select(g.Mis);
                events.Add(new ChampionEvent
                {
                    Name = key.Form.Length > 0 ? $"{spell} ({key.Form})" : spell,
                    ClipAnmFile = FindClip(clips, ClipPrefixFor(key.Spell), key.Form, spell),
                    CasterSystems = Select(g.Cas).Where(h => !missiles.Contains(h)).ToList(),
                    TargetSystems = Select(g.Tar),
                    AimSystems = Select(g.Aim).Where(h => !missiles.Contains(h)).ToList(),
                    DroppedSystemNames = g.Dropped.Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                    MissileSystems = missiles,
                    ReturnMissileSystems = g.Mis.Where(h => !missiles.Contains(h)
                        && missiles.Any(m => systems[h].Name.Equals(systems[m].Name + "_return", StringComparison.OrdinalIgnoreCase))).ToList(),
                });
            }
        }

        // ---- authored animation sequences (recalls, emotes, transforms — clips with their own events) ----
        foreach (var c in clips
                     .Where(c => c.ParticleEvents is { Count: > 0 } || c.SoundEvents is { Count: > 0 })
                     .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            var file = System.IO.Path.GetFileName(c.AnmPath.Replace('\\', '/'));
            if (file.Length == 0) continue;
            // hash-named clips read better by their anm file
            string display = c.Name.StartsWith("0x", StringComparison.Ordinal)
                ? System.IO.Path.GetFileNameWithoutExtension(file)
                : c.Name;
            events.Add(new ChampionEvent { Name = display, ClipAnmFile = file });
        }
        return events;
    }

    /// <summary>The cast clip for a spell: prefer the form-specific variant (Spell4_Air_Slayer for
    /// Slayer), else the form-free one, shortest name winning inside each bucket.</summary>
    private static int CastVariant(string name, string spell, bool castOnly)
    {
        var tokens = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        foreach (string token in tokens)
        {
            if (token.StartsWith(spell, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(token[spell.Length..], out int explicitCast) && explicitCast is > 0 and <= 9)
                return explicitCast;
            if (token.StartsWith("cas", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(token[3..], out int cast) && cast is > 0 and <= 9) return cast;
        }
        if (!castOnly)
            for (int i = 0; i + 1 < tokens.Length; i++)
                if ((tokens[i].Equals("indicator", StringComparison.OrdinalIgnoreCase)
                     || tokens[i].Equals("trail", StringComparison.OrdinalIgnoreCase))
                    && int.TryParse(tokens[i + 1], out int cast) && cast is > 0 and <= 9) return cast;
        return 0;
    }

    private static string? FindClip(IReadOnlyCollection<AnimClipInfo> clips, string? prefix, string form, string spell)
    {
        if (prefix is null) return null;
        var candidates = clips.Where(c => c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0 && spell.Length > 0 && "QWER".Contains(spell[0]))
            candidates = clips.Where(c =>
            {
                string n = Characters.CharacterActions.SlotClipName(c, spell[0]);
                return spell.Length > 1
                    ? n.Equals(spell, StringComparison.OrdinalIgnoreCase) || n.StartsWith(spell + "_", StringComparison.OrdinalIgnoreCase)
                    : Characters.CharacterActions.IsSlotLetterClip(n, spell[0]);
            }).OrderBy(c => Characters.CharacterActions.SlotClipName(c, spell[0]).Contains('_') ? 1 : 0)
              .ThenBy(c => c.AnmPath.Contains("_ult_", StringComparison.OrdinalIgnoreCase) ? 1 : 0).ToList();
        if (candidates.Count == 0) return null;

        bool HasForm(AnimClipInfo c) => form.Length > 0 && c.Name.Contains(form, StringComparison.OrdinalIgnoreCase);
        bool HasAnyForm(AnimClipInfo c) => FormTokens.Any(f => c.Name.Contains(f, StringComparison.OrdinalIgnoreCase));

        var best = candidates.Where(HasForm).OrderBy(c => c.Name.Length).FirstOrDefault()
                   ?? candidates.Where(c => !HasAnyForm(c)).OrderBy(c => Characters.CharacterActions.SlotClipName(c, spell[0]).Length).FirstOrDefault()
                   ?? candidates.First();
        var file = System.IO.Path.GetFileName(best.AnmPath.Replace('\\', '/'));
        return file.Length > 0 ? file : null;
    }
}
