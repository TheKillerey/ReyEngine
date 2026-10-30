using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReyEngine.Core.Build;

/// <summary>
/// M850: the YAML manifest a .fantome layer stores its declarations as, <c>META/game_data/&lt;layer&gt;/game_data.yaml</c>
/// (league-mod PR #318, <c>ltk_fantome::GAME_DATA_MANIFEST</c>, ADR-0042).
///
/// <para><b>Writing</b> (<see cref="FromDocument"/>) turns the document ReyEngine keeps (<see cref="GameDataDocumentText"/>, JSON) into
/// the manifest: <c>version: N</c>, <c>modules:</c> and one module per line as a YAML flow mapping (JSON is YAML), without its
/// <c>origin</c>, every other token as the document spells it. league-mod's <c>manifest_yaml()</c> writes the same declarations in
/// block style (<c>serde_saphyr</c>); the two load to the same <c>Declarations</c> and differ only in layout. Reproducing the block
/// layout byte for byte needs serde_saphyr's quoting rules for every scalar; the flow form is the one this project already sends to
/// LTK Manager (M757/M816, checked against league-mod's own loader) and needs none.</para>
///
/// <para><b>Reading</b> (<see cref="ToDocumentJson"/>) is a reader for the YAML a manifest uses - block and flow collections, plain,
/// quoted and block scalars, comments, local tags - that gives back the JSON document the rest of ReyEngine stores and applies (the
/// <c>origin</c> of module N is <c>{manifest: game_data.yaml, source: null, module: N}</c>, what league-mod's loader gives it). Tags
/// follow <c>ltk_game_data::Value</c>: <c>!f32 1.5</c> is <c>{"f32": 1.5}</c>, <c>!pointer(C) {..}</c> is
/// <c>{"pointer": {"class": "C", "set": {..}}}</c>, and a tag on a body of <c>objects</c> is its class. It is not a general YAML
/// reader: anchors, aliases, merge keys, complex keys, multiple documents and non-finite floats are refused with a
/// <see cref="FormatException"/>, and so is a manifest naming a <c>source</c> (the entry names none).</para>
/// </summary>
public static class GameDataManifestYaml
{
    /// <summary>The layer-relative name of a layer's manifest entry (<c>ltk_fantome::GAME_DATA_MANIFEST</c>).</summary>
    public const string EntryName = "game_data.yaml";

    /// <summary>The origin manifest label a loader gives modules read from the entry.</summary>
    public const string OriginManifest = "game_data.yaml";

    /// <summary>Whether a layer-relative path is the name of the manifest entry. league-mod compares ASCII case-insensitively.</summary>
    public static bool IsManifestName(string layerRelativePath) =>
        string.Equals(layerRelativePath, EntryName, StringComparison.OrdinalIgnoreCase);

    // =============================================================== writing

    /// <summary>
    /// The manifest of a document, or null when the document is not <see cref="GameDataDocumentText.IsExpectedShape"/> (a manifest
    /// holds <c>version</c> and <c>modules</c> and nothing else, so such a document cannot be written as one).
    /// </summary>
    /// <param name="documentJson">The layer's document: <c>{"version": 1, "modules": [..]}</c>.</param>
    /// <exception cref="System.Text.Json.JsonException">The text is not JSON.</exception>
    public static string? FromDocument(string documentJson)
    {
        ArgumentNullException.ThrowIfNull(documentJson);
        var doc = GameDataDocumentText.Read(documentJson);
        if (!doc.IsExpectedShape) return null;
        var sb = new StringBuilder();
        sb.Append("version: ").Append(doc.VersionText ?? "1").Append('\n');
        if (doc.Modules.Count == 0) return sb.Append("modules: []\n").ToString();   // a bare `modules:` is null, not a list
        sb.Append("modules:\n");
        foreach (var module in doc.Modules)
            sb.Append("  - ").Append(GameDataDocumentText.ManifestModule(module.Text)).Append('\n');
        return sb.ToString();
    }

    // =============================================================== reading

    /// <summary>
    /// The document (compact JSON) a manifest declares.
    /// </summary>
    /// <exception cref="FormatException">The text is not a manifest this reader takes; the message says where.</exception>
    public static string ToDocumentJson(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        if (yaml.Length > 0 && yaml[0] == '﻿') yaml = yaml[1..];
        var root = new Parser(yaml).ParseDocument();
        var json = new StringBuilder();
        new Converter(json).Root(root);
        return json.ToString();
    }

    private enum Kind { Null, Scalar, Map, Seq }

    private sealed class Node
    {
        public Kind Kind;
        public string? Tag;                  // as written, with its '!' (!f32, !pointer(C), !!str)
        public string Text = "";             // a scalar's text
        public bool Plain;                   // a scalar written without quotes: only a plain scalar is typed
        public List<(string Key, Node Value)> Map = new();
        public List<Node> Seq = new();
        public int Line;
    }

    // ------------------------------------------------------------------ parser

    private sealed class Parser
    {
        private readonly List<string> _lines;
        private int _pos;

        public Parser(string text)
        {
            _lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        }

        private static FormatException Fail(int line, string why) => new($"line {line + 1}: {why}");

