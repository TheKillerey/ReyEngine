using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ReyEngine.App.ViewModels;
using ReyEngine.App.Views;

namespace ReyEngine.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // M685: every window gets the picture layer the moment its content is set - installed before the
        // first window exists so the main window's own InitializeComponent is already covered.
        ReyEngine.App.Services.WindowBackdrop.Install();
        // M72: apply the user's saved theme before any window is created (App.axaml ships the default).
        ReyEngine.App.Services.ThemeService.Apply(ReyEngine.Core.Settings.EditorSettings.Load());   // M683: the whole look

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow { DataContext = new MainWindowViewModel() };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
