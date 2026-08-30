using System.Numerics;
using System.Reflection;
using System.Text.RegularExpressions;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Cinematics;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M607: the cinematic panel's view model, and the bindings the window points at it.
///
/// <para>A binding to a property that does not exist is Avalonia's quietest failure: the control simply
/// shows nothing and the build stays green. That is what this file is mostly for — every
/// <c>{Binding X}</c> in the window is resolved against the type that will actually be its data context.
/// It is not a substitute for launching the app, but it catches the whole class of typo that launching
/// would only reveal if you happened to look at the right control.</para>
/// </summary>
public sealed class CinematicPanelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "reyengine-panel-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    /// <summary>A host that records what the panel asked it to do, and renders flat frames.</summary>
    private sealed class FakeHost : ICinematicHost
    {
        public CinematicPose Pose = new(new Vector3(10, 20, 30), Quaternion.Identity, 1.1f, 0f);
        public readonly List<CinematicPose?> Previews = new();
        public readonly List<float> RenderedTimes = new();
        public int BeginCalls, EndCalls;

        public CinematicPose CurrentPose => Pose;
        public void PreviewPose(CinematicPose? pose) => Previews.Add(pose);
        public IDisposable BeginCapture() { BeginCalls++; return new Scope(this); }
        public byte[]? RenderFrame(CinematicPose pose, int width, int height, float timeSeconds)
        {
            RenderedTimes.Add(timeSeconds);
            return new byte[width * height * 4];
        }

        private sealed class Scope(FakeHost host) : IDisposable
        {
            public void Dispose() => host.EndCalls++;
        }
    }

    private CinematicWindowViewModel Panel(out FakeHost host)
    {
        host = new FakeHost();
        return new CinematicWindowViewModel(host, _root);
    }

    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        return dir?.FullName;
    }

    // ===================================================== the bindings resolve

    [Fact]
    public void EveryBindingInTheWindowResolvesToSomethingThatExists()
    {
        if (RepoRoot() is not { } root) return;
        string xaml = Path.Combine(root, "src", "ReyEngine.App", "Views", "CinematicWindow.axaml");
        if (!File.Exists(xaml)) return;

        // The window's own data context, plus the two item templates inside it.
        Type[] contexts =
        {
            typeof(CinematicWindowViewModel),
            typeof(CinematicKeyRowViewModel),
            typeof(CinematicShot),
        };

        var missing = new List<string>();
        foreach (Match m in Regex.Matches(File.ReadAllText(xaml), @"\{Binding\s+!?([A-Za-z_][A-Za-z0-9_]*)"))
        {
            string path = m.Groups[1].Value;
            if (path is "Binding") continue;
            bool found = contexts.Any(t =>
                t.GetProperty(path, BindingFlags.Public | BindingFlags.Instance) is not null);
            if (!found) missing.Add(path);
        }

        Assert.True(missing.Count == 0,
            "bound in CinematicWindow.axaml but present on none of the data contexts: "
            + string.Join(", ", missing.Distinct()));
    }

    [Fact]
    public void TheCommandsTheWindowInvokesAllExist()
    {
        // CommunityToolkit generates XxxCommand from a [RelayCommand] method, so a renamed method leaves
        // the XAML pointing at a command that is silently never created.
        foreach (string name in new[]
                 {
                     "AddShotCommand", "RemoveShotCommand", "AddKeyframeCommand", "RemoveKeyframeCommand",
                     "GoToKeyframeCommand", "CaptureCommand", "CancelCaptureCommand",
                     "UpdateKeyframeFromCameraCommand", "DistributeEvenlyCommand", "ApplyBlendToAllCommand",
                 })
            Assert.NotNull(typeof(CinematicWindowViewModel).GetProperty(name));
    }

    // ===================================================== behaviour

    [Fact]
    public void AddingAKeyframeRecordsTheCameraYouAreLookingThrough()
    {
        // The authoring model: fly to the framing, press the button. Not typed numbers.
        var panel = Panel(out var host);
        panel.AddShotCommand.Execute(null);
        panel.AddKeyframeCommand.Execute(null);

        var key = Assert.Single(panel.SelectedShot!.Keyframes);
        Assert.Equal(host.Pose.Position, key.Position);
        Assert.Equal(host.Pose.FieldOfView, key.FieldOfView, 5);
        Assert.Single(panel.Keys);
    }

    [Fact]
    public void AddingAKeyframeWithNoShotCreatesOneRatherThanDoingNothing()
    {
        var panel = Panel(out _);
        panel.AddKeyframeCommand.Execute(null);

        Assert.Single(panel.Shots);
        Assert.Single(panel.SelectedShot!.Keyframes);
    }

    [Fact]
    public void KeyframesAreSpacedInTimeSoASecondOneDoesNotLandOnTheFirst()
    {
        // Two keyframes at t=0 is a zero-length shot that exports one frame - a silent dead end.
        var panel = Panel(out _);
        panel.AddKeyframeCommand.Execute(null);
        panel.AddKeyframeCommand.Execute(null);

        Assert.True(panel.SelectedShot!.Duration > 0f);
        Assert.Equal(2, panel.Keys.Count);
    }

    [Fact]
    public void ShotsSurviveClosingAndReopeningThePanel()
    {
        var panel = Panel(out _);
        panel.AddKeyframeCommand.Execute(null);
        panel.AddKeyframeCommand.Execute(null);
        string shotName = panel.SelectedShot!.Name;

        var reopened = Panel(out _);
        Assert.Equal(shotName, Assert.Single(reopened.Shots).Name);
        Assert.Equal(2, reopened.Shots[0].Keyframes.Count);
    }

    [Fact]
    public void ScrubbingOnlyDrivesTheViewportWhilePreviewIsOn()
    {
        // Moving the slider must not seize the camera from someone who is still flying it.
        var panel = Panel(out var host);
        panel.AddKeyframeCommand.Execute(null);
        panel.AddKeyframeCommand.Execute(null);

        panel.ScrubTime = 1.0;
        Assert.Empty(host.Previews);

        panel.Previewing = true;
        panel.ScrubTime = 1.5;
        Assert.NotEmpty(host.Previews);
        Assert.All(host.Previews, p => Assert.NotNull(p));
    }

    [Fact]
    public void TurningPreviewOffHandsTheCameraBack()
    {
        var panel = Panel(out var host);
        panel.AddKeyframeCommand.Execute(null);
        panel.Previewing = true;
        panel.Previewing = false;

        Assert.Null(host.Previews[^1]);
    }

    [Fact]
    public async Task CapturingWrapsEveryFrameInOneScopeAndWalksTheShotsTimeline()
    {
        // The scope has to open once and close once: it holds the particle clock aside, and reopening it
        // per frame would reset the delta every time.
        var panel = Panel(out var host);
        panel.AddKeyframeCommand.Execute(null);
        panel.AddKeyframeCommand.Execute(null);
        panel.OutputDirectory = Path.Combine(_root, "out");
        panel.Fps = 24;
        panel.ResolutionIndex = 0;

        await panel.CaptureCommand.ExecuteAsync(null);

        Assert.Equal(1, host.BeginCalls);
        Assert.Equal(1, host.EndCalls);
        Assert.NotEmpty(host.RenderedTimes);
        Assert.Equal(host.RenderedTimes.OrderBy(t => t), host.RenderedTimes);   // strictly forward in time
        Assert.False(panel.Capturing);
    }

    [Fact]
    public async Task CapturingWithTooFewKeyframesSaysSoRatherThanWritingAStill()
    {
        var panel = Panel(out var host);
        panel.AddKeyframeCommand.Execute(null);
        panel.OutputDirectory = Path.Combine(_root, "out");

        await panel.CaptureCommand.ExecuteAsync(null);

        Assert.Equal(0, host.BeginCalls);
        Assert.Contains("two keyframes", panel.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BadSettingsAreReportedBeforeAnythingIsRendered()
    {
        var panel = Panel(out var host);
        panel.AddKeyframeCommand.Execute(null);
        panel.AddKeyframeCommand.Execute(null);
        panel.OutputDirectory = "";     // no folder chosen

        await panel.CaptureCommand.ExecuteAsync(null);

        Assert.Equal(0, host.BeginCalls);
        Assert.False(string.IsNullOrWhiteSpace(panel.Status));
    }

    [Fact]
    public void TheResolutionChoicesAreTheOnesTheSpecAsksFor()
    {
        var panel = Panel(out _);
        Assert.Equal(new[] { "1920 x 1080", "2560 x 1440", "3840 x 2160" }, panel.Resolutions.ToArray());
        Assert.Equal(new[] { 24, 30, 60 }, panel.FrameRates.ToArray());

        panel.ResolutionIndex = 2;
        Assert.Equal((3840, 2160), (panel.Width, panel.Height));
    }

    [Fact]
    public void TheSummarySaysHowManyFramesTheCaptureWillWrite()
    {
        // A 4K 60 fps export is a lot of files and a long wait; saying so up front is the difference
        // between a considered click and a surprise.
        var panel = Panel(out _);
        panel.AddKeyframeCommand.Execute(null);
        panel.AddKeyframeCommand.Execute(null);
        panel.Fps = 30;

        Assert.Contains("frames", panel.CaptureSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1920 x 1080", panel.CaptureSummary);
    }

    [Fact]
    public void ADocumentThatCannotBeReadIsNeverOverwritten()
    {
        // The file on disk may be the only copy of work a newer build or a hand edit could still rescue.
        string path = CinematicDocument.PathFor(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "Version": 999 }""");
        string before = File.ReadAllText(path);

        var panel = Panel(out _);
        panel.AddKeyframeCommand.Execute(null);

        Assert.Equal(before, File.ReadAllText(path));
        // The warning has to OUTLIVE the next action - a status line would have been wiped by the
        // keyframe message above, leaving the user building a shot that is never saved.
        Assert.Contains("Could not read", panel.SaveBlocked, StringComparison.OrdinalIgnoreCase);
    }

    // ===================================================== M608: timing and blending in the panel

    [Fact]
    public void EveryRowSaysHowLongItsOwnMoveLasts()
    {
        // "How long is this bit" is the question an author asks when a move feels wrong, and keyframe
        // times are absolute - so without this they are subtracting two numbers in their head.
        var panel = Panel(out _);
        panel.AddKeyframeCommand.Execute(null);
        panel.AddKeyframeCommand.Execute(null);
        panel.AddKeyframeCommand.Execute(null);

        Assert.Equal(3, panel.Keys.Count);
        Assert.Equal("1", panel.Keys[0].NumberText);
        Assert.StartsWith("+", panel.Keys[0].SegmentText);
        Assert.StartsWith("+", panel.Keys[1].SegmentText);
        Assert.Equal("—", panel.Keys[^1].SegmentText);   // nothing leaves the last keyframe
    }

    [Fact]
    public void TheSegmentLengthsShownAddUpToTheShotLength()
    {
        var panel = Panel(out _);
        panel.AddKeyframeCommand.Execute(null);
        panel.AddKeyframeCommand.Execute(null);
        panel.AddKeyframeCommand.Execute(null);

        float sum = panel.Keys.Sum(k => k.SegmentSeconds);
        Assert.Equal(panel.SelectedShot!.Duration, sum, 3);
    }

    [Fact]
    public void RetimingTheSelectedKeyframeMovesItAndIsSaved()
    {
        var panel = Panel(out _);
        panel.AddKeyframeCommand.Execute(null);
        panel.AddKeyframeCommand.Execute(null);
        panel.SelectedKey = panel.Keys[1];

        panel.EditTime = 8.0;

        Assert.Equal(8f, panel.SelectedShot!.Keyframes[^1].Time, 3);
        Assert.Equal(8f, panel.ShotDuration, 3);
        Assert.Equal(8f, Panel(out _).Shots[0].Keyframes[^1].Time, 3);   // reopened from disk
    }

    [Fact]
    public void EditingAKeyframeKeepsItSelectedSoSeveralFieldsCanBeChangedInARow()
    {
        // Every edit rebuilds the rows. Losing the selection each time would mean re-clicking the row
        // between setting the time, the blend and the field of view.
        var panel = Panel(out _);
        panel.AddKeyframeCommand.Execute(null);
        panel.AddKeyframeCommand.Execute(null);
        panel.SelectedKey = panel.Keys[0];

        panel.EditFovDegrees = 35;
        Assert.True(panel.HasSelectedKey);
        Assert.Equal(0, panel.SelectedKey!.Index);

        panel.EditBlend = CinematicBlend.Hold;
        Assert.Equal(CinematicBlend.Hold, panel.SelectedShot!.Keyframes[0].Blend);
        Assert.Equal(35f, panel.SelectedShot.Keyframes[0].FieldOfView * 180f / MathF.PI, 1);
    }

    [Fact]
    public void SelectingAKeyframeLoadsItsValuesIntoTheEditorWithoutWritingThemBack()
    {
        // The editor fields are re-read on every selection change. If that re-read went through the
        // same path as a user edit, clicking a row would rewrite the keyframe it just showed.
        var panel = Panel(out _);
        panel.AddKeyframeCommand.Execute(null);
        panel.AddKeyframeCommand.Execute(null);
        panel.SelectedKey = panel.Keys[1];
        panel.EditBlend = CinematicBlend.Linear;

        var before = panel.SelectedShot!.Keyframes.ToList();
        panel.SelectedKey = panel.Keys[0];
        panel.SelectedKey = panel.Keys[1];

        Assert.Equal(before, panel.SelectedShot.Keyframes);
        Assert.Equal(CinematicBlend.Linear, panel.EditBlend);
    }

    [Fact]
    public void ApplyingABlendToAllSetsEveryKeyframeAndKeepsTheSelection()
    {
        var panel = Panel(out _);
        for (int i = 0; i < 4; i++) panel.AddKeyframeCommand.Execute(null);
        panel.SelectedKey = panel.Keys[2];
        panel.EditBlend = CinematicBlend.Linear;
        panel.EditEase = CinematicEase.SmoothStep;

        panel.ApplyBlendToAllCommand.Execute(null);

        Assert.All(panel.SelectedShot!.Keyframes, k =>
        {
            Assert.Equal(CinematicBlend.Linear, k.Blend);
            Assert.Equal(CinematicEase.SmoothStep, k.Ease);
        });
        Assert.Equal(2, panel.SelectedKey!.Index);
    }

    [Fact]
    public void SpacingEvenlyLeavesTheEndsWhereTheyAreAndEvensOutTheMiddle()
    {
        var panel = Panel(out _);
        for (int i = 0; i < 4; i++) panel.AddKeyframeCommand.Execute(null);
        panel.SelectedKey = panel.Keys[1];
        panel.EditTime = 0.1;                       // bunch the middle up against the start
        panel.SelectedKey = panel.Keys[2];
        panel.EditTime = 0.2;

        var shot = panel.SelectedShot!;
        float first = shot.Keyframes[0].Time, last = shot.Keyframes[^1].Time;
        panel.DistributeEvenlyCommand.Execute(null);

        Assert.Equal(first, shot.Keyframes[0].Time, 3);
        Assert.Equal(last, shot.Keyframes[^1].Time, 3);
        float step = (last - first) / 3f;
        Assert.Equal(first + step, shot.Keyframes[1].Time, 3);
        Assert.Equal(first + step * 2f, shot.Keyframes[2].Time, 3);
    }

    [Fact]
    public void ReRecordingAKeyframeTakesTheNewCameraButKeepsItsPlaceInTheShot()
    {
        // Re-flying to a better framing is the natural fix. Deleting and re-adding would lose the
        // keyframe timing and its blend, which is the work the author actually wants to keep.
        var panel = Panel(out var host);
        panel.AddKeyframeCommand.Execute(null);
        panel.AddKeyframeCommand.Execute(null);
        panel.SelectedKey = panel.Keys[0];
        panel.EditBlend = CinematicBlend.Hold;
        float time = panel.SelectedShot!.Keyframes[0].Time;

        host.Pose = new CinematicPose(new Vector3(-400, 900, 55), Quaternion.Identity, 0.7f, 0f);
        panel.UpdateKeyframeFromCameraCommand.Execute(null);

        var key = panel.SelectedShot.Keyframes[0];
        Assert.Equal(new Vector3(-400, 900, 55), key.Position);
        Assert.Equal(0.7f, key.FieldOfView, 3);
        Assert.Equal(time, key.Time, 3);
        Assert.Equal(CinematicBlend.Hold, key.Blend);
    }

    [Fact]
    public void ShotSpeedRetimesTheWholeShotWithoutMovingAKeyframe()
    {
        var panel = Panel(out _);
        panel.AddKeyframeCommand.Execute(null);
        panel.AddKeyframeCommand.Execute(null);
        float authored = panel.SelectedShot!.Keyframes[^1].Time;
        double before = panel.ShotDuration;

        panel.ShotSpeed = 2.0;

        Assert.Equal(before / 2d, panel.ShotDuration, 3);
        Assert.Equal(authored, panel.SelectedShot.Keyframes[^1].Time, 3);
        Assert.Equal(2f, Panel(out _).Shots[0].SpeedScale, 3);   // and it is saved
    }

    [Fact]
    public void TheShotSummarySaysHowManyKeyframesAndHowLong()
    {
        var panel = Panel(out _);
        panel.AddKeyframeCommand.Execute(null);
        panel.AddKeyframeCommand.Execute(null);

        Assert.Contains("2", panel.ShotSummary);
        Assert.Contains("s", panel.ShotSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NothingIsSelectedUntilARowIs()
    {
        // The whole editor block is gated on this, so a wrong answer either hides the controls or shows
        // an editor bound to nothing.
        var panel = Panel(out _);
        Assert.False(panel.HasSelectedKey);
        panel.AddKeyframeCommand.Execute(null);
        panel.SelectedKey = panel.Keys[0];
        Assert.True(panel.HasSelectedKey);
    }
}
