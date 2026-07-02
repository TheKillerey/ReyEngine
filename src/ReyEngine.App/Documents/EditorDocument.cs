using CommunityToolkit.Mvvm.ComponentModel;
using ReyEngine.Core.Assets;

namespace ReyEngine.App.Documents;

public enum DocumentKind { Map, Mesh, Texture, Bin, Material, Animation, Other }

/// <summary>
/// One open editor document = one asset the user opened, shown as a viewport/inspector tab (M33). Heavy
/// scenes (maps) cache their full viewport state in <see cref="Scene"/> so switching tabs restores them
/// without re-decoding (preserving edits/selection/visibility); lighter assets re-load on activation.
/// </summary>
public sealed partial class EditorDocument : ObservableObject
{
    public required string Title { get; init; }
    public required DocumentKind Kind { get; init; }
    public ulong Key { get; init; }               // identity (entry path hash)
    public WadAssetEntry? Entry { get; init; }

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isDirty;

    /// <summary>Cached viewport scene (boxed by the host) so this document restores without a reload.</summary>
    public object? Scene { get; set; }

    public string Glyph => Kind switch
    {
        DocumentKind.Map => "🗺",
        DocumentKind.Mesh => "🧊",
        DocumentKind.Texture => "🖼",
        DocumentKind.Bin => "📦",
        DocumentKind.Material => "🎨",
        DocumentKind.Animation => "🎞",
        _ => "📄",
    };

    public static DocumentKind KindOf(AssetType type) => type switch
    {
        AssetType.MapGeometry => DocumentKind.Map,
        AssetType.SkinnedMesh or AssetType.StaticMesh => DocumentKind.Mesh,
        AssetType.Texture or AssetType.Dds or AssetType.Image => DocumentKind.Texture,
        AssetType.Bin => DocumentKind.Bin,
        AssetType.Animation => DocumentKind.Animation,
        _ => DocumentKind.Other,
    };
}
