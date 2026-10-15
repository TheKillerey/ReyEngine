using System.Text.Json;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Interop;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M843: navgrid variants (whole grids and state overlays) and the shared map-visibility function, against the installed 16.20
/// game WADs (skipped without them) and small synthetic fixtures for the parsing rules.
/// </summary>
public sealed class NavGridVariantsTests
{
    private const string Shipping = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping";

    private static WadArchive? Open(string map)
    {
        string path = Path.Combine(Shipping, map + ".wad.client");
        if (!File.Exists(path)) return null;
        var db = new HashSyncService().LoadLocal(_ => { });
        return WadArchive.Open(path, new WadPathResolver(db));
    }

    private static byte[] Overlay(params (int X, int Z, int W, int H, ushort[] Flags)[] blocks)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write((byte)1); w.Write((byte)blocks.Length);
        foreach (var b in blocks)
        {
            w.Write(b.X); w.Write(b.Z); w.Write(b.W); w.Write(b.H);
            foreach (ushort f in b.Flags) w.Write(f);
        }
        w.Write((byte)0);
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ overlay format

    [Fact]
    public void AnOverlayReplacesTheFlagWordsInsideItsBlocksAndNothingElse()
    {
        var grid = NavGrid.CreateForTests(new System.Numerics.Vector3(0, 0, 0), 50f, 4, 3,
            new ushort[] { 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4 }, null);
        Assert.True(NavGridOverlay.TryParse(Overlay((1, 1, 2, 2, new ushort[] { 0x42, 0, 0x80, 0x2 })), out var overlay, out string? why), why);
        var shown = overlay!.ApplyTo(grid, out int changed, out int outside);

        Assert.Equal(4, changed);
        Assert.Equal(0, outside);
        Assert.Equal(new ushort[] { 1, 1, 1, 1, 2, 0x42, 0, 2, 4, 0x80, 0x2, 4 }, shown.Flags);
        Assert.Equal(new ushort[] { 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4 }, grid.Flags);   // the base is untouched
    }

