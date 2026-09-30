using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.D3D11;
using Xunit.Abstractions;
using static ReyEngine.Formats.Tests.VfxComponentEmitterTests;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M800: what the Particle Editor says about a system authored in Riot's component (Shimmer) VFX format - the
/// note on its system card - and the D3D11 preview's "Nothing to draw" status, which replaces the renderer's
/// "render failed: no shader loaded" for a system that has no visual emitter and for nothing else.
///
/// <para>The synthetic bins come from <see cref="VfxComponentEmitterTests"/>, so the wording asserted here is
/// the wording asserted there. The frame loop itself needs a live window and a device; the UiProbe
/// "componentnote" card drives that half (see ParticleEditorView.Dx11.cs).</para>
/// </summary>
public sealed class ParticleEditorComponentNoteTests(ITestOutputHelper output)
{
    private static readonly Dictionary<uint, string> Names = new()
    {
        [VfxComponentFormat.ShimmerListField] = "ShimmerEmitterDefinitionData",
        [VfxComponentFormat.ComponentsField] = "VfxComponents",
        [Hash("emitterName")] = "emitterName",
        [Hash("disabled")] = "disabled",
    };

    private static uint Hash(string s) => HashAlgorithms.Fnv1a(s);

    private static ParticleEditorViewModel Open(IEnumerable<BinTreeStruct>? classic, IEnumerable<BinTreeStruct>? shimmer)
    {
        var vm = new ParticleEditorViewModel { ResolveBinName = h => Names.GetValueOrDefault(h) };
        Assert.True(vm.Load(new WadAssetEntry { Path = "particles.bin" }, Bin(classic, shimmer), editable: true));
        return vm;
    }

    // ------------------------------------------------------------------ the note

    [Fact]
    public void TheSystemCardCarriesTheNoteAndNoEmitterCardDoes()
    {
        var vm = Open(
            classic: new[] { Classic("comp1", disabled: true, components: true), Classic("comp2", components: true) },
            shimmer: new[] { Shimmer("s1", true), Shimmer("s2", true), Shimmer("s3", true) });

        var system = vm.Cards[0];
        Assert.True(system.IsSystemCard);
        Assert.True(system.HasComponentNote);
        Assert.Equal("This system uses Riot's component (Shimmer) VFX format, added in 16.16. ReyEngine does not "
                     + "simulate it. All 3 shipped Shimmer emitters here are disabled, and the client ignores the "
                     + "VfxComponents block on classic emitters, so the game draws nothing from them either.",
            system.ComponentNote);
        Assert.All(vm.Cards.Skip(1), c =>
        {
            Assert.False(c.HasComponentNote);
            Assert.Null(c.ComponentNote);
        });
        Assert.Equal(vm.ComponentNote, system.ComponentNote);
        Assert.True(vm.HasComponentNote);
    }

    [Fact]
    public void ASystemWithoutTheFormatHasNoNote()
    {
        var vm = Open(new[] { Classic("plain", texture: "ASSETS/t.dds") }, shimmer: null);
        Assert.False(vm.HasComponentNote);
        Assert.Null(vm.ComponentNote);
        Assert.Null(vm.SelectedComponentEmitters);
        Assert.All(vm.Cards, c => Assert.Null(c.ComponentNote));
    }

    [Fact]
    public void EnablingAShimmerEmitterInTheRawTreeChangesTheNoteAtOnce()
    {
        // "keep the data editable as today": the flag inside a Shimmer emitter is an ordinary row of the raw
        // tree, editing it re-extracts the definitions, and the note follows - it does not go stale.
        var vm = Open(new[] { Classic("comp", components: true) }, new[] { Shimmer("s1", true), Shimmer("s2", true) });
        var card = vm.Cards[0];
        Assert.Contains("All 2 shipped Shimmer emitters here are disabled", card.ComponentNote);

        var raised = new List<string?>();
        card.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        var row = card.Modules.SelectMany(m => m.Rows)
            .First(r => r.Name == "disabled" && r.Prop.Depth == 2 && !r.IsReadOnly);
        row.EditText = "false";
        vm.ApplyEditCommand.Execute(row);

        Assert.Null(row.ErrorText);
        Assert.Contains(nameof(card.ComponentNote), raised);
        Assert.Contains(nameof(card.HasComponentNote), raised);
        Assert.Equal(new VfxComponentEmitters(2, 1, 1).Note, card.ComponentNote);
        Assert.Contains("1 of 2 Shimmer emitters here is enabled", card.ComponentNote);
        Assert.DoesNotContain("draws nothing", card.ComponentNote);
    }

