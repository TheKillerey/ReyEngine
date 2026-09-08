using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core.Decoding;
using ReyEngine.Formats.Animation;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Skeletons;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M50/M55: character/mesh preview in its OWN window (separate viewport), so the main viewport stays
/// dedicated to the map. Holds its own copies of the mesh/skeleton/textures — no shared state with the
/// map viewport. M55 adds animation playback (own AnimationInspectorViewModel: list + play/loop/speed)
/// and the champion's VFX library (played at the model origin in the preview viewport).
/// </summary>
/// <summary>M84: one submesh visibility toggle in the Model Preview.</summary>
public sealed partial class SubmeshToggleViewModel : ObservableObject
{
    public required string Name { get; init; }
    /// <summary>M643: the submesh's index in the mesh, which is what the viewport outlines by.</summary>
    public int Index { get; init; }
    [ObservableProperty] private bool _isVisible = true;
    /// <summary>M643: the name of the material this submesh draws with - its own materialOverride entry,
    /// else the skin's default. Empty until the skin's materials have loaded.</summary>
    [ObservableProperty] private string _materialName = "";
    public bool HasMaterial => MaterialName.Length > 0;
    partial void OnMaterialNameChanged(string value) => OnPropertyChanged(nameof(HasMaterial));
    public Action? Changed;
    partial void OnIsVisibleChanged(bool value) => Changed?.Invoke();
}

public sealed partial class MeshPreviewViewModel : ObservableObject
{
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _stats = "";
    [ObservableProperty] private MeshAsset? _mesh;
    [ObservableProperty] private SkeletonAsset? _skeleton;
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _textures;
    // M142: per-submesh preview materials for the SUBJECT mesh — used by the legacy-map viewer to flag
    // Map10's baked height-blend ground submeshes (CompositeGround). Null for champions/props.
    [ObservableProperty] private IReadOnlyList<ReyEngine.Rendering.ViewportMeshRenderer.SubmeshMaterial>? _materials;
    // M142.2: extra SUBJECT texture layers for the legacy-map viewer — height-scale map (mask slot),
    // the four-blend colour layers (gradient/emissive/matcap slots) and the baked composite (lightmap
    // slot). All null for champions/props.
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _maskTextures;
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _gradientTextures;
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _emissiveTextures;
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _matCapTextures;
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _lightmapTextures;
    // M142.4: legacy NVR statics use PrimaryColor AS their baked lightmap (night mood). Off for champions.
    [ObservableProperty] private bool _useVertexLightmap;
    [ObservableProperty] private double _vertexLightmapScale = 2.0;
    // M148: legacy-map (NVR) render settings, exposed in the preview window so any map can be tuned.
    // IsLegacyMap gates the panel; FourBlend is on for maps with real BLEND_MAPs (Dominion), off for
    // height-blend (Twisted Treeline, which uses the composite atlas) and plain maps (Map4).
    [ObservableProperty] private bool _isLegacyMap;
    [ObservableProperty] private bool _nvrFourBlend;
    [ObservableProperty] private double _nvrVertexLight;          // additive baked-vertex term (M89 "Ground")
    [ObservableProperty] private double _nvrBrightness = 0.55;    // flat sun/sky for mask-blend + plain maps
    [ObservableProperty] private bool _nvrHeightBlend;            // read-only indicator: composite ground map
    // M149: the level's authored sun/ambient (terrain.inibin / sun.ini) + whether to use it.
    [ObservableProperty] private Formats.Environment.NvrSunSettings? _nvrSun;
    [ObservableProperty] private bool _nvrUseMapSun = true;
    public bool HasNvrSun => NvrSun is not null;
    public string NvrSunSource => NvrSun is { } s ? $"Environment: {s.Source}" : "No environment file — flat sun/sky";
    partial void OnNvrSunChanged(Formats.Environment.NvrSunSettings? value)
    { OnPropertyChanged(nameof(HasNvrSun)); OnPropertyChanged(nameof(NvrSunSource)); }
    [ObservableProperty] private bool _showBones;
    [ObservableProperty] private bool _wireframe;
    [ObservableProperty] private bool _cullBackfaces = true;
    /// <summary>M672: the shading mode, in the map viewport's numbering (0 Basic, 1 Riot Approx, 2..14
    /// the debug views - ViewportMeshRenderer's uMode and ShaderPreviewRenderer.DebugMode agree on it).
    /// One property for both surfaces, as the map window does it: the GL control is bound in XAML, the
    /// D3D11 surface takes it per frame in RenderDx11Frame. On D3D11 the two shaded modes are the same
    /// picture - Riot's own shaders draw below the first debug mode - and "Debug · Base" is the diffuse
    /// as painted, unlit, which is what a plain model viewer shows and what the shaded views are not.</summary>
    [ObservableProperty] private int _previewMode;

    // ---- M55 animation: reuses the same self-ticking inspector VM the main window uses ----
    public AnimationInspectorViewModel Animation { get; } = new();
    [ObservableProperty] private AnimationClip? _currentAnimation;
    [ObservableProperty] private double _animationTime;

    // ---- M55 champion VFX: the skin's effect library, played at the model origin ----
    public ObservableCollection<VfxSystemItemViewModel> VfxSystems { get; } = new();
    [ObservableProperty] private VfxSystemItemViewModel? _selectedVfx;
    [ObservableProperty] private VfxPlayback? _playback;
    [ObservableProperty] private bool _hasVfx;

    private IReadOnlyDictionary<uint, VfxSystemDefinition> _vfxDefs =
        new Dictionary<uint, VfxSystemDefinition>();
    // M86: skin bin ResourceResolver — effect-key hash → VFX object hash (how clips reference effects)
    private IReadOnlyDictionary<uint, uint> _vfxResourceMap = new Dictionary<uint, uint>();

    // wired once by MainWindowViewModel
    public Action<string>? PlaySoundEvent;   // M90: clip SFX via the champion's Wwise banks
    public Action? StopSounds;
    public Func<VfxSystemDefinition, IReadOnlyList<TextureImage?>>? ResolveTextures;
    /// <summary>M634: Riot's TEXTUREMULT stage - the multiplier / mask texture. The one stage this window
    /// never resolved: the map viewport and the particle editor have passed it since M117, and here every
    /// MULT_PASS emitter got the renderer's white stand-in, which multiplies by one. That is what "the
    /// mask is not applied" was.</summary>
    public Func<VfxSystemDefinition, IReadOnlyList<TextureImage?>>? ResolveMultTextures;
    public Func<VfxSystemDefinition, IReadOnlyList<TextureImage?>>? ResolveDistortionTextures;
    public Func<VfxSystemDefinition, IReadOnlyList<TextureImage?>>? ResolveColorTextures;   // M68
    public Func<VfxSystemDefinition, IReadOnlyList<TextureImage?>>? ResolveErosionTextures;   // M174 (2.1)
    public Func<VfxSystemDefinition, IReadOnlyList<TextureImage?>>? ResolvePaletteTextures;   // M175 (2.6)
    public Func<VfxSystemDefinition, IReadOnlyList<CubemapImage?>>? ResolveReflectionCubemaps;   // M181 (2.12)
    public Func<VfxSystemDefinition, IReadOnlyList<StaticMeshData?>?>? ResolveMeshes;

