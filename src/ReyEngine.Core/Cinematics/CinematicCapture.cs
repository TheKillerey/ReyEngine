using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ReyEngine.Core.Cinematics;

/// <summary>
/// What a capture writes and at what size. Deliberately NOT the shot: the same move can be exported at
/// preview size to check the framing and at 4K for the finished trailer, and neither should touch the
/// keyframes.
/// </summary>
/// <param name="SuperSample">Render at this multiple and downscale. 2 gives clean edges on thin geometry
/// without asking the renderer for anti-aliasing it may not have, at 4x the pixels.</param>
/// <param name="StartFrame">First frame to write, 0-based, or null for the beginning. Re-rendering a
/// range is what you want after fixing one keyframe - the rest of the sequence is still good.</param>
public sealed record CinematicCaptureSettings(
    int Width,
    int Height,
    int Fps,
    string OutputDirectory,
    string NamePrefix,
    int SuperSample = 1,
    int? StartFrame = null,
    int? EndFrame = null)
{
    public static CinematicCaptureSettings Preview(string directory, string prefix) =>
        new(1920, 1080, 30, directory, prefix);

    /// <summary>Null when usable, otherwise why not. Checked before a capture starts rather than on the
    /// first frame - discovering the output path is unwritable after 900 frames is the whole problem.</summary>
    public string? Validate()
    {
        if (Width < 16 || Height < 16) return "The capture size must be at least 16x16.";
        if (Width > 7680 || Height > 4320) return "The capture size must be no larger than 7680x4320.";
        if (Fps is < 1 or > 240) return "Frames per second must be between 1 and 240.";
        if (SuperSample is < 1 or > 4) return "Supersampling must be between 1 and 4.";
        if ((long)Width * SuperSample > 16384 || (long)Height * SuperSample > 16384)
            return $"{Width}x{Height} at {SuperSample}x supersampling exceeds the 16384 texture limit.";
        if (string.IsNullOrWhiteSpace(OutputDirectory)) return "Choose an output folder.";
        if (string.IsNullOrWhiteSpace(NamePrefix)) return "Give the sequence a name.";
        if (NamePrefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "The sequence name contains characters a file name cannot hold.";
        if (StartFrame is { } s && s < 0) return "The first frame cannot be negative.";
        if (StartFrame is { } a && EndFrame is { } b && b < a) return "The last frame is before the first.";
        return null;
    }
}

/// <summary>One frame of the plan: which frame, at what time in the shot, under what name.</summary>
public readonly record struct CinematicFrame(int Index, float TimeSeconds, string FileName);

/// <summary>How a capture ended. <paramref name="Cancelled"/> is not a failure - a part-written sequence
/// is still useful, and the frames that exist are correct.</summary>
public sealed record CinematicCaptureResult(
    int FramesWritten, int FramesPlanned, string OutputDirectory, bool Cancelled, string? Error = null)
{
    public bool Success => Error is null && !Cancelled;
}

/// <summary>Render one frame and hand back its pixels as BGRA, top-down, <c>width * height * 4</c> bytes.
/// Returning null fails the capture at that frame rather than writing a broken image.</summary>
public delegate ReadOnlyMemory<byte>? CinematicFrameRenderer(CinematicPose pose, int width, int height, float timeSeconds);

/// <summary>
/// M604: turn a shot into a numbered PNG sequence, one frame at a FIXED time step.
///
/// <para><b>Why not record the screen.</b> A screen capture samples whenever the compositor happens to
/// present, so the result carries the editor's frame rate and every hitch in it. Here the frame index
/// decides the time - <c>t = index / fps</c> - so a viewport running at 9 fps and one running at 200
/// produce byte-identical output, and a 60 fps export is genuinely 60 distinct instants rather than the
/// same frame duplicated. It also means the capture can take as long as it needs per frame.</para>
///
/// <para><b>The simulation has to follow the same clock.</b> Particles, animated props and map effects
/// advanced from a wall clock would land somewhere different on every run - the same shot exported twice
/// would not match, and slow frames would make particles jump. The renderer delegate is handed the
/// frame's time and is expected to drive everything from it.</para>
///
/// <para>Rendering is left to the caller so this stays testable and device-free: everything here - the
/// frame plan, the timing, the naming, the downscale, the file writing - runs in a unit test against a
/// fake renderer, and only the D3D call sits outside.</para>
/// </summary>
public static class CinematicCapture
{
    /// <summary>Six digits and 1-BASED, matching the sequence names an editor expects to import
    /// (<c>Harrowing_NexusReveal_000001.png</c>). Zero-padded so lexical order is chronological order,
    /// which is what makes a folder of them import as a sequence rather than as a shuffled pile.</summary>
    public static string FileNameFor(string prefix, int frameIndex) => $"{prefix}_{frameIndex + 1:000000}.png";

    /// <summary>
    /// Every frame the capture will write. Computed up front so a run can be counted, ranged and resumed
    /// without re-deriving the timing.
    /// </summary>
    public static IReadOnlyList<CinematicFrame> Plan(CinematicShot shot, CinematicCaptureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(shot);
        ArgumentNullException.ThrowIfNull(settings);

        // A still shot is one frame, not none - a single keyframe is a legitimate locked-off shot.
        int total = Math.Max(1, (int)MathF.Round(shot.Duration * settings.Fps));
        int first = Math.Clamp(settings.StartFrame ?? 0, 0, total - 1);
        int last = Math.Clamp(settings.EndFrame ?? total - 1, first, total - 1);

        var frames = new List<CinematicFrame>(last - first + 1);
        for (int i = first; i <= last; i++)
            frames.Add(new CinematicFrame(i, i / (float)settings.Fps, FileNameFor(settings.NamePrefix, i)));
        return frames;
    }

    /// <summary>
    /// Run the capture. Reports <c>(framesDone, framesTotal)</c> as it goes.
    /// </summary>
    public static async Task<CinematicCaptureResult> RunAsync(
        CinematicShot shot,
        CinematicCaptureSettings settings,
        CinematicFrameRenderer render,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(shot);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(render);

        if (settings.Validate() is { } invalid)
            return new CinematicCaptureResult(0, 0, settings.OutputDirectory, false, invalid);

        var frames = Plan(shot, settings);
        int renderWidth = settings.Width * settings.SuperSample;
        int renderHeight = settings.Height * settings.SuperSample;

        try { Directory.CreateDirectory(settings.OutputDirectory); }
        catch (Exception ex)
        { return new CinematicCaptureResult(0, frames.Count, settings.OutputDirectory, false, $"Output folder: {ex.Message}"); }

        int written = 0;
        foreach (var frame in frames)
        {
            if (ct.IsCancellationRequested)
                return new CinematicCaptureResult(written, frames.Count, settings.OutputDirectory, true);

            var pose = shot.Sample(frame.TimeSeconds);
            ReadOnlyMemory<byte>? pixels;
            try { pixels = render(pose, renderWidth, renderHeight, frame.TimeSeconds); }
            catch (Exception ex)
            {
                return new CinematicCaptureResult(written, frames.Count, settings.OutputDirectory, false,
                    $"Frame {frame.Index}: {ex.Message}");
            }

            if (pixels is not { } data)
                return new CinematicCaptureResult(written, frames.Count, settings.OutputDirectory, false,
                    $"Frame {frame.Index}: the renderer produced no pixels.");

            int expected = renderWidth * renderHeight * 4;
            if (data.Length < expected)
                return new CinematicCaptureResult(written, frames.Count, settings.OutputDirectory, false,
                    $"Frame {frame.Index}: expected {expected:n0} bytes of BGRA, got {data.Length:n0}.");

            string target = Path.Combine(settings.OutputDirectory, frame.FileName);
            try
            {
                await WritePngAsync(data[..expected], renderWidth, renderHeight,
                    settings.Width, settings.Height, target, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A cancelled save leaves a TRUNCATED png behind, and a truncated frame in the middle of
                // a sequence is worse than a missing one: the folder still imports, and the damage shows
                // up as one corrupt frame in the finished video. Remove it so the count on disk matches
                // the count reported.
                TryDelete(target);
                return new CinematicCaptureResult(written, frames.Count, settings.OutputDirectory, true);
            }
            catch (Exception ex)
            {
                TryDelete(target);
                return new CinematicCaptureResult(written, frames.Count, settings.OutputDirectory, false,
                    $"Writing {frame.FileName}: {ex.Message}");
            }

            written++;
            progress?.Report((written, frames.Count));
        }

        return new CinematicCaptureResult(written, frames.Count, settings.OutputDirectory, false);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort; the error is already reported */ }
    }

    /// <summary>BGRA top-down to a PNG, downscaling when the frame was supersampled.</summary>
    private static async Task WritePngAsync(ReadOnlyMemory<byte> bgra, int width, int height,
        int outWidth, int outHeight, string path, CancellationToken ct)
    {
        // The span is consumed by LoadPixelData before the first await, so it never lives across one -
        // a ref struct cannot.
        // Bgra32 rather than Rgba32: the readback hands back the D3D swap format, and loading it as RGBA
        // silently swaps red and blue - a whole sequence that looks colour-graded until someone notices
        // the team colours are wrong.
        using var image = Image.LoadPixelData<Bgra32>(bgra.Span, width, height);
        if (width != outWidth || height != outHeight)
            image.Mutate(x => x.Resize(outWidth, outHeight, KnownResamplers.Lanczos3));
        await image.SaveAsPngAsync(path, ct).ConfigureAwait(false);
    }
}
