using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Characters.Simulation;
using ReyEngine.App.Services;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.App.ViewModels;

/// <summary>One row of the SIMULATION ability list: the definition's ability, what the asset check says about it, and a Play button.</summary>
public sealed class SimAbilityRowViewModel : ObservableObject
{
    public SimAbilityRowViewModel(AbilitySim ability, AbilityValidation verdict, bool isSub, Action<SimAbilityRowViewModel> play, string? parentName = null,
        ClientImageHandle? icon = null, string? description = null)
    {
        _icon = icon ?? ClientImageHandle.None;
        _description = string.IsNullOrWhiteSpace(description) ? null : description;
        // the letter the icon falls back to: the slot key, or the parent's for a sub-cast; an extra has none
        IconLetter = ability.Key.Length > 0 && "PQWER".IndexOf(ability.Key[0]) >= 0 && (ability.Key.Length == 1 || !char.IsLetter(ability.Key[1])) ? ability.Key[..1]
            : ability.ParentKey is { Length: > 0 } pk && "PQWER".IndexOf(pk[0]) >= 0 && (pk.Length == 1 || !char.IsLetter(pk[1])) ? pk[..1] : "*";
        Ability = ability;
        Key = ability.Key;
        IsSub = isSub;
        Indent = isSub ? 16 : 0;
        // a sub-cast authors no client name of its own; it is a part of its parent
        Name = ability.DisplayName ?? (isSub ? parentName : null) ?? ability.AbilityName;
        Status = verdict.Status;
        StatusText = verdict.Status switch { SimStatus.Complete => "OK", SimStatus.Incomplete => "PARTIAL", _ => "BLOCKED" };
        Reasons = verdict.Reasons.Count == 0 ? "Every asset check passed (runtime and visual validation is a later step)." : string.Join("\n", verdict.Reasons);
        PlayCommand = new RelayCommand(() => play(this));
    }

    public AbilitySim Ability { get; }
    public string Key { get; }
    public string Name { get; }
    public bool IsSub { get; }
    public double Indent { get; }
    public SimStatus Status { get; }
    public string StatusText { get; }
    public string Reasons { get; }

    /// <summary>M870: the ability's icon from the client (decoded when the row is on screen); the key letter shows until it is there, and
    /// stays for an ability the client has no icon for.</summary>
    private ClientImageHandle _icon;
    public ClientImageHandle Icon { get => _icon; private set => SetProperty(ref _icon, value); }
    public string IconLetter { get; }
    /// <summary>The client's description as plain text, or null (extras and sub-casts the client does not describe).</summary>
    private string? _description;
    public string? Description { get => _description; private set { if (SetProperty(ref _description, value)) { OnPropertyChanged(nameof(HasDescription)); OnPropertyChanged(nameof(Tooltip)); } } }
    public bool HasDescription => Description is not null;

    /// <summary>The client's text arrived after the row was built (it is read on a worker): fill in what the row does not have yet.</summary>
    public void ApplyClient(string? description, ClientImageHandle? icon)
    {
        if (!string.IsNullOrWhiteSpace(description)) Description = description;
        if (icon is not null && !Icon.CanLoad) Icon = icon;
    }

    private const int TooltipColumns = 56;

    /// <summary>Word-wrapped at about <see cref="TooltipColumns"/> columns (a string tooltip does not wrap on its own).</summary>
    internal static string Wrap(string text)
    {
        var lines = new System.Collections.Generic.List<string>();
        foreach (string paragraph in text.Split('\n'))
        {
            string line = "";
            foreach (string word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > TooltipColumns) { lines.Add(line); line = word; }
                else line = line.Length == 0 ? word : line + " " + word;
            }
            lines.Add(line);
        }
        return string.Join("\n", lines);
    }
    /// <summary>What the row's tooltip says: the name, then the description. Null when the client has no text.</summary>
    public string? Tooltip => Description is null ? null : Wrap(Name + "\n\n" + Description);
    public bool IsComplete => Status == SimStatus.Complete;
    public bool IsIncomplete => Status == SimStatus.Incomplete;
    public bool IsBlocked => Status == SimStatus.Blocked;
    public ICommand PlayCommand { get; }

    private bool _isRunning;
    /// <summary>True while this ability is the one being simulated.</summary>
    public bool IsRunning { get => _isRunning; set => SetProperty(ref _isRunning, value); }
}

