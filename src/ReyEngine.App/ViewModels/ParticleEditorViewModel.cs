using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Decoding;
using ReyEngine.Formats.Particles;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M46 Particle Editor (Particle-Town style): tree of systems/emitters, emitter module cards, a property
/// inspector with safe primitive editing, a read-only curve display, and a live billboard preview.
/// Backed by <see cref="ParticleDocument"/> (live BinTree); edits re-serialize + re-extract the playable
/// definitions so the preview updates immediately. Saving goes through the project-override pipeline.
/// </summary>
public sealed partial class ParticleEditorViewModel : ObservableObject
{
    // wired by MainWindowViewModel
    public Func<VfxSystemDefinition, IReadOnlyList<TextureImage?>>? ResolveTextures;
    public Func<VfxSystemDefinition, IReadOnlyList<TextureImage?>>? ResolveMultTextures;
    public Func<VfxSystemDefinition, IReadOnlyList<TextureImage?>>? ResolveDistortionTextures;
    public Func<VfxSystemDefinition, IReadOnlyList<TextureImage?>>? ResolveColorTextures;   // M68
    public Func<VfxSystemDefinition, IReadOnlyList<TextureImage?>>? ResolveErosionTextures;   // M174 (2.1)
    public Func<VfxSystemDefinition, IReadOnlyList<TextureImage?>>? ResolvePaletteTextures;   // M175 (2.6)
    public Func<VfxSystemDefinition, IReadOnlyList<CubemapImage?>>? ResolveReflectionCubemaps;   // M181 (2.12)
    public Func<VfxSystemDefinition, IReadOnlyList<ReyEngine.Formats.Meshes.StaticMeshData?>?>? ResolveMeshes; // M47
    public Func<string, Avalonia.Media.Imaging.Bitmap?>? LoadThumbnail;   // particle sprite preview on cards
    /// <summary>M187 (3.1): the host's .bin name dictionary, so emitter rows show field names rather than
    /// raw hashes. Measured, this takes named coverage from 85.5% to 99.8% of emitter field occurrences.</summary>
    public Func<uint, string?>? ResolveBinName;
    public Action<string>? Info;
    public Action<string>? Error;
    public Action? MarkDocumentDirty;
    public Func<System.Threading.Tasks.Task>? SaveOverrideAsync;
    /// <summary>M125: host hook — open the Bin Issues window for this document.</summary>
    public Action? OpenIssues;

    [ObservableProperty] private ParticleDocument? _document;
    [ObservableProperty] private string _assetName = "";
    [ObservableProperty] private bool _isEditable;
    [ObservableProperty] private VfxPlayback? _playback;
    [ObservableProperty] private double _speed = 1.0;
    [ObservableProperty] private bool _paused;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private ParticleSystemNodeViewModel? _selectedSystem;
    [ObservableProperty] private ParticlePropertyRowViewModel? _selectedProperty;

    // M125: repairs the tolerant reader applied while loading this bin
    [ObservableProperty] private bool _hasBinIssues;
    [ObservableProperty] private string _binIssuesLabel = "";
    [RelayCommand] private void ShowIssues() => OpenIssues?.Invoke();

    public WadAssetEntry? Entry { get; private set; }
    public ObservableCollection<ParticleSystemNodeViewModel> Systems { get; } = new();
    public ObservableCollection<ParticleEmitterCardViewModel> Cards { get; } = new();

    private IReadOnlyDictionary<uint, VfxSystemDefinition> _defs =
        new Dictionary<uint, VfxSystemDefinition>();

