using System.Text;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ReyEngine.Core.Assets;

namespace ReyEngine.App.ViewModels;

public sealed partial class InspectorViewModel : ViewModelBase
{
    [ObservableProperty] private string _title = "Nothing selected";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _details = "Select an asset in the browser to inspect it.";
    [ObservableProperty] private Bitmap? _previewImage;
    [ObservableProperty] private bool _hasPreview;

    public void ShowEntry(WadAssetEntry e)
    {
        Title = e.DisplayName;
        Subtitle = e.Type.ToString();

        var sb = new StringBuilder();
        sb.AppendLine($"Path          {e.Path}");
        sb.AppendLine($"Hash          0x{e.PathHash:x16}");
        sb.AppendLine($"Type          {e.Type}");
        sb.AppendLine($"Resolved      {(e.IsResolved ? "yes" : "no (unknown hash)")}");
        sb.AppendLine($"Compression   {e.Compression}");
        sb.AppendLine($"Size on disk  {Format(e.CompressedSize)}");
        sb.AppendLine($"Size raw      {Format(e.UncompressedSize)}");
        Details = sb.ToString();
    }

    public void SetPreview(Bitmap? bmp)
    {
        PreviewImage = bmp;
        HasPreview = bmp is not null;
    }

    public void Clear()
    {
        Title = "Nothing selected";
        Subtitle = "";
        Details = "Select an asset in the browser to inspect it.";
        SetPreview(null);
    }

    private static string Format(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes / (1024.0 * 1024):0.00} MB",
    };
}