        public Node ParseDocument()
        {
            SkipBlank();
            if (_pos < _lines.Count && _lines[_pos].TrimEnd() == "---") { _pos++; }
            else if (_pos < _lines.Count && _lines[_pos].StartsWith("--- ", StringComparison.Ordinal))
                _lines[_pos] = _lines[_pos][4..];
            var node = ParseBlock(0);
            SkipBlank();
            if (_pos < _lines.Count)
            {
                string t = _lines[_pos].TrimEnd();
                if (t is "..." or "---") throw Fail(_pos, "a second document is not a manifest");
                throw Fail(_pos, "unexpected content after the manifest");
            }
            return node;
        }

        private static int IndentOf(string line)
        {
            int i = 0;
            while (i < line.Length && line[i] == ' ') i++;
            return i;
        }

        private static bool IsBlank(string line)
        {
            int i = IndentOf(line);
            return i >= line.Length || line[i] == '#' || (line[i] == '\t' && line.Trim().Length == 0);
        }

        private void SkipBlank()
        {
            while (_pos < _lines.Count && IsBlank(_lines[_pos])) _pos++;
        }

        /// <summary>The next node at an indent of at least <paramref name="min"/>; a null node when there is none.</summary>
        private Node ParseBlock(int min)
        {
            SkipBlank();
            if (_pos >= _lines.Count) return new Node { Kind = Kind.Null, Line = _pos };
            string line = _lines[_pos];
            int indent = IndentOf(line);
            if (indent < min) return new Node { Kind = Kind.Null, Line = _pos };
            if (line.Length > indent && line[indent] == '\t') throw Fail(_pos, "a tab cannot indent");
            string body = line[indent..];
            if (body == "---" || body == "...") return new Node { Kind = Kind.Null, Line = _pos };
            if (IsSeqEntry(body)) return ParseSeq(indent);
            if (TryFindKeyColon(body, out _)) return ParseMap(indent);
            // a scalar, flow collection or tagged node on lines of its own
            _lines[_pos] = "";
            return ParseInline(body, indent - 1, indent, _pos);
        }

        private static bool IsSeqEntry(string body) => body[0] == '-' && (body.Length == 1 || body[1] == ' ');

        private Node ParseSeq(int indent)
        {
            var seq = new Node { Kind = Kind.Seq, Line = _pos };
            while (true)
            {
                SkipBlank();
                if (_pos >= _lines.Count) break;
                string line = _lines[_pos];
                if (IndentOf(line) != indent) break;
                string body = line[indent..];
                if (!IsSeqEntry(body)) break;
                int lineNo = _pos;
                string rest = body.Length == 1 ? "" : body[2..];
                int skip = 0;
                while (skip < rest.Length && rest[skip] == ' ') skip++;
                rest = rest[skip..];
                int contentIndent = indent + 2 + skip;
                if (rest.Length == 0 || rest[0] == '#')
                {
                    _lines[_pos] = "";
                    _pos++;
                    seq.Seq.Add(ParseBlock(indent + 1));
                }
                else
                {
                    // the item's own text sits at contentIndent: put it back as a line of that indent and read it as a block
                    _lines[_pos] = new string(' ', contentIndent) + rest;
                    var item = ParseBlock(indent + 1);
                    seq.Seq.Add(item);
                    _ = lineNo;
                }
            }
            return seq;
        }

        private Node ParseMap(int indent)
        {
            var map = new Node { Kind = Kind.Map, Line = _pos };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                SkipBlank();
                if (_pos >= _lines.Count) break;
                string line = _lines[_pos];
                if (IndentOf(line) != indent) break;
                string body = line[indent..];
                if (IsSeqEntry(body) || !TryFindKeyColon(body, out int colon)) break;
                int lineNo = _pos;
                string key = ReadKey(body[..colon], lineNo);
                if (!seen.Add(key)) throw Fail(lineNo, $"duplicate key '{key}'");
                string rest = body[(colon + 1)..];
                int skip = 0;
                while (skip < rest.Length && rest[skip] == ' ') skip++;
                rest = rest[skip..];
                _lines[_pos] = "";
                Node value;
                if (rest.Length == 0 || rest[0] == '#')
                {
                    _pos++;
                    SkipBlank();
                    if (_pos < _lines.Count && IndentOf(_lines[_pos]) == indent && IsSeqEntry(_lines[_pos][indent..]))
                        value = ParseSeq(indent);                  // a sequence may sit at its key's indent
                    else value = ParseBlock(indent + 1);
                }
                else
                {
                    value = ParseInline(rest, indent, indent + 1 + skip + colon, lineNo);
                }
                map.Map.Add((key, value));
            }
            return map;
        }

        private static string ReadKey(string raw, int line)
        {
            string k = raw.TrimEnd();
            if (k.Length == 0) throw Fail(line, "an empty key");
            if (k[0] is '"' or '\'')
            {
                int p = 0;
                var scalar = ReadQuoted(k, ref p, line);
                if (k[p..].Trim().Length != 0) throw Fail(line, "text after a quoted key");
                return scalar;
            }
            if (k[0] is '&' or '*' or '?' or '!') throw Fail(line, "a tagged, anchored or complex key is not a manifest key");
            return k;
        }

