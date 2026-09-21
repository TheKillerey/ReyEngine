using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>M697: the Character Creator's view model - the rows a folder produces, the choices the user
/// can change before creating, and what is handed to the host. The folder is a real one, extracted out of
/// the installed Summoner's Rift; the host is a stub, because staging into a project is the host's job
/// and this is about what the window decides.</summary>
public sealed class CharacterCreatorTests : IDisposable
{
    private const string Map11 = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping\Map11.wad.client";
    private readonly string _root = Directory.CreateTempSubdirectory("reyengine_m697_vm_").FullName;

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private string? Folder(string prefix)
    {
        if (!File.Exists(Map11)) return null;
        HashDatabase database;
        try { database = new HashSyncService().LoadLocal(_ => { }); } catch { return null; }
        using var wad = WadArchive.Open(Map11, new WadPathResolver(database));
        int written = 0;
        foreach (var e in wad.Entries.Where(x => x.IsResolved && x.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            string file = Path.Combine(_root, e.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, wad.Extract(e));
            written++;
        }
        Assert.True(written > 0, $"Map11.wad.client no longer holds {prefix}");
        return Path.Combine(_root, prefix.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));
    }

    [Fact]
    public void AFolderFillsTheRowsAndTheChoicesTheUserCanChange()
    {
        if (Folder("assets/characters/yellowtrinketupgrade/") is not { } folder) return;
        var vm = new CharacterCreatorViewModel { Create = (_, _) => Task.FromResult("done") };
        Assert.False(vm.CanCreate);          // nothing scanned yet
        Assert.False(vm.HasScan);

        vm.Folder = folder;
        vm.Rescan();
        Assert.True(vm.HasScan);
        Assert.True(vm.CanCreate);
        Assert.Equal("Yellowtrinketupgrade", vm.Name);
        Assert.Equal(8, vm.Files.Count);
        Assert.Equal(4, vm.Clips.Count);
        Assert.Equal(4, vm.UpgradeCount);    // the skn and three v4 clips
        Assert.Equal(2, vm.Textures.Count);
        Assert.Equal("trinket_yellow_upgrade_tx_cm.tex", vm.SelectedTexture!.FileName);
        Assert.Equal("", vm.Problems);
        Assert.Contains("SKN 2.1", vm.Summary);
        Assert.Contains("will be upgraded", vm.Summary);
        Assert.Contains(vm.Files, f => f.Role == "MESH" && f.WillUpgrade);
        Assert.Contains(vm.Files, f => f.Role == "SKELETON" && !f.WillUpgrade);
        Assert.All(vm.Files.Where(f => f.IsUsed), f => Assert.NotEqual("", f.Note));

        // the choices: a renamed clip, an unticked clip, the other texture, a scale
        var idle = vm.Clips.First(c => c.Name.Contains("idle", StringComparison.OrdinalIgnoreCase));
        idle.Name = "Idle1";
        Assert.True(idle.Loop);
        vm.Clips.First(c => c.FileName.StartsWith("tempchar", StringComparison.OrdinalIgnoreCase)).Include = false;
        vm.SelectedTexture = vm.Textures.First(t => t.FileName == "yellowtrinketupgrade_square.tex");
        vm.Name = "TrinketProp";
        vm.SkinScale = 2.5f;

        var spec = vm.BuildSpec();
        Assert.Equal("TrinketProp", spec.Name);
        Assert.Equal(2.5f, spec.SkinScale);
        Assert.Equal(3, spec.Clips.Count);
        Assert.Contains(spec.Clips, c => c.Name == "Idle1" && c.Loop);
        Assert.DoesNotContain(spec.Clips, c => c.AnmFileName.StartsWith("tempchar", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("yellowtrinketupgrade_square.tex", spec.TextureFileName);
        Assert.Equal("Idle1", CharacterPackageBuilder.PickIdle(spec.Clips));
    }

    [Fact]
    public void ABadNameOrAClashingClipStopsIt()
    {
        if (Folder("assets/characters/yellowtrinketupgrade/") is not { } folder) return;
        var vm = new CharacterCreatorViewModel { Create = (_, _) => Task.FromResult("done") };
        vm.Folder = folder;
        vm.Rescan();

        vm.Name = "9 bad";
        Assert.NotNull(vm.NameProblem);
        Assert.True(vm.HasNameProblem);
        Assert.False(vm.CanCreate);
        Assert.Throws<InvalidOperationException>(() => vm.BuildSpec());

        vm.Name = "Fine";
        Assert.Null(vm.NameProblem);
        Assert.True(vm.CanCreate);

        // two clips under one name, and a clip with no name at all
        vm.Clips[0].Name = "Idle1";
        vm.Clips[1].Name = "idle1";
        Assert.Contains("Two clips", Assert.Throws<InvalidOperationException>(() => vm.BuildSpec()).Message);
        vm.Clips[1].Name = "123";
        Assert.Contains("needs a name", Assert.Throws<InvalidOperationException>(() => vm.BuildSpec()).Message);

        // an empty folder says so and creates nothing
        string empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        vm.Folder = empty;
        vm.Rescan();
        Assert.False(vm.CanCreate);
        Assert.True(vm.HasProblems);
        Assert.Empty(vm.Files);
        Assert.Empty(vm.Clips);
    }

    [Fact]
    public async Task CreateHandsTheHostThePackageAndTheChoiceToPlaceIt()
    {
        if (Folder("assets/characters/sru_camprespawnmarker/") is not { } folder) return;
        CharacterImportResult? handed = null;
        bool? placed = null;
        var vm = new CharacterCreatorViewModel
        {
            CanPlace = true,
            Create = (result, place) => { handed = result; placed = place; return Task.FromResult("staged 6 file(s)"); },
        };
        vm.Folder = folder;
        vm.Rescan();
        vm.Name = "Marker";
        Assert.Equal("Create and place at the gizmo", vm.CreateLabel);
        vm.AlsoPlace = false;
        Assert.Equal("Create in project", vm.CreateLabel);

        await vm.CreateCharacterCommand.ExecuteAsync(null);
        Assert.NotNull(handed);
        Assert.False(placed);
        Assert.Equal("staged 6 file(s)", vm.Status);
        Assert.True(vm.Created);
        Assert.False(vm.CanCreate);                       // one create per scan
        Assert.Equal("Marker", handed!.Package.Name);
        Assert.Equal("Characters/Marker/Skins/Skin0", handed.Package.Skin);
        Assert.Contains(handed.Upgrades, u => u.After == "SKN 4.1");
        Assert.Contains(handed.Upgrades, u => u.After == "ANM v5");
        Assert.Contains(handed.Files, f => f.Path == "data/characters/marker/skins/skin0.bin");

        // a host that refuses says why, and the window stays usable
        var refused = new CharacterCreatorViewModel { Create = (_, _) => throw new InvalidOperationException("Open a map first.") };
        refused.Folder = folder;
        refused.Rescan();
        await refused.CreateCharacterCommand.ExecuteAsync(null);
        Assert.Equal("Open a map first.", refused.Status);
        Assert.False(refused.Created);
        Assert.True(refused.CanCreate);
    }

    [Fact]
    public void TheMenuOpensItAndTheHostStagesAndPlaces()
    {
        var xaml = Source("src", "ReyEngine.App", "Views", "MainWindow.axaml");
        var code = Source("src", "ReyEngine.App", "Views", "MainWindow.axaml.cs");
        var host = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.CharacterCreator.cs");
        var staging = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        if (xaml is null || code is null || host is null || staging is null) return;
        Assert.Contains("Header=\"Import character folder…\" Command=\"{Binding OpenCharacterCreatorCommand}\"", xaml);
        Assert.Contains("vm.ShowCharacterCreatorWindow = ShowCharacterCreator;", code);
        Assert.Contains("new CharacterCreatorWindow { DataContext = vm }", code);
        // the host stages with overwrite (a re-created character must not keep the last attempt's bins)
        Assert.Contains("WriteStagedAssets(sources, mapEntry, new List<string>(), overwrite: true)", host);
        Assert.Contains("bool overwrite = false)", staging);
        Assert.Contains("if (!overwrite && File.Exists(file)) continue;", staging);
        // and places through the writer's create verbs, at the gizmo, with the package's idle - as a
        // MapAnimatedProp by default (M747), as a scenery character when the window says so
        Assert.Contains("CreateCharacter = true,", host);
        Assert.Contains("CreateAnimatedProp = true,", host);
        Assert.Contains("await PlaceCharacterAsync(package.Name, package.CharacterRecord, package.Skin, package.IdleClip, mapEntry, asAnimatedProp,", host);
        Assert.Contains("transform.Translation = GizmoPivot ?? map.Center;", host);
        // M722: and lists every character on the map in the map's own bin, so the game preloads it
        Assert.Contains("string listed = await RegisterMapCharactersAsync(mapEntry, onMap);", host);
        Assert.Contains("MapCharacterListWriter.Register(bytes, characters, characters,", host);
        Assert.Contains("MapBinPathFor(mapEntry.Path)", host);
    }

    private static string? Source(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        return null;
    }
}
