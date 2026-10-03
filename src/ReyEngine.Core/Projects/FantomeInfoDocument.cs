using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReyEngine.Core.Build;

namespace ReyEngine.Core.Projects;

/// <summary>M816: one entry of a .fantome's <c>Layers</c> table, as read from <c>META/info.json</c>.</summary>
/// <param name="Key">The table key.</param>
/// <param name="Name">The layer's name: its <c>Name</c>, or the key when that is empty or absent (as <c>ltk_fantome</c>'s
/// <c>declare_layers</c> resolves it).</param>
/// <param name="DisplayName"><c>DisplayName</c>, or null.</param>
/// <param name="Priority"><c>Priority</c>; 0 when the entry has none.</param>
/// <param name="GameDataText">The layer's <c>GameData</c> exactly as the file spells it - the text from its <c>{</c> to its
/// <c>}</c> - or null when it has none (an absent key and <c>null</c> both).</param>
/// <param name="StringOverrides"><c>StringOverrides</c> (locale, field, text) with its key order, or null.</param>
/// <param name="UnknownKeys">Keys of the entry this reader does not carry.</param>
public sealed record FantomeInfoLayer(
    string Key, string Name, string? DisplayName, int Priority, string? GameDataText, JsonObject? StringOverrides,
    IReadOnlyList<string> UnknownKeys);

/// <summary>M816: one <c>Hashtables</c> manifest entry (<c>ltk_fantome</c>'s <c>FantomeHashtable</c>).</summary>
public sealed record FantomeInfoHashtable(string Path, string Category, string Algorithm, int Bits);

