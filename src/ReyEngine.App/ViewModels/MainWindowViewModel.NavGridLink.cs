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
}
