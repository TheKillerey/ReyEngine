using ReyEngine.App.ViewModels;
using ReyEngine.Core.Diagnostics;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M519: the Content Browser and the Console share one tabbed dock.
///
/// <para>Merging them buys back a whole pane of vertical space, but it costs something too: the console can
/// now be BEHIND a tab when something goes wrong, and the user has no reason to look. The badge is the
/// compensation — it counts warnings and errors that arrived while the console was hidden — so the tests
/// here are about that bookkeeping rather than about the layout.</para>
/// </summary>
public sealed class BottomDockTests
{
    private const int ContentBrowser = 0;
    private const int ConsoleTab = 1;

    /// <summary>
    /// ConsoleViewModel.Write hops to the UI thread, and in a test there is no one running that queue.
    ///
    /// <para>M576: this used to call Write and rely on CheckAccess() being true, which held only when this
    /// class happened to be the first thing in the run to touch the dispatcher. Under Avalonia 12 that
    /// stopped being the case in a full suite - another test claims the UI thread first, Write posts to a
    /// queue nobody pumps, and four tests here failed while still passing in isolation. Append is the half
    /// that does the work, and it does not care which thread is calling.</para>
    /// </summary>
    private static void Log(MainWindowViewModel vm, LogLevel level) =>
        vm.Console.Append(new LogEntry(DateTime.Now, level, "test", "message"));

    [Fact]
    public void TheDockStartsOnTheContentBrowserWithNothingUnseen()
    {
        var vm = new MainWindowViewModel();

        Assert.Equal(ContentBrowser, vm.BottomDockTab);
        Assert.Equal(0, vm.UnseenConsoleProblems);
        Assert.False(vm.HasUnseenConsoleProblems);
    }

    [Fact]
    public void ProblemsArrivingBehindTheTabAreCounted()
    {
        var vm = new MainWindowViewModel();

        Log(vm, LogLevel.Warning);
        Log(vm, LogLevel.Error);

        Assert.Equal(2, vm.UnseenConsoleProblems);
        Assert.True(vm.HasUnseenConsoleProblems);
    }

    [Fact]
    public void ChatterDoesNotBadgeTheTab()
    {
        // The dock logs constantly — every load, every save, every mount. A badge that lit up for those
        // would be ignored within a minute, and then it would not be there for the one that mattered.
        var vm = new MainWindowViewModel();

        Log(vm, LogLevel.Info);
        Log(vm, LogLevel.Success);

        Assert.Equal(0, vm.UnseenConsoleProblems);
        Assert.False(vm.HasUnseenConsoleProblems);
    }

    [Fact]
    public void OpeningTheConsoleClearsTheBadgeAndKeepsItClear()
    {
        var vm = new MainWindowViewModel();
        Log(vm, LogLevel.Error);
        Assert.Equal(1, vm.UnseenConsoleProblems);

        vm.BottomDockTab = ConsoleTab;
        Assert.Equal(0, vm.UnseenConsoleProblems);

        // Still in front: a problem you are looking at is not unseen.
        Log(vm, LogLevel.Error);
        Assert.Equal(0, vm.UnseenConsoleProblems);

        // Switch away and it starts counting again.
        vm.BottomDockTab = ContentBrowser;
        Log(vm, LogLevel.Warning);
        Assert.Equal(1, vm.UnseenConsoleProblems);
    }

    [Fact]
    public void HasUnseenConsoleProblemsIsRaisedForTheBadgeBinding()
    {
        // The badge's IsVisible binds to HasUnseenConsoleProblems, which is a plain getter — without the
        // hand-written notification it would never appear or disappear.
        var vm = new MainWindowViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Log(vm, LogLevel.Error);
        Assert.Contains(nameof(vm.HasUnseenConsoleProblems), raised);

        raised.Clear();
        vm.BottomDockTab = ConsoleTab;
        Assert.Contains(nameof(vm.HasUnseenConsoleProblems), raised);
    }

    [Fact]
    public void ShowConsoleBringsTheTabToTheFront()
    {
        var vm = new MainWindowViewModel();
        Log(vm, LogLevel.Error);

        vm.ShowConsoleCommand.Execute(null);

        Assert.Equal(ConsoleTab, vm.BottomDockTab);
        Assert.Equal(0, vm.UnseenConsoleProblems);

        vm.ShowContentBrowserCommand.Execute(null);
        Assert.Equal(ContentBrowser, vm.BottomDockTab);
    }

    [Fact]
    public void ClearingTheConsoleAlsoClearsTheBadge()
    {
        // Otherwise the badge would still be advertising lines that no longer exist to read.
        var vm = new MainWindowViewModel();
        Log(vm, LogLevel.Error);
        Assert.Equal(1, vm.UnseenConsoleProblems);

        vm.ClearConsoleCommand.Execute(null);

        Assert.Empty(vm.Console.Entries);
        Assert.Equal(0, vm.UnseenConsoleProblems);
    }
}