    private double _lastAnimTime;

    public MeshPreviewViewModel()
    {
        // M116: two playback modes. ANIMATIONS = the pure .anm (no VFX, no SFX, no VO); EVENTS = the
        // full authored sequence. EventPlaybackActive flips when PlayEvent drives the clip selection.
        Animation.ClipChanged = clip =>
        {
            CurrentAnimation = clip; _lastAnimTime = 0; ApplyAutoVisibility();
            if (_eventPlaybackActive)
            {
                ApplyClipParticles(); ResetSoundSchedule(stopCurrent: true); TryAutoVoice();   // M85/M86/M90/M95c
            }
            else
            {
                _eventBundle = null; _activeEvent = null;
                if (SelectedVfx is null) Playback = null;
                ClearSoundSchedule();
            }
        };
        Animation.TimeChanged = t =>
        {
            // M86: clip event VFX are one-shot — respawn them each time the looping clip wraps around
            // (manual VFX picks are left alone). Backward jump in time = the loop restarted.
            if (t + 0.05 < _lastAnimTime && _eventPlaybackActive)
            {
                if (SelectedVfx is null) ApplyClipParticles();
                ResetSoundSchedule(stopCurrent: false);   // M91: refire from the top; one-shots finish across the seam
            }
            _lastAnimTime = t;
            AnimationTime = t;
            if (_eventPlaybackActive) TickSoundSchedule(t);   // M91: frame-accurate SFX
        };
    }

    public void Show(string title, MeshAsset mesh, SkeletonAsset? skeleton, IReadOnlyList<TextureImage?>? textures)
    {
        Title = title;
        Mesh = mesh;
        Skeleton = skeleton;
        Textures = textures;
        Materials = null;   // M142: cleared per preview; only the legacy-map viewer sets it after Show
        MaskTextures = null; GradientTextures = null; EmissiveTextures = null;   // M142.2: map-only layers
        MatCapTextures = null; LightmapTextures = null;
        UseVertexLightmap = false;   // M142.4: map-only; champions use normal lighting
        // M148: clear the legacy-map render mode so a champion preview never inherits a map's lighting
        IsLegacyMap = false; NvrFourBlend = false; NvrHeightBlend = false;
        NvrVertexLight = 0; NvrBrightness = 0.55;
        NvrSun = null; NvrUseMapSun = true;   // M149
        ShowBones = skeleton is not null;
        Stats = $"{mesh.VertexCount:n0} verts · {mesh.TriangleCount:n0} tris · {mesh.SubMeshes.Count} submesh(es)" +
                (skeleton is not null ? $" · {skeleton.BoneCount} bones" : "");
        CurrentAnimation = null;
        AnimationTime = 0;
        Animation.SetSkeleton(skeleton?.BoneCount ?? 0);
        RebuildCasterBoneOptions();   // M663: this skeleton's joints, and a default among them
        Playback = null;
        SelectedVfx = null;
        ImagePreview = null;    // M120: a model preview replaces a texture preview
        StopSounds?.Invoke();   // M90: previous champion's SFX must not bleed into the new preview
        // M142.1: a legacy map (no backdrop) sets its Light.dat lights directly — drop them when the next
        // preview isn't backdrop-lit, or the previous map's 95 torches keep lighting the new champion.
        if (!HasBackground) { BackgroundLights = null; BackgroundLightsEnabled = false; }

        // M84: per-submesh visibility toggles (all visible on load)
        Submeshes.Clear();
        foreach (var s in mesh.SubMeshes)
        {
            var t = new SubmeshToggleViewModel { Index = Submeshes.Count, Name = string.IsNullOrEmpty(s.Material) ? $"submesh {Submeshes.Count}" : s.Material };
            t.Changed = RebuildSubmeshVisibility;
            Submeshes.Add(t);
        }
        HasSubmeshes = Submeshes.Count > 1;
        RebuildSubmeshVisibility();
        RefreshOutliner();   // M643: material names, selection and outline follow the new mesh
    }

    // ---- M88: NVR map backdrop (character stands in-map, lit by the map's Light.dat) ----
    [ObservableProperty] private MeshAsset? _backgroundMesh;
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _backgroundTextures;
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _backgroundBlendTextures;   // M89 four-blend
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _backgroundColor1Textures;
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _backgroundColor2Textures;
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _backgroundColor3Textures;
    // M142.9: give the backdrop the same M142 treatment as the standalone legacy-map viewer.
    [ObservableProperty] private IReadOnlyList<ReyEngine.Rendering.ViewportMeshRenderer.SubmeshMaterial>? _backgroundMaterials;
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _backgroundMaskTextures;
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _backgroundLightmapTextures;
    [ObservableProperty] private bool _backgroundVisible = true;
    [ObservableProperty] private IReadOnlyList<ReyEngine.Formats.Lighting.PointLight>? _backgroundLights;
    [ObservableProperty] private bool _backgroundLightsEnabled;
    [ObservableProperty] private string? _backgroundMapName;
    [ObservableProperty] private bool _hasBackground;

    // M89: backdrop tuning — move/rotate the whole map, boost the (Light.dat) lights, tune baked shading.
    // Defaults are the user-tuned Crystal Scar (Map8) hero shot.
    [ObservableProperty] private double _backgroundOffsetX = -6400;
    [ObservableProperty] private double _backgroundOffsetY = -60;
    [ObservableProperty] private double _backgroundOffsetZ = 2000;
    [ObservableProperty] private double _backgroundRotation = 180;   // degrees about Y
    [ObservableProperty] private System.Numerics.Vector3 _backgroundOffset = new(-6400, -60, 2000);
    [ObservableProperty] private double _backgroundLightIntensity = 8.0;
    [ObservableProperty] private double _backgroundVertexLight;      // 0 = baked vertex shading off by default
    [ObservableProperty] private double _backgroundBrightness = 0.55;   // base sun/sky on the map — dark, so Light.dat pops
    [ObservableProperty] private bool _showGrid = true;

