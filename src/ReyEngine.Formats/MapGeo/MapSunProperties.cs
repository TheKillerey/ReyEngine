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
    public Vector3 SunDirection { get; init; } = new(0f, 1f, 0f);
    public Vector4 SkyLightColor { get; init; } = Vector4.One;
    public float SkyLightScale { get; init; } = 1f;
    public float LightMapColorScale { get; init; } = 1f;
    public Vector4 HorizonColor { get; init; } = Vector4.One;
    public Vector4 GroundColor { get; init; } = Vector4.One;
    public Vector4 FogColor { get; init; } = Vector4.One;
    public Vector2 FogStartAndEnd { get; init; }

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
                        SunDirection = Vec3(s, "sunDirection", new Vector3(0f, 1f, 0f)),
                        SkyLightColor = Vec4(s, "skyLightColor", Vector4.One),
                        SkyLightScale = F32(s, "skyLightScale", 1f),
                        LightMapColorScale = F32(s, "lightMapColorScale", 1f),
                        HorizonColor = Vec4(s, "horizonColor", Vector4.One),
                        GroundColor = Vec4(s, "groundColor", Vector4.One),
                        FogColor = Vec4(s, "fogColor", Vector4.One),
                        FogStartAndEnd = Vec2(s, "fogStartAndEnd", Vector2.Zero),
                    };
                }
            }
        }
        catch { /* malformed bin: no sun properties */ }
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