        /// <summary>
        /// The colon that ends a key in a block line: a ':' followed by a space or the end of the line, outside quotes and flow
        /// brackets that open before it. False when the line has none (it is a scalar).
        /// </summary>
        private static bool TryFindKeyColon(string body, out int colon)
        {
            colon = -1;
            char c0 = body[0];
            if (c0 is '[' or '{' or '|' or '>' or '!' or '&' or '*' or '#') return false;
            int i = 0;
            if (c0 is '"' or '\'')
            {
                int p = 0;
                try { ReadQuoted(body, ref p, 0); } catch (FormatException) { return false; }
                i = p;
                while (i < body.Length && body[i] == ' ') i++;
                if (i < body.Length && body[i] == ':' && (i + 1 == body.Length || body[i + 1] == ' ')) { colon = i; return true; }
                return false;
            }
            for (; i < body.Length; i++)
            {
                char c = body[i];
                if (c == '#' && i > 0 && body[i - 1] == ' ') return false;
                if (c == ':' && (i + 1 == body.Length || body[i + 1] == ' ')) { colon = i; return true; }
            }
            return false;
        }

        /// <summary>
        /// A value that starts in <paramref name="rest"/>: optional tag (and refused anchor), then a flow collection, quoted scalar, block
        /// scalar, plain scalar, or - after a tag alone - a block node on the lines below. <paramref name="parentIndent"/> is the indent of the
        /// construct that owns the value; lines indented deeper continue it.
        /// </summary>
        private Node ParseInline(string rest, int parentIndent, int column, int lineNo)
        {
            rest = rest.TrimStart(' ');
            string? tag = null;
            while (rest.Length > 0 && (rest[0] == '!' || rest[0] == '&' || rest[0] == '*'))
            {
                if (rest[0] != '!') throw Fail(lineNo, "anchors and aliases are not read");
                if (rest.StartsWith("!<", StringComparison.Ordinal)) throw Fail(lineNo, "a verbatim tag is not read");
                int e = 1;
                int paren = 0;
                while (e < rest.Length && (paren > 0 || rest[e] != ' '))
                {
                    if (rest[e] == '(') paren++;
                    else if (rest[e] == ')') paren--;
                    e++;
                }
                tag = rest[..e];
                rest = rest[e..].TrimStart(' ');
            }

            Node node;
            if (rest.Length == 0 || rest[0] == '#')
            {
                // a tag on a node written on the lines below, or on nothing (null)
                _pos = Math.Max(_pos, lineNo + 1);
                SkipBlank();
                if (_pos < _lines.Count && IndentOf(_lines[_pos]) > parentIndent) node = ParseBlock(parentIndent + 1);
                else node = new Node { Kind = Kind.Null, Plain = true, Line = lineNo };
            }
            else if (rest[0] is '[' or '{')
            {
                node = ParseFlowFromLines(rest, lineNo);
            }
            else if (rest[0] is '"' or '\'')
            {
                node = ParseQuotedFromLines(rest, parentIndent, lineNo);
            }
            else if (rest[0] is '|' or '>')
            {
                node = ParseBlockScalar(rest, parentIndent, lineNo);
            }
            else
            {
                node = ParsePlainFromLines(rest, parentIndent, lineNo);
            }
            if (tag is not null) node.Tag = tag;
            return node;
        }

        // ---- flow ----

        private Node ParseFlowFromLines(string first, int lineNo)
        {
            // gather lines until the brackets balance (outside quotes), comments removed
            var text = new StringBuilder();
            string current = first;
            int depth = 0;
            int ln = lineNo;
            while (true)
            {
                bool closed = Scan(current, ref depth, text);
                text.Append('\n');
                if (closed) break;
                ln++;
                if (ln >= _lines.Count) throw Fail(lineNo, "a flow collection is not closed");
                current = _lines[ln];
                _lines[ln] = "";
            }
            _pos = Math.Max(_pos, ln + 1);
            string s = text.ToString();
            int p = 0;
            var node = new FlowReader(s, lineNo).ReadNode(ref p);
            while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
            if (p < s.Length && s[p] != '#') throw Fail(lineNo, "text after a flow collection");
            return node;

            static bool Scan(string line, ref int depth, StringBuilder text)
            {
                for (int i = 0; i < line.Length; i++)
                {
                    char c = line[i];
                    if (c is '"' or '\'')
                    {
                        int start = i;
                        int end = QuotedEnd(line, i);
                        if (end < 0) { text.Append(line, start, line.Length - start); i = line.Length; break; }   // multi-line quote: the reader fails
                        text.Append(line, start, end - start + 1);
                        i = end;
                        continue;
                    }
                    if (c == '#' && (i == 0 || line[i - 1] == ' ')) return depth == 0;
                    text.Append(c);
                    if (c is '[' or '{') depth++;
                    else if (c is ']' or '}')
                    {
                        depth--;
                        if (depth == 0) { text.Append(line, i + 1, line.Length - i - 1); return true; }   // what follows is checked by the caller
                    }
                }
                return depth == 0;
            }
        }

        private static int QuotedEnd(string s, int start)
        {
            char q = s[start];
            for (int i = start + 1; i < s.Length; i++)
            {
                if (q == '"' && s[i] == '\\') { i++; continue; }
                if (s[i] == q)
                {
                    if (q == '\'' && i + 1 < s.Length && s[i + 1] == '\'') { i++; continue; }
                    return i;
                }
            }
            return -1;
        }

