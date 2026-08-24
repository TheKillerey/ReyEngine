using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;

namespace ReyEngine.App.Views;

/// <summary>M82: shared branded title bar for secondary windows. Auto-reads the host window's Title,
/// loads the logo, and drags the window. The host sets ExtendClientAreaToDecorationsHint="True".</summary>
public partial class ReyTitleBar : UserControl
{
    private static Bitmap? _logo;

    /// <summary>
    /// The window this bar belongs to.
    ///
    /// <para>M579: this used to be <c>VisualRoot is Window</c>, which stopped matching under Avalonia 12 -
    /// <c>Visual.VisualRoot</c> is typed <c>Visual</c> now and is no longer the Window itself. Every one of
    /// this class's guards was written that way, so a secondary window silently lost its title text, its
    /// drag, and all three caption buttons at once: nothing threw, the checks just never passed.
    /// <c>TopLevel.GetTopLevel</c> is the supported way to ask, and it is the same break that took
    /// RenderScaling out of ViewportControl in M576.</para>
    /// </summary>
    private Window? Host => TopLevel.GetTopLevel(this) as Window;

    public ReyTitleBar()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            if (Host is { } w)
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
        if (Host is { } w) w.WindowState = WindowState.Minimized;
    }

    private void OnMaximise(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Host is { CanResize: true } w)
            w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnClose(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Host is { } w) w.Close();
    }

    private void OnDrag(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Host is not { } w) return;

        // M578: a press on a Button is that button's, not the start of a window move.
        //
        // PointerPressed bubbles up here from the caption buttons, and BeginMoveDrag captures the pointer
        // and enters the platform's modal move loop - so the release never reaches the button and Click
        // never fires. The buttons drew perfectly and did nothing, including Close. MainWindow's title bar
        // has always had this guard; this one was written before it had any buttons of its own to protect.
        if (e.Source is Visual source)
            foreach (var ancestor in Avalonia.VisualTree.VisualExtensions.GetSelfAndVisualAncestors(source))
                if (ancestor is Button) return;

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
