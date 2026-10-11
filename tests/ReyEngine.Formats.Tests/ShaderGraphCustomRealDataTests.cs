using System.Numerics;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Materials.ShaderGraph;
using ReyEngine.Formats.Shaders;
using ReyEngine.Rendering.D3D11;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M834: the customisation features against Riot's installed shader cache and the real D3D11 preview renderer: every new node, a custom parameter and
/// a Custom HLSL node compile for ALL colour input signatures of DefaultEnv_Flat and pass the compiler's verification (ISGN / OSGN equal Riot's,
/// $Globals limited to declared + custom names); and three renders written as PNGs (REYENGINE_M832_SHOTS): a custom float4 colour parameter driving the
/// tint, a Panner scrolling the texture (two frames at different times) and a Custom node drawing a checker.
/// No-ops with a SKIPPED line where the game install, the hash dictionary or a D3D11 device is absent.
/// </summary>
public sealed class ShaderGraphCustomRealDataTests(ITestOutputHelper output)
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private const string Shader = "Shaders/StaticMesh/DefaultEnv_Flat";
    private static readonly Dictionary<string, string> FlatMacros = new() { ["NO_BAKED_LIGHTING"] = "1", ["DISABLE_DEPTH_FOG"] = "1" };

    private sealed class Rig : IDisposable
    {
        public required ShaderCacheReader Cache { get; init; }
        public required ShaderPermutationIndex Perms { get; init; }
        public required SgBase Base { get; init; }
        public required DxbcShader Vs { get; init; }
        public required DxbcShader RiotPs { get; init; }
        public void Dispose() { Cache.Dispose(); }

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
            var pixelToc = cache.ReadToc(ShaderCacheReader.TocPathFor(full, DxbcStage.Pixel))!;
            var vertexToc = cache.ReadToc(ShaderCacheReader.TocPathFor(full, DxbcStage.Vertex))!;
            IReadOnlyDictionary<string, string>? feat = null; IReadOnlyDictionary<string, bool>? sw = null;
            if (perms.TryGetShaderDefs(Shader, out var f, out var s)) { feat = f; sw = s; }
            var vsPerm = ShaderCacheReader.ResolvePermutation(vertexToc, new Dictionary<string, string>(FlatMacros), new Dictionary<string, bool>(), feat, sw, out _)!;
            var psPerm = ShaderCacheReader.ResolvePermutation(pixelToc, new Dictionary<string, string>(FlatMacros), new Dictionary<string, bool>(), feat, sw, out _)!;
            return new Rig
            {
                Cache = cache, Perms = perms, Base = b!,
                Vs = cache.LoadShader(vertexToc.Path, vsPerm.BlobIndex, out _)!,
                RiotPs = cache.LoadShader(pixelToc.Path, psPerm.BlobIndex, out _)!,
            };
        }
    }

    private static string Shots()
    {
        string d = System.Environment.GetEnvironmentVariable("REYENGINE_M832_SHOTS") ?? Path.Combine(Path.GetTempPath(), "reyengine_m832_shots");
        Directory.CreateDirectory(d);
        return d;
    }

    private static byte[] Texture()
    {
        const int n = 256;
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float u = (x + 0.5f) / n, v = (y + 0.5f) / n;
                float c = (((int)(u * 8) + (int)(v * 8)) & 1) == 0 ? 1f : 0.6f;
                int o = (y * n + x) * 4;
                px[o] = (byte)(u * c * 255); px[o + 1] = (byte)(v * c * 255); px[o + 2] = (byte)((1 - u) * 0.8f * c * 255); px[o + 3] = 255;
            }
        return px;
    }

    private static bool Clear(byte[] f, int i) =>
        Math.Abs(f[i] - 0.07f * 255) < 3 && Math.Abs(f[i + 1] - 0.05f * 255) < 3 && Math.Abs(f[i + 2] - 0.04f * 255) < 3;

    /// <summary>The covered pixels' mean colour (r, g, b, 0..255) and their count.</summary>
    private static (double R, double G, double B, int N) Mean(byte[] f)
    {
        double r = 0, g = 0, b = 0; int n = 0;
        for (int i = 0; i + 3 < f.Length; i += 4)
        {
            if (Clear(f, i)) continue;
            b += f[i]; g += f[i + 1]; r += f[i + 2]; n++;
        }
        return n == 0 ? (0, 0, 0, 0) : (r / n, g / n, b / n, n);
    }

    private static double MeanDiff(byte[] a, byte[] b)
    {
        double sum = 0; int n = 0;
        for (int i = 0; i + 3 < a.Length; i += 4)
        {
            if (Clear(a, i) && Clear(b, i)) continue;
            sum += (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2])) / 3.0; n++;
        }
        return n == 0 ? 0 : sum / n;
    }

    private byte[]? Render(Rig rig, DxbcShader ps, string name, Dictionary<string, float[]>? overrides = null, float time = 1.25f, bool texture = true)
    {
        using var r = new ShaderPreviewRenderer();
        if (!r.Initialize(out var initError)) { output.WriteLine("SKIPPED: no D3D11 device here: " + initError); return null; }
        var report = r.LoadShaders(rig.Vs, ps);
        Assert.True(report.Success, report.Error);
        r.SetMesh(PreviewGeometry.CreateBuiltIn("Plane"));
        if (texture) r.SetTexture("DiffuseTexture__TX", Texture(), 256, 256);
        foreach (var (n, v) in overrides ?? new()) r.Overrides[n] = v;
        var s = new PreviewSettings
        {
            Yaw = 0f, Pitch = 1.5f, Distance = 3.2f, AlphaBlend = false, DepthTest = true, CullBackFaces = false,
            ClearColor = new Vector4(0.04f, 0.05f, 0.07f, 1f), TimeSeconds = time, MirrorX = false, TransposeMatrices = true,
        };
        var frame = r.RenderFrame(512, 512, s, out var error, new List<string>());
        Assert.True(frame is not null, error);
        var copy = frame!.ToArray();
        ChromaRecolourRealDataTests.WritePng(Path.Combine(Shots(), name + ".png"), copy, 512, 512);
        output.WriteLine($"rendered {name} -> {Path.Combine(Shots(), name + ".png")}");
        return copy;
    }

    private static SgCompiled CompiledFor(Rig rig, ShaderGraphDocument d)
    {
        var build = ShaderGraphCompiler.BuildAll(d, rig.Base);
        Assert.True(build.Ok, string.Join("\n", build.Diagnostics) + "\n" + string.Join("\n", build.Compiled.SelectMany(c => c.Errors)));
        return build.ForRiotPixel(rig.RiotPs) ?? throw new InvalidOperationException("no compiled shader for Riot's pixel signature");
    }

    // ================================================================================= every signature

    [Fact]
    public void The_new_nodes_a_custom_parameter_and_a_Custom_node_compile_and_verify_for_every_input_signature_of_DefaultEnv_Flat()
    {
        using var rig = Rig.Open(output);
        if (rig is null) return;
        var d = ShaderGraphDocument.CreateEmpty("everything", Shader);
        d.Params.Add(new SgCustomParam { Name = "Glow", Components = 4, Default = "0.9 0.5 0.1 1", IsColor = true });
        d.Params.Add(new SgCustomParam { Name = "Amount", Components = 1, Default = "0.5" });
        void A(string id, string type, params (string, string)[] p) => ShaderGraphTests.Add(d, id, type, p);
        void L(string a, string ap, string b, string bp) => ShaderGraphTests.Link(d, a, ap, b, bp);
        A("tx", "TextureSample", ("texture", "DiffuseTexture"));
        A("pa", "Panner", ("Speed", "0.1")); A("ro", "Rotator"); A("to", "TilingOffset", ("Tiling", "2"));
        L("pa", "UV", "ro", "UV"); L("ro", "UV", "to", "UV"); L("to", "UV", "tx", "UV");
        A("gl", "Parameter", ("name", "Glow")); A("am", "Parameter", ("name", "Amount"));
        A("gm", "ComponentMask", ("mask", "rgb")); L("gl", "Value", "gm", "Value");
        A("ds", "Desaturation"); L("tx", "RGB", "ds", "Color"); L("am", "Value", "ds", "Fraction");
        A("cr", "Cross"); L("ds", "Value", "cr", "A"); L("gm", "Value", "cr", "B");
        A("rf", "Reflect"); L("cr", "Value", "rf", "Incident"); L("gm", "Value", "rf", "Normal");
        A("fm", "Fmod", ("B", "0.7")); L("rf", "Value", "fm", "A");
        A("sg", "Sign"); L("fm", "Value", "sg", "Value");
        A("ce", "Ceil"); L("sg", "Value", "ce", "Value");
        A("rn", "Round"); L("ce", "Value", "rn", "Value");
        A("le", "Length"); L("rn", "Value", "le", "Value");
        A("di", "Distance"); L("ds", "Value", "di", "A"); L("cr", "Value", "di", "B");
        A("if", "If"); L("le", "Value", "if", "A"); L("di", "Value", "if", "B"); L("ds", "Value", "if", "A > B"); L("rn", "Value", "if", "A == B"); L("cr", "Value", "if", "A < B");
        A("cu", "Custom", ("inputs", "Base:float3;Tint:float4;K:float"), ("out", "float3"), ("code", "float3 r = Base * Tint.rgb;\nif (K > 0.5) r = 1.0 - r;\nreturn r;"));
        L("if", "Value", "cu", "Base"); L("gl", "Value", "cu", "Tint"); L("am", "Value", "cu", "K");
        A("cc", "ConstantColor", ("value", "0.1 0.2 0.3 1")); A("cm", "ComponentMask", ("mask", "rgb")); L("cc", "Value", "cm", "Value");
        A("mix", "Lerp"); L("cu", "Value", "mix", "A"); L("cm", "Value", "mix", "B");
        L("mix", "Value", "n1", SgCatalog.BaseColorPin); L("tx", "A", "n1", SgCatalog.OpacityPin);

        var build = ShaderGraphCompiler.BuildAll(d, rig.Base);
        output.WriteLine($"{build.Compiled.Count} signature(s); " + string.Join("; ", build.Compiled.Select(c => $"#{c.Signature.Index}:{(c.Ok ? "ok" : "FAIL")}")));
        Assert.True(build.Ok, string.Join("\n", build.Diagnostics) + "\n" + string.Join("\n", build.Compiled.SelectMany(c => c.Errors.Select(e => e.ToString()))) + "\n" + build.Compiled.FirstOrDefault(c => !c.Ok)?.CompilerLog);
        Assert.Equal(rig.Base.Signatures.Count, build.Compiled.Count);
        Assert.True(build.Compiled.Count >= 16, "DefaultEnv_Flat has 16 colour input signatures");
        Assert.All(build.Compiled, c =>
        {
            var globals = c.Shader!.ConstantBuffers.Single(b => b.Name == "$Globals");
            Assert.Contains(globals.Variables, v => v.Name == "Glow");
            Assert.Equal(0, globals.BindPoint);
            Assert.All(globals.Variables, v => Assert.True(v.Name is "Glow" or "Amount" or "TintColor", v.Name));
        });
        // the export is blocked by the validator, not by the compile
        Assert.Equal(new[] { "Glow", "Amount" }, SgCustomParams.UsedBy(d, SgAnalyzer.Analyze(d, rig.Base)).Select(p => p.Name));
    }

    // ================================================================================= renders

    [Fact]
    public void A_custom_float4_colour_parameter_drives_the_tint_on_the_real_preview()
    {
        using var rig = Rig.Open(output);
        if (rig is null) return;
        var d = ShaderGraphDocument.CreateEmpty("custom_tint", Shader);
        d.Params.Add(new SgCustomParam { Name = "GlowColor", Components = 4, Default = "0.2 0.9 0.3 1", IsColor = true });
        ShaderGraphTests.Add(d, "n2", "TextureSample", ("texture", "DiffuseTexture"));
        ShaderGraphTests.Add(d, "n3", "Parameter", ("name", "GlowColor"));
        ShaderGraphTests.Add(d, "n4", "Multiply");
        ShaderGraphTests.Add(d, "n5", "ComponentMask", ("mask", "rgb"));
        ShaderGraphTests.Link(d, "n2", "RGBA", "n4", "A"); ShaderGraphTests.Link(d, "n3", "Value", "n4", "B");
        ShaderGraphTests.Link(d, "n4", "Value", "n5", "Value");
        ShaderGraphTests.Link(d, "n5", "Value", "n1", SgCatalog.BaseColorPin); ShaderGraphTests.Link(d, "n2", "A", "n1", SgCatalog.OpacityPin);
        var ps = CompiledFor(rig, d).Shader!;

        var green = Render(rig, ps, "m834_custom_tint_green", new() { ["GlowColor"] = new[] { 0.2f, 0.9f, 0.3f, 1f } });
        if (green is null) return;
        var orange = Render(rig, ps, "m834_custom_tint_orange", new() { ["GlowColor"] = new[] { 1f, 0.45f, 0.05f, 1f } })!;
        var g = Mean(green); var o = Mean(orange);
        output.WriteLine($"green mean rgb ({g.R:0},{g.G:0},{g.B:0}) over {g.N}px; orange mean rgb ({o.R:0},{o.G:0},{o.B:0}) over {o.N}px");
        Assert.True(g.N > 5000 && o.N > 5000);
        Assert.True(g.G > g.R * 1.5 && g.G > g.B * 1.5, "the green parameter should make the plane green");
        Assert.True(o.R > o.G * 1.4 && o.R > o.B * 3, "the orange parameter should make the plane orange");
    }

    [Fact]
    public void A_Panner_scrolls_the_texture_between_two_frames()
    {
        using var rig = Rig.Open(output);
        if (rig is null) return;
        var d = ShaderGraphDocument.CreateEmpty("panner", Shader);
        ShaderGraphTests.Add(d, "n2", "TextureSample", ("texture", "DiffuseTexture"));
        ShaderGraphTests.Add(d, "n3", "Panner", ("Speed", "0.25"));
        ShaderGraphTests.Link(d, "n3", "UV", "n2", "UV");
        ShaderGraphTests.Link(d, "n2", "RGB", "n1", SgCatalog.BaseColorPin); ShaderGraphTests.Link(d, "n2", "A", "n1", SgCatalog.OpacityPin);
        var compiled = CompiledFor(rig, d);
        Assert.True(compiled.Hlsl.UsesTime);
        var ps = compiled.Shader!;

        var t0 = Render(rig, ps, "m834_panner_t0", time: 0f);
        if (t0 is null) return;
        var t1 = Render(rig, ps, "m834_panner_t1", time: 0.5f)!;
        var same = Render(rig, ps, "m834_panner_t0_again", time: 0f)!;
        double moved = MeanDiff(t0, t1), still = MeanDiff(t0, same);
        output.WriteLine($"mean difference t=0 vs t=0.5: {moved:0.0}; t=0 vs t=0 again: {still:0.0}");
        Assert.True(moved > 8, "the texture should have scrolled between the two times");
        Assert.True(still < 0.5, "the same time must give the same picture");
    }

    [Fact]
    public void A_Custom_node_draws_a_checker_on_the_real_preview()
    {
        using var rig = Rig.Open(output);
        if (rig is null) return;
        var d = ShaderGraphDocument.CreateEmpty("custom_checker", Shader);
        ShaderGraphTests.Add(d, "n2", "TexCoord", ("set", "1"));
        ShaderGraphTests.Add(d, "n3", "Custom", ("inputs", "UV:float2"), ("out", "float3"),
            ("code", "float2 cell = floor(UV * 8.0);\nfloat c = fmod(cell.x + cell.y, 2.0);\nreturn float3(c, c * 0.5, 1.0 - c);"));
        ShaderGraphTests.Link(d, "n2", "UV", "n3", "UV");
        ShaderGraphTests.Link(d, "n3", "Value", "n1", SgCatalog.BaseColorPin);
        var ps = CompiledFor(rig, d).Shader!;
        var f = Render(rig, ps, "m834_custom_checker", texture: false);
        if (f is null) return;
        // two colours only (blue and orange), in many cells
        var colours = new Dictionary<int, int>();
        for (int i = 0; i + 3 < f.Length; i += 4)
            if (!Clear(f, i)) { int key = (f[i] >> 5) | ((f[i + 1] >> 5) << 3) | ((f[i + 2] >> 5) << 6); colours[key] = colours.GetValueOrDefault(key) + 1; }
        var top = colours.OrderByDescending(kv => kv.Value).Take(2).Sum(kv => kv.Value);
        int total = colours.Values.Sum();
        output.WriteLine($"{colours.Count} quantised colours, the two biggest cover {top} of {total} px");
        Assert.True(total > 5000);
        Assert.True(top > total * 0.9, "a checker is two colours");
    }
}
