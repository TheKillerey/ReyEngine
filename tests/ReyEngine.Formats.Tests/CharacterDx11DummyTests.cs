namespace ReyEngine.Formats.Tests;

/// <summary>
/// M627/M628: the target dummy on the D3D11 character preview, and the silence that made the last four
/// milestones guesswork.
///
/// <para>These are wiring assertions, which is the right shape for the fault they cover: every one of
/// them is a single line that compiles perfectly whether or not it is there, and whose absence shows up
/// only as something missing on screen.</para>
/// </summary>
public sealed class CharacterDx11DummyTests
{
    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        return dir?.FullName;
    }

    private static string? Read(params string[] parts)
    {
        if (RepoRoot() is not { } root) return null;
        string path = Path.Combine(new[] { root }.Concat(parts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static string? Window() => Read("src", "ReyEngine.App", "Views", "MeshPreviewWindow.Dx11.cs");
    private static string? Surface() => Read("src", "ReyEngine.App", "Views", "Dx11ViewportSurface.cs");

    // ===================================================== a failed frame must not be silent

    [Fact]
    public void TheRenderErrorIsKeptRatherThanDiscarded()
    {
        // RenderFrame catches every exception internally and returns null with a reason. Passing `out _`
        // made a frame that FAILED indistinguishable from one that was never asked for: no log, no status,
        // and the last good bitmap still on screen - a viewport that looks live and is minutes old.
        if (Surface() is not { } text) return;

        Assert.Contains("out var renderError", text);
        Assert.Contains("public string? LastError", text);
        Assert.DoesNotContain("RenderFrame(width, height, settings, out _,", text);
    }

    [Fact]
    public void TheWindowShowsWhyAFrameFailed()
    {
        if (Window() is not { } text) return;
        Assert.Contains("render failed: ", text);
        Assert.Contains("_dx11.LastError", text);
    }

    // ===================================================== picking must survive a failed frame

    [Fact]
    public void PickMatricesAreRefreshedBeforeTheRenderNotAfterIt()
    {
        // They live on the GL control, which is hidden and not rendering, so this call is the only thing
        // that keeps the dummy drag and the M613 right-click orders alive. Behind an early return, one
        // declined frame froze picking - at Identity/1x1 on the very first one - and silently.
        if (Window() is not { } text) return;

        int sync = text.IndexOf("SyncPickMatrices(", StringComparison.Ordinal);
        int render = text.IndexOf("_dx11.Render(", StringComparison.Ordinal);
        Assert.True(sync > 0 && render > 0, "the pick sync or the render call is gone");
        Assert.True(sync < render,
            "SyncPickMatrices must run BEFORE the render, or a failed frame leaves picking stale");
    }

    // ===================================================== the dummy itself

    [Fact]
    public void TheDummyModelIsHandedToTheD3D11Surface()
    {
        // PropMeshes had exactly one assignment in the whole app - the map window's - so the preview's
        // prop driver was never even constructed and the dummy could not draw however well it loaded.
        if (Window() is not { } text) return;
        Assert.Contains("_dx11.PropMeshes = vm.DummyProps", text);
    }

    [Fact]
    public void ThereIsABoxForWhenTheDummyModelIsUnavailable()
    {
        // The model lives in Map11.wad and is absent on some installs; GL falls back to a cube and D3D11
        // had no equivalent at all. Built from the SHARED BuildBoxLines so both viewports agree on where
        // the dummy is - a stand-in drawn somewhere else would be worse than none.
        if (Window() is not { } window || Surface() is not { } surface) return;

        Assert.Contains("_dx11.DummyLines", window);
        Assert.Contains("ViewportMeshRenderer.BuildBoxLines", window);
        Assert.Contains("_renderer.SetDummyLines(DummyLines)", surface);
    }

    [Fact]
    public void TheModelAndTheBoxAreMutuallyExclusive()
    {
        // DummyCubePosition is non-null only while DummyProps is null, so feeding both from those two
        // properties can never draw the box inside the model.
        if (Window() is not { } text) return;
        Assert.Contains("vm.DummyCubePosition is { } box", text);
    }

    [Fact]
    public void TheDummyChannelExistsOnTheRenderer()
    {
        if (Read("src", "ReyEngine.Rendering.D3D11", "ShaderPreviewRenderer.cs") is not { } text) return;
        Assert.Contains("public void SetDummyLines(", text);
        Assert.Contains("DrawDummyLines(view, proj)", text);
    }

    // ===================================================== the commit must not orphan the other drivers

    [Fact]
    public void ASceneCommitReRegistersTheParticleAndPropDrivers()
    {
        // Dx11CharacterScene.Commit calls ClearMaterials, which disposes the particle and prop materials
        // along with the character's and releases the geometry their handles point at. The map host has
        // called NotifySceneRebuilt after every commit since M266; this one never did, so every champion
        // load silently destroyed them and nothing brought them back.
        if (Window() is not { } text) return;

        int commit = text.IndexOf("Dx11CharacterScene.Commit(", StringComparison.Ordinal);
        int notify = text.IndexOf("NotifySceneRebuilt()", StringComparison.Ordinal);
        Assert.True(commit > 0, "the commit call is gone");
        Assert.True(notify > commit, "NotifySceneRebuilt must run AFTER the commit that cleared them");
    }
}
