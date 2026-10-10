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
/// M831: wiring in the Material Graph - connect, move, disconnect, create and delete - on a synthetic material with a synthetic
/// shader (its inputs are supplied as reflection would). Every gesture writes the material's entries through the Material Editor and
/// is ONE undo step; <see cref="MaterialGraphEditRealDataTests"/>-style real-data checks run on Riot's own Map11 bin in
/// <see cref="MaterialGraphWiringRealDataTests"/>.
/// </summary>
public sealed class MaterialGraphWiringTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);
    private const string MatPath = "Maps/Test/Materials/Rock_MAT";
    private const string OtherPath = "Maps/Test/Materials/Other_MAT";
    private const string ShaderPath = "Shaders/StaticMesh/Test_Shader";

    private static readonly Dictionary<uint, string> Known = new[] { "StaticMaterialDef", "name", ShaderPath }.ToDictionary(H);
    private static string? Resolve(uint h) => Known.TryGetValue(h, out var n) ? n : null;

    private static BinTreeEmbedded Sampler(string name, string path) =>
        new(0, H("StaticMaterialShaderSamplerDef"), new BinTreeProperty[]
        {
            new BinTreeString(H("TextureName"), name),
            new BinTreeString(H("texturePath"), path),
            new BinTreeU32(H("addressW"), 1),
        });

    private static BinTreeEmbedded Param(string name, BinTreeProperty value) =>
        new(0, H("StaticMaterialShaderParamDef"), new BinTreeProperty[] { new BinTreeString(H("name"), name), value });

    private static BinTreeObject Material(string path, BinTreeEmbedded[] samplers, BinTreeEmbedded[] pars)
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
        return new BinTreeObject(H(path), H("StaticMaterialDef"), new BinTreeProperty[]
        {
            new BinTreeString(H("name"), path),
            new BinTreeUnorderedContainer(H("samplerValues"), BinPropertyType.Embedded, samplers.ToList()),
            new BinTreeUnorderedContainer(H("paramValues"), BinPropertyType.Embedded, pars.ToList()),
            new BinTreeUnorderedContainer(H("techniques"), BinPropertyType.Embedded, new List<BinTreeEmbedded> { tech }),
        });
    }

    private static byte[] Bin()
    {
        var rock = Material(MatPath,
            new[] { Sampler("Diffuse_Texture", "assets/maps/rock_d.tex"), Sampler("Mask_Texture", "assets/maps/rock_m.tex"), Sampler("Orphan", "assets/maps/rock_o.tex") },
            new[]
            {
                Param("TintColor", new BinTreeVector4(H("value"), new Vector4(1f, 0.5f, 0.25f, 1f))),
                Param("Scale", new BinTreeVector4(H("value"), new Vector4(2f, 0, 0, 0))),
                Param("Amount", new BinTreeF32(H("value"), 0.5f)),
                Param("Offset2", new BinTreeVector2(H("value"), new Vector2(3f, 4f))),
            });
        var other = Material(OtherPath, new[] { Sampler("Diffuse_Texture", "assets/maps/other_d.tex") },
            new[] { Param("TintColor", new BinTreeVector4(H("value"), Vector4.One)) });
        using var ms = new MemoryStream();
        new BinTree(new[] { rock, other }, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    /// <summary>What reflection would say about the shader: three 2D texture inputs and a cube one; five constants.</summary>
    private static MaterialGraphShaderInfo Info() => new()
    {
        ShaderName = ShaderPath,
        Textures = new ShaderTextureInput[]
        {
            new("Diffuse_Texture__TX", 0, "tex2d", "PS"),
            new("Mask_Texture__TX", 1, "tex2d", "PS"),
            new("Normal_Texture__TX", 2, "tex2d", "PS"),
            new("Detail_Texture__TX", 3, "tex2d", "PS"),
            new("Env_Cube__TX", 4, "texcube", "PS"),
        },
        Constants = new ShaderConstantInput[]
        {
            new("TintColor", "Material", "float4", true, "PS"),
            new("Scale", "Material", "float4", true, "PS"),
            new("Amount", "Material", "float", true, "PS"),
            new("Offset2", "Material", "float2", true, "PS"),
            new("Strength", "Material", "float", true, "PS"),
            new("Shift", "Material", "float2", true, "PS"),
            new("Fade", "Material", "float4", true, "PS"),
        },
        ParameterDefaults = new Dictionary<string, float[]> { ["Strength"] = new[] { 0.75f }, ["Shift"] = new[] { 0.25f, -0.5f }, ["Fade"] = new[] { 1f, 1f, 1f, 1f } },
        TextureDefaults = new Dictionary<string, string> { ["Detail_Texture"] = "assets/maps/detail_default.tex" },
    };

    private sealed class Rig : IDisposable
    {
        public UndoRedoService Undo { get; } = new();
        public MaterialEditorViewModel Editor { get; }
        public MaterialGraphViewModel Graph { get; } = new(null);
        public MaterialBindingViewModel Material => Editor.Materials.First();
        public byte[] Baseline { get; }

        public Rig()
        {
            Editor = new MaterialEditorViewModel { UndoService = Undo };
            var bytes = Bin();
            Editor.Load(MaterialDocument.Parse(bytes, Resolve), new WadAssetEntry { Path = "maps/test/rock.materials.bin", IsResolved = true }, bytes);
            Baseline = Editor.Serialize()!;
            Graph.ShaderInfoSource = _ => Info();
            Graph.ShowMaterial(Material.Model, null, null, Editor);
        }

        public MaterialGraph G => Graph.Graph!;
        public GraphNode Shader => G.ShaderNode!;
        public int Pin(string name) => Shader.Inputs.ToList().FindIndex(p => p.Name == name);
        public GraphNode Node(string source) => G.Nodes.First(n => n.Source == source && n.State != GraphNodeState.ShaderDefault);
        public GraphNode AnyNode(string source) => G.Nodes.First(n => n.Source == source);
        public bool Connect(string source, string pin, bool move = false) => Graph.Connect(Node(source).Id, "shader", Pin(pin), move);
        public IEnumerable<string> Samplers() => Material.Model.Slots.Select(s => s.SamplerName);
        public IEnumerable<string> Params() => Material.Model.Parameters.Select(p => p.Name);
        public void Undone() { while (Undo.CanUndo) Undo.Undo(); }

        public void Dispose() => Graph.Dispose();
    }

    // ===================================================================== the graph describes its inputs

    [Fact]
    public void TheShaderNodeListsEveryTextureInputAndEveryUsedMaterialConstant_UnauthoredOnesAsDashedPinsWithTheirTargetName()
    {
        using var rig = new Rig();
        Assert.True(rig.Graph.CanWire);
        string[] names = rig.Shader.Inputs.Select(p => p.Name).ToArray();
        foreach (var expected in new[] { "Diffuse_Texture", "Normal_Texture", "Detail_Texture", "Env_Cube", "TintColor", "Strength", "Shift", "Fade" })
            Assert.Contains(expected, names);
        var strength = rig.Shader.Inputs[rig.Pin("Strength")];
        Assert.Equal("Strength", strength.Target);
        Assert.Equal(1, strength.Components);
        Assert.False(strength.Linked);
        Assert.Equal("texcube", rig.Shader.Inputs[rig.Pin("Env_Cube")].Dimension);
        Assert.Equal(4, rig.G.Nodes.First(n => n.Source == "TintColor").Outputs[0].Components);
        Assert.Equal("tex2d", rig.Node("Diffuse_Texture").Outputs[0].Dimension);
        Assert.Equal(3, rig.G.Nodes.Count(n => n.Kind == GraphNodeKind.Texture && n.State != GraphNodeState.ShaderDefault));
    }

    // ===================================================================== connect

    [Fact]
    public void ANodeThatFeedsNothingIsRenamedToTheInputItIsWiredTo_OneUndoStep_ByteIdentical()
    {
        using var rig = new Rig();
        Assert.Equal(GraphNodeState.Unused, rig.Node("Orphan").State);   // the shader declares no 'Orphan'
        int steps = rig.Undo.UndoHistory.Count;

        Assert.True(rig.Connect("Orphan", "Normal_Texture"), rig.Graph.EditStatus);
        Assert.Equal(new[] { "Diffuse_Texture", "Mask_Texture", "Normal_Texture" }, rig.Samplers());          // renamed in place, no copy
        Assert.Equal("assets/maps/rock_o.tex", rig.Material.Model.Slots.Single(s => s.SamplerName == "Normal_Texture").Path);
        Assert.True(rig.Shader.Inputs.First(p => p.Name == "Normal_Texture").Linked);
        Assert.Equal(steps + 1, rig.Undo.UndoHistory.Count);
        Assert.True(rig.Editor.IsDirty);
        Assert.Contains("Normal_Texture", rig.Material.Slots.Select(s => s.SamplerName));                       // the Material tab's rows follow

        Assert.True(rig.Graph.UndoCommand.CanExecute(null));
        rig.Graph.UndoCommand.Execute(null);
        Assert.Equal(new[] { "Diffuse_Texture", "Mask_Texture", "Orphan" }, rig.Samplers());
        Assert.Equal(rig.Baseline, rig.Editor.Serialize());
        Assert.Contains("Orphan", rig.Material.Slots.Select(s => s.SamplerName));
        rig.Graph.RedoCommand.Execute(null);
        Assert.Equal(new[] { "Diffuse_Texture", "Mask_Texture", "Normal_Texture" }, rig.Samplers());
    }

    [Fact]
    public void ANodeThatAlreadyFeedsAnInputIsCopiedToASecondInput_KeepingItsAddressModes_OneUndoStep()
    {
        using var rig = new Rig();
        int steps = rig.Undo.UndoHistory.Count;
        Assert.True(rig.Connect("Diffuse_Texture", "Normal_Texture"), rig.Graph.EditStatus);   // output pin -> a second input: one output feeds many
        Assert.Equal(new[] { "Diffuse_Texture", "Mask_Texture", "Orphan", "Normal_Texture" }, rig.Samplers());
        var copy = rig.Material.Model.Slots.Single(s => s.SamplerName == "Normal_Texture");
        Assert.Equal("assets/maps/rock_d.tex", copy.Path);
        Assert.Equal((int?)1, copy.AddressW);                                                          // the whole element was cloned
        Assert.Equal("assets/maps/rock_d.tex", rig.Material.Model.Slots.Single(s => s.SamplerName == "Diffuse_Texture").Path);
        Assert.Equal(steps + 1, rig.Undo.UndoHistory.Count);
        rig.Graph.UndoCommand.Execute(null);
        Assert.Equal(rig.Baseline, rig.Editor.Serialize());
        Assert.Equal(3, rig.Material.Slots.Count);
    }

    [Fact]
    public void ConnectingToAPinThatHasALinkReplacesIt_AndMovingAnEndRenamesTheEntry_EachOneStep()
    {
        using var rig = new Rig();
        // Mask_Texture -> Diffuse_Texture replaces the sampler that fed Diffuse (copy: the mask keeps feeding its own input)
        Assert.True(rig.Connect("Mask_Texture", "Diffuse_Texture"), rig.Graph.EditStatus);
        Assert.Equal(new[] { "Mask_Texture", "Orphan", "Diffuse_Texture" }, rig.Samplers());
        Assert.Equal("assets/maps/rock_m.tex", rig.Material.Model.Slots.Single(s => s.SamplerName == "Diffuse_Texture").Path);
        Assert.Single(rig.Undo.UndoHistory);
        rig.Graph.UndoCommand.Execute(null);
        Assert.Equal(rig.Baseline, rig.Editor.Serialize());                                      // the removed element came back where it was

        // move: pick Mask's link up by its input end and drop it on Detail_Texture
        Assert.True(rig.Connect("Mask_Texture", "Detail_Texture", move: true), rig.Graph.EditStatus);
        Assert.Equal(new[] { "Diffuse_Texture", "Detail_Texture", "Orphan" }, rig.Samplers());
        Assert.False(rig.Shader.Inputs[rig.Pin("Mask_Texture")].Linked);                          // the old input falls back to the shader default
        rig.Graph.UndoCommand.Execute(null);
        Assert.Equal(rig.Baseline, rig.Editor.Serialize());
    }

    [Fact]
    public void AParameterIsCopiedOrMovedByNameWithItsValue_AndAnInputTakesOneLink()
    {
        using var rig = new Rig();
        // Scale (float4) -> Fade (float4, unauthored): a copy
        Assert.True(rig.Connect("Scale", "Fade"), rig.Graph.EditStatus);
        Assert.Equal(new[] { "TintColor", "Scale", "Amount", "Offset2", "Fade" }, rig.Params());
        Assert.Equal("2, 0, 0, 0", rig.Material.Model.Parameters.Single(p => p.Name == "Fade").CurrentText);
        rig.Graph.UndoCommand.Execute(null);
        Assert.Equal(rig.Baseline, rig.Editor.Serialize());

        // Amount (float) moved to Strength (float): renamed, and any entry already named Strength would be replaced
        Assert.True(rig.Connect("Amount", "Strength", move: true), rig.Graph.EditStatus);
        Assert.Equal(new[] { "TintColor", "Scale", "Strength", "Offset2" }, rig.Params());
        Assert.Equal("0.5", rig.Material.Model.Parameters.Single(p => p.Name == "Strength").CurrentText);
        rig.Graph.UndoCommand.Execute(null);
        Assert.Equal(rig.Baseline, rig.Editor.Serialize());

        // TintColor -> Scale replaces Scale
        Assert.True(rig.Connect("TintColor", "Scale"), rig.Graph.EditStatus);
        Assert.Equal(new[] { "TintColor", "Offset2", "Amount", "Scale" }.OrderBy(x => x), rig.Params().OrderBy(x => x));
        Assert.StartsWith("1, 0.5, 0.25", rig.Material.Model.Parameters.Single(p => p.Name == "Scale").CurrentText);
        Assert.Single(rig.Undo.UndoHistory);
        rig.Graph.UndoCommand.Execute(null);
        Assert.Equal(rig.Baseline, rig.Editor.Serialize());
        Assert.Equal(new[] { "TintColor", "Scale", "Amount", "Offset2" }, rig.Params());
    }

    // ===================================================================== validation

    [Fact]
    public void ATypeMismatchIsRefusedWithAReason_AndNothingChanges()
    {
        using var rig = new Rig();
        int steps = rig.Undo.UndoHistory.Count;

        // components: float4 -> float2 and float -> float4
        var a = rig.Graph.CheckLink(rig.Node("Scale").Id, "shader", rig.Pin("Shift"));
        Assert.False(a.Ok);
        Assert.Contains("4 components", a.Reason);
        Assert.Contains("2-component", a.Reason);
        Assert.Contains("truncated", a.Reason);
        var b = rig.Graph.CheckLink(rig.Node("Amount").Id, "shader", rig.Pin("Fade"));
        Assert.False(b.Ok);

        // kinds: a texture into a constant, a parameter into a texture
        Assert.Contains("texture inputs only", rig.Graph.CheckLink(rig.Node("Diffuse_Texture").Id, "shader", rig.Pin("TintColor")).Reason);
        Assert.Contains("cannot feed", rig.Graph.CheckLink(rig.Node("TintColor").Id, "shader", rig.Pin("Diffuse_Texture")).Reason);

        // 2D into a cube input
        var cube = rig.Graph.CheckLink(rig.Node("Diffuse_Texture").Id, "shader", rig.Pin("Env_Cube"));
        Assert.False(cube.Ok);
        Assert.Contains("cube texture", cube.Reason);
        Assert.Contains("2D texture", cube.Reason);

        // the refused gesture says why and writes nothing
        Assert.False(rig.Graph.Connect(rig.Node("Scale").Id, "shader", rig.Pin("Shift"), move: false));
        Assert.Contains("2-component", rig.Graph.EditStatus);
        Assert.Equal(steps, rig.Undo.UndoHistory.Count);
        Assert.Equal(rig.Baseline, rig.Editor.Serialize());
        Assert.False(rig.Editor.IsDirty);
    }

    [Fact]
    public void TheOutputAndShaderNodesAreNotWirableOrDeletable()
    {
        using var rig = new Rig();
        var shaderId = rig.Shader.Id;
        Assert.False(rig.Graph.CheckLink("output", shaderId, 0).Ok);
        Assert.False(rig.Graph.DeleteNode("shader"));
        Assert.False(rig.Graph.DeleteNode("output"));
        Assert.Equal(rig.Baseline, rig.Editor.Serialize());
    }

    [Fact]
    public void ReadOnlyBlocksEveryGestureAndSaysWhy()
    {
        using var rig = new Rig();
        using var readOnly = new MaterialGraphViewModel(null) { ShaderInfoSource = _ => Info() };
        readOnly.ShowMaterial(rig.Material.Model, null, null, editor: null);
        Assert.False(readOnly.CanEdit);
        Assert.False(readOnly.CanWire);
        var g = readOnly.Graph!;
        Assert.False(readOnly.Connect(g.Nodes.First(n => n.Source == "Scale").Id, "shader", 0, false));
        Assert.StartsWith("Read-only", readOnly.EditStatus);
        Assert.False(readOnly.Disconnect(0));
        Assert.False(readOnly.CreateAt(0));
        Assert.False(readOnly.DeleteNode(g.Nodes.First(n => n.Source == "Scale").Id));
        Assert.False(readOnly.AddTextureSample());
        Assert.False(readOnly.CheckLink("a", "shader", 0).Ok);
        Assert.Equal(rig.Baseline, rig.Editor.Serialize());
    }

    // ===================================================================== disconnect / create / delete

    [Fact]
    public void DisconnectRemovesTheEntryAndTheShaderDefaultAppliesAgain_AndUndoRestoresTheElementInPlace()
    {
        using var rig = new Rig();
        Assert.True(rig.Graph.Disconnect(rig.Pin("Mask_Texture")), rig.Graph.EditStatus);
        Assert.Equal(new[] { "Diffuse_Texture", "Orphan" }, rig.Samplers());
        Assert.Contains("shader default", rig.Graph.EditStatus);
        Assert.False(rig.Shader.Inputs[rig.Pin("Mask_Texture")].Linked);
        Assert.Single(rig.Undo.UndoHistory);
        rig.Graph.UndoCommand.Execute(null);
        Assert.Equal(rig.Baseline, rig.Editor.Serialize());
        Assert.Equal(new[] { "Diffuse_Texture", "Mask_Texture", "Orphan" }, rig.Samplers());

        // nothing to disconnect on an unauthored input
        Assert.False(rig.Graph.Disconnect(rig.Pin("Normal_Texture")));
        Assert.Contains("not connected", rig.Graph.EditStatus);
    }

    [Fact]
    public void CreateHereAuthorsAnEntryWithTheShadersOwnDefault()
    {
        using var rig = new Rig();
        var open = rig.Graph.UnconnectedInputs();
        Assert.Contains(open, u => u.Name == "Normal_Texture" && u.IsTexture);
        Assert.Contains(open, u => u.Name == "Detail_Texture" && u.IsTexture);
        Assert.Contains(open, u => u.Name == "Strength" && !u.IsTexture);
        Assert.DoesNotContain(open, u => u.Name == "Diffuse_Texture");
        Assert.DoesNotContain(open, u => u.Name == "TintColor");

        // a texture input with a shader default texture
        Assert.True(rig.Graph.CreateAt(rig.Pin("Detail_Texture")), rig.Graph.EditStatus);
        var detail = rig.Material.Model.Slots.Single(s => s.SamplerName == "Detail_Texture");
        Assert.Equal("assets/maps/detail_default.tex", detail.Path);
        Assert.Equal(rig.G.Find(rig.Graph.SelectedNodeId!)!.Source, "Detail_Texture");            // the new node is selected
        // one without a shader default is refused: a sampler is never written with an empty path
        int stepsBefore = rig.Undo.UndoHistory.Count;
        Assert.False(rig.Graph.CreateAt(rig.Pin("Normal_Texture")));
        Assert.Contains("empty path", rig.Graph.EditStatus);
        Assert.Equal(stepsBefore, rig.Undo.UndoHistory.Count);
        // a constant, with the shader default value, in the type the input needs
        Assert.True(rig.Graph.CreateAt(rig.Pin("Shift")), rig.Graph.EditStatus);
        var shift = rig.Material.Model.Parameters.Single(p => p.Name == "Shift");
        Assert.Equal("Vector2", shift.TypeName);
        Assert.Equal("0.25, -0.5", shift.CurrentText);
        Assert.True(rig.Graph.CreateAt(rig.Pin("Strength")));
        Assert.Equal("F32", rig.Material.Model.Parameters.Single(p => p.Name == "Strength").TypeName);
        Assert.Equal("0.75", rig.Material.Model.Parameters.Single(p => p.Name == "Strength").CurrentText);
        Assert.Equal(3, rig.Undo.UndoHistory.Count);                                              // one step per gesture
        Assert.DoesNotContain(rig.Graph.UnconnectedInputs(), u => u.Name == "Shift");

        // already there
        Assert.False(rig.Graph.CreateAt(rig.Pin("Detail_Texture")));

        rig.Undone();
        Assert.Equal(rig.Baseline, rig.Editor.Serialize());
        Assert.Equal(3, rig.Material.Slots.Count);
        Assert.Equal(4, rig.Material.Parameters.Count);
    }

    [Fact]
    public void AddTextureSampleCopiesTheSelectedSample_NeverWritesAnEmptyPath_AndAddParameterMakesAnUnwiredEntry()
    {
        using var rig = new Rig();
        Assert.False(rig.Graph.AddTextureSample());                                                // nothing selected: refused, nothing written
        Assert.Contains("Select a texture sample", rig.Graph.EditStatus);
        Assert.Equal(rig.Baseline, rig.Editor.Serialize());

        rig.Graph.SelectedNodeId = rig.Node("Mask_Texture").Id;
        Assert.True(rig.Graph.AddTextureSample());
        Assert.True(rig.Graph.AddTextureSample());
        Assert.Equal("assets/maps/rock_m.tex", rig.Material.Model.Slots.Single(s => s.SamplerName == "Mask_Texture_Copy").Path);
        Assert.Contains("Mask_Texture_Copy_Copy", rig.Samplers());                                  // the second copy was made from the new selection
        Assert.DoesNotContain(rig.Material.Model.Slots, s => s.Path.Length == 0);
        Assert.True(rig.Graph.AddParameterNode());
        Assert.Equal("0, 0, 0, 0", rig.Material.Model.Parameters.Single(p => p.Name == "New_Parameter").CurrentText);
        Assert.Equal(GraphNodeState.Unused, rig.Node("New_Parameter").State);
        rig.Undone();
        Assert.Equal(rig.Baseline, rig.Editor.Serialize());
    }

    // ===================================================================== undo outlives the window / the material shown

    [Fact]
    public void UndoAfterTheGraphViewModelIsDisposedStillSyncsTheMaterialTabAndTheDirtyFlag()
    {
        var rig = new Rig();
        Assert.True(rig.Graph.Disconnect(rig.Pin("Mask_Texture")));
        Assert.Equal(2, rig.Material.Slots.Count);
        rig.Graph.Dispose();                                                                        // the window closed: the undo stack still holds the command
        rig.Undo.Undo();
        Assert.Equal(3, rig.Material.Model.Slots.Count);
        Assert.Equal(3, rig.Material.Slots.Count);                                                  // the Material tab's rows follow the model
        Assert.Equal(new[] { "Diffuse_Texture", "Mask_Texture", "Orphan" }, rig.Material.Slots.Select(s => s.SamplerName));
        Assert.Equal(rig.Baseline, rig.Editor.Serialize());   // (the document's structural-edit flag is sticky after an undo, as for Material-tab sampler undo)
        rig.Undo.Redo();
        Assert.Equal(2, rig.Material.Slots.Count);
        Assert.True(rig.Editor.IsDirty);
    }

    [Fact]
    public void UndoAfterTheGraphSwitchedToAnotherMaterialStillSyncsTheFirstMaterialsRows()
    {
        using var rig = new Rig();
        var first = rig.Material;
        Assert.True(rig.Graph.Disconnect(rig.Pin("Mask_Texture")));
        rig.Graph.ShowMaterial(rig.Editor.Materials[1].Model, null, null, rig.Editor);              // the window shows the other material now
        rig.Undo.Undo();
        Assert.Equal(new[] { "Diffuse_Texture", "Mask_Texture", "Orphan" }, first.Slots.Select(s => s.SamplerName));
        Assert.Equal(rig.Baseline, rig.Editor.Serialize());
    }

    // ===================================================================== repeats

    [Fact]
    public void DeletingOrMovingTheFirstOfTwoSameNamedSamplersTakesTheRepeatWithIt_OneUndoStep()
    {
        using var rig = new Rig();
        var mask = rig.Material.Model.Slots.Single(s => s.SamplerName == "Mask_Texture");
        Assert.NotNull(rig.Material.Model.DuplicateSampler(mask, "Mask_Texture"));                   // a same-named repeat the game would ignore
        rig.Material.ResyncRows();
        rig.Editor.NotifyChanged();
        byte[] withRepeat = rig.Editor.Serialize()!;
        Assert.Equal(2, rig.Samplers().Count(n => n == "Mask_Texture"));

        Assert.True(rig.Graph.DeleteNode(rig.Node("Mask_Texture").Id), rig.Graph.EditStatus);
        Assert.DoesNotContain("Mask_Texture", rig.Samplers());                                       // the repeat did not take the input over
        Assert.Single(rig.Undo.UndoHistory);
        rig.Graph.UndoCommand.Execute(null);
        Assert.Equal(withRepeat, rig.Editor.Serialize());

        Assert.True(rig.Graph.Connect(rig.Node("Mask_Texture").Id, "shader", rig.Pin("Detail_Texture"), move: true), rig.Graph.EditStatus);
        Assert.DoesNotContain("Mask_Texture", rig.Samplers());
        Assert.Contains("Detail_Texture", rig.Samplers());
        Assert.Single(rig.Undo.UndoHistory);
        rig.Graph.UndoCommand.Execute(null);
        Assert.Equal(withRepeat, rig.Editor.Serialize());
    }

    [Fact]
    public void DeletingANodeRemovesItsEntryAndTheSelectionFollowsTheEntryNotThePositionalId()
    {
        using var rig = new Rig();
        // select Offset2 (param:3), then delete TintColor (param:0) above it: ids shift, the selection must stay on Offset2
        rig.Graph.SelectedNodeId = rig.Node("Offset2").Id;
        Assert.Equal("param:3", rig.Graph.SelectedNodeId);
        Assert.True(rig.Graph.DeleteNode(rig.Node("TintColor").Id), rig.Graph.EditStatus);
        Assert.Equal(new[] { "Scale", "Amount", "Offset2" }, rig.Params());
        Assert.Equal("param:2", rig.Graph.SelectedNodeId);
        Assert.Equal("Offset2", rig.G.Find(rig.Graph.SelectedNodeId!)!.Source);
        var row = Assert.IsType<GraphParamEditRow>(Assert.Single(rig.Graph.EditRows));
        Assert.Equal("Offset2", row.Parameter.Name);

        rig.Graph.UndoCommand.Execute(null);                                                       // TintColor comes back, first
        Assert.Equal(rig.Baseline, rig.Editor.Serialize());
        Assert.Equal("param:3", rig.Graph.SelectedNodeId);                                         // and the selection is still on Offset2
        Assert.Equal("Offset2", rig.G.Find(rig.Graph.SelectedNodeId!)!.Source);

        // deleting the selected node clears the selection
        rig.Graph.SelectedNodeId = rig.Node("Amount").Id;
        Assert.True(rig.Graph.DeleteNode(rig.Graph.SelectedNodeId!));
        Assert.Null(rig.Graph.SelectedNodeId);
    }

    [Fact]
    public void RemovingTheLastSamplerAndPuttingItBackSurvivesASerializeInBetween()
    {
        // the live preview serialises between edits, and a serialise strips an empty container off its object (M414)
        using var rig = new Rig();
        var two = rig.Editor.Materials[1];
        using var g2 = new MaterialGraphViewModel(null) { ShaderInfoSource = _ => Info() };
        g2.ShowMaterial(two.Model, null, null, rig.Editor);
        Assert.True(g2.Disconnect(g2.Graph!.ShaderNode!.Inputs.ToList().FindIndex(p => p.Name == "Diffuse_Texture")));
        Assert.Empty(two.Model.Slots);
        byte[] stripped = rig.Editor.Serialize()!;                                                 // samplerValues is now empty and stripped
        Assert.NotEqual(rig.Baseline, stripped);
        Assert.True(g2.CreateAt(g2.Graph!.ShaderNode!.Inputs.ToList().FindIndex(p => p.Name == "Detail_Texture")), g2.EditStatus);   // must land on the object, not on the stripped container
        Assert.Contains(MaterialDocument.Parse(rig.Editor.Serialize()!, Resolve).Materials.Single(m => m.Name == OtherPath).Slots, s => s.SamplerName == "Detail_Texture");
        g2.UndoCommand.Execute(null);
        g2.UndoCommand.Execute(null);
        var back = MaterialDocument.Parse(rig.Editor.Serialize()!, Resolve).Materials.Single(m => m.Name == OtherPath);
        Assert.Equal("Diffuse_Texture", Assert.Single(back.Slots).SamplerName);
        Assert.Equal("assets/maps/other_d.tex", back.Slots[0].Path);
    }

    // ===================================================================== layout

    [Fact]
    public void AMovedNodeStaysWhereItWasPutAfterEveryRebuild_AndMovingItTouchesNoDocument()
    {
        using var rig = new Rig();
        var node = rig.Node("Mask_Texture");
        double x = node.X, y = node.Y;
        rig.Graph.MoveNode(node.Id, x + 123, y + 45);
        Assert.False(rig.Editor.IsDirty);
        Assert.Empty(rig.Undo.UndoHistory);
        Assert.Equal(rig.Baseline, rig.Editor.Serialize());

        Assert.True(rig.Graph.DeleteNode(rig.Node("Diffuse_Texture").Id));                         // a rebuild: Mask_Texture is tex:0 now
        var moved = rig.Node("Mask_Texture");
        Assert.Equal(x + 123, moved.X, 3);
        Assert.Equal(y + 45, moved.Y, 3);
        Assert.True(rig.G.Bounds.Width > 0);
    }
}
