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
            var def = SgCatalog.Get(n.Type);
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
                        : n.Type == "TextureSample" && pd.Name == "UV" ? "UV" + (b?.UvSet ?? 1)
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
                    "Parameter" or "Constant" or "TexCoord" or "VertexColor" or "Time" => GraphNodeKind.Input,
                    SgCatalog.OutputType => GraphNodeKind.Output,
                    _ => GraphNodeKind.Math,
                },
                Group = n.Type switch
                {
                    "TextureSample" => GraphGroup.Textures,
                    "Parameter" or "Constant" or "TexCoord" or "VertexColor" or "Time" => GraphGroup.Parameters,
                    SgCatalog.OutputType => GraphGroup.Output,
                    _ => GraphGroup.Switches,
                },
                Title = TitleOf(n, def),
                Subtitle = SubtitleOf(n, def),
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
        };
        graph.UpdateBounds();
        return graph;
    }

    private static GraphPinKind KindOf(int width) => width <= 1 ? GraphPinKind.Scalar : GraphPinKind.Vector;

    private static string TitleOf(SgNode n, SgNodeDef? def)
    {
        if (def is null) return "? " + n.Type;
        return n.Type switch
        {
            "TextureSample" => n.Prop("texture").Length > 0 ? "Sample " + n.Prop("texture") : def.Title,
            "ComponentMask" => "Mask (" + n.Prop("mask", "rgb") + ")",
            _ => def.Title,
        };
    }

    private static string SubtitleOf(SgNode n, SgNodeDef? def) => n.Type switch
    {
        "Parameter" => n.Prop("name"),
        "TexCoord" => "TexCoord " + n.Prop("set", "1"),
        "Constant" => "Constant",
        "VertexColor" => "COLOR0",
        "Time" => "TIME.x",
        _ => "",
    };

    private static string ValueText(SgNode n, SgNodeDef def, int width) => n.Type switch
    {
        "Constant" => SgCatalog.TryParseConstant(n.Prop("value"), out var v) ? SgCatalog.FormatConstant(v) : "?",
        "Parameter" or "TexCoord" or "VertexColor" or "Time" => width > 0 ? SgCatalog.TypeName(width) : "?",
        _ => width > 0 && def.Outputs.Count == 1 ? SgCatalog.TypeName(width) : "",
    };
}
