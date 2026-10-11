using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ReyEngine.Formats.Materials.ShaderGraph;

public sealed class SgHlslOptions
{
    /// <summary>End the shader the way Riot's DefaultEnv_Flat does: <c>if (RENDERTARGET_IS_SRGB != 0) rgb = pow(rgb, 2.2)</c>. Needs the
    /// engine cbuffer in the base; without it nothing is emitted.</summary>
    public bool EncodeOutput { get; set; } = true;
}

/// <summary>The generated pixel shader for ONE input signature of the base shader.</summary>
public sealed class SgHlslResult
{
    public required SgSignature Signature { get; init; }

    /// <summary>The HLSL (ASCII only). Empty when <see cref="Errors"/> is not.</summary>
    public string Source { get; init; } = "";
    public IReadOnlyList<SgDiagnostic> Errors { get; init; } = Array.Empty<SgDiagnostic>();

    /// <summary>1-based source line -> the node whose statement it is (so a compiler error lands on a node).</summary>
    public IReadOnlyDictionary<int, string> LineNodes { get; init; } = new Dictionary<int, string>();

    /// <summary>The declared textures and parameters the shader reads - what the game must bind for it.</summary>
    public IReadOnlyList<string> Textures { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Parameters { get; init; } = Array.Empty<string>();

    /// <summary>M834: the subset of <see cref="Parameters"/> the GRAPH declares (not the base shader): what the game cannot be given yet.</summary>
    public IReadOnlyList<SgCustomParam> CustomParameters { get; init; } = Array.Empty<SgCustomParam>();

    /// <summary>M835: the subset of <see cref="Textures"/> the GRAPH declares.</summary>
    public IReadOnlyList<SgCustomTexture> CustomTextures { get; init; } = Array.Empty<SgCustomTexture>();
    public bool UsesTime { get; init; }
    public bool EncodesOutput { get; init; }
    public bool Ok => Errors.Count == 0 && Source.Length > 0;
}

/// <summary>
/// M832: the Shader Graph's HLSL generator. One pixel shader per input signature of the base shader (the Riot vertex shader that
/// feeds it writes exactly that signature):
/// <list type="bullet">
/// <item>the base shader's parameters the graph reads are declared at file scope - the compiler puts them in <c>$Globals</c> at b0, and the
/// client fills them BY NAME;</item>
/// <item>each texture is <c>Texture2D &lt;Name&gt;__TX</c> with <c>SamplerState &lt;Name&gt;__SMP</c> - Riot's names;</item>
/// <item>the engine's per-frame cbuffer (<c>PerFramePixelCB</c>) is redeclared with Riot's exact layout when the graph reads Time or
/// ends with the sRGB encode;</item>
/// <item>the input struct is the signature, element for element; outputs are the base's SV_Target list (target 0 = the graph, the others
/// the constant (0,0,0,1) every cooked colour blob writes);</item>
/// <item>every node is one statement, in dependency order, tagged with its id in <see cref="SgHlslResult.LineNodes"/>.</item>
/// </list>
/// Pure text: the compile and the ISGN check are <c>ShaderGraphCompiler</c>'s, in the D3D11 project.
/// </summary>
public static class SgHlsl
{
    public static SgHlslResult Generate(ShaderGraphDocument doc, SgBase b, SgSignature sig, SgAnalysis? analysis = null, SgHlslOptions? options = null)
    {
        options ??= new SgHlslOptions();
        analysis ??= SgAnalyzer.Analyze(doc, b);
        if (analysis.HasErrors) return Fail(sig, analysis.Errors.ToList());

        var errors = new List<SgDiagnostic>();
        var byId = doc.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);

        // ---- the signature must be one this generator can declare
        foreach (var e in sig.Inputs)
            if (e.ComponentType != 3) errors.Add(new SgDiagnostic("", null, true, $"input {e.FullSemantic} is not a float element"));
        foreach (var e in sig.Outputs)
            if (!e.Semantic.Equals("SV_Target", StringComparison.OrdinalIgnoreCase) || e.Mask != 0xF || e.ComponentType != 3)
                errors.Add(new SgDiagnostic("", null, true, $"output {e.FullSemantic} is not a float4 SV_Target"));
        if (sig.Outputs.Count == 0 || !sig.Outputs.Select(o => o.Index).SequenceEqual(Enumerable.Range(0, sig.Outputs.Count).Select(k => (uint)k)))
            errors.Add(new SgDiagnostic("", null, true, "the output targets are not SV_Target0..N in order"));
        if (errors.Count > 0) return Fail(sig, errors);

