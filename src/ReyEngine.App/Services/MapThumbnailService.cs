using System.Runtime.CompilerServices;
using ReyEngine.App.ViewModels;

namespace ReyEngine.App.Services;

/// <summary>M807: what the service asks of the editor: whether a thumbnail may be drawn now, and everything one needs.</summary>
public interface IMapThumbnailHost
{
    /// <summary>False while generating is not allowed at all - the OpenGL viewport is selected (no D3D11 device is created
    /// for it), or no game folder is set. Pictures already on disk are still shown.</summary>
    bool CanGenerate { get; }

    /// <summary>True while the editor is busy with something a background thumbnail must not compete with: a map loading, or
    /// the D3D11 viewport building its scene. Read from the worker thread - between jobs, and by the job itself at every stage
    /// boundary and before every texture decode, where it WAITS (it is not cancelled) until this clears.</summary>
    bool ShouldPause { get; }

    /// <summary>A counter that moves whenever something a draw reads through is changed IN PLACE under it - a Riot WAD mounted as
    /// a fallback, a path taught to the hash dictionary - as opposed to a rebuild, which leaves the readers a running draw holds
    /// intact. A draw during which it moved read through a half-changed state, so its picture is not kept. Read from the worker.</summary>
    long InputsVersion { get; }

    /// <summary>The cache key and the job for a map tile, or null when it cannot be drawn (no materials bin, no game
    /// folder, not a mapgeo). Called on the UI thread, which is where the mounts it reads are owned.</summary>
    MapThumbnailTarget? Describe(AssetNodeViewModel node);

    /// <summary>A line for the console. Any thread; the host puts it on the UI thread.</summary>
    void Log(string message);
}

/// <summary>M807: one tile's cache key and the job that would draw it.</summary>
public sealed record MapThumbnailTarget(string Key, string Name, MapThumbnailJob Job);

/// <summary>
/// <para>M807: rendered preview pictures for the <c>.mapgeo</c> tiles of the Content Browser - what the map will look like -
/// drawn in the background and cached.</para>
///
/// <para><b>For the UI thread:</b> <see cref="SetVisible"/> is told which tiles are on screen (the grid reports its scroll
/// window, debounced; an empty list when the browser is hidden); only those are drawn, nearest the top first, and a queued tile
/// that has scrolled away is dropped. <see cref="Refresh"/> draws one tile again, keeping its picture until the new one is
/// there. <see cref="CancelPending"/> is for a rebuilt tree or mount, which makes every assumption about what was queued stale:
/// the tile being drawn is NOT stopped (it reads through the readers it was described against, which are kept alive for it -
/// <see cref="Retire"/>), and its picture is kept by its key. <see cref="Abandon"/> is for different content altogether (another
/// WAD or project), which stops it. A picture arrives as PNG bytes through the <c>apply</c> callback, on the UI thread.</para>
///
/// <para><b>The work</b> is done by one dedicated, below-normal-priority thread, one tile at a time, through an
/// <see cref="IMapThumbnailRenderer"/> that is created on that thread and released after
/// <see cref="IdleRelease"/> without work (a Summoner's Rift of vertex and texture memory is not held while the editor sits
/// idle). It waits while the editor is loading a map or building its scene - before it starts a tile and at every stage of one -
/// so it never competes with either. A tile's picture is looked for in memory (an LRU of the PNG bytes), then on disk
/// (<see cref="MapThumbnailCache"/>, read on the thread pool, never the UI thread and never behind a long render), and only then
/// drawn. A map that cannot be drawn is remembered, on disk when the reason is a fact about the map and for the session
/// otherwise, and its tile keeps the glyph. If the D3D11 device cannot be created, drawing is off for the session and the
/// reason is logged once.</para>
///
/// <para><b>What is cached.</b> Only a picture drawn while nothing it reads through changed in place
/// (<see cref="IMapThumbnailHost.InputsVersion"/>) and without a read that threw: such a draw is drawn again, up to
/// <see cref="MaxAttempts"/> times, rather than filed as good or as a failure. A picture that will not decode is evicted and
/// deleted and the tile is drawn again once.</para>
/// </summary>
public sealed class MapThumbnailService : IDisposable
{
    /// <summary>How many times a tile is drawn before a draw that keeps being disturbed is given up on.</summary>
    public const int MaxAttempts = 3;

