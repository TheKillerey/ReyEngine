using System.Numerics;
using ReyEngine.App.Services;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Materials.ShaderGraph;
using ReyEngine.Formats.Shaders;
using ReyEngine.Rendering.D3D11;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M835 against Riot's installed game (16.20 when written) and a real D3D11 device: the custom texture declaration compared with Riot's own DiffuseTexture entry, every
/// template compiled for all 16 pixel input signatures and rendered, and - the main point - the MAP VIEWPORT path (<c>Dx11SceneBuilder.Prepare/Commit</c>, the same code the
/// viewport and the thumbnails run) drawing a Map11 DefaultEnv_Flat material with its Shader Graph instead of Riot's shader. PNGs go to REYENGINE_M835_SHOTS (default: the temp
/// folder). They are the evidence; every assertion also states what the picture shows. No-ops with a SKIPPED line where the game or the hash dictionary is absent.
/// </summary>
[Collection("ShaderGraphViewportStatics")]
public sealed class ShaderGraphUsabilityRealDataTests(ITestOutputHelper output) : IDisposable
{
    private const string Game = @"C:\Riot Games\League of Legends";
    private const string Final = Game + @"\Game\DATA\FINAL";
    private const string Shader = "Shaders/StaticMesh/DefaultEnv_Flat";
    private const string BaseSrx = "data/maps/mapgeometry/map11/base_srx.materials.bin";
    private const string MapGeo = "data/maps/mapgeometry/map11/base_srx.mapgeo";
    private const string Ground = "Worlds_Ground_A5_OrderBase_A_MAT";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rey-m835-real-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        ShaderGraphViewport.Reset();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    internal static string Shots()
    {
        string d = System.Environment.GetEnvironmentVariable("REYENGINE_M835_SHOTS") ?? Path.Combine(Path.GetTempPath(), "reyengine_m835_shots");
        Directory.CreateDirectory(d);
        return d;
    }

    private sealed class Rig : IDisposable
    {
        public required HashDatabase Db { get; init; }
        public required WadPathResolver Resolver { get; init; }
        public required ShaderCacheReader Cache { get; init; }
        public required ShaderPermutationIndex Perms { get; init; }
        public required SgBase Base { get; init; }
        public void Dispose() { Cache.Dispose(); Perms.Dispose(); }
    }

