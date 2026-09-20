using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using ReyEngine.Formats.MapGeo;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M738: the ported decal overlay is welded flat onto the terrain, and everything drawn at ground level
/// lands in the same depth plane because of it.
///
/// <para>Measured on the Harrowing port against Riot's own Map453, per decal vertex, against the highest
/// surface not above it: the port has 15,843 of 15,845 decal vertices within 0.001 of the ground (median
/// gap 0.000); Riot's own decals on that map sit a median 6.37 units clear and are coplanar in 9.3% of
/// cases. That is why a champion's ground projection and a turret shot's projected sprite look wrong over
/// this map and nowhere else - the materials are identical to Riot's, shader and blend factors alike.</para>
/// </summary>
public sealed class LegacyDecalLiftTests
{
    private const string Ported =
        @"D:\ReyEngine\Classic Rift - Halloween\Map453\data\maps\mapgeometry\map453\jade_container.mapgeo";

    [Fact]
    public void OnlyThePortersOwnDecalsAreSelected()
    {
        // the porter's naming: LegacyPort/<source>/Decal_<texture>
        Assert.True(LegacyMapPorter.IsLegacyDecalMaterial("LegacyPort/map2/Decal_order_seam"));
        Assert.True(LegacyMapPorter.IsLegacyDecalMaterial("legacyport/map2/decal_base_chasm1"));

        // the destination map's own decals are placed correctly and must not move
        Assert.False(LegacyMapPorter.IsLegacyDecalMaterial("Maps/KitPieces/Jade/Base/Materials/Default/Jade_Decal_EA_MAT"));
        Assert.False(LegacyMapPorter.IsLegacyDecalMaterial("Maps/KitPieces/TestMaps/Base/Materials/Default/new_stone_road_decalVersion3_no_shadow"));

        // ported geometry that is not a decal must not move either - it IS the terrain
        Assert.False(LegacyMapPorter.IsLegacyDecalMaterial("LegacyPort/map2/Ground_order_dirt"));
        Assert.False(LegacyMapPorter.IsLegacyDecalMaterial("LegacyPort/map2/Wall_stone"));
        Assert.False(LegacyMapPorter.IsLegacyDecalMaterial(null));
        Assert.False(LegacyMapPorter.IsLegacyDecalMaterial(""));
    }

    /// <summary>The real port: 1,036 decal meshes, which is the count the porter's own research records,
    /// and none of the map's own geometry.</summary>
    [Fact]
    public void TheHarrowingPortSelectsItsDecalOverlayAndNothingElse()
    {
        if (!File.Exists(Ported)) return;
        var map = MapGeoDecoder.Decode(File.ReadAllBytes(Ported));

        var decals = LegacyMapPorter.ImportedDecalMeshes(map);
        var imported = LegacyMapPorter.ImportedMeshes(map);
        Assert.NotEmpty(decals);
        Assert.True(decals.Count < imported.Count, "the decal overlay cannot be the whole import");

        // every selected mesh carries only ported decal groups
        var selected = decals.Select(m => m.Index).ToHashSet();
        foreach (var g in map.Groups.Where(g => selected.Contains(g.MeshIndex)))
            Assert.True(LegacyMapPorter.IsLegacyDecalMaterial(g.Material),
                $"mesh {g.MeshIndex} was selected but carries '{g.Material}'");
    }

    /// <summary>A binding that names a member the view model does not have compiles, passes every test,
    /// and fails silently at runtime (that is how no menu entry glowed for four releases). So the two
    /// names this milestone adds are checked against the view model itself.</summary>
    [Fact]
    public void TheLiftControlsAreBoundToMembersThatExist()
    {
        string? xaml = Source("src", "ReyEngine.App", "Views", "MapInspectorView.axaml");
        if (xaml is null) return;
        Assert.Contains("{Binding DecalLift}", xaml);
        Assert.Contains("{Binding LiftLegacyDecalsCommand}", xaml);

        var vm = typeof(ReyEngine.App.ViewModels.MainWindowViewModel);
        foreach (string member in new[] { "DecalLift", "LiftLegacyDecalsCommand", "NudgeLegacyImportCommand" })
            Assert.True(vm.GetMember(member, BindingFlags.Public | BindingFlags.Instance).Length > 0,
                $"MapInspectorView binds {member}, which MainWindowViewModel does not expose");

        // the lift is additive and undoable, like the nudge it sits beside
        string? host = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        if (host is null) return;
        Assert.Contains("map.TranslateMesh(mesh, mesh.Offset + new System.Numerics.Vector3(0f, lift, 0f));", host);
        Assert.Contains("new BatchTransformCommand(\"Lift Legacy Decals\", map, entries, MakeBatchRefresh(map));", host);
        Assert.Contains("LegacyMapPorter.ImportedDecalMeshes(map)", host);
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
