using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ReyEngine.Formats.Audio;

namespace ReyEngine.App.Services;

/// <summary>
/// M138: recover Wwise EVENT names from their ids.
///
/// Wwise object ids are FNV-1 32-bit of the (lowercased) name, so they can't be inverted — but they
/// can be looked up: League's own .bin files carry the event names as plain strings (sound placements,
/// animation/VFX sound events). Harvesting those strings, hashing each, and keeping the ones that match
/// an id recovers about half of a map's events outright, plus more via the Play_/Stop_ sibling trick.
///
/// NOTE: media (wem) ids are NOT name hashes — Wwise assigns them per project, so there is nothing to
/// reverse. A wem gets its name indirectly, from the event(s) that play it.
/// </summary>
public sealed class WwiseNameIndex
{
    private readonly Dictionary<uint, string> _names = new();

    public int Count => _names.Count;
    public bool TryGet(uint id, out string name) => _names.TryGetValue(id, out name!);
    public string Label(uint id) => _names.TryGetValue(id, out var n) ? n : $"event 0x{id:x8}";

    /// <summary>Verb prefixes Wwise event names use — a known "Play_X" implies "Stop_X" and friends.</summary>
    private static readonly string[] Verbs =
        { "Play_", "Stop_", "Pause_", "Resume_", "Mute_", "Unmute_", "Set_", "Trigger_" };

    /// <summary>Add every string that hashes to one of <paramref name="wanted"/>.</summary>
    public void Harvest(IEnumerable<string> candidates, IReadOnlySet<uint> wanted)
    {
        foreach (var s in candidates)
        {
            if (s.Length is < 4 or > 160) continue;
            if (s.Contains('/') || s.Contains('\\')) continue;   // asset paths, not event names
            uint h = WwiseHash.Fnv1(s);
            if (wanted.Contains(h)) _names.TryAdd(h, s);
        }
    }

    /// <summary>Derive siblings of known names (Play_Foo → Stop_Foo, …) and keep the ones that match.</summary>
    public int ExpandVerbs(IReadOnlySet<uint> wanted)
    {
        int added = 0;
        foreach (var known in _names.Values.ToList())
        {
            var verb = Verbs.FirstOrDefault(v => known.StartsWith(v, StringComparison.OrdinalIgnoreCase));
            if (verb is null) continue;
            var stem = known[verb.Length..];
            foreach (var v in Verbs)
            {
                if (string.Equals(v, verb, StringComparison.OrdinalIgnoreCase)) continue;
                var candidate = v + stem;
                uint h = WwiseHash.Fnv1(candidate);
                if (wanted.Contains(h) && _names.TryAdd(h, candidate)) added++;
            }
        }
        return added;
    }

    // ---- disk cache (rebuilding means re-reading thousands of bins) ----

    public static string CacheFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ReyEngine", "wwise_names.json");

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            var map = _names.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(map));
        }
        catch { /* cache is an optimisation, never fatal */ }
    }

    public static WwiseNameIndex Load()
    {
        var idx = new WwiseNameIndex();
        try
        {
            if (!File.Exists(CacheFile)) return idx;
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CacheFile));
            if (map is null) return idx;
            foreach (var (k, v) in map) if (uint.TryParse(k, out var id)) idx._names[id] = v;
        }
        catch { }
        return idx;
    }

    public void Merge(WwiseNameIndex other)
    {
        foreach (var (k, v) in other._names) _names.TryAdd(k, v);
    }
}
