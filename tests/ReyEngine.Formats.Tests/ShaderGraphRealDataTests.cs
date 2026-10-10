using System.Numerics;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Materials.ShaderGraph;
using ReyEngine.Formats.Shaders;
using ReyEngine.Rendering.D3D11;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M832: the Shader Graph against Riot's own installed shader cache (read-only; 16.20 when this was written): the base shader read from
/// the cache, one generated pixel shader per input signature, each compiled with the Windows HLSL compiler and checked element for element
/// against every Riot colour blob that has that signature - and the same graphs drawn by the real D3D11 preview renderer, next to Riot's
/// own blob. Renders are written as PNGs (set REYENGINE_M832_SHOTS to choose the folder; the default is the temp folder).
/// No-ops, with a SKIPPED line, where the game install, the hash dictionary or a D3D11 device is absent - the convention of the other
/// real-data tests.
/// </summary>
public sealed class ShaderGraphRealDataTests(ITestOutputHelper output)
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private const string Shader = "Shaders/StaticMesh/DefaultEnv_Flat";

    private sealed class Rig : IDisposable
    {
        public required ShaderCacheReader Cache { get; init; }
        public required ShaderPermutationIndex Perms { get; init; }
        public required SgBase Base { get; init; }
        public required ShaderStageToc PixelToc { get; init; }
        public required ShaderStageToc VertexToc { get; init; }
        public void Dispose() { Cache.Dispose(); Perms.Dispose(); }

        public static Rig? Open(ITestOutputHelper output)
        {
            if (!OperatingSystem.IsWindows() || !Directory.Exists(Final)) { output.WriteLine("SKIPPED: no game install"); return null; }
            HashDatabase db;
            try { db = new HashSyncService().LoadLocal(_ => { }); } catch { output.WriteLine("SKIPPED: no hash dictionary"); return null; }
            var resolver = new WadPathResolver(db);
            var cache = ShaderCacheReader.Open(Final, resolver, out _);
            if (cache is null) { output.WriteLine("SKIPPED: the shader cache did not open"); return null; }
            var perms = new ShaderPermutationIndex(Final, h => resolver.TryGetPath(h, out var p) ? p : null);
            var b = ShaderGraphBaseResolver.Resolve(cache, perms, Shader, out string error, out _);
            Assert.True(b is not null, error);
            string full = "assets/shaders/generated/" + Shader.ToLowerInvariant();
            return new Rig
            {
                Cache = cache, Perms = perms, Base = b!,
                PixelToc = cache.ReadToc(ShaderCacheReader.TocPathFor(full, DxbcStage.Pixel))!,
                VertexToc = cache.ReadToc(ShaderCacheReader.TocPathFor(full, DxbcStage.Vertex))!,
            };
        }

        /// <summary>The colour permutations (not the shadow-map pass) with their define sets, one per blob.</summary>
        public IReadOnlyList<ShaderPermutation> ColourPermutations()
        {
            var described = ShaderCacheReader.DescribePermutations(PixelToc, out bool truncated);
            Assert.False(truncated);
            return described.Where(p => p.Defines is null || !p.Defines.Contains("GENERATE_SHADOW_MAP=1")).GroupBy(p => p.BlobIndex).Select(g => g.First()).ToList();
        }

        public (DxbcShader Vs, DxbcShader Ps) Riot(IDictionary<string, string> macros)
        {
            perms_defs(out var feat, out var sw);
            var vsPerm = ShaderCacheReader.ResolvePermutation(VertexToc, new Dictionary<string, string>(macros), new Dictionary<string, bool>(), feat, sw, out _)!;
            var psPerm = ShaderCacheReader.ResolvePermutation(PixelToc, new Dictionary<string, string>(macros), new Dictionary<string, bool>(), feat, sw, out _)!;
            return (Cache.LoadShader(VertexToc.Path, vsPerm.BlobIndex, out _)!, Cache.LoadShader(PixelToc.Path, psPerm.BlobIndex, out _)!);
        }

        private void perms_defs(out IReadOnlyDictionary<string, string>? feat, out IReadOnlyDictionary<string, bool>? sw)
        {
            feat = null; sw = null;
            if (Perms.TryGetShaderDefs(Shader, out var f, out var s)) { feat = f; sw = s; }
        }
    }

    // ================================================================================= the base shader

    [Fact]
    public void The_base_of_DefaultEnv_Flat_is_read_from_the_cache_with_its_declared_inputs_and_every_colour_signature()
    {
        using var rig = Rig.Open(output);
        if (rig is null) return;
        var b = rig.Base;
        output.WriteLine($"signatures: {b.Signatures.Count}; textures: {string.Join(",", b.Textures.Select(t => t.Name))}; params: {string.Join(",", b.Parameters.Select(p => $"{p.Name}/{p.Components}"))}");
        Assert.Equal(new[] { "DiffuseTexture" }, b.Textures.Select(t => t.Name));       // shaders.bin declares one texture; the engine-bound BAKED_LIGHT etc. are not offered
        var tint = Assert.Single(b.Parameters);
        Assert.Equal("TintColor", tint.Name);
        Assert.Equal(4, tint.Components);
        Assert.NotEmpty(b.Signatures);
        // UV0 = TEXCOORD1.xy in EVERY colour signature (the default UV of a texture sample relies on it)
        Assert.All(b.Signatures, s => Assert.True(s.Input("TEXCOORD", b.UvSet) is { ComponentCount: >= 2 }, "signature #" + s.Index));
        Assert.NotNull(b.Frame);
        Assert.True(b.HasTime);
        Assert.NotNull(b.Frame!.Var("RENDERTARGET_IS_SRGB"));
        Assert.False(b.HasVertexColor);                                                 // DefaultEnv_Flat writes no COLOR: the Vertex Color node is not offered
        // the permutations the signatures stand for are what a shipping step needs twin keys for
        int keys = b.Signatures.Sum(s => s.Permutations.Count);
        output.WriteLine($"colour permutation keys over the signatures: {keys}");
        Assert.True(keys > 0);
        Assert.Equal(b.Signatures.SelectMany(s => s.Permutations).Select(p => p.Key).Distinct().Count(), keys);   // a key belongs to one signature
        // the TOC's every key is a colour key of one signature or a shadow-map key (what a twin-key patch has to cover)
        var all = rig.PixelToc.Permutations.Select(p => p.Key).Distinct().ToHashSet();
        var covered = b.Signatures.SelectMany(s => s.Permutations).Select(p => p.Key).Concat(b.ShadowPermutations.Select(p => p.Key)).ToHashSet();
        output.WriteLine($"TOC unique keys {all.Count}: {keys} colour + {b.ShadowPermutations.Count} shadow; {b.PixelBlobCount} blobs in {b.PixelTocPath}");
        Assert.True(all.SetEquals(covered));
    }

    [Fact]
    public void An_unsupported_base_is_refused_with_a_reason()
    {
        using var rig = Rig.Open(output);
        if (rig is null) return;
        Assert.Null(ShaderGraphBaseResolver.Resolve(rig.Cache, rig.Perms, "Shaders/StaticMesh/Mantis", out string error, out _));
        Assert.Contains("not a base shader this build supports", error);
        Assert.False(SgBase.IsSupported("Shaders/StaticMesh/Mantis"));
        Assert.True(SgBase.IsSupported("shaders\\staticmesh\\defaultenv_flat"));
    }

    // ================================================================================= compile + verify

    private static ShaderGraphDocument TextureTimesTint()
    {
        var d = ShaderGraphDocument.CreateEmpty("tex_x_tint", Shader);
        ShaderGraphTests.Add(d, "n2", "TextureSample", ("texture", "DiffuseTexture"));
        ShaderGraphTests.Add(d, "n3", "Parameter", ("name", "TintColor"));
        ShaderGraphTests.Add(d, "n4", "Multiply");
        ShaderGraphTests.Add(d, "n5", "ComponentMask", ("mask", "rgb"));
        ShaderGraphTests.Link(d, "n2", "RGBA", "n4", "A"); ShaderGraphTests.Link(d, "n3", "Value", "n4", "B");
        ShaderGraphTests.Link(d, "n4", "Value", "n5", "Value");
        ShaderGraphTests.Link(d, "n5", "Value", "n1", SgCatalog.BaseColorPin);
        ShaderGraphTests.Link(d, "n2", "A", "n1", SgCatalog.OpacityPin);
        return d;
    }

    [Fact]
    public void A_graph_compiles_for_every_input_signature_and_each_compiled_ISGN_and_OSGN_equals_every_Riot_blob_it_stands_in_for()
    {
        using var rig = Rig.Open(output);
        if (rig is null) return;
        var b = rig.Base;
        var build = ShaderGraphCompiler.BuildAll(TextureTimesTint(), b);
        Assert.True(build.Ok, string.Join("\n", build.Diagnostics));
        Assert.Equal(b.Signatures.Count, build.Compiled.Count);

        // every Riot colour blob: its signature is one of ours, and the compiled shader of that signature equals it
        var perms = rig.ColourPermutations();
        int checkedBlobs = 0, o1Constant = 0;
        foreach (var p in perms)
        {
            var riot = rig.Cache.LoadShader(rig.PixelToc.Path, p.BlobIndex, out var why);
            Assert.True(riot is not null, $"blob #{p.BlobIndex}: {why}");
            var sig = b.SignatureOf(riot!);
            Assert.True(sig is not null, $"blob #{p.BlobIndex} has a signature the base does not list");
            var compiled = build.For(sig!)!;
            Assert.True(SgSignature.SameList(riot!.Inputs, compiled.Shader!.Inputs), $"ISGN differs for blob #{p.BlobIndex}");
            Assert.True(SgSignature.SameList(riot.Outputs, compiled.Shader.Outputs), $"OSGN differs for blob #{p.BlobIndex}");
            checkedBlobs++;
            // what Riot writes to the extra render target: a constant, in every blob (the generator copies it)
            Assert.True(DxbcDisassembler.TryDisassemble(riot.Bytecode, out var asm, out _));
            if (asm.Contains("mov o1.xyzw, l(0,0,0,1.000000)", StringComparison.Ordinal)) o1Constant++;
        }
        output.WriteLine($"Riot colour blobs checked: {checkedBlobs}; signatures: {b.Signatures.Count}; blobs whose SV_Target1 is the constant (0,0,0,1): {o1Constant}");
        Assert.True(checkedBlobs > 0);
        Assert.Equal(checkedBlobs, o1Constant);

        foreach (var c in build.Compiled)
        {
            var globals = Assert.Single(c.Shader!.ConstantBuffers, cb => cb.Name == "$Globals");
            Assert.Equal(0, globals.BindPoint);
            Assert.Equal("TintColor", Assert.Single(globals.Variables).Name);
            var frame = Assert.Single(c.Shader.ConstantBuffers, cb => cb.Name == "PerFramePixelCB");
            Assert.Equal(b.Frame!.Size, frame.Size);
            Assert.Equal(b.Frame.Variables.Count, frame.Variables.Count);
            Assert.Contains(c.Shader.Resources, r => r.Name == "DiffuseTexture__TX");
            Assert.Contains(c.Shader.Resources, r => r.Name == "DiffuseTexture__SMP");
        }
    }

    [Fact]
    public void The_engine_cbuffer_the_generator_redeclares_has_the_offsets_every_Riot_blob_has()
    {
        using var rig = Rig.Open(output);
        if (rig is null) return;
        var frame = rig.Base.Frame!;
        int checkedVars = 0;
        foreach (var p in rig.ColourPermutations())
        {
            var riot = rig.Cache.LoadShader(rig.PixelToc.Path, p.BlobIndex, out _)!;
            foreach (var cb in riot.ConstantBuffers.Where(c => c.Name == "PerFramePixelCB"))
            {
                Assert.Equal(frame.Size, cb.Size);
                foreach (var v in cb.Variables)
                {
                    var known = frame.Var(v.Name);
                    Assert.True(known is not null && known.Offset == v.Offset && known.Size == v.Size, $"{v.Name} in blob #{p.BlobIndex}");
                    checkedVars++;
                }
            }
        }
        output.WriteLine($"PerFramePixelCB variables compared with the redeclared layout: {checkedVars}");
        Assert.True(checkedVars > 0);
    }

    [Fact]
    public void Graphs_using_time_and_a_second_uv_set_compile_where_the_signature_allows_it()
    {
        using var rig = Rig.Open(output);
        if (rig is null) return;
        var b = rig.Base;
        var d = ShaderGraphDocument.CreateEmpty("anim", Shader);
        ShaderGraphTests.Add(d, "n2", "Time");
        ShaderGraphTests.Add(d, "n3", "Sine");
        ShaderGraphTests.Add(d, "n4", "Saturate");
        ShaderGraphTests.Add(d, "n5", "TexCoord", ("set", "1"));
        ShaderGraphTests.Add(d, "n6", "TextureSample", ("texture", "DiffuseTexture"));
        ShaderGraphTests.Add(d, "n7", "Add");
        ShaderGraphTests.Link(d, "n2", "Seconds", "n3", "Value"); ShaderGraphTests.Link(d, "n3", "Value", "n4", "Value");
        ShaderGraphTests.Link(d, "n5", "UV", "n6", "UV");
        ShaderGraphTests.Link(d, "n6", "RGB", "n7", "A"); ShaderGraphTests.Link(d, "n4", "Value", "n7", "B");
        ShaderGraphTests.Link(d, "n7", "Value", "n1", SgCatalog.BaseColorPin);
        var build = ShaderGraphCompiler.BuildAll(d, b);
        Assert.True(build.Ok, string.Join("\n", build.Diagnostics));
        Assert.All(build.Compiled, c => Assert.Contains("TIME", c.Hlsl.Source));
    }

    // ================================================================================= real D3D11 renders

    private static string ShotsDir()
    {
        string dir = System.Environment.GetEnvironmentVariable("REYENGINE_M832_SHOTS") is { Length: > 0 } e ? e : Path.Combine(Path.GetTempPath(), "reyengine-m832-shots");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static byte[] Texture(Func<float, float, (float, float, float)> f, int n = 256)
    {
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                var (r, g, bl) = f((x + 0.5f) / n, (y + 0.5f) / n);
                int o = (y * n + x) * 4;
                px[o] = (byte)Math.Clamp(r * 255, 0, 255); px[o + 1] = (byte)Math.Clamp(g * 255, 0, 255); px[o + 2] = (byte)Math.Clamp(bl * 255, 0, 255); px[o + 3] = 255;
            }
        return px;
    }

    private static readonly byte[] TexA = Texture((u, v) => { float c = (((int)(u * 8) + (int)(v * 8)) & 1) == 0 ? 1f : 0.6f; return (u * c, v * c, (1 - u) * 0.8f * c); });
    private static readonly byte[] TexB = Texture((u, v) => { float s = ((int)((u + v) * 16) & 1) == 0 ? 0.9f : 0.15f; return (s, s, s); });

    private sealed record Frame(byte[] Bgra, string Name);

    private Frame? Render(DxbcShader vs, DxbcShader ps, Dictionary<string, byte[]> textures, Dictionary<string, float[]> overrides, string name)
    {
        using var r = new ShaderPreviewRenderer();
        if (!r.Initialize(out var initError)) { output.WriteLine("SKIPPED: no D3D11 device here: " + initError); return null; }
        var report = r.LoadShaders(vs, ps);
        Assert.True(report.Success, report.Error);
        r.SetMesh(PreviewGeometry.CreateBuiltIn("Plane"));
        foreach (var (n, px) in textures) r.SetTexture(n, px, 256, 256);
        foreach (var (n, v) in overrides) r.Overrides[n] = v;
        var s = new PreviewSettings
        {
            Yaw = 0f, Pitch = 1.5f, Distance = 3.2f, AlphaBlend = false, DepthTest = true, CullBackFaces = false,
            ClearColor = new Vector4(0.04f, 0.05f, 0.07f, 1f), TimeSeconds = 1.25f, MirrorX = false, TransposeMatrices = true,
        };
        var frame = r.RenderFrame(512, 512, s, out var error, new List<string>());
        Assert.True(frame is not null, error);
        var copy = frame!.ToArray();
        ChromaRecolourRealDataTests.WritePng(Path.Combine(ShotsDir(), name + ".png"), copy, 512, 512);
        output.WriteLine($"rendered {name} -> {Path.Combine(ShotsDir(), name + ".png")}  (unbound textures: [{string.Join(",", r.UnboundTextureNames())}])");
        return new Frame(copy, name);
    }

    private static bool IsClear(byte[] f, int i) =>
        Math.Abs(f[i] - 0.07f * 255) < 3 && Math.Abs(f[i + 1] - 0.05f * 255) < 3 && Math.Abs(f[i + 2] - 0.04f * 255) < 3;   // BGRA of (0.04, 0.05, 0.07)

    private static (double Mean, double Max, int Pixels) Diff(Frame a, Frame b)
    {
        double sum = 0, max = 0; int n = 0;
        for (int i = 0; i + 3 < a.Bgra.Length; i += 4)
        {
            if (IsClear(a.Bgra, i) && IsClear(b.Bgra, i)) continue;
            double d = (Math.Abs(a.Bgra[i] - b.Bgra[i]) + Math.Abs(a.Bgra[i + 1] - b.Bgra[i + 1]) + Math.Abs(a.Bgra[i + 2] - b.Bgra[i + 2])) / 3.0;
            sum += d; max = Math.Max(max, d); n++;
        }
        return (sum / Math.Max(n, 1), max, n);
    }

    private static readonly Dictionary<string, string> FlatMacros = new() { ["NO_BAKED_LIGHTING"] = "1", ["DISABLE_DEPTH_FOG"] = "1" };

    private SgCompiled CompileFor(Rig rig, ShaderGraphDocument d, DxbcShader riotPixel, SgBase? baseOverride = null)
    {
        var build = ShaderGraphCompiler.BuildAll(d, baseOverride ?? rig.Base);
        Assert.True(build.Ok, string.Join("\n", build.Diagnostics));
        return build.ForRiotPixel(riotPixel) ?? throw new InvalidOperationException("no compiled shader for the Riot pixel signature");
    }

    [Fact]
    public void Texture_times_a_red_constant_draws_only_red_on_the_real_preview()
    {
        using var rig = Rig.Open(output);
        if (rig is null) return;
        var (vs, ps) = rig.Riot(FlatMacros);
        var d = ShaderGraphDocument.CreateEmpty("tex_x_red", Shader);
        ShaderGraphTests.Add(d, "n2", "TextureSample", ("texture", "DiffuseTexture"));
        ShaderGraphTests.Add(d, "n3", "Constant", ("value", "1 0 0"));
        ShaderGraphTests.Add(d, "n4", "Multiply");
        ShaderGraphTests.Link(d, "n2", "RGB", "n4", "A"); ShaderGraphTests.Link(d, "n3", "Value", "n4", "B");
        ShaderGraphTests.Link(d, "n4", "Value", "n1", SgCatalog.BaseColorPin); ShaderGraphTests.Link(d, "n2", "A", "n1", SgCatalog.OpacityPin);
        var tex = new Dictionary<string, byte[]> { ["DiffuseTexture__TX"] = TexA };

        var plain = Render(vs, CompileFor(rig, d, ps).Shader!, tex, new(), "graph1_texture_x_red");
        if (plain is null) return;
        int covered = 0, red = 0;
        for (int i = 0; i + 3 < plain.Bgra.Length; i += 4)
        {
            if (IsClear(plain.Bgra, i)) continue;
            covered++;
            Assert.True(plain.Bgra[i] == 0 && plain.Bgra[i + 1] == 0, "a covered pixel has blue or green: the constant red did not multiply the texture");
            if (plain.Bgra[i + 2] > 100) red++;
        }
        output.WriteLine($"covered pixels {covered}, strongly red {red}");
        Assert.True(covered > 5000);
        Assert.True(red > 500);
    }

    [Fact]
    public void A_lerp_of_two_samples_of_the_texture_follows_its_parameter_exactly_and_two_textures_bind_by_name()
    {
        using var rig = Rig.Open(output);
        if (rig is null) return;
        var (vs, ps) = rig.Riot(FlatMacros);
        var tex = new Dictionary<string, byte[]> { ["DiffuseTexture__TX"] = TexA };

        // the real base declares ONE texture, so this lerps the texture against itself at four times the UV scale, by TintColor.r
        var d = ShaderGraphDocument.CreateEmpty("lerp_two_samples", Shader);
        ShaderGraphTests.Add(d, "n2", "TextureSample", ("texture", "DiffuseTexture"));
        ShaderGraphTests.Add(d, "n3", "TexCoord", ("set", "1")); ShaderGraphTests.Add(d, "n4", "Multiply"); ShaderGraphTests.Add(d, "n5", "Constant", ("value", "4"));
        ShaderGraphTests.Add(d, "n6", "TextureSample", ("texture", "DiffuseTexture"));
        ShaderGraphTests.Add(d, "n7", "Parameter", ("name", "TintColor")); ShaderGraphTests.Add(d, "n8", "ComponentMask", ("mask", "r")); ShaderGraphTests.Add(d, "n9", "Lerp");
        ShaderGraphTests.Link(d, "n3", "UV", "n4", "A"); ShaderGraphTests.Link(d, "n5", "Value", "n4", "B"); ShaderGraphTests.Link(d, "n4", "Value", "n6", "UV");
        ShaderGraphTests.Link(d, "n2", "RGB", "n9", "A"); ShaderGraphTests.Link(d, "n6", "RGB", "n9", "B");
        ShaderGraphTests.Link(d, "n7", "Value", "n8", "Value"); ShaderGraphTests.Link(d, "n8", "Value", "n9", "Alpha");
        ShaderGraphTests.Link(d, "n9", "Value", "n1", SgCatalog.BaseColorPin);
        var lerp = CompileFor(rig, d, ps).Shader!;
        var f0 = Render(vs, lerp, tex, new() { ["TintColor"] = new[] { 0f, 0, 0, 1 } }, "graph2_lerp_alpha0");
        if (f0 is null) return;
        var f5 = Render(vs, lerp, tex, new() { ["TintColor"] = new[] { 0.5f, 0, 0, 1 } }, "graph2_lerp_alpha05")!;
        var f1 = Render(vs, lerp, tex, new() { ["TintColor"] = new[] { 1f, 0, 0, 1 } }, "graph2_lerp_alpha1")!;
        var (m01, _, _) = Diff(f0, f1);
        Assert.True(m01 > 10, "alpha 0 and alpha 1 draw the same thing");
        // the half-way frame is the mean of the end frames, to rounding
        double worst = 0, sum = 0; int n = 0;
        for (int i = 0; i + 3 < f0.Bgra.Length; i += 4)
        {
            if (IsClear(f0.Bgra, i) && IsClear(f1.Bgra, i)) continue;
            for (int k = 0; k < 3; k++)
            {
                double d2 = Math.Abs(f5.Bgra[i + k] - (f0.Bgra[i + k] + f1.Bgra[i + k]) / 2.0);
                worst = Math.Max(worst, d2); sum += d2; n++;
            }
        }
        output.WriteLine($"lerp(0.5) vs mean of the ends: mean {sum / n:F3}, worst {worst:F1} (8-bit levels)");
        Assert.True(worst <= 2.5, $"worst {worst}");

        // two textures, on a test base that adds TexB (DefaultEnv_Flat declares only DiffuseTexture; the preview renderer binds by reflected name)
        var two = new SgBase
        {
            Shader = rig.Base.Shader, Textures = new[] { new SgTextureDecl("DiffuseTexture"), new SgTextureDecl("TexB") },
            Parameters = rig.Base.Parameters, Signatures = rig.Base.Signatures, UvSet = rig.Base.UvSet, Frame = rig.Base.Frame,
        };
        var d3 = ShaderGraphDocument.CreateEmpty("lerp_two_textures", Shader);
        ShaderGraphTests.Add(d3, "n2", "TextureSample", ("texture", "DiffuseTexture")); ShaderGraphTests.Add(d3, "n3", "TextureSample", ("texture", "TexB"));
        ShaderGraphTests.Add(d3, "n4", "Parameter", ("name", "TintColor")); ShaderGraphTests.Add(d3, "n5", "ComponentMask", ("mask", "r")); ShaderGraphTests.Add(d3, "n6", "Lerp");
        ShaderGraphTests.Link(d3, "n2", "RGB", "n6", "A"); ShaderGraphTests.Link(d3, "n3", "RGB", "n6", "B");
        ShaderGraphTests.Link(d3, "n4", "Value", "n5", "Value"); ShaderGraphTests.Link(d3, "n5", "Value", "n6", "Alpha"); ShaderGraphTests.Link(d3, "n6", "Value", "n1", SgCatalog.BaseColorPin);
        var twoShader = CompileFor(rig, d3, ps, two).Shader!;
        var both = new Dictionary<string, byte[]> { ["DiffuseTexture__TX"] = TexA, ["TexB__TX"] = TexB };
        var a0 = Render(vs, twoShader, both, new() { ["TintColor"] = new[] { 0f, 0, 0, 1 } }, "graph3_two_textures_alpha0")!;
        var a1 = Render(vs, twoShader, both, new() { ["TintColor"] = new[] { 1f, 0, 0, 1 } }, "graph3_two_textures_alpha1")!;
        // alpha 1 is TexB alone, a grey texture; alpha 0 is TexA, which is coloured
        int greyPixels = 0, colouredPixels = 0, total = 0;
        for (int i = 0; i + 3 < a1.Bgra.Length; i += 4)
        {
            if (IsClear(a1.Bgra, i)) continue;
            total++;
            if (Math.Abs(a1.Bgra[i] - a1.Bgra[i + 1]) <= 1 && Math.Abs(a1.Bgra[i + 1] - a1.Bgra[i + 2]) <= 1) greyPixels++;
            if (Math.Abs(a0.Bgra[i] - a0.Bgra[i + 2]) > 20) colouredPixels++;
        }
        output.WriteLine($"two textures: {greyPixels}/{total} grey at alpha 1, {colouredPixels}/{total} coloured at alpha 0");
        Assert.True(greyPixels >= total * 0.99);
        Assert.True(colouredPixels > total * 0.5);
    }

    [Fact]
    public void A_graph_equal_to_Riots_texture_and_TintColor_blend_matches_Riots_own_blob_and_a_plain_multiply_does_not()
    {
        using var rig = Rig.Open(output);
        if (rig is null) return;
        var (vs, ps) = rig.Riot(FlatMacros);
        var tex = new Dictionary<string, byte[]> { ["DiffuseTexture__TX"] = TexA };
        var tint = new Dictionary<string, float[]> { ["TintColor"] = new[] { 0.9f, 0.5f, 0.2f, 1f } };

        var riot = Render(vs, ps, tex, tint, "riot_defaultenv_flat");
        if (riot is null) return;

        // (a) the obvious graph: texture x TintColor
        var multiply = Render(vs, CompileFor(rig, TextureTimesTint(), ps).Shader!, tex, tint, "graph4_texture_x_tint_multiply")!;

        // (b) what Riot's disassembly does with them (DefaultEnv_Flat, DiffuseTexture / TintColor): an OVERLAY blend,
        //     tex < 0.5 ? 2*tex*tint : 1 - 2*(1-tex)*(1-tint)
        var d = ShaderGraphDocument.CreateEmpty("tex_overlay_tint", Shader);
        void T(ShaderGraphDocument dd, string id, string type, params (string, string)[] props) => ShaderGraphTests.Add(dd, id, type, props);
        void L(ShaderGraphDocument dd, string a, string ap, string b2, string bp) => ShaderGraphTests.Link(dd, a, ap, b2, bp);
        T(d, "t", "TextureSample", ("texture", "DiffuseTexture")); T(d, "p", "Parameter", ("name", "TintColor")); T(d, "pm", "ComponentMask", ("mask", "rgb"));
        T(d, "two", "Constant", ("value", "2")); T(d, "half", "Constant", ("value", "0.5"));
        T(d, "mul", "Multiply"); T(d, "lo", "Multiply");
        T(d, "it", "OneMinus"); T(d, "ip", "OneMinus"); T(d, "m2", "Multiply"); T(d, "hi2", "Multiply"); T(d, "hi", "OneMinus");
        T(d, "step", "Step"); T(d, "mix", "Lerp");
        L(d, "p", "Value", "pm", "Value");
        L(d, "t", "RGB", "mul", "A"); L(d, "pm", "Value", "mul", "B"); L(d, "mul", "Value", "lo", "A"); L(d, "two", "Value", "lo", "B");
        L(d, "t", "RGB", "it", "Value"); L(d, "pm", "Value", "ip", "Value"); L(d, "it", "Value", "m2", "A"); L(d, "ip", "Value", "m2", "B");
        L(d, "m2", "Value", "hi2", "A"); L(d, "two", "Value", "hi2", "B"); L(d, "hi2", "Value", "hi", "Value");
        L(d, "half", "Value", "step", "Edge"); L(d, "t", "RGB", "step", "X");
        L(d, "lo", "Value", "mix", "A"); L(d, "hi", "Value", "mix", "B"); L(d, "step", "Value", "mix", "Alpha");
        L(d, "mix", "Value", "n1", SgCatalog.BaseColorPin); L(d, "t", "A", "n1", SgCatalog.OpacityPin);
        var overlay = Render(vs, CompileFor(rig, d, ps).Shader!, tex, tint, "graph5_overlay_equivalent")!;

        var dm = Diff(riot, multiply);
        var dov = Diff(riot, overlay);
        output.WriteLine($"plain multiply vs Riot: mean {dm.Mean:F2}, max {dm.Max:F1} 8-bit levels over {dm.Pixels} pixels");
        output.WriteLine($"overlay graph  vs Riot: mean {dov.Mean:F2}, max {dov.Max:F1} 8-bit levels over {dov.Pixels} pixels");
        Assert.True(dov.Pixels > 5000);
        Assert.True(dov.Max <= 2.0, $"the overlay graph differs from Riot's blob by up to {dov.Max} levels");
        Assert.True(dm.Mean > 10, "a plain multiply is expected to differ clearly from Riot's overlay blend");
    }
}
