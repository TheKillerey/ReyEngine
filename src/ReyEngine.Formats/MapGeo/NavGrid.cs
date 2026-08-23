using System.Numerics;

namespace ReyEngine.Formats.MapGeo;

/// <summary>
/// M562: League's navigation grid — <c>assets/maps/navgrid/&lt;map&gt;/aipath.aimesh_ngrid</c>.
///
/// <para>This is where the GAMEPLAY bush lives, as opposed to the swaying foliage, which is ordinary
/// geometry on a VertexDeform material. The two are unrelated: the foliage is art and the grid is what
/// the game blocks vision with.</para>
/// </summary>
public sealed class NavGrid
{
    /// <summary>u8 major + u16 minor + 6 floats of bounds + f32 cell size + 2 u32 counts.</summary>
    public const int HeaderBytes = 39;

    /// <summary>
    /// Size of one entry in the cell section that precedes the flag plane.
    ///
    /// <para>Derived rather than documented: the plane was first located on Map11 by scanning, at byte
    /// 4,191,399, and <c>4,191,399 - 39 = 4,191,360 = 87,320 cells x 48</c> exactly. The same arithmetic
    /// then lands on a valid plane in every other shipped map, so the section really is a fixed 48 bytes
    /// per cell and the offset does not have to be searched for at load.</para>
    /// </summary>
    public const int CellRecordBytes = 48;

    public byte VersionMajor { get; private init; }
    public ushort VersionMinor { get; private init; }
    public Vector3 Min { get; private init; }
    public Vector3 Max { get; private init; }
    /// <summary>World units per cell. 50 on every shipped map measured.</summary>
    public float CellSize { get; private init; }
    public int CountX { get; private init; }
    public int CountZ { get; private init; }

    /// <summary>One bitmask per cell, row-major with X fastest. Empty when the file carried no plane.</summary>
    public ushort[] Flags { get; private init; } = Array.Empty<ushort>();

    /// <summary>
    /// Per-cell ground height — the FIRST float of each 48-byte cell record.
    ///
    /// <para>Measured rather than documented: on Summoner's Rift it runs -71.2 to 184.3 across 26,545
    /// distinct values, and the grid's own Y bounds are -71.2 to 184.5. An exact match to the header is
    /// what identifies it.</para>
    ///
    /// <para>Not every map fills it. Map453's grid is a STUB — flat Y bounds, every height zero — so
    /// <see cref="HasHeights"/> is how a caller tells "the bush is at sea level" from "this grid does not
    /// know where the ground is".</para>
    /// </summary>
    public float[] Heights { get; private init; } = Array.Empty<float>();

    /// <summary>False when the grid carries no usable ground height, which several maps do not.</summary>
    public bool HasHeights { get; private init; }

    public int CellCount => CountX * CountZ;
    public bool HasFlags => Flags.Length == CellCount && CellCount > 0;

    // ---------------------------------------------------------------- M568: what the bits are
    //
    // Identified by DRAWING each one over a map and looking, after M562 got it wrong by reasoning from
    // cluster shapes. The layer overlay exists precisely so this could be done by observation:
    //
    //   bit 0  0x0001  BUSH - the vision blocker. 2,015 cells on Summoner's Rift.
    //   bit 1  0x0002  not walkable. Fills the border, 39% of SR.
    //   bit 2  0x0004  blue side only.
    //   bit 3  0x0008  both teams' restricted areas.
    //   bit 6  0x0040  the outline of the not-walkable area.
    //   bit 9  0x0200  another not-walkable region, covering one area; purpose unclear.
    //   bit 10 0x0400  blue side only.
    //   bit 11 0x0800  red side only.
    //   bit 12 0x1000  much the same as bit 3.
    //
    // Note how close together 2, 3, 10, 11 and 12 are - several overlapping team restrictions - which is
    // why picking one from its distribution was never going to work. Bit 7 (31% of SR) is unlabelled: it
    // was not reported on, and guessing it is exactly the mistake this comment exists to record.

    /// <summary>
    /// The bush - the volume that actually blocks vision.
    ///
    /// <para>M562 guessed 0x0004 from the shape of its clusters and was wrong; it is blue-side-only
    /// walking. This one was identified by drawing the layer over a map and looking at it.</para>
    /// </summary>
    public const ushort BushFlag = 0x0001;

    /// <summary>Blue side only. What M562 mistook for the bush.</summary>
    public const ushort TeamRestrictedFlag = 0x0004;

    /// <summary>Fills the map border and covers 39% of Summoner's Rift, so: not walkable.</summary>
    public const ushort BlockedFlag = 0x0002;

    /// <summary>A human label for a flag, or null when nobody has identified it yet.</summary>
    public static string? LabelFor(ushort mask) => mask switch
    {
        0x0001 => "bush",
        0x0002 => "not walkable",
        0x0004 => "blue side only",
        0x0008 => "team restricted (both)",
        0x0040 => "not-walkable outline",
        0x0200 => "not walkable (one area)",
        0x0400 => "blue side only",
        0x0800 => "red side only",
        0x1000 => "team restricted (both)",
        _ => null,
    };

