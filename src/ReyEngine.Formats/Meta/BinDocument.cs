using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;

namespace ReyEngine.Formats.Meta;

/// <summary>A display node in the .bin property tree.</summary>
public sealed class BinNode
{
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
    public bool IsBranch { get; init; }
    public List<BinNode> Children { get; } = new();
}

public sealed class BinDocument
{
    public IReadOnlyList<BinNode> Roots { get; }
    public IReadOnlyList<string> Dependencies { get; }

    private BinDocument(IReadOnlyList<BinNode> roots, IReadOnlyList<string> dependencies)
    {
        Roots = roots;
        Dependencies = dependencies;
    }

    /// <summary>Parse a .bin into a readable tree. <paramref name="resolve"/> maps a name hash to a name (or null).</summary>
    public static BinDocument Parse(byte[] data, Func<uint, string?> resolve)
    {
        var bin = SafeBinTree.Parse(data);
        var roots = new List<BinNode>(bin.Objects.Count);

        foreach (var (pathHash, obj) in bin.Objects)
        {
            var node = new BinNode
            {
                Name = Name(obj.ClassHash, resolve),
                Value = $"0x{pathHash:x8}",
                IsBranch = true,
            };
            foreach (var (nameHash, prop) in obj.Properties)
                node.Children.Add(BuildProperty(Name(nameHash, resolve), prop, resolve));
            roots.Add(node);
        }

        return new BinDocument(roots, bin.Dependencies);
    }

    private static BinNode BuildProperty(string name, BinTreeProperty prop, Func<uint, string?> resolve)
    {
        switch (prop)
        {
            case BinTreeContainer container: // also covers UnorderedContainer
            {
                var node = new BinNode { Name = name, Value = $"{container.ElementType}[{container.Elements.Count}]", IsBranch = true };
                for (int i = 0; i < container.Elements.Count; i++)
                    node.Children.Add(BuildProperty($"[{i}]", container.Elements[i], resolve));
                return node;
            }
            case BinTreeStruct s: // also covers Embedded
            {
                var node = new BinNode { Name = name, Value = Name(s.ClassHash, resolve), IsBranch = true };
                foreach (var (nameHash, child) in s.Properties)
                    node.Children.Add(BuildProperty(Name(nameHash, resolve), child, resolve));
                return node;
            }
            case BinTreeOptional opt:
            {
                var node = new BinNode { Name = name, Value = "optional", IsBranch = true };
                if (opt.Value is not null)
                    node.Children.Add(BuildProperty("value", opt.Value, resolve));
                return node;
            }
            default:
                return new BinNode { Name = name, Value = FormatValue(prop, resolve) };
        }
    }

    private static string FormatValue(BinTreeProperty p, Func<uint, string?> resolve) => p switch
    {
        BinTreeString s => s.Value,
        BinTreeHash h => resolve(h.Value) ?? $"0x{h.Value:x8}",
        BinTreeObjectLink l => "→ " + (resolve(l.Value) ?? $"0x{l.Value:x8}"),
        BinTreeBool b => b.Value ? "true" : "false",
        BinTreeBitBool b => b.Value ? "true" : "false",
        BinTreeI8 v => v.Value.ToString(),
        BinTreeU8 v => v.Value.ToString(),
        BinTreeI16 v => v.Value.ToString(),
        BinTreeU16 v => v.Value.ToString(),
        BinTreeI32 v => v.Value.ToString(),
        BinTreeU32 v => v.Value.ToString(),
        BinTreeI64 v => v.Value.ToString(),
        BinTreeU64 v => v.Value.ToString(),
        BinTreeF32 v => v.Value.ToString("0.####"),
        BinTreeVector2 v => $"({v.Value.X:0.##}, {v.Value.Y:0.##})",
        BinTreeVector3 v => $"({v.Value.X:0.##}, {v.Value.Y:0.##}, {v.Value.Z:0.##})",
        BinTreeVector4 v => $"({v.Value.X:0.##}, {v.Value.Y:0.##}, {v.Value.Z:0.##}, {v.Value.W:0.##})",
        _ => p.Type.ToString(),
    };

    private static string Name(uint hash, Func<uint, string?> resolve) => resolve(hash) ?? $"0x{hash:x8}";
}
