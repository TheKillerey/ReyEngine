using System.Buffers.Binary;
using ReyEngine.Core.Build;
using ReyEngine.Formats.Shaders;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.Formats.Tests;

public class ExperimentalShaderPatchTests
{
    private static ShaderStageToc Toc(params (string Key, string Value)[] pool) => new()
    {
        Path = "assets/shaders/generated/shaders/staticmesh/test.vs-dx11",
        ShaderName = "assets/shaders/generated/shaders/staticmesh/test",
        Stage = DxbcStage.Vertex,
        DefinePool = pool,
        Permutations = Array.Empty<ShaderPermutation>(),
        DeclaredBlobCount = 2,
        Flag = 7,
    };

    [Fact]
    public void PlannerPinsExplicitFalseAndEnumeratesRuntimeAxes()
    {
        var toc = Toc(("ANIMATION", "1"), ("ANIMATION", "0"), ("NO_BAKED_LIGHTING", "1"), ("QUALITY", "1"));
        var absent = new HashSet<string> { "NO_BAKED_LIGHTING" };
        var candidates = ShaderPermutationPlanner.EnumerateCandidates(
            toc, null, new Dictionary<string, bool> { ["ANIMATION"] = false }, null, null,
            out var why, forcedAbsent: absent);

        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, c => Assert.Contains("ANIMATION=0", c.Defines));
        Assert.DoesNotContain(candidates.SelectMany(c => c.Defines), d => d.StartsWith("NO_BAKED_LIGHTING="));
        Assert.Empty(candidates[0].InferredDefines);
        Assert.Equal(new[] { "QUALITY=1" }, candidates[1].InferredDefines);
        Assert.Contains("2 complete define", why);
    }

    [Fact]
    public void TocWriterRoundTripsAddedKeysAndBlobCount()
    {
        var source = Toc(("A", "1"), ("B", "0"));
        var permutations = new[]
        {
            new ShaderPermutation(0x11, 0),
            new ShaderPermutation(0x22, 2),
        };
        var bytes = ShaderCachePatchWriter.WriteToc(source, permutations, declaredBlobCount: 3);
        var reparsed = ShaderCacheReader.ParseToc(bytes, source.Path);

        Assert.NotNull(reparsed);
        Assert.Equal(7u, reparsed!.Flag);
        Assert.Equal(3u, reparsed.DeclaredBlobCount);
        Assert.Equal(source.DefinePool, reparsed.DefinePool);
        Assert.Equal(permutations.Select(p => (p.Key, p.BlobIndex)),
                     reparsed.Permutations.Select(p => (p.Key, p.BlobIndex)));
    }

    [Fact]
    public void ContainerWriterUsesRiotTrailingByteRecordLayout()
    {
        byte[] dxbc = new byte[32];
        "DXBC"u8.CopyTo(dxbc);
        BinaryPrimitives.WriteInt32LittleEndian(dxbc.AsSpan(24), dxbc.Length);
        var container = ShaderCachePatchWriter.WriteContainer(new[] { dxbc });

        Assert.Equal(dxbc.Length + 1, BinaryPrimitives.ReadInt32LittleEndian(container));
        Assert.Equal(dxbc, container.AsSpan(4, dxbc.Length).ToArray());
        Assert.Equal(0, container[^1]);
    }

    [Theory]
    [InlineData("assets/shaders/generated/foo.vs-dx11", true)]
    [InlineData("assets/shaders/generated/foo.ps-dx11_400", true)]
    [InlineData("assets/shaders/generated/foo.vs.dx11_0", true)]
    [InlineData("assets/shaders/generated/foo.hlsl", false)]
    [InlineData("notes/foo.ps-dx11", false)]
    public void KnownTypesFilterRecognisesOnlyGeneratedDx11CacheEntries(string path, bool expected) =>
        Assert.Equal(expected, WadPackService.IsDx11ShaderCacheEntry(path));

    [Fact]
    public void ExperimentalShaderCompilesWithTheLightmapContractOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.True(ExperimentalDynamicEffectLightmapShader.TryCompile(out var compiled, out var error), error);

        var vertex = DxbcReflection.Parse(compiled!.Vertex);
        var pixel = DxbcReflection.Parse(compiled.Pixel);
        var flow = DxbcReflection.Parse(compiled.FlowRipplePixel);
        Assert.Contains(vertex.Inputs, i => i.Semantic == "TEXCOORD" && i.Index == 7);
        Assert.Contains(pixel.Textures, r => r.Name == "BAKED_LIGHT__TX");
        Assert.Contains(pixel.Textures, r => r.Name == "DiffuseTexture__TX");
        Assert.Contains(flow.Textures, r => r.Name == "Mask_Tex__TX");

        var vertexFrame = vertex.ConstantBuffers.Single(cb => cb.BindPoint == 2);
        Assert.Equal(528, vertexFrame.Variables.Single(v => v.Name == "SUN_LIGHT_DIRECTION").Offset);
        Assert.Equal(540, vertexFrame.Variables.Single(v => v.Name == "NORMAL_OFFSET_BIAS").Offset);
        var globals = pixel.ConstantBuffers.Single(cb => cb.BindPoint == 0);
        Assert.Equal(0, globals.Variables.Single(v => v.Name == "BAKED_LIGHT_SCALE_AND_BIAS").Offset);
        Assert.Equal(16, globals.Variables.Single(v => v.Name == "BaseTex_TintColor").Offset);
        var frame = pixel.ConstantBuffers.Single(cb => cb.BindPoint == 1);
        Assert.Equal(128, frame.Variables.Single(v => v.Name == "LIGHT_MAP_COLOR_SCALE_AND_INTENSITY").Offset);
    }
}
