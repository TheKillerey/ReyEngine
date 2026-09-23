using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.MapGeo;

/// <summary>
/// M45: the map's sun/atmosphere component, embedded in the map materials.bin's MapContainer object
/// (components[]). Live maps (Map12 Bloom etc.) carry real values here — most importantly
/// <see cref="LightMapColorScale"/> (e.g. 2.0), the multiplier the game applies to the baked lightmap.
/// Without it the whole lightmapped map renders too dark. Null when the bin has no MapContainer/sun
/// component (e.g. older mod bins) — callers fall back to neutral defaults.
/// </summary>
public sealed record MapSunProperties
{
    public Vector4 SunColor { get; init; } = Vector4.One;
    /// <summary>M451: the sun's own strength multiplier, separate from its colour. Authored in shipped
    /// maps (map12/jade carries 0.5) — the game applies it while a viewport that ignores it renders the
    /// sun up to 2x off, which is exactly the "not the same as in game" report.</summary>
    public float SunIntensityScale { get; init; } = 1f;
    public Vector3 SunDirection { get; init; } = new(0f, 1f, 0f);
    public Vector4 SkyLightColor { get; init; } = Vector4.One;
    public float SkyLightScale { get; init; } = 1f;
    public float LightMapColorScale { get; init; } = 1f;
    public Vector4 HorizonColor { get; init; } = Vector4.One;
    public Vector4 GroundColor { get; init; } = Vector4.One;
    // ---------------------------------------------------------------- M759: the environment fog, all six
    //
    // Measured from Riot's compiled map shaders (staticmesh/defaultenv_flat ps blob 114,
    // staticmesh/mantis_env_baked_pbr ps blob 9), and it is HEIGHT fog, not distance fog - the fog that
    // fills the void under a map's edge. With t = saturate((worldY - end) / (start - end)):
    //
    //   legacy : a = max((1/exp2(smoothstep(t) * 2.88539) - 0.135335) * 1.156518, 0)   // 1 at end, 0 at start
    //            fog = lerp(fogColor, fogAlternateColor, a);  pixel = lerp(lit, fog, a)
    //   Mantis : s = smoothstep(t);  fog = lerp(fogAlternateColor, fogColor, s);  pixel = lerp(fog, lit, s)
    //            emissive is added back weighted by saturate(s*s + ENV_FOG_..._EMISSIVE_REMAP.z)
    //
    // So above `start` a pixel is clear, and toward `end` (further down) it sinks into the alternate colour.
    // Summoner's Rift authors (0, -19000): clear at ground level, gone into the void 19,000 units below.
    //
    // Defaults are the meta schema's, the value the game uses when a map leaves the field out - jade
    // authors fogColor and no range, so it runs (0, -2000). 153 of 201 shipped sun blocks author
    // fogEnabled = false.

    /// <summary>fogEnabled. False draws no environment fog at all. Schema default true.</summary>
    public bool FogEnabled { get; init; } = true;
    /// <summary>fogColor: the fog just below <c>start</c>. Schema default (0.2, 0.2, 0.4, 1).</summary>
    public Vector4 FogColor { get; init; } = new(0.2f, 0.2f, 0.4f, 1f);
    /// <summary>fogAlternateColor: the fog at <c>end</c> and below. Schema default (0.1, 0.1, 0.2, 1).</summary>
    public Vector4 FogAlternateColor { get; init; } = new(0.1f, 0.1f, 0.2f, 1f);
    /// <summary>fogStartAndEnd: world HEIGHTS, start above end - fog begins below X and is complete at Y.
    /// Schema default (0, -2000).</summary>
    public Vector2 FogStartAndEnd { get; init; } = new(0f, -2000f);
    /// <summary>fogEmissiveRemap: how much emissive survives inside the fog (the Mantis shader's
    /// saturate(s*s + remap) weight; 1.9 keeps it whole). Schema default 1.9.</summary>
    public float FogEmissiveRemap { get; init; } = 1.9f;
    /// <summary>fogLowQualityModeEmissiveRemap: the same, in the game's low-quality mode. Saved, not
    /// previewed - the preview draws high quality. Schema default 0.02.</summary>
    public float FogLowQualityModeEmissiveRemap { get; init; } = 0.02f;

