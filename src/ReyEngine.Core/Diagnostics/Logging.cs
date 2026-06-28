namespace ReyEngine.Core.Diagnostics;

public enum LogLevel { Trace, Info, Success, Warning, Error }

public readonly record struct LogEntry(DateTime Time, LogLevel Level, string Category, string Message)
{
    public override string ToString() => $"[{Time:HH:mm:ss}]  {Category,-10}  {Message}";
}

public interface ILogSink
{
    void Write(LogEntry entry);
}

/// <summary>
/// Lightweight pub/sub logger. UI binds to <see cref="Logged"/> or registers an
/// <see cref="ILogSink"/>; core services just call Info/Warn/Error.
/// </summary>
public sealed class Logger
{
    private readonly List<ILogSink> _sinks = new();

    public event Action<LogEntry>? Logged;

    public void AddSink(ILogSink sink) => _sinks.Add(sink);

    public void Log(LogLevel level, string category, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, category, message);
        foreach (var sink in _sinks)
            sink.Write(entry);
        Logged?.Invoke(entry);
    }

    public void Trace(string category, string message) => Log(LogLevel.Trace, category, message);
    public void Info(string category, string message) => Log(LogLevel.Info, category, message);
    public void Success(string category, string message) => Log(LogLevel.Success, category, message);
    public void Warn(string category, string message) => Log(LogLevel.Warning, category, message);
    public void Error(string category, string message) => Log(LogLevel.Error, category, message);
}
