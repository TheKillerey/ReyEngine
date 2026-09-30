using ReyEngine.Formats.Vfx;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M800: what the Particle Editor says about a system authored in Riot's component-based ("Shimmer") VFX
/// format, added in 16.16. ReyEngine does not simulate it, so such a system draws nothing - which, without a
/// sentence saying so, reads as "HoL_26_CubeGrid does not load". The wording comes from
/// <see cref="VfxComponentEmitters"/>, the same source the D3D11 status line and the raw-tree coverage notes
/// take theirs from; this half only decides WHERE it is shown and keeps it current as the document changes.
///
/// <para>The data stays editable exactly as before: the Shimmer list and the classic entries' <c>VfxComponents</c>
/// block are ordinary rows of the raw tree, and editing them re-extracts the definitions like any other edit -
/// which is also what keeps this note current when a <c>disabled</c> flag is flipped.</para>
/// </summary>
public sealed partial class ParticleEditorViewModel
{
    /// <summary>The selected system's component-format counts, or null when it uses none (or nothing is
    /// selected). Read from the same re-extracted definitions the preview plays, so an edit shows here at once.</summary>
    public VfxComponentEmitters? SelectedComponentEmitters =>
        SelectedSystem is { } node && _defs.TryGetValue(node.Entry.PathHash, out var def)
            && def.ComponentEmitters is { Any: true } components
            ? components
            : null;

    /// <summary>The note for the system card, or null when the selected system does not use the component format.</summary>
    public string? ComponentNote => SelectedComponentEmitters?.Note;

    public bool HasComponentNote => SelectedComponentEmitters is not null;

    /// <summary>The selected system was re-extracted or changed: its counts may have.</summary>
    private void NotifyComponentNote()
    {
        OnPropertyChanged(nameof(ComponentNote));
        OnPropertyChanged(nameof(HasComponentNote));
        foreach (var c in Cards) c.NotifyComponentNote();
    }
}
