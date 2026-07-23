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
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception ex) { WriteCrash(ex); throw; }
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
        }
        catch { /* logging a crash must never throw */ }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
