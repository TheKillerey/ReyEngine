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

/// <summary><paramref name="MeshPath"/> non-null makes this emitter a mesh particle instead of a
/// billboard quad. It is only set where there is positive evidence - see
/// <see cref="TroyBinConverter"/> - because binding the wrong emitter is two errors, not one: the
/// billboard becomes a mesh AND the real mesh emitter stays flat.</summary>
public sealed record TroyEmitterNote(string EmitterName, string? TexturePath, TroyTextureSource Source,
    string? MeshPath = null);

/// <summary>A legacy asset and where the converted system expects to find it.</summary>
public sealed record TroyAssetMapping(string SourcePath, string TargetPath, bool NeedsTexTranscode);

public sealed record TroyConversionResult(
    byte[] BinBytes, uint SystemHash, string SystemName, string ParticlePath,
    IReadOnlyList<TroyEmitterNote> Emitters, IReadOnlyList<TroyAssetMapping> Assets,
    int ColorKeys, int UndecodedBodyBytes, IReadOnlyList<string> UnboundMeshes)
{
    /// <summary>One line the UI can show verbatim. Says what did NOT come across, because that is the
    /// part a user would otherwise discover by wondering why the effect looks wrong.</summary>
    public string Provenance =>
        $"{Emitters.Count} emitter(s), {Assets.Count} asset(s), "
        + $"{Emitters.Count(e => e.MeshPath is not null)} mesh emitter(s), "
        + $"{(ColorKeys > 0 ? $"{ColorKeys} colour key(s)" : "no colour curve")}. "
        + $"Timing and physics ({UndecodedBodyBytes:n0} undecoded bytes) are engine defaults, not the original values."
        + (UnboundMeshes.Count > 0
            ? $" {UnboundMeshes.Count} mesh(es) could not be matched to an emitter and are staged but unbound."
            : "");
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

        // The ramp is the second half of the legacy shader's multiply. Which emitter each ramp belonged
        // to is not recoverable (that lives in the undecoded body), so a file's single ramp goes to every
        // emitter and a file with several is left alone rather than guessed at.
        string? ramp = troy.ColorRampPaths.Count() == 1 ? troy.ColorRampPaths.Single() : null;

        notes = BindMeshes(notes, troy.MeshPaths.ToList(), out var unboundMeshes);

        var emitters = new List<BinTreeProperty>(names.Count);
        foreach (var note in notes)
            emitters.Add(BuildEmitter(note, troy.ColorKeys, ramp, troy.SkeletonPath));

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
        // the .skl belongs with a skinned .skn or the mesh primitive dangles
        var assets = assetsUsed.Concat(troy.ColorRampPaths).Concat(troy.MeshPaths)
            .Concat(troy.SkeletonPath is { } skl ? new[] { skl } : Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(ToMapping).ToArray();

        return new TroyConversionResult(ms.ToArray(), systemHash, systemName, particlePath,
            notes, assets, troy.ColorKeys.Count, troy.UndecodedBodyBytes, unboundMeshes);
    }

    /// <summary>
    /// Decide which emitters are mesh particles rather than billboards.
    ///
    /// <para>Only where there is positive evidence, because most files cannot be resolved: of the 374
    /// files referencing a mesh, the common shape is ONE mesh and several emitters (42 files have 4
    /// emitters and 1 mesh, 32 have 3 and 1, and so on), and which emitter owned it lives in the
    /// undecoded body. Binding the wrong one costs two errors - a billboard wrongly becomes a mesh and
    /// the real mesh emitter stays flat - so an unresolvable mesh is reported instead of assigned.</para>
    ///
    /// <para>Two rules fire, both measured: the emitter name appearing in the mesh filename (197
    /// agreements across the corpus), and a file with a single emitter (38 are unambiguously one
    /// emitter and one mesh).</para>
    /// </summary>
    private static IReadOnlyList<TroyEmitterNote> BindMeshes(
        IReadOnlyList<TroyEmitterNote> notes, List<string> meshes, out IReadOnlyList<string> unbound)
    {
        unbound = Array.Empty<string>();
        if (meshes.Count == 0) return notes;

        var result = notes.ToArray();
        var claimed = new bool[meshes.Count];

        for (int i = 0; i < result.Length; i++)
        {
            string name = result[i].EmitterName;
            if (name.Length < 3) continue;
            for (int m = 0; m < meshes.Count; m++)
            {
                if (claimed[m]) continue;
                if (!Path.GetFileNameWithoutExtension(meshes[m]).Contains(name, StringComparison.OrdinalIgnoreCase))
                    continue;
                result[i] = result[i] with { MeshPath = meshes[m] };
                claimed[m] = true;
                break;
            }
        }

        // a single emitter owns the file's mesh by elimination, not by guess
        if (result.Length == 1 && result[0].MeshPath is null)
        {
            result[0] = result[0] with { MeshPath = meshes[0] };
            claimed[0] = true;
        }

        unbound = meshes.Where((_, m) => !claimed[m]).ToArray();
        return result;
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

    /// <summary>
    /// The mesh primitive, copied from a shipped system rather than inferred:
    /// <c>primitive</c> is a Struct of class <c>VfxPrimitiveMesh</c> holding <c>mMesh</c> as an
    /// EMBEDDED <c>VfxMeshDefinitionData</c>. A simple mesh names <c>mSimpleMeshName</c> (.scb/.sco); a
    /// skinned one names <c>mMeshName</c> (.skn) plus <c>mMeshSkeletonName</c> (.skl).
    /// </summary>
    private static BinTreeProperty MeshPrimitive(string meshPath, string? skeletonPath)
    {
        bool skinned = meshPath.EndsWith(".skn", StringComparison.OrdinalIgnoreCase);
        var mesh = new List<BinTreeProperty>();
        if (skinned)
        {
            mesh.Add(new BinTreeString(H("mMeshName"), ToTargetPath(meshPath)));
            if (!string.IsNullOrWhiteSpace(skeletonPath))
                mesh.Add(new BinTreeString(H("mMeshSkeletonName"), ToTargetPath(skeletonPath!)));
        }
        else mesh.Add(new BinTreeString(H("mSimpleMeshName"), ToTargetPath(meshPath)));

        return new BinTreeStruct(H("primitive"), H("VfxPrimitiveMesh"), new BinTreeProperty[]
        {
            new BinTreeEmbedded(H("mMesh"), H("VfxMeshDefinitionData"), mesh),
        });
    }

    private static BinTreeProperty BuildEmitter(TroyEmitterNote note,
        IReadOnlyList<(float Time, Vector4 Color)> colorKeys, string? colorRamp, string? skeletonPath)
    {
        var props = new List<BinTreeProperty>
        {
            new BinTreeString(H("emitterName"), note.EmitterName),
            ValueFloat("rate", DefaultRate),
            ValueFloat("particleLifetime", DefaultLifetime),
            // STATED ASSUMPTION, not a recovered value: legacy sprites are additive glows on black, and
            // M117 established 1/3/4/5 as the additive family. The original per-emitter blend lives in
            // the undecoded body. Written explicitly so the result does not depend on whatever the game
            // defaults an absent blendMode to.
            new BinTreeU8(H("blendMode"), 1),
            new BinTreeEmbedded(H("birthScale0"), H("ValueVector3"), new BinTreeProperty[]
            {
                new BinTreeVector3(H("constantValue"), new Vector3(DefaultScale, DefaultScale, 0f)),
            }),
            // a mesh emitter renders its geometry; everything else is the billboard quad the legacy
            // sprite emitters used
            note.MeshPath is { } mesh
                ? MeshPrimitive(mesh, skeletonPath)
                : new BinTreeStruct(H("primitive"), H("VfxPrimitiveArbitraryQuad"), Array.Empty<BinTreeProperty>()),
        };

        if (!string.IsNullOrWhiteSpace(note.TexturePath))
            props.Add(new BinTreeString(H("texture"), ToTargetPath(note.TexturePath!)));
        if (!string.IsNullOrWhiteSpace(colorRamp))
            props.Add(new BinTreeString(H("particleColorTexture"), ToTargetPath(colorRamp!)));

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

    /// <summary>
    /// Where a legacy asset has to live for the modern engine to find it.
    ///
    /// <para><b>The root must change, and this is measured.</b> Every one of the 2,381,099 texture
    /// references in shipped particle systems - all 2,381,029 <c>.tex</c> and all 70 <c>.dds</c> - sits
    /// under <c>ASSETS/</c>. Not one uses <c>DATA/</c>. The first cut of this converter kept the authored
    /// <c>DATA/</c> path; the effect rendered as a white quad in game because the loader never resolved
    /// the texture and fell back to its default.</para>
    ///
    /// <para><b>And it must be namespaced.</b> Swapping <c>DATA/</c> for <c>ASSETS/</c> directly collides
    /// with 444 shipped assets - <c>ASSETS/Shared/Particles/Black.tex</c> among them - and staging over
    /// those would replace those textures for every effect in the game, not just the ported one.
    /// <c>ASSETS/Legacy/</c> collides with 0 of the 2,388 legacy assets.</para>
    /// </summary>
    public static string ToTargetPath(string legacyPath)
    {
        string path = legacyPath.Replace('\\', '/').Trim().TrimStart('/');
        if (path.StartsWith("DATA/", StringComparison.OrdinalIgnoreCase)) path = path["DATA/".Length..];
        if (path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
            path = path[..^4] + ".tex";
        return "ASSETS/Legacy/" + path;
    }

    /// <summary>Transcoding is keyed on the EXTENSION changing, not on the path changing. Every path
    /// changes now that assets are re-rooted under ASSETS/Legacy/, so comparing paths would send meshes
    /// through the DDS transcoder.</summary>
    private static TroyAssetMapping ToMapping(string source)
    {
        string target = ToTargetPath(source);
        bool transcode = target.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)
                         && !source.EndsWith(".tex", StringComparison.OrdinalIgnoreCase);
        return new TroyAssetMapping(source, target, transcode);
    }
}
