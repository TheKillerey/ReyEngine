using System.IO.Compression;
using System.Reflection;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Shaders;
using Xunit.Abstractions;
using ReyBuild = ReyEngine.Core.Build;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M824: the Character window's BODY RECOLOUR against Riot's own champion files, through the real view models - the CHROMA scan the
/// card makes, the sliders, the live push, Apply &amp; Save, a project reload, an export and a revert - in a scratch folder project
/// under the temp folder. Riot's files, the user's projects and the settings are never written.
///
/// <para>No-ops (not failures) when the game install or the hash dictionary is absent, the convention of
/// <see cref="SkinColorInventoryRealDataTests"/>.</para>
/// </summary>
public sealed class ChromaRecolourRealDataTests : IDisposable
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private const string Game = @"C:\Riot Games\League of Legends\Game";
    private const string Final = Game + @"\DATA\FINAL";

    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "reyengine-m824-" + Guid.NewGuid().ToString("N"));

    public ChromaRecolourRealDataTests(ITestOutputHelper output) { _output = output; Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static bool HaveGame(string champion) => File.Exists(Path.Combine(Final, "Champions", champion + ".wad.client"));

    private static object? Call(MainWindowViewModel vm, string name, params object?[] args) =>
        typeof(MainWindowViewModel).GetMethod(name, Private)!.Invoke(vm, args);

    /// <summary>A folder project on disk with the game folder set, and a view model on it - the editor after "Open Project".</summary>
    private (MainWindowViewModel Vm, ReyProject Project) OpenScratchProject(string name = "scratch")
    {
        string root = Path.Combine(_dir, name);
        Directory.CreateDirectory(root);
        var project = ReyProjectService.OpenFolder(root);   // creates .reyengine/project.json
        project.GameDirectory = Game;
        project.ProjectVersion = 2;
        ReyProjectService.Save(project, project.ProjectFilePath!);
        return (Attach(project), project);
    }

    private static MainWindowViewModel Attach(ReyProject project)
    {
        var vm = new MainWindowViewModel { Project = project };
        vm.Settings.AutoSaveEdits = false;   // the machine's setting must not start a DispatcherTimer in a test
        Call(vm, "BuildMounts");
        return vm;
    }

    private static void OpenChampion(MainWindowViewModel vm, string champion, string skinBin)
    {
        Assert.True((bool)Call(vm, "MakeCharacterWadReadable", Path.Combine(Final, "Champions", champion + ".wad.client"))!);
        vm.MeshPreview.SetChromaSkin(skinBin);
    }

    private static async Task ScanAsync(MainWindowViewModel vm)
    {
        await vm.MeshPreview.ScanColoursCommand.ExecuteAsync(null);
        await Idle(vm);
    }

    private static Task Idle(MainWindowViewModel vm) => vm.MeshPreview.ChromaIdleAsync();

    private static void RunInline(MeshPreviewViewModel card) => card.ChromaUiPost = a => { a(); return Task.CompletedTask; };

    private static byte[] Original(MainWindowViewModel vm, ChromaTextureRowViewModel row) =>
        vm.MeshPreview.ReadChromaOriginal!(row.Target)!;

    private static double MeanAbsError(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        double sum = 0; long n = 0;
        for (int i = 0; i < a.Length; i += 4)
            for (int c = 0; c < 3; c++) { sum += Math.Abs(a[i + c] - b[i + c]); n++; }
        return n == 0 ? 0 : sum / n;
    }

    private string ProjectFile(ReyProject project, string folder, string assetPath) =>
        Path.Combine(project.RootPath!, folder, assetPath.Replace('/', Path.DirectorySeparatorChar));

    // ================================================================================================ Lillia 49 (Rose Quartz over 46)

    [Fact]
    public async Task LilliaRoseQuartz_RecolourSavesReloadsExportsAndReverts()
    {
        if (!HaveGame("Lillia")) return;
        var (vm, project) = OpenScratchProject();
        const string bin = "data/characters/lillia/skins/skin49.bin";
        OpenChampion(vm, "Lillia", bin);
        var card = vm.MeshPreview;
        RunInline(card);

        // ---- the card's own scan lists the body textures
        await ScanAsync(vm);
        Assert.True(card.HasChromaTextures, "the scan listed no body texture");
        var rows = card.ChromaTextures.ToList();
        _output.WriteLine($"{rows.Count} row(s): " + string.Join(", ", rows.Select(r => $"{r.Name}[{(r.IsIncluded ? "on" : "off")}{(r.CanInclude ? "" : ", " + r.Note)}]")));
        var included = rows.Where(r => r.IsIncluded && r.CanInclude).ToList();
        Assert.NotEmpty(included);
        Assert.All(included, r => Assert.False(r.IsOutside));

        // ---- the sliders: nothing is pending until one moves
        Assert.False(card.ChromaRecolourDirty);
        var pushed = new List<ChromaPushItem>();
        card.UseDx11Preview = false;                       // the GL route: the push is the host's, so the test can see it
        card.PushChromaGl = items => { pushed.Clear(); pushed.AddRange(items); };
        card.ChromaHue = 120;
        Assert.True(card.ChromaRecolourDirty);
        await Idle(vm);

        // the live push is the transform of the ORIGINAL texels, exactly
        var transform = card.ChromaSettings.ToTransform();
        Assert.False(transform.IsIdentity);
        Assert.Equal(included.Count, pushed.Count);
        foreach (var item in pushed)
        {
            var row = included.Single(r => r.Hash == item.Hash);
            var original = TextureDecoder.Decode(Original(vm, row));
            Assert.Equal(original.Width, item.Width);
            Assert.Equal(transform.Apply(original).Rgba, item.Rgba);
        }
        // and dragging back and forth never compounds
        card.ChromaHue = 40; await Idle(vm);
        card.ChromaHue = 120; await Idle(vm);
        foreach (var item in pushed)
        {
            var row = included.Single(r => r.Hash == item.Hash);
            Assert.Equal(transform.Apply(TextureDecoder.Decode(Original(vm, row))).Rgba, item.Rgba);
        }

        // ---- Apply & Save: through the real host
        Assert.True(card.ApplyChromaRecolourCommand.CanExecute(null));
        await card.SaveChromaRecolourNowAsync();
        Assert.False(card.ChromaRecolourDirty);
        Assert.True(card.HasSavedChromaRecolour);
        _output.WriteLine(card.ChromaRecolourStatus);

        var records = project.TextureRecolors.Where(r => r.Transform is not null).ToList();
        Assert.NotEmpty(records);
        Assert.All(records, r => Assert.Equal(bin, r.ChromaSkin));
        var written = new Dictionary<ulong, string>();
        foreach (var record in records)
        {
            string file = ProjectFile(project, "Lillia", record.AssetPath);
            Assert.True(File.Exists(file), $"{record.AssetPath} was not written under the champion's WAD folder: {file}");
            written[record.PathHash] = file;
            // same container, same format, same mip flag as Riot's original
            var row = rows.Single(r => r.Hash == record.PathHash);
            var original = Original(vm, row);
            var recoloured = File.ReadAllBytes(file);
            Assert.Equal(TexWriter.DetectFormat(original), TexWriter.DetectFormat(recoloured));
            Assert.Equal(TextureRecolor.HasMips(original), TextureRecolor.HasMips(recoloured));
            // and the texels are the transform's, up to one generation of BC loss
            var expected = transform.Apply(TextureDecoder.Decode(original));
            var actual = TextureDecoder.Decode(recoloured);
            double mae = MeanAbsError(expected.Rgba, actual.Rgba);
            _output.WriteLine($"{record.AssetPath}: {actual.Width}x{actual.Height}, mean abs error vs the transform {mae:0.00}/255");
            Assert.True(mae < 6.0, $"{record.AssetPath} is not the transform of its original (mean abs error {mae:0.00})");
        }
        Assert.Contains("Lillia", project.ProjectFolders);
        Assert.DoesNotContain("Overrides", project.ProjectFolders);
        Assert.Equal(records.Count, (project.TextureRecolors.Count));

        // a second save of a different transform stays in the one folder and re-derives from the original
        card.ChromaHue = 200;
        await card.SaveChromaRecolourNowAsync();
        Assert.DoesNotContain("Overrides", project.ProjectFolders);
        foreach (var (hash, file) in written)
        {
            var row = rows.Single(r => r.Hash == hash);
            var expected = card.ChromaSettings.ToTransform().Apply(TextureDecoder.Decode(Original(vm, row)));
            Assert.True(MeanAbsError(expected.Rgba, TextureDecoder.Decode(File.ReadAllBytes(file)).Rgba) < 6.0, "the second save compounded on the first");
        }
        var savedTransform = card.ChromaSettings.ToTransform();

        // ---- the project file on disk holds the recipe
        string json = File.ReadAllText(project.ProjectFilePath!);
        Assert.Contains("\"ChromaSkin\"", json);
        Assert.Contains("\"HueShiftDegrees\": 200", json);

        // ---- a reload: a new editor on the saved project file
        var reloaded = ReyProjectService.OpenFolder(project.RootPath!);
        var vm2 = Attach(reloaded);
        OpenChampion(vm2, "Lillia", bin);
        var card2 = vm2.MeshPreview;
        RunInline(card2);
        Assert.True(card2.HasSavedChromaRecolour);
        Assert.Equal(savedTransform, card2.ChromaSettings.ToTransform());
        Assert.False(card2.ChromaRecolourDirty);
        await ScanAsync(vm2);
        Assert.False(card2.ChromaRecolourDirty, "a restored recipe must not look pending");
        Assert.Equal(written.Keys.OrderBy(h => h), card2.ChromaTextures.Where(r => r.IsIncluded).Select(r => r.Hash).OrderBy(h => h));
        // the project serves the recoloured file, the original stays what Riot ships
        foreach (var (hash, file) in written)
        {
            Assert.Equal(File.ReadAllBytes(file), (byte[])Call(vm2, "ReadAsset", hash)!);
            var row = card2.ChromaTextures.Single(r => r.Hash == hash);
            Assert.NotEqual(File.ReadAllBytes(file), vm2.MeshPreview.ReadChromaOriginal!(row.Target));
        }

        // ---- export: the champion's WAD in the .fantome carries the recoloured .tex, byte for byte
        string fantome = Path.Combine(_dir, "lillia.fantome");
        Directory.CreateDirectory(Path.Combine(project.RootPath!, "Build"));
        reloaded.OutputDirectory = Path.Combine(project.RootPath!, "Build");
        var export = typeof(MainWindowViewModel).GetMethod("ExportFantomeCore", Private)!;
        export.Invoke(vm2, new object?[] { fantome, new ReyBuild.FantomeMeta { Name = "t", Author = "t", Version = "1.0.0", Description = "" }, null,
            reloaded.OutputDirectory, new NoProgress(), "Export", "nothing", "zip" });
        using (var zip = ZipFile.OpenRead(fantome))
        {
            var entry = zip.GetEntry("WAD/Lillia.wad.client") ?? throw new InvalidOperationException("no Lillia WAD in the package: " + string.Join(", ", zip.Entries.Select(e => e.FullName)));
            string wadPath = Path.Combine(_dir, "exported.wad.client");
            using (var s = entry.Open()) using (var f = File.Create(wadPath)) s.CopyTo(f);
            using var wad = WadArchive.Open(wadPath);
            foreach (var (hash, file) in written)
            {
                Assert.True(wad.TryGetEntry(hash, out var chunk), "the exported champion WAD lacks a recoloured texture");
                Assert.Equal(File.ReadAllBytes(file), wad.Extract(chunk));
            }
        }

        // ---- revert: the files and the records go, the sliders reset
        Assert.True(card2.RevertChromaRecolourCommand.CanExecute(null));
        await card2.RevertChromaRecolourCommand.ExecuteAsync(null);
        Assert.False(card2.HasSavedChromaRecolour);
        Assert.Empty(reloaded.TextureRecolors.Where(r => r.Transform is not null));
        foreach (var file in written.Values) Assert.False(File.Exists(file), "revert left " + file);
        Assert.True(card2.ChromaSettings.ToTransform().IsIdentity);
    }

    // ================================================================================================ more champions: scan, recolour, save, revert

    [Theory]
    [InlineData("Ahri", 0)]
    [InlineData("Jinx", 0)]
    [InlineData("Aatrox", 0)]
    public async Task AnotherChampionSavesRecolouredTexturesIntoItsOwnWadFolderAndRevertsThem(string champion, int skinNumber)
    {
        if (!HaveGame(champion)) return;
        var (vm, project) = OpenScratchProject("more-" + champion);
        string bin = $"data/characters/{champion.ToLowerInvariant()}/skins/skin{skinNumber}.bin";
        OpenChampion(vm, champion, bin);
        var card = vm.MeshPreview;
        RunInline(card);
        await ScanAsync(vm);
        Assert.True(card.HasChromaTextures, $"{champion}: the scan listed no body texture");
        var included = card.ChromaTextures.Where(r => r.IsIncluded && r.CanInclude).ToList();
        _output.WriteLine($"{champion} skin {skinNumber}: {card.ChromaTextures.Count} listed, {included.Count} included; "
            + string.Join(", ", card.ChromaTextures.Where(r => !r.CanInclude).Select(r => $"{r.Name} ({r.Note})")));
        Assert.NotEmpty(included);

        card.ChromaHue = 150;
        card.ChromaSaturation = 1.2;
        await card.SaveChromaRecolourNowAsync();
        var transform = card.ChromaSettings.ToTransform();

        _output.WriteLine("folders: " + string.Join(", ", project.ProjectFolders) + "; files: " + string.Join(", ", Directory.EnumerateFiles(project.RootPath!, "*.tex", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(project.RootPath!, f))));
        {
            var mounts = (AssetMountService)typeof(MainWindowViewModel).GetField("_mounts", Private)!.GetValue(vm)!;
            foreach (var r in project.TextureRecolors.Where(x => x.Transform is not null))
            {
                var holders = mounts.Fallback.Where(f => f.Contains(r.PathHash)).ToList();
                if (holders.Count > 1)
                    _output.WriteLine($"{r.AssetPath} is held by {holders.Count} Riot WADs: " + string.Join(", ", holders.Select(h => Path.GetFileName(h.Location) + (h.Read(r.PathHash).AsSpan().SequenceEqual(holders[0].Read(r.PathHash)) ? "" : " (DIFFERENT BYTES)"))));
            }
        }
        var records = project.TextureRecolors.Where(r => r.Transform is not null).ToList();
        Assert.NotEmpty(records);
        foreach (var record in records)
        {
            var row = card.ChromaTextures.Single(r => r.Hash == record.PathHash);
            string file = record.AssetPath.StartsWith("0x", StringComparison.Ordinal)
                ? Path.Combine(project.RootPath!, champion, $"{record.PathHash:x16}.tex")
                : ProjectFile(project, champion, record.AssetPath);
            Assert.True(File.Exists(file), $"{record.AssetPath} is not under the {champion} WAD folder ({file})");
            var original = Original(vm, row);
            var recoloured = File.ReadAllBytes(file);
            Assert.Equal(TexWriter.DetectFormat(original), TexWriter.DetectFormat(recoloured));
            var expected = transform.Apply(TextureDecoder.Decode(original));
            double mae = MeanAbsError(expected.Rgba, TextureDecoder.Decode(recoloured).Rgba);
            Assert.True(mae < 8.0, $"{record.AssetPath}: mean abs error {mae:0.00}");
        }
        Assert.Contains(champion, project.ProjectFolders);
        Assert.DoesNotContain("Overrides", project.ProjectFolders);
        _output.WriteLine($"{champion}: {records.Count} texture(s) written, {card.ChromaRecolourStatus}");

        await card.RevertChromaRecolourCommand.ExecuteAsync(null);
        Assert.Empty(project.TextureRecolors);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(project.RootPath!, champion), "*.tex", SearchOption.AllDirectories));
    }

    // ================================================================================================ the real D3D11 renderer

    /// <summary>The recolour on Riot's own shaders: Lillia's Rose Quartz chroma drawn by the D3D11 character scene, its textures
    /// recoloured through the card's live push (the view model's pool-key mapping and the window's <c>UpdatePooledTexture</c> +
    /// <c>RegeneratePooledMips</c> calls), and drawn again. The frames are written as PNGs when
    /// <c>REYENGINE_M824_SHOTS</c> names a folder.</summary>
    [Theory]
    [InlineData("Lillia", 49)]
    [InlineData("Ahri", 0)]
    public async Task TheHueShiftReachesTheRealD3D11Frame(string champion, int skinNumber)
    {
        if (!HaveGame(champion)) return;
        var database = new HashSyncService().LoadLocal(_ => { });
        if (database.IsEmpty) return;
        var resolver = new WadPathResolver(database);
        using var cache = ShaderCacheReader.Open(Final, resolver, out _);
        if (cache is null) return;
        using var renderer = new ReyEngine.Rendering.D3D11.ShaderPreviewRenderer();
        if (!renderer.Initialize(out _)) return;   // no D3D11 here

        string binPath = $"data/characters/{champion.ToLowerInvariant()}/skins/skin{skinNumber}.bin";
        var (vm, project) = OpenScratchProject("device");
        OpenChampion(vm, champion, binPath);
        using var archive = WadArchive.Open(Path.Combine(Final, "Champions", champion + ".wad.client"), resolver);
        byte[]? Read(ulong h) { try { return archive.TryGetEntry(h, out _) ? archive.Extract(h) : null; } catch { return null; } }
        byte[] bin = Read(HashAlgorithms.WadPath(binPath))!;
        var doc = MaterialDocument.Parse(bin,
            h => database.TryGetBinName(h, out var n) ? n : null, h => database.TryGetPath(h, out var p) ? p : null);
        byte[] skn = Read(BinTexturePath.HashOfReference(doc.SkinMesh!.SimpleSkin!))!;
        ShaderPermutationIndex? perms = null;
        try { perms = new ShaderPermutationIndex(Final, h => database.TryGetPath(h, out var p) ? p : null); } catch { }
        var scene = Dx11CharacterScene.Prepare(skn, bin, cache, perms, readAsset: Read,
            resolveBinName: h => database.TryGetBinName(h, out var n) ? n : null,
            resolveWadPath: h => database.TryGetPath(h, out var p) ? p : null,
            fallbackShader: Dx11CharacterScene.DefaultCharacterShader);
        Assert.NotNull(scene);
        Assert.NotEmpty(scene!.Slices);
        Dx11CharacterScene.Commit(renderer, scene, "");

        var mesh = scene.Mesh;
        var lo = new System.Numerics.Vector3(float.MaxValue); var hi = new System.Numerics.Vector3(float.MinValue);
        foreach (var v in mesh.Vertices) { lo = System.Numerics.Vector3.Min(lo, v.Position); hi = System.Numerics.Vector3.Max(hi, v.Position); }
        var centre = (lo + hi) * 0.5f;
        float radius = MathF.Max(1f, (hi - lo).Length() * 0.5f);
        var eye = centre + new System.Numerics.Vector3(radius * 0.6f, radius * 0.10f, radius * 1.25f);
        var settings = new ReyEngine.Rendering.D3D11.PreviewSettings
        {
            SuppliedView = System.Numerics.Matrix4x4.CreateLookAt(eye, centre, System.Numerics.Vector3.UnitY),
            SuppliedProjection = System.Numerics.Matrix4x4.CreatePerspectiveFieldOfView(0.9f, 1f, radius * 0.02f, radius * 40f),
            SuppliedCameraPosition = eye,
            AlphaBlend = true, DepthTest = true, MirrorX = true, TransposeMatrices = true,
            CullBackFaces = true, SortByPipeline = true, TimeSeconds = 0f,
            ClearColor = new System.Numerics.Vector4(0.039f, 0.051f, 0.075f, 1f),
        };
        const int Size = 512;
        byte[] before = renderer.RenderFrame(Size, Size, settings, out var err1)?.ToArray() ?? throw new InvalidOperationException("no frame: " + err1);

        // ---- the card: the real scan, the real sliders, the window's own push into this renderer
        var card = vm.MeshPreview;
        RunInline(card);
        card.UseDx11Preview = true;
        card.SetDx11Scene(scene, "");
        int updated = 0;
        card.PushChromaDx11 = items =>
        {
            var touched = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (key, rgba, width, height) in items)
                if (renderer.UpdatePooledTexture(key, rgba, width, height)) { touched.Add(key); updated++; }
            foreach (string key in touched) renderer.RegeneratePooledMips(key);
        };
        await ScanAsync(vm);
        card.ChromaHue = 120;
        await Idle(vm);
        Assert.True(updated > 0, "no pooled texture was updated: the view model found no D3D11 key for any included texture");
        _output.WriteLine($"{updated} pooled texture(s) updated");

        byte[] after = renderer.RenderFrame(Size, Size, settings, out var err2)?.ToArray() ?? throw new InvalidOperationException("no frame: " + err2);

        // ---- the picture changed where the character is, and by hue
        long covered = 0, changed = 0; double dr = 0, dg = 0, db = 0, br = 0, bg = 0, bb = 0;
        for (int i = 0; i + 3 < before.Length; i += 4)
        {
            bool body = Math.Abs(before[i] - 19) > 12 || Math.Abs(before[i + 1] - 13) > 12 || Math.Abs(before[i + 2] - 10) > 12;
            if (!body) continue;
            covered++;
            if (Math.Abs(before[i] - after[i]) + Math.Abs(before[i + 1] - after[i + 1]) + Math.Abs(before[i + 2] - after[i + 2]) > 12) changed++;
            br += before[i + 2]; bg += before[i + 1]; bb += before[i];
            dr += after[i + 2]; dg += after[i + 1]; db += after[i];
        }
        _output.WriteLine($"covered {covered} px, changed {changed} px; mean RGB before ({br / covered:0},{bg / covered:0},{bb / covered:0}) after ({dr / covered:0},{dg / covered:0},{db / covered:0})");
        Assert.True(covered > 2000, "the character did not draw");
        Assert.True(changed > covered / 10, $"only {changed} of {covered} character pixels changed under a +120 degree hue shift");

        // the pool entry is only written at its own size: texels for another size are refused instead of read past the end of the buffer
        {
            var (key, pooled) = scene.Textures.First(kv => kv.Value.Width >= 64 && kv.Value.Height >= 64 && renderer.IsCached(kv.Key));
            Assert.False(renderer.UpdatePooledTexture(key, new byte[(pooled.Width / 2) * (pooled.Height / 2) * 4], pooled.Width / 2, pooled.Height / 2),
                "a half-size image was written into a full-size pool texture");
            Assert.False(renderer.UpdatePooledTexture(key, new byte[pooled.Width * pooled.Height * 4], pooled.Width * 2, pooled.Height * 2));
            Assert.True(renderer.UpdatePooledTexture(key, pooled.Rgba, pooled.Width, pooled.Height));
        }

        // how long one slider position takes from the slider to the renderer (Debug build, every included texture of the skin)
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int hue = 130; hue < 135; hue++) { card.ChromaHue = hue; await Idle(vm); }
        _output.WriteLine($"one update of {card.ChromaTextures.Count(r => r.IsIncluded)} texture(s): {clock.ElapsedMilliseconds / 5.0:0} ms (Debug, push and mip regeneration included)");

        // dragging back to no change draws the original again
        card.ChromaHue = 0;
        await Idle(vm);
        byte[] reset = renderer.RenderFrame(Size, Size, settings, out _)!.ToArray();
        long residual = 0;
        for (int i = 0; i < before.Length; i++) residual += Math.Abs(before[i] - reset[i]);
        _output.WriteLine($"after resetting the hue: mean abs difference to the original frame {residual / (double)before.Length:0.000}/255");
        Assert.True(residual / (double)before.Length < 0.5, "resetting the slider did not restore the original frame");

        // ---- saved, the project reloaded, the scene built again from the PROJECT's files: the frame still shows the recolour
        card.ChromaHue = 120;
        await Idle(vm);
        byte[] live = renderer.RenderFrame(Size, Size, settings, out _)!.ToArray();
        await card.SaveChromaRecolourNowAsync();
        var reloadedProject = ReyProjectService.OpenFolder(project.RootPath!);
        var vm2 = Attach(reloadedProject);
        OpenChampion(vm2, champion, binPath);
        byte[]? ReadFromProject(ulong h) { try { return (byte[])Call(vm2, "ReadAsset", h)!; } catch { return null; } }
        var scene2 = Dx11CharacterScene.Prepare(skn, ReadFromProject(HashAlgorithms.WadPath(binPath))!, cache, perms, readAsset: ReadFromProject,
            resolveBinName: h => database.TryGetBinName(h, out var n) ? n : null,
            resolveWadPath: h => database.TryGetPath(h, out var p) ? p : null,
            fallbackShader: Dx11CharacterScene.DefaultCharacterShader);
        Assert.NotNull(scene2);
        Dx11CharacterScene.Commit(renderer, scene2!, "");   // clears the pool: every texture is made again from what the project serves
        byte[] fromProject = renderer.RenderFrame(Size, Size, settings, out _)!.ToArray();
        long vsLive = 0, vsOriginal = 0;
        for (int i = 0; i < live.Length; i++) { vsLive += Math.Abs(live[i] - fromProject[i]); vsOriginal += Math.Abs(before[i] - fromProject[i]); }
        _output.WriteLine($"project reloaded and the scene rebuilt from its files: mean abs difference to the live preview {vsLive / (double)live.Length:0.00}, to the original {vsOriginal / (double)live.Length:0.00} (/255)");
        Assert.True(vsLive / (double)live.Length < 3.0, "the frame built from the saved project does not match the live preview");
        Assert.True(vsOriginal / (double)live.Length > 1.0, "the frame built from the saved project still shows the original colours");

        if (System.Environment.GetEnvironmentVariable("REYENGINE_M824_SHOTS") is { Length: > 0 } shots)
        {
            Directory.CreateDirectory(shots);
            WritePng(Path.Combine(shots, $"{champion.ToLowerInvariant()}{skinNumber}-d3d11-before.png"), before, Size, Size);
            WritePng(Path.Combine(shots, $"{champion.ToLowerInvariant()}{skinNumber}-d3d11-hue120.png"), after, Size, Size);
            WritePng(Path.Combine(shots, $"{champion.ToLowerInvariant()}{skinNumber}-d3d11-reset.png"), reset, Size, Size);
            WritePng(Path.Combine(shots, $"{champion.ToLowerInvariant()}{skinNumber}-d3d11-reloaded.png"), fromProject, Size, Size);
            _output.WriteLine("shots in " + shots);
        }
    }

    /// <summary>A minimal PNG writer for a BGRA8 frame (no image library in the test project).</summary>
    internal static void WritePng(string path, byte[] bgra, int width, int height)
    {
        var raw = new byte[(width * 4 + 1) * height];
        for (int y = 0; y < height; y++)
        {
            int o = y * (width * 4 + 1);
            raw[o] = 0;
            for (int x = 0; x < width; x++)
            {
                int s = (y * width + x) * 4, d = o + 1 + x * 4;
                raw[d] = bgra[s + 2]; raw[d + 1] = bgra[s + 1]; raw[d + 2] = bgra[s]; raw[d + 3] = 255;
            }
        }
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw);
        using var file = File.Create(path);
        file.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        void Chunk(string type, byte[] data)
        {
            var len = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
            file.Write(len);
            var body = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            file.Write(body);
            uint crc = 0xFFFFFFFF;
            foreach (byte b in body)
            {
                crc ^= b;
                for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
            var c = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(c, ~crc);
            file.Write(c);
        }
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0), (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8; ihdr[9] = 6;
        Chunk("IHDR", ihdr);
        Chunk("IDAT", ms.ToArray());
        Chunk("IEND", Array.Empty<byte>());
    }

    // ================================================================================================ the GL viewport's images

    /// <summary>The OpenGL half: the Character window's GL images (its own decode of each submesh's diffuse) are changed in place and
    /// queued for upload, through the host's real chunk-to-image mapping. A GL context cannot be had headlessly, so this stops at the
    /// control's <c>QueueTextureUpdate</c> - the paint tool's own entry, whose draining is not changed.</summary>
    [Fact]
    public async Task LilliaRoseQuartz_TheGlImagesAreRecolouredInPlaceAndQueuedForUpload()
    {
        if (!HaveGame("Lillia")) return;
        var (vm, project) = OpenScratchProject("gl");
        const string bin = "data/characters/lillia/skins/skin49.bin";
        OpenChampion(vm, "Lillia", bin);
        var card = vm.MeshPreview;
        RunInline(card);

        // what the host loads for the window: the mesh and each submesh's diffuse
        ulong sknHash = BinTexturePath.HashOfReference("ASSETS/Characters/Lillia/Skins/Skin46/Lillia_Skin46.skn");
        object?[] tryResolve = { sknHash, null };
        Assert.True((bool)typeof(MainWindowViewModel).GetMethod("TryResolveEntry", Private)!.Invoke(vm, tryResolve)!);
        var entry = tryResolve[1]!;
        typeof(MainWindowViewModel).GetField("_previewSkn", Private)!.SetValue(vm, entry);
        typeof(MainWindowViewModel).GetField("_previewSkinBin", Private)!.SetValue(vm, bin);
        var mesh = ReyEngine.Formats.Meshes.SkinnedMeshDecoder.Decode((byte[])Call(vm, "ReadAsset", sknHash)!);
        var loaded = Call(vm, "TryLoadPreviewDiffuse", entry, mesh, bin)!;
        var images = (IReadOnlyList<TextureImage?>?)loaded.GetType().GetField("Item1")!.GetValue(loaded);
        Assert.NotNull(images);
        Assert.Contains(images!, i => i is not null);
        card.Show("Lillia skin 49", mesh, null, images);
        card.SetChromaSkin(bin);
        card.UseDx11Preview = false;
        var copies = images!.Where(i => i is not null).Distinct().Select(i => (Image: i!, Before: (byte[])i!.Rgba.Clone())).ToList();

        var queued = new List<TextureImage>();
        card.QueueGlTextureUpdate = (image, rect) =>
        {
            Assert.Equal(0, rect.X); Assert.Equal(image.Width, rect.Width); Assert.Equal(image.Height, rect.Height);
            queued.Add(image);
        };
        int mipRebuilds = 0;
        card.RebuildGlTextureMips = () => mipRebuilds++;

        await ScanAsync(vm);
        card.ChromaHue = 120;
        await Idle(vm);

        Assert.NotEmpty(queued);
        Assert.True(mipRebuilds > 0);
        var transform = card.ChromaSettings.ToTransform();
        foreach (var image in queued.Distinct())
        {
            var (_, before) = copies.Single(c => ReferenceEquals(c.Image, image));
            // the project holds no recolour yet, so the GL image began as Riot's original: it is now the transform of it
            Assert.Equal(transform.Apply(new TextureImage(image.Width, image.Height, before)).Rgba, image.Rgba);
        }
        _output.WriteLine($"{queued.Distinct().Count()} GL image(s) recoloured in place and queued");

        // and back: the original texels return
        card.ChromaHue = 0;
        queued.Clear();
        await Idle(vm);
        Assert.NotEmpty(queued);
        foreach (var image in queued.Distinct())
            Assert.Equal(copies.Single(c => ReferenceEquals(c.Image, image)).Before, image.Rgba);
    }

    private sealed class NoProgress : IProgress<(double Frac, string Stage)>
    {
        public void Report((double Frac, string Stage) value) { }
    }
}
