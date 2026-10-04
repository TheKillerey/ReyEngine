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

    // ---- M816: what an LTK-layered package carries beyond the layers. Each is written only when set. ----

    /// <summary><c>License</c>: a bare string, or an object with a name and a link. The shape (<see cref="Projects.ProjectLicense.AsObject"/>)
    /// is written as it was read.</summary>
    public Projects.ProjectLicense? License { get; set; }

    /// <summary><c>Tags</c> (for example <c>map-skin</c>); omitted when empty.</summary>
    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();

    /// <summary><c>Champions</c> the mod targets; omitted when empty.</summary>
    public IReadOnlyList<string> Champions { get; set; } = Array.Empty<string>();

    /// <summary><c>Maps</c> the mod targets (for example <c>summoners-rift</c>); omitted when empty.</summary>
    public IReadOnlyList<string> Maps { get; set; } = Array.Empty<string>();

    /// <summary>The package's own text files - <c>META/README.md</c> and <c>META/LICENSE</c> - as (entry name, file on disk).</summary>
    public IReadOnlyList<(string EntryName, string FilePath)> MetaFiles { get; set; } = Array.Empty<(string, string)>();
}

/// <summary>M816: a file a layer's declarations name under <c>overrides</c> (a <c>.ptch</c>), stored in the package at
/// <c>META/game_data/&lt;layer&gt;/&lt;Path&gt;</c>.</summary>
/// <param name="Path">The layer-relative path the declarations spell it by, with '/' separators.</param>
/// <param name="FilePath">The file to store, on disk.</param>
public sealed record FantomeOverrideFile(string Path, string FilePath);

/// <summary>M814: one entry of a .fantome's <c>Layers</c> table (<c>ltk_fantome</c>'s
/// <c>FantomeLayerInfo</c>). The table key and <see cref="Name"/> are the same string.</summary>
/// <param name="GameData">The layer's declarations: a <c>DeclarationDocument</c> (<c>{version, modules}</c>);
/// null when the layer declares nothing. M816: when the layer also has <see cref="ImportedGameData"/>, these are ReyEngine's OWN
/// modules, and only their <c>modules</c> are used - they are written behind the imported ones.</param>
public sealed record FantomeLayer(string Name, int Priority, string? DisplayName = null, JsonNode? GameData = null)
{
    /// <summary>
    /// M816: the GameData document an import stored for this layer (<see cref="Projects.LtkProjectStore"/>), as TEXT. Written
    /// FIRST and VERBATIM: every imported module keeps its own spelling, its <c>name</c> and its <c>origin</c>, and with no
    /// own modules to add the document is written as it came. ReyEngine's own modules (<see cref="GameData"/>) follow it - their
    /// <c>origin.module</c> numbers must count on from the imported ones - so a layer that holds both reads, in
    /// LTK's order, as what the author declared and then what this project changed.
    /// </summary>
    public string? ImportedGameData { get; init; }

    /// <summary>M816: the layer's string overrides (locale, field, text), written after <c>Priority</c> as <c>FantomeLayerInfo</c>
    /// orders them; null or empty writes none.</summary>
    public JsonObject? StringOverrides { get; init; }

    /// <summary>M816: the files this layer's declarations name, stored below <c>META/game_data/&lt;layer&gt;/</c>.</summary>
    public IReadOnlyList<FantomeOverrideFile> OverrideFiles { get; init; } = Array.Empty<FantomeOverrideFile>();

    /// <summary>
    /// M823: the modules ReyEngine keeps on top of this layer's imported ones - the edits a person made to bins the imported GameData targets, one literal
    /// module per bin (<see cref="Projects.LtkEditStore"/>) - already numbered for their place in the document (<see cref="GameDataDocumentText.ModuleNode"/>).
    /// They are written right after the imported modules, so at install they run after every imported module that touches their bin, and before the
    /// declarations the planner makes of the project's own bins (<see cref="GameData"/>), whose numbering continues from them.
    /// </summary>
    public IReadOnlyList<JsonNode> EditModules { get; init; } = Array.Empty<JsonNode>();

    /// <summary>The own modules, as nodes, in the order they are written behind the imported ones: the edits (<see cref="EditModules"/>), then the planner's
    /// (<see cref="GameData"/>; an object with no <c>modules</c> list has none).</summary>
    internal IReadOnlyList<JsonNode> OwnModules
    {
        get
        {
            var planned = GameData is JsonObject o && o["modules"] is JsonArray list ? list.OfType<JsonNode>().ToList() : new List<JsonNode>();
            return EditModules.Count == 0 ? planned : EditModules.Concat(planned).ToList();
        }
    }
}

