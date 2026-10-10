using System.Numerics;
using System.Reflection;
using System.Text.RegularExpressions;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Materials.Graph;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M829: the view-only Material Graph - the model the builder makes from a material and its shader, the
/// deterministic layout, and the promise that the old Shader Preview window lost nothing in the redesign.
/// <see cref="MaterialGraphRealDataTests"/> holds the same checks against Riot's own files.
/// </summary>
public sealed class MaterialGraphTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private const string ShaderPath = "Shaders/StaticMesh/Test_Shader";
    private const string MatPath = "Maps/Test/Materials/Rock_MAT";

    private static readonly Dictionary<uint, string> Known = new[]
    {
        "StaticMaterialDef", "name", ShaderPath,
    }.ToDictionary(H);
    private static string? Resolve(uint h) => Known.TryGetValue(h, out var n) ? n : null;

    // ===================================================================== builders

    private static BinTreeEmbedded Sampler(string name, string path) =>
        new(0, H("StaticMaterialShaderSamplerDef"), new BinTreeProperty[]
        {
            new BinTreeString(H("TextureName"), name),
            new BinTreeString(H("texturePath"), path),
        });

    private static BinTreeEmbedded Param(string name, BinTreeProperty? value) =>
        new(0, H("StaticMaterialShaderParamDef"), value is null
            ? new BinTreeProperty[] { new BinTreeString(H("name"), name) }
            : new BinTreeProperty[] { new BinTreeString(H("name"), name), value });

    private static BinTreeProperty V4(float x, float y = 0, float z = 0, float w = 0) => new BinTreeVector4(H("value"), new Vector4(x, y, z, w));

    private static BinTreeEmbedded Switch(string name, bool? on) =>
        new(0, H("StaticMaterialSwitchDef"), on is null
            ? new BinTreeProperty[] { new BinTreeString(H("name"), name) }
            : new BinTreeProperty[] { new BinTreeString(H("name"), name), new BinTreeBool(H("on"), on.Value) });

    private static BinTreeObject Material(IEnumerable<BinTreeEmbedded> samplers, IEnumerable<BinTreeEmbedded> parameters,
        IEnumerable<BinTreeEmbedded> switches, (string Name, string Value)[]? macros = null, bool pass = true)
    {
        var props = new List<BinTreeProperty>
        {
            new BinTreeString(H("name"), MatPath),
            new BinTreeUnorderedContainer(H("samplerValues"), BinPropertyType.Embedded, samplers.ToList()),
            new BinTreeUnorderedContainer(H("paramValues"), BinPropertyType.Embedded, parameters.ToList()),
        };
        var sw = switches.ToList();
        if (sw.Count > 0) props.Add(new BinTreeUnorderedContainer(H("switches"), BinPropertyType.Embedded, sw));
        if (macros is { Length: > 0 })
        {
            var map = new BinTreeMap(H("shaderMacros"), BinPropertyType.String, BinPropertyType.String,
                macros.ToDictionary(m => (BinTreeProperty)new BinTreeString(0, m.Name), m => (BinTreeProperty)new BinTreeString(0, m.Value)));
            props.Add(map);
        }
        if (pass)
        {
            var passDef = new BinTreeEmbedded(0, H("StaticMaterialPassDef"), new BinTreeProperty[]
            {
                new BinTreeObjectLink(H("shader"), H(ShaderPath)),
                new BinTreeBool(H("blendEnable"), true),
                new BinTreeU8(H("srcColorBlendFactor"), 6),
                new BinTreeU8(H("dstColorBlendFactor"), 7),
                new BinTreeBool(H("cullEnable"), false),
            });
            var tech = new BinTreeEmbedded(0, H("StaticMaterialTechniqueDef"), new BinTreeProperty[]
            {
                new BinTreeUnorderedContainer(H("passes"), BinPropertyType.Embedded, new List<BinTreeEmbedded> { passDef }),
            });
            props.Add(new BinTreeUnorderedContainer(H("techniques"), BinPropertyType.Embedded, new List<BinTreeEmbedded> { tech }));
        }
        return new BinTreeObject(H(MatPath), H("StaticMaterialDef"), props);
    }

    private static MaterialBinding Parse(BinTreeObject material)
    {
        using var ms = new MemoryStream();
        new BinTree(new[] { material }, Array.Empty<string>()).Write(ms);
        var doc = MaterialDocument.Parse(ms.ToArray(), Resolve);
        return doc.Materials.Single(m => m.Name == MatPath);
    }

    /// <summary>The material most tests use: a diffuse, a mask no shader reads, a colour, a scalar, a parameter the
    /// shader does not declare, an authored-ON and an authored-OFF switch, and one macro.</summary>
    private static MaterialBinding Rock() => Parse(Material(
        new[] { Sampler("Diffuse_Texture", "assets/maps/rock_d.tex"), Sampler("Mask_Texture", "assets/maps/rock_m.tex") },
        new[]
        {
            Param("TintColor", V4(1f, 0.5f, 0.25f, 1f)),
            Param("Scale", V4(2f)),
            Param("NotDeclared", V4(3f)),
        },
        new[] { Switch("USE_A", null), Switch("USE_B", false) },
        new[] { ("NO_BAKED_LIGHTING", "1") }));

    private static MaterialGraphShaderInfo RockShader() => new()
    {
        ShaderName = ShaderPath,
        Textures = new[]
        {
            new ShaderTextureInput("Diffuse_Texture__TX", 0, "tex2d", "PS"),
            new ShaderTextureInput("Normal__TX", 1, "tex2d", "PS"),
        },
        Constants = new[]
        {
            new ShaderConstantInput("TintColor", "PerMaterial", "float4", true, "PS"),
            new ShaderConstantInput("Scale", "PerMaterial", "float4", true, "PS"),
        },
        SwitchDefaults = new Dictionary<string, bool> { ["USE_A"] = true, ["USE_B"] = true, ["USE_C"] = false },
        HasSwitchDefinition = true,
    };

    private static string PinName(MaterialGraph g, GraphWire w) => g.Find(w.ToNode)!.Inputs[w.ToPin].Name;

    // ===================================================================== the model

    [Fact]
    public void ASyntheticMaterialBecomesTexturesParametersSwitchesAShaderAndAnOutput()
    {
        var g = MaterialGraphBuilder.Build(Rock(), RockShader());

        // one node per authored thing, plus the shader's own default switch, the shader and the output
        Assert.Equal(2, g.Nodes.Count(n => n.Kind == GraphNodeKind.Texture));
        Assert.Equal(3, g.Nodes.Count(n => n.Kind is GraphNodeKind.Scalar or GraphNodeKind.Vector or GraphNodeKind.Color));
        Assert.Equal(3, g.Nodes.Count(n => n.Kind == GraphNodeKind.Switch));      // USE_A, USE_B, and USE_C from the shader
        Assert.Single(g.Nodes, n => n.Kind == GraphNodeKind.Macro);
        Assert.Single(g.Nodes, n => n.Kind == GraphNodeKind.Shader);
        Assert.Single(g.Nodes, n => n.Kind == GraphNodeKind.Output);
        Assert.Equal("Shaders/StaticMesh/Test_Shader", g.ShaderName);
        Assert.Equal("Test_Shader", g.ShaderNode!.Title);

        // texture: wired to the shader pin it feeds; a sampler no shader input matches is shown, but unused and unwired
        var diffuse = g.Nodes.Single(n => n.Source == "Diffuse_Texture");
        Assert.Equal(GraphNodeState.Authored, diffuse.State);
        Assert.Equal("assets/maps/rock_d.tex", diffuse.TexturePath);
        var wire = g.Wires.Single(w => w.FromNode == diffuse.Id);
        Assert.Equal("shader", wire.ToNode);
        Assert.Equal("Diffuse_Texture", PinName(g, wire));
        var mask = g.Nodes.Single(n => n.Source == "Mask_Texture");
        Assert.Equal(GraphNodeState.Unused, mask.State);
        Assert.DoesNotContain(g.Wires, w => w.FromNode == mask.Id);

        // the shader lists the input the material leaves unbound, with no wire on it
        var normal = g.ShaderNode!.Inputs.Single(p => p.Name == "Normal");
        Assert.False(normal.Linked);
        Assert.True(g.ShaderNode.Inputs.Single(p => p.Name == "Diffuse_Texture").Linked);
    }

    [Fact]
    public void ParametersKeepTheirKindValueAndSwatch_AndOnesTheShaderDoesNotDeclareAreUnused()
    {
        var g = MaterialGraphBuilder.Build(Rock(), RockShader());

        var tint = g.Nodes.Single(n => n.Source == "TintColor");
        Assert.Equal(GraphNodeKind.Color, tint.Kind);
        Assert.Equal(new Vector4(1f, 0.5f, 0.25f, 1f), tint.Swatch);
        Assert.Equal("(1, 0.5, 0.25, 1)", tint.Outputs[0].Detail);
        Assert.Contains(g.Wires, w => w.FromNode == tint.Id && PinName(g, w) == "TintColor");

        var scale = g.Nodes.Single(n => n.Source == "Scale");
        Assert.Equal(GraphNodeKind.Vector, scale.Kind);
        Assert.Null(scale.Swatch);
        Assert.Contains(g.Wires, w => w.FromNode == scale.Id && PinName(g, w) == "Scale");

        var stray = g.Nodes.Single(n => n.Source == "NotDeclared");
        Assert.Equal(GraphNodeState.Unused, stray.State);
        Assert.DoesNotContain(g.Wires, w => w.FromNode == stray.Id);
        Assert.Contains(stray.Details, d => d.Value.Contains("does not declare") || d.Value.Contains("not a constant", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SwitchesShowOnOrOff_AnAbsentOnFieldIsEnabled_AndTheShaderDefaultFillsTheRest()
    {
        var g = MaterialGraphBuilder.Build(Rock(), RockShader());

        var a = g.Nodes.Single(n => n.Kind == GraphNodeKind.Switch && n.Source == "USE_A");
        Assert.Equal("ON", a.Outputs[0].Detail);                  // M103: an entry with no 'on' is enabled
        Assert.Equal(GraphNodeState.Authored, a.State);
        var b = g.Nodes.Single(n => n.Kind == GraphNodeKind.Switch && n.Source == "USE_B");
        Assert.Equal("OFF", b.Outputs[0].Detail);
        var c = g.Nodes.Single(n => n.Kind == GraphNodeKind.Switch && n.Source == "USE_C");
        Assert.Equal(GraphNodeState.ShaderDefault, c.State);      // the material never mentions it: the shader's default applies
        Assert.Equal("default OFF", c.Outputs[0].Detail);
        foreach (var s in new[] { a, b, c })
            Assert.Contains(g.Wires, w => w.FromNode == s.Id && PinName(g, w) == s.Source);

        var macro = g.Nodes.Single(n => n.Kind == GraphNodeKind.Macro);
        Assert.Equal("NO_BAKED_LIGHTING", macro.Title);
        Assert.Equal("= 1", macro.Outputs[0].Detail);
        Assert.Contains(g.Wires, w => w.FromNode == macro.Id && PinName(g, w) == "NO_BAKED_LIGHTING");
    }

    [Fact]
    public void TheOutputNodeShadesWhatTheMaterialSets_AndLeavesTheRestOnTheDefault()
    {
        var g = MaterialGraphBuilder.Build(Rock(), RockShader());
        var o = g.OutputNode!;
        GraphPin Pin(string name) => o.Inputs.Single(p => p.Name == name);

        Assert.True(Pin("Surface").Linked);
        Assert.Equal("ON", Pin("Blend").Detail);
        Assert.True(Pin("Blend").Shaded);
        Assert.Equal("SrcAlpha", Pin("Src color factor").Detail);
        Assert.True(Pin("Src color factor").Shaded);
        Assert.Equal("1-SrcAlpha", Pin("Dst color factor").Detail);
        Assert.Equal("none (two-sided)", Pin("Cull").Detail);
        Assert.True(Pin("Cull").Shaded);
        // depth is not authored: the schema default shows, and is not shaded
        Assert.Equal("ON", Pin("Depth test").Detail);
        Assert.False(Pin("Depth test").Shaded);
        Assert.StartsWith("31: R+G+B+A+depth", Pin("Write mask").Detail);
        Assert.False(Pin("Write mask").Shaded);
        Assert.Equal("not authored", Pin("Alpha test").Detail);
        Assert.Contains(o.Details, d => d.Label == "Cull" && d.Value.Contains("two-sided"));
    }

    [Fact]
    public void WithoutAShaderTheGraphStillShowsEverythingTheMaterialAuthors_AndSaysWhy()
    {
        var g = MaterialGraphBuilder.Build(Rock(), null);

        Assert.Contains(g.Notes, n => n.Contains("No shader reflection"));
        // nothing is "unused" when there is nothing to compare it with: every authored thing is wired to a pin named after it
        Assert.DoesNotContain(g.Nodes, n => n.State == GraphNodeState.Unused);
        var pins = g.ShaderNode!.Inputs.Select(p => p.Name).ToList();
        foreach (string expect in new[] { "Diffuse_Texture", "Mask_Texture", "TintColor", "Scale", "NotDeclared", "USE_A", "USE_B", "NO_BAKED_LIGHTING" })
            Assert.Contains(expect, pins);
        Assert.All(g.ShaderNode.Inputs, p => Assert.True(p.Linked));
    }

    [Fact]
    public void AShaderOnItsOwnGetsAGraphWithItsDefaults()
    {
        var info = RockShader();
        var g = MaterialGraphBuilder.BuildForShader(ShaderPath, info);
        Assert.Equal("Test_Shader", g.ShaderNode!.Title);
        Assert.Equal(3, g.Nodes.Count(n => n.Kind == GraphNodeKind.Switch && n.State == GraphNodeState.ShaderDefault));
        Assert.DoesNotContain(g.Nodes, n => n.State == GraphNodeState.Authored && n.Kind is GraphNodeKind.Texture or GraphNodeKind.Vector);
        Assert.NotNull(g.OutputNode);
    }

    [Fact]
    public void SelectionNeighboursAndSearchAreExact()
    {
        var g = MaterialGraphBuilder.Build(Rock(), RockShader());
        var diffuse = g.Nodes.Single(n => n.Source == "Diffuse_Texture");

        // a texture is joined to the shader only; the shader to every wired node and the output
        var related = g.RelatedTo(diffuse.Id);
        Assert.Equal(new[] { diffuse.Id, "shader" }.OrderBy(x => x), related.OrderBy(x => x));
        Assert.Contains("output", g.RelatedTo("shader"));
        Assert.DoesNotContain(g.Nodes.Single(n => n.Source == "Mask_Texture").Id, g.RelatedTo("shader"));

        Assert.Equal(new[] { "Diffuse_Texture", "Mask_Texture" }, g.Search("_texture").Select(n => n.Source).OrderBy(x => x).ToArray());
        Assert.Single(g.Search("rock_d.tex"));                        // by texture path
        Assert.Empty(g.Search("no such node"));
        Assert.Empty(g.Search("  "));
    }

    // ===================================================================== layout

    [Fact]
    public void TheLayoutIsDeterministic_ColumnsRunLeftToRight_AndNothingOverlaps()
    {
        var a = MaterialGraphBuilder.Build(Rock(), RockShader());
        var b = MaterialGraphBuilder.Build(Rock(), RockShader());

        Assert.Equal(a.Nodes.Select(n => (n.Id, n.X, n.Y, n.Width, n.Height)), b.Nodes.Select(n => (n.Id, n.X, n.Y, n.Width, n.Height)));
        Assert.Equal(a.Wires.Select(w => (w.FromNode, w.FromPin, w.ToNode, w.ToPin)), b.Wires.Select(w => (w.FromNode, w.FromPin, w.ToNode, w.ToPin)));
        Assert.Equal(a.Frames.Select(f => (f.Title, f.X, f.Y, f.Width, f.Height)), b.Frames.Select(f => (f.Title, f.X, f.Y, f.Width, f.Height)));
        Assert.Equal(a.Bounds, b.Bounds);

        double Left(GraphGroup grp) => a.Nodes.Where(n => n.Group == grp).Min(n => n.X);
        Assert.True(Left(GraphGroup.Textures) < Left(GraphGroup.Parameters));
        Assert.True(Left(GraphGroup.Parameters) < Left(GraphGroup.Switches));
        Assert.True(Left(GraphGroup.Switches) < Left(GraphGroup.Shader));
        Assert.True(Left(GraphGroup.Shader) < Left(GraphGroup.Output));
        AssertNoOverlap(a);

        // every node sits inside its comment frame
        foreach (var n in a.Nodes.Where(n => n.Group is GraphGroup.Textures or GraphGroup.Parameters or GraphGroup.Switches))
        {
            var f = a.Frames.Single(fr => fr.Group == n.Group);
            Assert.True(n.X >= f.X && n.Right <= f.X + f.Width && n.Y >= f.Y && n.Bottom <= f.Y + f.Height, n.Id + " is outside its frame");
        }
        // the bounds contain everything
        var (bx, by, bw, bh) = a.Bounds;
        Assert.All(a.Nodes, n => Assert.True(n.X >= bx && n.Right <= bx + bw && n.Y >= by && n.Bottom <= by + bh));
    }

    [Fact]
    public void AManyParameterMaterialWrapsIntoColumnsInsteadOfOneTallStrip()
    {
        var samplers = Enumerable.Range(0, 11).Select(i => Sampler("Tex" + i, $"assets/t{i}.tex")).ToList();
        var parameters = Enumerable.Range(0, 30).Select(i => Param("P" + i, V4(i))).ToList();
        var info = new MaterialGraphShaderInfo
        {
            Textures = samplers.Select((_, i) => new ShaderTextureInput($"Tex{i}__TX", (uint)i, "tex2d", "PS")).ToList(),
            Constants = parameters.Select((_, i) => new ShaderConstantInput("P" + i, "cb", "float4", true, "PS")).ToList(),
        };
        var g = MaterialGraphBuilder.Build(Parse(Material(samplers, parameters, Array.Empty<BinTreeEmbedded>())), info);

        Assert.Equal(11 + 30 + 2, g.Nodes.Count);
        AssertNoOverlap(g);
        Assert.Equal(3, g.Nodes.Where(n => n.Group == GraphGroup.Textures).Select(n => n.X).Distinct().Count());       // 4 + 4 + 3
        Assert.Equal(4, g.Nodes.Where(n => n.Group == GraphGroup.Parameters).Select(n => n.X).Distinct().Count());     // 9 x 3 + 3
        Assert.True(g.Bounds.Height < 1700, "the graph must not be one tall strip: " + g.Bounds.Height);

        // within a column, nodes run in shader-pin order, so wires of one column never cross each other
        foreach (var col in g.Nodes.Where(n => n.Group == GraphGroup.Parameters).GroupBy(n => n.X))
        {
            var pins = col.OrderBy(n => n.Y).Select(n => g.Wires.Single(w => w.FromNode == n.Id).ToPin).ToList();
            Assert.Equal(pins.OrderBy(p => p), pins);
        }
        // every wire lands exactly on a pin dot of the shader node
        var shader = g.ShaderNode!;
        foreach (var w in g.Wires.Where(w => w.ToNode == "shader"))
        {
            var (px, _) = shader.PinPosition(shader.Inputs[w.ToPin]);
            Assert.Equal(shader.X, px);
        }
    }

    private static void AssertNoOverlap(MaterialGraph g)
    {
        for (int i = 0; i < g.Nodes.Count; i++)
            for (int j = i + 1; j < g.Nodes.Count; j++)
            {
                var a = g.Nodes[i]; var b = g.Nodes[j];
                bool apart = a.Right <= b.X || b.Right <= a.X || a.Bottom <= b.Y || b.Bottom <= a.Y;
                Assert.True(apart, $"{a.Id} overlaps {b.Id}");
            }
    }

    // ===================================================================== the stats are the shader's own

    [Fact]
    public void StatsReadNAForWhatTheBlobDoesNotCarry_NeverAGuess()
    {
        var rows = MaterialGraphStats.Shader(null, null);
        Assert.All(rows.Where(r => r.Label != "Texture samplers"), r => { Assert.Equal("n/a", r.Pixel); Assert.Equal("n/a", r.Vertex); });
        Assert.Equal("n/a", rows.Single(r => r.Label == "Texture samplers").Pixel);

        var g = MaterialGraphBuilder.Build(Rock(), RockShader());
        var m = MaterialGraphStats.Material(g).ToDictionary(x => x.Label, x => x.Value);
        Assert.Equal("2  (1 reach the shader)", m["Texture samplers authored"]);
        Assert.Equal("3  (2 reach the shader)", m["Parameters authored"]);
        Assert.Equal($"{g.Nodes.Count} nodes, {g.Wires.Count} wires", m["Graph"]);
    }

    // ===================================================================== the window

    internal static string? Source(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        return null;
    }

    /// <summary>The redesign moved every old tab, never removed one. Each binding the old window's XAML carried is
    /// still bound by the new one, and still names a real member of the view model - an Avalonia binding that names
    /// nothing builds and tests fine, and only fails on screen.</summary>
    [Fact]
    public void EveryCapabilityOfTheOldShaderPreviewWindowIsStillBound()
    {
        var xaml = Source("src", "ReyEngine.App", "Views", "ShaderPreviewWindow.axaml");
        if (xaml is null) return;

        // old tab -> where it lives now
        string[] tabs = { "Details", "Material", "Shader", "Scene", "Debug", "Particles", "Parameters", "Textures", "Stats", "Shader Code", "Shader Info", "Material Report", "Scene Report", "Bindings", "Compare", "Log" };
        foreach (string t in tabs) Assert.Contains($"<TabItem Header=\"{t}\"", xaml);

        string[] bindings =
        {
            // Shader picker + permutations + load
            "Filter", "ShaderNames", "SelectedShader", "VertexPermutations", "SelectedVertexPerm", "PixelPermutations", "SelectedPixelPerm",
            "MeshNames", "SelectedMesh", "LoadCommand", "ReloadCommand",
            // render options + sun
            "Wireframe", "CullBackFaces", "DepthTest", "AlphaBlend", "AnimateTime", "TransposeMatrices", "UseMapSun", "LowQuality", "MirrorX",
            "UseComparisonShader", "SunAzimuth", "SunElevation", "Preview", "IsLoaded",
            // Scene + Debug
            "SceneFilter", "SceneAssets", "SelectedSceneAsset", "LoadSceneCommand", "SceneSubmeshes", "SceneReport", "SceneDefines", "ResetSceneDefinesCommand",
            // Material
            "BinFilter", "MaterialBins", "SelectedBin", "Materials", "SelectedMaterial", "ApplyMaterialCommand", "MaterialReport",
            // Particles
            "ParticleSystems", "SelectedParticleSystem", "PlayParticlesCommand", "ParticlesPlaying", "RestartParticlesCommand", "StopParticlesCommand",
            "ParticleSpeed", "ParticleReport",
            // Textures, Constants (Parameters), Compare, Shader info, Bindings, Log, status
            "TextureSlots", "LoadShaderDefaultsCommand", "BindCheckerToAllCommand", "Constants", "ComparisonSource", "Metadata", "Bindings", "Log",
            "Status", "Perf",
        };
        var vm = typeof(ShaderPreviewViewModel);
        foreach (string b in bindings)
        {
            Assert.True(Regex.IsMatch(xaml, @"\{Binding !?" + Regex.Escape(b) + @"[,}\s]"), "the window no longer binds " + b);
            Assert.True(vm.GetProperty(b, BindingFlags.Public | BindingFlags.Instance) is not null, "the view model has no " + b);
        }
        // the surfaces the code-behind drives
        Assert.Contains("x:Name=\"PreviewSurface\"", xaml);
        Assert.Contains("x:Name=\"TextureList\"", xaml);

        // and every path the XAML binds on the graph model exists on it
        var graph = typeof(MaterialGraphViewModel);
        foreach (Match m in Regex.Matches(xaml, @"\{Binding MaterialGraph\.([A-Za-z]+)"))
            Assert.True(graph.GetProperty(m.Groups[1].Value, BindingFlags.Public | BindingFlags.Instance) is not null,
                "MaterialGraphViewModel has no " + m.Groups[1].Value);
    }

    /// <summary>M830: the graph edits - but only through the Material Editor. The toolbar's Apply / Save are the editor's own
    /// commands (disabled, with the reason as their tooltip, when there is no editor), the canvas still writes nothing, and the
    /// edit rows apply values by the editor's row view models rather than by touching the document.</summary>
    [Fact]
    public void TheGraphEditsOnlyThroughTheMaterialEditor_AndTheDisabledReasonIsTheTooltip()
    {
        var xaml = Source("src", "ReyEngine.App", "Views", "ShaderPreviewWindow.axaml");
        var canvas = Source("src", "ReyEngine.App", "Views", "MaterialGraphCanvas.cs");
        var rows = Source("src", "ReyEngine.App", "ViewModels", "MaterialGraphEditRows.cs");
        var graphVm = Source("src", "ReyEngine.App", "ViewModels", "MaterialGraphViewModel.Edit.cs");
        if (xaml is null || canvas is null || rows is null || graphVm is null) return;

        foreach (var (label, command, tip) in new[] { ("Apply", "ApplyEditsCommand", "ApplyTip"), ("Save", "SaveCommand", "SaveTip"), ("Undo", "UndoCommand", ""), ("Redo", "RedoCommand", "") })
        {
            var m = Regex.Match(xaml, "<Button[^>]*Content=\"" + label + "\"[^>]*>");
            Assert.True(m.Success, label + " button missing");
            Assert.Contains("Command=\"{Binding MaterialGraph." + command + "}\"", m.Value);
            Assert.DoesNotContain("IsEnabled=\"False\"", m.Value);
            if (tip.Length > 0) Assert.Contains("{Binding MaterialGraph." + tip + "}", m.Value);   // the reason a disabled button gives
        }
        Assert.DoesNotContain("view-only in this version", xaml);
        foreach (string key in new[] { "Ctrl+Z", "Ctrl+Y", "Ctrl+S" })
            Assert.Contains("Gesture=\"" + key + "\"", xaml);

        // the canvas selects and pans; it never writes anything, and it says whether the graph is editable
        Assert.DoesNotContain("SetOn(", canvas);
        Assert.DoesNotContain(".Apply(", canvas);
        Assert.DoesNotContain("SetPath(", canvas);
        Assert.DoesNotContain("VIEW ONLY", canvas);
        Assert.Contains("ModeText=\"{Binding MaterialGraph.ModeBadge}\"", xaml);

        // one store, one save: the rows and the view model go through the editor's view models and commands
        foreach (string source in new[] { rows, graphVm })
        {
            Assert.DoesNotContain("StoreOverrideBytes", source);
            Assert.DoesNotContain("Serialize(", source);
            Assert.DoesNotContain(".Model.Apply(", source);
            Assert.DoesNotContain(".SetPath(", source);
            Assert.DoesNotContain(".SetOn(", source);
        }
        Assert.Contains("_editor.SaveCommand.ExecuteAsync", graphVm);
        Assert.Contains("Parameter.ApplyCommand.Execute", rows);
        Assert.Contains("Slot.ApplyCommand.Execute", rows);
    }

    // ===================================================================== the view model and the window

    [Fact]
    public void AMissingBinOrMaterialShowsItsReasonInTheCanvasInsteadOfAStaleGraph()
    {
        using var vm = new MaterialGraphViewModel(null);
        vm.ShowShader("shaders/x", null, null, null, "", "");
        Assert.NotNull(vm.Graph);
        vm.SourceNote = "unsaved";

        vm.ShowMessage("'data/x.bin' is not among the mounted .bin assets.");
        Assert.Null(vm.Graph);
        Assert.Equal("'data/x.bin' is not among the mounted .bin assets.", vm.EmptyText);
        Assert.Equal("", vm.SourceNote);
        Assert.Empty(vm.DetailGroups);
        Assert.Empty(vm.ShaderStats);

        // and the next real graph puts the ordinary hint back
        vm.ShowShader("shaders/x", null, null, null, "", "");
        Assert.NotNull(vm.Graph);
        Assert.StartsWith("Pick a material", vm.EmptyText);
    }

    [Fact]
    public void TheShaderCodeIsOnlyProducedWhenItsTabIsShown()
    {
        using var vm = new MaterialGraphViewModel(null);
        vm.ShowShader("shaders/x", null, null, null, "", "");
        // no stages resolved: nothing to disassemble, and it says so rather than waiting for a tab
        Assert.Contains("No shader code", vm.CodeNote);
        Assert.Equal("", vm.PixelCode);
    }

    [Fact]
    public void TheWindowReusesOneInstance_KeepsFlyKeysAwayFromTheGraph_AndFlagsUnsavedEdits()
    {
        var window = Source("src", "ReyEngine.App", "Views", "ShaderPreviewWindow.axaml.cs");
        var main = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        var materials = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.CharacterMaterials.cs");
        var pick = Source("src", "ReyEngine.App", "ViewModels", "ShaderPreviewViewModel.cs");
        var editor = Source("src", "ReyEngine.App", "Views", "MaterialEditorView.axaml");
        if (window is null || main is null || materials is null || pick is null || editor is null) return;

        Assert.Contains("is TextBox or MaterialGraphCanvas", window);          // fly keys stay out of the 3D camera
        Assert.Contains("_graphWindow is { IsVisible: true } open", main);      // a second click re-targets the open window
        Assert.Contains("open.Activate()", main);
        Assert.Contains("editor.IsDirty", materials);                           // the unsaved-edits flag travels with the request
        Assert.Contains("unsaved edits are not shown", pick);                   // only said when no editor is behind the graph
        Assert.Contains("edits THIS editor's material", editor);                // M830: with one, the graph IS the editor's document
        Assert.Contains("Task.Delay(150, token)", pick);                        // the preview apply is debounced
        Assert.Contains("SceneSubmeshes.Count > 0", pick);                      // and never replaces a loaded scene
    }

    // ===================================================================== the theme

    [Fact]
    public void TheCanvasAndTheViewsCarryNoColoursOfTheirOwn_EveryGraphKeyIsInEveryPalette()
    {
        var canvas = Source("src", "ReyEngine.App", "Views", "MaterialGraphCanvas.cs");
        if (canvas is null) return;
        // the canvas paints with theme brushes only: no Color.Parse / hex literal / named colour constant for a graph element
        Assert.DoesNotMatch("Color\\.Parse", canvas);
        Assert.DoesNotMatch("#[0-9A-Fa-f]{6}", canvas);

        var wanted = Regex.Matches(canvas, "\"(Rey[A-Za-z0-9]+Brush)\"").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Contains("ReyGraphTextureBrush", wanted);
        Assert.Contains("ReyGraphOutputBrush", wanted);

        string dir = Path.GetDirectoryName(FindUp(Path.Combine("src", "ReyEngine.App", "Themes", "Palettes", "Crimson.axaml"))!)!;
        foreach (string palette in Directory.GetFiles(dir, "*.axaml"))
        {
            string text = File.ReadAllText(palette);
            foreach (string key in wanted)
                Assert.True(text.Contains("x:Key=\"" + key + "\""), Path.GetFileName(palette) + " lacks " + key);
        }

        var window = Source("src", "ReyEngine.App", "Views", "ShaderPreviewWindow.axaml")!;
        Assert.DoesNotMatch("=\"#(?!01000000)[0-9A-Fa-f]{6,8}\"", window);
    }

    private static string? FindUp(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, relative);
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