    partial void OnBackgroundOffsetXChanged(double value) => UpdateBackgroundOffset();
    partial void OnBackgroundOffsetYChanged(double value) => UpdateBackgroundOffset();
    partial void OnBackgroundOffsetZChanged(double value) => UpdateBackgroundOffset();
    private void UpdateBackgroundOffset() =>
        BackgroundOffset = new System.Numerics.Vector3((float)BackgroundOffsetX, (float)BackgroundOffsetY, (float)BackgroundOffsetZ);

    [RelayCommand] private void ResetBackgroundOffset()
    { BackgroundOffsetX = -6400; BackgroundOffsetY = -60; BackgroundOffsetZ = 2000; BackgroundRotation = 180; }

    /// <summary>Attach (or clear) the loaded NVR backdrop. Lights are enabled only when present.</summary>
    public void SetBackground(Services.MapPreviewBackground? bg)
    {
        BackgroundMesh = bg?.Mesh;
        BackgroundTextures = bg?.SubmeshTextures;
        BackgroundBlendTextures = bg?.SubmeshBlend;
        BackgroundColor1Textures = bg?.SubmeshColor1;
        BackgroundColor2Textures = bg?.SubmeshColor2;
        BackgroundColor3Textures = bg?.SubmeshColor3;
        BackgroundMaterials = bg?.SubmeshMaterials;         // M142.9
        BackgroundMaskTextures = bg?.SubmeshMask;
        BackgroundLightmapTextures = bg?.SubmeshLightmap;
        BackgroundLights = bg?.Lights;
        BackgroundLightsEnabled = bg?.Lights is { Count: > 0 };
        BackgroundMapName = bg?.MapName;
        HasBackground = bg?.Mesh is not null;
    }

    [RelayCommand] private void ToggleBackground() => BackgroundVisible = !BackgroundVisible;

    // ---- M84: per-submesh visibility ----
    public ObservableCollection<SubmeshToggleViewModel> Submeshes { get; } = new();
    [ObservableProperty] private bool _hasSubmeshes;
    [ObservableProperty] private IReadOnlyList<bool>? _submeshVisible;

    private void RebuildSubmeshVisibility() =>
        SubmeshVisible = Submeshes.Select(s => s.IsVisible).ToList();

    [RelayCommand] private void ShowAllSubmeshes() { foreach (var s in Submeshes) s.IsVisible = true; }
    [RelayCommand] private void HideAllSubmeshes() { foreach (var s in Submeshes) s.IsVisible = false; }

    // ---- M85: game-accurate submesh visibility — skin bin initial-hide + per-clip show/hide events.
    // AUTO (default): selecting an animation applies the game's lists; MANUAL: checkboxes are yours. ----
    [ObservableProperty] private bool _autoSubmeshVisibility = true;
    private IReadOnlyList<string> _initialHide = Array.Empty<string>();
    private IReadOnlyDictionary<string, Formats.Skeletons.AnimClipInfo>? _clipsByAnm;
    /// <summary>M663: EVERY clip, not one per .anm file. Riot points several clips at one file, so the
    /// by-file view silently drops all but the first - which is fine for "what does the animation now
    /// playing hide?" and wrong for anything that reads the clip list as a whole.</summary>
    private IReadOnlyList<Formats.Skeletons.AnimClipInfo> _allClips = Array.Empty<Formats.Skeletons.AnimClipInfo>();

    /// <summary>Provide the skin's initial-hide list + animation-graph clips (keyed by .anm file name),
    /// and the full clip list beside it.</summary>
    public void SetSubmeshRules(IReadOnlyList<string> initialHide,
        IReadOnlyDictionary<string, Formats.Skeletons.AnimClipInfo>? clipsByAnm,
        IReadOnlyList<Formats.Skeletons.AnimClipInfo>? allClips = null)
    {
        _initialHide = initialHide;
        _clipsByAnm = clipsByAnm;
        _allClips = allClips ?? clipsByAnm?.Values.ToList() ?? (IReadOnlyList<Formats.Skeletons.AnimClipInfo>)Array.Empty<Formats.Skeletons.AnimClipInfo>();
        ApplyAutoVisibility();
    }

    partial void OnAutoSubmeshVisibilityChanged(bool value) { if (value) ApplyAutoVisibility(); }

    /// <summary>M86/M91: the playing clip's ParticleEventData → play those VFX bone-attached, like in-game.
    /// Each item carries its StartFrame as a sim delay, so effects fire at their authored moment.</summary>
    private void ApplyClipParticles()
    {
        if (Animation.SelectedAnimation?.Name is not { } anm) return;
        var clip = _clipsByAnm?.GetValueOrDefault(anm);
        if (clip is not { ParticleEvents.Count: > 0 })
        {
            // no clip-authored events: the event bundle (spell composite) still plays (M116)
            if (_eventBundle is { Count: > 0 }) Playback = new VfxPlayback(_eventBundle);
            else if (SelectedVfx is null) Playback = null;
            return;
        }

        var items = new List<VfxPlaybackItem>();
        foreach (var ev in clip.ParticleEvents!)
        {
            // primary: the skin bin's ResourceResolver map (effect key → system object), like the game
            uint keyHash = ev.EffectHash != 0 ? ev.EffectHash
                : ReyEngine.Core.Hashing.HashAlgorithms.Fnv1a(ev.EffectName);
            VfxSystemDefinition? def = null;
            if (_vfxResourceMap.TryGetValue(keyHash, out var objHash))
                _vfxDefs.TryGetValue(objHash, out def);
            def ??= _vfxDefs.Values.FirstOrDefault(d =>
                (ev.EffectName.Length > 0 && string.Equals(d.Name, ev.EffectName, StringComparison.OrdinalIgnoreCase))
                || (ev.EffectHash != 0 && (d.PathHash == ev.EffectHash
                    || ReyEngine.Core.Hashing.HashAlgorithms.Fnv1a(d.Name) == ev.EffectHash)));
            if (def is null || !def.Emitters.Any(e => e.IsVisual)) continue;
            // M114: target-bound systems ("_tar") anchor at the dummy, not on the caster's bones.
            var anchor = AnchorFor(def);
            bool atDummy = anchor != System.Numerics.Vector3.Zero;
            // M634: every stage, through the one builder - this copy had no multiplier texture and no
            // child systems, so a clip event's mask was white and its children never spawned.
            // M635: a clip event at the dummy faces the caster; a bone-attached one takes its frame from
            // the bone every frame, so its placement here is only where it starts.
            items.Add(BuildItem(def, atDummy
                    ? VfxCastFrame.Toward(anchor, CharacterPosition, anchor)
                    : System.Numerics.Matrix4x4.CreateTranslation(anchor)) with
            {
                AttachBone = atDummy ? null : ResolveBoneName(ev),
                StartDelay = MathF.Max(0f, ev.StartFrame) / ClipFps(),   // M91: fire at the authored frame
            });
        }
        if (_eventBundle is { Count: > 0 }) items.AddRange(_eventBundle);   // M116: spell composite rides along
        // M667: the whole playback about to reach the renderers, in one line. Item counts and bone
        // attachments are what a native fault in the GL upload would need to be traced back to.
        LogDx11?.Invoke("Event", $"clip '{anm}': {items.Count} item(s) to the viewport, "
            + $"{items.Count(i => i.AttachBone is { Length: > 0 })} bone-attached, "
            + $"{items.Count(i => i.EmitterMeshes is not null)} with mesh emitters");
        if (items.Count > 0) Playback = new VfxPlayback(items);
    }

