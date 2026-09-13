using Avalonia.Controls;
using Avalonia.Input;
using ReyEngine.App.ViewModels;

namespace ReyEngine.App.Views;

/// <summary>M46 Particle Editor view. Code-behind forwards pointer input on the preview surface to the
/// embedded viewport camera (LMB orbit · MMB pan · wheel zoom), and the curve graph's commits to the
/// selected row (M718).</summary>
public partial class ParticleEditorView : UserControl
{
    private bool _lmb, _mmb;
    private Avalonia.Point _last;

    public ParticleEditorView()
    {
        InitializeComponent();
        PreviewInput.PointerPressed += OnPressed;
        PreviewInput.PointerMoved += OnMoved;
        PreviewInput.PointerReleased += OnReleased;
        PreviewInput.PointerWheelChanged += OnWheel;

        // M718: the graph commits through the same row methods the key list uses, so a drag, a double-click
        // and a Delete each take exactly one EditCurve, as a typed value and an Apply click do
        CurveGraph.KeyMoved += (index, time, components) => CurveRow()?.SetKey(index, time, components);
        CurveGraph.KeyAdded += (time, components) => CurveRow()?.AddKey(time, components);
        CurveGraph.KeyRemoved += index => CurveRow()?.DeleteKey(index);
        CurveFit.Click += (_, _) => CurveGraph.FitView();
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
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
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
        var props = e.GetCurrentPoint(PreviewInput).Properties;
        _lmb = props.IsLeftButtonPressed;
        _mmb = props.IsMiddleButtonPressed;
        if (!(_lmb || _mmb)) e.Pointer.Capture(null);
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e) =>
        PreviewViewport.ZoomBy((float)e.Delta.Y);
}
