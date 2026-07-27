using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Particles;

/// <summary>
/// M46 Particle Editor: an editable view over a .bin containing VfxSystemDefinitionData objects
/// (champion particle bins, map materials.bin). Wraps a live BinTree (MaterialDocument pattern):
/// property edits mutate the tree in place; <see cref="Serialize"/> re-writes the whole bin
/// (preserving everything not understood) for a project override. Preview definitions are
/// re-extracted from the serialized bytes via <c>VfxSystemResolver</c> after each edit.
/// </summary>
public sealed class ParticleDocument
{
    private readonly BinTree _tree;

    public IReadOnlyList<ParticleSystemEntry> Systems { get; }
    public bool IsDirty => Systems.Any(s => s.IsDirty);

    /// <summary>M125: what the tolerant reader had to repair while reading this bin (empty = well-formed).</summary>
    public IReadOnlyList<BinRepairIssue> Issues { get; }

    private ParticleDocument(BinTree tree, IReadOnlyList<ParticleSystemEntry> systems, IReadOnlyList<BinRepairIssue> issues)
    {
        _tree = tree;
        Systems = systems;
        Issues = issues;
    }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        _tree.Write(ms);
        return ms.ToArray();
    }

    /// <summary>Parse a .bin into its VFX systems; null when it contains none (not a particle bin).</summary>
    /// <param name="resolveName">M187 (3.1): the host's hash dictionary, used to name emitter fields.
    /// Optional so the Formats layer stays standalone; when null (or when a hash is unknown to it) the
    /// built-in <see cref="ParticleEmitterEntry"/> fallback table is used instead.</param>
    public static ParticleDocument? Parse(byte[] data, Func<uint, string?>? resolveName = null)
    {
        BinTree tree;
        IReadOnlyList<BinRepairIssue> issues;
        try { tree = SafeBinTree.Parse(data, out issues); }
        catch { return null; }

        uint sysClass = HashAlgorithms.Fnv1a("VfxSystemDefinitionData");
        uint emitterClass = HashAlgorithms.Fnv1a("VfxEmitterDefinitionData");

        var systems = new List<ParticleSystemEntry>();
        foreach (var o in tree.Objects.Values)
        {
            if (o.ClassHash != sysClass) continue;
            string name = Str(o.Properties, "particleName") ?? Str(o.Properties, "particlePath") ?? $"0x{o.PathHash:x8}";
            string path = Str(o.Properties, "particlePath") ?? "";

            var emitters = new List<ParticleEmitterEntry>();
            foreach (var (_, prop) in o.Properties)
            {
                if (prop is not BinTreeContainer c) continue;
                foreach (var el in c.Elements)
                    if (el is BinTreeStruct s && s.ClassHash == emitterClass)
                        emitters.Add(ParticleEmitterEntry.From(s, resolveName));
            }
            if (emitters.Count > 0)
                systems.Add(new ParticleSystemEntry(o.PathHash, name, path, emitters));
        }
        return systems.Count > 0 ? new ParticleDocument(tree, systems, issues) : null;
    }

    private static string? Str(IReadOnlyDictionary<uint, BinTreeProperty> p, string name) =>
        p.TryGetValue(HashAlgorithms.Fnv1a(name), out var v) && v is BinTreeString s ? s.Value : null;
}

/// <summary>One VfxSystemDefinitionData object (keyed by its path hash, matching the resolver output).</summary>
public sealed record ParticleSystemEntry(uint PathHash, string Name, string ParticlePath, IReadOnlyList<ParticleEmitterEntry> Emitters)
{
    public bool IsDirty => Emitters.Any(e => e.IsDirty);
}

/// <summary>One emitter: its editable primitive properties, grouped into Particle-Town-style modules.</summary>
public sealed class ParticleEmitterEntry
{
    public string Name { get; private init; } = "(emitter)";
    public IReadOnlyList<ParticleProperty> Properties { get; private init; } = Array.Empty<ParticleProperty>();
    internal BinTreeStruct EmitterStruct { get; init; } = null!;
    private BinTreeStruct _struct => EmitterStruct;
    private bool _disabledEdited;
    public bool IsDirty => _disabledEdited || Properties.Any(p => p.IsDirty);

    private static readonly uint DisabledHash = HashAlgorithms.Fnv1a("disabled");

