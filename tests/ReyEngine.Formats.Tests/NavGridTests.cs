using ReyEngine.Core.Hashing;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M562/M565: the navigation grid and the flag plane inside it.
///
/// <para>The flags are NOT named, and that is the point. M562 called 0x0004 the bush bit from the shape
/// of its clusters and from Howling Abyss and TFT scoring zero, which looked convincing; the reporter
/// then looked at those cells drawn on their own map and identified them as the area only one team may
/// walk. So these tests assert what is MEASURED - which bits exist, how many cells carry them, that the
/// plane is aligned to the grid - and leave the meaning to whoever is looking at the map.</para>
/// </summary>
public sealed class NavGridTests
{
    private const string Shipping = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping";

    private static NavGrid? Load(string map)
    {
        string wadPath = Path.Combine(Shipping, char.ToUpperInvariant(map[0]) + map[1..] + ".wad.client");
        if (!File.Exists(wadPath)) return null;
        using var wad = ReyEngine.Core.Wad.WadArchive.Open(wadPath);
        ulong hash = HashAlgorithms.WadPath(NavGrid.PathFor(map));
        if (!wad.TryGetEntry(hash, out _)) return null;
        return NavGrid.TryParse(wad.Extract(hash), out var grid, out _) ? grid : null;
    }

    [Fact]
    public void TheGridPathIsWhereRiotActuallyKeepsIt()
    {
        // Every path guessed from convention missed - data/maps/shipping, levels/, *.ngrid on disk. This
        // one came from the repo's own resolved path list.
        Assert.Equal("assets/maps/navgrid/map11/aipath.aimesh_ngrid", NavGrid.PathFor("Map11"));
    }

    [Theory]
    [InlineData("map11", 295, 296)]
    [InlineData("map12", 248, 237)]
    [InlineData("map453", 321, 321)]
    public void TheHeaderReadsTheGridRiotShipped(string map, int countX, int countZ)
    {
        if (Load(map) is not { } grid) return;
        Assert.Equal(7, grid.VersionMajor);
        Assert.Equal(countX, grid.CountX);
        Assert.Equal(countZ, grid.CountZ);
        Assert.Equal(50f, grid.CellSize);
        Assert.True(grid.HasFlags, "the flag plane should be reachable at the derived offset");
    }

    [Fact]
    public void TheGridCoversTheMapItBelongsTo()
    {
        // min/max are world space, so the lattice has to span roughly countX*cellSize. If the header were
        // being misread, this is what would go wrong first.
        if (Load("map11") is not { } grid) return;
        Assert.InRange(grid.Max.X - grid.Min.X, grid.CountX * grid.CellSize * 0.9f, grid.CountX * grid.CellSize * 1.1f);
        Assert.InRange(grid.Max.Z - grid.Min.Z, grid.CountZ * grid.CellSize * 0.9f, grid.CountZ * grid.CellSize * 1.1f);
    }

    [Fact]
    public void Flag0x0004IsPresentOnSummonersRiftAndAbsentOnHowlingAbyssAndTft()
    {
        // Stated as a MEASUREMENT, not an interpretation. This distribution is what made 0x0004 look like
        // the bush in M562 and it is still true - it just does not mean what it was taken to mean, since
        // the reporter identified the same cells on their map as a team-restricted walk area. Pinned
        // because the numbers are real and the next person deserves them without the wrong label.
        var sr = Load("map11");
        var abyss = Load("map12");
        var tft = Load("map22");
        if (sr is null) return;

        int srCells = sr.CountWith(NavGrid.TeamRestrictedFlag);
        Assert.True(srCells > 500, $"expected 0x0004 on Summoner's Rift, saw {srCells} cells");
        Assert.InRange(srCells / (double)sr.CellCount, 0.005, 0.05);   // measured 1.2%
        if (abyss is not null) Assert.Equal(0, abyss.CountWith(NavGrid.TeamRestrictedFlag));
        if (tft is not null) Assert.Equal(0, tft.CountWith(NavGrid.TeamRestrictedFlag));
    }

