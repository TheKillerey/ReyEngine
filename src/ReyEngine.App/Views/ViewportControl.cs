using Avalonia;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using ReyEngine.Rendering;
using Silk.NET.OpenGL;

namespace ReyEngine.App.Views;

/// <summary>
/// OpenGL viewport. Today it renders the editor grid with an orbit camera; it is the
/// foundation the SKN / MAPGEO mesh renderer will plug into (same GL context + camera).
/// </summary>
public sealed class ViewportControl : OpenGlControlBase
{
    private GL? _gl;
    private GridRenderer? _grid;
    private readonly OrbitCamera _camera = new();
    private Point _lastPointer;
    private bool _orbiting;

    protected override void OnOpenGlInit(GlInterface gl)
    {
        _gl = GL.GetApi(gl.GetProcAddress);
        _grid = new GridRenderer();
        _grid.Initialize(_gl);
        RequestNextFrameRendering();
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _grid?.Dispose();
        _grid = null;
        _gl = null;
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_gl is null || _grid is null) return;

        float scale = (float)(VisualRoot?.RenderScaling ?? 1.0);
        uint w = (uint)Math.Max(1, Bounds.Width * scale);
        uint h = (uint)Math.Max(1, Bounds.Height * scale);

        _gl.Viewport(0, 0, w, h);
        _gl.ClearColor(0.039f, 0.051f, 0.075f, 1f);
        _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        float aspect = h == 0 ? 1f : (float)w / h;
        _grid.Render(_camera.ViewProjection(aspect));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _orbiting = true;
        _lastPointer = e.GetPosition(this);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_orbiting) return;

        var p = e.GetPosition(this);
        var dx = (float)(p.X - _lastPointer.X);
        var dy = (float)(p.Y - _lastPointer.Y);
        _lastPointer = p;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            _camera.Pan(-dx, dy);
        else
            _camera.Orbit(-dx * 0.01f, -dy * 0.01f);

        RequestNextFrameRendering();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _orbiting = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _camera.Zoom(e.Delta.Y > 0 ? 0.9f : 1.1f);
        RequestNextFrameRendering();
    }
}
