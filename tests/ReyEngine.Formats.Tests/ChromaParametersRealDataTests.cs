using System.IO.Compression;
using System.Numerics;
using System.Reflection;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Shaders;
using Xunit.Abstractions;
using ReyBuild = ReyEngine.Core.Build;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M825: the Character window's colour PARAMETER recolour against Riot's own champion files, through the real view models - the CHROMA scan
/// the card makes, the sliders, Apply &amp; Save, Ctrl+S, a project reload, an export, a build and a revert - in a scratch folder project
/// under the temp folder. Riot's files, the user's projects and the settings are never written.
///
/// <para>No-ops (not failures) when the game install or the hash dictionary is absent, the convention of
/// <see cref="ChromaRecolourRealDataTests"/>.</para>
/// </summary>
public sealed class ChromaParametersRealDataTests : IDisposable
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private const string Game = @"C:\Riot Games\League of Legends\Game";
    private const string Final = Game + @"\DATA\FINAL";

    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "reyengine-m825-" + Guid.NewGuid().ToString("N"));

    public ChromaParametersRealDataTests(ITestOutputHelper output) { _output = output; Directory.CreateDirectory(_dir); }
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
    private static string? Path_(ulong h) => Database.Value is { } db && db.TryGetPath(h, out var p) ? p : null;

    private static Dictionary<SkinColorParamKey, SkinColorParam> Params(byte[] bin) =>
        SkinColorParameters.Read(bin, Name, Path_).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First());

    private static byte[] RiotBin(string champion, string skinBin)
    {
        using var archive = WadArchive.Open(Path.Combine(Final, "Champions", champion + ".wad.client"), new WadPathResolver(Database.Value!));
        return archive.Extract(HashAlgorithms.WadPath(skinBin));
    }

    private string ProjectBin(ReyProject project, string folder, string skinBin) =>
        Path.Combine(project.RootPath!, folder, skinBin.Replace('/', Path.DirectorySeparatorChar));

    private static bool SameVector(Vector4 a, Vector4 b, float tolerance) =>
        Math.Abs(a.X - b.X) <= tolerance && Math.Abs(a.Y - b.Y) <= tolerance && Math.Abs(a.Z - b.Z) <= tolerance;

    // ================================================================================================ Lillia 49 (Rose Quartz over 46)

    [Fact]
    public async Task LilliaRoseQuartz_ParametersSaveFromRiotsValue_CtrlSReloadExportBuildAndRevert()
    {
        if (!HaveGame("Lillia") || Database.Value is null) return;
        const string bin = "data/characters/lillia/skins/skin49.bin";
        var (vm, project) = OpenScratchProject();
        OpenChampion(vm, "Lillia", bin);
        var card = vm.MeshPreview;
        RunInline(card);
        card.UseDx11Preview = false;
        var pushed = new List<ChromaParamPush>();
        card.PushChromaParamsGl = items => { pushed.Clear(); pushed.AddRange(items); };
        byte[] riot = RiotBin("Lillia", bin);
        var riotParams = Params(riot);

        // ---- the card's own scan lists the colour parameters, with the reason for every one it leaves alone
        await ScanAsync(vm);
        Assert.True(card.HasChromaParameters, card.ChromaParameterProblem);
        var rows = card.ChromaParameters.ToList();
        _output.WriteLine($"{rows.Count} parameter row(s): {rows.Count(r => r.CanInclude)} recolourable, {rows.Count(r => !r.CanInclude)} left alone, {rows.Count(r => r.IsIncluded)} on");
        Assert.Contains(rows, r => r.Key.Name == "TintColor" && r.IsIncluded);
        Assert.Contains(rows, r => r.Key.Name == "OutlineColor" && r.IsIncluded);
        Assert.Contains(rows, r => r.Key.Name == "reflectionFresnelColor" && r.Key.Material == 0 && r.IsIncluded);   // the skin block's own colour
        Assert.All(rows.Where(r => r.Key.Name.Contains("Mask", StringComparison.OrdinalIgnoreCase)), r => Assert.False(r.CanInclude));
        var linked = rows.Single(r => r.Key.Name == "Tint_Color");                               // Outline_Vfxleg: defined in the shared Multi_Skins bin
        Assert.False(linked.CanInclude);
        Assert.Contains("linked bin", linked.Note);
        Assert.Equal(rows.Count(r => r.IsIncluded), riotParams.Values.Count(p => p.Recolourable && !p.IsLinked));
        Assert.False(card.ChromaRecolourDirty);

        // ---- the sliders: the live push is Riot's value through the transform
        card.ChromaHue = 120;
        await card.ChromaIdleAsync();
        var transform = card.ChromaSettings.ToTransform();
        Assert.Equal(rows.Count(r => r.IsIncluded && r.CanPreview), pushed.Count);
        Assert.True(rows.Count(r => r.IsIncluded && !r.CanPreview) > 0, "Lillia has driven and skin-block parameters the preview cannot draw");
        foreach (var item in pushed)
        {
            var expected = transform.Apply(riotParams[item.Key].Value);
            Assert.Equal(new[] { expected.X, expected.Y, expected.Z, riotParams[item.Key].Value.W }, item.Value);
        }
        card.ChromaHue = 40; await card.ChromaIdleAsync();       // dragging back and forth never compounds
        card.ChromaHue = 120; await card.ChromaIdleAsync();
        foreach (var item in pushed) Assert.Equal(transform.Apply(riotParams[item.Key].Value).X, item.Value[0]);

        // ---- Apply & Save through the real host
        Assert.True(card.ApplyChromaRecolourCommand.CanExecute(null));
        await card.SaveChromaRecolourNowAsync();
        Assert.False(card.ChromaRecolourDirty);
        Assert.True(card.HasSavedChromaRecolour);
        _output.WriteLine(card.ChromaRecolourStatus);

        string file = ProjectBin(project, "Lillia", bin);
        Assert.True(File.Exists(file), "the skin bin was not placed in the champion's WAD folder: " + file);
        Assert.DoesNotContain("Overrides", project.ProjectFolders);
        var projectParams = Params(File.ReadAllBytes(file));
        int moved = 0, same = 0;
        foreach (var (key, original) in riotParams)
        {
            var now = projectParams[key];
            if (card.ChromaParameters.Single(r => r.Key == key).IsIncluded)
            {
                var expected = transform.Apply(original.Value);
                float tolerance = original.TypeName == "Color" ? 1f / 255f : 1e-6f;   // a Color is stored in bytes
                Assert.True(SameVector(expected, now.Value, tolerance), $"{original.MaterialName}.{key.Name}: expected {expected}, the bin holds {now.Value}");
                Assert.Equal(original.Value.W, now.Value.W);                          // alpha is never changed
                if (!SameVector(original.Value, now.Value, 0f)) moved++;
            }
            else
            {
                Assert.Equal(original.Value, now.Value);                              // everything not owned is Riot's
                same++;
            }
        }
        _output.WriteLine($"{moved} parameter(s) changed, {same} left as Riot's, {projectParams.Count} listed in the project's bin");
        Assert.True(moved > 20, "the recolour moved hardly any parameter");

        // everything else in the bin is Riot's, object for object (the writer may reorder properties; the data is the same)
        AssertSameExceptParameters(riot, File.ReadAllBytes(file), riotParams.Keys.Where(k => card.ChromaParameters.Single(r => r.Key == k).IsIncluded).ToHashSet());

        // the textures of the same recolour were saved beside it (M824, unchanged)
        Assert.NotEmpty(project.TextureRecolors.Where(r => r.Transform is not null));
        var record = Assert.Single(project.ChromaParameterRecolors!);
        Assert.Equal(bin, record.ChromaSkin);
        Assert.Equal(card.ChromaParameters.Count(r => r.IsIncluded), record.Parameters.Count);
        string json = File.ReadAllText(project.ProjectFilePath!);
        Assert.Contains("\"ChromaParameterRecolors\"", json);
        // the recipe holds the transform and the parameters, never the values
        Assert.DoesNotContain("\"Value\"", json[json.IndexOf("\"ChromaParameterRecolors\"", StringComparison.Ordinal)..]);

        // ---- a second save of another transform re-derives from Riot's value: no compounding
        card.ChromaHue = 200;
        card.ChromaSaturation = 0.8;
        await card.SaveChromaRecolourNowAsync();
        var second = card.ChromaSettings.ToTransform();
        projectParams = Params(File.ReadAllBytes(file));
        foreach (var row in card.ChromaParameters.Where(r => r.IsIncluded))
        {
            float tolerance = riotParams[row.Key].TypeName == "Color" ? 1f / 255f : 1e-6f;
            Assert.True(SameVector(second.Apply(riotParams[row.Key].Value), projectParams[row.Key].Value, tolerance), row.Name + " compounded on the first save");
        }
        // saving the same state again writes nothing new
        byte[] stable = File.ReadAllBytes(file);
        card.ChromaHue = 201; card.ChromaHue = 200;
        await card.SaveChromaRecolourNowAsync();
        Assert.Equal(stable, File.ReadAllBytes(file));

        // ---- Ctrl+S: a pending parameter recolour is part of what the editor flushes
        card.ChromaHue = 250;
        Assert.True(card.HasPendingChromaRecolour);
        await (Task)Call(vm, "SavePendingEditorEdits")!;
        Assert.False(card.ChromaRecolourDirty);
        var third = card.ChromaSettings.ToTransform();
        projectParams = Params(File.ReadAllBytes(file));
        foreach (var row in card.ChromaParameters.Where(r => r.IsIncluded))
        {
            float tolerance = riotParams[row.Key].TypeName == "Color" ? 1f / 255f : 1e-6f;
            Assert.True(SameVector(third.Apply(riotParams[row.Key].Value), projectParams[row.Key].Value, tolerance), row.Name + " was not saved by Ctrl+S");
        }
        var savedTransform = card.ChromaSettings.ToTransform();
        var savedOn = card.ChromaParameters.Where(r => r.IsIncluded).Select(r => r.Key).OrderBy(k => k.Name).ThenBy(k => k.Material).ToList();

        // ---- a reload: a new editor on the saved project file
        var reloaded = ReyProjectService.OpenFolder(project.RootPath!);
        var vm2 = Attach(reloaded);
        OpenChampion(vm2, "Lillia", bin);
        var card2 = vm2.MeshPreview;
        RunInline(card2);
        card2.UseDx11Preview = false;
        Assert.True(card2.HasSavedChromaRecolour);
        Assert.Equal(savedTransform, card2.ChromaSettings.ToTransform());
        Assert.False(card2.ChromaRecolourDirty);
        await ScanAsync(vm2);
        Assert.False(card2.ChromaRecolourDirty, "a restored recipe must not look pending");
        Assert.Equal(savedOn, card2.ChromaParameters.Where(r => r.IsIncluded).Select(r => r.Key).OrderBy(k => k.Name).ThenBy(k => k.Material).ToList());
        Assert.Equal(File.ReadAllBytes(file), ReadAsset(vm2, HashAlgorithms.WadPath(bin)));   // the reloaded project serves the saved bin
        // the rows know the project's value is the recolour's, not somebody's edit
        Assert.All(card2.ChromaParameters.Where(r => r.IsIncluded), r => Assert.False(r.IsEditedOutside));

        // ---- Export .fantome: the champion's WAD in the package carries the changed skin bin, byte for byte
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
            using var wad = WadArchive.Open(wadPath, new WadPathResolver(Database.Value!));
            Assert.True(wad.TryGetEntry(HashAlgorithms.WadPath(bin), out var chunk), "the exported champion WAD lacks the skin bin");
            Assert.Equal(File.ReadAllBytes(file), wad.Extract(chunk));
        }

        // ---- Build Package: a pending recolour is flushed by the build itself, and the built WAD carries the bin
        card2.ChromaBrightness = 0.8;
        Assert.True(card2.HasPendingChromaRecolour);
        reloaded.OutputDirectory = Path.Combine(project.RootPath!, "Build2");
        await (Task)Call(vm2, "BuildProject")!;
        Assert.False(card2.ChromaRecolourDirty, "Build Package did not flush the pending parameter recolour");
        var built = Directory.EnumerateFiles(reloaded.OutputDirectory, "Lillia*.wad.client", SearchOption.AllDirectories).FirstOrDefault();
        Assert.NotNull(built);
        using (var wad = WadArchive.Open(built!, new WadPathResolver(Database.Value!)))
        {
            Assert.True(wad.TryGetEntry(HashAlgorithms.WadPath(bin), out var chunk), "the built champion WAD lacks the skin bin");
            Assert.Equal(File.ReadAllBytes(file), wad.Extract(chunk));
            var inBuild = Params(wad.Extract(chunk));
            var brightness = card2.ChromaSettings.ToTransform();
            foreach (var row in card2.ChromaParameters.Where(r => r.IsIncluded).Take(8))
            {
                float tolerance = riotParams[row.Key].TypeName == "Color" ? 1f / 255f : 1e-6f;
                Assert.True(SameVector(brightness.Apply(riotParams[row.Key].Value), inBuild[row.Key].Value, tolerance), row.Name);
            }
        }

        // ---- revert: Riot's values come back, the record goes, the sliders reset
        Assert.True(card2.RevertChromaRecolourCommand.CanExecute(null));
        await card2.RevertChromaRecolourCommand.ExecuteAsync(null);
        Assert.False(card2.HasSavedChromaRecolour);
        Assert.Null(reloaded.ChromaParameterRecolors);
        Assert.DoesNotContain("ChromaParameterRecolors", File.ReadAllText(project.ProjectFilePath!));
        Assert.Empty(reloaded.TextureRecolors.Where(r => r.Transform is not null));
        // the copy of the skin bin the recolour put in the project is Riot's data again, so it is taken out again (a project file nobody asked for)
        Assert.False(File.Exists(file), "the revert left the skin bin the recolour had copied into the project");
        var after = Params(ReadAsset(vm2, HashAlgorithms.WadPath(bin)));
        foreach (var (key, original) in riotParams) Assert.True(original.Value == after[key].Value, $"{original.MaterialName}.{key.Name} is not Riot's after the revert");
        AssertSameExceptParameters(riot, ReadAsset(vm2, HashAlgorithms.WadPath(bin)), new HashSet<SkinColorParamKey>());
        Assert.True(card2.ChromaSettings.ToTransform().IsIdentity);
    }

    // ================================================================================================ review round: what the recipe owns, carries and gives back

    /// <summary>Writes a colour into one parameter of the project's copy of the skin bin, the way a Material-tab save would (the writer's own serialisation).</summary>
    private static void EditProjectParameter(string file, SkinColorParamKey key, Vector4 value)
    {
        var doc = MaterialDocument.Parse(File.ReadAllBytes(file), Name, Path_);
        var material = doc.Materials.Single(m => m.ObjectPathHash == key.Material);
        Assert.True(material.Parameters.Where(p => p.Name == key.Name).ElementAt(key.Occurrence).TrySetColor(value));
        File.WriteAllBytes(file, doc.Serialize());
    }

    private static (MainWindowViewModel Vm, MeshPreviewViewModel Card) Reopen(ReyProject project, string champion, string bin, Action<ReyProject>? beforeOpen = null)
    {
        var reloaded = ReyProjectService.OpenFolder(project.RootPath!);
        beforeOpen?.Invoke(reloaded);
        var vm = Attach(reloaded);
        OpenChampion(vm, champion, bin);
        RunInline(vm.MeshPreview);
        vm.MeshPreview.UseDx11Preview = false;
        return (vm, vm.MeshPreview);
    }

    private async Task<(MainWindowViewModel Vm, ReyProject Project, MeshPreviewViewModel Card, string File, byte[] Riot, Dictionary<SkinColorParamKey, SkinColorParam> RiotParams)> SavedLillia(string name)
    {
        const string bin = "data/characters/lillia/skins/skin49.bin";
        var (vm, project) = OpenScratchProject(name);
        OpenChampion(vm, "Lillia", bin);
        var card = vm.MeshPreview;
        RunInline(card);
        card.UseDx11Preview = false;
        byte[] riot = RiotBin("Lillia", bin);
        await ScanAsync(vm);
        card.ChromaHue = 120;
        await card.SaveChromaRecolourNowAsync();
        return (vm, project, card, ProjectBin(project, "Lillia", bin), riot, Params(riot));
    }

    [Fact]
    public async Task LilliaRoseQuartz_UntickingAParameterAndResettingTheSlidersLeaveTheCardSaved_NotPending()
    {
        if (!HaveGame("Lillia") || Database.Value is null) return;
        var (vm, project, card, file, riot, riotParams) = await SavedLillia("untick");
        var owned = project.ChromaParameterRecolors!.Single().Parameters.Count;
        Assert.False(card.ChromaRecolourDirty);

        // untick a parameter the recolour moved: one save gives it back, and then nothing is pending
        var savedNow = Params(File.ReadAllBytes(file));
        var moved = card.ChromaParameters.First(r => r.IsIncluded && r.Info.TypeName == "Vector4" && !SameVector(riotParams[r.Key].Value, savedNow[r.Key].Value, 0f));
        moved.IsIncluded = false;
        Assert.True(card.ChromaRecolourDirty);
        await card.SaveChromaRecolourNowAsync();
        Assert.False(card.ChromaRecolourDirty, "a parameter given back left the card pending: " + card.ChromaRecolourStatus);
        Assert.False(card.HasPendingChromaRecolour);
        Assert.True(card.HasSavedChromaRecolour);
        Assert.Equal(owned - 1, project.ChromaParameterRecolors!.Single().Parameters.Count);
        Assert.DoesNotContain(project.ChromaParameterRecolors!.Single().Parameters, p => p.Name == moved.Key.Name && p.Material == moved.Key.Material && p.Occurrence == moved.Key.Occurrence);
        Assert.Equal(riotParams[moved.Key].Value, Params(File.ReadAllBytes(file))[moved.Key].Value);        // back to Riot's
        Assert.Equal(riotParams[moved.Key].Value, moved.CurrentValue);                                          // and the row knows it
        Assert.False(card.ChromaRecolourDirty);                                                                  // still, after the bookkeeping settled
        _output.WriteLine(card.ChromaRecolourStatus);

        // reset the sliders to no change: one save, nothing pending, nothing saved, the copy of the bin the recolour placed is gone
        card.ResetChromaSlidersCommand.Execute(null);
        Assert.True(card.ChromaRecolourDirty);
        await card.SaveChromaRecolourNowAsync();
        Assert.False(card.ChromaRecolourDirty, "a reset left the card pending: " + card.ChromaRecolourStatus);
        Assert.False(card.HasSavedChromaRecolour);
        Assert.Null(project.ChromaParameterRecolors);
        Assert.False(File.Exists(file), "giving every parameter back left Riot's skin bin copied into the project");
        Assert.Empty(project.TextureRecolors.Where(r => r.Transform is not null));
        foreach (var row in card.ChromaParameters) Assert.True(SameVector(riotParams.TryGetValue(row.Key, out var p) ? p.Value : row.Info.Riot, row.CurrentValue, 0f), row.Name);
    }

    [Fact]
    public async Task LilliaRoseQuartz_AParameterTheScanDoesNotListStaysInTheRecord_AndKeepsItsRevert()
    {
        if (!HaveGame("Lillia") || Database.Value is null) return;
        const string bin = "data/characters/lillia/skins/skin49.bin";
        var (vm, project, card, file, riot, riotParams) = await SavedLillia("carried");
        await vm.MeshPreview.ChromaIdleAsync();

        // the project's record names a parameter the skin bin no longer has (a patch removed it, say)
        var gone = new ChromaParameterRef { Material = 0x12345678, MaterialName = "A_removed_material", Name = "Gone_Color" };
        var (vm2, card2) = Reopen(project, "Lillia", bin, p => p.ChromaParameterRecolors!.Single().Parameters.Add(gone));
        await ScanAsync(vm2);
        Assert.False(card2.ChromaRecolourDirty, "an unlisted saved parameter made the card look pending");
        int listed = card2.ChromaParameters.Count(r => r.IsIncluded);

        // a flush with a real edit keeps it
        card2.ChromaHue = 200;
        await card2.SaveChromaRecolourNowAsync();
        var record = vm2.Project.ChromaParameterRecolors!.Single();
        Assert.Contains(record.Parameters, p => p.Name == "Gone_Color");
        Assert.Equal(listed + 1, record.Parameters.Count);

        // untick EVERY listed parameter: the record keeps only the carried one - it is not deleted, and the placed bin stays
        foreach (var row in card2.ChromaParameters.Where(r => r.IsIncluded)) row.IsIncluded = false;
        await card2.SaveChromaRecolourNowAsync();
        Assert.False(card2.ChromaRecolourDirty, card2.ChromaRecolourStatus);
        record = Assert.Single(vm2.Project.ChromaParameterRecolors!);
        Assert.Equal("Gone_Color", Assert.Single(record.Parameters).Name);
        Assert.True(File.Exists(file), "the bin was removed while the record still owned a parameter");
        Assert.True(card2.HasSavedChromaRecolour);

        // and after a reload it still has its Revert
        var (vm3, card3) = Reopen(vm2.Project, "Lillia", bin);
        Assert.True(card3.HasSavedChromaRecolour);
        Assert.True(card3.RevertChromaRecolourCommand.CanExecute(null));
        await card3.RevertChromaRecolourCommand.ExecuteAsync(null);
        Assert.Null(vm3.Project.ChromaParameterRecolors);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task LilliaRoseQuartz_AnOwnedParameterTheMaterialTabEditedSinceIsKeptNotOverwritten_UntilItIsSwitchedOnAgain()
    {
        if (!HaveGame("Lillia") || Database.Value is null) return;
        const string bin = "data/characters/lillia/skins/skin49.bin";
        var (vm, project, card, file, riot, riotParams) = await SavedLillia("ownededit");
        var transform = card.ChromaSettings.ToTransform();
        var edited = card.ChromaParameters.First(r => r.IsIncluded && r.Info.TypeName == "Vector4" && !SameVector(transform.Apply(r.Info.Riot), r.Info.Riot, 1e-3f));
        var other = card.ChromaParameters.First(r => r.IsIncluded && r.Info.TypeName == "Vector4" && r.Key != edited.Key && !SameVector(transform.Apply(r.Info.Riot), r.Info.Riot, 1e-3f));
        var mine = new Vector4(0.11f, 0.22f, 0.33f, edited.Info.Riot.W);
        EditProjectParameter(file, edited.Key, mine);

        var (vm2, card2) = Reopen(project, "Lillia", bin);
        await ScanAsync(vm2);
        var row = card2.ChromaParameters.Single(r => r.Key == edited.Key);
        Assert.True(row.Info.OwnedEdited);
        Assert.False(row.IsIncluded);
        Assert.True(row.IsEditedOutside);
        Assert.Contains("Edited in the Material tab since the recolour", row.Note);
        Assert.False(card2.ChromaRecolourDirty, "a kept edit made the card look pending");
        Assert.DoesNotContain(card2.ChromaParameters.Where(r => r.Key != edited.Key && r.Info.Recolourable && !r.Info.OwnedEdited), r => !r.IsIncluded && vm2.Project.ChromaParameterRecolors!.Single().Parameters.Any(p => p.Name == r.Key.Name && p.Material == r.Key.Material));

        // a save that gives ANOTHER parameter back leaves the edit alone
        card2.ChromaParameters.Single(r => r.Key == other.Key).IsIncluded = false;
        await card2.SaveChromaRecolourNowAsync();
        Assert.False(card2.ChromaRecolourDirty, card2.ChromaRecolourStatus);
        var now = Params(ReadAsset(vm2, HashAlgorithms.WadPath(bin)));
        Assert.Equal(mine, now[edited.Key].Value);                                               // the edit survives
        Assert.Equal(riotParams[other.Key].Value, now[other.Key].Value);                         // the other went back to Riot's
        Assert.Contains(vm2.Project.ChromaParameterRecolors!.Single().Parameters, p => p.Name == edited.Key.Name && p.Material == edited.Key.Material);   // and is still owned (carried)

        // a reset to no change gives back what the recipe wrote and leaves the edit - released, not overwritten
        card2.ResetChromaSlidersCommand.Execute(null);
        await card2.SaveChromaRecolourNowAsync();
        Assert.False(card2.ChromaRecolourDirty, card2.ChromaRecolourStatus);
        Assert.Equal(mine, Params(ReadAsset(vm2, HashAlgorithms.WadPath(bin)))[edited.Key].Value);
        Assert.Null(vm2.Project.ChromaParameterRecolors);
        Assert.True(File.Exists(file), "the bin holds a Material-tab edit, so it must not be removed");

        // switching it on again recolours it from Riot's value, replacing the edit
        card2.ChromaHue = 120;
        row = card2.ChromaParameters.Single(r => r.Key == edited.Key);
        row.IsIncluded = true;
        await card2.SaveChromaRecolourNowAsync();
        Assert.True(SameVector(card2.ChromaSettings.ToTransform().Apply(row.Info.Riot), Params(ReadAsset(vm2, HashAlgorithms.WadPath(bin)))[edited.Key].Value, 1e-6f));
    }

    [Fact]
    public async Task LilliaRoseQuartz_ARevertKeepsTheCopiedSkinBinWhenItHoldsAnEditOfSomebodysOwn()
    {
        if (!HaveGame("Lillia") || Database.Value is null) return;
        const string bin = "data/characters/lillia/skins/skin49.bin";
        var (vm, project, card, file, riot, riotParams) = await SavedLillia("keepcopy");

        // somebody changes a number of the project's copy (the Material tab's save)
        var doc = MaterialDocument.Parse(File.ReadAllBytes(file), Name, Path_);
        doc.Materials.Single(m => m.Name.EndsWith("Lillia_Skin49_Leg_inst", StringComparison.OrdinalIgnoreCase)).Parameters.Single(p => p.Name == "Blend_Tile").Apply("6, 6, 0, 0");
        File.WriteAllBytes(file, doc.Serialize());

        var (vm2, card2) = Reopen(project, "Lillia", bin);
        await card2.RevertChromaRecolourCommand.ExecuteAsync(null);
        Assert.Null(vm2.Project.ChromaParameterRecolors);
        Assert.True(File.Exists(file), "a skin bin with somebody's edit in it was removed");
        Assert.Equal(new Vector4(6, 6, 0, 0), BlendTile(File.ReadAllBytes(file), "Lillia_Skin49_Leg_inst"));
        var back = Params(File.ReadAllBytes(file));
        foreach (var (key, original) in riotParams) Assert.True(original.Value == back[key].Value, $"{original.MaterialName}.{key.Name} is not Riot's after the revert");
    }

    // ================================================================================================ the Material tab holds the same bin

    /// <summary>The Character window's Material tab owns the skin bin while it is open. A recolour must neither overwrite its unsaved edits nor
    /// leave it showing (and later saving) the old colours: the tab's edits are saved FIRST, through its own save, the recolour is written on top,
    /// and the tab reads the bin again; a Material-tab save after that starts from the recoloured bin.</summary>
    [Fact]
    public async Task LilliaRoseQuartz_TheMaterialTabsEditIsSavedFirst_TheTabShowsTheRecolour_AndItsNextSaveKeepsBoth()
    {
        if (!HaveGame("Lillia") || Database.Value is null) return;
        const string bin = "data/characters/lillia/skins/skin49.bin";
        var (vm, project) = OpenScratchProject("mattab");
        OpenChampion(vm, "Lillia", bin);
        var card = vm.MeshPreview;
        RunInline(card);
        card.UseDx11Preview = false;
        byte[] riot = RiotBin("Lillia", bin);
        var riotParams = Params(riot);

        // the Material tab opens the skin's bin, as the Character window's Load skin does
        object?[] args = { HashAlgorithms.WadPath(bin), null };
        Assert.True((bool)typeof(MainWindowViewModel).GetMethod("TryResolveEntry", Private)!.Invoke(vm, args)!);
        var entry = (WadAssetEntry)args[1]!;
        byte[] served0 = ReadAsset(vm, entry.PathHash);
        var editor = card.MaterialEditor;
        editor.LoadThumbnail = _ => null;   // no render platform in a test: the thumbnails and the material ball are the editor's, not this test's
        editor.LoadTextureRaw = null;   // the editor skips the material ball when no decode pipeline is wired (its own headless rule)
        editor.Load(MaterialDocument.Parse(served0, Name, Path_, p => { try { return (byte[])Call(vm, "ReadAssetByPath", p)!; } catch { return null; } }), entry, served0);
        Assert.Equal(entry.PathHash, editor.BinEntry!.PathHash);
        Assert.False(editor.IsDirty);

        // an unsaved Material-tab edit of a parameter the recolour does not own (Leg_inst.Blend_Tile)
        var legs = editor.Materials.Single(m => m.Name.EndsWith("Lillia_Skin49_Leg_inst", StringComparison.OrdinalIgnoreCase));
        legs.Model.Parameters.Single(p => p.Name == "Blend_Tile").Apply("6, 6, 0, 0");
        editor.IsDirty = true;
        editor.Search = "Leg_inst";                  // a search and a selection the recolour must not throw away
        editor.SelectedMaterial = editor.FilteredMaterials.Single(m => m.Name.EndsWith("Lillia_Skin49_Leg_inst", StringComparison.OrdinalIgnoreCase));

        await ScanAsync(vm);
        card.ChromaHue = 120;
        var log = new List<string>();
        ((ReyEngine.Core.Diagnostics.Logger)typeof(MainWindowViewModel).GetField("_log", Private)!.GetValue(vm)!).Logged += e => { lock (log) log.Add($"{e.Level} {e.Category}: {e.Message}"); };
        try { await card.SaveChromaRecolourNowAsync(); }   // Apply & Save
        catch
        {
            foreach (var line in log) _output.WriteLine(line);
            foreach (var f in Directory.EnumerateFiles(project.RootPath!, "*", SearchOption.AllDirectories).Where(f => !f.Contains(".tex")).Take(40)) _output.WriteLine("FILE " + Path.GetRelativePath(project.RootPath!, f));
            throw;
        }

        byte[] Served() => ReadAsset(vm, entry.PathHash);   // the Material tab saved the bin as an override of the project: the project serves it from there
        var saved = Params(Served());
        var transform = card.ChromaSettings.ToTransform();
        // both are in the bin: the Material tab's edit...
        Assert.Equal(new Vector4(6, 6, 0, 0), BlendTile(Served(), "Lillia_Skin49_Leg_inst"));
        // ...and the recolour, from Riot's value
        var tint = card.ChromaParameters.First(r => r.IsIncluded && riotParams[r.Key].TypeName == "Vector4" && !SameVector(transform.Apply(riotParams[r.Key].Value), riotParams[r.Key].Value, 1e-4f));
        Assert.True(SameVector(transform.Apply(riotParams[tint.Key].Value), saved[tint.Key].Value, 1e-6f));
        Assert.False(editor.IsDirty);

        // the tab reads the recoloured bin again: its document shows the recolour, and holds the served bin as its base
        Assert.Equal(Served(), editor.BaseBytes);
        Assert.Equal("Leg_inst", editor.Search);
        Assert.EndsWith("Lillia_Skin49_Leg_inst", editor.SelectedMaterial!.Name, StringComparison.OrdinalIgnoreCase);
        var shown = editor.Materials.SelectMany(m => m.Model.Parameters.Select(p => (Material: m.Model, Parameter: p)))
            .First(x => x.Material.ObjectPathHash == tint.Key.Material && x.Parameter.Name == tint.Key.Name);
        shown.Parameter.TryGetColor(out var shownValue);
        Assert.True(SameVector(saved[tint.Key].Value, shownValue, 1e-6f));

        // a later Material-tab edit and save keeps the recolour (it is saved on top of the bin as the project holds it)
        var weapon = editor.Materials.Single(m => m.Name.EndsWith("Lillia_Skin49_Weapon_inst", StringComparison.OrdinalIgnoreCase));
        weapon.Model.Parameters.Single(p => p.Name == "Blend_Tile").Apply("3, 3, 0, 0");
        editor.IsDirty = true;
        await (Task)Call(vm, "SaveCharacterMaterialOverride")!;
        var after = Params(Served());
        Assert.True(SameVector(transform.Apply(riotParams[tint.Key].Value), after[tint.Key].Value, 1e-6f), "a Material-tab save lost the recolour");
        Assert.Equal(new Vector4(3, 3, 0, 0), BlendTile(Served(), "Lillia_Skin49_Weapon_inst"));

        // and a recolour after THAT is still Riot's value through the new transform (not compounded on the first), with both edits kept
        card.ChromaHue = 200;
        await card.SaveChromaRecolourNowAsync();
        var third = card.ChromaSettings.ToTransform();
        var last = Params(Served());
        Assert.True(SameVector(third.Apply(riotParams[tint.Key].Value), last[tint.Key].Value, 1e-6f), "the second recolour compounded on the first");
        Assert.Equal(new Vector4(6, 6, 0, 0), BlendTile(Served(), "Lillia_Skin49_Leg_inst"));
        Assert.Equal(new Vector4(3, 3, 0, 0), BlendTile(Served(), "Lillia_Skin49_Weapon_inst"));
    }

    private static Vector4 BlendTile(byte[] bin, string materialSuffix)
    {
        var parameter = MaterialDocument.Parse(bin, Name, Path_).Materials
            .Single(m => m.Name.EndsWith(materialSuffix, StringComparison.OrdinalIgnoreCase)).Parameters.Single(p => p.Name == "Blend_Tile");
        parameter.TryGetVector4(out var value);
        return value;
    }

    // ================================================================================================ more champions: scan, recolour, save, revert

    [Theory]
    [InlineData("Ahri", 0)]
    [InlineData("Jinx", 0)]
    [InlineData("Aatrox", 0)]
    [InlineData("Aatrox", 24)]
    [InlineData("Lux", 38)]
    [InlineData("Sett", 49)]
    public async Task AnotherSkinSavesItsParametersFromRiotsValue_AndRevertGivesRiotsBinBack(string champion, int skinNumber)
    {
        if (!HaveGame(champion) || Database.Value is null) return;
        string bin = $"data/characters/{champion.ToLowerInvariant()}/skins/skin{skinNumber}.bin";
        var (vm, project) = OpenScratchProject("more-" + champion + skinNumber);
        OpenChampion(vm, champion, bin);
        var card = vm.MeshPreview;
        RunInline(card);
        card.UseDx11Preview = false;
        byte[] riot = RiotBin(champion, bin);
        var riotParams = Params(riot);
        await ScanAsync(vm);
        int on = card.ChromaParameters.Count(r => r.IsIncluded);
        _output.WriteLine($"{champion} skin {skinNumber}: {card.ChromaParameters.Count} listed, {on} on, {card.ChromaParameters.Count(r => !r.CanInclude)} left alone"
            + (card.HasChromaParameterProblem ? "; " + card.ChromaParameterProblem : ""));
        if (on == 0) return;   // nothing in this skin's own bin to recolour

        card.ChromaHue = 150;
        card.ChromaSaturation = 1.2;
        await card.SaveChromaRecolourNowAsync();
        var transform = card.ChromaSettings.ToTransform();
        string file = ProjectBin(project, champion, bin);
        bool anyMoves = card.ChromaParameters.Where(r => r.IsIncluded).Any(r => !SameVector(transform.Apply(riotParams[r.Key].Value), riotParams[r.Key].Value, riotParams[r.Key].TypeName == "Color" ? 1f / 255f : 0f));
        if (!anyMoves)
        {
            // a transform that moves none of this skin's colours (black and white stay what they are) writes nothing: the project does not get a copy of the bin
            Assert.False(File.Exists(file), "a recolour that changes no value still copied the skin bin into the project");
            Assert.NotEmpty(Assert.Single(project.ChromaParameterRecolors!).Parameters);   // the recipe is still saved: a reload gets the sliders back
            _output.WriteLine($"{champion} skin {skinNumber}: no parameter moves under this transform (all black/white/grey): nothing written");
            return;
        }
        Assert.True(File.Exists(file), "the skin bin is not under the " + champion + " WAD folder: " + file);
        var projectParams = Params(File.ReadAllBytes(file));
        foreach (var row in card.ChromaParameters.Where(r => r.IsIncluded))
        {
            float tolerance = riotParams[row.Key].TypeName == "Color" ? 1f / 255f : 1e-6f;
            var expected = transform.Apply(riotParams[row.Key].Value);
            Assert.True(SameVector(expected, projectParams[row.Key].Value, tolerance), $"{row.Name}: expected {expected}, the bin holds {projectParams[row.Key].Value}");
            Assert.Equal(riotParams[row.Key].Value.W, projectParams[row.Key].Value.W);
            if (!ColorTransform.CanTransform(riotParams[row.Key].Value)) Assert.Equal(riotParams[row.Key].Value, projectParams[row.Key].Value);
        }
        AssertSameExceptParameters(riot, File.ReadAllBytes(file), card.ChromaParameters.Where(r => r.IsIncluded).Select(r => r.Key).ToHashSet());
        Assert.DoesNotContain("Overrides", project.ProjectFolders);

        await card.RevertChromaRecolourCommand.ExecuteAsync(null);
        Assert.Null(project.ChromaParameterRecolors);
        Assert.False(File.Exists(file), "the revert left the skin bin the recolour had copied into the project");
        byte[] served = ReadAsset(vm, HashAlgorithms.WadPath(bin));
        var back = Params(served);
        foreach (var (key, original) in riotParams) Assert.True(original.Value == back[key].Value, $"{original.MaterialName}.{key.Name} is not Riot's after the revert");
        AssertSameExceptParameters(riot, served, new HashSet<SkinColorParamKey>());
    }

    // ================================================================================================ the real D3D11 renderer

    /// <summary>A real D3D11 device with one champion skin's character scene committed to it, and the camera the frames are drawn from.</summary>
    private sealed class Rig : IDisposable
    {
        public required ReyEngine.Rendering.D3D11.ShaderPreviewRenderer Renderer { get; init; }
        public required ShaderCacheReader Cache { get; init; }
        public required PreparedCharacterScene Scene { get; init; }
        public required ReyEngine.Rendering.D3D11.PreviewSettings Settings { get; init; }
        public required Func<byte[], Func<ulong, byte[]?>, PreparedCharacterScene?> Build { get; init; }
        public required byte[] Bin { get; init; }
        public required Func<ulong, byte[]?> Read { get; init; }
        public required WadArchive Archive { get; init; }
        public const int Size = 512;

        public byte[] Frame() => Renderer.RenderFrame(Size, Size, Settings, out var error)?.ToArray() ?? throw new InvalidOperationException("no frame: " + error);

        public void Dispose() { Renderer.Dispose(); Cache.Dispose(); Archive.Dispose(); }

        public static Rig? Create(string champion, int skinNumber)
        {
            if (!HaveGame(champion) || Database.Value is not { } database) return null;
            var resolver = new WadPathResolver(database);
            var cache = ShaderCacheReader.Open(Final, resolver, out _);
            if (cache is null) return null;
            var renderer = new ReyEngine.Rendering.D3D11.ShaderPreviewRenderer();
            if (!renderer.Initialize(out _)) { renderer.Dispose(); cache.Dispose(); return null; }   // no D3D11 here

            string binPath = $"data/characters/{champion.ToLowerInvariant()}/skins/skin{skinNumber}.bin";
            var archive = WadArchive.Open(Path.Combine(Final, "Champions", champion + ".wad.client"), resolver);
            byte[]? Read(ulong h) { try { return archive.TryGetEntry(h, out _) ? archive.Extract(h) : null; } catch { return null; } }
            byte[] bin = Read(HashAlgorithms.WadPath(binPath))!;
            var doc = MaterialDocument.Parse(bin, Name, Path_);
            byte[] skn = Read(BinTexturePath.HashOfReference(doc.SkinMesh!.SimpleSkin!))!;
            ShaderPermutationIndex? perms = null;
            try { perms = new ShaderPermutationIndex(Final, h => database.TryGetPath(h, out var p) ? p : null); } catch { }
            PreparedCharacterScene? Build(byte[] skinBin, Func<ulong, byte[]?> read) => Dx11CharacterScene.Prepare(skn, skinBin, cache, perms, readAsset: read,
                resolveBinName: Name, resolveWadPath: Path_, fallbackShader: Dx11CharacterScene.DefaultCharacterShader);
            var scene = Build(bin, Read)!;
            Assert.NotEmpty(scene.Slices);
            Dx11CharacterScene.Commit(renderer, scene, "");

            var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
            foreach (var v in scene.Mesh.Vertices) { lo = Vector3.Min(lo, v.Position); hi = Vector3.Max(hi, v.Position); }
            var centre = (lo + hi) * 0.5f;
            float radius = MathF.Max(1f, (hi - lo).Length() * 0.5f);
            var eye = centre + new Vector3(radius * 0.6f, radius * 0.10f, radius * 1.25f);
            var settings = new ReyEngine.Rendering.D3D11.PreviewSettings
            {
                SuppliedView = Matrix4x4.CreateLookAt(eye, centre, Vector3.UnitY),
                SuppliedProjection = Matrix4x4.CreatePerspectiveFieldOfView(0.9f, 1f, radius * 0.02f, radius * 40f),
                SuppliedCameraPosition = eye,
                AlphaBlend = true, DepthTest = true, MirrorX = true, TransposeMatrices = true,
                CullBackFaces = true, SortByPipeline = true, TimeSeconds = 0f,
                ClearColor = new Vector4(0.039f, 0.051f, 0.075f, 1f),
            };
            return new Rig { Renderer = renderer, Cache = cache, Scene = scene, Settings = settings, Build = Build, Bin = bin, Read = Read, Archive = archive };
        }
    }

    private static void WireDevice(MeshPreviewViewModel card, Rig rig, bool textures, Action<int>? parameterWrites = null)
    {
        RunInline(card);
        card.UseDx11Preview = true;
        card.SetDx11Scene(rig.Scene, "");
        card.PushChromaParamsDx11 = items => { int n = ChromaDx11Parameters.Apply(rig.Renderer, rig.Scene, items); parameterWrites?.Invoke(n); };
        if (!textures) return;
        card.PushChromaDx11 = items =>
        {
            var touched = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (key, rgba, width, height) in items)
                if (rig.Renderer.UpdatePooledTexture(key, rgba, width, height)) touched.Add(key);
            foreach (string key in touched) rig.Renderer.RegeneratePooledMips(key);
        };
    }

    private static void SaveShots(string stem, int size, params (string Name, byte[] Bgra)[] frames)
    {
        if (System.Environment.GetEnvironmentVariable("REYENGINE_M825_SHOTS") is not { Length: > 0 } shots) return;
        Directory.CreateDirectory(shots);
        foreach (var (name, bgra) in frames) ChromaRecolourRealDataTests.WritePng(Path.Combine(shots, $"{stem}-{name}.png"), bgra, size, size);
    }

    /// <summary>The recolour of the colour PARAMETERS on Riot's own shaders: the champion's D3D11 character scene drawn on a real device, its
    /// parameters recoloured through the card's live push (the view model's batch and <see cref="ChromaDx11Parameters"/>, the very code the window
    /// runs), and drawn again - with the textures switched OFF so that only the parameters can have moved the picture. The frames are written as
    /// PNGs when <c>REYENGINE_M825_SHOTS</c> names a folder.</summary>
    [Theory]
    [InlineData("Lillia", 49)]
    [InlineData("Ahri", 0)]
    public async Task TheParameterRecolourReachesTheRealD3D11Frame(string champion, int skinNumber)
    {
        using var rig = Rig.Create(champion, skinNumber);
        if (rig is null) return;
        string binPath = $"data/characters/{champion.ToLowerInvariant()}/skins/skin{skinNumber}.bin";
        var (vm, project) = OpenScratchProject("device-" + champion);
        OpenChampion(vm, champion, binPath);
        byte[] before = rig.Frame();

        // ---- the card: the real scan, the real sliders, the window's own push into this renderer. Textures OFF: only parameters move.
        var card = vm.MeshPreview;
        int written = 0;
        WireDevice(card, rig, textures: false, n => written += n);
        await ScanAsync(vm);
        foreach (var row in card.ChromaTextures.ToList()) row.IsIncluded = false;
        await card.ChromaIdleAsync();
        Assert.NotEmpty(card.ChromaParameters.Where(r => r.IsIncluded));
        card.ChromaHue = 120;
        await card.ChromaIdleAsync();
        Assert.True(written > 0, "no parameter reached a D3D11 material");
        _output.WriteLine($"{champion} {skinNumber}: {card.ChromaParameters.Count(r => r.IsIncluded)} parameter(s) on, {card.ChromaParameters.Count(r => r.IsIncluded && r.CanPreview)} previewable, {written} material write(s) in the first push");
        foreach (var row in card.ChromaParameters.Where(r => r.IsIncluded && !r.CanPreview).Take(3)) _output.WriteLine("  not drawn: " + row.Name + " - " + row.Detail);

        byte[] after = rig.Frame();
        var (covered, changed, shiftBefore, shiftAfter) = Compare(before, after);
        _output.WriteLine($"covered {covered} px, changed {changed} px; mean RGB before ({shiftBefore.R:0},{shiftBefore.G:0},{shiftBefore.B:0}) after ({shiftAfter.R:0},{shiftAfter.G:0},{shiftAfter.B:0})");
        Assert.True(covered > 2000, "the character did not draw");
        Assert.True(changed > covered / 50, $"only {changed} of {covered} character pixels changed under a +120 degree hue shift of the colour parameters");

        // the slider back to no change draws Riot's colours again
        card.ChromaHue = 0;
        await card.ChromaIdleAsync();
        byte[] reset = rig.Frame();
        long residual = 0;
        for (int i = 0; i < before.Length; i++) residual += Math.Abs(before[i] - reset[i]);
        _output.WriteLine($"after resetting the hue: mean abs difference to the original frame {residual / (double)before.Length:0.000}/255");
        Assert.True(residual / (double)before.Length < 0.05, "resetting the slider did not restore the original frame");

        // ---- saved, the project reloaded, the scene built again from the PROJECT's skin bin: the frame still shows the recolour
        card.ChromaHue = 120;
        await card.ChromaIdleAsync();
        byte[] live = rig.Frame();
        await card.SaveChromaRecolourNowAsync();
        var reloadedProject = ReyProjectService.OpenFolder(project.RootPath!);
        var vm2 = Attach(reloadedProject);
        OpenChampion(vm2, champion, binPath);
        byte[]? ReadFromProject(ulong h) { try { return ReadAsset(vm2, h); } catch { return null; } }
        var scene2 = rig.Build(ReadFromProject(HashAlgorithms.WadPath(binPath))!, ReadFromProject);
        Assert.NotNull(scene2);
        Dx11CharacterScene.Commit(rig.Renderer, scene2!, "");   // clears the pool: every texture is made again from what the project serves
        byte[] fromProject = rig.Frame();
        long vsLive = 0, vsOriginal = 0;
        for (int i = 0; i < live.Length; i++) { vsLive += Math.Abs(live[i] - fromProject[i]); vsOriginal += Math.Abs(before[i] - fromProject[i]); }
        _output.WriteLine($"project reloaded and the scene rebuilt from its bin: mean abs difference to the live preview {vsLive / (double)live.Length:0.000}, to the original {vsOriginal / (double)live.Length:0.000} (/255)");
        Assert.True(vsLive / (double)live.Length < 0.5, "the frame built from the saved project does not match the live preview");
        Assert.True(vsOriginal / (double)live.Length > 0.05, "the frame built from the saved project still shows the original colours");

        SaveShots($"{champion.ToLowerInvariant()}{skinNumber}-params", Rig.Size,
            ("d3d11-before", before), ("d3d11-hue120", after), ("d3d11-reset", reset), ("d3d11-reloaded", fromProject));
    }

    /// <summary>The whole BODY RECOLOUR on the device - the textures and the colour parameters moved by the same slider - drawn before and after,
    /// and the project reloaded and drawn from its own files (the saved skin bin and the saved textures).</summary>
    [Theory]
    [InlineData("Ahri", 0)]
    [InlineData("Lillia", 49)]
    public async Task TheWholeBodyRecolourTexturesAndParametersTogether(string champion, int skinNumber)
    {
        using var rig = Rig.Create(champion, skinNumber);
        if (rig is null) return;
        string binPath = $"data/characters/{champion.ToLowerInvariant()}/skins/skin{skinNumber}.bin";
        var (vm, project) = OpenScratchProject("whole-" + champion);
        OpenChampion(vm, champion, binPath);
        byte[] before = rig.Frame();

        var card = vm.MeshPreview;
        WireDevice(card, rig, textures: true);
        await ScanAsync(vm);
        card.ChromaHue = 120;
        await card.ChromaIdleAsync();
        byte[] live = rig.Frame();
        var (covered, changed, b, a) = Compare(before, live);
        _output.WriteLine($"{champion} {skinNumber} whole recolour: covered {covered} px, changed {changed} px; mean RGB before ({b.R:0},{b.G:0},{b.B:0}) after ({a.R:0},{a.G:0},{a.B:0})");
        Assert.True(changed > covered / 4, "a +120 degree hue shift of the textures and the parameters moved only " + changed + " of " + covered + " pixels");

        await card.SaveChromaRecolourNowAsync();
        var reloadedProject = ReyProjectService.OpenFolder(project.RootPath!);
        var vm2 = Attach(reloadedProject);
        OpenChampion(vm2, champion, binPath);
        byte[]? ReadFromProject(ulong h) { try { return ReadAsset(vm2, h); } catch { return null; } }
        var scene2 = rig.Build(ReadFromProject(HashAlgorithms.WadPath(binPath))!, ReadFromProject);
        Assert.NotNull(scene2);
        Dx11CharacterScene.Commit(rig.Renderer, scene2!, "");
        byte[] fromProject = rig.Frame();
        long vsLive = 0;
        for (int i = 0; i < live.Length; i++) vsLive += Math.Abs(live[i] - fromProject[i]);
        _output.WriteLine($"reloaded from the project's own files: mean abs difference to the live preview {vsLive / (double)live.Length:0.000}/255");
        Assert.True(vsLive / (double)live.Length < 3.0, "the frame built from the saved project does not match the live preview");

        SaveShots($"{champion.ToLowerInvariant()}{skinNumber}-whole", Rig.Size, ("d3d11-before", before), ("d3d11-hue120", live), ("d3d11-reloaded", fromProject));
    }

    private static (long Covered, long Changed, (double R, double G, double B) Before, (double R, double G, double B) After) Compare(byte[] before, byte[] after)
    {
        long covered = 0, changed = 0; double dr = 0, dg = 0, db = 0, br = 0, bg = 0, bb = 0;
        for (int i = 0; i + 3 < before.Length; i += 4)
        {
            bool body = Math.Abs(before[i] - 19) > 12 || Math.Abs(before[i + 1] - 13) > 12 || Math.Abs(before[i + 2] - 10) > 12;   // BGRA: not the clear colour
            if (!body) continue;
            covered++;
            if (Math.Abs(before[i] - after[i]) + Math.Abs(before[i + 1] - after[i + 1]) + Math.Abs(before[i + 2] - after[i + 2]) > 12) changed++;
            br += before[i + 2]; bg += before[i + 1]; bb += before[i];
            dr += after[i + 2]; dg += after[i + 1]; db += after[i];
        }
        double n = Math.Max(1, covered);
        return (covered, changed, (br / n, bg / n, bb / n), (dr / n, dg / n, db / n));
    }

    /// <summary>The two bins hold the same objects, property for property, except for these parameters (the writer may order properties differently; Riot's order is not the writer's).</summary>
    private static void AssertSameExceptParameters(byte[] riot, byte[] project, HashSet<SkinColorParamKey> owned)
    {
        var a = MaterialDocument.Parse(riot, Name, Path_);
        var b = MaterialDocument.Parse(project, Name, Path_);
        Assert.Equal(a.Materials.Count, b.Materials.Count);
        // the whole tree, with the owned parameters put back to Riot's value, is Riot's tree
        var restored = SkinColorParameters.Rewrite(project, riot, ColorTransform.Identity, Array.Empty<SkinColorParamKey>(), owned, Name, Path_);
        byte[] healed = restored.Bytes ?? project;
        string? difference = BinTreeEquivalence.FirstDifference(SafeBinTree.Parse(riot), SafeBinTree.Parse(healed));
        Assert.True(difference is null, "the skin bin differs from Riot's beyond the recoloured parameters: " + difference);
    }

    private sealed class NoProgress : IProgress<(double Frac, string Stage)>
    {
        public void Report((double Frac, string Stage) value) { }
    }
}