    /// <summary>
    /// M759: the environment-fog constants a map shader reads, from this sun - or the "no fog" constants
    /// when fog is off (the viewport toggle, or the map's own fogEnabled). No fog is a range every real
    /// height is above: t saturates to 1 and both formulas return the lit pixel untouched.
    /// <c>.z</c> is filled from fogEmissiveRemap, the name's match; the shader uses it only as the emissive
    /// floor above, where 1.9 and the old 1 both keep emissive whole. <c>.w</c> is read by no shader
    /// measured and stays 1.
    /// </summary>
    public static (Vector4 StartEndScaleRemap, Vector3 Color, Vector3 AltColor) EnvFogConstants(MapSunProperties? sun, bool show)
    {
        if (!show || sun is null || !sun.FogEnabled)
            return (new Vector4(-1e9f + 1e4f, -1e9f, sun?.FogEmissiveRemap ?? 1.9f, 1f), Vector3.Zero, Vector3.Zero);
        return (new Vector4(sun.FogStartAndEnd.X, sun.FogStartAndEnd.Y, sun.FogEmissiveRemap, 1f),
                new Vector3(sun.FogColor.X, sun.FogColor.Y, sun.FogColor.Z),
                new Vector3(sun.FogAlternateColor.X, sun.FogAlternateColor.Y, sun.FogAlternateColor.Z));
    }

    /// <summary>M759: how much fog the LEGACY shader puts on a pixel at <paramref name="worldY"/>, 0..1 -
    /// the expression of defaultenv_flat ps blob 114, for tests and the GL viewport's port.</summary>
    public static float LegacyFogAmount(Vector2 startAndEnd, float worldY)
    {
        float t = Math.Clamp((worldY - startAndEnd.Y) * (1f / (startAndEnd.X - startAndEnd.Y)), 0f, 1f);
        float s = t * t * (3f - 2f * t);
        float a = (1f / MathF.Pow(2f, s * 2.88539f) - 0.135335f) * 1.156518f;
        return MathF.Max(a, 0f);
    }

    // ---------------------------------------------------------------- M467: the sun SHADOW half
    //
    // The report was that in game, shadows land on characters and mobs but map geometry casts none — and
    // the search for a per-material or per-mesh "cast shadows" flag comes up empty. It is empty because
    // there isn't one: a sweep of the 5,340-class meta database finds `castShadows` on exactly one class,
    // SkinMeshDataProperties (the CHARACTER mesh, default true). No map class and no material class has a
    // counterpart. What the map side has instead is these four, and ReyEngine has never read any of them.
    //
    // Not a cook limitation, which was the competing explanation and is now ruled out:
    // defaultenv_flat.ps-dx11 cooks 48 distinct GENERATE_SHADOW_MAP blobs, so the shader half of drawing map
    // geometry into the sun shadow map ships. Defaults are from the meta schema, not chosen.

    /// <summary>Sun shadow coverage radius. Schema default <b>0</b>.
    ///
    /// <para><b>176 of 207 shipped MapSunProperties author this</b>, overwhelmingly to 75 (151 of them;
    /// then 100 x12, 15 x4, 250, 150). <b>Map11 — Summoner's Rift — authors none of the four</b>, so it
    /// takes 0 here.
    ///
    /// <para><b>M468 — REFUTED as a cast switch.</b> Setting it to 75 on a real map and testing in the
    /// client produced no map-geometry shadows. The correlation was real and the causal reading was wrong,
    /// which is why it was labelled a correlation. <b>Static map geometry never casts a real-time sun
    /// shadow in League</b>: its shadows are baked into the lightmap's ALPHA channel, which
    /// <c>defaultenv_flat</c> blob 226 line 200 reads as <c>shadow = min(realtimePCF, lightmap.w)</c>.
    /// A map whose terrain casts nothing needs a BAKE, not this field. The value most likely controls the
    /// soft-shadow penumbra radius; that remains unmeasured, and no shader constant is named after it.
    /// </para></summary>
    public float SunRadiusForShadows { get; init; }

    /// <summary>Multiplier on sun shadow darkness. Schema default 1. Authored by 30 of 207 (0.05, 0.4, 0,
    /// 0.01, 0.1, 0.5) and by 131 of 173 MapLightingVolumes, so it is a live per-region control.</summary>
    public float ScaleSunShadowIntensity { get; init; } = 1f;

    /// <summary>Depth bias for the sun shadow comparison. Schema default 0.0006. <b>Authored by 0 of 207
    /// shipped maps</b> — every map takes the default, so a non-default here is unlike anything Riot
    /// ships.</summary>
    public float ShadowBias { get; init; } = 0.0006f;

