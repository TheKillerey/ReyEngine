using System.Buffers.Binary;
using System.Text;

namespace ReyEngine.Formats.Characters;

/// <summary>The three files a character is made of, told apart by extension.</summary>
public enum CharacterFileKind { Unknown, SimpleSkin, Skeleton, Animation }

/// <summary>
/// M695: what an skn / skl / anm file's header says it is.
///
/// <para><b>Measured over the installed maps</b> (Map11 + Map453, 2026-09): the client still SHIPS
/// every one of these - skn 0.1 / 1.1 / 2.1 beside 4.1, uncompressed anm v3 / v4 beside v5 and the
/// compressed v1 - so an old form is not a broken one. The current forms are the ones Riot's own
/// exporters write today: skn 4.1, skl version 0 with the format token, anm v5 uncompressed or the
/// compressed family. Only the legacy <c>r3d2sklt</c> skeleton is absent from every shipped wad.</para>
/// </summary>
/// <param name="Kind">Which file this is, from its header rather than its extension.</param>
/// <param name="Label">Short form for a table cell: "SKN 2.1", "SKL legacy v1", "ANM v3", "ANM compressed v1".</param>
/// <param name="IsReadable">Whether the decoders here (LeagueToolkit's) can load it at all.</param>
/// <param name="IsCurrent">Whether it is already in the form Riot's tools write today.</param>
/// <param name="Note">One line for the user: why it is outdated, or why it cannot be read.</param>
public sealed record CharacterFileFormat(CharacterFileKind Kind, string Label, bool IsReadable, bool IsCurrent, string? Note)
{
    /// <summary>Outdated but loadable: the upgrade is worth doing and can be done.</summary>
    public bool CanUpgrade => IsReadable && !IsCurrent;
}

public static class CharacterFileFormats
{
    public const uint SknMagic = 0x00112233;
    public const uint SklFormatToken = 0x22FD4FC3;
    public const string LegacySklMagic = "r3d2sklt";
    public const string AnmUncompressedMagic = "r3d2anmd";
    public const string AnmCompressedMagic = "r3d2canm";

    public const string CurrentSkinLabel = "SKN 4.1";
    public const string CurrentSkeletonLabel = "SKL v0";
    public const string CurrentAnimationLabel = "ANM v5";

    /// <summary>The kind an extension promises; the header has the last word.</summary>
    public static CharacterFileKind KindOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".skn" => CharacterFileKind.SimpleSkin,
        ".skl" => CharacterFileKind.Skeleton,
        ".anm" => CharacterFileKind.Animation,
        _ => CharacterFileKind.Unknown,
    };

    /// <summary>Read the header. Never throws; an unrecognisable file is <see cref="CharacterFileKind.Unknown"/>.</summary>
    public static CharacterFileFormat Inspect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 12)
        {
            string magic8 = Ascii(bytes[..8]);
            uint version = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
            switch (magic8)
            {
                case LegacySklMagic:
                    return version is 1 or 2
                        ? new(CharacterFileKind.Skeleton, $"SKL legacy v{version}", true, false,
                            "the pre-2015 r3d2sklt rig; no shipped wad carries one any more")
                        : new(CharacterFileKind.Skeleton, $"SKL legacy v{version}", false, false, "unknown legacy rig version");
                case AnmUncompressedMagic:
                    return version switch
                    {
                        5 => new(CharacterFileKind.Animation, CurrentAnimationLabel, true, true, null),
                        4 => new(CharacterFileKind.Animation, "ANM v4", true, false, "the 2015 palette form with per-frame joint hashes; still loads"),
                        3 => new(CharacterFileKind.Animation, "ANM v3", true, false, "the legacy per-track form with joints by name; still loads"),
                        _ => new(CharacterFileKind.Animation, $"ANM v{version}", false, false, "unknown uncompressed animation version"),
                    };
                case AnmCompressedMagic:
                    return version is 1 or 2 or 3
                        ? new(CharacterFileKind.Animation, $"ANM compressed v{version}", true, true, null)
                        : new(CharacterFileKind.Animation, $"ANM compressed v{version}", false, false, "unknown compressed animation version");
            }
        }
        if (bytes.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == SknMagic)
        {
            ushort major = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
            ushort minor = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
            string label = $"SKN {major}.{minor}";
            if (major == 4 && minor == 1) return new(CharacterFileKind.SimpleSkin, label, true, true, null);
            // LeagueToolkit reads 0.x, 2.x, 4.x and anything .1; the shipped old forms are 0.1, 1.1, 2.1.
            bool readable = major is 0 or 2 or 4 || minor == 1;
            return new(CharacterFileKind.SimpleSkin, label, readable, false, readable
                ? major == 0 ? "one unnamed submesh, no bounds; still loads" : "no vertex type or bounds in the header; still loads"
                : "unknown simple-skin version");
        }
        if (bytes.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) == SklFormatToken)
        {
            uint version = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
            return version == 0
                ? new(CharacterFileKind.Skeleton, CurrentSkeletonLabel, true, true, null)
                : new(CharacterFileKind.Skeleton, $"SKL v{version}", false, false, "unknown rig version");
        }
        return new(CharacterFileKind.Unknown, "unknown", false, false, "not a simple skin, rig or animation");
    }

    private static string Ascii(ReadOnlySpan<byte> b)
    {
        foreach (byte c in b) if (c < 0x20 || c > 0x7e) return "";
        return Encoding.ASCII.GetString(b);
    }
}
