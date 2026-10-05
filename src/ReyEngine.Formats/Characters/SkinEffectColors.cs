using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.Formats.Characters;

/// <summary>What a colour field is to the particle it colours (M826).</summary>
public enum EffectColorRole
{
    /// <summary><c>birthColor</c>: the particle's colour at birth, a factor of the product.</summary>
    Birth,
    /// <summary><c>color</c>: the colour over life, a MULTIPLIER of the birth colour.</summary>
    OverLife,
    /// <summary><c>Linger.SeparateLingerColor</c>: replaces the colour over life while the system winds down.</summary>
    Linger,
    /// <summary><c>reflectionDefinition.fresnelColor</c>: the rim tint of a mesh particle.</summary>
    Fresnel,
    /// <summary><c>reflectionDefinition.reflectionFresnelColor</c>: tints the cubemap sample.</summary>
    ReflectionFresnel,
}

/// <summary>
/// M826: the identity of one effect colour field inside a bin - the system object (path hash), the emitter (its ordinal when the system's emitter
/// containers are walked in ascending field-hash order, so the same emitter in Riot's bin and in a re-serialised copy of it) and the field as
/// <see cref="VfxColorReader.ColorFields"/> spells it. Stable across Riot's untouched bin and the project's edited copy of it; never a value.
/// </summary>
public readonly record struct EffectColorKey(uint System, int Emitter, string Field)
{
    public EffectColorRole Role => Field switch
    {
        "birthColor" => EffectColorRole.Birth,
        "color" => EffectColorRole.OverLife,
        "Linger.SeparateLingerColor" => EffectColorRole.Linger,
        "reflectionDefinition.fresnelColor" => EffectColorRole.Fresnel,
        _ => EffectColorRole.ReflectionFresnel,
    };

    /// <summary>One of the three factors of a particle's colour product (birth x over-life or linger x texture).</summary>
    public bool IsProductFactor => Role is EffectColorRole.Birth or EffectColorRole.OverLife or EffectColorRole.Linger;
}

/// <summary>How a colour field is randomised per particle through its <c>probabilityTables</c> (one table per channel, a multiplier rolled at birth).</summary>
public enum EffectProbability
{
    None,
    /// <summary>Only the alpha channel is randomised: a hue shift moves nothing of it.</summary>
    AlphaOnly,
    /// <summary>Red, green and blue share one table (a brightness scatter): it commutes with a hue shift.</summary>
    Uniform,
    /// <summary>The tables differ per colour channel, or only some channels have one: after a hue shift the same scatter lands on different colours.</summary>
    Chromatic,
}

/// <summary>M826: one effect colour field as the bin authors it, with the verdict on whether a recolour may act on it.</summary>
/// <param name="IsBare">The field is a bare Vector4 / Color, not a <c>ValueColor</c> struct (a fresnel colour usually is).</param>
/// <param name="Constant">The authored <c>constantValue</c> (or the bare value); null when the struct carries none - which is written back as none.</param>
/// <param name="Keys">The curve keys' colours (dynamics.values), in order; empty for a constant.</param>
/// <param name="Chroma">The largest HSV saturation of any value the transform can act on (an HDR colour is normalised first): how much colour the field carries.</param>
/// <param name="StoredAsBytes">The values are <c>Color</c> properties (bytes): a recolour clamps them to 0..1.</param>
public sealed record EffectColorField(EffectColorKey Key, string SystemName, string EmitterName, bool EmitterDisabled, bool IsBare,
    Vector4? Constant, IReadOnlyList<Vector4> Keys, EffectProbability Probability, bool Recolourable, string Reason, float Chroma, bool StoredAsBytes = false)
{
    public bool IsCurve => Keys.Count > 0;
    public IEnumerable<Vector4> Values => Constant is { } c ? Keys.Prepend(c) : Keys;
}

/// <summary>M826: a field of an emitter the recolour must never touch, with the reason - so "no mask is changed" is checkable rather than asserted.</summary>
public sealed record EffectExcludedField(uint System, int Emitter, string SystemName, string EmitterName, string Field, string Value, string Reason);

/// <summary>What <see cref="SkinEffectColors.Read(BinTree, Func{uint, string?}?, ISet{uint}?, bool)"/> found in one bin.</summary>
public sealed record EffectColorScan(IReadOnlyList<EffectColorField> Fields, IReadOnlyList<EffectExcludedField> Excluded);