    /// <summary>Live 'disabled' flag of this VfxEmitterDefinitionData (absent = enabled).</summary>
    public bool Disabled => _struct.Properties.TryGetValue(DisabledHash, out var p) && p switch
    {
        BinTreeBool b => b.Value,
        BinTreeBitBool bb => bb.Value,
        _ => false,
    };

    /// <summary>Enable/disable the emitter by editing (or adding) its 'disabled' bool on the live tree —
    /// persists through Serialize/Save Override, exactly like toggling it in the real data.</summary>
    public void SetDisabled(bool disabled)
    {
        if (Disabled == disabled) return;
        if (_struct.Properties.TryGetValue(DisabledHash, out var p))
        {
            switch (p)
            {
                case BinTreeBool b: b.Value = disabled; break;
                case BinTreeBitBool bb: bb.Value = disabled; break;
                default: return; // unexpected type: leave untouched
            }
        }
        else _struct.Properties[DisabledHash] = new BinTreeBool(DisabledHash, disabled);
        _disabledEdited = true;
    }

    /// <summary>Distinct module names, in the canonical Particle Town order.</summary>
    public IReadOnlyList<string> Modules =>
        ModuleOrder.Where(m => Properties.Any(p => p.Module == m)).ToList();

    private static readonly string[] ModuleOrder =
        { "Emission", "Birth", "Position", "Velocity", "Scale", "Color", "Render", "Texture", "Other" };

