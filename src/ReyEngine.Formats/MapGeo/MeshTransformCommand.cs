using System.Numerics;
using ReyEngine.Core.Undo;

namespace ReyEngine.Formats.MapGeo;

/// <summary>
/// Reversible mapgeo mesh transform: captures the full (Offset, Rotation, Scale) state before and
/// after an edit and swaps between them. One gizmo drag = one command (state captured at press and
/// release, not per pointer-move). Covers translate, rotate, scale and reset uniformly.
/// </summary>
public sealed class MeshTransformCommand : IEditorCommand
{
    public readonly record struct State(Vector3 Offset, Vector3 RotationDegrees, Vector3 Scale, Matrix4x4 GroupMatrix)
    {
        public static State Capture(MapGeoMesh mesh) => new(mesh.Offset, mesh.RotationDegrees, mesh.Scale, mesh.GroupMatrix);

        public void ApplyTo(MapGeoMesh mesh)
        {
            mesh.Offset = Offset;
            mesh.RotationDegrees = RotationDegrees;
            mesh.Scale = Scale;
            mesh.GroupMatrix = GroupMatrix;
        }
    }

    private readonly MapGeoAsset _map;
    private readonly MapGeoMesh _mesh;
    private readonly State _before;
    private State _after;
    private readonly Action? _onApplied;   // UI sync (viewport re-upload, fields, highlight) — runs after Execute AND Undo

    public MeshTransformCommand(string name, MapGeoAsset map, MapGeoMesh mesh, State before, State after, Action? onApplied)
    {
        Name = name;
        _map = map;
        _mesh = mesh;
        _before = before;
        _after = after;
        _onApplied = onApplied;
    }

    public string Name { get; }
    public object? Context => _map;
    public MapGeoMesh Mesh => _mesh;

    public void Execute() => Apply(_after);
    public void Undo() => Apply(_before);

    private void Apply(State s)
    {
        s.ApplyTo(_mesh);
        _map.ApplyMeshTransform(_mesh);
        _onApplied?.Invoke();
    }

    public bool CanMergeWith(IEditorCommand next) => false; // a drag is already one command
    public void MergeWith(IEditorCommand next) => throw new NotSupportedException();
}

/// <summary>
/// A single reversible undo step for a BATCH transform: captures the full transform state of every
/// affected mesh before and after, and restores/reapplies all of them together. One batch move/rotate/
/// scale/reset = one entry on the undo stack (the spec's "undo restores every mesh exactly").
/// </summary>
public sealed class BatchTransformCommand : IEditorCommand
{
    private readonly MapGeoAsset _map;
    private readonly (MapGeoMesh mesh, MeshTransformCommand.State before, MeshTransformCommand.State after)[] _entries;
    private readonly Action? _onApplied;

    public BatchTransformCommand(string name, MapGeoAsset map,
        IEnumerable<(MapGeoMesh mesh, MeshTransformCommand.State before, MeshTransformCommand.State after)> entries,
        Action? onApplied)
    {
        Name = name;
        _map = map;
        _entries = entries.ToArray();
        _onApplied = onApplied;
    }

    public string Name { get; }
    public object? Context => _map;
    public int Count => _entries.Length;

    /// <summary>True when at least one mesh's state actually changed (callers skip a no-op push).</summary>
    public bool HasChange => _entries.Any(e => e.before != e.after);

    public void Execute() { foreach (var e in _entries) ApplyOne(e.mesh, e.after); _onApplied?.Invoke(); }
    public void Undo() { foreach (var e in _entries) ApplyOne(e.mesh, e.before); _onApplied?.Invoke(); }

    private void ApplyOne(MapGeoMesh mesh, MeshTransformCommand.State s)
    {
        s.ApplyTo(mesh);
        _map.ApplyMeshTransform(mesh);
    }

    public bool CanMergeWith(IEditorCommand next) => false;
    public void MergeWith(IEditorCommand next) => throw new NotSupportedException();
}

/// <summary>
/// M105: reversible layer-system edit — captures each selected mesh's pending
/// (VisibilityEdit, ControllerEdit, BackfaceEdit) before/after one UI action.
/// </summary>
public sealed class MeshLayerCommand : IEditorCommand
{
    public readonly record struct State(int? Visibility, uint? Controller, bool? Backface)
    {
        public static State Capture(MapGeoMesh m) => new(m.VisibilityEdit, m.ControllerEdit, m.BackfaceEdit);
        public void ApplyTo(MapGeoMesh m)
        {
            m.VisibilityEdit = Visibility;
            m.ControllerEdit = Controller;
            m.BackfaceEdit = Backface;
        }
    }

    private readonly MapGeoAsset _map;
    private readonly IReadOnlyList<(MapGeoMesh Mesh, State Before, State After)> _entries;
    private readonly Action? _onApplied;

    public MeshLayerCommand(string name, MapGeoAsset map,
        IReadOnlyList<(MapGeoMesh Mesh, State Before, State After)> entries, Action? onApplied)
    {
        Name = name;
        _map = map;
        _entries = entries;
        _onApplied = onApplied;
    }

    public string Name { get; }
    public object? Context => _map;

    public void Execute() { foreach (var e in _entries) e.After.ApplyTo(e.Mesh); _onApplied?.Invoke(); }
    public void Undo() { foreach (var e in _entries) e.Before.ApplyTo(e.Mesh); _onApplied?.Invoke(); }

    public bool CanMergeWith(IEditorCommand next) => false;
    public void MergeWith(IEditorCommand next) => throw new NotSupportedException();
}
