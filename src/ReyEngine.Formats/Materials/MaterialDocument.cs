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
    public Dictionary<string, string> SubmeshDiffuse() => SubmeshSampler(b => b.Diffuse);

    /// <summary>Generic submesh → secondary-sampler path map (mask/gradient/emissive), live.</summary>
    public Dictionary<string, string> SubmeshSampler(Func<MaterialBinding, TextureSlot?> pick)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in Materials)
        {
            var p = pick(b)?.Path;
            if (string.IsNullOrEmpty(p)) continue;
            foreach (var sub in b.Submeshes) map[sub] = p;
        }
        return map;
    }

    private string? DefaultSampler(Func<MaterialBinding, TextureSlot?> pick) =>
        Materials.Where(m => m.IsDefault).Select(m => pick(m)?.Path).FirstOrDefault(p => !string.IsNullOrEmpty(p));

    public string? DefaultMaskPath => DefaultSampler(b => b.Mask);
    public string? DefaultGradientPath => DefaultSampler(b => b.Gradient);
    public string? DefaultEmissivePath => DefaultSampler(b => b.Emissive);
    public string? DefaultMatCapPath => DefaultSampler(b => b.MatCap);
    public string? DefaultMatCapMaskPath => DefaultSampler(b => b.MatCapMask);

    /// <summary>Champion: submesh name → preview profile (M32). Only real StaticMaterialDef bindings
    /// contribute a profile (the skin-default-texture/inline bindings carry no switches/params).</summary>
    public Dictionary<string, MaterialProfile> SubmeshProfiles()
    {
        var map = new Dictionary<string, MaterialProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in Materials)
        {
            if (!b.IsStaticMaterialDef) continue;
            foreach (var sub in b.Submeshes) map[sub] = b.Profile;
        }
        return map;
    }

    /// <summary>Profile for submeshes with no override — the default StaticMaterialDef's profile.</summary>
    public MaterialProfile DefaultProfile =>
        Materials.FirstOrDefault(m => m.IsDefault && m.IsStaticMaterialDef)?.Profile
        ?? Materials.FirstOrDefault(m => m.IsStaticMaterialDef)?.Profile
        ?? MaterialProfile.Default;

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
        var tree = SafeBinTree.Parse(data);
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
            var samplers = Field(o.Properties, "samplerValues") as BinTreeContainer;
            // Also parse sampler-LESS StaticMaterialDefs — effect/indicator materials (e.g. FaeLights:
            // no textures, just TintColor + blend) so they get a real profile instead of the opaque grey
            // fallback. Non-material objects (vfx/controllers) still get skipped.
            bool isStaticMat = string.Equals(resolve(o.ClassHash), "StaticMaterialDef", StringComparison.OrdinalIgnoreCase);
            if (samplers is null && !isStaticMat) continue;

            string name = (Field(o.Properties, "name") as BinTreeString)?.Value ?? resolve(pathHash) ?? $"0x{pathHash:x8}";
            string shader = resolve(o.ClassHash) ?? "StaticMaterialDef";

            var slots = new List<TextureSlot>();
            uint nameFieldHash = 0, pathFieldHash = 0;
            int diffuseAddrU = 0, diffuseAddrV = 0;   // M34: texture wrap mode for the diffuse sampler (1=Clamp)
            if (samplers is not null)
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
                    // Capture the diffuse sampler's addressU/V (else the first sampler) — decals use Clamp (1).
                    if (diffuseAddrU == 0 && (sampler.Contains("Diffuse", StringComparison.OrdinalIgnoreCase) || slots.Count == 0))
                    { diffuseAddrU = AsByte(Field(s.Properties, "addressU")); diffuseAddrV = AsByte(Field(s.Properties, "addressV")); }
                    slots.Add(new TextureSlot(sampler, pathProp, el));
                }

            var parameters = new List<MaterialParameter>();
            BinTreeContainer? paramContainer = null;
            if (Field(o.Properties, "paramValues") is BinTreeContainer pv)
            {
                paramContainer = pv;
                foreach (var el in pv.Elements)
                    if (el is BinTreeStruct ps
                        && Field(ps.Properties, "name") is BinTreeString pn
                        && Field(ps.Properties, "value") is { } valProp)
                        parameters.Add(new MaterialParameter(pn.Value, valProp, ps));
            }

            // Shader feature switches (StaticMaterialSwitchDef: 'name' + optional 'on'; absent 'on' = true).
            // M103: the structs are kept live so the switches can be toggled/added/removed, not just read.
            var switches = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var switchList = new List<MaterialSwitch>();
            BinTreeContainer? switchContainer = null;
            if (Field(o.Properties, "switches") is BinTreeContainer sw)
            {
                switchContainer = sw;
                foreach (var el in sw.Elements)
                    if (el is BinTreeStruct ss && Field(ss.Properties, "name") is BinTreeString sn)
                    {
                        bool on = Field(ss.Properties, "on") switch
                        {
                            BinTreeBool ob => ob.Value,
                            BinTreeBitBool obb => obb.Value,
                            _ => true, // an entry with no explicit 'on' is enabled
                        };
                        switches[sn.Value] = on;
                        switchList.Add(new MaterialSwitch(sn.Value, on, ss));
                    }
            }

            // Technique/pass render state (M34): the FIRST technique's FIRST pass carries the real shader
            // link + blend state (the class-hash "shader" above is just "StaticMaterialDef").
            string? renderShader = null;
            bool blendEnable = false;
            bool? cullEnable = null;
            int srcBlend = -1, dstBlend = -1;
            BinTreeObjectLink? shaderLink = null;   // M52: kept live so the shader can be CHANGED
            if (Field(o.Properties, "techniques") is BinTreeContainer techs
                && techs.Elements.OfType<BinTreeStruct>().FirstOrDefault() is { } tech0
                && Field(tech0.Properties, "passes") is BinTreeContainer passes
                && passes.Elements.OfType<BinTreeStruct>().FirstOrDefault() is { } pass0)
            {
                if (Field(pass0.Properties, "shader") is BinTreeObjectLink shLink)
                {
                    shaderLink = shLink;
                    renderShader = resolve(shLink.Value) ?? $"0x{shLink.Value:x8}";
                }
                blendEnable = Field(pass0.Properties, "blendEnable") switch
                {
                    BinTreeBool bb => bb.Value,
                    BinTreeBitBool bbb => bbb.Value,
                    _ => false,
                };
                // Riot's real backface-culling flag (StaticMaterialPassDef.cullEnable): true = single-sided
                // (cull back faces), false = double-sided. Null when absent (schema default).
                cullEnable = Field(pass0.Properties, "cullEnable") switch
                {
                    BinTreeBool cb => cb.Value,
                    BinTreeBitBool cbb => cbb.Value,
                    _ => (bool?)null,
                };
                srcBlend = AsByte(Field(pass0.Properties, "srcColorBlendFactor"));
                dstBlend = AsByte(Field(pass0.Properties, "dstColorBlendFactor"));
            }

            var subs = assignment.TryGetValue(pathHash, out var list2)
                ? list2.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                : Array.Empty<string>();
            bool isDefault = defaultMaterialHash == pathHash;

            materials.Add(new MaterialBinding(name, renderShader ?? shader, subs, isDefault, slots, parameters)
            {
                SamplerContainer = samplers,
                NameFieldHash = nameFieldHash,
                PathFieldHash = pathFieldHash,
                Switches = switches,
                SwitchContainer = switchContainer,
                SwitchEntries = switchList,
                RenderShader = renderShader,
                ShaderLink = shaderLink,
                ParamContainer = paramContainer,
                BlendEnable = blendEnable,
                CullEnable = cullEnable,
                SrcBlendFactor = srcBlend,
                DstBlendFactor = dstBlend,
                DiffuseAddressU = diffuseAddrU,
                DiffuseAddressV = diffuseAddrV,
            });
        }

        var kind = champion ? MaterialSourceKind.ChampionSkin : MaterialSourceKind.MapMaterials;
        foreach (var b in materials) b.Profile = MaterialProfiles.Classify(b, kind);
        return new MaterialDocument(tree, kind, materials);
    }

    private static BinTreeProperty? Field(IReadOnlyDictionary<uint, BinTreeProperty> props, string name)
    {
        if (props.TryGetValue(HashAlgorithms.Fnv1aRaw(name), out var p)) return p;
        if (props.TryGetValue(HashAlgorithms.Fnv1a(name), out p)) return p;
        return null;
    }

    /// <summary>Read a small integer blend-factor field (stored as u8/byte); -1 when absent.</summary>
    private static int AsByte(BinTreeProperty? p) => p switch
    {
        BinTreeU8 u => u.Value,
        _ => p?.GetType().GetProperty("Value")?.GetValue(p) is { } v && int.TryParse(v.ToString(), out var n) ? n : -1,
    };

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
    private readonly List<MaterialParameter> _params;

    public string Name { get; }
    public string ShaderName { get; }
    public IReadOnlyList<string> Submeshes { get; }
    public bool IsDefault { get; }
    public IReadOnlyList<TextureSlot> Slots => _slots;
    public IReadOnlyList<MaterialParameter> Parameters => _params;

    // Set for StaticMaterialDef bindings — enables add/remove of sampler slots (M10).
    internal BinTreeContainer? SamplerContainer { get; init; }
    internal uint NameFieldHash { get; init; }
    internal uint PathFieldHash { get; init; }
    // M55: the live paramValues container — enables add/remove of parameters.
    internal BinTreeContainer? ParamContainer { get; init; }

    /// <summary>Shader feature switches (name → on). Only populated for StaticMaterialDef bindings (M32).
    /// This is the parse-time snapshot that feeds the preview profile — for editing use
    /// <see cref="SwitchEntries"/>, which stays in sync with the live bin.</summary>
    public IReadOnlyDictionary<string, bool> Switches { get; init; } = EmptySwitches;
    private static readonly IReadOnlyDictionary<string, bool> EmptySwitches = new Dictionary<string, bool>();

    // ---- M103: editable feature switches ----
    /// <summary>The material's switches as live, toggleable entries.</summary>
    public IReadOnlyList<MaterialSwitch> SwitchEntries { get; init; } = Array.Empty<MaterialSwitch>();
    internal BinTreeContainer? SwitchContainer { get; init; }

    /// <summary>Switches can only be added by cloning an existing entry's schema, so a material with no
    /// switches at all has nothing to clone from.</summary>
    public bool CanEditSwitches => SwitchContainer is not null
                                   && SwitchContainer.Elements.OfType<BinTreeStruct>().Any();

    /// <summary>Enable a shader feature switch this material doesn't carry yet.</summary>
    public MaterialSwitch? AddSwitch(string name)
    {
        if (SwitchContainer is null) return null;
        if (SwitchContainer.Elements.OfType<BinTreeStruct>().FirstOrDefault() is not { } proto) return null;

        var clone = (BinTreeStruct)BinTreeCloner.Clone(proto, 0);
        uint nameHash = 0;
        foreach (var h in new[] { HashAlgorithms.Fnv1aRaw("name"), HashAlgorithms.Fnv1a("name") })
            if (clone.Properties.ContainsKey(h)) { nameHash = h; break; }
        if (nameHash == 0 || clone.Properties[nameHash] is not BinTreeString ns) return null;
        ns.Value = name;

        SwitchContainer.Add(clone);
        var sw = new MaterialSwitch(name, true, clone);
        sw.SetOn(true);
        _switchEdits.Add(sw);
        _structurallyEdited = true;
        return sw;
    }

    public bool RemoveSwitch(MaterialSwitch sw)
    {
        if (SwitchContainer is null || !SwitchContainer.Remove(sw.Element)) return false;
        _switchEdits.Remove(sw);
        _structurallyEdited = true;
        return true;
    }

    /// <summary>Switches added after parse (SwitchEntries is the parse-time list).</summary>
    private readonly List<MaterialSwitch> _switchEdits = new();
    public IEnumerable<MaterialSwitch> AllSwitches => SwitchEntries.Concat(_switchEdits);

    /// <summary>The material's real technique-pass shader (e.g. Shaders/StaticMesh/DefaultEnv_Flat_AlphaTest),
    /// resolved from the first technique's first pass; null when the material has no techniques (M34).</summary>
    public string? RenderShader { get; internal set; }

    /// <summary>M52: the live pass 'shader' objlink, kept so the shader can be swapped in place.</summary>
    internal BinTreeObjectLink? ShaderLink { get; init; }
    public bool CanChangeShader => ShaderLink is not null;

    /// <summary>M52: point this material's first pass at a different shader (objlink = FNV1a of the shader
    /// path, same hashing the game uses for bin object links). Serialize() persists it.</summary>
    public bool SetRenderShader(string shaderPath)
    {
        if (ShaderLink is null || string.IsNullOrWhiteSpace(shaderPath)) return false;
        ShaderLink.Value = ReyEngine.Core.Hashing.HashAlgorithms.Fnv1a(shaderPath.Trim());
        RenderShader = shaderPath.Trim();
        _structurallyEdited = true;
        return true;
    }
    /// <summary>First pass's blendEnable — the .bin's own transparency flag (M34).</summary>
    public bool BlendEnable { get; init; }
    /// <summary>First pass's cullEnable — Riot's backface-culling flag: true = single-sided (cull back),
    /// false = double-sided. Null when the field is absent (M34).</summary>
    public bool? CullEnable { get; init; }
    /// <summary>Raw src/dst colour blend factors from the first pass (Riot enum; -1 when absent). Observed
    /// SR/HA values: 6 (SrcAlpha) / 7 (OneMinusSrcAlpha) for alpha blending.</summary>
    public int SrcBlendFactor { get; init; } = -1;
    public int DstBlendFactor { get; init; } = -1;
    /// <summary>Diffuse sampler's addressU/V wrap mode (Riot enum: 1 = Clamp — used by decals; else Wrap). M34.</summary>
    public int DiffuseAddressU { get; init; }
    public int DiffuseAddressV { get; init; }

    /// <summary>The derived RiotApprox preview profile (features + UV transform). Set during parse (M32).</summary>
    public MaterialProfile Profile { get; internal set; } = MaterialProfile.Default;

    /// <summary>True for real StaticMaterialDef bindings (they carry the switches/params that drive the profile).</summary>
    public bool IsStaticMaterialDef => SamplerContainer is not null;

    public MaterialBinding(string name, string shaderName, IReadOnlyList<string> submeshes, bool isDefault,
        List<TextureSlot> slots, IReadOnlyList<MaterialParameter> parameters)
    {
        Name = name; ShaderName = shaderName; Submeshes = submeshes; IsDefault = isDefault;
        _slots = slots; _params = parameters.ToList();
    }

    /// <summary>Display string for the submesh(es)/group this material drives.</summary>
    public string AssignedTo => Submeshes.Count > 0 ? string.Join(", ", Submeshes) : (IsDefault ? "(base mesh)" : "");

    /// <summary>The diffuse/albedo slot if present, else the first base-colour-safe slot (never a normal map).</summary>
    public TextureSlot? Diffuse =>
        _slots.FirstOrDefault(s => s.IsDiffuse)
        ?? _slots.FirstOrDefault(s => s.IsBaseColorCandidate)
        ?? _slots.FirstOrDefault(s => !s.IsNormal);

    // Secondary samplers (M19/M20) used by the RiotApprox preview.
    public TextureSlot? Mask => _slots.FirstOrDefault(s => s.IsMask);
    public TextureSlot? Gradient => _slots.FirstOrDefault(s => s.IsGradient);
    public TextureSlot? Emissive => _slots.FirstOrDefault(s => s.IsEmissive);
    public TextureSlot? MatCap => _slots.FirstOrDefault(s => s.IsMatCap);
    public TextureSlot? MatCapMask => _slots.FirstOrDefault(s => s.IsMatCapMask);

    public bool IsDirty => _structurallyEdited || _slots.Any(s => s.IsDirty) || Parameters.Any(p => p.IsDirty)
                           || SwitchEntries.Any(s => s.IsDirty);
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

    /// <summary>
    /// Re-insert a previously removed sampler — the EXACT original element, kept alive by its
    /// <see cref="TextureSlot"/> — for undo support. (Appended at the container end: BinTreeContainer
    /// has no positional insert, so a mid-list remove + undo may reorder samplers; order is not
    /// semantically meaningful for sampler lookup, which is by name.)
    /// </summary>
    public bool ReinsertSampler(TextureSlot slot)
    {
        if (SamplerContainer is null || slot.Element is null) return false;
        if (SamplerContainer.Elements.Contains(slot.Element)) return false; // already present
        SamplerContainer.Add(slot.Element);
        _slots.Add(slot);
        _structurallyEdited = true;
        return true;
    }

    public void Revert()
    {
        foreach (var s in _slots) s.Revert();
        foreach (var p in Parameters) p.Revert();
        foreach (var w in SwitchEntries) w.Revert();
    }

    // ---- M55: parameter add/remove (same clone-the-schema approach as samplers) ----

    /// <summary>True when parameters can be added (needs at least one existing param to clone the schema).</summary>
    public bool CanEditParameters => ParamContainer is not null && ParamContainer.Elements.OfType<BinTreeStruct>().Any();

    /// <summary>Add a parameter by cloning an existing one (keeps the value TYPE of the prototype — edit the
    /// value afterwards). Null when this material has no parameter to clone from.</summary>
    public MaterialParameter? AddParameter(string name)
    {
        if (ParamContainer is null) return null;
        if (ParamContainer.Elements.OfType<BinTreeStruct>().FirstOrDefault() is not { } proto) return null;

        var clone = (BinTreeStruct)BinTreeCloner.Clone(proto, 0);
        static uint HashOf(IReadOnlyDictionary<uint, BinTreeProperty> props, string n)
        {
            uint h = HashAlgorithms.Fnv1aRaw(n);
            if (props.ContainsKey(h)) return h;
            h = HashAlgorithms.Fnv1a(n);
            return props.ContainsKey(h) ? h : 0u;
        }
        uint nameHash = HashOf(clone.Properties, "name");
        uint valueHash = HashOf(clone.Properties, "value");
        if (nameHash == 0 || valueHash == 0) return null;
        if (clone.Properties[nameHash] is not BinTreeString ns) return null;
        ns.Value = name;

        ParamContainer.Add(clone);
        var p = new MaterialParameter(name, clone.Properties[valueHash], clone);
        _params.Add(p);
        _structurallyEdited = true;
        return p;
    }

    public bool RemoveParameter(MaterialParameter p)
    {
        if (ParamContainer is null || p.Element is null) return false;
        if (!ParamContainer.Remove(p.Element)) return false;
        _params.Remove(p);
        _structurallyEdited = true;
        return true;
    }
}

