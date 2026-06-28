using System.Buffers.Binary;

namespace ReyEngine.Core.Assets;

public enum AssetType
{
    Unknown,
    Wad,
    Bin,            // .bin property file
    SkinnedMesh,    // .skn
    Skeleton,       // .skl
    Animation,      // .anm
    MapGeometry,    // .mapgeo
    StaticMesh,     // .scb / .sco
    Texture,        // .tex
    Dds,            // .dds
    Image,          // .png / .jpg
    Audio,          // .bnk / .wpk (wwise)
    Shader,         // shader source / cache
    Json,
    Text,
}

/// <summary>Detects an asset's type from its path extension and/or magic bytes.</summary>
public static class AssetTypeDetector
{
    public static AssetType FromPath(string path)
    {
        int dot = path.LastIndexOf('.');
        string ext = dot < 0 ? "" : path[(dot + 1)..].ToLowerInvariant();
        return ext switch
        {
            "wad" or "client" => AssetType.Wad,
            "bin" => AssetType.Bin,
            "skn" => AssetType.SkinnedMesh,
            "skl" => AssetType.Skeleton,
            "anm" => AssetType.Animation,
            "mapgeo" => AssetType.MapGeometry,
            "scb" or "sco" => AssetType.StaticMesh,
            "tex" => AssetType.Texture,
            "dds" => AssetType.Dds,
            "png" or "jpg" or "jpeg" => AssetType.Image,
            "bnk" or "wpk" => AssetType.Audio,
            "json" => AssetType.Json,
            "txt" or "cfg" or "ini" or "log" => AssetType.Text,
            "fx" or "vs_2_0" or "ps_2_0" or "preload" => AssetType.Shader,
            _ => AssetType.Unknown,
        };
    }

    public static AssetType FromMagic(ReadOnlySpan<byte> d)
    {
        if (d.Length >= 8 && d[0] == 'r' && d[1] == '3' && d[2] == 'd' && d[3] == '2')
        {
            if (Eq(d[4..8], "anmd") || Eq(d[4..8], "canm")) return AssetType.Animation;
            if (Eq(d[4..8], "sklt")) return AssetType.Skeleton;
            if (Eq(d[4..8], "Mesh")) return AssetType.SkinnedMesh;
        }
        if (d.Length >= 4)
        {
            if (Eq(d[..4], "DDS ")) return AssetType.Dds;
            if (Eq(d[..4], "TEX\0")) return AssetType.Texture;
            if (Eq(d[..4], "OEGM")) return AssetType.MapGeometry;
            if (Eq(d[..4], "PROP") || Eq(d[..4], "PTCH")) return AssetType.Bin;
            if (Eq(d[..4], "BKHD")) return AssetType.Audio;
            if (d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G') return AssetType.Image;
            if (d[0] == 0xFF && d[1] == 0xD8) return AssetType.Image;
            uint m = BinaryPrimitives.ReadUInt32LittleEndian(d);
            if (m == 0x00112233) return AssetType.SkinnedMesh;
        }
        return AssetType.Unknown;
    }

    /// <summary>Best guess: trust a known extension, fall back to magic sniffing.</summary>
    public static AssetType Detect(string path, ReadOnlySpan<byte> head)
    {
        var t = FromPath(path);
        return t != AssetType.Unknown ? t : FromMagic(head);
    }

    private static bool Eq(ReadOnlySpan<byte> bytes, string ascii)
    {
        if (bytes.Length < ascii.Length) return false;
        for (int i = 0; i < ascii.Length; i++)
            if (bytes[i] != (byte)ascii[i]) return false;
        return true;
    }
}
