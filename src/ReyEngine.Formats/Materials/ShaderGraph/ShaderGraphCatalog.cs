using System.Globalization;

namespace ReyEngine.Formats.Materials.ShaderGraph;

/// <summary>An input pin of a node type. <see cref="Fixed"/> is the width the pin demands (0 = any width, a scalar broadcasts); an
/// unconnected pin takes <see cref="Default"/> (a scalar) unless the node carries a <c>Props[pin name]</c> override.</summary>
public sealed record SgPinDef(string Name, int Fixed = 0, float Default = 0f);

/// <summary>A node type: its pins and where the add palette files it.</summary>
public sealed class SgNodeDef
{
    public required string Type { get; init; }
    public required string Title { get; init; }
    public required string Category { get; init; }
    public IReadOnlyList<SgPinDef> Inputs { get; init; } = Array.Empty<SgPinDef>();
    public IReadOnlyList<string> Outputs { get; init; } = new[] { "Value" };
    public string Description { get; init; } = "";

    public int InputIndex(string pin) { for (int i = 0; i < Inputs.Count; i++) if (Inputs[i].Name == pin) return i; return -1; }
    public int OutputIndex(string pin) { for (int i = 0; i < Outputs.Count; i++) if (Outputs[i] == pin) return i; return -1; }
}

/// <summary>One line of the add-node search palette: what to create and with which settings.</summary>
public sealed record SgPaletteEntry(string Title, string Category, string Type, IReadOnlyDictionary<string, string> Props, string Keywords = "")
{
    public string Display => Category + " / " + Title;
}

/// <summary>
/// M832: the node types of the Shader Graph. Float widths 1..4 with Unreal's promotion rules: a scalar broadcasts to any width; two
/// vectors must have the same width (anything else is an error shown on the node, never silently truncated).
/// </summary>
public static class SgCatalog
{
    public const string OutputType = "Output";
    public const string BaseColorPin = "Base Color";
    public const string OpacityPin = "Opacity";

    private static readonly Dictionary<string, SgNodeDef> Defs = Build();

    public static IReadOnlyCollection<SgNodeDef> All => Defs.Values;
    public static SgNodeDef? Get(string type) => Defs.TryGetValue(type, out var d) ? d : null;

    private static Dictionary<string, SgNodeDef> Build()
    {
        var list = new List<SgNodeDef>();
        void Add(string type, string title, string cat, SgPinDef[]? ins = null, string[]? outs = null, string desc = "") =>
            list.Add(new SgNodeDef { Type = type, Title = title, Category = cat, Inputs = ins ?? Array.Empty<SgPinDef>(), Outputs = outs ?? new[] { "Value" }, Description = desc });
        SgPinDef P(string n, float d = 0f, int fixedW = 0) => new(n, fixedW, d);

        // ---- inputs
        Add("TextureSample", "Texture Sample", "Input", new[] { new SgPinDef("UV", 2, 0f) }, new[] { "RGBA", "RGB", "R", "G", "B", "A" },
            "Samples one of the base shader's declared textures. UV defaults to the base shader's UV0 interpolant.");
        Add("Parameter", "Parameter", "Input", null, null, "A parameter the base shader declares in shaders.bin; a material sets it by name.");
        Add("Constant", "Constant", "Input", null, null, "A fixed float, float2, float3 or float4.");
        Add("TexCoord", "TexCoord", "Input", null, new[] { "UV" }, "The xy of a TEXCOORD interpolant the base vertex shader writes.");
        Add("VertexColor", "Vertex Color", "Input", null, new[] { "RGBA" }, "COLOR0 of the vertex stage, when the base shader writes it.");
        Add("Time", "Time", "Input", null, new[] { "Seconds" }, "TIME.x of the engine's per-frame cbuffer.");

        // ---- math, binary
        Add("Add", "Add", "Math", new[] { P("A"), P("B") });
        Add("Subtract", "Subtract", "Math", new[] { P("A"), P("B") });
        Add("Multiply", "Multiply", "Math", new[] { P("A", 1), P("B", 1) });
        Add("Divide", "Divide", "Math", new[] { P("A", 1), P("B", 1) });
        Add("Power", "Power", "Math", new[] { P("Base", 1), P("Exp", 1) });
        Add("Min", "Min", "Math", new[] { P("A"), P("B") });
        Add("Max", "Max", "Math", new[] { P("A"), P("B") });
        Add("Step", "Step", "Math", new[] { P("Edge", 0.5f), P("X") }, null, "1 where X >= Edge, else 0.");
        Add("Dot", "Dot", "Math", new[] { P("A"), P("B") });
        Add("Lerp", "Lerp", "Math", new[] { P("A"), P("B", 1), P("Alpha", 0.5f) });
        Add("Clamp", "Clamp", "Math", new[] { P("Value"), P("Min"), P("Max", 1) });
        // ---- math, unary
        Add("Saturate", "Saturate", "Math", new[] { P("Value") });
        Add("OneMinus", "One Minus", "Math", new[] { P("Value") });
        Add("Abs", "Abs", "Math", new[] { P("Value") });
        Add("Normalize", "Normalize", "Math", new[] { P("Value") });
        Add("Sine", "Sine", "Math", new[] { P("Value") });
        Add("Cosine", "Cosine", "Math", new[] { P("Value") });
        Add("Sqrt", "Square Root", "Math", new[] { P("Value", 1) });
        Add("Floor", "Floor", "Math", new[] { P("Value") });
        Add("Frac", "Frac", "Math", new[] { P("Value") });
        // ---- vector
        Add("ComponentMask", "Component Mask", "Vector", new[] { P("Value") }, null, "Keeps the chosen components (r g b a).");
        Add("Append", "Append", "Vector", new[] { P("A"), P("B") }, null, "Joins two values into one wider vector (at most float4).");
        Add("Split", "Split", "Vector", new[] { P("Value") }, new[] { "R", "G", "B", "A" });
        // ---- output
        Add(OutputType, "Shader Output", "Output", new[] { P(BaseColorPin, 0f, 3), P(OpacityPin, 1f, 1) }, new string[0],
            "The pixel stage result: Base Color (rgb) and Opacity (a).");

        return list.ToDictionary(d => d.Type, StringComparer.Ordinal);
    }

