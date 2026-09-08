using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ReyEngine.App.ViewModels;

namespace ReyEngine.App.Views;

public partial class MainWindow : Window, ReyEngine.App.ViewModels.ICinematicHost
{
    // ---- M607: ICinematicHost ---------------------------------------------------------------------
    //
    // Implemented here rather than on the view-model because this is the object that owns BOTH halves a
    // capture needs: the D3D11 surface and the camera the viewport is flying. The panel asks for poses
    // and frames and knows nothing about either.

    ReyEngine.Core.Cinematics.CinematicPose ReyEngine.App.ViewModels.ICinematicHost.CurrentPose
    {
        get
        {
            var camera = Viewport.Camera;
            return new ReyEngine.Core.Cinematics.CinematicPose(
                camera.Position,
                ReyEngine.Core.Cinematics.CinematicShot.Aim(camera.Position, camera.Target),
                camera.FieldOfView,
                0f);   // the orbit camera has no roll to record; a keyframe gains one by being edited
        }
    }

    void ReyEngine.App.ViewModels.ICinematicHost.PreviewPose(ReyEngine.Core.Cinematics.CinematicPose? pose)
    {
        if (_dx11 is null) return;
        _dx11.PreviewPose = pose;
        QueueDx11Frame();   // scrubbing must repaint, and nothing else invalidates on a pose change
    }

    IDisposable ReyEngine.App.ViewModels.ICinematicHost.BeginCapture()
    {
        if (_dx11 is null) throw new InvalidOperationException("The Direct3D 11 viewport is not running.");
        return _dx11.BeginCapture();
    }

    byte[]? ReyEngine.App.ViewModels.ICinematicHost.RenderFrame(
        ReyEngine.Core.Cinematics.CinematicPose pose, int width, int height, float timeSeconds) =>
        _dx11?.RenderCaptureFrame(pose, width, height, timeSeconds, Viewport.Camera);

    /// <summary>
    /// M576 (Avalonia 12): a drag payload is now a typed <see cref="DataFormat"/> rather than a string key
    /// on an untyped DataObject. In-process is the right kind for this one - the node is a live view model
    /// that only means anything inside this app, and asking the platform to serialise it would be wrong.
    /// </summary>
    private static readonly DataFormat<AssetNodeViewModel> AssetDragFormat =
        DataFormat.CreateInProcessFormat<AssetNodeViewModel>("rey/asset");

    private Point _lastPointer;
    private bool _lmb, _rmb, _mmb, _alt;
    private readonly HashSet<Key> _heldKeys = new();
    private DispatcherTimer? _flyTimer;

    // Translate-gizmo drag state (mutually exclusive with camera fly for the same LMB stroke).
    private ViewportControl.GizmoAxis? _gizmoDragAxis;
    /// <summary>M567: this drag is moving FACES, not a mesh or a placement.</summary>
    private bool _gizmoTargetIsFaces;
    /// <summary>M568: click count from the press, for double-click linked selection.</summary>
    private int _pressClickCount;
    private float _gizmoDragStartT;
    private Vector3 _gizmoDragStartOffset;
    private Vector3 _gizmoDragOrigin;   // pivot at drag start — the axis line must NOT re-anchor mid-drag
    private Vector3 _gizmoStartRotation; // M42: rotate/scale drag-start state
    private Vector3 _gizmoStartScale;
    private bool _gizmoTargetIsPlacement; // M75: this drag targets a particle/sound placement, not a mesh

    // Click-to-select: a press+release with almost no movement is a pick, not a camera drag.
    private Point _pressPos;
    private bool _pressMoved;
    private const double ClickSlopPixels = 4.0;

    // M248 (phase 6, step 1): the side-by-side D3D11 surface. Null until the toggle is first turned on -
    // a user who never touches it never creates a D3D11 device.
    private Dx11ViewportSurface? _dx11;

    /// <summary>M293: last bucket-grid array handed to D3D11, compared by REFERENCE. The array is
    /// multi-megabyte and is rebuilt only when the grid actually changes, so re-uploading it every frame
    /// would dominate the frame for a buffer whose contents are identical.</summary>
    /// <summary>M660: one guard per overlay. See <see cref="Services.Dx11OverlayGate"/> for what the
    /// single inline guard this replaces was quietly gating.</summary>
    private readonly Services.Dx11OverlayGate _dx11Overlays = new();
    private (System.Numerics.Vector3 Min, System.Numerics.Vector3 Max)? _lastDx11BakeBox;   // M412
    private Services.SkyboxSpec? _lastDx11Skybox;
    private bool _dx11FrameQueued;

    /// <summary>M360: keys painted since the last mip rebuild, so the finish rebuilds only what the
    /// stroke touched instead of the whole texture pool.</summary>
    private readonly HashSet<string> _dx11PaintedKeys = new(StringComparer.Ordinal);
    private bool _closed;

