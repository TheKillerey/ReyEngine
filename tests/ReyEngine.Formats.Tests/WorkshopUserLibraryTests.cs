using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M655: the Workshop's own shelf.
///
/// <para>The catalogue beside it is a census of the installed game, rebuilt from nothing whenever a
/// patch changes the wads' fingerprint — so a user's own <c>.troybin</c>, or the effects inside a
/// <c>materials.bin</c> they are working on, cannot live in it. This is where they go: pointers at the
/// user's files, persisted separately, and deletable.</para>
/// </summary>
public sealed class WorkshopUserLibraryTests : IDisposable
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private readonly string _dir = Directory.CreateTempSubdirectory("reyengine_m655_").FullName;

    private string Store => Path.Combine(_dir, "shelf.json");
    private WorkshopUserLibrary Library() => new(Store);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>A real shipped .troybin written out to disk, so the scan runs on the format the user will
    /// actually point it at. Null when the game is not installed on this machine.</summary>
    private string? ATroyBinOnDisk()
    {
        string wad = Path.Combine(Final, "DATA.wad.client");
        if (!File.Exists(wad)) return null;
        HashDatabase database;
        try { database = new HashSyncService().LoadLocal(_ => { }); } catch { return null; }
        try
        {
            // WITH a resolver: without one every entry reports IsResolved = false and this would find
            // nothing while looking exactly like "the game is not installed".
            using var archive = WadArchive.Open(wad, new WadPathResolver(database));
            foreach (var entry in archive.Entries)
            {
                if (!entry.IsResolved || !entry.Path.EndsWith(".troybin", StringComparison.OrdinalIgnoreCase)) continue;
                string path = Path.Combine(_dir, Path.GetFileName(entry.Path));
                File.WriteAllBytes(path, archive.Extract(entry.PathHash));
                return path;
            }
        }
        catch { }
        return null;
    }

    /// <summary>A real shipped map materials.bin, standing in for the custom one a user would import.</summary>
    private string? ABinWithParticlesOnDisk()
    {
        string wad = Path.Combine(Final, "Maps", "Shipping", "Map11.wad.client");
        if (!File.Exists(wad)) return null;
        try
        {
            using var archive = WadArchive.Open(wad);
            ulong hash = HashAlgorithms.WadPath("data/maps/mapgeometry/map11/base_srx.materials.bin");
            if (!archive.TryGetEntry(hash, out _)) return null;
            string path = Path.Combine(_dir, "base_srx.materials.bin");
            File.WriteAllBytes(path, archive.Extract(hash));
            return path;
        }
        catch { return null; }
    }

    [Fact]
    public void AShelfStartsEmptyAndNeverTouchesTheRealOne()
    {
        var lib = Library();
        Assert.Empty(lib.Entries);
        Assert.Equal(Store, lib.StorePath);
        Assert.NotEqual(WorkshopUserLibrary.DefaultStorePath, lib.StorePath);
        Assert.False(File.Exists(Store));   // nothing is written until something is added
    }

    [Fact]
    public void ATroyBinBecomesALegacyEntryThatSurvivesAReload()
    {
        if (ATroyBinOnDisk() is not { } troy) return;   // no installed game
        var lib = Library();
        var found = lib.ScanTroyBins(new[] { troy }, out var failures);
        Assert.Empty(failures);
        var entry = Assert.Single(found);
        Assert.True(entry.IsLegacy);
        Assert.Equal(troy, entry.FilePath);
        Assert.True(entry.Emitters > 0);

        Assert.Equal((1, 0), lib.Add(found));
        Assert.True(File.Exists(Store));

        // a second library over the same store sees it - that is the whole point of a shelf
        var reopened = Library();
        Assert.Equal(entry.Id, Assert.Single(reopened.Entries).Id);
    }

    [Fact]
    public void ImportingTheSameFileTwiceRefreshesRatherThanDuplicating()
    {
        if (ATroyBinOnDisk() is not { } troy) return;
        var lib = Library();
        lib.Add(lib.ScanTroyBins(new[] { troy }, out _));
        var second = lib.Add(lib.ScanTroyBins(new[] { troy }, out _));

        Assert.Equal((0, 1), second);
        Assert.Single(lib.Entries);
    }

    [Fact]
    public void AnUnreadableFileIsNamedRatherThanSilentlySkipped()
    {
        string junk = Path.Combine(_dir, "not-a-troybin.troybin");
        File.WriteAllBytes(junk, new byte[] { 9, 9, 9, 9 });
        var lib = Library();

        Assert.Empty(lib.ScanTroyBins(new[] { junk, Path.Combine(_dir, "missing.troybin") }, out var failures));
        Assert.Equal(2, failures.Count);
        Assert.All(failures, f => Assert.Contains(".troybin", f));
    }

    [Fact]
    public void ACustomBinOffersEveryVfxSystemInIt()
    {
        if (ABinWithParticlesOnDisk() is not { } bin) return;
        var lib = Library();
        var found = lib.ScanBin(bin, out var failure);
        Assert.Null(failure);
        Assert.NotEmpty(found);
        Assert.All(found, e =>
        {
            Assert.False(e.IsLegacy);      // a modern bin graph, copied not converted
            Assert.Equal(bin, e.FilePath);
            Assert.NotEqual(0u, e.SystemHash);
        });
        // Each system is its own row, so the user picks - not "the file" as one lump.
        Assert.Equal(found.Count, found.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public void ABinWithNoParticlesSaysSoInsteadOfAddingNothingQuietly()
    {
        string junk = Path.Combine(_dir, "empty.bin");
        File.WriteAllBytes(junk, new byte[] { 1, 2, 3, 4 });
        Assert.Empty(Library().ScanBin(junk, out var failure));
        Assert.False(string.IsNullOrWhiteSpace(failure));
    }

    [Fact]
    public void RemovingTakesTheEntryOffTheShelfAndLeavesTheFileAlone()
    {
        if (ATroyBinOnDisk() is not { } troy) return;
        var lib = Library();
        var entry = lib.ScanTroyBins(new[] { troy }, out _)[0];
        lib.Add(new[] { entry });

        Assert.True(lib.Remove(entry.Id));
        Assert.Empty(lib.Entries);
        Assert.Empty(Library().Entries);          // and it stays gone
        Assert.True(File.Exists(troy));           // the user's file was never ours to delete
        Assert.False(lib.Remove(entry.Id));       // removing twice is not an error, just false
    }

    [Fact]
    public void ATemplateCarriesTheFileAsItsSourceAndIsMarkedAsTheUsers()
    {
        if (ATroyBinOnDisk() is not { } troy) return;
        var lib = Library();
        lib.Add(lib.ScanTroyBins(new[] { troy }, out _));

        var template = Assert.Single(lib.ToTemplates());
        Assert.True(template.IsUser);
        Assert.True(template.UserFileExists);
        Assert.Equal(troy, template.SourceBinPath);
        // SourceWad is the file itself - WorkshopCatalogService reads a plain file as its own bytes.
        Assert.Equal(troy, template.SourceWad);
        Assert.NotNull(template.UserEntryId);
    }

    [Fact]
    public void AnEntryWhoseFileHasGoneStaysListedAndSaysSo()
    {
        if (ATroyBinOnDisk() is not { } troy) return;
        var lib = Library();
        lib.Add(lib.ScanTroyBins(new[] { troy }, out _));
        File.Delete(troy);

        var template = Assert.Single(lib.ToTemplates());
        Assert.False(template.UserFileExists);
        // Dropping it would look like the Workshop lost it, which is a different and worse story.
        var row = new WorkshopParticleViewModel { Template = template };
        Assert.True(row.IsMissingFile);
        Assert.Contains("FILE IS GONE", row.OriginNote);
    }

    // ---- reading a source that is a file rather than an archive ---------------------------------

    /// <summary>
    /// The seam the whole feature stands on. Every import path asks the catalogue service for the
    /// template's source, and for one of the user's entries that source is a plain file on disk, not a
    /// chunk inside a wad. If this did not hold, a user entry would list and preview and then import
    /// nothing.
    /// </summary>
    [Fact]
    public void TheCatalogueReadsAUsersOwnFileAsItsOwnBytes()
    {
        if (ABinWithParticlesOnDisk() is not { } bin) return;
        var service = new WorkshopCatalogService(new HashDatabase(), _ => null);
        byte[] expected = File.ReadAllBytes(bin);

        // The hash is meaningless for a loose file and must not be consulted - pass a wrong one.
        Assert.Equal(expected, service.ReadBin(0xdeadbeefdeadbeef, bin));
        Assert.Equal(expected, service.ReadAsset(bin));

        var closure = service.ReadBinClosure(0xdeadbeefdeadbeef, bin);
        Assert.Equal(expected, Assert.Single(closure));   // no whole-install index here, so just the root
    }

    [Fact]
    public void AWadIsStillOpenedAsAWad()
    {
        // The rule is "a plain file IS the bytes" - a .wad.client is never that, however rooted its path.
        string wad = Path.Combine(Final, "Maps", "Shipping", "Map11.wad.client");
        if (!File.Exists(wad)) return;
        var service = new WorkshopCatalogService(new HashDatabase(), _ => null);
        byte[]? read = service.ReadBin(HashAlgorithms.WadPath("data/maps/mapgeometry/map11/base_srx.materials.bin"), wad);
        Assert.NotNull(read);
        Assert.NotEqual(new FileInfo(wad).Length, read!.Length);   // a chunk out of it, not the archive
    }

    // ---- the window's side of it ----------------------------------------------------------------

    private WorkshopViewModel Window(WorkshopUserLibrary? lib) =>
        new(new WorkshopCatalogService(new HashDatabase(), _ => null), _dir) { UserLibrary = lib };

    [Fact]
    public void WithNoShelfTheWorkshopIsExactlyWhatItWas()
    {
        var vm = Window(null);
        Assert.False(vm.CanImport);
        Assert.False(vm.CanDeleteParticle);
    }

    [Fact]
    public async Task ImportingFromABinAsksWhichSystemsBeforeAddingAny()
    {
        if (ABinWithParticlesOnDisk() is not { } bin) return;
        var lib = Library();
        var vm = Window(lib);
        vm.PickBin = () => Task.FromResult<string?>(bin);

        await vm.ImportBinCommand.ExecuteAsync(null);
        Assert.True(vm.IsChoosingImports);
        Assert.NotEmpty(vm.PendingImports);
        Assert.Empty(lib.Entries);                 // nothing has been added yet
        Assert.Equal(vm.PendingImports.Count, vm.PendingIncluded);

        vm.SelectNoPendingCommand.Execute(null);
        Assert.Equal(0, vm.PendingIncluded);
        vm.ConfirmPendingImportCommand.Execute(null);
        Assert.Empty(lib.Entries);
        Assert.Contains("Nothing ticked", vm.Status);

        vm.SelectAllPendingCommand.Execute(null);
        int wanted = vm.PendingImports.Count;
        vm.ConfirmPendingImportCommand.Execute(null);
        Assert.False(vm.IsChoosingImports);
        Assert.Equal(wanted, lib.Entries.Count);
        Assert.Equal(wanted, vm.Particles.Count(p => p.IsUser));
    }

    [Fact]
    public async Task OnlyTheUsersOwnRowsCanBeRemoved()
    {
        if (ATroyBinOnDisk() is not { } troy) return;
        var lib = Library();
        var vm = Window(lib);
        vm.PickTroyBins = () => Task.FromResult<IReadOnlyList<string>>(new[] { troy });

        await vm.ImportTroyBinsCommand.ExecuteAsync(null);
        var mine = Assert.Single(vm.Particles);
        Assert.True(mine.IsUser);

        vm.SelectedParticle = mine;
        Assert.True(vm.CanDeleteParticle);
        vm.DeleteSelectedParticleCommand.Execute(null);
        Assert.Empty(lib.Entries);
        Assert.Empty(vm.Particles);

        // A shipped template is a fact about the installed game; removing it from the list would only
        // make the Workshop lie about what is there.
        vm.SelectedParticle = new WorkshopParticleViewModel
        {
            Template = new WorkshopParticleTemplate(1, "Shipped", "particles/x", 2, "x.bin", "y.wad.client", 1, 1, null),
        };
        Assert.False(vm.CanDeleteParticle);
    }
}
