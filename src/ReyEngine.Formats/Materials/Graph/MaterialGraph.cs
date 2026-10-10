using System.Numerics;

namespace ReyEngine.Formats.Materials.Graph;

/// <summary>
/// M829: the node graph of one Riot material - what Unreal's Material Editor shows for a material,
/// built from the real <see cref="MaterialBinding"/> (StaticMaterialDef) and the shader it names.
///
/// <para>Nothing in here is invented: a node exists because the bin authors it (a sampler, a paramValues
/// entry, a switch, a shader macro) or because the shader declares it (a texture input, a switch default).
/// The graph is a read-only projection; it holds no reference back into the BinTree, so it can never write
/// to the material. M830: it is still a projection - an edit is made by the Material Editor's own row view models
/// and the graph is REBUILT from the binding afterwards (node ids are positional and stable, so a selection survives);
/// <see cref="GraphNode.Source"/> names the element a node stands for.</para>
/// </summary>
public enum GraphNodeKind
{
    Texture, Scalar, Vector, Color, Switch, Macro, Shader, Output,
    /// <summary>M832 (Shader Graph): a texture sample node (compact, no thumbnail).</summary>
    Sample,
    /// <summary>M832 (Shader Graph): a math or vector node.</summary>
    Math,
    /// <summary>M832 (Shader Graph): a value input (parameter, constant, texcoord, time).</summary>
    Input,
}

/// <summary>Which column / comment frame a node belongs to.</summary>
public enum GraphGroup { Textures, Parameters, Switches, Shader, Output }

public enum GraphPinKind { Texture, Scalar, Vector, Color, Bool, Shader, Attribute }

/// <summary>How a node should read: authored by the material, or only a shader default / unused here.</summary>
public enum GraphNodeState
{
    /// <summary>The material authors it and the shader consumes it.</summary>
    Authored,
    /// <summary>Authored, but the resolved shader permutation does not declare it, so it has no effect.</summary>
    Unused,
    /// <summary>Not authored by the material; shown because the shader defines a default for it.</summary>
    ShaderDefault,
    /// <summary>M832 (Shader Graph): the node has an error (drawn with a red edge; the message is in <see cref="GraphNode.Message"/>).</summary>
    Error,
}

public sealed class GraphPin
{
    public required string Name { get; init; }
    public required bool IsInput { get; init; }
    public GraphPinKind Kind { get; init; }
    /// <summary>Row inside the node's pin block (0-based). Inputs and outputs number separately.</summary>
    public int Row { get; init; }
    /// <summary>True when a wire is attached.</summary>
    public bool Linked { get; internal set; }
    /// <summary>True when the material itself sets what this pin stands for (render-state pins).</summary>
    public bool Shaded { get; init; }
    /// <summary>A short value / explanation shown beside the pin name.</summary>
    public string Detail { get; init; } = "";

    /// <summary>M831: shader input pins: the shader's own identity of the input ("tx:Name", "c:Name", "sw:Name", "mac:Name"). Empty elsewhere.</summary>
    public string Key { get; init; } = "";

    /// <summary>M831: shader input pins: the NAME a material entry must carry to bind this input (the sampler name for a texture,
    /// the parameter name for a constant). A material binds by name, so wiring is writing this name.</summary>
    public string Target { get; init; } = "";

    /// <summary>M831: a texture pin's shape from the shader reflection ("tex2d", "texcube", ...). A texture node's output carries the shape of the
    /// input it feeds, or "tex2d" while it feeds none. Empty for other pins.</summary>
    public string Dimension { get; internal set; } = "";

    /// <summary>M831: a constant pin's component count (1 float, 2, 3, 4) or a parameter output's; 0 = not a numeric vector (cannot be wired).</summary>
    public int Components { get; init; }
}

public readonly record struct GraphDetail(string Category, string Label, string Value);

public sealed class GraphNode
{
    public required string Id { get; init; }
    public required GraphNodeKind Kind { get; init; }
    public required GraphGroup Group { get; init; }
    public required string Title { get; init; }
    public string Subtitle { get; init; } = "";
    public GraphNodeState State { get; init; } = GraphNodeState.Authored;

    public IReadOnlyList<GraphPin> Inputs { get; init; } = Array.Empty<GraphPin>();
    public IReadOnlyList<GraphPin> Outputs { get; init; } = Array.Empty<GraphPin>();

    /// <summary>Short one-line body rows ("value  0.5 0.5 0 0").</summary>
    public IReadOnlyList<(string Label, string Value)> Fields { get; init; } = Array.Empty<(string, string)>();

    /// <summary>The Details panel content for this node.</summary>
    public IReadOnlyList<GraphDetail> Details { get; init; } = Array.Empty<GraphDetail>();

    /// <summary>Texture nodes: the WAD path the thumbnail is decoded from (may be empty).</summary>
    public string TexturePath { get; init; } = "";

    /// <summary>Texture nodes: the WAD chunk hash to load the thumbnail by (works when the path dictionary cannot name it).</summary>
    public ulong TextureChunk { get; init; }

    /// <summary>Colour nodes: the colour, 0..1 per channel (not clamped - a brightness boost stays visible).</summary>
    public Vector4? Swatch { get; init; }