    private sealed class Pending
    {
        public required AssetNodeViewModel Node { get; init; }
        public required MapThumbnailTarget Target { get; init; }
        public required long Generation { get; init; }
        /// <summary>Drawn on request (Refresh Thumbnail), not because the tile came into view: the caches are not asked first.</summary>
        public bool Forced { get; init; }
        /// <summary>The tile already shows a picture, which stays until the new one is written and stays if the draw fails.</summary>
        public bool KeepsPicture { get; init; }
        public int Attempt { get; init; }
        /// <summary>The content this was described against is gone for good: stop at the next stage and keep nothing.</summary>
        public volatile bool Abandoned;
        /// <summary><see cref="IMapThumbnailHost.InputsVersion"/> when the draw began (worker thread).</summary>
        public long InputsVersion;
    }

    private readonly IMapThumbnailHost _host;
    private readonly MapThumbnailCache _cache;
    private readonly Func<IMapThumbnailRenderer> _rendererFactory;
    private readonly Func<AssetNodeViewModel, byte[], bool> _apply;
    private readonly Action<Action> _post;
    private readonly int _lruCapacity;

    /// <summary>How long the worker may sit without work before it gives the device back.</summary>
    public TimeSpan IdleRelease { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>How often a worker holding a queue it must not start (the editor is busy) asks again.</summary>
    public TimeSpan PauseRecheck { get; init; } = TimeSpan.FromMilliseconds(500);

    // ---- UI-thread state
    private readonly Dictionary<AssetNodeViewModel, Pending> _tracked = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> _failedThisSession = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, byte[]>>> _lru = new(StringComparer.Ordinal);
    private readonly LinkedList<KeyValuePair<string, byte[]>> _lruOrder = new();
    /// <summary>Tiles known to have no picture to wait for (a remembered failure, or nothing to describe): not asked about again
    /// on every scroll - describing a tile that cannot be drawn is the expensive case. Cleared when the tree or the mounts are
    /// rebuilt and when the renderer or the game folder changes.</summary>
    private readonly HashSet<AssetNodeViewModel> _unavailable = new(ReferenceEqualityComparer.Instance);
    /// <summary>Visible tiles that show a picture but whose key must be looked at again (Game Depth was toggled).</summary>
    private readonly HashSet<AssetNodeViewModel> _stale = new(ReferenceEqualityComparer.Instance);
    /// <summary>The key of the picture each tile shows now.</summary>
    private readonly ConditionalWeakTable<AssetNodeViewModel, string> _shownKey = new();
    /// <summary>Keys whose cached picture would not decode and were drawn again once: a second failure ends it.</summary>
    private readonly HashSet<string> _healed = new(StringComparer.Ordinal);
    /// <summary>Where each wanted tile stands in the grid's order, as of the last <see cref="SetVisible"/>: a queued tile's
    /// turn. Late arrivals (a disk look that finishes after a newer window was reported) are queued by it too.</summary>
    private Dictionary<AssetNodeViewModel, int> _order = new(ReferenceEqualityComparer.Instance);
    private IReadOnlyList<AssetNodeViewModel> _lastVisible = Array.Empty<AssetNodeViewModel>();
    private bool? _lastCanGenerate;
    private bool _disabled;
    private volatile bool _disposed;
    private bool _pruned;

    // ---- shared with the worker, under _gate
    private readonly object _gate = new();
    private readonly List<Pending> _queue = new();
    private readonly List<IDisposable> _retired = new();
    private Pending? _active;
    private bool _stop;
    private Thread? _thread;
    private long _generation;

    // ---- counters (for the console and the probes)
    private int _rendered, _fromDisk, _fromMemory, _failed;
    public int Rendered => Volatile.Read(ref _rendered);
    public int FromDisk => Volatile.Read(ref _fromDisk);
    public int FromMemory => Volatile.Read(ref _fromMemory);
    public int Failed => Volatile.Read(ref _failed);
    /// <summary>Tiles waiting for the worker, plus the one it is drawing.</summary>
    public int Outstanding { get { lock (_gate) return _queue.Count + (_active is null ? 0 : 1); } }
    /// <summary>Drawing was switched off for the session (the device could not be created).</summary>
    public bool Disabled => _disabled;
    /// <summary>Can a tile be drawn again now? False for the OpenGL viewport, with no game folder, and after the device failed:
    /// Refresh Thumbnail then does nothing, and its menu item can say so.</summary>
    public bool CanRefresh => !_disposed && !_disabled && _host.CanGenerate;
    public MapThumbnailCache Cache => _cache;

    /// <param name="apply">Shows a picture on a tile (UI thread). False when the bytes would not decode.</param>
    public MapThumbnailService(IMapThumbnailHost host, MapThumbnailCache cache, Func<IMapThumbnailRenderer> rendererFactory,
        Func<AssetNodeViewModel, byte[], bool> apply, Action<Action>? post = null, int memoryCapacity = 256)
    {
        _host = host;
        _cache = cache;
        _rendererFactory = rendererFactory;
        _apply = apply;
        _post = post ?? (a => Avalonia.Threading.Dispatcher.UIThread.Post(a, Avalonia.Threading.DispatcherPriority.Background));
        _lruCapacity = Math.Max(1, memoryCapacity);
    }

    // ------------------------------------------------------------------------------------------------ UI-facing

    /// <summary>The tiles on screen now (empty for the list view or a hidden grid). Draws the map tiles among them that have
    /// no picture yet, in order; drops queued ones that are no longer wanted. Call on the UI thread.</summary>
    public void SetVisible(IReadOnlyList<AssetNodeViewModel> visible)
    {
        if (_disposed) return;
        _lastVisible = visible;
        // the renderer or the game folder changed: what could not be drawn before may be drawable now
        bool can = _host.CanGenerate;
        if (_lastCanGenerate != can) { _lastCanGenerate = can; _unavailable.Clear(); }

        var want = new List<AssetNodeViewModel>();
        var wanted = new HashSet<AssetNodeViewModel>(ReferenceEqualityComparer.Instance);
        foreach (var n in visible)
            if (n.WantsMapThumbnail && (n.Thumbnail is null || _stale.Contains(n)) && !_unavailable.Contains(n) && wanted.Add(n)) want.Add(n);

        lock (_gate)
        {
            // a queued tile that scrolled away is not drawn (one already being drawn finishes: its picture is cached either way)
            for (int i = _queue.Count - 1; i >= 0; i--)
            {
                var q = _queue[i];
                if (q.Forced || wanted.Contains(q.Node)) continue;
                _tracked.Remove(q.Node);
                _queue.RemoveAt(i);
            }
        }

        // nearest the top first: the order the grid lists them in. Set BEFORE the tiles are resolved - a picture found in
        // memory or on disk is shown at once, but one that needs a draw is queued by this
        var order = new Dictionary<AssetNodeViewModel, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < want.Count; i++) order[want[i]] = i;
        _order = order;

        foreach (var n in want)
            if (!_tracked.ContainsKey(n)) Resolve(n, forced: false, known: null, keepsPicture: false, attempt: 0);

        lock (_gate)
        {
            var ranked = _queue.Select((p, at) => (p, at, rank: Rank(p))).OrderBy(x => x.rank).ThenBy(x => x.at).Select(x => x.p).ToList();
            _queue.Clear();
            _queue.AddRange(ranked);
        }
    }