/// <summary>
/// M103: one shader feature switch (<c>StaticMaterialSwitchDef</c>) as a live, toggleable entry.
/// Riot treats a switch entry with no explicit <c>on</c> field as enabled, so turning one OFF means
/// adding the field rather than changing it.
/// </summary>
public sealed class MaterialSwitch
{
    private readonly bool _originalOn;
    internal BinTreeStruct Element { get; }

    public string Name { get; }
    public bool On { get; private set; }
    public bool IsDirty => On != _originalOn;

    internal MaterialSwitch(string name, bool on, BinTreeStruct element)
    {
        Name = name;
        On = on;
        _originalOn = on;
        Element = element;
    }

    public void SetOn(bool on)
    {
        On = on;
        uint hash = 0;
        foreach (var h in new[] { HashAlgorithms.Fnv1aRaw("on"), HashAlgorithms.Fnv1a("on") })
            if (Element.Properties.ContainsKey(h)) { hash = h; break; }

        if (hash != 0)
        {
            switch (Element.Properties[hash])
            {
                case BinTreeBool b: b.Value = on; return;
                case BinTreeBitBool bb: bb.Value = on; return;
            }
        }
        // No 'on' field yet — write one explicitly so the value survives a round-trip either way.
        hash = HashAlgorithms.Fnv1aRaw("on");
        Element.Properties[hash] = new BinTreeBool(hash, on);
    }

