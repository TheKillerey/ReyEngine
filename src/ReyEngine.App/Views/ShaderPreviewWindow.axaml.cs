using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Input.Raw;
using Avalonia.Platform.Storage;
using ReyEngine.App.ViewModels;

namespace ReyEngine.App.Views;

/// <summary>M210: the experimental DX11 shader preview. Non-modal, and deliberately separate from the
/// Material Editor — it exists to validate that Riot's compiled shaders load and render correctly before
/// any of that is wired into the real material pipeline.</summary>
public partial class ShaderPreviewWindow : Window
{
    public ShaderPreviewWindow()
    {
        InitializeComponent();

        // double-click a texture slot to point it at an image
        if (this.FindControl<ListBox>("TextureList") is { } list)
            list.DoubleTapped += OnTextureDoubleTapped;

        Closed += (_, _) => { _closed = true; (DataContext as ShaderPreviewViewModel)?.Dispose(); if (_hooked is not null) { _hooked.FitRequested -= OnFit; _hooked.FocusRequested -= OnFocusNode; } };

        // M215: the same camera bindings as the map viewport - WASD/QE fly, drag to look, middle-drag to
        // pan, alt-drag to orbit, wheel to zoom, LMB+wheel for fly speed, F to reframe.
        if (this.FindControl<Panel>("PreviewSurface") is { } surface)
        {
            // M216: render at the surface's REAL pixel size. A fixed 640x480 stretched across a wider panel
            // is what made everything look soft.
            surface.PropertyChanged += (_, ev) =>
            {
                if (ev.Property == BoundsProperty)
                    Vm?.SetSurfaceSize(surface.Bounds.Width, surface.Bounds.Height, RenderScaling);
            };
            surface.PointerPressed += OnSurfacePressed;
            surface.PointerReleased += OnSurfaceReleased;
            surface.PointerMoved += OnSurfaceMoved;
            surface.PointerWheelChanged += OnSurfaceWheel;
        }
        // M829: the graph canvas is driven by the view model's Home / Search commands
        DataContextChanged += (_, _) => HookGraph();
        HookGraph();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => Vm?.ClearKeys();
        Opened += (_, _) =>
        {
            if (this.FindControl<Panel>("PreviewSurface") is { } sfc)
                Vm?.SetSurfaceSize(sfc.Bounds.Width, sfc.Bounds.Height, RenderScaling);
            RequestNextFrame();
        };
    }

    private ShaderPreviewViewModel? Vm => DataContext as ShaderPreviewViewModel;

    private MaterialGraphViewModel? _hooked;

    /// <summary>M829: Home frames the whole graph and Search centres a node - both are view actions the
    /// canvas owns, so the view model raises events and the window forwards them.</summary>
    private void HookGraph()
    {
        if (_hooked is not null)
        {
            _hooked.FitRequested -= OnFit;
            _hooked.FocusRequested -= OnFocusNode;
            _hooked = null;
        }
        if (DataContext is ShaderPreviewViewModel vm)
        {
            _hooked = vm.MaterialGraph;
            _hooked.FitRequested += OnFit;
            _hooked.FocusRequested += OnFocusNode;
        }
    }

    private void OnFit() => this.FindControl<MaterialGraphCanvas>("GraphCanvas")?.FitToGraph();
    private void OnFocusNode(string id) => this.FindControl<MaterialGraphCanvas>("GraphCanvas")?.FocusNode(id);

    private bool _closed;

    /// <summary>M227: drive the preview from the compositor rather than a 33 ms DispatcherTimer, which
    /// capped the window at 30 fps nominal and measured 20 in practice. RequestAnimationFrame fires per
    /// compositor frame, so the rate follows the display and the frame cost, not an arbitrary interval.</summary>
    private void RequestNextFrame()
    {
        if (_closed) return;
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(_ =>
        {
            if (_closed) return;
            Vm?.ExternalTick();
            RequestNextFrame();
        });
    }

    private bool _lmb, _rmb, _mmb;
    private Avalonia.Point _last;

    private void OnSurfacePressed(object? sender, PointerPressedEventArgs e)
    {
        var pt = e.GetCurrentPoint(this);
        _lmb = pt.Properties.IsLeftButtonPressed;
        _rmb = pt.Properties.IsRightButtonPressed;
        _mmb = pt.Properties.IsMiddleButtonPressed;
        _last = pt.Position;
        // the surface must hold focus or WASD goes to whatever list was clicked last
        (sender as Panel)?.Focus();
        e.Pointer.Capture(sender as IInputElement);
    }

    private void OnSurfaceReleased(object? sender, PointerReleasedEventArgs e)
    {
        _lmb = _rmb = _mmb = false;
        e.Pointer.Capture(null);
    }

    private void OnSurfaceMoved(object? sender, PointerEventArgs e)
    {
        if (Vm is null || (!_lmb && !_rmb && !_mmb)) return;
        var p = e.GetPosition(this);
        float dx = (float)(p.X - _last.X), dy = (float)(p.Y - _last.Y);
        _last = p;

        bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        if (_lmb && alt) Vm.OrbitBy(dx, dy);
        else if (_lmb || _rmb) Vm.LookBy(dx, dy);
        else if (_mmb) Vm.PanBy(dx, dy);
    }

    private void OnSurfaceWheel(object? sender, PointerWheelEventArgs e)
    {
        if (Vm is null) return;
        if (_lmb) Vm.AdjustFlySpeed((float)e.Delta.Y);
        else Vm.ZoomBy((float)e.Delta.Y);
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // never swallow typing in a filter box or a constant override
        // ... nor the graph canvas, whose F (focus node) and Home are its own: fly keys must not move the 3D camera
        if (FocusManager?.GetFocusedElement() is TextBox or MaterialGraphCanvas) return;
        // M830: Ctrl+Z / Ctrl+Y / Ctrl+S are the editor's (window key bindings), never camera keys
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        Vm?.KeyDown(e.Key);
    }

    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        // a key released after focus moved to the canvas must still let go of the camera
        Vm?.KeyUp(e.Key);
    }

    /// <summary>M830: an editor box lost focus - what was typed in it is applied (Enter does the same). The row ignores a
    /// value that did not change, so tabbing through boxes writes nothing.</summary>
    private void OnEditLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Tag: "hex", DataContext: GraphParamEditRow colour }) { colour.CommitHexCommand.Execute(null); return; }
        switch ((sender as Control)?.DataContext)
        {
            case GraphComponentField f: f.CommitCommand.Execute(null); break;
            case GraphEditRow row: row.Commit(); break;
        }
    }

    private async void OnTextureDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not ShaderPreviewViewModel vm) return;
        if (sender is not ListBox { SelectedItem: TextureSlotRow row }) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Bind an image to {row.Name}",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Images") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp" } },
            },
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) vm.BindTextureFile(row, path);
    }
}
