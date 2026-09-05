using Avalonia.Controls;

namespace ReyEngine.App.Views;

/// <summary>
/// M642: the material editor as a control of its own, bound to a <see cref="ViewModels.MaterialEditorViewModel"/>.
///
/// <para>Lifted byte for byte out of the inspector's Materials tab, where it had been since M351e with
/// every binding prefixed <c>MaterialEditor.</c> against the main window's view model. That prefix was
/// the only thing tying it to ONE editor instance; without it the same markup serves the map inspector
/// and the character window, which each own an editor now.</para>
/// </summary>
public partial class MaterialEditorView : UserControl
{
    public MaterialEditorView()
    {
        InitializeComponent();
    }
}
