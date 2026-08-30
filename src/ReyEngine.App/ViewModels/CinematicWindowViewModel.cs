using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core.Cinematics;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// What the cinematic panel needs from the editor. An interface rather than four loose delegates: the
/// four are only ever supplied together, by the one object that owns both the camera and the D3D11
/// surface, and a half-wired panel would fail at capture time rather than at construction.
/// </summary>
public interface ICinematicHost
{
    /// <summary>The editor camera right now, as a keyframe would record it.</summary>
    CinematicPose CurrentPose { get; }

    /// <summary>Show this pose in the viewport, or null to hand the camera back to the user. This is what
    /// makes the scrubber a preview rather than a number.</summary>
    void PreviewPose(CinematicPose? pose);

    /// <summary>Take the viewport out of live mode for a capture; disposing restores it.</summary>
    IDisposable BeginCapture();

    /// <summary>One frame, BGRA top-down. Null when the renderer produced nothing.</summary>
    byte[]? RenderFrame(CinematicPose pose, int width, int height, float timeSeconds);
}

/// <summary>One keyframe, as a row the panel can show and edit.</summary>
public sealed partial class CinematicKeyRowViewModel : ObservableObject
{
    public CinematicKeyRowViewModel(CinematicKeyframe key) => Key = key;

    public CinematicKeyframe Key { get; set; }

    public string TimeText => Key.Time.ToString("0.00", CultureInfo.InvariantCulture) + " s";
    public string PositionText =>
        $"{Key.Position.X:0}, {Key.Position.Y:0}, {Key.Position.Z:0}";
    public string FovText => (Key.FieldOfView * 180f / MathF.PI).ToString("0", CultureInfo.InvariantCulture) + "°";
    public string EaseText => Key.Ease.ToString();
}

/// <summary>
/// M607: the Cinematic Capture panel.
///
/// <para>Scope is the spec's version 1 and no more: a shot list, keyframes taken from the camera you are
/// already looking through, a scrubber that previews the move, and a deterministic export. Depth of
/// field, fog presets, camera shake and FFmpeg are deliberately absent — they were listed as later
/// extensions, and each is easier to add against a capture that already works than against one that does
/// not.</para>
/// </summary>
public sealed partial class CinematicWindowViewModel : ObservableObject
{
    private readonly ICinematicHost _host;
    private readonly string _documentPath;
    private CinematicDocument _document;
    private CancellationTokenSource? _capture;

    public CinematicWindowViewModel(ICinematicHost host, string projectRoot, Action<string, string>? log = null)
    {
        _host = host;
        _log = log;
        _documentPath = CinematicDocument.PathFor(projectRoot);

        try { _document = CinematicDocument.Load(_documentPath); }
        catch (Exception ex)
        {
            // A document that cannot be read must not take the panel down with it, and must not be
            // silently replaced by an empty one either - that would look like the shots were lost, and
            // the next save would make it true.
            _document = new CinematicDocument();
            _loadFailed = true;
            // A PERSISTENT banner, not a status line. Status is overwritten by the next thing the user
            // does, so a warning there would vanish the moment they added a keyframe - and they would go
            // on building a shot that is never being saved, which is the same as losing it twice.
            SaveBlocked = $"Could not read {Path.GetFileName(_documentPath)}: {ex.Message} "
                        + "Nothing here will be saved, and that file will not be written over - move it aside to start fresh.";
        }

        foreach (var shot in _document.Shots) Shots.Add(shot);
        SelectedShot = Shots.FirstOrDefault();

        if (_document.LastCapture is { } last)
        {
            Width = last.Width; Height = last.Height; Fps = last.Fps;
            OutputDirectory = last.OutputDirectory; NamePrefix = last.NamePrefix;
            SuperSample = last.SuperSample;
        }
    }

    private readonly Action<string, string>? _log;
    private readonly bool _loadFailed;

    public ObservableCollection<CinematicShot> Shots { get; } = new();
    public ObservableCollection<CinematicKeyRowViewModel> Keys { get; } = new();

    [ObservableProperty] private CinematicShot? _selectedShot;
    [ObservableProperty] private CinematicKeyRowViewModel? _selectedKey;
    [ObservableProperty] private string _status = "Fly the camera, then Add Keyframe.";