        private sealed class FlowReader
        {
            private readonly string _s;
            private readonly int _line;
            public FlowReader(string s, int line) { _s = s; _line = line; }

            private void Ws(ref int p) { while (p < _s.Length && char.IsWhiteSpace(_s[p])) p++; }

            public Node ReadNode(ref int p)
            {
                Ws(ref p);
                if (p >= _s.Length) throw Fail(_line, "a flow value is missing");
                string? tag = null;
                while (p < _s.Length && (_s[p] == '!' || _s[p] == '&' || _s[p] == '*'))
                {
                    if (_s[p] != '!') throw Fail(_line, "anchors and aliases are not read");
                    int e = p + 1, paren = 0;
                    while (e < _s.Length && (paren > 0 || !char.IsWhiteSpace(_s[e])) && !(paren == 0 && _s[e] is ',' or ']' or '}'))
                    {
                        if (_s[e] == '(') paren++;
                        else if (_s[e] == ')') paren--;
                        e++;
                    }
                    tag = _s[p..e];
                    p = e;
                    Ws(ref p);
                }
                Node node;
                if (p >= _s.Length) throw Fail(_line, "a flow value is missing");
                char c = _s[p];
                if (c == '[')
                {
                    p++;
                    node = new Node { Kind = Kind.Seq, Line = _line };
                    while (true)
                    {
                        Ws(ref p);
                        if (p >= _s.Length) throw Fail(_line, "a flow sequence is not closed");
                        if (_s[p] == ']') { p++; break; }
                        node.Seq.Add(ReadNode(ref p));
                        Ws(ref p);
                        if (p < _s.Length && _s[p] == ',') p++;
                        else if (p < _s.Length && _s[p] == ']') { p++; break; }
                        else throw Fail(_line, "expected ',' or ']' in a flow sequence");
                    }
                }
                else if (c == '{')
                {
                    p++;
                    node = new Node { Kind = Kind.Map, Line = _line };
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    while (true)
                    {
                        Ws(ref p);
                        if (p >= _s.Length) throw Fail(_line, "a flow mapping is not closed");
                        if (_s[p] == '}') { p++; break; }
                        string key = ReadKey(ref p);
                        if (!seen.Add(key)) throw Fail(_line, $"duplicate key '{key}'");
                        Ws(ref p);
                        Node value;
                        if (p < _s.Length && _s[p] == ':')
                        {
                            p++;
                            Ws(ref p);
                            if (p < _s.Length && _s[p] is ',' or '}') value = new Node { Kind = Kind.Null, Plain = true, Line = _line };
                            else value = ReadNode(ref p);
                        }
                        else value = new Node { Kind = Kind.Null, Plain = true, Line = _line };
                        node.Map.Add((key, value));
                        Ws(ref p);
                        if (p < _s.Length && _s[p] == ',') p++;
                        else if (p < _s.Length && _s[p] == '}') { p++; break; }
                        else throw Fail(_line, "expected ',' or '}' in a flow mapping");
                    }
                }
                else if (c is '"' or '\'')
                {
                    node = new Node { Kind = Kind.Scalar, Text = ReadQuoted(_s, ref p, _line), Plain = false, Line = _line };
                }
                else
                {
                    node = new Node { Kind = Kind.Scalar, Text = ReadPlainFlow(ref p, key: false), Plain = true, Line = _line };
                    if (node.Text.Length == 0) node.Kind = Kind.Null;
                }
                if (tag is not null) node.Tag = tag;
                return node;
            }

            private string ReadKey(ref int p)
            {
                Ws(ref p);
                if (p < _s.Length && _s[p] is '"' or '\'') return ReadQuoted(_s, ref p, _line);
                if (p < _s.Length && _s[p] is '[' or '{' or '!' or '&' or '*' or '?') throw Fail(_line, "only a scalar can be a key");
                return ReadPlainFlow(ref p, key: true);
            }

            private string ReadPlainFlow(ref int p, bool key)
            {
                int start = p;
                while (p < _s.Length)
                {
                    char c = _s[p];
                    if (c is ',' or ']' or '}' or '[' or '{') break;
                    if (c == ':' && (p + 1 >= _s.Length || char.IsWhiteSpace(_s[p + 1]) || _s[p + 1] is ',' or ']' or '}')) break;
                    if (c == '#' && p > start && char.IsWhiteSpace(_s[p - 1])) break;
                    p++;
                }
                _ = key;
                return Regex.Replace(_s[start..p].Trim(), @"\s*\n\s*", " ");
            }
        }

        // ---- quoted scalars ----

        private Node ParseQuotedFromLines(string first, int parentIndent, int lineNo)
        {
            string text = first;
            int ln = lineNo;
            while (QuotedEnd(text, 0) < 0)
            {
                ln++;
                if (ln >= _lines.Count) throw Fail(lineNo, "a quoted scalar is not closed");
                text += "\n" + _lines[ln].TrimStart(' ');
                _lines[ln] = "";
            }
            _pos = Math.Max(_pos, ln + 1);
            int p = 0;
            string value = ReadQuoted(text, ref p, lineNo);
            string tail = text[p..].Trim();
            if (tail.Length > 0 && tail[0] != '#') throw Fail(lineNo, "text after a quoted scalar");
            _ = parentIndent;
            return new Node { Kind = Kind.Scalar, Text = value, Plain = false, Line = lineNo };
        }

