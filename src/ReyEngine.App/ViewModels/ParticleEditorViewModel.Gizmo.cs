using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using ReyEngine.Formats.Particles;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M753: the particle preview's Move gizmo and the force shapes drawn under it.
///
/// <para><b>What moves.</b> An emitter's <c>EmitterPosition</c> constant, or the <c>Position</c> of a Drag,
/// Noise or Attraction force - the two places the simulator reads an offset from, both relative to the
/// SYSTEM's origin (<c>BasePos = Transform(EmitterPosition, world)</c>, <c>centre = Transform(Position,
/// world)</c>). A force's position is therefore not carried by its emitter: moving an emitter leaves its
/// Drag where it was, which is what the game does with it too.</para>
///
/// <para><b>Only on a rig that stands still.</b> Still and Burst place the system at a fixed lift
/// (<see cref="VfxPreviewRig.Pose"/> is a translation by the rig height), so a world position and a file
/// position differ by that lift and nothing else. A Missile or a Trail moves the origin every frame; a
/// handle there would be dragged against a moving frame, so it is not offered and the note says why.</para>
///
/// <para><b>One edit per drag.</b> The handle and the shapes follow the pointer live; the file is written
/// once, on release, through the same <see cref="EditForce"/> every force edit takes - so a drag is one
/// dirty mark and one preview rebuild, not one per pointer move.</para>
/// </summary>
public sealed partial class ParticleEditorViewModel
{
    private string? _gizmoTarget;
    private Vector3? _dragLocal;

    /// <summary>Where the Move handle stands in the preview, or null for no handle.</summary>
    [ObservableProperty] private Vector3? _gizmoPivot;
    /// <summary>The force shapes as a line list (xyz pairs) for the viewport, or null for none.</summary>
    [ObservableProperty] private float[]? _forceShapeLines;
    /// <summary>Draw the forces' reach in the preview. Preview only.</summary>
    [ObservableProperty] private bool _showForceShapes = true;
    /// <summary>What the handle is on, or why there is none.</summary>
    [ObservableProperty] private string _gizmoNote = "";

    partial void OnShowForceShapesChanged(bool value) => RefreshGizmo();

    /// <summary>The key a handle on an emitter is kept under. Force handles use <see cref="ForceKey"/>.</summary>
    internal static string EmitterKey(int emitterIndex) => $"{emitterIndex}:emitter";

    /// <summary>What the handle is on - an emitter or a force key - or null.</summary>
    public string? GizmoTarget => _gizmoTarget;

    public bool HasGizmoNote => GizmoNote.Length > 0;
    partial void OnGizmoNoteChanged(string value) => OnPropertyChanged(nameof(HasGizmoNote));

    /// <summary>True when the rig holds the system still, which is when a handle means something.</summary>
    public bool RigIsStatic => RigMode is VfxRigMode.Still or VfxRigMode.Burst;

    /// <summary>A still rig's pose is exactly this translation (see <see cref="VfxPreviewRig.Pose"/>).</summary>
    private Vector3 Lift => new(0f, (float)RigHeight, 0f);

    internal bool IsGizmoTarget(string key) => _gizmoTarget == key;

    /// <summary>Put the handle on something, or take it off when it is already there.</summary>
    internal void ToggleGizmoTarget(string key)
    {
        _gizmoTarget = _gizmoTarget == key ? null : key;
        _dragLocal = null;
        RefreshGizmo();
        foreach (var c in Cards)
        {
            c.NotifyGizmo();
            foreach (var f in c.Forces) f.NotifyGizmo();
        }
        OnPropertyChanged(nameof(GizmoTarget));
    }

    /// <summary>Recompute the handle and the shapes from the document. Called after every rebuild of the
    /// preview, so an edit typed into a field moves the handle as well.</summary>
    internal void RefreshGizmo()
    {
        var local = _gizmoTarget is null ? null : TargetPosition(_gizmoTarget, out _);
        if (_gizmoTarget is not null && local is null)
        {
            // the thing it was on is gone - a removed force, another system
            _gizmoTarget = null;
            OnPropertyChanged(nameof(GizmoTarget));
        }
        if (local is not { } at)
        {
            GizmoPivot = null;
            GizmoNote = "";
        }
        else if (!RigIsStatic)
        {
            GizmoPivot = null;
            GizmoNote = "Move works on the Still and Burst rigs - a Missile or a Trail moves the system every frame, so there is no fixed place to drag from.";
        }
        else
        {
            GizmoPivot = (_dragLocal ?? at) + Lift;
            TargetPosition(_gizmoTarget!, out string label);
            GizmoNote = $"Moving {label}. Drag an arrow; the file changes when you let go.";
        }
        ForceShapeLines = ShowForceShapes && RigIsStatic ? BuildForceShapes() : null;
    }

