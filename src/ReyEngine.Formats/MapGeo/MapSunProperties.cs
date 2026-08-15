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
    public Vector4 FogColor { get; init; } = Vector4.One;
    public Vector2 FogStartAndEnd { get; init; }

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
    /// takes 0 here, which is the single sharpest correlation with "SR map geometry casts no sun shadow".
    /// That correlation is NOT proof of causation: the name equally supports a penumbra/soft-shadow radius
    /// reading, and nothing in the shader disassembly names this constant. Treat 75 as the value to TEST,
    /// evidenced by Map12/jade and 150 other shipped maps, not as a documented switch.</para></summary>
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

    /// <summary>
    /// M145: the fog range as usable positive world distances. Riot stores <c>fogStartAndEnd</c> in a
    /// view-space depth convention — negative, and with the far value "smaller" (Twisted Treeline ships
    /// <c>(-10000, -50000)</c>, i.e. fog from 10000 to 50000). Normalise by magnitude so callers get
    /// (near, far) regardless of sign or ordering. False when the map authored no usable range.
    /// </summary>
    public bool TryGetFogRange(out float start, out float end)
    {
        float a = Math.Abs(FogStartAndEnd.X), b = Math.Abs(FogStartAndEnd.Y);
        start = Math.Min(a, b);
        end = Math.Max(a, b);
        return end > start && end > 0f;
    }

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
                        FogColor = Vec4(s, "fogColor", Vector4.One),
                        FogStartAndEnd = Vec2(s, "fogStartAndEnd", Vector2.Zero),
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
}
