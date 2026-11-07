using System.Text.Json;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Animation;
using ReyEngine.Formats.Skeletons;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.Formats.Characters.Simulation;

/// <summary>
/// M867: what the generator needs of one champion, read once from its WAD: the record, the base skin's VFX closure
/// (systems + effect-key map) and the base skin's animation graph.
///
/// <para>The closure walk is the one <c>MainWindowViewModel.TryLoadChampionVfxWithResources</c> does for the character
/// window: start at <c>skins/skin0.bin</c> and follow every linked bin that exists in the champion's WAD, merging systems
/// and resource maps first-wins. Dependencies that live in another WAD are not followed - the same limit the window has.</para>
///
/// <para>Clips come from the base skin's OWN graph (the first <c>/animations/</c> link of skin0.bin). The window also
/// merges every other skin's graph behind it, by clip name and first-wins; that adds names the base graph lacks and can
/// never change a name the base graph has, so it is left out here - a clip missing from the base graph is reported as
/// missing rather than borrowed from another skin.</para>
/// </summary>
public sealed class ChampionSimInputs
{
    public required string Champion { get; init; }
    public byte[]? RecordBin { get; init; }
    public required string BaseSkinBin { get; init; }
    public string? AnimationGraph { get; init; }
    public IReadOnlyDictionary<uint, VfxSystemDefinition> Systems { get; init; } = new Dictionary<uint, VfxSystemDefinition>();
    public IReadOnlyDictionary<uint, uint> ResourceMap { get; init; } = new Dictionary<uint, uint>();
    public IReadOnlyList<AnimClipInfo> Clips { get; init; } = Array.Empty<AnimClipInfo>();
    /// <summary>The .anm header of a clip path: frame rate and duration, or null when it cannot be read. Cached by the caller.</summary>
    public Func<string, (float Fps, float Duration)?> ReadClipTiming { get; init; } = _ => null;
    public IReadOnlyList<string> Issues { get; init; } = Array.Empty<string>();
}

public static class ChampionSimSource
{
    public static ChampionSimInputs Load(WadArchive wad, string champion,
        Func<uint, string?> resolveBinName, Func<ulong, string?> resolveWadPath)
    {
        string lower = champion.ToLowerInvariant();
        string skinBin = $"data/characters/{lower}/skins/skin0.bin";
        var issues = new List<string>();

        byte[]? record = null;
        ulong recordHash = HashAlgorithms.WadPath(ChampionRecord.PathFor(champion));
        if (wad.TryGetEntry(recordHash, out _)) record = wad.Extract(recordHash);
        else issues.Add($"champion record {ChampionRecord.PathFor(champion)} is not in the WAD");

        var systems = new Dictionary<uint, VfxSystemDefinition>();
        var resMap = new Dictionary<uint, uint>();
        string? graphPath = null;
        IReadOnlyList<AnimClipInfo> clips = Array.Empty<AnimClipInfo>();

        ulong skinHash = HashAlgorithms.WadPath(skinBin);
        if (!wad.TryGetEntry(skinHash, out _))
        {
            issues.Add($"base skin bin {skinBin} is not in the WAD");
        }
        else
        {
            var visited = new HashSet<ulong> { skinHash };
            var queue = new Queue<ulong>();
            queue.Enqueue(skinHash);
            int guard = 0;
            bool first = true;
            while (queue.Count > 0 && guard++ < 64)
            {
                byte[] bytes;
                try { bytes = wad.Extract(queue.Dequeue()); }
                catch (Exception ex) { issues.Add($"closure bin unreadable: {ex.Message}"); continue; }

                foreach (var (k, v) in VfxSystemResolver.ExtractAll(bytes)) systems.TryAdd(k, v);
                foreach (var (k, v) in VfxSystemResolver.ExtractResourceMap(bytes)) resMap.TryAdd(k, v);
                var deps = VfxSystemResolver.ExtractDependencies(bytes);
                if (first)
                {
                    first = false;
                    graphPath = deps.FirstOrDefault(d => d.Contains("/animations/", StringComparison.OrdinalIgnoreCase));
                }
                foreach (var dep in deps)
                {
                    ulong h = HashAlgorithms.WadPath(dep);
                    if (visited.Add(h) && wad.TryGetEntry(h, out _)) queue.Enqueue(h);
                }
            }

            if (graphPath is null) issues.Add("skin0.bin links no animation graph");
            else if (!wad.TryGetEntry(HashAlgorithms.WadPath(graphPath), out _))
                issues.Add($"animation graph {graphPath} is not in the WAD");
            else
                clips = ChampionAnimationData.ParseClips(wad.Extract(HashAlgorithms.WadPath(graphPath)),
                    resolveBinName, resolveWadPath);
        }

        var timing = new Dictionary<string, (float, float)?>(StringComparer.OrdinalIgnoreCase);
        (float, float)? ReadTiming(string anmPath)
        {
            if (timing.TryGetValue(anmPath, out var cached)) return cached;
            (float, float)? result = null;
            try
            {
                ulong h = HashAlgorithms.WadPath(anmPath.Replace('\\', '/'));
                if (wad.TryGetEntry(h, out _))
                {
                    var clip = AnimationDecoder.Decode(wad.Extract(h), Path.GetFileName(anmPath));
                    result = (clip.Fps, clip.Duration);
                }
            }
            catch { /* an unreadable clip is reported as "assumed30fps" by the generator */ }
            timing[anmPath] = result;
            return result;
        }

        return new ChampionSimInputs
        {
            Champion = champion,
            RecordBin = record,
            BaseSkinBin = skinBin,
            AnimationGraph = graphPath,
            Systems = systems,
            ResourceMap = resMap,
            Clips = clips,
            ReadClipTiming = p => ReadTiming(p),
            Issues = issues,
        };
    }

