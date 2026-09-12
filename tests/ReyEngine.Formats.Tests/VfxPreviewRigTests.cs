using System.Numerics;
using ReyEngine.App.ViewModels;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M712: the preview rig. A system definition describes emitters and says nothing about where the effect
/// goes - what makes one a missile is the spell that flies it - so a preview that parks everything at the
/// origin shows a missile as a puff standing still and a trail with nothing to trail behind.
/// </summary>
public sealed class VfxPreviewRigTests
{
    private static string? Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    // ================================================================ the motion

    [Fact]
    public void AStillRigSitsAtItsHeightAndFacesNowhere()
    {
        var rig = new VfxPreviewRig(VfxRigMode.Still, Height: 100f);
        var pose = rig.Pose(0f);
        Assert.Equal(new Vector3(0f, 100f, 0f), pose.Translation);
        // still at four seconds: the clock runs, the rig does not
        Assert.Equal(pose.Translation, rig.Pose(4f).Translation);
        // no direction to face, so none is invented
        Assert.Equal(Matrix4x4.Identity, VfxCastFrame.RotationOf(pose));
    }

    [Fact]
    public void AMissileFliesItsDistanceInItsFlightTime()
    {
        var rig = new VfxPreviewRig(VfxRigMode.Missile, Height: 100f, Distance: 1200f, Speed: 1600f);
        // six champions at eight champions a second
        Assert.Equal(0.75f, rig.FlightSeconds, 4);

        Assert.Equal(new Vector3(-600f, 100f, 0f), rig.Pose(0f).Translation);
        Assert.Equal(new Vector3(0f, 100f, 0f), rig.Pose(0.375f).Translation);
        Assert.Equal(new Vector3(600f, 100f, 0f), rig.Pose(0.75f).Translation);
        // it holds where it lands rather than flying on out of frame
        Assert.Equal(new Vector3(600f, 100f, 0f), rig.Pose(9f).Translation);

        // and it stops emitting there, which is what the game does with a missile - no toggle needed
        Assert.True(rig.Landed(0.75f));
        Assert.False(rig.Landed(0.5f));
        Assert.Equal(0.75f, rig.StopAt(2f)!.Value, 4);

        // the aim travels with it: the system faces along the flight, not wherever it started
        var forward = VfxCastFrame.WorldForward(rig.Pose(0.2f));
        Assert.True(forward.X > 0.9f, $"a missile flying +X should face +X, got {forward}");
    }

    [Fact]
    public void AMissileWithNoSpeedFallsBackToTheSystemsOwnSpan()
    {
        // a run of no length would restart every frame
        var rig = new VfxPreviewRig(VfxRigMode.Missile, Speed: 0f);
        Assert.Equal(0f, rig.FlightSeconds);
        Assert.Equal(2.5f, rig.RunLength(2.5f), 4);
        Assert.Null(rig.StopAt(2.5f));
        Assert.Equal(new Vector3(0f, VfxRigDefaults.Height, 0f), rig.Pose(1f).Translation);
    }

    [Fact]
    public void ATrailCirclesAndFacesWhereItIsGoing()
    {
        var rig = new VfxPreviewRig(VfxRigMode.Trail, Height: 0f, Radius: 300f, Period: 4f);
        Assert.Equal(new Vector3(300f, 0f, 0f), rig.Pose(0f).Translation, Round);
        Assert.Equal(new Vector3(0f, 0f, 300f), rig.Pose(1f).Translation, Round);
        Assert.Equal(new Vector3(-300f, 0f, 0f), rig.Pose(2f).Translation, Round);
        // back where it started after one period
        Assert.Equal(rig.Pose(0f).Translation, rig.Pose(4f).Translation, Round);
        // a run is at least one revolution, even for a shorter system
        Assert.Equal(4f, rig.RunLength(1f), 4);
    }

    private static readonly IEqualityComparer<Vector3> Round =
        new RoundComparer();

