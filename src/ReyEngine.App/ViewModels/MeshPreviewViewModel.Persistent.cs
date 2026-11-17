using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Skeletons;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M902: one switch a skin's <c>PersistentEffectConditions</c> ask about (a buff, a gear, ...), in the Character window.
/// Same vocabulary and keys as the M647 material-driver switches (<see cref="MaterialDriverCondition"/>), but a separate card:
/// flipping one re-evaluates the persistent effects only and never rebuilds the D3D11 scene.
/// </summary>
public sealed partial class PersistentGateViewModel : ViewModelBase
{
    private readonly Action _changed;

    public PersistentGateViewModel(PersistentGate gate, Action changed)
    {
        Model = gate.Condition;
        DefaultOn = gate.DefaultOn;
        _isOn = gate.DefaultOn;
        _changed = changed;
    }

    public MaterialDriverCondition Model { get; }
    public bool DefaultOn { get; }
    public string Key => Model.Key;
    public string Label => Model.Label;
    public string Tip => $"Run the skin's persistent effects as if {Model.Describe}. "
                         + (DefaultOn ? "Starts on: this is an inference that the buff is an idle cosmetic the game always gives the character. " : "Starts off. ")
                         + $"Condition {Model.Key}.";

    [ObservableProperty] private bool _isOn;

    partial void OnIsOnChanged(bool value) => _changed();
}

/// <summary>
/// M902: the Character window plays a skin's <c>PersistentEffectConditions</c> - effects (and submesh show/hide lists) that are on
/// only while a condition tree holds. Before this they were read by nothing but the role labeller, so Ahri skin 97's orb and tail
/// fire, Aatrox skin 33's banner turbines and Kai'Sa skin 83's engine glow never ran.
///
/// <para>The active entries' effects join the skin's idle effects (<see cref="RebuildIdleItems"/>), so bone attachment, the
/// M892/M894 survival of idle effects across clip changes and loads, and M883 (a user-picked clip ends spell effects, not these)
/// all apply unchanged. The tree is re-evaluated when the clip, the submesh picture or a switch changes - never per frame
/// unless one of those moved.</para>
/// </summary>
public sealed partial class MeshPreviewViewModel
{
    private IReadOnlyList<PersistentEffectCondition> _persistent = Array.Empty<PersistentEffectCondition>();
    private Func<uint, string?> _persistentResolve = _ => null;
    private readonly HashSet<string> _unknownConditionsLogged = new(StringComparer.Ordinal);

    /// <summary>The switches the loaded skin's persistent conditions ask about.</summary>
    public ObservableCollection<PersistentGateViewModel> PersistentGates { get; } = new();
    [ObservableProperty] private bool _hasPersistentGates;

    /// <summary>How many persistent entries are on now (entries, not effects).</summary>
    [ObservableProperty] private int _persistentActiveCount;

    // what the last evaluation produced - the effects mounted, and the submesh lists applied while active
    private IReadOnlyList<SkinIdleEffect> _persistentRecords = Array.Empty<SkinIdleEffect>();
    private bool[] _persistentShow = Array.Empty<bool>(), _persistentHide = Array.Empty<bool>();

    // the inputs of that evaluation, so an unchanged frame does no work
    private bool[]? _pLastComputed;
    private string _pLastClip = "";
    private int _pGateVersion, _pLastGateVersion = -1;
    private HashSet<uint> _pClipHashes = new();
    private string? _pClipHashesFor;

    /// <summary>
    /// M902: the loaded skin's <c>PersistentEffectConditions</c>. Call after <see cref="SetIdleEffects"/> (the effects resolve through
    /// the resource map <see cref="SetVfx"/> installs) and with the SKIN's own bin read, for the same reason idle effects are.
    /// </summary>
    public void SetPersistentEffects(IReadOnlyList<PersistentEffectCondition>? entries, Func<uint, string?>? resolve)
    {
        _persistent = entries ?? Array.Empty<PersistentEffectCondition>();
        _persistentResolve = resolve ?? (_ => null);
        _persistentSwitching = true;
        try
        {
            // a switch the user already set survives the same skin coming back (a rebuild must not undo it); a new skin starts from the defaults
            var wasOn = PersistentGates.ToDictionary(g => g.Key, g => g.IsOn, StringComparer.OrdinalIgnoreCase);
            PersistentGates.Clear();
            foreach (var gate in PersistentConditions.GatesOf(_persistent, _persistentResolve))
            {
                var vm = new PersistentGateViewModel(gate, GateChanged);
                if (wasOn.TryGetValue(gate.Key, out bool on) && on != vm.IsOn) vm.IsOn = on;
                PersistentGates.Add(vm);
            }
            HasPersistentGates = PersistentGates.Count > 0;
        }
        finally { _persistentSwitching = false; }
        _pGateVersion++;
        _pLastComputed = null;
        _pClipHashesFor = null;
        ApplyAutoVisibility();   // evaluates (it holds the submesh picture the conditions read) and republishes when the set changed
        if (Submeshes.Count == 0) EvaluatePersistent(Array.Empty<bool>());
    }

    private bool _persistentSwitching;

