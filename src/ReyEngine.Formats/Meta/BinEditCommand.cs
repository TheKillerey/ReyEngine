using ReyEngine.Core.Undo;

namespace ReyEngine.Formats.Meta;

/// <summary>
/// Reversible primitive edit on a live .bin field: swaps between the previously-applied text and the
/// new text via the round-trippable <see cref="BinValueEditor"/> format. Consecutive edits of the SAME
/// field merge into one undo step (keeps the first before-value, takes the latest after-value).
/// </summary>
public sealed class BinEditCommand : IEditorCommand
{
    private readonly EditableBinField _field;
    private readonly string _oldText;
    private string _newText;
    private readonly Action<string>? _onApplied;   // receives the now-current text for UI sync

    public BinEditCommand(object? context, EditableBinField field, string oldText, string newText, Action<string>? onApplied)
    {
        Context = context;
        _field = field;
        _oldText = oldText;
        _newText = newText;
        _onApplied = onApplied;
    }

    public string Name => $"Edit {_field.Name}";
    public object? Context { get; }

    public void Execute() { _field.Apply(_newText); _onApplied?.Invoke(_newText); }
    public void Undo() { _field.Apply(_oldText); _onApplied?.Invoke(_oldText); }

    public bool CanMergeWith(IEditorCommand next) => next is BinEditCommand b && ReferenceEquals(b._field, _field);
    public void MergeWith(IEditorCommand next) => _newText = ((BinEditCommand)next)._newText;
}
