using System.Numerics;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Animation;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Skeletons;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M619: the bones, the gizmo and the VFX on the D3D11 character preview.
///
/// <para>The GPU half cannot be exercised here, so what is asserted is the half that decides what gets
/// drawn: that the skeleton overlay is the SAME geometry the GL viewport draws, computed the cheap way,
/// and that the three per-frame publishes actually happen.</para>
/// </summary>
public sealed class CharacterDx11OverlayTests
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

    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        return dir?.FullName;
    }

    // ===================================================== the skeleton overlay

    [Fact]
    public void TheSkeletonOverlayIsExactlyWhatTheGlViewportDraws()
    {
        // The load-bearing one. Both viewports have to draw the same bones in the same places, or an A/B
        // between the renderers turns into an argument about the overlay instead of about the shading.
        if (Ahri() is not { } data) return;
        var (mesh, skeleton, clip) = data;

        foreach (float t in new[] { 0f, clip.Duration * 0.4f, clip.Duration * 0.8f })
        {
            var gl = SkinnedMeshAnimator.Skin(mesh, skeleton, clip, t).BoneSegments;
            var (_, dx) = BonePalette.BuildWithSegments(skeleton, clip, t);

            Assert.Equal(gl.Length, dx.Length);
            for (int i = 0; i < gl.Length; i++)
                Assert.True(MathF.Abs(gl[i] - dx[i]) < 1e-3f,
                    $"at t={t:0.00}s component {i} differs: GL {gl[i]} vs D3D11 {dx[i]}");
        }
    }

    [Fact]
    public void TheOverlayIsComputedWithoutSkinningEveryVertex()
    {
        // The point of the separate path: the GL side gets its segments as a by-product of transforming
        // 30,000 vertices on the CPU. Asking for them alone must not do that work - so ask for a lot of
        // them and require it to stay far below what a full skin costs.
        if (Ahri() is not { } data) return;
        var (mesh, skeleton, clip) = data;
        Assert.True(mesh.VertexCount > 1000, "this test is only meaningful on a real mesh");

        var warm = BonePalette.BuildWithSegments(skeleton, clip, 0.1f);
        Assert.NotEmpty(warm.Segments);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 50; i++) BonePalette.BuildWithSegments(skeleton, clip, i * 0.01f);
        double overlayOnly = clock.Elapsed.TotalMilliseconds;

        clock.Restart();
        for (int i = 0; i < 50; i++) SkinnedMeshAnimator.Skin(mesh, skeleton, clip, i * 0.01f);
        double fullSkin = clock.Elapsed.TotalMilliseconds;

        Assert.True(overlayOnly < fullSkin,
            $"the overlay-only path took {overlayOnly:0.0} ms and a full CPU skin took {fullSkin:0.0} ms");
    }

    [Fact]
    public void NoSegmentsAreBuiltWhenTheOverlayIsOff()
    {
        // Every frame, with the overlay hidden, this would otherwise be a few hundred wasted multiplies
        // and an allocation.
        if (Ahri() is not { } data) return;
        var (_, skeleton, clip) = data;

        var (palette, segments) = BonePalette.BuildWithSegments(skeleton, clip, 0.3f, wantSegments: false);

        Assert.NotEmpty(palette);
        Assert.Empty(segments);
    }

    [Fact]
    public void ASkeletonlessSubjectHasNoPoseAtAll()
    {
        var empty = new SkeletonAsset
        {
            Bones = Array.Empty<BoneInfo>(),
            Joints = Array.Empty<SkinJoint>(),
            Influences = Array.Empty<short>(),
        };

        var (palette, segments) = BonePalette.BuildWithSegments(empty, null, 0f);

        Assert.NotEmpty(palette);          // a usable bind palette rather than an empty array
        Assert.Empty(segments);
    }

    // ===================================================== the three publishes

    [Fact]
    public void AllThreeOverlaysArePushedEveryFrame()
    {
        // Each is a single assignment that compiles whether or not it is there, and each has a distinct
        // "does nothing" symptom: no bones, no gizmo, no particles.
        if (RepoRoot() is not { } root) return;
        string window = Path.Combine(root, "src", "ReyEngine.App", "Views", "MeshPreviewWindow.Dx11.cs");
        if (!File.Exists(window)) return;
        string text = File.ReadAllText(window);

        Assert.Contains("_dx11.BoneLines = bones", text);
        Assert.Contains("_dx11.ParticlePlayback = vm.Playback", text);
        Assert.Contains("SetGizmoGeometry(", text);
    }

    [Fact]
    public void TheParticleDriverIsGivenItsShaders()
    {
        // The D3D11 particle path draws nothing at all without the cache, and silently: no exception,
        // no log, just no particles.
        if (RepoRoot() is not { } root) return;
        string host = Path.Combine(root, "src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        string window = Path.Combine(root, "src", "ReyEngine.App", "Views", "MeshPreviewWindow.Dx11.cs");
        if (!File.Exists(host) || !File.Exists(window)) return;

        Assert.Contains("MeshPreview.Dx11ShaderCache = _dx11ShaderCache", File.ReadAllText(host));
        Assert.Contains("_dx11.ShaderCache = vm.Dx11ShaderCache", File.ReadAllText(window));
    }

    [Fact]
    public void TheBoneLineChannelReachesTheRenderer()
    {
        if (RepoRoot() is not { } root) return;
        string surface = Path.Combine(root, "src", "ReyEngine.App", "Views", "Dx11ViewportSurface.cs");
        string renderer = Path.Combine(root, "src", "ReyEngine.Rendering.D3D11", "ShaderPreviewRenderer.cs");
        if (!File.Exists(surface) || !File.Exists(renderer)) return;

        Assert.Contains("_renderer.SetBoneLines(BoneLines)", File.ReadAllText(surface));
        string r = File.ReadAllText(renderer);
        Assert.Contains("public void SetBoneLines(", r);
        Assert.Contains("DrawBoneLines(view, proj)", r);
    }

    [Fact]
    public void TheGizmoIsBuiltByTheSameBuilderTheHitTestMeasuresAgainst()
    {
        // If the drawn arm and the measured arm ever came from different code, the gizmo would be
        // grabbable somewhere other than where it is drawn - which is exactly why the map viewport
        // builds it this way.
        if (RepoRoot() is not { } root) return;
        string window = Path.Combine(root, "src", "ReyEngine.App", "Views", "MeshPreviewWindow.Dx11.cs");
        if (!File.Exists(window)) return;
        string text = File.ReadAllText(window);

        Assert.Contains("ViewportMeshRenderer.BuildGizmoAxis(", text);
        Assert.Contains("PreviewViewport.GizmoArmLengthFor(", text);
    }
}