/// <summary>
/// M816: <c>META/info.json</c> of a .fantome, read the way <c>ltk_fantome</c> 0.15.1 reads it - but tolerantly. League-mod's
/// <c>serde_json</c> reader makes any mistake (a missing <c>Author</c>, a <c>Priority</c> that is not an integer) fail the whole
/// mod; an import can still take what is there, so this reads field by field, keeps what it understands and records what it
/// did not in <see cref="Problems"/> and <see cref="UnknownKeys"/>.
///
/// <para>What it reads: <c>Name</c>, <c>Author</c>, <c>Version</c>, <c>Description</c>, <c>Heart</c>/<c>Home</c> (cslol-manager's),
/// <c>License</c> (a string, or <c>{Name, Url?}</c>), <c>Tags</c>, <c>Champions</c>, <c>Maps</c>, <c>Layers</c> (each: <c>Name</c>,
/// <c>DisplayName</c>, <c>Priority</c>, <c>GameData</c>, <c>StringOverrides</c>), <c>Hashtables</c> and <c>Generator</c>. A layer's
/// <c>GameData</c> is kept as TEXT (<see cref="FantomeInfoLayer.GameDataText"/>): the span of the file that holds it, which
/// <see cref="JsonElement.GetRawText"/> returns as written.</para>
///
/// <para>The caller supplies the entry's text, decoded (a byte order mark is the decoder's to remove); the name of the entry is
/// matched case-insensitively by whoever opens the archive.</para>
/// </summary>
public sealed class FantomeInfoDocument
{
    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        "Name", "Author", "Version", "Description", "Heart", "Home", "License", "Tags", "Champions", "Maps", "Layers",
        "Hashtables", "Generator",
    };

    private static readonly HashSet<string> KnownLayerKeys = new(StringComparer.Ordinal)
    {
        "GameData", "Name", "DisplayName", "Priority", "StringOverrides",
    };

    public string? Name { get; private set; }
    public string? Author { get; private set; }
    public string? Version { get; private set; }
    public string? Description { get; private set; }
    public string? Heart { get; private set; }
    public string? Home { get; private set; }
    public string? Generator { get; private set; }
    public ProjectLicense? License { get; private set; }
    public IReadOnlyList<string> Tags => _tags;
    public IReadOnlyList<string> Champions => _champions;
    public IReadOnlyList<string> Maps => _maps;

    /// <summary>The layer table in file order.</summary>
    public IReadOnlyList<FantomeInfoLayer> Layers => _layers;

    public IReadOnlyList<FantomeInfoHashtable> Hashtables => _hashtables;

    /// <summary>Top-level keys this reader does not carry. A package from another tool may hold some.</summary>
    public IReadOnlyList<string> UnknownKeys => _unknownKeys;

    /// <summary>What was found and not understood, one plain sentence each: a value of the wrong type, a table that is
    /// not a table. Nothing here stopped the read.</summary>
    public IReadOnlyList<string> Problems => _problems;

    private readonly List<string> _tags = new(), _champions = new(), _maps = new(), _unknownKeys = new(), _problems = new();
    private readonly List<FantomeInfoLayer> _layers = new();
    private readonly List<FantomeInfoHashtable> _hashtables = new();

    private FantomeInfoDocument() { }

    /// <summary>Reads the file's text; null when it is not a JSON object (the caller then names the mod after the file, as an import
    /// always has).</summary>
    public static FantomeInfoDocument? TryRead(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = GameDataDocumentText.MaxDepth }); }
        catch (JsonException) { return null; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var info = new FantomeInfoDocument();
            foreach (var property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "Name": info.Name = info.Text(property, "Name"); break;
                    case "Author": info.Author = info.Text(property, "Author"); break;
                    case "Version": info.Version = info.Text(property, "Version", numbers: true); break;
                    case "Description": info.Description = info.Text(property, "Description"); break;
                    case "Heart": info.Heart = info.Text(property, "Heart"); break;
                    case "Home": info.Home = info.Text(property, "Home"); break;
                    case "Generator": info.Generator = info.Text(property, "Generator"); break;
                    case "License": info.License = info.ReadLicense(property.Value); break;
                    case "Tags": info.ReadStrings(property.Value, "Tags", info._tags); break;
                    case "Champions": info.ReadStrings(property.Value, "Champions", info._champions); break;
                    case "Maps": info.ReadStrings(property.Value, "Maps", info._maps); break;
                    case "Layers": info.ReadLayers(property.Value); break;
                    case "Hashtables": info.ReadHashtables(property.Value); break;
                    default: if (!KnownKeys.Contains(property.Name)) info._unknownKeys.Add(property.Name); break;
                }
            }
            return info;
        }
    }

    private string? Text(JsonProperty property, string label, bool numbers = false)
    {
        switch (property.Value.ValueKind)
        {
            case JsonValueKind.String: return property.Value.GetString();
            case JsonValueKind.Null: return null;
            case JsonValueKind.Number when numbers: return property.Value.GetRawText();
            default:
                _problems.Add($"{label} is not text, so it was not read.");
                return null;
        }
    }

    private ProjectLicense? ReadLicense(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null: return null;
            case JsonValueKind.String: return new ProjectLicense { Name = value.GetString() ?? "", AsObject = false };
            case JsonValueKind.Object:
                string? name = null, url = null;
                foreach (var p in value.EnumerateObject())
                {
                    if (p.Name == "Name" && p.Value.ValueKind == JsonValueKind.String) name = p.Value.GetString();
                    else if (p.Name == "Url" && p.Value.ValueKind == JsonValueKind.String) url = p.Value.GetString();
                    else if (p.Name == "Url" && p.Value.ValueKind == JsonValueKind.Null) { }
                    else _problems.Add($"License has a '{p.Name}' that ltk_fantome does not accept, so it was not read.");
                }
                if (name is null) { _problems.Add("License is an object without a Name, so it was not read."); return null; }
                return new ProjectLicense { Name = name, Url = url, AsObject = true };
            default:
                _problems.Add("License is neither text nor an object, so it was not read.");
                return null;
        }
    }

    private void ReadStrings(JsonElement value, string label, List<string> into)
    {
        if (value.ValueKind == JsonValueKind.Null) return;
        if (value.ValueKind != JsonValueKind.Array) { _problems.Add($"{label} is not a list, so it was not read."); return; }
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s) into.Add(s);
            else _problems.Add($"{label} holds a value that is not text, which was left out.");
        }
    }

    private void ReadHashtables(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return;
        if (value.ValueKind != JsonValueKind.Array) { _problems.Add("Hashtables is not a list, so it was not read."); return; }
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("Path", out var path) || path.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("Category", out var category) || category.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("Algorithm", out var algorithm) || algorithm.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("Bits", out var bits) || !bits.TryGetInt32(out int width))
            {
                _problems.Add("A Hashtables entry lacks its Path, Category, Algorithm or Bits, so it was not read.");
                continue;
            }
            _hashtables.Add(new FantomeInfoHashtable(path.GetString()!, category.GetString()!, algorithm.GetString()!, width));
        }
    }

    private void ReadLayers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return;
        if (value.ValueKind != JsonValueKind.Object) { _problems.Add("Layers is not a table, so no layer was read."); return; }
        foreach (var entry in value.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object)
            {
                _problems.Add($"Layer '{entry.Name}' is not an object, so it was not read.");
                continue;
            }

            string? name = null, display = null, gameData = null;
            int priority = 0;
            JsonObject? overrides = null;
            var unknown = new List<string>();
            foreach (var p in entry.Value.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "Name" when p.Value.ValueKind == JsonValueKind.String: name = p.Value.GetString(); break;
                    case "DisplayName" when p.Value.ValueKind is JsonValueKind.String: display = p.Value.GetString(); break;
                    case "DisplayName" when p.Value.ValueKind is JsonValueKind.Null: break;
                    case "Priority":
                        if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out int pr)) priority = pr;
                        else _problems.Add($"Layer '{entry.Name}' has a Priority that is not a whole number, so 0 was used.");
                        break;
                    case "GameData":
                        if (p.Value.ValueKind == JsonValueKind.Object) gameData = p.Value.GetRawText();
                        else if (p.Value.ValueKind != JsonValueKind.Null)
                            _problems.Add($"Layer '{entry.Name}' has a GameData that is not an object, so it was not read.");
                        break;
                    case "StringOverrides":
                        if (p.Value.ValueKind == JsonValueKind.Object) overrides = ReadOverrides(entry.Name, p.Value);
                        else if (p.Value.ValueKind != JsonValueKind.Null)
                            _problems.Add($"Layer '{entry.Name}' has StringOverrides that are not an object, so they were not read.");
                        break;
                    default:
                        if (KnownLayerKeys.Contains(p.Name)) _problems.Add($"Layer '{entry.Name}' has a {p.Name} of the wrong type, so it was not read.");
                        else unknown.Add(p.Name);
                        break;
                }
            }

            if (overrides is { Count: 0 }) overrides = null;
            _layers.Add(new FantomeInfoLayer(entry.Name, string.IsNullOrEmpty(name) ? entry.Name : name, display, priority, gameData, overrides, unknown));
        }
    }

    /// <summary>
    /// A layer's string overrides, in the one shape LTK has for them: <c>locale -&gt; field -&gt; text</c>, objects of objects of strings
    /// (<c>ltk_fantome</c>'s <c>FantomeLayerInfo::string_overrides</c> is an <c>IndexMap&lt;String, IndexMap&lt;String, String&gt;&gt;</c>, and serde
    /// refuses every other shape). Key order is kept. A key repeated in one object keeps its first place and its last value, as an
    /// <c>IndexMap</c> does. Anything else - a locale that is not an object, a field that is not text (a number, a list, a table, null) - is a
    /// problem noted for the layer and the layer's string overrides are not read; its other content is imported.
    ///
    /// <para>M816 review (round 3): this replaces a parse of whatever the file held. That parse stopped at 64 levels with an exception nothing
    /// caught, and a table of 63 levels passed it and was then too deep for the serializers that write it into project.json and
    /// <c>mod.config.json</c>, which add levels of their own: the import succeeded and the first Save threw. With the shape fixed the
    /// depth is two, whatever the package holds.</para>
    /// </summary>
    private JsonObject? ReadOverrides(string layer, JsonElement value)
    {
        var locales = new JsonObject();
        try
        {
            foreach (var locale in value.EnumerateObject())
            {
                if (locale.Value.ValueKind != JsonValueKind.Object)
                    return Refuse(layer, $"locale '{Brief(locale.Name)}' is {Kind(locale.Value)}, not a table of fields");
                var fields = new JsonObject();
                foreach (var field in locale.Value.EnumerateObject())
                {
                    if (field.Value.ValueKind != JsonValueKind.String)
                        return Refuse(layer, $"'{Brief(locale.Name)}' / '{Brief(field.Name)}' is {Kind(field.Value)}, not text");
                    fields[field.Name] = field.Value.GetString();
                }
                locales[locale.Name] = fields;
            }
        }
        catch (InvalidOperationException)
        {
            // a string that is not valid text (a lone surrogate escape): serde_json refuses it too
            return Refuse(layer, "a text in it is not valid Unicode");
        }
        return locales;
    }

    private JsonObject? Refuse(string layer, string why)
    {
        _problems.Add($"Layer '{layer}' has StringOverrides that are not locale -> field -> text as LTK reads them ({why}), so they were not read.");
        return null;
    }

    private static string Kind(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "a table",
        JsonValueKind.Array => "a list",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Null => "null",
        _ => "text",
    };

    private static string Brief(string text) => text.Length <= 40 ? text : text[..40] + "...";

    /// <summary>Every layer the table declares, base first, then by priority and name as a person reads it - the order a
    /// reader applies them in (<c>ModProjectLayer::apply_order</c>). Names compare without regard to case and the first of two
    /// that tie is kept; the base layer is read at priority 0 whatever the file says.</summary>
    public IReadOnlyList<FantomeInfoLayer> LayersInApplyOrder(out IReadOnlyList<string> dropped)
    {
        var ordered = _layers
            .Select(l => FantomeLayers.IsBase(l.Name) ? l with { Name = FantomeLayers.Base, Priority = 0 } : l)
            .OrderBy(l => FantomeLayers.IsBase(l.Name) ? 0 : 1)
            .ThenBy(l => l.Priority)
            .ThenBy(l => l.Name, Comparer<string>.Create(FantomeLayers.NaturalCompare))
            .ToList();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<FantomeInfoLayer>();
        var lost = new List<string>();
        foreach (var l in ordered)
        {
            if (seen.Add(l.Name)) kept.Add(l);
            else lost.Add($"Layer '{l.Key}' repeats the name '{l.Name}' (names compare without regard to case), so it was left out.");
        }
        dropped = lost;
        return kept;
    }

    /// <summary>The number as text, for a message.</summary>
    internal static string N(int n) => n.ToString(CultureInfo.InvariantCulture);
}
