using System.Numerics;

namespace ReyEngine.Formats.Characters;

/// <summary>
/// M893: what a <c>MissileSpecification</c> says about HOW a missile flies, beyond its speed: where it leaves the caster, the heights
/// it starts and ends at, the height solver, the vertical facing and the behaviour list. Every value is read off the record; a class
/// this reader does not know comes back as its hash ("0x72f86c81") so a new class shows up instead of disappearing.
///
/// <para>MEANING, and where it comes from. The bin only names classes and fields; the formulas below are the ones LTK Manager's VFX engine
/// implements (<c>engine/model/missileHeight.ts</c>, <c>missileSpec.ts</c>), which the user holds as the reference. They are not independently
/// verified against the game client - the report says so. A field whose meaning is not established (<c>mTracksTarget</c>,
/// <c>mStartBoneSkinOverrides</c>, the behaviour list beyond the classes named below) is stored and never read by the player.</para>
/// </summary>
public sealed record MissileFlight(
    /// <summary>The movement component's class ("FixedSpeedMovement"), or its hash.</summary>
    string MovementClass,
    /// <summary><c>mStartBoneName</c> of the movement component: the CASTER bone the missile leaves from. Null when absent or empty.</summary>
    string? StartBoneName,
    /// <summary><c>mTargetHeightAugment</c>, raw.</summary>
    float? TargetHeightAugment,
    /// <summary><c>mOffsetInitialTargetHeight</c>, raw.</summary>
    float? OffsetInitialTargetHeight,
    /// <summary><c>mUseHeightOffsetAtEnd</c>, raw.</summary>
    bool? UseHeightOffsetAtEnd,
    /// <summary><c>mTracksTarget</c>, raw. Stored, not used: how a tracking missile steers was not established.</summary>
    bool? TracksTarget,
    /// <summary>The <c>verticalFacing</c> class ("VeritcalFacingMatchVelocity" is Riot's own spelling), or null.</summary>
    string? VerticalFacing,
    /// <summary>The <c>heightSolver</c> class, or null when the spec authors none.</summary>
    string? HeightSolver,
    /// <summary>Every float field of the solver by name (or hash when the name is unknown): <c>mGravity</c>, <c>mAmplitude</c>, ...</summary>
    IReadOnlyDictionary<string, float> HeightSolverFields,
    /// <summary>The <c>behaviors</c> list, one class name (or hash) per element, in order.</summary>
    IReadOnlyList<string> Behaviors)
{
    // ---- the AcceleratingMovement fields (raw; LTK Manager `accelerating`): a missile that leaves slowly and speeds up. The reader's old stand-in was the INITIAL speed
    // alone, which made Ahri Q's return missile (initial 60 u/s, +1900 u/s^2, max 2600) an eight-second crawl.
    public float? InitialSpeed { get; init; }
    public float? Acceleration { get; init; }
    public float? MinSpeed { get; init; }
    public float? MaxSpeed { get; init; }

    /// <summary>Seconds an AcceleratingMovement takes to cover <paramref name="distance"/>: the speed starts at <c>mInitialSpeed</c>, grows by <c>mAcceleration</c> per second and is clamped
    /// to <c>mMinSpeed</c>..<c>mMaxSpeed</c>. Null for any other movement class, for a spec without a positive speed, and when 30 s would not be enough.</summary>
    public float? AcceleratingSeconds(float distance)
    {
        if (MovementClass != "AcceleratingMovement" || InitialSpeed is not { } v0 || Acceleration is not { } rate || distance <= 0f) return null;
        float least = MinSpeed ?? 0f, most = MaxSpeed is > 0f ? MaxSpeed.Value : float.MaxValue;
        const float dt = 1f / 120f;
        float speed = v0, covered = 0f;
        for (int i = 0; i < 30 * 120; i++)
        {
            speed = Math.Clamp(speed, least, most);
            if (speed <= 0f && rate <= 0f) return null;
            covered += speed * dt;
            if (covered >= distance) return (i + 1) * dt;
            speed += rate * dt;
        }
        return null;
    }

    public const string ReturnBehavior = "ReturnToCasterOnMovementComplete";
    public const string DestroyOnHitBehavior = "DestroyOnHit";
    public const string MatchVelocityFacing = "VeritcalFacingMatchVelocity";

    public bool HasBehavior(string name) => Behaviors.Contains(name, StringComparer.Ordinal);

    /// <summary>The missile turns around at the end of its flight by its own behaviour (authored; 1 spec in the 16.22 corpus).</summary>
    public bool ReturnsToCaster => HasBehavior(ReturnBehavior);

    /// <summary>The missile is destroyed by the first unit it hits (<c>DestroyOnHit</c>; 43 specs). Null when the list does not say - CastOnHit
    /// alone does not: the spell script decides there.</summary>
    public bool? DestroyedOnHit => HasBehavior(DestroyOnHitBehavior) ? true : null;

    /// <summary>The solvers whose class draws a curve rather than the straight blend.</summary>
    public bool IsArc => HeightSolver is "GravityHeightSolver" or "SinusoidalHeightSolver" or "CurveTheDifferenceHeightSolver";

    public bool HasHeightData => StartBoneName is not null || TargetHeightAugment is not null || OffsetInitialTargetHeight is not null
                                 || UseHeightOffsetAtEnd is not null || HeightSolver is not null;

    /// <summary>The end height above the target's own ground height: the movement's <c>mOffsetInitialTargetHeight</c> plus, with
    /// <c>mUseHeightOffsetAtEnd</c>, the launch height and <c>mTargetHeightAugment</c> (LTK Manager <c>flightOf</c> / <c>planFlight</c>).</summary>
    public float EndHeightAboveGround(float launchHeight)
    {
        bool atEnd = UseHeightOffsetAtEnd ?? false;
        return (atEnd ? launchHeight : 0f) + (OffsetInitialTargetHeight ?? 0f) + (atEnd ? TargetHeightAugment ?? 0f : 0f);
    }

    /// <summary>The height curve this missile flies. Null for a spec with no solver and for a solver that is not implemented (FollowTerrain: the
    /// ground is flat in the preview, so the straight line stays; an unknown class is stored, not guessed).</summary>
    public MissileHeightPath? Path(float seconds) => HeightSolver switch
    {
        "BlendedLinearHeightSolver" => new MissileHeightPath(MissileHeightKind.BlendedLinear, seconds),
        "GravityHeightSolver" when Field("mGravity") is { } g => new MissileHeightPath(MissileHeightKind.Gravity, seconds) { Gravity = g },
        "SinusoidalHeightSolver" => new MissileHeightPath(MissileHeightKind.Sinusoidal, seconds)
        {
            Amplitude = Field("mAmplitude") ?? 0f, Periods = Field("mNumberOfPeriods") ?? 0f,
            VerticalOffset = Field("mVerticalOffset") ?? 0f, Phase = Field("PhaseShift") ?? 0f,
        },
        "CurveTheDifferenceHeightSolver" => new MissileHeightPath(MissileHeightKind.CurveTheDifference, seconds)
        { InitialTargetHeightOffset = Field("mInitialTargetHeightOffset") ?? 0f },
        _ => null,
    };

    private float? Field(string name) => HeightSolverFields.TryGetValue(name, out var v) ? v : null;
}

