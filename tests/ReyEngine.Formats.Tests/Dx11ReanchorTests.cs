using System.Numerics;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Animation;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Skeletons;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M630: re-anchoring on the D3D11 particle path — bone attachment, missile travel, beam targets.
///
/// <para>All three existed only in the GL viewport. The one with real arithmetic behind it is the bone
/// transform, so that is asserted against the CPU skinner, which has been placing these effects correctly
/// for a hundred milestones. The other two are wiring, and are asserted as wiring.</para>
/// </summary>
public sealed class Dx11ReanchorTests
{
    private const string Champions = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Champions";

    private static readonly Lazy<HashDatabase?> Database = new(() =>
    {
        try { return new HashSyncService().LoadLocal(_ => { }); }
        catch { return null; }
    });

    private static (MeshAsset Mesh, SkeletonAsset Skeleton, AnimationClip Clip)? Ahri()
    {
        string wad = Path.Combine(Champions, "Ahri.wad.client");
        if (!File.Exists(wad) || Database.Value is not { } database) return null;

        using var archive = WadArchive.Open(wad, new WadPathResolver(database));
        if (archive.ResolvedCount == 0) return null;
        byte[]? Read(string p) =>
            archive.TryGetEntry(HashAlgorithms.WadPath(p), out _) ? archive.Extract(HashAlgorithms.WadPath(p)) : null;

        if (Read("assets/characters/ahri/skins/base/ahri_base.skn") is not { } skn) return null;
        if (Read("assets/characters/ahri/skins/base/ahri_base.skl") is not { } skl) return null;
        if (Read("assets/characters/ahri/skins/base/animations/spell1.anm") is not { } anm) return null;

        try { return (SkinnedMeshDecoder.Decode(skn), SkeletonDecoder.Decode(skl), AnimationDecoder.Decode(anm, "spell1")); }
        catch { return null; }
    }

