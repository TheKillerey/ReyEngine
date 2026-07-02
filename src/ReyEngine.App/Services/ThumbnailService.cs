using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace ReyEngine.App.Services;

/// <summary>
/// Lazy thumbnail loader for the Content Browser (M33). Decodes one texture at a time on the UI thread at
/// background priority (so the WAD reader is never touched concurrently and the UI stays responsive), and
/// caches by path. Requesting an already-cached path returns it immediately.
/// </summary>
public sealed class ThumbnailService
{
    private readonly Func<string, Bitmap?> _decode;
    private readonly Dictionary<string, Bitmap?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<(string path, Action<Bitmap?> apply)> _queue = new();
    private bool _pumping;

    public ThumbnailService(Func<string, Bitmap?> decode) => _decode = decode;

    /// <summary>Deliver a thumbnail for <paramref name="path"/> to <paramref name="apply"/> (immediately if
    /// cached, otherwise once decoded on a later background tick).</summary>
    public void Request(string? path, Action<Bitmap?> apply)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (_cache.TryGetValue(path, out var hit)) { if (hit is not null) apply(hit); return; }
        _queue.Enqueue((path, apply));
        if (!_pumping) { _pumping = true; Dispatcher.UIThread.Post(Pump, DispatcherPriority.Background); }
    }

    private void Pump()
    {
        if (_queue.Count == 0) { _pumping = false; return; }
        var (path, apply) = _queue.Dequeue();
        if (!_cache.TryGetValue(path, out var bmp))
        {
            try { bmp = _decode(path); } catch { bmp = null; }
            _cache[path] = bmp;
        }
        if (bmp is not null) apply(bmp);
        Dispatcher.UIThread.Post(Pump, DispatcherPriority.Background);
    }

    public void Clear()
    {
        _queue.Clear();
        _cache.Clear();
        _pumping = false;
    }
}
