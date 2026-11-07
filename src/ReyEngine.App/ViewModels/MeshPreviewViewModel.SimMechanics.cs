using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Characters.Simulation;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.App.ViewModels;

/// <summary>M874: a summoned unit's meshes: the one it stands in and the one it walks in (separate prop meshes, because a prop plays one clip).</summary>
public sealed record SimUnitMeshes(PropMesh Idle, PropMesh? Run);

/// <summary>
/// M874: the scripted mechanics of the simulation - a summoned unit (<see cref="SimStepKind.Spawn"/>), a unit moved by a mechanic
/// (<see cref="SimStepKind.MoveUnit"/>), a named effect (<see cref="SimStepKind.Effect"/>), a hidden unit, a basic attack and a jump.
///
/// <para>No second pipeline. A summoned unit is a PROP built by the same decode a map's props use (the host's <c>TryBuildPropMesh</c>:
/// the skin bin's mesh, skeleton, materials and animation graph) and drawn by the same prop driver as the enemy and ally dummies; an
/// effect is one more <see cref="VfxPlaybackItem"/> in the one playback; a basic attack is the Control tab's own attack path.</para>
///
/// <para>Everything a run adds is undone by <see cref="CleanupMechanics"/> from <c>RestoreScene</c>: spawned units removed, the ally shown
/// again, dummies back on the stage, effects stopped. Nothing may reach the next ability, champion or skin.</para>
/// </summary>
public sealed partial class MeshPreviewViewModel
{
    /// <summary>Host hook: the meshes of a summoned unit by skin ("Characters/KalistaSpawn/Skins/Skin0") and the graph clip names to stand and walk in. Null: the unit is not drawn.</summary>
    public Func<string, string?, string?, SimUnitMeshes?>? LoadUnitMeshes { get; set; }

    /// <summary>Host hook: prepare the D3D11 scene of a mesh this view model owns (a summoned unit) with the reader that can find its textures - the main view model's.</summary>
    public Func<PropMesh, ReyEngine.App.Services.PreparedCharacterScene?>? PrepareOwnPropScene { get; set; }

    /// <summary>Is <paramref name="mesh"/> one of the summoned units' meshes this view model built?</summary>
    public bool IsOwnPropMesh(PropMesh mesh) =>
        _simUnitMeshCache.Values.Any(v => v.Meshes is { } m && (ReferenceEquals(m.Idle, mesh) || ReferenceEquals(m.Run, mesh)));

    /// <summary>Host hook: the number of the skin on stage ("Characters/Kalista/Skins/Skin24" gives 24), so a summoned unit uses the sentinel skin of the same number when it has one.</summary>
    public Func<int?>? LoadedSkinNumber { get; set; }

    // ---- units ----

    /// <summary>A live summoned unit.</summary>
    internal sealed class SimUnit
    {
        public required string Id { get; init; }
        public required SimStep Step { get; init; }
        public SimUnitMeshes? Meshes { get; init; }
        public string Skin { get; init; } = "";
        public Vector3 Position;
        public float Yaw;
        public double SpawnClock;
        public bool Walking;
    }

    private readonly Dictionary<string, SimUnit> _simUnits = new(StringComparer.OrdinalIgnoreCase);
    private bool _simAllyHidden;
    private bool _simPropsDirty;
    private readonly List<Vector3> _simFrameExtras = new();
    private readonly List<ActiveMove> _simMoves = new();
    private sealed record ActiveMove(SimStep Step);

    /// <summary>Where a unit of the script stands now: "caster", "enemy", "ally" or a spawned unit's id; null when it is not on the stage.</summary>
    public Vector3? SimUnitPosition(string unit) => unit.ToLowerInvariant() switch
    {
        "caster" => CharacterPosition,
        "enemy" => TargetDummyPosition,
        "ally" => _simAllyHidden ? null : SimAllyPosition,
        _ => _simUnits.TryGetValue(unit, out var u) ? u.Position : null,
    };

    /// <summary>The summoned units now on the stage (ids), for the check and for tests.</summary>
    public IReadOnlyList<string> SimSpawnedUnits => _simUnits.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>The ally dummy is hidden (inside Kalista).</summary>
    public bool SimAllyHidden => _simAllyHidden;

