using System.Numerics;
using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using ReyEngine.Core.Decoding;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Skeletons;
using ReyEngine.Rendering;
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
    public static readonly StyledProperty<IReadOnlyList<TextureImage?>?> ModelTexturesProperty =
        AvaloniaProperty.Register<ViewportControl, IReadOnlyList<TextureImage?>?>(nameof(ModelTextures));

    public IReadOnlyList<TextureImage?>? ModelTextures { get => GetValue(ModelTexturesProperty); set => SetValue(ModelTexturesProperty, value); }
    public MeshAsset? Mesh { get => GetValue(MeshProperty); set => SetValue(MeshProperty, value); }
    public SkeletonAsset? Skeleton { get => GetValue(SkeletonProperty); set => SetValue(SkeletonProperty, value); }
    public bool Wireframe { get => GetValue(WireframeProperty); set => SetValue(WireframeProperty, value); }
    public bool ShowBones { get => GetValue(ShowBonesProperty); set => SetValue(ShowBonesProperty, value); }
    public bool ShowBounds { get => GetValue(ShowBoundsProperty); set => SetValue(ShowBoundsProperty, value); }

    private GL? _gl;
    private bool _gles;
    private GridRenderer? _grid;
    private ViewportMeshRenderer? _meshRenderer;
    private readonly OrbitCamera _camera = new();
    private bool _meshDirty, _bonesDirty, _needFrame, _texturesDirty;

    // Offscreen target with a real depth buffer (Avalonia's default FBO has none).
    private uint _fbo, _colorRb, _depthRb;
    private int _fboW, _fboH;

    // ---- Public camera API (driven by the input overlay) ----

    public void OrbitBy(float dx, float dy)
    {
        _camera.Orbit(dx * 0.01f, dy * 0.01f);
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

        if (Mesh is not null) { _meshDirty = true; }
        if (Skeleton is not null) _bonesDirty = true;
        RequestNextFrameRendering();
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _grid?.Dispose();
        _meshRenderer?.Dispose();
        DeleteFbo();
        _grid = null;
        _meshRenderer = null;
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
                _meshRenderer.SetMesh(m.Positions, m.Normals, m.Uvs, m.Indices, m.VertexCount, m.BoundsMin, m.BoundsMax, subs);
                _needFrame = true;
                _texturesDirty = true;
            }
            else _meshRenderer.ClearMesh();
            _meshDirty = false;
        }
        if (_texturesDirty && _meshRenderer.HasMesh)
        {
            if (ModelTextures is { } texs)
            {
                int n = Math.Min(texs.Count, _meshRenderer.SubmeshCount);
                for (int i = 0; i < n; i++)
                    if (texs[i] is { } img)
                        _meshRenderer.SetSubmeshTexture(i, img.Rgba, img.Width, img.Height);
            }
            _texturesDirty = false;
        }
        if (_bonesDirty)
        {
            _meshRenderer.SetBoneSegments(Skeleton is null ? null : BuildBoneSegments(Skeleton));
            _bonesDirty = false;
        }
        if (_needFrame) { FrameCamera(); _needFrame = false; }

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

        _grid.Render(viewProj);
        _meshRenderer.Render(viewProj, Wireframe, ShowBounds, ShowBones);

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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MeshProperty) { _meshDirty = true; RequestNextFrameRendering(); }
        else if (change.Property == ModelTexturesProperty) { _texturesDirty = true; RequestNextFrameRendering(); }
        else if (change.Property == SkeletonProperty) { _bonesDirty = true; RequestNextFrameRendering(); }
        else if (change.Property == WireframeProperty || change.Property == ShowBonesProperty || change.Property == ShowBoundsProperty)
            RequestNextFrameRendering();
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
