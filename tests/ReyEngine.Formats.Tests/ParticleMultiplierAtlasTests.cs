using System.Numerics;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Shaders;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M633: the multiplier atlas. Riot's particle vertex shader takes TWO flipbook descriptors and the D3D11
/// emitter pipeline only ever sent the first, so every MULT_PASS emitter sampled its multiplier texture as
/// one cell covering the whole sprite - a mask over the wrong pixels.
/// </summary>
public sealed class ParticleMultiplierAtlasTests
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private static bool Installed => Directory.Exists(Final);

    private static string? Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    // ===================================================== the descriptor itself

    [Fact]
    public void TheDescriptorIsColumnsAndTheTwoReciprocals()
    {
        // quad_vs spends it as col = frame - floor(frame * .y) * .x, then uv = (col+u) * .y, (row+v) * .z.
        Assert.Equal(new[] { 2f, 0.5f, 0.5f, 0f }, ParticleQuadBuilder.TextureInfo(new Vector2(2f, 2f)));
        Assert.Equal(new[] { 4f, 0.25f, 0.5f, 0f }, ParticleQuadBuilder.TextureInfo(new Vector2(4f, 2f)));

        // A single-frame sheet must build the IDENTITY, because that is what makes this change a no-op for
        // the 157 of 192 measured multiplier emitters that author no grid.
        Assert.Equal(new[] { 1f, 1f, 1f, 0f }, ParticleQuadBuilder.TextureInfo(new Vector2(1f, 1f)));
        Assert.Equal(new[] { 1f, 1f, 1f, 0f }, ParticleQuadBuilder.TextureInfo(Vector2.Zero));
    }

    // ===================================================== what Riot's shader actually declares

    [Fact]
    public void RiotsMultPassVertexShaderReallyUsesASecondDescriptor()
    {
        // The whole justification. If TEXTURE_INFO_2 were declared and unused, sending it would be noise;
        // it is USED, at $Globals+16, in every MULT_PASS permutation of quad_vs.
        if (!Installed) return;
        var database = new HashSyncService().LoadLocal(_ => { });
        using var cache = ShaderCacheReader.Open(Final, new WadPathResolver(database), out var error);
        if (cache is null) return;

        const string Name = "assets/shaders/hlsl/particlesystem/quad_vs";
        string toc = ShaderCacheReader.TocPathFor(Name, DxbcStage.Vertex);
        var stage = cache.ReadToc(toc);
        Assert.NotNull(stage);

        var described = ShaderCacheReader.DescribePermutations(stage!, out _);
        var mult = described.Where(p => p.Defines?.Any(d => d.StartsWith("MULT_PASS", StringComparison.Ordinal)) == true)
            .ToList();
        Assert.NotEmpty(mult);

        int checkedBlobs = 0;
        foreach (var permutation in mult)
        {
            var blob = cache.LoadShader(toc, permutation.BlobIndex, out _);
            if (blob is null) continue;
            checkedBlobs++;

            var globals = blob.ConstantBuffers.FirstOrDefault(c => c.Name == "$Globals");
            Assert.NotNull(globals);
            var second = globals!.Variables.FirstOrDefault(v => v.Name == "TEXTURE_INFO_2");
            Assert.NotNull(second);
            Assert.True(second!.IsUsed, "TEXTURE_INFO_2 is declared but unused - the M633 premise is wrong");
            Assert.Equal(16, second.Offset);

            // And the multiplier UV rides its own vertex input, not the primary's: the arithmetic adds
            // TEXCOORD1 to the multiplier cell (add r0.xy, r0.xyxx, v3.xyxx). Without that element in the
            // layout the mask would sample one texel however right the descriptor is.
            Assert.Contains(blob.Inputs, i => i.FullSemantic.Equals("TEXCOORD1", StringComparison.OrdinalIgnoreCase));
        }
        Assert.True(checkedBlobs > 0, "no MULT_PASS blob loaded");
    }

    [Fact]
    public void TheQuadBuilderSuppliesTheVertexInputThatDescriptorIndexes()
    {
        // Uv1 is TEXCOORD1 at +56. The descriptor scales it; if it were left at zero the multiplier would
        // collapse to the cell's corner texel and the fix above would do nothing visible.
        var text = Source("src", "ReyEngine.Rendering.D3D11", "ParticleQuadBuilder.cs");
        if (text is null) return;
        Assert.Contains("vert.Uv1 = new Vector2(u, v);", text);
    }

    // ===================================================== the two renderers, and that they agree

    [Fact]
    public void TheD3D11PipelineSendsTheSecondDescriptorFromTheAuthoredGrid()
    {
        var text = Source("src", "ReyEngine.App", "Services", "VfxD3D11EmitterPipeline.cs");
        if (text is null) return;

        Assert.Contains("mat.Params[\"TEXTURE_INFO_2\"] = ParticleQuadBuilder.TextureInfo(e.TextureMultTexDiv)", text);
        // Only for an emitter that has one. Sending a descriptor for an absent stage would be harmless but
        // would also make the parameter report lie about which stages an emitter drives.
        Assert.Contains("if (!string.IsNullOrEmpty(e.TextureMultPath))", text);
    }

    [Fact]
    public void TheGlShaderWalksTheMultiplierAtlasWithTheSameFrame()
    {
        // GL divided by the grid and stopped, so a multi-cell multiplier stayed on cell 0 - its own comment
        // said so. Riot walks it from the same frame index as the primary; both renderers do now.
        var text = Source("src", "ReyEngine.Rendering", "Vfx", "VfxParticleRenderer.cs");
        if (text is null) return;

        Assert.Contains("float mfx = mod(frame, max(abs(multCols), 1.0));", text);
        Assert.Contains("float mfy = floor(frame / max(abs(multCols), 1.0));", text);
        Assert.Contains("vUvMult = (vec2(mfx, mfy) + vec2(cell.x, 1.0 - cell.y)) / vec2(multCols, multRows)", text);
        Assert.DoesNotContain("a multi-cell multiplier atlas stays on cell 0 - which is a separate known defect", text);
    }

    [Fact]
    public void TheShaderStringsStayAscii()
    {
        // A non-ASCII byte inside a GLSL literal compiles in C# and is rejected by the driver, which reads
        // as a blank viewport rather than as an error (shader-strings-ascii-only).
        var text = Source("src", "ReyEngine.Rendering", "Vfx", "VfxParticleRenderer.cs");
        if (text is null) return;

        for (int at = text.IndexOf("@\"", StringComparison.Ordinal); at >= 0;
             at = text.IndexOf("@\"", at + 2, StringComparison.Ordinal))
        {
            int end = text.IndexOf('"', at + 2);
            while (end > 0 && end + 1 < text.Length && text[end + 1] == '"') end = text.IndexOf('"', end + 2);
            if (end < 0) break;
            string literal = text[at..end];
            Assert.All(literal, c => Assert.True(c < 128, $"non-ASCII U+{(int)c:X4} inside a shader literal"));
            at = end;
        }
    }

    // ===================================================== and that any of it matters

    [Fact]
    public void ChampionsReallyAuthorMultiCellMultiplierAtlases()
    {
        // Without this the fix is theory. Measured over the skin0 VFX closure of five champions: 192
        // emitters bind a multiplier texture, 35 author a grid bigger than 1x1 - 20 of them on Aatrox's R
        // (Aatrox_Base_R_burning_mult, 2x2) and 9 on his Q.
        if (!Installed) return;
        string wad = Path.Combine(Final, "Champions", "Aatrox.wad.client");
        if (!File.Exists(wad)) return;

        var database = new HashSyncService().LoadLocal(_ => { });
        using var archive = WadArchive.Open(wad, new WadPathResolver(database));

        var systems = new Dictionary<uint, VfxSystemDefinition>();
        var seen = new HashSet<ulong>();
        var queue = new Queue<ulong>();
        ulong start = HashAlgorithms.WadPath("data/characters/aatrox/skins/skin0.bin");
        if (!archive.TryGetEntry(start, out _)) return;
        seen.Add(start);
        queue.Enqueue(start);
        for (int guard = 0; queue.Count > 0 && guard < 64; guard++)
        {
            byte[] bytes;
            try { bytes = archive.Extract(queue.Dequeue()); }
            catch { continue; }
            foreach (var (key, value) in VfxSystemResolver.ExtractAll(bytes)) systems.TryAdd(key, value);
            foreach (string dependency in VfxSystemResolver.ExtractDependencies(bytes))
            {
                ulong hash = HashAlgorithms.WadPath(dependency);
                if (seen.Add(hash) && archive.TryGetEntry(hash, out _)) queue.Enqueue(hash);
            }
        }

        var multipliers = systems.Values.SelectMany(s => s.Emitters)
            .Where(e => !e.Disabled && !string.IsNullOrEmpty(e.TextureMultPath))
            .ToList();
        Assert.NotEmpty(multipliers);

        var grids = multipliers.Where(e => e.TextureMultTexDiv.X > 1f || e.TextureMultTexDiv.Y > 1f).ToList();
        Assert.True(grids.Count > 0,
            "Aatrox authors no multi-cell multiplier atlas - the emitters this fix targets are gone");
        Assert.Contains(grids, e => e.TextureMultTexDiv == new Vector2(2f, 2f));

        // The descriptor those emitters will now receive is not the identity, which is the whole point.
        foreach (var e in grids)
            Assert.NotEqual(new[] { 1f, 1f, 1f, 0f }, ParticleQuadBuilder.TextureInfo(e.TextureMultTexDiv));
    }
}
