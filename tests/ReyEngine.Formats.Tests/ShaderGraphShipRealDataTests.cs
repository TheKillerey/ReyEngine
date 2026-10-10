using System.IO.Compression;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Build;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Core.Undo;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Materials.ShaderGraph;
using ReyEngine.Formats.Shaders;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using SilkD3D11 = Silk.NET.Direct3D11.D3D11;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M833: shipping a Shader Graph against Riot's INSTALLED game (16.20 when written) - the TOC twins compared with the table the probe
/// shipped and proved in game, and the whole export (assign through the editor, Export .fantome through the real view model) read back
/// from the package, with every new blob given to a real D3D11 device. No-ops with a SKIPPED line where the game, the hash dictionary or
/// the probe's folder is absent. Set <c>REYENGINE_M833_FANTOME</c> to a path to keep the exported package there.
/// </summary>
public sealed class ShaderGraphShipRealDataTests(ITestOutputHelper output) : IDisposable
{
    private const string Game = @"C:\Riot Games\League of Legends";
    private const string Final = Game + @"\Game\DATA\FINAL";
    private const string Shader = "Shaders/StaticMesh/DefaultEnv_Flat";
    private const string TocName = "assets/shaders/generated/shaders/staticmesh/defaultenv_flat.ps-dx11";
    private const string Probe = @"D:\ProjectReyMaps\_ModUpdate\_shadergraph";
    private const string BaseSrx = "data/maps/mapgeometry/map11/base_srx.materials.bin";
    private const string Ground = "Worlds_Ground_A5_OrderBase_A_MAT";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rey-m833-real-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private sealed class Rig : IDisposable
    {
        public required HashDatabase Db { get; init; }
        public required WadPathResolver Resolver { get; init; }
        public required ShaderCacheReader Cache { get; init; }
        public required ShaderPermutationIndex Perms { get; init; }
        public required SgBase Base { get; init; }
        public required ShaderStageToc Toc { get; init; }
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
        Assert.True(b is not null, error);
        return new Rig { Db = db, Resolver = resolver, Cache = cache, Perms = perms, Base = b!, Toc = cache.ReadToc(b!.PixelTocPath)! };
    }

    /// <summary>One Riot colour blob per input signature, standing in for a graph's compiled shaders where only the TOC matters.</summary>
    private static List<byte[]> StandIns(Rig r) =>
        r.Base.Signatures.Select(s => r.Cache.LoadBlob(r.Base.PixelTocPath, s.Permutations[0].BlobIndex, out _, out _)!).ToList();

    // ================================================================================= the probe's table

