using System.Collections.ObjectModel;
using ReyEngine.Core.Assets;

namespace ReyEngine.App.ViewModels;

public sealed class AssetNodeViewModel : ViewModelBase
{
    public AssetTreeNode Model { get; }
    public ObservableCollection<AssetNodeViewModel> Children { get; } = new();

    public AssetNodeViewModel(AssetTreeNode model)
    {
        Model = model;
        foreach (var child in model.Children)
            Children.Add(new AssetNodeViewModel(child));
    }

    public string Name => Model.Name;
    public bool IsFolder => Model.IsFolder;
    public WadAssetEntry? Entry => Model.Entry;

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
}
