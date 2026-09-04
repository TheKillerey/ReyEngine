using System.Numerics;
using Avalonia.Controls;
using Avalonia.Input;
using ReyEngine.App.ViewModels;

namespace ReyEngine.App.Views;

/// <summary>M50 model-preview window: forwards pointer input to the embedded viewport camera.
/// M114: when the target dummy is enabled, its translate gizmo captures the left-drag first.</summary>
public partial class MeshPreviewWindow : Window
{
    private bool _lmb, _mmb;
    private Avalonia.Point _last;

    // M114: an active dummy gizmo drag (null = camera input as usual)
    private ViewportControl.GizmoAxis? _dummyAxis;
    private float _dummyStartT;
    private Vector3 _dummyStartPos;

    public MeshPreviewWindow()
    {
        InitializeComponent();
        PreviewInput.PointerPressed += OnPressed;
        PreviewInput.PointerMoved += OnMoved;
        PreviewInput.PointerReleased += OnReleased;
        PreviewInput.PointerWheelChanged += OnWheel;
        // M613: ability keys. Tunnelling because a focused list or text box would otherwise eat them.
        AddHandler(KeyDownEvent, OnControlKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        HookDx11();   // M618: the Direct3D 11 surface, off until the toggle turns it on
        Closed += (_, _) => (DataContext as MeshPreviewViewModel)?.StopControl();
    }

    /// <summary>Q/W/E/R cast, S stops. Only while control mode is on, and never while something is
    /// being typed into — a champion search box would otherwise fire an ability per keystroke.</summary>
    private void OnControlKey(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MeshPreviewViewModel { ControlMode: true } vm) return;
        if (FocusManager?.GetFocusedElement() is TextBox) return;

        int slot = e.Key switch { Key.Q => 0, Key.W => 1, Key.E => 2, Key.R => 3, _ => -1 };
        if (slot >= 0)
        {
            // M637: aimed at the ground under the cursor; null when the cursor is off the ground (the sky,
            // or outside the viewport), in which case the cast falls back to the dummy.
            vm.CastAbility(slot, TryGroundPoint(_hover, vm, out var aim) ? aim : null);
            e.Handled = true;
        }
        else if (e.Key == Key.S) { vm.ResetCharacterCommand.Execute(null); e.Handled = true; }
    }

    /// <summary>M613: a right-click order. On the dummy it is an attack, anywhere else on the ground
    /// plane it is a move — the same two meanings the right button has in game.</summary>
    private void OnOrder(Avalonia.Point at, MeshPreviewViewModel vm)
    {
        if (!PreviewViewport.TryGetPickRay(at, out var origin, out var dir)) return;
        vm.CancelPendingCast();   // M637: a manual order overrides a cast still walking into range

        // The dummy first: clicking a target you can see must never be read as a move order past it.
        if (vm.TargetDummyPosition is { } dummy && HitsSphere(origin, dir, dummy, 120f))
        {
            vm.OrderAttack(dummy);
            return;
        }

        if (!TryGroundPoint(at, vm, out var point)) return;
        if (!vm.OrderMoveOnArena(point)) vm.OrderMove(point);
    }

    /// <summary>The ground under a screen position. M636: on an arena that is the navgrid's height field,
    /// so a click on a hill lands on the hill; otherwise the plane the character stands on, not y=0 - a
    /// preview whose model sits on a backdrop at another height would otherwise walk through the floor.</summary>
    private bool TryGroundPoint(Avalonia.Point at, MeshPreviewViewModel vm, out Vector3 point)
    {
        point = default;
        if (!PreviewViewport.TryGetPickRay(at, out var origin, out var dir)) return false;
        if (vm.TryArenaGroundHit(origin, dir, out point)) return true;

        float planeY = vm.CharacterPosition.Y;
        if (MathF.Abs(dir.Y) < 1e-5f) return false;           // looking along the plane: no intersection
        float t = (planeY - origin.Y) / dir.Y;
        if (t <= 0f) return false;                             // the plane is behind the camera
        point = origin + dir * t;
        return true;
    }

    private static bool HitsSphere(Vector3 origin, Vector3 dir, Vector3 centre, float radius)
    {
        var toCentre = centre - origin;
        float along = Vector3.Dot(toCentre, dir);
        if (along < 0f) return false;
        return (toCentre - dir * along).LengthSquared() <= radius * radius;
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(PreviewInput).Properties;
        _lmb = props.IsLeftButtonPressed;
        _mmb = props.IsMiddleButtonPressed;
        _last = e.GetPosition(PreviewInput);
        e.Pointer.Capture(PreviewInput);

        // M613: the right button is the only one control mode claims. Left still orbits, middle still
        // pans, and the dummy gizmo still takes the left drag first.
        if (props.IsRightButtonPressed && DataContext is MeshPreviewViewModel { ControlMode: true } order)
        {
            OnOrder(_last, order);
            e.Handled = true;
            return;
        }

        // M114: left press on a dummy gizmo arm starts a move drag instead of orbiting
        if (_lmb && DataContext is MeshPreviewViewModel { TargetDummyPosition: { } pivot }
            && PreviewViewport.HitTestGizmoAxis(_last) is { } axis
            && PreviewViewport.TryGetAxisParameter(axis, _last, pivot, out var t0))
        {
            _dummyAxis = axis;
            _dummyStartT = t0;
            _dummyStartPos = pivot;
        }
    }

    /// <summary>M637: where the mouse last was over the viewport, pressed or not - a cast key aims at the
    /// ground under it, the way the game aims every spell at the cursor.</summary>
    private Avalonia.Point _hover;

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        _hover = e.GetPosition(PreviewInput);
        if (!(_lmb || _mmb)) return;
        var p = _hover;

        if (_dummyAxis is { } axis && DataContext is MeshPreviewViewModel vm)
        {
            // slide along the pressed axis by the ray-parameter delta (same math as the map gizmo)
            if (PreviewViewport.TryGetAxisParameter(axis, p, _dummyStartPos, out var t))
            {
                var target = _dummyStartPos + PreviewViewport.AxisDir(axis) * (t - _dummyStartT);
                vm.MoveDummy(target - new Vector3((float)vm.DummyX, (float)vm.DummyY, (float)vm.DummyZ));
            }
            _last = p;
            return;
        }

        var dx = (float)(p.X - _last.X);
        var dy = (float)(p.Y - _last.Y);
        _last = p;
        if (_lmb) PreviewViewport.OrbitBy(dx, dy);
        else if (_mmb) PreviewViewport.PanBy(dx, dy);
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        var props = e.GetCurrentPoint(PreviewInput).Properties;
        _lmb = props.IsLeftButtonPressed;
        _mmb = props.IsMiddleButtonPressed;
        if (!_lmb) _dummyAxis = null;
        if (!(_lmb || _mmb)) e.Pointer.Capture(null);
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e) =>
        PreviewViewport.ZoomBy((float)e.Delta.Y);
}