    /// <summary>One recorded position of a unit: the simulation clock, the unit, where it was, and whether it was drawn. Appended while a run moves units.</summary>
    public sealed record SimUnitSample(double Clock, string Unit, Vector3 Position, bool Visible);

    /// <summary>Positions of every unit a mechanic moved, sampled at most every 50 ms of simulation time, since the run began. Cleared with <see cref="ClearSimulationHistory"/> and at each run start.</summary>
    public List<SimUnitSample> SimUnitTrack { get; } = new();
    private double _simLastTrack = double.NegativeInfinity;

    /// <summary>The caster's position over the run (clock, X, Y, Z), sampled with the units; a jump's arc is read from it.</summary>
    public List<(double Clock, float X, float Y, float Z)> SimCasterTrack { get; } = new();

    /// <summary>The names of the effects that started in this run, from Effect steps whose system resolved.</summary>
    public List<string> SimEffectStarts { get; } = new();

    /// <summary>Which prop instance each spawned unit is drawn as, for the check's pixel box (bound the unit's world position projected to the screen).</summary>
    public IReadOnlyList<(string Id, Vector3 Position, bool HasMesh)> SimSpawnedUnitStates =>
        _simUnits.Values.Select(u => (u.Id, u.Position, u.Meshes is not null)).ToList();

    /// <summary>Instances the scene adds for the spawned units (one per unit; the run mesh while it walks).</summary>
    private IEnumerable<PropInstanceData> SimUnitInstances()
    {
        foreach (var u in _simUnits.Values)
        {
            if (u.Meshes is not { } meshes) continue;
            var mesh = u.Walking && meshes.Run is not null ? meshes.Run : meshes.Idle;
            yield return new PropInstanceData(mesh, Matrix4x4.CreateRotationY(u.Yaw) * Matrix4x4.CreateTranslation(u.Position));
        }
    }

    private void SetEnemyPositionLight(Vector3 p)
    {
        // The dummy's own setters replay whatever is playing (ReplaySelectedOrClip): a mechanic moves it every frame, so only the position and the model change.
        _dummyX = p.X; _dummyY = p.Y; _dummyZ = p.Z;
        OnPropertyChanged(nameof(TargetDummyPosition));
        OnPropertyChanged(nameof(DummyGizmoPivot));
        RebuildDummyProps();
    }

    private void SetUnitPosition(string unit, Vector3 p)
    {
        switch (unit.ToLowerInvariant())
        {
            case "enemy": SetEnemyPositionLight(p); break;
            case "ally": SimAllyPosition = p; break;
            default:
                if (_simUnits.TryGetValue(unit, out var u)) { u.Position = p; _simPropsDirty = true; }
                break;
        }
    }

    // ---- executing the steps ----

    private void ExecuteMechanic(SimRun run, SimStep step, string tag)
    {
        switch (step.Kind)
        {
            case SimStepKind.Attack:
                PlayAttackStep(run, step);
                AddSimLog(step.Time, step.Text + tag);
                break;
            case SimStepKind.Spawn:
                SpawnUnit(run, step);
                AddSimLog(step.Time, step.Text + tag);
                break;
            case SimStepKind.Despawn:
                if (step.Unit is { } du && _simUnits.Remove(du)) { _simPropsDirty = true; RebuildSceneProps(); _simPropsDirty = false; }
                AddSimLog(step.Time, step.Text + tag);
                break;
            case SimStepKind.MoveUnit:
                _simMoves.Add(new ActiveMove(step));
                AddSimLog(step.Time, step.Text + tag);
                break;
            case SimStepKind.Effect:
                PlayEffectStep(run, step);
                AddSimLog(step.Time, step.Text + tag);
                break;
            case SimStepKind.Hide:
                if (step.Unit == "ally") { _simAllyHidden = true; RebuildSceneProps(); }
                AddSimLog(step.Time, step.Text + tag);
                break;
            case SimStepKind.Show:
                if (step.Unit == "ally") { _simAllyHidden = false; RebuildSceneProps(); }
                AddSimLog(step.Time, step.Text + tag);
                break;
        }
    }

    // ---- the tick ----

