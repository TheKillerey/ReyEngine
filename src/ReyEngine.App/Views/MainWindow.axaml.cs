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

public partial class MainWindow : Window
{
    private Point _lastPointer;
    private bool _lmb, _rmb, _mmb, _alt;
    private readonly HashSet<Key> _heldKeys = new();
    private DispatcherTimer? _flyTimer;

    // Translate-gizmo drag state (mutually exclusive with camera fly for the same LMB stroke).
    private ViewportControl.GizmoAxis? _gizmoDragAxis;
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
    private float[]? _lastDx11BucketGrid;
    private bool _dx11FrameQueued;
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
                    // M268: and rebuild when the MAP changes underneath a viewport that is already on.
                    // The scene was only ever built on the toggle, so opening a second map left the first
                    // one on screen - stale geometry that looked like the new map had failed to load.
                    else if (e.PropertyName == nameof(MainWindowViewModel.MapGeneration)
                             && vm.UseDx11Viewport) OnDx11Toggled(vm);
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

    /// <summary>Compositor-driven, like the shader preview window - a DispatcherTimer caps the rate and
    /// measured 20 fps there. Only runs while the toggle is on.</summary>
    private void QueueDx11Frame()
    {
        if (_closed || _dx11FrameQueued) return;
        _dx11FrameQueued = true;
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(_ =>
        {
            _dx11FrameQueued = false;
            if (_closed || DataContext is not MainWindowViewModel vm || !vm.UseDx11Viewport) return;
            RenderDx11Frame(vm);
            QueueDx11Frame();
        });
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
        double scale = RenderScaling;
        int w = (int)(Viewport.Bounds.Width * scale);
        int h = (int)(Viewport.Bounds.Height * scale);
        if (w <= 0 || h <= 0) return;

        // M261: the same lighting inputs the GL surface is bound to in XAML. Pushed every frame rather
        // than on load because all three are live-editable - the sun sliders, the fog toggle and the
        // lightmap scale all change without the scene being rebuilt.
        _dx11.MapSun = vm.CurrentSunProperties;
        _dx11.FogEnabled = vm.ShowFog;
        _dx11.LightmapScale = vm.CurrentLightmapScale;
        _dx11.AnimateTime = vm.AnimationsPlaying;
        _dx11.Wireframe = vm.ShowWireframe;
        // M269: pushed every frame rather than on a selection-changed event - the selection, the map and
        // the scene rebuild all move independently, and one of the three going stale is exactly how a
        // highlight ends up pointing at geometry that is no longer there.
        _dx11.Renderer.SetHighlightRanges(vm.Dx11HighlightRanges);
        _dx11.Renderer.SetIcons(vm.Dx11Icons(Viewport.Camera.Distance));
        // M292: dragon / baron / render-region filtering, from the same array the GL viewport binds to.
        // Per frame for the same reason the highlight is: the selection, the layer combos and the scene
        // rebuild all move independently, and a rebuild would otherwise come back with everything visible.
        _dx11.ApplyGroupVisibility(vm.CurrentModelSubmeshVisible);

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
            _dx11.Renderer.SetGizmoLines(
                ReyEngine.Rendering.ViewportMeshRenderer.BuildGizmoAxis(mode, gizmoPivot, ax, arm),
                ReyEngine.Rendering.ViewportMeshRenderer.BuildGizmoAxis(mode, gizmoPivot, ay, arm),
                ReyEngine.Rendering.ViewportMeshRenderer.BuildGizmoAxis(mode, gizmoPivot, az, arm));
        }
        else _dx11.Renderer.SetGizmoLines(null, null, null);

        // M295: props, from the same set the GL viewport binds to. The setter compares by reference, so
        // this is a no-op until the view-model actually republishes the prop set.
        _dx11.PropMeshes = vm.CurrentPropMeshes;
        _dx11.PlayPropAnimations = vm.PlayPropAnimations;

