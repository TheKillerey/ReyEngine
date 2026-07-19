using Avalonia.Controls;
using ReyEngine.App.ViewModels;

namespace ReyEngine.App.Views;

public partial class FirstRunWindow : Window
{
    public FirstRunWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is FirstRunViewModel vm)
                vm.CloseRequested += Close;
        };
    }
}
