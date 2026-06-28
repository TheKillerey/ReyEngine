using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using ReyEngine.App.ViewModels;

namespace ReyEngine.App.Views;

public partial class MainWindow : Window
{
    private bool _dragging;
    private Point _lastPointer;

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is MainWindowViewModel vm)
            vm.Dialogs.Owner = this;
    }

    // ---- Viewport camera input (forwarded from the transparent overlay) ----

    private void OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragging = true;
        _lastPointer = e.GetPosition(ViewportInput);
        e.Pointer.Capture(ViewportInput);
    }

    private void OnViewportPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(ViewportInput);
        var dx = (float)(p.X - _lastPointer.X);
        var dy = (float)(p.Y - _lastPointer.Y);
        _lastPointer = p;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            Viewport.PanBy(dx, dy);
        else
            Viewport.OrbitBy(dx, dy);
    }

    private void OnViewportPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragging = false;
        e.Pointer.Capture(null);
    }

    private void OnViewportPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        Viewport.ZoomBy((float)e.Delta.Y);
    }

    private void OnFrameClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Viewport.RequestFrame();
    }
}
