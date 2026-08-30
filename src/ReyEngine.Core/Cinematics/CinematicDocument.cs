using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReyEngine.Core.Cinematics;

/// <summary>
/// M606: every shot a project has, and the capture settings last used, saved beside the project.
///
/// <para>A trailer is not one shot and not one session. The keyframes are the expensive part — they are
/// placed by flying the camera and looking — so they have to outlive closing the editor, and they belong
/// WITH the project rather than in global settings: two maps have nothing to say to each other about
/// where a camera should fly.</para>
///
/// <para>Serialised through explicit DTOs rather than by pointing a serialiser at
/// <see cref="CinematicShot"/>. The shot holds its keyframes in a private list it keeps sorted, which a
/// round trip through auto-serialisation would either bypass or fail on; and a hand-written schema is one
/// that can be read and edited by hand when something needs rescuing.</para>
/// </summary>
public sealed class CinematicDocument
{
    /// <summary>Bumped when the schema changes shape. A file from the future is refused rather than
    /// half-read — losing a trailer's keyframes to a silent partial load is worse than an error.</summary>
    public const int CurrentVersion = 1;

    public List<CinematicShot> Shots { get; } = new();

    /// <summary>The capture settings last used, so re-exporting does not mean re-typing them. Null until
    /// a capture has been configured.</summary>
    public CinematicCaptureSettings? LastCapture { get; set; }

    /// <summary>Where this lives for a project rooted at <paramref name="projectRoot"/>.</summary>
    public static string PathFor(string projectRoot) =>
        Path.Combine(projectRoot, ".reyengine", "cinematics.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Easing and blend modes are written by NAME. As bare numbers they are positional, so the
        // meaning of a saved shot would depend on the order of an enum in a build the file has no way of
        // naming - and a file whose docs promise it can be rescued by hand should not need a lookup
        // table to read. Numbers are still accepted on load, so documents saved before this still open.
        Converters = { new JsonStringEnumConverter() },
    };

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var dto = new DocumentDto
        {
            Version = CurrentVersion,
            Shots = Shots.Select(ShotDto.From).ToList(),
            LastCapture = LastCapture is { } c ? CaptureDto.From(c) : null,
        };

        // Write beside and move into place: a half-written file is how a trailer's keyframes get lost,
        // and the failure would only show on the next open.
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(dto, Json));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Load, or an empty document when there is nothing to load. Throws only on a file that
    /// exists and cannot be understood — silently starting empty would look like the shots were lost.</summary>
    public static CinematicDocument Load(string path)
    {
        var document = new CinematicDocument();
        if (!File.Exists(path)) return document;

        var dto = JsonSerializer.Deserialize<DocumentDto>(File.ReadAllText(path), Json)
                  ?? throw new InvalidDataException($"{Path.GetFileName(path)} is empty.");
        if (dto.Version > CurrentVersion)
            throw new InvalidDataException(
                $"{Path.GetFileName(path)} was written by a newer ReyEngine (format {dto.Version}, this build reads {CurrentVersion}).");

        foreach (var shot in dto.Shots ?? new()) document.Shots.Add(shot.ToShot());
        document.LastCapture = dto.LastCapture?.ToSettings();
        return document;
    }

    // ---- wire format -----------------------------------------------------------------------------

    private sealed class DocumentDto
    {
        public int Version { get; set; }
        public List<ShotDto>? Shots { get; set; }
        public CaptureDto? LastCapture { get; set; }
    }

    private sealed class ShotDto
    {
        public string Name { get; set; } = "Shot";
        public float SpeedScale { get; set; } = 1f;
        public float[]? LookAtTarget { get; set; }
        public List<KeyDto> Keys { get; set; } = new();

        public static ShotDto From(CinematicShot shot) => new()
        {
            Name = shot.Name,
            SpeedScale = shot.SpeedScale,
            LookAtTarget = shot.LookAtTarget is { } t ? new[] { t.X, t.Y, t.Z } : null,
            Keys = shot.Keyframes.Select(KeyDto.From).ToList(),
        };

        public CinematicShot ToShot()
        {
            var shot = new CinematicShot { Name = Name, SpeedScale = SpeedScale };
            if (LookAtTarget is { Length: 3 } t) shot.LookAtTarget = new Vector3(t[0], t[1], t[2]);
            foreach (var key in Keys) shot.Add(key.ToKeyframe());
            return shot;
        }
    }

    private sealed class KeyDto
    {
        public float Time { get; set; }
        public float[] Position { get; set; } = new float[3];
        /// <summary>x, y, z, w. Stored as the quaternion rather than as Euler angles for the same reason
        /// it is interpolated as one: a round trip through yaw/pitch/roll is lossy near the poles.</summary>
        public float[] Orientation { get; set; } = { 0f, 0f, 0f, 1f };
        public float FieldOfView { get; set; } = MathF.PI / 4f;
        public float Roll { get; set; }
        public CinematicEase Ease { get; set; } = CinematicEase.EaseInOut;
        /// <summary>Absent in a v1 file, which predates blend modes - those keyframes load as Spline,
        /// the behaviour they were authored against.</summary>
        public CinematicBlend Blend { get; set; } = CinematicBlend.Spline;

        public static KeyDto From(CinematicKeyframe k) => new()
        {
            Time = k.Time,
            Position = new[] { k.Position.X, k.Position.Y, k.Position.Z },
            Orientation = new[] { k.Orientation.X, k.Orientation.Y, k.Orientation.Z, k.Orientation.W },
            FieldOfView = k.FieldOfView,
            Roll = k.Roll,
            Ease = k.Ease,
            Blend = k.Blend,
        };

        public CinematicKeyframe ToKeyframe() => new(
            Time,
            new Vector3(Get(Position, 0), Get(Position, 1), Get(Position, 2)),
            Normalise(new Quaternion(Get(Orientation, 0), Get(Orientation, 1), Get(Orientation, 2), Get(Orientation, 3, 1f))),
            FieldOfView, Roll, Ease, Blend);

        private static float Get(float[]? a, int i, float fallback = 0f) => a is not null && i < a.Length ? a[i] : fallback;

        /// <summary>A hand-edited file can hold a quaternion that is not unit length, and an unnormalised
        /// one skews the view matrix rather than failing.</summary>
        private static Quaternion Normalise(Quaternion q) =>
            q.LengthSquared() < 1e-8f ? Quaternion.Identity : Quaternion.Normalize(q);
    }

    private sealed class CaptureDto
    {
        public int Width { get; set; } = 1920;
        public int Height { get; set; } = 1080;
        public int Fps { get; set; } = 30;
        public string OutputDirectory { get; set; } = "";
        public string NamePrefix { get; set; } = "Shot";
        public int SuperSample { get; set; } = 1;
        public int? StartFrame { get; set; }
        public int? EndFrame { get; set; }

        public static CaptureDto From(CinematicCaptureSettings s) => new()
        {
            Width = s.Width, Height = s.Height, Fps = s.Fps,
            OutputDirectory = s.OutputDirectory, NamePrefix = s.NamePrefix,
            SuperSample = s.SuperSample, StartFrame = s.StartFrame, EndFrame = s.EndFrame,
        };

        public CinematicCaptureSettings ToSettings() =>
            new(Width, Height, Fps, OutputDirectory, NamePrefix, SuperSample, StartFrame, EndFrame);
    }
}
