using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ReyEngine.Core.Decoding;
using ReyEngine.Formats.Shaders;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.App.Services;

/// <summary>
/// <para>M266: the single emitter -> <see cref="PreviewMaterial"/> recipe for Riot's
/// <c>particlesystem/quad_vs</c> + <c>quad_ps</c>.</para>
///
/// <para>This sequence - flags to defines to permutation to pipeline to textures to Params - has already
/// been the site of two divergence bugs on its own (M236's emitter index drift, M237's neutered alphaRef and
/// flat depthPushPull). Both were "the second copy did not learn what the first one did". There is now one
/// copy: the shader preview window and the map viewport call this, so a newly-understood emitter field is
/// added once.</para>
///
/// <para>In the App layer for the same reason <see cref="D3D11ParticlePlayback"/> is: it needs both the VFX
/// data model and the D3D11 renderer, and those two assemblies deliberately do not know about each
/// other.</para>
/// </summary>
public static class VfxD3D11EmitterPipeline
{
    public const string VsName = "assets/shaders/hlsl/particlesystem/quad_vs";
    public const string PsName = "assets/shaders/hlsl/particlesystem/quad_ps";

    /// <summary>M640: Riot's shaders for MESH primitives. mesh_vs takes POSITION0/NORMAL0/TEXCOORD0, a
    /// per-draw <c>mWorld</c>, the particle colour as <c>kColorFactor</c> and a <c>vParticleUVTransform</c>
    /// affine; mesh_ps carries the same stages as quad_ps (PALETTIZE_TEXTURES, ALPHA_EROSION,
    /// SOFT_PARTICLES, ALPHA_TEST, MULT_PASS, MASKED) plus REFLECTIVE. Read off the shipped bytecode.</summary>
    public const string MeshVsName = "assets/shaders/hlsl/particlesystem/mesh_vs";
    public const string MeshPsName = "assets/shaders/hlsl/particlesystem/mesh_ps";

    /// <summary>The two stage tables every emitter resolves its permutation against, and the names they
    /// were read under so the build loads and describes the blobs of whichever pair it was handed. Read
    /// once per playback, not once per emitter.</summary>
    public sealed record Tocs(ShaderStageToc Vs, ShaderStageToc Ps, string VsName = VsName, string PsName = PsName);

    public static Tocs? ReadTocs(ShaderCacheReader cache, out string? error) =>
        ReadTocs(cache, VsName, PsName, "particlesystem/quad_vs+quad_ps", out error);

    /// <summary>M640: the mesh pair. Null, with the reason, when the cache lacks either stage.</summary>
    public static Tocs? ReadMeshTocs(ShaderCacheReader cache, out string? error) =>
        ReadTocs(cache, MeshVsName, MeshPsName, "particlesystem/mesh_vs+mesh_ps", out error);

    private static Tocs? ReadTocs(ShaderCacheReader cache, string vsName, string psName, string label, out string? error)
    {
        error = null;
        var vs = cache.ReadToc(ShaderCacheReader.TocPathFor(vsName, DxbcStage.Vertex));
        var ps = cache.ReadToc(ShaderCacheReader.TocPathFor(psName, DxbcStage.Pixel));
        if (vs is null || ps is null)
        {
            error = label + " not in the shader cache";
            return null;
        }
        return new Tocs(vs, ps, vsName, psName);
    }

