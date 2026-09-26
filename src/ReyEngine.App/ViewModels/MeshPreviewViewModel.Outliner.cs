using CommunityToolkit.Mvvm.ComponentModel;

using System;
namespace ReyEngine.App.ViewModels;

/// <summary>
/// M643: the character window's left panel - the champion picker and the submesh outliner.
///
/// <para>The picker is M610's browser view model, embedded rather than rewritten: the host creates it
/// once and lists the install again when the game folder changes. The outliner is the submesh list the
/// window has had since M84, grown up: each row names the material it draws with, and selecting a row
/// outlines the submesh in the viewport and opens that material in the Material tab. Which material a
/// submesh draws with is the skin bin's own answer - its materialOverride entry, else the default - read
/// from the same document the editor edits, so the two cannot disagree.</para>
/// </summary>
public sealed partial class MeshPreviewViewModel
{
    /// <summary>The champion / character / skin picker, supplied by the host. Null in a bare view model.</summary>
    [ObservableProperty] private CharacterBrowserViewModel? _browser;

    /// <summary>The outliner's selected row.</summary>
    [ObservableProperty] private SubmeshToggleViewModel? _selectedSubmesh;

    /// <summary>The submesh indices the GL viewport outlines - the selected row, or nothing. The D3D11
    /// surface has no outline pass; there the selection shows only in the Material tab.</summary>
    [ObservableProperty] private IReadOnlyList<int>? _highlightedSubmeshes;

    partial void OnSelectedSubmeshChanged(SubmeshToggleViewModel? value)
    {
        HighlightedSubmeshes = value is null ? null : new[] { value.Index };
        if (value is null || !HasMaterialEditor) return;
        if (MaterialFor(value.Name) is not { } material) return;

        // A search that hides the material would leave the selector showing nothing selected.
        if (MaterialEditor.Search.Length > 0 && !MaterialEditor.FilteredMaterials.Contains(material))
            MaterialEditor.Search = "";
        MaterialEditor.SelectedMaterial = material;
        PanelTab = MaterialTab;
    }

    /// <summary>The material a submesh draws with: the one whose materialOverride names it, else the skin's
    /// default. Null when the skin's materials have not loaded, or name neither.</summary>
    public MaterialBindingViewModel? MaterialFor(string submesh) =>
        MaterialEditor.Materials.FirstOrDefault(m => m.Model.Submeshes.Any(s => s.Equals(submesh, StringComparison.OrdinalIgnoreCase)))
        ?? MaterialEditor.Materials.FirstOrDefault(m => m.Model.IsDefault);

    /// <summary>Re-derive every row's material name from the editor, and drop a selection that no longer
    /// exists. Called when the mesh changes and when the skin's materials arrive or leave.</summary>
    public void RefreshOutliner()
    {
        foreach (var row in Submeshes)
        {
            row.MaterialName = HasMaterialEditor ? MaterialFor(row.Name)?.Name ?? "" : "";
            // M703: its OWN material, not the skin default it falls back to - only the first can have
            // its shader changed, and only the second is what a character with no materials shows.
            row.HasOwnMaterial = HasMaterialEditor && MaterialEditor.Materials.Any(m =>
                m.Model.Submeshes.Any(sub => sub.Equals(row.Name, StringComparison.OrdinalIgnoreCase)));
        }
        if (SelectedSubmesh is { } selected && !Submeshes.Contains(selected)) SelectedSubmesh = null;
        // M704: the Materials tab offers the same step for the submeshes that have no material of their
        // own, because that tab is where a person finds out the default block has no shader to change.
        MaterialEditor.SetAddableSubmeshes(Submeshes.Where(r => !r.HasOwnMaterial).Select(r => r.Name));
        // M775: the checklist/reorder editors for initialSubmeshToHide/submeshRenderOrder need this
        // mesh's real submesh names, not just whatever the field already lists.
        MaterialEditor.SetKnownSubmeshNames(Submeshes.Select(r => r.Name));
    }
}
