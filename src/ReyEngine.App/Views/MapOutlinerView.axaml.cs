using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using ReyEngine.App.ViewModels;

namespace ReyEngine.App.Views;

/// <summary>
/// The map scene outliner (M33): visibility layers, the layer-group tree, the selected-mesh transform
/// panels, and mesh details — hosted in the Inspector's "Map" tab (relocated from the old Map Content
/// panel). Owns the Ctrl+click multi-select handling for its tree.
/// </summary>
public partial class MapOutlinerView : UserControl
{
    public MapOutlinerView()
    {
        InitializeComponent();
        var tree = this.FindControl<TreeView>("MapContentTree");
        // Ctrl+click a mesh row toggles multi-select — intercept before the TreeView's own single-select.
        tree?.AddHandler(InputElement.PointerPressedEvent, OnMapTreePointerPressed, RoutingStrategies.Tunnel);
        tree?.AddHandler(InputElement.KeyDownEvent, OnMapTreeKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnMapTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        bool toggle = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool range = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (!toggle && !range) return;
        if ((e.Source as Control)?.DataContext is MapOutlinerItemViewModel item)
        {
            vm.SelectMapContentFromTree(item, toggle, range);
            e.Handled = true;
        }
    }

    private static void OnMapTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Delete or Key.X)) return;
        if (sender is not TreeView { DataContext: MainWindowViewModel vm }) return;
        if (vm.DeleteMapContentSelectionCommand.CanExecute(null))
            vm.DeleteMapContentSelectionCommand.Execute(null);
        e.Handled = true;
    }
}
