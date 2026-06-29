using System.Numerics;
using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using ReyEngine.Core.Decoding;
using ReyEngine.Formats.Animation;
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
    public static readonly StyledProperty<AnimationClip?> AnimationClipProperty =
        AvaloniaProperty.Register<ViewportControl, AnimationClip?>(nameof(AnimationClip));
    public static readonly StyledProperty<double> AnimationTimeProperty =
        AvaloniaProperty.Register<ViewportControl, double>(nameof(AnimationTime));
    public static readonly StyledProperty<int> PreviewModeProperty =
        AvaloniaProperty.Register<ViewportControl, int>(nameof(PreviewMode));

    public int PreviewMode { get => GetValue(PreviewModeProperty); set => SetValue(PreviewModeProperty, value); }
    public AnimationClip? AnimationClip { get => GetValue(AnimationClipProperty); set => SetValue(AnimationClipProperty, value); }
    public double AnimationTime { get => GetValue(AnimationTimeProperty); set => SetValue(AnimationTimeProperty, value); }
    public IReadOnlyList<TextureImage?>? ModelTextures { get => GetValue(ModelTexturesProperty); set => SetValue(ModelTexturesProperty, value); }
    public IReadOnlyList<TextureImage?>? ModelMaskTextures { get => GetValue(ModelMaskTexturesProperty); set => SetValue(ModelMaskTexturesProperty, value); }
    public IReadOnlyList<TextureImage?>? ModelGradientTextures { get => GetValue(ModelGradientTexturesProperty); set => SetValue(ModelGradientTexturesProperty, value); }
    public IReadOnlyList<TextureImage?>? ModelEmissiveTextures { get => GetValue(ModelEmissiveTexturesProperty); set => SetValue(ModelEmissiveTexturesProperty, value); }
    public IReadOnlyList<TextureImage?>? ModelMatCapTextures { get => GetValue(ModelMatCapTexturesProperty); set => SetValue(ModelMatCapTexturesProperty, value); }
    public IReadOnlyList<TextureImage?>? ModelMatCapMaskTextures { get => GetValue(ModelMatCapMaskTexturesProperty); set => SetValue(ModelMatCapMaskTexturesProperty, value); }
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
    private bool _meshDirty, _bonesDirty, _needFrame, _texturesDirty, _skinDirty, _wasAnimating;

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
                _skinDirty = true;
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
            _texturesDirty = false;
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
        var view = Matrix4x4.CreateScale(-1f, 1f, 1f) * _camera.View; // same X-mirror as viewProj, for the matcap lookup
        _meshRenderer.Render(viewProj, view, _camera.Position, PreviewMode, Wireframe, ShowBounds, ShowBones);

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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MeshProperty) { _meshDirty = true; RequestNextFrameRendering(); }
        else if (change.Property == ModelTexturesProperty || change.Property == ModelMaskTexturesProperty
                 || change.Property == ModelGradientTexturesProperty || change.Property == ModelEmissiveTexturesProperty
                 || change.Property == ModelMatCapTexturesProperty || change.Property == ModelMatCapMaskTexturesProperty)
        { _texturesDirty = true; RequestNextFrameRendering(); }
        else if (change.Property == SkeletonProperty) { _bonesDirty = true; _skinDirty = true; RequestNextFrameRendering(); }
        else if (change.Property == AnimationClipProperty || change.Property == AnimationTimeProperty) { _skinDirty = true; RequestNextFrameRendering(); }
        else if (change.Property == WireframeProperty || change.Property == ShowBonesProperty
                 || change.Property == ShowBoundsProperty || change.Property == PreviewModeProperty)
        { _skinDirty = true; RequestNextFrameRendering(); }
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