/// <summary>
/// M869: the ability SIMULATION in the Character window.
///
/// <para>The champion's <see cref="ChampionSimDefinition"/> (M867) says what each ability is; <see cref="SimulationPlanner"/> turns one
/// ability plus the stage into a timed <see cref="SimScript"/>; this file runs that script on the machinery the window already has.
/// There is no second VFX pipeline: the clip and the spell composite play through <c>PlayEvent</c> (so clip particle and sound events,
/// the caster, missile and target systems and the cast plan behave exactly as the Animate tab's EVENTS do), the caster is moved with
/// the character controller, and the dummies are the dummy prop instances.</para>
///
/// <para>One clock. The simulation clock advances by wall time times <see cref="SimSpeed"/>, and the same speed is handed to the clip
/// (<c>Animation.Speed</c>) and to both particle simulators, so pausing or slowing freezes or slows the pose, the displacement, the
/// hit times and the effects together. The GL control takes <see cref="SimSpeed"/> / <see cref="SimPaused"/> through its
/// ParticleSpeed / ParticlePaused (with <see cref="SimDrivesParticles"/> putting the effects on real time while a run is active); the
/// D3D11 surface takes <see cref="SimParticleTimeScale"/> every frame.</para>
/// </summary>
public sealed partial class MeshPreviewViewModel
{
    // ---- the definition ----

    /// <summary>Host hook: the definition of a champion (by its folder name), read from the cache or generated. Runs off the UI thread.</summary>
    public Func<string, Task<ChampionSimDefinition?>>? LoadSimulationDefinition { get; set; }

    private ChampionSimDefinition? _simDef;
    private ChampionValidation? _simVerdict;
    private string? _simChampion;
    private Task _simLoad = Task.CompletedTask;

    /// <summary>The load <see cref="BeginSimulation"/> started, so a caller (or a test) can wait for it.</summary>
    public Task SimulationTask => _simLoad;

    /// <summary>How many times a definition has been requested from the host: once per champion, not per skin.</summary>
    public int SimulationLoadCount { get; private set; }

    public ChampionSimDefinition? SimulationDefinition => _simDef;

    public ObservableCollection<SimAbilityRowViewModel> SimAbilities { get; } = new();
    [ObservableProperty] private bool _hasSimulation;
    [ObservableProperty] private string _simSummary = "";
    [ObservableProperty] private SimAbilityRowViewModel? _simSelected;

    /// <summary>
    /// A character was shown. Lazy and once per champion: a second call for the same champion (another skin) does nothing; a
    /// different champion drops the old definition, stops a run, and asks the host for the new one.
    /// </summary>
    public void BeginSimulation(string champion)
    {
        if (string.IsNullOrWhiteSpace(champion)) return;
        if (string.Equals(_simChampion, champion, StringComparison.OrdinalIgnoreCase)) return;
        StopSimulation();
        _simChampion = champion;
        _simDef = null; _simVerdict = null;
        ForgetRun();
        SimAbilities.Clear();
        SimSelected = null;
        HasSimulation = false;
        if (LoadSimulationDefinition is not { } load) { SimSummary = ""; return; }
        SimSummary = "Simulation: reading the champion's abilities...";
        SimulationLoadCount++;
        _simLoad = LoadSimulationAsync(champion, load);
    }

    /// <summary>A subject that is not a character is shown: stop any run and forget the champion's definition.</summary>
    public void ClearSimulation()
    {
        StopSimulation();
        _simChampion = null; _simDef = null; _simVerdict = null;
        ForgetRun();
        SimAbilities.Clear();
        SimSelected = null;
        HasSimulation = false;
        SimSummary = "";
    }

    /// <summary>Nothing of the last champion's run may survive into the next: the replay list, the log, the notes, the status.</summary>
    private void ForgetRun()
    {
        _simReplay.Clear();
        SimLog.Clear();
        SimTrace.Clear();
        SimNotes = "";
        SimStatusLine = "";
        SimCurrentAbility = "";
        SimCurrentStep = "";
    }

    private async Task LoadSimulationAsync(string champion, Func<string, Task<ChampionSimDefinition?>> load)
    {
        ChampionSimDefinition? def = null; string? error = null;
        try { def = await load(champion).ConfigureAwait(true); }
        catch (Exception ex) { error = ex.Message; }
        if (!string.Equals(_simChampion, champion, StringComparison.OrdinalIgnoreCase)) return;   // another champion took over
        if (def is null)
        {
            SimSummary = "Simulation: " + (error ?? "no definition for this champion.");
            _simChampion = null;               // a failed load is not remembered: showing the champion again tries again
            return;
        }
        SetSimulationDefinition(def);
    }