    /// <summary>Follow the pointer: the handle and the shapes move, the file does not.</summary>
    public void DragGizmoTo(Vector3 worldPivot)
    {
        if (GizmoPivot is null || _gizmoTarget is null) return;
        _dragLocal = worldPivot - Lift;
        GizmoPivot = worldPivot;
        ForceShapeLines = ShowForceShapes ? BuildForceShapes() : null;
    }

    /// <summary>The pointer let go: write the new position once.</summary>
    public void EndGizmoDrag()
    {
        if (_dragLocal is not { } local || _gizmoTarget is not { } key) { _dragLocal = null; return; }
        _dragLocal = null;
        if (TargetPosition(key, out _) is { } before && before == local) { RefreshGizmo(); return; }
        if (!TryParseKey(key, out int emitterIndex, out var kind, out int index)) return;
        var card = Cards.FirstOrDefault(c => c.EmitterIndex == emitterIndex);
        if (card is null) { RefreshGizmo(); return; }
        string where = ParticleForceValueViewModel.Format(local, isVector: true);
        if (kind is null)
            EditForce(card, e => e.SetEmitterPosition(local), $"Moved '{card.Name}' to {where}.", structural: false);
        else
            EditForce(card, e => e.SetForceValue(kind.Value, index, "Position", local),
                $"Moved {ParticleForces.Name(kind.Value)} {index + 1} of '{card.Name}' to {where}.", structural: false);
        RefreshGizmo();   // a refused edit (read-only) puts the handle back where the file still has it
    }

    /// <summary>Drop a drag without writing - Escape, or the pointer lost.</summary>
    public void CancelGizmoDrag()
    {
        _dragLocal = null;
        RefreshGizmo();
    }

    private static bool TryParseKey(string key, out int emitterIndex, out ParticleForceKind? kind, out int index)
    {
        kind = null;
        index = -1;
        var parts = key.Split(':');
        if (parts.Length < 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out emitterIndex))
        {
            emitterIndex = -1;
            return false;
        }
        if (parts[1] == "emitter") return true;
        if (parts.Length != 3 || !Enum.TryParse<ParticleForceKind>(parts[1], out var k)
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
            return false;
        kind = k;
        return true;
    }

    /// <summary>Where the keyed thing sits relative to its system, from the live document.</summary>
    private Vector3? TargetPosition(string key, out string label)
    {
        label = "";
        if (SelectedSystem is not { } node || !TryParseKey(key, out int e, out var kind, out int index)) return null;
        if (e < 0 || e >= node.Entry.Emitters.Count) return null;
        var emitter = node.Entry.Emitters[e];
        if (kind is null)
        {
            label = $"emitter '{emitter.Name}'";
            return emitter.EmitterPosition;
        }
        var force = emitter.Forces.FirstOrDefault(f => f.Kind == kind && f.Index == index);
        if (force is not { IsPositional: true }) return null;
        label = $"{force.Title} of '{emitter.Name}'";
        return force.Position;
    }

    /// <summary>Does this force act in the preview - the same rule the Mute and Solo filter applies.</summary>
    private bool ForceActs(string key) =>
        !_mutedForces.Contains(key) && (_soloedForces.Count == 0 || _soloedForces.Contains(key));