    /// <summary>Move what the mechanics move, for this tick: active MoveUnit spans, the summoned units along their paths, and the unit track.</summary>
    private void TickMechanics(SimRun run)
    {
        bool moved = false;
        for (int i = _simMoves.Count - 1; i >= 0; i--)
        {
            var s = _simMoves[i].Step;
            double span = Math.Max(1e-6, s.EndTime - s.Time);
            float f = (float)Math.Clamp((run.Clock - s.Time) / span, 0.0, 1.0);
            var p = Vector3.Lerp(s.From ?? Vector3.Zero, s.To ?? Vector3.Zero, f);
            if (s.Move == SimMoveKind.Knockup) p.Y += s.Height * 4f * f * (1f - f);     // a parabola: up and back to the same spot
            else if (_arena?.Nav is { } nav) p.Y = NavGridPath.GroundHeight(nav, p);
            SetUnitPosition(s.Unit ?? "", p);
            moved = true;
            if (f >= 1f) _simMoves.RemoveAt(i);
        }
        foreach (var u in _simUnits.Values)
        {
            var before = u.Position;
            AdvanceUnit(u, run.Clock);
            if (u.Position != before) { moved = true; _simPropsDirty = true; }
        }
        if (_simPropsDirty) { _simPropsDirty = false; RebuildSceneProps(); }
        if (moved || SimUnitTrack.Count == 0 || run.Clock - _simLastTrack >= 0.05) RecordUnits(run.Clock);
    }

    private void RecordUnits(double clock)
    {
        if (clock - _simLastTrack < 0.05 && SimUnitTrack.Count > 0) return;
        _simLastTrack = clock;
        SimCasterTrack.Add((clock, CharacterPosition.X, CharacterPosition.Y, CharacterPosition.Z));
        if (TargetDummyPosition is { } e) SimUnitTrack.Add(new SimUnitSample(clock, "enemy", e, true));
        if (SimAllyPosition is { } a) SimUnitTrack.Add(new SimUnitSample(clock, "ally", a, !_simAllyHidden));
        foreach (var u in _simUnits.Values) SimUnitTrack.Add(new SimUnitSample(clock, u.Id, u.Position, u.Meshes is not null));
    }

    // ---- summoned units ----

    private readonly Dictionary<string, (string Skin, SimUnitMeshes? Meshes)> _simUnitMeshCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, System.Threading.Tasks.Task<(string Skin, SimUnitMeshes? Meshes)>> _simUnitLoads = new(StringComparer.OrdinalIgnoreCase);

    private static string UnitKey(SimStep step, int? number) => $"{step.Resource}|{number}|{step.IdleClip}|{step.RunClip}";

    /// <summary>The decode of a unit: the sentinel skin of the loaded skin's number when it has one, else the base skin (skin 0). Runs on a worker (the host's prop decode is what map props use on workers).</summary>
    private (string Skin, SimUnitMeshes? Meshes) BuildUnit(SimStep step, int? number)
    {
        string skinFolder = $"Characters/{step.Resource}/Skins/Skin";
        string skin = skinFolder + "0";
        SimUnitMeshes? meshes = null;
        if (LoadUnitMeshes is { } load)
        {
            if (number is > 0)
            {
                string own = skinFolder + number.Value.ToString(CultureInfo.InvariantCulture);
                if (load(own, step.IdleClip, step.RunClip) is { } found) { meshes = found; skin = own; }
            }
            meshes ??= load(skin, step.IdleClip, step.RunClip);
        }
        return (skin, meshes);
    }

    /// <summary>The skin and meshes a Spawn step stands in. Normally built by <see cref="PreloadSimUnits"/> on a worker while the run's first moments play, so the UI thread is not held for the decode; when the
    /// Spawn step arrives first it waits for that build. Only a SUCCESS is remembered: a unit that failed to build is tried again next time.</summary>
    private (string Skin, SimUnitMeshes? Meshes) ResolveUnitMeshes(SimStep step)
    {
        int? number = LoadedSkinNumber?.Invoke();
        string key = UnitKey(step, number);
        if (_simUnitMeshCache.TryGetValue(key, out var cached)) return cached;
        (string Skin, SimUnitMeshes? Meshes) result;
        if (_simUnitLoads.Remove(key, out var task)) { try { result = task.GetAwaiter().GetResult(); } catch { result = ($"Characters/{step.Resource}/Skins/Skin0", null); } }
        else result = BuildUnit(step, number);
        if (result.Meshes is null) result = BuildUnit(step, number);   // a failed (or stale) background build gets one more try here before the unit is given up

        if (result.Meshes is not null) _simUnitMeshCache[key] = result;
        return result;
    }

