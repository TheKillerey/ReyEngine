using System;
using System.Linq;
using System.Numerics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Formats.Characters;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M613: driving the character the way the game does — right-click the ground to walk, right-click the
/// dummy to attack it, Q/W/E/R to cast.
///
/// <para>Off by default and behind a toggle. The preview's left-drag orbit, middle-drag pan and gizmo
/// are how every other part of this window works, and quietly repurposing them would break the tool for
/// everyone not using this. Control mode only claims the RIGHT button, which nothing else used.</para>
///
/// <para>This is a preview harness, not a game. No pathfinding, no collision, no cooldowns, no damage.
/// Movement speed, attack range and attack speed are editable defaults rather than looked-up facts —
/// they are per-champion values that live in the spell and character records the editor does not
/// read.</para>
/// </summary>
public sealed partial class MeshPreviewViewModel
{
    private readonly CharacterController _controller = new();
    private DispatcherTimer? _controlTimer;
    private DateTime _lastControlTick;
    /// <summary>Wall-clock time until which a cast owns the animation. Without it the walk cycle would
    /// stamp over the ability on the very next frame and no cast would ever be visible.</summary>
    private DateTime _castBusyUntil = DateTime.MinValue;

    [ObservableProperty] private bool _controlMode;
    [ObservableProperty] private Vector3 _characterPosition;
    [ObservableProperty] private double _characterYaw;
    [ObservableProperty] private string _controlStatus = "";

    public double MoveSpeed
    {
        get => _controller.MoveSpeed;
        set { _controller.MoveSpeed = (float)value; OnPropertyChanged(); }
    }

    public double AttackRange
    {
        get => _controller.AttackRange;
        set { _controller.AttackRange = (float)value; OnPropertyChanged(); }
    }

    public double AttacksPerSecond
    {
        get => _controller.AttacksPerSecond;
        set { _controller.AttacksPerSecond = (float)value; OnPropertyChanged(); }
    }

    partial void OnControlModeChanged(bool value)
    {
        if (value)
        {
            _controller.Teleport(CharacterPosition);
            _lastControlTick = DateTime.UtcNow;
            _controlTimer ??= CreateTimer();
            _controlTimer.Start();
            ControlStatus = "Right-click the ground to move, the dummy to attack. Q W E R cast.";
        }
        else
        {
            _controlTimer?.Stop();
            _controller.Stop();
            ControlStatus = "";
        }
    }