    /// <summary>Install a definition and build the ability list: top-level entries in the definition's order, each followed by its sub-casts.</summary>
    public void SetSimulationDefinition(ChampionSimDefinition def)
    {
        _simDef = def;
        _simChampion ??= def.Champion;
        _simVerdict = ChampionSimValidator.Validate(def);
        SimAbilities.Clear();
        // M870: the client's text for the ability bar - icons and descriptions - when the browser has the client data. Never required.
        _simClient = null;
        foreach (var top in def.Abilities.Where(a => a.ParentKey is null))
        {
            SimAbilities.Add(MakeRow(top, false));
            foreach (var sub in def.Abilities.Where(s => s.ParentKey == top.Key)) SimAbilities.Add(MakeRow(sub, true));
        }
        // a sub-cast whose parent is not a top-level entry would otherwise vanish
        foreach (var orphan in def.Abilities.Where(a => a.ParentKey is not null && def.Abilities.All(t => t.Key != a.ParentKey)))
            SimAbilities.Add(MakeRow(orphan, true));
        HasSimulation = SimAbilities.Count > 0;
        SimClientTask = ApplyClientTextAsync(def);
        SimSelected = SimAbilities.FirstOrDefault(r => r.Key == "Q") ?? SimAbilities.FirstOrDefault();
        int ok = SimAbilities.Count(r => r.IsComplete), partial = SimAbilities.Count(r => r.IsIncomplete), blocked = SimAbilities.Count(r => r.IsBlocked);
        SimSummary = $"{def.DisplayName ?? def.Champion}: {SimAbilities.Count} abilities - {ok} ok, {partial} partial, {blocked} blocked (asset check, game {def.GameVersion})";
        OnPropertyChanged(nameof(SimSpeedIndex));
    }

    private ClientChampionDetails? _simClient;

    /// <summary>The client text fetch started by the last <see cref="SetSimulationDefinition"/>; a test awaits it.</summary>
    public Task SimClientTask { get; private set; } = Task.CompletedTask;

    /// <summary>M870: the ability bar's descriptions (and icons the definition lacks) come from the client champion JSON. Reading it opens
    /// the client WAD, so it is done on a worker and the rows are filled in when it arrives; the bar is usable meanwhile.</summary>
    private async Task ApplyClientTextAsync(ChampionSimDefinition def)
    {
        if (Browser is not { } browser) return;
        ClientChampionDetails? details = null;
        try
        {
            var client = browser.ClientData;
            details = await Task.Run(() => client.Champion(def.Champion) is { } champion ? client.GetDetails(champion.Id) : null);
        }
        catch { /* no client text: the bar keeps its names and key letters */ }
        if (details is null || !ReferenceEquals(_simDef, def)) return;      // another champion took over meanwhile
        _simClient = details;
        foreach (var row in SimAbilities)
        {
            string? slot = SlotLetter(row.Key) ?? SlotLetter(row.Ability.ParentKey);
            var ability = slot is null ? null : details.Ability(slot);
            string? parentIcon = row.Ability.ParentKey is null ? null : def.Abilities.FirstOrDefault(p => p.Key == row.Ability.ParentKey)?.IconPath;
            string? iconPath = row.Ability.IconPath ?? ability?.IconPath ?? parentIcon;
            row.ApplyClient(ability?.Description, iconPath is not null && browser.Images is { } images ? new ClientImageHandle(images, iconPath, 72) : null);
        }
    }

    private static string? SlotLetter(string? key) =>
        key is { Length: > 0 } && "PQWER".IndexOf(key[0]) >= 0 && (key.Length == 1 || !char.IsLetter(key[1])) ? key[..1] : null;

    private SimAbilityRowViewModel MakeRow(AbilitySim a, bool isSub)
    {
        var verdict = _simVerdict!.Abilities.FirstOrDefault(v => v.Key == a.Key)
                      ?? new AbilityValidation(a.Key, SimStatus.Incomplete, Array.Empty<ValidationCheck>(), Array.Empty<string>());
        string? parentName = a.ParentKey is null ? null : _simDef!.Abilities.FirstOrDefault(p => p.Key == a.ParentKey)?.DisplayName;
        // the client ability of this slot (a sub-cast or a form variant takes its parent's): description, and the icon when the
        // definition has none. A sub-cast with no icon of its own shows its parent's.
        string? slot = SlotLetter(a.Key) ?? SlotLetter(a.ParentKey);
        var client = slot is null ? null : _simClient?.Ability(slot);
        string? parentIcon = a.ParentKey is null ? null : _simDef!.Abilities.FirstOrDefault(p => p.Key == a.ParentKey)?.IconPath;
        string? iconPath = a.IconPath ?? client?.IconPath ?? parentIcon;
        var icon = iconPath is not null && Browser?.Images is { } images ? new ClientImageHandle(images, iconPath, 72) : null;
        return new SimAbilityRowViewModel(a, verdict, isSub, row => PlaySimulation(row.Ability), parentName, icon, client?.Description);
    }

    // ---- controls and status ----

    public static readonly double[] SimSpeeds = { 0.25, 0.5, 1.0, 1.5, 2.0 };
    public IReadOnlyList<string> SimSpeedLabels { get; } = new[] { "0.25x", "0.5x", "1x", "1.5x", "2x" };

