using System.Numerics;

namespace ReyEngine.Formats.Materials;

/// <summary>Which RiotApprox preview profile a material maps to (drives which lighting features apply).</summary>
public enum PreviewProfileKind { Unknown, ChampionSkin, MapStatic, MatCap, Specular }

/// <summary>How a material composites, derived from its technique/pass blend state + shader name (M34).
/// Opaque writes depth solidly; Cutout alpha-tests (discard) but stays opaque; Transparent alpha-blends
/// over the scene (drawn after opaque, no depth write). TransparentCutout does both for soft-edged decals.</summary>
public enum MaterialRenderMode { Opaque, Cutout, Transparent, TransparentCutout }

/// <summary>
/// The preview feature set + UV transform derived from a material's real .bin data (switches +
/// paramValues + sampler names). This is what lets RiotApprox apply rim/specular/etc. per-material
/// instead of globally. Feature flags default OFF; UV defaults to identity (scale 1,1 · offset 0,0).
/// See <see cref="MaterialProfiles.Classify"/> for the data-driven rules.
/// </summary>
public sealed record MaterialProfile(
    PreviewProfileKind Kind,
    bool UsesRim,
    bool UsesSpecular,
    bool UsesEmissive,
    bool UsesMatCap,
    Vector2 UvScale,
    Vector2 UvOffset,
    float UvRotationDegrees,
    string? UvScaleSource,
    string? UvOffsetSource,
    MaterialRenderMode RenderMode = MaterialRenderMode.Opaque,
    bool DoubleSided = false,
    Vector4? Tint = null,   // M34: TintColor param (rgba); also applied to textured soft decals when TintTextured
    bool TintTextured = false,
    bool BlendEnabled = false,   // M34: pass.blendEnable (real .bin flag)
    float? AlphaCutoff = null,   // M34: AlphaTestValue param (real cutout threshold, e.g. 0.3); null = shader default 0.35
    bool ClampU = false,         // M34: diffuse addressU == Clamp (decals) — clamp UV instead of tiling
    bool ClampV = false,
    // ---- M44: Flowmap_River animated water (Bloom_FlowMapRiver_*). When IsFlowmap, the preview renders
    // flowing tinted translucent water instead of a flat diffuse. FlowMapPath/FlowNormalPath are the sampler
    // .tex paths the App resolves and binds (Flow_Map -> mask slot, Flowing_Normal_Map -> gradient slot). ----
    bool IsFlowmap = false,
    float FlowSpeed = 0f,
    float FlowStrength = 0f,
    Vector2 FlowTile = default,
    Vector4 ColorInside = default,
    Vector4 ColorOutside = default,
    float WaterAlpha = 1f,
    string? FlowMapPath = null,
    string? FlowNormalPath = null,
    // Terrain blend shader 0xe25b830f: Mask_Texture selects three detail layers over Bottom_Texture.
    // Detail layers use world-space XZ coordinates with independent tiling; the mask keeps mesh UVs.
    bool IsTerrainBlend = false,
    string? TerrainMaskPath = null,
    string? TerrainBottomPath = null,
    string? TerrainMiddlePath = null,
    string? TerrainTopPath = null,
    string? TerrainExtrasPath = null,
    Vector2 TerrainBottomTiling = default,
    Vector2 TerrainMiddleTiling = default,
    Vector2 TerrainTopTiling = default,
    Vector2 TerrainExtrasTiling = default,
    float TerrainWorldScale = 1f,
    float TerrainRMaskMultiplier = 1f,
    float TerrainGMaskMultiplier = 1f,
    float TerrainBMaskMultiplier = 1f)
{
    public static readonly MaterialProfile Default =
        new(PreviewProfileKind.Unknown, false, false, false, false, Vector2.One, Vector2.Zero, 0f, null, null);

    // ---- M34 render state (only cullEnable + blendEnable exist in the .bin; the rest are derived) ----
    /// <summary>Backface culling flag (true = single-sided/cull; the .bin default when cullEnable is absent).</summary>
    public bool CullEnabled => !DoubleSided;
    /// <summary>Two-sided lighting is needed exactly when the material is not culled.</summary>
    public bool TwoSided => DoubleSided;
    /// <summary>Depth is written for opaque/cutout, not for transparent (alpha-blended) surfaces.</summary>
    public bool DepthWrite => RenderMode is not (MaterialRenderMode.Transparent or MaterialRenderMode.TransparentCutout);
    /// <summary>Alpha-test/cutout (fixed shader threshold; no explicit cutoff value exists in the schema).</summary>
    public bool AlphaCutout => RenderMode is MaterialRenderMode.Cutout or MaterialRenderMode.TransparentCutout;

    /// <summary>True when the UV transform actually changes the mapping (identity 1,1/0,0 is not flagged,
    /// even if a UV param is present but set to identity).</summary>
    public bool HasUvTransform =>
        UvScale != Vector2.One || UvOffset != Vector2.Zero || UvRotationDegrees != 0f;

    public string ProfileLabel => Kind switch
    {
        PreviewProfileKind.ChampionSkin => "Champion Skin",
        PreviewProfileKind.MapStatic => "Map Static",
        PreviewProfileKind.MatCap => "MatCap",
        PreviewProfileKind.Specular => "Specular",
        _ => "Unknown",
    };

    /// <summary>Short "diffuse + rim + …" summary of the active features, for the inspector.</summary>
    public string FeatureSummary
    {
        get
        {
            var parts = new List<string> { IsTerrainBlend ? "terrain blend" : "diffuse" };
            if (UsesRim) parts.Add("rim");
            if (UsesSpecular) parts.Add("specular");
            if (UsesEmissive) parts.Add("emissive");
            if (UsesMatCap) parts.Add("matcap");
            return string.Join(" + ", parts);
        }
    }

    /// <summary>Blend/compositing label for the inspector (e.g. "Transparent, double-sided").</summary>
    public string RenderModeLabel
    {
        get
        {
            string m = RenderMode switch
            {
                MaterialRenderMode.Cutout => "Cutout (alpha-test)",
                MaterialRenderMode.Transparent => "Transparent (alpha-blend)",
                MaterialRenderMode.TransparentCutout => "Transparent cutout (alpha-blend)",
                _ => "Opaque",
            };
            return DoubleSided ? m + ", double-sided" : m;
        }
    }

    /// <summary>Full render-state summary for the inspector/material editor (M34).</summary>
    public string RenderStateSummary
    {
        get
        {
            var parts = new List<string>
            {
                RenderMode switch
                {
                    MaterialRenderMode.Cutout => "cutout",
                    MaterialRenderMode.Transparent => "transparent",
                    MaterialRenderMode.TransparentCutout => "transparent cutout",
                    _ => "opaque",
                },
                CullEnabled ? "cull backfaces" : "two-sided",
            };
            if (BlendEnabled) parts.Add(IsTerrainBlend ? "texture blend" : "blend");
            if (!DepthWrite) parts.Add("no depth-write");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>Classifies a <see cref="MaterialBinding"/> into a <see cref="MaterialProfile"/> from its
/// real .bin data. Rules are conservative: a feature turns on only when the data clearly asks for it.</summary>
public static class MaterialProfiles
{
    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

    // paramValues names (normalized: lowercased, '_' and spaces stripped) that carry the base-diffuse UV
    // transform. Scrolling_*/EMISSION_* etc. are deliberately excluded — they're animated/sub-effect, not
    // static base tiling.
    private static readonly string[] ScaleNames =
        { "uvscale", "uvtiling", "tiling", "texturescale", "texscale", "texcoordscale", "diffusescale", "decaluvtile", "uvtile" };
    private static readonly string[] OffsetNames =
        { "uvoffset", "uvtranslate", "textureoffset", "texoffset", "texcoordoffset" };
    private static readonly string[] RotationNames =
        { "uvrotation", "uvrotate", "texrotation" };

    private static string Norm(string s) => s.Replace("_", "").Replace(" ", "").ToLowerInvariant();

    public static MaterialProfile Classify(MaterialBinding b, MaterialSourceKind sourceKind)
    {
        var terrain = ClassifyTerrainBlend(b);

        // ---- UV transform from paramValues (first matching name wins) ----
        Vector2 scale = Vector2.One, offset = Vector2.Zero;
        float rotationDeg = 0f;
        string? scaleSrc = null, offsetSrc = null;
        foreach (var p in b.Parameters)
        {
            var norm = Norm(p.Name);
            if (scaleSrc is null && ScaleNames.Contains(norm) && p.TryGetVector4(out var sv) && (sv.X != 0 || sv.Y != 0))
            { scale = new Vector2(sv.X, sv.Y); scaleSrc = p.Name; }
            else if (offsetSrc is null && OffsetNames.Contains(norm) && p.TryGetVector4(out var ov))
            { offset = new Vector2(ov.X, ov.Y); offsetSrc = p.Name; }
            else if (RotationNames.Contains(norm) && p.TryGetVector4(out var rv) && rv.X != 0)
            { rotationDeg = rv.X; }
        }

        // ---- Feature flags from switches / samplers / params ----
        bool matcap = b.MatCap is not null;
        bool emissive = b.Emissive is not null
            || b.Switches.Any(kv => kv.Value && (kv.Key.Contains("EMISS", OIC) || kv.Key.Contains("GLOW", OIC) || kv.Key.Contains("ILLUM", OIC)));

        // Rim/fresnel: League champion shaders use USE_RIM; a Gradient sampler is the rim ramp; some skins
        // expose a rim* param. Map static materials have none of these ⇒ no rim (the old fake-specular fix).
        bool rim = b.Switches.Any(kv => kv.Value && kv.Key.Contains("RIM", OIC))
            || b.Gradient is not null
            || b.Parameters.Any(p => p.Name.Contains("rim", OIC));

        // Specular: NO global switch exists — real specular materials carry Specular_*/Spec_Color params
        // (or a gloss/metal switch). Off unless the data clearly indicates it.
        bool specular = b.Switches.Any(kv => kv.Value && (kv.Key.Contains("SPEC", OIC) || kv.Key.Contains("GLOSS", OIC) || kv.Key.Contains("METAL", OIC)))
            || b.Parameters.Any(p => { var n = Norm(p.Name); return n.Contains("specular") || n.Contains("speccolor") || n.Contains("glossiness") || n.Contains("metallic"); });

        var kind = matcap ? PreviewProfileKind.MatCap
            : sourceKind == MaterialSourceKind.ChampionSkin ? PreviewProfileKind.ChampionSkin
            : sourceKind == MaterialSourceKind.MapMaterials ? PreviewProfileKind.MapStatic
            : specular ? PreviewProfileKind.Specular
            : PreviewProfileKind.Unknown;

        // AlphaTestValue param = the real alpha-test cutoff (foliage/decals). Its PRESENCE means the material
        // is alpha-tested (cutout), even when the shader name doesn't say "AlphaTest" (e.g. converted NVR mods
        // use SRX_Blend_Master + AlphaTestValue). This is what lets mod foliage cut out like Riot's own.
        float? alphaCutoff = null;
        foreach (var p in b.Parameters)
            if (Norm(p.Name) == "alphatestvalue" && p.TryGetVector4(out var av) && av.X > 0f) { alphaCutoff = av.X; break; }

        var (renderMode, doubleSided) = ClassifyRenderMode(b, sourceKind, alphaCutoff);
        // This shader's pass has blendEnable set even though it blends terrain textures internally. The final
        // ground surface is solid and must stay in the opaque depth-writing pass.
        if (terrain.IsTerrainBlend) renderMode = MaterialRenderMode.Opaque;

        // TintColor: for sampler-less effect/indicator materials the tint IS the colour and alpha. Authored
        // soft decals also multiply their diffuse by TintColor (Map453's road decal uses 0.5 grey).
        Vector4? tint = null;
        bool tintTextured = renderMode == MaterialRenderMode.TransparentCutout;
        if (tintTextured || b.Slots.All(s => !s.SamplerName.Contains("Diffuse", OIC)))
            foreach (var p in b.Parameters)
                if (Norm(p.Name) == "tintcolor" && p.TryGetVector4(out var tv)) { tint = tv; break; }

        // Texture wrap: decals use Clamp (address 1; also treat D3D Clamp 3 / Border 4 as clamp) so their
        // out-of-[0,1] UVs don't tile the decal over the whole mesh. Everything else (Wrap/Mirror) tiles.
        static bool IsClamp(int a) => a == 1 || a == 3 || a == 4;
        bool clampU = IsClamp(b.DiffuseAddressU), clampV = IsClamp(b.DiffuseAddressV);

        var flow = ClassifyFlowmap(b);

        return new MaterialProfile(kind, rim, specular, emissive, matcap, scale, offset, rotationDeg, scaleSrc, offsetSrc,
            renderMode, doubleSided, tint, tintTextured, b.BlendEnable, alphaCutoff, clampU, clampV,
            flow.IsFlowmap, flow.Speed, flow.Strength, flow.Tile, flow.Inside, flow.Outside, flow.Alpha,
            flow.FlowMapPath, flow.FlowNormalPath,
            terrain.IsTerrainBlend, terrain.MaskPath, terrain.BottomPath, terrain.MiddlePath, terrain.TopPath,
            terrain.ExtrasPath, terrain.BottomTiling, terrain.MiddleTiling, terrain.TopTiling, terrain.ExtrasTiling,
            terrain.WorldScale, terrain.RMultiplier, terrain.GMultiplier, terrain.BMultiplier);
    }

    /// <summary>Detect shader 0xe25b830f and read its authored terrain layer paths, tiling, and RGB mask weights.</summary>
    private static (bool IsTerrainBlend, string? MaskPath, string? BottomPath, string? MiddlePath,
        string? TopPath, string? ExtrasPath, Vector2 BottomTiling, Vector2 MiddleTiling,
        Vector2 TopTiling, Vector2 ExtrasTiling, float WorldScale, float RMultiplier,
        float GMultiplier, float BMultiplier) ClassifyTerrainBlend(MaterialBinding b)
    {
        TextureSlot? Slot(string name) => b.Slots.FirstOrDefault(s => s.SamplerName.Equals(name, OIC));
        var mask = Slot("Mask_Texture");
        var bottom = Slot("Bottom_Texture");
        var middle = Slot("Middle_Texture");
        var top = Slot("Top_Texture");
        var extras = Slot("Extras_Texture");
        bool exactShader = (b.RenderShader ?? "").Equals("0xe25b830f", OIC);
        bool samplerSignature = mask is not null && bottom is not null && middle is not null && top is not null && extras is not null;
        if (!(exactShader || samplerSignature) || !samplerSignature)
            return (false, null, null, null, null, null, default, default, default, default, 1f, 1f, 1f, 1f);

        Vector4 Param(string name, Vector4 fallback)
        {
            var parameter = b.Parameters.FirstOrDefault(p => Norm(p.Name) == Norm(name));
            return parameter is not null && parameter.TryGetVector4(out var value) ? value : fallback;
        }
        static Vector2 Tiling(Vector4 value) => new(
            value.X != 0f ? value.X : 1f,
            value.Y != 0f ? value.Y : (value.X != 0f ? value.X : 1f));

        var worldScale = Param("WS_Multiplier", new Vector4(1f, 0f, 0f, 0f)).X;
        if (worldScale == 0f) worldScale = 1f;
        return (true, mask!.Path, bottom!.Path, middle!.Path, top!.Path, extras!.Path,
            Tiling(Param("Bottom_Tiling", Vector4.One)),
            Tiling(Param("Mid_Tiling", Vector4.One)),
            Tiling(Param("Top_Tiling", Vector4.One)),
            Tiling(Param("Extra_Tiling", Vector4.One)),
            worldScale,
            Param("R_mask_multiplier", Vector4.One).X,
            Param("G_mask_multiplier", Vector4.One).X,
            Param("B_mask_multiplier", Vector4.One).X);
    }

    /// <summary>M44: detect + read a Flowmap_River water material (Bloom_FlowMapRiver_*). A flowmap material
    /// has a Flow_Map sampler AND a FlowMap_Speed/Flowmap_Strength param (or "FlowMap" in its shader/name).
    /// Returns the flow textures + tint/tile/speed params so the preview can animate flowing water.</summary>
    private static (bool IsFlowmap, float Speed, float Strength, Vector2 Tile, Vector4 Inside, Vector4 Outside,
        float Alpha, string? FlowMapPath, string? FlowNormalPath) ClassifyFlowmap(MaterialBinding b)
    {
        // Flow_Map sampler carries the per-texel flow direction; the flowing normal map is a Normal sampler
        // whose name also mentions "Flow" (Flowing_Normal_Map) so it isn't confused with a regular normal map.
        var flowMap = b.Slots.FirstOrDefault(s => s.SamplerName.Contains("Flow", OIC) && !s.SamplerName.Contains("Normal", OIC));
        var flowNormal = b.Slots.FirstOrDefault(s => s.SamplerName.Contains("Flow", OIC) && s.SamplerName.Contains("Normal", OIC));

        bool named = (b.RenderShader ?? b.ShaderName ?? "").Contains("Flow", OIC) || b.Name.Contains("FlowMap", OIC);
        bool hasFlowParam = b.Parameters.Any(p => { var n = Norm(p.Name); return n.Contains("flowmapspeed") || n.Contains("flowmapstrength"); });
        if (!(flowMap is not null && (named || hasFlowParam))) return (false, 0, 0, default, default, default, 1f, null, null);

        float speed = 0.15f, strength = 1f, alpha = 0.9f;
        Vector2 tile = new(5f, 10f);
        Vector4 inside = new(0.44f, 0.87f, 0.92f, 1f), outside = new(0.57f, 0.64f, 0.70f, 1f);
        foreach (var p in b.Parameters)
        {
            var n = Norm(p.Name);
            if (!p.TryGetVector4(out var v)) continue;
            if (n == "flowmapspeed") speed = v.X;
            else if (n == "flowmapstrength") strength = v.X;
            else if (n == "flownormaltile") tile = new Vector2(v.X != 0 ? v.X : 5f, v.Y != 0 ? v.Y : 10f);
            else if (n == "colorinside") inside = v;
            else if (n == "coloroutside") outside = v;
            else if (n == "translucentcontrol") alpha = Math.Clamp(v.X, 0.15f, 1f);
        }
        return (true, speed, strength, tile, inside, outside, alpha, flowMap!.Path, flowNormal?.Path);
    }

    /// <summary>Derive the compositing mode from the material's technique/pass blend state + shader name (M34).
    /// The shader name is the primary intent signal; blendEnable disambiguates opaque vs alpha-blend.</summary>
    private static (MaterialRenderMode, bool) ClassifyRenderMode(MaterialBinding b, MaterialSourceKind sourceKind, float? alphaCutoff)
    {
        // real technique shader (e.g. Shaders/StaticMesh/DefaultEnv_Flat_AlphaTest); may fall back to class name.
        string shader = (b.RenderShader ?? b.ShaderName ?? "");
        // Double-sided is authoritative from the pass's cullEnable (false = double-sided); only when that
        // field is absent do we fall back to the shader-name heuristic.
        bool doubleSided = b.CullEnable is bool cull
            ? !cull
            : shader.Contains("DoubleSided", OIC) || shader.Contains("TwoSided", OIC);

        // A blend-enabled pass authored SrcAlpha/InvSrcAlpha alpha blending. A *small* AlphaTestValue alongside
        // it is only a "reject fully-transparent texels" floor, NOT a hard-cutout instruction: the surface still
        // wants its soft alpha gradient composited. That covers decals AND feathered ground/prop overlays like
        // Jade's turret_stoneBase_hq_ (blend 6/7 + AlphaTestValue 0.005) — keep them TransparentCutout so the
        // soft edge survives instead of snapping every texel above 0.005 to fully opaque (M66 "too harsh cut").
        // A *large* cutoff is a genuine alpha test — foliage (SRX_Blend_Master + AlphaTestValue ~0.3-0.5 +
        // blendEnable) must stay Cutout (opaque depth) or it ghosts and mis-sorts — so only small floors soften.
        const float SoftAlphaFloorMax = 0.1f; // below this, blendEnable's soft blend wins over the alpha test
        bool decal = b.Name.Contains("decal", OIC) || shader.Contains("decal", OIC);
        if (b.BlendEnable && alphaCutoff is { } floor && (decal || floor < SoftAlphaFloorMax))
            return (MaterialRenderMode.TransparentCutout, doubleSided);

        // Alpha-tested cutout: either the shader name says so, OR the material carries an AlphaTestValue param.
        // The latter catches converted materials (NVR mods use SRX_Blend_Master + AlphaTestValue + blendEnable)
        // that would otherwise be mis-classified as plain transparent and render as ghosts. Cutout stays opaque
        // in the depth buffer (discard the hard cutoff), so foliage reads correctly regardless of blendEnable.
        if (alphaCutoff is not null || shader.Contains("AlphaTest", OIC) || shader.Contains("Cutout", OIC))
            return (MaterialRenderMode.Cutout, doubleSided);

        // Anything the .bin marks blendEnable (glass, water/flowmap, decals, dynamic effects) composites over
        // the scene. (Observed SR/HA data blends with SrcAlpha/InvSrcAlpha; we treat all as alpha-blend.)
        if (b.BlendEnable)
            return (MaterialRenderMode.Transparent, doubleSided);

        // Champion skins historically relied on RiotApprox's global alpha-test discard; keep that (Cutout) so
        // their alpha'd parts stay cut. Map static geometry with no blend flag is genuinely Opaque (and must
        // NOT discard — that was the latent over-cut on solid ground).
        return (sourceKind == MaterialSourceKind.ChampionSkin
            ? (MaterialRenderMode.Cutout, doubleSided)
            : (MaterialRenderMode.Opaque, doubleSided));
    }

    /// <summary>Classify every named material in a map .materials.bin. Returns material name → profile
    /// (only names present in <paramref name="materialNames"/>). Never throws — returns what it can.</summary>
    public static Dictionary<string, MaterialProfile> ForMapMaterials(byte[] materialsBin, IEnumerable<string> materialNames, Func<uint, string?> resolve)
    {
        var wanted = new HashSet<string>(materialNames.Where(n => !string.IsNullOrEmpty(n)), StringComparer.OrdinalIgnoreCase);
        var map = new Dictionary<string, MaterialProfile>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var doc = MaterialDocument.Parse(materialsBin, resolve);
            foreach (var b in doc.Materials)
                if (wanted.Contains(b.Name)) map[b.Name] = b.Profile;
        }
        catch { /* best effort */ }
        return map;
    }
}
