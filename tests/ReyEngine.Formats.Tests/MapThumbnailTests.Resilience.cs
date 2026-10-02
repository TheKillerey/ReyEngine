using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using LeagueToolkit.Core.Renderer;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M807 (review): what a thumbnail does when the editor changes under it - a rebuild while it draws, a map that loads, a Riot
/// WAD that mounts, a toggle that changes the picture, a hidden browser, a stored picture that is corrupt - and what it never
/// writes down. The fakes are <see cref="MapThumbnailTests"/>'s.
/// </summary>
public sealed partial class MapThumbnailTests
{
    private sealed class Resource : IDisposable
    {
        private int _disposed;
        public int Disposed => Volatile.Read(ref _disposed);
        public void Dispose() => Interlocked.Increment(ref _disposed);
    }

    private static readonly byte[] Png1 = { 0x89, 1, 2, 3 };

    private static MapThumbnailOutcome Picture(byte marker) =>
        new() { Png = new byte[] { 0x89, marker }, TotalMs = 1, Timings = "t", Summary = "s" };

    // ================================================================================================ a rebuild under a draw

    [Fact]
    public void A_rebuild_lets_the_tile_in_hand_finish_and_the_new_tile_for_that_map_gets_its_picture_without_a_second_draw()
    {
        // the project's file watcher rebuilds the mounts and the tree on every save: a draw that restarted each time would never
        // finish, and one that finished would be thrown away
        using var rig = new Rig();
        rig.Renderer.Gate = new ManualResetEventSlim(false);
        var old = MapNode("lux");
        rig.Service.SetVisible(new[] { old });
        Assert.True(rig.WaitFor(() => rig.Renderer.Rendered.Count == 1), "the draw is in the worker's hands");

        rig.Service.CancelPending();                                     // BuildMounts, BuildProjectTree
        var fresh = MapNode("lux");                                      // the grid lists the same map again, as a new tile object
        rig.Service.SetVisible(new[] { fresh });
        Assert.True(rig.WaitFor(() => rig.Service.Outstanding == 2), "the new tile waits behind the draw in hand");

        rig.Renderer.Gate.Set();
        Assert.True(rig.WaitFor(() => rig.Applied.Any(a => ReferenceEquals(a.Node, fresh))), "the new tile got the picture");
        Assert.Single(rig.Renderer.Rendered);                             // drawn once
        Assert.Equal(1, rig.Service.Rendered);
        Assert.Equal(1, rig.Service.FromDisk);                            // the second tile found the first one's file
        Assert.True(rig.Service.Cache.TryReadPng("key_lux.mapgeo", out _));
    }

    [Fact]
    public void A_reader_retired_while_a_tile_is_in_hand_is_disposed_when_that_tile_is_done()
    {
        using var rig = new Rig();
        var idle = new Resource();
        rig.Service.Retire(idle);
        Assert.Equal(1, idle.Disposed);                                  // nothing in hand: at once

        rig.Renderer.Gate = new ManualResetEventSlim(false);
        rig.Service.SetVisible(new[] { MapNode("lux") });
        Assert.True(rig.WaitFor(() => rig.Renderer.Rendered.Count == 1));
        var reading = new Resource();
        rig.Service.Retire(reading);
        Thread.Sleep(100);
        Assert.Equal(0, reading.Disposed);                               // the tile in hand may be reading through it

        rig.Renderer.Gate.Set();
        Assert.True(rig.WaitFor(() => reading.Disposed == 1), "disposed the moment the tile was done");
        Assert.Equal(1, reading.Disposed);                               // and only once
    }

    [Fact]
    public void Disposing_the_service_disposes_what_was_retired_for_a_tile_that_never_finished()
    {
        var rig = new Rig();
        try
        {
            rig.Renderer.Gate = new ManualResetEventSlim(false);
            rig.Service.SetVisible(new[] { MapNode("lux") });
            Assert.True(rig.WaitFor(() => rig.Renderer.Rendered.Count == 1));
            var reading = new Resource();
            rig.Service.Retire(reading);
            Assert.Equal(0, reading.Disposed);

            rig.Service.Dispose();                                       // the window closes mid-draw
            Assert.Equal(1, reading.Disposed);

            var late = new Resource();
            rig.Service.Retire(late);                                    // and a reader retired after that is not left behind either
            Assert.Equal(1, late.Disposed);
        }
        finally { rig.Renderer.Gate?.Set(); rig.Dispose(); }
    }

    // ================================================================================================ what is never written down

    [Fact]
    public void A_draw_during_which_something_changed_in_place_is_not_kept_and_is_drawn_again()
    {
        using var rig = new Rig();
        int calls = 0;
        rig.Renderer.Behaviour = _ =>
        {
            int n = Interlocked.Increment(ref calls);
            if (n == 1) Interlocked.Increment(ref rig.Host.Version);     // a Riot WAD was mounted as a fallback under the draw
            return Picture((byte)n);
        };

        rig.Service.SetVisible(new[] { MapNode("lux") });
        Assert.True(rig.WaitFor(() => rig.Applied.Count == 1));

        Assert.Equal(2, rig.Renderer.Rendered.Count);                     // the disturbed draw, then a clean one
        Assert.Equal(new byte[] { 0x89, 2 }, rig.Applied[0].Png);          // the tile never saw the first
        Assert.True(rig.Service.Cache.TryReadPng("key_lux.mapgeo", out var onDisk));
        Assert.Equal(new byte[] { 0x89, 2 }, onDisk);                      // and neither did the cache
        Assert.Contains(rig.Host.Logs, l => l.Contains("what it reads changed while it was drawn") && l.Contains("drawing it again"));
    }

