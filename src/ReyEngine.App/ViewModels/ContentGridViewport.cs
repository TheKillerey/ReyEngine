namespace ReyEngine.App.ViewModels;

/// <summary>
/// <para>M807: which tiles of the Content Browser's grid are in view, worked out from the tile size - the grid is a plain
/// <c>WrapPanel</c> of fixed cells (no virtualization: up to 4000 tiles are real controls), so the row a tile is on and the
/// window the scroll viewer shows are enough to name the visible items without asking a single control where it is.</para>
///
/// <para>The constants are the tile button in <c>MainWindow.axaml</c> (<c>Width="94" Height="86" Margin="4"</c>); a test pins the
/// markup to them, so changing one without the other fails loudly instead of quietly lighting the wrong tiles.</para>
/// </summary>
public static class ContentGridViewport
{
    public const double TileWidth = 94, TileHeight = 86, TileMargin = 4;

    /// <summary>One cell: the tile and the margin on each side of it.</summary>
    public const double CellWidth = TileWidth + 2 * TileMargin, CellHeight = TileHeight + 2 * TileMargin;

    /// <summary>Rows kept above and below the window, so a tile one scroll step away is already drawn when it arrives.</summary>
    public const int OverscanRows = 1;

    /// <summary>Indices (inclusive) of the items to treat as visible; <c>First &gt; Last</c> when there are none.</summary>
    /// <param name="offsetY">How far the grid is scrolled down.</param>
    /// <param name="contentWidth">The width the tiles wrap in (the grid's own width).</param>
    /// <param name="viewportHeight">The height of the window the scroll viewer shows.</param>
    public static (int First, int Last) VisibleRange(int itemCount, double offsetY, double contentWidth, double viewportHeight)
    {
        if (itemCount <= 0 || !(contentWidth > 0) || !(viewportHeight > 0)) return (0, -1);

        int columns = Math.Max(1, (int)Math.Floor(contentWidth / CellWidth));
        double top = Math.Max(0, offsetY);
        int firstRow = Math.Max(0, (int)Math.Floor(top / CellHeight) - OverscanRows);
        int lastRow = (int)Math.Floor((top + viewportHeight) / CellHeight) + OverscanRows;

        long first = (long)firstRow * columns;
        long last = ((long)lastRow + 1) * columns - 1;
        if (first >= itemCount) return (0, -1);
        return ((int)first, (int)Math.Min(last, itemCount - 1));
    }
}
