using System;
using System.Diagnostics;
using System.Numerics;
using Avalonia.Controls;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;

namespace ReyEngine.App.Views;

/// <summary>
/// M783: the Particle Editor's D3D11 half - the device, the frame loop and the presentation, the same
/// shape MeshPreviewWindow.Dx11.cs uses for the character window (M618) and the map viewport before it
/// (M248): a compositor-driven, rate-capped loop that only turns while the toggle is on, drawing into an
/// Image that sits behind the same transparent input Border the GL control does. Input, camera and picking
/// are untouched - both renderers share <see cref="ViewportControl.Camera"/> and the pick-matrix cache
/// <see cref="ViewportControl.SyncPickMatrices"/> refreshes here every D3D11 frame, which is what keeps
/// dragging the Move handle working whichever renderer is actually drawing it.
///
/// <para>Unlike the character window, this host draws no scene at all - only particles and the editor's
/// own furniture (the gizmo, the force shapes, the floor grid). There is no "commit a scene" step and
/// nothing here ever calls <c>Dx11ViewportSurface.NotifySceneRebuilt</c>.</para>
/// </summary>
public partial class ParticleEditorView
{
    private Dx11ViewportSurface? _dx11;
    private bool _dx11FrameQueued;
    private bool _dx11Closed;
    private readonly Stopwatch _dx11Clock = Stopwatch.StartNew();
    private double _dx11LastFrame = double.NegativeInfinity;
    private string? _dx11LastErrorShown;
    private double _dx11CacheCheckedAt = double.NegativeInfinity;

    /// <summary>~60 fps, like every other D3D11 host in the app - the preview is one system, but the loop
    /// still self-throttles rather than spinning the GPU on an effect nobody is moving.</summary>
    private const double Dx11FrameSeconds = 1.0 / 60.0;

    private void HookDx11()
    {
        DetachedFromVisualTree += (_, _) => { _dx11Closed = true; _dx11?.Dispose(); _dx11 = null; };
        // M799: MainWindow builds `new ParticleEditorWindow { DataContext = ... }` BEFORE it shows the window, with
        // D3D11 already on, so StartDx11 runs while this view is in no window and QueueDx11Frame cannot ask for a
        // frame yet. The first frame is asked for here, once the view is actually in one.
        AttachedToVisualTree += (_, _) =>
        {
            if (DataContext is ParticleEditorViewModel { UseDx11Preview: true }) QueueDx11Frame();
        };
        DataContextChanged += (_, _) => WatchDx11Toggle();
        WatchDx11Toggle();
    }