    public void Revert() => SetOn(_originalOn);
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

    // Secondary samplers (M19/M20). A "Color_Mask" counts as a mask, not a diffuse, so exclude diffuse-likes
    // first; a "MatCap_Mask" is the matcap's own mask, not the rim mask, so exclude MatCap from the rim mask.
    public bool IsMatCap => SamplerName.Contains("MatCap", OIC) && !SamplerName.Contains("Mask", OIC);
    public bool IsMatCapMask => SamplerName.Contains("MatCap", OIC) && SamplerName.Contains("Mask", OIC);
    public bool IsMask => !IsDiffuse && SamplerName.Contains("Mask", OIC) && !SamplerName.Contains("MatCap", OIC);
    public bool IsGradient => SamplerName.Contains("Gradient", OIC) || SamplerName.Contains("Gredient", OIC);
    public bool IsEmissive =>
        SamplerName.Contains("Emiss", OIC) || SamplerName.Contains("EmissionR", OIC) ||
        SamplerName.Contains("Glow", OIC) || SamplerName.Contains("Illum", OIC);

    // Normal map (M21): a tangent-space normal. We classify it so it's never shown as the base
    // texture; proper normal mapping needs tangents and is only applied by shaders that declare it.
    public bool IsNormal =>
        SamplerName.Contains("Normal", OIC) || SamplerName.Contains("_nrm", OIC) ||
        SamplerName.Contains("NormalMap", OIC) || SamplerName.EndsWith("_NM", OIC);

