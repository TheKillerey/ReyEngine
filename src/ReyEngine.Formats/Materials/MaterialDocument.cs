using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Materials;

public enum MaterialSourceKind { ChampionSkin, MapMaterials }

/// <summary>
/// A material-centric editable view over a champion skin .bin or a map .materials.bin. Wraps a
/// live (mutable) LeagueToolkit BinTree; texture-slot paths and numeric params reference the
/// underlying BinTree properties so edits mutate the tree in place. <see cref="Serialize"/>
/// re-writes the whole tree (preserving everything else) — feed the result back through the
/// existing material resolvers for live preview, or save it as a project override.
/// Built on top of the M7 .bin editing primitives (<see cref="BinValueEditor"/>).
/// </summary>
public sealed class MaterialDocument
{
    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
    private readonly BinTree _tree;

    public MaterialSourceKind Kind { get; }
    public IReadOnlyList<MaterialBinding> Materials { get; }
    public bool IsDirty => Materials.Any(m => m.IsDirty);

    private MaterialDocument(BinTree tree, MaterialSourceKind kind, IReadOnlyList<MaterialBinding> materials)
    {
        _tree = tree;
        Kind = kind;
        Materials = materials;
    }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        _tree.Write(ms);
        return ms.ToArray();
    }

    public static MaterialDocument Parse(byte[] data, Func<uint, string?> resolve)
    {
        var tree = new BinTree(new MemoryStream(data, writable: false));
        bool champion = tree.Objects.Values.Any(o => Field(o.Properties, "skinMeshProperties") is not null);
        var materials = new List<MaterialBinding>();

        // Champion: default diffuse + reverse map material-object -> submesh(es) from materialOverride.
        var assignment = new Dictionary<uint, List<string>>();
        if (champion)
        {
            foreach (var o in tree.Objects.Values)
            {
                if (Field(o.Properties, "skinMeshProperties") is not BinTreeStruct smp) continue;

                if (Field(smp.Properties, "texture") is BinTreeString defTex)
                    materials.Add(new MaterialBinding(
                        "(skin default texture)", "SkinMeshDataProperties", "(base mesh)",
                        new List<TextureSlot> { new("texture", defTex) }, new List<MaterialParameter>()));

                if (Field(smp.Properties, "materialOverride") is BinTreeContainer overrides)
                {
                    foreach (var el in overrides.Elements)
                    {
                        if (el is not BinTreeStruct ov) continue;
                        var submesh = (Field(ov.Properties, "submesh") as BinTreeString)?.Value;
                        if (string.IsNullOrEmpty(submesh)) continue;

                        if (Field(ov.Properties, "material") is BinTreeObjectLink ml)
                        {
                            if (!assignment.TryGetValue(ml.Value, out var list)) assignment[ml.Value] = list = new();
                            list.Add(submesh);
                        }
                        if (Field(ov.Properties, "texture") is BinTreeString inlineTex)
                            materials.Add(new MaterialBinding(
                                $"(inline override: {submesh})", "MaterialOverride", submesh,
                                new List<TextureSlot> { new("texture", inlineTex) }, new List<MaterialParameter>()));
                    }
                }
                break;
            }
        }

        // Every StaticMaterialDef (shared by champions and maps).
        foreach (var (pathHash, o) in tree.Objects)
        {
            if (Field(o.Properties, "samplerValues") is not BinTreeContainer samplers) continue;

            string name = (Field(o.Properties, "name") as BinTreeString)?.Value ?? resolve(pathHash) ?? $"0x{pathHash:x8}";
            string shader = resolve(o.ClassHash) ?? "StaticMaterialDef";

            var slots = new List<TextureSlot>();
            foreach (var el in samplers.Elements)
            {
                if (el is not BinTreeStruct s) continue;
                string sampler = (Field(s.Properties, "TextureName") as BinTreeString)?.Value
                                 ?? (Field(s.Properties, "samplerName") as BinTreeString)?.Value ?? "(sampler)";
                var pathProp = (Field(s.Properties, "texturePath") as BinTreeString)
                               ?? (Field(s.Properties, "textureName") as BinTreeString);
                if (pathProp is null) continue;
                slots.Add(new TextureSlot(sampler, pathProp));
            }

            var parameters = new List<MaterialParameter>();
            if (Field(o.Properties, "paramValues") is BinTreeContainer pv)
                foreach (var el in pv.Elements)
                    if (el is BinTreeStruct ps
                        && Field(ps.Properties, "name") is BinTreeString pn
                        && Field(ps.Properties, "value") is { } valProp)
                        parameters.Add(new MaterialParameter(pn.Value, valProp));

            string assignedTo = assignment.TryGetValue(pathHash, out var subs)
                ? string.Join(", ", subs.Distinct(StringComparer.OrdinalIgnoreCase))
                : (champion ? "(shared / default)" : "");

            materials.Add(new MaterialBinding(name, shader, assignedTo, slots, parameters));
        }

        return new MaterialDocument(tree, champion ? MaterialSourceKind.ChampionSkin : MaterialSourceKind.MapMaterials, materials);
    }

    private static BinTreeProperty? Field(IReadOnlyDictionary<uint, BinTreeProperty> props, string name)
    {
        if (props.TryGetValue(HashAlgorithms.Fnv1aRaw(name), out var p)) return p;
        if (props.TryGetValue(HashAlgorithms.Fnv1a(name), out p)) return p;
        return null;
    }
}

