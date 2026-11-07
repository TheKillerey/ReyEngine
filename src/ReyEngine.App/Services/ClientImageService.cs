using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using ReyEngine.Formats.Characters;

namespace ReyEngine.App.Services;

/// <summary>
/// M870: the League client's images (champion portraits, skin tiles, load screens, ability icons, chroma pictures) as Avalonia
/// bitmaps for the Character window.
///
/// <para>Lazy. Nothing is read until something asks; a request decodes off the UI thread, straight to the size it will
/// be shown at (<see cref="Bitmap.DecodeToWidth"/>, so a 380 px tile never sits in memory at full size for a 84 px card); the result is
/// kept in a least-recently-used cache of <see cref="Capacity"/> entries; two requests for the same image share one decode; and an
/// image the client does not have is remembered as missing for <see cref="RetryMilliseconds"/> only (a WAD that would not open, or a
/// client that was mid-update, must not leave a blank tile for the whole session). At most <see cref="MaxConcurrentDecodes"/> decodes
/// run at once, so scrolling a long grid does not start a hundred of them together.</para>
///
/// <para>The cache holds at most <see cref="Capacity"/> bitmaps that nothing else references. It does not bound memory absolutely: a
/// <see cref="ClientImageHandle"/> keeps the bitmap it was given for as long as its row lives, so an evicted bitmap that is still on
/// screen stays alive. At the sizes used here (a few hundred small bitmaps) that is fine. Evicted bitmaps are not disposed, because
/// one may be on screen; an undisposed bitmap is released by its finalizer.</para>
///
/// <para>The WAD data behind the service can be swapped (<see cref="Use"/>) when the game folder changes, so rows built earlier keep
/// working.</para>
/// </summary>
public sealed class ClientImageService
{
    public const int DefaultCapacity = 320;
    public const int MaxConcurrentDecodes = 2;

    private ClientChampionData _data;
    public const int RetryMilliseconds = 5000;
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, Bitmap Bitmap)>> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, Bitmap Bitmap)> _recent = new();
    private readonly Dictionary<string, Task<Bitmap?>> _inFlight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _missing = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slots = new(MaxConcurrentDecodes);

    public ClientImageService(ClientChampionData data, int capacity = DefaultCapacity)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _capacity = Math.Max(8, capacity);
    }

    public ClientChampionData Data { get { lock (_gate) return _data; } }

    /// <summary>Point the service at another install's data (the game folder changed). Cached bitmaps and failures are dropped.</summary>
    public void Use(ClientChampionData data)
    {
        lock (_gate)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _cache.Clear(); _recent.Clear(); _missing.Clear();
        }
    }

    private bool IsMissingLocked(string key)
    {
        if (!_missing.TryGetValue(key, out long until)) return false;
        if (Environment.TickCount64 < until) return true;
        _missing.Remove(key);
        return false;
    }
    public int Capacity => _capacity;

    /// <summary>How many decoded bitmaps are held right now.</summary>
    public int CachedCount { get { lock (_gate) return _cache.Count; } }

    /// <summary>How many decodes ran (cache misses that reached the decoder) since this service was made. For tests.</summary>
    public int DecodeCount { get { lock (_gate) return _decodes; } }
    private int _decodes;

    private static string KeyOf(string wadPath, int width) => wadPath + "|" + width;

    /// <summary>The bitmap when it is already decoded, else null. Never reads anything.</summary>
    public Bitmap? TryGetCached(string? path, int width)
    {
        string wad = ClientChampionData.ToWadPath(path);
        if (wad.Length == 0) return null;
        lock (_gate)
        {
            if (!_cache.TryGetValue(KeyOf(wad, width), out var node)) return null;
            _recent.Remove(node);
            _recent.AddFirst(node);
            return node.Value.Bitmap;
        }
    }

    /// <summary>True when this image was asked for, could not be had, and the retry delay has not passed.</summary>
    public bool IsMissing(string? path, int width)
    {
        string wad = ClientChampionData.ToWadPath(path);
        lock (_gate) return IsMissingLocked(KeyOf(wad, width));
    }

    /// <summary>Decode the image at <paramref name="width"/> pixels wide (aspect kept). Null when the client does not have it or it does
    /// not decode. Never throws. The decode runs on a worker thread; await it from the UI thread and the continuation returns there.</summary>
    public Task<Bitmap?> LoadAsync(string? path, int width, CancellationToken cancel = default)
    {
        string wad = ClientChampionData.ToWadPath(path);
        if (wad.Length == 0 || width <= 0) return Task.FromResult<Bitmap?>(null);
        string key = KeyOf(wad, width);

        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var node))
            {
                _recent.Remove(node);
                _recent.AddFirst(node);
                return Task.FromResult<Bitmap?>(node.Value.Bitmap);
            }
            if (IsMissingLocked(key)) return Task.FromResult<Bitmap?>(null);
            if (_inFlight.TryGetValue(key, out var running)) return running;
            var started = Task.Run(() => DecodeAsync(wad, key, width), CancellationToken.None);
            _inFlight[key] = started;
            return started;
        }
    }

    private async Task<Bitmap?> DecodeAsync(string wad, string key, int width)
    {
        Bitmap? bitmap = null;
        await _slots.WaitAsync().ConfigureAwait(false);
        try
        {
            ClientChampionData data; lock (_gate) data = _data;
            byte[]? bytes = data.ReadAsset(wad);
            if (bytes is not null)
            {
                try
                {
                    using var stream = new MemoryStream(bytes, writable: false);
                    bitmap = Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.HighQuality);
                }
                catch { bitmap = null; }
            }
        }
        finally
        {
            _slots.Release();
            lock (_gate)
            {
                _inFlight.Remove(key);
                _decodes++;
                if (bitmap is null) _missing[key] = Environment.TickCount64 + RetryMilliseconds;
                else
                {
                    var node = _recent.AddFirst((key, bitmap));
                    _cache[key] = node;
                    while (_cache.Count > _capacity && _recent.Last is { } oldest)
                    {
                        _recent.RemoveLast();
                        _cache.Remove(oldest.Value.Key);
                    }
                }
            }
        }
        return bitmap;
    }
}
