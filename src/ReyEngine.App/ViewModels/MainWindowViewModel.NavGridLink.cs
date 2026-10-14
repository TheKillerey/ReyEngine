using ReyEngine.Core.Assets;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M842: the open map's navgrid for the Blender link - shown in Blender the way the editor's overlay shows it,
/// never edited. The layers (one per single-bit flag present, commonest first), their labels, colours and
/// default visibility all come from the editor, so the add-on hard-codes none of them.
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>The navgrid file the overlay was read from (null when the map ships none).</summary>
    private WadAssetEntry? _navGridEntry;

    private Formats.Interop.BridgeNavGrid NavGridForBlender()
    {
        var none = Array.Empty<Formats.Interop.BridgeNavLayer>();
        if (_currentMap is null) return new(false, "", "", null, none, "no map is open");
        if (_navGrid is not { } grid || _navGridEntry is not { } entry)
            return new(false, "", "", null, none, "this map ships no navgrid (or it is not a version 7 grid)");
        string source = entry.SourceKind switch
        {
            AssetSourceKind.RiotReference => "game",
            AssetSourceKind.LtkGameData => "gamedata",
            _ => "project",
        };
        // ToggleNavGridOverlay: switching the overlay on with no layer chosen shows the commonest one
        var layers = NavGridLayers.Select((l, i) => new Formats.Interop.BridgeNavLayer(
            l.Mask, l.Label, l.Color, l.Cells, ShowBushAreas && l.IsVisible, i == 0)).ToList();
        return new(true, entry.Path, source, grid, layers, null);
    }
}