public sealed class MaterialBinding
{
    public string Name { get; }
    public string ShaderName { get; }
    public string AssignedTo { get; }
    public IReadOnlyList<TextureSlot> Slots { get; }
    public IReadOnlyList<MaterialParameter> Parameters { get; }

    public MaterialBinding(string name, string shaderName, string assignedTo,
        IReadOnlyList<TextureSlot> slots, IReadOnlyList<MaterialParameter> parameters)
    {
        Name = name; ShaderName = shaderName; AssignedTo = assignedTo;
        Slots = slots; Parameters = parameters;
    }

    public bool IsDirty => Slots.Any(s => s.IsDirty) || Parameters.Any(p => p.IsDirty);
    public void Revert() { foreach (var s in Slots) s.Revert(); foreach (var p in Parameters) p.Revert(); }
}

/// <summary>One texture sampler slot whose path is an editable live BinTree string.</summary>
public sealed class TextureSlot
{
    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
    private readonly BinTreeString _prop;

    public string SamplerName { get; }
    public string OriginalPath { get; }

    public TextureSlot(string samplerName, BinTreeString prop)
    {
        SamplerName = samplerName;
        _prop = prop;
        OriginalPath = prop.Value;
    }

    public string Path => _prop.Value;
    public void SetPath(string path) => _prop.Value = path ?? "";
    public void Revert() => _prop.Value = OriginalPath;
    public bool IsDirty => !string.Equals(_prop.Value, OriginalPath, StringComparison.Ordinal);

    public bool IsDiffuse =>
        SamplerName.Contains("Diffuse", OIC) || SamplerName.Contains("Albedo", OIC) ||
        SamplerName.Contains("Color", OIC) || SamplerName.Contains("Main", OIC);
}

/// <summary>One material parameter (e.g. a vec4 tint) editable via the M7 value editor.</summary>
public sealed class MaterialParameter
{
    private readonly BinTreeProperty _prop;

    public string Name { get; }
    public string OriginalText { get; }
    public string TypeName { get; }

    public MaterialParameter(string name, BinTreeProperty prop)
    {
        Name = name;
        _prop = prop;
        OriginalText = BinValueEditor.Format(prop, _ => null);
        TypeName = prop.Type.ToString();
    }

    public string CurrentText => BinValueEditor.Format(_prop, _ => null);
    public bool IsEditable => BinValueEditor.KindOf(_prop) != BinValueKind.ReadOnly;
    public bool IsDirty => !string.Equals(CurrentText, OriginalText, StringComparison.Ordinal);

    /// <summary>Apply text (throws on invalid input — caller keeps the old value).</summary>
    public void Apply(string text) => BinValueEditor.Apply(_prop, text);
    public void Revert() { try { BinValueEditor.Apply(_prop, OriginalText); } catch { } }
}
