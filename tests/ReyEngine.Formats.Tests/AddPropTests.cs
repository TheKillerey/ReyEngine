using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Meta;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>M697: adding a prop the map's package already carries. The package is the real jade map, whose
/// characters are the camps and turrets the editor has drawn since M676; the placement it asks the host
/// for is the same one the Character Creator asks for, which is the point of the milestone.</summary>
public sealed class AddPropTests
{
    private const string Map453 = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping\Map453.wad.client";

    private static (AddPropViewModel Vm, HashDatabase Db)? Open()
    {
        if (!File.Exists(Map453)) return null;
        HashDatabase db;
        try { db = new HashSyncService().LoadLocal(_ => { }); } catch { return null; }
        var wad = WadArchive.Open(Map453, new WadPathResolver(db));
        byte[]? Read(string path)
        {
            ulong h = BinTexturePath.HashOfReference(path);
            return wad.TryGetEntry(h, out _) ? wad.Extract(h) : null;
        }
        var vm = new AddPropViewModel
        {
            ReadAsset = Read,
            ResolveBinName = h => db.TryGetBinName(h, out var n) ? n : null,
            ResolveWadPath = h => db.TryGetPath(h, out var p) ? p : null,
            Add = _ => Task.FromResult("placed"),
        };
        vm.Load(wad.Entries.Where(e => e.IsResolved).Select(e => e.Path));
        return (vm, db);
    }

    [Fact]
    public void TheListIsEveryCharacterInThePackageAndCostsNoParsing()
    {
        if (Open() is not var (vm, _)) return;
        Assert.True(vm.CharacterCount > 20, $"the jade map carries more characters than {vm.CharacterCount}");
        Assert.Contains(vm.Characters, c => c.Name.Equals("smallgolem", StringComparison.OrdinalIgnoreCase));
        Assert.All(vm.Characters, c => Assert.NotEmpty(c.Skins));

        vm.Search = "golem";
        Assert.NotEmpty(vm.Characters);
        Assert.All(vm.Characters, c => Assert.Contains("golem", c.Name, StringComparison.OrdinalIgnoreCase));
        vm.Search = "";
        Assert.True(vm.Characters.Count == vm.CharacterCount);

        // a listing of nothing says so rather than looking like a broken window
        var empty = new AddPropViewModel();
        empty.Load(new[] { "data/maps/shipping/map11/map11.materials.bin", "assets/characters/x/y.skn" });
        Assert.Empty(empty.Characters);
        Assert.Contains("no characters", empty.Status, StringComparison.OrdinalIgnoreCase);
        Assert.False(empty.CanAdd);
    }

    [Fact]
    public async Task PickingASkinResolvesItsMeshItsClipsAndTheStringsThePlacementStores()
    {
        if (Open() is not var (vm, _)) return;
        vm.Search = "smallgolem";
        var golem = Assert.Single(vm.Characters);
        vm.SelectedCharacter = golem;
        Assert.NotEmpty(vm.Skins);
        Assert.NotNull(vm.SelectedSkin);

        // the two strings a scenery placement carries, spelled the way the hash dictionary knows them
        Assert.Equal("Characters/SmallGolem/Skins/Skin0", vm.SkinObjectPath);
        Assert.Equal("Characters/SmallGolem/CharacterRecords/Root", vm.CharacterRecordPath);
        Assert.Equal("", vm.Problem);
        Assert.True(vm.CanAdd);
        // the clips come from the skin's OWN graph - SmallGolem's are the Golem's (M679)
        Assert.NotEmpty(vm.Clips);
        Assert.Equal("Idle1", vm.SelectedClip);
        Assert.Contains("golem", vm.Detail, StringComparison.OrdinalIgnoreCase);

        AddPropRequest? asked = null;
        vm.Add = r => { asked = r; return Task.FromResult("placed 1"); };
        await vm.AddPropCommand.ExecuteAsync(null);
        Assert.Equal("placed 1", vm.Status);
        Assert.NotNull(asked);
        Assert.Equal("Characters/SmallGolem/Skins/Skin0", asked!.Skin);
        Assert.Equal("Characters/SmallGolem/CharacterRecords/Root", asked.CharacterRecord);
        Assert.Equal("Idle1", asked.IdleClip);

        // a host that refuses is reported, not swallowed
        vm.Add = _ => throw new InvalidOperationException("Open a map first.");
        await vm.AddPropCommand.ExecuteAsync(null);
        Assert.Equal("Open a map first.", vm.Status);
    }

    [Fact]
    public void ASkinWithNoMeshIsRefusedRatherThanPlacedInvisible()
    {
        var vm = new AddPropViewModel
        {
            ReadAsset = _ => new byte[] { 1, 2, 3 },        // not a bin at all
            Add = _ => Task.FromResult("placed"),
        };
        vm.Load(new[] { "data/characters/ghost/skins/skin0.bin" });
        Assert.Single(vm.Characters);
        vm.SelectedCharacter = vm.Characters[0];
        Assert.True(vm.HasProblem);
        Assert.False(vm.CanAdd);
        Assert.Null(vm.SkinObjectPath);

        // and one the package does not actually hold
        var missing = new AddPropViewModel { ReadAsset = _ => null, Add = _ => Task.FromResult("placed") };
        missing.Load(new[] { "data/characters/ghost/skins/skin0.bin" });
        missing.SelectedCharacter = missing.Characters[0];
        Assert.Contains("not in this map", missing.Problem);
        Assert.False(missing.CanAdd);
    }

    [Fact]
    public void TheCatalogueListsFromPathsAndNeverTwice()
    {
        var paths = new[]
        {
            "data/characters/golem/skins/skin0.bin",
            "DATA/Characters/Golem/Skins/Skin0.bin",            // the same bin through a second mount
            "data/characters/golem/skins/skin2.bin",
            "data/characters/golem/golem.bin",                  // the record, not a skin
            "data/characters/golem/animations/skin0.bin",       // the graph, not a skin
            "assets/characters/golem/golem.skn",
        };
        var characters = CharacterCatalog.Characters(paths, championName: null);
        var golem = Assert.Single(characters);
        Assert.Equal("golem", golem.Name);
        Assert.Equal(2, golem.Skins.Count);
        Assert.Equal(new[] { 0, 2 }, golem.Skins.Select(s => s.Number));
    }

    [Fact]
    public void TheMenuOpensItAndBothWindowsPlaceThroughOneMethod()
    {
        var xaml = Source("src", "ReyEngine.App", "Views", "MainWindow.axaml");
        var code = Source("src", "ReyEngine.App", "Views", "MainWindow.axaml.cs");
        var host = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.CharacterCreator.cs");
        if (xaml is null || code is null || host is null) return;
        Assert.Contains("Header=\"Add prop to map…\" Command=\"{Binding OpenAddPropCommand}\"", xaml);
        Assert.Contains("vm.ShowAddPropWindow = ShowAddProp;", code);
        Assert.Contains("new AddPropWindow { DataContext = vm }", code);
        // one placement method, called by the creator and by the prop list
        Assert.Contains("private async Task<string> PlaceCharacterAsync(", host);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(host, @"await PlaceCharacterAsync\(").Count);
        Assert.Contains("vm.Load(AssetEntries.Where(e => e.IsResolved).Select(e => e.Path));", host);
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
