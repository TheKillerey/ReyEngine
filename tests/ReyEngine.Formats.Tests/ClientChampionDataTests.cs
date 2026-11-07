using Avalonia.Media;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Characters;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M870: the League client's champion metadata and art behind the Character window's champion grid, skin cards, chroma swatches and ability
/// bar. The service is proven on the real plugin WADs (the only place "Kalista has skin 24 with eight chromas" has an answer) and on the
/// behaviours that must hold without them: a missing client degrades to nothing, never to an exception.
/// </summary>
public sealed class ClientChampionDataTests
{
    private const string Install = @"C:\Riot Games\League of Legends";
    private const string Champions = Install + @"\Game\DATA\FINAL\Champions";

    private static string? DataWad => Directory.Exists(Install) ? ClientNameCatalog.FindDataWad(Install) : null;

    private static ClientChampionData? Real() => DataWad is { } wad ? ClientChampionData.Open(wad) : null;

    // ===================================================== paths and text (no install needed)

    [Theory]
    [InlineData("/lol-game-data/assets/ASSETS/Characters/Kalista/Skins/Skin24/Images/kalista_splash_tile_24.jpg",
        "plugins/rcp-be-lol-game-data/global/default/assets/characters/kalista/skins/skin24/images/kalista_splash_tile_24.jpg")]
    [InlineData("/lol-game-data/assets/v1/champion-icons/429.png", "plugins/rcp-be-lol-game-data/global/default/v1/champion-icons/429.png")]
    [InlineData("/lol-game-data/assets/ASSETS/Characters/Kalista/HUD/Icons2D/Kalista_Q.png",
        "plugins/rcp-be-lol-game-data/global/default/assets/characters/kalista/hud/icons2d/kalista_q.png")]
    [InlineData("v1/champion-summary.json", "plugins/rcp-be-lol-game-data/global/default/v1/champion-summary.json")]
    [InlineData("plugins/rcp-be-lol-game-data/global/default/V1/Champions/429.JSON", "plugins/rcp-be-lol-game-data/global/default/v1/champions/429.json")]
    public void ClientPathsMapToWadPaths(string client, string wad) => Assert.Equal(wad, ClientChampionData.ToWadPath(client));

    [Fact]
    public void AnEmptyPathMapsToNothing()
    {
        Assert.Equal("", ClientChampionData.ToWadPath(null));
        Assert.Equal("", ClientChampionData.ToWadPath("  "));
    }

    [Fact]
    public void TheAbilityCatalogAndTheNewServiceShareOneMapping()
    {
        // M867's catalogue used to carry its own copy of the prefix rule; one rule means an icon the bar asks for is the icon the definition found.
        string wad = ClientChampionData.ToWadPath("/lol-game-data/assets/ASSETS/Characters/Kalista/HUD/Icons2D/Kalista_W.png");
        Assert.Equal(ClientChampionData.WadRoot + "assets/characters/kalista/hud/icons2d/kalista_w.png", wad);
    }

    [Fact]
    public void DescriptionTagsAreStrippedToPlainText()
    {
        Assert.Equal("Kalista hurls a spear, dealing 90 physical damage to the first target hit.\n\nKalista can dash.",
            ClientChampionData.StripMarkup(
                "Kalista hurls a spear, dealing <physicalDamage>90 physical damage</physicalDamage> to the first target hit.<br><br>Kalista can dash."));
        Assert.Equal("Rend & more", ClientChampionData.StripMarkup("<spellName>Rend</spellName>  &amp; more"));
        Assert.Equal("a\nb", ClientChampionData.StripMarkup("a<br />b"));
        Assert.Equal("deals ... damage", ClientChampionData.StripMarkup("deals @TotalDamage@ damage"));
        Assert.Equal("1 < 2", ClientChampionData.StripMarkup("1 &lt; 2"));
        Assert.Equal("", ClientChampionData.StripMarkup(null));
        Assert.Equal("", ClientChampionData.StripMarkup("<br>"));
    }

    [Fact]
    public void RarityLosesTheClientsPrefixAndNoRarityIsNothing()
    {
        Assert.Equal("Epic", ClientChampionData.RarityLabel("kEpic"));
        Assert.Equal("Legendary", ClientChampionData.RarityLabel("kLegendary"));
        Assert.Null(ClientChampionData.RarityLabel("kNoRarity"));
        Assert.Null(ClientChampionData.RarityLabel(""));
        Assert.Null(ClientChampionData.RarityLabel(null));
    }

