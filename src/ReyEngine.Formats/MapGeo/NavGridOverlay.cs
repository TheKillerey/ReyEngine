namespace ReyEngine.Formats.MapGeo;

/// <summary>
/// M843: a <c>.ngrid_overlay</c> - the cells a map STATE (a dragon terrain, a baron pit shape, an Atakhan pit)
/// changes in the navigation grid, as shipped beside the grid (<c>assets/maps/navgrid/navgrid_*.ngrid_overlay</c>).
///
/// <para><b>Layout</b>, measured on all eight Map11 overlays (every file parses to within one byte of its end):
/// <c>u8 version (1)</c>, <c>u8 blockCount</c>, then per block <c>i32 x, i32 z, i32 width, i32 height</c> (cell
/// coordinates in the base grid's lattice) and <c>width*height</c> little-endian <c>u16</c> flag words, row-major
/// with X fastest; one trailing byte follows the last block (0, or 1 in the mountain, ocean and tunnel files; its meaning is unknown).</para>
///
/// <para><b>What a block does</b> is an interpretation, not a documented fact: the words come from the same
/// vocabulary as the base grid's flag plane (0x0002 blocked, 0x0042 outline, 0x0080, 0x00c2 ...), and against
/// Map11's grid the ocean overlay turns 0x0000 / 0x0080 cells into 0x0001 / 0x0081 (the ocean terrain adds bush),
/// the chemtech overlay turns 0x0082 into 0x0002 (a wall opens). So the editor shows an overlay by REPLACING the
/// base cell's flag word inside each block. Riot's own handling in game is not emulated beyond that.</para>
/// </summary>
public sealed class NavGridOverlay
{
    public readonly record struct Block(int X, int Z, int Width, int Height, ushort[] Flags);

    public byte Version { get; private init; }
    public IReadOnlyList<Block> Blocks { get; private init; } = Array.Empty<Block>();
    /// <summary>The byte after the last block (0, or 1 in three shipped files); kept, never interpreted.</summary>
    public byte Trailer { get; private init; }

    public int CellCount => Blocks.Sum(b => b.Flags.Length);

    public static bool TryParse(byte[] data, out NavGridOverlay? overlay, out string? error)
    {
        overlay = null; error = null;
        try
        {
            if (data is null || data.Length < 2) { error = "shorter than an overlay header"; return false; }
            byte version = data[0];
            if (version != 1) { error = $"navgrid overlay version {version} is not one this reader has measured"; return false; }
            int count = data[1];
            long at = 2;
            var blocks = new List<Block>(count);
            for (int i = 0; i < count; i++)
            {
                if (at + 16 > data.Length) { error = $"block {i} runs past the end"; return false; }
                int x = BitConverter.ToInt32(data, (int)at), z = BitConverter.ToInt32(data, (int)at + 4);
                int w = BitConverter.ToInt32(data, (int)at + 8), h = BitConverter.ToInt32(data, (int)at + 12);
                at += 16;
                if (x < 0 || z < 0 || x > 65535 || z > 65535 || w <= 0 || h <= 0 || w > 4096 || h > 4096)
                { error = $"block {i} has an implausible rectangle ({x},{z} {w}x{h})"; return false; }
                long bytes = (long)w * h * 2;
                if (at + bytes > data.Length) { error = $"block {i} ({w}x{h}) runs past the end"; return false; }
                var flags = new ushort[w * h];
                for (int k = 0; k < flags.Length; k++) flags[k] = BitConverter.ToUInt16(data, (int)at + k * 2);
                at += bytes;
                blocks.Add(new Block(x, z, w, h, flags));
            }
            long rest = data.Length - at;
            if (rest > 1) { error = $"{rest} unexpected byte(s) after the last block"; return false; }
            overlay = new NavGridOverlay { Version = version, Blocks = blocks, Trailer = rest == 1 ? data[at] : (byte)0 };
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>
    /// The base grid with this overlay's blocks written over its flag plane. Cells outside the base lattice are skipped
    /// and counted in <paramref name="outside"/>; <paramref name="changed"/> counts cells whose word actually differs.
    /// </summary>
    public NavGrid ApplyTo(NavGrid grid, out int changed, out int outside)
    {
        ArgumentNullException.ThrowIfNull(grid);
        changed = 0; outside = 0;
        if (!grid.HasFlags) return grid;
        var flags = (ushort[])grid.Flags.Clone();
        foreach (var b in Blocks)
            for (int j = 0; j < b.Height; j++)
                for (int i = 0; i < b.Width; i++)
                {
                    long cx = (long)b.X + i, cz = (long)b.Z + j;
                    if (cx < 0 || cz < 0 || cx >= grid.CountX || cz >= grid.CountZ) { outside++; continue; }
                    int at = (int)(cz * grid.CountX + cx);
                    ushort v = b.Flags[j * b.Width + i];
                    if (flags[at] != v) { flags[at] = v; changed++; }
                }
        return grid.WithFlags(flags);
    }
}
