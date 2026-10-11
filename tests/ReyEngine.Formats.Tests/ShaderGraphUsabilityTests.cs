using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Materials.ShaderGraph;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M835: Shader Graph usability without a game install (the base is the hand-built test base): custom textures in the document, the analyser, the HLSL and the
/// rename; the templates; the auto-connect rules for a wire dropped in empty space; Promote to Parameter / Add Constant; the export gate for textures.
/// The real shaders.bin shape check and the D3D11 renders are in <see cref="ShaderGraphUsabilityRealDataTests"/>.
/// </summary>
public sealed class ShaderGraphUsabilityTests : IDisposable
{
    private const string Flat = "Shaders/StaticMesh/DefaultEnv_Flat";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "reyengine-m835-" + Guid.NewGuid().ToString("N"));

    public ShaderGraphUsabilityTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private ShaderGraphViewModel Vm() => new(() => _root)
    {
        BaseResolver = shader => SgBase.IsSupported(shader) ? (ShaderGraphTests.TestBase(), "") : (null, "not a supported base"),
        CompileDelay = TimeSpan.Zero,
    };

    private static SgPaletteEntry Entry(ShaderGraphViewModel vm, string title) => vm.Palette.First(p => p.Title == title);

    // ================================================================================= custom textures in the document

    [Fact]
    public void Custom_textures_round_trip_as_version_3_and_an_older_file_loads_with_none()
    {
        var d = ShaderGraphDocument.CreateEmpty("t", Flat);
        d.Textures.Add(new SgCustomTexture { Name = "Stripes", DefaultPath = "assets/x/stripes.tex", AddressU = "clamp", AddressV = "mirror" });
        string json = ShaderGraphJson.Serialize(d);
        Assert.Contains("\"version\": 3", json);
        var back = ShaderGraphJson.TryDeserialize(json, out string? err);
        Assert.True(back is not null, err);
        Assert.Equal(json, ShaderGraphJson.Serialize(back!));
        var t = Assert.Single(back!.Textures);
        Assert.Equal(("Stripes", "assets/x/stripes.tex", "clamp", "mirror"), (t.Name, t.DefaultPath, t.AddressU, t.AddressV));
        Assert.Equal("Stripes__TX", t.Resource);
        Assert.Equal("Stripes__SMP", t.Sampler);

        var v2 = ShaderGraphJson.TryDeserialize("{ \"version\": 2, \"name\": \"old\", \"baseShader\": \"" + Flat + "\", \"nodes\": [], \"links\": [] }", out err);
        Assert.True(v2 is not null, err);
        Assert.Empty(v2!.Textures);
    }

    [Theory]
    [InlineData("[{\"name\":\"Bad Name\"}]", "not a valid name")]
    [InlineData("[{\"name\":\"A\"},{\"name\":\"a\"}]", "used twice")]
    [InlineData("[{\"name\":\"A\",\"addressU\":\"sideways\"}]", "address mode")]
    [InlineData("[{\"name\":\"A\",\"defaultPath\":\"x\\u0001y\"}]", "not a path")]
    public void A_hostile_texture_entry_is_refused_with_a_reason(string textures, string reason)
    {
        var doc = ShaderGraphJson.TryDeserialize("{ \"version\": 3, \"nodes\": [], \"links\": [], \"textures\": " + textures + " }", out string? error);
        Assert.Null(doc);
        Assert.Contains(reason, error);
    }

    [Fact]
    public void Texture_names_follow_the_parameter_rules_and_share_their_namespace()
    {
        var b = ShaderGraphTests.TestBase();
        var d = ShaderGraphDocument.CreateEmpty("t", Flat);
        d.Params.Add(new SgCustomParam { Name = "Amount", Components = 1 });
        d.Textures.Add(new SgCustomTexture { Name = "Stripes" });
        Assert.Null(SgNames.TextureProblem("Other", d, b));
        Assert.NotNull(SgNames.TextureProblem("DiffuseTexture", d, b));   // a base texture
        Assert.NotNull(SgNames.TextureProblem("TintColor", d, b));        // a base parameter
        Assert.NotNull(SgNames.TextureProblem("amount", d, b));           // a custom parameter, any case
        Assert.NotNull(SgNames.TextureProblem("STRIPES", d, b));          // another texture
        Assert.NotNull(SgNames.TextureProblem("a__b", d, b));             // reserved double underscore
        Assert.NotNull(SgNames.TextureProblem("float", d, b));
        Assert.Null(SgNames.TextureProblem("Stripes", d, b, renaming: "Stripes"));
        Assert.NotNull(SgNames.ParameterProblem("stripes", d, b));        // and a parameter cannot take a texture's name
        Assert.Equal("Stripes2", SgNames.UniqueTextureName("Stripes", d, b));
    }

    [Fact]
    public async Task A_Texture_Sample_can_read_a_custom_texture_and_the_HLSL_declares_it_like_Riots()
    {
        var b = ShaderGraphTests.TestBase();
        var d = ShaderGraphDocument.CreateEmpty("t", Flat);
        d.Textures.Add(new SgCustomTexture { Name = "Stripes" });
        ShaderGraphTests.Add(d, "n2", "TextureSample", ("texture", "Stripes"));
        ShaderGraphTests.Link(d, "n2", "RGB", "n1", SgCatalog.BaseColorPin);
        var hlsl = SgHlsl.Generate(d, b, b.Signatures[0]);
        Assert.True(hlsl.Ok, string.Join("\n", hlsl.Errors));
        Assert.Contains("Texture2D Stripes__TX;", hlsl.Source);
        Assert.Contains("SamplerState Stripes__SMP;", hlsl.Source);
        Assert.Contains("Stripes__TX.Sample(Stripes__SMP,", hlsl.Source);
        Assert.Equal("Stripes", Assert.Single(hlsl.CustomTextures).Name);

        // the real compiler and Verify accept it (the texture is not one the base declares, but the graph does)
        var build = ShaderGraphCompiler.BuildAll(d, b);
        Assert.True(build.Ok, string.Join("\n", build.Diagnostics));

        // an undeclared texture is still an error
        d.Textures.Clear();
        var bad = SgAnalyzer.Analyze(d, b);
        Assert.Contains(bad.Errors, e => e.NodeId == "n2" && e.Message.Contains("neither a custom texture"));
        await Task.CompletedTask;
    }

    // ================================================================================= the editor

    [Fact]
    public void A_texture_parameter_from_the_palette_creates_the_texture_and_the_node_in_one_undo_step_and_rename_follows()
    {
        var vm = Vm();
        Assert.True(vm.NewFromTemplate(Flat, SgTemplates.Empty, out string e1), e1);
        string start = ShaderGraphJson.Serialize(vm.Document!);
        string? id = vm.AddNode(Entry(vm, "Texture Parameter (new, custom)"), 100, 100);
        Assert.NotNull(id);
        var tex = Assert.Single(vm.Document!.Textures);
        Assert.Equal("CustomTexture", tex.Name);
        Assert.Equal("CustomTexture", vm.Document.Find(id!)!.Prop("texture"));
        Assert.Equal("CustomTexture", vm.Graph!.Find(id!)!.Title);                         // the node shows the texture's name
        Assert.Contains("Texture Parameter (custom)", vm.Graph.Find(id!)!.Subtitle);
        Assert.Single(vm.TextureRows);

        Assert.True(vm.SetTextureDefault("CustomTexture", "assets/x/checker.tex"));
        Assert.True(vm.SetTextureAddress("CustomTexture", "clamp", null));
        Assert.True(vm.RenameTexture("CustomTexture", "Checker"));
        Assert.Equal("Checker", vm.Document.Find(id!)!.Prop("texture"));
        Assert.Equal("clamp", vm.Document.FindTexture("Checker")!.AddressU);
        Assert.False(vm.RenameTexture("Checker", "TintColor"));                            // a base parameter's name
        Assert.True(vm.Palette.Any(p => p.Title == "Texture Sample: Checker (custom)"));

        Assert.False(vm.DeleteTexture("Checker"));                                        // still read by a node
        Assert.True(vm.DeleteNode(id!));
        Assert.True(vm.DeleteTexture("Checker"));
        Assert.Empty(vm.Document.Textures);
        for (int i = 0; i < 6; i++) Assert.True(vm.Undo(), "undo " + i);
        Assert.Equal(start, ShaderGraphJson.Serialize(vm.Document));
    }

    [Fact]
    public void The_rename_reaches_the_materials_through_the_host()
    {
        var vm = Vm();
        vm.NewFromTemplate(Flat, SgTemplates.Empty, out _);
        vm.AddTexture("Stripes");
        var calls = new List<(string From, string To)>();
        vm.RenameTextureInMaterials = (_, from, to) => { calls.Add((from, to)); return (1, ""); };
        Assert.True(vm.RenameTexture("Stripes", "Bands"));
        Assert.True(vm.Undo());
        Assert.Equal(new[] { ("Stripes", "Bands"), ("Bands", "Stripes") }, calls);
    }

    // ================================================================================= templates

    [Theory]
    [InlineData(SgTemplates.Riot)] [InlineData(SgTemplates.Tinted)] [InlineData(SgTemplates.Scroll)]
    [InlineData(SgTemplates.Blend)] [InlineData(SgTemplates.Pulse)] [InlineData(SgTemplates.Empty)]
    public void Every_template_is_a_valid_compiling_graph_laid_out_without_overlaps(string id)
    {
        var b = ShaderGraphTests.TestBase();
        var d = SgTemplates.Build(id, "T_" + id, b);
        var a = SgAnalyzer.Analyze(d, b);
        Assert.False(a.HasErrors, string.Join("\n", a.Errors));
        foreach (var sig in b.Signatures)
        {
            var hlsl = SgHlsl.Generate(d, b, sig, a);
            Assert.True(hlsl.Ok, string.Join("\n", hlsl.Errors));
        }
        var build = ShaderGraphCompiler.BuildAll(d, b);
        Assert.True(build.Ok, id + ": " + string.Join("\n", build.Diagnostics));
        // laid out: no two nodes at one spot, and the output is the right-most node
        Assert.Equal(d.Nodes.Count, d.Nodes.Select(n => (n.X, n.Y)).Distinct().Count());
        Assert.Equal(d.Nodes.Max(n => n.X), d.Output!.X);
        // the template survives a save
        Assert.NotNull(ShaderGraphJson.TryDeserialize(ShaderGraphJson.Serialize(d), out _));
    }

    [Fact]
    public void The_template_list_names_the_six_starting_points_and_the_editor_opens_each()
    {
        Assert.Equal(new[] { "riot", "tinted", "scroll", "blend", "pulse", "empty" }, SgTemplates.All.Select(t => t.Id));
        Assert.Contains(SgTemplates.All, t => t.Title.StartsWith("Riot look (Texture x TintColor overlay, identical to Riot's shader)"));
        Assert.Contains(SgTemplates.All, t => t.Title == "Scrolling texture (Panner)");
        foreach (var t in SgTemplates.All)
        {
            var vm = Vm();
            Assert.True(vm.NewFromTemplate(Flat, t.Id, out string error), t.Id + ": " + error);
            Assert.True(vm.IsOpen);
            Assert.False(vm.Analysis!.HasErrors, t.Id);
        }
        Assert.False(Vm().NewFromTemplate(Flat, "nope", out _));
    }

    [Fact]
    public void The_blend_template_declares_its_two_custom_textures_and_the_tinted_one_a_colour_parameter()
    {
        var b = ShaderGraphTests.TestBase();
        var blend = SgTemplates.Build(SgTemplates.Blend, "b", b);
        Assert.Equal(new[] { "SecondTexture", "MaskTexture" }, blend.Textures.Select(t => t.Name));
        var tinted = SgTemplates.Build(SgTemplates.Tinted, "t", b);
        var tint = Assert.Single(tinted.Params);
        Assert.True(tint.IsColor);
        Assert.Equal(3, tint.Components);
    }

    // ================================================================================= drag a wire into empty space

    [Fact]
    public void Auto_connect_offers_only_nodes_that_fit_the_dragged_pin()
    {
        var b = ShaderGraphTests.TestBase();
        var d = ShaderGraphDocument.CreateEmpty("t", Flat);
        var palette = SgCatalog.Palette(b, d);

        // from a float3 output: Lerp (any width), Multiply (any), Desaturation (float3 exactly), Cross, Component Mask... but not a Panner (UV is float2)
        var from3 = SgAutoConnect.ForOutput(palette, d, b, 3);
        Assert.Contains(from3, m => m.Entry.Type == "Desaturation" && m.Pin == "Color" && m.Exact);
        Assert.Contains(from3, m => m.Entry.Type == "Cross" && m.Exact);
        Assert.Contains(from3, m => m.Entry.Type == "Multiply" && m.Pin == "A");
        Assert.DoesNotContain(from3, m => m.Entry.Type is "Panner" or "Rotator" or "TilingOffset" or "TextureSample");
        Assert.DoesNotContain(from3, m => m.Entry.Type == SgCatalog.OutputType);
        Assert.DoesNotContain(from3, m => m.Entry.Type is "Constant" or "Time");        // no inputs at all
        Assert.True(from3.TakeWhile(m => m.Exact).Count() > 0 && from3.SkipWhile(m => m.Exact).All(m => !m.Exact), "exact matches come first");

        // from a float2 output: a texture sample (UV), a Panner, a Rotator
        var from2 = SgAutoConnect.ForOutput(palette, d, b, 2);
        Assert.Contains(from2, m => m.Entry.Type == "TextureSample" && m.Pin == "UV" && m.Exact);
        Assert.Contains(from2, m => m.Entry.Type == "Panner" && m.Pin == "UV");
        Assert.DoesNotContain(from2, m => m.Entry.Type is "Cross" or "Desaturation");   // float3 pins

        // a scalar broadcasts to anything
        var from1 = SgAutoConnect.ForOutput(palette, d, b, 1);
        Assert.Contains(from1, m => m.Entry.Type is "Cross");
        Assert.Contains(from1, m => m.Entry.Type is "TextureSample");
        Assert.Contains(from1, m => m.Entry.Type is "Desaturation");
    }

    [Fact]
    public void Auto_connect_from_an_input_offers_nodes_whose_output_fits_it()
    {
        var b = ShaderGraphTests.TestBase();
        var d = ShaderGraphDocument.CreateEmpty("t", Flat);
        var palette = SgCatalog.Palette(b, d);

        // Base Color takes float3: the texture's RGB (exact), a float3 constant, a Cross; not the float4 colour or the float2 UV
        var into3 = SgAutoConnect.ForInput(palette, d, b, 3);
        Assert.Contains(into3, m => m.Entry.Type == "TextureSample" && m.Pin == "RGB" && m.Exact);
        Assert.Contains(into3, m => m.Entry.Title == "Constant3 (float3)" && m.Exact);
        Assert.Contains(into3, m => m.Entry.Type == "Cross" && m.Exact);
        Assert.Contains(into3, m => m.Entry.Type == "Multiply");                         // a math node adopts the width
        Assert.DoesNotContain(into3, m => m.Entry.Type == "TexCoord");
        Assert.DoesNotContain(into3, m => m.Entry.Type == "ConstantColor");

        // Opacity takes a scalar: R of a texture, Time, a scalar parameter; not a vector constant
        var into1 = SgAutoConnect.ForInput(palette, d, b, 1);
        Assert.Contains(into1, m => m.Entry.Type == "Time");
        Assert.Contains(into1, m => m.Entry.Type == "TextureSample" && m.Pin == "R");
        Assert.DoesNotContain(into1, m => m.Entry.Title == "Constant3 (float3)");

        // an any-width input takes everything with an output
        Assert.True(SgAutoConnect.ForInput(palette, d, b, 0).Count > into3.Count);

        // the search filter narrows the compatible list only
        var hits = SgAutoConnect.Filter(into3, "lerp");
        Assert.All(hits, m => Assert.Equal("Lerp", m.Entry.Type));
    }

    [Fact]
    public void A_node_added_by_dragging_a_wire_is_connected_in_one_undo_step_from_either_end()
    {
        var vm = Vm();
        Assert.True(vm.NewFromTemplate(Flat, SgTemplates.Empty, out _));
        string start = ShaderGraphJson.Serialize(vm.Document!);

        // from the Base Color INPUT of the output node: pick "Texture Sample: DiffuseTexture", wired by its RGB output
        var tex = SgAutoConnect.ForInput(vm.Palette, vm.Document!, vm.Base, 3).First(m => m.Entry.Title == "Texture Sample: DiffuseTexture");
        string? t = vm.AddNodeAndConnect(tex.Entry, 100, 100, "n1", SgCatalog.BaseColorPin, fromIsOutput: false, tex.Pin);
        Assert.NotNull(t);
        Assert.Contains(vm.Document!.Links, l => l.FromNode == t && l.FromPin == "RGB" && l.ToNode == "n1" && l.ToPin == SgCatalog.BaseColorPin);
        Assert.Equal(2, vm.Document.Nodes.Count);

        // from the texture's RGB OUTPUT: pick Desaturation, wired into Color
        var des = SgAutoConnect.ForOutput(vm.Palette, vm.Document, vm.Base, 3).First(m => m.Entry.Type == "Desaturation");
        string? s = vm.AddNodeAndConnect(des.Entry, 300, 100, t!, "RGB", fromIsOutput: true, des.Pin);
        Assert.NotNull(s);
        Assert.Contains(vm.Document.Links, l => l.FromNode == t && l.ToNode == s && l.ToPin == "Color");
        Assert.False(vm.Analysis!.HasErrors, string.Join("\n", vm.Analysis.Errors));

        Assert.True(vm.Undo()); Assert.True(vm.Undo());
        Assert.Equal(start, ShaderGraphJson.Serialize(vm.Document));
    }

    [Fact]
    public void Dragging_into_a_new_parameter_line_creates_the_parameter_too_in_the_same_step()
    {
        var vm = Vm();
        vm.NewFromTemplate(Flat, SgTemplates.Empty, out _);
        string start = ShaderGraphJson.Serialize(vm.Document!);
        var line = SgAutoConnect.ForInput(vm.Palette, vm.Document!, vm.Base, 3).First(m => m.Entry.Title == "Scalar Parameter (new, custom)");   // a scalar broadcasts into the float3 Base Color
        Assert.NotNull(vm.AddNodeAndConnect(line.Entry, 0, 0, "n1", SgCatalog.BaseColorPin, false, line.Pin));
        Assert.Single(vm.Document!.Params);
        Assert.True(vm.Undo());
        Assert.Equal(start, ShaderGraphJson.Serialize(vm.Document));
    }

    // ================================================================================= right click on an input pin

    [Fact]
    public void Promote_to_Parameter_makes_a_custom_parameter_of_the_pins_type_with_its_value_and_wires_it()
    {
        var vm = Vm();
        Assert.True(vm.NewFromTemplate(Flat, SgTemplates.Empty, out _));
        string start = ShaderGraphJson.Serialize(vm.Document!);

        // Opacity: a float with the pin's value 1
        Assert.Equal("OpacityParam", vm.PromoteToParameter("n1", SgCatalog.OpacityPin));   // "opacity" is a name the generated main declares
        var op = vm.Document!.FindParam("OpacityParam")!;
        Assert.Equal((1, "1", false), (op.Components, op.Default, op.IsColor));
        Assert.Contains(vm.Document.Links, l => l.ToNode == "n1" && l.ToPin == SgCatalog.OpacityPin);

        // Base Color: a colour3 (the pin is float3)
        string? colour = vm.PromoteToParameter("n1", SgCatalog.BaseColorPin);
        Assert.Equal("BaseColorParam", colour);
        var bc = vm.Document.FindParam("BaseColorParam")!;
        Assert.Equal((3, true), (bc.Components, bc.IsColor));

        // an already wired pin is refused; a pin of a math node with an edited value keeps it
        Assert.Null(vm.PromoteToParameter("n1", SgCatalog.OpacityPin));
        string? mul = vm.AddNode(Entry(vm, "Multiply"), 100, 100);
        Assert.True(vm.SetProp(mul!, "B", "0.25"));
        Assert.Equal("B", vm.PromoteToParameter(mul!, "B"));
        Assert.Equal("0.25", vm.Document.FindParam("B")!.Default);

        Assert.False(vm.Analysis!.HasErrors, string.Join("\n", vm.Analysis.Errors));
        // each promotion is one undo step
        for (int i = 0; i < 5; i++) Assert.True(vm.Undo(), "undo " + i);
        Assert.Equal(start, ShaderGraphJson.Serialize(vm.Document));
    }

    [Fact]
    public void Add_Constant_feeds_the_pin_a_constant_of_its_width_holding_its_value()
    {
        var vm = Vm();
        vm.NewFromTemplate(Flat, SgTemplates.Empty, out _);
        string start = ShaderGraphJson.Serialize(vm.Document!);
        string? c = vm.AddConstantTo("n1", SgCatalog.BaseColorPin);
        Assert.NotNull(c);
        Assert.Equal("0 0 0", vm.Document!.Find(c!)!.Prop("value"));                      // float3, the pin's current default broadcast
        Assert.Contains(vm.Document.Links, l => l.FromNode == c && l.ToNode == "n1");
        string? o = vm.AddConstantTo("n1", SgCatalog.OpacityPin);
        Assert.Equal("1", vm.Document.Find(o!)!.Prop("value"));
        Assert.False(vm.Analysis!.HasErrors, string.Join("\n", vm.Analysis.Errors));
        Assert.Null(vm.AddConstantTo("n1", SgCatalog.OpacityPin));                        // already wired
        Assert.True(vm.Undo()); Assert.True(vm.Undo());
        Assert.Equal(start, ShaderGraphJson.Serialize(vm.Document));
    }

    // ================================================================================= palette tooltips

    [Fact]
    public void Every_palette_entry_explains_what_the_node_does_in_one_line()
    {
        var b = ShaderGraphTests.TestBase();
        var d = ShaderGraphDocument.CreateEmpty("t", Flat);
        d.Params.Add(new SgCustomParam { Name = "Amount", Components = 1 });
        d.Textures.Add(new SgCustomTexture { Name = "Stripes" });
        var palette = SgCatalog.Palette(b, d);
        Assert.NotEmpty(palette);
        foreach (var e in palette)
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Tooltip), "no tooltip for " + e.Display);
            Assert.DoesNotContain('\n', e.Tooltip);
            Assert.True(e.Tooltip.Length < 200, "tooltip too long for " + e.Display);
        }
    }

    // ================================================================================= the export

    [Fact]
    public void Custom_textures_are_proven_in_game_and_the_gate_still_blocks_when_it_is_switched_off()
    {
        Assert.True(SgShip.CustomTexturesProven, "test7 worked in game: custom textures ship");
        var b = ShaderGraphTests.TestBase();
        var d = ShaderGraphDocument.CreateEmpty("g", Flat);
        d.Textures.Add(new SgCustomTexture { Name = "Stripes", DefaultPath = "assets/x/stripes.tex" });
        ShaderGraphTests.Add(d, "n2", "TextureSample", ("texture", "Stripes"));
        ShaderGraphTests.Link(d, "n2", "RGB", "n1", SgCatalog.BaseColorPin);
        var graph = new SgResolvedGraph(new SgNumberEntry { File = "g", Base = Flat, Number = 1 }, "g.json", d, null);
        var m = new SgAssignedMaterial("a.bin", "Mat", Flat, "1", Array.Empty<string>(), new[] { "Stripes" });

        Assert.Empty(SgShipValidator.Validate(m, graph, b));                                       // proven: a sampler named like the custom texture is fine
        var blocked = SgShipValidator.Validate(m, graph, b, customTexturesProven: false);
        Assert.Contains(blocked, x => x.Contains("Custom textures are waiting for in-game test7"));

        // a sampler that is neither a base nor a custom texture is still refused
        var stray = new SgAssignedMaterial("a.bin", "Mat", Flat, "1", Array.Empty<string>(), new[] { "Nope" });
        Assert.Contains(SgShipValidator.Validate(stray, graph, b), x => x.Contains("'Nope'"));
        // a custom texture whose name the base uses is refused
        d.Textures.Add(new SgCustomTexture { Name = "DetailTexture" });
        Assert.Contains(SgShipValidator.Validate(m, graph, b), x => x.Contains("custom texture 'DetailTexture' cannot be declared"));
    }
}

