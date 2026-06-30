using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ReyEngine.Core.Assets;

namespace ReyEngine.App.ViewModels;

public sealed partial class AssetNodeViewModel : ViewModelBase
{
    public AssetTreeNode Model { get; }
    public ObservableCollection<AssetNodeViewModel> Children { get; } = new();
    public AssetNodeViewModel? Parent { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private AssetStatus _status = AssetStatus.Original;

    public AssetNodeViewModel(AssetTreeNode model)
    {
        Model = model;
        foreach (var child in model.Children)
            Children.Add(new AssetNodeViewModel(child) { Parent = this });
    }

    /// <summary>Folder children only (for the Content Browser folder tree).</summary>
    public IEnumerable<AssetNodeViewModel> Folders => Children.Where(c => c.IsFolder);
    public bool HasSubfolders => Children.Any(c => c.IsFolder);

    public string Name => Model.Name;
    public bool IsFolder => Model.IsFolder;
    public WadAssetEntry? Entry => Model.Entry;
    public bool IsModified => Status == AssetStatus.Modified;

    public bool IsReadOnly => Entry is { ReadOnly: true };
    public bool HasConflict => Entry is { HasConflict: true };
    public string SourceTag => IsFolder ? "" : Entry?.SourceKind switch
    {
        AssetSourceKind.ProjectOverride => "OVR",
        AssetSourceKind.ProjectFolder or AssetSourceKind.ProjectWad => "PRJ",
        AssetSourceKind.RiotReference => "RIOT",
        _ => "",
    };
    public bool HasSourceTag => SourceTag.Length > 0;

    public string Kind => IsFolder
        ? "DIR"
        : Entry?.Type switch
        {
            AssetType.Texture or AssetType.Dds or AssetType.Image => "IMG",
            AssetType.SkinnedMesh or AssetType.StaticMesh => "MSH",
            AssetType.Skeleton => "SKL",
            AssetType.Animation => "ANM",
            AssetType.MapGeometry => "MAP",
            AssetType.Bin => "BIN",
            AssetType.Audio => "SND",
            AssetType.Shader => "FX",
            AssetType.Json or AssetType.Text => "TXT",
            AssetType.Unknown => "?",
            _ => "BIN",
        };

    /// <summary>Type glyph shown in the Content Browser (folder + per-file-type icons).</summary>
    public string Icon => IsFolder
        ? (HasSubfolders ? "📂" : "📁")
        : Entry?.Type switch
        {
            AssetType.Texture or AssetType.Dds or AssetType.Image => "🖼",
            AssetType.SkinnedMesh or AssetType.StaticMesh => "🧊",
            AssetType.Skeleton => "🦴",
            AssetType.Animation => "🎞",
            AssetType.MapGeometry => "🗺",
            AssetType.Bin => "📦",
            AssetType.Audio => "🔊",
            AssetType.Shader => "✨",
            AssetType.Json or AssetType.Text => "📄",
            _ => "📄",
        };
}
