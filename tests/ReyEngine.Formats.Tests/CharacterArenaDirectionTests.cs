using System.Numerics;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.Formats.Tests;

public sealed class CharacterArenaDirectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShortCastStartsAtAgeZeroWithOrWithoutAmbientMapParticles(bool withAmbient)
    {
        const string final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
        if (!Directory.Exists(final)) return;
        using var cache = ReyEngine.Formats.Shaders.ShaderCacheReader.Open(final, null, out _);
        Assert.NotNull(cache);
        using var renderer = new ReyEngine.Rendering.D3D11.ShaderPreviewRenderer();
        Assert.True(renderer.Initialize(out var error), error);
        var emitter = new VfxEmitterDefinition("short-cast", VfxCurveF.Const(1), VfxCurveF.Const(0.25f),
            0.2f, 0, 0, true, false, 3, VfxCurve3.Const(new Vector3(50)), null,
            VfxCurve4.Const(Vector4.One), null, null, null, null, VfxCurve3.Const(Vector3.Zero),
            "assets/test/white.dds", Vector2.One, 1, false, false);
        var system = new VfxSystemDefinition(1, "short-cast", "", new[] { emitter });
        var cast = new VfxPlaybackItem(system, Matrix4x4.Identity,
            new ReyEngine.Core.Decoding.TextureImage?[] { new(1, 1, new byte[] { 255, 255, 255, 255 }) });
        var map = withAmbient ? new VfxPlayback(new[] { cast with
        {
            System = system with { PathHash = 2, Emitters = new[] { emitter with
                { Name = "ambient", EmitterLifetime = null, ParticleLifetime = VfxCurveF.Const(10) } } }
        } }, true) : null;
        var playback = VfxPlaybackSim.Combine(map, new VfxPlayback(new[] { cast }));
        var driver = new D3D11MapParticles(renderer, cache);
        driver.SetPlayback(playback);
        var view = Matrix4x4.CreateLookAt(new Vector3(0, 100, 300), Vector3.Zero, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(0.9f, 1, 1, 5000);
        driver.Tick(0.016f, view, view * projection, new Vector3(0, 100, 300), 300);
        var sims = (Dictionary<VfxPlaybackItem, ReyEngine.Rendering.Vfx.VfxParticleSimulator>)
            typeof(D3D11MapParticles).GetField("_sims", System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance)!.GetValue(driver)!;
        var liveCast = Assert.Single(sims.Where(pair => pair.Key.System.PathHash == 1)).Value.Emitters[0];
        Assert.InRange(liveCast.EmitterAge, 0.015f, 0.017f);
        Assert.Equal(1, liveCast.InstanceCount);
        Assert.True(driver.QuadsRequested > 0, driver.BuildReport);
    }

    private static VfxPlaybackItem Item(Matrix4x4 transform) => new(
        new VfxSystemDefinition(1, "direction", "", Array.Empty<VfxEmitterDefinition>()),
        transform, Array.Empty<ReyEngine.Core.Decoding.TextureImage?>());

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, -1)]
    [InlineData(1, 0)]
    [InlineData(-1, 0)]
    [InlineData(1, -1)]
    public void CharacterAndAttachedCastShareWorldForwardAtMapCoordinates(float x, float z)
    {
        var direction = Vector3.Normalize(new Vector3(x, 0, z));
        var caster = new Vector3(7500, 80, 7500);
        var preview = new MeshPreviewViewModel
        { CharacterPosition = caster, CharacterYaw = MathF.Atan2(x, z) };
        preview.ControlMode = true;
        try
        {
            var modelForward = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, preview.ModelWorld));
            var cast = Item(VfxCastFrame.Toward(caster, caster + direction * 500, caster))
                with { AttachBone = "caster", PreserveCastDirection = true };
            // A locator's authored root rotation must not replace the spell's aim.
            var bone = Matrix4x4.CreateRotationY(0.8f) * Matrix4x4.CreateTranslation(12, 50, 9);
            var placed = VfxPlaybackSim.AttachmentTransform(cast, bone, preview.ModelWorld);
            Assert.True(Vector3.Distance(direction, modelForward) < 0.0001f);
            Assert.True(Vector3.Distance(modelForward, VfxCastFrame.WorldForward(placed)) < 0.0001f);
            Assert.Equal(Vector3.Transform(bone.Translation, preview.ModelWorld), placed.Translation);
            Assert.True(Vector3.Dot(Vector3.TransformNormal(new Vector3(0, 0, 200), placed), direction) > 199);
            Assert.Equal(caster, preview.CharacterPosition);
        }
        finally { preview.StopControl(); }
    }

    [Fact]
    public void AuthoredBoneEffectsRetainTheirFullBoneFrame()
    {
        var bone = Matrix4x4.CreateRotationY(1) * Matrix4x4.CreateTranslation(1, 2, 3);
        var model = Matrix4x4.CreateRotationY(2) * Matrix4x4.CreateTranslation(7500, 0, 7500);
        Assert.Equal(bone * model, VfxPlaybackSim.AttachmentTransform(Item(Matrix4x4.Identity), bone, model));
    }

    [Fact]
    public void CombiningArenaParticlesDoesNotCullCharacterEffectsAtTheirOldBoneOrigin()
    {
        var mapItem = Item(Matrix4x4.CreateTranslation(10000, 0, 10000));
        var spell = Item(Matrix4x4.CreateTranslation(10000, 0, 10000)) with { AttachBone = "caster" };
        var combined = VfxPlaybackSim.Combine(new VfxPlayback(new[] { mapItem }, true),
            new VfxPlayback(new[] { spell }, false));
        Assert.True(combined.CullByCamera);
        Assert.False(VfxPlaybackSim.IsActive(combined.Items[0], Vector3.Zero, 100, Matrix4x4.Identity));
        Assert.True(VfxPlaybackSim.IsActive(combined.Items[1], Vector3.Zero, 100, Matrix4x4.Identity));
        Assert.False(mapItem.SkipCameraCulling);
        Assert.False(spell.SkipCameraCulling);
    }
}
