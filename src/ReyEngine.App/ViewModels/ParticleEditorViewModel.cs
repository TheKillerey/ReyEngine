using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    public Func<VfxSystemDefinition, IReadOnlyList<ReyEngine.Formats.Meshes.StaticMeshData?>?>? ResolveMeshes; // M47
    public Func<string, Avalonia.Media.Imaging.Bitmap?>? LoadThumbnail;   // particle sprite preview on cards
    public Action<string>? Info;
    public Action<string>? Error;
    public Action? MarkDocumentDirty;
    public Func<System.Threading.Tasks.Task>? SaveOverrideAsync;

    [ObservableProperty] private ParticleDocument? _document;
    [ObservableProperty] private string _assetName = "";
    [ObservableProperty] private bool _isEditable;
    [ObservableProperty] private VfxPlayback? _playback;
    [ObservableProperty] private double _speed = 1.0;
    [ObservableProperty] private bool _paused;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private ParticleSystemNodeViewModel? _selectedSystem;
    [ObservableProperty] private ParticlePropertyRowViewModel? _selectedProperty;

    public WadAssetEntry? Entry { get; private set; }
    public ObservableCollection<ParticleSystemNodeViewModel> Systems { get; } = new();
    public ObservableCollection<ParticleEmitterCardViewModel> Cards { get; } = new();

    private IReadOnlyDictionary<uint, VfxSystemDefinition> _defs =
        new Dictionary<uint, VfxSystemDefinition>();

    /// <summary>Load a particle .bin into the editor. Returns false when it holds no VFX systems.</summary>
    public bool Load(WadAssetEntry entry, byte[] bytes, bool editable)
    {
        var doc = ParticleDocument.Parse(bytes);
        if (doc is null) return false;

        Entry = entry;
        Document = doc;
        AssetName = entry.DisplayName;
        IsEditable = editable;
        _defs = VfxSystemResolver.ExtractAll(bytes);

        Systems.Clear();
        foreach (var s in doc.Systems)
            Systems.Add(new ParticleSystemNodeViewModel(s));
        SelectedSystem = Systems.FirstOrDefault();
        Status = $"{doc.Systems.Count} system(s), {doc.Systems.Sum(s => s.Emitters.Count)} emitter(s)" +
                 (editable ? "" : "  ·  READ-ONLY (Copy To Project to edit)");
        return true;
    }

    partial void OnSelectedSystemChanged(ParticleSystemNodeViewModel? value)
    {
        Cards.Clear();
        SelectedProperty = null;
        if (value is null) { Playback = null; return; }
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

    private void RebuildPlayback()
    {
        if (SelectedSystem is null) { Playback = null; return; }
        if (!_defs.TryGetValue(SelectedSystem.Entry.PathHash, out var def)) { Playback = null; return; }
        var texs = ResolveTextures?.Invoke(def) ?? new TextureImage?[def.Emitters.Count];
        var meshes = ResolveMeshes?.Invoke(def);
        Playback = new VfxPlayback(new[] { new VfxPlaybackItem(def, System.Numerics.Vector3.Zero, texs, meshes) });
    }

    [RelayCommand] private void Restart() => RebuildPlayback();
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
        if (Document is null || card.Entry.Disabled == !enabled) return;
        if (!IsEditable)
        {
            Error?.Invoke("Read-only Riot reference: Copy To Project to toggle emitters.");
            card.IsEnabled = !card.Entry.Disabled;   // revert the checkbox
            return;
        }
        card.Entry.SetDisabled(!enabled);
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
    public string Detail => $"{Entry.Emitters.Count} emitter(s)";
    public IReadOnlyList<string> EmitterNames { get; }

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
    public ParticleEmitterEntry Entry { get; }
    public string Name { get; }
    public IReadOnlyList<ParticleModuleGroupViewModel> Modules { get; }
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
    public bool HasCurve => Prop.HasCurve;
    public float[]? CurveTimes => Prop.CurveTimes;
    public float[][]? CurveChannels => Prop.CurveChannels;

    public ParticlePropertyRowViewModel(ParticleProperty prop, ParticleEditorViewModel owner)
    {
        Prop = prop;
        _owner = owner;
        _currentText = prop.CurrentText;
        _editText = prop.CurrentText;
    }

    public void Refresh() { CurrentText = Prop.CurrentText; EditText = Prop.CurrentText; }

    [RelayCommand] private void Select() => _owner.SelectRow(this);
}