    /// <summary>Load a particle .bin into the editor. Returns false when it holds no VFX systems.</summary>
    public bool Load(WadAssetEntry entry, byte[] bytes, bool editable)
    {
        var doc = ParticleDocument.Parse(bytes, ResolveBinName);
        if (doc is null) return false;

        Entry = entry;
        Document = doc;
        AssetName = entry.DisplayName;
        IsEditable = editable;
        _defs = VfxSystemResolver.ExtractAll(bytes);

        Systems.Clear();
        foreach (var s in doc.Systems)
        {
            var issues = doc.Issues.Where(i => i.ObjectPathHash == s.PathHash).ToList();
            Systems.Add(new ParticleSystemNodeViewModel(s)
            {
                HasIssue = issues.Count > 0,
                IssueTip = issues.Count > 0
                    ? string.Join(Environment.NewLine, issues.Select(i => $"{i.Kind}: {i.Message}"))
                    : null,
            });
        }
        SelectedSystem = Systems.FirstOrDefault();
        HasBinIssues = doc.Issues.Count > 0;
        BinIssuesLabel = $"⚠ {doc.Issues.Count} issue(s)";
        Status = $"{doc.Systems.Count} system(s), {doc.Systems.Sum(s => s.Emitters.Count)} emitter(s)" +
                 (editable ? "" : "  ·  READ-ONLY (Copy To Project to edit)");
        return true;
    }

    partial void OnSelectedSystemChanged(ParticleSystemNodeViewModel? value)
    {
        Cards.Clear();
        SelectedProperty = null;
        if (value is null) { Playback = null; return; }
        Cards.Add(new ParticleEmitterCardViewModel(value.Entry, this));   // M188 (3.5): the system's own fields
        foreach (var e in value.Entry.Emitters)
            Cards.Add(new ParticleEmitterCardViewModel(e, this));
        RebuildPlayback();
    }

    partial void OnSelectedPropertyChanged(ParticlePropertyRowViewModel? value)
    {
        foreach (var c in Cards)
            foreach (var m in c.Modules)
                foreach (var r in m.Rows)
                    r.IsSelected = ReferenceEquals(r, value);
    }

    internal void SelectRow(ParticlePropertyRowViewModel row) => SelectedProperty = row;

    /// <summary>M190 (3.6): run a curve mutation, then do exactly what a scalar edit does - mark the
    /// document dirty, re-serialize, re-extract, and rebuild the preview - so an edited curve is visible
    /// in the viewport straight away rather than only after a reload.</summary>
    internal void EditCurve(ParticlePropertyRowViewModel row, Action mutate)
    {
        if (Document is null) return;
        if (!IsEditable) { row.ErrorText = "Read-only: Copy To Project first."; return; }
        try
        {
            mutate();
            row.ErrorText = null;
            row.RefreshCurve();
            MarkDocumentDirty?.Invoke();
            _defs = VfxSystemResolver.ExtractAll(Document.Serialize());
            RebuildPlayback();
            Info?.Invoke($"Curve of {row.Name}: {row.CurveKeys.Count} key(s).");
        }
        catch (Exception ex) { row.ErrorText = ex.Message; }
    }

    private void RebuildPlayback()
    {
        if (SelectedSystem is null) { Playback = null; return; }
        if (!_defs.TryGetValue(SelectedSystem.Entry.PathHash, out var def)) { Playback = null; return; }
        var texs = ResolveTextures?.Invoke(def) ?? new TextureImage?[def.Emitters.Count];
        var multTexs = ResolveMultTextures?.Invoke(def) ?? new TextureImage?[def.Emitters.Count];
        var distortionTexs = ResolveDistortionTextures?.Invoke(def) ?? new TextureImage?[def.Emitters.Count];
        var colorTexs = ResolveColorTextures?.Invoke(def) ?? new TextureImage?[def.Emitters.Count];
        // M175: erosion and palette maps. Live-editing a dissolve or a gradient with the stage switched
        // off in the preview would show the user an effect that does not match what the game draws.
        var erosionTexs = ResolveErosionTextures?.Invoke(def) ?? new TextureImage?[def.Emitters.Count];
        var paletteTexs = ResolvePaletteTextures?.Invoke(def) ?? new TextureImage?[def.Emitters.Count];
        var meshes = ResolveMeshes?.Invoke(def);
        Playback = new VfxPlayback(new[] { new VfxPlaybackItem(def, System.Numerics.Vector3.Zero, texs, meshes,
            multTexs, distortionTexs, colorTexs, erosionTexs, paletteTexs,
            emitterReflectionCubemaps: ResolveReflectionCubemaps?.Invoke(def)) });
    }

