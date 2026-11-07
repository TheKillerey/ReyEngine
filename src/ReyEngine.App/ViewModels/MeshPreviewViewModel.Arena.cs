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
/// M636: the ARENA - the character on a shipped map, with the map's navigation grid under him.
///
/// <para>M868: the Character window no longer offers a map picker or manual play. Its one environment is
/// Riot's Summoner's Rift (Map11), loaded automatically the first time a champion is shown, kept for
/// the rest of the window's life, and framed on a fixed stage (<see cref="ArenaStage"/>). What the
/// arena provides is unchanged: the map's geometry as a floor, its navgrid deciding where a unit may stand and how
/// high the ground is, an A* route along that grid at the champion's own authored move speed, the target
/// dummy on the same floor, and every spell this window already casts. The automated simulation drives
/// all of it from code.</para>
///
/// <para>What this is NOT: Riot's ability logic is not in the data. <c>Scripts.wad.client</c> holds 53,908
/// files, and the spell scripts in it are Lua 5.1 bytecode STUBS - Aatrox's Q is 60 bytes, an empty
/// chunk. Damage, dashes, buffs, crowd control and passives are compiled into the game client. What the
/// bin does author - move speed, attack range and speed, cast ranges, targeting kinds, cooldowns, mana,
/// cast frames, missile speeds - is what the arena uses, and nothing here is invented to stand in for the
/// rest.</para>
/// </summary>
public sealed partial class MeshPreviewViewModel
{
    // ---- host wiring (the main window owns the install, the hash database and the readers) ----

    /// <summary>Everything the loader needs from the host, handed over once per load of the window.
    /// <paramref name="LoadScene"/> is for a host with no map viewport to hand over (and for tests): it
    /// produces the finished scene for a map WAD, and is used when <paramref name="LoadViewport"/> is null.</summary>
    public sealed record ArenaHost(
        string? GameDirectory,
        ReyEngine.Core.Hashing.IHashResolver Resolver,
        Func<uint, string?> ResolveBinName,
        Func<ulong, string?> ResolveWadPath,
        Func<string, Task<MainWindowViewModel>>? LoadViewport = null,
        Func<string, Task<ArenaScene>>? LoadScene = null);

    private ArenaHost? _arenaHost;

    /// <summary>The map every character is shown on: Summoner's Rift as the client ships it.</summary>
    public const string EnvironmentMapKey = "Map11";

    /// <summary>The one-line description the Play tab shows once the environment is up.</summary>
    public const string EnvironmentLine = "Environment: Summoner's Rift (Map11)";

    /// <summary>True when the install has Map11.wad.client, i.e. the default environment can load.</summary>
    public bool EnvironmentAvailable { get; private set; }

    private Task _environmentTask = Task.CompletedTask;

    /// <summary>The environment load that <see cref="ConfigureArena"/> started, so a caller (or a test) can
    /// wait for it. Completed when nothing is loading.</summary>
    public Task EnvironmentTask => _environmentTask;

    /// <summary>How many times the environment has actually been loaded, for the "once per window session"
    /// rule: a character or skin change must leave this where it was.</summary>
    public int EnvironmentLoadCount { get; private set; }

