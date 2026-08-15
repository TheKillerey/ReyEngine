using Avalonia.Controls;

namespace ReyEngine.App.Views;

/// <summary>M492: view and edit the second UV set. Non-modal — it is a diagnostic as much as an editor,
/// and it is meant to stay open next to the viewport while meshes are selected.</summary>
public partial class UvEditorWindow : Window
{
    public UvEditorWindow()
    {
        InitializeComponent();
    }
}
