using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M585: a bucket grid carries its own baked copy of the map, and the GAME culls against that copy.
///
/// <para>So any edit that changes geometry has to rebuild it. When it does not, the map still loads and
/// still looks right from where you are standing — meshes and decals blink out as the camera turns,
/// because the grid is hiding things that have moved out from under its cells. Measured on a real edited
/// map: the grid claimed 945,676 baked vertices where the geometry had 843,339.</para>
/// </summary>
public sealed class BucketGridStalenessTests
{
    private static string? ViewModelSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(dir.FullName, "src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    [Fact]
    public void EveryGeometryEditRebuildsTheGridItInvalidates()
    {
        if (ViewModelSource() is not { } source) return;

        int at = source.IndexOf("// 2b) M105: bucket grids bake per-face visibility", StringComparison.Ordinal);
        Assert.True(at > 0, "the bucket-grid regeneration block has moved");
        string block = source[at..(at + 1400)];

        // Moves and appends rebuild on their own paths; these are the ones that reach this branch.
        foreach (string edit in new[] { "hasLayers", "hasReshapes", "hasFaces", "hasGrows" })
            Assert.True(block.Contains(edit, StringComparison.Ordinal),
                $"'{edit}' changes geometry but no longer triggers a bucket-grid rebuild — "
                + "the game would cull against the old shape");
    }

    [Fact]
    public void AReshapeLeavesTheGridDisagreeingUntilItIsRebuilt()
    {
        // The failure itself, on real data: reshape a mesh, and the stored grid no longer matches what a
        // rebuild from the new geometry produces. This is what the save has to notice.
        string wad = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping\Map453.wad.client";
        if (!File.Exists(wad)) return;
        using var archive = ReyEngine.Core.Wad.WadArchive.Open(wad);
        ulong hash = ReyEngine.Core.Hashing.HashAlgorithms.WadPath("data/maps/mapgeometry/map453/jade_container.mapgeo");
        if (!archive.TryGetEntry(hash, out _)) return;
        byte[] bytes = archive.Extract(hash);

        var before = MapGeoDecoder.Decode(bytes);
        if (before.BucketGrids.Count == 0) return;

        var refusals = MapGeoMeshReshaper.Refusals(bytes);
        MapGeoBinary.TryReadEditable(bytes, out var editable);
        int index = -1;
        for (int i = 0; i < editable.Meshes.Count && index < 0; i++)
            if (!refusals.ContainsKey(i) && editable.Meshes[i].VertexCount > 64) index = i;
        if (index < 0) return;

        // A tetrahedron in place of a real mesh: a big, unmistakable change in the baked total.
        var reshaped = MapGeoMeshReshaper.TryApply(bytes, new[]
        {
            new MeshReshape(index,
                new float[] { 0, 0, 0, 100, 0, 0, 0, 100, 0, 0, 0, 100 },
                null, null, new uint[] { 0, 1, 2, 0, 1, 3, 1, 2, 3, 0, 2, 3 }),
        }, out string? error);
        Assert.True(reshaped is not null, error);

        var after = MapGeoDecoder.Decode(reshaped!);
        var rebuilt = MapBucketGridBuilder.Rebuild(after, 1000f);
        Assert.NotEmpty(rebuilt);

        // The grid the file still carries describes the geometry that WAS there.
        Assert.NotEqual(after.BucketGrids[0].VertexCount, rebuilt[0].Vertices.Count);
    }
}
