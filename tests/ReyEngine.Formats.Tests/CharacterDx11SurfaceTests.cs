using System.Reflection;
using System.Text.RegularExpressions;
using ReyEngine.App.ViewModels;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M618: the D3D11 surface in the character preview window.
///
/// <para>The device, the frame loop and the presentation cannot be exercised without a GPU, so what is
/// asserted here is everything around them that can silently be wrong: that the two renderers really do
/// swap rather than stack, that the palette is built off the same clock the GL path animates with, and
/// that a subject with no scene says so instead of showing an empty frame.</para>
/// </summary>
public sealed class CharacterDx11SurfaceTests
{
    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        return dir?.FullName;
    }

    private static string Xaml(string name) =>
        Path.Combine(RepoRoot() ?? "", "src", "ReyEngine.App", "Views", name);

    // ===================================================== the two renderers swap

    [Fact]
    public void TheGlControlStepsAsideWhenD3D11IsOn()
    {
        // Stacked rather than swapped is the failure that looks like "D3D11 does nothing": the GL control
        // draws over the image every frame and the toggle appears inert.
        if (RepoRoot() is null || !File.Exists(Xaml("MeshPreviewWindow.axaml"))) return;
        string text = File.ReadAllText(Xaml("MeshPreviewWindow.axaml"));

        Assert.Contains("x:Name=\"Dx11Preview\"", text);
        Assert.Contains("IsVisible=\"{Binding UseDx11Preview}\"", text);
        Assert.Contains("IsVisible=\"{Binding !UseDx11Preview}\"", text);
    }

    [Fact]
    public void TheImageIsFilledNotCentreCropped()
    {
        // Stretch=None centre-crops a device-pixel bitmap inside a logical-pixel Image, which draws the
        // middle of the frustum magnified and puts every click in the wrong place. Measured in M500 on
        // the map viewport; the same bitmap and the same mismatch apply here.
        if (RepoRoot() is null || !File.Exists(Xaml("MeshPreviewWindow.axaml"))) return;
        string text = File.ReadAllText(Xaml("MeshPreviewWindow.axaml"));

        var image = Regex.Match(text, @"<Image x:Name=""Dx11Preview""(.|\n)*?/>");
        Assert.True(image.Success, "the Dx11Preview image is missing");
        Assert.Contains("Stretch=\"Fill\"", image.Value);
    }

    [Fact]
    public void EveryRendererBindingResolves()
    {
        if (RepoRoot() is null || !File.Exists(Xaml("MeshPreviewWindow.axaml"))) return;
        string text = File.ReadAllText(Xaml("MeshPreviewWindow.axaml"));

        var card = Regex.Match(text, @"Text=""RENDERER""(.|\n)*?</StackPanel>");
        Assert.True(card.Success, "the RENDERER card is missing");

        var missing = new List<string>();
        foreach (Match m in Regex.Matches(card.Value, @"\{Binding\s+!?([A-Za-z_][A-Za-z0-9_]*)"))
        {
            string path = m.Groups[1].Value;
            if (typeof(MeshPreviewViewModel).GetProperty(path, BindingFlags.Public | BindingFlags.Instance) is null)
                missing.Add(path);
        }
        Assert.True(missing.Count == 0, "bound in the RENDERER card but not on the view model: "
                                        + string.Join(", ", missing.Distinct()));
    }

    // ===================================================== the view model half

    [Fact]
    public void ASubjectWithNoSceneSaysSoRatherThanShowingAnEmptyFrame()
    {
        var preview = new MeshPreviewViewModel();
        preview.SetDx11Scene(null, "");

        preview.UseDx11Preview = true;

        Assert.False(string.IsNullOrWhiteSpace(preview.Dx11Status));
        Assert.Contains("not a character", preview.Dx11Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TurningTheRendererOffClearsItsStatus()
    {
        // The status describes a renderer that is no longer drawing; leaving it up reads as if it is.
        var preview = new MeshPreviewViewModel();
        preview.UseDx11Preview = true;
        preview.UseDx11Preview = false;

        Assert.Equal("", preview.Dx11Status);
    }

    [Fact]
    public void ANewSceneIsSignalledSoTheWindowCommitsItOnce()
    {
        // A reference check is not enough - the same scene can be handed over again after an edit - and
        // committing every frame would rebuild every pipeline sixty times a second.
        var preview = new MeshPreviewViewModel();
        int first = preview.Dx11SceneRevision;

        preview.SetDx11Scene(null, "a");
        preview.SetDx11Scene(null, "b");

        Assert.Equal(first + 2, preview.Dx11SceneRevision);
    }

    [Fact]
    public void WithNoSkeletonThePaletteIsNullSoTheBindPoseStands()
    {
        // Null is the contract the renderer already had: it keeps the M216 constant, which is the bind
        // pose. A zero-filled palette would collapse the mesh to the origin instead.
        var preview = new MeshPreviewViewModel();
        Assert.Null(preview.CurrentBonePalette());
    }

    [Fact]
    public void TheHostPreparesASceneWhenASkinLoads()
    {
        // The link that lives in the main window and is invisible to every other test here: without it
        // the toggle is present, the loop runs, and there is never anything to draw.
        if (RepoRoot() is not { } root) return;
        string source = Path.Combine(root, "src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        if (!File.Exists(source)) return;

        string text = File.ReadAllText(source);
        Assert.Contains("BuildCharacterDx11Scene(entry)", text);
        Assert.Contains("MeshPreview.SetDx11Scene(", text);
    }

    [Fact]
    public void ThePaletteReachesTheSurface()
    {
        // Three links in a row, each of which compiles perfectly while doing nothing: the window sets it
        // on the surface, the surface puts it in the settings, the renderer reads it from there.
        if (RepoRoot() is not { } root) return;

        string surface = Path.Combine(root, "src", "ReyEngine.App", "Views", "Dx11ViewportSurface.cs");
        string window = Path.Combine(root, "src", "ReyEngine.App", "Views", "MeshPreviewWindow.Dx11.cs");
        if (!File.Exists(surface) || !File.Exists(window)) return;

        Assert.Contains("BonePalette = BonePalette", File.ReadAllText(surface));
        Assert.Contains("_dx11.BonePalette = vm.CurrentBonePalette()", File.ReadAllText(window));
    }

    [Fact]
    public void TheFrameIsSizedFromTheInputBorderNotTheHiddenGlControl()
    {
        // Avalonia freezes Bounds on an invisible control. Sizing from the GL control - which is hidden
        // whenever D3D11 is on - leaves the render size stuck at whatever it last measured, so resizing
        // the window does nothing and every click lands somewhere else.
        if (RepoRoot() is not { } root) return;
        string window = Path.Combine(root, "src", "ReyEngine.App", "Views", "MeshPreviewWindow.Dx11.cs");
        if (!File.Exists(window)) return;

        string text = File.ReadAllText(window);
        Assert.Contains("PreviewInput.Bounds", text);
        Assert.DoesNotContain("PreviewViewport.Bounds", text);
    }
}
