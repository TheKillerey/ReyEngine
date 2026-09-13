using System;
using Avalonia;

namespace ReyEngine.App.Views;

/// <summary>
/// M718: the one mapping between a curve's (time, value) space and a control's pixels. Drawing and
/// hit-testing both go through it, so a key is picked exactly where it is painted - two copies of one
/// formula are how a click ends up a few pixels off the dot it was aimed at.
/// </summary>
public readonly record struct CurveTransform(float T0, float T1, float Min, float Max, double Width, double Height)
{
    /// <summary>The narrowest and widest span a zoom may reach. Below the first, float stops telling
    /// neighbouring pixels apart; past the second the curve is a flat line anyway.</summary>
    private const float MinSpan = 1e-4f, MaxSpan = 1e6f;

    /// <summary>The auto-fit the curve display has always used: every key in view, the value range padded
    /// by 8%, a flat curve opened to +-0.5, and a single key given one unit of time. Null when there is
    /// nothing to fit. <paramref name="include"/> leaves hidden channels out of the value range.</summary>
    public static CurveTransform? Fit(float[] times, float[][] channels, Size size, Func<int, bool>? include = null)
    {
        float min = float.MaxValue, max = float.MinValue;
        for (int c = 0; c < channels.Length; c++)
        {
            if (include is not null && !include(c)) continue;
            foreach (var v in channels[c]) { if (v < min) min = v; if (v > max) max = v; }
        }
        if (min > max) return null;
        if (max - min < 1e-6f) { min -= 0.5f; max += 0.5f; }
        float pad = (max - min) * 0.08f; min -= pad; max += pad;

        float t0 = times[0], t1 = times[^1];
        if (t1 - t0 < 1e-6f) t1 = t0 + 1f;
        return new CurveTransform(t0, t1, min, max, size.Width, size.Height);
    }

    /// <summary>Curve space to pixels.</summary>
    public Point ToScreen(float time, float value) => new(
        (time - T0) / (T1 - T0) * Width,
        Height - (value - Min) / (Max - Min) * Height);

    /// <summary>Pixels to curve space - the exact inverse of <see cref="ToScreen"/>.</summary>
    public (float Time, float Value) ToCurve(Point p) => (
        (float)(T0 + p.X / Width * (T1 - T0)),
        (float)(Min + (Height - p.Y) / Height * (Max - Min)));

    /// <summary>Scale the view about a pixel, which stays over the same curve point. A factor below 1
    /// zooms in; 1 leaves that axis alone.</summary>
    public CurveTransform Zoom(Point anchor, double timeFactor, double valueFactor)
    {
        var (at, av) = ToCurve(anchor);
        float t0 = T0, t1 = T1, v0 = Min, v1 = Max;
        // an axis left alone is skipped rather than scaled by 1, which would round it a little each turn
        if (timeFactor != 1)
        {
            t0 = (float)(at + (T0 - at) * timeFactor); t1 = (float)(at + (T1 - at) * timeFactor);
            if (!(t1 - t0 >= MinSpan && t1 - t0 <= MaxSpan)) { t0 = T0; t1 = T1; }
        }
        if (valueFactor != 1)
        {
            v0 = (float)(av + (Min - av) * valueFactor); v1 = (float)(av + (Max - av) * valueFactor);
            if (!(v1 - v0 >= MinSpan && v1 - v0 <= MaxSpan)) { v0 = Min; v1 = Max; }
        }
        return this with { T0 = t0, T1 = t1, Min = v0, Max = v1 };
    }

    /// <summary>Slide the view so the curve follows a pointer that moved by <paramref name="pixels"/>.</summary>
    public CurveTransform Pan(Vector pixels)
    {
        float dt = (float)(-pixels.X / Width * (T1 - T0));
        float dv = (float)(pixels.Y / Height * (Max - Min));
        return this with { T0 = T0 + dt, T1 = T1 + dt, Min = Min + dv, Max = Max + dv };
    }
}