    [ObservableProperty] private double _simSpeed = 1.0;
    [ObservableProperty] private bool _simPaused;
    [ObservableProperty] private bool _simRunning;
    [ObservableProperty] private double _simTime;
    [ObservableProperty] private string _simCurrentAbility = "";
    [ObservableProperty] private string _simCurrentStep = "";
    [ObservableProperty] private string _simStatusLine = "";
    [ObservableProperty] private string _simNotes = "";

    /// <summary>The last ~8 events of the run, oldest first: "0.61 s Kalista_Base_Q_mis hits enemy".</summary>
    public ObservableCollection<string> SimLog { get; } = new();
    private const int SimLogLines = 8;

    public int SimSpeedIndex
    {
        get => Math.Max(0, Array.FindIndex(SimSpeeds, s => Math.Abs(s - SimSpeed) < 1e-6));
        set { if (value >= 0 && value < SimSpeeds.Length) SimSpeed = SimSpeeds[value]; }
    }

    /// <summary>What the D3D11 surface multiplies its live particle step by: zero while paused, else the speed.</summary>
    public double SimParticleTimeScale => !SimRunning ? 1.0 : SimPaused ? 0.0 : SimSpeed;

    /// <summary>What the GL control's ParticleSpeed is bound to: the chosen speed during a run, neutral otherwise.</summary>
    public double SimEffectiveSpeed => SimRunning ? SimSpeed : 1.0;

    /// <summary>What the GL control's ParticlePaused is bound to.</summary>
    public bool SimEffectivePaused => SimRunning && SimPaused;

    private double _simSavedAnimationSpeed = 1.0;
    private bool _simHoldsAnimationSpeed;

    /// <summary>True while a run is active: the GL control then puts the effects on real time (see ViewportControl.ParticleFreeClock).</summary>
    public bool SimDrivesParticles => SimRunning;

    partial void OnSimSpeedChanged(double value)
    {
        if (SimRunning) Animation.Speed = value;          // the chosen speed is only in force while a run is
        OnPropertyChanged(nameof(SimSpeedIndex));
        OnPropertyChanged(nameof(SimParticleTimeScale));
        OnPropertyChanged(nameof(SimEffectiveSpeed));
    }

    partial void OnSimPausedChanged(bool value)
    {
        OnPropertyChanged(nameof(SimParticleTimeScale));
        OnPropertyChanged(nameof(SimEffectivePaused));
        if (value) Animation.Pause();
        else if (SimRunning && Animation.HasClip) Animation.Play();
        if (SimRunning) UpdateSimStatus();
        SimPauseLabel = value ? "Resume" : "Pause";
    }

    [ObservableProperty] private string _simPauseLabel = "Pause";

    partial void OnSimRunningChanged(bool value)
    {
        // The speed belongs to the run: take the clip's speed when it starts, give the user's back when it ends.
        if (value && !_simHoldsAnimationSpeed)
        {
            _simSavedAnimationSpeed = Animation.Speed;
            _simHoldsAnimationSpeed = true;
            Animation.Speed = SimSpeed;
        }
        else if (!value && _simHoldsAnimationSpeed)
        {
            _simHoldsAnimationSpeed = false;
            Animation.Speed = _simSavedAnimationSpeed;
        }
        OnPropertyChanged(nameof(SimDrivesParticles));
        OnPropertyChanged(nameof(SimParticleTimeScale));
        OnPropertyChanged(nameof(SimEffectiveSpeed));
        OnPropertyChanged(nameof(SimEffectivePaused));
    }

    /// <summary>The ally dummy's position while an ability needs one; null otherwise. Drawn as a second instance of the dummy mesh.</summary>
    [ObservableProperty] private Vector3? _simAllyPosition;

    partial void OnSimAllyPositionChanged(Vector3? value)
    {
        OnPropertyChanged(nameof(CameraFocus));
        RebuildSceneProps();
    }

    // ---- a run ----

    private sealed class SimRun
    {
        public required AbilitySim Ability { get; init; }
        public required SimScript Script { get; init; }
        public int Next;
        public double Clock;
        public SimStep? Displace;
        public int Hits;
    }

    private SimRun? _simRun;
    private readonly Queue<AbilitySim> _simQueue = new();
    /// <summary>What Replay runs: exactly what the user last asked for - one ability, a wrapper's whole sub-cast sequence, or the showcase.</summary>
    private readonly List<AbilitySim> _simReplay = new();
    private bool _simReplayShowcase;
    private double _simGap;
    private bool _simShowcase;
    private DispatcherTimer? _simTimer;
    private DateTime _simLastTick;