    /// <summary>Non-empty when the shots on disk could not be read, and therefore will not be written
    /// over. Shown for as long as it applies rather than as a status line that the next action clears.</summary>
    [ObservableProperty] private string _saveBlocked = "";
    [ObservableProperty] private double _scrubTime;
    [ObservableProperty] private bool _previewing;
    [ObservableProperty] private bool _capturing;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";

    // ---- capture settings -------------------------------------------------------------------------
    [ObservableProperty] private int _width = 1920;
    [ObservableProperty] private int _height = 1080;
    [ObservableProperty] private int _fps = 30;
    [ObservableProperty] private int _superSample = 1;
    [ObservableProperty] private string _outputDirectory = "";
    [ObservableProperty] private string _namePrefix = "Shot";

    public IReadOnlyList<string> Resolutions { get; } = new[] { "1920 x 1080", "2560 x 1440", "3840 x 2160" };
    public IReadOnlyList<int> FrameRates { get; } = new[] { 24, 30, 60 };
    public IReadOnlyList<int> SuperSamples { get; } = new[] { 1, 2 };

    /// <summary>Index into <see cref="Resolutions"/>, so the combo can be a plain string list.</summary>
    public int ResolutionIndex
    {
        get => (Width, Height) switch { (2560, 1440) => 1, (3840, 2160) => 2, _ => 0 };
        set
        {
            (Width, Height) = value switch { 1 => (2560, 1440), 2 => (3840, 2160), _ => (1920, 1080) };
            OnPropertyChanged();
            OnPropertyChanged(nameof(CaptureSummary));
        }
    }

    public double ShotDuration => SelectedShot?.Duration ?? 0d;

    public string CaptureSummary
    {
        get
        {
            if (SelectedShot is not { } shot || shot.Keyframes.Count < 2) return "Add at least two keyframes.";
            int frames = CinematicCapture.Plan(shot, BuildSettings()).Count;
            string ss = SuperSample > 1 ? $", rendered at {Width * SuperSample} x {Height * SuperSample}" : "";
            return $"{frames:n0} frames · {shot.Duration:0.00} s · {Width} x {Height} at {Fps} fps{ss}";
        }
    }

    partial void OnSelectedShotChanged(CinematicShot? value)
    {
        RefreshKeys();
        ScrubTime = 0;
        OnPropertyChanged(nameof(ShotDuration));
        OnPropertyChanged(nameof(CaptureSummary));
        if (value is not null && string.Equals(NamePrefix, "Shot", StringComparison.Ordinal)) NamePrefix = Safe(value.Name);
    }

    partial void OnFpsChanged(int value) => OnPropertyChanged(nameof(CaptureSummary));
    partial void OnSuperSampleChanged(int value) => OnPropertyChanged(nameof(CaptureSummary));

    partial void OnScrubTimeChanged(double value)
    {
        if (!Previewing || SelectedShot is not { } shot || shot.Keyframes.Count == 0) return;
        _host.PreviewPose(shot.Sample((float)value));
    }

    partial void OnPreviewingChanged(bool value)
    {
        if (value && SelectedShot is { Keyframes.Count: > 0 } shot) _host.PreviewPose(shot.Sample((float)ScrubTime));
        else _host.PreviewPose(null);
    }

    private void RefreshKeys()
    {
        Keys.Clear();
        if (SelectedShot is not { } shot) return;
        foreach (var key in shot.Keyframes) Keys.Add(new CinematicKeyRowViewModel(key));
    }

    private static string Safe(string name)
    {
        var cleaned = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length > 0 ? cleaned : "Shot";
    }

    // ---- shots ------------------------------------------------------------------------------------

    [RelayCommand]
    private void AddShot()
    {
        var shot = new CinematicShot { Name = $"Shot {Shots.Count + 1}" };
        Shots.Add(shot);
        _document.Shots.Add(shot);
        SelectedShot = shot;
        Save();
        Status = "Shot added. Fly the camera and press Add Keyframe.";
    }

    [RelayCommand]
    private void RemoveShot()
    {
        if (SelectedShot is not { } shot) return;
        Shots.Remove(shot);
        _document.Shots.Remove(shot);
        SelectedShot = Shots.FirstOrDefault();
        Save();
        Status = $"Removed {shot.Name}.";
    }

    // ---- keyframes --------------------------------------------------------------------------------

