using System.Reflection;
using System.Text.RegularExpressions;
using ReyEngine.App.ViewModels;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M577: the regrouped viewport toolbar.
///
/// <para>Avalonia resolves a binding path at RUNTIME. A name that does not exist compiles, passes every
/// other test, and then shows up as a control that quietly does nothing — which is how a toolbar rework
/// touching two dozen bindings goes wrong. So the paths are resolved here against the real view model.</para>
/// </summary>
public sealed class ViewportToolbarTests
{
    private static string? RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        return File.Exists(path) ? path : null;
    }

    private static string? MainWindowXaml() => RepoFile("src", "ReyEngine.App", "Views", "MainWindow.axaml");

    /// <summary>Walk a dotted binding path through the type graph. Null when a hop does not exist.</summary>
    private static PropertyInfo? Resolve(Type root, string path)
    {
        Type current = root;
        PropertyInfo? last = null;
        foreach (string hop in path.Split('.'))
        {
            last = current.GetProperty(hop, BindingFlags.Public | BindingFlags.Instance);
            if (last is null) return null;
            current = last.PropertyType;
        }
        return last;
    }

    /// <summary>Binding paths that name a plain property, i.e. the ones a typo can silently break.
    /// Anything with a converter, index, relative source or multi-part expression is skipped.</summary>
    private static IEnumerable<string> SimpleBindingPaths(string markup)
    {
        foreach (Match m in Regex.Matches(markup, @"\{Binding ([^}]+)\}"))
        {
            string expr = m.Groups[1].Value.Trim();
            if (expr.Length == 0) continue;
            string path = expr.Split(',')[0].Trim().TrimStart('!');
            if (path.StartsWith('$') || path.StartsWith('#')) continue;      // relative / element source
            if (path.Contains('[') || path.Contains(' ') || path.Contains('(')) continue;
            if (!Regex.IsMatch(path, @"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$")) continue;
            yield return path;
        }
    }


    /// <summary>
    /// Remove every region whose DataContext is an ITEM rather than the window.
    ///
    /// <para>A non-greedy regex is not enough: this window nests templates, and the first close tag then
    /// ends the wrong one, leaving item-scoped bindings behind to be mistaken for window-scoped ones.
    /// An ItemContainerTheme rebinds the same way a template does - the recent-projects menu builds its
    /// items that way - so it is stripped too.</para>
    /// </summary>
    private static string StripTemplates(string markup)
    {
        foreach (string tag in new[] { "TreeDataTemplate", "DataTemplate", "MenuItem.ItemContainerTheme" })
        {
            string open = "<" + tag, close = "</" + tag + ">";
            while (true)
            {
                int start = markup.IndexOf(open, StringComparison.Ordinal);
                if (start < 0) break;
                int depth = 0, i = start;
                int end = -1;
                while (i < markup.Length)
                {
                    int nextOpen = markup.IndexOf(open, i, StringComparison.Ordinal);
                    int nextClose = markup.IndexOf(close, i, StringComparison.Ordinal);
                    if (nextClose < 0) break;
                    if (nextOpen >= 0 && nextOpen < nextClose) { depth++; i = nextOpen + open.Length; continue; }
                    depth--;
                    i = nextClose + close.Length;
                    if (depth == 0) { end = i; break; }
                }
                if (end < 0) break;                       // unbalanced markup: leave it rather than guess
                markup = markup.Remove(start, end - start);
            }
        }
        return markup;
    }

    [Fact]
    public void EveryBindingInTheMainWindowNamesSomethingThatExists()
    {
        if (MainWindowXaml() is not { } file) return;
        string markup = File.ReadAllText(file);

        // Templates rebind to their item type, so a path inside one is not a MainWindowViewModel path.
        string outsideTemplates = StripTemplates(markup);

        var missing = SimpleBindingPaths(outsideTemplates)
            .Distinct(StringComparer.Ordinal)
            .Where(p => Resolve(typeof(MainWindowViewModel), p) is null)
            .ToList();

        Assert.True(missing.Count == 0,
            "MainWindow.axaml binds to name(s) the view model does not have: " + string.Join(", ", missing));
    }

    [Fact]
    public void TheToolbarIsOneGroupedRowRatherThanTwoFlatOnes()
    {
        if (MainWindowXaml() is not { } file) return;
        string markup = File.ReadAllText(file);

        // A WrapPanel so a narrow window wraps instead of clipping the right-hand end off.
        Assert.Contains("<WrapPanel Orientation=\"Horizontal\" ItemSpacing=", markup);
        foreach (string group in new[] { "👁 Display ▾", "◉ Overlays ▾", "🛠 Tools ▾" })
            Assert.Contains(group, markup);
    }

    [Fact]
    public void ThePointLightMarkersHaveAToggleInTheViewport()
    {
        // The gap this milestone closes: the flag existed and was reachable only from a checkbox inside the
        // Lighting window, which is not where anyone looks when the map is covered in light icons.
        if (MainWindowXaml() is not { } file) return;
        Assert.Contains("{Binding ShowLightMarkers}", File.ReadAllText(file));
        Assert.NotNull(typeof(MainWindowViewModel).GetProperty("ShowLightMarkers"));
    }

    [Fact]
    public void TheBadgeCountsExactlyWhatTheOverlaysMenuHolds()
    {
        // If these drift apart the number becomes a lie, and a lying badge is worse than no badge.
        if (MainWindowXaml() is not { } file) return;
        string markup = File.ReadAllText(file);
        int start = markup.IndexOf("OVERLAYS", StringComparison.Ordinal);
        int end = markup.IndexOf("NAVGRID LAYERS", StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "the Overlays menu is not where this test expects it");
        string menu = markup[start..end];

        var inMenu = Regex.Matches(menu, @"IsChecked=""\{Binding (\w+)\}""")
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        var counted = new HashSet<string>(StringComparer.Ordinal)
        {
            "ShowSoundIcons", "ShowPropIcons", "ShowParticles", "ShowLightMarkers", "ShowLightRanges",
            "ShowPlaceables", "ShowBounds", "ShowBones", "ShowBucketGrid", "ShowBakeBox",
        };

        // M659: the menu holds two kinds of thing. These are not overlays - they change how something
        // already counted is DRAWN - so counting them would say "one more thing is on" while nothing
        // extra appears. Listed rather than pattern-matched, so a new checkbox still fails this test
        // until somebody decides which kind it is.
        var notCounted = new HashSet<string>(StringComparer.Ordinal) { "IconsThroughWalls" };

        Assert.Equal(counted.Union(notCounted).ToHashSet(StringComparer.Ordinal), inMenu);
        Assert.Empty(counted.Intersect(notCounted));
    }

    [Fact]
    public void TheBadgeTracksTheTogglesItCounts()
    {
        var vm = new MainWindowViewModel();
        int start = vm.ActiveOverlayCount;

        vm.ShowLightMarkers = !vm.ShowLightMarkers;
        Assert.NotEqual(start, vm.ActiveOverlayCount);
        vm.ShowLightMarkers = !vm.ShowLightMarkers;
        Assert.Equal(start, vm.ActiveOverlayCount);

        // and the badge string follows the count
        Assert.Equal(vm.ActiveOverlayCount.ToString(), vm.OverlayBadge);
        Assert.Equal(vm.ActiveOverlayCount > 0, vm.HasActiveOverlays);
    }

    [Fact]
    public void TheBadgeRaisesChangeNotificationsSoTheButtonRepaints()
    {
        // A computed property nothing raises is a number that freezes at whatever it was on startup.
        var vm = new MainWindowViewModel();
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        vm.ShowBounds = !vm.ShowBounds;

        Assert.Contains(nameof(vm.ActiveOverlayCount), raised);
        Assert.Contains(nameof(vm.OverlayBadge), raised);
        Assert.Contains(nameof(vm.HasActiveOverlays), raised);
    }

    [Fact]
    public void GameModeStillClearsEveryOverlayItPromisesTo()
    {
        // Game Mode's whole claim is "hide every editor overlay at once". Now that the menu and the badge
        // enumerate them too, the count is a cheap way to hold it to that.
        var vm = new MainWindowViewModel { ShowLightMarkers = true, ShowBounds = true, ShowBones = true };
        int before = vm.ActiveOverlayCount;
        Assert.True(before > 0);

        vm.GameMode = true;
        Assert.Equal(0, vm.ActiveOverlayCount);
        Assert.False(vm.HasActiveOverlays);

        vm.GameMode = false;
        Assert.Equal(before, vm.ActiveOverlayCount);
    }
}