    /// <summary>A queued tile's turn: a refresh asked for by hand first, then the grid's own order; a tile no longer in the
    /// window last.</summary>
    private int Rank(Pending p) => p.Forced ? -1 : _order.TryGetValue(p.Node, out int i) ? i : int.MaxValue;

    /// <summary>Draw one tile again - for a texture or prop edited under the map, which the cache key does not see. Works whether
    /// or not the tile is on screen. The picture the tile shows stays until the new one has been written, and stays if the draw
    /// fails; nothing is deleted first. False, and nothing at all done, when no tile can be drawn now
    /// (<see cref="CanRefresh"/>).</summary>
    public bool Refresh(AssetNodeViewModel node)
    {
        if (!CanRefresh || !node.WantsMapThumbnail) return false;
        var target = _host.Describe(node);
        if (target is null) return false;

        _failedThisSession.Remove(target.Key);
        _unavailable.Remove(node);
        _stale.Remove(node);
        lock (_gate)
        {
            for (int i = _queue.Count - 1; i >= 0; i--)
                if (ReferenceEquals(_queue[i].Node, node)) _queue.RemoveAt(i);
        }
        _tracked.Remove(node);
        Resolve(node, forced: true, target, keepsPicture: node.Thumbnail is not null, attempt: 0);
        return true;
    }

