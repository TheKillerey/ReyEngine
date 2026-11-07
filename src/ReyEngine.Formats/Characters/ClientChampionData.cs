using System.Text.Json;
using System.Text.RegularExpressions;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;

namespace ReyEngine.Formats.Characters;

/// <summary>One champion in the client's list: <c>v1/champion-summary.json</c> plus the square portrait's WAD path.</summary>
public sealed record ClientChampionSummary(int Id, string Alias, string Name, string Title, IReadOnlyList<string> Roles, string IconPath);

/// <summary>One ability of a client champion document. <see cref="IconPath"/> is a WAD path, and only when that entry exists.
/// <c>VideoPath</c> / <c>VideoImagePath</c> are the client JSON's <c>abilityVideoPath</c> / <c>abilityVideoImagePath</c> verbatim
/// ("champion-abilities/0266/ability_0266_Q1.webm"): they are not in any WAD (the client streams them), so nothing checks them.</summary>
public sealed record ClientAbility(string Key, string? Name, string? IconPath, string Description, string? VideoPath = null, string? VideoImagePath = null);

/// <summary>A chroma of a skin: <c>Number</c> is the skin number its bin carries (id - championId * 1000), <c>Colors</c> the client's
/// swatch colours ("#27211C"), <c>ImagePath</c> the WAD path of its picture.</summary>
public sealed record ClientChroma(int Id, int Number, string Name, IReadOnlyList<string> Colors, string? ImagePath);

/// <summary>A skin with the art the client keeps for it. Every path is a WAD path, null when the client WAD does not hold the entry.</summary>
public sealed record ClientSkinDetail(int Id, int Number, string Name, bool IsBase, string? Rarity, string? TilePath,
    string? LoadScreenPath, string? SplashPath, IReadOnlyList<ClientChroma> Chromas);