    /// <summary>Ratio of world surface area to shadow-map area — i.e. how much world one shadow texel
    /// covers. Schema default 0.05; authored by 17 of 207 (0.1, 0.02, 0.025, 0.015, 0.03, 1).</summary>
    public float SurfaceAreaToShadowMapScale { get; init; } = 0.05f;

    // M759: TryGetFogRange is gone. It read fogStartAndEnd as positive camera DISTANCES (M145), which no
    // shader does - the values are world heights - and the GL viewport's distance fog built on it tinted
    // Summoner's Rift blue with zoom where the game draws nothing.

    /// <summary>Find the MapContainer's MapSunProperties component. Never throws; null when absent.</summary>
    public static MapSunProperties? Extract(byte[] materialsBin)
    {
        try
        {
            var tree = SafeBinTree.Parse(materialsBin);
            uint containerCls = HashAlgorithms.Fnv1a("MapContainer");
            uint sunCls = HashAlgorithms.Fnv1a("MapSunProperties");
            foreach (var obj in tree.Objects.Values)
            {
                if (obj.ClassHash != containerCls) continue;
                if (Field(obj.Properties, "components") is not BinTreeContainer comps) continue;
                foreach (var el in comps.Elements)
                {
                    if (el is not BinTreeStruct s || s.ClassHash != sunCls) continue;
                    return new MapSunProperties
                    {
                        SunColor = Vec4(s, "sunColor", Vector4.One),
                        SunIntensityScale = F32(s, "SunIntensityScale", 1f),
                        SunDirection = Vec3(s, "sunDirection", new Vector3(0f, 1f, 0f)),
                        SkyLightColor = Vec4(s, "skyLightColor", Vector4.One),
                        SkyLightScale = F32(s, "skyLightScale", 1f),
                        LightMapColorScale = F32(s, "lightMapColorScale", 1f),
                        HorizonColor = Vec4(s, "horizonColor", Vector4.One),
                        GroundColor = Vec4(s, "groundColor", Vector4.One),
                        // M759: the schema defaults, where M145 read an absent field as white and (0, 0)
                        FogEnabled = Bool(s, "fogEnabled", true),
                        FogColor = Vec4(s, "fogColor", new Vector4(0.2f, 0.2f, 0.4f, 1f)),
                        FogAlternateColor = Vec4(s, "fogAlternateColor", new Vector4(0.1f, 0.1f, 0.2f, 1f)),
                        FogStartAndEnd = Vec2(s, "fogStartAndEnd", new Vector2(0f, -2000f)),
                        FogEmissiveRemap = F32(s, "fogEmissiveRemap", 1.9f),
                        FogLowQualityModeEmissiveRemap = F32(s, "fogLowQualityModeEmissiveRemap", 0.02f),
                        // M467. Defaults are the meta schema's, so an absent field reads back as whatever
                        // the game would use — the same contract every field above already keeps.
                        SunRadiusForShadows = F32(s, "SunRadiusForShadows", 0f),
                        ScaleSunShadowIntensity = F32(s, "ScaleSunShadowIntensity", 1f),
                        ShadowBias = F32(s, "ShadowBias", 0.0006f),
                        SurfaceAreaToShadowMapScale = F32(s, "surfaceAreaToShadowMapScale", 0.05f),
                    };
                }
            }
        }
        catch { /* malformed bin: no sun properties */ }
        return null;
    }

    /// <summary>What a write did, so the caller can report it honestly.</summary>
    public sealed record WriteResult(bool Written, bool CreatedComponent, int FieldsUpdated, int FieldsAdded, string Detail);

