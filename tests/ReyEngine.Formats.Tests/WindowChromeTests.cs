using System.Text.RegularExpressions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M578: the app draws its own window chrome.
///
/// <para>Avalonia 12 paints a title bar of its own into the extended client area, so every window sets
/// <c>WindowDecorations="BorderOnly"</c> and supplies its own buttons. That trades one failure mode for
/// another: get it wrong and a window has no way to close, which is not something a user can work around.
/// These are the invariants that keep that from happening.</para>
/// </summary>
public sealed class WindowChromeTests
{
    private static DirectoryInfo? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        return dir;
    }

    private static IEnumerable<string> ViewFiles(string pattern)
    {
        if (RepoRoot() is not { } root) yield break;
        string views = Path.Combine(root.FullName, "src", "ReyEngine.App", "Views");
        if (!Directory.Exists(views)) yield break;
        foreach (string f in Directory.EnumerateFiles(views, pattern)) yield return f;
    }

    [Fact]
    public void NoWindowStillAsksAvaloniaToPaintOverTheClientArea()
    {
        // ExtendClientAreaToDecorationsHint is what let Avalonia 12 draw its title straight through the
        // REYENGINE wordmark and its buttons under the settings gear.
        var offenders = ViewFiles("*.axaml")
            .Where(f => File.ReadAllText(f).Contains("ExtendClientArea", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();
        Assert.True(offenders.Count == 0, "still extending into the decorations: " + string.Join(", ", offenders));
    }

    [Fact]
    public void EveryWindowWithoutNativeChromeCanStillBeClosed()
    {
        // A window with BorderOnly has no native caption. If it also has neither the shared title bar nor
        // a close button of its own, it cannot be dismissed at all.
        var stranded = new List<string>();
        foreach (string f in ViewFiles("*.axaml"))
        {
            string markup = File.ReadAllText(f);
            if (!markup.Contains("WindowDecorations=\"BorderOnly\"", StringComparison.Ordinal)) continue;
            bool hasSharedBar = markup.Contains("ReyTitleBar", StringComparison.Ordinal);
            bool hasOwnClose = markup.Contains("Classes=\"caption close\"", StringComparison.Ordinal);
            if (!hasSharedBar && !hasOwnClose) stranded.Add(Path.GetFileName(f)!);
        }
        Assert.True(stranded.Count == 0, "no way to close: " + string.Join(", ", stranded));
    }

    [Fact]
    public void BothTitleBarsLetAButtonPressReachItsButton()
    {
        // The bug this pins: PointerPressed bubbles from a caption button up to the title bar, and
        // BeginMoveDrag then captures the pointer into the platform's modal move loop - so the release
        // never gets back to the button and Click never fires. The buttons drew perfectly and did nothing,
        // Close included. Both bars must bail out on a Button BEFORE they start a move.
        foreach (string file in new[] { "ReyTitleBar.axaml.cs", "MainWindow.axaml.cs" })
        {
            string? path = ViewFiles(file).FirstOrDefault();
            if (path is null) continue;
            string source = File.ReadAllText(path);

            // MainWindow exempts MenuItem too ("is MenuItem or Button"), so match the tail both share.
            int guard = source.IndexOf("Button) return;", StringComparison.Ordinal);
            int move = source.IndexOf("BeginMoveDrag(e)", StringComparison.Ordinal);
            Assert.True(guard > 0, $"{file} does not exempt buttons from the title-bar drag");
            Assert.True(move > 0, $"{file} has no window move to guard");
            Assert.True(guard < move, $"{file} starts the move before checking for a button");
        }
    }

    [Fact]
    public void TheSharedTitleBarCarriesAllThreeCaptionButtons()
    {
        string? path = ViewFiles("ReyTitleBar.axaml").FirstOrDefault();
        if (path is null) return;
        string markup = File.ReadAllText(path);

        foreach (string handler in new[] { "OnMinimise", "OnMaximise", "OnClose" })
            Assert.Contains($"Click=\"{handler}\"", markup, StringComparison.Ordinal);
        // and the reserve for native buttons is gone with the native buttons
        Assert.DoesNotContain("0,150,0", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void MaximiseIsHiddenOnWindowsThatCannotResize()
    {
        // Offering a maximise button on a fixed-size dialog is a button that does nothing when pressed -
        // the same class of problem as the drag swallowing the click.
        string? path = ViewFiles("ReyTitleBar.axaml.cs").FirstOrDefault();
        if (path is null) return;
        Assert.Contains("MaximiseButton.IsVisible = w.CanResize", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void TheMaximiseGlyphFollowsTheWindowState()
    {
        // A button labelled "maximise" that would restore is telling the user the wrong thing.
        foreach (string file in new[] { "ReyTitleBar.axaml.cs", "MainWindow.axaml.cs" })
        {
            string? path = ViewFiles(file).FirstOrDefault();
            if (path is null) continue;
            string source = File.ReadAllText(path);
            Assert.Contains("WindowState.Maximized", source, StringComparison.Ordinal);
            Assert.Contains("Restore", source, StringComparison.Ordinal);
            Assert.Matches(new Regex(@"WindowStateProperty"), source);
        }
    }
}
