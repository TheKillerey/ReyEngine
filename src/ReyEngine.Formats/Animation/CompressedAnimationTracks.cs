using System.Numerics;

namespace ReyEngine.Formats.Animation;

/// <summary>
/// M889: a stateless evaluator for compressed .anm (<c>r3d2canm</c>) clips that omit tracks, which LeagueToolkit's hot-frame
/// evaluator cannot seek. Newer clips (K/DA Firestarter, True Damage Know My Name; header flags 0x3C instead of 0x07) carry no frame
/// for a track that never changes: on akali skin102 idle1.anm 242 of 247 joints have no scale frame, 34 no rotation and 7 no
/// translation, and the jump caches hold 0xFFFF for those tracks. LTK 4.1.0-beta.53 indexes its frame array with that 0xFFFF whenever
/// it re-seeds (a start past the first jump-cache window, a seek, a loop wrap), throws IndexOutOfRangeException and stays broken - the
/// pose walk then falls back to the bind pose on every later frame.
///
/// <para>The layout is the one LTK reads: a 128-byte header (flags @20, joint count @24, frame count @28, duration @36, fps @40,
/// translation and scale min/max @68..115, frame/jump-cache/joint-hash offsets @116 relative to byte 12), 10-byte frames (u16 time
/// over the duration, u16 joint index | type shl 14 with 0 rotation, 1 translation, 2 scale, three u16 values) and one Elf joint hash
/// per joint. Rotations are 48-bit smallest-three quaternions, translations and scales u16 over min/max. Between keys the value is
/// the Catmull-Rom spline over the four surrounding keys of the track (clamped at the ends), parametrized by key spacing when flag 4
/// is set. A track with one key is constant; an absent track takes the joint's bind value.</para>
/// </summary>
public sealed class CompressedAnimationTracks
{
    private const int HeaderSize = 128;

    private readonly struct Track
    {
        public readonly float[] Times;
        public readonly Vector4[] Values;   // xyz for vectors, xyzw for rotations
        public Track(float[] t, Vector4[] v) { Times = t; Values = v; }
        public bool IsEmpty => Times is null || Times.Length == 0;
    }

    private readonly uint[] _jointHashes;
    private readonly Track[] _rotation, _translation, _scale;
    private readonly bool _parametrized;

    public float Duration { get; }
    public float Fps { get; }
    public int Flags { get; }
    public int JointCount => _jointHashes.Length;
    /// <summary>Joint tracks (rotation, translation and scale counted separately) the file carries no frame for.</summary>
    public int AbsentTracks { get; }

    private CompressedAnimationTracks(uint[] hashes, Track[] r, Track[] t, Track[] s, float duration, float fps, int flags, int absent)
    {
        _jointHashes = hashes; _rotation = r; _translation = t; _scale = s;
        Duration = duration; Fps = fps; Flags = flags; AbsentTracks = absent;
        _parametrized = (flags & 4) != 0;
    }

    public static bool IsCompressed(ReadOnlySpan<byte> data) =>
        data.Length >= HeaderSize && data[..8].SequenceEqual("r3d2canm"u8);

