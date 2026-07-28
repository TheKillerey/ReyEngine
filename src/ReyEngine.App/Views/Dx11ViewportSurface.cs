using System;
using System.Numerics;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ReyEngine.Rendering;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.App.Views;

/// <summary>
/// <para>M248 (phase 6, step 1): a Direct3D 11 surface that can stand in for the editor's OpenGL viewport,
/// side by side with it.</para>
///
/// <para><b>What this step is and is not.</b> It is the plumbing: a live D3D11 device inside the main
/// window, presenting through the same offscreen-readback path the shader preview already uses, driven by
/// the viewport's own camera, switchable at runtime. It is NOT the migration - nothing GL is deleted, and
/// the map/champion content itself arrives in step 2. Until then this draws a reference mesh, which is
/// enough to prove the device, the presentation and the shared camera all work in this window.</para>
///
/// <para><c>ViewportControl</c> derives from <c>OpenGlControlBase</c>, so the control IS the GL surface and
/// cannot render D3D itself. The swap therefore happens one level up, between two sibling controls in the
/// viewport <c>Grid</c>, which is also why this step costs nothing to reverse.</para>
/// </summary>
public sealed class Dx11ViewportSurface : IDisposable
{
    private readonly ShaderPreviewRenderer _renderer = new();
    private WriteableBitmap? _front, _back;
    private int _width, _height;
    private bool _ready;

    /// <summary>Null until <see cref="Initialize"/> succeeds. Non-null and unchanging afterwards, so it can
    /// be reported once rather than polled.</summary>
    public string? Error { get; private set; }

    public bool IsReady => _ready;

    /// <summary>M249: the renderer, so a scene can be built into it. Exposed rather than wrapped because
    /// the builder needs the full surface and hiding it behind a facade would only duplicate it.</summary>
    public ShaderPreviewRenderer Renderer => _renderer;

    /// <summary>M249: what happened the last time a scene was built, and whether one is loaded at all.</summary>
    public string SceneReport { get; set; } = "";
    public bool HasScene { get; set; }

    /// <summary>The image to show. Swaps between two bitmaps so Avalonia is never compositing the one being
    /// written - a single bitmap tears under the compositor.</summary>
    public WriteableBitmap? Current { get; private set; }

    /// <summary>Last frame's cost and draw count, for the side-by-side comparison this step exists to make
    /// possible.</summary>
    public double LastFrameMs { get; private set; }
    public int LastDrawCalls { get; private set; }
    public int LastCulled { get; private set; }

    public bool Initialize()
    {
        if (_ready) return true;
        if (!_renderer.Initialize(out var err)) { Error = err ?? "D3D11 device creation failed"; return false; }

        // M249: only the fallback now - a real map replaces it. Kept because a recognisable shape makes a
        // wrong projection or a mirrored axis obvious at a glance, where an empty frame says nothing.
        _renderer.SetMesh(PreviewGeometry.CreateBuiltIn("Sphere"));
        _ready = true;
        return true;
    }

    /// <summary>Render one frame at the given pixel size using the editor camera. Returns false when there
    /// is nothing to show, in which case the caller should leave the previous image up rather than blank
    /// the viewport.</summary>
    public bool Render(OrbitCamera camera, int width, int height)
    {
        if (!_ready || width <= 0 || height <= 0) return false;

        var settings = new PreviewSettings
        {
            // The editor camera is authoritative. Supplying the matrices directly rather than copying
            // yaw/pitch/distance keeps the two viewports genuinely on the same camera - a reconstructed
            // one drifts, and a drifting camera makes an A/B comparison meaningless.
            SuppliedView = camera.View,
            SuppliedProjection = camera.Projection((float)width / height),
            SuppliedCameraPosition = camera.Position,

            // M240's verified map preset. Mirror X matters: League data is authored in the opposite
            // handedness, and without it the DX11 image is mirrored against the GL one, which would read
            // as a rendering bug rather than a convention difference.
            AlphaBlend = true,
            DepthTest = true,
            CullBackFaces = false,
            MirrorX = true,
            TransposeMatrices = true,
        };

        var t0 = DateTime.UtcNow;
        var pixels = _renderer.RenderFrame(width, height, settings, out _, null);
        LastFrameMs = (DateTime.UtcNow - t0).TotalMilliseconds;
        if (pixels is null) return false;

        LastDrawCalls = _renderer.DrawCalls;
        LastCulled = _renderer.CulledSlices;

        EnsureBitmaps(width, height);
        var target = ReferenceEquals(Current, _front) ? _back : _front;
        if (target is null) return false;

        using (var buf = target.Lock())
        {
            int stride = width * 4;
            for (int y = 0; y < height; y++)
                System.Runtime.InteropServices.Marshal.Copy(
                    pixels, y * stride, buf.Address + y * buf.RowBytes, stride);
        }
        Current = target;
        return true;
    }

    private void EnsureBitmaps(int width, int height)
    {
        if (_width == width && _height == height && _front is not null) return;
        _front?.Dispose(); _back?.Dispose();
        var size = new PixelSize(width, height);
        var dpi = new Avalonia.Vector(96, 96);
        _front = new WriteableBitmap(size, dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        _back = new WriteableBitmap(size, dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        Current = null;
        _width = width; _height = height;
    }

    public void Dispose()
    {
        _front?.Dispose(); _back?.Dispose();
        _front = _back = null; Current = null;
        _renderer.Dispose();
        _ready = false;
    }
}