    /// <summary>The tree or the mounts were rebuilt: everything queued was described against assets that may no longer exist, and
    /// the tiles are new objects. Drops the queue and forgets what was tracked. The tile being drawn is NOT stopped: it reads
    /// through the readers it was described against (kept alive for it, <see cref="Retire"/>), so its picture is as true as the
    /// key it is cached under - and the new tile for the same map finds it there instead of starting the draw over, which a
    /// project that saves often would otherwise do forever. The memory LRU stays - its keys carry the assets' identity, so a hit
    /// is still a true picture. Call on the UI thread.</summary>
    public void CancelPending()
    {
        lock (_gate)
        {
            _generation++;
            _queue.Clear();
        }
        _tracked.Clear();
        _unavailable.Clear();
        _stale.Clear();
    }

    /// <summary>Different content is open (another WAD, another project): everything of <see cref="CancelPending"/>, and the tile
    /// being drawn is stopped at its next stage and its picture thrown away - it is a picture of something no longer open.</summary>
    public void Abandon()
    {
        lock (_gate)
        {
            _generation++;
            _queue.Clear();
            if (_active is { } active) active.Abandoned = true;
        }
        _tracked.Clear();
        _unavailable.Clear();
        _stale.Clear();
    }

    /// <summary>What the pictures are drawn with changed (Game Depth): every visible tile's key is different now. Whatever was
    /// queued was described with the old state and goes; each visible tile that shows a picture is looked at again, and keeps its
    /// picture until the one for the new key is ready. Call on the UI thread.</summary>
    public void Reevaluate()
    {
        if (_disposed) return;
        CancelPending();
        foreach (var n in _lastVisible)
            if (n.WantsMapThumbnail && n.Thumbnail is not null) _stale.Add(n);
        SetVisible(_lastVisible);
    }

    /// <summary>The renderer or the game folder changed (OpenGL on or off): tiles that could not be drawn are asked about again.</summary>
    public void AvailabilityChanged()
    {
        if (_disposed) return;
        _unavailable.Clear();
        SetVisible(_lastVisible);
    }

    /// <summary>The editor is replacing something a draw may be reading through (the mounts, the archive): dispose it - but not
    /// while the tile in hand is still reading through it. With no tile in hand it is disposed at once, on the calling thread.
    /// Otherwise it goes on the worker, the moment that tile is done. Call BEFORE the replacement is published.</summary>
    public void Retire(IDisposable? resource)
    {
        if (resource is null) return;
        bool now;
        lock (_gate)
        {
            now = _active is null || _disposed;
            if (!now) _retired.Add(resource);
        }
        if (now) DisposeQuietly(resource);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Thread? thread;
        lock (_gate)
        {
            _stop = true;
            _generation++;
            _queue.Clear();
            if (_active is { } active) active.Abandoned = true;
            thread = _thread;
            Monitor.PulseAll(_gate);
        }
        _tracked.Clear();
        _lru.Clear();
        _lruOrder.Clear();
        // The worker is a background thread and the job in hand stops at its next stage (it is abandoned), so closing the
        // window does not wait for a render; a short wait lets an idle worker dispose its renderer on its own thread first.
        if (thread is not null && thread.IsAlive) thread.Join(TimeSpan.FromMilliseconds(500));
        DrainRetired();
    }

    // ------------------------------------------------------------------------------------------------ the pipeline

