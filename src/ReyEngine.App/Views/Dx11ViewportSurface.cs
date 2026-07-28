using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ReyEngine.Formats.MapGeo;
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

    /// <summary>
    /// <para>M261: the map's own lighting, from the same source the GL viewport binds to
    /// (<c>CurrentSunProperties</c>). Until now this surface supplied NONE of it, so every scene rendered
    /// against the renderer's fallback sun and the no-map-fog path - which is why the sun looked wrong and
    /// the fog looked absent while the preview window, which does supply them, looked right.</para>
    ///
    /// <para>Null is meaningful: it selects the same neutral fallbacks the preview uses when no map is
    /// loaded, rather than zeros. An unbound constant reads as zero and zero is black - see M229.</para>
    /// </summary>
    public MapSunProperties? MapSun { get; set; }

    /// <summary>Mirrors the GL viewport's <c>FogEnabled</c>, itself bound to <c>ShowFog</c>. Gated the same
    /// way on purpose: if the two viewports disagreed about whether fog is on, the A/B diff would report a
    /// difference that is a UI setting rather than a rendering one.</summary>
    public bool FogEnabled { get; set; }

    /// <summary>Mirrors the GL viewport's <c>LightmapScale</c> (<c>CurrentLightmapScale</c>).</summary>
    public double LightmapScale { get; set; } = 1.0;

    /// <summary>
    /// <para>Drives the <c>TIME</c> constant, which is what animates scrolling UVs, flipbooks and flow.</para>
    ///
    /// <para>Deliberately NOT gated on <c>HasFlowmapWater</c> the way the GL path's <c>AnimateWater</c> is.
    /// That gate is a property of our own GL shader, where TIME only ever fed the water; Riot's shaders use
    /// it for far more, so gating on water would leave most animated materials frozen. This also matches
    /// the "Animate TIME = On" map preset from M240.</para>
    /// </summary>
    public bool AnimateTime { get; set; } = true;

    /// <summary>M263: wireframe, from the toolbar's Wire toggle. The renderer has always supported it;
    /// nothing was passing it.</summary>
    public bool Wireframe { get; set; }

    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private float _frozenTime;

    /// <summary>M263: the clock the TIME constant reads. Pausing FREEZES it rather than sending zero -
    /// zeroing would snap every animated material back to its first frame, which is a different thing from
    /// pausing and makes the button useless for looking at a moment.</summary>
    private float AnimationTime()
    {
        if (AnimateTime) _frozenTime = (float)_clock.Elapsed.TotalSeconds;
        return _frozenTime;
    }

    /// <summary>The image to show. Swaps between two bitmaps so Avalonia is never compositing the one being
    /// written - a single bitmap tears under the compositor.</summary>
    public WriteableBitmap? Current { get; private set; }

    /// <summary>Last frame's cost and draw count, for the side-by-side comparison this step exists to make
    /// possible.</summary>
    /// <summary>
    /// <para>M252: the raw BGRA of the last frame, for the A/B diff. Same layout the GL capture
    /// normalises to.</para>
    ///
    /// <para><b>Aliases the renderer's reused readback buffer</b>, so it is only valid until the next
    /// <see cref="Render"/>. The A/B path is safe because it renders and compares back to back on the UI
    /// thread with no await between them - but anything that holds this across a frame boundary must copy
    /// it first. See the note on <c>ShaderPreviewRenderer.RenderFrame</c>.</para>
    /// </summary>
    public byte[]? LastPixels { get; private set; }

    private readonly List<string> _unbound = new();

    /// <summary>Constants the last frame declared but nothing wrote. Distinct, because a scene with 1,389
    /// slices repeats the same name once per draw.</summary>
    public IReadOnlyList<string> UnboundConstants => _unbound.Distinct().ToList();

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

            // M261. The sun and the lightmap scale are ungated, matching the GL path, which applies them
            // unconditionally. Fog is gated on the toggle, also matching it.
            TimeSeconds = AnimationTime(),
            Wireframe = Wireframe,
            MapSunColor = MapSun?.SunColor,
            MapSunDirection = MapSun?.SunDirection,
            MapLightMapScale = (float)LightmapScale,

            // RAW fogStartAndEnd, not TryGetFogRange's normalised (near, far). Riot ships these negative
            // and reversed and the shader consumes them unmodified - the GL path normalises only because
            // its fog is our own reimplementation. Normalising here would put the fog cliff in the wrong
            // place rather than fail loudly.
            MapFogColor = FogEnabled ? MapSun?.FogColor : null,
            MapFogStartEnd = FogEnabled ? MapSun?.FogStartAndEnd : null,
        };

        var t0 = DateTime.UtcNow;
        // M255: collect the constants nothing supplied. This report is what solved M229, M230 and M235,
        // and the viewport path was built without it - so it has been resolving scenes blind.
        _unbound.Clear();
        var pixels = _renderer.RenderFrame(width, height, settings, out _, _unbound);
        LastFrameMs = (DateTime.UtcNow - t0).TotalMilliseconds;
        if (pixels is null) return false;

        LastPixels = pixels;
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
