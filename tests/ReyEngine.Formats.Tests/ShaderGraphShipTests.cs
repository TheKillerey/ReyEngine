using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Build;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Undo;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Materials.ShaderGraph;
using ReyEngine.Formats.Shaders;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M833: shipping Shader Graphs. The pure pieces without a game install: the twinned TOC and the appended container, the 0x35 trailer, the
/// numbering, the export validator, assign / unassign with undo, and that packing carries the generated shader-cache entries but never the
/// project's <c>.reyengine</c> folder. The same pieces against Riot's installed cache are in <see cref="ShaderGraphShipRealDataTests"/>.
/// </summary>
public sealed class ShaderGraphShipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rey-m833-" + Guid.NewGuid().ToString("N"));
    public ShaderGraphShipTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static byte[] FakeDxbc(byte tag)
    {
        var b = new byte[40];
        b[0] = (byte)'D'; b[1] = (byte)'X'; b[2] = (byte)'B'; b[3] = (byte)'C'; b[24] = 40; b[39] = tag;
        return b;
    }

    // ================================================================================= container + TOC

    [Fact]
    public void TheContainerWriterKeepsZeroAsItsDefaultTrailerAndWritesRiotsWhenAsked()
    {
        var blobs = new[] { FakeDxbc(1), FakeDxbc(2) };
        byte[] zero = ShaderCachePatchWriter.WriteContainer(blobs);
        byte[] riot = ShaderCachePatchWriter.WriteContainer(blobs, SgShip.RecordTrailer);
        Assert.Equal(2 * (4 + 40 + 1), zero.Length);
        Assert.Equal(zero.Length, riot.Length);
        for (int i = 0; i < 2; i++)
        {
            int off = i * 45;
            Assert.Equal(41, BitConverter.ToInt32(riot, off));          // length = DXBC + 1
            Assert.Equal(blobs[i], riot.AsSpan(off + 4, 40).ToArray());
            Assert.Equal(0x35, riot[off + 44]);
            Assert.Equal(0x00, zero[off + 44]);
        }
    }

    private static (SgBase Base, ShaderStageToc Toc, byte[] TocBytes, byte[] Container) SyntheticCache()
    {
        ulong K(params string[] d) => ShaderCacheReader.PermutationKey(d);
        var perms = new[]
        {
            new ShaderPermutation(K(), 0), new ShaderPermutation(K("A=1"), 1),
            new ShaderPermutation(K("GENERATE_SHADOW_MAP=1"), 2), new ShaderPermutation(K("A=1", "GENERATE_SHADOW_MAP=1"), 3),
            new ShaderPermutation(K("A=1"), 1),   // a repeated key, as Riot's TOCs have
        };
        var toc = new ShaderStageToc
        {
            Path = "assets/shaders/generated/shaders/x.ps-dx11", ShaderName = "assets/shaders/generated/shaders/x", Stage = DxbcStage.Pixel,
            DefinePool = new List<(string, string)> { ("A", "1"), ("GENERATE_SHADOW_MAP", "1") },
            Permutations = perms, DeclaredBlobCount = 4, Flag = 1,
        };
        byte[] tocBytes = ShaderCachePatchWriter.WriteToc(toc, perms, 4);
        byte[] container = ShaderCachePatchWriter.WriteContainer(new[] { FakeDxbc(10), FakeDxbc(11), FakeDxbc(12), FakeDxbc(13) }, SgShip.RecordTrailer);
        var sigs = new[]
        {
            new SgSignature { Index = 0, Inputs = Array.Empty<DxbcSignatureElement>(), Outputs = Array.Empty<DxbcSignatureElement>(), Permutations = new[] { perms[0] } },
            new SgSignature { Index = 1, Inputs = Array.Empty<DxbcSignatureElement>(), Outputs = Array.Empty<DxbcSignatureElement>(), Permutations = new[] { perms[1] } },
        };
        var b = new SgBase { Shader = "Shaders/X", Signatures = sigs, ShadowPermutations = new[] { perms[2], perms[3] }, PixelTocPath = toc.Path, PixelBlobCount = 4 };
        return (b, toc, tocBytes, container);
    }

    [Fact]
    public void TheTocGetsAPoolPairPerGraphAndATwinOfEveryUniqueRiotKey()
    {
        var (b, toc, tocBytes, container) = SyntheticCache();
        var patch = SgShipBuilder.Build(b, toc, tocBytes, container, toc.Path + "_0", new[]
        {
            new SgGraphBlobs(3, "three", new[] { FakeDxbc(31), FakeDxbc(32) }),
            new SgGraphBlobs(1, "one", new[] { FakeDxbc(11), FakeDxbc(12) }),
        });
        var back = ShaderCacheReader.ParseToc(patch.Toc, toc.Path)!;

        Assert.Equal(new[] { ("A", "1"), ("GENERATE_SHADOW_MAP", "1"), ("REY_GRAPH", "1"), ("REY_GRAPH", "3") }, back.DefinePool.ToArray());
        Assert.Equal(8u, back.DeclaredBlobCount);
        Assert.Equal(toc.Flag, back.Flag);
        // Riot's keys and blob indices first, unchanged and in order (five, the repeated one included)
        Assert.Equal(toc.Permutations.Select(p => (p.Key, p.BlobIndex)), back.Permutations.Take(5).Select(p => (p.Key, p.BlobIndex)));
        // 4 unique keys x 2 graphs = 8 twins, graph-major (number 1 first whatever the argument order)
        Assert.Equal(5 + 8, back.Permutations.Count);
        Assert.Equal(4 + 4, patch.TwinKeys);
        Assert.Equal(4, patch.ColourTwins);
        Assert.Equal(4, patch.ShadowTwins);
        uint At(params string[] d) => back.Permutations.Single(p => p.Key == ShaderCacheReader.PermutationKey(d)).BlobIndex;
        Assert.Equal(4u, At("REY_GRAPH=1"));                             // colour, signature 0 of graph 1
        Assert.Equal(5u, At("A=1", "REY_GRAPH=1"));                      // colour, signature 1 of graph 1
        Assert.Equal(2u, At("GENERATE_SHADOW_MAP=1", "REY_GRAPH=1"));    // shadow: Riot's own blob
        Assert.Equal(3u, At("A=1", "GENERATE_SHADOW_MAP=1", "REY_GRAPH=1"));
        Assert.Equal(6u, At("REY_GRAPH=3"));
        Assert.Equal(7u, At("A=1", "REY_GRAPH=3"));
        Assert.Equal(2u, At("GENERATE_SHADOW_MAP=1", "REY_GRAPH=3"));
    }

    [Fact]
    public void TheContainerKeepsRiotsRecordsAndAppendsTheGraphBlobsWithATrailerOf35()
    {
        var (b, toc, tocBytes, container) = SyntheticCache();
        var patch = SgShipBuilder.Build(b, toc, tocBytes, container, toc.Path + "_0", new[] { new SgGraphBlobs(1, "one", new[] { FakeDxbc(21), FakeDxbc(22) }) });

        Assert.Equal(toc.Path + "_0", patch.ContainerPath);
        Assert.Equal(container, patch.Container.AsSpan(0, container.Length).ToArray());          // Riot's four records, byte for byte
        Assert.Equal(6, SgShipBuilder.ReadRecords(patch.Container, "c"));
        for (int i = 0; i < 2; i++)
        {
            int off = container.Length + i * 45;
            Assert.Equal(41, BitConverter.ToInt32(patch.Container, off));
            Assert.Equal(FakeDxbc((byte)(21 + i)), patch.Container.AsSpan(off + 4, 40).ToArray());
            Assert.Equal(0x35, patch.Container[off + 44]);
        }
        Assert.Equal(4u, patch.FirstNewBlob);
        Assert.Equal(SgShip.Sha1Hex(tocBytes), patch.BaseTocSha1);
    }

    [Fact]
    public void ABuildRefusesWhatItCouldNotShipSafely()
    {
        var (b, toc, tocBytes, container) = SyntheticCache();
        var blobs = new[] { FakeDxbc(1), FakeDxbc(2) };
        // a table that already has the axis is not Riot's table
        var modded = new ShaderStageToc
        {
            Path = toc.Path, ShaderName = toc.ShaderName, Stage = toc.Stage, Permutations = toc.Permutations, DeclaredBlobCount = 4, Flag = 1,
            DefinePool = toc.DefinePool.Append(("REY_GRAPH", "1")).ToList(),
        };
        Assert.Throws<InvalidDataException>(() => SgShipBuilder.Build(b, modded, ShaderCachePatchWriter.WriteToc(modded, modded.Permutations, 4), container, "c", new[] { new SgGraphBlobs(2, "g", blobs) }));
        // bytes that are not the table as parsed
        Assert.Throws<InvalidDataException>(() => SgShipBuilder.Build(b, toc, tocBytes.Append((byte)0).ToArray(), container, "c", new[] { new SgGraphBlobs(1, "g", blobs) }));
        // a container whose record count is not the TOC's
        Assert.Throws<InvalidDataException>(() => SgShipBuilder.Build(b, toc, tocBytes, container.AsSpan(0, 45 * 3).ToArray(), "c", new[] { new SgGraphBlobs(1, "g", blobs) }));
        // wrong blob count, number 0, a number twice, a blob that is not DXBC
        Assert.Throws<InvalidDataException>(() => SgShipBuilder.Build(b, toc, tocBytes, container, "c", new[] { new SgGraphBlobs(1, "g", new[] { FakeDxbc(1) }) }));
        Assert.Throws<InvalidDataException>(() => SgShipBuilder.Build(b, toc, tocBytes, container, "c", new[] { new SgGraphBlobs(0, "g", blobs) }));
        Assert.Throws<InvalidDataException>(() => SgShipBuilder.Build(b, toc, tocBytes, container, "c", new[] { new SgGraphBlobs(1, "g", blobs), new SgGraphBlobs(1, "h", blobs) }));
        Assert.Throws<InvalidDataException>(() => SgShipBuilder.Build(b, toc, tocBytes, container, "c", new[] { new SgGraphBlobs(1, "g", new[] { FakeDxbc(1), new byte[40] }) }));
    }

    [Fact]
    public void ABuildThatDoesNotFitTheLastContainerIsRefused()
    {
        var (b, toc, tocBytes, container) = SyntheticCache();
        var blobs = new[] { FakeDxbc(1), FakeDxbc(2) };
        // 4 + 2 x 49 = 102 blobs would need blob 101 in a container that holds 0..99
        var many = Enumerable.Range(1, 49).Select(n => new SgGraphBlobs(n, "g" + n, blobs)).ToList();
        Assert.Throws<InvalidDataException>(() => SgShipBuilder.Build(b, toc, tocBytes, container, "c", many));
    }

    // ================================================================================= numbering

    [Fact]
    public void GraphNumbersAreStablePerBaseShaderAndNeverReused()
    {
        const string flat = "Shaders/StaticMesh/DefaultEnv_Flat";
        Assert.Equal(1, ShaderGraphRegistry.NumberFor(_root, "stripes", flat));
        Assert.Equal(2, ShaderGraphRegistry.NumberFor(_root, "checker", flat));
        Assert.Equal(1, ShaderGraphRegistry.NumberFor(_root, "stripes", flat));                    // stable
        Assert.Equal(1, ShaderGraphRegistry.NumberFor(_root, "other", "Shaders/StaticMesh/Other")); // unique PER BASE
        Assert.Equal(3, ShaderGraphRegistry.NumberFor(_root, "third", flat.ToLowerInvariant()));    // the base spelling does not matter
        Assert.Equal("checker", ShaderGraphRegistry.Find(_root, flat, 2)!.File);
        Assert.Null(ShaderGraphRegistry.Find(_root, flat, 9));

        // the registry file is editor data beside the graphs, and is not a graph
        Assert.True(File.Exists(ShaderGraphRegistry.RegistryPath(_root)));
        Assert.False(ShaderGraphRegistry.FileName.EndsWith(ShaderGraphDocument.FileSuffix));
        Assert.StartsWith(Path.Combine(_root, ".reyengine"), ShaderGraphRegistry.RegistryPath(_root));
    }

    // ================================================================================= validator

    private static ShaderGraphDocument ValidGraph(string name = "g")
    {
        var d = ShaderGraphDocument.CreateEmpty(name, "Shaders/StaticMesh/DefaultEnv_Flat");
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

    private int Save(ShaderGraphDocument d, string stem)
    {
        Directory.CreateDirectory(ShaderGraphRegistry.FolderIn(_root));
        File.WriteAllText(ShaderGraphRegistry.GraphPath(_root, stem), ShaderGraphJson.Serialize(d));
        return ShaderGraphRegistry.NumberFor(_root, stem, d.BaseShader);
    }

    private IReadOnlyList<string> Check(int number, string shader = "Shaders/StaticMesh/DefaultEnv_Flat", params string[] parameters)
    {
        var b = ShaderGraphTests.TestBase();
        var m = new SgAssignedMaterial("data/x.materials.bin", "Mat", shader, number.ToString(), parameters);
        return SgShipValidator.Validate(m, SgShipValidator.Resolve(_root, shader, number), b);
    }

    [Fact]
    public void AValidAssignmentPassesAndEachRuleBlocksWithTheMaterialAndGraphNamed()
    {
        int n = Save(ValidGraph(), "stripes");
        Assert.Empty(Check(n, parameters: "TintColor"));

        // an undeclared material parameter: the test1 crash ("Missing shader constant")
        var bad = Check(n, parameters: new[] { "TintColor", "ReyProbe" });
        var one = Assert.Single(bad);
        Assert.Contains("'Mat'", one); Assert.Contains("ReyProbe", one); Assert.Contains("Missing shader constant", one);

        // the material is on another shader than the graph's base
        var wrong = Assert.Single(Check(n, "Shaders/StaticMesh/DefaultEnv_Flat_AlphaTest"));
        Assert.Contains("belongs to Shaders/StaticMesh/DefaultEnv_Flat", wrong);

        // a number no graph has, and a number that is no number
        Assert.Contains("no Shader Graph", Assert.Single(Check(77)));
        var junk = SgShipValidator.Validate(new SgAssignedMaterial("b", "Mat", "Shaders/StaticMesh/DefaultEnv_Flat", "x", Array.Empty<string>()),
            new SgResolvedGraph(null, null, null, null), ShaderGraphTests.TestBase());
        Assert.Contains("not a graph number", Assert.Single(junk));

        // the graph file is gone (its number stays reserved)
        File.Delete(ShaderGraphRegistry.GraphPath(_root, "stripes"));
        var gone = Assert.Single(Check(n));
        Assert.Contains("stripes", gone); Assert.Contains("missing", gone);
    }

    [Fact]
    public void AGraphWithErrorsBlocksTheExport()
    {
        var d = ValidGraph("broken");
        ShaderGraphTests.Add(d, "n9", "Parameter", ("name", "NotDeclaredAnywhere"));   // not a declared parameter of the base
        ShaderGraphTests.Link(d, "n9", "Value", "n4", "B");
        int n = Save(d, "broken");
        var errors = Check(n, parameters: "TintColor");
        Assert.NotEmpty(errors);
        Assert.All(errors, e => Assert.Contains("'broken'", e));
        Assert.All(errors, e => Assert.Contains("'Mat'", e));
    }

    // ================================================================================= assign + undo

    private static byte[] OneMaterial(string shader)
    {
        var props = new List<BinTreeProperty>
        {
            new BinTreeString(H("name"), "Mat"),
            new BinTreeContainer(H("techniques"), BinPropertyType.Embedded, new BinTreeProperty[]
            {
                new BinTreeEmbedded(0, H("StaticMaterialTechniqueDef"), new BinTreeProperty[]
                {
                    new BinTreeContainer(H("passes"), BinPropertyType.Embedded, new BinTreeProperty[]
                    {
                        new BinTreeEmbedded(0, H("StaticMaterialPassDef"), new BinTreeProperty[] { new BinTreeObjectLink(H("shader"), H(shader)) }),
                    }),
                }),
            }),
        };
        using var ms = new MemoryStream();
        new BinTree(new[] { new BinTreeObject(H("Mat"), H("StaticMaterialDef"), props) }, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    private static Func<uint, string?> Names(string shader)
    {
        var map = new Dictionary<uint, string> { [H("StaticMaterialDef")] = "StaticMaterialDef", [H(shader)] = shader };
        return h => map.TryGetValue(h, out var n) ? n : null;
    }

    [Fact]
    public void AssigningAndUnassigningAGraphIsUndoableAndSavedByTheNormalSave()
    {
        const string shader = "Shaders/StaticMesh/DefaultEnv_Flat";
        Save(ValidGraph("Stripes"), "stripes");
        var undo = new UndoRedoService();
        var editor = new MaterialEditorViewModel { UndoService = undo, ProjectRoot = () => _root };
        editor.Load(MaterialDocument.Parse(OneMaterial(shader), Names(shader)), new WadAssetEntry { Path = "data/maps/mapgeometry/map11/x.materials.bin" });
        var binding = editor.Materials.Single();
        binding.RefreshMacroStatus();

        Assert.True(binding.HasGraphSection);
        Assert.False(binding.HasGraphAssigned);
        var choice = Assert.Single(binding.GraphChoices);

        binding.AssignGraphCommand.Execute(choice);
        Assert.True(binding.HasGraphAssigned);
        Assert.False(binding.GraphAssignmentIsError);
        Assert.Contains("'stripes' (#1)", binding.GraphAssignmentText);
        Assert.Contains(binding.Macros, m => m.Name == SgShip.Macro && m.Model.Value == "1");
        Assert.True(editor.IsDirty);
        Assert.Equal("", binding.Macros.Single(m => m.Name == SgShip.Macro).StatusLabel);     // not "would crash"
        // the normal Save's bytes carry it
        Assert.Equal("1", MaterialDocument.Parse(editor.Serialize()!, Names(shader)).Materials.Single().Macros[SgShip.Macro]);

        // Ctrl+Z removes it again - absent, not "0"
        Assert.True(undo.CanUndo);
        undo.Undo();
        Assert.False(binding.HasGraphAssigned);
        Assert.DoesNotContain(binding.Macros, m => m.Name == SgShip.Macro);
        Assert.False(MaterialDocument.Parse(editor.Serialize()!, Names(shader)).Materials.Single().Macros.ContainsKey(SgShip.Macro));
        undo.Redo();
        Assert.True(binding.HasGraphAssigned);

        binding.UnassignGraphCommand.Execute(null);
        Assert.False(binding.HasGraphAssigned);
        Assert.False(MaterialDocument.Parse(editor.Serialize()!, Names(shader)).Materials.Single().Macros.ContainsKey(SgShip.Macro));
        undo.Undo();                                                                           // the unassign is one step too
        Assert.Equal("1", binding.Model.Macros[SgShip.Macro]);

        // the tick box cannot overwrite the number
        var row = binding.Macros.Single(m => m.Name == SgShip.Macro);
        row.IsOn = false;
        Assert.Equal("1", binding.Model.Macros[SgShip.Macro]);
    }

    [Fact]
    public void AnAssignedGraphWhoseFileIsMissingShowsAsAnError()
    {
        const string shader = "Shaders/StaticMesh/DefaultEnv_Flat";
        Save(ValidGraph("Stripes"), "stripes");
        var editor = new MaterialEditorViewModel { UndoService = new UndoRedoService(), ProjectRoot = () => _root };
        editor.Load(MaterialDocument.Parse(OneMaterial(shader), Names(shader)), new WadAssetEntry { Path = "x.materials.bin" });
        var binding = editor.Materials.Single();
        binding.AssignGraphCommand.Execute(binding.GraphChoices.Single());
        File.Delete(ShaderGraphRegistry.GraphPath(_root, "stripes"));
        binding.RefreshGraphSection();
        Assert.True(binding.GraphAssignmentIsError);
        Assert.Contains("missing", binding.GraphAssignmentText);
        Assert.Contains("blocked", binding.GraphAssignmentText);
    }

    [Fact]
    public void AGraphIsOnlyOfferedToAMaterialOnItsBaseShader()
    {
        Save(ValidGraph("Stripes"), "stripes");
        const string other = "Shaders/StaticMesh/DefaultEnv_Flat_AlphaTest";
        var editor = new MaterialEditorViewModel { UndoService = new UndoRedoService(), ProjectRoot = () => _root };
        editor.Load(MaterialDocument.Parse(OneMaterial(other), Names(other)), new WadAssetEntry { Path = "x.materials.bin" });
        var binding = editor.Materials.Single();
        Assert.False(binding.HasGraphSection);      // not a supported base: no section at all
        Assert.Empty(binding.GraphChoices);
    }

    // ================================================================================= packing

    [Fact]
    public void PackingCarriesTheGeneratedShaderCacheEntriesAndNeverTheReyengineFolder()
    {
        string dir = Path.Combine(_root, "staged");
        string toc = "assets/shaders/generated/shaders/staticmesh/defaultenv_flat.ps-dx11";
        void Put(string rel, byte[] data)
        {
            string p = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, data);
        }
        Put(toc, new byte[] { 1, 2, 3 });
        Put(toc + "_200", new byte[] { 4, 5, 6 });
        Put(".reyengine/shadergraphs/stripes.shadergraph.json", new byte[] { 7 });
        Put(".reyengine/shadergraphs/numbers.json", new byte[] { 8 });
        Put("readme.txt", new byte[] { 9 });

        Assert.DoesNotContain(WadPackService.EnumerateChunkFiles(dir), f => f.path.Contains(".reyengine"));
        var report = WadPackService.Pack(dir, Path.Combine(_root, "out.wad.client"), knownTypesOnly: true);
        Assert.True(report.Success);
        Assert.Equal(2, report.Chunks);
        Assert.Contains(toc, report.PackedPaths);
        Assert.Contains(toc + "_200", report.PackedPaths);
        Assert.DoesNotContain(report.PackedPaths, p => p.Contains(".reyengine"));
        Assert.Contains("readme.txt", report.CleanedUnknown);
    }

    // ================================================================================= review fixes

    [Fact]
    public void BuildPackageRefusesAnOverrideThatNamesAGraph()
    {
        string src = Path.Combine(_root, "src.wad.client"); File.WriteAllBytes(src, new byte[] { 1 });
        string ov = Path.Combine(_root, "ov.bin"); File.WriteAllBytes(ov, System.Text.Encoding.ASCII.GetBytes("xxREY_GRAPHxx"));
        var p = new ReyEngine.Core.Projects.ReyProject { SourceWadPath = src };
        p.Overrides.Add(new ReyEngine.Core.Projects.ProjectAssetOverride { PathHash = 5, OverrideFile = ov });
        var report = BuildPackageService.Build(p, Path.Combine(_root, "out.wad.client"));
        Assert.Contains(report.Issues, i => i.Severity == BuildSeverity.Error && i.Message.Contains("folder projects"));
        Assert.False(File.Exists(Path.Combine(_root, "out.wad.client")));
    }

    [Fact]
    public void AProjectWadReplacementThatNamesAGraphBlocksTheExport()
    {
        string folder = Path.Combine(_root, "w"); Directory.CreateDirectory(Path.Combine(folder, "data"));
        File.WriteAllBytes(Path.Combine(folder, "data", "a.bin"), new byte[] { 1, 2, 3 });
        string wad = Path.Combine(_root, "Map11.wad.client");
        Assert.True(WadPackService.Pack(folder, wad).Success);
        string ov = Path.Combine(_root, "a.bin"); File.WriteAllBytes(ov, System.Text.Encoding.ASCII.GetBytes("REY_GRAPH"));
        var p = new ReyEngine.Core.Projects.ReyProject { Name = "w", RootPath = _root, OutputDirectory = Path.Combine(_root, "build"), ProjectWads = { wad } };
        var vm = new MainWindowViewModel { Project = p };
        var overrides = (ReyEngine.Core.Projects.AssetOverrideStore)typeof(MainWindowViewModel).GetField("_overrides", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(vm)!;
        overrides.Set(new ReyEngine.Core.Projects.ProjectAssetOverride { PathHash = HashAlgorithms.WadPath("data/a.bin"), OverrideFile = ov });
        Directory.CreateDirectory(p.OutputDirectory!);
        var method = typeof(MainWindowViewModel).GetMethod("BuildProjectCore", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var ex = Assert.Throws<System.Reflection.TargetInvocationException>(() => method.Invoke(vm, new object?[] { p.OutputDirectory, null, false }));
        Assert.Contains("folder projects", ex.InnerException!.Message);
    }

    [Fact]
    public void AssignSaveUnassignSaveAssignKeepsTheMacroAfterTheEmptyMapWasStripped()
    {
        const string shader = "Shaders/StaticMesh/DefaultEnv_Flat";
        Save(ValidGraph("Stripes"), "stripes");
        var editor = new MaterialEditorViewModel { UndoService = new UndoRedoService(), ProjectRoot = () => _root };
        editor.Load(MaterialDocument.Parse(OneMaterial(shader), Names(shader)), new WadAssetEntry { Path = "x.materials.bin" });
        var b = editor.Materials.Single();
        string? Saved() => MaterialDocument.Parse(editor.Serialize()!, Names(shader)).Materials.Single().Macros.GetValueOrDefault(SgShip.Macro);
        b.AssignGraphCommand.Execute(b.GraphChoices.Single());
        Assert.Equal("1", Saved());
        b.UnassignGraphCommand.Execute(null);
        Assert.Null(Saved());                       // the empty map is stripped from the tree here
        b.AssignGraphCommand.Execute(b.GraphChoices.Single());
        Assert.Equal("1", Saved());
    }

    [Fact]
    public void NumbersAreNeverReusedAndAMissingRegistryBlocks()
    {
        const string flat = "Shaders/StaticMesh/DefaultEnv_Flat";
        Assert.Equal(1, ShaderGraphRegistry.NumberFor(_root, "a", flat));
        Assert.Equal(2, ShaderGraphRegistry.NumberFor(_root, "b", flat));
        Assert.Equal(1, ShaderGraphRegistry.NumberFor(_root, "a", "Shaders/StaticMesh/Other"));   // a moves away
        Assert.Equal(3, ShaderGraphRegistry.NumberFor(_root, "c", flat));                          // 1 on the flat base is NOT handed out again
        Assert.Equal(4, ShaderGraphRegistry.NumberFor(_root, "a", flat));                          // and coming back gets a fresh number
        File.Delete(ShaderGraphRegistry.RegistryPath(_root));
        var r = SgShipValidator.Resolve(_root, flat, 1);
        Assert.Null(r.Document);
        Assert.Contains("registry is missing", r.Problem);
    }

    [Fact]
    public void ASamplerTheBaseDoesNotDeclareBlocksTheExport()
    {
        int n = Save(ValidGraph(), "stripes");
        var b = ShaderGraphTests.TestBase();
        var ok = new SgAssignedMaterial("b", "Mat", "Shaders/StaticMesh/DefaultEnv_Flat", n.ToString(), new[] { "TintColor" }, new[] { "DiffuseTexture" });
        Assert.Empty(SgShipValidator.Validate(ok, SgShipValidator.Resolve(_root, ok.Shader!, n), b));
        var bad = ok with { SamplerNames = new[] { "DiffuseTexture", "MysteryTexture" } };
        var err = Assert.Single(SgShipValidator.Validate(bad, SgShipValidator.Resolve(_root, bad.Shader!, n), b));
        Assert.Contains("MysteryTexture", err); Assert.Contains("unverified in game; blocked to be safe", err);
    }

    [Fact]
    public void ABinThatMentionsAGraphButHoldsNoRecognisedMaterialBlocksTheExport()
    {
        string staged = Path.Combine(_root, "staged"); Directory.CreateDirectory(staged);
        var tree = new BinTree(new[] { new BinTreeObject(H("o"), H("Thing"), new BinTreeProperty[] { new BinTreeString(H("note"), "REY_GRAPH") }) }, Array.Empty<string>());
        using (var ms = new MemoryStream()) { tree.Write(ms); File.WriteAllBytes(Path.Combine(staged, "x.bin"), ms.ToArray()); }
        var ex = Assert.Throws<InvalidOperationException>(() => ReyEngine.App.Services.ShaderGraphShipService.Prepare(_root, null, new[] { staged }, _ => null, null));
        Assert.Contains("mentions REY_GRAPH but no material was recognised", ex.Message);
    }
}
