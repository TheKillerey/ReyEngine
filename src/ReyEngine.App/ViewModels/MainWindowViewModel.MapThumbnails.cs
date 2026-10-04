using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.App.Services;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.LtkGameData;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M807: the editor's side of the Content Browser's map thumbnails (<see cref="MapThumbnailService"/>) - a rendered picture of
/// what a <c>.mapgeo</c> will look like, in place of its glyph, props included.
///
/// <para>This file only describes a tile to the service. Everything that decides the picture - the start state, the camera,
/// the lighting, the props - is the service's renderer, which shares the viewport's own pipeline (<c>Dx11SceneBuilder</c>,
/// <c>PropMeshBuilder</c>, <c>D3D11MapProps</c>) rather than this view model. What this contributes is what only it owns: the
/// mounts a tile's assets are read through (identity for the cache key, a reader for the worker thread), the game folder, and
/// whether the editor is busy.</para>
///
/// <para><b>Readers.</b> A job reads through the mounts (or the archive) as they were when its tile was described, not through
/// whatever the view model holds when the read happens. A rebuild replaces them and hands the old ones to
/// <see cref="MapThumbnailService.Retire"/>, which disposes them after the tile in hand is done - so a project that saves often
/// (the file watcher rebuilds the mounts each time) does not start the same minute-long draw over forever, and a read never
/// lands on a half-built mount. What changes IN PLACE (a Riot WAD mounted as a fallback, a path taught to the hash dictionary)
/// moves <see cref="_mapThumbnailInputs"/>, and a draw that overlapped it is not kept.</para>
///
/// <para><b>Never written:</b> the project, <c>data/</c>, <c>settings.json</c>. The pictures live in the per-user cache
/// (<see cref="MapThumbnailCache.DefaultRoot"/>).</para>
/// </summary>
public sealed partial class MainWindowViewModel
{
    private MapThumbnailService? _mapThumbnails;

    /// <summary>Map loads and D3D11 scene builds in flight: a background thumbnail waits until both are done.</summary>
    private int _mapThumbnailBusy;

    /// <summary>Moves whenever something a thumbnail draw reads through is changed in place (see the class remarks).</summary>
    private long _mapThumbnailInputs;

    /// <summary>Changes whenever the mounts or the tree are rebuilt; the per-folder caches below belong to one epoch.</summary>
    private long _mapThumbnailEpoch;
    private (long Epoch, Dictionary<string, ulong[]> Siblings) _mapThumbnailSiblings = (-1, new());

    /// <summary>The service this view model draws its map tiles with. Created on first use, so an editor that never lists a
    /// map never starts a thread. Internal for the tests and probes.</summary>
    internal MapThumbnailService MapThumbnails => _mapThumbnails ??= new MapThumbnailService(
        new MapThumbnailHost(this),
        new MapThumbnailCache(MapThumbnailCache.DefaultRoot()),
        () => new MapThumbnailRenderer(),
        ApplyMapThumbnail);

    /// <summary>Is the Content Browser the selected tab of the bottom dock? (Content Browser | Console.) A hidden browser has no
    /// tile in view.</summary>
    public bool IsContentBrowserTabSelected => BottomDockTab != ConsoleTabIndex;

    /// <summary>The Content Browser reports the tiles in view (debounced by the grid; none while it is hidden).</summary>
    private void OnBrowserVisibleItems(IReadOnlyList<AssetNodeViewModel> visible)
    {
        MapThumbnails.SetVisible(visible);
        RefreshMapThumbnailCommand.NotifyCanExecuteChanged();   // the device may have failed, the renderer may have changed
    }

    /// <summary>The mounts or the tree were rebuilt: what the thumbnails queued was described against assets that may be gone.
    /// The tile in hand finishes against the readers it holds.</summary>
    private void InvalidateMapThumbnails()
    {
        _mapThumbnailEpoch++;
        _mapThumbnails?.CancelPending();
    }

