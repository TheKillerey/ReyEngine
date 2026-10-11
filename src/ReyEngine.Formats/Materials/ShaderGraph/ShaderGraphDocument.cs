using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReyEngine.Formats.Materials.ShaderGraph;

/// <summary>
/// M832: one node of a Shader Graph document. <see cref="Type"/> names a <see cref="SgCatalog"/> definition;
/// <see cref="Props"/> holds that node's own settings as text (the texture or parameter name, a constant's value, a
/// component mask, an unconnected input's value). Unknown types and props are kept as they are - the analyser
/// reports them, the loader never drops them.
/// </summary>
public sealed class SgNode
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }

    /// <summary>M834: an optional name the person gave the node (shown in its title bar instead of the type's). Editor data only: it never
    /// changes what the node computes.</summary>
    public string Label { get; set; } = "";

    public Dictionary<string, string> Props { get; set; } = new(StringComparer.Ordinal);

    public string Prop(string key, string fallback = "") => Props.TryGetValue(key, out var v) ? v : fallback;

    public SgNode Clone() => new() { Id = Id, Type = Type, X = X, Y = Y, Label = Label, Props = new Dictionary<string, string>(Props, StringComparer.Ordinal) };
}

/// <summary>A wire from one node's output pin to another node's input pin, both named (pin names are stable; indices are not).</summary>
public sealed class SgLink
{
    public string FromNode { get; set; } = "";
    public string FromPin { get; set; } = "";
    public string ToNode { get; set; } = "";
    public string ToPin { get; set; } = "";

    public SgLink Clone() => new() { FromNode = FromNode, FromPin = FromPin, ToNode = ToNode, ToPin = ToPin };
    public bool Same(SgLink o) => FromNode == o.FromNode && FromPin == o.FromPin && ToNode == o.ToNode && ToPin == o.ToPin;
}

/// <summary>
/// M834: a parameter the GRAPH declares (Unreal's Scalar / Vector Parameter), as opposed to one the base shader declares in shaders.bin.
/// A custom parameter works in the Shader Graph preview (the value comes from the assigned material's paramValues, else <see cref="Default"/>)
/// and the export DECLARES it: it adds the parameter to the base shader's definition in a shipped shaders.bin (<see cref="SgShadersBin"/>, proven in game by
/// test4), and the material carries it as an ordinary paramValues entry (always a Vector4). Name, width, default and colour flag are all the export needs.
/// </summary>
public sealed class SgCustomParam
{
    public string Name { get; set; } = "";

    /// <summary>1 (float) to 4 (float4).</summary>
    public int Components { get; set; } = 4;

    /// <summary>The default as 1 to 4 numbers separated by spaces (the same text a Constant node holds); <see cref="Components"/> of them.</summary>
    public string Default { get; set; } = "0 0 0 0";

    /// <summary>A float3 / float4 that is a colour (the editors offer a colour picker for it).</summary>
    public bool IsColor { get; set; }

    public SgCustomParam Clone() => new() { Name = Name, Components = Components, Default = Default, IsColor = IsColor };

    /// <summary>The default as numbers, padded or cut to <see cref="Components"/>; zeros when the text does not parse.</summary>
    public float[] DefaultValues()
    {
        var v = new float[Math.Clamp(Components, 1, 4)];
        if (SgCatalog.TryParseConstant(Default, out var parsed))
            for (int i = 0; i < v.Length; i++) v[i] = i < parsed.Length ? parsed[i] : 0f;
        return v;
    }
}

/// <summary>M834: a comment box (Unreal's): a titled rectangle around nodes. Editor data only; it moves with the nodes inside it.</summary>
public sealed class SgComment
{
    public const double MinSize = 80;
    public const double MaxSize = 20000;
    public const string DefaultColor = "Comment";
    public static readonly string[] ColorKeys = { "Comment", "Texture", "Param", "Switch", "Macro", "Shader", "Output" };

    public string Id { get; set; } = "";
    public string Title { get; set; } = "Comment";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 320;
    public double Height { get; set; } = 200;

    /// <summary>A theme palette key (one of <see cref="ColorKeys"/>): the canvas paints it with the theme's own brush, so every palette restyles it.</summary>
    public string Color { get; set; } = DefaultColor;

    public SgComment Clone() => new() { Id = Id, Title = Title, X = X, Y = Y, Width = Width, Height = Height, Color = Color };
}

/// <summary>
/// M832: a custom shader asset - a node graph bound to a BASE Riot shader. It is editor data: it lives in the open
/// project under <c>.reyengine/shadergraphs/</c> (a folder Build Package and the exporters skip) and nothing in a
/// .bin refers to it. Pure data, no GPU and no UI, so it round-trips and tests headlessly.
/// </summary>
public sealed class ShaderGraphDocument
{
    /// <summary>2 (M834): custom parameters, comment boxes, node labels and the Custom / colour nodes. Version 1 files load unchanged (the new lists are empty).</summary>
    public const int CurrentVersion = 2;
    public const int MaxNodes = 4096;
    public const int MaxLinks = 16384;
    public const int MaxFileBytes = 8 * 1024 * 1024;
    public const int MaxPropLength = 1024;
    public const int MaxCodeLength = 4096;
    public const int MaxCustomParams = 64;
    public const int MaxComments = 256;
    public const int MaxLabelLength = 64;
    public const string FileSuffix = ".shadergraph.json";
    public const string FolderName = "shadergraphs";

