using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Meshes;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M656: geometry on the Workshop shelf, the same way M655 put effects there.
///
/// <para>Same shape of answer throughout: entries are pointers at the user's own files, a file that can
/// hold hundreds is a library you pick from rather than a lump, and adding one routes into the Add Mesh
/// window instead of growing a second, quieter material path.</para>
/// </summary>
public sealed class WorkshopMeshShelfTests : IDisposable
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private readonly string _dir = Directory.CreateTempSubdirectory("reyengine_m656_").FullName;

    private string Store => Path.Combine(_dir, "shelf.json");
    private WorkshopUserLibrary Library() => new(Store);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>A one-triangle .obj — enough to exercise every shelf rule without the game installed.</summary>
    private string AnObjOnDisk(string name = "rock")
    {
        string path = Path.Combine(_dir, name + ".obj");
        File.WriteAllText(path, "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");
        return path;
    }

    /// <summary>A real shipping mapgeo, for the rules that only bite on a file with hundreds of meshes.</summary>
    private string? AMapGeoOnDisk()
    {
        string wad = Path.Combine(Final, "Maps", "Shipping", "Map11.wad.client");
        if (!File.Exists(wad)) return null;
        try
        {
            using var archive = WadArchive.Open(wad);
            ulong hash = HashAlgorithms.WadPath("data/maps/mapgeometry/sr/npe_1.mapgeo");
            if (!archive.TryGetEntry(hash, out _)) return null;
            string path = Path.Combine(_dir, "npe_1.mapgeo");
            File.WriteAllBytes(path, archive.Extract(hash));
            return path;
        }
        catch { return null; }
    }

    // ---- the loader both windows share -----------------------------------------------------------

    [Fact]
    public void OnePlaceDecidesHowAFileBecomesAScene()
    {
        // The point of SceneFileLoader: Add Mesh and the Workshop cannot disagree about which formats
        // work, because they no longer each carry their own extension switch.
        Assert.Contains(".mapgeo", SceneFileLoader.Extensions);
        Assert.True(SceneFileLoader.IsSupported("x.FBX"));
        Assert.False(SceneFileLoader.IsSupported("x.bin"));

        var loaded = SceneFileLoader.Load(AnObjOnDisk(), out string? error);
        Assert.Null(error);
        Assert.NotNull(loaded);
        Assert.False(loaded!.IsMapGeo);
        Assert.Null(loaded.SourceMaterialsBin);
        Assert.Single(loaded.Scene.Meshes);
    }

    [Fact]
    public void AnUnreadableFileReportsRatherThanThrows()
    {
        string junk = Path.Combine(_dir, "broken.fbx");
        File.WriteAllBytes(junk, new byte[] { 1, 2, 3, 4 });
        Assert.Null(SceneFileLoader.Load(junk, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));

        Assert.Null(SceneFileLoader.Load(Path.Combine(_dir, "gone.obj"), out string? missing));
        Assert.False(string.IsNullOrWhiteSpace(missing));
    }

    [Fact]
    public void AMapGeoBesideItsBinRemembersWhereTheOriginalMaterialLives()
    {
        string geo = Path.Combine(_dir, "fake.mapgeo");
        File.WriteAllBytes(geo, new byte[] { 1, 2, 3, 4 });          // does not decode - that is fine
        File.WriteAllBytes(Path.Combine(_dir, "fake.materials.bin"), new byte[] { 9 });
        Assert.Null(SceneFileLoader.Load(geo, out _));                // ...so nothing is returned

        if (AMapGeoOnDisk() is not { } real) return;                  // and on a real one it is found
        File.WriteAllBytes(Path.ChangeExtension(real, null) + ".materials.bin", new byte[] { 9 });
        var loaded = SceneFileLoader.Load(real, out _);
        Assert.NotNull(loaded);
        Assert.True(loaded!.IsMapGeo);
        Assert.EndsWith("npe_1.materials.bin", loaded.SourceMaterialsBin);
    }

    // ---- the shelf --------------------------------------------------------------------------------

    [Fact]
    public void AMeshGoesOnTheShelfAndSurvivesAReload()
    {
        string obj = AnObjOnDisk();
        var lib = Library();
        var found = lib.ScanMeshFile(obj, out string? failure);
        Assert.Null(failure);
        var mesh = Assert.Single(found);
        Assert.Equal("rock", mesh.MeshName);
        Assert.Equal(3, mesh.VertexCount);
        Assert.Equal(1, mesh.TriangleCount);
        Assert.Equal(obj, mesh.FilePath);

        Assert.Equal((1, 0), lib.AddMeshes(found));
        Assert.Equal(mesh.Id, Assert.Single(Library().Meshes).Id);

        // and shelving the same file again refreshes rather than duplicating
        Assert.Equal((0, 1), lib.AddMeshes(lib.ScanMeshFile(obj, out _)));
        Assert.Single(lib.Meshes);
    }

    [Fact]
    public void RemovingTakesItOffTheShelfAndLeavesTheFileAlone()
    {
        string obj = AnObjOnDisk();
        var lib = Library();
        var mesh = lib.ScanMeshFile(obj, out _)[0];
        lib.AddMeshes(new[] { mesh });

        Assert.True(lib.RemoveMesh(mesh.Id));
        Assert.Empty(lib.Meshes);
        Assert.Empty(Library().Meshes);
        Assert.True(File.Exists(obj));
        Assert.False(lib.RemoveMesh(mesh.Id));
    }

    /// <summary>The shelf file grew a second list in M656. An M655 shelf is a bare array of particles and
    /// must still load, or upgrading silently empties everyone's shelf.</summary>
    [Fact]
    public void AnM655ShelfIsStillRead()
    {
        File.WriteAllText(Store, """
        [
          {
            "Id": "abc:legacy", "FilePath": "C:/somewhere/effect.troybin", "DisplayName": "effect",
            "ParticlePath": "C:/somewhere/effect.troybin", "SystemHash": 7, "Emitters": 2,
            "VisualEmitters": 1, "PreviewTexturePath": null, "IsLegacy": true, "IsDecoded": true,
            "AddedUtc": "2026-01-01T00:00:00Z"
          }
        ]
        """);
        var lib = Library();
        Assert.Equal("abc:legacy", Assert.Single(lib.Entries).Id);
        Assert.Empty(lib.Meshes);

        // and once anything is written it comes back in the new shape, both lists intact
        lib.AddMeshes(lib.ScanMeshFile(AnObjOnDisk(), out _));
        var reopened = Library();
        Assert.Single(reopened.Entries);
        Assert.Single(reopened.Meshes);
    }

    [Fact]
    public void AMapGeoIsShelvedMeshByMeshWithItsOwnCounts()
    {
        if (AMapGeoOnDisk() is not { } geo) return;
        var found = Library().ScanMeshFile(geo, out string? failure);
        Assert.Null(failure);
        Assert.True(found.Count > 100, $"a shipping mapgeo should offer hundreds, got {found.Count}");
        Assert.Equal(found.Count, found.Select(m => m.Id).Distinct().Count());   // repeated names still differ
        Assert.All(found, m =>
        {
            Assert.True(m.VertexCount > 0);
            Assert.True(m.TriangleCount > 0);
            Assert.Equal(geo, m.FilePath);
        });
        // M654: each entry carries only its own geometry, so the counts are not all the parent's.
        Assert.True(found.Select(m => m.VertexCount).Distinct().Count() > 1);
    }

    // ---- Add Mesh opening on one shelved mesh -----------------------------------------------------

    private static AddMeshWindowViewModel Window() => new()
    {
        ShaderChoices = new[] { "Shaders/StaticMesh/DefaultEnv_Flat" },
        ExistingMaterials = new[] { "Existing_MAT" },
    };

    [Fact]
    public void OpeningOnAShelvedMeshTicksThatOneAndNothingElse()
    {
        if (AMapGeoOnDisk() is not { } geo) return;
        var shelf = Library().ScanMeshFile(geo, out _);
        var wanted = shelf[shelf.Count / 2];

        var vm = Window();
        Assert.True(vm.LoadFileAndSelectOnly(geo, new[] { wanted.MeshName }));
        Assert.True(vm.IncludedCount >= 1);
        Assert.All(vm.Meshes.Where(m => m.Include),
            m => Assert.Equal(wanted.MeshName, m.Mesh.Name, ignoreCase: true));
        // and only the material that mesh uses needs setting up
        Assert.Single(vm.Materials);
    }

    [Fact]
    public void AMeshThatIsNoLongerInTheFileSaysSoInsteadOfAddingSomethingElse()
    {
        var vm = Window();
        Assert.False(vm.LoadFileAndSelectOnly(AnObjOnDisk(), new[] { "a_mesh_that_was_renamed" }));
        Assert.Equal(0, vm.IncludedCount);
        Assert.Contains("no longer holds", vm.Status);
        Assert.True(vm.HasScene);          // the file is still open so the user can pick
    }

    // ---- the window's side of it ------------------------------------------------------------------

    private WorkshopViewModel Workshop(WorkshopUserLibrary lib) =>
        new(new WorkshopCatalogService(new HashDatabase(), _ => null), _dir) { UserLibrary = lib };

    [Fact]
    public async Task ImportingAFileAsksWhichMeshesBeforeShelvingAny()
    {
        string obj = AnObjOnDisk();
        var lib = Library();
        var vm = Workshop(lib);
        vm.PickMeshFile = () => Task.FromResult<string?>(obj);

        await vm.ImportMeshFileCommand.ExecuteAsync(null);
        Assert.True(vm.IsChoosingImports);
        Assert.Single(vm.PendingImports);
        Assert.Empty(lib.Meshes);                  // nothing shelved yet
        Assert.Equal(2, vm.SelectedTab);           // and the window moved to the Meshes tab

        vm.ConfirmPendingImportCommand.Execute(null);
        Assert.False(vm.IsChoosingImports);
        Assert.Single(lib.Meshes);
        Assert.Single(vm.Meshes);
        Assert.Equal("rock", vm.SelectedMesh!.Name);
    }

    [Fact]
    public async Task ALibrarySizedMeshFileStartsWithNothingTicked()
    {
        if (AMapGeoOnDisk() is not { } geo) return;
        var lib = Library();
        var vm = Workshop(lib);
        vm.PickMeshFile = () => Task.FromResult<string?>(geo);

        await vm.ImportMeshFileCommand.ExecuteAsync(null);
        Assert.True(vm.PendingImports.Count > AddMeshWindowViewModel.AutoIncludeLimit);
        Assert.Equal(0, vm.PendingIncluded);
        Assert.Contains("Nothing is ticked", vm.Status);

        vm.ConfirmPendingImportCommand.Execute(null);
        Assert.Empty(lib.Meshes);                  // confirming with nothing ticked shelves nothing
        Assert.Contains("Nothing ticked", vm.Status);

        vm.SelectAllPendingCommand.Execute(null);
        int wanted = vm.PendingImports.Count;
        vm.ConfirmPendingImportCommand.Execute(null);
        Assert.Equal(wanted, lib.Meshes.Count);
    }

    [Fact]
    public async Task AShelvedMeshWhoseFileHasGoneCannotBeAdded()
    {
        string obj = AnObjOnDisk();
        var lib = Library();
        var vm = Workshop(lib);
        vm.PickMeshFile = () => Task.FromResult<string?>(obj);
        vm.AddMesh = _ => Task.FromResult("added");

        await vm.ImportMeshFileCommand.ExecuteAsync(null);
        vm.ConfirmPendingImportCommand.Execute(null);
        Assert.True(vm.CanAddMesh);
        Assert.True(vm.CanDeleteMesh);

        File.Delete(obj);
        vm.SelectedMesh = null;
        vm.SelectedMesh = vm.Meshes[0];
        Assert.True(vm.SelectedMesh.IsMissingFile);
        Assert.False(vm.CanAddMesh);               // the shelf holds a pointer, not the geometry
        Assert.True(vm.CanDeleteMesh);             // but you can still take the dead row off
        Assert.Contains("FILE IS GONE", vm.SelectedMesh.OriginNote);
    }

    [Fact]
    public void WithNoShelfTheMeshTabOffersNothing()
    {
        var vm = new WorkshopViewModel(new WorkshopCatalogService(new HashDatabase(), _ => null), _dir);
        Assert.False(vm.CanImport);
        Assert.False(vm.CanAddMesh);
        Assert.False(vm.CanDeleteMesh);
        Assert.Empty(vm.Meshes);
    }
}
