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

    /// <summary>The two stage tables every emitter resolves its permutation against. Read once per
    /// playback, not once per emitter.</summary>
    public sealed record Tocs(ShaderStageToc Vs, ShaderStageToc Ps);

    public static Tocs? ReadTocs(ShaderCacheReader cache, out string? error)
    {
        error = null;
        var vs = cache.ReadToc(ShaderCacheReader.TocPathFor(VsName, DxbcStage.Vertex));
        var ps = cache.ReadToc(ShaderCacheReader.TocPathFor(PsName, DxbcStage.Pixel));
        if (vs is null || ps is null)
        {
            error = "particlesystem/quad_vs+quad_ps not in the shader cache";
            return null;
        }
        return new Tocs(vs, ps);
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

        var vsPerm = ShaderCacheReader.ResolvePermutation(tocs.Vs, defines, null, null, null, out var vw);
        var psPerm = ShaderCacheReader.ResolvePermutation(tocs.Ps, defines, null, null, null, out var pw);

        log.AppendLine($"     defines: {(defines.Count == 0 ? "(none - base permutation)" : string.Join(", ", defines.Keys))}");
        foreach (var w in why) log.AppendLine($"       {w}");
        log.AppendLine($"     vs {(vsPerm is null ? "UNRESOLVED" : "blob " + vsPerm.BlobIndex)}   ps {(psPerm is null ? "UNRESOLVED" : "blob " + psPerm.BlobIndex)}");
        if (vsPerm is null) log.AppendLine($"       vs: {vw}");
        if (psPerm is null) log.AppendLine($"       ps: {pw}");
        if (vsPerm is null || psPerm is null) return null;

        var vs = cache.LoadShader(ShaderCacheReader.TocPathFor(VsName, DxbcStage.Vertex), vsPerm.BlobIndex, out _);
        var ps = cache.LoadShader(ShaderCacheReader.TocPathFor(PsName, DxbcStage.Pixel), psPerm.BlobIndex, out _);
        if (vs is null || ps is null) { log.AppendLine("       bytecode would not load"); return null; }

        // M242: describe the variant and the state so the pipeline cache has an honest key. Emitters
        // sharing a permutation AND a blend now share one set of shader objects; a system where every
        // emitter is a base-permutation additive quad collapses to a single pipeline.
        bool additive = VfxShaderFlags.IsAdditive(e.BlendMode);
        var vsDesc = new ShaderDescription(VsName, DxbcStage.Vertex, vsPerm.Key, vsPerm.BlobIndex, defines, vs);
        var psDesc = new ShaderDescription(PsName, DxbcStage.Pixel, psPerm.Key, psPerm.BlobIndex, defines, ps);
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
        log.AppendLine($"     blend: {(mat.Additive ? "additive" : "alpha")} (blendMode {e.BlendMode})");

        // The sprite. TEXTURE__TX is the name quad_ps declares for it.
        BindTexture(renderer, mat, ps, "TEXTURE", sprites, log);
        if (!string.IsNullOrEmpty(e.TextureMultPath)) BindTexture(renderer, mat, ps, "TEXTUREMULT", sprites, log);
        if (e.AlphaErosion is not null) BindTexture(renderer, mat, ps, "sAlphaErosionTexture", sprites, log);
        if (e.Palette is not null) BindTexture(renderer, mat, ps, "sPalettesTexture", sprites, log);

        // The flipbook atlas descriptor, per emitter: (columns, 1/columns, 1/rows).
        // Derived in M231 from quad_vs's cell arithmetic.
        mat.Params["TEXTURE_INFO"] = ParticleQuadBuilder.TextureInfo(e.TexDiv);

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

        return mat;
    }

    private static void BindTexture(ShaderPreviewRenderer renderer, PreviewMaterial mat, DxbcShader ps,
        string sampler, Func<string, Sprite?> sprites, StringBuilder log)
    {
        var slot = ps.Textures.FirstOrDefault(t =>
            t.Name.Equals(sampler + "__TX", StringComparison.OrdinalIgnoreCase)
            || t.Name.Equals(sampler, StringComparison.OrdinalIgnoreCase));
        if (slot is null) { log.AppendLine($"     {sampler}: no such slot in this permutation"); return; }

        if (sprites(sampler) is not { } sprite) { log.AppendLine($"     {sampler}: not authored"); return; }

        // M266 (divergence 15): TryBindCached FIRST, always, and before the sprite is even opened.
        //
        // SetTexture unconditionally creates a new SRV and retires the previous one under that key, and
        // _retired is only freed by ClearMaterials - so a rebuild that went straight to SetTexture would leak
        // one view per sprite per particle-selection click, and RebuildParticlePlayback fires on every one of
        // those. Probing by key first also means a repeat play costs no WAD read and no decode.
        if (renderer.TryBindCached(mat, slot.Name, sprite.Key)) return;

        if (sprite.Open is null)
        {
            renderer.SetTexture(mat, slot.Name, sprite.Key, VfxPlaybackSim.SoftDot(SoftDotSize),
                SoftDotSize, SoftDotSize);
            log.AppendLine($"     {sampler} -> {slot.Name}  [soft-dot fallback]");
            return;
        }

        TextureImage? img;
        try { img = sprite.Open(); }
        catch (Exception ex) { log.AppendLine($"     {sampler}: FAILED {ex.Message}"); return; }
        if (img is null) return;   // the callback already said why; nothing bound = the renderer's stand-in

        renderer.SetTexture(mat, slot.Name, sprite.Key, img.Rgba, img.Width, img.Height);
        log.AppendLine($"     {sampler} -> {slot.Name} ({img.Width}x{img.Height})");
    }
}
