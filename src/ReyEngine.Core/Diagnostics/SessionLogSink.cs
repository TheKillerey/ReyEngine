using System.Text;

namespace ReyEngine.Core.Diagnostics;

/// <summary>
/// M666: the app's own log, on disk, flushed as it is written.
///
/// <para>Locke crashed the tool and left NOTHING behind. The crash net in <c>Program.cs</c> catches an
/// unhandled managed exception, but a crash that is not one - a native access violation in a graphics or
/// audio driver, a stack overflow, the process being killed - writes no <c>crash.log</c>, and the
/// in-memory console the UI binds to dies with the process. There was no way to tell even WHICH STEP of
/// loading a champion it died on.</para>
///
/// <para>This mirrors every log line to <c>%AppData%/ReyEngine/session.log</c> and flushes each one, so
/// whatever survives the crash is the trail up to it. The file is truncated at startup and capped, because
/// it is a breadcrumb for the current run and not an archive.</para>
/// </summary>
public sealed class SessionLogSink : ILogSink, IDisposable
{
    private readonly StreamWriter? _writer;
    private readonly object _gate = new();
    private long _written;

    /// <summary>Stop writing past this much, so a runaway loop cannot fill the disk.</summary>
    public const long MaxBytes = 8 * 1024 * 1024;

    public string? Path { get; }

    public SessionLogSink(string? directory = null)
    {
        try
        {
            directory ??= System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReyEngine");
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "session.log");
            // Truncate: this describes the run that is happening now. A crash is read before the next
            // launch, and keeping history here would bury the interesting lines under old ones.
            _writer = new StreamWriter(new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(false)) { AutoFlush = true };
            _writer.WriteLine($"---- ReyEngine session started {DateTime.Now:O} ----");
        }
        catch
        {
            // A log that cannot be opened must never stop the app starting.
            _writer = null; Path = null;
        }
    }

    public void Write(LogEntry entry)
    {
        if (_writer is null) return;
        lock (_gate)
        {
            if (_written > MaxBytes) return;
            try
            {
                string line = $"[{entry.Time:HH:mm:ss.fff}] {entry.Level,-7} {entry.Category,-12} {entry.Message}";
                _writer.WriteLine(line);
                _written += line.Length + 2;
                if (_written > MaxBytes) _writer.WriteLine("---- log cap reached; further lines dropped ----");
            }
            catch { /* a failing log must never take the app with it */ }
        }
    }

    /// <summary>Note something outside the logger's own flow — the crash handler uses this.</summary>
    public void WriteRaw(string text)
    {
        if (_writer is null) return;
        lock (_gate)
        {
            try { _writer.WriteLine(text); } catch { }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _writer?.WriteLine($"---- ended cleanly {DateTime.Now:O} ----"); _writer?.Dispose(); }
            catch { }
        }
    }
}
