using System.IO.Compression;
using System.Numerics;
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
/// M834: custom parameters SHIP (test4 worked in game): the installed shaders.bin is patched at export - Riot's file with only DefaultEnv_Flat's
/// parameters extended - and written to the Global WAD of the package beside the shader cache patch and the material. A scratch project with a graph that reads
/// one custom colour and one custom float is exported through the real view model and read back: the shaders.bin diff, the material parameters, the TOC and
/// every new blob on a real D3D11 device. Set <c>REYENGINE_M834_FANTOME</c> to keep the package (this is how ShaderGraphEditor_16.20-test5.fantome is made).
/// No-ops with a SKIPPED line where the game or the hash dictionary is absent.
/// </summary>
public sealed class ShaderGraphCustomExportRealDataTests(ITestOutputHelper output) : IDisposable
{
    private const string Game = @"C:\Riot Games\League of Legends";
    private const string Final = Game + @"\Game\DATA\FINAL";
    private const string Shader = "Shaders/StaticMesh/DefaultEnv_Flat";
    private const string TocName = "assets/shaders/generated/shaders/staticmesh/defaultenv_flat.ps-dx11";
    private const string BaseSrx = "data/maps/mapgeometry/map11/base_srx.materials.bin";
    private const string Ground = "Worlds_Ground_A5_OrderBase_A_MAT";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rey-m834-export-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>The test5 graph: the ground texture with diagonal stripes of a custom colour, mixed in by a custom amount.</summary>
    internal static ShaderGraphDocument Test5Graph()
    {
        var d = ShaderGraphDocument.CreateEmpty("ShaderGraphEditor_test5", Shader);
        d.Params.Add(new SgCustomParam { Name = "StripeColor", Components = 4, Default = "1 0.2 0.8 1", IsColor = true });
        d.Params.Add(new SgCustomParam { Name = "StripeAmount", Components = 1, Default = "0.85" });
        void A(string id, string type, params (string, string)[] p) => ShaderGraphTests.Add(d, id, type, p);
        void L(string a, string ap, string b, string bp) => ShaderGraphTests.Link(d, a, ap, b, bp);
        A("n2", "TextureSample", ("texture", "DiffuseTexture"));
        A("n3", "TexCoord", ("set", "1"));
        A("n4", "ComponentMask", ("mask", "r")); A("n5", "ComponentMask", ("mask", "g"));
        A("n6", "Add"); A("n7", "Multiply", ("B", "6")); A("n8", "Frac"); A("n9", "Step", ("Edge", "0.5"));
        A("n10", "Parameter", ("name", "StripeAmount")); A("n11", "Multiply");
        A("n12", "Parameter", ("name", "StripeColor")); A("n13", "ComponentMask", ("mask", "rgb"));
        A("n14", "Lerp");
        L("n3", "UV", "n4", "Value"); L("n3", "UV", "n5", "Value");
        L("n4", "Value", "n6", "A"); L("n5", "Value", "n6", "B");
        L("n6", "Value", "n7", "A"); L("n7", "Value", "n8", "Value"); L("n8", "Value", "n9", "X");
        L("n9", "Value", "n11", "A"); L("n10", "Value", "n11", "B");
        L("n12", "Value", "n13", "Value");
        L("n2", "RGB", "n14", "A"); L("n13", "Value", "n14", "B"); L("n11", "Value", "n14", "Alpha");
        L("n14", "Value", "n1", SgCatalog.BaseColorPin); L("n2", "A", "n1", SgCatalog.OpacityPin);
        return d;
    }

