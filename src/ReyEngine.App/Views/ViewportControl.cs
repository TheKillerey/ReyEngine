using System.Numerics;
using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using ReyEngine.Core.Decoding;
using ReyEngine.Formats.Animation;
using ReyEngine.Formats.Lighting;
using ReyEngine.Formats.MapGeo;
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
    public static readonly StyledProperty<bool> LightmapsEnabledProperty =
        AvaloniaProperty.Register<ViewportControl, bool>(nameof(LightmapsEnabled), true);   // M69
    public static readonly StyledProperty<IReadOnlyList<PointLight>?> DynamicLightsProperty =        // M70
        AvaloniaProperty.Register<ViewportControl, IReadOnlyList<PointLight>?>(nameof(DynamicLights));
    public static readonly StyledProperty<bool> DynamicLightsEnabledProperty =
        AvaloniaProperty.Register<ViewportControl, bool>(nameof(DynamicLightsEnabled));
    public static readonly StyledProperty<double> DynamicLightIntensityProperty =
        AvaloniaProperty.Register<ViewportControl, double>(nameof(DynamicLightIntensity), 1.0);
    public static readonly StyledProperty<double> DynamicLightRadiusScaleProperty =
        AvaloniaProperty.Register<ViewportControl, double>(nameof(DynamicLightRadiusScale), 1.0);   // M71
    public static readonly StyledProperty<double> DynamicLightPositionScaleProperty =
        AvaloniaProperty.Register<ViewportControl, double>(nameof(DynamicLightPositionScale), 1.0);   // M71
    public static readonly StyledProperty<double> DynamicLightScaleXProperty =
        AvaloniaProperty.Register<ViewportControl, double>(nameof(DynamicLightScaleX), 1.0);   // M71
    public static readonly StyledProperty<double> DynamicLightScaleZProperty =
        AvaloniaProperty.Register<ViewportControl, double>(nameof(DynamicLightScaleZ), 1.0);
    public static readonly StyledProperty<double> DynamicLightOffsetXProperty =
        AvaloniaProperty.Register<ViewportControl, double>(nameof(DynamicLightOffsetX), 0.0);
    public static readonly StyledProperty<double> DynamicLightOffsetZProperty =
        AvaloniaProperty.Register<ViewportControl, double>(nameof(DynamicLightOffsetZ), 0.0);
    public static readonly StyledProperty<bool> ShowLightMarkersProperty =
        AvaloniaProperty.Register<ViewportControl, bool>(nameof(ShowLightMarkers), true);   // M71: light position icons
    public static readonly StyledProperty<TextureImage?> GrassTintTextureProperty =
        AvaloniaProperty.Register<ViewportControl, TextureImage?>(nameof(GrassTintTexture));   // M78
    public static readonly StyledProperty<Vector4> GrassTintRectProperty =
        AvaloniaProperty.Register<ViewportControl, Vector4>(nameof(GrassTintRect), new Vector4(0, 0, 1, 1));
    public static readonly StyledProperty<IReadOnlyList<Vector3>?> ParticleMarkersProperty =
        AvaloniaProperty.Register<ViewportControl, IReadOnlyList<Vector3>?>(nameof(ParticleMarkers));
    public static readonly StyledProperty<Vector3?> SelectedParticlePositionProperty =
        AvaloniaProperty.Register<ViewportControl, Vector3?>(nameof(SelectedParticlePosition));
    public static readonly StyledProperty<IReadOnlyList<Vector3>?> PropMarkersProperty =
        AvaloniaProperty.Register<ViewportControl, IReadOnlyList<Vector3>?>(nameof(PropMarkers));
    public static readonly StyledProperty<IReadOnlyList<Vector3>?> ProbeMarkersProperty =
        AvaloniaProperty.Register<ViewportControl, IReadOnlyList<Vector3>?>(nameof(ProbeMarkers));
    public static readonly StyledProperty<IReadOnlyList<Vector3>?> SoundMarkersProperty =
        AvaloniaProperty.Register<ViewportControl, IReadOnlyList<Vector3>?>(nameof(SoundMarkers));      // M55
    public static readonly StyledProperty<float[]?> BucketGridLinesProperty =
        AvaloniaProperty.Register<ViewportControl, float[]?>(nameof(BucketGridLines));                  // M55
    public static readonly StyledProperty<PropRenderSet?> PropMeshesProperty =
        AvaloniaProperty.Register<ViewportControl, PropRenderSet?>(nameof(PropMeshes));
    public static readonly StyledProperty<VfxPlayback?> ParticlePlaybackProperty =
        AvaloniaProperty.Register<ViewportControl, VfxPlayback?>(nameof(ParticlePlayback));
    public static readonly StyledProperty<bool> AnimateWaterProperty =
        AvaloniaProperty.Register<ViewportControl, bool>(nameof(AnimateWater));
    public static readonly StyledProperty<double> LightmapScaleProperty =
        AvaloniaProperty.Register<ViewportControl, double>(nameof(LightmapScale), 1.0);
    public static readonly StyledProperty<MapSunProperties?> SunPropertiesProperty =
        AvaloniaProperty.Register<ViewportControl, MapSunProperties?>(nameof(SunProperties));
    public static readonly StyledProperty<IReadOnlyList<int>?> HighlightSubmeshesProperty =
        AvaloniaProperty.Register<ViewportControl, IReadOnlyList<int>?>(nameof(HighlightSubmeshes));   // M50b outline
    public static readonly StyledProperty<bool> PlayPropAnimationsProperty =
        AvaloniaProperty.Register<ViewportControl, bool>(nameof(PlayPropAnimations));   // M54 prop idles
    public static readonly StyledProperty<double> ParticleSpeedProperty =
        AvaloniaProperty.Register<ViewportControl, double>(nameof(ParticleSpeed), 1.0);   // M46: sim speed multiplier
    public static readonly StyledProperty<bool> ParticlePausedProperty =
        AvaloniaProperty.Register<ViewportControl, bool>(nameof(ParticlePaused));         // M46: freeze the sim
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
    // M88: static NVR map backdrop, drawn behind the previewed character (own renderer instance).
    public static readonly StyledProperty<MeshAsset?> BackgroundMeshProperty =
        AvaloniaProperty.Register<ViewportControl, MeshAsset?>(nameof(BackgroundMesh));
    public static readonly StyledProperty<IReadOnlyList<TextureImage?>?> BackgroundTexturesProperty =
        AvaloniaProperty.Register<ViewportControl, IReadOnlyList<TextureImage?>?>(nameof(BackgroundTextures));
    public static readonly StyledProperty<IReadOnlyList<TextureImage?>?> BackgroundBlendTexturesProperty =   // M89 four-blend
        AvaloniaProperty.Register<ViewportControl, IReadOnlyList<TextureImage?>?>(nameof(BackgroundBlendTextures));
    public static readonly StyledProperty<IReadOnlyList<TextureImage?>?> BackgroundColor1TexturesProperty =
        AvaloniaProperty.Register<ViewportControl, IReadOnlyList<TextureImage?>?>(nameof(BackgroundColor1Textures));
    public static readonly StyledProperty<IReadOnlyList<TextureImage?>?> BackgroundColor2TexturesProperty =
        AvaloniaProperty.Register<ViewportControl, IReadOnlyList<TextureImage?>?>(nameof(BackgroundColor2Textures));
    public static readonly StyledProperty<IReadOnlyList<TextureImage?>?> BackgroundColor3TexturesProperty =
        AvaloniaProperty.Register<ViewportControl, IReadOnlyList<TextureImage?>?>(nameof(BackgroundColor3Textures));
    public static readonly StyledProperty<bool> BackgroundVisibleProperty =
        AvaloniaProperty.Register<ViewportControl, bool>(nameof(BackgroundVisible), true);
    public static readonly StyledProperty<Vector3> BackgroundOffsetProperty =        // M89: move the map
        AvaloniaProperty.Register<ViewportControl, Vector3>(nameof(BackgroundOffset));
    public static readonly StyledProperty<double> BackgroundRotationProperty =        // M89: rotate the map (degrees, Y)
        AvaloniaProperty.Register<ViewportControl, double>(nameof(BackgroundRotation));
    public static readonly StyledProperty<double> BackgroundVertexLightProperty =    // M89: NVR baked-light scale
        AvaloniaProperty.Register<ViewportControl, double>(nameof(BackgroundVertexLight), 3.0);
    // M142.4: legacy NVR statics use PrimaryColor AS their baked lightmap (subject path). Off by default;
    // the legacy-map viewer turns it on so props/structures read the map's baked night lighting.
    public static readonly StyledProperty<bool> UseVertexLightmapProperty =
        AvaloniaProperty.Register<ViewportControl, bool>(nameof(UseVertexLightmap));
    public static readonly StyledProperty<double> VertexLightmapScaleProperty =
        AvaloniaProperty.Register<ViewportControl, double>(nameof(VertexLightmapScale), 2.0);
    public static readonly StyledProperty<double> BackgroundBrightnessProperty =     // M89: base sun/sky on the backdrop
        AvaloniaProperty.Register<ViewportControl, double>(nameof(BackgroundBrightness), 0.55);
    public static readonly StyledProperty<bool> ShowGridProperty =                   // M89: reference grid toggle
        AvaloniaProperty.Register<ViewportControl, bool>(nameof(ShowGrid), true);
    public static readonly StyledProperty<double> ModelScaleProperty =               // M90: preview model scale
        AvaloniaProperty.Register<ViewportControl, double>(nameof(ModelScale), 1.0);
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
    /// <summary>M122: what the viewport draws as its sky; null = plain background.</summary>
    public static readonly StyledProperty<Services.SkyboxSpec?> SkyboxProperty =
        AvaloniaProperty.Register<ViewportControl, Services.SkyboxSpec?>(nameof(Skybox));

    /// <summary>M114: world position the target-dummy cube stands on; null hides it.</summary>
    public static readonly StyledProperty<Vector3?> TargetDummyPositionProperty =
        AvaloniaProperty.Register<ViewportControl, Vector3?>(nameof(TargetDummyPosition));
    public static readonly StyledProperty<Vector3?> GizmoPivotProperty =
        AvaloniaProperty.Register<ViewportControl, Vector3?>(nameof(GizmoPivot));
    public static readonly StyledProperty<int> GizmoModeProperty =
        AvaloniaProperty.Register<ViewportControl, int>(nameof(GizmoMode));
    public static readonly StyledProperty<IReadOnlyList<Vector3>?> GizmoAxesProperty =
        AvaloniaProperty.Register<ViewportControl, IReadOnlyList<Vector3>?>(nameof(GizmoAxes));

    public int PreviewMode { get => GetValue(PreviewModeProperty); set => SetValue(PreviewModeProperty, value); }
    /// <summary>World-space bounds of every selected map mesh, for the amber highlight boxes.</summary>
    public IReadOnlyList<(Vector3 min, Vector3 max)>? SelectionBoxes { get => GetValue(SelectionBoxesProperty); set => SetValue(SelectionBoxesProperty, value); }
    /// <summary>Combined bounds of a multi-selection (the dimmer group box); null for single/empty selection.</summary>
    public Vector3? GroupBoundsMin { get => GetValue(GroupBoundsMinProperty); set => SetValue(GroupBoundsMinProperty, value); }
    public Vector3? GroupBoundsMax { get => GetValue(GroupBoundsMaxProperty); set => SetValue(GroupBoundsMaxProperty, value); }
    /// <summary>World-space center of the selection — the translate-gizmo origin (null = no selection).</summary>
    public Services.SkyboxSpec? Skybox { get => GetValue(SkyboxProperty); set => SetValue(SkyboxProperty, value); }
    public Vector3? TargetDummyPosition { get => GetValue(TargetDummyPositionProperty); set => SetValue(TargetDummyPositionProperty, value); }
    public Vector3? GizmoPivot { get => GetValue(GizmoPivotProperty); set => SetValue(GizmoPivotProperty, value); }
    /// <summary>Transform gizmo mode (M42): 0 move · 1 rotate · 2 scale.</summary>
    public int GizmoMode { get => GetValue(GizmoModeProperty); set => SetValue(GizmoModeProperty, value); }
    /// <summary>The 3 gizmo axis directions (world or the mesh's local axes).</summary>
    public IReadOnlyList<Vector3>? GizmoAxes { get => GetValue(GizmoAxesProperty); set => SetValue(GizmoAxesProperty, value); }
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
    public MeshAsset? BackgroundMesh { get => GetValue(BackgroundMeshProperty); set => SetValue(BackgroundMeshProperty, value); }
    public IReadOnlyList<TextureImage?>? BackgroundTextures { get => GetValue(BackgroundTexturesProperty); set => SetValue(BackgroundTexturesProperty, value); }
    public IReadOnlyList<TextureImage?>? BackgroundBlendTextures { get => GetValue(BackgroundBlendTexturesProperty); set => SetValue(BackgroundBlendTexturesProperty, value); }
    public IReadOnlyList<TextureImage?>? BackgroundColor1Textures { get => GetValue(BackgroundColor1TexturesProperty); set => SetValue(BackgroundColor1TexturesProperty, value); }
    public IReadOnlyList<TextureImage?>? BackgroundColor2Textures { get => GetValue(BackgroundColor2TexturesProperty); set => SetValue(BackgroundColor2TexturesProperty, value); }
    public IReadOnlyList<TextureImage?>? BackgroundColor3Textures { get => GetValue(BackgroundColor3TexturesProperty); set => SetValue(BackgroundColor3TexturesProperty, value); }
    public bool BackgroundVisible { get => GetValue(BackgroundVisibleProperty); set => SetValue(BackgroundVisibleProperty, value); }
    public Vector3 BackgroundOffset { get => GetValue(BackgroundOffsetProperty); set => SetValue(BackgroundOffsetProperty, value); }
    public double BackgroundRotation { get => GetValue(BackgroundRotationProperty); set => SetValue(BackgroundRotationProperty, value); }
    // M89: world transform for the backdrop — rotate the map about the character (origin), then place it.
    private Matrix4x4 BackgroundModel() =>
        Matrix4x4.CreateTranslation(BackgroundOffset) * Matrix4x4.CreateRotationY((float)(BackgroundRotation * Math.PI / 180.0));
    public double BackgroundVertexLight { get => GetValue(BackgroundVertexLightProperty); set => SetValue(BackgroundVertexLightProperty, value); }
    public bool UseVertexLightmap { get => GetValue(UseVertexLightmapProperty); set => SetValue(UseVertexLightmapProperty, value); }   // M142.4
    public double VertexLightmapScale { get => GetValue(VertexLightmapScaleProperty); set => SetValue(VertexLightmapScaleProperty, value); }
    public double BackgroundBrightness { get => GetValue(BackgroundBrightnessProperty); set => SetValue(BackgroundBrightnessProperty, value); }
    public bool ShowGrid { get => GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }
    public double ModelScale { get => GetValue(ModelScaleProperty); set => SetValue(ModelScaleProperty, value); }
    public IReadOnlyList<ViewportMeshRenderer.SubmeshMaterial>? ModelSubmeshMaterials { get => GetValue(ModelSubmeshMaterialsProperty); set => SetValue(ModelSubmeshMaterialsProperty, value); }
    public int MeshVerticesRevision { get => GetValue(MeshVerticesRevisionProperty); set => SetValue(MeshVerticesRevisionProperty, value); }
    public MeshAsset? Mesh { get => GetValue(MeshProperty); set => SetValue(MeshProperty, value); }
    public SkeletonAsset? Skeleton { get => GetValue(SkeletonProperty); set => SetValue(SkeletonProperty, value); }
    public bool Wireframe { get => GetValue(WireframeProperty); set => SetValue(WireframeProperty, value); }
    public bool CullBackfaces { get => GetValue(CullBackfacesProperty); set => SetValue(CullBackfacesProperty, value); }
    public bool LightmapsEnabled { get => GetValue(LightmapsEnabledProperty); set => SetValue(LightmapsEnabledProperty, value); }
    public IReadOnlyList<PointLight>? DynamicLights { get => GetValue(DynamicLightsProperty); set => SetValue(DynamicLightsProperty, value); }
    public bool DynamicLightsEnabled { get => GetValue(DynamicLightsEnabledProperty); set => SetValue(DynamicLightsEnabledProperty, value); }
    public double DynamicLightIntensity { get => GetValue(DynamicLightIntensityProperty); set => SetValue(DynamicLightIntensityProperty, value); }
    public double DynamicLightRadiusScale { get => GetValue(DynamicLightRadiusScaleProperty); set => SetValue(DynamicLightRadiusScaleProperty, value); }
    public double DynamicLightPositionScale { get => GetValue(DynamicLightPositionScaleProperty); set => SetValue(DynamicLightPositionScaleProperty, value); }
    public double DynamicLightScaleX { get => GetValue(DynamicLightScaleXProperty); set => SetValue(DynamicLightScaleXProperty, value); }
    public double DynamicLightScaleZ { get => GetValue(DynamicLightScaleZProperty); set => SetValue(DynamicLightScaleZProperty, value); }
    public double DynamicLightOffsetX { get => GetValue(DynamicLightOffsetXProperty); set => SetValue(DynamicLightOffsetXProperty, value); }
    public double DynamicLightOffsetZ { get => GetValue(DynamicLightOffsetZProperty); set => SetValue(DynamicLightOffsetZProperty, value); }
    public bool ShowLightMarkers { get => GetValue(ShowLightMarkersProperty); set => SetValue(ShowLightMarkersProperty, value); }
    public TextureImage? GrassTintTexture { get => GetValue(GrassTintTextureProperty); set => SetValue(GrassTintTextureProperty, value); }
    public Vector4 GrassTintRect { get => GetValue(GrassTintRectProperty); set => SetValue(GrassTintRectProperty, value); }
    /// <summary>World positions of placed-particle markers to draw (M35); null/empty hides them.</summary>
    public IReadOnlyList<Vector3>? ParticleMarkers { get => GetValue(ParticleMarkersProperty); set => SetValue(ParticleMarkersProperty, value); }
    public Vector3? SelectedParticlePosition { get => GetValue(SelectedParticlePositionProperty); set => SetValue(SelectedParticlePositionProperty, value); }
    /// <summary>World positions of animated-prop markers (M38); orange.</summary>
    public IReadOnlyList<Vector3>? PropMarkers { get => GetValue(PropMarkersProperty); set => SetValue(PropMarkersProperty, value); }
    /// <summary>World positions of cubemap-probe markers (M38); green.</summary>
    public IReadOnlyList<Vector3>? ProbeMarkers { get => GetValue(ProbeMarkersProperty); set => SetValue(ProbeMarkersProperty, value); }
    public IReadOnlyList<Vector3>? SoundMarkers { get => GetValue(SoundMarkersProperty); set => SetValue(SoundMarkersProperty, value); }
    public float[]? BucketGridLines { get => GetValue(BucketGridLinesProperty); set => SetValue(BucketGridLinesProperty, value); }
    /// <summary>Decoded placed prop meshes to render at their transforms (M41); null clears them.</summary>
    public PropRenderSet? PropMeshes { get => GetValue(PropMeshesProperty); set => SetValue(PropMeshesProperty, value); }
    /// <summary>Set to a world point to recentre the camera on it (M35 focus); cleared after applying.</summary>
    public Vector3? FocusPoint { get => GetValue(FocusPointProperty); set => SetValue(FocusPointProperty, value); }
    /// <summary>The placed VFX system to simulate and play live (M36); null stops playback.</summary>
    public VfxPlayback? ParticlePlayback { get => GetValue(ParticlePlaybackProperty); set => SetValue(ParticlePlaybackProperty, value); }
    public bool AnimateWater { get => GetValue(AnimateWaterProperty); set => SetValue(AnimateWaterProperty, value); }
    public double LightmapScale { get => GetValue(LightmapScaleProperty); set => SetValue(LightmapScaleProperty, value); }
    public MapSunProperties? SunProperties { get => GetValue(SunPropertiesProperty); set => SetValue(SunPropertiesProperty, value); }
    public IReadOnlyList<int>? HighlightSubmeshes { get => GetValue(HighlightSubmeshesProperty); set => SetValue(HighlightSubmeshesProperty, value); }
    public bool PlayPropAnimations { get => GetValue(PlayPropAnimationsProperty); set => SetValue(PlayPropAnimationsProperty, value); }
    public double ParticleSpeed { get => GetValue(ParticleSpeedProperty); set => SetValue(ParticleSpeedProperty, value); }
    public bool ParticlePaused { get => GetValue(ParticlePausedProperty); set => SetValue(ParticlePausedProperty, value); }
    public bool ShowBones { get => GetValue(ShowBonesProperty); set => SetValue(ShowBonesProperty, value); }
    public bool ShowBounds { get => GetValue(ShowBoundsProperty); set => SetValue(ShowBoundsProperty, value); }

    private GL? _gl;
    private bool _gles;
    private GridRenderer? _grid;
    private ViewportMeshRenderer? _meshRenderer;
    private ViewportMeshRenderer? _bgRenderer;      // M88: NVR map backdrop
    private bool _bgMeshDirty, _bgTexDirty;
    private readonly OrbitCamera _camera = new();
    private bool _meshDirty, _bonesDirty, _needFrame, _texturesDirty, _skinDirty, _wasAnimating, _visibilityDirty, _verticesDirty, _materialsDirty;
    private bool _dynamicLightsDirty;   // M70: re-upload the Light.dat table on the GL thread when it changes
    private bool _lightMarkersDirty;    // M71: recompute the transformed light-position icons
    private float[]? _lastBucketGridLines;   // M77: skip redundant multi-MB line uploads
    private bool _grassTintDirty;            // M78: upload the grass-tint texture on the GL thread
    private bool _particlesDirty;
    private bool _propMeshesDirty;   // M41
    private float _markerSize = 40f; // world-size for placement markers, fixed once the mesh loads
    private Vector3? _pendingFocus;

    // M36: live VFX particle playback (one simulator per played placement)
    private VfxParticleRenderer? _particleRenderer;
    private readonly List<VfxParticleSimulator> _particleSims = new();
    private readonly Dictionary<VfxPlaybackItem, VfxParticleSimulator> _particleSimCache = new(ReferenceEqualityComparer.Instance);
    /// <summary>M116: per travelling item, seconds since its playback started (drives caster→target flight).</summary>
    private readonly Dictionary<VfxPlaybackItem, float> _travelElapsed = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TextureImage, uint> _particleTextureCache = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<VfxParticleSimulator.EmitterState, VfxMeshAnimation> _particleMeshAnimations = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<VfxParticleSimulator> _wantedParticleSims = new(ReferenceEqualityComparer.Instance);
    private readonly List<(VfxParticleSimulator.EmitterState Es, VfxMeshAnimation Anim)> _animatedMeshEmitters = new(); // M48
    private readonly List<(int Geo, PropMesh Mesh)> _animatedPropGeoms = new();   // M54: prop idle animations
    private readonly System.Diagnostics.Stopwatch _propAnimClock = new();

    /// <summary>M56: fired (on the UI thread) when the camera has moved ~100+ world units — drives
    /// positional map ambience volume without polling.</summary>
    public event Action<Vector3>? CameraMoved;
    private Vector3 _lastAudioCamPos = new(float.MaxValue, 0, 0);
    private bool _particlePlaybackDirty;
    private uint _softDotTex;
    private readonly System.Diagnostics.Stopwatch _particleClock = new();
    private SkyboxRenderer? _skyboxRenderer;   // M122
    private bool _skyboxDirty;
    private bool SetSkyboxDirty() { _skyboxDirty = true; return true; }
    private readonly System.Diagnostics.Stopwatch _waterClock = new();   // M44: flowmap-water animation clock

    // Offscreen target with a real depth buffer (Avalonia's default FBO has none).
    private uint _fbo, _colorRb, _depthRb;
    private int _fboW, _fboH;

    // ---- Public camera API (driven by the input overlay) ----

    // M40: user camera-feel multipliers (1 = default), driven by EditorSettings.
    private float _lookSens = 1f, _orbitSens = 1f, _panSens = 1f, _zoomSens = 1f;
    private bool _invertLookY;

    /// <summary>Apply the user's camera preferences (sensitivities are multipliers; base fly speed in u/s).</summary>
    public void ApplyCameraSettings(float look, float orbit, float pan, float zoom, bool invertLookY, float flySpeed)
    {
        _lookSens = look; _orbitSens = orbit; _panSens = pan; _zoomSens = zoom;
        _invertLookY = invertLookY;
        _camera.FlySpeed = flySpeed;
    }

    public void OrbitBy(float dx, float dy)
    {
        _camera.Orbit(dx * 0.01f * _orbitSens, dy * 0.01f * _orbitSens);
        RequestNextFrameRendering();
    }

    /// <summary>LMB mouse-look (rotate the view in place). Direct mapping: cursor up→look up, left→look left.</summary>
    public void LookBy(float dx, float dy)
    {
        _camera.Look(-dx * 0.005f * _lookSens, dy * 0.005f * _lookSens * (_invertLookY ? -1f : 1f));
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
        _camera.Pan(-dx * _panSens, dy * _panSens);
        RequestNextFrameRendering();
    }

    public void ZoomBy(float wheelDelta)
    {
        float step = 0.1f * _zoomSens;
        _camera.Zoom(wheelDelta > 0 ? 1f - step : 1f + step);
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

    /// <summary>The world direction of a gizmo axis — the mesh's local axes when supplied, else world.</summary>
    public Vector3 AxisDir(GizmoAxis axis)
    {
        var ax = GizmoAxes;
        if (ax is { Count: 3 })
            return axis switch { GizmoAxis.X => ax[0], GizmoAxis.Y => ax[1], _ => ax[2] };
        return axis switch { GizmoAxis.X => Vector3.UnitX, GizmoAxis.Y => Vector3.UnitY, _ => Vector3.UnitZ };
    }

    /// <summary>Which gizmo handle (if any) is under the point. Move/Scale hit the axis arms; Rotate hits the
    /// nearest ring (a circle in the plane perpendicular to each axis).</summary>
    public GizmoAxis? HitTestGizmoAxis(Point screenPos)
    {
        if (GizmoPivot is not { } pivot || _lastViewportW <= 0 || _lastViewportH <= 0) return null;
        float armLength = GizmoArmLength(pivot);
        if (armLength <= 0f) return null;
        var sp = new Vector2((float)screenPos.X, (float)screenPos.Y);

        GizmoAxis? best = null;
        float bestDist = GizmoHitPixels;

        if (GizmoMode == 1) // rotate: nearest ring
        {
            foreach (var axis in new[] { GizmoAxis.X, GizmoAxis.Y, GizmoAxis.Z })
            {
                float d = DistanceToRing(sp, pivot, AxisDir(axis), armLength);
                if (d < bestDist) { bestDist = d; best = axis; }
            }
            return best;
        }

        foreach (var axis in new[] { GizmoAxis.X, GizmoAxis.Y, GizmoAxis.Z }) // move / scale: axis arm
        {
            if (!ViewportPicking.ProjectToScreen(pivot, _lastViewProj, _lastViewportW, _lastViewportH, out var p0)) continue;
            if (!ViewportPicking.ProjectToScreen(pivot + AxisDir(axis) * armLength, _lastViewProj, _lastViewportW, _lastViewportH, out var p1)) continue;
            float d = DistancePointToSegment(sp, p0, p1);
            if (d < bestDist) { bestDist = d; best = axis; }
        }
        return best;
    }

    /// <summary>Screen distance from a point to a world-space ring (circle perpendicular to <paramref name="axis"/>).</summary>
    private float DistanceToRing(Vector2 sp, Vector3 center, Vector3 axis, float radius)
    {
        axis = Vector3.Normalize(axis);
        var u = Vector3.Normalize(MathF.Abs(axis.Y) < 0.99f ? Vector3.Cross(axis, Vector3.UnitY) : Vector3.Cross(axis, Vector3.UnitX));
        var w = Vector3.Cross(axis, u);
        float best = float.MaxValue;
        const int N = 32;
        Vector2 prev = default; bool havePrev = false;
        for (int i = 0; i <= N; i++)
        {
            float t = i / (float)N * MathF.Tau;
            var p = center + (u * MathF.Cos(t) + w * MathF.Sin(t)) * radius;
            if (!ViewportPicking.ProjectToScreen(p, _lastViewProj, _lastViewportW, _lastViewportH, out var s)) { havePrev = false; continue; }
            if (havePrev) best = MathF.Min(best, DistancePointToSegment(sp, prev, s));
            prev = s; havePrev = true;
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

    /// <summary>M76: world point → screen pixel with the same mirror-inclusive matrices as rendering —
    /// lets the host pick placeable icons in SCREEN space (UE-style), so they're clickable at any zoom.</summary>
    public bool TryProjectToScreen(Vector3 world, out Vector2 screen)
        => ViewportPicking.ProjectToScreen(world, _lastViewProj, _lastViewportW, _lastViewportH, out screen);

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
        _bgRenderer = new ViewportMeshRenderer();   // M88: NVR map backdrop
        _bgRenderer.Initialize(_gl, _gles);

        _particleRenderer = new VfxParticleRenderer();
        _skyboxRenderer = new SkyboxRenderer();
        _skyboxRenderer.Initialize(_gl);
        _skyboxDirty = true;   // re-apply the spec on a fresh context
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
        _bgRenderer?.Dispose();
        _particleRenderer?.Dispose();
        DeleteFbo();
        _grid = null;
        _meshRenderer = null;
        _bgRenderer = null;
        _particleRenderer = null;
        _skyboxRenderer?.Dispose();
        _skyboxRenderer = null;
        _particleSims.Clear();
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
                _markerSize = Math.Clamp(m.Radius * 0.004f, 4f, 90f); // fixed from the mesh so toggling Show doesn't resize markers
                _needFrame = true;
                _texturesDirty = true;
                _skinDirty = true;
                _visibilityDirty = true;
                _materialsDirty = true;
                _lightMarkersDirty = true;   // M71: re-place light icons with the new mesh's marker size
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
        if (_bgMeshDirty && _bgRenderer is not null)   // M88: (re)build the NVR backdrop geometry
        {
            if (BackgroundMesh is { } bgm)
            {
                var subs = bgm.SubMeshes.Select(s => (s.StartIndex, s.IndexCount)).ToList();
                // M89: pass vertex colours (baked ground shading) + the 2nd UV set (four-blend mask UV).
                _bgRenderer.SetMesh(bgm.Positions, bgm.Normals, bgm.Uvs, bgm.Indices, bgm.VertexCount,
                    bgm.BoundsMin, bgm.BoundsMax, subs, bgm.Colors, bgm.LightmapUvs);
            }
            else _bgRenderer.ClearMesh();
            _bgMeshDirty = false;
            _bgTexDirty = true;
        }
        if (_bgTexDirty && _bgRenderer is { HasMesh: true })   // M88/M89: upload backdrop textures (5 layers)
        {
            var uploaded = new Dictionary<TextureImage, uint>(ReferenceEqualityComparer.Instance);
            UploadBgLayer(BackgroundTextures, 0, uploaded);        // COLOR_MAP_0 (base diffuse)
            UploadBgLayer(BackgroundBlendTextures, 1, uploaded);   // BLEND_MAP (mask slot — gates four-blend)
            UploadBgLayer(BackgroundColor1Textures, 2, uploaded);  // COLOR_MAP_1 (gradient slot)
            UploadBgLayer(BackgroundColor2Textures, 3, uploaded);  // COLOR_MAP_2 (emissive slot)
            UploadBgLayer(BackgroundColor3Textures, 4, uploaded);  // COLOR_MAP_3 (matcap slot)
            _bgTexDirty = false;
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
            _meshRenderer.SetParticleMarkers(pts, SelectedParticlePosition, _markerSize);
            _meshRenderer.SetTargetDummy(TargetDummyPosition, 120f);   // M114: ~melee-minion sized cube
            _meshRenderer.SetPropMarkers(PropMarkers ?? (IReadOnlyList<Vector3>)Array.Empty<Vector3>(), _markerSize);
            _meshRenderer.SetProbeMarkers(ProbeMarkers ?? (IReadOnlyList<Vector3>)Array.Empty<Vector3>(), _markerSize * 1.4f);
            _meshRenderer.SetSoundMarkers(SoundMarkers ?? (IReadOnlyList<Vector3>)Array.Empty<Vector3>(), _markerSize * 1.2f);
            // M77 perf: the bucket-grid overlay can be megabytes of data — re-upload ONLY when the array
            // instance actually changed, not on every marker refresh (gizmo drags dirty markers per frame).
            // M77b: the array is pos3+bary3 triangle soup for the barycentric wireframe path.
            if (!ReferenceEquals(_lastBucketGridLines, BucketGridLines))
            {
                _meshRenderer.SetBucketGridMesh(BucketGridLines);
                _lastBucketGridLines = BucketGridLines;
            }
            _particlesDirty = false;
        }
        if (_lightMarkersDirty)
        {
            // M71: place an icon at each light's TRANSFORMED position (same scale/offset the shader applies),
            // so the markers track the layout as the user drags the position sliders.
            if (ShowLightMarkers && DynamicLights is { Count: > 0 } lights)
            {
                double ps = DynamicLightPositionScale, sx = DynamicLightScaleX, sz = DynamicLightScaleZ;
                double ox = DynamicLightOffsetX, oz = DynamicLightOffsetZ;
                var pts = new Vector3[lights.Count];
                for (int i = 0; i < lights.Count; i++)
                {
                    var p = lights[i].Position;
                    pts[i] = new Vector3((float)(p.X * ps * sx + ox), p.Y, (float)(p.Z * ps * sz + oz));
                }
                _meshRenderer.SetLightMarkers(pts, _markerSize * 1.2f);
            }
            else _meshRenderer.SetLightMarkers(Array.Empty<Vector3>(), _markerSize);
            _lightMarkersDirty = false;
        }
        if (_particlePlaybackDirty) { RebuildParticleSim(); _particlePlaybackDirty = false; }
        if (_propMeshesDirty) { RebuildPropMeshes(); _propMeshesDirty = false; }
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

        // M122: sky first - rotation-only view (same X-mirror), depth writes off, scene overdraws it.
        if (_skyboxRenderer is { } sky)
        {
            if (_skyboxDirty)
            {
                var spec = Skybox;
                if (spec is null) sky.Clear();
                else if (spec.Cubemap is { } cm) sky.SetCubemap(cm);
                else if (spec.Equirect is { } eq) sky.SetEquirect(eq);
                else if (spec.MeshPositions is { } mp && spec.MeshIndices is { } mi)
                    sky.SetMesh(mp, spec.MeshUvs ?? Array.Empty<float>(), mi, spec.MeshTexture);
                _skyboxDirty = false;
            }
            if (sky.HasSkybox)
            {
                var viewRot = _camera.View;
                viewRot.M41 = 0f; viewRot.M42 = 0f; viewRot.M43 = 0f;
                sky.Render(Matrix4x4.CreateScale(-1f, 1f, 1f) * viewRot, _camera.Projection(aspect));
            }
        }

        // Cache the exact matrices/size used for THIS frame so gizmo hit-testing (driven by pointer
        // events, outside the render loop) always matches what's actually on screen. The eye is stored
        // in MESH-DATA space: vertices get X-mirrored before the view matrix, so the camera effectively
        // sits at (-x, y, z) relative to the un-mirrored data the pivots/bounds live in.
        _lastViewProj = viewProj;
        _lastViewportW = Bounds.Width;
        _lastViewportH = Bounds.Height;
        _lastCamPos = new Vector3(-_camera.Position.X, _camera.Position.Y, _camera.Position.Z);
        // M56: notify camera movement for positional map ambience (coarse: every ~100 world units)
        if (CameraMoved is not null && Vector3.DistanceSquared(_lastCamPos, _lastAudioCamPos) > 100f * 100f)
        {
            _lastAudioCamPos = _lastCamPos;
            var pos = _lastCamPos;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => CameraMoved?.Invoke(pos));
        }

        _meshRenderer.SetHighlightBoxes(SelectionBoxes ?? Array.Empty<(Vector3, Vector3)>());
        _meshRenderer.SetGroupBounds(GroupBoundsMin, GroupBoundsMax);
        var gax = GizmoAxes;
        _meshRenderer.SetGizmo(GizmoPivot, GizmoPivot is { } piv ? GizmoArmLength(piv) : 0f, GizmoMode,
            gax is { Count: 3 } ? gax[0] : null, gax is { Count: 3 } ? gax[1] : null, gax is { Count: 3 } ? gax[2] : null);

        if (ShowGrid) _grid.Render(viewProj);   // M89: reference grid is now toggleable
        var view = Matrix4x4.CreateScale(-1f, 1f, 1f) * _camera.View; // same X-mirror as viewProj, for the matcap lookup
        // M44: advance the flowmap-water clock so the river flows; only ticks while water is on screen.
        if (AnimateWater) { if (!_waterClock.IsRunning) _waterClock.Start(); _meshRenderer.SetTime((float)_waterClock.Elapsed.TotalSeconds); }
        else if (_waterClock.IsRunning) _waterClock.Reset();
        _meshRenderer.SetLightmapScale((float)LightmapScale);   // M45: MapSunProperties.lightMapColorScale
        _meshRenderer.SetLightmapsEnabled(LightmapsEnabled);    // M69: baked-lightmap on/off toggle
        if (_grassTintDirty)   // M78: (re)upload the map's grass-tint texture on the GL thread
        {
            var gti = GrassTintTexture;
            _meshRenderer.SetGrassTintTexture(gti?.Rgba, gti?.Width ?? 0, gti?.Height ?? 0, GrassTintRect);
            _grassTintDirty = false;
        }
        if (_dynamicLightsDirty)   // M70: (re)upload the Light.dat point-light table on the GL thread
        {
            var lights = DynamicLights ?? (IReadOnlyList<PointLight>)Array.Empty<PointLight>();
            // M89: when the backdrop is moved/rotated, transform its lights the same way so terrain stays lit
            // consistently and the character moves through the light field. M142.1: only when a backdrop is
            // actually loaded — a legacy map viewed as the SUBJECT keeps its lights in world space (the
            // backdrop offset defaults are non-identity and would teleport them).
            var bgModel = BackgroundModel();
            if (!bgModel.IsIdentity && BackgroundMesh is not null)
                lights = lights.Select(l => l with { Position = Vector3.Transform(l.Position, bgModel) }).ToList();
            _meshRenderer.SetPointLights(lights);
            _bgRenderer?.SetPointLights(lights);   // M88: light the backdrop identically
            _dynamicLightsDirty = false;
        }
        _meshRenderer.SetVertexLightmap(UseVertexLightmap, (float)VertexLightmapScale);   // M142.4: NVR statics
        _meshRenderer.SetDynamicLightsEnabled(DynamicLightsEnabled);
        _meshRenderer.SetLightIntensity((float)DynamicLightIntensity);
        _meshRenderer.SetLightRadiusScale((float)DynamicLightRadiusScale);
        _meshRenderer.SetLightPositionScale((float)DynamicLightPositionScale);
        _meshRenderer.SetLightPositionScaleXZ((float)DynamicLightScaleX, (float)DynamicLightScaleZ);
        _meshRenderer.SetLightPositionOffset((float)DynamicLightOffsetX, (float)DynamicLightOffsetZ);
        if (SunProperties is { } sun)
            _meshRenderer.SetSunLighting(sun.SunDirection, sun.SunColor, sun.SkyLightColor, sun.SkyLightScale);
        else
            _meshRenderer.SetSunLighting(Vector3.Zero, Vector4.One, Vector4.One, 1f);
        _meshRenderer.SetSubmeshHighlight(HighlightSubmeshes);  // M50b: selection outline overlay
        // M54: prop idle animations — CPU-skin each animated prop geometry at the shared looping clock
        // (all placements of a mesh share the pose) and keep frames pumping while playing.
        if (PlayPropAnimations && _animatedPropGeoms.Count > 0)
        {
            if (!_propAnimClock.IsRunning) _propAnimClock.Start();
            float t = (float)_propAnimClock.Elapsed.TotalSeconds;
            foreach (var (geo, pm) in _animatedPropGeoms)
            {
                float dur = pm.IdleClip!.Duration > 1e-3f ? pm.IdleClip.Duration : 1f;
                var frame = SkinnedMeshAnimator.Skin(pm.SknMesh!, pm.Skeleton!, pm.IdleClip, t % dur);
                _meshRenderer.UpdatePropGeometryVertices(geo, frame.Positions, frame.Normals);
            }
            RequestNextFrameRendering();
        }
        else if (_propAnimClock.IsRunning) _propAnimClock.Reset();
        // M88: draw the NVR map backdrop first (static, lit by the same point lights + sun, never culled
        // so terrain never shows holes). Depth-tested so the character composites in front correctly.
        if (BackgroundVisible && _bgRenderer is { HasMesh: true } bg)
        {
            bg.SetDynamicLightsEnabled(DynamicLightsEnabled);
            bg.SetLightIntensity((float)DynamicLightIntensity);
            bg.SetLightRadiusScale((float)DynamicLightRadiusScale);
            bg.SetVertexBakedLight(true, (float)BackgroundVertexLight);   // M89: NVR ground baked shading
            bg.SetNvrFourBlend(true);                                     // M89: CREATE_GROUND_MOSAIC_FOUR_BLEND
            bg.SetWorldTransform(BackgroundModel());                      // M89: move + rotate the map
            // M89: base sun/sky kept DARK by default — old maps are meant to be lit by their Light.dat
            // point lights, and a bright base washes the ground flat (in-game look = dark + warm pools).
            float bright = (float)BackgroundBrightness;
            var bgLight = new Vector4(bright, bright, bright, 1f);
            if (SunProperties is { } bsun)
                bg.SetSunLighting(bsun.SunDirection, bsun.SunColor * bright, bsun.SkyLightColor * bright, bsun.SkyLightScale);
            else
                bg.SetSunLighting(Vector3.Zero, bgLight, bgLight, 1f);
            bg.Render(viewProj, view, _camera.Position, 0, Wireframe, false, false, cullBackfaces: false);
        }
        // M90: uniform preview-model scale (identity at 1.0 — the main map viewport never changes it).
        _meshRenderer.SetWorldTransform(Matrix4x4.CreateScale((float)ModelScale));
        _meshRenderer.Render(viewProj, view, _camera.Position, PreviewMode, Wireframe, ShowBounds, ShowBones, CullBackfaces);
        if (AnimateWater) RequestNextFrameRendering(); // keep frames coming so the water animates

        // M36/M60: for Play All, only keep placements near and inside the camera frustum active.
        if (ParticlePlayback is { } playback)
            UpdateActiveParticleSims(playback, viewProj);

        // Advance + draw live VFX particles on top of the scene, then keep requesting frames so they animate.
        if (_particleRenderer is { } prend && ParticlePlayback is not null)
        {
            // M61: distortion particles (heat haze) refract a stable copy of the already-rendered scene.
            // Avoid the framebuffer copy entirely for the common case where no active system uses distortion.
            if (_particleSims.Any(static s => s.Emitters.Any(static e => e.Def.Distortion is not null)))
                prend.CaptureScene(w, h);
            float dt = _particleClock.IsRunning ? (float)_particleClock.Elapsed.TotalSeconds : 1f / 60f;
            _particleClock.Restart();
            dt = ParticlePaused ? 0f : dt * (float)ParticleSpeed;   // M46: editor speed/pause controls
            // M48 wing flap: CPU-skin animated mesh primitives (butterflies) at the looping emitter age
            // and push the new positions before drawing. Tiny meshes (~100 verts) — negligible cost.
            foreach (var (es, anim) in _animatedMeshEmitters)
            {
                float t = anim.Clip.Duration > 1e-3f ? es.EmitterAge % anim.Clip.Duration : 0f;
                var frame = SkinnedMeshAnimator.Skin(anim.Mesh, anim.Skeleton, anim.Clip, t);
                prend.UpdateEmitterMeshPositions(es, frame.Positions);
            }
            // M116: missiles fly — travelling systems lerp from their spawn point to TravelTo after
            // their StartDelay, re-anchoring the sim the same way bone attachment does.
            if (dt > 0f)
                foreach (var (item, sim) in _particleSimCache)
                {
                    if (item.TravelTo is not { } dest || item.TravelSeconds <= 0f) continue;
                    float elapsed = (_travelElapsed.TryGetValue(item, out var te) ? te : 0f) + dt;
                    _travelElapsed[item] = elapsed;
                    float t01 = Math.Clamp((elapsed - item.StartDelay) / item.TravelSeconds, 0f, 1f);
                    var pos = Vector3.Lerp(item.WorldPos, dest, t01);
                    sim.SetWorldTransform(Matrix4x4.CreateTranslation(pos));
                }

            foreach (var psim in _particleSims)
            {
                psim.Update(dt);
                prend.Render(psim, viewProj, view);
            }
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
    // M89: upload one texture layer to the background renderer (shared image → shared GL id).
    private void UploadBgLayer(IReadOnlyList<TextureImage?>? texs, int slot, Dictionary<TextureImage, uint> uploaded)
    {
        if (_bgRenderer is null) return;
        int count = texs?.Count ?? 0;
        for (int i = 0; i < _bgRenderer.SubmeshCount; i++)
        {
            uint id = 0;
            if (i < count && texs![i] is { } img && !uploaded.TryGetValue(img, out id))
            {
                id = _bgRenderer.UploadTexture(img.Rgba, img.Width, img.Height);
                uploaded[img] = id;
            }
            _bgRenderer.SetSubmeshLayer(i, slot, id);
        }
    }

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

    /// <summary>(Re)build the placed prop meshes on the GL thread (M41): register each unique geometry +
    /// texture once (shared by reference), then instance per placement.</summary>
    private void RebuildPropMeshes()
    {
        if (_meshRenderer is null) return;
        _meshRenderer.ClearProps();
        _animatedPropGeoms.Clear();   // M54: rebuilt with the geometries
        var set = PropMeshes;
        if (set is null || set.Instances.Count == 0) return;

        var geoByMesh = new Dictionary<PropMesh, int>(ReferenceEqualityComparer.Instance);
        var texByImage = new Dictionary<TextureImage, uint>(ReferenceEqualityComparer.Instance);
        foreach (var inst in set.Instances)
        {
            if (!geoByMesh.TryGetValue(inst.Mesh, out var handle))
            {
                var subs = inst.Mesh.Submeshes.Select(s =>
                {
                    uint tex = 0;
                    if (s.Texture is { } img)
                    {
                        if (!texByImage.TryGetValue(img, out tex))
                            tex = texByImage[img] = _meshRenderer.UploadPropTexture(img.Rgba, img.Width, img.Height);
                    }
                    return (s.Start, s.Count, tex);
                }).ToList();
                handle = _meshRenderer.RegisterPropGeometry(inst.Mesh.Positions, inst.Mesh.Normals, inst.Mesh.Uvs, inst.Mesh.Indices, subs);
                geoByMesh[inst.Mesh] = handle;
                if (inst.Mesh.CanAnimate) _animatedPropGeoms.Add((handle, inst.Mesh));   // M54 idle playback
            }
            _meshRenderer.AddPropInstance(handle, inst.Transform);
        }
    }

    /// <summary>(Re)build the particle simulator from <see cref="ParticlePlayback"/> and upload each emitter's
    /// sprite (procedural soft-dot fallback when unresolved). Runs on the GL thread. M36.</summary>
    private void RebuildParticleSim()
    {
        if (_particleRenderer is null) return;
        _particleSims.Clear();
        _particleSimCache.Clear();
        _travelElapsed.Clear();
        _particleTextureCache.Clear();
        _particleMeshAnimations.Clear();
        _animatedMeshEmitters.Clear();   // M48: rebuilt with the sims

        var pb = ParticlePlayback;
        _particleRenderer.ClearTextures();
        if (pb is null || pb.Items.Count == 0) { _particleClock.Stop(); return; }

        // Upload every unique sprite once (shared across placements of the same system) + a soft-dot fallback.
        _softDotTex = _particleRenderer.UploadTexture(SoftDot(64), 64, 64);

        foreach (var item in pb.Items)
        {
            if (item.System.Emitters.Count == 0) continue;
            // A stable placement-specific seed prevents every repeated torch/brazier from animating in lockstep.
            int seed = HashCode.Combine(item.System.PathHash,
                BitConverter.SingleToInt32Bits(item.Transform.M41),
                BitConverter.SingleToInt32Bits(item.Transform.M42),
                BitConverter.SingleToInt32Bits(item.Transform.M43));
            var sim = new VfxParticleSimulator(seed);
            sim.SetSystem(item.System, item.Transform);
            if (item.StartDelay > 0f) sim.SetStartDelay(item.StartDelay);   // M91: frame-accurate clip events
            foreach (var es in sim.Emitters)
            {
                // match the state back to its emitter index (by reference — SetSystem keeps the instances)
                int idx = -1;
                for (int i = 0; i < item.System.Emitters.Count; i++)
                    if (ReferenceEquals(item.System.Emitters[i], es.Def)) { idx = i; break; }
                var img = idx >= 0 && idx < item.EmitterTextures.Count ? item.EmitterTextures[idx] : null;
                if (img is null) es.Texture = _softDotTex;
                else
                {
                    if (!_particleTextureCache.TryGetValue(img, out var tex))
                    {
                        tex = _particleRenderer.UploadTexture(img.Rgba, img.Width, img.Height);
                        _particleTextureCache[img] = tex;
                    }
                    es.Texture = tex;
                    if (es.Def.UseTextureAspect)
                    {
                        float cellWidth = img.Width / Math.Max(1f, es.Def.TexDiv.X);
                        float cellHeight = img.Height / Math.Max(1f, es.Def.TexDiv.Y);
                        if (cellHeight > 0f) es.SpriteAspect = Math.Clamp(cellWidth / cellHeight, 0.05f, 20f);
                    }
                }
                var multImg = item.EmitterMultTextures is { } mts && idx >= 0 && idx < mts.Count ? mts[idx] : null;
                if (multImg is not null)
                {
                    if (!_particleTextureCache.TryGetValue(multImg, out var multTex))
                    {
                        multTex = _particleRenderer.UploadTexture(multImg.Rgba, multImg.Width, multImg.Height);
                        _particleTextureCache[multImg] = multTex;
                    }
                    es.TextureMult = multTex;
                }
                var distortionImg = item.EmitterDistortionTextures is { } dts && idx >= 0 && idx < dts.Count ? dts[idx] : null;
                if (distortionImg is not null)
                {
                    if (!_particleTextureCache.TryGetValue(distortionImg, out var distortionTex))
                    {
                        distortionTex = _particleRenderer.UploadTexture(distortionImg.Rgba, distortionImg.Width, distortionImg.Height);
                        _particleTextureCache[distortionImg] = distortionTex;
                    }
                    es.DistortionTexture = distortionTex;
                }
                // M68: particleColorTexture is sampled on the CPU (in the simulator), so hand the emitter the
                // decoded RGBA gradient directly rather than uploading it to GL.
                var colorImg = item.EmitterColorTextures is { } cts && idx >= 0 && idx < cts.Count ? cts[idx] : null;
                if (colorImg is not null)
                {
                    es.ColorGradient = colorImg.Rgba;
                    es.ColorGradientW = colorImg.Width;
                    es.ColorGradientH = colorImg.Height;
                }
                // M47: mesh-primitive emitters draw their .scb/.sco geometry instead of billboards
                var mesh = item.EmitterMeshes is { } ms && idx >= 0 && idx < ms.Count ? ms[idx] : null;
                if (mesh is not null && img is not null)
                {
                    _particleRenderer.UploadEmitterMesh(es, mesh.Positions, mesh.Uvs,
                        mesh.Animation is not null ? mesh.Indices : null);   // skn = indexed; scb = triangle soup
                    if (mesh.Animation is { } anim) _particleMeshAnimations[es] = anim;   // M48 wing flap
                }
                else if (mesh is not null)
                {
                    // Solid-white mesh fallbacks become huge opaque blocks in a full-map preview.
                    // Keep the emitter dormant until its authored mesh texture can be resolved.
                    es.Texture = 0;
                }
            }
            _particleSimCache[item] = sim;
            _particleSims.Add(sim);
        }
        if (pb.CullByCamera) _particleSims.Clear();
        RebuildActiveParticleAnimations();
        _particleClock.Restart();
        RequestNextFrameRendering();
    }

    private void UpdateActiveParticleSims(VfxPlayback playback, Matrix4x4 viewProj)
    {
        if (!playback.CullByCamera) return;

        float maxDistance = Math.Max(6000f, _camera.Distance * 1.35f);
        float maxDistanceSq = maxDistance * maxDistance;
        var wanted = _wantedParticleSims;
        wanted.Clear();
        foreach (var item in playback.Items)
        {
            if (Vector3.DistanceSquared(_lastCamPos, item.WorldPos) > maxDistanceSq) continue;
            var clip = Vector4.Transform(new Vector4(item.WorldPos, 1f), viewProj);
            if (clip.W <= 0f) continue;
            float margin = clip.W * 1.25f;
            if (MathF.Abs(clip.X) > margin || MathF.Abs(clip.Y) > margin || clip.Z < -margin || clip.Z > margin) continue;
            if (_particleSimCache.TryGetValue(item, out var sim)) wanted.Add(sim);
        }

        bool changed = _particleSims.Count != wanted.Count || _particleSims.Any(sim => !wanted.Contains(sim));
        if (!changed) return;
        foreach (var sim in wanted)
            if (!_particleSims.Contains(sim, ReferenceEqualityComparer.Instance)) sim.Reset();
        _particleSims.Clear();
        _particleSims.AddRange(wanted);
        RebuildActiveParticleAnimations();
    }

    private void RebuildActiveParticleAnimations()
    {
        _animatedMeshEmitters.Clear();
        foreach (var sim in _particleSims)
        foreach (var es in sim.Emitters)
            if (_particleMeshAnimations.TryGetValue(es, out var anim)) _animatedMeshEmitters.Add((es, anim));
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
            // tight core: glow fades out by ~55% of the quad radius so the placeholder reads as a small dot
            float a = Math.Clamp(1f - MathF.Sqrt(dx * dx + dy * dy) * 1.8f, 0f, 1f);
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
        else if (change.Property == BackgroundMeshProperty) { _bgMeshDirty = true; _bgTexDirty = true; RequestNextFrameRendering(); }
        else if (change.Property == BackgroundTexturesProperty || change.Property == BackgroundBlendTexturesProperty
                 || change.Property == BackgroundColor1TexturesProperty || change.Property == BackgroundColor2TexturesProperty
                 || change.Property == BackgroundColor3TexturesProperty) { _bgTexDirty = true; RequestNextFrameRendering(); }
        else if (change.Property == BackgroundVisibleProperty || change.Property == BackgroundVertexLightProperty
                 || change.Property == BackgroundBrightnessProperty || change.Property == ShowGridProperty
                 || change.Property == UseVertexLightmapProperty || change.Property == VertexLightmapScaleProperty) { RequestNextFrameRendering(); }
        else if (change.Property == ModelScaleProperty) { _skinDirty = true; RequestNextFrameRendering(); }   // M90: rescale attached VFX too
        else if (change.Property == BackgroundOffsetProperty || change.Property == BackgroundRotationProperty)
        { _dynamicLightsDirty = true; RequestNextFrameRendering(); }   // M89: move/rotate lights with the map
        else if (change.Property == ModelSubmeshMaterialsProperty) { _materialsDirty = true; RequestNextFrameRendering(); }
        else if (change.Property == MeshVerticesRevisionProperty)
        {
            // GL buffer uploads need the GL context current — only true inside OnOpenGlRender, never
            // here on the UI thread. Flag it and do the actual UpdateVertices in the render loop.
            _verticesDirty = true;
            RequestNextFrameRendering();
        }
        else if (change.Property == SelectionBoxesProperty || change.Property == GroupBoundsMinProperty
                 || change.Property == GroupBoundsMaxProperty || change.Property == GizmoPivotProperty
                 || change.Property == TargetDummyPositionProperty
                 || change.Property == SkyboxProperty && SetSkyboxDirty()
                 || change.Property == GizmoModeProperty || change.Property == GizmoAxesProperty)
        { RequestNextFrameRendering(); }
        else if (change.Property == SkeletonProperty) { _bonesDirty = true; _skinDirty = true; RequestNextFrameRendering(); }
        else if (change.Property == AnimationClipProperty || change.Property == AnimationTimeProperty) { _skinDirty = true; RequestNextFrameRendering(); }
        else if (change.Property == WireframeProperty || change.Property == ShowBonesProperty
                 || change.Property == ShowBoundsProperty || change.Property == PreviewModeProperty
                 || change.Property == CullBackfacesProperty)
        { _skinDirty = true; RequestNextFrameRendering(); }
        else if (change.Property == ParticleMarkersProperty || change.Property == SelectedParticlePositionProperty
                 || change.Property == PropMarkersProperty || change.Property == ProbeMarkersProperty
                 || change.Property == SoundMarkersProperty || change.Property == BucketGridLinesProperty)
        { _particlesDirty = true; RequestNextFrameRendering(); }
        else if (change.Property == ParticlePlaybackProperty)
        { _particlePlaybackDirty = true; RequestNextFrameRendering(); }
        else if (change.Property == AnimateWaterProperty || change.Property == LightmapScaleProperty || change.Property == SunPropertiesProperty
                 || change.Property == HighlightSubmeshesProperty || change.Property == PlayPropAnimationsProperty
                 || change.Property == LightmapsEnabledProperty || change.Property == DynamicLightsEnabledProperty
                 || change.Property == DynamicLightIntensityProperty || change.Property == DynamicLightRadiusScaleProperty)
        { RequestNextFrameRendering(); } // M44/M45/M50b/M54/M69/M70: water + lightmap scale + outline + prop idles + lightmap toggle + dynamic lights
        else if (change.Property == DynamicLightsProperty)   // M70: new Light.dat table -> re-upload + re-mark on the GL thread
        { _dynamicLightsDirty = true; _lightMarkersDirty = true; RequestNextFrameRendering(); }
        else if (change.Property == GrassTintTextureProperty || change.Property == GrassTintRectProperty)   // M78
        { _grassTintDirty = true; RequestNextFrameRendering(); }
        else if (change.Property == DynamicLightPositionScaleProperty || change.Property == DynamicLightScaleXProperty
                 || change.Property == DynamicLightScaleZProperty || change.Property == DynamicLightOffsetXProperty
                 || change.Property == DynamicLightOffsetZProperty || change.Property == ShowLightMarkersProperty)
        { _lightMarkersDirty = true; RequestNextFrameRendering(); }   // M71: light transform moves the icons too
        else if (change.Property == PropMeshesProperty)
        { _propMeshesDirty = true; RequestNextFrameRendering(); }
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

                // M86: clip particle events ride their bone — re-anchor attached VFX systems to the
                // animated bone transform every skinned frame (live particles keep flying).
                // M90: bone globals are pre-scale, so apply the preview model scale on top.
                if (frame.BoneGlobals is { } bones)
                {
                    var scaleM = ModelScale is 1.0 ? Matrix4x4.Identity : Matrix4x4.CreateScale((float)ModelScale);
                    foreach (var (item, sim) in _particleSimCache)
                        if (item.AttachBone is { Length: > 0 } bone && bones.TryGetValue(bone, out var bm))
                            sim.SetWorldTransform(scaleM.IsIdentity ? bm : bm * scaleM);
                }
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
        // The viewport pre-mirrors Riot world X before applying the camera view. Camera state lives
        // in that mirrored space too (CameraMoved converts it back), so mirror an external focus point.
        _camera.Target = new Vector3(-p.X, p.Y, p.Z);
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
