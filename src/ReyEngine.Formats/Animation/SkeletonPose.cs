using System.Numerics;
using System.Runtime.CompilerServices;
using ReyEngine.Formats.Skeletons;

namespace ReyEngine.Formats.Animation;

/// <summary>
/// M694: what a skeleton's pose evaluation needs to know about the skeleton, computed ONCE per skeleton
/// and cached on it. <see cref="BonePalette"/> and <see cref="SkinnedMeshAnimator"/> used to rebuild the
/// id-to-joint dictionary and re-scan for the highest id on every call - per animated skin per frame -
/// and allocate the global, done and skin arrays each time. On the jade map that was 355 KB of garbage
/// per frame for 21 skins before a single vertex moved.
/// </summary>
public sealed class SkeletonIndex
{
    private static readonly ConditionalWeakTable<SkeletonAsset, SkeletonIndex> Cache = new();

    public SkeletonAsset Skeleton { get; }
    /// <summary>The highest joint id; arrays indexed by id are MaxId + 1 long.</summary>
    public int MaxId { get; }
    /// <summary>Joint by id, null for an id no joint carries.</summary>
    public SkinJoint?[] ById { get; }
    /// <summary>The palette length the skeleton's influence table implies (the CPU skinner's fallback
    /// indexes joints directly when the table is empty), clamped to <see cref="BonePalette.MaxBones"/>.</summary>
    public int PaletteLength { get; }

    private SkeletonIndex(SkeletonAsset skeleton)
    {
        Skeleton = skeleton;
        int maxId = 0;
        foreach (var j in skeleton.Joints) maxId = Math.Max(maxId, j.Id);
        MaxId = maxId;
        ById = new SkinJoint?[maxId + 1];
        foreach (var j in skeleton.Joints) if (j.Id >= 0) ById[j.Id] = j;
        int count = skeleton.Influences.Count > 0 ? skeleton.Influences.Count : maxId + 1;
        PaletteLength = Math.Clamp(count, 1, BonePalette.MaxBones);
    }

    public static SkeletonIndex For(SkeletonAsset skeleton) => Cache.GetValue(skeleton, s => new SkeletonIndex(s));
}

/// <summary>
/// M694: the scratch a pose evaluation writes into - the clip's TRS per animated joint, the global and
/// skin matrix per joint id - owned by the caller and reused frame after frame. One per animated
/// mesh in a driver; never shared between threads (the CPU deform that reads <see cref="Skin"/> may run
/// on another thread, but only after the pose for that buffer has been computed and before the next
/// one is asked for).
/// </summary>
public sealed class PoseBuffer
{
    public readonly Dictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> Pose = new();
    public Matrix4x4[] Global = Array.Empty<Matrix4x4>();
    public bool[] Done = Array.Empty<bool>();
    /// <summary>InverseBind * Global per joint id; identity for ids without a joint.</summary>
    public Matrix4x4[] Skin = Array.Empty<Matrix4x4>();
    /// <summary>The last skeleton this buffer was sized for.</summary>
    public SkeletonIndex? Index { get; private set; }

    internal void Size(SkeletonIndex index)
    {
        if (ReferenceEquals(Index, index) && Skin.Length == index.MaxId + 1) return;
        Index = index;
        Global = new Matrix4x4[index.MaxId + 1];
        Done = new bool[index.MaxId + 1];
        Skin = new Matrix4x4[index.MaxId + 1];
    }
}

/// <summary>M694: the one pose walk both the palette builder and the CPU skinner used to carry in their
/// own copies - now shared, cached per skeleton and allocation-free after the first frame.</summary>
public static class SkeletonPose
{
    /// <summary>
    /// Evaluate <paramref name="clip"/> at <paramref name="time"/> over <paramref name="index"/>'s skeleton
    /// into <paramref name="buffer"/>: <see cref="PoseBuffer.Global"/> per joint id and
    /// <see cref="PoseBuffer.Skin"/> = InverseBind * Global. A null clip, or one that will not evaluate,
    /// is the bind pose - exactly the fallback the two callers always had.
    /// </summary>
    public static void ComputeSkin(SkeletonIndex index, AnimationClip? clip, float time, PoseBuffer buffer)
    {
        buffer.Size(index);
        var pose = buffer.Pose;
        pose.Clear();
        if (clip is not null)
            try { clip.Evaluate(time, pose); }
            catch { pose.Clear(); /* a clip that will not evaluate falls back to the bind pose */ }

        var byId = index.ById;
        var global = buffer.Global;
        var done = buffer.Done;
        var skin = buffer.Skin;
        Array.Clear(done);
        Array.Fill(skin, Matrix4x4.Identity);

        Matrix4x4 Global(int id)
        {
            if ((uint)id >= (uint)byId.Length || byId[id] is not { } j) return Matrix4x4.Identity;
            if (done[id]) return global[id];
            done[id] = true;                                  // guard against cycles, as before
            var local = pose.TryGetValue(j.AnimHash, out var trs)
                ? Compose(trs.Translation, trs.Rotation, trs.Scale)
                : j.LocalTransform;
            global[id] = j.ParentId >= 0 && (uint)j.ParentId < (uint)byId.Length && byId[j.ParentId] is not null
                ? local * Global(j.ParentId)
                : local;
            return global[id];
        }

        foreach (var j in index.Skeleton.Joints)
            if (j.Id >= 0) skin[j.Id] = j.InverseBindTransform * Global(j.Id);
        // joints nobody skins still need their global for the segment overlay and the bone lookups
        foreach (var j in index.Skeleton.Joints) if (j.Id >= 0) Global(j.Id);
    }

    /// <summary>The joint global of <paramref name="id"/> after <see cref="ComputeSkin"/>; identity for an
    /// id that carries no joint.</summary>
    public static Matrix4x4 GlobalOf(PoseBuffer buffer, int id) =>
        (uint)id < (uint)buffer.Global.Length && buffer.Index?.ById[id] is not null ? buffer.Global[id] : Matrix4x4.Identity;

    public static Matrix4x4 Compose(Vector3 translation, Quaternion rotation, Vector3 scale) =>
        Matrix4x4.CreateScale(scale)
        * Matrix4x4.CreateFromQuaternion(rotation)
        * Matrix4x4.CreateTranslation(translation);
}
