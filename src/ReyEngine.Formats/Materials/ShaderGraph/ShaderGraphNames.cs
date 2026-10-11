using System.Text;
using System.Text.RegularExpressions;

namespace ReyEngine.Formats.Materials.ShaderGraph;

/// <summary>
/// M834: the name rules of everything a person types that ends up as an HLSL identifier: a custom parameter, an input of a Custom node.
/// A name must be an ASCII identifier of a bounded length that cannot collide with anything the generated shader (or the engine) already
/// uses: HLSL keywords and types, the intrinsics the generator calls, the locals it declares (<c>v0</c>, <c>i</c>, <c>o0</c>...), the engine's
/// constant vocabulary, and the names the base shader declares. Comparisons are case-insensitive because the preview's constant table is.
/// </summary>
public static class SgNames
{
    public const int MaxParamName = 40;
    public const int MaxInputName = 24;
    public const int MaxCustomInputs = 8;

    private static readonly HashSet<string> Words = new(StringComparer.Ordinal)
    {
        // keywords and storage classes
        "if", "else", "for", "while", "do", "switch", "case", "default", "break", "continue", "return", "discard", "struct", "cbuffer",
        "tbuffer", "register", "packoffset", "static", "const", "extern", "uniform", "shared", "groupshared", "volatile", "precise",
        "in", "out", "inout", "true", "false", "typedef", "interface", "class", "namespace", "technique", "pass", "string", "void",
        "linear", "centroid", "nointerpolation", "noperspective", "sample", "point", "line", "triangle", "row_major", "column_major",
        "snorm", "unorm", "vector", "matrix", "sampler", "samplerstate", "samplercomparisonstate", "texture", "buffer", "compile", "main",
        "this", "template", "typename", "operator", "public", "private", "protected", "new", "delete", "sizeof", "null",
        // scalar and vector types
        "float", "half", "double", "int", "uint", "dword", "bool", "min16float", "min10float", "min16int", "min12int", "min16uint",
        // intrinsics the generated code (or a Custom node body) calls
        "abs", "acos", "all", "any", "asin", "atan", "atan2", "ceil", "clamp", "clip", "cos", "cosh", "cross", "ddx", "ddy", "degrees",
        "distance", "dot", "exp", "exp2", "floor", "fmod", "frac", "fwidth", "isfinite", "isinf", "isnan", "length", "lerp", "lit", "log",
        "log10", "log2", "mad", "max", "min", "modf", "mul", "normalize", "pow", "radians", "rcp", "reflect", "refract", "round", "rsqrt",
        "saturate", "sign", "sin", "sincos", "sinh", "smoothstep", "sqrt", "step", "tan", "tanh", "transpose", "trunc", "faceforward",
    };

