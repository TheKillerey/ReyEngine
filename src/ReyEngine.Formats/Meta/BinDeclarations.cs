using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Build;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using LtProp = LeagueToolkit.Core.Meta.BinTreeProperty;

namespace ReyEngine.Formats.Meta;

/// <summary>The plaintext a declaration spells hashes with. Every answer is checked against the hash it
/// names before it is used, so a stale or colliding table entry falls back to the hash form.</summary>
public interface IDeclarationNames
{
    string? Field(uint hash);
    string? Class(uint hash);
    string? Entry(uint hash);
    string? File(ulong hash);
}

/// <summary>
/// M814: a declared value before it is spelled. The diff builds these once and the two renderers - the
/// YAML manifest Send to LTK Manager writes and the JSON document a .fantome carries - read the same tree,
/// so the two cannot disagree about what a bin's edit says.
/// </summary>
public abstract record DeclValue;
public sealed record DeclNull : DeclValue;
public sealed record DeclBool(bool Value) : DeclValue;
/// <summary>An integer as its decimal digits. Never spelled with a point: the loader refuses
/// <c>1.0</c> for an integer field (<c>coerce.rs</c> reads an integer only from an integer).</summary>
public sealed record DeclInteger(string Digits) : DeclValue;
/// <summary>An f32 as <see cref="BinDeclarations.Float"/> spells it.</summary>
public sealed record DeclFloat(string Text) : DeclValue;
public sealed record DeclText(string Value) : DeclValue;
public sealed record DeclList(IReadOnlyList<DeclValue> Items) : DeclValue;
public sealed record DeclMap(IReadOnlyList<DeclEntry> Entries) : DeclValue;
/// <summary>A <c>pointer</c> or <c>embed</c> pin: the class and the fields it sets.</summary>
public sealed record DeclStruct(string Pin, string Class, IReadOnlyList<DeclEntry> Fields) : DeclValue;

/// <summary>
/// M815: the value another entry of the INSTALLED game holds, named instead of copied - <c>{"ref": "&lt;entry&gt;:&lt;path&gt;"}</c>
/// in <c>ltk_game_data</c> (<c>value.rs</c> <c>reference()</c>, <c>reference.rs</c>). LTK reads it from the game's own
/// copy of the entry every time it applies a layer, so a Riot patch that changes the value changes what the mod
/// applies: that is the whole point, and the reason a map skin switch is declared this way.
///
/// <para><b>What a reference can and cannot be.</b> Anywhere a value is: a property edit's value, a list element,
/// a map VALUE, a struct field. Never a map KEY (keys are text, <see cref="DeclMap"/> holds strings) and never an
/// operand of a <c>-</c> edit (<c>coerce.rs</c> reads the operands of a map removal as key text, so a reference there is a
/// <c>KindMismatch</c>): the renderers refuse that with <see cref="BinDeclarations.Refused"/>. It resolves against the
/// UNMODIFIED game - the first game chunk that declares <see cref="Entry"/> (<c>game_data.rs</c> <c>read_referenced_entry</c>) -
/// never against the module's own earlier edits, whatever the module order.</para>
///
/// <para><b>Spelling.</b> <see cref="Entry"/> is a bin object path or <c>0x</c> and eight hex digits; LTK splits the text at the FIRST
/// <c>:</c>, so the entry holds none and the path may. The pair is checked here the way <c>Reference::parse</c> checks it.</para>
/// </summary>
public sealed record DeclRef : DeclValue
{
    public string Entry { get; }
    public DeclPath Path { get; }

    public DeclRef(string entry, DeclPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (string.IsNullOrEmpty(entry) || entry.Contains(':') || entry.Any(char.IsControl))
            throw new ArgumentException($"A reference's entry is a non-empty object path or hash form without ':' (got '{entry}').", nameof(entry));
        if (!entry.Contains('/') && !BinDeclarations.IsHashForm(entry))
            throw new ArgumentException($"A reference's entry '{entry}' carries no '/' and is not 0x and eight hex digits; spell it as its hash.", nameof(entry));
        Entry = entry;
        Path = path;
    }

    /// <summary>The text of the <c>ref</c> value: <c>entry:path</c>.</summary>
    public string Text => Entry + ":" + Path.Text;
}

/// <summary>
/// M815: a property path as <c>ltk_meta</c> parses it (<c>path/parse.rs</c>): segments joined by <c>.</c>, each a <c>name</c>, a
/// <c>name[index]</c> or a <c>name{key}</c>. A name is anything but <c>.[]{}()</c> and controls; <c>0x</c> and eight hex digits
/// is the field hash itself (<c>address.rs</c>). An index is decimal with no leading zero (a leading zero reads as octal there).
/// A <c>{key}</c> is the decimal value of a hash, integer or file-hash key and never <c>{0x...}</c> (<c>path/resolve.rs</c>
/// <c>key_as</c>). That is a different rule from the keys of a map VALUE or of a <c>-</c> list, which are <c>"0x%08x"</c>
/// or a name and for which a decimal string is a name to hash (<c>coerce.rs</c> <c>key</c>) - the two spellings of one
/// key in a single module are therefore different strings, as in <c>"-items": ["0x4241132a"]</c> next to
/// <c>items{1111561002}</c>.
///
/// <para>Only the shapes a declaration this tool writes needs are built: no text keys. A path with two subscripts on one
/// segment is refused, as the parser refuses it.</para>
/// </summary>
public sealed record DeclPath
{
    public string Text { get; }
    private readonly bool _subscripted;

    private DeclPath(string text, bool subscripted) { Text = text; _subscripted = subscripted; }

    /// <summary>A one-segment path.</summary>
    public static DeclPath Field(string name) => new(CheckName(name), false);

    /// <summary>The path with one more segment under it.</summary>
    public DeclPath Then(string name) => new(Text + "." + CheckName(name), false);

