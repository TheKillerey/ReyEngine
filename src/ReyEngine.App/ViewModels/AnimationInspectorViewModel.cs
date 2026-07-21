using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core.Assets;
using ReyEngine.Formats.Animation;

namespace ReyEngine.App.ViewModels;

public sealed class AnimationEntryViewModel
{
    public WadAssetEntry Entry { get; }
    public AnimationEntryViewModel(WadAssetEntry entry) => Entry = entry;
    public string Name => Entry.DisplayName;

    // ---- M115: skin grouping ----
    /// <summary>Which skin folder this .anm lives in — "Base", "Skin 02", … or "Shared" for
    /// champion-level animation folders outside skins/.</summary>
    public string SkinGroup { get; init; } = "Shared";
    /// <summary>True when the loaded skin's own animation graph references this clip (green highlight).</summary>
    public bool IsCurrentSkin { get; init; }

    /// <summary>Derive the group label from the .anm path (…/skins/skin02/animations/…).</summary>
    public static string GroupFromPath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        int si = System.Array.FindIndex(parts, p => p.Equals("skins", StringComparison.OrdinalIgnoreCase));
        if (si < 0 || si + 1 >= parts.Length) return "Shared";
        var skin = parts[si + 1];
        if (skin.Equals("base", StringComparison.OrdinalIgnoreCase)) return "Base";
        return skin.StartsWith("skin", StringComparison.OrdinalIgnoreCase) && int.TryParse(skin.AsSpan(4), out int n)
            ? $"Skin {n:00}"
            : skin;
    }
}

/// <summary>
/// Animation panel + playback clock. Owns a ~60 fps timer that advances <see cref="Time"/>;
/// the host wires callbacks to decode clips and forward clip/time to the viewport.
/// </summary>
public sealed partial class AnimationInspectorViewModel : ViewModelBase
{
    private readonly DispatcherTimer _timer;
    private DateTime _lastTick;
    private AnimationClip? _clip;
    private bool _suppress;

    public Func<WadAssetEntry, AnimationClip?>? ClipLoader { get; set; }
    public Action<AnimationClip?>? ClipChanged { get; set; }
    public Action<double>? TimeChanged { get; set; }

    public ObservableCollection<AnimationEntryViewModel> Animations { get; } = new();

    [ObservableProperty] private bool _hasSkeleton;
    [ObservableProperty] private string _skeletonStatus = "No skeleton";
    [ObservableProperty] private AnimationEntryViewModel? _selectedAnimation;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _loop = true;
    [ObservableProperty] private double _speed = 1.0;
    [ObservableProperty] private double _time;
    [ObservableProperty] private double _duration;
    [ObservableProperty] private string _timeText = "—";
    [ObservableProperty] private string _currentName = "(none)";

    public bool HasAnimations => Animations.Count > 0;
    public bool HasClip => _clip is not null;

    public AnimationInspectorViewModel()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTick;
    }

    public void SetSkeleton(int boneCount)
    {
        HasSkeleton = boneCount > 0;
        SkeletonStatus = boneCount > 0 ? $"Skeleton: {boneCount} bones" : "No skeleton — cannot animate";
    }

    public void SetAnimations(IEnumerable<AnimationEntryViewModel> anims)
    {
        ClearClip();
        Animations.Clear();
        foreach (var a in anims) Animations.Add(a);
        OnPropertyChanged(nameof(HasAnimations));
    }

    public void Clear()
    {
        ClearClip();
        Animations.Clear();
        OnPropertyChanged(nameof(HasAnimations));
        HasSkeleton = false;
        SkeletonStatus = "No skeleton";
    }

    /// <summary>Use a clip from outside the list (e.g. manual Assign Animation…).</summary>
    public void SetExternalClip(AnimationClip clip)
    {
        _suppress = true; SelectedAnimation = null; _suppress = false;
        ApplyClip(clip);
    }

    private void ClearClip()
    {
        Pause();
        _suppress = true; SelectedAnimation = null; _suppress = false;
        _clip = null;
        Duration = 0; Time = 0; CurrentName = "(none)"; TimeText = "—";
        OnPropertyChanged(nameof(HasClip));
        ClipChanged?.Invoke(null);
    }

    private void ApplyClip(AnimationClip clip)
    {
        _clip = clip;
        CurrentName = clip.Name;
        Duration = clip.Duration;
        _suppress = true; Time = 0; _suppress = false;
        OnPropertyChanged(nameof(HasClip));
        ClipChanged?.Invoke(clip);
        TimeChanged?.Invoke(0);
        UpdateTimeText();
        Play();
    }

    partial void OnSelectedAnimationChanged(AnimationEntryViewModel? value)
    {
        if (_suppress || value is null || ClipLoader is null) return;
        var clip = ClipLoader(value.Entry);
        if (clip is null) { CurrentName = "(decode failed)"; return; }
        ApplyClip(clip);
    }

    partial void OnTimeChanged(double value)
    {
        if (!_suppress) TimeChanged?.Invoke(value);
        UpdateTimeText();
    }

    [RelayCommand]
    private void TogglePlay()
    {
        if (IsPlaying) Pause(); else Play();
    }

    [RelayCommand]
    private void StepForward() => Seek(Time + (_clip is { Fps: > 0 } c ? 1.0 / c.Fps : 1.0 / 30));

    [RelayCommand]
    private void StepBack() => Seek(Time - (_clip is { Fps: > 0 } c ? 1.0 / c.Fps : 1.0 / 30));

    public void Play()
    {
        if (_clip is null) return;
        _lastTick = DateTime.UtcNow;
        IsPlaying = true;
        _timer.Start();
    }

    public void Pause()
    {
        IsPlaying = false;
        _timer.Stop();
    }

    private void Seek(double t)
    {
        if (Duration <= 0) return;
        Time = Math.Clamp(t, 0, Duration);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        double dt = (now - _lastTick).TotalSeconds;
        _lastTick = now;
        if (Duration <= 0) { Pause(); return; }

        double t = Time + dt * Speed;
        if (t >= Duration)
        {
            if (Loop) t %= Duration;
            else { t = Duration; Pause(); }
        }
        Time = t;
    }

    private void UpdateTimeText()
    {
        float fps = _clip?.Fps is > 0 ? _clip.Fps : 30f;
        TimeText = $"{Time:0.00} / {Duration:0.00}s   (frame {(int)(Time * fps)}/{(int)(Duration * fps)})";
    }
}