    /// <summary>
    /// Runs on EVERY character load. Hands over the host, then makes sure Map11 is the environment:
    /// loads it when it is not up yet, does nothing when it is (a different champion or skin is the same map).
    /// Without Map11 in the install the preview stays empty and <see cref="ArenaStatus"/> says so in one line.
    /// <paramref name="loadEnvironment"/> is false for a subject that is not a character (a prop, a clipless
    /// mesh), which has no use for a stage.
    /// </summary>
    public void ConfigureArena(ArenaHost host, bool loadEnvironment = true)
    {
        _arenaHost = host;
        EnvironmentAvailable = ArenaLoader.WadPathFor(host.GameDirectory, EnvironmentMapKey) is not null;
        OnPropertyChanged(nameof(EnvironmentAvailable));
        if (!loadEnvironment) { LeaveStage(); return; }   // a prop or bare mesh: no stage under or around it
        if (!EnvironmentAvailable)
        {
            if (_arena is null) ArenaStatus = "Environment: none - Map11 is not in the game install.";
            return;
        }
        // A load that failed for this install is not retried on every character click; the failure line stays.
        if (_environmentFailedFor is { } failed)
        {
            if (string.Equals(failed, host.GameDirectory ?? "", StringComparison.OrdinalIgnoreCase)) return;
            _environmentFailedFor = null;
        }
        _environmentTask = EnsureEnvironmentAsync();
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

    /// <summary>The Environment line of the Play tab: what is loaded, loading, missing or failed.</summary>
    [ObservableProperty] private string _arenaStatus = "";
    [ObservableProperty] private bool _arenaLoading;
    [ObservableProperty] private bool _arenaFollowCamera = true;

    private ArenaScene? _arena;
    [ObservableProperty] private MainWindowViewModel? _arenaViewport;
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
        if (UseDx11Preview && ArenaViewport is null && _arena is { } arena)
            list.Add(new PropInstanceData(arena.Dx11Geometry, Matrix4x4.Identity));
        // M725: the NVR preview backdrop gets the same treatment the arena floor has had since M665.
        // Ticking Direct3D 11 used to make Dominion / Twisted Treeline silently vanish while the MAP
        // BACKDROP card and its sliders stayed on screen driving nothing.
        //
        // M727: now only a FALLBACK. The D3D11 renderer has a real NVR backdrop pass (four-blend, height blend,
        // the composite atlas, vertex light, Light.dat - MeshPreviewViewModel.Backdrop), so this diffuse-only
        // prop comes back only when that pass could not be built. Drawing both would put the map in twice.
        else if (UseDx11Preview && Dx11BackdropFailed && BackgroundVisible && BackdropProp() is { } backdrop)
            list.Add(new PropInstanceData(backdrop, BackdropWorld));
        if (DummyProps is { } dummy) list.AddRange(dummy.Instances);
        // M869: the ally dummy, while a simulated ability needs one. The same mesh as the enemy, turned toward the champion.
        if (SimAllyPosition is { } ally && _dummyMesh is not null && !_simAllyHidden)
        {
            float yaw = MathF.Atan2(CharacterPosition.X - ally.X, CharacterPosition.Z - ally.Z);
            list.Add(new PropInstanceData(_dummyMesh, Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(ally)));
        }
        list.AddRange(SimUnitInstances());   // M874: summoned units
        SceneProps = list.Count > 0 ? new PropRenderSet(list) : null;
    }

    /// <summary>M872: a stage viewport opened under Direct3D 11 skipped the OpenGL texture decode. When OpenGL is the preview, make
    /// those textures and hand the backdrop its pictures. A no-op for a viewport that already has them, or while D3D11 is on.</summary>
    private void EnsureArenaGlTextures()
    {
        if (UseDx11Preview || ArenaViewport is not { IsLeanStage: true } viewport) return;
        // textures still to make, or made (while D3D11 was ticked again) but the backdrop was built before them
        if (viewport.NeedsDeferredMapTextures || !ReferenceEquals(_glBackdropViewport, viewport))
            _ = RestoreArenaGlTexturesAsync(viewport);
    }

    private MainWindowViewModel? _glBackdropViewport;

    private async Task RestoreArenaGlTexturesAsync(MainWindowViewModel viewport)
    {
        try
        {
            await viewport.LoadDeferredMapTexturesAsync();
            if (UseDx11Preview || !ReferenceEquals(ArenaViewport, viewport) || !_arenaOwnsBackdrop) return;
            SetArenaBackdrop(viewport.ArenaBackground(EnvironmentMapKey));
            _glBackdropViewport = viewport;
        }
        catch (Exception ex) { LogDx11?.Invoke("Arena", "OpenGL textures for the stage could not be made: " + ex.Message); }
    }

    /// <summary>M725: the backdrop's placement, composed EXACTLY as the GL viewport composes it
    /// (<c>ViewportControl.BackgroundWorld</c>: translate, then rotate about world Y). A different order
    /// here would put the same map in a different place under the two renderers, which is the one thing
    /// this is meant to stop.</summary>
    private Matrix4x4 BackdropWorld =>
        Matrix4x4.CreateTranslation(BackgroundOffset)
        * Matrix4x4.CreateRotationY((float)(BackgroundRotation * Math.PI / 180.0));

    private PropMesh? _backdropProp;
    private object? _backdropPropFor;
    private object? _backdropPropTexturesFor;

