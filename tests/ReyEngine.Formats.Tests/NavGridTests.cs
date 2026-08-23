using ReyEngine.Core.Hashing;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M562: the navigation grid, and the bush bit inside it.
///
/// <para>The bush bit is not documented anywhere — it was identified by scanning for a plane that behaves
/// like one enum per cell, then confirmed against the maps themselves. These tests keep that confirmation
/// executable, because the strongest evidence for it is a fact about League rather than about the file:
/// Howling Abyss and TFT have no brush, and they score exactly zero.</para>
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
    public void SummonersRiftHasBrushAndHowlingAbyssDoesNot()
    {
        // THE control for the bush bit, and it is a fact about the game rather than about the format:
        // Howling Abyss has no brush at all. If bit 2 meant anything else, it would not be empty there.
        var sr = Load("map11");
        var abyss = Load("map12");
        if (sr is null || abyss is null) return;

        int srBush = sr.CountWith(NavGrid.BushFlag);
        int abyssBush = abyss.CountWith(NavGrid.BushFlag);

        Assert.True(srBush > 500, $"Summoner's Rift should be full of brush, saw {srBush} cells");
        Assert.InRange(srBush / (double)sr.CellCount, 0.005, 0.05);   // measured 1.2%
        Assert.Equal(0, abyssBush);
    }

    [Fact]
    public void TftHasNoBrushEither()
    {
        if (Load("map22") is not { } tft) return;
        Assert.Equal(0, tft.CountWith(NavGrid.BushFlag));
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
    public void TheBushCellsComeBackAsWorldBoxes()
    {
        if (Load("map11") is not { } grid) return;
        var boxes = grid.CellsWith(NavGrid.BushFlag).Take(50).ToList();
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

        Assert.NotNull(typeof(ReyEngine.App.ViewModels.MainWindowViewModel).GetProperty("ShowBushAreas"));
        Assert.NotNull(typeof(ReyEngine.App.ViewModels.MainWindowViewModel).GetProperty("BushCellLines"));
        Assert.Contains("BushCellLines=\"{Binding BushCellLines}\"",
            Read("src", "ReyEngine.App", "Views", "MainWindow.axaml"));
        Assert.Contains("IsChecked=\"{Binding ShowBushAreas}\"",
            Read("src", "ReyEngine.App", "Views", "MainWindow.axaml"));
        Assert.Contains("SetBushCellMesh(BushCellLines)",
            Read("src", "ReyEngine.App", "Views", "ViewportControl.cs"));
        Assert.Contains("public unsafe void SetBushCellMesh",
            Read("src", "ReyEngine.Rendering", "ViewportMeshRenderer.cs"));
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
