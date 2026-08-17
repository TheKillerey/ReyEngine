using System.Collections.ObjectModel;
using ReyEngine.App.ViewModels;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M513: deleting map objects has to be reversible.
///
/// <para>It was already non-destructive on disk — a map piece is only flagged and nothing is written until
/// the map is saved — but nothing could clear the flag, so a misclick cost whatever it took to rebuild the
/// object. Every other map edit had been on the undo stack for milestones.</para>
/// </summary>
public sealed class MapContentDeleteCommandTests
{
    private static AddedMapMeshViewModel Mesh(string name) => new()
    {
        Name = name,
        Positions = new float[3],
        Normals = new float[3],
        Uvs = new float[2],
        Indices = new[] { 0, 0, 0 },
        Material = "mat",
        LocalCenter = System.Numerics.Vector3.Zero,
    };

    [Fact]
    public void UndoPutsAnAddedMeshBackWhereItWas()
    {
        // Order matters: the added-mesh list is the order the save writes them in, so appending on undo
        // would quietly reshuffle the map.
        var list = new ObservableCollection<AddedMapMeshViewModel> { Mesh("a"), Mesh("b"), Mesh("c") };
        var middle = list[1];

        var command = new MapContentDeleteCommand(
            Array.Empty<MapOutlinerItemViewModel>(),
            new[] { (Mesh: middle, Index: 1) }, list, context: null, onApplied: null);

        command.Execute();
        Assert.Equal(new[] { "a", "c" }, list.Select(m => m.Name));

        command.Undo();
        Assert.Equal(new[] { "a", "b", "c" }, list.Select(m => m.Name));
        Assert.Same(middle, list[1]);
    }

    [Fact]
    public void SeveralAddedMeshesComeBackInTheirOriginalOrder()
    {
        var list = new ObservableCollection<AddedMapMeshViewModel>
            { Mesh("a"), Mesh("b"), Mesh("c"), Mesh("d") };
        var b = list[1];
        var d = list[3];

        var command = new MapContentDeleteCommand(
            Array.Empty<MapOutlinerItemViewModel>(),
            new[] { (Mesh: b, Index: 1), (Mesh: d, Index: 3) }, list, context: null, onApplied: null);

        command.Execute();
        Assert.Equal(new[] { "a", "c" }, list.Select(m => m.Name));

        command.Undo();
        Assert.Equal(new[] { "a", "b", "c", "d" }, list.Select(m => m.Name));
    }

    [Fact]
    public void RedoDeletesAgain()
    {
        var list = new ObservableCollection<AddedMapMeshViewModel> { Mesh("a"), Mesh("b") };
        var command = new MapContentDeleteCommand(
            Array.Empty<MapOutlinerItemViewModel>(),
            new[] { (Mesh: list[0], Index: 0) }, list, context: null, onApplied: null);

        command.Execute();
        command.Undo();
        command.Execute();      // the undo service re-runs Execute for redo
        Assert.Equal(new[] { "b" }, list.Select(m => m.Name));
    }

    [Fact]
    public void TheRefreshRunsOnBothDirections()
    {
        // A viewport that refreshes on delete but not on undo shows a mesh that is no longer deleted.
        int refreshes = 0;
        var list = new ObservableCollection<AddedMapMeshViewModel> { Mesh("a") };
        var command = new MapContentDeleteCommand(
            Array.Empty<MapOutlinerItemViewModel>(),
            new[] { (Mesh: list[0], Index: 0) }, list, context: null, onApplied: () => refreshes++);

        command.Execute();
        Assert.Equal(1, refreshes);
        command.Undo();
        Assert.Equal(2, refreshes);
    }

    [Fact]
    public void TheNameSaysHowMuchIsBeingDeleted()
    {
        var list = new ObservableCollection<AddedMapMeshViewModel> { Mesh("a"), Mesh("b") };
        Assert.Equal("Delete Map Object", new MapContentDeleteCommand(
            Array.Empty<MapOutlinerItemViewModel>(), new[] { (Mesh: list[0], Index: 0) },
            list, null, null).Name);
        Assert.Equal("Delete 2 Map Objects", new MapContentDeleteCommand(
            Array.Empty<MapOutlinerItemViewModel>(),
            new[] { (Mesh: list[0], Index: 0), (Mesh: list[1], Index: 1) }, list, null, null).Name);
    }
}
