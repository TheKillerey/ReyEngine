using System.ComponentModel;
using ReyEngine.Core.Settings;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M593: the XAML face of <see cref="NewFeatures"/> — <c>Classes.newFeature="{Binding NewFeature[some-id]}"</c>.
///
/// <para>An indexer rather than a property per feature, because the alternative does not scale: every
/// highlighted control would need its own view-model property, and marking a control as new would mean
/// touching C# as well as XAML. With this, marking one is a single attribute on the control itself.</para>
///
/// <para>It is also deliberately NOT an attached property or a behaviour. This codebase has zero of either
/// (measured across src/), while <c>Classes.x="{Binding Bool}"</c> is used throughout — NewProjectWindow's
/// wizard steps, SettingsWindow's theme cards, the inspector's issue rows. Matching the existing idiom
/// keeps the styling declarative and keeps the glow inside the theme where it can be restyled per palette.</para>
/// </summary>
public sealed class NewFeatureLookup : INotifyPropertyChanged
{
    private string _lastSeenVersion = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Is this feature still worth pointing out? Unknown ids are false — see NewFeatures.IsNew.</summary>
    public bool this[string featureId] => NewFeatures.IsNew(featureId, _lastSeenVersion);

    /// <summary>True while anything at all is unacknowledged — for a "What's New" affordance.</summary>
    public bool AnyUnseen => NewFeatures.AnyUnseen(_lastSeenVersion);

    /// <summary>What the user has acknowledged. Setting it re-evaluates every binding at once.</summary>
    public string LastSeenVersion
    {
        get => _lastSeenVersion;
        set
        {
            if (string.Equals(_lastSeenVersion, value, StringComparison.Ordinal)) return;
            _lastSeenVersion = value ?? "";
            // M811: how a binding learns the indexer changed. Avalonia 12 re-reads a reflection indexer binding
            // only for the indexer's own name, "Item". "Item[]" - Avalonia 11's and WPF's name, the only one
            // raised until M811 - and an empty or null name leave every glow as it was (measured on the real
            // MainWindow, UiProbe glowlive), so Got it put no glow out until the editor restarted. Both names
            // are raised, so a binding that follows either rule re-reads.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AnyUnseen)));
        }
    }
}
