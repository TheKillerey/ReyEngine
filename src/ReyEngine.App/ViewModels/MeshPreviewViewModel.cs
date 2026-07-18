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
        Animation.ClipChanged = clip => { CurrentAnimation = clip; _lastAnimTime = 0; ApplyAutoVisibility(); ApplyClipParticles(); ResetSoundSchedule(stopCurrent: true); };   // M85/M86/M90
        Animation.TimeChanged = t =>
        {
            // M86: clip event VFX are one-shot — respawn them each time the looping clip wraps around
            // (manual VFX picks are left alone). Backward jump in time = the loop restarted.
            if (t + 0.05 < _lastAnimTime)
            {
                if (SelectedVfx is null) ApplyClipParticles();
                ResetSoundSchedule(stopCurrent: false);   // M91: refire from the top; one-shots finish across the seam
            }
            _lastAnimTime = t;
            AnimationTime = t;
            TickSoundSchedule(t);   // M91: frame-accurate SFX
        };
    }

    public void Show(string title, MeshAsset mesh, SkeletonAsset? skeleton, IReadOnlyList<TextureImage?>? textures)
    {
        Title = title;
        Mesh = mesh;
        Skeleton = skeleton;
        Textures = textures;
        ShowBones = skeleton is not null;
        Stats = $"{mesh.VertexCount:n0} verts · {mesh.TriangleCount:n0} tris · {mesh.SubMeshes.Count} submesh(es)" +
                (skeleton is not null ? $" · {skeleton.BoneCount} bones" : "");
        CurrentAnimation = null;
        AnimationTime = 0;
        Animation.SetSkeleton(skeleton?.BoneCount ?? 0);
        Playback = null;
        SelectedVfx = null;
        StopSounds?.Invoke();   // M90: previous champion's SFX must not bleed into the new preview

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
        if (_clipsByAnm is null || Animation.SelectedAnimation?.Name is not { } anm) return;
        if (_clipsByAnm.GetValueOrDefault(anm) is not { ParticleEvents.Count: > 0 } clip)
        { if (SelectedVfx is null) Playback = null; return; }   // no events: stop auto VFX, keep manual picks

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
            items.Add(new VfxPlaybackItem(def, System.Numerics.Vector3.Zero, texs,
                ResolveMeshes?.Invoke(def),
                emitterDistortionTextures: ResolveDistortionTextures?.Invoke(def),
                emitterColorTextures: ResolveColorTextures?.Invoke(def))
            {
                AttachBone = ResolveBoneName(ev),
                StartDelay = MathF.Max(0f, ev.StartFrame) / ClipFps(),   // M91: fire at the authored frame
            });
        }
        if (items.Count > 0) Playback = new VfxPlayback(items);
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
    }

    partial void OnSelectedVfxChanged(VfxSystemItemViewModel? value)
    {
        if (value is null || !_vfxDefs.TryGetValue(value.Hash, out var def)) { Playback = null; return; }
        var texs = ResolveTextures?.Invoke(def) ?? new TextureImage?[def.Emitters.Count];
        Playback = new VfxPlayback(new[] { new VfxPlaybackItem(def, System.Numerics.Vector3.Zero, texs,
            ResolveMeshes?.Invoke(def), emitterDistortionTextures: ResolveDistortionTextures?.Invoke(def),
            emitterColorTextures: ResolveColorTextures?.Invoke(def)) });
    }

    [RelayCommand] private void StopVfx() => SelectedVfx = null;
}
