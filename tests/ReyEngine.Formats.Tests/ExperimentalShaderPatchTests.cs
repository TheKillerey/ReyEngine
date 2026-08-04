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
    public void PlannerDoesNotPinObservedRuntimeAxisToShaderDefault()
    {
        var toc = Toc(
            ("ENV_TRANSITION", "1"), ("ENV_TRANSITION", "0"),
            ("FLAG_ANIMATION_ON", "1"), ("FLAG_ANIMATION_ON", "0"),
            ("FOLIAGEWIND_ANIMATION_ON", "0"), ("FOLIAGEWIND_ANIMATION_ON", "1"),
            ("NO_BAKED_LIGHTING", "1"),
            ("TWO_D_DEFORM_ON", "1"), ("TWO_D_DEFORM_ON", "0"),
            ("USE_ROTATION", "0"), ("USE_WORLD_OFFSET", "1"),
            ("USE_SINUSOIDAL_MOVEMENT", "1"), ("USE_SINUSOIDAL_MOVEMENT", "0"),
            ("USE_TRANSLATION", "1"),
            ("VERTEX_ANIMATION_ON", "1"), ("VERTEX_ANIMATION_ON", "0"));
        var switches = new Dictionary<string, bool>
        {
            ["FLAG_ANIMATION_ON"] = false,
            ["FOLIAGEWIND_ANIMATION_ON"] = false,
            ["NO_BAKED_LIGHTING"] = true,
            ["TWO_D_DEFORM_ON"] = false,
            ["USE_ROTATION"] = false,
            ["USE_WORLD_OFFSET"] = true,
            ["USE_SINUSOIDAL_MOVEMENT"] = false,
            ["USE_TRANSLATION"] = true,
            ["VERTEX_ANIMATION_ON"] = false,
        };
        var defaults = new Dictionary<string, bool> { ["ENV_TRANSITION"] = false };
        var absent = new HashSet<string> { "NO_BAKED_LIGHTING" };
        var runtime = new HashSet<string> { "ENV_TRANSITION" };

        var candidates = ShaderPermutationPlanner.EnumerateCandidates(
            toc, null, switches, null, defaults, out _, forcedAbsent: absent, forcedFree: runtime);

        var live = Assert.Single(candidates, c => c.Key == 0x8adf585ed8c8b38aUL);
        Assert.Contains("ENV_TRANSITION=1", live.Defines);
        Assert.Equal(new[] { "ENV_TRANSITION=1" }, live.InferredDefines);
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
        Assert.True(DxbcChecksum.IsValid(compiled.Vertex));
        Assert.True(DxbcChecksum.IsValid(compiled.Pixel));
        Assert.True(DxbcChecksum.IsValid(compiled.FlowRipplePixel));
        Assert.Equal("$Globals", vertex.ConstantBuffers.Single(cb => cb.BindPoint == 1).Name);
        Assert.Equal("$Globals", pixel.ConstantBuffers.Single(cb => cb.BindPoint == 0).Name);
        Assert.Equal("$Globals", flow.ConstantBuffers.Single(cb => cb.BindPoint == 0).Name);
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

        // Renaming the compiler-reserved RDEF symbol changes metadata after D3DCompile. Prove the actual
        // driver accepts the resulting blobs, rather than trusting the reflection parser alone.
        using var renderer = new ShaderPreviewRenderer();
        Assert.True(renderer.Initialize(out var deviceError), deviceError);
        var load = renderer.LoadShaders(vertex, pixel);
        Assert.True(load.Success, load.Error);
        var flowLoad = renderer.LoadShaders(vertex, flow);
        Assert.True(flowLoad.Success, flowLoad.Error);
    }

    [Fact]
    public void ExperimentalSrxBlendShadersCompileWithTheLightmapContractOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.True(ExperimentalSrxBlendLightmapShader.TryCompile(out var compiled, out var error), error);

        foreach (var vertexBytes in new[] { compiled!.Vertex, compiled.ChemtechVertex })
        {
            var vertex = DxbcReflection.Parse(vertexBytes);
            Assert.True(DxbcChecksum.IsValid(vertexBytes));
            Assert.Equal("$Globals", vertex.ConstantBuffers.Single(cb => cb.BindPoint == 1).Name);
            Assert.Contains(vertex.Inputs, i => i.Semantic == "TEXCOORD" && i.Index == 7);
        }

        foreach (var pixelBytes in new[] { compiled.MasterPixel, compiled.ChemtechPixel })
        {
            var pixel = DxbcReflection.Parse(pixelBytes);
            Assert.True(DxbcChecksum.IsValid(pixelBytes));
            Assert.Equal("$Globals", pixel.ConstantBuffers.Single(cb => cb.BindPoint == 0).Name);
            Assert.Contains(pixel.Textures, r => r.Name == "BAKED_LIGHT__TX");
        }

        var master = DxbcReflection.Parse(compiled.MasterPixel);
        Assert.Contains(master.Textures, r => r.Name == "DiffuseTexture__TX");
        var chemtech = DxbcReflection.Parse(compiled.ChemtechPixel);
        Assert.Contains(chemtech.Textures, r => r.Name == "Diffuse_Texture__TX");
        Assert.Contains(chemtech.Textures, r => r.Name == "EmissionMaskTex__TX");
        Assert.Contains(chemtech.Textures, r => r.Name == "EmissionTex__TX");
        Assert.Contains(chemtech.Textures, r => r.Name == "TERRAIN_BLEND_SharedTexture" && r.Dimension == 5);
        var masterGlobals = master.ConstantBuffers.Single(cb => cb.Name == "$Globals");
        Assert.Equal(0, masterGlobals.Variables.Single(v => v.Name == "BAKED_LIGHT_SCALE_AND_BIAS").Offset);
        var chemtechGlobals = chemtech.ConstantBuffers.Single(cb => cb.Name == "$Globals");
        Assert.Equal(0, chemtechGlobals.Variables.Single(v => v.Name == "BAKED_LIGHT_SCALE_AND_BIAS").Offset);
        Assert.Equal(16, chemtechGlobals.Variables.Single(v => v.Name == "Tint_Color").Offset);
        Assert.Equal(32, chemtechGlobals.Variables.Single(v => v.Name == "EmissionColor").Offset);
        Assert.Equal(128, chemtech.ConstantBuffers.Single(cb => cb.BindPoint == 1)
            .Variables.Single(v => v.Name == "LIGHT_MAP_COLOR_SCALE_AND_INTENSITY").Offset);

        using var renderer = new ShaderPreviewRenderer();
        Assert.True(renderer.Initialize(out var deviceError), deviceError);
        var masterLoad = renderer.LoadShaders(DxbcReflection.Parse(compiled.Vertex), master);
        Assert.True(masterLoad.Success, masterLoad.Error);
        var chemtechLoad = renderer.LoadShaders(DxbcReflection.Parse(compiled.ChemtechVertex), chemtech);
        Assert.True(chemtechLoad.Success, chemtechLoad.Error);
    }
}
