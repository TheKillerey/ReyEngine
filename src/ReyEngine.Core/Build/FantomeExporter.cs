using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReyEngine.Core.Build;

public sealed class FantomeMeta
{
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = "";
    public string? Heart { get; set; }   // optional URL (donate/social)
    public string? Home { get; set; }    // optional URL (homepage/releases)

    /// <summary>M814: the tool that wrote the archive, "name version". Informational: league-mod's reader
    /// shows it and never varies its behaviour on it.</summary>
    public string? Generator { get; set; }

    /// <summary>M814: every layer of the mod, base included - the table written to <c>Layers</c>. Empty
    /// means a mod of the base layer alone.</summary>
    public IReadOnlyList<FantomeLayer> Layers { get; set; } = Array.Empty<FantomeLayer>();
}

/// <summary>M814: one entry of a .fantome's <c>Layers</c> table (<c>ltk_fantome</c>'s
/// <c>FantomeLayerInfo</c>). The table key and <see cref="Name"/> are the same string.</summary>
/// <param name="GameData">The layer's declarations: a <c>DeclarationDocument</c> (<c>{version, modules}</c>);
/// null when the layer declares nothing.</param>
public sealed record FantomeLayer(string Name, int Priority, string? DisplayName = null, JsonNode? GameData = null);

/// <summary>M814: a packed <c>.wad.client</c> and the layer whose WAD directory holds it.</summary>
/// <param name="ChunkPaths">The WAD-relative path of every file packed into it. The WAD stores hashes only;
/// the harvested hashtable keeps these names so an import can give the author's files their paths back.</param>
public sealed record FantomeWad(string Path, string Layer = FantomeLayers.Base, IReadOnlyList<string>? ChunkPaths = null);

/// <summary>
/// M814: the layer rules of the .fantome layout, as <c>ltk_fantome</c> 0.15.1 states them
/// (<c>reader.rs:795-878</c>).
///
/// <para>The base layer's WADs are entries of <c>WAD/</c>; every other layer's are entries of
/// <c>WAD_&lt;layer&gt;/</c>, matched case-insensitively. A layer name is one or more ASCII letters, digits,
/// <c>-</c> or <c>_</c> (<c>is_layer_name</c>), and <c>base</c> in any casing names the base layer, so a
/// <c>WAD_base/</c> directory is not a layer. A name outside that set cannot name a directory the reader
/// recognises, and a mod holding one would lose that layer's content without a word.</para>
/// </summary>
public static class FantomeLayers
{
    public const string Base = "base";

    /// <summary>
    /// Whether the GameData modules of an export carry a <c>name</c>. They do not.
    ///
    /// <para>A name is a label for a manager's declarations view and carries no meaning for loading or applying. It
    /// is not worth what it can cost. Measured: <c>ltk_game_data</c> 0.6.0 refuses a layer whose module has one
    /// (<c>unknown field `name`, expected one of `target`, `edits`, `entries`, `origin`</c>, from
    /// <c>deny_unknown_fields</c> on <c>Module</c>) and reads the same document without it. LTK Manager v1.21.0
    /// (2026-09-23; <c>ltk_fantome</c> 0.14.2 and <c>ltk_game_data</c> 0.6.0 in its Cargo.lock) reads a fantome's
    /// GameData with that version, so a named module has the manager refuse the whole layer. The declared bins are
    /// not in the WADs, so the player's mod silently loses its bin edits. <c>name</c> exists from
    /// <c>ltk_game_data</c> 0.7.0; the 1.25.0 manager installed on the development machine bundles 0.8.0 and reads
    /// either form.</para>
    ///
    /// <para>The renderer keeps its name support: <c>BinDeclarations.GameDataDocument</c> takes the flag, defaulting
    /// to this constant, and labels a module with the project path of its bin. Flip this to true only when no
    /// manager that still pins 0.6 is worth supporting.</para>
    /// </summary>
    public const bool ModuleNames = false;

