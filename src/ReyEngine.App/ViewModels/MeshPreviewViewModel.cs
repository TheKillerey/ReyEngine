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
    public Func<VfxSystemDefinition, IReadOnlyList<TextureImage?>>? ResolveTextures;
    public Func<VfxSystemDefinition, IReadOnlyList<TextureImage?>>? ResolveDistortionTextures;
    public Func<VfxSystemDefinition, IReadOnlyList<TextureImage?>>? ResolveColorTextures;   // M68
    public Func<VfxSystemDefinition, IReadOnlyList<StaticMeshData?>?>? ResolveMeshes;

    private double _lastAnimTime;

    public MeshPreviewViewModel()
    {
        Animation.ClipChanged = clip => { CurrentAnimation = clip; _lastAnimTime = 0; ApplyAutoVisibility(); ApplyClipParticles(); };   // M85/M86
        Animation.TimeChanged = t =>
        {
            // M86: clip event VFX are one-shot — respawn them each time the looping clip wraps around
            // (manual VFX picks are left alone). Backward jump in time = the loop restarted.
            if (t + 0.05 < _lastAnimTime && SelectedVfx is null) ApplyClipParticles();
            _lastAnimTime = t;
            AnimationTime = t;
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

    /// <summary>M86: the playing clip's ParticleEventData → play those VFX bone-attached, like in-game.
    /// (StartFrame timing is not simulated yet — every event effect plays for the whole clip.)</summary>
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
            { AttachBone = ResolveBoneName(ev) });
        }
        if (items.Count > 0) Playback = new VfxPlayback(items);
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
