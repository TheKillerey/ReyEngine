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
    internal const int HeaderBytes = 39;

    /// <summary>
    /// Size of one entry in the cell section that precedes the flag plane.
    ///
    /// <para>Derived rather than documented: the plane was first located on Map11 by scanning, at byte
    /// 4,191,399, and <c>4,191,399 - 39 = 4,191,360 = 87,320 cells x 48</c> exactly. The same arithmetic
    /// then lands on a valid plane in every other shipped map, so the section really is a fixed 48 bytes
    /// per cell and the offset does not have to be searched for at load.</para>
    /// </summary>
    internal const int CellRecordBytes = 48;

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

    public int CellCount => CountX * CountZ;
    public bool HasFlags => Flags.Length == CellCount && CellCount > 0;

    /// <summary>
    /// The bush bit.
    ///
    /// <para>Strongly supported rather than documented, and the maps are their own control: on Summoner's
    /// Rift it marks 1,085 cells (1.2%) in about 25 small clusters laid out with the diagonal symmetry SR's
    /// brush has, while Howling Abyss and TFT — which have no brush at all — score exactly ZERO.</para>
    /// </summary>
    public const ushort BushFlag = 0x0004;

    /// <summary>Fills the map border and covers 39% of Summoner's Rift, so: not walkable.</summary>
    public const ushort BlockedFlag = 0x0002;

    public bool IsBush(int x, int z) => Has(x, z, BushFlag);

    public bool Has(int x, int z, ushort mask)
    {
        if (!HasFlags || (uint)x >= (uint)CountX || (uint)z >= (uint)CountZ) return false;
        return (Flags[z * CountX + x] & mask) != 0;
    }

    /// <summary>World-space XZ box of one cell, at the grid's own minimum height.</summary>
    public (Vector3 Min, Vector3 Max) CellBounds(int x, int z)
    {
        var lo = new Vector3(Min.X + x * CellSize, Min.Y, Min.Z + z * CellSize);
        return (lo, new Vector3(lo.X + CellSize, Max.Y, lo.Z + CellSize));
    }

    /// <summary>Every cell carrying <paramref name="mask"/>, as world-space XZ boxes.</summary>
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

            grid = new NavGrid
            {
                VersionMajor = major, VersionMinor = minor,
                Min = min, Max = max, CellSize = cellSize,
                CountX = countX, CountZ = countZ, Flags = flags,
            };
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }
}
