using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Formats.Skeletons;

namespace ReyEngine.Formats.Tests;

/// <summary>M886: a loaded champion skin starts its own idle, looped.</summary>
public sealed class CharacterIdleAutoPlayTests
{
    private static AnimClipInfo Clip(string name, string file) =>
        new(name, "assets/characters/x/skins/base/animations/" + file, Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<uint>(), Array.Empty<uint>());

    private static MeshPreviewViewModel Load(int bones, string[] files, AnimClipInfo[] clips, AnimClipNode[]? nodes = null)
    {
        var vm = new MeshPreviewViewModel();
        vm.Animation.SetSkeleton(bones);
        vm.Animation.SetAnimations(files.Select(f => new AnimationEntryViewModel(new WadAssetEntry { Path = "a/" + f })));
        vm.SetSubmeshRules(Array.Empty<string>(), null, clips, clips, nodes ?? Array.Empty<AnimClipNode>());
        return vm;
    }

    [Fact]
    public void AnIdleIsSelectedAndLooping()
    {
        var vm = Load(10, new[] { "run.anm", "idle1.anm" }, new[] { Clip("Run", "run.anm"), Clip("Idle1", "idle1.anm") });
        Assert.True(vm.PlayLoadedIdle());
        Assert.Equal("idle1.anm", vm.Animation.SelectedAnimation?.Name);
        Assert.True(vm.Animation.Loop);
    }

    [Fact]
    public void ACompositeIdle1ResolvesThroughTheGraph()
    {
        var clips = new[] { Clip("Run", "run.anm"), Clip("Idle1_Base", "stand.anm") };
        var node = new AnimClipNode("Idle1", AnimationClipGraph.HashOf("Idle1"), AnimClipNodeKind.Sequencer, "SequencerClipData",
            new[] { new AnimClipChild(AnimationClipGraph.HashOf("Idle1_Base"), null) });
        var vm = Load(10, new[] { "run.anm", "stand.anm" }, clips, new[] { node });
        Assert.True(vm.PlayLoadedIdle());
        Assert.Equal("stand.anm", vm.Animation.SelectedAnimation?.Name);
    }

    [Fact]
    public void NoIdleNoSkeletonOrAClipAlreadyChosenChangesNothing()
    {
        var noIdle = Load(10, new[] { "run.anm" }, new[] { Clip("Run", "run.anm") });
        Assert.False(noIdle.PlayLoadedIdle());
        Assert.Null(noIdle.Animation.SelectedAnimation);

        var noSkeleton = Load(0, new[] { "idle1.anm" }, new[] { Clip("Idle1", "idle1.anm") });
        Assert.False(noSkeleton.PlayLoadedIdle());
        Assert.Null(noSkeleton.Animation.SelectedAnimation);

        var chosen = Load(10, new[] { "run.anm", "idle1.anm" }, new[] { Clip("Run", "run.anm"), Clip("Idle1", "idle1.anm") });
        var run = chosen.Animation.Animations.First(a => a.Name == "run.anm");
        chosen.Animation.SelectedAnimation = run;
        Assert.False(chosen.PlayLoadedIdle());
        Assert.Same(run, chosen.Animation.SelectedAnimation);
    }

    [Fact]
    public void ASpawnIntroPlaysOnceThenTheIdleLoops()
    {
        var vm = Load(10, new[] { "idle_in.anm", "idle1.anm" }, new[] { Clip("Idle_In", "idle_in.anm"), Clip("Idle1", "idle1.anm") });
        Assert.True(vm.PlayLoadedIdle());
        Assert.Equal("idle_in.anm", vm.Animation.SelectedAnimation?.Name);
        Assert.False(vm.Animation.Loop);
        vm.Animation.ClipEnded!();
        Assert.Equal("idle1.anm", vm.Animation.SelectedAnimation?.Name);
        Assert.True(vm.Animation.Loop);
        vm.Animation.ClipEnded!();   // nothing pending: stays
        Assert.Equal("idle1.anm", vm.Animation.SelectedAnimation?.Name);
    }

    [Fact]
    public void APickDuringTheIntroEndsTheChain()
    {
        var vm = Load(10, new[] { "idle_in.anm", "idle1.anm", "run.anm" },
            new[] { Clip("Idle_In", "idle_in.anm"), Clip("Idle1", "idle1.anm"), Clip("Run", "run.anm") });
        Assert.True(vm.PlayLoadedIdle());
        vm.Animation.SelectedAnimation = vm.Animation.Animations.First(a => a.Name == "run.anm");
        vm.Animation.ClipEnded!();
        Assert.Equal("run.anm", vm.Animation.SelectedAnimation?.Name);
    }
}
