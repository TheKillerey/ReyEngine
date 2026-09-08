using System.Numerics;
using ReyEngine.Core.Assets;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M657: the viewport's placement icons are painted art now, embedded in Core so both renderers can
/// reach them without referencing each other.
///
/// <para>Embedding is the whole point — it keeps the property the drawn glyphs had, that there is no
/// packaging step which can ship a build with the markers silently absent. That only holds if the
/// resource NAMES are what the loader asks for, which is an MSBuild convention and not a compiler
/// error when it is wrong: get it wrong and every icon quietly falls back to its drawn glyph.</para>
/// </summary>
public sealed class ViewportIconTests
{
    public static TheoryData<ViewportIcon> AllIcons() => new()
    {
        ViewportIcon.Particle, ViewportIcon.Sound, ViewportIcon.Prop, ViewportIcon.Probe, ViewportIcon.Light,
    };

    [Theory]
    [MemberData(nameof(AllIcons))]
    public void EveryIconIsEmbeddedAndDecodes(ViewportIcon icon)
    {
        var image = ViewportIcons.Load(icon);
        Assert.NotNull(image);
        Assert.Equal(256, image!.Size);
        Assert.Equal(256 * 256 * 4, image.Rgba.Length);
    }

    /// <summary>A marker is drawn over the scene, so its shape has to come from alpha. Art exported onto
    /// an opaque background would draw as a square card and look like a broken texture.</summary>
    [Theory]
    [MemberData(nameof(AllIcons))]
    public void TheShapeIsCarriedByAlphaAndTheCornersAreClear(ViewportIcon icon)
    {
        var image = ViewportIcons.Load(icon)!;
        int n = image.Size;
        byte Alpha(int x, int y) => image.Rgba[(y * n + x) * 4 + 3];

        Assert.Equal(0, Alpha(0, 0));
        Assert.Equal(0, Alpha(n - 1, 0));
        Assert.Equal(0, Alpha(0, n - 1));

        int opaque = 0;
        for (int i = 3; i < image.Rgba.Length; i += 4) if (image.Rgba[i] > 200) opaque++;
        double covered = opaque * 100.0 / (n * n);
        Assert.InRange(covered, 5.0, 60.0);   // a symbol, not a blank and not a filled card
    }

    /// <summary>These are COLOURED now, unlike the white-with-alpha glyphs they replace — which is why
    /// both shaders had to stop substituting a flat tint for the texture's own rgb.</summary>
    [Fact]
    public void TheArtCarriesItsOwnColour()
    {
        var image = ViewportIcons.Load(ViewportIcon.Particle)!;
        bool coloured = false;
        for (int i = 0; i < image.Rgba.Length && !coloured; i += 4)
        {
            if (image.Rgba[i + 3] < 200) continue;
            int r = image.Rgba[i], g = image.Rgba[i + 1], b = image.Rgba[i + 2];
            coloured = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 40;
        }
        Assert.True(coloured, "the particle icon reads as greyscale; a flat tint would have hidden that");
    }

    [Fact]
    public void LoadingIsCachedAndTheSameInstanceComesBack()
    {
        Assert.Same(ViewportIcons.Load(ViewportIcon.Light), ViewportIcons.Load(ViewportIcon.Light));
    }

    // ---- the size cap -----------------------------------------------------------------------------

    private const int ViewportH = 1080;
    private const float Fov = 0.9f;

    private static Matrix4x4 ViewProjectionAt(float distance)
    {
        var eye = new Vector3(0f, 0f, distance);
        var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(Fov, 16f / 9f, 1f, 100000f);
        return Matrix4x4.Multiply(view, proj);
    }

    /// <summary>What a world-sized billboard measures on screen, from the projection alone - the figure
    /// the cap is there to limit.</summary>
    private static float UncappedPixels(float worldSize, float distance) =>
        worldSize / (2f * MathF.Tan(Fov * 0.5f) * distance) * ViewportH;

    private static float DrawnPixels(float worldSize, float distance)
    {
        float capped = ViewportIcons.CapWorldSize(Vector3.Zero, Vector3.UnitY, worldSize,
            ViewProjectionAt(distance), ViewportH);
        return UncappedPixels(capped, distance);
    }

    /// <summary>
    /// The reported bug: "the icon does not get smaller as I come closer". A marker is a WORLD-sized
    /// billboard, so closing in grew it without limit until it covered the particle being moved.
    /// </summary>
    [Fact]
    public void ClosingInStopsGrowingTheMarkerOnceItFillsTheCap()
    {
        const float world = 144f;   // what the app supplies on a big map: Clamp(radius*0.004, 4, 90)*1.6
        float capPx = ViewportH * ViewportIcons.MaxHeightFraction;

        // Far enough away that the cap does not bite: drawn exactly as before.
        Assert.Equal(UncappedPixels(world, 20000f), DrawnPixels(world, 20000f), 2);

        // Close in, and the UNCAPPED size runs away while the drawn one stops at the cap.
        Assert.True(UncappedPixels(world, 200f) > capPx * 4f,
            "the test camera is not close enough for the old behaviour to be a problem");
        Assert.Equal(capPx, DrawnPixels(world, 200f), 1);
        Assert.Equal(capPx, DrawnPixels(world, 50f), 1);
    }

    /// <summary>The far half is deliberately untouched: markers still shrink with distance, which is what
    /// keeps a zoomed-out map from becoming a wall of icons.</summary>
    [Fact]
    public void DistanceStillShrinksAMarker()
    {
        const float world = 144f;
        float near = DrawnPixels(world, 6000f);
        float far = DrawnPixels(world, 24000f);
        Assert.True(near > far * 3f, $"expected the far marker to be much smaller, got {near:F1} vs {far:F1}");
    }

    [Fact]
    public void TheCapNeverEnlargesAMarkerAndSurvivesDegenerateInput()
    {
        const float world = 10f;
        Assert.Equal(world, ViewportIcons.CapWorldSize(Vector3.Zero, Vector3.UnitY, world,
            ViewProjectionAt(9000f), ViewportH), 3);

        // No viewport, nothing behind the eye, no size: the supplied size comes back untouched rather
        // than a NaN or a zero-size quad.
        Assert.Equal(world, ViewportIcons.CapWorldSize(Vector3.Zero, Vector3.UnitY, world, ViewProjectionAt(500f), 0f));
        Assert.Equal(0f, ViewportIcons.CapWorldSize(Vector3.Zero, Vector3.UnitY, 0f, ViewProjectionAt(500f), ViewportH));
        var behind = ViewportIcons.CapWorldSize(new Vector3(0f, 0f, 5000f), Vector3.UnitY, world,
            ViewProjectionAt(500f), ViewportH);
        Assert.Equal(world, behind, 3);
    }

    /// <summary>Turning the cap off restores exactly the pre-M657 behaviour, which is what makes it a
    /// setting rather than a hard-coded opinion.</summary>
    [Fact]
    public void ZeroDisablesTheCap()
    {
        float previous = ViewportIcons.MaxHeightFraction;
        try
        {
            ViewportIcons.MaxHeightFraction = 0f;
            Assert.Equal(144f, ViewportIcons.CapWorldSize(Vector3.Zero, Vector3.UnitY, 144f,
                ViewProjectionAt(50f), ViewportH), 3);
        }
        finally { ViewportIcons.MaxHeightFraction = previous; }
    }
}
