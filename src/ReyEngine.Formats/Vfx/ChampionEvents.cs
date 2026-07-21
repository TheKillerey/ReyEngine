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
    public bool NeedsTarget => TargetSystems.Count > 0 || MissileSystems.Count > 0;
    public bool HasSystems => CasterSystems.Count > 0 || NeedsTarget;

    public string Description
    {
        get
        {
            var parts = new List<string>();
            if (CasterSystems.Count > 0) parts.Add($"{CasterSystems.Count} caster");
            if (MissileSystems.Count > 0) parts.Add($"{MissileSystems.Count} missile");
            if (TargetSystems.Count > 0) parts.Add($"{TargetSystems.Count} target");
            if (ClipAnmFile is not null) parts.Add(System.IO.Path.GetFileNameWithoutExtension(ClipAnmFile));
            return parts.Count > 0 ? string.Join(" · ", parts) : "";
        }
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
        IReadOnlyCollection<AnimClipInfo> clips)
    {
        var events = new List<ChampionEvent>();

        // ---- spell composites from VFX naming ----
        var groups = new Dictionary<(string Form, string Spell), (List<uint> Cas, List<uint> Tar, List<uint> Mis)>();
        foreach (var (hash, def) in systems)
        {
            if (!def.Emitters.Any(e => e.IsVisual)) continue;
            var tokens = def.Name.Split('_', StringSplitOptions.RemoveEmptyEntries);

            string? spell = null;
            string form = "";
            bool isTarget = false, isMissile = false;
            foreach (var raw in tokens)
            {
                var tk = raw.ToLowerInvariant();
                if (spell is null && SpellTokens.Contains(tk)) spell = tk;
                if (form.Length == 0 && Array.IndexOf(FormTokens, tk) >= 0) form = raw;
                if (tk is "tar" or "hit") isTarget = true;
                if (tk == "mis") isMissile = true;
            }
            if (spell is null) continue;   // idles/transforms/etc. — covered by clip events

            // Q1/Q2… collapse into one spell; passive normalizes to P.
            string spellKey = spell.ToUpperInvariant() switch
            {
                "Q1" or "Q2" or "Q3" => "Q",
                "PASSIVE" => "P",
                var s => s,
            };
            var key = (form, spellKey);
            if (!groups.TryGetValue(key, out var g)) groups[key] = g = (new List<uint>(), new List<uint>(), new List<uint>());
            (isTarget ? g.Tar : isMissile ? g.Mis : g.Cas).Add(hash);
        }

        static int SpellRank(string s) => s switch
        { "P" => 0, "Q" => 1, "W" => 2, "E" => 3, "R" => 4, "BA1" => 5, "BA2" => 6, "BA3" => 7, "CRIT" => 8, _ => 9 };

        foreach (var (key, g) in groups
                     .OrderBy(kv => kv.Key.Form, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(kv => SpellRank(kv.Key.Spell)))
        {
            string? clip = FindClip(clips, ClipPrefixFor(key.Spell), key.Form);
            events.Add(new ChampionEvent
            {
                Name = key.Form.Length > 0 ? $"{key.Spell} ({key.Form})" : key.Spell,
                ClipAnmFile = clip,
                CasterSystems = g.Cas,
                TargetSystems = g.Tar,
                MissileSystems = g.Mis,
            });
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
    private static string? FindClip(IReadOnlyCollection<AnimClipInfo> clips, string? prefix, string form)
    {
        if (prefix is null) return null;
        var candidates = clips.Where(c => c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0) return null;

        bool HasForm(AnimClipInfo c) => form.Length > 0 && c.Name.Contains(form, StringComparison.OrdinalIgnoreCase);
        bool HasAnyForm(AnimClipInfo c) => FormTokens.Any(f => c.Name.Contains(f, StringComparison.OrdinalIgnoreCase));

        var best = candidates.Where(HasForm).OrderBy(c => c.Name.Length).FirstOrDefault()
                   ?? candidates.Where(c => !HasAnyForm(c)).OrderBy(c => c.Name.Length).FirstOrDefault()
                   ?? candidates.OrderBy(c => c.Name.Length).First();
        var file = System.IO.Path.GetFileName(best.AnmPath.Replace('\\', '/'));
        return file.Length > 0 ? file : null;
    }
}