    /// <summary>A sampler that is safe to treat as the base colour (not a normal/mask/secondary map).</summary>
    public bool IsBaseColorCandidate => !IsNormal && !IsMask && !IsMatCap && !IsMatCapMask && !IsGradient && !IsEmissive;
}

/// <summary>One material parameter (e.g. a vec4 tint) editable via the M7 value editor.</summary>
public sealed class MaterialParameter
{
    private readonly BinTreeProperty _prop;

    public string Name { get; }
    public string OriginalText { get; }
    public string TypeName { get; }

    /// <summary>The underlying paramValues element (struct), for removal. Null for non-removable params.</summary>
    internal BinTreeProperty? Element { get; }
    public bool IsRemovable => Element is not null;

    public MaterialParameter(string name, BinTreeProperty prop, BinTreeProperty? element = null)
    {
        Name = name;
        _prop = prop;
        Element = element;
        OriginalText = BinValueEditor.Format(prop, _ => null);
        TypeName = prop.Type.ToString();
    }

    public string CurrentText => BinValueEditor.Format(_prop, _ => null);
    public bool IsEditable => BinValueEditor.KindOf(_prop) != BinValueKind.ReadOnly;
    public bool IsDirty => !string.Equals(CurrentText, OriginalText, StringComparison.Ordinal);

    /// <summary>Read the parameter as a Vector4 (scalars/vec2/vec3 zero-extend). False if not numeric (M32 UV read).</summary>
    public bool TryGetVector4(out System.Numerics.Vector4 v)
    {
        switch (_prop)
        {
            case BinTreeVector4 p: v = p.Value; return true;
            case BinTreeVector3 p: v = new System.Numerics.Vector4(p.Value, 0f); return true;
            case BinTreeVector2 p: v = new System.Numerics.Vector4(p.Value.X, p.Value.Y, 0f, 0f); return true;
            case BinTreeF32 p: v = new System.Numerics.Vector4(p.Value, 0f, 0f, 0f); return true;
            default: v = default; return false;
        }
    }

    /// <summary>Apply text (throws on invalid input — caller keeps the old value).</summary>
    public void Apply(string text) => BinValueEditor.Apply(_prop, text);
    public void Revert() { try { BinValueEditor.Apply(_prop, OriginalText); } catch { } }
}
