using System.Collections.ObjectModel;
using Avalonia.Threading;
using ReyEngine.Core.Diagnostics;

namespace ReyEngine.App.ViewModels;

public sealed class ConsoleViewModel : ViewModelBase, ILogSink
{
    public ObservableCollection<LogEntry> Entries { get; } = new();

    /// <summary>M519: raised on the UI thread for every line, so the dock can badge its Console tab when a
    /// warning or error arrives while the browser is in front.</summary>
    public event Action<LogEntry>? EntryWritten;

    /// <summary>
    /// Log a line from anywhere. Entries is bound to the UI, so a background caller is marshalled.
    /// </summary>
    public void Write(LogEntry entry)
    {
        if (Dispatcher.UIThread.CheckAccess()) Append(entry);
        else Dispatcher.UIThread.Post(() => Append(entry));
    }

    /// <summary>
    /// Add an entry on the CALLING thread — the work <see cref="Write"/> marshals.
    ///
    /// <para>M576: separated out because the two halves are genuinely different jobs, and because tests of
    /// the badge bookkeeping cannot use Write. Under Avalonia 12, whether Write runs inline depends on
    /// which test in the run touched the dispatcher first: the loser posts to a queue nobody pumps, and
    /// the entry never arrives. This is the half those tests are actually about.</para>
    /// </summary>
    public void Append(LogEntry entry)
    {
        Entries.Add(entry);
        if (Entries.Count > 2000) Entries.RemoveAt(0);
        EntryWritten?.Invoke(entry);
    }

    public void Clear() => Entries.Clear();
}