        // ---- what the reachable nodes need
        var textures = new List<string>();
        var parameters = new List<string>();
        bool usesTime = false;
        var customFns = new Dictionary<string, (string Name, SgCustomInput[] Inputs, int Out)>(StringComparer.Ordinal);
        foreach (string id in analysis.Order)
        {
            var n = byId[id];
            var ndef = SgCatalog.DefFor(n);
            if (ndef is not null)
                foreach (var pin in ndef.Inputs)
                {
                    bool wired = doc.Links.Any(l => l.ToNode == id && l.ToPin == pin.Name);
                    if (!wired && SgCatalog.IsUvPin(n, pin) && !HasUv(sig, b.UvSet))
                        errors.Add(new SgDiagnostic(id, pin.Name, true, $"this signature has no TEXCOORD{b.UvSet} to default the UV to"));
                    if (!wired && SgCatalog.IsTimePin(n, pin) && b.HasTime) usesTime = true;
                }
            switch (n.Type)
            {
                case "TextureSample":
                    if (!textures.Contains(n.Prop("texture"))) textures.Add(n.Prop("texture"));
                    break;
                case "Parameter":
                    if (!parameters.Contains(n.Prop("name"))) parameters.Add(n.Prop("name"));
                    break;
                case "Custom":
                    customFns[id] = ("sg_custom_" + customFns.Count, SgCustom.Inputs(n).ToArray(), SgCustom.OutWidth(n.Prop("out", SgCustom.DefaultOut)));
                    break;
                case "TexCoord":
                    int.TryParse(n.Prop("set", b.UvSet.ToString(CultureInfo.InvariantCulture)), out int set);
                    if (!HasUv(sig, set))
                        errors.Add(new SgDiagnostic(id, null, true, $"pixel input signature #{sig.Index} has no TEXCOORD{set} (xy)"));
                    break;
                case "VertexColor":
                    if (sig.Input("COLOR", 0) is null)
                        errors.Add(new SgDiagnostic(id, null, true, $"pixel input signature #{sig.Index} has no COLOR0"));
                    break;
                case "Time":
                    usesTime = true;
                    break;
            }
        }
        bool encode = options.EncodeOutput && b.Frame?.Var("RENDERTARGET_IS_SRGB") is not null;
        bool needFrame = usesTime || encode;
        string? frameDecl = null;
        if (needFrame)
        {
            frameDecl = FrameBuffer(b.Frame!, out string? why);
            if (frameDecl is null)
            {
                if (usesTime) errors.Add(new SgDiagnostic("", null, true, "the engine cbuffer cannot be redeclared exactly: " + why));
                needFrame = false;
                encode = false;
            }
        }
        if (errors.Count > 0) return Fail(sig, errors);

        // ---- text
        var lines = new List<string>();
        var lineNodes = new Dictionary<int, string>();
        void L(string text = "", string? node = null)
        {
            lines.Add(text);
            if (node is not null) lineNodes[lines.Count] = node;
        }

        L($"// ReyEngine Shader Graph \"{Ascii(doc.Name)}\" on {Ascii(b.Shader)}");
        L("// Shipped when a material is assigned to this graph.");
        L($"// Pixel input signature #{sig.Index}: {Ascii(sig.Text)}");
        L();
        if (parameters.Count > 0)
        {
            L("// ---- $Globals: base shader parameters the graph reads (the client fills them by name)");
            foreach (string p in parameters)
                L($"{SgCatalog.TypeName(doc.FindParam(p)?.Components ?? b.Parameter(p)!.Components)} {p};" + (doc.FindParam(p) is not null ? "   // custom parameter (the graph's own)" : ""));
            L();
        }
        if (textures.Count > 0)
        {
            L("// ---- textures, named like Riot's (the client binds <Name>__TX and <Name>__SMP by name)");
            foreach (string t in textures)
            {
                var (res, smp) = TextureNames(doc, b, t);
                L($"Texture2D {res};" + (doc.FindTexture(t) is not null ? "   // custom texture (the graph's own)" : ""));
                L($"SamplerState {smp};");
            }
            L();
        }
        if (needFrame)
        {
            L("// ---- the engine's per-frame cbuffer, redeclared with Riot's exact layout");
            foreach (string s in frameDecl!.Split('\n')) L(s);
            L();
        }

