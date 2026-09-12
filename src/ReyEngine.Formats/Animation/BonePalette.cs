using System.Numerics;
using ReyEngine.Formats.Skeletons;

namespace ReyEngine.Formats.Animation;

/// <summary>
/// The GPU skinning palette: one matrix per influence slot, InverseBind * Global at the clip's time.
///
/// <para>M694: built on <see cref="SkeletonPose"/> - the skeleton's index is cached on the skeleton and
/// the scratch lives in a caller-owned <see cref="PoseBuffer"/>, so a driver that poses the same skin every
/// frame allocates nothing after the first. The parameterless <see cref="Build(SkeletonAsset, AnimationClip?, float)"/>
/// keeps its old shape (a fresh array each call) for the callers that want one.</para>
/// </summary>
public static class BonePalette
{
    public const int MaxBones = 256;

    public static Matrix4x4[] Bind(int count)
    {
        var palette = new Matrix4x4[Math.Clamp(count, 1, MaxBones)];
        Array.Fill(palette, Matrix4x4.Identity);
        return palette;
    }

    /// <summary>A fresh palette for <paramref name="clip"/> at <paramref name="time"/>.</summary>
    public static Matrix4x4[] Build(SkeletonAsset skeleton, AnimationClip? clip, float time) =>
        Build(skeleton, clip, time, new PoseBuffer(), null);

    /// <summary>
    /// The palette into <paramref name="reuse"/> when it is the right length (the array handed back IS
    /// the one filled, so a material that holds it sees every frame's values), else a new one.
    /// <paramref name="scratch"/> is the caller's pose buffer, reused across frames.
    /// </summary>
    public static Matrix4x4[] Build(SkeletonAsset skeleton, AnimationClip? clip, float time, PoseBuffer scratch, Matrix4x4[]? reuse)
    {
        var index = SkeletonIndex.For(skeleton);
        if (skeleton.Joints.Count == 0)
        {
            if (reuse is { Length: 1 }) { reuse[0] = Matrix4x4.Identity; return reuse; }
            return Bind(1);
        }
        SkeletonPose.ComputeSkin(index, clip, time, scratch);
        var palette = reuse is not null && reuse.Length == index.PaletteLength ? reuse : new Matrix4x4[index.PaletteLength];
        FillPalette(index, scratch, palette);
        return palette;
    }

    /// <summary>
    /// M619: the palette AND the bone segments, from one walk of the skeleton.
    ///
    /// <para>The GL viewport gets its segments from <see cref="SkinnedMeshAnimator.Skin"/>, which computes
    /// them as a by-product of transforming every vertex on the CPU. A GPU-skinned path has no reason to
    /// pay that: the segments are joint positions, and the joint globals are already computed here. On a
    /// 108-bone champion this is a few hundred multiplies instead of a pass over 30,000 vertices, every
    /// frame the overlay is on.</para>
    /// </summary>
    public static (Matrix4x4[] Palette, float[] Segments) BuildWithSegments(
        SkeletonAsset skeleton, AnimationClip? clip, float time, bool wantSegments = true)
    {
        var joints = skeleton.Joints;
        if (joints.Count == 0) return (Bind(1), Array.Empty<float>());

        var index = SkeletonIndex.For(skeleton);
        var scratch = new PoseBuffer();
        SkeletonPose.ComputeSkin(index, clip, time, scratch);
        var palette = new Matrix4x4[index.PaletteLength];
        FillPalette(index, scratch, palette);

        if (!wantSegments) return (palette, Array.Empty<float>());
        return (palette, Segments(index, scratch));
    }

    /// <summary>One line per joint that has a parent, exactly the pairs the GL overlay draws.</summary>
    public static float[] Segments(SkeletonIndex index, PoseBuffer scratch)
    {
        var joints = index.Skeleton.Joints;
        var segments = new List<float>(joints.Count * 6);
        foreach (var j in joints)
        {
            if (j.ParentId < 0 || (uint)j.ParentId >= (uint)index.ById.Length || index.ById[j.ParentId] is null) continue;
            var a = SkeletonPose.GlobalOf(scratch, j.Id).Translation;
            var b = SkeletonPose.GlobalOf(scratch, j.ParentId).Translation;
            segments.Add(a.X); segments.Add(a.Y); segments.Add(a.Z);
            segments.Add(b.X); segments.Add(b.Y); segments.Add(b.Z);
        }
        return segments.ToArray();
    }

    /// <summary>Indexed by influence slot, because that is what a vertex's BLENDINDICES holds. A skeleton
    /// with no influence table indexes joints directly - the same fallback the CPU skinner uses.</summary>
    private static void FillPalette(SkeletonIndex index, PoseBuffer scratch, Matrix4x4[] palette)
    {
        var influences = index.Skeleton.Influences;
        var skin = scratch.Skin;
        for (int slot = 0; slot < palette.Length; slot++)
        {
            int jointId = influences.Count > 0 ? influences[slot] : slot;
            palette[slot] = (uint)jointId <= (uint)index.MaxId ? skin[jointId] : Matrix4x4.Identity;
        }
    }

    public static IReadOnlyDictionary<string, Matrix4x4> Globals(
        SkeletonAsset skeleton, AnimationClip? clip, float time)
    {
        var joints = skeleton.Joints;
        var globals = new Dictionary<string, Matrix4x4>(joints.Count, StringComparer.OrdinalIgnoreCase);
        if (joints.Count == 0) return globals;
        var index = SkeletonIndex.For(skeleton);
        var scratch = new PoseBuffer();
        SkeletonPose.ComputeSkin(index, clip, time, scratch);
        // By NAME, and case-insensitively, because that is how a ParticleEventData names its bone.
        foreach (var j in joints) globals[j.Name] = SkeletonPose.GlobalOf(scratch, j.Id);
        return globals;
    }
}
