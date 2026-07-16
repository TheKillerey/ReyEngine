using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ReyEngine.App.ViewModels;

namespace ReyEngine.App.Views;

public partial class NewProjectWindow : Window
{
    public NewProjectWindow() => InitializeComponent();

    private async void OnBrowseInstall(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not NewProjectViewModel vm) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the League of Legends 'Game' folder",
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            vm.AddCustomInstall(path);
    }

    private async void OnBrowseLocation(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not NewProjectViewModel vm) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select where to create the project",
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            vm.Location = path;
    }
}