    // ===================================================== no client: nothing, and nothing throws

    [Fact]
    public void AMissingClientWadDegradesToEmptyAnswers()
    {
        using var data = ClientChampionData.Open(@"C:\this\does\not\exist\default-assets2.wad");
        Assert.False(data.IsAvailable);
        Assert.Empty(data.Champions);
        Assert.Null(data.Champion("Kalista"));
        Assert.Null(data.GetDetails(429));
        Assert.Null(data.ReadAsset(ClientChampionData.IconPathOf(429)));
        Assert.False(data.Exists(ClientChampionData.IconPathOf(429)));
    }

    [Fact]
    public void NoPathAtAllIsTheSameAsAMissingOne()
    {
        using var data = ClientChampionData.Open(null);
        Assert.False(data.IsAvailable);
        Assert.Empty(data.Champions);
        using var install = ClientChampionData.ForInstall(@"C:\no\such\install");
        Assert.Empty(install.Champions);
        Assert.Null(install.GetDetails(1));
    }

    [Fact]
    public void ACorruptWadCostsTheEntriesNotTheCaller()
    {
        string path = Path.Combine(Path.GetTempPath(), "m870-corrupt-" + Guid.NewGuid().ToString("N") + ".wad");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        try
        {
            using var data = ClientChampionData.Open(path);
            Assert.Empty(data.Champions);
            Assert.Null(data.GetDetails(429));
            Assert.Null(data.ReadAsset(ClientChampionData.IconPathOf(429)));
        }
        finally
        {
            // WadArchive.Open leaves the file open when the header is bad; let the finalizer release it before the delete
            GC.Collect(); GC.WaitForPendingFinalizers();
            try { File.Delete(path); } catch (IOException) { /* a temp file left behind is harmless */ }
        }
    }

    [Fact]
    public void AfterDisposeEveryReadIsNull()
    {
        if (Real() is not { } data) return;
        Assert.NotNull(data.ReadAsset(ClientChampionData.IconPathOf(429)));
        data.Dispose();
        Assert.Null(data.ReadAsset(ClientChampionData.IconPathOf(429)));
        Assert.False(data.Exists(ClientChampionData.IconPathOf(429)));
        data.Dispose();                                   // twice is fine
    }

    // ===================================================== the real plugin WADs

    [Fact]
    public void OnThisMachineTheClientDataIsThere()
    {
        // A skip and a pass look the same below; this one turns "the plugin is installed but the service found nothing" into a failure.
        if (DataWad is null) return;
        using var data = Real()!;
        Assert.True(data.IsAvailable);
        Assert.True(data.Champions.Count > 150, $"expected the roster, got {data.Champions.Count}");
    }

