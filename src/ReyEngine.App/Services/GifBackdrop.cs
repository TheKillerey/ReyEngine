using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SkiaSharp;

namespace ReyEngine.App.Services;

/// <summary>M690: the frames of an animated GIF, decoded in order through Skia's codec. Avalonia's own
/// Bitmap takes the first frame of a GIF and stops, so the backdrop showed a still. This is the
/// decoding half, free of Avalonia so it can be tested on bytes; <see cref="GifPlayer"/> is the half
/// that paces the frames into a WriteableBitmap for the windows.
///
/// GIF frames are deltas on the frame before, so decoding is sequential: the codec is handed the
/// buffer that still holds frame i-1 and told so (PriorFrame), and writes frame i over it. Asking
/// for a frame out of order costs a rewind to frame 0.</summary>
public sealed class GifFrames : IDisposable
{
    private readonly SKCodec _codec;
    private readonly SKBitmap _scratch;
    private readonly SKCodecFrameInfo[] _frames;
    private int _decoded = -1;   // the frame the scratch buffer holds

    private GifFrames(SKCodec codec)
    {
        _codec = codec;
        _frames = codec.FrameInfo;
        Width = codec.Info.Width;
        Height = codec.Info.Height;
        _scratch = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
    }

    public int Width { get; }
    public int Height { get; }
    public int FrameCount => _frames.Length;
    /// <summary>Bytes per row of the decoded BGRA frame (Width * 4).</summary>
    public int Stride => _scratch.RowBytes;

    /// <summary>How long frame <paramref name="index"/> is shown, in milliseconds; GIFs that carry 0 (a
    /// browser convention for "as fast as you can") get the 100 ms browsers use for them.</summary>
    public int DurationMs(int index)
    {
        int d = _frames[index].Duration;
        return d <= 10 ? 100 : d;
    }

    /// <summary>Open a file. Null for anything that is not an animated GIF Skia can decode: a still GIF
    /// (one frame), another format, a broken file, or a frame size no backdrop needs (beyond 4096²).</summary>
    public static GifFrames? Open(string path)
    {
        try { return FromBytes(File.ReadAllBytes(path)); }
        catch { return null; }
    }

    public static GifFrames? FromBytes(byte[] bytes)
    {
        SKCodec? codec = null;
        try
        {
            codec = SKCodec.Create(new SKMemoryStream(bytes));
            if (codec is null || codec.FrameCount < 2) { codec?.Dispose(); return null; }
            if (codec.Info.Width <= 0 || codec.Info.Height <= 0 || (long)codec.Info.Width * codec.Info.Height > 4096L * 4096L) { codec.Dispose(); return null; }
            return new GifFrames(codec);
        }
        catch
        {
            codec?.Dispose();
            return null;
        }
    }

    /// <summary>Decode frame <paramref name="index"/> into the scratch buffer and copy it out as BGRA
    /// premultiplied rows of <see cref="Stride"/> bytes. False when Skia refuses the frame.</summary>
    public bool Decode(int index, byte[] bgra)
    {
        if (index < 0 || index >= FrameCount) return false;
        if (bgra.Length < Stride * Height) return false;
        // sequential from wherever the buffer is; a step backwards means starting over from frame 0
        int from = index > _decoded ? _decoded + 1 : 0;
        for (int i = from; i <= index; i++)
        {
            var options = i == 0 ? new SKCodecOptions(0) : new SKCodecOptions(i, i - 1);
            var result = _codec.GetPixels(_scratch.Info, _scratch.GetPixels(), options);
            if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput) { _decoded = -1; return false; }
            _decoded = i;
        }
        Marshal.Copy(_scratch.GetPixels(), bgra, 0, Stride * Height);
        return true;
    }

    public void Dispose()
    {
        _scratch.Dispose();
        _codec.Dispose();
    }
}

/// <summary>M690: one animated GIF paced onto the screen for every window that shows it. A
/// DispatcherTimer decodes the next frame when the current one's time is up and publishes it through
/// <see cref="FrameChanged"/>; the backdrop hosts swap their brush's source to <see cref="Current"/>.
/// Two WriteableBitmaps alternate so the renderer never reads the one being written.
///
/// Shared by path and reference-counted: ten open windows decode the GIF once, and the timer stops when
/// the last window lets go.</summary>
public sealed class GifPlayer
{
    private static readonly Dictionary<string, GifPlayer> Players = new(StringComparer.OrdinalIgnoreCase);

    private readonly GifFrames _frames;
    private readonly WriteableBitmap[] _buffers = new WriteableBitmap[2];
    private readonly byte[] _pixels;
    private readonly DispatcherTimer _timer;
    private int _frame = -1;
    private int _shown;
    private int _users;
    private bool _busy;

    public string Path { get; }
    /// <summary>The bitmap holding the frame on screen now.</summary>
    public WriteableBitmap Current => _buffers[_shown];
    public int Frame => _frame;
    public int FrameCount => _frames.FrameCount;
    public event Action? FrameChanged;

    private GifPlayer(string path, GifFrames frames)
    {
        Path = path;
        _frames = frames;
        _pixels = new byte[frames.Stride * frames.Height];
        for (int i = 0; i < 2; i++)
            _buffers[i] = new WriteableBitmap(new PixelSize(frames.Width, frames.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Advance();
        Advance();   // frame 0 is on Current before anyone looks
    }

    /// <summary>The player for a path, shared; null when the file is not an animated GIF. The caller
    /// must <see cref="Acquire"/> it to keep it running and <see cref="Release"/> it when done.</summary>
    public static GifPlayer? Shared(string path)
    {
        lock (Players)
        {
            if (Players.TryGetValue(path, out var existing)) return existing;
            var frames = GifFrames.Open(path);
            if (frames is null) return null;
            var player = new GifPlayer(path, frames);
            Players[path] = player;
            return player;
        }
    }

    public void Acquire()
    {
        _users++;
        if (!_timer.IsEnabled) _timer.Start();
    }

    public void Release()
    {
        _users--;
        if (_users > 0) return;
        _users = 0;
        _timer.Stop();
        lock (Players) Players.Remove(Path);
        _frames.Dispose();
    }

    /// <summary>Decode the next frame into the spare buffer, make it current, and set the timer for its
    /// duration. Public so a headless check can step the animation without waiting on a timer.</summary>
    public void Advance()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            int next = (_frame + 1) % _frames.FrameCount;
            if (!_frames.Decode(next, _pixels)) { _timer.Stop(); return; }
            int spare = 1 - _shown;
            var target = _buffers[spare];
            using (var fb = target.Lock())
            {
                int stride = _frames.Stride;
                if (fb.RowBytes == stride) Marshal.Copy(_pixels, 0, fb.Address, stride * _frames.Height);
                else
                    for (int y = 0; y < _frames.Height; y++)
                        Marshal.Copy(_pixels, y * stride, fb.Address + y * fb.RowBytes, Math.Min(stride, fb.RowBytes));
            }
            _shown = spare;
            _frame = next;
            _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(20, _frames.DurationMs(next)));
            FrameChanged?.Invoke();
        }
        finally { _busy = false; }
    }
}
