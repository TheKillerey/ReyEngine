using ReyEngine.App.Services;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M252: the A/B diff is an instrument, so what it claims has to be trustworthy. The distinction it exists
/// to draw is COVERAGE mismatch - geometry one renderer draws and the other does not - from shading
/// difference. Conflating those would make the output useless: everything would read as "different".
/// </summary>
public class FrameDiffTests
{
    private static byte[] Fill(int n, byte b, byte g, byte r)
    {
        var buf = new byte[n * 4];
        for (int i = 0; i < n; i++) { buf[i * 4] = b; buf[i * 4 + 1] = g; buf[i * 4 + 2] = r; buf[i * 4 + 3] = 255; }
        return buf;
    }

    [Fact]
    public void Identical_frames_report_no_difference()
    {
        var a = Fill(64, 40, 50, 60);
        var r = FrameDiff.Compare(a, (byte[])a.Clone(), 8, 8);
        Assert.Equal(0, r.DifferingPixels);
        Assert.Equal(0, r.MaxChannelDelta);
        Assert.Equal(0, r.CoverageMismatch);
        Assert.Equal(64, r.Histogram[0]);
    }

    [Fact]
    public void Geometry_in_only_one_renderer_is_a_coverage_mismatch_not_just_a_delta()
    {
        // A draws, B is background. This is the case that names a real bug.
        var a = Fill(64, 200, 200, 200);
        var b = Fill(64, 0, 0, 0);
        var r = FrameDiff.Compare(a, b, 8, 8);
        Assert.Equal(64, r.OnlyA);
        Assert.Equal(0, r.OnlyB);
        Assert.Equal(64, r.CoverageMismatch);
    }

    [Fact]
    public void Both_background_is_not_counted_as_coverage_mismatch()
    {
        // Two slightly different dark clears are not a geometry difference, and must not be reported as one.
        var r = FrameDiff.Compare(Fill(64, 2, 2, 2), Fill(64, 5, 5, 5), 8, 8);
        Assert.Equal(0, r.CoverageMismatch);
        Assert.Equal(64, r.BothBackground);
        Assert.Equal(64, r.DifferingPixels);   // still differing, just not a coverage fault
    }

    [Fact]
    public void Shading_difference_where_both_drew_is_not_a_coverage_mismatch()
    {
        var r = FrameDiff.Compare(Fill(64, 100, 100, 100), Fill(64, 140, 100, 100), 8, 8);
        Assert.Equal(0, r.CoverageMismatch);
        Assert.Equal(64, r.DifferingPixels);
        Assert.Equal(40, r.MaxChannelDelta);
    }

    [Fact]
    public void Mean_absolute_error_averages_over_all_three_channels()
    {
        // one channel differs by 30 across every pixel => 30/3 = 10 per channel
        var r = FrameDiff.Compare(Fill(16, 10, 10, 10), Fill(16, 40, 10, 10), 4, 4);
        Assert.Equal(10.0, r.MeanAbsError, 3);
    }

    [Fact]
    public void Histogram_bands_the_worst_channel_not_the_sum()
    {
        var r = FrameDiff.Compare(Fill(4, 0, 0, 0), Fill(4, 5, 5, 5), 2, 2);
        Assert.Equal(4, r.Histogram[2]);   // 5 falls in the 4-7 band
    }

    [Fact]
    public void Diff_image_marks_coverage_in_colour_and_shading_in_grey()
    {
        var diff = new byte[4 * 4];
        // pixel 0: only A drew -> magenta. pixel 1: both drew, differ -> grey.
        var a = new byte[] { 200, 200, 200, 255, 100, 100, 100, 255, 0, 0, 0, 255, 0, 0, 0, 255 };
        var b = new byte[] { 0, 0, 0, 255, 110, 100, 100, 255, 0, 0, 0, 255, 0, 0, 0, 255 };
        FrameDiff.Compare(a, b, 2, 2, diff);

        Assert.Equal(255, diff[0]); Assert.Equal(0, diff[1]); Assert.Equal(255, diff[2]);  // magenta
        Assert.Equal(diff[4], diff[5]); Assert.Equal(diff[5], diff[6]);                    // grey
    }
}
