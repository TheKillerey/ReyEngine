using System.Numerics;

namespace ReyEngine.Core.Cinematics;

/// <summary>How a segment's speed is shaped. The curve changes SPEED along the path, never the path
/// itself — easing reparameterises time, so a shot can slow into a reveal without the camera drifting off
/// the line the keyframes describe.</summary>
public enum CinematicEase
{
    Linear,
    EaseIn,
    EaseOut,
    EaseInOut,
}

/// <summary>
/// One camera keyframe. <paramref name="Time"/> is seconds from the shot's start, so keyframes carry
/// their own spacing rather than being evenly divided — a slow push followed by a fast whip is two
/// keyframes 4 s apart and two 0.3 s apart.
/// </summary>
/// <param name="Orientation">Where the camera looks. Ignored while the shot has a
/// <see cref="CinematicShot.LookAtTarget"/>, which derives orientation per sample instead.</param>
/// <param name="FieldOfView">Vertical FOV in RADIANS, matching <c>OrbitCamera.FieldOfView</c>.</param>
/// <param name="Roll">Radians about the view axis. Separate from <paramref name="Orientation"/> because a
/// look-at shot still wants roll, and there is no orientation to put it in.</param>
/// <param name="Ease">Shapes the segment that STARTS at this keyframe. The last keyframe's value is
/// unused — nothing leaves it.</param>
public sealed record CinematicKeyframe(
    float Time,
    Vector3 Position,
    Quaternion Orientation,
    float FieldOfView,
    float Roll = 0f,
    CinematicEase Ease = CinematicEase.EaseInOut);

/// <summary>What the camera is at one instant. <see cref="ViewMatrix"/> is what the renderer wants —
/// <c>ShaderPreviewSettings.SuppliedView</c> takes it directly, which is also how roll survives: an
/// orbit camera builds its view with a fixed world up and cannot express one.</summary>
public readonly record struct CinematicPose(Vector3 Position, Quaternion Orientation, float FieldOfView, float Roll)
{
    /// <summary>Unit forward, from the orientation.</summary>
    public Vector3 Forward => Vector3.Normalize(Vector3.Transform(-Vector3.UnitZ, Orientation));

    /// <summary>Unit up, with <see cref="Roll"/> applied about the view axis.</summary>
    public Vector3 Up
    {
        get
        {
            var forward = Forward;
            var up = Vector3.Transform(Vector3.UnitY, Orientation);
            if (Roll != 0f) up = Vector3.Transform(up, Quaternion.CreateFromAxisAngle(forward, Roll));
            return Vector3.Normalize(up);
        }
    }

    public Matrix4x4 ViewMatrix => Matrix4x4.CreateLookAt(Position, Position + Forward, Up);

    /// <summary>The frustum, with the near and far planes SUPPLIED rather than assumed.
    ///
    /// <para>The editor camera does not use a fixed near plane - it derives one from its distance
    /// (<c>OrbitCamera.EffectiveNear</c>) so close-up framing does not clip. Hardcoding one here would
    /// make a captured frame clip differently from the preview it was framed in, which is precisely the
    /// "it looked right in the viewport" failure this whole mode exists to avoid. The capture passes the
    /// live camera's own values so the two agree exactly.</para></summary>
    public Matrix4x4 Projection(float aspect, float near = 1f, float far = 200000f) =>
        Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, aspect <= 0f ? 1f : aspect,
            MathF.Max(0.01f, near), MathF.Max(near + 0.02f, far));
}

/// <summary>
/// A camera move, as keyframes plus the rule for getting between them.
///
/// <para><b>Positions interpolate on a Catmull-Rom spline</b> so the path is smooth through every
/// keyframe rather than a polyline with visible corners at each one. Catmull-Rom over Bézier because it
/// passes THROUGH its control points: a keyframe is a place the camera actually visits, which is what an
/// author means by placing one. The first and last segments reflect a neighbour they do not have, so a
/// two-keyframe shot is exactly a straight line at constant speed rather than one the spline has quietly
/// eased on its own.</para>
///
/// <para><b>Orientation interpolates by quaternion slerp</b>, never by Euler angles. Interpolating
/// yaw/pitch/roll separately makes a camera swing the long way round whenever a component crosses its
/// wrap point, and near the poles it gimbal-locks — both show up as a rotation that snaps mid-shot.</para>
/// </summary>
public sealed class CinematicShot
{
    private readonly List<CinematicKeyframe> _keys = new();

    public string Name { get; set; } = "Shot";

    /// <summary>When set, orientation comes from position → target per sample and every keyframe's
    /// <see cref="CinematicKeyframe.Orientation"/> is ignored. This is the "keep the nexus in frame while
    /// the camera arcs around it" case, and deriving it per sample is what keeps it exact — slerping
    /// orientations authored at the keyframes would only aim correctly AT the keyframes.</summary>
    public Vector3? LookAtTarget { get; set; }

    /// <summary>Multiplies playback speed without moving any keyframe. 2 plays the shot in half the time.</summary>
    public float SpeedScale { get; set; } = 1f;

    /// <summary>Keyframes in time order. Held sorted so sampling never has to.</summary>
    public IReadOnlyList<CinematicKeyframe> Keyframes => _keys;

    /// <summary>Seconds, after <see cref="SpeedScale"/>. Zero for an empty or single-key shot.</summary>
    public float Duration => _keys.Count < 2
        ? 0f
        : MathF.Max(0f, (_keys[^1].Time - _keys[0].Time) / MathF.Max(0.0001f, SpeedScale));

    public void Add(CinematicKeyframe key)
    {
        _keys.Add(key);
        _keys.Sort(static (a, b) => a.Time.CompareTo(b.Time));
    }

