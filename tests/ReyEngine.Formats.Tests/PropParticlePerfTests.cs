using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ReyEngine.App.Services;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Animation;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Skeletons;
using ReyEngine.Rendering.Vfx;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>M694: the props and particles of a whole map at once. The pose is allocation-free and equals
/// the old path bit for bit, the vertex pass is a pure function a driver may run on every core, props
/// far from the camera or already posed this 30 Hz tick are skipped, and a particle system entering the
/// camera gate is warmed a few per frame instead of all at once.</summary>
public sealed class PropParticlePerfTests
{
    private const string Shipping = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping";
    private static readonly Lazy<HashDatabase?> Database = new(() =>
    {
        try { return new HashSyncService().LoadLocal(_ => { }); }
        catch { return null; }
    });

    /// <summary>A Map453 mob with its mesh, skeleton and idle clip, or null when the game is not here.</summary>
    private static (MeshAsset Mesh, SkeletonAsset Skeleton, AnimationClip Clip)? Mob(string character)
    {
        string wadPath = Path.Combine(Shipping, "Map453.wad.client");
        if (!File.Exists(wadPath) || Database.Value is not { } database) return null;
        var resolver = new WadPathResolver(database);
        using var wad = WadArchive.Open(wadPath, resolver);
        byte[]? Read(string path) { ulong h = BinTexturePath.HashOfReference(path); return wad.TryGetEntry(h, out _) ? wad.Extract(h) : null; }
        string? Bin(uint h) => database.TryGetBinName(h, out var n) ? n : null;
        string? Wad(ulong h) => database.TryGetPath(h, out var p) ? p : null;
        var bin = Read($"data/characters/{character}/skins/skin0.bin");
        if (bin is null) return null;
        var meshRef = SkinMeshExtractor.Extract(bin, Wad);
        if (meshRef?.SimpleSkin is not { } sknPath || meshRef.Skeleton is not { } sklPath) return null;
        var skn = Read(sknPath); var skl = Read(sklPath);
        if (skn is null || skl is null) return null;
        var idle = PropAnimations.PickIdle(PropAnimations.ResolveClips(bin, Read, Bin, Wad));
        if (idle is null || Read(idle.AnmPath) is not { } anm) return null;
        return (SkinnedMeshDecoder.Decode(skn), SkeletonDecoder.Decode(skl), AnimationDecoder.Decode(anm, idle.Name));
    }

    [Fact]
    public void TheReusedPaletteEqualsAFreshOneAndAllocatesNothingAfterTheFirstFrame()
    {
        if (Mob("smallgolem") is not { } mob) return;
        var scratch = new PoseBuffer();
        Matrix4x4[]? reused = null;
        for (int f = 0; f < 5; f++)
        {
            float t = f * 0.123f;
            var fresh = BonePalette.Build(mob.Skeleton, mob.Clip, t);
            reused = BonePalette.Build(mob.Skeleton, mob.Clip, t, scratch, reused);
            Assert.Equal(fresh.Length, reused.Length);
            for (int i = 0; i < fresh.Length; i++) Assert.Equal(fresh[i], reused[i]);
        }
        // the array handed in is the array filled - a material holding it sees the new frame in place
        var before = reused!;
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        var after = BonePalette.Build(mob.Skeleton, mob.Clip, 0.7f, scratch, before);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - a0;
        Assert.Same(before, after);
        Assert.True(allocated < 2048, $"a reused build allocated {allocated} bytes (the clip evaluator's own scratch is the only allowance)");
    }