/// <summary>M814: a packed <c>.wad.client</c> and the layer whose WAD directory holds it.</summary>
/// <param name="ChunkPaths">The WAD-relative path of every file packed into it. The WAD stores hashes only;
/// the harvested hashtable keeps these names so an import can give the author's files their paths back.</param>
public sealed record FantomeWad(string Path, string Layer = FantomeLayers.Base, IReadOnlyList<string>? ChunkPaths = null)
{
    /// <summary>M816: the name the archive stores the WAD under (<c>Map11.wad.client</c>), when it is not the name of the file.
    /// A project can hold one WAD in two layers, and the two staged files cannot both be called <c>Map11.wad.client</c>.</summary>
    public string? Name { get; init; }
}

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

    /// <summary>Why <paramref name="name"/> cannot be the name of a layer other than base, or null.
    ///
    /// <para>M816 review: a layer is also a FOLDER - <c>content/&lt;layer&gt;/</c> in LTK Manager's workshop, and the folder Build Package
    /// writes a layer's WADs to - and a layer name arrives from a package. Besides the rule of <see cref="IsLayerName"/> (which keeps every
    /// separator, drive letter and <c>..</c> out) a Windows device name is refused: <c>con</c> is made of letters and passes that rule,
    /// and no folder on Windows can be called it.</para></summary>
    public static string? NameProblem(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "A layer needs a name.";
        if (IsBase(name)) return $"'{name}' is the base layer, which every mod has and no other layer can be called.";
        if (!IsLayerName(name))
            return $"'{name}' cannot name a .fantome layer: a layer's content lives in WAD_{name}/, and a layer name is "
                 + "ASCII letters, digits, '-' and '_' only.";
        if (IsWindowsDeviceName(name))
            return $"'{name}' is a Windows device name, which no folder can be called, and a layer is a folder "
                 + "(content/<layer>/ in LTK Manager's workshop, WAD_<layer>/ in a .fantome).";
        if (name.Length > MaxLayerNameLength)
            return $"A layer name is at most {MaxLayerNameLength} characters long ('{name[..20]}...' has {name.Length}): a layer is a folder, "
                 + "and the files below it must still fit in a path.";
        return null;
    }

    /// <summary>M816 review: the longest layer name <see cref="NameProblem"/> accepts. A layer is a folder with a WAD folder and files
    /// below it, and a path has a limit; <c>ltk_fantome</c> sets none, and no real layer comes near this.</summary>
    public const int MaxLayerNameLength = 100;

    /// <summary>M816 review: <c>CON</c>, <c>PRN</c>, <c>AUX</c>, <c>NUL</c>, <c>COM0</c>-<c>COM9</c> and <c>LPT0</c>-<c>LPT9</c>, in any casing:
    /// the names Windows reserves for devices, which a path cannot use for a file or a folder.</summary>
    public static bool IsWindowsDeviceName(string name)
    {
        if (name.Length == 3)
            return name.Equals("CON", StringComparison.OrdinalIgnoreCase) || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || name.Equals("AUX", StringComparison.OrdinalIgnoreCase) || name.Equals("NUL", StringComparison.OrdinalIgnoreCase);
        return name.Length == 4 && name[3] is >= '0' and <= '9'
            && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// M816 review: a name <see cref="NameProblem"/> accepts that stands in for <paramref name="original"/>, which it refused. What an import
    /// does with a layer whose name a package spelled in a way no folder can carry (<c>..\..</c>, <c>C:\Temp\x</c>, <c>a/b</c>, <c>con</c>):
    /// the layer is kept, under a name that is safe to write to disk.
    ///
    /// <para>Each run of characters a layer name may not hold becomes one <c>_</c> (at either end it is dropped), so <c>my layer</c>
    /// is <c>my_layer</c> and <c>C:\Temp\x</c> is <c>C_Temp_x</c>; a name nothing is left of is <c>layer</c>; a name that is
    /// still the base layer or a device name gets a trailing <c>_</c>; one that another layer already holds (compared without regard to
    /// case) gets <c>-2</c>, <c>-3</c>, ... The result depends on the name and on <paramref name="taken"/> alone, so importing one package
    /// twice gives the same names. It is added to <paramref name="taken"/>.</para>
    /// </summary>
    public static string SafeName(string original, ISet<string> taken)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(taken);
        const int MaxLength = 64;
        var sb = new StringBuilder();
        bool gap = false;
        foreach (char c in original.Trim())
        {
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_')
            {
                if (gap && sb.Length > 0) sb.Append('_');
                gap = false;
                sb.Append(c);
            }
            else gap = true;
        }
        string stem = sb.Length == 0 ? "layer" : sb.ToString();
        if (stem.Length > MaxLength) stem = stem[..MaxLength];
        if (NameProblem(stem) is not null) stem += "_";

        string candidate = stem;
        for (int n = 2; taken.Any(t => string.Equals(t, candidate, StringComparison.OrdinalIgnoreCase)); n++)
            candidate = stem + "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        taken.Add(candidate);
        return candidate;
    }

    /// <summary>
    /// M816 review (round 3): why <paramref name="segment"/> cannot be ONE folder or file name on Windows - the rule for a name that only has
    /// to be PATH-SAFE, where <see cref="NameProblem"/> is the stricter rule of the .fantome layout. A phrase that completes "it ...", or null.
    ///
    /// <list type="bullet">
    /// <item><description>empty;</description></item>
    /// <item><description>a character a file name cannot hold: <c>/</c>, <c>\</c>, <c>:</c>, a wildcard or a control character (every character
    /// <see cref="System.IO.Path.GetInvalidFileNameChars"/> names, and the C1 controls);</description></item>
    /// <item><description><c>.</c> or <c>..</c>, or any name that ends with a dot or a space (Windows drops them, so the folder made would
    /// not be the one named);</description></item>
    /// <item><description>a Windows device name, with or without an extension (<c>con</c>, <c>NUL.txt</c>, <c>com1.x</c>);</description></item>
    /// <item><description>longer than <paramref name="maxLength"/> characters.</description></item>
    /// </list>
    /// </summary>
    public static string? SegmentProblem(string segment, int maxLength)
    {
        if (string.IsNullOrEmpty(segment)) return "is empty";
        if (segment.IndexOfAny(UnusableChars) >= 0 || segment.Any(char.IsControl))
            return "has a character a file name cannot hold (a separator, a colon, a wildcard or a control character)";
        if (segment is "." or "..") return "is a dot name";
        if (segment[^1] is '.' or ' ') return "ends with a dot or a space, which Windows drops";
        int dot = segment.IndexOf('.');
        if (IsWindowsDeviceName((dot < 0 ? segment : segment[..dot]).TrimEnd(' '))) return "is a Windows device name (with or without an extension)";
        if (segment.Length > maxLength) return $"is longer than {maxLength} characters";
        return null;
    }

    private static readonly char[] UnusableChars = System.IO.Path.GetInvalidFileNameChars();

    /// <summary>
    /// M816 review (round 3): why <paramref name="name"/> cannot be the FOLDER of a layer in LTK Manager's workshop or in a build's output
    /// folder, or null. The rule for a destination a person or another tool chose, where <see cref="NameProblem"/> is the .fantome layout's:
    /// LTK Manager does not validate a layer name it reads (the M744 dialog let a layer be called "Particle Fix", and a send wrote it to
    /// <c>content/Particle Fix/</c>), so a name only has to be a single, safe folder name - <see cref="SegmentProblem"/> - of at most
    /// <see cref="MaxLayerNameLength"/> characters, and <c>base</c> exactly (another casing of it is a second spelling of base's folder).
    /// </summary>
    public static string? PathProblem(string name)
    {
        if (string.IsNullOrEmpty(name)) return "A layer needs a name.";
        if (name == Base) return null;
        if (IsBase(name)) return $"'{name}' is another casing of the base layer's name, so its folder would be a second spelling of base's.";
        return SegmentProblem(name, MaxLayerNameLength) is { } why ? $"'{Shown(name)}' cannot be a folder name: it {why}." : null;
    }

    /// <summary>A name for a message: cut to 40 characters, a control character shown as '?'.</summary>
    private static string Shown(string name)
    {
        var sb = new StringBuilder(Math.Min(name.Length, 43));
        foreach (char c in name.Length > 40 ? name[..40] : name) sb.Append(char.IsControl(c) ? '?' : c);
        return name.Length > 40 ? sb.Append("...").ToString() : sb.ToString();
    }

    /// <summary>
    /// M816 review: the folder below <paramref name="root"/> that a layer's files are written in - <c>root/&lt;layer&gt;</c> - once it is
    /// shown to be one.
    ///
    /// <para>A layer name comes from a package or a project file, and <c>Path.Combine(root, "..\..")</c> is the grandparent of
    /// <c>root</c> while <c>Path.Combine(root, @"C:\Temp\x")</c> is not below it at all. Writers delete and create below this folder,
    /// so it is checked twice: the name against <see cref="PathProblem"/>, and then the folder it resolves to against
    /// <paramref name="root"/>, so a name that reached a writer by a route that skipped the first check still cannot name a place
    /// outside. The second check is made for every name, whatever the first allows. Nothing is created here.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The name cannot be a folder name, or resolves to a place that is not strictly below
    /// <paramref name="root"/>.</exception>
    public static string LayerDirectory(string root, string layer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (layer is null) throw new InvalidOperationException("A layer needs a name.");
        if (PathProblem(layer) is { } problem)
            throw new InvalidOperationException($"Layer '{Shown(layer)}' cannot be written to a folder: {problem}");
        string rootFull = System.IO.Path.GetFullPath(root);
        string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(rootFull, layer));
        if (!IsStrictlyBelow(dir, rootFull))
            throw new InvalidOperationException($"refusing to write outside {rootFull}: layer '{Shown(layer)}' resolves to {dir}");
        return dir;
    }

    /// <summary>M816 review: whether <paramref name="path"/> lies inside <paramref name="root"/> and is not <paramref name="root"/> itself.
    /// Both are resolved first; the comparison ignores case, as the file system of the platform this runs on does.</summary>
    public static bool IsStrictlyBelow(string path, string root)
    {
        char[] separators = { System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar };
        string r = System.IO.Path.GetFullPath(root).TrimEnd(separators);
        string p = System.IO.Path.GetFullPath(path).TrimEnd(separators);
        return p.Length > r.Length && p.StartsWith(r + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// M816: the name a WAD is stored under when its project folder is called <paramref name="folderLeaf"/>: the leaf as it is when it
    /// already ends in a WAD extension <c>ltk_fantome</c> recognises (<c>.wad.client</c>, <c>.wad</c>, <c>.wad.mobile</c> - the importer
    /// keeps the latter two on the folder), else the leaf with <c>.wad.client</c>.
    /// </summary>
    public static string WadFileName(string folderLeaf) =>
        folderLeaf.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase)
        || folderLeaf.EndsWith(".wad", StringComparison.OrdinalIgnoreCase)
        || folderLeaf.EndsWith(".wad.mobile", StringComparison.OrdinalIgnoreCase)
            ? folderLeaf : folderLeaf + ".wad.client";

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

    /// <summary>
    /// M816: the names in a table file, in file order. <c>ltk_hashtable</c>'s <c>Hashtable::from_reader</c>: one name per line,
    /// blank lines skipped, CRLF tolerated.
    ///
    /// <para>Where the reader refuses the whole file - for a byte order mark, or a line outside the grammar - this keeps the
    /// names it can: an import has the package in hand and the good lines are still the author's names. The lines it could
    /// not keep are counted in <paramref name="rejected"/>, and a byte order mark is stripped.</para>
    /// </summary>
    public static IReadOnlyList<string> ReadNames(ReadOnlySpan<byte> utf8, out int rejected)
    {
        rejected = 0;
        if (utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF) utf8 = utf8[3..];

        var names = new List<string>();
        string text = Encoding.UTF8.GetString(utf8);
        int start = 0;
        while (start <= text.Length)
        {
            int end = text.IndexOf('\n', start);
            if (end < 0) end = text.Length;
            string line = text[start..end];
            if (line.EndsWith('\r')) line = line[..^1];
            if (line.Length > 0)
            {
                if (IsTableName(line)) names.Add(line);
                else rejected++;
            }
            start = end + 1;
        }
        return names;
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
        // M816: written with a Utf8JsonWriter rather than as a JsonObject, because an imported layer's GameData goes in as its own
        // TEXT (WriteRawValue) and a node cannot hold that. The tokens are the ones JsonObject.ToJsonString wrote for the same document
        // (it is built on this writer with these options), so a package without imported content is byte-identical to M814's.
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            MaxDepth = GameDataDocumentText.MaxDepth,
        }))
        {
            w.WriteStartObject();
            w.WriteString("Name", meta.Name);
            w.WriteString("Author", meta.Author);
            w.WriteString("Version", meta.Version);
            w.WriteString("Description", meta.Description);
            if (!string.IsNullOrWhiteSpace(meta.Heart)) w.WriteString("Heart", meta.Heart);
            if (!string.IsNullOrWhiteSpace(meta.Home)) w.WriteString("Home", meta.Home);

            // M816: ltk_fantome's FantomeInfo order puts License, Tags, Champions and Maps after Description and before Layers
            if (meta.License is { } license)
            {
                w.WritePropertyName("License");
                if (license.AsObject || !string.IsNullOrEmpty(license.Url))
                {
                    w.WriteStartObject();
                    w.WriteString("Name", license.Name);
                    if (!string.IsNullOrEmpty(license.Url)) w.WriteString("Url", license.Url);
                    w.WriteEndObject();
                }
                else w.WriteStringValue(license.Name);
            }
            WriteStrings(w, "Tags", meta.Tags);
            WriteStrings(w, "Champions", meta.Champions);
            WriteStrings(w, "Maps", meta.Maps);

            w.WriteStartObject("Layers");
            foreach (var layer in layers)
            {
                w.WriteStartObject(layer.Name);
                // FantomeLayerInfo's field order: GameData, Name, DisplayName, Priority, StringOverrides
                if (layer.ImportedGameData is { } imported)
                {
                    w.WritePropertyName("GameData");
                    GameDataDocumentText.Read(imported).WriteTo(w, layer.OwnModules);
                }
                else if (layer.GameData is not null)
                {
                    w.WritePropertyName("GameData");
                    layer.GameData.WriteTo(w);
                }
                w.WriteString("Name", layer.Name);
                if (!string.IsNullOrEmpty(layer.DisplayName)) w.WriteString("DisplayName", layer.DisplayName);
                w.WriteNumber("Priority", layer.Priority);
                if (layer.StringOverrides is { Count: > 0 } overrides)
                {
                    w.WritePropertyName("StringOverrides");
                    overrides.WriteTo(w);
                }
                w.WriteEndObject();
            }
            w.WriteEndObject();

            if (hashtable)
            {
                w.WriteStartArray("Hashtables");
                w.WriteStartObject();
                w.WriteString("Path", FantomeHashtables.HarvestedPath);
                w.WriteString("Category", "game");
                w.WriteString("Algorithm", "xxh64");
                w.WriteNumber("Bits", 64);
                w.WriteEndObject();
                w.WriteEndArray();
            }
            if (!string.IsNullOrWhiteSpace(meta.Generator)) w.WriteString("Generator", meta.Generator);
            w.WriteEndObject();
        }
        return Utf8.GetString(stream.ToArray());
    }

    private static void WriteStrings(Utf8JsonWriter w, string key, IReadOnlyList<string> values)
    {
        if (values.Count == 0) return;
        w.WriteStartArray(key);
        foreach (string v in values) w.WriteStringValue(v);
        w.WriteEndArray();
    }

    /// <summary>The archive entry a packed WAD is stored as.</summary>
    public static string WadEntryName(FantomeWad wad) =>
        FantomeLayers.WadDirectory(wad.Layer) + "/" + (wad.Name ?? System.IO.Path.GetFileName(wad.Path));

    /// <summary>M816: the archive entry of an override file: <c>META/game_data/&lt;layer&gt;/&lt;path&gt;</c>
    /// (<c>ltk_fantome::game_data_entry_name</c>).</summary>
    public static string OverrideEntryName(string layer, FantomeOverrideFile file) => "META/game_data/" + layer + "/" + file.Path;

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

        // M816: the files an imported layer's declarations name, and the package's own text files, must be there to be stored
        foreach (var layer in layers)
            foreach (var file in layer.OverrideFiles)
            {
                if (Projects.LtkProjectStore.PathProblem(file.Path) is { } why)
                    throw new InvalidOperationException($"The override file '{file.Path}' of layer '{layer.Name}' cannot be stored in a package: {why}.");
                if (!File.Exists(file.FilePath))
                    throw new InvalidOperationException($"The override file '{file.Path}' of layer '{layer.Name}' is missing from the project ({file.FilePath}).");
                if (!entryNames.Add(OverrideEntryName(layer.Name, file)))
                    throw new InvalidOperationException($"Two files would be stored as {OverrideEntryName(layer.Name, file)}.");
            }
        foreach (var (entryName, filePath) in meta.MetaFiles)
        {
            if (!File.Exists(filePath))
                throw new InvalidOperationException($"{entryName} is missing from the project ({filePath}).");
            if (!entryNames.Add(entryName))
                throw new InvalidOperationException($"Two files would be stored as {entryName}.");
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

            // M816: the package's README and license text, then each layer's override files below META/game_data/<layer>/
            foreach (var (entryName, filePath) in meta.MetaFiles)
                zip.CreateEntryFromFile(filePath, entryName, CompressionLevel.Optimal);
            foreach (var layer in layers)
                foreach (var file in layer.OverrideFiles)
                    zip.CreateEntryFromFile(file.FilePath, OverrideEntryName(layer.Name, file), CompressionLevel.Optimal);

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
