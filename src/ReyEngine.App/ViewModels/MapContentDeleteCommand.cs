using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ReyEngine.Core.Undo;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M513: deleting map objects, reversibly.
///
/// <para>Deletion was already non-destructive on disk — a map piece is only FLAGGED
/// (<see cref="MapOutlinerItemViewModel.IsRemoved"/>) and nothing is written until the map is saved — but
/// there was no way to un-flag it. The object left the viewport and the outliner and that was that, so a
/// misclick cost whatever it took to rebuild it. Every other map edit (move, layer change, paint stroke)
/// has been on the undo stack for milestones; delete was the one that was not.</para>
///
/// <para>Two kinds of object, and they come back differently: a piece of the loaded map is a flag to
/// clear, while a mesh added this session was REMOVED FROM A LIST and has to go back at its original
/// index — appending would silently reorder the added-mesh list, which is the order the save writes them
/// in.</para>
/// </summary>
public sealed class MapContentDeleteCommand : IEditorCommand
{
    private readonly IReadOnlyList<MapOutlinerItemViewModel> _flagged;
    private readonly IReadOnlyList<(AddedMapMeshViewModel Mesh, int Index)> _added;
    private readonly ObservableCollection<AddedMapMeshViewModel> _addedList;
    private readonly Action? _onApplied;

    public MapContentDeleteCommand(
        IReadOnlyList<MapOutlinerItemViewModel> flagged,
        IReadOnlyList<(AddedMapMeshViewModel Mesh, int Index)> added,
        ObservableCollection<AddedMapMeshViewModel> addedList,
        object? context,
        Action? onApplied)
    {
        _flagged = flagged;
        _added = added;
        _addedList = addedList;
        Context = context;
        _onApplied = onApplied;
        Name = Describe(flagged.Count + added.Count);
    }

    private static string Describe(int count) => count == 1 ? "Delete Map Object" : $"Delete {count} Map Objects";

    public string Name { get; }
    public object? Context { get; }

    public void Execute()
    {
        foreach (var item in _flagged) item.IsRemoved = true;
        // Highest index first, so each removal cannot shift the one after it.
        foreach (var (mesh, _) in _added.OrderByDescending(a => a.Index)) _addedList.Remove(mesh);
        _onApplied?.Invoke();
    }

    public void Undo()
    {
        foreach (var item in _flagged) item.IsRemoved = false;
        // Lowest index first, so each insert lands where it was: re-inserting in reverse would put the
        // later ones in front of the earlier ones.
        foreach (var (mesh, index) in _added.OrderBy(a => a.Index))
            _addedList.Insert(Math.Min(index, _addedList.Count), mesh);
        _onApplied?.Invoke();
    }

    public bool CanMergeWith(IEditorCommand next) => false;
    public void MergeWith(IEditorCommand next) => throw new NotSupportedException();
}