    [Fact]
    public void TheChampionListIsSortedByDisplayNameWithPortraitsThatExist()
    {
        if (Real() is not { } data) return;
        using (data)
        {
            var list = data.Champions;
            Assert.Equal(list.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).Select(c => c.Id), list.Select(c => c.Id));
            var kalista = data.Champion("Kalista")!;
            Assert.Equal(429, kalista.Id);
            Assert.Equal("Kalista", kalista.Name);
            Assert.Equal("the Spear of Vengeance", kalista.Title);
            Assert.NotEmpty(kalista.Roles);
            Assert.Equal(kalista, data.Champion(429));
            Assert.Equal("Twisted Fate", data.Champion("TwistedFate")!.Name);

            // every listed portrait is a real entry - a tile never shows a path that is not there
            int missing = list.Count(c => !data.Exists(c.IconPath));
            Assert.True(missing <= 2, $"{missing} champions list a portrait the client WAD does not hold");
            byte[] icon = data.ReadAsset(kalista.IconPath)!;
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, icon[..4]);          // PNG
        }
    }

    [Fact]
    public void KalistaHasFourSpellsAndAPassiveWithIconsThatResolveToEntries()
    {
        if (Real() is not { } data) return;
        using (data)
        {
            var kalista = data.GetDetails(429)!;
            Assert.Equal("Kalista", kalista.Name);
            Assert.Equal("the Spear of Vengeance", kalista.Title);

            Assert.NotNull(kalista.Passive);
            Assert.Equal("Martial Poise", kalista.Passive!.Name);
            Assert.True(kalista.Passive.Description.Length > 10);
            Assert.Equal(4, kalista.Spells.Count);
            Assert.Equal(new[] { "Q", "W", "E", "R" }, kalista.Spells.Select(s => s.Key));
            Assert.Equal("Pierce", kalista.Ability("Q")!.Name);

            foreach (var ability in kalista.Spells.Append(kalista.Passive))
            {
                Assert.False(string.IsNullOrWhiteSpace(ability.Name));
                Assert.NotNull(ability.IconPath);
                Assert.StartsWith(ClientChampionData.WadRoot + "assets/characters/kalista/hud/icons2d/", ability.IconPath);
                Assert.True(data.Exists(ability.IconPath), ability.IconPath);
                Assert.DoesNotContain("<", ability.Description);          // plain text: no tags left
                Assert.DoesNotContain(">", ability.Description);
            }
            Assert.Same(kalista.Passive, kalista.Ability("p"));
            Assert.Null(kalista.Ability("X:Something"));
            Assert.Null(kalista.Ability(""));
        }
    }

    [Fact]
    public void KalistaSkin24HasEightChromasWithTheirColours()
    {
        if (Real() is not { } data) return;
        using (data)
        {
            var skin = data.GetDetails(429)!.Skin(24)!;
            Assert.Equal(429024, skin.Id);
            Assert.Equal("Dawnbringer Kalista", skin.Name);
            Assert.False(skin.IsBase);
            Assert.Equal("Epic", skin.Rarity);
            Assert.NotNull(skin.TilePath);
            Assert.NotNull(skin.LoadScreenPath);
            Assert.True(data.Exists(skin.TilePath));
            Assert.True(data.Exists(skin.LoadScreenPath));
            Assert.Equal(new byte[] { 0xFF, 0xD8 }, data.ReadAsset(skin.TilePath)![..2]);       // JPEG

            Assert.Equal(8, skin.Chromas.Count);
            Assert.Equal(Enumerable.Range(25, 8), skin.Chromas.Select(c => c.Number));
            Assert.Equal(Enumerable.Range(429025, 8), skin.Chromas.Select(c => c.Id));
            var ruby = skin.Chromas[0];
            Assert.Equal("Dawnbringer Kalista (Ruby)", ruby.Name);
            Assert.Equal("#D33528", ruby.Colors[0]);
            Assert.All(skin.Chromas, c =>
            {
                Assert.NotEmpty(c.Colors);
                Assert.All(c.Colors, hex => Assert.True(Color.TryParse(hex, out _), hex));
                Assert.NotNull(c.ImagePath);
                Assert.True(data.Exists(c.ImagePath), c.ImagePath);
            });
        }
    }

    [Fact]
    public void ASkinWithNoChromasHasAnEmptyListAndTheBaseSkinIsFirst()
    {
        if (Real() is not { } data) return;
        using (data)
        {
            var kalista = data.GetDetails(429)!;
            Assert.Equal(0, kalista.Skins[0].Number);
            Assert.True(kalista.Skins[0].IsBase);
            Assert.Empty(kalista.Skins[0].Chromas);
            Assert.Equal(kalista.Skins.OrderBy(s => s.Number).Select(s => s.Number), kalista.Skins.Select(s => s.Number));
            Assert.All(kalista.Skins, s => Assert.Equal(429, s.Id / 1000));

            // and across the roster: a champion none of whose skins has a chroma still has details, never a failure
            var chromaless = data.Champions.Select(c => data.GetDetails(c.Id)).Where(d => d is not null && d.Skins.All(s => s.Chromas.Count == 0)).ToList();
            Assert.All(chromaless, d => Assert.NotEmpty(d!.Skins));
        }
    }

    [Fact]
    public void AnUnknownChampionHasNoDetailsAndTheAnswerIsRemembered()
    {
        if (Real() is not { } data) return;
        using (data)
        {
            Assert.Null(data.GetDetails(999999));
            Assert.Null(data.GetDetails(0));
            Assert.Null(data.GetDetails(-5));
            Assert.Same(data.GetDetails(429), data.GetDetails(429));
        }
    }

    [Fact]
    public void ManyThreadsAskingAtOnceGetOneAnswer()
    {
        if (Real() is not { } data) return;
        using (data)
        {
            var results = new ClientChampionDetails?[16];
            Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
            {
                _ = data.Champions;
                results[i] = data.GetDetails(429);
                Assert.NotNull(data.ReadAsset(ClientChampionData.IconPathOf(429)));
            });
            Assert.All(results, r => Assert.Same(results[0], r));
        }
    }

    // ===================================================== the rows the window binds

    [Fact]
    public void ASkinRowCarriesRarityTheChipTileAndItsParent()
    {
        byte[] champions = """[ { "id": 429, "alias": "Kalista", "name": "Kalista", "description": "the Spear of Vengeance", "roles": ["marksman"] } ]"""u8.ToArray();
        byte[] skins = """
            { "429000": { "id": 429000, "name": "Kalista", "isBase": true, "tilePath": "/lol-game-data/assets/ASSETS/Characters/Kalista/Skins/Base/Images/kalista_splash_tile_0.jpg", "rarity": "kNoRarity" },
              "429024": { "id": 429024, "name": "Dawnbringer Kalista", "isBase": false, "rarity": "kLegendary", "loadScreenPath": "/lol-game-data/assets/ASSETS/Characters/Kalista/Skins/Skin24/KalistaLoadScreen_24.jpg",
                          "chromas": [ { "id": 429025, "name": "Dawnbringer Kalista (Ruby)" } ] } }
            """u8.ToArray();
        var catalog = ClientNameCatalog.FromJson(champions, skins);

        var baseRow = new SkinRowViewModel(new CharacterSkinRef(0, "data/characters/kalista/skins/skin0.bin"), catalog.Skin("Kalista", 0));
        Assert.Equal("Base", baseRow.ChipText);
        Assert.False(baseRow.IsPremium);
        Assert.Null(baseRow.Rarity);
        Assert.False(baseRow.Tile.CanLoad);                      // no image service: a placeholder, not a failure

        var legendary = new SkinRowViewModel(new CharacterSkinRef(24, "data/characters/kalista/skins/skin24.bin"), catalog.Skin("Kalista", 24));
        Assert.Equal("Legendary", legendary.ChipText);
        Assert.True(legendary.IsPremium);
        Assert.True(legendary.HasChip);
        Assert.Contains("KalistaLoadScreen_24", legendary.LoadScreenPath);

        var chroma = new SkinRowViewModel(new CharacterSkinRef(25, "data/characters/kalista/skins/skin25.bin"), catalog.Skin("Kalista", 25));
        Assert.True(chroma.IsChroma);
        Assert.Equal(24, chroma.ChromaOf);
        Assert.False(chroma.HasChip);

        var noClient = new SkinRowViewModel(new CharacterSkinRef(3, "data/characters/kalistaspawn/skins/skin3.bin"), null);
        Assert.Equal("Skin 3", noClient.Name);
        Assert.False(noClient.IsChroma);
        Assert.Equal("", noClient.ChipText);
    }

    [Fact]
    public void ASwatchIsADiscOfTheClientsColoursSplitWhenThereAreTwo()
    {
        var row = new SkinRowViewModel(new CharacterSkinRef(25, "data/characters/kalista/skins/skin25.bin"), null);
        var one = new ChromaSwatchViewModel(row, new ClientChroma(429025, 25, "Ruby", new[] { "#D33528", "#D33528" }, null), null);
        Assert.IsType<SolidColorBrush>(one.Fill);
        Assert.Equal(Color.Parse("#D33528"), ((SolidColorBrush)one.Fill).Color);

        var two = new ChromaSwatchViewModel(row, new ClientChroma(429026, 26, "Duo", new[] { "#102030", "#FFEE59" }, null), null);
        var gradient = Assert.IsType<LinearGradientBrush>(two.Fill);
        Assert.Equal(4, gradient.GradientStops.Count);

        var none = new ChromaSwatchViewModel(row, null, null);
        Assert.NotNull(none.Fill);
        Assert.Equal(row.Name, none.Name);
        Assert.False(none.HasPicture);
        Assert.Empty(none.Colors);
    }

    private sealed class RecordingHost(string? gameDirectory) : ICharacterBrowserHost
    {
        public string? GameDirectory { get; } = gameDirectory;
        public IHashResolver? Resolver { get; } = ResolverOnce.Value;
        public readonly List<CharacterSkinInfo> Opened = new();
        public void OpenSkin(ChampionPackage champion, CharacterEntry character, CharacterSkinInfo skin) => Opened.Add(skin);
    }

    private static readonly Lazy<WadPathResolver?> ResolverOnce = new(() =>
    {
        try { return new WadPathResolver(new HashSyncService().LoadLocal(_ => { })); }
        catch { return null; }
    });

    private static CharacterBrowserViewModel? Browser(out RecordingHost host)
    {
        host = new RecordingHost(Install + @"\Game");
        if (!Directory.Exists(Champions) || ResolverOnce.Value is null) return null;
        var browser = new CharacterBrowserViewModel(host);
        return browser.Champions.Count > 0 ? browser : null;
    }

    [Fact]
    public void TheGridIsSortedByDisplayNameAndEveryClientChampionHasAPortraitHandle()
    {
        if (Browser(out _) is not { } browser) return;
        using (browser)
        {
            var names = browser.Champions.Select(c => c.Name).ToList();
            Assert.Equal(names.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase), names);
            var kalista = browser.Champions.First(c => c.Folder == "Kalista");
            Assert.Equal(429, kalista.ClientId);
            Assert.True(kalista.Icon.CanLoad);
            Assert.Equal("K", kalista.Initial);
            Assert.Contains("the Spear of Vengeance", kalista.Tooltip);
            // nothing was decoded to build the list: handles are lazy until a tile asks
            Assert.Equal(0, browser.Images.DecodeCount);
            Assert.Equal(0, browser.Images.CachedCount);
        }
    }

    [Fact]
    public async Task SkinCardsAreTheExistingBinsOnlyAndChromasAreSwatchesOfTheSelectedSkin()
    {
        if (Browser(out var host) is not { } browser) return;
        using (browser)
        {
            browser.SelectedChampion = browser.Champions.First(c => c.Folder == "Kalista");
            var character = browser.SelectedCharacter!;
            var binNumbers = character.Entry.Skins.Where(s => !s.IsRoot).Select(s => s.Number).ToHashSet();

            // never a dead entry: every card and every swatch is a skinN.bin the champion WAD (or project) holds
            Assert.NotEmpty(browser.SkinCards);
            Assert.All(browser.SkinCards, c => { Assert.Contains(c.Number, binNumbers); Assert.False(c.IsChroma); });
            Assert.Equal(browser.SkinCards.Select(c => c.Number).OrderBy(n => n), browser.SkinCards.Select(c => c.Number));
            Assert.Equal(0, browser.SkinCards[0].Number);                       // base first
            Assert.DoesNotContain(browser.SkinCards, c => c.Reference.IsRoot);
            Assert.Contains(browser.Skins, s => s.IsChroma);                    // the chromas exist as rows ...
            Assert.Empty(browser.Skins.Where(s => s.IsChroma && browser.SkinCards.Contains(s)));   // ... and are not cards

            var skin24 = browser.SkinCards.FirstOrDefault(c => c.Number == 24);
            if (skin24 is null) return;
            Assert.Equal("Epic", skin24.Rarity);

            browser.SelectedSkin = skin24;
            await browser.ChromasTask;                                          // the colours are read on a worker
            Assert.True(skin24.IsActive);
            Assert.Single(browser.SkinCards, c => c.IsActive);
            Assert.NotEmpty(browser.Chromas);
            Assert.All(browser.Chromas, c =>
            {
                Assert.Contains(c.Row.Number, binNumbers);
                Assert.Equal(24, c.Row.ChromaOf);
                Assert.NotEmpty(c.Colors);
                Assert.True(c.Picture.CanLoad);
            });
            Assert.True(browser.Chromas.Count <= 8);
            Assert.True(browser.HasChromas);

            // a chroma selected: its skin's card stays active, its swatch is the selected one, the swatches are not rebuilt
            var first = browser.Chromas[0];
            first.Row.Select!.Execute(first.Row);
            Assert.Same(first.Row, browser.SelectedSkin);
            Assert.True(skin24.IsActive);
            Assert.True(first.IsSelected);
            Assert.Single(browser.Chromas, c => c.IsSelected);
            Assert.Same(first, browser.Chromas[0]);
            Assert.Equal(skin24.Number, first.Row.ChromaOf);

            // loading came from the click alone - no Load button
            Assert.Single(host.Opened);
            Assert.Equal(first.Row.Number, int.Parse(host.Opened[0].BinPath.Split("skin").Last().Replace(".bin", "")));

            // the base skin has none
            browser.SelectedSkin = browser.SkinCards[0];
            await browser.ChromasTask;
            Assert.Empty(browser.Chromas);
            Assert.False(browser.HasChromas);
        }
    }

    [Fact]
    public void ClickingACardLoadsThatSkinAndTheHeaderFollowsTheLoadNotTheClick()
    {
        if (Browser(out var host) is not { } browser) return;
        using (browser)
        {
            browser.SelectedChampion = browser.Champions.First(c => c.Folder == "Kalista");
            Assert.False(browser.Header.HasChampion);                  // picking a champion loads nothing, so the banner stays empty

            var card = browser.SkinCards.First(c => c.Number == 0);
            card.Select!.Execute(card);
            Assert.Single(host.Opened);
            Assert.EndsWith("skin0.bin", host.Opened[0].BinPath);
            Assert.False(browser.Header.HasChampion);                  // asked for, not loaded yet: the banner does not claim it

            browser.NotifySkinLoaded(host.Opened[0].BinPath);          // the host: the skin is on stage
            Assert.True(browser.Header.HasChampion);
            Assert.Equal("Kalista", browser.Header.Name);
            Assert.Equal("the Spear of Vengeance", browser.Header.Title);
            Assert.Equal("Marksman", browser.Header.Roles);
            Assert.True(browser.Header.Art.CanLoad);
            Assert.Contains("loadscreen", browser.Header.Art.Path, StringComparison.OrdinalIgnoreCase);

            // another champion merely SELECTED: the banner still names what is on stage
            var other = browser.Champions.First(c => c.Folder == "Talon");
            browser.SelectedChampion = other;
            Assert.Equal("Kalista", browser.Header.Name);

            // a load that fails never notifies: the banner keeps describing the stage
            var talon = browser.SkinCards.First(c => c.Number == 0);
            talon.Select!.Execute(talon);
            Assert.Equal("Kalista", browser.Header.Name);
            browser.NotifySkinLoaded(host.Opened[1].BinPath);
            Assert.Equal("Talon", browser.Header.Name);

            // something else was loaded (a prop, a skin from the asset tree): no stale champion
            browser.NotifySkinLoaded("data/characters/other/skins/skin0.bin");
            Assert.False(browser.Header.HasChampion);
        }
    }

    [Fact]
    public async Task ASkinLoadDoesNotTakeTheSelectionOrTheCardsAway()
    {
        // Live bug (M870): every skin load made the editor list the install again, which cleared the selection - the SKINS card vanished.
        if (Browser(out var host) is not { } browser) return;
        using (browser)
        {
            browser.SelectedChampion = browser.Champions.First(c => c.Folder == "Kalista");
            var skin24 = browser.SkinCards.FirstOrDefault(c => c.Number == 24);
            if (skin24 is null) return;
            skin24.Select!.Execute(skin24);

            browser.Reload();                                         // what the host did on every load
            await browser.ChromasTask;
            Assert.Equal("Kalista", browser.SelectedChampion?.Folder);
            Assert.Equal("kalista", browser.SelectedCharacter?.Name, ignoreCase: true);
            Assert.Equal(24, browser.SelectedSkin?.Number);
            Assert.True(browser.HasSkinCards);
            Assert.Single(browser.SkinCards, c => c.IsActive);
            Assert.True(browser.HasChromas);
            Assert.Single(host.Opened);                               // a reload loads nothing by itself
        }
    }

    [Fact]
    public async Task AfterAReloadTheOldRowsStillDecodeThroughTheSameService()
    {
        if (Browser(out _) is not { } browser) return;
        using (browser)
        {
            var service = browser.Images;
            var row = browser.Champions.First(c => c.Folder == "Kalista");
            var before = browser.ClientData;
            browser.Reload();
            Assert.Same(service, browser.Images);                     // one service for the browser's life
            Assert.NotSame(before, browser.ClientData);               // pointed at the new data
            Assert.Same(browser.ClientData, row.Icon.CanLoad ? browser.Images.Data : null);
            await browser.ClientData.PrefetchAsync();
            Assert.NotEmpty(browser.ClientData.Champions);
        }
    }

    // ===================================================== the WADs are not held

    [Fact]
    public async Task TheClientWadsAreClosedAfterTheIdleTimeAndReopenedOnDemand()
    {
        if (Real() is not { } data) return;
        using (data)
        {
            data.IdleCloseMilliseconds = 200;
            Assert.False(data.IsOpen);                                // nothing opened until asked
            Assert.NotEmpty(data.Champions);
            Assert.Equal(1, data.OpenCount);
            Assert.True(data.IsOpen);

            // closed for real: the file can be opened with write sharing refused by nobody - here, simply that the idle timer let go
            for (int i = 0; i < 40 && data.IsOpen; i++) await Task.Delay(100);
            Assert.False(data.IsOpen);

            // what was parsed stays: no reopen to answer again
            Assert.NotEmpty(data.Champions);
            Assert.NotNull(data.GetDetails(429));                     // a new document: reopens
            Assert.Equal(2, data.OpenCount);
            Assert.NotNull(data.ReadAsset(ClientChampionData.IconPathOf(429)));
            Assert.Equal(2, data.OpenCount);                          // still inside the idle window: one open serves the burst
            data.Release();
            Assert.False(data.IsOpen);
            Assert.NotNull(data.GetDetails(429));                     // cached: no open
            Assert.False(data.IsOpen);
        }
    }

    [Fact]
    public void AFailedOpenIsNotTheAnswerForGood()
    {
        string path = Path.Combine(Path.GetTempPath(), "m870-retry-" + Guid.NewGuid().ToString("N") + ".wad");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        try
        {
            using var data = ClientChampionData.Open(path);
            Assert.Empty(data.Champions);
            Assert.False(data.IsOpen);
            Assert.True(data.IsAvailable);                            // the file is there; only the read failed
            // not cached as "the client has no such champion": details come back null WITHOUT being remembered, so a later try can succeed
            Assert.Null(data.GetDetails(429));
            Assert.Null(data.GetDetails(429));
        }
        finally
        {
            GC.Collect(); GC.WaitForPendingFinalizers();
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    [Fact]
    public void ACompanionFormStaysReachableThroughTheSameChampion()
    {
        if (Browser(out _) is not { } browser) return;
        using (browser)
        {
            browser.SelectedChampion = browser.Champions.First(c => c.Folder == "Kalista");
            Assert.True(browser.HasCompanions);
            var spawn = browser.Characters.FirstOrDefault(c => c.Name.Equals("KalistaSpawn", StringComparison.OrdinalIgnoreCase));
            if (spawn is null) return;
            browser.SelectedCharacter = spawn;
            Assert.NotEmpty(browser.SkinCards);                          // its skins are cards too
            Assert.Empty(browser.Chromas);                               // and the client lists no chromas for it
            Assert.All(browser.SkinCards, c => Assert.False(c.Tile.CanLoad));
        }
    }

    [Fact]
    public void WithoutAClientTheBrowserStillListsAndLoads()
    {
        // a game folder with no Plugins/ beside it: the same lists, folder names, no art, nothing thrown
        string root = Path.Combine(Path.GetTempPath(), "m870-noclient-" + Guid.NewGuid().ToString("N"));
        string champions = Path.Combine(root, "Game", "DATA", "FINAL", "Champions");
        Directory.CreateDirectory(champions);
        try
        {
            using var browser = new CharacterBrowserViewModel(new RecordingHost(Path.Combine(root, "Game")));
            Assert.False(browser.ClientData.IsAvailable);
            Assert.Empty(browser.SkinCards);
            Assert.False(browser.HasSkinCards);
            Assert.False(browser.HasChromas);
            Assert.False(browser.Header.HasChampion);
            Assert.False(browser.Images.Data.IsAvailable);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
