using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M609: which characters exist and what each skin is, measured against the shipped champion WADs.
///
/// <para>Every assertion here is against real Riot data rather than a fixture, because the whole point of
/// the catalogue is to describe what Riot actually ships — a fixture would only prove the reader agrees
/// with my idea of a skin bin. The tests skip silently without a game install, following the house
/// pattern for the rest of the suite.</para>
/// </summary>
public sealed class CharacterCatalogTests
{
    private const string Champions = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Champions";

    private static bool Installed => Directory.Exists(Champions);
    private static string Wad(string champion) => Path.Combine(Champions, champion + ".wad.client");

    /// <summary>The catalogue reads RESOLVED paths, so a WAD opened without the hash dictionary lists
    /// nothing at all. Shared across the class because loading it is the expensive part.</summary>
    private static readonly Lazy<WadPathResolver?> Resolver = new(() =>
    {
        try { return new WadPathResolver(new HashSyncService().LoadLocal(_ => { })); }
        catch { return null; }
    });

    private static WadArchive? Open(string champion)
    {
        string path = Wad(champion);
        if (!File.Exists(path) || Resolver.Value is not { } resolver) return null;
        var archive = WadArchive.Open(path, resolver);
        // No dictionary, no paths - and every assertion below would fail for a reason that has nothing
        // to do with the catalogue. Skip instead.
        return archive.Entries.Any(e => e.IsResolved) ? archive : null;
    }

    // ===================================================== the champion list

    [Fact]
    public void EveryChampionWadIsListedAndNoLocaleWadIs()
    {
        if (!Installed) return;
        var champions = CharacterCatalog.Champions(Champions);

        Assert.True(champions.Count > 100, $"expected the full roster, got {champions.Count}");
        Assert.Contains(champions, c => c.Name.Equals("Ahri", StringComparison.OrdinalIgnoreCase));

        // Ahri.en_US.wad.client sits right beside Ahri.wad.client and holds voice-over only. Listing it
        // would put a second, empty "Ahri" in the browser.
        Assert.DoesNotContain(champions, c => c.Name.Contains('.'));
        Assert.All(champions, c => Assert.True(File.Exists(c.WadPath)));
    }

