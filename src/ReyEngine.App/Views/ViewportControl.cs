using System.Numerics;
using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using ReyEngine.Core.Decoding;
using ReyEngine.Formats.Animation;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Skeletons;
using ReyEngine.App.ViewModels;
using ReyEngine.Rendering;
using ReyEngine.Rendering.Vfx;
using Silk.NET.OpenGL;

namespace ReyEngine.App.Views;

/// <summary>
/// OpenGL viewport: grid backdrop + selected mesh (solid/wireframe) with optional
/// bounding box and skeleton overlays. Mesh uploads + auto-framing happen on the GL
/// thread. Camera input is driven externally (a transparent overlay forwards pointer
/// events here, because a bare OpenGlControlBase is not hit-testable).
/// </summary>
public sealed class ViewportControl : OpenGlControlBase
{
    public static readonly StyledProperty<MeshAsset?> MeshProperty =
        AvaloniaProperty.Register<ViewportControl, MeshAsset?>(nameof(Mesh));
    public static readonly StyledProperty<SkeletonAsset?> SkeletonProperty =
        AvaloniaProperty.Register<ViewportControl, SkeletonAsset?>(nameof(Skeleton));
    public static readonly StyledProperty<bool> WireframeProperty =
        AvaloniaProperty.Register<ViewportControl, bool>(nameof(Wireframe));
    public static readonly StyledProperty<bool> ShowBonesProperty =
        AvaloniaProperty.Register<ViewportControl, bool>(nameof(ShowBones));
    public static readonly StyledProperty<bool> ShowBoundsProperty =
        AvaloniaProperty.Register<ViewportControl, bool>(nameof(ShowBounds));
    public static readonly StyledProperty<bool> CullBackfacesProperty =
        AvaloniaProperty.Register<ViewportControl, bool>(nameof(CullBackfaces));
    public static readonly StyledProperty<IReadOnlyList<Vector3>?> ParticleMarkersProperty =
        AvaloniaProperty.Register<ViewportControl, IReadOnlyList<Vector3>?>(nameof(ParticleMarkers));
    public static readonly StyledProperty<Vector3?> SelectedParticlePositionProperty =
        AvaloniaProperty.Register<ViewportControl, Vector3?>(nameof(SelectedParticlePosition));
    public static readonly StyledProperty<VfxPlayback?> ParticlePlaybackProperty =
        AvaloniaProperty.Register<ViewportControl, VfxPlayback?>(nameof(ParticlePlayback));
    public static readonly StyledProperty<Vector3?> FocusPointProperty =
        AvaloniaProperty.Register<ViewportControl, Vector3?>(nameof(FocusPoint));
    public static readonly StyledProperty<IReadOnlyList<TextureImage?>?> ModelTexturesProperty =
        AvaloniaProperty.Register<ViewportControl, IReadOnlyList<TextureImage?>?>(nameof(ModelTextures));
    public static readonly StyledProperty<IReadOnlyList<TextureImage?>?> ModelMaskTexturesProperty =
        AvaloniaProperty.Register<ViewportControl, IReadOnlyList<TextureImage?>?>(nameof(ModelMaskTextures));
    public static readonly StyledProperty<IReadOnlyList<TextureImage?>?> ModelGradientTexturesProperty =
        AvaloniaProperty.Register<ViewportControl, IReadOnlyList<TextureImage?>?>(nameof(ModelGradientTextures));
    public static readonly StyledProperty<IReadOnlyList<TextureImage?>?> ModelEmissiveTexturesProperty =
        AvaloniaProperty.Register<ViewportControl, IReadOnlyList<TextureImage?>?>(nameof(ModelEmissiveTextures));
    public static readonly StyledProperty<IReadOnlyList<TextureImage?>?> ModelMatCapTexturesProperty =
        AvaloniaProperty.Register<ViewportControl, IReadOnlyList<TextureImage?>?>(nameof(ModelMatCapTextures));
    public static readonly StyledProperty<IReadOnlyList<TextureImage?>?> ModelMatCapMaskTexturesProperty =
        AvaloniaProperty.Register<ViewportControl, IReadOnlyList<TextureImage?>?>(nameof(ModelMatCapMaskTextures));
    public static readonly StyledProperty<IReadOnlyList<TextureImage?>?> ModelLightmapTexturesProperty =
        AvaloniaProperty.Register<ViewportControl, IReadOnlyList<TextureImage?>?>(nameof(ModelLightmapTextures));
    public static readonly StyledProperty<IReadOnlyList<bool>?> ModelSubmeshVisibleProperty =
        AvaloniaProperty.Register<ViewportControl, IReadOnlyList<bool>?>(nameof(ModelSubmeshVisible));
    public static readonly StyledProperty<IReadOnlyList<ViewportMeshRenderer.SubmeshMaterial>?> ModelSubmeshMaterialsProperty =
        AvaloniaProperty.Register<ViewportControl, IReadOnlyList<ViewportMeshRenderer.SubmeshMaterial>?>(nameof(ModelSubmeshMaterials));
    public static readonly StyledProperty<int> MeshVerticesRevisionProperty =
        AvaloniaProperty.Register<ViewportControl, int>(nameof(MeshVerticesRevision));
    public static readonly StyledProperty<AnimationClip?> AnimationClipProperty =
        AvaloniaProperty.Register<ViewportControl, AnimationClip?>(nameof(AnimationClip));
    public static readonly StyledProperty<double> AnimationTimeProperty =
        AvaloniaProperty.Register<ViewportControl, double>(nameof(AnimationTime));
    public static readonly StyledProperty<int> PreviewModeProperty =
        AvaloniaProperty.Register<ViewportControl, int>(nameof(PreviewMode));
    public static readonly StyledProperty<IReadOnlyList<(Vector3 min, Vector3 max)>?> SelectionBoxesProperty =
        AvaloniaProperty.Register<ViewportControl, IReadOnlyList<(Vector3 min, Vector3 max)>?>(nameof(SelectionBoxes));
    public static readonly StyledProperty<Vector3?> GroupBoundsMinProperty =
        AvaloniaProperty.Register<ViewportControl, Vector3?>(nameof(GroupBoundsMin));
    public static readonly StyledProperty<Vector3?> GroupBoundsMaxProperty =
        AvaloniaProperty.Register<ViewportControl, Vector3?>(nameof(GroupBoundsMax));
    public static readonly StyledProperty<Vector3?> GizmoPivotProperty =
        AvaloniaProperty.Register<ViewportControl, Vector3?>(nameof(GizmoPivot));

