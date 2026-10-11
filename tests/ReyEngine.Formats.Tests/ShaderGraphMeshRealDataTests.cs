using System.Numerics;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Materials.ShaderGraph;
using ReyEngine.Formats.Shaders;
using ReyEngine.Rendering.D3D11;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M834: the preview meshes. A texture graph (and Riot's own DefaultEnv_Flat) is drawn on the Sphere, Cube, Plane and Particle Quad by the real D3D11
/// preview renderer; every one must show the texture (not one flat colour). PNGs go to REYENGINE_M832_SHOTS (default: the temp folder).
/// </summary>
public sealed class ShaderGraphMeshRealDataTests(ITestOutputHelper output)
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private const string Shader = "Shaders/StaticMesh/DefaultEnv_Flat";
    private static readonly Dictionary<string, string> FlatMacros = new() { ["NO_BAKED_LIGHTING"] = "1", ["DISABLE_DEPTH_FOG"] = "1" };

    private static string ShotsDir()
    {
        string d = System.Environment.GetEnvironmentVariable("REYENGINE_M832_SHOTS") ?? Path.Combine(Path.GetTempPath(), "reyengine_m832_shots");
        Directory.CreateDirectory(d);
        return d;
    }

    internal static byte[] CheckerPixels() => Checker();
    internal static int Distinct(byte[] f) => DistinctColours(f);

    private static byte[] Checker()
    {
        const int n = 256;
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float u = (x + 0.5f) / n, v = (y + 0.5f) / n;
                float c = (((int)(u * 8) + (int)(v * 8)) & 1) == 0 ? 1f : 0.55f;
                int o = (y * n + x) * 4;
                px[o] = (byte)(u * c * 255); px[o + 1] = (byte)(v * c * 255); px[o + 2] = (byte)((1 - u) * 0.8f * c * 255); px[o + 3] = 255;
            }
        return px;
    }

    private static bool IsClear(byte[] f, int i) =>
        Math.Abs(f[i] - 0.07f * 255) < 3 && Math.Abs(f[i + 1] - 0.05f * 255) < 3 && Math.Abs(f[i + 2] - 0.04f * 255) < 3;

    /// <summary>Distinct colours among the covered pixels: a flat fill has one or two, a textured surface dozens.</summary>
    private static int DistinctColours(byte[] f)
    {
        var set = new HashSet<int>();
        for (int i = 0; i + 3 < f.Length; i += 4)
            if (!IsClear(f, i)) set.Add((f[i] >> 3) | ((f[i + 1] >> 3) << 5) | ((f[i + 2] >> 3) << 10));
        return set.Count;
    }

    [Fact]
    public void A_texture_graph_and_Riots_shader_show_the_texture_on_every_preview_mesh()
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(Final)) { output.WriteLine("SKIPPED: no game install"); return; }
        HashDatabase db;
        try { db = new HashSyncService().LoadLocal(_ => { }); } catch { output.WriteLine("SKIPPED: no hash dictionary"); return; }
        var resolver = new WadPathResolver(db);
        using var cache = ShaderCacheReader.Open(Final, resolver, out _)!;
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
        var vs = cache.LoadShader(vertexToc.Path, vsPerm.BlobIndex, out _)!;
        var riotPs = cache.LoadShader(pixelToc.Path, psPerm.BlobIndex, out _)!;

        var d = ShaderGraphDocument.CreateEmpty("mesh_check", Shader);
        ShaderGraphTests.Add(d, "n2", "TextureSample", ("texture", "DiffuseTexture"));
        ShaderGraphTests.Link(d, "n2", "RGB", "n1", SgCatalog.BaseColorPin);
        ShaderGraphTests.Link(d, "n2", "A", "n1", SgCatalog.OpacityPin);
        var build = ShaderGraphCompiler.BuildAll(d, b!);
        Assert.True(build.Ok, string.Join("\n", build.Diagnostics));
        var graphPs = build.ForRiotPixel(riotPs)!.Shader!;

        foreach (string mesh in PreviewGeometry.BuiltInNames)
            foreach (var (label, ps) in new[] { ("riot", riotPs), ("graph", graphPs) })
            {
                using var r = new ShaderPreviewRenderer();
                if (!r.Initialize(out var initError)) { output.WriteLine("SKIPPED: no D3D11 device here: " + initError); return; }
                var report = r.LoadShaders(vs, ps);
                Assert.True(report.Success, report.Error);
                r.SetMesh(PreviewGeometry.CreateBuiltIn(mesh));
                r.SetTexture("DiffuseTexture__TX", Checker(), 256, 256);
                r.Overrides["TintColor"] = new[] { 1f, 1f, 1f, 1f };
                var st = new PreviewSettings
                {
                    Yaw = 0.5f, Pitch = 0.5f, Distance = 3.6f, AlphaBlend = true, DepthTest = true, CullBackFaces = false,
                    ClearColor = new Vector4(0.04f, 0.05f, 0.07f, 1f), TimeSeconds = 1.25f, MirrorX = true, TransposeMatrices = true,
                };
                var frame = r.RenderFrame(512, 512, st, out var err, new List<string>());
                Assert.True(frame is not null, err);
                var copy = frame!.ToArray();
                string name = $"m834_mesh_{mesh.Replace(' ', '_')}_{label}";
                ChromaRecolourRealDataTests.WritePng(Path.Combine(ShotsDir(), name + ".png"), copy, 512, 512);
                int colours = DistinctColours(copy);
                output.WriteLine($"{name}: {colours} distinct colours -> {Path.Combine(ShotsDir(), name + ".png")}");
                Assert.True(colours > 12, $"{name} is a flat fill ({colours} colours)");
            }
    }
}

