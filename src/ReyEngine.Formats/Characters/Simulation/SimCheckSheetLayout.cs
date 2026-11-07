namespace ReyEngine.Formats.Characters.Simulation;

/// <summary>Where everything of a contact sheet goes, in sheet pixels. Pure geometry: the App draws into it.</summary>
public sealed record SheetLayout(int Width, int Height, int Rows, int Columns, int HeaderHeight, int RowHeight, int LabelWidth,
    int CellWidth, int CellHeight, int Gap)
{
    public PixelRect Header => new(0, 0, Width, HeaderHeight);
    public PixelRect RowRect(int row) => new(0, HeaderHeight + row * RowHeight, Width, RowHeight);
    public PixelRect LabelRect(int row) => new(Gap, HeaderHeight + row * RowHeight + Gap, LabelWidth - Gap, RowHeight - 2 * Gap);
    /// <summary>The picture of a column of a row (the caption sits under it, in the remaining height of the row).</summary>
    public PixelRect CellRect(int row, int column) =>
        new(LabelWidth + column * (CellWidth + Gap), HeaderHeight + row * RowHeight + Gap, CellWidth, CellHeight);
    public PixelRect CaptionRect(int row, int column) =>
        new(LabelWidth + column * (CellWidth + Gap), HeaderHeight + row * RowHeight + Gap + CellHeight,
            CellWidth, RowHeight - CellHeight - 2 * Gap);
}

/// <summary>M871: the contact sheet of one champion - one row per ability, one column per sampled frame.</summary>
public static class SimCheckSheetLayout
{
    public const int DefaultMaxWidth = 1600;

    /// <summary>
    /// Fit <paramref name="rows"/> x <paramref name="columns"/> cells of aspect <paramref name="frameAspect"/> (width / height) into a sheet
    /// no wider than <paramref name="maxWidth"/>. The label column and the gaps are fixed; the cells take the rest, so a sheet is at most
    /// <paramref name="maxWidth"/> wide however many frames a row has.
    /// </summary>
    public static SheetLayout Compute(int rows, int columns, double frameAspect, int maxWidth = DefaultMaxWidth, int labelWidth = 190,
        int gap = 4, int headerHeight = 34, int captionHeight = 34)
    {
        rows = Math.Max(0, rows);
        columns = Math.Max(1, columns);
        if (!(frameAspect > 0.1) || double.IsNaN(frameAspect) || double.IsInfinity(frameAspect)) frameAspect = 16.0 / 9.0;
        int cellWidth = Math.Max(16, (maxWidth - labelWidth - gap * columns) / columns);
        int cellHeight = Math.Max(9, (int)Math.Round(cellWidth / frameAspect));
        int rowHeight = cellHeight + captionHeight + 2 * gap;
        int width = labelWidth + columns * (cellWidth + gap);
        return new SheetLayout(width, headerHeight + rows * rowHeight, rows, columns, headerHeight, rowHeight, labelWidth, cellWidth, cellHeight, gap);
    }
}
