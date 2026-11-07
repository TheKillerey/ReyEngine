namespace ReyEngine.Formats.Characters.Simulation;

public enum SimStatus { Complete, Incomplete, Blocked }

/// <param name="Codes">Stable short codes for what failed (empty when passed), for counting reasons across a roster.</param>
public sealed record ValidationCheck(string Name, bool Passed, string Detail, IReadOnlyList<string>? Codes = null);

public sealed record AbilityValidation(
    string Key,
    SimStatus Status,
    IReadOnlyList<ValidationCheck> Checks,
    IReadOnlyList<string> Reasons);

public sealed record ChampionValidation(
    string Champion,
    SimStatus Status,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<AbilityValidation> Abilities);

/// <summary>
/// M867: asset-level checks over a <see cref="ChampionSimDefinition"/>. It answers "does the data name everything an
/// ability needs, and does every name resolve?" - not "does it look right when it plays". That needs the player (M869) and
/// the in-app validation (M871), so a champion is never better than <see cref="SimStatus.Incomplete"/> here.
///
/// <para>Per-ability status: <b>Complete</b> when every applicable check passed; <b>Incomplete</b> when something is
/// missing but the ability still shows something (a clip or a visible effect); <b>Blocked</b> when it has nothing visible at
/// all, so there is nothing to simulate.</para>
/// </summary>
public static class ChampionSimValidator
{
    public const string Pending = "asset checks passed; runtime and visual validation pending M869/M871";

    public static ChampionValidation Validate(ChampionSimDefinition definition)
    {
        var abilities = definition.Abilities
            .Select(a => Validate(a, definition.Abilities.Where(s => s.ParentKey == a.Key).ToList())).ToList();
        var reasons = new List<string>();

        SimStatus status;
        if (abilities.Count == 0)
        {
            status = SimStatus.Blocked;
            reasons.Add("the definition has no abilities");
        }
        else if (abilities.All(a => a.Status == SimStatus.Blocked))
        {
            status = SimStatus.Blocked;
            reasons.Add("no ability has a clip or a visible effect");
        }
        else
        {
            // M867: never Complete - nothing has played yet.
            status = SimStatus.Incomplete;
            int gaps = abilities.Count(a => a.Status != SimStatus.Complete);
            if (gaps == 0) reasons.Add(Pending);
            else reasons.Add($"{gaps} of {abilities.Count} abilities have asset-level gaps; runtime and visual validation pending M869/M871");
        }
        return new ChampionValidation(definition.Champion, status, reasons, abilities);
    }

