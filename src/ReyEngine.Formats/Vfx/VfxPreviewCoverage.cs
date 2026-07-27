using System.Reflection;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Vfx;

/// <summary>
/// M191 (3.7): which emitter/system fields the preview actually consumes, so the Particle Editor can say
/// when a field it lets you edit will not change what you see. Editing something that silently does
/// nothing is worse than not being able to edit it - it reads as a bug in the renderer.
/// </summary>
public static class VfxPreviewCoverage
{
    /// <summary>Every hash <see cref="VfxSystemResolver"/> knows about, read off the resolver itself by
    /// reflection rather than copied into a second list. A duplicated list would rot the first time a
    /// milestone taught the resolver a new field, and it would rot SILENTLY, in the direction of telling
    /// the user that something works when it does not.
    ///
    /// Both <c>static readonly</c> and <c>const</c> are collected: <c>F_spawnShape</c> is a const, and
    /// taking only readonly fields marked SpawnShape (632,283 rows, 51.8% of emitters) as ignored when the
    /// resolver reads it.
    ///
    /// The set also contains class hashes and a few non-field constants. That is harmless: it can only ever
    /// cause a field to be treated as READ, so the failure direction is a missing badge, never a false one.</summary>
    private static readonly HashSet<uint> ReadHashes = Collect();

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
            return new HashSet<uint>();   // fail open: no set means no badges, never a wrong badge
        }
    }

    /// <summary>False when nothing in the preview pipeline so much as reads this field.</summary>
    public static bool IsRead(uint fieldHash) => ReadHashes.Count == 0 || ReadHashes.Contains(fieldHash);

    /// <summary>Fields the resolver DOES read but the renderer then does nothing with. This one is
    /// hand-maintained - proving "parsed but unused" mechanically needs real dataflow analysis, and a wrong
    /// answer here is exactly the false badge this class exists to avoid - so it is deliberately limited to
    /// cases verified in the source, and under-claims rather than guesses.</summary>
    private static readonly Dictionary<uint, string> ParsedButUnused = new()
    {
        [HashAlgorithms.Fnv1a("particleLingerType")] =
            "Parsed but unused. M185 measured it as largely independent of the Linger struct - 19,406 Linger "
            + "structs carry no type and 11,347 type values sit on emitters with no Linger struct - so nothing "
            + "establishes that it selects a behaviour.",
        [HashAlgorithms.Fnv1a("disableBackfaceCull")] =
            "Parsed but not applied. Two probes gave contradictory answers about the winding of Riot's mesh "
            + "primitives, and applying the flag on the wrong winding removes the geometry entirely.",
    };

    /// <summary>A note for the editor when this field will not affect the preview, or null when it will.</summary>
    public static string? IgnoredNote(uint fieldHash)
    {
        if (ParsedButUnused.TryGetValue(fieldHash, out var why)) return why;
        if (!IsRead(fieldHash))
            return "The preview does not read this field. The edit is saved to the .bin and the game will use "
                 + "it - the viewport just will not show the difference.";
        return null;
    }
}
