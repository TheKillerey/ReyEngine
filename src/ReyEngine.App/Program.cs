using System;
using System.IO;
using Avalonia;

namespace ReyEngine.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // M140.2: the app had no crash net — an unhandled exception died silently. Record it so a
        // crash leaves a readable stack behind (%AppData%/ReyEngine/crash.log).
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash(e.ExceptionObject as Exception);
        // M666: a faulted Task nobody awaited used to disappear entirely - no crash.log, no console line,
        // and on some runtimes it takes the process down later for no visible reason. The load paths are
        // full of fire-and-forget tasks, so this is exactly where a "it just closed" comes from.
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrash(e.Exception);
            e.SetObserved();   // recorded; do not escalate to a process kill
        };
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception ex) { WriteCrash(ex); throw; }
        finally { ViewModels.MainWindowViewModel.SessionLog?.Dispose(); }
    }

    private static void WriteCrash(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReyEngine");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"---- {DateTime.Now:O} ----{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            // M666: and into the session log, where the breadcrumbs that led here already are.
            ViewModels.MainWindowViewModel.SessionLog?.WriteRaw(
                $"---- CRASH {DateTime.Now:O} ----{Environment.NewLine}{ex}");
        }
        catch { /* logging a crash must never throw */ }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
