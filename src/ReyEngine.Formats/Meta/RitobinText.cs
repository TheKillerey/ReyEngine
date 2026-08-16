using System.Globalization;
using System.Text;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Meta;

/// <summary>
/// M496: ritobin's <c>#PROP_text</c> representation of a .bin — the format the League modding community
/// reads and edits bins in, commonly saved with a <c>.py</c> extension so editors syntax-highlight it.
///
/// <para><b>Type names are ritobin's, and the ids line up exactly.</b> Taken from
/// <c>ritobin_lib/src/ritobin/bin_types.hpp</c>: none/bool/i8/u8/i16/u16/i32/u32/i64/u64/f32, vec2/vec3/
/// vec4/mtx44/rgba, string/hash/file, then list/list2/pointer/embed/link/option/map/flag at 0x80..0x87.
/// LeagueToolkit's <see cref="BinPropertyType"/> uses the same numeric values, so the mapping is a
/// renaming and never a reinterpretation.</para>
///
/// <para><b>Names vs hashes.</b> A field name, class name or hash-valued property is written as text when
/// the hash database resolves it and as <c>0x…</c> when it does not — the same choice ritobin makes, and
/// the reason its output is readable at all. Round-tripping is unaffected either way: the writer emits what
/// hashes to the same value, and the reader hashes any bare name it is given.</para>
/// </summary>
public static class RitobinText
{
    public const string Header = "#PROP_text";

    /// <summary>The PROP header version Riot ships and LeagueToolkit writes. Read off Map453's shipped
    /// materials.bin rather than assumed.</summary>
    public const uint PropVersion = 3;

    // ---- type names ------------------------------------------------------

    public static string TypeName(BinPropertyType type) => type switch
    {
        BinPropertyType.None => "none",
        BinPropertyType.Bool => "bool",
        BinPropertyType.I8 => "i8",
        BinPropertyType.U8 => "u8",
        BinPropertyType.I16 => "i16",
        BinPropertyType.U16 => "u16",
        BinPropertyType.I32 => "i32",
        BinPropertyType.U32 => "u32",
        BinPropertyType.I64 => "i64",
        BinPropertyType.U64 => "u64",
        BinPropertyType.F32 => "f32",
        BinPropertyType.Vector2 => "vec2",
        BinPropertyType.Vector3 => "vec3",
        BinPropertyType.Vector4 => "vec4",
        BinPropertyType.Matrix44 => "mtx44",
        BinPropertyType.Color => "rgba",
        BinPropertyType.String => "string",
        BinPropertyType.Hash => "hash",
        BinPropertyType.WadChunkLink => "file",
        BinPropertyType.Container => "list",
        BinPropertyType.UnorderedContainer => "list2",
        BinPropertyType.Struct => "pointer",
        BinPropertyType.Embedded => "embed",
        BinPropertyType.ObjectLink => "link",
        BinPropertyType.Optional => "option",
        BinPropertyType.Map => "map",
        BinPropertyType.BitBool => "flag",
        _ => "none",
    };

    public static bool TryParseType(string name, out BinPropertyType type)
    {
        type = name.Trim().ToLowerInvariant() switch
        {
            "none" => BinPropertyType.None,
            "bool" => BinPropertyType.Bool,
            "i8" => BinPropertyType.I8,
            "u8" => BinPropertyType.U8,
            "i16" => BinPropertyType.I16,
            "u16" => BinPropertyType.U16,
            "i32" => BinPropertyType.I32,
            "u32" => BinPropertyType.U32,
            "i64" => BinPropertyType.I64,
            "u64" => BinPropertyType.U64,
            "f32" => BinPropertyType.F32,
            "vec2" => BinPropertyType.Vector2,
            "vec3" => BinPropertyType.Vector3,
            "vec4" => BinPropertyType.Vector4,
            "mtx44" => BinPropertyType.Matrix44,
            "rgba" => BinPropertyType.Color,
            "string" => BinPropertyType.String,
            "hash" => BinPropertyType.Hash,
            "file" => BinPropertyType.WadChunkLink,
            "list" => BinPropertyType.Container,
            "list2" => BinPropertyType.UnorderedContainer,
            "pointer" => BinPropertyType.Struct,
            "embed" => BinPropertyType.Embedded,
            "link" => BinPropertyType.ObjectLink,
            "option" => BinPropertyType.Optional,
            "map" => BinPropertyType.Map,
            "flag" => BinPropertyType.BitBool,
            _ => (BinPropertyType)255,
        };
        return (int)type != 255;
    }

    // ---- writing ---------------------------------------------------------

    /// <summary>Serialize a bin to ritobin text.</summary>
    /// <param name="resolveName">32-bit FNV-1a name lookup; null or empty result means "write the hash".</param>
    /// <param name="resolveWadPath">64-bit WAD path lookup, for <c>file</c> values.</param>
    public static string Write(BinTree tree, Func<uint, string?>? resolveName = null,
        Func<ulong, string?>? resolveWadPath = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        var w = new Writer(resolveName, resolveWadPath);
        return w.Write(tree);
    }