    public int PreviewMode { get => GetValue(PreviewModeProperty); set => SetValue(PreviewModeProperty, value); }
    /// <summary>World-space bounds of every selected map mesh, for the amber highlight boxes.</summary>
    public IReadOnlyList<(Vector3 min, Vector3 max)>? SelectionBoxes { get => GetValue(SelectionBoxesProperty); set => SetValue(SelectionBoxesProperty, value); }
    /// <summary>Combined bounds of a multi-selection (the dimmer group box); null for single/empty selection.</summary>
    public Vector3? GroupBoundsMin { get => GetValue(GroupBoundsMinProperty); set => SetValue(GroupBoundsMinProperty, value); }
    public Vector3? GroupBoundsMax { get => GetValue(GroupBoundsMaxProperty); set => SetValue(GroupBoundsMaxProperty, value); }
    /// <summary>World-space center of the selection — the translate-gizmo origin (null = no selection).</summary>
    public Vector3? GizmoPivot { get => GetValue(GizmoPivotProperty); set => SetValue(GizmoPivotProperty, value); }
    public AnimationClip? AnimationClip { get => GetValue(AnimationClipProperty); set => SetValue(AnimationClipProperty, value); }
    public double AnimationTime { get => GetValue(AnimationTimeProperty); set => SetValue(AnimationTimeProperty, value); }
    public IReadOnlyList<TextureImage?>? ModelTextures { get => GetValue(ModelTexturesProperty); set => SetValue(ModelTexturesProperty, value); }
    public IReadOnlyList<TextureImage?>? ModelMaskTextures { get => GetValue(ModelMaskTexturesProperty); set => SetValue(ModelMaskTexturesProperty, value); }
    public IReadOnlyList<TextureImage?>? ModelGradientTextures { get => GetValue(ModelGradientTexturesProperty); set => SetValue(ModelGradientTexturesProperty, value); }
    public IReadOnlyList<TextureImage?>? ModelEmissiveTextures { get => GetValue(ModelEmissiveTexturesProperty); set => SetValue(ModelEmissiveTexturesProperty, value); }
    public IReadOnlyList<TextureImage?>? ModelMatCapTextures { get => GetValue(ModelMatCapTexturesProperty); set => SetValue(ModelMatCapTexturesProperty, value); }
    public IReadOnlyList<TextureImage?>? ModelMatCapMaskTextures { get => GetValue(ModelMatCapMaskTexturesProperty); set => SetValue(ModelMatCapMaskTexturesProperty, value); }
    public IReadOnlyList<TextureImage?>? ModelLightmapTextures { get => GetValue(ModelLightmapTexturesProperty); set => SetValue(ModelLightmapTexturesProperty, value); }
    public IReadOnlyList<bool>? ModelSubmeshVisible { get => GetValue(ModelSubmeshVisibleProperty); set => SetValue(ModelSubmeshVisibleProperty, value); }
    public IReadOnlyList<ViewportMeshRenderer.SubmeshMaterial>? ModelSubmeshMaterials { get => GetValue(ModelSubmeshMaterialsProperty); set => SetValue(ModelSubmeshMaterialsProperty, value); }
    public int MeshVerticesRevision { get => GetValue(MeshVerticesRevisionProperty); set => SetValue(MeshVerticesRevisionProperty, value); }
    public MeshAsset? Mesh { get => GetValue(MeshProperty); set => SetValue(MeshProperty, value); }
    public SkeletonAsset? Skeleton { get => GetValue(SkeletonProperty); set => SetValue(SkeletonProperty, value); }
    public bool Wireframe { get => GetValue(WireframeProperty); set => SetValue(WireframeProperty, value); }
    public bool CullBackfaces { get => GetValue(CullBackfacesProperty); set => SetValue(CullBackfacesProperty, value); }
    /// <summary>World positions of placed-particle markers to draw (M35); null/empty hides them.</summary>
    public IReadOnlyList<Vector3>? ParticleMarkers { get => GetValue(ParticleMarkersProperty); set => SetValue(ParticleMarkersProperty, value); }
    public Vector3? SelectedParticlePosition { get => GetValue(SelectedParticlePositionProperty); set => SetValue(SelectedParticlePositionProperty, value); }
    /// <summary>Set to a world point to recentre the camera on it (M35 focus); cleared after applying.</summary>
    public Vector3? FocusPoint { get => GetValue(FocusPointProperty); set => SetValue(FocusPointProperty, value); }
    /// <summary>The placed VFX system to simulate and play live (M36); null stops playback.</summary>
    public VfxPlayback? ParticlePlayback { get => GetValue(ParticlePlaybackProperty); set => SetValue(ParticlePlaybackProperty, value); }
    public bool ShowBones { get => GetValue(ShowBonesProperty); set => SetValue(ShowBonesProperty, value); }
    public bool ShowBounds { get => GetValue(ShowBoundsProperty); set => SetValue(ShowBoundsProperty, value); }