    /// <summary>Start building every unit a script will summon, on a worker, before the clock starts.</summary>
    private void PreloadSimUnits(SimScript plan)
    {
        foreach (var st in plan.Steps)
        {
            if (st.Kind != SimStepKind.Spawn) continue;
            int? number = LoadedSkinNumber?.Invoke();
            string key = UnitKey(st, number);
            if (_simUnitLoads.TryGetValue(key, out var earlier) && (earlier.IsFaulted || earlier.IsCompletedSuccessfully && earlier.Result.Meshes is null)) _simUnitLoads.Remove(key);
            if (_simUnitMeshCache.ContainsKey(key) || _simUnitLoads.ContainsKey(key)) continue;
            _simUnitLoads[key] = System.Threading.Tasks.Task.Run(() => BuildUnit(st, number));
        }
    }

    private void SpawnUnit(SimRun run, SimStep step)
    {
        string id = step.Unit ?? "unit";
        var (skin, meshes) = ResolveUnitMeshes(step);
        var from = step.From ?? Vector3.Zero;
        var first = step.Path is { Count: > 0 } p ? p[0] : step.To ?? from;
        var dir = Flat(first - from);
        var unit = new SimUnit
        {
            Id = id, Step = step, Meshes = meshes, Skin = skin, Position = from, SpawnClock = run.Clock,
            Yaw = dir.LengthSquared() > 1f ? MathF.Atan2(dir.X, dir.Z) : (float)CharacterYaw,
            Walking = step.Path is { Count: > 0 },
        };
        _simUnits[id] = unit;
        _simPropsDirty = false;
        RebuildSceneProps();
        LogDx11?.Invoke("Simulation", $"spawn {id}: {skin}{(meshes is null ? " - no meshes (the host could not build the unit)" : "")}");
    }

    /// <summary>Where a unit spawned by <paramref name="spawn"/> stands <paramref name="seconds"/> after it appeared, without running anything (the path logic on its own, for tests).</summary>
    public Vector3 PreviewUnitPosition(SimStep spawn, double seconds)
    {
        var unit = new SimUnit { Id = "preview", Step = spawn, Position = spawn.From ?? Vector3.Zero, SpawnClock = 0 };
        AdvanceUnit(unit, seconds);
        return unit.Position;
    }

    private static Vector3 Flat(Vector3 v) => new(v.X, 0f, v.Z);

    /// <summary>Where a unit that left <c>Step.From</c> at its spawn stands <paramref name="clock"/> seconds into the run: along the first leg to the first waypoint at the step's speed, then round the waypoints (a patrol) when the step loops, else it stops at the last one.</summary>
    private void AdvanceUnit(SimUnit u, double clock)
    {
        var s = u.Step;
        if (s.Path is not { Count: > 0 } path || !(s.Speed is > 0f)) { u.Walking = false; return; }
        double travelled = Math.Max(0.0, clock - u.SpawnClock) * s.Speed.Value;
        var legs = new List<(Vector3 A, Vector3 B)> { (s.From ?? path[0], path[0]) };
        for (int i = 0; i + 1 < path.Count; i++) legs.Add((path[i], path[i + 1]));
        var cycle = new List<(Vector3 A, Vector3 B)>();
        if (s.Loop && path.Count > 1) { for (int i = 0; i + 1 < path.Count; i++) cycle.Add((path[i], path[i + 1])); cycle.Add((path[^1], path[0])); }
        // the first leg, then (looping) the cycle forever; else the legs once
        Vector3 pos = path[^1]; Vector3 dir = Vector3.Zero; bool walking = false;
        double d = travelled;
        var order = s.Loop && cycle.Count > 0 ? new List<(Vector3 A, Vector3 B)> { legs[0] } : legs;
        bool placed = false;
        foreach (var (a, b) in order)
        {
            float len = Flat(b - a).Length();
            if (len < 1e-3f) continue;                       // a leg with no length (the target clamped onto the start) is walked through, not stood on
            if (d <= len) { pos = a + (b - a) * (float)(d / len); dir = b - a; walking = true; placed = true; break; }
            d -= len;
        }
        if (!placed && s.Loop && cycle.Count > 0)
        {
            double total = cycle.Sum(c => (double)Flat(c.B - c.A).Length());
            if (total > 1e-3)
            {
                d %= total;
                foreach (var (a, b) in cycle)
                {
                    float len = Flat(b - a).Length();
                    if (d <= len) { pos = a + (b - a) * (float)(len < 1e-3f ? 1.0 : d / len); dir = b - a; walking = true; break; }
                    d -= len;
                }
            }
        }
        else if (!placed) { pos = path[^1]; walking = false; }
        if (_arena?.Nav is { } nav) pos.Y = NavGridPath.GroundHeight(nav, pos);
        u.Position = pos;
        u.Walking = walking;
        if (Flat(dir).LengthSquared() > 1f) u.Yaw = MathF.Atan2(dir.X, dir.Z);
    }

