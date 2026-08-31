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
    public ObservableCollection<CharacterActionRowViewModel> Actions { get; } = new();

    [ObservableProperty] private CharacterActionRowViewModel? _selectedAction;
    [ObservableProperty] private bool _hasActions;
    [ObservableProperty] private string _actionSummary = "";

    /// <summary>Populate from the champion record and this skin's clips. Empty for anything that is not
    /// a character — props and map meshes have no abilities.</summary>
    public void SetActions(IReadOnlyList<CharacterAction> actions)
    {
        Actions.Clear();
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

        return ChampionEvents.FirstOrDefault(e =>
                   string.Equals(e.Name, slot, StringComparison.OrdinalIgnoreCase))
               ?? ChampionEvents.FirstOrDefault(e =>
                   e.Name.StartsWith(slot + " ", StringComparison.OrdinalIgnoreCase));
    }
}
