using System.Numerics;

namespace ReyEngine.Formats.Materials;

/// <summary>Which RiotApprox preview profile a material maps to (drives which lighting features apply).</summary>
public enum PreviewProfileKind { Unknown, ChampionSkin, MapStatic, MatCap, Specular }

/// <summary>How a material composites, derived from its technique/pass blend state + shader name (M34).
/// Opaque writes depth solidly; Cutout alpha-tests (discard) but stays opaque; Transparent alpha-blends
/// over the scene (drawn after opaque, no depth write).</summary>
public enum MaterialRenderMode { Opaque, Cutout, Transparent }

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
    Vector4? Tint = null)   // M34: TintColor param (rgba); ONLY applied when the material has no diffuse texture
{
    public static readonly MaterialProfile Default =
        new(PreviewProfileKind.Unknown, false, false, false, false, Vector2.One, Vector2.Zero, 0f, null, null);

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
            var parts = new List<string> { "diffuse" };
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
                _ => "Opaque",
            };
            return DoubleSided ? m + ", double-sided" : m;
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

        var (renderMode, doubleSided) = ClassifyRenderMode(b, sourceKind);

        // TintColor: for sampler-less effect/indicator materials (no diffuse texture) the tint IS the colour
        // and its alpha the opacity (e.g. FaeLights <0,1,1,0.1>). The renderer only applies this on the
        // untextured fallback path, so textured materials that also carry a TintColor are never recoloured.
        Vector4? tint = null;
        if (b.Slots.All(s => !s.SamplerName.Contains("Diffuse", OIC)))   // no diffuse sampler present
            foreach (var p in b.Parameters)
                if (Norm(p.Name) == "tintcolor" && p.TryGetVector4(out var tv)) { tint = tv; break; }

        return new MaterialProfile(kind, rim, specular, emissive, matcap, scale, offset, rotationDeg, scaleSrc, offsetSrc,
            renderMode, doubleSided, tint);
    }

    /// <summary>Derive the compositing mode from the material's technique/pass blend state + shader name (M34).
    /// The shader name is the primary intent signal; blendEnable disambiguates opaque vs alpha-blend.</summary>
    private static (MaterialRenderMode, bool) ClassifyRenderMode(MaterialBinding b, MaterialSourceKind sourceKind)
    {
        // real technique shader (e.g. Shaders/StaticMesh/DefaultEnv_Flat_AlphaTest); may fall back to class name.
        string shader = (b.RenderShader ?? b.ShaderName ?? "");
        // Double-sided is authoritative from the pass's cullEnable (false = double-sided); only when that
        // field is absent do we fall back to the shader-name heuristic.
        bool doubleSided = b.CullEnable is bool cull
            ? !cull
            : shader.Contains("DoubleSided", OIC) || shader.Contains("TwoSided", OIC);

        // AlphaTest shaders cut out (discard) but stay opaque in the depth buffer — do NOT alpha-blend them.
        if (shader.Contains("AlphaTest", OIC) || shader.Contains("Cutout", OIC))
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
