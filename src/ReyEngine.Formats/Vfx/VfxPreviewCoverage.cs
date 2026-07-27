using System.Reflection;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Vfx;

/// <summary>
/// M191 (3.7): which emitter/system fields the preview actually consumes, so the Particle Editor can say
/// when a field it lets you edit will not change what you see. Editing something that silently does
/// nothing is worse than not being able to edit it - it reads as a bug in the renderer.
///
/// M192 corrected three defects that made this class assert the opposite of the truth. They are recorded
/// here rather than quietly fixed, because each one is a way this design can go wrong again:
///
///   1. "The resolver reads it" is NOT "the preview renders it". <see cref="VfxSystemResolver"/> is shared
///      with the MAP path, so a field can be read, parked in the model, and consumed only by
///      <c>MapPlaceableExtractor</c> - which the particle preview never runs. Four such fields shipped
///      unbadged. They are listed in <see cref="NotRendered"/> now.
///   2. The lookup ran only for top-level fields; sub-rows inherited their parent's answer verbatim. A
///      field inside a struct the resolver DOES read was therefore never asked about, and 657,785 measured
///      occurrences across 46 nested (owner, field) pairs claimed to be visible - the six Linger
///      <c>Use*</c> flags among them, which <see cref="VfxSystemResolver"/> states in capitals that it
///      deliberately does not read. The caller now asks per row.
///   3. It failed OPEN. The old comment claimed "no set means no badges, never a wrong badge", which had
///      the safe direction backwards: under this badge's semantics the ABSENCE of a badge is the claim
///      that an edit is visible, so failing open asserted that all 3,989,530 unread occurrences render.
///      It now fails CLOSED - unknown means badged.
/// </summary>
public static class VfxPreviewCoverage
{
    /// <summary>Every hash <see cref="VfxSystemResolver"/> knows about, read off the resolver itself by
    /// reflection rather than copied into a second list. A duplicated list would rot the first time a
    /// milestone taught the resolver a new field, and it would rot SILENTLY.
    ///
    /// Both <c>static readonly</c> and <c>const</c> are collected: <c>F_spawnShape</c> is a const, and
    /// taking only readonly fields marked SpawnShape (632,283 rows, 51.8% of emitters) as ignored when the
    /// resolver reads it.
    ///
    /// The set also contains 12 class hashes. Measured: none of them collides with any of the 398 field
    /// hashes in the corpus census, so they currently cost nothing - but that is a property of this corpus,
    /// not a guarantee, which is why <see cref="NotRendered"/> exists as the explicit override.</summary>
    private static readonly HashSet<uint> ParsedHashes = Collect();

    /// <summary>True when reflection produced a usable set. False forces every row to be badged.</summary>
    public static bool CoverageAvailable => ParsedHashes.Count > 0;

    private static HashSet<uint> Collect()
    {
        try
        {
            return typeof(VfxSystemResolver)
                .GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.FieldType == typeof(uint) && (f.IsInitOnly || f.IsLiteral))
                .Select(f => (uint)f.GetValue(null)!)
                .ToHashSet();
        }
        catch
        {
            // FAIL CLOSED. An empty set makes IsParsed false for everything, so every row is badged
            // "not shown in the preview". That is noisy and obviously wrong to a user, which is the point:
            // the opposite failure - silently promising that every edit is visible - looks correct and is not.
            return new HashSet<uint>();
        }
    }

    /// <summary>Does the resolver read this field at all? Note this is NOT the same question as "does the
    /// preview render it" - see <see cref="NotRendered"/>.</summary>
    public static bool IsParsed(uint fieldHash) => ParsedHashes.Contains(fieldHash);

    /// <summary>Fields the resolver DOES read but that never reach anything the particle preview draws.
    /// Hand-maintained: proving "parsed but unrendered" mechanically needs real dataflow analysis, and an
    /// attempt at it produced obvious false positives, so this list is limited to cases traced by hand from
    /// the parse site to every consumer in <c>src/</c>, and under-claims rather than guesses.</summary>
    private static readonly Dictionary<uint, string> NotRendered = new()
    {
        [HashAlgorithms.Fnv1a("particleLingerType")] =
            "Parsed but unused. M185 measured it as largely independent of the Linger struct - 19,406 Linger "
            + "structs carry no type and 11,347 type values sit on emitters with no Linger struct - so nothing "
            + "establishes that it selects a behaviour.",

        // M192: read by the shared resolver, consumed ONLY by the MAP placement path
        // (MapPlaceableExtractor.cs:149,150,157). The particle preview has no audio and no visibility
        // culling, so these were claiming to be visible in a viewport that cannot show them.
        [HashAlgorithms.Fnv1a("soundOnCreateDefault")] =
            "Parsed, but only the map placement path uses it (to start ambient sound on a placed system). "
            + "The particle preview has no audio, so nothing here can be heard or seen in the viewport.",
        [HashAlgorithms.Fnv1a("soundPersistentDefault")] =
            "Parsed, but only the map placement path uses it (to start looping ambient sound on a placed "
            + "system). The particle preview has no audio, so nothing here can be heard or seen in the viewport.",
        [HashAlgorithms.Fnv1a("visibilityRadius")] =
            "Parsed, but only the map placement path uses it, as the audible/visible radius of a placed "
            + "system. The preview draws one system at the origin and never culls by distance.",
        [HashAlgorithms.Fnv1a("emitterLinger")] =
            "Parsed into the model and then read by nothing at all - M192 traced it from "
            + "VfxSystemResolver to VfxSystemDefinition and found no consumer anywhere in the codebase. "
            + "The per-particle linger window is what drives the shutdown curves; this emitter-level value "
            + "is not wired up.",
    };

    /// <summary>A note for the editor when this field will not affect the preview, or null when it will.
    /// Callers must ask this for EVERY row, including rows nested inside a definition struct - a field
    /// inside a struct the resolver reads can still be one the resolver skips.</summary>
    public static string? IgnoredNote(uint fieldHash)
    {
        if (NotRendered.TryGetValue(fieldHash, out var why)) return why;
        // M193 (4.1): fields parked in VfxEmitterExtras. They are declared in their own table rather than
        // as resolver constants precisely so this lookup cannot be forgotten - parking a field badges it.
        if (VfxParkedEmitterFields.Hashes.Contains(fieldHash) || VfxParkedSystemFields.Hashes.Contains(fieldHash))
            return "ReyEngine parses this field into its model, but no renderer stage consumes it yet. The "
                 + "edit is saved to the .bin and the game will use it; the viewport will not change.";
        if (!CoverageAvailable)
            return "The editor could not determine which fields the preview reads, so it is flagging all of "
                 + "them. Edits are still saved to the .bin normally.";
        if (!IsParsed(fieldHash))
            return "The preview does not read this field. The edit is saved to the .bin and the game will use "
                 + "it - the viewport just will not show the difference.";
        return null;
    }
}
