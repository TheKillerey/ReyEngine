using System.Numerics;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.App.Services;

/// <summary>
/// <para>M807: the map's sun in the form the viewport RENDERS it, computed without a view model - what the Content Browser's
/// map thumbnails hand the renderer.</para>
///
/// <para>The view model reads a map's authored <see cref="MapSunProperties"/> into the lighting panel's sliders
/// (<c>ApplySunProperties</c>: colours clamped to 0..1, intensities to 0..8) and folds them back into
/// <c>CurrentSunProperties</c> (<c>RebuildSun</c>: M451's render form, the intensity multiplied into the colour and the
/// scale pinned to 1). With nobody touching a slider that round trip is this function, and a test holds the two equal.</para>
/// </summary>
public static class MapSunRender
{
    /// <summary>The sun used when a map has none (and before any map): the renderer's own defaults. The view model's
    /// <c>NoMapSun</c> returns this very value - one definition, since M761 found two that had drifted.</summary>
    public static MapSunProperties NoMapSun() => new()
    {
        SunDirection = new Vector3(0.4f, 0.85f, 0.45f),
        SunColor = new Vector4(0.75f, 0.75f, 0.75f, 1f),
        SkyLightColor = new Vector4(0.35f, 0.35f, 0.35f, 1f),
        SkyLightScale = 1f,
        FogEnabled = false,
        FogStartAndEnd = Vector2.Zero,
    };

    /// <summary>The render form of an authored sun (null = the map has none).</summary>
    public static MapSunProperties RenderForm(MapSunProperties? authored)
    {
        var b = authored ?? NoMapSun();
        double sunIntensity = Math.Clamp((double)b.SunIntensityScale, 0.0, 8.0);
        double skyIntensity = Math.Clamp((double)b.SkyLightScale, 0.0, 8.0);
        return b with
        {
            SunColor = new Vector4((float)(Clamp01(b.SunColor.X) * sunIntensity), (float)(Clamp01(b.SunColor.Y) * sunIntensity),
                (float)(Clamp01(b.SunColor.Z) * sunIntensity), 1f),
            SunIntensityScale = 1f,
            SkyLightColor = new Vector4((float)Clamp01(b.SkyLightColor.X), (float)Clamp01(b.SkyLightColor.Y), (float)Clamp01(b.SkyLightColor.Z), 1f),
            SkyLightScale = (float)skyIntensity,
            SunDirection = b.SunDirection,
            HorizonColor = new Vector4((float)Clamp01(b.HorizonColor.X), (float)Clamp01(b.HorizonColor.Y), (float)Clamp01(b.HorizonColor.Z), 1f),
            GroundColor = new Vector4((float)Clamp01(b.GroundColor.X), (float)Clamp01(b.GroundColor.Y), (float)Clamp01(b.GroundColor.Z), 1f),
            FogColor = new Vector4((float)Clamp01(b.FogColor.X), (float)Clamp01(b.FogColor.Y), (float)Clamp01(b.FogColor.Z), 1f),
            FogStartAndEnd = b.FogStartAndEnd,
            FogEnabled = b.FogEnabled,
            FogAlternateColor = new Vector4((float)Clamp01(b.FogAlternateColor.X), (float)Clamp01(b.FogAlternateColor.Y),
                (float)Clamp01(b.FogAlternateColor.Z), 1f),
            FogEmissiveRemap = b.FogEmissiveRemap,
            FogLowQualityModeEmissiveRemap = b.FogLowQualityModeEmissiveRemap,
        };
    }

    /// <summary>The lightmap multiplier the viewport publishes for a sun (<c>CurrentLightmapScale</c>).</summary>
    public static float LightmapScale(MapSunProperties? authored) => authored?.LightMapColorScale ?? 1f;

    private static double Clamp01(double v) => Math.Clamp(v, 0.0, 1.0);
}
