using System.Numerics;
using ReyEngine.Core.Cinematics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M604: the deterministic PNG-sequence export.
///
/// <para>The point of rendering frames rather than recording the screen is that the OUTPUT does not
/// depend on how fast the editor runs. So the properties worth asserting are the ones that would betray a
/// wall clock leaking in: frame times are exactly <c>index / fps</c>, two runs plan identically, and the
/// renderer is asked for the same instants every time.</para>
///
/// <para>Everything except the D3D call is exercised here — the plan, the timing, the naming, the
/// supersample downscale and the file writing — because a capture defect otherwise surfaces only after a
/// few hundred frames have been rendered.</para>
/// </summary>
public sealed class CinematicCaptureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "reyengine-cine-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* a temp dir we cannot remove is not a test failure */ }
    }

    private static CinematicShot Shot(float seconds)
    {
        var shot = new CinematicShot();
        shot.Add(new CinematicKeyframe(0f, Vector3.Zero, Quaternion.Identity, 1f));
        shot.Add(new CinematicKeyframe(seconds, new Vector3(100, 0, 0), Quaternion.Identity, 1f));
        return shot;
    }

    private CinematicCaptureSettings Settings(int w = 32, int h = 16, int fps = 30, int ss = 1) =>
        new(w, h, fps, _dir, "Shot", ss);

    /// <summary>A renderer that fills every pixel with one colour and records what it was asked for.</summary>
    private static CinematicFrameRenderer Solid(List<(int W, int H, float T)> calls, byte b = 10, byte g = 20, byte r = 30)
        => (pose, w, h, t) =>
        {
            calls.Add((w, h, t));
            var buffer = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                buffer[i * 4 + 0] = b; buffer[i * 4 + 1] = g; buffer[i * 4 + 2] = r; buffer[i * 4 + 3] = 255;
            }
            return buffer;
        };

    // ===================================================== the plan

    [Fact]
    public void FrameTimesComeFromTheFrameIndexAndNotFromAClock()
    {
        // The whole reason this is not a screen recording.
        var frames = CinematicCapture.Plan(Shot(2f), Settings(fps: 30));

        Assert.Equal(60, frames.Count);
        for (int i = 0; i < frames.Count; i++)
        {
            Assert.Equal(i, frames[i].Index);
            Assert.Equal(i / 30f, frames[i].TimeSeconds, 6);
        }
    }

    [Theory]
    [InlineData(24, 4f, 96)]
    [InlineData(30, 4f, 120)]
    [InlineData(60, 4f, 240)]
    [InlineData(60, 0.5f, 30)]
    public void TheFrameCountIsTheShotLengthTimesTheRate(int fps, float seconds, int expected)
    {
        Assert.Equal(expected, CinematicCapture.Plan(Shot(seconds), Settings(fps: fps)).Count);
    }

    [Fact]
    public void ALockedOffShotStillProducesOneFrame()
    {
        // A single keyframe has zero duration but is a legitimate shot - a still. Zero frames would be a
        // capture that silently does nothing.
        var still = new CinematicShot();
        still.Add(new CinematicKeyframe(0f, Vector3.Zero, Quaternion.Identity, 1f));

        Assert.Single(CinematicCapture.Plan(still, Settings()));
    }

    [Fact]
    public void PlanningIsDeterministicAcrossRuns()
    {
        var a = CinematicCapture.Plan(Shot(3f), Settings(fps: 60));
        var b = CinematicCapture.Plan(Shot(3f), Settings(fps: 60));

        Assert.Equal(a.Count, b.Count);
        Assert.Equal(a.Select(f => (f.Index, f.TimeSeconds, f.FileName)),
                     b.Select(f => (f.Index, f.TimeSeconds, f.FileName)));
    }

    // ===================================================== naming

    [Fact]
    public void NamesAreOneBasedSixDigitAndSortChronologically()
    {
        // The example in the spec starts at 000001, and an editor imports a folder by lexical order - so
        // lexical order has to BE chronological order or the sequence assembles shuffled.
        Assert.Equal("Harrowing_NexusReveal_000001.png", CinematicCapture.FileNameFor("Harrowing_NexusReveal", 0));
        Assert.Equal("Harrowing_NexusReveal_000010.png", CinematicCapture.FileNameFor("Harrowing_NexusReveal", 9));

        var names = CinematicCapture.Plan(Shot(1f), Settings(fps: 30)).Select(f => f.FileName).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), names);
    }

    [Fact]
    public void APartialRangeKeepsTheOriginalFrameNumbers()
    {
        // Re-rendering frames 30..39 after fixing a keyframe has to overwrite exactly those files, not
        // write a fresh sequence starting at 1.
        var frames = CinematicCapture.Plan(Shot(4f), Settings(fps: 30) with { StartFrame = 30, EndFrame = 39 });

        Assert.Equal(10, frames.Count);
        Assert.Equal(30, frames[0].Index);
        Assert.Equal("Shot_000031.png", frames[0].FileName);
        Assert.Equal(30 / 30f, frames[0].TimeSeconds, 6);
    }

    [Fact]
    public void ARangePastTheEndOfTheShotIsClampedRatherThanInvented()
    {
        var frames = CinematicCapture.Plan(Shot(1f), Settings(fps: 30) with { StartFrame = 20, EndFrame = 9999 });

        Assert.Equal(29, frames[^1].Index);   // 30 frames, 0..29
        Assert.Equal(10, frames.Count);
    }

    // ===================================================== writing

    [Fact]
    public async Task EveryPlannedFrameIsWrittenAsAPng()
    {
        var calls = new List<(int, int, float)>();
        var result = await CinematicCapture.RunAsync(Shot(1f), Settings(fps: 10), Solid(calls));

        Assert.True(result.Success, result.Error);
        Assert.Equal(10, result.FramesWritten);
        Assert.Equal(10, Directory.GetFiles(_dir, "*.png").Length);
        Assert.True(File.Exists(Path.Combine(_dir, "Shot_000001.png")));
        Assert.True(File.Exists(Path.Combine(_dir, "Shot_000010.png")));
    }

    [Fact]
    public async Task ThePixelsKeepTheirColourInsteadOfSwappingRedAndBlue()
    {
        // The readback is BGRA. Reading it as RGBA silently swaps the channels, and the result looks like
        // a deliberate colour grade rather than a bug - a whole sequence can be exported before anyone
        // notices the team colours are the wrong way round.
        var calls = new List<(int, int, float)>();
        var result = await CinematicCapture.RunAsync(Shot(0.1f), Settings(fps: 10),
            Solid(calls, b: 0, g: 0, r: 255));   // pure RED in BGRA

        Assert.True(result.Success, result.Error);
        using var png = await Image.LoadAsync<Rgba32>(Path.Combine(_dir, "Shot_000001.png"));
        var pixel = png[png.Width / 2, png.Height / 2];

        Assert.Equal(255, pixel.R);
        Assert.Equal(0, pixel.G);
        Assert.Equal(0, pixel.B);
    }

    [Fact]
    public async Task SupersamplingRendersLargerAndStillWritesTheRequestedSize()
    {
        var calls = new List<(int W, int H, float T)>();
        var result = await CinematicCapture.RunAsync(Shot(0.1f), Settings(w: 64, h: 32, fps: 10, ss: 2), Solid(calls));

        Assert.True(result.Success, result.Error);
        Assert.Equal((128, 64), (calls[0].W, calls[0].H));     // asked the renderer for 2x

        using var png = await Image.LoadAsync<Rgba32>(Path.Combine(_dir, "Shot_000001.png"));
        Assert.Equal(64, png.Width);                            // wrote the requested size
        Assert.Equal(32, png.Height);
    }

    [Fact]
    public async Task TheRendererIsAskedForTheSameInstantsThePlanNames()
    {
        var calls = new List<(int W, int H, float T)>();
        await CinematicCapture.RunAsync(Shot(1f), Settings(fps: 25), Solid(calls));

        var planned = CinematicCapture.Plan(Shot(1f), Settings(fps: 25)).Select(f => f.TimeSeconds).ToList();
        Assert.Equal(planned, calls.Select(c => c.T).ToList());
    }

    [Fact]
    public async Task CancellingStopsAndKeepsTheFramesAlreadyWritten()
    {
        // A part-written sequence is useful and its frames are correct, so cancelling is not a failure.
        using var cts = new CancellationTokenSource();
        int rendered = 0;
        CinematicFrameRenderer render = (pose, w, h, t) =>
        {
            if (++rendered == 4) cts.Cancel();
            var buffer = new byte[w * h * 4];
            Array.Fill(buffer, (byte)255);
            return buffer;
        };

        var result = await CinematicCapture.RunAsync(Shot(2f), Settings(fps: 30), render, null, cts.Token);

        Assert.True(result.Cancelled);
        Assert.False(result.Success);
        Assert.InRange(result.FramesWritten, 1, 5);
        Assert.Equal(result.FramesWritten, Directory.GetFiles(_dir, "*.png").Length);
    }

    [Fact]
    public async Task ARendererThatReturnsNothingFailsTheFrameRatherThanWritingABrokenImage()
    {
        var result = await CinematicCapture.RunAsync(Shot(1f), Settings(), (p, w, h, t) => null);

        Assert.False(result.Success);
        Assert.Contains("no pixels", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(_dir, "*.png"));
    }

    [Fact]
    public async Task AShortBufferIsRefusedInsteadOfReadingPastItsEnd()
    {
        var result = await CinematicCapture.RunAsync(Shot(1f), Settings(),
            (p, w, h, t) => new byte[w * h * 4 - 16]);

        Assert.False(result.Success);
        Assert.Contains("BGRA", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProgressCountsUpToTheFrameTotal()
    {
        var seen = new List<(int Done, int Total)>();
        var calls = new List<(int, int, float)>();
        await CinematicCapture.RunAsync(Shot(1f), Settings(fps: 10), Solid(calls),
            new Progress<(int, int)>(p => { lock (seen) seen.Add(p); }));

        // Progress is posted, so give it a moment to drain before asserting on it.
        await Task.Delay(200);
        lock (seen)
        {
            Assert.NotEmpty(seen);
            Assert.All(seen, p => Assert.Equal(10, p.Total));
            Assert.Equal(10, seen.Max(p => p.Done));
        }
    }

    // ===================================================== settings

    [Fact]
    public void UsableSettingsValidate() => Assert.Null(Settings().Validate());

    [Theory]
    [InlineData(1920, 1080, 30, 1)]
    [InlineData(2560, 1440, 24, 1)]
    [InlineData(3840, 2160, 60, 1)]
    public void EverySizeAndRateTheSpecAsksForIsAccepted(int w, int h, int fps, int ss)
    {
        Assert.Null(new CinematicCaptureSettings(w, h, fps, _dir, "Trailer", ss).Validate());
    }

    [Fact]
    public void SettingsThatWouldFailMidCaptureAreRefusedUpFront()
    {
        // Finding out after 900 frames is the whole problem this avoids.
        Assert.NotNull((Settings() with { Width = 4 }).Validate());
        Assert.NotNull((Settings() with { Fps = 0 }).Validate());
        Assert.NotNull((Settings() with { SuperSample = 9 }).Validate());
        Assert.NotNull((Settings() with { OutputDirectory = "" }).Validate());
        Assert.NotNull((Settings() with { NamePrefix = "" }).Validate());
        Assert.NotNull((Settings() with { NamePrefix = "bad/name" }).Validate());
        Assert.NotNull((Settings() with { StartFrame = 10, EndFrame = 2 }).Validate());
        // 8K at 4x supersampling is 30720 wide - past the 16384 a texture can be. 4K at 4x is 15360
        // and fits, so it must still be accepted.
        Assert.NotNull(new CinematicCaptureSettings(7680, 4320, 30, _dir, "x", 4).Validate());
        Assert.Null(new CinematicCaptureSettings(3840, 2160, 30, _dir, "x", 4).Validate());
    }

    [Fact]
    public async Task InvalidSettingsFailBeforeAnythingIsRendered()
    {
        int rendered = 0;
        var result = await CinematicCapture.RunAsync(Shot(1f), Settings() with { Fps = 0 },
            (p, w, h, t) => { rendered++; return new byte[w * h * 4]; });

        Assert.False(result.Success);
        Assert.Equal(0, rendered);
    }
}