    /// <summary>The backdrop as diffuse-only prop geometry for the D3D11 host, built once per mesh. The
    /// D3D11 path has no NVR material model, so this is the same deliberate compromise the arena floor
    /// makes: the same geometry and the same resolved diffuse, without the blend layers or baked light.
    ///
    /// <para>M727: cached on the mesh AND the texture list. Keyed on the mesh alone, a prop built before its
    /// textures arrived stayed textureless for the rest of the session - the mesh reference never changed, so
    /// the cache never missed. That, together with SetBackground assigning the mesh first, is the whole of
    /// "the D3D11 backdrop is white".</para></summary>
    private PropMesh? BackdropProp()
    {
        if (BackgroundMesh is not { } m)
        {
            _backdropProp = null; _backdropPropFor = null; _backdropPropTexturesFor = null;
            return null;
        }
        var tex = BackgroundTextures;
        if (_backdropProp is not null && ReferenceEquals(_backdropPropFor, m) && ReferenceEquals(_backdropPropTexturesFor, tex))
            return _backdropProp;

        var subs = m.SubMeshes
            .Select((s, i) => new PropSubmesh(s.StartIndex, s.IndexCount, tex is not null && i < tex.Count ? tex[i] : null))
            .ToList();
        _backdropPropFor = m;
        _backdropPropTexturesFor = tex;
        return _backdropProp = new PropMesh("backdrop|" + (BackgroundMapName ?? "?"),
            m.Positions, m.Normals, m.Uvs, m.Indices, subs);
    }

    partial void OnBackgroundMeshChanged(ReyEngine.Formats.Meshes.MeshAsset? value) => RebuildSceneProps();
    partial void OnBackgroundVisibleChanged(bool value) => RebuildSceneProps();
    partial void OnBackgroundOffsetChanged(Vector3 value) => RebuildSceneProps();
    partial void OnBackgroundRotationChanged(double value) => RebuildSceneProps();

    /// <summary>M725: true when the backdrop is configured but the active renderer cannot show it the way
    /// the card's controls describe - the D3D11 host draws it as diffuse-only geometry, with no baked
    /// light, no blend layers and no Light.dat. Surfaced so the card can say so instead of looking broken.</summary>
    // M727: true only when the D3D11 backdrop pass could NOT be built and the diffuse-only prop is standing in.
    // With the pass working, D3D11 draws the full NVR look and the card's note would be a false statement.
    public bool BackdropIsDiffuseOnly => UseDx11Preview && HasBackground && _arena is null && Dx11BackdropFailed;

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

    /// <summary>The champion/dummy placement the loaded map defines; null until a map is up.</summary>
    private ArenaStage? _stage;

    /// <summary>True when the environment is up and still the viewport's backdrop. The legacy-map viewer
    /// clears the backdrop (<see cref="SetBackground"/>) when it takes the window over, which leaves
    /// <c>_arena</c> set and the floor gone; that case is "not intact", so the next champion reloads it.</summary>
    private bool EnvironmentIntact => _arena is not null && _arenaOwnsBackdrop && HasBackground;

    // The environment while a non-character subject is shown: loaded, kept, not drawn, not owning the character.
    private ArenaScene? _parkedScene;
    private MainWindowViewModel? _parkedViewport;

    /// <summary>False once the stage was left or the window closed. A load that finishes after that parks its
    /// result instead of applying it (no control timer against a closed window, no stage under a legacy map).</summary>
    private bool _stageWanted;

    /// <summary>The install a load failed for. A failed load is not retried on every character click; it is
    /// retried when the game directory changes.</summary>
    private string? _environmentFailedFor;

    /// <summary>
    /// M868: leave the stage. A non-character subject (a legacy map, a prop, a bare mesh) is on screen, and the
    /// stage must not leak into it: no control tick, no dummy, no follow camera, and no Map11 drawn under or around
    /// the subject. The loaded environment is PARKED, not thrown away - the next character brings it back
    /// without reading the map again.
    /// </summary>
    public void LeaveStage()
    {
        _stageWanted = false;
        ClearSimulation();                     // M869: a run must not outlive the stage it plays on, nor a champion's list a prop
        if (_arena is not { } scene) return;

        _pendingAttackTarget = null;
        _waypoints.Clear();
        CancelPendingCast();
        _controller.Stop();
        ControlMode = false;
        TargetDummyEnabled = false;

        _parkedScene = scene;
        _parkedViewport = ArenaViewport;
        _arena = null;
        ArenaViewport = null;
        RebuildSceneProps();
        SetArenaBackdrop(null);                  // gives the backdrop placement back and clears the floor

        // a subject is drawn at ModelPosition/ModelYaw: leave them at the origin
        _controller.Teleport(Vector3.Zero, 0f);
        CharacterPosition = Vector3.Zero;
        CharacterYaw = 0;
        RangeRingSlot = null;
        _rangeRingCastSlot = -1;
        RefreshRangeRing();
        OnPropertyChanged(nameof(HasArena));
        OnPropertyChanged(nameof(ArenaName));
    }

