using System;
using System.IO;
using Avalonia;

namespace ReyEngine.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // M867: the headless champion simulation generator runs before any Avalonia, settings or project state exists.
        if (SimGenCommand.Handles(args)) return SimGenCommand.Run(args);

        // M871: --simcheck runs the real application (Avalonia, the Character window, D3D11) and exits by itself when done.
        if (SimCheck.SimCheckOptions.Handles(args))
        {
            try { AttachConsole(-1); } catch { }
            var options = SimCheck.SimCheckOptions.Parse(args, out string? problem);
            if (options is null) { Console.Error.WriteLine(problem); return 2; }
            SimCheck.SimCheckSession.Options = options;
        }

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
        int exit = 0;
        try { exit = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception ex) { WriteCrash(ex); throw; }
        finally { ViewModels.MainWindowViewModel.SessionLog?.Dispose(); }
        return SimCheck.SimCheckSession.Active ? SimCheck.SimCheckSession.ExitCode : 0;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);

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
