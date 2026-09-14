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

    /// <summary>M725: idempotent. This runs on EVERY character load, and it used to Clear() the map list
    /// each time - which drops the selection, so a user who had picked Map12 and then loaded another
    /// champion silently got Map11 back. The list only depends on the install, so rebuild it only when the
    /// install actually offers something different, and keep a selection that is still valid.</summary>
    public void ConfigureArena(ArenaHost host)
    {
        _arenaHost = host;
        var available = ArenaLoader.AvailableMaps(host.GameDirectory).ToList();
        if (!available.SequenceEqual(ArenaMaps, StringComparer.OrdinalIgnoreCase))
        {
            string? keep = SelectedArenaMap;
            ArenaMaps.Clear();
            foreach (var key in available) ArenaMaps.Add(key);
            // put the user's pick back if the new install still has it
            SelectedArenaMap = keep is not null && ArenaMaps.Contains(keep, StringComparer.OrdinalIgnoreCase) ? keep : null;
        }
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
        // M665: in GL the arena floor is the viewport's BACKDROP - a full second renderer carrying the
        // map's own materials and baked lightmaps - so it must NOT also be a prop there or it draws twice.
        // The D3D11 host has no backdrop channel, so under it the floor stays the diffuse-only prop it has
        // always been. Exactly one of the two per renderer.
        var list = new List<PropInstanceData>();
        if (UseDx11Preview && _arena is { } arena)
            list.Add(new PropInstanceData(arena.Dx11Geometry, Matrix4x4.Identity));
        if (DummyProps is { } dummy) list.AddRange(dummy.Instances);
        SceneProps = list.Count > 0 ? new PropRenderSet(list) : null;
    }

    /// <summary>M725: true when the backdrop is configured but the active renderer cannot show it the way
    /// the card's controls describe - the D3D11 host draws it as diffuse-only geometry, with no baked
    /// light, no blend layers and no Light.dat. Surfaced so the card can say so instead of looking broken.</summary>
    public bool BackdropIsDiffuseOnly => UseDx11Preview && HasBackground && _arena is null;

    /// <summary>
    /// M665: put the backdrop at the origin while an arena owns it.
    ///
    /// <para>The champion-preview backdrop defaults to (-6400, -60, 2000) rotated 180 - numbers chosen to
    /// place a champion nicely inside an NVR room. An arena's navgrid, spawn and click-to-move all speak
    /// world coordinates, so the floor has to be exactly where the mapgeo says it is or the character
    /// walks on nothing.</para>
    /// </summary>
    private void SetArenaBackdrop(Services.MapPreviewBackground? bg)
    {
        if (bg is not null)
        {
            BackgroundOffsetX = 0; BackgroundOffsetY = 0; BackgroundOffsetZ = 0; BackgroundRotation = 0;
        }
        else if (_arenaOwnsBackdrop)
        {
            ResetBackgroundOffset();   // hand the NVR backdrop its own placement back
        }
        _arenaOwnsBackdrop = bg is not null;
        SetBackground(bg);
        BackgroundVisible = true;
    }

    /// <summary>M665: an arena is using the backdrop as its floor, so nothing else may replace it. The
    /// champion load path streams an NVR backdrop in (or clears it) after every Show, which would
    /// otherwise delete the map out from under a character standing on it.</summary>
    public bool ArenaOwnsBackdrop => _arenaOwnsBackdrop;

    private bool _arenaOwnsBackdrop;

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

            // M667: the apply is breadcrumbed step by step. A session log ended exactly here - after the
            // loader's own lines and before anything else - with the process gone and no crash.log, which
            // means a native fault rather than a managed exception. Every data-level cause was measured
            // and ruled out (mesh channels, index range, submesh ranges, and all 17 textures sized
            // correctly), so what is left is the apply and the GL upload it triggers. These say which.
            LogDx11?.Invoke("Arena", $"{key}: applying scene - props");
            _arena = scene;
            _waypoints.Clear();
            RebuildSceneProps();
            LogDx11?.Invoke("Arena", $"{key}: applying scene - backdrop "
                + $"({scene.Background.Mesh.VertexCount:n0} verts, {scene.Background.Mesh.SubMeshes.Count} submeshes)");
            SetArenaBackdrop(scene.Background);   // M665: the map draws as the backdrop, before the champion
            LogDx11?.Invoke("Arena", $"{key}: backdrop handed to the viewport");

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
            LogDx11?.Invoke("Arena", $"{key}: dummy placed, enabling control");
            ControlMode = true;
            FocusPoint = scene.Spawn;
            LogDx11?.Invoke("Arena", $"{key}: applied.");

            ArenaStatus = $"{scene.MapKey}: {scene.GroupsDrawn} groups, "
                          + (scene.HasNavGrid ? "navgrid movement" : "flat floor, no navgrid")
                          + (scene.LightmappedGroups > 0 ? $", {scene.LightmappedGroups} baked-lit" : ", no baked light")
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

    /// <summary>M725: host hook - reload and re-attach the configured Dominion / Twisted Treeline backdrop.
    /// The view model cannot do it itself: the loader, the settings and the cache all live on the host.</summary>
    public Func<Task>? ReapplyBackdrop;

    [RelayCommand]
    private async Task UnloadArena()
    {
        _arena = null;
        _waypoints.Clear();
        RebuildSceneProps();
        SetArenaBackdrop(null);   // M665: and give the backdrop back to whatever owned it
        ArenaStatus = "";
        OnPropertyChanged(nameof(HasArena));
        OnPropertyChanged(nameof(ArenaName));

        // M725: "whatever owned it" was nothing. SetArenaBackdrop(null) clears the mesh and the champion
        // load path is the only thing that ever streams the NVR room back in, so unloading an arena left the
        // viewer standing in empty space with no visible way to get an environment back until the next
        // character was loaded. Ask the host to put the configured backdrop back.
        if (ReapplyBackdrop is { } reapply)
        {
            try { await reapply(); }
            catch (Exception ex) { LogDx11?.Invoke("Arena", $"backdrop could not be restored: {ex.Message}"); }
        }
    }

    /// <summary>Where the viewport should look. Set on spawn and, while following, every control tick.</summary>
    [ObservableProperty] private Vector3? _focusPoint;

    // ---- M639: the cast-range ring ----

    /// <summary>The ring drawn around the character, as a line list both viewports take; null when none
    /// is shown. Rebuilt every control tick while shown, because the character moves under it.</summary>
    [ObservableProperty] private float[]? _rangeRingLines;

    /// <summary>The slot whose range is pinned on screen (0..3), or null. Toggled from the arena card.</summary>
    [ObservableProperty] private int? _rangeRingSlot;

    /// <summary>A cast shows its ring briefly on its own - long enough to see where the range was.</summary>
    private DateTime _rangeRingUntil = DateTime.MinValue;
    private int _rangeRingCastSlot = -1;

    public static readonly TimeSpan CastRingHold = TimeSpan.FromSeconds(1.5);

    [RelayCommand]
    private void ToggleRangeRing(string slot)
    {
        int s = slot switch { "Q" => 0, "W" => 1, "E" => 2, "R" => 3, _ => -1 };
        if (s < 0) return;
        RangeRingSlot = RangeRingSlot == s ? null : s;
        RefreshRangeRing();
    }

    partial void OnRangeRingSlotChanged(int? value) => RefreshRangeRing();

    public bool IsRangeRingQ => RangeRingSlot == 0;
    public bool IsRangeRingW => RangeRingSlot == 1;
    public bool IsRangeRingE => RangeRingSlot == 2;
    public bool IsRangeRingR => RangeRingSlot == 3;

    /// <summary>Called by a cast: show that slot's ring for <see cref="CastRingHold"/>.</summary>
    private void ShowRangeRingFor(int slot)
    {
        _rangeRingCastSlot = slot;
        _rangeRingUntil = DateTime.UtcNow + CastRingHold;
        RefreshRangeRing();
    }

    /// <summary>The range the ring shows for a slot: the DISPLAY range the game draws (Ezreal Q 1150,
    /// not the 1200 it checks against), and nothing for a range authored as unbounded - a 25000-unit ring
    /// would be the whole map.</summary>
    public float? RangeRingRadiusFor(int slot)
    {
        var ability = _abilities.FirstOrDefault(a => a.Index == slot);
        if (ability is null || ability.IsUnboundedRange || ability.CastRangeDisplay <= 0f) return null;
        return ability.CastRangeDisplay;
    }

    /// <summary>Rebuild the ring for the pinned slot, else for the slot just cast while its hold lasts.</summary>
    public void RefreshRangeRing()
    {
        int? slot = RangeRingSlot;
        if (slot is null && _rangeRingCastSlot >= 0)
        {
            if (DateTime.UtcNow <= _rangeRingUntil) slot = _rangeRingCastSlot;
            else _rangeRingCastSlot = -1;
        }
        OnPropertyChanged(nameof(IsRangeRingQ));
        OnPropertyChanged(nameof(IsRangeRingW));
        OnPropertyChanged(nameof(IsRangeRingE));
        OnPropertyChanged(nameof(IsRangeRingR));

        if (slot is not { } s || RangeRingRadiusFor(s) is not { } radius)
        {
            if (RangeRingLines is not null) RangeRingLines = null;
            return;
        }

        var nav = _arena?.Nav;
        var centre = CharacterPosition;
        int segments = Math.Clamp((int)(radius / 12f), 48, 160);
        RangeRingLines = Rendering.ViewportMeshRenderer.BuildCircleLines(centre, radius, segments,
            nav is null ? null : p => NavGridPath.GroundHeight(nav, p));
    }

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
