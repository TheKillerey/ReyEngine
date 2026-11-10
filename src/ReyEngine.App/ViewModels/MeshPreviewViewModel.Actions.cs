using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Formats.Characters;

namespace ReyEngine.App.ViewModels;

/// <summary>One row of the ACTIONS list.</summary>
public sealed class CharacterActionRowViewModel(CharacterAction action)
{
    public CharacterAction Action { get; } = action;
    public string Label { get; } = action.Label;
    public bool HasClip => Action.HasClip;

    /// <summary>The group heading this row belongs under. Abilities first because they are what anyone
    /// opens a character to look at.</summary>
    public string Group { get; } = action.Kind switch
    {
        CharacterActionKind.Ability => "Abilities",
        CharacterActionKind.Attack or CharacterActionKind.Movement => "Basics",
        CharacterActionKind.Recall or CharacterActionKind.Idle or CharacterActionKind.Death => "States",
        CharacterActionKind.Emote => "Emotes",
        _ => "Other",
    };

    /// <summary>What this row will actually play, or why it will not.</summary>
    public string Detail
    {
        get
        {
            if (!Action.HasClip) return "no animation in this skin";
            var parts = new List<string> { System.IO.Path.GetFileNameWithoutExtension(Action.Clip!.AnmPath.Replace('\\', '/')) };
            if (Action.Variants.Count > 0) parts.Add($"+{Action.Variants.Count} variant(s)");
            if (Action.ParticleEvents.Count > 0) parts.Add($"{Action.ParticleEvents.Count} vfx");
            if (Action.SoundEvents.Count > 0) parts.Add($"{Action.SoundEvents.Count} sfx");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// M612: the ACTIONS list in the preview — Q/W/E/R, attack, move, recall, emotes.
///
/// <para>The ANIMATIONS list is 55 file names in alphabetical order and the EVENTS list is whatever
/// Riot's VFX naming happened to reveal. Neither answers "show me this champion's R", which is the
/// question the preview exists to answer.</para>
///
/// <para>Playing an action reuses what is already here rather than growing a second pipeline: when the
/// VFX-name-driven event list (M116) has a composite for the same slot, that event is played, so the
/// caster/missile/target systems land where they belong. When it does not — which is most of Movement,
/// Recall and the emotes — the clip is played directly, and the particle and sound events authored INTO
/// the clip still fire, because that is the same path the animation list uses.</para>
/// </summary>
public sealed partial class MeshPreviewViewModel
{
    private readonly Dictionary<string, int> _nextAbilityCast = new(StringComparer.OrdinalIgnoreCase);
    public ObservableCollection<CharacterActionRowViewModel> Actions { get; } = new();

    [ObservableProperty] private CharacterActionRowViewModel? _selectedAction;
    [ObservableProperty] private bool _hasActions;
    [ObservableProperty] private string _actionSummary = "";

    /// <summary>Populate from the champion record and this skin's clips. Empty for anything that is not
    /// a character — props and map meshes have no abilities.</summary>
    public void SetActions(IReadOnlyList<CharacterAction> actions)
    {
        Actions.Clear();
        _nextAbilityCast.Clear();
        SelectedAction = null;
        foreach (var action in actions) Actions.Add(new CharacterActionRowViewModel(action));

        HasActions = Actions.Count > 0;
        int playable = Actions.Count(a => a.HasClip);
        ActionSummary = Actions.Count == 0
            ? ""
            : playable == Actions.Count
                ? $"{Actions.Count} actions"
                : $"{playable} of {Actions.Count} actions have an animation in this skin";
    }

    /// <summary>M886: after a skin loads, start its idle, looped - the game never shows a champion in bind pose.
    /// The clip is what the skin's OWN graph answers for "Idle1" (a composite resolves to its first atomic clip,
    /// as M873 does for spells), else the base-idle pick over the same graph's clips. Nothing happens without a
    /// skeleton, without a resolvable idle in the list, or when a clip is already selected (the user's or a
    /// simulation's). Runs after SetSubmeshRules, so the base visibility is already applied when the clip's
    /// events arrive (M884). Returns true when an idle was started.</summary>
    public bool PlayLoadedIdle()
    {
        _introEntry = null; _introFollowUp = null;
        if (!Animation.HasSkeleton || Animation.SelectedAnimation is not null || Animation.HasClip) return false;
        if (Animation.Animations.Count == 0 || _ownClips.Count == 0) return false;
        var info = Formats.Skeletons.AnimationClipGraph.Resolve("Idle1", _ownClips, _ownNodes)?.First
                   ?? Formats.Characters.PropAnimations.PickIdle(_ownClips);
        if (info is null) return false;
        var entry = EntryFor(info);
        if (entry is null) return false;

        // M887: CONVENTION, not graph data - "spawn intro". The graph does not chain Idle_In to Idle1, but a skin
        // whose idle shows no visibility events (Ahri skin97: small tails hidden only by Idle_In's events) expects
        // the intro to have run first; the M884 carry-over then keeps the state it leaves. Played once, then Idle1.
        var intro = Formats.Skeletons.AnimationClipGraph.Resolve("Idle_In", _ownClips, _ownNodes)?.First is { } introInfo
            ? EntryFor(introInfo) : null;
        if (intro is not null && !ReferenceEquals(intro, entry))
        {
            _introEntry = intro; _introFollowUp = entry;
            Animation.Loop = false;
            SelectClipInternal(intro, false);
            return true;
        }
        Animation.Loop = true;
        SelectClipInternal(entry, false);
        return true;
    }

    private AnimationEntryViewModel? _introEntry, _introFollowUp;

    private AnimationEntryViewModel? EntryFor(Formats.Skeletons.AnimClipInfo info)
    {
        string file = System.IO.Path.GetFileName(info.AnmPath.Replace((char)92, (char)47));
        return Animation.Animations.FirstOrDefault(a => string.Equals(a.Name, file, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>M887: the spawn intro played to its end - continue into the looped idle, unless something else was picked meanwhile.</summary>
    private void OnIntroClipEnded()
    {
        if (_introEntry is null || _introFollowUp is null) { OnPlainClipEnded(); return; }
        var next = _introFollowUp;
        bool still = ReferenceEquals(Animation.SelectedAnimation, _introEntry);
        _introEntry = null; _introFollowUp = null;
        if (!still) return;
        Animation.Loop = true;
        SelectClipInternal(next, false);
    }

    /// <summary>M897: the entry for what the skin's own graph answers for "Idle1" (PlayLoadedIdle's resolution), else the base-idle pick; null when the skin has none.</summary>
    private AnimationEntryViewModel? ResolveIdleEntry()
    {
        if (_ownClips.Count == 0 || Animation.Animations.Count == 0) return null;
        var info = Formats.Skeletons.AnimationClipGraph.Resolve("Idle1", _ownClips, _ownNodes)?.First
                   ?? Formats.Characters.PropAnimations.PickIdle(_ownClips);
        return info is null ? null : EntryFor(info);
    }

    /// <summary>M897 channel hold: a clip that ended inside a scripted Channel span is left as it is; the idle follows when the span ends.</summary>
    private bool _idleReturnDeferred;
    /// <summary>M897: the looped idle shown by the return - the cast's clip-bound state (particle re-arming at the loop seam, sound schedule) must not follow its time.</summary>
    private bool _onIdleReturn, _idleReturnSelect;

    /// <summary>M897: a non-looping clip reached its end - the champion goes back to the looped idle (cast, scripted Attack / step, or a clip the user picked with Loop off).
    /// The cast's effects and the skin's idle effects are not touched. Never while a channel holds the clip.</summary>
    private void OnPlainClipEnded()
    {
        if (Animation.Loop || !Animation.HasSkeleton || Animation.SelectedAnimation is null) return;
        if (InChannelSpan()) { _idleReturnDeferred = true; return; }
        ReturnToIdle();
    }

    private void ReturnToIdle()
    {
        _idleReturnDeferred = false;
        var entry = ResolveIdleEntry();
        if (entry is null) return;
        Animation.Loop = true;
        _idleReturnSelect = true;
        try { SelectClipInternal(entry, true); }
        finally { _idleReturnSelect = false; }
        _onIdleReturn = true;
    }

    partial void OnSelectedActionChanged(CharacterActionRowViewModel? value)
    {
        if (value is not null) PlayAction(value);
    }

    [RelayCommand]
    private void ReplayAction() { if (SelectedAction is { } row) PlayAction(row); }

    private void PlayAction(CharacterActionRowViewModel row)
    {
        var action = row.Action;

        // Prefer the composite: it carries the caster/missile/target systems that a clip alone does not
        // know about. Only abilities and attacks ever have one.
        if (MatchingEvent(action) is { } composite)
        {
            SelectedEvent = ReferenceEquals(SelectedEvent, composite) ? null : SelectedEvent;
            SelectedEvent = composite;      // OnSelectedEventChanged runs the full event pipeline
            return;
        }

        if (action.Clip is not { } clip) return;

        string file = System.IO.Path.GetFileName(clip.AnmPath.Replace('\\', '/'));
        var entry = Animation.Animations.FirstOrDefault(a =>
            string.Equals(a.Name, file, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return;

        // Straight clip playback, which still fires the particle and sound events authored into the clip.
        if (ReferenceEquals(Animation.SelectedAnimation, entry)) Animation.SelectedAnimation = null;
        Animation.SelectedAnimation = entry;
    }

    /// <summary>The M116 composite for the same slot, when there is one. Matched on the slot letter the
    /// event builder uses as its name ("Q", "R", "Q (assassin)") against the letter this action's label
    /// starts with — the two lists are built from different evidence and share no identity.</summary>
    private Formats.Vfx.ChampionEvent? MatchingEvent(CharacterAction action)
    {
        if (action.Kind is not (CharacterActionKind.Ability or CharacterActionKind.Attack)) return null;
        if (ChampionEvents.Count == 0) return null;

        string slot = action.Kind == CharacterActionKind.Ability
            ? action.Label[..1]
            : action.Label.StartsWith("Crit", StringComparison.OrdinalIgnoreCase) ? "CRIT" : "BA1";

        var primary = ChampionEvents.FirstOrDefault(e =>
                   string.Equals(e.Name, slot, StringComparison.OrdinalIgnoreCase))
               ?? ChampionEvents.FirstOrDefault(e =>
                   e.Name.StartsWith(slot + " ", StringComparison.OrdinalIgnoreCase));
        if (primary is not null) return primary;
        var casts = ChampionEvents.Where(e => e.Name.Length > slot.Length
                   && e.Name.StartsWith(slot, StringComparison.OrdinalIgnoreCase)
                   && char.IsAsciiDigit(e.Name[slot.Length])).ToList();
        if (casts.Count == 0) return null;
        int next = _nextAbilityCast.GetValueOrDefault(slot);
        _nextAbilityCast[slot] = (next + 1) % casts.Count;
        return casts[next % casts.Count];
    }
}