    /// <summary>M185 (2.15): stop emitting and let the Linger curves play out. Riot's shutdown stage is
    /// triggered by an external stop - a buff dropping, an ult ending - which a looping preview never
    /// produces, so without this button the stage is unobservable. Restart clears it.</summary>
    [ObservableProperty] private bool _stopped;

    [RelayCommand] private void StopEmitting() => Stopped = true;

    /// <summary>M186 (2.15): loop the preview as run -> stop -> linger -> restart, so the shutdown curves
    /// play every cycle without the user pressing Stop. On by default: an effect whose only fade lives in
    /// its Linger curves otherwise looks like it never ends.</summary>
    [ObservableProperty] private bool _autoStop = true;

    [RelayCommand] private void Restart() { Stopped = false; RebuildPlayback(); }
    [RelayCommand] private void TogglePause() => Paused = !Paused;

    [RelayCommand]
    private void ApplyEdit(ParticlePropertyRowViewModel? row)
    {
        if (row is null || Document is null) return;
        if (!IsEditable) { row.ErrorText = "Read-only: Copy To Project first."; return; }
        if (row.Prop.IsReadOnly) { row.ErrorText = "This property type isn't editable yet."; return; }
        try
        {
            row.Prop.Apply(row.EditText);
            row.ErrorText = null;
            row.Refresh();
            MarkDocumentDirty?.Invoke();
            // live preview: re-serialize the edited tree and re-extract the playable definitions
            _defs = VfxSystemResolver.ExtractAll(Document.Serialize());
            RebuildPlayback();
            Info?.Invoke($"Set {row.Name} = {row.EditText}");
        }
        catch (Exception ex) { row.ErrorText = ex.Message; }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task SaveOverride()
    {
        if (SaveOverrideAsync is not null) await SaveOverrideAsync();
    }

    /// <summary>M49: enable/disable one emitter — edits its 'disabled' bool on the live tree and refreshes
    /// the preview (the resolver skips disabled emitters, so it stops/starts immediately).</summary>
    internal void SetEmitterEnabled(ParticleEmitterCardViewModel card, bool enabled)
    {
        if (Document is null || card.Entry is not { } entry || entry.Disabled == !enabled) return;
        if (!IsEditable)
        {
            Error?.Invoke("Read-only Riot reference: Copy To Project to toggle emitters.");
            card.IsEnabled = !card.Entry.Disabled;   // revert the checkbox
            return;
        }
        entry.SetDisabled(!enabled);
        MarkDocumentDirty?.Invoke();
        _defs = VfxSystemResolver.ExtractAll(Document.Serialize());
        RebuildPlayback();
        Info?.Invoke($"Emitter '{card.Name}' {(enabled ? "enabled" : "disabled")} (save Override to persist).");
    }
}

/// <summary>Left-tree node: one VFX system with its emitter names.</summary>
public sealed partial class ParticleSystemNodeViewModel : ObservableObject
{
    public ParticleSystemEntry Entry { get; }
    public string Name => Entry.Name;
    // M188 (3.4): emitterless systems are listed now, so the tree has to say so rather than "0 emitter(s)",
    // which reads like a parse failure. 55.4% of them are named stubs carrying only a name and a path.
    public string Detail => Entry.Emitters.Count == 0
        ? "no emitters (stub system)"
        : $"{Entry.Emitters.Count} emitter(s)";
    public IReadOnlyList<string> EmitterNames { get; }

    /// <summary>M125: this system's bin object needed repairs while loading (marked red in the tree).</summary>
    public bool HasIssue { get; init; }
    public string? IssueTip { get; init; }

