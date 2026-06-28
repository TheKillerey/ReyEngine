using System.Numerics;
using LeagueToolkit.Core.Animation;

namespace ReyEngine.Formats.Animation;

/// <summary>A decoded .anm animation. Joints are keyed by Elf hash of their name.</summary>
public sealed class AnimationClip
{
    private readonly IAnimationAsset _asset;

    public string Name { get; }
    public float Duration => _asset.Duration;
    public float Fps => _asset.Fps;

    internal AnimationClip(IAnimationAsset asset, string name)
    {
        _asset = asset;
        Name = name;
    }

    /// <summary>Fill <paramref name="pose"/> with local TRS per joint hash at the given time.</summary>
    public void Evaluate(float time, IDictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> pose)
        => _asset.Evaluate(time, pose);
}

public static class AnimationDecoder
{
    public static AnimationClip Decode(byte[] data, string name)
    {
        using var ms = new MemoryStream(data, writable: false);
        return new AnimationClip(AnimationAsset.Load(ms), name);
    }
}
