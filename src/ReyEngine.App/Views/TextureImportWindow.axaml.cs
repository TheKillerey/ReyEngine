using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace ReyEngine.App.Views;

/// <summary>M392: how the import dialog closed. The caller needs all three apart, because "copy as-is"
/// is a real import and must not be confused with a cancel.</summary>
public enum TextureImportResult
{
    Cancel,
    Convert,
    CopyAsIs,
}

public partial class TextureImportWindow : Window
{
    public TextureImportWindow() => AvaloniaXamlLoader.Load(this);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(TextureImportResult.Cancel);
    private void OnConvert(object? sender, RoutedEventArgs e) => Close(TextureImportResult.Convert);
    private void OnCopyAsIs(object? sender, RoutedEventArgs e) => Close(TextureImportResult.CopyAsIs);
}
