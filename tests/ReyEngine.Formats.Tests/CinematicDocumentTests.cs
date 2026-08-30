using System.Numerics;
using ReyEngine.Core.Cinematics;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M606: shots survive closing the editor.
///
/// <para>Keyframes are the expensive part of a trailer — each one is placed by flying the camera and
/// looking — so a round trip that quietly drops or degrades one costs work that cannot be recovered by
/// re-running anything. The orientation is the fragile piece: it is stored as a quaternion for the same
/// reason it is interpolated as one, and a round trip through Euler angles would be lossy near the poles
/// in a way nobody notices until a shot is replayed.</para>
/// </summary>
public sealed class CinematicDocumentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "reyengine-doc-" + Guid.NewGuid().ToString("N")[..8]);

    private string Path_ => CinematicDocument.PathFor(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private static CinematicShot Shot(string name = "NexusReveal")
    {
        var shot = new CinematicShot { Name = name, SpeedScale = 1.5f, LookAtTarget = new Vector3(1, 2, 3) };
        shot.Add(new CinematicKeyframe(0f, new Vector3(10, 20, 30),
            Quaternion.CreateFromYawPitchRoll(0.4f, -0.2f, 0.1f), 0.9f, 0.15f, CinematicEase.EaseIn));
        shot.Add(new CinematicKeyframe(2.5f, new Vector3(-40, 5, 60),
            Quaternion.CreateFromYawPitchRoll(-2.1f, 0.5f, 0f), 1.2f, 0f, CinematicEase.EaseOut));
        return shot;
    }

    [Fact]
    public void AShotComesBackExactlyAsItWentIn()
    {
        var document = new CinematicDocument();
        document.Shots.Add(Shot());
        document.Save(Path_);

        var reloaded = CinematicDocument.Load(Path_);
        var a = document.Shots[0];
        var b = Assert.Single(reloaded.Shots);

        Assert.Equal(a.Name, b.Name);
        Assert.Equal(a.SpeedScale, b.SpeedScale, 5);
        Assert.Equal(a.LookAtTarget, b.LookAtTarget);
        Assert.Equal(a.Keyframes.Count, b.Keyframes.Count);

        for (int i = 0; i < a.Keyframes.Count; i++)
        {
            var (x, y) = (a.Keyframes[i], b.Keyframes[i]);
            Assert.Equal(x.Time, y.Time, 5);
            Assert.Equal(x.Position, y.Position);
            Assert.Equal(x.FieldOfView, y.FieldOfView, 5);
            Assert.Equal(x.Roll, y.Roll, 5);
            Assert.Equal(x.Ease, y.Ease);
            // The orientation is what a lossy format would degrade: compare the rotation, not the
            // components, since q and -q are the same rotation.
            Assert.True(MathF.Abs(Quaternion.Dot(x.Orientation, y.Orientation)) > 0.99999f);
        }
    }

    [Fact]
    public void TheSampledPathIsIdenticalAfterARoundTrip()
    {
        // The real property: not "the fields match" but "the camera flies the same move". A degraded
        // keyframe would still compare equal field by field at low precision and fly differently.
        var document = new CinematicDocument();
        document.Shots.Add(Shot());
        document.Save(Path_);
        var reloaded = CinematicDocument.Load(Path_);

        var before = document.Shots[0];
        var after = reloaded.Shots[0];
        for (float t = 0f; t <= before.Duration; t += 0.05f)
        {
            var p = before.Sample(t);
            var q = after.Sample(t);
            Assert.True(Vector3.Distance(p.Position, q.Position) < 1e-3f, $"position differs at t={t:f2}");
            Assert.True(Vector3.Distance(p.Forward, q.Forward) < 1e-3f, $"aim differs at t={t:f2}");
            Assert.Equal(p.FieldOfView, q.FieldOfView, 4);
        }
    }

    [Fact]
    public void KeyframesComeBackInTimeOrder()
    {
        var shot = new CinematicShot();
        shot.Add(new CinematicKeyframe(3f, Vector3.Zero, Quaternion.Identity, 1f));
        shot.Add(new CinematicKeyframe(1f, Vector3.One, Quaternion.Identity, 1f));
        var document = new CinematicDocument();
        document.Shots.Add(shot);
        document.Save(Path_);

        var reloaded = CinematicDocument.Load(Path_);
        Assert.Equal(new[] { 1f, 3f }, reloaded.Shots[0].Keyframes.Select(k => k.Time).ToArray());
    }

    [Fact]
    public void CaptureSettingsSurviveSoAReExportNeedsNoRetyping()
    {
        var document = new CinematicDocument
        {
            LastCapture = new CinematicCaptureSettings(3840, 2160, 60, _root, "Harrowing_NexusReveal", 2, 10, 200),
        };
        document.Save(Path_);

        var s = CinematicDocument.Load(Path_).LastCapture;
        Assert.NotNull(s);
        Assert.Equal((3840, 2160, 60, 2), (s!.Width, s.Height, s.Fps, s.SuperSample));
        Assert.Equal("Harrowing_NexusReveal", s.NamePrefix);
        Assert.Equal(10, s.StartFrame);
        Assert.Equal(200, s.EndFrame);
    }

    [Fact]
    public void SeveralShotsKeepTheirOrder()
    {
        var document = new CinematicDocument();
        foreach (string name in new[] { "Open", "Push", "Reveal" }) document.Shots.Add(Shot(name));
        document.Save(Path_);

        Assert.Equal(new[] { "Open", "Push", "Reveal" },
            CinematicDocument.Load(Path_).Shots.Select(s => s.Name).ToArray());
    }

    [Fact]
    public void NoFileMeansAnEmptyDocumentRatherThanAnError()
    {
        // Opening a project that has never had a shot is the common case, not a failure.
        var document = CinematicDocument.Load(Path_);
        Assert.Empty(document.Shots);
        Assert.Null(document.LastCapture);
    }

    [Fact]
    public void AFileFromANewerBuildIsRefusedRatherThanHalfRead()
    {
        // Silently starting empty would look exactly like the shots having been lost.
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
        File.WriteAllText(Path_, """{ "Version": 999, "Shots": [] }""");

        var ex = Assert.Throws<InvalidDataException>(() => CinematicDocument.Load(Path_));
        Assert.Contains("newer", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AHandEditedQuaternionThatIsNotUnitLengthIsNormalisedOnLoad()
    {
        // The file is meant to be readable and rescuable by hand. An unnormalised quaternion skews the
        // view matrix rather than failing, so it is fixed on the way in.
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
        File.WriteAllText(Path_, """
        {
          "Version": 1,
          "Shots": [{
            "Name": "Hand", "SpeedScale": 1,
            "Keys": [{ "Time": 0, "Position": [0,0,0], "Orientation": [0,0,0,5], "FieldOfView": 1 }]
          }]
        }
        """);

        var shot = CinematicDocument.Load(Path_).Shots[0];
        Assert.Equal(1f, shot.Keyframes[0].Orientation.Length(), 4);
    }

    [Fact]
    public void SavingTwiceLeavesNoTemporaryFileBehind()
    {
        // The write goes to a .tmp and moves into place so a crash mid-write cannot truncate the real
        // file. The temp must not survive a successful save.
        var document = new CinematicDocument();
        document.Shots.Add(Shot());
        document.Save(Path_);
        document.Save(Path_);

        Assert.True(File.Exists(Path_));
        Assert.False(File.Exists(Path_ + ".tmp"));
        Assert.Single(CinematicDocument.Load(Path_).Shots);
    }

    [Fact]
    public void TheDocumentLivesInsideTheProjectRatherThanInGlobalSettings()
    {
        // Two maps have nothing to say to each other about where a camera should fly.
        Assert.StartsWith(_root, CinematicDocument.PathFor(_root), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".reyengine", CinematicDocument.PathFor(_root), StringComparison.OrdinalIgnoreCase);
    }
}
