namespace ReyEngine.Formats.Vfx;

/// <summary>
/// M719: which cell of its atlas a particle draws, as the engine picks it - reading 2.11 of ltk-manager's
/// particle renderer plan, checked against their implementation and their own tests.
///
/// <para><b>The run wraps before the start is added.</b> <c>frame = startFrame + fmod(phase + age * rate,
/// numFrames)</c>, so a book with a <c>startFrame</c> cycles from that cell back to that cell. We ran
/// <c>fmod(start + t, numFrames)</c>, which sent every such book back to the grid's first cell instead:
/// 6,433 reachable books author a start and a rate.</para>
///
/// <para><b>A random start is a phase, not a replacement.</b> <c>isRandomStartFrame</c> draws
/// <c>rand * numFrames</c> as a float inside the run, and the authored <c>startFrame</c> still lands on top.
/// We drew a whole cell and threw the start away; 10,268 reachable books author both.</para>
///
/// <para><b>Nothing clamps the start, and nothing gates it.</b> A single-frame emitter over a grid is
/// picking one still cell - FireTorch_Med/Flat takes cell 5 of a 2x3 sheet, which M535 made the converter
/// write - and the old <c>NumFrames &gt; 1</c> gate drew cell 0 for all 2,961 reachable emitters that do
/// it. A start at or past <c>numFrames</c> was clamped to the last frame on 2,184 more.</para>
///
/// <para><b>No wrap over the grid.</b> Riot's <c>quad_vs</c> computes the row as <c>floor(frame / cols)</c>
/// with nothing bounding it, so a frame past the last row reaches the sampler as a coordinate past 1 and the
/// texture's address mode decides. Both renderers are handed the unwrapped frame for that reason.</para>
///
/// <para>Three edges are choices rather than readings: a rate at or below zero holds (5 emitters author
/// one), a written <c>numFrames</c> of 0 counts as 1 (1,652 do), and a phase is kept strictly below
/// <c>numFrames</c> so a draw that rounds up in float cannot wrap to the start.</para>
///
/// <para>Not settled here: whether <c>birthFrameRate</c> REPLACES <c>frameRate</c> (what we do) or
/// multiplies it (what ltk-manager does, with a declared default of 1.0 behind it). No reading covers it,
/// and 5,390 reachable books that author it without a frameRate animate under one answer and hold under
/// the other.</para>
/// </summary>
public static class VfxFlipbook
{
    /// <summary>The phase a random start draws, from one sample in [0, 1).</summary>
    public static float RandomPhase(double sample, int numFrames)
    {
        int n = Math.Max(numFrames, 1);
        float phase = (float)(sample * n);
        return phase >= n ? MathF.BitDecrement(n) : phase;
    }

    /// <summary>The cell a particle draws: whole, and unwrapped over the grid.</summary>
    public static float Frame(float startFrame, float phase, float age, float rate, int numFrames)
    {
        int n = Math.Max(numFrames, 1);
        double run = phase + (rate > 0f ? (double)age * rate : 0.0);
        double played = run % n;
        if (played < 0.0) played += n;
        return (float)Math.Floor(startFrame + played);
    }
}
