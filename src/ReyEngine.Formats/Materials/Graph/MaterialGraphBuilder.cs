using System.Globalization;
using System.Numerics;

namespace ReyEngine.Formats.Materials.Graph;

/// <summary>
/// M829: turns one <see cref="MaterialBinding"/> (+ optionally the shader it names) into a
/// <see cref="MaterialGraph"/>, and lays it out. Pure and deterministic: the same inputs give the same node ids,
/// the same wire list and the same coordinates, which is what lets a test pin the layout.
///
/// <para>Columns, left to right: Textures | Parameters | Switches &amp; Macros | the shader | the material
/// output. Inside a column nodes are ordered by the shader pin they feed, so wires of one column never cross
/// each other; nodes that feed nothing sit below the ones that do.</para>
/// </summary>
public static class MaterialGraphBuilder
{
    private const double TextureWidth = 210, ParamWidth = 230, SwitchWidth = 230, ShaderWidth = 270, OutputWidth = 270;

    public static MaterialGraph Build(MaterialBinding b, MaterialGraphShaderInfo? info)
    {
        var notes = new List<string>();
        bool reflected = info is not null && (info.Textures.Count > 0 || info.Constants.Count > 0);
        if (info is null) notes.Add("No shader reflection was available, so the shader's own inputs are not listed - wires end on pins named after what they carry.");
        else if (!reflected) notes.Add("The shader declares no texture or constant inputs (or its bytecode did not load).");

        string shaderPath = b.RenderShader ?? "";
        string shaderLeaf = shaderPath.Length == 0 ? "(no renderShader)" : shaderPath.Split('/', '\\').Last();

        // ------------------------------------------------------------- shader input pins
        var shaderIn = new List<GraphPin>();
        var pinIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        int AddShaderPin(string key, string name, GraphPinKind kind, string detail)
        {
            if (pinIndex.TryGetValue(key, out int at)) return at;
            pinIndex[key] = shaderIn.Count;
            // row 0 of the shader node belongs to its Result output (and the shader's path on the left)
            shaderIn.Add(new GraphPin { Name = name, IsInput = true, Kind = kind, Row = shaderIn.Count + 1, Detail = detail });
            return shaderIn.Count - 1;
        }

        // textures: everything the shader declares, in bind order, bound or not
        if (reflected)
            foreach (var t in info!.Textures)
                AddShaderPin("tx:" + t.Name, t.DisplayName, GraphPinKind.Texture, $"t{t.BindPoint} {t.Dimension}");

        // ------------------------------------------------------------- textures
        var texNodes = new List<(GraphNode Node, int Pin)>();
        var takenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenSamplers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int ti = 0;
        foreach (var slot in b.Slots)
        {
            string path = slot.Path ?? "";
            bool repeat = !seenSamplers.Add(slot.SamplerName);
            string? target = reflected ? info!.TargetFor(slot.SamplerName) : slot.SamplerName + "__TX";
            int pin = -1;
            var state = GraphNodeState.Authored;
            string why = "";

            if (repeat) { state = GraphNodeState.Unused; why = "Repeats an earlier sampler of the same name; the editor (and the D3D11 builders) keep the first."; }
            else if (target is null) { state = GraphNodeState.Unused; why = "The shader permutation this material resolves to does not declare a texture for this sampler, so it is compiled out."; }
            else if (!takenTargets.Add(target)) { state = GraphNodeState.Unused; why = "Another sampler already feeds this shader input."; }
            else pin = AddShaderPin("tx:" + target, target.EndsWith("__TX", StringComparison.OrdinalIgnoreCase) ? target[..^4] : target,
                GraphPinKind.Texture, "");

            var details = new List<GraphDetail>
            {
                new("Texture", "Sampler", slot.SamplerName),
                new("Texture", "Path", path.Length == 0 ? "(none authored)" : path),
            };
            if (slot.IsWadChunkLink) details.Add(new("Texture", "Reference", $"WadChunkLink 0x{slot.ChunkHash:x16}"));
            if (slot.AddressU is int au) details.Add(new("Texture", "Address U", AddressName(au)));
            if (slot.AddressV is int av) details.Add(new("Texture", "Address V", AddressName(av)));
            if (slot.AddressW is int aw) details.Add(new("Texture", "Address W", AddressName(aw)));
            details.Add(new("Shader", "Feeds", target is null ? "(nothing)" : target));
            if (why.Length > 0) details.Add(new("Shader", "Note", why));

            var node = new GraphNode
            {
                Id = "tex:" + ti,
                Kind = GraphNodeKind.Texture,
                Group = GraphGroup.Textures,
                Title = slot.SamplerName,
                Subtitle = path.Length == 0 ? "(no path)" : LeafOf(path),
                State = state,
                Outputs = new[] { new GraphPin { Name = "RGBA", IsInput = false, Kind = GraphPinKind.Texture, Row = 0, Detail = "" } },
                TexturePath = path,
                TextureChunk = slot.ChunkHash,
                Source = slot.SamplerName,
                Details = details,
                Width = TextureWidth,
            };
            texNodes.Add((node, pin));
            ti++;
        }

        // shader-default textures the material leaves unbound (dimmed): they are what the game samples there
        if (reflected)
            foreach (var (sampler, path) in info!.TextureDefaults.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                string? target = info.TargetFor(sampler);
                if (target is null || !takenTargets.Add(target)) continue;
                int pin = AddShaderPin("tx:" + target, target.EndsWith("__TX", StringComparison.OrdinalIgnoreCase) ? target[..^4] : target, GraphPinKind.Texture, "");
                texNodes.Add((new GraphNode
                {
                    Id = "texdef:" + sampler,
                    Kind = GraphNodeKind.Texture,
                    Group = GraphGroup.Textures,
                    Title = sampler,
                    Subtitle = LeafOf(path),
                    State = GraphNodeState.ShaderDefault,
                    Outputs = new[] { new GraphPin { Name = "RGBA", IsInput = false, Kind = GraphPinKind.Texture, Row = 0 } },
                    TexturePath = path,
                    TextureChunk = ReyEngine.Core.Hashing.HashAlgorithms.WadPath(path.ToLowerInvariant()),
                    Source = sampler,
                    Width = TextureWidth,
                    Details = new GraphDetail[]
                    {
                        new("Texture", "Sampler", sampler),
                        new("Texture", "Path", path),
                        new("Shader", "Note", "Not set by the material: this is the shader's own defaultTexturePath (shaders.bin)."),
                        new("Shader", "Feeds", target),
                    },
                }, pin));
            }

        // ------------------------------------------------------------- parameters
        var declared = reflected
            ? info!.Constants.ToDictionary(c => c.Name, c => c, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, ShaderConstantInput>(StringComparer.OrdinalIgnoreCase);
        var dynamicByName = b.DynamicParameters.GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var paramNodes = new List<(GraphNode Node, int Pin)>();
        var seenParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int pi = 0;
        foreach (var p in b.Parameters)
        {
            bool repeat = !seenParams.Add(p.Name);
            ShaderConstantInput? con = declared.TryGetValue(p.Name, out var c0) ? c0 : null;
            var state = GraphNodeState.Authored;
            string why = "";
            int pin = -1;
            var kind = KindOf(p, out string valueText, out Vector4? swatch);
            var pinKind = kind == GraphNodeKind.Color ? GraphPinKind.Color : kind == GraphNodeKind.Vector ? GraphPinKind.Vector : GraphPinKind.Scalar;
            if (repeat) { state = GraphNodeState.Unused; why = "Repeats an earlier parameter of the same name; the editor keeps the first."; }
            else if (reflected && con is null) { state = GraphNodeState.Unused; why = "Not a constant the resolved shader permutation declares."; }
            else
            {
                pin = AddShaderPin("c:" + p.Name, p.Name, pinKind,
                    con is null ? "" : con.IsUsed ? con.TypeName : con.TypeName + " (unread)");
                if (con is { IsUsed: false }) why = "Declared by the shader but not read by this permutation.";
            }

            var details = new List<GraphDetail>
            {
                new("Parameter", "Name", p.Name),
                new("Parameter", "Type", p.TypeName),
                new("Parameter", "Value", valueText),
            };
            if (p.IsValueOmitted) details.Add(new("Parameter", "Note", "The entry names the parameter and writes no value: an authored zero."));
            if (con is not null) details.Add(new("Shader", "Constant", $"{con.Buffer}.{con.Name}  ({con.TypeName})"));
            if (reflected && info!.ParameterDefaults.TryGetValue(p.Name, out var def))
                details.Add(new("Shader", "Shader default", string.Join(" ", def.Select(F))));
            if (dynamicByName.TryGetValue(p.Name, out var dyn))
                details.Add(new("Runtime", "Driven by", (dyn.Enabled ? "" : "(disabled) ") + dyn.Driver));
            if (why.Length > 0) details.Add(new("Shader", "Note", why));

            paramNodes.Add((new GraphNode
            {
                Id = "param:" + pi,
                Kind = kind,
                Group = GraphGroup.Parameters,
                Title = p.Name,
                Subtitle = dynamicByName.ContainsKey(p.Name) ? "driven at runtime" : p.TypeName,
                State = state,
                Outputs = new[]
                {
                    new GraphPin
                    {
                        Name = "Out", IsInput = false, Row = 0, Detail = valueText,
                        Kind = pinKind,
                    },
                },
                Swatch = swatch,
                Source = p.Name,
                Details = details,
                Width = ParamWidth,
            }, pin));
            pi++;
        }

        // ------------------------------------------------------------- switches and macros
        var swNodes = new List<(GraphNode Node, int Pin)>();
        var seenSwitch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int si = 0;
        foreach (var sw in b.CanEditSwitches ? b.AllSwitches : b.SwitchEntries)   // M830: a switch added after the parse is a node too
        {
            bool repeat = !seenSwitch.Add(sw.Name);
            var state = GraphNodeState.Authored;
            string why = "";
            int pin = -1;
            if (repeat) { state = GraphNodeState.Unused; why = "Repeats an earlier switch of the same name; the editor keeps the first."; }
            else if (info is { HasSwitchDefinition: true } && !info.SwitchDefaults.ContainsKey(sw.Name))
            { state = GraphNodeState.Unused; why = "The shader definition (shaders.bin) lists no switch of this name."; }
            else pin = AddShaderPin("sw:" + sw.Name, sw.Name, GraphPinKind.Bool, "switch");

            string shaderDefault = info is not null && info.SwitchDefaults.TryGetValue(sw.Name, out bool dv) ? (dv ? "on" : "off") : "unknown";
            var details = new List<GraphDetail>
            {
                new("Switch", "Name", sw.Name),
                new("Switch", "Value", sw.On ? "ON" : "OFF"),
                new("Switch", "Shader default", shaderDefault),
                new("Switch", "Note", "A switch entry without an explicit 'on' field is enabled (M103); Riot writes 'on' only to turn one off."),
            };
            if (why.Length > 0) details.Add(new("Shader", "Note", why));
            swNodes.Add((SwitchNode("sw:" + si, sw.Name, "switch", sw.On ? "ON" : "OFF", state, details), pin));
            si++;
        }
        if (info is not null)
            foreach (var (name, on) in info.SwitchDefaults.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!seenSwitch.Add(name)) continue;
                int pin = AddShaderPin("sw:" + name, name, GraphPinKind.Bool, "switch");
                swNodes.Add((SwitchNode("swdef:" + name, name, "shader default", on ? "default ON" : "default OFF", GraphNodeState.ShaderDefault,
                    new GraphDetail[]
                    {
                        new("Switch", "Name", name),
                        new("Switch", "Value", on ? "ON (shader default)" : "OFF (shader default)"),
                        new("Switch", "Note", "The material does not author this switch, so the shader's onByDefault applies."),
                    }), pin));
            }

        int mi = 0;
        var seenMacro = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in b.CanEditMacros ? b.AllMacros : b.MacroEntries)
        {
            bool repeat = !seenMacro.Add(m.Name);
            int pin = repeat ? -1 : AddShaderPin("mac:" + m.Name, m.Name, GraphPinKind.Bool, "define");
            var details = new List<GraphDetail>
            {
                new("Macro", "Name", m.Name),
                new("Macro", "Value", m.Value),
                new("Macro", "Note", "A shaderMacros entry: a preprocessor define that selects a cooked shader permutation."),
            };
            if (repeat) details.Add(new("Shader", "Note", "Repeats an earlier macro of the same name; the editor keeps the first."));
            swNodes.Add((SwitchNode("mac:" + mi, m.Name, "macro", "= " + m.Value, repeat ? GraphNodeState.Unused : GraphNodeState.Authored, details, GraphNodeKind.Macro), pin));
            mi++;
        }

