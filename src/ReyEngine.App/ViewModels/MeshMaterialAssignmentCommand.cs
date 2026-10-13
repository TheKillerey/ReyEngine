using System;
using System.Collections.Generic;
using System.Linq;
using ReyEngine.Core.Undo;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M841: a material assignment from the Blender link, reversibly - the whole-mesh edit
/// (<see cref="MapGeoMesh.MaterialEdit"/>) and the per-submesh edits
/// (<see cref="MapGeoMesh.SubmeshMaterialEdits"/>) put back together, because one assignment can move a mesh
/// from one form to the other. Same shape as <see cref="MeshMaterialCommand"/>: the edit is a pending value
/// on the mesh, written by Save Map Content Edits.
/// </summary>
public sealed class MeshMaterialAssignmentCommand : IEditorCommand
{
    /// <summary>The pending material edits of one mesh.</summary>
    public sealed record State(string? Whole, IReadOnlyDictionary<int, string>? Submeshes)
    {
        public void ApplyTo(MapGeoMesh mesh)
        {
            mesh.MaterialEdit = Whole;
            mesh.SubmeshMaterialEdits = Submeshes is { Count: > 0 } s ? new Dictionary<int, string>(s) : null;
        }

        public bool Equals(State? other) =>
            other is not null && string.Equals(Whole, other.Whole, StringComparison.Ordinal)
            && (Submeshes?.Count ?? 0) == (other.Submeshes?.Count ?? 0)
            && (Submeshes is null || Submeshes.All(kv => other.Submeshes!.TryGetValue(kv.Key, out var v) && v == kv.Value));

        public override int GetHashCode() => HashCode.Combine(Whole, Submeshes?.Count ?? 0);
    }

    public static State Capture(MapGeoMesh mesh) =>
        new(mesh.MaterialEdit, mesh.SubmeshMaterialEdits is { Count: > 0 } s ? new Dictionary<int, string>(s) : null);

    /// <summary>
    /// The simplest edit that draws each submesh with <paramref name="target"/>: nothing when every submesh
    /// keeps what the file names, the whole-mesh edit when they all share one material, otherwise one edit per
    /// submesh that differs from the file.
    /// </summary>
    public static State Normalize(MapGeoMesh mesh, IReadOnlyList<string> target)
    {
        var file = mesh.Materials;
        if (file.Count == 0) return new State(target.Count > 0 && target[0].Length > 0 ? target[0] : null, null);
        bool unchanged = true;
        for (int k = 0; k < file.Count && k < target.Count; k++)
            if (!string.Equals(file[k], target[k], StringComparison.Ordinal)) { unchanged = false; break; }
        if (unchanged) return new State(null, null);
        if (target.Distinct(StringComparer.Ordinal).Count() == 1) return new State(target[0], null);

        var edits = new Dictionary<int, string>();
        for (int k = 0; k < file.Count && k < target.Count; k++)
            if (!string.Equals(file[k], target[k], StringComparison.Ordinal)) edits[k] = target[k];
        return new State(null, edits);
    }

    private readonly MapGeoMesh _mesh;
    private readonly State _before;
    private readonly State _after;
    private readonly Action? _onApplied;

    public MeshMaterialAssignmentCommand(object? context, MapGeoMesh mesh, State before, State after, Action? onApplied)
    {
        Context = context;
        _mesh = mesh;
        _before = before;
        _after = after;
        _onApplied = onApplied;
    }

    public string Name => "Assign Material (Blender)";
    public object? Context { get; }

    public void Execute() { _after.ApplyTo(_mesh); _onApplied?.Invoke(); }
    public void Undo() { _before.ApplyTo(_mesh); _onApplied?.Invoke(); }

    public bool CanMergeWith(IEditorCommand next) => false;
    public void MergeWith(IEditorCommand next) => throw new NotSupportedException();
}