/// <summary>M835: what an open, saved graph tells the viewports (the static hook the main window listens to). Serial: it uses the process-wide viewport service.</summary>
[Collection("ShaderGraphViewportStatics")]
public sealed class ShaderGraphViewportHookTests : IDisposable
{
    private const string Flat = "Shaders/StaticMesh/DefaultEnv_Flat";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "reyengine-m835-hook-" + Guid.NewGuid().ToString("N"));
    public ShaderGraphViewportHookTests() => Directory.CreateDirectory(_root);
    public void Dispose() { ShaderGraphViewport.Reset(); try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task A_saved_graph_publishes_its_edits_and_its_closing_to_the_viewport_service_and_an_unsaved_one_does_not()
    {
        var vm = new ShaderGraphViewModel(() => _root)
        {
            BaseResolver = shader => SgBase.IsSupported(shader) ? (ShaderGraphTests.TestBase(), "") : (null, "no"),
            CompileDelay = TimeSpan.Zero,
        };
        var seen = new List<string>();
        void Handler(string path) => seen.Add(path);
        ShaderGraphViewport.GraphChanged += Handler;
        try
        {
            Assert.True(vm.NewFromTemplate(Flat, SgTemplates.Tinted, out string error), error);
            await vm.CompileNowAsync();
            Assert.Empty(seen);                                           // not saved: nothing a material could name yet
            Assert.True(await vm.SaveAsync());
            string path = vm.FilePath!;

            Assert.True(vm.SetParameterDefault("Tint", "0.2 0.4 0.6"));
            await vm.CompileNowAsync();
            Assert.Contains(path, seen);                                  // an edit of a saved graph is published once compiled
            Assert.Contains("0.2 0.4 0.6", ShaderGraphJson.Serialize(vm.Document!));
            var status = ShaderGraphViewport.StatusOf(path);
            Assert.NotNull(status);
            Assert.True(status!.Ok, status.Text);

            // a graph with errors is reported as such, not as drawn
            Assert.True(vm.SetProp("n2", "texture", "NotATexture"));
            await vm.CompileNowAsync();
            Assert.False(ShaderGraphViewport.StatusOf(path)!.Ok);
            Assert.Contains("has errors", ShaderGraphViewport.StatusOf(path)!.Text);

            seen.Clear();
            vm.Close();
            Assert.Contains(path, seen);                                  // closing tells the viewport to go back to the saved file
        }
        finally { ShaderGraphViewport.GraphChanged -= Handler; }
    }

    [Fact]
    public async Task Closing_replacing_or_disposing_an_editor_releases_its_live_document()
    {
        ShaderGraphViewModel NewVm() => new(() => _root)
        {
            BaseResolver = shader => SgBase.IsSupported(shader) ? (ShaderGraphTests.TestBase(), "") : (null, "no"),
            CompileDelay = TimeSpan.Zero,
        };
        var seen = new List<string>();
        void Handler(string path) => seen.Add(path);
        ShaderGraphViewport.GraphChanged += Handler;
        try
        {
            var a = NewVm();
            Assert.True(a.NewFromTemplate(Flat, SgTemplates.Tinted, out string e, "GraphA"), e);
            Assert.True(await a.SaveAsync());
            string pathA = a.FilePath!;
            Assert.True(a.SetParameterDefault("Tint", "0.1 0.2 0.3"));
            await a.CompileNowAsync();
            Assert.True(ShaderGraphViewport.IsLive(pathA));                      // an unsaved edit stands in for the file

            // Open B (a saved graph) while A is open: A's edits stop being drawn
            var b = NewVm();
            Assert.True(b.NewFromTemplate(Flat, SgTemplates.Scroll, out e, "GraphB"), e);
            Assert.True(await b.SaveAsync());
            seen.Clear();
            Assert.True(a.Open(b.FilePath!, out e), e);
            Assert.False(ShaderGraphViewport.IsLive(pathA));
            Assert.Contains(pathA, seen);                                         // and the viewport is told to go back to the saved A

            // a NEW graph while B is open releases B too
            await a.CompileNowAsync();
            Assert.True(ShaderGraphViewport.IsLive(b.FilePath!));
            seen.Clear();
            Assert.True(a.NewFromTemplate(Flat, SgTemplates.Empty, out e), e);
            Assert.False(ShaderGraphViewport.IsLive(b.FilePath!));
            Assert.Contains(b.FilePath!, seen);

            // and so does disposing the editor
            Assert.True(a.Open(pathA, out e), e);
            await a.CompileNowAsync();
            Assert.True(ShaderGraphViewport.IsLive(pathA));
            seen.Clear();
            a.Dispose();
            Assert.False(ShaderGraphViewport.IsLive(pathA));
            Assert.Contains(pathA, seen);
        }
        finally { ShaderGraphViewport.GraphChanged -= Handler; }
    }

    [Fact]
    public void The_service_says_nothing_for_a_material_without_a_graph_and_names_the_problem_for_one_it_cannot_resolve()
    {
        ShaderGraphViewport.Reset();
        // pure checks need no shader: the macro test and the project test come before the cache is touched
        Assert.Null(ShaderGraphViewport.TryOverride(Flat, new Dictionary<string, string>(), null!, null!, null, out string why));
        Assert.Equal("", why);
        Assert.Null(ShaderGraphViewport.TryOverride(Flat, new Dictionary<string, string> { ["REY_GRAPH"] = "x" }, null!, null!, null, out why));
        Assert.Contains("not a graph number", why);
        Assert.Null(ShaderGraphViewport.TryOverride("Shaders/StaticMesh/Mantis", new Dictionary<string, string> { ["REY_GRAPH"] = "1" }, null!, null!, null, out why));
        Assert.Contains("not a base shader", why);
        Assert.Null(ShaderGraphViewport.TryOverride(Flat, new Dictionary<string, string> { ["REY_GRAPH"] = "1" }, null!, null!, null, out why));
        Assert.Contains("no project is open", why);
        ShaderGraphViewport.ProjectRoot = () => _root;
        Assert.Null(ShaderGraphViewport.TryOverride(Flat, new Dictionary<string, string> { ["REY_GRAPH"] = "1" }, null!, null!, null, out why));
        Assert.Contains("registry is missing", why);
    }

    [Fact]
    public void A_graph_that_could_not_be_resolved_is_looked_up_again_once_the_registry_changes()
    {
        ShaderGraphViewport.Reset();
        ShaderGraphViewport.ProjectRoot = () => _root;
        var macros = new Dictionary<string, string> { ["REY_GRAPH"] = "1" };
        Assert.Null(ShaderGraphViewport.TryOverride(Flat, macros, null!, null!, null, out string why));
        Assert.Contains("registry is missing", why);
        Assert.Null(ShaderGraphViewport.TryOverride(Flat, macros, null!, null!, null, out why));   // the failure is cached against the registry stamp
        Assert.Contains("registry is missing", why);
        // the registry now exists (graph 1 is registered, its file is not there): the cached failure must not be served
        Assert.Equal(1, ShaderGraphRegistry.NumberFor(_root, "Gone", Flat));
        Assert.Null(ShaderGraphViewport.TryOverride(Flat, macros, null!, null!, null, out why));
        Assert.DoesNotContain("registry is missing", why);
    }

    [Fact]
    public void The_materials_texture_link_and_the_shaders_bin_link_hash_one_path_whatever_its_case_or_slashes()
    {
        var a = new SgCustomTexture { Name = "Pic", DefaultPath = "ASSETS/Maps/Pic.TEX" };
        var b = new SgCustomTexture { Name = "Pic", DefaultPath = "assets/maps/pic.tex" };
        Assert.Equal(HashAlgorithms.WadPath(b.NormalizedPath), HashAlgorithms.WadPath(a.NormalizedPath));
        Assert.NotNull(SgNames.TexturePathProblem("0x3fa863f58325b833"));
        Assert.NotNull(SgNames.TexturePathProblem("0X3FA863F5"));
        Assert.Null(SgNames.TexturePathProblem("assets/maps/pic.tex"));
    }
}