        // M293: the bucket grid, from the same array the GL viewport is bound to. Re-uploaded only when
        // the ARRAY ITSELF changes - it is multi-megabyte, and the GL host guards it the same way for the
        // same reason. Toggling the grid off publishes null, which clears it.
        if (!ReferenceEquals(_lastDx11BucketGrid, vm.BucketGridLines))
        {
            _lastDx11BucketGrid = vm.BucketGridLines;
            _dx11.Renderer.SetBucketGrid(vm.BucketGridLines);
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
        Dx11Surface.Width = Viewport.Bounds.Width;
        Dx11Surface.Height = Viewport.Bounds.Height;

        // M263: the GL control is hidden and not rendering, so nothing else refreshes the matrices that
        // mesh picking raycasts against. Same size the GL path caches - logical bounds, not pixels.
        Viewport.SyncPickMatrices(Viewport.Bounds.Width, Viewport.Bounds.Height);
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
            // No scene is a legitimate state, not a failure - say which, rather than showing an empty
            // viewport and letting it read as a broken renderer.
            : "D3D11 no scene: " + WhyNoScene(_dx11.SceneReport);
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
        if (DataContext is MainWindowViewModel vm)
        {
            vm.Dialogs.Owner = this;
            vm.PromptOwner = this;   // M74: rename/delete prompts
            vm.RequestProjectSettings += () => ShowProjectSettings(vm);
            vm.RequestSettings += () => ShowSettings(vm);
            vm.RequestNewProject += () => ShowNewProject(vm);   // M73: template wizard
            vm.ShowParticleEditorWindow = () => ShowParticleEditor(vm);   // M46
            vm.ShowMapBinEditorWindow = () => ShowMapBinEditor(vm);       // M98
            vm.ShowMeshPreviewWindow = () => ShowMeshPreview(vm);         // M50
            vm.ShowAddMeshWindow = ShowAddMesh;                           // M123
            vm.ShowLightBakeWindow = () => ShowLightBake(vm);             // M158
            vm.ShowLightingWindow = () => ShowLighting(vm);               // M169
            vm.ShowTextureRecolorWindow = () => ShowTextureRecolor(vm);   // M171
            vm.PushTextureRegion = Viewport.QueueTextureUpdate;            // M172c: live brush strokes
            vm.ShowBrushRing = Viewport.SetBrushRing;                      // M172e: brush footprint
            vm.RebuildTextureMips = Viewport.RequestMipRebuild;
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
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragStartPos.X) + Math.Abs(p.Y - _dragStartPos.Y) < 6) return;   // click slop
        _dragCandidate = null;
        _collapseOnRelease = null;
        var data = new DataObject();
        data.Set("rey/asset", node);
        await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
    }

    private void OnBrowserDragOver(object? sender, DragEventArgs e)
    {
        bool internalAsset = e.Data.Contains("rey/asset");
        bool externalFiles = e.Data.Contains(DataFormats.Files);
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

        if (e.Data.Get("rey/asset") is AssetNodeViewModel item)
        {
            // M100: dragging one item of a multi-selection moves the whole selection.
            var batch = vm.ContentBrowser.SelectedItems.Contains(item)
                ? vm.ContentBrowser.SelectedItems.ToList()
                : new List<AssetNodeViewModel> { item };
            foreach (var n in batch) vm.MoveAssetToFolder(n, target);
            e.Handled = true;
        }
        else if (e.Data.GetFiles() is { } storageItems)
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
                () => (vm.HasMapForLayout, vm.MeshesWithoutLightmapUv, vm.MapMeshCountForLayout));
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
                if (vm.SelectedMapMesh is { } mesh)
                {
                    _gizmoDragAxis = a;
                    _gizmoDragOrigin = pivot;   // frozen for the whole drag
                    _gizmoDragStartT = t0;
                    _gizmoDragStartOffset = mesh.Offset;
                    _gizmoTargetIsPlacement = false;
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
                        if (_gizmoTargetIsPlacement) gvm.DragSelectedPlacementTo(target);   // M75
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
            if (_gizmoTargetIsPlacement) (DataContext as MainWindowViewModel)?.EndPlacementDrag();   // M75
            else (DataContext as MainWindowViewModel)?.EndMeshDrag();
            _gizmoTargetIsPlacement = false;
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
            // M76: UE-style screen-space icon picking — pass a projector + the click pixel so placeable
            // icons are clickable at any zoom (18px tolerance), not just via a ray-vs-world-sphere hit.
            var clickPos = e.GetPosition(ViewportInput);
            vm.SelectAnyFromViewport(origin, dir, additive,
                world => Viewport.TryProjectToScreen(world, out var s) ? s : null,
                new System.Numerics.Vector2((float)clickPos.X, (float)clickPos.Y));
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

    /// <summary>Global Ctrl+Z / Ctrl+Y (and Ctrl+Shift+Z). TextBoxes keep their own local undo:
    /// when one has focus its unhandled shortcuts must not fire the global editor stack.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Source is TextBox) return;
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || DataContext is not MainWindowViewModel vm) return;

        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (e.Key == Key.Z && !shift) { vm.UndoCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Y || (e.Key == Key.Z && shift)) { vm.RedoCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.OemComma) { vm.OpenSettingsCommand.Execute(null); e.Handled = true; }
    }
}