    private Rig? Open()
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(Final)) { output.WriteLine("SKIPPED: no game install"); return null; }
        HashDatabase db;
        try { db = new HashSyncService().LoadLocal(_ => { }); } catch { output.WriteLine("SKIPPED: no hash dictionary"); return null; }
        var resolver = new WadPathResolver(db);
        var cache = ShaderCacheReader.Open(Final, resolver, out _);
        if (cache is null) { output.WriteLine("SKIPPED: the shader cache did not open"); return null; }
        var perms = new ShaderPermutationIndex(Final, h => resolver.TryGetPath(h, out var p) ? p : null);
        var b = ShaderGraphBaseResolver.Resolve(cache, perms, Shader, out string error, out _);
        if (b is null) { output.WriteLine("SKIPPED: " + error); cache.Dispose(); perms.Dispose(); return null; }
        return new Rig { Db = db, Resolver = resolver, Cache = cache, Perms = perms, Base = b };
    }

    private static byte[] InstalledShadersBin()
    {
        using var g = WadArchive.Open(Path.Combine(Final, SgShadersBin.WadName));
        return g.Extract(HashAlgorithms.WadPath(SgShadersBin.GamePath));
    }

    // ================================================================================= the texture declaration

    [Fact]
    public void A_declared_custom_texture_is_a_copy_of_Riots_DiffuseTexture_entry_with_only_the_name_changed()
    {
        if (!Directory.Exists(Final)) { output.WriteLine("SKIPPED: no game install"); return; }
        byte[] riot = InstalledShadersBin();
        var before = SgShadersBin.DeclaredTextures(riot, Shader);
        output.WriteLine($"{Shader} declares textures: {string.Join(", ", before)}");
        Assert.Contains("DiffuseTexture", before);

        var add = new[] { new SgDeclaredTexture("StripesTexture", ""), new SgDeclaredTexture("MaskTexture", "assets/maps/particles/x/y.tex") };
        byte[] patched = SgShadersBin.Patch(riot, Shader, Array.Empty<SgDeclaredParam>(), add);
        Assert.Empty(SgShadersBin.Verify(riot, patched, Shader, Array.Empty<SgDeclaredParam>(), add));
        Assert.Equal(before.Concat(new[] { "StripesTexture", "MaskTexture" }), SgShadersBin.DeclaredTextures(patched, Shader));
        Assert.Equal(SgShadersBin.Declared(riot, Shader), SgShadersBin.Declared(patched, Shader));       // the parameters are Riot's

        // the entry bytes: same class hash, same two fields in the same order and types; only the name (and, when a path is named, the link) differ
        var d = SgShadersBin.TextureEntryBytes(patched, Shader);
        var diffuse = d["DiffuseTexture"]; var plain = d["StripesTexture"]; var linked = d["MaskTexture"];
        output.WriteLine($"entry bytes: DiffuseTexture {diffuse.Length}, Stripes {plain.Length}, Mask {linked.Length}");
        Assert.Equal(BitConverter.ToUInt32(diffuse, 0), BitConverter.ToUInt32(plain, 0));              // the class hash
        Assert.Equal(2, BitConverter.ToUInt16(diffuse, 8));                                            // exactly two fields: name and defaultTexturePath
        Assert.Equal(2, BitConverter.ToUInt16(plain, 8));
        // with no default path of its own the entry carries Riot's DiffuseTexture default link verbatim
        var tail = diffuse.AsSpan(diffuse.Length - 13);                                               // hash(4) type(1) link(8)
        Assert.Equal(0x12, tail[4]);                                                                   // a WadChunkLink
        Assert.True(plain.AsSpan(plain.Length - 13).SequenceEqual(tail));
        Assert.Equal(HashAlgorithms.Fnv1a("defaultTexturePath"), BitConverter.ToUInt32(tail));
        // a named default path becomes its chunk link
        Assert.Equal(HashAlgorithms.WadPath("assets/maps/particles/x/y.tex"), BitConverter.ToUInt64(linked, linked.Length - 8));
        Assert.Equal(plain.Length - "StripesTexture".Length, linked.Length - "MaskTexture".Length);     // the same shape, name apart

        // every other object of shaders.bin is Riot's, byte for byte (Verify) and the guards hold
        Assert.Throws<InvalidDataException>(() => SgShadersBin.Patch(riot, Shader, Array.Empty<SgDeclaredParam>(), new[] { new SgDeclaredTexture("DiffuseTexture", "") }));
        Assert.Throws<InvalidDataException>(() => SgShadersBin.Patch(riot, Shader, Array.Empty<SgDeclaredParam>(), new[] { new SgDeclaredTexture("TintColor", "") }));
        Assert.Throws<InvalidDataException>(() => SgShadersBin.Patch(riot, Shader, Array.Empty<SgDeclaredParam>(), new[] { new SgDeclaredTexture("a", ""), new SgDeclaredTexture("A", "") }));
        Assert.Throws<InvalidDataException>(() => SgShadersBin.Patch(riot, Shader, new[] { new SgDeclaredParam("Same", 1) }, new[] { new SgDeclaredTexture("same", "") }));
        var tampered = (byte[])patched.Clone();
        tampered[^3] ^= 0xFF;
        Assert.NotEmpty(SgShadersBin.Verify(riot, tampered, Shader, Array.Empty<SgDeclaredParam>(), add));
    }

    // ================================================================================= the templates

    [Fact]
    public void Every_template_compiles_for_all_signatures_of_the_real_base()
    {
        using var rig = Open();
        if (rig is null) return;
        Assert.Equal(16, rig.Base.Signatures.Count);
        foreach (var t in SgTemplates.All)
        {
            var d = SgTemplates.Build(t.Id, "T_" + t.Id, rig.Base);
            var build = ShaderGraphCompiler.BuildAll(d, rig.Base);
            output.WriteLine($"{t.Id}: {build.Compiled.Count} of {rig.Base.Signatures.Count} signatures, ok={build.Ok}");
            Assert.True(build.Ok, t.Id + ": " + string.Join("\n", build.Diagnostics));
            Assert.Equal(16, build.Compiled.Count(c => c.Ok));
        }
    }

    // ================================================================================= the templates, drawn

    private static byte[] Pattern(int kind)
    {
        const int n = 256;
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float u = (x + 0.5f) / n, v = (y + 0.5f) / n;
                float c = (((int)(u * 8) + (int)(v * 8)) & 1) == 0 ? 1f : 0.55f;
                (float r, float g, float b) = kind switch
                {
                    0 => (u * c, v * c, (1 - u) * 0.8f * c),                       // a gradient checker
                    1 => (0.2f * c, 0.9f * c, 0.4f * c),                           // green checker
                    _ => ((((int)(u * 4) + (int)(v * 4)) & 1) == 0 ? 1f : 0.05f, 0f, 0f),    // a mask: big red/black squares
                };
                int o = (y * n + x) * 4;
                px[o] = (byte)(r * 255); px[o + 1] = (byte)(g * 255); px[o + 2] = (byte)(b * 255); px[o + 3] = 255;
            }
        return px;
    }

    [Fact]
    public void Each_template_renders_on_the_sphere_with_its_textures_and_none_is_a_flat_fill()
    {
        using var rig = Open();
        if (rig is null) return;
        var (vs, riotPs) = LoadFlat(rig);
        foreach (var t in SgTemplates.All.Where(t => t.Id != SgTemplates.Empty))
        {
            var d = SgTemplates.Build(t.Id, "T_" + t.Id, rig.Base);
            var sig = rig.Base.SignatureOf(riotPs)!;
            var c = ShaderGraphCompiler.Compile(d, rig.Base, sig);
            Assert.True(c.Ok, t.Id + ": " + string.Join("\n", c.Errors));

            using var r = new ShaderPreviewRenderer();
            if (!r.Initialize(out var initError)) { output.WriteLine("SKIPPED: no D3D11 device: " + initError); return; }
            var report = r.LoadShaders(vs, c.Shader!);
            Assert.True(report.Success, report.Error);
            r.SetMesh(PreviewGeometry.CreateBuiltIn("Sphere"));
            r.SetTexture("DiffuseTexture__TX", Pattern(0), 256, 256);
            if (d.Textures.Any(x => x.Name == "SecondTexture")) r.SetTexture("SecondTexture__TX", Pattern(1), 256, 256);
            if (d.Textures.Any(x => x.Name == "MaskTexture")) r.SetTexture("MaskTexture__TX", Pattern(2), 256, 256);
            r.Overrides["TintColor"] = new[] { 0.9f, 0.6f, 0.4f, 1f };
            foreach (var p in d.Params) r.Overrides[p.Name] = p.DefaultValues().Concat(new float[4]).Take(4).ToArray();
            var st = new PreviewSettings
            {
                Yaw = 0.5f, Pitch = 0.4f, Distance = 3.6f, AlphaBlend = true, DepthTest = true, CullBackFaces = false,
                ClearColor = new Vector4(0.04f, 0.05f, 0.07f, 1f), TimeSeconds = 1.25f, MirrorX = true, TransposeMatrices = true,
            };
            var frame = r.RenderFrame(512, 512, st, out var err, new List<string>());
            Assert.True(frame is not null, err);
            var bytes = frame!.ToArray();
            string file = Path.Combine(Shots(), $"m835_template_{t.Id}.png");
            ChromaRecolourRealDataTests.WritePng(file, bytes, 512, 512);
            int colours = ShaderGraphMeshRealDataTests.Distinct(bytes);
            output.WriteLine($"{t.Id}: {colours} distinct colours -> {file}");
            Assert.True(colours > 12, $"{t.Id} is a flat fill ({colours} colours)");
        }
    }

    private (DxbcShader Vs, DxbcShader RiotPs) LoadFlat(Rig rig)
    {
        string full = "assets/shaders/generated/" + Shader.ToLowerInvariant();
        var pixelToc = rig.Cache.ReadToc(ShaderCacheReader.TocPathFor(full, DxbcStage.Pixel))!;
        var vertexToc = rig.Cache.ReadToc(ShaderCacheReader.TocPathFor(full, DxbcStage.Vertex))!;
        IReadOnlyDictionary<string, string>? feat = null; IReadOnlyDictionary<string, bool>? sw = null;
        if (rig.Perms.TryGetShaderDefs(Shader, out var f, out var s)) { feat = f; sw = s; }
        var macros = new Dictionary<string, string> { ["NO_BAKED_LIGHTING"] = "1", ["DISABLE_DEPTH_FOG"] = "1" };
        var vsPerm = ShaderCacheReader.ResolvePermutation(vertexToc, macros, new Dictionary<string, bool>(), feat, sw, out _)!;
        var psPerm = ShaderCacheReader.ResolvePermutation(pixelToc, macros, new Dictionary<string, bool>(), feat, sw, out _)!;
        return (rig.Cache.LoadShader(vertexToc.Path, vsPerm.BlobIndex, out _)!, rig.Cache.LoadShader(pixelToc.Path, psPerm.BlobIndex, out _)!);
    }

    // ================================================================================= the map viewport path

    private sealed class MapRig : IDisposable
    {
        public required Rig Rig { get; init; }
        public required MapGeoAsset Map { get; init; }
        public required MaterialDocument Doc { get; init; }
        public required MaterialBinding Ground { get; init; }
        public required Func<ulong, byte[]?> Read { get; init; }
        public required WadArchive Wad { get; init; }
        public ShaderPreviewRenderer Renderer { get; } = new();
        public bool Ready { get; set; }
        public void Dispose() { Renderer.Dispose(); Wad.Dispose(); Rig.Dispose(); }
    }

    private MapRig? OpenMap()
    {
        var rig = Open();
        if (rig is null) return null;
        string map11 = Path.Combine(Final, "Maps", "Shipping", "Map11.wad.client");
        if (!File.Exists(map11)) { output.WriteLine("SKIPPED: no Map11.wad.client"); rig.Dispose(); return null; }
        var wad = WadArchive.Open(map11, rig.Resolver);
        ulong geoHash = HashAlgorithms.WadPath(MapGeo), binHash = HashAlgorithms.WadPath(BaseSrx);
        if (!wad.TryGetEntry(geoHash, out _) || !wad.TryGetEntry(binHash, out _)) { output.WriteLine("SKIPPED: Map11 has no base_srx"); wad.Dispose(); rig.Dispose(); return null; }
        string? BinName(uint h) => rig.Db.TryGetBinName(h, out var n) ? n : null;
        string? WadPathOf(ulong h) => rig.Db.TryGetPath(h, out var p) ? p : null;
        var bin = wad.Extract(binHash);
        var map = MapGeoDecoder.Decode(wad.Extract(geoHash), ExtendedChannelRule.From(bin, BinName));
        var doc = MaterialDocument.Parse(bin, BinName, WadPathOf);
        var ground = doc.Materials.Single(m => m.Name.EndsWith(Ground, StringComparison.OrdinalIgnoreCase));
        byte[]? Read(ulong h) { try { return wad.TryGetEntry(h, out _) ? wad.Extract(h) : null; } catch { return null; } }
        return new MapRig { Rig = rig, Map = map, Doc = doc, Ground = ground, Read = Read, Wad = wad };
    }

    private sealed record Shot(byte[] Bgra, Dx11SceneBuilder.PreparedScene Scene);

    /// <summary>The map viewport's code path for ONE material: Prepare and Commit as the viewport runs them, then a close frame of that material's geometry.</summary>
    private Shot? DrawGround(MapRig m, string name, float time = 2.0f)
    {
        var scene = Dx11SceneBuilder.Prepare(m.Rig.Cache, m.Rig.Perms, m.Map, new[] { m.Ground }, m.Read, MapGeo, null, false);
        if (scene.Slices.Count == 0) { output.WriteLine("no slice: " + string.Join("; ", scene.FailureReasons)); return null; }
        if (!m.Ready) { if (!m.Renderer.Initialize(out var initError)) { output.WriteLine("SKIPPED: no D3D11 device: " + initError); return null; } m.Ready = true; }
        Dx11SceneBuilder.Commit(m.Renderer, scene, "16.20");
        var ranges = scene.Slices.Select(s => (s.Start, s.Count)).ToList();
        var centroids = MapThumbnailCamera.SampleCentroids(m.Map.Positions, m.Map.Indices, ranges);
        if (!MapThumbnailCamera.TryFrame(centroids, 1f, out var frame)) { output.WriteLine("no extent"); return null; }
        var settings = new PreviewSettings
        {
            SuppliedView = frame.View, SuppliedProjection = frame.Projection, SuppliedCameraPosition = frame.Eye,
            AlphaBlend = true, DepthTest = true, CullBackFaces = true, SortByPipeline = true, MirrorX = true, TransposeMatrices = true,
            DebugMode = 1, ClearColor = new Vector4(0.04f, 0.05f, 0.07f, 1f), TimeSeconds = time,
        };
        var bytes = m.Renderer.RenderFrame(640, 640, settings, out string? err);
        Assert.True(bytes is not null, err);
        var copy = (byte[])bytes!.Clone();
        string file = Path.Combine(Shots(), $"m835_viewport_{name}.png");
        ChromaRecolourRealDataTests.WritePng(file, copy, 640, 640);
        output.WriteLine($"{name}: {scene.GraphSlices} graph slice(s); {string.Join("; ", scene.GraphNotes.Keys)} -> {file}");
        return new Shot(copy, scene);
    }

    private static double Diff(byte[] a, byte[] b)
    {
        long sum = 0; long n = 0;
        for (int i = 0; i + 3 < a.Length; i += 4) { sum += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]); n += 3; }
        return sum / (double)n;
    }

    /// <summary>The share of covered pixels whose colour is close to <paramref name="target"/> (r, g, b 0..255).</summary>
    private static double ShareNear(byte[] bgra, (int R, int G, int B) target, int tolerance = 70)
    {
        long near = 0, covered = 0;
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            if (Math.Abs(bgra[i + 2] - 10) < 4 && Math.Abs(bgra[i + 1] - 13) < 4 && Math.Abs(bgra[i] - 18) < 4) continue;   // the clear colour
            covered++;
            if (Math.Abs(bgra[i + 2] - target.R) + Math.Abs(bgra[i + 1] - target.G) + Math.Abs(bgra[i] - target.B) < tolerance) near++;
        }
        return covered == 0 ? 0 : near / (double)covered;
    }

    /// <summary>The scratch project of the viewport tests: a registry and a stripes graph, numbered 1.</summary>
    private (string Root, ShaderGraphDocument Graph, string File) Project(ShaderGraphDocument? graph = null)
    {
        string project = Path.Combine(_root, "project");
        Directory.CreateDirectory(ShaderGraphRegistry.FolderIn(project));
        var g = graph ?? ShaderGraphCustomExportRealDataTests.Test5Graph();
        string file = ShaderGraphRegistry.GraphPath(project, "Stripes");
        File.WriteAllText(file, ShaderGraphJson.Serialize(g));
        Assert.Equal(1, ShaderGraphRegistry.NumberFor(project, "Stripes", Shader));
        ShaderGraphViewport.Reset();
        ShaderGraphViewport.ProjectRoot = () => project;
        return (project, g, file);
    }

    private static void Param(MaterialBinding b, string name, string value)
    {
        var p = b.Parameters.FirstOrDefault(x => x.Name == name) ?? b.AddParameter(name, "Vector4")!;
        p.Apply(value);
    }

    [Fact]
    public void The_map_viewport_draws_the_graph_not_Riots_shader_follows_the_materials_values_and_edits_and_goes_back_when_unassigned()
    {
        using var m = OpenMap();
        if (m is null) return;
        var (root, graph, file) = Project();

        // ---- 1. Riot's shader: the ground texture, as the game draws it
        var riot = DrawGround(m, "1_riot");
        if (riot is null) return;
        Assert.Equal(0, riot.Scene.GraphSlices);
        Assert.True(ShareNear(riot.Bgra, (255, 51, 204)) < 0.01, "Riot's ground has no pink stripes");

        // ---- 2. REY_GRAPH=1 + the pink stripes: the same call, now the graph
        Assert.NotNull(m.Ground.SetMacroValue(SgShip.Macro, "1"));
        Param(m.Ground, "StripeColor", "1, 0.2, 0.8, 1");
        Param(m.Ground, "StripeAmount", "0.85, 0, 0, 0");
        var pink = DrawGround(m, "2_graph_pink")!;
        Assert.True(pink.Scene.GraphSlices > 0, "no slice used the graph: " + string.Join("; ", pink.Scene.GraphNotes.Keys));
        double pinkShare = ShareNear(pink.Bgra, (255, 51, 204));
        double d1 = Diff(riot.Bgra, pink.Bgra);
        output.WriteLine($"pink stripe share {pinkShare:P1}; mean diff graph vs Riot {d1:F1}");
        Assert.True(pinkShare > 0.05, "the stripes of the graph are not on screen");
        Assert.True(d1 > 8, "the graph draws like Riot's shader");

        // ---- 2b. the editor is not kinder than the game: a custom parameter the material does not carry is ZERO (the shipped definition declares it with no value)
        m.Ground.RemoveParameter(m.Ground.Parameters.First(x => x.Name == "StripeColor"));
        var lacking = DrawGround(m, "2b_graph_param_missing")!;
        Assert.True(lacking.Scene.GraphSlices > 0);
        Assert.True(ShareNear(lacking.Bgra, (255, 51, 204)) < 0.01, "a parameter the material lacks drew the graph's default colour");
        Param(m.Ground, "StripeColor", "1, 0.2, 0.8, 1");

        // ---- 3. a custom value of the MATERIAL: the stripes turn green
        Param(m.Ground, "StripeColor", "0.1, 0.95, 0.2, 1");
        var green = DrawGround(m, "3_graph_green")!;
        double greenShare = ShareNear(green.Bgra, (26, 242, 51));
        output.WriteLine($"green stripe share {greenShare:P1}; pink share now {ShareNear(green.Bgra, (255, 51, 204)):P1}");
        Assert.True(greenShare > 0.05);
        Assert.True(ShareNear(green.Bgra, (255, 51, 204)) < pinkShare / 4);

        // ---- 4. the same scene again compiles nothing; an EDIT of the graph (the open editor's document) compiles once per signature that is drawn
        int compiles = ShaderGraphViewport.CompileCount;
        DrawGround(m, "4_graph_again");
        Assert.Equal(compiles, ShaderGraphViewport.CompileCount);
        var edited = graph.Clone();
        edited.Find("n7")!.Props["B"] = "20";                               // a finer stripe
        ShaderGraphViewport.SetLive(file, edited);
        var fine = DrawGround(m, "5_graph_edited_live")!;
        Assert.True(ShaderGraphViewport.CompileCount > compiles, "the edited graph was not compiled");
        double d2 = Diff(green.Bgra, fine.Bgra);
        output.WriteLine($"mean diff edited vs before {d2:F1}; compiles {ShaderGraphViewport.CompileCount - compiles}");
        Assert.True(d2 > 3, "editing the graph did not change the picture");
        ShaderGraphViewport.SetLive(file, null);
        int after = ShaderGraphViewport.CompileCount;
        DrawGround(m, "6_back_to_saved");
        Assert.Equal(after, ShaderGraphViewport.CompileCount);              // the saved graph's compiles are still cached (content hash)

        // ---- 5. a graph with an error: Riot's shader is drawn and the reason is published for the Material Editor
        var broken = graph.Clone();
        broken.Find("n2")!.Props["texture"] = "NotATexture";
        ShaderGraphViewport.SetLive(file, broken);
        var fallback = DrawGround(m, "7_graph_with_errors")!;
        Assert.Equal(0, fallback.Scene.GraphSlices);
        Assert.Contains(fallback.Scene.GraphNotes.Keys, k => k.Contains("has errors") && k.Contains("NotATexture"));
        Assert.True(Diff(riot.Bgra, fallback.Bgra) < 1.0, "the fallback is Riot's shader");
        var status = ShaderGraphViewport.StatusOf(file);
        Assert.NotNull(status);
        Assert.False(status!.Ok);
        Assert.Contains("Riot's shader is drawn", status.Text);
        ShaderGraphViewport.SetLive(file, null);

        // ---- 6. unassigned: Riot's shader again, identical to the first picture
        Assert.True(m.Ground.RemoveMacro(SgShip.Macro));
        var back = DrawGround(m, "8_unassigned")!;
        Assert.Equal(0, back.Scene.GraphSlices);
        double d3 = Diff(riot.Bgra, back.Bgra);
        output.WriteLine($"mean diff unassigned vs Riot {d3:F3}");
        Assert.True(d3 < 0.5, "unassigning did not bring Riot's shader back");
    }

    [Fact]
    public void The_test8_graph_made_from_the_Scrolling_template_cross_fades_with_its_custom_texture_in_the_map_viewport()
    {
        using var m = OpenMap();
        if (m is null) return;
        var vm = ShaderGraphUsabilityExportRealDataTests.BuildTest8(m.Rig.Base, Path.Combine(_root, "scratch"));
        Project(vm.Document!.Clone());
        var riot = DrawGround(m, "13_test8_riot");
        if (riot is null) return;
        m.Ground.SetMacroValue(SgShip.Macro, "1");
        Assert.NotNull(m.Ground.AddSampler(ShaderGraphUsabilityExportRealDataTests.Overlay, ShaderGraphUsabilityExportRealDataTests.MaterialImage));
        var a = DrawGround(m, "14_test8_t0", 0.0f)!;       // sin(0) = 0 -> the fade is 0.5
        var b = DrawGround(m, "15_test8_t1", 1.3f)!;       // sin(1.56) ~ 1 -> the fade is ~1: the custom texture
        var c = DrawGround(m, "16_test8_t2", 3.9f)!;       // sin(4.7) ~ -1 -> the fade is ~0: the ground
        Assert.True(a.Scene.GraphSlices > 0, string.Join("; ", a.Scene.GraphNotes.Keys));
        double dab = Diff(a.Bgra, b.Bgra), dbc = Diff(b.Bgra, c.Bgra), dcr = Diff(c.Bgra, riot.Bgra);
        output.WriteLine($"diff a-b {dab:F1}, b-c {dbc:F1}, c vs Riot {dcr:F1}, b vs Riot {Diff(b.Bgra, riot.Bgra):F1}");
        Assert.True(dbc > 20, "the fade does not move between the two textures with Time");
        Assert.True(Diff(b.Bgra, riot.Bgra) > 20, "at full fade the custom texture is not on screen");
    }

    [Fact]
    public void A_custom_texture_with_a_default_image_is_drawn_by_the_map_viewport_and_a_materials_own_entry_wins()
    {
        using var m = OpenMap();
        if (m is null) return;

        // the custom texture's default: a different texture of the same map (a recognisable one: not the ground's own)
        string? otherPath = m.Doc.Materials.Where(x => !ReferenceEquals(x, m.Ground)).SelectMany(x => x.Slots).Select(s => s.Path)
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && p.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) && p != m.Ground.Diffuse?.Path
                                 && m.Read(HashAlgorithms.WadPath(p.ToLowerInvariant())) is { Length: > 0 });
        if (otherPath is null) { output.WriteLine("SKIPPED: no second texture in Map11"); return; }
        output.WriteLine("custom texture default: " + otherPath);

        var g = ShaderGraphDocument.CreateEmpty("custom_tex", Shader);
        g.Textures.Add(new SgCustomTexture { Name = "OtherTexture", DefaultPath = otherPath });
        ShaderGraphTests.Add(g, "n2", "TextureSample", ("texture", "OtherTexture"));
        ShaderGraphTests.Link(g, "n2", "RGB", "n1", SgCatalog.BaseColorPin);
        ShaderGraphTests.Link(g, "n2", "A", "n1", SgCatalog.OpacityPin);
        Project(g);

        var riot = DrawGround(m, "9_custom_texture_riot");
        if (riot is null) return;
        m.Ground.SetMacroValue(SgShip.Macro, "1");
        // the editor is not kinder than the game: a custom texture the material does not carry is NOT bound (the graph's default image is what Assign writes into the material)
        var without = DrawGround(m, "10_custom_texture_not_on_the_material")!;
        Assert.True(without.Scene.GraphSlices > 0, string.Join("; ", without.Scene.GraphNotes.Keys));

        // Assign's entry: a clone of the DiffuseTexture entry pointing at the graph's default image
        var slot = m.Ground.AddSampler("OtherTexture", otherPath, "DiffuseTexture");
        Assert.NotNull(slot);
        var withEntry = DrawGround(m, "11_custom_texture_default_entry")!;
        double d = Diff(without.Bgra, withEntry.Bgra);
        output.WriteLine($"mean diff entry vs no entry {d:F1}; vs Riot {Diff(riot.Bgra, withEntry.Bgra):F1}");
        Assert.True(d > 8, "the material's texture entry is not on screen");
        Assert.True(Diff(riot.Bgra, withEntry.Bgra) > 8);

        // the material's entry points somewhere else (the ground's own texture): the picture follows the MATERIAL
        slot!.SetPath(m.Ground.Diffuse!.Path);
        var own = DrawGround(m, "12_custom_texture_material_path")!;
        double dOwn = Diff(withEntry.Bgra, own.Bgra);
        output.WriteLine($"mean diff material path vs default {dOwn:F1}");
        Assert.True(dOwn > 8, "the material's texture path did not replace the default image");
    }
}