    [Fact]
    public void TheVertexPassIsPureAndEqualsTheOneCallSkinner()
    {
        if (Mob("younglizard") is not { } mob) return;
        var index = SkeletonIndex.For(mob.Skeleton);
        Assert.Same(index, SkeletonIndex.For(mob.Skeleton));   // cached on the skeleton
        var scratch = new PoseBuffer();
        SkeletonPose.ComputeSkin(index, mob.Clip, 0.42f, scratch);
        var pos = new float[mob.Mesh.VertexCount * 3];
        var nrm = new float[mob.Mesh.VertexCount * 3];
        SkinnedMeshAnimator.Deform(mob.Mesh, index, scratch, pos, nrm);
        var whole = SkinnedMeshAnimator.Skin(mob.Mesh, mob.Skeleton, mob.Clip, 0.42f);
        Assert.Equal(whole.Positions, pos);
        Assert.Equal(whole.Normals, nrm);

        // the same pose deformed on several threads at once gives the same vertices every time
        var outputs = Enumerable.Range(0, 8).Select(_ => (P: new float[pos.Length], N: new float[nrm.Length])).ToArray();
        System.Threading.Tasks.Parallel.ForEach(outputs, o => SkinnedMeshAnimator.Deform(mob.Mesh, index, scratch, o.P, o.N));
        foreach (var o in outputs) { Assert.Equal(pos, o.P); Assert.Equal(nrm, o.N); }
    }

    [Fact]
    public void TheGateSkipsFarMeshesAndRepeatsAnIdleAt30Hz()
    {
        var far = new[] { Matrix4x4.CreateTranslation(20000, 0, 0) };
        var near = new[] { Matrix4x4.CreateTranslation(20000, 0, 0), Matrix4x4.CreateTranslation(100, 0, 50) };
        float gateSq = VfxPlaybackSim.MaxDistanceSquared(1000);   // the 6,000-unit floor
        Assert.False(PropAnimationGate.AnyNear(far, Vector3.Zero, gateSq));
        Assert.True(PropAnimationGate.AnyNear(near, Vector3.Zero, gateSq));
        Assert.False(PropAnimationGate.AnyNear(Array.Empty<Matrix4x4>(), Vector3.Zero, gateSq));

        Assert.False(PropAnimationGate.ShouldPose(float.NegativeInfinity, 1f, driven: false, near: false));
        Assert.True(PropAnimationGate.ShouldPose(float.NegativeInfinity, 1f, driven: false, near: true));
        Assert.False(PropAnimationGate.ShouldPose(1f, 1.01f, driven: false, near: true));    // 100 Hz asks, 30 Hz answers
        Assert.True(PropAnimationGate.ShouldPose(1f, 1.04f, driven: false, near: true));
        Assert.True(PropAnimationGate.ShouldPose(1f, 1.01f, driven: true, near: true));      // the playground actor, every frame
        Assert.True(PropAnimationGate.ShouldPose(5f, 1f, driven: false, near: true));        // a clock that went backwards (rebuild) poses
    }

    [Fact]
    public void TheWarmupQueueWarmsUnderABudgetAndNeverTwice()
    {
        var warmed = new List<VfxParticleSimulator>();
        var queue = new ParticleWarmupQueue(sim => warmed.Add(sim)) { BudgetMs = 0 };   // at least one per pump
        var sims = Enumerable.Range(0, 5).Select(i => new VfxParticleSimulator(seed: i)).ToArray();
        foreach (var s in sims) queue.Enqueue(s);
        queue.Enqueue(sims[0]);                                  // twice is once
        Assert.Equal(5, queue.Pending);
        queue.Remove(sims[2]);                                   // left the gate before its turn
        Assert.Equal(4, queue.Pending);
        Assert.False(queue.IsPending(sims[2]));

        var ready = new List<VfxParticleSimulator>();
        Assert.Equal(1, queue.Pump(ready));
        Assert.Same(sims[0], ready[0]);
        Assert.Equal(3, queue.Pending);
        while (queue.Pending > 0) queue.Pump(ready);
        Assert.Equal(new[] { sims[0], sims[1], sims[3], sims[4] }, ready);
        Assert.Equal(ready, warmed);
        Assert.Equal(0, queue.Pump(ready));                      // nothing left, nothing done

        var generous = new ParticleWarmupQueue(sim => warmed.Add(sim)) { BudgetMs = 1000 };
        foreach (var s in sims) generous.Enqueue(s);
        var all = new List<VfxParticleSimulator>();
        Assert.Equal(5, generous.Pump(all));                     // a wide budget drains the queue in one call
    }

