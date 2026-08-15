using System.Globalization;
using System.Numerics;
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

/// <summary>The material state Riot authors most often for one shader. This is separate from the raw
/// shaders.bin defaults: those describe the parameter ABI and frequently contain zero multipliers,
/// while real StaticMaterialDefs supply the values the shader is intended to use.</summary>
public sealed record ShaderMaterialSetup(
    Dictionary<string, Vector4> Parameters,
    Dictionary<string, bool> Switches,
    Dictionary<string, string> Macros,
    bool BlendEnable,
    bool? CullEnable,
    int SourceBlendFactor,
    int DestinationBlendFactor)
{
    public int SourceMaterialCount { get; init; }
    public int MatchingSetupCount { get; init; }
    public string ExampleMaterial { get; init; } = "";

    public string Summary => MatchingSetupCount > 0
        ? $"Most used Riot setup ({MatchingSetupCount:n0} of {SourceMaterialCount:n0} material(s))"
        : "Riot material setup";
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
    List<string> StaticSwitches,
    ShaderMaterialSetup? CommonSetup = null,
    /// <summary>M431: the shader's own featureDefines, which name the vertex data and render features
    /// it needs. FEATURE_BAKED_PAINT means it reads the baked UV set (Texcoord7) - a mesh without one
    /// cannot satisfy the input layout.</summary>
    Dictionary<string, string>? FeatureDefines = null)
{
    /// <summary>Name without the <c>Shaders/Category/</c> prefix (what the dropdown shows).</summary>
    public string ShortName { get { int i = Name.LastIndexOf('/'); return i < 0 ? Name : Name[(i + 1)..]; } }
    public string Summary => $"{Textures.Count} sampler(s) · {Parameters.Count} param(s) · {StaticSwitches.Count} switch(es)"
        + (CommonSetup is null ? "" : $" · {CommonSetup.Summary}");
}

/// <summary>Every shader one game install ships, as scanned from its <c>data/shaders/shaders.bin</c>.</summary>
public sealed class ShaderCatalog
{
    /// <summary>"Live", "PBE", or whatever the caller labelled the install.</summary>
    public string Environment { get; init; } = "";
    /// <summary>The game directory this was scanned from (so a stale cache can be spotted).</summary>
    public string GameDirectory { get; init; } = "";

    /// <summary>
    /// M475: identity of the <c>Global.wad.client</c> this was read from — size and last-write time.
    ///
    /// <para>Matching the game DIRECTORY was the only cache check, and a directory does not change when
    /// Riot patches it. Measured on this install: the Live cache was written 2026-07-20 holding 347
    /// shaders while the installed client's shaders.bin declares 351, so four shaders — including
    /// <c>Shaders/StaticMesh/4TextureBlend_UVBased_baseMat</c> — could not be picked in any shader
    /// dropdown and would never come back on their own. The PBE cache, written the same day, has all 351,
    /// which is what made it look like a per-shader problem rather than a stale file.</para>
    ///
    /// <para>Empty on a cache written before this field existed, which mismatches any real stamp and makes
    /// those caches rebuild once — the migration is the invalidation.</para>
    /// </summary>
    public string SourceStamp { get; init; } = "";

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
            return new ShaderCatalog
            {
                Environment = environment,
                GameDirectory = gameDirectory,
                SourceStamp = StampFor(globalWadPath),   // M475
                Shaders = shaders,
            };
        }
        catch { return null; }
    }

    /// <summary>M475: size + last-write time of the shader source, the same shape
    /// <c>WorkshopCatalogService.Fingerprint</c> already uses per wad. Cheap enough to compute on every
    /// load, which is the point — the check has to run before the cache is trusted.</summary>
    public static string StampFor(string globalWadPath)
    {
        try
        {
            var info = new FileInfo(globalWadPath);
            return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "";
        }
        catch { return ""; }
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

        var features = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Field(obj.Properties, "featureDefines", resolve) is BinTreeMap fm)
            foreach (var kv in fm)
                if (kv.Key is BinTreeString fk)
                    features[fk.Value] = kv.Value is BinTreeString fv ? fv.Value : "";

        return new LeagueShaderDef(name, category, textures, parameters, switches, null, features);
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

    /// <param name="expectedStamp">
    /// M475: <see cref="ShaderCatalogLoader.StampFor"/> of the CURRENT Global.wad. Required, because
    /// matching only the game directory meant a Riot patch never invalidated the cache — the directory
    /// path is identical before and after. Pass "" only where no source file can be identified; that
    /// disables the check and restores the old behaviour deliberately rather than by omission.
    /// </param>
    public static ShaderCatalog? Load(string path, string gameDirectory, string expectedStamp)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var c = JsonSerializer.Deserialize<ShaderCatalog>(File.ReadAllText(path));
            if (c is not { Shaders.Count: > 0 }) return null;
            // A cache from a different install must not be served for this one.
            if (!string.Equals(c.GameDirectory, gameDirectory, StringComparison.OrdinalIgnoreCase)) return null;
            // ...nor one read from a different build of the same install. A cache written before this
            // field existed has an empty stamp and therefore rebuilds once.
            if (expectedStamp.Length > 0 && !string.Equals(c.SourceStamp, expectedStamp, StringComparison.Ordinal))
                return null;
            return c;
        }
        catch { return null; }
    }
}
