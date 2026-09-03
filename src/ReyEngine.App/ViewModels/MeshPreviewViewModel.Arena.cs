using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.App.Services;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M636: the ARENA - play the character on a shipped map of the user's choosing.
///
/// <para>What this is: the map's geometry as a floor beside the champion, its navigation grid deciding
/// where a right-click may send him and how high the ground is under his feet, an A* route along that
/// grid at the champion's own authored move speed, the target dummy standing on the same floor, and every
/// spell this window already casts - now with the map under it.</para>
///
/// <para>What this is NOT, said plainly because the request was "a full playable character like in
/// game": Riot's ability logic is not in the data. <c>Scripts.wad.client</c> holds 53,908 files, and the
/// spell scripts in it are Lua 5.1 bytecode STUBS - Aatrox's Q is 60 bytes, an empty chunk; his R is 566
/// bytes of constant tags (NotSingleTargetSpell, IsDamagingSpell, a buff icon path). Damage, dashes,
/// buffs, crowd control and passives are compiled into the game client. What the bin does author - move
/// speed, attack range and speed, cast ranges, targeting kinds, cooldowns, mana, cast frames, missile
/// speeds - is what the arena uses, and nothing here is invented to stand in for the rest.</para>
/// </summary>
public sealed partial class MeshPreviewViewModel
{
    // ---- host wiring (the main window owns the install, the hash database and the readers) ----

    /// <summary>Everything the loader needs from the host, handed over once per load of the window.</summary>
    public sealed record ArenaHost(
        string? GameDirectory,
        ReyEngine.Core.Hashing.IHashResolver Resolver,
        Func<uint, string?> ResolveBinName,
        Func<ulong, string?> ResolveWadPath);

    private ArenaHost? _arenaHost;

    public void ConfigureArena(ArenaHost host)
    {
        _arenaHost = host;
        ArenaMaps.Clear();
        foreach (var key in ArenaLoader.AvailableMaps(host.GameDirectory)) ArenaMaps.Add(key);
        SelectedArenaMap ??= ArenaMaps.FirstOrDefault(m => m.Equals("Map11", StringComparison.OrdinalIgnoreCase))
                             ?? ArenaMaps.FirstOrDefault();
        OnPropertyChanged(nameof(HasArenaMaps));
    }

    /// <summary>M636: the champion's own numbers, off its CharacterRecord (ChampionStatsReader). Applied to
    /// the controller so the arena walks and swings at the authored rates rather than the defaults.</summary>
    public void SetStats(ChampionStats? stats)
    {
        _championStats = stats;
        if (stats is null) return;
        if (stats.MoveSpeed > 0f) MoveSpeed = stats.MoveSpeed;
        if (stats.AttackRange > 0f) AttackRange = stats.AttackRange;
        if (stats.AttackSpeed > 0f) AttacksPerSecond = stats.AttackSpeed;
        OnPropertyChanged(nameof(StatsSummary));
    }

    private ChampionStats? _championStats;

    public string StatsSummary => _championStats is null
        ? "no champion record - editor defaults"
        : $"MS {_championStats.MoveSpeed:0}  range {_championStats.AttackRange:0}  AS {_championStats.AttackSpeed:0.###}  HP {_championStats.BaseHealth:0}";

    // ---- the arena itself ----

    public ObservableCollection<string> ArenaMaps { get; } = new();
    public bool HasArenaMaps => ArenaMaps.Count > 0;

    [ObservableProperty] private string? _selectedArenaMap;
    [ObservableProperty] private string _arenaStatus = "";
    [ObservableProperty] private bool _arenaLoading;
    [ObservableProperty] private bool _arenaFollowCamera = true;

    private ArenaScene? _arena;
    public bool HasArena => _arena is not null;
    public string ArenaName => _arena?.MapKey ?? "";

    /// <summary>What the viewport draws as props: the arena floor (when loaded) and the target dummy.
    /// One set, because both renderers take exactly one PropRenderSet.</summary>
    [ObservableProperty] private PropRenderSet? _sceneProps;

    partial void OnDummyPropsChanged(PropRenderSet? value) => RebuildSceneProps();

    private void RebuildSceneProps()
    {
        var list = new List<PropInstanceData>();
        if (_arena is { } arena) list.Add(new PropInstanceData(arena.Geometry, Matrix4x4.Identity));
        if (DummyProps is { } dummy) list.AddRange(dummy.Instances);
        SceneProps = list.Count > 0 ? new PropRenderSet(list) : null;
    }

