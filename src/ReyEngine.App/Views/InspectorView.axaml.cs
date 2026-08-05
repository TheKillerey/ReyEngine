using Avalonia.Controls;

namespace ReyEngine.App.Views;

/// <summary>M351a: the asset inspector (Overview / Materials / Raw BIN Tree). Extracted from
/// MainWindow.axaml so the v0.3.0 redesign has somewhere to live; DataContext is inherited from the
/// window, so every binding still resolves against MainWindowViewModel exactly as before.</summary>
public partial class InspectorView : UserControl
{
    public InspectorView()
    {
        InitializeComponent();
    }
}
