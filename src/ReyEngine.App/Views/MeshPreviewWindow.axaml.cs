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
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(PreviewInput).Properties;
        _lmb = props.IsLeftButtonPressed;
        _mmb = props.IsMiddleButtonPressed;
        _last = e.GetPosition(PreviewInput);
        e.Pointer.Capture(PreviewInput);

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

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!(_lmb || _mmb)) return;
        var p = e.GetPosition(PreviewInput);

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