        /// <summary>Reads a quoted scalar starting at <paramref name="p"/>; line breaks inside fold to a space (a blank line to a break).</summary>
        private static string ReadQuoted(string s, ref int p, int lineNo)
        {
            char q = s[p];
            int end = QuotedEnd(s, p);
            if (end < 0) throw Fail(lineNo, "a quoted scalar is not closed");
            string inner = s.Substring(p + 1, end - p - 1);
            p = end + 1;
            var sb = new StringBuilder(inner.Length);
            for (int i = 0; i < inner.Length; i++)
            {
                char c = inner[i];
                if (c == '\n')
                {
                    // fold: trailing spaces go, one break is a space, extra breaks are breaks
                    while (sb.Length > 0 && sb[^1] is ' ' or '\t') sb.Length--;
                    int breaks = 1;
                    while (i + 1 < inner.Length && (inner[i + 1] == '\n' || inner[i + 1] is ' ' or '\t'))
                    {
                        i++;
                        if (inner[i] == '\n') breaks++;
                    }
                    if (breaks == 1) sb.Append(' '); else sb.Append('\n', breaks - 1);
                    continue;
                }
                if (q == '\'')
                {
                    if (c == '\'' && i + 1 < inner.Length && inner[i + 1] == '\'') { sb.Append('\''); i++; }
                    else sb.Append(c);
                    continue;
                }
                if (c != '\\') { sb.Append(c); continue; }
                if (++i >= inner.Length) throw Fail(lineNo, "a dangling escape");
                char e = inner[i];
                switch (e)
                {
                    case '0': sb.Append('\0'); break;
                    case 'a': sb.Append('\a'); break;
                    case 'b': sb.Append('\b'); break;
                    case 't': case '\t': sb.Append('\t'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'v': sb.Append('\v'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'e': sb.Append('\u001b'); break;
                    case ' ': sb.Append(' '); break;
                    case '"': sb.Append('"'); break;
                    case '/': sb.Append('/'); break;
                    case '\\': sb.Append('\\'); break;
                    case 'N': sb.Append('\u0085'); break;
                    case '_': sb.Append('\u00a0'); break;
                    case 'L': sb.Append('\u2028'); break;
                    case 'P': sb.Append('\u2029'); break;
                    case 'x': sb.Append(Hex(inner, ref i, 2, lineNo)); break;
                    case 'u': sb.Append(Hex(inner, ref i, 4, lineNo)); break;
                    case 'U': sb.Append(Hex(inner, ref i, 8, lineNo)); break;
                    case '\n':      // an escaped line break joins the lines with nothing between
                        while (i + 1 < inner.Length && inner[i + 1] is ' ' or '\t') i++;
                        break;
                    default: throw Fail(lineNo, $"the escape \\{e} is not YAML");
                }
            }
            return sb.ToString();

            static string Hex(string t, ref int i, int digits, int line)
            {
                if (i + digits >= t.Length) throw Fail(line, "a short unicode escape");
                if (!int.TryParse(t.AsSpan(i + 1, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int cp))
                    throw Fail(line, "a bad \\u escape");
                i += digits;
                if (cp is >= 0xD800 and <= 0xDFFF) return ((char)cp).ToString();   // half of a pair: the next escape completes it
                if (cp > 0x10FFFF) throw Fail(line, "a code point past U+10FFFF");
                return char.ConvertFromUtf32(cp);
            }
        }

        // ---- block scalars ----

        private Node ParseBlockScalar(string header, int parentIndent, int lineNo)
        {
            bool literal = header[0] == '|';
            char chomp = 'c';
            int explicitIndent = 0;
            int h = 1;
            for (; h < header.Length && header[h] != ' ' && header[h] != '#'; h++)
            {
                char c = header[h];
                if (c is '+' or '-') chomp = c;
                else if (c is >= '1' and <= '9') explicitIndent = c - '0';
                else throw Fail(lineNo, "a bad block scalar header");
            }
            int ln = lineNo + 1;
            int contentIndent = explicitIndent > 0 ? Math.Max(parentIndent, 0) + explicitIndent : -1;
            var rows = new List<string>();
            for (; ln < _lines.Count; ln++)
            {
                string line = _lines[ln];
                if (line.Trim().Length == 0) { rows.Add(""); continue; }
                int ind = IndentOf(line);
                if (contentIndent < 0)
                {
                    if (ind <= parentIndent) break;
                    contentIndent = ind;
                }
                if (ind < contentIndent) break;
                rows.Add(line[contentIndent..]);
                _lines[ln] = "";
            }
            // blank rows after the last content row belong to the chomping, not the text
            int trailing = 0;
            while (rows.Count > 0 && rows[^1].Length == 0) { rows.RemoveAt(rows.Count - 1); trailing++; }
            for (int k = ln - trailing; k < ln; k++) { }
            _pos = Math.Max(_pos, ln - trailing);
            var sb = new StringBuilder();
            if (literal)
            {
                sb.Append(string.Join("\n", rows));
            }
            else
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    if (i > 0)
                    {
                        bool prevBlank = rows[i - 1].Length == 0, curBlank = rows[i].Length == 0;
                        bool moreIndented = rows[i].StartsWith(' ') || rows[i - 1].StartsWith(' ');
                        if (curBlank) sb.Append('\n');
                        else if (prevBlank) { if (moreIndented) sb.Append('\n'); }
                        else sb.Append(moreIndented ? "\n" : " ");
                    }
                    sb.Append(rows[i]);
                }
            }
            if (rows.Count > 0)
            {
                if (chomp == 'c') sb.Append('\n');
                else if (chomp == '+') { sb.Append('\n'); sb.Append('\n', trailing); }
            }
            else if (chomp == '+') sb.Append('\n', trailing);
            return new Node { Kind = Kind.Scalar, Text = sb.ToString(), Plain = false, Line = lineNo };
        }

        // ---- plain scalars ----

        private Node ParsePlainFromLines(string first, int parentIndent, int lineNo)
        {
            string text = StripComment(first).TrimEnd();
            _pos = Math.Max(_pos, lineNo + 1);
            // continuation lines: deeper than the owner, not a comment, and the scalar is not a flow/quoted one
            while (_pos < _lines.Count)
            {
                string line = _lines[_pos];
                if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#')) { if (line.Trim().Length == 0) { _pos++; continue; } break; }
                if (IndentOf(line) <= parentIndent) break;
                string body = line.Trim();
                if (TryFindKeyColon(body, out _) || IsSeqEntry(body)) break;
                text += " " + StripComment(body).TrimEnd();
                _lines[_pos] = "";
                _pos++;
            }
            return new Node { Kind = Kind.Scalar, Text = text, Plain = true, Line = lineNo };
        }

        private static string StripComment(string s)
        {
            for (int i = 0; i < s.Length; i++)
                if (s[i] == '#' && (i == 0 || s[i - 1] == ' ')) return s[..i];
            return s;
        }
    }