    private GL? _gl;
    private bool _gles;
    private GridRenderer? _grid;
    private ViewportMeshRenderer? _meshRenderer;
    private readonly OrbitCamera _camera = new();
    private bool _meshDirty, _bonesDirty, _needFrame, _texturesDirty, _skinDirty, _wasAnimating, _visibilityDirty, _verticesDirty, _materialsDirty;
    private bool _particlesDirty;
    private Vector3? _pendingFocus;

    // M36: live VFX particle playback
    private VfxParticleRenderer? _particleRenderer;
    private VfxParticleSimulator? _particleSim;
    private bool _particlePlaybackDirty;
    private uint _softDotTex;
    private readonly System.Diagnostics.Stopwatch _particleClock = new();

    // Offscreen target with a real depth buffer (Avalonia's default FBO has none).
    private uint _fbo, _colorRb, _depthRb;
    private int _fboW, _fboH;

    // ---- Public camera API (driven by the input overlay) ----

    public void OrbitBy(float dx, float dy)
    {
        _camera.Orbit(dx * 0.01f, dy * 0.01f);
        RequestNextFrameRendering();
    }

    /// <summary>LMB mouse-look (rotate the view in place). Direct mapping: cursor up→look up, left→look left.</summary>
    public void LookBy(float dx, float dy)
    {
        _camera.Look(-dx * 0.005f, dy * 0.005f);
        RequestNextFrameRendering();
    }

