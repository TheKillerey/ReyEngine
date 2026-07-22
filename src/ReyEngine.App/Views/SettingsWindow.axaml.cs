using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using ReyEngine.App.ViewModels;

namespace ReyEngine.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        // Tunnel so a capturing keybind row grabs the key before it triggers anything else.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;

        // ignore modifier-only presses so a binding can't become "just Ctrl"
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                  or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;

        if (e.Key == Key.Escape) { vm.CancelCapture(); e.Handled = true; return; }

        if (vm.AssignCapturedKey(e.Key.ToString())) e.Handled = true;
    }

    // M133: pick the projects folder (where new projects and .fantome imports are created).
    private async void OnBrowseProjectsFolder(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        try
        {
            var start = vm.ProjectsDirectory;
            IStorageFolder? suggested = null;
            if (!string.IsNullOrWhiteSpace(start) && System.IO.Directory.Exists(start))
                suggested = await StorageProvider.TryGetFolderFromPathAsync(start);

            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select the folder where projects should live",
                AllowMultiple = false,
                SuggestedStartLocation = suggested,
            });
            if (picked.FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path)
                vm.ProjectsDirectory = path;
        }
        catch { /* picker cancelled or unavailable */ }
    }

    // M88: pick the legacy LEVELS/<Map> folder used as the preview backdrop.
    private async void OnBrowsePreviewMap(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        try
        {
            var start = vm.PreviewBackgroundMapFolder;
            IStorageFolder? suggested = null;
            if (!string.IsNullOrWhiteSpace(start) && System.IO.Directory.Exists(start))
                suggested = await StorageProvider.TryGetFolderFromPathAsync(start);

            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select a League LEVELS map folder (contains Scene\\room.nvr)",
                AllowMultiple = false,
                SuggestedStartLocation = suggested,
            });
            if (picked.FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path)
                vm.PreviewBackgroundMapFolder = path;
        }
        catch { /* picker cancelled or unavailable */ }
    }
}
