using Avalonia.Controls;
using Avalonia.Input;
using ReyEngine.App.ViewModels;

namespace ReyEngine.App.Views;

/// <summary>M46 Particle Editor view. Code-behind forwards pointer input on the preview surface to the
/// embedded viewport camera (LMB orbit · MMB pan · wheel zoom), and the curve graph's commits to the
/// selected row (M718). M753: an LMB press on a Move handle's arrow drags it instead of orbiting.</summary>
public partial class ParticleEditorView : UserControl
{
    private bool _lmb, _mmb;
    private Avalonia.Point _last;

    // M753: the drag, MainWindow's way - the axis line is anchored at the pivot the drag STARTED from, or
    // it would re-anchor on every move and chase itself
    private ViewportControl.GizmoAxis? _dragAxis;
    private System.Numerics.Vector3 _dragOrigin;
    private float _dragStartT;

    public ParticleEditorView()
    {
        InitializeComponent();
        PreviewInput.PointerPressed += OnPressed;
        PreviewInput.PointerMoved += OnMoved;
        PreviewInput.PointerReleased += OnReleased;
        PreviewInput.PointerWheelChanged += OnWheel;
        // M753: a drag that loses the pointer writes nothing
        PreviewInput.PointerCaptureLost += (_, _) =>
        {
            if (_dragAxis is null) return;
            _dragAxis = null;
            (DataContext as ParticleEditorViewModel)?.CancelGizmoDrag();
        };

        // M718: the graph commits through the same row methods the key list uses, so a drag, a double-click
        // and a Delete each take exactly one EditCurve, as a typed value and an Apply click do
        CurveGraph.KeyMoved += (index, time, components) => CurveRow()?.SetKey(index, time, components);
        CurveGraph.KeyAdded += (time, components) => CurveRow()?.AddKey(time, components);
        CurveGraph.KeyRemoved += index => CurveRow()?.DeleteKey(index);
        CurveFit.Click += (_, _) => CurveGraph.FitView();

        // M753: the force shapes reuse the cast-range line channel, in a colour no gizmo arm uses
        PreviewViewport.RangeRingTint = new System.Numerics.Vector4(0.85f, 0.5f, 1f, 0.9f);
    }

    /// <summary>The row the graph shows: its Times and Channels are bound to SelectedProperty.</summary>
    private ParticlePropertyRowViewModel? CurveRow() => (DataContext as ParticleEditorViewModel)?.SelectedProperty;

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(PreviewInput).Properties;
        _lmb = props.IsLeftButtonPressed;
        _mmb = props.IsMiddleButtonPressed;
        _last = e.GetPosition(PreviewInput);
        e.Pointer.Capture(PreviewInput);

        if (_lmb && PreviewViewport.GizmoPivot is { } pivot
            && PreviewViewport.HitTestGizmoAxis(_last) is { } axis
            && PreviewViewport.TryGetAxisParameter(axis, _last, pivot, out float t0))
        {
            _dragAxis = axis;
            _dragOrigin = pivot;
            _dragStartT = t0;
            _lmb = false;   // a handle drag, not an orbit
        }
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_dragAxis is { } axis)
        {
            var at = e.GetPosition(PreviewInput);
            if (DataContext is ParticleEditorViewModel vm && PreviewViewport.TryGetAxisParameter(axis, at, _dragOrigin, out float t))
                // whole units along the arm: the untouched axes keep their authored value to the last digit
                vm.DragGizmoTo(_dragOrigin + PreviewViewport.AxisDir(axis) * MathF.Round(t - _dragStartT));
            return;
        }
        if (!(_lmb || _mmb)) return;
        var p = e.GetPosition(PreviewInput);
        var dx = (float)(p.X - _last.X);
        var dy = (float)(p.Y - _last.Y);
        _last = p;
        if (_lmb) PreviewViewport.OrbitBy(dx, dy);
        else if (_mmb) PreviewViewport.PanBy(dx, dy);
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragAxis is not null && e.InitialPressMouseButton == MouseButton.Left)
        {
            _dragAxis = null;
            (DataContext as ParticleEditorViewModel)?.EndGizmoDrag();   // the one write of the drag
        }
        var props = e.GetCurrentPoint(PreviewInput).Properties;
        _lmb = props.IsLeftButtonPressed;
        _mmb = props.IsMiddleButtonPressed;
        if (!(_lmb || _mmb)) e.Pointer.Capture(null);
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e) =>
        PreviewViewport.ZoomBy((float)e.Delta.Y);
}
