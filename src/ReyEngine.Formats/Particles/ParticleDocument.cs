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

    private ParticleDocument(BinTree tree, IReadOnlyList<ParticleSystemEntry> systems)
    {
        _tree = tree;
        Systems = systems;
    }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        _tree.Write(ms);
        return ms.ToArray();
    }

    /// <summary>Parse a .bin into its VFX systems; null when it contains none (not a particle bin).</summary>
    public static ParticleDocument? Parse(byte[] data)
    {
        BinTree tree;
        try { tree = SafeBinTree.Parse(data); }
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
                        emitters.Add(ParticleEmitterEntry.From(s));
            }
            if (emitters.Count > 0)
                systems.Add(new ParticleSystemEntry(o.PathHash, name, path, emitters));
        }
        return systems.Count > 0 ? new ParticleDocument(tree, systems) : null;
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

    internal static ParticleEmitterEntry From(BinTreeStruct emitter)
    {
        var props = new List<ParticleProperty>();
        string name = "(emitter)";
        foreach (var (hash, prop) in emitter.Properties)
        {
            string fieldName = FieldNames.TryGetValue(hash, out var fn) ? fn : $"0x{hash:x8}";
            if (fieldName == "emitterName" && prop is BinTreeString ns) name = ns.Value;

            string module = ModuleOf(fieldName);
            switch (prop)
            {
                // Value* structs: constantValue is the editable scalar; dynamics = the curve keys.
                case BinTreeStruct vs when Field(vs.Properties, "constantValue") is { } cv:
                    var (times, channels) = ReadDynamics(vs.Properties);
                    props.Add(new ParticleProperty(module, fieldName, cv, isConstantOfCurve: true,
                        curveTimes: times, curveChannels: channels));
                    break;
                // plain primitives (numbers, bools, strings/paths, vectors, colours) — directly editable
                case BinTreeF32 or BinTreeU8 or BinTreeU16 or BinTreeU32 or BinTreeI32 or BinTreeBool
                    or BinTreeBitBool or BinTreeString or BinTreeVector2 or BinTreeVector3 or BinTreeVector4
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

    private static string ModuleOf(string field) => field switch
    {
        "rate" or "particleLifetime" or "lifetime" or "particleLinger" or "timeBeforeFirstEmission"
            or "isSingleParticle" or "disabled" or "importance" => "Emission",
        "birthScale0" or "birthColor" or "birthVelocity" or "birthRotation0" or "birthRotationalVelocity0"
            or "birthUvScrollRate" or "birthFrameRate" or "birthUvRotation0" or "birthDrag" => "Birth",
        "emitterPosition" or "isLocalOrientation" or "bindWeight" or "emitOffset" or "shape" => "Position",
        "velocity" or "worldAcceleration" or "acceleration" or "drag" => "Velocity",
        "scale0" or "isUniformScale" or "scaleBirthScaleByBoundObjectSize" or "scaleEmitOffsetByBoundObjectSize" => "Scale",
        "color" or "colorLookUpTypeX" or "colorLookUpTypeY" or "lingerColor" => "Color",
        "blendMode" or "pass" or "miscRenderFlags" or "alphaRef" or "isDirectionOriented" or "primitive"
            or "isRandomStartFrame" or "depthBiasFactors" or "renderPhaseOverride" => "Render",
        "texture" or "texDiv" or "numFrames" or "frameRate" or "uvScroll" or "textureMult" or "uvRotation0"
            or "uvScale0" or "particleColorTexture" or "falloffTexture" => "Texture",
        _ => "Other",
    };

    /// <summary>Known emitter field names (FNV1a-lowercase), so rows show names instead of raw hashes.</summary>
    private static readonly Dictionary<uint, string> FieldNames = BuildFieldNames();
    private static Dictionary<uint, string> BuildFieldNames()
    {
        string[] names =
        {
            "emitterName","rate","particleLifetime","lifetime","particleLinger","timeBeforeFirstEmission",
            "isSingleParticle","disabled","importance","blendMode","pass","miscRenderFlags","alphaRef",
            "isDirectionOriented","primitive","birthScale0","scale0","isUniformScale","birthColor","color",
            "birthVelocity","velocity","worldAcceleration","acceleration","drag","birthDrag",
            "birthRotation0","birthRotationalVelocity0","birthUvScrollRate","birthFrameRate","birthUvRotation0",
            "emitterPosition","isLocalOrientation","bindWeight","emitOffset","shape","texture","texDiv",
            "numFrames","frameRate","uvScroll","textureMult","uvRotation0","uvScale0","isRandomStartFrame",
            "particleColorTexture","falloffTexture","colorLookUpTypeX","colorLookUpTypeY","lingerColor",
            "scaleBirthScaleByBoundObjectSize","scaleEmitOffsetByBoundObjectSize","depthBiasFactors",
            "renderPhaseOverride","doesLifetimeScale","censorModifiers","emitterDefinitionDataFlags",
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

    public ParticleProperty(string module, string name, BinTreeProperty prop, bool isConstantOfCurve = false,
        float[]? curveTimes = null, float[][]? curveChannels = null, bool readOnly = false)
    {
        Module = module;
        Name = name;
        _prop = prop;
        IsCurveConstant = isConstantOfCurve;
        CurveTimes = curveTimes;
        CurveChannels = curveChannels;
        IsReadOnly = readOnly || BinValueEditor.KindOf(prop) == BinValueKind.ReadOnly;
        TypeName = prop.Type.ToString() + (HasCurve ? $" + curve({CurveTimes!.Length} keys)" : "");
        _originalText = SafeFormat();
    }

    public string CurrentText => SafeFormat();
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
