using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

// M10: array/struct element editing — see MaterialBinding.AddSampler/RemoveSampler + BinTreeCloner.

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

    /// <summary>Diffuse texture path for the base mesh (submeshes with no material override). Reads live (reflects edits).</summary>
    public string? DefaultDiffusePath =>
        Materials.Where(m => m.IsDefault).Select(m => m.Diffuse?.Path).FirstOrDefault(p => !string.IsNullOrEmpty(p));

    /// <summary>Champion: submesh name → diffuse texture path (live). Each material's submeshes resolve to its diffuse slot.</summary>
    public Dictionary<string, string> SubmeshDiffuse()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in Materials)
        {
            var d = b.Diffuse?.Path;
            if (string.IsNullOrEmpty(d)) continue;
            foreach (var sub in b.Submeshes) map[sub] = d;
        }
        return map;
    }

    /// <summary>Map (or any): material name → diffuse texture path (live).</summary>
    public Dictionary<string, string> MaterialDiffuse()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in Materials)
            if (b.Diffuse?.Path is { Length: > 0 } d) map[b.Name] = d;
        return map;
    }

    public static MaterialDocument Parse(byte[] data, Func<uint, string?> resolve)
    {
        var tree = new BinTree(new MemoryStream(data, writable: false));
        bool champion = tree.Objects.Values.Any(o => Field(o.Properties, "skinMeshProperties") is not null);
        var materials = new List<MaterialBinding>();

        // Champion: default diffuse + reverse map material-object -> submesh(es) from materialOverride.
        var assignment = new Dictionary<uint, List<string>>();
        uint? defaultMaterialHash = null;
        if (champion)
        {
            foreach (var o in tree.Objects.Values)
            {
                if (Field(o.Properties, "skinMeshProperties") is not BinTreeStruct smp) continue;

                if (Field(smp.Properties, "texture") is BinTreeString defTex)
                    materials.Add(new MaterialBinding(
                        "(skin default texture)", "SkinMeshDataProperties", Array.Empty<string>(), isDefault: true,
                        new List<TextureSlot> { new("texture", defTex) }, new List<MaterialParameter>()));

                // The default material applies to every submesh not covered by an override.
                if (Field(smp.Properties, "material") is BinTreeObjectLink defMat) defaultMaterialHash = defMat.Value;

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
                                $"(inline override: {submesh})", "MaterialOverride", new[] { submesh }, isDefault: false,
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
            uint nameFieldHash = 0, pathFieldHash = 0;
            foreach (var el in samplers.Elements)
            {
                if (el is not BinTreeStruct s) continue;
                // League sampler structs: 'TextureName' holds the sampler name (e.g. Diffuse_Texture),
                // 'texturePath' holds the .tex path. (Some schemas fall back to samplerName/textureName.)
                if (nameFieldHash == 0) nameFieldHash = FieldHash(s.Properties, "TextureName", "samplerName");
                if (pathFieldHash == 0) pathFieldHash = FieldHash(s.Properties, "texturePath", "textureName");
                string sampler = (Field(s.Properties, "TextureName") as BinTreeString)?.Value
                                 ?? (Field(s.Properties, "samplerName") as BinTreeString)?.Value ?? "(sampler)";
                var pathProp = (Field(s.Properties, "texturePath") as BinTreeString)
                               ?? (Field(s.Properties, "textureName") as BinTreeString);
                if (pathProp is null) continue;
                slots.Add(new TextureSlot(sampler, pathProp, el));
            }

            var parameters = new List<MaterialParameter>();
            if (Field(o.Properties, "paramValues") is BinTreeContainer pv)
                foreach (var el in pv.Elements)
                    if (el is BinTreeStruct ps
                        && Field(ps.Properties, "name") is BinTreeString pn
                        && Field(ps.Properties, "value") is { } valProp)
                        parameters.Add(new MaterialParameter(pn.Value, valProp));

            var subs = assignment.TryGetValue(pathHash, out var list2)
                ? list2.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                : Array.Empty<string>();
            bool isDefault = defaultMaterialHash == pathHash;

            materials.Add(new MaterialBinding(name, shader, subs, isDefault, slots, parameters)
            {
                SamplerContainer = samplers,
                NameFieldHash = nameFieldHash,
                PathFieldHash = pathFieldHash,
            });
        }

        return new MaterialDocument(tree, champion ? MaterialSourceKind.ChampionSkin : MaterialSourceKind.MapMaterials, materials);
    }

    private static BinTreeProperty? Field(IReadOnlyDictionary<uint, BinTreeProperty> props, string name)
    {
        if (props.TryGetValue(HashAlgorithms.Fnv1aRaw(name), out var p)) return p;
        if (props.TryGetValue(HashAlgorithms.Fnv1a(name), out p)) return p;
        return null;
    }

    private static uint FieldHash(IReadOnlyDictionary<uint, BinTreeProperty> props, params string[] names)
    {
        foreach (var name in names)
        {
            uint h1 = HashAlgorithms.Fnv1aRaw(name);
            if (props.ContainsKey(h1)) return h1;
            uint h2 = HashAlgorithms.Fnv1a(name);
            if (props.ContainsKey(h2)) return h2;
        }
        return 0;
    }
}

