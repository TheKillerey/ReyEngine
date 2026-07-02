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
    public readonly record struct State(Vector3 Offset, Vector3 RotationDegrees, Vector3 Scale)
    {
        public static State Capture(MapGeoMesh mesh) => new(mesh.Offset, mesh.RotationDegrees, mesh.Scale);
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
        _mesh.Offset = s.Offset;
        _mesh.RotationDegrees = s.RotationDegrees;
        _mesh.Scale = s.Scale;
        _map.ApplyMeshTransform(_mesh);
        _onApplied?.Invoke();
    }

    public bool CanMergeWith(IEditorCommand next) => false; // a drag is already one command
    public void MergeWith(IEditorCommand next) => throw new NotSupportedException();
}