    [Fact]
    public void DeselectingTheSystemTakesTheNoteWithIt()
    {
        var vm = Open(classic: null, shimmer: new[] { Shimmer("s", true) });
        Assert.True(vm.HasComponentNote);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.SelectedSystem = null;

        Assert.False(vm.HasComponentNote);
        Assert.Null(vm.ComponentNote);
        Assert.Contains(nameof(vm.ComponentNote), raised);   // anything bound to it lets go
        Assert.Empty(vm.Cards);
    }

    // ------------------------------------------------------------------ the D3D11 status

    [Fact]
    public void ASystemAuthoredInTheComponentFormatReportsNothingToDrawNotARenderFailure()
    {
        var vm = Open(new[] { Classic("comp", components: true) }, new[] { Shimmer("s", true) });
        Assert.Equal("Nothing to draw - component (Shimmer) VFX is not simulated; the game "
                     + "draws nothing from it either.",
            vm.Dx11RenderFailedStatus(ShaderPreviewRenderer.NoShaderLoaded));
    }

    [Fact]
    public void AnyOtherSystemWithNothingToDrawSaysWhy()
    {
        Assert.Equal("Nothing to draw - this system has no emitters.",
            Open(classic: null, shimmer: null).Dx11RenderFailedStatus(ShaderPreviewRenderer.NoShaderLoaded));
        Assert.Equal("Nothing to draw - every emitter of this system is disabled.",
            Open(new[] { Classic("off", disabled: true, texture: "ASSETS/t.dds") }, shimmer: null)
                .Dx11RenderFailedStatus(ShaderPreviewRenderer.NoShaderLoaded));
        Assert.Equal("Nothing to draw - no enabled emitter names a texture or a mesh.",
            Open(new[] { Classic("bare") }, shimmer: null).Dx11RenderFailedStatus(ShaderPreviewRenderer.NoShaderLoaded));
    }

    [Fact]
    public void RealRenderFailuresKeepTheirMessage()
    {
        var empty = Open(new[] { Classic("comp", components: true) }, new[] { Shimmer("s", true) });
        // a device fault is not "nothing to draw", whatever the system holds
        Assert.Equal("render failed: device removed", empty.Dx11RenderFailedStatus("device removed"));
        Assert.Equal("render failed: zero-sized target", empty.Dx11RenderFailedStatus("zero-sized target"));

        // a system that DOES have a visual emitter whose pipelines all failed to build reads "no shader loaded"
        // for real - that is worth the red flag it always had
        var drawing = Open(new[] { Classic("plain", texture: "ASSETS/t.dds") }, shimmer: null);
        Assert.Equal("render failed: no shader loaded", drawing.Dx11RenderFailedStatus(ShaderPreviewRenderer.NoShaderLoaded));

        // and with nothing selected there is no system to be innocent: the renderer's words stand
        Assert.Equal("render failed: no shader loaded",
            new ParticleEditorViewModel().Dx11RenderFailedStatus(ShaderPreviewRenderer.NoShaderLoaded));
    }

    [Fact]
    public void TheRendererNamesTheErrorWithTheSymbolTheEditorCompares()
    {
        Assert.Equal("no shader loaded", ShaderPreviewRenderer.NoShaderLoaded);
    }

    [Fact]
    public void AnEmptyRendererReportsExactlyThatError()
    {
        // the pairing the mapping above depends on, against the real renderer: a device and no material is
        // refused with NoShaderLoaded and not with some other first-failing check
        using var renderer = new ShaderPreviewRenderer();
        if (!renderer.Initialize(out var why))
        {
            output.WriteLine("SKIPPED: no D3D11 device here: " + why);
            return;
        }
        var frame = renderer.RenderFrame(64, 64, new PreviewSettings(), out var error);
        Assert.Null(frame);
        Assert.Equal(ShaderPreviewRenderer.NoShaderLoaded, error);
        output.WriteLine("RAN on a real D3D11 device: an empty renderer returns '" + error + "'.");
    }

    [Fact]
    public void TheFrameLoopRoutesRenderFailuresThroughTheMapping()
    {
        // The frame loop needs a window and a device, so the UiProbe drives it; this pins the one line that
        // must not regress: the failing branch asks the view model for the line instead of writing its own.
        var src = Source("src", "ReyEngine.App", "Views", "ParticleEditorView.Dx11.cs");
        if (src is null) return;
        Assert.Contains("vm.Dx11RenderFailedStatus(why)", src);
        Assert.DoesNotContain("\"render failed: \" + why", src);
    }

    private static string? Source(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) continue;
            string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        return null;
    }
}