    private sealed class RoundComparer : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 0.01f;
        public int GetHashCode(Vector3 v) => 0;
    }

    // ================================================================ the run

    [Fact]
    public void ReplayWrapsTheClockIntoOneRun()
    {
        var once = new VfxPreviewRig(VfxRigMode.Still);
        Assert.Equal(7f, once.Phase(7f, 2f), 4);            // the clock runs on

        var looping = once with { Replay = true };
        Assert.Equal(2f, looping.RunLength(2f), 4);          // a still rig runs for the system's own span
        Assert.Equal(1f, looping.Phase(7f, 2f), 4);          // 7 seconds is three runs and a second

        // Burst is Still that starts over, and the view model turns Replay on with it
        var vm = new ParticleEditorViewModel { RigMode = VfxRigMode.Burst };
        Assert.True(vm.RigReplay);
        Assert.Equal(VfxRigMode.Burst, vm.Rig.Mode);
    }

    [Fact]
    public void StopMidRunIsOffUnlessAsked()
    {
        Assert.Null(new VfxPreviewRig(VfxRigMode.Still).StopAt(3f));
        Assert.Equal(1.5f, new VfxPreviewRig(VfxRigMode.Still, StopMidRun: true).StopAt(3f)!.Value, 4);
    }

    // ================================================================ the guess

    [Theory]
    [InlineData("Ahri_Base_E_mis", VfxRigMode.Missile)]
    [InlineData("Ahri_Skin76_P_mis", VfxRigMode.Missile)]
    [InlineData("Katarina_Skin67_W_Missile_TopArc", VfxRigMode.Missile)]
    [InlineData("Diana_Skin27_Q_Trail", VfxRigMode.Trail)]
    [InlineData("SightWard_Skin95_Trails", VfxRigMode.Trail)]
    // the two shapes the corpus says are the usual mistakes, and they are handled
    [InlineData("Vayne_Skin52_Recall_mis2", VfxRigMode.Still)]
    [InlineData("XinZhao_Skin53_R_IndicatorRing", VfxRigMode.Still)]
    // a child is spawned by a parent, so on its own it has nowhere to go
    [InlineData("Star1_ChildParticle", VfxRigMode.Still)]
    [InlineData("Aatrox_Base_Q_Indicator_01", VfxRigMode.Still)]
    [InlineData("Aatrox_Base_Idle", VfxRigMode.Still)]
    public void TheNameSuggestsARig(string name, VfxRigMode expected) =>
        Assert.Equal(expected, VfxRigNaming.For(name).Mode);

    [Fact]
    public void TheGuessAlwaysSaysWhy()
    {
        // a guess the user cannot see is a guess they cannot correct
        foreach (var name in new[] { "Ahri_Base_E_mis", "Diana_Skin27_Q_Trail", "Star1_ChildParticle", "x", "" })
            Assert.False(string.IsNullOrWhiteSpace(VfxRigNaming.For(name).Why), $"no reason given for '{name}'");
        Assert.Null(Record.Exception(() => VfxRigNaming.For(null)));

        // "mis" is an exact token with its digits stripped, never a substring - otherwise "dismiss" and
        // "mist" fly across the screen
        Assert.Equal(VfxRigMode.Still, VfxRigNaming.For("Zeri_Skin21_Mist").Mode);
        Assert.Equal(VfxRigMode.Missile, VfxRigNaming.For("Akshan_Skin27_Q_Mis02").Mode);
    }

    // ================================================================ the wiring

    [Fact]
    public void TheViewportCarriesTheRigAndTheSeedReachesTheSimulator()
    {
        string? viewport = Source("src", "ReyEngine.App", "Views", "ViewportControl.cs");
        Assert.NotNull(viewport);
        // M715: composed with the item's own placement. The particle editor places at the origin, so
        // this is exactly what it always was; the champion window anchors at the caster or the dummy.
        Assert.Contains("rig.Pose(phase) * Matrix4x4.CreateTranslation(item.WorldPos)", viewport);
        // a map placement that travels of its own accord is not the preview's to move
        Assert.Contains("if (item.TravelTo is not null) continue;", viewport);
        // replay owns the cycle when it is on, so the auto-stop cycle does not restart the run underneath it
        Assert.Contains("ParticleRig is not { Replay: true }", viewport);

        // the seed is the one rig setting that must live on the item: a simulator's random stream is fixed
        // when it is built
        string? sim = Source("src", "ReyEngine.App", "Services", "VfxPlaybackSim.cs");
        Assert.NotNull(sim);
        Assert.Contains("int seed = item.Seed ?? HashCode.Combine(", sim);

        string? view = Source("src", "ReyEngine.App", "Views", "ParticleEditorView.axaml");
        Assert.NotNull(view);
        Assert.Contains("ParticleRig=\"{Binding Rig}\"", view);
    }

    [Fact]
    public void TheSegmentsAreOneEnumAndClickingOneClearsTheRest()
    {
        var vm = new ParticleEditorViewModel();
        Assert.True(vm.IsRigStill);

        vm.IsRigMissile = true;
        Assert.Equal(VfxRigMode.Missile, vm.RigMode);
        Assert.False(vm.IsRigStill);
        Assert.True(vm.IsRigMissile);

        // clicking the segment that is already down must not clear it - a segmented control has no "none"
        vm.IsRigMissile = false;
        Assert.Equal(VfxRigMode.Missile, vm.RigMode);

        // the rig the viewport reads is rebuilt from the settings, not stored beside them
        vm.RigHeight = 250;
        Assert.Equal(250f, vm.Rig.Height, 3);
        vm.RigStopMidRun = true;
        Assert.True(vm.Rig.StopMidRun);
    }
}