    [Fact]
    public void EveryFlagInTheGridIsReportedSoTheUserCanLabelIt()
    {
        // The editor draws one toggleable layer per flag rather than picking which one matters, because
        // picking is exactly what went wrong in M562.
        if (Load("map11") is not { } grid) return;
        var present = grid.PresentFlags();

        Assert.NotEmpty(present);
        Assert.All(present, f => Assert.True(System.Numerics.BitOperations.PopCount(f.Mask) == 1,
            $"0x{f.Mask:x4} is not a single bit"));
        Assert.All(present, f => Assert.True(f.Cells > 0));
        // commonest first, so the default layer is the one most likely to show something
        Assert.True(present[0].Cells >= present[^1].Cells);
        Assert.Contains(present, f => f.Mask == NavGrid.BlockedFlag);
        Assert.Contains(present, f => f.Mask == NavGrid.TeamRestrictedFlag);
    }

    [Fact]
    public void TheBlockedBitFillsTheBorder()
    {
        // Bit 1 covers 39% of Summoner's Rift and rings the map, which is what unwalkable looks like.
        // Pinned because it is the sanity check that the plane is aligned to the grid at all: if the
        // offset were wrong by even one cell the border would not be solid.
        if (Load("map11") is not { } grid) return;

        int border = 0, solid = 0;
        for (int x = 0; x < grid.CountX; x++)
            foreach (int z in new[] { 0, grid.CountZ - 1 })
            { border++; if (grid.Has(x, z, NavGrid.BlockedFlag)) solid++; }

        Assert.True(solid > border * 0.95, $"only {solid} of {border} border cells are blocked");
    }

    [Fact]
    public void CellBoundsLandInsideTheGrid()
    {
        if (Load("map11") is not { } grid) return;
        var (lo, hi) = grid.CellBounds(0, 0);
        Assert.Equal(grid.Min.X, lo.X, 3);
        Assert.Equal(grid.Min.Z, lo.Z, 3);
        Assert.Equal(grid.CellSize, hi.X - lo.X, 3);

        var (lastLo, _) = grid.CellBounds(grid.CountX - 1, grid.CountZ - 1);
        Assert.InRange(lastLo.X, grid.Min.X, grid.Max.X);
        Assert.InRange(lastLo.Z, grid.Min.Z, grid.Max.Z);
    }

    [Fact]
    public void FlaggedCellsComeBackAsWorldBoxes()
    {
        if (Load("map11") is not { } grid) return;
        var boxes = grid.CellsWith(NavGrid.TeamRestrictedFlag).Take(50).ToList();
        Assert.NotEmpty(boxes);
        foreach (var (lo, hi) in boxes)
        {
            Assert.Equal(grid.CellSize, hi.X - lo.X, 3);
            Assert.Equal(grid.CellSize, hi.Z - lo.Z, 3);
            Assert.InRange(lo.X, grid.Min.X, grid.Max.X);
        }
    }

