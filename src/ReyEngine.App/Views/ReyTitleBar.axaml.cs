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
            if (VisualRoot is Window w)
            {
                TitleText.Text = w.Title;
                // M578: only a resizable window gets a maximise button, and the glyph has to follow the
                // state or it tells you the wrong thing about what the click will do.
                MaximiseButton.IsVisible = w.CanResize;
                SyncMaximiseGlyph(w);
                w.PropertyChanged += (_, e) =>
                {
                    if (e.Property == Window.WindowStateProperty) SyncMaximiseGlyph(w);
                };
            }
            try
            {
                _logo ??= File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "reyengine_logo.png"))
                    ? new Bitmap(Path.Combine(AppContext.BaseDirectory, "Assets", "reyengine_logo.png")) : null;
                Logo.Source = _logo;
            }
            catch { /* cosmetic */ }
        };
    }

    private void SyncMaximiseGlyph(Window w)
    {
        bool max = w.WindowState == WindowState.Maximized;
        MaximiseButton.Content = max ? "❐" : "☐";
        ToolTip.SetTip(MaximiseButton, max ? "Restore" : "Maximise");
    }

    /// <summary>M578: these are the window's only caption buttons now — the host has no native ones.</summary>
    private void OnMinimise(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (VisualRoot is Window w) w.WindowState = WindowState.Minimized;
    }

    private void OnMaximise(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (VisualRoot is Window { CanResize: true } w)
            w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnClose(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (VisualRoot is Window w) w.Close();
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