public enum MissileHeightKind { BlendedLinear, Gravity, Sinusoidal, CurveTheDifference }

/// <summary>
/// M893: the height of a missile along its flight, one formula per solver class (LTK Manager <c>missileHeight.ts</c>). <c>t</c> is the
/// fraction of the flight, 0..1. Pure; the player samples it every frame.
/// </summary>
public sealed record MissileHeightPath(MissileHeightKind Kind, float Seconds)
{
    public float Gravity { get; init; }
    public float Amplitude { get; init; }
    public float Periods { get; init; }
    public float VerticalOffset { get; init; }
    public float Phase { get; init; }
    public float InitialTargetHeightOffset { get; init; }

    /// <summary>True for the classes that bend the path; the blended solver only eases the climb.</summary>
    public bool IsArc => Kind != MissileHeightKind.BlendedLinear;

    public float HeightAt(float t, float startY, float endY)
    {
        t = Math.Clamp(t, 0f, 1f);
        switch (Kind)
        {
            case MissileHeightKind.BlendedLinear:
                return startY + (endY - startY) * MathF.Pow(t, 0.75f);
            case MissileHeightKind.Gravity:
            {
                if (Seconds <= 1e-4f) return startY + (endY - startY) * t;
                float time = Seconds * t;
                float rise = (endY - startY) / Seconds + 0.5f * Gravity * Seconds;
                return startY + rise * time - 0.5f * Gravity * time * time;
            }
            case MissileHeightKind.Sinusoidal:
            {
                float periods = Periods != 0f ? Periods : 1f;
                float wave = MathF.Sin((periods * t + Phase) * MathF.PI * 2f) * Amplitude;
                return wave + VerticalOffset + (1f - t) * startY + t * endY;
            }
            default:
                return (1f - t) * (t * InitialTargetHeightOffset + (1f - t) * startY) + t * (t * endY + (1f - t) * startY);
        }
    }

    /// <summary>The position at fraction <paramref name="t"/> of a flight from <paramref name="from"/> to <paramref name="to"/>: the horizontal line, the solver's height.</summary>
    public Vector3 PositionAt(float t, Vector3 from, Vector3 to)
    {
        var p = Vector3.Lerp(from, to, Math.Clamp(t, 0f, 1f));
        p.Y = HeightAt(t, from.Y, to.Y);
        return p;
    }

    /// <summary>The direction of travel at <paramref name="t"/>, from a central difference; the zero vector when the path is a point.</summary>
    public Vector3 DirectionAt(float t, Vector3 from, Vector3 to)
    {
        const float h = 0.01f;
        return PositionAt(MathF.Min(1f, t + h), from, to) - PositionAt(MathF.Max(0f, t - h), from, to);
    }
}
