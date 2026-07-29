using System.Numerics;
using ReyEngine.Formats.Baking;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M280: the light-fit sliders (Spread, Scale X/Z, Offset X/Z) moved the preview and did nothing to the
/// bake. The machinery existed end to end - BakeLighting carried the fields and ResolvePosition applied
/// the exact shader formula - but the sole call into it hardcoded scale to One and offset to Zero, so
/// GatherBakeInputs had no way to pass what the panel authored. The footer under the panel promises
/// "these are viewport values - they drive the preview and are what a bake reproduces"; these tests hold
/// it to that.
///
/// The decisive test is the LAST one: it goes through LightBakeService.BuildLighting, the seam that was
/// broken, so re-hardcoding the identity there turns it red. Mutation-checked, not assumed.
/// </summary>
public class LightFitTests
{
    /// <summary>The formula itself: scale about the world ORIGIN, per-axis on top of the master spread,
    /// offset after scaling, height untouched. Mirrors the GLSL at uLightPosScale/ScaleXZ/Offset.</summary>
    [Fact]
    public void FitPosition_ScalesAboutTheOriginThenOffsets_AndNeverTouchesHeight()
    {
        var fitted = BakeLighting.FitPosition(
            new Vector3(100f, 50f, 200f), spread: 2f,
            scaleXZ: new Vector2(3f, 4f), offset: new Vector2(10f, 20f));

        Assert.Equal(100f * 2f * 3f + 10f, fitted.X, 3);   // scale first, offset after
        Assert.Equal(50f, fitted.Y, 3);                    // "height is never touched" - the panel's own words
        Assert.Equal(200f * 2f * 4f + 20f, fitted.Z, 3);
    }

    [Fact]
    public void FitPosition_AtTheDefaultsIsTheIdentity()
    {
        var p = new Vector3(-1234.5f, 42f, 987.6f);
        Assert.Equal(p, BakeLighting.FitPosition(p, 1f, Vector2.One, Vector2.Zero));
    }

    /// <summary>FromViewport must clamp the per-axis scale to the same [0.05, 20] range the GL setter
    /// enforces - otherwise a degenerate slider value would make the bake diverge from the preview that
    /// the user approved.</summary>
    [Fact]
    public void FromViewport_ClampsThePerAxisScaleLikeTheGlSetter()
    {
        var lighting = BakeLighting.FromViewport(
            new Vector3(0f, 1f, 0f), Vector3.One, Vector3.One, 1f, 1f,
            new[] { new BakePointLight(Vector3.Zero, Vector3.One, 100f, 1f) },
            lightIntensity: 1f, lightRadiusScale: 1f, lightPositionScale: 1f,
            lightPositionScaleXZ: new Vector2(0.001f, 100f),
            lightPositionOffset: Vector2.Zero);

        Assert.Equal(0.05f, lighting.LightPositionScaleXZ.X, 4);
        Assert.Equal(20f, lighting.LightPositionScaleXZ.Y, 4);
    }

    /// <summary>
    /// The regression that shipped: the fit reached the preview but not the bake. This goes through
    /// LightBakeService.BuildLighting - the exact call that used to pass Vector2.One / Vector2.Zero -
    /// and requires the resolved position to carry the panel's values. Re-hardcode the identity there
    /// and this fails; verified by doing exactly that (mutation check), not by assumption.
    /// </summary>
    [Fact]
    public void BuildLighting_ForwardsScaleAndOffsetIntoTheResolvedPositions()
    {
        var raw = new BakePointLight(new Vector3(1000f, 10f, -500f), Vector3.One, 300f, 1f);
        var lighting = ReyEngine.App.Services.LightBakeService.BuildLighting(
            sunDirectionTowardSun: new Vector3(0f, 1f, 0f),
            sunColor: Vector3.One, skyColor: Vector3.One, skyScale: 1f, lightMapColorScale: 1f,
            lights: new[] { raw },
            lightIntensity: 1f, lightRadiusScale: 1f,
            lightPositionScale: 1.5f,
            lightPositionScaleXZ: new Vector2(2f, 3f),
            lightPositionOffset: new Vector2(1066f, 457f),      // the values from the report itself
            settings: new BakeSettings());

        var fitted = lighting.ResolvePosition(raw);
        Assert.Equal(1000f * 1.5f * 2f + 1066f, fitted.X, 2);
        Assert.Equal(10f, fitted.Y, 3);
        Assert.Equal(-500f * 1.5f * 3f + 457f, fitted.Z, 2);
    }
}
