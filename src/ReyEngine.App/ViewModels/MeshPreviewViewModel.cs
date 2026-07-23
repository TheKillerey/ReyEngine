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
    [ObservableProperty] private bool _isVisible = true;
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
    [ObservableProperty] private bool _showBones;
    [ObservableProperty] private bool _wireframe;
    [ObservableProperty] private bool _cullBackfaces = true;

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
    public Func<VfxSystemDefinition, IReadOnlyList<TextureImage?>>? ResolveDistortionTextures;
    public Func<VfxSystemDefinition, IReadOnlyList<TextureImage?>>? ResolveColorTextures;   // M68
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
        ShowBones = skeleton is not null;
        Stats = $"{mesh.VertexCount:n0} verts · {mesh.TriangleCount:n0} tris · {mesh.SubMeshes.Count} submesh(es)" +
                (skeleton is not null ? $" · {skeleton.BoneCount} bones" : "");
        CurrentAnimation = null;
        AnimationTime = 0;
        Animation.SetSkeleton(skeleton?.BoneCount ?? 0);
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
            var t = new SubmeshToggleViewModel { Name = string.IsNullOrEmpty(s.Material) ? $"submesh {Submeshes.Count}" : s.Material };
            t.Changed = RebuildSubmeshVisibility;
            Submeshes.Add(t);
        }
        HasSubmeshes = Submeshes.Count > 1;
        RebuildSubmeshVisibility();
    }

    // ---- M88: NVR map backdrop (character stands in-map, lit by the map's Light.dat) ----
    [ObservableProperty] private MeshAsset? _backgroundMesh;
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _backgroundTextures;
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _backgroundBlendTextures;   // M89 four-blend
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _backgroundColor1Textures;
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _backgroundColor2Textures;
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _backgroundColor3Textures;
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

    /// <summary>Provide the skin's initial-hide list + animation-graph clips (keyed by .anm file name).</summary>
    public void SetSubmeshRules(IReadOnlyList<string> initialHide,
        IReadOnlyDictionary<string, Formats.Skeletons.AnimClipInfo>? clipsByAnm)
    {
        _initialHide = initialHide;
        _clipsByAnm = clipsByAnm;
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
            var texs = ResolveTextures?.Invoke(def) ?? new TextureImage?[def.Emitters.Count];
            // M114: target-bound systems ("_tar") anchor at the dummy, not on the caster's bones.
            var anchor = AnchorFor(def);
            bool atDummy = anchor != System.Numerics.Vector3.Zero;
            items.Add(new VfxPlaybackItem(def, anchor, texs,
                ResolveMeshes?.Invoke(def),
                emitterDistortionTextures: ResolveDistortionTextures?.Invoke(def),
                emitterColorTextures: ResolveColorTextures?.Invoke(def))
            {
                AttachBone = atDummy ? null : ResolveBoneName(ev),
                StartDelay = MathF.Max(0f, ev.StartFrame) / ClipFps(),   // M91: fire at the authored frame
            });
        }
        if (_eventBundle is { Count: > 0 }) items.AddRange(_eventBundle);   // M116: spell composite rides along
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
        SelectedVfx = null;                       // manual pick and events are exclusive
        _activeEvent = ev;
        _eventPlaybackActive = true;
        if (ev.NeedsTarget && !TargetDummyEnabled) TargetDummyEnabled = true;   // _tar plays ONLY on the dummy
        _eventBundle = BuildEventBundle(ev);

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

    /// <summary>The spell composite: caster systems on the champion, target systems on the dummy,
    /// missiles travelling between at ~1800 u/s (League's common missile speed band).</summary>
    private List<VfxPlaybackItem> BuildEventBundle(Formats.Vfx.ChampionEvent ev)
    {
        var items = new List<VfxPlaybackItem>();
        var dummy = TargetDummyPosition ?? new System.Numerics.Vector3(350, 0, 0);

        VfxPlaybackItem? Make(uint hash, System.Numerics.Vector3 at, System.Numerics.Vector3? travelTo)
        {
            if (!_vfxDefs.TryGetValue(hash, out var def)) return null;
            var texs = ResolveTextures?.Invoke(def) ?? new TextureImage?[def.Emitters.Count];
            float dist = travelTo is { } dst ? (dst - at).Length() : 0f;
            return new VfxPlaybackItem(def, at, texs,
                ResolveMeshes?.Invoke(def),
                emitterDistortionTextures: ResolveDistortionTextures?.Invoke(def),
                emitterColorTextures: ResolveColorTextures?.Invoke(def))
            {
                TravelTo = travelTo,
                TravelSeconds = dist > 1f ? dist / 1800f : 0f,
                StartDelay = travelTo is not null ? 0.15f : 0f,   // small windup before the missile leaves
            };
        }

        foreach (var h in ev.CasterSystems) if (Make(h, System.Numerics.Vector3.Zero, null) is { } i) items.Add(i);
        foreach (var h in ev.TargetSystems) if (Make(h, dummy, null) is { } i) items.Add(i);
        foreach (var h in ev.MissileSystems) if (Make(h, System.Numerics.Vector3.Zero, dummy) is { } i) items.Add(i);
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
        if (_vfxDefs.Count > 0 || _clipsByAnm is { Count: > 0 })
            foreach (var ev in ChampionEventBuilder.Build(_vfxDefs,
                         (_clipsByAnm?.Values ?? Enumerable.Empty<Formats.Skeletons.AnimClipInfo>()).ToList()))
                ChampionEvents.Add(ev);
        HasEvents = ChampionEvents.Count > 0;
    }

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
        var texs = ResolveTextures?.Invoke(def) ?? new TextureImage?[def.Emitters.Count];
        // M114: "_tar" systems (or the explicit override) play at the target dummy — Kayn's
        // R_tar_enemy lands on the dummy instead of stacking on the caster.
        Playback = new VfxPlayback(new[] { new VfxPlaybackItem(def, AnchorFor(def, PlaySelectedAtDummy), texs,
            ResolveMeshes?.Invoke(def), emitterDistortionTextures: ResolveDistortionTextures?.Invoke(def),
            emitterColorTextures: ResolveColorTextures?.Invoke(def)) });
    }

    [RelayCommand] private void StopVfx() => SelectedVfx = null;
}