/// <summary>M835: the Material Graph window's own preview (the sphere beside the graph) draws a material's assigned graph with the material's values, with or without the Shader Graph window open on it.</summary>
[Collection("ShaderGraphViewportStatics")]
public sealed class ShaderGraphMaterialPreviewRealDataTests(ITestOutputHelper output) : IDisposable
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private const string Shader = "Shaders/StaticMesh/DefaultEnv_Flat";
    private const string BaseSrx = "data/maps/mapgeometry/map11/base_srx.materials.bin";
    private const string Ground = "Worlds_Ground_A5_OrderBase_A_MAT";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rey-m835-vm-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { ShaderGraphViewport.Reset(); try { Directory.Delete(_root, recursive: true); } catch { } }

    private static double ShareNear(byte[] bgra, (int R, int G, int B) target, int tolerance = 70)
    {
        long near = 0, covered = 0;
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            if (Math.Abs(bgra[i + 2] - 10) < 4 && Math.Abs(bgra[i + 1] - 13) < 4 && Math.Abs(bgra[i] - 18) < 4) continue;
            covered++;
            if (Math.Abs(bgra[i + 2] - target.R) + Math.Abs(bgra[i + 1] - target.G) + Math.Abs(bgra[i] - target.B) < tolerance) near++;
        }
        return covered == 0 ? 0 : near / (double)covered;
    }

    [Fact]
    public async Task The_material_preview_draws_the_assigned_graph_with_the_materials_values_and_the_open_graph_window_follows_them_too()
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(Final)) { output.WriteLine("SKIPPED: no game install"); return; }
        HashDatabase db;
        try { db = new HashSyncService().LoadLocal(_ => { }); } catch { output.WriteLine("SKIPPED: no hash dictionary"); return; }
        var resolver = new WadPathResolver(db);
        string map11 = Path.Combine(Final, "Maps", "Shipping", "Map11.wad.client");
        if (!File.Exists(map11)) { output.WriteLine("SKIPPED: no Map11.wad.client"); return; }
        using var wad = WadArchive.Open(map11, resolver);
        byte[]? Read(ulong h) { try { return wad.TryGetEntry(h, out _) ? wad.Extract(h) : null; } catch { return null; } }
        var doc = MaterialDocument.Parse(wad.Extract(HashAlgorithms.WadPath(BaseSrx)), h => db.TryGetBinName(h, out var n) ? n : null, h => db.TryGetPath(h, out var p) ? p : null);
        var ground = doc.Materials.Single(m => m.Name.EndsWith(Ground, StringComparison.OrdinalIgnoreCase));

        string project = Path.Combine(_root, "project");
        Directory.CreateDirectory(ShaderGraphRegistry.FolderIn(project));
        string file = ShaderGraphRegistry.GraphPath(project, "Stripes");
        File.WriteAllText(file, ShaderGraphJson.Serialize(ShaderGraphCustomExportRealDataTests.Test5Graph()));
        Assert.Equal(1, ShaderGraphRegistry.NumberFor(project, "Stripes", Shader));
        ShaderGraphViewport.Reset();
        ShaderGraphViewport.ProjectRoot = () => project;

        var vm = new ReyEngine.App.ViewModels.ShaderPreviewViewModel(Final, resolver, Read, projectRoot: () => project);
        if (!vm.CacheAvailable) { output.WriteLine("SKIPPED: " + vm.Status); return; }
        var t = typeof(ReyEngine.App.ViewModels.ShaderPreviewViewModel);
        var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        ((Avalonia.Threading.DispatcherTimer)t.GetField("_timer", bf)!.GetValue(vm)!).Stop();
        var renderer = (ShaderPreviewRenderer)t.GetField("_renderer", bf)!.GetValue(vm)!;
        var settings = (PreviewSettings)t.GetField("_settings", bf)!.GetValue(vm)!;

        byte[] Frame(string name)
        {
            vm.SelectedMesh = "Plane";
            try { t.GetMethod("Tick", bf)!.Invoke(vm, null); } catch (System.Reflection.TargetInvocationException) { }
            var f = renderer.RenderFrame(512, 512, settings, out var err, new List<string>());
            Assert.True(f is not null, err);
            var px = f!.ToArray();
            ChromaRecolourRealDataTests.WritePng(Path.Combine(ShaderGraphUsabilityRealDataTests.Shots(), "m835_materialpreview_" + name + ".png"), px, 512, 512);
            return px;
        }
        void Apply() { t.GetMethod("ApplyMaterial", bf)!.Invoke(vm, null); Assert.True(vm.IsLoaded, vm.Status); }
        void Param(string n, string v) { var p = ground.Parameters.FirstOrDefault(x => x.Name == n) ?? ground.AddParameter(n, "Vector4")!; p.Apply(v); }

        vm.SelectedMaterial = new ReyEngine.App.ViewModels.MaterialRow { Binding = ground };
        // ---- Riot's shader first (no graph named)
        Apply();
        var riot = Frame("1_riot");
        Assert.True(ShareNear(riot, (255, 51, 204)) < 0.01);
        Assert.Equal("", vm.GraphPreviewNote);

        // ---- REY_GRAPH=1, graph NOT open: the material preview draws it, with the material's pink
        ground.SetMacroValue(SgShip.Macro, "1");
        Param("StripeColor", "1, 0.2, 0.8, 1"); Param("StripeAmount", "0.85, 0, 0, 0");
        Apply();
        output.WriteLine("note: " + vm.GraphPreviewNote);
        Assert.Contains("this material's Shader Graph 'Stripes'", vm.GraphPreviewNote);
        var pink = Frame("2_graph_pink");
        double pinkShare = ShareNear(pink, (255, 51, 204));
        output.WriteLine($"pink share {pinkShare:P1}");
        Assert.True(pinkShare > 0.05, "the material preview did not draw the stripes");

        // ---- the material's own value changes the picture
        Param("StripeColor", "0.1, 0.95, 0.2, 1");
        Apply();
        var green = Frame("3_graph_green");
        Assert.True(ShareNear(green, (26, 242, 51)) > 0.05);
        Assert.True(ShareNear(green, (255, 51, 204)) < pinkShare / 4);

        // ---- the graph window OPEN on the graph: the preview still follows the material's values (not the graph's default)
        Assert.True(vm.ShaderGraph.Open(file, out string openError) || vm.ShaderGraph.BaseResolver is null, openError);
        var build = await vm.ShaderGraph.CompileNowAsync();
        Assert.True(build is not null && build.Ok, build is null ? "no build" : string.Join(", ", build.Diagnostics));
        vm.ShaderGraph.Dispose();
        Param("StripeColor", "0.9, 0.9, 0.1, 1");
        Apply();
        var yellow = Frame("4_window_open_yellow");
        double yShare = ShareNear(yellow, (230, 230, 26));
        output.WriteLine($"window open: yellow share {yShare:P1}; {vm.GraphPreviewNote}");
        Assert.True(yShare > 0.05, "the open graph window's preview ignored the material's value");

        // ---- the editor is not kinder than the game: a parameter the material does not carry is ZERO (the shipped definition declares it with no value), and the report says so
        ground.RemoveParameter(ground.Parameters.First(x => x.Name == "StripeColor"));
        Apply();
        var zero = Frame("5_missing_param_is_zero");
        Assert.True(ShareNear(zero, (255, 51, 204)) < 0.01 && ShareNear(zero, (230, 230, 26)) < 0.01, "a parameter the material lacks drew the graph's default");
        Assert.Contains("StripeColor", vm.MaterialReport);
        Assert.Contains("not on the material: the game uses 0", vm.MaterialReport);
    }
}

[CollectionDefinition("ShaderGraphViewportStatics", DisableParallelization = true)]
public sealed class ShaderGraphViewportStaticsCollection { }
