namespace ReyEngine.Core.Undo;

/// <summary>
/// The global undo/redo stack. Every mutation of live editor state goes through here as an
/// <see cref="IEditorCommand"/>. Failed commands never enter the stack; a command whose Undo/Redo
/// throws is dropped (with the error surfaced) rather than corrupting the history.
/// </summary>
public sealed class UndoRedoService
{
    private readonly List<IEditorCommand> _undo = new();
    private readonly List<IEditorCommand> _redo = new();
    private int _savedDepth;

    /// <summary>Raised after any change to the stacks (push, undo, redo, clear, purge, savepoint).</summary>
    public event Action? Changed;

    /// <summary>Raised when a command's Execute/Undo throws during Undo()/Redo() (message for the log).</summary>
    public event Action<string>? Error;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoName => _undo.Count > 0 ? _undo[^1].Name : null;
    public string? RedoName => _redo.Count > 0 ? _redo[^1].Name : null;
    public IReadOnlyList<IEditorCommand> UndoHistory => _undo;

    /// <summary>True when the stack position differs from the last <see cref="MarkSaved"/> point.</summary>
    public bool IsDirty => _undo.Count != _savedDepth;

    /// <summary>Execute the command and push it. Returns false (nothing pushed) if Execute throws.</summary>
    public bool Do(IEditorCommand command)
    {
        try { command.Execute(); }
        catch (Exception ex)
        {
            Error?.Invoke($"{command.Name}: {ex.Message}");
            return false;
        }
        PushApplied(command);
        return true;
    }

    /// <summary>Record a command whose effect ALREADY happened live (e.g. a finished gizmo drag).</summary>
    public void PushApplied(IEditorCommand command)
    {
        if (_undo.Count > 0 && _undo[^1].CanMergeWith(command))
        {
            _undo[^1].MergeWith(command);
        }
        else
        {
            _undo.Add(command);
            if (_savedDepth > _undo.Count - 1) _savedDepth = -1; // saved point now unreachable
        }
        _redo.Clear();
        Changed?.Invoke();
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var cmd = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        try { cmd.Undo(); }
        catch (Exception ex)
        {
            Error?.Invoke($"Undo {cmd.Name}: {ex.Message}");
            Changed?.Invoke();
            return false; // dropped — safer than a stack that keeps failing
        }
        _redo.Add(cmd);
        Changed?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var cmd = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        try { cmd.Execute(); }
        catch (Exception ex)
        {
            Error?.Invoke($"Redo {cmd.Name}: {ex.Message}");
            Changed?.Invoke();
            return false;
        }
        _undo.Add(cmd);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Drop the whole history (project closed / new project opened).</summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _savedDepth = 0;
        Changed?.Invoke();
    }

    /// <summary>
    /// Drop every command belonging to a replaced document so undo can never mutate a stale object
    /// (e.g. the previous map after loading a new one, or a reloaded bin document).
    /// </summary>
    public void PurgeContext(object context)
    {
        int undoRemoved = _undo.RemoveAll(c => ReferenceEquals(c.Context, context));
        int redoRemoved = _redo.RemoveAll(c => ReferenceEquals(c.Context, context));
        if (undoRemoved + redoRemoved == 0) return;
        // Removing mid-stack entries makes the old savepoint meaningless — stay dirty until the next save.
        if (undoRemoved > 0) _savedDepth = -1;
        Changed?.Invoke();
    }

    /// <summary>Record the current position as the on-disk state (title drops its dirty marker).</summary>
    public void MarkSaved()
    {
        _savedDepth = _undo.Count;
        Changed?.Invoke();
    }
}

/// <summary>Several commands treated as one undo step (reserved for multi-select in M30).</summary>
public sealed class CompositeCommand : IEditorCommand
{
    private readonly List<IEditorCommand> _commands;

    public CompositeCommand(string name, IEnumerable<IEditorCommand> commands, object? context = null)
    {
        Name = name;
        _commands = commands.ToList();
        Context = context ?? (_commands.Count > 0 ? _commands[0].Context : null);
    }

    public string Name { get; }
    public object? Context { get; }

    public void Execute() { foreach (var c in _commands) c.Execute(); }
    public void Undo() { for (int i = _commands.Count - 1; i >= 0; i--) _commands[i].Undo(); }
    public bool CanMergeWith(IEditorCommand next) => false;
    public void MergeWith(IEditorCommand next) => throw new NotSupportedException();
}
