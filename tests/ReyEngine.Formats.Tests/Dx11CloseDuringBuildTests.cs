using ReyEngine.App.Services;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M827: closing a D3D11 window while a scene build was in flight crashed the process (access violation in
/// <c>Dx11SceneBuilder.Commit</c> inside <c>CreateBuffer</c>): the device was disposed, the build still committed into it.
/// </summary>
public sealed class Dx11CloseDuringBuildTests
{
    private static Dx11SceneBuilder.PreparedScene Tiny() => new()
    {
        Mesh = PreviewGeometry.CreateBuiltIn("Sphere"),
        Slices = new(),
        Textures = new(),
    };

    [Fact]
    public void Committing_into_a_renderer_that_was_closed_is_a_no_op_not_a_native_crash()
    {
        var renderer = new ShaderPreviewRenderer();
        if (!renderer.Initialize(out _)) { renderer.Dispose(); return; }   // no D3D11 device on this machine
        Assert.False(renderer.IsDisposed);

        renderer.Dispose();            // the window closed while the CPU half was still running
        Assert.True(renderer.IsDisposed);

        var result = Dx11SceneBuilder.Commit(renderer, Tiny(), "test");   // used to be an access violation
        Assert.Equal(0, result.Materials);
        Assert.Contains("closed", result.Report);
        renderer.Dispose();            // a second close is harmless
    }

    [Fact]
    public void Committing_into_a_live_renderer_still_builds()
    {
        var renderer = new ShaderPreviewRenderer();
        if (!renderer.Initialize(out _)) { renderer.Dispose(); return; }
        try
        {
            var result = Dx11SceneBuilder.Commit(renderer, Tiny(), "test");
            Assert.DoesNotContain("was closed", result.Report);
            Assert.False(renderer.IsDisposed);
        }
        finally { renderer.Dispose(); }
    }
}