    [Fact]
    public void AnOverlayCellOutsideTheLatticeIsCountedNotWritten()
    {
        var grid = NavGrid.CreateForTests(default, 50f, 2, 2, new ushort[4], null);
        Assert.True(NavGridOverlay.TryParse(Overlay((1, 1, 2, 2, new ushort[] { 8, 8, 8, 8 })), out var overlay, out _));
        var shown = overlay!.ApplyTo(grid, out int changed, out int outside);
        Assert.Equal(1, changed);
        Assert.Equal(3, outside);
        Assert.Equal(8, shown.Flags[3]);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 1 })]
    [InlineData(new byte[] { 2, 0 })]                       // unmeasured version
    [InlineData(new byte[] { 1, 1, 0, 0, 0, 0, 0, 0, 0, 0 })]   // block header cut short
    public void AnOverlayThatIsNotOneIsRefusedNotGuessed(byte[] data)
    {
        Assert.False(NavGridOverlay.TryParse(data, out var overlay, out string? why));
        Assert.Null(overlay);
        Assert.False(string.IsNullOrEmpty(why));
    }

    [Fact]
    public void AnOverlayBlockThatClaimsMoreCellsThanTheFileHoldsIsRefusedBeforeAnyAllocation()
    {
        var bytes = new List<byte> { 1, 1 };
        foreach (int v in new[] { 0, 0, 4096, 4096 }) bytes.AddRange(BitConverter.GetBytes(v));
        Assert.False(NavGridOverlay.TryParse(bytes.ToArray(), out _, out string? why));
        Assert.Contains("runs past the end", why);
    }

    [Fact]
    public void AHostileOverlayIsRefusedOrAppliedWithoutThrowing()
    {
        // a coordinate that would overflow the cell index is refused at parse time
        foreach (int x in new[] { int.MaxValue, 1_000_000, -1 })
        {
            var data = Overlay((x, 0, 1, 1, new ushort[] { 1 }));
            Assert.False(NavGridOverlay.TryParse(data, out _, out string? why), $"x={x}");
            Assert.Contains("implausible", why);
        }
        // the largest accepted rectangle far outside a small grid is skipped and counted
        var big = Overlay((65535, 65535, 2, 2, new ushort[] { 1, 1, 1, 1 }));
        Assert.True(NavGridOverlay.TryParse(big, out var overlay, out _));
        var grid = NavGrid.CreateForTests(default, 50f, 2, 2, new ushort[4], null);
        var shown = overlay!.ApplyTo(grid, out int changed, out int outside);
        Assert.Equal(0, changed);
        Assert.Equal(4, outside);
        Assert.Equal(grid.Flags, shown.Flags);
    }

    [Fact]
    public void ABlockWithTrailingGarbageIsRefused()
    {
        var data = Overlay((0, 0, 1, 1, new ushort[] { 1 })).Concat(new byte[] { 9, 9 }).ToArray();
        Assert.False(NavGridOverlay.TryParse(data, out _, out string? why));
        Assert.Contains("unexpected byte", why);
    }

    // ------------------------------------------------------------------ the real files

    [Fact]
    public void EveryMap11OverlayParsesToTheEndOfItsFileAndFitsTheBaseLattice()
    {
        using var wad = Open("Map11");
        if (wad is null) return;
        var overlays = wad.Entries.Where(e => e.IsResolved && e.Path.EndsWith(".ngrid_overlay", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(8, overlays.Count);
        NavGrid.TryParse(wad.Extract(HashAlgorithms.WadPath("assets/maps/navgrid/map11/aipath_srx_3.aimesh_ngrid")), out var grid, out _);
        Assert.NotNull(grid);
        foreach (var e in overlays)
        {
            byte[] raw = wad.Extract(e);
            Assert.True(NavGridOverlay.TryParse(raw, out var overlay, out string? why), $"{e.Path}: {why}");
            Assert.True(overlay!.Blocks.Count > 0, e.Path);
            Assert.InRange(overlay.Trailer, 0, 1);   // 1 in the mountain, ocean and tunnel files: kept, meaning unknown
            var shown = overlay.ApplyTo(grid!, out int changed, out int outside);
            Assert.Equal(0, outside);           // every block lies on the 295x296 lattice
            Assert.True(changed > 0, $"{e.Path} changes nothing");

            // against the file's own data: inside every block the shown flags ARE the block, outside they ARE the base
            var inside = new HashSet<int>();
            foreach (var b in overlay.Blocks)
                for (int j = 0; j < b.Height; j++)
                    for (int i = 0; i < b.Width; i++)
                    {
                        int at = (b.Z + j) * grid!.CountX + b.X + i;
                        inside.Add(at);
                    }
            for (int at = 0; at < grid!.CellCount; at++)
                if (!inside.Contains(at)) Assert.Equal(grid.Flags[at], shown.Flags[at]);
            // the bytes of the file say the same, read independently of the parser
            int p = 2; var expected = (ushort[])grid.Flags.Clone();
            for (int k = 0; k < raw[1]; k++)
            {
                int x = BitConverter.ToInt32(raw, p), z = BitConverter.ToInt32(raw, p + 4), w = BitConverter.ToInt32(raw, p + 8), h = BitConverter.ToInt32(raw, p + 12);
                p += 16;
                for (int j = 0; j < h; j++) for (int i = 0; i < w; i++, p += 2) expected[(z + j) * grid.CountX + x + i] = BitConverter.ToUInt16(raw, p);
            }
            Assert.Equal(expected, shown.Flags);
        }
    }

    [Fact]
    public void TheOceanOverlayAddsBushAndTheChemtechOverlayOpensAWall()
    {
        // The evidence the replace-the-words reading rests on (see NavGridOverlay): a terrain that grows bushes adds bit 0, and
        // the chemtech terraform removes the 0x80 / 0x40 wall words in its 4x2 block.
        using var wad = Open("Map11");
        if (wad is null) return;
        NavGrid.TryParse(wad.Extract(HashAlgorithms.WadPath("assets/maps/navgrid/map11/aipath_srx_3.aimesh_ngrid")), out var grid, out _);
        NavGridOverlay.TryParse(wad.Extract(HashAlgorithms.WadPath("assets/maps/navgrid/navgrid_ocean_seasonal.ngrid_overlay")), out var ocean, out _);
        NavGridOverlay.TryParse(wad.Extract(HashAlgorithms.WadPath("assets/maps/navgrid/navgrid_chemtech.ngrid_overlay")), out var chem, out _);
        Assert.True(ocean!.ApplyTo(grid!, out _, out _).CountWith(NavGrid.BushFlag) > grid!.CountWith(NavGrid.BushFlag) + 300);
        var shown = chem!.ApplyTo(grid, out int changed, out _);
        Assert.Equal(8, changed);
        Assert.True(shown.CountWith(0x0080) < grid.CountWith(0x0080));
    }

    // ------------------------------------------------------------------ the catalog

    private static (NavGridVariantCatalog Catalog, MapVisibilityDefinition Visibility, NavGridDeclarations Declarations)? Catalog11(string stem)
    {
        using var wad = Open("Map11");
        if (wad is null) return null;
        var db = new HashSyncService().LoadLocal(_ => { });
        var resolver = new WadPathResolver(db);
        var def = MapVisibility.Parse(wad.Extract(HashAlgorithms.WadPath("data/maps/shipping/map11/map11.bin")), h => db.TryGetBinName(h, out var n) ? n : null);
        var decl = NavGridDeclarations.Read(new[]
        {
            wad.Extract(HashAlgorithms.WadPath($"data/maps/mapgeometry/map11/{stem}.materials.bin")),
            wad.Extract(HashAlgorithms.WadPath("data/maps/shipping/map11/map11.bin")),
        });
        var catalog = NavGridVariantCatalog.Build("map11", stem, wad.Entries.Where(e => e.IsResolved).Select(e => e.Path), decl, def,
            h => db.TryGetBinName(h, out var n) ? n : null);
        return (catalog, def, decl);
    }

    [Fact]
    public void Map11ListsEveryGridAndOverlayAndFollowsTheGeometrysContainer()
    {
        if (Catalog11("base_srx") is not { } c) return;
        var ids = c.Catalog.Grids.Select(g => g.Id).ToList();
        Assert.Equal(13, ids.Count);   // the hash tables name more (sodapop_srs, socialsr, urf_boba ...) but the 16.20 WAD ships these 13
        foreach (string want in new[] { "grid:aipath", "grid:aipath_srx", "grid:aipath_srx_2", "grid:aipath_srx_3", "grid:aipath_npe_1", "grid:aipath_ultbook",
                     "grid:bloom", "grid:boba_srs_act2a", "grid:boba_srs_act2b", "grid:crepe", "grid:ruby_sr", "grid:tutorial", "grid:contentcapture" })
            Assert.Contains(want, ids);

        // base_srx's container names AIPath_SRX_3, and that is the base that comes first
        Assert.Equal("grid:aipath_srx_3", c.Catalog.DefaultGridId);
        Assert.Equal("grid:aipath_srx_3", c.Catalog.Grids[0].Id);
        Assert.StartsWith("Base: aipath_srx_3", c.Catalog.Grids[0].Label);

        Assert.Equal(8, c.Catalog.Overlays.Count);
        string Label(string stem) => c.Catalog.Overlays.Single(o => o.Id == "overlay:" + stem).Label;
        Assert.Equal("Dragon: Chemtech", Label("navgrid_chemtech"));
        Assert.Equal("Dragon: Infernal", Label("navgrid_infernal_seasonal"));
        Assert.Equal("Dragon: Mountain", Label("navgrid_mountain_seasonal"));
        Assert.Equal("Dragon: Ocean", Label("navgrid_ocean_seasonal"));
        Assert.Equal("Baron: Tunnel", Label("navgrid_tunnel_base"));
        Assert.Equal("Baron: Cup", Label("navgrid_cup_base"));
        Assert.Equal("Overlay: Atakhanbot", Label("navgrid_atakhanbot"));
        Assert.Equal("Overlay: Atakhantop", Label("navgrid_atakhantop"));
    }

    [Theory]
    [InlineData("ruby_sr", "grid:ruby_sr")]
    [InlineData("crepe", "grid:crepe")]
    [InlineData("bloom", "grid:bloom")]
    [InlineData("tutorial", "grid:tutorial")]
    [InlineData("contentcapture", "grid:contentcapture")]
    public void EachGeometrySetPlaysOnTheGridItsOwnContainerNames(string stem, string grid)
    {
        if (Catalog11(stem) is not { } c) return;
        Assert.Equal(grid, c.Catalog.DefaultGridId);
    }

    [Fact]
    public void AnUnknownGeometryFallsBackToTheClassicAipath()
    {
        if (Catalog11("base_srx") is not { } c) return;
        var catalog = NavGridVariantCatalog.Build("map11", "no_such_set", c.Catalog.Grids.Select(g => g.Path), c.Declarations, c.Visibility);
        Assert.Equal("grid:aipath", catalog.DefaultGridId);
    }

    [Fact]
    public void ADragonOrBaronSelectionPicksTheOverlayOfTheSameName()
    {
        if (Catalog11("base_srx") is not { } c) return;
        uint dragon = MapVisibility.PrimaryAxisHash, baron = MapVisibility.BaronPitAxisHash;
        int Bit(uint axis, string layer) => c.Visibility.Axes.Single(a => a.DefinitionFieldHash == axis).Layers.Single(l => l.Name == layer).Bit;

        Assert.Empty(c.Catalog.OverlaysFor(new Dictionary<uint, int> { [dragon] = 0, [baron] = 0 }));
        Assert.Empty(c.Catalog.OverlaysFor(new Dictionary<uint, int> { [dragon] = Bit(dragon, "Base") }));
        Assert.Empty(c.Catalog.OverlaysFor(new Dictionary<uint, int> { [dragon] = Bit(dragon, "Hextech") }));   // no hextech overlay exists
        Assert.Equal(new[] { "overlay:navgrid_infernal_seasonal" }, c.Catalog.OverlaysFor(new Dictionary<uint, int> { [dragon] = Bit(dragon, "Infernal") }).Select(o => o.Id));
        Assert.Equal(new[] { "overlay:navgrid_ocean_seasonal" }, c.Catalog.OverlaysFor(new Dictionary<uint, int> { [dragon] = Bit(dragon, "Ocean") }).Select(o => o.Id));
        Assert.Equal(new[] { "overlay:navgrid_cup_base" }, c.Catalog.OverlaysFor(new Dictionary<uint, int> { [baron] = Bit(baron, "Cup") }).Select(o => o.Id));
        Assert.Equal(new[] { "overlay:navgrid_chemtech", "overlay:navgrid_tunnel_base" }.Order(),
            c.Catalog.OverlaysFor(new Dictionary<uint, int> { [dragon] = Bit(dragon, "Chemtech"), [baron] = Bit(baron, "Tunnel") }).Select(o => o.Id).Order());
    }

    [Fact]
    public void TheMapSkinsOwnChoicesAreListedAsEvidenceAndOneOfThemDisagreesWithTheContainer()
    {
        if (Catalog11("base_srx") is not { } c) return;
        Assert.True(c.Declarations.SkinGrids.Count > 30, "map11.bin lists a MapSkin per skin");
        // the Default skin names AIPath_SRX_2 while its geometry set's container names AIPath_SRX_3: the editor follows the container
        var db = new HashSyncService().LoadLocal(_ => { });
        var defaultSkin = c.Declarations.SkinGrids.Single(s => db.TryGetBinName(s.Skin, out var n) && n.EndsWith("/Default", StringComparison.OrdinalIgnoreCase));
        Assert.EndsWith("AIPath_SRX_2.aimesh_ngrid", defaultSkin.Grid, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("skin Default", c.Catalog.Find("grid:aipath_srx_2")!.Detail);
    }

    [Fact]
    public void EveryShippedGridOfMap11And12IsVersion7AndParses()
    {
        foreach (string map in new[] { "Map11", "Map12" })
        {
            using var wad = Open(map);
            if (wad is null) continue;
            var grids = wad.Entries.Where(e => e.IsResolved && e.Path.EndsWith(".aimesh_ngrid", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.NotEmpty(grids);
            foreach (var e in grids)
            {
                Assert.True(NavGrid.TryParse(wad.Extract(e), out var g, out string? why), $"{e.Path}: {why}");
                Assert.Equal(7, g!.VersionMajor);
                Assert.True(g.HasFlags, e.Path);
            }
        }
    }

    [Fact]
    public void Map12AndMap22AlsoListTheirGrids()
    {
        foreach (var (map, folder, atLeast) in new[] { ("Map12", "map12", 6), ("Map22", "map22", 100) })
        {
            using var wad = Open(map);
            if (wad is null) continue;
            var catalog = NavGridVariantCatalog.Build(folder, "anything", wad.Entries.Where(e => e.IsResolved).Select(e => e.Path),
                NavGridDeclarations.Empty, null);
            Assert.True(catalog.Grids.Count >= atLeast, $"{map}: {catalog.Grids.Count}");
            Assert.Equal($"grid:aipath", catalog.DefaultGridId);   // no declaration: the classic grid, as before M843
        }
    }

    // ------------------------------------------------------------------ shared visibility function

    private static (MapGeoAsset Map, MapVisibilityDefinition Def, MapVisibilityControllers Controllers)? Open11()
    {
        using var wad = Open("Map11");
        if (wad is null) return null;
        var db = new HashSyncService().LoadLocal(_ => { });
        var def = MapVisibility.Parse(wad.Extract(HashAlgorithms.WadPath("data/maps/shipping/map11/map11.bin")), h => db.TryGetBinName(h, out var n) ? n : null);
        var controllers = MapVisibilityControllers.Build(new[] { wad.Extract(HashAlgorithms.WadPath("data/maps/mapgeometry/map11/base_srx.materials.bin")) }, def);
        var map = MapGeoDecoder.Decode(wad.Extract(HashAlgorithms.WadPath("data/maps/mapgeometry/map11/base_srx.mapgeo")), new HashSet<string>());
        return (map, def, controllers);
    }

    [Fact]
    public void TheEvaluatorIsTheSameDecisionTheStartStateMakes()
    {
        // MapStartState keeps its own loop (the Content Browser draws from it); on the same selection both must agree group for group.
        if (Open11() is not { } m) return;
        var start = App.Services.MapStartState.ResolveWith(m.Map, m.Def, m.Controllers, null);
        var selection = new VisibilitySelection(start.Selections, new HashSet<string>(StringComparer.OrdinalIgnoreCase), start.StageMask, true);
        var evaluated = MapVisibilityEvaluator.GroupsVisible(m.Map, new MapVisibilityResolver(m.Controllers, m.Def), selection);
        Assert.Equal(start.GroupVisible, evaluated);
    }

    [Fact]
    public void DragonAndBaronAndEventSelectionsChangeWhatIsDrawn()
    {
        if (Open11() is not { } m) return;
        var resolver = new MapVisibilityResolver(m.Controllers, m.Def);
        IReadOnlyList<int> Visible(Action<Dictionary<uint, int>, HashSet<string>> edit, bool regions = false)
        {
            var axes = m.Def.Axes.ToDictionary(a => a.DefinitionFieldHash, _ => 0);
            var events = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            edit(axes, events);
            return MapVisibilityEvaluator.VisibleMeshIndices(m.Map, resolver, new VisibilitySelection(axes, events, null, regions));
        }
        int Dragon(string layer) => m.Def.Primary!.Layers.Single(l => l.Name == layer).Bit;
        int Baron(string layer) => m.Def.Axes.Single(a => !a.IsPrimary).Layers.Single(l => l.Name == layer).Bit;
        uint baronAxis = m.Def.Axes.Single(a => !a.IsPrimary).DefinitionFieldHash;

        var all = Visible((_, _) => { });
        var infernal = Visible((a, _) => a[m.Def.Primary!.DefinitionFieldHash] = Dragon("Infernal"));
        var ocean = Visible((a, _) => a[m.Def.Primary!.DefinitionFieldHash] = Dragon("Ocean"));
        var cup = Visible((a, _) => a[baronAxis] = Baron("Cup"));
        var tunnel = Visible((a, _) => a[baronAxis] = Baron("Tunnel"));
        Assert.NotEqual(infernal, ocean);
        Assert.NotEqual(cup, tunnel);
        Assert.NotEqual(all, infernal);

        // an event switches its content on: some event of the map adds meshes
        // (SR_Hall_Of_Legends gates 2 meshes of base_srx; the sponsor banners are props, which are no mapgeo mesh)
        var changed = m.Controllers.EventNames.Where(ev => Visible((_, e) => e.Add(ev)).Count > all.Count).ToList();
        Assert.Contains("SR_Hall_Of_Legends", changed);

        // render-region meshes show only with regions on
        if (m.Map.Meshes.Any(x => x.RegionHash != 0))
        {
            var regions = Visible((_, _) => { }, regions: true);
            Assert.True(regions.Count > all.Count);
            Assert.All(all, i => Assert.Contains(i, regions));
        }

        // a mesh is drawn exactly when one of its draw groups is
        var perGroup = MapVisibilityEvaluator.GroupsVisible(m.Map, resolver,
            new VisibilitySelection(m.Def.Axes.ToDictionary(a => a.DefinitionFieldHash, a => a.IsPrimary ? Dragon("Infernal") : 0),
                new HashSet<string>(), null, false));
        var drawn = Enumerable.Range(0, perGroup.Length).Where(i => perGroup[i]).Select(i => m.Map.Groups[i].MeshIndex).ToHashSet();
        Assert.Equal(infernal.OrderBy(i => i), drawn.OrderBy(i => i));
    }

    [Fact]
    public void ASelectionTheMapCannotTakeIsRefusedWithAReason()
    {
        var def = new MapVisibilityDefinition(new[]
        {
            new MapVisibilityAxis(MapVisibility.PrimaryAxisHash, "Map Visibility", 1, true,
                new[] { new VisibilityLayer("Base", 1), new VisibilityLayer("Infernal", 2) }),
        });
        var events = new[] { "SR_Hall_Of_Legends" };
        bool Try(string json, out VisibilitySelection s, out string? err)
        {
            using var doc = JsonDocument.Parse(json);
            return BlenderLinkVisibility.TryParseSelection(doc.RootElement, def, events, null, out s, out _, out err);
        }

        Assert.True(Try("{}", out var defaults, out _));
        Assert.Equal(0, defaults.Axes[MapVisibility.PrimaryAxisHash]);   // the editor opens on All
        Assert.Empty(defaults.Events);
        Assert.False(defaults.RenderRegions);

        Assert.True(Try("{\"selection\":{\"axes\":{\"0x9e019715\":2},\"events\":[\"sr_hall_of_legends\"],\"render_regions\":true}}", out var ok, out _));
        Assert.Equal(2, ok.Axes[MapVisibility.PrimaryAxisHash]);
        Assert.Contains("SR_Hall_Of_Legends", ok.Events);
        Assert.True(ok.RenderRegions);

        Assert.False(Try("{\"axes\":{\"0x9e019715\":4}}", out _, out string? bit));
        Assert.Contains("no layer with bit 4", bit);
        Assert.False(Try("{\"axes\":{\"0x11111111\":1}}", out _, out string? axis));
        Assert.Contains("no visibility axis", axis);
        Assert.False(Try("{\"events\":[\"Nope\"]}", out _, out string? ev));
        Assert.Contains("no event", ev);
        Assert.False(Try("{\"stage\":2}", out _, out string? stage));
        Assert.Contains("no board stage", stage);
    }
}
