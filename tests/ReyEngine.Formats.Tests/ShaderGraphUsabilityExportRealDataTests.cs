using System.IO.Compression;
using System.Reflection;
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
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M835: custom TEXTURES ship (test7 worked in game). A graph made through the editor's own view model from the Scrolling template - a ground texture that scrolls and is
/// cross-faded with a custom texture parameter by Time and a Sine - is assigned to the blue-base ground material, the texture is added to the material through the Material Editor, and
/// the project is exported with the real view model. The package is read back: the shipped shaders.bin declares the texture like Riot's DiffuseTexture, the material carries the
/// samplerValues entry, the twinned shader cache holds 16 compiled blobs that name the texture. Set <c>REYENGINE_M835_FANTOME</c> to keep the package (this is how
/// ShaderGraphEditor_16.20-test8.fantome is made). No-ops with a SKIPPED line where the game or the hash dictionary is absent.
/// </summary>
[Collection("ShaderGraphViewportStatics")]
public sealed class ShaderGraphUsabilityExportRealDataTests(ITestOutputHelper output) : IDisposable
{
    private const string Game = @"C:\Riot Games\League of Legends";
    private const string Final = Game + @"\Game\DATA\FINAL";
    private const string Shader = "Shaders/StaticMesh/DefaultEnv_Flat";
    private const string TocName = "assets/shaders/generated/shaders/staticmesh/defaultenv_flat.ps-dx11";
    private const string BaseSrx = "data/maps/mapgeometry/map11/base_srx.materials.bin";
    private const string Ground = "Worlds_Ground_A5_OrderBase_A_MAT";
    private const string GraphName = "ShaderGraphEditor_test8";
    internal const string Overlay = "GalaxyTexture";
    private const string DefaultImage = "assets/shared/materials/marble_med_1024.tex";
    internal const string MaterialImage = "assets/shared/materials/galaxy.tex";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rey-m835-export-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>The test8 graph, made by the editor's gestures from the Scrolling template.</summary>
    internal static ShaderGraphViewModel BuildTest8(SgBase b, string root)
    {
        var vm = new ShaderGraphViewModel(() => root) { BaseResolver = _ => (b, ""), CompileDelay = TimeSpan.Zero };
        Assert.True(vm.NewFromTemplate(Shader, SgTemplates.Scroll, out string error, GraphName), error);
        var d = vm.Document!;
        string panner = d.Nodes.Single(n => n.Type == "Panner").Id, ground = d.Nodes.Single(n => n.Type == "TextureSample").Id;

        SgPaletteEntry E(string title) => vm.Palette.First(p => p.Title == title);
        string tex = vm.AddNode(E("Texture Parameter (new, custom)"), 1000, 420)!;
        Assert.True(vm.RenameTexture("CustomTexture", Overlay));
        Assert.True(vm.SetTextureDefault(Overlay, DefaultImage));
        string tiling = vm.AddNode(E("Tiling / Offset"), 760, 420)!;
        Assert.True(vm.SetProp(tiling, "Tiling", "3"));
        Assert.True(vm.Connect(panner, "UV", tiling, "UV"));
        Assert.True(vm.Connect(tiling, "UV", tex, "UV"));

        string time = vm.AddNode(E("Time"), 0, 640)!;
        string speed = vm.AddNode(E("Multiply"), 250, 640)!; Assert.True(vm.SetProp(speed, "B", "1.2"));
        string sine = vm.AddNode(E("Sine"), 500, 640)!;
        string half = vm.AddNode(E("Multiply"), 750, 640)!; Assert.True(vm.SetProp(half, "B", "0.5"));
        string up = vm.AddNode(E("Add"), 1000, 640)!; Assert.True(vm.SetProp(up, "B", "0.5"));
        string fade = vm.AddNode(E("Lerp"), 1500, 200)!;
        Assert.True(vm.Connect(time, "Seconds", speed, "A")); Assert.True(vm.Connect(speed, "Value", sine, "Value"));
        Assert.True(vm.Connect(sine, "Value", half, "A")); Assert.True(vm.Connect(half, "Value", up, "A"));
        Assert.True(vm.Connect(ground, "RGB", fade, "A")); Assert.True(vm.Connect(tex, "RGB", fade, "B")); Assert.True(vm.Connect(up, "Value", fade, "Alpha"));
        Assert.True(vm.Connect(fade, "Value", "n1", SgCatalog.BaseColorPin));
        Assert.False(vm.Analysis!.HasErrors, string.Join("\n", vm.Analysis.Errors));
        return vm;
    }