    internal static ParticleEmitterEntry From(BinTreeStruct emitter, Func<uint, string?>? resolveName = null)
    {
        var props = new List<ParticleProperty>();
        string name = "(emitter)";
        foreach (var (hash, prop) in emitter.Properties)
        {
            // M187 (3.1): the host's hash dictionary first, the built-in table only as an offline fallback.
            // Measured over 25,008,999 emitter field occurrences: the table alone names 85.5% of them,
            // the dictionary names 99.8%. Exactly two hashes are unknown to both - 0xcb13aff1 (51,189,
            // F32) and 0xd1ee8634 (583, BitBool) - and those still show as hex.
            string fieldName = resolveName?.Invoke(hash) ?? (FieldNames.TryGetValue(hash, out var fn) ? fn : $"0x{hash:x8}");
            if (hash == EmitterNameHash && prop is BinTreeString ns) name = ns.Value;

            string module = ModuleOf(fieldName);
            switch (prop)
            {
                // Value* structs: constantValue is the editable scalar; dynamics = the curve keys.
                case BinTreeStruct vs when Field(vs.Properties, "constantValue") is { } cv:
                    var (times, channels) = ReadDynamics(vs.Properties);
                    props.Add(new ParticleProperty(module, fieldName, cv, isConstantOfCurve: true,
                        curveTimes: times, curveChannels: channels));
                    break;
                // M187: a Value* struct with a curve but NO constantValue. Riot's writer omits default-valued
                // properties, so this is common rather than exotic - ValueColor ships constantValue in only
                // 63.0% of its instances and IntegratedValueFloat in 34.1%. These rows previously fell through
                // to `default:` and became one opaque "Embedded" row, which hid the CURVE as well as the
                // constant. The curve is now shown. The constant stays unwritten and unguessed: what value
                // Riot's reader substitutes for an absent constantValue is not established here.
                case BinTreeStruct ds when Field(ds.Properties, "dynamics") is BinTreeStruct:
                    var (dt, dc) = ReadDynamics(ds.Properties);
                    props.Add(new ParticleProperty(module, fieldName, ds, readOnly: true,
                        curveTimes: dt, curveChannels: dc, displayText: "(curve only - no constantValue)",
                        readOnlyReason: "This field ships only a curve - Riot's writer omitted its constantValue "
                                      + "because it was the default. The curve is shown; the constant has no row to edit."));
                    break;
                // M187 (3.2): Optional<T> holds the value one level down. 1,701,250 emitter occurrences are
                // Optional<F32> - lifetime, particleLinger, emitterLinger, period, timeActiveDuringPeriod,
                // MaximumRateByVelocity - and every one of them was read-only. Editing the inner property
                // edits the live tree in place, exactly as a bare field does.
                case BinTreeOptional { Value: { } inner } when BinValueEditor.KindOf(inner) != BinValueKind.ReadOnly:
                    props.Add(new ParticleProperty(module, fieldName, inner, typeNote: " (optional)"));
                    break;
                // An EMPTY Optional has no value to edit; writing one would mean changing the field's
                // presence, not its value, which the row model cannot express. Shown, not editable.
                case BinTreeOptional:
                    props.Add(new ParticleProperty(module, fieldName, prop, readOnly: true,
                        readOnlyReason: "This optional field is present but empty. Giving it a value would change "
                                      + "whether the field exists, not what it holds, which this row cannot express."));
                    break;
                // plain primitives (numbers, bools, strings/paths, vectors, colours) - directly editable.
                // The signed/wide integer types were missing: `pass` alone is 976,544 I16 occurrences.
                case BinTreeF32 or BinTreeU8 or BinTreeU16 or BinTreeU32 or BinTreeU64 or BinTreeI8
                    or BinTreeI16 or BinTreeI32 or BinTreeI64 or BinTreeBool or BinTreeBitBool
                    or BinTreeString or BinTreeHash or BinTreeVector2 or BinTreeVector3 or BinTreeVector4
                    or BinTreeColor:
                    props.Add(new ParticleProperty(module, fieldName, prop));
                    break;
                default:
                    props.Add(new ParticleProperty(module, fieldName, prop, readOnly: true)); // unsupported: show, don't crash
                    break;
            }
        }
        // stable, Particle-Town-ish order: module, then name
        props.Sort((a, b) =>
        {
            int m = Array.IndexOf(ModuleOrder, a.Module).CompareTo(Array.IndexOf(ModuleOrder, b.Module));
            return m != 0 ? m : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return new ParticleEmitterEntry { Name = name, Properties = props, EmitterStruct = emitter };
    }

    private static BinTreeProperty? Field(IReadOnlyDictionary<uint, BinTreeProperty> p, string name) =>
        p.TryGetValue(HashAlgorithms.Fnv1a(name), out var v) ? v : null;

    /// <summary>dynamics{times[],values[]} → parallel arrays; channels = per-component float rows (up to 4).</summary>
    private static (float[]? Times, float[][]? Channels) ReadDynamics(IReadOnlyDictionary<uint, BinTreeProperty> valueProps)
    {
        if (Field(valueProps, "dynamics") is not BinTreeStruct dyn) return (null, null);
        if (Field(dyn.Properties, "times") is not BinTreeContainer tc) return (null, null);
        if (Field(dyn.Properties, "values") is not BinTreeContainer vc) return (null, null);
        int n = Math.Min(tc.Elements.Count, vc.Elements.Count);
        if (n == 0) return (null, null);

        var times = new float[n];
        for (int i = 0; i < n; i++) times[i] = tc.Elements[i] is BinTreeF32 f ? f.Value : 0f;

        static float[] Comp(BinTreeProperty p) => p switch
        {
            BinTreeF32 f => new[] { f.Value },
            BinTreeVector2 v => new[] { v.Value.X, v.Value.Y },
            BinTreeVector3 v => new[] { v.Value.X, v.Value.Y, v.Value.Z },
            BinTreeVector4 v => new[] { v.Value.X, v.Value.Y, v.Value.Z, v.Value.W },
            BinTreeColor c => new[] { c.Value.R, c.Value.G, c.Value.B, c.Value.A },
            _ => new[] { 0f },
        };
        int comps = Comp(vc.Elements[0]).Length;
        var channels = new float[comps][];
        for (int c = 0; c < comps; c++) channels[c] = new float[n];
        for (int i = 0; i < n; i++)
        {
            var v = Comp(vc.Elements[i]);
            for (int c = 0; c < comps; c++) channels[c][i] = c < v.Length ? v[c] : 0f;
        }
        return (times, channels);
    }

    /// <summary>Which module card a field belongs on. Case-INSENSITIVE: the bin name hash is computed over
    /// the lowercased string, so Riot's own spelling varies (<c>Color</c>, <c>EmitterPosition</c>,
    /// <c>SpawnShape</c>, <c>TextureFlipV</c>) and an ordinal match would drop those into "Other".</summary>
    private static string ModuleOf(string field) =>
        ModuleByField.TryGetValue(field, out var m) ? m : "Other";

    private static readonly Dictionary<string, string> ModuleByField = BuildModules();
    private static Dictionary<string, string> BuildModules()
    {
        // M187 (3.1): every emitter field measured across the corpus, so the ~90 that the hash dictionary
        // newly names land on a real card instead of piling up in "Other".
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string module, params string[] fields) { foreach (var f in fields) d[f] = module; }

        Add("Emission",
            "rate", "rateByVelocityFunction", "MaximumRateByVelocity", "flexRate",
            "particleLifetime", "flexParticleLifetime", "lifetime", "doesLifetimeScale",
            "offsetLifetimeScaling", "offsetLifeScalingSymmetryMode",
            "particleLinger", "emitterLinger", "particleLingerType", "Linger",
            "timeBeforeFirstEmission", "HasVariableStartTime", "period", "timeActiveDuringPeriod",
            "isSingleParticle", "disabled", "importance", "ChanceToNotExist", "ParticlesShareRandomValue",
            "childParticleSetDefinition",
            "emissionSurfaceDefinition", "emissionMeshName", "emissionMeshScale", "useEmissionMeshNormalForBirth");

        Add("Birth",
            "birthScale0", "birthColor", "birthVelocity", "birthDrag", "birthAcceleration",
            "birthOrbitalVelocity", "birthRotation0", "birthRotationalVelocity0", "birthRotationalAcceleration",
            "birthUvScrollRate", "birthFrameRate", "birthUvRotation0", "birthUvRotateRate", "birthUVOffset",
            "flexBirthVelocity", "flexBirthRotationalVelocity0", "flexBirthUVOffset", "flexBirthUVScrollRate");

        Add("Position",
            "EmitterPosition", "isLocalOrientation", "particleIsLocalOrientation", "IsEmitterSpace",
            "bindWeight", "emitOffset", "shape", "SpawnShape",
            "translationOverride", "rotationOverride", "scaleOverride",
            "rotation0", "isRotationEnabled", "hasPostRotateOrientation", "postRotateOrientationAxis",
            "isFollowingTerrain", "useNavmeshMask", "isGroundLayer");

        Add("Velocity",
            "velocity", "worldAcceleration", "acceleration", "drag",
            "directionVelocityScale", "directionVelocityMinScale", "fieldCollectionDefinition");

        Add("Scale",
            "scale0", "isUniformScale", "scaleBirthScaleByBoundObjectSize", "scaleEmitOffsetByBoundObjectSize",
            "FlexShapeDefinition", "FlexInstanceScale", "flexScaleBirthScale");

        Add("Color",
            "Color", "lingerColor", "colorLookUpTypeX", "colorLookUpTypeY", "colorLookUpScales",
            "colorLookUpOffsets", "colorRenderFlags", "paletteDefinition", "modulationFactor",
            "censorModifiers", "censorModulateValue", "colorblindVisibility");

        Add("Render",
            "blendMode", "pass", "miscRenderFlags", "alphaRef", "isDirectionOriented", "primitive",
            "isRandomStartFrame", "depthBiasFactors", "renderPhaseOverride", "meshRenderFlags",
            "stencilMode", "stencilRef", "StencilReferenceId", "disableBackfaceCull", "WriteAlphaOnly",
            "softParticleParams", "reflectionDefinition", "distortionDefinition", "alphaErosionDefinition",
            "doesCastShadow", "sliceTechniqueRange", "SortEmittersByPos",
            "CustomMaterial", "materialOverrideDefinitions", "Filtering", "LegacySimple");

        Add("Texture",
            "texture", "texDiv", "numFrames", "frameRate", "startFrame", "textureMult",
            "uvScroll", "uvScale", "uvScale0", "uvRotation", "uvRotation0", "uvMode", "uvScrollClamp",
            "uvTransformCenter", "uvParallaxScale", "emitterUvScrollRate",
            "particleUVScrollRate", "particleUVRotateRate", "texAddressModeBase",
            "TextureFlipU", "TextureFlipV", "isTexturePixelated",
            "particleColorTexture", "falloffTexture");

        return d;
    }

    private static readonly uint EmitterNameHash = HashAlgorithms.Fnv1a("emitterName");

    /// <summary>Offline fallback for emitter field names, used when the host has no hash dictionary loaded.
    /// Trimmed in M187 to the 46 entries whose hashes actually occur on an emitter: the other 11 - among
    /// them <c>emitOffset</c>, <c>shape</c>, <c>uvScroll</c>, <c>uvScale0</c> and <c>lingerColor</c> - were
    /// guesses that never matched anything in the corpus. Spelling follows CDTB, since the hash is
    /// case-insensitive but the displayed name should agree with the dictionary.</summary>
    private static readonly Dictionary<uint, string> FieldNames = BuildFieldNames();
    private static Dictionary<uint, string> BuildFieldNames()
    {
        string[] names =
        {
            "emitterName","rate","particleLifetime","lifetime","particleLinger","timeBeforeFirstEmission",
            "isSingleParticle","disabled","importance","blendMode","pass","miscRenderFlags","alphaRef",
            "isDirectionOriented","primitive","birthScale0","scale0","isUniformScale","birthColor","Color",
            "birthVelocity","velocity","worldAcceleration","acceleration","drag","birthDrag",
            "birthRotation0","birthRotationalVelocity0","birthUvScrollRate","birthFrameRate",
            "EmitterPosition","isLocalOrientation","bindWeight","texture","texDiv",
            "numFrames","frameRate","textureMult","isRandomStartFrame",
            "particleColorTexture","falloffTexture","colorLookUpTypeX","colorLookUpTypeY",
            "depthBiasFactors","renderPhaseOverride","doesLifetimeScale",
        };
        var d = new Dictionary<uint, string>(names.Length);
        foreach (var n in names) d[HashAlgorithms.Fnv1a(n)] = n;
        return d;
    }
}

/// <summary>One editable (or read-only) emitter property row, live over the BinTree.</summary>
public sealed class ParticleProperty
{
    private readonly BinTreeProperty _prop;
    private readonly string _originalText;

