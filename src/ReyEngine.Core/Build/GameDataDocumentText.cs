using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReyEngine.Core.Build;

/// <summary>
/// M816: one module of a GameData document, as the text it was written in.
/// </summary>
/// <param name="Index">Its position in <c>modules</c>, from 0.</param>
/// <param name="Text">The module's JSON exactly as the document spells it, from its <c>{</c> to its <c>}</c>:
/// whitespace, key order, number tokens and string escapes untouched.</param>
/// <param name="Name">Its <c>name</c> when it has one. A label for a manager's declarations view; it carries no meaning
/// for applying.</param>
/// <param name="Target">Its <c>target</c> (a game path, or 16 hexadecimal digits) when it is a <c>target</c> module;
/// null for an <c>entries</c> module.</param>
public sealed record GameDataModuleText(int Index, string Text, string? Name, string? Target)
{
    /// <summary>Whether this module names its chunk (<c>target</c>) and not entries (<c>entries</c>).</summary>
    public bool IsTarget => Target is not null;
}

/// <summary>
/// M816: a layer's GameData document (<c>Layers.&lt;name&gt;.GameData</c> of a .fantome's <c>META/info.json</c>) as TEXT.
///
/// <para>The document is <c>ltk_game_data</c>'s <c>DeclarationDocument</c>: <c>{"version": 1, "modules": [...]}</c>
/// and nothing else (the crate refuses unknown keys). What an import stores and an export writes back is this text, not
/// a parsed form, because a parsed form decides things the author wrote differently: <c>JsonNode</c> keeps number
/// tokens (M814 measured it) but writes string escapes its own way. A module's <c>name</c>, its <c>origin</c>, the order of
/// its keys and the number <c>1.0</c> stay exactly as they were written when the text is.</para>
///
/// <para>This class only finds where the modules are: it reads the text once with a forward-only reader and
/// remembers each module's byte range, so a module can be taken out as the string the author wrote, or have ReyEngine's own
/// modules written behind them (<see cref="WriteTo"/>) without the imported ones being re-rendered.</para>
///
/// <para>Limits: nothing here checks that a module is VALID - which targets exist, whether an edit applies, whether a
/// value coerces. That is the apply engine's job (M817). The text is held as it arrived; one that is not
/// <see cref="IsExpectedShape"/> (a document with another key, a <c>modules</c> that is not a list) is carried and written
/// back unchanged, and reports no modules.</para>
/// </summary>
public sealed class GameDataDocumentText
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The nesting depth read and written; <c>serde_json</c> stops at 128 for a whole info.json, so this is generous.</summary>
    public const int MaxDepth = 256;

    private GameDataDocumentText(string text, string? versionText, IReadOnlyList<GameDataModuleText> modules, bool expectedShape)
    {
        Text = text;
        VersionText = versionText;
        Modules = modules;
        IsExpectedShape = expectedShape;
    }

    /// <summary>The document, as it was given.</summary>
    public string Text { get; }

    /// <summary>The <c>version</c> token as written (<c>1</c>); null when the document has none.</summary>
    public string? VersionText { get; }

    /// <summary>The modules in execution order; empty when the document is not <see cref="IsExpectedShape"/>.</summary>
    public IReadOnlyList<GameDataModuleText> Modules { get; }

    /// <summary>True for an object holding exactly <c>version</c> and <c>modules</c>, with <c>modules</c> a list of objects.</summary>
    public bool IsExpectedShape { get; }

    /// <summary>Reads <paramref name="text"/>, which must be one JSON value.</summary>
    /// <exception cref="JsonException">The text is not JSON.</exception>
    public static GameDataDocumentText Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        byte[] utf8 = Utf8.GetBytes(text);
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = MaxDepth });

        string? version = null;
        var modules = new List<GameDataModuleText>();
        bool shape = true, sawVersion = false, sawModules = false;

        if (!reader.Read()) throw new JsonException("The document is empty.");
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            shape = false;
            reader.Skip();
        }
        else
        {
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                string key = reader.GetString()!;
                reader.Read();

                if (key == "version" && !sawVersion)
                {
                    sawVersion = true;
                    if (reader.TokenType == JsonTokenType.Number) version = Utf8.GetString(reader.ValueSpan);
                    else shape = false;
                    reader.Skip();
                }
                else if (key == "modules" && !sawModules)
                {
                    sawModules = true;
                    if (reader.TokenType != JsonTokenType.StartArray) { shape = false; reader.Skip(); continue; }
                    int index = 0;
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        if (reader.TokenType != JsonTokenType.StartObject) { shape = false; reader.Skip(); index++; continue; }
                        int start = checked((int)reader.TokenStartIndex);
                        reader.Skip();
                        int end = checked((int)reader.BytesConsumed);
                        modules.Add(Describe(index++, Utf8.GetString(utf8, start, end - start)));
                    }
                }
                else
                {
                    shape = false;   // an unknown (or repeated) key: the crate refuses the document
                    reader.Skip();
                }
            }
        }

        while (reader.Read()) { }   // the rest of the text is JSON too, or this throws
        if (!sawVersion || !sawModules) shape = false;
        if (!shape) modules.Clear();
        return new GameDataDocumentText(text, version, modules, shape);
    }

    private static GameDataModuleText Describe(int index, string moduleText)
    {
        string? name = null, target = null;
        using var doc = JsonDocument.Parse(moduleText, new JsonDocumentOptions { MaxDepth = MaxDepth });
        if (doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) name = n.GetString();
        if (doc.RootElement.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.String) target = t.GetString();
        return new GameDataModuleText(index, moduleText, name, target);
    }

    /// <summary>
    /// The override files the document names, in document order, each once: the strings of every <c>overrides</c> list of a target
    /// module's edits (<c>ltk_game_data</c>'s first phase of an edit). A layer-relative path with '/' separators; a loader finds the
    /// file at <c>content/&lt;layer&gt;/&lt;path&gt;</c>, a .fantome keeps it at <c>META/game_data/&lt;layer&gt;/&lt;path&gt;</c>.
    /// A value that is not a list of strings is not a path and is left out; nothing checks that a path is a valid one.
    /// </summary>
    public IReadOnlyList<string> OverridePaths()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var paths = new List<string>();
        foreach (var module in Modules)
        {
            using var doc = JsonDocument.Parse(module.Text, new JsonDocumentOptions { MaxDepth = MaxDepth });
            if (!doc.RootElement.TryGetProperty("edits", out var edits) || edits.ValueKind != JsonValueKind.Array) continue;
            foreach (var edit in edits.EnumerateArray())
                if (edit.ValueKind == JsonValueKind.Object && edit.TryGetProperty("overrides", out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var item in list.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String && item.GetString() is { } path && seen.Add(path)) paths.Add(path);
        }
        return paths;
    }

    /// <summary>
    /// A document module as a manifest module: the same JSON on one line, with the top-level member <c>origin</c> left out.
    ///
    /// <para>A manifest module has no <c>origin</c> - a loader works it out from the file and the module's position - and a
    /// key that is not a manifest key is read as an entry name, which refuses the layer. Every other token is copied from the text
    /// as it stands: a number keeps its spelling, a string its escapes, and only the whitespace between tokens goes.</para>
    ///
    /// <para><b>One escape is not copied</b> (review): a character outside the Basic Multilingual Plane that a source spelled the way JSON
    /// does, as a PAIR of surrogate escapes (an emoji is two <c>\u</c> escapes), is written as the character itself. JSON reads the pair as one
    /// character; YAML 1.2 reads each <c>\u</c> escape as ONE code point, and half of a pair is not a Unicode scalar value, so a strict YAML
    /// reader has no reading for the line and may refuse it. The character is exactly what the pair means, so the manifest says what the
    /// document says, in a spelling every YAML reader takes; the stored JSON keeps the pair. Measured against league-mod's own loader
    /// (<c>serde_saphyr</c>): it joins the pair, and the manifest loads either way, so for LTK Manager this changes nothing - it is for any
    /// other reader of the file. A surrogate escape that is not half of a pair is copied as written (serde_json refuses it in the document
    /// too), and a character a source wrote raw stays raw.</para>
    /// </summary>
    /// <param name="moduleText">One module, as <see cref="GameDataModuleText.Text"/> has it.</param>
    /// <exception cref="JsonException">The text is not a JSON object.</exception>
    public static string ManifestModule(string moduleText)
    {
        ArgumentNullException.ThrowIfNull(moduleText);
        byte[] utf8 = Utf8.GetBytes(moduleText);
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = MaxDepth });
        using var output = new MemoryStream(utf8.Length);

        void Quoted(ReadOnlySpan<byte> escaped) { output.WriteByte((byte)'"'); WriteYamlEscaped(output, escaped); output.WriteByte((byte)'"'); }

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new JsonException("A module is a JSON object.");
        output.WriteByte((byte)'{');
        bool first = true;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            bool drop = reader.ValueTextEquals("origin"u8);
            if (!drop)
            {
                if (!first) output.WriteByte((byte)',');
                first = false;
                Quoted(reader.ValueSpan);
                output.WriteByte((byte)':');
            }
            reader.Read();
            CopyValue(ref reader, output, copy: !drop);
        }
        output.WriteByte((byte)'}');
        while (reader.Read()) { }
        return Utf8.GetString(output.ToArray());

        // one value, with every token of it, to the end of that value
        static void CopyValue(ref Utf8JsonReader reader, MemoryStream output, bool copy)
        {
            int depth = 0;
            bool needComma = false, afterName = false;
            do
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        if (copy) { if (needComma && !afterName) output.WriteByte((byte)','); output.WriteByte(reader.TokenType == JsonTokenType.StartObject ? (byte)'{' : (byte)'['); }
                        depth++; needComma = false; afterName = false;
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        if (copy) output.WriteByte(reader.TokenType == JsonTokenType.EndObject ? (byte)'}' : (byte)']');
                        depth--; needComma = true; afterName = false;
                        break;
                    case JsonTokenType.PropertyName:
                        if (copy)
                        {
                            if (needComma) output.WriteByte((byte)',');
                            output.WriteByte((byte)'"'); WriteYamlEscaped(output, reader.ValueSpan); output.WriteByte((byte)'"'); output.WriteByte((byte)':');
                        }
                        needComma = false; afterName = true;
                        break;
                    case JsonTokenType.String:
                        if (copy) { if (needComma && !afterName) output.WriteByte((byte)','); output.WriteByte((byte)'"'); WriteYamlEscaped(output, reader.ValueSpan); output.WriteByte((byte)'"'); }
                        needComma = true; afterName = false;
                        break;
                    default:   // a number, true, false, null: copied as written
                        if (copy) { if (needComma && !afterName) output.WriteByte((byte)','); output.Write(reader.ValueSpan); }
                        needComma = true; afterName = false;
                        break;
                }
                if (depth == 0) return;
            } while (reader.Read());
        }
    }

    /// <summary>
    /// The text of a JSON string as written (the bytes between its quotes) for a YAML double-quoted string: every escape is copied as it
    /// stands - <c>\\</c> and <c>\"</c> are read in pairs, so <c>\\ud83d</c> (a backslash, then the letters) is not mistaken for an escape - except
    /// a PAIR of surrogate escapes, which becomes the character it spells (see <see cref="ManifestModule"/>).
    /// </summary>
    private static void WriteYamlEscaped(Stream output, ReadOnlySpan<byte> escaped)
    {
        if (escaped.IndexOf("\\u"u8) < 0) { output.Write(escaped); return; }

        Span<byte> scalar = stackalloc byte[4];
        int i = 0;
        while (i < escaped.Length)
        {
            byte b = escaped[i];
            if (b != (byte)'\\' || i + 1 >= escaped.Length) { output.WriteByte(b); i++; continue; }

            if (escaped[i + 1] == (byte)'u' && i + 12 <= escaped.Length
                && TryHex4(escaped.Slice(i + 2, 4), out int high) && high is >= 0xD800 and <= 0xDBFF
                && escaped[i + 6] == (byte)'\\' && escaped[i + 7] == (byte)'u'
                && TryHex4(escaped.Slice(i + 8, 4), out int low) && low is >= 0xDC00 and <= 0xDFFF)
            {
                var rune = new Rune(0x10000 + ((high - 0xD800) << 10) + (low - 0xDC00));
                output.Write(scalar[..rune.EncodeToUtf8(scalar)]);
                i += 12;
                continue;
            }

            output.WriteByte(b);                 // any other escape is one unit: its two bytes, as written
            output.WriteByte(escaped[i + 1]);
            i += 2;
        }
    }

    private static bool TryHex4(ReadOnlySpan<byte> digits, out int value)
    {
        value = 0;
        foreach (byte d in digits)
        {
            int v = d is >= (byte)'0' and <= (byte)'9' ? d - '0'
                  : d is >= (byte)'a' and <= (byte)'f' ? d - 'a' + 10
                  : d is >= (byte)'A' and <= (byte)'F' ? d - 'A' + 10 : -1;
            if (v < 0) return false;
            value = (value << 4) | v;
        }
        return true;
    }

    /// <summary>
    /// M823: one module of ReyEngine's own as a node, numbered for the place it takes in a document: its <c>origin.module</c> is
    /// <paramref name="index"/> (the first free place after the modules in front of it), the rest of the module is kept as it was
    /// written - number tokens, key order, string escapes. A module with no <c>origin</c> gets the one the document requires
    /// (<c>manifest: game_data.yaml</c>, no source), because the loader refuses a module without one.
    /// </summary>
    /// <exception cref="JsonException">The text is not a JSON object.</exception>
    public static JsonNode ModuleNode(string moduleText, int index)
    {
        ArgumentNullException.ThrowIfNull(moduleText);
        var node = JsonNode.Parse(moduleText, documentOptions: new JsonDocumentOptions { MaxDepth = MaxDepth }) as JsonObject
                   ?? throw new JsonException("A module is a JSON object.");
        if (node["origin"] is JsonObject origin) origin["module"] = index;
        else node["origin"] = new JsonObject { ["manifest"] = "game_data.yaml", ["source"] = null, ["module"] = index };
        return node;
    }

    /// <summary>
    /// M823: this document with ReyEngine's <paramref name="own"/> modules behind the ones it holds, as one compact text. The
    /// imported modules go in as their own text, byte for byte (<see cref="WriteTo"/>); what the preview applies, so that the
    /// edits made on top of an imported package run exactly where an export will put them.
    /// </summary>
    public string WithModules(IReadOnlyList<JsonNode> own)
    {
        ArgumentNullException.ThrowIfNull(own);
        if (own.Count == 0) return Text;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, MaxDepth = MaxDepth, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            WriteTo(writer, own);
        return Utf8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Writes the document as a JSON value into <paramref name="writer"/>, with <paramref name="own"/> modules behind the
    /// ones it holds.
    ///
    /// <para>With nothing to add, the text goes out as it is. With modules to add to a document of the expected shape, the
    /// wrapper (<c>version</c> and the list) is laid out the way <paramref name="writer"/> lays out what it writes at its
    /// depth - the same indentation, the same line breaks - and every imported module goes into it as its own text, byte for byte,
    /// followed by the new ones, so a document a pretty printer wrote reads, with ReyEngine's modules added, as the pretty printer
    /// would have written all of them. (<see cref="Utf8JsonWriter.WriteRawValue(string, bool)"/> adds no indentation of its own,
    /// which is why the wrapper is not left to the writer.) A document that is not of the expected shape cannot be taken apart;
    /// the modules are added to its parsed form instead (number tokens kept), which only a document the loader would refuse is
    /// ever put through.</para>
    /// </summary>
    /// <param name="own">ReyEngine's own modules, each a JSON object whose <c>origin.module</c> already counts past the
    /// imported ones.</param>
    public void WriteTo(Utf8JsonWriter writer, IReadOnlyList<JsonNode> own)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(own);

        if (own.Count == 0)
        {
            writer.WriteRawValue(Text, skipInputValidation: true);
            return;
        }

        if (!IsExpectedShape)
        {
            var node = JsonNode.Parse(Text, documentOptions: new JsonDocumentOptions { MaxDepth = MaxDepth }) as JsonObject ?? new JsonObject();
            if (node["modules"] is not JsonArray list) node["modules"] = list = new JsonArray();
            foreach (var m in own) list.Add(m.DeepClone());
            node.WriteTo(writer);
            return;
        }

        var options = writer.Options;
        string nl = options.NewLine, unit = new string(options.IndentCharacter, options.IndentSize);
        string at(int levels) => options.Indented ? string.Concat(Enumerable.Repeat(unit, writer.CurrentDepth + levels)) : "";
        string line = options.Indented ? nl : "";
        string colon = options.Indented ? ": " : ":";

        var sb = new StringBuilder();
        sb.Append('{').Append(line);
        sb.Append(at(1)).Append("\"version\"").Append(colon).Append(VersionText ?? "1").Append(',').Append(line);
        sb.Append(at(1)).Append("\"modules\"").Append(colon).Append('[').Append(line);
        int total = Modules.Count + own.Count, written = 0;
        foreach (var m in Modules)
            sb.Append(at(2)).Append(m.Text).Append(++written < total ? "," : "").Append(line);
        var ownOptions = new JsonSerializerOptions
        {
            WriteIndented = options.Indented,
            NewLine = nl,
            Encoder = options.Encoder,
        };
        foreach (var m in own)
        {
            string text = m.ToJsonString(ownOptions);
            // a JSON string holds no raw line break, so every line break of the text is a place to indent
            sb.Append(at(2)).Append(options.Indented ? text.Replace(nl, nl + at(2)) : text).Append(++written < total ? "," : "").Append(line);
        }
        sb.Append(at(1)).Append(']').Append(line);
        sb.Append(at(0)).Append('}');
        writer.WriteRawValue(sb.ToString(), skipInputValidation: true);
    }
}