    private sealed class Writer
    {
        private readonly Func<uint, string?>? _name;
        private readonly Func<ulong, string?>? _wad;
        private readonly StringBuilder _sb = new();

        public Writer(Func<uint, string?>? name, Func<ulong, string?>? wad) { _name = name; _wad = wad; }

        public string Write(BinTree tree)
        {
            _sb.Append(Header).Append('\n').Append('\n');

            // The three header fields ritobin always emits. `type` is "PROP" for a normal bin and "PTCH"
            // for a patch bin; LeagueToolkit exposes neither directly, so it is inferred from whether any
            // data overrides are present — the same thing that makes a file a patch.
            bool isPatch = tree.DataOverrides.Count > 0;
            Line(0, $"type: string = \"{(isPatch ? "PTCH" : "PROP")}\"");
            // LeagueToolkit does not surface the header version and always writes 3, which is what Riot's
            // shipped bins carry (read straight off Map453's materials.bin). Emitting anything else here
            // would describe a file this tool cannot actually produce.
            Line(0, $"version: u32 = {PropVersion}");

            Line(0, "linked: list[string] = {");
            foreach (var dep in tree.Dependencies) Line(1, Quote(dep));
            Line(0, "}");

            Line(0, "entries: map[hash,embed] = {");
            foreach (var (pathHash, obj) in tree.Objects)
            {
                Line(1, $"{HashLiteral(pathHash)} = {ClassLiteral(obj.ClassHash)} {{");
                foreach (var (_, property) in obj.Properties) WriteProperty(2, property);
                Line(1, "}");
            }
            Line(0, "}");

            return _sb.ToString();
        }

        private void WriteProperty(int depth, BinTreeProperty property)
        {
            string name = NameLiteral(property.NameHash);
            string type = TypeName(property.Type);

            switch (property)
            {
                case BinTreeContainer container:
                    Line(depth, $"{name}: {type}[{TypeName(container.ElementType)}] = {{");
                    foreach (var element in container.Elements) WriteElement(depth + 1, element);
                    Line(depth, "}");
                    return;

                case BinTreeOptional optional:
                    Line(depth, $"{name}: option[{TypeName(OptionalElementType(optional))}] = {{");
                    if (optional.Value is { } inner) WriteElement(depth + 1, inner);
                    Line(depth, "}");
                    return;

                case BinTreeMap map:
                    Line(depth, $"{name}: map[{TypeName(map.KeyType)},{TypeName(map.ValueType)}] = {{");
                    foreach (var pair in map)
                    {
                        // A map entry is "key = value"; an embed/pointer value carries its class name.
                        string key = Scalar(pair.Key);
                        if (pair.Value is BinTreeStruct s)
                        {
                            Line(depth + 1, $"{key} = {ClassLiteral(StructClass(s))} {{");
                            foreach (var (_, p) in s.Properties) WriteProperty(depth + 2, p);
                            Line(depth + 1, "}");
                        }
                        else Line(depth + 1, $"{key} = {Scalar(pair.Value)}");
                    }
                    Line(depth, "}");
                    return;

                case BinTreeStruct str:
                    Line(depth, $"{name}: {type} = {ClassLiteral(StructClass(str))} {{");
                    foreach (var (_, p) in str.Properties) WriteProperty(depth + 1, p);
                    Line(depth, "}");
                    return;

                default:
                    Line(depth, $"{name}: {type} = {Scalar(property)}");
                    return;
            }
        }

        /// <summary>An element inside a container has no name — only a value, or a class plus a body.</summary>
        private void WriteElement(int depth, BinTreeProperty element)
        {
            if (element is BinTreeStruct s)
            {
                Line(depth, $"{ClassLiteral(StructClass(s))} {{");
                foreach (var (_, p) in s.Properties) WriteProperty(depth + 1, p);
                Line(depth, "}");
            }
            else if (element is BinTreeContainer c)
            {
                Line(depth, $"{TypeName(c.Type)}[{TypeName(c.ElementType)}] = {{");
                foreach (var e in c.Elements) WriteElement(depth + 1, e);
                Line(depth, "}");
            }
            else Line(depth, Scalar(element));
        }