    /// <summary>Different content is being opened (another WAD, another project): the tile in hand is stopped too.</summary>
    private void AbandonMapThumbnails()
    {
        _mapThumbnailEpoch++;
        _mapThumbnails?.Abandon();
    }

    /// <summary>Dispose a mount service or archive the editor is replacing - once the thumbnail in hand has stopped reading
    /// through it. Call before the replacement is published. With no service (no map was ever listed) it is just a dispose.</summary>
    private void RetireMapThumbnailReader(IDisposable? reader)
    {
        if (reader is null) return;
        // M812: a colour scan that took THIS reader on the UI thread is still reading through it on a worker; it waits for the scan
        // exactly as it waits for the tile in hand, and is handed to the thumbnail service (which waits for the tile) once the
        // last scan holding it lets go. Any other reader - the service a later rebuild replaces, say - is not the scan's business
        // and goes at once.
        lock (_readerLeaseGate)
        {
            if (_readerHolds.TryGetValue(reader, out var hold)) { hold.Retired = true; return; }
        }
        if (_mapThumbnails is { } service) service.Retire(reader);
        else reader.Dispose();
    }

    /// <summary>
    /// M812: the readers colour scans hold, and which of them the editor has since replaced. A scan takes a lease on the exact
    /// readers it will read through (the mount service and the archive as they were) on the UI thread, before its worker starts,
    /// and gives it back when the worker is done. A rebuild that retires one of THOSE parks it here instead of disposing it under
    /// the scan; two rebuilds during one long scan park the one reader the scan took, not the one in between.
    /// </summary>
    private readonly object _readerLeaseGate = new();
    private readonly Dictionary<IDisposable, ReaderHold> _readerHolds = new(ReferenceEqualityComparer.Instance);
    private int _activeReaderLeases;

    private sealed class ReaderHold
    {
        public int Leases;
        public bool Retired;
    }

    private IDisposable AcquireReaderLease(params IDisposable?[] readers)
    {
        var held = new List<IDisposable>();
        Interlocked.Increment(ref _activeReaderLeases);
        lock (_readerLeaseGate)
            foreach (var reader in readers)
            {
                if (reader is null || held.Any(h => ReferenceEquals(h, reader))) continue;
                if (!_readerHolds.TryGetValue(reader, out var hold)) _readerHolds[reader] = hold = new ReaderHold();
                hold.Leases++;
                held.Add(reader);
            }
        return new ReaderLease(this, held);
    }

    private void ReleaseReaderLease(IReadOnlyList<IDisposable> readers)
    {
        Interlocked.Decrement(ref _activeReaderLeases);
        List<IDisposable>? ready = null;
        lock (_readerLeaseGate)
            foreach (var reader in readers)
            {
                if (!_readerHolds.TryGetValue(reader, out var hold) || --hold.Leases > 0) continue;
                _readerHolds.Remove(reader);
                if (hold.Retired) (ready ??= new List<IDisposable>()).Add(reader);
            }
        if (ready is null) return;
        // Whatever the editor replaced while the scan read goes now - and goes quietly, each on its own: a reader that will not
        // close must neither fail a scan that has finished nor keep the readers after it open (MapThumbnailService does the same).
        foreach (var reader in ready)
            try { RetireMapThumbnailReader(reader); }
            catch { /* released with the process */ }
    }

