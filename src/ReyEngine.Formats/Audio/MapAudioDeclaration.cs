using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Audio;

/// <summary>One <c>BankUnit</c> a map declares: the bank files it loads and the events it expects from them.</summary>
public sealed record MapBankUnit(string Name, IReadOnlyList<string> BankPaths, IReadOnlyList<string> Events);

/// <summary>
/// M575: which Wwise banks a map actually loads.
///
/// <para>A bank only exists for a map if its <c>MapAudioDataProperties.bankUnits</c> names it, in
/// <c>data/maps/shipping/map&lt;N&gt;/map&lt;N&gt;.bin</c> — dropping a .bnk into the WAD does nothing on its own.
/// Reading the declaration is what lets a port extend a bank the map already has rather than ship, and
/// then have to maintain, a modified copy of the whole map bin.</para>
/// </summary>
public static class MapAudioDeclaration
{
    private static readonly uint AudioDataClass = HashAlgorithms.Fnv1a("MapAudioDataProperties");
    private static readonly uint F_bankUnits = HashAlgorithms.Fnv1a("bankUnits");
    private static readonly uint F_bankPath = HashAlgorithms.Fnv1a("bankPath");
    private static readonly uint F_name = HashAlgorithms.Fnv1a("name");
    private static readonly uint F_events = HashAlgorithms.Fnv1a("events");

    /// <summary>Every bank unit the bin declares. Empty when the bin has no audio block or cannot be read.</summary>
    public static IReadOnlyList<MapBankUnit> Read(byte[] mapBin)
    {
        var units = new List<MapBankUnit>();
        BinTree tree;
        try { tree = new BinTree(new MemoryStream(mapBin, false)); }
        catch { return units; }

        foreach (var obj in tree.Objects.Values)
        {
            if (obj.ClassHash != AudioDataClass) continue;
            if (!obj.Properties.TryGetValue(F_bankUnits, out var prop)) continue;
            foreach (var element in Elements(prop))
            {
                if (element is not BinTreeStruct unit) continue;
                string name = (unit.Properties.GetValueOrDefault(F_name) as BinTreeString)?.Value ?? "";
                var paths = Strings(unit.Properties.GetValueOrDefault(F_bankPath));
                var events = Strings(unit.Properties.GetValueOrDefault(F_events));
                if (paths.Count > 0) units.Add(new MapBankUnit(name, paths, events));
            }
        }
        return units;
    }

    /// <summary>Banks shared by every map. Extending one of these would put a map's ambience into
    /// whatever else the mod is loaded alongside, so they lose to a map-specific bank however big they are.</summary>
    private static bool IsShared(string bankPath)
    {
        string stem = System.IO.Path.GetFileNameWithoutExtension(bankPath);
        return stem.Contains("global", StringComparison.OrdinalIgnoreCase)
            || stem.Contains("gameplay", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("init", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The best pair to extend: an <c>*_events.bnk</c> and <c>*_audio.bnk</c> that share a stem, both
    /// declared by the map. Map-specific banks come first, then the ones already holding the most media -
    /// that is the map's main SFX bank rather than an empty stub (several shipped <c>_audio.bnk</c> files
    /// are 32 bytes and have no DATA section to extend at all).
    /// </summary>
    /// <param name="mediaCount">Number of embedded media entries in a bank path, or -1 when unreadable.</param>
    public static IEnumerable<(string Events, string Audio, int Media)> HostCandidates(
        IReadOnlyList<MapBankUnit> units, Func<string, int> mediaCount)
    {
        var all = units.SelectMany(u => u.BankPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var pairs = new List<(string Events, string Audio, int Media)>();
        foreach (string eventsPath in all)
        {
            if (!eventsPath.EndsWith("_events.bnk", StringComparison.OrdinalIgnoreCase)) continue;
            string stem = eventsPath[..^"_events.bnk".Length];
            string audioPath = all.FirstOrDefault(p =>
                p.Equals(stem + "_audio.bnk", StringComparison.OrdinalIgnoreCase));
            if (audioPath is null) continue;
            int media = mediaCount(audioPath);
            if (media <= 0) continue;   // a stub bank has no DATA section to extend
            pairs.Add((eventsPath, audioPath, media));
        }
        return pairs.OrderBy(p => IsShared(p.Audio) ? 1 : 0).ThenByDescending(p => p.Media);
    }

    private static IEnumerable<BinTreeProperty> Elements(BinTreeProperty p) => p switch
    {
        BinTreeUnorderedContainer u => u.Elements,
        BinTreeContainer c => c.Elements,
        _ => Array.Empty<BinTreeProperty>(),
    };

    private static IReadOnlyList<string> Strings(BinTreeProperty? p) =>
        p is null ? Array.Empty<string>()
                  : Elements(p).OfType<BinTreeString>().Select(s => s.Value).ToList();
}
