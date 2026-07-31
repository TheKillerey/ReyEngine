using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;

namespace ReyEngine.App.Views;

/// <summary>M82: shared branded title bar for secondary windows. Auto-reads the host window's Title,
/// loads the logo, and drags the window. The host sets ExtendClientAreaToDecorationsHint="True".</summary>
public partial class ReyTitleBar : UserControl
{
    private static Bitmap? _logo;

    public ReyTitleBar()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            if (VisualRoot is Window w) TitleText.Text = w.Title;
            try
            {
                _logo ??= File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "reyengine_logo.png"))
                    ? new Bitmap(Path.Combine(AppContext.BaseDirectory, "Assets", "reyengine_logo.png")) : null;
                Logo.Source = _logo;
            }
            catch { /* cosmetic */ }
        };
    }

    private void OnDrag(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || VisualRoot is not Window w) return;

        // M290: double-click toggles maximise, the way every title bar does. The native caption buttons
        // are overlaid on the extended client area and still work, but this bar swallowed the double-click
        // into a move-drag, so the most reflexive way to maximise a window did nothing. Shared here rather
        // than per window, since every secondary window wears this bar.
        if (e.ClickCount >= 2 && w.CanResize)
        {
            w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            e.Handled = true;
            return;
        }
        w.BeginMoveDrag(e);
    }
}
