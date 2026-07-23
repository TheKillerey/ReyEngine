using Avalonia.Controls;
using Avalonia.Data.Converters;
using ReyEngine.App.ViewModels;

namespace ReyEngine.App.Views;

/// <summary>M140: the HUD Editor window — element tree, 2D canvas render, inspector.</summary>
public partial class HudEditorWindow : Window
{
    public HudEditorWindow()
    {
        InitializeComponent();
        Canvas.ElementPicked += hash =>
        {
            if (DataContext is HudEditorViewModel vm)
            {
                if (hash == 0) vm.ClearSelectionCommand.Execute(null);
                else vm.SelectByHash(hash);
            }
        };
    }

    private void OnFitView(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Canvas.FitView();
}

/// <summary>Small value converters for the HUD editor.</summary>
public static class HudConverters
{
    /// <summary>true (dimmed / disabled element) → 0.45 opacity, false → 1.0.</summary>
    public static readonly IValueConverter DimOpacity =
        new FuncValueConverter<bool, double>(dim => dim ? 0.45 : 1.0);
}
