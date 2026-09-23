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
/// M753: the particle preview's Move handle - on an emitter's EmitterPosition or a positional force's
/// Position - and the force shapes drawn with it. The handle follows the pointer; the file is written once,
/// on release.
/// </summary>
public sealed class ParticleGizmoTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    /// <summary>One system, one emitter, authored at (10, 20, 30).</summary>
    private static byte[] Bin()
    {
        var position = new BinTreeEmbedded(H("EmitterPosition"), H("ValueVector3"), new BinTreeProperty[]
            { new BinTreeVector3(H("constantValue"), new Vector3(10, 20, 30)) });
        var emitter = new BinTreeStruct(0, H("VfxEmitterDefinitionData"), new BinTreeProperty[]
            { new BinTreeString(H("emitterName"), "smoke"), position });
        var system = new BinTreeObject(H("sys"), H("VfxSystemDefinitionData"), new BinTreeProperty[]
        {
            new BinTreeString(H("particleName"), "sys"),
            new BinTreeContainer(H("complexEmitterDefinitionData"), BinPropertyType.Struct, new BinTreeProperty[] { emitter }),
        });
        using var ms = new MemoryStream();
        new BinTree(new[] { system }, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    private static ParticleEditorViewModel Open(bool editable = true)
    {
        var vm = new ParticleEditorViewModel();
        Assert.True(vm.Load(new WadAssetEntry { Path = "particles.bin" }, Bin(), editable));
        vm.RigMode = VfxRigMode.Still;   // the name guess could open on anything; the tests pin the rig
        return vm;
    }

    private static ParticleEmitterCardViewModel Smoke(ParticleEditorViewModel vm) => vm.Cards.Single(c => c.Name == "smoke");

    private static VfxEmitterDefinition Saved(ParticleEditorViewModel vm) =>
        VfxSystemResolver.ExtractAll(vm.Document!.Serialize()).Values.Single().Emitters.Single();

    private static Vector3 Lift(ParticleEditorViewModel vm) => new(0, (float)vm.RigHeight, 0);

    private static void Add(ParticleEditorViewModel vm, ParticleForceKind kind) =>
        Smoke(vm).AddForceCommand.Execute(ParticleEmitterCardViewModel.ForceKinds.Single(k => k.Kind == kind));

    [Fact]
    public void MoveOnAnEmitterPutsTheHandleWhereThePreviewDrawsIt()
    {
        var vm = Open();
        Assert.Null(vm.GizmoPivot);

        Smoke(vm).MoveCommand.Execute(null);

        Assert.True(Smoke(vm).IsGizmoTarget);
        // a still rig is a translation by its height, and the simulator places the emitter at
        // Transform(EmitterPosition, pose) - so that is where the handle has to stand
        var pose = vm.Rig.Pose(0f);
        Assert.Equal(Vector3.Transform(new Vector3(10, 20, 30), pose), vm.GizmoPivot);

        Smoke(vm).MoveCommand.Execute(null);   // a second click takes it off
        Assert.Null(vm.GizmoPivot);
        Assert.False(Smoke(vm).IsGizmoTarget);
    }

    [Fact]
    public void ADragFollowsThePointerAndWritesOnceOnRelease()
    {
        var vm = Open();
        Smoke(vm).MoveCommand.Execute(null);
        var start = vm.GizmoPivot!.Value;

        vm.DragGizmoTo(start + new Vector3(50, 0, 0));
        vm.DragGizmoTo(start + new Vector3(120, 0, 0));
        Assert.Equal(start + new Vector3(120, 0, 0), vm.GizmoPivot);
        Assert.Equal(new Vector3(10, 20, 30), Saved(vm).EmitterPosition.Constant);   // nothing written yet
        Assert.False(vm.Document!.IsDirty);

        vm.EndGizmoDrag();

        Assert.Equal(new Vector3(130, 20, 30), Saved(vm).EmitterPosition.Constant);
        Assert.Equal(new Vector3(130, 20, 30), vm.Playback!.Items.Single().System.Emitters.Single().EmitterPosition.Constant);
        Assert.Equal(start + new Vector3(120, 0, 0), vm.GizmoPivot);   // the handle stays where it was let go
        Assert.True(Smoke(vm).IsGizmoTarget);                          // and on the rebuilt card
    }

    [Fact]
    public void ACancelledDragWritesNothing()
    {
        var vm = Open();
        Smoke(vm).MoveCommand.Execute(null);
        var start = vm.GizmoPivot!.Value;
        vm.DragGizmoTo(start + new Vector3(0, 80, 0));
        vm.CancelGizmoDrag();
        Assert.Equal(start, vm.GizmoPivot);
        Assert.Equal(new Vector3(10, 20, 30), Saved(vm).EmitterPosition.Constant);
        Assert.False(vm.Document!.IsDirty);
    }

    [Fact]
    public void APositionalForceMovesRelativeToTheSystemNotTheEmitter()
    {
        var vm = Open();
        Add(vm, ParticleForceKind.Attraction);
        var force = Smoke(vm).Forces.Single();
        Assert.True(force.CanMove);

        force.MoveCommand.Execute(null);
        Assert.True(Smoke(vm).Forces.Single().IsGizmoTarget);
        // a new Attraction has no Position (Riot omits a zero one), so its centre is the system origin -
        // NOT the emitter's (10, 20, 30)
        Assert.Equal(Lift(vm), vm.GizmoPivot);

        vm.DragGizmoTo(Lift(vm) + new Vector3(0, 0, -75));
        vm.EndGizmoDrag();

        Assert.Equal(new Vector3(0, 0, -75), Saved(vm).ForceFields!.Attraction.Single().Position);
        Assert.Equal(new Vector3(10, 20, 30), Saved(vm).EmitterPosition.Constant);   // the emitter did not move
        Assert.Equal("0, 0, -75", Smoke(vm).Forces.Single().Values.Single(v => v.Label == "Position").Text);
    }

    [Fact]
    public void AForceWithoutAPositionOffersNoHandle()
    {
        var vm = Open();
        Add(vm, ParticleForceKind.Acceleration);
        var force = Smoke(vm).Forces.Single();
        Assert.False(force.CanMove);
        force.MoveCommand.Execute(null);
        Assert.Null(vm.GizmoPivot);
    }

    [Fact]
    public void AMovingRigHasNoHandleAndSaysWhy()
    {
        var vm = Open();
        Smoke(vm).MoveCommand.Execute(null);
        vm.RigMode = VfxRigMode.Missile;
        Assert.Null(vm.GizmoPivot);
        Assert.Contains("Still and Burst", vm.GizmoNote);
        Assert.Null(vm.ForceShapeLines);

        vm.RigMode = VfxRigMode.Burst;   // the target was kept, so the handle comes back
        Assert.NotNull(vm.GizmoPivot);
    }

    [Fact]
    public void TheRigHeightLiftsTheHandle()
    {
        var vm = Open();
        Smoke(vm).MoveCommand.Execute(null);
        vm.RigHeight = 250;
        Assert.Equal(new Vector3(10, 270, 30), vm.GizmoPivot);
    }

    [Fact]
    public void RemovingAForceTakesTheHandleOffItsNeighbour()
    {
        var vm = Open();
        Add(vm, ParticleForceKind.Drag);
        Add(vm, ParticleForceKind.Drag);
        Smoke(vm).Forces[1].MoveCommand.Execute(null);
        Assert.NotNull(vm.GizmoPivot);

        // removing Drag 1 would shift Drag 2 into its key; a kept handle would then be on the wrong force
        Smoke(vm).Forces[0].RemoveCommand.Execute(null);
        Assert.Null(vm.GizmoTarget);
        Assert.Null(vm.GizmoPivot);
    }

    [Fact]
    public void AReadOnlyBinPutsTheHandleBack()
    {
        var vm = Open(editable: false);
        string? error = null;
        vm.Error = e => error = e;
        Smoke(vm).MoveCommand.Execute(null);   // a handle may be looked at...
        var start = vm.GizmoPivot!.Value;
        vm.DragGizmoTo(start + new Vector3(40, 0, 0));
        vm.EndGizmoDrag();                      // ...but not written
        Assert.NotNull(error);
        Assert.Equal(start, vm.GizmoPivot);
        Assert.Equal(new Vector3(10, 20, 30), Saved(vm).EmitterPosition.Constant);
    }

    [Fact]
    public void TheShapesDrawEveryActingForceAndNothingMuted()
    {
        var vm = Open();
        Assert.Null(vm.ForceShapeLines);
        Add(vm, ParticleForceKind.Drag);   // radius 1000
        // three great circles of 48 segments and a centre cross, as xyz line pairs
        int ball = (48 * 3 + 3) * 6;
        Assert.Equal(ball, vm.ForceShapeLines!.Length);
        // the ball is centred on the force (the system origin), lifted by the rig
        var xs = Enumerable.Range(0, vm.ForceShapeLines.Length / 3).Select(i => vm.ForceShapeLines[i * 3]).ToList();
        Assert.Equal(1000f, xs.Max(), 2);
        Assert.Equal(-1000f, xs.Min(), 2);

        Add(vm, ParticleForceKind.Acceleration);   // an arrow: the shaft and four barbs
        Assert.Equal(ball + 5 * 6, vm.ForceShapeLines!.Length);

        Smoke(vm).Forces.Single(f => f.Force.Kind == ParticleForceKind.Drag).IsMuted = true;
        Assert.Equal(5 * 6, vm.ForceShapeLines!.Length);

        vm.ShowForceShapes = false;
        Assert.Null(vm.ForceShapeLines);
    }

    [Fact]
    public void DraggingAnEmitterCarriesItsArrowButNotItsDrag()
    {
        var vm = Open();
        Add(vm, ParticleForceKind.Acceleration);
        Smoke(vm).MoveCommand.Execute(null);
        var before = vm.ForceShapeLines!.ToArray();
        vm.DragGizmoTo(vm.GizmoPivot!.Value + new Vector3(0, 100, 0));
        var during = vm.ForceShapeLines!;
        // the arrow starts at the emitter, so the live shapes move with the handle before anything is written
        Assert.Equal(before[1] + 100f, during[1], 3);
    }

    [Fact]
    public void ShapesOfOneForceAreStable()
    {
        var accel = new ParticleForce(ParticleForceKind.Acceleration, 0, new[]
            { new ParticleForceValue("acceleration", "Acceleration", true, new Vector3(0, -2000, 0), false, true) });
        var lines = new List<float>();
        ParticleForceShapes.Append(lines, accel, new Vector3(5, 0, 0), Vector3.Zero);
        // the shaft points down the push, clamped to 300 units however strong it is
        Assert.Equal(new[] { 5f, 0f, 0f, 5f, -300f, 0f }, lines.Take(6).ToArray());

        var none = new List<float>();
        ParticleForceShapes.Append(none, accel with { Values = new[] { accel.Values[0] with { Value = Vector3.Zero } } }, Vector3.Zero, Vector3.Zero);
        Assert.Empty(none);   // no push, no arrow - not a zero-length line
    }
}
