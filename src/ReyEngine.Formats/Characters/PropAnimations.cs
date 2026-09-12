using ReyEngine.Formats.Skeletons;
using ReyEngine.Formats.Vfx;

using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Characters;

/// <summary>
/// M679: which clips a placed prop plays, and which of them is its idle.
///
/// <para>From the skin's OWN animation graph - the bins its skin bin depends on under <c>/animations/</c> -
/// not from the .anm files that happen to sit in the character's folder. The two are not the same
/// population, measured on Map453: SmallGolem's skin uses the Golem's mesh, skeleton and graph, whose
/// idles live under <c>characters/golem/animations/</c>, while <c>characters/smallgolem/</c> holds a stale
/// <c>golem_idle1.anm</c> for another rig; YoungLizard's graph points its idles at the Lizard's files and its
/// own folder ships only two attacks. A folder scan played the wrong rig on one and no idle on the other.
/// Exactly the rule the character window has used since M86.</para>
/// </summary>
public static class PropAnimations
{
    const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

    /// <summary>Every clip the skin's graphs name, in graph order, de-duplicated by name and file.</summary>
    public static IReadOnlyList<AnimClipInfo> ResolveClips(byte[] skinBin, Func<string, byte[]?> readByPath,
        Func<uint, string?> resolveBinName, Func<ulong, string?>? resolveWadPath)
    {
        var clips = new List<AnimClipInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> deps;
        try { deps = VfxSystemResolver.ExtractDependencies(skinBin); }
        catch { return clips; }

        foreach (string dep in deps)
        {
            if (!dep.Contains("/animations/", OIC)) continue;
            byte[]? graph;
            try { graph = readByPath(dep); } catch { graph = null; }
            if (graph is null) continue;
            IReadOnlyList<AnimClipInfo> parsed;
            try { parsed = ChampionAnimationData.ParseClips(graph, resolveBinName, resolveWadPath); }
            catch { continue; }
            foreach (var c in parsed)
            {
                if (string.IsNullOrEmpty(c.AnmPath)) continue;
                if (seen.Add(DisplayName(c) + "|" + c.AnmPath)) clips.Add(c);
            }
        }
        return clips;
    }

    /// <summary>
    /// M697: the clip NAMES the skin's graph declares - what a placement's <c>IdleAnimationName</c> has
    /// to match, which is not the same list as <see cref="ResolveClips"/>.
    ///
    /// <para>That list is for PLAYING a clip, so it drops every entry without an .anm behind it. The
    /// Golem's graph is exactly why the two must not be confused: its "Idle1" is a SelectorClipData that
    /// picks between two bored idles, so it has no file of its own and never appears there, while the
    /// entries that do appear are hash-named atomics whose display name is their file name. Writing one
    /// of those into a placement names a clip the game cannot find, and the prop stands still.</para>
    ///
    /// <para>Only names the hash database resolves are returned: a clip the placement cannot spell is a
    /// clip it cannot ask for.</para>
    /// </summary>
    public static IReadOnlyList<string> GraphClipNames(byte[] skinBin, Func<string, byte[]?> readByPath,
        Func<uint, string?> resolveBinName)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> deps;
        try { deps = VfxSystemResolver.ExtractDependencies(skinBin); }
        catch { return names; }

        uint clipDataMap = HashAlgorithms.Fnv1a("mClipDataMap");
        foreach (string dep in deps)
        {
            if (!dep.Contains("/animations/", OIC)) continue;
            byte[]? graph;
            try { graph = readByPath(dep); } catch { graph = null; }
            if (graph is null) continue;
            try
            {
                foreach (var o in SafeBinTree.Parse(graph).Objects.Values)
                {
                    if (!o.Properties.TryGetValue(clipDataMap, out var mapProp) || mapProp is not System.Collections.IEnumerable entries) continue;
                    foreach (var kv in entries)
                    {
                        var key = kv.GetType().GetProperty("Key")?.GetValue(kv);
                        uint hash = key switch { BinTreeHash h => h.Value, BinTreeU32 u => u.Value, _ => 0u };
                        if (hash == 0) continue;
                        if (resolveBinName(hash) is { Length: > 0 } name && seen.Add(name)) names.Add(name);
                    }
                }
            }
            catch { /* an unreadable graph contributes nothing */ }
        }
        return names;
    }

    /// <summary>A clip table from loose .anm files, for a skin whose graph is missing - the pre-M679 view,
    /// kept as the fallback. Name = file name.</summary>
    public static IReadOnlyList<AnimClipInfo> FromFiles(IEnumerable<string> anmPaths) =>
        anmPaths.Where(p => !string.IsNullOrEmpty(p))
            .Select(p => new AnimClipInfo(Path.GetFileNameWithoutExtension(p.Replace('\\', '/')), p,
                Array.Empty<string>(), Array.Empty<string>(), Array.Empty<uint>(), Array.Empty<uint>()))
            .ToList();

    /// <summary>What a clip is called in a list: its graph name, or its file name when the graph name did
    /// not resolve (the legacy mobs' idle names are hashes the database does not know).</summary>
    public static string DisplayName(AnimClipInfo c) =>
        c.Name is { Length: > 0 } n && !n.StartsWith("0x", OIC) ? n : FileName(c);

    public static string FileName(AnimClipInfo c) => Path.GetFileNameWithoutExtension(c.AnmPath.Replace('\\', '/'));

    /// <summary>The clip a prop plays when nothing is chosen: the base idle by name or file (Idle1,
    /// Idle_Base, Idle01), else a bored idle, else any idle; ties by name. Null when the skin has none.</summary>
    public static AnimClipInfo? PickIdle(IReadOnlyList<AnimClipInfo> clips) =>
        clips.Select(c => (Clip: c, Score: IdleScore(c)))
            .Where(x => x.Score > 0f)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => DisplayName(x.Clip), StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Clip)
            .FirstOrDefault();

    /// <summary>The clip a list entry stands for - by display name, then by file name.</summary>
    public static AnimClipInfo? Find(IReadOnlyList<AnimClipInfo> clips, string displayName) =>
        clips.FirstOrDefault(c => DisplayName(c).Equals(displayName, OIC))
        ?? clips.FirstOrDefault(c => FileName(c).Equals(displayName, OIC));

    private static float IdleScore(AnimClipInfo c)
    {
        float best = 0f;
        foreach (string n in new[] { c.Name, FileName(c) })
        {
            if (string.IsNullOrEmpty(n) || n.StartsWith("0x", OIC)) continue;
            float s = n.Contains("idle1", OIC) || n.Contains("idle_base", OIC) || n.Contains("idle01", OIC) ? 3f
                    : n.Contains("idle_bored", OIC) ? 2.5f
                    : n.Contains("idle", OIC) ? 2f
                    : 0f;
            if (s > best) best = s;
        }
        return best;
    }
}
