using Avalonia.Controls;
using ReyEngine.App.ViewModels;

namespace ReyEngine.App.Views;

public partial class MapBinEditorWindow : Window
{
    public MapBinEditorWindow()
    {
        InitializeComponent();
    }

    private void OnObjectSelection(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is TreeView tv && tv.SelectedItem is MapBinObjectViewModel obj
            && DataContext is MapBinEditorViewModel vm)
            vm.SelectedObject = obj;
    }
}
