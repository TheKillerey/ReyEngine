using System.Text.Json;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;

namespace ReyEngine.Formats.Characters;

/// <summary>A champion as the client names it: "Twisted Fate", whose folder is "TwistedFate".</summary>
public sealed record ClientChampion(int Id, string Alias, string DisplayName, string Title, IReadOnlyList<string> Roles);

/// <summary>A skin as the client names it: "Spirit Blossom Ahri", not "AhriSkin70".</summary>
public sealed record ClientSkin(int Id, int Number, string DisplayName, bool IsBase, bool IsLegacy, string? TilePath);

/// <summary>
/// M609: the marketing names, from the League client's own data.
///
/// <para>The game files do not have them. A skin bin calls itself <c>AhriSkin04</c> and its champion
/// <c>Ahri</c>; nowhere under <c>Game/DATA/FINAL</c> is the string "K/DA Ahri" — measured across the
/// champion WADs, their locale WADs and Global.wad. What does have them is the client plugin
/// <c>rcp-be-lol-game-data</c>, which ships <c>champion-summary.json</c> and <c>skins.json</c> inside
/// <c>default-assets2.wad</c>.</para>
///
/// <para>Treated as ENRICHMENT and never as a dependency: a Game-only install, or a future client that
/// moves these files, leaves the catalogue working with the code names it read from the bins. Failing to
/// pretty a name is not a reason to fail to list a skin.</para>
/// </summary>
public sealed class ClientNameCatalog
{
    private readonly Dictionary<string, ClientChampion> _byAlias = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, ClientSkin> _skinsById = new();

    public int ChampionCount => _byAlias.Count;
    public int SkinCount => _skinsById.Count;

    /// <summary>The client data WAD for a League install root (the folder holding Game/ and Plugins/),
    /// or null when this install does not carry one.</summary>
    public static string? FindDataWad(string installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot)) return null;
        // Riot has moved the split between these two more than once; take whichever holds the files.
        foreach (string name in new[] { "default-assets2.wad", "default-assets.wad" })
        {
            string candidate = Path.Combine(installRoot, "Plugins", "rcp-be-lol-game-data", name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>The install root above a <c>Game/DATA/FINAL/Champions</c> folder.</summary>
    public static string? InstallRootFromChampions(string championsDirectory)
    {
        var dir = new DirectoryInfo(championsDirectory);
        for (int i = 0; i < 4 && dir is not null; i++) dir = dir.Parent;   // Champions/FINAL/DATA/Game -> root
        return dir?.FullName;
    }

    /// <summary>Load from a client data WAD. Returns an empty catalogue rather than throwing — every
    /// caller of this is drawing a list it can draw without names.</summary>
    public static ClientNameCatalog Load(string? dataWadPath)
    {
        var catalog = new ClientNameCatalog();
        if (string.IsNullOrWhiteSpace(dataWadPath) || !File.Exists(dataWadPath)) return catalog;

        try
        {
            using var archive = WadArchive.Open(dataWadPath);
            catalog.ReadChampions(Json(archive, "champion-summary.json"));
            catalog.ReadSkins(Json(archive, "skins.json"));
        }
        catch
        {
            // A client update that changes the layout must not stop the character list from appearing.
        }
        return catalog;
    }

    public ClientChampion? Champion(string alias) =>
        _byAlias.TryGetValue(alias, out var champion) ? champion : null;

    /// <summary>The skin the client calls number <paramref name="number"/> of this champion. Null when
    /// either the champion or that number is unknown — chromas and mode-only skins numbered in the 300s
    /// are often absent.</summary>
    public ClientSkin? Skin(string alias, int number) =>
        _byAlias.TryGetValue(alias, out var champion)
        && _skinsById.TryGetValue(champion.Id * 1000 + number, out var skin)
            ? skin
            : null;

    // ---- reading ----------------------------------------------------------------------------------

    private static byte[]? Json(WadArchive archive, string fileName)
    {
        // Addressed by hash so no name dictionary is needed: these paths are fixed and known.
        string path = "plugins/rcp-be-lol-game-data/global/default/v1/" + fileName;
        if (archive.TryGetEntry(HashAlgorithms.WadPath(path), out _))
            return archive.Extract(HashAlgorithms.WadPath(path));

        var match = archive.Entries.FirstOrDefault(e =>
            e.IsResolved && e.Path.EndsWith("/v1/" + fileName, StringComparison.OrdinalIgnoreCase));
        return match is null ? null : archive.Extract(match.PathHash);
    }

    private void ReadChampions(byte[]? json)
    {
        if (json is null) return;
        using var document = JsonDocument.Parse(json);
        foreach (var element in document.RootElement.EnumerateArray())
        {
            int id = Int(element, "id");
            string alias = Str(element, "alias");
            if (id <= 0 || alias.Length == 0) continue;      // id -1 is the "None" placeholder

            var roles = new List<string>();
            if (element.TryGetProperty("roles", out var rolesElement) && rolesElement.ValueKind == JsonValueKind.Array)
                foreach (var role in rolesElement.EnumerateArray())
                    if (role.ValueKind == JsonValueKind.String) roles.Add(role.GetString()!);

            _byAlias[alias] = new ClientChampion(id, alias, Str(element, "name"), Str(element, "description"), roles);
        }
    }

    private void ReadSkins(byte[]? json)
    {
        if (json is null) return;
        using var document = JsonDocument.Parse(json);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var element = property.Value;
            int id = Int(element, "id");
            if (id <= 0) continue;
            _skinsById[id] = new ClientSkin(
                id,
                id % 1000,
                Str(element, "name"),
                Bool(element, "isBase"),
                Bool(element, "isLegacy"),
                Str(element, "tilePath") is { Length: > 0 } tile ? tile : null);
        }
    }

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int result)
            ? result
            : 0;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