    // ---- effects ----

    private sealed class SimFx
    {
        public required VfxPlaybackItem Template { get; init; }
        public required double EndClock { get; init; }
        public required string Name { get; init; }
    }

    private readonly List<SimFx> _simFx = new();
    private readonly HashSet<VfxPlaybackItem> _simFxPublished = new();

    /// <summary>The names of the effects now playing from Effect steps (for the check and for tests).</summary>
    public IReadOnlyList<string> SimLiveEffects => _simFx.Select(f => f.Name).ToList();

    private VfxSystemDefinition? FindSystemByName(string name) =>
        _vfxDefs.Values.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

    private void PlayEffectStep(SimRun run, SimStep step)
    {
        if (step.Resource is not { Length: > 0 } name || FindSystemByName(name) is not { } def)
        {
            LogDx11?.Invoke("Simulation", $"effect '{step.Resource}' is not in the skin's systems; skipped");
            return;
        }
        var at = (step.Unit is { } u ? SimUnitPosition(u) : null) ?? step.From ?? CharacterPosition;
        var toward = step.Unit == "enemy" ? CharacterPosition : (TargetDummyPosition ?? at + new Vector3(0f, 0f, 100f));
        var item = BuildItem(def, VfxCastFrame.Toward(at, toward, at)) with { HostIsCaster = string.Equals(step.Unit, "caster", StringComparison.OrdinalIgnoreCase) };
        if (step.To is { } travel && step.Speed is > 0f)
        {
            float dist = Flat(travel - at).Length();
            item = item with { TravelTo = travel, TravelSeconds = dist / step.Speed.Value };
        }
        SimEffectStarts.Add(def.Name);
        _simFx.Add(new SimFx { Template = item, EndClock = step.EndTime > step.Time ? step.EndTime : run.Clock + 1.0, Name = def.Name });
        RepublishSimEffects(run.Clock);
    }

    /// <summary>A composite or an attack's own items (the missile, the impact) with the simulation clock at which that playback began: their delays count from there.</summary>
    private sealed record SimBundle(IReadOnlyList<VfxPlaybackItem> Items, double Start);
    private readonly List<SimBundle> _simBundles = new();
    private readonly HashSet<VfxPlaybackItem> _simBundleOriginals = new();
    private readonly HashSet<VfxPlaybackItem> _simBundlePublished = new();

