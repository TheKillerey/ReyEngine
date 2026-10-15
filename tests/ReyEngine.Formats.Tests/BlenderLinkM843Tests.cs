using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Interop;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M843: the navgrid variants and the visibility model through the REAL view model and a REAL socket, on a Map11 project
/// copied from the game: the pickers, Auto overlays following Map Visibility, and <c>visibility_eval</c> against what
/// <c>ApplyMapVisibility</c> draws for the same selection. Skipped without the game install.
/// </summary>
public sealed class BlenderLinkM843Tests : IDisposable
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private const string MapgeoPath = "data/maps/mapgeometry/map11/base_srx.mapgeo";
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rey-m843-link-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static object? Call(object o, string name, params object?[] args)
    {
        var m = o.GetType().GetMethods(Any).First(x => x.Name == name && x.GetParameters().Length == args.Length);
        try { return m.Invoke(o, args); }
        catch (TargetInvocationException e) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
    }

    private static (JsonElement Header, byte[] Body) Send(NetworkStream stream, object request)
    {
        BlenderBridgeProtocol.WriteMessage(stream, JsonSerializer.Serialize(request));
        var header = JsonDocument.Parse(BlenderBridgeProtocol.ReadHeader(stream)!).RootElement.Clone();
        int bytes = header.TryGetProperty("bytes", out var b) ? b.GetInt32() : 0;
        return (header, bytes > 0 ? BlenderBridgeProtocol.ReadExactly(stream, bytes) : Array.Empty<byte>());
    }

    private static ushort[] Flags(JsonElement header, byte[] body)
    {
        int n = header.GetProperty("count_x").GetInt32() * header.GetProperty("count_z").GetInt32();
        Assert.Equal(n * 6, body.Length);
        var flags = new ushort[n];
        for (int i = 0; i < n; i++) flags[i] = BitConverter.ToUInt16(body, n * 4 + i * 2);
        return flags;
    }

    [Fact]
    public async Task Map11ServesItsVariantsAndItsVisibilityAndBlenderCannotDisagreeWithTheEditor()
    {
        string wadPath = Path.Combine(Final, "Maps", "Shipping", "Map11.wad.client");
        if (!File.Exists(wadPath)) return;

        // ---- a project folder: the geometry, its materials, the shipping bin and every navgrid file of the map
        string folder = Path.Combine(_root, "Map11");
        var db = new HashSyncService().LoadLocal(_ => { });
        using (var wad = WadArchive.Open(wadPath, new WadPathResolver(db)))
        {
            var wanted = new List<string> { MapgeoPath, "data/maps/mapgeometry/map11/base_srx.materials.bin", "data/maps/shipping/map11/map11.bin" };
            wanted.AddRange(wad.Entries.Where(e => e.IsResolved && e.Path.StartsWith("assets/maps/navgrid/", StringComparison.OrdinalIgnoreCase)
                && !e.Path.StartsWith("assets/maps/navgrid/map90", StringComparison.OrdinalIgnoreCase)).Select(e => e.Path));
            foreach (string p in wanted)
            {
                string file = Path.Combine(folder, p.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllBytes(file, wad.Extract(HashAlgorithms.WadPath(p)));
            }
        }
        string mapFile = Path.Combine(folder, MapgeoPath.Replace('/', Path.DirectorySeparatorChar));

        var vm = new MainWindowViewModel { Project = ReyProjectService.OpenFolder(_root) };
        Call(vm, "BuildMounts");
        var args = new object?[] { HashAlgorithms.WadPath(MapgeoPath), null };
        Assert.True((bool)Call(vm, "TryResolveEntry", args)!);
        var entry = (WadAssetEntry)args[1]!;
        byte[] raw = File.ReadAllBytes(mapFile);
        var map = MapGeoDecoder.Decode(raw, (IReadOnlySet<string>)Call(vm, "ExtendedChannelMaterialsFor", entry.Path)!);
        foreach (var (field, value) in new (string, object)[] { ("_currentMap", map), ("_currentMapBytes", raw), ("_currentMapEntry", entry) })
            typeof(MainWindowViewModel).GetField(field, Any)!.SetValue(vm, value);
        // what the map open does: the navgrid beside the mapgeo, then the visibility axes (which re-tie the overlays)
        Call(vm, "LoadNavGridForCurrentMap", entry.Path);
        Call(vm, "BuildMapVisibility", entry.Path, map);
        Call(vm, "ApplyMapVisibility");

        // ---- the pickers
        Assert.Equal(13, vm.NavGridGridLabels.Count);
        Assert.StartsWith("Base: aipath_srx_3", vm.NavGridGridLabels[0]);
        Assert.Equal(0, vm.NavGridGridIndex);
        Assert.Equal(10, vm.NavGridOverlayLabels.Count);      // Auto, None, 8 overlays
        Assert.Contains("Dragon: Infernal", vm.NavGridOverlayLabels);
        Assert.Contains("Baron: Cup", vm.NavGridOverlayLabels);
        Assert.Equal(0, vm.NavGridOverlayIndex);
        Assert.Contains("aipath_srx_3", vm.NavGridVariantSummary);
        Assert.DoesNotContain("overlay", vm.NavGridVariantSummary, StringComparison.OrdinalIgnoreCase);
        Assert.True(vm.HasNavGrid);

        // ---- the real handlers on a real socket
        using var server = new BlenderBridgeServer(a => a(), (_, _) => { })
        {
            Handlers = (BlenderBridgeHandlers)Call(vm, "BlenderHandlers")!,
            HandlerTimeout = TimeSpan.FromMinutes(3),
            Token = BlenderBridgeServer.NewToken(),
        };
        Assert.True(server.Start(0, out string? error), error);
        using var client = new TcpClient();
        client.Connect(System.Net.IPAddress.Loopback, server.Port);
        var stream = client.GetStream();
        Send(stream, new { op = "hello", protocol = 5, client = "M843 test", token = server.Token!.ToLowerInvariant() });

        // navgrid_variants: every grid and overlay, readable, with versions
        var (variants, _) = Send(stream, new { op = "navgrid_variants" });
        Assert.True(variants.GetProperty("found").GetBoolean());
        Assert.Equal("grid:aipath_srx_3", variants.GetProperty("default_grid").GetString());
        var list = variants.GetProperty("variants").EnumerateArray().ToList();
        Assert.Equal(13 + 8, list.Count);
        Assert.All(list.Where(v => v.GetProperty("kind").GetString() == "grid"), v =>
        {
            Assert.True(v.GetProperty("supported").GetBoolean(), v.GetProperty("id").GetString());
            Assert.StartsWith("7.", v.GetProperty("version").GetString());
        });
        var infernal = list.Single(v => v.GetProperty("id").GetString() == "overlay:navgrid_infernal_seasonal");
        Assert.Equal("Dragon: Infernal", infernal.GetProperty("label").GetString());
        Assert.Equal("0x9e019715", infernal.GetProperty("axis").GetString());
        Assert.Equal(2, infernal.GetProperty("bit").GetInt32());
        Assert.Contains("skin Default", list.Single(v => v.GetProperty("id").GetString() == "grid:aipath_srx_2").GetProperty("detail").GetString());

        // navgrid: default = what the editor shows; a variant is whole grid + overlays, applied in the file's own terms
        var (nav, navBody) = Send(stream, new { op = "navgrid" });
        Assert.Equal("grid:aipath_srx_3", nav.GetProperty("variant").GetString());
        Assert.Equal(vm.NavGridLayers.Count, nav.GetProperty("layers").GetArrayLength());
        var baseFlags = Flags(nav, navBody);

        var (ocean, oceanBody) = Send(stream, new { op = "navgrid", variant = "grid:aipath_srx_3+overlay:navgrid_ocean_seasonal" });
        Assert.Equal("grid:aipath_srx_3+overlay:navgrid_ocean_seasonal", ocean.GetProperty("variant").GetString());
        var oceanFlags = Flags(ocean, oceanBody);
        int changed = ocean.GetProperty("overlays")[0].GetProperty("changed").GetInt32();
        Assert.True(changed > 500);
        Assert.Equal(changed, Enumerable.Range(0, baseFlags.Length).Count(i => baseFlags[i] != oceanFlags[i]));

        var (other, otherBody) = Send(stream, new { op = "navgrid", variant = "grid:aipath" });   // another whole grid, no overlay
        Assert.Equal("grid:aipath", other.GetProperty("variant").GetString());
        Assert.NotEqual(baseFlags, Flags(other, otherBody));
        var (bad, _) = Send(stream, new { op = "navgrid", variant = "grid:nonsense" });
        Assert.False(bad.GetProperty("found").GetBoolean());
        Assert.Contains("no navgrid variant", bad.GetProperty("message").GetString());

        // the selection Blender sends decides 'auto'
        var (auto, autoBody) = Send(stream, new { op = "navgrid", variant = "auto", selection = new { axes = new Dictionary<string, int> { ["0x9e019715"] = 8 } } });
        Assert.Equal("grid:aipath_srx_3+overlay:navgrid_ocean_seasonal", auto.GetProperty("variant").GetString());
        Assert.Equal(oceanFlags, Flags(auto, autoBody));

        // visibility: the editor's lists and its current selection
        var (vis, _) = Send(stream, new { op = "visibility" });
        var axes = vis.GetProperty("axes").EnumerateArray().ToList();
        Assert.Equal(new[] { "Map Visibility", "Baron Pit" }, axes.Select(a => a.GetProperty("name").GetString()));
        Assert.Equal(new[] { "All", "Base", "Infernal", "Mountain", "Ocean", "Cloud", "Hextech", "Chemtech", "Void" },
            axes[0].GetProperty("options").EnumerateArray().Select(o => o.GetProperty("label").GetString()));
        Assert.Equal(vm.VisibilityAxes[0].Options, axes[0].GetProperty("options").EnumerateArray().Select(o => o.GetProperty("label").GetString()).ToList());
        Assert.Equal(vm.VisibilityAxes[1].Options, axes[1].GetProperty("options").EnumerateArray().Select(o => o.GetProperty("label").GetString()).ToList());
        Assert.Equal(vm.MapEvents.Select(e => e.Name), vis.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("name").GetString()));
        Assert.Equal(vm.MapEvents.Select(e => e.Label), vis.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("label").GetString()));
        Assert.All(vis.GetProperty("events").EnumerateArray(), e => Assert.False(e.GetProperty("on").GetBoolean()));   // a normal game: none on
        Assert.False(vis.GetProperty("render_regions").GetProperty("enabled").GetBoolean());
        var visibleNow = vis.GetProperty("visible").EnumerateArray().Select(v => v.GetInt32()).ToList();

        // parity: for each selection, ReyEngine's own ApplyMapVisibility (the editor's controls) and visibility_eval agree
        var visibleGroups = () => ((IReadOnlyList<bool>)vm.CurrentModelSubmeshVisible!);
        HashSet<int> EditorMeshes()
        {
            var groups = visibleGroups();
            return Enumerable.Range(0, groups.Count).Where(i => groups[i]).Select(i => map.Groups[i].MeshIndex).ToHashSet();
        }
        Assert.Equal(visibleNow.OrderBy(i => i), EditorMeshes().OrderBy(i => i));

        var selections = new List<(string Name, Action Apply, object Selection)>
        {
            ("Infernal", () => vm.VisibilityAxes[0].SelectedIndex = 2, new { axes = new Dictionary<string, int> { ["0x9e019715"] = 2 } }),
            ("Ocean", () => vm.VisibilityAxes[0].SelectedIndex = 4, new { axes = new Dictionary<string, int> { ["0x9e019715"] = 8 } }),
            ("Cloud + Cup", () => { vm.VisibilityAxes[0].SelectedIndex = 5; vm.VisibilityAxes[1].SelectedIndex = 2; },
                new { axes = new Dictionary<string, int> { ["0x9e019715"] = 16, ["0xd31ac6ce"] = 2 } }),
            ("Chemtech + Tunnel", () => { vm.VisibilityAxes[0].SelectedIndex = 7; vm.VisibilityAxes[1].SelectedIndex = 3; },
                new { axes = new Dictionary<string, int> { ["0x9e019715"] = 64, ["0xd31ac6ce"] = 4 } }),
            ("Upgraded", () => { vm.VisibilityAxes[0].SelectedIndex = 0; vm.VisibilityAxes[1].SelectedIndex = 4; },
                new { axes = new Dictionary<string, int> { ["0xd31ac6ce"] = 8 } }),
            ("Hall of Legends", () => { vm.VisibilityAxes[0].SelectedIndex = 0; vm.VisibilityAxes[1].SelectedIndex = 0; vm.MapEvents.First(e => e.Name == "SR_Hall_Of_Legends").IsOn = true; },
                new { events = new[] { "SR_Hall_Of_Legends" } }),
        };
        var seen = new List<List<int>>();
        foreach (var (name, apply, selection) in selections)
        {
            foreach (var ev in vm.MapEvents) ev.IsOn = false;
            apply();
            var editor = EditorMeshes();
            var (eval, _) = Send(stream, new { op = "visibility_eval", selection });
            var served = eval.GetProperty("visible").EnumerateArray().Select(v => v.GetInt32()).ToList();
            Assert.Equal(editor.OrderBy(i => i), served.OrderBy(i => i));
            Assert.True(served.Count > 100 && served.Count < map.Meshes.Count, name);
            seen.Add(served);
        }
        Assert.True(seen.Select(s => string.Join(",", s)).Distinct().Count() >= 5, "the selections draw different meshes");

        // the editor's current selection is served back
        foreach (var ev in vm.MapEvents) ev.IsOn = false;
        vm.VisibilityAxes[0].SelectedIndex = 2;
        vm.VisibilityAxes[1].SelectedIndex = 0;
        var (current, _) = Send(stream, new { op = "visibility" });
        Assert.Equal(2, current.GetProperty("selection").GetProperty("axes").GetProperty("0x9e019715").GetInt32());
        Assert.Equal(new[] { "overlay:navgrid_infernal_seasonal" }, current.GetProperty("navgrid_overlays").EnumerateArray().Select(o => o.GetString()));

        // Auto overlays follow Map Visibility in the editor too: Infernal brought its overlay along
        Assert.Contains("Dragon: Infernal", vm.NavGridVariantSummary);
        vm.NavGridOverlayIndex = 1;   // None
        Assert.DoesNotContain("Dragon: Infernal", vm.NavGridVariantSummary);
        vm.NavGridOverlayIndex = 0;   // Auto again
        vm.NavGridGridIndex = vm.NavGridGridLabels.ToList().FindIndex(l => l.StartsWith("Grid: aipath ")  || l.StartsWith("Grid: aipath -"));
        Assert.StartsWith("aipath", vm.NavGridVariantSummary);
        Assert.Contains("Dragon: Infernal", vm.NavGridVariantSummary);

        // a selection the map cannot take is refused, never guessed
        var (refused, _) = Send(stream, new { op = "visibility_eval", selection = new { axes = new Dictionary<string, int> { ["0x9e019715"] = 3 } } });
        Assert.False(refused.GetProperty("found").GetBoolean());
        Assert.Contains("no layer with bit 3", refused.GetProperty("message").GetString());

        // a pull carries the editor's visibility
        var (pulled, _) = Send(stream, new { op = "pull" });
        Assert.Equal(current.GetProperty("visible").GetArrayLength(), pulled.GetProperty("visibility").GetProperty("visible").GetArrayLength());
        // the index a pull sends (rey_index in Blender) is the MapGeoMesh.Index the evaluator returns
        var universe = map.Meshes.Select(m => m.Index).ToHashSet();
        var pulledIndices = pulled.GetProperty("meshes").EnumerateArray().Select(m => m.GetProperty("i").GetInt32()).ToList();
        Assert.All(pulledIndices, i => Assert.Contains(i, universe));
        Assert.All(pulled.GetProperty("visibility").GetProperty("visible").EnumerateArray(), v => Assert.Contains(v.GetInt32(), universe));
        // an overlay that cannot be loaded is asked for once per selection, not re-applied on every visibility refresh
        var overlayCache = (System.Collections.IDictionary)typeof(MainWindowViewModel).GetField("_navOverlayCache", Any)!.GetValue(vm)!;
        overlayCache["assets/maps/navgrid/navgrid_ocean_seasonal.ngrid_overlay"] = ((NavGridOverlay?)null, "gone");
        vm.VisibilityAxes[0].SelectedIndex = 4;   // Ocean: its overlay is unavailable
        Assert.DoesNotContain("Ocean", vm.NavGridVariantSummary);
        var layerBefore = vm.NavGridLayers[0];
        Call(vm, "ApplyMapVisibility"); Call(vm, "ApplyMapVisibility");
        Assert.Same(layerBefore, vm.NavGridLayers[0]);   // nothing was rebuilt

        await Task.CompletedTask;
    }
}
