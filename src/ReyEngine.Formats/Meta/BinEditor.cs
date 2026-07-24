using System.Globalization;
using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;

namespace ReyEngine.Formats.Meta;

public enum BinValueKind { ReadOnly, Bool, Int, UInt, Float, String, Hash, Vector2, Vector3, Vector4 }

/// <summary>
/// Editable wrapper over a live (mutable) BinTree. Editing mutates primitive property values in
/// place; everything else (complex objects, arrays, unknown fields, hashes) is preserved by
/// LeagueToolkit's own serializer on <see cref="Serialize"/>. Build editing on top of this.
/// </summary>
public sealed class BinEditorDocument
{
    private readonly BinTree _tree;

    public IReadOnlyList<EditableBinField> Roots { get; }
    public IReadOnlyList<string> Dependencies => _tree.Dependencies;

    private BinEditorDocument(BinTree tree, IReadOnlyList<EditableBinField> roots)
    {
        _tree = tree;
        Roots = roots;
    }

    public static BinEditorDocument Parse(byte[] data, Func<uint, string?> resolve)
    {
        var tree = SafeBinTree.Parse(data);
        var roots = new List<EditableBinField>(tree.Objects.Count);
        foreach (var (pathHash, obj) in tree.Objects)
        {
            string className = Name(obj.ClassHash, resolve);
            var node = new EditableBinField
            {
                Name = className,
                NameHash = pathHash,
                TypeName = "object",
                IsBranch = true,
                Kind = BinValueKind.ReadOnly,
                PathLabel = className,
            };
            foreach (var (nh, prop) in obj.Properties)
                node.Children.Add(Build(Name(nh, resolve), nh, prop, resolve, className));
            roots.Add(node);
        }
        return new BinEditorDocument(tree, roots);
    }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        _tree.Write(ms);
        return ms.ToArray();
    }

    private static EditableBinField Build(string name, uint nameHash, BinTreeProperty p, Func<uint, string?> resolve, string parentPath)
    {
        string path = parentPath + " / " + name;
        switch (p)
        {
            case BinTreeContainer c:
            {
                var node = new EditableBinField { Name = name, NameHash = nameHash, TypeName = $"{c.ElementType}[{c.Elements.Count}]", IsBranch = true, Kind = BinValueKind.ReadOnly, PathLabel = path };
                for (int i = 0; i < c.Elements.Count; i++)
                    node.Children.Add(Build($"[{i}]", 0, c.Elements[i], resolve, path));
                return node;
            }
            case BinTreeStruct s:
            {
                var node = new EditableBinField { Name = name, NameHash = nameHash, TypeName = Name(s.ClassHash, resolve), IsBranch = true, Kind = BinValueKind.ReadOnly, PathLabel = path };
                foreach (var (nh, child) in s.Properties)
                    node.Children.Add(Build(Name(nh, resolve), nh, child, resolve, path));
                return node;
            }
            case BinTreeOptional o:
            {
                var node = new EditableBinField { Name = name, NameHash = nameHash, TypeName = "optional", IsBranch = true, Kind = BinValueKind.ReadOnly, PathLabel = path };
                if (o.Value is not null) node.Children.Add(Build("value", 0, o.Value, resolve, path));
                return node;
            }
            // M151: Map (e.g. StaticMaterialDef.shaderMacros) was falling through to the leaf case, so it
            // rendered as an empty un-expandable row — the macros were invisible and uneditable even though
            // the data was there. Each entry becomes a child named by its key, with the VALUE editable.
            case BinTreeMap m:
            {
                var node = new EditableBinField
                {
                    Name = name, NameHash = nameHash,
                    TypeName = $"Map[{m.Count}]",
                    IsBranch = true, Kind = BinValueKind.ReadOnly, PathLabel = path,
                };
                foreach (var e in m)
                {
                    string key = e.Key switch
                    {
                        BinTreeString ks => ks.Value,
                        BinTreeHash kh => resolve(kh.Value) ?? $"0x{kh.Value:x8}",
                        _ => e.Key?.ToString() ?? "?",
                    };
                    node.Children.Add(Build(key, 0, e.Value, resolve, path));
                }
                return node;
            }
            default:
                return new EditableBinField
                {
                    Name = name,
                    NameHash = nameHash,
                    TypeName = p.Type.ToString(),
                    IsBranch = false,
                    Kind = BinValueEditor.KindOf(p),
                    PathLabel = path,
                    OriginalText = BinValueEditor.Format(p, resolve),
                    Property = p,
                };
        }
    }

    private static string Name(uint hash, Func<uint, string?> resolve) => resolve(hash) ?? $"0x{hash:x8}";
}

