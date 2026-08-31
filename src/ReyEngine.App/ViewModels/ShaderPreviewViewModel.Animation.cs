using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using ReyEngine.Formats.Animation;
using ReyEngine.Formats.Skeletons;

namespace ReyEngine.App.ViewModels;

public sealed class PreviewAnimationRow
{
    public required string Path { get; init; }
    public required ulong Hash { get; init; }
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
}

/// <summary>
/// M615: animating a character in the D3D11 preview.
///
/// <para>Until now this renderer drew every skinned mesh in its bind pose, because <c>BonesCB</c> was
/// filled with one constant matrix repeated across every slot. The palette exists now, so the missing
/// half is someone to compute it per frame — this.</para>
///
/// <para>The skeleton is found by convention rather than by lookup: a champion's <c>.skl</c> sits beside
/// its <c>.skn</c> under the same name, which is true of every skin Riot ships. Clips are offered from
/// whatever <c>.anm</c> assets the host handed over, narrowed to the loaded character so a champion does
/// not offer you another champion's walk.</para>
/// </summary>
public sealed partial class ShaderPreviewViewModel
{
    private readonly List<(string Path, ulong Hash)> _allAnimations = new();
    private SkeletonAsset? _skeleton;
    private AnimationClip? _clip;
    private float _clipTime;

    public ObservableCollection<PreviewAnimationRow> Animations { get; } = new();

    [ObservableProperty] private PreviewAnimationRow? _selectedAnimation;
    [ObservableProperty] private bool _animationPlaying = true;
    [ObservableProperty] private double _animationSpeed = 1.0;
    [ObservableProperty] private string _animationStatus = "";

    public bool HasSkeleton => _skeleton is not null;
    public bool HasAnimations => Animations.Count > 0;

    /// <summary>Called when a scene asset finishes loading. Clears everything for a map — a map has no
    /// skeleton, and offering one clips would be offering something that cannot be applied.</summary>
    private void LoadSkeletonFor(SceneAssetRow asset)
    {
        _skeleton = null;
        _clip = null;
        _clipTime = 0f;
        SelectedAnimation = null;
        Animations.Clear();
        _settings.BonePalette = null;

        if (asset.IsMap || _readAsset is null)
        {
            AnimationStatus = "";
            OnPropertyChanged(nameof(HasSkeleton));
            OnPropertyChanged(nameof(HasAnimations));
            return;
        }

        // The .skl sits beside the .skn under the same name. True of every skin Riot ships, and cheaper
        // and more reliable than searching for one.
        string sklPath = asset.Path[..^4] + ".skl";
        try
        {
            if (_readAsset(Core.Hashing.HashAlgorithms.WadPath(sklPath)) is { } bytes)
                _skeleton = SkeletonDecoder.Decode(bytes);
        }
        catch
        {
            _skeleton = null;
        }

        if (_skeleton is null)
        {
            AnimationStatus = $"No skeleton beside {asset.Display} - bind pose only.";
        }
        else
        {
            foreach (var row in AnimationsFor(asset.Path))
                Animations.Add(row);
            AnimationStatus = Animations.Count > 0
                ? $"{_skeleton.Joints.Count} joints, {Animations.Count} clip(s)."
                : $"{_skeleton.Joints.Count} joints, no clips shipped beside this skin.";
        }

        OnPropertyChanged(nameof(HasSkeleton));
        OnPropertyChanged(nameof(HasAnimations));
        ApplyBonePalette();
    }

    /// <summary>The clips that belong to this character. Narrowed to the champion folder, because a WAD
    /// holds several characters and a champion offering its pet's animations is noise at best and a
    /// skeleton mismatch at worst.</summary>
    private IEnumerable<PreviewAnimationRow> AnimationsFor(string sknPath)
    {
        var parts = sknPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        int ci = Array.FindIndex(parts, p => p.Equals("characters", StringComparison.OrdinalIgnoreCase));
        string marker = ci >= 0 && ci + 1 < parts.Length ? $"/characters/{parts[ci + 1]}/" : "";

        return _allAnimations
            .Where(a => marker.Length == 0 || a.Path.Replace('\\', '/').Contains(marker, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Path, StringComparer.OrdinalIgnoreCase)
            .Select(a => new PreviewAnimationRow { Path = a.Path, Hash = a.Hash });
    }

    partial void OnSelectedAnimationChanged(PreviewAnimationRow? value)
    {
        _clip = null;
        _clipTime = 0f;

        if (value is not null && _readAsset is not null)
        {
            try
            {
                if (_readAsset(value.Hash) is { } bytes)
                    _clip = AnimationDecoder.Decode(bytes, value.Name);
            }
            catch (Exception ex)
            {
                AnimationStatus = $"{value.Name}: {ex.Message}";
            }
        }

        if (_clip is not null) AnimationStatus = $"{_clip.Name} - {_clip.Duration:0.00}s";
        ApplyBonePalette();
    }

    /// <summary>Advance the clip and rebuild the palette. Called once per rendered frame.</summary>
    private void AdvanceAnimation(float dt)
    {
        if (_skeleton is null) return;

        if (_clip is not null && AnimationPlaying && _clip.Duration > 1e-3f)
        {
            _clipTime += dt * (float)Math.Max(0.0, AnimationSpeed);
            _clipTime %= _clip.Duration;
        }
        ApplyBonePalette();
    }

    /// <summary>Null palette means the renderer keeps its bind-pose constant, which is exactly right for
    /// a map or for a character with no skeleton.</summary>
    private void ApplyBonePalette() =>
        _settings.BonePalette = _skeleton is null ? null : BonePalette.Build(_skeleton, _clip, _clipTime);
}
