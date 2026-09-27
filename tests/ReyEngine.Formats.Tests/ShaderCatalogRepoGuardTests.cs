using System.Collections.Concurrent;
using ReyEngine.App.ViewModels;
using ReyEngine.Core;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M790: a test run leaves the committed shader catalogues byte-identical.
///
/// <para>Twice a run rewrote <c>data/shader_catalogs/Live.json</c>: a stale probe in M784, then in M789 nothing
/// more than the material suites run from a worktree. A headless editor loads a catalogue as soon as it is
/// constructed, and after a Riot patch the committed stamp no longer matches Global.wad, so that load rescanned
/// and saved over the tracked file. The rescan needs hash tables to find shaders.bin, so any run from the main
/// checkout could do it, and a run from a worktree once they had been copied in. Only the stamp and the refreshed
/// shaders changed, so no test failed; it surfaced in <c>git status</c>. <see cref="TestRunIsolation"/> now sends
/// the cache to a temporary folder before the first test, and these pin that it stays there.</para>
/// </summary>
public sealed class ShaderCatalogRepoGuardTests
{
    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// Runs <paramref name="body"/> with every continuation on the calling thread, one at a time, the way the
    /// app's UI thread runs the editor's catalogue loads. The constructor fires a load and drops the task; on
    /// pool threads it and a load asked for here can land in SetCatalog together and race inside its
    /// collections (an IndexOutOfRangeException in ObservableCollection.Insert, seen once).
    /// </summary>
    private static void OnOneThread(Func<Task> body)
    {
        var previous = SynchronizationContext.Current;
        var context = new OneThreadContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try { context.RunUntilDone(body()); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private sealed class OneThreadContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback callback, object? state)
        {
            // A continuation arriving after the body finished - the constructor's load, still reading - runs
            // on the pool. Throwing here would throw on the pool thread that completed its task.
            try { _queue.Add((callback, state)); }
            catch (InvalidOperationException) { ThreadPool.QueueUserWorkItem(_ => callback(state)); }
        }

        public void RunUntilDone(Task task)
        {
            task.ContinueWith(_ => _queue.CompleteAdding(), TaskScheduler.Default);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable()) callback(state);
            task.GetAwaiter().GetResult();
        }
    }

    [Fact]
    public void ATestProcessCachesCataloguesInATemporaryFolderOfItsOwn()
    {
        string cache = Full(ReyPaths.ShaderCatalogsDir);
        Assert.Equal(Full(TestRunIsolation.ShaderCatalogsDir), cache);
        Assert.StartsWith(Full(Path.GetTempPath()), cache, StringComparison.OrdinalIgnoreCase);
        foreach (string committed in TestRunIsolation.CommittedCatalogueDirs())
            Assert.False(string.Equals(Full(committed), cache, StringComparison.OrdinalIgnoreCase),
                $"the test process caches shader catalogues in the committed folder {committed}");
    }

    [Fact]
    public void AHeadlessEditorLoadingEveryCatalogueLeavesTheCommittedOnesByteIdentical()
    {
        // The incident on purpose: a headless editor, then every environment's catalogue loaded to the end - the
        // constructor only fires its load and forgets it. With an install present and the committed stamp
        // stale, which is every Riot patch, that load rescans and saves.
        var loaded = new List<string>();
        OnOneThread(async () =>
        {
            var vm = new MainWindowViewModel();
            foreach (string environment in vm.MaterialEditor.ShaderEnvironments.ToList())
            {
                await vm.MaterialEditor.RequestCatalog!(environment);
                if (vm.MaterialEditor.Catalog?.Environment == environment) loaded.Add(environment);
            }
        });

        // Against the files as they were before this process ran any test, so a write by any other test in the
        // same run fails here too.
        var changed = TestRunIsolation.ChangedSinceStart();
        Assert.True(changed.Count == 0, "this test run rewrote committed shader catalogues: "
            + string.Join(", ", changed) + ". Restore them with git checkout -- data/shader_catalogs; "
            + "something writes them without going through ReyPaths.ShaderCatalogsDir.");

        // ...and not because nothing was saved: every catalogue the editor received is cached in this process's
        // folder. There are none where the checkout has no hash tables: shaders.bin is found by its resolved
        // path, so the load returns nothing and saves nothing, and there is nothing to protect either.
        foreach (string environment in loaded)
            Assert.True(File.Exists(Path.Combine(TestRunIsolation.ShaderCatalogsDir, environment + ".json")),
                $"{environment}: no catalogue cached in {TestRunIsolation.ShaderCatalogsDir}");
    }

    [Fact]
    public void OnlyReyPathsNamesTheCatalogueFolder()
    {
        // The redirect covers every writer only while the folder has one name in the code. A second
        // Path.Combine(DataRoot, "shader_catalogs") anywhere would write the tracked files again.
        if (TestRunIsolation.RepoRoot() is not { } root) return;
        string src = Path.Combine(root, "src");
        var naming = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(src, file).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "bin" or "obj"))
            .Where(file => File.ReadAllText(file).Contains("\"shader_catalogs\"", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .ToList();
        Assert.Equal(new[] { "src/ReyEngine.Core/ReyPaths.cs" }, naming);
    }
}