    /// <summary>The last segment subscripted <c>[index]</c>.</summary>
    public DeclPath At(int index)
    {
        if (_subscripted) throw new InvalidOperationException($"'{Text}' already carries a subscript on its last segment.");
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index), "A list index is not negative.");
        return new(Text + "[" + index.ToString(CultureInfo.InvariantCulture) + "]", true);
    }

    /// <summary>The last segment subscripted <c>{key}</c> with the key's decimal value (a hash, a file hash or an integer).</summary>
    public DeclPath Keyed(uint key) => Keyed((ulong)key);

    /// <inheritdoc cref="Keyed(uint)"/>
    public DeclPath Keyed(ulong key)
    {
        if (_subscripted) throw new InvalidOperationException($"'{Text}' already carries a subscript on its last segment.");
        return new(Text + "{" + key.ToString(CultureInfo.InvariantCulture) + "}", true);
    }

    /// <inheritdoc cref="Keyed(uint)"/>
    public DeclPath Keyed(long key)
    {
        if (_subscripted) throw new InvalidOperationException($"'{Text}' already carries a subscript on its last segment.");
        return new(Text + "{" + key.ToString(CultureInfo.InvariantCulture) + "}", true);
    }

    public override string ToString() => Text;

    private static string CheckName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Any(c => c is '.' or '[' or ']' or '{' or '}' or '(' or ')' || char.IsControl(c)))
            throw new ArgumentException($"'{name}' is not a path segment name (non-empty, none of . [ ] {{ }} ( ) and no controls).", nameof(name));
        if (name[0] is '+' or '-')
            throw new ArgumentException($"'{name}' starts with a sign, which a property key reads as add or remove.", nameof(name));
        return name;
    }
}

/// <summary>One key with its value: a struct field, a map entry or a property edit.</summary>
public sealed record DeclEntry(string Key, DeclValue Value)
{
    /// <summary>M815: <c>+path</c>, which appends the elements of a list or adds (or replaces) the entries of a map.</summary>
    public static DeclEntry Add(string path, DeclValue value) => new("+" + path, value);

    /// <summary>M815: <c>-path</c>, which removes the listed elements of a list or the listed keys of a map. Its operand is a
    /// list of literals: a <see cref="DeclRef"/> in it is refused when the module is rendered.</summary>
    public static DeclEntry Remove(string path, DeclValue value) => new("-" + path, value);
}

/// <summary>The edits of one existing object: its entry name and its dotted property keys.</summary>
public sealed record DeclaredBody(string Entry, IReadOnlyList<DeclEntry> Edits);

/// <summary>An object the project added (<see cref="Class"/> and <see cref="Set"/>) or removed
/// (<see cref="Class"/> null).</summary>
public sealed record DeclaredObject(string Name, string? Class, IReadOnlyList<DeclEntry> Set)
{
    public bool Remove => Class is null;
}

/// <summary>M814: one bin's declaration as a model. Added objects come before removed ones, as the
/// manifest has always listed them.</summary>
public sealed record DeclaredEdit(
    IReadOnlyList<string> AddLinks,
    IReadOnlyList<string> DropLinks,
    IReadOnlyList<DeclaredBody> Bodies,
    IReadOnlyList<DeclaredObject> Objects)
{
    public bool IsEmpty => AddLinks.Count == 0 && DropLinks.Count == 0 && Bodies.Count == 0 && Objects.Count == 0;
}

/// <summary>One bin's outcome: its module text, "unchanged", or why it has to ship whole.</summary>
/// <param name="Target">The chunk, as the module's <c>target</c> spells it.</param>
/// <param name="Module">The module's YAML lines, indented for <c>modules:</c>; null when nothing to declare.</param>
/// <param name="WhyNot">Why this bin cannot be declared and ships as a file; null otherwise.</param>
public sealed record DeclaredChunk(
    string Target,
    string? Module,
    string? WhyNot,
    int Properties = 0,
    int ObjectsAdded = 0,
    int ObjectsRemoved = 0,
    int LinksChanged = 0)
{
    public bool Declared => Module is not null;
    public bool Unchanged => Module is null && WhyNot is null;

    /// <summary>M814: the declaration as a model, which <see cref="BinDeclarations.GameDataDocument"/>
    /// reads. Set exactly when <see cref="Declared"/>; <see cref="Module"/> is rendered from the same value.</summary>
    public DeclaredEdit? Edit { get; init; }

    /// <summary>M814: what the module is called in a JSON document - the project path of the bin. Null
    /// falls back to <see cref="Target"/>.</summary>
    public string? Label { get; init; }

    /// <summary>M815: how many values of this module are references to the installed game (<see cref="DeclRef"/>)
    /// instead of values. Zero for a module the diff built.</summary>
    public int References => Edit is { } edit ? BinDeclarations.CountReferences(edit) : 0;
}