        // ---- Custom nodes: each body in its own function, so it can see only its inputs
        foreach (string id in analysis.Order)
        {
            if (!customFns.TryGetValue(id, out var fn)) continue;
            var node = byId[id];
            L($"// ---- Custom node {Ascii(id)}{(node.Label.Length > 0 ? " \"" + Ascii(node.Label) + "\"" : "")}", id);
            L($"{SgCatalog.TypeName(fn.Out)} {fn.Name}({string.Join(", ", fn.Inputs.Select(i => SgCatalog.TypeName(i.Components) + " " + i.Name))})", id);
            L("{", id);
            foreach (string line in SgCustom.BodyLines(node.Prop("code", SgCustom.DefaultCode)))
                L("    " + line, id);
            L("}", id);
            L();
        }

        L("struct PSIn");
        L("{");
        foreach (var e in sig.Inputs)
            L($"    {SgCatalog.TypeName(e.ComponentCount)} {FieldOf(e)} : {e.Semantic}{e.Index};");
        L("};");
        L();
        var outs = string.Join(", ", sig.Outputs.Select(o => $"out float4 o{o.Index} : SV_Target{o.Index}"));
        L($"void main(PSIn i, {outs})");
        L("{");

        // ---- nodes
        var vars = new Dictionary<string, string[]>(StringComparer.Ordinal);   // node id -> expression per output pin
        string uvDefault = $"i.{FieldOf(sig.Input("TEXCOORD", b.UvSet)!)}.xy";
        int counter = 0;

        string Operand(SgNode n, int pin, int width)
        {
            var def = SgCatalog.DefFor(n)!;
            var pd = def.Inputs[pin];
            var link = doc.LinkInto(n.Id, pd.Name);
            if (link is not null)
            {
                var src = byId[link.FromNode];
                int sp = SgCatalog.Get(src.Type)!.OutputIndex(link.FromPin);
                string expr = vars[src.Id][sp];
                int sw = analysis.OutputWidth(src.Id, sp);
                return Broadcast(expr, sw, width);
            }
            if (SgCatalog.IsUvPin(n, pd)) return uvDefault;
            if (SgCatalog.IsTimePin(n, pd)) return b.HasTime ? "TIME.x" : "0.0";
            return Literal(SgCatalog.PinDefault(n, pd), width);
        }

