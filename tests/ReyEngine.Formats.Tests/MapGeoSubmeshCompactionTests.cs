using System.Numerics;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M654: <see cref="MapGeoMeshImporter.ToScene"/> emits one entry per SUBMESH, and until M654 handed
/// every entry its PARENT mesh's whole vertex buffer with the submesh's own absolute indices.
///
/// <para>Both places that read <c>Positions</c> were therefore reading the parent: the window's
/// over-65,535 gate, and the staging code's <c>BoundsOf</c>, which derives where the imported mesh is
/// placed. Measured over Map11's 27 shipping mapgeos before the fix: up to 45 entries per file whose own
/// centre is not their parent's, the worst 1,850 units away from where the gizmo was — pick a piece,
/// place it at the cursor, and it lands somewhere else.</para>
///
/// <para>The u16 index question the compaction also settles is NOT a defect Riot's own files exhibit:
/// 0 of 601 entries in base_srx has an index above 65,535, so this is about ported and merged maps.
/// Recorded here so the claim is not overstated.</para>
/// </summary>
public sealed class MapGeoSubmeshCompactionTests
{
    private const string Wad = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping\Map11.wad.client";
    private const string GeoPath = "data/maps/mapgeometry/map11/base_srx.mapgeo";

    private static byte[]? ShippedMapGeo()
    {
        if (!File.Exists(Wad)) return null;
        try
        {
            using var wad = ReyEngine.Core.Wad.WadArchive.Open(Wad);
            ulong hash = HashAlgorithms.WadPath(GeoPath);
            return wad.TryGetEntry(hash, out _) ? wad.Extract(hash) : null;
        }
        catch { return null; }
    }

    [Fact]
    public void EveryEntryCarriesOnlyTheVerticesItsOwnTrianglesReach()
    {
        if (ShippedMapGeo() is not { } bytes) return;   // no installed game on this machine
        var scene = MapGeoMeshImporter.ToScene(bytes, out string? error);
        Assert.Null(error);
        Assert.NotNull(scene);
        Assert.NotEmpty(scene!.Meshes);

        foreach (var mesh in scene.Meshes)
        {
            int vertexCount = mesh.Positions.Length / 3;
            Assert.Equal(vertexCount * 3, mesh.Normals.Length);
            Assert.Equal(vertexCount * 2, mesh.Uvs.Length);

            // Renumbered from zero and dense: no index out of range, and no vertex carried along unused.
            Assert.All(mesh.Indices, i => Assert.InRange(i, 0, vertexCount - 1));
            Assert.Equal(vertexCount, mesh.Indices.Distinct().Count());
            Assert.Equal(0, mesh.Indices.Length % 3);
        }
    }

    /// <summary>
    /// The placement defect itself: a mesh drawn with two materials becomes two entries, and each one
    /// must now have its OWN bounds. The staging code places an import at the gizmo by subtracting the
    /// centre of its bounds, so two entries sharing one set of bounds is exactly the offset that was
    /// measured at up to 1,850 units.
    /// </summary>
    [Fact]
    public void TwoSubmeshesOfOneMeshNoLongerShareTheirParentsBounds()
    {
        if (ShippedMapGeo() is not { } bytes) return;
        var scene = MapGeoMeshImporter.ToScene(bytes, out _);
        Assert.NotNull(scene);

        // Entries split off a multi-material mesh are named "<mesh>_<material>"; group by that prefix.
        var groups = scene!.Meshes
            .Select(m => (Mesh: m, Prefix: m.Name.Contains('_') ? m.Name[..m.Name.LastIndexOf('_')] : m.Name))
            .GroupBy(x => x.Prefix, StringComparer.Ordinal)
            .Where(g => g.Select(x => x.Mesh.MaterialName).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .ToList();
        if (groups.Count == 0) return;   // no multi-material mesh in this file - nothing to prove here

        int distinct = 0;
        foreach (var group in groups)
        {
            var centres = group.Select(x => Centre(x.Mesh.Positions)).ToList();
            for (int i = 1; i < centres.Count; i++)
                if ((centres[i] - centres[0]).Length() > 1f) distinct++;
        }
        // At least one really is somewhere else - which is the whole point of the fix. Before it, every
        // entry of a group reported the parent's centre and this was always zero.
        Assert.True(distinct > 0,
            "no multi-material mesh had submeshes at different places, so this file cannot show the defect");
    }

    private static Vector3 Centre(float[] positions)
    {
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        for (int i = 0; i < positions.Length; i += 3)
        {
            var v = new Vector3(positions[i], positions[i + 1], positions[i + 2]);
            min = Vector3.Min(min, v); max = Vector3.Max(max, v);
        }
        return (min + max) * 0.5f;
    }
}
