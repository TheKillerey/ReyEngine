using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Formats.Materials;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M647: one condition the loaded skin's material drivers ask about, as a switch.
///
/// <para>Not a hardcoded list of champion states: the conditions come out of the skin's own
/// DynamicMaterialDefs, so Locke offers Dead and LockeW, Aatrox offers Dead, AatroxR, taunt and the rest
/// of his, and a skin with no drivers offers nothing and the whole card stays hidden.</para>
/// </summary>
public sealed partial class CharacterConditionViewModel : ViewModelBase
{
    private readonly Action _changed;

    public CharacterConditionViewModel(MaterialDriverCondition model, float transitionSeconds, Action changed)
    {
        Model = model;
        TransitionSeconds = transitionSeconds;
        _changed = changed;
    }

    /// <summary>The longest ramp among the parameters that ask this condition - how long the game takes
    /// to finish the change once it turns on.</summary>
    public float TransitionSeconds { get; }

    public MaterialDriverCondition Model { get; }
    public string Key => Model.Key;
    public string Label => Model.Label;
    public string Kind => Model.Kind;
    public string Tip => $"Draw the skin as if {Model.Describe}. The condition is {Model.Key} - every "
                         + "material of this skin that asks it follows this switch.";

    [ObservableProperty] private bool _isOn;

    partial void OnIsOnChanged(bool value) => _changed();
}

/// <summary>
/// M647: the character editor's state switch - lebend / tot / W-Form, and whatever else the skin names.
///
/// <para>A champion's materials do not draw what their paramValues say. Locke authors
/// <c>VCDissolve_Value = 1.23</c> and drives it from an IsDead lerp to -0.8 while alive; his
/// <c>Transition_Value</c> rides his W buff. Until now the preview could only show one of those - the
/// resting one - so his W form and his death dissolve were invisible to the editor even though the data
/// for both is right there in the bin.</para>
///
/// <para>The seconds are part of the state, not a decoration. Riot's lerps have their own durations
/// (Locke's death dissolve runs 8 s, Aatrox's 4 s), and held at the end of that ramp the dissolve is
/// complete and the champion is gone - measured, 108,824 covered pixels down to 39,689. So flipping a
/// switch parks the clock halfway through THAT condition's own ramp, where the change is visibly under
/// way, and the slider walks the rest. A fixed fraction of the skin's longest ramp was tried first and is
/// wrong in both directions: a quarter of Locke's 10 s moved 2,621 pixels (it looks like the switch did
/// nothing) and half of it had already taken his head.</para>
/// </summary>
public sealed partial class MeshPreviewViewModel
{
    /// <summary>The conditions this skin names, each a switch. Empty for a skin without drivers.</summary>
    public ObservableCollection<CharacterConditionViewModel> Conditions { get; } = new();

    [ObservableProperty] private bool _hasConditions;

    /// <summary>How long the active conditions have held. Drives each lerp's position along its own
    /// turn-on time.</summary>
    [ObservableProperty] private double _stateSeconds;

    /// <summary>The end of the longest transition in this skin - the slider's range.</summary>
    [ObservableProperty] private double _stateSecondsMax = 1;

    /// <summary>Set while a scene is being applied, so seeding the switches does not ask for a rebuild
    /// once per switch.</summary>
    private bool _applyingConditions;

    /// <summary>True once the user has moved the slider themselves - after that the clock is theirs and
    /// flipping a switch no longer repositions it.</summary>
    private bool _secondsChosen;

    /// <summary>
    /// Where in a condition's ramp to park the clock when its switch is flipped. A taste call, not a fact
    /// about the game, and picked by looking: Locke's five materials ramp their death dissolve over 6, 8
    /// and 10 s, so the condition's longest is 10 s, and rendering him at a run of clock positions gives
    /// 108,824 covered pixels alive, 108,825 at 2 s and 3 s (the switch looks broken), 97,023 at 4 s (the
    /// dissolve is visibly climbing him), 59,841 at 5 s (his head has gone) and 39,689 held to the end.
    /// 4 s of 10 is where a death reads as a death in progress. The slider sits right next to it.
    /// </summary>
    private const double ParkFraction = 0.4;

