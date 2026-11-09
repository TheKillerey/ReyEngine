using System.Linq;
using System.Numerics;
using ReyEngine.App.ViewModels;
using ReyEngine.App.Services;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Meshes;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>M895: the Character window draws the champion at its skin's skinScale.</summary>
public sealed class SkinScaleTests
{
    private static byte[] SkinBin(float scale)
    {
        var spec = new CharacterPackageSpec("MyGolem", "MyGolem.skn", "MyGolem.skl", "MyGolem_TX_CM.tex",
            new[] { new CharacterClipSpec("Idle1", "mygolem_idle1.anm", Loop: true) }, SkinScale: scale);
        return CharacterPackageBuilder.Build(spec).Bins.First(b => b.Path.EndsWith("skins/skin0.bin")).Bytes;
    }

    [Fact]
    public void TheScaleComesFromTheChosenSkinBinAndAbsentIsOne()
    {
        Assert.Equal(1.5f, SkinMeshExtractor.SkinScaleOf(SkinBin(1.5f)));
        Assert.Equal(0.8f, SkinMeshExtractor.SkinScaleOf(SkinBin(0.8f)));   // a chroma's own bin is just another bin
        Assert.Equal(1f, SkinMeshExtractor.SkinScaleOf(SkinBin(1f)));       // 1 is written as absent
        Assert.Equal(1f, SkinMeshExtractor.SkinScaleOf(null));
        Assert.Equal(1f, SkinMeshExtractor.SkinScaleOf(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void TheModelTransformScalesBonePositionsAndBadValuesFallBackToOne()
    {
        var vm = new MeshPreviewViewModel();
        var bone = new Vector3(0f, 100f, 20f);
        Assert.Equal(bone, Vector3.Transform(bone, vm.ModelWorld));
        vm.SetSkinScale(1.5f);
        Assert.Equal(bone * 1.5f, Vector3.Transform(bone, vm.ModelWorld));
        vm.SetSkinScale(float.NaN);
        Assert.Equal(1f, vm.SkinScale);
        vm.SetSkinScale(-2f);
        Assert.Equal(1f, vm.SkinScale);
    }

    [Fact]
    public void AnAttachedSystemStandsOnTheScaledBoneButKeepsItsOwnSize()
    {
        var item = (VfxPlaybackItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(VfxPlaybackItem));
        var bone = Matrix4x4.CreateTranslation(0f, 100f, 0f);
        var model = Matrix4x4.CreateScale(1.5f);
        var t = VfxPlaybackSim.AttachmentTransform(item, bone, model, 1.5f);
        Assert.Equal(150f, t.Translation.Y, 3);
        Assert.Equal(1f, t.M11, 3);
    }
}
