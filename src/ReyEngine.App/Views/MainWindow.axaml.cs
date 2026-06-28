using Avalonia.Controls;
using ReyEngine.App.ViewModels;

namespace ReyEngine.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is MainWindowViewModel vm)
            vm.Dialogs.Owner = this;
    }
}