    /// <param name="subCasts">The definition's entries that belong to this one ("Q1".."Q3" for "Q"). An ability that is only a
    /// wrapper for sub-casts (Aatrox's Q authors no clip and owns no effect) is judged through them.</param>
    public static AbilityValidation Validate(AbilitySim a, IReadOnlyList<AbilitySim>? subCasts = null)
    {
        subCasts ??= Array.Empty<AbilitySim>();
        var checks = new List<ValidationCheck>();
        bool passive = a.Key == "P";
        bool subCast = a.ParentKey is not null;

        // 1. the clip
        bool clipOk; string clipDetail;
        if (a.Clip is not null) { clipOk = true; clipDetail = $"clip {a.Clip.Name} ({a.Clip.Source})"; }
        else if (a.AuthoredClipName is not null) { clipOk = false; clipDetail = $"the spell names clip '{a.AuthoredClipName}', which is not in the base animation graph"; }
        else if (passive) { clipOk = true; clipDetail = "a passive has no cast clip"; }
        else if (subCasts.Count > 0) { clipOk = true; clipDetail = $"the spell authors no clip; its {subCasts.Count} sub-cast(s) carry them"; }
        else if (!subCast) { clipOk = true; clipDetail = "the spell authors no clip (clip-less by design)"; }
        else { clipOk = false; clipDetail = "the sub-cast has no clip file"; }
        checks.Add(new ValidationCheck("clip", clipOk, clipDetail,
            clipOk ? null : new[] { a.AuthoredClipName is not null ? "clip.unresolved" : "clip.subCastMissing" }));

        // 2. missiles: each needs a resolvable visual system and a motion
        if (a.Missiles.Count == 0) checks.Add(new ValidationCheck("missiles", true, "no missiles"));
        else
        {
            var bad = new List<string>(); var codes = new List<string>();
            void Bad(string code, string text) { bad.Add(text); if (!codes.Contains(code)) codes.Add(code); }
            foreach (var m in a.Missiles)
            {
                string who = m.SpellName ?? m.Effect?.Name ?? "missile";
                if (m.Effect is null) Bad("missile.noEffectKey", $"{who}: no effect key");
                else if (m.Effect.Name is null) Bad("missile.effectUnresolved", $"{who}: effect {m.Effect.KeyHash ?? m.Effect.ObjectHash} does not resolve to a system");
                else if (m.Effect.Visual != true) Bad("missile.effectInvisible", $"{who}: system {m.Effect.Name} has no visual emitter");
                if (m.MotionKind == "Unknown") Bad("missile.noMotion", $"{who}: no speed or flight time");
            }
            checks.Add(new ValidationCheck("missiles", bad.Count == 0,
                bad.Count == 0 ? $"{a.Missiles.Count} missile(s) resolve and move" : string.Join("; ", bad), codes));
        }

        // 3. caster / target / return systems
        var systems = a.CasterSystems.Concat(a.TargetSystems).Concat(a.ReturnSystems).Concat(a.AimSystems ?? Array.Empty<SimSystemRef>()).ToList();
        if (systems.Count == 0) checks.Add(new ValidationCheck("systems", true, "no caster, target or return systems"));
        else
        {
            var badSystems = systems.Where(s => s.Name is null || s.Visual != true).ToList();
            var bad = badSystems.Select(s => s.Name is null ? $"{s.KeyHash ?? s.ObjectHash} unresolved" : $"{s.Name} has no visual emitter").ToList();
            var codes = new List<string>();
            if (badSystems.Any(s => s.Name is null)) codes.Add("system.unresolved");
            if (badSystems.Any(s => s.Name is not null)) codes.Add("system.invisible");
            checks.Add(new ValidationCheck("systems", bad.Count == 0,
                bad.Count == 0 ? $"{systems.Count} system(s) resolve with visual emitters" : string.Join("; ", bad), codes));
        }

        // 4. cast timing
        if (passive) checks.Add(new ValidationCheck("castTime", true, "a passive is not cast"));
        else checks.Add(new ValidationCheck("castTime", a.CastSeconds is > 0f,
            a.CastSeconds is > 0f ? a.CastSeconds.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " s" : "no authored cast frame or cast time",
            a.CastSeconds is > 0f ? null : new[] { "castTime.missing" }));

        // 5. something to see
        bool visible = a.Clip is not null
            || a.CasterSystems.Any(s => s.Visual == true) || a.TargetSystems.Any(s => s.Visual == true)
            || a.ReturnSystems.Any(s => s.Visual == true) || (a.AimSystems?.Any(s => s.Visual == true) ?? false)
            || a.Missiles.Any(m => m.Effect?.Visual == true)
            || a.ClipParticleEvents.Any(e => e.System?.Visual == true);
        if (!visible && subCasts.Any(sc => Validate(sc).Status != SimStatus.Blocked)) visible = true;
        // A passive with no clip and no systems of its own is normal (many champions' passives are stat rules or buffs the game
        // draws from elsewhere): it is a gap in the data, not a broken ability.
        bool ownVisualMissing = !visible && passive;
        checks.Add(new ValidationCheck("visible", visible,
            visible ? "a clip or a visible effect exists"
            : passive ? "no visual of its own in the data" : "no clip and no visible effect",
            visible ? null : new[] { passive ? "visible.passiveNone" : "visible.none" }));

        var reasons = checks.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Detail}").ToList();
        SimStatus status = !visible && !ownVisualMissing ? SimStatus.Blocked : reasons.Count == 0 ? SimStatus.Complete : SimStatus.Incomplete;
        return new AbilityValidation(a.Key, status, checks, reasons);
    }
}