    /// <summary>The host rebuilds the D3D11 scene for the new state. Null until wired.</summary>
    public Action? RequestDriverState { get; set; }

    /// <summary>The situation to draw.</summary>
    public MaterialDriverState DriverState =>
        new(Conditions.Where(c => c.IsOn).Select(c => c.Key), (float)StateSeconds);

    public bool IsRestState => !Conditions.Any(c => c.IsOn);

    /// <summary>A line under the switches: what is being drawn, and how far into it.</summary>
    public string StateSummary
    {
        get
        {
            var on = Conditions.Where(c => c.IsOn).Select(c => c.Label).ToList();
            if (on.Count == 0) return "Alive, unbuffed, idle - what the skin draws at rest.";
            return string.Join(" + ", on) + $", {StateSeconds:0.##}s in."
                   + (StateSeconds <= 0 ? " At 0s nothing has transitioned yet - drag the slider." : "");
        }
    }

    partial void OnStateSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(StateSummary));
        if (_applyingConditions) return;
        _secondsChosen = true;   // moved from the UI: the clock is the user's now
        RequestDriverState?.Invoke();
    }

    /// <summary>A switch moved: park the clock halfway through the ramp of whatever is now on (unless the
    /// user has taken the slider over), re-summarise, and ask the host for a scene in the new state.</summary>
    private void ConditionChanged()
    {
        if (!_applyingConditions && !_secondsChosen)
        {
            float ramp = Conditions.Where(c => c.IsOn).Select(c => c.TransitionSeconds).DefaultIfEmpty(0f).Max();
            double park = ramp > 0f ? ramp * ParkFraction : StateSecondsMax * ParkFraction;
            if (Math.Abs(park - StateSeconds) > 0.001)
            {
                _applyingConditions = true;
                StateSeconds = park;
                _applyingConditions = false;
            }
        }
        OnPropertyChanged(nameof(StateSummary));
        OnPropertyChanged(nameof(IsRestState));
        if (!_applyingConditions) RequestDriverState?.Invoke();
    }

    /// <summary>Back to rest - alive, unbuffed, idle.</summary>
    [RelayCommand]
    private void ResetState()
    {
        if (IsRestState) return;
        _applyingConditions = true;
        foreach (var c in Conditions) c.IsOn = false;
        _applyingConditions = false;
        ConditionChanged();
    }

    /// <summary>
    /// The switches for a freshly loaded skin. Conditions already on are KEPT when the same skin comes
    /// back - a material edit rebuilds the scene, and a rebuild that silently revived the corpse would
    /// undo the state the user is looking at.
    /// </summary>
    public void SetConditions(IReadOnlyList<MaterialDriverCondition> conditions, float longestTransition,
        IReadOnlyDictionary<string, float>? ramps = null)
    {
        ramps ??= new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var wasOn = Conditions.Where(c => c.IsOn).Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _applyingConditions = true;
        Conditions.Clear();
        foreach (var c in conditions)
            Conditions.Add(new CharacterConditionViewModel(c, ramps.GetValueOrDefault(c.Key), ConditionChanged)
            {
                IsOn = wasOn.Contains(c.Key),
            });
        HasConditions = Conditions.Count > 0;

        // The slider covers the longest ramp in the skin; where it sits is decided per switch, in
        // ConditionChanged. A different skin is a different clock, so the user's choice does not carry over.
        double max = Math.Max(1, Math.Ceiling(longestTransition));
        if (Math.Abs(max - StateSecondsMax) > 0.001) { StateSecondsMax = max; _secondsChosen = false; StateSeconds = 0; }
        _applyingConditions = false;

        OnPropertyChanged(nameof(StateSummary));
        OnPropertyChanged(nameof(IsRestState));
    }
}
