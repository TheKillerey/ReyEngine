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

    /// <summary>M222: the champion skin's own material layer, when this bin has one. Separate from
    /// <see cref="Materials"/> because these are properties of the SKIN rather than of any one material -
    /// they apply across every submesh that does not override them.</summary>
    public SkinMeshProperties? SkinMesh { get; private set; }
    public bool IsDirty => Materials.Any(m => m.IsDirty);

    private MaterialDocument(BinTree tree, MaterialSourceKind kind, IReadOnlyList<MaterialBinding> materials)
    {
        _tree = tree;
        Kind = kind;
        Materials = materials;
    }

    /// <summary>M125: what the tolerant reader had to repair while reading this bin (empty = well-formed).
    /// <see cref="Serialize"/> always writes the repaired form, so saving clears these for good.</summary>
    public IReadOnlyList<BinRepairIssue> Issues { get; private init; } = Array.Empty<BinRepairIssue>();

    /// <summary>M106: re-derive one material's preview profile after its render state changed, so the
    /// derived read-outs (blend mode, depth-write, alpha-cutout) follow the fields the user just edited.</summary>
    public void Reclassify(MaterialBinding b) => b.Profile = MaterialProfiles.Classify(b, Kind);

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
    /// <summary>M222: read the SkinMeshDataProperties block. Every field is optional; anything absent
    /// stays null so a caller can tell "not authored" from "authored as zero".</summary>
    private static void ReadSkinMeshProperties(BinTreeStruct smp, out SkinMeshProperties info)
    {
        float? F(string name) => Field(smp.Properties, name) is BinTreeF32 f ? f.Value : null;
        string? S(string name) => Field(smp.Properties, name) is BinTreeString t && t.Value.Length > 0 ? t.Value : null;
        System.Numerics.Vector4? C(string name) => Field(smp.Properties, name) switch
        {
            BinTreeColor c => c.Value,
            BinTreeVector4 v => v.Value,
            _ => null,
        };

        var hide = new List<string>();
        foreach (var n in new[] { "initialSubmeshToHide", "initialSubmeshShadowsToHide" })
            if (S(n) is { } raw)
                foreach (var part in raw.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    if (n == "initialSubmeshToHide" && !hide.Contains(part, StringComparer.OrdinalIgnoreCase))
                        hide.Add(part);

        info = new SkinMeshProperties
        {
            Skeleton = S("skeleton"),
            SimpleSkin = S("simpleSkin"),
            Texture = S("texture"),
            GlossTexture = S("glossTexture"),
            ReflectionMap = S("reflectionMap"),
            SkinScale = F("skinScale"),
            SelfIllumination = F("selfIllumination"),
            BrushAlphaOverride = F("brushAlphaOverride"),
            Fresnel = F("fresnel"),
            FresnelColor = C("fresnelColor"),
            ReflectionOpacityDirect = F("reflectionOpacityDirect"),
            ReflectionOpacityGlancing = F("reflectionOpacityGlancing"),
            ReflectionFresnel = F("reflectionFresnel"),
            ReflectionFresnelColor = C("reflectionFresnelColor"),
            InitialSubmeshesToHide = hide,
        };
    }

    public Dictionary<string, string> MaterialDiffuse()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in Materials)
            if (b.Diffuse?.Path is { Length: > 0 } d) map[b.Name] = d;
        return map;
    }

    public static MaterialDocument Parse(byte[] data, Func<uint, string?> resolve)
    {
        var tree = SafeBinTree.Parse(data, out var issues);
        bool champion = tree.Objects.Values.Any(o => Field(o.Properties, "skinMeshProperties") is not null);
        var materials = new List<MaterialBinding>();

        // Champion: default diffuse + reverse map material-object -> submesh(es) from materialOverride.
        var assignment = new Dictionary<uint, List<string>>();
        uint? defaultMaterialHash = null;
        SkinMeshProperties? pendingSkinMesh = null;
        if (champion)
        {
            foreach (var o in tree.Objects.Values)
            {
                if (Field(o.Properties, "skinMeshProperties") is not BinTreeStruct smp) continue;

                ReadSkinMeshProperties(smp, out var skinInfo);
                pendingSkinMesh = skinInfo;

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
        // (pendingSkinMesh is attached to the document at the end.)
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

            // M150: shaderMacros — a SECOND, separate feature system from 'switches': a string->string map
            // of preprocessor defines, values "0"/"1". This is where Riot puts the flags that change how a
            // surface reacts to the scene rather than how it shades: NO_BAKED_LIGHTING (ignore the lightmap)
            // and DISABLE_DEPTH_FOG (exclude the mesh from distance fog). Map11 uses them heavily —
            // NO_BAKED_LIGHTING on 1901 materials, DISABLE_DEPTH_FOG on 1709. Kept live so they can be
            // toggled/added/removed exactly like switches.
            var macros = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var macroList = new List<MaterialMacro>();
            BinTreeMap? macroMap = null;
            if (Field(o.Properties, "shaderMacros") is BinTreeMap mm)
            {
                macroMap = mm;
                foreach (var e in mm)
                    if (e.Key is BinTreeString mk)
                    {
                        string mv = e.Value is BinTreeString ms ? ms.Value : e.Value?.ToString() ?? "";
                        macros[mk.Value] = mv;
                        macroList.Add(new MaterialMacro(mk.Value, mv));
                    }
            }

            // Technique/pass render state (M34): the FIRST technique's FIRST pass carries the real shader
            // link + blend state (the class-hash "shader" above is just "StaticMaterialDef").
            string? renderShader = null;
            bool blendEnable = false;
            bool? cullEnable = null;
            int srcBlend = -1, dstBlend = -1;
            BinTreeObjectLink? shaderLink = null;   // M52: kept live so the shader can be CHANGED
            BinTreeStruct? passStruct = null;       // M106: kept live so the render state can be EDITED
            if (Field(o.Properties, "techniques") is BinTreeContainer techs
                && techs.Elements.OfType<BinTreeStruct>().FirstOrDefault() is { } tech0
                && Field(tech0.Properties, "passes") is BinTreeContainer passes
                && passes.Elements.OfType<BinTreeStruct>().FirstOrDefault() is { } pass0)
            {
                passStruct = pass0;
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
                ObjectPathHash = pathHash,
                MaterialObject = isStaticMat ? o : null,
                SamplerContainer = samplers,
                NameFieldHash = nameFieldHash,
                PathFieldHash = pathFieldHash,
                Switches = switches,
                SwitchContainer = switchContainer,
                SwitchEntries = switchList,
                Macros = macros,                 // M150
                MacroMap = macroMap,
                MacroEntries = macroList,
                PassStruct = passStruct,
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

        // (see Reclassify below — the profile is re-derived when the render state is edited)
        return new MaterialDocument(tree, kind, materials) { Issues = issues, SkinMesh = pendingSkinMesh };
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

/// <summary>M222: a champion skin's own material layer, from SkinMeshDataProperties. These sit beside the
/// StaticMaterialDefs and apply to every submesh that does not override them - selfIllumination in
/// particular is declared by ALL 90 skinnedmesh pixel shaders sampled, so leaving it unset renders every
/// champion with its emissive term at zero.
///
/// <para>Every field is nullable on purpose: absent and "authored as zero" are different, and the preview
/// reports which of these it could actually apply.</para></summary>
public sealed class SkinMeshProperties
{
    public string? Skeleton { get; init; }
    public string? SimpleSkin { get; init; }
    public string? Texture { get; init; }
    public string? GlossTexture { get; init; }
    public string? ReflectionMap { get; init; }

    public float? SkinScale { get; init; }
    public float? SelfIllumination { get; init; }
    public float? BrushAlphaOverride { get; init; }
    public float? Fresnel { get; init; }
    public System.Numerics.Vector4? FresnelColor { get; init; }
    public float? ReflectionOpacityDirect { get; init; }
    public float? ReflectionOpacityGlancing { get; init; }
    public float? ReflectionFresnel { get; init; }
    public System.Numerics.Vector4? ReflectionFresnelColor { get; init; }

    /// <summary>Submeshes the skin hides by default — Kalista hides Altar_Spear, which otherwise draws.</summary>
    public IReadOnlyList<string> InitialSubmeshesToHide { get; init; } = Array.Empty<string>();
}

public sealed class MaterialBinding
{
    private readonly List<TextureSlot> _slots;
    private readonly List<MaterialParameter> _params;

    public string Name { get; }
    public string ShaderName { get; }
    public IReadOnlyList<string> Submeshes { get; }
    public bool IsDefault { get; }
    /// <summary>M125: the bin object this binding came from (0 for champion pseudo-bindings) —
    /// links repair issues back to the material they live in.</summary>
    public uint ObjectPathHash { get; init; }
    public IReadOnlyList<TextureSlot> Slots => _slots;
    public IReadOnlyList<MaterialParameter> Parameters => _params;

    // Set for StaticMaterialDef bindings — enables add/remove of sampler slots (M10).
    private BinTreeContainer? _samplerContainer;
    internal BinTreeContainer? SamplerContainer { get => _samplerContainer; init => _samplerContainer = value; }
    internal uint NameFieldHash { get; init; }
    internal uint PathFieldHash { get; init; }
    // M55: the live paramValues container — enables add/remove of parameters.
    private BinTreeContainer? _paramContainer;
    internal BinTreeContainer? ParamContainer { get => _paramContainer; init => _paramContainer = value; }

    /// <summary>Shader feature switches (name → on). Static materials expose the live edited values;
    /// champion pseudo-bindings retain their parse-time snapshot.</summary>
    private IReadOnlyDictionary<string, bool> _switchesInit = EmptySwitches;
    public IReadOnlyDictionary<string, bool> Switches
    {
        get => MaterialObject is null
            ? _switchesInit
            : AllSwitches.ToDictionary(s => s.Name, s => s.On, StringComparer.OrdinalIgnoreCase);
        init => _switchesInit = value;
    }
    private static readonly IReadOnlyDictionary<string, bool> EmptySwitches = new Dictionary<string, bool>();

    /// <summary>M150: shaderMacros (name → "0"/"1") — the preprocessor defines, separate from
    /// <see cref="Switches"/>. Carries NO_BAKED_LIGHTING and DISABLE_DEPTH_FOG.</summary>
    private IReadOnlyDictionary<string, string> _macrosInit = EmptyMacros;
    public IReadOnlyDictionary<string, string> Macros
    {
        get => MaterialObject is null
            ? _macrosInit
            : AllMacros.ToDictionary(m => m.Name, m => m.Value, StringComparer.OrdinalIgnoreCase);
        init => _macrosInit = value;
    }
    private static readonly IReadOnlyDictionary<string, string> EmptyMacros = new Dictionary<string, string>();
    public IReadOnlyList<MaterialMacro> MacroEntries { get; init; } = Array.Empty<MaterialMacro>();
    private BinTreeMap? _macroMap;
    internal BinTreeMap? MacroMap { get => _macroMap; init => _macroMap = value; }
    /// <summary>The owning StaticMaterialDef. Used to create an absent optional shaderMacros map only
    /// when the user adds the first macro, keeping untouched documents byte-exact.</summary>
    internal BinTreeObject? MaterialObject { get; init; }

    /// <summary>M368: this material's own class hash (StaticMaterialDef in practice), for looking the
    /// declared schema up in the meta-class database. 0 for the inline/skin-default bindings, which have no
    /// StaticMaterialDef object behind them.</summary>
    public uint ClassHash => MaterialObject?.ClassHash ?? 0;

    /// <summary>M368: the property hashes this material actually carries. Anything the class declares that
    /// is NOT in here is running on the game's authored default.</summary>
    public IReadOnlyCollection<uint> PresentHashes =>
        MaterialObject is { } o ? o.Properties.Keys.ToList() : Array.Empty<uint>();

    /// <summary>True when a macro is present AND set to a truthy value ("1"/"true"). Reads the LIVE
    /// entries, not the parse-time snapshot, so an edit is reflected immediately (the viewport
    /// re-derives the profile after every material change).</summary>
    public bool MacroOn(string name)
    {
        foreach (var m in AllMacros)
            if (m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return m.On;
        return false;
    }

    /// <summary>Riot's define for "ignore the baked lightmap on this surface".</summary>
    public const string MacroNoBakedLighting = "NO_BAKED_LIGHTING";
    /// <summary>Riot's define for "exclude this surface from distance fog".</summary>
    public const string MacroDisableDepthFog = "DISABLE_DEPTH_FOG";

    /// <summary>Macros live in a string map, so every StaticMaterialDef can gain one even when Riot
    /// omitted the optional shaderMacros field.</summary>
    public bool CanEditMacros => MaterialObject is not null;

    // ---- M103: editable feature switches ----
    /// <summary>The material's switches as live, toggleable entries.</summary>
    public IReadOnlyList<MaterialSwitch> SwitchEntries { get; init; } = Array.Empty<MaterialSwitch>();
    private BinTreeContainer? _switchContainer;
    internal BinTreeContainer? SwitchContainer { get => _switchContainer; init => _switchContainer = value; }

    /// <summary>Every real StaticMaterialDef can receive the standard switches container, including a
    /// material whose current shader authored no switches at all.</summary>
    public bool CanEditSwitches => MaterialObject is not null;

    /// <summary>Enable a shader feature switch this material doesn't carry yet.</summary>
    public MaterialSwitch? AddSwitch(string name)
    {
        if (MaterialObject is null) return null;
        if (_switchContainer is null)
        {
            uint field = HashAlgorithms.Fnv1a("switches");
            _switchContainer = new BinTreeContainer(field, BinPropertyType.Struct, Array.Empty<BinTreeProperty>());
            MaterialObject.Properties[field] = _switchContainer;
        }

        BinTreeStruct clone;
        if (_switchContainer.Elements.OfType<BinTreeStruct>().FirstOrDefault() is { } proto)
            clone = (BinTreeStruct)BinTreeCloner.Clone(proto, 0);
        else
        {
            uint nameField = HashAlgorithms.Fnv1a("name");
            clone = new BinTreeStruct(0, HashAlgorithms.Fnv1a("StaticMaterialSwitchDef"),
                new BinTreeProperty[] { new BinTreeString(nameField, name) });
        }
        uint nameHash = 0;
        foreach (var h in new[] { HashAlgorithms.Fnv1aRaw("name"), HashAlgorithms.Fnv1a("name") })
            if (clone.Properties.ContainsKey(h)) { nameHash = h; break; }
        if (nameHash == 0 || clone.Properties[nameHash] is not BinTreeString ns) return null;
        ns.Value = name;

        _switchContainer.Add(clone);
        var sw = new MaterialSwitch(name, true, clone);
        sw.SetOn(true);
        _switchEdits.Add(sw);
        _structurallyEdited = true;
        return sw;
    }

    public bool RemoveSwitch(MaterialSwitch sw)
    {
        if (_switchContainer is null || !_switchContainer.Remove(sw.Element)) return false;
        _switchEdits.Remove(sw);
        _structurallyEdited = true;
        return true;
    }

    /// <summary>Switches added after parse (SwitchEntries is the parse-time list).</summary>
    private readonly List<MaterialSwitch> _switchEdits = new();
    public IEnumerable<MaterialSwitch> AllSwitches => SwitchEntries.Concat(_switchEdits)
        .Where(s => _switchContainer?.Elements.Contains(s.Element) == true);

    // ---- M150: editable shaderMacros ----
    private readonly List<MaterialMacro> _macroEdits = new();
    private readonly HashSet<string> _macroRemoved = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Every macro currently on the material — parsed plus added, minus removed.</summary>
    public IEnumerable<MaterialMacro> AllMacros =>
        MacroEntries.Concat(_macroEdits).Where(m => !_macroRemoved.Contains(m.Name));

    /// <summary>Set (or add) a shaderMacro, writing straight into the live bin map so Serialize persists it.
    /// Values are Riot's "0"/"1" strings.</summary>
    public MaterialMacro? SetMacro(string name, bool on) => SetMacroValue(name, on ? "1" : "0");

    /// <summary>Set a macro to its exact authored string value. Most are 0/1, but retaining the string is
    /// required when a real Riot material uses a numeric feature value.</summary>
    public MaterialMacro? SetMacroValue(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        uint canonicalField = HashAlgorithms.Fnv1a("shaderMacros");
        uint rawField = HashAlgorithms.Fnv1aRaw("shaderMacros");
        if (_macroMap is null)
        {
            if (MaterialObject is null) return null;
            _macroMap = new BinTreeMap(canonicalField, BinPropertyType.String, BinPropertyType.String,
                Array.Empty<KeyValuePair<BinTreeProperty, BinTreeProperty>>());
            MaterialObject.Properties[canonicalField] = _macroMap;
            _structurallyEdited = true;
        }
        else if (MaterialObject is not null && rawField != canonicalField
                 && MaterialObject.Properties.TryGetValue(rawField, out var rawProperty)
                 && ReferenceEquals(rawProperty, _macroMap))
        {
            // BIN field hashes are lowercase FNV-1a. M348 accidentally authored this optional camel-case
            // field with case-sensitive FNV when a material had no macro map yet. Riot displays the raw
            // hash and can reject the StaticMaterialDef while loading. Preserve its entries but move the
            // map onto the canonical schema field the first time it is edited.
            if (MaterialObject.Properties.TryGetValue(canonicalField, out var canonicalProperty)
                && canonicalProperty is BinTreeMap canonicalMap)
                _macroMap = canonicalMap;
            else
            {
                _macroMap = new BinTreeMap(canonicalField, BinPropertyType.String, BinPropertyType.String,
                    _macroMap.ToArray());
                MaterialObject.Properties[canonicalField] = _macroMap;
            }
            MaterialObject.Properties.Remove(rawField);
            _structurallyEdited = true;
        }
        value = string.IsNullOrWhiteSpace(value) ? "0" : value.Trim();
        _macroRemoved.Remove(name);

        foreach (var e in _macroMap)
            if (e.Key is BinTreeString k && k.Value.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                if (e.Value is BinTreeString vs) vs.Value = value;
                var hit = AllMacros.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                hit?.Apply(value);
                return hit;
            }

        // String-to-string is the field's fixed schema; direct construction also works for an empty map.
        var newKey = new BinTreeString(0, name);
        var newVal = new BinTreeString(0, value);
        _macroMap.Add(newKey, newVal);
        var macro = new MaterialMacro(name, value);
        _macroEdits.Add(macro);
        _structurallyEdited = true;
        return macro;
    }

    /// <summary>Remove a shaderMacro entirely (absent = the shader's default, which differs from "0").</summary>
    public bool RemoveMacro(string name)
    {
        if (_macroMap is null) return false;
        BinTreeProperty? key = null;
        foreach (var e in _macroMap)
            if (e.Key is BinTreeString k && k.Value.Equals(name, StringComparison.OrdinalIgnoreCase)) { key = e.Key; break; }
        if (key is null) return false;
        _macroMap.Remove(key);
        _macroEdits.RemoveAll(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        _macroRemoved.Add(name);
        _structurallyEdited = true;
        return true;
    }

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
    // ---- M106: editable render state (the pass's own fields) ----
    /// <summary>The live first-pass struct. Only <c>blendEnable</c>, <c>cullEnable</c> and the four blend
    /// factors are real fields here — depth-write, alpha-cutout and the blend MODE are derived (see
    /// <see cref="MaterialProfile"/>), so there is nothing in the bin to edit for those.</summary>
    internal BinTreeStruct? PassStruct { get; init; }
    public bool CanEditRenderState => PassStruct is not null;

    /// <summary>Current value of a pass bool, honouring "field absent = <paramref name="whenAbsent"/>".</summary>
    public bool GetPassBool(string field, bool whenAbsent) =>
        PassStruct is null ? whenAbsent
        : FindProp(PassStruct, field) switch
        {
            BinTreeBool b => b.Value,
            BinTreeBitBool bb => bb.Value,
            _ => whenAbsent,
        };

    /// <summary>Set a pass bool, writing the field explicitly when it isn't there yet (the game's
    /// schema default only applies while the field is absent, so we must not rely on it once edited).</summary>
    public bool SetPassBool(string field, bool value)
    {
        if (PassStruct is null) return false;
        uint canonical = HashAlgorithms.Fnv1a(field);
        uint raw = HashAlgorithms.Fnv1aRaw(field);
        PassStruct.Properties.TryGetValue(canonical, out var property);
        if (property is null && raw != canonical)
            PassStruct.Properties.TryGetValue(raw, out property);
        switch (property)
        {
            case BinTreeBool b: b.Value = value; break;
            case BinTreeBitBool bb: bb.Value = value; break;
            default:
                property = new BinTreeBool(canonical, value);
                break;
        }
        if (property.NameHash != canonical)
            property = property switch
            {
                BinTreeBitBool => new BinTreeBitBool(canonical, value),
                _ => new BinTreeBool(canonical, value),
            };
        PassStruct.Properties[canonical] = property;
        if (raw != canonical) PassStruct.Properties.Remove(raw);
        _structurallyEdited = true;
        return true;
    }

    /// <summary>Current value of a pass U32 (blend factors); -1 when the field is absent.</summary>
    public int GetPassU32(string field) =>
        PassStruct is null ? -1
        : FindProp(PassStruct, field) switch
        {
            BinTreeU32 u => (int)u.Value,
            BinTreeU8 b => b.Value,
            _ => -1,
        };

    public bool SetPassU32(string field, uint value)
    {
        if (PassStruct is null) return false;
        uint canonical = HashAlgorithms.Fnv1a(field);
        uint raw = HashAlgorithms.Fnv1aRaw(field);
        PassStruct.Properties.TryGetValue(canonical, out var property);
        if (property is null && raw != canonical)
            PassStruct.Properties.TryGetValue(raw, out property);
        switch (property)
        {
            case BinTreeU32 u: u.Value = value; break;
            case BinTreeU8 b when value <= byte.MaxValue: b.Value = (byte)value; break;
            default:
                property = new BinTreeU32(canonical, value);
                break;
        }
        if (property.NameHash != canonical)
            property = property switch
            {
                BinTreeU8 when value <= byte.MaxValue => new BinTreeU8(canonical, (byte)value),
                _ => new BinTreeU32(canonical, value),
            };
        PassStruct.Properties[canonical] = property;
        if (raw != canonical) PassStruct.Properties.Remove(raw);
        _structurallyEdited = true;
        return true;
    }

    public bool RemovePassProperty(string field)
    {
        if (PassStruct is null) return false;
        foreach (var hash in new[] { HashAlgorithms.Fnv1aRaw(field), HashAlgorithms.Fnv1a(field) })
            if (PassStruct.Properties.Remove(hash))
            {
                if (field.Equals("cullEnable", StringComparison.OrdinalIgnoreCase)) _cullEnableInit = null;
                else if (field.Equals("srcColorBlendFactor", StringComparison.OrdinalIgnoreCase)) _srcBlendInit = -1;
                else if (field.Equals("dstColorBlendFactor", StringComparison.OrdinalIgnoreCase)) _dstBlendInit = -1;
                _structurallyEdited = true;
                return true;
            }
        return false;
    }

    private static BinTreeProperty? FindProp(BinTreeStruct st, string field)
    {
        foreach (var h in new[] { HashAlgorithms.Fnv1aRaw(field), HashAlgorithms.Fnv1a(field) })
            if (st.Properties.TryGetValue(h, out var p)) return p;
        return null;
    }

    /// <summary>First pass's blendEnable — the .bin's own transparency flag (M34). Reads the live pass so
    /// an edit flows through to the preview profile (M106); the init value is the parse-time fallback for
    /// bindings that have no pass struct.</summary>
    private readonly bool _blendEnableInit;
    public bool BlendEnable
    {
        get => PassStruct is null ? _blendEnableInit : GetPassBool("blendEnable", _blendEnableInit);
        init => _blendEnableInit = value;
    }

    /// <summary>First pass's cullEnable — Riot's backface-culling flag: true = single-sided (cull back),
    /// false = double-sided. Null when the field is absent (M34).</summary>
    private bool? _cullEnableInit;
    public bool? CullEnable
    {
        get
        {
            if (PassStruct is null) return _cullEnableInit;
            return FindProp(PassStruct, "cullEnable") switch
            {
                BinTreeBool b => b.Value,
                BinTreeBitBool bb => bb.Value,
                _ => _cullEnableInit,
            };
        }
        init => _cullEnableInit = value;
    }
    /// <summary>Raw src/dst colour blend factors from the first pass (Riot enum; -1 when absent). Observed
    /// SR/HA values: 6 (SrcAlpha) / 7 (OneMinusSrcAlpha) for alpha blending.</summary>
    private int _srcBlendInit = -1;
    private int _dstBlendInit = -1;
    public int SrcBlendFactor
    {
        get { int v = GetPassU32("srcColorBlendFactor"); return v >= 0 ? v : _srcBlendInit; }
        init => _srcBlendInit = value;
    }
    public int DstBlendFactor
    {
        get { int v = GetPassU32("dstColorBlendFactor"); return v >= 0 ? v : _dstBlendInit; }
        init => _dstBlendInit = value;
    }
    /// <summary>Diffuse sampler's addressU/V wrap mode (Riot enum: 1 = Clamp — used by decals; else Wrap). M34.</summary>
    public int DiffuseAddressU { get; init; }
    public int DiffuseAddressV { get; init; }

    /// <summary>The derived RiotApprox preview profile (features + UV transform). Set during parse (M32).</summary>
    public MaterialProfile Profile { get; internal set; } = MaterialProfile.Default;

    /// <summary>True for real StaticMaterialDef bindings (they carry the switches/params that drive the profile).</summary>
    public bool IsStaticMaterialDef => MaterialObject is not null;

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
                           || SwitchEntries.Any(s => s.IsDirty) || AllMacros.Any(m => m.IsDirty);
    private bool _structurallyEdited;

    /// <summary>Every real StaticMaterialDef can receive the standard sampler container, including a
    /// shader whose authored material currently has no texture slots.</summary>
    public bool CanEditSamplers => MaterialObject is not null;

    /// <summary>Add a sampler slot by cloning an existing one when possible, or by authoring Riot's
    /// standard StaticMaterialShaderSamplerDef schema for a sampler-less material.</summary>
    public TextureSlot? AddSampler(string samplerName, string path)
    {
        if (MaterialObject is null) return null;
        if (_samplerContainer is null)
        {
            uint field = HashAlgorithms.Fnv1a("samplerValues");
            _samplerContainer = new BinTreeUnorderedContainer(field, BinPropertyType.Struct,
                Array.Empty<BinTreeProperty>());
            MaterialObject.Properties[field] = _samplerContainer;
        }

        uint nameHash = NameFieldHash != 0 ? NameFieldHash : HashAlgorithms.Fnv1a("TextureName");
        uint pathHash = PathFieldHash != 0 ? PathFieldHash : HashAlgorithms.Fnv1a("texturePath");
        BinTreeStruct clone;
        if (_samplerContainer.Elements.OfType<BinTreeStruct>().FirstOrDefault() is { } proto)
            clone = (BinTreeStruct)BinTreeCloner.Clone(proto, 0);
        else
            clone = new BinTreeStruct(0, 0x0904b150, new BinTreeProperty[]
            {
                new BinTreeString(nameHash, samplerName),
                new BinTreeString(pathHash, path),
            });

        if (clone.Properties.TryGetValue(pathHash, out var p) && p is BinTreeString pathStr)
            pathStr.Value = path;
        else return null;
        if (clone.Properties.TryGetValue(nameHash, out var n) && n is BinTreeString nameStr)
            nameStr.Value = samplerName;

        _samplerContainer.Add(clone);
        var slot = new TextureSlot(samplerName, (BinTreeString)clone.Properties[pathHash], clone);
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
        foreach (var m in AllMacros) m.Revert();
    }

    // ---- M55: parameter add/remove (same clone-the-schema approach as samplers) ----

    /// <summary>Every real StaticMaterialDef can receive the standard parameter container, including a
    /// material whose current shader authored no parameters at all.</summary>
    public bool CanEditParameters => MaterialObject is not null;

    /// <summary>Add a parameter by cloning an existing one (keeps the value TYPE of the prototype — edit the
    /// value afterwards). Null when this material has no parameter to clone from.</summary>
    public MaterialParameter? AddParameter(string name)
    {
        if (MaterialObject is null) return null;
        if (_paramContainer is null)
        {
            uint field = HashAlgorithms.Fnv1a("paramValues");
            _paramContainer = new BinTreeUnorderedContainer(field, BinPropertyType.Struct,
                Array.Empty<BinTreeProperty>());
            MaterialObject.Properties[field] = _paramContainer;
        }

        BinTreeStruct clone;
        if (_paramContainer.Elements.OfType<BinTreeStruct>().FirstOrDefault() is { } proto)
            clone = (BinTreeStruct)BinTreeCloner.Clone(proto, 0);
        else
        {
            uint nameField = HashAlgorithms.Fnv1a("name"), valueField = HashAlgorithms.Fnv1a("value");
            clone = new BinTreeStruct(0, HashAlgorithms.Fnv1a("StaticMaterialShaderParamDef"),
                new BinTreeProperty[]
                {
                    new BinTreeString(nameField, name),
                    new BinTreeVector4(valueField, System.Numerics.Vector4.Zero),
                });
        }
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

        _paramContainer.Add(clone);
        var p = new MaterialParameter(name, clone.Properties[valueHash], clone);
        _params.Add(p);
        _structurallyEdited = true;
        return p;
    }

    /// <summary>Set or add a standard shader vector parameter.</summary>
    public MaterialParameter? SetVectorParameter(string name, System.Numerics.Vector4 value)
    {
        var parameter = _params.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        ?? AddParameter(name);
        if (parameter is null) return null;
        string text = string.Join(", ", new[] { value.X, value.Y, value.Z, value.W }
            .Select(x => x.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        try { parameter.Apply(text); return parameter; }
        catch { return null; }
    }

    public bool RemoveParameter(MaterialParameter p)
    {
        if (_paramContainer is null || p.Element is null) return false;
        if (!_paramContainer.Remove(p.Element)) return false;
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

/// <summary>
/// M150: one shaderMacros entry — a preprocessor define with a "0"/"1" value. Separate from
/// <see cref="MaterialSwitch"/>: macros are map entries, not structs, and carry the flags that decide how
/// a surface reacts to the scene (NO_BAKED_LIGHTING, DISABLE_DEPTH_FOG) rather than how it shades.
/// </summary>
public sealed class MaterialMacro
{
    private readonly string _originalValue;

    public string Name { get; }
    public string Value { get; private set; }
    public bool On => Value == "1" || Value.Equals("true", StringComparison.OrdinalIgnoreCase);
    public bool IsDirty => !string.Equals(Value, _originalValue, StringComparison.Ordinal);

    internal MaterialMacro(string name, string value)
    {
        Name = name;
        Value = value;
        _originalValue = value;
    }

    internal void Apply(string value) => Value = value;
    public void Revert() => Value = _originalValue;
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
