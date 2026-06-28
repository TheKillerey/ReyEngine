using System.Numerics;

namespace ReyEngine.Formats.Skeletons;

public sealed class SkeletonAsset
{
    public required IReadOnlyList<BoneInfo> Bones { get; init; }
    public int BoneCount => Bones.Count;
}

/// <summary>A joint with its global bind-pose position (for the debug bone overlay).</summary>
public sealed record BoneInfo(string Name, int Index, int ParentIndex, Vector3 WorldPosition);