    private sealed class ReaderLease(MainWindowViewModel owner, IReadOnlyList<IDisposable> readers) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) owner.ReleaseReaderLease(readers);
        }
    }

    /// <summary>For the tests: readers the editor has replaced that a scan is still reading through.</summary>
    internal int ReadersHeldForScans { get { lock (_readerLeaseGate) return _readerHolds.Values.Count(h => h.Retired); } }

    /// <summary>For the tests: scans (leases) in flight.</summary>
    internal int ReaderLeasesHeld => Volatile.Read(ref _activeReaderLeases);

    /// <summary>Something thumbnails read through is about to change, or has just changed, in place: a draw that overlaps the
    /// change read a half-changed state and is not kept. Call before AND after the change.</summary>
    private void NoteMapThumbnailInputsChanged() => Interlocked.Increment(ref _mapThumbnailInputs);

    /// <summary>The renderer or the game folder changed (View ▸ Use OpenGL renderer): tiles that could not be drawn are asked
    /// about again, and Refresh Thumbnail enables or disables itself.</summary>
    private void NotifyMapThumbnailAvailability()
    {
        RefreshMapThumbnailCommand.NotifyCanExecuteChanged();
        _mapThumbnails?.AvailabilityChanged();
    }

    /// <summary>The window is closing: stop the thumbnail thread and give back its device.</summary>
    public void ShutDownMapThumbnails()
    {
        _mapThumbnails?.Dispose();
        _mapThumbnails = null;
    }

    /// <summary>Game Depth was toggled: the draw differs, so every visible tile's key does. Each keeps its picture until the
    /// one for the new key is ready.</summary>
    private void ReevaluateMapThumbnails() => _mapThumbnails?.Reevaluate();

    /// <summary>For the view: a problem in the thumbnail wiring goes to the console, never out of a timer tick.</summary>
    internal void LogMapThumbnailProblem(string message) => _log.Warn("Thumbnails", message);

    private bool ApplyMapThumbnail(AssetNodeViewModel node, byte[] png)
    {
        try
        {
            node.Thumbnail = new Bitmap(new MemoryStream(png));
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn("Thumbnails", $"{node.Name}: the stored picture would not load ({ex.Message})");
            return false;
        }
    }

    /// <summary>Tile context menu, Refresh Thumbnail: draw the picture again - the tile keeps the one it has until the new one is
    /// written. Acts on the whole selection when the tile is part of one, as the other tile commands do. Does nothing, and is
    /// disabled, while no tile can be drawn (the OpenGL viewport is selected, no game folder, the device failed).</summary>
    [RelayCommand(CanExecute = nameof(CanRefreshMapThumbnail))]
    private void RefreshMapThumbnail(AssetNodeViewModel? node)
    {
        if (node is null) return;
        var nodes = ContentBrowser.SelectedItems.Contains(node) ? ContentBrowser.SelectedItems.ToList() : new List<AssetNodeViewModel> { node };
        var service = MapThumbnails;
        foreach (var n in nodes.Where(n => n.WantsMapThumbnail)) service.Refresh(n);
    }

    private bool CanRefreshMapThumbnail() => _mapThumbnails?.CanRefresh ?? new MapThumbnailHost(this).CanGenerate;

    // ---- the host the service asks

    private sealed class MapThumbnailHost(MainWindowViewModel vm) : IMapThumbnailHost
    {
        public bool CanGenerate =>
            // READ only: the setting belongs to the View menu. OpenGL builds no D3D11 device for a thumbnail.
            !vm.Settings.UseOpenGlViewport
            && GameReferenceLibrary.FindFinalDirectory(vm.Project.GameDirectory) is not null;

        public bool ShouldPause => Volatile.Read(ref vm._mapThumbnailBusy) > 0;

        public long InputsVersion => Interlocked.Read(ref vm._mapThumbnailInputs);

        public MapThumbnailTarget? Describe(AssetNodeViewModel node) => vm.DescribeMapThumbnail(node);

        public void Log(string message)
        {
            if (Dispatcher.UIThread.CheckAccess()) vm._log.Info("Thumbnails", message);
            else Dispatcher.UIThread.Post(() => vm._log.Info("Thumbnails", message));
        }
    }

    /// <summary>Reads that threw, for one job (not the ones that found nothing).</summary>
    private sealed class MapThumbnailReads
    {
        public int Faults;
    }

    /// <summary>The cache key and the job for one map tile (UI thread). Null when the tile cannot be drawn: it is not a
    /// resolved mapgeo, no game folder is set (there is no shader cache to draw with), or its materials bin is not there.</summary>
    private MapThumbnailTarget? DescribeMapThumbnail(AssetNodeViewModel node)
    {
        if (node.Entry is not { Type: AssetType.MapGeometry, IsResolved: true } entry) return null;
        string? final = GameReferenceLibrary.FindFinalDirectory(Project.GameDirectory);
        if (final is null) return null;
        // M819: while the mod's GameData is being prepared the bins a tile reads are the unchanged ones - no picture is better than a stale one, and the tree is rebuilt when it is ready
        if (_gameData is { IsPending: true }) return null;
        if (!TryResolveMaterialsBin(entry.Path, out var materials)) return null;

        string? shippingPath = MapThumbnailRenderer.ShippingBinPathFor(entry.Path);
        string mapIdentity = AssetIdentity(entry.PathHash), materialsIdentity = AssetIdentity(materials.PathHash);
        // an asset whose identity cannot be read cannot be told from its edited self: no picture is better than a stale one
        if (mapIdentity.Length == 0 || materialsIdentity.Length == 0) return null;
        string shippingIdentity = shippingPath is null ? "none" : AssetIdentity(HashAlgorithms.WadPath(shippingPath));
        string cacheIdentity = FileIdentityOrEmpty(Path.Combine(final, "ShaderCache.dx11.wad.client"));
        if (cacheIdentity.Length == 0) return null;

        // Game Depth (Dx11SceneBuilder.EmulateClientDepthRules) changes how transparents draw, so it is part of the state a
        // picture shows. Read ONCE: the key and the draw both use this value (the job carries it), so a toggle between the two
        // cannot file a picture under a key it was not drawn for. The thumbnail reads the flag and never writes it.
        bool clientDepth = Dx11SceneBuilder.EmulateClientDepthRules;
        string state = clientDepth ? MapThumbnailKey.StartState + "+clientdepth" : MapThumbnailKey.StartState;
        // M819: what the mod's GameData makes of the bins (the siblings and the props' skins too, which no identity above sees) is in the key when there is any, and only then: a project without it keeps every key it had
        string? gameDataIdentity = _gameData is { State: GameDataPreviewState.Ready, DocumentsFingerprint: { } documents } ? documents.ToString("x16") : null;
        string key = MapThumbnailKey.Compute(AppInfo.Version, state,
            entry.Path, mapIdentity, materials.Path, materialsIdentity, shippingIdentity.Length == 0 ? "missing" : shippingIdentity, cacheIdentity, gameDataIdentity);

        string dir = entry.Path[..(entry.Path.LastIndexOf('/') + 1)];
        // The readers THIS tile is drawn through: the mounts or the archive as they are now, kept alive for the job by
        // RetireMapThumbnailReader whatever the editor rebuilds meanwhile. A read that throws is a fault the job reports
        // (its picture is then not kept); a read that finds nothing is just a missing asset.
        var mounts = _mounts;
        var archive = _archive;
        var reads = new MapThumbnailReads();
        // sibling bins are only for drawing: a tile that can never be drawn (the OpenGL viewport) does not enumerate them
        bool drawable = !Settings.UseOpenGlViewport;
        var job = new MapThumbnailJob
        {
            Key = key,
            MapGeoPath = entry.Path,
            MapGeoHash = entry.PathHash,
            MaterialsBinPath = materials.Path,
            MaterialsBinHash = materials.PathHash,
            ControllerBins = drawable ? SiblingBinsOf(dir, materials.PathHash) : Array.Empty<ulong>(),
            FinalDirectory = final,
            Read = hash => ReadForThumbnail(mounts, archive, reads, hash),
            Has = hash => HasForThumbnail(mounts, archive, reads, hash),
            Hashes = _resolver.Database,
            BinName = ResolveBinName,
            WadPath = ResolveWadPath,
            ClientDepthRules = clientDepth,
            ReadFaults = () => Volatile.Read(ref reads.Faults),
        };
        return new MapThumbnailTarget(key, node.Name, job);
    }

    /// <summary>The editor's own asset read (<see cref="ReadAssetFrom"/>, so the project's overrides and loose files win exactly as
    /// they do everywhere else) through the readers a job was described against. Null when the asset is not there; null and a
    /// fault on the job when the read threw.</summary>
    private byte[]? ReadForThumbnail(AssetMountService? mounts, WadArchive? archive, MapThumbnailReads reads, ulong hash)
    {
        try
        {
            if (!HasIn(mounts, archive, hash)) return null;
            return ReadAssetFrom(mounts, archive, hash);
        }
        catch
        {
            Interlocked.Increment(ref reads.Faults);
            return null;
        }
    }

    private bool HasForThumbnail(AssetMountService? mounts, WadArchive? archive, MapThumbnailReads reads, ulong hash)
    {
        try { return HasIn(mounts, archive, hash); }
        catch
        {
            Interlocked.Increment(ref reads.Faults);
            return false;
        }
    }

    private bool HasIn(AssetMountService? mounts, WadArchive? archive, ulong hash)
    {
        if (mounts is not null) return mounts.Has(hash);
        if (archive is null) return false;
        return archive.TryGetEntry(hash, out _) || (_overrides.TryGet(hash, out var over) && File.Exists(over.OverrideFile));
    }

    /// <summary>The other bins in a mapgeo's folder, whose visibility controllers the map may name, by path order - read once per
    /// folder per mount epoch (enumerating every asset of a project is not something to do per tile).</summary>
    private IReadOnlyList<ulong> SiblingBinsOf(string dir, ulong exceptHash)
    {
        if (_mapThumbnailSiblings.Epoch != _mapThumbnailEpoch) _mapThumbnailSiblings = (_mapThumbnailEpoch, new Dictionary<string, ulong[]>(StringComparer.OrdinalIgnoreCase));
        if (!_mapThumbnailSiblings.Siblings.TryGetValue(dir, out var all))
        {
            all = AssetEntries
                .Where(e => e.IsResolved && e.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                            && e.Path.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
                .Select(e => e.PathHash)
                .ToArray();
            _mapThumbnailSiblings.Siblings[dir] = all;
        }
        return all.Where(h => h != exceptHash).ToArray();
    }

    /// <summary>A cheap fingerprint of an asset as the editor reads it: a file's length and write time, or a WAD chunk's sizes
    /// with the WAD's length and write time. Empty when it cannot be read - the asset is not mounted.</summary>
    private string AssetIdentity(ulong hash)
    {
        try
        {
            if (_mounts is not null)
            {
                if (!_mounts.TryGet(hash, out var asset)) return "";
                if (_mounts.OverlayIdentityOf(hash) is { } served) return served;   // M819: the bytes the GameData serves, by what they were made from
                if (asset.Source.TryGetFilePath(hash, out var file)) return FileIdentityOrEmpty(file);
                if (asset.Source is WadMount mount && mount.Archive.TryGetEntry(hash, out var chunk))
                    return WadChunkIdentity(mount.Archive.FilePath, chunk);
                return "";
            }
            if (_archive is not null && _archive.TryGetEntry(hash, out var entry))
            {
                if (_overrides.TryGet(hash, out var over) && File.Exists(over.OverrideFile)) return FileIdentityOrEmpty(over.OverrideFile);
                return WadChunkIdentity(_archive.FilePath, entry);
            }
        }
        catch { /* an identity that cannot be read is no identity */ }
        return "";
    }

    private static string FileIdentityOrEmpty(string file)
    {
        try
        {
            var info = new FileInfo(file);
            return info.Exists ? MapThumbnailKey.FileIdentity(info.Length, info.LastWriteTimeUtc.Ticks) : "";
        }
        catch { return ""; }
    }

    private static string WadChunkIdentity(string wadPath, WadAssetEntry chunk)
    {
        var info = new FileInfo(wadPath);
        return info.Exists
            ? MapThumbnailKey.WadChunkIdentity(info.Length, info.LastWriteTimeUtc.Ticks, chunk.CompressedSize, chunk.UncompressedSize)
            : "";
    }
}