    /// <summary>Parse a compressed clip, or null when <paramref name="data"/> is not one or is malformed.</summary>
    public static CompressedAnimationTracks? TryParse(byte[] data)
    {
        if (!IsCompressed(data)) return null;
        try
        {
            int I(int at) => BitConverter.ToInt32(data, at);
            float F(int at) => BitConverter.ToSingle(data, at);
            int flags = I(20), joints = I(24), frames = I(28);
            float duration = F(36), fps = F(40);
            var tMin = new Vector3(F(68), F(72), F(76)); var tMax = new Vector3(F(80), F(84), F(88));
            var sMin = new Vector3(F(92), F(96), F(100)); var sMax = new Vector3(F(104), F(108), F(112));
            int framesAt = I(116) + 12, hashesAt = I(124) + 12;
            if (joints <= 0 || joints > 0x4000 || frames < 0 || framesAt < HeaderSize || !(duration > 0f)
                || (long)framesAt + frames * 10L > data.Length || (long)hashesAt + joints * 4L > data.Length) return null;

            var hashes = new uint[joints];
            for (int j = 0; j < joints; j++) hashes[j] = BitConverter.ToUInt32(data, hashesAt + j * 4);

            var times = new List<float>?[joints * 3];
            var values = new List<Vector4>?[joints * 3];
            for (int f = 0; f < frames; f++)
            {
                int at = framesAt + f * 10;
                ushort time = BitConverter.ToUInt16(data, at), key = BitConverter.ToUInt16(data, at + 2);
                int joint = key & 0x3FFF, type = key >> 14;
                if (joint >= joints || type > 2) continue;
                ushort a = BitConverter.ToUInt16(data, at + 4), b = BitConverter.ToUInt16(data, at + 6), c = BitConverter.ToUInt16(data, at + 8);
                Vector4 v = type switch
                {
                    0 => DecompressQuaternion(a, b, c),
                    1 => new Vector4(Dequantize(a, b, c, tMin, tMax), 0f),
                    _ => new Vector4(Dequantize(a, b, c, sMin, sMax), 0f),
                };
                int slot = joint * 3 + type;
                (times[slot] ??= new List<float>()).Add(time / 65535f * duration);
                (values[slot] ??= new List<Vector4>()).Add(v);
            }

            var r = new Track[joints]; var t = new Track[joints]; var s = new Track[joints];
            int absent = 0;
            for (int j = 0; j < joints; j++)
                for (int type = 0; type < 3; type++)
                {
                    int slot = j * 3 + type;
                    Track tr = default;
                    if (times[slot] is { Count: > 0 } ts)
                    {
                        // frames are stored in streaming order, not per track; order each track by time (stable for equal times)
                        var vs = values[slot]!;
                        var order = Enumerable.Range(0, ts.Count).OrderBy(i => ts[i]).ThenBy(i => i).ToArray();
                        tr = new Track(order.Select(i => ts[i]).ToArray(), order.Select(i => vs[i]).ToArray());
                    }
                    else absent++;
                    (type == 0 ? r : type == 1 ? t : s)[j] = tr;
                }
            return new CompressedAnimationTracks(hashes, r, t, s, duration, fps, flags, absent);
        }
        catch (ArgumentException) { return null; }
        catch (IndexOutOfRangeException) { return null; }
    }

