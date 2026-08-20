using System.Numerics;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M533: three defects that only appeared on the SECOND port of a map.
///
/// <para>All three shared a shape - something worked once and then silently stopped, with the port still
/// reporting success. That is the expensive kind, because the report is what a user trusts.</para>
/// </summary>
public sealed class LegacyPortRegressionTests
{
    /// <summary>
    /// The alpha classifier never ran. <c>ApplyShaderOptions</c> asked the plan for a sampler named
    /// <c>DiffuseTexture</c>, but <c>BuildMaterialPlans</c> keys every non-terrain plan's diffuse
    /// <c>__diffuse__</c> - only four-blend terrain spells real sampler names. The lookup missed on every
    /// ordinary material, so M528's whole rule was dead code and every Normal material took the
    /// alpha-tested shader whatever its alpha actually held.
    ///
    /// <para>The user reported this as "nothing changed for me" at the time. It was not the wrong rule;
    /// it never executed.</para>
    /// </summary>
    [Theory]
    [InlineData(LegacyAlphaKind.Gradient, false)]   // gloss in alpha - must NOT be alpha-tested
    [InlineData(LegacyAlphaKind.Opaque, false)]     // nothing to test
    [InlineData(LegacyAlphaKind.Cutout, true)]      // a real mask - the test belongs here
    public void TheAlphaClassifierReachesAPorterShapedPlan(LegacyAlphaKind kind, bool expectAlphaTest)
    {
        int calls = 0;
        var ported = LegacyMapPorter.ApplyShaderOptions(
            Result(Plan("__diffuse__")),                 // the key the porter actually writes
            LegacyPortShaderOptions.Defaults,
            null, _ => { },
            _ => { calls++; return kind; });

        Assert.Equal(1, calls);
        Assert.Equal(expectAlphaTest ? LegacyMapPorter.NormalShader : LegacyMapPorter.SolidShader,
            ported.Materials[0].Shader);
    }

    [Fact]
    public void TheRealSamplerNameStillWorksBecauseTerrainPlansUseIt()
    {
        int calls = 0;
        var ported = LegacyMapPorter.ApplyShaderOptions(
            Result(Plan("DiffuseTexture")), LegacyPortShaderOptions.Defaults, null, _ => { },
            _ => { calls++; return LegacyAlphaKind.Gradient; });

        Assert.Equal(1, calls);
        Assert.Equal(LegacyMapPorter.SolidShader, ported.Materials[0].Shader);
    }

    [Fact]
    public void AMaterialWithNoDiffuseAtAllIsLeftOnTheDefaultRatherThanCrashing()
    {
        var ported = LegacyMapPorter.ApplyShaderOptions(
            Result(Plan(null)), LegacyPortShaderOptions.Defaults, null, _ => { },
            _ => LegacyAlphaKind.Gradient);

        Assert.Equal(LegacyMapPorter.NormalShader, ported.Materials[0].Shader);
    }

    private static LegacyMaterialPlan Plan(string? samplerKey) => new(
        "LegacyPort/map2/Normal_test",
        LegacyMaterialRole.Normal,
        LegacyMapPorter.NormalShader,
        samplerKey is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [samplerKey] = "ground.dds" },
        new Dictionary<string, Vector4>(),
        new Dictionary<string, bool>(),
        new Dictionary<string, bool>());

    private static LegacyMapPortResult Result(LegacyMaterialPlan plan) => new(
        Array.Empty<byte>(), Array.Empty<LegacyTextureCopy>(), new[] { plan },
        SourceFile: @"K:\LeagueSandbox\League_Sandbox_Client\LEVELS\Map2\Scene\room.nvr",
        SourceFormat: "NVR", SourceMeshCount: 1, ImportedMeshCount: 1, RemovedBaseMeshCount: 0,
        PreservedRenderRegionMeshCount: 0, SourceMaterialCount: 1, Warnings: Array.Empty<string>());
}