    /// <summary>Memory, then disk (thread pool), then the worker.</summary>
    private void Resolve(AssetNodeViewModel node, bool forced, MapThumbnailTarget? known, bool keepsPicture, int attempt)
    {
        var target = known ?? _host.Describe(node);
        if (target is null)
        {
            // nothing to describe: remembered, or the same scan for a materials bin reruns on every settle of the grid
            _unavailable.Add(node);
            _stale.Remove(node);
            return;
        }
        string key = target.Key;

        // a tile that shows the picture of this very key needs nothing (a stale tile whose state did not change)
        if (!forced && node.Thumbnail is not null && _shownKey.TryGetValue(node, out var shown) && shown == key)
        {
            _stale.Remove(node);
            return;
        }
        if (!forced && TryRemembered(key, out var png))
        {
            Interlocked.Increment(ref _fromMemory);
            _stale.Remove(node);
            Present(node, key, png, replace: false);
            return;
        }
        if (!forced && _failedThisSession.Contains(key)) { _unavailable.Add(node); _stale.Remove(node); return; }

        var pending = NewPending(node, target, forced, keepsPicture, attempt);
        if (forced) { EnqueueRender(pending); return; }

        // the disk is looked at off the UI thread, and off the worker too: a picture that is already cached must not wait
        // behind a render that takes twenty seconds
        ThreadPool.QueueUserWorkItem(_ =>
        {
            byte[] cached = Array.Empty<byte>();
            string? failure = null;
            bool hit = _cache.TryReadPng(key, out cached);
            if (!hit) failure = _cache.TryReadFailure(key);
            _post(() =>
            {
                if (_disposed || !_tracked.TryGetValue(node, out var current) || !ReferenceEquals(current, pending)) return;
                if (Volatile.Read(ref _generation) != pending.Generation) return;
                if (hit)
                {
                    _tracked.Remove(node);
                    _stale.Remove(node);
                    Interlocked.Increment(ref _fromDisk);
                    Remember(key, cached);
                    Present(node, key, cached, replace: false);
                }
                else if (failure is not null)
                {
                    _tracked.Remove(node);
                    _failedThisSession.Add(key);
                    _unavailable.Add(node);
                    _stale.Remove(node);
                }
                else EnqueueRender(pending);
            });
        });
    }

    private Pending NewPending(AssetNodeViewModel node, MapThumbnailTarget target, bool forced, bool keepsPicture, int attempt)
    {
        long gen;
        lock (_gate) gen = _generation;
        var pending = new Pending
        {
            Node = node, Target = target, Generation = gen, Forced = forced, KeepsPicture = keepsPicture, Attempt = attempt,
        };
        _tracked[node] = pending;
        target.Job.IsCancelled = () => _disposed || pending.Abandoned;
        target.Job.ShouldPause = () => _host.ShouldPause;
        return pending;
    }

    /// <summary>Shows a picture on a tile. A picture the tile already shows for this key is left alone unless
    /// <paramref name="replace"/>. Returns whether the tile shows it afterwards. A picture that will not decode is evicted from
    /// memory, deleted from disk, and the tile is drawn again - once per key.</summary>
    private bool Present(AssetNodeViewModel node, string key, byte[] png, bool replace)
    {
        if (!replace && node.Thumbnail is not null && _shownKey.TryGetValue(node, out var shown) && shown == key) return true;
        if (_apply(node, png))
        {
            _shownKey.AddOrUpdate(node, key);
            return true;
        }

        ForgetMemory(key);
        _cache.Delete(key);
        if (_healed.Add(key))
        {
            _host.Log($"Map thumbnail {node.Name}: the stored picture would not load - deleted, drawing it again.");
            Resolve(node, forced: false, known: null, keepsPicture: node.Thumbnail is not null, attempt: 0);
        }
        else
        {
            _failedThisSession.Add(key);
            _unavailable.Add(node);
            _host.Log($"Map thumbnail {node.Name}: a freshly drawn picture would not load either - the tile keeps its icon.");
        }
        return false;
    }

    private void EnqueueRender(Pending pending)
    {
        if (_disposed) return;
        if (!_host.CanGenerate || _disabled)
        {
            _tracked.Remove(pending.Node);   // the OpenGL viewport is selected, or the device failed: cached pictures only
            return;
        }
        if (!pending.Forced && !_order.ContainsKey(pending.Node))
        {
            _tracked.Remove(pending.Node);   // it scrolled away while the disk was being looked at: not worth a draw
            return;
        }
        lock (_gate)
        {
            if (_stop || pending.Generation != _generation) return;
            int rank = Rank(pending), at = _queue.Count;
            for (int i = 0; i < _queue.Count; i++)
                if (Rank(_queue[i]) > rank) { at = i; break; }   // behind everything with a turn before or equal to its own
            _queue.Insert(at, pending);
            _thread ??= StartThread();
            Monitor.PulseAll(_gate);
        }
        if (!_pruned) { _pruned = true; ThreadPool.QueueUserWorkItem(_ => _cache.Prune(3000)); }
    }