    /// <summary>The sampler / parameter / switch / macro name in the bin.</summary>
    public string Source { get; init; } = "";

    /// <summary>M832: what is wrong with the node (Shader Graph errors), shown in its tooltip. Empty = nothing.</summary>
    public string Message { get; init; } = "";

    // ---- layout, in graph units. Written by the layout pass, which is deterministic. ----
    public double X { get; internal set; }
    public double Y { get; internal set; }
    public double Width { get; internal set; }
    public double Height { get; internal set; }

    public double Right => X + Width;
    public double Bottom => Y + Height;

    /// <summary>Centre of a pin's dot, in graph space. Inputs sit on the left edge, outputs on the right.</summary>
    public (double X, double Y) PinPosition(GraphPin pin) =>
        (pin.IsInput ? X : X + Width, Y + GraphMetrics.PinRowCentre(pin.Row));

    public bool Contains(double x, double y) => x >= X && x <= X + Width && y >= Y && y <= Y + Height;

    /// <summary>M831: move the node (the person dragged it). Layout only - nothing about the material.</summary>
    public void SetPosition(double x, double y) { X = x; Y = y; }
}

public sealed class GraphWire
{
    public required string FromNode { get; init; }
    public required int FromPin { get; init; }
    public required string ToNode { get; init; }
    public required int ToPin { get; init; }
    /// <summary>The kind of the source node, so a view can colour the wire by what feeds it.</summary>
    public required GraphNodeKind SourceKind { get; init; }

    /// <summary>M832: the wire ends at an input that reports an error (drawn red).</summary>
    public bool Error { get; init; }
}

/// <summary>A grey comment frame around a group of nodes (Unreal's comment box).</summary>
public sealed class GraphFrame
{
    public required string Title { get; init; }
    public required GraphGroup Group { get; init; }
    public double X { get; internal set; }
    public double Y { get; internal set; }
    public double Width { get; internal set; }
    public double Height { get; internal set; }
}

/// <summary>Sizes shared by the layout and the drawing, so a wire ends exactly where a pin is painted.</summary>
public static class GraphMetrics
{
    public const double HeaderHeight = 24;
    public const double PinRowHeight = 18;
    public const double PinBlockPad = 6;
    public const double ThumbSize = 96;
    public const double BodyPad = 8;
    public const double ColumnGap = 84;
    public const double RowGap = 14;
    public const double FramePad = 18;
    public const double FrameTitleHeight = 26;

    /// <summary>Distance from a node's top to the centre of pin row <paramref name="row"/>.</summary>
    public static double PinRowCentre(int row) => HeaderHeight + PinBlockPad + row * PinRowHeight + PinRowHeight / 2;

    /// <summary>Distance from a node's top to the first body row (below the pin block).</summary>
    public static double BodyTop(int pinRows) => HeaderHeight + PinBlockPad * 2 + pinRows * PinRowHeight;
}

public sealed class MaterialGraph
{
    public required string MaterialName { get; init; }
    public string ShaderName { get; init; } = "";
    public IReadOnlyList<GraphNode> Nodes { get; init; } = Array.Empty<GraphNode>();
    public IReadOnlyList<GraphWire> Wires { get; init; } = Array.Empty<GraphWire>();
    public IReadOnlyList<GraphFrame> Frames { get; init; } = Array.Empty<GraphFrame>();

    /// <summary>Anything the builder could not show faithfully (no shader reflection, unmapped samplers...).</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>Bounds of everything drawn, in graph units: x, y, width, height.</summary>
    public (double X, double Y, double Width, double Height) Bounds { get; internal set; }

    public GraphNode? Find(string id)
    {
        foreach (var n in Nodes) if (n.Id == id) return n;
        return null;
    }

    /// <summary>M831: recompute <see cref="Bounds"/> after nodes were moved.</summary>
    public void UpdateBounds() => Bounds = MaterialGraphBuilder.BoundsOf(Nodes, Frames);

    public GraphNode? ShaderNode => Nodes.FirstOrDefault(n => n.Kind == GraphNodeKind.Shader);
    public GraphNode? OutputNode => Nodes.FirstOrDefault(n => n.Kind == GraphNodeKind.Output);

    /// <summary>The node itself plus every node a wire joins to it directly (Unreal's "Hide Unrelated").</summary>
    public HashSet<string> RelatedTo(string nodeId)
    {
        var set = new HashSet<string>(StringComparer.Ordinal) { nodeId };
        foreach (var w in Wires)
        {
            if (w.FromNode == nodeId) set.Add(w.ToNode);
            else if (w.ToNode == nodeId) set.Add(w.FromNode);
        }
        return set;
    }

    /// <summary>Case-insensitive search over title, subtitle, source name and path. Layout order.</summary>
    public IEnumerable<GraphNode> Search(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        foreach (var n in Nodes)
            if (n.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
                || n.Subtitle.Contains(text, StringComparison.OrdinalIgnoreCase)
                || n.Source.Contains(text, StringComparison.OrdinalIgnoreCase)
                || n.TexturePath.Contains(text, StringComparison.OrdinalIgnoreCase))
                yield return n;
    }
}
