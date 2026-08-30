using System.Reflection;
using System.Text.RegularExpressions;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Characters;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M610: the character browser — the front door to a champion.
///
/// <para>The listing behaviour is asserted against the real install because that is the only place the
/// question "does Elise show her spider form" has an answer. The rest is the class of failure a build
/// cannot see: a binding pointing at a property that does not exist shows nothing and stays green.</para>
/// </summary>
public sealed class CharacterBrowserTests
{
    private const string Champions = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Champions";
    private const string GameDirectory = @"C:\Riot Games\League of Legends\Game";

    private static bool Installed => Directory.Exists(Champions);

    private static readonly Lazy<WadPathResolver?> Resolver = new(() =>
    {
        try { return new WadPathResolver(new HashSyncService().LoadLocal(_ => { })); }
        catch { return null; }
    });

    private sealed class FakeHost(string? gameDirectory) : ICharacterBrowserHost
    {
        public string? GameDirectory { get; } = gameDirectory;
        public IHashResolver? Resolver => CharacterBrowserTests.Resolver.Value;
        public readonly List<(ChampionPackage Champion, CharacterEntry Character, CharacterSkinInfo Skin)> Opened = new();

        public void OpenSkin(ChampionPackage champion, CharacterEntry character, CharacterSkinInfo skin) =>
            Opened.Add((champion, character, skin));
    }

