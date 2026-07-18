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
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && VisualRoot is Window w)
            w.BeginMoveDrag(e);
    }
}
