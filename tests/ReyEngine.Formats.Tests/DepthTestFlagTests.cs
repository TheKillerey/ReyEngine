using System.Numerics;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M711, the third of the ltk-manager readings ported back: <c>miscRenderFlags</c> bit 0 is the engine's
/// DISABLE_ZBUFFER, and an emitter carrying it draws with the depth test off - over everything, rather than
/// being occluded by it. Both renderers tested depth on every particle unconditionally, so a ground decal
/// authored to paint over the terrain it lies on was cut into by that terrain.
///
/// <para>Only the TEST moves. The depth WRITE stays off for every particle, which it already was.</para>
/// </summary>
public sealed class DepthTestFlagTests
{
    private static string? Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static VfxEmitterDefinition Emitter(int? flags) => new(
        Name: "e",
        Rate: VfxCurveF.Const(10f),
        ParticleLifetime: VfxCurveF.Const(1f),
        EmitterLifetime: null,
        ParticleLinger: 0f,
        TimeBeforeFirstEmission: 0f,
        IsSingleParticle: false,
        Disabled: false,
        BlendMode: 1,
        BirthScale: VfxCurve3.Const(new Vector3(10f, 10f, 10f)),
        ScaleOverLife: null,
        BirthColor: VfxCurve4.Const(Vector4.One),
        ColorOverLife: null,
        BirthVelocity: null,
        Acceleration: null,
        BirthRotationalVelocity: null,
        EmitterPosition: VfxCurve3.Const(Vector3.Zero),
        TexturePath: "ASSETS/Test/p.dds",
        TexDiv: new Vector2(1f, 1f),
        NumFrames: 1,
        RandomStartFrame: false,
        IsMeshPrimitive: false,
        Extras: flags is null ? null : new VfxEmitterExtras { MiscRenderFlags = flags });

    [Fact]
    public void OnlyBitZeroTurnsTheDepthTestOff()
    {
        Assert.Equal(0x1, VfxMiscRenderFlags.DisableZBuffer);
        Assert.Equal(0x2, VfxMiscRenderFlags.Projected);
        Assert.Equal(0x4, VfxMiscRenderFlags.DisableFogOfWar);

        Assert.True(VfxMiscRenderFlags.DisablesDepthTest(Emitter(1)));
        Assert.True(VfxMiscRenderFlags.DisablesDepthTest(Emitter(3)));   // 0x1 | 0x2
        Assert.True(VfxMiscRenderFlags.DisablesDepthTest(Emitter(5)));   // 0x1 | 0x4
        Assert.True(VfxMiscRenderFlags.DisablesDepthTest(Emitter(7)));

        // the other two bits are named so the byte is documented, and neither renderer acts on them -
        // their meaning is asserted engine behaviour with no implementation anywhere to read
        Assert.False(VfxMiscRenderFlags.DisablesDepthTest(Emitter(2)));
        Assert.False(VfxMiscRenderFlags.DisablesDepthTest(Emitter(4)));
        Assert.False(VfxMiscRenderFlags.DisablesDepthTest(Emitter(6)));
    }

    [Fact]
    public void AnEmitterThatOmitsTheFieldTestsDepth()
    {
        // absence means the declared default, 0, which is the test left ON - and that is verified rather
        // than assumed: across 2.9 million authored occurrences on this class, not one field ever writes
        // its own declared default, including the three whose default is not zero.
        Assert.False(VfxMiscRenderFlags.DisablesDepthTest(Emitter(null)));
        Assert.False(VfxMiscRenderFlags.DisablesDepthTest(Emitter(0)));
    }

    [Fact]
    public void BothRenderersApplyItAndOnlyToTheTest()
    {
        string? gl = Source("src", "ReyEngine.Rendering", "Vfx", "VfxParticleRenderer.cs");
        Assert.NotNull(gl);
        // per emitter, before the branch that sends meshes and ribbons off to their own paths
        int apply = gl!.IndexOf("ApplyDepthTest(es.Def);", StringComparison.Ordinal);
        int meshBranch = gl.IndexOf("if (es.MeshVao != 0)", StringComparison.Ordinal);
        Assert.True(apply > 0 && apply < meshBranch, "the depth test must be applied before the mesh branch");
        // and the frame's own state is put back, so a viewport that drew particles is not left with the
        // depth test off for whatever draws next
        Assert.Contains("if (_depthTestOff) { _gl.Enable(EnableCap.DepthTest); _depthTestOff = false; }", gl);
        // the write is untouched: still off for every particle
        Assert.Contains("_gl.DepthMask(false);", gl);

        string? pipeline = Source("src", "ReyEngine.App", "Services", "VfxD3D11EmitterPipeline.cs");
        Assert.NotNull(pipeline);
        Assert.Contains("mat.TestsDepth = !ReyEngine.Formats.Vfx.VfxMiscRenderFlags.DisablesDepthTest(e);", pipeline);

        string? dx11 = Source("src", "ReyEngine.Rendering.D3D11", "ShaderPreviewRenderer.cs");
        Assert.NotNull(dx11);
        // one chooser over three states, and both draw sites go through it - the two-state expression was
        // hand-copied at both, which is how a third state gets added to one of them and not the other
        Assert.Contains("private ComPtr<ID3D11DepthStencilState> DepthStateFor(PreviewMaterial mat)", dx11);
        Assert.Equal(2, dx11!.Split("OMSetDepthStencilState(DepthStateFor(mat), 0);").Length - 1);
        Assert.DoesNotContain("mat.WritesDepth || _depthStateNoWrite.Handle is null ? _depthState : _depthStateNoWrite, 0);", dx11);
        // the no-test state is the no-write state with the test off, so the two cannot drift on the
        // comparison function
        Assert.Contains("var dsdNoTest = dsdNoWrite;", dx11);
        Assert.Contains("dsdNoTest.DepthEnable = 0;", dx11);
    }

    [Fact]
    public void TheFieldIsNoLongerFiledOrBadgedAsUnused()
    {
        Assert.DoesNotContain("miscRenderFlags", VfxParkedEmitterFields.Names);
        Assert.Null(VfxPreviewCoverage.IgnoredNote(Core.Hashing.HashAlgorithms.Fnv1a("miscRenderFlags")));
        Assert.True(VfxPreviewCoverage.IsParsed(Core.Hashing.HashAlgorithms.Fnv1a("miscRenderFlags")));
    }
}
