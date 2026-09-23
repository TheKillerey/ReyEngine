using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
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
}

/// <summary>
/// M757: a project's copy of a game bin, written as LTK Manager game-data declarations against the game's
/// copy - league-mod's <c>game_data.yaml</c> (declarations version 1, <c>ltk_game_data</c> 0.6), which
/// LTK Manager 1.20+ applies over the INSTALLED patch's bin at every overlay build. A mod that declares its
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
/// <c>none</c> or a map key of a non-scalar kind does not render (league-mod section 6).</para>
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
/// </summary>
public static class BinDeclarations
{
    public const string FileName = "game_data.yaml";

    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex PlainKey = new(@"^[A-Za-z_][A-Za-z0-9_]*(?:[./][A-Za-z0-9_]+)*$", RegexOptions.Compiled);
    private static readonly Regex TagClass = new(@"^[A-Za-z0-9_\-.:/]+$", RegexOptions.Compiled);

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

    /// <summary>The chunk's target spelling: its path when <paramref name="relPath"/> hashes to
    /// <paramref name="chunkHash"/>, else the 16 hexadecimal digits of the hash (league-mod section 4).</summary>
    public static string TargetOf(string relPath, ulong chunkHash)
    {
        string path = relPath.Replace('\\', '/');
        return HashAlgorithms.WadPath(path) == chunkHash ? path.ToLowerInvariant() : chunkHash.ToString("x16");
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

    private static bool IsPatch(byte[] b) => b.Length >= 4 && b[0] == (byte)'P' && b[1] == (byte)'T' && b[2] == (byte)'C' && b[3] == (byte)'H';

    private static DeclaredChunk Diff(string target, BinTree riot, BinTree mod, IDeclarationNames names)
    {
        var w = new Writer(names);
        var body = new StringBuilder();
        int properties = 0, added = 0, removed = 0;

        // dependencies: compared ASCII case-insensitively, as the format removes and de-duplicates them
        var riotLinks = riot.Dependencies.Select(d => d.ToLowerInvariant()).ToHashSet();
        var modLinks = mod.Dependencies.Select(d => d.ToLowerInvariant()).ToHashSet();
        var addLinks = mod.Dependencies.Where(d => !riotLinks.Contains(d.ToLowerInvariant()))
            .DistinctBy(d => d.ToLowerInvariant()).ToList();
        var dropLinks = riot.Dependencies.Where(d => !modLinks.Contains(d.ToLowerInvariant()))
            .DistinctBy(d => d.ToLowerInvariant()).ToList();
        if (addLinks.Count > 0) body.Append("    links: [").Append(string.Join(", ", addLinks.Select(Quote))).Append("]\n");
        if (dropLinks.Count > 0) body.Append("    -links: [").Append(string.Join(", ", dropLinks.Select(Quote))).Append("]\n");

        // changed objects, in the project's order
        var objects = new StringBuilder();
        foreach (var (hash, mo) in mod.Objects)
        {
            if (!riot.Objects.TryGetValue(hash, out var ro))
            {
                objects.Append("      ").Append(Key(w.EntryName(hash, needsSlash: false))).Append(":\n")
                       .Append("        class: ").Append(Quote(w.ClassName(mo.ClassHash))).Append('\n');
                if (mo.Properties.Count > 0)
                    objects.Append("        set: ").Append(w.Fields(mo.Properties.Values, mo.ClassHash)).Append('\n');
                added++;
                continue;
            }
            if (ro.ClassHash != mo.ClassHash)
                throw new Refused($"{w.EntryName(hash, false)} changed class, which no declaration can express");
            if (BinPropEquality.ObjectsEqual(ro, mo)) continue;

            var edits = new List<(string Path, string Value)>();
            DiffProps("", ro.ClassHash, ro.Properties, mo.Properties, topLevel: true, edits, w, w.EntryName(hash, false));
            if (edits.Count == 0) continue;
            body.Append("    ").Append(Key(w.EntryName(hash, needsSlash: true))).Append(":\n");
            foreach (var (path, value) in edits)
                body.Append("      ").Append(Key(path)).Append(": ").Append(value).Append('\n');
            properties += edits.Count;
        }
        foreach (var (hash, _) in riot.Objects)
        {
            if (mod.Objects.ContainsKey(hash)) continue;
            objects.Append("      ").Append(Key(w.EntryName(hash, needsSlash: false))).Append(":\n")
                   .Append("        remove: true\n");
            removed++;
        }
        if (objects.Length > 0) body.Append("    objects:\n").Append(objects);

        if (body.Length == 0) return new(target, null, null);
        var module = new StringBuilder();
        module.Append("  - target: ").Append(Quote(target)).Append('\n').Append(body);
        return new(target, module.ToString(), null, properties, added, removed, addLinks.Count + dropLinks.Count);
    }

    private static void DiffProps(string prefix, uint cls, IReadOnlyDictionary<uint, LtProp> riot,
        IReadOnlyDictionary<uint, LtProp> mod, bool topLevel, List<(string, string)> edits, Writer w, string entry)
    {
        foreach (var (hash, mp) in mod)
        {
            string path = prefix.Length == 0 ? w.FieldName(hash, cls) : prefix + "." + w.FieldName(hash, cls);
            if (!riot.TryGetValue(hash, out var rp)) { edits.Add((path, w.Value(mp))); continue; }
            if (BinPropEquality.PropsEqual(rp, mp)) continue;
            if (rp is BinTreeStruct rs && mp is BinTreeStruct ms && rp.GetType() == mp.GetType()
                && rs.ClassHash == ms.ClassHash && rs.ClassHash != 0
                && rs.Properties.Keys.All(ms.Properties.ContainsKey))
            {
                // the same struct with fields changed or added: one key per field
                DiffProps(path, ms.ClassHash, rs.Properties, ms.Properties, topLevel: false, edits, w, entry);
                continue;
            }
            edits.Add((path, w.Value(mp)));   // a different struct, a struct that lost a field, a container
        }
        if (!topLevel) return;   // a nested removal was set whole above
        foreach (var hash in riot.Keys)
            if (!mod.ContainsKey(hash))
                throw new Refused($"{entry} removes {w.FieldName(hash, cls)}, and no declaration removes a property");
    }

    /// <summary>The manifest for a layer: every declared module, in the order given.</summary>
    public static string Manifest(IEnumerable<DeclaredChunk> chunks)
    {
        var sb = new StringBuilder();
        sb.Append("# Written by ReyEngine: each module is one bin's changes against the game's copy at the time\n");
        sb.Append("# of sending. LTK Manager applies them over the installed patch's bin at every build.\n");
        sb.Append("version: 1\nmodules:\n");
        foreach (var c in chunks) if (c.Module is { } m) sb.Append(m);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ spelling

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
    /// (<c>reject_non_finite_typeless_float</c>, measured), so a bin holding one ships whole.</summary>
    public static string Float(float f) =>
        !float.IsFinite(f) ? throw new Refused($"a non-finite f32 ({f.ToString(CultureInfo.InvariantCulture)}), which the declaration loader rejects")
        : f == 0f && float.IsNegative(f) ? "-0.0"   // "-0" would read as the integer zero and lose the sign bit
        : f.ToString("R", CultureInfo.InvariantCulture);

    private sealed class Writer(IDeclarationNames names)
    {
        public string FieldName(uint hash, uint cls)
        {
            string? n = names.Field(hash);
            return n is not null && Identifier.IsMatch(n) && !Keywords.Contains(n) && HashAlgorithms.Fnv1a(n) == hash
                ? n : $"0x{hash:x8}";
        }

        public string ClassName(uint hash)
        {
            string? n = names.Class(hash);
            return n is not null && n.Length > 0 && HashAlgorithms.Fnv1a(n) == hash ? n : $"0x{hash:x8}";
        }

        /// <summary>An entry name. At a target body's root it must carry a slash or be hash-form
        /// (league-mod D22), so a known name without one is spelled as its hash there.</summary>
        public string EntryName(uint hash, bool needsSlash)
        {
            string? n = names.Entry(hash);
            bool ok = n is not null && n.Length > 0 && HashAlgorithms.Fnv1a(n) == hash && !Keywords.Contains(n)
                      && (!needsSlash || n.Contains('/'));
            return ok ? n! : $"0x{hash:x8}";
        }

        private string Hash32(uint h, string? name) =>
            name is not null && name.Length > 0 && HashAlgorithms.Fnv1a(name) == h ? name : $"0x{h:x8}";

        private string File64(ulong h)
        {
            string? n = names.File(h);
            return n is not null && n.Length > 0 && HashAlgorithms.WadPath(n) == h ? n : $"0x{h:x16}";
        }

        /// <summary>A struct's fields as a flow mapping of single field names.</summary>
        public string Fields(IEnumerable<LtProp> props, uint cls)
        {
            var parts = props.Select(p => Quote(FieldName(p.NameHash, cls)) + ": " + Value(p)).ToList();
            return parts.Count == 0 ? "{}" : "{" + string.Join(", ", parts) + "}";
        }

        public string Value(LtProp p) => p switch
        {
            BinTreeNone => throw new Refused("a value of kind none, which does not render"),
            BinTreeBitBool b => b.Value ? "true" : "false",
            BinTreeBool b => b.Value ? "true" : "false",
            BinTreeI8 v => v.Value.ToString(CultureInfo.InvariantCulture),
            BinTreeU8 v => v.Value.ToString(CultureInfo.InvariantCulture),
            BinTreeI16 v => v.Value.ToString(CultureInfo.InvariantCulture),
            BinTreeU16 v => v.Value.ToString(CultureInfo.InvariantCulture),
            BinTreeI32 v => v.Value.ToString(CultureInfo.InvariantCulture),
            BinTreeU32 v => v.Value.ToString(CultureInfo.InvariantCulture),
            BinTreeI64 v => v.Value.ToString(CultureInfo.InvariantCulture),
            BinTreeU64 v => v.Value.ToString(CultureInfo.InvariantCulture),
            BinTreeF32 v => Float(v.Value),
            BinTreeVector2 v => $"[{Float(v.Value.X)}, {Float(v.Value.Y)}]",
            BinTreeVector3 v => $"[{Float(v.Value.X)}, {Float(v.Value.Y)}, {Float(v.Value.Z)}]",
            BinTreeVector4 v => $"[{Float(v.Value.X)}, {Float(v.Value.Y)}, {Float(v.Value.Z)}, {Float(v.Value.W)}]",
            BinTreeMatrix44 v => "[" + string.Join(", ", new[]
            {
                v.Value.M11, v.Value.M12, v.Value.M13, v.Value.M14, v.Value.M21, v.Value.M22, v.Value.M23, v.Value.M24,
                v.Value.M31, v.Value.M32, v.Value.M33, v.Value.M34, v.Value.M41, v.Value.M42, v.Value.M43, v.Value.M44,
            }.Select(Float)) + "]",
            BinTreeColor v => $"[{Byte(v.Value.R)}, {Byte(v.Value.G)}, {Byte(v.Value.B)}, {Byte(v.Value.A)}]",
            BinTreeString v => Quote(v.Value ?? ""),
            BinTreeHash v => Quote(Hash32(v.Value, names.Field(v.Value) ?? names.Entry(v.Value) ?? names.Class(v.Value))),
            BinTreeObjectLink v => Quote(Hash32(v.Value, names.Entry(v.Value))),
            BinTreeWadChunkLink v => Quote(File64(v.Value)),
            BinTreeContainer c => "[" + string.Join(", ", c.Elements.Select(Value)) + "]",   // list and list2
            BinTreeOptional o => Optional(o),
            BinTreeMap m => Map(m),
            BinTreeEmbedded e => Struct("embed", e),
            BinTreeStruct s => s.ClassHash == 0 ? "null" : Struct("pointer", s),
            _ => throw new Refused($"a value of type {p.GetType().Name}, which this writer does not spell"),
        };

        /// <summary>Null when empty; the element, or a one-element list where the element itself renders as
        /// a list (a vector, a colour, a container) or as null (league-mod section 6).</summary>
        private string Optional(BinTreeOptional o)
        {
            if (o.Value is null) return "null";
            string element = Value(o.Value);
            return element.StartsWith('[') || element == "null" ? "[" + element + "]" : element;
        }

        private static string Byte(float channel) => ((int)MathF.Round(Math.Clamp(channel, 0f, 1f) * 255f)).ToString(CultureInfo.InvariantCulture);

        private string Struct(string pin, BinTreeStruct s)
        {
            string cls = ClassName(s.ClassHash);
            string fields = Fields(s.Properties.Values, s.ClassHash);
            if (TagClass.IsMatch(cls)) return $"!{pin}({cls}) {fields}";
            // a class spelled outside a tag's characters stays in the pin's document form
            string set = s.Properties.Count == 0 ? "" : ", \"set\": " + fields;
            return $"{{\"{pin}\": {{\"class\": {Quote(cls)}{set}}}}}";
        }

        private string Map(BinTreeMap m)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var parts = new List<string>();
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
                parts.Add(Quote(key) + ": " + Value(v));
            }
            if (keys.Count == 1 && Reserved.Contains(keys.First()))
                throw new Refused($"a map whose one key '{keys.First()}' reads back as a pin");
            return "{" + string.Join(", ", parts) + "}";
        }
    }
}