    [Fact]
    public void ChampionsAreInAlphabeticalOrder()
    {
        if (!Installed) return;
        var names = CharacterCatalog.Champions(Champions).Select(c => c.Name).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase), names);
    }

    [Fact]
    public void AMissingFolderIsAnEmptyListRatherThanAThrow()
    {
        Assert.Empty(CharacterCatalog.Champions(Path.Combine(Path.GetTempPath(), "no-such-champions-dir")));
    }

    // ===================================================== the characters inside one champion

    [Fact]
    public void AChampionWadHoldsMoreThanTheChampion()
    {
        // Measured: 40+ champion WADs ship several characters. Elise's spider form is a separate
        // character with its own skins and mesh, and a browser that showed only "Elise" could not reach it.
        if (Open("Elise") is not { } archive) return;
        using (archive)
        {
            var characters = CharacterCatalog.Characters(archive, "Elise");

            Assert.Contains(characters, c => c.Name.Equals("elise", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(characters, c => c.Name.Equals("elisespider", StringComparison.OrdinalIgnoreCase));
            Assert.All(characters, c => Assert.NotEmpty(c.Skins));
        }
    }

    [Fact]
    public void TheChampionIsListedFirstAndItsJadeDoubleLast()
    {
        // Ahri ships as ahri + jade_ahri. The jade variant is an Arena copy of everything above it, so it
        // must never be the first thing offered when someone opens the champion.
        if (Open("Ahri") is not { } archive) return;
        using (archive)
        {
            var characters = CharacterCatalog.Characters(archive, "Ahri");

            Assert.Equal("ahri", characters[0].Name, ignoreCase: true);
            Assert.Equal(CharacterKind.Champion, characters[0].Kind);
            Assert.Equal(CharacterKind.Jade, characters[^1].Kind);
            Assert.StartsWith("jade_", characters[^1].Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SkinsAreNumberedAndOrderedAndRootIsMarked()
    {
        if (Open("Ahri") is not { } archive) return;
        using (archive)
        {
            var ahri = CharacterCatalog.Characters(archive, "Ahri")
                .First(c => c.Name.Equals("ahri", StringComparison.OrdinalIgnoreCase));

            Assert.True(ahri.Skins.Count > 50, $"Ahri ships many skins, got {ahri.Skins.Count}");
            Assert.Equal(ahri.Skins.Select(s => s.Number).OrderBy(n => n), ahri.Skins.Select(s => s.Number));
            Assert.Contains(ahri.Skins, s => s.Number == 0);
            Assert.Single(ahri.Skins, s => s.IsRoot);      // root.bin, exactly one, sorted to the front
            Assert.True(ahri.Skins[0].IsRoot);
        }
    }

    [Fact]
    public void OnlySkinBinsAreListedAsSkins()
    {
        // A champion WAD also holds data/characters/<name>/<name>.bin and animations/*.bin. Both would
        // parse as "a bin under characters" and neither is a skin.
        if (Open("Ahri") is not { } archive) return;
        using (archive)
        {
            foreach (var character in CharacterCatalog.Characters(archive, "Ahri"))
                Assert.All(character.Skins, s => Assert.Contains("/skins/", s.BinPath, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Theory]
    [InlineData("jade_ahri", "Ahri", CharacterKind.Jade)]
    [InlineData("ahri", "Ahri", CharacterKind.Champion)]
    [InlineData("AHRI", "ahri", CharacterKind.Champion)]
    [InlineData("annietibbers", "Annie", CharacterKind.Companion)]
    [InlineData("ahri", null, CharacterKind.Companion)]
    public void TheKindOfACharacterIsDecidedByItsName(string character, string? champion, CharacterKind expected) =>
        Assert.Equal(expected, CharacterCatalog.KindOf(character, champion));

    // ===================================================== reading one skin

    private static CharacterSkinInfo? ReadSkin(WadArchive archive, string binPath) =>
        archive.TryGetEntry(HashAlgorithms.WadPath(binPath), out _)
            ? CharacterSkinReader.Read(archive.Extract(HashAlgorithms.WadPath(binPath)), binPath,
                h => archive.TryGetEntry(h, out var e) && e.IsResolved ? e.Path : null)
            : null;

    [Fact]
    public void ASkinNamesItsMeshSkeletonAndTexture()
    {
        if (Open("Ahri") is not { } archive) return;
        using (archive)
        {
            var skin = ReadSkin(archive, "data/characters/ahri/skins/skin0.bin");
            Assert.NotNull(skin);

            Assert.Equal(0, skin!.Number);
            Assert.Equal("Ahri", skin.CodeName);
            Assert.EndsWith(".skn", skin.MeshPath!, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".skl", skin.SkeletonPath!, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".tex", skin.TexturePath!, StringComparison.OrdinalIgnoreCase);
            Assert.True(skin.IsLoadable);
        }
    }

    [Fact]
    public void TheCodeNameIsTheSkinNotTheChampion()
    {
        // skin0 answers "Ahri", which is why the champion name alone cannot label a skin list. Later
        // skins carry their own code names, and that is the only naming the GAME data has.
        if (Open("Ahri") is not { } archive) return;
        using (archive)
        {
            var first = ReadSkin(archive, "data/characters/ahri/skins/skin1.bin");
            var second = ReadSkin(archive, "data/characters/ahri/skins/skin2.bin");
            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.NotEqual(first!.CodeName, second!.CodeName);
            Assert.NotEmpty(first.CodeName);
        }
    }

    [Fact]
    public void SubmeshesHiddenAtLoadAreSplitOutOfTheOneStringRiotWritesThemIn()
    {
        if (Open("Ahri") is not { } archive) return;
        using (archive)
        {
            // Riot writes "Tail_Large Body_Proxy" - one space-separated string, not a list.
            var skin = ReadSkin(archive, "data/characters/ahri/skins/skin0.bin");
            Assert.NotNull(skin);
            Assert.True(skin!.InitiallyHiddenSubmeshes.Count >= 2);
            Assert.All(skin.InitiallyHiddenSubmeshes, s => Assert.DoesNotContain(' ', s));
        }
    }

    [Fact]
    public void PerSubmeshTextureOverridesComeBackWithTheirSubmeshNames()
    {
        if (Open("Ahri") is not { } archive) return;
        using (archive)
        {
            var skin = ReadSkin(archive, "data/characters/ahri/skins/skin0.bin");
            Assert.NotNull(skin);
            Assert.NotEmpty(skin!.MaterialOverrides);
            Assert.All(skin.MaterialOverrides, o => Assert.NotEmpty(o.Submesh));
            Assert.Contains(skin.MaterialOverrides, o => o.TexturePath is { Length: > 0 });
        }
    }

    [Fact]
    public void RootBinIsReadableButNotLoadable()
    {
        // Every champion ships one. It carries shared defaults and no mesh, so offering it as something
        // to open would be offering an empty viewport.
        if (Open("Ahri") is not { } archive) return;
        using (archive)
        {
            var root = ReadSkin(archive, "data/characters/ahri/skins/root.bin");
            Assert.NotNull(root);
            Assert.Equal(-1, root!.Number);
            Assert.False(root.IsLoadable);
            Assert.Null(root.MeshPath);
        }
    }

    [Fact]
    public void PathsThatBecameWadLinksInPatch1617StillRead()
    {
        // M590: simpleSkin/skeleton/texture are WadChunkLinks now. Reading them as strings returns
        // nothing at all, and a skin with no mesh path looks exactly like a skin that does not exist.
        if (Open("Lux") is not { } archive) return;
        using (archive)
        {
            var withResolver = ReadSkin(archive, "data/characters/lux/skins/skin0.bin");
            var withoutResolver = CharacterSkinReader.Read(
                archive.Extract(HashAlgorithms.WadPath("data/characters/lux/skins/skin0.bin")),
                "data/characters/lux/skins/skin0.bin");

            Assert.NotNull(withResolver);
            Assert.NotNull(withResolver!.MeshPath);
            Assert.StartsWith("assets/", withResolver.MeshPath!, StringComparison.OrdinalIgnoreCase);

            // Without a resolver the link cannot be NAMED, but it is still addressable: BinTexturePath
            // hands back the 0x hex reference the rest of the app looks assets up by (M592). What must
            // never happen is a fabricated path or a silent empty - the first loads the wrong asset, the
            // second makes a skin that exists look like one that does not.
            Assert.NotNull(withoutResolver);
            Assert.NotNull(withoutResolver!.TexturePath);
            Assert.StartsWith("0x", withoutResolver.TexturePath!, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                ReyEngine.Formats.Meta.BinTexturePath.HashOfReference(withResolver.TexturePath),
                ReyEngine.Formats.Meta.BinTexturePath.HashOfReference(withoutResolver.TexturePath));
        }
    }

    [Fact]
    public void WithoutAHashDictionaryTheCatalogueFindsNothing()
    {
        // Worth stating out loud: the catalogue works off resolved paths, so an archive opened with no
        // dictionary lists zero characters. That is not a bug to route around - it is why the app hands
        // its resolver in - but it does mean "no characters" can mean "no dictionary".
        if (!Installed || !File.Exists(Wad("Ahri"))) return;
        using var raw = WadArchive.Open(Wad("Ahri"));
        if (raw.Entries.Any(e => e.IsResolved)) return;    // an archive that resolves on its own proves nothing
        Assert.Empty(CharacterCatalog.Characters(raw, "Ahri"));
    }

    [Fact]
    public void ABinThatIsNotASkinReadsAsNullRatherThanThrowing()
    {
        Assert.Null(CharacterSkinReader.Read(new byte[] { 1, 2, 3, 4 }, "junk.bin"));
        Assert.Null(CharacterSkinReader.Read(Array.Empty<byte>(), "empty.bin"));
    }

    [Theory]
    [InlineData("data/characters/ahri/skins/skin0.bin", 0)]
    [InlineData("data/characters/ahri/skins/skin42.bin", 42)]
    [InlineData("data/characters/ahri/skins/skin301.bin", 301)]
    [InlineData("data/characters/ahri/skins/root.bin", -1)]
    [InlineData("skin.bin", -1)]
    public void TheSkinNumberComesFromTheFileName(string path, int expected) =>
        Assert.Equal(expected, CharacterSkinReader.NumberFromBinPath(path));

    // ===================================================== the metadata tags

    [Fact]
    public void TheSkinLineTagIsReadWhenRiotWroteOne()
    {
        if (Open("Ahri") is not { } archive) return;
        using (archive)
        {
            var skin = ReadSkin(archive, "data/characters/ahri/skins/skin1.bin");
            Assert.NotNull(skin);
            Assert.NotNull(skin!.SkinLine);
            Assert.Equal("female", skin.Tag("gender"));
            Assert.Null(skin.Tag("nosuchtag"));
        }
    }

    // ===================================================== the client names

    [Fact]
    public void TheClientDataGivesTheMarketingNamesTheGameFilesDoNot()
    {
        if (!Installed) return;
        string? root = ClientNameCatalog.InstallRootFromChampions(Champions);
        Assert.NotNull(root);
        string? dataWad = ClientNameCatalog.FindDataWad(root!);
        if (dataWad is null) return;      // a Game-only install: names simply stay as code names

        var catalog = ClientNameCatalog.Load(dataWad);
        Assert.True(catalog.ChampionCount > 100, $"expected the roster, got {catalog.ChampionCount}");
        Assert.True(catalog.SkinCount > 1000, $"expected thousands of skins, got {catalog.SkinCount}");

        // The name the folder does not give you.
        Assert.Equal("Twisted Fate", catalog.Champion("TwistedFate")?.DisplayName);
        Assert.Equal("Ahri", catalog.Champion("Ahri")?.DisplayName);

        var baseAhri = catalog.Skin("Ahri", 0);
        Assert.NotNull(baseAhri);
        Assert.True(baseAhri!.IsBase);
        Assert.Equal(0, baseAhri.Number);

        var skinOne = catalog.Skin("Ahri", 1);
        Assert.NotNull(skinOne);
        Assert.False(skinOne!.IsBase);
        Assert.NotEqual(baseAhri.DisplayName, skinOne.DisplayName);
    }

    [Fact]
    public void AMissingClientInstallIsAnEmptyCatalogueRatherThanAFailure()
    {
        // The names are enrichment. Losing them must never stop a character being listed or opened.
        var catalog = ClientNameCatalog.Load(null);
        Assert.Equal(0, catalog.ChampionCount);
        Assert.Null(catalog.Champion("Ahri"));
        Assert.Null(catalog.Skin("Ahri", 0));

        Assert.Null(ClientNameCatalog.FindDataWad(Path.Combine(Path.GetTempPath(), "not-a-league-install")));
        Assert.Equal(0, ClientNameCatalog.Load(Path.Combine(Path.GetTempPath(), "nope.wad")).SkinCount);
    }
}
