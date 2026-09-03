using System.Numerics;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M635: two things the character window got wrong about a cast spell - where it pointed, and why one of
/// its sprites drew as a grey rectangle on D3D11.
/// </summary>
public sealed class CastFrameAndPaletteTests
{
    private static string? Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    // ===================================================== the cast frame

    [Theory]
    [InlineData(0f, 0f, 500f, 0f)]        // straight along +Z
    [InlineData(0f, 0f, -500f, 0f)]       // straight along -Z
    [InlineData(0f, 0f, 0f, 350f)]        // along +X, the default dummy
    [InlineData(120f, -40f, -300f, 200f)] // arbitrary, from somewhere else
    public void TheSystemsLocalForwardPointsFromCasterToTarget(float fromX, float fromZ, float toX, float toZ)
    {
        var from = new Vector3(fromX, 0f, fromZ);
        var to = new Vector3(toX, 0f, toZ);
        var placement = VfxCastFrame.Toward(from, to, from);

        var expected = Vector3.Normalize(new Vector3(toX - fromX, 0f, toZ - fromZ));
        var actual = VfxCastFrame.WorldForward(placement);
        Assert.Equal(expected.X, actual.X, 4);
        Assert.Equal(0f, actual.Y, 4);
        Assert.Equal(expected.Z, actual.Z, 4);
        Assert.Equal(from, placement.Translation);
    }

    [Fact]
    public void TheLocalForwardIsMinusZAndTheModelsIsPlusZ()
    {
        // The whole point: the two conventions differ by half a turn. A model facing (0,0,1) has yaw 0;
        // a cast system aimed at (0,0,1) is turned by pi so that its -Z lands there.
        Assert.Equal(-Vector3.UnitZ, VfxCastFrame.LocalForward);
        Assert.Equal(MathF.PI, VfxCastFrame.YawToward(Vector3.UnitZ), 5);
        Assert.Equal(MathF.Atan2(1f, 0f) + MathF.PI, VfxCastFrame.YawToward(Vector3.UnitX), 5);
    }

    [Fact]
    public void HeightIsIgnoredBecauseSpellsAreAimedAcrossTheGround()
    {
        var placement = VfxCastFrame.Toward(new Vector3(0f, 0f, 0f), new Vector3(0f, 900f, 400f), Vector3.Zero);
        var fwd = VfxCastFrame.WorldForward(placement);
        Assert.Equal(0f, fwd.Y, 5);
        Assert.Equal(1f, fwd.Z, 4);
    }

    [Fact]
    public void ACoincidentTargetGivesATranslationAndNoInventedDirection()
    {
        var at = new Vector3(10f, 0f, 20f);
        Assert.Equal(Matrix4x4.CreateTranslation(at), VfxCastFrame.Toward(at, at, at));
    }

    [Fact]
    public void RotationOfKeepsTheAimAndDropsThePosition()
    {
        var placement = VfxCastFrame.Toward(Vector3.Zero, new Vector3(300f, 0f, 400f), new Vector3(7f, 8f, 9f));
        var rotation = VfxCastFrame.RotationOf(placement);

        Assert.Equal(Vector3.Zero, rotation.Translation);
        Assert.Equal(VfxCastFrame.WorldForward(placement), VfxCastFrame.WorldForward(rotation));

        // And re-attaching a position - what a missile does every tick - keeps the aim.
        var moved = rotation * Matrix4x4.CreateTranslation(new Vector3(100f, 0f, 100f));
        Assert.Equal(VfxCastFrame.WorldForward(placement), VfxCastFrame.WorldForward(moved));
        Assert.Equal(new Vector3(100f, 0f, 100f), moved.Translation);
    }

    // ===================================================== where it is used

    [Fact]
    public void EverySpellItemIsAimedAndEveryMissileKeepsItsAimInFlight()
    {
        var preview = Source("src", "ReyEngine.App", "ViewModels", "MeshPreviewViewModel.cs");
        if (preview is null) return;
        // Caster and missile systems aim at the dummy; target systems aim back at the caster.
        Assert.Contains("Make(h, caster, dummy, null)", preview);
        Assert.Contains("Make(h, dummy, caster, null)", preview);
        Assert.Contains("Make(h, caster, dummy, dummy)", preview);
        Assert.Contains("BuildItem(def, VfxCastFrame.Toward(at, faceToward, at))", preview);

        // Both renderers re-issue the missile's matrix every tick; both keep the rotation now.
        var d3d = Source("src", "ReyEngine.App", "Services", "D3D11MapParticles.cs");
        var gl = Source("src", "ReyEngine.App", "Views", "ViewportControl.cs");
        if (d3d is null || gl is null) return;
        Assert.Contains("VfxCastFrame.RotationOf(item.Transform)", d3d);
        Assert.Contains("VfxCastFrame.RotationOf(item.Transform)", gl);
        Assert.DoesNotContain("sim.SetWorldTransform(Matrix4x4.CreateTranslation(pos));", gl);
    }

    // ===================================================== the palette strip

    [Fact]
    public void ThePaletteSamplerClampsWhileTheSpritesSamplerWraps()
    {
        // Under WRAP with linear filtering, a lookup at u = 0 - every black texel of an additive sprite -
        // blends the strip's first texel with its LAST. Aatrox's AA_Gradient_RGB runs (53,0,53) to
        // (247,251,132), so the black field of AA_hit_flash came out mid grey and drew as a 500x500
        // rectangle. GL has clamped this slot since M184; D3D11 needed a per-slot mechanism to do it.
        var renderer = Source("src", "ReyEngine.Rendering.D3D11", "ShaderPreviewRenderer.cs");
        var pipeline = Source("src", "ReyEngine.App", "Services", "VfxD3D11EmitterPipeline.cs");
        if (renderer is null || pipeline is null) return;

        Assert.Contains("public HashSet<string>? ClampedSamplers { get; set; }", renderer);
        Assert.Contains("mat.ClampedSamplers is { } clamped && clamped.Contains(smp.Name) ? _linearClamp", renderer);
        Assert.Contains(".Add(slot.Replace(\"__TX\", \"__SMP\", StringComparison.OrdinalIgnoreCase));", pipeline);
    }

    [Fact]
    public void AStageWhoseTextureDidNotResolveIsNotSelectedAtAll()
    {
        // Selecting PALETTIZE_TEXTURES or ALPHA_EROSION and then binding the white stand-in is not a missing
        // effect, it is a solid card: the palette REPLACES rgb with a lookup into white, erosion multiplies
        // alpha by a mask read from white. GL gates both stages on the texture being bound; so does this.
        var pipeline = Source("src", "ReyEngine.App", "Services", "VfxD3D11EmitterPipeline.cs");
        if (pipeline is null) return;
        Assert.Contains("(\"PALETTIZE_TEXTURES\", \"sPalettesTexture\"), (\"ALPHA_EROSION\", \"sAlphaErosionTexture\")", pipeline);
        Assert.Contains("if (defines.ContainsKey(define) && sprites(sampler) is null)", pipeline);
        Assert.Contains("defines.Remove(define);", pipeline);
    }
}
