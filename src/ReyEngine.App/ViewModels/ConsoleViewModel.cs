using System.Collections.ObjectModel;
using Avalonia.Threading;
using ReyEngine.Core.Diagnostics;

namespace ReyEngine.App.ViewModels;

public sealed class ConsoleViewModel : ViewModelBase, ILogSink
{
    public ObservableCollection<LogEntry> Entries { get; } = new();

    public void Write(LogEntry entry)
    {
        if (Dispatcher.UIThread.CheckAccess()) Add(entry);
        else Dispatcher.UIThread.Post(() => Add(entry));
    }

    private void Add(LogEntry entry)
    {
        Entries.Add(entry);
        if (Entries.Count > 2000) Entries.RemoveAt(0);
    }

    public void Clear() => Entries.Clear();
}