    public static bool IsBase(string name) => name.Equals(Base, StringComparison.OrdinalIgnoreCase);

    /// <summary><c>ltk_fantome::is_layer_name</c>.</summary>
    public static bool IsLayerName(string name)
    {
        if (name.Length == 0) return false;
        foreach (char c in name)
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_')) return false;
        return true;
    }

    /// <summary>Why <paramref name="name"/> cannot be the name of a layer other than base, or null.</summary>
    public static string? NameProblem(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "A layer needs a name.";
        if (IsBase(name)) return $"'{name}' is the base layer, which every mod has and no other layer can be called.";
        if (!IsLayerName(name))
            return $"'{name}' cannot name a .fantome layer: a layer's content lives in WAD_{name}/, and a layer name is "
                 + "ASCII letters, digits, '-' and '_' only.";
        return null;
    }

    /// <summary>The archive directory a layer's WADs are entries of: <c>WAD</c> or <c>WAD_&lt;layer&gt;</c>.</summary>
    public static string WadDirectory(string layer) => IsBase(layer) ? "WAD" : "WAD_" + layer;

    /// <summary>
    /// <c>ltk_mod_project</c>'s <c>natural_cmp</c>: names compare as a person reads them. A run of digits
    /// compares by the number it spells, padding zeros ignored, so <c>layer9</c> comes before <c>layer10</c>;
    /// everything else compares byte by byte; names that tie on every run fall back to plain ordinal order.
    /// </summary>
    public static int NaturalCompare(string a, string b)
    {
        int c = NaturalRuns(a, b);
        return c != 0 ? c : Math.Sign(string.CompareOrdinal(a, b));
    }

    private static int NaturalRuns(string a, string b)
    {
        int i = 0, j = 0;
        while (true)
        {
            if (i >= a.Length && j >= b.Length) return 0;
            if (i >= a.Length) return -1;
            if (j >= b.Length) return 1;
            char x = a[i], y = b[j];
            if (char.IsAsciiDigit(x) && char.IsAsciiDigit(y))
            {
                var (xs, xe) = Number(a, i);
                var (ys, ye) = Number(b, j);
                int len = (xe - xs).CompareTo(ye - ys);   // longer means larger once the padding is gone
                int c = len != 0 ? len : string.CompareOrdinal(a, xs, b, ys, xe - xs);
                if (c != 0) return Math.Sign(c);
                i = xe; j = ye;
                continue;
            }
            if (x == y) { i++; j++; continue; }
            return x < y ? -1 : 1;
        }
    }

    /// <summary>The digits of the run starting at <paramref name="at"/> with their padding zeros dropped:
    /// the start of the value and the end of the run.</summary>
    private static (int Start, int End) Number(string s, int at)
    {
        int end = at;
        while (end < s.Length && char.IsAsciiDigit(s[end])) end++;
        int start = at;
        while (start < end && s[start] == '0') start++;
        return (start, end);
    }
}

/// <summary>
/// M814: the harvested hashtable - the names a .fantome keeps for the chunks of its packed WADs, which store
/// hashes and no paths. Mirrors <c>ltk_mod_project</c>'s <c>harvested_routes</c>
/// (<c>fantome/pack.rs:381-445</c>) for a mod that declares no table of its own.
///
/// <para>The names are the WAD-relative paths of the packed files, as the author spelled them, de-duplicated
/// and sorted in byte order. A path whose file stem is sixteen hexadecimal digits is a nameless chunk and is
/// left out. A path outside the table grammar - printable ASCII with no backslash (<c>table.rs:43-47</c>) -
/// is left out too and stays hex, which is where it would have been without a table. The file is one name per
/// line, each ended by LF, with no byte order mark.</para>
///
/// <para>One difference from <c>ltk_mod_project</c>: it takes a trailing <c>.ltk</c> off a path (its extractor adds
/// one to a path two chunks claimed), because it hashes the name without it. ReyEngine hashes the path it packs,
/// so a file called <c>x.ltk</c> is the chunk <c>x.ltk</c> and keeps its name.</para>
/// </summary>
public static class FantomeHashtables
{
    public const string HarvestedPath = "META/hashes/game.harvested.hashes.txt";

