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
    }

    private void OnMapTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (DataContext is not MainWindowViewModel vm) return;
        if ((e.Source as Control)?.DataContext is MapPieceViewModel piece)
        {
            vm.ToggleMeshSelectionFromTree(piece);
            e.Handled = true;
        }
    }
}