    /// <summary>Local TRS per joint hash at <paramref name="time"/> (clamped to the clip). A joint with no track at all is left out of
    /// the pose (the skeleton walk uses its bind local). A missing track of a partly animated joint takes the component from
    /// <paramref name="bindLocal"/>, else identity rotation, zero translation, unit scale.</summary>
    public void Evaluate(float time, IDictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> pose,
        Func<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)?>? bindLocal = null)
    {
        time = Math.Clamp(time, 0f, Duration);
        for (int j = 0; j < _jointHashes.Length; j++)
        {
            bool hr = !_rotation[j].IsEmpty, ht = !_translation[j].IsEmpty, hs = !_scale[j].IsEmpty;
            if (!hr && !ht && !hs) continue;
            uint hash = _jointHashes[j];
            var bind = hr && ht && hs ? null : bindLocal?.Invoke(hash);
            var rot = hr ? SampleRotation(_rotation[j], time) : bind?.Rotation ?? Quaternion.Identity;
            var tr = ht ? Sample(_translation[j], time) : bind?.Translation ?? Vector3.Zero;
            var sc = hs ? Sample(_scale[j], time) : bind?.Scale ?? Vector3.One;
            pose[hash] = (rot, tr, sc);
        }
    }

    private static void Keys(float[] t, float time, out int i0, out int i1, out int i2, out int i3, out float amount)
    {
        int n = t.Length;
        int lo = 0, hi = n - 1, i = 0;                     // i = last key at or before time
        while (lo <= hi) { int mid = (lo + hi) >> 1; if (t[mid] <= time) { i = mid; lo = mid + 1; } else hi = mid - 1; }
        i1 = i; i2 = Math.Min(i + 1, n - 1); i0 = Math.Max(i - 1, 0); i3 = Math.Min(i + 2, n - 1);
        float span = t[i2] - t[i1];
        amount = span > 1e-6f ? Math.Clamp((time - t[i1]) / span, 0f, 1f) : 0f;
    }

    private (float, float, float, float) Weights(float[] t, int i0, int i1, int i2, int i3, float a)
    {
        float easeIn = 0.5f, easeOut = 0.5f;
        if (_parametrized)
        {
            float span = t[i2] - t[i1], inSpan = t[i2] - t[i0], outSpan = t[i3] - t[i1];
            easeIn = inSpan > 1e-6f ? span / inSpan : 0.5f;
            easeOut = outSpan > 1e-6f ? span / outSpan : 0.5f;
        }
        float m0 = (((2f - a) * a) - 1f) * a * easeIn;
        float m1 = ((((2f - easeOut) * a) + (easeOut - 3f)) * a * a) + 1f;
        float m2 = ((((3f - (2f * easeIn)) + ((easeIn - 2f) * a)) * a) + easeIn) * a;
        float m3 = ((a - 1f) * a) * a * easeOut;
        return (m0, m1, m2, m3);
    }

    private Vector3 Sample(in Track track, float time)
    {
        var v = track.Values;
        if (v.Length == 1) return AsV3(v[0]);
        Keys(track.Times, time, out int i0, out int i1, out int i2, out int i3, out float a);
        var (w0, w1, w2, w3) = Weights(track.Times, i0, i1, i2, i3, a);
        return AsV3(v[i0]) * w0 + AsV3(v[i1]) * w1 + AsV3(v[i2]) * w2 + AsV3(v[i3]) * w3;
    }

    private Quaternion SampleRotation(in Track track, float time)
    {
        var v = track.Values;
        if (v.Length == 1) return AsQ(v[0]);
        Keys(track.Times, time, out int i0, out int i1, out int i2, out int i3, out float a);
        var (w0, w1, w2, w3) = Weights(track.Times, i0, i1, i2, i3, a);
        // every key on the hemisphere of the key at or before the time, then blend and renormalise
        Vector4 q1 = v[i1];
        Vector4 Al(Vector4 q) => Vector4.Dot(q, q1) < 0f ? -q : q;
        var q = Al(v[i0]) * w0 + q1 * w1 + Al(v[i2]) * w2 + Al(v[i3]) * w3;
        float len = q.Length();
        return len > 1e-8f ? AsQ(q / len) : AsQ(q1);
    }

    private static Vector3 AsV3(Vector4 v) => new(v.X, v.Y, v.Z);
    private static Quaternion AsQ(Vector4 v) => new(v.X, v.Y, v.Z, v.W);

    private static Vector3 Dequantize(ushort a, ushort b, ushort c, Vector3 min, Vector3 max) =>
        min + (max - min) * new Vector3(a / 65535f, b / 65535f, c / 65535f);

    private static Vector4 DecompressQuaternion(ushort v0, ushort v1, ushort v2)
    {
        ulong bits = v0 | (ulong)v1 << 16 | (ulong)v2 << 32;
        int maxIndex = (int)(bits >> 45 & 3);
        const double sqrt2 = 1.41421356237309504880;
        float A(int shift) => (float)((bits >> shift & 0x7FFF) / 32767.0 * sqrt2 - 1.0 / sqrt2);
        float a = A(30), b = A(15), c = A(0);
        float d = MathF.Sqrt(MathF.Max(0f, 1f - a * a - b * b - c * c));
        return maxIndex switch
        {
            0 => new Vector4(d, a, b, c),
            1 => new Vector4(a, d, b, c),
            2 => new Vector4(a, b, d, c),
            _ => new Vector4(a, b, c, d),
        };
    }
}