    [Fact]
    public void TheOverlayIsWiredAllTheWayToTheRenderer()
    {
        // Four hops, and a break in any of them shows as a toggle that does nothing: view model property,
        // XAML binding, control property, renderer upload.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return;
        string Read(params string[] parts) =>
            File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));

        var vm = typeof(ReyEngine.App.ViewModels.MainWindowViewModel);
        Assert.NotNull(vm.GetProperty("ShowBushAreas"));
        Assert.NotNull(vm.GetProperty("BushCellLines"));
        Assert.NotNull(vm.GetProperty("BushCellLayers"));
        Assert.NotNull(vm.GetProperty("NavGridLayers"));
        Assert.NotNull(vm.GetProperty("ToggleNavGridOverlayCommand"));
        string axaml = Read("src", "ReyEngine.App", "Views", "MainWindow.axaml");
        Assert.Contains("BushCellLines=\"{Binding BushCellLines}\"", axaml);
        Assert.Contains("BushCellLayers=\"{Binding BushCellLayers}\"", axaml);
        Assert.Contains("{Binding NavGridLayers}", axaml);
        Assert.Contains("SetBushCellMesh(BushCellLines, BushCellLayers)",
            Read("src", "ReyEngine.App", "Views", "ViewportControl.cs"));
        Assert.Contains("public unsafe void SetBushCellMesh",
            Read("src", "ReyEngine.Rendering", "ViewportMeshRenderer.cs"));
    }

    [Fact]
    public void CellHeightsComeFromTheCellRecordWhereTheGridHasThem()
    {
        // The first float of each 48-byte record is the ground height. Identified by it matching the
        // header: on Summoner's Rift it runs -71.2 to 184.3 across 26,545 distinct values and the grid's
        // own Y bounds are -71.2 to 184.5.
        if (Load("map11") is not { } grid) return;
        Assert.True(grid.HasHeights);

        float lo = float.MaxValue, hi = float.MinValue;
        foreach (float h in grid.Heights) { lo = Math.Min(lo, h); hi = Math.Max(hi, h); }
        Assert.InRange(lo, grid.Min.Y - 1f, grid.Min.Y + 1f);
        Assert.InRange(hi, grid.Max.Y - 5f, grid.Max.Y + 5f);

        // and a flagged cell is then placed at its own ground rather than at the grid floor
        var (bl, _) = grid.CellsWith(NavGrid.TeamRestrictedFlag).First();
        Assert.InRange(bl.Y, grid.Min.Y, grid.Max.Y);
    }

    [Fact]
    public void AStubGridReportsThatItHasNoHeights()
    {
        // Map453 ships a navgrid with flat Y bounds and every height zero. Saying so is the difference
        // between "the bush is at sea level" and "this grid does not know where the ground is" - and the
        // overlay looked broken until that was distinguishable.
        if (Load("map453") is not { } grid) return;
        Assert.False(grid.HasHeights);
        Assert.Empty(grid.Heights);
        Assert.Equal(grid.Min.Y, grid.CellBounds(0, 0).Min.Y, 3);
    }

    [Fact]
    public void EveryMapOpenLoadsTheNavGrid()
    {
        // M564: there are TWO paths that open a map, and only one was hooked - so the overlay worked on
        // one and reported "no navgrid loaded" on the other, which is how it was reported. The rule is
        // simply that every place which adopts a map entry must also go looking for its grid.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return;
        string file = Path.Combine(dir.FullName, "src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        if (!File.Exists(file)) return;
        string source = File.ReadAllText(file);

        // Capture what is assigned and filter, rather than a negative lookahead: \s* backtracks to
        // zero width, so (?!null) happily matches at the space before it and the clearing assignment
        // counts as a map open. The guard's first finding was its own bug.
        int adopts = System.Text.RegularExpressions.Regex.Matches(source, @"_currentMapEntry\s*=\s*([A-Za-z_][\w.]*)")
            .Count(m => m.Groups[1].Value != "null");
        int loads = System.Text.RegularExpressions.Regex.Matches(
            source, @"LoadNavGridForCurrentMap\(").Count - 1;   // minus the declaration

        Assert.True(adopts > 0, "expected at least one place to adopt a map entry");
        Assert.True(loads >= adopts,
            $"{adopts} place(s) adopt a map entry but only {loads} load the navgrid - the overlay will "
            + "report 'no navgrid loaded' on whichever path was missed");
    }

    [Fact]
    public void RubbishIsRefusedQuietly()
    {
        // Opened opportunistically alongside a map, so a miss must not throw.
        Assert.False(NavGrid.TryParse(Array.Empty<byte>(), out _, out _));
        Assert.False(NavGrid.TryParse(new byte[200], out _, out var why));   // version 0
        Assert.Contains("version", why ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
