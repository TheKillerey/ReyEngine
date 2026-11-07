using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using ReyEngine.App.Views;

namespace ReyEngine.App.SimCheck;

/// <summary>
/// M871: window handling for a check that runs while someone is using the PC. Nothing here takes focus: windows are shown with
/// <c>ShowActivated = false</c>, the Character window gets a fixed 1280 x 720 client area, and the main window - which a check does not
/// need - is minimised.
/// </summary>
internal static class SimCheckWindows
{
    public const double ClientWidth = 1280, ClientHeight = 720;

    /// <summary>Shown without an owner (an owned window would follow the minimised main window down) and without activation.</summary>
    public static void ShowCharacterWindow(MeshPreviewWindow window)
    {
        var placement = SimCheckSession.Options?.Placement ?? SimCheckPlacement.Corner;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Width = ClientWidth;
        window.Height = ClientHeight;
        window.CanResize = false;
        var main = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var screen = main?.Screens.Primary ?? main?.Screens.All.FirstOrDefault();
        double scale = screen?.Scaling ?? 1.0;
        switch (placement)
        {
            case SimCheckPlacement.Offscreen:
                window.Position = new PixelPoint(-32000, -32000);
                break;
            case SimCheckPlacement.Minimized:
                window.Position = new PixelPoint(0, 0);
                window.WindowState = WindowState.Minimized;
                break;
            default:
                // the bottom-right corner of the primary screen's working area (clear of the taskbar)
                if (screen is not null)
                {
                    var work = screen.WorkingArea;
                    window.Position = new PixelPoint(Math.Max(work.X, work.Right - (int)(ClientWidth * scale)), Math.Max(work.Y, work.Bottom - (int)(ClientHeight * scale)));
                }
                break;
        }
        window.Show();
    }
}