    /// <summary>
    /// Every single-bit flag that appears anywhere in this grid, with how many cells carry it, commonest
    /// first. This is what the editor turns into toggleable layers.
    /// </summary>
    public IReadOnlyList<(ushort Mask, int Cells)> PresentFlags()
    {
        var found = new List<(ushort, int)>();
        if (!HasFlags) return found;
        for (int bit = 0; bit < 16; bit++)
        {
            ushort mask = (ushort)(1 << bit);
            int n = CountWith(mask);
            if (n > 0) found.Add((mask, n));
        }
        found.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return found;
    }

    public bool Has(int x, int z, ushort mask)
    {
        if (!HasFlags || (uint)x >= (uint)CountX || (uint)z >= (uint)CountZ) return false;
        return (Flags[z * CountX + x] & mask) != 0;
    }

    /// <summary>
    /// World-space box of one cell. Y comes from the cell's own recorded ground height where the grid has
    /// one, and falls back to the grid's floor where it does not.
    /// </summary>
    public (Vector3 Min, Vector3 Max) CellBounds(int x, int z)
    {
        float y = HasHeights && (uint)x < (uint)CountX && (uint)z < (uint)CountZ
            ? Heights[z * CountX + x]
            : Min.Y;
        var lo = new Vector3(Min.X + x * CellSize, y, Min.Z + z * CellSize);
        return (lo, new Vector3(lo.X + CellSize, Math.Max(y, Max.Y), lo.Z + CellSize));
    }

    /// <summary>Every cell carrying <paramref name="mask"/>, as world-space boxes.</summary>
    public IEnumerable<(Vector3 Min, Vector3 Max)> CellsWith(ushort mask)
    {
        if (!HasFlags) yield break;
        for (int z = 0; z < CountZ; z++)
            for (int x = 0; x < CountX; x++)
                if ((Flags[z * CountX + x] & mask) != 0)
                    yield return CellBounds(x, z);
    }

    public int CountWith(ushort mask)
    {
        if (!HasFlags) return 0;
        int n = 0;
        foreach (ushort v in Flags) if ((v & mask) != 0) n++;
        return n;
    }

    /// <summary>The WAD path the grid lives at, for a map folder name like <c>map11</c>.</summary>
    public static string PathFor(string mapFolder) =>
        $"assets/maps/navgrid/{mapFolder.ToLowerInvariant()}/aipath.aimesh_ngrid";

    /// <summary>
    /// Reads the header and the per-cell flag plane. Returns false rather than throwing on anything that
    /// does not look like a grid — this is opened opportunistically alongside a map, so a miss must be
    /// quiet.
    /// </summary>
    public static bool TryParse(byte[] data, out NavGrid? grid, out string? error)
    {
        grid = null;
        error = null;
        try
        {
            if (data is null || data.Length < HeaderBytes) { error = "shorter than a navgrid header"; return false; }
            var r = new BinaryReader(new MemoryStream(data, writable: false));
            byte major = r.ReadByte();
            ushort minor = r.ReadUInt16();
            var min = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var max = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            float cellSize = r.ReadSingle();
            int countX = (int)r.ReadUInt32();
            int countZ = (int)r.ReadUInt32();

            // Only version 7 has been measured. Refusing an unknown major is the honest behaviour: the
            // flag-plane offset below is derived from v7's layout and would silently read noise on another.
            if (major != 7) { error = $"navgrid version {major}.{minor} is not one this reader has measured"; return false; }
            if (countX <= 0 || countZ <= 0 || (long)countX * countZ > 4_000_000)
            { error = $"implausible grid {countX}x{countZ}"; return false; }
            if (!(cellSize > 0f) || !float.IsFinite(cellSize)) { error = $"implausible cell size {cellSize}"; return false; }

            long cells = (long)countX * countZ;
            long flagsAt = HeaderBytes + cells * CellRecordBytes;
            var flags = Array.Empty<ushort>();
            if (flagsAt + cells * 2 <= data.Length)
            {
                flags = new ushort[cells];
                for (long i = 0; i < cells; i++)
                    flags[i] = BitConverter.ToUInt16(data, (int)(flagsAt + i * 2));
            }
            // A grid whose plane runs past the end still parses - the header alone is useful - but it
            // reports no flags rather than pretending.

            // The first float of each 48-byte cell record is the ground height (see Heights). Read only
            // when the section is fully present, and only trusted when it actually varies - a grid of
            // identical zeroes is a stub, not a flat map.
            var heights = Array.Empty<float>();
            bool hasHeights = false;
            if (HeaderBytes + cells * CellRecordBytes <= data.Length)
            {
                heights = new float[cells];
                float first = 0f;
                bool varies = false, finite = true;
                for (long i = 0; i < cells; i++)
                {
                    float h = BitConverter.ToSingle(data, (int)(HeaderBytes + i * CellRecordBytes));
                    heights[i] = h;
                    if (!float.IsFinite(h)) { finite = false; break; }
                    if (i == 0) first = h; else if (h != first) varies = true;
                }
                hasHeights = finite && varies;
                if (!hasHeights) heights = Array.Empty<float>();
            }

            grid = new NavGrid
            {
                VersionMajor = major, VersionMinor = minor,
                Min = min, Max = max, CellSize = cellSize,
                CountX = countX, CountZ = countZ, Flags = flags,
                Heights = heights, HasHeights = hasHeights,
            };
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }
}