    public int Version { get; set; } = CurrentVersion;
    public string Name { get; set; } = "NewShaderGraph";

    /// <summary>The Riot shader the graph replaces the pixel stage of, spelled like a material's renderShader
    /// (<c>Shaders/StaticMesh/DefaultEnv_Flat</c>).</summary>
    public string BaseShader { get; set; } = "";

    public List<SgNode> Nodes { get; set; } = new();
    public List<SgLink> Links { get; set; } = new();

    /// <summary>M834: the parameters this graph declares itself (see <see cref="SgCustomParam"/>).</summary>
    public List<SgCustomParam> Params { get; set; } = new();

    /// <summary>M834: the comment boxes.</summary>
    public List<SgComment> Comments { get; set; } = new();

    public SgCustomParam? FindParam(string name) => Params.FirstOrDefault(p => p.Name.Equals(name, StringComparison.Ordinal));

    public SgComment? FindComment(string id) => Comments.FirstOrDefault(c => c.Id == id);

    public string NewCommentId()
    {
        int max = 0;
        foreach (var c in Comments)
            if (c.Id.Length > 1 && c.Id[0] == 'c' && int.TryParse(c.Id.AsSpan(1), out int k)) max = Math.Max(max, k);
        return "c" + (max + 1);
    }

    public SgNode? Find(string id) => Nodes.FirstOrDefault(n => n.Id == id);

    public SgNode? Output => Nodes.FirstOrDefault(n => n.Type == SgCatalog.OutputType);

    public SgLink? LinkInto(string node, string pin) => Links.FirstOrDefault(l => l.ToNode == node && l.ToPin == pin);

    /// <summary>The first unused id of the form <c>n&lt;number&gt;</c>.</summary>
    public string NewId()
    {
        int max = 0;
        foreach (var n in Nodes)
            if (n.Id.Length > 1 && n.Id[0] == 'n' && int.TryParse(n.Id.AsSpan(1), out int k)) max = Math.Max(max, k);
        return "n" + (max + 1);
    }

    public ShaderGraphDocument Clone() => new()
    {
        Version = Version, Name = Name, BaseShader = BaseShader,
        Nodes = Nodes.Select(n => n.Clone()).ToList(),
        Links = Links.Select(l => l.Clone()).ToList(),
        Params = Params.Select(p => p.Clone()).ToList(),
        Comments = Comments.Select(c => c.Clone()).ToList(),
    };

    /// <summary>A graph with the output node and nothing else.</summary>
    public static ShaderGraphDocument CreateEmpty(string name, string baseShader)
    {
        var d = new ShaderGraphDocument { Name = name, BaseShader = baseShader };
        d.Nodes.Add(new SgNode { Id = "n1", Type = SgCatalog.OutputType, X = 760, Y = 120 });
        return d;
    }

    /// <summary>The default file name for a graph name: letters, digits, dash, underscore; everything else becomes an underscore.</summary>
    public static string SafeFileStem(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in name.Trim())
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return sb.Length == 0 ? "ShaderGraph" : sb.ToString();
    }

    public static string PathIn(string projectRoot, string name) =>
        Path.Combine(projectRoot, ".reyengine", FolderName, SafeFileStem(name) + FileSuffix);
}

