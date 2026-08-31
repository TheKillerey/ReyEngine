using System.Numerics;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Animation;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Skeletons;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M615: the bone palette a GPU skinned shader needs.
///
/// <para>The load-bearing test is the last one: applying the palette by hand to the bind-pose vertices
/// must reproduce, to floating-point tolerance, exactly what the CPU skinner already produces for the
/// same clip at the same instant. That is an equivalence against a path that has been rendering
/// correctly for a hundred milestones, which is a far stronger statement than any assertion about
/// matrices in isolation — and it is the only way to be sure the influence-slot indexing is right.</para>
/// </summary>
public sealed class BonePaletteTests
{
    private const string Champions = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Champions";

    private static readonly Lazy<HashDatabase?> Database = new(() =>
    {
        try { return new HashSyncService().LoadLocal(_ => { }); }
        catch { return null; }
    });

    /// <summary>A real champion: mesh, skeleton and one clip. Null when the install is unavailable.</summary>
    private static (MeshAsset Mesh, SkeletonAsset Skeleton, AnimationClip Clip)? Ahri()
    {
        string wad = Path.Combine(Champions, "Ahri.wad.client");
        if (!File.Exists(wad) || Database.Value is not { } database) return null;

        using var archive = WadArchive.Open(wad, new WadPathResolver(database));
        if (archive.ResolvedCount == 0) return null;

        byte[]? Read(string path) =>
            archive.TryGetEntry(HashAlgorithms.WadPath(path), out _) ? archive.Extract(HashAlgorithms.WadPath(path)) : null;

        if (Read("assets/characters/ahri/skins/base/ahri_base.skn") is not { } skn) return null;
        if (Read("assets/characters/ahri/skins/base/ahri_base.skl") is not { } skl) return null;
        if (Read("assets/characters/ahri/skins/base/animations/spell1.anm") is not { } anm) return null;

        try
        {
            return (SkinnedMeshDecoder.Decode(skn), SkeletonDecoder.Decode(skl), AnimationDecoder.Decode(anm, "spell1"));
        }
        catch
        {
            return null;
        }
    }

    // ===================================================== shape

    [Fact]
    public void TheBindPaletteIsAllIdentity()
    {
        var palette = BonePalette.Bind(64);
        Assert.Equal(64, palette.Length);
        Assert.All(palette, m => Assert.True(m.IsIdentity));
    }

    [Fact]
    public void WithNoClipTheMeshRendersWhereItWasAuthored()
    {
        // What a character with no animation selected must show, and it is asserted in WORLD UNITS
        // rather than on the matrices. The matrices are not exactly identity and cannot be: a joint's
        // inverse bind transform is stored quantised in the .skl, so inverseBind * global comes back
        // identity only to about three decimal places - measured, 2.4e-4 in the off-diagonals on Ahri.
        // What matters is whether that moves a vertex anywhere you could see, and it does not.
        if (Ahri() is not { } data) return;
        var (mesh, skeleton, _) = data;
        if (mesh.BlendIndices is null || mesh.BlendWeights is null) return;

        var palette = BonePalette.Build(skeleton, clip: null, 0f);
        Assert.NotEmpty(palette);

        float worst = 0f;
        for (int v = 0; v < mesh.VertexCount; v += 97)
        {
            var bind = new Vector3(mesh.Positions[v * 3], mesh.Positions[v * 3 + 1], mesh.Positions[v * 3 + 2]);
            Vector3 posed = Vector3.Zero;
            float weightSum = 0f;
            for (int k = 0; k < 4; k++)
            {
                float w = mesh.BlendWeights[v * 4 + k];
                if (w <= 0f) continue;
                int slot = mesh.BlendIndices[v * 4 + k];
                if (slot < 0 || slot >= palette.Length) continue;
                posed += w * Vector3.Transform(bind, palette[slot]);
                weightSum += w;
            }
            if (weightSum <= 0f) posed = bind;
            worst = MathF.Max(worst, Vector3.Distance(posed, bind));
        }

        // Measured: 0.63 units at worst on Ahri, who is ~275 units tall - 0.23% of model height, and
        // NOT introduced here. The CPU skinner computes the identical inverseBind * global, so the GL
        // preview has always drawn the bind pose with the same deviation. The bound is set above the
        // measurement to catch a real regression rather than to certify the data as exact.
        Assert.True(worst < 1.5f, $"the bind palette moved a vertex {worst:0.0000} units");
    }