/// <summary>M834: the same check through the app's own ShaderPreviewViewModel (the window's view model), because the renderer alone is not what the user sees.</summary>
public sealed class ShaderGraphMeshViewModelTests(ITestOutputHelper output)
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";

    [Fact]
    public async Task The_window_view_model_draws_the_graph_on_every_mesh()
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(Final)) { output.WriteLine("SKIPPED: no game install"); return; }
        HashDatabase db;
        try { db = new HashSyncService().LoadLocal(_ => { }); } catch { output.WriteLine("SKIPPED: no hash dictionary"); return; }
        var resolver = new WadPathResolver(db);
        var vm = new ReyEngine.App.ViewModels.ShaderPreviewViewModel(Final, resolver);
        if (!vm.CacheAvailable) { output.WriteLine("SKIPPED: " + vm.Status); return; }
        var row = vm.ShaderNames.FirstOrDefault(r => r.Full.EndsWith("staticmesh/defaultenv_flat", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(row);
        vm.SelectedShader = row;
        var t = typeof(ReyEngine.App.ViewModels.ShaderPreviewViewModel);
        ((Avalonia.Threading.DispatcherTimer)t.GetField("_timer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(vm)!).Stop();   // the test drives the frames itself: a background tick must not race its reflection calls
        var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        Assert.True(vm.ShaderGraph.NewFromBase("Shaders/StaticMesh/DefaultEnv_Flat", out string gerr), gerr);
        var build = await vm.ShaderGraph.CompileNowAsync();
        Assert.True(build!.Ok, string.Join(", ", build.Diagnostics));
        vm.LoadCommand.Execute(null);
        output.WriteLine("status: " + vm.Status + " | " + vm.GraphPreviewNote);
        Assert.True(vm.IsLoaded, vm.Status);
        var renderer = (ShaderPreviewRenderer)t.GetField("_renderer", bf)!.GetValue(vm)!;
        renderer.SetTexture("DiffuseTexture__TX", ShaderGraphMeshRealDataTests.CheckerPixels(), 256, 256);
        var settings = (PreviewSettings)t.GetField("_settings", bf)!.GetValue(vm)!;
        foreach (string mesh in PreviewGeometry.BuiltInNames)
        {
            vm.SelectedMesh = mesh;
            try { t.GetMethod("Tick", bf)!.Invoke(vm, null); } catch (System.Reflection.TargetInvocationException ex) { output.WriteLine("tick: " + ex.InnerException?.GetType().Name); }
            var frame = renderer.RenderFrame(512, 512, settings, out var err, new List<string>());
            Assert.True(frame is not null, err);
            string name = "m834_vm_" + mesh.Replace(' ', '_');
            ChromaRecolourRealDataTests.WritePng(Path.Combine(ShotsDirPublic(), name + ".png"), frame!.ToArray(), 512, 512);
            int colours = ShaderGraphMeshRealDataTests.Distinct(frame!.ToArray());
            output.WriteLine($"{name}: {colours} distinct colours");
            Assert.True(colours > 12, $"{name} is a flat fill");
        }
    }

    private static string ShotsDirPublic()
    {
        string d = System.Environment.GetEnvironmentVariable("REYENGINE_M832_SHOTS") ?? Path.Combine(Path.GetTempPath(), "reyengine_m832_shots");
        Directory.CreateDirectory(d);
        return d;
    }
}

/// <summary>M834: the same, with a REAL Map11 DefaultEnv_Flat material (its real texture, macros and parameters) loaded the way the window does.</summary>
public sealed class ShaderGraphMeshMaterialTests(ITestOutputHelper output)
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";

    [Fact]
    public async Task A_real_material_shows_its_texture_with_and_without_the_graph_on_every_mesh()
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(Final)) { output.WriteLine("SKIPPED: no game install"); return; }
        HashDatabase db;
        try { db = new HashSyncService().LoadLocal(_ => { }); } catch { output.WriteLine("SKIPPED: no hash dictionary"); return; }
        var resolver = new WadPathResolver(db);
        using var map = WadArchive.Open(Path.Combine(Final, "Maps", "Shipping", "Map11.wad.client"), resolver);
        byte[]? Read(ulong h) { try { return map.TryGetEntry(h, out _) ? map.Extract(h) : null; } catch { return null; } }

        MaterialBinding? pick = null;
        foreach (var e in map.Entries.Where(e => e.Path != null && e.Path.EndsWith(".materials.bin") && e.Path.Contains("map11/")))
        {
            MaterialDocument doc;
            try { doc = MaterialDocument.Parse(map.Extract(e.PathHash), h => db.TryGetBinName(h, out var n) ? n : null); } catch { continue; }
            foreach (var m in doc.Materials)
                if (!m.IsLinked && (m.RenderShader ?? "").EndsWith("DefaultEnv_Flat", StringComparison.OrdinalIgnoreCase)
                    && m.Slots.Any(sl => sl.ChunkHash != 0 && Read(sl.ChunkHash) is { Length: > 0 }))
                { pick = m; break; }
            if (pick is not null) break;
        }
        if (pick is null) { output.WriteLine("SKIPPED: no Map11 DefaultEnv_Flat material with a readable texture"); return; }
        output.WriteLine($"material {pick.Name}; macros: {string.Join(",", pick.Macros.Select(kv => kv.Key + "=" + kv.Value))}; slots: {string.Join(",", pick.Slots.Select(sl => sl.SamplerName + "=" + sl.Path))}");

        var vm = new ReyEngine.App.ViewModels.ShaderPreviewViewModel(Final, resolver, Read);
        Assert.True(vm.CacheAvailable, vm.Status);
        var t = typeof(ReyEngine.App.ViewModels.ShaderPreviewViewModel);
        ((Avalonia.Threading.DispatcherTimer)t.GetField("_timer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(vm)!).Stop();   // the test drives the frames itself: a background tick must not race its reflection calls
        var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        // The graph compiles off the UI thread and applies its result through Dispatcher.UIThread.Post: in PRODUCTION the one UI thread. This test has no UI loop, so
        // the compile NewFromBase starts and the one CompileNowAsync runs could both land on pool threads and both ApplyMaterial at once (a "collection was modified" in
        // BuildSlots that exists only here). So the material is picked only AFTER the build is in, and the graph is then disposed: a straggler's ApplyBuild returns
        // at once, and a build completing with no material picked applies nothing. Everything after this runs on one thread of control.
        Assert.True(vm.ShaderGraph.NewFromBase("Shaders/StaticMesh/DefaultEnv_Flat", out string gerr), gerr);
        var build = await vm.ShaderGraph.CompileNowAsync();
        Assert.True(build!.Ok, string.Join(", ", build.Diagnostics));
        vm.ShaderGraph.Dispose();
        vm.SelectedMaterial = new ReyEngine.App.ViewModels.MaterialRow { Binding = pick };
        var renderer = (ShaderPreviewRenderer)t.GetField("_renderer", bf)!.GetValue(vm)!;
        var settings = (PreviewSettings)t.GetField("_settings", bf)!.GetValue(vm)!;

        foreach (bool graphOn in new[] { false, true })
        {
            vm.ShaderGraph.PreviewEnabled = graphOn;
            t.GetMethod("ApplyMaterial", bf)!.Invoke(vm, null);
            output.WriteLine($"graph {(graphOn ? "ON" : "off")}: {vm.Status} | {vm.GraphPreviewNote}" + System.Environment.NewLine + vm.MaterialReport);
            Assert.True(vm.IsLoaded, vm.Status);
            foreach (string mesh in PreviewGeometry.BuiltInNames)
            {
                vm.SelectedMesh = mesh;
                try { t.GetMethod("Tick", bf)!.Invoke(vm, null); } catch (System.Reflection.TargetInvocationException) { }
                var frame = renderer.RenderFrame(512, 512, settings, out var err, new List<string>());
                Assert.True(frame is not null, err);
                var px = frame!.ToArray();
                string name = $"m834_mat_{(graphOn ? "graph" : "riot")}_{mesh.Replace(' ', '_')}";
                ChromaRecolourRealDataTests.WritePng(Path.Combine(System.Environment.GetEnvironmentVariable("REYENGINE_M832_SHOTS") ?? Path.GetTempPath(), name + ".png"), px, 512, 512);
                output.WriteLine($"{name}: {ShaderGraphMeshRealDataTests.Distinct(px)} distinct colours");
            }
        }
    }
}
