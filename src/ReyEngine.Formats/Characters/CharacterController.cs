using System.Numerics;

namespace ReyEngine.Formats.Characters;

public enum CharacterStance
{
    Idle,
    Moving,
    Attacking,
}

/// <summary>What changed during one <see cref="CharacterController.Tick"/>.</summary>
/// <param name="Stance">What the character is doing now — the caller maps this onto a clip.</param>
/// <param name="StanceChanged">True on the frame the stance changed, so the caller knows to switch clip
/// rather than restarting the current one every frame.</param>
/// <param name="StartedAttack">True on the frame a new attack begins. Attacks repeat on a cadence, and
/// each one is its own animation, so this fires once per swing rather than once per approach.</param>
public readonly record struct CharacterTick(CharacterStance Stance, bool StanceChanged, bool StartedAttack);

/// <summary>
/// M613: driving a character the way the game does — click the ground to walk, click a target to attack.
///
/// <para>Pure movement logic with no rendering in it, because the interesting failures are all in the
/// logic: walking past the destination on a long frame, spinning to face a target that is under your
/// feet, or attacking at a cadence that drifts. Each of those is a one-line test here and a
/// twenty-minute hunt in a viewport.</para>
///
/// <para>Deliberately NOT a game simulation. There is no pathfinding, no collision, no cooldowns and no
/// damage — the character walks in a straight line and swings on a timer, which is what a preview needs
/// to show an animation set in context.</para>
/// </summary>
public sealed class CharacterController
{
    /// <summary>Units per second. League's base movement sits around 325–355; the exact value is per
    /// champion and is not in the data the editor reads, so this is a sane default rather than a fact.</summary>
    public float MoveSpeed { get; set; } = 340f;

    /// <summary>How close to a target the character walks before swinging. Melee champions sit near 125,
    /// marksmen near 550. Same caveat: a default, not a lookup.</summary>
    public float AttackRange { get; set; } = 175f;

    public float AttacksPerSecond { get; set; } = 0.65f;

    /// <summary>Radians per second. Turning instantly reads as a snap on every click.</summary>
    public float TurnSpeed { get; set; } = 14f;

    /// <summary>How close counts as arrived. Without a threshold a character oscillates around its
    /// destination forever, one step over and one step back.</summary>
    public float ArriveEpsilon { get; set; } = 4f;

    public Vector3 Position { get; private set; }
    /// <summary>Yaw in radians, 0 = facing +Z.
    ///
    /// <para>Measured in the viewport, not assumed: with the -Z convention every left/right order aimed
    /// correctly and every forward/back one aimed exactly backwards, which is only possible if the sign
    /// on Z is wrong. A general mirror would have broken both axes.</para></summary>
    public float Facing { get; private set; }
    public CharacterStance Stance { get; private set; } = CharacterStance.Idle;

    public Vector3? Destination { get; private set; }
    public Vector3? Target { get; private set; }

    private float _attackCooldown;

    public void Teleport(Vector3 position, float facing = 0f)
    {
        Position = position;
        Facing = facing;
        Stop();
    }

    /// <summary>Walk to a point on the ground.</summary>
    public void MoveTo(Vector3 point)
    {
        Destination = point;
        Target = null;
        _attackCooldown = 0f;
    }

    /// <summary>M636: put the character on the ground the arena reports under it. Movement is flat - Step
    /// never changes Y - so a walker on a map with real terrain needs its height re-read every tick, and
    /// this is the one setter that changes Y without cancelling the order the way Teleport does.</summary>
    public void SetGroundHeight(float y) => Position = new Vector3(Position.X, y, Position.Z);

    /// <summary>M637: turn to face a point at once, the way a cast snaps a champion toward its aim in
    /// game. Keeps whatever order is pending; a point underfoot leaves the facing alone.</summary>
    public void FaceToward(Vector3 point)
    {
        var flat = Flatten(point - Position);
        if (flat.LengthSquared() < 1e-6f) return;
        Facing = Wrap(MathF.Atan2(flat.X, flat.Z));
    }

    /// <summary>Walk into range of a target and keep attacking it.</summary>
    public void Attack(Vector3 target)
    {
        Target = target;
        Destination = null;
        // No cooldown reset: a fresh order should swing as soon as it is in range, and resetting here
        // would let rapid re-clicks fire faster than the attack speed allows.
    }

    public void Stop()
    {
        Destination = null;
        Target = null;
        Stance = CharacterStance.Idle;
        _attackCooldown = 0f;
    }

    public CharacterTick Tick(float seconds)
    {
        if (seconds <= 0f) return new CharacterTick(Stance, false, false);

        var previous = Stance;
        bool swung = false;

        if (Target is { } target)
        {
            var flat = Flatten(target - Position);
            float distance = flat.Length();

            if (distance > AttackRange)
            {
                Step(flat, distance, seconds);
                Stance = CharacterStance.Moving;
                _attackCooldown = MathF.Max(0f, _attackCooldown - seconds);
            }
            else
            {
                // In range: hold position, turn to face, and swing on the cadence.
                TurnToward(flat, seconds);
                Stance = CharacterStance.Attacking;
                _attackCooldown -= seconds;
                if (_attackCooldown <= 0f)
                {
                    swung = true;
                    _attackCooldown += MathF.Max(0.05f, 1f / MathF.Max(0.01f, AttacksPerSecond));
                    // += rather than = keeps the cadence from drifting slower on long frames.
                    if (_attackCooldown <= 0f) _attackCooldown = 1f / MathF.Max(0.01f, AttacksPerSecond);
                }
            }
        }
        else if (Destination is { } destination)
        {
            var flat = Flatten(destination - Position);
            float distance = flat.Length();

            if (distance <= ArriveEpsilon)
            {
                Position = new Vector3(destination.X, Position.Y, destination.Z);
                Destination = null;
                Stance = CharacterStance.Idle;
            }
            else
            {
                Step(flat, distance, seconds);
                Stance = CharacterStance.Moving;
            }
        }
        else
        {
            Stance = CharacterStance.Idle;
        }

        return new CharacterTick(Stance, Stance != previous, swung);
    }

    /// <summary>One step toward a flat direction, never overshooting the remaining distance.</summary>
    private void Step(Vector3 flat, float distance, float seconds)
    {
        TurnToward(flat, seconds);
        float travel = MathF.Min(MoveSpeed * seconds, distance);
        Position += flat / distance * travel;
    }

    private void TurnToward(Vector3 flat, float seconds)
    {
        // A target directly underfoot has no direction to face; keeping the current one is the only
        // answer that does not spin the model.
        if (flat.LengthSquared() < 1e-6f) return;

        float wanted = MathF.Atan2(flat.X, flat.Z);
        float delta = Wrap(wanted - Facing);
        float step = TurnSpeed * seconds;
        Facing = Wrap(MathF.Abs(delta) <= step ? wanted : Facing + MathF.Sign(delta) * step);
    }

    /// <summary>Ground movement only: a click on sloped terrain must not lift or sink the character.</summary>
    private static Vector3 Flatten(Vector3 v) => new(v.X, 0f, v.Z);

    /// <summary>To (-pi, pi], so turning always takes the short way round.</summary>
    private static float Wrap(float radians)
    {
        const float twoPi = MathF.PI * 2f;
        radians %= twoPi;
        if (radians > MathF.PI) radians -= twoPi;
        if (radians <= -MathF.PI) radians += twoPi;
        return radians;
    }
}
