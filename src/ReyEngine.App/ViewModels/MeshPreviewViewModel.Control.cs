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

    /// <summary>Q/W/E/R, as 0..3. Casting stops the character where it stands, which is what the vast
    /// majority of League casts do and is the only behaviour derivable without the spell records.</summary>
    public void CastAbility(int slot)
    {
        if (!ControlMode || slot is < 0 or > 3) return;

        var row = Actions.FirstOrDefault(a =>
            a.Action.Kind == CharacterActionKind.Ability && a.Label.StartsWith("QWER"[slot]));
        if (row is null) return;

        _controller.Stop();
        CharacterPosition = _controller.Position;

        if (!row.HasClip)
        {
            ControlStatus = $"{row.Label} has no animation in this skin.";
            return;
        }

        SelectedAction = null;                 // re-fire even when the same ability is cast twice
        SelectedAction = row;
        ControlStatus = row.Label;
        // Hold the animation for as long as the clip runs, so the idle does not stamp over the cast.
        _castBusyUntil = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Clamp(Animation.Duration, 0.1, 10.0));
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
