using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace ReyEngine.App.Services;

/// <summary>Wraps the active window's StorageProvider so view-models can pick files.</summary>
public sealed class DialogService
{
    public TopLevel? Owner { get; set; }

    public static FilePickerFileType Wad => new("WAD archive") { Patterns = new[] { "*.wad.client", "*.wad" } };
    public static FilePickerFileType All => new("All files") { Patterns = new[] { "*" } };

    public async Task<string?> OpenFileAsync(string title, params FilePickerFileType[] filters)
    {
        if (Owner is null) return null;
        var files = await Owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = filters.Length > 0 ? filters : null,
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> OpenFolderAsync(string title)
    {
        if (Owner is null) return null;
        var folders = await Owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName)
    {
        if (Owner is null) return null;
        var file = await Owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
        });
        return file?.TryGetLocalPath();
    }
}
