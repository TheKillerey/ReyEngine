using System.Globalization;

namespace ReyEngine.Formats.Materials.ShaderGraph;

/// <summary>A problem the analyser or the compiler found, on a node (and its pin, when it is about one). Warnings never stop a compile.</summary>
public sealed record SgDiagnostic(string NodeId, string? Pin, bool IsError, string Message)
{
    /// <summary>Other input pins the problem is about (a width mismatch is about BOTH operands): their wires are drawn red too.</summary>
    public IReadOnlyList<string>? AlsoPins { get; init; }

    public bool Concerns(string pin) => Pin == pin || AlsoPins?.Contains(pin) == true;

    public override string ToString() => (IsError ? "error" : "warning") + (NodeId.Length > 0 ? $" [{NodeId}{(Pin is null ? "" : "." + Pin)}]" : "") + ": " + Message;
}

/// <summary>
/// M832: what the type pass found. <see cref="OutputWidths"/> is the float width of every output pin of every node (0 = no value: the
/// node is broken or the pin is not available), <see cref="Order"/> the nodes the output depends on, dependencies first.
/// </summary>
public sealed class SgAnalysis
{
    public required IReadOnlyList<SgDiagnostic> Diagnostics { get; init; }
    public required IReadOnlyDictionary<string, int[]> OutputWidths { get; init; }

    /// <summary>The widths of the values arriving at each input pin (the unconnected default's width where nothing is wired).</summary>
    public required IReadOnlyDictionary<string, int[]> InputWidths { get; init; }
    public required IReadOnlyList<string> Order { get; init; }
    public required IReadOnlySet<string> Reachable { get; init; }

    public bool HasErrors => Diagnostics.Any(d => d.IsError);
    public IEnumerable<SgDiagnostic> Errors => Diagnostics.Where(d => d.IsError);
    public IEnumerable<SgDiagnostic> For(string nodeId) => Diagnostics.Where(d => d.NodeId == nodeId);
    public bool NodeHasError(string nodeId) => Diagnostics.Any(d => d.IsError && d.NodeId == nodeId);

    public int OutputWidth(string node, int pin) =>
        OutputWidths.TryGetValue(node, out var w) && pin >= 0 && pin < w.Length ? w[pin] : 0;
}