    /// <summary>
    /// Put the live effects - and what an earlier step started and has not finished - back into the one playback. Every playback change builds a fresh VfxPlayback, which the D3D11
    /// surface treats as a restart of everything in it, so a step that rebuilds the playback (a composite, an attack, a clip) would otherwise cut off the missile and impact of the step
    /// before it. This runs after each such step and after an Effect starts: an earlier bundle's items that are still in the future are re-timed to this moment; one that has begun and
    /// has no delay or flight (a persistent caster system) starts again for a few seconds; a missile or impact that is already past is not replayed. Expired effects are dropped.
    /// </summary>
    private void RepublishSimEffects(double clock)
    {
        if (_eventBundle is { Count: > 0 } latest && !_simBundles.Any(x => ReferenceEquals(x.Items, latest)))
        {
            _simBundles.Add(new SimBundle(latest, clock));
            foreach (var i in latest) _simBundleOriginals.Add(i);
        }
        _simFx.RemoveAll(f => f.EndClock <= clock);
        var items = new List<VfxPlaybackItem>(_idleItems);
        if (Playback is { } current)
        {
            // M882: the Playback setter rewrites every item to character semantics, so what we published earlier is no longer Equal to what we
            // remember; compare against both forms or the idle effects and republished copies would double on every republish.
            var known = new HashSet<VfxPlaybackItem>();
            foreach (var set in new IEnumerable<VfxPlaybackItem>[] { _idleItems, _simFxPublished, _simBundleOriginals, _simBundlePublished })
                foreach (var k in set) { known.Add(k); known.Add(CharacterItem(k)); }
            items.AddRange(current.Items.Where(i => !known.Contains(i)));
        }
        _simFxPublished.Clear();
        _simBundlePublished.Clear();
        foreach (var bundle in _simBundles)
        {
            double elapsed = clock - bundle.Start;
            foreach (var original in bundle.Items)
            {
                bool event_ = original.StartDelay > 0f || original.TravelTo is not null;
                float remaining = (float)(original.StartDelay - elapsed);
                VfxPlaybackItem? copy = null;
                if (event_) { if (remaining >= -0.02f) copy = original with { StartDelay = Math.Max(0f, remaining) }; }
                else if (elapsed < 4.0) copy = original;
                if (copy is null) continue;
                _simBundlePublished.Add(copy);
                items.Add(copy);
            }
        }
        foreach (var fx in _simFx)
        {
            var item = fx.Template with { StartDelay = 0f, EndTime = (float)Math.Max(0.05, fx.EndClock - clock) };
            _simFxPublished.Add(item);
            items.Add(item);
        }
        Playback = items.Count > 0 ? new VfxPlayback(items) : null;
    }

    // ---- a basic attack ----

    private void PlayAttackStep(SimRun run, SimStep step)
    {
        var attack = _attacks.Concat(_followUpAttacks).FirstOrDefault(a => a.Name.Equals(step.Name, StringComparison.OrdinalIgnoreCase));
        var target = (step.Unit is { } u ? SimUnitPosition(u) : null) ?? step.To ?? TargetDummyPosition ?? CharacterPosition;
        FaceToward(target);
        if (attack is null) { LogDx11?.Invoke("Simulation", $"attack '{step.Name}' is not in the champion's record; the clip alone plays"); PlayStepClip(step.Clip, null); }
        else PlayAttackSpell(attack, target, step.Clip);
        RepublishSimEffects(run.Clock);
    }

    private void FaceToward(Vector3 target)
    {
        var d = Flat(target - CharacterPosition);
        if (d.LengthSquared() > 1f) MoveCaster(CharacterPosition, MathF.Atan2(d.X, d.Z));
    }

    /// <summary>Play the clip a step names by itself (an attack's, a hop's): the same choice a spell's clip gets - Riot's name in the loaded skin's own graph, then the definition's file.</summary>
    private void PlayStepClip(SimClip? clip, string? authoredName)
    {
        if (clip is null) return;
        bool Has(string? file) => file is not null && Animation.Animations.Any(e => string.Equals(e.Name, file, StringComparison.OrdinalIgnoreCase));
        var choice = SimClipChooser.ChooseClip(authoredName, clip, null, _ownClips, _ownNodes, Has, null);
        if (choice is null) return;
        var entry = Animation.Animations.FirstOrDefault(e => string.Equals(e.Name, choice.File, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return;
        Animation.Loop = false;
        SelectClipInternal(entry, true);        // restart even when the same clip repeats
    }

    // ---- the end ----

    /// <summary>Undo everything the mechanics added: units removed, the ally shown, effects stopped, moves dropped, the framing extras forgotten. Idempotent.</summary>
    private void CleanupMechanics()
    {
        _simMoves.Clear();
        bool any = _simUnits.Count > 0 || _simAllyHidden;
        _simUnits.Clear();
        _simAllyHidden = false;
        _simFx.Clear();
        _simFxPublished.Clear();
        _simBundles.Clear(); _simBundleOriginals.Clear(); _simBundlePublished.Clear();
        _simFrameExtras.Clear();
        _simPropsDirty = false;
        _simLastTrack = double.NegativeInfinity;
        if (any) RebuildSceneProps();
    }
}