    private DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) => Advance();
        return timer;
    }

    private void Advance()
    {
        var now = DateTime.UtcNow;
        float dt = (float)(now - _lastControlTick).TotalSeconds;
        _lastControlTick = now;
        // A window that was minimised or a breakpoint would otherwise hand the controller a ten-second
        // frame, and it would cross the map in one step.
        dt = Math.Clamp(dt, 0f, 0.1f);

        var tick = _controller.Tick(dt);
        CharacterPosition = _controller.Position;
        CharacterYaw = _controller.Facing;
        AdvanceArena();   // M636: next waypoint, ground height, follow camera - no-ops without an arena
        AdvancePendingCast(now);   // M637: a walk-into-range cast fires on arrival; cooldowns count down

        if (now < _castBusyUntil) return;      // a cast owns the animation until it finishes

        if (tick.StartedAttack) PlayKind(CharacterActionKind.Attack);
        else if (tick.StanceChanged)
            PlayKind(tick.Stance switch
            {
                CharacterStance.Moving => CharacterActionKind.Movement,
                CharacterStance.Attacking => CharacterActionKind.Attack,
                _ => CharacterActionKind.Idle,
            });
    }

    /// <summary>Right-click on the ground: walk there.</summary>
    public void OrderMove(Vector3 groundPoint)
    {
        if (!ControlMode) return;
        _controller.MoveTo(groundPoint);
        _castBusyUntil = DateTime.MinValue;    // a move order cancels the cast, as it does in game
    }

    /// <summary>Right-click on the dummy: walk into range and keep attacking it.</summary>
    public void OrderAttack(Vector3 target)
    {
        if (!ControlMode) return;
        _controller.Attack(target);
        _castBusyUntil = DateTime.MinValue;
    }

    // ---- M637: casts are AIMED, gated by cooldown, and walk into range like the game ----

    /// <summary>The plan the current cast resolved to; BuildEventBundle reads it for where the caster
    /// faces, where the missile flies and where the hit plays. Null means "at the dummy", the pre-M637
    /// behaviour every other caller of the bundle still gets.</summary>
    private CastPlan? _castPlan;

    private readonly DateTime[] _cooldownUntil = new DateTime[4];
    private (int Slot, Vector3? Cursor)? _pendingCast;

    [ObservableProperty] private string _cooldownStatus = "";

    /// <summary>Q/W/E/R, as 0..3. <paramref name="cursorGround"/> is the ground point under the mouse when
    /// the key went down - the only aim a player has in game - and null falls back to the dummy. The spell's
    /// authored targeting kind decides what that point means (SpellAim); its cooldown gates the cast; a
    /// plain location cast beyond range walks into range first and fires on arrival.</summary>
    public void CastAbility(int slot, Vector3? cursorGround = null)
    {
        if (!ControlMode || slot is < 0 or > 3) return;

        var row = Actions.FirstOrDefault(a =>
            a.Action.Kind == CharacterActionKind.Ability && a.Label.StartsWith("QWER"[slot]));
        if (row is null) return;

        var now = DateTime.UtcNow;
        if (now < _cooldownUntil[slot])
        {
            ControlStatus = $"{row.Label}: ready in {(_cooldownUntil[slot] - now).TotalSeconds:0.0} s";
            return;
        }

        var ability = _abilities.FirstOrDefault(a => a.Index == slot);
        var dummy = TargetDummyPosition;
        var cursor = cursorGround ?? dummy ?? CharacterPosition + Forward() * 500f;
        var plan = SpellAim.Plan(ability, CharacterPosition, cursor, dummy);

        // Out of range: walk in, then cast from there. The pending cast re-plans on arrival, so the aim is
        // re-measured from where the character actually stopped.
        if (plan.WalkTo is { } walkTo)
        {
            _pendingCast = (slot, cursorGround);
            _castBusyUntil = DateTime.MinValue;
            if (!OrderMoveOnArena(walkTo)) _controller.MoveTo(walkTo);
            ControlStatus = $"{row.Label}: {plan.Note}";
            return;
        }
        _pendingCast = null;

        _controller.Stop();
        if (!plan.IsSelfCast) _controller.FaceToward(plan.Aim);
        CharacterPosition = _controller.Position;
        CharacterYaw = _controller.Facing;
        _castPlan = plan;

        if (ability is { Cooldown: > 0f })
            _cooldownUntil[slot] = now + TimeSpan.FromSeconds(ability.Cooldown);
        RefreshCooldownStatus(now);

        if (!row.HasClip)
        {
            ControlStatus = $"{row.Label} has no animation in this skin.";
            return;
        }

        SelectedAction = null;                 // re-fire even when the same ability is cast twice
        SelectedAction = row;
        ControlStatus = $"{row.Label}: {plan.Note}"
                        + (ability is { Mana: > 0f } ? $" · {ability.Mana:0} mana" : "");
        // Hold the animation for as long as the clip runs, so the idle does not stamp over the cast.
        _castBusyUntil = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Clamp(Animation.Duration, 0.1, 10.0));
    }

    private Vector3 Forward() =>
        new(MathF.Sin(_controller.Facing), 0f, MathF.Cos(_controller.Facing));

    /// <summary>A manual order (right-click) cancels a cast that was still walking into range, as it does
    /// in game. Called by the window before it issues the order, so the walk itself - which goes through
    /// the same OrderMove - cannot cancel its own cast.</summary>
    public void CancelPendingCast() => _pendingCast = null;

    /// <summary>Q/W/E/R with seconds left, refreshed every control tick while any is running.</summary>
    private void RefreshCooldownStatus(DateTime now)
    {
        bool any = false;
        var parts = new string[4];
        for (int i = 0; i < 4; i++)
        {
            double left = (_cooldownUntil[i] - now).TotalSeconds;
            if (left > 0) { any = true; parts[i] = $"{"QWER"[i]} {left:0.0}s"; }
            else parts[i] = $"{"QWER"[i]} ready";
        }
        CooldownStatus = any ? string.Join(" · ", parts) : "";
    }

    /// <summary>Called by the control tick: the walk-into-range cast fires once the walk is over, and the
    /// cooldown readout counts down.</summary>
    private void AdvancePendingCast(DateTime now)
    {
        if (CooldownStatus.Length > 0) RefreshCooldownStatus(now);
        if (_pendingCast is not { } pending) return;
        if (_controller.Destination is not null || _waypoints.Count > 0) return;
        _pendingCast = null;
        CastAbility(pending.Slot, pending.Cursor);
    }

    private void PlayKind(CharacterActionKind kind)
    {
        var row = Actions.FirstOrDefault(a => a.Action.Kind == kind && a.HasClip);
        if (row is null) return;
        SelectedAction = null;
        SelectedAction = row;
        Animation.Loop = kind is CharacterActionKind.Movement or CharacterActionKind.Idle;
    }

    /// <summary>Put the character back at the origin facing forward.</summary>
    [RelayCommand]
    private void ResetCharacter()
    {
        _controller.Teleport(Vector3.Zero);
        CharacterPosition = Vector3.Zero;
        CharacterYaw = 0;
        _castBusyUntil = DateTime.MinValue;
    }

    /// <summary>Stop the control timer when the window closes — a DispatcherTimer holding this view
    /// model alive would keep ticking against a viewport that is gone.</summary>
    public void StopControl()
    {
        _controlTimer?.Stop();
        ControlMode = false;
    }
}