/// <summary>
/// M757: a project's copy of a game bin, written as LTK Manager game-data declarations against the game's
/// copy - league-mod's <c>game_data.yaml</c> (declarations version 1, <c>ltk_game_data</c> 0.6), which
/// LTK Manager 1.21+ applies over the INSTALLED patch's bin at every overlay build. A mod that declares its
/// edits ships no copy of the bin, so Riot's later changes to every key it does not name survive.
///
/// <para><b>One <c>target</c> module per bin</b>, not <c>entries</c>: an entries module edits an object in
/// every chunk that declares it, and this diff is of one chunk. In the module, each changed object is an
/// entry body of dotted property paths; objects the project added are <c>objects: {class, set}</c>,
/// objects it removed <c>remove: true</c>, and changed dependencies <c>links</c> / <c>-links</c>.</para>
///
/// <para><b>Per key where the format allows, whole value where it does not.</b> A changed field inside a
/// struct of the same class is its own dotted key; a container, a map, an option, a struct of another
/// class, or a struct a field was removed from is set whole - the format lowers container edits to whole
/// replacements anyway (league-mod D20), and a whole value freezes that list at the project's version,
/// which is ADR-0042's stated cost.</para>
///
/// <para><b>What cannot be declared ships the bin, with the reason.</b> No declaration removes a property
/// (ADR-0042), an object whose class changed would be created over a live name (<c>ObjectExists</c>), a
/// PTCH override is not a PROP bin, a lossy parse would declare what was lost, and a value of kind
/// <c>none</c> or a map key of a non-scalar kind does not render (league-mod section 6). M814 adds an embedded
/// struct whose class changed: <c>ltk_game_data</c> refuses an embed pin of another class (<c>PinMismatch</c>), so
/// the edit would be skipped on the player's machine; a pointer may change class and is set whole.</para>
///
/// <para><b>Values render by league-mod's table</b>, so they coerce back to the same bits: f32 by the
/// shortest round-trip spelling, vectors and mtx44 as lists in file order (ltk_meta reads mtx44 row-major,
/// the order <c>Matrix4x4</c> holds it), rgba as four integers, hash/link/file by name only where the name
/// hashes back, pointers and embeds as struct tags. No type pins: LTK Manager types every key with the
/// installed patch's schema (league-mod D26).</para>
///
/// <para>Values are written in flow style with every string double-quoted, the one YAML form with no
/// indentation or plain-scalar ambiguity; the file stays YAML so LTK Manager's in-place editor, which
/// writes <c>game_data.yaml</c>, keeps editing it.</para>
///
/// <para><b>M814: the same diff, a second renderer.</b> A .fantome carries a layer's declarations as a JSON
/// document inside <c>META/info.json</c> (<c>Layers.&lt;name&gt;.GameData</c>), not as a file.
/// <see cref="Convert"/> builds one <see cref="DeclaredEdit"/> model; the manifest text
/// (<see cref="DeclaredChunk.Module"/>) and <see cref="GameDataDocument"/> are both rendered from it, so there
/// is a single place that decides what a bin's edit is. The JSON form is <c>ltk_game_data</c>'s
/// <c>DeclarationDocument</c> (<c>document.rs</c>): <c>{version, modules:[{name?, target, edits:[body],
/// origin}]}</c> with struct pins as one-key <c>{"embed": {class, set}}</c> mappings (the YAML tag form is a
/// spelling of the same thing, <c>value.rs</c>). The optional <c>name</c> is left out unless
/// <see cref="FantomeLayers.ModuleNames"/> says otherwise, and it does not: <c>ltk_game_data</c> 0.6 refuses a layer
/// whose modules carry one (see <see cref="GameDataDocument"/>). Checked outside the suite the strong way: league-mod's own
/// <c>ltk_fantome</c> reads the export and <c>ltk_game_data</c> 0.8 applies each layer over the untouched
/// bin - see the M814 commit for the corpus and counts.</para>
///
/// <para><b>M815: references.</b> A value may be <see cref="DeclRef"/> - <c>{"ref": "&lt;entry&gt;:&lt;path&gt;"}</c> in both
/// renderers - and a property key may carry the <c>+</c> / <c>-</c> sign of an add or a removal. The diff never writes
/// either; <see cref="MapSkinDeclarations"/> does, to declare a forced map skin as "this slot loads that slot's value" and
/// not as the value of the day. Both spellings are the ones <c>ltk_game_data</c> reads from 0.4.0, the crate of LTK Manager v1.20.0.
/// Measured with the 0.4.0 crate (M815): it parses and applies a switch's documents - target modules, references as values and as map
/// values, <c>-items</c> / <c>+items</c>, <c>origin</c> - and the result equals the project's bins on Map11 and Map12. What 0.4.0 refuses
/// is M814's <c>objects</c> binding, which creates or removes an object (<c>unsupported binding `objects`</c>, from 0.6.0 = LTK Manager
/// v1.21.0), and a refused binding refuses the whole layer. So the declarations as a whole need LTK Manager 1.21; the references
/// alone would not.</para>
/// </summary>
public static class BinDeclarations
{
    public const string FileName = "game_data.yaml";

    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex PlainKey = new(@"^[A-Za-z_][A-Za-z0-9_]*(?:[./][A-Za-z0-9_]+)*$", RegexOptions.Compiled);
    private static readonly Regex TagClass = new(@"^[A-Za-z0-9_\-.:/]+$", RegexOptions.Compiled);
    // `0x` and exactly 8 / 16 hex digits: the hash forms ltk_game_data reads as hashes (coerce.rs hex32/hex)
    private static readonly Regex HashForm32 = new("^0[xX][0-9A-Fa-f]{8}$", RegexOptions.Compiled);
    private static readonly Regex HashForm64 = new("^0[xX][0-9A-Fa-f]{16}$", RegexOptions.Compiled);

    /// <summary>Words a key must not spell (league-mod section 4: binding keywords compare case-sensitively).</summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal) { "overrides", "objects", "links", "+links", "-links" };

