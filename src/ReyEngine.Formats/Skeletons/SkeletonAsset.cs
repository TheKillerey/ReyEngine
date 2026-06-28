using System.Numerics;

namespace ReyEngine.Formats.Skeletons;

public sealed class SkeletonAsset
{
    public required IReadOnlyList<BoneInfo> Bones { get; init; }
    public required IReadOnlyList<SkinJoint> Joints { get; init; }
    public required IReadOnlyList<short> Influences { get; init; }
    public int BoneCount => Bones.Count;
}

/// <summary>A joint with its global bind-pose position (for the debug bone overlay).</summary>
public sealed record BoneInfo(string Name, int Index, int ParentIndex, Vector3 WorldPosition);

/// <summary>Full joint data needed for skinning + animation matching (AnimHash = Elf of the joint name).</summary>
public sealed record SkinJoint(
    string Name, int Id, int ParentId, uint AnimHash,
    Matrix4x4 LocalTransform, Matrix4x4 InverseBindTransform);