    private (HashDatabase Db, WadPathResolver Resolver)? Open()
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(Final)) { output.WriteLine("SKIPPED: no game install"); return null; }
        HashDatabase db;
        try { db = new HashSyncService().LoadLocal(_ => { }); } catch { output.WriteLine("SKIPPED: no hash dictionary"); return null; }
        return (db, new WadPathResolver(db));
    }

    private static byte[] InstalledShadersBin()
    {
        using var g = WadArchive.Open(Path.Combine(Final, SgShadersBin.WadName));
        return g.Extract(HashAlgorithms.WadPath(SgShadersBin.GamePath));
    }

    // ================================================================================= the patcher on Riot's real file

    [Fact]
    public void The_patch_of_the_installed_shaders_bin_changes_only_DefaultEnv_Flat_and_matches_the_probe_that_worked_in_game()
    {
        if (Open() is null) return;
        byte[] riot = InstalledShadersBin();
        var before = SgShadersBin.Declared(riot, Shader);
        output.WriteLine($"installed shaders.bin {riot.Length:n0} B; {Shader} declares {string.Join(", ", before.Select(p => p.Name + ":" + SgCatalog.TypeName(p.Components)))}");
        Assert.Contains(before, p => p.Name == "TintColor" && p.Components == 4);

        // the same two parameters the test4 probe declared: its proven shaders.bin is the reference when the install is the build it was made from
        var add = new[] { new SgDeclaredParam("ReyColorA", 4), new SgDeclaredParam("ReyAmount", 1) };
        byte[] patched = SgShadersBin.Patch(riot, Shader, add);
        Assert.Empty(SgShadersBin.Verify(riot, patched, Shader, add));
        var after = SgShadersBin.Declared(patched, Shader);
        Assert.Equal(before.Count + 2, after.Count);
        Assert.Equal(new[] { 15u, 1u }, add.Select(a => a.FieldsMask));
        string probe = @"D:\ProjectReyMaps\_ModUpdate\_shadergraph\build_test4\Global\data\shaders\shaders.bin";
        string probeRiot = @"D:\ProjectReyMaps\_ModUpdate\_shadergraph\build_test4\riot_shaders.bin";
        if (File.Exists(probe) && File.Exists(probeRiot) && File.ReadAllBytes(probeRiot).AsSpan().SequenceEqual(riot))
        {
            byte[] proven = File.ReadAllBytes(probe);
            output.WriteLine($"probe test4 shaders.bin {proven.Length:n0} B; identical to the C# patch: {proven.AsSpan().SequenceEqual(patched)}");
            Assert.True(proven.AsSpan().SequenceEqual(patched), "the C# patch must equal the shaders.bin that worked in game, byte for byte");
        }
        else output.WriteLine("the probe's test4 shaders.bin is not here (or the install is another build): byte comparison skipped");

        // the guards
        Assert.Throws<InvalidDataException>(() => SgShadersBin.Patch(riot, Shader, new[] { new SgDeclaredParam("TintColor", 4) }));        // already declared
        Assert.Throws<InvalidDataException>(() => SgShadersBin.Patch(riot, Shader, new[] { new SgDeclaredParam("tintcolor", 4) }));
        Assert.Throws<InvalidDataException>(() => SgShadersBin.Patch(riot, Shader, new[] { new SgDeclaredParam("A", 1), new SgDeclaredParam("a", 1) }));
        Assert.Throws<InvalidDataException>(() => SgShadersBin.Patch(riot, Shader, new[] { new SgDeclaredParam("bad name", 1) }));
        Assert.Throws<InvalidDataException>(() => SgShadersBin.Patch(riot, "Shaders/Nope", add));
        Assert.Throws<InvalidDataException>(() => SgShadersBin.Patch(riot.AsSpan(0, riot.Length - 5).ToArray(), Shader, add));
        // the check notices anything else that changed
        var tampered = (byte[])patched.Clone();
        tampered[^3] ^= 0xFF;
        Assert.NotEmpty(SgShadersBin.Verify(riot, tampered, Shader, add));
        Assert.NotEmpty(SgShadersBin.Verify(riot, patched, Shader, new[] { new SgDeclaredParam("Other", 1) }));
    }

    // ================================================================================= the whole export

    [Fact]
    public void A_graph_with_custom_parameters_exports_with_a_patched_shaders_bin_the_material_values_and_the_twinned_table()
    {
        var o = Open();
        if (o is null) return;
        var (db, resolver) = o.Value;
        string map11 = Path.Combine(Final, "Maps", "Shipping", "Map11.wad.client");
        if (!File.Exists(map11)) { output.WriteLine("SKIPPED: no Map11.wad.client"); return; }
        byte[] riotBin;
        using (var w = WadArchive.Open(map11, resolver))
        {
            ulong h = HashAlgorithms.WadPath(BaseSrx);
            if (!w.TryGetEntry(h, out _)) { output.WriteLine("SKIPPED: Map11 has no base_srx.materials.bin"); return; }
            riotBin = w.Extract(h);
        }

        // ---- a SCRATCH project: Riot's base_srx bin, the test5 graph, the assignment and the parameter values made through the editor
        string project = Path.Combine(_root, "project");
        Directory.CreateDirectory(project);
        var doc = MaterialDocument.Parse(riotBin, h => db.TryGetBinName(h, out var n) ? n : null);
        var ground = doc.Materials.Single(m => m.Name.EndsWith(Ground, StringComparison.OrdinalIgnoreCase));
        Directory.CreateDirectory(ShaderGraphRegistry.FolderIn(project));
        File.WriteAllText(ShaderGraphRegistry.GraphPath(project, "ShaderGraphEditor_test5"), ShaderGraphJson.Serialize(Test5Graph()));

        var undo = new UndoRedoService();
        var editor = new MaterialEditorViewModel { UndoService = undo, ProjectRoot = () => project };
        editor.Load(doc, new WadAssetEntry { Path = BaseSrx });
        var binding = editor.Materials.Single(m => m.Name == ground.Name);
        binding.RefreshMacroStatus();
        binding.AssignGraphCommand.Execute(Assert.Single(binding.GraphChoices));
        Assert.True(binding.HasGraphAssigned, binding.GraphAssignmentText);
        Assert.False(binding.GraphAssignmentIsError, binding.GraphAssignmentText);                    // no longer blocked
        Assert.Equal(new[] { "StripeColor", "StripeAmount" }, binding.GraphParams.Select(r => r.Name));
        foreach (var row in binding.GraphParams.ToList()) row.AddToMaterialCommand.Execute(null);      // written as ordinary paramValues
        void Set(string name, string text) { var pr = binding.Parameters.Single(p => p.Name == name); pr.EditedText = text; pr.ApplyCommand.Execute(null); Assert.False(pr.HasError, pr.ErrorText); }
        Set("StripeColor", "1, 0.2, 0.8, 1");
        Set("StripeAmount", "0.85, 0, 0, 0");
        byte[] edited = editor.Serialize()!;
        Directory.CreateDirectory(Path.Combine(project, "Map11", "data", "maps", "mapgeometry", "map11"));
        File.WriteAllBytes(Path.Combine(project, "Map11", BaseSrx.Replace('/', Path.DirectorySeparatorChar)), edited);
        byte[] gameShadersBefore = InstalledShadersBin();

        // ---- Export .fantome through the real view model
        var p = new ReyProject { Name = "ScratchCustomParams", RootPath = project, GameDirectory = Game, OutputDirectory = Path.Combine(_root, "build"), ProjectFolders = { "Map11" } };
        var vm = new MainWindowViewModel { Project = p };
        string keep = System.Environment.GetEnvironmentVariable("REYENGINE_M834_FANTOME") ?? "";
        string packagePath = keep.Length > 0 ? keep : Path.Combine(_root, "out.fantome");
        Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
        Directory.CreateDirectory(p.OutputDirectory!);
        var method = typeof(MainWindowViewModel).GetMethod("ExportFantomeCore", BindingFlags.NonPublic | BindingFlags.Instance)!;
        try
        {
            method.Invoke(vm, new object?[] { packagePath, new FantomeMeta
                {
                    Name = "ShaderGraphEditor test6", Author = "ReyEngine", Version = "16.20-test6",
                    Description = "ReyEngine Shader Graph export test: custom parameters. Blue-base ground texture with diagonal stripes: StripeColor (custom float4 colour, pink) mixed in by StripeAmount (custom float, 0.85). Ships a patched shaders.bin.",
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

        // THE shaders.bin DIFF: one chunk, Riot's file with only DefaultEnv_Flat's parameters extended
        using (var gw = WadArchive.Open(Path.Combine(tmp, "Global.wad.client")))
        {
            var entry = Assert.Single(gw.Entries);
            Assert.Equal(HashAlgorithms.WadPath(SgShadersBin.GamePath), entry.PathHash);
            byte[] shipped = gw.Extract(entry.PathHash);
            var add = new[] { new SgDeclaredParam("StripeAmount", 1), new SgDeclaredParam("StripeColor", 4) };      // the patch orders by name
            Assert.Empty(SgShadersBin.Verify(gameShadersBefore, shipped, Shader, add));
            var declared = SgShadersBin.Declared(shipped, Shader);
            output.WriteLine($"shipped shaders.bin {shipped.Length:n0} B (installed {gameShadersBefore.Length:n0}); {Shader} now declares {string.Join(", ", declared.Select(x => x.Name + ":" + SgCatalog.TypeName(x.Components)))}");
            Assert.Equal(new[] { "TintColor", "StripeAmount", "StripeColor" }, declared.Select(x => x.Name));
            Assert.Equal(new[] { 4, 1, 4 }, declared.Select(x => x.Components));
        }
        Assert.True(InstalledShadersBin().AsSpan().SequenceEqual(gameShadersBefore), "the installed game file must not have been touched");

        // THE MATERIAL: the values, every other material as Riot ships it
        using (var mw = WadArchive.Open(Path.Combine(tmp, "Map11.wad.client")))
        {
            var shipped = MaterialDocument.Parse(mw.Extract(HashAlgorithms.WadPath(BaseSrx)), h => db.TryGetBinName(h, out var n) ? n : null);
            var sm = shipped.Materials.Single(m => m.Name == ground.Name);
            Assert.Equal("1", sm.Macros["REY_GRAPH"]);
            Assert.True(sm.Parameters.Single(x => x.Name == "StripeColor").TryGetVector4(out var color));
            Assert.Equal(new Vector4(1f, 0.2f, 0.8f, 1f), color);
            Assert.True(sm.Parameters.Single(x => x.Name == "StripeAmount").TryGetVector4(out var amount));
            Assert.Equal(0.85f, amount.X, 4);
            // LTK Manager's health check (rule bin/property-type): every paramValues value of the assigned material is a vec4, the float one included (test5 wrote it as f32 and was FATAL)
            Assert.All(sm.Parameters, x => Assert.Equal("Vector4", x.TypeName));
            Assert.Equal(0f, amount.Y);
            foreach (var m in shipped.Materials.Where(m => m.Name != ground.Name))
                Assert.Equal(doc.Materials.Single(x => x.Name == m.Name).Parameters.Select(x => x.Name), m.Parameters.Select(x => x.Name));
        }

        // THE TOC and the container
        using var sw = WadArchive.Open(Path.Combine(tmp, "ShaderCache.dx11.wad.client"));
        Assert.Equal(2, sw.Entries.Count);
        var back = ShaderCacheReader.ParseToc(sw.Extract(HashAlgorithms.WadPath(TocName)), TocName)!;
        Assert.Contains(("REY_GRAPH", "1"), back.DefinePool);
        byte[] container = sw.Extract(HashAlgorithms.WadPath(TocName + "_200"));
        Assert.Equal(54, SgShipBuilder.ReadRecords(container, "shipped container"));

        // CreatePixelShader on every new blob; their $Globals hold only declared names
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
            var g = refl.ConstantBuffers.Single(c => c.Name == "$Globals");
            Assert.All(g.Variables, v => Assert.Contains(v.Name, new[] { "StripeColor", "StripeAmount" }));
            Assert.Equal(2, g.Variables.Count);
        }
        output.WriteLine($"exported {packagePath} ({new FileInfo(packagePath).Length:n0} bytes)");
    }

    [Fact]
    public void A_project_that_already_ships_its_own_shaders_bin_refuses_the_export()
    {
        var o = Open();
        if (o is null) return;
        var (db, resolver) = o.Value;
        string map11 = Path.Combine(Final, "Maps", "Shipping", "Map11.wad.client");
        if (!File.Exists(map11)) { output.WriteLine("SKIPPED: no Map11.wad.client"); return; }
        byte[] riotBin;
        using (var w = WadArchive.Open(map11, resolver)) riotBin = w.Extract(HashAlgorithms.WadPath(BaseSrx));
        string project = Path.Combine(_root, "project");
        Directory.CreateDirectory(ShaderGraphRegistry.FolderIn(project));
        File.WriteAllText(ShaderGraphRegistry.GraphPath(project, "g"), ShaderGraphJson.Serialize(Test5Graph()));
        var doc = MaterialDocument.Parse(riotBin, h => db.TryGetBinName(h, out var n) ? n : null);
        var editor = new MaterialEditorViewModel { UndoService = new UndoRedoService(), ProjectRoot = () => project };
        editor.Load(doc, new WadAssetEntry { Path = BaseSrx });
        var b = editor.Materials.Single(m => m.Name.EndsWith(Ground, StringComparison.OrdinalIgnoreCase));
        b.AssignGraphCommand.Execute(b.GraphChoices.Single());
        string map = Path.Combine(project, "Map11");
        Directory.CreateDirectory(Path.Combine(map, "data", "maps", "mapgeometry", "map11"));
        File.WriteAllBytes(Path.Combine(map, BaseSrx.Replace('/', Path.DirectorySeparatorChar)), editor.Serialize()!);
        string global = Path.Combine(project, "Global");
        Directory.CreateDirectory(Path.Combine(global, "data", "shaders"));
        File.WriteAllBytes(Path.Combine(global, "data", "shaders", "shaders.bin"), new byte[] { 1 });

        var ex = Assert.Throws<InvalidOperationException>(() => ReyEngine.App.Services.ShaderGraphShipService.Prepare(
            project, Final, new[] { map, global }, h => db.TryGetBinName(h, out var n) ? n : null, resolver));
        output.WriteLine(ex.Message);
        Assert.Contains("already ships its own data/shaders/shaders.bin", ex.Message);
        Assert.Contains("merging", ex.Message);
    }

    [Fact]
    public void A_material_that_keeps_a_custom_parameter_after_unassigning_the_graph_blocks_the_export_by_itself()
    {
        var o = Open();
        if (o is null) return;
        var (db, resolver) = o.Value;
        string map11 = Path.Combine(Final, "Maps", "Shipping", "Map11.wad.client");
        if (!File.Exists(map11)) { output.WriteLine("SKIPPED: no Map11.wad.client"); return; }
        byte[] riotBin;
        using (var w = WadArchive.Open(map11, resolver)) riotBin = w.Extract(HashAlgorithms.WadPath(BaseSrx));
        string project = Path.Combine(_root, "project");
        Directory.CreateDirectory(ShaderGraphRegistry.FolderIn(project));
        File.WriteAllText(ShaderGraphRegistry.GraphPath(project, "ShaderGraphEditor_test5"), ShaderGraphJson.Serialize(Test5Graph()));
        var doc = MaterialDocument.Parse(riotBin, h => db.TryGetBinName(h, out var n) ? n : null);
        var editor = new MaterialEditorViewModel { UndoService = new UndoRedoService(), ProjectRoot = () => project };
        editor.Load(doc, new WadAssetEntry { Path = BaseSrx });
        var b = editor.Materials.Single(m => m.Name.EndsWith(Ground, StringComparison.OrdinalIgnoreCase));
        b.AssignGraphCommand.Execute(b.GraphChoices.Single());
        foreach (var row in b.GraphParams.ToList()) row.AddToMaterialCommand.Execute(null);
        // the ways a parameter outlives its graph without the editor's unassign: macro dropped by hand (an editor-only undo, a text edit, a copied material)
        MacroPresenceCommand.Write(b.Model, SgShip.Macro, null);
        string map = Path.Combine(project, "Map11");
        Directory.CreateDirectory(Path.Combine(map, "data", "maps", "mapgeometry", "map11"));
        File.WriteAllBytes(Path.Combine(map, BaseSrx.Replace('/', Path.DirectorySeparatorChar)), editor.Serialize()!);

        var ex = Assert.Throws<InvalidOperationException>(() => ReyEngine.App.Services.ShaderGraphShipService.Prepare(
            project, Final, new[] { map }, h => db.TryGetBinName(h, out var n) ? n : null, resolver));
        output.WriteLine(ex.Message);
        Assert.Contains("StripeColor", ex.Message); Assert.Contains("StripeAmount", ex.Message);
        Assert.Contains(Ground, ex.Message);
        Assert.Contains("not assigned to a graph that declares it", ex.Message);

        // and the editor's own unassign takes them away, so the same project exports nothing and nothing blocks
        MacroPresenceCommand.Write(b.Model, SgShip.Macro, "1");
        b.UnassignGraphCommand.Execute(null);
        Assert.DoesNotContain(b.Model.Parameters, p => p.Name is "StripeColor" or "StripeAmount");
        File.WriteAllBytes(Path.Combine(map, BaseSrx.Replace('/', Path.DirectorySeparatorChar)), editor.Serialize()!);
        Assert.Null(ReyEngine.App.Services.ShaderGraphShipService.Prepare(project, Final, new[] { map }, h => db.TryGetBinName(h, out var n) ? n : null, resolver));
    }

    [Fact]
    public void The_hash_named_form_of_a_project_shaders_bin_is_caught_too_and_names_that_differ_only_in_case_are_refused()
    {
        var o = Open();
        if (o is null) return;
        var (db, resolver) = o.Value;
        string map11 = Path.Combine(Final, "Maps", "Shipping", "Map11.wad.client");
        if (!File.Exists(map11)) { output.WriteLine("SKIPPED: no Map11.wad.client"); return; }
        byte[] riotBin;
        using (var w = WadArchive.Open(map11, resolver)) riotBin = w.Extract(HashAlgorithms.WadPath(BaseSrx));
        string project = Path.Combine(_root, "project");
        Directory.CreateDirectory(ShaderGraphRegistry.FolderIn(project));
        File.WriteAllText(ShaderGraphRegistry.GraphPath(project, "ShaderGraphEditor_test5"), ShaderGraphJson.Serialize(Test5Graph()));
        var doc = MaterialDocument.Parse(riotBin, h => db.TryGetBinName(h, out var n) ? n : null);
        var editor = new MaterialEditorViewModel { UndoService = new UndoRedoService(), ProjectRoot = () => project };
        editor.Load(doc, new WadAssetEntry { Path = BaseSrx });
        var b = editor.Materials.Single(m => m.Name.EndsWith(Ground, StringComparison.OrdinalIgnoreCase));
        b.AssignGraphCommand.Execute(b.GraphChoices.Single());
        string map = Path.Combine(project, "Map11");
        Directory.CreateDirectory(Path.Combine(map, "data", "maps", "mapgeometry", "map11"));
        File.WriteAllBytes(Path.Combine(map, BaseSrx.Replace('/', Path.DirectorySeparatorChar)), editor.Serialize()!);
        string global = Path.Combine(project, "Global");
        Directory.CreateDirectory(global);
        File.WriteAllBytes(Path.Combine(global, HashAlgorithms.WadPath(SgShadersBin.GamePath).ToString("x16") + ".bin"), new byte[] { 1 });   // 0x....bin: the loose hash-named form

        var ex = Assert.Throws<InvalidOperationException>(() => ReyEngine.App.Services.ShaderGraphShipService.Prepare(
            project, Final, new[] { map, global }, h => db.TryGetBinName(h, out var n) ? n : null, resolver));
        Assert.Contains("already ships its own data/shaders/shaders.bin", ex.Message);
    }

    [Fact]
    public void A_material_parameter_of_a_graph_that_clashes_with_a_declared_name_or_is_unknown_is_still_blocked()
    {
        var o = Open();
        if (o is null) return;
        var (db, resolver) = o.Value;
        var g = Test5Graph();
        g.Params[0].Name = "TintColor";                                   // a name Riot's definition declares
        string project = Path.Combine(_root, "project");
        Directory.CreateDirectory(ShaderGraphRegistry.FolderIn(project));
        File.WriteAllText(ShaderGraphRegistry.GraphPath(project, "g"), ShaderGraphJson.Serialize(g));
        int n = ShaderGraphRegistry.NumberFor(project, "g", Shader);
        using var cache = ShaderCacheReader.Open(Final, resolver, out _)!;
        var perms = new ShaderPermutationIndex(Final, h => resolver.TryGetPath(h, out var pth) ? pth : null);
        var b = ShaderGraphBaseResolver.Resolve(cache, perms, Shader, out string err, out _);
        Assert.True(b is not null, err);
        var m = new SgAssignedMaterial("data/x.materials.bin", "Mat", Shader, n.ToString(), new[] { "TintColor", "NotDeclared" });
        var problems = SgShipValidator.Validate(m, SgShipValidator.Resolve(project, Shader, n), b);
        output.WriteLine(string.Join("\n", problems));
        Assert.Contains(problems, x => x.Contains("'TintColor'") && x.Contains("cannot be declared"));
        Assert.Contains(problems, x => x.Contains("'NotDeclared'") && x.Contains("Missing shader constant"));
    }
}