        foreach (string id in analysis.Order)
        {
            var n = byId[id];
            var def = SgCatalog.DefFor(n)!;
            if (n.Type == SgCatalog.OutputType) continue;
            string v = "v" + counter++;
            int W = analysis.OutputWidth(id, 0);
            var inW = analysis.InputWidths[id];
            string title = Ascii(n.Label.Length > 0 ? n.Label + " (" + def.Title + ")" : def.Title);
            string[] outsExpr;
            string stmt;

            switch (n.Type)
            {
                case "Constant":
                {
                    SgCatalog.TryParseConstant(n.Prop("value"), out var cv);
                    stmt = $"{SgCatalog.TypeName(cv.Length)} {v} = {(cv.Length == 1 ? Float(cv[0]) : SgCatalog.TypeName(cv.Length) + "(" + string.Join(", ", cv.Select(Float)) + ")")};";
                    outsExpr = new[] { v };
                    break;
                }
                case "Parameter":
                    stmt = $"{SgCatalog.TypeName(W)} {v} = {n.Prop("name")};";
                    outsExpr = new[] { v };
                    break;
                case "TexCoord":
                {
                    int.TryParse(n.Prop("set", b.UvSet.ToString(CultureInfo.InvariantCulture)), out int set);
                    stmt = $"float2 {v} = i.{FieldOf(sig.Input("TEXCOORD", set)!)}.xy;";
                    outsExpr = new[] { v };
                    break;
                }
                case "VertexColor":
                    stmt = $"float4 {v} = i.{FieldOf(sig.Input("COLOR", 0)!)};";
                    outsExpr = new[] { v };
                    break;
                case "Time":
                    stmt = $"float {v} = TIME.x;";
                    outsExpr = new[] { v };
                    break;
                case "TextureSample":
                {
                    var (tres, tsmp) = TextureNames(doc, b, n.Prop("texture"));
                    stmt = $"float4 {v} = {tres}.Sample({tsmp}, {Operand(n, 0, 2)});";
                    outsExpr = new[] { v, v + ".rgb", v + ".r", v + ".g", v + ".b", v + ".a" };
                    break;
                }
                case "ConstantColor":
                {
                    SgCatalog.TryParseConstant(n.Prop("value", "1 1 1 1"), out var cc);
                    stmt = $"float4 {v} = float4({string.Join(", ", cc.Select(Float))});";
                    outsExpr = new[] { v };
                    break;
                }
                case "Panner":
                    stmt = Assign(v, 2, $"({Operand(n, 0, 2)} + ({Operand(n, 2, 1)} * {Operand(n, 1, 2)}))");
                    outsExpr = new[] { v };
                    break;
                case "Rotator":
                    stmt = $"float {v}a = {Operand(n, 3, 1)} * {Operand(n, 2, 1)} * 6.28318530718;\n"
                         + $"float2 {v}d = {Operand(n, 0, 2)} - {Operand(n, 1, 2)};\n"
                         + $"float2 {v} = float2({v}d.x * cos({v}a) - {v}d.y * sin({v}a), {v}d.x * sin({v}a) + {v}d.y * cos({v}a)) + {Operand(n, 1, 2)};";
                    outsExpr = new[] { v };
                    break;
                case "TilingOffset":
                    stmt = Assign(v, 2, $"({Operand(n, 0, 2)} * {Operand(n, 1, 2)} + {Operand(n, 2, 2)})");
                    outsExpr = new[] { v };
                    break;
                case "Length": stmt = Assign(v, 1, inW[0] == 1 ? $"abs({Operand(n, 0, 1)})" : $"length({Operand(n, 0, inW[0])})"); outsExpr = new[] { v }; break;
                case "Distance":
                {
                    int u = Math.Max(inW[0], inW[1]);
                    stmt = Assign(v, 1, u == 1 ? $"abs({Operand(n, 0, 1)} - {Operand(n, 1, 1)})" : $"distance({Operand(n, 0, u)}, {Operand(n, 1, u)})");
                    outsExpr = new[] { v };
                    break;
                }
                case "Cross": stmt = Assign(v, 3, $"cross({Operand(n, 0, 3)}, {Operand(n, 1, 3)})"); outsExpr = new[] { v }; break;
                case "Reflect": stmt = Assign(v, W, $"reflect({Operand(n, 0, W)}, {Operand(n, 1, W)})"); outsExpr = new[] { v }; break;
                case "Fmod": stmt = Assign(v, W, $"fmod({Operand(n, 0, W)}, {Operand(n, 1, W)})"); outsExpr = new[] { v }; break;
                case "Ceil": stmt = Assign(v, W, $"ceil({Operand(n, 0, W)})"); outsExpr = new[] { v }; break;
                case "Round": stmt = Assign(v, W, $"round({Operand(n, 0, W)})"); outsExpr = new[] { v }; break;
                case "Sign": stmt = Assign(v, W, $"sign({Operand(n, 0, W)})"); outsExpr = new[] { v }; break;
                case "Desaturation":
                    stmt = Assign(v, 3, $"lerp({Operand(n, 0, 3)}, (float3)dot({Operand(n, 0, 3)}, float3(0.299, 0.587, 0.114)), {Operand(n, 1, 1)})");
                    outsExpr = new[] { v };
                    break;
                case "If":
                    stmt = Assign(v, W, $"(abs({Operand(n, 0, 1)} - {Operand(n, 1, 1)}) <= {Operand(n, 5, 1)}) ? {Operand(n, 3, W)} : (({Operand(n, 0, 1)} > {Operand(n, 1, 1)}) ? {Operand(n, 2, W)} : {Operand(n, 4, W)})");
                    outsExpr = new[] { v };
                    break;
                case "Custom":
                {
                    var fn = customFns[id];
                    stmt = Assign(v, W, $"{fn.Name}({string.Join(", ", fn.Inputs.Select((inp, k) => Operand(n, k, inp.Components)))})");
                    outsExpr = new[] { v };
                    break;
                }
                case "Add": stmt = Assign(v, W, $"({Operand(n, 0, W)} + {Operand(n, 1, W)})"); outsExpr = new[] { v }; break;
                case "Subtract": stmt = Assign(v, W, $"({Operand(n, 0, W)} - {Operand(n, 1, W)})"); outsExpr = new[] { v }; break;
                case "Multiply": stmt = Assign(v, W, $"({Operand(n, 0, W)} * {Operand(n, 1, W)})"); outsExpr = new[] { v }; break;
                case "Divide": stmt = Assign(v, W, $"({Operand(n, 0, W)} / {Operand(n, 1, W)})"); outsExpr = new[] { v }; break;
                case "Power": stmt = Assign(v, W, $"pow({Operand(n, 0, W)}, {Operand(n, 1, W)})"); outsExpr = new[] { v }; break;
                case "Min": stmt = Assign(v, W, $"min({Operand(n, 0, W)}, {Operand(n, 1, W)})"); outsExpr = new[] { v }; break;
                case "Max": stmt = Assign(v, W, $"max({Operand(n, 0, W)}, {Operand(n, 1, W)})"); outsExpr = new[] { v }; break;
                case "Step": stmt = Assign(v, W, $"step({Operand(n, 0, W)}, {Operand(n, 1, W)})"); outsExpr = new[] { v }; break;
                case "Dot":
                {
                    int u = Math.Max(inW[0], inW[1]);
                    string e = u == 1 ? $"({Operand(n, 0, 1)} * {Operand(n, 1, 1)})" : $"dot({Operand(n, 0, u)}, {Operand(n, 1, u)})";
                    stmt = Assign(v, 1, e);
                    outsExpr = new[] { v };
                    break;
                }
                case "Lerp": stmt = Assign(v, W, $"lerp({Operand(n, 0, W)}, {Operand(n, 1, W)}, {Operand(n, 2, W)})"); outsExpr = new[] { v }; break;
                case "Clamp": stmt = Assign(v, W, $"clamp({Operand(n, 0, W)}, {Operand(n, 1, W)}, {Operand(n, 2, W)})"); outsExpr = new[] { v }; break;
                case "Saturate": stmt = Assign(v, W, $"saturate({Operand(n, 0, W)})"); outsExpr = new[] { v }; break;
                case "OneMinus": stmt = Assign(v, W, $"(1.0 - {Operand(n, 0, W)})"); outsExpr = new[] { v }; break;
                case "Abs": stmt = Assign(v, W, $"abs({Operand(n, 0, W)})"); outsExpr = new[] { v }; break;
                case "Normalize": stmt = Assign(v, W, $"normalize({Operand(n, 0, W)})"); outsExpr = new[] { v }; break;
                case "Sine": stmt = Assign(v, W, $"sin({Operand(n, 0, W)})"); outsExpr = new[] { v }; break;
                case "Cosine": stmt = Assign(v, W, $"cos({Operand(n, 0, W)})"); outsExpr = new[] { v }; break;
                case "Sqrt": stmt = Assign(v, W, $"sqrt({Operand(n, 0, W)})"); outsExpr = new[] { v }; break;
                case "Floor": stmt = Assign(v, W, $"floor({Operand(n, 0, W)})"); outsExpr = new[] { v }; break;
                case "Frac": stmt = Assign(v, W, $"frac({Operand(n, 0, W)})"); outsExpr = new[] { v }; break;
                case "ComponentMask":
                {
                    var idx = SgCatalog.ParseMask(n.Prop("mask", "rgb"))!;
                    string sw = new string(idx.Select(k => "xyzw"[k]).ToArray());
                    stmt = Assign(v, W, $"({Operand(n, 0, inW[0])}).{sw}");
                    outsExpr = new[] { v };
                    break;
                }
                case "Append":
                    stmt = Assign(v, W, $"{SgCatalog.TypeName(W)}({Operand(n, 0, inW[0])}, {Operand(n, 1, inW[1])})");
                    outsExpr = new[] { v };
                    break;
                case "Split":
                {
                    int w0 = inW[0];
                    stmt = Assign(v, w0, Operand(n, 0, w0));
                    outsExpr = w0 == 1 ? new[] { v, v, v, v } : new[] { v + ".x", v + ".y", v + ".z", v + ".w" };
                    break;
                }
                default:
                    errors.Add(new SgDiagnostic(id, null, true, $"no HLSL for node type '{n.Type}'"));
                    continue;
            }
            vars[id] = outsExpr;
            var stmtLines = stmt.Split('\n');
            for (int k = 0; k < stmtLines.Length; k++)
                L($"    {stmtLines[k]}" + (k == 0 ? $"   // {title}" : ""), id);
        }
        if (errors.Count > 0) return Fail(sig, errors);

