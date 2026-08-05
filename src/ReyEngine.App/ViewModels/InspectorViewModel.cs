using System.Text;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core.Assets;

namespace ReyEngine.App.ViewModels;

public sealed partial class InspectorViewModel : ViewModelBase
{
    [ObservableProperty] private string _title = "Nothing selected";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _details = "Select an asset in the browser to inspect it.";
    [ObservableProperty] private Bitmap? _previewImage;
    [ObservableProperty] private bool _hasPreview;
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private bool _hasNote;
    [ObservableProperty] private string _assetStatus = "";
    [ObservableProperty] private string _assetSource = "";
    [ObservableProperty] private bool _isModified;

    // ---- M351c: the four header cells (Asset / Type / Source / Size) ----
    // These were all reachable before, but only as prose: the size existed solely inside the monospace
    // Details blob, and the source only as a sentence like "Read-only Riot material". Reading either meant
    // parsing English, so neither could be shown as a field.

    /// <summary>Full virtual path, e.g. <c>assets/maps/.../normal.dds</c>. <see cref="Title"/> is only
    /// the display name, which is ambiguous across folders.</summary>
    [ObservableProperty] private string _assetPath = "";

    /// <summary>Which mount the asset actually came from - project override, project folder/WAD, or the
    /// read-only Riot reference. This is the fact the status sentence was describing.</summary>
    [ObservableProperty] private string _sourceLabel = "";

    /// <summary>Uncompressed size, pre-formatted.</summary>
    [ObservableProperty] private string _sizeLabel = "";

    [ObservableProperty] private bool _hasAsset;

    /// <summary>Host hook for the copy button next to the path (the clipboard lives in the view layer).</summary>
    public Func<string, Task>? CopyHandler { get; set; }

    [RelayCommand]
    private async Task CopyPath()
    {
        if (CopyHandler is { } copy && !string.IsNullOrEmpty(AssetPath)) await copy(AssetPath);
    }

    private static string SourceOf(AssetSourceKind kind) => kind switch
    {
        AssetSourceKind.ProjectOverride => "Project Override",
        AssetSourceKind.ProjectFolder => "Project Folder",
        AssetSourceKind.ProjectWad => "Project WAD",
        AssetSourceKind.RiotReference => "Riot Reference",
        _ => "Unknown",
    };

    public void SetAssetStatus(string status, string? overrideFile)
    {
        AssetStatus = status;
        IsModified = overrideFile is not null;
        AssetSource = overrideFile is not null ? $"Override: {System.IO.Path.GetFileName(overrideFile)}" : "";
        // An override written just now wins over whatever mount the entry was read from, and the header
        // must say so rather than keep reporting the Riot reference it started life as.
        if (overrideFile is not null) SourceLabel = "Project Override";
    }

    public void ShowEntry(WadAssetEntry e)
    {
        Title = e.DisplayName;
        Subtitle = e.Type.ToString();

        // M351c: the same facts as discrete fields, so the header can show them as cells.
        AssetPath = e.Path;
        SourceLabel = SourceOf(e.SourceKind) + (e.ReadOnly ? " (read-only)" : "");
        SizeLabel = Format(e.UncompressedSize);
        HasAsset = true;

        var sb = new StringBuilder();
        sb.AppendLine($"Path          {e.Path}");
        sb.AppendLine($"Hash          0x{e.PathHash:x16}");
        sb.AppendLine($"Type          {e.Type}");
        sb.AppendLine($"Resolved      {(e.IsResolved ? "yes" : "no (unknown hash)")}");
        sb.AppendLine($"Compression   {e.Compression}");
        sb.AppendLine($"Size on disk  {Format(e.CompressedSize)}");
        sb.AppendLine($"Size raw      {Format(e.UncompressedSize)}");
        Details = sb.ToString();
        SetNote("");
    }

    public void SetNote(string note)
    {
        Note = note;
        HasNote = !string.IsNullOrEmpty(note);
    }

    public void SetPreview(Bitmap? bmp)
    {
        PreviewImage = bmp;
        HasPreview = bmp is not null;
    }

    /// <summary>M90: close the texture preview (there was no way back out of it).</summary>
    [RelayCommand] private void ClearPreview() => SetPreview(null);

    public void Clear()
    {
        Title = "Nothing selected";
        Subtitle = "";
        Details = "Select an asset in the browser to inspect it.";
        AssetPath = ""; SourceLabel = ""; SizeLabel = ""; HasAsset = false;
        AssetStatus = ""; AssetSource = ""; IsModified = false;
        SetPreview(null);
        SetNote("");
    }

    private static string Format(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes / (1024.0 * 1024):0.00} MB",
    };
}
