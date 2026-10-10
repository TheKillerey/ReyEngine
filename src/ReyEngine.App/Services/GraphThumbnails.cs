using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ReyEngine.Core.Decoding;

namespace ReyEngine.App.Services;

/// <summary>
/// M829: the small textures the Material Graph's texture nodes show. A texture node asks for a WAD chunk hash;
/// the first ask starts a background read + decode (the same <c>TextureDecoder</c> every other view uses) and the
/// bitmap arrives later through <see cref="Updated"/>. Nothing is decoded on the UI thread. Entries are kept per
/// chunk, so reselecting a material or two materials sharing a texture decode it once; a texture that will not
/// decode keeps its reason, which the node shows instead of a blank square.
///
/// <para>Disposing (the window closing) cancels the queued loads and disposes every cached bitmap, so closing the
/// window during a burst of decodes neither keeps working nor leaks the bitmaps.</para>
/// </summary>
public sealed class GraphThumbnails : IDisposable
{
    public sealed class Entry
    {
        public Bitmap? Bitmap { get; internal set; }
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        public string? Error { get; internal set; }
        public bool Pending { get; internal set; } = true;
    }

    public const int MaxSize = 128;

    private readonly Func<ulong, byte[]?>? _read;
    private readonly ConcurrentDictionary<ulong, Entry> _entries = new();
    private readonly SemaphoreSlim _gate = new(2);
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _disposed;

    public GraphThumbnails(Func<ulong, byte[]?>? read) => _read = read;

    /// <summary>Raised on the UI thread whenever an entry finishes (loaded or failed).</summary>
    public event Action? Updated;

    public int Count => _entries.Count;

    /// <summary>The entry for a chunk, starting its load on the first request. Null for hash 0 or once disposed.</summary>
    public Entry? Get(ulong chunk)
    {
        if (chunk == 0 || _disposed) return null;
        if (_entries.TryGetValue(chunk, out var existing)) return existing;
        var entry = new Entry();
        if (!_entries.TryAdd(chunk, entry)) return _entries[chunk];

        if (_read is null)
        {
            entry.Pending = false;
            entry.Error = "no asset mounts";
            return entry;
        }

        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await _gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    token.ThrowIfCancellationRequested();
                    var data = _read(chunk);
                    if (data is null || data.Length == 0) { entry.Error = "not found in the mounted archives"; return; }
                    token.ThrowIfCancellationRequested();
                    TextureImage img = TextureDecoder.Decode(data);
                    token.ThrowIfCancellationRequested();
                    var bmp = Downscale(img);
                    if (_disposed) { bmp.Dispose(); return; }
                    entry.Width = img.Width;
                    entry.Height = img.Height;
                    entry.Bitmap = bmp;
                }
                finally { _gate.Release(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { entry.Error = ex.Message; }
            finally
            {
                entry.Pending = false;
                if (!_disposed) Dispatcher.UIThread.Post(() => { if (!_disposed) Updated?.Invoke(); });
            }
        });
        return entry;
    }

    /// <summary>A box-filtered thumbnail (each output pixel is the mean of the source pixels it covers), so a 2K
    /// texture does not alias into noise the way picking one pixel in N does.</summary>
    internal static WriteableBitmap Downscale(TextureImage img)
    {
        int w = img.Width, h = img.Height;
        double scale = Math.Min(1.0, (double)MaxSize / Math.Max(w, h));
        int tw = Math.Max(1, (int)(w * scale)), th = Math.Max(1, (int)(h * scale));
        var bgra = new byte[tw * th * 4];

        if (tw == w && th == h)
        {
            for (int i = 0; i < w * h * 4; i += 4)
            {
                bgra[i] = img.Rgba[i + 2]; bgra[i + 1] = img.Rgba[i + 1]; bgra[i + 2] = img.Rgba[i]; bgra[i + 3] = img.Rgba[i + 3];
            }
        }
        else
        {
            var sum = new long[tw * th * 4];
            var cnt = new int[tw * th];
            for (int y = 0; y < h; y++)
            {
                int ty = Math.Min(th - 1, (int)((long)y * th / h));
                for (int x = 0; x < w; x++)
                {
                    int tx = Math.Min(tw - 1, (int)((long)x * tw / w));
                    int si = (y * w + x) * 4, ti = ty * tw + tx;
                    sum[ti * 4] += img.Rgba[si + 2];
                    sum[ti * 4 + 1] += img.Rgba[si + 1];
                    sum[ti * 4 + 2] += img.Rgba[si];
                    sum[ti * 4 + 3] += img.Rgba[si + 3];
                    cnt[ti]++;
                }
            }
            for (int ti = 0; ti < tw * th; ti++)
            {
                int c = Math.Max(1, cnt[ti]);
                for (int k = 0; k < 4; k++) bgra[ti * 4 + k] = (byte)(sum[ti * 4 + k] / c);
            }
        }

        var bmp = new WriteableBitmap(new PixelSize(tw, th), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var fb = bmp.Lock();
        for (int y = 0; y < th; y++)
            Marshal.Copy(bgra, y * tw * 4, IntPtr.Add(fb.Address, y * fb.RowBytes), tw * 4);
        return bmp;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        foreach (var e in _entries.Values) { e.Bitmap?.Dispose(); e.Bitmap = null; }
        _entries.Clear();
        _cts.Dispose();
    }
}
