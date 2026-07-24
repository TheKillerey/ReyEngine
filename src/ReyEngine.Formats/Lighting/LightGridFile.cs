using System.Buffers.Binary;
using System.Numerics;

namespace ReyEngine.Formats.Lighting;

/// <summary>M158: League's modern <c>lightgrid.dat</c> — the probe volume that lights everything a
/// baked lightmap cannot (characters, effects, meshes whose material sets NO_BAKED_LIGHTING).
///
/// Layout, measured across Map11/Map12/Map21/Map30 (every one of them version 3, 256x256, 1,572,896 B):
///   0   u32  version      always 3
///   4   u32  headerSize   always 32 — the file says how big its own header is
///   8   u32  width        cells along X (256 everywhere observed)
///   12  u32  height       cells along Z
///   16  f32  worldSizeX   world span the grid covers (Map30 5000, Map21 14820 — X and Z differ, so
///   20  f32  worldSizeZ   these are extents, not a single square)
///   24  f32  fullBrightScale   per-file, varies by theme (0.25 .. 1.0)
///   28  f32  unknown28    per-MAP constant (Map12 0.5, Map21 0.6, Map30 0.65)
///   32+ cells, row-major, 24 bytes each: six RGBA8 samples = an ambient cube.
///
/// The six directions are +X, -X, +Y, -Y, +Z, -Z: sample 2 is the brightest and sample 3 the darkest in
/// all four maps (Map12 48.2 vs 16.5, Map21 65.7 vs 8.9, Map30 170.1 vs 40.4), which is sky above and
/// ground below. Alpha is 255 in every sample of every cell.
///
/// NOTE the legacy NVR lightgrid is a different file with a 76-byte header (1,572,940 B for the same
/// 256x256 grid); this class handles the modern one only and rejects the legacy size rather than
/// silently misreading it.</summary>
public sealed class LightGridFile
{
    public const int HeaderSize = 32;
    public const int CellSize = 24;
    public const int Directions = 6;

    public int Version { get; set; } = 3;
    public int Width { get; set; } = 256;
    public int Height { get; set; } = 256;
    public float WorldSizeX { get; set; }
    public float WorldSizeZ { get; set; }
    public float FullBrightScale { get; set; } = 1f;
    /// <summary>Header float at offset 28. Constant per map, purpose unverified — preserved on load so a
    /// re-written grid keeps whatever the map shipped with.</summary>
    public float Unknown28 { get; set; } = 0.5f;

    /// <summary>Width*Height*6 colours, row-major by cell then by direction (see DirectionVectors).</summary>
    public Vector3[] Samples { get; set; } = Array.Empty<Vector3>();

    /// <summary>The six ambient-cube axes, in file order.</summary>
    public static readonly Vector3[] DirectionVectors =
    {
        new(1, 0, 0), new(-1, 0, 0),
        new(0, 1, 0), new(0, -1, 0),
        new(0, 0, 1), new(0, 0, -1),
    };

    public static LightGridFile Create(int width, int height, float worldSizeX, float worldSizeZ)
    {
        var g = new LightGridFile
        {
            Width = Math.Max(1, width), Height = Math.Max(1, height),
            WorldSizeX = worldSizeX, WorldSizeZ = worldSizeZ,
        };
        g.Samples = new Vector3[g.Width * g.Height * Directions];
        return g;
    }

    public static bool LooksLikeLightGrid(byte[] data)
    {
        if (data.Length < HeaderSize + CellSize) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)) != HeaderSize) return false;
        uint w = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
        uint h = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
        if (w == 0 || h == 0 || w > 4096 || h > 4096) return false;
        return HeaderSize + (long)w * h * CellSize == data.Length;
    }

    public static LightGridFile Read(byte[] data)
    {
        if (!LooksLikeLightGrid(data))
            throw new InvalidDataException("not a modern (32-byte header) lightgrid.dat");

        var g = new LightGridFile
        {
            Version = BinaryPrimitives.ReadInt32LittleEndian(data),
            Width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8)),
            Height = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12)),
            WorldSizeX = BitConverter.ToSingle(data, 16),
            WorldSizeZ = BitConverter.ToSingle(data, 20),
            FullBrightScale = BitConverter.ToSingle(data, 24),
            Unknown28 = BitConverter.ToSingle(data, 28),
        };
        g.Samples = new Vector3[g.Width * g.Height * Directions];
        for (int c = 0; c < g.Width * g.Height; c++)
        {
            int o = HeaderSize + c * CellSize;
            for (int d = 0; d < Directions; d++)
                g.Samples[c * Directions + d] = new Vector3(
                    data[o + d * 4] / 255f, data[o + d * 4 + 1] / 255f, data[o + d * 4 + 2] / 255f);
        }
        return g;
    }

    public byte[] Write()
    {
        var data = new byte[HeaderSize + Width * Height * CellSize];
        BinaryPrimitives.WriteInt32LittleEndian(data, Version);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), Width);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), Height);
        BitConverter.TryWriteBytes(data.AsSpan(16), WorldSizeX);
        BitConverter.TryWriteBytes(data.AsSpan(20), WorldSizeZ);
        BitConverter.TryWriteBytes(data.AsSpan(24), FullBrightScale);
        BitConverter.TryWriteBytes(data.AsSpan(28), Unknown28);

        for (int c = 0; c < Width * Height; c++)
        {
            int o = HeaderSize + c * CellSize;
            for (int d = 0; d < Directions; d++)
            {
                var s = c * Directions + d < Samples.Length ? Samples[c * Directions + d] : Vector3.Zero;
                data[o + d * 4 + 0] = ToByte(s.X);
                data[o + d * 4 + 1] = ToByte(s.Y);
                data[o + d * 4 + 2] = ToByte(s.Z);
                data[o + d * 4 + 3] = 255;      // alpha is 255 in every shipped sample
            }
        }
        return data;
    }

    /// <summary>World XZ -> cell index, clamped. The header carries only a size, no origin, so the grid
    /// is anchored at the world origin — which is how League maps are authored (all-positive XZ).</summary>
    public int CellIndex(float worldX, float worldZ)
    {
        int cx = WorldSizeX > 0 ? (int)(worldX / WorldSizeX * Width) : 0;
        int cz = WorldSizeZ > 0 ? (int)(worldZ / WorldSizeZ * Height) : 0;
        cx = Math.Clamp(cx, 0, Width - 1);
        cz = Math.Clamp(cz, 0, Height - 1);
        return cz * Width + cx;
    }

    /// <summary>Ambient-cube lookup: blend the three samples facing the same way as the normal, weighted
    /// by the squared axis components (the standard HL2-style reconstruction).</summary>
    public Vector3 Sample(float worldX, float worldZ, Vector3 normal)
    {
        int c = CellIndex(worldX, worldZ) * Directions;
        if (c + Directions > Samples.Length) return Vector3.Zero;
        var n2 = normal * normal;
        return Samples[c + (normal.X >= 0 ? 0 : 1)] * n2.X
             + Samples[c + (normal.Y >= 0 ? 2 : 3)] * n2.Y
             + Samples[c + (normal.Z >= 0 ? 4 : 5)] * n2.Z;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp(MathF.Round(v * 255f), 0f, 255f);
}
