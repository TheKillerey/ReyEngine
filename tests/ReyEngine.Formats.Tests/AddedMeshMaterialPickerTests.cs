using System.Collections.ObjectModel;
using ReyEngine.App.ViewModels;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M516: the picker has to offer materials the BIN defines, not the ones the geometry already uses.
///
/// <para>It was built from the mapgeo's groups, so a material you had just created — by Add Mesh, by the
/// Workshop, by an import — was in the bin, referenced by nothing, and therefore missing from the only
/// list you could pick from. The mesh carried a name the picker could not offer and the inspector could
/// not show, which is what "I cannot apply any material to this mesh" looked like.</para>
///
/// <para>The rule under test is the SET the picker must produce. The main view model reads the bin for
/// real; here the two sources are supplied directly so the union rule can be pinned without a project.</para>
/// </summary>
public sealed class AddedMeshMaterialPickerTests
{
    /// <summary>The same union MainWindowViewModel.MapMaterialNames performs.</summary>
    private static IReadOnlyList<string> Offer(IEnumerable<string> definedByBin, IEnumerable<string> usedByGeometry)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string n in definedByBin) if (n.Length > 0) names.Add(n);
        foreach (string n in usedByGeometry) if (n.Length > 0) names.Add(n);
        return names.ToList();
    }

    [Fact]
    public void AMaterialTheBinDefinesButNothingUsesIsOffered()
    {
        // The whole point: a freshly created material has no geometry yet, and is exactly the one you are
        // trying to assign.
        var offered = Offer(
            definedByBin: new[] { "Rey_river", "LegacyPort/map1/Normal_1" },
            usedByGeometry: new[] { "LegacyPort/map1/Normal_1" });

        Assert.Contains("Rey_river", offered);
        Assert.Equal(2, offered.Count);
    }

    [Fact]
    public void AMaterialTheGeometryUsesAndTheBinLostIsStillOffered()
    {
        // Hiding it would hide the problem: that mesh draws untextured and the name is the only clue.
        var offered = Offer(
            definedByBin: new[] { "Rey_river" },
            usedByGeometry: new[] { "Rey_river", "GoneFromTheBin" });

        Assert.Contains("GoneFromTheBin", offered);
    }

    [Fact]
    public void TheListIsDedupedAndSorted()
    {
        var offered = Offer(
            definedByBin: new[] { "b_material", "a_material", "" },
            usedByGeometry: new[] { "A_MATERIAL", "c_material" });

        Assert.Equal(new[] { "a_material", "b_material", "c_material" }, offered);
    }

    [Fact]
    public void AMeshNamingSomethingTheMapDoesNotHaveIsDetectable()
    {
        // The inspector's red line hangs off exactly this test.
        var offered = Offer(new[] { "Rey_river" }, Array.Empty<string>());
        var mesh = new AddedMapMeshViewModel
        {
            Name = "imported",
            Positions = new float[3], Normals = new float[3], Uvs = new float[2],
            Indices = new[] { 0, 0, 0 }, LocalCenter = System.Numerics.Vector3.Zero,
            Material = "Rey_Maps_KitPieces_TestMaps_new_stone_road",
        };

        Assert.DoesNotContain(mesh.Material, offered, StringComparer.OrdinalIgnoreCase);

        mesh.Material = "Rey_river";
        Assert.Contains(mesh.Material, offered, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void AssigningIsJustSettingTheName()
    {
        // The mapgeo append reads AddedMapMeshViewModel.Material at save time, so the assignment is the
        // whole operation - which is why the missing half was the LIST, not the writing.
        var list = new ObservableCollection<AddedMapMeshViewModel>();
        var mesh = new AddedMapMeshViewModel
        {
            Name = "imported",
            Positions = new float[3], Normals = new float[3], Uvs = new float[2],
            Indices = new[] { 0, 0, 0 }, LocalCenter = System.Numerics.Vector3.Zero,
            Material = "",
        };
        list.Add(mesh);

        mesh.Material = "Rey_river";
        Assert.Equal("Rey_river", list[0].Material);
    }
}