    [Fact]
    public void TheTocTwinsEqualTheTableTheProbeShippedAndTheGameAccepted()
    {
        using var r = Open();
        string probeToc = Path.Combine(Probe, "build_test2", "ShaderCache.dx11", "assets", "shaders", "generated", "shaders", "staticmesh", "defaultenv_flat.ps-dx11");
        string probeContainer = probeToc + "_200";
        string inputs = Path.Combine(Probe, "evidence", "test2", "riot_inputs.json");
        if (r is null) return;
        if (!File.Exists(probeToc) || !File.Exists(inputs)) { output.WriteLine("SKIPPED: the probe's build folder is not here"); return; }

        byte[] tocBytes = r.Cache.ReadEntry(TocName, out _)!;
        uint last = (r.Toc.DeclaredBlobCount - 1) / 100 * 100;
        byte[] container = r.Cache.ReadContainer(r.Toc.Path, last, out string? cpath, out _)!;
        using var j = JsonDocument.Parse(File.ReadAllText(inputs));
        string wantToc = j.RootElement.GetProperty("riot_toc_sha1").GetString()!, wantContainer = j.RootElement.GetProperty("riot_container_sha1").GetString()!;
        output.WriteLine($"installed TOC sha1 {SgShip.Sha1Hex(tocBytes)} (probe was built from {wantToc}); container {cpath} sha1 {SgShip.Sha1Hex(container)} (probe {wantContainer})");
        if (SgShip.Sha1Hex(tocBytes) != wantToc || SgShip.Sha1Hex(container) != wantContainer)
        { output.WriteLine("SKIPPED: the installed cache is not the one the probe was built from (a patch re-cooked it)"); return; }

        var patch = SgShipBuilder.Build(r.Base, r.Toc, tocBytes, container, cpath!, new[] { new SgGraphBlobs(1, "probe", StandIns(r)) });

        // THE TABLE: byte-identical. It holds keys and blob indices only, so it does not depend on which shader the blobs are.
        byte[] want = File.ReadAllBytes(probeToc);
        Assert.Equal(want.Length, patch.Toc.Length);
        Assert.True(patch.Toc.AsSpan().SequenceEqual(want), "the generated TOC differs from the probe's");
        Assert.Equal(1280, patch.TwinKeys);
        Assert.Equal(640, patch.ColourTwins);
        Assert.Equal(640, patch.ShadowTwins);
        Assert.Equal(16, patch.NewBlobs);
        Assert.Equal(238u, patch.FirstNewBlob);
        Assert.Equal("assets/shaders/generated/shaders/staticmesh/defaultenv_flat.ps-dx11_200", patch.ContainerPath);

        // THE CONTAINER: Riot's 38 records verbatim, then 16 framed records with trailer 0x35 (the probe's blobs differ in content,
        // being compiled by another front end; the framing is the same)
        byte[] wantC = File.ReadAllBytes(probeContainer);
        Assert.True(patch.Container.AsSpan(0, container.Length).SequenceEqual(container));
        Assert.True(wantC.AsSpan(0, container.Length).SequenceEqual(container));
        Assert.Equal(54, SgShipBuilder.ReadRecords(patch.Container, "generated"));
        Assert.Equal(54, SgShipBuilder.ReadRecords(wantC, "probe"));
        int off = container.Length;
        for (int i = 0; i < 16; i++)
        {
            int len = BitConverter.ToInt32(patch.Container, off);
            Assert.Equal(0x35, patch.Container[off + 4 + len - 1]);
            off += 4 + len;
        }
        Assert.Equal(patch.Container.Length, off);
        output.WriteLine("TOC byte-identical to build_test2; container prefix equal, 16 appended records framed length+1 / 0x35.");
    }

    // ================================================================================= end to end

    internal static ShaderGraphDocument Test3Graph()
    {
        // texture x TintColor, with green stripes: 8 stripes across UV0.u, half of each period lerped to green
        var d = ShaderGraphDocument.CreateEmpty("ShaderGraphEditor_test3", Shader);
        ShaderGraphTests.Add(d, "n2", "TextureSample", ("texture", "DiffuseTexture"));
        ShaderGraphTests.Add(d, "n3", "Parameter", ("name", "TintColor"));
        ShaderGraphTests.Add(d, "n4", "Multiply");
        ShaderGraphTests.Add(d, "n5", "ComponentMask", ("mask", "rgb"));
        ShaderGraphTests.Add(d, "n6", "TexCoord", ("set", "1"));
        ShaderGraphTests.Add(d, "n7", "ComponentMask", ("mask", "r"));
        ShaderGraphTests.Add(d, "n8", "Multiply", ("B", "8"));
        ShaderGraphTests.Add(d, "n9", "Frac");
        ShaderGraphTests.Add(d, "n10", "Step", ("Edge", "0.5"));
        ShaderGraphTests.Add(d, "n11", "Constant", ("value", "0 1 0"));
        ShaderGraphTests.Add(d, "n12", "Lerp");
        ShaderGraphTests.Link(d, "n2", "RGBA", "n4", "A"); ShaderGraphTests.Link(d, "n3", "Value", "n4", "B");
        ShaderGraphTests.Link(d, "n4", "Value", "n5", "Value");
        ShaderGraphTests.Link(d, "n6", "UV", "n7", "Value");
        ShaderGraphTests.Link(d, "n7", "Value", "n8", "A");
        ShaderGraphTests.Link(d, "n8", "Value", "n9", "Value");
        ShaderGraphTests.Link(d, "n9", "Value", "n10", "X");
        ShaderGraphTests.Link(d, "n5", "Value", "n12", "A"); ShaderGraphTests.Link(d, "n11", "Value", "n12", "B"); ShaderGraphTests.Link(d, "n10", "Value", "n12", "Alpha");
        ShaderGraphTests.Link(d, "n12", "Value", "n1", SgCatalog.BaseColorPin);
        ShaderGraphTests.Link(d, "n2", "A", "n1", SgCatalog.OpacityPin);
        return d;
    }