    /// <summary>WASD/QE fly step (forward/right/up in [-1..1], dt seconds).</summary>
    public void FlyBy(float forward, float right, float up, float dt)
    {
        _camera.MoveLocal(forward, right, up, dt);
        RequestNextFrameRendering();
    }

    public void AdjustFlySpeed(float wheelDelta)
    {
        _camera.AdjustFlySpeed(wheelDelta > 0 ? 1.15f : 0.87f);
        RequestNextFrameRendering();
    }

    public void PanBy(float dx, float dy)
    {
        _camera.Pan(-dx, dy);
        RequestNextFrameRendering();
    }

    public void ZoomBy(float wheelDelta)
    {
        _camera.Zoom(wheelDelta > 0 ? 0.9f : 1.1f);
        RequestNextFrameRendering();
    }

    /// <summary>F: frame the current mesh (Unreal-style focus-selected).</summary>
    public void FocusSelected() => RequestFrame();

    // ---- Translate gizmo picking (screen point → axis / drag amount) ----
    // Uses the exact viewProj/size/camera state from the most recent render, so hit-testing always
    // matches what's on screen even mid-drag while the camera or gizmo arm length is unchanged.

    public enum GizmoAxis { X, Y, Z }

    private Matrix4x4 _lastViewProj = Matrix4x4.Identity;
    private double _lastViewportW = 1, _lastViewportH = 1;
    private Vector3 _lastCamPos;
    private const float GizmoHitPixels = 10f;

    private static Vector3 AxisDir(GizmoAxis axis) => axis switch
    {
        GizmoAxis.X => Vector3.UnitX,
        GizmoAxis.Y => Vector3.UnitY,
        _ => Vector3.UnitZ,
    };

    /// <summary>Which gizmo axis (if any) is under the given control-relative point, within a pixel threshold.</summary>
    public GizmoAxis? HitTestGizmoAxis(Point screenPos)
    {
        if (GizmoPivot is not { } pivot || _lastViewportW <= 0 || _lastViewportH <= 0) return null;
        float armLength = GizmoArmLength(pivot);
        if (armLength <= 0f) return null;

        GizmoAxis? best = null;
        float bestDist = GizmoHitPixels;
        foreach (var axis in new[] { GizmoAxis.X, GizmoAxis.Y, GizmoAxis.Z })
        {
            if (!ViewportPicking.ProjectToScreen(pivot, _lastViewProj, _lastViewportW, _lastViewportH, out var p0)) continue;
            if (!ViewportPicking.ProjectToScreen(pivot + AxisDir(axis) * armLength, _lastViewProj, _lastViewportW, _lastViewportH, out var p1)) continue;
            float d = DistancePointToSegment(new Vector2((float)screenPos.X, (float)screenPos.Y), p0, p1);
            if (d < bestDist) { bestDist = d; best = axis; }
        }
        return best;
    }

    /// <summary>
    /// Where along a world-space axis line the given screen point projects to. The line origin is
    /// passed explicitly and MUST stay fixed for the whole drag (using the live gizmo pivot would
    /// re-anchor the line every frame the mesh moves — a feedback loop that oscillates the mesh).
    /// </summary>
    public bool TryGetAxisParameter(GizmoAxis axis, Point screenPos, Vector3 lineOrigin, out float t)
    {
        t = 0f;
        if (!ViewportPicking.TryGetRay(new Vector2((float)screenPos.X, (float)screenPos.Y), _lastViewProj, _lastViewportW, _lastViewportH, out var rayOrigin, out var rayDir))
            return false;
        t = ViewportPicking.ClosestParameterOnLine(rayOrigin, rayDir, lineOrigin, AxisDir(axis));
        return true;
    }