    [Fact]
    public async Task A_graph_with_a_custom_texture_exports_with_the_texture_declared_the_material_entry_and_16_blobs_that_name_it()
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(Final)) { output.WriteLine("SKIPPED: no game install"); return; }
        HashDatabase db;
        try { db = new HashSyncService().LoadLocal(_ => { }); } catch { output.WriteLine("SKIPPED: no hash dictionary"); return; }
        var resolver = new WadPathResolver(db);
        string map11 = Path.Combine(Final, "Maps", "Shipping", "Map11.wad.client");
        if (!File.Exists(map11)) { output.WriteLine("SKIPPED: no Map11.wad.client"); return; }
        using var cache = ShaderCacheReader.Open(Final, resolver, out _);
        if (cache is null) { output.WriteLine("SKIPPED: the shader cache did not open"); return; }
        var perms = new ShaderPermutationIndex(Final, h => resolver.TryGetPath(h, out var p) ? p : null);
        var b = ShaderGraphBaseResolver.Resolve(cache, perms, Shader, out string baseError, out _);
        if (b is null) { output.WriteLine("SKIPPED: " + baseError); return; }

        byte[] riotBin;
        using (var w = WadArchive.Open(map11, resolver))
        {
            Assert.True(w.TryGetEntry(HashAlgorithms.WadPath(MaterialImage), out _), "galaxy.tex is not in Map11.wad");
            Assert.True(w.TryGetEntry(HashAlgorithms.WadPath(DefaultImage), out _), "marble_med_1024.tex is not in Map11.wad");
            riotBin = w.Extract(HashAlgorithms.WadPath(BaseSrx));
        }
        Func<uint, string?> binName = h => db.TryGetBinName(h, out var n) ? n : null;

        // ---- the graph, through the editor's gestures; saved into the SCRATCH project
        string project = Path.Combine(_root, "project");
        Directory.CreateDirectory(project);
        var sg = BuildTest8(b, project);
        var build = await sg.CompileNowAsync();
        Assert.True(build!.Ok, string.Join("\n", build.Diagnostics));
        Assert.True(await sg.SaveAsync(), sg.Status);
        output.WriteLine($"graph {Path.GetFileName(sg.FilePath)}: {sg.Document!.Nodes.Count} nodes, textures [{string.Join(", ", sg.Document.Textures.Select(t => t.Name + "=" + t.DefaultPath))}]");

        // ---- the material, through the Material Editor: assign, add the texture, point it at galaxy.tex
        var doc = MaterialDocument.Parse(riotBin, binName);
        var ground = doc.Materials.Single(m => m.Name.EndsWith(Ground, StringComparison.OrdinalIgnoreCase));
        var undo = new UndoRedoService();
        var editor = new MaterialEditorViewModel { UndoService = undo, ProjectRoot = () => project };
        editor.Load(doc, new WadAssetEntry { Path = BaseSrx });
        var binding = editor.Materials.Single(m => m.Name == ground.Name);
        binding.RefreshMacroStatus();
        binding.AssignGraphCommand.Execute(Assert.Single(binding.GraphChoices));
        Assert.True(binding.HasGraphAssigned, binding.GraphAssignmentText);
        Assert.False(binding.GraphAssignmentIsError, binding.GraphAssignmentText);
        Assert.Equal(Overlay, Assert.Single(binding.GraphTextures).Name);
        Assert.True(binding.GraphTextures[0].InMaterial, "Assign adds the graph's textures to the material");   // M835: the entry is a clone of DiffuseTexture's, with the graph's default image
        var slot = binding.Slots.Single(s => s.SamplerName == Overlay);
        slot.EditedPath = MaterialImage;
        slot.ApplyCommand.Execute(null);
        byte[] edited = editor.Serialize()!;
        Directory.CreateDirectory(Path.Combine(project, "Map11", "data", "maps", "mapgeometry", "map11"));
        File.WriteAllBytes(Path.Combine(project, "Map11", BaseSrx.Replace('/', Path.DirectorySeparatorChar)), edited);
        byte[] riotShaders;
        using (var g = WadArchive.Open(Path.Combine(Final, SgShadersBin.WadName))) riotShaders = g.Extract(HashAlgorithms.WadPath(SgShadersBin.GamePath));

        // ---- Export .fantome through the real view model
        var p = new ReyProject { Name = "ScratchCustomTexture", RootPath = project, GameDirectory = Game, OutputDirectory = Path.Combine(_root, "build"), ProjectFolders = { "Map11" } };
        var vm = new MainWindowViewModel { Project = p };
        string keep = System.Environment.GetEnvironmentVariable("REYENGINE_M835_FANTOME") ?? "";
        string packagePath = keep.Length > 0 ? keep : Path.Combine(_root, "out.fantome");
        if (keep.Length > 0 && File.Exists(keep)) throw new Xunit.Sdk.XunitException("refusing to overwrite " + keep);
        Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
        Directory.CreateDirectory(p.OutputDirectory!);
        var method = typeof(MainWindowViewModel).GetMethod("ExportFantomeCore", BindingFlags.NonPublic | BindingFlags.Instance)!;
        try
        {
            method.Invoke(vm, new object?[] { packagePath, new FantomeMeta
                {
                    Name = "ShaderGraphEditor test8", Author = "ReyEngine", Version = "16.20-test8",
                    Description = "ReyEngine Shader Graph export test: a custom texture parameter. Made from the Scrolling template: the blue-base ground texture scrolls and is cross-faded with a custom texture (GalaxyTexture, the material points it at assets/shared/materials/galaxy.tex) by Time and a Sine. Ships a patched shaders.bin that declares the texture.",
                },
                null, p.OutputDirectory, new ShaderGraphShipRealDataTests.NoProgress(), "Export", "No WAD was produced.", "Zipping..." });
        }
        catch (TargetInvocationException ex) { throw new Xunit.Sdk.XunitException("export failed: " + ex.InnerException); }

        // ---- read the package back
        using var zip = ZipFile.OpenRead(packagePath);
        var names = zip.Entries.Select(e => e.FullName).ToArray();
        output.WriteLine("package: " + string.Join(", ", names));
        Assert.Contains("WAD/Global.wad.client", names);
        Assert.Contains("WAD/Map11.wad.client", names);
        Assert.Contains("WAD/ShaderCache.dx11.wad.client", names);
        Assert.DoesNotContain(names, n => n.Contains(".reyengine") || n.Contains("shadergraph"));
        string tmp = Path.Combine(_root, "read"); Directory.CreateDirectory(tmp);
        foreach (string wad in new[] { "Global", "Map11", "ShaderCache.dx11" })
            File.WriteAllBytes(Path.Combine(tmp, wad + ".wad.client"), ShaderGraphShipRealDataTests.ReadFromZip(zip, $"WAD/{wad}.wad.client"));

        // THE shaders.bin: Riot's file, DefaultEnv_Flat's textures extended with a copy of the DiffuseTexture entry, nothing else changed
        using (var gw = WadArchive.Open(Path.Combine(tmp, "Global.wad.client")))
        {
            var entry = Assert.Single(gw.Entries);
            Assert.Equal(HashAlgorithms.WadPath(SgShadersBin.GamePath), entry.PathHash);
            byte[] shipped = gw.Extract(entry.PathHash);
            var add = new[] { new SgDeclaredTexture(Overlay, DefaultImage) };
            Assert.Empty(SgShadersBin.Verify(riotShaders, shipped, Shader, Array.Empty<SgDeclaredParam>(), add));
            Assert.Equal(new[] { "DiffuseTexture", Overlay }, SgShadersBin.DeclaredTextures(shipped, Shader));
            Assert.Equal(SgShadersBin.Declared(riotShaders, Shader), SgShadersBin.Declared(shipped, Shader));
            var entries = SgShadersBin.TextureEntryBytes(shipped, Shader);
            Assert.Equal(entries["DiffuseTexture"].Length - "DiffuseTexture".Length, entries[Overlay].Length - Overlay.Length);   // the same two-field shape, name apart
            Assert.Equal(BitConverter.ToUInt32(entries["DiffuseTexture"], 0), BitConverter.ToUInt32(entries[Overlay], 0));           // the same class
            Assert.Equal(HashAlgorithms.WadPath(DefaultImage), BitConverter.ToUInt64(entries[Overlay], entries[Overlay].Length - 8)); // defaultTexturePath = the graph's default image
            output.WriteLine($"shipped shaders.bin {shipped.Length:n0} B (installed {riotShaders.Length:n0}); {Shader} textures: {string.Join(", ", SgShadersBin.DeclaredTextures(shipped, Shader))}");
        }

        // THE MATERIAL: REY_GRAPH, the samplerValues entry, every other material as Riot ships it
        using (var mw = WadArchive.Open(Path.Combine(tmp, "Map11.wad.client")))
        {
            var shipped = MaterialDocument.Parse(mw.Extract(HashAlgorithms.WadPath(BaseSrx)), binName);
            var sm = shipped.Materials.Single(m => m.Name == ground.Name);
            Assert.Equal("1", sm.Macros["REY_GRAPH"]);
            Assert.Equal(new[] { "DiffuseTexture", Overlay }, sm.Slots.Select(s => s.SamplerName));
            var galaxy = sm.Slots.Single(s => s.SamplerName == Overlay);
            Assert.Equal(HashAlgorithms.WadPath(MaterialImage), galaxy.ChunkHash != 0 ? galaxy.ChunkHash : HashAlgorithms.WadPath(galaxy.Path.ToLowerInvariant()));
            var diffuseBefore = ground.Slots.Single(s => s.SamplerName == "DiffuseTexture");
            var diffuseAfter = sm.Slots.Single(s => s.SamplerName == "DiffuseTexture");
            Assert.Equal(diffuseBefore.ChunkHash, diffuseAfter.ChunkHash);
            Assert.Equal(sm.Parameters.Select(x => x.Name), ground.Parameters.Select(x => x.Name));   // no parameter was added (the graph declares none)
            foreach (var m in shipped.Materials.Where(m => m.Name != ground.Name))
                Assert.Equal(doc.Materials.Single(x => x.Name == m.Name).Slots.Select(x => x.SamplerName), m.Slots.Select(x => x.SamplerName));
        }

        // THE TOC and the container: 16 new blobs, each of which names the texture and its sampler
        using var sw = WadArchive.Open(Path.Combine(tmp, "ShaderCache.dx11.wad.client"));
        Assert.Equal(2, sw.Entries.Count);
        var back = ShaderCacheReader.ParseToc(sw.Extract(HashAlgorithms.WadPath(TocName)), TocName)!;
        Assert.Contains(("REY_GRAPH", "1"), back.DefinePool);
        byte[] container = sw.Extract(HashAlgorithms.WadPath(TocName + "_200"));
        Assert.Equal(54, SgShipBuilder.ReadRecords(container, "shipped container"));
        var blobs = new List<byte[]>();
        int off = 0, idx = 0;
        while (off < container.Length)
        {
            int len = BitConverter.ToInt32(container, off);
            if (idx >= 38) blobs.Add(container.AsSpan(off + 4, len - 1).ToArray());
            off += 4 + len; idx++;
        }
        Assert.Equal(16, blobs.Count);
        var hrs = ShaderGraphShipRealDataTests.CreatePixelShaders(blobs);
        if (hrs.Length == 0) output.WriteLine("SKIPPED: no hardware D3D11 device for CreatePixelShader");
        else { output.WriteLine("CreatePixelShader hr: " + string.Join(" ", hrs.Select(h => "0x" + h.ToString("x8")))); Assert.All(hrs, h => Assert.True(h >= 0)); }
        foreach (var blob in blobs)
        {
            var refl = DxbcReflection.Parse(blob);
            Assert.Contains(refl.Resources, r => r.Name == Overlay + "__TX");
            Assert.Contains(refl.Resources, r => r.Name == Overlay + "__SMP");
            Assert.Contains(refl.Resources, r => r.Name == "DiffuseTexture__TX");
            Assert.DoesNotContain(refl.ConstantBuffers, c => c.Name == "$Globals" && c.Variables.Any(v => v.Name == Overlay));
        }
        output.WriteLine($"exported {packagePath} ({new FileInfo(packagePath).Length:n0} bytes)");
    }

    [Fact]
    public void A_default_image_that_is_no_asset_or_is_a_hash_blocks_the_export_and_a_game_asset_passes()
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(Final)) { output.WriteLine("SKIPPED: no game install"); return; }
        HashDatabase db;
        try { db = new HashSyncService().LoadLocal(_ => { }); } catch { output.WriteLine("SKIPPED: no hash dictionary"); return; }
        var resolver = new WadPathResolver(db);
        string map11 = Path.Combine(Final, "Maps", "Shipping", "Map11.wad.client");
        if (!File.Exists(map11)) { output.WriteLine("SKIPPED: no Map11.wad.client"); return; }
        byte[] riotBin;
        using (var w = WadArchive.Open(map11, resolver)) riotBin = w.Extract(HashAlgorithms.WadPath(BaseSrx));
        Func<uint, string?> binName = h => db.TryGetBinName(h, out var n) ? n : null;

        string project = Path.Combine(_root, "project");
        Directory.CreateDirectory(ShaderGraphRegistry.FolderIn(project));
        var g = ShaderGraphDocument.CreateEmpty("tex", Shader);
        g.Textures.Add(new SgCustomTexture { Name = "Pic", DefaultPath = "assets/nope/missing.tex" });
        ShaderGraphTests.Add(g, "n2", "TextureSample", ("texture", "Pic"));
        ShaderGraphTests.Link(g, "n2", "RGB", "n1", SgCatalog.BaseColorPin);
        string file = ShaderGraphRegistry.GraphPath(project, "Pic");
        File.WriteAllText(file, ShaderGraphJson.Serialize(g));
        var doc = MaterialDocument.Parse(riotBin, binName);
        var editor = new MaterialEditorViewModel { UndoService = new UndoRedoService(), ProjectRoot = () => project };
        editor.Load(doc, new WadAssetEntry { Path = BaseSrx });
        var b = editor.Materials.Single(m => m.Name.EndsWith(Ground, StringComparison.OrdinalIgnoreCase));
        b.AssignGraphCommand.Execute(b.GraphChoices.Single());
        string mapDir = Path.Combine(project, "Map11");
        Directory.CreateDirectory(Path.Combine(mapDir, "data", "maps", "mapgeometry", "map11"));
        File.WriteAllBytes(Path.Combine(mapDir, BaseSrx.Replace('/', Path.DirectorySeparatorChar)), editor.Serialize()!);

        var ex = Assert.Throws<InvalidOperationException>(() => ReyEngine.App.Services.ShaderGraphShipService.Prepare(project, Final, new[] { mapDir }, binName, resolver));
        output.WriteLine(ex.Message);
        Assert.Contains("default image of custom texture 'Pic'", ex.Message);
        Assert.Contains("not an asset of the game or of this project", ex.Message);

        // a game asset (found in the game's archives, not in the project) passes
        g.Textures[0].DefaultPath = DefaultImage;
        File.WriteAllText(file, ShaderGraphJson.Serialize(g));
        Assert.NotNull(ReyEngine.App.Services.ShaderGraphShipService.Prepare(project, Final, new[] { mapDir }, binName, resolver));

        // the hash form is refused by the validator whatever the archives hold
        g.Textures[0].DefaultPath = "0x3fa863f58325b833";
        File.WriteAllText(file, ShaderGraphJson.Serialize(g));
        var ex2 = Assert.Throws<InvalidOperationException>(() => ReyEngine.App.Services.ShaderGraphShipService.Prepare(project, Final, new[] { mapDir }, binName, resolver));
        Assert.Contains("0x", ex2.Message);
    }
}