        // ------------------------------------------------------------- shader + output
        var surfaceOut = new GraphPin { Name = "Result", IsInput = false, Kind = GraphPinKind.Shader, Row = 0 };
        var shaderDetails = new List<GraphDetail>
        {
            new("Shader", "Render shader", shaderPath.Length == 0 ? "(none authored)" : shaderPath),
            new("Shader", "Material class", b.ShaderName),
        };
        if (info is not null)
        {
            if (info.PixelPermutation.Length > 0) shaderDetails.Add(new("Shader", "Pixel permutation", info.PixelPermutation));
            if (info.VertexPermutation.Length > 0) shaderDetails.Add(new("Shader", "Vertex permutation", info.VertexPermutation));
            foreach (var kv in info.FeatureDefines.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                shaderDetails.Add(new("Feature defines", kv.Key, kv.Value));
            shaderDetails.Add(new("Shader", "Texture inputs", info.Textures.Count.ToString(CultureInfo.InvariantCulture)));
            shaderDetails.Add(new("Shader", "Constants declared", info.Constants.Count.ToString(CultureInfo.InvariantCulture)));
        }
        var shaderNode = new GraphNode
        {
            Id = "shader",
            Kind = GraphNodeKind.Shader,
            Group = GraphGroup.Shader,
            Title = shaderLeaf,
            Subtitle = shaderPath.Length == 0 ? b.ShaderName : shaderPath,
            Inputs = shaderIn,
            Outputs = new[] { surfaceOut },
            Source = shaderPath,
            Details = shaderDetails,
            Width = ShaderWidth,
        };

        // the material output: render state
        var outIn = new List<GraphPin>();
        void OutPin(string name, string detail, bool shaded, GraphPinKind kind = GraphPinKind.Attribute) =>
            outIn.Add(new GraphPin { Name = name, IsInput = true, Kind = kind, Row = outIn.Count, Detail = detail, Shaded = shaded });

        OutPin("Surface", "shader", true, GraphPinKind.Shader);
        OutPin("Blend", b.BlendEnable ? "ON" : "OFF", b.PassAuthors("blendEnable"));
        OutPin("Src color factor", FactorName(MaterialBlendFactors.Source(b.SrcBlendFactor)), b.PassAuthors("srcColorBlendFactor"));
        OutPin("Dst color factor", FactorName(MaterialBlendFactors.Destination(b.DstBlendFactor)), b.PassAuthors("dstColorBlendFactor"));
        OutPin("Depth test", b.DepthEnable ? "ON" : "OFF", b.PassAuthors("depthEnable"));
        OutPin("Write mask", WriteMaskText(b.WriteMask), b.PassAuthors("writeMask"));
        OutPin("Cull", b.CullEnable switch { true => "back faces", false => "none (two-sided)", null => "not authored" }, b.CullEnable.HasValue);
        var alphaParam = b.Parameters.FirstOrDefault(p => p.Name.Equals("AlphaTestValue", StringComparison.OrdinalIgnoreCase));
        OutPin("Alpha test", alphaParam is null ? "not authored" : alphaParam.CurrentText, alphaParam is not null);
        OutPin("Technique / pass", shaderPath.Length > 0 ? "technique 0, pass 0" : "none", shaderPath.Length > 0);

        var outDetails = new List<GraphDetail>
        {
            new("Material", "Name", b.Name),
            new("Material", "Class", b.ShaderName),
            new("Material", "Assigned to", b.AssignedTo.Length == 0 ? "(nothing in this bin)" : b.AssignedTo),
            new("Material", "Composite (derived)", b.Profile.RenderMode.ToString()),
            new("Render state", "Blend enable", (b.BlendEnable ? "true" : "false") + Auth(b, "blendEnable")),
            new("Render state", "Source colour factor", FactorName(MaterialBlendFactors.Source(b.SrcBlendFactor)) + Auth(b, "srcColorBlendFactor")),
            new("Render state", "Destination colour factor", FactorName(MaterialBlendFactors.Destination(b.DstBlendFactor)) + Auth(b, "dstColorBlendFactor")),
            new("Render state", "Depth test", (b.DepthEnable ? "true" : "false") + Auth(b, "depthEnable")),
            new("Render state", "Write mask", WriteMaskText(b.WriteMask) + Auth(b, "writeMask")),
            new("Render state", "Cull", b.CullEnable switch { true => "cullEnable = true (single-sided)", false => "cullEnable = false (two-sided)", null => "not authored" }),
            new("Render state", "Alpha test value", alphaParam is null ? "not authored" : alphaParam.CurrentText),
            new("Render state", "Pass", "First technique, first pass (the only pass this editor reads)"),
        };
        if (b.IsLinked) outDetails.Add(new("Material", "Note", $"Read-only: lives in the linked bin {b.LinkedFromBin}."));

        var outputNode = new GraphNode
        {
            Id = "output",
            Kind = GraphNodeKind.Output,
            Group = GraphGroup.Output,
            Title = LeafOf(b.Name),
            Subtitle = "Material output - render state",
            Inputs = outIn,
            Source = b.Name,
            Details = outDetails,
            Width = OutputWidth,
        };

        // ------------------------------------------------------------- order, wire, layout
        static IEnumerable<(GraphNode Node, int Pin)> Ordered(List<(GraphNode Node, int Pin)> list) =>
            list.Select((x, i) => (x, i)).OrderBy(t => t.x.Pin < 0 ? int.MaxValue : t.x.Pin).ThenBy(t => t.i).Select(t => t.x);

        var texOrdered = Ordered(texNodes).ToList();
        var paramOrdered = Ordered(paramNodes).ToList();
        var swOrdered = Ordered(swNodes).ToList();

        var nodes = new List<GraphNode>();
        var wires = new List<GraphWire>();
        foreach (var group in new[] { texOrdered, paramOrdered, swOrdered })
            foreach (var (node, pin) in group)
            {
                nodes.Add(node);
                if (pin >= 0)
                {
                    wires.Add(new GraphWire { FromNode = node.Id, FromPin = 0, ToNode = shaderNode.Id, ToPin = pin, SourceKind = node.Kind });
                    node.Outputs[0].Linked = true;
                    shaderIn[pin].Linked = true;
                }
            }
        nodes.Add(shaderNode);
        nodes.Add(outputNode);
        wires.Add(new GraphWire { FromNode = shaderNode.Id, FromPin = 0, ToNode = outputNode.Id, ToPin = 0, SourceKind = GraphNodeKind.Shader });
        surfaceOut.Linked = true;
        outIn[0].Linked = true;

        if (shaderPath.Length == 0)
            notes.Add("This material names no renderShader: the engine's default applies and is not shown.");

        var frames = Layout(nodes, texOrdered.Count, paramOrdered.Count, swOrdered.Count);
        var graph = new MaterialGraph
        {
            MaterialName = b.Name,
            ShaderName = shaderPath,
            Nodes = nodes,
            Wires = wires,
            Frames = frames,
            Notes = notes,
        };
        graph.Bounds = BoundsOf(nodes, frames);
        return graph;
    }

