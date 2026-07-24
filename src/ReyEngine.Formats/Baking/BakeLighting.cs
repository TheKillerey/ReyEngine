using System.Numerics;

namespace ReyEngine.Formats.Baking;

/// <summary>One point light as the baker sees it. Mirrors Rendering.PointLight but lives here so the
/// Formats layer stays free of a Rendering reference.</summary>
public readonly record struct BakePointLight(Vector3 Position, Vector3 Color, float Radius, float Intensity);

/// <summary>M158: the light model a bake evaluates, deliberately IDENTICAL to the viewport shader's
/// (ViewportMeshRenderer's fragment stage). If these two ever disagree the bake stops matching the
/// preview, which is the one thing a lightmap baker must never do — so every clamp and every falloff
/// term below is copied from the renderer, with the source line noted.</summary>
public sealed class BakeLighting
{
    /// <summary>Unit vector pointing TOWARD the sun. NOTE the direction convention: MapSunProperties
    /// .sunDirection already points at the sun (its default is +Y, straight up); only the SHADER flips it
    /// (uLight is the ray direction, hence its normalize(-uLight)). So this is the sun property
    /// normalized, NOT negated — negating it would aim every shadow ray underground and bake a
    /// sky-only, sunless map.</summary>
    public Vector3 DirectionToSun { get; init; } = Vector3.UnitY;

    /// <summary>Sun colour, pre-clamped like SetSunLighting does ([0,8] per channel).</summary>
    public Vector3 SunColor { get; init; } = new(0.75f);

    /// <summary>Sky/ambient term, already multiplied by skyLightScale and clamped ([0,8]).</summary>
    public Vector3 SkyLight { get; init; } = new(0.35f);

    /// <summary>MapSunProperties.lightMapColorScale, as the renderer clamps it (SetLightmapScale,
    /// [0.05, 8]). The renderer MULTIPLIES the sampled atlas by this, so the baker must DIVIDE by it —
    /// otherwise the preview is scale-squared too bright (2x2 = 4x on live Map12).</summary>
    public float LightMapColorScale { get; init; } = 1f;

    public IReadOnlyList<BakePointLight> PointLights { get; init; } = Array.Empty<BakePointLight>();

    /// <summary>Global point-light multiplier (SetLightIntensity, [0,8]).</summary>
    public float LightIntensity { get; init; } = 1f;
    /// <summary>Global radius multiplier (SetLightRadiusScale, [0.01,40]).</summary>
    public float LightRadiusScale { get; init; } = 1f;
    /// <summary>Master XZ spread about the origin (SetLightPositionScale, [0.05,20]).</summary>
    public float LightPositionScale { get; init; } = 1f;
    /// <summary>Per-axis XZ fine scale applied on top of the master spread.</summary>
    public Vector2 LightPositionScaleXZ { get; init; } = Vector2.One;
    /// <summary>World-space XZ translate applied after scaling.</summary>
    public Vector2 LightPositionOffset { get; init; } = Vector2.Zero;

    /// <summary>Trace shadow rays for the sun. Opt-in: it is by far the most expensive part of a bake,
    /// and a lighting-only preview pass wants it off.</summary>
    public bool SunShadows { get; init; } = true;
    /// <summary>Trace shadow rays for point lights.</summary>
    public bool PointLightShadows { get; init; } = true;

    /// <summary>Resolve a light's world position exactly as the shader's uLightPos* uniforms do — the
    /// same scale/offset must be applied here or baked lights land somewhere else than preview lights.</summary>
    public Vector3 ResolvePosition(in BakePointLight l)
    {
        var p = l.Position;
        return new Vector3(
            p.X * LightPositionScale * LightPositionScaleXZ.X + LightPositionOffset.X,
            p.Y,
            p.Z * LightPositionScale * LightPositionScaleXZ.Y + LightPositionOffset.Y);
    }

    public float ResolveRadius(in BakePointLight l) => l.Radius * LightRadiusScale;

    /// <summary>0 = the classic (1-t)^2 falloff, 1 = (1-t^2)^2. Blending toward the latter holds the
    /// light further out and lands far more gently, so its rim fades instead of drawing a visible
    /// terminator. Must stay identical to the shader's uLightFalloffSoftness blend.</summary>
    public float FalloffSoftness { get; init; }

    /// <summary>The shared falloff curve. THE single definition used by the atlas bake, the lightgrid
    /// probes and (mirrored in GLSL) the viewport — so all three agree by construction.</summary>
    public float Attenuation(float dist, float radius)
    {
        if (radius <= 0f || dist >= radius) return 0f;
        float t = dist / radius;
        float sharp = 1f - t;      sharp *= sharp;
        float soft = 1f - t * t;   soft *= soft;
        return sharp + (soft - sharp) * Math.Clamp(FalloffSoftness, 0f, 1f);
    }

    /// <summary>Build the lighting model straight from the renderer-facing values the viewport is using,
    /// applying the SAME clamps so a bake can never exceed what the preview would have shown.</summary>
    public static BakeLighting FromViewport(
        Vector3 sunDirectionTowardSun, Vector3 sunColor, Vector3 skyLightColor, float skyLightScale,
        float lightMapColorScale, IReadOnlyList<BakePointLight> lights,
        float lightIntensity, float lightRadiusScale, float lightPositionScale,
        Vector2 lightPositionScaleXZ, Vector2 lightPositionOffset,
        bool sunShadows = true, bool pointLightShadows = true, float falloffSoftness = 0f)
    {
        var dir = sunDirectionTowardSun.LengthSquared() < 1e-6f
            ? new Vector3(0.4f, 0.85f, 0.45f)                 // SetSunLighting's own fallback
            : Vector3.Normalize(sunDirectionTowardSun);
        var max8 = new Vector3(8f);
        return new BakeLighting
        {
            DirectionToSun = dir,
            SunColor = Vector3.Clamp(sunColor, Vector3.Zero, max8),
            SkyLight = Vector3.Clamp(skyLightColor * Math.Clamp(skyLightScale, 0f, 8f), Vector3.Zero, max8),
            LightMapColorScale = Math.Clamp(lightMapColorScale, 0.05f, 8f),
            PointLights = lights,
            LightIntensity = Math.Clamp(lightIntensity, 0f, 8f),
            LightRadiusScale = Math.Clamp(lightRadiusScale, 0.01f, 40f),
            LightPositionScale = Math.Clamp(lightPositionScale, 0.05f, 20f),
            LightPositionScaleXZ = lightPositionScaleXZ,
            LightPositionOffset = lightPositionOffset,
            SunShadows = sunShadows,
            PointLightShadows = pointLightShadows,
            FalloffSoftness = Math.Clamp(falloffSoftness, 0f, 1f),
        };
    }
}