    /// <summary>
    /// M450: write this sun/atmosphere back into the bin's <c>MapContainer.components[]</c> — the missing
    /// half of <see cref="Extract"/>. Until now the panel edited a copy the viewport rendered and nothing
    /// could persist.
    ///
    /// <para>Follows <see cref="MapBakeProperties.Write"/>, the proven writer for a SIBLING component in
    /// the same container: find the struct by class hash (index varies across shipped maps), field name
    /// hashes are FNV-1a over the LOWERCASED name (Fnv1aRaw does not match — measured there), and a
    /// created struct carries NameHash 0 like every shipped container element.</para>
    ///
    /// <para><b>Update vs add.</b> A field that exists is updated in place. A field the component does not
    /// carry is added ONLY when its value differs from the read-side default — <see cref="Extract"/>
    /// returns the default for an absent field anyway, so adding a default-valued field changes nothing
    /// for ReyEngine and only risks meaning something different to the game. Fields this record does not
    /// model (fogAlternateColor, CharacterSunLight*, …) are never touched.</para>
    /// </summary>
    /// <returns>The rewritten bin, or null when it cannot be done (unparseable, no MapContainer).</returns>
    public static byte[]? Write(byte[] materialsBin, MapSunProperties sun, out WriteResult result)
    {
        result = new WriteResult(false, false, 0, 0, "");
        ArgumentNullException.ThrowIfNull(sun);
        BinTree tree;
        try { tree = SafeBinTree.Parse(materialsBin); }
        catch (Exception ex) { result = result with { Detail = $"bin did not parse: {ex.Message}" }; return null; }

        uint containerCls = HashAlgorithms.Fnv1a("MapContainer");
        uint sunCls = HashAlgorithms.Fnv1a("MapSunProperties");
        var defaults = new MapSunProperties();

        foreach (var obj in tree.Objects.Values)
        {
            if (obj.ClassHash != containerCls) continue;
            uint componentsField = Field(obj.Properties, "components")?.NameHash ?? HashAlgorithms.Fnv1a("components");
            if (!obj.Properties.TryGetValue(componentsField, out var prop) || prop is not BinTreeContainer comps) continue;

            var s = comps.Elements.OfType<BinTreeStruct>().FirstOrDefault(e => e.ClassHash == sunCls);
            bool created = false;
            if (s is null)
            {
                // All 207 shipped map bins carry a sun component; creating one is the mod-bin fallback.
                s = new BinTreeStruct(0, sunCls, Array.Empty<BinTreeProperty>());
                if (comps.Elements is IList<BinTreeProperty> list) list.Add(s);
                else obj.Properties[componentsField] =
                    new BinTreeContainer(componentsField, comps.ElementType, comps.Elements.Append(s));
                created = true;
            }

            int updated = 0, added = 0;
            void Set(string name, BinTreeProperty fresh, bool isDefault)
            {
                uint h = HashAlgorithms.Fnv1a(name);
                uint raw = HashAlgorithms.Fnv1aRaw(name);
                bool exists = s.Properties.ContainsKey(h) || s.Properties.ContainsKey(raw);
                if (!exists && isDefault) return;              // absent + default = leave absent
                // One canonical form: the game resolves fields by the lowercased hash, so a stale
                // raw-hash duplicate would be dead weight next to the field we write.
                if (raw != h) s.Properties.Remove(raw);
                s.Properties[h] = fresh;
                if (exists) updated++; else added++;
            }

            Set("sunColor", new BinTreeVector4(HashAlgorithms.Fnv1a("sunColor"), sun.SunColor), sun.SunColor == defaults.SunColor);
            Set("SunIntensityScale", new BinTreeF32(HashAlgorithms.Fnv1a("SunIntensityScale"), sun.SunIntensityScale), sun.SunIntensityScale == defaults.SunIntensityScale);
            Set("sunDirection", new BinTreeVector3(HashAlgorithms.Fnv1a("sunDirection"), sun.SunDirection), sun.SunDirection == defaults.SunDirection);
            Set("skyLightColor", new BinTreeVector4(HashAlgorithms.Fnv1a("skyLightColor"), sun.SkyLightColor), sun.SkyLightColor == defaults.SkyLightColor);
            Set("skyLightScale", new BinTreeF32(HashAlgorithms.Fnv1a("skyLightScale"), sun.SkyLightScale), sun.SkyLightScale == defaults.SkyLightScale);
            Set("lightMapColorScale", new BinTreeF32(HashAlgorithms.Fnv1a("lightMapColorScale"), sun.LightMapColorScale), sun.LightMapColorScale == defaults.LightMapColorScale);
            Set("horizonColor", new BinTreeVector4(HashAlgorithms.Fnv1a("horizonColor"), sun.HorizonColor), sun.HorizonColor == defaults.HorizonColor);
            Set("groundColor", new BinTreeVector4(HashAlgorithms.Fnv1a("groundColor"), sun.GroundColor), sun.GroundColor == defaults.GroundColor);
            Set("fogColor", new BinTreeVector4(HashAlgorithms.Fnv1a("fogColor"), sun.FogColor), sun.FogColor == defaults.FogColor);
            Set("fogStartAndEnd", new BinTreeVector2(HashAlgorithms.Fnv1a("fogStartAndEnd"), sun.FogStartAndEnd), sun.FogStartAndEnd == defaults.FogStartAndEnd);
            // M759: the rest of the fog. fogEnabled is a Bool on this class (not a Flag), as the schema says.
            Set("fogEnabled", new BinTreeBool(HashAlgorithms.Fnv1a("fogEnabled"), sun.FogEnabled), sun.FogEnabled == defaults.FogEnabled);
            Set("fogAlternateColor", new BinTreeVector4(HashAlgorithms.Fnv1a("fogAlternateColor"), sun.FogAlternateColor), sun.FogAlternateColor == defaults.FogAlternateColor);
            Set("fogEmissiveRemap", new BinTreeF32(HashAlgorithms.Fnv1a("fogEmissiveRemap"), sun.FogEmissiveRemap), sun.FogEmissiveRemap == defaults.FogEmissiveRemap);
            Set("fogLowQualityModeEmissiveRemap", new BinTreeF32(HashAlgorithms.Fnv1a("fogLowQualityModeEmissiveRemap"), sun.FogLowQualityModeEmissiveRemap), sun.FogLowQualityModeEmissiveRemap == defaults.FogLowQualityModeEmissiveRemap);
            // M467. Case matters: these four are FNV-1a over the name AS RIOT SPELLS IT, and Riot spells
            // three of them capitalised and surfaceAreaToShadowMapScale lower — same inconsistency
            // SunIntensityScale already has above. The hashes were taken from the shipped hash list, not
            // guessed from a convention.
            Set("SunRadiusForShadows", new BinTreeF32(HashAlgorithms.Fnv1a("SunRadiusForShadows"), sun.SunRadiusForShadows), sun.SunRadiusForShadows == defaults.SunRadiusForShadows);
            Set("ScaleSunShadowIntensity", new BinTreeF32(HashAlgorithms.Fnv1a("ScaleSunShadowIntensity"), sun.ScaleSunShadowIntensity), sun.ScaleSunShadowIntensity == defaults.ScaleSunShadowIntensity);
            Set("ShadowBias", new BinTreeF32(HashAlgorithms.Fnv1a("ShadowBias"), sun.ShadowBias), sun.ShadowBias == defaults.ShadowBias);
            Set("surfaceAreaToShadowMapScale", new BinTreeF32(HashAlgorithms.Fnv1a("surfaceAreaToShadowMapScale"), sun.SurfaceAreaToShadowMapScale), sun.SurfaceAreaToShadowMapScale == defaults.SurfaceAreaToShadowMapScale);

            var ms = new MemoryStream();
            tree.Write(ms);
            result = new WriteResult(true, created, updated, added,
                (created ? "created the component; " : "") + $"{updated} field(s) updated, {added} added");
            return ms.ToArray();
        }

        result = result with { Detail = "no MapContainer in this bin" };
        return null;
    }

