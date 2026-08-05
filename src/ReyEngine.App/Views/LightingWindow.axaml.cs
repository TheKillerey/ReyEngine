using Avalonia.Controls;

namespace ReyEngine.App.Views;

/// <summary>M169: the Lighting panel as a real resizable window instead of a 240px flyout column.
/// Binds straight to MainWindowViewModel — every value here is live viewport state, so edits show
/// immediately in the viewport behind it and there is nothing to apply or confirm.</summary>
public partial class LightingWindow : Window
{
    public LightingWindow()
    {
        InitializeComponent();
    }
}
