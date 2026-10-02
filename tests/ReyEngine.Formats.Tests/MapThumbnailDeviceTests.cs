using ReyEngine.App.Services;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M807: the real draw of a Content Browser map thumbnail - <see cref="MapThumbnailRenderer"/> on a worker thread of its own,
/// through a real D3D11 device, over the installed game's own map WADs (read-only), exactly as the service runs it.
///
/// <para>These need the game (<c>C:\Riot Games\League of Legends\Game\DATA\FINAL</c>) and a D3D11 device; without either they
/// say SKIPPED in the test output and pass, like the other device tests. Every test asserts on the picture itself - its size,
/// the share of it that is map, what the start state drew, the props - so a draw that quietly returned early fails: a run that
/// printed SKIPPED did not test anything.</para>
/// </summary>
public sealed class MapThumbnailDeviceTests(ITestOutputHelper output)
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";

    private sealed class Game : IDisposable
    {
        public readonly WadArchive Wad;
        public readonly HashDatabase Db;
        public Game(WadArchive wad, HashDatabase db) { Wad = wad; Db = db; }

        public static Game? Open(string map)
        {
            string path = Path.Combine(Final, "Maps", "Shipping", map + ".wad.client");
            if (!File.Exists(path) || !File.Exists(Path.Combine(Final, "ShaderCache.dx11.wad.client"))) return null;
            var db = new HashSyncService().LoadLocal(_ => { });
            return new Game(WadArchive.Open(path, new WadPathResolver(db)), db);
        }

        public MapThumbnailJob Job(string mapFolder, string skin)
        {
            string dir = $"data/maps/mapgeometry/{mapFolder}/";
            string mapPath = dir + skin + ".mapgeo", matsPath = dir + skin + ".materials.bin";
            ulong matsHash = HashAlgorithms.WadPath(matsPath);
            byte[]? Read(ulong h) { try { return Wad.TryGetEntry(h, out _) ? Wad.Extract(h) : null; } catch { return null; } }
            return new MapThumbnailJob
            {
                Key = skin, MapGeoPath = mapPath, MapGeoHash = HashAlgorithms.WadPath(mapPath),
                MaterialsBinPath = matsPath, MaterialsBinHash = matsHash,
                ControllerBins = Wad.Entries.Where(e => e.IsResolved && e.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                        && e.Path.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && e.PathHash != matsHash)
                    .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).Select(e => e.PathHash).ToList(),
                FinalDirectory = Final,
                Read = Read, Has = h => Wad.TryGetEntry(h, out _), Hashes = Db,
                BinName = h => Db.TryGetBinName(h, out var n) ? n : null,
                WadPath = h => Db.TryGetPath(h, out var p) ? p : null,
            };
        }

        public void Dispose() => Wad.Dispose();
    }

    /// <summary>Draw jobs one after another on ONE new thread, the way the service's worker does, and hand back the outcomes with
    /// the thread they ran on.</summary>
    private static (List<MapThumbnailOutcome> Outcomes, int ThreadId) OnWorker(MapThumbnailRenderer renderer, params MapThumbnailJob[] jobs)
    {
        var outcomes = new List<MapThumbnailOutcome>();
        int threadId = 0;
        var worker = new Thread(() =>
        {
            threadId = System.Environment.CurrentManagedThreadId;
            foreach (var job in jobs) outcomes.Add(renderer.Render(job));
            renderer.ReleaseDevice();
        }) { Name = "test thumbnail worker", Priority = ThreadPriority.BelowNormal, IsBackground = true };
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromMinutes(3)), "the worker finished");
        return (outcomes, threadId);
    }

    private bool Skipped(MapThumbnailOutcome outcome)
    {
        if (!outcome.DeviceFailed) return false;
        output.WriteLine("SKIPPED: no D3D11 device here - " + outcome.Failure);
        return true;
    }

    private static (int Width, int Height, double Mean) Inspect(byte[] png)
    {
        using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgb24>(png);
        double sum = 0;
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++) { var p = image[x, y]; sum += (p.R + p.G + p.B) / 3.0; }
        return (image.Width, image.Height, sum / (image.Width * image.Height));
    }

    private static double Difference(byte[] a, byte[] b)
    {
        using var ia = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgb24>(a);
        using var ib = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgb24>(b);
        int changed = 0;
        for (int y = 0; y < ia.Height; y++)
            for (int x = 0; x < ia.Width; x++)
            {
                var p = ia[x, y]; var q = ib[x, y];
                if (Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B) > 24) changed++;
            }
        return (double)changed / (ia.Width * ia.Height);
    }

    [Fact]
    public void A_real_tft_board_is_drawn_on_a_worker_thread_with_its_props()
    {
        using var game = Game.Open("Map22");
        if (game is null) { output.WriteLine("SKIPPED: Map22.wad.client / the shader cache are not installed"); return; }
        var job = game.Job("map22", "anniversary");

        var (withProps, threadId) = OnWorker(new MapThumbnailRenderer(), job);
        var outcome = Assert.Single(withProps);
        if (Skipped(outcome)) return;

        Assert.NotEqual(System.Environment.CurrentManagedThreadId, threadId);          // not the test's thread: nothing here touches the UI thread
        Assert.True(outcome.Png is not null, "a picture: " + outcome.Failure);
        var (w, h, mean) = Inspect(outcome.Png!);
        Assert.Equal((MapThumbnailKey.ThumbWidth, MapThumbnailKey.ThumbHeight), (w, h));
        Assert.InRange(outcome.NonClearShare, 0.25, 1.0);                       // the board fills the picture, not the editor's background
        Assert.InRange(mean, 25, 235);                                           // neither black nor blown out
        Assert.True(outcome.Slices >= 12, $"the board's meshes were drawn ({outcome.Slices})");
        Assert.True(outcome.PropInstances > 0 && outcome.PropMeshes > 0, "anniversary places props");
        Assert.True(outcome.PropsDrawn > 0, "and the props reached the device");
        Assert.Contains("Board ready (level 1)", outcome.Summary);               // the stage a board is played in
        output.WriteLine($"anniversary on thread {threadId}: {outcome.TotalMs:0} ms [{outcome.Timings}]");
        output.WriteLine(outcome.Summary);

        // and the props are IN the picture: the same board without them is a different image
        var (without, _) = OnWorker(new MapThumbnailRenderer { DrawProps = false }, job);
        Assert.NotNull(without[0].Png);
        Assert.Equal(0, without[0].PropInstances);
        double changed = Difference(outcome.Png!, without[0].Png!);
        output.WriteLine($"props change {changed:P2} of the picture");
        Assert.True(changed > 0.002, "the props show in the picture");
    }

    [Fact]
    public void A_board_is_drawn_in_the_stage_it_is_played_in_not_the_state_before_it_is_ready()
    {
        using var game = Game.Open("Map22");
        if (game is null) { output.WriteLine("SKIPPED: Map22.wad.client / the shader cache are not installed"); return; }

        // Lux flags 38 of its 40 groups for a stage; at the bare initial mask (67) 2 of her 38 materials were drawn
        var (outcomes, _) = OnWorker(new MapThumbnailRenderer(), game.Job("map22", "lux"));
        var outcome = Assert.Single(outcomes);
        if (Skipped(outcome)) return;

        Assert.True(outcome.Png is not null, outcome.Failure);
        Assert.True(outcome.Slices >= 20, $"the board's own content is on ({outcome.Slices} slices drawn, {outcome.HiddenSlices} hidden)");
        Assert.True(outcome.HiddenSlices < outcome.Slices);
        Assert.InRange(outcome.NonClearShare, 0.5, 1.0);
        output.WriteLine($"lux: {outcome.Summary}");
    }

    [Fact]
    public void The_one_renderer_draws_map_after_map_reuses_its_device_and_survives_a_release()
    {
        using var game = Game.Open("Map30");
        if (game is null) { output.WriteLine("SKIPPED: Map30.wad.client / the shader cache are not installed"); return; }
        var renderer = new MapThumbnailRenderer();

        var (first, _) = OnWorker(renderer, game.Job("map30", "arenaa"), game.Job("map30", "arenab"), game.Job("map30", "arenac"));
        if (Skipped(first[0])) return;
        Assert.All(first, o => Assert.True(o.Png is not null, o.Failure));
        Assert.InRange(Difference(first[0].Png!, first[1].Png!), 0.2, 1.0);       // different maps, different pictures
        output.WriteLine(string.Join(" | ", first.Select(o => $"{o.TotalMs:0} ms")));

        var (again, _) = OnWorker(renderer, game.Job("map30", "arenaa"));          // after ReleaseDevice: it starts again
        Assert.NotNull(again[0].Png);
        Assert.Equal(0.0, Difference(first[0].Png!, again[0].Png!), 3);            // and draws the same picture: it is deterministic
    }

    [Fact]
    public void A_map_that_has_nothing_to_draw_is_a_remembered_failure_and_a_cancelled_job_is_neither()
    {
        using var game = Game.Open("Map22");
        if (game is null) { output.WriteLine("SKIPPED: Map22.wad.client / the shader cache are not installed"); return; }

        var empty = game.Job("map22", "carousel_memorybudget");                    // a mapgeo with no geometry in it
        var cancelled = game.Job("map22", "anniversary");
        cancelled.IsCancelled = () => true;                                         // the project moved on before the draw began

        var (outcomes, _) = OnWorker(new MapThumbnailRenderer(), empty, cancelled);
        if (Skipped(outcomes[0])) return;

        Assert.Null(outcomes[0].Png);
        Assert.True(outcomes[0].Permanent, "no geometry is a fact about the map");
        Assert.Contains("no geometry", outcomes[0].Failure);
        Assert.True(outcomes[1].Cancelled);
        Assert.False(outcomes[1].Permanent);
        Assert.Null(outcomes[1].Png);
    }

    [Fact]
    public void A_single_state_map_with_hundreds_of_materials_draws_all_of_them_and_fills_the_picture()
    {
        using var game = Game.Open("Map12");
        if (game is null) { output.WriteLine("SKIPPED: Map12.wad.client / the shader cache are not installed"); return; }

        var (outcomes, _) = OnWorker(new MapThumbnailRenderer(), game.Job("map12", "base"));
        var outcome = Assert.Single(outcomes);
        if (Skipped(outcome)) return;

        Assert.True(outcome.Png is not null, outcome.Failure);
        Assert.True(outcome.Slices > 200, $"ARAM's start state draws its slices ({outcome.Slices})");
        Assert.Equal(0, outcome.HiddenSlices);                                      // a single-state map hides nothing at its own state
        Assert.True(outcome.PropInstances > 0);
        Assert.InRange(outcome.NonClearShare, 0.5, 1.0);
        output.WriteLine($"map12 base: {outcome.TotalMs:0} ms; {outcome.Summary}");
    }
}