/// <summary>JSON reading and writing of <see cref="ShaderGraphDocument"/> (indented, camelCase, stable order).</summary>
public static class ShaderGraphJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = true,
    };

    public static string Serialize(ShaderGraphDocument doc)
    {
        // props are written in key order so a save of an unchanged graph is byte-identical
        var copy = doc.Clone();
        copy.Version = ShaderGraphDocument.CurrentVersion;   // a file this build writes is a current one (a version 1 file is upgraded by its next save)
        foreach (var n in copy.Nodes)
            n.Props = new Dictionary<string, string>(n.Props.OrderBy(kv => kv.Key, StringComparer.Ordinal), StringComparer.Ordinal);
        return JsonSerializer.Serialize(copy, Options) + "\n";
    }

    /// <summary>Parse and bounds-check a document. Null with <paramref name="error"/> when the text is not a Shader Graph this build can read
    /// (a NEWER version is refused rather than half-read: saving it back would lose what this build does not know).</summary>
    public static ShaderGraphDocument? TryDeserialize(string json, out string? error)
    {
        error = null;
        if (json is null || json.Length > ShaderGraphDocument.MaxFileBytes) { error = "the file is too large to be a Shader Graph"; return null; }
        ShaderGraphDocument? doc;
        try { doc = JsonSerializer.Deserialize<ShaderGraphDocument>(json, Options); }
        catch (Exception ex) { error = "not a Shader Graph file: " + ex.Message; return null; }
        if (doc is null) { error = "the file is empty"; return null; }
        if (doc.Version < 1 || doc.Version > ShaderGraphDocument.CurrentVersion)
        {
            error = $"Shader Graph version {doc.Version} is not supported (this build reads version {ShaderGraphDocument.CurrentVersion}).";
            return null;
        }
        if (doc.Nodes is null || doc.Links is null) { error = "the file has no nodes or links list"; return null; }
        if (doc.Nodes.Count > ShaderGraphDocument.MaxNodes) { error = $"more than {ShaderGraphDocument.MaxNodes} nodes"; return null; }
        if (doc.Links.Count > ShaderGraphDocument.MaxLinks) { error = $"more than {ShaderGraphDocument.MaxLinks} links"; return null; }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in doc.Nodes)
        {
            if (n is null) { error = "a node entry is null"; return null; }
            if (string.IsNullOrEmpty(n.Id) || !ids.Add(n.Id)) { error = $"node id '{n.Id}' is empty or repeated"; return null; }
            n.Type ??= "";
            n.Props ??= new Dictionary<string, string>(StringComparer.Ordinal);
            // a null value reads as empty; a key or value past the cap is refused (nothing legitimate is that long)
            foreach (var key in n.Props.Keys.ToList())
            {
                string v = n.Props[key] ?? "";
                int cap = key == "code" ? ShaderGraphDocument.MaxCodeLength : ShaderGraphDocument.MaxPropLength;
                if (key.Length > ShaderGraphDocument.MaxPropLength || v.Length > cap) { error = $"a setting of node {n.Id} is too long"; return null; }
                n.Props[key] = v;
            }
            n.Label ??= "";
            if (n.Label.Length > ShaderGraphDocument.MaxLabelLength) { error = $"the label of node {n.Id} is too long"; return null; }
            if (n.Id.Length > 64 || n.Type.Length > 64) { error = $"node {n.Id} has an id or type that is too long"; return null; }
            if (!double.IsFinite(n.X) || !double.IsFinite(n.Y)) { error = $"node {n.Id} has a position that is not a number"; return null; }
        }
        foreach (var l in doc.Links)
        {
            if (l is null) { error = "a link entry is null"; return null; }
            l.FromNode ??= ""; l.FromPin ??= ""; l.ToNode ??= ""; l.ToPin ??= "";
        }
        doc.Params ??= new List<SgCustomParam>();
        doc.Comments ??= new List<SgComment>();
        if (doc.Params.Count > ShaderGraphDocument.MaxCustomParams) { error = $"more than {ShaderGraphDocument.MaxCustomParams} custom parameters"; return null; }
        if (doc.Comments.Count > ShaderGraphDocument.MaxComments) { error = $"more than {ShaderGraphDocument.MaxComments} comment boxes"; return null; }
        var paramNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in doc.Params)
        {
            if (p is null) { error = "a custom parameter entry is null"; return null; }
            p.Name ??= ""; p.Default ??= "";
            if (!SgNames.IsIdentifier(p.Name, SgNames.MaxParamName)) { error = $"the custom parameter name '{(p.Name.Length > 40 ? p.Name[..40] : p.Name)}' is not a valid name"; return null; }
            if (!paramNames.Add(p.Name)) { error = $"the custom parameter '{p.Name}' is declared twice"; return null; }
            if (p.Components is < 1 or > 4) { error = $"the custom parameter '{p.Name}' has {p.Components} components (1 to 4)"; return null; }
            if (p.Default.Length > 128) { error = $"the default of custom parameter '{p.Name}' is too long"; return null; }
        }
        var commentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in doc.Comments)
        {
            if (c is null) { error = "a comment entry is null"; return null; }
            c.Id ??= ""; c.Title ??= ""; c.Color ??= SgComment.DefaultColor;
            if (c.Id.Length is 0 or > 64 || !commentIds.Add(c.Id)) { error = $"comment id '{c.Id}' is empty, too long or repeated"; return null; }
            if (c.Title.Length > 256) { error = $"the title of comment {c.Id} is too long"; return null; }
            if (!double.IsFinite(c.X) || !double.IsFinite(c.Y) || !double.IsFinite(c.Width) || !double.IsFinite(c.Height)) { error = $"comment {c.Id} has a position or size that is not a number"; return null; }
            c.Width = Math.Clamp(c.Width, SgComment.MinSize, SgComment.MaxSize);
            c.Height = Math.Clamp(c.Height, SgComment.MinSize, SgComment.MaxSize);
        }
        doc.Name ??= "";
        doc.BaseShader ??= "";
        if (doc.Name.Length > 128 || doc.BaseShader.Length > 256) { error = "the name or base shader is too long"; return null; }
        return doc;
    }
}
