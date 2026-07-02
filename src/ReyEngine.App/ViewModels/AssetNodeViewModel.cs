using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ReyEngine.Core.Assets;

namespace ReyEngine.App.ViewModels;

public sealed partial class AssetNodeViewModel : ViewModelBase
{
    /// <summary>The backing mount/tree node. Null for virtual nodes (material folders/leaves, M33).</summary>
    public AssetTreeNode? Model { get; }
    public ObservableCollection<AssetNodeViewModel> Children { get; } = new();
    public AssetNodeViewModel? Parent { get; private set; }

    // Virtual-node overrides (used when Model is null).
    private readonly string? _virtualName;
    private readonly bool _virtualIsFolder;

    /// <summary>Set for a virtual material leaf — clicking it opens the Material Editor (M33).</summary>
    public MaterialAssetViewModel? MaterialAsset { get; private init; }
    public bool IsMaterial => MaterialAsset is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private AssetStatus _status = AssetStatus.Original;

    public AssetNodeViewModel(AssetTreeNode model)
    {
        Model = model;
        foreach (var child in model.Children)
            Children.Add(new AssetNodeViewModel(child) { Parent = this });
    }

    private AssetNodeViewModel(string name, bool isFolder)
    {
        _virtualName = name;
        _virtualIsFolder = isFolder;
    }

    /// <summary>A synthetic folder node (no backing entry) for grouping virtual assets.</summary>
    public static AssetNodeViewModel VirtualFolder(string name) => new(name, isFolder: true);

    /// <summary>A synthetic leaf node for a material virtual-asset.</summary>
    public static AssetNodeViewModel MaterialLeaf(MaterialAssetViewModel material) =>
        new(material.Name, isFolder: false) { MaterialAsset = material };

    /// <summary>Attach a child and set its parent (for post-construction virtual grafting).</summary>
    public void AddChild(AssetNodeViewModel child)
    {
        child.Parent = this;
        Children.Add(child);
    }

    /// <summary>Folder children only (for the Content Browser folder tree).</summary>
    public IEnumerable<AssetNodeViewModel> Folders => Children.Where(c => c.IsFolder);
    public bool HasSubfolders => Children.Any(c => c.IsFolder);

    public string Name => Model?.Name ?? _virtualName ?? "";
    public bool IsFolder => Model?.IsFolder ?? _virtualIsFolder;
    public WadAssetEntry? Entry => Model?.Entry;
    public bool IsModified => Status == AssetStatus.Modified;

    public bool IsReadOnly => IsMaterial ? MaterialAsset!.ReadOnly : Entry is { ReadOnly: true };
    public bool HasConflict => Entry is { HasConflict: true };
    public string SourceTag => IsFolder ? "" : IsMaterial
        ? (MaterialAsset!.ReadOnly ? "RIOT" : "PRJ")
        : Entry?.SourceKind switch
        {
            AssetSourceKind.ProjectOverride => "OVR",
            AssetSourceKind.ProjectFolder or AssetSourceKind.ProjectWad => "PRJ",
            AssetSourceKind.RiotReference => "RIOT",
            _ => "",
        };
    public bool HasSourceTag => SourceTag.Length > 0;

    public string Kind => IsFolder ? "DIR" : IsMaterial ? "MAT"
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
        : IsMaterial ? "🎨"
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