    [RelayCommand]
    private async Task LoadArenaAsync()
    {
        if (_arenaHost is not { } host || SelectedArenaMap is not { Length: > 0 } key || ArenaLoading) return;
        string? wad = ArenaLoader.WadPathFor(host.GameDirectory, key);
        if (wad is null) { ArenaStatus = $"{key}.wad.client is not in the game install."; return; }

        ArenaLoading = true;
        ArenaStatus = $"Loading {key}…";
        try
        {
            var lines = new List<string>();
            var scene = await Task.Run(() => ArenaLoader.Load(wad, host.Resolver, host.ResolveBinName,
                host.ResolveWadPath, l => lines.Add(l)));
            foreach (var l in lines) LogDx11?.Invoke("Arena", l);

            _arena = scene;
            _waypoints.Clear();
            RebuildSceneProps();

            // The champion on the spawn, the dummy 400 units into the map on walkable ground, the camera
            // on him. Control mode on: the arena is for playing, and its status line says how.
            _controller.Teleport(scene.Spawn, 0f);
            CharacterPosition = scene.Spawn;
            CharacterYaw = 0;
            var dummyAt = scene.Spawn + new Vector3(400f, 0f, 0f);
            if (scene.Nav is { } nav)
            {
                dummyAt = NavGridPath.SnapToWalkable(nav, dummyAt, maxRadiusCells: 20);
                dummyAt.Y = NavGridPath.GroundHeight(nav, dummyAt);
            }
            TargetDummyEnabled = true;
            DummyX = dummyAt.X; DummyY = dummyAt.Y; DummyZ = dummyAt.Z;
            ControlMode = true;
            FocusPoint = scene.Spawn;

            ArenaStatus = $"{scene.MapKey}: {scene.GroupsDrawn} groups, "
                          + (scene.HasNavGrid ? "navgrid movement" : "flat floor, no navgrid")
                          + (scene.TexturesMissing > 0 ? $", {scene.TexturesMissing} texture(s) missing" : "")
                          + ". Right-click to move, the dummy to attack, Q W E R to cast.";
            OnPropertyChanged(nameof(HasArena));
            OnPropertyChanged(nameof(ArenaName));
        }
        catch (Exception ex)
        {
            ArenaStatus = $"{key}: {ex.Message}";
            LogDx11?.Invoke("Arena", $"{key}: {ex}");
        }
        finally { ArenaLoading = false; }
    }

    [RelayCommand]
    private void UnloadArena()
    {
        _arena = null;
        _waypoints.Clear();
        RebuildSceneProps();
        ArenaStatus = "";
        OnPropertyChanged(nameof(HasArena));
        OnPropertyChanged(nameof(ArenaName));
    }

    /// <summary>Where the viewport should look. Set on spawn and, while following, every control tick.</summary>
    [ObservableProperty] private Vector3? _focusPoint;

    // ---- movement on the grid ----

    private readonly Queue<Vector3> _waypoints = new();

    /// <summary>An order that goes through the navigation grid when there is one: the point is snapped to
    /// walkable ground, the route is A* over the lattice, and the controller is fed one waypoint at a
    /// time. Without a grid it is the plain straight-line order this window always had.</summary>
    public bool OrderMoveOnArena(Vector3 groundPoint)
    {
        if (_arena is not { Nav: { } nav }) return false;
        var path = NavGridPath.FindPath(nav, CharacterPosition, groundPoint);
        _waypoints.Clear();
        if (path.Count == 0)
        {
            ControlStatus = "No route there - that ground is not walkable.";
            return true;
        }
        foreach (var p in path.Skip(1)) _waypoints.Enqueue(p);
        if (_waypoints.Count == 0) _waypoints.Enqueue(path[0]);
        OrderMove(_waypoints.Dequeue());
        ControlStatus = $"Moving: {path.Count - 1} leg(s).";
        return true;
    }

    /// <summary>Called by the control tick: hands the controller its next leg when it has arrived, keeps
    /// the character on the arena's ground, and keeps the camera on him.</summary>
    private void AdvanceArena()
    {
        if (_arena is not { } arena) return;

        if (_controller.Destination is null && _controller.Target is null && _waypoints.Count > 0)
            _controller.MoveTo(_waypoints.Dequeue());

        if (arena.Nav is { } nav)
        {
            float y = NavGridPath.GroundHeight(nav, _controller.Position);
            if (MathF.Abs(y - _controller.Position.Y) > 0.01f)
            {
                _controller.SetGroundHeight(y);
                CharacterPosition = _controller.Position;
            }
        }

        if (ArenaFollowCamera && _controller.Stance != CharacterStance.Idle) FocusPoint = CharacterPosition;
    }

    /// <summary>The ground under a pick ray on the arena: marched against the navgrid's height field, so a
    /// click on a hill lands on the hill rather than on the plane the character happens to stand on. False
    /// when there is no arena or no grid, in which case the caller falls back to that plane.</summary>
    public bool TryArenaGroundHit(Vector3 origin, Vector3 dir, out Vector3 hit)
    {
        hit = default;
        if (_arena is not { Nav: { } nav }) return false;
        if (dir.LengthSquared() < 1e-8f) return false;
        dir = Vector3.Normalize(dir);

        const float Step = 20f;
        const float MaxDistance = 60000f;
        float previous = 0f;
        for (float t = Step; t <= MaxDistance; t += Step)
        {
            var p = origin + dir * t;
            float ground = NavGridPath.GroundHeight(nav, p);
            if (p.Y <= ground)
            {
                // Bisect the last step so the hit sits on the surface, not up to 20 units below it.
                float lo = previous, hi = t;
                for (int i = 0; i < 12; i++)
                {
                    float mid = (lo + hi) * 0.5f;
                    var q = origin + dir * mid;
                    if (q.Y <= NavGridPath.GroundHeight(nav, q)) hi = mid; else lo = mid;
                }
                hit = origin + dir * hi;
                hit.Y = NavGridPath.GroundHeight(nav, hit);
                return true;
            }
            previous = t;
        }
        return false;
    }
}
