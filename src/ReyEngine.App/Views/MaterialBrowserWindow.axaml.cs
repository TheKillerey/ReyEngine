using Avalonia.Controls;

namespace ReyEngine.App.Views;

/// <summary>M503b: every material in the open map with what uses it and what is wrong with it. Non-modal —
/// it is a triage list you keep open beside the viewport while fixing what it points at.</summary>
public partial class MaterialBrowserWindow : Window
{
    public MaterialBrowserWindow()
    {
        InitializeComponent();
    }
}
