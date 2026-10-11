using ReyEngine.Formats.Materials.Graph;

namespace ReyEngine.Formats.Materials.ShaderGraph;

/// <summary>
/// M832: draws a Shader Graph with the Material Graph's canvas. The canvas paints <see cref="MaterialGraph"/> (nodes with typed pins,
/// wires, a state per node), so a Shader Graph document is PROJECTED onto one after every edit: same ids, same positions, pin colours by
/// float width, an error state on the nodes the analyser (or the compiler) flagged and red wires into the inputs they flagged.
/// The projection holds no reference back into the document.
/// </summary>
public static class SgGraphView
{
    public const double NodeWidth = 210, OutputWidth = 230;

    public static MaterialGraph Build(ShaderGraphDocument doc, SgBase? b, SgAnalysis analysis, IReadOnlyList<SgDiagnostic>? extra = null)
    {
        var diags = new List<SgDiagnostic>(analysis.Diagnostics);
        if (extra is not null) foreach (var d in extra) if (!diags.Contains(d)) diags.Add(d);

        var nodes = new List<GraphNode>();
        var index = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        foreach (var n in doc.Nodes)
        {
            var def = SgCatalog.DefFor(n);
            var errors = diags.Where(d => d.IsError && d.NodeId == n.Id).ToList();
            var warn = diags.Where(d => !d.IsError && d.NodeId == n.Id).ToList();
            string message = string.Join("\n", errors.Concat(warn).Select(d => (d.IsError ? "" : "(warning) ") + d.Message));
            int[] inW = analysis.InputWidths.TryGetValue(n.Id, out var iw) ? iw : Array.Empty<int>();
            int[] outW = analysis.OutputWidths.TryGetValue(n.Id, out var ow) ? ow : Array.Empty<int>();

            var inputs = new List<GraphPin>();
            var outputs = new List<GraphPin>();
            if (def is not null)
            {
                for (int i = 0; i < def.Inputs.Count; i++)
                {
                    var pd = def.Inputs[i];
                    bool linked = doc.LinkInto(n.Id, pd.Name) is not null;
                    int w = i < inW.Length ? inW[i] : 0;
                    string detail = linked ? (w > 0 ? SgCatalog.TypeName(w) : "?")
                        : SgCatalog.IsUvPin(n, pd) ? "UV" + (b?.UvSet ?? 1)
                        : SgCatalog.IsTimePin(n, pd) ? (b is null || b.HasTime ? "TIME" : "0")
                        : SgHlsl.Float(SgCatalog.PinDefault(n, pd));
                    inputs.Add(new GraphPin
                    {
                        Name = pd.Name, IsInput = true, Row = i, Linked = linked, Detail = detail,
                        Kind = pd.Name == SgCatalog.BaseColorPin ? GraphPinKind.Color : KindOf(pd.Fixed > 0 ? pd.Fixed : 1),
                        Components = pd.Fixed,
                    });
                }
                for (int o = 0; o < def.Outputs.Count; o++)
                {
                    int w = o < outW.Length ? outW[o] : 0;
                    bool linked = doc.Links.Any(l => l.FromNode == n.Id && l.FromPin == def.Outputs[o]);
                    outputs.Add(new GraphPin
                    {
                        Name = def.Outputs[o], IsInput = false, Row = o, Linked = linked,
                        Kind = KindOf(w > 0 ? w : 1), Components = w,
                        Detail = ValueText(n, def, w),
                    });
                }
            }

            int rows = Math.Max(1, Math.Max(inputs.Count, outputs.Count));
            var node = new GraphNode
            {
                Id = n.Id,
                Kind = n.Type switch
                {
                    "TextureSample" => GraphNodeKind.Sample,
                    "Parameter" or "Constant" or "ConstantColor" or "TexCoord" or "VertexColor" or "Time" => GraphNodeKind.Input,
                    SgCatalog.OutputType => GraphNodeKind.Output,
                    _ => GraphNodeKind.Math,
                },
                Group = n.Type switch
                {
                    "TextureSample" => GraphGroup.Textures,
                    "Parameter" or "Constant" or "ConstantColor" or "TexCoord" or "VertexColor" or "Time" => GraphGroup.Parameters,
                    SgCatalog.OutputType => GraphGroup.Output,
                    _ => GraphGroup.Switches,
                },
                Title = n.Label.Length > 0 ? n.Label : TitleOf(n, def),
                Subtitle = n.Label.Length > 0 ? (TitleOf(n, def) + " " + SubtitleOf(n, def, doc)).Trim() : SubtitleOf(n, def, doc),
                Swatch = n.Type == "ConstantColor" && SgCatalog.TryParseConstant(n.Prop("value", "1 1 1 1"), out var sw) && sw.Length == 4 ? new System.Numerics.Vector4(sw[0], sw[1], sw[2], sw[3])
                    : n.Type == "Parameter" && doc.FindParam(n.Prop("name")) is { IsColor: true } cp && cp.DefaultValues() is { Length: >= 3 } dv ? new System.Numerics.Vector4(dv[0], dv[1], dv[2], dv.Length > 3 ? dv[3] : 1f) : null,
                State = errors.Count > 0 ? GraphNodeState.Error
                    : analysis.Reachable.Count > 0 && !analysis.Reachable.Contains(n.Id) ? GraphNodeState.Unused
                    : GraphNodeState.Authored,
                Inputs = inputs,
                Outputs = outputs,
                Source = n.Type == "Parameter" ? n.Prop("name") : n.Type == "TextureSample" ? n.Prop("texture") : n.Type,
                Message = message,
            };
            node.Width = n.Type == SgCatalog.OutputType ? OutputWidth : NodeWidth;
            node.Height = GraphMetrics.BodyTop(rows) - GraphMetrics.PinBlockPad + (n.Type == SgCatalog.OutputType ? 4 : 0);
            node.SetPosition(n.X, n.Y);
            nodes.Add(node);
            index[n.Id] = node;
        }

        var wires = new List<GraphWire>();
        foreach (var l in doc.Links)
        {
            if (!index.TryGetValue(l.FromNode, out var from) || !index.TryGetValue(l.ToNode, out var to)) continue;
            int fp = from.Outputs.ToList().FindIndex(p => p.Name == l.FromPin);
            int tp = to.Inputs.ToList().FindIndex(p => p.Name == l.ToPin);
            if (fp < 0 || tp < 0) continue;
            bool bad = diags.Any(d => d.IsError && d.NodeId == l.ToNode && d.Concerns(l.ToPin));
            wires.Add(new GraphWire { FromNode = l.FromNode, FromPin = fp, ToNode = l.ToNode, ToPin = tp, SourceKind = from.Kind, Error = bad });
        }

        var graph = new MaterialGraph
        {
            MaterialName = doc.Name,
            ShaderName = doc.BaseShader,
            Nodes = nodes,
            Wires = wires,
            Notes = diags.Where(d => d.NodeId.Length == 0).Select(d => d.Message).ToList(),
            Comments = doc.Comments.Select(c => new GraphComment { Id = c.Id, Title = c.Title, ColorKey = c.Color, X = c.X, Y = c.Y, Width = c.Width, Height = c.Height }).ToList(),
        };
        graph.UpdateBounds();
        if (graph.Comments.Count > 0)
        {
            var (bx, by, bw, bh) = graph.Bounds;
            double x0 = bw > 0 ? bx : double.MaxValue, y0 = bh > 0 ? by : double.MaxValue, x1 = bw > 0 ? bx + bw : double.MinValue, y1 = bh > 0 ? by + bh : double.MinValue;
            foreach (var c in graph.Comments) { x0 = Math.Min(x0, c.X); y0 = Math.Min(y0, c.Y); x1 = Math.Max(x1, c.Right); y1 = Math.Max(y1, c.Bottom); }
            graph.Bounds = (x0, y0, x1 - x0, y1 - y0);
        }
        return graph;
    }

