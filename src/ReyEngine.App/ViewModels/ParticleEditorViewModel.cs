using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Decoding;
using ReyEngine.Formats.Characters;
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

    /// <summary>M368: class hash -> every property the class DECLARES, from the LeagueToolkit meta database.
    /// Null when it was never synced, in which case the schema panel simply does not appear - the editor has
    /// always worked without it and must keep doing so.</summary>
    public Func<uint, IReadOnlyList<ReyEngine.Core.Meta.MetaProperty>>? DeclaredProperties;

    /// <summary>M368: class hash -> its resolved name, for the card header.</summary>
    public Func<uint, string?>? ClassName;
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

    /// <summary>M197 (4.5): the map VFX bins hold thousands of systems - map22.bin alone gives 2,579 - so a
    /// flat unsearchable list is unusable for the browse case this milestone exists to serve.</summary>
    [ObservableProperty] private string _systemFilter = "";
    private readonly List<ParticleSystemNodeViewModel> _allSystems = new();

    partial void OnSystemFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var keep = SystemFilter;
        var match = string.IsNullOrWhiteSpace(keep)
            ? _allSystems
            : _allSystems.Where(s => s.Name.Contains(keep, StringComparison.OrdinalIgnoreCase)).ToList();

        var previous = SelectedSystem;
        Systems.Clear();
        foreach (var s in match) Systems.Add(s);
        // keep the selection when it survives the filter, so typing does not reset the preview
        SelectedSystem = previous is not null && match.Contains(previous) ? previous : Systems.FirstOrDefault();
    }
    public ObservableCollection<ParticleEmitterCardViewModel> Cards { get; } = new();

    private IReadOnlyDictionary<uint, VfxSystemDefinition> _defs =
        new Dictionary<uint, VfxSystemDefinition>();

    /// <summary>Load a particle .bin into the editor. Returns false when it holds no VFX systems.
    /// Parses on the calling thread - use <see cref="Parse"/> + the pre-parsed overload for large bins.</summary>
    public bool Load(WadAssetEntry entry, byte[] bytes, bool editable)
    {
        var (doc, defs) = Parse(bytes, ResolveBinName);
        return doc is not null && Load(entry, doc, defs, editable);
    }

    /// <summary>M197 (4.5): the expensive half, safe to run off the UI thread. map22.bin measures roughly
    /// 3 seconds through here, which is a visible freeze if it runs on the dispatcher.</summary>
    public static (ParticleDocument? Doc, IReadOnlyDictionary<uint, VfxSystemDefinition> Defs)
        Parse(byte[] bytes, Func<uint, string?>? resolveBinName)
    {
        var doc = ParticleDocument.Parse(bytes, resolveBinName);
        return doc is null
            ? (null, new Dictionary<uint, VfxSystemDefinition>())
            : (doc, VfxSystemResolver.ExtractAll(bytes));
    }

    /// <summary>The UI half: everything here touches observable state and must run on the dispatcher.</summary>
    public bool Load(WadAssetEntry entry, ParticleDocument doc,
        IReadOnlyDictionary<uint, VfxSystemDefinition> defs, bool editable)
    {
        Entry = entry;
        Document = doc;
        AssetName = entry.DisplayName;
        IsEditable = editable;
        _defs = defs;

        _allSystems.Clear();
        foreach (var s in doc.Systems)
        {
            var issues = doc.Issues.Where(i => i.ObjectPathHash == s.PathHash).ToList();
            _allSystems.Add(new ParticleSystemNodeViewModel(s)
            {
                HasIssue = issues.Count > 0,
                IssueTip = issues.Count > 0
                    ? string.Join(Environment.NewLine, issues.Select(i => $"{i.Kind}: {i.Message}"))
                    : null,
            });
        }
        SystemFilter = "";
        ApplyFilter();
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
        // M713: the game's own wiring first - a spell record that names this system as its missile says
        // so, and hands over the speed with it. M712's name guess is the fallback for the systems nothing
        // names, which is about 44% of them, and it stays labelled as a guess.
        var def = _defs.GetValueOrDefault(value.Entry.PathHash);
        if (def is not null && ResolveRole?.Invoke(def) is { } link)
        {
            RoleLink = link;
            var rig = link.Rig(Rig);
            RigMode = rig.Mode;
            RigSpeed = rig.Speed;
            RigNote = link.Why;
        }
        else
        {
            RoleLink = null;
            RigSpeed = ReyEngine.Formats.Vfx.VfxRigDefaults.Speed;
            var guess = ReyEngine.Formats.Vfx.VfxRigNaming.For(value.Entry.Name);
            RigMode = guess.Mode;
            RigNote = guess.Why;
        }
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

    /// <summary>
    /// M651: a list edit, which is a curve edit plus one thing - the number of rows changes, so the card
    /// is rebuilt rather than refreshed. <see cref="EditCurve"/> can refresh in place because a curve
    /// lives inside one row; adding or removing a list item adds or removes rows around it.
    /// </summary>
    internal void EditList(ParticleEmitterCardViewModel card, ParticlePropertyRowViewModel row, Action mutate)
    {
        if (Document is null) return;
        if (!IsEditable) { row.ErrorText = "Read-only: Copy To Project first."; return; }
        int at = Cards.IndexOf(card);
        if (at < 0) return;
        try
        {
            mutate();
            MarkDocumentDirty?.Invoke();
            _defs = VfxSystemResolver.ExtractAll(Document.Serialize());

            var rebuilt = card.Entry is { } emitter
                ? new ParticleEmitterCardViewModel(Document.RebuildRows(emitter), this)
                : SelectedSystem is { } node
                    ? new ParticleEmitterCardViewModel(Document.RebuildRows(node.Entry), this)
                    : null;
            if (rebuilt is not null) { Cards[at] = rebuilt; SelectedProperty = null; }

            RebuildPlayback();
            Info?.Invoke($"{row.Name}: the list now holds {row.Prop.ListCount} item(s).");
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
            emitterReflectionCubemaps: ResolveReflectionCubemaps?.Invoke(def))
            { Seed = RigSeed } });   // M712: the rig's run, rather than one derived from where it stands
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

    // ================================================================ M712: the rig
    //
    // The file says nothing about where an effect goes - a system definition describes emitters, and what
    // makes one a missile is the spell that flies it. So the preview parked everything at the origin, which
    // shows a missile as a puff standing still and a trail with nothing to trail behind. The rig is the
    // reader's answer to "what is this standing in for", opened on a guess from the system's own name.
    //
    // Mode, height and the two toggles are NOT on the playback item: they reach the viewport as a styled
    // property, so dragging the height does not rebuild the playback and re-upload every sprite. The seed
    // is the exception - a simulator's random stream is fixed when it is built - so changing it rebuilds,
    // which is what Restart already does.

    /// <summary>M713: what the GAME says this system is for, when the host can find out.
    ///
    /// <para>Keyed by the system, like every other resolver this view model is given, because it has no
    /// access to a WAD and should not grow one. The host owns the mounts and the champion's other bins;
    /// this asks and does not care how.</para></summary>
    public Func<VfxSystemDefinition, VfxSystemLink?>? ResolveRole;

    [ObservableProperty] private ReyEngine.Formats.Vfx.VfxRigMode _rigMode;
    [ObservableProperty] private double _rigHeight = ReyEngine.Formats.Vfx.VfxRigDefaults.Height;
    [ObservableProperty] private bool _rigReplay;
    [ObservableProperty] private bool _rigStopMidRun;
    [ObservableProperty] private int _rigSeed = ReyEngine.Formats.Vfx.VfxRigDefaults.Seed;
    /// <summary>Why the rig opened where it did. Shown, because a guess the user cannot see is a guess they
    /// cannot correct.</summary>
    [ObservableProperty] private string _rigNote = "";
    /// <summary>M713: the speed a missile rig flies at. Set from the spell record when there is one, and
    /// left at the rig's own default when nothing names this system.</summary>
    [ObservableProperty] private double _rigSpeed = ReyEngine.Formats.Vfx.VfxRigDefaults.Speed;
    /// <summary>M713: the game's own statement about this system, or null when nothing names it. Drives
    /// the badge that tells the reader which of the two they are looking at.</summary>
    [ObservableProperty] private VfxSystemLink? _roleLink;
    /// <summary>True when the rig came from the game's wiring rather than from the system's name.</summary>
    public bool RoleIsAuthored => RoleLink is not null;

    /// <summary>What the viewport carries the system with. Rebuilt from the four settings above, so a
    /// binding on it updates the moment any of them moves.</summary>
    public ReyEngine.Formats.Vfx.VfxPreviewRig Rig => new(
        RigMode, (float)RigHeight, Speed: (float)RigSpeed,
        Replay: RigReplay, StopMidRun: RigStopMidRun);

    // One bool per segment over the enum, which is how a segmented control binds in this codebase. The
    // setter assigns only on true so clicking the segment that is already down does not clear it.
    public bool IsRigStill
    {
        get => RigMode == ReyEngine.Formats.Vfx.VfxRigMode.Still;
        set { if (value) RigMode = ReyEngine.Formats.Vfx.VfxRigMode.Still; }
    }
    public bool IsRigBurst
    {
        get => RigMode == ReyEngine.Formats.Vfx.VfxRigMode.Burst;
        set { if (value) RigMode = ReyEngine.Formats.Vfx.VfxRigMode.Burst; }
    }
    public bool IsRigMissile
    {
        get => RigMode == ReyEngine.Formats.Vfx.VfxRigMode.Missile;
        set { if (value) RigMode = ReyEngine.Formats.Vfx.VfxRigMode.Missile; }
    }
    public bool IsRigTrail
    {
        get => RigMode == ReyEngine.Formats.Vfx.VfxRigMode.Trail;
        set { if (value) RigMode = ReyEngine.Formats.Vfx.VfxRigMode.Trail; }
    }

    partial void OnRigModeChanged(ReyEngine.Formats.Vfx.VfxRigMode value)
    {
        // Burst is Still that starts over, so picking it turns Replay on - otherwise the segment reads as
        // doing nothing at all, which is the shape of a dead control.
        if (value == ReyEngine.Formats.Vfx.VfxRigMode.Burst) RigReplay = true;
        OnPropertyChanged(nameof(IsRigStill));
        OnPropertyChanged(nameof(IsRigBurst));
        OnPropertyChanged(nameof(IsRigMissile));
        OnPropertyChanged(nameof(IsRigTrail));
        OnPropertyChanged(nameof(Rig));
    }
    partial void OnRigHeightChanged(double value) => OnPropertyChanged(nameof(Rig));
    partial void OnRigSpeedChanged(double value) => OnPropertyChanged(nameof(Rig));
    partial void OnRoleLinkChanged(VfxSystemLink? value) => OnPropertyChanged(nameof(RoleIsAuthored));
    partial void OnRigReplayChanged(bool value) => OnPropertyChanged(nameof(Rig));
    partial void OnRigStopMidRunChanged(bool value) => OnPropertyChanged(nameof(Rig));
    partial void OnRigSeedChanged(int value) => RebuildPlayback();

    /// <summary>A different run of the same system. The seed is the whole of a run's randomness, so this is
    /// the only way to see the effect play differently without editing it.</summary>
    [RelayCommand] private void RerollSeed() => RigSeed = unchecked(RigSeed * 1103515245 + 12345) & 0x7fffffff;

    [RelayCommand] private void Restart() { Stopped = false; RebuildPlayback(); }
    [RelayCommand] private void TogglePause() => Paused = !Paused;

    [RelayCommand]
    private void ApplyEdit(ParticlePropertyRowViewModel? row)
    {
        if (row is null || Document is null) return;
        if (!IsEditable) { row.ErrorText = "Read-only: Copy To Project first."; return; }
        // M523: the row already knows WHY it is read-only - a struct header, an empty optional, a
        // probability table that has keys rather than a value - and one blanket sentence misattributes
        // most of them.
        if (row.Prop.IsReadOnly) { row.ErrorText = row.ReadOnlyReason; return; }
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
                    .Select(p => new ParticlePropertyRowViewModel(p, owner) { Card = this }).ToList()))
            .ToList();
        var texPath = emitter.Properties.FirstOrDefault(p => p.Name == "texture")?.CurrentText;
        if (!string.IsNullOrWhiteSpace(texPath))
            try { Thumbnail = owner.LoadThumbnail?.Invoke(texPath); } catch { Thumbnail = null; }
        Schema = MetaSchemaPanelViewModel.Build(
            emitter.ClassHash, emitter.PresentHashes, owner.DeclaredProperties, owner.ClassName,
            emitter.TryAddDefaultProperty, owner.IsEditable);
    }

    /// <summary>M368: the fields this object does NOT carry, and what the game uses instead. M370 makes
    /// each row writable. Shared with the material editor - see <see cref="MetaSchemaPanelViewModel"/> for
    /// why it lives there rather than being written twice.</summary>
    public MetaSchemaPanelViewModel Schema { get; private set; } = MetaSchemaPanelViewModel.None;

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
                    .Select(p => new ParticlePropertyRowViewModel(p, owner) { Card = this }).ToList()))
            .ToList();
        Schema = MetaSchemaPanelViewModel.Build(
            system.ClassHash, system.PresentHashes, owner.DeclaredProperties, owner.ClassName,
            system.TryAddDefaultProperty, owner.IsEditable);
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

    /// <summary>M651: the card this row is shown on. A list edit changes how many rows there are, so the
    /// host rebuilds this card rather than refreshing the row.</summary>
    internal ParticleEmitterCardViewModel? Card { get; init; }

    /// <summary>M651: true when this row is one item of a list, so the item buttons appear on it.</summary>
    public bool IsListItem => Prop.IsListItem && _owner.IsEditable;

    /// <summary>The last item of a list stays - Riot ships no empty containers anywhere and an empty one
    /// crashes the game at map load.</summary>
    public bool CanRemoveItem => Prop.CanRemoveFromList && _owner.IsEditable;

    public string RemoveItemTip => Prop.ListBlockedReason is { Length: > 0 } why
        ? why
        : $"Remove item {Prop.ListIndex} of {Prop.ListCount}.";

    public string DuplicateItemTip =>
        $"Insert a copy of item {Prop.ListIndex} right after it. The copy is exactly what Riot wrote, "
        + "so it is valid before you have changed anything in it.";

    [RelayCommand]
    private void DuplicateItem()
    {
        if (Card is { } card) _owner.EditList(card, this, Prop.DuplicateInList);
    }

    [RelayCommand]
    private void RemoveItem()
    {
        if (Card is { } card) _owner.EditList(card, this, Prop.RemoveFromList);
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
