using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Particles;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M752: the FORCES section of an emitter card. Adding, removing and editing are real edits and reach both
/// the saved file and the preview; Mute and Solo change only what the preview simulates.
/// </summary>
public sealed class ParticleForceEditorTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    /// <summary>One system with two emitters, the first disabled. The resolver keeps a disabled emitter in
    /// its list (the simulator skips it later), so a mapping that counted past it - the first version of
    /// this milestone did - would put every switch on the wrong emitter.</summary>
    private static byte[] Bin()
    {
        BinTreeStruct Emitter(string name, bool disabled) => new(0, H("VfxEmitterDefinitionData"),
            disabled
                ? new BinTreeProperty[] { new BinTreeString(H("emitterName"), name), new BinTreeBool(H("disabled"), true) }
                : new BinTreeProperty[] { new BinTreeString(H("emitterName"), name) });
        var system = new BinTreeObject(H("sys"), H("VfxSystemDefinitionData"), new BinTreeProperty[]
        {
            new BinTreeString(H("particleName"), "sys"),
            new BinTreeContainer(H("complexEmitterDefinitionData"), BinPropertyType.Struct, new BinTreeProperty[]
                { Emitter("off", disabled: true), Emitter("smoke", disabled: false) }),
        });
        using var ms = new MemoryStream();
        new BinTree(new[] { system }, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    private static ParticleEditorViewModel Open(bool editable = true)
    {
        var vm = new ParticleEditorViewModel();
        Assert.True(vm.Load(new WadAssetEntry { Path = "particles.bin" }, Bin(), editable));
        return vm;
    }

    /// <summary>The live card of the enabled emitter - the SYSTEM card is first, then the two emitters.</summary>
    private static ParticleEmitterCardViewModel Smoke(ParticleEditorViewModel vm) => vm.Cards.Single(c => c.Name == "smoke");

    private static VfxForceFields? PreviewForces(ParticleEditorViewModel vm) =>
        vm.Playback!.Items.Single().System.Emitters.Single(e => e.Name == "smoke").ForceFields;

    private static VfxForceFields? SavedForces(ParticleEditorViewModel vm) =>
        VfxSystemResolver.ExtractAll(vm.Document!.Serialize()).Values.Single().Emitters.Single(e => e.Name == "smoke").ForceFields;

    private static void Add(ParticleEditorViewModel vm, ParticleForceKind kind) =>
        Smoke(vm).AddForceCommand.Execute(ParticleEmitterCardViewModel.ForceKinds.Single(k => k.Kind == kind));

    [Fact]
    public void TheCardOffersAllFiveKindsAndAddingOneReachesThePreviewAndTheFile()
    {
        var vm = Open();
        var card = Smoke(vm);
        Assert.True(card.ShowForces);
        Assert.False(card.HasForces);
        Assert.Equal(5, card.ForceKindChoices.Count);
        Assert.False(vm.Cards.First().ShowForces);   // the SYSTEM card has no forces

        Add(vm, ParticleForceKind.Noise);

        var rebuilt = Smoke(vm);
        Assert.Equal("Noise 1", Assert.Single(rebuilt.Forces).Title);
        Assert.Equal("FORCES (1)", rebuilt.ForcesHeader);
        Assert.Single(PreviewForces(vm)!.Noise);
        Assert.Single(SavedForces(vm)!.Noise);
    }

    [Fact]
    public void AValueTypedInTheCardIsApplied()
    {
        var vm = Open();
        Add(vm, ParticleForceKind.Acceleration);
        var value = Smoke(vm).Forces.Single().Values.Single();
        value.Text = "0, -250, 0";
        value.ApplyCommand.Execute(null);

        Assert.Equal(new Vector3(0, -250, 0), SavedForces(vm)!.Acceleration.Single().Acceleration);
        Assert.Equal(new Vector3(0, -250, 0), PreviewForces(vm)!.Acceleration.Single().Acceleration);
        Assert.Equal("0, -250, 0", Smoke(vm).Forces.Single().Values.Single().Text);
    }

    [Theory]
    [InlineData("1, 2")]      // a vector needs three
    [InlineData("a, b, c")]
    [InlineData("1, 2, NaN")]
    public void ABadValueIsRefusedWithAReasonAndNothingIsWritten(string text)
    {
        var vm = Open();
        Add(vm, ParticleForceKind.Acceleration);
        var value = Smoke(vm).Forces.Single().Values.Single();
        value.Text = text;
        value.ApplyCommand.Execute(null);

        Assert.True(value.HasError);
        Assert.Equal(new Vector3(0, 40, 0), SavedForces(vm)!.Acceleration.Single().Acceleration);
    }

    [Fact]
    public void GermanDecimalCommasDoNotSplitAScalar()
    {
        // the app runs on a German locale; the fields are invariant, so "0.5" is one half, not two numbers
        Assert.True(ParticleForceValueViewModel.TryParse("0.5", isVector: false, out var v, out _));
        Assert.Equal(0.5f, v.X);
        Assert.False(ParticleForceValueViewModel.TryParse("0,5", isVector: false, out _, out _));
    }

    [Fact]
    public void MuteSilencesAForceInThePreviewOnly()
    {
        var vm = Open();
        Add(vm, ParticleForceKind.Drag);
        Add(vm, ParticleForceKind.Noise);

        Smoke(vm).Forces.Single(f => f.Force.Kind == ParticleForceKind.Noise).IsMuted = true;

        Assert.Empty(PreviewForces(vm)!.Noise);    // the preview no longer simulates it...
        Assert.Single(PreviewForces(vm)!.Drag);
        Assert.Single(SavedForces(vm)!.Noise);      // ...but the file still has it
    }

    [Fact]
    public void SoloLeavesOnlyTheSoloedForcesActing()
    {
        var vm = Open();
        Add(vm, ParticleForceKind.Drag);
        Add(vm, ParticleForceKind.Noise);
        Add(vm, ParticleForceKind.Attraction);

        Smoke(vm).Forces.Single(f => f.Force.Kind == ParticleForceKind.Attraction).IsSoloed = true;
        Assert.True(vm.AnyForceSoloed);

        var preview = PreviewForces(vm)!;
        Assert.Single(preview.Attraction);
        Assert.Empty(preview.Drag);
        Assert.Empty(preview.Noise);
        var saved = SavedForces(vm)!;
        Assert.Equal(3, saved.Drag.Count + saved.Noise.Count + saved.Attraction.Count);
    }

    [Fact]
    public void TheSwitchesSurviveAValueEditButNotAStructuralOne()
    {
        var vm = Open();
        Add(vm, ParticleForceKind.Drag);
        Add(vm, ParticleForceKind.Drag);
        Smoke(vm).Forces[1].IsMuted = true;

        // a value edit keeps the mute: the card is rebuilt, the key is not
        var v = Smoke(vm).Forces[0].Values.First();
        v.Text = "5";
        v.ApplyCommand.Execute(null);
        Assert.True(Smoke(vm).Forces[1].IsMuted);
        Assert.Single(PreviewForces(vm)!.Drag);

        // removing the first shifts the second into its place - a kept mute would now silence the wrong force
        Smoke(vm).Forces[0].RemoveCommand.Execute(null);
        Assert.False(Smoke(vm).Forces.Single().IsMuted);
        Assert.Single(PreviewForces(vm)!.Drag);
    }

    [Fact]
    public void ARemovedForceLeavesTheFileAndThePreview()
    {
        var vm = Open();
        Add(vm, ParticleForceKind.Orbital);
        Smoke(vm).Forces.Single().RemoveCommand.Execute(null);
        Assert.False(Smoke(vm).HasForces);
        Assert.Null(SavedForces(vm));
        Assert.Null(PreviewForces(vm));
    }

    [Fact]
    public void AReadOnlyBinRefusesTheEdit()
    {
        var vm = Open(editable: false);
        string? error = null;
        vm.Error = e => error = e;
        Add(vm, ParticleForceKind.Noise);
        Assert.NotNull(error);
        Assert.Null(SavedForces(vm));
        Assert.False(Smoke(vm).Forces.Count > 0);
    }
}
