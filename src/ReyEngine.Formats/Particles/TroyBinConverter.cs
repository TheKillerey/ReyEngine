using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Particles;

/// <summary>How an emitter's texture was chosen, so the UI can say so instead of implying fidelity.</summary>
public enum TroyTextureSource
{
    /// <summary>The emitter name appears in the texture's filename - the strongest available signal,
    /// 352 agreements across the corpus.</summary>
    NameMatch,
    /// <summary>Nothing matched by name, so the next unclaimed texture was used in file order.</summary>
    Positional,
    /// <summary>The file has no texture left for this emitter.</summary>
    None,
}

public sealed record TroyEmitterNote(string EmitterName, string? TexturePath, TroyTextureSource Source);

/// <summary>A legacy asset and where the converted system expects to find it.</summary>
public sealed record TroyAssetMapping(string SourcePath, string TargetPath, bool NeedsTexTranscode);

public sealed record TroyConversionResult(
    byte[] BinBytes, uint SystemHash, string SystemName, string ParticlePath,
    IReadOnlyList<TroyEmitterNote> Emitters, IReadOnlyList<TroyAssetMapping> Assets,
    int ColorKeys, int UndecodedBodyBytes)
{
    /// <summary>One line the UI can show verbatim. Says what did NOT come across, because that is the
    /// part a user would otherwise discover by wondering why the effect looks wrong.</summary>
    public string Provenance =>
        $"{Emitters.Count} emitter(s), {Assets.Count} asset(s), "
        + $"{(ColorKeys > 0 ? $"{ColorKeys} colour key(s)" : "no colour curve")}. "
        + $"Timing and physics ({UndecodedBodyBytes:n0} undecoded bytes) are engine defaults, not the original values.";
}

