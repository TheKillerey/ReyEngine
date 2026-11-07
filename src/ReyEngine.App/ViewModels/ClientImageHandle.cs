using System;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ReyEngine.App.Services;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M870: one client image a view model owns - a portrait, a skin tile, an ability icon - that is decoded only when something shows it.
///
/// <para>The view calls <see cref="Request"/> when the control comes into view (see <c>ClientImageView</c>); until then this holds a path
/// and nothing else. <see cref="Bitmap"/> turns non-null when the decode finishes, on the thread that called <see cref="Request"/>
/// (the UI thread). A null service or path is a handle that never has an image, so a Game-only install, a project character or a
/// companion binds exactly the same way and simply shows its placeholder.</para>
/// </summary>
public sealed class ClientImageHandle : ObservableObject
{
    private readonly ClientImageService? _service;
    private bool _requested;
    private Bitmap? _bitmap;

    public ClientImageHandle(ClientImageService? service, string? path, int decodeWidth)
    {
        _service = service;
        Path = string.IsNullOrWhiteSpace(path) ? null : path;
        DecodeWidth = Math.Max(16, decodeWidth);
    }

    /// <summary>A handle with nothing behind it.</summary>
    public static ClientImageHandle None { get; } = new(null, null, 16);

    public string? Path { get; }
    public int DecodeWidth { get; }

    /// <summary>True when there is something to load at all.</summary>
    public bool CanLoad => _service is not null && Path is not null;

    public Bitmap? Bitmap
    {
        get => _bitmap;
        private set
        {
            if (ReferenceEquals(_bitmap, value)) return;
            _bitmap = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasImage));
            OnPropertyChanged(nameof(NoImage));
        }
    }

    public bool HasImage => _bitmap is not null;
    public bool NoImage => _bitmap is null;

    /// <summary>Start loading when nobody has yet. Cheap to call again. Call it on the UI thread.</summary>
    public async void Request()
    {
        if (_requested || !CanLoad) return;
        _requested = true;
        if (_service!.TryGetCached(Path, DecodeWidth) is { } cached) { Bitmap = cached; return; }
        try { Bitmap = await _service.LoadAsync(Path, DecodeWidth); }
        catch { /* an image that will not load is a placeholder */ }
        if (_bitmap is null) _requested = false;     // a failure is not final: the next time the control is shown it asks again
    }
}
