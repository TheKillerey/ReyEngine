using ReyEngine.App.ViewModels;
using ReyEngine.Formats.Materials.ShaderGraph;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M836: a wired pin's own constant is shown as ignored (canvas pin and Details row), and "+ New texture..." on a Texture Sample creates the custom texture its label
/// names, on every assigned material, as one undo step on each stack.
/// </summary>
public sealed class ShaderGraphDisplayFixesTests : IDisposable
{
    private const string Flat = "Shaders/StaticMesh/DefaultEnv_Flat";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "reyengine-m836-" + Guid.NewGuid().ToString("N"));

    public ShaderGraphDisplayFixesTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private ShaderGraphViewModel Vm()
    {
        var vm = new ShaderGraphViewModel(() => _root)
        {
            BaseResolver = shader => SgBase.IsSupported(shader) ? (ShaderGraphTests.TestBase(), "") : (null, "not a supported base"),
            CompileDelay = TimeSpan.Zero,
        };
        Assert.True(vm.NewFromTemplate(Flat, SgTemplates.Empty, out string err), err);
        return vm;
    }

    private static SgPaletteEntry Entry(ShaderGraphViewModel vm, string title) => vm.Palette.First(p => p.Title == title);

    [Fact]
    public void A_wired_pin_that_still_stores_a_constant_shows_it_as_ignored()
    {
        var vm = Vm();
        string tex = vm.AddNode(Entry(vm, "Texture Sample: DiffuseTexture"), 100, 100)!;
        string mul = vm.AddNode(Entry(vm, "Multiply"), 300, 100)!;
        Assert.True(vm.SetProp(mul, "B", "3"));

        var before = vm.Graph!.Find(mul)!.Inputs.First(p => p.Name == "B");
        Assert.Equal("", before.IgnoredConstant);                                     // not wired: the constant is in use

        Assert.True(vm.Connect(tex, "RGB", mul, "B"));
        var pin = vm.Graph!.Find(mul)!.Inputs.First(p => p.Name == "B");
        Assert.True(pin.Linked);
        Assert.Equal("3", pin.IgnoredConstant);
        Assert.Equal("", vm.Graph.Find(mul)!.Inputs.First(p => p.Name == "A").IgnoredConstant);   // A was never set

        vm.SelectedNodeId = mul;
        var row = vm.PropRows.First(r => r.Label.StartsWith("B"));
        Assert.True(row.IsOverridden);
        Assert.Contains("constant 3 ignored", row.Hint);
        Assert.True(row.RowOpacity < 1.0);
    }

    [Fact]
    public void New_texture_on_a_sample_node_is_named_after_its_label_and_follows_the_materials_in_one_step_each()
    {
        var vm = Vm();
        string start = ShaderGraphJson.Serialize(vm.Document!);
        string tex = vm.AddNode(Entry(vm, "Texture Sample: DiffuseTexture"), 100, 100)!;
        Assert.True(vm.SetLabel(tex, "Glow texture"));
        var calls = new List<(string Name, bool Add)>();
        vm.AddTextureToMaterials = (_, t, add) => { calls.Add((t.Name, add)); return (2, ""); };

        // the Details row says what the node really samples, and offers the new-texture choice and button
        vm.SelectedNodeId = tex;
        var row = vm.PropRows.First(r => r.Label == "Texture");
        Assert.Contains(ShaderGraphViewModel.NewTextureChoice, row.Choices);
        Assert.True(row.HasAction);
        Assert.Contains("samples", row.Hint);
        string sampled = vm.Document.Find(tex)!.Prop("texture");

        row.Text = ShaderGraphViewModel.NewTextureChoice;                           // picking it from the list
        var made = Assert.Single(vm.Document.Textures);
        Assert.Equal("GlowTexture", made.Name);
        Assert.Equal("GlowTexture", vm.Document.Find(tex)!.Prop("texture"));
        Assert.Equal(new[] { ("GlowTexture", true) }, calls);

        Assert.True(vm.Undo());                                                     // ONE graph step, and the materials lose it again
        Assert.Empty(vm.Document.Textures);
        Assert.Equal(sampled, vm.Document.Find(tex)!.Prop("texture"));
        Assert.Equal(new[] { ("GlowTexture", true), ("GlowTexture", false) }, calls);
        Assert.True(vm.Redo());
        Assert.Equal("GlowTexture", vm.Document.Find(tex)!.Prop("texture"));

        // the button makes the next one, unique
        vm.SelectedNodeId = tex;
        Assert.Equal("GlowTexture2", vm.NewTextureForNode(tex));
        for (int i = 0; i < 4; i++) Assert.True(vm.Undo(), "undo " + i);   // the second texture, the first, the label, the node
        Assert.Equal(start, ShaderGraphJson.Serialize(vm.Document));
    }

    [Theory]
    [InlineData("", "CustomTexture")]
    [InlineData("glow  texture", "GlowTexture")]
    [InlineData("1st pass!", "StPass")]
    [InlineData("Texture1", "CustomTexture")]   // looks like an HLSL texture type: not usable
    public void A_label_is_made_into_a_valid_texture_name(string label, string expected)
    {
        var vm = Vm();
        Assert.Equal(expected, vm.TextureNameFromLabel(label, vm.Document!));
    }
}