    /// <summary>Record the camera you are looking through, at the end of the shot. Taking the pose from
    /// the live camera rather than from typed numbers is the whole authoring model: you fly to the framing
    /// you want and press the button.</summary>
    [RelayCommand]
    private void AddKeyframe()
    {
        if (SelectedShot is not { } shot)
        {
            AddShot();
            shot = SelectedShot!;
        }

        var pose = _host.CurrentPose;
        float time = shot.Keyframes.Count == 0 ? 0f : shot.Keyframes[^1].Time + 2f;
        shot.Add(new CinematicKeyframe(time, pose.Position, pose.Orientation, pose.FieldOfView, pose.Roll));
        RefreshKeys();
        OnPropertyChanged(nameof(ShotDuration));
        OnPropertyChanged(nameof(CaptureSummary));
        Save();
        Status = $"Keyframe {shot.Keyframes.Count} at {time:0.00} s.";
    }

    [RelayCommand]
    private void RemoveKeyframe()
    {
        if (SelectedShot is not { } shot || SelectedKey is not { } row) return;
        shot.Remove(row.Key);
        RefreshKeys();
        OnPropertyChanged(nameof(ShotDuration));
        OnPropertyChanged(nameof(CaptureSummary));
        Save();
        Status = "Keyframe removed.";
    }

    /// <summary>Put the camera back where a keyframe was taken, so it can be re-flown and replaced.</summary>
    [RelayCommand]
    private void GoToKeyframe()
    {
        if (SelectedShot is not { } shot || SelectedKey is not { } row) return;
        Previewing = true;
        ScrubTime = Math.Clamp(row.Key.Time, 0d, shot.Duration);
        _host.PreviewPose(shot.Sample((float)ScrubTime));
    }

    // ---- capture ----------------------------------------------------------------------------------

    public CinematicCaptureSettings BuildSettings() =>
        new(Width, Height, Fps, OutputDirectory, Safe(NamePrefix), SuperSample);

    [RelayCommand]
    private async Task CaptureAsync()
    {
        if (SelectedShot is not { } shot) { Status = "Select a shot first."; return; }
        if (shot.Keyframes.Count < 2) { Status = "A shot needs at least two keyframes."; return; }

        var settings = BuildSettings();
        if (settings.Validate() is { } problem) { Status = problem; return; }

        _document.LastCapture = settings;
        Save();

        _capture = new CancellationTokenSource();
        Capturing = true;
        Progress = 0;
        ProgressText = "Starting…";
        bool wasPreviewing = Previewing;
        Previewing = false;

        var progress = new Progress<(int Done, int Total)>(p =>
        {
            Progress = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
            ProgressText = $"{p.Done:n0} / {p.Total:n0}";
        });

        CinematicCaptureResult result;
        // The scope has to outlive every frame: it holds the particle clock aside so walking the shot's
        // timeline does not hand the live viewport one enormous delta when the capture ends.
        using (var scope = _host.BeginCapture())
        {
            result = await CinematicCapture.RunAsync(shot, settings,
                (pose, w, h, t) => _host.RenderFrame(pose, w, h, t),
                progress, _capture.Token).ConfigureAwait(true);
        }

        Capturing = false;
        _capture.Dispose();
        _capture = null;
        Previewing = wasPreviewing;

        Status = result switch
        {
            { Error: { } e } => $"Capture failed after {result.FramesWritten:n0} frame(s): {e}",
            { Cancelled: true } => $"Cancelled after {result.FramesWritten:n0} of {result.FramesPlanned:n0} frame(s). What was written is usable.",
            _ => $"Wrote {result.FramesWritten:n0} frame(s) to {result.OutputDirectory}",
        };
        _log?.Invoke(result.Success ? "Cinematic" : "Cinematic", Status);
    }

    [RelayCommand]
    private void CancelCapture()
    {
        _capture?.Cancel();
        ProgressText = "Cancelling…";
    }

    // ---- persistence ------------------------------------------------------------------------------

    private void Save()
    {
        // A document that failed to load is never written over: the file on disk may be the only copy of
        // work that a newer build, or a hand edit, can still rescue.
        if (_loadFailed) return;
        try { _document.Save(_documentPath); }
        catch (Exception ex) { Status = $"Could not save shots: {ex.Message}"; }
    }
}