    [Fact]
    public void A_draw_that_is_disturbed_every_time_is_given_up_after_a_few_tries_and_leaves_nothing_behind()
    {
        using var rig = new Rig();
        rig.Renderer.Behaviour = _ => { Interlocked.Increment(ref rig.Host.Version); return Picture(1); };

        rig.Service.SetVisible(new[] { MapNode("lux") });
        Assert.True(rig.WaitFor(() => rig.Renderer.Rendered.Count == MapThumbnailService.MaxAttempts));
        Assert.True(rig.WaitFor(() => rig.Host.Logs.Any(l => l.Contains("given up"))));
        Thread.Sleep(100); rig.Pump();

        Assert.Equal(MapThumbnailService.MaxAttempts, rig.Renderer.Rendered.Count);   // not forever
        Assert.Empty(rig.Applied);
        Assert.False(rig.Service.Cache.TryReadPng("key_lux.mapgeo", out _));
        Assert.Null(rig.Service.Cache.TryReadFailure("key_lux.mapgeo"));
    }

    [Fact]
    public void A_failure_found_after_a_read_threw_is_never_written_down_but_one_found_cleanly_is()
    {
        using var rig = new Rig();
        int calls = 0;
        rig.Renderer.Behaviour = job =>
        {
            if (job.Key.Contains("flaky"))
            {
                // the first draw of "flaky" reads a mount that is rebuilt under it: a read throws, the asset reads as missing, and
                // the map therefore "has no geometry" - which is not a fact about the map
                if (Interlocked.Increment(ref calls) == 1) rig.Host.Fault(job);
                return calls == 1
                    ? new MapThumbnailOutcome { Failure = "the mapgeo holds no geometry", Permanent = true }
                    : Picture(5);
            }
            return new MapThumbnailOutcome { Failure = "the mapgeo holds no geometry", Permanent = true };
        };

        rig.Service.SetVisible(new[] { MapNode("flaky"), MapNode("empty") });
        Assert.True(rig.WaitFor(() => rig.Applied.Count == 1 && rig.Service.Failed == 1));

        Assert.Null(rig.Service.Cache.TryReadFailure("key_flaky.mapgeo"));             // the draw with the fault wrote nothing
        Assert.True(rig.Service.Cache.TryReadPng("key_flaky.mapgeo", out _));         // and the clean redraw drew it
        Assert.Equal("the mapgeo holds no geometry", rig.Service.Cache.TryReadFailure("key_empty.mapgeo"));
    }

    [Fact]
    public void The_renderer_remembers_only_failures_that_are_facts_about_the_map()
    {
        // a map that "would not decode" or "resolved no material" may have been torn by a read: an exception is not a fact
        string? src = Source("src", "ReyEngine.App", "Services", "MapThumbnailRenderer.cs");
        if (src is null) { output.WriteLine("SKIPPED: MapThumbnailRenderer.cs not found"); return; }
        Assert.Contains("Fail($\"the map would not decode: {ex.Message}\", false, total)", src);
        Assert.Contains("Fail(\"no material of the map resolved to a shader\" + why, false, total)", src);
        // and the ones that are: nothing to draw, a start state that hides everything, no extent, a blank frame
        Assert.Contains("Fail(\"the mapgeo holds no geometry\", true, total)", src);
        Assert.Contains("Fail(\"the map's start state draws nothing (every mesh is hidden)\", true, total)", src);
    }