/// <summary>
/// Builds a modern <c>VfxSystemDefinitionData</c> from a legacy <see cref="TroyBinFile"/>.
///
/// <para><b>Scope, stated plainly.</b> Only what the string block proves survives: emitter names, asset
/// paths, sound events and the colour-over-life curve. Every numeric parameter - rate, lifetime,
/// velocity, scale - is a modern default, because the region of the file holding those values is not
/// decoded (see <see cref="TroyBinFile"/> for the three hypotheses that were tested and killed). The
/// result is a real, editable particle system to start from, not a faithful reproduction, and
/// <see cref="TroyConversionResult.Provenance"/> exists so callers say that out loud.</para>
///
/// <para><b>The emitter/texture mapping is the one judgement call</b>, and it is made from measurement
/// rather than taste. Positional assignment alone is not defensible: only 476 of 1,168 files (40%) have
/// as many textures as emitter names. So a texture whose filename contains the emitter name wins first,
/// and only the leftovers are assigned in order - with <see cref="TroyEmitterNote.Source"/> recording
/// which rule fired for each emitter so a wrong guess is visible rather than silent.</para>
///
/// <para><b>Shapes are copied from shipped data</b>, dumped from a live
/// <c>VfxSystemDefinitionData</c> rather than inferred from ReyEngine's own reader (which is structural
/// and tolerant, so it would have accepted a shape the game rejects). Note in particular that
/// <c>complexEmitterDefinitionData</c> holds <b>Struct</b> elements - unlike material containers, which
/// need Embedded (M416).</para>
/// </summary>
public static class TroyBinConverter
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static readonly uint SystemClass = H("VfxSystemDefinitionData");
    private static readonly uint EmitterClass = H("VfxEmitterDefinitionData");

    /// <summary>Default emission rate and particle lifetime. These are NOT the original values - the
    /// original values live in the undecoded region - they are ordinary modern defaults chosen so a
    /// converted system is visible at all when it is dropped into a map.</summary>
    private const float DefaultRate = 10f;
    private const float DefaultLifetime = 1f;
    private const float DefaultScale = 50f;

    public static TroyConversionResult Convert(TroyBinFile troy, string systemName, string particlePath)
    {
        ArgumentNullException.ThrowIfNull(troy);
        if (string.IsNullOrWhiteSpace(systemName)) throw new ArgumentException("A system name is required.", nameof(systemName));
        particlePath = string.IsNullOrWhiteSpace(particlePath) ? systemName : particlePath;

        var textures = troy.TexturePaths.ToList();
        var names = troy.EmitterNames.Count > 0 ? troy.EmitterNames.ToList() : new List<string> { "emitter" };
        var (notes, assetsUsed) = MapTextures(names, textures);

        var emitters = new List<BinTreeProperty>(names.Count);
        foreach (var note in notes)
            emitters.Add(BuildEmitter(note, troy.ColorKeys));

        uint systemHash = H(particlePath);
        var system = new BinTreeObject(systemHash, SystemClass, new BinTreeProperty[]
        {
            new BinTreeString(H("particleName"), systemName),
            new BinTreeString(H("particlePath"), particlePath),
            new BinTreeContainer(H("complexEmitterDefinitionData"), BinPropertyType.Struct, emitters),
        });

        using var ms = new MemoryStream();
        new BinTree(new[] { system }, Array.Empty<string>()).Write(ms);

        // meshes come across as staged assets even though nothing binds them yet: dropping them would
        // lose the only record that the original effect used them
        var assets = assetsUsed.Concat(troy.MeshPaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(ToMapping).ToArray();

        return new TroyConversionResult(ms.ToArray(), systemHash, systemName, particlePath,
            notes, assets, troy.ColorKeys.Count, troy.UndecodedBodyBytes);
    }

    /// <summary>Name-match first, leftovers in order. Returns the notes plus every texture actually
    /// referenced, so unassigned textures are not staged for nothing.</summary>
    private static (IReadOnlyList<TroyEmitterNote> Notes, IReadOnlyList<string> Used) MapTextures(
        List<string> names, List<string> textures)
    {
        var notes = new TroyEmitterNote?[names.Count];
        var claimed = new bool[textures.Count];
        var used = new List<string>();

        for (int i = 0; i < names.Count; i++)
        {
            string name = names[i];
            if (name.Length < 3) continue;      // "w", "V" style stubs match everything
            for (int t = 0; t < textures.Count; t++)
            {
                if (claimed[t]) continue;
                string file = Path.GetFileNameWithoutExtension(textures[t]);
                if (file.Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    notes[i] = new TroyEmitterNote(name, textures[t], TroyTextureSource.NameMatch);
                    claimed[t] = true;
                    used.Add(textures[t]);
                    break;
                }
            }
        }

        int next = 0;
        for (int i = 0; i < names.Count; i++)
        {
            if (notes[i] is not null) continue;
            while (next < textures.Count && claimed[next]) next++;
            if (next < textures.Count)
            {
                notes[i] = new TroyEmitterNote(names[i], textures[next], TroyTextureSource.Positional);
                claimed[next] = true;
                used.Add(textures[next]);
            }
            else notes[i] = new TroyEmitterNote(names[i], null, TroyTextureSource.None);
        }

        // a texture nothing claimed is still part of the effect; keep it so the user can wire it up
        for (int t = 0; t < textures.Count; t++)
            if (!claimed[t]) used.Add(textures[t]);

        return (notes.Select(n => n!).ToArray(), used);
    }

    private static BinTreeProperty BuildEmitter(TroyEmitterNote note, IReadOnlyList<(float Time, Vector4 Color)> colorKeys)
    {
        var props = new List<BinTreeProperty>
        {
            new BinTreeString(H("emitterName"), note.EmitterName),
            ValueFloat("rate", DefaultRate),
            ValueFloat("particleLifetime", DefaultLifetime),
            new BinTreeEmbedded(H("birthScale0"), H("ValueVector3"), new BinTreeProperty[]
            {
                new BinTreeVector3(H("constantValue"), new Vector3(DefaultScale, DefaultScale, 0f)),
            }),
            // the billboard quad every legacy sprite emitter used
            new BinTreeStruct(H("primitive"), H("VfxPrimitiveArbitraryQuad"), Array.Empty<BinTreeProperty>()),
        };

        if (!string.IsNullOrWhiteSpace(note.TexturePath))
            props.Add(new BinTreeString(H("texture"), ToTargetPath(note.TexturePath!)));

        if (colorKeys.Count >= 2)
            props.Add(new BinTreeEmbedded(H("Color"), H("ValueColor"), new BinTreeProperty[]
            {
                new BinTreeStruct(H("dynamics"), H("VfxAnimatedColorVariableData"), new BinTreeProperty[]
                {
                    new BinTreeContainer(H("times"), BinPropertyType.F32,
                        colorKeys.Select(k => (BinTreeProperty)new BinTreeF32(0, k.Time)).ToArray()),
                    new BinTreeContainer(H("values"), BinPropertyType.Vector4,
                        colorKeys.Select(k => (BinTreeProperty)new BinTreeVector4(0, k.Color)).ToArray()),
                }),
            }));
        else
            props.Add(new BinTreeEmbedded(H("birthColor"), H("ValueColor"), new BinTreeProperty[]
            {
                new BinTreeVector4(H("constantValue"),
                    colorKeys.Count == 1 ? colorKeys[0].Color : Vector4.One),
            }));

        return new BinTreeStruct(0, EmitterClass, props);
    }

    private static BinTreeProperty ValueFloat(string field, float value) =>
        new BinTreeEmbedded(H(field), H("ValueFloat"), new BinTreeProperty[]
        {
            new BinTreeF32(H("constantValue"), value),
        });

    /// <summary>Legacy effects reference <c>DATA/Particles/x.dds</c>; shipped modern systems reference
    /// <c>.tex</c> 2,381,029 times against 70 <c>.dds</c>, so the converted system points at a .tex and
    /// the caller transcodes. The directory is kept as authored - WAD paths hash lowercased, so the
    /// staged file resolves without inventing an ASSETS layout.</summary>
    public static string ToTargetPath(string legacyPath)
    {
        string path = legacyPath.Replace('\\', '/').Trim();
        return path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)
            ? path[..^4] + ".tex"
            : path;
    }

    private static TroyAssetMapping ToMapping(string source)
    {
        string target = ToTargetPath(source);
        return new TroyAssetMapping(source, target,
            !target.Equals(source, StringComparison.OrdinalIgnoreCase));
    }
}