    /// <summary>
    /// <para>What a texture stage resolves to: the pool key, plus a way to GET the pixels.</para>
    ///
    /// <para><see cref="Open"/> is a delegate, not a decoded image, so the pool can be probed by key BEFORE
    /// anything is read or decoded - that ordering is the whole reason the preview window's repeat-play does
    /// not re-read the WAD, and losing it would be a silent cost regression rather than a visible bug. A null
    /// <see cref="Open"/> means the stage resolved to nothing and should get the shared soft-dot fallback;
    /// returning null from the delegate means the read or decode failed, which binds nothing and lets the
    /// renderer's stand-in through, exactly as the preview window has always behaved.</para>
    ///
    /// <para>Returning <c>null</c> from the callback itself means "this emitter does not author this stage" -
    /// no bind and no fallback.</para>
    /// </summary>
    public readonly record struct Sprite(string Key, Func<TextureImage?>? Open)
    {
        /// <summary>An unresolved sprite: the soft dot, under the one shared key. What the GL viewport
        /// substitutes, and what stops an unresolved emitter drawing as an opaque white card.</summary>
        public static Sprite Fallback => new(VfxPlaybackSim.SoftDotKey, null);

        /// <summary>A sprite the caller has already decoded - the map viewport's case, where the view-model
        /// resolved every emitter's texture before playback even started.</summary>
        public static Sprite Decoded(TextureImage image, string key) => new(key, () => image);
    }

    /// <summary>Edge of the generated fallback sprite, matching what the GL viewport uploads.</summary>
    private const int SoftDotSize = 64;