    public bool Remove(CinematicKeyframe key) => _keys.Remove(key);

    public void Clear() => _keys.Clear();

    /// <summary>
    /// The camera at <paramref name="seconds"/> from the shot's start. Times outside the shot clamp to
    /// its ends rather than extrapolating — a capture that runs one frame long must repeat the last pose,
    /// not fly the camera off the end of the spline.
    /// </summary>
    public CinematicPose Sample(float seconds)
    {
        if (_keys.Count == 0)
            return new CinematicPose(Vector3.Zero, Quaternion.Identity, MathF.PI / 4f, 0f);
        if (_keys.Count == 1) return Pose(_keys[0], _keys[0].Position);

        float t = _keys[0].Time + Math.Clamp(seconds, 0f, Duration) * MathF.Max(0.0001f, SpeedScale);

        int i = SegmentAt(t);
        var a = _keys[i];
        var b = _keys[i + 1];

        float span = b.Time - a.Time;
        float u = span <= 0f ? 0f : Math.Clamp((t - a.Time) / span, 0f, 1f);
        float e = Shape(u, a.Ease);

        // The end segments have no outer neighbour. REFLECT one rather than duplicating the endpoint:
        // duplication leaves the curve geometrically straight but not uniform in speed (a two-key move
        // becomes 50t + 150t^2 - 100t^3), so the spline imposes an ease the author did not ask for and
        // cannot switch off. Reflection gives a symmetric tangent, a two-key move is exactly linear, and
        // easing stays entirely under the Ease setting.
        var p0 = i - 1 >= 0 ? _keys[i - 1].Position : a.Position + (a.Position - b.Position);
        var p3 = i + 2 < _keys.Count ? _keys[i + 2].Position : b.Position + (b.Position - a.Position);
        var position = CatmullRom(p0, a.Position, b.Position, p3, e);

        // FOV and roll are scalars along the same eased parameter: a dolly-zoom has to stay in step with
        // the move, so they must not run on their own clock.
        float fov = Lerp(a.FieldOfView, b.FieldOfView, e);
        float roll = Lerp(a.Roll, b.Roll, e);

        var orientation = Quaternion.Slerp(Normalise(a.Orientation), Normalise(b.Orientation), e);
        return new CinematicPose(position, orientation, fov, roll) is var pose && LookAtTarget is { } target
            ? new CinematicPose(position, Aim(position, target), fov, roll)
            : pose;
    }

    /// <summary>The orientation that points a camera at <paramref name="target"/>. Exposed so the UI can
    /// author a keyframe by clicking a point, and so a look-at shot and a free shot agree on what
    /// "looking at that" means.</summary>
    public static Quaternion Aim(Vector3 from, Vector3 target)
    {
        var forward = target - from;
        if (forward.LengthSquared() < 1e-8f) return Quaternion.Identity;
        forward = Vector3.Normalize(forward);

        // Straight up or down has no unique yaw; pick a stable reference rather than producing NaN.
        var reference = MathF.Abs(Vector3.Dot(forward, Vector3.UnitY)) > 0.9995f ? Vector3.UnitZ : Vector3.UnitY;
        // cross(forward, reference), NOT cross(reference, forward). The other order yields a basis whose
        // determinant is -1 - a reflection, not a rotation - and CreateFromRotationMatrix reads that as a
        // quaternion that mirrors the scene. It looks plausible until a target fails to sit on the view axis.
        var right = Vector3.Normalize(Vector3.Cross(forward, reference));
        var up = Vector3.Cross(right, forward);

        // CreateFromRotationMatrix wants the camera's basis with -Z forward, matching CinematicPose.Forward.
        var basis = new Matrix4x4(
            right.X, right.Y, right.Z, 0f,
            up.X, up.Y, up.Z, 0f,
            -forward.X, -forward.Y, -forward.Z, 0f,
            0f, 0f, 0f, 1f);
        return Normalise(Quaternion.CreateFromRotationMatrix(basis));
    }

    /// <summary>Index of the segment containing <paramref name="t"/>, clamped into range.</summary>
    private int SegmentAt(float t)
    {
        for (int i = _keys.Count - 2; i >= 0; i--)
            if (t >= _keys[i].Time) return i;
        return 0;
    }

    private CinematicPose Pose(CinematicKeyframe key, Vector3 position) =>
        new(position,
            LookAtTarget is { } target ? Aim(position, target) : Normalise(key.Orientation),
            key.FieldOfView, key.Roll);

    private static Quaternion Normalise(Quaternion q) =>
        q.LengthSquared() < 1e-8f ? Quaternion.Identity : Quaternion.Normalize(q);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>Uniform Catmull-Rom. <paramref name="p0"/> and <paramref name="p3"/> are the neighbours
    /// that give the curve its tangents; see Sample for why a missing one is reflected rather than
    /// duplicated.</summary>
    public static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * ((2f * p1)
                       + (-p0 + p2) * t
                       + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                       + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    /// <summary>Reparameterise 0..1 by the easing curve. Every curve fixes both ends, so a keyframe is hit
    /// at exactly its authored time whatever easing is chosen.</summary>
    public static float Shape(float u, CinematicEase ease)
    {
        u = Math.Clamp(u, 0f, 1f);
        return ease switch
        {
            CinematicEase.EaseIn => u * u,
            CinematicEase.EaseOut => 1f - (1f - u) * (1f - u),
            CinematicEase.EaseInOut => u < 0.5f ? 2f * u * u : 1f - 2f * (1f - u) * (1f - u),
            _ => u,
        };
    }
}
