using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Formats.Particles;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M752: one force field of an emitter in the card's FORCES section - its values, and the two preview-only
/// switches LTK Manager's particle editor offers, Mute and Solo.
///
/// <para>Mute and Solo are NEVER written to the file. They filter what the preview simulates, so an effect
/// with four forces can be taken apart one at a time; the saved effect keeps all four. The key is the
/// emitter's position in its system plus the force's kind and index, held by the editor so it survives the
/// card being rebuilt after an edit.</para>
/// </summary>
public sealed partial class ParticleForceViewModel : ObservableObject
{
    private readonly ParticleEditorViewModel _owner;
    private readonly ParticleEmitterCardViewModel _card;
    public ParticleForce Force { get; }
    public string Title => Force.Title;
    public string Kind => ParticleForces.Name(Force.Kind);
    public string Summary { get; }
    public IReadOnlyList<ParticleForceValueViewModel> Values { get; }
    public bool IsPositional => Force.IsPositional;
    public bool CanEdit => _owner.IsEditable;

    /// <summary>The key the editor keeps the preview switches under.</summary>
    internal string Key => ParticleEditorViewModel.ForceKey(_card.EmitterIndex, Force.Kind, Force.Index);

    public bool IsMuted
    {
        get => _owner.IsForceMuted(Key);
        set { _owner.SetForceMuted(Key, value); OnPropertyChanged(); }
    }

    public bool IsSoloed
    {
        get => _owner.IsForceSoloed(Key);
        set { _owner.SetForceSoloed(Key, value); OnPropertyChanged(); }
    }

    public ParticleForceViewModel(ParticleForce force, ParticleEmitterCardViewModel card, ParticleEditorViewModel owner)
    {
        Force = force;
        _card = card;
        _owner = owner;
        Values = force.Values.Select(v => new ParticleForceValueViewModel(v, this, owner)).ToList();
        Summary = string.Join(" · ", force.Values.Take(2).Select(v => $"{v.Label} {ParticleForceValueViewModel.Format(v.Value, v.IsVector)}"));
    }

    internal ParticleEmitterCardViewModel Card => _card;

    public void NotifySwitches()
    {
        OnPropertyChanged(nameof(IsMuted));
        OnPropertyChanged(nameof(IsSoloed));
    }

    [RelayCommand]
    private void Remove() => _owner.EditForce(_card, e => e.RemoveForce(Force.Kind, Force.Index),
        $"Removed {Title} from '{_card.Name}'.", structural: true);
}

/// <summary>M752: one value of a force - "0, 40, 0" or "300" - typed in the invariant culture, because the
/// app runs on a German locale where "0,5" would read as two components.</summary>
public sealed partial class ParticleForceValueViewModel : ObservableObject
{
    private readonly ParticleEditorViewModel _owner;
    private readonly ParticleForceViewModel _force;
    public ParticleForceValue Value { get; }
    public string Label => Value.Label;
    public bool HasCurve => Value.HasCurve;
    public bool IsDefault => !Value.IsAuthored;
    public bool CanEdit => _owner.IsEditable;
    public bool HasError => !string.IsNullOrEmpty(Error);
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));
    public string Tip => Value.HasCurve
        ? "This value is also a curve. The number here is its constant; the curve is in the property list."
        : Value.IsAuthored ? $"{Value.Field} - press Enter or ✓ to apply." : $"{Value.Field} is not in the file; this is the class default.";

    [ObservableProperty] private string _text;
    [ObservableProperty] private string? _error;

    public ParticleForceValueViewModel(ParticleForceValue value, ParticleForceViewModel force, ParticleEditorViewModel owner)
    {
        Value = value;
        _force = force;
        _owner = owner;
        _text = Format(value.Value, value.IsVector);
    }

    public static string Format(Vector3 v, bool isVector) => isVector
        ? string.Join(", ", new[] { v.X, v.Y, v.Z }.Select(c => c.ToString("0.###", CultureInfo.InvariantCulture)))
        : v.X.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Parse the typed text: one number, or three comma-separated for a vector.</summary>
    public static bool TryParse(string text, bool isVector, out Vector3 value, out string? error)
    {
        value = default;
        error = null;
        var parts = text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        int want = isVector ? 3 : 1;
        if (parts.Length != want) { error = isVector ? "Give three numbers: x, y, z." : "Give one number."; return false; }
        var c = new float[3];
        for (int i = 0; i < want; i++)
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out c[i]) || !float.IsFinite(c[i]))
            { error = $"'{parts[i]}' is not a number."; return false; }
        value = new Vector3(c[0], c[1], c[2]);
        return true;
    }

    [RelayCommand]
    private void Apply()
    {
        if (!TryParse(Text, Value.IsVector, out var v, out var why)) { Error = why; return; }
        Error = null;
        var f = _force.Force;
        _owner.EditForce(_force.Card, e => e.SetForceValue(f.Kind, f.Index, Value.Field, v),
            $"{_force.Title}: {Label} = {Text}", structural: false);
    }
}

/// <summary>M752: one entry of the card's "+ Force" menu.</summary>
public sealed record ParticleForceKindChoice(ParticleForceKind Kind, string Name, string Hint);
