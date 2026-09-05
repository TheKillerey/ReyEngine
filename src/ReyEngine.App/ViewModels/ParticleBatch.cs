using System.Globalization;
using System.Numerics;
using ReyEngine.Core.Undo;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M644: every pending edit on one particle placement, as a value - so a batch edit can record exactly
/// what each placement looked like before and after, and undo puts each one back precisely.
///
/// <para>Transform AND the verbs. The single-placement fields edit live and only the transform was
/// undoable (PlacementTransformCommand); a batch that re-links twenty placements must come back as one
/// step, and "one step" has to restore every field it touched, including the ones ResetEdits clears.</para>
/// </summary>
public readonly record struct PlacementEditSnapshot(
    Vector3 Offset, Vector3 Rotation, Vector3 Scale,
    string? Name, string? Tint, uint SystemHash, int? VisibilityFlags,
    bool Removed, bool Disabled, bool EditorVisible)
{
    public static PlacementEditSnapshot Capture(ParticlePlacementViewModel p) => new(
        p.Offset, p.RotationDegrees, p.Scale,
        p.EditedName, p.EditedTint, p.EditedSystemHash, p.EditedVisibilityFlags,
        p.IsRemoved, p.IsDisabled, p.IsEditorVisible);

    /// <summary>Put the placement back into this state. Each observable is assigned only when it differs,
    /// so the change notifications (and the host's state handler behind them) fire for real changes only.</summary>
    public void ApplyTo(ParticlePlacementViewModel p)
    {
        p.Offset = Offset; p.RotationDegrees = Rotation; p.Scale = Scale;
        if (p.EditedName != Name) p.EditedName = Name;
        if (p.EditedTint != Tint) p.EditedTint = Tint;
        if (p.EditedSystemHash != SystemHash) p.EditedSystemHash = SystemHash;
        if (p.EditedVisibilityFlags != VisibilityFlags) p.EditedVisibilityFlags = VisibilityFlags;
        if (p.IsRemoved != Removed) p.IsRemoved = Removed;
        if (p.IsDisabled != Disabled) p.IsDisabled = Disabled;
        if (p.IsEditorVisible != EditorVisible) p.IsEditorVisible = EditorVisible;
    }
}

/// <summary>M644: one placement's edits, before and after, as an undoable step.</summary>
public sealed class PlacementEditsCommand : IEditorCommand
{
    private readonly ParticlePlacementViewModel _target;
    private readonly PlacementEditSnapshot _before, _after;
    private readonly Action _refresh;

    public PlacementEditsCommand(ParticlePlacementViewModel target, PlacementEditSnapshot before,
        PlacementEditSnapshot after, object? context, Action refresh, string? name = null)
    {
        _target = target; _before = before; _after = after; _refresh = refresh;
        Context = context;
        Name = name ?? $"Edit {target.Name}";
    }

    public string Name { get; }
    public object? Context { get; }
    public void Execute() { _after.ApplyTo(_target); _refresh(); }
    public void Undo() { _before.ApplyTo(_target); _refresh(); }
    public bool CanMergeWith(IEditorCommand next) => false;
    public void MergeWith(IEditorCommand next) => throw new NotSupportedException();
}

/// <summary>
/// M644: the shared inspector's arithmetic for a selection of particle placements - what they have in
/// common, and the edits that write all of them as ONE undo step.
///
/// <para>Unity's rule, as chosen for this editor: a field shows its value when every selected placement
/// agrees, "mixed" when they differ, and an edit writes every one of them. The edits here apply LIVE,
/// the way the single-placement fields do, and return the composite that records them; the host pushes
/// it as applied. Pure over the view models so it can be tested without a map.</para>
/// </summary>
public static class ParticleBatch
{
    public sealed record Summary(
        int Count,
        IReadOnlyList<uint> SystemHashes,
        uint? SharedSystemHash,
        string? SharedTintText,
        bool TintMixed,
        int Edited)
    {
        public string Status =>
            $"{Count} particles selected · {SystemHashes.Count} system{(SystemHashes.Count == 1 ? "" : "s")}"
            + (Edited > 0 ? $" · {Edited} with pending edits" : "");
    }

    public static Summary Summarize(IReadOnlyList<ParticlePlacementViewModel> nodes)
    {
        var systems = nodes.Select(n => n.EffectiveSystemHash).Distinct().ToList();
        var tints = nodes.Select(n => TintText(n.EffectiveTint)).Distinct(StringComparer.Ordinal).ToList();
        return new Summary(nodes.Count, systems,
            systems.Count == 1 ? systems[0] : null,
            tints.Count == 1 ? tints[0] : null,
            tints.Count > 1,
            nodes.Count(n => n.HasEdits));
    }

    /// <summary>A tint as the inspector types it: four numbers, InvariantCulture (this machine runs a
    /// German locale, where "0,5" would split into two components). An authored-default tint is "".</summary>
    public static string TintText(Vector4? tint) => tint is { } t
        ? string.Format(CultureInfo.InvariantCulture, "{0:0.###}, {1:0.###}, {2:0.###}, {3:0.###}", t.X, t.Y, t.Z, t.W)
        : "";

    /// <summary>Empty clears the tint back to authored; otherwise it must parse as four numbers.</summary>
    public static bool IsValidTint(string? text) =>
        string.IsNullOrWhiteSpace(text) || ParticlePlacementViewModel.ParseTint(text) is not null;

    /// <summary>Point every placement at <paramref name="systemHash"/>. A placement whose AUTHORED system
    /// that already is gets its edit cleared rather than a no-op re-link, exactly as the single-placement
    /// picker does.</summary>
    public static CompositeCommand Relink(IReadOnlyList<ParticlePlacementViewModel> nodes, uint systemHash, object? context, Action refresh) =>
        Batch(nodes, $"Re-link {nodes.Count} Particles", n => n.EditedSystemHash = systemHash == n.Placement.SystemHash ? 0u : systemHash, context, refresh);

    public static CompositeCommand Tint(IReadOnlyList<ParticlePlacementViewModel> nodes, string? tintText, object? context, Action refresh) =>
        Batch(nodes, $"Tint {nodes.Count} Particles", n => n.EditedTint = string.IsNullOrWhiteSpace(tintText) ? null : tintText.Trim(), context, refresh);

    public static CompositeCommand Reset(IReadOnlyList<ParticlePlacementViewModel> nodes, object? context, Action refresh) =>
        Batch(nodes, $"Reset {nodes.Count} Particles", n => n.ResetEdits(), context, refresh);

    /// <summary>Apply <paramref name="edit"/> to every placement now, and return the one command that
    /// undoes all of it. Placements the edit did not change contribute nothing, so undoing a re-link to a
    /// system half the selection already used touches only the half that moved.</summary>
    public static CompositeCommand Batch(IReadOnlyList<ParticlePlacementViewModel> nodes, string name,
        Action<ParticlePlacementViewModel> edit, object? context, Action refresh)
    {
        var commands = new List<IEditorCommand>();
        foreach (var node in nodes)
        {
            var before = PlacementEditSnapshot.Capture(node);
            edit(node);
            var after = PlacementEditSnapshot.Capture(node);
            if (before != after) commands.Add(new PlacementEditsCommand(node, before, after, context, refresh, name));
        }
        return new CompositeCommand(name, commands, context);
    }

    /// <summary>How many placements a composite from <see cref="Batch"/> actually changed.</summary>
    public static int ChangedCount(CompositeCommand command) => command.Count;
}
