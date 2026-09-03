using System;
using System.Diagnostics;
using Avalonia.Controls;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;

namespace ReyEngine.App.Views;

/// <summary>
/// M618: the D3D11 half of the model preview window — the device, the frame loop and the presentation.
///
/// <para>Deliberately the same shape as the map viewport's (M248): a compositor-driven, rate-capped loop
/// that only turns while the toggle is on, drawing into an Image that sits behind the same transparent
/// input Border the GL control does. Nothing about input, camera or picking changes — both renderers are
/// driven by the one <see cref="ViewportControl.Camera"/>, which is what keeps an A/B honest.</para>
/// </summary>
public partial class MeshPreviewWindow
{
    private Dx11ViewportSurface? _dx11;
    private bool _dx11FrameQueued;
    private bool _dx11Closed;
    private int _dx11CommittedRevision = -1;
    private readonly Stopwatch _dx11Clock = Stopwatch.StartNew();
    private double _dx11LastFrame = double.NegativeInfinity;
    private int _dx11LastReport = -1;
    private bool _dx11ReportedBlank;
    private string? _dx11LastErrorShown;

    /// <summary>~60 fps. The preview is one character, but the loop still self-throttles rather than
    /// spinning the GPU on a model nobody is moving.</summary>
    private const double Dx11FrameSeconds = 1.0 / 60.0;

    private void HookDx11()
    {
        Closed += (_, _) => { _dx11Closed = true; _dx11?.Dispose(); _dx11 = null; };
        DataContextChanged += (_, _) => WatchDx11Toggle();
        WatchDx11Toggle();
    }