    /// <summary>The graph of a shader on its own (no material): the shader node, its inputs, and the textures and
    /// switches its shaders.bin definition defaults. Used when a raw shader is loaded in the preview.</summary>
    public static MaterialGraph BuildForShader(string shaderPath, MaterialGraphShaderInfo info)
    {
        var b = new MaterialBinding("(shader defaults)", "Shader", Array.Empty<string>(), false,
            new List<TextureSlot>(), Array.Empty<MaterialParameter>()) { RenderShader = shaderPath };
        return Build(b, info);
    }

    // ===================================================================================== layout

    private static List<GraphFrame> Layout(List<GraphNode> nodes, int nTex, int nParam, int nSwitch)
    {
        var frames = new List<GraphFrame>();
        double x = 0;
        double top = GraphMetrics.FrameTitleHeight + GraphMetrics.FramePad;

        // A group with many nodes becomes several columns: one tall column of 11 textures would make a graph
        // 2,000 units high and unreadable at Home. Nodes fill column by column, in shader-pin order.
        void Column(GraphGroup group, string title, double width, int perColumn)
        {
            var members = nodes.Where(n => n.Group == group).ToList();
            if (members.Count == 0) return;
            int columns = (members.Count + perColumn - 1) / perColumn;
            const double innerGap = 22;
            double maxBottom = top;
            for (int c = 0; c < columns; c++)
            {
                double y = top;
                foreach (var n in members.Skip(c * perColumn).Take(perColumn))
                {
                    n.X = x + GraphMetrics.FramePad + c * (width + innerGap);
                    n.Y = y;
                    n.Height = HeightOf(n);
                    y += n.Height + GraphMetrics.RowGap;
                }
                maxBottom = Math.Max(maxBottom, y - GraphMetrics.RowGap);
            }
            double frameW = columns * width + (columns - 1) * innerGap + 2 * GraphMetrics.FramePad;
            frames.Add(new GraphFrame
            {
                Title = title,
                Group = group,
                X = x,
                Y = 0,
                Width = frameW,
                Height = maxBottom + GraphMetrics.FramePad,
            });
            x += frameW + GraphMetrics.ColumnGap;
        }

        Column(GraphGroup.Textures, "Textures", TextureWidth, 4);
        Column(GraphGroup.Parameters, "Parameters", ParamWidth, 9);
        Column(GraphGroup.Switches, "Switches and defines", SwitchWidth, 9);

        foreach (var n in nodes.Where(n => n.Group == GraphGroup.Shader))
        { n.X = x; n.Y = top; n.Height = HeightOf(n); x += n.Width + GraphMetrics.ColumnGap; }
        foreach (var n in nodes.Where(n => n.Group == GraphGroup.Output))
        { n.X = x; n.Y = top; n.Height = HeightOf(n); }
        return frames;
    }