public sealed class MaterialBinding
{
    private readonly List<TextureSlot> _slots;

    public string Name { get; }
    public string ShaderName { get; }
    public IReadOnlyList<string> Submeshes { get; }
    public bool IsDefault { get; }
    public IReadOnlyList<TextureSlot> Slots => _slots;
    public IReadOnlyList<MaterialParameter> Parameters { get; }

    // Set for StaticMaterialDef bindings — enables add/remove of sampler slots (M10).
    internal BinTreeContainer? SamplerContainer { get; init; }
    internal uint NameFieldHash { get; init; }
    internal uint PathFieldHash { get; init; }

    public MaterialBinding(string name, string shaderName, IReadOnlyList<string> submeshes, bool isDefault,
        List<TextureSlot> slots, IReadOnlyList<MaterialParameter> parameters)
    {
        Name = name; ShaderName = shaderName; Submeshes = submeshes; IsDefault = isDefault;
        _slots = slots; Parameters = parameters;
    }

    /// <summary>Display string for the submesh(es)/group this material drives.</summary>
    public string AssignedTo => Submeshes.Count > 0 ? string.Join(", ", Submeshes) : (IsDefault ? "(base mesh)" : "");

    /// <summary>The diffuse/albedo slot if present, else the first texture slot.</summary>
    public TextureSlot? Diffuse => _slots.FirstOrDefault(s => s.IsDiffuse) ?? _slots.FirstOrDefault();

    public bool IsDirty => _structurallyEdited || _slots.Any(s => s.IsDirty) || Parameters.Any(p => p.IsDirty);
    private bool _structurallyEdited;

    /// <summary>True when this material exposes editable sampler slots that can be added/removed.</summary>
    public bool CanEditSamplers => SamplerContainer is not null && SamplerContainer.Elements.Count > 0 && PathFieldHash != 0;

    /// <summary>Add a sampler slot by cloning an existing one (keeps the schema) and setting its name + path.</summary>
    public TextureSlot? AddSampler(string samplerName, string path)
    {
        if (SamplerContainer is null || PathFieldHash == 0) return null;
        if (SamplerContainer.Elements.FirstOrDefault() is not { } proto) return null;

        var clone = (BinTreeStruct)BinTreeCloner.Clone(proto, 0);
        if (clone.Properties.TryGetValue(PathFieldHash, out var p) && p is BinTreeString pathStr)
            pathStr.Value = path;
        else return null;
        if (NameFieldHash != 0 && clone.Properties.TryGetValue(NameFieldHash, out var n) && n is BinTreeString nameStr)
            nameStr.Value = samplerName;

        SamplerContainer.Add(clone);
        var slot = new TextureSlot(samplerName, (BinTreeString)clone.Properties[PathFieldHash], clone);
        _slots.Add(slot);
        _structurallyEdited = true;
        return slot;
    }

    public bool RemoveSampler(TextureSlot slot)
    {
        if (SamplerContainer is null || slot.Element is null) return false;
        if (!SamplerContainer.Remove(slot.Element)) return false;
        _slots.Remove(slot);
        _structurallyEdited = true;
        return true;
    }

    public void Revert() { foreach (var s in _slots) s.Revert(); foreach (var p in Parameters) p.Revert(); }
}

/// <summary>One texture sampler slot whose path is an editable live BinTree string.</summary>
public sealed class TextureSlot
{
    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
    private readonly BinTreeString _prop;

    public string SamplerName { get; }
    public string OriginalPath { get; }

    /// <summary>The underlying sampler element (struct), for removal. Null for inline/default slots.</summary>
    internal BinTreeProperty? Element { get; }

    public TextureSlot(string samplerName, BinTreeString prop, BinTreeProperty? element = null)
    {
        SamplerName = samplerName;
        _prop = prop;
        OriginalPath = prop.Value;
        Element = element;
    }

    public string Path => _prop.Value;
    public bool IsRemovable => Element is not null;
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