    /// <summary>M732: the two D3D11 consumers upload positions only, so the normal half is skipped
    /// entirely rather than computed and dropped. The positions must not move because of it.</summary>
    [Fact]
    public void DeformingWithoutNormalsLeavesThePositionsIdentical()
    {
        if (Mob("smallgolem") is not { } mob) return;
        var index = SkeletonIndex.For(mob.Skeleton);
        var pose = new PoseBuffer();
        SkeletonPose.ComputeSkin(index, mob.Clip, 0.35f, pose);

        int n = mob.Mesh.VertexCount * 3;
        var withNormals = new float[n];
        var normals = new float[n];
        var without = new float[n];
        SkinnedMeshAnimator.Deform(mob.Mesh, index, pose, withNormals, normals);
        SkinnedMeshAnimator.Deform(mob.Mesh, index, pose, without, null);
        Assert.Equal(withNormals, without);

        // and the positions-only pass allocates nothing at all
        SkinnedMeshAnimator.Deform(mob.Mesh, index, pose, without, null);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 4; i++) SkinnedMeshAnimator.Deform(mob.Mesh, index, pose, without, null);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>M732: an emitter's instance array grows geometrically. Growing to the exact size meant a
    /// reallocation on every frame an emitter gained a particle, which is every frame of its fill.</summary>
    [Fact]
    public void TheInstanceArrayDoesNotReallocateOnEveryAddedParticle()
    {
        var src = Source("src", "ReyEngine.Rendering", "Vfx", "VfxParticleSimulator.cs");
        if (src is null) return;
        Assert.Contains("s.Instances = new float[Math.Max(Math.Max(n * stride, s.Instances.Length * 2), stride * 4)];", src);
        Assert.DoesNotContain("s.Instances = new float[Math.Max(n * stride, stride * 4)];", src);
    }

    /// <summary>M732: the per-frame work each of the three toggles was repeating - a re-skin that threw
    /// most of its result away, a ring buffer re-uploaded unchanged, and a mesh decoded twice.</summary>
    [Fact]
    public void TheFrameDoesNotRedoWorkItAlreadyDid()
    {
        var particles = Source("src", "ReyEngine.App", "Services", "D3D11MapParticles.cs");
        var props = Source("src", "ReyEngine.App", "Services", "D3D11MapProps.cs");
        var renderer = Source("src", "ReyEngine.Rendering.D3D11", "ShaderPreviewRenderer.cs");
        var characters = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.Characters.cs");
        if (particles is null || props is null || renderer is null || characters is null) return;

        // a mesh emitter re-skins into buffers it keeps, positions only - no PoseBuffer, arrays, bone
        // segments and joint-name dictionary per emitter per frame
        Assert.Contains("SkinnedMeshAnimator.Deform(anim.Mesh, index, ms.Pose, ms.SkinPositions, null)", particles);
        Assert.DoesNotContain("SkinnedMeshAnimator.Skin(", particles);
        // the prop fallback path likewise
        Assert.Contains("SkinnedMeshAnimator.Deform(m.SknMesh!, index, g.Pose, g.SkinPositions, null)", props);
        // the light rings are uploaded when they change, like the icons beside them
        Assert.Contains("if (ReferenceEquals(_lightRangeSource, verts)) return;", renderer);
        // and a placed prop's mesh is decoded once, off the UI thread, not again inside the frame
        Assert.Contains("decodedMesh: mesh.SknMesh);", characters);
    }

