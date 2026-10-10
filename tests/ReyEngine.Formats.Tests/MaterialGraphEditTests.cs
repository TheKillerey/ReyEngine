using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Undo;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Materials.Graph;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M830: the Material Graph edits values - parameters, textures, switches, render state - through the Material
/// Editor that holds the material. These tests drive the real view models (editor, graph, rows) on a synthetic
/// material; <see cref="MaterialGraphEditRealDataTests"/> does the same on Riot's own files, with the shader cache.
/// </summary>
public sealed class MaterialGraphEditTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);
    private const string MatPath = "Maps/Test/Materials/Rock_MAT";
    private const string ShaderPath = "Shaders/StaticMesh/Test_Shader";

    private static readonly Dictionary<uint, string> Known = new[] { "StaticMaterialDef", "name", ShaderPath }.ToDictionary(H);
    private static string? Resolve(uint h) => Known.TryGetValue(h, out var n) ? n : null;

    private static BinTreeEmbedded Sampler(string name, string path) =>
        new(0, H("StaticMaterialShaderSamplerDef"), new BinTreeProperty[]
        {
            new BinTreeString(H("TextureName"), name),
            new BinTreeString(H("texturePath"), path),
        });

    private static BinTreeEmbedded Param(string name, BinTreeProperty value) =>
        new(0, H("StaticMaterialShaderParamDef"), new BinTreeProperty[] { new BinTreeString(H("name"), name), value });

    private static BinTreeEmbedded Switch(string name, bool? on) =>
        new(0, H("StaticMaterialSwitchDef"), on is null
            ? new BinTreeProperty[] { new BinTreeString(H("name"), name) }
            : new BinTreeProperty[] { new BinTreeString(H("name"), name), new BinTreeBool(H("on"), on.Value) });

    private static byte[] RockBin()
    {
        var passDef = new BinTreeEmbedded(0, H("StaticMaterialPassDef"), new BinTreeProperty[]
        {
            new BinTreeObjectLink(H("shader"), H(ShaderPath)),
            new BinTreeBool(H("blendEnable"), false),
            new BinTreeBool(H("cullEnable"), false),
        });
        var tech = new BinTreeEmbedded(0, H("StaticMaterialTechniqueDef"), new BinTreeProperty[]
        {
            new BinTreeUnorderedContainer(H("passes"), BinPropertyType.Embedded, new List<BinTreeEmbedded> { passDef }),
        });
        var mat = new BinTreeObject(H(MatPath), H("StaticMaterialDef"), new BinTreeProperty[]
        {
            new BinTreeString(H("name"), MatPath),
            new BinTreeUnorderedContainer(H("samplerValues"), BinPropertyType.Embedded,
                new List<BinTreeEmbedded> { Sampler("Diffuse_Texture", "assets/maps/rock_d.tex") }),
            new BinTreeUnorderedContainer(H("paramValues"), BinPropertyType.Embedded, new List<BinTreeEmbedded>
            {
                Param("TintColor", new BinTreeVector4(H("value"), new Vector4(1f, 0.5f, 0.25f, 1f))),
                Param("Scale", new BinTreeVector4(H("value"), new Vector4(2f, 0, 0, 0))),
            }),
            new BinTreeUnorderedContainer(H("switches"), BinPropertyType.Embedded,
                new List<BinTreeEmbedded> { Switch("USE_A", null), Switch("USE_B", false) }),
            new BinTreeUnorderedContainer(H("techniques"), BinPropertyType.Embedded, new List<BinTreeEmbedded> { tech }),
        });
        using var ms = new MemoryStream();
        new BinTree(new[] { mat }, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    private sealed class Rig : IDisposable
    {
        public UndoRedoService Undo { get; } = new();
        public MaterialEditorViewModel Editor { get; }
        public MaterialGraphViewModel Graph { get; } = new(null);
        public MaterialBindingViewModel Material => Editor.Materials.Single();
        public int LiveApplies;

        public Rig()
        {
            Editor = new MaterialEditorViewModel { UndoService = Undo };
            var bytes = RockBin();
            Editor.Load(MaterialDocument.Parse(bytes, Resolve), new WadAssetEntry { Path = "maps/test/rock.materials.bin", IsResolved = true }, bytes);
            Graph.ShowMaterial(Material.Model, null, null, Editor);
        }

        public GraphParamEditRow ParamRow(string node)
        {
            Graph.SelectedNodeId = node;
            return Assert.IsType<GraphParamEditRow>(Assert.Single(Graph.EditRows));
        }

        public void Dispose() => Graph.Dispose();
    }

    private static string Tint(MaterialBindingViewModel m) => m.Model.Parameters.Single(p => p.Name == "TintColor").CurrentText;

    // ===================================================================== parameters

    [Fact]
    public void AColourEditedInTheGraphIsTheEditorsEdit_ItsRowTheNodeAndTheDirtyFlagAllAgree_AndUndoRestoresIt()
    {
        using var rig = new Rig();
        Assert.True(rig.Graph.CanEdit);
        Assert.False(rig.Editor.IsDirty);
        Assert.False(rig.Graph.ShowDirty);

        var row = rig.ParamRow("param:0");                       // TintColor
        Assert.True(row.IsColour);
        Assert.True(row.HasPicker);
        Assert.Equal(new[] { "R", "G", "B", "A" }, row.Components.Select(c => c.Label));

        row.Components[1].Text = "0.9";
        row.Components[1].CommitCommand.Execute(null);

        // the document, the Material tab's row, the node and the dirty flag
        Assert.Equal("1, 0.9, 0.25, 1", Tint(rig.Material));
        Assert.Equal("1, 0.9, 0.25, 1", rig.Material.Parameters.First(p => p.Name == "TintColor").EditedText);
        Assert.True(rig.Editor.IsDirty);
        Assert.True(rig.Graph.IsEditorDirty);
        Assert.True(rig.Graph.ShowDirty);
        var node = rig.Graph.Graph!.Find("param:0")!;
        Assert.Equal(0.9f, node.Swatch!.Value.Y, 4);
        Assert.Contains("0.9", node.Outputs[0].Detail);
        Assert.Equal("param:0", rig.Graph.SelectedNodeId);       // the selection survived the rebuild

        // Undo is the editor's undo: the value, the dirty flag and the node come back
        Assert.True(rig.Undo.CanUndo);
        rig.Graph.UndoCommand.Execute(null);
        Assert.Equal("1, 0.5, 0.25, 1", Tint(rig.Material));
        Assert.False(rig.Editor.IsDirty);
        Assert.False(rig.Graph.ShowDirty);
        Assert.Equal(0.5f, rig.Graph.Graph!.Find("param:0")!.Swatch!.Value.Y, 4);
        Assert.Equal("0.5", row.Components[1].Text);             // the box followed, in place

        rig.Graph.RedoCommand.Execute(null);
        Assert.Equal("1, 0.9, 0.25, 1", Tint(rig.Material));
        Assert.True(rig.Editor.IsDirty);
    }

    [Fact]
    public void ThePickerAndTheHexBoxWriteOnlyTheChannelsTheyChange()
    {
        using var rig = new Rig();
        var row = rig.ParamRow("param:0");
        // 0.5 -> 128 (8-bit); a click that leaves the pixel where it is writes nothing
        row.PickerColor = row.PickerColor;
        Assert.False(rig.Editor.IsDirty);

        // B 0.25 reads as 64 in 8 bits: a hex that keeps 64 keeps the authored 0.25 text; G 128 -> 0 and R 255 are written
        row.Hex = "#FF0040";
        row.CommitHexCommand.Execute(null);
        Assert.Equal("1, 0, 0.25, 1", Tint(rig.Material));
        Assert.True(rig.Editor.IsDirty);

        row.Hex = "red";
        row.CommitHexCommand.Execute(null);
        Assert.True(row.HasError);                                // refused with a reason, nothing written
        Assert.StartsWith("1, 0, ", Tint(rig.Material));

        row.Hex = "#FF0041";                                      // B 65: a changed channel is rewritten as 65/255
        row.CommitHexCommand.Execute(null);
        Assert.Equal("1, 0, " + (65 / 255f).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ", 1", Tint(rig.Material));
    }

    [Fact]
    public void ABadNumberIsRefusedWithAReason_AndAnUnchangedBoxWritesNothing()
    {
        using var rig = new Rig();
        var row = rig.ParamRow("param:1");                        // Scale = (2, 0, 0, 0)
        Assert.False(row.IsColour);
        Assert.False(row.HasPicker);

        row.Components[0].Text = "two";
        row.Components[0].CommitCommand.Execute(null);
        Assert.True(row.HasError);
        Assert.False(rig.Editor.IsDirty);
        Assert.False(rig.Undo.CanUndo);

        row.Components[0].Text = "2";                             // typed back to what it was
        row.Components[0].CommitCommand.Execute(null);
        Assert.False(row.HasError);
        Assert.False(rig.Editor.IsDirty);
        Assert.False(rig.Undo.CanUndo);

        row.Components[0].Text = "3.5";
        row.Components[0].CommitCommand.Execute(null);
        Assert.Equal("3.5, 0, 0, 0", rig.Material.Model.Parameters.Single(p => p.Name == "Scale").CurrentText);
    }

    [Fact]
    public void AnEditMadeOnTheMaterialTabReachesTheGraph()
    {
        using var rig = new Rig();
        rig.Graph.SelectedNodeId = "param:1";
        var tabRow = rig.Material.Parameters.First(p => p.Name == "Scale");
        tabRow.EditedText = "9, 0, 0, 0";
        tabRow.ApplyCommand.Execute(null);

        Assert.Contains("9", rig.Graph.Graph!.Find("param:1")!.Outputs[0].Detail);
        var row = Assert.IsType<GraphParamEditRow>(Assert.Single(rig.Graph.EditRows));
        Assert.Equal("9", row.Components[0].Text);                // and the Details box
    }

    // ===================================================================== textures

    [Fact]
    public void ATexturePathEditedInTheGraphIsTheSlotsEdit_AndUndoPutsTheOldPathBack()
    {
        using var rig = new Rig();
        rig.Graph.SelectedNodeId = "tex:0";
        var row = Assert.IsType<GraphTextureEditRow>(Assert.Single(rig.Graph.EditRows));
        Assert.Equal("assets/maps/rock_d.tex", row.Pending);

        row.Pending = "assets/maps/rock_other.tex";
        row.CommitPathCommand.Execute(null);

        var slot = rig.Material.Model.Slots.Single();
        Assert.Equal("assets/maps/rock_other.tex", slot.Path);
        Assert.Equal("assets/maps/rock_other.tex", rig.Material.Slots.Single().EditedPath);
        Assert.Equal("assets/maps/rock_other.tex", rig.Graph.Graph!.Find("tex:0")!.TexturePath);
        Assert.Equal(slot.ChunkHash, rig.Graph.Graph.Find("tex:0")!.TextureChunk);   // the thumbnail follows the new chunk
        Assert.True(rig.Editor.IsDirty);

        rig.Graph.UndoCommand.Execute(null);
        Assert.Equal("assets/maps/rock_d.tex", slot.Path);
        Assert.Equal("assets/maps/rock_d.tex", row.Pending);
        Assert.False(rig.Editor.IsDirty);
    }

    // ===================================================================== switches and render state

    [Fact]
    public void ASwitchToggledInTheGraphWritesTheEditorsModel_AbsentOnMeansOn_AndUndoRestoresTheAbsentField()
    {
        using var rig = new Rig();
        rig.Graph.SelectedNodeId = "sw:0";                        // USE_A, no 'on' field = ON
        var row = Assert.IsType<GraphSwitchEditRow>(Assert.Single(rig.Graph.EditRows));
        Assert.True(row.Switch.IsOn);
        Assert.Equal("ON", rig.Graph.Graph!.Find("sw:0")!.Outputs[0].Detail);

        row.Switch.IsOn = false;
        Assert.False(rig.Material.Model.AllSwitches.First(s => s.Name == "USE_A").On);
        Assert.Equal("OFF", rig.Graph.Graph!.Find("sw:0")!.Outputs[0].Detail);
        Assert.True(rig.Editor.IsDirty);
        Assert.Equal("sw:0", rig.Graph.SelectedNodeId);

        rig.Graph.UndoCommand.Execute(null);                      // a toggle is one undo step, here and on the Material tab
        Assert.True(rig.Material.Model.AllSwitches.First(s => s.Name == "USE_A").On);
        Assert.True(row.Switch.IsOn);
        Assert.Equal("ON", rig.Graph.Graph!.Find("sw:0")!.Outputs[0].Detail);
        Assert.False(rig.Editor.IsDirty);
        Assert.Equal(rig.Editor.Serialize(), RoundTrip(rig.Editor.BaseBytes!));   // the 'on' field is absent again: byte-identical to the original
    }

    private static byte[] RoundTrip(byte[] bytes) => MaterialDocument.Parse(bytes, Resolve).Serialize();

    [Fact]
    public void TheOutputNodeEditsCullAndBlendThroughTheMaterialTabsOwnProperties_AndTheyAreUndoable()
    {
        using var rig = new Rig();
        rig.Graph.SelectedNodeId = "output";
        var row = Assert.IsType<GraphRenderStateEditRow>(Assert.Single(rig.Graph.EditRows));
        Assert.Same(rig.Material, row.Binding);
        Assert.False(row.Binding.BlendEnable);

        row.Binding.BlendEnable = true;
        row.Binding.SrcBlendChoice = 1 + 6;                       // SrcAlpha
        Assert.True(rig.Material.Model.BlendEnable);
        Assert.Equal("ON", rig.Graph.Graph!.OutputNode!.Inputs.Single(p => p.Name == "Blend").Detail);
        Assert.Equal("SrcAlpha", rig.Graph.Graph.OutputNode.Inputs.Single(p => p.Name == "Src color factor").Detail);

        rig.Graph.UndoCommand.Execute(null);                      // the factor
        Assert.Equal(0, row.Binding.SrcBlendChoice);              // absent again
        Assert.True(row.Binding.BlendEnable);
        rig.Graph.UndoCommand.Execute(null);                      // the blend flag
        Assert.False(row.Binding.BlendEnable);
        Assert.False(rig.Material.Model.BlendEnable);
        Assert.Equal("OFF", rig.Graph.Graph!.OutputNode!.Inputs.Single(p => p.Name == "Blend").Detail);
        Assert.False(rig.Undo.CanUndo);
    }

    // ===================================================================== the graph is rebuilt, not replaced

    [Fact]
    public void AnEditRebuildsTheGraphAsANewObjectOfTheSameMaterial_AndTellsThePreview()
    {
        using var rig = new Rig();
        var before = rig.Graph.Graph;
        int edited = 0;
        rig.Graph.Edited += () => edited++;
        var row = rig.ParamRow("param:1");
        row.Components[0].Text = "4";
        row.Components[0].CommitCommand.Execute(null);

        Assert.NotSame(before, rig.Graph.Graph);                  // the canvas sees a graph change and keeps its view for the same material
        Assert.Equal(before!.MaterialName, rig.Graph.Graph!.MaterialName);
        Assert.Equal(before.Nodes.Select(n => n.Id), rig.Graph.Graph.Nodes.Select(n => n.Id));
        Assert.Equal(1, edited);
        Assert.Same(rig.Material.Model, rig.Graph.EditedBinding);
    }

    // ===================================================================== read-only, with the reason

    [Fact]
    public void AGraphWithoutAnEditorIsReadOnlyAndSaysWhy_NothingIsEditableAndNoCommandRuns()
    {
        var bytes = RockBin();
        var doc = MaterialDocument.Parse(bytes, Resolve);
        using var graph = new MaterialGraphViewModel(null);
        graph.ShowMaterial(doc.Materials.Single(), null, null);

        Assert.False(graph.CanEdit);
        Assert.Equal(MaterialGraphViewModel.NoEditorNote, graph.EditNote);
        Assert.Equal("READ ONLY", graph.ModeBadge);
        graph.SelectedNodeId = "param:0";
        Assert.Empty(graph.EditRows);
        Assert.False(graph.SaveCommand.CanExecute(null));
        Assert.False(graph.ApplyEditsCommand.CanExecute(null));
        Assert.False(graph.UndoCommand.CanExecute(null));
        Assert.Equal(graph.EditNote, graph.SaveTip);              // the disabled button explains itself
        Assert.Null(graph.EditedBinding);

        // a shader on its own has no material
        graph.ShowShader("shaders/x", null, null, null, "", "");
        Assert.False(graph.CanEdit);
        Assert.Contains("no material to edit", graph.EditNote);
    }

    [Fact]
    public void AMaterialTheEditorDoesNotHoldIsReadOnly_AndAnEditorThatLoadedAnotherFileLetsTheGraphGo()
    {
        using var rig = new Rig();
        var otherDoc = MaterialDocument.Parse(RockBin(), Resolve);   // a second parse: not the editor's objects
        using var stranger = new MaterialGraphViewModel(null);
        stranger.ShowMaterial(otherDoc.Materials.Single(), null, null, rig.Editor);
        Assert.False(stranger.CanEdit);
        Assert.Contains("not one of the materials the Material Editor holds", stranger.EditNote);

        // the editor opens another file: the graph can no longer reach its document
        Assert.True(rig.Graph.CanEdit);
        var bytes = RockBin();
        rig.Editor.Load(MaterialDocument.Parse(bytes, Resolve), new WadAssetEntry { Path = "maps/test/other.materials.bin", IsResolved = true }, bytes);
        Assert.False(rig.Graph.CanEdit);
        Assert.Contains("loaded another file", rig.Graph.EditNote);
        Assert.Empty(rig.Graph.EditRows);
        Assert.False(rig.Graph.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void AnEditorThatLoadsTheSameFileAgainIsFollowed_WithTheSelection_AndItsNewDocumentIsWhatTheGraphEdits()
    {
        using var rig = new Rig();
        rig.Graph.SelectedNodeId = "param:1";
        var oldMaterial = rig.Material.Model;
        int edited = 0;
        rig.Graph.Edited += () => edited++;

        var bytes = RockBin();
        rig.Editor.Load(MaterialDocument.Parse(bytes, Resolve), new WadAssetEntry { Path = "maps/test/rock.materials.bin", IsResolved = true }, bytes);   // a refresh of the same file

        Assert.True(rig.Graph.CanEdit);
        Assert.NotSame(oldMaterial, rig.Material.Model);
        Assert.Same(rig.Material.Model, rig.Graph.EditedBinding);         // the new document's binding
        Assert.Equal("param:1", rig.Graph.SelectedNodeId);
        Assert.True(edited > 0);                                          // so the window can point its preview at it

        var row = Assert.IsType<GraphParamEditRow>(Assert.Single(rig.Graph.EditRows));
        row.Components[0].Text = "8";
        row.Components[0].CommitCommand.Execute(null);
        Assert.Equal("8, 0, 0, 0", rig.Material.Model.Parameters.Single(p => p.Name == "Scale").CurrentText);   // the editor's current document took it
    }

    [Fact]
    public void RowsAreNotReusedWhenAMaterialTabEditMovesWhatTheNodeResolvesTo()
    {
        using var rig = new Rig();
        var tint = rig.ParamRow("param:0");                              // TintColor
        Assert.Equal("TintColor", tint.Parameter.Name);

        // the Material tab removes TintColor: the positional id param:0 now names Scale. M831: the SELECTION follows the entry, not the id,
        // so the removed node's selection is cleared instead of silently moving to Scale
        rig.Material.RemoveParameterCommand.Execute(rig.Material.Parameters.First(p => p.Name == "TintColor"));
        Assert.Null(rig.Graph.SelectedNodeId);
        rig.Graph.SelectedNodeId = "param:0";

        var row = Assert.IsType<GraphParamEditRow>(Assert.Single(rig.Graph.EditRows));
        Assert.Equal("Scale", row.Parameter.Name);
        Assert.NotSame(tint, row);
        Assert.Contains(rig.Material.Parameters, p => ReferenceEquals(p, row.Parameter));   // wrapping a live row, not a detached one
    }

    [Fact]
    public void RowsAreNotReusedWhenTheSelectedIdStaysValidButNowWrapsAnotherModel()
    {
        // a selection that has only an id (no entry behind it, as the shader-default nodes) hits the identity guard of the row refresh:
        // the id param:0 stays valid, but the Material tab removing TintColor makes it Scale
        using var rig = new Rig();
        var tint = rig.ParamRow("param:0");
        typeof(MaterialGraphViewModel).GetField("_selectedModel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(rig.Graph, null);
        rig.Material.RemoveParameterCommand.Execute(rig.Material.Parameters.First(p => p.Name == "TintColor"));
        Assert.Equal("param:0", rig.Graph.SelectedNodeId);
        var row = Assert.IsType<GraphParamEditRow>(Assert.Single(rig.Graph.EditRows));
        Assert.Equal("Scale", row.Parameter.Name);
        Assert.NotSame(tint, row);
        Assert.Contains(rig.Material.Parameters, p => ReferenceEquals(p, row.Parameter));
    }

    [Fact]
    public void UndoRedoAndSaveCommitWhatIsTypedButNotYetCommitted()
    {
        using var rig = new Rig();
        var row = rig.ParamRow("param:1");                                // Scale = 2
        row.Components[0].Text = "7";                                    // typed, no Enter, no focus change
        Assert.Equal("2, 0, 0, 0", rig.Material.Model.Parameters.Single(p => p.Name == "Scale").CurrentText);

        rig.Graph.UndoCommand.Execute(null);                              // commits "7" as a step, then takes it back
        Assert.Equal("2, 0, 0, 0", rig.Material.Model.Parameters.Single(p => p.Name == "Scale").CurrentText);
        Assert.True(rig.Undo.CanRedo);                                    // proof the typed value became a step
        rig.Graph.RedoCommand.Execute(null);
        Assert.Equal("7, 0, 0, 0", rig.Material.Model.Parameters.Single(p => p.Name == "Scale").CurrentText);

        var colour = rig.ParamRow("param:0");
        colour.Hex = "#FF0000";                                          // a typed hex, not committed
        rig.Graph.CommitPending();
        Assert.StartsWith("1, 0, 0", Tint(rig.Material));
    }

    [Fact]
    public void TheStatusLineIsAboutTheLastUndoOnly()
    {
        using var rig = new Rig();
        var row = rig.ParamRow("param:1");
        row.Components[0].Text = "3";
        row.Components[0].CommitCommand.Execute(null);
        rig.Graph.UndoCommand.Execute(null);
        Assert.StartsWith("Undid", rig.Graph.EditStatus);
        row.Components[0].Text = "4";
        row.Components[0].CommitCommand.Execute(null);                   // an ordinary edit: the old line is stale
        Assert.Equal("", rig.Graph.EditStatus);
    }

    [Fact]
    public void ARowKeepsItsSavedBindingWhileItShowsTheEditorsAndGetsItBack()
    {
        var saved = MaterialDocument.Parse(RockBin(), Resolve).Materials.Single();
        var live = MaterialDocument.Parse(RockBin(), Resolve).Materials.Single();
        var row = new MaterialRow { Binding = saved };
        row.UseEditorBinding(live);
        Assert.Same(live, row.Binding);
        Assert.Same(saved, row.SavedBinding);
        row.UseEditorBinding(MaterialDocument.Parse(RockBin(), Resolve).Materials.Single());   // the editor reloaded
        Assert.Same(saved, row.SavedBinding);                              // the parsed one is never lost
        row.RestoreSavedBinding();
        Assert.Same(saved, row.Binding);
    }

    [Fact]
    public void UndoOnlyTakesBackEditsOfTheEditorsOwnFile()
    {
        using var rig = new Rig();
        var foreign = new object();
        var command = new ForeignCommand(foreign);
        rig.Undo.PushApplied(command);                           // the last change on the global stack is somebody else's
        rig.Graph.UndoCommand.Execute(null);
        Assert.Contains("not an edit of this material file", rig.Graph.EditStatus);
        Assert.Equal(0, command.Undone);                         // it was left alone
        Assert.True(rig.Undo.CanUndo);
        Assert.Same(foreign, rig.Undo.UndoContext);
    }

    private sealed class ForeignCommand : IEditorCommand
    {
        public int Undone;
        public ForeignCommand(object context) => Context = context;
        public string Name => "Move something";
        public object? Context { get; }
        public void Execute() { }
        public void Undo() => Undone++;
        public bool CanMergeWith(IEditorCommand next) => false;
        public void MergeWith(IEditorCommand next) => throw new NotSupportedException();
    }

    [Fact]
    public void ClosingTheGraphLeavesTheEditsPendingInTheEditor()
    {
        var rig = new Rig();
        var row = rig.ParamRow("param:1");
        row.Components[0].Text = "5";
        row.Components[0].CommitCommand.Execute(null);
        Assert.True(rig.Editor.IsDirty);

        rig.Dispose();                                           // the window closed

        Assert.True(rig.Editor.IsDirty);                          // still pending, exactly as after switching tabs
        Assert.Equal("5, 0, 0, 0", rig.Material.Model.Parameters.Single(p => p.Name == "Scale").CurrentText);
        Assert.True(rig.Undo.CanUndo);
        // and an edit after the close no longer reaches a disposed graph
        rig.Material.Parameters.First(p => p.Name == "Scale").EditedText = "6, 0, 0, 0";
        rig.Material.Parameters.First(p => p.Name == "Scale").ApplyCommand.Execute(null);
        Assert.True(rig.Editor.IsDirty);
    }
}
