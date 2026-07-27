using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Vfx;

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
            // M188 (3.4): emitterless systems are kept. 5.8% of systems have no emitter and 55.4% of those
            // carry nothing but particleName and particlePath - they are named stubs. Dropping them made the
            // editor look like it had failed to parse an effect the user could see referenced by name, and
            // it refused to open a bin whose systems were ALL emitterless (70 of 2,674 in the sample).
            systems.Add(new ParticleSystemEntry(o.PathHash, name, path, emitters,
                ParticleSystemEntry.ReadProperties(o, resolveName)));
        }
        return systems.Count > 0 ? new ParticleDocument(tree, systems, issues) : null;
    }

    private static string? Str(IReadOnlyDictionary<uint, BinTreeProperty> p, string name) =>
        p.TryGetValue(HashAlgorithms.Fnv1a(name), out var v) && v is BinTreeString s ? s.Value : null;
}

/// <summary>One VfxSystemDefinitionData object (keyed by its path hash, matching the resolver output).</summary>
public sealed record ParticleSystemEntry(uint PathHash, string Name, string ParticlePath,
    IReadOnlyList<ParticleEmitterEntry> Emitters, IReadOnlyList<ParticleProperty> Properties)
{
    public bool IsDirty => Emitters.Any(e => e.IsDirty) || Properties.Any(p => p.IsDirty);

    /// <summary>Distinct module names carried by this system's own properties, in panel order.</summary>
    public IReadOnlyList<string> Modules =>
        ModuleOrder.Where(m => Properties.Any(p => p.Module == m)).ToList();

    private static readonly string[] ModuleOrder = { "System", "Transform", "Audio", "Other" };

    /// <summary>M188 (3.5): the system's OWN fields, which had no editor surface at all - the tree showed a
    /// system only as a name, and everything on it (flags, transform, visibilityRadius, the default sounds)
    /// was invisible and unreachable. The emitter containers are skipped: those are the emitters, which the
    /// tree already shows.</summary>
    internal static IReadOnlyList<ParticleProperty> ReadProperties(BinTreeObject o, Func<uint, string?>? resolveName)
    {
        var props = new List<ParticleProperty>();
        foreach (var (hash, prop) in o.Properties)
        {
            if (hash == ComplexEmittersHash || hash == SimpleEmittersHash) continue;
            string fieldName = resolveName?.Invoke(hash) ?? (SystemFieldNames.TryGetValue(hash, out var fn) ? fn : $"0x{hash:x8}");
            ParticleEmitterEntry.AddSystemRows(props, ModuleOf(fieldName), fieldName, prop, resolveName,
                VfxPreviewCoverage.IgnoredNote(hash));
        }
        ParticleEmitterEntry.SortRows(props, ModuleOrder);
        return props;
    }

    private static readonly uint ComplexEmittersHash = HashAlgorithms.Fnv1a("complexEmitterDefinitionData");
    private static readonly uint SimpleEmittersHash = HashAlgorithms.Fnv1a("simpleEmitterDefinitionData");

    private static string ModuleOf(string field) => field switch
    {
        "particleName" or "particlePath" or "flags" or "drawingLayer" or "mEyeCandy" or "mIsPoseAfterimage"
            or "selfIllumination" or "colorblindVisibility" or "assetRemappingTable"
            or "materialOverrideDefinitions" => "System",
        "transform" or "overrideScaleCap" or "scaleDynamicallyWithAttachedBone" or "visibilityRadius"
            or "buildUpTime" or "hudAnchorPositionFromWorldProjection" or "hudLayerDimension" => "Transform",
        "soundOnCreateDefault" or "soundPersistentDefault" or "voiceOverOnCreateDefault"
            or "voiceOverPersistentDefault" or "audioParameterFlexID" or "audioParameterTimeScaledDuration"
            or "ClockToUse" => "Audio",
        _ => "Other",
    };

    /// <summary>Offline fallback, same role as the emitter table: every system field measured in the corpus.</summary>
    private static readonly Dictionary<uint, string> SystemFieldNames = BuildSystemFieldNames();
    private static Dictionary<uint, string> BuildSystemFieldNames()
    {
        string[] names =
        {
            "particleName","particlePath","flags","visibilityRadius","soundOnCreateDefault",
            "soundPersistentDefault","overrideScaleCap","transform","buildUpTime",
            "scaleDynamicallyWithAttachedBone","assetRemappingTable","voiceOverOnCreateDefault",
            "materialOverrideDefinitions","mIsPoseAfterimage","mEyeCandy","voiceOverPersistentDefault",
            "hudAnchorPositionFromWorldProjection","drawingLayer","audioParameterFlexID",
            "audioParameterTimeScaledDuration","hudLayerDimension","ClockToUse","selfIllumination",
        };
        var d = new Dictionary<uint, string>(names.Length);
        foreach (var n in names) d[HashAlgorithms.Fnv1a(n)] = n;
        return d;
    }
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

            // M191 (3.7): resolved once per top-level field and inherited by its sub-rows - a struct the
            // preview never reads makes every field inside it equally invisible in the viewport.
            AddRows(props, ModuleOf(fieldName), fieldName, prop, 0, resolveName,
                VfxPreviewCoverage.IgnoredNote(hash));
        }
        SortRows(props, ModuleOrder);
        return new ParticleEmitterEntry { Name = name, Properties = props, EmitterStruct = emitter };
    }

    /// <summary>Order by module, then by top-level field name - but keep each expanded struct's sub-rows
    /// immediately under their parent, in the order M189 emitted them. Sorting rows individually would
    /// scatter a struct's contents across the card.</summary>
    internal static void SortRows(List<ParticleProperty> props, string[] moduleOrder)
    {
        var groups = new List<List<ParticleProperty>>();
        foreach (var p in props)
        {
            if (p.Depth == 0 || groups.Count == 0) groups.Add(new List<ParticleProperty> { p });
            else groups[^1].Add(p);
        }
        groups.Sort((a, b) =>
        {
            int m = Array.IndexOf(moduleOrder, a[0].Module).CompareTo(Array.IndexOf(moduleOrder, b[0].Module));
            return m != 0 ? m : string.Compare(a[0].Name, b[0].Name, StringComparison.OrdinalIgnoreCase);
        });
        props.Clear();
        foreach (var g in groups) props.AddRange(g);
    }

    /// <summary>M189 (3.3): expand a nested definition struct into sub-rows instead of showing it as one
    /// opaque read-only row. Every Vfx*DefinitionData was previously a dead end in the editor - erosion,
    /// soft particles, palette, reflection, trail, beam, mesh, Linger, the field collection, the child set -
    /// and most of those the renderer now actually implements, so the user could see a stage in the preview
    /// with no way to touch what drives it.
    ///
    /// The rows are FLATTENED with a depth rather than made into a real tree: the module card already
    /// renders a flat list, and depth-indenting it keeps selection, the inspector, dirty tracking and the
    /// curve panel working unchanged. A struct's own row stays as a read-only header.</summary>
    /// <summary>M189: the same expansion for the system panel - assetRemappingTable and
    /// materialOverrideDefinitions were read-only containers there for exactly the same reason.</summary>
    internal static void AddSystemRows(List<ParticleProperty> into, string module, string name,
        BinTreeProperty prop, Func<uint, string?>? resolveName, string? previewNote)
        => AddRows(into, module, name, prop, 0, resolveName, previewNote);

    private static void AddRows(List<ParticleProperty> into, string module, string name, BinTreeProperty prop,
        int depth, Func<uint, string?>? resolveName, string? previewNote)
    {
        // A Value*/Integrated* struct is a LEAF, not a container: MakeRow turns it into one editable
        // constant plus its curve. Expanding it would replace that with constantValue/dynamics/times/values
        // rows and lose the curve display entirely.
        bool isValueStruct = prop is BinTreeStruct vs
            && (Field(vs.Properties, "constantValue") is not null || Field(vs.Properties, "dynamics") is BinTreeStruct);

        if (depth < MaxRowDepth && !isValueStruct)
        {
            switch (prop)
            {
                case BinTreeStruct s when s.Properties.Count > 0:
                    into.Add(new ParticleProperty(module, name, prop, readOnly: true, depth: depth,
                        displayText: $"({s.Properties.Count} field(s))", previewNote: previewNote,
                        readOnlyReason: "A struct header. Its fields are the rows indented beneath it."));
                    foreach (var (h, child) in s.Properties)
                        AddRows(into, module, resolveName?.Invoke(h) ?? $"0x{h:x8}", child, depth + 1, resolveName, previewNote);
                    return;

                case BinTreeContainer c when c.Elements.Count > 0:
                    into.Add(new ParticleProperty(module, name, prop, readOnly: true, depth: depth,
                        displayText: $"({c.Elements.Count} item(s))", previewNote: previewNote,
                        readOnlyReason: "A list header. Its items are the rows indented beneath it. Adding and "
                                      + "removing items is not supported."));
                    for (int i = 0; i < c.Elements.Count; i++)
                        AddRows(into, module, $"[{i}]", c.Elements[i], depth + 1, resolveName, previewNote);
                    return;

                // An Optional holding a struct: unwrap it and expand, rather than stopping at the wrapper.
                case BinTreeOptional { Value: BinTreeStruct } o:
                    AddRows(into, module, name, o.Value!, depth, resolveName, previewNote);
                    return;
            }
        }

        var row = MakeRow(module, name, prop, depth, previewNote);
        // Depth-limited rather than silently truncated: say so on the row that stopped.
        if (depth >= MaxRowDepth && prop is BinTreeStruct or BinTreeContainer)
            row = new ParticleProperty(module, name, prop, readOnly: true, depth: depth, previewNote: previewNote,
                displayText: "(nested too deep to expand)",
                readOnlyReason: $"The editor expands nested structs to {MaxRowDepth} levels. This one sits deeper.");
        into.Add(row);
    }

    /// <summary>How many levels of nested struct the editor expands. Chosen for the editor, not read from
    /// Riot: it bounds the row count on pathological data rather than encoding anything about the format.</summary>
    private const int MaxRowDepth = 4;

    /// <summary>Turn one live bin property into an editor row. Shared by emitters and, since M188, by the
    /// system-level panel - the type handling is a property of the .bin format, not of who owns the field.</summary>
    internal static ParticleProperty MakeRow(string module, string fieldName, BinTreeProperty prop,
        int depth = 0, string? previewNote = null)
    {
        {
            switch (prop)
            {
                // Value* structs: constantValue is the editable scalar; dynamics = the curve keys.
                case BinTreeStruct vs when Field(vs.Properties, "constantValue") is { } cv:
                    var (times, channels, dyn) = ReadDynamics(vs.Properties);
                    return new ParticleProperty(module, fieldName, cv, isConstantOfCurve: true,
                        curveTimes: times, curveChannels: channels, depth: depth, curveDynamics: dyn,
                        previewNote: previewNote);
                // M187: a Value* struct with a curve but NO constantValue. Riot's writer omits default-valued
                // properties, so this is common rather than exotic - ValueColor ships constantValue in only
                // 63.0% of its instances and IntegratedValueFloat in 34.1%. These rows previously fell through
                // to `default:` and became one opaque "Embedded" row, which hid the CURVE as well as the
                // constant. The curve is now shown. The constant stays unwritten and unguessed: what value
                // Riot's reader substitutes for an absent constantValue is not established here.
                case BinTreeStruct ds when Field(ds.Properties, "dynamics") is BinTreeStruct:
                    var (dt, dc, ddyn) = ReadDynamics(ds.Properties);
                    return new ParticleProperty(module, fieldName, ds, readOnly: true, depth: depth,
                        curveTimes: dt, curveChannels: dc, curveDynamics: ddyn, previewNote: previewNote,
                        displayText: "(curve only - no constantValue)",
                        readOnlyReason: "This field ships only a curve - Riot's writer omitted its constantValue "
                                      + "because it was the default. The curve is shown; the constant has no row to edit.");
                // M187 (3.2): Optional<T> holds the value one level down. 1,701,250 emitter occurrences are
                // Optional<F32> - lifetime, particleLinger, emitterLinger, period, timeActiveDuringPeriod,
                // MaximumRateByVelocity - and every one of them was read-only. Editing the inner property
                // edits the live tree in place, exactly as a bare field does.
                case BinTreeOptional { Value: { } inner } when BinValueEditor.KindOf(inner) != BinValueKind.ReadOnly:
                    return new ParticleProperty(module, fieldName, inner, typeNote: " (optional)", depth: depth,
                        previewNote: previewNote);
                // An EMPTY Optional has no value to edit; writing one would mean changing the field's
                // presence, not its value, which the row model cannot express. Shown, not editable.
                case BinTreeOptional:
                    return new ParticleProperty(module, fieldName, prop, readOnly: true, depth: depth,
                        previewNote: previewNote,
                        readOnlyReason: "This optional field is present but empty. Giving it a value would change "
                                      + "whether the field exists, not what it holds, which this row cannot express.");
                // plain primitives (numbers, bools, strings/paths, vectors, colours) - directly editable.
                // The signed/wide integer types were missing: `pass` alone is 976,544 I16 occurrences.
                case BinTreeF32 or BinTreeU8 or BinTreeU16 or BinTreeU32 or BinTreeU64 or BinTreeI8
                    or BinTreeI16 or BinTreeI32 or BinTreeI64 or BinTreeBool or BinTreeBitBool
                    or BinTreeString or BinTreeHash or BinTreeVector2 or BinTreeVector3 or BinTreeVector4
                    or BinTreeColor:
                    return new ParticleProperty(module, fieldName, prop, depth: depth, previewNote: previewNote);
                default:
                    return new ParticleProperty(module, fieldName, prop, readOnly: true, depth: depth,
                        previewNote: previewNote); // unsupported: show, don't crash
            }
        }
    }

    private static BinTreeProperty? Field(IReadOnlyDictionary<uint, BinTreeProperty> p, string name) =>
        p.TryGetValue(HashAlgorithms.Fnv1a(name), out var v) ? v : null;

    /// <summary>dynamics{times[],values[]} → parallel arrays plus the LIVE containers, so M190 can write
    /// keys back into the tree rather than editing a copy.</summary>
    private static (float[]? Times, float[][]? Channels, BinTreeStruct? Dynamics)
        ReadDynamics(IReadOnlyDictionary<uint, BinTreeProperty> valueProps)
    {
        if (Field(valueProps, "dynamics") is not BinTreeStruct dyn) return (null, null, null);
        if (Field(dyn.Properties, "times") is not BinTreeContainer tc) return (null, null, null);
        if (Field(dyn.Properties, "values") is not BinTreeContainer vc) return (null, null, null);
        var (times, channels) = ParticleProperty.ReadCurve(tc, vc);
        return times is null ? (null, null, null) : (times, channels, dyn);
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
    public float[]? CurveTimes { get; private set; }
    public float[][]? CurveChannels { get; private set; }
    public bool HasCurve => CurveTimes is { Length: > 0 };
    public bool IsReadOnly { get; }
    /// <summary>M189 (3.3): how deep inside a nested struct this row sits. 0 = a field of the emitter or
    /// system itself; the card indents by this.</summary>
    public int Depth { get; }
    /// <summary>M191 (3.7): set when the preview will not show this field's effect - either the resolver
    /// never reads it, or it is read and the renderer does nothing with it. Null means the edit is visible
    /// in the viewport. The edit is always written to the .bin either way.</summary>
    public string? PreviewNote { get; }
    public bool IgnoredByPreview => PreviewNote is not null;
    /// <summary>Why this row cannot be edited, shown in the inspector. Read-only rows have several distinct
    /// causes and one blanket message misattributes most of them.</summary>
    public string ReadOnlyReason { get; }

    /// <summary>Shown instead of the formatted value when the row has no editable value of its own
    /// (a Value* struct whose constantValue Riot omitted). Constant, so such a row is never dirty.</summary>
    private readonly string? _display;

    public ParticleProperty(string module, string name, BinTreeProperty prop, bool isConstantOfCurve = false,
        float[]? curveTimes = null, float[][]? curveChannels = null, bool readOnly = false,
        string typeNote = "", string? displayText = null,
        string readOnlyReason = "Read-only property (unsupported type or Riot reference).", int depth = 0,
        BinTreeStruct? curveDynamics = null, string? previewNote = null)
    {
        Depth = depth;
        PreviewNote = previewNote;
        _dynamics = curveDynamics;
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
    public bool IsDirty => _curveEdited || !string.Equals(CurrentText, _originalText, StringComparison.Ordinal);

    // ---- M190 (3.6): curve key editing ---------------------------------------------------------------
    // The curve display was a copy of the keys, so nothing the user did to it could reach the bin. These
    // are the LIVE dynamics containers; every mutation below edits the tree in place, exactly as the
    // scalar rows do, and Serialize then writes the edited keys out.

    // BinTreeContainer.Elements is read-only through the public API, so a key edit rebuilds both
    // containers and puts them back on the dynamics struct, whose property dictionary IS mutable. That
    // keeps the whole path on supported API instead of casting the library's backing list.
    private readonly BinTreeStruct? _dynamics;
    private bool _curveEdited;

    private static readonly uint TimesHash = HashAlgorithms.Fnv1a("times");
    private static readonly uint ValuesHash = HashAlgorithms.Fnv1a("values");

    private BinTreeContainer? Times => _dynamics?.Properties.GetValueOrDefault(TimesHash) as BinTreeContainer;
    private BinTreeContainer? Values => _dynamics?.Properties.GetValueOrDefault(ValuesHash) as BinTreeContainer;

    /// <summary>True when this row's curve keys can be edited.</summary>
    public bool CanEditCurve => Times is not null && Values is not null && HasCurve;

    /// <summary>How many float components each key holds (1 = float curve, 3 = vector, 4 = colour).</summary>
    public int CurveComponents => CurveChannels?.Length ?? 0;

    /// <summary>Overwrite one key's time and value.</summary>
    public void SetCurveKey(int index, float time, IReadOnlyList<float> components)
    {
        var (t, v) = RequireCurve(index);
        var times = t.Elements.ToList();
        var values = v.Elements.ToList();
        times[index] = new BinTreeF32(0, time);
        values[index] = MakeValue(values[index], components);
        WriteCurve(times, values, t, v);
    }

    /// <summary>Add a key, placed so the times stay ascending - Riot's curves are read in order, so an
    /// out-of-order key would evaluate as a discontinuity rather than as the point the user placed.</summary>
    public void AddCurveKey(float time, IReadOnlyList<float> components)
    {
        var (t, v) = RequireCurve(0);
        var times = t.Elements.ToList();
        var values = v.Elements.ToList();
        int at = 0;
        while (at < times.Count && times[at] is BinTreeF32 f && f.Value < time) at++;
        times.Insert(at, new BinTreeF32(0, time));
        values.Insert(at, MakeValue(values[Math.Min(at, values.Count - 1)], components));
        WriteCurve(times, values, t, v);
    }

    /// <summary>Remove a key. The last one cannot go: an empty dynamics block is not the same thing as no
    /// curve, and what Riot's reader does with one is not established here.</summary>
    public void RemoveCurveKey(int index)
    {
        var (t, v) = RequireCurve(index);
        if (t.Elements.Count <= 1)
            throw new InvalidOperationException("A curve must keep at least one key.");
        var times = t.Elements.ToList();
        var values = v.Elements.ToList();
        times.RemoveAt(index);
        values.RemoveAt(index);
        WriteCurve(times, values, t, v);
    }

    private (BinTreeContainer Times, BinTreeContainer Values) RequireCurve(int index)
    {
        if (Times is not { } t || Values is not { } v || !HasCurve)
            throw new InvalidOperationException("This row has no editable curve.");
        if (index < 0 || index >= t.Elements.Count || index >= v.Elements.Count)
            throw new ArgumentOutOfRangeException(nameof(index), "No such curve key.");
        return (t, v);
    }

    private void WriteCurve(List<BinTreeProperty> times, List<BinTreeProperty> values,
        BinTreeContainer oldTimes, BinTreeContainer oldValues)
    {
        var nt = new BinTreeContainer(TimesHash, oldTimes.ElementType, times);
        var nv = new BinTreeContainer(ValuesHash, oldValues.ElementType, values);
        _dynamics!.Properties[TimesHash] = nt;
        _dynamics.Properties[ValuesHash] = nv;
        _curveEdited = true;
        var (ct, cc) = ReadCurve(nt, nv);
        CurveTimes = ct;
        CurveChannels = cc;
    }

    /// <summary>Build a values-container element matching the type already stored there. The type comes
    /// from the existing element rather than from the container's declared ElementType, so a curve keeps
    /// whatever Riot actually wrote in it.</summary>
    private static BinTreeProperty MakeValue(BinTreeProperty like, IReadOnlyList<float> v)
    {
        float C(int i) => i < v.Count ? v[i] : 0f;
        return like switch
        {
            BinTreeF32 => new BinTreeF32(0, C(0)),
            BinTreeVector2 => new BinTreeVector2(0, new System.Numerics.Vector2(C(0), C(1))),
            BinTreeVector3 => new BinTreeVector3(0, new System.Numerics.Vector3(C(0), C(1), C(2))),
            BinTreeVector4 => new BinTreeVector4(0, new System.Numerics.Vector4(C(0), C(1), C(2), C(3))),
            BinTreeColor => new BinTreeColor(0, new LeagueToolkit.Core.Primitives.Color(C(0), C(1), C(2), C(3))),
            _ => throw new NotSupportedException($"Curve keys of type {like.Type} cannot be edited."),
        };
    }

    /// <summary>times[]/values[] containers → parallel float arrays. Shared by the initial read and by
    /// every refresh after an edit, so the display can never drift from the tree.</summary>
    internal static (float[]? Times, float[][]? Channels) ReadCurve(BinTreeContainer tc, BinTreeContainer vc)
    {
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
