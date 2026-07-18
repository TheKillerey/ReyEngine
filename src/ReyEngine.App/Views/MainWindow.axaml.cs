using System.Collections.Generic;
using System.IO;
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

    public MainWindow()
    {
        InitializeComponent();
        LoadBranding();
        TitleVersionText.Text = AppInfo.DisplayVersion;   // M81
        _ = AutoCheckUpdatesAsync();                      // M81: silent startup check
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
            vm.ShowMeshPreviewWindow = () => ShowMeshPreview(vm);         // M50
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
        }
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
                items.AddHandler(PointerReleasedEvent, (_, _) => _dragCandidate = null, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            }
        // Drop targets: the folder tree (move/import into a specific folder) + the items panel (current folder).
        foreach (var name in new[] { "BrowserFolderTree", "BrowserItemsPanel" })
            if (this.FindControl<Control>(name) is { } target)
            {
                target.AddHandler(DragDrop.DragOverEvent, OnBrowserDragOver);
                target.AddHandler(DragDrop.DropEvent, OnBrowserDrop);
            }
    }

    private void OnBrowserItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _dragCandidate = null; return; }
        _dragCandidate = FindNodeFromEvent(e.Source);
        _dragStartPos = e.GetPosition(this);
    }

    private async void OnBrowserItemPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragCandidate is not { IsFolder: false, Entry: not null } node) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragStartPos.X) + Math.Abs(p.Y - _dragStartPos.Y) < 6) return;   // click slop
        _dragCandidate = null;
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
            vm.MoveAssetToFolder(item, target);
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

    // M50: the model preview lives in its own (non-modal) window; reuse one instance while open.
    private MeshPreviewWindow? _meshPreviewWindow;
    private void ShowMeshPreview(MainWindowViewModel vm)
    {
        if (_meshPreviewWindow is null)
        {
            _meshPreviewWindow = new MeshPreviewWindow { DataContext = vm.MeshPreview };
            _meshPreviewWindow.Closed += (_, _) => _meshPreviewWindow = null;
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
        var wizard = new NewProjectViewModel(vm.PathResolver);
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

    private void OnViewportPointerMoved(object? sender, PointerEventArgs e)
    {
        var p = e.GetPosition(ViewportInput);
        if (Math.Abs(p.X - _pressPos.X) > ClickSlopPixels || Math.Abs(p.Y - _pressPos.Y) > ClickSlopPixels)
            _pressMoved = true;

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
