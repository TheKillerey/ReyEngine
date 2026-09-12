using System.Globalization;
using System.Numerics;
using System.Text.Json;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;

namespace ReyEngine.Formats.Meta;

/// <summary>
/// <para>M370: builds a live <see cref="BinTreeProperty"/> from a meta-class field type plus the authored
/// default the schema records, so an editor can ADD a field the object omits rather than only report it.</para>
///
/// <para><b>This is a write path into a real mod's .bin, so it refuses far more than it accepts.</b> Two
/// hard rules:</para>
/// <list type="number">
///   <item>Only SELF-CONTAINED types. A scalar, vector, string or hash is fully described by its own value.
///   Embed/Pointer/Link/List/List2/Map/Option are not: they need a class hash, an element type, or a nested
///   object graph that the type tuple alone does not pin down. Writing a malformed one of those into a bin
///   the game then loads is exactly the kind of damage that is not worth a convenience feature, so they are
///   declined with a reason rather than guessed.</item>
///   <item>NO DEFAULT, NO WRITE. If the dump records no default for a field, this refuses. Inventing a zero
///   would be asserting a game behaviour nobody measured - the same mistake that cost four reverted
///   attempts elsewhere in this project.</item>
/// </list>
///
/// <para>Takes primitives rather than the Core meta types on purpose, so ReyEngine.Formats keeps no
/// dependency on the optional meta database.</para>
/// </summary>
public static class MetaDefaultProperty
{
    /// <summary>
    /// <para>M372: the CLR property type a declared field type must arrive as, for the cases where that is
    /// UNAMBIGUOUS. Null means "do not check" - deliberately, and for most of the type system.</para>
    ///
    /// <para>Only the self-contained types are listed. The container/struct/map/option families are
    /// omitted because their CLR hierarchy has real subtyping (BinTreeEmbedded derives from BinTreeStruct,
    /// BinTreeUnorderedContainer from BinTreeContainer), so an exact-name comparison would flag correct
    /// bins. A validator that cries wolf is worse than one that stays quiet, so it stays quiet there.</para>
    /// </summary>
    public static string? ExpectedWireType(string fieldType) => fieldType switch
    {
        "Bool" => nameof(BinTreeBool),
        "Flag" => nameof(BinTreeBitBool),
        "U8" => nameof(BinTreeU8),
        "I8" => nameof(BinTreeI8),
        "U16" => nameof(BinTreeU16),
        "I16" => nameof(BinTreeI16),
        "U32" => nameof(BinTreeU32),
        "I32" => nameof(BinTreeI32),
        "U64" => nameof(BinTreeU64),
        "I64" => nameof(BinTreeI64),
        "F32" => nameof(BinTreeF32),
        "String" => nameof(BinTreeString),
        "Hash" => nameof(BinTreeHash),
        "Vec2" => nameof(BinTreeVector2),
        "Vec3" => nameof(BinTreeVector3),
        "Vec4" => nameof(BinTreeVector4),
        // M407: Color is deliberately NOT here even though it is now creatable. This method feeds the
        // VALIDATOR, and M372 measured that wire-type checking the Color/container/struct families
        // produces false alarms on correct bins. Being able to CREATE a Color and choosing not to POLICE
        // one are separate decisions; conflating them would start flagging shipped data.
        _ => null,
    };

    /// <summary>Field types this can build. Everything else is declined - see the class remarks.</summary>
    public static bool IsSupported(string fieldType) => fieldType switch
    {
        "Bool" or "Flag" or "U8" or "I8" or "U16" or "I16" or "U32" or "I32" or "U64" or "I64"
            or "F32" or "String" or "Hash" or "Vec2" or "Vec3" or "Vec4"
            // M407: Color is four floats like Vec4 and every bit as constructible. It was declined only
            // because it was never listed - 310 declared properties across the meta database are Color,
            // making it the single largest refused type, and the editor now draws a swatch for them.
            or "Color"
            // M706: a File is one 64-bit chunk link, as constructible as U64, and the schema's default for
            // every one of them is "0x0" - the empty link the game already assumes when the field is
            // absent. Declining it kept the texture settings of a skin (emissiveTexture, glossTexture,
            // reflectionMap) unaddable, which is most of what a character's look is made of.
            or "File" => true,
        _ => false,
    };

    /// <summary>Why a field cannot be added, or null when it can. Phrased for a tooltip.</summary>
    public static string? DeclineReason(string fieldType, string? defaultJson)
    {
        if (!IsSupported(fieldType))
            return $"{fieldType} fields are not self-contained — they need a class, element type or nested "
                   + "object the schema alone does not pin down, so ReyEngine will not synthesise one.";
        if (string.IsNullOrEmpty(defaultJson))
            return "The schema records no default for this field, so there is no measured value to write.";
        return null;
    }

