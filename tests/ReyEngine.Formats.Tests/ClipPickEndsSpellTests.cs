using System.Reflection;
using System.Runtime.CompilerServices;
using ReyEngine.App.ViewModels;
using ReyEngine.Formats.Animation;

namespace ReyEngine.Formats.Tests;

/// <summary>M883: picking another clip in the Animate tab ends the previous spell's effects.</summary>
public sealed class ClipPickEndsSpellTests
{
    private static readonly BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void AClipPickedByTheUserDropsTheSpellCompositeAndItsPlayback()
    {
        var preview = new MeshPreviewViewModel();
        var item = (VfxPlaybackItem)RuntimeHelpers.GetUninitializedObject(typeof(VfxPlaybackItem));
        var bundle = new List<VfxPlaybackItem> { item };
        typeof(MeshPreviewViewModel).GetField("_eventPlaybackActive", Any)!.SetValue(preview, true);
        typeof(MeshPreviewViewModel).GetField("_eventBundle", Any)!.SetValue(preview, bundle);
        typeof(MeshPreviewViewModel).GetField("_playback", Any)!.SetValue(preview, new VfxPlayback(bundle));

        var idle = (AnimationClip)RuntimeHelpers.GetUninitializedObject(typeof(AnimationClip));
        preview.Animation.ClipChanged!(idle);   // what ApplyClip raises for a clip the user picked

        Assert.Null(preview.Playback);
        Assert.Null(typeof(MeshPreviewViewModel).GetField("_eventBundle", Any)!.GetValue(preview));
        Assert.False((bool)typeof(MeshPreviewViewModel).GetField("_eventPlaybackActive", Any)!.GetValue(preview)!);
    }

    [Fact]
    public void AClipTheViewModelSelectsItselfKeepsTheSpellComposite()
    {
        var preview = new MeshPreviewViewModel();
        var bundle = new List<VfxPlaybackItem> { (VfxPlaybackItem)RuntimeHelpers.GetUninitializedObject(typeof(VfxPlaybackItem)) };
        typeof(MeshPreviewViewModel).GetField("_eventPlaybackActive", Any)!.SetValue(preview, true);
        typeof(MeshPreviewViewModel).GetField("_eventBundle", Any)!.SetValue(preview, bundle);
        typeof(MeshPreviewViewModel).GetField("_internalClipSelect", Any)!.SetValue(preview, true);

        preview.Animation.ClipChanged!((AnimationClip)RuntimeHelpers.GetUninitializedObject(typeof(AnimationClip)));

        Assert.NotNull(typeof(MeshPreviewViewModel).GetField("_eventBundle", Any)!.GetValue(preview));
    }
}
