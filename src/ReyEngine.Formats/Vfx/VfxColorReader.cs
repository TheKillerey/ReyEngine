using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Vfx;

/// <summary>
/// M812: one colour field of one emitter, exactly as the bin authors it.
///
/// <para>This is NOT <see cref="VfxEmitterDefinition.BirthColor"/>. The resolver reads for playback, so it answers
/// an absent <c>birthColor</c> with white and an absent fresnel colour with zero - fine for drawing, wrong for an
/// inventory, which must say "this emitter does not author one". Riot's writer omits any property equal to its
/// schema default, so absence is a statement about the file and not about the colour; this record exists only for
/// fields that are present.</para>
/// </summary>
/// <param name="Field">The bin field, dotted when it is nested: <c>birthColor</c>, <c>color</c>,
/// <c>Linger.SeparateLingerColor</c>, <c>reflectionDefinition.fresnelColor</c>,
/// <c>reflectionDefinition.reflectionFresnelColor</c>.</param>
/// <param name="IsCurve">The value animates: its <c>dynamics</c> carries at least one key.</param>
/// <param name="KeyCount">Keys of the curve; 0 for a constant. The smaller of the times and values lists, as the
/// playback reader takes it.</param>
/// <param name="Constant">The authored <c>constantValue</c>; null when the field carries none. A curve that ships
/// without one is common (ValueColor writes the constant on 63.0% of its instances), and what the engine
/// substitutes for it is not established, so none is invented here. Components exceed 1.0 in shipped data (HDR).</param>
/// <param name="First">First curve key's colour, null for a constant.</param>
/// <param name="Last">Last curve key's colour, null for a constant.</param>
/// <param name="HasProbabilityTables">The value is randomised per particle through <c>probabilityTables</c>.</param>
public sealed record VfxColorValue(string Field, bool IsCurve, int KeyCount, Vector4? Constant, Vector4? First,
    Vector4? Last, bool HasProbabilityTables);

/// <summary>M812: one COLOUR texture an emitter names. Masks and data maps never become one of these - see
/// <see cref="VfxColorReader.MaskFields"/>.</summary>
/// <param name="Field">The bin field: <c>texture</c>, <c>textureMult.textureMult</c>, <c>particleColorTexture</c>,
/// <c>paletteDefinition.paletteTexture</c> or <c>reflectionDefinition.reflectionMapTexture</c>.</param>
/// <param name="Role">What the texture is to the emitter, in words.</param>
/// <param name="Path">The path as the bin spells it, or the <c>0x...</c> form of a chunk link the dictionary cannot
/// name (<see cref="BinTexturePath.Read"/>).</param>
/// <param name="Hash">The chunk it addresses, from <see cref="BinTexturePath.HashOfReference"/> - NOT
/// <c>HashAlgorithms.WadPath</c> of the text, which is a different chunk for a <c>0x...</c> reference.</param>
/// <param name="IsCubemap"><c>reflectionMapTexture</c> is a cube map, not a sprite: it is a .dds with six faces.</param>
public sealed record VfxColorTexture(string Field, string Role, string Path, ulong Hash, bool IsCubemap);

/// <summary>M812: the colour-bearing part of one <c>VfxEmitterDefinitionData</c>.</summary>
/// <param name="Index">Position among the system's emitters, in bin order. Names repeat inside a system.</param>
/// <param name="Disabled">The emitter carries <c>disabled = true</c>: the game draws nothing from it. Its colours and
/// textures are still listed, because they are still in the file.</param>
public sealed record VfxColorEmitter(int Index, string Name, bool Disabled, IReadOnlyList<VfxColorValue> Colors,
    IReadOnlyList<VfxColorTexture> Textures);

/// <summary>M812: one entry of <c>childParticleSetDefinition.childrenIdentifiers</c>. A child is named either by
/// <paramref name="EffectKey"/> (a ResourceResolver key, so the skin's own resolver decides what plays) or by
/// <paramref name="EffectLink"/> (a direct object link to a system).</summary>
public sealed record VfxChildLink(uint EffectKey, uint EffectLink);

/// <summary>M812: everything the colour inventory needs from one <c>VfxSystemDefinitionData</c> object.</summary>
/// <param name="PathHash">The object's path hash - the identity a resolver target and a child link point at.</param>
/// <param name="Name"><c>particleName</c>, else the hash.</param>
/// <param name="Children">Every child of every emitter.</param>
/// <param name="AllTextureHashes">EVERY texture-shaped reference anywhere in the system, masks and data maps
/// included. The inventory lists colour only, but "who else uses this file" must not: editing a texture changes it
/// for every system that samples it in any role, so a sharing test that looked at colour roles alone would call a
/// file unshared while another skin masks with it.</param>
public sealed record VfxColorSystem(uint PathHash, string Name, string ParticlePath,
    IReadOnlyList<VfxColorEmitter> Emitters, IReadOnlyList<VfxChildLink> Children, IReadOnlyList<ulong> AllTextureHashes)
{
    public int ColorValueCount => Emitters.Sum(e => e.Colors.Count);
    public int TextureCount => Emitters.Sum(e => e.Textures.Count);
}

