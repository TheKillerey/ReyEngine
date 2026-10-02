using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.MapGeo;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M807: the Content Browser's map thumbnails - a rendered picture of the map in place of a .mapgeo tile's glyph.
///
/// <para>This file holds everything that needs no GPU: the cache key and the disk cache, the camera, the visible-tile window
/// and the markup it is pinned to, the start state, the sun's render form against the view model's, the texture reduction,
/// and the service itself (visible tiles only, dropped queue, cancellation, pause, device failure, memory and disk cache)
/// driven with a fake renderer. The real draw - on a worker thread, through the D3D11 device - is in
/// <see cref="MapThumbnailDeviceTests"/>.</para>
/// </summary>
public sealed partial class MapThumbnailTests(ITestOutputHelper output)
{
    // ================================================================================================ the cache key

    private static string Key(string app = "0.4.10", string state = "start", string map = "data/maps/mapgeometry/map22/lux.mapgeo",
        string mapId = "wad:1:2:3:4", string mats = "data/maps/mapgeometry/map22/lux.materials.bin", string matsId = "wad:1:2:5:6",
        string ship = "wad:1:2:7:8", string cache = "file:100:200") =>
        MapThumbnailKey.Compute(app, state, map, mapId, mats, matsId, ship, cache);

    [Fact]
    public void The_key_is_32_lowercase_hex_digits_and_the_same_inputs_give_the_same_key()
    {
        string key = Key();
        Assert.Equal(32, key.Length);
        Assert.Matches("^[0-9a-f]{32}$", key);
        Assert.Equal(key, Key());
        Assert.Equal(key, Key(map: "DATA/Maps/MapGeometry/Map22/LUX.mapgeo"));   // a path is a path whatever its case
    }

    [Fact]
    public void Every_input_that_decides_the_picture_is_in_the_key()
    {
        string key = Key();
        var changed = new Dictionary<string, string>
        {
            ["app version"] = Key(app: "0.4.11"),
            ["state (a board stage, Game Depth)"] = Key(state: "start+clientdepth"),
            ["map path"] = Key(map: "data/maps/mapgeometry/map22/anniversary.mapgeo"),
            ["map identity (edited or patched)"] = Key(mapId: "wad:1:2:3:5"),
            ["materials path"] = Key(mats: "data/maps/mapgeometry/map22/other.materials.bin"),
            ["materials identity"] = Key(matsId: "file:9:9"),
            ["shipping bin identity"] = Key(ship: "none"),
            ["shader cache identity (a game patch)"] = Key(cache: "file:100:201"),
        };
        foreach (var (what, other) in changed) Assert.NotEqual(key, other);
        Assert.Equal(changed.Count, changed.Values.Distinct().Count());   // and each one moves it somewhere of its own
        output.WriteLine($"{changed.Count} inputs, each changes the key");
    }

    [Fact]
    public void The_two_identities_name_a_file_by_its_length_and_write_time_and_a_wad_chunk_by_its_sizes()
    {
        Assert.Equal("file:12:34", MapThumbnailKey.FileIdentity(12, 34));
        Assert.Equal("wad:1:2:3:4", MapThumbnailKey.WadChunkIdentity(1, 2, 3, 4));
        Assert.NotEqual(MapThumbnailKey.FileIdentity(12, 34), MapThumbnailKey.FileIdentity(12, 35));
        Assert.NotEqual(MapThumbnailKey.WadChunkIdentity(1, 2, 3, 4), MapThumbnailKey.WadChunkIdentity(1, 2, 3, 5));
    }

    [Fact]
    public void The_size_is_the_tile_cards_aspect_at_three_times_the_pixels()
    {
        Assert.Equal(MapThumbnailKey.RenderWidth, MapThumbnailKey.ThumbWidth * 2);
        Assert.Equal(MapThumbnailKey.RenderHeight, MapThumbnailKey.ThumbHeight * 2);
        double card = 66.0 / 46.0, thumb = (double)MapThumbnailKey.ThumbWidth / MapThumbnailKey.ThumbHeight;
        Assert.InRange(thumb / card, 0.995, 1.005);   // UniformToFill crops nothing worth seeing
    }