    /// <summary>The wall-clock-free gap between two abilities of a showcase: the scene resets, then the next one starts.</summary>
    public const double ShowcaseGapSeconds = 0.5;

    /// <summary>The keys of every ability a run STARTED, in order, since <see cref="ClearSimulationHistory"/>. For the status line and tests.</summary>
    public List<string> SimVisited { get; } = new();

    /// <summary>How many times the scene has been put back (between abilities, at the end, on Stop).</summary>
    public int SimResetCount { get; private set; }

    public SimScript? SimCurrentScript => _simRun?.Script;
    public double SimClock => _simRun?.Clock ?? 0.0;

    /// <summary>One executed step: which ability, which step, when the script planned it and where the simulation clock stood when it ran.</summary>
    public sealed record SimTraceEntry(string Key, SimStepKind Kind, float Planned, double Clock);

    /// <summary>Every step executed since the last user-initiated start (Play, Showcase, Replay), in order.</summary>
    public List<SimTraceEntry> SimTrace { get; } = new();

    public void ClearSimulationHistory() { SimVisited.Clear(); SimResetCount = 0; SimTrace.Clear(); }

    /// <summary>Play one ability. A wrapper entry that only owns sub-casts (Aatrox Q) plays its sub-casts in order.</summary>
    public void PlaySimulation(AbilitySim ability)
    {
        if (_simDef is null) return;
        _simQueue.Clear();
        _simShowcase = false;
        SimLog.Clear(); SimTrace.Clear();
        _simReplay.Clear(); _simReplayShowcase = false;
        if (IsWrapper(ability))
        {
            foreach (var sub in SubCastsOf(ability)) _simReplay.Add(sub);
            if (_simReplay.Count > 0) { StartReplayList(); return; }
        }
        _simReplay.Add(ability);
        StartReplayList();
    }

    public void PlaySimulation(string key)
    {
        if (_simDef?.Abilities.FirstOrDefault(a => a.Key == key) is { } a) PlaySimulation(a);
    }

    [RelayCommand]
    private void PlaySimulationSelected()
    {
        if (SimSelected is { } row) PlaySimulation(row.Ability);
    }

    /// <summary>P, Q, W, E, R, then the extras - each one followed by its own sub-casts - with the scene reset between abilities.</summary>
    [RelayCommand]
    public void ShowcaseSimulation()
    {
        if (_simDef is not { } def) return;
        _simReplay.Clear();
        foreach (var top in def.Abilities.Where(a => a.ParentKey is null))
        {
            if (!IsWrapper(top)) _simReplay.Add(top);
            foreach (var sub in SubCastsOf(top)) _simReplay.Add(sub);
        }
        _simReplayShowcase = true;
        SimLog.Clear(); SimTrace.Clear();
        StartReplayList();
    }

    /// <summary>Run the list in <see cref="_simReplay"/> from the first entry: the rest wait in the queue.</summary>
    private void StartReplayList()
    {
        _simQueue.Clear();
        _simShowcase = _simReplayShowcase;
        for (int i = 1; i < _simReplay.Count; i++) _simQueue.Enqueue(_simReplay[i]);
        if (_simReplay.Count > 0) BeginRun(_simReplay[0]);
    }

    /// <summary>Run what the user last asked for again from a clean scene (an ability, a wrapper's sub-casts, or the showcase).
    /// The plan is deterministic, so the run is identical.</summary>
    [RelayCommand]
    public void ReplaySimulation()
    {
        if (_simReplay.Count == 0 || _simDef is null) return;
        SimLog.Clear(); SimTrace.Clear();
        StartReplayList();
    }

    /// <summary>The Stop / Reset button: stops a run, and puts the scene back even when nothing is running (the dummy may have been dragged).</summary>
    [RelayCommand]
    public void StopSimulationAndReset()
    {
        bool had = _simRun is not null || SimRunning || _simQueue.Count > 0;
        StopSimulation();
        if (!had)
        {
            RestoreScene();
            SimStatusLine = "Scene reset.";
        }
    }

    /// <summary>Stop the run and put the scene back: playback, sounds, queue, positions, ally. Safe at any time; does nothing when idle.</summary>
    public void StopSimulation()
    {
        bool had = _simRun is not null || SimRunning || _simQueue.Count > 0;
        _simQueue.Clear();
        _simShowcase = false;
        _simGap = 0;
        _simRun = null;
        SetRowRunning(null);
        SimRunning = false;
        SimPaused = false;
        _simTimer?.Stop();
        SimTime = 0;
        SimCurrentAbility = "";
        SimCurrentStep = "";
        if (had)
        {
            RestoreScene();
            SimStatusLine = "Stopped - scene reset.";
            AddSimLogLine("stopped");
        }
    }

    [RelayCommand]
    private void ToggleSimulationPause()
    {
        if (!SimRunning) return;
        SimPaused = !SimPaused;
    }

