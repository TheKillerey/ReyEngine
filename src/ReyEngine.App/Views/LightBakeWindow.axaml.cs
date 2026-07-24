using Avalonia.Controls;
using ReyEngine.App.ViewModels;

namespace ReyEngine.App.Views;

/// <summary>M158: the Light Baking window — tune bake settings, run the bake, watch progress.</summary>
public partial class LightBakeWindow : Window
{
    public LightBakeWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is LightBakeViewModel vm)
        {
            vm.CloseRequested += Close;
            vm.Refresh();
        }
    }
}