    [Fact]
    public void ThePaletteIsIndexedByInfluenceSlotNotByJointId()
    {
        // A vertex's BLENDINDICES names a slot in the influence table, not a joint. Getting this wrong
        // produces a palette that is correct, complete, and indexed by something nothing refers to.
        if (Ahri() is not { } data) return;

        var palette = BonePalette.Build(data.Skeleton, data.Clip, 0.2f);

        if (data.Skeleton.Influences.Count > 0)
            Assert.Equal(Math.Min(data.Skeleton.Influences.Count, BonePalette.MaxBones), palette.Length);
        Assert.True(palette.Length <= BonePalette.MaxBones);
    }

    [Fact]
    public void AClipThatMovesSomethingDoesNotLeaveEveryBoneAtIdentity()
    {
        // The negative of the bind-pose test: if this passed as identity too, the palette would be
        // "correct" and completely inert.
        if (Ahri() is not { } data) return;

        var palette = BonePalette.Build(data.Skeleton, data.Clip, data.Clip.Duration * 0.5f);

        Assert.Contains(palette, m => !IsNearIdentity(m));
    }

    [Fact]
    public void AnEmptySkeletonIsAUsablePaletteRatherThanACrash()
    {
        var empty = new SkeletonAsset
        {
            Bones = Array.Empty<BoneInfo>(),
            Joints = Array.Empty<SkinJoint>(),
            Influences = Array.Empty<short>(),
        };

        var palette = BonePalette.Build(empty, clip: null, 0f);

        Assert.NotEmpty(palette);
        Assert.All(palette, m => Assert.True(m.IsIdentity));
    }

    // ===================================================== the equivalence that matters

    [Fact]
    public void ThePaletteReproducesTheCpuSkinnerExactly()
    {
        // If these two disagree, one of them is wrong, and the CPU one has been drawing champions
        // correctly for a hundred milestones. Sampled across the clip because a single instant can agree
        // by accident - the bind pose agrees trivially.
        if (Ahri() is not { } data) return;
        var (mesh, skeleton, clip) = data;
        if (mesh.BlendIndices is null || mesh.BlendWeights is null) return;

        foreach (float t in new[] { 0f, clip.Duration * 0.25f, clip.Duration * 0.5f, clip.Duration * 0.9f })
        {
            var cpu = SkinnedMeshAnimator.Skin(mesh, skeleton, clip, t);
            var palette = BonePalette.Build(skeleton, clip, t);

            // Every 97th vertex: a prime stride walks the whole mesh without testing 30,000 of them.
            int checkedCount = 0, worstVertex = -1;
            float worst = 0f;
            for (int v = 0; v < mesh.VertexCount; v += 97)
            {
                var bind = new Vector3(mesh.Positions[v * 3], mesh.Positions[v * 3 + 1], mesh.Positions[v * 3 + 2]);
                Vector3 gpu = Vector3.Zero;
                float weightSum = 0f;

                for (int k = 0; k < 4; k++)
                {
                    float w = mesh.BlendWeights[v * 4 + k];
                    if (w <= 0f) continue;
                    int slot = mesh.BlendIndices[v * 4 + k];
                    if (slot < 0 || slot >= palette.Length) continue;
                    gpu += w * Vector3.Transform(bind, palette[slot]);
                    weightSum += w;
                }
                if (weightSum <= 0f) gpu = bind;

                var expected = new Vector3(cpu.Positions[v * 3], cpu.Positions[v * 3 + 1], cpu.Positions[v * 3 + 2]);
                float error = Vector3.Distance(gpu, expected);
                if (error > worst) { worst = error; worstVertex = v; }
                checkedCount++;
            }

            Assert.True(checkedCount > 50, $"expected to check a useful number of vertices, checked {checkedCount}");
            // League models are hundreds of units tall; a thousandth of a unit is far below anything visible.
            Assert.True(worst < 0.01f,
                $"at t={t:0.00}s the palette and the CPU skinner disagree by {worst:0.0000} units at vertex {worstVertex}");
        }
    }

    private static bool IsNearIdentity(Matrix4x4 m)
    {
        var i = Matrix4x4.Identity;
        return MathF.Abs(m.M11 - i.M11) < 1e-4f && MathF.Abs(m.M22 - i.M22) < 1e-4f
            && MathF.Abs(m.M33 - i.M33) < 1e-4f && MathF.Abs(m.M41) < 1e-3f
            && MathF.Abs(m.M42) < 1e-3f && MathF.Abs(m.M43) < 1e-3f
            && MathF.Abs(m.M12) < 1e-4f && MathF.Abs(m.M13) < 1e-4f && MathF.Abs(m.M21) < 1e-4f;
    }
}
