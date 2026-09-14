using System.Numerics;
using ReyEngine.Formats.Environment;

namespace ReyEngine.App.Services;

/// <summary>
/// M729: how the Character Viewer lights its legacy (NVR) map backdrop - resolved ONCE, for both renderers.
///
/// <para><b>Why one function.</b> M727 gave D3D11 its own copy of the GL recipe, typed from ViewportControl's
/// render loop, and the copy was wrong in two places nobody could see from the call sites. GL's
/// <c>ViewportMeshRenderer.SetSunLighting</c> takes the direction TOWARD the sun and negates it into the shader's
/// ray direction, and a ZERO direction does not mean "no sun" there: it swaps in a built-in sun (0.75) and sky
/// (0.35) and ignores the colours it was handed. So Dominion's GL backdrop was lit by that default whatever the
/// Bright slider said, while D3D11 took the zero literally and drew it flat; and Twisted Treeline's night sun,
/// written as the direction light travels, reached GL upside down and D3D11 the right way up. Both renderers now
/// take these numbers, and nothing here ever hands GL a zero direction.</para>
/// </summary>
/// <param name="DirectionToSun">Unit vector from the surface TOWARD the sun - GL's SetSunLighting argument.</param>
/// <param name="CompositeModel">The M142 model: vertex colour as a lightmap, and a composite ground atlas.</param>
/// <param name="VertexBakedScale">The additive <c>base * vColor * scale</c> term; unused by the composite model.</param>
public readonly record struct BackdropLighting(
    Vector3 DirectionToSun, Vector3 SunColor, Vector3 SkyColor, bool CompositeModel, float VertexBakedScale)
{
    /// <summary>The Bright slider's neutral value: at 0.55 every model draws its authored or reference level.</summary>
    public const float NeutralBrightness = 0.55f;

    /// <summary>What LIT_PS multiplies a non-colour-mapped level's vertex colour by: <c>m_Color1.rgb * 4</c>.</summary>
    public const double LegacyVertexLight = 4.0;

    public static BackdropLighting Resolve(bool compositeModel, float brightness, float vertexLight,
        NvrSunSettings? sun, bool useMapSun)
    {
        float k = brightness / NeutralBrightness;

        // Twisted Treeline (M142): the composite atlas and the vertex colours carry the light. This dim night sun
        // only gives shape to meshes that shipped neither (LM_ and decals), and M142.6 meant it to shine from ABOVE.
        if (compositeModel)
            return new(Vector3.Normalize(new Vector3(0.3f, 0.85f, 0.4f)),
                new Vector3(0.30f, 0.30f, 0.36f) * k, new Vector3(0.20f, 0.21f, 0.26f) * k, true, 0f);

        // A level with an authored sun (Dominion): the legacy client's own environment shader, on its path for a
        // level with no colour map. LIT_VS: sun = saturate(-dot(SunDir, N)) * DIRECTIONAL_LIGHT_COLOR * 2.
        // LIT_PS: lighting = sun + vColor * 4 + AMBIENT_COLOR. The torch pools are IN that vertex colour.
        if (sun is not null && useMapSun)
            return new(-sun.SunDirection, sun.SunColor * 2f * k, sun.AmbientColor * k, false, vertexLight);

        // No authored environment: exactly the sun GL already drew here (its zero-direction default), now tunable.
        return new(Vector3.Normalize(new Vector3(0.4f, 0.85f, 0.45f)), new Vector3(0.75f) * k, new Vector3(0.35f) * k,
            false, vertexLight);
    }

    /// <summary>
    /// M729: a backdrop map's lighting defaults - applied when the map CHANGES, the way its placement is (M725), so
    /// a setting the user changed survives reloading the same map for the next skin.
    ///
    /// <para><b>Runtime Light.dat starts OFF wherever the map already bakes those pools</b>: Twisted Treeline into
    /// its composite atlas (M725), and Dominion into its vertex colours - of its 1,027,174 vertices, the 267,279
    /// outside all 462 lights' radii average a colour of 0.000, and LIT_PS has no point-light term at all.
    /// Turning the runtime lights on counts every torch twice. A level with neither keeps the M89 look: runtime
    /// lights on, vertex term off.</para>
    /// </summary>
    public static (bool LightsEnabled, double VertexLight) Defaults(bool compositeModel, bool hasSun, int lightCount) =>
        compositeModel ? (false, 0d)
        : hasSun ? (false, LegacyVertexLight)
        : (lightCount > 0, 0d);
}