    private static string? Read(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    // ===================================================== the bone transform

    [Fact]
    public void TheBoneGlobalsMatchTheOnesTheCpuSkinnerHandsTheGlPath()
    {
        // The load-bearing assertion. GL re-anchors onto SkinnedFrame.BoneGlobals; D3D11 skins on the GPU
        // and has no such by-product, so these come from a second walk of the joints. If the two ever
        // disagreed, the same effect would sit in two different places depending on the renderer.
        if (Ahri() is not { } data) return;
        var (mesh, skeleton, clip) = data;

        foreach (float t in new[] { 0f, clip.Duration * 0.3f, clip.Duration * 0.7f })
        {
            var gl = SkinnedMeshAnimator.Skin(mesh, skeleton, clip, t).BoneGlobals;
            var dx = BonePalette.Globals(skeleton, clip, t);
            Assert.NotNull(gl);

            Assert.Equal(gl!.Count, dx.Count);
            foreach (var (name, expected) in gl)
            {
                Assert.True(dx.TryGetValue(name, out var actual), $"{name} missing at t={t:0.00}");
                Assert.True(Vector3.Distance(expected.Translation, actual.Translation) < 1e-3f,
                    $"{name} at t={t:0.00}: GL {expected.Translation} vs D3D11 {actual.Translation}");
            }
        }
    }

    [Fact]
    public void TheGlobalIsTheBonesPlaceNotTheSkinningMatrix()
    {
        // A particle is PLACED at the bone; it is not a vertex being moved from its bind pose. Handing the
        // skinning matrix here would put every attached effect at inverseBind * global instead - near the
        // origin, which is exactly the symptom this milestone fixes.
        if (Ahri() is not { } data) return;
        var (_, skeleton, clip) = data;

        var globals = BonePalette.Globals(skeleton, clip, clip.Duration * 0.5f);
        var palette = BonePalette.Build(skeleton, clip, clip.Duration * 0.5f);

        Assert.NotEmpty(globals);
        // A real skeleton has joints well away from the origin; the skinning matrices are near-identity.
        Assert.Contains(globals.Values, m => m.Translation.Length() > 20f);
        Assert.All(palette, m => Assert.True(m.Translation.Length() < 500f));
    }

    [Fact]
    public void BoneNamesAreMatchedTheWayAParticleEventNamesThem()
    {
        // ParticleEventData spells bones in whatever case the artist typed. A case-sensitive lookup drops
        // the attachment silently and the effect falls back to the origin.
        if (Ahri() is not { } data) return;
        var globals = BonePalette.Globals(data.Skeleton, data.Clip, 0.2f);
        if (globals.Count == 0) return;

        string any = globals.Keys.First();
        Assert.True(globals.ContainsKey(any.ToUpperInvariant()));
        Assert.True(globals.ContainsKey(any.ToLowerInvariant()));
    }

    [Fact]
    public void ASkeletonlessSubjectHasNoBoneGlobals()
    {
        var empty = new SkeletonAsset
        {
            Bones = Array.Empty<BoneInfo>(),
            Joints = Array.Empty<SkinJoint>(),
            Influences = Array.Empty<short>(),
        };
        Assert.Empty(BonePalette.Globals(empty, null, 0f));
    }

    // ===================================================== the wiring

    [Fact]
    public void TheDriverReanchorsBeforeItSteps()
    {
        // A bone-attached system has to be simulated from where its bone is THIS frame. Re-anchoring after
        // the step spawns every particle one frame behind the bone, which reads as the effect lagging.
        if (Read("src", "ReyEngine.App", "Services", "D3D11MapParticles.cs") is not { } text) return;

        int reanchor = text.IndexOf("Reanchor(dt);", StringComparison.Ordinal);
        int update = text.IndexOf("sim.Update(dt);", StringComparison.Ordinal);
        Assert.True(reanchor > 0, "Reanchor is gone");
        Assert.True(update > reanchor, "the simulation step must come AFTER the re-anchor");
    }

    [Fact]
    public void AllThreeReanchorPathsExistOnTheD3D11Driver()
    {
        // Each is a distinct symptom: no bone attach = effects at the world origin; no travel = missiles
        // that never leave the caster; no beam target = a chain that stops in mid-air.
        if (Read("src", "ReyEngine.App", "Services", "D3D11MapParticles.cs") is not { } text) return;

        Assert.Contains("item.AttachBone is { Length: > 0 } bone", text);
        Assert.Contains("item.TravelTo is not { } destination", text);
        // M674 made the beam target per item - a Blitzcrank return cable ends at the cast origin, not at
        // the dummy - with the host target as the fallback. The symptom this guards is unchanged: it is
        // still supplied every frame.
        Assert.Contains("sim.SetBeamTarget(item.BeamTarget ?? _beamTarget)", text);
    }

    [Fact]
    public void FlightTimeIsDroppedWithTheSimulatorsItBelongedTo()
    {
        // Keeping elapsed flight across a rebuild would start a fresh missile part-way to its target.
        if (Read("src", "ReyEngine.App", "Services", "D3D11MapParticles.cs") is not { } text) return;
        Assert.Contains("_travelElapsed.Clear()", text);
    }

    [Fact]
    public void TheHostSuppliesTheBonesAndTheTargetEveryFrame()
    {
        // The capability is inert without these three lines, which is the trap this project has now hit
        // twice. The pose changes every animated frame and the dummy moves whenever it is dragged, so
        // neither can be set once at build time.
        if (Read("src", "ReyEngine.App", "Views", "MeshPreviewWindow.Dx11.cs") is not { } window) return;
        if (Read("src", "ReyEngine.App", "Views", "Dx11ViewportSurface.cs") is not { } surface) return;

        Assert.Contains("_dx11.BoneGlobals = vm.CurrentBoneGlobals()", window);
        Assert.Contains("_dx11.BeamTarget = vm.TargetDummyPosition", window);
        Assert.Contains("Particles?.SetBoneGlobals(BoneGlobals, BoneModelWorld)", surface);
        Assert.Contains("Particles?.SetBeamTarget(BeamTarget)", surface);
    }

    [Fact]
    public void TheModelTransformIsAppliedOverTheBone()
    {
        // Bone globals are pre-placement. Without the model transform on top, a bone-attached effect
        // stands where the character was authored while the character itself walks away - the M613/M614
        // lesson, arriving on this path.
        if (Read("src", "ReyEngine.App", "Services", "D3D11MapParticles.cs") is not { } text) return;
        Assert.Contains("bm * _boneModelWorld", text);
    }
}
