using System;
using ReyEngine.App.Services;
using ReyEngine.Core.Undo;

namespace ReyEngine.App.ViewModels;

/// <summary>M172c: one brush stroke, undoable.
///
/// The stroke has already been applied by the time this is constructed — the user watched it appear —
/// so it goes onto the stack via <c>PushApplied</c> and Execute() is the REDO path.
///
/// State is stored as 64x64 texel tiles rather than whole textures. A painting session is hundreds of
/// strokes and a 2048² RGBA copy is 16 MiB, so whole-texture snapshots would cost gigabytes; a dab
/// touches at most a handful of tiles (16 KiB each).
///
/// Strokes deliberately do NOT merge. Each drag is one undoable unit, which is what every paint tool
/// does and what a user expects from Ctrl+Z mid-session.</summary>
public sealed class PaintStrokeCommand : IEditorCommand
{
    private readonly PaintStrokeRecord _record;
    private readonly Action<PaintStrokeRecord> _refresh;

    public PaintStrokeCommand(PaintStrokeRecord record, object? context, Action<PaintStrokeRecord> refresh)
    {
        _record = record;
        Context = context;
        _refresh = refresh;
    }

    public string Name => _record.Entries.Count == 1
        ? "Paint " + System.IO.Path.GetFileName(_record.Entries[0].AssetPath)
        : $"Paint {_record.Entries.Count} textures";

    public object? Context { get; }

    public void Execute() { _record.Redo(); _refresh(_record); }
    public void Undo() { _record.Undo(); _refresh(_record); }

    public bool CanMergeWith(IEditorCommand next) => false;
    public void MergeWith(IEditorCommand next) => throw new NotSupportedException();
}