    public string Module { get; }
    public string Name { get; }
    public string TypeName { get; }
    /// <summary>True when this row edits the constantValue of a Value*/curve struct.</summary>
    public bool IsCurveConstant { get; }
    /// <summary>Curve keys of the owning Value* struct (null when constant-only). Times normalised 0..1.</summary>
    public float[]? CurveTimes { get; }
    public float[][]? CurveChannels { get; }
    public bool HasCurve => CurveTimes is { Length: > 0 };
    public bool IsReadOnly { get; }
    /// <summary>Why this row cannot be edited, shown in the inspector. Read-only rows have several distinct
    /// causes and one blanket message misattributes most of them.</summary>
    public string ReadOnlyReason { get; }

    /// <summary>Shown instead of the formatted value when the row has no editable value of its own
    /// (a Value* struct whose constantValue Riot omitted). Constant, so such a row is never dirty.</summary>
    private readonly string? _display;

    public ParticleProperty(string module, string name, BinTreeProperty prop, bool isConstantOfCurve = false,
        float[]? curveTimes = null, float[][]? curveChannels = null, bool readOnly = false,
        string typeNote = "", string? displayText = null,
        string readOnlyReason = "Read-only property (unsupported type or Riot reference).")
    {
        _display = displayText;
        ReadOnlyReason = readOnlyReason;
        Module = module;
        Name = name;
        _prop = prop;
        IsCurveConstant = isConstantOfCurve;
        CurveTimes = curveTimes;
        CurveChannels = curveChannels;
        IsReadOnly = readOnly || BinValueEditor.KindOf(prop) == BinValueKind.ReadOnly;
        TypeName = prop.Type + typeNote + (HasCurve ? $" + curve({CurveTimes!.Length} keys)" : "");
        _originalText = CurrentText;   // must match CurrentText's source, or a _display row reads as dirty forever
    }

    public string CurrentText => _display ?? SafeFormat();
    public bool IsDirty => !string.Equals(CurrentText, _originalText, StringComparison.Ordinal);

    /// <summary>Apply text to the live property (throws on invalid input — caller keeps the old value).</summary>
    public void Apply(string text)
    {
        if (IsReadOnly) throw new InvalidOperationException("This property is not editable.");
        BinValueEditor.Apply(_prop, text);
    }

    private string SafeFormat()
    {
        try { return BinValueEditor.Format(_prop, _ => null); }
        catch { return $"({_prop.Type})"; }
    }
}
