using ReyEngine.App.ViewModels;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M728: a chroma previewed as the skin it recolours. Lillia's skin 49 (Petals of Spring, Rose Quartz) showed
/// skin 46, because every read the character window made worked the skin bin out from the MESH's folder - and a
/// chroma ships no mesh of its own.
/// </summary>
public sealed class ChromaPreviewTests
{
    private const string Champions = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Champions";
    private const string LilliaMesh = "ASSETS/Characters/Lillia/Skins/Skin46/Lillia_Skin46.skn";
    private const string ChromaBin = "data/characters/lillia/skins/skin49.bin";
    private const string BaseBin = "data/characters/lillia/skins/skin46.bin";

    private static readonly Lazy<HashDatabase?> Database = new(() =>
    {
        try { return new HashSyncService().LoadLocal(_ => { }); }
        catch { return null; }
    });

    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        return dir?.FullName;
    }

    private static string? Source(params string[] parts)
    {
        if (RepoRoot() is not { } root) return null;
        string path = Path.Combine(new[] { root }.Concat(parts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static IEnumerable<int> Occurrences(string text, string needle)
    {
        for (int at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            yield return at;
    }

    // ===================================================== the rule

    [Fact]
    public void TheSkinThatWasChosenWinsOverTheOneInTheMeshFolder()
    {
        Assert.Equal(ChromaBin, SkinPaths.PreviewBinPath(ChromaBin, LilliaMesh));

        // Nothing chosen - a bare .skn clicked in the asset tree - and the folder is the best there is.
        Assert.Equal(BaseBin, SkinPaths.PreviewBinPath(null, LilliaMesh), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(BaseBin, SkinPaths.PreviewBinPath("  ", LilliaMesh), StringComparer.OrdinalIgnoreCase);
        Assert.Null(SkinPaths.PreviewBinPath(null, null));
    }

    // ===================================================== the data that makes the rule necessary

    [Fact]
    public void LilliasRoseQuartzChromaDrawsSkin46sMeshWithTexturesOfItsOwn()
    {
        if (!Directory.Exists(Champions) || Database.Value is not { } database) return;
        string wad = Path.Combine(Champions, "Lillia.wad.client");
        if (!File.Exists(wad)) return;

        using var archive = WadArchive.Open(wad, new WadPathResolver(database));
        byte[]? Bin(string path) => archive.TryGetEntry(HashAlgorithms.WadPath(path), out _)
            ? archive.Extract(HashAlgorithms.WadPath(path))
            : null;
        Assert.True(Bin(ChromaBin) is not null && Bin(BaseBin) is not null,
            "Lillia no longer ships skins 46 and 49 - pick another skin and one of its chromas from skins.json");

        string? WadPath(ulong hash) => database.TryGetPath(hash, out var path) ? path : null;
        string? BinName(uint hash) => database.TryGetBinName(hash, out var name) ? name : null;

        var chroma = CharacterSkinReader.Read(Bin(ChromaBin)!, ChromaBin, WadPath);
        Assert.NotNull(chroma);

        // No mesh of its own: the chroma names its base skin's...
        Assert.Equal(LilliaMesh, chroma!.MeshPath, StringComparer.OrdinalIgnoreCase);
        // ...so the folder rule lands on the base skin...
        Assert.Equal(BaseBin, SkinPaths.BinPathForSkn(chroma.MeshPath!), StringComparer.OrdinalIgnoreCase);

        // ...whose look is not the chroma's.
        Assert.Contains("/skin49/", chroma.TexturePath ?? "", StringComparison.OrdinalIgnoreCase);
        var chromaLook = ChampionMaterialResolver.Resolve(Bin(ChromaBin)!, BinName, WadPath);
        var baseLook = ChampionMaterialResolver.Resolve(Bin(BaseBin)!, BinName, WadPath);
        Assert.Contains("/skin49/", chromaLook.For("Bird") ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/skin46/", baseLook.For("Bird") ?? "", StringComparison.OrdinalIgnoreCase);
    }

    // ===================================================== the load path hands the chosen bin on

    [Fact]
    public void TheCharacterBrowserOpensTheSkinWithItsOwnBin()
    {
        if (Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.Characters.cs") is not { } characters) return;
        Assert.Contains("_ = LoadMeshPreviewAsync(entry, skin.BinPath);", characters);
        Assert.Contains("TryLoadMaterialBin(entry, alsoRawBin: true, skinBin: skin.BinPath);", characters);
        // and the D3D11 scene reads that bin rather than the mesh's folder
        Assert.Contains("SkinPaths.PreviewBinPath(skinBin, skn.Path)", characters);
    }

    [Fact]
    public void APlacedPropOpensWithThePlacementsOwnSkinBin()
    {
        if (Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs") is not { } main) return;
        int start = main.IndexOf("private void OpenPropInCharacterEditor()", StringComparison.Ordinal);
        Assert.True(start >= 0, "OpenPropInCharacterEditor moved - re-point this guard");
        int end = main.IndexOf("partial void OnSelectedPropNodeChanged", start, StringComparison.Ordinal);
        string body = main[start..end];
        Assert.Contains("_ = LoadMeshPreviewAsync(entry, skinBin);", body);
        Assert.Contains("TryLoadMaterialBin(entry, alsoRawBin: true, skinBin: skinBin);", body);
    }

    [Fact]
    public void EveryReadTheLoadMakesGoesToTheSameBin()
    {
        if (Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs") is not { } main) return;
        int start = main.IndexOf("private async Task LoadMeshPreviewAsync(WadAssetEntry entry, string? skinBin = null)",
            StringComparison.Ordinal);
        Assert.True(start >= 0, "LoadMeshPreviewAsync no longer takes the chosen skin bin");
        int end = main.IndexOf("// M88: cache the last-loaded backdrop", start, StringComparison.Ordinal);
        string body = main[start..end];
        foreach (string read in new[]
                 {
                     "TryLoadPreviewDiffuse(entry, m, binPath)",
                     "TryLoadChampionVfxWithResources(entry, binPath)",
                     "LoadSubmeshRules(entry, binPath)",
                     "BuildCharacterDx11Scene(entry, skinBin: binPath)",
                     "TryLoadIdleEffects(entry, binPath)",
                     "TryLoadVoiceEvents(entry, binPath)",
                 })
            Assert.Contains(read, body);
    }

    [Fact]
    public void AMaterialEditRebuildsTheSkinTheWindowIsShowing()
    {
        if (Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.CharacterMaterials.cs") is not { } materials) return;
        // The live preview compares the edited bin to the SHOWN bin, and the D3D11 rebuild reads that bin.
        Assert.Contains("_previewSkinBin is { Length: > 0 } binPath", materials);
        Assert.Contains("BuildCharacterDx11Scene(skn, bytes, state, skinBin)", materials);
        // and the two leave together when the window stops showing a skin
        int forget = materials.IndexOf("private void ForgetPreviewSkin()", StringComparison.Ordinal);
        Assert.True(forget >= 0);
        Assert.Contains("_previewSkinBin = null;", materials[forget..materials.IndexOf('}', forget)]);
    }

    [Fact]
    public void NoCharacterWindowReadWorksTheSkinOutFromTheMeshFolder()
    {
        // Every place the App still calls the folder rule directly. The main viewport's .skn loader is the one that
        // never has a chosen skin; every character-window read goes through PreviewBinPath, so a new read that
        // re-derives the bin is caught here instead of by the next chroma.
        if (RepoRoot() is not { } root) return;
        var hits = Directory.EnumerateFiles(Path.Combine(root, "src", "ReyEngine.App"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => Occurrences(File.ReadAllText(file), "BinPathForSkn(").Select(at => (File: file, At: at)))
            .ToList();

        var hit = Assert.Single(hits);
        Assert.Equal("MainWindowViewModel.cs", Path.GetFileName(hit.File));
        string main = File.ReadAllText(hit.File);
        int loader = main.IndexOf("TryLoadTextures(WadAssetEntry skn, MeshAsset mesh)", StringComparison.Ordinal);
        int next = loader < 0 ? -1 : main.IndexOf("/// <summary>", loader, StringComparison.Ordinal);
        Assert.True(loader >= 0 && hit.At > loader && hit.At < next,
            "the remaining folder-rule call is no longer the main viewport's .skn loader");
    }

    // ===================================================== the picker names a chroma

    [Fact]
    public void AChromaIsListedByItsOwnNameAndMarkedAsOne()
    {
        byte[] champions = """[ { "id": 876, "alias": "Lillia", "name": "Lillia", "description": "The Bashful Bloom", "roles": [] } ]"""u8.ToArray();
        byte[] skins = """
            {
              "876000": { "id": 876000, "name": "Lillia", "isBase": true },
              "876046": { "id": 876046, "name": "Petals of Spring Lillia", "isBase": false,
                          "chromas": [ { "id": 876049, "name": "Petals of Spring Lillia (Rose Quartz)" } ] }
            }
            """u8.ToArray();
        var catalog = ClientNameCatalog.FromJson(champions, skins);

        var chroma = catalog.Skin("Lillia", 49);
        Assert.NotNull(chroma);
        Assert.Equal("Petals of Spring Lillia (Rose Quartz)", chroma!.DisplayName);
        Assert.True(chroma.IsChroma);
        Assert.Equal(46, chroma.ChromaOf);
        Assert.False(catalog.Skin("Lillia", 46)!.IsChroma);

        var row = new SkinRowViewModel(new CharacterSkinRef(49, ChromaBin), chroma);
        Assert.Equal("Petals of Spring Lillia (Rose Quartz)", row.Name);
        Assert.Equal("chroma", row.Badge);
        Assert.Equal("base", new SkinRowViewModel(new CharacterSkinRef(0, "data/characters/lillia/skins/skin0.bin"),
            catalog.Skin("Lillia", 0)).Badge);
    }

    [Fact]
    public void TheInstalledClientDataNamesLilliasChromas()
    {
        if (!Directory.Exists(Champions)) return;
        string? root = ClientNameCatalog.InstallRootFromChampions(Champions);
        if (root is null || ClientNameCatalog.FindDataWad(root) is not { } dataWad) return;   // a Game-only install

        var catalog = ClientNameCatalog.Load(dataWad);
        var petals = catalog.Skin("Lillia", 46);
        var roseQuartz = catalog.Skin("Lillia", 49);
        Assert.NotNull(petals);
        Assert.NotNull(roseQuartz);
        Assert.False(petals!.IsChroma);
        Assert.Equal(46, roseQuartz!.ChromaOf);
        Assert.StartsWith(petals.DisplayName, roseQuartz.DisplayName);
    }
}
