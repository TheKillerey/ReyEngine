using CommunityToolkit.Mvvm.ComponentModel;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M642: the character's materials, edited in the character window.
///
/// <para>The window that shows the model is the window that edits its materials. Before this the skin
/// bin loaded into the MAIN window's inspector - a window away from the character, and one whose
/// material editor was overwritten the moment a map mesh was clicked - and with D3D11 on, an edit
/// there changed nothing on screen at all, because the D3D11 scene was built once at load and never
/// again. This editor is a second <see cref="MaterialEditorViewModel"/>, wired by the host to the same
/// thumbnail, catalogue, undo and override-save hooks as the map's, and its live preview rebuilds BOTH
/// renderers' view of the skin.</para>
/// </summary>
public sealed partial class MeshPreviewViewModel
{
    /// <summary>The skin bin's materials. Empty until a champion skin loads; a prop or a legacy map has none.</summary>
    public MaterialEditorViewModel MaterialEditor { get; } = new();

    /// <summary>Whether the Material tab has anything to show. False hides the tab rather than showing an
    /// empty editor with a "no materials" line, which is what a prop would otherwise get.</summary>
    [ObservableProperty] private bool _hasMaterialEditor;

    /// <summary>Which of the window's panel tabs is up: 0 Material, 1 Animate, 2 Play, 3 Scene.</summary>
    [ObservableProperty] private int _panelTab = 1;

    public const int MaterialTab = 0, AnimateTab = 1, PlayTab = 2, SceneTab = 3;

    /// <summary>The window's title. It is the Character Editor while it holds a skin with materials, and
    /// the Model Preview it always was for everything else it can show - props, meshes, legacy maps.</summary>
    public string WindowTitle => HasMaterialEditor ? "ReyEngine — Character Editor" : "ReyEngine — Model Preview";

    partial void OnHasMaterialEditorChanged(bool value)
    {
        OnPropertyChanged(nameof(WindowTitle));
        // A hidden tab must not stay selected: Avalonia shows the empty content of a collapsed TabItem.
        if (!value && PanelTab == MaterialTab) PanelTab = AnimateTab;
    }

    /// <summary>Called by the host when a skin's materials arrive (or leave). Arriving materials bring the
    /// Material tab to the front, because that is what was just asked for.</summary>
    public void ShowMaterials(bool has)
    {
        HasMaterialEditor = has;
        if (has) PanelTab = MaterialTab;
    }
}
