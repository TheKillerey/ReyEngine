using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReyEngine.Core.Hashing;

/// <summary>One published table in a Mimir release.</summary>
public sealed class MimirTable
{
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("entries")] public long Entries { get; set; }
    [JsonPropertyName("key_width")] public int KeyWidth { get; set; }
}

/// <summary>Where a Mimir release was generated from.</summary>
public sealed class MimirSource
{
    [JsonPropertyName("repo")] public string Repo { get; set; } = "";
    [JsonPropertyName("commit")] public string Commit { get; set; } = "";
    [JsonPropertyName("inputs_sha256")] public string InputsSha256 { get; set; } = "";
}

/// <summary>
/// M495: the <c>manifest.json</c> shipped alongside Mimir's <c>.lhdb</c> tables.
///
/// <para>It is the reason this integration can be trusted without trusting the download: every table
/// carries its SHA-256 and entry count, and the manifest records the exact CommunityDragon commit the
/// tables were generated from. A partial or tampered download fails the hash and is discarded rather than
/// being memory-mapped and served as truth.</para>
///
/// <para>Shape as published in the 2026-08-14 release: <c>schema</c>, <c>generated_at</c>,
/// <c>source {repo, commit, inputs_sha256}</c> and <c>tables</c> keyed by table name — binentries,
/// binfields, binhashes, bintypes, game, lcu, rst, rst-xxh3.</para>
/// </summary>
public sealed class MimirManifest
{
    [JsonPropertyName("schema")] public int Schema { get; set; }
    [JsonPropertyName("generated_at")] public string GeneratedAt { get; set; } = "";
    [JsonPropertyName("source")] public MimirSource? Source { get; set; }
    [JsonPropertyName("tables")] public Dictionary<string, MimirTable> Tables { get; set; } = new();

    /// <summary>The release this manifest came from, e.g. "hashes-2026-08-14". Not part of the published
    /// document — the updater stamps it so a cached copy knows which release produced it.</summary>
    [JsonPropertyName("rey_release_tag")] public string ReleaseTag { get; set; } = "";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static MimirManifest? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<MimirManifest>(json, Options); }
        catch { return null; }
    }

    public static MimirManifest? Load(string path)
        => File.Exists(path) ? Parse(File.ReadAllText(path)) : null;

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Tables this build knows how to consume, mapped to what they resolve.</summary>
    public IEnumerable<(string Name, MimirTable Table, MimirTableKind Kind)> UsableTables()
    {
        foreach (var (name, table) in Tables)
        {
            var kind = KindOf(name);
            if (kind != MimirTableKind.Ignored) yield return (name, table, kind);
        }
    }

    /// <summary>
    /// What a table resolves, decided by NAME rather than by key width.
    ///
    /// <para>Key width alone cannot tell these apart in the way that matters: <c>game</c> and <c>lcu</c>
    /// are both 8-wide WAD path tables, while <c>rst</c> and <c>rst-xxh3</c> are also wide but hold
    /// translation-string hashes that are not paths at all. HashDatabase's existing text loader skips RST
    /// for exactly that reason, and this keeps that decision.</para>
    /// </summary>
    public static MimirTableKind KindOf(string name) => name.ToLowerInvariant() switch
    {
        "binentries" or "binhashes" or "binfields" or "bintypes" => MimirTableKind.Bin,
        "game" or "lcu" => MimirTableKind.Wad,
        _ => MimirTableKind.Ignored,      // rst / rst-xxh3: translation hashes, not WAD or bin names
    };
}

/// <summary>Which lookup a Mimir table serves.</summary>
public enum MimirTableKind
{
    /// <summary>Not consumed — translation hashes and anything this build does not recognise.</summary>
    Ignored,
    /// <summary>32-bit FNV-1a .bin names.</summary>
    Bin,
    /// <summary>64-bit XxHash64 WAD paths.</summary>
    Wad,
}