    private static unsafe int[] CreatePixelShaders(IReadOnlyList<byte[]> blobs)
    {
        // a throwaway process-lifetime device; the Silk api is deliberately NOT disposed (see ShaderPreviewRenderer.PinD3D11Library, M809)
        var d3d = SilkD3D11.GetApi(null);
        var levels = stackalloc D3DFeatureLevel[2] { D3DFeatureLevel.Level111, D3DFeatureLevel.Level110 };
        D3DFeatureLevel got = default;
        ComPtr<ID3D11Device> dev = default;
        ComPtr<ID3D11DeviceContext> ctx = default;
        int hr0 = d3d.CreateDevice(default(ComPtr<IDXGIAdapter>), D3DDriverType.Hardware, 0, (uint)CreateDeviceFlag.None, levels, 2u, SilkD3D11.SdkVersion, ref dev, ref got, ref ctx);
        if (hr0 < 0) return Array.Empty<int>();
        var result = new int[blobs.Count];
        for (int i = 0; i < blobs.Count; i++)
        {
            ComPtr<ID3D11PixelShader> ps = default;
            fixed (byte* p = blobs[i])
                result[i] = dev.CreatePixelShader(p, (nuint)blobs[i].Length, ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref ps);
            if (result[i] >= 0) ps.Release();
        }
        ctx.Release(); dev.Release();
        return result;
    }

    private sealed class NoProgress : IProgress<(double Frac, string Stage)> { public void Report((double Frac, string Stage) value) { } }

