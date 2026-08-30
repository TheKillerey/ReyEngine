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
}
