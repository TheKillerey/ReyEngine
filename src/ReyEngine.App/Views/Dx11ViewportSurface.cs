using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Rendering;
using ReyEngine.Core.Cinematics;
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
    private object? _visibilitySource = new();

    /// <summary>Null until <see cref="Initialize"/> succeeds. Non-null and unchanging afterwards, so it can
    /// be reported once rather than polled.</summary>
    public string? Error { get; private set; }

    public bool IsReady => _ready;

    /// <summary>M249: the renderer, so a scene can be built into it. Exposed rather than wrapped because
    /// the builder needs the full surface and hiding it behind a facade would only duplicate it.</summary>
    public ShaderPreviewRenderer Renderer => _renderer;

    /// <summary>
    /// <para>M292: apply the map's per-group visibility - dragon layer, baron state, render regions - to
    /// the D3D11 materials.</para>
    ///
    /// <para><paramref name="visible"/> is the SAME array the OpenGL viewport consumes
    /// (<c>MainWindowViewModel.CurrentModelSubmeshVisible</c>, produced by
    /// <c>MapVisibilityResolver</c>). None of those rules are re-implemented here: DX11 only has to map a
    /// material back to its group, which <c>PreviewMaterial.MapGroupIndex</c> carries. Anything that is
    /// not map geometry - particle emitters, mesh emitters, overlays - reports -1 and is left alone,
    /// because those own their Visible flag and a blanket sweep would fight them.</para>
    /// </summary>
    public void ApplyGroupVisibility(IReadOnlyList<bool>? visible)
    {
        if (ReferenceEquals(_visibilitySource, visible)) return;
        _visibilitySource = visible;
        foreach (var m in _renderer.Materials)
        {
            int g = m.MapGroupIndex;
            if (g < 0) continue;
            m.Visible = visible is null || g >= visible.Count || visible[g];
        }
    }

    /// <summary>M627: why the last frame produced nothing, or null when it produced a frame. The host is
    /// expected to SHOW this - a silent failed frame leaves a stale image on screen that looks live.</summary>
    public string? LastError { get; private set; }

    /// <summary>M249: what happened the last time a scene was built, and whether one is loaded at all.</summary>
    public string SceneReport { get; set; } = "";
    public bool HasScene { get; set; }

    /// <summary>M618: per-bone skinning matrices for this frame, or null for the bind pose. Set per frame
    /// by the character preview; every other host leaves it null and renders exactly as it always has.</summary>
    public System.Numerics.Matrix4x4[]? BonePalette { get; set; }

    /// <summary>M619: the animated skeleton, as the position pairs the GL overlay draws. Null clears it.</summary>
    public float[]? BoneLines { get; set; }

    /// <summary>M628: the target dummy's wire box, for a host with no dummy MODEL. Null clears it.</summary>
    public float[]? DummyLines { get; set; }

    /// <summary>M620: where the subject stands, for the character preview's control mode. Identity for
    /// the map viewport, which draws its geometry at the coordinates the data puts it.</summary>
    public System.Numerics.Matrix4x4 World { get; set; } = System.Numerics.Matrix4x4.Identity;

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

    /// <summary>M452: the editor's dynamic point lights, from the SAME view-model list the GL viewport is
    /// bound to (<c>DynamicLights</c>, rebuilt by <c>RepublishLights</c>). Until now this surface carried
    /// none of it, so lights that render in game and in GL were invisible the moment the user switched
    /// renderers. The fit/strength knobs below mirror the GL bindings one for one.</summary>
    public IReadOnlyList<ReyEngine.Formats.Lighting.PointLight>? Lights { get; set; }
    /// <summary>Mirrors <c>ShowDynamicLights</c> - the same gate GL applies via SetDynamicLightsEnabled.</summary>
    public bool ShowDynamicLights { get; set; }
    public double DynamicLightIntensity { get; set; } = 1.0;
    public double DynamicLightRadiusScale { get; set; } = 1.0;
    public double LightFalloffSoftness { get; set; }   // M457: 0 = Riot's linear falloff
    public double DynamicLightPositionScale { get; set; } = 1.0;
    public double DynamicLightScaleX { get; set; } = 1.0;
    public double DynamicLightScaleZ { get; set; } = 1.0;
    public double DynamicLightOffsetX { get; set; }
    public double DynamicLightOffsetZ { get; set; }

    /// <summary>M396: GRASS_INTERP for this frame, pushed from the view-model's running transition.
    /// 0 when nothing is fading, which is the correct resting value.</summary>
    public float GrassInterp { get; set; }

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
    /// <summary>M540: the viewport's "Cull" toggle, which never reached this surface at all.
    ///
    /// <para>The map host hardcoded CullBackFaces = false and pushed no per-frame value, so the toggle
    /// did nothing under D3D11 while GL honoured it (<c>cullBackfaces &amp;&amp; !s.DoubleSided</c>).
    /// Decals are single-sided by material, so they were culled here whatever the user asked for and
    /// simply were not on screen - which is also why D3D11 could not be used to look at the in-game
    /// transparency problem.</para></summary>
    public bool CullBackFaces { get; set; } = true;

    /// <summary>M559: false makes the renderer keep SUBMISSION order instead of grouping draws by
    /// pipeline. Driven by the Game Depth toggle, because emulating the client's depth mask without also
    /// emulating its ordering reproduces only half of what the client does - and the ordering half is the
    /// one a mapgeo change can actually fix.</summary>
    public bool SortByPipeline { get; set; } = true;

    public bool Wireframe { get; set; }

    /// <summary>M460: bind Riot's glow buffer as RT1, blur it with their own mip chain and composite it
    /// with their screen blend. On by default - the glow is already being computed by the shaders this
    /// viewport runs, and before this it was thrown away. See <c>PreviewSettings.Bloom</c>.</summary>
    public bool Bloom { get; set; } = true;

    /// <summary>M465: render the map from the sun into a depth map and let Riot's own 5-tap PCF sample it.
    /// On by default - the taps are already executing against a 1x1 white stand-in, so this supplies an
    /// input rather than adding an effect. See <c>PreviewSettings.Shadows</c>.</summary>
    public bool Shadows { get; set; } = true;

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

    private float _lastParticleTime = -1f;

    // ---- M605: cinematic capture ------------------------------------------------------------------
    //
    // A capture frame is the ordinary frame with four things taken over: the camera comes from the shot
    // rather than the editor, TIME comes from the frame index rather than the wall clock, the size is the
    // export size rather than the control's, and the result is returned instead of presented.
    //
    // Held as a FIELD rather than threaded through Render's signature because it has to reach four places
    // inside one synchronous call - the settings, the particle view, the particle tick and the tail - and
    // a four-parameter overload of a method this long is harder to read than a field that is set and
    // cleared within the same call. Never non-null across an await; RenderCaptureFrame is synchronous.
    private readonly record struct CaptureRequest(Matrix4x4 View, Matrix4x4 Projection, Vector3 Position, float TimeSeconds);

    private CaptureRequest? _capture;

    /// <summary>M607: fly the LIVE viewport along a shot while scrubbing. Same override as a capture but
    /// on the wall clock, so the preview is the frame that would be exported rather than an orbit camera
    /// approximating it - which is the only way roll is visible before the sequence comes back.</summary>
    public CinematicPose? PreviewPose { get; set; }

    /// <summary>
    /// Take the viewport out of live mode for the duration of a capture and put it back afterwards.
    ///
    /// <para>The particle clock is the reason this exists. <see cref="ParticleDelta"/> is a difference
    /// against the last tick, so walking the shot's timeline would otherwise hand the live viewport one
    /// enormous delta on its next frame - every particle system on the map jumping forward by however
    /// long the export took. Resetting it on the way in also gives the first captured frame a delta of
    /// zero rather than a jump from whenever the viewport last drew.</para>
    ///
    /// <para>Wireframe and the gizmo are cleared here because they are the two pieces of editor
    /// decoration this surface owns. The markers, icons and overlays belong to the view-model - Game Mode
    /// already hides that set and restores it, so a capture turns Game Mode on rather than growing a
    /// second switch that means the same thing.</para>
    /// </summary>
    public IDisposable BeginCapture()
    {
        var restore = new CaptureScope(this, _lastParticleTime, _frozenTime, Wireframe);
        _lastParticleTime = -1f;
        Wireframe = false;
        _renderer.SetGizmoLines(null, null, null);
        return restore;
    }

    private sealed class CaptureScope : IDisposable
    {
        private readonly Dx11ViewportSurface _surface;
        private readonly float _particleTime, _frozen;
        private readonly bool _wireframe;

        public CaptureScope(Dx11ViewportSurface surface, float particleTime, float frozen, bool wireframe)
        { _surface = surface; _particleTime = particleTime; _frozen = frozen; _wireframe = wireframe; }

        public void Dispose()
        {
            _surface._capture = null;
            _surface._lastParticleTime = _particleTime;
            _surface._frozenTime = _frozen;
            _surface.Wireframe = _wireframe;
        }
    }

    /// <summary>
    /// Render one frame of a cinematic shot and hand back its pixels as BGRA, top-down. Null when the
    /// surface is not ready or the renderer produced nothing.
    /// </summary>
    /// <param name="camera">The live camera, for the two things a free pose has no opinion about: its
    /// <c>Distance</c> drives the particle distance cull, and its near/far planes keep a captured frame
    /// clipping exactly as the preview does.</param>
    public byte[]? RenderCaptureFrame(CinematicPose pose, int width, int height, float timeSeconds, OrbitCamera camera)
    {
        if (!_ready || width <= 0 || height <= 0) return null;
        // The live camera's own near/far, so a captured frame clips exactly as the preview it was framed
        // in - see CinematicPose.Projection.
        _capture = new CaptureRequest(pose.ViewMatrix,
            pose.Projection((float)width / height, camera.EffectiveNear, camera.Far),
            pose.Position, timeSeconds);
        try { return Render(camera, width, height) ? LastPixels : null; }
        finally { _capture = null; }
    }

    /// <summary>
    /// <para>M266: seconds since the last particle tick, read off the SAME clock the TIME constant uses.</para>
    ///
    /// <para>That gives pausing the M263 meaning for particles too: the clock freezes, dt goes to 0 and
    /// <c>VfxParticleSimulator.Update</c> early-returns - but the quads are still rebuilt against the new
    /// camera basis, so orbiting a frozen effect works instead of leaving stale billboards facing an old
    /// camera. Unpausing produces one large delta, which the simulator's own <c>dt = Min(dt, 0.1f)</c>
    /// absorbs; that is the same clamp the GL viewport relies on for a hitched frame.</para>
    ///
    /// <para>This is an INTENTIONAL divergence: the GL map viewport never pauses particles, because its
    /// ParticlePaused property is only bound in the particle editor. The play/pause button is DX11-only, so
    /// the difference is only observable while a DX11-exclusive control is held - and its tooltip says so.</para>
    /// </summary>
    private float ParticleDelta(float now)
    {
        float dt = _lastParticleTime < 0f ? 0f : MathF.Max(0f, now - _lastParticleTime);
        _lastParticleTime = now;
        return dt;
    }

    /// <summary>M266: the map's particle driver, built lazily once a shader cache has been supplied - a
    /// viewport that never opens a map never creates one.</summary>
    public D3D11MapParticles? Particles { get; private set; }

    /// <summary>M295: the prop driver - placed meshes like Baron, the dragons and the jungle camps.
    /// Built alongside the particle driver and for the same reason: both need the shader cache, which the
    /// view-model opens only once a map is loaded.</summary>
    public D3D11MapProps? Props { get; private set; }

    /// <summary>The prop set to draw, or null for "props are off". Applied when the driver exists;
    /// remembered until then, exactly as the particle playback is.</summary>
    public PropRenderSet? PropMeshes
    {
        get => _propMeshes;
        set
        {
            if (ReferenceEquals(_propMeshes, value)) return;   // rebuilt only when the set really changes
            _propMeshes = value;
            Props?.Load(value);
        }
    }
    private PropRenderSet? _propMeshes;

    /// <summary>M295: play prop idle animations. Off leaves them in whatever pose they last held, which
    /// is bind pose until something ticks them.</summary>
    public bool PlayPropAnimations { get; set; }

    /// <summary>M266: the shader cache the particle pipelines resolve against, pushed from the view-model.
    /// The map scene builder already has one; particles need the same instance so the two share its
    /// pipeline cache.</summary>
    public ReyEngine.Formats.Shaders.ShaderCacheReader? ShaderCache { get; set; }

    private VfxPlayback? _particlePlayback;

    /// <summary>
    /// <para>M266: what to play, straight from <c>CurrentParticlePlayback</c> - the SAME property the GL
    /// viewport is bound to in XAML, so the two are gated identically.</para>
    ///
    /// <para>Compared by REFERENCE deliberately. Every mutation path builds a fresh VfxPlayback, so a
    /// reference change is exactly "something changed"; VfxPlayback and VfxPlaybackItem are records, so
    /// <c>==</c> would compare a Matrix4x4 for each of thousands of items on every frame.</para>
    /// </summary>
    public VfxPlayback? ParticlePlayback
    {
        get => _particlePlayback;
        set
        {
            if (ReferenceEquals(_particlePlayback, value)) return;
            _particlePlayback = value;
            Particles?.SetPlayback(value);
        }
    }

    /// <summary>M266: call after a scene build. <c>Dx11SceneBuilder.Commit</c> calls ClearMaterials, which
    /// disposes the particle materials and empties the texture pool along with the map's - so the retained
    /// playback has to be registered again, AFTER the commit.</summary>
    public void NotifySceneRebuilt()
    {
        _visibilitySource = new object();
        Particles?.Invalidate();
        // M295: a scene rebuild calls ClearMaterials, which takes the prop materials with it AND releases
        // the mesh geometry their handles point at. Reloading is not an optimisation here - without it the
        // handles would dangle into another scene's geometry list.
        Props?.Load(_propMeshes);
    }

    /// <summary>Last frame's particle counts, for the viewport's detail tooltip. Empty when nothing is
    /// playing, which is a state rather than a failure.</summary>
    public string ParticleStatus { get; private set; } = "";

    /// <summary>The first line of a build report that reads like a problem. The whole report is a
    /// per-system tally and far too long for a tooltip, but the reason nothing drew is worth one line.</summary>
    private static string FirstProblem(string report)
    {
        foreach (var line in report.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            if (t.Contains("no pipeline", StringComparison.OrdinalIgnoreCase)
                || t.Contains("unresolved", StringComparison.OrdinalIgnoreCase)
                || t.Contains("not in the shader cache", StringComparison.OrdinalIgnoreCase)
                || t.Contains("failed", StringComparison.OrdinalIgnoreCase))
                return t;
        }
        return report.Split('\n')[0].Trim();
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

    /// <summary>M623: scene materials that actually drew, and ones skipped as invisible - separately from
    /// DrawCalls, which also counts overlays.</summary>
    public int LastGeometryDraws { get; private set; }
    public int LastHidden { get; private set; }

    /// <summary>M466: the sun shadow pass's own counters, forwarded for the status detail.
    ///
    /// <para>M465 computed both and surfaced neither, which left the two ways to see no shadows
    /// indistinguishable from the app: a pass that bailed (no blobs, no sun direction, no caster bounds)
    /// looks exactly like a pass that ran and was then vetoed by the lightmap, because Riot's own combine is
    /// <c>min(realtime PCF, lightmap alpha)</c> — measured, defaultenv_flat blob 226 line 200. Zero draws
    /// says the first; a live radius with visible-nothing says the second.</para></summary>
    public int ShadowDraws { get; private set; }
    public float ShadowRadius { get; private set; }

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
        var frameClock = Stopwatch.StartNew();

        // Hoisted out of the settings initialiser because the particle tick below needs the same value:
        // one clock reading per frame, or the shader animation and the particles would drift apart.
        float t = _capture?.TimeSeconds ?? AnimationTime();

        // M295: prop idle animations, off the SAME clock as everything else in this viewport - so pausing
        // pauses props too. GL drives these from a dedicated stopwatch; that divergence is deliberate and
        // noted here rather than left to be discovered.
        Props?.Tick(t, PlayPropAnimations);

        _renderer.SetBoneLines(BoneLines);   // M619
        _renderer.SetDummyLines(DummyLines); // M628

        var settings = new PreviewSettings
        {
            // M618: null means the M216 bind-pose constant, which is what every non-character host wants.
            BonePalette = BonePalette,
            World = World,   // M620
            // The editor camera is authoritative. Supplying the matrices directly rather than copying
            // yaw/pitch/distance keeps the two viewports genuinely on the same camera - a reconstructed
            // one drifts, and a drifting camera makes an A/B comparison meaningless.
            SuppliedView = _capture?.View ?? PreviewPose?.ViewMatrix ?? camera.View,
            SuppliedProjection = _capture?.Projection
                ?? PreviewPose?.Projection((float)width / height, camera.EffectiveNear, camera.Far)
                ?? camera.Projection((float)width / height),
            SuppliedCameraPosition = _capture?.Position ?? PreviewPose?.Position ?? camera.Position,

            // M240's verified map preset. Mirror X matters: League data is authored in the opposite
            // handedness, and without it the DX11 image is mirrored against the GL one, which would read
            // as a rendering bug rather than a convention difference.
            AlphaBlend = true,
            DepthTest = true,
            CullBackFaces = CullBackFaces,   // M540: the viewport toggle, not a pinned false
            SortByPipeline = SortByPipeline,  // M559: off under Game Depth, so submission order stands
            MirrorX = true,
            TransposeMatrices = true,

            // M359: the same background the GL viewport clears to. This host set no clear colour at all,
            // so it fell back to the renderer's own default (0.08, 0.09, 0.11) - a lighter, greyer field
            // than GL's (0.039, 0.051, 0.075). Toggling between the two renderers to compare a map made
            // the whole image look shifted before a single pixel of geometry was drawn.
            //
            // A literal, matching GL's literal, because GL's clear is NOT the map's sky colour - it is a
            // hardcoded editor background (ViewportControl.cs, OnOpenGlRender). Deriving one here from
            // MapSunProperties would make the two viewports disagree, which is the opposite of the point.
            ClearColor = new System.Numerics.Vector4(0.039f, 0.051f, 0.075f, 1f),

            // M261. The sun and the lightmap scale are ungated, matching the GL path, which applies them
            // unconditionally. Fog is gated on the toggle, also matching it.
            TimeSeconds = t,
            Wireframe = Wireframe,
            Bloom = Bloom,
            Shadows = Shadows,   // M465
            MapSunColor = MapSun?.SunColor,
            MapSunDirection = MapSun?.SunDirection,
            MapLightMapScale = (float)LightmapScale,

            // M463: the whole record as well, for the two inputs that are not single constants - the
            // LightRegionInfo structured buffer (Mantis reads its SUN from there, not from
            // SUN_LIGHT_COLOR) and the IBL ambient pair the sky drives. Ungated like the sun above.
            MapSun = MapSun,

            // M396: the environment crossfade. Riot's shaders do
            // lerp(GRASS_TINT_MAP, GRASS_TINT_MAP_ALTERNATE, GRASS_INTERP) themselves, so passing this
            // through is the whole of the transition here.
            GrassInterp = GrassInterp,

            // RAW fogStartAndEnd, not TryGetFogRange's normalised (near, far). Riot ships these negative
            // and reversed and the shader consumes them unmodified - the GL path normalises only because
            // its fog is our own reimplementation. Normalising here would put the fog cliff in the wrong
            // place rather than fail loudly.
            MapFogColor = FogEnabled ? MapSun?.FogColor : null,
            MapFogStartEnd = FogEnabled ? MapSun?.FogStartAndEnd : null,

            // M452: the point-light overlay. Ungated here beyond the toggle - the renderer applies the
            // same clamps the GL setters do, so the two viewports see identical values.
            DynamicLights = Lights,
            DynamicLightsEnabled = ShowDynamicLights,
            DynamicLightIntensity = (float)DynamicLightIntensity,
            DynamicLightRadiusScale = (float)DynamicLightRadiusScale,
            DynamicLightFalloffSoftness = (float)LightFalloffSoftness,
            DynamicLightPositionScale = (float)DynamicLightPositionScale,
            DynamicLightPositionScaleXZ = new Vector2((float)DynamicLightScaleX, (float)DynamicLightScaleZ),
            DynamicLightPositionOffset = new Vector2((float)DynamicLightOffsetX, (float)DynamicLightOffsetZ),
        };

        // M266: advance the particles and refill the dynamic buffer, HERE and not in the host window.
        //
        // Two reasons this placement is load-bearing. First, RenderFrame is what reads the dynamic index
        // count that UpdateDynamicMesh writes, so ticking after it would draw last frame's quads - a
        // one-frame lag that reads as particles trailing the camera. Second, the mirror-inclusive view is
        // only knowable here: RenderFrame applies the -X mirror itself, from the same MirrorX flag set
        // above, and reconstructing that anywhere else would be a second source of truth for the one thing
        // hardest to get right.
        EnsureParticles();
        // Gated on the DRIVER existing, not on it having a playback. SetPlayback(null) only marks the
        // driver dirty - the teardown that calls RemoveMaterials lives in Tick's Rebuild - so gating on
        // HasPlayback made retraction unreachable: switching Play All off left the last frame's quads
        // registered, Visible, and pointing into a dynamic buffer nobody rewrites, painted over the map
        // forever while the status line was blanked. Tick already returns immediately once Rebuild has
        // run against a null playback, so this costs nothing when there is nothing to draw.
        if (Particles is not null)
        {
            var particleView = _capture?.View ?? PreviewPose?.ViewMatrix ?? camera.View;
            if (settings.MirrorX) particleView = Matrix4x4.CreateScale(-1f, 1f, 1f) * particleView;
            Particles.Tick(ParticleDelta(t), particleView,
                particleView * settings.SuppliedProjection!.Value,
                _capture?.Position ?? PreviewPose?.Position ?? camera.Position, camera.Distance);
            ParticleStatus = Particles.FrameReport();
            // Surface the BUILD report too, but only when it names a failure. It records unresolved
            // sprites, emitters whose permutation would not resolve, and a missing shader TOC - and it
            // had no consumer anywhere in the app, so any of those showed up as "0 slices" with no
            // reason given. The per-frame line above cannot explain why a system is absent; this can.
            if (Particles.DrawSlices == 0 && Particles.BuildReport.Length > 0)
                ParticleStatus = (ParticleStatus.Length > 0 ? ParticleStatus + "\n" : "")
                                 + FirstProblem(Particles.BuildReport);
        }
        else ParticleStatus = "";

        // M255: collect the constants nothing supplied. This report is what solved M229, M230 and M235,
        // and the viewport path was built without it - so it has been resolving scenes blind.
        _unbound.Clear();
        // M627: KEEP the reason. This was `out _`, and RenderFrame catches every exception internally, so
        // a frame that failed for any reason produced exactly the same observable state as a frame that
        // was never asked for: false, no log, and Current still holding the last good bitmap. That is why
        // "the gizmo draws but nothing responds" was undiagnosable - the picture on screen was minutes old.
        var pixels = _renderer.RenderFrame(width, height, settings, out var renderError, _unbound);
        LastError = pixels is null ? renderError ?? "RenderFrame returned nothing and gave no reason" : null;
        if (pixels is null) return false;

        LastPixels = pixels;
        LastDrawCalls = _renderer.DrawCalls;
        LastGeometryDraws = _renderer.GeometryDraws;   // M623
        LastHidden = _renderer.HiddenSlices;
        LastCulled = _renderer.CulledSlices;
        ShadowDraws = _renderer.ShadowDraws;       // M466
        ShadowRadius = _renderer.ShadowRadius;

        // A capture is read back, not shown. Presenting it would reallocate both display bitmaps at the
        // export size - two 4K buffers per frame - and leave Current pointing at one the control is not
        // sized for.
        if (_capture is not null) { LastFrameMs = frameClock.Elapsed.TotalMilliseconds; return true; }

        EnsureBitmaps(width, height);
        var target = ReferenceEquals(Current, _front) ? _back : _front;
        if (target is null) return false;

        using (var buf = target.Lock())
        {
            int stride = width * 4;
            if (buf.RowBytes == stride)
                System.Runtime.InteropServices.Marshal.Copy(pixels, 0, buf.Address, pixels.Length);
            else
                for (int y = 0; y < height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(
                        pixels, y * stride, buf.Address + y * buf.RowBytes, stride);
        }
        Current = target;
        LastFrameMs = frameClock.Elapsed.TotalMilliseconds;
        return true;
    }

    /// <summary>Build the particle driver the first time a shader cache is available, and hand it whatever
    /// playback was set while it did not exist yet. Deferred rather than constructed with the surface
    /// because the cache is opened by the view-model when a map is loaded, which can be long after this
    /// surface starts drawing its fallback sphere.</summary>
    private void EnsureParticles()
    {
        if (ShaderCache is null) return;
        if (Particles is null)
        {
            Particles = new D3D11MapParticles(_renderer, ShaderCache);
            if (_particlePlayback is not null) Particles.SetPlayback(_particlePlayback);
        }
        if (Props is null)
        {
            Props = new D3D11MapProps(_renderer, ShaderCache);
            if (_propMeshes is not null) Props.Load(_propMeshes);
        }
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
