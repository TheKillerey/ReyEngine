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
        Assert.True(vm.Animation.Loop);   // M889: the user's pick loops again - the intro's one-shot loop does not stick
    }

    [Fact]
    public void AnotherSkinsFileOfTheSameNameNeverStandsInForTheIdle()
    {
        // M889: Evelynn skin65's Idle1 is its own idle01.anm; skin24 ships a file of the same name for another rig
        const string own = "assets/characters/evelynn/skins/skin65/animations/idle01.anm";
        const string other = "assets/characters/evelynn/skins/skin24/animations/idle01.anm";
        var entries = new[] { other, own }.Select(p => new AnimationEntryViewModel(new WadAssetEntry { Path = p })
        {
            SkinGroup = AnimationEntryViewModel.GroupFromPath(p),
            IsCurrentSkin = true,   // the by-file-name highlight marks both
        });
        var ownAnms = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "idle01.anm", own };
        var ordered = AnimationEntryViewModel.OrderForSkin(entries, ownAnms);
        Assert.Equal(own, ordered[0].Entry.Path);

        var clip = new AnimClipInfo("Idle1", own, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<uint>(), Array.Empty<uint>());
        var vm = new MeshPreviewViewModel();
        vm.Animation.SetSkeleton(277);
        vm.Animation.SetAnimations(ordered);
        vm.SetSubmeshRules(Array.Empty<string>(), null, new[] { clip }, new[] { clip }, Array.Empty<AnimClipNode>());
        Assert.True(vm.PlayLoadedIdle());
        Assert.Equal(own, vm.Animation.SelectedAnimation?.Entry.Path);
    }

    // ---- M897: a clip that reaches its end goes back to the looped idle ----

    private static MeshPreviewViewModel WithIdleAndSpell(out AnimationEntryViewModel spell)
    {
        var vm = Load(10, new[] { "idle1.anm", "spell1.anm" }, new[] { Clip("Idle1", "idle1.anm"), Clip("Spell1", "spell1.anm") });
        Assert.True(vm.PlayLoadedIdle());
        spell = vm.Animation.Animations.First(a => a.Name == "spell1.anm");
        return vm;
    }

    [Fact]
    public void M897_a_cast_clip_that_ends_returns_to_the_looped_idle_and_keeps_the_playback()
    {
        var vm = WithIdleAndSpell(out var spell);
        var item = (VfxPlaybackItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(VfxPlaybackItem));
        var bundle = new List<VfxPlaybackItem> { item };
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(MeshPreviewViewModel).GetField("_eventPlaybackActive", flags)!.SetValue(vm, true);
        typeof(MeshPreviewViewModel).GetField("_eventBundle", flags)!.SetValue(vm, bundle);
        typeof(MeshPreviewViewModel).GetField("_playback", flags)!.SetValue(vm, new VfxPlayback(bundle));
        var playback = vm.Playback;
        typeof(MeshPreviewViewModel).GetMethod("SelectClipInternal", flags)!.Invoke(vm, new object[] { spell, true });
        vm.Animation.Loop = false;

        vm.Animation.ClipEnded!();

        Assert.Equal("idle1.anm", vm.Animation.SelectedAnimation?.Name);
        Assert.True(vm.Animation.Loop);
        Assert.Same(playback, vm.Playback);   // the effects the cast published are not re-driven by the idle
        Assert.NotNull(typeof(MeshPreviewViewModel).GetField("_eventBundle", flags)!.GetValue(vm));
    }

    [Fact]
    public void M897_a_looping_clip_is_not_taken_back_and_a_user_picked_one_shot_returns_to_idle()
    {
        var vm = WithIdleAndSpell(out var spell);
        vm.Animation.SelectedAnimation = spell;   // the Animate tab
        vm.Animation.Loop = true;
        vm.Animation.ClipEnded!();                // a looping clip never ends; a stray callback changes nothing
        Assert.Same(spell, vm.Animation.SelectedAnimation);

        vm.Animation.Loop = false;                // Loop off: it plays once, then the idle
        vm.Animation.ClipEnded!();
        Assert.Equal("idle1.anm", vm.Animation.SelectedAnimation?.Name);
        Assert.True(vm.Animation.Loop);
    }
}