public sealed class EditableBinField
{
    public required string Name { get; init; }
    public required uint NameHash { get; init; }
    public required string TypeName { get; init; }
    public required bool IsBranch { get; init; }
    public required BinValueKind Kind { get; init; }
    public required string PathLabel { get; init; }
    public string OriginalText { get; init; } = "";
    public List<EditableBinField> Children { get; } = new();
    internal BinTreeProperty? Property { get; init; }

    public bool IsEditable => !IsBranch && Kind != BinValueKind.ReadOnly && Property is not null;

    /// <summary>M98e: the property's CURRENT value — <see cref="OriginalText"/> is a parse-time snapshot
    /// and goes stale the moment an edit is applied; row UIs must display this instead.</summary>
    public string CurrentText(Func<uint, string?> resolve) =>
        Property is null ? OriginalText : BinValueEditor.Format(Property, resolve);

    /// <summary>Apply text to the underlying property (throws on invalid input).</summary>
    public void Apply(string text)
    {
        if (Property is null) throw new InvalidOperationException("Not an editable field.");
        BinValueEditor.Apply(Property, text);
    }
}

public static class BinValueEditor
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static BinValueKind KindOf(BinTreeProperty p) => p switch
    {
        // M151: Color is 4 editable floats (rgba); without this it was ReadOnly and showed as "Color".
        BinTreeColor => BinValueKind.Vector4,
        BinTreeBool or BinTreeBitBool => BinValueKind.Bool,
        BinTreeI8 or BinTreeI16 or BinTreeI32 or BinTreeI64 => BinValueKind.Int,
        BinTreeU8 or BinTreeU16 or BinTreeU32 or BinTreeU64 => BinValueKind.UInt,
        BinTreeF32 => BinValueKind.Float,
        BinTreeString => BinValueKind.String,
        BinTreeHash => BinValueKind.Hash,
        BinTreeVector2 => BinValueKind.Vector2,
        BinTreeVector3 => BinValueKind.Vector3,
        BinTreeVector4 => BinValueKind.Vector4,
        _ => BinValueKind.ReadOnly,
    };

    public static string Format(BinTreeProperty p, Func<uint, string?> resolve) => p switch
    {
        BinTreeBool b => b.Value ? "true" : "false",
        BinTreeBitBool b => b.Value ? "true" : "false",
        BinTreeI8 v => v.Value.ToString(Inv),
        BinTreeU8 v => v.Value.ToString(Inv),
        BinTreeI16 v => v.Value.ToString(Inv),
        BinTreeU16 v => v.Value.ToString(Inv),
        BinTreeI32 v => v.Value.ToString(Inv),
        BinTreeU32 v => v.Value.ToString(Inv),
        BinTreeI64 v => v.Value.ToString(Inv),
        BinTreeU64 v => v.Value.ToString(Inv),
        BinTreeF32 v => v.Value.ToString("R", Inv),
        BinTreeString v => v.Value,
        BinTreeHash v => $"0x{v.Value:x8}",
        BinTreeVector2 v => $"{v.Value.X.ToString("R", Inv)}, {v.Value.Y.ToString("R", Inv)}",
        BinTreeVector3 v => $"{v.Value.X.ToString("R", Inv)}, {v.Value.Y.ToString("R", Inv)}, {v.Value.Z.ToString("R", Inv)}",
        BinTreeVector4 v => $"{v.Value.X.ToString("R", Inv)}, {v.Value.Y.ToString("R", Inv)}, {v.Value.Z.ToString("R", Inv)}, {v.Value.W.ToString("R", Inv)}",
        BinTreeObjectLink l => $"0x{l.Value:x8}",
        // M151: these were falling through to the bare type name, so their values were invisible.
        BinTreeColor c => $"{c.Value.R.ToString("R", Inv)}, {c.Value.G.ToString("R", Inv)}, {c.Value.B.ToString("R", Inv)}, {c.Value.A.ToString("R", Inv)}",
        BinTreeMatrix44 m => FormatMatrix(m.Value),
        _ => p.Type.ToString(),
    };

    /// <summary>M151: a 4x4 matrix as four comma-separated rows — readable, and not editable as text.</summary>
    private static string FormatMatrix(Matrix4x4 m) =>
        $"[{m.M11:0.###}, {m.M12:0.###}, {m.M13:0.###}, {m.M14:0.###}] " +
        $"[{m.M21:0.###}, {m.M22:0.###}, {m.M23:0.###}, {m.M24:0.###}] " +
        $"[{m.M31:0.###}, {m.M32:0.###}, {m.M33:0.###}, {m.M34:0.###}] " +
        $"[{m.M41:0.###}, {m.M42:0.###}, {m.M43:0.###}, {m.M44:0.###}]";

    public static void Apply(BinTreeProperty p, string text)
    {
        var t = text.Trim();
        switch (p)
        {
            case BinTreeBool b: b.Value = ParseBool(t); break;
            case BinTreeBitBool b: b.Value = ParseBool(t); break;
            case BinTreeI8 v: v.Value = sbyte.Parse(t, Inv); break;
            case BinTreeU8 v: v.Value = byte.Parse(t, Inv); break;
            case BinTreeI16 v: v.Value = short.Parse(t, Inv); break;
            case BinTreeU16 v: v.Value = ushort.Parse(t, Inv); break;
            case BinTreeI32 v: v.Value = int.Parse(t, Inv); break;
            case BinTreeU32 v: v.Value = uint.Parse(t, Inv); break;
            case BinTreeI64 v: v.Value = long.Parse(t, Inv); break;
            case BinTreeU64 v: v.Value = ulong.Parse(t, Inv); break;
            case BinTreeF32 v: v.Value = float.Parse(t, NumberStyles.Float, Inv); break;
            case BinTreeString v: v.Value = text; break;
            case BinTreeHash v: v.Value = ParseHexOrUInt(t); break;
            case BinTreeColor v: { var f = ParseFloats(t, 4); v.Value = new LeagueToolkit.Core.Primitives.Color(f[0], f[1], f[2], f[3]); break; }   // M151
            case BinTreeVector2 v: { var f = ParseFloats(t, 2); v.Value = new Vector2(f[0], f[1]); break; }
            case BinTreeVector3 v: { var f = ParseFloats(t, 3); v.Value = new Vector3(f[0], f[1], f[2]); break; }
            case BinTreeVector4 v: { var f = ParseFloats(t, 4); v.Value = new Vector4(f[0], f[1], f[2], f[3]); break; }
            default: throw new NotSupportedException($"{p.Type} is read-only.");
        }
    }

    private static bool ParseBool(string t) => t.ToLowerInvariant() switch
    {
        "true" or "1" or "yes" => true,
        "false" or "0" or "no" => false,
        _ => throw new FormatException("Expected true/false."),
    };

    private static uint ParseHexOrUInt(string t)
    {
        var s = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? t[2..] : t;
        if (uint.TryParse(s, NumberStyles.HexNumber, Inv, out var hex)) return hex;
        return uint.Parse(t, Inv);
    }

    private static float[] ParseFloats(string t, int count)
    {
        var parts = t.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != count) throw new FormatException($"Expected {count} comma-separated numbers.");
        var f = new float[count];
        for (int i = 0; i < count; i++) f[i] = float.Parse(parts[i], NumberStyles.Float, Inv);
        return f;
    }
}