    // ---- M114: target dummy — a stand-in unit so targeted-spell VFX have somewhere to land ----

    [ObservableProperty] private bool _targetDummyEnabled;
    [ObservableProperty] private double _dummyX = 350;
    [ObservableProperty] private double _dummyY;
    [ObservableProperty] private double _dummyZ;
    /// <summary>Play the manually selected VFX at the dummy even when its name isn't target-ish.</summary>
    [ObservableProperty] private bool _playSelectedAtDummy;

    /// <summary>The dummy's base position; null when disabled.</summary>
    public System.Numerics.Vector3? TargetDummyPosition =>
        TargetDummyEnabled ? new System.Numerics.Vector3((float)DummyX, (float)DummyY, (float)DummyZ) : null;

    /// <summary>Gizmo pivot (bound by the preview window) — the dummy is the only gizmo user here.</summary>
    public System.Numerics.Vector3? DummyGizmoPivot => TargetDummyPosition;

    // ---- M115: render Riot's practice-tool dummy instead of the cube when its assets load ----

    /// <summary>Host hook: decode Riot's target dummy from Map11.wad (cached; null = keep the cube).</summary>
    public Func<PropMesh?>? LoadDummyMesh { get; set; }
    private PropMesh? _dummyMesh;
    private bool _dummyMeshTried;

    /// <summary>The real dummy as a one-instance prop set; null when disabled or assets unavailable.</summary>
    [ObservableProperty] private PropRenderSet? _dummyProps;

    /// <summary>Cube fallback position — only set while the real model isn't available.</summary>
    public System.Numerics.Vector3? DummyCubePosition => DummyProps is null ? TargetDummyPosition : null;

    private void RebuildDummyProps()
    {
        if (TargetDummyPosition is not { } pos) { DummyProps = null; OnPropertyChanged(nameof(DummyCubePosition)); return; }
        if (!_dummyMeshTried) { _dummyMeshTried = true; _dummyMesh = LoadDummyMesh?.Invoke(); }
        if (_dummyMesh is null) { DummyProps = null; OnPropertyChanged(nameof(DummyCubePosition)); return; }

        // Face the champion (at the origin), the way a unit would face its attacker.
        float yaw = MathF.Atan2(-pos.X, -pos.Z);
        var transform = System.Numerics.Matrix4x4.CreateRotationY(yaw)
                        * System.Numerics.Matrix4x4.CreateTranslation(pos);
        DummyProps = new PropRenderSet(new[] { new PropInstanceData(_dummyMesh, transform) });
        OnPropertyChanged(nameof(DummyCubePosition));
    }

    partial void OnTargetDummyEnabledChanged(bool value) => OnDummyMoved();
    partial void OnDummyXChanged(double value) => OnDummyMoved();
    partial void OnDummyYChanged(double value) => OnDummyMoved();
    partial void OnDummyZChanged(double value) => OnDummyMoved();
    partial void OnPlaySelectedAtDummyChanged(bool value) => ReplaySelectedOrClip();

    private void OnDummyMoved()
    {
        OnPropertyChanged(nameof(TargetDummyPosition));
        OnPropertyChanged(nameof(DummyGizmoPivot));
        RebuildDummyProps();      // M115: the model rides the gizmo
        ReplaySelectedOrClip();   // re-anchor whatever is playing
    }

    /// <summary>Move the dummy from a viewport gizmo drag (world axis + amount).</summary>
    public void MoveDummy(System.Numerics.Vector3 delta)
    {
        _dummyX += delta.X; _dummyY += delta.Y; _dummyZ += delta.Z;
        OnPropertyChanged(nameof(DummyX)); OnPropertyChanged(nameof(DummyY)); OnPropertyChanged(nameof(DummyZ));
        OnDummyMoved();
    }

    [RelayCommand]
    private void ResetDummy() { DummyX = 350; DummyY = 0; DummyZ = 0; }

    /// <summary>Riot's naming convention for target-attached systems — "_tar" (e.g. Kayn_Base_Primary_
    /// R_tar_enemy). Verified against Kayn: matches exactly the systems the game plays on the target.</summary>
    private static bool IsTargetVfxName(string name) =>
        name.Contains("_tar", StringComparison.OrdinalIgnoreCase);

    /// <summary>Where a system should be anchored: at the dummy for target-bound systems (when the dummy
    /// is enabled), else at the champion's root.</summary>
    // ---- M663: the bone a name-assembled caster effect rides ----

    /// <summary>Every joint in the loaded skeleton, plus the world-space opt-out at the head.</summary>
    public ObservableCollection<string> CasterBoneOptions { get; } = new();

    /// <summary>The world-space option's label — chosen, nothing is attached and the old behaviour is back.</summary>
    public const string NoCasterBone = "(none - world space)";

    /// <summary>
    /// M663: which bone the composite's caster systems ride. A clip that authors its own ParticleEventData
    /// always wins over this; this is only for the composite assembled from VFX names, which carries no
    /// binding at all - see <see cref="Formats.Characters.CasterBone"/> for why the default is a locator
    /// and not a hand.
    /// </summary>
    [ObservableProperty] private string _selectedCasterBone = NoCasterBone;

    /// <summary>False for a preview with no skeleton — then the picker would offer only the opt-out.</summary>
    [ObservableProperty] private bool _hasCasterBones;

    partial void OnSelectedCasterBoneChanged(string value) => ReplaySelectedOrClip();

    /// <summary>The bone to attach to, or null for world space.</summary>
    private string? CasterBoneOrNull =>
        SelectedCasterBone is { Length: > 0 } b && b != NoCasterBone ? b : null;

