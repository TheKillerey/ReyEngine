using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
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

    // Click-to-select: a press+release with almost no movement is a pick, not a camera drag.
    private Point _pressPos;
    private bool _pressMoved;
    private const double ClickSlopPixels = 4.0;

    public MainWindow()
    {
        InitializeComponent();
        LoadBranding();
    }

    /// <summary>M39 custom title bar: drag to move, double-click to maximize/restore — but ONLY from
    /// non-interactive header space. Clicks that originate inside the menu (or any button) must reach it,
    /// so bail if the press came from an interactive child.</summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Avalonia.Visual v)
        {
            foreach (var a in Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(v))
                if (a is Menu or MenuItem or Button) return;
            if (v is Menu or MenuItem or Button) return;
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
            Icon = new WindowIcon(bmp);
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
            vm.RequestProjectSettings += () => ShowProjectSettings(vm);
            vm.RequestSettings += () => ShowSettings(vm);
            ApplyEditorSettings(vm.Settings);   // M40: apply saved keybinds + camera feel at startup
        }
    }

    private async void ShowProjectSettings(MainWindowViewModel vm)
    {
        var settings = new ProjectSettingsViewModel(vm.Project, vm.Dialogs);
        var win = new ProjectSettingsWindow { DataContext = settings };
        settings.CloseRequested += () => win.Close();
        await win.ShowDialog(this);
        if (settings.Saved) vm.ApplyProjectSettings(settings);
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
            if (axis is { } a && DataContext is MainWindowViewModel { SelectedMapMesh: { } mesh } vm
                && Viewport.GizmoPivot is { } pivot
                && Viewport.TryGetAxisParameter(a, pt.Position, pivot, out var t0))
            {
                _gizmoDragAxis = a;
                _gizmoDragOrigin = pivot;   // frozen for the whole drag
                _gizmoDragStartT = t0;
                _gizmoDragStartOffset = mesh.Offset;
                var (rot, scale) = vm.SelectedMeshRotScale;
                _gizmoStartRotation = rot;
                _gizmoStartScale = scale;
                vm.BeginMeshDrag();         // capture the before-state → the whole drag = ONE undo step
                return; // gizmo drag takes over this stroke — don't also start camera fly
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
                    gvm.RotateSelectedMeshTo(WithComponent(_gizmoStartRotation, comp, ComponentOf(_gizmoStartRotation, comp) + deg));
                    break;
                }
                case 2: // SCALE — drag along the axis arm; ratio to the grab distance scales that axis
                {
                    if (Viewport.TryGetAxisParameter(axis, p, _gizmoDragOrigin, out var t))
                    {
                        float f = MathF.Abs(_gizmoDragStartT) > 1e-3f ? t / _gizmoDragStartT : 1f;
                        f = Math.Clamp(f, 0.05f, 50f);
                        float target = gvm.ApplyScaleSnap(Math.Clamp(ComponentOf(_gizmoStartScale, comp) * f, 0.05f, 50f));
                        gvm.ScaleSelectedMeshTo(WithComponent(_gizmoStartScale, comp, target));
                    }
                    break;
                }
                default: // MOVE — slide along the FROZEN drag-start axis line (live pivot would re-anchor → oscillate)
                {
                    if (Viewport.TryGetAxisParameter(axis, p, _gizmoDragOrigin, out var t))
                    {
                        float dist = gvm.ApplyMoveSnap(t - _gizmoDragStartT);
                        gvm.DragSelectedMeshTo(_gizmoDragStartOffset + axisDir * dist);
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
            (DataContext as MainWindowViewModel)?.EndMeshDrag();
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
            vm.SelectMeshFromViewport(origin, dir, additive);
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
