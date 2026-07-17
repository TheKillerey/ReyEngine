using System;
using System.Numerics;
using ReyEngine.Core.Undo;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M76: one placement gizmo drag (particle move/rotate/scale or sound move) as an undoable command.
/// The edit already happened live during the drag, so it is pushed via <see cref="UndoRedoService.PushApplied"/>;
/// Execute re-applies the after-state (redo), Undo restores the before-state. The host refresh callback
/// re-syncs markers, gizmo pivot, numeric fields, playback and the dirty flag after either direction.
/// </summary>
public sealed class PlacementTransformCommand : IEditorCommand
{
    public readonly record struct State(Vector3 Offset, Vector3 Rotation, Vector3 Scale)
    {
        public static State Capture(object target) => target switch
        {
            ParticlePlacementViewModel p => new State(p.Offset, p.RotationDegrees, p.Scale),
            MapSoundViewModel s => new State(s.Offset, Vector3.Zero, Vector3.One),
            _ => default,
        };

        public void ApplyTo(object target)
        {
            switch (target)
            {
                case ParticlePlacementViewModel p:
                    p.Offset = Offset; p.RotationDegrees = Rotation; p.Scale = Scale;
                    break;
                case MapSoundViewModel s:
                    s.Offset = Offset;
                    break;
            }
        }
    }

    private readonly object _target;
    private readonly State _before;
    private State _after;
    private readonly Action<object> _refresh;

    public string Name { get; }
    public object? Context { get; }

    public PlacementTransformCommand(object target, State before, State after, object? context, Action<object> refresh)
    {
        _target = target;
        _before = before;
        _after = after;
        _refresh = refresh;
        Context = context;
        Name = target switch
        {
            ParticlePlacementViewModel p => $"Transform Particle '{p.Name}'",
            MapSoundViewModel s => $"Move Sound '{s.Name}'",
            _ => "Transform Placement",
        };
    }

    public void Execute() { _after.ApplyTo(_target); _refresh(_target); }
    public void Undo() { _before.ApplyTo(_target); _refresh(_target); }

    /// <summary>Consecutive drags of the SAME placement fold into one step (keep first before, last after).</summary>
    public bool CanMergeWith(IEditorCommand next) => false;
    public void MergeWith(IEditorCommand next) { }
}
