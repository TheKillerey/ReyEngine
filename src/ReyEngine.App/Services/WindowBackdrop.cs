using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace ReyEngine.App.Services;

/// <summary>M685: the user's picture behind EVERY window, not only the main one.
///
/// M683 put a Border behind the main window's DockPanel and set an ImageBrush on it from code. The
/// other thirty windows have nothing of the kind, and giving each one a layer by hand is the sort of
/// thing that is forgotten on the next window. So this hooks the one thing every window does: set its
/// Content. A class handler on <see cref="ContentControl.ContentProperty"/> for <see cref="Window"/>
/// wraps whatever content a window is given in a <see cref="Host"/> - a Panel with the picture layer
/// behind and the original content in front - the moment it is set, which for a XAML window is inside
/// InitializeComponent, before the window has a template or a screen. The host follows
/// <see cref="ThemeService.BackdropChanged"/> while it is on screen, so Settings previews the picture
/// live in whichever windows are open.
///
/// The bitmap is decoded once per path and shared by every window's brush; a changed path decodes the
/// new file and lets the old bitmap go with its last brush. M690: an animated GIF goes through one
/// shared <see cref="GifPlayer"/> instead, whose frames every host's brush follows.</summary>
public static class WindowBackdrop
{
    private static bool _installed;

    /// <summary>Install the hook. Idempotent; called from App startup before any window exists.</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        ContentControl.ContentProperty.Changed.AddClassHandler<Window>((window, e) => Wrap(window, e.NewValue));
    }

    /// <summary>The picture layer of a window that has been wrapped, or null (content not a control, or
    /// the hook not installed when the content was set).</summary>
    public static Border? LayerOf(Window window) => (window.Content as Host)?.Layer;

    private static void Wrap(Window window, object? content)
    {
        if (content is not Control control || control is Host) return;
        var host = new Host();
        // detach first: a window that is already showing holds the control in its ContentPresenter, and
        // a control cannot be given a second parent while it still has one
        window.Content = null;
        host.Children.Add(control);
        window.Content = host;
    }

    /// <summary>The brush for a backdrop: null for none, for a missing file, or for a file that will not
    /// decode - a bad path in Settings must never take a window down.</summary>
    public static ImageBrush? BrushFor(ThemeService.BackdropSpec? spec)
    {
        if (spec is null || string.IsNullOrWhiteSpace(spec.Path)) return null;
        var bitmap = BitmapFor(spec.Path);
        if (bitmap is null) return null;
        return Configure(new ImageBrush(bitmap), spec);
    }

    /// <summary>Opacity, stretch, tiling and centring from the spec - the same for a still and for every
    /// frame of a GIF.</summary>
    public static ImageBrush Configure(ImageBrush brush, ThemeService.BackdropSpec spec)
    {
        brush.Opacity = Math.Clamp(spec.Opacity, 0, 1);
        brush.Stretch = spec.Stretch switch
        {
            1 => Stretch.Uniform,
            2 => Stretch.Fill,
            3 => Stretch.None,
            _ => Stretch.UniformToFill,
        };
        brush.TileMode = spec.Stretch == 3 ? TileMode.Tile : TileMode.None;
        brush.AlignmentX = AlignmentX.Center;
        brush.AlignmentY = AlignmentY.Center;
        return brush;
    }

    /// <summary>Is this a file the GIF player should have? Only by extension; a still GIF falls back to
    /// the bitmap path when the player finds one frame.</summary>
    public static bool IsGif(string? path) => path is not null && path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);

    private static string? _cachedPath;
    private static DateTime _cachedStamp;
    private static Bitmap? _cachedBitmap;

    private static Bitmap? BitmapFor(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var stamp = File.GetLastWriteTimeUtc(path);
            if (_cachedBitmap is not null && string.Equals(_cachedPath, path, StringComparison.OrdinalIgnoreCase) && _cachedStamp == stamp)
                return _cachedBitmap;
            var bitmap = new Bitmap(path);
            _cachedPath = path; _cachedStamp = stamp; _cachedBitmap = bitmap;
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The wrapper: the picture layer behind, the window's own content in front. Subscribes to
    /// the theme service only while on screen.</summary>
    public sealed class Host : Panel
    {
        public Border Layer { get; } = new Border { IsHitTestVisible = false };
        private GifPlayer? _gif;
        private ImageBrush? _gifBrush;

        public Host() => Children.Add(Layer);

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            ThemeService.BackdropChanged += OnBackdropChanged;
            Apply(ThemeService.Backdrop);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            ThemeService.BackdropChanged -= OnBackdropChanged;
            DropGif();
            base.OnDetachedFromVisualTree(e);
        }

        private void OnBackdropChanged(ThemeService.BackdropSpec? spec)
        {
            if (Dispatcher.UIThread.CheckAccess()) Apply(spec);
            else Dispatcher.UIThread.Post(() => Apply(spec));
        }

        private void Apply(ThemeService.BackdropSpec? spec)
        {
            DropGif();
            // M690: an animated GIF is a shared player whose frames this brush follows
            if (spec is not null && IsGif(spec.Path) && GifPlayer.Shared(spec.Path) is { } player)
            {
                _gif = player;
                player.FrameChanged += OnGifFrame;
                player.Acquire();
                _gifBrush = Configure(new ImageBrush(player.Current), spec);
                Layer.Background = _gifBrush;
                return;
            }
            Layer.Background = BrushFor(spec);
        }

        private void OnGifFrame()
        {
            if (_gif is not null && _gifBrush is not null) _gifBrush.Source = _gif.Current;
        }

        private void DropGif()
        {
            if (_gif is null) return;
            _gif.FrameChanged -= OnGifFrame;
            _gif.Release();
            _gif = null;
            _gifBrush = null;
        }
    }
}
