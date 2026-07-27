using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ReyEngine.App.ViewModels;

namespace ReyEngine.App.Views;

/// <summary>M210: the experimental DX11 shader preview. Non-modal, and deliberately separate from the
/// Material Editor — it exists to validate that Riot's compiled shaders load and render correctly before
/// any of that is wired into the real material pipeline.</summary>
public partial class ShaderPreviewWindow : Window
{
    public ShaderPreviewWindow()
    {
        InitializeComponent();

        // double-click a texture slot to point it at an image
        if (this.FindControl<ListBox>("TextureList") is { } list)
            list.DoubleTapped += OnTextureDoubleTapped;

        Closed += (_, _) => (DataContext as ShaderPreviewViewModel)?.Dispose();
    }

    private async void OnTextureDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not ShaderPreviewViewModel vm) return;
        if (sender is not ListBox { SelectedItem: TextureSlotRow row }) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Bind an image to {row.Name}",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Images") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp" } },
            },
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) vm.BindTextureFile(row, path);
    }
}