    private bool IsWrapper(AbilitySim a) =>
        a.Clip is null && a.CasterSystems.Count == 0 && a.TargetSystems.Count == 0 && a.Missiles.Count == 0
        && a.ClipParticleEvents.Count == 0 && SubCastsOf(a).Any();

    private IEnumerable<AbilitySim> SubCastsOf(AbilitySim a) =>
        _simDef?.Abilities.Where(s => s.ParentKey == a.Key) ?? Enumerable.Empty<AbilitySim>();

    private void BeginRun(AbilitySim ability)
    {
        if (_simDef is not { } def) return;
        if (SimPaused) SimPaused = false;                  // a new run starts moving; a leftover pause would freeze it at t=0
        if (!_simSceneClean) RestoreScene();               // a clean slate: nothing from the last run may reach this one
        var stage = _stage ?? ArenaStage.Origin;
        var nav = _arena?.Nav;
        var plan = SimulationPlanner.Plan(def, ability,
            new SimStage(stage.Champion, stage.Yaw, stage.Dummy, null, nav is null ? null : p => NavGridPath.GroundHeight(nav, p), nav,
                nav is null ? null : p => NavGridPath.IsWalkable(nav, p)));

        // Place: champion, enemy dummy, ally dummy; then frame all of them
        _controller.Teleport(plan.ChampionPosition, plan.ChampionYaw);
        CharacterPosition = plan.ChampionPosition;
        CharacterYaw = plan.ChampionYaw;
        if (!TargetDummyEnabled) TargetDummyEnabled = true;
        DummyX = plan.EnemyPosition.X; DummyY = plan.EnemyPosition.Y; DummyZ = plan.EnemyPosition.Z;
        SimAllyPosition = plan.AllyPosition;
        RebuildDummyProps();
        FrameScene();

        _simSceneClean = false;
        _castPlan = BuildSimCastPlan(ability, plan);
        // the target systems play when the ability reaches the enemy, not at the start of the cast
        _simHitDelay = plan.Find(SimStepKind.Hit) is { } hitStep ? hitStep.Time : null;
        _castBusyUntil = DateTime.UtcNow.AddHours(1);     // a cast owns the animation: the control tick must not stamp the idle over it
        Animation.Loop = false;                            // one pass: a wrap would replay the effects

        _simRun = new SimRun { Ability = ability, Script = plan };
        SimVisited.Add(ability.Key);
        SetRowRunning(ability.Key);
        SimRunning = true;
        SimTime = 0;
        SimCurrentAbility = DescribeAbility(ability);
        SimNotes = plan.Notes.Count == 0 ? "" : string.Join("\n", plan.Notes);
        _simGap = 0;
        EnsureSimTimer();
        ProcessDueSteps();
        UpdateSimStatus();
    }

    private string DescribeAbility(AbilitySim a) => a.DisplayName is { Length: > 0 } n ? $"{a.Key} - {n}" : a.Key;

    private void SetRowRunning(string? key)
    {
        foreach (var r in SimAbilities) r.IsRunning = key is not null && r.Key == key;
    }

    private void EnsureSimTimer()
    {
        _simTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        if (_simTickAttached is false) { _simTimer.Tick += (_, _) => TimerTick(); _simTickAttached = true; }
        _simLastTick = DateTime.UtcNow;
        _simTimer.Start();
    }
    private bool _simTickAttached;

    private void TimerTick()
    {
        var now = DateTime.UtcNow;
        // No clamp: the clip's own timer does not clamp either, so after a stall the pose and the simulation clock stay together
        // (the particle simulators cap their own step, so effects may trail by the stall).
        double dt = Math.Max(0.0, (now - _simLastTick).TotalSeconds);
        _simLastTick = now;
        AdvanceSimulation(dt);
    }

    /// <summary>
    /// Advance the simulation by <paramref name="wallSeconds"/> of real time. The simulation clock moves by that times
    /// <see cref="SimSpeed"/>, and not at all while paused. The dispatcher timer calls this every ~16 ms; a test calls it directly
    /// with a chosen step, which is what makes a run reproducible.
    /// </summary>
    public void AdvanceSimulation(double wallSeconds)
    {
        if (!SimRunning || SimPaused) return;
        double dt = Math.Max(0.0, wallSeconds) * SimSpeed;

        if (_simRun is null)
        {
            // between two abilities of a showcase: the scene was reset, the next one starts when the gap is spent
            _simGap -= dt;
            if (_simGap <= 0 && _simQueue.Count > 0) BeginRun(_simQueue.Dequeue());
            return;
        }

        var run = _simRun;
        run.Clock += dt;
        SimTime = run.Clock;
        ProcessDueSteps();
        if (_simRun is null) return;           // the End step finished the run
        ApplyDisplacement(run);
        UpdateSimStatus();
    }