    private static byte[] ReadFromZip(ZipArchive zip, string entry)
    {
        using var s = zip.GetEntry(entry)!.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>With the declaration setting ON the bin that names a graph must still ship whole (a declaration of the macro edit is not relied on).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnAssignedGraphIsExportedInThePackageAndEveryNewBlobCreatesOnADevice(bool declare)
    {
        using var r = Open();
        if (r is null) return;
        string map11 = Path.Combine(Final, "Maps", "Shipping", "Map11.wad.client");
        if (!File.Exists(map11)) { output.WriteLine("SKIPPED: no Map11.wad.client"); return; }
        byte[] riotBin;
        using (var w = WadArchive.Open(map11, r.Resolver))
        {
            ulong h = HashAlgorithms.WadPath(BaseSrx);
            if (!w.TryGetEntry(h, out _)) { output.WriteLine("SKIPPED: Map11 has no base_srx.materials.bin"); return; }
            riotBin = w.Extract(h);
        }

        // ---- a SCRATCH project: Riot's base_srx bin, the test3 graph, the assignment made through the editor
        string project = Path.Combine(_root, "project");
        Directory.CreateDirectory(project);
        var doc = MaterialDocument.Parse(riotBin, h => r.Db.TryGetBinName(h, out var n) ? n : null);
        var ground = doc.Materials.Single(m => m.Name.EndsWith(Ground, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Shader, ground.RenderShader, ignoreCase: true);
        output.WriteLine($"material {ground.Name}: macros {string.Join(" ", ground.Macros.Select(kv => kv.Key + "=" + kv.Value))}, params {string.Join(",", ground.Parameters.Select(p => p.Name))}");

        Directory.CreateDirectory(ShaderGraphRegistry.FolderIn(project));
        File.WriteAllText(ShaderGraphRegistry.GraphPath(project, "ShaderGraphEditor_test3"), ShaderGraphJson.Serialize(Test3Graph()));

        var undo = new UndoRedoService();
        var editor = new MaterialEditorViewModel { UndoService = undo, ProjectRoot = () => project };
        editor.Load(doc, new WadAssetEntry { Path = BaseSrx });
        byte[] untouched = editor.Serialize()!;
        output.WriteLine($"base_srx.materials.bin: Riot {riotBin.Length:n0} B, re-serialised unedited {untouched.Length:n0} B, identical bytes: {untouched.AsSpan().SequenceEqual(riotBin)}, differing bytes: {Enumerable.Range(0, Math.Min(untouched.Length, riotBin.Length)).Count(i => untouched[i] != riotBin[i])}, first at {Enumerable.Range(0, Math.Min(untouched.Length, riotBin.Length)).FirstOrDefault(i => untouched[i] != riotBin[i])}");
        var binding = editor.Materials.Single(m => m.Name == ground.Name);
        binding.RefreshMacroStatus();
        binding.Model.SetVectorParameter("TintColor", new Vector4(0.6f, 0.8f, 1f, 1f));        // a blue-ish tint so the ground differs from Riot's grey
        binding.AssignGraphCommand.Execute(Assert.Single(binding.GraphChoices));
        Assert.True(binding.HasGraphAssigned, binding.GraphAssignmentText);
        byte[] edited = editor.Serialize()!;
        Directory.CreateDirectory(Path.Combine(project, "Map11", "data", "maps", "mapgeometry", "map11"));
        File.WriteAllBytes(Path.Combine(project, "Map11", BaseSrx.Replace('/', Path.DirectorySeparatorChar)), edited);

        // ---- Export .fantome through the real view model
        var p = new ReyProject
        {
            Name = "ScratchGraph", RootPath = project, GameDirectory = Game, OutputDirectory = Path.Combine(_root, "build"),
            ShipBinEditsAsDeclarations = declare, ProjectFolders = { "Map11" },
        };
        if (declare) p.ReferenceWads.Add(map11);   // the installed Map11 stands for the game's copy, so the bin CAN be compared and declared
        var vm = new MainWindowViewModel { Project = p };
        string output3 = !declare && System.Environment.GetEnvironmentVariable("REYENGINE_M833_FANTOME") is { Length: > 0 } keep ? keep : Path.Combine(_root, "out.fantome");
        Directory.CreateDirectory(Path.GetDirectoryName(output3)!);
        Directory.CreateDirectory(p.OutputDirectory!);
        var method = typeof(MainWindowViewModel).GetMethod("ExportFantomeCore", BindingFlags.NonPublic | BindingFlags.Instance)!;
        try
        {
            method.Invoke(vm, new object?[] { output3, new FantomeMeta { Name = "ShaderGraphEditor test3", Author = "ReyEngine", Version = "16.20-test3", Description = "ReyEngine Shader Graph export test: texture x TintColor with green stripes on the blue base ground." },
                null, p.OutputDirectory, new NoProgress(), "Export", "No WAD was produced.", "Zipping..." });
        }
        catch (TargetInvocationException ex) { throw new Xunit.Sdk.XunitException("export failed: " + ex.InnerException); }

        // ---- read the package back
        using var zip = ZipFile.OpenRead(output3);
        var names = zip.Entries.Select(e => e.FullName).ToArray();
        output.WriteLine("package: " + string.Join(", ", names));
        Assert.Contains("WAD/Map11.wad.client", names);
        Assert.Contains("WAD/ShaderCache.dx11.wad.client", names);
        Assert.DoesNotContain(names, n => n.Contains(".reyengine") || n.Contains("shadergraph"));

        string tmp = Path.Combine(_root, "read"); Directory.CreateDirectory(tmp);
        File.WriteAllBytes(Path.Combine(tmp, "Map11.wad.client"), ReadFromZip(zip, "WAD/Map11.wad.client"));
        File.WriteAllBytes(Path.Combine(tmp, "ShaderCache.dx11.wad.client"), ReadFromZip(zip, "WAD/ShaderCache.dx11.wad.client"));

        using (var mw = WadArchive.Open(Path.Combine(tmp, "Map11.wad.client")))
        {
            Assert.Equal(1, mw.Entries.Count);
            var shipped = MaterialDocument.Parse(mw.Extract(HashAlgorithms.WadPath(BaseSrx)), h => r.Db.TryGetBinName(h, out var n) ? n : null);
            var sm = shipped.Materials.Single(m => m.Name == ground.Name);
            Assert.Equal("1", sm.Macros["REY_GRAPH"]);
            Assert.Equal(new Vector4(0.6f, 0.8f, 1f, 1f), sm.Parameters.Single(x => x.Name == "TintColor") is { } tp && tp.TryGetVector4(out var tv) ? tv : default);
            // every other material of the bin is as Riot ships it
            foreach (var m in shipped.Materials.Where(m => m.Name != ground.Name))
                Assert.Equal(doc.Materials.Single(o => o.Name == m.Name).Macros.OrderBy(k => k.Key).Select(k => k.Key + k.Value), m.Macros.OrderBy(k => k.Key).Select(k => k.Key + k.Value));
        }

        using var sw = WadArchive.Open(Path.Combine(tmp, "ShaderCache.dx11.wad.client"));
        Assert.Equal(2, sw.Entries.Count);
        byte[] newToc = sw.Extract(HashAlgorithms.WadPath(TocName));
        byte[] newContainer = sw.Extract(HashAlgorithms.WadPath(TocName + "_200"));
        Assert.Null(sw.Entries.FirstOrDefault(e => e.PathHash == HashAlgorithms.WadPath(TocName + "_0")));   // the other containers are not shipped
        var back = ShaderCacheReader.ParseToc(newToc, TocName)!;
        Assert.Contains(("REY_GRAPH", "1"), back.DefinePool);

        // the TOC differs from Riot's only by the pool pair and the twins
        var riot = r.Toc;
        Assert.Equal(riot.DefinePool.Count + 1, back.DefinePool.Count);
        Assert.Equal(riot.Permutations.Select(x => (x.Key, x.BlobIndex)), back.Permutations.Take(riot.Permutations.Count).Select(x => (x.Key, x.BlobIndex)));
        Assert.Equal(riot.DeclaredBlobCount + 16u, back.DeclaredBlobCount);

        // every unique Riot key has a twin; the colour ones land in the 16 new records, the shadow ones keep Riot's blob
        var described = ShaderCacheReader.DescribePermutations(riot, out _);
        var byKey = back.Permutations.GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First().BlobIndex);
        foreach (var pm in described.GroupBy(x => x.Key).Select(g => g.First()))
        {
            ulong twin = ShaderCacheReader.PermutationKey(pm.Defines!.Append("REY_GRAPH=1"));
            Assert.True(byKey.TryGetValue(twin, out uint blob), "no twin for " + pm.DefineSummary);
            if (pm.Defines!.Contains("GENERATE_SHADOW_MAP=1")) Assert.Equal(pm.BlobIndex, blob);
            else Assert.InRange(blob, riot.DeclaredBlobCount, riot.DeclaredBlobCount + 15);
        }

        // the 16 new records: framed, DXBC, and each one creates on a real D3D11 device
        Assert.Equal(54, SgShipBuilder.ReadRecords(newContainer, "shipped container"));
        var blobs = new List<byte[]>();
        int off = 0, idx = 0;
        while (off < newContainer.Length)
        {
            int len = BitConverter.ToInt32(newContainer, off);
            if (idx >= 38) { Assert.Equal(0x35, newContainer[off + 4 + len - 1]); blobs.Add(newContainer.AsSpan(off + 4, len - 1).ToArray()); }
            off += 4 + len; idx++;
        }
        Assert.Equal(16, blobs.Count);
        var hrs = CreatePixelShaders(blobs);
        if (hrs.Length == 0) output.WriteLine("SKIPPED: no hardware D3D11 device for CreatePixelShader");
        else
        {
            output.WriteLine("CreatePixelShader hr: " + string.Join(" ", hrs.Select(h => "0x" + h.ToString("x8"))));
            Assert.All(hrs, h => Assert.True(h >= 0, "CreatePixelShader failed 0x" + h.ToString("x8")));
        }
        var bad = (byte[])blobs[0].Clone(); bad[4] ^= 0xFF;
        var neg = CreatePixelShaders(new[] { bad });
        if (neg.Length > 0) Assert.True(neg[0] < 0, "the negative control (a flipped checksum byte) must be refused");
        output.WriteLine($"exported {output3} ({new FileInfo(output3).Length:n0} bytes)");
    }

    // ================================================================================= the block

    [Fact]
    public void AnExportWithAnUndeclaredParameterOnAnAssignedMaterialIsBlockedAndNothingIsWritten()
    {
        using var r = Open();
        if (r is null) return;
        string map11 = Path.Combine(Final, "Maps", "Shipping", "Map11.wad.client");
        if (!File.Exists(map11)) { output.WriteLine("SKIPPED: no Map11.wad.client"); return; }
        byte[] riotBin;
        using (var w = WadArchive.Open(map11, r.Resolver)) riotBin = w.Extract(HashAlgorithms.WadPath(BaseSrx));

        string project = Path.Combine(_root, "project");
        Directory.CreateDirectory(ShaderGraphRegistry.FolderIn(project));
        File.WriteAllText(ShaderGraphRegistry.GraphPath(project, "g"), ShaderGraphJson.Serialize(Test3Graph()));
        var doc = MaterialDocument.Parse(riotBin, h => r.Db.TryGetBinName(h, out var n) ? n : null);
        var editor = new MaterialEditorViewModel { UndoService = new UndoRedoService(), ProjectRoot = () => project };
        editor.Load(doc, new WadAssetEntry { Path = BaseSrx });
        var binding = editor.Materials.Single(m => m.Name.EndsWith(Ground, StringComparison.OrdinalIgnoreCase));
        binding.AssignGraphCommand.Execute(Assert.Single(binding.GraphChoices));
        // the probe's test1 mistake: a parameter the shader does not declare
        binding.Model.SetVectorParameter("ReyProbe", new Vector4(0, 0, 1, 0));
        Directory.CreateDirectory(Path.Combine(project, "Map11", "data", "maps", "mapgeometry", "map11"));
        File.WriteAllBytes(Path.Combine(project, "Map11", BaseSrx.Replace('/', Path.DirectorySeparatorChar)), editor.Serialize()!);

        var ex = Assert.Throws<InvalidOperationException>(() => ReyEngine.App.Services.ShaderGraphShipService.Prepare(
            project, Final, new[] { Path.Combine(project, "Map11") }, h => r.Db.TryGetBinName(h, out var n) ? n : null, r.Resolver));
        output.WriteLine(ex.Message);
        Assert.Contains("ReyProbe", ex.Message);
        Assert.Contains(Ground, ex.Message);
        Assert.Contains("Missing shader constant", ex.Message);
    }

    [Fact]
    public void AProjectThatAlreadyShipsTheSameTableRefusesTheGraph()
    {
        using var r = Open();
        if (r is null) return;
        string map11 = Path.Combine(Final, "Maps", "Shipping", "Map11.wad.client");
        if (!File.Exists(map11)) { output.WriteLine("SKIPPED: no Map11.wad.client"); return; }
        byte[] riotBin;
        using (var w = WadArchive.Open(map11, r.Resolver)) riotBin = w.Extract(HashAlgorithms.WadPath(BaseSrx));

        string project = Path.Combine(_root, "project");
        Directory.CreateDirectory(ShaderGraphRegistry.FolderIn(project));
        File.WriteAllText(ShaderGraphRegistry.GraphPath(project, "g"), ShaderGraphJson.Serialize(Test3Graph()));
        var doc = MaterialDocument.Parse(riotBin, h => r.Db.TryGetBinName(h, out var n) ? n : null);
        var editor = new MaterialEditorViewModel { UndoService = new UndoRedoService(), ProjectRoot = () => project };
        editor.Load(doc, new WadAssetEntry { Path = BaseSrx });
        editor.Materials.Single(m => m.Name.EndsWith(Ground, StringComparison.OrdinalIgnoreCase)).AssignGraphCommand.Execute(
            editor.Materials.Single(m => m.Name.EndsWith(Ground, StringComparison.OrdinalIgnoreCase)).GraphChoices.Single());
        string map = Path.Combine(project, "Map11");
        Directory.CreateDirectory(Path.Combine(map, "data", "maps", "mapgeometry", "map11"));
        File.WriteAllBytes(Path.Combine(map, BaseSrx.Replace('/', Path.DirectorySeparatorChar)), editor.Serialize()!);

        // an RTX-style folder of the project that ships Riot's own table under another spelling
        string rtx = Path.Combine(project, "ShaderCache.dx11");
        string rel = TocName.Replace("-dx11", ".dx11").Replace('/', Path.DirectorySeparatorChar);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(rtx, rel))!);
        File.WriteAllBytes(Path.Combine(rtx, rel), new byte[] { 1 });