    public ParticleSystemNodeViewModel(ParticleSystemEntry entry)
    {
        Entry = entry;
        EmitterNames = entry.Emitters.Select(e => e.Name).ToList();
    }
}

/// <summary>Center card: one emitter as a column of module groups (Particle Town style).</summary>
public sealed partial class ParticleEmitterCardViewModel : ObservableObject
{
    private readonly ParticleEditorViewModel _owner;
    public ParticleEmitterEntry? Entry { get; }
    public string Name { get; }
    public IReadOnlyList<ParticleModuleGroupViewModel> Modules { get; }
    /// <summary>M188 (3.5): this card holds the SYSTEM's own fields rather than an emitter's. Same rows and
    /// the same inspector - only the enable toggle and the sprite strip do not apply.</summary>
    public bool IsSystemCard => Entry is null;
    public bool ShowToggle => Entry is not null;
    /// <summary>The emitter's sprite texture, decoded as a small preview (null when unresolved).</summary>
    public Avalonia.Media.Imaging.Bitmap? Thumbnail { get; }
    public bool HasThumbnail => Thumbnail is not null;
    public bool CanToggle => _owner.IsEditable;

    /// <summary>Emitter on/off — edits the VfxEmitterDefinitionData's 'disabled' bool on the live tree
    /// (persists via Save Override); the preview re-extracts so the emitter stops/starts immediately.</summary>
    [ObservableProperty] private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value) => _owner.SetEmitterEnabled(this, value);

    public ParticleEmitterCardViewModel(ParticleEmitterEntry emitter, ParticleEditorViewModel owner)
    {
        _owner = owner;
        Entry = emitter;
        Name = emitter.Name;
        _isEnabled = !emitter.Disabled;
        Modules = emitter.Modules
            .Select(m => new ParticleModuleGroupViewModel(m,
                emitter.Properties.Where(p => p.Module == m)
                    .Select(p => new ParticlePropertyRowViewModel(p, owner)).ToList()))
            .ToList();
        var texPath = emitter.Properties.FirstOrDefault(p => p.Name == "texture")?.CurrentText;
        if (!string.IsNullOrWhiteSpace(texPath))
            try { Thumbnail = owner.LoadThumbnail?.Invoke(texPath); } catch { Thumbnail = null; }
    }

    /// <summary>M188 (3.5): the system-level card. Its fields - particleName, particlePath, flags, transform,
    /// visibilityRadius, the default sounds - had no editor surface at all before this.</summary>
    public ParticleEmitterCardViewModel(ParticleSystemEntry system, ParticleEditorViewModel owner)
    {
        _owner = owner;
        Entry = null;
        Name = "SYSTEM";
        _isEnabled = true;
        Modules = system.Modules
            .Select(m => new ParticleModuleGroupViewModel(m,
                system.Properties.Where(p => p.Module == m)
                    .Select(p => new ParticlePropertyRowViewModel(p, owner)).ToList()))
            .ToList();
    }
}

/// <summary>M190 (3.6): one editable curve key - its time and its 1..4 value components.</summary>
public sealed partial class ParticleCurveKeyViewModel : ObservableObject
{
    private readonly ParticlePropertyRowViewModel _row;
    public int Index { get; }
    [ObservableProperty] private string _timeText;
    [ObservableProperty] private string _valueText;

    public ParticleCurveKeyViewModel(ParticlePropertyRowViewModel row, int index, float time, float[] components)
    {
        _row = row;
        Index = index;
        // InvariantCulture throughout: the app runs on a German locale, where "0,5" would round-trip as
        // two components rather than as one half.
        _timeText = time.ToString("0.####", CultureInfo.InvariantCulture);
        _valueText = string.Join(", ", components.Select(c => c.ToString("0.####", CultureInfo.InvariantCulture)));
    }

    [RelayCommand] private void ApplyKey()
    {
        try { _row.ApplyKey(Index, TimeText, ValueText); }
        catch (Exception ex) { _row.ErrorText = ex.Message; }
    }

    [RelayCommand] private void DeleteKey() => _row.DeleteKey(Index);
}

public sealed record ParticleModuleGroupViewModel(string Name, IReadOnlyList<ParticlePropertyRowViewModel> Rows);

/// <summary>One property row: live value + edit text + validation error.</summary>
public sealed partial class ParticlePropertyRowViewModel : ObservableObject
{
    private readonly ParticleEditorViewModel _owner;
    public ParticleProperty Prop { get; }