    /// <summary>A browser over the real install, or null when it (or the hash dictionary) is missing.</summary>
    private static CharacterBrowserViewModel? Browser(out FakeHost host)
    {
        host = new FakeHost(GameDirectory);
        if (!Installed || Resolver.Value is null) return null;
        var browser = new CharacterBrowserViewModel(host);
        return browser.Champions.Count > 0 ? browser : null;
    }

    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        return dir?.FullName;
    }

    // ===================================================== the bindings resolve

    [Fact]
    public void EveryBindingInTheWindowResolvesToSomethingThatExists()
    {
        if (RepoRoot() is not { } root) return;
        string xaml = Path.Combine(root, "src", "ReyEngine.App", "Views", "CharacterBrowserWindow.axaml");
        if (!File.Exists(xaml)) return;

        Type[] contexts =
        {
            typeof(CharacterBrowserViewModel),
            typeof(ChampionRowViewModel),
            typeof(CharacterRowViewModel),
            typeof(SkinRowViewModel),
        };

        var missing = new List<string>();
        foreach (Match m in Regex.Matches(File.ReadAllText(xaml), @"\{Binding\s+!?([A-Za-z_][A-Za-z0-9_]*)"))
        {
            string path = m.Groups[1].Value;
            if (path is "Binding") continue;
            if (!contexts.Any(t => t.GetProperty(path, BindingFlags.Public | BindingFlags.Instance) is not null))
                missing.Add(path);
        }

        Assert.True(missing.Count == 0,
            "bound in CharacterBrowserWindow.axaml but on none of the data contexts: "
            + string.Join(", ", missing.Distinct()));
    }

    [Fact]
    public void TheMenuCommandExists() =>
        Assert.NotNull(typeof(MainWindowViewModel).GetProperty("OpenCharacterBrowserCommand"));

    [Fact]
    public void TheEditorIsAValidHostForTheBrowser() =>
        Assert.True(typeof(ICharacterBrowserHost).IsAssignableFrom(typeof(MainWindowViewModel)));

    // ===================================================== without a game folder

    [Fact]
    public void WithNoGameFolderTheBrowserSaysSoRatherThanShowingAnEmptyList()
    {
        // An empty list with no explanation reads as "this champion has nothing", which is the wrong
        // problem to go and investigate.
        var browser = new CharacterBrowserViewModel(new FakeHost(null));

        Assert.Empty(browser.Champions);
        Assert.Contains("game folder", browser.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OnThisMachineTheBrowserActuallyRuns()
    {
        // Every test below skips when the install or the hash dictionary is missing, which is right on a
        // build machine and dangerous here: a skip and a pass look identical. This one turns a silent
        // skip into a visible failure whenever the install IS present.
        if (!Installed) return;
        Assert.NotNull(Resolver.Value);
        Assert.NotNull(Browser(out _));
    }

    // ===================================================== the champion list

    [Fact]
    public void TheWholeRosterIsListedWithDisplayNames()
    {
        if (Browser(out _) is not { } browser) return;
        using (browser)
        {
            Assert.True(browser.Champions.Count > 100, $"expected the roster, got {browser.Champions.Count}");

            // "Twisted Fate" is the name; TwistedFate is the folder. Both have to be visible, because one
            // is what you look for and the other is what the files are called.
            var tf = browser.Champions.FirstOrDefault(c => c.Folder.Equals("TwistedFate", StringComparison.OrdinalIgnoreCase));
            if (tf is not null && tf.Name != tf.Folder)
            {
                Assert.Equal("Twisted Fate", tf.Name);
                Assert.True(tf.ShowFolder);
            }
        }
    }

    [Fact]
    public void SearchFiltersOnBothTheNameAndTheFolder()
    {
        if (Browser(out _) is not { } browser) return;
        using (browser)
        {
            int all = browser.Champions.Count;

            browser.Search = "ahri";
            Assert.True(browser.Champions.Count < all);
            Assert.All(browser.Champions, c =>
                Assert.True(c.Name.Contains("ahri", StringComparison.OrdinalIgnoreCase)
                            || c.Folder.Contains("ahri", StringComparison.OrdinalIgnoreCase)));

            browser.Search = "";
            Assert.Equal(all, browser.Champions.Count);
        }
    }

    [Fact]
    public void TypingDoesNotThrowAwayASelectionThatStillMatches()
    {
        // Losing it would clear the character and skin lists under the cursor while the user is typing.
        if (Browser(out _) is not { } browser) return;
        using (browser)
        {
            browser.Search = "ahri";
            browser.SelectedChampion = browser.Champions.FirstOrDefault();
            var chosen = browser.SelectedChampion;
            if (chosen is null) return;

            browser.Search = "ahr";
            Assert.Same(chosen, browser.SelectedChampion);
            Assert.NotEmpty(browser.Characters);
        }
    }

    // ===================================================== characters and skins

    [Fact]
    public void SelectingAChampionListsItsCharactersAndPicksTheChampionItself()
    {
        if (Browser(out _) is not { } browser) return;
        using (browser)
        {
            browser.SelectedChampion = browser.Champions.FirstOrDefault(c =>
                c.Folder.Equals("Elise", StringComparison.OrdinalIgnoreCase));
            if (browser.SelectedChampion is null) return;

            Assert.Contains(browser.Characters, c => c.Name.Equals("elise", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(browser.Characters, c => c.Name.Equals("elisespider", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("champion", browser.Characters[0].KindText);
            Assert.Same(browser.Characters[0], browser.SelectedCharacter);
            Assert.NotEmpty(browser.Skins);
        }
    }

    [Fact]
    public void TheSkinListLeavesOutRootAndCarriesTheClientNames()
    {
        if (Browser(out _) is not { } browser) return;
        using (browser)
        {
            browser.SelectedChampion = browser.Champions.FirstOrDefault(c =>
                c.Folder.Equals("Ahri", StringComparison.OrdinalIgnoreCase));
            if (browser.SelectedChampion is null) return;

            // root.bin has no mesh; listing it would offer an empty viewport.
            Assert.DoesNotContain(browser.Skins, s => s.Reference.IsRoot);
            Assert.All(browser.Skins, s => Assert.True(s.Number >= 0));
            Assert.Contains(browser.Skins, s => s.Number == 0);
            Assert.Contains(browser.Skins, s => s.Badge == "base");
        }
    }

    [Fact]
    public void SelectingASkinReadsItsBinAndFillsInWhatItOwns()
    {
        if (Browser(out _) is not { } browser) return;
        using (browser)
        {
            browser.SelectedChampion = browser.Champions.FirstOrDefault(c =>
                c.Folder.Equals("Ahri", StringComparison.OrdinalIgnoreCase));
            browser.SelectedSkin = browser.Skins.FirstOrDefault(s => s.Number == 0);
            if (browser.SelectedSkin is null) return;

            Assert.True(browser.HasSkinDetails);
            Assert.Equal("Ahri", browser.CodeName);
            Assert.EndsWith(".skn", browser.MeshPath, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".skl", browser.SkeletonPath, StringComparison.OrdinalIgnoreCase);
            Assert.NotEmpty(browser.HiddenSubmeshes);
            Assert.NotEmpty(browser.Overrides);
            Assert.True(browser.CanOpen);
        }
    }

    [Fact]
    public void ChangingChampionClearsTheSkinUnderneathIt()
    {
        // The details pane is the one thing on screen that describes a specific file. Leaving the last
        // skin's mesh path visible under a different champion is worse than showing nothing.
        if (Browser(out _) is not { } browser) return;
        using (browser)
        {
            browser.SelectedChampion = browser.Champions.FirstOrDefault(c =>
                c.Folder.Equals("Ahri", StringComparison.OrdinalIgnoreCase));
            browser.SelectedSkin = browser.Skins.FirstOrDefault(s => s.Number == 0);
            if (browser.SelectedSkin is null) return;

            browser.SelectedChampion = null;
            Assert.False(browser.HasSkinDetails);
            Assert.False(browser.CanOpen);
            Assert.Empty(browser.Skins);
            Assert.Empty(browser.Overrides);
            Assert.Equal("", browser.MeshPath);
        }
    }

    // ===================================================== opening

    [Fact]
    public void OpeningHandsTheHostTheChampionTheCharacterAndTheSkin()
    {
        if (Browser(out var host) is not { } browser) return;
        using (browser)
        {
            browser.SelectedChampion = browser.Champions.FirstOrDefault(c =>
                c.Folder.Equals("Ahri", StringComparison.OrdinalIgnoreCase));
            browser.SelectedSkin = browser.Skins.FirstOrDefault(s => s.Number == 0);
            if (browser.SelectedSkin is null) return;

            browser.OpenSkinCommand.Execute(null);

            var opened = Assert.Single(host.Opened);
            Assert.Equal("Ahri", opened.Champion.Name, ignoreCase: true);
            Assert.Equal("ahri", opened.Character.Name, ignoreCase: true);
            Assert.Equal("Ahri", opened.Skin.CodeName);
            Assert.True(opened.Skin.IsLoadable);
        }
    }

    [Fact]
    public void NothingCanBeOpenedUntilASkinIsChosen()
    {
        if (Browser(out var host) is not { } browser) return;
        using (browser)
        {
            Assert.False(browser.CanOpen);
            Assert.False(browser.OpenSkinCommand.CanExecute(null));
            browser.OpenSkinCommand.Execute(null);
            Assert.Empty(host.Opened);
        }
    }
}
