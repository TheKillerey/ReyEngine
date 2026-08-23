using ReyEngine.Core.Hashing;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M559: a decal that writes depth is harmless if it draws AFTER the ground.
///
/// <para>M558 confirmed the client gives these the depth mask - the editor reproduces the black the
/// moment it does the same. The damage needs BOTH halves though: the ground is already in the
/// framebuffer by the time a late decal draws, so its depth write changes nothing. M279 measured the
/// pairing going wrong in our own renderer, with base_chasm1's decal at draw position 395 of 426 while
/// the ground under it drew at 407-414.</para>
/// </summary>
public sealed class DecalDrawOrderTests
{
    private const string Legacy = @"K:\LeagueSandbox\League_Sandbox_Client\LEVELS\Map2";
    private const string Wad =
        @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping\Map453.wad.client";
    private const string GeoPath = "data/maps/mapgeometry/map453/jade_container.mapgeo";

    private static readonly Lazy<MapGeoAsset?> Ported = new(() =>
    {
        if (!Directory.Exists(Legacy) || !File.Exists(Wad)) return null;
        using var wad = ReyEngine.Core.Wad.WadArchive.Open(Wad);
        ulong hash = HashAlgorithms.WadPath(GeoPath);
        if (!wad.TryGetEntry(hash, out _)) return null;
        return MapGeoDecoder.Decode(LegacyMapPorter.Port(Legacy, wad.Extract(hash), GeoPath).MapGeoBytes);
    });

    private static bool IsDecal(string material) =>
        material.Contains("LegacyPort/", StringComparison.OrdinalIgnoreCase)
        && material.Contains("/Decal_", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void EveryDecalIsSubmittedAfterEveryOpaqueSurface()
    {
        if (Ported.Value is not { } a) return;

        int firstDecal = int.MaxValue, lastOpaque = -1;
        for (int i = 0; i < a.Groups.Count; i++)
        {
            if (IsDecal(a.Groups[i].Material)) firstDecal = Math.Min(firstDecal, i);
            else lastOpaque = Math.Max(lastOpaque, i);
        }
        if (firstDecal == int.MaxValue) return;

        Assert.True(firstDecal > lastOpaque,
            $"first decal submits at {firstDecal} but an opaque surface still submits at {lastOpaque}; "
            + "a decal that draws before the ground it sits on composites over nothing");
    }

    [Fact]
    public void TheReorderMovesMeshesAndChangesNothingElse()
    {
        // A repartition of the draw list, not a rewrite of the geometry: same decal count, same triangles.
        if (Ported.Value is not { } a) return;

        Assert.InRange(a.Groups.Count(g => IsDecal(g.Material)), 900, 1_200);
        Assert.InRange(a.Indices.Length / 3, 1_400_000, 1_700_000);
    }

    [Fact]
    public void GameDepthAlsoStopsTheEditorReordering()
    {
        // Emulating the client's depth mask without also emulating its ORDER reproduces half of what the
        // client does - and the ordering half is the one a mapgeo change can fix. Without this the reorder
        // above is invisible in the editor, because the renderer regroups draws by pipeline anyway.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return;

        string host = Path.Combine(dir.FullName, "src", "ReyEngine.App", "Views", "MainWindow.axaml.cs");
        string surface = Path.Combine(dir.FullName, "src", "ReyEngine.App", "Views", "Dx11ViewportSurface.cs");
        if (!File.Exists(host) || !File.Exists(surface)) return;

        Assert.Contains("_dx11.SortByPipeline = !vm.ClientDepthRules;", File.ReadAllText(host));
        Assert.Contains("SortByPipeline = SortByPipeline,", File.ReadAllText(surface));
    }
}
