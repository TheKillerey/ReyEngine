using System;
using System.IO;
using System.Linq;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M726: a clip's particle events reach the viewport as cues on the ANIMATION's timeline - one per spawn
/// pair, aimed at their target bone, ending where authored, and replayed rather than rebuilt on every loop.
/// </summary>
public sealed class ClipParticleCueTests
{
    [Fact]
    public void EveryEventSpawnBecomesItsOwnItemAndKillEventsBecomeNone()
    {
        string? vm = Source("src", "ReyEngine.App", "ViewModels", "MeshPreviewViewModel.cs");
        if (vm is null) return;

        // a kill event STOPS a system; spawning it played a second copy nothing ever stopped
        Assert.Contains("if (ev.IsKill) continue;", vm);
        // one item per pair - we used to take the first pair that named a bone and drop the rest
        Assert.Contains("foreach (var spawn in ev.Spawns", vm);
        Assert.Contains("TargetBone = atDummy ? null : ResolveBoneName(spawn.TargetBoneName, spawn.TargetBoneHash)", vm);
        // the CLIP's own tick, not the .anm's fps
        Assert.Contains("float frameSeconds = FrameSeconds();", vm);
        Assert.Contains("ev.StartFrame) * frameSeconds", vm);
        // and mEndFrame becomes a stop time
        Assert.Contains("EndTime = end,", vm);
    }

    [Fact]
    public void BothRenderersResolveABeamsTargetBoneBeforeFallingBackToTheDummy()
    {
        string? gl = Source("src", "ReyEngine.App", "Views", "ViewportControl.cs");
        string? dx = Source("src", "ReyEngine.App", "Services", "D3D11MapParticles.cs");
        if (gl is null || dx is null) return;

        // the SAME precedence in both: explicit world target, then the event's target bone, then the dummy
        Assert.Contains("beamItem.BeamTarget", gl);
        Assert.Contains("BoneWorldPosition(beamItem.TargetBone)", gl);
        Assert.Contains("?? TargetDummyPosition", gl);

        Assert.Contains("item.BeamTarget ?? BoneWorldPosition(item.TargetBone) ?? _beamTarget", dx);

        // and both derive it from bone globals rather than from a second source of truth
        Assert.Contains("private Vector3? BoneWorldPosition(string? bone)", gl);
        Assert.Contains("private Vector3? BoneWorldPosition(string? bone)", dx);
    }

    [Fact]
    public void BoneAttachedSystemsAreAnchoredWithNoClipPlaying()
    {
        string? gl = Source("src", "ReyEngine.App", "Views", "ViewportControl.cs");
        if (gl is null) return;

        // idle effects play with NO clip, so anchoring only inside the animated branch left them at the
        // world origin - the D3D11 sibling has always anchored from bind pose
        Assert.Contains("AnchorBoneSystemsToBindPose();", gl);
        Assert.Contains("BonePalette.Globals(skeleton, null, 0f)", gl);
        // the joint walk is cached per skeleton, not redone every frame
        Assert.Contains("_bindPoseGlobalsFor", gl);
    }

    [Fact]
    public void ALoopingClipReplaysItsCuesInsteadOfRebuildingThePlayback()
    {
        string? vm = Source("src", "ReyEngine.App", "ViewModels", "MeshPreviewViewModel.cs");
        string? gl = Source("src", "ReyEngine.App", "Views", "ViewportControl.cs");
        string? xaml = Source("src", "ReyEngine.App", "Views", "MeshPreviewWindow.axaml");
        if (vm is null || gl is null || xaml is null) return;

        // the wrap bumps a token when a set is already built; only an empty one is rebuilt
        Assert.Contains("if (Playback is { Items.Count: > 0 }) ParticleReplayToken++;", vm);
        Assert.Contains("else ApplyClipParticles();", vm);
        Assert.Contains("ParticleReplayToken=\"{Binding ParticleReplayToken}\"", xaml);

        // Reset() clears particles but Update() CONSUMES the start delay, so a replay that does not re-arm
        // it fires every cue at once on the second pass. This is the line that stops that.
        Assert.Contains("if (item.StartDelay > 0f) sim.SetStartDelay(item.StartDelay);", gl);
    }

    [Fact]
    public void ClipEffectsRunOnTheAnimationClockButIdleEffectsDoNot()
    {
        string? gl = Source("src", "ReyEngine.App", "Views", "ViewportControl.cs");
        if (gl is null) return;

        // gated on a clip being selected: with none - idle effects, and the particle editor - the clock
        // stays real time, because freezing there would mean idle effects that never move
        Assert.Contains("if (AnimationClip is not null)", gl);
        Assert.Contains("dt = MathF.Max(0f, t - _lastParticleAnimTime);", gl);
        // the editor's speed multiplier must not stack on top of the clip's own speed
        Assert.Contains("dt = ParticlePaused ? 0f : dt * (float)ParticleSpeed;", gl);
    }

    private static string? Source(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx")))
                return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        return null;
    }
}