    private void RebuildCasterBoneOptions()
    {
        CasterBoneOptions.Clear();
        CasterBoneOptions.Add(NoCasterBone);
        if (Skeleton is not { Joints.Count: > 0 } skel)
        {
            SelectedCasterBone = NoCasterBone; HasCasterBones = false; return;
        }
        foreach (var j in skel.Joints) CasterBoneOptions.Add(j.Name);
        HasCasterBones = true;
        SelectedCasterBone = Formats.Characters.CasterBone.Choose(skel.Joints.Select(j => j.Name))
                             ?? NoCasterBone;
    }

    private System.Numerics.Vector3 AnchorFor(VfxSystemDefinition def, bool forceDummy = false) =>
        TargetDummyPosition is { } dummy && (forceDummy || IsTargetVfxName(def.Name))
            ? dummy
            : System.Numerics.Vector3.Zero;

    private void ReplaySelectedOrClip()
    {
        if (_activeEvent is { } ev && _eventPlaybackActive) { _eventBundle = BuildEventBundle(ev); ApplyClipParticles(); return; }
        if (SelectedVfx is not null) OnSelectedVfxChanged(SelectedVfx);
        else ApplyClipParticles();
    }

    // ---- M116: EVENTS — full sequences (anim + VFX + SFX + caster/target/missile routing) ----

    public ObservableCollection<Formats.Vfx.ChampionEvent> ChampionEvents { get; } = new();
    [ObservableProperty] private Formats.Vfx.ChampionEvent? _selectedEvent;
    [ObservableProperty] private bool _hasEvents;

    /// <summary>True while an EVENTS-section playback drives the clip (full VFX/SFX pipeline);
    /// false = the ANIMATIONS section is in charge and plays the bare .anm.</summary>
    private bool _eventPlaybackActive;
    private List<VfxPlaybackItem>? _eventBundle;   // the spell composite riding the current event
    private Formats.Vfx.ChampionEvent? _activeEvent;

    partial void OnSelectedEventChanged(Formats.Vfx.ChampionEvent? value)
    {
        if (value is not null) PlayEvent(value);
    }

    [RelayCommand]
    private void ReplayEvent() { if (_activeEvent is { } ev) PlayEvent(ev); }

    [RelayCommand]
    private void StopEvent()
    {
        _eventPlaybackActive = false;
        _eventBundle = null;
        _activeEvent = null;
        SelectedEvent = null;
        Playback = null;
        ClearSoundSchedule();
        Animation.SelectedAnimation = null;
    }

    private void PlayEvent(Formats.Vfx.ChampionEvent ev)
    {
        // M667: event playback used to log nothing at all, so a session that died during one ended on
        // whatever happened to be logged last - an arena load, in the case that prompted this - and the
        // wrong thing got investigated. Every event, composite and clip alike, is played through the real
        // D3D11 driver headlessly without a fault (all 50 of Locke's); the GL path cannot be, so it says
        // what it is doing instead.
        LogDx11?.Invoke("Event", $"play '{ev.Name}' ({ev.Description}) - building composite");
        SelectedVfx = null;                       // manual pick and events are exclusive
        _activeEvent = ev;
        _eventPlaybackActive = true;
        if (ev.NeedsTarget && !TargetDummyEnabled) TargetDummyEnabled = true;   // _tar plays ONLY on the dummy
        _eventBundle = BuildEventBundle(ev);
        LogDx11?.Invoke("Event", $"'{ev.Name}': composite {_eventBundle.Count} item(s), "
            + $"clip {(ev.ClipAnmFile ?? "(none)")}");

        // The clip drives timing when the event has one; else the bundle plays on its own.
        var entry = ev.ClipAnmFile is { } anm
            ? Animation.Animations.FirstOrDefault(a => string.Equals(a.Name, anm, StringComparison.OrdinalIgnoreCase))
            : null;
        if (entry is not null)
        {
            if (ReferenceEquals(Animation.SelectedAnimation, entry)) ApplyClipParticles();  // re-fire same clip
            else Animation.SelectedAnimation = entry;                                        // ClipChanged runs the full path
        }
        else
        {
            Playback = _eventBundle is { Count: > 0 } ? new VfxPlayback(_eventBundle) : null;
        }
    }

    /// <summary>M631: the ability records, when the host could read them. Empty for a non-champion, and
    /// for the one shipped CharacterRecord that has no spells at all.</summary>
    private IReadOnlyList<Formats.Characters.AbilitySlot> _abilities = Array.Empty<Formats.Characters.AbilitySlot>();

    public void SetAbilities(IReadOnlyList<Formats.Characters.AbilitySlot> abilities) => _abilities = abilities;

