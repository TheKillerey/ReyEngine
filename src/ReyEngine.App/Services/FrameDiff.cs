using System;
using System.Text;

namespace ReyEngine.App.Services;

/// <summary>
/// <para>M252 (phase 6, step 3): compare two rendered frames of the same scene from the same camera.</para>
///
/// <para>This is the instrument the whole side-by-side exists to enable. "OpenGL worked mostly but not how
/// it looked ingame" is not actionable; a per-channel difference histogram over the same frame is.</para>
///
/// <para>Both inputs must be BGRA, top-down, same dimensions. The OpenGL capture normalises to that at the
/// source (glReadPixels gives RGBA bottom-up) precisely so this has one format to reason about - a flip or
/// a channel swap left in would report every pixel as different and the result would mean nothing.</para>
/// </summary>
public static class FrameDiff
{
    public sealed record Report(
        int Width, int Height,
        long DifferingPixels, long TotalPixels,
        double MeanAbsError, int MaxChannelDelta,
        long BothBackground, long OnlyA, long OnlyB,
        int[] Histogram)
    {
        public double DifferingPercent => TotalPixels == 0 ? 0 : 100.0 * DifferingPixels / TotalPixels;

        /// <summary>Pixels drawn by exactly one renderer - geometry present in one and missing in the
        /// other. This is the number that names a real bug, as opposed to a shading difference.</summary>
        public long CoverageMismatch => OnlyA + OnlyB;

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{Width}x{Height}  ({TotalPixels:n0} pixels)");
            sb.AppendLine($"differing      {DifferingPixels,10:n0}  ({DifferingPercent:F2}%)");
            sb.AppendLine($"mean abs error {MeanAbsError,10:F2}  per channel, 0-255");
            sb.AppendLine($"max delta      {MaxChannelDelta,10}");
            sb.AppendLine();
            sb.AppendLine("coverage:");
            sb.AppendLine($"   both empty  {BothBackground,10:n0}");
            sb.AppendLine($"   only A (GL) {OnlyA,10:n0}");
            sb.AppendLine($"   only B (DX) {OnlyB,10:n0}");
            sb.AppendLine($"   MISMATCH    {CoverageMismatch,10:n0}   <- geometry one renderer draws and the other does not");
            sb.AppendLine();
            sb.AppendLine("per-pixel max channel delta:");
            string[] bands = { "0 (exact)", "1-3", "4-7", "8-15", "16-31", "32-63", "64-127", "128-255" };
            for (int i = 0; i < bands.Length; i++)
                sb.AppendLine($"   {bands[i],-10} {Histogram[i],10:n0}  ({100.0 * Histogram[i] / Math.Max(1, TotalPixels),5:F1}%)");
            return sb.ToString();
        }
    }

    /// <summary>Threshold below which a channel is treated as background rather than drawn geometry. Both
    /// renderers clear to something dark; this separates "nothing here" from "shaded differently".</summary>
    private const int BackgroundLevel = 8;

    public static Report Compare(byte[] a, byte[] b, int width, int height, byte[]? diffOut = null)
    {
        long total = (long)width * height;
        long differing = 0, sumAbs = 0, bothBg = 0, onlyA = 0, onlyB = 0;
        int maxDelta = 0;
        var hist = new int[8];

        for (long i = 0; i < total; i++)
        {
            long o = i * 4;
            int db = Math.Abs(a[o] - b[o]);
            int dg = Math.Abs(a[o + 1] - b[o + 1]);
            int dr = Math.Abs(a[o + 2] - b[o + 2]);
            int worst = Math.Max(db, Math.Max(dg, dr));

            sumAbs += db + dg + dr;
            if (worst > maxDelta) maxDelta = worst;
            if (worst > 0) differing++;
            hist[Band(worst)]++;

            bool aDrew = a[o] > BackgroundLevel || a[o + 1] > BackgroundLevel || a[o + 2] > BackgroundLevel;
            bool bDrew = b[o] > BackgroundLevel || b[o + 1] > BackgroundLevel || b[o + 2] > BackgroundLevel;
            if (!aDrew && !bDrew) bothBg++;
            else if (aDrew && !bDrew) onlyA++;
            else if (!aDrew && bDrew) onlyB++;

            if (diffOut is not null)
            {
                // Coverage mismatches are the interesting failure, so they get a colour rather than a grey
                // level: magenta for "only GL drew", green for "only DX11 drew", grey for shading deltas.
                bool coverage = aDrew != bDrew;
                byte g = (byte)Math.Min(255, worst * 4);
                diffOut[o + 0] = coverage ? (byte)(aDrew ? 255 : 0) : g;
                diffOut[o + 1] = coverage ? (byte)(aDrew ? 0 : 255) : g;
                diffOut[o + 2] = coverage ? (byte)(aDrew ? 255 : 0) : g;
                diffOut[o + 3] = 255;
            }
        }

        return new Report(width, height, differing, total,
            total == 0 ? 0 : (double)sumAbs / (total * 3), maxDelta,
            bothBg, onlyA, onlyB, hist);
    }

    private static int Band(int d) => d switch
    {
        0 => 0, <= 3 => 1, <= 7 => 2, <= 15 => 3, <= 31 => 4, <= 63 => 5, <= 127 => 6, _ => 7,
    };
}