    private static GraphPinKind KindOf(int width) => width <= 1 ? GraphPinKind.Scalar : GraphPinKind.Vector;

    private static string TitleOf(SgNode n, SgNodeDef? def)
    {
        if (def is null) return "? " + n.Type;
        return n.Type switch
        {
            "TextureSample" => n.Prop("texture").Length > 0 ? n.Prop("texture") : def.Title,
            "Parameter" => n.Prop("name").Length > 0 ? n.Prop("name") : def.Title,
            "Constant" => SgCatalog.TryParseConstant(n.Prop("value"), out var cv) ? "Constant " + SgCatalog.FormatConstant(cv) : def.Title,
            "TexCoord" => "TexCoord " + n.Prop("set", "1"),
            "ComponentMask" => "Mask (" + n.Prop("mask", "rgb") + ")",
            _ => def.Title,
        };
    }

    private static string SubtitleOf(SgNode n, SgNodeDef? def, ShaderGraphDocument doc) => n.Type switch
    {
        "TextureSample" => doc.FindTexture(n.Prop("texture")) is not null ? "Texture Parameter (custom)" : "Texture Sample",
        "Parameter" => doc.FindParam(n.Prop("name")) is not null ? "Parameter (custom)" : "Parameter (base shader)",
        "ConstantColor" => "Colour",
        "Custom" => "HLSL",
        "TexCoord" => "UV set",
        "Constant" => "Constant",
        "VertexColor" => "COLOR0",
        "Time" => "TIME.x",
        _ => "",
    };

    private static string ValueText(SgNode n, SgNodeDef def, int width) => n.Type switch
    {
        "Constant" or "ConstantColor" => SgCatalog.TryParseConstant(n.Prop("value"), out var v) ? SgCatalog.FormatConstant(v) : "?",
        "Parameter" or "TexCoord" or "VertexColor" or "Time" => width > 0 ? SgCatalog.TypeName(width) : "?",
        _ => width > 0 && def.Outputs.Count == 1 ? SgCatalog.TypeName(width) : "",
    };
}