    private Thread StartThread()
    {
        var thread = new Thread(WorkerLoop)
        {
            Name = "MapThumbnails",
            IsBackground = true,                       // never keeps the process alive
            Priority = ThreadPriority.BelowNormal,     // the editor comes first
        };
        thread.Start();
        return thread;
    }

    private void WorkerLoop()
    {
        IMapThumbnailRenderer? renderer = null;
        bool holdingDevice = false;
        try
        {
            while (true)
            {
                Pending? job = null;
                bool release = false;
                lock (_gate)
                {
                    while (true)
                    {
                        if (_stop) return;
                        bool paused = _queue.Count > 0 && _host.ShouldPause;
                        if (_queue.Count > 0 && !paused)
                        {
                            job = _queue[0];
                            _queue.RemoveAt(0);
                            _active = job;
                            break;
                        }
                        // nothing to start: sleep until there is, and give the device back if that takes a while
                        bool signalled = Monitor.Wait(_gate, paused ? PauseRecheck : IdleRelease);
                        if (!signalled && !paused && _queue.Count == 0 && holdingDevice) { release = true; break; }
                    }
                }
                if (release)
                {
                    renderer?.ReleaseDevice();
                    holdingDevice = false;
                    continue;
                }

                var drawing = job!;
                drawing.InputsVersion = SafeVersion();
                var outcome = Draw(ref renderer, drawing);
                holdingDevice = true;
                IDisposable[] retired;
                lock (_gate)
                {
                    _active = null;
                    retired = _retired.ToArray();
                    _retired.Clear();
                }
                // whatever the editor replaced while this tile was reading through it can go now
                foreach (var r in retired) DisposeQuietly(r);

                // the file goes down on this thread; the UI only ever hears about it afterwards. Nothing here may throw out of
                // the loop: an exception on a background thread ends the whole app.
                try
                {
                    var inputs = drawing.Target.Job;
                    bool abandoned = inputs.IsCancelled?.Invoke() == true;
                    // a draw during which something it reads through changed in place, or in which a read threw, may have read
                    // "missing" for assets that are there: its picture is not kept, and it is not a failure to write down
                    bool tainted = !abandoned && !outcome.Cancelled && !outcome.FromCache && !outcome.DeviceFailed
                                   && (SafeVersion() != drawing.InputsVersion || (inputs.ReadFaults?.Invoke() ?? 0) > 0);
                    if (!abandoned && !tainted && !outcome.FromCache)
                    {
                        if (outcome.Png is { } png) _cache.WritePng(drawing.Target.Key, png);
                        else if (outcome.Permanent && outcome.Failure is { } why) _cache.WriteFailure(drawing.Target.Key, why);
                    }
                    _post(() => OnDrawn(drawing, outcome, tainted));
                }
                catch (Exception ex)
                {
                    try { _host.Log($"Map thumbnail {drawing.Target.Name}: {ex.GetType().Name}: {ex.Message}"); } catch { }
                }
            }
        }
        finally
        {
            try { renderer?.Dispose(); } catch { /* the device goes with the process */ }
            DrainRetired();
        }
    }

    private long SafeVersion()
    {
        try { return _host.InputsVersion; }
        catch { return -1; }
    }

    private MapThumbnailOutcome Draw(ref IMapThumbnailRenderer? renderer, Pending job)
    {
        // The same map may have been drawn while this tile waited: a rebuild gives the grid new tile objects, and the new tile
        // for the map whose draw is finishing queues behind it. Its picture is on disk by now (written before the next tile is
        // taken), so this is a read, not a second draw of a minute-long Summoner's Rift.
        if (!job.Forced && _cache.TryReadPng(job.Target.Key, out var existing))
            return new MapThumbnailOutcome { Png = existing, FromCache = true };
        try
        {
            renderer ??= _rendererFactory();
            return renderer.Render(job.Target.Job);
        }
        catch (Exception ex)
        {
            return new MapThumbnailOutcome { Failure = $"{ex.GetType().Name}: {ex.Message}" };
        }
    }