    // ------------------------------------------------------------------ conversion to the JSON document

    private sealed class Converter
    {
        private readonly StringBuilder _o;
        public Converter(StringBuilder o) { _o = o; }

        private static FormatException Fail(Node n, string why) => new($"line {n.Line + 1}: {why}");

        private static readonly HashSet<string> TypeNames = new(StringComparer.Ordinal)
        {
            "bool", "i8", "i16", "i32", "i64", "u8", "u16", "u32", "u64", "f32", "vec2", "vec3", "vec4", "mtx44", "rgba",
            "string", "hash", "file", "link", "flag", "option", "pointer", "embed",
        };

        public void Root(Node root)
        {
            if (root.Kind != Kind.Map) throw Fail(root, "a manifest is a mapping with version and modules");
            Node? version = null, modules = null;
            foreach (var (key, value) in root.Map)
            {
                if (key == "version") version = value;
                else if (key == "modules") modules = value;
                else throw Fail(value, $"the manifest key '{key}' is not version or modules");
            }
            if (version is null) throw Fail(root, "the manifest has no version");
            if (version.Kind != Kind.Scalar || !version.Plain || !IsDecimalInt(version.Text)) throw Fail(version, "version is an integer");
            _o.Append("{\"version\":").Append(version.Text.Trim().TrimStart('+').TrimStart('0') is { Length: > 0 } v ? v : "0").Append(",\"modules\":[");
            int index = 0;
            if (modules is { Kind: not Kind.Null })
            {
                if (modules.Kind != Kind.Seq) throw Fail(modules, "modules is a list");
                foreach (var module in modules.Seq)
                {
                    if (index > 0) _o.Append(',');
                    Module(module, index++);
                }
            }
            _o.Append("]}");
        }

        private static bool IsDecimalInt(string t) => t.Length > 0 && t.TrimStart('+').All(char.IsAsciiDigit);

        private void Module(Node module, int index)
        {
            if (module.Kind != Kind.Map) throw Fail(module, "a module is a mapping");
            NoTag(module);
            var edits = new List<(string, Node)>();   // compact binding keys
            Node? name = null, target = null, editList = null, entries = null;
            foreach (var (key, value) in module.Map)
            {
                switch (key)
                {
                    case "name": name = value; break;
                    case "target": target = value; break;
                    case "edits": editList = value; break;
                    case "entries": entries = value; break;
                    case "source": throw Fail(value, "the manifest entry names no source, so a module cannot take one");
                    case "origin": throw Fail(value, "a manifest module has no origin");
                    default: edits.Add((key, value)); break;
                }
            }
            if ((target is null) == (entries is null)) throw Fail(module, "a module has one of target and entries");
            _o.Append('{');
            bool first = true;
            void Key(string k) { if (!first) _o.Append(','); first = false; Str(k); _o.Append(':'); }
            if (name is not null) { Key("name"); AsString(name); }
            if (target is not null)
            {
                Key("target"); AsString(target);
                if (editList is not null && edits.Count > 0) throw Fail(module, "a module has edits or compact bindings, not both");
                Key("edits");
                if (editList is not null)
                {
                    if (editList.Kind != Kind.Seq) throw Fail(editList, "edits is a list");
                    _o.Append('[');
                    for (int i = 0; i < editList.Seq.Count; i++)
                    {
                        if (i > 0) _o.Append(',');
                        Body(editList.Seq[i]);
                    }
                    _o.Append(']');
                }
                else
                {
                    _o.Append('[');
                    BodyOf(edits, module);
                    _o.Append(']');
                }
            }
            else
            {
                if (editList is not null || edits.Count > 0) throw Fail(module, "an entries module takes nothing else");
                Key("entries");
                if (entries!.Kind == Kind.Null) _o.Append("{}");
                else
                {
                    if (entries.Kind != Kind.Map) throw Fail(entries, "entries is a mapping");
                    NoTag(entries);
                    _o.Append('{');
                    bool firstEntry = true;
                    foreach (var (entry, body) in entries.Map)
                    {
                        if (!firstEntry) _o.Append(',');
                        firstEntry = false;
                        Str(entry); _o.Append(':'); Value(body);
                    }
                    _o.Append('}');
                }
            }
            Key("origin");
            _o.Append("{\"manifest\":\"").Append(OriginManifest).Append("\",\"source\":null,\"module\":").Append(index.ToString(CultureInfo.InvariantCulture)).Append('}');
            _o.Append('}');
        }

