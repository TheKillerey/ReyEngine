using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meshes;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M654: making Add Mesh usable when the source is a mapgeo.
///
/// <para>A shipping mapgeo is not a model, it is a library. Measured over Map11's 27 mapgeos:
/// base_srx offers 601 import entries across 186 materials and banner_test 1,664 across 277. Every row
/// arrived ticked, there was no way to untick them and no way to search, so "Add To Map" meant "append
/// the whole source map" — and the MATERIALS card asked the user to configure 277 materials, one at a
/// time, for a selection of one rock.</para>
/// </summary>
public sealed class AddMeshSelectionTests
{
    private static ImportedSceneMesh Mesh(string name, string material) =>
        new(name, material,
            new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 },
            new float[9], new float[6], new[] { 0, 1, 2 });

    private static ImportedScene Scene(params (string Name, string Material)[] meshes)
    {
        var mats = meshes.Select(m => m.Material).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(m => new ImportedSceneMaterial(m, null)).ToArray();
        return new ImportedScene(meshes.Select(m => Mesh(m.Name, m.Material)).ToArray(), mats);
    }

    private static ImportedScene BigScene(int count)
    {
        var meshes = Enumerable.Range(0, count)
            .Select(i => (Name: $"MapGeo_Instance_{i}", Material: i % 2 == 0 ? "Rock_A_MAT" : "Grass_B_MAT"))
            .ToArray();
        return Scene(meshes);
    }

    private static AddMeshWindowViewModel Window() => new()
    {
        ShaderChoices = new[] { "Shaders/StaticMesh/DefaultEnv_Flat" },
        ExistingMaterials = new[] { "Existing_MAT" },
    };

    [Fact]
    public void ASmallImportStaysFullyTickedTheWayItAlwaysWas()
    {
        var vm = Window();
        vm.LoadScene(Scene(("Rock", "Rock_A_MAT"), ("Bush", "Grass_B_MAT")), "model.fbx", isMapGeo: false);

        Assert.Equal(2, vm.IncludedCount);
        Assert.All(vm.Meshes, m => Assert.True(m.Include));
        Assert.DoesNotContain("Nothing is selected", vm.Status);
    }

    [Fact]
    public void ALibrarySizedImportStartsWithNothingTicked()
    {
        var vm = Window();
        vm.LoadScene(BigScene(AddMeshWindowViewModel.AutoIncludeLimit + 1), "base_srx.mapgeo", isMapGeo: true);

        Assert.Equal(0, vm.IncludedCount);
        Assert.Contains("Nothing is selected", vm.Status);
        // ...and with nothing ticked there is nothing to configure, which is the whole point.
        Assert.Empty(vm.Materials);
    }

    [Fact]
    public void SearchMatchesTheMeshNameAndTheMaterialName()
    {
        var vm = Window();
        vm.LoadScene(Scene(("Rock_01", "Rock_A_MAT"), ("Bush_01", "Grass_B_MAT"), ("Rock_02", "Rock_A_MAT")),
            "x.mapgeo", isMapGeo: true);

        vm.MeshSearch = "bush";
        Assert.Single(vm.VisibleMeshes);
        Assert.Equal("Bush_01", vm.VisibleMeshes[0].Mesh.Name);

        // The material name is the useful half for a mapgeo: "everything drawn with this material".
        vm.MeshSearch = "rock_a_mat";
        Assert.Equal(2, vm.VisibleMeshes.Count);

        vm.MeshSearch = "";
        Assert.Equal(3, vm.VisibleMeshes.Count);
    }

    [Fact]
    public void SelectAllAppliesToWhatTheSearchIsShowingAndNothingElse()
    {
        var vm = Window();
        vm.LoadScene(BigScene(200), "base_srx.mapgeo", isMapGeo: true);
        Assert.Equal(0, vm.IncludedCount);

        vm.MeshSearch = "rock_a_mat";
        int shown = vm.VisibleMeshes.Count;
        Assert.Equal(100, shown);
        vm.SelectAllMeshesCommand.Execute(null);

        Assert.Equal(shown, vm.IncludedCount);
        Assert.All(vm.Meshes.Where(m => m.Mesh.MaterialName == "Grass_B_MAT"), m => Assert.False(m.Include));

        // Clearing the search must not un-do the selection - only the view changed.
        vm.MeshSearch = "";
        Assert.Equal(shown, vm.IncludedCount);

        vm.SelectNoMeshesCommand.Execute(null);
        Assert.Equal(0, vm.IncludedCount);
    }

    [Fact]
    public void OnlyTheMaterialsTheSelectionUsesGetACard()
    {
        var vm = Window();
        vm.LoadScene(Scene(("Rock", "Rock_A_MAT"), ("Bush", "Grass_B_MAT"), ("Tree", "Wood_C_MAT")),
            "x.mapgeo", isMapGeo: true);
        Assert.Equal(3, vm.Materials.Count);

        vm.Meshes.First(m => m.Mesh.Name == "Bush").Include = false;
        vm.Meshes.First(m => m.Mesh.Name == "Tree").Include = false;

        var only = Assert.Single(vm.Materials);
        Assert.Equal("Rock_A_MAT", only.Source.Name);
        Assert.Contains("1 of 3", vm.MaterialsSummary);
    }

    [Fact]
    public void ConfirmCarriesExactlyTheTickedMeshesAndTheirMaterials()
    {
        var vm = Window();
        vm.LoadScene(Scene(("Rock", "Rock_A_MAT"), ("Bush", "Grass_B_MAT")), "x.mapgeo", isMapGeo: true);
        vm.Meshes.First(m => m.Mesh.Name == "Bush").Include = false;

        AddMeshPlan? plan = null;
        vm.Confirmed = p => plan = p;
        vm.ConfirmCommand.Execute(null);

        Assert.NotNull(plan);
        Assert.Equal("Rock", Assert.Single(plan!.Meshes).Name);
        Assert.Equal("Rock_A_MAT", Assert.Single(plan.Materials).ImportedName);
        Assert.True(plan.MeshMaterialNames.ContainsKey("Rock_A_MAT"));
        Assert.False(plan.MeshMaterialNames.ContainsKey("Grass_B_MAT"));
    }

    // ---- the source materials bin ---------------------------------------------------------------

    private static string WriteBin(string directory, string fileName, params string[] materialNames)
    {
        var objects = materialNames.Select(n => new BinTreeObject(HashAlgorithms.Fnv1a(n),
            HashAlgorithms.Fnv1a("StaticMaterialDef"),
            new BinTreeProperty[] { new BinTreeString(HashAlgorithms.Fnv1a("name"), n) })).ToArray();
        var tree = new BinTree(objects, Array.Empty<string>());
        string path = Path.Combine(directory, fileName);
        using var stream = File.Create(path);
        tree.Write(stream);
        return path;
    }

    [Fact]
    public void WithNoBinTheOriginalMaterialSimplyCannotBeCopied()
    {
        var vm = Window();
        vm.LoadScene(Scene(("Rock", "Rock_A_MAT")), "x.mapgeo", isMapGeo: true);

        Assert.True(vm.ShowSourceBinRow);          // and the window says where to point it
        Assert.Contains("choose one", vm.SourceBinNote);
        var material = Assert.Single(vm.Materials);
        Assert.False(material.CanCopyFromSource);
        Assert.Equal(1, material.Mode);            // falls back to "build one from a shader"
    }

    [Fact]
    public void ChoosingTheRightBinTurnsCopyOnAndMakesItTheDefault()
    {
        string dir = Directory.CreateTempSubdirectory("reyengine_m654_").FullName;
        try
        {
            string bin = WriteBin(dir, "base_srx.materials.bin", "Rock_A_MAT", "Grass_B_MAT");
            var vm = Window();
            vm.LoadScene(Scene(("Rock", "Rock_A_MAT")), Path.Combine(dir, "base_srx.mapgeo"), isMapGeo: true);
            vm.UseSourceBin(bin);

            var material = Assert.Single(vm.Materials);
            Assert.True(material.CanCopyFromSource);
            Assert.Equal(2, material.Mode);        // copying the real thing beats rebuilding it
            Assert.Contains("1 of 1", vm.SourceBinNote);

            AddMeshPlan? plan = null;
            vm.Confirmed = p => plan = p;
            vm.ConfirmCommand.Execute(null);
            var planned = Assert.Single(plan!.Materials);
            Assert.Equal(bin, planned.CopyFromBin);
            Assert.Equal("Rock_A_MAT", planned.CopyFromMaterial);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>The trap this closes: until M654 "a file called .materials.bin sits next to the mapgeo"
    /// was taken as "the original material can be copied". Offering the option and then failing at
    /// confirm time is worse than not offering it.</summary>
    [Fact]
    public void ABinThatDoesNotHoldTheMaterialDoesNotOfferToCopyIt()
    {
        string dir = Directory.CreateTempSubdirectory("reyengine_m654_").FullName;
        try
        {
            string bin = WriteBin(dir, "someone_elses.materials.bin", "Something_Else_MAT");
            var vm = Window();
            vm.LoadScene(Scene(("Rock", "Rock_A_MAT")), Path.Combine(dir, "x.mapgeo"), isMapGeo: true);
            vm.UseSourceBin(bin);

            var material = Assert.Single(vm.Materials);
            Assert.False(material.CanCopyFromSource);
            Assert.Equal(1, material.Mode);
            Assert.Contains("none of this mapgeo", vm.SourceBinNote);
            Assert.Contains("Not in the chosen materials.bin", material.CopyNote);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AnUnreadableBinIsReportedRatherThanThrown()
    {
        string dir = Directory.CreateTempSubdirectory("reyengine_m654_").FullName;
        try
        {
            string bin = Path.Combine(dir, "broken.materials.bin");
            File.WriteAllBytes(bin, new byte[] { 1, 2, 3, 4 });
            var vm = Window();
            vm.LoadScene(Scene(("Rock", "Rock_A_MAT")), Path.Combine(dir, "x.mapgeo"), isMapGeo: true);
            vm.UseSourceBin(bin);

            Assert.Contains("could not be read", vm.SourceBinNote);
            Assert.False(Assert.Single(vm.Materials).CanCopyFromSource);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void SetAllToCopyOnlyTouchesTheMaterialsThatReallyAreInTheBin()
    {
        string dir = Directory.CreateTempSubdirectory("reyengine_m654_").FullName;
        try
        {
            string bin = WriteBin(dir, "half.materials.bin", "Rock_A_MAT");
            var vm = Window();
            vm.LoadScene(Scene(("Rock", "Rock_A_MAT"), ("Bush", "Grass_B_MAT")),
                Path.Combine(dir, "x.mapgeo"), isMapGeo: true);
            vm.UseSourceBin(bin);
            vm.ApplyModeToAllCommand.Execute("2");

            Assert.Equal(2, vm.Materials.First(m => m.Source.Name == "Rock_A_MAT").Mode);
            Assert.NotEqual(2, vm.Materials.First(m => m.Source.Name == "Grass_B_MAT").Mode);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
