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
            // M300: "r3d2Mesh" is the STATIC mesh (.scb). A skinned mesh (.skn) is 0x00112233, handled
            // below - this arm used to claim .skn and would have named every .scb wrongly.
            if (Eq(d[4..8], "Mesh")) return AssetType.StaticMesh;
            // Wwise package: "r3d2" followed by a version dword rather than a four-character tag.
            if (d.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(d[4..8]) == 1) return AssetType.Audio;
        }
        // Text formats, checked before the binary magics so a leading brace is not mistaken for one.
        if (d.Length >= 13 && Eq(d[..13], "[ObjectBegin]")) return AssetType.StaticMesh;   // .sco
        if (d.Length >= 1 && (d[0] == (byte)'{' || d[0] == (byte)'['))
        {
            // The reference extractor leaves these extensionless; they are plainly JSON. Byte codes
            // rather than escapes: space, CR, LF, tab.
            for (int i = 1; i < Math.Min(d.Length, 64); i++)
            {
                if (d[i] == (byte)'"') return AssetType.Json;
                if (d[i] is not (0x20 or 0x0D or 0x0A or 0x09)) break;
            }
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

    /// <summary>M300: the file extension a sniffed type should be written with.
    ///
    /// <para>Used when a WAD chunk's path hash is unknown, so the only name available is the hash. Writing
    /// every such chunk as ".bin" is what made a mod's CUSTOM textures unfindable - custom paths are
    /// exactly the ones a hash database does not know, so the files most worth finding were the ones
    /// disguised. Null means genuinely unidentified, and those keep ".bin" rather than being given a
    /// guessed extension that would be worse than an honest unknown.</para></summary>
    public static string? ExtensionFor(AssetType type) => type switch
    {
        AssetType.Dds => "dds",
        AssetType.Texture => "tex",
        AssetType.Bin => "bin",
        AssetType.SkinnedMesh => "skn",
        AssetType.Skeleton => "skl",
        AssetType.Animation => "anm",
        AssetType.MapGeometry => "mapgeo",
        AssetType.StaticMesh => "scb",
        AssetType.Image => "png",
        AssetType.Audio => "bnk",
        AssetType.Json => "json",
        _ => null,
    };

    /// <summary>M300: the extension to write a chunk under when its path is unknown.
    ///
    /// <para>Not just <see cref="ExtensionFor"/> of the sniffed type, because extension and type are not
    /// one to one: ".scb" and ".sco" are both StaticMesh (one binary, one text) and ".bnk" and ".wpk" are
    /// both Audio. Collapsing those through the enum would name a .sco as .scb - not fatal, but wrong in
    /// a way that would quietly mislead anyone browsing the folder.</para></summary>
    public static string? FileExtensionFromMagic(ReadOnlySpan<byte> d)
    {
        if (d.Length >= 13 && Eq(d[..13], "[ObjectBegin]")) return "sco";     // text static mesh
        if (d.Length >= 8 && Eq(d[..4], "r3d2"))
        {
            if (Eq(d[4..8], "Mesh")) return "scb";                            // binary static mesh
            if (BinaryPrimitives.ReadUInt32LittleEndian(d[4..8]) == 1) return "wpk";
        }
        if (d.Length >= 4 && Eq(d[..4], "BKHD")) return "bnk";
        return ExtensionFor(FromMagic(d));
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
