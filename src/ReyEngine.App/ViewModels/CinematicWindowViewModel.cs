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

    /// <summary>M693: the same frame, rendered where the host's renderer lives and at a priority that
    /// lets input and the live viewport through between frames. The default runs it inline, which is
    /// right for a host that is already on its render thread.</summary>
    Task<byte[]?> RenderFrameAsync(CinematicPose pose, int width, int height, float timeSeconds)
        => Task.FromResult(RenderFrame(pose, width, height, timeSeconds));

    /// <summary>M693: a line for the host's status bar while a capture runs; null when it ends.</summary>
    void ReportCapture(string? status) { }
}

/// <summary>One keyframe, as a row the panel can show and edit.</summary>
public sealed partial class CinematicKeyRowViewModel : ObservableObject
{
    public CinematicKeyRowViewModel(CinematicKeyframe key, int index, float segmentSeconds)
    {
        Key = key;
        Index = index;
        SegmentSeconds = segmentSeconds;
    }

    public CinematicKeyframe Key { get; set; }
    public int Index { get; }

    /// <summary>How long the move LEAVING this keyframe lasts. Keyframe times are absolute, so without
    /// this the author has to subtract two numbers in their head to answer "how long is this bit" - which
    /// is the question actually being asked when a move feels too fast.</summary>
    public float SegmentSeconds { get; }

    public string NumberText => (Index + 1).ToString(CultureInfo.InvariantCulture);
    public string TimeText => Key.Time.ToString("0.00", CultureInfo.InvariantCulture) + " s";
    public string PositionText =>
        $"{Key.Position.X:0}, {Key.Position.Y:0}, {Key.Position.Z:0}";
    public string FovText => (Key.FieldOfView * 180f / MathF.PI).ToString("0", CultureInfo.InvariantCulture) + "°";
    public string EaseText => Key.Ease.ToString();
    public string BlendText => Key.Blend.ToString();

