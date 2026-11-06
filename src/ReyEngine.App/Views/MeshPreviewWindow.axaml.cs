using System.Numerics;
using Avalonia.Controls;
using Avalonia.Input;
using ReyEngine.App.ViewModels;

namespace ReyEngine.App.Views;

/// <summary>M50 model-preview window: forwards pointer input to the embedded viewport camera.
/// M114: when the target dummy is enabled, its translate gizmo captures the left-drag first.
/// M868: no manual play - the right button and the Q/W/E/R/S keys do nothing here; the automated
/// simulation moves and casts through the view model.</summary>
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
        // Ctrl+S saves the editor edits. Tunnelling because a focused list or text box would otherwise eat it.
        AddHandler(KeyDownEvent, OnSaveKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        HookDx11();   // M618: the Direct3D 11 surface, off until the toggle turns it on
        InstallGameplayCamera();
        Closed += (_, _) =>
        {
            if (DataContext is not MeshPreviewViewModel vm) return;
            vm.StopControl();
            if (vm.ArenaViewport is { } arena) arena.Dx11RebindGrassTintPair = null;
        };
    }

    private void OnSaveKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Control
            && DataContext is MeshPreviewViewModel { SaveEditorEdits: { } save })
        { _ = save(); e.Handled = true; }
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MeshPreviewViewModel { UseDx11Preview: true } current)
        {
            PreviewViewport.GizmoPivot = current.DummyGizmoPivot;
            PreviewViewport.SyncPickMatrices(PreviewInput.Bounds.Width, PreviewInput.Bounds.Height);
        }
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
        var p = e.GetPosition(PreviewInput);
        if (!(_lmb || _mmb)) return;

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
        // M868: orbit always works. Pan is the one thing Follow takes (it would fight the target lock).
        if (_lmb)
        {
            PreviewViewport.OrbitBy(dx, dy);
            // On the stage the camera stays above the ground. The orbit camera's own range (+-1.5 rad) lets it swing
            // under the map, which on Map11 shows the terrain upside down from below - seen live on Ezreal.
            if (DataContext is MeshPreviewViewModel { ControlMode: true })
                PreviewViewport.Camera.Pitch = Math.Clamp(PreviewViewport.Camera.Pitch, StageMinPitch, StageMaxPitch);
        }
        else if (_mmb && DataContext is not MeshPreviewViewModel { ControlMode: true, ArenaFollowCamera: true })
            PreviewViewport.PanBy(dx, dy);
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        var props = e.GetCurrentPoint(PreviewInput).Properties;
        _lmb = props.IsLeftButtonPressed;
        _mmb = props.IsMiddleButtonPressed;
        if (!_lmb) _dummyAxis = null;
        if (!(_lmb || _mmb)) e.Pointer.Capture(null);
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        PreviewViewport.ZoomBy((float)e.Delta.Y);
        // On the stage the zoom has a floor close enough for a skin look and a ceiling short of the whole map.
        if (DataContext is MeshPreviewViewModel { ControlMode: true })
            PreviewViewport.Camera.Distance = Math.Clamp(PreviewViewport.Camera.Distance, StageMinDistance, StageMaxDistance);
    }
}