    /// <summary>A new mesh is being shown: the previous skin's entries, effects and cached evaluation must not leak into it. The switches stay until
    /// <see cref="SetPersistentEffects"/> replaces them, so a user's setting survives the same skin coming back.</summary>
    private void ResetPersistent()
    {
        _persistent = Array.Empty<PersistentEffectCondition>();
        _persistentRecords = Array.Empty<SkinIdleEffect>();
        _persistentShow = Array.Empty<bool>(); _persistentHide = Array.Empty<bool>();
        _pLastComputed = null; _pClipHashesFor = null;
        PersistentActiveCount = 0;
    }

    private void GateChanged()
    {
        if (_persistentSwitching) return;
        _pGateVersion++;
        ApplyAutoVisibility();
        if (Submeshes.Count == 0) EvaluatePersistent(Array.Empty<bool>());
    }

    /// <summary>The graph names of the clips that play the selected .anm, hashed - what <c>IsAnimationPlaying</c> lists name.</summary>
    private HashSet<uint> PlayingClipHashes()
    {
        string? anm = Animation.SelectedAnimation?.Name;
        if (_pClipHashesFor is null || !string.Equals(anm, _pClipHashesFor, StringComparison.OrdinalIgnoreCase))
        {
            _pClipHashesFor = anm;
            _pClipHashes = new HashSet<uint>();
            if (anm is not null)
                foreach (var c in _allClips)
                    if (string.Equals(System.IO.Path.GetFileName(c.AnmPath.Replace((char)92, (char)47)), anm, StringComparison.OrdinalIgnoreCase))
                        _pClipHashes.Add(AnimationClipGraph.HashOf(c.Name));
            if (CurrentClip() is { } cur) _pClipHashes.Add(AnimationClipGraph.HashOf(cur.Name));
        }
        return _pClipHashes;
    }

    /// <summary>
    /// Evaluate every entry against the clip playing and <paramref name="computed"/> (the submesh picture after the skin and the clip,
    /// before the user's overrides and before these entries' own show/hide - which is what keeps a "not visible" test from reading its
    /// own effect). Republishes the effects when the active set changed, and returns the submesh lists to apply.
    /// </summary>
    private (bool[] Show, bool[] Hide) EvaluatePersistent(bool[] computed)
    {
        if (_persistent.Count == 0)
        {
            if (_persistentRecords.Count > 0) { _persistentRecords = Array.Empty<SkinIdleEffect>(); PersistentActiveCount = 0; RebuildIdleItems(); RepublishPlayback(); }
            return (Array.Empty<bool>(), Array.Empty<bool>());
        }

        string clipKey = Animation.SelectedAnimation?.Name ?? "";
        if (_pLastComputed is { } last && last.AsSpan().SequenceEqual(computed) && _pLastClip == clipKey && _pLastGateVersion == _pGateVersion)
            return (_persistentShow, _persistentHide);
        _pLastComputed = (bool[])computed.Clone(); _pLastClip = clipKey; _pLastGateVersion = _pGateVersion;

        var ctx = new PersistentContext
        {
            PlayingClips = PlayingClipHashes(),
            IsOn = key => PersistentGates.Any(g => g.IsOn && string.Equals(g.Key, key, StringComparison.OrdinalIgnoreCase)),
            Resolve = _persistentResolve,
            IsChampion = true,
            AnySubmeshVisible = hashes =>
            {
                for (int i = 0; i < Submeshes.Count && i < computed.Length; i++)
                    if (computed[i] && ChampionAnimationData.Matches(Submeshes[i].Name, Array.Empty<string>(), hashes)) return true;
                return false;
            },
            OnUnknown = name =>
            {
                if (_unknownConditionsLogged.Add(name))
                    LogDx11?.Invoke("Character", $"persistent effect condition '{name}' is not known to the editor - its entries stay OFF");
            },
        };

        var records = new List<SkinIdleEffect>();
        var show = new bool[Submeshes.Count];
        var hide = new bool[Submeshes.Count];
        int active = 0;
        foreach (var entry in _persistent)
        {
            if (!PersistentConditions.IsActive(entry, ctx)) continue;
            active++;
            foreach (var v in entry.Vfxs)
            {
                // The character window has no gameplay camera to ride; the record is kept in the model and simply not mounted.
                if (v.AttachToCamera) continue;
                records.Add(new SkinIdleEffect(v.EffectKey, v.BoneName, v.TargetBoneName));
            }
            for (int i = 0; i < Submeshes.Count; i++)
            {
                if (ChampionAnimationData.Matches(Submeshes[i].Name, Array.Empty<string>(), entry.SubmeshesToShow)) show[i] = true;
                if (ChampionAnimationData.Matches(Submeshes[i].Name, Array.Empty<string>(), entry.SubmeshesToHide)) hide[i] = true;
            }
        }
        _persistentShow = show; _persistentHide = hide;
        PersistentActiveCount = active;

        bool same = records.Count == _persistentRecords.Count
                    && records.Zip(_persistentRecords).All(p => p.First == p.Second);
        if (!same)
        {
            _persistentRecords = records;
            RebuildIdleItems();
            RepublishPlayback();
        }
        return (show, hide);
    }
}