    /// <summary>M733: switching props on used to upload every distinct mesh - the scene prepare, the
    /// texture decodes, the pipeline build and the geometry upload - inside the host's render frame,
    /// because that is where the PropMeshes setter is assigned. The set is grouped there now and the
    /// meshes land a few per frame, the way a particle system is warmed.</summary>
    [Fact]
    public void SwitchingPropsOnDoesNotUploadEveryMeshInOneFrame()
    {
        var props = Source("src", "ReyEngine.App", "Services", "D3D11MapProps.cs");
        var main = Source("src", "ReyEngine.App", "Views", "MainWindow.axaml.cs");
        if (props is null || main is null) return;

        // Load groups and queues; it no longer uploads as it walks the placements
        Assert.DoesNotContain("g = Upload(inst.Mesh, prepare, sb);", props);
        Assert.Contains("_pending.Add(p);", props);
        // the pump runs every frame, ahead of the animation early-out, and keeps at least one per frame
        Assert.Contains("PumpUploads();", props);
        Assert.Contains("while (_pendingAt < _pending.Count && (done == 0 || clock.Elapsed.TotalMilliseconds < UploadBudgetMs))", props);
        // and it counts what it uploaded - without this the "at least one" guard never clears and the
        // budget never applies, which is the whole point of the queue
        int pump = props.IndexOf("public int PumpUploads()", StringComparison.Ordinal);
        Assert.True(pump > 0);
        Assert.Contains("done++;", props.Substring(pump, 700));
        Assert.Contains("if (!playing) { LastTickMs = clock.Elapsed.TotalMilliseconds; return; }", props);
        Assert.True(props.IndexOf("PumpUploads();", StringComparison.Ordinal)
                    < props.IndexOf("if (!playing) {", StringComparison.Ordinal));
        // the same budget the particle warm-up takes
        Assert.Contains("public double UploadBudgetMs { get; set; } = 3.0;", props);
        // and the viewport says it is still filling in
        Assert.Contains("uploading {loading.UploadsPending}", main);
    }

    [Fact]
    public void BothViewportsGateTheirPropsAndBudgetTheirWarmups()
    {
        var surface = Source("src", "ReyEngine.App", "Views", "Dx11ViewportSurface.cs");
        var props = Source("src", "ReyEngine.App", "Services", "D3D11MapProps.cs");
        var particles = Source("src", "ReyEngine.App", "Services", "D3D11MapParticles.cs");
        var gl = Source("src", "ReyEngine.App", "Views", "ViewportControl.cs");
        var main = Source("src", "ReyEngine.App", "Views", "MainWindow.axaml.cs");
        if (surface is null || props is null || particles is null || gl is null || main is null) return;
        // D3D11: the camera reaches the props, the props gate and reuse, the particles warm under a budget
        Assert.Contains("VfxPlaybackSim.MaxDistanceSquared(camera.Distance));   // M694", surface);
        Assert.Contains("PropAnimationGate.ShouldPose(g.LastPoseTime, seconds, m.PoseSource is not null, near)", props);
        Assert.Contains("BonePalette.Build(m.Skeleton!, clip, time, g.Pose, g.Palette)", props);
        Assert.Contains("_warmup.Pump(_readyScratch);", particles);
        Assert.DoesNotContain("sim.PreWarm(sim.FillDuration);", particles);
        // GL: the same gate, the vertex pass on every core, the same warm-up budget
        Assert.Contains("Services.PropAnimationGate.ShouldPose(anim.LastPoseTime, t, pm.PoseSource is not null, near)", gl);
        Assert.Contains("Parallel.ForEach(_duePropAnims,", gl);
        Assert.Contains("_particleWarmup.Pump(_readyParticleSims);", gl);
        Assert.DoesNotContain("sim.PreWarm(sim.FillDuration);", gl);
        // and the status line says where the frame went
        Assert.Contains("props {_dx11.PropsMs:F1} · particles {_dx11.ParticlesMs:F1}", main);
    }

    private static string? Source(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        return null;
    }
}