/// <summary>
/// M812: reads the COLOUR of a <c>VfxSystemDefinitionData</c> straight from its bin object, for the Chroma Studio's
/// inventory.
///
/// <para><b>Why a second reader beside <see cref="VfxSystemResolver"/>.</b> The resolver builds a playback model:
/// it defaults what is absent (white birth colour, zero fresnel) and discards what the simulator does not use.
/// The inventory needs the reverse - which fields the file really authors, as the file authors them, constant or
/// curve with its key count - and a field list it can promise to keep masks out of. It reads the raw tree, and
/// only the fields below.</para>
///
/// <para><b>Colour values</b> (a <c>ValueColor</c> {constantValue Vector4, dynamics{times, values}} or a bare
/// Vector4): <c>birthColor</c>, <c>color</c>, <c>Linger.SeparateLingerColor</c>,
/// <c>reflectionDefinition.fresnelColor</c> and <c>reflectionDefinition.reflectionFresnelColor</c>. The same five
/// fields <see cref="VfxSystemResolver"/> reads as colours. Not listed, on purpose: <c>mAnimatedColorWithDistance</c>
/// on a beam (a curve over world units, not over life) and <c>modulationFactor</c> / <c>censorModulateValue</c>.</para>
///
/// <para><b>Colour textures</b>: <c>texture</c>, <c>textureMult.textureMult</c>, <c>particleColorTexture</c>,
/// <c>paletteDefinition.paletteTexture</c> and <c>reflectionDefinition.reflectionMapTexture</c> (a cube map).</para>
///
/// <para><b>Never listed</b> (<see cref="MaskFields"/>): <c>paletteDefinition.palleteSrcMixColor</c> and
/// <c>alphaErosionDefinition.erosionMapChannelMixer</c> are typed as colours but are channel MIXERS - a vector that
/// picks which channel of a texture drives a lookup - and hue-shifting one scrambles the effect; and the mask and
/// data textures <c>erosionMapName</c>, the distortion <c>normalMapTexture</c>, <c>falloffTexture</c>,
/// <c>glossTexture</c> and <c>transitionTexture</c>. Nothing here reads them; there is no exclusion step that could
/// fail to run.</para>
///
/// <para>Component ("Shimmer") emitters are not read: the format exists in two Map11 systems and no champion bin.</para>
/// </summary>
public static class VfxColorReader
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    public static readonly uint SystemClass = H("VfxSystemDefinitionData");
    private static readonly uint EmitterClass = H("VfxEmitterDefinitionData");

    private static readonly uint FParticleName = H("particleName");
    private static readonly uint FParticlePath = H("particlePath");
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

    private static readonly uint FTexture = H("texture");
    private static readonly uint FTextureMult = H("textureMult");
    private static readonly uint FParticleColorTexture = H("particleColorTexture");
    private static readonly uint FPalette = H("paletteDefinition");
    private static readonly uint FPaletteTexture = H("paletteTexture");

    private static readonly uint FChildSet = H("childParticleSetDefinition");
    private static readonly uint FChildren = H("childrenIdentifiers");
    private static readonly uint FChildKey = H("effectKey");
    private static readonly uint FChildLink = H("effect");

    private static readonly uint FConstant = H("constantValue");
    private static readonly uint FDynamics = H("dynamics");
    private static readonly uint FTimes = H("times");
    private static readonly uint FValues = H("values");
    private static readonly uint FProbTables = H("probabilityTables");

    /// <summary>The fields this reader refuses to list. Not consulted by the reader - it only ever reads the fields
    /// above, so none of these can reach a result - but kept as the written contract, which the tests check a real
    /// bin against with an independent walk.</summary>
    public static IReadOnlyList<string> MaskFields { get; } = new[]
    {
        "paletteDefinition.palleteSrcMixColor",
        "alphaErosionDefinition.erosionMapChannelMixer",
        "alphaErosionDefinition.erosionMapName",
        "distortionDefinition.normalMapTexture",
        "falloffTexture",
        "glossTexture",
        "transitionTexture",
    };

    /// <summary>The colour fields, in the order they are listed.</summary>
    public static IReadOnlyList<string> ColorFields { get; } = new[]
    {
        "birthColor", "color", "Linger.SeparateLingerColor",
        "reflectionDefinition.fresnelColor", "reflectionDefinition.reflectionFresnelColor",
    };

    /// <summary>The colour texture fields, in the order they are listed.</summary>
    public static IReadOnlyList<string> TextureFields { get; } = new[]
    {
        "texture", "textureMult.textureMult", "particleColorTexture",
        "paletteDefinition.paletteTexture", "reflectionDefinition.reflectionMapTexture",
    };

    /// <summary>Read one system. Never throws on content; a field of an unexpected type is simply not listed.</summary>
    /// <param name="resolveWadPath">Names a chunk link (<see cref="BinTexturePath.Read"/>); null leaves links as
    /// <c>0x...</c>, which still hash correctly.</param>
    public static VfxColorSystem Read(BinTreeObject system, Func<ulong, string?>? resolveWadPath = null)
    {
        var emitters = new List<VfxColorEmitter>();
        var children = new List<VfxChildLink>();

        // The resolver's rule: any container field whose elements are emitter structs holds emitters.
        foreach (var property in system.Properties.Values)
        {
            if (property is not BinTreeContainer container) continue;
            foreach (var element in container.Elements)
            {
                if (element is not BinTreeStruct emitter || emitter.ClassHash != EmitterClass) continue;
                emitters.Add(ReadEmitter(emitters.Count, emitter, resolveWadPath, children));
            }
        }

        var textures = new HashSet<ulong>();
        CollectTextureHashes(system, textures, depth: 0);

        return new VfxColorSystem(
            system.PathHash,
            Str(system.Properties, FParticleName) is { Length: > 0 } name ? name : $"0x{system.PathHash:x8}",
            Str(system.Properties, FParticlePath) ?? "",
            emitters, children, textures.Order().ToArray());
    }

    private static VfxColorEmitter ReadEmitter(int index, BinTreeStruct emitter, Func<ulong, string?>? resolveWadPath,
        List<VfxChildLink> children)
    {
        var p = emitter.Properties;
        var colors = new List<VfxColorValue>();
        var textures = new List<VfxColorTexture>();

        Add(colors, ReadColor("birthColor", Get(p, FBirthColor)));
        Add(colors, ReadColor("color", Get(p, FColor)));
        if (Get(p, FLinger) is BinTreeStruct linger)
            Add(colors, ReadColor("Linger.SeparateLingerColor", Get(linger.Properties, FLingerColor)));
        var reflection = Get(p, FReflection) as BinTreeStruct;
        if (reflection is not null)
        {
            Add(colors, ReadColor("reflectionDefinition.fresnelColor", Get(reflection.Properties, FFresnelColor)));
            Add(colors, ReadColor("reflectionDefinition.reflectionFresnelColor", Get(reflection.Properties, FReflFresnelColor)));
        }

        Add(textures, ReadTexture("texture", "Sprite", Get(p, FTexture), false, resolveWadPath));
        if (Get(p, FTextureMult) is BinTreeStruct mult)
            Add(textures, ReadTexture("textureMult.textureMult", "Multiplier", Get(mult.Properties, FTextureMult), false, resolveWadPath));
        Add(textures, ReadTexture("particleColorTexture", "Colour lookup", Get(p, FParticleColorTexture), false, resolveWadPath));
        if (Get(p, FPalette) is BinTreeStruct palette)
            Add(textures, ReadTexture("paletteDefinition.paletteTexture", "Palette", Get(palette.Properties, FPaletteTexture), false, resolveWadPath));
        if (reflection is not null)
            Add(textures, ReadTexture("reflectionDefinition.reflectionMapTexture", "Reflection cubemap",
                Get(reflection.Properties, FReflMap), true, resolveWadPath));

        if (Get(p, FChildSet) is BinTreeStruct set && Get(set.Properties, FChildren) is BinTreeContainer ids)
            foreach (var el in ids.Elements)
            {
                if (el is not BinTreeStruct id) continue;
                uint key = Get(id.Properties, FChildKey) is BinTreeHash h ? h.Value : 0u;
                uint link = Get(id.Properties, FChildLink) is BinTreeObjectLink l ? l.Value : 0u;
                if (key != 0 || link != 0) children.Add(new VfxChildLink(key, link));
            }

        return new VfxColorEmitter(index,
            Str(p, FEmitterName) is { Length: > 0 } name ? name : "(emitter)",
            Get(p, FDisabled) switch { BinTreeBool b => b.Value, BinTreeBitBool bb => bb.Value, _ => false },
            colors, textures);
    }

    private static void Add<T>(List<T> into, T? item) where T : class { if (item is not null) into.Add(item); }

    // ---- colour ------------------------------------------------------------------------------------

    private static VfxColorValue? ReadColor(string field, BinTreeProperty? property)
    {
        if (property is BinTreeOptional { Value: { } inner }) property = inner;
        switch (property)
        {
            case BinTreeVector4 or BinTreeColor:
                return new VfxColorValue(field, false, 0, AsVec4(property), null, null, false);

            case BinTreeStruct value:
            {
                var vp = value.Properties;
                Vector4? constant = Get(vp, FConstant) is { } cv ? AsVec4(cv) : null;
                int keys = 0;
                Vector4? first = null, last = null;
                bool probability = Get(vp, FProbTables) is BinTreeContainer { Elements.Count: > 0 };
                if (Get(vp, FDynamics) is BinTreeStruct dynamics)
                {
                    probability |= Get(dynamics.Properties, FProbTables) is BinTreeContainer { Elements.Count: > 0 };
                    if (Get(dynamics.Properties, FTimes) is BinTreeContainer times
                        && Get(dynamics.Properties, FValues) is BinTreeContainer values)
                    {
                        keys = Math.Min(times.Elements.Count, values.Elements.Count);
                        if (keys > 0)
                        {
                            first = AsVec4(values.Elements[0]);
                            last = AsVec4(values.Elements[keys - 1]);
                        }
                    }
                }
                return new VfxColorValue(field, keys > 0, keys, constant, keys > 0 ? first : null, keys > 0 ? last : null, probability);
            }

            default:
                return null;
        }
    }

    private static Vector4? AsVec4(BinTreeProperty? p) => p switch
    {
        BinTreeVector4 v => v.Value,
        BinTreeColor c => new Vector4(c.Value.R, c.Value.G, c.Value.B, c.Value.A),
        BinTreeVector3 v => new Vector4(v.Value, 1f),
        _ => null,
    };

    // ---- textures ----------------------------------------------------------------------------------

    private static VfxColorTexture? ReadTexture(string field, string role, BinTreeProperty? property, bool cubemap,
        Func<ulong, string?>? resolveWadPath)
    {
        if (!BinTexturePath.Is(property)) return null;
        string path = BinTexturePath.Read(property, resolveWadPath);
        if (string.IsNullOrWhiteSpace(path)) return null;
        return new VfxColorTexture(field, role, path, BinTexturePath.HashOfReference(path), cubemap);
    }

    /// <summary>Every texture-shaped reference below <paramref name="property"/>: a string ending .tex or .dds, a
    /// <c>0x...</c> string, or a non-zero WadChunkLink. A chunk link may name a mesh or an animation rather than a
    /// texture; that costs nothing, because the set is only ever intersected with texture hashes.</summary>
    private static void CollectTextureHashes(BinTreeProperty property, HashSet<ulong> into, int depth)
    {
        if (depth > 12) return;   // bins nest a few levels; this bounds a pathological one
        switch (property)
        {
            case BinTreeString s when BinTexturePath.IsTextureReference(s.Value):
                into.Add(BinTexturePath.HashOfReference(s.Value));
                break;
            case BinTreeWadChunkLink { Value: not 0 } w:
                into.Add(w.Value);
                break;
            case BinTreeStruct st:
                foreach (var child in st.Properties.Values) CollectTextureHashes(child, into, depth + 1);
                break;
            case BinTreeContainer c when c.Elements.Count > 0 && c.Elements[0] is BinTreeStruct or BinTreeContainer
                or BinTreeString or BinTreeWadChunkLink or BinTreeOptional or BinTreeMap:
                // curves are containers of floats and vectors by the hundred; only these element types can carry a path
                foreach (var child in c.Elements) CollectTextureHashes(child, into, depth + 1);
                break;
            case BinTreeOptional { Value: { } inner }:
                CollectTextureHashes(inner, into, depth + 1);
                break;
            case BinTreeMap m:
                foreach (var kv in m)
                {
                    CollectTextureHashes(kv.Key, into, depth + 1);
                    CollectTextureHashes(kv.Value, into, depth + 1);
                }
                break;
        }
    }

    /// <summary>The same walk from the object's own property table.</summary>
    private static void CollectTextureHashes(BinTreeObject o, HashSet<ulong> into, int depth)
    {
        foreach (var child in o.Properties.Values) CollectTextureHashes(child, into, depth + 1);
    }

    private static BinTreeProperty? Get(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash) =>
        p.TryGetValue(hash, out var v) ? v : null;

    private static string? Str(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash) =>
        Get(p, hash) is BinTreeString s ? s.Value : null;
}
