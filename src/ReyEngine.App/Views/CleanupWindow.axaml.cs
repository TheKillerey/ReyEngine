using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ReyEngine.App.Views;

/// <summary>M302: Cleanup Project — scan, preview, select, move to the project's recycle area.</summary>
public partial class CleanupWindow : Window
{
    public CleanupWindow()
    {
        InitializeComponent();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