    /// <summary>The sorted names, empty when there is nothing to record.</summary>
    public static IReadOnlyList<string> Harvest(IEnumerable<string> chunkPaths)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string path in chunkPaths)
            if (IsTableName(path) && !IsHexChunkPath(path)) names.Add(path);
        return names.ToList();
    }

    /// <summary>The file's text: each name, then LF.</summary>
    public static string Text(IReadOnlyList<string> names)
    {
        var sb = new StringBuilder();
        foreach (string name in names) sb.Append(name).Append('\n');
        return sb.ToString();
    }

    /// <summary><c>ltk_hashtable</c>'s <c>is_valid_name</c>: printable ASCII, no backslash.</summary>
    public static bool IsTableName(string name)
    {
        if (name.Length == 0) return false;
        foreach (char c in name)
            if (c < 0x20 || c > 0x7e || c == '\\') return false;
        return true;
    }

    /// <summary><c>ltk_wad::is_hex_chunk_path</c>: the file stem - the last name without its last extension -
    /// is sixteen hexadecimal digits.</summary>
    public static bool IsHexChunkPath(string path)
    {
        string file = path[(path.LastIndexOf('/') + 1)..];
        int dot = file.LastIndexOf('.');
        string stem = dot > 0 ? file[..dot] : file;
        if (stem.Length != 16) return false;
        foreach (char c in stem)
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')) return false;
        return true;
    }
}

