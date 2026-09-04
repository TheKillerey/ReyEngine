using System.Numerics;
using ReyEngine.Rendering;

namespace ReyEngine.Formats.Tests;

/// <summary>M639: the cast-range ring's geometry, and that both renderers receive it.</summary>
public sealed class RangeRingTests
{
    private static string? Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    [Fact]
    public void ACircleIsAClosedLoopOfSegmentsAtTheRadius()
    {
        var centre = new Vector3(100f, 20f, -50f);
        var lines = ViewportMeshRenderer.BuildCircleLines(centre, 300f, 64, heightAt: null, lift: 0f);

        Assert.Equal(64 * 2 * 3, lines.Length);                      // two xyz per segment
        for (int i = 0; i < lines.Length; i += 3)
        {
            float dx = lines[i] - centre.X, dz = lines[i + 2] - centre.Z;
            Assert.Equal(300f, MathF.Sqrt(dx * dx + dz * dz), 2);    // every vertex on the circle
            Assert.Equal(20f, lines[i + 1], 3);                       // flat when nothing reports height
        }
        // Closed: the last segment ends where the first begins.
        Assert.Equal(lines[0], lines[^3], 3);
        Assert.Equal(lines[2], lines[^1], 3);
        // Consecutive: each segment starts where the previous ended.
        for (int s = 1; s < 64; s++)
        {
            Assert.Equal(lines[(s - 1) * 6 + 3], lines[s * 6], 3);
            Assert.Equal(lines[(s - 1) * 6 + 5], lines[s * 6 + 2], 3);
        }
    }

    [Fact]
    public void TheRingFollowsTheGroundItIsAsked()
    {
        // A sloped "terrain": height rises with X. The ring must sample it per vertex, lifted a little
        // above it so it does not z-fight with the floor.
        var lines = ViewportMeshRenderer.BuildCircleLines(Vector3.Zero, 100f, 32, p => p.X * 0.5f, lift: 4f);
        for (int i = 0; i < lines.Length; i += 3)
            Assert.Equal(lines[i] * 0.5f + 4f, lines[i + 1], 3);
    }

    [Fact]
    public void FewerThanEightSegmentsIsRoundedUpToAShapeThatReadsAsACircle()
    {
        Assert.Equal(8 * 6, ViewportMeshRenderer.BuildCircleLines(Vector3.Zero, 10f, 3).Length);
    }

    [Fact]
    public void BothViewportsReceiveTheSameRing()
    {
        // M628's lesson in reverse: a line list one renderer draws and the other never receives.
        var axaml = Source("src", "ReyEngine.App", "Views", "MeshPreviewWindow.axaml");
        var dx11 = Source("src", "ReyEngine.App", "Views", "MeshPreviewWindow.Dx11.cs");
        var gl = Source("src", "ReyEngine.App", "Views", "ViewportControl.cs");
        var surface = Source("src", "ReyEngine.App", "Views", "Dx11ViewportSurface.cs");
        if (axaml is null || dx11 is null || gl is null || surface is null) return;

        Assert.Contains("RangeRingLines=\"{Binding RangeRingLines}\"", axaml);
        Assert.Contains("_meshRenderer.SetRangeRingLines(RangeRingLines);", gl);
        Assert.Contains("_dx11.RangeLines = vm.RangeRingLines;", dx11);
        Assert.Contains("_renderer.SetRangeLines(RangeLines);", surface);
    }
}