        var ex = Assert.Throws<InvalidOperationException>(() => ReyEngine.App.Services.ShaderGraphShipService.Prepare(
            project, Final, new[] { map, rtx }, h => r.Db.TryGetBinName(h, out var n) ? n : null, r.Resolver));
        output.WriteLine(ex.Message);
        Assert.Contains("already ships", ex.Message);
        Assert.Contains("merging", ex.Message);
    }

    [Fact]
    public void AnUnreadableInstalledCacheRefusesInsteadOfShippingStaleData()
    {
        using var r = Open();
        if (r is null) return;
        string project = Path.Combine(_root, "project");
        string map = Path.Combine(project, "Map11");
        Directory.CreateDirectory(map);
        // a bin that names a graph is enough: the cache is checked before anything else is built
        string map11 = Path.Combine(Final, "Maps", "Shipping", "Map11.wad.client");
        if (!File.Exists(map11)) return;
        byte[] riotBin;
        using (var w = WadArchive.Open(map11, r.Resolver)) riotBin = w.Extract(HashAlgorithms.WadPath(BaseSrx));
        var doc = MaterialDocument.Parse(riotBin, h => r.Db.TryGetBinName(h, out var n) ? n : null);
        var editor = new MaterialEditorViewModel { UndoService = new UndoRedoService(), ProjectRoot = () => project };
        Directory.CreateDirectory(ShaderGraphRegistry.FolderIn(project));
        File.WriteAllText(ShaderGraphRegistry.GraphPath(project, "g"), ShaderGraphJson.Serialize(Test3Graph()));
        editor.Load(doc, new WadAssetEntry { Path = BaseSrx });
        var b = editor.Materials.Single(m => m.Name.EndsWith(Ground, StringComparison.OrdinalIgnoreCase));
        b.AssignGraphCommand.Execute(b.GraphChoices.Single());
        Directory.CreateDirectory(Path.Combine(map, "data", "maps", "mapgeometry", "map11"));
        File.WriteAllBytes(Path.Combine(map, BaseSrx.Replace('/', Path.DirectorySeparatorChar)), editor.Serialize()!);

        var ex = Assert.Throws<InvalidOperationException>(() => ReyEngine.App.Services.ShaderGraphShipService.Prepare(
            project, Path.Combine(_root, "no-such-game"), new[] { map }, h => r.Db.TryGetBinName(h, out var n) ? n : null, r.Resolver));
        Assert.Contains("stale", ex.Message);
    }
}