public enum EffectColorOutcome
{
    /// <summary>The bin now holds a value different from the one it had.</summary>
    Written,
    /// <summary>The bin already held exactly what the recolour (or the restore) gives.</summary>
    Unchanged,
    /// <summary>The field is not in Riot's bin it is derived from, or not in the bin it is written into (the bin changed shape): left alone.</summary>
    Missing,
    /// <summary>Not a colour a recolour may act on, or the two bins disagree about its shape: left exactly as authored.</summary>
    Skipped,
    /// <summary>To be given back to Riot's value, but the bin no longer holds what the recipe wrote: somebody edited it since. Left as it is.</summary>
    Kept,
}

public sealed record EffectColorEdit(EffectColorKey Key, EffectColorOutcome Outcome, string Note);

/// <summary>What <see cref="SkinEffectColors.Apply"/> did: one edit per field asked for.</summary>
public sealed record EffectColorApplyResult(IReadOnlyList<EffectColorEdit> Edits)
{
    public int Written => Edits.Count(e => e.Outcome == EffectColorOutcome.Written);
    public IEnumerable<uint> WrittenSystems => Edits.Where(e => e.Outcome == EffectColorOutcome.Written).Select(e => e.Key.System).Distinct();
}

/// <summary>The bin after a rewrite - null <see cref="Bytes"/> when nothing in it changed.</summary>
public sealed record EffectColorRewrite(byte[]? Bytes, EffectColorApplyResult Result);

