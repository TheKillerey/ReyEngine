using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace ReyEngine.App.Services;

/// <summary>Wraps the active window's StorageProvider so view-models can pick files.</summary>
public sealed class DialogService
{
    public TopLevel? Owner { get; set; }

    public static FilePickerFileType Wad => new("WAD archive") { Patterns = new[] { "*.wad.client", "*.wad" } };
    public static FilePickerFileType Project => new("ReyEngine project") { Patterns = new[] { "*.reyproject" } };
    public static FilePickerFileType All => new("All files") { Patterns = new[] { "*" } };

    public async Task CopyAsync(string text)
    {
        var clipboard = Owner?.Clipboard;
        if (clipboard is not null) await clipboard.SetTextAsync(text);
    }

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

    /// <summary>M100: multi-file picker for the Content Browser's Import command.</summary>
    public async Task<IReadOnlyList<string>> OpenFilesAsync(string title, params FilePickerFileType[] filters)
    {
        if (Owner is null) return Array.Empty<string>();
        var files = await Owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = filters.Length > 0 ? filters : null,
        });
        return files.Select(f => f.TryGetLocalPath()).Where(p => p is not null).ToList()!;
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
