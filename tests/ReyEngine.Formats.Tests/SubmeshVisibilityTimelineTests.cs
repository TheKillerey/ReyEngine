using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Skeletons;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M724: a clip's submesh visibility is a TIMELINE, and the user's own tick outranks it.
///
/// <para>Measured on the shipped champion wads, which is where every number in these tests comes from:
/// Kayn's <c>Transform_Assassin</c> shows two submeshes and hides one at frame 114; Riven's
/// <c>Recall_Winddown</c> hides two at frame 0 and shows three at frame 9; 194 of Aatrox's clips and 78 of
/// Gnar's carry more than one visibility event. Unioning those over the whole clip - what this used to do -
/// draws both of Kayn's forms at once from frame 0.</para>
/// </summary>
public sealed class SubmeshVisibilityTimelineTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    /// <summary>An animation bin with one clip carrying the events the caller describes.</summary>
    private static byte[] GraphWith(params BinTreeProperty[] events)
    {
        var eventMap = new BinTreeMap(H("mEventDataMap"), BinPropertyType.Hash, BinPropertyType.Embedded,
            events.Select((e, i) => new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, (uint)(i + 1)), e)));

        var clip = new BinTreeStruct(0, H("atomicClipData"), new BinTreeProperty[]
        {
            new BinTreeEmbedded(H("mAnimationResourceData"), H("AnimationResourceData"), new BinTreeProperty[]
            {
                new BinTreeString(H("mAnimationFilePath"), "assets/characters/x/animations/idle1.anm"),
            }),
            eventMap,
        });

        var graph = new BinTreeObject(H("Characters/X/Animations/Skin0"), H("animationGraphData"), new BinTreeProperty[]
        {
            new BinTreeMap(H("mClipDataMap"), BinPropertyType.Hash, BinPropertyType.Struct, new[]
            {
                new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, H("Idle1")), clip),
            }),
        });

        using var ms = new MemoryStream();
        new BinTree(new[] { graph }, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    private static BinTreeProperty Vis(float start, float end, string[] show, string[] hide)
    {
        var props = new List<BinTreeProperty>
        {
            new BinTreeF32(H("mStartFrame"), start),
            new BinTreeContainer(H("mShowSubmeshList"), BinPropertyType.String, show.Select(s => (BinTreeProperty)new BinTreeString(0, s))),
            new BinTreeContainer(H("mHideSubmeshList"), BinPropertyType.String, hide.Select(s => (BinTreeProperty)new BinTreeString(0, s))),
        };
        if (end >= 0f) props.Add(new BinTreeF32(H("mEndFrame"), end));
        return new BinTreeEmbedded(0, H("SubmeshVisibilityEventData"), props);
    }

    private static string Resolve(uint h) => h == H("Idle1") ? "Idle1" : "";

    [Fact]
    public void EachVisibilityEventKeepsItsOwnWindowInsteadOfBeingUnionedOverTheClip()
    {
        // Riven's Recall_Winddown shape: hide at frame 0, show at frame 9
        byte[] bin = GraphWith(
            Vis(0f, -1f, Array.Empty<string>(), new[] { "Cape", "Hood" }),
            Vis(9f, -1f, new[] { "Sword_A" }, Array.Empty<string>()));

        var clip = Assert.Single(ChampionAnimationData.ParseClips(bin, Resolve));
        Assert.Equal("Idle1", clip.Name);

        var events = clip.VisibilityEvents;
        Assert.NotNull(events);
        Assert.Equal(2, events!.Count);
        Assert.Equal(0f, events[0].StartFrame);       // sorted by start frame, not by bin order
        Assert.Equal(9f, events[1].StartFrame);
        Assert.Equal(-1f, events[0].EndFrame);        // unauthored end reads as "to the end of the clip"
        Assert.Equal(new[] { "Cape", "Hood" }, events[0].HideNames);
        Assert.Equal(new[] { "Sword_A" }, events[1].ShowNames);

        // the clip-wide union is still filled, for the callers that predate the timeline
        Assert.Equal(new[] { "Sword_A" }, clip.ShowNames);
        Assert.Equal(new[] { "Cape", "Hood" }, clip.HideNames);
    }

    [Fact]
    public void AnAuthoredEndFrameIsKeptSoAnEventCanExpire()
    {
        // Aatrox's Recall shape: an event that covers frames 0..185 and no more
        byte[] bin = GraphWith(Vis(0f, 185f, Array.Empty<string>(), new[] { "Weapon" }));
        var clip = Assert.Single(ChampionAnimationData.ParseClips(bin, Resolve));
        var ev = Assert.Single(clip.VisibilityEvents!);
        Assert.Equal(0f, ev.StartFrame);
        Assert.Equal(185f, ev.EndFrame);
    }

    [Fact]
    public void AClipWithNoVisibilityEventsReportsNoneRatherThanAnEmptyTimeline()
    {
        byte[] bin = GraphWith();
        var clip = Assert.Single(ChampionAnimationData.ParseClips(bin, Resolve));
        Assert.Null(clip.VisibilityEvents);
    }

    // ---- the layering itself ----

    [Fact]
    public void TheAutoPassDoesNotRecordAnOverrideButAUserClickDoes()
    {
        var s = new SubmeshToggleViewModel { Name = "Sword_A", Index = 0 };
        Assert.Null(s.Override);
        Assert.True(s.IsVisible);

        // what ApplyAutoVisibility does: write the computed value
        s.ApplyComputed(false);
        Assert.False(s.IsVisible);
        Assert.Null(s.Override);          // still following the skin + animation
        Assert.False(s.IsOverridden);

        // what the checkbox does: set the property
        s.IsVisible = true;
        Assert.True(s.Override);          // the user has spoken
        Assert.True(s.IsOverridden);

        // and the next auto pass must NOT be able to take it back...
        s.ApplyComputed(false);
        Assert.False(s.IsVisible);        // ApplyComputed itself still writes,
        Assert.True(s.Override);          // but the override survives it,
        // ...because the caller re-applies the override on top, which is the contract:
        s.ApplyComputed(s.Override ?? false);
        Assert.True(s.IsVisible);

        s.ClearOverride();
        Assert.Null(s.Override);
        Assert.False(s.IsOverridden);
    }

    [Fact]
    public void TheChangedHookFiresForBothComputedAndUserWrites()
    {
        int changed = 0;
        var s = new SubmeshToggleViewModel { Name = "Cape", Index = 1 };
        s.Changed = () => changed++;

        s.ApplyComputed(false);
        Assert.Equal(1, changed);          // the renderer must hear the computed change too
        s.IsVisible = true;
        Assert.Equal(2, changed);
        s.ApplyComputed(true);
        Assert.Equal(2, changed);          // no-op writes do not churn the render set
    }

    // ---- the splitter every reader now shares ----

    [Theory]
    [InlineData("Tail_Large Body_Proxy", new[] { "Tail_Large", "Body_Proxy" })]
    [InlineData("Tail_Large,Body_Proxy", new[] { "Tail_Large", "Body_Proxy" })]
    [InlineData("Tail_Large, Body_Proxy", new[] { "Tail_Large", "Body_Proxy" })]
    [InlineData("  Sword_A  ", new[] { "Sword_A" })]
    [InlineData("", new string[0])]
    public void OneSplitterAcceptsEveryFormTheBinsCarry(string raw, string[] expected)
        => Assert.Equal(expected, ChampionAnimationData.SplitSubmeshList(raw));

    [Fact]
    public void TheSplitterTakesNull() => Assert.Empty(ChampionAnimationData.SplitSubmeshList(null));
}