    /// <summary>Names the generated <c>main</c> declares itself (compared without regard to case, like the preview's constant table).</summary>
    private static readonly HashSet<string> GeneratorNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "i", "pos", "baseColor", "opacity", "PSIn", "TIME", "main",
    };

    /// <summary>Constants the engine (the renderer or the game) fills by name, besides the variables of the base's per-frame cbuffer.</summary>
    private static readonly HashSet<string> Engine = new(StringComparer.OrdinalIgnoreCase)
    {
        "WORLD_MATRIX", "MWORLD", "VIEW_PROJECTION_MATRIX", "MVIEWPROJ", "MVIEW", "MVIEWINV", "MWORLDINV", "MPROJ", "LIGHTGRID_COLORS",
        "NAV_GRID_XFORM", "BAKED_PAINT_UV_SCALE_BIAS", "TERRAIN_XFORM", "TEEMO_ACTIVE", "CONSTANT_DEPTH_BIAS", "SLOPE_SCALED_DEPTH_BIAS",
        "WORLD_TO_CLUSTER_TRANSFORM", "CLUSTER_MAX_CLAMP", "LIGHT_REGION_TEXTURE_SIZE", "IBL_CUBEMAP_SCALES", "MSHADOWPROJ",
        "NORMAL_OFFSET_BIAS", "ENV_LIGHTING_MASK", "RENDERTARGET_IS_SRGB", "MESH_CENTER", "PerFramePixelCB", "PerFrameVertexCB", "BAKED_LIGHT",
    };

    private static readonly Regex Typed = new(@"^(float|half|double|int|uint|bool|min16float|min10float|min16int|min16uint)([1-4])?(x[1-4])?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Generated = new(@"^(v|o|tc|col)\d", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ResourcePrefix = new(@"^(texture|rwtexture|sampler|rwbuffer|rwstructuredbuffer|structuredbuffer|bytesaddressbuffer|rwbytesaddressbuffer|appendstructuredbuffer|consumestructuredbuffer|tex2d|tex3d)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>True when <paramref name="s"/> is 1..<paramref name="maxLength"/> ASCII letters, digits and underscores, not starting with a digit.</summary>
    public static bool IsIdentifier(string s, int maxLength)
    {
        if (string.IsNullOrEmpty(s) || s.Length > maxLength) return false;
        if (char.IsAsciiDigit(s[0])) return false;
        foreach (char c in s)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_')) return false;
        return true;
    }

    /// <summary>The reason <paramref name="name"/> may not be used as an identifier of the generated shader, or null when it may. This is the part that
    /// does not depend on the graph (shared by the parameter and Custom-node-input rules).</summary>
    public static string? ReservedReason(string name, bool forInput = false)
    {
        if (Words.Contains(name)) return $"'{name}' is an HLSL word the generated shader uses";
        if (!forInput && GeneratorNames.Contains(name)) return $"'{name}' is an HLSL word the generated shader uses";
        if (Typed.IsMatch(name)) return $"'{name}' is an HLSL type name";
        if (ResourcePrefix.IsMatch(name)) return $"'{name}' looks like an HLSL texture or sampler type";
        if (!forInput && Generated.IsMatch(name)) return $"'{name}' looks like a local the generator declares (v0, o0, tc1, col0...)";
        if (name.StartsWith("sg_", StringComparison.OrdinalIgnoreCase)) return "names starting with 'sg_' are the generator's";
        if (name.Contains("__", StringComparison.Ordinal)) return "a name cannot contain two underscores in a row (they are reserved for texture and sampler names)";
        if (name[0] == '_') return "a name cannot start with an underscore";
        return null;
    }

    /// <summary>The reason <paramref name="name"/> is not allowed for a custom parameter of <paramref name="doc"/> on <paramref name="b"/>, or null.
    /// <paramref name="renaming"/> is the parameter being renamed (its own current name is not a clash).</summary>
    public static string? ParameterProblem(string name, ShaderGraphDocument doc, SgBase? b, string? renaming = null)
    {
        if (string.IsNullOrEmpty(name)) return "Type a name.";
        if (name.Length > MaxParamName) return $"A parameter name has at most {MaxParamName} characters.";
        foreach (char c in name)
            if (c > 126 || c < 33) return "A parameter name uses ASCII letters, digits and underscores only.";
        if (!IsIdentifier(name, MaxParamName)) return char.IsAsciiDigit(name[0]) ? "A parameter name cannot start with a digit." : "A parameter name uses ASCII letters, digits and underscores only.";
        if (ReservedReason(name) is { } why) return char.ToUpperInvariant(why[0]) + why[1..] + ".";
        if (Engine.Contains(name)) return $"'{name}' is a constant the engine fills; choose another name.";
        if (b is not null)
        {
            if (b.Frame is { } frame)
            {
                if (frame.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || frame.Variables.Any(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    return $"'{name}' is a constant of the engine's {frame.Name}; choose another name.";
            }
            if (b.Parameters.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return $"'{name}' is a parameter {b.Shader} already declares; use the declared parameter instead.";
            if (b.Textures.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return $"'{name}' is a texture {b.Shader} declares; choose another name.";
        }
        if (doc.Params.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && !string.Equals(p.Name, renaming, StringComparison.Ordinal)))
            return $"The graph already has a parameter called '{name}' (names are compared without regard to case).";
        return null;
    }

    /// <summary>The first free name of the form <c>Base</c>, <c>Base2</c>, ... that passes <see cref="ParameterProblem"/>.</summary>
    public static string UniqueParameterName(string baseName, ShaderGraphDocument doc, SgBase? b)
    {
        string name = baseName;
        for (int i = 2; ParameterProblem(name, doc, b) is not null && i < 1000; i++) name = baseName + i;
        return name;
    }
}

/// <summary>One named input of a Custom node.</summary>
public sealed record SgCustomInput(string Name, int Components);

/// <summary>
/// M834: the Custom node (Unreal's Custom HLSL expression). The person writes an expression, or a function body with <c>return</c>, over named typed
/// inputs. The generator wraps it in its OWN function (<c>float4 sg_custom_N(float4 In) { ... }</c>): the inputs are the parameters, the
/// <c>return</c> is the output, so the body cannot see or clash with the locals of <c>main</c>. This class is the sanitiser: what may not appear in the body
/// at all (resources, buffers, registers, preprocessor lines, non-ASCII text, braces that could close the wrapper, comment openers that could swallow it).
/// Node props: <c>inputs</c> (<c>Name:float3;Mask:float</c>), <c>out</c> (<c>float</c>..<c>float4</c>), <c>code</c>.
/// </summary>
public static class SgCustom
{
    public const string Type = "Custom";
    public const string DefaultInputs = "In:float4";
    public const string DefaultOut = "float4";
    public const string DefaultCode = "In";
    public const int MaxLines = 80;
    public const int MaxLineLength = 200;

    private static readonly HashSet<string> Forbidden = new(StringComparer.Ordinal)
    {
        "cbuffer", "tbuffer", "register", "packoffset", "struct", "interface", "class", "namespace", "typedef", "technique", "pass",
        "groupshared", "static", "extern", "uniform", "shared", "sampler", "SamplerState", "SamplerComparisonState", "Buffer",
        "StructuredBuffer", "ByteAddressBuffer", "RWBuffer", "AppendStructuredBuffer", "ConsumeStructuredBuffer", "string",
        "ConstantBuffer", "RWStructuredBuffer", "RWByteAddressBuffer", "InputPatch", "OutputPatch",
    };

    /// <summary>Type families a body may not name: rasterizer-ordered views and geometry-shader streams.</summary>
    private static bool ForbiddenFamily(string t) =>
        t.StartsWith("RasterizerOrdered", StringComparison.Ordinal) || t.EndsWith("Stream", StringComparison.Ordinal) || t.StartsWith("RWTexture", StringComparison.Ordinal);

    private static readonly Regex Token = new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

    public static IReadOnlyList<SgCustomInput> Inputs(SgNode node)
    {
        TryParseInputs(node.Prop("inputs", DefaultInputs), out var list, out _);
        return list;
    }

    /// <summary>The width of the output (<c>float</c> 1 .. <c>float4</c> 4); 0 when the text is none of them.</summary>
    public static int OutWidth(string text) => text.Trim().ToLowerInvariant() switch { "float" => 1, "float2" => 2, "float3" => 3, "float4" => 4, _ => 0 };

    public static string FormatInputs(IEnumerable<SgCustomInput> inputs) => string.Join(";", inputs.Select(i => i.Name + ":" + SgCatalog.TypeName(i.Components)));

    public static bool TryParseInputs(string text, out List<SgCustomInput> list, out string error)
    {
        list = new List<SgCustomInput>();
        error = "";
        if (text.Length == 0) return true;   // a Custom node may have no inputs
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in text.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = part.IndexOf(':');
            string name = (colon < 0 ? part : part[..colon]).Trim();
            string type = colon < 0 ? "float" : part[(colon + 1)..].Trim();
            if (InputProblem(name) is { } bad) { error = bad; return false; }
            int w = OutWidth(type);
            if (w == 0) { error = $"input '{name}': '{type}' is not float, float2, float3 or float4"; return false; }
            if (!seen.Add(name)) { error = $"two inputs are called '{name}'"; return false; }
            list.Add(new SgCustomInput(name, w));
            if (list.Count > SgNames.MaxCustomInputs) { error = $"a Custom node has at most {SgNames.MaxCustomInputs} inputs"; return false; }
        }
        return true;
    }

    /// <summary>The reason an input name is refused, or null.</summary>
    public static string? InputProblem(string name)
    {
        if (name.Length == 0) return "an input has no name";
        if (name.Length > SgNames.MaxInputName) return $"input name '{name[..12]}...' is longer than {SgNames.MaxInputName} characters";
        if (!SgNames.IsIdentifier(name, SgNames.MaxInputName)) return $"input name '{name}' must be ASCII letters, digits and underscores, not starting with a digit";
        if (SgNames.ReservedReason(name, forInput: true) is { } why) return "input name: " + why;
        return null;
    }

    /// <summary>True when the body is an expression (no <c>return</c>): the generator writes <c>return (expr);</c> for it.</summary>
    public static bool IsExpression(string code) => !Token.Matches(StripComments(code)).Any(m => m.Value == "return");

    /// <summary>The body with its <c>//</c> comments removed (the lines stay, so a line number in a compiler message still points at the right text).</summary>
    public static string StripComments(string code)
    {
        var sb = new StringBuilder(code.Length);
        foreach (string line in code.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            int cut = line.IndexOf("//", StringComparison.Ordinal);
            sb.Append(cut < 0 ? line : line[..cut]).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>The reason <paramref name="code"/> may not be put in a Custom node, or null when it is safe to wrap.</summary>
    public static string? CodeProblem(string code, out int line)
    {
        line = 0;
        if (code is null || code.Trim().Length == 0) return "write an expression (for example In * 2.0) or a body that returns a value";
        if (code.Length > ShaderGraphDocument.MaxCodeLength) return $"the code is longer than {ShaderGraphDocument.MaxCodeLength} characters";
        var lines = code.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (lines.Length > MaxLines) return $"the code has more than {MaxLines} lines";
        for (int i = 0; i < lines.Length; i++)
        {
            line = i + 1;
            if (lines[i].Length > MaxLineLength) return $"line {line} is longer than {MaxLineLength} characters";
            foreach (char c in lines[i])
            {
                if (c > 126) return $"line {line} has a character that is not ASCII (HLSL text must be ASCII only)";
                if (c < 32 && c != '\t') return $"line {line} has a control character";
            }
            if (lines[i].Contains('#')) return $"line {line}: '#' is not allowed (no preprocessor lines such as #include or #define)";
            // no string or character literals and no backslash: a line-splice or a string could hide text from the checks above
            foreach (char q in new[] { '"', '\'', '\\' })
                if (lines[i].Contains(q)) return $"line {line}: '{q}' is not allowed (no string or character literals and no backslash in HLSL code)";
            if (lines[i].Contains("/*", StringComparison.Ordinal) || lines[i].Contains("*/", StringComparison.Ordinal))
                return $"line {line}: block comments are not allowed; use // comments";
        }
        line = 0;
        string stripped = StripComments(code);
        // braces must balance and never close more than they opened, so nothing can end the wrapper function early
        int depth = 0, at = 1;
        foreach (char c in stripped)
        {
            if (c == '\n') at++;
            else if (c == '{') depth++;
            else if (c == '}' && --depth < 0) { line = at; return $"line {at}: '}}' closes more than the code opened (the code is the inside of a function)"; }
        }
        if (depth != 0) return "a '{' is never closed";
        at = 1;
        int parens = 0;
        foreach (char c in stripped)
        {
            if (c == '\n') at++;
            else if (c == '(') parens++;
            else if (c == ')' && --parens < 0) { line = at; return $"line {at}: ')' without a matching '('"; }
        }
        if (parens != 0) return "a '(' is never closed";
        at = 1;
        foreach (string l in stripped.Split('\n'))
        {
            foreach (Match m in Token.Matches(l))
            {
                string t = m.Value;
                if (Forbidden.Contains(t) || ForbiddenFamily(t) || t.StartsWith("Texture", StringComparison.Ordinal) || t.StartsWith("sampler", StringComparison.Ordinal))
                { line = at; return $"line {at}: '{t}' is not allowed (a Custom node cannot declare resources, buffers, registers or types; use the node's inputs)"; }
                if (t.StartsWith("sg_", StringComparison.Ordinal) || t.Contains("__", StringComparison.Ordinal))
                { line = at; return $"line {at}: '{t}' is a name the generator reserves"; }
            }
            at++;
        }
        line = 0;
        if (IsExpression(code))
        {
            string expr = stripped.Trim().TrimEnd(';');
            if (expr.Contains(';')) return "an expression cannot contain ';' - write 'return ...;' when you need statements";
            if (expr.Length == 0) return "the expression is empty";
        }
        return null;
    }

    /// <summary>The body lines of the wrapper function, in order: an expression becomes <c>return (expr);</c> (one line per source line), a body is copied as is.</summary>
    public static IReadOnlyList<string> BodyLines(string code)
    {
        var lines = code.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.TrimEnd().Replace('\t', ' ')).ToList();
        if (!IsExpression(code)) return lines;
        // the expression keeps its own lines; the wrapper adds "return (" before the first and ");" after the last (without a trailing ';')
        string last = lines[^1];
        while (last.Length == 0 && lines.Count > 1) { lines.RemoveAt(lines.Count - 1); last = lines[^1]; }
        int cut = last.IndexOf("//", StringComparison.Ordinal);
        string code0 = cut < 0 ? last : last[..cut];
        string comment = cut < 0 ? "" : last[cut..];
        code0 = code0.TrimEnd().TrimEnd(';');
        lines[^1] = code0 + ");" + (comment.Length > 0 ? " " + comment : "");
        lines[0] = "return (" + lines[0];
        return lines;
    }
}

/// <summary>
/// M834: what the rest of the editor needs to know about a graph's custom parameters - the notes and the export block, the rename that follows the
/// parameter into every node that reads it, and the list a future shaders.bin step would declare.
/// </summary>
public static class SgCustomParams
{
    public const string ShipNote = "custom parameter: the export declares it in a shaders.bin the mod ships (Global.wad.client, regenerated from the installed game on every export)";

    /// <summary>Why a material editor / export report mentions the shipped shaders.bin.</summary>
    public const string ShipRisk = "shaders.bin is global: the mod's copy replaces Riot's whole file, so enable only one mod that ships it and re-export after every patch";

    /// <summary>Every custom parameter of a graph, as the declarations an export adds to its base shader (used or not: a material may carry any of them,
    /// and a material parameter the definition does not declare crashes the game).</summary>
    public static IReadOnlyList<SgDeclaredParam> Declarations(ShaderGraphDocument doc) =>
        doc.Params.Select(p => new SgDeclaredParam(p.Name, Math.Clamp(p.Components, 1, 4))).ToList();

    /// <summary>The custom parameters a REACHABLE Parameter node reads - what an export would have to declare. Empty = the graph is exportable on this count.</summary>
    public static IReadOnlyList<SgCustomParam> UsedBy(ShaderGraphDocument doc, SgAnalysis? analysis = null)
    {
        var used = new List<SgCustomParam>();
        foreach (var n in doc.Nodes)
        {
            if (n.Type != "Parameter") continue;
            if (analysis is not null && analysis.Reachable.Count > 0 && !analysis.Reachable.Contains(n.Id)) continue;
            if (doc.FindParam(n.Prop("name")) is { } p && !used.Contains(p)) used.Add(p);
        }
        return used;
    }

    /// <summary>Make the value text of a parameter of <paramref name="components"/> numbers: the old text padded with zeros / cut.</summary>
    public static string ResizeDefault(string text, int components)
    {
        SgCatalog.TryParseConstant(text, out var v);
        var r = new float[Math.Clamp(components, 1, 4)];
        for (int i = 0; i < r.Length; i++) r[i] = i < v.Length ? v[i] : (i == 3 ? 1f : 0f);
        return SgCatalog.FormatConstant(r);
    }

    /// <summary>The widths a "new parameter" kind names: (components, isColour). Null for a kind that is none of the six.</summary>
    public static (int Components, bool IsColor)? ParseKind(string kind) => kind switch
    {
        "float" => (1, false), "float2" => (2, false), "float3" => (3, false), "float4" => (4, false),
        "color3" => (3, true), "color4" => (4, true),
        _ => null,
    };

    /// <summary>The default value text for a new parameter: colours start white, scalars and vectors at zero... except a colour, which is opaque white.</summary>
    public static string NewDefault(int components, bool isColor) =>
        isColor ? SgCatalog.FormatConstant(Enumerable.Repeat(1f, components)) : SgCatalog.FormatConstant(new float[components]);
}
