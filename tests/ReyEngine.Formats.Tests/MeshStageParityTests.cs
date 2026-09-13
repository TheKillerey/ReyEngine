using System.Text;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Shaders;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M641: the two stages the GL mesh program never had (palette, alpha erosion), and two ordering facts
/// read off Riot's shipped bytecode rather than carried over from the quad path:
///
/// <list type="number">
/// <item>The palette runs on the SOURCE texel, BEFORE the multiplier. Both quad_ps and mesh_ps do this.
/// The GL quad shader multiplied first, which fed the gradient lookup a darkened texel and then threw the
/// multiplier's rgb away, because the lookup REPLACES rgb.</item>
/// <item>On the mesh path the erosion DRIVE is <c>cAlphaErosionParams.x</c>. quad_ps reads it from an
/// interpolant because a quad's vertices carry it; a mesh draw is already one particle, so Riot puts it in
/// the constant buffer - the very slot the quad path leaves at zero. Leaving it zero, as M640 shipped,
/// froze every mesh dissolve at drive 0: a static mask rather than a dissolve.</item>
/// </list>
/// </summary>
public sealed class MeshStageParityTests
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

    /// <summary>The disassembly of the one permutation of <paramref name="shader"/> that names every
    /// define in <paramref name="defines"/> and the fewest besides, or null when the game is not
    /// installed / the stage is not in the cache.</summary>
    private static unsafe string? Disassemble(string shader, DxbcStage stage, params string[] defines)
    {
        if (!Installed) return null;
        var database = new HashSyncService().LoadLocal(_ => { });
        using var cache = ShaderCacheReader.Open(Final, new WadPathResolver(database), out _);
        if (cache is null) return null;
        string name = "assets/shaders/hlsl/particlesystem/" + shader;
        string toc = ShaderCacheReader.TocPathFor(name, stage);
        var t = cache.ReadToc(toc);
        if (t is null) return null;

        var permutation = ShaderCacheReader.DescribePermutations(t, out _)
            .Where(p => p.Defines is not null && defines.All(d => p.Defines.Any(x => x.StartsWith(d, StringComparison.Ordinal))))
            .OrderBy(p => p.Defines!.Count)
            .FirstOrDefault();
        if (permutation is null) return null;
        var blob = cache.LoadShader(toc, permutation.BlobIndex, out _);
        if (blob is null) return null;

        var compiler = D3DCompiler.GetApi();
        ID3D10Blob* text = null;
        int hr;
        fixed (byte* bytes = blob.Bytecode)
            hr = compiler.Disassemble(bytes, (nuint)blob.Bytecode.Length, 0, (byte*)null, &text);
        if (hr < 0 || text is null) return null;
        string listing = Encoding.ASCII.GetString(
            new ReadOnlySpan<byte>(text->GetBufferPointer(), (int)text->GetBufferSize())).TrimEnd('\0');
        text->Release();
        return listing;
    }

    /// <summary>The instruction lines only - the header comments name constants that are not evidence of
    /// what the shader DOES with them.</summary>
    private static string[] Body(string listing) => listing.Split('\n')
        .Select(l => l.Trim())
        .Where(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal))
        .ToArray();

    // ===================================================== 1. the erosion drive

    [Fact]
    public void TheMeshShaderTakesTheErosionDriveFromTheConstantWhereTheQuadTakesAnInterpolant()
    {
        var mesh = Disassemble("mesh_ps", DxbcStage.Pixel, "ALPHA_EROSION");
        var quad = Disassemble("quad_ps", DxbcStage.Pixel, "ALPHA_EROSION");
        if (mesh is null || quad is null) return;

        // The erosion band is the same four instructions in both, in the same order. Only the source of
        // the DRIVE differs, and that is the whole finding.
        string[] mb = Body(mesh), qb = Body(quad);
        int mi = Array.FindIndex(mb, l => l.StartsWith("dp4_sat", StringComparison.Ordinal) && l.Contains("cb0[1].xyzw"));
        int qi = Array.FindIndex(qb, l => l.StartsWith("dp4_sat", StringComparison.Ordinal) && l.Contains("cb0[1].xyzw"));
        Assert.True(mi >= 0 && qi >= 0, "neither shader mixes the erosion map against cAlphaErosionTextureMixer");

        // [+1] subtract the fade-in offset, [+2] the drive minus the mask, [+3] the two ramps.
        Assert.Contains("-cb0[0].y", mb[mi + 1]);
        Assert.Contains("-cb0[0].y", qb[qi + 1]);
        Assert.Contains("cb0[0].zwzz", mb[mi + 3]);
        Assert.Contains("cb0[0].zwzz", qb[qi + 3]);

        // The substitution: the mesh reads the constant, the quad reads a vertex interpolant (v*).
        Assert.Contains("cb0[0].xxxx", mb[mi + 2]);
        Assert.Matches(@"\bv\d+\.[xyzw]{4}", qb[qi + 2]);
        Assert.DoesNotContain("cb0[0].xxxx", qb[qi + 2]);
    }

    [Fact]
    public void TheD3D11MeshDrawSendsThePerParticleDriveAndRefreshesThatBuffer()
    {
        var d3d = Source("src", "ReyEngine.Rendering.D3D11", "ShaderPreviewRenderer.cs");
        if (d3d is null) return;
        Assert.Contains("erosionParams[0] = inst[o + 18];", d3d);
        // Writing the value is not enough - the constant buffer holding it has to be re-uploaded per
        // particle, or every particle draws with the first one's drive.
        Assert.Contains("or \"cAlphaErosionParams\")) continue;", d3d);
    }

    // ===================================================== 2. palette before multiplier

    [Fact]
    public void BothRiotShadersPalettizeTheSourceTexelBeforeTheMultiplier()
    {
        foreach (string shader in new[] { "quad_ps", "mesh_ps" })
        {
            var listing = Disassemble(shader, DxbcStage.Pixel, "PALETTIZE_TEXTURES", "MULT_PASS");
            if (listing is null) return;
            string[] body = Body(listing);

            // The palette texture is the one sampled with a coordinate BUILT from a dot product, not with
            // an interpolated UV; find it by that construction rather than by a texture register number,
            // which differs between the two shaders.
            int mix = Array.FindIndex(body, l => l.StartsWith("dp4_sat", StringComparison.Ordinal) && l.Contains("cb0[1].xyzw"));
            Assert.True(mix >= 0, shader + ": no palette source mix");
            int paletteSample = Array.FindIndex(body, mix, l => l.StartsWith("sample", StringComparison.Ordinal));
            Assert.True(paletteSample > mix, shader + ": no gradient lookup after the mix");

            // The mix reads the texel sampled just before it - the SOURCE texel, unmultiplied.
            int sourceSample = Array.FindLastIndex(body, mix, l => l.StartsWith("sample", StringComparison.Ordinal));
            Assert.True(sourceSample >= 0 && sourceSample < mix, shader + ": the mix has no source sample before it");
            Assert.DoesNotContain(body[(sourceSample + 1)..mix], l => l.StartsWith("mul", StringComparison.Ordinal));

            // ...and the multiply that folds another texture in comes only AFTER the lookup.
            int firstMul = Array.FindIndex(body, l => l.StartsWith("mul r", StringComparison.Ordinal));
            Assert.True(firstMul > paletteSample,
                shader + ": a multiply lands before the gradient lookup, so the lookup would see a modulated texel");
        }
    }

    [Fact]
    public void BothGlShadersPalettizeBeforeTheMultiplier()
    {
        var gl = Source("src", "ReyEngine.Rendering", "Vfx", "VfxParticleRenderer.cs");
        if (gl is null) return;

        foreach (var (name, start, texel) in new[] { ("MeshFrag", "MeshFrag = @\"", "texel"), ("Frag", "string Frag = @\"", "t") })
        {
            int from = gl.IndexOf(start, StringComparison.Ordinal);
            Assert.True(from > 0, name + " not found");
            int end = gl.IndexOf("\";", from, StringComparison.Ordinal);
            string body = gl[from..end];

            int palette = body.IndexOf("uHasPalette != 0", StringComparison.Ordinal);
            int mult = body.IndexOf("uHasTexMult != 0", StringComparison.Ordinal);
            Assert.True(palette > 0, name + ": no palette stage");
            Assert.True(mult > 0, name + ": no multiplier stage");
            Assert.True(palette < mult,
                name + ": the multiplier runs before the palette, but both Riot shaders palettize the source texel first");
            Assert.Contains("texture(uPaletteTex, vec2(m, uPaletteV)).rgb", body);
        }
    }

    // ===================================================== 3. the GL mesh stages exist at all

    [Fact]
    public void TheGlMeshShaderCarriesTheStagesItsD3D11TwinAlreadyHad()
    {
        var gl = Source("src", "ReyEngine.Rendering", "Vfx", "VfxParticleRenderer.cs");
        if (gl is null) return;
        int from = gl.IndexOf("MeshFrag = @\"", StringComparison.Ordinal);
        string body = gl[from..gl.IndexOf("\";", from, StringComparison.Ordinal)];

        // Erosion: the same difference of two saturated linear ramps the quad path uses, driven by the
        // uniform rather than an interpolant.
        Assert.Contains("float te = uErosionDrive - E;", body);
        Assert.Contains("clamp((te + uErosionParams.y) * uErosionParams.z, 0.0, 1.0)", body);
        Assert.Contains("clamp( te                    * uErosionParams.w, 0.0, 1.0)", body);
        Assert.Contains("texel.a *= (ea - eb);", body);
        // The alpha test, on the eroded alpha, as the quad path and the D3D11 path both do.
        Assert.Contains("if (uAlphaRef > 0.0 && outColor.a < uAlphaRef) discard;", body);

        // ...and the C# side actually feeds them, including the per-particle drive inside the draw loop.
        Assert.Contains("bool meshHasPalette = es.PaletteTexture != 0 && es.Def.Palette is not null;", gl);
        // M717: and not when the client would route this emitter to quad_ps_fixedalphauv - which for a
        // MESH it never does, so the helper answers no here and the stage survives. The gate is written
        // anyway so one question is asked in both paths rather than two that can drift.
        Assert.Contains("bool meshHasErosion = es.ErosionTexture != 0 && es.Def.AlphaErosion is { IsDegenerate: false }", gl);
        Assert.Contains("VfxPrimitiveSupport.DrawsFixedAlphaUv(es.Def.Extras?.UvMode, es.Def.PrimitiveClass)", gl);
        Assert.Contains("if (meshHasErosion) _gl.Uniform1(_muErosionDrive, es.Instances[o + 18]);", gl);
        Assert.Contains("_gl.BindSampler(6, PaletteSampler(es.Def.PaletteAddressMode));", gl);
    }

    /// <summary>Every GLSL string in the renderer assembly is ASCII. A non-ASCII character compiles in C#
    /// and then breaks the GL driver, which shows up as a blank viewport rather than as an error.</summary>
    [Fact]
    public void EveryGlslStringIsAscii()
    {
        var gl = Source("src", "ReyEngine.Rendering", "Vfx", "VfxParticleRenderer.cs");
        if (gl is null) return;
        int at = 0;
        while ((at = gl.IndexOf(" = @\"", at, StringComparison.Ordinal)) > 0)
        {
            int end = gl.IndexOf("\";", at, StringComparison.Ordinal);
            if (end < 0) break;
            string body = gl[at..end];
            if (body.Contains("void main("))
                Assert.DoesNotContain(body, c => c > 127);
            at = end + 2;
        }
    }
}