    // ================================================================================================ the disk cache

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "reyengine_mapthumb_" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); } catch { } }
    }

    [Fact]
    public void A_picture_round_trips_through_the_cache_and_leaves_no_temporary_file()
    {
        using var dir = new TempDir();
        var cache = new MapThumbnailCache(dir.Path);
        Assert.False(cache.TryReadPng("abc", out _));

        Assert.True(cache.WritePng("abc", new byte[] { 1, 2, 3 }));
        Assert.True(cache.TryReadPng("abc", out var png));
        Assert.Equal(new byte[] { 1, 2, 3 }, png);
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));

        Assert.True(cache.WritePng("abc", new byte[] { 9 }));                      // replacing is allowed
        Assert.True(cache.TryReadPng("abc", out png));
        Assert.Equal(new byte[] { 9 }, png);
        Assert.False(cache.WritePng("empty", ReadOnlySpan<byte>.Empty));          // nothing worth caching
    }

    [Fact]
    public void A_failure_is_remembered_until_a_picture_replaces_it_or_the_tile_is_refreshed()
    {
        using var dir = new TempDir();
        var cache = new MapThumbnailCache(dir.Path);
        Assert.Null(cache.TryReadFailure("k"));

        Assert.True(cache.WriteFailure("k", "no material resolved"));
        Assert.Equal("no material resolved", cache.TryReadFailure("k"));

        Assert.True(cache.WritePng("k", new byte[] { 1 }));
        Assert.Null(cache.TryReadFailure("k"));                                    // a picture supersedes the failure

        cache.WriteFailure("k", "again");
        cache.Delete("k");                                                          // Refresh Thumbnail
        Assert.Null(cache.TryReadFailure("k"));
        Assert.False(cache.TryReadPng("k", out _));
    }

    [Fact]
    public void Pruning_drops_the_least_recently_written_and_stale_temporary_files()
    {
        using var dir = new TempDir();
        var cache = new MapThumbnailCache(dir.Path);
        for (int i = 0; i < 10; i++)
        {
            cache.WritePng($"k{i}", new byte[] { 1 });
            File.SetLastWriteTimeUtc(cache.PngPath($"k{i}"), DateTime.UtcNow.AddMinutes(-100 + i));   // k0 oldest
        }
        string stale = Path.Combine(dir.Path, "k0.deadbeef.tmp");
        File.WriteAllBytes(stale, new byte[] { 1 });
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-3));
        string fresh = Path.Combine(dir.Path, "k1.fresh.tmp");
        File.WriteAllBytes(fresh, new byte[] { 1 });                                // a write in progress

        int deleted = cache.Prune(5);                                               // keeps 80% of 5 = 4

        Assert.Equal(1 + 6, deleted);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        Assert.False(cache.TryReadPng("k0", out _));
        Assert.True(cache.TryReadPng("k9", out _));
        Assert.Equal(4, Directory.GetFiles(dir.Path, "*.png").Length);
    }

    [Fact]
    public void The_default_folder_is_the_per_user_cache_beside_the_workshop_catalog()
    {
        string root = MapThumbnailCache.DefaultRoot();
        string appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        Assert.StartsWith(appData, root);
        Assert.EndsWith(Path.Combine("ReyEngine", "Cache", "map-thumbnails", "v1"), root);
        // ...\ReyEngine\Cache\workshop-catalog-v7.json and ...\ReyEngine\Cache\map-thumbnails\v1 share a folder
        Assert.Equal(Path.GetDirectoryName(WorkshopCatalogService.CachePath), Path.GetDirectoryName(Path.GetDirectoryName(root)));
    }

    // ================================================================================================ the camera

    private static List<Vector3> Ground(float x0, float x1, float z0, float z1, float y, int n)
    {
        var pts = new List<Vector3>(n * n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                pts.Add(new Vector3(x0 + (x1 - x0) * i / (n - 1), y, z0 + (z1 - z0) * j / (n - 1)));
        return pts;
    }

    /// <summary>The NDC extents of a world box under the camera as the renderer draws it (X mirrored ahead of the view).</summary>
    private static (float Max, float MinX, float MaxX) Project(MapThumbnailFrame f)
    {
        float max = 0, minX = float.MaxValue, maxX = float.MinValue;
        var viewProj = Matrix4x4.CreateScale(-1f, 1f, 1f) * f.View * f.Projection;
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? f.BoxMin.X : f.BoxMax.X, (i & 2) == 0 ? f.BoxMin.Y : f.BoxMax.Y, (i & 4) == 0 ? f.BoxMin.Z : f.BoxMax.Z);
            var clip = Vector4.Transform(new Vector4(corner, 1f), viewProj);
            Assert.True(clip.W > 0, "a corner of the framed box is behind the camera");
            float x = clip.X / clip.W, y = clip.Y / clip.W;
            max = MathF.Max(max, MathF.Max(MathF.Abs(x), MathF.Abs(y)));
            minX = MathF.Min(minX, x); maxX = MathF.Max(maxX, x);
        }
        return (max, minX, maxX);
    }

    [Fact]
    public void The_box_is_where_the_triangles_are_and_ignores_the_outer_two_percent()
    {
        var points = Ground(0, 10000, 0, 10000, 5f, 200);             // 40,000 triangles of ground
        for (int i = 0; i < 200; i++) points.Add(new Vector3(1_000_000f + i, 9000f, -900_000f));   // a far sky bowl: 0.5%

        Assert.True(MapThumbnailCamera.TryBox(points, out var min, out var max));

        Assert.InRange(min.X, 0, 400);                                 // the ground, not the bowl
        Assert.InRange(max.X, 9600, 10000);
        Assert.InRange(min.Z, 0, 400);
        Assert.InRange(max.Z, 9600, 10000);
        Assert.Equal(5f, min.Y);                                       // the floor is the median height
        Assert.Equal(min.Y + MathF.Max(max.X - min.X, max.Z - min.Z) * MapThumbnailCamera.HeightShare, max.Y, 2);
    }

    [Theory]
    [InlineData(0, 4000, 0, 4000)]          // a TFT board
    [InlineData(-600, 15400, -600, 15400)]  // Summoner's Rift
    [InlineData(0, 22000, 0, 6000)]         // a long, thin map
    [InlineData(100, 140, 100, 4000)]       // a corridor
    public void The_frame_fills_the_view_without_leaving_it(float x0, float x1, float z0, float z1)
    {
        var points = Ground(x0, x1, z0, z1, 0f, 60);

        Assert.True(MapThumbnailCamera.TryFrame(points, 384f / 268f, out var frame));

        var (max, minX, maxX) = Project(frame);
        Assert.InRange(max, 0.84f, 0.97f);                             // fills it (0.92 asked), and never pokes out
        Assert.InRange(frame.Extent, 0.84f, 0.97f);
        Assert.InRange((minX + maxX) / 2, -0.12f, 0.12f);              // centred sideways
        // from above and behind: a 56 degree pitch
        var look = Vector3.Normalize(frame.Eye - frame.Target);
        Assert.Equal(MathF.Atan2(1.5f, 1f) * 180f / MathF.PI, MathF.Asin(look.Y) * 180f / MathF.PI, 0.5f);
        Assert.True(frame.Near > 0 && frame.Far > frame.Distance * 2f);
        output.WriteLine($"{x0}..{x1} x {z0}..{z1}: distance {frame.Distance:0}, extent {frame.Extent:0.00}");
    }

    [Fact]
    public void A_wider_frame_is_limited_by_the_boards_height_on_screen_and_a_square_one_by_its_width()
    {
        var board = Ground(0, 4000, 0, 4000, 0f, 40);
        Assert.True(MapThumbnailCamera.TryFrame(board, 1.0f, out var square));
        Assert.True(MapThumbnailCamera.TryFrame(board, 2.0f, out var wide));
        // seen from 56 degrees up the board is foreshortened, so it is wider on screen than tall: a wide frame has room to
        // spare sideways and closes in until the height fills it; a square one is full across first
        Assert.True(wide.Distance < square.Distance);
        Assert.InRange(Project(square).Max, 0.84f, 0.97f);
        Assert.InRange(Project(wide).Max, 0.84f, 0.97f);
    }

    [Fact]
    public void One_point_or_nothing_still_has_an_answer()
    {
        Assert.False(MapThumbnailCamera.TryFrame(new List<Vector3>(), 1.4f, out _));
        Assert.False(MapThumbnailCamera.TryFrame(new List<Vector3> { new(float.NaN, 0, 0) }, 1.4f, out _));
        Assert.False(MapThumbnailCamera.TryFrame(new List<Vector3> { Vector3.Zero }, 0f, out _));     // no aspect, no frame

        Assert.True(MapThumbnailCamera.TryFrame(new List<Vector3> { new(500, 20, 500) }, 1.4f, out var frame));   // a box with sides to look at
        Assert.InRange(Project(frame).Max, 0.84f, 0.97f);
    }

    [Fact]
    public void Sampling_takes_every_nth_triangle_of_the_drawn_ranges_and_skips_what_is_not_a_triangle()
    {
        // a strip of 1,000 triangles along X, three vertices each, centroid at x = t
        var positions = new float[1000 * 9];
        var indices = new uint[1000 * 3];
        for (int t = 0; t < 1000; t++)
        {
            for (int c = 0; c < 3; c++)
            {
                int v = t * 3 + c;
                positions[v * 3] = t; positions[v * 3 + 1] = 2f; positions[v * 3 + 2] = 7f;
                indices[v] = (uint)v;
            }
        }

        var all = MapThumbnailCamera.SampleCentroids(positions, indices, new[] { (0, 3000) }, maxSamples: 5000);
        Assert.Equal(1000, all.Count);
        Assert.Equal(new Vector3(0, 2, 7), all[0]);

        var thinned = MapThumbnailCamera.SampleCentroids(positions, indices, new[] { (0, 3000) }, maxSamples: 100);
        Assert.InRange(thinned.Count, 90, 100);                         // bounded however big the map

        var half = MapThumbnailCamera.SampleCentroids(positions, indices, new[] { (1500, 1500) }, maxSamples: 5000);
        Assert.Equal(500, half.Count);
        Assert.Equal(500f, half[0].X);                                  // only the range asked for

        // a range past the end, one too short for a triangle, one with an index outside the vertices: skipped, never thrown
        var bad = (uint[])indices.Clone();
        bad[0] = 99999;
        var guarded = MapThumbnailCamera.SampleCentroids(positions, bad, new[] { (0, 6), (2998, 2), (-3, 9), (50000, 30) }, maxSamples: 10);
        Assert.Single(guarded);                                          // only the second triangle of the first range
    }

    // ================================================================================================ the visible window

    [Theory]
    [InlineData(1000, 0, 1020, 300, 0, 49)]        // 10 columns; rows 0-3 in view, one more below -> 5 rows
    [InlineData(1000, 940, 1020, 300, 90, 149)]    // scrolled to row 10: row 9 above, rows 10-13 in view, row 14 below
    [InlineData(1000, 99_000, 1020, 300, 0, -1)]   // scrolled past the end: nothing
    [InlineData(5, 0, 1020, 300, 0, 4)]            // fewer tiles than the window
    [InlineData(0, 0, 1020, 300, 0, -1)]           // no tiles
    [InlineData(100, 0, 60, 300, 0, 4)]            // narrower than one cell: a single column, 5 rows
    [InlineData(100, 0, 0, 300, 0, -1)]            // not laid out yet
    [InlineData(100, 0, 1020, 0, 0, -1)]
    public void The_window_names_the_tiles_in_view_from_the_fixed_cell_size(int count, double offset, double width, double height, int first, int last)
    {
        Assert.Equal((first, last), ContentGridViewport.VisibleRange(count, offset, width, height));
    }

    [Fact]
    public void The_grid_hands_the_view_model_only_the_tiles_in_view_and_nothing_for_the_list_view()
    {
        var browser = new ContentBrowserViewModel();
        var nodes = Enumerable.Range(0, 300).Select(i => MapNode($"m{i}")).ToList();
        foreach (var n in nodes) browser.Items.Add(n);
        IReadOnlyList<AssetNodeViewModel>? seen = null;
        browser.VisibleItemsChanged = v => seen = v;

        browser.ReportGridViewport(offsetY: 0, contentWidth: 1020, viewportHeight: 300);
        Assert.Equal(nodes.Take(50), seen);

        browser.ReportGridViewport(offsetY: 940, contentWidth: 1020, viewportHeight: 300);
        Assert.Equal(nodes.Skip(90).Take(60), seen);

        browser.ListView = true;
        browser.ReportGridViewport(offsetY: 0, contentWidth: 1020, viewportHeight: 300);
        Assert.Empty(seen!);                                            // the list view shows icons only
    }

    private static string? Source(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        // a redirected build output sits outside the repo: walk up from this file instead
        return SourceFromHere(parts);
    }

    private static string? SourceFromHere(string[] parts, [System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(here)!); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        return null;
    }

    [Fact]
    public void The_markup_is_pinned_to_the_cell_size_the_window_is_computed_from()
    {
        string? xaml = Source("src", "ReyEngine.App", "Views", "MainWindow.axaml");
        if (xaml is null) { output.WriteLine("SKIPPED: MainWindow.axaml not found"); return; }

        // the grid tile: a fixed button in a WrapPanel - change one side without the other and the wrong tiles light up
        Assert.Contains($"<Button Width=\"{ContentGridViewport.TileWidth}\" Height=\"{ContentGridViewport.TileHeight}\" Margin=\"{ContentGridViewport.TileMargin}\"", xaml);
        Assert.Contains("<ScrollViewer x:Name=\"BrowserGridScroll\" Padding=\"6,4\" IsVisible=\"{Binding !ContentBrowser.ListView}\">", xaml);
        Assert.Contains("<ItemsControl x:Name=\"BrowserGrid\" ItemsSource=\"{Binding ContentBrowser.Items}\">", xaml);
        // the tile card the picture goes into, and the picture binding the textures already use
        Assert.Contains("Width=\"66\" Height=\"46\"", xaml);
        Assert.Contains("<Image Source=\"{Binding Thumbnail}\" Stretch=\"UniformToFill\" IsVisible=\"{Binding HasThumbnail}\" />", xaml);
        // Refresh Thumbnail: on map tiles only, through the view model's command
        Assert.Contains("Header=\"Refresh Thumbnail\" IsVisible=\"{Binding WantsMapThumbnail}\"", xaml);
        Assert.Contains("RefreshMapThumbnailCommand", xaml);
        Assert.True(typeof(MainWindowViewModel).GetProperty("RefreshMapThumbnailCommand") is not null);
        Assert.NotNull(typeof(AssetNodeViewModel).GetProperty("WantsMapThumbnail"));

        string? code = Source("src", "ReyEngine.App", "Views", "MainWindow.axaml.cs");
        if (code is null) return;
        Assert.Contains("FindControl<ScrollViewer>(\"BrowserGridScroll\")", code);
        Assert.Contains("ReportGridViewport(scroll.Offset.Y, grid.Bounds.Width, Shown() ? scroll.Viewport.Height : 0)", code);
        Assert.Contains("TimeSpan.FromMilliseconds(150)", code);        // debounced
    }

    // ================================================================================================ the start state

    private static byte[] Bin(params BinTreeObject[] objects)
    {
        using var stream = new MemoryStream();
        new BinTree(objects, Array.Empty<string>()).Write(stream);
        return stream.ToArray();
    }

    private static MapGeoAsset MapOf(params (int Flags, uint Controller)[] groups) => new()
    {
        Positions = new float[9], Normals = new float[9], Uvs = new float[6], Indices = new uint[] { 0, 1, 2 },
        Groups = groups.Select((g, i) => new MapGeoGroup("g" + i, 0, 3, VisibilityFlags: g.Flags, ControllerHash: g.Controller)).ToArray(),
    };

    private static readonly MapVisibilityAxis Rift = new(MapVisibility.PrimaryAxisHash, "Map Visibility", 1, true, new[]
    {
        new VisibilityLayer("Base", 1), new VisibilityLayer("Infernal", 2), new VisibilityLayer("Mountain", 4),
    });

    private static readonly MapVisibilityAxis Board = new(MapVisibility.PrimaryAxisHash, "Map Visibility", 67, true, new[]
    {
        new VisibilityLayer("Stage1", 4), new VisibilityLayer("Stage2", 8), new VisibilityLayer("Stage3", 16), new VisibilityLayer("base", 64),
    });

    private static MapBoardStage Stage(int mask) => new("Board ready (level 1)", "MapBehavior_BoardReady", "ArenaSkin_Cue_BoardReady", null, mask,
        new Dictionary<string, bool>(), new[] { "MapBehavior_BoardReady" }, 0);

    [Fact]
    public void A_map_that_starts_on_one_state_is_drawn_on_that_state_and_not_on_all_of_them()
    {
        // Summoner's Rift: Base alone persists, a dragon decides once a mask names one (M771). The editor opens on All.
        var map = MapOf((255, 0), (1, 0), (2, 0), (4, 0), (3, 0), (0, 0));
        var state = MapStartState.Resolve(map, new MapVisibilityDefinition(new[] { Rift }), new[] { Bin() }, boardStage: null);

        Assert.Null(state.StageMask);
        Assert.Equal(1, state.Selections[MapVisibility.PrimaryAxisHash]);
        Assert.Equal(new[] { true, true, false, false, true, true }, state.GroupVisible);   // 255 and 0 are every layer; 3 holds Base
    }

    [Fact]
    public void A_tft_board_is_drawn_as_the_stage_it_is_played_in_not_the_state_before_it_is_ready()
    {
        var map = MapOf((4, 0), (8, 0), (64, 0), (255, 0));

        var start = MapStartState.Resolve(map, new MapVisibilityDefinition(new[] { Board }), new[] { Bin() }, boardStage: null);
        Assert.Equal(67, start.StageMask);                                        // no stage to name: the initial mask itself
        Assert.Equal(new[] { false, false, true, true }, start.GroupVisible);     // the board's own content (4) is not on yet

        var ready = MapStartState.Resolve(map, new MapVisibilityDefinition(new[] { Board }), new[] { Bin() }, Stage(20));
        Assert.Equal(20, ready.StageMask);
        Assert.Equal(new[] { true, false, false, true }, ready.GroupVisible);      // Stage1 + Stage3: the board
    }

    [Fact]
    public void A_map_without_a_shipping_bin_has_no_start_state_to_invent()
    {
        var map = MapOf((1, 0), (2, 0), (4, 0));
        var state = MapStartState.Resolve(map, (byte[]?)null, new[] { Bin() }, _ => null, boardStage: null);
        Assert.Null(state.StageMask);
        Assert.Empty(state.Selections);
        Assert.All(state.GroupVisible, v => Assert.True(v));
        // and a board stage does nothing without an axis to apply it to
        var staged = MapStartState.Resolve(map, (byte[]?)null, new[] { Bin() }, _ => null, Stage(20));
        Assert.Null(staged.StageMask);
    }

    private const uint Hall = 0x76c50391;

    [Fact]
    public void Event_only_content_is_hidden_because_every_event_is_off_at_the_start()
    {
        var mutator = new BinTreeObject(Hall, HashAlgorithms.Fnv1a("MutatorMapVisibilityController"),
            new BinTreeProperty[] { new BinTreeString(HashAlgorithms.Fnv1a("MutatorName"), "SR_Hall_Of_Legends") });
        var map = MapOf((255, 0), (255, Hall));

        var state = MapStartState.Resolve(map, new MapVisibilityDefinition(new[] { Rift }), new[] { Bin(mutator) }, boardStage: null);

        Assert.Equal(new[] { true, false }, state.GroupVisible);
        Assert.False(state.LevelPropShown(new MapLevelProp("p", Vector3.Zero, Matrix4x4.Identity, "SRU_Snail", VisibilityControllerHash: Hall)));
        Assert.True(state.LevelPropShown(new MapLevelProp("p", Vector3.Zero, Matrix4x4.Identity, "SRU_Snail")));
    }

    [Fact]
    public void Props_follow_the_same_rule_as_the_stage_does()
    {
        var map = MapOf((255, 0));
        MapAnimatedProp Prop(int flags) => new("p", Vector3.Zero, Matrix4x4.Identity, "Characters/X/CharacterRecords/Root", "Characters/X/Skins/Skin0",
            VisibilityFlags: flags, HasVisibilityFlags: true);
        MapLevelProp Level(int flags) => new("p", Vector3.Zero, Matrix4x4.Identity, "X", VisibilityFlags: flags, HasVisibilityFlags: true);

        var plain = MapStartState.Resolve(map, new MapVisibilityDefinition(new[] { Rift }), new[] { Bin() }, boardStage: null);
        Assert.True(plain.PropShown(Prop(2)));                         // the Map Visibility layer never filtered a prop
        Assert.False(plain.PropShown(Prop(0)));                        // flags 0 is a disabled prop
        Assert.False(plain.LevelPropShown(Level(0)));

        var board = MapStartState.Resolve(map, new MapVisibilityDefinition(new[] { Board }), new[] { Bin() }, Stage(20));
        Assert.True(board.PropShown(Prop(4)));                         // shares a bit with 20
        Assert.False(board.PropShown(Prop(64)));
        Assert.True(board.PropShown(Prop(255)));
        Assert.True(board.LevelPropShown(Level(16)));
        Assert.False(board.LevelPropShown(Level(8)));
    }

    // ================================================================================================ the sun

    private static void AssertSameSun(MapSunProperties expected, MapSunProperties actual)
    {
        foreach (var p in typeof(MapSunProperties).GetProperties().Where(p => p.GetIndexParameters().Length == 0))
            Assert.True(Equals(p.GetValue(expected), p.GetValue(actual)), $"{p.Name}: {p.GetValue(expected)} vs {p.GetValue(actual)}");
    }

    [Fact]
    public void The_render_form_of_a_sun_is_exactly_what_the_view_model_publishes_for_it()
    {
        var rich = new MapSunProperties
        {
            SunColor = new Vector4(0.87f, 1.4f, 0.6f, 1f), SunIntensityScale = 0.5f, SunDirection = new Vector3(-0.3f, 0.9f, 0.2f),
            SkyLightColor = new Vector4(0.2f, 0.4f, 1.3f, 1f), SkyLightScale = 2.2f, LightMapColorScale = 2f,
            HorizonColor = new Vector4(0.9f, 0.5f, 0.1f, 1f), GroundColor = new Vector4(0.1f, 0.2f, 0.3f, 1f),
            FogEnabled = true, FogColor = new Vector4(0.3f, 0.2f, 0.9f, 1f), FogAlternateColor = new Vector4(0.05f, 0.1f, 0.4f, 1f),
            FogStartAndEnd = new Vector2(500f, -1800f), FogEmissiveRemap = 1.5f, FogLowQualityModeEmissiveRemap = 0.1f,
            SunRadiusForShadows = 3f, ScaleSunShadowIntensity = 0.7f, ShadowBias = 0.001f,
        };
        var apply = typeof(MainWindowViewModel).GetMethod("ApplySunProperties", BindingFlags.NonPublic | BindingFlags.Instance)!;

        foreach (var authored in new MapSunProperties?[] { rich, new MapSunProperties(), null })
        {
            var vm = new MainWindowViewModel();
            apply.Invoke(vm, new object?[] { authored });
            AssertSameSun(vm.CurrentSunProperties!, MapSunRender.RenderForm(authored));
            Assert.Equal(vm.CurrentLightmapScale, MapSunRender.LightmapScale(authored));
        }
    }

    [Fact]
    public void A_map_without_a_sun_gets_the_one_sun_the_editor_defines()
    {
        var none = MapSunRender.NoMapSun();
        Assert.False(none.FogEnabled);
        Assert.Equal(Vector2.Zero, none.FogStartAndEnd);
        var viaVm = (MapSunProperties)typeof(MainWindowViewModel).GetMethod("NoMapSun", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        AssertSameSun(none, viaVm);
        Assert.Equal(1f, MapSunRender.RenderForm(null).SunIntensityScale);
    }

    // ================================================================================================ textures

    [Fact]
    public void Reducing_a_texture_averages_whole_blocks_and_leaves_one_that_fits_alone()
    {
        // 8x4: the left half is (200,100,50,255), the right (0,0,0,0); halving twice (cap 2) gives 2x1
        var rgba = new byte[8 * 4 * 4];
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                int i = (y * 8 + x) * 4;
                rgba[i] = 200; rgba[i + 1] = 100; rgba[i + 2] = 50; rgba[i + 3] = 255;
            }
        var image = new TextureImage(8, 4, rgba);

        var reduced = TextureReduce.ToMaxSize(image, 2);

        Assert.Equal((2, 1), (reduced.Width, reduced.Height));
        Assert.Equal(new byte[] { 200, 100, 50, 255, 0, 0, 0, 0 }, reduced.Rgba);
        Assert.Same(image, TextureReduce.ToMaxSize(image, 8));         // already fits
        Assert.Same(image, TextureReduce.ToMaxSize(image, 0));         // no cap
        Assert.Same(image, TextureReduce.ToMaxSize(image, -5));
    }

    [Fact]
    public void A_size_that_is_not_a_power_of_two_keeps_every_texel_in_an_edge_block()
    {
        var rgba = new byte[10 * 6 * 4];
        Array.Fill(rgba, (byte)100);
        for (int y = 0; y < 6; y++)
            for (int x = 8; x < 10; x++)                                // the last two columns are 200
                for (int c = 0; c < 4; c++) rgba[(y * 10 + x) * 4 + c] = 200;

        var reduced = TextureReduce.ToMaxSize(new TextureImage(10, 6, rgba), 4);   // factor 4 -> ceil(10/4) x ceil(6/4) = 3 x 2

        Assert.Equal((3, 2), (reduced.Width, reduced.Height));
        Assert.Equal(100, reduced.Rgba[0]);
        Assert.Equal(200, reduced.Rgba[2 * 4]);                          // the edge block averages only the two columns it covers
    }

    /// <summary>A .tex of the engine's BGRA8 layout with a full mip chain, smallest mip first, whose level L is flat colour L.</summary>
    private static byte[] BgraTexWithMips(int size)
    {
        int mips = (int)Math.Log2(size) + 1;
        var bytes = new List<byte> { (byte)'T', (byte)'E', (byte)'X', 0 };
        bytes.AddRange(BitConverter.GetBytes((ushort)size)); bytes.AddRange(BitConverter.GetBytes((ushort)size));
        bytes.AddRange(new byte[] { 1, 20, 0, 1 });                      // depth 1, BGRA8, 2D texture, has mips
        for (int level = mips - 1; level >= 0; level--)
        {
            int s = Math.Max(1, size >> level);
            for (int i = 0; i < s * s; i++) bytes.AddRange(new byte[] { (byte)(10 * level), (byte)(20 * level), (byte)(30 * level), 255 });   // B, G, R, A
        }
        return bytes.ToArray();
    }

    [Fact]
    public void A_mipped_tex_is_decoded_at_the_largest_stored_mip_that_fits_not_at_full_size()
    {
        var tex = BgraTexWithMips(64);                                   // mips 64,32,16,8,4,2,1 = levels 0..6

        var small = TextureDecoder.DecodeToMaxSize(tex, 16);

        Assert.Equal((16, 16), (small.Width, small.Height));
        // level 2 is the 16x16 one: B=20, G=40, R=60 stored, so RGBA is (60, 40, 20, 255)
        Assert.Equal(new byte[] { 60, 40, 20, 255 }, small.Rgba[..4]);
        Assert.Equal((64, 64), (TextureDecoder.DecodeToMaxSize(tex, 64).Width, 64));   // already fits: the top mip

        // a truncated chain cannot be trusted: the full decoder is asked (and may reject it) rather than a wrong offset read
        var truncated = tex[..(tex.Length - 100)];
        var result = Record.Exception(() => TextureDecoder.DecodeToMaxSize(truncated, 16));
        output.WriteLine("truncated chain: " + (result?.GetType().Name ?? "decoded by the full path"));
    }

    // ================================================================================================ pure bits of the renderer

    [Fact]
    public void The_shipping_bin_of_a_mapgeo_is_found_from_its_folder()
    {
        Assert.Equal("data/maps/shipping/map22/map22.bin", MapThumbnailRenderer.ShippingBinPathFor("data/maps/mapgeometry/map22/lux.mapgeo"));
        Assert.Equal("data/maps/shipping/map11/map11.bin", MapThumbnailRenderer.ShippingBinPathFor("DATA/Maps/MapGeometry/Map11/base_srx.mapgeo"));
        Assert.Null(MapThumbnailRenderer.ShippingBinPathFor("data/maps/mapgeometry/sr/banner_test.mapgeo"));
        Assert.Null(MapThumbnailRenderer.ShippingBinPathFor("assets/x/y.mapgeo"));
    }

    [Fact]
    public void The_frame_share_counts_what_differs_from_the_background_and_the_png_is_the_stored_size()
    {
        int w = MapThumbnailKey.RenderWidth, h = MapThumbnailKey.RenderHeight;
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) { bgra[i * 4] = 19; bgra[i * 4 + 1] = 13; bgra[i * 4 + 2] = 10; bgra[i * 4 + 3] = 0; }   // the clear colour, alpha unset
        Assert.Equal(0.0, MapThumbnailRenderer.NonClearShare(bgra));

        for (int i = 0; i < w * h / 4; i++) { bgra[i * 4] = 200; bgra[i * 4 + 1] = 120; bgra[i * 4 + 2] = 60; }                 // a quarter map
        Assert.Equal(0.25, MapThumbnailRenderer.NonClearShare(bgra), 3);

        var png = MapThumbnailRenderer.EncodePng(bgra);
        using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(png);
        Assert.Equal((MapThumbnailKey.ThumbWidth, MapThumbnailKey.ThumbHeight), (image.Width, image.Height));
        Assert.Equal(255, image[5, 5].A);                                // opaque whatever the readback's alpha said
        Assert.Throws<ArgumentException>(() => MapThumbnailRenderer.EncodePng(new byte[16]));
    }

    [Fact]
    public void The_d3d11_library_is_pinned_by_the_shared_renderer_so_releasing_the_last_device_cannot_unload_it()
    {
        // M807: ShaderPreviewRenderer.Dispose ends by freeing the Silk library handle. With the thumbnail device the last one in
        // the process, a thread whose entry point is inside d3d11.dll had not started yet and then started in the unloaded
        // image - a native access violation no catch sees (every Map12 batch, and two of two under cdb with the debug heap off).
        // M809: the pin M807 kept here, for this worker alone, now lives in ShaderPreviewRenderer.Initialize and covers every host
        // (D3D11LibraryPinTests pins it). The worker creates its device through Initialize and carries no pin of its own.
        if (!OperatingSystem.IsWindows()) { output.WriteLine("SKIPPED: d3d11.dll is Windows-only"); return; }
        ReyEngine.Rendering.D3D11.ShaderPreviewRenderer.PinD3D11Library();
        ReyEngine.Rendering.D3D11.ShaderPreviewRenderer.PinD3D11Library();   // the second call loads nothing more and throws nothing

        using var self = System.Diagnostics.Process.GetCurrentProcess();
        Assert.Contains(self.Modules.Cast<System.Diagnostics.ProcessModule>(),
            m => string.Equals(m.ModuleName, "d3d11.dll", StringComparison.OrdinalIgnoreCase));

        string? src = Source("src", "ReyEngine.App", "Services", "MapThumbnailRenderer.cs");
        if (src is null) { output.WriteLine("SKIPPED (source half): MapThumbnailRenderer.cs not found"); return; }
        Assert.DoesNotContain("NativeLibrary", src);                       // no second copy of the pin
        Assert.DoesNotContain("PinD3D11Library", src);
        Assert.Contains("created.Initialize(out string? initError)", src);   // and its device comes from the call that pins
    }

    [Fact]
    public void A_heavy_job_is_collected_after_it_has_returned_not_inside_its_own_frame()
    {
        // M807: inside RenderJob every local of the job (the decoded map, the textures, the scene) is still reachable from its
        // frame, so a collection there frees none of it: 26 Summoner's Rift maps in a row peaked at a 4.5 GB working set with the
        // collection in the job's finally and at 3.3-3.7 GB with it around the call.
        string? src = Source("src", "ReyEngine.App", "Services", "MapThumbnailRenderer.cs");
        if (src is null) { output.WriteLine("SKIPPED: MapThumbnailRenderer.cs not found"); return; }
        int wrapper = src.IndexOf("try { return RenderJob(job)", StringComparison.Ordinal);
        int collect = src.IndexOf("finally { CollectAfterJob(", StringComparison.Ordinal);
        int job = src.IndexOf("private MapThumbnailOutcome RenderJob(", StringComparison.Ordinal);
        Assert.True(wrapper > 0 && collect > wrapper && job > collect, "Render must collect around RenderJob");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(src, @"CollectAfterJob\(job\.BytesRead\)"));   // and nowhere inside it
        // sized by what the JOB read - not by process-wide allocation (the UI, the viewport and a map load would count) and not
        // by the size of a heap the editor holds a map open in
        Assert.DoesNotContain("GetTotalAllocatedBytes", src);
        Assert.DoesNotContain("GetTotalMemory", src);
    }

    [Fact]
    public void Nothing_the_renderer_does_to_the_heap_stops_the_world()
    {
        // M807: a blocking, compacting collection from the below-normal thread stops the whole editor - an open Summoner's Rift's
        // arrays included - and LargeObjectHeapCompactionMode.CompactOnce is process-wide: it would make the next full collection
        // of ANYTHING compact the large-object heap. Both the after-job collection and the idle release are background requests.
        string? src = Source("src", "ReyEngine.App", "Services", "MapThumbnailRenderer.cs");
        if (src is null) { output.WriteLine("SKIPPED: MapThumbnailRenderer.cs not found"); return; }
        Assert.DoesNotContain("blocking: true", src);
        Assert.DoesNotContain("Aggressive", src);
        Assert.DoesNotContain("CompactOnce", src);
        Assert.DoesNotContain("LargeObjectHeapCompactionMode", src);
        Assert.DoesNotContain("compacting: true", src);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(src, @"GC\.Collect\(").Count);   // after a job, and on release
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(src, @"blocking: false").Count);
    }

    // ================================================================================================ the service

    private static AssetNodeViewModel MapNode(string name) => new(new AssetTreeNode
    {
        Name = name + ".mapgeo",
        Entry = new WadAssetEntry
        {
            Path = $"data/maps/mapgeometry/map22/{name}.mapgeo", PathHash = (ulong)Math.Abs(name.GetHashCode()) + 1,
            IsResolved = true, Type = AssetType.MapGeometry,
        },
    });

    private static AssetNodeViewModel PlainNode(string name) => new(new AssetTreeNode
    {
        Name = name,
        Entry = new WadAssetEntry { Path = $"data/{name}", PathHash = 7, IsResolved = true, Type = AssetType.Bin },
    });

    [Fact]
    public void Only_resolved_mapgeo_tiles_want_a_map_thumbnail_and_a_texture_tile_does_not()
    {
        Assert.True(MapNode("lux").WantsMapThumbnail);
        Assert.False(MapNode("lux").WantsThumbnail);                    // the texture loader leaves it alone
        Assert.False(PlainNode("x.bin").WantsMapThumbnail);
        Assert.False(new AssetNodeViewModel(new AssetTreeNode { Name = "dir", IsFolder = true }).WantsMapThumbnail);
        var unresolved = new AssetNodeViewModel(new AssetTreeNode
        {
            Name = "0x1.unknown", Entry = new WadAssetEntry { Path = "0x1.unknown", IsResolved = false, Type = AssetType.MapGeometry },
        });
        Assert.False(unresolved.WantsMapThumbnail);
    }

    private sealed class NoHashes : IHashResolver
    {
        public bool TryGetPath(ulong hash, out string path) { path = ""; return false; }
        public string ResolvePath(ulong hash) => "";
    }

    private sealed class FakeHost : IMapThumbnailHost
    {
        public volatile bool Generate = true, Pause, Depth;
        public long Version;
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<MapThumbnailJob, System.Runtime.CompilerServices.StrongBox<int>> _faults = new();
        /// <summary>A read "threw" during this job's draw, as the job reports it.</summary>
        public void Fault(MapThumbnailJob job)
        {
            if (_faults.TryGetValue(job, out var box)) Interlocked.Increment(ref box.Value);
        }
        /// <summary>Tiles the host cannot describe (no materials bin): by name.</summary>
        public readonly ConcurrentDictionary<string, bool> NotDescribable = new();
        public ConcurrentQueue<string> Logs { get; } = new();
        public bool CanGenerate => Generate;
        public bool ShouldPause => Pause;
        public long InputsVersion => Interlocked.Read(ref Version);
        public int Described;
        public MapThumbnailTarget? Describe(AssetNodeViewModel node)
        {
            Interlocked.Increment(ref Described);
            if (node.Entry is not { Type: AssetType.MapGeometry } e) return null;
            if (NotDescribable.ContainsKey(node.Name)) return null;
            string key = "key_" + node.Name + (Depth ? "_depth" : "");
            var box = new System.Runtime.CompilerServices.StrongBox<int>();
            var job = new MapThumbnailJob
            {
                Key = key, MapGeoPath = e.Path, MapGeoHash = e.PathHash, MaterialsBinPath = "m.bin", MaterialsBinHash = 1,
                FinalDirectory = "final", Read = _ => null, Has = _ => false, Hashes = new NoHashes(), BinName = _ => null, WadPath = _ => null,
                ClientDepthRules = Depth, ReadFaults = () => Volatile.Read(ref box.Value),
            };
            _faults.Add(job, box);
            return new MapThumbnailTarget(key, node.Name, job);
        }
        public void Log(string message) => Logs.Enqueue(message);
    }

    private sealed class FakeRenderer : IMapThumbnailRenderer
    {
        public ConcurrentQueue<string> Rendered { get; } = new();
        public ConcurrentQueue<Thread> Threads { get; } = new();
        public ManualResetEventSlim? Gate;
        public Func<MapThumbnailJob, MapThumbnailOutcome>? Behaviour;
        public int Released, Disposed;
        public MapThumbnailOutcome Render(MapThumbnailJob job)
        {
            Rendered.Enqueue(job.Key);
            Threads.Enqueue(Thread.CurrentThread);
            Gate?.Wait(TimeSpan.FromSeconds(20));
            if (job.IsCancelled?.Invoke() == true) return new MapThumbnailOutcome { Cancelled = true, Failure = "cancelled" };
            return Behaviour?.Invoke(job) ?? new MapThumbnailOutcome { Png = new byte[] { 0x89, 1, 2, 3 }, TotalMs = 5, Timings = "t", Summary = "s" };
        }
        public void ReleaseDevice() => Interlocked.Increment(ref Released);
        public void Dispose() => Interlocked.Increment(ref Disposed);
    }

    /// <summary>A service over fakes, whose "UI thread" is whoever calls <see cref="Pump"/>.</summary>
    private sealed class Rig : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly FakeHost Host = new();
        public readonly FakeRenderer Renderer = new();
        public readonly MapThumbnailService Service;
        public readonly ConcurrentQueue<Action> Ui = new();
        public readonly List<(AssetNodeViewModel Node, byte[] Png)> Applied = new();
        /// <summary>The fake apply also gives the tile a picture (an Avalonia bitmap needs a platform; this one never draws).</summary>
        public bool ShowPictures;
        /// <summary>Bytes the fake apply refuses to load, as a corrupt PNG would be.</summary>
        public Func<byte[], bool>? Corrupt;

        public Rig(TimeSpan? idle = null, int memory = 256)
        {
            Service = new MapThumbnailService(Host, new MapThumbnailCache(Dir.Path), () => Renderer,
                (n, png) =>
                {
                    Applied.Add((n, png));
                    if (Corrupt?.Invoke(png) == true) return false;
                    if (ShowPictures) n.Thumbnail = PictureWithoutAPlatform();
                    return true;
                }, a => Ui.Enqueue(a), memory)
            { IdleRelease = idle ?? TimeSpan.FromSeconds(30), PauseRecheck = TimeSpan.FromMilliseconds(20) };
        }

        public static Avalonia.Media.Imaging.Bitmap PictureWithoutAPlatform() =>
            (Avalonia.Media.Imaging.Bitmap)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Avalonia.Media.Imaging.Bitmap));

        public void Pump() { while (Ui.TryDequeue(out var a)) a(); }

        public bool WaitFor(Func<bool> condition, int ms = 10_000)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until)
            {
                Pump();
                if (condition()) return true;
                Thread.Sleep(5);
            }
            Pump();
            return condition();
        }

        public void Dispose() { Service.Dispose(); Dir.Dispose(); }
    }

    [Fact]
    public void Only_the_tiles_in_view_are_drawn_each_by_the_one_worker_thread_at_below_normal_priority()
    {
        using var rig = new Rig();
        var nodes = Enumerable.Range(0, 12).Select(i => MapNode("m" + i)).ToList();

        rig.Service.SetVisible(nodes.Take(3).ToList());

        Assert.True(rig.WaitFor(() => rig.Applied.Count == 3), "the three visible tiles got their pictures");
        Assert.Equal(new[] { "key_m0.mapgeo", "key_m1.mapgeo", "key_m2.mapgeo" }, rig.Renderer.Rendered.OrderBy(k => k));
        Assert.Equal(3, rig.Service.Rendered);
        Assert.Equal(0, rig.Service.Outstanding);

        var threads = rig.Renderer.Threads.Distinct().ToList();
        var worker = Assert.Single(threads);                            // one thread for every draw
        Assert.NotEqual(System.Environment.CurrentManagedThreadId, worker.ManagedThreadId);
        Assert.Equal(ThreadPriority.BelowNormal, worker.Priority);
        Assert.True(worker.IsBackground);                                // never keeps the process alive
        Assert.Equal("MapThumbnails", worker.Name);
        Assert.Contains(rig.Host.Logs, l => l.Contains("Map thumbnail m0.mapgeo") && l.Contains("(t)") && l.Contains("s."));
    }

    [Fact]
    public void A_tile_that_is_not_a_map_is_never_asked_about_and_the_picture_is_cached_on_disk()
    {
        using var rig = new Rig();
        var map = MapNode("lux");
        rig.Service.SetVisible(new[] { PlainNode("a.bin"), map });

        Assert.True(rig.WaitFor(() => rig.Applied.Count == 1));
        Assert.Equal(1, rig.Host.Described);                            // the .bin tile was filtered before the host was asked
        Assert.True(rig.Service.Cache.TryReadPng("key_lux.mapgeo", out var png));
        Assert.Equal(new byte[] { 0x89, 1, 2, 3 }, png);
    }

    [Fact]
    public void A_queued_tile_that_scrolls_away_is_dropped_but_the_one_being_drawn_finishes()
    {
        using var rig = new Rig();
        rig.Renderer.Gate = new ManualResetEventSlim(false);
        var nodes = Enumerable.Range(0, 10).Select(i => MapNode("m" + i)).ToList();

        rig.Service.SetVisible(nodes.Take(5).ToList());
        Assert.True(rig.WaitFor(() => rig.Renderer.Rendered.Count == 1), "one of them is in the worker's hands");
        string inHand = rig.Renderer.Rendered.Single();                 // whichever of the five the disk looks delivered first
        Assert.True(rig.WaitFor(() => rig.Service.Outstanding == 5));
        rig.Service.SetVisible(nodes.Skip(7).Take(3).ToList());         // scrolled: 7, 8, 9 now

        Assert.True(rig.WaitFor(() => rig.Service.Outstanding == 1 + 3), "the 4 queued ones left the screen and went");
        rig.Renderer.Gate.Set();
        Assert.True(rig.WaitFor(() => rig.Applied.Count == 4));

        var drawn = rig.Renderer.Rendered.ToList();
        Assert.Equal(inHand, drawn[0]);                                 // the one being drawn finished, and is cached for next time
        Assert.True(rig.Service.Cache.TryReadPng(inHand, out _));
        Assert.Equal(new[] { "key_m7.mapgeo", "key_m8.mapgeo", "key_m9.mapgeo" }, drawn.Skip(1));   // then the new window, top first
        Assert.Equal(4, drawn.Count);                                   // none of the other four was ever drawn
    }

    [Fact]
    public void A_cached_picture_is_shown_without_a_draw_and_remembered_in_memory()
    {
        using var rig = new Rig();
        var node = MapNode("lux");
        rig.Service.Cache.WritePng("key_lux.mapgeo", new byte[] { 7, 7, 7 });

        rig.Service.SetVisible(new[] { node });
        Assert.True(rig.WaitFor(() => rig.Applied.Count == 1));
        Assert.Equal(new byte[] { 7, 7, 7 }, rig.Applied[0].Png);
        Assert.Equal(1, rig.Service.FromDisk);

        File.Delete(rig.Service.Cache.PngPath("key_lux.mapgeo"));       // and now it needs no file at all
        rig.Applied.Clear();
        rig.Service.SetVisible(new[] { node });
        rig.Pump();
        Assert.Single(rig.Applied);
        Assert.Equal(1, rig.Service.FromMemory);
        Assert.Empty(rig.Renderer.Rendered);                            // nothing was ever drawn
    }

    [Fact]
    public void Memory_keeps_only_the_most_recent_pictures()
    {
        using var rig = new Rig(memory: 2);
        var nodes = new[] { MapNode("a"), MapNode("b"), MapNode("c") };
        foreach (var n in nodes) rig.Service.Cache.WritePng("key_" + n.Name, new byte[] { 1 });

        foreach (var n in nodes)
        {
            rig.Service.SetVisible(new[] { n });
            Assert.True(rig.WaitFor(() => rig.Applied.Any(x => ReferenceEquals(x.Node, n))));
        }
        foreach (var n in nodes) File.Delete(rig.Service.Cache.PngPath("key_" + n.Name));

        rig.Applied.Clear();
        rig.Service.SetVisible(new[] { nodes[2] }); rig.Pump();         // c: still in memory
        rig.Service.SetVisible(new[] { nodes[1] }); rig.Pump();         // b: still in memory
        Assert.Equal(2, rig.Applied.Count);
        rig.Service.SetVisible(new[] { nodes[0] });                     // a was evicted and its file is gone: it is drawn again
        Assert.True(rig.WaitFor(() => rig.Renderer.Rendered.Contains("key_a.mapgeo")));
    }

    [Fact]
    public void A_remembered_failure_is_not_drawn_again_and_the_tile_keeps_its_glyph()
    {
        using var rig = new Rig();
        rig.Service.Cache.WriteFailure("key_lux.mapgeo", "no material of the map resolved to a shader");

        rig.Service.SetVisible(new[] { MapNode("lux") });
        Assert.True(rig.WaitFor(() => rig.Host.Described >= 1));
        Thread.Sleep(100);
        rig.Pump();

        Assert.Empty(rig.Renderer.Rendered);
        Assert.Empty(rig.Applied);
    }

    [Fact]
    public void A_failure_that_is_a_fact_about_the_map_is_written_down_and_one_that_may_not_recur_is_not()
    {
        using var rig = new Rig();
        rig.Renderer.Behaviour = job => job.Key.Contains("empty")
            ? new MapThumbnailOutcome { Failure = "the mapgeo holds no geometry", Permanent = true }
            : new MapThumbnailOutcome { Failure = "IOException: a mount closed under the job" };

        rig.Service.SetVisible(new[] { MapNode("empty"), MapNode("flaky") });
        Assert.True(rig.WaitFor(() => rig.Service.Failed == 2));

        Assert.Equal("the mapgeo holds no geometry", rig.Service.Cache.TryReadFailure("key_empty.mapgeo"));
        Assert.Null(rig.Service.Cache.TryReadFailure("key_flaky.mapgeo"));
        Assert.Contains(rig.Host.Logs, l => l.Contains("empty.mapgeo") && l.Contains("no geometry") && l.Contains("Refresh Thumbnail"));
        Assert.Contains(rig.Host.Logs, l => l.Contains("flaky.mapgeo") && !l.Contains("Refresh Thumbnail"));

        // within the session neither is retried on the next look
        int before = rig.Renderer.Rendered.Count;
        rig.Service.SetVisible(new[] { MapNode("empty"), MapNode("flaky") });
        rig.Pump(); Thread.Sleep(50); rig.Pump();
        Assert.Equal(before, rig.Renderer.Rendered.Count);
    }

    [Fact]
    public void Abandoning_drops_the_queue_stops_the_tile_in_hand_and_throws_its_picture_away()
    {
        using var rig = new Rig();
        rig.Renderer.Gate = new ManualResetEventSlim(false);
        var nodes = Enumerable.Range(0, 4).Select(i => MapNode("m" + i)).ToList();
        rig.Service.SetVisible(nodes);
        Assert.True(rig.WaitFor(() => rig.Renderer.Rendered.Count == 1));
        string inHand = rig.Renderer.Rendered.Single();

        rig.Service.Abandon();                                           // another WAD or project is being opened
        rig.Renderer.Gate.Set();

        Assert.True(rig.WaitFor(() => rig.Service.Outstanding == 0));
        Thread.Sleep(100);
        rig.Pump();
        Assert.Empty(rig.Applied);                                       // nothing from the old content reaches a tile
        Assert.False(rig.Service.Cache.TryReadPng(inHand, out _));       // and nothing is cached for it
        Assert.Single(rig.Renderer.Rendered);

        // the same tiles asked for again are drawn: the new content lists them again
        rig.Renderer.Gate = null;
        rig.Service.SetVisible(nodes.Take(2).ToList());
        Assert.True(rig.WaitFor(() => rig.Applied.Count == 2));
    }

    [Fact]
    public void The_worker_waits_while_the_editor_loads_a_map_or_builds_its_scene()
    {
        using var rig = new Rig();
        rig.Host.Pause = true;

        rig.Service.SetVisible(new[] { MapNode("lux") });
        Assert.True(rig.WaitFor(() => rig.Service.Outstanding == 1));
        Thread.Sleep(150);                                               // several recheck periods
        rig.Pump();
        Assert.Empty(rig.Renderer.Rendered);

        rig.Host.Pause = false;
        Assert.True(rig.WaitFor(() => rig.Applied.Count == 1));
    }

    [Fact]
    public void A_device_that_will_not_start_switches_drawing_off_for_the_session_and_says_so_once()
    {
        using var rig = new Rig();
        rig.Renderer.Behaviour = _ => new MapThumbnailOutcome { Failure = "D3D11 is not available: no adapter", DeviceFailed = true };
        var nodes = Enumerable.Range(0, 4).Select(i => MapNode("m" + i)).ToList();

        rig.Service.SetVisible(nodes);
        Assert.True(rig.WaitFor(() => rig.Service.Disabled));
        rig.Pump();

        Assert.Single(rig.Host.Logs, l => l.Contains("off for this session") && l.Contains("no adapter"));
        int rendered = rig.Renderer.Rendered.Count;
        rig.Service.SetVisible(new[] { MapNode("later") });
        rig.Pump(); Thread.Sleep(50); rig.Pump();
        Assert.Equal(rendered, rig.Renderer.Rendered.Count);             // nothing more is drawn
        Assert.Empty(rig.Applied);
    }

    [Fact]
    public void With_the_opengl_viewport_nothing_is_drawn_but_cached_pictures_still_show()
    {
        using var rig = new Rig();
        rig.Host.Generate = false;
        rig.Service.Cache.WritePng("key_cached.mapgeo", new byte[] { 5 });

        rig.Service.SetVisible(new[] { MapNode("cached"), MapNode("uncached") });
        Assert.True(rig.WaitFor(() => rig.Applied.Count == 1));
        Thread.Sleep(100); rig.Pump();

        Assert.Equal("cached.mapgeo", rig.Applied[0].Node.Name);
        Assert.Empty(rig.Renderer.Rendered);                             // no device was ever created
        Assert.Equal(0, rig.Service.Outstanding);

        rig.Host.Generate = true;                                        // the user switches renderer: the next look draws it
        rig.Service.SetVisible(new[] { MapNode("uncached") });
        Assert.True(rig.WaitFor(() => rig.Renderer.Rendered.Contains("key_uncached.mapgeo")));
    }

    [Fact]
    public void Refresh_draws_the_tile_again_and_a_picture_replaces_a_remembered_failure()
    {
        using var rig = new Rig();
        var node = MapNode("lux");
        rig.Service.SetVisible(new[] { node });
        Assert.True(rig.WaitFor(() => rig.Applied.Count == 1));

        Assert.True(rig.Service.Refresh(node));                          // works whether or not the tile is in view
        Assert.True(rig.WaitFor(() => rig.Applied.Count == 2));
        Assert.Equal(2, rig.Renderer.Rendered.Count);

        rig.Service.Cache.WriteFailure("key_lux.mapgeo", "stale");
        rig.Renderer.Behaviour = null;
        Assert.True(rig.Service.Refresh(node));
        Assert.True(rig.WaitFor(() => rig.Applied.Count == 3));
        Assert.Null(rig.Service.Cache.TryReadFailure("key_lux.mapgeo"));
    }

    [Fact]
    public void The_device_is_given_back_after_the_idle_time_and_the_thread_ends_with_the_service()
    {
        var rig = new Rig(idle: TimeSpan.FromMilliseconds(120));
        try
        {
            rig.Service.SetVisible(new[] { MapNode("lux") });
            Assert.True(rig.WaitFor(() => rig.Applied.Count == 1));
            Assert.True(rig.WaitFor(() => rig.Renderer.Released >= 1, 5000), "the worker released the device when idle");
            Assert.Equal(0, rig.Renderer.Disposed);                      // released, not gone: the next tile reuses the renderer

            rig.Service.SetVisible(new[] { MapNode("next") });
            Assert.True(rig.WaitFor(() => rig.Applied.Count == 2));
            var worker = rig.Renderer.Threads.First();
            rig.Service.Dispose();
            Assert.True(rig.WaitFor(() => !worker.IsAlive, 5000), "the worker thread ended");
            Assert.Equal(1, rig.Renderer.Disposed);                      // and it disposed its renderer on its own thread
        }
        finally { rig.Dispose(); }
    }

    [Fact]
    public void The_editor_draws_thumbnails_only_for_the_d3d11_viewport_with_a_game_folder_and_waits_while_it_is_busy()
    {
        using var game = new TempDir();                                   // a folder that looks like a game install: DATA/FINAL/DATA.wad.client
        Directory.CreateDirectory(Path.Combine(game.Path, "DATA", "FINAL"));
        File.WriteAllBytes(Path.Combine(game.Path, "DATA", "FINAL", "DATA.wad.client"), new byte[] { 1 });

        var vm = new MainWindowViewModel();
        var hostType = typeof(MainWindowViewModel).GetNestedType("MapThumbnailHost", BindingFlags.NonPublic)!;
        var host = (IMapThumbnailHost)Activator.CreateInstance(hostType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new object[] { vm }, null)!;
        var busy = typeof(MainWindowViewModel).GetField("_mapThumbnailBusy", BindingFlags.NonPublic | BindingFlags.Instance)!;

        bool openGl = vm.Settings.UseOpenGlViewport;                      // in memory only: the setting is READ, never saved here
        try
        {
            vm.Settings.UseOpenGlViewport = false;
            vm.Project.GameDirectory = null;
            Assert.False(host.CanGenerate, "no game folder, no shader cache to draw with");
            vm.Project.GameDirectory = game.Path;
            Assert.True(host.CanGenerate);
            vm.Settings.UseOpenGlViewport = true;
            Assert.False(host.CanGenerate, "the OpenGL viewport builds no D3D11 device for a thumbnail");
        }
        finally { vm.Settings.UseOpenGlViewport = openGl; }

        Assert.False(host.ShouldPause);                                   // a map load or a D3D11 scene build holds the counter
        busy.SetValue(vm, 1);
        Assert.True(host.ShouldPause);
        busy.SetValue(vm, 0);
        Assert.False(host.ShouldPause);

        // nothing is open, so there is no tile to describe: not a map, and a map with nothing mounted
        Assert.Null(host.Describe(PlainNode("x.bin")));
        Assert.Null(host.Describe(MapNode("lux")));
    }

    [Fact]
    public void Every_view_model_rebuild_cancels_what_the_thumbnails_queued()
    {
        string? main = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        if (main is null) { output.WriteLine("SKIPPED: MainWindowViewModel.cs not found"); return; }

        // the places the mounts / archive / tree are replaced: each tells the service BEFORE the old reader goes, and the old
        // reader is retired - disposed once the tile in hand has stopped reading through it - never disposed outright. Other
        // content (another WAD, another project) also stops the tile in hand.
        foreach (string pattern in new[]
        {
            @"AbandonMapThumbnails\(\);[^\n]*\r?\n\s*RetireMapThumbnailReader\(_archive\);\r?\n\s*_archive = WadArchive\.Open\(path",   // LoadWad
            @"RetireMapThumbnailReader\(_mounts\); _mounts = null;[^\n]*\r?\n\s*ProjectMode = false",                                       // LoadWad, the mounts of the project it replaces
            @"InvalidateMapThumbnails\(\);[^\n]*\r?\n\s*RetireMapThumbnailReader\(_mounts\);[^\n]*\r?\n\s*_mounts = new AssetMountService",   // BuildMounts
            @"AbandonMapThumbnails\(\);[^\n]*\r?\n\s*RetireMapThumbnailReader\(_archive\); _archive = null;",                              // OpenProjectAt
            @"_thumbnails\.Clear\(\);\r?\n\s*InvalidateMapThumbnails\(\);",                                                    // BuildProjectTree
            @"_nodesByHash\.Clear\(\);\r?\n\s*InvalidateMapThumbnails\(\);",                                                   // RebuildTree
        })
            Assert.True(System.Text.RegularExpressions.Regex.IsMatch(main, pattern), "missing: " + pattern);
        // and the busy counter brackets the two things a thumbnail must not compete with
        Assert.Contains("Interlocked.Increment(ref _mapThumbnailBusy);   // M807: a background map thumbnail waits while a map loads", main);
        Assert.Contains("Interlocked.Decrement(ref _mapThumbnailBusy)", main);
        Assert.Contains("BuildDx11SceneCoreAsync", main);
    }
}
