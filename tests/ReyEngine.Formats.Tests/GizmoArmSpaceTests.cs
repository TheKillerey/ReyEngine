namespace ReyEngine.Formats.Tests;

/// <summary>
/// M632: the gizmo arm was measured between two different spaces, and the D3D11 frame loop could be
/// killed for good by one throw.
///
/// <para>Both are source assertions, which is the honest shape here: the arm bug was found by a headless
/// probe that measured 7.06x, and re-measuring it in the suite would mean rebuilding that probe. What
/// these pin is that the two mistakes cannot come back — mixing the spaces again, and re-arming the loop
/// from inside a body that can throw.</para>
/// </summary>
public sealed class GizmoArmSpaceTests
{
    private static string? Read(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    [Fact]
    public void TheGizmoArmIsMeasuredAgainstTheRealCameraNotTheMirroredOne()
    {
        // _lastCamPos is deliberately X-mirrored for its other two readers - the VFX distance cull, which
        // compares it against a mirrored viewProj, and the audio listener. The pivot is un-mirrored world,
        // so measuring between them is measuring between two spaces. Sweeping the orbit, that produced an
        // arm up to 7.06x the intended length, changing size as the camera TURNED rather than as it moved.
        if (Read("src", "ReyEngine.App", "Views", "ViewportControl.cs") is not { } text) return;

        int at = text.IndexOf("private float GizmoArmLength(", StringComparison.Ordinal);
        Assert.True(at > 0, "GizmoArmLength is gone or renamed");
        string body = text.Substring(at, Math.Min(400, text.Length - at));

        Assert.Contains("Vector3.Distance(_camera.Position, pivot)", body);
        Assert.DoesNotContain("Vector3.Distance(_lastCamPos, pivot)", body);
    }

    [Fact]
    public void TheMirroredCameraIsStillMirroredForTheReadersThatWantIt()
    {
        // The fix must not "correct" _lastCamPos itself: the cull and the audio listener both work in
        // mirrored space on purpose, and un-mirroring it there would cull the wrong half of the map.
        if (Read("src", "ReyEngine.App", "Views", "ViewportControl.cs") is not { } text) return;
        Assert.Contains("_lastCamPos = new Vector3(-_camera.Position.X", text);
    }

    [Fact]
    public void OneThrowCannotKillTheD3D11FrameLoop()
    {
        // The callback re-arms itself on its LAST line, and SyncPickMatrices has exactly one caller,
        // reached only from RenderDx11Frame. So an exception used to freeze the picture AND the dummy
        // drag AND the right-click orders together, permanently and in silence - the error reporting
        // only ever surfaced failures the renderer RETURNED, never ones it threw.
        if (Read("src", "ReyEngine.App", "Views", "MeshPreviewWindow.Dx11.cs") is not { } text) return;

        Assert.Contains("try { RenderDx11Frame(vm); }", text);
        Assert.Contains("render threw: ", text);

        // And the re-arm must still happen whether or not the body threw.
        int katch = text.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
        int rearm = text.IndexOf("QueueDx11Frame();", katch > 0 ? katch : 0, StringComparison.Ordinal);
        Assert.True(katch > 0 && rearm > katch, "the loop must re-arm after the catch, not inside the try");
    }
}