    private void WatchDx11Toggle()
    {
        if (DataContext is not MeshPreviewViewModel vm) return;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MeshPreviewViewModel.UseDx11Preview) && vm.UseDx11Preview)
                StartDx11(vm);
        };
        if (vm.UseDx11Preview) StartDx11(vm);
    }

    private void StartDx11(MeshPreviewViewModel vm)
    {
        if (_dx11Closed) return;
        _dx11 ??= new Dx11ViewportSurface();
        if (!_dx11.IsReady && !_dx11.Initialize())
        {
            // Turned back off rather than left ticking against a device that does not exist: an unticked
            // box beside the reason is a state the user can act on, a frozen image is not.
            vm.Dx11Status = "D3D11 unavailable: " + (_dx11.Error ?? "unknown");
            vm.UseDx11Preview = false;
            return;
        }

        _dx11CommittedRevision = -1;    // commit whatever scene is loaded, on the next frame
        _dx11ReportedBlank = false;
        QueueDx11Frame();
    }

    private void QueueDx11Frame()
    {
        if (_dx11Closed || _dx11FrameQueued) return;
        _dx11FrameQueued = true;
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(_ =>
        {
            _dx11FrameQueued = false;
            if (_dx11Closed || DataContext is not MeshPreviewViewModel vm || !vm.UseDx11Preview) return;

            double now = _dx11Clock.Elapsed.TotalSeconds;
            if (now - _dx11LastFrame >= Dx11FrameSeconds)
            {
                _dx11LastFrame = now;
                // M632: a throw in here used to kill the loop for good. This callback re-arms itself on
                // its LAST line, and SyncPickMatrices has exactly one caller, reached only from
                // RenderDx11Frame - so one exception froze the picture AND froze the dummy drag and the
                // right-click orders with it, silently, because the error reporting below only surfaces
                // failures the renderer RETURNS and never ones it throws.
                try { RenderDx11Frame(vm); }
                catch (Exception ex)
                {
                    string why = ex.GetType().Name + ": " + ex.Message;
                    if (why != _dx11LastErrorShown)
                    {
                        _dx11LastErrorShown = why;
                        vm.Dx11Status = "render threw: " + why;
                        vm.LogDx11?.Invoke("D3D11", "render threw: " + why);
                    }
                }
            }
            QueueDx11Frame();
        });
    }

    private void RenderDx11Frame(MeshPreviewViewModel vm)
    {
        if (_dx11 is null || !_dx11.IsReady) return;

        // Sized from PreviewInput, not from PreviewViewport. PreviewViewport is the GL control and it is
        // HIDDEN while D3D11 is on; Avalonia freezes Bounds on an invisible control, so resizing would
        // leave the render size stuck at whatever GL last measured. PreviewInput is the transparent
        // Border that takes the clicks and is visible in both modes.
        var surface = PreviewInput.Bounds;
        double scale = RenderScaling;
        int w = (int)(surface.Width * scale);
        int h = (int)(surface.Height * scale);
        if (w <= 0 || h <= 0) return;

        if (vm.Dx11SceneRevision != _dx11CommittedRevision)
        {
            _dx11CommittedRevision = vm.Dx11SceneRevision;
            if (vm.Dx11Scene is { } scene)
            {
                int drew = Dx11CharacterScene.Commit(_dx11.Renderer, scene, GameVersionOf(vm));
                _dx11.HasScene = drew > 0;
                _dx11.SceneReport = scene.Report;

                // M627: Commit calls ClearMaterials, which disposes the particle and prop materials along
                // with the character's and releases the geometry their handles point at. The map host has
                // called this after every commit since M266; this one never did, so every champion load
                // silently destroyed the particle driver's materials and nothing re-registered them.
                _dx11.NotifySceneRebuilt();

                // M625: what the renderer is actually holding, straight after the commit. Two turns were
                // spent theorising about where the character materials went; this reads the list instead.
                vm.LogDx11?.Invoke("D3D11", $"after commit: {_dx11.Renderer.MaterialCount} material(s)");
                foreach (var m in _dx11.Renderer.Materials)
                    vm.LogDx11?.Invoke("D3D11",
                        $"   {(m.Visible ? "shown " : "HIDDEN")} {m.Name}  idx {m.StartIndex}+{m.IndexCount}"
                        + $"  group {m.MapGroupIndex}  dynamic={m.UsesDynamicMesh}");
                vm.Dx11Status = drew > 0
                    ? $"{drew} material(s) drawing"
                      + (scene.Failures.Count > 0 ? $", {scene.Failures.Count} unresolved" : "")
                    : "nothing drew: " + (scene.Failures.Count > 0 ? scene.Failures[0] : "no materials resolved");
            }
            else
            {
                _dx11.HasScene = false;
            }
        }

        // The pose for THIS frame, off the same clock the GL path animates with. Null palette leaves the
        // renderer's bind-pose constant in place, which is right for anything unskinned.
        var (palette, bones) = vm.CurrentPose(vm.ShowBones);
        _dx11.BonePalette = palette;
        _dx11.BoneLines = bones;
        _dx11.World = vm.ModelWorld;   // M620: control mode moves the character here too
        _dx11.Wireframe = vm.Wireframe;

        // M633: the window's own Cull toggle, which the GL viewport beside it has honoured all along.
        //
        // M624 pinned this false and said why: "the character winding on this renderer has never been
        // measured", with M354 - culling on, terrain deleted - as the precedent for not guessing. That was
        // the right call at the time and it is what this milestone came back with the measurement for.
        // Two independent ones, plus a picture; they are written out in Dx11CharacterScene.Commit next to
        // the per-material flag this gates, because that is where the reader who wants them will be.
        //
        // Gated rather than forced. The material decides per submesh (cullEnable, absent = cull), this
        // decides for the viewport, and the AND of the two is what reaches the rasteriser - the same rule
        // GL uses and the same escape hatch: if anything ever does vanish, "Cull" is one click away and
        // turning it off is exactly the old behaviour.
        _dx11.CullBackFaces = vm.CullBackfaces;

        // M619: the VFX. The SAME playback object the GL viewport is bound to in XAML, so both viewports
        // show the same effect at the same age rather than two independent simulations.
        _dx11.ShaderCache = vm.Dx11ShaderCache;
        _dx11.ParticlePlayback = vm.Playback;

        // M630: the two things that make a spell land where it should. Clip particle events ride their
        // bone, and beams terminate at the dummy - both pushed per frame, because the pose changes every
        // animated frame and the dummy moves whenever it is dragged.
        _dx11.BoneGlobals = vm.CurrentBoneGlobals();
        _dx11.BoneModelWorld = vm.ModelWorld;
        _dx11.BeamTarget = vm.TargetDummyPosition;

        // M628: the target dummy itself. Two halves, exactly as the GL viewport has always had them, and
        // the D3D11 host was wired for NEITHER - which is why its gizmo drew over empty space.
        //
        // The real practice-tool model when one loaded (it lives in Map11.wad and is absent on some
        // installs), and a wire box at the same place when it did not. The two are mutually exclusive by
        // construction: DummyCubePosition is non-null only while DummyProps is null.
        // M636: the arena floor rides in the same set as the dummy - one PropRenderSet per renderer.
        _dx11.PropMeshes = vm.SceneProps;
        _dx11.PlayPropAnimations = true;
        _dx11.DummyLines = vm.DummyCubePosition is { } box
            ? Rendering.ViewportMeshRenderer.BuildBoxLines(
                box - new System.Numerics.Vector3(60f, 0f, 60f),
                box + new System.Numerics.Vector3(60f, 120f, 60f))
            : null;

        // M619: the target dummy's translate gizmo, built by ViewportMeshRenderer's own builder at the arm
        // length PreviewViewport.HitTestGizmoAxis measures against - so what is DRAWN and what is
        // GRABBABLE are the same geometry by construction, which is the whole reason the map viewport
        // builds it this way too.
        if (vm.DummyGizmoPivot is { } pivot)
        {
            float arm = PreviewViewport.GizmoArmLengthFor(pivot);
            _dx11.Renderer.SetGizmoLines(
                Rendering.ViewportMeshRenderer.BuildGizmoAxis(0, pivot, System.Numerics.Vector3.UnitX, arm),
                Rendering.ViewportMeshRenderer.BuildGizmoAxis(0, pivot, System.Numerics.Vector3.UnitY, arm),
                Rendering.ViewportMeshRenderer.BuildGizmoAxis(0, pivot, System.Numerics.Vector3.UnitZ, arm));
        }
        else _dx11.Renderer.SetGizmoLines(null, null, null);

        // M627: BEFORE the render, not after. These are the matrices the dummy gizmo drag and the M613
        // right-click orders raycast against, and they live on the GL control, which is hidden and not
        // rendering. Refreshing them after an early return meant that any frame the renderer declined to
        // produce also froze picking - at Identity/1x1 on the very first one - and the failure was silent
        // in both places at once.
        PreviewViewport.SyncPickMatrices(surface.Width, surface.Height);

        if (!_dx11.Render(PreviewViewport.Camera, w, h))
        {
            // M627: say so. A failed frame used to return in silence while the last good bitmap stayed on
            // screen, which reads as a live viewport that has stopped responding.
            if (_dx11.LastError is { Length: > 0 } why && why != _dx11LastErrorShown)
            {
                _dx11LastErrorShown = why;
                vm.Dx11Status = "render failed: " + why;
                vm.LogDx11?.Invoke("D3D11", "render failed: " + why);
            }
            return;
        }
        _dx11LastErrorShown = null;

        // M622: what the renderer actually DID, once a second. Three milestones were spent guessing at an
        // invisible mesh from the outside, and the numbers that separate the possibilities - was it
        // submitted at all, was it frustum-culled, is a palette even being supplied - existed the whole
        // time and were never shown. Cheap, and it makes the next failure a reading rather than a guess.
        if (_dx11.HasScene && (int)(_dx11Clock.Elapsed.TotalSeconds * 2) != _dx11LastReport)
        {
            _dx11LastReport = (int)(_dx11Clock.Elapsed.TotalSeconds * 2);

            // M625: and once, the first time everything is hidden at DRAW time - which is a different
            // moment from the commit above, and the difference between the two is the whole question.
            if (_dx11.LastGeometryDraws == 0 && !_dx11ReportedBlank)
            {
                _dx11ReportedBlank = true;
                vm.LogDx11?.Invoke("D3D11", $"nothing drew: {_dx11.Renderer.MaterialCount} material(s) held");
                foreach (var m in _dx11.Renderer.Materials)
                    vm.LogDx11?.Invoke("D3D11",
                        $"   {(m.Visible ? "shown " : "HIDDEN")} {m.Name}  idx {m.StartIndex}+{m.IndexCount}"
                        + $"  group {m.MapGroupIndex}  dynamic={m.UsesDynamicMesh}");
            }
            vm.Dx11Status =
                $"{_dx11.LastGeometryDraws}/{_dx11.Renderer.MaterialCount} mesh draw(s), "
                + $"{_dx11.LastHidden} hidden, {_dx11.LastCulled} culled, "
                + $"{palette?.Length.ToString() ?? "no"} bone(s), {_dx11.LastFrameMs:F1} ms";
        }

        Dx11Preview.Source = _dx11.Current;
        Dx11Preview.Width = surface.Width;
        Dx11Preview.Height = surface.Height;


    }

    private static string GameVersionOf(MeshPreviewViewModel vm) => "";
}