    [Fact]
    public void A_read_that_threw_is_a_fault_on_the_job_and_one_that_found_nothing_is_not()
    {
        var vm = new MainWindowViewModel();
        var readsType = typeof(MainWindowViewModel).GetNestedType("MapThumbnailReads", BindingFlags.NonPublic)!;
        var reads = Activator.CreateInstance(readsType, nonPublic: true)!;
        var faults = readsType.GetField("Faults")!;
        var read = typeof(MainWindowViewModel).GetMethod("ReadForThumbnail", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var has = typeof(MainWindowViewModel).GetMethod("HasForThumbnail", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var mounts = new AssetMountService();
        mounts.AddFallback(new FakeMount(good: 11, poisoned: 22));
        object?[] Args(ulong hash) => new object?[] { mounts, null, reads, hash };

        Assert.Equal(new byte[] { 1, 2, 3 }, (byte[]?)read.Invoke(vm, Args(11)));
        Assert.Null((byte[]?)read.Invoke(vm, Args(99)));                   // not in any mount: a missing asset
        Assert.Equal(0, (int)faults.GetValue(reads)!);

        Assert.Null((byte[]?)read.Invoke(vm, Args(22)));                   // the mount threw while reading
        Assert.Equal(1, (int)faults.GetValue(reads)!);
        Assert.False((bool)has.Invoke(vm, Args(22))!);                     // and while being asked: also a fault
        Assert.True((bool)has.Invoke(vm, Args(11))!);
        Assert.Equal(2, (int)faults.GetValue(reads)!);
    }

    private sealed class FakeMount(ulong good, ulong poisoned) : IAssetMount
    {
        public string Name => "fake";
        public string Location => "fake";
        public AssetSourceKind Kind => AssetSourceKind.RiotReference;
        public bool IsEditable => false;
        public IEnumerable<MountedAsset> Enumerate() => Array.Empty<MountedAsset>();
        public bool Contains(ulong pathHash) => pathHash == poisoned ? throw new InvalidOperationException("Collection was modified") : pathHash == good;
        public MountedAsset? Get(ulong pathHash) => null;
        public byte[] Read(ulong pathHash) => pathHash == good ? new byte[] { 1, 2, 3 } : throw new InvalidOperationException("Collection was modified");
        public bool TryGetFilePath(ulong pathHash, out string filePath) { filePath = ""; return false; }
        public void Dispose() { }
    }

    // ================================================================================================ Refresh Thumbnail

    [Fact]
    public void Refresh_keeps_the_picture_until_the_new_one_is_written_and_keeps_it_if_the_draw_fails()
    {
        using var rig = new Rig { ShowPictures = true };
        var node = MapNode("lux");
        rig.Service.SetVisible(new[] { node });
        Assert.True(rig.WaitFor(() => rig.Applied.Count == 1));
        var first = node.Thumbnail;
        Assert.NotNull(first);
        byte[] firstBytes = rig.Applied[0].Png;

        // a refresh whose draw fails: the tile and the file are exactly as they were
        rig.Renderer.Gate = new ManualResetEventSlim(false);
        rig.Renderer.Behaviour = _ => new MapThumbnailOutcome { Failure = "IOException: a mount closed under the job" };
        Assert.True(rig.Service.Refresh(node));
        Assert.True(rig.WaitFor(() => rig.Renderer.Rendered.Count == 2), "the refresh is in the worker's hands");
        Assert.Same(first, node.Thumbnail);                               // nothing was cleared up front
        Assert.True(rig.Service.Cache.TryReadPng("key_lux.mapgeo", out var during));
        Assert.Equal(firstBytes, during);
        rig.Renderer.Gate.Set();
        Assert.True(rig.WaitFor(() => rig.Service.Outstanding == 0));
        rig.Pump();
        Assert.Same(first, node.Thumbnail);
        Assert.True(rig.Service.Cache.TryReadPng("key_lux.mapgeo", out var after));
        Assert.Equal(firstBytes, after);
        Assert.Null(rig.Service.Cache.TryReadFailure("key_lux.mapgeo"));
        Assert.Equal(0, rig.Service.Failed);                              // the tile did not fail: its refresh did
        Assert.Contains(rig.Host.Logs, l => l.Contains("The picture it had stays"));

        // one that succeeds replaces it
        rig.Renderer.Gate = null;
        rig.Renderer.Behaviour = _ => Picture(9);
        Assert.True(rig.Service.Refresh(node));
        Assert.True(rig.WaitFor(() => rig.Applied.Count == 2));
        Assert.Equal(new byte[] { 0x89, 9 }, rig.Applied[1].Png);
        Assert.NotSame(first, node.Thumbnail);
        Assert.True(rig.Service.Cache.TryReadPng("key_lux.mapgeo", out var replaced));
        Assert.Equal(new byte[] { 0x89, 9 }, replaced);
    }

    [Fact]
    public void Refresh_does_nothing_and_deletes_nothing_while_no_tile_can_be_drawn()
    {
        using var rig = new Rig();
        var node = MapNode("lux");
        rig.Service.Cache.WritePng("key_lux.mapgeo", Png1);

        rig.Host.Generate = false;                                        // the OpenGL viewport, or no game folder
        Assert.False(rig.Service.CanRefresh);
        Assert.False(rig.Service.Refresh(node));
        Assert.True(rig.Service.Cache.TryReadPng("key_lux.mapgeo", out var kept));
        Assert.Equal(Png1, kept);                                         // the picture is not lost for a draw that cannot happen
        Assert.Empty(rig.Renderer.Rendered);

        rig.Host.Generate = true;                                         // the device fails: drawing is off for the session
        rig.Renderer.Behaviour = _ => new MapThumbnailOutcome { Failure = "no adapter", DeviceFailed = true };
        rig.Service.SetVisible(new[] { MapNode("other") });
        Assert.True(rig.WaitFor(() => rig.Service.Disabled));
        Assert.False(rig.Service.CanRefresh);
        Assert.False(rig.Service.Refresh(node));
        Assert.True(rig.Service.Cache.TryReadPng("key_lux.mapgeo", out _));
    }

    [Fact]
    public void The_refresh_command_is_disabled_while_the_opengl_viewport_is_selected()
    {
        var vm = new MainWindowViewModel();
        bool openGl = vm.Settings.UseOpenGlViewport;                      // in memory only: never saved here
        try
        {
            vm.Settings.UseOpenGlViewport = true;
            Assert.False(vm.RefreshMapThumbnailCommand.CanExecute(MapNode("lux")), "the OpenGL viewport draws no thumbnail");
        }
        finally { vm.Settings.UseOpenGlViewport = openGl; }
    }

    // ================================================================================================ a picture that will not load

    [Fact]
    public void A_stored_picture_that_will_not_load_is_evicted_deleted_and_drawn_again_once()
    {
        using var rig = new Rig();
        byte[] corrupt = { 0, 0, 0 };
        rig.Corrupt = png => png.SequenceEqual(corrupt);
        rig.Service.Cache.WritePng("key_lux.mapgeo", corrupt);

        rig.Service.SetVisible(new[] { MapNode("lux") });
        Assert.True(rig.WaitFor(() => rig.Applied.Count == 2), "the corrupt bytes, then the redrawn picture");

        Assert.Equal(corrupt, rig.Applied[0].Png);
        Assert.Equal(new byte[] { 0x89, 1, 2, 3 }, rig.Applied[1].Png);
        Assert.Single(rig.Renderer.Rendered);
        Assert.True(rig.Service.Cache.TryReadPng("key_lux.mapgeo", out var onDisk));
        Assert.Equal(new byte[] { 0x89, 1, 2, 3 }, onDisk);                // the bad file is gone, the good one is there
        Assert.Contains(rig.Host.Logs, l => l.Contains("would not load") && l.Contains("drawing it again"));
    }

    [Fact]
    public void A_picture_that_never_loads_is_tried_once_more_and_then_the_tile_keeps_its_icon()
    {
        using var rig = new Rig();
        rig.Corrupt = _ => true;                                          // even a freshly drawn picture will not load
        rig.Service.Cache.WritePng("key_lux.mapgeo", new byte[] { 0, 0, 0 });
        var node = MapNode("lux");

        rig.Service.SetVisible(new[] { node });
        Assert.True(rig.WaitFor(() => rig.Host.Logs.Any(l => l.Contains("freshly drawn picture would not load"))));
        Thread.Sleep(100); rig.Pump();
        Assert.Single(rig.Renderer.Rendered);

        rig.Service.SetVisible(new[] { node });                           // asked again: no third try
        rig.Pump(); Thread.Sleep(100); rig.Pump();
        Assert.Single(rig.Renderer.Rendered);
    }

    // ================================================================================================ a hidden browser

    [Fact]
    public void A_hidden_browser_reports_no_tiles_so_what_is_queued_is_dropped_and_what_is_in_hand_finishes()
    {
        using var rig = new Rig { ShowPictures = true };
        rig.Renderer.Gate = new ManualResetEventSlim(false);
        var nodes = Enumerable.Range(0, 5).Select(i => MapNode("m" + i)).ToList();
        rig.Service.SetVisible(nodes);
        Assert.True(rig.WaitFor(() => rig.Renderer.Rendered.Count == 1 && rig.Service.Outstanding == 5));

        rig.Service.SetVisible(Array.Empty<AssetNodeViewModel>());        // the Console tab was selected
        Assert.True(rig.WaitFor(() => rig.Service.Outstanding == 1), "only the tile in hand remains");
        rig.Renderer.Gate.Set();
        Assert.True(rig.WaitFor(() => rig.Service.Outstanding == 0));
        Thread.Sleep(100); rig.Pump();
        Assert.Single(rig.Renderer.Rendered);                              // nothing was drawn for a tile nobody sees

        rig.Service.SetVisible(nodes);                                    // the tab is back
        Assert.True(rig.WaitFor(() => nodes.All(n => n.Thumbnail is not null)));
        Assert.Equal(5, rig.Renderer.Rendered.Count);
    }

    [Fact]
    public void The_grid_reports_no_tile_while_the_console_tab_or_a_detached_grid_hides_it()
    {
        string? view = Source("src", "ReyEngine.App", "Views", "MainWindow.axaml.cs");
        if (view is null) { output.WriteLine("SKIPPED: MainWindow.axaml.cs not found"); return; }
        Assert.Contains("vm.IsContentBrowserTabSelected && TopLevel.GetTopLevel(scroll) is not null && scroll.IsEffectivelyVisible", view);
        Assert.Contains("Shown() ? scroll.Viewport.Height : 0", view);               // a zero-height window names no tile
        Assert.Contains("e.PropertyName == nameof(MainWindowViewModel.BottomDockTab)", view);   // and the tab changing re-reports
        Assert.Contains("scroll.DetachedFromVisualTree", view);
        Assert.Contains("scroll.AttachedToVisualTree", view);
        // a tick that threw would end the dispatcher's timer: it is caught and put in the console
        Assert.Matches(@"settle\.Tick \+= \(_, _\) =>\s*\{\s*settle\.Stop\(\);\s*try \{ Report\(\); \}\s*catch \(Exception ex\) \{ vm\.LogMapThumbnailProblem", view);

        var vm = new MainWindowViewModel();
        Assert.True(vm.IsContentBrowserTabSelected);                       // tab 0 = Content Browser
        vm.BottomDockTab = 1;
        Assert.False(vm.IsContentBrowserTabSelected);                      // tab 1 = Console
        var (first, last) = ContentGridViewport.VisibleRange(100, 0, 1020, 0);
        Assert.True(first > last, "a zero-height window names no tile");
    }

    // ================================================================================================ Game Depth

    [Fact]
    public void Toggling_game_depth_looks_at_the_visible_tiles_again_and_each_keeps_its_picture_until_the_new_one_is_ready()
    {
        using var rig = new Rig { ShowPictures = true };
        var a = MapNode("a"); var b = MapNode("b");
        rig.Service.SetVisible(new[] { a, b });
        Assert.True(rig.WaitFor(() => rig.Applied.Count == 2));
        var pictureA = a.Thumbnail; var pictureB = b.Thumbnail;

        // Game Depth goes on: the key of every tile changes, and the draw is a different one
        rig.Host.Depth = true;
        rig.Renderer.Gate = new ManualResetEventSlim(false);
        rig.Renderer.Behaviour = _ => Picture(7);
        rig.Service.Reevaluate();
        Assert.True(rig.WaitFor(() => rig.Renderer.Rendered.Count == 3), "one new draw is in hand, the other waits");
        Assert.Same(pictureA, a.Thumbnail);                               // the tiles still show what they showed
        Assert.Same(pictureB, b.Thumbnail);
        rig.Renderer.Gate.Set();
        Assert.True(rig.WaitFor(() => rig.Applied.Count == 4));
        Assert.True(rig.WaitFor(() => rig.Applied.Count == 4 && rig.Service.Outstanding == 0));
        Assert.NotSame(pictureA, a.Thumbnail);                            // replaced, not cleared
        Assert.NotSame(pictureB, b.Thumbnail);
        Assert.True(rig.Service.Cache.TryReadPng("key_a.mapgeo_depth", out _));
        Assert.True(rig.Service.Cache.TryReadPng("key_b.mapgeo_depth", out _));

        // and off again: the first pictures come back from memory, nothing is drawn
        rig.Host.Depth = false;
        rig.Renderer.Gate = null;
        int drawn = rig.Renderer.Rendered.Count;
        rig.Service.Reevaluate();
        Assert.True(rig.WaitFor(() => rig.Applied.Count == 6));
        Assert.Equal(drawn, rig.Renderer.Rendered.Count);
    }

    [Fact]
    public void A_tile_whose_key_did_not_change_is_not_drawn_again_when_game_depth_is_toggled()
    {
        using var rig = new Rig { ShowPictures = true };
        var node = MapNode("a");
        rig.Service.SetVisible(new[] { node });
        Assert.True(rig.WaitFor(() => rig.Applied.Count == 1));

        rig.Service.Reevaluate();                                         // nothing the key reads changed
        rig.Pump(); Thread.Sleep(100); rig.Pump();

        Assert.Single(rig.Renderer.Rendered);
        Assert.Single(rig.Applied);
    }

    [Fact]
    public void The_draw_uses_the_game_depth_value_the_key_was_made_with()
    {
        string? renderer = Source("src", "ReyEngine.App", "Services", "MapThumbnailRenderer.cs");
        string? builder = Source("src", "ReyEngine.App", "Services", "Dx11SceneBuilder.cs");
        string? vm = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.MapThumbnails.cs");
        string? hub = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        if (renderer is null || builder is null || vm is null || hub is null) { output.WriteLine("SKIPPED: sources not found"); return; }

        // the key and the job carry ONE reading of the static; the renderer draws with the job's value
        Assert.Contains("bool clientDepth = Dx11SceneBuilder.EmulateClientDepthRules;", vm);
        Assert.Contains("ClientDepthRules = clientDepth,", vm);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(vm, @"=\s*Dx11SceneBuilder\.EmulateClientDepthRules\b"));
        Assert.Contains("Dx11SceneBuilder.Commit(renderer, prepared, AppInfo.DisplayVersion, job.ClientDepthRules)", renderer);
        Assert.Contains("SortByPipeline = !job.ClientDepthRules", renderer);
        Assert.DoesNotMatch(@"(?<!cref="")Dx11SceneBuilder\.EmulateClientDepthRules\b(?!""\s*/>)", renderer);   // the renderer never reads the static
        // Commit has the flag as a parameter (the viewport's three-argument form reads the static for it)
        Assert.Contains("string gameVersion) =>\r\n        Commit(renderer, scene, gameVersion, EmulateClientDepthRules);".Replace("\r\n", "\n"), builder.Replace("\r\n", "\n"));
        Assert.Contains("if (emulateClientDepthRules) depthWrite = true;", builder);
        Assert.DoesNotContain("if (EmulateClientDepthRules) depthWrite = true;", builder);
        // and a toggle looks at the visible tiles again - after the static changed, so they are described with the new value
        int set = hub.IndexOf("Services.Dx11SceneBuilder.EmulateClientDepthRules = value;", StringComparison.Ordinal);
        int again = hub.IndexOf("ReevaluateMapThumbnails();", StringComparison.Ordinal);
        Assert.True(set > 0 && again > set, "Reevaluate follows the assignment");
    }

    // ================================================================================================ tiles that cannot be described

    [Fact]
    public void A_tile_that_cannot_be_described_is_remembered_until_something_is_rebuilt()
    {
        using var rig = new Rig();
        rig.Host.NotDescribable["nomap.mapgeo"] = true;                   // no materials bin: describing it scans the whole project
        var node = MapNode("nomap");

        rig.Service.SetVisible(new[] { node });
        Assert.Equal(1, rig.Host.Described);
        for (int i = 0; i < 5; i++) rig.Service.SetVisible(new[] { node });
        Assert.Equal(1, rig.Host.Described);                              // asked once, not on every settle of the grid

        rig.Service.CancelPending();                                      // a rebuild: whatever was wrong may be mended
        rig.Service.SetVisible(new[] { node });
        Assert.Equal(2, rig.Host.Described);
    }

    [Fact]
    public void Switching_the_renderer_asks_the_tiles_that_could_not_be_drawn_again()
    {
        using var rig = new Rig();
        rig.Host.NotDescribable["late.mapgeo"] = true;
        var node = MapNode("late");
        rig.Service.SetVisible(new[] { node });
        Assert.Equal(1, rig.Host.Described);

        rig.Host.NotDescribable.Clear();                                  // the game folder was set
        rig.Host.Generate = false; rig.Service.AvailabilityChanged();     // View > Use OpenGL renderer
        rig.Host.Generate = true; rig.Service.AvailabilityChanged();      // and back: asked again, drawn
        Assert.True(rig.WaitFor(() => rig.Applied.Count == 1));
        Assert.Single(rig.Renderer.Rendered);
    }

    [Fact]
    public void A_tile_that_can_never_be_drawn_does_not_enumerate_its_sibling_bins()
    {
        string? vm = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.MapThumbnails.cs");
        if (vm is null) { output.WriteLine("SKIPPED: MainWindowViewModel.MapThumbnails.cs not found"); return; }
        Assert.Contains("bool drawable = !Settings.UseOpenGlViewport;", vm);
        Assert.Contains("ControllerBins = drawable ? SiblingBinsOf(dir, materials.PathHash) : Array.Empty<ulong>(),", vm);
    }

    // ================================================================================================ pausing inside a job

    private static MapThumbnailJob BareJob(Func<bool>? cancelled, Func<bool>? pause) => new()
    {
        Key = "k", MapGeoPath = "p", MapGeoHash = 1, MaterialsBinPath = "m", MaterialsBinHash = 2, FinalDirectory = "f",
        Read = _ => null, Has = _ => false, Hashes = new NoHashes(), BinName = _ => null, WadPath = _ => null,
        IsCancelled = cancelled, ShouldPause = pause,
    };

    [Fact]
    public void A_job_waits_at_a_stage_boundary_while_the_editor_is_busy_and_stops_if_it_is_cancelled_meanwhile()
    {
        var proceed = typeof(MapThumbnailRenderer).GetMethod("Proceed", BindingFlags.NonPublic | BindingFlags.Static)!;
        var check = typeof(MapThumbnailRenderer).GetMethod("Check", BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.True((bool)proceed.Invoke(null, new object[] { BareJob(null, null) })!);          // nothing to wait for

        long until = System.Environment.TickCount64 + 250;                                               // a map loads for a quarter second
        var sw = Stopwatch.StartNew();
        Assert.True((bool)proceed.Invoke(null, new object[] { BareJob(null, () => System.Environment.TickCount64 < until) })!);
        Assert.InRange(sw.ElapsedMilliseconds, 200, 5000);                                        // it waited - it was not cancelled

        sw.Restart();
        Assert.False((bool)proceed.Invoke(null, new object[] { BareJob(() => true, () => true) })!);   // cancelled while paused: out at once
        Assert.InRange(sw.ElapsedMilliseconds, 0, 1000);

        var ex = Assert.Throws<TargetInvocationException>(() => check.Invoke(null, new object[] { BareJob(() => true, null) }));
        Assert.IsType<OperationCanceledException>(ex.InnerException);
    }

    private static (Dictionary<string, TextureImage> Textures, List<ThreadPriority> Priorities, ThreadPriority CallerAfter) RunTextureLoop(
        int count, Func<bool>? checkpoint, Action<int>? onRead = null)
    {
        var paths = Enumerable.Range(0, count).Select(i => $"assets/test/t{i}.tex").ToList();
        var bytes = BgraTexWithMips(64);
        var byHash = paths.ToDictionary(p => HashAlgorithms.WadPath(p), _ => bytes);
        var priorities = new ConcurrentBag<ThreadPriority>();
        int reads = 0;
        byte[]? Read(ulong hash)
        {
            priorities.Add(Thread.CurrentThread.Priority);
            onRead?.Invoke(Interlocked.Increment(ref reads));
            return byHash.TryGetValue(hash, out var b) ? b : null;
        }

        var scene = new Dx11SceneBuilder.PreparedScene { Mesh = PreviewGeometry.CreateBuiltIn("Sphere"), Slices = new(), Textures = new() };
        var limits = new Dx11SceneBuilder.PrepareLimits(16, 2, checkpoint);
        var decode = typeof(Dx11SceneBuilder).GetMethod("DecodeTextures", BindingFlags.NonPublic | BindingFlags.Static)!;
        // on a thread of its own at normal priority, which takes part in the loop: its priority afterwards is the one thing the
        // loop must give back that no other test's pool threads can disturb
        var after = ThreadPriority.Highest;
        var caller = new Thread(() =>
        {
            decode.Invoke(null, new object[] { new HashSet<string>(paths), (Func<ulong, byte[]?>)Read, scene, limits });
            after = Thread.CurrentThread.Priority;
        }) { Priority = ThreadPriority.Normal };
        caller.Start();
        Assert.True(caller.Join(TimeSpan.FromMinutes(2)), "the loop returned");
        return (scene.Textures, priorities.ToList(), after);
    }

    [Fact]
    public void The_capped_texture_loop_asks_before_each_texture_and_decodes_at_below_normal_priority()
    {
        var (textures, priorities, callerAfter) = RunTextureLoop(12, () => true);

        Assert.Equal(12, textures.Count);
        Assert.All(textures.Values, t => Assert.Equal((16, 16), (t.Width, t.Height)));            // the 16-wide mip of a 64-wide texture
        Assert.Equal(12, priorities.Count);
        Assert.All(priorities, p => Assert.True(p <= ThreadPriority.BelowNormal, $"decoded at {p}"));   // the caller and the pool threads alike
        Assert.Equal(ThreadPriority.Normal, callerAfter);                                          // and the thread that ran the loop got its own back
    }

    [Fact]
    public void The_capped_texture_loop_stops_when_the_checkpoint_says_the_job_is_cancelled()
    {
        int asked = 0;
        var (textures, _, _) = RunTextureLoop(40, () => Interlocked.Increment(ref asked) <= 3);

        Assert.InRange(textures.Count, 1, 12);                             // it stopped well short of 40
        Assert.True(asked < 40);
    }

    [Fact]
    public async Task The_capped_texture_loop_waits_inside_the_checkpoint_while_the_editor_is_busy()
    {
        using var open = new ManualResetEventSlim(false);
        int reads = 0;
        var task = Task.Run(() => RunTextureLoop(10, () => { open.Wait(TimeSpan.FromSeconds(30)); return true; }, _ => Interlocked.Increment(ref reads)));

        Thread.Sleep(400);
        Assert.Equal(0, Volatile.Read(ref reads));                          // a map is loading: not one texture was read
        Assert.False(task.IsCompleted);

        open.Set();
        var finished = await task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(10, finished.Textures.Count);                          // and when it cleared the loop went on, it was not cancelled
    }

    // ================================================================================================ textures, against LeagueToolkit

    private static byte[] RandomBgraTexWithMips(int size, int seed)
    {
        var random = new Random(seed);
        int mips = (int)Math.Log2(size) + 1;
        var bytes = new List<byte> { (byte)'T', (byte)'E', (byte)'X', 0 };
        bytes.AddRange(BitConverter.GetBytes((ushort)size)); bytes.AddRange(BitConverter.GetBytes((ushort)size));
        bytes.AddRange(new byte[] { 1, 20, 0, 1 });                      // depth 1, BGRA8, 2D texture, has mips
        for (int level = mips - 1; level >= 0; level--)
        {
            int s = Math.Max(1, size >> level);
            var pixels = new byte[s * s * 4];
            random.NextBytes(pixels);
            bytes.AddRange(pixels);
        }
        return bytes.ToArray();
    }

    private static byte[] ReferenceMip(byte[] tex, int cap, out int pick, out int width, out int height)
    {
        using var ms = new MemoryStream(tex, writable: false);
        Texture reference = Texture.Load(ms);
        pick = 0;
        while (pick < reference.Mips.Length - 1 && Math.Max(reference.Mips[pick].Width, reference.Mips[pick].Height) > cap) pick++;
        var mip = reference.Mips[pick];
        width = mip.Width; height = mip.Height;
        var expected = new byte[mip.Width * mip.Height * 4];
        var pixels = mip.Span;
        int i = 0;
        for (int y = 0; y < mip.Height; y++)
            for (int x = 0; x < mip.Width; x++)
            {
                var c = pixels[y, x];
                expected[i++] = c.r; expected[i++] = c.g; expected[i++] = c.b; expected[i++] = c.a;
            }
        return expected;
    }

    [Fact]
    public void A_BGRA8_chain_is_decoded_byte_for_byte_as_LeagueToolkit_decodes_the_same_mip()
    {
        foreach (var (size, cap) in new[] { (256, 64), (512, 256), (512, 128) })
        {
            var tex = RandomBgraTexWithMips(size, seed: size);
            var expected = ReferenceMip(tex, cap, out int pick, out int w, out int h);

            var got = TextureDecoder.DecodeToMaxSize(tex, cap);

            Assert.True(pick > 0);
            Assert.Equal((w, h), (got.Width, got.Height));
            Assert.True(expected.AsSpan().SequenceEqual(got.Rgba), $"{size} capped at {cap}: mip {pick} differs from LeagueToolkit's");
        }
    }

    /// <summary>A block-compressed .tex (format 10 = BC1, 12 = BC3) of random blocks with a full mip chain, smallest first. Random BC1
    /// blocks put about half of them in the 3-colour mode whose fourth colour is a transparent texel - the "1bitalpha" cutouts.</summary>
    private static byte[] RandomBlockTexWithMips(byte format, int size, int seed)
    {
        var random = new Random(seed);
        int bytesPerBlock = format == 12 ? 16 : 8;
        int mips = (int)Math.Log2(size) + 1;
        var bytes = new List<byte> { (byte)'T', (byte)'E', (byte)'X', 0 };
        bytes.AddRange(BitConverter.GetBytes((ushort)size)); bytes.AddRange(BitConverter.GetBytes((ushort)size));
        bytes.AddRange(new byte[] { 1, format, 0, 1 });                  // depth 1, the format, 2D texture, has mips
        for (int level = mips - 1; level >= 0; level--)
        {
            int s = Math.Max(1, size >> level);
            int blocks = ((s + 3) / 4) * ((s + 3) / 4);
            var data = new byte[blocks * bytesPerBlock];
            random.NextBytes(data);
            bytes.AddRange(data);
        }
        return bytes.ToArray();
    }

    [Fact]
    public void Random_BC1_and_BC3_chains_including_the_transparent_texel_decode_byte_for_byte_as_LeagueToolkit_does()
    {
        foreach (var (format, size, cap) in new (byte, int, int)[] { (10, 512, 256), (10, 1024, 256), (10, 512, 64), (12, 512, 256), (12, 1024, 128) })
        {
            var tex = RandomBlockTexWithMips(format, size, seed: size + format);
            var expected = ReferenceMip(tex, cap, out int pick, out int w, out int h);

            var got = TextureDecoder.DecodeToMaxSize(tex, cap);

            Assert.True(pick > 0);
            Assert.Equal((w, h), (got.Width, got.Height));
            Assert.True(expected.AsSpan().SequenceEqual(got.Rgba), $"format {format} {size} capped at {cap}: mip {pick} differs from LeagueToolkit's");
            if (format == 10)
            {
                int transparent = 0;
                for (int a = 3; a < got.Rgba.Length; a += 4) if (got.Rgba[a] == 0) transparent++;
                Assert.True(transparent > 0, "the random BC1 chain has transparent texels, so the 1-bit alpha is what was compared");
            }
        }
    }

    // ================================================================================================ real textures

    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";

    [Fact]
    public void The_mip_a_thumbnail_decodes_is_byte_for_byte_the_one_LeagueToolkit_decodes_for_real_BC1_and_BC3()
    {
        // Real BGRA8 (format 20) textures are in Map22 only - thirteen particle vertex-animation strips, none with a mip chain,
        // so the mip path never sees one (they take the full decoder and the reducer); the BGRA8 chain is checked on a synthetic
        // texture above. Format 11 is in none of the map WADs: it is read exactly as format 10.
        var wads = new[] { "Map22", "Map11", "Map12", "Map30" }
            .Select(n => Path.Combine(Final, "Maps", "Shipping", n + ".wad.client")).Where(File.Exists).ToList();
        if (wads.Count == 0) { output.WriteLine("SKIPPED: no map WAD is installed"); return; }

        const int cap = 256;   // PrepareLimits.Thumbnail.MaxTextureSize
        const int PerFormat = 4;
        var db = new HashSyncService().LoadLocal(_ => { });
        // real textures of each stored format with a mip chain whose top level is bigger than the cap; for BC1 at least one with
        // a transparent texel (the "1bitalpha" cutouts: plain BC1 decodes those with an opaque alpha, LeagueToolkit does not)
        var found = new List<(string Name, string Path, byte Format, byte[] Data, bool Alpha)>();
        int scanned = 0;
        bool Enough() =>
            found.Count(f => f.Name == "BC1") >= PerFormat && found.Count(f => f.Name == "BC3") >= PerFormat
            && (found.Any(f => f.Alpha) || scanned > 4000);
        foreach (var wadPath in wads)
        {
            if (Enough()) break;
            using var wad = WadArchive.Open(wadPath, new WadPathResolver(db));
            foreach (var entry in wad.Entries.Where(e => e.IsResolved && e.Path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)
                                                         && e.UncompressedSize > 40_000))
            {
                if (Enough() || ++scanned > 8000) break;
                byte[] data;
                try { data = wad.Extract(entry); } catch { continue; }
                if (data.Length < 12 || data[0] != 'T' || data[1] != 'E' || data[2] != 'X' || data[3] != 0) continue;
                int width = BitConverter.ToUInt16(data, 4), height = BitConverter.ToUInt16(data, 6);
                byte depth = data[8], format = data[9], resource = data[10], flags = data[11];
                if (resource != 0 || depth > 1 || (flags & 1) == 0 || Math.Max(width, height) <= cap) continue;
                string name = format is 10 or 11 ? "BC1" : format == 12 ? "BC3" : "";
                if (name.Length == 0) continue;
                bool alpha = false;
                if (name == "BC1")
                {
                    var reference = ReferenceMip(data, cap, out _, out _, out _);
                    for (int a = 3; a < reference.Length && !alpha; a += 4) alpha = reference[a] != 255;
                }
                int have = found.Count(f => f.Name == name);
                if (have < PerFormat || (alpha && !found.Any(f => f.Alpha))) found.Add((name, entry.Path, format, data, alpha));
            }
        }
        output.WriteLine($"scanned {scanned} texture(s); compared {string.Join("; ", found.Select(f => $"{f.Name} f{f.Format}{(f.Alpha ? " with alpha" : "")} {f.Path}"))}");
        Assert.True(found.Count(f => f.Name == "BC1") >= 2 && found.Count(f => f.Name == "BC3") >= 2, "the game's maps hold BC1 and BC3 textures with mips");

        foreach (var (name, path, format, data, _) in found)
        {
            var expected = ReferenceMip(data, cap, out int pick, out int w, out int h);

            var got = TextureDecoder.DecodeToMaxSize(data, cap);

            Assert.True(pick > 0, $"{name} {path}: a texture bigger than the cap takes a smaller mip");
            Assert.Equal((w, h), (got.Width, got.Height));
            Assert.True(expected.AsSpan().SequenceEqual(got.Rgba), $"{name} (format {format}) {path}: mip {pick} differs from LeagueToolkit's");
        }
    }
}
