using System;
using System.IO;
using System.Linq;
using System.Numerics;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Lighting;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Meta;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M680: placed props are lit by the map's lightgrid - the six-colour ambient cube sampled where each
/// placement stands, handed to the character shaders as LIGHTGRID_COLORS per placement, with the grid's
/// own LIGHTGRID_SCALE. The sampling is pinned on a synthetic grid; the real grid Map453 ships is read
/// through the same MapBakeProperties declaration the game follows.
/// </summary>
public sealed class PropLightGridTests
{
    private static LightGridFile Grid2x2()
    {
        // world 200 x 200, cells centred at 50 and 150; every direction of a cell carries the cell's colour
        var g = LightGridFile.Create(2, 2, 200f, 200f);
        var colours = new[] { new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1), new Vector3(1, 1, 1) };
        for (int c = 0; c < 4; c++)
            for (int d = 0; d < LightGridFile.Directions; d++)
                g.Samples[c * LightGridFile.Directions + d] = colours[c];
        return g;
    }

    [Fact]
    public void ACellCentreReadsItsOwnColourAndTheGridIsAnchoredAtTheOrigin()
    {
        var g = Grid2x2();
        Assert.Equal(new Vector3(1, 0, 0), g.SampleAmbient(new Vector3(50, 0, 50))[0]);     // cx 0, cz 0
        Assert.Equal(new Vector3(0, 1, 0), g.SampleAmbient(new Vector3(150, 0, 50))[0]);    // cx 1, cz 0
        Assert.Equal(new Vector3(0, 0, 1), g.SampleAmbient(new Vector3(50, 0, 150))[0]);    // cx 0, cz 1
        Assert.Equal(new Vector3(1, 1, 1), g.SampleAmbient(new Vector3(150, 0, 150))[3]);   // any direction
    }

    [Fact]
    public void BetweenCellsItBlendsAndBeyondTheEdgeTheEdgeHolds()
    {
        var g = Grid2x2();
        var mid = g.SampleAmbient(new Vector3(100, 0, 50))[0];   // halfway between red and green
        Assert.Equal(0.5f, mid.X, 3); Assert.Equal(0.5f, mid.Y, 3); Assert.Equal(0f, mid.Z, 3);
        Assert.Equal(new Vector3(1, 0, 0), g.SampleAmbient(new Vector3(-500, 0, -500))[0]);
        Assert.Equal(new Vector3(1, 1, 1), g.SampleAmbient(new Vector3(9000, 0, 9000))[0]);
    }

    [Fact]
    public void TheCubeIsLaidOutAsTheShaderIndexesItAndTheScaleIsTheEnginesOwn()
    {
        var g = Grid2x2();
        g.FullBrightScale = 0.25f;
        g.CharacterFullBrightIntensity = 0.5f;
        var f = LightGridFile.ToLightGridColors(g.SampleAmbient(new Vector3(150, 0, 150)));
        Assert.Equal(24, f.Length);
        for (int d = 0; d < 6; d++) { Assert.Equal(1f, f[d * 4]); Assert.Equal(1f, f[d * 4 + 3]); }
        Assert.Equal(new[] { 1f, 0.5f, 0f, 0f }, g.LightGridScale);
    }

    private static readonly Lazy<HashDatabase?> Database = new(() =>
    {
        try { return new HashSyncService().LoadLocal(_ => { }); }
        catch { return null; }
    });

    [Fact]
    public void Map453DeclaresAGridAndItReadsWhereTheCampsStand()
    {
        string wadPath = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping\Map453.wad.client";
        if (!File.Exists(wadPath) || Database.Value is not { } database) return;
        using var wad = WadArchive.Open(wadPath, new WadPathResolver(database));
        byte[]? Read(string path) { ulong h = BinTexturePath.HashOfReference(path); return wad.TryGetEntry(h, out _) ? wad.Extract(h) : null; }

        var bin = Read("data/maps/mapgeometry/map453/jade_container.materials.bin");
        if (bin is null) return;
        var bake = MapBakeProperties.Read(bin);
        Assert.NotNull(bake);
        Assert.NotEmpty(bake!.Value.File);
        var bytes = Read(bake.Value.File);
        Assert.NotNull(bytes);
        Assert.True(LightGridFile.LooksLikeLightGrid(bytes!));

        var grid = LightGridFile.Read(bytes!);
        Assert.True(grid.Width > 0 && grid.Height > 0 && grid.WorldSizeX > 0 && grid.WorldSizeZ > 0);
        var cube = grid.SampleAmbient(new Vector3(grid.WorldSizeX * 0.5f, 0f, grid.WorldSizeZ * 0.5f));
        Assert.Equal(6, cube.Length);
        Assert.True(cube.Any(c => c.LengthSquared() > 0f), "the middle of the map is not unlit");
        Assert.Equal(grid.FullBrightScale * 4f, grid.LightGridScale[0]);
    }

    [Fact]
    public void EachPlacementCarriesItsOwnCubeThroughTheRendererAndTheWindow()
    {
        var renderer = Source("src", "ReyEngine.Rendering.D3D11", "ShaderPreviewRenderer.cs");
        var driver = Source("src", "ReyEngine.App", "Services", "D3D11MapProps.cs");
        var window = Source("src", "ReyEngine.App", "Views", "MainWindow.axaml.cs");
        if (renderer is null || driver is null || window is null) return;
        int draw = renderer.IndexOf("private void DrawCharacterInstances(", StringComparison.Ordinal);
        Assert.True(draw > 0);
        Assert.Contains("mat.Params[\"LIGHTGRID_COLORS\"] = cube;", renderer[draw..]);
        Assert.Contains("mat.CharacterInstanceAmbient = g.Ambient;", driver);
        Assert.Contains("mat.Params[\"LIGHTGRID_SCALE\"] = scale;", driver);
        int lighting = window.IndexOf("_dx11.PropLightingAt ??= vm.PropLightingAt;", StringComparison.Ordinal);
        int props = window.IndexOf("_dx11.PropMeshes = vm.CurrentPropMeshes;", StringComparison.Ordinal);
        Assert.True(lighting > 0 && props > lighting, "PropLightingAt must be supplied before PropMeshes is set");
    }

    private static string? Source(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        return null;
    }
}