    /// <summary>Build the property. False (with a reason) whenever anything is unclear - a malformed value
    /// must never reach the tree.</summary>
    public static bool TryCreate(uint nameHash, string fieldType, string? defaultJson,
        out BinTreeProperty? property, out string? reason)
    {
        property = null;
        reason = DeclineReason(fieldType, defaultJson);
        if (reason is not null) return false;

        try
        {
            using var doc = JsonDocument.Parse(defaultJson!);
            var v = doc.RootElement;
            switch (fieldType)
            {
                case "Bool":
                    property = new BinTreeBool(nameHash, AsBool(v));
                    break;
                // The bit-packed bool. A distinct wire type, so writing a BinTreeBool here would produce a
                // property the game reads at the wrong width.
                case "Flag":
                    property = new BinTreeBitBool(nameHash, AsBool(v));
                    break;
                case "U8": property = new BinTreeU8(nameHash, checked((byte)AsI64(v))); break;
                case "I8": property = new BinTreeI8(nameHash, checked((sbyte)AsI64(v))); break;
                case "U16": property = new BinTreeU16(nameHash, checked((ushort)AsI64(v))); break;
                case "I16": property = new BinTreeI16(nameHash, checked((short)AsI64(v))); break;
                case "U32": property = new BinTreeU32(nameHash, checked((uint)AsI64(v))); break;
                case "I32": property = new BinTreeI32(nameHash, checked((int)AsI64(v))); break;
                case "U64": property = new BinTreeU64(nameHash, (ulong)AsI64(v)); break;
                case "I64": property = new BinTreeI64(nameHash, AsI64(v)); break;
                case "F32": property = new BinTreeF32(nameHash, (float)AsF64(v)); break;
                case "String":
                    property = new BinTreeString(nameHash, v.ValueKind == JsonValueKind.String
                        ? v.GetString() ?? "" : "");
                    break;
                // Recorded as a hex STRING ("0x0"), not a number - parsing it as one silently yields 0.
                case "Hash": property = new BinTreeHash(nameHash, AsHash(v)); break;
                // M706: the same hex-string form, 64 bits wide. Zero is "no chunk", which is what the
                // field's absence means, so adding it at the default changes nothing until a path is set.
                case "File": property = new BinTreeWadChunkLink(nameHash, AsChunkLink(v)); break;
                case "Vec2":
                {
                    var f = AsFloats(v, 2);
                    property = new BinTreeVector2(nameHash, new Vector2(f[0], f[1]));
                    break;
                }
                case "Vec3":
                {
                    var f = AsFloats(v, 3);
                    property = new BinTreeVector3(nameHash, new Vector3(f[0], f[1], f[2]));
                    break;
                }
                case "Vec4":
                {
                    var f = AsFloats(v, 4);
                    property = new BinTreeVector4(nameHash, new Vector4(f[0], f[1], f[2], f[3]));
                    break;
                }
                case "Color":
                {
                    // Same four floats as Vec4, different wire type. Riot writes colours 0..1.
                    var f = AsFloats(v, 4);
                    property = new BinTreeColor(nameHash, new LeagueToolkit.Core.Primitives.Color(f[0], f[1], f[2], f[3]));
                    break;
                }
                default:
                    reason = $"{fieldType} is not handled.";
                    return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            property = null;
            reason = $"The recorded default could not be read as {fieldType}: {ex.Message}";
            return false;
        }
    }

    private static bool AsBool(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => v.GetDouble() != 0,
        _ => throw new FormatException($"expected a boolean, got {v.ValueKind}"),
    };

    private static long AsI64(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.TryGetInt64(out long i) ? i : (long)v.GetDouble(),
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        _ => throw new FormatException($"expected a number, got {v.ValueKind}"),
    };

    private static double AsF64(JsonElement v) => v.ValueKind == JsonValueKind.Number
        ? v.GetDouble()
        : throw new FormatException($"expected a number, got {v.ValueKind}");

    /// <summary>M706: a 64-bit chunk link, recorded the same way a hash is - a hex STRING.</summary>
    private static ulong AsChunkLink(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Number) return (ulong)AsI64(v);
        if (v.ValueKind != JsonValueKind.String) throw new FormatException("expected a chunk-link string");
        var s = (v.GetString() ?? "").AsSpan();
        if (s.Length > 2 && (s[1] == 'x' || s[1] == 'X')) s = s[2..];
        return ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong h)
            ? h
            : throw new FormatException("chunk link was not hexadecimal");
    }

    private static uint AsHash(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Number) return (uint)AsI64(v);
        if (v.ValueKind != JsonValueKind.String) throw new FormatException("expected a hash string");
        var s = (v.GetString() ?? "").AsSpan();
        if (s.Length > 2 && (s[1] == 'x' || s[1] == 'X')) s = s[2..];
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint h)
            ? h
            : throw new FormatException("hash was not hexadecimal");
    }

    private static float[] AsFloats(JsonElement v, int count)
    {
        if (v.ValueKind != JsonValueKind.Array) throw new FormatException("expected an array");
        var result = new float[count];
        int i = 0;
        foreach (var e in v.EnumerateArray())
        {
            if (i >= count) break;
            result[i++] = (float)AsF64(e);
        }
        if (i != count) throw new FormatException($"expected {count} components, got {i}");
        return result;
    }
}
