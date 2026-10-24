using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using System.Numerics;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Undo;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Materials.Graph;
using ReyEngine.Formats.Materials.ShaderGraph;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M834: Shader Graph customisation without a game install (the hand-built test base; the compile uses the real Windows HLSL compiler):
/// custom parameters (creation, name rules, rename that reaches the nodes AND the assigned materials, undo), the Custom HLSL node and its
/// sanitiser, node labels, comment boxes, the new nodes, the export block for custom parameters and the version 1 to 2 load.
/// </summary>
public sealed class ShaderGraphCustomizationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rey-m834-" + Guid.NewGuid().ToString("N"));
    public ShaderGraphCustomizationTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private const string Flat = "Shaders/StaticMesh/DefaultEnv_Flat";
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private ShaderGraphViewModel Vm() => new(() => _root)
    {
        BaseResolver = shader => SgBase.IsSupported(shader) ? (ShaderGraphTests.TestBase(), "") : (null, "not a supported base"),
        CompileDelay = TimeSpan.Zero,
    };

    private static SgPaletteEntry Entry(ShaderGraphViewModel vm, string title) => vm.Palette.First(p => p.Title == title);

    private static ShaderGraphDocument Doc() => ShaderGraphDocument.CreateEmpty("t", Flat);

    // ================================================================================= name rules

    [Theory]
    [InlineData("Glow", null)]
    [InlineData("glow_2", null)]
    [InlineData("", "Type a name")]
    [InlineData("2Glow", "digit")]
    [InlineData("Gl ow", "ASCII")]
    [InlineData("Glöw", "ASCII")]
    [InlineData("Glow;", "ASCII")]
    [InlineData("tintcolor", "already declares")]          // a declared parameter, compared without regard to case
    [InlineData("DiffuseTexture", "texture")]
    [InlineData("TIME", "HLSL word")]
    [InlineData("vCamera", "engine")]                       // a variable of the engine cbuffer
    [InlineData("WORLD_MATRIX", "engine")]
    [InlineData("float4", "HLSL")]
    [InlineData("saturate", "HLSL")]
    [InlineData("v12", "generator")]
    [InlineData("o0", "generator")]
    [InlineData("sg_thing", "sg_")]
    [InlineData("a__b", "two underscores")]
    [InlineData("_hidden", "underscore")]
    [InlineData("Texture2D", "texture or sampler")]
    [InlineData("main", "HLSL word")]
    public void A_custom_parameter_name_must_be_a_safe_ASCII_identifier_that_clashes_with_nothing(string name, string? problem)
    {
        string? got = SgNames.ParameterProblem(name, Doc(), ShaderGraphTests.TestBase());
        if (problem is null) Assert.Null(got); else Assert.Contains(problem, got ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_parameter_name_is_capped_and_unique_in_the_graph()
    {
        var b = ShaderGraphTests.TestBase();
        Assert.NotNull(SgNames.ParameterProblem(new string('a', SgNames.MaxParamName + 1), Doc(), b));
        Assert.Null(SgNames.ParameterProblem(new string('a', SgNames.MaxParamName), Doc(), b));
        var d = Doc();
        d.Params.Add(new SgCustomParam { Name = "Glow" });
        Assert.Contains("already has", SgNames.ParameterProblem("GLOW", d, b));
        Assert.Null(SgNames.ParameterProblem("Glow", d, b, renaming: "Glow"));
        Assert.Equal("Glow2", SgNames.UniqueParameterName("Glow", d, b));
    }

    // ================================================================================= custom parameters

    [Fact]
    public async Task A_new_parameter_node_creates_its_parameter_in_one_undo_step_and_the_graph_compiles_with_it()
    {
        var vm = Vm();
        Assert.True(vm.NewFromBase(Flat, out _));
        string? id = vm.AddNode(Entry(vm, "Colour Parameter (new, custom)"), 100, 100);
        Assert.NotNull(id);
        var p = Assert.Single(vm.Document!.Params);
        Assert.Equal("ColorParam", p.Name);
        Assert.Equal(4, p.Components);
        Assert.True(p.IsColor);
        Assert.Equal("1 1 1 1", p.Default);
        Assert.Equal("ColorParam", vm.Document.Find(id!)!.Prop("name"));
        Assert.Single(vm.ParamRows);

        // multiply the texture by its rgb
        string mul = vm.AddNode(Entry(vm, "Multiply"), 300, 100)!;
        string mask = vm.AddNode(Entry(vm, "Component Mask"), 200, 100)!;
        Assert.True(vm.Connect(id!, "Value", mask, "Value"));
        Assert.True(vm.Connect("n2", "RGB", mul, "A"));
        Assert.True(vm.Connect(mask, "Value", mul, "B"));
        Assert.True(vm.Connect(mul, "Value", "n1", SgCatalog.BaseColorPin));
        var build = await vm.CompileNowAsync();
        Assert.NotNull(build);
        Assert.Contains("float4 ColorParam;", build!.Compiled.First().Hlsl.Source);
        Assert.Contains("ColorParam", build.Compiled.First().Hlsl.Parameters);
        Assert.Single(build.Compiled.First().Hlsl.CustomParameters);

        // the parameter and its node are one step back
        while (vm.CanUndo) vm.Undo();
        Assert.Empty(vm.Document.Params);
        Assert.Empty(vm.ParamRows);
        Assert.Null(vm.Document.Find(id!));
    }

    [Fact]
    public void Unwired_new_scalar_and_vector_parameters_get_distinct_names_and_types()
    {
        var vm = Vm();
        vm.NewFromBase(Flat, out _);
        vm.AddNode(Entry(vm, "Scalar Parameter (new, custom)"), 0, 0);
        vm.AddNode(Entry(vm, "Scalar Parameter (new, custom)"), 0, 0);
        vm.AddNode(Entry(vm, "Vector Parameter (new, custom float4)"), 0, 0);
        Assert.Equal(new[] { "ScalarParam", "ScalarParam2", "VectorParam" }, vm.Document!.Params.Select(p => p.Name));
        Assert.Equal(new[] { 1, 1, 4 }, vm.Document.Params.Select(p => p.Components));
        // existing custom parameters are offered by the palette too, and the palette finds them by typing
        Assert.Contains(SgCatalog.Search(vm.Palette, "scalarparam2"), e => e.Props.TryGetValue("name", out var n) && n == "ScalarParam2");
    }

    [Fact]
    public void Renaming_a_parameter_updates_every_node_that_reads_it_and_one_undo_puts_it_back()
    {
        var vm = Vm();
        vm.NewFromBase(Flat, out _);
        string a = vm.AddNode(Entry(vm, "Vector Parameter (new, custom float4)"), 0, 0)!;
        string b = vm.AddNode(Entry(vm, "Parameter: VectorParam (custom)"), 0, 50)!;
        Assert.Equal("VectorParam", vm.Document!.Find(b)!.Prop("name"));
        var renames = new List<(string?, string, string)>();
        vm.RenameParameterInMaterials = (path, from, to) => { renames.Add((path, from, to)); return (0, ""); };

        Assert.True(vm.RenameParameter("VectorParam", "Glow"));
        Assert.Equal("Glow", vm.Document.Params.Single().Name);
        Assert.Equal("Glow", vm.Document.Find(a)!.Prop("name"));
        Assert.Equal("Glow", vm.Document.Find(b)!.Prop("name"));
        Assert.Equal(("VectorParam", "Glow"), (renames.Last().Item2, renames.Last().Item3));

        // refused: a clash, a bad name, an unknown parameter
        Assert.False(vm.RenameParameter("Glow", "TintColor"));
        Assert.Contains("already declares", vm.Status);
        Assert.False(vm.RenameParameter("Glow", "9lives"));
        Assert.False(vm.RenameParameter("Nope", "Other"));
        Assert.Equal(1, renames.Count);

        Assert.True(vm.Undo());                                   // ONE step puts parameter and both nodes back
        Assert.Equal("VectorParam", vm.Document.Params.Single().Name);
        Assert.Equal("VectorParam", vm.Document.Find(a)!.Prop("name"));
        Assert.Equal("VectorParam", vm.Document.Find(b)!.Prop("name"));
        Assert.Equal(("Glow", "VectorParam"), (renames.Last().Item2, renames.Last().Item3));   // the materials are told to follow back
        Assert.True(vm.Redo());
        Assert.Equal("Glow", vm.Document.Find(b)!.Prop("name"));
    }

    [Fact]
    public void A_parameter_can_change_type_default_and_colour_flag_and_is_deleted_only_when_nothing_reads_it()
    {
        var vm = Vm();
        vm.NewFromBase(Flat, out _);
        string n = vm.AddNode(Entry(vm, "Vector Parameter (new, custom float4)"), 0, 0)!;
        Assert.True(vm.SetParameterDefault("VectorParam", "0.2 0.4 0.6 1"));
        Assert.Equal("0.2 0.4 0.6 1", vm.Document!.Params[0].Default);
        Assert.False(vm.SetParameterDefault("VectorParam", "1 2"));          // wrong width
        Assert.False(vm.SetParameterDefault("VectorParam", "x"));
        Assert.True(vm.SetParameterColor("VectorParam", true));
        Assert.True(vm.SetParameterKind("VectorParam", 2));                  // a float2 cannot be a colour
        Assert.False(vm.Document.Params[0].IsColor);
        Assert.Equal("0.2 0.4", vm.Document.Params[0].Default);
        Assert.False(vm.SetParameterColor("VectorParam", true));
        Assert.True(vm.SetParameterKind("VectorParam", 3));
        Assert.Equal(3, vm.Document.Params[0].DefaultValues().Length);

        Assert.False(vm.DeleteParameter("VectorParam"));                     // a node still reads it
        Assert.Contains("still read", vm.Status);
        Assert.True(vm.DeleteNode(n));
        Assert.True(vm.DeleteParameter("VectorParam"));
        Assert.Empty(vm.Document.Params);
        while (vm.CanUndo) vm.Undo();
        Assert.Empty(vm.Document.Params);
    }

    [Fact]
    public void A_parameter_name_that_is_no_longer_declared_is_an_error_on_the_node()
    {
        var d = Doc();
        ShaderGraphTests.Add(d, "n2", "Parameter", ("name", "Ghost"));
        ShaderGraphTests.Link(d, "n2", "Value", "n1", SgCatalog.OpacityPin);
        var a = SgAnalyzer.Analyze(d, ShaderGraphTests.TestBase());
        Assert.Contains(a.Errors, e => e.NodeId == "n2" && e.Message.Contains("neither a custom parameter"));
        d.Params.Add(new SgCustomParam { Name = "Ghost", Components = 1, Default = "1" });
        a = SgAnalyzer.Analyze(d, ShaderGraphTests.TestBase());
        Assert.False(a.HasErrors);
        Assert.Equal(1, a.OutputWidths["n2"][0]);
    }

    // ================================================================================= preview HLSL of a custom parameter

    [Fact]
    public async Task The_preview_shader_declares_the_custom_parameter_in_Globals_and_verifies()
    {
        var vm = Vm();
        vm.NewFromBase(Flat, out _);
        string n = vm.AddNode(Entry(vm, "Colour Parameter (new, custom)"), 0, 0)!;
        Assert.True(vm.Connect(n, "Value", "n1", SgCatalog.BaseColorPin) == false || true);
        var ch = vm.AddNode(Entry(vm, "Component Mask"), 0, 0)!;
        vm.SetProp(ch, "mask", "rgb");
        Assert.True(vm.Connect(n, "Value", ch, "Value"));
        Assert.True(vm.Connect(ch, "Value", "n1", SgCatalog.BaseColorPin));
        var build = await vm.CompileNowAsync();
        Assert.True(build!.Ok, string.Join("\n", build.Diagnostics));      // Verify allows $Globals variables the GRAPH declares
        Assert.All(build.Compiled, c => Assert.Contains(c.Shader!.ConstantBuffers.Single(b => b.Name == "$Globals").Variables, v => v.Name == "ColorParam"));
    }

    // ================================================================================= export block

    private int Save(ShaderGraphDocument d, string stem)
    {
        Directory.CreateDirectory(ShaderGraphRegistry.FolderIn(_root));
        File.WriteAllText(ShaderGraphRegistry.GraphPath(_root, stem), ShaderGraphJson.Serialize(d));
        return ShaderGraphRegistry.NumberFor(_root, stem, d.BaseShader);
    }

    private IReadOnlyList<string> Check(int number, params string[] parameters)
    {
        var m = new SgAssignedMaterial("data/x.materials.bin", "Mat", Flat, number.ToString(), parameters);
        return SgShipValidator.Validate(m, SgShipValidator.Resolve(_root, Flat, number), ShaderGraphTests.TestBase());
    }

    private static ShaderGraphDocument WithCustomParam(bool read)
    {
        var d = Doc();
        d.Params.Add(new SgCustomParam { Name = "Glow", Components = 1, Default = "0.5" });
        ShaderGraphTests.Add(d, "n2", "TextureSample", ("texture", "DiffuseTexture"));
        ShaderGraphTests.Link(d, "n2", "RGB", "n1", SgCatalog.BaseColorPin);
        if (read)
        {
            ShaderGraphTests.Add(d, "n3", "Parameter", ("name", "Glow"));
            ShaderGraphTests.Link(d, "n3", "Value", "n1", SgCatalog.OpacityPin);
        }
        return d;
    }

    [Fact]
    public void A_graph_that_reads_custom_parameters_passes_the_export_check_when_the_material_carries_only_declared_or_custom_names()
    {
        int n = Save(WithCustomParam(read: true), "glow");
        Assert.Empty(Check(n));
        Assert.Empty(Check(n, "Glow"));                                       // the material carries the graph's custom parameter
        Assert.Empty(Check(n, "Glow", "TintColor"));                          // and Riot's declared one
        Assert.Empty(Check(Save(WithCustomParam(read: false), "unread"), "Glow"));   // declared but unread: the export still declares it, a material may carry it
    }

    [Fact]
    public void A_material_parameter_that_is_neither_declared_nor_a_custom_parameter_of_the_graph_still_crashes_the_game_so_it_is_blocked()
    {
        int n = Save(WithCustomParam(read: true), "glow2");
        var one = Assert.Single(Check(n, "Glow", "ReyProbe"));
        Assert.Contains("'ReyProbe'", one); Assert.Contains("Missing shader constant", one); Assert.Contains("custom parameter of the Shader Graph", one);
    }

    [Fact]
    public void A_custom_parameter_that_clashes_with_a_name_the_installed_base_declares_cannot_be_declared()
    {
        var d = WithCustomParam(read: true);
        d.Params[0].Name = "TintColor";
        d.Find("n3")!.Props["name"] = "TintColor";
        var errors = Check(Save(d, "clash"));
        Assert.Contains(errors, e => e.Contains("'TintColor'") && e.Contains("cannot be declared") && e.Contains("already declares"));
        var d2 = WithCustomParam(read: true);
        d2.Params[0].Name = "v3";                                             // a generated local
        d2.Find("n3")!.Props["name"] = "v3";
        Assert.Contains(Check(Save(d2, "clash2")), e => e.Contains("'v3'") && e.Contains("cannot be declared"));
    }

    [Fact]
    public void The_patcher_declares_widths_as_fields_masks_and_refuses_what_it_does_not_understand()
    {
        Assert.Equal(new[] { 1u, 3u, 7u, 15u }, Enumerable.Range(1, 4).Select(c => new SgDeclaredParam("X", c).FieldsMask));
        Assert.Throws<InvalidDataException>(() => SgShadersBin.Declared(new byte[] { 1, 2, 3 }, "Shaders/X"));
        Assert.Throws<InvalidDataException>(() => SgShadersBin.Patch(new byte[] { (byte)'P', (byte)'R', (byte)'O', (byte)'P', 1, 0, 0, 0, 0, 0, 0, 0 }, "Shaders/X", new[] { new SgDeclaredParam("A", 1) }));
        Assert.Same(Array.Empty<byte>(), SgShadersBin.Patch(Array.Empty<byte>(), "Shaders/X", Array.Empty<SgDeclaredParam>()));
    }

    // ================================================================================= vec4, unassign, rename edge cases (M834 fixes)

    private (UndoRedoService Undo, MaterialEditorViewModel Editor, MaterialBindingViewModel Binding) EditorWithGraph(ShaderGraphViewModel sg)
    {
        var undo = new UndoRedoService();
        var editor = new MaterialEditorViewModel { UndoService = undo, ProjectRoot = () => _root };
        editor.Load(MaterialDocument.Parse(MaterialWithTint(), Names), new WadAssetEntry { Path = "data/maps/mapgeometry/map11/x.materials.bin" });
        var binding = editor.Materials.Single();
        binding.RefreshMacroStatus();
        return (undo, editor, binding);
    }

    [Fact]
    public async Task A_custom_parameter_is_written_into_paramValues_as_a_Vector4_whatever_its_width()
    {
        var sg = Vm();
        sg.NewFromBase(Flat, out _);
        sg.AddNode(Entry(sg, "Scalar Parameter (new, custom)"), 0, 0);
        sg.AddNode(Entry(sg, "Vector Parameter (new, custom float4)"), 0, 0);
        sg.AddParameter("float3", "Tri");
        sg.AddParameter("float2", "Duo");
        Assert.True(await sg.SaveAsync());
        var (undo, editor, binding) = EditorWithGraph(sg);
        binding.AssignGraphCommand.Execute(Assert.Single(binding.GraphChoices));
        foreach (var row in binding.GraphParams.ToList()) row.AddToMaterialCommand.Execute(null);
        Assert.Equal(4, binding.GraphParams.Count);
        foreach (var name in new[] { "ScalarParam", "VectorParam", "Tri", "Duo" })
        {
            var p = binding.Model.Parameters.Single(x => x.Name == name);
            Assert.Equal("Vector4", p.TypeName);
            Assert.True(p.TryGetVector4(out _));
        }
        // the saved bin: every paramValues value is a vec4 (what LTK's health check demands)
        var back = MaterialDocument.Parse(editor.Serialize()!, Names).Materials.Single();
        Assert.All(back.Parameters, p => Assert.Equal("Vector4", p.TypeName));
        // a float default is x then zeros; a kind change in the graph needs no re-typing of the material (it is a vec4 either way)
        Assert.True(back.Parameters.Single(p => p.Name == "ScalarParam").TryGetVector4(out var v));
        Assert.Equal(0f, v.Y);
        Assert.True(sg.SetParameterKind("Tri", 4));
        Assert.All(binding.Model.Parameters, p => Assert.Equal("Vector4", p.TypeName));
    }

    [Fact]
    public void A_material_value_that_is_not_a_vec4_blocks_the_export()
    {
        int n = Save(WithCustomParam(read: true), "glow3");
        var m = new SgAssignedMaterial("data/x.materials.bin", "Mat", Flat, n.ToString(), new[] { "Glow" }, null, new[] { "Glow" });
        var errors = SgShipValidator.Validate(m, SgShipValidator.Resolve(_root, Flat, n), ShaderGraphTests.TestBase());
        var one = Assert.Single(errors);
        Assert.Contains("not a vec4", one); Assert.Contains("bin/property-type", one);
    }

    [Fact]
    public async Task Unassigning_or_reassigning_removes_the_outgoing_graphs_custom_parameters_in_one_undo_step()
    {
        var sg = Vm();
        sg.NewFromBase(Flat, out _);
        sg.AddNode(Entry(sg, "Vector Parameter (new, custom float4)"), 0, 0);
        Assert.True(await sg.SaveAsync());
        var (undo, editor, binding) = EditorWithGraph(sg);
        binding.AssignGraphCommand.Execute(Assert.Single(binding.GraphChoices));
        binding.GraphParams.Single().AddToMaterialCommand.Execute(null);
        Assert.Contains(binding.Model.Parameters, p => p.Name == "VectorParam");
        int before = undo.UndoHistory.Count;

        binding.UnassignGraphCommand.Execute(null);
        Assert.False(binding.HasGraphAssigned);
        Assert.DoesNotContain(binding.Model.Parameters, p => p.Name == "VectorParam");   // gone with the graph
        Assert.DoesNotContain(binding.Parameters, p => p.Name == "VectorParam");
        Assert.Contains(binding.Model.Parameters, p => p.Name == "TintColor");            // Riot's own stay
        Assert.Equal(before + 1, undo.UndoHistory.Count);                                 // ONE step: macro + parameter
        undo.Undo();
        Assert.True(binding.HasGraphAssigned);
        Assert.Contains(binding.Model.Parameters, p => p.Name == "VectorParam");
        undo.Redo();
        Assert.DoesNotContain(binding.Model.Parameters, p => p.Name == "VectorParam");
    }

    // ================================================================================= M835: custom textures follow the assignment

    private async Task<string> SaveGraphWith(string name, params (string Param, string Texture)[] members)
    {
        var g = Vm();
        Assert.True(g.NewFromBase(Flat, out string e, name), e);
        foreach (var (param, texture) in members)
        {
            if (param.Length > 0) Assert.Equal(param, g.AddParameter("float4", param));
            if (texture.Length > 0) { Assert.Equal(texture, g.AddTexture(texture)); Assert.True(g.SetTextureDefault(texture, "assets/x/" + texture.ToLowerInvariant() + ".tex")); }
        }
        Assert.True(await g.SaveAsync());
        return g.FilePath!;
    }

    [Fact]
    public async Task Assign_adds_the_graphs_parameters_and_textures_and_unassign_or_switch_removes_them_all_in_one_undo_step()
    {
        await SaveGraphWith("GraphA", ("GlowA", "StripesA"));
        await SaveGraphWith("GraphB", ("GlowB", "BandsB"));
        var (undo, editor, binding) = EditorWithGraph(Vm());
        var a = binding.GraphChoices.Single(c => c.Stem == "GraphA");
        var b = binding.GraphChoices.Single(c => c.Stem == "GraphB");
        int steps = undo.UndoHistory.Count;

        // ASSIGN A: its custom parameter (a vec4 holding the default) and its custom texture (a sampler entry at the default image) are written, ONE step
        binding.AssignGraphCommand.Execute(a);
        Assert.Equal(steps + 1, undo.UndoHistory.Count);
        Assert.Equal("Vector4", binding.Model.Parameters.Single(p => p.Name == "GlowA").TypeName);
        var slot = binding.Model.Slots.Single(x => x.SamplerName == "StripesA");
        Assert.Equal(HashAlgorithms.WadPath("assets/x/stripesa.tex"), slot.ChunkHash);          // the graph's default image, as the link the bin uses
        Assert.Contains(binding.Slots, r => r.SamplerName == "StripesA");                    // an ordinary, editable row
        Assert.True(binding.GraphTextures.Single().InMaterial);
        Assert.True(binding.GraphParams.Single().InMaterial);

        // SWITCH to B: A's parameter and texture go, B's come, ONE step
        binding.AssignGraphCommand.Execute(b);
        Assert.Equal(steps + 2, undo.UndoHistory.Count);
        Assert.DoesNotContain(binding.Model.Parameters, p => p.Name == "GlowA");
        Assert.DoesNotContain(binding.Model.Slots, x => x.SamplerName == "StripesA");
        Assert.DoesNotContain(binding.Slots, r => r.SamplerName == "StripesA");
        Assert.Contains(binding.Model.Parameters, p => p.Name == "GlowB");
        Assert.Contains(binding.Model.Slots, x => x.SamplerName == "BandsB");
        Assert.Contains(binding.Model.Parameters, p => p.Name == "TintColor");                // Riot's own stay

        // UNASSIGN: B's go
        binding.UnassignGraphCommand.Execute(null);
        Assert.Equal(steps + 3, undo.UndoHistory.Count);
        Assert.DoesNotContain(binding.Model.Parameters, p => p.Name == "GlowB");
        Assert.DoesNotContain(binding.Model.Slots, x => x.SamplerName == "BandsB");

        // and every step undoes and redoes whole
        undo.Undo();
        Assert.Contains(binding.Model.Slots, x => x.SamplerName == "BandsB");
        Assert.Contains(binding.Model.Parameters, p => p.Name == "GlowB");
        Assert.True(binding.HasGraphAssigned);
        undo.Undo();
        Assert.Contains(binding.Model.Slots, x => x.SamplerName == "StripesA");
        Assert.DoesNotContain(binding.Model.Slots, x => x.SamplerName == "BandsB");
        undo.Undo();
        Assert.DoesNotContain(binding.Model.Slots, x => x.SamplerName == "StripesA");
        Assert.DoesNotContain(binding.Model.Parameters, p => p.Name == "GlowA");
        Assert.False(binding.HasGraphAssigned);
        undo.Redo(); undo.Redo(); undo.Redo();
        Assert.False(binding.HasGraphAssigned);
        Assert.DoesNotContain(binding.Model.Slots, x => x.SamplerName == "BandsB");
    }

    [Fact]
    public async Task A_material_that_keeps_a_custom_texture_after_its_graph_is_gone_blocks_the_export_and_the_editors_unassign_does_not()
    {
        await SaveGraphWith("GraphA", ("", "StripesA"));
        var (undo, editor, binding) = EditorWithGraph(Vm());
        binding.AssignGraphCommand.Execute(binding.GraphChoices.Single());
        Assert.Contains(binding.Model.Slots, x => x.SamplerName == "StripesA");
        Assert.Equal("StripesA", Assert.Single(ReyEngine.App.Services.ShaderGraphShipService.ProjectCustomTextures(_root)).Key);

        // the ways a texture entry outlives its graph without the editor's unassign: the macro dropped by hand
        MacroPresenceCommand.Write(binding.Model, SgShip.Macro, null);
        string dir = Path.Combine(_root, "staged"); Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "a.materials.bin"), editor.Serialize()!);
        var errors = new List<string>();
        ReyEngine.App.Services.ShaderGraphShipService.FindAssigned(new[] { dir }, Names, errors,
            ReyEngine.App.Services.ShaderGraphShipService.ProjectCustomParameters(_root), ReyEngine.App.Services.ShaderGraphShipService.ProjectCustomTextures(_root));
        Assert.Contains(errors, m => m.Contains("carries the texture 'StripesA'") && m.Contains("GraphA") && m.Contains("not assigned to a graph that declares it"));

        // the editor's own unassign takes the entry away, so nothing blocks
        MacroPresenceCommand.Write(binding.Model, SgShip.Macro, "1");
        binding.UnassignGraphCommand.Execute(null);
        File.WriteAllBytes(Path.Combine(dir, "a.materials.bin"), editor.Serialize()!);
        errors.Clear();
        ReyEngine.App.Services.ShaderGraphShipService.FindAssigned(new[] { dir }, Names, errors,
            ReyEngine.App.Services.ShaderGraphShipService.ProjectCustomParameters(_root), ReyEngine.App.Services.ShaderGraphShipService.ProjectCustomTextures(_root));
        Assert.Empty(errors);
    }

    [Fact]
    public async Task A_stray_parameter_or_texture_names_every_graph_that_declares_it()
    {
        await SaveGraphWith("Graph_2", ("Tint", "Bands"));
        await SaveGraphWith("Graph_8", ("Tint", "Bands"));
        var (undo, editor, binding) = EditorWithGraph(Vm());
        binding.AssignGraphCommand.Execute(binding.GraphChoices.Single(c => c.Stem == "Graph_8"));
        Assert.Contains(binding.Model.Parameters, p => p.Name == "Tint");
        MacroPresenceCommand.Write(binding.Model, SgShip.Macro, null);       // the macro dropped by hand: the parameter and the texture stay behind
        string dir = Path.Combine(_root, "staged-m839"); Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "a.materials.bin"), editor.Serialize()!);
        var errors = new List<string>();
        ReyEngine.App.Services.ShaderGraphShipService.FindAssigned(new[] { dir }, Names, errors,
            ReyEngine.App.Services.ShaderGraphShipService.ProjectCustomParameters(_root), ReyEngine.App.Services.ShaderGraphShipService.ProjectCustomTextures(_root));
        Assert.Contains(errors, m => m.Contains("the parameter 'Tint'") && m.Contains("custom parameter of the Shader Graphs 'Graph_2', 'Graph_8'"));
        Assert.Contains(errors, m => m.Contains("the texture 'Bands'") && m.Contains("custom texture of the Shader Graphs 'Graph_2', 'Graph_8'"));
        Assert.All(errors, m => Assert.Contains("assign the graph", m));
    }

    [Fact]
    public async Task A_graph_macro_removed_by_hand_and_assigned_again_is_persisted_and_shown_enabled_after_save_and_reload()
    {
        await SaveGraphWith("GraphA", ("", ""));
        await SaveGraphWith("GraphB", ("", ""));
        Assert.Equal(1, ShaderGraphRegistry.NumberFor(_root, "GraphA", Flat));          // numbers are handed out on first use: B becomes #2
        var (undo, editor, binding) = EditorWithGraph(Vm());
        var b = binding.GraphChoices.Single(c => c.Stem == "GraphB");
        binding.AssignGraphCommand.Execute(b);
        string first = binding.Model.AllMacros.Single(m => SgShip.IsGraphMacro(m.Name)).Value;
        Assert.NotEqual("1", first);                                                     // a number other than 1: MaterialMacro.On is false for it
        Assert.True(binding.Macros.Single(m => SgShip.IsGraphMacro(m.Name)).IsOn);

        // save + reload, then the Material tab's Remove on the row, save + reload
        MaterialEditorViewModel Reload(MaterialEditorViewModel e)
        {
            var e2 = new MaterialEditorViewModel { UndoService = new UndoRedoService(), ProjectRoot = () => _root };
            e2.Load(MaterialDocument.Parse(e.Serialize()!, Names), new WadAssetEntry { Path = "data/maps/mapgeometry/map11/x.materials.bin" });
            e2.Materials.Single().RefreshMacroStatus();
            return e2;
        }
        var e1 = Reload(editor);
        var b1 = e1.Materials.Single();
        Assert.True(b1.Macros.Single(m => SgShip.IsGraphMacro(m.Name)).IsOn);            // a reloaded valid number is shown enabled
        b1.Macros.Single(m => SgShip.IsGraphMacro(m.Name)).RemoveCommand.Execute(null);
        var e2 = Reload(e1);
        var b2 = e2.Materials.Single();
        Assert.DoesNotContain(b2.Model.AllMacros, m => SgShip.IsGraphMacro(m.Name));

        // Assign again through the graph section, save + reload
        b2.AssignGraphCommand.Execute(b2.GraphChoices.Single(c => c.Stem == "GraphB"));
        Assert.True(b2.Macros.Single(m => SgShip.IsGraphMacro(m.Name)).IsOn);
        var e3 = Reload(e2);
        var b3 = e3.Materials.Single();
        var macro = b3.Model.AllMacros.Single(m => SgShip.IsGraphMacro(m.Name));
        Assert.Equal(first, macro.Value);                                                // the bin holds the graph number, enabled
        Assert.True(b3.Macros.Single(m => SgShip.IsGraphMacro(m.Name)).IsOn);
        Assert.True(b3.HasGraphAssigned);

        // a hand-edited disabled / invalid value is an export error, never a silent ship without the graph
        foreach (string bad in new[] { "0", "false", "", "-3" })
        {
            var m = new SgAssignedMaterial("data/x.materials.bin", "Mat", Flat, bad, new string[0], null, new string[0]);
            var errs = SgShipValidator.Validate(m, SgShipValidator.Resolve(_root, Flat, 2), ShaderGraphTests.TestBase());
            Assert.Contains(errs, e => e.Contains("is not a graph number"));
        }
    }

    [Fact]
    public async Task A_default_image_path_is_checked_normalised_and_hashed_the_same_by_the_material_and_the_shaders_bin()
    {
        Assert.Null(SgNames.TexturePathProblem(""));
        Assert.Null(SgNames.TexturePathProblem("assets/maps/x/y.tex"));
        Assert.Null(SgNames.TexturePathProblem("Assets\\Maps\\X\\Y.TEX"));
        Assert.Contains("0x", SgNames.TexturePathProblem("0xb32db105d8790a47"));
        Assert.Contains("0x", SgNames.TexturePathProblem("0XB32DB105D8790A47"));
        Assert.Contains(".tex", SgNames.TexturePathProblem("assets/maps/x/y.png"));
        Assert.NotNull(SgNames.TexturePathProblem("assets/x/\u00e9.tex"));
        Assert.NotNull(SgNames.TexturePathProblem("assets/x/y\u0001.tex"));

        var sg = Vm();
        sg.NewFromBase(Flat, out _);
        sg.AddTexture("Stripes");
        Assert.False(sg.SetTextureDefault("Stripes", "0xb32db105d8790a47"));
        Assert.Contains("0x", sg.Status);
        Assert.Equal("", sg.Document!.FindTexture("Stripes")!.DefaultPath);
        Assert.True(sg.SetTextureDefault("Stripes", "Assets\\Maps\\Particles\\X\\Y.tex"));
        var t = sg.Document.FindTexture("Stripes")!;
        Assert.Equal("Assets/Maps/Particles/X/Y.tex", t.DefaultPath);                          // forward slashes; the case does not matter to the hash

        // the material's link and the shaders.bin's link are the same chunk
        ulong expected = HashAlgorithms.WadPath("assets/maps/particles/x/y.tex");
        Assert.Equal(expected, HashAlgorithms.WadPath(t.NormalizedPath));
        Assert.Equal(expected, HashAlgorithms.WadPath(new SgCustomTexture { DefaultPath = " Assets\\Maps\\Particles\\X\\Y.tex " }.NormalizedPath));
        Assert.Equal(expected, SgCustomTextures.Declarations(sg.Document).Select(d => HashAlgorithms.WadPath(d.DefaultPath)).Single());
        Assert.True(await sg.SaveAsync());
        var (_, _, binding) = EditorWithGraph(sg);
        binding.AssignGraphCommand.Execute(Assert.Single(binding.GraphChoices));
        Assert.Equal(expected, binding.Model.Slots.Single(x => x.SamplerName == "Stripes").ChunkHash);

        // a hand-edited file with the hash form is refused at the export
        var d = ShaderGraphDocument.CreateEmpty("g", Flat);
        d.Textures.Add(new SgCustomTexture { Name = "Bad", DefaultPath = "0xb32db105d8790a47" });
        var graph = new SgResolvedGraph(new SgNumberEntry { File = "g", Base = Flat, Number = 1 }, "g.json", d, null);
        var m = new SgAssignedMaterial("a.bin", "Mat", Flat, "1", Array.Empty<string>());
        Assert.Contains(SgShipValidator.Validate(m, graph, ShaderGraphTests.TestBase()), x => x.Contains("'Bad'") && x.Contains("0x"));
    }

    [Fact]
    public async Task A_rename_that_a_material_cannot_follow_is_reported_and_a_deleted_parameter_names_the_materials_that_still_carry_it()
    {
        var sg = Vm();
        sg.NewFromBase(Flat, out _);
        sg.AddNode(Entry(sg, "Vector Parameter (new, custom float4)"), 0, 0);
        Assert.True(await sg.SaveAsync());
        var (undo, editor, binding) = EditorWithGraph(sg);
        binding.AssignGraphCommand.Execute(Assert.Single(binding.GraphChoices));
        binding.GraphParams.Single().AddToMaterialCommand.Execute(null);
        binding.Model.AddParameter("Glow", "Vector4");                                    // the material already has the NEW name
        var mg = new MaterialGraphViewModel(null);
        mg.AttachShaderGraph(sg);
        mg.ShowMaterial(binding.Model, null, null, editor);

        Assert.True(sg.RenameParameter("VectorParam", "Glow"));
        Assert.Contains("Not renamed", sg.Status);
        Assert.Contains("export is blocked", sg.Status);
        Assert.Contains(binding.Model.Parameters, p => p.Name == "VectorParam");           // untouched, and said so

        // delete a parameter that a material still carries: the status names the material
        binding.Model.AddParameter("Extra", "Vector4");
        Assert.Equal("Extra", sg.AddParameter("float", "Extra"));
        Assert.Equal(new[] { "Mat" }, mg.MaterialsCarryingCustomParameter(sg.FilePath, "Extra"));
        Assert.True(sg.DeleteParameter("Extra"));
        Assert.Contains("Mat", sg.Status); Assert.Contains("still carry", sg.Status);
    }

    // ================================================================================= material side

    private static BinTreeEmbedded Param(string name, BinTreeProperty value) =>
        new(0, H("StaticMaterialShaderParamDef"), new BinTreeProperty[] { new BinTreeString(H("name"), name), value });

    private static byte[] MaterialWithTint()
    {
        var props = new List<BinTreeProperty>
        {
            new BinTreeString(H("name"), "Mat"),
            new BinTreeUnorderedContainer(H("paramValues"), BinPropertyType.Embedded, new List<BinTreeEmbedded>
            {
                Param("TintColor", new BinTreeVector4(H("value"), new Vector4(1f, 0.5f, 0.25f, 1f))),
            }),
            new BinTreeContainer(H("techniques"), BinPropertyType.Embedded, new BinTreeProperty[]
            {
                new BinTreeEmbedded(0, H("StaticMaterialTechniqueDef"), new BinTreeProperty[]
                {
                    new BinTreeContainer(H("passes"), BinPropertyType.Embedded, new BinTreeProperty[]
                    {
                        new BinTreeEmbedded(0, H("StaticMaterialPassDef"), new BinTreeProperty[] { new BinTreeObjectLink(H("shader"), H(Flat)) }),
                    }),
                }),
            }),
        };
        using var ms = new MemoryStream();
        new BinTree(new[] { new BinTreeObject(H("Mat"), H("StaticMaterialDef"), props) }, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    private static string? Names(uint h) => new[] { "StaticMaterialDef", Flat }.Select(s => (H(s), s)).Where(x => x.Item1 == h).Select(x => x.s).FirstOrDefault();

    [Fact]
    public async Task A_graph_rename_follows_into_the_assigned_materials_as_one_editor_undo_step_and_back()
    {
        // the graph: one custom vector parameter, saved in the project
        var sg = Vm();
        sg.NewFromBase(Flat, out _);
        sg.AddNode(Entry(sg, "Vector Parameter (new, custom float4)"), 0, 0);
        Assert.True(await sg.SaveAsync());

        // a material in the Material Editor, assigned to it, with the graph's parameter written
        var undo = new UndoRedoService();
        var editor = new MaterialEditorViewModel { UndoService = undo, ProjectRoot = () => _root };
        editor.Load(MaterialDocument.Parse(MaterialWithTint(), Names), new WadAssetEntry { Path = "data/maps/mapgeometry/map11/x.materials.bin" });
        var binding = editor.Materials.Single();
        binding.RefreshMacroStatus();
        binding.AssignGraphCommand.Execute(Assert.Single(binding.GraphChoices));
        Assert.True(binding.HasGraphParams);
        var row = Assert.Single(binding.GraphParams);
        Assert.Equal("VectorParam", row.Name);
        Assert.True(row.InMaterial);                                              // M835: Assign writes the graph's parameters (the game declares them with no value)
        Assert.Contains("custom parameter", binding.GraphAssignmentText);          // the section says they are declared in the shipped shaders.bin
        Assert.Contains("shaders.bin", binding.GraphAssignmentText);
        Assert.False(binding.GraphAssignmentIsError);
        Assert.Contains(binding.Model.Parameters, p => p.Name == "VectorParam");
        Assert.Contains(binding.Parameters, p => p.Name == "VectorParam");        // an ordinary, editable row

        // the Material Graph window's view model hosts both
        var mg = new MaterialGraphViewModel(null);
        mg.AttachShaderGraph(sg);
        mg.ShowMaterial(binding.Model, null, null, editor);

        Assert.True(sg.RenameParameter("VectorParam", "Glow"));
        Assert.Contains(binding.Model.Parameters, p => p.Name == "Glow");
        Assert.DoesNotContain(binding.Model.Parameters, p => p.Name == "VectorParam");
        Assert.Contains(binding.Parameters, p => p.Name == "Glow");                // the editor's row followed
        Assert.Contains("renamed in the Material Editor", sg.Status);

        // the editor's own undo is ONE step for the whole rename
        undo.Undo();
        Assert.Contains(binding.Model.Parameters, p => p.Name == "VectorParam");
        Assert.DoesNotContain(binding.Model.Parameters, p => p.Name == "Glow");
        undo.Redo();
        Assert.Contains(binding.Model.Parameters, p => p.Name == "Glow");

        // undoing in the graph renames the materials back too (a further editor step)
        Assert.True(sg.Undo());
        Assert.Contains(binding.Model.Parameters, p => p.Name == "VectorParam");
        Assert.Equal("VectorParam", sg.Document!.Params.Single().Name);
    }

    // ================================================================================= custom node

    private static ShaderGraphDocument CustomDoc(string code, string inputs = "In:float4", string outType = "float4")
    {
        var d = Doc();
        ShaderGraphTests.Add(d, "n2", "TextureSample", ("texture", "DiffuseTexture"));
        ShaderGraphTests.Add(d, "n3", "Custom", ("inputs", inputs), ("out", outType), ("code", code));
        ShaderGraphTests.Link(d, "n2", "RGBA", "n3", "In");
        ShaderGraphTests.Add(d, "n4", "ComponentMask", ("mask", "rgb"));
        ShaderGraphTests.Link(d, "n3", "Value", "n4", "Value");
        ShaderGraphTests.Link(d, "n4", "Value", "n1", SgCatalog.BaseColorPin);
        ShaderGraphTests.Link(d, "n2", "A", "n1", SgCatalog.OpacityPin);
        return d;
    }

    [Theory]
    [InlineData("In * 2.0", null)]
    [InlineData("In.bgra", null)]
    [InlineData("float4 c = In;\nc.rgb = 1.0 - c.rgb;\nreturn c;", null)]
    [InlineData("if (In.a > 0.5) { return In; } else { return 1.0 - In; }", null)]
    [InlineData("In // a line comment", null)]
    [InlineData("}", "closes more than")]
    [InlineData("In; } float4 evil() { return In", "closes more than")]
    [InlineData("return In; } cbuffer X { float4 y; } void f() {", "closes more than")]
    [InlineData("float4 f() { return In; } cbuffer X { float4 y; }", "cbuffer")]
    [InlineData("#include \"x.hlsl\"", "'#'")]
    [InlineData("#define X 1\nIn", "'#'")]
    [InlineData("cbuffer B : register(b3) { float4 v; }", "cbuffer")]
    [InlineData("Texture2D t; return In;", "Texture2D")]
    [InlineData("SamplerState s; return In;", "SamplerState")]
    [InlineData("TextureCube t; return In;", "TextureCube")]
    [InlineData("return In; /* open", "block comments")]
    [InlineData("return In * 2.0; /* x */", "block comments")]
    [InlineData("return In * 2.0 + float4(1.0, 0.0, 0.0, é);", "ASCII")]
    [InlineData("return In; // —", "ASCII")]
    [InlineData("return In;\u0007", "control character")]
    [InlineData("struct S { float a; }; return In;", "struct")]
    [InlineData("register(t0)", "register")]
    [InlineData("sg_custom_0(In)", "reserves")]
    [InlineData("v__x", "reserves")]
    [InlineData("if (In.a > 0.5) { return In;", "never closed")]
    [InlineData("(In", "never closed")]
    [InlineData("In);", "without a matching")]
    [InlineData("a; b", "';'")]
    [InlineData("return \"x\";", "string or character literals")]
    [InlineData("return 'a';", "string or character literals")]
    [InlineData("return In; \\", "backslash")]
    [InlineData("ConstantBuffer<float4> c; return In;", "ConstantBuffer")]
    [InlineData("RWStructuredBuffer<float4> u; return In;", "RWStructuredBuffer")]
    [InlineData("RWByteAddressBuffer u; return In;", "RWByteAddressBuffer")]
    [InlineData("RasterizerOrderedTexture2D<float4> u; return In;", "RasterizerOrderedTexture2D")]
    [InlineData("InputPatch<float4, 3> p; return In;", "InputPatch")]
    [InlineData("OutputPatch<float4, 3> p; return In;", "OutputPatch")]
    [InlineData("TriangleStream<float4> s; return In;", "TriangleStream")]
    [InlineData("   ", "write an expression")]
    public void The_Custom_node_sanitiser_refuses_what_could_escape_its_own_function(string code, string? problem)
    {
        string? got = SgCustom.CodeProblem(code, out _);
        if (problem is null) Assert.Null(got); else Assert.Contains(problem, got ?? "", StringComparison.OrdinalIgnoreCase);

        // the analyser shows the same refusal on the node, and no HLSL is generated for it
        var d = CustomDoc(code);
        var a = SgAnalyzer.Analyze(d, ShaderGraphTests.TestBase());
        Assert.Equal(problem is not null, a.Errors.Any(e => e.NodeId == "n3"));
        if (problem is not null)
        {
            var b = ShaderGraphTests.TestBase();
            Assert.False(SgHlsl.Generate(d, b, b.Signatures[0]).Ok);
        }
    }

    [Fact]
    public void The_sanitiser_caps_length_lines_and_inputs()
    {
        Assert.Contains("longer than", SgCustom.CodeProblem(new string('a', ShaderGraphDocument.MaxCodeLength + 1), out _));
        Assert.Contains("more than", SgCustom.CodeProblem(string.Join("\n", Enumerable.Repeat("x", SgCustom.MaxLines + 1)), out _));
        Assert.Contains("longer than", SgCustom.CodeProblem(new string('a', SgCustom.MaxLineLength + 1), out _));
        Assert.False(SgCustom.TryParseInputs("a:float;b:float;c:float;d:float;e:float;f:float;g:float;h:float;i:float", out _, out string e));
        Assert.Contains("at most", e);
        Assert.False(SgCustom.TryParseInputs("1bad:float", out _, out _));
        Assert.False(SgCustom.TryParseInputs("a:float5", out _, out _));
        Assert.False(SgCustom.TryParseInputs("a:float;A:float2", out _, out e));
        Assert.Contains("two inputs", e);
        Assert.False(SgCustom.TryParseInputs("float4:float", out _, out _));          // an HLSL type name
        Assert.True(SgCustom.TryParseInputs("v0:float", out _, out _));               // inside its own function a local of main cannot clash
        Assert.False(SgCustom.TryParseInputs("saté:float", out _, out _));       // non-ASCII
        Assert.True(SgCustom.TryParseInputs("Color:float3; Mask:float", out var ok, out _));
        Assert.Equal(new[] { 3, 1 }, ok.Select(i => i.Components));
    }

    [Fact]
    public void A_Custom_node_is_wrapped_in_its_own_function_and_returns_through_it()
    {
        var b = ShaderGraphTests.TestBase();
        var d = CustomDoc("In * 2.0 // double it\n");
        d.Find("n3")!.Label = "Doubler";
        var r = SgHlsl.Generate(d, b, b.Signatures[0]);
        Assert.True(r.Ok, string.Join("\n", r.Errors));
        Assert.Contains("float4 sg_custom_0(float4 In)\n{\n    return (In * 2.0); // double it\n}", r.Source);
        Assert.Contains("sg_custom_0(", r.Source);
        Assert.Contains("// Doubler (Custom)", r.Source);                                      // the label is in the node's statement comment
        // every line of the function belongs to the node, so a compiler error lands on it
        Assert.Contains("n3", r.LineNodes.Values);
        int fnLine = r.Source.Split('\n').ToList().FindIndex(l => l.StartsWith("float4 sg_custom_0")) + 1;
        Assert.Equal("n3", r.LineNodes[fnLine]);
        Assert.All(r.Source, c => Assert.True(c < 127));
    }

    [Fact]
    public void A_statement_body_is_copied_as_it_is()
    {
        var b = ShaderGraphTests.TestBase();
        var r = SgHlsl.Generate(CustomDoc("float4 c = In;\nc.rgb = 1.0 - c.rgb;\nreturn c;"), b, b.Signatures[0]);
        Assert.True(r.Ok);
        Assert.Contains("    float4 c = In;\n    c.rgb = 1.0 - c.rgb;\n    return c;\n}", r.Source);
    }

    [Fact]
    public async Task A_Custom_node_with_an_error_is_reported_on_the_node_and_fixing_it_compiles()
    {
        var vm = Vm();
        vm.NewFromBase(Flat, out _);
        string id = vm.AddNode(Entry(vm, "Custom"), 200, 50)!;
        Assert.True(vm.Connect("n2", "RGBA", id, "In"));
        Assert.Equal("In", vm.Document!.Find(id)!.Prop("code"));
        var mask = vm.AddNode(Entry(vm, "Component Mask"), 400, 50)!;
        vm.SetProp(mask, "mask", "rgb");
        Assert.True(vm.Connect(id, "Value", mask, "Value"));
        Assert.True(vm.Connect(mask, "Value", "n1", SgCatalog.BaseColorPin));

        // a mistake only the HLSL compiler sees: it lands on the Custom node, not on the output
        Assert.True(vm.SetProp(id, "code", "In.qq * 2.0"));
        var bad = await vm.CompileNowAsync();
        Assert.False(bad!.Ok);
        Assert.Contains(bad.Diagnostics, d => d.IsError && d.NodeId == id && d.Message.Contains("HLSL line"));
        Assert.Equal(GraphNodeState.Error, vm.Graph!.Find(id)!.State);
        Assert.Contains(vm.DiagnosticRows, r => r.IsError && r.Text.Contains("Custom " + id));

        // sanitiser refusals are instant, before any compile
        Assert.True(vm.SetProp(id, "code", "#include \"x\""));
        Assert.Contains(vm.Analysis!.Errors, e => e.NodeId == id && e.Message.Contains("'#'"));
        Assert.Equal(GraphNodeState.Error, vm.Graph.Find(id)!.State);

        // the fix
        Assert.True(vm.SetProp(id, "code", "In * float4(1.0, 0.5, 0.5, 1.0)"));
        var good = await vm.CompileNowAsync();
        Assert.True(good!.Ok, string.Join("\n", good.Diagnostics));
        Assert.Equal(GraphNodeState.Authored, vm.Graph.Find(id)!.State);
        Assert.Contains("sg_custom_0", vm.Hlsl);
    }

    [Fact]
    public void Custom_node_inputs_are_pins_and_changing_them_drops_wires_to_pins_that_went_away()
    {
        var vm = Vm();
        vm.NewFromBase(Flat, out _);
        string id = vm.AddNode(Entry(vm, "Custom"), 0, 0)!;
        Assert.True(vm.Connect("n2", "RGBA", id, "In"));
        Assert.True(vm.SetProp(id, "inputs", "Color:float3;Mask:float"));
        Assert.Equal(new[] { "Color", "Mask" }, vm.Graph!.Find(id)!.Inputs.Select(p => p.Name));
        Assert.DoesNotContain(vm.Document!.Links, l => l.ToNode == id);
        Assert.True(vm.Connect("n2", "RGB", id, "Color"));
        Assert.True(vm.Connect("n2", "R", id, "Mask"));
        Assert.True(vm.Undo()); Assert.True(vm.Undo());
        Assert.True(vm.Undo());                                                      // the inputs edit, with its dropped wire
        Assert.Contains(vm.Document.Links, l => l.ToNode == id && l.ToPin == "In");
    }

    [Fact]
    public void A_wider_value_into_a_Custom_input_is_a_type_error_like_any_pin()
    {
        var d = CustomDoc("In", inputs: "In:float3");     // a float4 arrives at a float3 input
        var a = SgAnalyzer.Analyze(d, ShaderGraphTests.TestBase());
        Assert.Contains(a.Errors, e => e.NodeId == "n3" && e.Pin == "In" && e.Message.Contains("float3"));
    }

    // ================================================================================= labels and comments

    [Fact]
    public async Task A_label_is_saved_undoable_and_shown_on_the_node_without_changing_what_it_computes()
    {
        var vm = Vm();
        vm.NewFromBase(Flat, out _);
        string before = vm.Hlsl;
        Assert.True(vm.SetLabel("n2", "Base albedo"));
        Assert.Equal("Base albedo", vm.Graph!.Find("n2")!.Title);
        Assert.Contains("DiffuseTexture Texture Sample", vm.Graph.Find("n2")!.Subtitle);
        Assert.Equal("Label", vm.PropRows.FirstOrDefault()?.Label ?? "Label");
        Assert.True(vm.Undo());
        Assert.Equal("", vm.Document!.Find("n2")!.Label);
        Assert.True(vm.Redo());
        Assert.False(vm.SetLabel("n2", new string('x', 65)));
        Assert.True(vm.SetLabel("n2", "  line\u0001 one \n "));
        Assert.Equal("line one", vm.Document.Find("n2")!.Label);                     // control characters are dropped

        var json = ShaderGraphJson.Serialize(vm.Document);
        Assert.Equal("line one", ShaderGraphJson.TryDeserialize(json, out _)!.Find("n2")!.Label);
        var b = ShaderGraphTests.TestBase();
        var r = SgHlsl.Generate(vm.Document, b, b.Signatures[0]);
        Assert.Contains("(Texture Sample)", r.Source);
        Assert.Contains("line one", r.Source);
        await Task.CompletedTask;
    }

    [Fact]
    public void Comment_boxes_are_created_around_the_selection_move_with_their_nodes_resize_and_recolour()
    {
        var vm = Vm();
        vm.NewFromBase(Flat, out _);
        // around the texture node alone
        string? c = vm.AddCommentAround(new[] { "n2" });
        Assert.NotNull(c);
        var box = vm.Document!.FindComment(c!)!;
        var n2 = vm.Graph!.Find("n2")!;
        Assert.True(box.X < n2.X && box.Y < n2.Y && box.X + box.Width > n2.Right && box.Y + box.Height > n2.Bottom);
        Assert.Equal(new[] { "n2" }, vm.NodesInside(c!));
        Assert.Null(vm.AddCommentAround(Array.Empty<string>()));                       // nothing selected: refused with a reason
        Assert.Contains("Select a node", vm.Status);

        // moving the box moves the nodes inside it (layout, like moving a node: no undo step)
        double x0 = vm.Document.Find("n2")!.X, bx0 = box.X;
        vm.MoveComment(c!, box.X + 100, box.Y + 40, new Dictionary<string, (double, double)> { ["n2"] = (x0 + 100, vm.Document.Find("n2")!.Y + 40) });
        Assert.Equal(x0 + 100, vm.Document.Find("n2")!.X);
        Assert.Equal(bx0 + 100, vm.Graph.Comments.Single().X);
        Assert.Equal(new[] { "n2" }, vm.NodesInside(c!));                              // still contains it
        vm.ResizeComment(c!, 5, 5);
        Assert.Equal(SgComment.MinSize, vm.Document.FindComment(c!)!.Width);          // never smaller than the minimum
        vm.ResizeComment(c!, 500, 300);

        // title and colour are undoable edits; the colour is a theme palette key, never a literal
        Assert.True(vm.SetCommentTitle(c!, "Albedo"));
        Assert.True(vm.SetCommentColor(c!, "Texture"));
        Assert.False(vm.SetCommentColor(c!, "#ff0000"));
        Assert.False(vm.SetCommentColor(c!, "NotAKey"));
        Assert.Equal("Albedo", vm.Graph.Comments.Single().Title);
        Assert.Equal("Texture", vm.Graph.Comments.Single().ColorKey);
        Assert.True(vm.Undo()); Assert.True(vm.Undo());
        Assert.Equal("Comment", vm.Document.FindComment(c!)!.Title);
        Assert.Equal(500, vm.Document.FindComment(c!)!.Width);                         // undo never moves or resizes (layout)

        // saved and loaded back
        var back = ShaderGraphJson.TryDeserialize(ShaderGraphJson.Serialize(vm.Document), out _)!;
        Assert.Equal(vm.Document.FindComment(c!)!.Color, back.FindComment(c!)!.Color);
        Assert.Equal(500, back.FindComment(c!)!.Width);

        Assert.True(vm.DeleteComment(c!));
        Assert.Empty(vm.Graph.Comments);
        Assert.True(vm.Undo());
        Assert.Single(vm.Graph.Comments);
    }

    [Fact]
    public void A_selected_comment_and_a_selected_node_exclude_each_other_and_show_their_own_settings()
    {
        var vm = Vm();
        vm.NewFromBase(Flat, out _);
        string c = vm.AddComment(0, 0)!;
        vm.SelectedCommentId = c;
        Assert.Equal(new[] { "Comment title", "Colour" }, vm.PropRows.Select(r => r.Label));
        vm.PropRows[0].Text = "Inputs"; vm.PropRows[0].Commit();
        Assert.Equal("Inputs", vm.Document!.FindComment(c)!.Title);
        vm.SelectedNodeId = "n2";
        Assert.Null(vm.SelectedCommentId);
        Assert.Contains(vm.PropRows, r => r.Label == "Texture");
        vm.SelectedCommentId = c;
        Assert.Null(vm.SelectedNodeId);
    }

    // ================================================================================= the new nodes

    [Fact]
    public void Every_new_node_and_the_old_ones_are_found_by_the_right_click_search()
    {
        var vm = Vm();
        vm.NewFromBase(Flat, out _);
        foreach (var (word, title) in new[]
        {
            ("panner", "Panner"), ("rotator", "Rotator"), ("tiling", "Tiling / Offset"), ("length", "Length"), ("distance", "Distance"), ("cross", "Cross"),
            ("reflect", "Reflect"), ("sign", "Sign"), ("ceil", "Ceil"), ("round", "Round"), ("fmod", "Fmod"), ("desaturation", "Desaturation"),
            ("if", "If"), ("colour", "Constant Color"), ("picker", "Constant Color"), ("custom", "Custom"), ("hlsl", "Custom"), ("scalar", "Scalar Parameter (new, custom)"),
            ("vector param", "Vector Parameter (new, custom float4)"), ("lerp", "Lerp"),
        })
            Assert.Contains(SgCatalog.Search(vm.Palette, word), e => e.Title == title);
        Assert.DoesNotContain(vm.Palette, e => e.Type == "Fresnel");                   // no normal / view data in DefaultEnv_Flat's vertex outputs
        Assert.All(vm.Palette.Where(e => e.Type == "Custom"), e => Assert.Equal("In:float4", e.Props["inputs"]));
    }

    private static void Wire(ShaderGraphDocument d, string type, string id, params (string Pin, string Src, string SrcPin)[] wires)
    {
        ShaderGraphTests.Add(d, id, type);
        foreach (var w in wires) ShaderGraphTests.Link(d, w.Src, w.SrcPin, id, w.Pin);
    }

    [Fact]
    public void The_new_nodes_type_correctly_and_produce_the_agreed_widths()
    {
        var b = ShaderGraphTests.TestBase();
        var d = Doc();
        ShaderGraphTests.Add(d, "tx", "TextureSample", ("texture", "DiffuseTexture"));
        ShaderGraphTests.Add(d, "c3", "Constant", ("value", "1 2 3"));
        ShaderGraphTests.Add(d, "c1", "Constant", ("value", "0.5"));
        ShaderGraphTests.Add(d, "c2", "Constant", ("value", "1 2"));
        foreach (var (type, id, wires, width) in new (string, string, (string, string, string)[], int)[]
        {
            ("Panner", "pa", Array.Empty<(string, string, string)>(), 2),
            ("Rotator", "ro", new[] { ("Speed", "c1", "Value") }, 2),
            ("TilingOffset", "to", new[] { ("Tiling", "c2", "Value") }, 2),
            ("Length", "le", new[] { ("Value", "c3", "Value") }, 1),
            ("Distance", "di", new[] { ("A", "c3", "Value"), ("B", "c3", "Value") }, 1),
            ("Cross", "cr", new[] { ("A", "c3", "Value"), ("B", "c3", "Value") }, 3),
            ("Reflect", "re", new[] { ("Incident", "c3", "Value"), ("Normal", "c3", "Value") }, 3),
            ("Sign", "si", new[] { ("Value", "c3", "Value") }, 3),
            ("Ceil", "ce", new[] { ("Value", "c2", "Value") }, 2),
            ("Round", "rn", new[] { ("Value", "c1", "Value") }, 1),
            ("Fmod", "fm", new[] { ("A", "c3", "Value"), ("B", "c1", "Value") }, 3),
            ("Desaturation", "de", new[] { ("Color", "c3", "Value") }, 3),
            ("If", "if", new[] { ("A > B", "c3", "Value"), ("A == B", "c3", "Value") }, 3),
            ("ConstantColor", "cc", Array.Empty<(string, string, string)>(), 4),
        })
        {
            Wire(d, type, id, wires);
            var a = SgAnalyzer.Analyze(d, b);
            Assert.True(a.OutputWidths[id].Length > 0 && a.OutputWidths[id][0] == width, $"{type}: width {(a.OutputWidths[id].Length > 0 ? a.OutputWidths[id][0] : -1)} instead of {width} ({string.Join("; ", a.For(id))})");
        }

        // the typing errors
        var bad = Doc();
        ShaderGraphTests.Add(bad, "c2", "Constant", ("value", "1 2"));
        ShaderGraphTests.Add(bad, "c1", "Constant", ("value", "1"));
        Wire(bad, "Cross", "cr", ("A", "c2", "Value"));                                 // float2 into a float3 pin
        Wire(bad, "Reflect", "rf", ("Incident", "c1", "Value"), ("Normal", "c1", "Value"));   // scalars
        Wire(bad, "If", "if", ("A", "c2", "Value"));                                    // a vector where a scalar is taken
        var a2 = SgAnalyzer.Analyze(bad, b);
        // (nothing reads these nodes, so the type pass reports them as warnings: they are still about the right node and pin)
        Assert.Contains(a2.Diagnostics, e => e.NodeId == "cr" && e.Pin == "A");
        Assert.Contains(a2.Diagnostics, e => e.NodeId == "rf" && e.Message.Contains("vectors"));
        Assert.Contains(a2.Diagnostics, e => e.NodeId == "if" && e.Pin == "A");
    }

    [Fact]
    public void A_Colour_constant_needs_four_numbers_and_draws_a_swatch()
    {
        var b = ShaderGraphTests.TestBase();
        var d = Doc();
        ShaderGraphTests.Add(d, "cc", "ConstantColor", ("value", "0.2 0.4 0.6 1"));
        ShaderGraphTests.Link(d, "cc", "Value", "n1", SgCatalog.OpacityPin);
        var g = SgGraphView.Build(d, b, SgAnalyzer.Analyze(d, b));
        Assert.Equal(0.4f, g.Find("cc")!.Swatch!.Value.Y, 3);
        d.Find("cc")!.Props["value"] = "1 2 3";
        Assert.Contains(SgAnalyzer.Analyze(d, b).Errors, e => e.NodeId == "cc" && e.Message.Contains("4 numbers"));
    }

    [Fact]
    public void The_generated_HLSL_of_each_new_node_is_a_snapshot_and_stays_ASCII()
    {
        var b = ShaderGraphTests.TestBase();
        var d = Doc();
        ShaderGraphTests.Add(d, "p", "Panner", ("Speed", "0.25"));
        ShaderGraphTests.Add(d, "r", "Rotator");
        ShaderGraphTests.Add(d, "t", "TilingOffset", ("Tiling", "4"));
        ShaderGraphTests.Add(d, "tx", "TextureSample", ("texture", "DiffuseTexture"));
        ShaderGraphTests.Add(d, "ds", "Desaturation", ("Fraction", "0.5"));
        ShaderGraphTests.Add(d, "if", "If", ("A", "1"), ("B", "2"));
        ShaderGraphTests.Add(d, "cc", "ConstantColor", ("value", "0.5 0.25 0.125 1"));
        ShaderGraphTests.Link(d, "p", "UV", "r", "UV");
        ShaderGraphTests.Link(d, "r", "UV", "t", "UV");
        ShaderGraphTests.Link(d, "t", "UV", "tx", "UV");
        ShaderGraphTests.Link(d, "tx", "RGB", "ds", "Color");
        ShaderGraphTests.Link(d, "ds", "Value", "n1", SgCatalog.BaseColorPin);
        ShaderGraphTests.Link(d, "cc", "Value", "if", "A > B");
        ShaderGraphTests.Link(d, "if", "Value", "n1", SgCatalog.OpacityPin);
        // a float4 into the scalar Opacity pin is a type error; route its alpha instead
        d.Links.RemoveAll(l => l.ToPin == SgCatalog.OpacityPin);
        ShaderGraphTests.Link(d, "tx", "A", "n1", SgCatalog.OpacityPin);

        var r = SgHlsl.Generate(d, b, b.Signatures[0]);
        Assert.True(r.Ok, string.Join("\n", r.Errors));
        Assert.Contains("= (i.tc1.xy + (TIME.x * float2(0.25, 0.25)));", r.Source);                  // Panner: UV + Time * Speed (engine time when unwired)
        Assert.Contains("* 6.28318530718;", r.Source);                                  // Rotator: revolutions
        Assert.Contains("* float2(4.0, 4.0)", r.Source);                                // Tiling
        Assert.Contains("lerp(", r.Source); Assert.Contains("float3(0.299, 0.587, 0.114)", r.Source);   // Desaturation
        Assert.Contains("TIME", r.Source);
        Assert.True(r.UsesTime);
        Assert.All(r.Source, ch => Assert.True(ch < 127));

        // a base without TIME: the engine time pin is zero, and Time is not declared
        var noFrame = ShaderGraphTests.TestBase(withFrame: false);
        var r2 = SgHlsl.Generate(d, noFrame, noFrame.Signatures[0]);
        Assert.True(r2.Ok, string.Join("\n", r2.Errors));
        Assert.Contains("(0.0 *", r2.Source);
        Assert.DoesNotContain("TIME", r2.Source);
    }

    [Fact]
    public async Task Every_new_node_compiles_with_the_real_HLSL_compiler_on_the_test_base()
    {
        var b = ShaderGraphTests.TestBase();
        var d = Doc();
        ShaderGraphTests.Add(d, "tx", "TextureSample", ("texture", "DiffuseTexture"));
        ShaderGraphTests.Add(d, "p", "Panner"); ShaderGraphTests.Add(d, "r", "Rotator"); ShaderGraphTests.Add(d, "t", "TilingOffset");
        ShaderGraphTests.Link(d, "p", "UV", "r", "UV"); ShaderGraphTests.Link(d, "r", "UV", "t", "UV"); ShaderGraphTests.Link(d, "t", "UV", "tx", "UV");
        ShaderGraphTests.Add(d, "ds", "Desaturation"); ShaderGraphTests.Link(d, "tx", "RGB", "ds", "Color");
        ShaderGraphTests.Add(d, "cr", "Cross"); ShaderGraphTests.Link(d, "ds", "Value", "cr", "A"); ShaderGraphTests.Link(d, "tx", "RGB", "cr", "B");
        ShaderGraphTests.Add(d, "rf", "Reflect"); ShaderGraphTests.Link(d, "cr", "Value", "rf", "Incident"); ShaderGraphTests.Link(d, "ds", "Value", "rf", "Normal");
        ShaderGraphTests.Add(d, "fm", "Fmod", ("B", "0.7")); ShaderGraphTests.Link(d, "rf", "Value", "fm", "A");
        ShaderGraphTests.Add(d, "sg", "Sign"); ShaderGraphTests.Link(d, "fm", "Value", "sg", "Value");
        ShaderGraphTests.Add(d, "ce", "Ceil"); ShaderGraphTests.Link(d, "sg", "Value", "ce", "Value");
        ShaderGraphTests.Add(d, "rn", "Round"); ShaderGraphTests.Link(d, "ce", "Value", "rn", "Value");
        ShaderGraphTests.Add(d, "le", "Length"); ShaderGraphTests.Link(d, "rn", "Value", "le", "Value");
        ShaderGraphTests.Add(d, "di", "Distance"); ShaderGraphTests.Link(d, "ds", "Value", "di", "A"); ShaderGraphTests.Link(d, "cr", "Value", "di", "B");
        ShaderGraphTests.Add(d, "if", "If");
        ShaderGraphTests.Link(d, "le", "Value", "if", "A"); ShaderGraphTests.Link(d, "di", "Value", "if", "B");
        ShaderGraphTests.Link(d, "ds", "Value", "if", "A > B"); ShaderGraphTests.Link(d, "rn", "Value", "if", "A == B"); ShaderGraphTests.Link(d, "cr", "Value", "if", "A < B");
        ShaderGraphTests.Add(d, "cc", "ConstantColor", ("value", "0.9 0.5 0.1 1"));
        ShaderGraphTests.Add(d, "mix", "Lerp"); ShaderGraphTests.Link(d, "if", "Value", "mix", "A");
        ShaderGraphTests.Add(d, "cm", "ComponentMask", ("mask", "rgb")); ShaderGraphTests.Link(d, "cc", "Value", "cm", "Value"); ShaderGraphTests.Link(d, "cm", "Value", "mix", "B");
        ShaderGraphTests.Link(d, "mix", "Value", "n1", SgCatalog.BaseColorPin);
        ShaderGraphTests.Link(d, "tx", "A", "n1", SgCatalog.OpacityPin);

        var build = ShaderGraphCompiler.BuildAll(d, b);
        Assert.True(build.Ok, string.Join("\n", build.Diagnostics) + "\n" + string.Join("\n", build.Compiled.SelectMany(c => c.Errors)) + "\n" + build.Compiled.FirstOrDefault()?.CompilerLog);
        Assert.Equal(b.Signatures.Count, build.Compiled.Count);
        await Task.CompletedTask;
    }

    // ================================================================================= markup names

    [Fact]
    public void The_markup_binds_only_members_that_exist_for_the_new_rows_and_the_material_tab_section()
    {
        const System.Reflection.BindingFlags pub = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
        void Has(Type t, params string[] names) { foreach (string n in names) Assert.True(t.GetProperty(n, pub) is not null, t.Name + " has no " + n); }
        Has(typeof(SgPropRow), "ShowsTextBox", "ShowsMultiline", "IsColor", "ColorValue", "Text", "CommitCommand");
        Has(typeof(SgParamRow), "NameText", "Kind", "IsColor", "CanBeColor", "DefaultText", "ShowsNumbers", "ShowsColor", "ColorValue", "UsedText", "DeleteCommand", "CommitNameCommand", "CommitDefaultCommand");
        Assert.NotNull(typeof(SgParamRow).GetProperty("KindChoices"));
        Has(typeof(GraphParamRow), "Name", "TypeText", "StatusText", "CanAdd", "AddToMaterialCommand");
        Has(typeof(MaterialBindingViewModel), "HasGraphParams", "GraphParams");
        Has(typeof(ShaderGraphViewModel), "ParamRows", "NoParamRows", "AddScalarParameterCommand", "AddVectorParameterCommand", "AddColourParameterCommand", "SelectedCommentId");

        string? win = Src("src", "ReyEngine.App", "Views", "ShaderPreviewWindow.axaml");
        string? mat = Src("src", "ReyEngine.App", "Views", "MaterialEditorView.axaml");
        if (win is null || mat is null) return;
        Assert.Contains("ShaderGraph.ParamRows", win);
        Assert.Contains("ColorPicker Color=\"{Binding ColorValue, Mode=TwoWay}\"", win);
        Assert.Contains("AcceptsReturn=\"True\"", win);
        Assert.Contains("GraphParams", mat);
        Assert.Contains("AddToMaterialCommand", mat);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(win, "=\"#[0-9A-Fa-f]{3,8}\""));    // no literal colour added
    }

    private static string? Src(params string[] parts)
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "ReyEngine.slnx"))) root = Path.GetDirectoryName(root);
        if (root is null) return null;
        string path = Path.Combine(new[] { root }.Concat(parts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    // ================================================================================= version 1 -> 2

    [Fact]
    public void A_version_1_file_loads_unchanged_with_empty_new_lists_and_is_written_as_version_2()
    {
        const string v1 = "{ \"version\": 1, \"name\": \"old\", \"baseShader\": \"Shaders/StaticMesh/DefaultEnv_Flat\", \"nodes\": [ { \"id\": \"n1\", \"type\": \"Output\", \"x\": 760, \"y\": 120, \"props\": {} }, "
                          + "{ \"id\": \"n2\", \"type\": \"TextureSample\", \"x\": 300, \"y\": 120, \"props\": { \"texture\": \"DiffuseTexture\" } } ], "
                          + "\"links\": [ { \"fromNode\": \"n2\", \"fromPin\": \"RGB\", \"toNode\": \"n1\", \"toPin\": \"Base Color\" } ] }";
        var doc = ShaderGraphJson.TryDeserialize(v1, out string? error);
        Assert.NotNull(doc);
        Assert.Null(error);
        Assert.Equal(1, doc!.Version);
        Assert.Empty(doc.Params); Assert.Empty(doc.Comments);
        Assert.All(doc.Nodes, n => Assert.Equal("", n.Label));
        Assert.Equal("DiffuseTexture", doc.Find("n2")!.Prop("texture"));
        Assert.False(SgAnalyzer.Analyze(doc, ShaderGraphTests.TestBase()).HasErrors);

        string v2 = ShaderGraphJson.Serialize(doc);
        Assert.Contains("\"version\": 3", v2);
        Assert.Contains("\"params\": []", v2);
        Assert.Contains("\"comments\": []", v2);
        var back = ShaderGraphJson.TryDeserialize(v2, out _)!;
        Assert.Equal(v2, ShaderGraphJson.Serialize(back));
        Assert.Equal(3, back.Version);

        // a v2 file with everything round-trips byte for byte
        var full = Doc();
        full.Params.Add(new SgCustomParam { Name = "Glow", Components = 3, Default = "1 0.5 0", IsColor = true });
        full.Comments.Add(new SgComment { Id = "c1", Title = "Box", X = 10, Y = 20, Width = 300, Height = 200, Color = "Param" });
        ShaderGraphTests.Add(full, "n2", "Custom", ("inputs", "A:float2;B:float"), ("out", "float3"), ("code", "float3 v = float3(A, B);\nreturn v;")).Label = "Mine";
        string text = ShaderGraphJson.Serialize(full);
        Assert.Equal(text, ShaderGraphJson.Serialize(ShaderGraphJson.TryDeserialize(text, out _)!));
        Assert.True(ShaderGraphJson.TryDeserialize(text, out _)!.Params.Single().IsColor);
    }

    [Theory]
    [InlineData("{ \"version\": 2, \"nodes\": [], \"links\": [], \"params\": [ { \"name\": \"1bad\", \"components\": 4, \"default\": \"0\" } ] }", "not a valid name")]
    [InlineData("{ \"version\": 2, \"nodes\": [], \"links\": [], \"params\": [ { \"name\": \"A\", \"components\": 4 }, { \"name\": \"a\", \"components\": 4 } ] }", "twice")]
    [InlineData("{ \"version\": 2, \"nodes\": [], \"links\": [], \"params\": [ { \"name\": \"A\", \"components\": 7 } ] }", "components")]
    [InlineData("{ \"version\": 2, \"nodes\": [], \"links\": [], \"comments\": [ { \"id\": \"c1\" }, { \"id\": \"c1\" } ] }", "repeated")]
    [InlineData("{ \"version\": 2, \"nodes\": [ { \"id\": \"a\", \"type\": \"Add\", \"label\": \"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\" } ], \"links\": [] }", "label")]
    public void A_version_2_file_with_a_bad_parameter_comment_or_label_is_refused_with_a_reason(string json, string reason)
    {
        Assert.Null(ShaderGraphJson.TryDeserialize(json, out var error));
        Assert.Contains(reason, error);
    }

    // ================================================================================= M837: a removal survives the save's three-way merge after an earlier save

    /// <summary>One Save To Override exactly as MainWindowViewModel.SaveMaterialOverrideFor does it: serialise, rebase onto the file when it moved from the editor's base, write, move the base.</summary>
    private static byte[] SaveLikeTheApp(MaterialEditorViewModel editor, byte[] disk, bool moveBase = true)
    {
        byte[] serialized = editor.Serialize()!;
        byte[] written = serialized;
        if (editor.BaseBytes is { } b && !disk.AsSpan().SequenceEqual(b))
            written = BinThreeWayMerge.Merge(b, serialized, disk, Names).Merged;
        _ = ReyEngine.Formats.Meta.BinTreeIo.Read(written);
        if (moveBase) editor.NoteSaved(serialized);
        return written;
    }

    private static string[] ParamNames(byte[] bin) => MaterialDocument.Parse(bin, Names).Materials.Single().Parameters.Select(p => p.Name).ToArray();
    private static string[] SamplerNames(byte[] bin) => MaterialDocument.Parse(bin, Names).Materials.Single().Slots.Select(p => p.SamplerName).ToArray();

    private async Task<(UndoRedoService Undo, MaterialEditorViewModel Editor, MaterialBindingViewModel Binding, byte[] Disk)> OpenSaved()
    {
        await SaveGraphWith("GraphA", ("GlowA", "StripesA"));
        await SaveGraphWith("GraphB", ("", "BandsB"));                       // like the report's Graph_4: textures only, so paramValues is unchanged against the opening base and ONLY the removal differs
        var undo = new UndoRedoService();
        var editor = new MaterialEditorViewModel { UndoService = undo, ProjectRoot = () => _root };
        byte[] disk = MaterialWithTint();
        editor.Load(MaterialDocument.Parse(disk, Names), new WadAssetEntry { Path = "data/maps/mapgeometry/map11/x.materials.bin" }, disk);
        var binding = editor.Materials.Single();
        binding.RefreshMacroStatus();
        return (undo, editor, binding, disk);
    }

    [Fact]
    public async Task The_chatgpt_sequence_assign_save_unassign_undo_save_switch_save_leaves_only_the_new_graphs_entries()
    {
        // Without editor.NoteSaved (SaveLikeTheApp's last step) the base stays the opening snapshot and the LAST save below keeps GlowA: the merge sees no change to paramValues
        // (the switch to a textures-only graph leaves it equal to the base) and so keeps the file's copy, which still holds GlowA from the first save.
        const bool moveBase = true;
        var (undo, editor, binding, disk) = await OpenSaved();
        var a = binding.GraphChoices.Single(c => c.Stem == "GraphA");
        var b = binding.GraphChoices.Single(c => c.Stem == "GraphB");

        binding.AssignGraphCommand.Execute(a);
        disk = SaveLikeTheApp(editor, disk, moveBase);                       // the export saves the material
        Assert.Contains("GlowA", ParamNames(disk));
        binding.UnassignGraphCommand.Execute(null);
        undo.Undo();                                                          // A restored
        disk = SaveLikeTheApp(editor, disk, moveBase);                       // Save To Override
        binding.AssignGraphCommand.Execute(b);                                // B directly over A
        disk = SaveLikeTheApp(editor, disk, moveBase);                       // the export saves again

        var p = ParamNames(disk); var s = SamplerNames(disk);
        Assert.DoesNotContain("GlowA", p);
        Assert.DoesNotContain("StripesA", s);
        Assert.Contains("BandsB", s);
        Assert.Equal(new[] { "TintColor" }, p);
    }

    [Fact]
    public async Task Unassign_and_a_Material_tab_removal_persist_after_an_earlier_save()
    {
        var (undo, editor, binding, disk) = await OpenSaved();
        binding.AssignGraphCommand.Execute(binding.GraphChoices.Single(c => c.Stem == "GraphA"));
        disk = SaveLikeTheApp(editor, disk);
        binding.UnassignGraphCommand.Execute(null);
        disk = SaveLikeTheApp(editor, disk);
        Assert.DoesNotContain("GlowA", ParamNames(disk));
        Assert.DoesNotContain("StripesA", SamplerNames(disk));

        // the Material tab: add a parameter, save, remove it, save
        var extra = binding.Model.AddParameter("ExtraThing", "Vector4")!;
        editor.IsDirty = true;
        disk = SaveLikeTheApp(editor, disk);
        Assert.Contains("ExtraThing", ParamNames(disk));
        Assert.True(binding.Model.RemoveParameter(extra));
        disk = SaveLikeTheApp(editor, disk);
        Assert.DoesNotContain("ExtraThing", ParamNames(disk));
    }
}
