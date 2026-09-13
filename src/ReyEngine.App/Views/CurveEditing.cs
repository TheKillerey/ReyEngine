using System;
using System.Linq;
using Avalonia;

namespace ReyEngine.App.Views;

/// <summary>
/// M718: the curve editor's arithmetic, kept off the control so it can be tested without a window. Every
/// question here is asked in pixels through <see cref="CurveTransform"/>, the same mapping the control
/// paints with.
/// </summary>
public static class CurveEditing
{
    /// <summary>How far from a key's centre, in pixels, a press still picks it up.</summary>
    public const double PickRadius = 7;

    /// <summary>The key under a pixel: the nearest within <paramref name="radius"/>, visible channels only.
    /// On a tie the later channel wins, because it is painted last and so is the dot the user can see.</summary>
    public static (int Index, int Channel)? HitTest(CurveTransform xf, float[] times, float[][] channels,
        Func<int, bool> visible, Point p, double radius = PickRadius)
    {
        (int, int)? best = null;
        double bestD = radius * radius;
        for (int c = 0; c < channels.Length; c++)
        {
            if (!visible(c)) continue;
            int n = Math.Min(times.Length, channels[c].Length);
            for (int i = 0; i < n; i++)
            {
                var k = xf.ToScreen(times[i], channels[c][i]);
                double dx = k.X - p.X, dy = k.Y - p.Y, d = dx * dx + dy * dy;
                if (d <= bestD) { bestD = d; best = (i, c); }
            }
        }
        return best;
    }

    /// <summary>The times a dragged key may take. Between its neighbours - equal is allowed, since
    /// <c>AddCurveKey</c> inserts before the first key that is not smaller and two keys at one time are a
    /// step - and for an end key no further out than the particle's lifetime, 0..1, or than where the key
    /// already was. Passing a neighbour would reorder the keys, and Riot reads them in order.</summary>
    public static (float Lo, float Hi) TimeBounds(float[] times, int index)
    {
        float lo = index > 0 ? times[index - 1] : Math.Min(0f, times[index]);
        float hi = index < times.Length - 1 ? times[index + 1] : Math.Max(1f, times[index]);
        return (lo, hi);
    }

    /// <summary>One channel read at <paramref name="t"/> along the straight segments the editor draws,
    /// held flat past either end.</summary>
    public static float Evaluate(float[] times, float[] values, float t)
    {
        int n = Math.Min(times.Length, values.Length);
        if (n == 0) return 0f;
        if (t <= times[0]) return values[0];
        for (int i = 1; i < n; i++)
        {
            if (t > times[i]) continue;
            float span = times[i] - times[i - 1];
            return span <= 0f ? values[i] : values[i - 1] + (values[i] - values[i - 1]) * (t - times[i - 1]) / span;
        }
        return values[n - 1];
    }

    /// <summary>The components of a key added at <paramref name="t"/>: <paramref name="channel"/> takes the
    /// value the user clicked at, and every other channel is read off its own line, so adding a red key
    /// does not move green, blue or alpha.</summary>
    public static float[] NewKey(float[] times, float[][] channels, float t, int channel, float value)
    {
        var comps = new float[channels.Length];
        for (int c = 0; c < channels.Length; c++)
            comps[c] = c == channel ? value : Evaluate(times, channels[c], t);
        return comps;
    }

    /// <summary>Where <c>ParticleProperty.AddCurveKey</c> will put a key at <paramref name="t"/> - after
    /// every key strictly earlier, the same comparison it uses.</summary>
    public static int InsertionIndex(float[] times, float t)
    {
        int at = 0;
        while (at < times.Length && times[at] < t) at++;
        return at;
    }

    /// <summary>The visible channel whose line passes closest to a pixel, measured straight up or down at
    /// that pixel's time. -1 when none is visible.</summary>
    public static int NearestChannel(CurveTransform xf, float[] times, float[][] channels, Func<int, bool> visible, Point p)
    {
        var (t, _) = xf.ToCurve(p);
        int best = -1;
        double bestD = double.MaxValue;
        for (int c = 0; c < channels.Length; c++)
        {
            if (!visible(c)) continue;
            double d = Math.Abs(xf.ToScreen(t, Evaluate(times, channels[c], t)).Y - p.Y);
            if (d < bestD) { bestD = d; best = c; }
        }
        return best;
    }
}

/// <summary>
/// M718: one drag of one key, on a COPY of the keys. The document is not touched while the pointer moves:
/// a commit re-serializes the whole bin, re-extracts every system and rebuilds the preview, which is
/// right once per edit and is seconds of work per move on a map bin. The control paints this copy and
/// commits it once, when the button comes up.
/// </summary>
public sealed class CurveKeyDrag
{
    private readonly float _lo, _hi;

    public int Index { get; }
    public int Channel { get; }
    public float[] Times { get; }
    public float[][] Channels { get; }
    public float StartTime { get; }
    public float StartValue { get; }

    public CurveKeyDrag(float[] times, float[][] channels, int index, int channel)
    {
        Times = (float[])times.Clone();
        Channels = channels.Select(c => (float[])c.Clone()).ToArray();
        Index = index;
        Channel = channel;
        StartTime = times[index];
        StartValue = channels[channel][index];
        (_lo, _hi) = CurveEditing.TimeBounds(times, index);
    }

    public float Time => Times[Index];
    public float Value => Channels[Channel][Index];

    /// <summary>False for a press that went nowhere - a click to select commits nothing.</summary>
    public bool Moved => Time != StartTime || Value != StartValue;

    /// <summary>Move the key, its time held between its neighbours. Min/Max rather than Math.Clamp, which
    /// throws on the reversed bounds an out-of-order bin would give.</summary>
    public void MoveTo(float time, float value)
    {
        if (!float.IsFinite(time) || !float.IsFinite(value)) return;
        Times[Index] = Math.Min(Math.Max(time, _lo), _hi);
        Channels[Channel][Index] = value;
    }

    /// <summary>Every component of the dragged key, as the row's SetKey takes them.</summary>
    public float[] Components()
    {
        var comps = new float[Channels.Length];
        for (int c = 0; c < Channels.Length; c++) comps[c] = Channels[c][Index];
        return comps;
    }
}