        // ---- output
        var output = byId[doc.Output!.Id];
        string color = Operand(output, 0, 3);
        string opacity = Operand(output, 1, 1);
        L();
        L($"    float3 baseColor = {color};", output.Id);
        L($"    float opacity = {opacity};", output.Id);
        if (encode)
        {
            L("    // Riot's colour blobs end with this: a render target flagged sRGB gets the colour raised to 2.2");
            L("    if (RENDERTARGET_IS_SRGB != 0) baseColor = pow(max(baseColor, 0.0), 2.2);");
        }
        L("    o0 = float4(baseColor, opacity);");
        foreach (var o in sig.Outputs.Skip(1))
            L($"    o{o.Index} = float4(0.0, 0.0, 0.0, 1.0);   // every cooked colour blob writes this constant to the extra targets");
        L("}");

        var sb = new StringBuilder();
        foreach (string line in lines) sb.Append(line).Append('\n');
        string source = sb.ToString();
        foreach (char c in source)
            if (c > 126 || (c < 32 && c != '\n')) throw new InvalidOperationException("the generated HLSL contains a non-ASCII or control character");

        return new SgHlslResult
        {
            Signature = sig, Source = source, LineNodes = lineNodes,
            Textures = textures, Parameters = parameters, UsesTime = usesTime, EncodesOutput = encode,
            CustomParameters = parameters.Select(doc.FindParam).Where(x => x is not null).Select(x => x!).ToList(),
            CustomTextures = textures.Select(doc.FindTexture).Where(x => x is not null).Select(x => x!).ToList(),
        };
    }

    // ================================================================================== helpers

    private static (string Resource, string Sampler) TextureNames(ShaderGraphDocument doc, SgBase b, string name) =>
        doc.FindTexture(name) is { } c ? (c.Resource, c.Sampler) : (b.Texture(name)!.Resource, b.Texture(name)!.Sampler);

    private static SgHlslResult Fail(SgSignature sig, List<SgDiagnostic> errors) => new() { Signature = sig, Errors = errors };

    private static bool HasUv(SgSignature sig, int set) => sig.Input("TEXCOORD", set) is { } e && e.ComponentCount >= 2;

    /// <summary>The struct field name of a signature element (<c>pos</c>, <c>tc1</c>, <c>col0</c>).</summary>
    public static string FieldOf(Shaders.DxbcSignatureElement e)
    {
        string s = e.Semantic.ToLowerInvariant();
        return s switch
        {
            "sv_position" => "pos",
            "texcoord" => "tc" + e.Index,
            "color" => "col" + e.Index,
            _ => Regex.Replace(s, "[^a-z0-9_]", "_") + e.Index,
        };
    }

    private static string Assign(string v, int width, string expr) => $"{SgCatalog.TypeName(width)} {v} = {expr};";

    private static string Broadcast(string expr, int from, int to) =>
        from == to || to <= 0 ? expr : from == 1 ? $"(({SgCatalog.TypeName(to)}){expr})" : expr;

    private static string Literal(float f, int width) =>
        width <= 1 ? Float(f) : $"{SgCatalog.TypeName(width)}({string.Join(", ", Enumerable.Repeat(Float(f), width))})";

    public static string Float(float f)
    {
        string s = f.ToString("R", CultureInfo.InvariantCulture);
        return s.Contains('.') || s.Contains('E') || s.Contains('e') ? s : s + ".0";
    }

    private static string Ascii(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(c is >= ' ' and <= '~' && c != '"' ? c : '?');
        return sb.ToString();
    }

    /// <summary>The engine cbuffer as HLSL with explicit packoffsets (so the layout is Riot's even though most variables go unused).</summary>
    private static string? FrameBuffer(SgEngineBuffer buf, out string? why)
    {
        why = null;
        var sb = new StringBuilder();
        sb.Append("cbuffer ").Append(buf.Name).Append(" : register(b1)\n{\n");
        foreach (var v in buf.Variables)
        {
            var m = Regex.Match(v.TypeName, @"^(float|uint|int)([1-4])?(?:x([1-4]))?(\[(\d+)\])?$");
            if (!m.Success) { why = $"{v.Name} has the type '{v.TypeName}'"; return null; }
            if (v.Offset < 0 || v.Offset % 4 != 0) { why = $"{v.Name} sits at offset {v.Offset}"; return null; }
            int reg = v.Offset / 16, comp = (v.Offset % 16) / 4;
            string pack = m.Groups[3].Success || m.Groups[5].Success ? $"c{reg}" : $"c{reg}.{"xyzw"[comp]}";
            if ((m.Groups[3].Success || m.Groups[5].Success) && comp != 0) { why = $"{v.Name} is an array or matrix at offset {v.Offset}"; return null; }
            sb.Append("    ").Append(v.TypeName.Contains('[') ? v.TypeName[..v.TypeName.IndexOf('[')] : v.TypeName).Append(' ').Append(v.Name)
              .Append(m.Groups[4].Success ? m.Groups[4].Value : "").Append(" : packoffset(").Append(pack).Append(");\n");
        }
        sb.Append("};");
        return sb.ToString();
    }
}
