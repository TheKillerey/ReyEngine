using System.Collections.Generic;
using System.IO;
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

    public MainWindow()
    {
        InitializeComponent();
        LoadBranding();
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

    // ---- Unreal-style viewport camera input (forwarded from the transparent overlay) ----
    // RMB = mouse-look + WASD/QE fly · Alt+LMB = orbit · MMB = pan · wheel = dolly (RMB+wheel = fly speed)
    // LMB = dolly + turn · F = focus selected.

    private void OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pt = e.GetCurrentPoint(ViewportInput);
        _lmb = pt.Properties.IsLeftButtonPressed;
        _rmb = pt.Properties.IsRightButtonPressed;
        _mmb = pt.Properties.IsMiddleButtonPressed;
        _alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        _lastPointer = pt.Position;
        e.Pointer.Capture(ViewportInput);
        ViewportInput.Focus(); // so WASD/F reach the viewport
        if (_rmb) StartFly();
    }

    private void OnViewportPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!(_lmb || _rmb || _mmb)) return;
        var p = e.GetPosition(ViewportInput);
        var dx = (float)(p.X - _lastPointer.X);
        var dy = (float)(p.Y - _lastPointer.Y);
        _lastPointer = p;

        if (_rmb) Viewport.LookBy(dx, dy);
        else if (_mmb) Viewport.PanBy(dx, dy);
        else if (_lmb && _alt) Viewport.OrbitBy(dx, dy);
        else if (_lmb) { Viewport.FlyBy(-dy * 0.04f, 0f, 0f, 0.05f); Viewport.LookBy(dx, 0f); }
    }

    private void OnViewportPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var props = e.GetCurrentPoint(ViewportInput).Properties;
        _lmb = props.IsLeftButtonPressed;
        _rmb = props.IsRightButtonPressed;
        _mmb = props.IsMiddleButtonPressed;
        if (!_rmb) StopFly();
        if (!(_lmb || _rmb || _mmb)) e.Pointer.Capture(null);
    }

    private void OnViewportPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_rmb) Viewport.AdjustFlySpeed((float)e.Delta.Y);
        else Viewport.ZoomBy((float)e.Delta.Y);
    }

    private void OnViewportKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F) { Viewport.FocusSelected(); return; }
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
        if (!_rmb) { StopFly(); return; }
        float f = 0, r = 0, u = 0;
        if (_heldKeys.Contains(Key.W)) f += 1;
        if (_heldKeys.Contains(Key.S)) f -= 1;
        if (_heldKeys.Contains(Key.D)) r += 1;
        if (_heldKeys.Contains(Key.A)) r -= 1;
        if (_heldKeys.Contains(Key.E)) u += 1;
        if (_heldKeys.Contains(Key.Q)) u -= 1;
        if (f != 0 || r != 0 || u != 0) Viewport.FlyBy(f, r, u, 0.016f);
    }

    private void OnFrameClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Viewport.RequestFrame();
    }
}