    private void WatchDx11Toggle()
    {
        if (DataContext is not ParticleEditorViewModel vm) return;
        // M667: the GL viewport's own breadcrumbs go to the same app log as everything else.
        PreviewViewport.Log ??= (cat, msg) => vm.LogDx11?.Invoke(cat, msg);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ParticleEditorViewModel.UseDx11Preview) && vm.UseDx11Preview)
                StartDx11(vm);
        };
        if (vm.UseDx11Preview) StartDx11(vm);
    }

    private void StartDx11(ParticleEditorViewModel vm)
    {
        if (_dx11Closed) return;
        _dx11 ??= new Dx11ViewportSurface();
        if (!_dx11.IsReady && !_dx11.Initialize())
        {
            // Turned back off rather than left ticking against a device that does not exist - see
            // MeshPreviewWindow.Dx11.cs's StartDx11 for the same call and the same reasoning. The toggle goes
            // off FIRST: turning it off clears the status, which would erase the reason just written.
            vm.UseDx11Preview = false;
            vm.Dx11Status = "D3D11 unavailable: " + (_dx11.Error ?? "unknown");
            return;
        }

        // This host's own furniture, pushed once: neither the grid's shape nor the force-ring colour ever
        // changes for the Particle Editor, so there is nothing to repeat every frame.
        // M753: the SAME violet (or its GL fallback) the constructor gives PreviewViewport.RangeRingTint -
        // a force ring must read the same colour whichever renderer drew it.
        _dx11.Renderer.RangeLineColor = PreviewViewport.RangeRingTint ?? Rendering.ViewportMeshRenderer.RangeRingGreen;
        _dx11.Renderer.GroundGrid = true;
        _dx11.Renderer.SetGroundGridLines(Rendering.GridRenderer.BuildGeometry(20, 100f, out _, out _));

        QueueDx11Frame();
    }

    private void QueueDx11Frame()
    {
        if (_dx11Closed || _dx11FrameQueued) return;
        // M799: no window yet - do NOT mark a frame as queued. The flag used to be set first and the request then
        // skipped by `?.`, so the flag stayed set and the loop never started (the D3D11 view stayed black with an
        // empty status). AttachedToVisualTree asks again once the view is in a window.
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        _dx11FrameQueued = true;
        top.RequestAnimationFrame(_ =>
        {
            _dx11FrameQueued = false;
            if (_dx11Closed || DataContext is not ParticleEditorViewModel vm || !vm.UseDx11Preview) return;

            double now = _dx11Clock.Elapsed.TotalSeconds;
            if (now - _dx11LastFrame >= Dx11FrameSeconds)
            {
                _dx11LastFrame = now;
                // M632 (ported): a throw in here must not kill the loop for good - it re-arms itself on
                // its LAST line, and a frozen picture would silently freeze picking with it.
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

    private void RenderDx11Frame(ParticleEditorViewModel vm)
    {
        if (_dx11 is null || !_dx11.IsReady) return;

        // Sized from PreviewInput, not from PreviewViewport - PreviewViewport is the GL control and it is
        // HIDDEN while D3D11 is on, and Avalonia freezes Bounds on an invisible control. PreviewInput is
        // the transparent Border that takes the clicks and is visible in both modes.
        var surface = PreviewInput.Bounds;
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int w = (int)(surface.Width * scale);
        int h = (int)(surface.Height * scale);
        if (w <= 0 || h <= 0) return;

        // M755/M783: a character host is posed for "born on the character" emitters only on the GL path
        // (ParticleEditorViewModel.PosedEmissionHost feeds ViewportControl's own simulators). Rather than
        // draw the rest of the system correctly and silently drop that emitter's particles, the whole
        // preview falls back to GL for as long as a host is attached - GL already draws it right.
        if (vm.HasHost)
        {
            if (vm.UseDx11Preview)
            {
                vm.UseDx11Preview = false;
                vm.Dx11Status = "A character host is set - showing GL, which poses it. Clear the host to use D3D11 again.";
            }
            Dx11Preview.Source = null;
            return;
        }

        // Resolving probes the game folder on disk, so it is asked at most once a second rather than on every
        // frame; the answer only changes when the project's game folder does.
        var cache = _dx11.ShaderCache;
        double checkedNow = _dx11Clock.Elapsed.TotalSeconds;
        if (cache is null || checkedNow - _dx11CacheCheckedAt >= 1.0)
        {
            _dx11CacheCheckedAt = checkedNow;
            cache = vm.ResolveDx11ShaderCache?.Invoke();
        }
        if (cache is null)
        {
            // M783: fall back to GL rather than sit on a blank D3D11 image - the same "turn the toggle back
            // off" MeshPreviewWindow.Dx11.cs's StartDx11 does when Initialize() fails. Unlike that case this
            // one can resolve itself (setting the project's game directory), so it is not remembered beyond
            // this frame; the user can simply re-check the box once it does.
            if (vm.UseDx11Preview)
            {
                vm.UseDx11Preview = false;
                vm.Dx11Status = "D3D11 unavailable: no shader cache open (set the project's game directory).";
            }
            Dx11Preview.Source = null;
            return;
        }
        _dx11.ShaderCache = cache;

        if (vm.Playback is null)
        {
            Dx11Preview.Source = null;
            vm.Dx11Status = "Nothing selected.";
            return;
        }

        _dx11.ParticlePlayback = vm.Playback;
        _dx11.AnimateTime = !vm.Paused;
        _dx11.ParticleTimeScale = vm.Speed;
        // M712/M185/M186: the rig, the manual Stop and the auto-stop loop - the SAME properties the GL
        // control is bound to in XAML (Rig/Stopped/AutoStop), forwarded to the D3D11 driver every frame
        // because all three change live while the preview plays.
        _dx11.Rig = vm.Rig;
        _dx11.Stopped = vm.Stopped;
        _dx11.AutoStop = vm.AutoStop;
        // M753: the force shapes, through the range-ring channel - the same one the GL control tints
        // violet via RangeRingTint.
        _dx11.RangeLines = vm.ForceShapeLines;

        // M753: the Move handle, built by ViewportMeshRenderer's own builder at the arm length
        // HitTestGizmoAxis measures against - so what is DRAWN and what is GRABBABLE are the same geometry.
        if (vm.GizmoPivot is { } pivot)
        {
            float arm = PreviewViewport.GizmoArmLengthFor(pivot);
            _dx11.Renderer.SetGizmoGeometry(
                Rendering.ViewportMeshRenderer.BuildGizmoAxis(0, pivot, Vector3.UnitX, arm),
                Rendering.ViewportMeshRenderer.BuildGizmoAxis(0, pivot, Vector3.UnitY, arm),
                Rendering.ViewportMeshRenderer.BuildGizmoAxis(0, pivot, Vector3.UnitZ, arm));
        }
        else _dx11.Renderer.SetGizmoGeometry(null, null, null);

        // BEFORE the render: these are the matrices the Move-handle drag raycasts against, and refreshing
        // them after an early return would freeze picking along with the picture.
        PreviewViewport.SyncPickMatrices(surface.Width, surface.Height);

        if (!_dx11.Render(PreviewViewport.Camera, w, h))
        {
            if (_dx11.LastError is { Length: > 0 } why && why != _dx11LastErrorShown)
            {
                _dx11LastErrorShown = why;
                vm.Dx11Status = "render failed: " + why;
                vm.LogDx11?.Invoke("D3D11", "render failed: " + why);
            }
            Dx11Preview.Source = null;   // no stale frame while nothing renders
            return;
        }
        _dx11LastErrorShown = null;

        Dx11Preview.Source = _dx11.Current;
        Dx11Preview.Width = surface.Width;
        Dx11Preview.Height = surface.Height;

        vm.Dx11Status = _dx11.ParticleStatus.Length > 0 ? _dx11.ParticleStatus : "0 systems playing";
    }
}
