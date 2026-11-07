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
            var main = new MainWindow { DataContext = new MainWindowViewModel() };
            desktop.MainWindow = main;
            if (ReyEngine.App.SimCheck.SimCheckSession.Options is { } check)
            {
                // M871: --simcheck. The main window is not needed: shown minimised and without activation, so nothing takes the user's focus.
                main.ShowActivated = false;
                main.WindowState = Avalonia.Controls.WindowState.Minimized;
                var vm = (MainWindowViewModel)main.DataContext!;
                main.Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(
                    async () => await new ReyEngine.App.SimCheck.SimCheckDriver(check, main, vm).RunAsync(),
                    Avalonia.Threading.DispatcherPriority.Background);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
