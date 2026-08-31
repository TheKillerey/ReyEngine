using System.Numerics;
using ReyEngine.Formats.Skeletons;

namespace ReyEngine.Formats.Animation;

/// <summary>
/// M615: the per-bone skinning matrices a GPU skinned shader needs, indexed the way the vertices index
/// them.
///
/// <para>The CPU skinner in <see cref="SkinnedMeshAnimator"/> already computes exactly these — it just
/// consumes them immediately and throws them away, transforming every vertex on the CPU. A GPU path
/// needs the same matrices handed over as an array instead.</para>
///
/// <para>The indexing is the part that is easy to get wrong. A vertex's BLENDINDICES do NOT name joints:
/// they name slots in the skeleton's <c>Influences</c> table, which then names the joint. So the palette
/// is built per influence SLOT, not per joint — building it per joint would produce a palette that is
/// correct, complete, and indexed by something the vertices never refer to, which shows up as a mesh
/// exploded across the scene rather than as an obvious failure.</para>
/// </summary>
public static class BonePalette
{
    /// <summary>League caps a skinned draw at 256 bones and the shipped BonesCB is sized for it.</summary>
    public const int MaxBones = 256;

    /// <summary>The bind pose — every matrix identity. What a palette must contain for a mesh to render
    /// exactly as authored.</summary>
    public static Matrix4x4[] Bind(int count)
    {
        var palette = new Matrix4x4[Math.Clamp(count, 1, MaxBones)];
        Array.Fill(palette, Matrix4x4.Identity);
        return palette;
    }

    /// <summary>
    /// The skinning matrices for one instant of a clip, indexed by influence slot.
    /// </summary>
    /// <param name="clip">Null for the bind pose, which is a legitimate thing to ask for — it is what a
    /// character with no animation selected should show.</param>
    public static Matrix4x4[] Build(SkeletonAsset skeleton, AnimationClip? clip, float time) =>
        BuildWithSegments(skeleton, clip, time, wantSegments: false).Palette;

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
        var pose = new Dictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)>();
        if (clip is not null)
            try { clip.Evaluate(time, pose); }
            catch { /* a clip that will not evaluate falls back to the bind pose, as the CPU skinner does */ }

        var joints = skeleton.Joints;
        if (joints.Count == 0) return (Bind(1), Array.Empty<float>());

        int maxId = 0;
        foreach (var j in joints) maxId = Math.Max(maxId, j.Id);

        var byId = new Dictionary<int, SkinJoint>(joints.Count);
        foreach (var j in joints) byId[j.Id] = j;

        var global = new Matrix4x4[maxId + 1];
        var done = new bool[maxId + 1];

        Matrix4x4 Global(int id)
        {
            if ((uint)id > maxId || !byId.TryGetValue(id, out var j)) return Matrix4x4.Identity;
            if (done[id]) return global[id];
            done[id] = true;                                  // guard against cycles, as the CPU skinner does

            var local = pose.TryGetValue(j.AnimHash, out var trs)
                ? Compose(trs.Translation, trs.Rotation, trs.Scale)
                : j.LocalTransform;

            global[id] = j.ParentId >= 0 && byId.ContainsKey(j.ParentId) ? local * Global(j.ParentId) : local;
            return global[id];
        }

        var skin = new Matrix4x4[maxId + 1];
        Array.Fill(skin, Matrix4x4.Identity);
        foreach (var j in joints) skin[j.Id] = j.InverseBindTransform * Global(j.Id);

        // Indexed by influence slot, because that is what a vertex's BLENDINDICES holds. A skeleton with
        // no influence table indexes joints directly — the same fallback the CPU skinner uses.
        var influences = skeleton.Influences;
        int count = influences.Count > 0 ? influences.Count : maxId + 1;
        var palette = new Matrix4x4[Math.Clamp(count, 1, MaxBones)];

        for (int slot = 0; slot < palette.Length; slot++)
        {
            int jointId = influences.Count > 0 ? influences[slot] : slot;
            palette[slot] = (uint)jointId <= maxId ? skin[jointId] : Matrix4x4.Identity;
        }

        if (!wantSegments) return (palette, Array.Empty<float>());

        // One line per joint that has a parent, exactly the pairs the GL overlay draws.
        var segments = new List<float>(joints.Count * 6);
        foreach (var j in joints)
        {
            if (j.ParentId < 0 || !byId.ContainsKey(j.ParentId)) continue;
            var a = Global(j.Id).Translation;
            var b = Global(j.ParentId).Translation;
            segments.Add(a.X); segments.Add(a.Y); segments.Add(a.Z);
            segments.Add(b.X); segments.Add(b.Y); segments.Add(b.Z);
        }
        return (palette, segments.ToArray());
    }

    /// <summary>Same composition the CPU skinner uses, kept identical so the two paths cannot drift.</summary>
    private static Matrix4x4 Compose(Vector3 translation, Quaternion rotation, Vector3 scale) =>
        Matrix4x4.CreateScale(scale)
        * Matrix4x4.CreateFromQuaternion(rotation)
        * Matrix4x4.CreateTranslation(translation);
}
