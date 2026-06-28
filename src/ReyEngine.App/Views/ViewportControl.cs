using Avalonia;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Skeletons;
using ReyEngine.Rendering;
using Silk.NET.OpenGL;

namespace ReyEngine.App.Views;

/// <summary>
/// OpenGL viewport: grid backdrop + selected mesh (solid/wireframe) with optional
/// bounding box and skeleton overlays. Mesh uploads happen on the GL thread.
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
    private bool _meshDirty, _bonesDirty;

    private Point _lastPointer;
    private bool _orbiting;

    protected override void OnOpenGlInit(GlInterface gl)
    {
        _gl = GL.GetApi(gl.GetProcAddress);
        _gles = ShaderUtil.DetectGles(_gl);

        _grid = new GridRenderer();
        _grid.Initialize(_gl, _gles);
        _meshRenderer = new ViewportMeshRenderer();
        _meshRenderer.Initialize(_gl, _gles);

        if (Mesh is not null) { _meshDirty = true; FrameCamera(); }
        if (Skeleton is not null) _bonesDirty = true;
        RequestNextFrameRendering();
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _grid?.Dispose();
        _meshRenderer?.Dispose();
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
                _meshRenderer.SetMesh(m.Positions, m.Normals, m.Uvs, m.Indices, m.VertexCount, m.BoundsMin, m.BoundsMax);
            else
                _meshRenderer.ClearMesh();
            _meshDirty = false;
        }
        if (_bonesDirty)
        {
            _meshRenderer.SetBoneSegments(Skeleton is null ? null : BuildBoneSegments(Skeleton));
            _bonesDirty = false;
        }

        float scale = (float)(VisualRoot?.RenderScaling ?? 1.0);
        uint w = (uint)Math.Max(1, Bounds.Width * scale);
        uint h = (uint)Math.Max(1, Bounds.Height * scale);

        _gl.Viewport(0, 0, w, h);
        _gl.ClearColor(0.039f, 0.051f, 0.075f, 1f);
        _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        float aspect = h == 0 ? 1f : (float)w / h;
        var viewProj = _camera.ViewProjection(aspect);

        _grid.Render(viewProj);
        _meshRenderer.Render(viewProj, Wireframe, ShowBounds, ShowBones);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MeshProperty) { _meshDirty = true; FrameCamera(); RequestNextFrameRendering(); }
        else if (change.Property == SkeletonProperty) { _bonesDirty = true; RequestNextFrameRendering(); }
        else if (change.Property == WireframeProperty || change.Property == ShowBonesProperty || change.Property == ShowBoundsProperty)
            RequestNextFrameRendering();
    }

    private void FrameCamera()
    {
        if (Mesh is not { } m) return;
        _camera.Target = m.Center;
        _camera.Distance = Math.Clamp(m.Radius * 2.6f, 20f, 50000f);
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