    /// <summary>
    /// The shapes of every force that acts in the preview: a wire ball the size of a positional force's
    /// radius at its position, an arrow from the emitter along an Acceleration, and a ring about the axis of
    /// an Orbital. Disabled emitters and muted forces draw nothing, because they do nothing.
    /// </summary>
    private float[]? BuildForceShapes()
    {
        if (SelectedSystem is not { } node) return null;
        var lines = new List<float>();
        var lift = Lift;
        TryParseKey(_gizmoTarget ?? "", out int dragEmitter, out var dragKind, out int dragIndex);
        for (int e = 0; e < node.Entry.Emitters.Count; e++)
        {
            var emitter = node.Entry.Emitters[e];
            if (emitter.Disabled) continue;
            var forces = emitter.Forces;
            if (forces.Count == 0) continue;
            var basePos = (_dragLocal is { } d && dragEmitter == e && dragKind is null ? d : emitter.EmitterPosition) + lift;
            foreach (var f in forces)
            {
                if (!ForceActs(ForceKey(e, f.Kind, f.Index))) continue;
                var pos = (_dragLocal is { } fd && dragEmitter == e && dragKind == f.Kind && dragIndex == f.Index ? fd : f.Position) + lift;
                ParticleForceShapes.Append(lines, f, basePos, pos);
            }
        }
        return lines.Count >= 6 ? lines.ToArray() : null;
    }
}

/// <summary>M753: the line geometry of one force, apart from the view model so a test can pin it.</summary>
public static class ParticleForceShapes
{
    /// <summary>Append the shape of <paramref name="force"/> as xyz line pairs. <paramref name="emitterBase"/>
    /// is where the emitter spawns (Acceleration and Orbital act about it); <paramref name="position"/> is the
    /// force's own centre, used by the three positional kinds.</summary>
    public static void Append(List<float> lines, ParticleForce force, Vector3 emitterBase, Vector3 position)
    {
        switch (force.Kind)
        {
            case ParticleForceKind.Drag or ParticleForceKind.Noise or ParticleForceKind.Attraction:
                lines.AddRange(ReyEngine.Rendering.ViewportMeshRenderer.BuildLightRangeRings(position, force.Radius, 48));
                Cross(lines, position, 12f);
                break;
            case ParticleForceKind.Acceleration:
            {
                var a = force.Values.FirstOrDefault(v => v.Field == "acceleration")?.Value ?? Vector3.Zero;
                float len = a.Length();
                if (len < 1e-4f) break;
                // the arrow's length reads the push's size, clamped so gravity fits the frame and a breeze shows
                Arrow(lines, emitterBase, a / len, Math.Clamp(len, 30f, 300f));
                break;
            }
            case ParticleForceKind.Orbital:
            {
                var spin = force.Values.FirstOrDefault(v => v.Field == "direction")?.Value ?? Vector3.Zero;
                float len = spin.Length();
                if (len < 1e-4f) break;
                var axis = spin / len;
                Ring(lines, emitterBase, axis, 60f, 32);
                Arrow(lines, emitterBase, axis, 60f);
                break;
            }
        }
    }

    private static void Seg(List<float> l, Vector3 a, Vector3 b)
    {
        l.Add(a.X); l.Add(a.Y); l.Add(a.Z);
        l.Add(b.X); l.Add(b.Y); l.Add(b.Z);
    }

    private static void Cross(List<float> l, Vector3 c, float s)
    {
        Seg(l, c - Vector3.UnitX * s, c + Vector3.UnitX * s);
        Seg(l, c - Vector3.UnitY * s, c + Vector3.UnitY * s);
        Seg(l, c - Vector3.UnitZ * s, c + Vector3.UnitZ * s);
    }

    private static (Vector3 U, Vector3 W) Basis(Vector3 axis)
    {
        var u = Vector3.Normalize(MathF.Abs(axis.Y) < 0.99f ? Vector3.Cross(axis, Vector3.UnitY) : Vector3.Cross(axis, Vector3.UnitX));
        return (u, Vector3.Cross(axis, u));
    }

    private static void Arrow(List<float> l, Vector3 from, Vector3 dir, float length)
    {
        var tip = from + dir * length;
        Seg(l, from, tip);
        var (u, w) = Basis(dir);
        float head = MathF.Min(20f, length * 0.3f);
        foreach (var side in new[] { u, -u, w, -w })
            Seg(l, tip, tip - dir * head + side * head * 0.5f);
    }

    private static void Ring(List<float> l, Vector3 centre, Vector3 axis, float radius, int segments)
    {
        var (u, w) = Basis(axis);
        var prev = centre + u * radius;
        for (int i = 1; i <= segments; i++)
        {
            float a = i / (float)segments * MathF.Tau;
            var p = centre + (u * MathF.Cos(a) + w * MathF.Sin(a)) * radius;
            Seg(l, prev, p);
            prev = p;
        }
    }
}
