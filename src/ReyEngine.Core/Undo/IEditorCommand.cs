namespace ReyEngine.Core.Undo;

/// <summary>
/// One reversible editor operation. Commands are pushed onto the <see cref="UndoRedoService"/> either
/// via <see cref="UndoRedoService.Do"/> (service executes them) or — for edits that already happened
/// live, like a gizmo drag — via <see cref="UndoRedoService.PushApplied"/> (recorded without re-running).
/// </summary>
public interface IEditorCommand
{
    /// <summary>Short human label ("Move Mesh", "Edit mat0/diffuseTexture") shown in the Edit menu.</summary>
    string Name { get; }

    /// <summary>
    /// The document/asset this command belongs to (e.g. the MapGeoAsset or bin document instance).
    /// When that document is replaced, its commands are purged so undo can never mutate a stale object.
    /// </summary>
    object? Context { get; }

    /// <summary>Apply the edit (also used for redo). May throw — a failed Execute never enters the stack.</summary>
    void Execute();

    /// <summary>Reverse the edit exactly.</summary>
    void Undo();

    /// <summary>Can <paramref name="next"/> be folded into this command (e.g. retyping the same field)?</summary>
    bool CanMergeWith(IEditorCommand next);

    /// <summary>Fold <paramref name="next"/> into this command (keep this before-state, take its after-state).</summary>
    void MergeWith(IEditorCommand next);
}