    // ================================================================================== values

    public static bool TryParseConstant(string text, out float[] values)
    {
        values = Array.Empty<float>();
        var parts = (text ?? "").Split(new[] { ' ', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 4) return false;
        var v = new float[parts.Length];
        for (int i = 0; i < v.Length; i++)
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]) || !float.IsFinite(v[i])) return false;
        values = v;
        return true;
    }

    public static string FormatConstant(IEnumerable<float> v) =>
        string.Join(" ", v.Select(f => f.ToString("R", CultureInfo.InvariantCulture)));

    /// <summary>The value an unconnected scalar pin has: the node's <c>Props[pin name]</c> when it parses, else the definition's default.</summary>
    public static float PinDefault(SgNode node, SgPinDef pin) =>
        node.Props.TryGetValue(pin.Name, out var t) && float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && float.IsFinite(f)
            ? f : pin.Default;

    /// <summary>"rgb" / "xy" -> component indices; null when a character is not a component or the mask is empty / longer than 4.</summary>
    public static int[]? ParseMask(string mask)
    {
        if (string.IsNullOrEmpty(mask) || mask.Length > 4) return null;
        var idx = new int[mask.Length];
        for (int i = 0; i < mask.Length; i++)
        {
            idx[i] = char.ToLowerInvariant(mask[i]) switch { 'r' or 'x' => 0, 'g' or 'y' => 1, 'b' or 'z' => 2, 'a' or 'w' => 3, _ => -1 };
            if (idx[i] < 0) return null;
        }
        return idx;
    }

    public static string TypeName(int components) => components switch { 1 => "float", 2 => "float2", 3 => "float3", 4 => "float4", _ => "?" };

    // ================================================================================== the add palette

    /// <summary>The palette for a base: one line per declared texture and parameter, every math node, the constants, the UV sets the
    /// base's signatures carry. Time and Vertex Color appear only when the base provides them.</summary>
    public static IReadOnlyList<SgPaletteEntry> Palette(SgBase? b)
    {
        var list = new List<SgPaletteEntry>();
        static Dictionary<string, string> Pr(params (string, string)[] kv) => kv.ToDictionary(x => x.Item1, x => x.Item2);

        if (b is not null)
        {
            foreach (var t in b.Textures)
                list.Add(new SgPaletteEntry("Texture Sample: " + t.Name, "Input", "TextureSample", Pr(("texture", t.Name)), "texture sample sampler " + t.Name));
            foreach (var p in b.Parameters)
                list.Add(new SgPaletteEntry("Parameter: " + p.Name, "Input", "Parameter", Pr(("name", p.Name)), "parameter param constant " + p.Name));
        }
        list.Add(new SgPaletteEntry("Constant (float)", "Input", "Constant", Pr(("value", "1")), "constant scalar number"));
        list.Add(new SgPaletteEntry("Constant2 (float2)", "Input", "Constant", Pr(("value", "1 1")), "constant vector2"));
        list.Add(new SgPaletteEntry("Constant3 (float3)", "Input", "Constant", Pr(("value", "1 1 1")), "constant vector3 colour color"));
        list.Add(new SgPaletteEntry("Constant4 (float4)", "Input", "Constant", Pr(("value", "1 1 1 1")), "constant vector4 colour color"));
        int uv = b?.UvSet ?? 1;
        var sets = new SortedSet<int> { uv };
        if (b is not null)
            foreach (var s in b.Signatures)
                foreach (var e in s.Inputs)
                    if (e.Semantic.Equals("TEXCOORD", StringComparison.OrdinalIgnoreCase) && e.ComponentCount >= 2) sets.Add((int)e.Index);
        foreach (int s in sets)
            list.Add(new SgPaletteEntry(s == uv ? $"TexCoord {s} (UV0)" : $"TexCoord {s}", "Input", "TexCoord", Pr(("set", s.ToString(CultureInfo.InvariantCulture))), "texcoord uv coordinates"));
        if (b is null || b.HasVertexColor) list.Add(new SgPaletteEntry("Vertex Color", "Input", "VertexColor", Pr(), "vertex colour color"));
        if (b is null || b.HasTime) list.Add(new SgPaletteEntry("Time", "Input", "Time", Pr(), "time seconds animate"));

        foreach (var d in Defs.Values)
        {
            if (d.Category is "Input" or "Output") continue;
            Dictionary<string, string> props = d.Type == "ComponentMask" ? Pr(("mask", "rgb")) : Pr();
            list.Add(new SgPaletteEntry(d.Title, d.Category, d.Type, props, d.Description + " " + d.Type));
        }
        return list;
    }

    /// <summary>The palette lines that match every word of <paramref name="filter"/> (title, category, keywords), best first.</summary>
    public static IReadOnlyList<SgPaletteEntry> Search(IReadOnlyList<SgPaletteEntry> palette, string? filter)
    {
        var words = (filter ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return palette;
        return palette
            .Select(e => (Entry: e, Hay: e.Title + " " + e.Category + " " + e.Keywords))
            .Where(x => words.All(w => x.Hay.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(x => x.Entry.Title.StartsWith(words[0], StringComparison.OrdinalIgnoreCase))
            .ThenBy(x => x.Entry.Title.Length)
            .Select(x => x.Entry)
            .ToList();
    }
}