        private string Scalar(BinTreeProperty p) => p switch
        {
            BinTreeNone => "null",
            BinTreeBool b => b.Value ? "true" : "false",
            BinTreeBitBool f => f.Value ? "true" : "false",
            BinTreeI8 v => v.Value.ToString(CultureInfo.InvariantCulture),
            BinTreeU8 v => v.Value.ToString(CultureInfo.InvariantCulture),
            BinTreeI16 v => v.Value.ToString(CultureInfo.InvariantCulture),
            BinTreeU16 v => v.Value.ToString(CultureInfo.InvariantCulture),
            BinTreeI32 v => v.Value.ToString(CultureInfo.InvariantCulture),
            BinTreeU32 v => v.Value.ToString(CultureInfo.InvariantCulture),
            BinTreeI64 v => v.Value.ToString(CultureInfo.InvariantCulture),
            BinTreeU64 v => v.Value.ToString(CultureInfo.InvariantCulture),
            BinTreeF32 v => Float(v.Value),
            BinTreeVector2 v => $"{{ {Float(v.Value.X)}, {Float(v.Value.Y)} }}",
            BinTreeVector3 v => $"{{ {Float(v.Value.X)}, {Float(v.Value.Y)}, {Float(v.Value.Z)} }}",
            BinTreeVector4 v => $"{{ {Float(v.Value.X)}, {Float(v.Value.Y)}, {Float(v.Value.Z)}, {Float(v.Value.W)} }}",
            BinTreeMatrix44 m => Matrix(m),
            // rgba is written as 0..255, which is ritobin's convention. LeagueToolkit's Color stores
            // NORMALISED floats and its byte constructor divides by 255, so printing the raw components
            // would emit "0.039" where ritobin writes "10" — and reading that back as a byte gives 0.
            BinTreeColor c => $"{{ {Byte255(c.Value.R)}, {Byte255(c.Value.G)}, {Byte255(c.Value.B)}, {Byte255(c.Value.A)} }}",
            BinTreeString s => Quote(s.Value),
            BinTreeHash h => HashLiteral(h.Value),
            BinTreeObjectLink l => HashLiteral(l.Value),
            BinTreeWadChunkLink f => WadLiteral(f.Value),
            _ => "null",
        };

        private static string Matrix(BinTreeMatrix44 m)
        {
            var v = m.Value;
            return "{ "
                 + $"{Float(v.M11)}, {Float(v.M12)}, {Float(v.M13)}, {Float(v.M14)}, "
                 + $"{Float(v.M21)}, {Float(v.M22)}, {Float(v.M23)}, {Float(v.M24)}, "
                 + $"{Float(v.M31)}, {Float(v.M32)}, {Float(v.M33)}, {Float(v.M34)}, "
                 + $"{Float(v.M41)}, {Float(v.M42)}, {Float(v.M43)}, {Float(v.M44)}"
                 + " }";
        }

        /// <summary>Round-trippable float text. "R" keeps every bit that matters, and InvariantCulture is
        /// not optional here — a German locale would otherwise write "1,5" and produce a file that reparses
        /// as two values.</summary>
        private static string Float(float f) =>
            float.IsFinite(f) ? f.ToString("R", CultureInfo.InvariantCulture) : "0";

        /// <summary>A normalised colour component back to the 0..255 byte it was authored as.</summary>
        private static int Byte255(float f) =>
            float.IsFinite(f) ? Math.Clamp((int)MathF.Round(f * 255f), 0, 255) : 0;

        private string NameLiteral(uint hash)
        {
            if (hash == 0) return "0x00000000";
            var resolved = _name?.Invoke(hash);
            return string.IsNullOrEmpty(resolved) ? $"0x{hash:x8}" : resolved!;
        }

        private string ClassLiteral(uint hash)
        {
            var resolved = _name?.Invoke(hash);
            return string.IsNullOrEmpty(resolved) ? $"0x{hash:x8}" : resolved!;
        }

        /// <summary>A hash-valued scalar: the resolved name in quotes, or the raw hash.</summary>
        private string HashLiteral(uint hash)
        {
            var resolved = _name?.Invoke(hash);
            return string.IsNullOrEmpty(resolved) ? $"0x{hash:x8}" : Quote(resolved!);
        }

        private string WadLiteral(ulong hash)
        {
            var resolved = _wad?.Invoke(hash);
            return string.IsNullOrEmpty(resolved) ? $"0x{hash:x16}" : Quote(resolved!);
        }

        private void Line(int depth, string text) =>
            _sb.Append(' ', depth * 2).Append(text).Append('\n');
    }

    /// <summary>LeagueToolkit models both Struct (pointer) and Embedded with BinTreeStruct; the class hash
    /// lives on the concrete type.</summary>
    internal static uint StructClass(BinTreeStruct s) => s.ClassHash;

    /// <summary>An option's element type comes from its value; an empty option has nothing to read it from,
    /// and ritobin still needs a type in the brackets. None is the honest answer there.</summary>
    internal static BinPropertyType OptionalElementType(BinTreeOptional optional) =>
        optional.Value?.Type ?? BinPropertyType.None;

    /// <summary>Quote and escape a string the way the reader expects.</summary>
    public static string Quote(string? value)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in value ?? "")
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        return sb.Append('"').ToString();
    }
}
