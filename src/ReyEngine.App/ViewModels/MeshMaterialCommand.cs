using System;
using System.Collections.Generic;
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
    private readonly IReadOnlyDictionary<int, string>? _submeshesBefore;

    /// <param name="submeshesBefore">M841: the per-submesh edits (from the Blender link) the mesh had before. The picker
    /// assigns the WHOLE mesh, so applying clears them and undo puts them back.</param>
    public MeshMaterialCommand(object? context, MapGeoMesh mesh, string? before, string? after, Action? onApplied,
        IReadOnlyDictionary<int, string>? submeshesBefore = null)
    {
        Context = context;
        _mesh = mesh;
        _before = before;
        _after = after;
        _onApplied = onApplied;
        _submeshesBefore = submeshesBefore is { Count: > 0 } b ? new Dictionary<int, string>(b) : null;
        Name = after is null ? "Revert Mesh Material" : "Change Mesh Material";
    }

    public string Name { get; }
    public object? Context { get; }

    public void Execute() { _mesh.MaterialEdit = _after; _mesh.SubmeshMaterialEdits = null; _onApplied?.Invoke(); }
    public void Undo()
    {
        _mesh.MaterialEdit = _before;
        _mesh.SubmeshMaterialEdits = _submeshesBefore is null ? null : new Dictionary<int, string>(_submeshesBefore);
        _onApplied?.Invoke();
    }

    public bool CanMergeWith(IEditorCommand next) => false;
    public void MergeWith(IEditorCommand next) => throw new NotSupportedException();
}