/// <summary>
/// M826: Chroma Studio C4 - the colour VALUES of a skin's effects (<c>birthColor</c>, <c>color</c>, <c>Linger.SeparateLingerColor</c> and the two fresnel
/// colours of <c>reflectionDefinition</c>) read from the bin and recoloured with the same <see cref="ColorTransform"/> the body uses.
///
/// <para><b>What is transformed.</b> The constant and EVERY curve key of a field, red green and blue through the transform, alpha as the bin has it.
/// A field the emitter does not author is not read, not listed and never written (an absent field is the schema default); a struct that authors no
/// <c>constantValue</c> gets none; a curve keeps its key count and times. A colour the transform cannot act on (negative, NaN: <see cref="ColorTransform.CanTransform(Vector4)"/>)
/// stays exactly as authored, an HDR colour keeps its intensity. The channel-mixer vectors that are TYPED as colours
/// (<c>paletteDefinition.palleteSrcMixColor</c>, <c>alphaErosionDefinition.erosionMapChannelMixer</c>) are never read as colours.</para>
///
/// <para><b>The product rule.</b> A particle's colour is <c>birthColor x (colour over life | linger colour) x texture</c>, so brightness and saturation
/// applied to every factor would compound (a white multiplier becomes (B, B, B) and the result B squared). Per emitter ONE factor carries brightness
/// and saturation (<see cref="Carriers"/>): of the colour-value factors the recolour acts on, the one that carries the most colour (the highest HSV
/// saturation of its keys; on a tie the colour over life, which the linger colour replaces and so shares a slot with). Every other factor and every
/// colour texture takes the HUE-ONLY form of the transform (<see cref="HueOnly"/>: hue, colourise, hue range, grey guard and strength, saturation and
/// brightness left at 1), which is a no-op on white and grey, so a white multiplier stays white and the result is the transform once. The two fresnel
/// colours are not factors of that product (a separate shader stage), so each takes the full transform.</para>
///
/// <para><b>Never compounding.</b> <see cref="Apply"/> derives every value from RIOT's bin and writes it into the CURRENT bin, so the result is a
/// function of Riot's value, the transform and the set of fields the recipe covers - however often it is run.</para>
/// </summary>
public static class SkinEffectColors
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static readonly uint EmitterClass = H("VfxEmitterDefinitionData");
    private static readonly uint FParticleName = H("particleName");
    private static readonly uint FEmitterName = H("emitterName");
    private static readonly uint FDisabled = H("disabled");
    private static readonly uint FBirthColor = H("birthColor");
    private static readonly uint FColor = H("color");
    private static readonly uint FLinger = H("Linger");
    private static readonly uint FLingerColor = H("SeparateLingerColor");
    private static readonly uint FReflection = H("reflectionDefinition");
    private static readonly uint FFresnelColor = H("fresnelColor");
    private static readonly uint FReflFresnelColor = H("reflectionFresnelColor");
    private static readonly uint FReflMap = H("reflectionMapTexture");
    private static readonly uint FConstant = H("constantValue");
    private static readonly uint FDynamics = H("dynamics");
    private static readonly uint FTimes = H("times");
    private static readonly uint FValues = H("values");
    private static readonly uint FProbTables = H("probabilityTables");
    private static readonly uint FKeyTimes = H("keyTimes");
    private static readonly uint FKeyValues = H("keyValues");
    private static readonly uint FPalette = H("paletteDefinition");
    private static readonly uint FPaletteMix = H("palleteSrcMixColor");
    private static readonly uint FErosion = H("alphaErosionDefinition");
    private static readonly uint FErosionMixer = H("erosionMapChannelMixer");
    private static readonly uint FErosionMap = H("erosionMapName");
    private static readonly uint FDistortion = H("distortionDefinition");
    private static readonly uint FNormalMap = H("normalMapTexture");
    private static readonly uint FFalloff = H("falloffTexture");
    private static readonly uint FGloss = H("glossTexture");
    private static readonly uint FTransition = H("transitionTexture");

    /// <summary>The colour value fields, in the order they are listed - <see cref="VfxColorReader.ColorFields"/>.</summary>
    public static IReadOnlyList<string> Fields => VfxColorReader.ColorFields;

    /// <summary>Two colours closer in saturation than this are a tie when choosing the carrier of brightness and saturation.</summary>
    public const float ChromaTie = 1e-4f;

    // ====================================================================== the policy

    /// <summary>The form of <paramref name="transform"/> a factor that does NOT carry brightness and saturation takes: hue, colourise, hue range, grey guard
    /// and strength unchanged, saturation and brightness at 1.</summary>
    public static ColorTransform HueOnly(ColorTransform transform) => transform with { Saturation = 1f, Brightness = 1f };

    /// <summary>The fields that carry brightness and saturation: per emitter, the colour-value slot (birth, or colour over life and linger together) that holds
    /// the most colour, plus every fresnel colour (not a factor of the product). Computed over the fields given, which are the ones being recoloured.</summary>
    public static HashSet<EffectColorKey> Carriers(IEnumerable<EffectColorField> fields)
    {
        var carriers = new HashSet<EffectColorKey>();
        foreach (var emitter in fields.GroupBy(f => (f.Key.System, f.Key.Emitter)))
        {
            var factors = emitter.Where(f => f.Key.IsProductFactor).ToList();
            foreach (var f in emitter) if (!f.Key.IsProductFactor) carriers.Add(f.Key);
            if (factors.Count == 0) continue;
            float birth = factors.Where(f => f.Key.Role == EffectColorRole.Birth).Select(f => f.Chroma).DefaultIfEmpty(-1f).Max();
            float life = factors.Where(f => f.Key.Role != EffectColorRole.Birth).Select(f => f.Chroma).DefaultIfEmpty(-1f).Max();
            bool birthCarries = birth >= 0f && (life < 0f || birth > life + ChromaTie);
            foreach (var f in factors)
                if ((f.Key.Role == EffectColorRole.Birth) == birthCarries) carriers.Add(f.Key);
        }
        return carriers;
    }

    /// <summary>The transform one field takes: the full transform for a carrier, the hue-only form for a factor that is not.</summary>
    public static ColorTransform TransformFor(EffectColorKey key, ColorTransform transform, ISet<EffectColorKey> carriers) =>
        carriers.Contains(key) ? transform : HueOnly(transform);

    /// <summary>Every texture of an effect takes the hue-only form: a texture multiplies the colour values, so brightness and saturation applied to it
    /// would compound with theirs, and an 8-bit texture cannot carry a brightness above 1.</summary>
    public static ColorTransform TextureTransform(ColorTransform transform) => HueOnly(transform);

    // ====================================================================== reading

    /// <summary>Read the colour fields of every system in the bin (or of those in <paramref name="onlySystems"/>). Never throws on content.</summary>
    public static EffectColorScan Read(byte[] bin, Func<uint, string?>? resolve = null, ISet<uint>? onlySystems = null, bool includeExcluded = true) =>
        Read(SafeBinTree.Parse(bin), resolve, onlySystems, includeExcluded);

    public static EffectColorScan Read(BinTree tree, Func<uint, string?>? resolve = null, ISet<uint>? onlySystems = null, bool includeExcluded = true)
    {
        var fields = new List<EffectColorField>();
        var excluded = new List<EffectExcludedField>();
        foreach (var o in tree.Objects.Values)
        {
            if (o.ClassHash != VfxColorReader.SystemClass || (onlySystems is not null && !onlySystems.Contains(o.PathHash))) continue;
            string system = o.Properties.TryGetValue(FParticleName, out var n) && n is BinTreeString { Value.Length: > 0 } s ? s.Value : $"0x{o.PathHash:x8}";
            if (system.StartsWith("0x", StringComparison.Ordinal) && resolve?.Invoke(o.PathHash) is { Length: > 0 } named) system = named;
            var emitters = Emitters(o);
            for (int i = 0; i < emitters.Count; i++)
            {
                var emitter = emitters[i];
                string name = Str(emitter.Properties, FEmitterName) is { Length: > 0 } en ? en : "(emitter)";
                bool disabled = emitter.Properties.TryGetValue(FDisabled, out var d) && d switch { BinTreeBool b => b.Value, BinTreeBitBool bb => bb.Value, _ => false };
                foreach (var slot in Slots(emitter, o.PathHash, i))
                    fields.Add(Describe(slot, system, name, disabled));
                if (includeExcluded) CollectExcluded(emitter, o.PathHash, i, system, name, excluded);
            }
        }
        return new EffectColorScan(fields, excluded);
    }

    /// <summary>The emitter structs of a system in a deterministic order: its container properties by ascending field hash, each in list order. The
    /// resolver and the colour reader walk the dictionary's own order; that order can differ between Riot's file and a re-serialised copy, this one cannot.</summary>
    private static List<BinTreeStruct> Emitters(BinTreeObject system)
    {
        var list = new List<BinTreeStruct>();
        foreach (var (_, property) in system.Properties.OrderBy(kv => kv.Key))
            if (property is BinTreeContainer container)
                foreach (var element in container.Elements)
                    if (element is BinTreeStruct emitter && emitter.ClassHash == EmitterClass) list.Add(emitter);
        return list;
    }

    /// <summary>A located colour field: where its values live in a tree, so they can be read and written in place.</summary>
    private sealed class Slot
    {
        public required EffectColorKey Key { get; init; }
        /// <summary>The bare Vector4 / Color property, when the field is not a struct.</summary>
        public BinTreeProperty? Bare { get; init; }
        public BinTreeProperty? Constant { get; init; }
        public List<BinTreeProperty> KeyProps { get; } = new();
        public EffectProbability Probability { get; set; }
    }

    private static IEnumerable<Slot> Slots(BinTreeStruct emitter, uint system, int index)
    {
        var p = emitter.Properties;
        if (Locate(new(system, index, "birthColor"), Get(p, FBirthColor)) is { } birth) yield return birth;
        if (Locate(new(system, index, "color"), Get(p, FColor)) is { } color) yield return color;
        if (Get(p, FLinger) is BinTreeStruct linger && Locate(new(system, index, "Linger.SeparateLingerColor"), Get(linger.Properties, FLingerColor)) is { } sep) yield return sep;
        if (Get(p, FReflection) is BinTreeStruct reflection)
        {
            if (Locate(new(system, index, "reflectionDefinition.fresnelColor"), Get(reflection.Properties, FFresnelColor)) is { } fresnel) yield return fresnel;
            if (Locate(new(system, index, "reflectionDefinition.reflectionFresnelColor"), Get(reflection.Properties, FReflFresnelColor)) is { } mix) yield return mix;
        }
    }

    private static Slot? Locate(EffectColorKey key, BinTreeProperty? property)
    {
        if (property is BinTreeOptional { Value: { } inner }) property = inner;
        switch (property)
        {
            case BinTreeVector4 or BinTreeColor:
                return new Slot { Key = key, Bare = property };

            case BinTreeStruct value:
            {
                var vp = value.Properties;
                var constant = Get(vp, FConstant) is { } c && IsColour(c) ? c : null;
                var slot = new Slot { Key = key, Constant = constant };
                BinTreeStruct? dynamics = Get(vp, FDynamics) as BinTreeStruct;
                if (dynamics is not null
                    && Get(dynamics.Properties, FTimes) is BinTreeContainer times && Get(dynamics.Properties, FValues) is BinTreeContainer values)
                {
                    int n = Math.Min(times.Elements.Count, values.Elements.Count);
                    for (int i = 0; i < n; i++)
                        if (IsColour(values.Elements[i])) slot.KeyProps.Add(values.Elements[i]);
                        else return null;   // a curve of something else than colours: not a ValueColor, left alone
                }
                slot.Probability = Probability(Get(vp, FProbTables) as BinTreeContainer, dynamics is null ? null : Get(dynamics.Properties, FProbTables) as BinTreeContainer);
                // a struct that authors no colour at all (only a probability table, say) has nothing to recolour
                return constant is null && slot.KeyProps.Count == 0 ? null : slot;
            }

            default:
                return null;
        }
    }

    private static bool IsColour(BinTreeProperty p) => p is BinTreeVector4 or BinTreeColor or BinTreeVector3;

    private static Vector4? ValueOf(BinTreeProperty? p) => p switch
    {
        BinTreeVector4 v => v.Value,
        BinTreeColor c => new Vector4(c.Value.R, c.Value.G, c.Value.B, c.Value.A),
        BinTreeVector3 v => new Vector4(v.Value, 1f),
        _ => null,
    };

    /// <summary>Write red, green and blue of <paramref name="v"/> into the property, keeping the alpha it holds. A Color is stored in bytes, so its
    /// channels are clamped to 0..1 (a brightness boost cannot be stored in it).</summary>
    private static void Write(BinTreeProperty p, Vector4 v)
    {
        switch (p)
        {
            case BinTreeVector4 x: x.Value = new Vector4(v.X, v.Y, v.Z, x.Value.W); break;
            case BinTreeColor x:
                // stored in bytes: the value is clamped to 0..1 and rounded to the byte it will be written as, so what the tree holds is what a re-read of the bin gives back
                static float Byte(float f) => float.IsNaN(f) ? 0f : MathF.Round(Math.Clamp(f, 0f, 1f) * 255f) / 255f;
                x.Value = new LeagueToolkit.Core.Primitives.Color(Byte(v.X), Byte(v.Y), Byte(v.Z), x.Value.A);
                break;
            case BinTreeVector3 x: x.Value = new Vector3(v.X, v.Y, v.Z); break;
        }
    }

    private static EffectColorField Describe(Slot slot, string system, string emitter, bool disabled)
    {
        var constant = ValueOf(slot.Bare ?? slot.Constant);
        var keys = slot.KeyProps.Select(k => ValueOf(k) ?? default).ToList();
        var values = constant is { } c ? keys.Prepend(c) : keys;
        float chroma = -1f;
        bool any = false, blocked = false;
        foreach (var v in values)
        {
            if (!ColorTransform.CanTransform(v)) { blocked = true; continue; }
            any = true;
            chroma = Math.Max(chroma, Chroma(v));
        }
        bool ok = any;
        string reason = any ? "" : blocked ? "Every value is negative or not a number: left exactly as authored." : "No colour value is written.";
        bool bytes = (slot.Bare ?? slot.Constant ?? slot.KeyProps.FirstOrDefault()) is BinTreeColor;
        return new EffectColorField(slot.Key, system, emitter, disabled, slot.Bare is not null, constant, keys, slot.Probability, ok, reason, Math.Max(chroma, 0f), bytes);
    }

    /// <summary>HSV saturation of a colour, an HDR one normalised to a largest channel of 1 first (the way the transform reads it).</summary>
    public static float Chroma(Vector4 c)
    {
        float m = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        float r = c.X, g = c.Y, b = c.Z;
        if (m > 1f) { r /= m; g /= m; b /= m; }
        return Hsv.FromRgb(r, g, b, out _, out float s, out _) ? s : 0f;
    }

    private static EffectProbability Probability(BinTreeContainer? onValue, BinTreeContainer? onDynamics)
    {
        var tables = onDynamics is { Elements.Count: > 0 } ? onDynamics : onValue;
        if (tables is null || tables.Elements.Count == 0) return EffectProbability.None;
        var channel = new (float[] T, float[] V)?[4];
        for (int i = 0; i < Math.Min(4, tables.Elements.Count); i++)
        {
            if (tables.Elements[i] is not BinTreeStruct s) continue;
            if (Get(s.Properties, FKeyTimes) is not BinTreeContainer t || Get(s.Properties, FKeyValues) is not BinTreeContainer v) continue;
            int n = Math.Min(t.Elements.Count, v.Elements.Count);
            if (n == 0) continue;
            var times = new float[n]; var vals = new float[n];
            for (int k = 0; k < n; k++)
            {
                times[k] = t.Elements[k] is BinTreeF32 tf ? tf.Value : 0f;
                vals[k] = v.Elements[k] is BinTreeF32 vf ? vf.Value : 0f;
            }
            channel[i] = (times, vals);
        }
        int rgb = (channel[0] is null ? 0 : 1) + (channel[1] is null ? 0 : 1) + (channel[2] is null ? 0 : 1);
        if (rgb == 0) return channel[3] is null ? EffectProbability.None : EffectProbability.AlphaOnly;
        if (rgb == 3 && channel[0]!.Value.T.AsSpan().SequenceEqual(channel[1]!.Value.T) && channel[0]!.Value.V.AsSpan().SequenceEqual(channel[1]!.Value.V)
            && channel[0]!.Value.T.AsSpan().SequenceEqual(channel[2]!.Value.T) && channel[0]!.Value.V.AsSpan().SequenceEqual(channel[2]!.Value.V))
            return EffectProbability.Uniform;
        return EffectProbability.Chromatic;
    }

    // ====================================================================== what is left alone

    private const string MixerReason = "A channel mixer typed as a colour: it picks which channel of a texture drives a lookup, so a hue shift would scramble the effect.";
    private const string DataReason = "A data map (erosion, distortion, falloff, gloss or transition), not a colour picture: a hue shift would corrupt what it drives.";
    private const string CubemapReason = "A reflection cubemap (DDS, six faces): the project writer handles TEX BC1/BC3 only, so it is left as it is.";

    private static void CollectExcluded(BinTreeStruct emitter, uint system, int index, string systemName, string emitterName, List<EffectExcludedField> into)
    {
        var p = emitter.Properties;
        void Add(string field, string value, string reason) => into.Add(new EffectExcludedField(system, index, systemName, emitterName, field, value, reason));

        if (Get(p, FPalette) is BinTreeStruct palette && Get(palette.Properties, FPaletteMix) is { } mix && ValueText(mix) is { } mixText)
            Add("paletteDefinition.palleteSrcMixColor", mixText, MixerReason);
        if (Get(p, FErosion) is BinTreeStruct erosion)
        {
            if (Get(erosion.Properties, FErosionMixer) is { } mixer && ValueText(mixer) is { } mixerText)
                Add("alphaErosionDefinition.erosionMapChannelMixer", mixerText, MixerReason);
            if (Get(erosion.Properties, FErosionMap) is { } map && PathOf(map) is { } mapPath)
                Add("alphaErosionDefinition.erosionMapName", mapPath, DataReason);
        }
        if (Get(p, FDistortion) is BinTreeStruct distortion && Get(distortion.Properties, FNormalMap) is { } normal && PathOf(normal) is { } normalPath)
            Add("distortionDefinition.normalMapTexture", normalPath, DataReason);
        foreach (var (hash, field) in new[] { (FFalloff, "falloffTexture"), (FGloss, "glossTexture"), (FTransition, "transitionTexture") })
            foreach (var found in Find(emitter, hash, 0))
                if (PathOf(found) is { } path) Add(field, path, DataReason);
        if (Get(p, FReflection) is BinTreeStruct reflection && Get(reflection.Properties, FReflMap) is { } cube && PathOf(cube) is { } cubePath)
            Add("reflectionDefinition.reflectionMapTexture", cubePath, CubemapReason);
    }

    private static string? PathOf(BinTreeProperty p) => p switch
    {
        BinTreeString { Value.Length: > 0 } s => s.Value,
        BinTreeWadChunkLink { Value: not 0 } w => $"0x{w.Value:x16}",
        _ => null,
    };

    private static string? ValueText(BinTreeProperty p) => ValueOf(p is BinTreeStruct s && Get(s.Properties, FConstant) is { } c ? c : p) is { } v
        ? SkinColorInventory.Format(v)
        : null;

    private static IEnumerable<BinTreeProperty> Find(BinTreeProperty property, uint field, int depth)
    {
        if (depth > 8) yield break;
        switch (property)
        {
            case BinTreeStruct st:
                foreach (var (hash, child) in st.Properties)
                {
                    if (hash == field) yield return child;
                    else foreach (var inner in Find(child, field, depth + 1)) yield return inner;
                }
                break;
            case BinTreeContainer c when c.Elements.Count > 0 && c.Elements[0] is BinTreeStruct or BinTreeContainer:
                foreach (var el in c.Elements) foreach (var inner in Find(el, field, depth + 1)) yield return inner;
                break;
            case BinTreeOptional { Value: { } v }:
                foreach (var inner in Find(v, field, depth + 1)) yield return inner;
                break;
        }
    }

    // ====================================================================== the rewrite

    /// <summary>
    /// Write into <paramref name="current"/> (in place) the colours the recolour and the restore say, derived from <paramref name="riot"/>.
    /// </summary>
    /// <param name="recolour">Fields to set to the transform of Riot's value (the carrier of brightness and saturation is chosen among these).</param>
    /// <param name="restore">Fields to put back to Riot's value (a field in both lists is recoloured).</param>
    /// <param name="recipe">The transform the recipe that owns the <paramref name="restore"/> fields wrote them with, and <paramref name="recipeKeys"/> the fields it
    /// owned then (which decides the carriers it used). When given, a field whose value in <paramref name="current"/> is no longer what that recipe wrote is NOT put back -
    /// it was edited since (outcome <see cref="EffectColorOutcome.Kept"/>). Null: put back whatever it holds (an explicit Revert).</param>
    public static EffectColorApplyResult Apply(BinTree current, BinTree riot, ColorTransform transform,
        IReadOnlyCollection<EffectColorKey> recolour, IReadOnlyCollection<EffectColorKey> restore,
        ColorTransform? recipe = null, IReadOnlyCollection<EffectColorKey>? recipeKeys = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(riot);
        ArgumentNullException.ThrowIfNull(transform);
        var edits = new List<EffectColorEdit>();
        var want = new List<(EffectColorKey Key, bool Recolour)>();
        var recolourSet = recolour.ToHashSet();
        foreach (var key in recolour) want.Add((key, true));
        foreach (var key in restore) if (!recolourSet.Contains(key)) want.Add((key, false));
        if (want.Count == 0) return new EffectColorApplyResult(edits);

        var systems = want.Select(w => w.Key.System).ToHashSet();
        var riotSlots = IndexSlots(riot, systems);
        var currentSlots = IndexSlots(current, systems);

        // the fields as Riot authors them, for the carriers: the ones asked to be recoloured (and, for the recipe's own check, the ones it owned)
        EffectColorField? FieldOf(EffectColorKey key) => riotSlots.TryGetValue(key, out var s) ? Describe(s, "", "", false) : null;
        var carriers = Carriers(recolourSet.Select(FieldOf).Where(f => f is { Recolourable: true }).Select(f => f!));
        HashSet<EffectColorKey>? recipeCarriers = recipeKeys is null ? null : Carriers(recipeKeys.Select(FieldOf).Where(f => f is { Recolourable: true }).Select(f => f!));

        foreach (var (key, doRecolour) in want)
        {
            if (!riotSlots.TryGetValue(key, out var from))
            { edits.Add(new(key, EffectColorOutcome.Missing, "Riot's bin has no such field, so there is nothing to derive it from.")); continue; }
            if (!currentSlots.TryGetValue(key, out var into))
            { edits.Add(new(key, EffectColorOutcome.Missing, "The project's bin no longer has this field.")); continue; }
            if (from.KeyProps.Count != into.KeyProps.Count || (from.Constant is null) != (into.Constant is null) || (from.Bare is null) != (into.Bare is null))
            { edits.Add(new(key, EffectColorOutcome.Skipped, "The project's bin holds this field in another shape than Riot's (keys added or removed): left as it is.")); continue; }

            var riotField = Describe(from, "", "", false);
            if (!riotField.Recolourable)
            { edits.Add(new(key, EffectColorOutcome.Skipped, riotField.Reason)); continue; }

            var tf = doRecolour ? TransformFor(key, transform, carriers) : ColorTransform.Identity;
            if (!doRecolour && recipe is not null && recipeCarriers is not null
                && !HoldsRecipeValue(Describe(into, "", "", false), riotField, TransformFor(key, recipe, recipeCarriers)))
            { edits.Add(new(key, EffectColorOutcome.Kept, "Edited since the recolour: left as it is.")); continue; }

            bool moved = false;
            void Set(BinTreeProperty target, BinTreeProperty source)
            {
                var original = ValueOf(source)!.Value;
                var made = doRecolour ? tf.Apply(original) : original;
                var before = ValueOf(target)!.Value;
                Write(target, made);
                var after = ValueOf(target)!.Value;
                if (!SameRgb(before, after)) moved = true;
            }
            if (from.Bare is not null) Set(into.Bare!, from.Bare);
            if (from.Constant is not null) Set(into.Constant!, from.Constant);
            for (int i = 0; i < from.KeyProps.Count; i++) Set(into.KeyProps[i], from.KeyProps[i]);
            edits.Add(new(key, moved ? EffectColorOutcome.Written : EffectColorOutcome.Unchanged, ""));
        }
        return new EffectColorApplyResult(edits);
    }

    /// <summary>The same on bytes: parses both bins, applies, and serialises <paramref name="current"/> only when a value changed.</summary>
    public static EffectColorRewrite Rewrite(byte[] current, byte[] riot, ColorTransform transform,
        IReadOnlyCollection<EffectColorKey> recolour, IReadOnlyCollection<EffectColorKey> restore,
        ColorTransform? recipe = null, IReadOnlyCollection<EffectColorKey>? recipeKeys = null)
    {
        var tree = SafeBinTree.Parse(current);
        var result = Apply(tree, SafeBinTree.Parse(riot), transform, recolour, restore, recipe, recipeKeys);
        return new EffectColorRewrite(result.Written > 0 ? Serialize(tree) : null, result);
    }

    /// <summary>The Particle Editor's own writer: a tolerant parse that had to abandon part of an object is never written back, and Riot ships no empty container.</summary>
    public static byte[] Serialize(BinTree tree)
    {
        SafeBinTree.ThrowIfLossy(tree, "this particle bin");
        BinEmptyProperty.Strip(tree);
        using var ms = new MemoryStream();
        tree.Write(ms);
        return ms.ToArray();
    }

    /// <summary>Whether <paramref name="held"/> (a field of the project's bin) carries what a recipe whose transform for it was <paramref name="recipeTransform"/> writes for
    /// Riot's field <paramref name="riot"/>: the transform of every value, red green and blue (a Color is stored in bytes, so it is the clamped value to within a byte's rounding).</summary>
    public static bool HoldsRecipeValue(EffectColorField held, EffectColorField riot, ColorTransform recipeTransform)
    {
        var a = held.Values.ToList();
        var b = riot.Values.ToList();
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            var expected = recipeTransform.Apply(b[i]);
            if (held.StoredAsBytes) expected = new Vector4(Math.Clamp(expected.X, 0f, 1f), Math.Clamp(expected.Y, 0f, 1f), Math.Clamp(expected.Z, 0f, 1f), expected.W);
            float tolerance = held.StoredAsBytes ? 0.004f : 1e-6f;
            if (Math.Abs(a[i].X - expected.X) > tolerance || Math.Abs(a[i].Y - expected.Y) > tolerance || Math.Abs(a[i].Z - expected.Z) > tolerance) return false;
        }
        return true;
    }

    /// <summary>The values of the fields as the tree holds them now (bare or constant first, then the keys), red green and blue, for <see cref="Restore"/>.</summary>
    internal static Dictionary<EffectColorKey, Vector4[]> Capture(BinTree tree, IReadOnlyCollection<EffectColorKey> keys)
    {
        var slots = IndexSlots(tree, keys.Select(k => k.System).ToHashSet());
        var saved = new Dictionary<EffectColorKey, Vector4[]>();
        foreach (var key in keys)
            if (slots.TryGetValue(key, out var slot)) saved[key] = ValuesOf(slot).ToArray();
        return saved;
    }

    /// <summary>Put values captured by <see cref="Capture"/> back. Returns the systems in which something changed.</summary>
    internal static HashSet<uint> Restore(BinTree tree, IReadOnlyDictionary<EffectColorKey, Vector4[]> saved, IReadOnlyCollection<EffectColorKey> keys)
    {
        var changed = new HashSet<uint>();
        var slots = IndexSlots(tree, keys.Select(k => k.System).ToHashSet());
        foreach (var key in keys)
        {
            if (!saved.TryGetValue(key, out var values) || !slots.TryGetValue(key, out var slot)) continue;
            var props = PropsOf(slot).ToList();
            for (int i = 0; i < props.Count && i < values.Length; i++)
            {
                var before = ValueOf(props[i])!.Value;
                Write(props[i], values[i]);
                if (!SameRgb(before, ValueOf(props[i])!.Value)) changed.Add(key.System);
            }
        }
        return changed;
    }

    private static IEnumerable<BinTreeProperty> PropsOf(Slot slot)
    {
        if (slot.Bare is not null) yield return slot.Bare;
        if (slot.Constant is not null) yield return slot.Constant;
        foreach (var k in slot.KeyProps) yield return k;
    }

    private static IEnumerable<Vector4> ValuesOf(Slot slot) => PropsOf(slot).Select(p => ValueOf(p)!.Value);

    private static Dictionary<EffectColorKey, Slot> IndexSlots(BinTree tree, ISet<uint> systems)
    {
        var map = new Dictionary<EffectColorKey, Slot>();
        foreach (var o in tree.Objects.Values)
        {
            if (o.ClassHash != VfxColorReader.SystemClass || !systems.Contains(o.PathHash)) continue;
            var emitters = Emitters(o);
            for (int i = 0; i < emitters.Count; i++)
                foreach (var slot in Slots(emitters[i], o.PathHash, i)) map.TryAdd(slot.Key, slot);
        }
        return map;
    }

    /// <summary>Bit-exact on red, green and blue (so -0 differs from +0 and a NaN equals itself), the comparison the transform itself uses.</summary>
    public static bool SameRgb(Vector4 a, Vector4 b) =>
        BitConverter.SingleToInt32Bits(a.X) == BitConverter.SingleToInt32Bits(b.X)
        && BitConverter.SingleToInt32Bits(a.Y) == BitConverter.SingleToInt32Bits(b.Y)
        && BitConverter.SingleToInt32Bits(a.Z) == BitConverter.SingleToInt32Bits(b.Z);

    // ====================================================================== tree helpers

    private static BinTreeProperty? Get(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash) => p.TryGetValue(hash, out var v) ? v : null;
    private static string? Str(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash) => Get(p, hash) is BinTreeString s ? s.Value : null;
}