    /// <summary>Empty on the last keyframe, which nothing leaves.</summary>
    public string SegmentText => SegmentSeconds <= 0f
        ? "—"
        : "+" + SegmentSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s";
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
        OnPropertyChanged(nameof(ShotSpeed));
        OnPropertyChanged(nameof(ShotSummary));
        if (value is not null && string.Equals(NamePrefix, "Shot", StringComparison.Ordinal)) NamePrefix = Safe(value.Name);
    }

    /// <summary>Per-shot playback speed. Changing it rescales every duration shown, which is why the rows
    /// are rebuilt rather than just the total.</summary>
    public double ShotSpeed
    {
        get => SelectedShot?.SpeedScale ?? 1d;
        set
        {
            if (SelectedShot is not { } shot) return;
            shot.SpeedScale = (float)Math.Clamp(value, 0.05d, 20d);
            OnPropertyChanged();
            RefreshKeys();
            OnPropertyChanged(nameof(ShotDuration));
            OnPropertyChanged(nameof(CaptureSummary));
            Save();
        }
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
        var previous = SelectedKey?.Key;
        int previousRow = SelectedKey?.Index ?? -1;
        Keys.Clear();
        if (SelectedShot is { } shot)
        {
            for (int i = 0; i < shot.Keyframes.Count; i++)
                Keys.Add(new CinematicKeyRowViewModel(shot.Keyframes[i], i, shot.SegmentDuration(i)));

            // Editing a keyframe rebuilds every row, and losing the selection each time would make
            // changing several fields in a row impossible. Match on the keyframe first; commands that
            // rewrite MANY keys at once (spacing them evenly, applying one blend to all) leave nothing
            // to match, so fall back to the same ROW rather than jumping the selection to the top.
            if (previousRow >= 0)
                SelectedKey = Keys.FirstOrDefault(r => r.Key == previous)
                              ?? Keys.ElementAtOrDefault(Math.Min(previousRow, Keys.Count - 1));
        }
        OnPropertyChanged(nameof(ShotSummary));
    }

    /// <summary>What the selected shot costs, in the terms the author is deciding in.</summary>
    public string ShotSummary => SelectedShot is not { } shot || shot.Keyframes.Count == 0
        ? "No keyframes yet."
        : $"{shot.Keyframes.Count} keyframe(s) · {shot.Duration:0.00} s"
          + (Math.Abs(shot.SpeedScale - 1f) > 0.001f ? $" at {shot.SpeedScale:0.##}x speed" : "");

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

    // ---- editing the selected keyframe ------------------------------------------------------------
    //
    // Keyframes are immutable records, so every edit is Replace(old, old with { ... }). That keeps the
    // shot's time ordering in one place and means an edit can never leave the list half-sorted.

    public IReadOnlyList<CinematicEase> Eases { get; } = Enum.GetValues<CinematicEase>();
    public IReadOnlyList<CinematicBlend> Blends { get; } = Enum.GetValues<CinematicBlend>();

    /// <summary>Guards the edit properties below: they are re-read whenever the selection changes, and
    /// without this each assignment would write itself straight back into the shot.</summary>
    private bool _syncingEdit;

    [ObservableProperty] private double _editTime;
    [ObservableProperty] private double _editFovDegrees;
    [ObservableProperty] private double _editRollDegrees;
    [ObservableProperty] private CinematicEase _editEase;
    [ObservableProperty] private CinematicBlend _editBlend;

    public bool HasSelectedKey => SelectedKey is not null;

    partial void OnSelectedKeyChanged(CinematicKeyRowViewModel? value)
    {
        _syncingEdit = true;
        if (value is { Key: var k })
        {
            EditTime = k.Time;
            EditFovDegrees = k.FieldOfView * 180d / Math.PI;
            EditRollDegrees = k.Roll * 180d / Math.PI;
            EditEase = k.Ease;
            EditBlend = k.Blend;
        }
        _syncingEdit = false;
        OnPropertyChanged(nameof(HasSelectedKey));
    }

    partial void OnEditTimeChanged(double value) => ApplyEdit(k => k with { Time = (float)Math.Max(0d, value) });
    partial void OnEditFovDegreesChanged(double value) =>
        ApplyEdit(k => k with { FieldOfView = (float)(Math.Clamp(value, 1d, 170d) * Math.PI / 180d) });
    partial void OnEditRollDegreesChanged(double value) =>
        ApplyEdit(k => k with { Roll = (float)(value * Math.PI / 180d) });
    partial void OnEditEaseChanged(CinematicEase value) => ApplyEdit(k => k with { Ease = value });
    partial void OnEditBlendChanged(CinematicBlend value) => ApplyEdit(k => k with { Blend = value });

    private void ApplyEdit(Func<CinematicKeyframe, CinematicKeyframe> edit)
    {
        if (_syncingEdit || SelectedShot is not { } shot || SelectedKey is not { } row) return;
        var updated = edit(row.Key);
        if (updated == row.Key) return;
        if (!shot.Replace(row.Key, updated)) return;

        row.Key = updated;
        RefreshKeys();
        OnPropertyChanged(nameof(ShotDuration));
        OnPropertyChanged(nameof(CaptureSummary));
        Save();
        if (Previewing) _host.PreviewPose(shot.Sample((float)ScrubTime));
    }

    /// <summary>Re-record the selected keyframe from the camera you are looking through now, keeping its
    /// time and its blending. Re-flying to a better framing is the natural way to fix a keyframe, and
    /// deleting and re-adding one loses its place in the shot.</summary>
    [RelayCommand]
    private void UpdateKeyframeFromCamera()
    {
        if (SelectedShot is not { } shot || SelectedKey is not { } row) return;
        var pose = _host.CurrentPose;
        var updated = row.Key with
        {
            Position = pose.Position,
            Orientation = pose.Orientation,
            FieldOfView = pose.FieldOfView,
        };
        if (!shot.Replace(row.Key, updated)) return;
        row.Key = updated;
        RefreshKeys();
        Save();
        Status = $"Keyframe {row.Index + 1} re-recorded from the current camera.";
    }

    /// <summary>Space every keyframe evenly across the shot's current length. The quickest fix for a
    /// sequence whose timing drifted while it was being built.</summary>
    [RelayCommand]
    private void DistributeEvenly()
    {
        if (SelectedShot is not { } shot || shot.Keyframes.Count < 3) return;
        float total = shot.Keyframes[^1].Time - shot.Keyframes[0].Time;
        float start = shot.Keyframes[0].Time;
        int last = shot.Keyframes.Count - 1;

        // Snapshot first: Replace re-sorts, and mutating while walking the live list would skip rows.
        var snapshot = shot.Keyframes.ToList();
        for (int i = 1; i < last; i++)
            shot.Replace(snapshot[i], snapshot[i] with { Time = start + total * i / last });

        RefreshKeys();
        Save();
        Status = $"Spaced {shot.Keyframes.Count} keyframes evenly across {shot.Duration:0.00} s.";
    }

    /// <summary>Apply the selected keyframe's easing and blend to every keyframe in the shot.</summary>
    [RelayCommand]
    private void ApplyBlendToAll()
    {
        if (SelectedShot is not { } shot || SelectedKey is not { } row) return;
        foreach (var key in shot.Keyframes.ToList())
            shot.Replace(key, key with { Ease = row.Key.Ease, Blend = row.Key.Blend });
        RefreshKeys();
        Save();
        Status = $"Every keyframe now uses {row.Key.Blend} / {row.Key.Ease}.";
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

        int rendered = 0, total = 0;
        void Show(int done)
        {
            Progress = total == 0 ? 0 : 100.0 * done / total;
            ProgressText = $"rendered {rendered:n0} · written {done:n0} / {total:n0}";
            _host.ReportCapture($"Capturing {shot.Name}: frame {rendered:n0} of {total:n0} rendered, {done:n0} written");
        }
        int writtenSoFar = 0;
        var progress = new Progress<(int Done, int Total)>(p => { total = p.Total; writtenSoFar = p.Done; Show(p.Done); });
        var renderedProgress = new Progress<int>(n => { rendered = n; Show(writtenSoFar); });
        total = CinematicCapture.Plan(shot, settings).Count;

        CinematicCaptureResult result;
        // The scope has to outlive every frame: it holds the particle clock aside so walking the shot's
        // timeline does not hand the live viewport one enormous delta when the capture ends.
        using (var scope = _host.BeginCapture())
        {
            // M693: each frame is rendered where the host's renderer lives (the UI thread, at background
            // priority, so the editor stays responsive and the live viewport shows the shot as it goes),
            // and the PNGs are encoded behind the renderer instead of in front of it.
            result = await CinematicCapture.RunAsync(shot, settings,
                async (pose, w, h, t) =>
                {
                    var bytes = await _host.RenderFrameAsync(pose, w, h, t).ConfigureAwait(false);
                    return bytes is { } b ? (ReadOnlyMemory<byte>?)b : null;
                },
                progress, n => ((IProgress<int>)renderedProgress).Report(n), _capture.Token).ConfigureAwait(true);
        }

        Capturing = false;
        _capture.Dispose();
        _capture = null;
        Previewing = wasPreviewing;
        if (!wasPreviewing) _host.PreviewPose(null);   // the live viewport followed the shot; hand the camera back
        _host.ReportCapture(null);

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
