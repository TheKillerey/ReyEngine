using System;
using ReyEngine.Core.Undo;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M517: swapping the material on an existing mesh, reversibly.
///
/// <para>The edit is a pending value on the mesh — nothing touches the mapgeo until Save Map Edits — so
/// undo is just putting the previous pending value back, including null for "the material the file
/// names". Same shape as MeshLayerCommand, which is what the visibility edits use.</para>
/// </summary>
public sealed class MeshMaterialCommand : IEditorCommand
{
    private readonly MapGeoMesh _mesh;
    private readonly string? _before;
    private readonly string? _after;
    private readonly Action? _onApplied;

    public MeshMaterialCommand(object? context, MapGeoMesh mesh, string? before, string? after, Action? onApplied)
    {
        Context = context;
        _mesh = mesh;
        _before = before;
        _after = after;
        _onApplied = onApplied;
        Name = after is null ? "Revert Mesh Material" : "Change Mesh Material";
    }

    public string Name { get; }
    public object? Context { get; }

    public void Execute() { _mesh.MaterialEdit = _after; _onApplied?.Invoke(); }
    public void Undo() { _mesh.MaterialEdit = _before; _onApplied?.Invoke(); }

    public bool CanMergeWith(IEditorCommand next) => false;
    public void MergeWith(IEditorCommand next) => throw new NotSupportedException();
}
