using System.Globalization;
using System.Text.Json;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;

namespace ReyEngine.Formats.Shaders;

/// <summary>One sampler a shader declares (<c>ShaderTexture</c>): the sampler name a material must
/// bind, plus the texture the shader falls back to when nothing is bound.</summary>
public sealed record ShaderTextureDef(string Name, string DefaultTexturePath);

/// <summary>One shader parameter (<c>ShaderPhysicalParameter</c>) and its default value. League packs
/// every parameter into a vector4 regardless of how many components the shader actually reads.</summary>
public sealed record ShaderParamDef(string Name, float X, float Y, float Z, float W)
{
    /// <summary>Invariant on purpose — this text is pasted straight into a material parameter, and a
    /// German locale would render "0,1" and never parse back.</summary>
    public string DefaultText =>
        string.Join(", ", new[] { X, Y, Z, W }.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)));
}

/// <summary>
/// M103: one <c>CustomShaderDef</c> from the client's shader bin — the authoritative answer to
/// "which samplers, parameters and feature switches does this shader support?". Materials that bind
/// anything outside these lists are binding something the shader will simply ignore.
/// </summary>
public sealed record LeagueShaderDef(
    string Name,
    string Category,
    List<ShaderTextureDef> Textures,
    List<ShaderParamDef> Parameters,
    List<string> StaticSwitches)
{
    /// <summary>Name without the <c>Shaders/Category/</c> prefix (what the dropdown shows).</summary>
    public string ShortName { get { int i = Name.LastIndexOf('/'); return i < 0 ? Name : Name[(i + 1)..]; } }
    public string Summary => $"{Textures.Count} sampler(s) · {Parameters.Count} param(s) · {StaticSwitches.Count} switch(es)";
}

/// <summary>Every shader one game install ships, as scanned from its <c>data/shaders/shaders.bin</c>.</summary>
public sealed class ShaderCatalog
{
    /// <summary>"Live", "PBE", or whatever the caller labelled the install.</summary>
    public string Environment { get; init; } = "";
    /// <summary>The game directory this was scanned from (so a stale cache can be spotted).</summary>
    public string GameDirectory { get; init; } = "";
    public List<LeagueShaderDef> Shaders { get; init; } = new();

    public IEnumerable<string> Categories =>
        Shaders.Select(s => s.Category).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.OrdinalIgnoreCase);

    public LeagueShaderDef? Find(string? name) => string.IsNullOrWhiteSpace(name)
        ? null
        : Shaders.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Reads the shader catalogue out of a game install. The definitions all live in a single bin
/// (<c>data/shaders/shaders.bin</c>, inside Global.wad) — map and champion WADs carry none, so this
/// one file is the complete list for that client. Never throws: an unreadable install yields null.
/// </summary>
public static class ShaderCatalogLoader
{
    /// <summary>WAD path of the client's shader definition bin.</summary>
    public const string ShaderBinPath = "data/shaders/shaders.bin";

    /// <param name="resolver">WAD path resolver (finds the bin inside Global.wad).</param>
    /// <param name="resolveBinName">bin-name resolver (FNV-1a object/field names).</param>
    public static ShaderCatalog? Load(string globalWadPath, string gameDirectory, string environment,
        IHashResolver resolver, Func<uint, string?> resolveBinName)
    {
        try
        {
            using var wad = WadArchive.Open(globalWadPath, resolver);
            var entry = wad.Entries.FirstOrDefault(e =>
                e.IsResolved && e.Path.Equals(ShaderBinPath, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return null;

            var tree = new BinTree(new MemoryStream(wad.Extract(entry), writable: false));
            var shaders = new List<LeagueShaderDef>();
            foreach (var (hash, obj) in tree.Objects)
            {
                string name = resolveBinName(hash) ?? $"0x{hash:x8}";
                if (!name.StartsWith("Shaders/", StringComparison.OrdinalIgnoreCase)) continue;
                shaders.Add(Parse(name, obj, resolveBinName));
            }
            shaders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return new ShaderCatalog { Environment = environment, GameDirectory = gameDirectory, Shaders = shaders };
        }
        catch { return null; }
    }

    private static LeagueShaderDef Parse(string name, BinTreeObject obj, Func<uint, string?> resolve)
    {
        var parts = name.Split('/');
        string category = parts.Length > 1 ? parts[1] : "Other";

        var textures = new List<ShaderTextureDef>();
        foreach (var st in Structs(obj, "textures", resolve))
            if (Str(st, "name", resolve) is { Length: > 0 } tn)
                textures.Add(new ShaderTextureDef(tn, Str(st, "defaultTexturePath", resolve) ?? ""));

        var parameters = new List<ShaderParamDef>();
        foreach (var st in Structs(obj, "parameters", resolve))
            if (Str(st, "name", resolve) is { Length: > 0 } pn)
            {
                var v = Field(st, "data", resolve) is BinTreeVector4 v4 ? v4.Value : default;
                parameters.Add(new ShaderParamDef(pn, v.X, v.Y, v.Z, v.W));
            }

        var switches = new List<string>();
        foreach (var st in Structs(obj, "staticSwitches", resolve))
            if (Str(st, "name", resolve) is { Length: > 0 } sn)
                switches.Add(sn);

        return new LeagueShaderDef(name, category, textures, parameters, switches);
    }

    private static IEnumerable<BinTreeStruct> Structs(BinTreeObject obj, string field, Func<uint, string?> resolve) =>
        Field(obj.Properties, field, resolve) is BinTreeContainer c
            ? c.Elements.OfType<BinTreeStruct>()
            : Enumerable.Empty<BinTreeStruct>();

    private static BinTreeProperty? Field(BinTreeStruct st, string field, Func<uint, string?> resolve) =>
        Field(st.Properties, field, resolve);

    private static BinTreeProperty? Field(IEnumerable<KeyValuePair<uint, BinTreeProperty>> props, string field, Func<uint, string?> resolve)
    {
        foreach (var (h, p) in props)
            if (string.Equals(resolve(h), field, StringComparison.OrdinalIgnoreCase))
                return p;
        return null;
    }

    private static string? Str(BinTreeStruct st, string field, Func<uint, string?> resolve) =>
        Field(st, field, resolve) is BinTreeString s ? s.Value : null;
}

/// <summary>Persists a scanned catalogue so the (multi-second) Global.wad scan happens once per install.</summary>
public static class ShaderCatalogCache
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static void Save(ShaderCatalog catalog, string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(catalog, Options));
        }
        catch { /* a cache we can't write just means we rescan next time */ }
    }

    public static ShaderCatalog? Load(string path, string gameDirectory)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var c = JsonSerializer.Deserialize<ShaderCatalog>(File.ReadAllText(path));
            // A cache from a different install must not be served for this one.
            return c is { Shaders.Count: > 0 }
                   && string.Equals(c.GameDirectory, gameDirectory, StringComparison.OrdinalIgnoreCase) ? c : null;
        }
        catch { return null; }
    }
}