    [ObservableProperty] private string _editText;
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _currentText;

    public string Name => Prop.Name;
    public string Module => Prop.Module;
    public string TypeName => Prop.TypeName;
    public bool IsReadOnly => Prop.IsReadOnly;
    public string ReadOnlyReason => Prop.ReadOnlyReason;   // M187: rows are read-only for several reasons
    /// <summary>M191 (3.7): the preview will not show this field's effect. The edit still reaches the .bin.</summary>
    public bool IgnoredByPreview => Prop.IgnoredByPreview;
    public string? PreviewNote => Prop.PreviewNote;
    /// <summary>M189 (3.3): left inset for a row nested inside a definition struct, 10px per level.</summary>
    public Avalonia.Thickness Indent => new(Prop.Depth * 10, 0, 0, 0);
    public bool HasCurve => Prop.HasCurve;
    public float[]? CurveTimes => Prop.CurveTimes;
    public float[][]? CurveChannels => Prop.CurveChannels;

    /// <summary>M190 (3.6): the curve display was a picture of the keys with no way to change them. These
    /// rows edit the live dynamics block, so a curve edit lands in the bin exactly as a scalar edit does.</summary>
    public bool CanEditCurve => Prop.CanEditCurve && _owner.IsEditable;
    public ObservableCollection<ParticleCurveKeyViewModel> CurveKeys { get; } = new();

    public ParticlePropertyRowViewModel(ParticleProperty prop, ParticleEditorViewModel owner)
    {
        Prop = prop;
        _owner = owner;
        _currentText = prop.CurrentText;
        _editText = prop.CurrentText;
        RebuildCurveKeys();
    }

    public void Refresh() { CurrentText = Prop.CurrentText; EditText = Prop.CurrentText; }

    /// <summary>Re-read the keys from the property after an edit, and repaint the curve. The arrays are
    /// replaced rather than mutated, so CurvePreview's AffectsRender picks the change up.</summary>
    public void RefreshCurve()
    {
        RebuildCurveKeys();
        OnPropertyChanged(nameof(CurveTimes));
        OnPropertyChanged(nameof(CurveChannels));
        OnPropertyChanged(nameof(HasCurve));
    }

    private void RebuildCurveKeys()
    {
        CurveKeys.Clear();
        var t = Prop.CurveTimes;
        var ch = Prop.CurveChannels;
        if (t is null || ch is null) return;
        for (int i = 0; i < t.Length; i++)
        {
            var comps = new float[ch.Length];
            for (int c = 0; c < ch.Length; c++) comps[c] = ch[c][i];
            CurveKeys.Add(new ParticleCurveKeyViewModel(this, i, t[i], comps));
        }
    }

    /// <summary>Add a key midway through the curve's time range, copying the first key's value so the new
    /// point starts somewhere meaningful rather than at zero.</summary>
    [RelayCommand] private void AddCurveKey()
    {
        var t = Prop.CurveTimes; var ch = Prop.CurveChannels;
        if (t is null || ch is null) return;
        float mid = t.Length > 1 ? (t[0] + t[^1]) * 0.5f : t[0] + 0.5f;
        var comps = new float[ch.Length];
        for (int c = 0; c < ch.Length; c++) comps[c] = ch[c][0];
        _owner.EditCurve(this, () => Prop.AddCurveKey(mid, comps));
    }

    internal void ApplyKey(int index, string timeText, string valueText)
    {
        float time = float.Parse(timeText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
        var parts = valueText.Split(',', StringSplitOptions.RemoveEmptyEntries);
        int want = Prop.CurveComponents;
        if (parts.Length != want)
            throw new FormatException($"This curve has {want} component(s); give {want} comma-separated number(s).");
        var comps = new float[want];
        for (int i = 0; i < want; i++)
            comps[i] = float.Parse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
        _owner.EditCurve(this, () => Prop.SetCurveKey(index, time, comps));
    }

    internal void DeleteKey(int index) => _owner.EditCurve(this, () => Prop.RemoveCurveKey(index));

    [RelayCommand] private void Select() => _owner.SelectRow(this);
}