    /// <summary>
    /// Make Map11 the environment: a no-op while it is loading or already up, a restore when it was parked, a
    /// load otherwise. The map is loaded ONCE per window session - a different champion or skin goes through
    /// here and returns at once.
    /// </summary>
    public Task EnsureEnvironmentAsync()
    {
        _stageWanted = true;
        if (ArenaLoading) return _environmentTask;
        if (EnvironmentIntact)
        {
            if (!ControlMode) ControlMode = true;   // the window was closed and reopened: the stage is still there
            return Task.CompletedTask;
        }
        if (_parkedScene is { } parked)
        {
            ArenaViewport = _parkedViewport;
            _parkedScene = null; _parkedViewport = null;
            ApplyEnvironment(parked);
            EnsureArenaGlTextures();
            return Task.CompletedTask;
        }
        return LoadEnvironmentAsync();
    }

    private async Task LoadEnvironmentAsync()
    {
        string key = EnvironmentMapKey;
        if (_arenaHost is not { } host || ArenaLoading) return;
        string? wad = ArenaLoader.WadPathFor(host.GameDirectory, key);
        if (wad is null) { ArenaStatus = "Environment: none - Map11 is not in the game install."; return; }

        ArenaLoading = true;
        EnvironmentLoadCount++;
        ArenaStatus = "Environment: loading Summoner's Rift (Map11)...";
        MainWindowViewModel? viewport = null;
        bool applied = false;
        try
        {
            ArenaScene scene;
            if (host.LoadViewport is { } loadViewport)
            {
                viewport = await loadViewport(wad);
                var background = viewport.ArenaBackground(key);
                var mesh = background.Mesh;
                var min = new Vector3(float.MaxValue);
                var max = new Vector3(float.MinValue);
                for (int i = 0; i < mesh.Positions.Length; i += 3)
                {
                    var p = new Vector3(mesh.Positions[i], mesh.Positions[i + 1], mesh.Positions[i + 2]);
                    min = Vector3.Min(min, p); max = Vector3.Max(max, p);
                }
                var arenaNav = viewport.ArenaNavGrid;
                scene = new ArenaScene(key, background,
                    new PropMesh(key, mesh.Positions, mesh.Normals, mesh.Uvs, mesh.Indices, new List<PropSubmesh>()),
                    arenaNav, ArenaLoader.SpawnFor(arenaNav, min, max), min, max, mesh.SubMeshes.Count, 0, 0, 0);
            }
            else if (host.LoadScene is { } loadScene)
            {
                scene = await loadScene(wad);
            }
            else
            {
                var lines = new List<string>();
                scene = await Task.Run(() => ArenaLoader.Load(wad, host.Resolver, host.ResolveBinName,
                    host.ResolveWadPath, l => lines.Add(l)));
                foreach (var l in lines) LogDx11?.Invoke("Arena", l);
            }

            _environmentFailedFor = null;
            if (!_stageWanted)
            {
                // The window closed, or a non-character subject took over, while this was loading. Keep the
                // result for the next character; start no timer and draw nothing.
                _parkedScene = scene; _parkedViewport = viewport;
                applied = true;
                ArenaStatus = EnvironmentLine + " (ready)";
                return;
            }

            var previous = ArenaViewport;
            ArenaViewport = viewport;
            if (!ReferenceEquals(previous, viewport)) previous?.ReleaseArenaViewport();
            applied = true;
            ApplyEnvironment(scene);
            EnsureArenaGlTextures();
        }
        catch (Exception ex)
        {
            // M868: a failure is remembered (no retry on every click) and a half-built viewport is released.
            _environmentFailedFor = host.GameDirectory ?? "";
            if (!applied) viewport?.ReleaseArenaViewport();
            else
            {
                _arena = null; _stage = null;
                var half = ArenaViewport; ArenaViewport = null;
                if (!ReferenceEquals(half, viewport)) half?.ReleaseArenaViewport();
                viewport?.ReleaseArenaViewport();
                try { SetArenaBackdrop(null); RebuildSceneProps(); } catch { /* the failure above is the one to report */ }
            }
            ArenaStatus = $"Environment: {key} could not be loaded - {ex.Message}";
            LogDx11?.Invoke("Arena", $"{key}: {ex}");
        }
        finally { ArenaLoading = false; }
    }