    private void ProcessDueSteps()
    {
        while (_simRun is { } run && run.Next < run.Script.Steps.Count && run.Script.Steps[run.Next].Time <= run.Clock + 1e-9)
        {
            var step = run.Script.Steps[run.Next++];
            Execute(run, step);
        }
    }

    private void Execute(SimRun run, SimStep step)
    {
        var a = run.Ability;
        SimTrace.Add(new SimTraceEntry(a.Key, step.Kind, step.Time, run.Clock));
        string tag = step.Source == "authored" ? "" : $" [{step.Source}]";
        SimCurrentStep = $"{step.Kind}: {step.Text} [{step.Source}]";
        switch (step.Kind)
        {
            case SimStepKind.Place:
                AddSimLog(step.Time, $"{a.Key} placed{(run.Script.AllyPosition is null ? "" : " with an ally")}");
                break;
            case SimStepKind.Clip:
                AddSimLog(step.Time, step.Text + tag);
                break;
            case SimStepKind.Composite:
                PlayComposite(a, run.Script);
                AddSimLog(step.Time, $"{a.Key} cast plays" + tag);
                break;
            case SimStepKind.Displace:
                if (step.Displacement == SimDisplaceKind.Blink)
                {
                    MoveCaster(step.To!.Value, run.Script.ChampionYaw);
                    AddSimLog(step.Time, step.Text + tag);
                }
                else
                {
                    run.Displace = step;
                    AddSimLog(step.Time, step.Text + tag);
                }
                break;
            case SimStepKind.Channel:
                AddSimLog(step.Time, step.Text + tag);
                break;
            case SimStepKind.Hit:
                run.Hits++;
                AddSimLog(step.Time, step.Text + tag);
                break;
            case SimStepKind.End:
                AddSimLog(step.Time, $"{a.Key} done" + tag);
                FinishRun(run);
                break;
        }
    }

    /// <summary>Move the caster along an active dash: linear from its start to its end over [t0, t1], on the ground the navgrid reports.</summary>
    private void ApplyDisplacement(SimRun run)
    {
        if (run.Displace is not { } d) return;
        double span = Math.Max(1e-6, d.EndTime - d.Time);
        float f = (float)Math.Clamp((run.Clock - d.Time) / span, 0.0, 1.0);
        var p = Vector3.Lerp(d.From!.Value, d.To!.Value, f);
        if (_arena?.Nav is { } nav) p.Y = NavGridPath.GroundHeight(nav, p);
        MoveCaster(p, run.Script.ChampionYaw);
        if (f >= 1f) run.Displace = null;
    }

    private void MoveCaster(Vector3 position, float yaw)
    {
        _controller.Teleport(position, yaw);               // the control tick reads the controller back into CharacterPosition
        CharacterPosition = position;
        CharacterYaw = yaw;
    }

    // ---- the cast: the same path the Animate tab's EVENTS take ----

    private void PlayComposite(AbilitySim ability, SimScript script)
    {
        var ev = FindEventFor(ability);
        string? clipFile = ability.Clip?.AnmPath is { Length: > 0 } p ? System.IO.Path.GetFileName(p.Replace('\\', '/')) : null;
        bool Has(string? file) => file is not null && Animation.Animations.Any(e => string.Equals(e.Name, file, StringComparison.OrdinalIgnoreCase));

        // The composite's own clip is this SKIN's (the event builder matched it against the loaded skin's clips); the definition's is
        // the base skin's. The skin's wins; the definition's is the fallback when the event names none.
        string? file = ev?.ClipAnmFile is { } own && Has(own) ? own : Has(clipFile) ? clipFile : ev?.ClipAnmFile;
        if (ev is null && file is null) return;           // no composite and no clip in this skin: the rest of the script still runs

        var play = ev is null
            ? new Formats.Vfx.ChampionEvent { Name = ability.Key, ClipAnmFile = file }
            : new Formats.Vfx.ChampionEvent
            {
                Name = ev.Name, ClipAnmFile = file,
                CasterSystems = ev.CasterSystems, TargetSystems = ev.TargetSystems,
                MissileSystems = ev.MissileSystems, ReturnMissileSystems = ev.ReturnMissileSystems,
            };
        PlayEvent(play);
        RepublishPlayback();                               // the skin's idle effects play under the cast, as in game
    }

