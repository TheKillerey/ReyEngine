using Avalonia.Controls;
using Avalonia.Input;
using ReyEngine.App.ViewModels;

namespace ReyEngine.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        // Tunnel so a capturing keybind row grabs the key before it triggers anything else.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;

        // ignore modifier-only presses so a binding can't become "just Ctrl"
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                  or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;

        if (e.Key == Key.Escape) { vm.CancelCapture(); e.Handled = true; return; }

        if (vm.AssignCapturedKey(e.Key.ToString())) e.Handled = true;
    }
}
