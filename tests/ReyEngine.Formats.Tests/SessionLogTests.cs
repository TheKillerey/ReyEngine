using ReyEngine.Core.Diagnostics;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M666: "the tool crashed on loading Locke" arrived with nothing to read — no crash.log, because the
/// crash net only catches unhandled MANAGED exceptions, and no console, because the log lived in memory
/// and died with the process. These pin the properties that make the next one diagnosable.
/// </summary>
public sealed class SessionLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "reyengine-sessionlog-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>
    /// Read the log WHILE the sink still holds it. File.ReadAllText asks for FileShare.Read, which denies
    /// write sharing and therefore cannot open a file something else is writing — a reader has to allow
    /// ReadWrite, exactly as `tail` does. The sink opens with FileShare.ReadWrite so that this is possible
    /// at all; a log you can only read after the app exits would be half a diagnostic.
    /// </summary>
    private static string ReadLive(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    [Fact]
    public void EveryLineIsOnDiskBeforeTheNextOneIsWritten()
    {
        // The whole point: a process that dies mid-load must still have its last line. Nothing may sit in
        // a buffer waiting for a flush that never comes.
        using var sink = new SessionLogSink(_dir);
        var log = new Logger();
        log.AddSink(sink);

        log.Info("Mesh", "loading Locke");
        string afterFirst = ReadLive(sink.Path!);
        Assert.Contains("loading Locke", afterFirst);

        log.Error("Mesh", "and then this happened");
        Assert.Contains("and then this happened", ReadLive(sink.Path!));
    }

    [Fact]
    public void TheLogDescribesThisRunAndNotTheLastOne()
    {
        string path;
        using (var first = new SessionLogSink(_dir))
        {
            path = first.Path!;
            first.Write(new LogEntry(DateTime.Now, LogLevel.Info, "Mesh", "the previous run"));
        }
        Assert.Contains("the previous run", ReadLive(path));

        using (var second = new SessionLogSink(_dir))
            second.Write(new LogEntry(DateTime.Now, LogLevel.Info, "Mesh", "this run"));

        string text = ReadLive(path);
        Assert.Contains("this run", text);
        Assert.DoesNotContain("the previous run", text);
    }

    [Fact]
    public void ADirectoryItCannotWriteIsNotAReasonToFail()
    {
        // A log that cannot open must never stop the app starting, and must not throw when written to.
        var sink = new SessionLogSink(Path.Combine(_dir, "a\0b"));   // an impossible path
        Assert.Null(sink.Path);
        sink.Write(new LogEntry(DateTime.Now, LogLevel.Error, "Mesh", "still fine"));
        sink.WriteRaw("still fine");
        sink.Dispose();
    }

    [Fact]
    public void ARunawayLoopCannotFillTheDisk()
    {
        using var sink = new SessionLogSink(_dir);
        var line = new string('x', 4096);
        // Comfortably past the cap, and it has to stop rather than keep going.
        for (int i = 0; i < (int)(SessionLogSink.MaxBytes / 4096) + 200; i++)
            sink.Write(new LogEntry(DateTime.Now, LogLevel.Info, "Spam", line));

        var size = new FileInfo(sink.Path!).Length;
        Assert.True(size <= SessionLogSink.MaxBytes + 65536, $"log grew to {size:n0} bytes");
    }
}