/// <summary>
/// Writes a Fantome / cslol-manager / LTK Manager mod package (<c>.fantome</c>): a ZIP with
/// <c>META/info.json</c>, <c>META/image.png</c> (thumbnail), <c>META/details.json</c> (cslol layer config) and
/// the mod WAD(s) under <c>WAD/</c>.
///
/// <para><b>M814: LTK's layered layout.</b> <c>META/info.json</c> keeps the fields it always had (Name, Author,
/// Version, Description, Heart, Home - league-mod's <c>FantomeInfo</c> has no <c>deny_unknown_fields</c> and
/// carries what it does not know in a flattened map, and cslol-manager reads Heart and Home) and adds what
/// <c>ltk_mod_project</c> 0.16.2 writes: <c>Layers</c> (every layer, base included, keyed by its name, each
/// <c>GameData</c>/<c>Name</c>/<c>DisplayName</c>/<c>Priority</c> in <c>FantomeLayerInfo</c>'s field order),
/// <c>Hashtables</c> (the harvested table, when there is one) and <c>Generator</c>. The base layer's WADs are
/// entries of <c>WAD/</c>; another layer's are entries of <c>WAD_&lt;layer&gt;/</c> (<see cref="FantomeLayers"/>),
/// which a reader that predates layers skips. A layer that holds only declarations has no WAD directory.</para>
///
/// <para>The JSON is pretty-printed with two-space indentation and LF, UTF-8 without a byte order mark, and
/// written in a fixed key order: the base layer first, then the others by priority and name.</para>
///
/// <para><b>Why <c>META/details.json</c> stays.</b> It is the cslol layer config of the M17 export, which was
/// checked byte for byte against a real Fantome export. <c>ltk_fantome</c> places no <c>META/details.json</c>
/// (<c>classify_entry</c> returns none for it), so league-mod ignores it; a cslol-manager that reads it still
/// finds what it always did, and its description - one base layer in <c>WAD</c> - is true of the base layer
/// here. Removing it would change what the oldest consumer sees for nothing.</para>
/// </summary>
public static class FantomeExporter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private const string DetailsJson =
        "{\n" +
        "  \"Priority\": 10,\n" +
        "  \"override_\": false,\n" +
        "  \"InnerPath\": \"\",\n" +
        "  \"Random\": false,\n" +
        "  \"Layers\": [\n" +
        "    { \"Name\": \"base\", \"Priority\": 1, \"folder_name\": \"WAD\", \"is_active\": false, \"Description\": null }\n" +
        "  ],\n" +
        "  \"layerss\": \"None\"\n" +
        "}";

    /// <summary>M814: league-mod's apply order (<c>ModProjectLayer::apply_order</c>): the base layer first, then
    /// by priority, then by name as a person reads it - <c>layer9</c> before <c>layer10</c>. The base layer is
    /// added when the table lacks it, and every other name is checked against the layer-name rule.</summary>
    /// <exception cref="InvalidOperationException">A name the format cannot carry, or two layers one name
    /// (the format compares layer names without regard to case).</exception>
    public static IReadOnlyList<FantomeLayer> OrderLayers(IEnumerable<FantomeLayer> layers)
    {
        FantomeLayer? baseLayer = null;
        var others = new List<FantomeLayer>();
        foreach (var layer in layers)
        {
            if (FantomeLayers.IsBase(layer.Name))
            {
                if (baseLayer is not null) throw new InvalidOperationException("The layer table names the base layer twice.");
                baseLayer = layer with { Name = FantomeLayers.Base };
                continue;
            }
            if (FantomeLayers.NameProblem(layer.Name) is { } problem) throw new InvalidOperationException(problem);
            if (others.Any(o => o.Name.Equals(layer.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Two layers are called '{layer.Name}': a .fantome compares layer names without regard to case.");
            others.Add(layer);
        }
        others.Sort((a, b) =>
        {
            int c = a.Priority.CompareTo(b.Priority);
            return c != 0 ? c : FantomeLayers.NaturalCompare(a.Name, b.Name);
        });
        // the base layer applies first whatever priority it is given, and league-mod corrects a base layer
        // whose priority is not 0 on import (ModProjectLayer::normalize_table), so it is written as 0
        var baseFirst = (baseLayer ?? new FantomeLayer(FantomeLayers.Base, 0)) with { Priority = 0 };
        return new[] { baseFirst }.Concat(others).ToList();
    }

    /// <summary>M814: the text of <c>META/info.json</c>. <paramref name="layers"/> is the ordered table
    /// (<see cref="OrderLayers"/>); <paramref name="hashtable"/> adds the <c>Hashtables</c> entry for
    /// <see cref="FantomeHashtables.HarvestedPath"/>.</summary>
    public static string BuildInfoJson(FantomeMeta meta, IReadOnlyList<FantomeLayer> layers, bool hashtable)
    {
        var info = new JsonObject
        {
            ["Name"] = meta.Name,
            ["Author"] = meta.Author,
            ["Version"] = meta.Version,
            ["Description"] = meta.Description,
        };
        if (!string.IsNullOrWhiteSpace(meta.Heart)) info["Heart"] = meta.Heart;
        if (!string.IsNullOrWhiteSpace(meta.Home)) info["Home"] = meta.Home;

        var table = new JsonObject();
        foreach (var layer in layers)
        {
            var entry = new JsonObject();
            // FantomeLayerInfo's field order: GameData, Name, DisplayName, Priority
            if (layer.GameData is not null) entry["GameData"] = layer.GameData.DeepClone();
            entry["Name"] = layer.Name;
            if (!string.IsNullOrEmpty(layer.DisplayName)) entry["DisplayName"] = layer.DisplayName;
            entry["Priority"] = layer.Priority;
            table[layer.Name] = entry;
        }
        info["Layers"] = table;

        if (hashtable)
            info["Hashtables"] = new JsonArray(new JsonObject
            {
                ["Path"] = FantomeHashtables.HarvestedPath,
                ["Category"] = "game",
                ["Algorithm"] = "xxh64",
                ["Bits"] = 64,
            });
        if (!string.IsNullOrWhiteSpace(meta.Generator)) info["Generator"] = meta.Generator;
        return info.ToJsonString(Json);
    }

    /// <summary>The archive entry a packed WAD is stored as.</summary>
    public static string WadEntryName(FantomeWad wad) =>
        FantomeLayers.WadDirectory(wad.Layer) + "/" + System.IO.Path.GetFileName(wad.Path);

    /// <summary>
    /// Writes the package. M814: the archive is built beside its destination as <c>&lt;output&gt;.tmp</c> and moved over
    /// <paramref name="outputFantome"/> only when it is complete, so a failure at any point - a layer the format cannot
    /// carry, a WAD another program holds open, a full disk - leaves an existing package exactly as it was, leaves no
    /// partial one where there was none, and removes its temporary file.
    ///
    /// <para>Everything that can be refused is refused, and every piece of text that goes into the archive is built,
    /// before the first file is created; what is left to fail while writing is input/output.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">A layer name the format cannot carry, a WAD in a layer the mod does
    /// not declare, or two WADs that would be stored under one name.</exception>
    public static void Export(FantomeMeta meta, IReadOnlyList<FantomeWad> wads, byte[]? thumbnailPng, string outputFantome)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(wads);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFantome);

        // 1. refuse what cannot be written, with the output untouched
        var layers = OrderLayers(meta.Layers);
        var entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var wad in wads)
        {
            if (!layers.Any(l => l.Name.Equals(wad.Layer, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    $"{System.IO.Path.GetFileName(wad.Path)} is in layer '{wad.Layer}', which the mod does not declare.");
            if (!entryNames.Add(WadEntryName(wad)))
                throw new InvalidOperationException($"Two WADs would be stored as {WadEntryName(wad)}.");
        }

        // 2. build the archive's text before any file exists. Names are kept for the WADs that are written.
        var names = FantomeHashtables.Harvest(wads.Where(w => File.Exists(w.Path)).SelectMany(w => w.ChunkPaths ?? Array.Empty<string>()));
        string? hashtable = names.Count > 0 ? FantomeHashtables.Text(names) : null;
        string info = BuildInfoJson(meta, layers, hashtable: hashtable is not null);

        // 3. write beside the destination, then move into place
        var dir = System.IO.Path.GetDirectoryName(outputFantome);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string temp = outputFantome + ".tmp";
        if (File.Exists(temp)) File.Delete(temp);        // what an export killed mid-write left behind; the name is ours

        ZipArchive? zip = null;
        try
        {
            zip = ZipFile.Open(temp, ZipArchiveMode.Create);

            if (hashtable is not null) WriteText(zip, FantomeHashtables.HarvestedPath, hashtable);
            WriteText(zip, "META/info.json", info);
            WriteText(zip, "META/details.json", DetailsJson);

            if (thumbnailPng is { Length: > 0 })
            {
                var entry = zip.CreateEntry("META/image.png", CompressionLevel.NoCompression);
                using var s = entry.Open();
                s.Write(thumbnailPng, 0, thumbnailPng.Length);
            }

            foreach (var wad in wads)
            {
                if (!File.Exists(wad.Path)) continue;
                // WAD chunks are already Zstd-compressed - store the file rather than re-deflating it.
                zip.CreateEntryFromFile(wad.Path, WadEntryName(wad), CompressionLevel.NoCompression);
            }

            zip.Dispose();                               // writes the central directory; a failure here is a failed export
            zip = null;
            File.Move(temp, outputFantome, overwrite: true);
        }
        catch
        {
            try { zip?.Dispose(); } catch { /* the failure being reported is the first one */ }
            try { File.Delete(temp); } catch { /* best effort: the next export replaces a temp the OS still held */ }
            throw;
        }
    }

    private static void WriteText(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var s = entry.Open();
        var bytes = Utf8.GetBytes(content);
        s.Write(bytes, 0, bytes.Length);
    }
}
