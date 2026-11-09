using System.Numerics;
using LeagueToolkit.Core.Animation;

namespace ReyEngine.Formats.Animation;

/// <summary>A decoded .anm animation. Joints are keyed by Elf hash of their name.</summary>
public sealed class AnimationClip
{
    private readonly IAnimationAsset? _asset;
    private readonly CompressedAnimationTracks? _tracks;

    public string Name { get; }
    public float Duration => _tracks?.Duration ?? _asset!.Duration;
    public float Fps => _tracks?.Fps ?? _asset!.Fps;

    internal AnimationClip(IAnimationAsset asset, string name)
    {
        _asset = asset;
        Name = name;
    }

    internal AnimationClip(CompressedAnimationTracks tracks, string name)
    {
        _tracks = tracks;
        Name = name;
    }

    /// <summary>Fill <paramref name="pose"/> with local TRS per joint hash at the given time.</summary>
    public void Evaluate(float time, IDictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> pose)
        => Evaluate(time, pose, null);

    /// <summary>As <see cref="Evaluate(float, IDictionary{uint, ValueTuple{Quaternion, Vector3, Vector3}})"/>; a compressed clip that
    /// omits a track of a joint takes that component from <paramref name="bindLocal"/> (the skeleton's local TRS by joint hash).</summary>
    public void Evaluate(float time, IDictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> pose,
        Func<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)?>? bindLocal)
    {
        if (_tracks is not null) _tracks.Evaluate(time, pose, bindLocal);
        else _asset!.Evaluate(time, pose);
    }
}

public static class AnimationDecoder
{
    public static AnimationClip Decode(byte[] data, string name)
    {
        // M889: a compressed clip that omits tracks breaks LTK's hot-frame evaluator on every seek; evaluate it here instead.
        // Every other clip keeps LTK's decoder.
        if (CompressedAnimationTracks.TryParse(data) is { AbsentTracks: > 0 } tracks)
            return new AnimationClip(tracks, name);
        using var ms = new MemoryStream(data, writable: false);
        return new AnimationClip(AnimationAsset.Load(ms), name);
    }
}