        private void NoTag(Node n)
        {
            if (n.Tag is not null && !n.Tag.StartsWith("!!", StringComparison.Ordinal))
                throw Fail(n, $"the tag {n.Tag} does not belong here");
        }

        /// <summary>One edit (a mapping of bindings): <c>objects</c> takes class tags; every other value is a value.</summary>
        private void Body(Node edit)
        {
            if (edit.Kind != Kind.Map) throw Fail(edit, "an edit is a mapping");
            NoTag(edit);
            BodyOf(edit.Map, edit);
        }

        private void BodyOf(IReadOnlyList<(string Key, Node Value)> members, Node owner)
        {
            _ = owner;
            _o.Append('{');
            bool first = true;
            foreach (var (key, value) in members)
            {
                if (!first) _o.Append(',');
                first = false;
                Str(key); _o.Append(':');
                if (key == "objects") Objects(value);
                else if (key is "overrides" or "links" or "-links") StringList(value);
                else Value(value);
            }
            _o.Append('}');
        }

        private void Objects(Node objects)
        {
            if (objects.Kind == Kind.Null) { _o.Append("null"); return; }
            if (objects.Kind != Kind.Map) { Value(objects); return; }
            NoTag(objects);
            _o.Append('{');
            bool first = true;
            foreach (var (key, body) in objects.Map)
            {
                if (!first) _o.Append(',');
                first = false;
                Str(key); _o.Append(':');
                ObjectBody(body);
            }
            _o.Append('}');
        }

        /// <summary>A node a string field holds: a plain scalar keeps the text it was written with, whatever it looks like.</summary>
        private void AsString(Node n)
        {
            if (n.Kind == Kind.Scalar) Str(n.Text);
            else if (n.Kind == Kind.Null) _o.Append("null");
            else throw Fail(n, "a string is expected here");
        }

        private void StringList(Node n)
        {
            if (n.Kind != Kind.Seq) { Value(n); return; }
            _o.Append('[');
            for (int i = 0; i < n.Seq.Count; i++)
            {
                if (i > 0) _o.Append(',');
                var item = n.Seq[i];
                if (item.Kind == Kind.Scalar && item.Tag is null) Str(item.Text); else Value(item);
            }
            _o.Append(']');
        }

        private void ObjectBody(Node body)
        {
            if (body.Tag is null && body.Kind == Kind.Map)
            {
                _o.Append('{');
                for (int i = 0; i < body.Map.Count; i++)
                {
                    if (i > 0) _o.Append(',');
                    var (k, v) = body.Map[i];
                    Str(k); _o.Append(':');
                    if (k is "class" or "clone" && v.Kind == Kind.Scalar && v.Tag is null) Str(v.Text); else Value(v);
                }
                _o.Append('}');
                return;
            }
            if (body.Tag is null || body.Tag.StartsWith("!!", StringComparison.Ordinal)) { Value(body); return; }
            string cls = body.Tag.TrimStart('!');
            if (TypeNames.Contains(cls) || cls == "ref" || StructTag(cls, out _, out _))
                throw Fail(body, $"{body.Tag} on an object body is not a class tag");
            _o.Append("{\"class\":"); Str(cls);
            if (body.Kind != Kind.Null) { _o.Append(",\"set\":"); Untagged(body); }
            _o.Append('}');
        }

        private static bool StructTag(string name, out string pin, out string? cls)
        {
            pin = name; cls = null;
            int paren = name.IndexOf('(');
            if (paren >= 0)
            {
                if (!name.EndsWith(')') || paren + 1 >= name.Length - 1) return false;
                pin = name[..paren];
                cls = name[(paren + 1)..^1];
            }
            return pin is "pointer" or "embed";
        }