    /// <summary>The part of the install's <c>content-metadata.json</c> <c>version</c> before the "+" (16.22.8348910), or "unknown".</summary>
    /// <param name="gameDirectory">The <c>Game</c> folder, or anything inside <c>Game/DATA/FINAL</c> - the file is searched upward.</param>
    public static string ReadGameVersion(string gameDirectory)
    {
        try
        {
            var dir = new DirectoryInfo(gameDirectory);
            for (int i = 0; i < 5 && dir is not null; i++, dir = dir.Parent)
            {
                string file = Path.Combine(dir.FullName, "content-metadata.json");
                if (!File.Exists(file)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
                if (doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                    && v.GetString() is { Length: > 0 } text)
                {
                    int plus = text.IndexOf('+');
                    return plus > 0 ? text[..plus] : text;
                }
            }
        }
        catch { /* unreadable metadata is "unknown", not a failure */ }
        return "unknown";
    }
}

/// <summary>One ability's display data from the client: name and the icon's WAD path (only when the icon exists in the client WAD).</summary>
public sealed record ClientAbilityInfo(string? Name, string? IconPath);

public sealed record ClientChampionAbilities(ClientAbilityInfo? Passive, IReadOnlyDictionary<string, ClientAbilityInfo> Spells);

/// <summary>
/// M867: optional enrichment - ability names and icon paths from the League client's own champion documents
/// (<c>v1/champions/&lt;id&gt;.json</c> inside <c>default-assets2.wad</c>). Never required: a missing client leaves
/// the names null. The icon path stored is the WAD path of the icon, and only when that entry exists in the client WAD
/// - a path is never derived and trusted without checking.
/// </summary>
public sealed class ClientAbilityCatalog : IDisposable
{
    private readonly WadArchive _archive;
    private ClientAbilityCatalog(WadArchive archive) => _archive = archive;

    public static ClientAbilityCatalog? Open(string? dataWadPath)
    {
        if (string.IsNullOrWhiteSpace(dataWadPath) || !File.Exists(dataWadPath)) return null;
        try { return new ClientAbilityCatalog(WadArchive.Open(dataWadPath)); }
        catch { return null; }
    }

    public ClientChampionAbilities? Get(int championId)
    {
        try
        {
            ulong hash = HashAlgorithms.WadPath($"plugins/rcp-be-lol-game-data/global/default/v1/champions/{championId}.json");
            if (!_archive.TryGetEntry(hash, out _)) return null;
            using var doc = JsonDocument.Parse(_archive.Extract(hash));
            var root = doc.RootElement;

            ClientAbilityInfo? passive = null;
            if (root.TryGetProperty("passive", out var p) && p.ValueKind == JsonValueKind.Object) passive = Info(p);

            var spells = new SortedDictionary<string, ClientAbilityInfo>(StringComparer.Ordinal);
            if (root.TryGetProperty("spells", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var s in list.EnumerateArray())
                    if (s.TryGetProperty("spellKey", out var k) && k.ValueKind == JsonValueKind.String && k.GetString() is { Length: > 0 } key)
                        spells[key.ToUpperInvariant()] = Info(s);
            return new ClientChampionAbilities(passive, spells);
        }
        catch { return null; }
    }

    private ClientAbilityInfo Info(JsonElement e)
    {
        string? name = e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } t ? t : null;
        string? icon = null;
        if (e.TryGetProperty("abilityIconPath", out var ip) && ip.ValueKind == JsonValueKind.String && ip.GetString() is { Length: > 0 } raw)
        {
            string wadPath = ClientChampionData.ToWadPath(raw);     // M870: the one client-path mapping
            if (_archive.TryGetEntry(HashAlgorithms.WadPath(wadPath), out _)) icon = wadPath;
        }
        return new ClientAbilityInfo(name, icon);
    }

    public void Dispose() => _archive.Dispose();
}