    /// <summary>The ability slot this event belongs to, by its Q/W/E/R label. The event builder names a
    /// composite after its slot letter, which is the only thing the two lists share.</summary>
    private Formats.Characters.AbilitySlot? SlotFor(Formats.Vfx.ChampionEvent ev)
    {
        if (_abilities.Count == 0 || ev.Name.Length == 0) return null;
        string slot = ev.Name[..1];
        foreach (var a in _abilities)
            if (a.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase)) return a;
        return null;
    }

    /// <summary>
    /// The spell composite: caster systems on the champion, target systems on the dummy, missiles
    /// travelling between.
    ///
    /// <para>M631: the missile's SPEED and the cast's DELAY now come from the champion's own spell
    /// records where they are authored. They used to be constants - 1,800 units/s and 0.15 s - and the
    /// speed was measurably one champion's number: exactly 1,800 occurs in 14 of 463 authored speeds,
    /// and Aatrox W is one of them, which is almost certainly how it was picked. Ezreal's Q is 2,000 at
    /// a 0.066 s cast; Ahri's R is 1,400.</para>
    ///
    /// <para>The IMPACT half still comes from the name-token heuristic, and that is not a shortcut.
    /// Measured over 692 ability slots, only 58 name a hit effect this reader can reach: the ability's
    /// impact effect is linked by the compiled spell script, which ships in no bin at all. There is no
    /// authored link to prefer.</para>
    /// </summary>
    private List<VfxPlaybackItem> BuildEventBundle(Formats.Vfx.ChampionEvent ev)
    {
        var items = new List<VfxPlaybackItem>();
        var dummy = TargetDummyPosition ?? new System.Numerics.Vector3(350, 0, 0);
        var ability = SlotFor(ev);
        // The authored cast, in seconds. castFrame is in ANIMATION frames, so the clip's own rate decides
        // what it means; 0.15 s was a constant that matched no champion in particular.
        float castDelay = ability?.CastSecondsAt(ClipFps()) ?? 0.15f;

        // M635: every spell item is AIMED. Caster-side systems and missiles point their local forward at
        // the target; a target-side system faces back at the caster - its sparks are authored to fly on
        // along +Z, which is "away from the caster" only when -Z looks back at him. VfxCastFrame holds the
        // axis convention and the evidence for it.
        VfxPlaybackItem? Make(uint hash, System.Numerics.Vector3 at, System.Numerics.Vector3 faceToward,
            System.Numerics.Vector3? travelTo, string? bone = null)
        {
            if (!_vfxDefs.TryGetValue(hash, out var def)) return null;
            float dist = travelTo is { } dst ? (dst - at).Length() : 0f;
            // M634: every stage, through the one builder - see BuildItem for what this copy was missing.
            return BuildItem(def, VfxCastFrame.Toward(at, faceToward, at)) with
            {
                TravelTo = travelTo,
                // M631: the champion's own numbers where it authored them. The fallbacks are the old
                // constants, kept for the 20 slots that author no motion at all and the ones with no
                // record to read - a guess is still better than an instant hit.
                TravelSeconds = travelTo is null ? 0f
                    : ability?.Missiles.Select(m => m.Motion.SecondsFor(dist)).FirstOrDefault(v => v is > 0f)
                      ?? (dist > 1f ? dist / 1800f : 0f),
                StartDelay = travelTo is not null ? castDelay : 0f,
                // M663: only the caster side. A missile is not bone-bound in game either - it leaves the
                // hand and flies - and a target-side system belongs to whatever it hit.
                AttachBone = bone,
            };
        }

        // M613: the caster is wherever the character is standing, not the origin. Bone-attached systems
        // ride the model matrix and always followed; these free-standing ones were spawning back at the
        // world origin, so every spell fired at the spot the character started from.
        var caster = CharacterPosition;

        // M637: an aimed cast overrides the dummy. The plan says where the caster faces, where the missile
        // flies and where - if anywhere - the hit plays: a skillshot that passed nobody has no HitAt, and
        // then the target-side systems simply do not play, which is what happens in game.
        var plan = _castPlan;
        var aim = plan?.Aim ?? dummy;
        var flightEnd = plan?.TravelTo ?? dummy;
        System.Numerics.Vector3? hitAt = plan is null ? dummy : plan.HitAt;

        // M663: the caster's own systems ride a bone. Blitzcrank's Q authors no ParticleEventData anywhere
        // in his graph - his abilities are spawned by the compiled spell script, which ships in no bin -
        // so this composite was the ONLY thing placing the effect, and it placed it in world space, where
        // it stayed while he moved. CasterBone explains why the default is a locator; the picker beside
        // the events list is there because Riot's files never name the real bone.
        string? casterBone = CasterBoneOrNull;
        foreach (var h in ev.CasterSystems) if (Make(h, caster, aim, null, casterBone) is { } i) items.Add(i);
        if (hitAt is { } hit)
            foreach (var h in ev.TargetSystems) if (Make(h, hit, caster, null) is { } i) items.Add(i);
        foreach (var h in ev.MissileSystems) if (Make(h, caster, flightEnd, flightEnd) is { } i) items.Add(i);

        // M631: the missile the spell record NAMES, when the token rule did not already find it. The
        // record says which system is the missile outright - 280 of 692 slots do - where the tokens infer
        // it from a substring of the system's name and classify only about three quarters of the corpus.
        // Added rather than substituted: a system the tokens found and the record does not name is still
        // that spell's missile, and dropping it would trade one incomplete answer for another.
        if (ability is not null)
            foreach (var missile in ability.Missiles)
            {
                if (missile.MissileEffectKey == 0) continue;
                if (!_vfxResourceMap.TryGetValue(missile.MissileEffectKey, out var systemHash)) continue;
                if (ev.MissileSystems.Contains(systemHash) || ev.CasterSystems.Contains(systemHash)) continue;
                if (Make(systemHash, caster, flightEnd, flightEnd) is { } authored) items.Add(authored);
            }
        return items;
    }

    // ---- M122: skybox (same catalogue as the map viewport, independent pick) ----

    public ObservableCollection<string> SkyboxOptions { get; } = new();
    [ObservableProperty] private int _selectedSkyboxIndex;
    [ObservableProperty] private Services.SkyboxSpec? _skybox;
    [ObservableProperty] private bool _hasSkyboxOptions;

    /// <summary>Host hook: decode the skybox behind a combo index (index 1 opens the custom picker).</summary>
    public Func<int, Task<Services.SkyboxSpec?>>? LoadSkybox;

    public void SetSkyboxOptions(IEnumerable<string> options)
    {
        SkyboxOptions.Clear();
        foreach (var o in options) SkyboxOptions.Add(o);
        HasSkyboxOptions = SkyboxOptions.Count > 1;
        SelectedSkyboxIndex = 0;
    }

    partial void OnSelectedSkyboxIndexChanged(int value)
    {
        if (LoadSkybox is { } load) _ = Apply();
        async Task Apply() { Skybox = await load(value); }
    }

    // ---- M120: image mode - textures preview in THIS window instead of a second inspector card ----

    /// <summary>When set, an image overlay covers the viewport (the single preview surface for
    /// textures). Cleared by its close button, by previewing a model, or by closing the window.</summary>
    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _imagePreview;
    [ObservableProperty] private string _imagePreviewInfo = "";
    public bool HasImagePreview => ImagePreview is not null;
    partial void OnImagePreviewChanged(Avalonia.Media.Imaging.Bitmap? value) => OnPropertyChanged(nameof(HasImagePreview));

    [RelayCommand] private void CloseImagePreview() => ImagePreview = null;

    /// <summary>Show a decoded texture in the preview window (title bar reflects it; the 3D scene
    /// underneath is left untouched so closing the image returns to the model).</summary>
    public void ShowImage(string title, Avalonia.Media.Imaging.Bitmap bmp, string info)
    {
        Title = title;
        ImagePreview = bmp;
        ImagePreviewInfo = info;
    }

    /// <summary>M120: the window was closed - stop EVERYTHING that outlives the visuals. The animation
    /// clock and the sound schedule live in this view-model, so without this the closed preview kept
    /// playing SFX/VO into the void.</summary>
    public void OnWindowClosed()
    {
        StopEvent();                          // event bundle + playback + sound schedule
        SelectedVfx = null;
        Animation.SelectedAnimation = null;   // stops the clip drive
        Animation.Pause();
        StopSounds?.Invoke();
        ImagePreview = null;
    }

    // ---- M90: preview model scale (compare champion size against the map backdrop) ----
    [ObservableProperty] private double _modelScale = 1.0;
    [RelayCommand] private void ResetModelScale() => ModelScale = 1.0;

    // ---- M90/M91: clip sound events — frame-accurate, driven by the animation clock ----
    [ObservableProperty] private bool _sfxEnabled = true;
    private readonly List<(float Time, string Name)> _soundSchedule = new();
    private int _soundsFired;

    /// <summary>Rebuild the clip's SFX schedule (event frame → seconds via the clip's fps). Called on clip
    /// change (stopping the old clip's sounds) and on loop wrap (one-shots finish across the seam).</summary>
    private void ResetSoundSchedule(bool stopCurrent)
    {
        if (stopCurrent) StopSounds?.Invoke();
        _soundSchedule.Clear();
        _soundsFired = 0;
        if (_clipsByAnm is null || Animation.SelectedAnimation?.Name is not { } anm
            || _clipsByAnm.GetValueOrDefault(anm) is not { SoundEvents.Count: > 0 } clip) return;
        float fps = ClipFps();
        foreach (var ev in clip.SoundEvents!.OrderBy(e => e.StartFrame))
            _soundSchedule.Add((MathF.Max(0f, ev.StartFrame) / fps, ev.SoundName));
    }

    /// <summary>Fire every scheduled sound whose time has come. Events skipped over by a big scrub jump
    /// are marked fired but stay silent — jumping to the end must not detonate the whole clip at once.</summary>
    private void TickSoundSchedule(double t)
    {
        while (_soundsFired < _soundSchedule.Count && _soundSchedule[_soundsFired].Time <= t + 0.001)
        {
            var (time, name) = _soundSchedule[_soundsFired++];
            if (SfxEnabled && t - time < 0.5) PlaySoundEvent?.Invoke(name);
        }
    }

    /// <summary>The clip's authored frame rate (event StartFrames are in these frames); 30 fps fallback.</summary>
    private float ClipFps() => CurrentAnimation?.Fps is > 1f and < 240f ? CurrentAnimation!.Fps : 30f;

    partial void OnSfxEnabledChanged(bool value) { if (!value) StopSounds?.Invoke(); }

    // ---- M95c: voice lines — the skin bin's authored Play_vo_ events (clips never carry them) ----
    public ObservableCollection<string> VoiceEvents { get; } = new();
    [ObservableProperty] private string? _selectedVoiceEvent;
    [ObservableProperty] private bool _hasVoiceEvents;

    public void SetVoiceEvents(IReadOnlyList<string> events)
    {
        VoiceEvents.Clear();
        foreach (var e in events) VoiceEvents.Add(e);
        HasVoiceEvents = VoiceEvents.Count > 0;
        SelectedVoiceEvent = null;
    }

    partial void OnSelectedVoiceEventChanged(string? value)
    { if (value is { Length: > 0 }) PlaySoundEvent?.Invoke(value); }

    [RelayCommand] private void ReplayVoiceEvent()
    { if (SelectedVoiceEvent is { Length: > 0 } ev) PlaySoundEvent?.Invoke(ev); }

    /// <summary>Emote clips (joke/taunt/laugh/dance/death) have no VO in their event data — the game
    /// fires the matching bank event from logic. Mirror that: fire the first VO line matching the
    /// clip's keyword so emotes speak like in-game.</summary>
    private void TryAutoVoice()
    {
        if (!SfxEnabled || VoiceEvents.Count == 0 || Animation.SelectedAnimation?.Name is not { } anm) return;
        foreach (var word in new[] { "joke", "taunt", "laugh", "dance", "death" })
        {
            if (!anm.Contains(word, StringComparison.OrdinalIgnoreCase)) continue;
            if (VoiceEvents.FirstOrDefault(v => v.Contains(word, StringComparison.OrdinalIgnoreCase)) is { } ev)
                PlaySoundEvent?.Invoke(ev);
            return;
        }
    }

    /// <summary>Bins store the bone as an unresolvable hash — match it against the skeleton's joints
    /// (FNV1a of the lowercased name, or the joint's Elf AnimHash) to get a real bone name.</summary>
    private string? ResolveBoneName(Formats.Skeletons.AnimParticleEvent ev)
    {
        if (ev.BoneName.Length > 0) return ev.BoneName;
        if (ev.BoneHash == 0 || Skeleton is null) return null;
        foreach (var j in Skeleton.Joints)
            if (j.AnimHash == ev.BoneHash
                || ReyEngine.Core.Hashing.HashAlgorithms.Fnv1a(j.Name) == ev.BoneHash)
                return j.Name;
        return null;
    }

    private void ApplyAutoVisibility()
    {
        if (!AutoSubmeshVisibility || Submeshes.Count == 0) return;
        var clip = Animation.SelectedAnimation?.Name is { } anm && _clipsByAnm is not null
            ? _clipsByAnm.GetValueOrDefault(anm) : null;
        foreach (var s in Submeshes)
        {
            bool visible = !_initialHide.Any(h => string.Equals(h, s.Name, StringComparison.OrdinalIgnoreCase));
            if (clip is not null)
            {
                if (Formats.Skeletons.ChampionAnimationData.Matches(s.Name, clip.HideNames, clip.HideHashes)) visible = false;
                if (Formats.Skeletons.ChampionAnimationData.Matches(s.Name, clip.ShowNames, clip.ShowHashes)) visible = true;
            }
            s.IsVisible = visible;
        }
    }

    /// <summary>Populate the animation list (same entries the main window's FindAnimations produces).</summary>
    public void SetAnimations(IEnumerable<AnimationEntryViewModel> animations) => Animation.SetAnimations(animations);

    /// <summary>Populate the champion VFX library for this skin (visual systems only). The resource map
    /// (M86) translates clip effect keys → system object hashes, the way the game resolves them.</summary>
    /// <summary>M116: stop pending clip SFX without touching a playing event (pure-animation mode).</summary>
    private void ClearSoundSchedule()
    {
        StopSounds?.Invoke();
        _soundSchedule.Clear();
        _soundsFired = 0;
    }

    /// <summary>M116: rebuild the EVENTS list (spell composites + authored clips) for this skin.</summary>
    private void RebuildChampionEvents()
    {
        ChampionEvents.Clear();
        _activeEvent = null; _eventBundle = null; _eventPlaybackActive = false;
        if (_vfxDefs.Count > 0 || _allClips.Count > 0)
            foreach (var ev in ChampionEventBuilder.Build(_vfxDefs, _allClips))
                ChampionEvents.Add(ev);
        HasEvents = ChampionEvents.Count > 0;
    }

    /// <summary>M180 (2.7): resolve one emitter's child set into playable items.
    ///
    /// A child is named either by <c>effectKey</c> - a key in the same namespace as
    /// <c>ResourceResolver.resourceMap</c>, hence the indirection - or by <c>effect</c>, a direct object
    /// link. Both are tried; unresolved children are simply dropped, which is the common case for the
    /// ~30% of keys that live only in bins this skin does not pull in.
    ///
    /// <paramref name="depth"/> exists because NOTHING in the data rules out a child chain reaching back
    /// to its own parent. An early probe appeared to show no cycles, but it compared child keys against
    /// system object-hashes - different keyspaces - so it established nothing. The cap is what guarantees
    /// termination, not the data.</summary>
    private IReadOnlyList<IReadOnlyList<VfxPlaybackItem>?>? ResolveChildren(VfxSystemDefinition sys, int depth)
    {
        if (depth >= MaxChildDepth) return null;
        List<IReadOnlyList<VfxPlaybackItem>?>? perEmitter = null;
        for (int i = 0; i < sys.Emitters.Count; i++)
        {
            var set = sys.Emitters[i].Children;
            List<VfxPlaybackItem>? items = null;
            if (set is not null)
                foreach (var id in set.Children)
                {
                    VfxSystemDefinition? child = null;
                    if (id.EffectKey != 0 && _vfxResourceMap.TryGetValue(id.EffectKey, out var mapped))
                        _vfxDefs.TryGetValue(mapped, out child);
                    if (child is null && id.EffectLink != 0) _vfxDefs.TryGetValue(id.EffectLink, out child);
                    if (child is null && id.EffectKey != 0) _vfxDefs.TryGetValue(id.EffectKey, out child);
                    if (child is null) continue;
                    (items ??= new()).Add(BuildItem(child, System.Numerics.Vector3.Zero, depth + 1));
                }
            if (items is not null) (perEmitter ??= NullList(sys.Emitters.Count))[i] = items;
        }
        return perEmitter;
    }

    private static List<IReadOnlyList<VfxPlaybackItem>?> NullList(int n)
    {
        var l = new List<IReadOnlyList<VfxPlaybackItem>?>(n);
        for (int i = 0; i < n; i++) l.Add(null);
        return l;
    }

    /// <summary>Children of children are resolved one level deep and no further. Deeper chains are rare,
    /// and the cost of getting recursion wrong here is an editor that hangs on preview.</summary>
    private const int MaxChildDepth = 2;

    /// <summary>M180: build a playback item with all its texture stages and its resolved children.
    ///
    /// <para>M634: THE ONLY place in this class that constructs a <see cref="VfxPlaybackItem"/>. There were
    /// four - the clip events, the spell composite, the manual pick and this one - each listing the stages
    /// by hand, and no two lists agreed: none of the four passed the multiplier texture, and the events and
    /// the spells also skipped child systems and reflection cubemaps. A stage forgotten in one copy is not
    /// an error anyone sees; it is a white stand-in the shader multiplies by, which reads as "the mask is
    /// not applied". The three callers now take this item and set only what is theirs - bone, delay,
    /// travel - with a <c>with</c> expression, so a stage added here reaches every path at once.</para></summary>
    private VfxPlaybackItem BuildItem(VfxSystemDefinition def, System.Numerics.Vector3 at, int depth = 0)
        => BuildItem(def, System.Numerics.Matrix4x4.CreateTranslation(at), depth);

    /// <summary>M635: the placement as a whole frame rather than a point, so a cast can be AIMED. Every
    /// spell item used to take a bare translation, which is the identity rotation - so Aatrox's W slammed
    /// the ground along the same world axis whatever he was aiming at. See <see cref="VfxCastFrame"/> for
    /// which local axis the data points at the target, and the evidence.</summary>
    private VfxPlaybackItem BuildItem(VfxSystemDefinition def, System.Numerics.Matrix4x4 placement, int depth = 0)
        => new(def, placement,
            ResolveTextures?.Invoke(def) ?? new TextureImage?[def.Emitters.Count],
            ResolveMeshes?.Invoke(def),
            EmitterMultTextures: ResolveMultTextures?.Invoke(def),
            EmitterDistortionTextures: ResolveDistortionTextures?.Invoke(def),
            EmitterColorTextures: ResolveColorTextures?.Invoke(def),
            EmitterErosionTextures: ResolveErosionTextures?.Invoke(def),
            EmitterPaletteTextures: ResolvePaletteTextures?.Invoke(def),
            EmitterChildren: ResolveChildren(def, depth),
            EmitterReflectionCubemaps: ResolveReflectionCubemaps?.Invoke(def));

    public void SetVfx(IReadOnlyDictionary<uint, VfxSystemDefinition> systems,
        IReadOnlyDictionary<uint, uint>? resourceMap = null)
    {
        _vfxDefs = systems;
        _vfxResourceMap = resourceMap ?? new Dictionary<uint, uint>();
        VfxSystems.Clear();
        foreach (var s in System.Linq.Enumerable.OrderBy(systems.Values, x => x.Name, StringComparer.OrdinalIgnoreCase))
            if (System.Linq.Enumerable.Any(s.Emitters, e => e.IsVisual))
                VfxSystems.Add(new VfxSystemItemViewModel { Hash = s.PathHash, Name = s.Name, EmitterCount = s.Emitters.Count });
        HasVfx = VfxSystems.Count > 0;
        RebuildChampionEvents();   // M116: events derive from the VFX set + this skin's clips
    }

    partial void OnSelectedVfxChanged(VfxSystemItemViewModel? value)
    {
        if (value is null || !_vfxDefs.TryGetValue(value.Hash, out var def)) { if (_eventBundle is null) Playback = null; return; }
        _eventPlaybackActive = false; _eventBundle = null; _activeEvent = null;   // manual pick replaces an event
        // M116: _tar systems play ONLY on the target dummy — picking one turns the dummy on.
        if (IsTargetVfxName(def.Name) && !TargetDummyEnabled) { TargetDummyEnabled = true; return; }   // re-enters via ReplaySelectedOrClip
        // M114: "_tar" systems (or the explicit override) play at the target dummy — Kayn's
        // R_tar_enemy lands on the dummy instead of stacking on the caster.
        // M634: every stage, through the one builder.
        // M635: and aimed - at the dummy it faces the caster, at the caster it faces the dummy.
        var anchor = AnchorFor(def, PlaySelectedAtDummy);
        var target = TargetDummyPosition ?? new System.Numerics.Vector3(350, 0, 0);
        var placement = anchor != System.Numerics.Vector3.Zero
            ? VfxCastFrame.Toward(anchor, CharacterPosition, anchor)
            : VfxCastFrame.Toward(anchor, target, anchor);
        Playback = new VfxPlayback(new[] { BuildItem(def, placement) });
    }

    [RelayCommand] private void StopVfx() => SelectedVfx = null;
}
