using System.Collections.Concurrent;
using System.Reflection;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Build;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M812: the host half of the CHROMA card - the mounts a colour scan reads through.
///
/// <para>Three promises, all about files going missing quietly. The champion WAD the Character window has open is mounted AGAIN
/// when the editor rebuilds its mounts (a rebuild makes a new service, and the WAD went with the old one while a set still said
/// it was mounted: every later read of that champion missed - textures, sibling skins, the scan) - that one WAD, from the
/// install the project points at, and not a champion of another install ahead of the new install's own copy. A scan reads
/// through the readers it was started with: a rebuild, or a WAD added meanwhile, can neither hand it a half-built service nor
/// dispose the one it is reading - and only the readers it TOOK are held for it. And a scan that straddled a change in place is
/// not handed over as if it were whole.</para>
///
/// <para>These drive the real <see cref="MainWindowViewModel"/> through its private members, the way
/// <c>MapThumbnailTests</c> does, with real WAD files packed into a temp folder laid out like a game install
/// (<c>DATA/FINAL/Champions</c>). Nothing else is touched: no project, no setting, no Riot file.</para>
/// </summary>
public sealed class ChromaHostTests : IDisposable
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "reyengine-m812-" + Guid.NewGuid().ToString("N"));

    public ChromaHostTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static uint H(string s) => HashAlgorithms.Fnv1a(s);
    private static ulong Hash(string character, int number) => HashAlgorithms.WadPath($"data/characters/{character}/skins/skin{number}.bin");
    private static string BinPath(string character, int number = 0) => $"data/characters/{character}/skins/skin{number}.bin";

    private static T Field<T>(object o, string name) => (T)typeof(MainWindowViewModel).GetField(name, Private)!.GetValue(o)!;
    private static object? RawField(object o, string name) => typeof(MainWindowViewModel).GetField(name, Private)!.GetValue(o);
    private static void SetField(object o, string name, object? value) => typeof(MainWindowViewModel).GetField(name, Private)!.SetValue(o, value);
    private static object? Call(MainWindowViewModel vm, string name, params object?[] args) => typeof(MainWindowViewModel).GetMethod(name, Private)!.Invoke(vm, args);
    private static bool Open(MainWindowViewModel vm, string wad) => (bool)Call(vm, "MakeCharacterWadReadable", wad)!;
    private static IDisposable Lease(MainWindowViewModel vm, params IDisposable?[] readers) => (IDisposable)Call(vm, "AcquireReaderLease", (object)readers)!;
    private static void Retire(MainWindowViewModel vm, IDisposable reader) => Call(vm, "RetireMapThumbnailReader", reader);
    private static int HeldForScans(MainWindowViewModel vm) => (int)typeof(MainWindowViewModel).GetProperty("ReadersHeldForScans", Private)!.GetValue(vm)!;
    private static int LeasesHeld(MainWindowViewModel vm) => (int)typeof(MainWindowViewModel).GetProperty("ReaderLeasesHeld", Private)!.GetValue(vm)!;
    private static AssetMountService Mounts(MainWindowViewModel vm) => Field<AssetMountService>(vm, "_mounts");
    private static Predicate<WadMount> IsAt(string wad) => m => string.Equals(m.Location, wad, StringComparison.OrdinalIgnoreCase);

    /// <summary>A skin bin as small as the scan accepts: the skin object and its mesh block. <paramref name="marker"/> tells two
    /// installs' copies of the same file apart.</summary>
    private static byte[] SkinBin(string character, int number, string marker = "")
    {
        var skin = new BinTreeObject(H($"Characters/{character}/Skins/Skin{number}"), H("SkinCharacterDataProperties"), new BinTreeProperty[]
        {
            new BinTreeString(H("championSkinName"), character + marker),
            new BinTreeEmbedded(H("skinMeshProperties"), H("SkinMeshDataProperties"), new BinTreeProperty[]
            {
                new BinTreeString(H("simpleSkin"), $"assets/characters/{character}/skins/base/{character}.skn"),
                new BinTreeString(H("skeleton"), $"assets/characters/{character}/skins/base/{character}.skl"),
                new BinTreeString(H("texture"), $"assets/characters/{character}/skins/base/{character}_tx_cm.tex"),
            }),
        });
        using var ms = new MemoryStream();
        new BinTree(new[] { skin }, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    /// <summary>A folder laid out like a League install as far as the editor looks (DATA/FINAL with a DATA.wad.client); the game
    /// folder a project points at. Without Global.wad.client it is not "valid", so a rebuild opens no game WAD of it.</summary>
    private string FakeInstall(string name)
    {
        string final = Path.Combine(_dir, name, "DATA", "FINAL");
        Directory.CreateDirectory(Path.Combine(final, "Champions"));
        File.WriteAllBytes(Path.Combine(final, "DATA.wad.client"), Array.Empty<byte>());
        return Path.Combine(_dir, name);
    }

    /// <summary>A real .wad.client in an install's Champions folder holding one skin bin of a made-up champion, packed by the
    /// editor's own packer.</summary>
    private string MakeWad(string install, string character, string marker = "", int skins = 1)
    {
        string staging = Path.Combine(_dir, "staging", Path.GetFileName(install) + "_" + character);
        for (int n = 0; n < skins; n++)
        {
            string file = Path.Combine(staging, "data", "characters", character, "skins", $"skin{n}.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, SkinBin(character, n, marker));
        }
        string wad = Path.Combine(install, "DATA", "FINAL", "Champions", character + ".wad.client");
        var report = WadPackService.Pack(staging, wad);
        Assert.True(report.Success, "the test WAD did not pack: " + string.Join("; ", report.Warnings));
        return wad;
    }

    private static MainWindowViewModel EditorOn(string install, AssetMountService? mounts = null)
    {
        var vm = new MainWindowViewModel();
        vm.Project.GameDirectory = install;                  // in memory: nothing saves it
        SetField(vm, "_mounts", mounts ?? new AssetMountService());
        return vm;
    }

    /// <summary>The skin has finished loading into the Character window: its card is on.</summary>
    private static void WindowShows(MainWindowViewModel vm, string character) => vm.MeshPreview.SetChromaSkin(BinPath(character));

    // ===================================================================== a rebuild must not lose the champion the window has open

    [Fact]
    public void TheChampionTheWindowHasOpenIsStillReadableAfterARebuild_AndIsNotMountedTwice()
    {
        string install = FakeInstall("live");
        string wad = MakeWad(install, "zzchamp");
        var first = new AssetMountService();
        var vm = EditorOn(install, first);

        Assert.True(Open(vm, wad));
        WindowShows(vm, "zzchamp");
        Assert.NotNull(first.Read(Hash("zzchamp", 0)));

        Call(vm, "BuildMounts");                                       // any structural change to a project does this
        var second = Mounts(vm);
        Assert.NotSame(first, second);
        // the champion went with the service it was mounted on, and came back with the new one
        Assert.NotNull(second.Read(Hash("zzchamp", 0)));
        Assert.True(Open(vm, wad));                                    // the Character window opens another skin of her: no second mount
        Assert.Single(second.Fallback.OfType<WadMount>(), IsAt(wad));

        Call(vm, "BuildMounts");                                       // and again, and again: still exactly one
        Assert.Single(Mounts(vm).Fallback.OfType<WadMount>(), IsAt(wad));
        Assert.NotNull(Mounts(vm).Read(Hash("zzchamp", 0)));
    }

    [Fact]
    public void TheMountedAgainChampionIsResolved_BecauseTheNextOpenSkinTakesTheMeshEntryFromIt()
    {
        // OpenSkin finds the champion already mounted and reads the mesh's ENTRY from the mount; with an entry that is not resolved,
        // TryPairSkeleton, FindAnimations and BuildCharacterActions each return nothing and the skin would open bare. So the remount
        // is opened with the resolver, not like a game fallback.
        string install = FakeInstall("live");
        string wad = MakeWad(install, "zzchamp");
        var vm = EditorOn(install);
        var resolver = Field<WadPathResolver>(vm, "_resolver");
        resolver.Database.AddWad(Hash("zzchamp", 0), BinPath("zzchamp"));          // the dictionary knows this file's name
        Assert.True(Open(vm, wad));
        WindowShows(vm, "zzchamp");

        Call(vm, "BuildMounts");

        Assert.True(Mounts(vm).TryGet(Hash("zzchamp", 0), out var asset));
        Assert.True(asset.IsResolved);
        Assert.Equal(BinPath("zzchamp"), asset.VirtualPath);
        Assert.Equal(AssetType.Bin, asset.Type);
    }

    [Fact]
    public void OnlyTheChampionTheWindowHasOpenIsMountedAgain_TheOthersMountWhenTheirSkinIsOpened()
    {
        string install = FakeInstall("live");
        var wads = new[] { "zza", "zzb", "zzc" }.ToDictionary(c => c, c => MakeWad(install, c));
        var vm = EditorOn(install);
        foreach (var wad in wads.Values) Assert.True(Open(vm, wad));   // browsed in this order; the window ends on zzc
        WindowShows(vm, "zzc");

        Call(vm, "BuildMounts");
        var mounts = Mounts(vm);

        Assert.True(mounts.Has(Hash("zzc", 0)));                       // the one on screen keeps reading
        Assert.False(mounts.Has(Hash("zza", 0)));                      // the ones browsed before it do not cost the rebuild anything
        Assert.False(mounts.Has(Hash("zzb", 0)));
        Assert.Single(mounts.Fallback.OfType<WadMount>());             // one WAD opened on the UI thread, not one per champion looked at

        Assert.True(Open(vm, wads["zza"]));                            // and a skin of an earlier one still opens: it mounts then
        Assert.True(Mounts(vm).Has(Hash("zza", 0)));
    }

    [Fact]
    public void AChampionWhoseSkinIsStillLoadingIsMountedAgain_BecauseTheWindowsCardIsSetAtTheEndOfTheLoad()
    {
        string install = FakeInstall("live");
        string wad = MakeWad(install, "zzchamp");
        var vm = EditorOn(install);
        Assert.True(Open(vm, wad));                                    // OpenSkin has begun; the load has not reached the window yet
        Assert.False(vm.MeshPreview.HasChromaCard);

        Call(vm, "BuildMounts");

        Assert.True(Mounts(vm).Has(Hash("zzchamp", 0)));               // the load goes on reading the champion it started with
    }

    [Fact]
    public void AWindowThatMovedOnToSomethingElseDoesNotKeepItsChampionMounted()
    {
        string install = FakeInstall("live");
        string wad = MakeWad(install, "zzchamp");
        var vm = EditorOn(install);
        Assert.True(Open(vm, wad));
        // the load finished long ago and the window now shows something that is not a champion (no card)
        string final = GameReferenceLibrary.FindFinalDirectory(install)!;
        SetField(vm, "_openChampionWad", (wad, final, System.Environment.TickCount64 - 120_000));
        Assert.False(vm.MeshPreview.HasChromaCard);

        Call(vm, "BuildMounts");

        Assert.False(Mounts(vm).Has(Hash("zzchamp", 0)));
        Assert.Null(RawField(vm, "_openChampionWad"));                 // forgotten, not retried on every rebuild
        Assert.True(Open(vm, wad));                                    // it mounts when its skin is next opened
        Assert.True(Mounts(vm).Has(Hash("zzchamp", 0)));
    }

    [Fact]
    public void AChampionWadThatIsGoneIsDroppedNotRetried()
    {
        string install = FakeInstall("live");
        var vm = EditorOn(install);
        string final = GameReferenceLibrary.FindFinalDirectory(install)!;
        string gone = Path.Combine(final, "Champions", "gone.wad.client");
        SetField(vm, "_openChampionWad", (gone, final, System.Environment.TickCount64));
        WindowShows(vm, "gone");

        Call(vm, "BuildMounts");                                       // must not throw

        Assert.Empty(Mounts(vm).Fallback.OfType<WadMount>());
        Assert.Null(RawField(vm, "_openChampionWad"));
        Assert.False(Open(vm, gone));                                  // reported when its skin is opened
        Assert.Null(RawField(vm, "_openChampionWad"));
    }

    // ===================================================================== a project on another install does not get the old install's champion

    [Fact]
    public void AProjectOnAnotherGameInstallDoesNotGetTheOldInstallsChampionAheadOfItsOwn_AProjectOnTheSameInstallKeepsIt()
    {
        string live = FakeInstall("live"), pbe = FakeInstall("pbe");
        string liveWad = MakeWad(live, "zzchamp", "-live"), pbeWad = MakeWad(pbe, "zzchamp", "-pbe");
        var vm = EditorOn(live);
        Assert.True(Open(vm, liveWad));
        WindowShows(vm, "zzchamp");

        // another project on the SAME install (OpenProjectAt): the window is still showing her, and still reads her
        vm.Project = new ReyProject { Name = "Other", GameDirectory = live };
        Call(vm, "BuildMounts");
        Assert.Equal(SkinBin("zzchamp", 0, "-live"), Mounts(vm).Read(Hash("zzchamp", 0)));

        // a project whose game folder is the PBE install (OpenProjectAt again): the live file must not be served
        vm.Project = new ReyProject { Name = "Pbe", GameDirectory = pbe };
        Call(vm, "BuildMounts");
        Assert.False(Mounts(vm).Has(Hash("zzchamp", 0)));
        Assert.Empty(Mounts(vm).Fallback.OfType<WadMount>());
        Assert.Null(RawField(vm, "_openChampionWad"));

        // its own copy mounts when its skin is opened - and is the one that answers
        Assert.True(Open(vm, pbeWad));
        WindowShows(vm, "zzchamp");
        Assert.Equal(SkinBin("zzchamp", 0, "-pbe"), Mounts(vm).Read(Hash("zzchamp", 0)));
        Call(vm, "BuildMounts");                                       // and it is the PBE copy that comes back
        Assert.Equal(SkinBin("zzchamp", 0, "-pbe"), Mounts(vm).Read(Hash("zzchamp", 0)));

        // the same project pointed back at the live install (SetGameFolder, ApplyProjectSettings): the PBE copy goes, nothing replaces it
        vm.Project.GameDirectory = live;
        Call(vm, "BuildMounts");
        Assert.False(Mounts(vm).Has(Hash("zzchamp", 0)));
    }

    [Fact]
    public void AGameFolderThatNoLongerExistsLeavesNoChampionMounted()
    {
        string live = FakeInstall("live");
        string wad = MakeWad(live, "zzchamp");
        var vm = EditorOn(live);
        Assert.True(Open(vm, wad));
        WindowShows(vm, "zzchamp");

        vm.Project.GameDirectory = null;                               // the project's game folder was cleared
        Call(vm, "BuildMounts");

        Assert.False(Mounts(vm).Has(Hash("zzchamp", 0)));
        Assert.Null(RawField(vm, "_openChampionWad"));
    }

    // ===================================================================== a scan reads what it was started with

    // The scan's worker is a thread-pool task (ScanSkinColours: Task.Run), and a full suite on a busy machine starved the pool past 10 s
    // once ("the worker never started reading", the M815 full run) while the class alone passes; a pass returns as soon as it starts.
    private static readonly TimeSpan WorkerStart = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task AScanReadsThroughTheMountsItWasStartedWith_AndARebuildWaitsForItBeforeDisposingThem()
    {
        var gated = new GatedMount(new Dictionary<ulong, byte[]>
        {
            [Hash("zzchamp", 0)] = SkinBin("zzchamp", 0),
            [Hash("zzchamp", 1)] = SkinBin("zzchamp", 1),
        });
        var original = new AssetMountService();
        original.AddFallback(gated);
        var vm = new MainWindowViewModel();
        SetField(vm, "_mounts", original);

        // the hook is called on this thread, as the card calls it on the UI thread: the readers are taken before it returns
        var scan = (Task<SkinColorInventory>)Call(vm, "ScanSkinColours", BinPath("zzchamp"), CancellationToken.None)!;
        Assert.True(gated.Entered.Wait(WorkerStart), "the worker never started reading");
        Assert.Equal(1, LeasesHeld(vm));

        // the editor rebuilds its mounts while the scan is in the middle of its first read
        Call(vm, "BuildMounts");
        var rebuilt = Mounts(vm);
        Assert.NotSame(original, rebuilt);
        Assert.False(rebuilt.Has(Hash("zzchamp", 0)));                 // the live mounts know nothing of the champion any more
        Assert.False(gated.Disposed);                                  // the old ones were NOT disposed under the scan...
        Assert.Equal(1, HeldForScans(vm));                             // ...they are waiting for it
        Assert.False(scan.IsCompleted);

        gated.Gate.Set();
        var inventory = await scan.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, inventory.SkinNumber);
        Assert.Equal(new[] { "skin1" }, inventory.ComparedSkins);      // skin 1 came from the mounts the scan took: the live ones hold neither skin
        Assert.Empty(inventory.Warnings);
        Assert.Equal(0, HeldForScans(vm));                             // once it was done they were let go...
        Assert.Equal(0, LeasesHeld(vm));
        Assert.True(gated.Disposed);                                   // ...and disposed, exactly as before M812
    }

    [Fact]
    public async Task TwoRebuildsDuringOneScanParkOnlyTheReaderTheScanTook()
    {
        var gated = new GatedMount(new Dictionary<ulong, byte[]> { [Hash("zzchamp", 0)] = SkinBin("zzchamp", 0) });
        var original = new AssetMountService();
        original.AddFallback(gated);
        var vm = new MainWindowViewModel();
        SetField(vm, "_mounts", original);

        var scan = (Task<SkinColorInventory>)Call(vm, "ScanSkinColours", BinPath("zzchamp"), CancellationToken.None)!;
        Assert.True(gated.Entered.Wait(WorkerStart));

        Call(vm, "BuildMounts");                                       // first rebuild: the service the scan took is replaced
        var between = new FlagMount();
        Mounts(vm).AddFallback(between);                               // something only the in-between service holds
        Call(vm, "BuildMounts");                                       // second rebuild: the in-between service is replaced too

        Assert.True(between.Disposed);                                 // not the scan's reader: it went at once, as it always did
        Assert.False(gated.Disposed);                                  // the scan's reader is parked...
        Assert.Equal(1, HeldForScans(vm));                             // ...and it is the only one
        Assert.False(scan.IsCompleted);

        gated.Gate.Set();
        await scan.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(gated.Disposed);
        Assert.Equal(0, HeldForScans(vm));
    }

    [Fact]
    public void ALeaseProtectsOnlyTheReadersItTook_AndEachGoesWhenTheLastLeaseOnItLetsGo()
    {
        var vm = new MainWindowViewModel();
        var taken = new Flag();
        var archive = new Flag();
        var unrelated = new Flag();

        var first = Lease(vm, taken, archive);
        var second = Lease(vm, taken, null);                           // a second scan through the same service
        Assert.Equal(2, LeasesHeld(vm));

        Retire(vm, unrelated);                                         // nobody holds it: gone at once
        Assert.True(unrelated.Disposed);
        Retire(vm, taken);                                             // a scan holds it: parked
        Assert.False(taken.Disposed);
        Assert.Equal(1, HeldForScans(vm));

        first.Dispose();                                               // one scan done, one still reading
        Assert.False(taken.Disposed);
        Assert.Equal(1, HeldForScans(vm));
        second.Dispose();
        Assert.True(taken.Disposed);
        Assert.Equal(0, HeldForScans(vm));
        Assert.Equal(0, LeasesHeld(vm));
        Assert.False(archive.Disposed);                                // never retired: still the editor's

        first.Dispose();                                               // a lease given back twice gives back once
        Assert.Equal(0, LeasesHeld(vm));
    }

    [Fact]
    public void AReaderThatThrowsWhenItIsLetGoNeitherFailsTheFinishedScanNorKeepsTheOthersOpen()
    {
        var vm = new MainWindowViewModel();
        var thrower = new Flag(throws: true);
        var second = new Flag();
        var third = new Flag();
        var lease = Lease(vm, thrower, second, third);
        Retire(vm, thrower);
        Retire(vm, second);
        Retire(vm, third);
        Assert.False(second.Disposed);

        lease.Dispose();                                               // the scan has finished: this must not throw...

        Assert.True(thrower.DisposeCalled);
        Assert.True(second.Disposed);                                  // ...nor stop at the reader that would not close
        Assert.True(third.Disposed);
        Assert.Equal(0, HeldForScans(vm));
    }

    [Fact]
    public async Task AMountThatThrowsWhenTheScansServiceIsDisposedDoesNotFailTheScan()
    {
        var gated = new GatedMount(new Dictionary<ulong, byte[]> { [Hash("zzchamp", 0)] = SkinBin("zzchamp", 0) }) { ThrowsOnDispose = true };
        var after = new FlagMount();
        var original = new AssetMountService();
        original.AddFallback(gated);
        original.AddFallback(after);                                   // disposed AFTER the one that throws
        var vm = new MainWindowViewModel();
        SetField(vm, "_mounts", original);

        var scan = (Task<SkinColorInventory>)Call(vm, "ScanSkinColours", BinPath("zzchamp"), CancellationToken.None)!;
        Assert.True(gated.Entered.Wait(WorkerStart));
        Call(vm, "BuildMounts");
        gated.Gate.Set();

        var inventory = await scan.WaitAsync(TimeSpan.FromSeconds(30));  // the scan's result is not lost to a Dispose that threw

        Assert.Equal(0, inventory.SkinNumber);
        Assert.True(gated.DisposeCalled);
        Assert.True(after.Disposed);
    }

    [Fact]
    public async Task AScanCancelledBeforeItStartedLetsGoOfItsReaders()
    {
        var gated = new GatedMount(new Dictionary<ulong, byte[]> { [Hash("zzchamp", 0)] = SkinBin("zzchamp", 0) });
        var original = new AssetMountService();
        original.AddFallback(gated);
        var vm = new MainWindowViewModel();
        SetField(vm, "_mounts", original);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var scan = (Task<SkinColorInventory>)Call(vm, "ScanSkinColours", BinPath("zzchamp"), cts.Token)!;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);

        Assert.Equal(0, LeasesHeld(vm));                               // a lease taken for a worker that never ran is not leaked
        Call(vm, "BuildMounts");
        Assert.True(gated.Disposed);                                   // so nothing waits for the old mounts: they go at once
    }

    // ===================================================================== a scan that straddled a change in place is not trusted

    [Fact]
    public async Task AScanThatStraddledASyncOfTheHashesIsRunAgain_AndTheSecondRunIsTheResult()
    {
        var gated = new GatedMount(new Dictionary<ulong, byte[]>
        {
            [Hash("zzchamp", 0)] = SkinBin("zzchamp", 0),
            [Hash("zzchamp", 1)] = SkinBin("zzchamp", 1),
        });
        var original = new AssetMountService();
        original.AddFallback(gated);
        var vm = new MainWindowViewModel();
        SetField(vm, "_mounts", original);

        var scan = (Task<SkinColorInventory>)Call(vm, "ScanSkinColours", BinPath("zzchamp"), CancellationToken.None)!;
        Assert.True(gated.Entered.Wait(WorkerStart));

        long before = Field<long>(vm, "_mapThumbnailInputs");
        Call(vm, "ApplyHashesToOpenWad");                              // Sync Hashes re-resolves and rebuilds the LIVE mounts, mid-scan
        Assert.True(Field<long>(vm, "_mapThumbnailInputs") >= before + 2, "the change is not marked before and after");
        gated.Gate.Set();

        var inventory = await scan.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, gated.ReadsOf(Hash("zzchamp", 0)));            // the straddling run was not the answer: the skin was read again
        Assert.Equal(new[] { "skin1" }, inventory.ComparedSkins);
        Assert.Empty(inventory.Warnings);
        Assert.Equal(0, LeasesHeld(vm));
    }

    [Fact]
    public async Task AScanThatKeepsStraddlingChangesIsDiscardedWithAWarning_NotHandedOverShort()
    {
        var always = new GatedMount(new Dictionary<ulong, byte[]> { [Hash("zzchamp", 0)] = SkinBin("zzchamp", 0) });
        var original = new AssetMountService();
        original.AddFallback(always);
        var vm = new MainWindowViewModel();
        SetField(vm, "_mounts", original);
        always.Gate.Set();
        always.OnRead = hash => { if (hash == Hash("zzchamp", 0)) Call(vm, "NoteMapThumbnailInputsChanged"); };   // every run is overlapped

        var inventory = await ((Task<SkinColorInventory>)Call(vm, "ScanSkinColours", BinPath("zzchamp"), CancellationToken.None)!)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(3, always.ReadsOf(Hash("zzchamp", 0)));           // three tries, then it gave up
        Assert.Empty(inventory.BodyTextures);
        Assert.Empty(inventory.Effects);
        var warning = Assert.Single(inventory.Warnings);
        Assert.Contains("files changed while this scan ran", warning);
        Assert.Contains("discarded", warning);
        Assert.Equal(0, LeasesHeld(vm));
    }

    // ===================================================================== the mount service itself

    [Fact]
    public async Task AFallbackAddedWhileOtherThreadsReadIsSafe_BecauseTheListIsCopyOnWrite()
    {
        var service = new AssetMountService();
        service.AddFallback(new FixedMount(42));
        using var stop = new CancellationTokenSource();
        var errors = new ConcurrentQueue<Exception>();
        long rounds = 0;
        Task Reader() => Task.Run(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    Assert.True(service.Has(42));
                    Assert.NotNull(service.Read(42));
                    Assert.False(service.Has(7));                     // a miss walks every fallback: the loop an Add used to break
                    Assert.Null(service.Read(7));
                    Assert.False(service.TryGet(7, out _));
                    _ = service.Fallback.Count;
                    Interlocked.Increment(ref rounds);
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        });
        var readers = Enumerable.Range(0, 3).Select(_ => Reader()).ToArray();

        for (int i = 0; i < 3000; i++)
        {
            service.AddFallback(new FixedMount(100_000UL + (ulong)i));
            if (i % 100 == 0) await Task.Yield();
        }
        stop.Cancel();
        await Task.WhenAll(readers);

        Assert.Empty(errors);
        Assert.True(Interlocked.Read(ref rounds) > 0);
        Assert.Equal(3001, service.Fallback.Count);
        Assert.True(service.Has(100_000UL + 2999));
        service.Dispose();
        Assert.Empty(service.Fallback);
    }

    [Fact]
    public async Task AnIndexRebuiltWhileOtherThreadsReadNeverLosesAFileThatIsThere()
    {
        // Sync Hashes rebuilds the LIVE service while a scan or a thumbnail reads through it: the index is swapped whole, so a file
        // that is in every version of it is never absent from one that is being read.
        var service = new AssetMountService();
        service.Add(new ListingMount(held: 42, filler: 3000));
        service.Rebuild();
        using var stop = new CancellationTokenSource();
        var errors = new ConcurrentQueue<string>();
        long rounds = 0;
        Task Reader() => Task.Run(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    if (!service.Has(42)) errors.Enqueue("Has(42) was false");
                    if (service.Read(42) is null) errors.Enqueue("Read(42) was null");
                    if (!service.TryGet(42, out _)) errors.Enqueue("TryGet(42) was false");
                    if (service.Count < 3000) errors.Enqueue("Count was " + service.Count);
                    Interlocked.Increment(ref rounds);
                }
            }
            catch (Exception ex) { errors.Enqueue(ex.GetType().Name + ": " + ex.Message); }
        });
        var readers = Enumerable.Range(0, 3).Select(_ => Reader()).ToArray();

        for (int i = 0; i < 300; i++)
        {
            service.Rebuild();
            if (i % 20 == 0) await Task.Yield();
        }
        stop.Cancel();
        await Task.WhenAll(readers);

        Assert.Empty(errors.Distinct());
        Assert.True(Interlocked.Read(ref rounds) > 0);
        Assert.Equal(3001, service.Count);
    }

    [Fact]
    public void AMountThatThrowsWhenDisposedDoesNotKeepTheOthersOpen()
    {
        var first = new FlagMount();
        var thrower = new FlagMount(throws: true);
        var last = new FlagMount();
        var fallback = new FlagMount();
        var service = new AssetMountService();
        service.Add(first);
        service.Add(thrower);
        service.Add(last);
        service.AddFallback(fallback);

        service.Dispose();                                             // must not throw

        Assert.True(first.Disposed);
        Assert.True(thrower.DisposeCalled);
        Assert.True(last.Disposed);                                    // after the one that threw
        Assert.True(fallback.Disposed);
        Assert.Empty(service.Mounts);
        Assert.Empty(service.Fallback);
        Assert.Equal(0, service.Count);
    }

    // ===================================================================== stand-in mounts

    /// <summary>A mount whose FIRST read stops at a gate - a scan held in the middle of its reading - that notes every read and
    /// being disposed, and can call a hook on each read or throw when disposed.</summary>
    private sealed class GatedMount(Dictionary<ulong, byte[]> files) : IAssetMount
    {
        private readonly ConcurrentDictionary<ulong, int> _reads = new();
        private int _first;
        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim Gate { get; } = new(false);
        public volatile bool Disposed;
        public volatile bool DisposeCalled;
        public bool ThrowsOnDispose { get; init; }
        public Action<ulong>? OnRead { get; set; }
        public int ReadsOf(ulong hash) => _reads.GetValueOrDefault(hash);

        public string Name => "gated";
        public string Location => "gated";
        public AssetSourceKind Kind => AssetSourceKind.RiotReference;
        public bool IsEditable => false;
        public IEnumerable<MountedAsset> Enumerate() => Array.Empty<MountedAsset>();
        public bool Contains(ulong pathHash) => !Disposed ? files.ContainsKey(pathHash) : throw new ObjectDisposedException("gated");
        public MountedAsset? Get(ulong pathHash) => null;
        public byte[] Read(ulong pathHash)
        {
            if (Disposed) throw new ObjectDisposedException("gated");
            _reads.AddOrUpdate(pathHash, 1, (_, n) => n + 1);
            OnRead?.Invoke(pathHash);
            if (Interlocked.Increment(ref _first) == 1) { Entered.Set(); Gate.Wait(TimeSpan.FromSeconds(30)); }
            if (Disposed) throw new ObjectDisposedException("gated");        // a dispose that overtook the read
            return files[pathHash];
        }
        public bool TryGetFilePath(ulong pathHash, out string filePath) { filePath = ""; return false; }
        public void Dispose()
        {
            DisposeCalled = true;
            Disposed = true;
            if (ThrowsOnDispose) throw new InvalidOperationException("this mount will not close");
        }
    }

    private sealed class FlagMount(bool throws = false) : IAssetMount
    {
        public volatile bool Disposed;
        public volatile bool DisposeCalled;
        public string Name => "flag";
        public string Location => "flag";
        public AssetSourceKind Kind => AssetSourceKind.RiotReference;
        public bool IsEditable => false;
        public IEnumerable<MountedAsset> Enumerate() => Array.Empty<MountedAsset>();
        public bool Contains(ulong pathHash) => false;
        public MountedAsset? Get(ulong pathHash) => null;
        public byte[] Read(ulong pathHash) => throw new KeyNotFoundException();
        public bool TryGetFilePath(ulong pathHash, out string filePath) { filePath = ""; return false; }
        public void Dispose()
        {
            DisposeCalled = true;
            if (throws) throw new InvalidOperationException("this mount will not close");
            Disposed = true;
        }
    }

    /// <summary>A reader at the level the lease deals in: anything disposable.</summary>
    private sealed class Flag(bool throws = false) : IDisposable
    {
        public volatile bool Disposed;
        public volatile bool DisposeCalled;
        public void Dispose()
        {
            DisposeCalled = true;
            if (throws) throw new InvalidOperationException("this reader will not close");
            Disposed = true;
        }
    }

    private sealed class FixedMount(ulong held) : IAssetMount
    {
        public string Name => "fixed";
        public string Location => "fixed";
        public AssetSourceKind Kind => AssetSourceKind.RiotReference;
        public bool IsEditable => false;
        public IEnumerable<MountedAsset> Enumerate() => Array.Empty<MountedAsset>();
        public bool Contains(ulong pathHash) => pathHash == held;
        public MountedAsset? Get(ulong pathHash) => null;
        public byte[] Read(ulong pathHash) => pathHash == held ? new byte[] { 1 } : throw new KeyNotFoundException();
        public bool TryGetFilePath(ulong pathHash, out string filePath) { filePath = ""; return false; }
        public void Dispose() { }
    }

    /// <summary>A mount with a few thousand assets, one of which every enumeration lists first.</summary>
    private sealed class ListingMount(ulong held, int filler) : IAssetMount
    {
        public string Name => "listing";
        public string Location => "listing";
        public AssetSourceKind Kind => AssetSourceKind.ProjectWad;
        public bool IsEditable => false;
        public IEnumerable<MountedAsset> Enumerate()
        {
            yield return new MountedAsset { PathHash = held, VirtualPath = "held.bin", IsResolved = true, Source = this };
            for (int i = 0; i < filler; i++)
                yield return new MountedAsset { PathHash = 1_000_000UL + (ulong)i, VirtualPath = $"filler/{i}.bin", IsResolved = true, Source = this };
        }
        public bool Contains(ulong pathHash) => pathHash == held;
        public MountedAsset? Get(ulong pathHash) => null;
        public byte[] Read(ulong pathHash) => new byte[] { 7 };
        public bool TryGetFilePath(ulong pathHash, out string filePath) { filePath = ""; return false; }
        public void Dispose() { }
    }
}
