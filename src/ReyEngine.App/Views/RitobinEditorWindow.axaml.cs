using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ReyEngine.App.ViewModels;

namespace ReyEngine.App.Views;

/// <summary>
/// M498: edit a .bin as ritobin text. Non-modal — it is meant to sit open beside the inspector while the
/// same bin is looked at from both sides.
///
/// <para>Import/Export are driven from here rather than the view model because they need this window's own
/// StorageProvider; everything that touches the bin itself lives in the view model.</para>
/// </summary>
public partial class RitobinEditorWindow : Window
{
    private static readonly FilePickerFileType RitobinText =
        new("ritobin text") { Patterns = new[] { "*.py", "*.txt" } };

    public RitobinEditorWindow()
    {
        InitializeComponent();
        ExportButton.Click += OnExport;
        ImportButton.Click += OnImport;
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RitobinEditorViewModel vm) return;
        await vm.ExportAsync(async (title, suggested) =>
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggested,
                FileTypeChoices = new[] { RitobinText },
            });
            return file?.TryGetLocalPath();
        });
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RitobinEditorViewModel vm) return;
        await vm.ImportAsync(async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import ritobin text",
                AllowMultiple = false,
                FileTypeFilter = new[] { RitobinText },
            });
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        });
    }
}