    /// <summary>World-space pick ray under a control-relative point (for click-to-select), derived from
    /// the exact matrices used to render the last frame.</summary>
    public bool TryGetPickRay(Point screenPos, out Vector3 rayOrigin, out Vector3 rayDir)
        => ViewportPicking.TryGetRay(new Vector2((float)screenPos.X, (float)screenPos.Y),
            _lastViewProj, _lastViewportW, _lastViewportH, out rayOrigin, out rayDir);

    private float GizmoArmLength(Vector3 pivot) => Math.Clamp(Vector3.Distance(_lastCamPos, pivot) * 0.15f, 10f, 5000f);

    private static float DistancePointToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        float t = len2 < 1e-6f ? 0f : Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }

    public void RequestFrame()
    {
        _needFrame = true;
        RequestNextFrameRendering();
    }

    // ---- GL lifecycle ----

    protected override void OnOpenGlInit(GlInterface gl)
    {
        _gl = GL.GetApi(gl.GetProcAddress);
        _gles = ShaderUtil.DetectGles(_gl);

        _grid = new GridRenderer();
        _grid.Initialize(_gl, _gles);
        _meshRenderer = new ViewportMeshRenderer();
        _meshRenderer.Initialize(_gl, _gles);

        _particleRenderer = new VfxParticleRenderer();
        _particleRenderer.Initialize(_gl);
        _softDotTex = _particleRenderer.UploadTexture(SoftDot(64), 64, 64);

        if (Mesh is not null) { _meshDirty = true; }
        if (Skeleton is not null) _bonesDirty = true;
        RequestNextFrameRendering();
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _grid?.Dispose();
        _meshRenderer?.Dispose();
        _particleRenderer?.Dispose();
        DeleteFbo();
        _grid = null;
        _meshRenderer = null;
        _particleRenderer = null;
        _particleSim = null;
        _gl = null;
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_gl is null || _grid is null || _meshRenderer is null) return;

        if (_meshDirty)
        {
            if (Mesh is { } m)
            {
                var subs = m.SubMeshes.Select(s => (s.StartIndex, s.IndexCount)).ToList();
                _meshRenderer.SetMesh(m.Positions, m.Normals, m.Uvs, m.Indices, m.VertexCount, m.BoundsMin, m.BoundsMax, subs, m.Colors, m.LightmapUvs);
                _needFrame = true;
                _texturesDirty = true;
                _skinDirty = true;
                _visibilityDirty = true;
                _materialsDirty = true;
            }
            else _meshRenderer.ClearMesh();
            _meshDirty = false;
        }
        if (_texturesDirty && _meshRenderer.HasMesh)
        {
            // Upload each unique image once (shared across all layers); submeshes sharing an image share its GL id.
            var uploaded = new Dictionary<TextureImage, uint>(ReferenceEqualityComparer.Instance);
            UploadLayer(ModelTextures, 0, uploaded);            // diffuse
            UploadLayer(ModelMaskTextures, 1, uploaded);        // mask
            UploadLayer(ModelGradientTextures, 2, uploaded);    // gradient
            UploadLayer(ModelEmissiveTextures, 3, uploaded);    // emissive
            UploadLayer(ModelMatCapTextures, 4, uploaded);      // matcap
            UploadLayer(ModelMatCapMaskTextures, 5, uploaded);  // matcap mask
            UploadLayer(ModelLightmapTextures, 6, uploaded);    // baked lightmap atlas
            _texturesDirty = false;
        }
        if (_visibilityDirty && _meshRenderer.HasMesh)
        {
            var vis = ModelSubmeshVisible;
            for (int i = 0; i < _meshRenderer.SubmeshCount; i++)
                _meshRenderer.SetSubmeshVisible(i, vis is null || i >= vis.Count || vis[i]);
            _visibilityDirty = false;
        }
        if (_materialsDirty && _meshRenderer.HasMesh)
        {
            var mats = ModelSubmeshMaterials;
            if (mats is null) _meshRenderer.ClearSubmeshMaterials();
            else
                for (int i = 0; i < _meshRenderer.SubmeshCount; i++)
                    _meshRenderer.SetSubmeshMaterial(i, i < mats.Count ? mats[i] : ViewportMeshRenderer.SubmeshMaterial.Default);
            _materialsDirty = false;
        }
        if (_verticesDirty && _meshRenderer.HasMesh)
        {
            if (Mesh is { } moved) _meshRenderer.UpdateVertices(moved.Positions, moved.Normals);
            _verticesDirty = false;
        }
        if (_bonesDirty)
        {
            _meshRenderer.SetBoneSegments(Skeleton is null ? null : BuildBoneSegments(Skeleton));
            _bonesDirty = false;
        }
        if (_skinDirty)
        {
            ApplySkinning();
            _skinDirty = false;
        }
        if (_particlesDirty)
        {
            var pts = ParticleMarkers ?? (IReadOnlyList<Vector3>)Array.Empty<Vector3>();
            float size = Mesh is { } pm ? Math.Clamp(pm.Radius * 0.012f, 8f, 400f) : 40f;
            _meshRenderer.SetParticleMarkers(pts, SelectedParticlePosition, size);
            _particlesDirty = false;
        }
        if (_particlePlaybackDirty) { RebuildParticleSim(); _particlePlaybackDirty = false; }
        if (_needFrame) { FrameCamera(); _needFrame = false; }
        if (_pendingFocus is { } fp) { FocusOnPoint(fp); _pendingFocus = null; }

        float scale = (float)(VisualRoot?.RenderScaling ?? 1.0);
        uint w = (uint)Math.Max(1, Bounds.Width * scale);
        uint h = (uint)Math.Max(1, Bounds.Height * scale);

        EnsureFbo(w, h);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

        _gl.Viewport(0, 0, w, h);
        _gl.ClearColor(0.039f, 0.051f, 0.075f, 1f);
        _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Lequal);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        float aspect = h == 0 ? 1f : (float)w / h;
        // League's engine is -X oriented; mirror world X so assets match their in-game orientation.
        var viewProj = Matrix4x4.CreateScale(-1f, 1f, 1f) * _camera.ViewProjection(aspect);

        // Cache the exact matrices/size used for THIS frame so gizmo hit-testing (driven by pointer
        // events, outside the render loop) always matches what's actually on screen. The eye is stored
        // in MESH-DATA space: vertices get X-mirrored before the view matrix, so the camera effectively
        // sits at (-x, y, z) relative to the un-mirrored data the pivots/bounds live in.
        _lastViewProj = viewProj;
        _lastViewportW = Bounds.Width;
        _lastViewportH = Bounds.Height;
        _lastCamPos = new Vector3(-_camera.Position.X, _camera.Position.Y, _camera.Position.Z);

        _meshRenderer.SetHighlightBoxes(SelectionBoxes ?? Array.Empty<(Vector3, Vector3)>());
        _meshRenderer.SetGroupBounds(GroupBoundsMin, GroupBoundsMax);
        _meshRenderer.SetGizmo(GizmoPivot, GizmoPivot is { } piv ? GizmoArmLength(piv) : 0f);

        _grid.Render(viewProj);
        var view = Matrix4x4.CreateScale(-1f, 1f, 1f) * _camera.View; // same X-mirror as viewProj, for the matcap lookup
        _meshRenderer.Render(viewProj, view, _camera.Position, PreviewMode, Wireframe, ShowBounds, ShowBones, CullBackfaces);

        // M36: advance + draw live VFX particles on top of the scene, then keep requesting frames so they animate.
        if (_particleSim is { } psim && _particleRenderer is { } prend && ParticlePlayback is not null)
        {
            float dt = _particleClock.IsRunning ? (float)_particleClock.Elapsed.TotalSeconds : 1f / 60f;
            _particleClock.Restart();
            psim.Update(dt);
            prend.Render(psim, viewProj, view);
            RequestNextFrameRendering();
        }

        // Resolve our offscreen color into Avalonia's framebuffer.
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)fb);
        _gl.BlitFramebuffer(0, 0, (int)w, (int)h, 0, 0, (int)w, (int)h,
            (uint)ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
    }

    private void EnsureFbo(uint w, uint h)
    {
        if (_gl is null) return;
        if (_fbo != 0 && _fboW == (int)w && _fboH == (int)h) return;
        DeleteFbo();

        _fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

        _colorRb = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _colorRb);
        _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Rgba8, w, h);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            RenderbufferTarget.Renderbuffer, _colorRb);

        _depthRb = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthRb);
        _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, w, h);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer, _depthRb);

        _fboW = (int)w;
        _fboH = (int)h;
    }

    private void DeleteFbo()
    {
        if (_gl is null || _fbo == 0) return;
        _gl.DeleteFramebuffer(_fbo);
        _gl.DeleteRenderbuffer(_colorRb);
        _gl.DeleteRenderbuffer(_depthRb);
        _fbo = _colorRb = _depthRb = 0;
        _fboW = _fboH = 0;
    }

    /// <summary>Upload a per-submesh texture list into a renderer layer (0 diffuse · 1 mask · 2 gradient · 3 emissive).</summary>
    private void UploadLayer(IReadOnlyList<TextureImage?>? texs, int slot, Dictionary<TextureImage, uint> uploaded)
    {
        if (_meshRenderer is null) return;
        int count = texs?.Count ?? 0;
        for (int i = 0; i < _meshRenderer.SubmeshCount; i++)
        {
            uint id = 0;
            if (i < count && texs![i] is { } img)
            {
                if (!uploaded.TryGetValue(img, out id))
                {
                    id = _meshRenderer.UploadTexture(img.Rgba, img.Width, img.Height);
                    uploaded[img] = id;
                }
            }
            _meshRenderer.SetSubmeshLayer(i, slot, id); // 0 when the layer is absent → renderer falls back
        }
    }

    /// <summary>(Re)build the particle simulator from <see cref="ParticlePlayback"/> and upload each emitter's
    /// sprite (procedural soft-dot fallback when unresolved). Runs on the GL thread. M36.</summary>
    private void RebuildParticleSim()
    {
        if (_particleRenderer is null) return;
        var pb = ParticlePlayback;
        if (pb is null || pb.System.Emitters.Count == 0) { _particleSim = null; _particleClock.Stop(); return; }

        _particleRenderer.ClearTextures();
        _softDotTex = _particleRenderer.UploadTexture(SoftDot(64), 64, 64);

        var sim = new VfxParticleSimulator();
        sim.SetSystem(pb.System, pb.WorldPos);
        foreach (var es in sim.Emitters)
        {
            // match the state back to its emitter index (by reference — SetSystem keeps the instances)
            int idx = -1;
            for (int i = 0; i < pb.System.Emitters.Count; i++)
                if (ReferenceEquals(pb.System.Emitters[i], es.Def)) { idx = i; break; }
            var img = idx >= 0 && idx < pb.EmitterTextures.Count ? pb.EmitterTextures[idx] : null;
            es.Texture = img is not null ? _particleRenderer.UploadTexture(img.Rgba, img.Width, img.Height) : _softDotTex;
        }
        _particleSim = sim;
        _particleClock.Restart();
    }

    /// <summary>A soft radial-gradient RGBA sprite used when a particle's real texture can't be resolved.</summary>
    private static byte[] SoftDot(int n)
    {
        var px = new byte[n * n * 4];
        float c = (n - 1) / 2f;
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float dx = (x - c) / c, dy = (y - c) / c;
            float a = Math.Clamp(1f - MathF.Sqrt(dx * dx + dy * dy), 0f, 1f);
            a *= a;
            int i = (y * n + x) * 4;
            px[i] = px[i + 1] = px[i + 2] = 255;
            px[i + 3] = (byte)(a * 255);
        }
        return px;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MeshProperty) { _meshDirty = true; RequestNextFrameRendering(); }
        else if (change.Property == ModelTexturesProperty || change.Property == ModelMaskTexturesProperty
                 || change.Property == ModelGradientTexturesProperty || change.Property == ModelEmissiveTexturesProperty
                 || change.Property == ModelMatCapTexturesProperty || change.Property == ModelMatCapMaskTexturesProperty
                 || change.Property == ModelLightmapTexturesProperty)
        { _texturesDirty = true; RequestNextFrameRendering(); }
        else if (change.Property == ModelSubmeshVisibleProperty) { _visibilityDirty = true; RequestNextFrameRendering(); }
        else if (change.Property == ModelSubmeshMaterialsProperty) { _materialsDirty = true; RequestNextFrameRendering(); }
        else if (change.Property == MeshVerticesRevisionProperty)
        {
            // GL buffer uploads need the GL context current — only true inside OnOpenGlRender, never
            // here on the UI thread. Flag it and do the actual UpdateVertices in the render loop.
            _verticesDirty = true;
            RequestNextFrameRendering();
        }
        else if (change.Property == SelectionBoxesProperty || change.Property == GroupBoundsMinProperty
                 || change.Property == GroupBoundsMaxProperty || change.Property == GizmoPivotProperty)
        { RequestNextFrameRendering(); }
        else if (change.Property == SkeletonProperty) { _bonesDirty = true; _skinDirty = true; RequestNextFrameRendering(); }
        else if (change.Property == AnimationClipProperty || change.Property == AnimationTimeProperty) { _skinDirty = true; RequestNextFrameRendering(); }
        else if (change.Property == WireframeProperty || change.Property == ShowBonesProperty
                 || change.Property == ShowBoundsProperty || change.Property == PreviewModeProperty
                 || change.Property == CullBackfacesProperty)
        { _skinDirty = true; RequestNextFrameRendering(); }
        else if (change.Property == ParticleMarkersProperty || change.Property == SelectedParticlePositionProperty)
        { _particlesDirty = true; RequestNextFrameRendering(); }
        else if (change.Property == ParticlePlaybackProperty)
        { _particlePlaybackDirty = true; RequestNextFrameRendering(); }
        else if (change.Property == FocusPointProperty && FocusPoint is { } fp)
        { _pendingFocus = fp; RequestNextFrameRendering(); }
    }

    private void ApplySkinning()
    {
        if (_meshRenderer is null || !_meshRenderer.HasMesh) return;

        if (AnimationClip is { } clip && Mesh is { CanSkin: true } m && Skeleton is { } skeleton)
        {
            try
            {
                var frame = SkinnedMeshAnimator.Skin(m, skeleton, clip, (float)AnimationTime);
                _meshRenderer.UpdateVertices(frame.Positions, frame.Normals);
                if (ShowBones) _meshRenderer.SetBoneSegments(frame.BoneSegments);
                _wasAnimating = true;
            }
            catch { /* keep last frame */ }
        }
        else if (_wasAnimating && Mesh is { } bind)
        {
            _meshRenderer.UpdateVertices(bind.Positions, bind.Normals);
            if (Skeleton is { } s) _meshRenderer.SetBoneSegments(BuildBoneSegments(s));
            _wasAnimating = false;
        }
    }

    private void FrameCamera()
    {
        if (Mesh is not { } m) return;
        float radius = MathF.Max(m.Radius, 1f);
        float dist = radius / MathF.Sin(_camera.FieldOfView * 0.5f) * 1.25f; // fit sphere + margin
        _camera.Target = m.Center;
        _camera.Distance = Math.Clamp(dist, 5f, 100000f);
        _camera.Near = MathF.Max(dist * 0.01f, 0.05f);
        _camera.Far = dist * 40f + radius * 20f;
    }

    /// <summary>Recentre the camera on a world point (M35 particle focus), keeping a close-in distance.</summary>
    private void FocusOnPoint(Vector3 p)
    {
        _camera.Target = p;
        _camera.Distance = Math.Clamp(_camera.Distance, 400f, 2500f);
        _camera.Near = 5f;
        _camera.Far = 200000f;
        RequestNextFrameRendering();
    }

    private static float[] BuildBoneSegments(SkeletonAsset skeleton)
    {
        var byIndex = new Dictionary<int, BoneInfo>();
        foreach (var b in skeleton.Bones) byIndex[b.Index] = b;

        var verts = new List<float>();
        foreach (var b in skeleton.Bones)
        {
            if (b.ParentIndex < 0 || !byIndex.TryGetValue(b.ParentIndex, out var parent)) continue;
            verts.Add(b.WorldPosition.X); verts.Add(b.WorldPosition.Y); verts.Add(b.WorldPosition.Z);
            verts.Add(parent.WorldPosition.X); verts.Add(parent.WorldPosition.Y); verts.Add(parent.WorldPosition.Z);
        }
        return verts.ToArray();
    }
}