    /// <summary>UI thread: a draw finished.</summary>
    private void OnDrawn(Pending job, MapThumbnailOutcome outcome, bool tainted)
    {
        if (_disposed) return;
        bool superseded = false;
        if (_tracked.TryGetValue(job.Node, out var current))
        {
            if (ReferenceEquals(current, job)) _tracked.Remove(job.Node);
            else superseded = true;   // the tile has a newer request (its key changed): this picture is not what it wants now
        }
        if (job.Abandoned || outcome.Cancelled) return;   // different content is open: nothing of this reaches a tile
        if (tainted) { Retry(job); return; }

        string key = job.Target.Key;
        if (outcome.Png is { } png)
        {
            if (outcome.FromCache) Interlocked.Increment(ref _fromDisk);
            else
            {
                Interlocked.Increment(ref _rendered);
                _host.Log($"Map thumbnail {job.Target.Name}: {outcome.TotalMs / 1000:0.0} s ({outcome.Timings}); {outcome.Summary}.");
            }
            // kept whatever became of the tile that asked: a rebuild gave the grid new tiles, and the one for this map finds the
            // picture in memory or on disk under the same key
            Remember(key, png);
            _stale.Remove(job.Node);
            if (!superseded) Present(job.Node, key, png, replace: job.Forced);
            return;
        }

        // a refresh that failed leaves the picture it was refreshing, and records nothing
        if (job.KeepsPicture)
        {
            _host.Log($"Map thumbnail {job.Target.Name}: not drawn again - {outcome.Failure}. The picture it had stays.");
            return;
        }

        Interlocked.Increment(ref _failed);
        _failedThisSession.Add(key);
        _unavailable.Add(job.Node);
        _stale.Remove(job.Node);
        if (outcome.DeviceFailed)
        {
            if (!_disabled)
            {
                _disabled = true;
                lock (_gate) { _generation++; _queue.Clear(); }
                _tracked.Clear();
                _host.Log($"Map thumbnails are off for this session: {outcome.Failure}");
            }
            return;
        }
        _host.Log($"Map thumbnail {job.Target.Name}: not drawn - {outcome.Failure}"
                  + (outcome.Permanent ? " (remembered; Refresh Thumbnail tries again)." : "."));
    }

    /// <summary>A draw that something changed under: drawn again, if the tile is still wanted and it has not been drawn
    /// <see cref="MaxAttempts"/> times already.</summary>
    private void Retry(Pending job)
    {
        bool more = job.Attempt + 1 < MaxAttempts;
        _host.Log($"Map thumbnail {job.Target.Name}: what it reads changed while it was drawn - the picture is not kept"
                  + (more ? ", drawing it again." : ", given up for now (Refresh Thumbnail tries again)."));
        if (!more) { _unavailable.Add(job.Node); return; }
        if (!job.Forced && !_order.ContainsKey(job.Node)) return;   // scrolled away meanwhile: not worth another draw
        var target = _host.Describe(job.Node);                       // a fresh key and a fresh reader
        if (target is null) { _unavailable.Add(job.Node); return; }
        EnqueueRender(NewPending(job.Node, target, job.Forced, job.KeepsPicture, job.Attempt + 1));
    }

    private void DrainRetired()
    {
        IDisposable[] retired;
        lock (_gate)
        {
            retired = _retired.ToArray();
            _retired.Clear();
        }
        foreach (var r in retired) DisposeQuietly(r);
    }

    private static void DisposeQuietly(IDisposable resource)
    {
        try { resource.Dispose(); } catch { /* a reader that will not close is released with the process */ }
    }

    // ------------------------------------------------------------------------------------------------ memory LRU

    private bool TryRemembered(string key, out byte[] png)
    {
        if (_lru.TryGetValue(key, out var node))
        {
            _lruOrder.Remove(node);
            _lruOrder.AddFirst(node);
            png = node.Value.Value;
            return true;
        }
        png = Array.Empty<byte>();
        return false;
    }

    private void Remember(string key, byte[] png)
    {
        if (_lru.TryGetValue(key, out var existing)) { _lruOrder.Remove(existing); _lru.Remove(key); }
        var node = _lruOrder.AddFirst(new KeyValuePair<string, byte[]>(key, png));
        _lru[key] = node;
        while (_lruOrder.Count > _lruCapacity)
        {
            var last = _lruOrder.Last!;
            _lruOrder.RemoveLast();
            _lru.Remove(last.Value.Key);
        }
    }

    private void ForgetMemory(string key)
    {
        if (_lru.Remove(key, out var node)) _lruOrder.Remove(node);
    }
}