    /// <summary>
    /// <para>Resolve one emitter's permutation, build its material, bind the four texture stages and write
    /// the three Params it authors. Returns null with the reason appended to <paramref name="log"/>.</para>
    ///
    /// <para><paramref name="sprites"/> is the seam between the two callers: the preview window reads and
    /// decodes an asset by path, the map viewport hands back the <c>TextureImage</c> the view-model already
    /// resolved. That is not only cheaper - it is what makes the map viewport swallow the same decode
    /// failures GL swallows, because it consumes the same resolved list.</para>
    /// </summary>
    public static PreviewMaterial? Build(
        ShaderPreviewRenderer renderer, ShaderCacheReader cache, Tocs tocs, VfxEmitterDefinition e,
        Func<string, Sprite?> sprites, StringBuilder log)
    {
        // The whole point of the milestone: the define set comes from the emitter's own flags.
        var defines = VfxShaderFlags.For(e, out var why);

        // M635: a stage whose texture did not resolve is not selected at all, which is what GL does
        // (hasPalette = texture bound && authored; hasErosion likewise). Selecting the permutation anyway
        // hands the stage the renderer's 1x1 WHITE stand-in, and neither stage has a bypass: the palette
        // REPLACES the sprite's rgb with a lookup into white, and erosion multiplies alpha by a mask read
        // from white. Both turn a missing texture into a solid card instead of a missing effect.
        foreach (var (define, sampler) in new[] { ("PALETTIZE_TEXTURES", "sPalettesTexture"), ("ALPHA_EROSION", "sAlphaErosionTexture") })
            if (defines.ContainsKey(define) && sprites(sampler) is null)
            {
                defines.Remove(define);
                why.Add($"{define} dropped: the {sampler} texture did not resolve, so the stage stays off as it does on GL");
            }

        // M640: mesh_vs has a REFLECTIVE axis quad_vs lacks (the fresnel rim in vFresnel). Only asked of the
        // mesh pair; asking quad_vs for an axis it does not have would resolve nothing.
        if (tocs.VsName == MeshVsName && e.Reflection is { HasFresnel: true })
        {
            defines["REFLECTIVE"] = "1";
            why.Add("REFLECTIVE  <- reflectionDefinition authors a fresnel colour and power");
        }

        var vsPerm = ShaderCacheReader.ResolvePermutation(tocs.Vs, defines, null, null, null, out var vw);
        var psPerm = ShaderCacheReader.ResolvePermutation(tocs.Ps, defines, null, null, null, out var pw);

        log.AppendLine($"     defines: {(defines.Count == 0 ? "(none - base permutation)" : string.Join(", ", defines.Keys))}");
        foreach (var w in why) log.AppendLine($"       {w}");
        log.AppendLine($"     vs {(vsPerm is null ? "UNRESOLVED" : "blob " + vsPerm.BlobIndex)}   ps {(psPerm is null ? "UNRESOLVED" : "blob " + psPerm.BlobIndex)}");
        if (vsPerm is null) log.AppendLine($"       vs: {vw}");
        if (psPerm is null) log.AppendLine($"       ps: {pw}");
        if (vsPerm is null || psPerm is null) return null;

        var vs = cache.LoadShader(ShaderCacheReader.TocPathFor(tocs.VsName, DxbcStage.Vertex), vsPerm.BlobIndex, out _);
        var ps = cache.LoadShader(ShaderCacheReader.TocPathFor(tocs.PsName, DxbcStage.Pixel), psPerm.BlobIndex, out _);
        if (vs is null || ps is null) { log.AppendLine("       bytecode would not load"); return null; }

        // M273: modes 2 and 3 pick their blend from the SPRITE, not the integer, so the diffuse stage has to
        // be resolved before the pipeline is built rather than after it (see VfxShaderFlags.IsAdditive).
        // Nothing is decoded twice to get this: a pooled asset already recorded its answer at decode time,
        // and the image read here is handed to BindTexture below instead of being re-opened. The map
        // viewport's sprites are Sprite.Decoded, so Open() there is just a field read.
        var texSprite = sprites("TEXTURE");
        TextureImage? texImage = null;
        bool? texHasAlpha = null;
        if (texSprite is { } sp)
        {
            if (renderer.TryGetTextureAlpha(sp.Key, out var cachedAlpha)) texHasAlpha = cachedAlpha;
            else if (sp.Open is not null)
            {
                try { texImage = sp.Open(); } catch { /* BindTexture reports it properly a few lines down */ }
                if (texImage is not null)
                {
                    texHasAlpha = VfxShaderFlags.TextureUsesAlpha(texImage.Rgba);
                    renderer.NoteTextureAlpha(sp.Key, texHasAlpha.Value);
                }
            }
        }

        // M282: a heat-haze emitter does not shade at all - the renderer swaps in the distortion pipeline
        // and refracts the scene behind the quad. Decided HERE rather than after BuildMaterial so the state
        // description below is honest: distortion is always straight alpha, and a pipeline cache keyed on a
        // blend the draw will not use would hand this material's shaders to an additive emitter later.
        bool isDistortion = e.Distortion is { NormalMapTexturePath.Length: > 0 };

        // M242: describe the variant and the state so the pipeline cache has an honest key. Emitters
        // sharing a permutation AND a blend now share one set of shader objects; a system where every
        // emitter is a base-permutation additive quad collapses to a single pipeline.
        //
        // Riot authors heat haze blendMode=1, which reads as additive - and additive on top of an already
        // bright refracted sample is exactly what turns it into a white blob. GL overrides the authored
        // mode back to alpha for these (VfxParticleRenderer.cs:398-402); so does this.
        bool additive = !isDistortion && VfxShaderFlags.IsAdditive(e.BlendMode, texHasAlpha);
        var vsDesc = new ShaderDescription(tocs.VsName, DxbcStage.Vertex, vsPerm.Key, vsPerm.BlobIndex, defines, vs);
        var psDesc = new ShaderDescription(tocs.PsName, DxbcStage.Pixel, psPerm.Key, psPerm.BlobIndex, defines, ps);
        var stateDesc = StateDescription.Particle(
            additive ? BlendKind.Additive : BlendKind.Alpha, e.AlphaRef / 255f);

        // indexCount MUST be 0, not -1. It means "draw nothing until a Tick assigns this material a range";
        // -1 means "the whole buffer", which on the first frame draws every quad in the shared dynamic
        // buffer through every emitter's pipeline.
        var mat = renderer.BuildMaterial(e.Name, vs, ps, 0, 0, out var rep, vsDesc, psDesc, stateDesc);
        if (mat is null) { log.AppendLine($"       pipeline failed: {rep.Error}"); return null; }

        mat.Additive = additive;
        // Particles never write depth, so they are never reordered - the authored emitter order is the
        // composite the artist built.
        mat.SortableByPipeline = false;
        // M264: these quads live in the dynamic buffer, not the static scene mesh.
        mat.UsesDynamicMesh = true;
        // GL runs particles with the depth TEST on and the depth MASK off (VfxParticleRenderer.cs:350-351).
        // The D3D11 renderer's single depth state writes unconditionally, so without this an additive quad
        // punches a hole in the map behind it.
        mat.WritesDepth = false;
        // Say WHICH rule decided it. On a texture-decided mode the integer alone does not explain the
        // result, and "blend: additive (blendMode 2)" read on its own looks like a bug in the table.
        log.AppendLine($"     blend: {(mat.Additive ? "additive" : "alpha")} (blendMode {e.BlendMode}"
            + (e.BlendMode is 2 or 3 && texHasAlpha is { } ha
                ? $", sprite {(ha ? "uses" : "ignores")} alpha -> {(ha ? "alpha" : "additive")})"
                : ")"));

        // The sprite. TEXTURE__TX is the name quad_ps declares for it.
        BindTexture(renderer, mat, ps, "TEXTURE", sprites, log, texImage);
        if (!string.IsNullOrEmpty(e.TextureMultPath)) BindTexture(renderer, mat, ps, "TEXTUREMULT", sprites, log);
        // M629: bind the map AND send the parameters that go with it.
        //
        // The permutation is selected with ALPHA_EROSION / PALETTIZE_TEXTURES on, the sampler is bound -
        // and then nothing wrote the constants, so the renderer's deliberately-neutral stand-ins stood.
        // Those are documented as no-ops "a real emitter overrides", and for two milestones no emitter
        // ever did. Measured on Aatrox: 77 of his 205 visual emitters run an erosion stage that dissolved
        // nothing, and 38 run a palette stage that recoloured every one of them off row 0. Same shape as
        // M237's AlphaTestReferenceValue: the flag that PICKS a permutation must also send its values.
        if (e.AlphaErosion is { } erosion)
        {
            string? slot = BindTexture(renderer, mat, ps, "sAlphaErosionTexture", sprites, log);

            // Two conditions, and both are load-bearing.
            //
            // The map must really be BOUND: against the renderer's white stand-in the erosion texel reads
            // 1, and real parameters then evaluate mask = 0 over most of the drive - the sprite vanishes.
            // The neutral default exists precisely to survive that case, so leave it standing.
            //
            // And IsDegenerate is GL's own guard (VfxParticleRenderer.cs), for the same reason: under the
            // INFERRED P.y/P.z/P.w assignment 16% of erosion emitters evaluate to a mask of zero
            // everywhere. Skipping those can only restore the pre-M174 look, never make one worse.
            if (slot is not null && !erosion.IsDegenerate)
            {
                var yzw = erosion.PackYzw();
                // .x is unread by the shader - the drive arrives per particle on the instance attribute,
                // which ParticleQuadBuilder already writes into Uv0.w. Same layout GL uploads.
                mat.Params["cAlphaErosionParams"] = new[] { 0f, yzw.X, yzw.Y, yzw.Z };
                mat.Params["cAlphaErosionTextureMixer"] = new[]
                    { erosion.ChannelMixer.X, erosion.ChannelMixer.Y, erosion.ChannelMixer.Z, erosion.ChannelMixer.W };
                log.AppendLine($"     erosion: slice {erosion.SliceWidth:0.###}, "
                    + $"feather {erosion.FeatherIn:0.###}/{erosion.FeatherOut:0.###}");
            }
            else if (slot is not null)
                log.AppendLine("     erosion: degenerate under the inferred packing - left neutral, as GL does");
        }

        if (e.Palette is { } palette)
        {
            string? slot = BindTexture(renderer, mat, ps, "sPalettesTexture", sprites, log);

            // M635: the strip is a LOOKUP TABLE, so its sampler clamps - the sprite's own sampler must
            // keep wrapping for flipbooks and scrolls, which is why this is per slot and not per material.
            // Under WRAP, u = 0 (every black texel of an additive sprite) filtered the strip's first texel
            // with its last and painted the sprite's whole black field a mid grey: a rectangle. GL has bound
            // a clamp sampler here since M184; the per-slot mechanism this path lacked is
            // PreviewMaterial.ClampedSamplers.
            if (slot is not null)
                (mat.ClampedSamplers ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                    .Add(slot.Replace("__TX", "__SMP", StringComparison.OrdinalIgnoreCase));

            // The palette stage REPLACES the sprite's RGB and has no bypass, so a bound-but-unparameterised
            // palette is not a missing effect, it is a wrong colour on every particle. Only IsUsable
            // configurations get here; the rest keep row 0, which is what they were already showing.
            if (slot is not null && palette.IsUsable)
            {
                // The shader reads v = Select.w + Select.x and u = mixed + Select.z. GL sends the row in
                // one uniform; here it goes in .x with the two animated offsets left at zero, which is the
                // same sample point.
                mat.Params["cPaletteSelectMain"] = new[] { palette.RowV, 0f, 0f, 0f };
                mat.Params["cPaletteSrcMixerMain"] = new[]
                    { palette.SrcMixer.X, palette.SrcMixer.Y, palette.SrcMixer.Z, palette.SrcMixer.W };
                log.AppendLine($"     palette: row {palette.Selector:0.#}/{palette.Count} -> v {palette.RowV:0.###}");

                // NOT handled here, and stated rather than left to be found: GL binds a CLAMP sampler to
                // the palette slot (M184) because a lookup landing outside [0,1] wraps to the far end of
                // the gradient. PreviewMaterial.SamplerAddress is per MATERIAL, not per slot, so the same
                // fix cannot be expressed on this path without a per-slot sampler - its own change.
            }
        }

        // M282: heat haze. The strength is set whether or not the normal map resolves, because it is what
        // routes this material away from the billboard path - and an unresolved heat haze must draw NOTHING
        // rather than fall back to that path. Its sprite is routinely a deliberate blank (Jade's is an 8x8
        // all-white "color-hold"), so the fallback is not a degraded effect, it is a solid white card over
        // the map. GL skips the emitter under the same condition (VfxParticleRenderer.cs:381).
        if (isDistortion)
        {
            mat.DistortionStrength = e.Distortion!.Strength;
            BindTexture(renderer, mat, ps, "DISTORTION", sprites, log,
                        targetKey: PreviewMaterial.DistortionNormalKey);
            bool bound = mat.HasTexture(PreviewMaterial.DistortionNormalKey);
            log.AppendLine($"     distortion: strength {e.Distortion.Strength:0.###}, mode {e.Distortion.Mode}"
                + $", blend forced to alpha (authored {e.BlendMode})"
                + (bound ? "" : " - NORMAL MAP UNRESOLVED, emitter will not draw"));
        }

        // The flipbook atlas descriptor, per emitter: (columns, 1/columns, 1/rows).
        // Derived in M231 from quad_vs's cell arithmetic.
        mat.Params["TEXTURE_INFO"] = ParticleQuadBuilder.TextureInfo(e.TexDiv);

        // M633: and the SECOND one, for the multiply stage. Same shape, same source, and it was the only
        // atlas descriptor this method never sent - so every MULT_PASS emitter fell back to the renderer's
        // identity default and its multiplier texture was sampled as ONE cell covering the whole sprite.
        //
        // Read off the shipped bytecode rather than assumed. quad_vs blob 9 (MULT_PASS=1) spends the two
        // descriptors in the same arithmetic, from the SAME flipbook frame index:
        //
        //     round_ni r0.x, v2.z                  // frame = floor(TEXCOORD0.z)
        //     mul  r0.y, r0.x, cb1[0].y            // primary: row  = floor(frame / cols)
        //     ...                                  //          o2.xy = (col+u)/cols, (row+v)/rows
        //     mul  r0.y, r0.x, cb1[1].y            // MULT:    row2 = floor(frame / cols2)
        //     mad  r0.x, -r0.y, cb1[1].x, r0.x     //          col2 = frame - row2*cols2
        //     add  r0.xy, r0.xyxx, v3.xyxx         //          + TEXCOORD1 (the cell coordinate)
        //     mul  o2.zw, r0.xxxy, cb1[1].yyyz     //          o2.zw = (col2+u2)/cols2, (row2+v2)/rows2
        //
        // cb1[1] is TEXTURE_INFO_2 at $Globals+16. Two facts follow. Its layout is identical to
        // TEXTURE_INFO, so ParticleQuadBuilder.TextureInfo builds it unchanged; and the multiplier atlas
        // ADVANCES with the flipbook, which the GL path has never done and recorded as a known defect - so
        // this does not merely restore parity, it is the first path that gets it right.
        //
        // What the identity default actually did: cols2 = 1 makes col2 = frame - floor(frame) = 0 and
        // row2 = frame, so the V coordinate came out as (frame + v) and the U as the full 0..1 sweep. On a
        // 2x2 multiplier sheet that stretches all four cells across the quad, which is a mask over the
        // wrong pixels rather than a subtle error. Measured over the skin0 VFX closure of five champions:
        // 192 emitters bind a multiplier texture and 35 of them author a non-1x1 grid, including 20 on
        // Aatrox's R (Aatrox_Base_R_burning_mult, 2x2) and 9 on his Q (Aatrox_Base_Q_smoke_mult, 2x2).
        // A 1x1 grid builds the identity, so this is a no-op for the other 157.
        if (!string.IsNullOrEmpty(e.TextureMultPath))
            mat.Params["TEXTURE_INFO_2"] = ParticleQuadBuilder.TextureInfo(e.TextureMultTexDiv);

        // M237: pass the values that SELECTED the permutation, which the first cut did not.
        //
        // VfxShaderFlags turns ALPHA_TEST on precisely because alphaRef > 0, and then the renderer's engine
        // default bound AlphaTestReferenceValue = 0 - a cutoff of zero discards nothing, so the permutation
        // was selected and then neutered.
        if (e.AlphaRef > 0) mat.Params["AlphaTestReferenceValue"] = new[] { e.AlphaRef / 255f, 0f, 0f, 0f };

        // Same shape of omission: quad_vs slides each vertex along its own camera ray by this, and the GL
        // path applies the emitter's authored value, while this bound a flat 0.
        if (e.DepthPushPull != 0f)
            mat.Params["PARTICLE_DEPTH_PUSH_PULL"] = new[] { e.DepthPushPull, 0f, 0f, 0f };

        // M363 (v0.4.0 item 7a): soft particles. The third instance of the same shape - VfxShaderFlags has
        // always turned SOFT_PARTICLES on from this very field, so the permutation was already being
        // selected; what was missing was the emitter's authored widths and a real depth texture to measure
        // against. The renderer's fallback deliberately neutralises the fade to "fully visible", so until
        // now these emitters rendered as ordinary hard-edged sprites rather than incorrectly.
        //
        // IsDegenerate is honoured for the reason VfxSoftParticle spells out: if the packing is wrong the
        // symptom is the sprite disappearing, and skipping the stage can only restore the previous
        // appearance, never make anything worse. NeedsSceneDepth is what tells the renderer to snapshot and
        // bind the depth buffer, so it must not be set for a configuration we then decline to drive.
        if (e.SoftParticle is { } soft && !soft.IsDegenerate)
        {
            var p = soft.PackParams();
            mat.Params["cSoftParticleParams"] = new[] { p.X, p.Y, p.Z, p.W };
            mat.NeedsSceneDepth = true;
        }

        return mat;
    }

    /// <param name="preloaded">Pixels the caller already had to decode for the blend decision (M273). Saves
    /// the second read; null means "open it here", which is every stage except TEXTURE.</param>
    /// <param name="targetKey">M282: bind under THIS key instead of a name resolved from the shader's
    /// declared textures. Needed for the distortion normal map, which no permutation of quad_ps declares -
    /// it feeds our own pipeline - but which still wants the pooling and lifetime handling every other
    /// stage gets, so it takes the same road with a different destination.</param>
    /// <summary>Binds a sampler's texture and returns the SLOT NAME it bound to, or null when nothing was
    /// bound. M629 needs that answer: uploading real alpha-erosion parameters against the renderer's WHITE
    /// stand-in erases the sprite outright, so the parameters may only be sent when the map is really there.</summary>
    private static string? BindTexture(ShaderPreviewRenderer renderer, PreviewMaterial mat, DxbcShader ps,
        string sampler, Func<string, Sprite?> sprites, StringBuilder log, TextureImage? preloaded = null,
        string? targetKey = null)
    {
        string slotName;
        if (targetKey is not null) slotName = targetKey;
        else
        {
            var slot = ps.Textures.FirstOrDefault(t =>
                t.Name.Equals(sampler + "__TX", StringComparison.OrdinalIgnoreCase)
                || t.Name.Equals(sampler, StringComparison.OrdinalIgnoreCase));
            if (slot is null) { log.AppendLine($"     {sampler}: no such slot in this permutation"); return null; }
            slotName = slot.Name;
        }

        if (sprites(sampler) is not { } sprite) { log.AppendLine($"     {sampler}: not authored"); return null; }

        // M266 (divergence 15): TryBindCached FIRST, always, and before the sprite is even opened.
        //
        // SetTexture unconditionally creates a new SRV and retires the previous one under that key, and
        // _retired is only freed by ClearMaterials - so a rebuild that went straight to SetTexture would leak
        // one view per sprite per particle-selection click, and RebuildParticlePlayback fires on every one of
        // those. Probing by key first also means a repeat play costs no WAD read and no decode.
        // M272: SAY SO. This branch used to return silently, and silence here is not neutral - it reads as
        // "this emitter has no sprite". Measured on Map22's Rising_Mist_Supernova: emitters [3] darkMist and
        // [5] brightMotes1 each re-author a .tex an earlier emitter in the same system already put in the
        // pool ([0] brightMist's Morde_Base_Dust, [1] impactStones_smoke's TFT_PDM_Cosmic_Spark_2x2), so both
        // took this branch and printed nothing at all. That silence was read off the log as "brightMotes1
        // binds no texture", and it cost a whole diagnosis - PreviewMaterial.UnboundTextures says TEXTURE__TX
        // is bound on both, and the pool holds 5 distinct views for 6 emitters, which is the dedup working.
        // The dimensions are not printed because the pool stores views, not sizes; the key identifies the
        // asset, which is what the reader actually needs to compare two emitters.
        if (renderer.TryBindCached(mat, slotName, sprite.Key))
        {
            log.AppendLine($"     {sampler} -> {slotName} (pooled: {sprite.Key})");
            return slotName;
        }

        if (sprite.Open is null)
        {
            renderer.SetTexture(mat, slotName, sprite.Key, VfxPlaybackSim.SoftDot(SoftDotSize),
                SoftDotSize, SoftDotSize);
            log.AppendLine($"     {sampler} -> {slotName}  [soft-dot fallback]");
            return slotName;
        }

        TextureImage? img = preloaded;
        if (img is null)
        {
            try { img = sprite.Open(); }
            catch (Exception ex) { log.AppendLine($"     {sampler}: FAILED {ex.Message}"); return null; }
        }
        if (img is null) return null;   // the callback already said why; nothing bound = the renderer's stand-in

        renderer.SetTexture(mat, slotName, sprite.Key, img.Rgba, img.Width, img.Height);
        log.AppendLine($"     {sampler} -> {slotName} ({img.Width}x{img.Height})");
        return slotName;
    }
}