    private static BinTreeProperty? Field(IReadOnlyDictionary<uint, BinTreeProperty> props, string name)
    {
        if (props.TryGetValue(HashAlgorithms.Fnv1aRaw(name), out var p)) return p;
        return props.TryGetValue(HashAlgorithms.Fnv1a(name), out p) ? p : null;
    }

    private static Vector4 Vec4(BinTreeStruct s, string name, Vector4 def) =>
        Field(s.Properties, name) is BinTreeVector4 v ? v.Value : def;
    private static Vector3 Vec3(BinTreeStruct s, string name, Vector3 def) =>
        Field(s.Properties, name) is BinTreeVector3 v ? v.Value : def;
    private static Vector2 Vec2(BinTreeStruct s, string name, Vector2 def) =>
        Field(s.Properties, name) is BinTreeVector2 v ? v.Value : def;
    private static float F32(BinTreeStruct s, string name, float def) =>
        Field(s.Properties, name) is BinTreeF32 v ? v.Value : def;
    /// <summary>M759: a Bool, or a BitBool should a writer have used one.</summary>
    private static bool Bool(BinTreeStruct s, string name, bool def) => Field(s.Properties, name) switch
    {
        BinTreeBool b => b.Value,
        BinTreeBitBool b => b.Value,
        _ => def,
    };
}
