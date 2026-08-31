using ReyEngine.Rendering.D3D11;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M615: the D3D11 renderer can pose a skeleton.
///
/// <para>NOTE, as of M616: nothing drives this yet. The M615 driver lived in the Shader Preview, and that
/// window has been put back exactly as it was — it is a shader-debugging tool and character animation
/// does not belong in it. The palette is therefore an unwired capability until the character preview
/// renders through D3D11. That is a deliberate and STATED intermediate state rather than a quiet one:
/// the contract is pinned here, and the equivalence proof in <see cref="BonePaletteTests"/> — that the
/// palette reproduces the CPU skinner exactly — holds regardless of who calls it.</para>
/// </summary>
public sealed class Dx11AnimationTests
{
    [Fact]
    public void TheRendererTakesABonePalette()
    {
        // The field the whole capability hangs off. Named and typed, so the rename that would orphan the
        // future driver is a failing test rather than a silent bind-pose regression.
        var field = typeof(PreviewSettings).GetField("BonePalette");
        Assert.NotNull(field);
        Assert.Equal(typeof(System.Numerics.Matrix4x4[]), field!.FieldType);
    }

    [Fact]
    public void ThePaletteDefaultsToNullSoNothingElseChangesBehaviour()
    {
        // Null means "keep the M216 constant", which is the bind pose. Every map and every non-skinned
        // draw in the app goes through this path and must be untouched by M615.
        Assert.Null(new PreviewSettings().BonePalette);
    }
}
