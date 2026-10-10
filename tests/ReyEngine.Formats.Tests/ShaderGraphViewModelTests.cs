using ReyEngine.App.ViewModels;
using ReyEngine.Formats.Materials.Graph;
using ReyEngine.Formats.Materials.ShaderGraph;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M832: the Shader Graph editor's view model without a game install (the base is the hand-built test base): the document, its own undo
/// stack (one step per gesture), dirty / save / open in the project's .reyengine/shadergraphs folder, the type errors reaching the canvas
/// projection, the toolbar commands taking over from the material graph while a graph is open, and the compile verdicts. The compile
/// uses the real Windows HLSL compiler.
/// </summary>
public sealed class ShaderGraphViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "reyengine-m832-" + Guid.NewGuid().ToString("N"));

    public ShaderGraphViewModelTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private ShaderGraphViewModel Vm(string? root = "")
    {
        var vm = new ShaderGraphViewModel(() => root == "" ? _root : root)
        {
            BaseResolver = shader => SgBase.IsSupported(shader) ? (ShaderGraphTests.TestBase(), "") : (null, "not a supported base"),
            CompileDelay = TimeSpan.Zero,
        };
        return vm;
    }

    private static SgPaletteEntry Entry(ShaderGraphViewModel vm, string title) => vm.Palette.First(p => p.Title == title);

    private const string Flat = "Shaders/StaticMesh/DefaultEnv_Flat";

    [Fact]
    public async Task A_new_graph_starts_with_the_texture_wired_to_the_output_and_compiles()
    {
        var vm = Vm();
        Assert.True(vm.NewFromBase(Flat, out string error), error);
        Assert.True(vm.IsOpen);
        Assert.True(vm.IsDirty);                      // not on disk yet
        Assert.Equal("DefaultEnv_Flat_Graph", vm.GraphName);
        Assert.Equal(2, vm.Graph!.Nodes.Count);
        Assert.Equal(2, vm.Graph.Wires.Count);
        var build = await vm.CompileNowAsync();
        Assert.True(build!.Ok, string.Join("\n", build.Diagnostics));
        Assert.Contains("Texture2D DiffuseTexture__TX;", vm.Hlsl);
        Assert.Contains("Compiled: 2 of 2", vm.CompileState);
        Assert.Contains("equal", vm.HlslNote);
    }

    [Fact]
    public void An_unsupported_base_is_refused_and_nothing_opens()
    {
        var vm = Vm();
        Assert.False(vm.NewFromBase("Shaders/StaticMesh/Mantis", out string error));
        Assert.Contains("not a supported base", error);
        Assert.False(vm.IsOpen);
    }

    [Fact]
    public void Every_gesture_is_exactly_one_undo_step_on_the_graphs_own_stack()
    {
        var vm = Vm();
        vm.NewFromBase(Flat, out _);
        string start = ShaderGraphJson.Serialize(vm.Document!);

        string? mul = vm.AddNode(Entry(vm, "Multiply"), 400, 80);
        Assert.NotNull(mul);
        Assert.Equal(3, vm.Document!.Nodes.Count);
        Assert.True(vm.Connect("n2", "RGB", mul!, "A"));
        Assert.True(vm.Connect(mul!, "Value", "n1", SgCatalog.BaseColorPin));       // replaces the texture's wire into Base Color
        Assert.Equal(3, vm.Document.Links.Count);
        Assert.True(vm.SetProp(mul!, "B", "0.5"));
        Assert.True(vm.DeleteNode(mul!));
        Assert.Equal(2, vm.Document.Nodes.Count);
        Assert.DoesNotContain(vm.Document.Links, l => l.ToNode == mul || l.FromNode == mul);

        // five gestures, five steps back - and the document is the one we started with, byte for byte
        for (int i = 0; i < 5; i++) Assert.True(vm.Undo(), "undo " + i);
        Assert.False(vm.CanUndo);
        Assert.Equal(start, ShaderGraphJson.Serialize(vm.Document));
        Assert.Equal(2, vm.Document.Links.Count);
        // and forward again
        for (int i = 0; i < 5; i++) Assert.True(vm.Redo(), "redo " + i);
        Assert.Equal(2, vm.Document.Nodes.Count);
        Assert.False(vm.CanRedo);
    }

    [Fact]
    public void A_wire_loop_a_missing_pin_and_deleting_the_output_are_refused_with_a_reason()
    {
        var vm = Vm();
        vm.NewFromBase(Flat, out _);
        string a = vm.AddNode(Entry(vm, "Add"), 0, 0)!, b = vm.AddNode(Entry(vm, "Add"), 0, 0)!;
        Assert.True(vm.Connect(a, "Value", b, "A"));
        var loop = vm.CheckLink(b, "Value", a, "A");
        Assert.False(loop.Ok);
        Assert.Contains("loop", loop.Reason);
        Assert.False(vm.Connect(b, "Value", a, "A"));
        Assert.False(vm.CheckLink(a, "Value", a, "B").Ok);
        Assert.False(vm.CheckLink("n2", "Nope", a, "A").Ok);
        Assert.False(vm.DeleteNode("n1"));
        Assert.Contains("cannot be deleted", vm.Status);
    }

    [Fact]
    public async Task A_width_mismatch_shows_on_the_node_the_wire_and_the_list_and_stops_the_hlsl()
    {
        var vm = Vm();
        vm.NewFromBase(Flat, out _);
        string c3 = vm.AddNode(Entry(vm, "Constant3 (float3)"), 0, 0)!, c2 = vm.AddNode(Entry(vm, "Constant2 (float2)"), 0, 100)!;
        string add = vm.AddNode(Entry(vm, "Add"), 200, 50)!;
        vm.Connect(c3, "Value", add, "A"); vm.Connect(c2, "Value", add, "B"); vm.Connect(add, "Value", "n1", SgCatalog.BaseColorPin);

        Assert.Equal(GraphNodeState.Error, vm.Graph!.Find(add)!.State);
        Assert.Contains("only a scalar", vm.Graph.Find(add)!.Message);
        Assert.Contains(vm.DiagnosticRows, r => r.IsError && r.NodeId == add);
        var build = await vm.CompileNowAsync();
        Assert.False(build!.Ok);
        Assert.Empty(vm.Hlsl);
        Assert.Contains("errors", vm.HlslNote);
        Assert.Contains("error(s) in the graph", vm.CompileState);
        Assert.Contains(add, vm.BuildLog());

        // fixing it (a scalar broadcasts) clears the error and compiles again
        Assert.True(vm.SetProp(c2, "value", "1"));
        build = await vm.CompileNowAsync();
        Assert.True(build!.Ok, string.Join("\n", build.Diagnostics));
        Assert.Equal(GraphNodeState.Authored, vm.Graph!.Find(add)!.State);
        Assert.NotEmpty(vm.Hlsl);
    }

    [Fact]
    public async Task Save_writes_the_project_file_and_Open_reads_it_back_identically()
    {
        var vm = Vm();
        vm.NewFromBase(Flat, out _);
        vm.AddNode(Entry(vm, "Saturate"), 300, 300);
        string expected = Path.Combine(_root, ".reyengine", "shadergraphs", "DefaultEnv_Flat_Graph.shadergraph.json");
        Assert.True(vm.IsDirty);
        Assert.True(await vm.SaveAsync());
        Assert.Equal(expected, vm.FilePath);
        Assert.True(File.Exists(expected));
        Assert.False(File.Exists(expected + ".tmp"));
        Assert.False(vm.IsDirty);
        Assert.Equal(ShaderGraphJson.Serialize(vm.Document!), File.ReadAllText(expected));

        // an edit dirties it, an undo back to the saved state cleans it
        vm.AddNode(Entry(vm, "Abs"), 0, 0);
        Assert.True(vm.IsDirty);
        vm.Undo();
        Assert.False(vm.IsDirty);

        var other = Vm();
        Assert.True(other.Open(expected, out string error), error);
        Assert.Equal(ShaderGraphJson.Serialize(vm.Document!), ShaderGraphJson.Serialize(other.Document!));
        Assert.False(other.IsDirty);
        Assert.False(other.CanUndo);
        other.RefreshSavedGraphs();
        Assert.Equal("DefaultEnv_Flat_Graph", Assert.Single(other.SavedGraphs).Name);
    }

    [Fact]
    public async Task A_second_new_graph_gets_its_own_name_and_never_overwrites_a_saved_one()
    {
        var vm = Vm();
        vm.NewFromBase(Flat, out _);
        await vm.SaveAsync();
        string first = vm.FilePath!;
        string before = File.ReadAllText(first);
        var vm2 = Vm();
        vm2.NewFromBase(Flat, out _);
        Assert.Equal("DefaultEnv_Flat_Graph_2", vm2.GraphName);
        vm2.AddNode(Entry(vm2, "Abs"), 0, 0);
        Assert.True(await vm2.SaveAsync());
        Assert.NotEqual(first, vm2.FilePath);
        Assert.Equal(before, File.ReadAllText(first));
    }

    [Fact]
    public async Task Without_a_project_nothing_is_written_and_the_status_says_why()
    {
        var vm = Vm(root: null);
        vm.NewFromBase(Flat, out _);
        Assert.False(await vm.SaveAsync());
        Assert.Contains("project", vm.Status);
        Assert.True(vm.IsDirty);
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public void A_file_this_build_cannot_read_leaves_the_editor_as_it_was()
    {
        string bad = Path.Combine(_root, "bad.shadergraph.json");
        File.WriteAllText(bad, "{ \"version\": 9, \"nodes\": [], \"links\": [] }");
        var vm = Vm();
        vm.NewFromBase(Flat, out _);
        Assert.False(vm.Open(bad, out string error));
        Assert.Contains("version 9", error);
        Assert.True(vm.IsOpen);
        Assert.Equal("DefaultEnv_Flat_Graph", vm.GraphName);
    }

    [Fact]
    public void Moving_a_node_marks_the_graph_changed_without_an_undo_step_and_selection_edits_the_nodes_settings()
    {
        var vm = Vm();
        vm.NewFromBase(Flat, out _);
        vm.MoveNode("n2", 12, 34);
        Assert.Equal(12, vm.Document!.Find("n2")!.X);
        Assert.False(vm.CanUndo);
        Assert.True(vm.IsDirty);

        vm.SelectedNodeId = "n2";
        var row = Assert.Single(vm.PropRows, r => r.Label == "Texture");
        Assert.Contains("DiffuseTexture", row.Choices);
        row.Text = "DetailTexture";                  // a choice from the list commits at once
        Assert.Equal("DetailTexture", vm.Document.Find("n2")!.Prop("texture"));
        Assert.True(vm.CanUndo);
        // an unwired input is editable on the node; a wired one is not (the wire decides)
        string add = vm.AddNode(Entry(vm, "Add"), 0, 0)!;
        vm.SelectedNodeId = add;
        Assert.Equal(new[] { "A (not wired)", "B (not wired)" }, vm.PropRows.Select(r => r.Label));
        vm.Connect("n2", "R", add, "A");
        Assert.Equal(new[] { "B (not wired)" }, vm.PropRows.Select(r => r.Label));
    }

    [Fact]
    public async Task The_material_graph_toolbar_commands_run_the_shader_graph_while_one_is_open()
    {
        var graph = new MaterialGraphViewModel(null);
        var vm = Vm();
        graph.AttachShaderGraph(vm);
        Assert.False(graph.SaveCommand.CanExecute(null));       // no material editor, no shader graph: still disabled as before
        vm.NewFromBase(Flat, out _);
        Assert.True(graph.SaveCommand.CanExecute(null));
        Assert.True(graph.UndoCommand.CanExecute(null));
        Assert.True(graph.ShowDirty);
        Assert.Equal(2, graph.CodeStage);

        vm.AddNode(Entry(vm, "Abs"), 0, 0);
        Assert.Equal(3, vm.Document!.Nodes.Count);
        graph.UndoCommand.Execute(null);                        // Ctrl+Z
        Assert.Equal(2, vm.Document.Nodes.Count);
        graph.RedoCommand.Execute(null);
        Assert.Equal(3, vm.Document.Nodes.Count);
        await graph.SaveCommand.ExecuteAsync(null);             // Ctrl+S
        Assert.True(File.Exists(vm.FilePath));
        Assert.False(graph.ShowDirty);
        Assert.Contains("Saved", graph.EditStatus);

        vm.Close();
        Assert.False(graph.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_generated_hlsl_is_the_third_view_of_the_Shader_Code_tab()
    {
        var graph = new MaterialGraphViewModel(null);
        var vm = Vm();
        graph.AttachShaderGraph(vm);
        vm.NewFromBase(Flat, out _);
        await vm.CompileNowAsync();
        Assert.Equal(2, graph.CodeStage);                       // opening a graph brings the generated HLSL up
        Assert.Contains("void main(PSIn i", graph.ShaderCodeText);
        Assert.Contains("Generated HLSL", graph.CodeNoteShown);
        graph.CodeStage = 0;                                    // the disassembly is still there, next to it
        Assert.DoesNotContain("void main(PSIn i", graph.ShaderCodeText);
    }

    [Fact]
    public async Task A_preview_swap_hook_fires_with_the_build_and_only_compiled_signatures_are_offered()
    {
        var vm = Vm();
        SgBuild? seen = null;
        vm.BuildCompleted += b => seen = b;
        vm.NewFromBase(Flat, out _);
        await vm.CompileNowAsync();
        Assert.NotNull(seen);
        Assert.All(seen!.Compiled, c => Assert.True(c.Ok));
        var sig1 = vm.Base!.Signatures[1];
        Assert.Same(seen.Compiled[1], seen.ForRiotPixel(seen.Compiled[1].Shader!));
        Assert.Equal(sig1.Index, seen.For(sig1)!.Signature.Index);
    }

    [Fact]
    public void Undo_restores_topology_and_settings_but_leaves_the_nodes_where_they_were_put()
    {
        var vm = Vm();
        vm.NewFromBase(Flat, out _);
        string add = vm.AddNode(Entry(vm, "Add"), 10, 10)!;
        vm.MoveNode(add, 300, 200);
        Assert.True(vm.Connect("n2", "R", add, "A"));
        Assert.True(vm.Undo());                                   // the wire goes
        Assert.Null(vm.Document!.LinkInto(add, "A"));
        Assert.Equal(300, vm.Document.Find(add)!.X);              // the move stays
        Assert.Equal(200, vm.Document.Find(add)!.Y);
        Assert.True(vm.Undo());                                   // the node goes
        Assert.Null(vm.Document.Find(add));
        Assert.True(vm.Redo());
        Assert.NotNull(vm.Document.Find(add));                    // a node that was gone comes back where the snapshot had it
    }

    [Fact]
    public void A_never_saved_graph_stays_dirty_whatever_is_undone_and_a_pin_value_must_be_a_number()
    {
        var vm = Vm();
        vm.NewFromBase(Flat, out _);
        vm.AddNode(Entry(vm, "Abs"), 0, 0);
        vm.Undo();
        Assert.True(vm.IsDirty);
        string add = vm.AddNode(Entry(vm, "Add"), 0, 0)!;
        Assert.False(vm.SetProp(add, "A", "banana"));
        Assert.Contains("not a number", vm.Status);
        Assert.True(vm.SetProp(add, "A", "0.5"));
    }

    [Fact]
    public async Task A_file_the_analyser_cannot_take_or_one_that_is_too_big_leaves_the_open_graph_alone()
    {
        var vm = Vm();
        vm.NewFromBase(Flat, out _);
        vm.AddNode(Entry(vm, "Abs"), 0, 0);
        string json = ShaderGraphJson.Serialize(vm.Document!);
        string big = Path.Combine(_root, "big.shadergraph.json");
        File.WriteAllText(big, new string(' ', ShaderGraphDocument.MaxFileBytes + 10));
        Assert.False(vm.Open(big, out string error));
        Assert.Contains("too large", error);
        Assert.True(vm.IsOpen);
        Assert.Equal(json, ShaderGraphJson.Serialize(vm.Document!));

        string nulls = Path.Combine(_root, "nulls.shadergraph.json");
        File.WriteAllText(nulls, "{ \"version\": 1, \"name\": \"N\", \"baseShader\": \"Shaders/StaticMesh/DefaultEnv_Flat\", \"nodes\": [ { \"id\": \"n1\", \"type\": \"Output\", \"props\": null } ], "
            + "\"links\": [ { \"fromNode\": null, \"fromPin\": null, \"toNode\": null, \"toPin\": null } ] }");
        Assert.True(vm.Open(nulls, out error), error);             // opens, and reports the dangling wire instead of crashing
        Assert.NotNull(await vm.CompileNowAsync());
    }

    [Fact]
    public void Closing_clears_the_status_and_stops_a_running_compile_from_being_shown()
    {
        var vm = Vm();
        vm.NewFromBase(Flat, out _);
        vm.AddNode(Entry(vm, "Abs"), 0, 0);
        Assert.False(string.IsNullOrEmpty(vm.Status));
        vm.Close();
        Assert.Equal("", vm.Status);
        Assert.False(vm.Compiling);
    }
}