    /// <summary>The M116 composite for an ability key: its own name, or its form variant ("Q (assassin)"). A sub-cast is only ever its own number.</summary>
    private Formats.Vfx.ChampionEvent? FindEventFor(AbilitySim ability)
    {
        var exact = ChampionEvents.FirstOrDefault(e => string.Equals(e.Name, ability.Key, StringComparison.OrdinalIgnoreCase) && e.HasSystems)
                    ?? ChampionEvents.FirstOrDefault(e => string.Equals(e.Name, ability.Key, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        if (ability.ParentKey is not null || ability.Key.StartsWith("X:", StringComparison.Ordinal)) return null;
        return ChampionEvents.FirstOrDefault(e => e.Name.StartsWith(ability.Key + " ", StringComparison.OrdinalIgnoreCase) && e.HasSystems);
    }

    /// <summary>
    /// The aim the composite is built around. Q/W/E/R use <see cref="SpellAim"/> with the record's slot, as a player's cast does;
    /// the passive, a sub-cast and an extra have no slot, so the plan comes straight from the script.
    /// </summary>
    private CastPlan BuildSimCastPlan(AbilitySim ability, SimScript script)
    {
        int slot = ability.Key switch { "Q" => 0, "W" => 1, "E" => 2, "R" => 3, _ => -1 };
        var unit = script.AimAt == SimAimTarget.Ally && script.AllyPosition is { } ally ? ally : script.EnemyPosition;
        if (slot >= 0 && _abilities.FirstOrDefault(s => s.Index == slot) is { } record)
        {
            var spell = SpellAim.Plan(record, script.ChampionPosition, script.AimAt == SimAimTarget.Caster ? script.ChampionPosition : script.Aim, unit);
            if (spell.WalkTo is null)
                // a missile that stops at the first unit ends at the enemy and the impact plays there
                return script.Find(SimStepKind.Hit) is { StopsMissile: true } ? spell with { TravelTo = unit, HitAt = unit } : spell;
        }
        var hit = script.Find(SimStepKind.Hit) is not null ? unit : (Vector3?)null;
        bool flies = ability.Missiles.Count > 0;
        return new CastPlan(ability.TargetingKind, script.Aim, flies ? (hit ?? script.Aim) : null, hit, null,
            $"{ability.Key}: simulation plan ({script.AimAt.ToString().ToLowerInvariant()})");
    }

    // ---- the end ----

    private void FinishRun(SimRun run)
    {
        _simRun = null;
        RestoreScene();
        SetRowRunning(null);
        SimTime = run.Script.EndTime;
        if (_simQueue.Count > 0)
        {
            _simGap = ShowcaseGapSeconds;
            SimStatusLine = _simShowcase ? $"{run.Ability.Key} done - next: {_simQueue.Peek().Key}" : $"{run.Ability.Key} done - next sub-cast {_simQueue.Peek().Key}";
            SimCurrentStep = "reset";
            return;
        }
        _simShowcase = false;
        SimRunning = false;
        SimPaused = false;
        _simTimer?.Stop();
        SimCurrentStep = "done";
        SimStatusLine = $"{run.Ability.Key} done at {run.Script.EndTime.ToString("0.00", CultureInfo.InvariantCulture)} s.";
    }

    /// <summary>
    /// Put everything a run touched back: the clip, the composite, the effects and the scheduled sounds stop; the champion, the enemy
    /// dummy and the camera return to the stage; the ally dummy is removed; the cast plan and the animation hold are dropped; the
    /// idle pose and the skin's idle effects come back. Idempotent.
    /// </summary>
    private void RestoreScene()
    {
        _castPlan = null;
        _simHitDelay = null;
        ResetScene();                                      // StopEvent, stage positions, idle pose, camera
        SimAllyPosition = null;
        Animation.Loop = true;                             // the idle clip loops
        RepublishPlayback();                               // StopEvent cleared the playback, idle effects included
        SimResetCount++;
        _simSceneClean = true;
    }

    private bool _simSceneClean;

    /// <summary>Seconds after the cast starts at which the planned hit lands; the composite's target systems are delayed to it. Null outside a run.</summary>
    private float? _simHitDelay;

    // ---- the status line ----

    private void UpdateSimStatus()
    {
        if (_simRun is not { } run) return;
        string line = string.Format(CultureInfo.InvariantCulture, "{0}  |  {1}  |  {2:0.00} / {3:0.00} s",
            SimCurrentAbility, SimCurrentStep, run.Clock, run.Script.EndTime);
        if (SimPaused) line += "  (paused)";
        if (Math.Abs(SimSpeed - 1.0) > 1e-6) line += string.Format(CultureInfo.InvariantCulture, "  x{0:0.##}", SimSpeed);
        SimStatusLine = line;
    }

    private void AddSimLog(float time, string text) =>
        AddSimLogLine(time.ToString("0.00", CultureInfo.InvariantCulture) + " s  " + text);

    private void AddSimLogLine(string line)
    {
        SimLog.Add(line);
        while (SimLog.Count > SimLogLines) SimLog.RemoveAt(0);
    }
}
