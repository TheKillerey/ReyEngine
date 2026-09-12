using System.Numerics;
using System.Text;
using LeagueToolkit.Core.Primitives;

namespace ReyEngine.Formats.Animation;

/// <summary>
/// M695: writes an animation as an uncompressed <c>r3d2anmd</c> version 5 - the form Riot's exporter
/// writes today and the one LeagueToolkit has no writer for (it reads v3/v4/v5 and the compressed
/// family, and only evaluates them).
///
/// <para><b>Layout, pinned against a shipped v5 file</b> (Map11's 04acb7e91c90c2bd.anm, 62 tracks, 11
/// frames): 8-byte magic, u32 version 5, then a 64-byte block the reader addresses RELATIVE TO BYTE 12 -
/// resource size (file length minus 12), format token 0, version 0, flags 0, track count, frame count,
/// frame duration, six offsets (joint hashes, asset name 0, time 0, vector palette, quaternion palette,
/// frames) and 12 bytes of padding, so the data starts at byte 76. Data order is the one both readers
/// assume when they size a section by the NEXT offset: vectors (12 bytes), quaternions (6-byte
/// quantized), joint hashes (u32), frames (u16 translation, u16 scale, u16 rotation per track per frame).</para>
///
/// <para>The source is anything <see cref="AnimationClip"/> can evaluate. Every frame is sampled at its
/// own time, so a legacy v3 clip (joints by name, no scale), a v4 clip and a compressed clip all come
/// out as the same v5, and the sampled pose is what the client would have played.</para>
/// </summary>
public static class AnimationWriter
{
    private const int HeaderSize = 76;
    private const int MaxPalette = ushort.MaxValue;

    /// <summary>Frames a clip of <paramref name="duration"/> at <paramref name="fps"/> spans. An
    /// uncompressed source counted its frames as duration times fps; a compressed source authored a
    /// duration and its last frame must land on it.</summary>
    public static int FrameCount(float duration, float fps, bool sourceIsCompressed)
    {
        int frames = sourceIsCompressed
            ? (int)MathF.Floor(duration * fps + 1e-4f) + 1
            : (int)MathF.Round(duration * fps);
        return Math.Max(1, frames);
    }

    /// <summary>Sample <paramref name="clip"/> at every frame and write it as v5.</summary>
    /// <exception cref="InvalidOperationException">More than 65,535 distinct vectors or rotations - a
    /// u16 index cannot address the palette.</exception>
    public static byte[] WriteUncompressedV5(AnimationClip clip, int frameCount)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (frameCount < 1) throw new ArgumentOutOfRangeException(nameof(frameCount));
        float fps = clip.Fps > 0 ? clip.Fps : 30f;
        float frameDuration = 1f / fps;

        // the tracks: whatever the clip poses at frame 0, in the order the asset enumerates them
        var pose = new Dictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)>();
        clip.Evaluate(0f, pose);
        var joints = pose.Keys.ToArray();
        if (joints.Length == 0) throw new InvalidOperationException("The animation poses no joints.");

        var vectors = new List<Vector3>();
        var vectorIndex = new Dictionary<Vector3, ushort>();
        var quats = new List<ulong>();                    // the 6 quantized bytes, packed
        var quatIndex = new Dictionary<ulong, ushort>();
        var frames = new ushort[frameCount * joints.Length * 3];
        var q6 = new byte[6];

        ushort VectorId(Vector3 v)
        {
            if (vectorIndex.TryGetValue(v, out var id)) return id;
            if (vectors.Count >= MaxPalette) throw new InvalidOperationException("The animation has more than 65,535 distinct translations and scales.");
            id = (ushort)vectors.Count;
            vectors.Add(v);
            vectorIndex[v] = id;
            return id;
        }
        ushort QuatId(Quaternion q)
        {
            QuantizedQuaternion.Compress(Quaternion.Normalize(q), q6);
            ulong packed = q6[0] | (ulong)q6[1] << 8 | (ulong)q6[2] << 16 | (ulong)q6[3] << 24 | (ulong)q6[4] << 32 | (ulong)q6[5] << 40;
            if (quatIndex.TryGetValue(packed, out var id)) return id;
            if (quats.Count >= MaxPalette) throw new InvalidOperationException("The animation has more than 65,535 distinct rotations.");
            id = (ushort)quats.Count;
            quats.Add(packed);
            quatIndex[packed] = id;
            return id;
        }

        for (int f = 0; f < frameCount; f++)
        {
            // the same expression the evaluator uses for a frame's own time, so it lands ON the frame
            if (f > 0) clip.Evaluate(f * frameDuration, pose);
            int at = f * joints.Length * 3;
            for (int t = 0; t < joints.Length; t++, at += 3)
            {
                var (rotation, translation, scale) = pose[joints[t]];
                frames[at] = VectorId(translation);
                frames[at + 1] = VectorId(scale);
                frames[at + 2] = QuatId(rotation);
            }
        }

        int vectorsOffset = HeaderSize;
        int quatsOffset = vectorsOffset + vectors.Count * 12;
        int jointsOffset = quatsOffset + quats.Count * 6;
        int framesOffset = jointsOffset + joints.Length * 4;
        int total = framesOffset + frames.Length * 2;

        using var ms = new MemoryStream(total);
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        bw.Write(Encoding.ASCII.GetBytes("r3d2anmd"));
        bw.Write(5u);
        bw.Write((uint)(total - 12));                     // resource size, as Riot counts it
        bw.Write(0u);                                     // format token
        bw.Write(0u);                                     // version (again)
        bw.Write(0u);                                     // flags
        bw.Write(joints.Length);
        bw.Write(frameCount);
        bw.Write(frameDuration);
        bw.Write(jointsOffset - 12);
        bw.Write(0);                                      // asset name
        bw.Write(0);                                      // time
        bw.Write(vectorsOffset - 12);
        bw.Write(quatsOffset - 12);
        bw.Write(framesOffset - 12);
        bw.Write(0u); bw.Write(0u); bw.Write(0u);         // padding to 76
        foreach (var v in vectors) { bw.Write(v.X); bw.Write(v.Y); bw.Write(v.Z); }
        foreach (ulong packed in quats)
            for (int i = 0; i < 6; i++) bw.Write((byte)(packed >> (8 * i)));
        foreach (uint hash in joints) bw.Write(hash);
        foreach (ushort id in frames) bw.Write(id);
        bw.Flush();
        return ms.ToArray();
    }
}
