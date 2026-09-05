using ReyEngine.App.ViewModels;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meshes;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M643: the character window's left panel - the picker embedded, and the outliner that names each
/// submesh's material and opens it on selection.
/// </summary>
public sealed class CharacterOutlinerTests
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private const string GameDirectory = @"C:\Riot Games\League of Legends\Game";

    private static readonly Lazy<HashDatabase?> Database = new(() =>
    {
        try { return new HashSyncService().LoadLocal(_ => { }); }
        catch { return null; }
    });

    private static string? Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private sealed class MutableHost : ICharacterBrowserHost
    {
        public string? GameDirectory { get; set; }
        public IHashResolver? Resolver => Database.Value is { } db ? new WadPathResolver(db) : null;
        public void OpenSkin(ChampionPackage champion, CharacterEntry character, CharacterSkinInfo skin) { }
    }

    // ===================================================== the outliner

    [Fact]
    public void SelectingASubmeshOpensItsMaterialAndOutlinesIt()
    {
        string wad = Path.Combine(Final, "Champions", "Ahri.wad.client");
        if (!File.Exists(wad) || Database.Value is not { } database) return;
        using var archive = WadArchive.Open(wad, new WadPathResolver(database));
        ulong sknHash = HashAlgorithms.WadPath("assets/characters/ahri/skins/base/ahri_base.skn");
        ulong binHash = HashAlgorithms.WadPath("data/characters/ahri/skins/skin0.bin");
        if (!archive.TryGetEntry(sknHash, out _) || !archive.TryGetEntry(binHash, out var binEntry)) return;

        var preview = new MeshPreviewViewModel();
        var mesh = SkinnedMeshDecoder.Decode(archive.Extract(sknHash));
        preview.Show("Ahri", mesh, null, null);
        Assert.Equal(mesh.SubMeshes.Count, preview.Submeshes.Count);
        Assert.All(preview.Submeshes, r => Assert.Equal("", r.MaterialName));   // no materials yet
        Assert.Equal(preview.Submeshes.Select((_, i) => i), preview.Submeshes.Select(r => r.Index));

        byte[] bin = archive.Extract(binHash);
        var doc = MaterialDocument.Parse(bin,
            h => database.TryGetBinName(h, out var n) ? n : null,
            h => database.TryGetPath(h, out var p) ? p : null);
        preview.MaterialEditor.Load(doc, binEntry, bin);
        preview.ShowMaterials(true);

        // The names come from the same document the editor edits.
        var named = preview.Submeshes.Where(r => r.HasMaterial).ToList();
        Assert.NotEmpty(named);
        foreach (var row in named)
            Assert.Equal(preview.MaterialFor(row.Name)!.Name, row.MaterialName);

        // Selecting a row outlines it and opens its material on the Material tab.
        preview.PanelTab = MeshPreviewViewModel.SceneTab;
        preview.SelectedSubmesh = named[0];
        Assert.Equal(new[] { named[0].Index }, preview.HighlightedSubmeshes);
        Assert.Same(preview.MaterialFor(named[0].Name), preview.MaterialEditor.SelectedMaterial);
        Assert.Equal(MeshPreviewViewModel.MaterialTab, preview.PanelTab);

        // A search that hides the material is cleared rather than leaving the selector empty.
        preview.MaterialEditor.Search = "no material is called this";
        preview.SelectedSubmesh = null;
        preview.SelectedSubmesh = named[0];
        Assert.Equal("", preview.MaterialEditor.Search);
        Assert.Same(preview.MaterialFor(named[0].Name), preview.MaterialEditor.SelectedMaterial);

        // Deselecting drops the outline; the materials leaving drops the names.
        preview.SelectedSubmesh = null;
        Assert.Null(preview.HighlightedSubmeshes);
        preview.ShowMaterials(false);
        Assert.All(preview.Submeshes, r => Assert.False(r.HasMaterial));
    }

    // ===================================================== the picker

    [Fact]
    public void ThePickerListsTheInstallAgainWhenTheGameFolderAppears()
    {
        var host = new MutableHost { GameDirectory = null };
        var browser = new CharacterBrowserViewModel(host);
        Assert.Empty(browser.Champions);
        Assert.Contains("game folder", browser.Status, StringComparison.OrdinalIgnoreCase);

        if (!Directory.Exists(Path.Combine(Final, "Champions"))) return;
        host.GameDirectory = GameDirectory;
        browser.Reload();
        Assert.True(browser.Champions.Count > 100, $"expected the roster after Reload, got {browser.Champions.Count}");
        browser.Dispose();
    }

    [Fact]
    public void TheHostCreatesThePickerForTheWindow()
    {
        var vm = new MainWindowViewModel();
        Assert.Null(vm.MeshPreview.Browser);
        vm.OpenCharacterBrowserCommand.Execute(null);   // the menu: opens the editor window, with the picker in it
        Assert.NotNull(vm.MeshPreview.Browser);
        var first = vm.MeshPreview.Browser;
        vm.OpenCharacterBrowserCommand.Execute(null);
        Assert.Same(first, vm.MeshPreview.Browser);      // created once, not per opening
    }

    // ===================================================== the wiring is where it says it is

    [Fact]
    public void TheWindowHostsThePickerAndTheOutliner()
    {
        var window = Source("src", "ReyEngine.App", "Views", "MeshPreviewWindow.axaml");
        var main = Source("src", "ReyEngine.App", "Views", "MainWindow.axaml");
        var host = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.Characters.cs");
        if (window is null || main is null || host is null) return;

        Assert.Contains("ColumnDefinitions=\"250,4,*,4,340\"", window);
        Assert.Contains("HighlightSubmeshes=\"{Binding HighlightedSubmeshes}\"", window);
        Assert.Contains("Command=\"{Binding Browser.OpenSkinCommand}\"", window);
        Assert.Contains("SelectedItem=\"{Binding SelectedSubmesh}\"", window);
        // ONE submesh list: the card left the Scene tab when it became the outliner.
        Assert.Equal(1, window.Split("Text=\"SUBMESHES\"").Length - 1);

        Assert.Contains("Header=\"Character Editor…\"", main);
        Assert.DoesNotContain("Views.CharacterBrowserWindow.Show(", host);   // nothing opens the old window
        Assert.Contains("EnsureCharacterBrowser();", host);
    }
}