/// <summary>
/// M832: the Shader Graph's type pass - Unreal's promotion rules over float widths 1..4: a scalar broadcasts to any width; two vectors
/// must be the same width; a fixed-width pin (UV is float2, Base Color float3, Opacity float) takes its width or a scalar. A mismatch,
/// an undeclared texture or parameter, a missing engine source and a wire cycle are errors on the node that owns them.
/// </summary>
public static class SgAnalyzer
{
    public static SgAnalysis Analyze(ShaderGraphDocument doc, SgBase? b)
    {
        var diags = new List<SgDiagnostic>();
        var outW = new Dictionary<string, int[]>(StringComparer.Ordinal);
        var inW = new Dictionary<string, int[]>(StringComparer.Ordinal);
        var nodeById = new Dictionary<string, SgNode>(StringComparer.Ordinal);
        foreach (var n in doc.Nodes) nodeById.TryAdd(n.Id, n);
        // links indexed by the node they end at: a pin looks at its own node's wires, not at all of them
        var linksTo = new Dictionary<string, List<SgLink>>(StringComparer.Ordinal);
        foreach (var l in doc.Links)
        {
            if (!linksTo.TryGetValue(l.ToNode, out var list)) linksTo[l.ToNode] = list = new List<SgLink>();
            list.Add(l);
        }
        var noLinks = new List<SgLink>();

        void Err(SgNode n, string? pin, string msg) => diags.Add(new SgDiagnostic(n.Id, pin, true, msg));

        // Dependencies first, found iteratively (a 4096-node chain must not overflow the stack): a wire back to a node that is still
        // being visited is a loop, reported once on that node.
        var evalOrder = new List<SgNode>();
        var visit = new Dictionary<string, int>(StringComparer.Ordinal);   // 1 = in progress, 2 = done
        foreach (var root in doc.Nodes)
        {
            if (visit.ContainsKey(root.Id)) continue;
            var stack = new Stack<(SgNode Node, int Next)>();
            stack.Push((root, 0));
            visit[root.Id] = 1;
            while (stack.Count > 0)
            {
                var (n, next) = stack.Pop();
                var deps = linksTo.TryGetValue(n.Id, out var ls) ? ls : noLinks;
                if (next < deps.Count)
                {
                    stack.Push((n, next + 1));
                    if (!nodeById.TryGetValue(deps[next].FromNode, out var src)) continue;
                    if (!visit.TryGetValue(src.Id, out int sv)) { visit[src.Id] = 1; stack.Push((src, 0)); }
                    else if (sv == 1 && !diags.Any(d => d.NodeId == src.Id && d.Message.Contains("loop"))) Err(src, null, "the wires form a loop through this node");
                }
                else { visit[n.Id] = 2; evalOrder.Add(n); }
            }
        }

        void Eval(SgNode n)
        {
            var def = SgCatalog.Get(n.Type);
            int[] result;
            if (def is null)
            {
                Err(n, null, $"unknown node type '{n.Type}'");
                result = Array.Empty<int>();
                inW[n.Id] = Array.Empty<int>();
            }
            else result = EvalNode(n, def);
            outW[n.Id] = result;
        }

        int[] EvalNode(SgNode n, SgNodeDef def)
        {
            // ---- resolve the width arriving at each input pin
            var ins = new int[def.Inputs.Count];
            for (int i = 0; i < ins.Length; i++)
            {
                var pin = def.Inputs[i];
                var links = (linksTo.TryGetValue(n.Id, out var all) ? all : noLinks).Where(l => l.ToPin == pin.Name).ToList();
                if (links.Count > 1) Err(n, pin.Name, $"{links.Count} wires end at '{pin.Name}'; an input takes one");
                int w;
                if (links.Count == 0) w = pin.Fixed > 0 ? pin.Fixed : 1;
                else
                {
                    var l = links[0];
                    if (!nodeById.TryGetValue(l.FromNode, out var src)) { Err(n, pin.Name, $"the wire comes from a node that does not exist ({l.FromNode})"); w = 0; }
                    else
                    {
                        var sdef = SgCatalog.Get(src.Type);
                        int sp = sdef?.OutputIndex(l.FromPin) ?? -1;
                        var sw = outW.TryGetValue(src.Id, out var known) ? known : Array.Empty<int>();
                        if (sdef is not null && sp < 0) { Err(n, pin.Name, $"{src.Type} has no output '{l.FromPin}'"); w = 0; }
                        else if (sp < 0 || sp >= sw.Length) w = 0;   // the source is broken; it said why
                        else
                        {
                            w = sw[sp];
                            if (w == 0 && !NodeHasErr(diags, src.Id))
                                Err(n, pin.Name, $"{src.Title()}.{l.FromPin} carries no value (the component is not in its input)");
                        }
                    }
                }
                if (w > 0 && links.Count > 0 && pin.Fixed > 0 && w != pin.Fixed && w != 1)
                    { Err(n, pin.Name, $"'{pin.Name}' takes {SgCatalog.TypeName(pin.Fixed)} (or a scalar), but a {SgCatalog.TypeName(w)} arrives"); w = 0; }
                ins[i] = w;
            }
            inW[n.Id] = ins;

            bool anyBad = ins.Any(x => x == 0);
            int A = ins.Length > 0 ? ins[0] : 0, B = ins.Length > 1 ? ins[1] : 0, C = ins.Length > 2 ? ins[2] : 0;

            int Unify(int x, int y, string px, string py)
            {
                if (x == 0 || y == 0) return 0;
                if (x == y) return x;
                if (x == 1) return y;
                if (y == 1) return x;
                diags.Add(new SgDiagnostic(n.Id, px, true, $"{px} is {SgCatalog.TypeName(x)} but {py} is {SgCatalog.TypeName(y)}: only a scalar can be mixed with a vector") { AlsoPins = new[] { py } });
                return 0;
            }

            int[] One(int v) => new[] { v };

            switch (def.Type)
            {
                case "Constant":
                    if (!SgCatalog.TryParseConstant(n.Prop("value"), out var cv)) { Err(n, null, "the value must be 1 to 4 numbers"); return One(0); }
                    return One(cv.Length);

                case "Parameter":
                {
                    string name = n.Prop("name");
                    if (name.Length == 0) { Err(n, null, "choose a parameter"); return One(0); }
                    if (b is null) return One(4);
                    var p = b.Parameter(name);
                    if (p is null) { Err(n, null, $"'{name}' is not a parameter {ShortName(b)} declares in shaders.bin: the game would not fill it"); return One(0); }
                    return One(p.Components);
                }

                case "TexCoord":
                {
                    if (!int.TryParse(n.Prop("set", (b?.UvSet ?? 1).ToString(CultureInfo.InvariantCulture)), NumberStyles.None, CultureInfo.InvariantCulture, out int set) || set > 15)
                    { Err(n, null, "the TEXCOORD set must be a number 0..15"); return One(0); }
                    if (b is not null && b.Signatures.Count > 0)
                    {
                        int have = b.Signatures.Count(s => s.Input("TEXCOORD", set) is { } e && e.ComponentCount >= 2);
                        if (have == 0) { Err(n, null, $"no pixel input signature of {ShortName(b)} carries TEXCOORD{set}"); return One(0); }
                        if (have < b.Signatures.Count)
                            diags.Add(new SgDiagnostic(n.Id, null, false, $"TEXCOORD{set} is missing in {b.Signatures.Count - have} of {b.Signatures.Count} pixel input signatures: the graph compiles for the other {have} only"));
                    }
                    return One(2);
                }

                case "VertexColor":
                    if (b is not null && !b.HasVertexColor) { Err(n, null, $"{ShortName(b)} does not write COLOR to the pixel stage"); return One(0); }
                    return One(4);

                case "Time":
                    if (b is not null && !b.HasTime) { Err(n, null, $"{ShortName(b)} reads no engine cbuffer that provides TIME"); return One(0); }
                    return One(1);

                case "TextureSample":
                {
                    string tex = n.Prop("texture");
                    bool ok = true;
                    if (tex.Length == 0) { Err(n, null, "choose a texture"); ok = false; }
                    else if (b is not null && b.Texture(tex) is null) { Err(n, null, $"'{tex}' is not a texture {ShortName(b)} declares: the game would not bind it"); ok = false; }
                    if (anyBad) ok = false;
                    return ok ? new[] { 4, 3, 1, 1, 1, 1 } : new[] { 0, 0, 0, 0, 0, 0 };
                }

                case "Add" or "Subtract" or "Multiply" or "Divide" or "Power" or "Min" or "Max" or "Step":
                    return One(Unify(A, B, def.Inputs[0].Name, def.Inputs[1].Name));

                case "Dot":
                {
                    int u = Unify(A, B, "A", "B");
                    return One(u == 0 ? 0 : 1);
                }

                case "Lerp":
                {
                    int u = Unify(A, B, "A", "B");
                    if (u == 0 || C == 0) return One(0);
                    if (C != 1 && C != u) { Err(n, "Alpha", $"Alpha is {SgCatalog.TypeName(C)}: it must be a scalar or match the {SgCatalog.TypeName(u)} it blends"); return One(0); }
                    return One(u);
                }

                case "Clamp":
                {
                    int u = Unify(A, B, "Value", "Min");
                    return One(Unify(u, C, "Value", "Max"));
                }

                case "Saturate" or "OneMinus" or "Abs" or "Sine" or "Cosine" or "Sqrt" or "Floor" or "Frac":
                    return One(A);

                case "Normalize":
                    if (A == 1) { Err(n, null, "Normalize needs a vector (float2 to float4)"); return One(0); }
                    return One(A);

                case "ComponentMask":
                {
                    var idx = SgCatalog.ParseMask(n.Prop("mask", "rgb"));
                    if (idx is null) { Err(n, null, "the mask must be 1 to 4 of r g b a (or x y z w)"); return One(0); }
                    if (A == 0) return One(0);
                    foreach (int k in idx)
                        if (k >= A) { Err(n, null, $"the mask asks for component {"rgba"[k]}, but the input is {SgCatalog.TypeName(A)}"); return One(0); }
                    return One(idx.Length);
                }

                case "Append":
                    if (A == 0 || B == 0) return One(0);
                    if (A + B > 4) { Err(n, null, $"{SgCatalog.TypeName(A)} and {SgCatalog.TypeName(B)} make {A + B} components; the most is 4"); return One(0); }
                    return One(A + B);

                case "Split":
                {
                    var o = new int[4];
                    for (int k = 0; k < 4; k++) o[k] = A > 0 && k < A ? 1 : 0;
                    return o;
                }

                case SgCatalog.OutputType:
                    return Array.Empty<int>();

                default:
                    Err(n, null, $"node type '{def.Type}' has no typing rule");
                    return new int[def.Outputs.Count];
            }
        }

        foreach (var n in evalOrder) Eval(n);

        // ---- the output node and reachability
        var outputs = doc.Nodes.Where(n => n.Type == SgCatalog.OutputType).ToList();
        var reach = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<string>();
        if (outputs.Count == 0) diags.Add(new SgDiagnostic("", null, true, "the graph has no Shader Output node"));
        else if (outputs.Count > 1)
            foreach (var o in outputs.Skip(1)) diags.Add(new SgDiagnostic(o.Id, null, true, "a graph has one Shader Output node"));
        if (outputs.Count > 0)
        {
            var st = new Stack<(SgNode Node, int Next)>();
            reach.Add(outputs[0].Id);
            st.Push((outputs[0], 0));
            while (st.Count > 0)
            {
                var (n, next) = st.Pop();
                var deps = linksTo.TryGetValue(n.Id, out var ls) ? ls : noLinks;
                if (next < deps.Count)
                {
                    st.Push((n, next + 1));
                    if (nodeById.TryGetValue(deps[next].FromNode, out var src) && reach.Add(src.Id)) st.Push((src, 0));
                }
                else order.Add(n.Id);
            }
        }

        // errors on nodes the output does not depend on are warnings: they do not stop a compile
        var final = new List<SgDiagnostic>();
        foreach (var d in diags)
            final.Add(d.NodeId.Length > 0 && outputs.Count > 0 && !reach.Contains(d.NodeId) && d.IsError && !outputs.Any(o => o.Id == d.NodeId)
                ? d with { IsError = false, Message = d.Message + " (not connected to the output)" } : d);

        return new SgAnalysis
        {
            Diagnostics = final, OutputWidths = outW, InputWidths = inW, Order = order, Reachable = reach,
        };
    }

    private static bool NodeHasErr(List<SgDiagnostic> d, string id) => d.Any(x => x.IsError && x.NodeId == id);

    private static string ShortName(SgBase b)
    {
        int cut = b.Shader.LastIndexOf('/');
        return cut >= 0 ? b.Shader[(cut + 1)..] : b.Shader;
    }

    private static string Title(this SgNode n) => SgCatalog.Get(n.Type)?.Title ?? n.Type;
}