    public MainWindow()
    {
        InitializeComponent();
        LoadBranding();
        TitleVersionText.Text = AppInfo.DisplayVersion;   // M81
        _ = AutoCheckUpdatesAsync();                      // M81: silent startup check

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainWindowViewModel.UseDx11Viewport)) OnDx11Toggled(vm);
                    else if (e.PropertyName == nameof(MainWindowViewModel.MeshVerticesRevision)
                             && vm.UseDx11Viewport && _dx11?.IsReady == true)
                        vm.UpdateDx11EditedMeshVertices(_dx11.Renderer);
                    // M268: and rebuild when the MAP changes underneath a viewport that is already on.
                    // The scene was only ever built on the toggle, so opening a second map left the first
                    // one on screen - stale geometry that looked like the new map had failed to load.
                    else if (e.PropertyName == nameof(MainWindowViewModel.MapGeneration)
                             && vm.UseDx11Viewport) OnDx11Toggled(vm);
                    // M501: ...and when the MATERIALS change under a viewport that is already on. The scene
                    // caches resolved shader permutations, decoded textures and every authored parameter,
                    // so a material edit, a legacy port or a bulk macro change left DX11 drawing the old
                    // ones until the map was reloaded. A full rebuild is heavier than a material-only
                    // refresh would be, but it is the honest fix: a changed material can change the
                    // permutation, which changes the input layout, so nothing shallower is safe yet.
                    else if (e.PropertyName == nameof(MainWindowViewModel.MaterialsRevision)
                             && vm.UseDx11Viewport && _dx11?.IsReady == true) OnDx11Toggled(vm);
                };
        };
        Closed += (_, _) => { _closed = true; _dx11?.Dispose(); _dx11 = null; };
    }

    // ---- M248: the D3D11 side-by-side surface ----

    private async void OnDx11Toggled(MainWindowViewModel vm)
    {
        if (!vm.UseDx11Viewport)
        {
            // Left alive rather than torn down: toggling back and forth to compare is the entire purpose
            // of this step, and re-creating a device each time would make that slow and flickery.
            vm.Dx11ViewportStatus = "";
            return;
        }

        _dx11 ??= new Dx11ViewportSurface();
        if (!_dx11.IsReady && !_dx11.Initialize())
        {
            // Fall straight back to OpenGL and SAY why. A viewport that silently stays blank would look
            // like a rendering bug rather than a device that never came up.
            vm.Dx11ViewportStatus = "D3D11 unavailable: " + (_dx11.Error ?? "unknown");
            vm.UseDx11Viewport = false;
            return;
        }

        // M249 (step 2): build whatever map is open into the D3D11 renderer. Done on toggle - and, since
        // M268, on a map change while the toggle is on - rather than eagerly on every map load, so a user
        // who never enables this never pays for it.
        // M250: the CPU half runs off the UI thread. The surface starts drawing immediately with whatever
        // it has (the fallback mesh on a first toggle) so the viewport is never a frozen blank rectangle
        // while the scene is prepared.
        vm.Dx11ViewportStatus = "D3D11  preparing scene…";
        QueueDx11Frame();

        _dx11.SceneReport = await vm.BuildDx11SceneAsync(_dx11.Renderer);
        _dx11.HasScene = _dx11.Renderer.MaterialCount > 0;

        // M266: the ordering is not negotiable. Dx11SceneBuilder.Commit calls ClearMaterials, which disposes
        // every material AND the texture pool - so any particle material registered before this point is
        // already gone. Re-registering has to happen after the commit, which is what this schedules.
        _dx11.NotifySceneRebuilt();
    }

    /// <summary>Target frame interval for the D3D11 map viewport, in seconds.</summary>
    /// <remarks>
    /// M537: this loop had NO cap. The doc comment used to claim "a DispatcherTimer caps the rate and
    /// measured 20 fps" - that describes the shader preview window; nothing capped THIS path. It asked the
    /// compositor for a frame, rendered, and immediately asked for another, so an idle map with nothing
    /// moving was re-rendered as fast as the GPU could manage: the in-app frame counter read 2.07 ms, or
    /// roughly 480 fps, and the GPU sat at 80%.
    ///
    /// <para>Capping is deliberately all this does. Rendering only on change would be better still, but it
    /// can go STALE - roughly twenty live-editable properties are pushed here every frame precisely so no
    /// one has to remember to invalidate - and a viewport that silently stops updating is a worse bug than
    /// one that costs too much. Deferring a frame can never go stale: the work still happens, just not
    /// hundreds of times a second.</para>
    /// </remarks>
    private const double Dx11FrameSeconds = 1.0 / 60.0;
    private readonly System.Diagnostics.Stopwatch _dx11FrameClock = System.Diagnostics.Stopwatch.StartNew();
    private double _dx11LastFrame = double.NegativeInfinity;

    /// <summary>Compositor-driven and rate-capped. Only runs while the D3D11 toggle is on.</summary>
    private void QueueDx11Frame()
    {
        if (_closed || _dx11FrameQueued) return;
        _dx11FrameQueued = true;
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(_ =>
        {
            _dx11FrameQueued = false;
            if (_closed || DataContext is not MainWindowViewModel vm || !vm.UseDx11Viewport) return;

            // Re-queue without drawing when the last frame is still fresh. The loop keeps turning, so
            // nothing can be missed - it just stops burning a 4090 on a map that is not moving.
            double now = _dx11FrameClock.Elapsed.TotalSeconds;
            if (now - _dx11LastFrame >= Dx11FrameSeconds)
            {
                _dx11LastFrame = now;
                RenderDx11Frame(vm);
            }
            QueueDx11Frame();
        });
    }


    /// <summary>
    /// M400: start the environment crossfade running. NOT a timer.
    ///
    /// <para>M397 used a 16 ms DispatcherTimer that called RequestFrame. Each render of this map costs
    /// more than 16 ms, so requests were queued faster than they drained and the UI thread never got
    /// back to input - the viewport locked for exactly the length of the fade, and the fly camera's
    /// wall-clock dt then arrived as one huge value and threw the camera across the map.</para>
    ///
    /// <para>Frame-driven instead: each rendered frame advances the fade and asks for the next one, so
    /// it self-throttles to whatever the renderer can actually sustain. A slow machine gets a chunkier
    /// fade, never a frozen editor.</para>
    /// </summary>
    private void StartGrassTransitionTimer()
    {
        if (_closed || DataContext is not MainWindowViewModel vm) return;
        if (vm.UseDx11Viewport) QueueDx11Frame();      // RenderDx11Frame ticks it
        else
        {
            if (!_grassFrameHooked) { _grassFrameHooked = true; Viewport.FrameRendered += OnGrassFrame; }
            Viewport.RequestRedraw();
        }
    }

    private bool _grassFrameHooked;

    /// <summary>Advance the fade once per rendered GL frame, and ask for another only while it runs.</summary>
    private void OnGrassFrame()
    {
        if (_closed || DataContext is not MainWindowViewModel vm) return;
        if (vm.TickGrassTransition()) Viewport.RequestRedraw();
    }

    private static void SavePng(string path, byte[] bgra, int w, int h)
    {
        var bmp = new Avalonia.Media.Imaging.WriteableBitmap(
            new Avalonia.PixelSize(w, h), new Avalonia.Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        using (var fb = bmp.Lock())
        {
            int stride = w * 4;
            for (int y = 0; y < h; y++)
                System.Runtime.InteropServices.Marshal.Copy(bgra, y * stride, fb.Address + y * fb.RowBytes, stride);
        }
        using var fs = File.Create(path);
        bmp.Save(fs);
    }

    private static string FirstLine(string s)
    {
        int i = s.IndexOf((char)10);
        return (i < 0 ? s : s[..i]).Trim();
    }

    /// <summary>M278: the one line worth showing when the scene came out empty.
    ///
    /// <para>The status panel has room for a single line, and it was taking the FIRST one - which is the
    /// vertex count, i.e. the one line that is always fine. So when the shader cache was renamed underneath
    /// us the panel said "0 material(s), 21 unresolved" and never got as far as the line naming the path it
    /// could not find. Dx11SceneBuilder now emits "unresolved - {kind}: {detail}"; prefer that.</para></summary>
    private static string WhyNoScene(string report)
    {
        foreach (var line in report.Split((char)10))
        {
            var t = line.Trim();
            if (t.StartsWith("unresolved - ", StringComparison.Ordinal)) return t;
        }
        return FirstLine(report);
    }

    private void RenderDx11Frame(MainWindowViewModel vm)
    {
        if (_dx11 is null || !_dx11.IsReady) return;

        // Match the GL surface's pixel size, scaling included - a mismatch would silently change the
        // aspect ratio and make the two viewports disagree for a reason that has nothing to do with either
        // renderer.
        //
        // M500: sized from ViewportInput, NOT from Viewport. Viewport is the GL control, and it is HIDDEN
        // whenever DX11 is on; Avalonia freezes Bounds on an invisible control, so resizing the window in
        // DX11 mode left the render size, the image size and the pick rect all stuck at whatever the GL
        // control last measured. ViewportInput is the transparent Border that receives the clicks and is
        // visible in both modes, which makes it the only correct source of truth for all three.
        var surface = ViewportInput.Bounds;
        double scale = RenderScaling;
        int w = (int)(surface.Width * scale);
        int h = (int)(surface.Height * scale);
        if (w <= 0 || h <= 0) return;

        // M261: the same lighting inputs the GL surface is bound to in XAML. Pushed every frame rather
        // than on load because all three are live-editable - the sun sliders, the fog toggle and the
        // lightmap scale all change without the scene being rebuilt.
        _dx11.MapSun = vm.CurrentSunProperties;
        _dx11.FogEnabled = vm.ShowFog;
        _dx11.LightmapScale = vm.CurrentLightmapScale;
        _dx11.AnimateTime = vm.AnimationsPlaying;
        _dx11.Wireframe = vm.ShowWireframe;
        // M661: the debug views. The GL viewport is bound to PreviewMode in XAML; this is the same
        // property, so the two viewports show the same view of the same thing.
        _dx11.DebugMode = vm.PreviewMode;
        _dx11.CullBackFaces = vm.CullBackfaces;   // M540: the toggle GL has always honoured
        // M559: Game Depth has to stop the REORDERING too, not just restore the depth mask. The client
        // draws map geometry in its own submission order; we group by pipeline to collapse state changes,
        // which is invisible for depth-writing geometry and is exactly what M279 caught putting a decal at
        // draw position 395 while its ground drew at 407-414. With the emulation on, submission order is
        // preserved, so changing the order in the mapgeo actually changes what the viewport draws - which
        // is what makes the ordering fix testable here instead of only in game.
        _dx11.SortByPipeline = !vm.ClientDepthRules;
        _dx11.Bloom = vm.ShowBloom;   // M460
        _dx11.Shadows = vm.ShowSunShadows;   // M465
        // M452: the dynamic point lights, from the SAME view-model properties the GL viewport is bound to
        // in XAML (DynamicLights / ShowDynamicLights / the fit sliders). Pushed every frame like the sun,
        // because every one of them is live-editable from the Lighting window.
        _dx11.Lights = vm.DynamicLights;
        _dx11.ShowDynamicLights = vm.ShowDynamicLights;
        _dx11.DynamicLightIntensity = vm.DynamicLightIntensity;
        _dx11.DynamicLightRadiusScale = vm.DynamicLightRadiusScale;
        _dx11.LightFalloffSoftness = vm.LightFalloffSoftness;
        _dx11.DynamicLightPositionScale = vm.DynamicLightPositionScale;
        _dx11.DynamicLightScaleX = vm.DynamicLightScaleX;
        _dx11.DynamicLightScaleZ = vm.DynamicLightScaleZ;
        _dx11.DynamicLightOffsetX = vm.DynamicLightOffsetX;
        _dx11.DynamicLightOffsetZ = vm.DynamicLightOffsetZ;
        // M400: same frame-driven tick on the D3D11 side - QueueDx11Frame is already the frame loop.
        vm.TickGrassTransition();
        _dx11.GrassInterp = vm.GrassInterp;
        // M269: pushed every frame rather than on a selection-changed event - the selection, the map and
        // the scene rebuild all move independently, and one of the three going stale is exactly how a
        // highlight ends up pointing at geometry that is no longer there.
        _dx11.Renderer.SetHighlightRanges(vm.Dx11HighlightRanges);
        _dx11.Renderer.SetIcons(vm.Dx11Icons(Viewport.Camera.Distance));
        // M659: the same two viewport rules the GL path follows - icons hidden by what is in front of
        // them unless asked otherwise, and a wire ball at each light showing how far it reaches. Pushed
        // per frame like the icons themselves, and from the SAME numbers, so the two viewports cannot
        // disagree about where a light stops.
        _dx11.Renderer.IconsThroughWalls = vm.IconsThroughWalls;
        _dx11.Renderer.SetLightRangeLines(vm.Dx11LightRangeLines());
        // M292: dragon / baron / render-region filtering, from the same array the GL viewport binds to.
        // Per frame for the same reason the highlight is: the selection, the layer combos and the scene
        // rebuild all move independently, and a rebuild would otherwise come back with everything visible.
        _dx11.ApplyGroupVisibility(vm.CurrentModelSubmeshVisible);
        _dx11.ApplyGroupMirrored(vm.CurrentModelSubmeshMirrored);   // M661: the Mirrored debug view

        // M296: the transform gizmo. Dragging already worked under D3D11 - the transparent input border
        // swallows pointer events in both modes and the hit-test is CPU maths against the matrices
        // SyncPickMatrices refreshes below - but nothing DREW it, so there was nothing to see or aim at.
        // Built from ViewportMeshRenderer's own builder, at the arm length Viewport.HitTestGizmoAxis
        // measures against, so what is drawn and what is grabbable are the same geometry by construction.
        if (vm.GizmoPivot is { } gizmoPivot)
        {
            var axes = vm.GizmoAxes;
            var ax = axes is { Count: 3 } ? axes[0] : System.Numerics.Vector3.UnitX;
            var ay = axes is { Count: 3 } ? axes[1] : System.Numerics.Vector3.UnitY;
            var az = axes is { Count: 3 } ? axes[2] : System.Numerics.Vector3.UnitZ;
            float arm = Viewport.GizmoArmLengthFor(gizmoPivot);
            int mode = vm.TransformMode;
            _dx11.Renderer.SetGizmoGeometry(
                ReyEngine.Rendering.ViewportMeshRenderer.BuildGizmoAxis(mode, gizmoPivot, ax, arm),
                ReyEngine.Rendering.ViewportMeshRenderer.BuildGizmoAxis(mode, gizmoPivot, ay, arm),
                ReyEngine.Rendering.ViewportMeshRenderer.BuildGizmoAxis(mode, gizmoPivot, az, arm));
        }
        else _dx11.Renderer.SetGizmoGeometry(null, null, null);

        // M295: props, from the same set the GL viewport binds to. The setter compares by reference, so
        // this is a no-op until the view-model actually republishes the prop set.
        _dx11.PropMeshes = vm.CurrentPropMeshes;
        _dx11.PlayPropAnimations = vm.PlayPropAnimations;

        // M293: the bucket grid, from the same array the GL viewport is bound to. Re-uploaded only when
        // the ARRAY ITSELF changes - it is multi-megabyte, and the GL host guards it the same way for the
        // same reason. Toggling the grid off publishes null, which clears it.
        if (_dx11Overlays.Changed(Services.Dx11OverlayGate.Overlay.BucketGrid, vm.BucketGridLines))
            _dx11.Renderer.SetBucketGrid(vm.BucketGridLines);

        // M569/M660: the navgrid layers and the face selection. Each on ITS OWN guard - both of these
        // used to sit inside the bucket grid's `if`, so they were published only when the BUCKET GRID
        // changed identity. Switching the navgrid overlay on therefore never reached D3D11 and the layer
        // simply never drew, while the GL viewport (which guards each separately) showed it fine.
        if (_dx11Overlays.Changed(Services.Dx11OverlayGate.Overlay.NavGridCells, vm.BushCellLines)
            | _dx11Overlays.Changed(Services.Dx11OverlayGate.Overlay.NavGridLayers, vm.BushCellLayers))
            _dx11.Renderer.SetNavGridCells(vm.BushCellLines, vm.BushCellLayers);

        if (_dx11Overlays.Changed(Services.Dx11OverlayGate.Overlay.SelectedFaces, vm.SelectedFaceLines))
            _dx11.Renderer.SetSelectedFaces(vm.SelectedFaceLines);
        // M412: the bake-volume preview - 72 floats, value-compared, from the SAME BuildBoxLines the GL
        // side draws, so the two viewports show the identical box.
        if (_lastDx11BakeBox != vm.BakeBox)
        {
            _lastDx11BakeBox = vm.BakeBox;
            _dx11.Renderer.SetBakeBoxLines(vm.BakeBox is { } bb
                ? ReyEngine.Rendering.ViewportMeshRenderer.BuildBoxLines(bb.Min, bb.Max)
                : null);
        }

        // M362: the sky, from the same SkyboxSpec property the GL viewport binds to - so the two renderers
        // cannot show different skies. Reference-guarded like the bucket grid above and for the same
        // reason: a cubemap is six full-resolution faces, and re-uploading it every frame would cost more
        // than everything else in this method. Publishing null clears it.
        if (!ReferenceEquals(_lastDx11Skybox, vm.CurrentSkybox))
        {
            _lastDx11Skybox = vm.CurrentSkybox;
            ApplyDx11Skybox(vm.CurrentSkybox);
        }

        // Pushed per frame rather than on load: the cache is opened lazily the first time a scene is built,
        // which can be after this surface has already drawn its first frames.
        _dx11.ShaderCache = vm.Dx11ShaderCache;
        // M266: the particle gate, and it is CurrentParticlePlayback - the same property MainWindow.axaml
        // binds the GL viewport's ParticlePlayback to. Not ShowParticles: that toggle drives the position
        // markers only, so binding to it would draw particles here while GL draws dots, and hide them here
        // while GL is playing.
        _dx11.ParticlePlayback = vm.CurrentParticlePlayback;

        if (!_dx11.Render(Viewport.Camera, w, h)) return;

        Dx11Surface.Source = _dx11.Current;
        Dx11Surface.Width = surface.Width;
        Dx11Surface.Height = surface.Height;

        // M263: the GL control is hidden and not rendering, so nothing else refreshes the matrices that
        // mesh picking raycasts against. Same size the GL path caches - logical bounds, not pixels.
        // M500: and the SAME rect the image is stretched over, so what is drawn and what is picked agree.
        Viewport.SyncPickMatrices(surface.Width, surface.Height);
        // M263: the toolbar shows the frame cost and nothing else.
        vm.Dx11ViewportStatus = _dx11.HasScene ? $"{_dx11.LastFrameMs:F2} ms" : "no scene";

        // ...and everything that used to be on the toolbar is still one hover away. M255's unbound report
        // in particular: an unbound constant reads as zero, and zero is black for anything the shader
        // multiplies by, which is the difference between a diagnosis and a guess.
        vm.Dx11ViewportDetail = _dx11.HasScene
            ? $"D3D11  ·  {_dx11.LastDrawCalls} draws  ·  {_dx11.LastCulled} culled"
              + (_dx11.UnboundConstants.Count > 0
                  ? "\nUNBOUND: " + string.Join(", ", _dx11.UnboundConstants.Take(6))
                    + (_dx11.UnboundConstants.Count > 6 ? $" +{_dx11.UnboundConstants.Count - 6}" : "")
                  : "\nall declared constants bound")
              // M266: the quad budget is the one place D3D11 can legitimately draw fewer particles than GL,
              // so the line that says how many were thinned belongs where the draw counts are, not in a log.
              + (_dx11.ParticleStatus.Length > 0 ? "\n" + _dx11.ParticleStatus : "")
              // M466: the sun shadow pass. Three distinguishable states rather than a silent absence -
              // switched off, ran and drew N casters, or on but drew nothing (which is the bail-out, and is
              // NOT the same as the lightmap vetoing a shadow that did render).
              + "\nshadow: " + (!vm.ShowSunShadows ? "off"
                  : _dx11.ShadowDraws > 0
                      ? $"{_dx11.ShadowDraws} casters, fit radius {_dx11.ShadowRadius:F0}"
                      : "ON but the pass drew nothing (no blobs / no sun direction / no caster bounds)")
            // No scene is a legitimate state, not a failure - say which, rather than showing an empty
            // viewport and letting it read as a broken renderer.
            : "D3D11 no scene: " + WhyNoScene(_dx11.SceneReport);
    }

    /// <summary>M362: hand a <see cref="Services.SkyboxSpec"/> to the D3D11 renderer. Deliberately the same
    /// source-selection order as the GL path in <c>ViewportControl</c> - exactly one of the three is ever
    /// set, and a spec that somehow carries none clears the sky rather than leaving the previous one up.</summary>
    private void ApplyDx11Skybox(Services.SkyboxSpec? spec)
    {
        if (_dx11 is null) return;
        var r = _dx11.Renderer;
        if (spec is null) { r.ClearSky(); return; }
        // Faces are +X -X +Y -Y +Z -Z: the order the GL path binds them in (TextureCubeMapPositiveX + f)
        // and the order D3D11 numbers its cube slices, so no remapping is needed or wanted.
        if (spec.Cubemap is { } cm) r.SetSkyCubemap(cm.Faces, cm.FaceSize);
        else if (spec.Equirect is { } eq) r.SetSkyEquirect(eq.Rgba, eq.Width, eq.Height);
        else if (spec.MeshPositions is { } mp && spec.MeshIndices is { } mi)
            r.SetSkyMesh(mp, spec.MeshUvs ?? Array.Empty<float>(), mi,
                spec.MeshTexture?.Rgba, spec.MeshTexture?.Width ?? 0, spec.MeshTexture?.Height ?? 0);
        else r.ClearSky();
    }

    // ---- M81: About + updates ----
    private void OnShowAbout(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => new AboutWindow().ShowDialog(this);

    private async void OnCheckUpdates(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var r = await ReyEngine.App.Services.UpdateService.CheckAsync();
        if (DataContext is not MainWindowViewModel vm) return;
        if (!r.Success)
            await PromptWindow.ConfirmAsync(this, "Check for Updates",
                $"Could not check for updates.\n\n{r.Error}\n\n(If no GitHub release is published yet, this is expected.)", "OK");
        else if (r.UpdateAvailable)
        {
            if (await PromptWindow.ConfirmAsync(this, "Update Available",
                $"A newer version is available: {r.LatestVersion}\nYou have {AppInfo.DisplayVersion}.\n\nOpen the download page?", "Open"))
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(r.ReleaseUrl!) { UseShellExecute = true }); } catch { }
        }
        else
            await PromptWindow.ConfirmAsync(this, "Check for Updates",
                $"You're up to date ({AppInfo.DisplayVersion}).", "OK");
    }

    /// <summary>Silent startup update check: only speaks up when a newer release exists.</summary>
    private async System.Threading.Tasks.Task AutoCheckUpdatesAsync()
    {
        await System.Threading.Tasks.Task.Delay(3000);   // let the app settle first
        var r = await ReyEngine.App.Services.UpdateService.CheckAsync();
        if (r is { Success: true, UpdateAvailable: true }
            && await PromptWindow.ConfirmAsync(this, "Update Available",
                $"ReyEngine {r.LatestVersion} is available (you have {AppInfo.DisplayVersion}).\n\nOpen the download page?", "Open"))
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(r.ReleaseUrl!) { UseShellExecute = true }); } catch { }
    }

    /// <summary>M39 custom title bar: drag to move, double-click to maximize/restore — but ONLY from
    /// non-interactive header space. Clicks that originate inside the menu (or any button) must reach it,
    /// so bail if the press came from an interactive child.</summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // Only real interactive controls swallow the drag — a MenuItem (opens its menu) or a Button.
        // The Menu container's own transparent fill (the wide empty stretch of the bar) stays draggable.
        if (e.Source is Avalonia.Visual v)
        {
            foreach (var a in Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(v))
                if (a is MenuItem or Button) return;
            if (v is MenuItem or Button) return;
        }
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        BeginMoveDrag(e);
    }

    /// <summary>
    /// M578: the window's own caption buttons.
    ///
    /// <para>Avalonia 12 paints a title bar of its own into the extended client area - window title at the
    /// left, buttons at the right - which landed on top of the brand and under the settings gear. There is
    /// no "buttons but no title" setting, so WindowDecorations is None and these are ours. The glyph on the
    /// middle one follows the state, which is the whole reason it is not a static bit of markup.</para>
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty) SyncCaptionButtons();
    }

    private void OnMinimiseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnMaximiseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    /// <summary>Keep the maximise glyph and its tip honest about what the click will do.</summary>
    private void SyncCaptionButtons()
    {
        if (MaximiseButton is null) return;
        bool max = WindowState == WindowState.Maximized;
        MaximiseButton.Content = max ? "❐" : "☐";   // overlapping squares = restore, single = maximise
        ToolTip.SetTip(MaximiseButton, max ? "Restore" : "Maximise");
    }

    /// <summary>Load the logo (copied next to the exe) for the titlebar icon + the menu-bar wordmark.</summary>
    private void LoadBranding()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "reyengine_logo.png");
            if (!File.Exists(path)) return;
            var bmp = new Bitmap(path);
            // M87: prefer the multi-resolution .ico for the window/taskbar icon (crisper at 16–32 px);
            // fall back to the PNG bitmap. The wordmark image always uses the PNG.
            var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "reyengine.ico");
            Icon = File.Exists(icoPath) ? new WindowIcon(icoPath) : new WindowIcon(bmp);
            if (this.FindControl<Image>("LogoImage") is { } img) img.Source = bmp;
        }
        catch { /* branding is cosmetic — never block startup */ }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        SyncCaptionButtons();
        if (DataContext is MainWindowViewModel vm)
        {
            vm.Dialogs.Owner = this;
            vm.PromptOwner = this;   // M74: rename/delete prompts
            vm.CinematicHost = this; // M607: the panel needs the surface and the camera, which live here
            vm.RequestProjectSettings += () => ShowProjectSettings(vm);
            vm.RequestSettings += () => ShowSettings(vm);
            vm.RequestNewProject += () => ShowNewProject(vm);   // M73: template wizard
            vm.ShowParticleEditorWindow = () => ShowParticleEditor(vm);   // M46
            vm.ShowMapBinEditorWindow = () => ShowMapBinEditor(vm);       // M98
            vm.ShowMeshPreviewWindow = () => ShowMeshPreview(vm);         // M50
            vm.ShowAddMeshWindow = ShowAddMesh;                           // M123
            vm.ShowWorkshopWindow = ShowWorkshop;
            vm.ShowLightBakeWindow = () => ShowLightBake(vm);             // M158
            vm.ShowTextureImportWindow = async ivm =>                     // M392
            {
                var w = new TextureImportWindow { DataContext = ivm };
                return await w.ShowDialog<TextureImportResult>(this);
            };
            // M386: the view owns the D3D11 surface, so the grass-tint swap is routed through here.
            // Guarded on HasScene: with no committed scene there are no materials to rebind, and the
            // next Prepare will pick the tint up from the view-model anyway.
            // M397: one timer drives the crossfade for BOTH viewports. It starts when a fade begins and
            // stops the moment it lands, so a settled editor is not pumping frames for nothing.
            vm.GrassTransitionStarted = StartGrassTransitionTimer;
            vm.Dx11RebindGrassTintPair = (fromPath, fromTex, toPath, toTex) =>
                _dx11 is { HasScene: true } d
                    ? ReyEngine.App.Services.Dx11SceneBuilder.RebindGrassTintPair(d.Renderer,
                        fromPath, fromTex.Rgba, fromTex.Width, fromTex.Height,
                        toPath, toTex.Rgba, toTex.Width, toTex.Height)
                    : 0;
            vm.ShowLightingWindow = () => ShowLighting(vm);               // M169
            vm.ShowTextureRecolorWindow = () => ShowTextureRecolor(vm);   // M171
            vm.ShowUvEditorWindow = () => ShowUvEditor(vm);               // M492
            vm.ShowRitobinEditorWindow = target => ShowRitobinEditor(target);  // M498
            vm.ShowMaterialBrowserWindow = ctx => ShowMaterialBrowser(ctx);    // M503b
            vm.PushTextureRegion = Viewport.QueueTextureUpdate;            // M172c: live brush strokes
            // M360: the same stroke to the D3D11 viewport, which the paint path never reached. Gated on the
            // surface being up: with DX11 off there is no device and nothing to update, and the pool lookup
            // would miss anyway. Painted keys are lower-cased to match how the scene builder pools them.
            vm.PushPaintedTextureByPath = (path, img) =>
            {
                if (_dx11?.IsReady != true || !vm.UseDx11Viewport) return;
                if (_dx11.Renderer.UpdatePooledTexture(path.ToLowerInvariant(), img.Rgba, img.Width, img.Height))
                    _dx11PaintedKeys.Add(path.ToLowerInvariant());
                QueueDx11Frame();
            };
            // M172e: brush footprint. M361: fed to BOTH viewports from ONE tessellation
            // (ViewportMeshRenderer.BuildBrushRing), so the ring cannot read one size in one viewport and
            // a different size in the other - which a second implementation would eventually do.
            vm.ShowBrushRing = (center, normal, radius, hardness) =>
            {
                Viewport.SetBrushRing(center, normal, radius, hardness);
                if (_dx11?.IsReady != true) return;
                _dx11.Renderer.SetBrushRingLines(
                    ReyEngine.Rendering.ViewportMeshRenderer.BuildBrushRing(center, normal, radius, hardness));
                QueueDx11Frame();
            };
            var glMipRebuild = Viewport.RequestMipRebuild;
            vm.RebuildTextureMips = () =>
            {
                glMipRebuild();
                // M360: mips are throttled mid-stroke in both renderers, so the finished result has to be
                // rebuilt for the textures this stroke actually touched - not the whole pool.
                if (_dx11?.IsReady == true)
                    foreach (var k in _dx11PaintedKeys) _dx11.Renderer.RegeneratePooledMips(k);
                _dx11PaintedKeys.Clear();
                QueueDx11Frame();
            };
            Viewport.CameraMoved += pos => vm.UpdateAmbience(pos);        // M56: positional map audio
            ApplyEditorSettings(vm.Settings);   // M40: apply saved keybinds + camera feel at startup
            WireBrowserDragDrop();   // M74: Explorer-style drag & drop

            // M83: breadcrumb behaves like Explorer's path bar — on navigation, scroll to the END so the
            // current folder is visible (the bar is hidden; it used to overlay and cover the whole path).
            vm.ContentBrowser.Breadcrumbs.CollectionChanged += (_, _) =>
                Dispatcher.UIThread.Post(() =>
                    BreadcrumbScroll.Offset = new Avalonia.Vector(double.MaxValue, 0),
                    DispatcherPriority.Loaded);
            BreadcrumbScroll.PointerWheelChanged += (_, e) =>
            {
                BreadcrumbScroll.Offset = new Avalonia.Vector(
                    Math.Max(0, BreadcrumbScroll.Offset.X - e.Delta.Y * 40), 0);
                e.Handled = true;
            };

            // M93: first launch — walk the user through hashes / audio decoder / preview map.
            if (!vm.Settings.FirstRunCompleted)
                Dispatcher.UIThread.Post(() => ShowSetupWizard(vm), DispatcherPriority.Background);
        }
    }

    // ---- M93: first-run setup wizard ----
    private void OnShowSetupWizard(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) ShowSetupWizard(vm);
    }

    private async void ShowSetupWizard(MainWindowViewModel vm)
    {
        var win = new FirstRunWindow { DataContext = new FirstRunViewModel(vm) };
        await win.ShowDialog(this);
        // closing the window any way counts as done — the wizard must not nag on every launch
        if (!vm.Settings.FirstRunCompleted) { vm.Settings.FirstRunCompleted = true; vm.Settings.Save(); }
    }

    // ---- M74: Content Browser drag & drop --------------------------------
    private AssetNodeViewModel? _dragCandidate;
    private Point _dragStartPos;

    /// <summary>M576 (Avalonia 12): DoDragDropAsync starts from the PRESS, not from the move that crossed
    /// the click slop, so the press args have to be kept until the drag actually begins.</summary>
    private PointerPressedEventArgs? _dragPress;

    private void WireBrowserDragDrop()
    {
        // Internal drag sources: tunnel handlers on the tile grid + list (buttons swallow bubbled events).
        foreach (var name in new[] { "BrowserGrid", "BrowserList" })
            if (this.FindControl<ItemsControl>(name) is { } items)
            {
                items.AddHandler(PointerPressedEvent, OnBrowserItemPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
                items.AddHandler(PointerMovedEvent, OnBrowserItemPointerMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);
                items.AddHandler(PointerReleasedEvent, OnBrowserItemPointerReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);
                // M100: opening is a DOUBLE click now — single click only selects.
                items.AddHandler(DoubleTappedEvent, OnBrowserItemDoubleTapped,
                    Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble);
            }
        // Drop targets: the folder tree (move/import into a specific folder) + the items panel (current folder).
        foreach (var name in new[] { "BrowserFolderTree", "BrowserItemsPanel" })
            if (this.FindControl<Control>(name) is { } target)
            {
                target.AddHandler(DragDrop.DragOverEvent, OnBrowserDragOver);
                target.AddHandler(DragDrop.DropEvent, OnBrowserDrop);
            }
    }

    /// <summary>M100: Explorer-style selection. Plain click selects, Ctrl toggles, Shift extends from
    /// the anchor; clicking empty space clears. A click on an already-selected item keeps the whole
    /// selection so it can be dragged (it collapses to that one item on release, see below).</summary>
    private void OnBrowserItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var props = e.GetCurrentPoint(this).Properties;
        var node = FindNodeFromEvent(e.Source);
        _collapseOnRelease = null;

        if (props.IsRightButtonPressed)
        {
            // Right-click keeps an existing multi-selection when the item is part of it, so the
            // context menu acts on everything highlighted.
            if (node is not null) vm.ContentBrowser.SelectForContextMenu(node);
            _dragCandidate = null;
            return;
        }
        if (!props.IsLeftButtonPressed) { _dragCandidate = null; return; }
        if (node is null) { vm.ContentBrowser.ClearSelection(); _dragCandidate = null; return; }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) vm.ContentBrowser.ToggleSelection(node);
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) vm.ContentBrowser.SelectRange(node);
        else if (!node.IsSelected) vm.ContentBrowser.SelectOnly(node);
        else _collapseOnRelease = node;

        _dragCandidate = node;
        _dragPress = e;
        _dragStartPos = e.GetPosition(this);
    }

    /// <summary>Item that was already selected when pressed — a plain click that didn't turn into a
    /// drag narrows the selection down to it (Explorer behaviour).</summary>
    private AssetNodeViewModel? _collapseOnRelease;

    private void OnBrowserItemPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_collapseOnRelease is { } node && DataContext is MainWindowViewModel vm
            && !e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            vm.ContentBrowser.SelectOnly(node);
        _collapseOnRelease = null;
        _dragCandidate = null;
        _dragPress = null;
    }

    private void OnBrowserItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (FindNodeFromEvent(e.Source) is { } node)
        {
            vm.ContentBrowser.Activate(node);
            e.Handled = true;
        }
    }

    private async void OnBrowserItemPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragCandidate is not { IsFolder: false, Entry: not null } node) return;
        if (_dragPress is not { } press) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragStartPos.X) + Math.Abs(p.Y - _dragStartPos.Y) < 6) return;   // click slop
        _dragCandidate = null;
        _dragPress = null;
        _collapseOnRelease = null;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(AssetDragFormat, node));
        await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Move);
    }

    private void OnBrowserDragOver(object? sender, DragEventArgs e)
    {
        bool internalAsset = e.DataTransfer.Contains(AssetDragFormat);
        bool externalFiles = e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = internalAsset ? DragDropEffects.Move
            : externalFiles ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnBrowserDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        // Target folder: the folder node under the cursor (tree item or a folder tile), else the current folder.
        var target = FindNodeFromEvent(e.Source) is { IsFolder: true } folder ? folder : vm.ContentBrowser.CurrentFolder;
        if (target is null) return;

        if (e.DataTransfer.TryGetValue(AssetDragFormat) is { } item)
        {
            // M100: dragging one item of a multi-selection moves the whole selection.
            var batch = vm.ContentBrowser.SelectedItems.Contains(item)
                ? vm.ContentBrowser.SelectedItems.ToList()
                : new List<AssetNodeViewModel> { item };
            foreach (var n in batch) vm.MoveAssetToFolder(n, target);
            e.Handled = true;
        }
        else if (e.DataTransfer.TryGetFiles() is { } storageItems)
        {
            var paths = new List<string>();
            foreach (var si in storageItems)
                if (si.TryGetLocalPath() is { } lp) paths.Add(lp);
            if (paths.Count > 0) { vm.ImportExternalFiles(paths, target); e.Handled = true; }
        }
    }

    /// <summary>Resolve the AssetNodeViewModel behind whatever visual the pointer event hit.</summary>
    private static AssetNodeViewModel? FindNodeFromEvent(object? source)
    {
        if (source is not Avalonia.Visual v) return null;
        if (v is StyledElement { DataContext: AssetNodeViewModel direct }) return direct;
        foreach (var a in Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(v))
            if (a is StyledElement { DataContext: AssetNodeViewModel node }) return node;
        return null;
    }

    // M123: Add Mesh import + setup window (modal-ish: one at a time).
    private AddMeshWindow? _addMeshWindow;
    private void ShowAddMesh(ViewModels.AddMeshWindowViewModel vm)
    {
        vm.Cancelled = () => _addMeshWindow?.Close();
        var confirmed = vm.Confirmed;
        vm.Confirmed = plan => { confirmed?.Invoke(plan); _addMeshWindow?.Close(); };
        _addMeshWindow = new AddMeshWindow { DataContext = vm };
        _addMeshWindow.Closed += (_, _) => _addMeshWindow = null;
        _addMeshWindow.Show(this);
    }

    private WorkshopWindow? _workshopWindow;
    private void ShowWorkshop(ViewModels.WorkshopViewModel vm)
    {
        _workshopWindow?.Close();
        _workshopWindow = new WorkshopWindow { DataContext = vm };
        _workshopWindow.Closed += (_, _) => _workshopWindow = null;
        _workshopWindow.Show(this);
    }

    // M50: the model preview lives in its own (non-modal) window; reuse one instance while open.
    private MeshPreviewWindow? _meshPreviewWindow;
    private void ShowMeshPreview(MainWindowViewModel vm)
    {
        if (_meshPreviewWindow is null)
        {
            _meshPreviewWindow = new MeshPreviewWindow { DataContext = vm.MeshPreview };
            _meshPreviewWindow.Closed += (_, _) =>
            {
                _meshPreviewWindow = null;
                vm.OnPreviewWindowClosed();   // M120/M121: stop sounds/animation AND close its tabs
            };
            _meshPreviewWindow.Show(this);
        }
        else _meshPreviewWindow.Activate();
    }

    // M46: the Particle Editor lives in its own (non-modal) window; reuse one instance while open.
    private ParticleEditorWindow? _particleEditorWindow;
    private void ShowParticleEditor(MainWindowViewModel vm)
    {
        if (_particleEditorWindow is null)
        {
            _particleEditorWindow = new ParticleEditorWindow { DataContext = vm.ParticleEditor };
            _particleEditorWindow.Closed += (_, _) => _particleEditorWindow = null;
            _particleEditorWindow.Show(this);
        }
        else _particleEditorWindow.Activate();
    }

    // M98: Map Bin Editor window (right-click a .bin ▸ Open in Map Bin Editor)
    private MapBinEditorWindow? _mapBinEditorWindow;
    private void ShowMapBinEditor(MainWindowViewModel vm)
    {
        if (_mapBinEditorWindow is null)
        {
            _mapBinEditorWindow = new MapBinEditorWindow { DataContext = vm.MapBinEditor };
            _mapBinEditorWindow.Closed += (_, _) => _mapBinEditorWindow = null;
            _mapBinEditorWindow.Show(this);
        }
        else _mapBinEditorWindow.Activate();
    }

    // M169: the Lighting window is non-modal and edits live viewport state, so it stays open while you
    // fly the camera around. Reuse one instance; its DataContext IS the main view model.
    private LightingWindow? _lightingWindow;
    private void ShowLighting(MainWindowViewModel vm)
    {
        if (_lightingWindow is null)
        {
            _lightingWindow = new LightingWindow { DataContext = vm };
            _lightingWindow.Closed += (_, _) => _lightingWindow = null;
            _lightingWindow.Show(this);
        }
        else _lightingWindow.Activate();
    }

    // M503b: the map-wide material browser. One instance, reloaded on each open and on Reload, so the
    // triage list never shows a state the map has already moved past.
    private MaterialBrowserWindow? _materialBrowserWindow;
    private void ShowMaterialBrowser(MaterialBrowserContext context)
    {
        if (_materialBrowserWindow is null)
        {
            var vm = new MaterialBrowserViewModel();
            vm.Load(context);
            _materialBrowserWindow = new MaterialBrowserWindow { DataContext = vm };
            _materialBrowserWindow.Closed += (_, _) => _materialBrowserWindow = null;
            _materialBrowserWindow.Show(this);
        }
        else
        {
            if (_materialBrowserWindow.DataContext is MaterialBrowserViewModel vm) vm.Load(context);
            _materialBrowserWindow.Activate();
        }
    }

    // M498: bin-as-ritobin-text. One instance, retargeted on each open so opening a second bin replaces
    // what is shown rather than stacking windows the user then has to tell apart.
    private RitobinEditorWindow? _ritobinWindow;
    private void ShowRitobinEditor(RitobinTarget target)
    {
        if (_ritobinWindow is null)
        {
            var vm = new RitobinEditorViewModel();
            _ritobinWindow = new RitobinEditorWindow { DataContext = vm };
            _ritobinWindow.Closed += (_, _) => _ritobinWindow = null;
            vm.Open(target);
            _ritobinWindow.Show(this);
        }
        else
        {
            if (_ritobinWindow.DataContext is RitobinEditorViewModel vm) vm.Open(target);
            _ritobinWindow.Activate();
        }
    }

    // M492: Second UV (Texcoord7) — non-modal, one instance. Reloaded on re-open so it follows whatever
    // map and selection are current, the same way the recolor window does.
    private UvEditorWindow? _uvEditorWindow;
    private void ShowUvEditor(MainWindowViewModel vm)
    {
        if (_uvEditorWindow is null)
        {
            var uvVm = new UvEditorViewModel(vm.GatherUvEditorContext, vm.SaveUvEditorResultAsync);
            _uvEditorWindow = new UvEditorWindow { DataContext = uvVm };
            _uvEditorWindow.Closed += (_, _) => _uvEditorWindow = null;
            _uvEditorWindow.Show(this);
        }
        else
        {
            if (_uvEditorWindow.DataContext is UvEditorViewModel uvm) uvm.Refresh();
            _uvEditorWindow.Activate();
        }
    }

    // M171: Recolor Textures — non-modal, one instance, and the list is re-read on each open so it
    // always reflects whatever map is currently loaded.
    private TextureRecolorWindow? _recolorWindow;
    private void ShowTextureRecolor(MainWindowViewModel vm)
    {
        if (_recolorWindow is null)
        {
            var recolorVm = new TextureRecolorViewModel(
                vm.GatherRecolorTargets, vm.ReadRecolorBase, vm.MakeRecolorService,
                vm.PersistRecolors, vm.RevertRecolors, r => vm.OnRecolorFinished(r),
                vm.GetRecolorSourceWarning,
                () => vm.Dialogs.OpenFileAsync("Load a .cube colour grade",
                    new Avalonia.Platform.Storage.FilePickerFileType("Colour lookup table")
                    { Patterns = new[] { "*.cube", "*.CUBE" } }));
            _recolorWindow = new TextureRecolorWindow { DataContext = recolorVm };
            _recolorWindow.Closed += (_, _) => _recolorWindow = null;
            _recolorWindow.Show(this);
        }
        else
        {
            if (_recolorWindow.DataContext is TextureRecolorViewModel rvm) _ = rvm.RefreshAsync();
            _recolorWindow.Activate();
        }
    }

    // M158: the Light Baking window is non-modal (a bake can take minutes) — reuse one instance.
    private LightBakeWindow? _lightBakeWindow;
    private void ShowLightBake(MainWindowViewModel vm)
    {
        if (_lightBakeWindow is null)
        {
            var bakeVm = new LightBakeViewModel(vm.GatherBakeInputs, vm.MakeBakeService, vm.OnLightBakeFinished,
                vm.GenerateLightmapLayoutAsync,
                () => (vm.HasMapForLayout, vm.MeshesWithoutLightmapUv, vm.MapMeshCountForLayout),
                vm.EnableExperimentalLightmapShadersAsync);
            _lightBakeWindow = new LightBakeWindow { DataContext = bakeVm };
            _lightBakeWindow.Closed += (_, _) => _lightBakeWindow = null;
            _lightBakeWindow.Show(this);
        }
        else _lightBakeWindow.Activate();
    }

    private async void ShowProjectSettings(MainWindowViewModel vm)
    {
        var settings = new ProjectSettingsViewModel(vm.Project, vm.Dialogs);
        var win = new ProjectSettingsWindow { DataContext = settings };
        settings.CloseRequested += () => win.Close();
        await win.ShowDialog(this);
        if (settings.Saved) vm.ApplyProjectSettings(settings);
    }

    /// <summary>M73: template-based New Project wizard; on success the created project opens directly.</summary>
    private async void ShowNewProject(MainWindowViewModel vm)
    {
        var wizard = new NewProjectViewModel(vm.PathResolver) { Location = vm.ProjectsFolder };   // M133
        var win = new NewProjectWindow { DataContext = wizard };
        wizard.CloseRequested += () => win.Close();
        await win.ShowDialog(this);
        if (wizard.Created && wizard.CreatedRoot is { } root)
            vm.OpenRecentProjectCommand.Execute(root);
    }

    private async void ShowSettings(MainWindowViewModel vm)
    {
        var settings = new SettingsViewModel(vm.Settings.Clone());
        var win = new SettingsWindow { DataContext = settings };
        settings.CloseRequested += () => win.Close();
        await win.ShowDialog(this);
        if (settings.Saved)
        {
            vm.ApplyEditorSettings(settings);
            ApplyEditorSettings(vm.Settings);
        }
        else
        {
            // M72: window closed without saving (Cancel or the OS close button) — undo any live theme preview.
            ReyEngine.App.Services.ThemeService.Apply(vm.Settings.Theme);
        }
    }

    // ---- M40: parsed viewport keybinds + camera feel, refreshed from EditorSettings ----
    private Key _kFwd = Key.W, _kBack = Key.S, _kLeft = Key.A, _kRight = Key.D, _kUp = Key.E, _kDown = Key.Q, _kFocus = Key.F;

    private void ApplyEditorSettings(ReyEngine.Core.Settings.EditorSettings s)
    {
        static Key P(string name, Key fallback) => System.Enum.TryParse<Key>(name, out var k) ? k : fallback;
        _kFwd = P(s.FlyForward, Key.W); _kBack = P(s.FlyBack, Key.S);
        _kLeft = P(s.FlyLeft, Key.A); _kRight = P(s.FlyRight, Key.D);
        _kUp = P(s.FlyUp, Key.E); _kDown = P(s.FlyDown, Key.Q);
        _kFocus = P(s.FocusSelected, Key.F);
        Viewport.ApplyCameraSettings((float)s.MouseLookSensitivity, (float)s.OrbitSensitivity,
            (float)s.PanSensitivity, (float)s.ZoomSensitivity, s.InvertLookY, (float)s.FlySpeed);
    }

    // ---- Unreal-style viewport camera input (forwarded from the transparent overlay) ----
    // LMB = mouse-look + WASD/QE fly · Alt+LMB = orbit · MMB = pan · wheel = dolly (LMB+wheel = fly speed)
    // F = focus selected. (Look is direct: cursor up→look up, left→look left.)
    // When a map mesh is selected, LMB-down first hit-tests the translate gizmo (X/Y/Z axis handles at
    // its pivot); a hit starts an axis-constrained drag instead of camera-look/fly for that stroke.

    private static Vector3 AxisUnitVector(ViewportControl.GizmoAxis axis) => axis switch
    {
        ViewportControl.GizmoAxis.X => Vector3.UnitX,
        ViewportControl.GizmoAxis.Y => Vector3.UnitY,
        _ => Vector3.UnitZ,
    };

    private static float ComponentOf(Vector3 v, int comp) => comp == 0 ? v.X : comp == 1 ? v.Y : v.Z;
    private static Vector3 WithComponent(Vector3 v, int comp, float value) =>
        comp == 0 ? new Vector3(value, v.Y, v.Z) : comp == 1 ? new Vector3(v.X, value, v.Z) : new Vector3(v.X, v.Y, value);

    private void OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pt = e.GetCurrentPoint(ViewportInput);
        _lmb = pt.Properties.IsLeftButtonPressed;
        _rmb = pt.Properties.IsRightButtonPressed;
        _mmb = pt.Properties.IsMiddleButtonPressed;
        _alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        _lastPointer = pt.Position;
        _pressPos = pt.Position;
        _pressMoved = false;
        _pressClickCount = e.ClickCount;   // M568: only the PRESS carries it; the pick happens on release
        e.Pointer.Capture(ViewportInput);
        ViewportInput.Focus(); // so WASD/F reach the viewport

        // M172c: paint mode owns the left drag. Checked before the gizmo and before StartFly, because in
        // paint mode a left-drag is a brush stroke, not a camera move or a handle grab.
        if (_lmb && !_alt && DataContext is MainWindowViewModel pvm && pvm.IsPaintMode
            && Viewport.TryGetPickRay(pt.Position, out var pOrigin, out var pDir))
        {
            _painting = true;
            pvm.BeginPaintStroke(pOrigin, pDir);
            return;
        }

        if (_lmb && !_alt)
        {
            var axis = Viewport.HitTestGizmoAxis(pt.Position);
            if (axis is { } a && DataContext is MainWindowViewModel vm
                && Viewport.GizmoPivot is { } pivot
                && Viewport.TryGetAxisParameter(a, pt.Position, pivot, out var t0))
            {
                // M567: faces first. In face mode the gizmo belongs to the face selection, and a mesh may
                // well still be selected underneath - falling through would drag the whole object.
                if (vm.FaceEditMode && vm.HasFaceGizmoTarget)
                {
                    _gizmoDragAxis = a;
                    _gizmoDragOrigin = pivot;
                    _gizmoDragStartT = t0;
                    _gizmoDragStartOffset = System.Numerics.Vector3.Zero;   // faces drag from zero, not from a stored offset
                    _gizmoTargetIsPlacement = false;
                    _gizmoTargetIsFaces = true;
                    vm.BeginFaceDrag();
                    return;
                }
                if (vm.SelectedMapMesh is { } mesh)
                {
                    _gizmoDragAxis = a;
                    _gizmoDragOrigin = pivot;   // frozen for the whole drag
                    _gizmoDragStartT = t0;
                    _gizmoDragStartOffset = mesh.Offset;
                    _gizmoTargetIsPlacement = false;
                    _gizmoTargetIsFaces = false;
                    var (rot, scale) = vm.SelectedMeshRotScale;
                    _gizmoStartRotation = rot;
                    _gizmoStartScale = scale;
                    vm.BeginMeshDrag();         // capture the before-state → the whole drag = ONE undo step
                    return; // gizmo drag takes over this stroke — don't also start camera fly
                }
                if (vm.HasPlacementGizmoTarget)   // M75: particles (move/rotate/scale) + sounds (move)
                {
                    _gizmoDragAxis = a;
                    _gizmoDragOrigin = pivot;
                    _gizmoDragStartT = t0;
                    _gizmoTargetIsPlacement = true;
                    var (off, rot, scale) = vm.PlacementDragStart;
                    _gizmoDragStartOffset = off;
                    _gizmoStartRotation = rot;
                    _gizmoStartScale = scale;
                    vm.BeginPlacementDrag();   // M76: capture before-state → whole drag = ONE undo step
                    return;
                }
            }
            StartFly();
        }
    }

    private bool _painting;

    private void OnViewportPointerMoved(object? sender, PointerEventArgs e)
    {
        var p = e.GetPosition(ViewportInput);
        if (Math.Abs(p.X - _pressPos.X) > ClickSlopPixels || Math.Abs(p.Y - _pressPos.Y) > ClickSlopPixels)
            _pressMoved = true;

        if (DataContext is MainWindowViewModel mvm && mvm.IsPaintMode
            && Viewport.TryGetPickRay(p, out var mOrigin, out var mDir))
        {
            if (_painting) { mvm.PaintStrokeMove(mOrigin, mDir); return; }
            mvm.PaintHoverAt(mOrigin, mDir);   // badge: what would a stroke here change?
        }

        if (_gizmoDragAxis is { } axis && DataContext is MainWindowViewModel gvm)
        {
            var axisDir = Viewport.AxisDir(axis);      // world or the mesh's local axis
            int comp = axis == ViewportControl.GizmoAxis.X ? 0 : axis == ViewportControl.GizmoAxis.Y ? 1 : 2;
            switch (gvm.TransformMode)
            {
                case 1: // ROTATE — horizontal drag → degrees about this axis
                {
                    float deg = gvm.ApplyRotateSnap((float)(p.X - _pressPos.X) * 0.5f);
                    var rot = WithComponent(_gizmoStartRotation, comp, ComponentOf(_gizmoStartRotation, comp) + deg);
                    if (_gizmoTargetIsPlacement) gvm.RotateSelectedPlacementTo(rot);   // M75
                    else gvm.RotateSelectedMeshTo(rot);
                    break;
                }
                case 2: // SCALE — drag along the axis arm; ratio to the grab distance scales that axis
                {
                    if (Viewport.TryGetAxisParameter(axis, p, _gizmoDragOrigin, out var t))
                    {
                        float f = MathF.Abs(_gizmoDragStartT) > 1e-3f ? t / _gizmoDragStartT : 1f;
                        f = Math.Clamp(f, 0.05f, 50f);
                        float target = gvm.ApplyScaleSnap(Math.Clamp(ComponentOf(_gizmoStartScale, comp) * f, 0.05f, 50f));
                        var scale = WithComponent(_gizmoStartScale, comp, target);
                        if (_gizmoTargetIsPlacement) gvm.ScaleSelectedPlacementTo(scale);   // M75
                        else gvm.ScaleSelectedMeshTo(scale);
                    }
                    break;
                }
                default: // MOVE — slide along the FROZEN drag-start axis line (live pivot would re-anchor → oscillate)
                {
                    if (Viewport.TryGetAxisParameter(axis, p, _gizmoDragOrigin, out var t))
                    {
                        float dist = gvm.ApplyMoveSnap(t - _gizmoDragStartT);
                        var target = _gizmoDragStartOffset + axisDir * dist;
                        if (_gizmoTargetIsFaces) gvm.DragSelectedFacesTo(target);           // M567
                        else if (_gizmoTargetIsPlacement) gvm.DragSelectedPlacementTo(target);   // M75
                        else gvm.DragSelectedMeshTo(target);
                    }
                    break;
                }
            }
            _lastPointer = p;
            return;
        }

        if (!(_lmb || _rmb || _mmb)) return;
        var dx = (float)(p.X - _lastPointer.X);
        var dy = (float)(p.Y - _lastPointer.Y);
        _lastPointer = p;

        if (_lmb && _alt) Viewport.OrbitBy(dx, dy);
        else if (_lmb) Viewport.LookBy(dx, dy);
        else if (_mmb) Viewport.PanBy(dx, dy);
        else if (_rmb) Viewport.LookBy(dx, dy);
    }

    private void OnViewportPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_painting)
        {
            _painting = false;
            (DataContext as MainWindowViewModel)?.EndPaintStroke();   // M172c: the whole drag = ONE undo step
            _lmb = _rmb = _mmb = false;
            e.Pointer.Capture(null);
            return;
        }

        bool wasGizmoDrag = _gizmoDragAxis is not null;
        if (wasGizmoDrag)
        {
            _gizmoDragAxis = null;
            if (_gizmoTargetIsFaces) (DataContext as MainWindowViewModel)?.EndFaceDrag();          // M567
            else if (_gizmoTargetIsPlacement) (DataContext as MainWindowViewModel)?.EndPlacementDrag();   // M75
            else (DataContext as MainWindowViewModel)?.EndMeshDrag();
            _gizmoTargetIsPlacement = false;
            _gizmoTargetIsFaces = false;
        }

        bool wasLmb = _lmb;
        var props = e.GetCurrentPoint(ViewportInput).Properties;
        _lmb = props.IsLeftButtonPressed;
        _rmb = props.IsRightButtonPressed;
        _mmb = props.IsMiddleButtonPressed;
        if (!_lmb) StopFly();
        if (!(_lmb || _rmb || _mmb)) e.Pointer.Capture(null);

        // A stationary LMB click (no camera drag, no gizmo drag, no Alt-orbit) = pick a mesh under the
        // cursor, Blender/UE-style. Ctrl adds/removes from the selection; a plain miss clears it.
        if (wasLmb && !_lmb && !wasGizmoDrag && !_pressMoved && !_alt
            && DataContext is MainWindowViewModel vm
            && Viewport.TryGetPickRay(e.GetPosition(ViewportInput), out var origin, out var dir))
        {
            bool additive = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            // M382: the click pixel next to the bounds the pick matrices were cached with. The click is
            // relative to ViewportInput and the matrices come from Viewport.Bounds - two different
            // controls, so if those ever disagree every ray is offset by the difference.
            var diagPos = e.GetPosition(ViewportInput);
            vm.LogPickClick(diagPos.X, diagPos.Y, Viewport.Bounds.Width, Viewport.Bounds.Height);
            // M76: UE-style screen-space icon picking — pass a projector + the click pixel so placeable
            // icons are clickable at any zoom (18px tolerance), not just via a ray-vs-world-sphere hit.
            var clickPos = e.GetPosition(ViewportInput);
            vm.SelectAnyFromViewport(origin, dir, additive,
                world => Viewport.TryProjectToScreen(world, out var s) ? s : null,
                new System.Numerics.Vector2((float)clickPos.X, (float)clickPos.Y),
                // M568: the count is captured on PRESS - the release event does not carry it - and the
                // selection happens on release so a camera drag is not mistaken for a click.
                doubleClick: _pressClickCount >= 2);
        }
    }


    private void OnViewportPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_lmb) Viewport.AdjustFlySpeed((float)e.Delta.Y);
        else Viewport.ZoomBy((float)e.Delta.Y);
    }

    private void OnViewportKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == _kFocus) { Viewport.FocusSelected(); return; }
        _heldKeys.Add(e.Key);
    }

    private void OnViewportKeyUp(object? sender, KeyEventArgs e) => _heldKeys.Remove(e.Key);

    private void StartFly()
    {
        if (_flyTimer is null)
        {
            _flyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _flyTimer.Tick += FlyTick;
        }
        _flyTimer.Start();
    }

    private void StopFly()
    {
        _flyTimer?.Stop();
        _heldKeys.Clear();
    }

    private void FlyTick(object? sender, EventArgs e)
    {
        if (!_lmb) { StopFly(); return; }
        float f = 0, r = 0, u = 0;
        if (_heldKeys.Contains(_kFwd)) f += 1;
        if (_heldKeys.Contains(_kBack)) f -= 1;
        if (_heldKeys.Contains(_kRight)) r += 1;
        if (_heldKeys.Contains(_kLeft)) r -= 1;
        if (_heldKeys.Contains(_kUp)) u += 1;
        if (_heldKeys.Contains(_kDown)) u -= 1;
        if (f != 0 || r != 0 || u != 0) Viewport.FlyBy(f, r, u, 0.016f);
    }

    private void OnFrameClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Viewport.RequestFrame();
    }

    /// <summary>Global editor shortcuts. TextBoxes keep their own local undo and clipboard: when one has
    /// focus its unhandled shortcuts must not fire the editor stack, or typing in a name field would
    /// delete the map objects behind it.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Source is TextBox) return;
        if (DataContext is not MainWindowViewModel vm) return;

        // M566: in face mode the face keys win. Delete means "delete what is selected", and what is
        // selected is a set of faces - falling through to the mesh delete would remove the whole object.
        if (vm.FaceEditMode && e.KeyModifiers == KeyModifiers.None)
        {
            if (e.Key == Key.Delete && vm.DeleteSelectedFacesCommand.CanExecute(null))
            { vm.DeleteSelectedFacesCommand.Execute(null); e.Handled = true; return; }
            if (e.Key == Key.F && vm.FlipSelectedFacesCommand.CanExecute(null))
            { vm.FlipSelectedFacesCommand.Execute(null); e.Handled = true; return; }
            if (e.Key == Key.Escape && vm.ClearFaceSelectionCommand.CanExecute(null))
            { vm.ClearFaceSelectionCommand.Execute(null); e.Handled = true; return; }
        }

        // M553: Delete needs no modifier, and shares the outliner's X so the two cannot diverge.
        if (e.Key is Key.Delete && e.KeyModifiers == KeyModifiers.None)
        {
            if (vm.DeleteMapContentSelectionCommand.CanExecute(null))
            { vm.DeleteMapContentSelectionCommand.Execute(null); e.Handled = true; }
            return;
        }
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (e.Key == Key.Z && !shift) { vm.UndoCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Y || (e.Key == Key.Z && shift)) { vm.RedoCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.OemComma) { vm.OpenSettingsCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.C) { Run(vm.CopyMapContentSelectionCommand, e); }
        else if (e.Key == Key.X) { Run(vm.CutMapContentSelectionCommand, e); }
        else if (e.Key == Key.V) { Run(vm.PasteMapContentCommand, e); }
        else if (e.Key == Key.D) { Run(vm.CopyMapContentSelectionCommand, e); Run(vm.PasteMapContentCommand, e); }

        // Only mark the key handled when the command could actually run, so Ctrl+C over a list or a
        // read-only field still reaches whatever else wants it.
        static void Run(System.Windows.Input.ICommand command, KeyEventArgs e)
        {
            if (!command.CanExecute(null)) return;
            command.Execute(null);
            e.Handled = true;
        }
    }
}
