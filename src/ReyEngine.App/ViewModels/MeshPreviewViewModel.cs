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

    // wired once by MainWindowViewModel
    public Func<VfxSystemDefinition, IReadOnlyList<TextureImage?>>? ResolveTextures;
    public Func<VfxSystemDefinition, IReadOnlyList<TextureImage?>>? ResolveDistortionTextures;
    public Func<VfxSystemDefinition, IReadOnlyList<TextureImage?>>? ResolveColorTextures;   // M68
    public Func<VfxSystemDefinition, IReadOnlyList<StaticMeshData?>?>? ResolveMeshes;

    public MeshPreviewViewModel()
    {
        Animation.ClipChanged = clip => { CurrentAnimation = clip; ApplyAutoVisibility(); };   // M85
        Animation.TimeChanged = t => AnimationTime = t;
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

    /// <summary>Populate the champion VFX library for this skin (visual systems only).</summary>
    public void SetVfx(IReadOnlyDictionary<uint, VfxSystemDefinition> systems)
    {
        _vfxDefs = systems;
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