    /// <summary>Type names and <c>ref</c>: a one-key mapping keyed by one of these reads back as a pin or a
    /// reference, so a map with that single key does not render (league-mod section 6).</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "bool", "i8", "i16", "i32", "i64", "u8", "u16", "u32", "u64", "f32", "vec2", "vec3", "vec4", "mtx44",
        "rgba", "string", "hash", "file", "link", "flag", "option", "pointer", "embed", "ref",
    };

    private static readonly IReadOnlyList<string> YamlWords = new[] { "true", "false", "null", "yes", "no", "on", "off", "y", "n", "~" };

    /// <summary>Why a bin cannot be declared; caught by <see cref="Convert"/>, which ships the bin whole.</summary>
    public sealed class Refused(string why) : Exception(why);

    /// <summary>M815: a lowercase <c>0x</c> and exactly eight hex digits - the one spelling <c>ltk_game_data</c> reads as a hash
    /// and not as a name (<c>coerce.rs</c> <c>hex32</c> strips <c>0x</c> only; <c>0X</c> would be hashed as text).</summary>
    internal static bool IsHashForm(string text) =>
        text.Length == 10 && text.StartsWith("0x", StringComparison.Ordinal) && text.Skip(2).All(Uri.IsHexDigit);

    /// <summary>M815: the references a declared edit holds, counted in every value it carries.</summary>
    internal static int CountReferences(DeclaredEdit edit)
    {
        int n = 0;
        foreach (var body in edit.Bodies) foreach (var e in body.Edits) n += CountReferences(e.Value);
        foreach (var o in edit.Objects) foreach (var e in o.Set) n += CountReferences(e.Value);
        return n;
    }

    private static int CountReferences(DeclValue v) => v switch
    {
        DeclRef => 1,
        DeclList l => l.Items.Sum(CountReferences),
        DeclMap m => m.Entries.Sum(e => CountReferences(e.Value)),
        DeclStruct s => s.Fields.Sum(e => CountReferences(e.Value)),
        _ => 0,
    };

    /// <summary>M815: a <c>-</c> edit removes by value or by key, and <c>ltk_game_data</c> reads the keys of a map removal
    /// as text (<c>entries.rs</c> <c>remove</c>: anything but a string, integer or bool is <c>KindMismatch</c>), so a
    /// reference among its operands would be skipped on every machine. Refused here, where the module is rendered.</summary>
    private static void CheckEdit(string entry, DeclEntry edit)
    {
        if (edit.Key.StartsWith('-') && CountReferences(edit.Value) > 0)
            throw new Refused($"{entry} removes with a reference ('{edit.Key}'), which league-mod refuses as the operand of a removal");
    }

    /// <summary>M815: the spelling an entry takes as a body key or as the entry of a reference - its name where the name
    /// hashes back to it and carries a slash, else its hash.</summary>
    internal static string EntryKey(uint hash, IDeclarationNames names) => new Writer(names).EntryName(hash, needsSlash: true);

    /// <summary>M815: the spelling a field takes in a property key or a path - its name where the name is an identifier that
    /// hashes back to it, else its hash.</summary>
    internal static string FieldKey(uint hash, IDeclarationNames names) => new Writer(names).FieldName(hash, 0);

    /// <summary>The chunk's target spelling: its path when <paramref name="relPath"/> hashes to
    /// <paramref name="chunkHash"/>, else the 16 hexadecimal digits of the hash (league-mod section 4).
    /// The path is lowercased with ASCII rules by the loader, so a path holding anything else is spelled
    /// as its hash.</summary>
    public static string TargetOf(string relPath, ulong chunkHash)
    {
        string path = relPath.Replace('\\', '/');
        return IsAscii(path) && HashAlgorithms.WadPath(path) == chunkHash ? path.ToLowerInvariant() : chunkHash.ToString("x16");
    }

    /// <summary>Diff <paramref name="mod"/> against <paramref name="riot"/>, both whole bin files.</summary>
    public static DeclaredChunk Convert(string target, byte[] riot, byte[] mod, IDeclarationNames names)
    {
        if (IsPatch(riot) || IsPatch(mod)) return new(target, null, "a PTCH override bin, not a PROP bin");
        BinTree r, m;
        try
        {
            r = SafeBinTree.Parse(riot, out var ri);
            if (ri.Count > 0) return new(target, null, $"the game's copy parses lossily ({ri.Count} issue(s))");
            m = SafeBinTree.Parse(mod, out var mi);
            if (mi.Count > 0) return new(target, null, $"the project's copy parses lossily ({mi.Count} issue(s))");
        }
        catch (Exception ex) { return new(target, null, $"did not parse: {ex.Message}"); }

        try { return Diff(target, r, m, names); }
        catch (Refused ex) { return new(target, null, ex.Message); }
    }

    /// <summary>
    /// M815: the diff of two bins that are already parsed. <paramref name="baseline"/> is what the declaration is applied
    /// ON TOP OF - the game's bin, or the game's bin with the edits an earlier module of the same target already made (the
    /// map skin switch, declared by reference). The caller has checked that both parsed without loss.
    /// </summary>
    internal static DeclaredChunk ConvertTrees(string target, BinTree baseline, BinTree mod, IDeclarationNames names)
    {
        try { return Diff(target, baseline, mod, names); }
        catch (Refused ex) { return new(target, null, ex.Message); }
    }

    /// <summary>
    /// M815: a module built from a model instead of from a diff - the edits of the map skin switch, written by reference.
    /// The YAML text and the model are rendered together, as <see cref="Convert"/> does, so a module that cannot be
    /// spelled (a reference where none is allowed) is refused here and not later when the document is written.
    /// </summary>
    /// <param name="properties">How many property edits the module holds, for the plan's counts.</param>
    /// <exception cref="Refused">A value this writer cannot spell, or a reference as the operand of a removal.</exception>
    public static DeclaredChunk FromModel(string target, DeclaredEdit edit, int properties)
    {
        if (edit.IsEmpty) return new(target, null, null);
        string yaml = Yaml.Module(target, edit);          // the signed-key rule is checked in here
        Json.Body(edit);                                   // and the JSON form is built once now, so it cannot fail at export
        return new(target, yaml, null, properties) { Edit = edit };
    }

    private static bool IsPatch(byte[] b) => b.Length >= 4 && b[0] == (byte)'P' && b[1] == (byte)'T' && b[2] == (byte)'C' && b[3] == (byte)'H';

    private static DeclaredChunk Diff(string target, BinTree riot, BinTree mod, IDeclarationNames names)
    {
        var w = new Writer(names);
        int properties = 0, added = 0, removed = 0;

        // dependencies: compared ASCII case-insensitively, as the format removes and de-duplicates them
        var riotLinks = riot.Dependencies.Select(d => d.ToLowerInvariant()).ToHashSet();
        var modLinks = mod.Dependencies.Select(d => d.ToLowerInvariant()).ToHashSet();
        var addLinks = mod.Dependencies.Where(d => !riotLinks.Contains(d.ToLowerInvariant()))
            .DistinctBy(d => d.ToLowerInvariant()).ToList();
        var dropLinks = riot.Dependencies.Where(d => !modLinks.Contains(d.ToLowerInvariant()))
            .DistinctBy(d => d.ToLowerInvariant()).ToList();

        // changed objects, in the project's order
        var bodies = new List<DeclaredBody>();
        var objects = new List<DeclaredObject>();
        var spelled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (hash, mo) in mod.Objects)
        {
            if (!riot.Objects.TryGetValue(hash, out var ro))
            {
                string name = w.EntryName(hash, needsSlash: false);
                objects.Add(new DeclaredObject(name, w.ClassName(mo.ClassHash), w.Fields(mo.Properties.Values, mo.ClassHash)));
                added++;
                continue;
            }
            if (ro.ClassHash != mo.ClassHash)
                throw new Refused($"{w.EntryName(hash, false)} changed class, which no declaration can express");
            if (BinPropEquality.ObjectsEqual(ro, mo)) continue;

            var edits = new List<DeclEntry>();
            DiffProps("", ro.ClassHash, ro.Properties, mo.Properties, topLevel: true, edits, w, w.EntryName(hash, false));
            if (edits.Count == 0) continue;
            bodies.Add(new DeclaredBody(w.EntryName(hash, needsSlash: true), edits));
            properties += edits.Count;
        }
        foreach (var (hash, _) in riot.Objects)
        {
            if (mod.Objects.ContainsKey(hash)) continue;
            objects.Add(new DeclaredObject(w.EntryName(hash, needsSlash: false), null, Array.Empty<DeclEntry>()));
            removed++;
        }

        // one mapping holds one value per key: two entries or objects spelling alike would be a duplicate key
        foreach (var body in bodies)
            if (!spelled.Add(body.Entry)) throw new Refused($"two entries spell the key '{body.Entry}'");
        spelled.Clear();
        foreach (var obj in objects)
            if (!spelled.Add(obj.Name)) throw new Refused($"two objects spell the key '{obj.Name}'");

        var edit = new DeclaredEdit(addLinks, dropLinks, bodies, objects);
        if (edit.IsEmpty) return new(target, null, null);
        return new(target, Yaml.Module(target, edit), null, properties, added, removed, addLinks.Count + dropLinks.Count)
        {
            Edit = edit,
        };
    }

    private static void DiffProps(string prefix, uint cls, IReadOnlyDictionary<uint, LtProp> riot,
        IReadOnlyDictionary<uint, LtProp> mod, bool topLevel, List<DeclEntry> edits, Writer w, string entry)
    {
        foreach (var (hash, mp) in mod)
        {
            string path = prefix.Length == 0 ? w.FieldName(hash, cls) : prefix + "." + w.FieldName(hash, cls);
            if (!riot.TryGetValue(hash, out var rp)) { edits.Add(new DeclEntry(path, w.Value(mp))); continue; }
            if (BinPropEquality.PropsEqual(rp, mp)) continue;
            // M814: ltk_game_data refuses an embed pin whose class is not the class of the value it replaces
            // (coerce.rs: PinMismatch) - the edit would be skipped with a diagnostic and the field left as the game
            // has it. A pointer may change class; an embedded struct may not, so the bin ships whole.
            if (rp is BinTreeEmbedded re && mp is BinTreeEmbedded me && re.ClassHash != me.ClassHash)
                throw new Refused($"{entry} changes the class of the embedded struct {path}, which league-mod refuses (an embed pin cannot change class)");
            if (rp is BinTreeStruct rs && mp is BinTreeStruct ms && rp.GetType() == mp.GetType()
                && rs.ClassHash == ms.ClassHash && rs.ClassHash != 0
                && rs.Properties.Keys.All(ms.Properties.ContainsKey))
            {
                // the same struct with fields changed or added: one key per field
                DiffProps(path, ms.ClassHash, rs.Properties, ms.Properties, topLevel: false, edits, w, entry);
                continue;
            }
            edits.Add(new DeclEntry(path, w.Value(mp)));   // a different struct, a struct that lost a field, a container
        }
        if (!topLevel) return;   // a nested removal was set whole above
        foreach (var hash in riot.Keys)
            if (!mod.ContainsKey(hash))
                throw new Refused($"{entry} removes {w.FieldName(hash, cls)}, and no declaration removes a property");
    }

    /// <summary>The manifest for a layer: every declared module, in the order given.</summary>
    public static string Manifest(IEnumerable<DeclaredChunk> chunks) => Manifest(chunks, null);

    /// <summary>
    /// M816: the manifest for a layer that also holds declarations an import stored. The imported modules come FIRST, in the order
    /// the package declared them, then ReyEngine's own - the order <see cref="FantomeExporter"/> writes them in a .fantome.
    ///
    /// <para><b>Imported modules are written as JSON, in YAML flow style, one module to a line.</b> YAML 1.2 is a superset of JSON, so
    /// the text of the module the package held is already a YAML mapping, and writing it as one means no value is re-spelled: an
    /// integer stays an integer, a string a string (a JSON string is always a double-quoted YAML string, which no YAML reading can
    /// take for another type), the order of the keys is the author's. The one change is that the module's <c>origin</c> is left
    /// out - a manifest module has no <c>origin</c> (the loader works it out from the file and the module's position), and a key
    /// that is not a manifest key would be read as an entry name and refuse the layer. Checked against league-mod's own loader
    /// (<c>load_declarations</c>) on Crauzer's 13 modules: the same declarations as the document gives.</para>
    ///
    /// <para>A module's <c>name</c> is kept. That is the author's own spelling; an LTK Manager built on <c>ltk_game_data</c> 0.6
    /// refuses it, as it refuses the package this was imported from.</para>
    /// </summary>
    /// <param name="imported">The modules of the stored document, in order. Null or empty is the plain manifest.</param>
    public static string Manifest(IEnumerable<DeclaredChunk> chunks, IReadOnlyList<GameDataModuleText>? imported)
    {
        var sb = new StringBuilder();
        sb.Append("# Written by ReyEngine: each module is one bin's changes against the game's copy at the time\n");
        sb.Append("# of sending. LTK Manager applies them over the installed patch's bin at every build.\n");
        if (imported is { Count: > 0 })
            sb.Append("# The first ").Append(imported.Count).Append(" module(s) are declarations imported from a .fantome, kept as its author wrote\n")
              .Append("# them (one JSON mapping per line); the modules after them are this project's own.\n");
        var own = chunks.Where(c => c.Module is not null).ToList();
        // a manifest of no modules is `modules: []`: a bare `modules:` is YAML null, which is not a list (review; league-mod's loader reads
        // the null as an empty list - measured - and the explicit list is the one spelling no reader can take for anything else)
        if ((imported?.Count ?? 0) + own.Count == 0) return sb.Append("version: 1\nmodules: []\n").ToString();
        sb.Append("version: 1\nmodules:\n");
        if (imported is not null)
            foreach (var m in imported) sb.Append("  - ").Append(FlowModule(m.Text)).Append('\n');
        foreach (var c in own) sb.Append(c.Module);
        return sb.ToString();
    }

    /// <summary>
    /// M816: <paramref name="own"/> (layer to manifest text, as the plan renders it) with a manifest added for every layer whose
    /// declarations are only imported ones - a layer this project declares nothing for still carries what its package declared.
    /// </summary>
    public static Dictionary<string, string> WithImported(IReadOnlyDictionary<string, string> own, IEnumerable<ImportedLayerData> imported)
    {
        var all = new Dictionary<string, string>(own, StringComparer.OrdinalIgnoreCase);
        foreach (var data in imported)
            if (!all.ContainsKey(data.Layer))
                all[data.Layer] = Manifest(Array.Empty<DeclaredChunk>(), data.Modules);
        return all;
    }

    /// <summary>M816: a document module as a manifest module - its JSON on one line, without <c>origin</c>, every other token as the
    /// package spelled it (<see cref="GameDataDocumentText.ManifestModule"/>).</summary>
    internal static string FlowModule(string moduleJson) => GameDataDocumentText.ManifestModule(moduleJson);

    // ------------------------------------------------------------------ the JSON document (M814)

    /// <summary>
    /// M814: a layer's declarations as the JSON document a .fantome stores under
    /// <c>Layers.&lt;name&gt;.GameData</c> - <c>ltk_game_data</c>'s <c>DeclarationDocument</c>
    /// (<c>document.rs:147-190</c>). Every declared chunk is one <c>target</c> module with one edit (the
    /// compact binding body), numbered in the order given, with the <c>origin</c> the document requires
    /// (<c>manifest</c> is a label for diagnostics; no such file exists in the archive).
    ///
    /// <para>The body key order is <c>Bindings</c>' serialisation order - <c>objects</c>, <c>links</c>,
    /// <c>-links</c>, then the entries - so league-mod parsing and rewriting this document gives it back
    /// unchanged. A chunk that is not declared has no module, and a module never holds an empty edit or an
    /// empty body.</para>
    ///
    /// <para><paramref name="firstModuleIndex"/> (M816): where <c>origin.module</c> starts counting. A layer that holds imported
    /// modules ahead of these numbers them from the first free place, so the origins of the whole document are 0, 1, 2, ...
    /// as a loader would number a manifest of the same modules.</para>
    ///
    /// <para><paramref name="moduleNames"/> (default <see cref="FantomeLayers.ModuleNames"/>, which is
    /// <c>false</c>): a module <c>name</c> exists from <c>ltk_game_data</c> 0.7, and <c>Module</c> is
    /// <c>deny_unknown_fields</c>, so 0.6.0 refuses the whole layer when one module carries a name - measured
    /// against the 0.6.0 crate in M814. LTK Manager v1.21.0 pins 0.6.0 (<c>Cargo.lock</c>); the 1.25.0
    /// installed on the development machine bundles 0.8.0. The name carries no meaning for loading or
    /// applying - it only labels the module in a manager's declarations view - so the document leaves it out
    /// until no manager that still pins 0.6 is worth supporting. The renderer keeps the support: with the
    /// flag on, a module is labelled with the project path of its bin.</para>
    /// </summary>
    public static JsonObject GameDataDocument(IEnumerable<DeclaredChunk> chunks, bool moduleNames = FantomeLayers.ModuleNames, int firstModuleIndex = 0)
    {
        var modules = new JsonArray();
        int index = firstModuleIndex;
        foreach (var c in chunks)
        {
            if (c.Edit is not { } edit) continue;
            var module = new JsonObject();
            if (moduleNames) module["name"] = c.Label is { Length: > 0 } label ? label : c.Target;
            module["target"] = c.Target;
            module["edits"] = new JsonArray(Json.Body(edit));
            module["origin"] = new JsonObject
            {
                ["manifest"] = FileName,
                ["source"] = null,
                ["module"] = index,
            };
            modules.Add(module);
            index++;
        }
        return new JsonObject { ["version"] = 1, ["modules"] = modules };
    }

    // ------------------------------------------------------------------ spelling

    private static bool IsAscii(string s)
    {
        foreach (char c in s) if (c > 0x7f) return false;
        return true;
    }

    /// <summary>A mapping key: plain when it is a dotted identifier path and no YAML word, else quoted.</summary>
    private static string Key(string key) =>
        PlainKey.IsMatch(key) && !YamlWords.Contains(key, StringComparer.OrdinalIgnoreCase) ? key : Quote(key);

    /// <summary>A double-quoted YAML string, escaped as JSON escapes it (a JSON string is a YAML string).</summary>
    public static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20 || c == 0x7f) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>An f32 by the shortest spelling that rounds to the same single-precision bits. A NaN or an
    /// infinity has no spelling: league-mod's loader rejects a non-finite float outright
    /// (<c>reject_non_finite_typeless_float</c>, measured), so a bin holding one ships whole.
    ///
    /// <para>M814: a whole-number spelling must be the whole number it stands for. .NET prints
    /// <c>123456792f</c> as <c>123456790</c>, which the loader reads as an INTEGER and refuses for an f32
    /// (<c>single()</c> accepts an integer only when the f32 represents it exactly - measured to skip the
    /// property), so such a spelling gets a <c>.0</c> and reads as the float it is.</para></summary>
    public static string Float(float f)
    {
        if (!float.IsFinite(f))
            throw new Refused($"a non-finite f32 ({f.ToString(CultureInfo.InvariantCulture)}), which the declaration loader rejects");
        if (f == 0f && float.IsNegative(f)) return "-0.0";   // "-0" would read as the integer zero and lose the sign bit
        string text = f.ToString("R", CultureInfo.InvariantCulture);
        bool wholeLooking = text.AsSpan().IndexOfAny('.', 'E') < 0;
        return wholeLooking && (double)f != double.Parse(text, CultureInfo.InvariantCulture) ? text + ".0" : text;
    }

    /// <summary>The model builder: reads bin properties and spells every name, so a refusal is decided once
    /// for both renderers.</summary>
    private sealed class Writer(IDeclarationNames names)
    {
        /// <summary>A plaintext the loader hashes the way <see cref="HashAlgorithms.Fnv1a"/> does: ASCII,
        /// and not a spelling the loader reads as a hash instead of a name.</summary>
        private static bool Plain32(string n) => n.Length > 0 && IsAscii(n) && !HashForm32.IsMatch(n);

        public string FieldName(uint hash, uint cls)
        {
            string? n = names.Field(hash);
            return n is not null && Identifier.IsMatch(n) && !Keywords.Contains(n) && HashAlgorithms.Fnv1a(n) == hash
                ? n : $"0x{hash:x8}";
        }

        public string ClassName(uint hash)
        {
            string? n = names.Class(hash);
            return n is not null && Plain32(n) && HashAlgorithms.Fnv1a(n) == hash ? n : $"0x{hash:x8}";
        }

        /// <summary>An entry name. At a target body's root it must carry a slash or be hash-form
        /// (league-mod D22), so a known name without one is spelled as its hash there.</summary>
        public string EntryName(uint hash, bool needsSlash)
        {
            string? n = names.Entry(hash);
            bool ok = n is not null && Plain32(n) && HashAlgorithms.Fnv1a(n) == hash && !Keywords.Contains(n)
                      && (!needsSlash || n.Contains('/'));
            return ok ? n! : $"0x{hash:x8}";
        }

        private string Hash32(uint h, string? name) =>
            name is not null && Plain32(name) && HashAlgorithms.Fnv1a(name) == h ? name : $"0x{h:x8}";

        private string File64(ulong h)
        {
            string? n = names.File(h);
            return n is not null && n.Length > 0 && IsAscii(n) && !HashForm64.IsMatch(n) && HashAlgorithms.WadPath(n) == h
                ? n : $"0x{h:x16}";
        }

        /// <summary>A struct's fields as keyed values, one key per field.</summary>
        public IReadOnlyList<DeclEntry> Fields(IEnumerable<LtProp> props, uint cls)
        {
            var fields = new List<DeclEntry>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in props)
            {
                string key = FieldName(p.NameHash, cls);
                if (!keys.Add(key)) throw new Refused($"a struct holding the field '{key}' twice");
                fields.Add(new DeclEntry(key, Value(p)));
            }
            return fields;
        }

        private static DeclValue Int(long v) => new DeclInteger(v.ToString(CultureInfo.InvariantCulture));
        private static DeclValue Int(ulong v) => new DeclInteger(v.ToString(CultureInfo.InvariantCulture));
        private static DeclValue Single(float v) => new DeclFloat(Float(v));
        private static DeclValue Singles(params float[] v) => new DeclList(v.Select(Single).ToList());

        public DeclValue Value(LtProp p) => p switch
        {
            BinTreeNone => throw new Refused("a value of kind none, which does not render"),
            BinTreeBitBool b => new DeclBool(b.Value),
            BinTreeBool b => new DeclBool(b.Value),
            BinTreeI8 v => Int(v.Value),
            BinTreeU8 v => Int(v.Value),
            BinTreeI16 v => Int(v.Value),
            BinTreeU16 v => Int(v.Value),
            BinTreeI32 v => Int(v.Value),
            BinTreeU32 v => Int(v.Value),
            BinTreeI64 v => Int(v.Value),
            BinTreeU64 v => Int(v.Value),
            BinTreeF32 v => Single(v.Value),
            BinTreeVector2 v => Singles(v.Value.X, v.Value.Y),
            BinTreeVector3 v => Singles(v.Value.X, v.Value.Y, v.Value.Z),
            BinTreeVector4 v => Singles(v.Value.X, v.Value.Y, v.Value.Z, v.Value.W),
            BinTreeMatrix44 v => Singles(
                v.Value.M11, v.Value.M12, v.Value.M13, v.Value.M14, v.Value.M21, v.Value.M22, v.Value.M23, v.Value.M24,
                v.Value.M31, v.Value.M32, v.Value.M33, v.Value.M34, v.Value.M41, v.Value.M42, v.Value.M43, v.Value.M44),
            BinTreeColor v => new DeclList(new DeclValue[]
                { Int((long)Byte(v.Value.R)), Int((long)Byte(v.Value.G)), Int((long)Byte(v.Value.B)), Int((long)Byte(v.Value.A)) }),
            BinTreeString v => new DeclText(v.Value ?? ""),
            BinTreeHash v => new DeclText(Hash32(v.Value, names.Field(v.Value) ?? names.Entry(v.Value) ?? names.Class(v.Value))),
            BinTreeObjectLink v => new DeclText(Hash32(v.Value, names.Entry(v.Value))),
            BinTreeWadChunkLink v => new DeclText(File64(v.Value)),
            BinTreeContainer c => new DeclList(c.Elements.Select(Value).ToList()),   // list and list2
            BinTreeOptional o => Optional(o),
            BinTreeMap m => Map(m),
            BinTreeEmbedded e => Struct("embed", e),
            BinTreeStruct s => s.ClassHash == 0 ? new DeclNull() : Struct("pointer", s),
            _ => throw new Refused($"a value of type {p.GetType().Name}, which this writer does not spell"),
        };

        /// <summary>Null when empty; the element, or a one-element list where the element itself renders as
        /// a list (a vector, a colour, a container) or as null (league-mod section 6).</summary>
        private DeclValue Optional(BinTreeOptional o)
        {
            if (o.Value is null) return new DeclNull();
            var element = Value(o.Value);
            return element is DeclList or DeclNull ? new DeclList(new[] { element }) : element;
        }

        private static int Byte(float channel) => (int)MathF.Round(Math.Clamp(channel, 0f, 1f) * 255f);

        private DeclValue Struct(string pin, BinTreeStruct s) =>
            new DeclStruct(pin, ClassName(s.ClassHash), Fields(s.Properties.Values, s.ClassHash));

        private DeclValue Map(BinTreeMap m)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var entries = new List<DeclEntry>();
            foreach (var (k, v) in m)
            {
                string key = k switch
                {
                    BinTreeBitBool b => b.Value ? "true" : "false",
                    BinTreeBool b => b.Value ? "true" : "false",
                    BinTreeI8 x => x.Value.ToString(CultureInfo.InvariantCulture),
                    BinTreeU8 x => x.Value.ToString(CultureInfo.InvariantCulture),
                    BinTreeI16 x => x.Value.ToString(CultureInfo.InvariantCulture),
                    BinTreeU16 x => x.Value.ToString(CultureInfo.InvariantCulture),
                    BinTreeI32 x => x.Value.ToString(CultureInfo.InvariantCulture),
                    BinTreeU32 x => x.Value.ToString(CultureInfo.InvariantCulture),
                    BinTreeI64 x => x.Value.ToString(CultureInfo.InvariantCulture),
                    BinTreeU64 x => x.Value.ToString(CultureInfo.InvariantCulture),
                    BinTreeF32 x => Float(x.Value),
                    BinTreeString x => x.Value ?? "",
                    BinTreeHash x => Hash32(x.Value, names.Field(x.Value) ?? names.Entry(x.Value) ?? names.Class(x.Value)),
                    BinTreeWadChunkLink x => File64(x.Value),
                    _ => throw new Refused($"a map keyed by {k.GetType().Name}, which does not render"),
                };
                if (!keys.Add(key)) throw new Refused($"a map holding the key '{key}' twice");
                entries.Add(new DeclEntry(key, Value(v)));
            }
            if (keys.Count == 1 && Reserved.Contains(keys.First()))
                throw new Refused($"a map whose one key '{keys.First()}' reads back as a pin");
            return new DeclMap(entries);
        }
    }

    // ------------------------------------------------------------------ the YAML rendering (M757)

    /// <summary>The manifest text of a model. Flow style throughout, every string double-quoted.</summary>
    private static class Yaml
    {
        public static string Module(string target, DeclaredEdit edit)
        {
            var body = new StringBuilder();
            if (edit.AddLinks.Count > 0) body.Append("    links: [").Append(string.Join(", ", edit.AddLinks.Select(Quote))).Append("]\n");
            if (edit.DropLinks.Count > 0) body.Append("    -links: [").Append(string.Join(", ", edit.DropLinks.Select(Quote))).Append("]\n");

            foreach (var entry in edit.Bodies)
            {
                body.Append("    ").Append(Key(entry.Entry)).Append(":\n");
                foreach (var p in entry.Edits)
                {
                    CheckEdit(entry.Entry, p);
                    body.Append("      ").Append(Key(p.Key)).Append(": ").Append(Value(p.Value)).Append('\n');
                }
            }

            if (edit.Objects.Count > 0)
            {
                body.Append("    objects:\n");
                foreach (var o in edit.Objects)
                {
                    body.Append("      ").Append(Key(o.Name)).Append(":\n");
                    if (o.Remove) { body.Append("        remove: true\n"); continue; }
                    body.Append("        class: ").Append(Quote(o.Class!)).Append('\n');
                    if (o.Set.Count > 0) body.Append("        set: ").Append(Fields(o.Set)).Append('\n');
                }
            }

            var module = new StringBuilder();
            module.Append("  - target: ").Append(Quote(target)).Append('\n').Append(body);
            return module.ToString();
        }

        private static string Fields(IReadOnlyList<DeclEntry> fields) =>
            fields.Count == 0 ? "{}" : "{" + string.Join(", ", fields.Select(f => Quote(f.Key) + ": " + Value(f.Value))) + "}";

        public static string Value(DeclValue v) => v switch
        {
            DeclNull => "null",
            DeclBool b => b.Value ? "true" : "false",
            DeclInteger i => i.Digits,
            DeclFloat f => f.Text,
            DeclText t => Quote(t.Value),
            DeclList l => "[" + string.Join(", ", l.Items.Select(Value)) + "]",
            DeclMap m => "{" + string.Join(", ", m.Entries.Select(e => Quote(e.Key) + ": " + Value(e.Value))) + "}",
            DeclStruct s => Struct(s),
            DeclRef r => "{\"ref\": " + Quote(r.Text) + "}",   // M815: the document form, which the loader reads as a reference
            _ => throw new Refused($"a value of type {v.GetType().Name}, which this writer does not spell"),
        };

        private static string Struct(DeclStruct s)
        {
            string fields = Fields(s.Fields);
            if (TagClass.IsMatch(s.Class)) return $"!{s.Pin}({s.Class}) {fields}";
            // a class spelled outside a tag's characters stays in the pin's document form
            string set = s.Fields.Count == 0 ? "" : ", \"set\": " + fields;
            return $"{{\"{s.Pin}\": {{\"class\": {Quote(s.Class)}{set}}}}}";
        }
    }

    // ------------------------------------------------------------------ the JSON rendering (M814)

    /// <summary>The document form of a model. Numbers are parsed from their spelling so the token written is
    /// the token the diff chose (<c>1E-30</c>, <c>-0.0</c>, a u64 past the i64 range).</summary>
    private static class Json
    {
        public static JsonObject Body(DeclaredEdit edit)
        {
            var body = new JsonObject();
            if (edit.Objects.Count > 0)
            {
                var objects = new JsonObject();
                foreach (var o in edit.Objects) objects[o.Name] = ObjectEdit(o);
                body["objects"] = objects;
            }
            if (edit.AddLinks.Count > 0) body["links"] = new JsonArray(edit.AddLinks.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray());
            if (edit.DropLinks.Count > 0) body["-links"] = new JsonArray(edit.DropLinks.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray());
            foreach (var entry in edit.Bodies)
            {
                foreach (var e in entry.Edits) CheckEdit(entry.Entry, e);
                body[entry.Entry] = Mapping(entry.Edits);
            }
            return body;
        }

        private static JsonObject ObjectEdit(DeclaredObject o)
        {
            if (o.Remove) return new JsonObject { ["remove"] = true };
            var json = new JsonObject { ["class"] = o.Class };
            if (o.Set.Count > 0) json["set"] = Mapping(o.Set);
            return json;
        }

        private static JsonObject Mapping(IReadOnlyList<DeclEntry> entries)
        {
            var json = new JsonObject();
            foreach (var e in entries) json[e.Key] = Value(e.Value);
            return json;
        }

        public static JsonNode? Value(DeclValue v) => v switch
        {
            DeclNull => null,
            DeclBool b => JsonValue.Create(b.Value),
            DeclInteger i => JsonNode.Parse(i.Digits),
            DeclFloat f => JsonNode.Parse(f.Text),
            DeclText t => JsonValue.Create(t.Value),
            DeclList l => new JsonArray(l.Items.Select(Value).ToArray()),
            DeclMap m => Mapping(m.Entries),
            DeclStruct s => Struct(s),
            DeclRef r => new JsonObject { ["ref"] = r.Text },   // M815
            _ => throw new Refused($"a value of type {v.GetType().Name}, which this writer does not spell"),
        };

        private static JsonNode Struct(DeclStruct s)
        {
            var pin = new JsonObject { ["class"] = s.Class };
            if (s.Fields.Count > 0) pin["set"] = Mapping(s.Fields);
            return new JsonObject { [s.Pin] = pin };
        }
    }
}
