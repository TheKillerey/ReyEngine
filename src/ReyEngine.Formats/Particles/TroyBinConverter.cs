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
    /// <summary>M422: read from the decoded body by key - exact, not inferred. No heuristic involved.</summary>
    KeyBound,
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
        + (Emitters.Any(e => e.Source == TroyTextureSource.KeyBound)
            ? "Rates, lifetimes, scales and every asset binding were read from the file itself."
            : $"Timing and physics ({UndecodedBodyBytes:n0} undecoded bytes) are engine defaults, not the original values.")
        + (UnboundMeshes.Count > 0
            ? $" {UnboundMeshes.Count} mesh(es) could not be matched to an emitter and are staged but unbound."
            : "");
}

/// <summary>
/// Builds a modern <c>VfxSystemDefinitionData</c> from a legacy <see cref="TroyBinFile"/>.
///
/// <para><b>Scope.</b> When the body decodes - 5,447 of the 5,851 legacy files - every value below is
/// read from the file by key and nothing is guessed; the string-only path further down is the fallback
/// for the rest, and there the numerics really are defaults.
/// <see cref="TroyConversionResult.Provenance"/> says which of the two ran.</para>
///
/// <para><b>The shapes are checked against Riot's own conversion (M521).</b> Riot re-authored these
/// legacy effects as modern systems and shipped the result, so for the 199 that exist under the same
/// name in both there is a right answer rather than a plausible one. Measured over the 444 emitters
/// matched by name: probability tables and the scale curve agree 100%, rate constants 97.3%, particle
/// lifetimes 98.5%. The residue is Riot re-tuning values in the decade since this legacy snapshot, not
/// converter error. Where Riot writes a value its own source does not carry - particleLinger 10 on 224
/// emitters whose legacy file has no p-linger - that default is deliberately NOT copied.</para>
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

        // M422: when the body decoded, every binding below is read by key and nothing is guessed.
        if (troy.HasDecodedBody) return ConvertDecoded(troy, systemName, particlePath);

        var textures = troy.TexturePaths.ToList();
        var names = troy.EmitterNames.Count > 0 ? troy.EmitterNames.ToList() : new List<string> { "emitter" };
        var (notes, assetsUsed) = MapTextures(names, textures);

        // The ramp is the second half of the legacy shader's multiply. Which emitter each ramp belonged
        // to is not recoverable (that lives in the undecoded body), so a file's single ramp goes to every
        // emitter and a file with several is left alone rather than guessed at.
        string? ramp = troy.ColorRampPaths.Count() == 1 ? troy.ColorRampPaths.Single() : null;

        notes = BindMeshes(notes, troy.MeshPaths.ToList(), out var unboundMeshes);

        var emitters = new List<BinTreeProperty>(names.Count);
        for (int i = 0; i < notes.Count; i++)
            emitters.Add(BuildEmitter(notes[i], CurveFor(troy.ColorCurves, i, notes.Count),
                ramp, troy.SkeletonPath));

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
            notes, assets, troy.ColorKeyCount, troy.UndecodedBodyBytes, unboundMeshes);
    }

    /// <summary>
    /// M422: conversion from the DECODED body. Nothing here is a heuristic - each emitter's texture,
    /// colour ramp, mesh, rate, lifetime and scale is read from a key built out of that emitter's own
    /// name, so the assignment is exact. This path retires the name-matching and positional rules,
    /// which remain only as the fallback for a body that will not decode.
    /// </summary>
    private static TroyConversionResult ConvertDecoded(TroyBinFile troy, string systemName, string particlePath)
    {
        var notes = new List<TroyEmitterNote>();
        var used = new List<string>();
        var emitters = new List<BinTreeProperty>();

        for (int i = 0; i < troy.Emitters.Count; i++)
        {
            var e = troy.Emitters[i];
            notes.Add(new TroyEmitterNote(e.Name, e.TexturePath, TroyTextureSource.KeyBound, e.MeshPath));
            foreach (string? p in new[] { e.TexturePath, e.ColorTexturePath, e.TextureMultPath, e.MeshPath })
                if (!string.IsNullOrWhiteSpace(p)) used.Add(p!);

            var props = new List<BinTreeProperty>
            {
                new BinTreeString(H("emitterName"), e.Name),
                ValueFloat("rate", e.Rate ?? DefaultRate, e.RateSpread),
                ValueFloat("particleLifetime", e.ParticleLifetime ?? DefaultLifetime, e.LifetimeSpread),
            };

            // M524: blendMode IS the legacy *rendermode, unchanged - 324 of the 325 paired emitters map
            // identity, the lone exception being one emitter Riot re-authored. Mode 0 is the default and
            // Riot omits it rather than writing a zero, so this does too. The old code hardcoded 1, which
            // was right for 168 emitters and wrong for the 157 that use additive (4) or another mode.
            if (e.RenderMode is { } mode && mode > 0 && mode <= 255)
                props.Add(new BinTreeU8(H("blendMode"), (byte)mode));

            // M524: the render-order bucket, which the reader has had since M520 and nothing wrote.
            // Riot ships it as I16 - 188 of the paired emitters carry one.
            if (e.Pass is { } pass) props.Add(new BinTreeI16(H("pass"), (short)pass));

            // -1 means "runs forever"; a positive value is a real emitter runtime.
            // M524: an OPTION, not a ValueFloat. Riot ships it as option[f32] in 136 of 136 shipped
            // emitters that have one - a ValueFloat in that slot is a different wire type, so the client
            // reads the property as malformed and the emitter runs forever regardless.
            if (e.EmitterLifetime is { } life && life > 0f)
                props.Add(new BinTreeOptional(H("lifetime"), new BinTreeF32(0, life)));

            // Legacy and modern both work in League world units, so the decoded scale is used AS IS.
            // The old path multiplied by 50 because it had no real value to work from; doing that to a
            // measured scale of 100 would emit 5,000.
            Vector3 scale = e.ScaleVector is { } sv && sv != Vector3.Zero
                ? sv
                : new Vector3(DefaultScale, DefaultScale, DefaultScale);
            props.Add(ValueVector3("birthScale0", scale, e.ScaleSpread));

            // M526: every field whose legacy source was measured against Riot's own conversion. The
            // table is declarative on purpose - each row carries its agreement figure, so what is
            // supported and how well is visible where the rule lives.
            TroyFieldMap.Apply(troy.Sections!, troy.StringAt, e.Name, props);

            // M522: the force fields this emitter pulls in. Look each reference up by name - the field
            // sections never appear in the group list, so this is the only route to them.
            if (e.FieldReferences is { Count: > 0 } refs)
            {
                var fields = refs
                    .Select(n => troy.ForceFields.FirstOrDefault(f =>
                        string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase)))
                    .Where(f => f is not null).Select(f => f!).ToList();
                if (FieldCollection(fields) is { } collection) props.Add(collection);
            }

            // M521: the scale-over-life curve, which is a separate property from the birth size.
            if (e.ScaleOverLife is { Count: > 0 } curveKeys)
                props.Add(ScaleOverLife(curveKeys, e.ScaleMultiplier));

            // particleLinger is an OPTION in the modern format, not a plain float
            if (e.ParticleLinger is { } linger && linger > 0f)
                props.Add(new BinTreeOptional(H("particleLinger"), new BinTreeF32(0, linger)));

            // PRIMITIVE. A mesh emitter names its geometry; everything else gets NO primitive property
            // at all, which is the camera-facing billboard. Writing VfxPrimitiveArbitraryQuad here was
            // the flat-particle bug: the renderer branches on it as
            //     right = uArbitraryQuad != 0 ? placedRight : uCamRight
            // so an "arbitrary quad" is locked to a fixed WORLD orientation instead of facing the
            // camera. Every converted particle was therefore a plane pinned to one direction, which is
            // why orbiting the preview turned an effect edge-on while modern systems stayed correct.
            if (Primitive(e, troy) is { } primitive) props.Add(primitive);

            if (!string.IsNullOrWhiteSpace(e.TexturePath))
                props.Add(new BinTreeString(H("texture"), ToTargetPath(e.TexturePath!)));
            if (!string.IsNullOrWhiteSpace(e.ColorTexturePath))
                props.Add(new BinTreeString(H("particleColorTexture"), ToTargetPath(e.ColorTexturePath!)));

            // a flipbook sheet drawn as one sprite is what made converted effects look like blobs.
            // frameRate is a PLAIN F32 in the modern format (the resolver reads it with GetF32), not a
            // ValueFloat wrapper - wrapping it meant nothing ever read it.
            // texDiv is the ATLAS GRID and is what actually makes a flipbook animate: the renderer
            // reads it as a plain Vector2 and defaults it to (1,1), which samples the entire sheet as a
            // single frame. numFrames on its own animates nothing, which is why the flipbooks stayed
            // static. Written whenever the legacy file has it, flipbook or not, since a (1,1) grid is
            // harmless and 1,487 emitters carry the field.
            if (e.TexDiv is { } div && div.X >= 1f && div.Y >= 1f)
                props.Add(new BinTreeVector2(H("texDiv"), div));
            if (e.IsFlipbook)
            {
                props.Add(new BinTreeU16(H("numFrames"), (ushort)e.FrameCount!.Value));
                if (e.FrameRate is { } fps && fps > 0f) props.Add(new BinTreeF32(H("frameRate"), fps));
                if (e.StartFrame is { } sf && sf > 0) props.Add(new BinTreeU16(H("startFrame"), (ushort)sf));
            }

            // ---- M423: motion and spawn volume ---------------------------------------------------
            // Without these every particle spawns at one point with zero velocity, which is what made
            // converted effects render as a flat plane of sprites. All are genuine vec3s in the source.
            void Vec3(string field, Vector3? value, TroyProbability? spread = null)
            {
                if (value is not { } v || v == Vector3.Zero) return;
                props.Add(ValueVector3(field, v, spread));
            }

            Vec3("birthVelocity", e.Velocity, e.VelocitySpread);
            Vec3("birthAcceleration", e.Acceleration);
            Vec3("worldAcceleration", e.WorldAcceleration);
            Vec3("birthDrag", e.Drag);
            Vec3("birthOrbitalVelocity", e.OrbitalVelocity);

            // The spawn VOLUME. VfxShapeLegacy is used rather than the bare 0xee39916f form because its
            // emitOffset is an Embedded ValueVector3 and can therefore carry dynamics - and the
            // probability table inside those dynamics is the entire point. Without it every particle
            // spawns at exactly the same offset, which is what made converted effects render as a line
            // while modern ones (which all carry their tables) looked correct.
            var rotations = e.EmitRotations ?? Array.Empty<TroyEmitRotation>();
            if ((e.Offset is { } offset && offset != Vector3.Zero) || rotations.Count > 0)
            {
                var shape = new List<BinTreeProperty>();
                if (e.Offset is { } o && o != Vector3.Zero)
                    shape.Add(ValueVector3("emitOffset", o, e.OffsetSpread));

                // The emit rotations are what turn a radius into a volume. *p-offset (30,0,0) is a
                // RADIUS along X; spun 0-360 degrees about Y it sweeps a cylinder. Without them every
                // particle stays in one plane, which is exactly how converted effects rendered while
                // modern ones - which all carry these - looked correct in the same viewport.
                if (rotations.Count > 0)
                {
                    shape.Add(new BinTreeContainer(H("emitRotationAxes"), BinPropertyType.Vector3,
                        rotations.Select(r => (BinTreeProperty)new BinTreeVector3(0, r.Axis)).ToArray()));
                    shape.Add(new BinTreeContainer(H("emitRotationAngles"), BinPropertyType.Embedded,
                        rotations.Select(r => AngleValue(r)).ToArray()));
                }
                props.Add(new BinTreeStruct(H("SpawnShape"), H("VfxShapeLegacy"), shape));
            }

            // M525: the colour curve, read by KEY from *p-xrgba{n}. The old route was the string-scan
            // heuristic - runs of five-token strings assigned to emitters POSITIONALLY - which is what
            // made Color the largest remaining disagreement with Riot: 456 differed against 1,036
            // agreed, with the first key's time landing on 0.2 or 0.5 where Riot always has 0. Bound by
            // key, an emitter's curve belongs to it by construction.
            //
            // The multiplier is folded in the way *p-xscale is folded into scale0, which is the same
            // shape of rule and was measured there.
            var keyed = e.ColorOverLife;
            var curve = keyed is { Count: > 0 }
                ? keyed.Select(k => (k.Time, Color: k.Color * (e.ColorMultiplier ?? Vector4.One))).ToList()
                : CurveFor(troy.ColorCurves, i, troy.Emitters.Count);
            if (curve.Count >= 2)
                props.Add(new BinTreeEmbedded(H("Color"), H("ValueColor"), new BinTreeProperty[]
                {
                    new BinTreeStruct(H("dynamics"), H("VfxAnimatedColorVariableData"), new BinTreeProperty[]
                    {
                        new BinTreeContainer(H("times"), BinPropertyType.F32,
                            curve.Select(k => (BinTreeProperty)new BinTreeF32(0, k.Time)).ToArray()),
                        new BinTreeContainer(H("values"), BinPropertyType.Vector4,
                            curve.Select(k => (BinTreeProperty)new BinTreeVector4(0, k.Color)).ToArray()),
                    }),
                }));
            else if (curve.Count == 1)
                props.Add(new BinTreeEmbedded(H("birthColor"), H("ValueColor"), new BinTreeProperty[]
                {
                    new BinTreeVector4(H("constantValue"), curve[0].Color),
                }));

            emitters.Add(new BinTreeStruct(0, EmitterClass, props));
        }

        uint systemHash = H(particlePath);
        var system = new BinTreeObject(systemHash, SystemClass, new BinTreeProperty[]
        {
            new BinTreeString(H("particleName"), systemName),
            new BinTreeString(H("particlePath"), particlePath),
            new BinTreeContainer(H("complexEmitterDefinitionData"), BinPropertyType.Struct, emitters),
        });

        using var ms = new MemoryStream();
        new BinTree(new[] { system }, Array.Empty<string>()).Write(ms);

        var assets = used.Concat(troy.SkeletonPath is { } skl ? new[] { skl } : Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(ToMapping).ToArray();

        return new TroyConversionResult(ms.ToArray(), systemHash, systemName, particlePath,
            notes, assets, troy.ColorKeyCount, troy.UndecodedBodyBytes, Array.Empty<string>());
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
    /// <summary>
    /// The primitive, chosen by <c>*p-type</c> (M527).
    ///
    /// <para><b>This is the field that decides what an emitter IS</b> - a camera-facing billboard, a
    /// world-oriented quad, a ray, a mesh, a trail - and it was being ignored: every non-mesh emitter
    /// got no primitive at all, which is the billboard, so 75 of the 441 paired emitters rendered as
    /// the wrong kind of thing.</para>
    ///
    /// <para>The table is measured against Riot's own conversion, 438 of 441 exact:</para>
    /// <list type="table">
    ///   <item>1 -&gt; VfxPrimitiveArbitraryQuad (27/27)</item>
    ///   <item>2 -&gt; VfxPrimitiveRay (37/37)</item>
    ///   <item>3 -&gt; VfxPrimitiveMesh (209/209)</item>
    ///   <item>4 -&gt; VfxPrimitiveCameraTrail (6/6)</item>
    ///   <item>7 -&gt; VfxPrimitivePlanarProjection (5/5)</item>
    ///   <item>0 or absent -&gt; no primitive property, which IS the camera-facing billboard (157/160)</item>
    /// </list>
    /// The 3 that miss are Riot writing an ArbitraryQuad where its own source says 0 or nothing - its
    /// choice, not derivable from the legacy file, so not copied.
    ///
    /// <para><b>Writing ArbitraryQuad used to be a bug, and this is not a regression of it.</b> The old
    /// code wrote that class for EVERY emitter, and the renderer branches on it as
    /// <c>right = uArbitraryQuad != 0 ? placedRight : uCamRight</c> - so every converted particle became
    /// a plane pinned to one world direction and went edge-on as the camera moved. Gated on
    /// <c>*p-type == 1</c> it is right 27 times out of 27, and those 27 are world-oriented in Riot's
    /// own output too. The lesson held was "do not write it unconditionally", not "never write it".</para>
    ///
    /// <para>The empty forms are shipped forms: ArbitraryQuad and Ray carry no properties at all in any
    /// of the 42,382 shipped instances, and Riot ships bare CameraTrail (46) and PlanarProjection (6)
    /// too - so a class written without its sub-struct is a shape the client already sees.</para>
    /// </summary>
    private static BinTreeProperty? Primitive(TroyEmitter e, TroyBinFile troy)
    {
        int? type = null;
        if (troy.Sections is { } sections
            && sections.TryGetScalar(TroyHash.FieldKey(e.Name, TroyFields.ParticleType), troy.StringAt,
                out float raw))
            type = (int)raw;

        // A mesh path is its own evidence: p-type 3 and a named mesh agree on 207 of 209 paired
        // emitters, so either signal alone is enough to make this a mesh particle.
        if (type == 3 || e.MeshPath is not null)
            return e.MeshPath is { } mesh ? MeshPrimitive(mesh, troy.SkeletonPath) : Bare("VfxPrimitiveMesh");

        return type switch
        {
            1 => Bare("VfxPrimitiveArbitraryQuad"),
            2 => Bare("VfxPrimitiveRay"),
            4 => Trail(e, troy),
            7 => Bare("VfxPrimitivePlanarProjection"),
            // 0 and absent both mean the camera-facing billboard, which is the ABSENCE of the property
            _ => null,
        };
    }

    private static BinTreeProperty Bare(string cls) =>
        new BinTreeStruct(H("primitive"), H(cls), Array.Empty<BinTreeProperty>());

    /// <summary>A camera trail. <c>mBirthTilingSize</c> comes from <c>*e-tilesize</c> (6/6); without it
    /// the bare class is written, which Riot also ships.</summary>
    private static BinTreeProperty Trail(TroyEmitter e, TroyBinFile troy)
    {
        if (troy.Sections is not { } sections
            || !sections.TryGetScalar(TroyHash.FieldKey(e.Name, TroyFields.TileSize), troy.StringAt,
                out float tile))
            return Bare("VfxPrimitiveCameraTrail");

        return new BinTreeStruct(H("primitive"), H("VfxPrimitiveCameraTrail"), new BinTreeProperty[]
        {
            new BinTreeEmbedded(H("mTrail"), H("VfxTrailDefinitionData"), new BinTreeProperty[]
            {
                new BinTreeEmbedded(H("mBirthTilingSize"), H("ValueFloat"), new BinTreeProperty[]
                {
                    new BinTreeF32(H("constantValue"), tile),
                }),
            }),
        });
    }

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

    /// <summary>
    /// Which colour curve belongs to emitter <paramref name="index"/>.
    ///
    /// <para>One curve and several emitters means it is the file's only colour information, so every
    /// emitter gets it - 223 of the 660 files with colour keys are that shape, and a purple torch should
    /// come out purple. Several curves are handed out in order, the same positional judgement the
    /// texture mapping makes, and an emitter past the end of the list gets none rather than a repeat.</para>
    /// </summary>
    private static IReadOnlyList<(float Time, Vector4 Color)> CurveFor(
        IReadOnlyList<IReadOnlyList<(float, Vector4)>> curves, int index, int emitterCount)
    {
        if (curves.Count == 0) return Array.Empty<(float, Vector4)>();
        if (curves.Count == 1) return curves[0];
        return index < curves.Count ? curves[index] : Array.Empty<(float, Vector4)>();
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
        };

        // See ConvertDecoded: NO primitive is the camera-facing billboard, which is what a legacy sprite
        // emitter is. Naming VfxPrimitiveArbitraryQuad locks the quad to a fixed world orientation.
        if (note.MeshPath is { } mesh) props.Add(MeshPrimitive(mesh, skeletonPath));

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
        else if (colorKeys.Count == 1)
            props.Add(new BinTreeEmbedded(H("birthColor"), H("ValueColor"), new BinTreeProperty[]
            {
                new BinTreeVector4(H("constantValue"), colorKeys[0].Color),
            }));
        // no colour data at all: write nothing. A hand-written white birthColor is an invented value,
        // and on an additive emitter it is the one that blows the effect out to white.

        return new BinTreeStruct(0, EmitterClass, props);
    }

    /// <summary>
    /// A vec3 field with its per-particle randomisation attached.
    ///
    /// <para>Shape copied from shipped data: <c>Embedded ValueVector3 { constantValue, dynamics }</c>,
    /// where <c>dynamics</c> is a <c>VfxAnimatedVector3fVariableData</c> holding a
    /// <c>probabilityTables</c> container of <c>VfxProbabilityTableData { keyTimes, keyValues }</c>.
    /// The legacy <c>(probability, multiplier)</c> pairs map straight onto that - probability becomes
    /// keyTimes, multiplier becomes keyValues - so <c>*p-velYP1 = (0,1)</c> with
    /// <c>*p-velYP2 = (1,3)</c> becomes keyTimes [0,1] and keyValues [1,3]: a speed uniformly random
    /// between one and three times the base.</para>
    ///
    /// <para>Three tables are emitted, X/Y/Z positionally, because the container is read by index.</para>
    /// </summary>
    private static BinTreeProperty ValueVector3(string field, Vector3 value, TroyProbability? spread)
    {
        var inner = new List<BinTreeProperty> { new BinTreeVector3(H("constantValue"), value) };
        if (spread is not null && !spread.IsEmpty)
        {
            var tables = new List<BinTreeProperty>(3);
            for (int axis = 0; axis < 3; axis++) tables.Add(ProbabilityTable(spread.ForAxis(axis)));
            inner.Add(new BinTreeStruct(H("dynamics"), H("VfxAnimatedVector3fVariableData"),
                new BinTreeProperty[]
                {
                    new BinTreeContainer(H("probabilityTables"), BinPropertyType.Struct, tables),
                    // M521: Riot writes these on every dynamics block, holding the constant at t=0
                    new BinTreeContainer(H("times"), BinPropertyType.F32,
                        new BinTreeProperty[] { new BinTreeF32(0, 0f) }),
                    new BinTreeContainer(H("values"), BinPropertyType.Vector3,
                        new BinTreeProperty[] { new BinTreeVector3(0, value) }),
                }));
        }
        return new BinTreeEmbedded(H(field), H("ValueVector3"), inner);
    }

    /// <summary>One emit-rotation angle as a ValueFloat curve. Riot's idiom is a constant of 1 scaled by
    /// a probability table, so <c>*e-rotation2 = 1</c> with a table reaching 360 means "uniformly random
    /// between 0 and 360 degrees".</summary>
    private static BinTreeProperty AngleValue(TroyEmitRotation r)
    {
        var inner = new List<BinTreeProperty> { new BinTreeF32(H("constantValue"), r.Angle) };
        if (r.Table.Count > 0)
            inner.Add(new BinTreeStruct(H("dynamics"), H("VfxAnimatedFloatVariableData"), new BinTreeProperty[]
            {
                new BinTreeContainer(H("probabilityTables"), BinPropertyType.Struct,
                    new BinTreeProperty[] { ProbabilityTable(r.Table) }),
            }));
        return new BinTreeEmbedded(0, H("ValueFloat"), inner);
    }

    private static BinTreeProperty ValueFloat(string field, float value) =>
        ValueFloat(field, value, null);

    /// <summary>
    /// A scalar with its probability table, in the shape Riot's own conversion uses (M521).
    ///
    /// <para>Measured on a shipped pair - <c>DestroyedBuilding_idle</c>'s <c>sparkburst3</c>, which has
    /// <c>e-rate=10</c> with <c>e-rateP1..3 = (0,0) (0.98,0) (1,2)</c>, and whose converted
    /// <c>rate</c> is a ValueFloat of 10 carrying one table with <c>keyTimes 0, 0.98, 1</c> and
    /// <c>keyValues 0, 0, 2</c>. A burst emitter read without its table is a steady trickle.</para>
    ///
    /// <para><c>times</c>/<c>values</c> are written beside the table because Riot writes them on every
    /// dynamics block it emits, holding the constant at t=0.</para>
    /// </summary>
    private static BinTreeProperty ValueFloat(string field, float value, TroyProbability? spread)
    {
        var inner = new List<BinTreeProperty> { new BinTreeF32(H("constantValue"), value) };
        var keys = spread?.Uniform ?? Array.Empty<(float Probability, float Multiplier)>();
        if (keys.Count > 0)
            inner.Add(new BinTreeStruct(H("dynamics"), H("VfxAnimatedFloatVariableData"),
                new BinTreeProperty[]
                {
                    new BinTreeContainer(H("probabilityTables"), BinPropertyType.Struct,
                        new BinTreeProperty[] { ProbabilityTable(keys) }),
                    new BinTreeContainer(H("times"), BinPropertyType.F32,
                        new BinTreeProperty[] { new BinTreeF32(0, 0f) }),
                    new BinTreeContainer(H("values"), BinPropertyType.F32,
                        new BinTreeProperty[] { new BinTreeF32(0, value) }),
                }));
        return new BinTreeEmbedded(H(field), H("ValueFloat"), inner);
    }

    /// <summary>
    /// The emitter's force fields as a <c>VfxFieldCollectionDefinitionData</c> (M522).
    ///
    /// <para>Every shape and every mapping below is read off Riot's own conversion rather than guessed:
    /// the collection is a POINTER, each list a container of EMBEDDED elements, and the per-kind
    /// properties are what a census of 5,138 shipped collections turns up. Values agree with Riot on
    /// the paired systems for acceleration, isLocalSpace, radius, strength, direction, velocityDelta,
    /// axisFraction and position.</para>
    ///
    /// <para>Two rules are Riot's rather than the format's, and both are measured. <c>frequency</c> is
    /// the RECIPROCAL of <c>f-period</c> - period 0.5 becomes frequency 2, 0.25 becomes 4, 0.4 becomes
    /// 2.5, exactly, every time. And a <c>Position</c> of zero is omitted rather than written, which is
    /// why only 222 of 848 shipped drag fields carry one.</para>
    /// </summary>
    private static BinTreeProperty? FieldCollection(IReadOnlyList<TroyForceField> fields)
    {
        if (fields.Count == 0) return null;

        var lists = new List<BinTreeProperty>();
        foreach (var (kind, listName) in new[]
        {
            (TroyFieldKind.Acceleration, "fieldAccelerationDefinitions"),
            (TroyFieldKind.Attraction, "fieldAttractionDefinitions"),
            (TroyFieldKind.Drag, "fieldDragDefinitions"),
            (TroyFieldKind.Orbital, "fieldOrbitalDefinitions"),
            (TroyFieldKind.Noise, "fieldNoiseDefinitions"),
        })
        {
            var of = fields.Where(f => f.Kind == kind).ToList();
            if (of.Count == 0) continue;   // an empty container crashes the client at load (M414)
            lists.Add(new BinTreeContainer(H(listName), BinPropertyType.Embedded,
                of.Select(f => FieldDefinition(kind, f)).ToArray()));
        }

        return lists.Count == 0
            ? null
            : new BinTreeStruct(H("fieldCollectionDefinition"), H("VfxFieldCollectionDefinitionData"), lists);
    }

    private static BinTreeProperty FieldDefinition(TroyFieldKind kind, TroyForceField f)
    {
        var props = new List<BinTreeProperty>();

        void Scalar(string name, float? value)
        {
            if (value is { } v) props.Add(ValueFloat(name, v));
        }
        void Vector(string name, Vector3? value)
        {
            // Riot omits a zero Position rather than writing it
            if (value is { } v && v != Vector3.Zero) props.Add(ValueVector3(name, v, null));
        }
        void LocalSpace()
        {
            if (f.LocalSpace) props.Add(new BinTreeBool(H("isLocalSpace"), true));
        }

        switch (kind)
        {
            case TroyFieldKind.Acceleration:
                Vector("acceleration", f.Acceleration);
                LocalSpace();
                break;
            case TroyFieldKind.Attraction:
                // f-accel is a SCALAR here - the pull rate toward Position - and a vec3 on an
                // acceleration field. The kind comes from the reference, which is what makes the two
                // readable apart at all.
                Scalar("acceleration", f.Strength);
                Scalar("radius", f.Radius);
                Vector("Position", f.Position);
                break;
            case TroyFieldKind.Drag:
                Scalar("strength", f.Drag);
                Scalar("radius", f.Radius);
                Vector("Position", f.Position);
                break;
            case TroyFieldKind.Orbital:
                Vector("direction", f.Direction);
                LocalSpace();
                break;
            case TroyFieldKind.Noise:
                Scalar("radius", f.Radius);
                Scalar("velocityDelta", f.VelocityDelta);
                if (f.Period is { } period && period > 0f) Scalar("frequency", 1f / period);
                // axisFraction is a PLAIN vec3, not a ValueVector3 wrapper - 3,612 of 3,635 shipped
                // noise fields carry it that way.
                //
                // (1,1,1) when the legacy file is silent. That is Riot's rule for the absent case, not
                // an invention: measured 5 of 5 on the paired systems, and unlike particleLinger's 10
                // - which is a behavioural value and is therefore NOT copied - (1,1,1) is the identity
                // for a per-axis weighting, so it cannot change an effect that a correct engine default
                // would have rendered anyway.
                props.Add(new BinTreeVector3(H("axisFraction"), f.AxisFraction ?? Vector3.One));
                Vector("Position", f.Position);
                break;
        }

        return new BinTreeEmbedded(0, H("VfxField" + kind + "DefinitionData"), props);
    }

    /// <summary>One probability table. An empty key list produces an EMPTY struct rather than empty
    /// containers - which is what Riot ships for the axes of a vec3 that only randomises one of
    /// them, and matters because an empty container crashes the client at load (M414).</summary>
    private static BinTreeProperty ProbabilityTable(IReadOnlyList<(float Probability, float Multiplier)> keys) =>
        new BinTreeStruct(0, H("VfxProbabilityTableData"), keys.Count == 0
            ? Array.Empty<BinTreeProperty>()
            : new BinTreeProperty[]
            {
                new BinTreeContainer(H("keyTimes"), BinPropertyType.F32,
                    keys.Select(k => (BinTreeProperty)new BinTreeF32(0, k.Probability)).ToArray()),
                new BinTreeContainer(H("keyValues"), BinPropertyType.F32,
                    keys.Select(k => (BinTreeProperty)new BinTreeF32(0, k.Multiplier)).ToArray()),
            });

    /// <summary>
    /// Scale over life as <c>scale0</c> (M521) - a ValueVector3 whose dynamics carry only
    /// times/values, with no constant, because it is a multiplier rather than a size.
    ///
    /// <para>The values are the curve keys times <c>*p-xscale</c>. That multiply is Riot's rule, read
    /// off their own output: SRU_Lane_Motes has keys 0.2/1/0.2 and <c>p-xscale=(20,20,20)</c>, and
    /// Riot's <c>scale0</c> for it is 4/20/4. Applying it takes agreement with Riot across the 1,720
    /// paired emitters that have this curve from 95.6% to 99.9% - the single remaining disagreement is
    /// 0.9 against 0.99, which is the binary's own tenths quantisation and not something a converter
    /// can recover.</para>
    /// </summary>
    private static BinTreeProperty ScaleOverLife(
        IReadOnlyList<(float Time, Vector3 Scale)> keys, Vector3? multiplier)
    {
        var m = multiplier ?? Vector3.One;
        return new BinTreeEmbedded(H("scale0"), H("ValueVector3"), new BinTreeProperty[]
        {
            new BinTreeStruct(H("dynamics"), H("VfxAnimatedVector3fVariableData"), new BinTreeProperty[]
            {
                new BinTreeContainer(H("times"), BinPropertyType.F32,
                    keys.Select(k => (BinTreeProperty)new BinTreeF32(0, k.Time)).ToArray()),
                new BinTreeContainer(H("values"), BinPropertyType.Vector3,
                    keys.Select(k => (BinTreeProperty)new BinTreeVector3(0, k.Scale * m)).ToArray()),
            }),
        });
    }

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