        /// <summary>A value: a tag becomes the one-key mapping of its name (or a struct pin).</summary>
        private void Value(Node n)
        {
            if (n.Tag is null || n.Tag.StartsWith("!!", StringComparison.Ordinal)) { Untagged(n); return; }
            string name = n.Tag.TrimStart('!');
            if (StructTag(name, out string pin, out string? cls))
            {
                _o.Append("{\"").Append(pin).Append("\":");
                if (cls is null && n.Kind == Kind.Null) _o.Append("null");
                else
                {
                    _o.Append('{');
                    bool any = false;
                    if (cls is not null) { _o.Append("\"class\":"); Str(cls); any = true; }
                    if (n.Kind != Kind.Null)
                    {
                        if (any) _o.Append(',');
                        _o.Append("\"set\":"); Untagged(n);
                    }
                    _o.Append('}');
                }
                _o.Append('}');
                return;
            }
            if (!TypeNames.Contains(name) && name != "ref") throw Fail(n, $"unknown type pin {n.Tag}");
            _o.Append("{\"").Append(name).Append("\":"); Untagged(n); _o.Append('}');
        }

        private void Untagged(Node n)
        {
            switch (n.Kind)
            {
                case Kind.Null:
                    _o.Append("null");
                    break;
                case Kind.Seq:
                    _o.Append('[');
                    for (int i = 0; i < n.Seq.Count; i++) { if (i > 0) _o.Append(','); Value(n.Seq[i]); }
                    _o.Append(']');
                    break;
                case Kind.Map:
                    _o.Append('{');
                    for (int i = 0; i < n.Map.Count; i++)
                    {
                        if (i > 0) _o.Append(',');
                        Str(n.Map[i].Key); _o.Append(':'); Value(n.Map[i].Value);
                    }
                    _o.Append('}');
                    break;
                default:
                    Scalar(n);
                    break;
            }
        }

        private void Scalar(Node n)
        {
            if (!n.Plain || n.Tag == "!!str") { Str(n.Text); return; }
            string t = n.Text;
            switch (t)
            {
                case "null": case "Null": case "NULL": case "~": _o.Append("null"); return;
                case "true": case "True": case "TRUE": _o.Append("true"); return;
                case "false": case "False": case "FALSE": _o.Append("false"); return;
            }
            if (TryNumber(t, out string? json, out bool nonFinite))
            {
                _o.Append(json);
                return;
            }
            if (nonFinite) throw Fail(n, $"{t} is not a finite number, and a declaration cannot hold it");
            Str(t);
        }

        private static bool TryNumber(string t, out string json, out bool nonFinite)
        {
            json = ""; nonFinite = false;
            if (t.Length == 0) return false;
            string u = t.TrimStart('+', '-');
            if (u is ".inf" or ".Inf" or ".INF" or ".nan" or ".NaN" or ".NAN") { nonFinite = t.Length - u.Length <= 1; return false; }
            bool neg = t[0] == '-';
            if (t.Length > 2 && t[0] == '0' && t[1] is 'x' && t.Skip(2).All(Uri.IsHexDigit))
                return Big(BigInteger.Parse("0" + t[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture), false, out json);
            if (t.Length > 2 && t[0] == '0' && t[1] is 'o' && t.Skip(2).All(c => c is >= '0' and <= '7'))
            {
                BigInteger v = BigInteger.Zero;
                foreach (char c in t.Skip(2)) v = v * 8 + (c - '0');
                return Big(v, false, out json);
            }
            // [-+]? ( [0-9]+ | [0-9]* '.' [0-9]* ) ( [eE] [-+]? [0-9]+ )?
            int i = 0;
            if (t[0] is '-' or '+') i++;
            int intStart = i;
            while (i < t.Length && char.IsAsciiDigit(t[i])) i++;
            string intPart = t[intStart..i];
            string frac = "", exp = "";
            bool isFloat = false;
            if (i < t.Length && t[i] == '.')
            {
                isFloat = true;
                i++;
                int fs = i;
                while (i < t.Length && char.IsAsciiDigit(t[i])) i++;
                frac = t[fs..i];
            }
            if (intPart.Length == 0 && frac.Length == 0) return false;
            if (i < t.Length && t[i] is 'e' or 'E')
            {
                isFloat = true;
                i++;
                int es = i;
                if (i < t.Length && t[i] is '+' or '-') i++;
                int ds = i;
                while (i < t.Length && char.IsAsciiDigit(t[i])) i++;
                if (i == ds) return false;
                exp = t[es..i];
            }
            if (i != t.Length) return false;
            if (!isFloat) return Big(BigInteger.Parse(intPart, CultureInfo.InvariantCulture) * (neg ? -1 : 1), true, out json);
            string mantissa = (intPart.Length == 0 ? "0" : intPart.TrimStart('0') is { Length: > 0 } s ? s : "0") + "." + (frac.Length == 0 ? "0" : frac);
            string number = (neg ? "-" : "") + mantissa + (exp.Length > 0 ? "e" + exp : "");
            if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || double.IsInfinity(d)) { nonFinite = true; return false; }
            json = number;
            return true;
        }

        /// <summary>An integer in the union of the i64 and u64 ranges is an integer; one past them is a float (ltk_game_data's Value::Float).</summary>
        private static bool Big(BigInteger v, bool _, out string json)
        {
            if (v >= long.MinValue && v <= ulong.MaxValue) { json = v.ToString(CultureInfo.InvariantCulture); return true; }
            json = ((double)v).ToString("R", CultureInfo.InvariantCulture);
            if (!json.Contains('.') && !json.Contains('E') && !json.Contains('e')) json += ".0";
            return true;
        }

        private void Str(string s) => _o.Append(JsonSerializer.Serialize(s, StrOptions));

        private static readonly JsonSerializerOptions StrOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    }
}