    private static double HeightOf(GraphNode n)
    {
        int rows = Math.Max(1, Math.Max(n.Inputs.Count, n.Outputs.Count));
        if (n.Kind == GraphNodeKind.Shader) rows = n.Inputs.Count + 1;
        double h = GraphMetrics.BodyTop(rows);
        return n.Kind switch
        {
            GraphNodeKind.Texture => h + GraphMetrics.ThumbSize + 26,
            GraphNodeKind.Shader or GraphNodeKind.Output => h + 4,
            _ => h - GraphMetrics.PinBlockPad,
        };
    }

    private static (double, double, double, double) BoundsOf(List<GraphNode> nodes, List<GraphFrame> frames)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var n in nodes) { x0 = Math.Min(x0, n.X); y0 = Math.Min(y0, n.Y); x1 = Math.Max(x1, n.Right); y1 = Math.Max(y1, n.Bottom); }
        foreach (var f in frames) { x0 = Math.Min(x0, f.X); y0 = Math.Min(y0, f.Y); x1 = Math.Max(x1, f.X + f.Width); y1 = Math.Max(y1, f.Y + f.Height); }
        if (x0 > x1) return (0, 0, 0, 0);
        return (x0, y0, x1 - x0, y1 - y0);
    }

    // ===================================================================================== helpers

    private static GraphNode SwitchNode(string id, string name, string subtitle, string value, GraphNodeState state,
        IReadOnlyList<GraphDetail> details, GraphNodeKind kind = GraphNodeKind.Switch) =>
        new()
        {
            Id = id,
            Kind = kind,
            Group = GraphGroup.Switches,
            Title = name,
            Subtitle = subtitle,
            State = state,
            Outputs = new[] { new GraphPin { Name = "Out", IsInput = false, Kind = GraphPinKind.Bool, Row = 0, Detail = value } },
            Source = name,
            Details = details,
            Width = SwitchWidth,
        };

    private static string Auth(MaterialBinding b, string field) => b.PassAuthors(field) ? "" : "   (not authored: schema default)";

    private static string LeafOf(string path)
    {
        int cut = path.LastIndexOfAny(new[] { '/', '\\' });
        return cut >= 0 ? path[(cut + 1)..] : path;
    }

    private static string AddressName(int v) => v switch { 0 => "Wrap", 1 => "Clamp", 2 => "Mirror", _ => v.ToString(CultureInfo.InvariantCulture) };

    private static string FactorName(MaterialBlendFactor f) => f switch
    {
        MaterialBlendFactor.Zero => "Zero",
        MaterialBlendFactor.One => "One",
        MaterialBlendFactor.SourceColor => "SrcColor",
        MaterialBlendFactor.OneMinusSourceColor => "1-SrcColor",
        MaterialBlendFactor.DestinationColor => "DstColor",
        MaterialBlendFactor.OneMinusDestinationColor => "1-DstColor",
        MaterialBlendFactor.SourceAlpha => "SrcAlpha",
        MaterialBlendFactor.OneMinusSourceAlpha => "1-SrcAlpha",
        MaterialBlendFactor.DestinationAlpha => "DstAlpha",
        MaterialBlendFactor.OneMinusDestinationAlpha => "1-DstAlpha",
        _ => f.ToString(),
    };

    /// <summary>writeMask bits (M788): 1 R, 2 G, 4 B, 8 A, 16 depth, 32 stencil; absent = 31.</summary>
    public static string WriteMaskText(int mask)
    {
        var parts = new List<string>();
        if ((mask & 1) != 0) parts.Add("R");
        if ((mask & 2) != 0) parts.Add("G");
        if ((mask & 4) != 0) parts.Add("B");
        if ((mask & 8) != 0) parts.Add("A");
        if ((mask & 16) != 0) parts.Add("depth");
        if ((mask & 32) != 0) parts.Add("stencil");
        return $"{mask}: " + (parts.Count == 0 ? "nothing" : string.Join("+", parts));
    }

    private static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Kind, a compact value string and a swatch for one parameter. A colour is a Color, or a
    /// Vector3/Vector4 whose NAME says colour/tint - the bin has no colour type for the Vector4 form, so this is
    /// a naming rule and the Details panel still shows the raw numbers.</summary>
    internal static GraphNodeKind KindOf(MaterialParameter p, out string text, out Vector4? swatch)
    {
        swatch = null;
        text = p.CurrentText;
        bool namedColour = p.Name.Contains("color", StringComparison.OrdinalIgnoreCase)
                           || p.Name.Contains("colour", StringComparison.OrdinalIgnoreCase)
                           || p.Name.Contains("tint", StringComparison.OrdinalIgnoreCase);

        if (p.TryGetColor(out var c) && (p.TypeName == "Color" || namedColour))
        {
            swatch = c;
            text = $"({F(c.X)}, {F(c.Y)}, {F(c.Z)}, {F(c.W)})";
            return GraphNodeKind.Color;
        }
        if (p.TryGetVector4(out var v))
        {
            switch (p.TypeName)
            {
                case "Vector4": text = $"({F(v.X)}, {F(v.Y)}, {F(v.Z)}, {F(v.W)})"; return GraphNodeKind.Vector;
                case "Vector3": text = $"({F(v.X)}, {F(v.Y)}, {F(v.Z)})"; return GraphNodeKind.Vector;
                case "Vector2": text = $"({F(v.X)}, {F(v.Y)})"; return GraphNodeKind.Vector;
                case "F32": text = F(v.X); return GraphNodeKind.Scalar;
            }
        }
        return GraphNodeKind.Scalar;
    }
}
