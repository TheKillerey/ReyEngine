using System;
using System.IO;
using System.Linq;
using ReyEngine.App.ViewModels;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M678: placed props in the GL viewport draw with the same per-submesh material state and texture layers
/// the character window resolves, through the ONE method THE mesh's passes use - so a prop cannot drift
/// from a champion field by field, which is exactly how the diffuse-only prop block drifted for 40
/// milestones. Pinned as text: the renderer needs a GPU, and what matters here is that both paths call
/// the same code.
/// </summary>
public sealed class PropGlMaterialTests
{
    [Fact]
    public void TheMeshAndThePropsSetSubmeshStateThroughOneMethod()
    {
        if (Source("src", "ReyEngine.Rendering", "ViewportMeshRenderer.cs") is not { } text) return;
        // two callers, one method: THE mesh's DrawSubmesh and the prop draw
        Assert.Equal(1, text.Split("ApplySubmesh(in s,").Length - 1);     // THE mesh's DrawSubmesh
        Assert.Equal(1, text.Split("ApplySubmesh(in sub,").Length - 1);   // the prop draw
        // the SubmeshMaterial mapping is shared the same way
        Assert.Contains("ApplyMaterial(ref _submeshes[index], mat);", text);
        Assert.Contains("if (spec.Material is { } mat) ApplyMaterial(ref d, in mat);", text);
        // a submesh without a material keeps the pre-M678 cutout
        Assert.Contains("else { d.AlphaMode = 1; d.AlphaCutoff = 0.35f; }", text);
        // the old fixed diffuse-only uniform block is gone from the prop draw
        Assert.DoesNotContain("_gl.Uniform1(_mAlphaMode, 1); _gl.Uniform1(_mAlphaCutoff, 0.35f);", text);
    }

    [Fact]
    public void ThePropsBlendedSurfacesKeepTheirDepthWriteLikeAChampions()
    {
        if (Source("src", "ReyEngine.Rendering", "ViewportMeshRenderer.cs") is not { } text) return;
        int pass = text.IndexOf("void DrawPropMeshes(bool transparent)", StringComparison.Ordinal);
        Assert.True(pass > 0);
        Assert.Contains("_gl.DepthMask(sub.BlendWritesDepth);", text[pass..]);
        // an empty transparent pass must not touch GL state
        Assert.Contains("if (!any) return;", text[pass..]);
    }

    [Fact]
    public void TheControlUploadsEveryLayerAndTheViewModelResolvesThem()
    {
        var control = Source("src", "ReyEngine.App", "Views", "ViewportControl.cs");
        var vm = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        if (control is null || vm is null) return;
        Assert.Contains("Upload(s.MatCap), Upload(s.MatCapMask), s.Material)", control);
        Assert.Contains("Material = mat.HasAny ? ToSubmeshMaterial(mat.Profile(s.Material)) with { BlendWritesDepth = true } : null,", vm);
        Assert.Contains("Emissive = Tex(mat.ForEmissive(s.Material)),", vm);
    }

    [Fact]
    public void ASubmeshWithoutASkinBinCarriesNoMaterial()
    {
        var sub = new PropSubmesh(0, 3, null);
        Assert.Null(sub.Material);
        Assert.Null(sub.Mask);
        Assert.Null(sub.Emissive);
    }

    private static string? Source(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        return null;
    }
}
