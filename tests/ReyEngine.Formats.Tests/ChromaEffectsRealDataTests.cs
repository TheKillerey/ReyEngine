using System.IO.Compression;
using System.Numerics;
using System.Reflection;
using LeagueToolkit.Core.Meta;
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
using ReyEngine.Formats.Particles;
using ReyEngine.Formats.Vfx;
using Xunit.Abstractions;
using ReyBuild = ReyEngine.Core.Build;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M826: the Character window's EFFECTS RECOLOUR against Riot's own champion files, through the real view models - the CHROMA scan the card makes, the sliders, Apply &amp; Save,
/// Ctrl+S, a project reload, an export, a build and a revert - in a scratch folder project under the temp folder. Riot's files, the user's projects and the settings are never written.
///
/// <para>No-ops (not failures) when the game install or the hash dictionary is absent, the convention of <see cref="ChromaParametersRealDataTests"/>.</para>
/// </summary>
public sealed class ChromaEffectsRealDataTests : IDisposable
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private const string Game = @"C:\Riot Games\League of Legends\Game";
    private const string Final = Game + @"\DATA\FINAL";
    private const string Lillia49 = "data/characters/lillia/skins/skin49.bin";

    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "reyengine-m826-" + Guid.NewGuid().ToString("N"));

    public ChromaEffectsRealDataTests(ITestOutputHelper output) { _output = output; Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static bool HaveGame(string champion) => File.Exists(Path.Combine(Final, "Champions", champion + ".wad.client"));

    private static object? Call(MainWindowViewModel vm, string name, params object?[] args)
    {
        try { return typeof(MainWindowViewModel).GetMethod(name, Private)!.Invoke(vm, args); }
        catch (TargetInvocationException e) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
    }

    private (MainWindowViewModel Vm, ReyProject Project) OpenScratchProject(string name = "scratch")
    {
        string root = Path.Combine(_dir, name);
        Directory.CreateDirectory(root);
        var project = ReyProjectService.OpenFolder(root);
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
        await vm.MeshPreview.ChromaIdleAsync();
    }

    private static void RunInline(MeshPreviewViewModel card) => card.ChromaUiPost = a => { a(); return Task.CompletedTask; };

    private static byte[] ReadAsset(MainWindowViewModel vm, ulong hash) => (byte[])Call(vm, "ReadAsset", hash)!;

    private static readonly Lazy<HashDatabase?> Database = new(() => { try { return new HashSyncService().LoadLocal(_ => { }); } catch { return null; } });
    private static string? Name(uint h) => Database.Value is { } db && db.TryGetBinName(h, out var n) ? n : null;

    private static byte[] RiotBin(string champion, string path)
    {
        using var archive = WadArchive.Open(Path.Combine(Final, "Champions", champion + ".wad.client"), new WadPathResolver(Database.Value!));
        return archive.Extract(HashAlgorithms.WadPath(path));
    }

    private string ProjectFile(ReyProject project, string folder, string wadPath) =>
        Path.Combine(project.RootPath!, folder, wadPath.Replace('/', Path.DirectorySeparatorChar));

    private static (MainWindowViewModel Vm, MeshPreviewViewModel Card) Reopen(ReyProject project, string champion, string bin)
    {
        var reloaded = ReyProjectService.OpenFolder(project.RootPath!);
        var vm = Attach(reloaded);
        OpenChampion(vm, champion, bin);
        RunInline(vm.MeshPreview);
        vm.MeshPreview.UseDx11Preview = false;
        return (vm, vm.MeshPreview);
    }

    /// <summary>Every colour field of a bin, keyed.</summary>
    private static Dictionary<EffectColorKey, EffectColorField> Fields(byte[] bin) =>
        SkinEffectColors.Read(bin, Name, includeExcluded: false).Fields.GroupBy(f => f.Key).ToDictionary(g => g.Key, g => g.First());

    // ================================================================================================ Lillia 49 (Rose Quartz over 46)

    [Fact]
    public async Task LilliaRoseQuartz_Scan_ListsTheEffectsColoursAndTextures_AndLeavesMasksOut()
    {
        if (!HaveGame("Lillia") || Database.Value is null) return;
        var (vm, project) = OpenScratchProject();
        OpenChampion(vm, "Lillia", Lillia49);
        var card = vm.MeshPreview;
        RunInline(card);
        card.UseDx11Preview = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await ScanAsync(vm);
        _output.WriteLine($"scan + effects listed in {sw.ElapsedMilliseconds} ms");

        Assert.True(card.HasChromaEffects, card.ChromaEffectProblem);
        _output.WriteLine(card.ChromaEffectSummary);
        _output.WriteLine($"{card.ChromaEffectSystems.Count} system row(s); {card.ChromaEffectTextures.Count} texture row(s); problem: '{card.ChromaEffectProblem}'");
        foreach (var line in card.ChromaEffectExcluded) _output.WriteLine($"excluded: {line.Title} - {line.Detail}");
        foreach (var s in card.ChromaEffectSystems.Take(6)) _output.WriteLine($"{s.Name}  {s.Detail}  shared={s.IsShared}");
        foreach (var t in card.ChromaEffectTextures.Take(8)) _output.WriteLine($"{t.Name}  {t.Detail}  on={t.IsIncluded} can={t.CanInclude} {t.Note}");
        _output.WriteLine($"{card.ChromaEffectTextures.Count(r => r.Note.Contains("also read as a data map", StringComparison.Ordinal))} of {card.ChromaEffectTextures.Count} colour texture(s) are also read as a data map");
        Assert.NotEmpty(card.ChromaEffectSystems);
        Assert.NotEmpty(card.ChromaEffectExcluded);
        Assert.False(card.ChromaRecolourDirty);
    }

    /// <summary>The bin as Riot has it, with the owned fields put back: a bin that differs from Riot's in nothing else is the same data.</summary>
    private static void AssertSameExceptColors(byte[] riot, byte[] project, IReadOnlyCollection<EffectColorKey> owned)
    {
        var tree = SafeBinTree.Parse(project);
        SkinEffectColors.Apply(tree, SafeBinTree.Parse(riot), ColorTransform.Identity, Array.Empty<EffectColorKey>(), owned);
        string? difference = BinTreeEquivalence.FirstDifference(SafeBinTree.Parse(riot), tree);
        Assert.True(difference is null, "the bin differs from Riot's beyond the recoloured colours: " + difference);
    }

    private static EffectColorKey KeyOf(ChromaEffectColorRef r) => new(r.System, r.Emitter, r.Field);

    /// <summary>Switch the body's textures and colour parameters off, so a save touches the effects only.</summary>
    private static void BodyOff(MeshPreviewViewModel card)
    {
        foreach (var row in card.ChromaTextures) row.IsIncluded = false;
        foreach (var row in card.ChromaParameters) row.IsIncluded = false;
    }

    /// <summary>Effects start off; this is the person pressing All colours and All textures.</summary>
    private static async Task EffectsOn(MeshPreviewViewModel card)
    {
        card.SelectAllEffectColorsCommand.Execute(null);
        card.SelectAllEffectTexturesCommand.Execute(null);
        await card.ChromaIdleAsync();
    }

    [Fact]
    public async Task LilliaRoseQuartz_EffectColoursSaveFromRiotsValue_EveryKeyExact_MasksUntouched_NeverCompounding()
    {
        if (!HaveGame("Lillia") || Database.Value is null) return;
        var (vm, project) = OpenScratchProject();
        OpenChampion(vm, "Lillia", Lillia49);
        var card = vm.MeshPreview;
        RunInline(card);
        card.UseDx11Preview = false;
        await ScanAsync(vm);
        Assert.True(card.HasChromaEffects, card.ChromaEffectProblem);
        Assert.False(card.ChromaRecolourDirty);
        card.ChromaHue = 90;
        Assert.False(card.ChromaEffectSystems.Any(s => s.IsIncluded) || card.ChromaEffectTextures.Any(r => r.IsIncluded), "the effects start switched off");
        card.ChromaHue = 0;
        BodyOff(card);   // this test is about the effects: the body's textures and parameters stay Riot's
        await EffectsOn(card);

        // the sliders, then Apply & Save through the real host
        card.ChromaHue = 120;
        card.ChromaSaturation = 1.3;
        card.ChromaBrightness = 1.2;
        var transform = card.ChromaSettings.ToTransform();
        Assert.True(card.ApplyChromaRecolourCommand.CanExecute(null));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await card.SaveChromaRecolourNowAsync();
        _output.WriteLine($"saved in {sw.ElapsedMilliseconds} ms: {card.ChromaRecolourStatus}");
        Assert.False(card.ChromaRecolourDirty, card.ChromaRecolourStatus);
        Assert.True(card.HasSavedChromaRecolour);

        var record = Assert.Single(project.ChromaEffectRecolors!);
        Assert.Equal(Lillia49, record.ChromaSkin);
        Assert.NotEmpty(record.Colors);
        _output.WriteLine($"{record.Colors.Count} colour field(s) owned in {record.Systems.Count} system(s); {record.Colors.Select(c => c.Bin).Distinct().Count()} bin(s); placed: {string.Join(", ", record.PlacedBins?.Select(p => p.File) ?? Array.Empty<string>())}");
        string json = File.ReadAllText(project.ProjectFilePath!);
        _output.WriteLine($"project.json is {json.Length / 1024.0:F0} KB");
        Assert.Contains("\"ChromaEffectRecolors\"", json);
        Assert.DoesNotContain("constantValue", json);

        // every bin that holds an owned field: every colour key is Riot's key through the transform (the carrier rule decides which form), everything else is Riot's
        var owned = record.Colors.Select(KeyOf).ToHashSet();
        int constants = 0, keys = 0, moved = 0;
        foreach (var bin in record.Colors.Select(c => c.Bin).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ulong hash = HashAlgorithms.WadPath(bin);
            byte[] riot = RiotBin("Lillia", bin);
            byte[] mine = ReadAsset(vm, hash);
            Assert.NotEqual(riot, mine);
            var riotFields = Fields(riot);
            var mineFields = Fields(mine);
            var binKeys = record.Colors.Where(c => string.Equals(c.Bin, bin, StringComparison.OrdinalIgnoreCase)).Select(KeyOf).ToHashSet();
            var carriers = SkinEffectColors.Carriers(binKeys.Select(k => riotFields[k]).Where(f => f.Recolourable));
            foreach (var key in binKeys)
            {
                var from = riotFields[key];
                var tf = SkinEffectColors.TransformFor(key, transform, carriers);
                var now = mineFields[key];
                Assert.Equal(from.Keys.Count, now.Keys.Count);
                Assert.Equal(from.Constant.HasValue, now.Constant.HasValue);       // a constant the bin did not write is not written
                float tolerance = from.StoredAsBytes ? 1f / 255f : 1e-6f;
                void Same(Vector4 original, Vector4 got, string what)
                {
                    var expected = tf.Apply(original);
                    if (from.StoredAsBytes) expected = new Vector4(Math.Clamp(expected.X, 0, 1), Math.Clamp(expected.Y, 0, 1), Math.Clamp(expected.Z, 0, 1), expected.W);
                    Assert.True(Math.Abs(expected.X - got.X) <= tolerance && Math.Abs(expected.Y - got.Y) <= tolerance && Math.Abs(expected.Z - got.Z) <= tolerance,
                        $"{from.SystemName}/{from.EmitterName}.{key.Field} {what}: expected {expected}, the bin holds {got}");
                    Assert.Equal(original.W, got.W);                                  // alpha is never changed
                    if (!SkinEffectColors.SameRgb(original, got)) moved++;
                }
                if (from.Constant is { } c) { Same(c, now.Constant!.Value, "constant"); constants++; }
                for (int i = 0; i < from.Keys.Count; i++) { Same(from.Keys[i], now.Keys[i], $"key {i}"); keys++; }
            }
            // masks, data maps and everything not recoloured: Riot's, object for object
            AssertSameExceptColors(riot, mine, binKeys);
            // every field this save did NOT own is exactly Riot's
            foreach (var (key, field) in riotFields.Where(kv => !binKeys.Contains(kv.Key)))
                Assert.True(field.Values.SequenceEqual(mineFields[key].Values), $"{field.SystemName}.{key.Field} changed though it is not owned");
        }
        _output.WriteLine($"{constants} constant(s) and {keys} key(s) checked, {moved} value(s) moved");
        Assert.True(moved > 200, "the recolour moved hardly any value");

        // a second save of another transform is Riot's value through THAT transform: nothing compounds
        card.ChromaHue = 200;
        card.ChromaSaturation = 0.8;
        card.ChromaBrightness = 1;
        await card.SaveChromaRecolourNowAsync();
        var second = card.ChromaSettings.ToTransform();
        foreach (var bin in record.Colors.Select(c => c.Bin).Distinct(StringComparer.OrdinalIgnoreCase).Take(3))
        {
            var riotFields = Fields(RiotBin("Lillia", bin));
            var mineFields = Fields(ReadAsset(vm, HashAlgorithms.WadPath(bin)));
            var binKeys = project.ChromaEffectRecolors!.Single().Colors.Where(c => string.Equals(c.Bin, bin, StringComparison.OrdinalIgnoreCase)).Select(KeyOf).ToHashSet();
            var carriers = SkinEffectColors.Carriers(binKeys.Select(k => riotFields[k]).Where(f => f.Recolourable));
            foreach (var key in binKeys.Take(400))
            {
                var tf = SkinEffectColors.TransformFor(key, second, carriers);
                var from = riotFields[key];
                float tolerance = from.StoredAsBytes ? 1f / 255f : 1e-6f;
                foreach (var (original, got) in from.Values.Zip(mineFields[key].Values))
                {
                    var expected = tf.Apply(original);
                    if (from.StoredAsBytes) expected = new Vector4(Math.Clamp(expected.X, 0, 1), Math.Clamp(expected.Y, 0, 1), Math.Clamp(expected.Z, 0, 1), expected.W);
                    Assert.True(Math.Abs(expected.X - got.X) <= tolerance && Math.Abs(expected.Y - got.Y) <= tolerance && Math.Abs(expected.Z - got.Z) <= tolerance, "compounded: " + from.SystemName + "." + key.Field);
                }
            }
        }
    }

    private static double MeanAbsError(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        double sum = 0; long n = 0;
        for (int i = 0; i < a.Length; i += 4)
            for (int c = 0; c < 3; c++) { sum += Math.Abs(a[i + c] - b[i + c]); n++; }
        return n == 0 ? 0 : sum / n;
    }

    private sealed class NoProgress : IProgress<(double Frac, string Stage)>
    {
        public void Report((double Frac, string Stage) value) { }
    }

    private static byte[] OriginalTexture(MeshPreviewViewModel card, ChromaEffectTextureRowViewModel row) => card.ReadChromaOriginal!(row.Target)!;

    [Fact]
    public async Task ABinWhoseNameNoFileCanHave_IsPlacedUnderItsHash_ShipsInTheBuild_AndTheRevertTakesItOut()
    {
        // Ahri's Multi_Skins bin is 430 characters long: the 255 a file name may hold are far behind it, so it cannot be copied into the champion's WAD folder under its name
        if (!HaveGame("Ahri") || Database.Value is null) return;
        const string bin = "data/characters/ahri/skins/skin0.bin";
        var (vm, project) = OpenScratchProject("long");
        OpenChampion(vm, "Ahri", bin);
        var card = vm.MeshPreview;
        RunInline(card);
        card.UseDx11Preview = false;
        await ScanAsync(vm);
        BodyOff(card);
        Assert.True(card.HasChromaEffects, card.ChromaEffectProblem);
        var longName = card.ChromaEffectSystems.Where(s => s.Info.Bin.Length > 255).ToList();
        Assert.NotEmpty(longName);
        _output.WriteLine($"{longName.Count} system(s) live in a bin named by {longName[0].Info.Bin.Length} characters");
        foreach (var system in longName.Take(3)) system.IsIncluded = true;
        card.ChromaHue = 100;
        await card.SaveChromaRecolourNowAsync();                                  // used to throw "the file name is invalid" and keep the whole save pending
        Assert.False(card.ChromaRecolourDirty, card.ChromaRecolourStatus);
        var record = Assert.Single(project.ChromaEffectRecolors!);
        string longBin = longName[0].Info.Bin;
        ulong hash = HashAlgorithms.WadPath(longBin);
        Assert.Contains(record.Colors, c => string.Equals(c.Bin, longBin, StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(ProjectFile(project, "Ahri", longBin)));          // no file by that name
        string loose = Path.Combine(project.RootPath!, "Ahri", $"{hash:x16}.bin");
        Assert.True(File.Exists(loose), "the bin was not placed under its hash in the champion's WAD folder");
        Assert.Equal(File.ReadAllBytes(loose), ReadAsset(vm, hash));                // the project serves it
        Assert.NotEqual(RiotBin("Ahri", longBin), ReadAsset(vm, hash));            // and it is the recolour
        Assert.Contains(record.PlacedBins!, p => p.File == $"Ahri/{hash:x16}.bin");

        // the build packs it, under the chunk the game reads
        project.OutputDirectory = Path.Combine(project.RootPath!, "Build");
        await (Task)Call(vm, "BuildProject")!;
        var wads = Directory.EnumerateFiles(project.OutputDirectory, "*.wad.client", SearchOption.AllDirectories).ToList();
        bool packed = false;
        foreach (var wadFile in wads)
        {
            using var wad = WadArchive.Open(wadFile, new WadPathResolver(Database.Value!));
            if (wad.TryGetEntry(hash, out var chunk)) { packed = wad.Extract(chunk).AsSpan().SequenceEqual(File.ReadAllBytes(loose)); break; }
        }
        Assert.True(packed, "the built package does not carry the recoloured bin: " + string.Join(", ", wads.Select(Path.GetFileName)));

        // revert: Riot's bin is served again and the copy the recolour placed is gone
        await card.RevertChromaRecolourCommand.ExecuteAsync(null);
        Assert.Null(project.ChromaEffectRecolors);
        Assert.False(File.Exists(loose), "the revert left the copy of Riot's own bin in the project");
        Assert.Equal(RiotBin("Ahri", longBin), ReadAsset(vm, hash));
    }

    [Theory]
    [InlineData("Ahri", 0)]
    [InlineData("Aatrox", 0)]
    [InlineData("Yone", 0)]
    [InlineData("Lux", 7)]
    public async Task OtherChampionsListTheirEffects_AndWhatItCostsIsStated(string champion, int skin)
    {
        if (!HaveGame(champion) || Database.Value is null) return;
        string bin = $"data/characters/{champion.ToLowerInvariant()}/skins/skin{skin}.bin";
        var (vm, project) = OpenScratchProject("cost-" + champion);
        OpenChampion(vm, champion, bin);
        var card = vm.MeshPreview;
        RunInline(card);
        card.UseDx11Preview = false;
        GC.Collect();
        long memory = GC.GetTotalMemory(true);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await ScanAsync(vm);
        long scanned = clock.ElapsedMilliseconds;
        Assert.True(card.HasChromaEffects, card.ChromaEffectProblem);
        int dataMaps = card.ChromaEffectTextures.Count(r => r.Note.Contains("also read as a data map", StringComparison.Ordinal));
        _output.WriteLine($"{champion} skin {skin}: {dataMaps} of {card.ChromaEffectTextures.Count} colour texture(s) are ALSO read as a mask or data map by a system of the skin (listed as unchangeable)");
        long after = GC.GetTotalMemory(false);
        _output.WriteLine($"{champion} skin {skin}: scan + effect lists {scanned} ms; {card.ChromaEffectSystems.Count} system row(s), {card.ChromaEffectTextures.Count} texture row(s), "
                          + $"{card.ChromaEffectSystems.Sum(s => s.FieldInfos.Count)} colour field(s), {card.ChromaEffectSystems.Select(s => s.Info.Bin).Distinct().Count()} bin(s); managed memory {(after - memory) / 1048576.0:F0} MB more than before the scan");
        BodyOff(card);
        await EffectsOn(card);
        card.ChromaHue = 80;
        clock.Restart();
        await card.SaveChromaRecolourNowAsync();
        _output.WriteLine($"  Apply & Save: {clock.ElapsedMilliseconds} ms, {card.ChromaRecolourStatus}");
        Assert.False(card.ChromaRecolourDirty, card.ChromaRecolourStatus);
        var record = Assert.Single(project.ChromaEffectRecolors!);
        _output.WriteLine($"  {record.Colors.Count} field(s) owned in {record.Systems.Count} system(s); project.json {new FileInfo(project.ProjectFilePath!).Length / 1024} KB");
        await card.RevertChromaRecolourCommand.ExecuteAsync(null);
        Assert.Null(project.ChromaEffectRecolors);
    }

    [Fact]
    public async Task AnOwnedFieldTheParticleEditorChangedSinceIsKeptNotOverwritten_UntilItIsSwitchedOnAgain()
    {
        if (!HaveGame("Lillia") || Database.Value is null) return;
        var (vm, project) = OpenScratchProject("owned-edit");
        OpenChampion(vm, "Lillia", Lillia49);
        var card = vm.MeshPreview;
        RunInline(card);
        card.UseDx11Preview = false;
        await ScanAsync(vm);
        BodyOff(card);
        var system = card.ChromaEffectSystems.First(s => s.Info.Bin.Contains("multi_skins", StringComparison.OrdinalIgnoreCase));
        system.IsIncluded = true;
        string bin = system.Info.Bin;
        ulong hash = HashAlgorithms.WadPath(bin);
        card.ChromaHue = 120;
        await card.SaveChromaRecolourNowAsync();
        var transform = card.ChromaSettings.ToTransform();
        var owned = project.ChromaEffectRecolors!.Single().Colors.Select(KeyOf).Where(k => k.System == system.Info.Hash).ToList();
        var riotFields = Fields(RiotBin("Lillia", bin));
        var pick = owned.First(k => riotFields[k].Recolourable && riotFields[k].Chroma > 0.3f);

        // somebody changes ONE owned colour in the project's bin (the Particle Editor's own writer)
        string file = Directory.EnumerateFiles(project.RootPath!, "*.bin", SearchOption.AllDirectories).First(f => File.ReadAllBytes(f).Length == ReadAsset(vm, hash).Length);
        var tree = SafeBinTree.Parse(File.ReadAllBytes(file));
        SkinEffectColors.Apply(tree, SafeBinTree.Parse(RiotBin("Lillia", bin)), new ColorTransform { HueShiftDegrees = 33f }, new[] { pick }, Array.Empty<EffectColorKey>());
        File.WriteAllBytes(file, SkinEffectColors.Serialize(tree));
        var theirs = Fields(File.ReadAllBytes(file))[pick].Values.ToList();

        var (vm2, card2) = Reopen(project, "Lillia", Lillia49);
        await ScanAsync(vm2);
        var system2 = card2.ChromaEffectSystems.Single(s => s.Info.Hash == system.Info.Hash);
        system2.IsExpanded = true;
        var row = system2.Fields.Single(f => f.Key == pick);
        Assert.True(row.IsEditedOutside, "the row does not know the bin's value is neither Riot's nor the recolour's");
        Assert.False(row.IsIncluded);
        Assert.Contains("Particle Editor", row.Note);
        Assert.False(card2.ChromaRecolourDirty, "a kept edit made the card look pending");

        // a new transform: every other owned field follows it, this one stays as it was edited
        var other = owned.First(k => k != pick && riotFields[k].Recolourable && riotFields[k].Chroma > 0.3f);
        var otherBefore = Fields(ReadAsset(vm2, hash))[other].Values.ToList();
        card2.ChromaHue = 200;
        await card2.SaveChromaRecolourNowAsync();
        var now = Fields(ReadAsset(vm2, hash));
        Assert.Equal(theirs, now[pick].Values.ToList());
        Assert.NotEqual(otherBefore, now[other].Values.ToList());   // it did move with the new transform

        // switched on again, it is Riot's value through the transform once more (that replaces the edit)
        row.IsIncluded = true;
        await card2.SaveChromaRecolourNowAsync();
        var carriers = SkinEffectColors.Carriers(card2.ChromaEffectSystems.SelectMany(s => s.FieldInfos).Where(f => f.Key.System == pick.System && f.Recolourable).Select(f => riotFields[f.Key]));
        var expected = SkinEffectColors.TransformFor(pick, card2.ChromaSettings.ToTransform(), carriers).Apply(riotFields[pick].Values.First());
        Assert.True(SkinEffectColors.SameRgb(expected, Fields(ReadAsset(vm2, hash))[pick].Values.First()) || Math.Abs(expected.X - Fields(ReadAsset(vm2, hash))[pick].Values.First().X) < 1e-6f);
    }

    [Fact]
    public async Task TheParticleEditorsUnsavedEditsAreSavedFirst_AndItsDocumentIsReadAgainFromTheRecolouredBin()
    {
        if (!HaveGame("Lillia") || Database.Value is null) return;
        var (vm, project) = OpenScratchProject("particle-editor");
        OpenChampion(vm, "Lillia", Lillia49);
        var card = vm.MeshPreview;
        RunInline(card);
        card.UseDx11Preview = false;
        await ScanAsync(vm);
        BodyOff(card);
        var system = card.ChromaEffectSystems.First(s => s.Info.Bin.Contains("multi_skins", StringComparison.OrdinalIgnoreCase));
        system.IsIncluded = true;
        string bin = system.Info.Bin;
        ulong hash = HashAlgorithms.WadPath(bin);
        card.ChromaHue = 60;
        await card.SaveChromaRecolourNowAsync();                                  // the bin is now the project's own copy: the Particle Editor can hold it for editing
        Assert.False(card.ChromaRecolourDirty, card.ChromaRecolourStatus);

        var entryArgs = new object?[] { hash, null };
        Assert.True((bool)Call(vm, "TryResolveEntry", entryArgs)!);
        var entry = (WadAssetEntry)entryArgs[1]!;
        await (Task)Call(vm, "OpenParticleEditorForAsync", entry, false)!;
        var editor = vm.ParticleEditor;
        var document = editor.Document ?? throw new InvalidOperationException("the Particle Editor did not open the bin");
        Assert.True(editor.IsEditable);
        var node = editor.Systems.First(s => s.Entry.PathHash == system.Info.Hash);
        editor.SelectedSystem = node;

        // an edit in the editor that the project does not have yet: a scalar of the system's first emitter
        var emitter = node.Entry.Emitters[0];
        var scalar = emitter.Properties.First(p => !p.IsReadOnly && p.TypeName.StartsWith("F32", StringComparison.Ordinal) && float.TryParse(p.CurrentText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _));
        float before = float.Parse(scalar.CurrentText, System.Globalization.CultureInfo.InvariantCulture);
        string edited = (before + 7f).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        scalar.Apply(edited);
        Assert.True(document.IsDirty);
        _output.WriteLine($"Particle Editor edit: {node.Name} / {emitter.Name} . {scalar.Name} {before} -> {edited}");

        // a new transform: the save settles the editor first, writes the recolour on top, and reads the editor's document again
        card.ChromaHue = 140;
        await card.SaveChromaRecolourNowAsync();
        Assert.False(card.ChromaRecolourDirty, card.ChromaRecolourStatus);
        byte[] served = ReadAsset(vm, hash);
        var servedDoc = ParticleDocument.Parse(served)!;
        var servedProp = servedDoc.Systems.First(s => s.PathHash == system.Info.Hash).Emitters[0].Properties.First(p => p.Name == scalar.Name && p.Depth == scalar.Depth);
        Assert.Equal(edited, servedProp.CurrentText);                              // the editor's edit is in the bin: not overwritten by the recolour
        var fields = Fields(served);
        var riotFields = Fields(RiotBin("Lillia", bin));
        var transform = card.ChromaSettings.ToTransform();
        var ownedKeys = project.ChromaEffectRecolors!.Single().Colors.Select(KeyOf).Where(k => k.System == system.Info.Hash).ToList();
        var carriers = SkinEffectColors.Carriers(ownedKeys.Select(k => riotFields[k]).Where(f => f.Recolourable));
        foreach (var key in ownedKeys.Take(20))
        {
            var tf = SkinEffectColors.TransformFor(key, transform, carriers);
            foreach (var (original, got) in riotFields[key].Values.Zip(fields[key].Values))
            {
                var expected = tf.Apply(original);
                Assert.True(Math.Abs(expected.X - got.X) <= 1e-6f && Math.Abs(expected.Y - got.Y) <= 1e-6f && Math.Abs(expected.Z - got.Z) <= 1e-6f, "the recolour is not the new transform's: " + key);
            }
        }
        // the editor shows the recoloured bin and its next save starts from it
        Assert.NotSame(document, editor.Document);
        Assert.False(editor.Document!.IsDirty);
        Assert.Equal(system.Info.Hash, editor.SelectedSystem?.Entry.PathHash);       // the selected system stays selected
        var shownProp = editor.Document.Systems.First(s => s.PathHash == system.Info.Hash).Emitters[0].Properties.First(p => p.Name == scalar.Name && p.Depth == scalar.Depth);
        Assert.Equal(edited, shownProp.CurrentText);
    }

    [Fact]
    public async Task LilliaRoseQuartz_EffectsSaveCtrlSReloadExportBuildAndRevert()
    {
        if (!HaveGame("Lillia") || Database.Value is null) return;
        var (vm, project) = OpenScratchProject();
        OpenChampion(vm, "Lillia", Lillia49);
        var card = vm.MeshPreview;
        RunInline(card);
        card.UseDx11Preview = false;
        await ScanAsync(vm);
        BodyOff(card);
        await EffectsOn(card);

        // ---- Apply & Save: colours into the bins, textures into the project, the recipe in project.json
        card.ChromaHue = 140;
        await card.SaveChromaRecolourNowAsync();
        Assert.False(card.ChromaRecolourDirty, card.ChromaRecolourStatus);
        var record = Assert.Single(project.ChromaEffectRecolors!);
        var texRecords = project.TextureRecolors.Where(r => r.ChromaPart == TextureRecolorRecord.EffectsPart).ToList();
        Assert.NotEmpty(texRecords);
        Assert.All(texRecords, r => { Assert.Equal(Lillia49, r.ChromaSkin); Assert.NotNull(r.Transform); });
        Assert.Empty(project.TextureRecolors.Where(r => r.ChromaPart is null && r.Transform is not null));   // the body's textures are untouched
        Assert.Null(project.ChromaParameterRecolors);

        // an effect texture is the HUE-ONLY form of the transform (brightness and saturation would count twice with the colour values), one BC generation from Riot's file
        var transform = card.ChromaSettings.ToTransform();
        var textureTransform = SkinEffectColors.TextureTransform(transform);
        var written = new Dictionary<ulong, string>();
        foreach (var rec in texRecords.Take(6))
        {
            Assert.Equal(textureTransform, rec.Transform);
            string file = ProjectFile(project, "Lillia", rec.AssetPath);
            Assert.True(File.Exists(file), $"{rec.AssetPath} was not written under the champion's WAD folder: {file}");
            written[rec.PathHash] = file;
            var row = card.ChromaEffectTextures.Single(r => r.Hash == rec.PathHash);
            var original = OriginalTexture(card, row);
            Assert.Equal(TexWriter.DetectFormat(original), TexWriter.DetectFormat(File.ReadAllBytes(file)));
            double mae = MeanAbsError(textureTransform.Apply(TextureDecoder.Decode(original)).Rgba, TextureDecoder.Decode(File.ReadAllBytes(file)).Rgba);
            Assert.True(mae < 6.0, $"{rec.AssetPath} is not the transform of its original (mean abs error {mae:0.00})");
        }
        // data maps and cubemaps are Riot's: nothing the texture list holds as excluded was written
        Assert.All(card.ChromaEffectTextures.Where(r => !r.CanInclude), r => Assert.DoesNotContain(texRecords, t => t.PathHash == r.Hash));

        // ---- Ctrl+S: a pending effect recolour is part of what the editor flushes
        card.ChromaHue = 200;
        Assert.True(card.HasPendingChromaRecolour);
        await (Task)Call(vm, "SavePendingEditorEdits")!;
        Assert.False(card.ChromaRecolourDirty);
        var saved = card.ChromaSettings.ToTransform();
        var firstTex = written.First();
        Assert.True(MeanAbsError(SkinEffectColors.TextureTransform(saved).Apply(TextureDecoder.Decode(OriginalTexture(card, card.ChromaEffectTextures.Single(r => r.Hash == firstTex.Key)))).Rgba,
            TextureDecoder.Decode(File.ReadAllBytes(firstTex.Value)).Rgba) < 6.0, "the texture compounded on the first save");
        var ownedAfter = project.ChromaEffectRecolors!.Single().Colors.Select(KeyOf).OrderBy(k => k.System).ThenBy(k => k.Emitter).ThenBy(k => k.Field, StringComparer.Ordinal).ToList();
        var bins = project.ChromaEffectRecolors!.Single().Colors.Select(c => c.Bin).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var binBytes = bins.ToDictionary(b => b, b => ReadAsset(vm, HashAlgorithms.WadPath(b)), StringComparer.OrdinalIgnoreCase);

        // ---- a reload: a new editor on the saved project file restores the sliders and the switches, and the card does not look pending
        var (vm2, card2) = Reopen(project, "Lillia", Lillia49);
        Assert.True(card2.HasSavedChromaRecolour);
        Assert.Equal(saved, card2.ChromaSettings.ToTransform());
        Assert.False(card2.ChromaRecolourDirty);
        await ScanAsync(vm2);
        Assert.False(card2.ChromaRecolourDirty, "a restored effects recipe must not look pending: " + card2.ChromaRecolourStatus);
        Assert.True(card2.HasChromaEffects);
        var reloadedRecord = Assert.Single(vm2.Project.ChromaEffectRecolors!);
        Assert.Equal(ownedAfter, reloadedRecord.Colors.Select(KeyOf).OrderBy(k => k.System).ThenBy(k => k.Emitter).ThenBy(k => k.Field, StringComparer.Ordinal).ToList());
        Assert.All(card2.ChromaEffectSystems.Where(s => s.IsIncluded), s => Assert.NotEmpty(s.FieldInfos));
        foreach (var (bin, bytes) in binBytes) Assert.Equal(bytes, ReadAsset(vm2, HashAlgorithms.WadPath(bin)));   // the reloaded project serves the saved bins

        // ---- Export .fantome: the champion's WAD carries the changed bins and textures byte for byte
        var reloaded = vm2.Project;
        string fantome = Path.Combine(_dir, "lillia.fantome");
        Directory.CreateDirectory(Path.Combine(project.RootPath!, "Build"));
        reloaded.OutputDirectory = Path.Combine(project.RootPath!, "Build");
        typeof(MainWindowViewModel).GetMethod("ExportFantomeCore", Private)!.Invoke(vm2, new object?[] { fantome,
            new ReyBuild.FantomeMeta { Name = "t", Author = "t", Version = "1.0.0", Description = "" }, null, reloaded.OutputDirectory, new NoProgress(), "Export", "nothing", "zip" });
        using (var zip = ZipFile.OpenRead(fantome))
        {
            var entry = zip.GetEntry("WAD/Lillia.wad.client") ?? throw new InvalidOperationException("no Lillia WAD in the package: " + string.Join(", ", zip.Entries.Select(e => e.FullName)));
            string wadPath = Path.Combine(_dir, "exported.wad.client");
            using (var s = entry.Open()) using (var f = File.Create(wadPath)) s.CopyTo(f);
            using var wad = WadArchive.Open(wadPath, new WadPathResolver(Database.Value!));
            foreach (var (bin, bytes) in binBytes)
            {
                Assert.True(wad.TryGetEntry(HashAlgorithms.WadPath(bin), out var chunk), "the exported champion WAD lacks " + bin);
                Assert.Equal(bytes, wad.Extract(chunk));
            }
            foreach (var (hash, file) in written)
            {
                Assert.True(wad.TryGetEntry(hash, out var chunk), $"the exported champion WAD lacks the recoloured texture 0x{hash:x16}");
                Assert.Equal(File.ReadAllBytes(file), wad.Extract(chunk));
            }
        }

        // ---- Build Package: a pending recolour is flushed by the build itself
        card2.ChromaBrightness = 0.8;
        Assert.True(card2.HasPendingChromaRecolour);
        reloaded.OutputDirectory = Path.Combine(project.RootPath!, "Build2");
        await (Task)Call(vm2, "BuildProject")!;
        Assert.False(card2.ChromaRecolourDirty, "Build Package did not flush the pending effects recolour");
        var built = Directory.EnumerateFiles(reloaded.OutputDirectory, "Lillia*.wad.client", SearchOption.AllDirectories).FirstOrDefault();
        Assert.NotNull(built);
        var nowTransform = card2.ChromaSettings.ToTransform();
        using (var wad = WadArchive.Open(built!, new WadPathResolver(Database.Value!)))
        {
            foreach (var bin in bins)
            {
                Assert.True(wad.TryGetEntry(HashAlgorithms.WadPath(bin), out var chunk), "the built champion WAD lacks " + bin);
                Assert.Equal(ReadAsset(vm2, HashAlgorithms.WadPath(bin)), wad.Extract(chunk));
            }
        }
        Assert.NotEqual(binBytes.First().Value, ReadAsset(vm2, HashAlgorithms.WadPath(binBytes.First().Key)));   // the flush rewrote the bins with brightness 0.8

        // ---- revert: Riot's bins and textures come back, the records go, the sliders reset
        Assert.True(card2.RevertChromaRecolourCommand.CanExecute(null));
        await card2.RevertChromaRecolourCommand.ExecuteAsync(null);
        Assert.False(card2.HasSavedChromaRecolour, card2.ChromaRecolourStatus);
        Assert.Null(reloaded.ChromaEffectRecolors);
        Assert.DoesNotContain("ChromaEffectRecolors", File.ReadAllText(project.ProjectFilePath!));
        Assert.Empty(reloaded.TextureRecolors.Where(r => r.Transform is not null));
        foreach (var bin in bins)
        {
            string placed = ProjectFile(project, "Lillia", bin);
            Assert.False(File.Exists(placed), "the revert left the copy of " + bin + " the recolour had placed in the project");
            Assert.Equal(RiotBin("Lillia", bin), ReadAsset(vm2, HashAlgorithms.WadPath(bin)));
        }
        foreach (var (hash, file) in written) Assert.False(File.Exists(file), "the revert left a recoloured texture in the project");
        Assert.True(card2.ChromaSettings.ToTransform().IsIdentity);
    }

    // ================================================================================================ review fixes (M826 round 2)

    [Fact]
    public async Task ASaveOfANewTransformJudgesALaterBinsGivenBackFieldsByTheRecipeThatWroteThem_NotByTheTransformAnEarlierBinMovedTo()
    {
        if (!HaveGame("Lillia") || Database.Value is null) return;
        var (vm, project) = OpenScratchProject("multi-bin");
        OpenChampion(vm, "Lillia", Lillia49);
        var card = vm.MeshPreview;
        RunInline(card);
        card.UseDx11Preview = false;
        await ScanAsync(vm);
        BodyOff(card);
        card.SelectAllEffectColorsCommand.Execute(null);
        card.ChromaHue = 120;
        await card.SaveChromaRecolourNowAsync();
        Assert.False(card.ChromaRecolourDirty, card.ChromaRecolourStatus);
        var first = Assert.Single(project.ChromaEffectRecolors!);
        var bins = first.Colors.Select(c => c.Bin).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _output.WriteLine($"{first.Colors.Count} field(s) owned in {bins.Count} bin(s): {string.Join(", ", bins.Select(Path.GetFileName))}");
        if (bins.Count < 2) return;                                       // the check needs a second bin

        // every system of ONE bin is switched off while the transform changes: that bin has only fields to give back, so it is written after the bins that are recoloured
        string off = bins[^1];
        foreach (var s in card.ChromaEffectSystems.Where(s => string.Equals(s.Info.Bin, off, StringComparison.OrdinalIgnoreCase))) s.IsIncluded = false;
        var givenBack = first.Colors.Where(c => string.Equals(c.Bin, off, StringComparison.OrdinalIgnoreCase)).Select(KeyOf).ToList();
        card.ChromaHue = 200;
        await card.SaveChromaRecolourNowAsync();
        Assert.False(card.ChromaRecolourDirty, card.ChromaRecolourStatus);

        var record = Assert.Single(project.ChromaEffectRecolors!);
        Assert.DoesNotContain(record.Colors, c => string.Equals(c.Bin, off, StringComparison.OrdinalIgnoreCase));   // the record gave them up...
        Assert.Null(record.BinTransforms);
        Assert.Equal(card.ChromaSettings.ToTransform(), record.Transform);
        var riotFields = Fields(RiotBin("Lillia", off));
        var mineFields = Fields(ReadAsset(vm, HashAlgorithms.WadPath(off)));
        int stillRecoloured = givenBack.Count(k => riotFields.TryGetValue(k, out var r) && mineFields.TryGetValue(k, out var m) && !r.Values.SequenceEqual(m.Values, SameRgbComparer.Instance));
        _output.WriteLine($"{givenBack.Count} field(s) of {Path.GetFileName(off)} given back; {stillRecoloured} still differ from Riot's");
        Assert.Equal(0, stillRecoloured);                                 // ...and the bin really holds Riot's colours again: a field dropped from the record while still recoloured is the bug

        // the other bins follow the new transform
        string other = bins[0];
        var otherRiot = Fields(RiotBin("Lillia", other));
        var otherNow = Fields(ReadAsset(vm, HashAlgorithms.WadPath(other)));
        var otherKeys = record.Colors.Where(c => string.Equals(c.Bin, other, StringComparison.OrdinalIgnoreCase)).Select(KeyOf).ToHashSet();
        var carriers = SkinEffectColors.Carriers(otherKeys.Select(k => otherRiot[k]).Where(f => f.Recolourable));
        var key = otherKeys.First(k => otherRiot[k].Recolourable && otherRiot[k].Chroma > 0.3f);
        var expected = SkinEffectColors.TransformFor(key, card.ChromaSettings.ToTransform(), carriers).Apply(otherRiot[key].Values.First());
        Assert.True(Math.Abs(expected.X - otherNow[key].Values.First().X) <= 1f / 255f + 1e-6f);
    }

    private sealed class SameRgbComparer : IEqualityComparer<Vector4>
    {
        public static readonly SameRgbComparer Instance = new();
        public bool Equals(Vector4 a, Vector4 b) => SkinEffectColors.SameRgb(a, b);
        public int GetHashCode(Vector4 v) => v.GetHashCode();
    }

    [Fact]
    public void ASaveThatStopsAfterTheFirstBinLeavesTheRecordTellingTheTruthPerBin_AndTheSaveThatCompletesMovesItsTransform()
    {
        var (vm, project) = OpenScratchProject("interrupted");
        const string skin = "data/characters/zz/skins/skin1.bin", a = "data/characters/zz/a.bin", b = "data/characters/zz/b.bin";
        var t0 = new ColorTransform { HueShiftDegrees = 60f };
        var t1 = new ColorTransform { HueShiftDegrees = 120f };
        ChromaEffectColorRef Ref(string bin, int emitter) => new() { System = 1, Emitter = emitter, Field = "birthColor", Bin = bin, SystemName = "S", EmitterName = "E" + emitter };
        project.ChromaEffectRecolors = new List<ChromaEffectRecord> { new() { ChromaSkin = skin, Transform = t0, Colors = new List<ChromaEffectColorRef> { Ref(a, 0), Ref(b, 1) } } };

        // a save at t1 recolours bin a ... and stops before bin b
        var args = new object?[] { skin, t1, a, new List<ChromaEffectColorRef> { Ref(a, 0) }, new HashSet<EffectColorKey> { new(1, 0, "birthColor") }, new HashSet<EffectColorKey>(), null, null };
        Call(vm, "PersistChromaEffectRecord", args);
        var record = Assert.Single(project.ChromaEffectRecolors!);
        Assert.Equal(t0, record.Transform);                               // the record's transform did not move: bin b still holds t0
        Assert.Equal(t1, record.TransformOf(a));                          // and bin a says it was written with t1
        Assert.Equal(t0, record.TransformOf(b));
        Assert.Equal(2, record.Colors.Count);
        Assert.Equal(new[] { "E0", "E1" }, record.Colors.Select(c => c.EmitterName).OrderBy(n => n, StringComparer.Ordinal));

        // the project file carries that per-bin truth
        var reread = ReyProjectService.OpenFolder(project.RootPath!).ChromaEffectRecolors!.Single();
        Assert.Equal(t1, reread.TransformOf(a));
        Assert.Equal(t0, reread.TransformOf(b));

        // the save that completes: every bin is at t1 now
        Call(vm, "SyncChromaEffectRecord", skin, t1, true);
        record = Assert.Single(project.ChromaEffectRecolors!);
        Assert.Equal(t1, record.Transform);
        Assert.Null(record.BinTransforms);
    }

    [Fact]
    public async Task AFieldWhoseEmitterRiotsBinNowCallsSomethingElseIsListedAsUnchangeableAndLeftAsItIs()
    {
        if (!HaveGame("Lillia") || Database.Value is null) return;
        var (vm, project) = OpenScratchProject("renamed");
        OpenChampion(vm, "Lillia", Lillia49);
        var card = vm.MeshPreview;
        RunInline(card);
        card.UseDx11Preview = false;
        await ScanAsync(vm);
        BodyOff(card);
        var system = card.ChromaEffectSystems.First(s => s.Info.Bin.Contains("multi_skins", StringComparison.OrdinalIgnoreCase));
        system.IsIncluded = true;
        card.ChromaHue = 120;
        await card.SaveChromaRecolourNowAsync();
        var record = Assert.Single(project.ChromaEffectRecolors!);
        var sysRef = record.Systems.First(s => s.System == system.Info.Hash && s.EmitterNames is { Count: > 0 });
        int emitter = sysRef.EmitterNames!.Keys.First();
        Assert.True(sysRef.EmitterNames[emitter].Length > 0);                                   // the record kept the emitter's name beside its ordinal
        string bin = sysRef.Bin;
        ulong hash = HashAlgorithms.WadPath(bin);
        sysRef.EmitterNames[emitter] = "SomeOtherEmitter";                                       // as if a patch had inserted an emitter before this one
        ReyProjectService.Save(project, project.ProjectFilePath!);

        var (vm2, card2) = Reopen(project, "Lillia", Lillia49);
        await ScanAsync(vm2);
        var system2 = card2.ChromaEffectSystems.Single(s => s.Info.Hash == system.Info.Hash);
        system2.IsExpanded = true;
        var rows = system2.Fields.Where(f => f.Key.Emitter == emitter && sysRef.Fields.Contains($"{f.Key.Emitter}:{f.Key.Field}")).ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, r => { Assert.False(r.CanInclude); Assert.Contains("changed since the recolour", r.Note); });
        var keys = rows.Select(r => r.Key).ToList();
        var before = Fields(ReadAsset(vm2, hash));

        card2.ChromaHue = 200;
        await card2.SaveChromaRecolourNowAsync();
        var after = Fields(ReadAsset(vm2, hash));
        foreach (var k in keys) Assert.True(before[k].Values.SequenceEqual(after[k].Values), $"{k.Field} of the renamed emitter was written");
        Assert.Contains(vm2.Project.ChromaEffectRecolors!.Single().Colors, c => keys.Contains(KeyOf(c)));   // it stays in the record: still the recolour's, just not touched now
    }
}
