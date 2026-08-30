using Avalonia.Controls;
using ReyEngine.App.ViewModels;

namespace ReyEngine.App.Views;

public partial class CharacterBrowserWindow : Window
{
    public CharacterBrowserWindow() => InitializeComponent();

    /// <summary>Open the character browser. The host supplies the game folder and takes the chosen skin;
    /// the browser owns nothing about rendering or mounting.</summary>
    public static void Show(Window owner, ICharacterBrowserHost host, System.Action<string, string>? log = null)
    {
        var model = new CharacterBrowserViewModel(host, log);
        var window = new CharacterBrowserWindow { DataContext = model };
        // The browser keeps one champion WAD open while you look through it; closing must let it go.
        window.Closed += (_, _) => model.Dispose();
        window.Show(owner);
    }
}