    /// <summary>Put a scene on screen: the floor, the stage, the dummy, the camera, and the control gate.</summary>
    private void ApplyEnvironment(ArenaScene scene)
    {
        string key = scene.MapKey;
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

        // M868: the fixed stage - champion, facing, dummy, camera - then the stage owns the character.
        _stage = ArenaStage.For(scene.MapKey, scene.Nav, scene.BoundsMin, scene.BoundsMax);
        TargetDummyEnabled = true;
        PlaceOnStage();
        LogDx11?.Invoke("Arena", $"{key}: stage at {_stage.Champion}, yaw {_stage.Yaw:0.###}, dummy {_stage.Dummy}");
        ControlMode = true;
        LogDx11?.Invoke("Arena", $"{key}: applied.");

        ArenaStatus = EnvironmentLine
                      + (scene.HasNavGrid ? "" : " - flat floor, no navgrid")
                      + (scene.TexturesMissing > 0 ? $" - {scene.TexturesMissing} texture(s) missing" : "");
        OnPropertyChanged(nameof(HasArena));
        OnPropertyChanged(nameof(ArenaName));
    }

    /// <summary>Champion, facing and dummy onto the stage, and the camera onto both of them.</summary>
    private void PlaceOnStage()
    {
        var stage = _stage ?? ArenaStage.Origin;
        _controller.Teleport(stage.Champion, stage.Yaw);
        CharacterPosition = stage.Champion;
        CharacterYaw = stage.Yaw;
        if (_stage is not null || TargetDummyEnabled)
        {
            DummyX = stage.Dummy.X; DummyY = stage.Dummy.Y; DummyZ = stage.Dummy.Z;
        }
        RebuildDummyProps();   // the dummy turns to the champion's NEW position even if it did not move itself
        FrameScene();
    }

    /// <summary>
    /// Frame the champion and the dummy together: the camera looks at their midpoint (<see cref="CameraFocus"/>).
    /// The window's follow camera takes it from here, and <see cref="SceneFrameCount"/> tells it to snap to
    /// the new target instead of easing there, so a reset never leaves the camera sliding.
    /// </summary>
    private void FrameScene()
    {
        FocusPoint = CameraFocus;
        SceneFrameCount++;
    }

    /// <summary>The point the camera frames: the middle of the champion and the dummy, or the champion alone.</summary>
    public Vector3 CameraFocus
    {
        get
        {
            // M869: the ally dummy, when an ability brought one, is part of the picture
            var sum = CharacterPosition; int n = 1;
            if (TargetDummyPosition is { } dummy) { sum += dummy; n++; }
            if (SimAllyPosition is { } ally && !_simAllyHidden) { sum += ally; n++; }
            foreach (var extra in _simFrameExtras) { sum += extra; n++; }   // M874: where a jump lands, where a summoned unit goes
            return sum / n;
        }
    }

    /// <summary>Bumped whenever the scene is (re)framed; the window snaps its camera on a change.</summary>
    public int SceneFrameCount { get; private set; }

    /// <summary>
    /// Restore the scene to how it opened: champion on the stage point facing the stage direction, the dummy
    /// back in front of him, playback, effects and sounds stopped, any walk or pending cast cancelled, the
    /// idle pose back, and the camera reframed. This supersedes the old "Reset position".
    /// </summary>
    [RelayCommand]
    public void ResetScene()
    {
        StopEvent();                       // playback, scheduled sounds, the playing clip
        SelectedVfx = null;
        _castPlan = null;
        _castBusyUntil = DateTime.MinValue;
        _rangeRingCastSlot = -1;
        PlaceOnStage();
        StopMovement();                    // walk, attack, pending cast; the controller; back to idle
        RefreshRangeRing();
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
            StopMovement();
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
        if (_pendingAttackTarget is { } attack
            && Vector2.Distance(new(CharacterPosition.X, CharacterPosition.Z), new(attack.X, attack.Z)) <= AttackRange)
        {
            _pendingAttackTarget = null;
            _waypoints.Clear();
            _controller.Attack(attack);
        }

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

        // The gameplay camera follows smoothly in the view; FocusPoint would snap the orbit every tick.
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
