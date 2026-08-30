using Avalonia.Controls;
using ReyEngine.App.ViewModels;

namespace ReyEngine.App.Views;

public partial class CinematicWindow : Window
{
    public CinematicWindow() => InitializeComponent();

    /// <summary>Open the panel for a project. The host supplies the camera and the D3D11 surface; the
    /// panel owns nothing about rendering itself.</summary>
    public static void Show(Window owner, ICinematicHost host, string projectRoot,
        System.Action<string, string>? log = null)
    {
        var window = new CinematicWindow { DataContext = new CinematicWindowViewModel(host, projectRoot, log) };
        window.Show(owner);
    }
}
