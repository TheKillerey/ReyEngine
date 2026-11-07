using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using ReyEngine.App.ViewModels;

namespace ReyEngine.App.Views;

/// <summary>
/// M870: an <see cref="Image"/> showing a <see cref="ClientImageHandle"/>, which it asks to decode only once the control is actually
/// visible - inside the part of its scroll viewers that is on screen (<see cref="Layoutable.EffectiveViewportChanged"/>) - so a grid of
/// 170 portraits decodes the dozen the user can see. <see cref="Eager"/> asks on attach instead (a tooltip, a banner).
/// </summary>
public sealed class ClientImageView : Image
{
    public static readonly StyledProperty<ClientImageHandle?> HandleProperty =
        AvaloniaProperty.Register<ClientImageView, ClientImageHandle?>(nameof(Handle));

    public static readonly StyledProperty<bool> EagerProperty =
        AvaloniaProperty.Register<ClientImageView, bool>(nameof(Eager));

    private ClientImageHandle? _watched;
    private bool _visible;

    public ClientImageView()
    {
        Stretch = Avalonia.Media.Stretch.UniformToFill;
        EffectiveViewportChanged += OnViewport;
    }

    public ClientImageHandle? Handle { get => GetValue(HandleProperty); set => SetValue(HandleProperty, value); }
    public bool Eager { get => GetValue(EagerProperty); set => SetValue(EagerProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != HandleProperty) return;
        var handle = change.GetNewValue<ClientImageHandle?>();
        Watch(handle);
        // a new handle on a control that is already showing: ask for it now, the viewport has not changed to say so
        if (handle is not null && (_visible || (Eager && VisualRoot is not null))) handle.Request();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Watch(Handle);
        if (Eager) Handle?.Request();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Watch(null);
        _visible = false;
        Source = null;     // the cache owns the bitmap; a recycled control must not keep one alive
    }

    private void Watch(ClientImageHandle? handle)
    {
        if (_watched is not null) _watched.PropertyChanged -= OnHandleChanged;
        _watched = handle;
        if (_watched is not null && VisualRoot is not null) _watched.PropertyChanged += OnHandleChanged;
        Show();
    }

    private void OnHandleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClientImageHandle.Bitmap)) Show();
    }

    private void Show() => Source = _watched?.Bitmap;

    private void OnViewport(object? sender, EffectiveViewportChangedEventArgs e)
    {
        _visible = e.EffectiveViewport.Width > 0 && e.EffectiveViewport.Height > 0;
        if (_visible) Handle?.Request();
    }
}