/// <summary>Everything <c>v1/champions/&lt;id&gt;.json</c> says that the Character window shows.</summary>
public sealed record ClientChampionDetails(int Id, string Alias, string Name, string Title, IReadOnlyList<string> Roles,
    ClientAbility? Passive, IReadOnlyList<ClientAbility> Spells, IReadOnlyList<ClientSkinDetail> Skins)
{
    /// <summary>The ability of a slot key: "P" is the passive, "Q".."R" the spells. Null for anything else.</summary>
    public ClientAbility? Ability(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        if (key.Equals("P", StringComparison.OrdinalIgnoreCase)) return Passive;
        return Spells.FirstOrDefault(s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    public ClientSkinDetail? Skin(int number) => Skins.FirstOrDefault(s => s.Number == number);
}

/// <summary>
/// M870: the League client's own champion metadata and art, read from the <c>rcp-be-lol-game-data</c> plugin WADs
/// (<c>default-assets2.wad</c>, then <c>default-assets.wad</c>).
///
/// <para>Enrichment and never a dependency, like <see cref="ClientNameCatalog"/>: a missing install, a missing WAD, or a client
/// update that moves a file leaves every answer empty or null and nothing throws.</para>
///
/// <para><b>The WADs are held open only while they are being read.</b> <see cref="WadArchive"/> opens a file with
/// <c>FileShare.Read</c> (measured: while one is open another handle cannot write, delete or rename it), and the League client patches
/// these files, so a handle kept for the whole session could block a client update. The archives are opened on the first read and
/// closed <see cref="IdleCloseMilliseconds"/> after the last one; the parsed summary and per-champion details stay in memory, and the
/// image service keeps decoded bitmaps, so a reopen is rare. A failed open is not remembered: it is retried after
/// <see cref="RetryMilliseconds"/>. Every read is serialised under one lock, so worker threads and the UI thread can share one instance.</para>
///
/// <para>Paths: the client's JSON names assets as <c>/lol-game-data/assets/ASSETS/Characters/Kalista/...</c>; the WAD path is that
/// lower-cased under <c>plugins/rcp-be-lol-game-data/global/default/</c> (see <see cref="ToWadPath"/>).</para>
/// </summary>
public sealed class ClientChampionData : IDisposable
{
    public const string WadRoot = "plugins/rcp-be-lol-game-data/global/default/";

    private readonly string[] _wadPaths;
    private readonly object _gate = new();
    private List<WadArchive>? _archives;
    private bool _disposed;
    private IReadOnlyList<ClientChampionSummary>? _champions;
    private long _championsRetryAt;
    private long _lastUse;
    private long _openRetryAt;
    private int _opens;
    private Timer? _idleTimer;

    /// <summary>How long after the last read the WADs are closed. Settable for tests.</summary>
    public int IdleCloseMilliseconds { get; set; } = 3000;

    /// <summary>How long a failed open (or an empty summary) is left alone before the next try.</summary>
    public const int RetryMilliseconds = 5000;

    /// <summary>True while the WAD files are open.</summary>
    public bool IsOpen { get { lock (_gate) return _archives is not null; } }

    /// <summary>How many times the WADs have been opened. For tests.</summary>
    public int OpenCount { get { lock (_gate) return _opens; } }
    private readonly Dictionary<int, ClientChampionDetails?> _details = new();

    private ClientChampionData(string[] wadPaths) => _wadPaths = wadPaths;

    /// <summary>Nothing available: every question answers empty.</summary>
    public static ClientChampionData Unavailable() => new(Array.Empty<string>());

    /// <summary>The data of the plugin whose WAD is <paramref name="dataWadPath"/> (what <see cref="ClientNameCatalog.FindDataWad"/> returns);
    /// the sibling WAD of the pair is searched too. Nothing is opened yet. Null or a missing file gives <see cref="Unavailable"/>.</summary>
    public static ClientChampionData Open(string? dataWadPath)
    {
        if (string.IsNullOrWhiteSpace(dataWadPath) || !File.Exists(dataWadPath)) return Unavailable();
        string dir = Path.GetDirectoryName(dataWadPath) ?? "";
        var paths = new List<string> { dataWadPath };
        foreach (string name in new[] { "default-assets2.wad", "default-assets.wad" })
        {
            string candidate = Path.Combine(dir, name);
            if (File.Exists(candidate) && !paths.Contains(candidate, StringComparer.OrdinalIgnoreCase)) paths.Add(candidate);
        }
        return new ClientChampionData(paths.ToArray());
    }

    /// <summary>The data of a League install root (the folder holding Game/ and Plugins/).</summary>
    public static ClientChampionData ForInstall(string? installRoot) =>
        Open(string.IsNullOrWhiteSpace(installRoot) ? null : ClientNameCatalog.FindDataWad(installRoot));

    /// <summary>True when a client data WAD file was found. Says nothing about whether it holds what is asked for.</summary>
    public bool IsAvailable => _wadPaths.Length > 0;

    // ---- paths -------------------------------------------------------------------------------------

    /// <summary>The WAD path of a client asset path: <c>/lol-game-data/assets/ASSETS/Characters/Kalista/x.png</c> becomes
    /// <c>plugins/rcp-be-lol-game-data/global/default/assets/characters/kalista/x.png</c>; <c>/lol-game-data/assets/v1/...</c> becomes
    /// <c>.../global/default/v1/...</c>. A path already under the plugin root passes through lower-cased. Empty in, empty out.</summary>
    public static string ToWadPath(string? clientPath)
    {
        if (string.IsNullOrWhiteSpace(clientPath)) return "";
        const string Prefix = "/lol-game-data/assets/";
        string p = clientPath.Trim().Replace('\\', '/');
        if (p.StartsWith(WadRoot, StringComparison.OrdinalIgnoreCase)) return p.ToLowerInvariant();
        string rel = p.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? p[Prefix.Length..] : p.TrimStart('/');
        return (WadRoot + rel).ToLowerInvariant();
    }

    /// <summary>The WAD path of a champion's square portrait. Not checked against the WAD - see <see cref="Exists"/>.</summary>
    public static string IconPathOf(int championId) => $"{WadRoot}v1/champion-icons/{championId}.png";

    // ---- the archives ------------------------------------------------------------------------------

    // caller holds _gate. Null when nothing could be opened (and then not again until RetryMilliseconds have passed).
    private List<WadArchive>? OpenLocked()
    {
        if (_disposed || _wadPaths.Length == 0) return null;
        if (_archives is null)
        {
            if (System.Environment.TickCount64 < _openRetryAt) return null;
            var list = new List<WadArchive>();
            foreach (string path in _wadPaths)
            {
                try { list.Add(WadArchive.Open(path)); }
                catch { /* an unreadable WAD costs its own entries, not the other one's */ }
            }
            if (list.Count == 0) { _openRetryAt = System.Environment.TickCount64 + RetryMilliseconds; return null; }
            _archives = list;
            _opens++;
        }
        _lastUse = System.Environment.TickCount64;
        _idleTimer ??= new Timer(_ => CloseIfIdle(), null, Timeout.Infinite, Timeout.Infinite);
        _idleTimer.Change(Math.Max(50, IdleCloseMilliseconds), Timeout.Infinite);
        return _archives;
    }

    private void CloseIfIdle()
    {
        lock (_gate)
        {
            if (_archives is null || _disposed) return;
            long idle = System.Environment.TickCount64 - _lastUse;
            if (idle < IdleCloseMilliseconds)
            {
                _idleTimer?.Change(Math.Max(50, IdleCloseMilliseconds - idle), Timeout.Infinite);
                return;
            }
            CloseLocked();
        }
    }

    private void CloseLocked()
    {
        foreach (var a in _archives ?? new List<WadArchive>()) { try { a.Dispose(); } catch { } }
        _archives = null;
    }

    /// <summary>Close the WAD files now (they reopen on the next read).</summary>
    public void Release()
    {
        lock (_gate) CloseLocked();
    }

    /// <summary>True when the client WAD holds this asset (a WAD path or a client path).</summary>
    public bool Exists(string? path)
    {
        string wad = ToWadPath(path);
        if (wad.Length == 0) return false;
        ulong hash = HashAlgorithms.WadPath(wad);
        lock (_gate)
        {
            var archives = OpenLocked();
            return archives is not null && archives.Any(a => a.TryGetEntry(hash, out _));
        }
    }

    /// <summary>The bytes of a client asset (a WAD path or a client path), or null when it is not there. Never throws.</summary>
    public byte[]? ReadAsset(string? path) => Read(path, out _);

    /// <summary><paramref name="reachable"/> is false when the WADs could not be opened at all (so a null is "try again later",
    /// not "the client does not have it").</summary>
    private byte[]? Read(string? path, out bool reachable)
    {
        reachable = false;
        string wad = ToWadPath(path);
        if (wad.Length == 0) return null;
        ulong hash = HashAlgorithms.WadPath(wad);
        lock (_gate)
        {
            var archives = OpenLocked();
            if (archives is null) return null;
            reachable = true;
            try
            {
                foreach (var archive in archives)
                    if (archive.TryGetEntry(hash, out _)) return archive.Extract(hash);
            }
            catch { /* a corrupt chunk is "not there" */ }
            return null;
        }
    }

    private string? PathIfPresent(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        string wad = ToWadPath(value.GetString());
        return wad.Length > 0 && Exists(wad) ? wad : null;
    }

    // ---- the champion list -------------------------------------------------------------------------

    /// <summary>Every champion the client lists, sorted by display name. Empty when the client is not there (and asked for again after
    /// <see cref="RetryMilliseconds"/>, not remembered as empty). Read once; <see cref="PrefetchAsync"/> does it on a worker.</summary>
    public IReadOnlyList<ClientChampionSummary> Champions
    {
        get
        {
            lock (_gate)
            {
                if (_champions is not null) return _champions;
                if (System.Environment.TickCount64 < _championsRetryAt) return Array.Empty<ClientChampionSummary>();
            }
            var list = new List<ClientChampionSummary>();
            try
            {
                byte[]? json = ReadAsset(WadRoot + "v1/champion-summary.json");
                if (json is not null)
                {
                    using var document = JsonDocument.Parse(json);
                    foreach (var e in document.RootElement.EnumerateArray())
                    {
                        int id = Int(e, "id");
                        string alias = Str(e, "alias");
                        if (id <= 0 || alias.Length == 0) continue;
                        list.Add(new ClientChampionSummary(id, alias, Str(e, "name"), Str(e, "description"), Roles(e), IconPathOf(id)));
                    }
                }
            }
            catch { /* a malformed document keeps what was read */ }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            lock (_gate)
            {
                if (_champions is not null) return _champions;
                if (list.Count == 0) { _championsRetryAt = System.Environment.TickCount64 + RetryMilliseconds; return list; }
                return _champions = list;
            }
        }
    }

    /// <summary>Open the WAD and parse the champion list on a worker thread, so the first question from the UI finds it done.</summary>
    public Task PrefetchAsync() => IsAvailable ? Task.Run(() => { _ = Champions; }) : Task.CompletedTask;

    /// <summary><see cref="GetDetails"/> on a worker thread.</summary>
    public Task<ClientChampionDetails?> GetDetailsAsync(int championId) => Task.Run(() => GetDetails(championId));

    public ClientChampionSummary? Champion(string alias) =>
        Champions.FirstOrDefault(c => c.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase));

    public ClientChampionSummary? Champion(int id) => Champions.FirstOrDefault(c => c.Id == id);

    // ---- one champion ------------------------------------------------------------------------------

    /// <summary>The champion's abilities, skins and chromas, or null when the client has no document for this id. Read once per id (a
    /// document the WAD does not have is remembered; one that could not be read because the WADs would not open is not).</summary>
    public ClientChampionDetails? GetDetails(int championId)
    {
        if (championId <= 0) return null;
        lock (_gate)
        {
            if (_details.TryGetValue(championId, out var cached)) return cached;
        }
        ClientChampionDetails? details = null;
        bool reachable = false;
        try
        {
            byte[]? json = Read($"{WadRoot}v1/champions/{championId}.json", out reachable);
            if (json is not null) details = Parse(championId, json);
        }
        catch { /* unreadable: no details, the plain list stays */ }
        if (!reachable && details is null) return null;
        // two threads that missed together both parsed; the first to finish is the one everybody gets
        lock (_gate)
        {
            if (_details.TryGetValue(championId, out var winner)) return winner;
            _details[championId] = details;
        }
        return details;
    }

    /// <summary>Parses one champion document. Public so a test can feed it text; icon paths are only kept when the WADs hold them.</summary>
    public ClientChampionDetails Parse(int championId, byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        ClientAbility? passive = root.TryGetProperty("passive", out var p) && p.ValueKind == JsonValueKind.Object
            ? Ability("P", p) : null;

        var spells = new List<ClientAbility>();
        if (root.TryGetProperty("spells", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var s in list.EnumerateArray())
                if (Str(s, "spellKey").ToUpperInvariant() is { Length: > 0 } key)
                    spells.Add(Ability(key, s));

        var skins = new List<ClientSkinDetail>();
        if (root.TryGetProperty("skins", out var skinList) && skinList.ValueKind == JsonValueKind.Array)
            foreach (var s in skinList.EnumerateArray())
            {
                int id = Int(s, "id");
                if (id <= 0 || id / 1000 != championId) continue;
                var chromas = new List<ClientChroma>();
                if (s.TryGetProperty("chromas", out var chromaList) && chromaList.ValueKind == JsonValueKind.Array)
                    foreach (var c in chromaList.EnumerateArray())
                    {
                        int cid = Int(c, "id");
                        if (cid <= 0 || cid / 1000 != championId) continue;
                        var colors = new List<string>();
                        if (c.TryGetProperty("colors", out var colorList) && colorList.ValueKind == JsonValueKind.Array)
                            foreach (var color in colorList.EnumerateArray())
                                if (color.ValueKind == JsonValueKind.String && color.GetString() is { Length: > 0 } hex) colors.Add(hex);
                        chromas.Add(new ClientChroma(cid, cid % 1000, Str(c, "name"), colors,
                            PathIfPresent(c, "chromaPath") ?? PathIfPresent(c, "tilePath")));
                    }
                skins.Add(new ClientSkinDetail(id, id % 1000, Str(s, "name"), Bool(s, "isBase"), RarityLabel(Str(s, "rarity")),
                    PathIfPresent(s, "tilePath"), PathIfPresent(s, "loadScreenPath"), PathIfPresent(s, "splashPath"), chromas));
            }
        skins.Sort((a, b) => a.Number.CompareTo(b.Number));

        return new ClientChampionDetails(championId, Str(root, "alias"), Str(root, "name"), Str(root, "title"), Roles(root),
            passive, spells, skins);
    }

    private ClientAbility Ability(string key, JsonElement e)
    {
        string description = Str(e, "description");
        if (description.Length == 0) description = Str(e, "dynamicDescription");
        return new ClientAbility(key, Str(e, "name") is { Length: > 0 } n ? n : null, PathIfPresent(e, "abilityIconPath"),
            StripMarkup(description), RawPath(e, "abilityVideoPath"), RawPath(e, "abilityVideoImagePath"));
    }

    private static string? RawPath(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } t ? t.Trim() : null;

    // ---- text --------------------------------------------------------------------------------------

    private static readonly Regex LineBreaks = new(@"<\s*(br|/p|/li|/ul)\s*/?\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Tags = new(@"<[^>]*>", RegexOptions.Compiled);
    private static readonly Regex Placeholders = new(@"@[^@\s]+@", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"[ \t ]+", RegexOptions.Compiled);
    private static readonly Regex BlankLines = new(@"\n\s*\n\s*(\n\s*)+", RegexOptions.Compiled);

    /// <summary>Client description text as plain text: <c>&lt;br&gt;</c> becomes a line break, every other tag
    /// (<c>&lt;magicDamage&gt;</c>, <c>&lt;spellName&gt;</c>...) is dropped, entities are decoded and <c>@Placeholder@</c>
    /// values the client fills in at run time become an ellipsis.</summary>
    public static string StripMarkup(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string s = LineBreaks.Replace(text, "\n");
        s = Tags.Replace(s, "");
        s = Placeholders.Replace(s, "...");
        s = s.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#39;", "'")
             .Replace("&apos;", "'").Replace("&nbsp;", " ");
        s = Spaces.Replace(s, " ");
        s = string.Join("\n", s.Split('\n').Select(l => l.Trim()));
        s = BlankLines.Replace(s, "\n\n");
        return s.Trim();
    }

    /// <summary>"kEpic" becomes "Epic"; the client's "kNoRarity" (and anything empty) becomes null.</summary>
    public static string? RarityLabel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = raw.Trim();
        if (s.Length > 1 && s[0] == 'k' && char.IsUpper(s[1])) s = s[1..];
        return s.Equals("NoRarity", StringComparison.OrdinalIgnoreCase) || s.Equals("None", StringComparison.OrdinalIgnoreCase) ? null : s;
    }

    // ---- json helpers ------------------------------------------------------------------------------

    private static IReadOnlyList<string> Roles(JsonElement element)
    {
        var roles = new List<string>();
        if (element.TryGetProperty("roles", out var r) && r.ValueKind == JsonValueKind.Array)
            foreach (var role in r.EnumerateArray())
                if (role.ValueKind == JsonValueKind.String && role.GetString() is { Length: > 0 } text) roles.Add(text);
        return roles;
    }

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result)
            ? result : 0;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            CloseLocked();
            _idleTimer?.Dispose();
            _idleTimer = null;
        }
    }
}
