using LeagueToolkit.Core.Animation;
using LeagueToolkit.Core.Mesh;
using ReyEngine.Formats.Animation;

namespace ReyEngine.Formats.Characters;

/// <summary>One file's upgrade: what it was, what it is now, and whether the bytes changed.</summary>
public sealed record CharacterFileUpgrade(CharacterFileFormat Before, CharacterFileFormat After, byte[] Bytes, bool Changed);

/// <summary>
/// M695: bring an old character file to the form Riot's tools write today.
///
/// <para>The mesh and the rig go through LeagueToolkit's own reader and writer - it reads every shipped
/// skn version and both rig families, and writes skn 4.1 and the format-token rig. The animation is
/// sampled frame by frame and written by <see cref="AnimationWriter"/> as uncompressed v5; a compressed
/// clip is already current and is left alone, because sampling it into v5 would trade Riot's smaller
/// file for a larger one that plays the same.</para>
///
/// <para>Nothing here touches disk. A caller that wants to keep the original keeps its bytes.</para>
/// </summary>
public static class CharacterFormatUpgrader
{
    /// <summary>Upgrade by header. A file that is current, unreadable or not a character file comes back
    /// unchanged with <see cref="CharacterFileUpgrade.Changed"/> false; an unreadable one is reported by
    /// its <see cref="CharacterFileFormat.Note"/>.</summary>
    public static CharacterFileUpgrade Upgrade(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var before = CharacterFileFormats.Inspect(bytes);
        if (!before.CanUpgrade) return new(before, before, bytes, false);
        byte[] upgraded = before.Kind switch
        {
            CharacterFileKind.SimpleSkin => UpgradeSimpleSkin(bytes),
            CharacterFileKind.Skeleton => UpgradeSkeleton(bytes),
            CharacterFileKind.Animation => UpgradeAnimation(bytes),
            _ => bytes,
        };
        var after = CharacterFileFormats.Inspect(upgraded);
        return new(before, after, upgraded, !ReferenceEquals(upgraded, bytes));
    }

    /// <summary>Any readable skn, rewritten as 4.1 with its ranges, vertex type and bounds.</summary>
    public static byte[] UpgradeSimpleSkin(byte[] skn)
    {
        using var input = new MemoryStream(skn, writable: false);
        using var mesh = SkinnedMesh.ReadFromSimpleSkin(input, leaveOpen: true);
        using var output = new MemoryStream();
        mesh.WriteSimpleSkin(output, leaveOpen: true);
        return output.ToArray();
    }

    /// <summary>A legacy <c>r3d2sklt</c> rig (or a current one), rewritten as the format-token form:
    /// joint hashes, influences, names - the legacy joints' world transforms become local + inverse-bind.</summary>
    public static byte[] UpgradeSkeleton(byte[] skl)
    {
        using var input = new MemoryStream(skl, writable: false);
        var rig = new RigResource(input);
        using var output = new MemoryStream();
        rig.Write(output);
        return output.ToArray();
    }

    /// <summary>An uncompressed v3 / v4 (or v5) clip, sampled at its own frame rate and written as v5.
    /// A compressed clip is returned as it is.</summary>
    public static byte[] UpgradeAnimation(byte[] anm)
    {
        var format = CharacterFileFormats.Inspect(anm);
        if (format.Kind != CharacterFileKind.Animation || !format.IsReadable) throw new InvalidOperationException($"Not a readable animation: {format.Label}.");
        if (format.Label.StartsWith("ANM compressed", StringComparison.Ordinal)) return anm;
        var clip = AnimationDecoder.Decode(anm, "upgrade");
        int frames = AnimationWriter.FrameCount(clip.Duration, clip.Fps, sourceIsCompressed: false);
        return AnimationWriter.WriteUncompressedV5(clip, frames);
    }
}
