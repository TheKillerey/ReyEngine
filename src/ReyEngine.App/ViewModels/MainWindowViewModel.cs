using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.App.Documents;
using ReyEngine.App.Imaging;
using ReyEngine.App.Services;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Build;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Painting;
using ReyEngine.Core.Diagnostics;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Core.Selection;
using ReyEngine.Core.Undo;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Animation;
using ReyEngine.Formats.Lighting;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Vfx;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Shaders;
using ReyEngine.Formats.Skeletons;
using ReyEngine.Rendering;

namespace ReyEngine.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly Logger _log = new();
    private readonly HashSyncService _sync = new();
    private readonly WadPathResolver _resolver;
    private WadArchive? _archive;
    private AssetMountService? _mounts;          // project mode: the virtual file system
    private string? _lastGameFallbackNotice;
    private readonly AssetOverrideStore _overrides = new();
    private readonly ReyEngine.App.Services.ThumbnailService _thumbnails; // Content Browser lazy thumbnails
    private readonly Dictionary<ulong, AssetNodeViewModel> _nodesByHash = new();
    private WorkshopCatalogService? _workshopCatalog;

    private bool ContentLoaded => _archive is not null || _mounts is not null;

    /// <summary>Read an asset's bytes, mount-aware (project mode) or override-aware (single WAD).</summary>
    private byte[] ReadAsset(ulong hash)
    {
        if (_mounts is not null)
            return _mounts.Read(hash) ?? throw new FileNotFoundException($"0x{hash:x16} not in any mount.");
        if (_overrides.TryGet(hash, out var ov) && File.Exists(ov.OverrideFile)) return File.ReadAllBytes(ov.OverrideFile);
        return _archive!.Extract(hash);
    }

    private bool TryResolveEntry(ulong hash, out WadAssetEntry entry)
    {
        if (_mounts is not null)
        {
            if (_mounts.TryGet(hash, out var a)) { entry = a.ToEntry(); return true; }
            entry = null!; return false;
        }
        if (_archive is not null) return _archive.TryGetEntry(hash, out entry!);
        entry = null!; return false;
    }

    private IEnumerable<WadAssetEntry> AssetEntries =>
        _mounts is not null ? _mounts.Assets.Select(a => a.ToEntry())
        : _archive is not null ? _archive.Entries
        : Enumerable.Empty<WadAssetEntry>();

    public DialogService Dialogs { get; } = new();
    public ConsoleViewModel Console { get; } = new();
    public InspectorViewModel Inspector { get; } = new();
    public MeshInspectorViewModel MeshInspector { get; } = new();
    public MapGeoInspectorViewModel MapGeoInspector { get; } = new();
    public AnimationInspectorViewModel Animation { get; } = new();
    public ObservableCollection<AssetNodeViewModel> RootNodes { get; } = new();
    public BinEditorViewModel BinEditor { get; } = new();
    public MaterialEditorViewModel MaterialEditor { get; } = new();
    public ContentBrowserViewModel ContentBrowser { get; } = new();
    public MapContentViewModel MapContent { get; } = new();

    // ---- Undo/Redo (M29) -------------------------------------------------
    public UndoRedoService UndoService { get; } = new();
    public bool CanUndo => UndoService.CanUndo;
    public bool CanRedo => UndoService.CanRedo;
    public string UndoLabel => UndoService.UndoName is { } u ? $"Undo {u}" : "Undo";
    public string RedoLabel => UndoService.RedoName is { } r ? $"Redo {r}" : "Redo";

    [RelayCommand] private void Undo() => UndoService.Undo();
    [RelayCommand] private void Redo() => UndoService.Redo();
    public ObservableCollection<RecentProjectViewModel> RecentProjectList { get; } = new();
    public bool HasRecentProjects => RecentProjectList.Count > 0;

    [ObservableProperty] private AssetNodeViewModel? _selectedNode;
    [ObservableProperty] private bool _projectMode;
    [ObservableProperty] private bool _inspectionMode;
    [ObservableProperty] private string _title = "ReyEngine";
    [ObservableProperty] private string _status = "Ready — open a .wad.client to begin";
    [ObservableProperty] private string _hashInput = "";
    [ObservableProperty] private ReyProject _project = new();
    [ObservableProperty] private bool _isBuilding;

    // M131: determinate build/export progress in the status bar
    [ObservableProperty] private double _buildProgress;          // 0..100
    [ObservableProperty] private string _buildStage = "";
    public bool BuildProgressActive => IsBuilding;
    partial void OnIsBuildingChanged(bool value)
    {
        if (!value) { BuildProgress = 0; BuildStage = ""; }
        OnPropertyChanged(nameof(BuildProgressActive));
    }

    /// <summary>UI-thread progress sink for build/export pipelines.</summary>
    private IProgress<(double Frac, string Stage)> BuildProgressSink() =>
        new Progress<(double Frac, string Stage)>(t =>
        {
            BuildProgress = Math.Clamp(t.Frac, 0, 1) * 100.0;
            BuildStage = t.Stage;
            Status = t.Stage;
        });

    // Viewport-bound state
    [ObservableProperty] private MeshAsset? _currentMesh;
    [ObservableProperty] private SkeletonAsset? _currentSkeleton;
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _currentModelTextures;
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _currentModelMaskTextures;
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _currentModelGradientTextures;
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _currentModelEmissiveTextures;
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _currentModelMatCapTextures;
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _currentModelMatCapMaskTextures;
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _currentModelLightmapTextures; // M33: per-submesh baked lightmap atlas
    [ObservableProperty] private IReadOnlyList<bool>? _currentModelSubmeshVisible;

    // M35: placed particle systems (MapParticle) on the current map.
    [ObservableProperty] private IReadOnlyList<MapParticlePlacement>? _currentModelParticles;
    [ObservableProperty] private bool _showParticles = true;
    [ObservableProperty] private object? _selectedParticleTreeItem;                               // TreeView selection (group or leaf)
    [ObservableProperty] private ParticlePlacementViewModel? _selectedParticleNode;               // the selected placement (leaf)
    [ObservableProperty] private IReadOnlyList<System.Numerics.Vector3>? _particleMarkers;         // positions shown in the viewport
    [ObservableProperty] private System.Numerics.Vector3? _selectedParticleMarker;
    [ObservableProperty] private System.Numerics.Vector3? _particleFocusPoint;                     // set to recentre the camera

    public bool HasParticles => MapContent.HasParticles;

    partial void OnShowParticlesChanged(bool value) => UpdateParticleMarkers();
    partial void OnCurrentModelParticlesChanged(IReadOnlyList<MapParticlePlacement>? value)
    {
        MapContent.SetParticles(value ?? Array.Empty<MapParticlePlacement>());
        OnPropertyChanged(nameof(HasParticles));
        UpdateParticleMarkers();
    }
    partial void OnSelectedParticleTreeItemChanged(object? value)
        => SelectedParticleNode = value as ParticlePlacementViewModel;

    // ---- M205: the re-link picker -------------------------------------------------------------------
    // MapPlaceableWriter has accepted a SystemLink since M199 and M204 wired it to the view model, but
    // there was no way to choose a system. A map bin defines a few hundred of them, so this is a filtered
    // list rather than a bare dropdown - the same shape the Particle Editor's SYSTEMS panel uses.

    /// <summary>Every VFX system defined in the loaded map, sorted, for the re-link picker.</summary>
    private readonly List<VfxSystemItemViewModel> _relinkAll = new();
    public ObservableCollection<VfxSystemItemViewModel> RelinkChoices { get; } = new();

    [ObservableProperty] private string _relinkFilter = "";
    [ObservableProperty] private VfxSystemItemViewModel? _selectedRelinkChoice;

    partial void OnRelinkFilterChanged(string value) => ApplyRelinkFilter();

    partial void OnSelectedRelinkChoiceChanged(VfxSystemItemViewModel? value)
    {
        if (SelectedParticleNode is not { } node) return;
        // Choosing the placement's CURRENT system clears the edit rather than recording a no-op re-link.
        node.EditedSystemHash = value is null || value.Hash == node.Placement.SystemHash ? 0u : value.Hash;
        HasParticleMoves = MapContent.AllParticles.Any(v => v.HasEdits) || MapContent.Sounds.Any(s => s.IsMoved);
        RebuildParticlePlayback();
    }

    /// <summary>Rebuild the candidate list from the loaded map. Only systems with a visual emitter: linking
    /// a placement to a system that draws nothing would look like a broken save.</summary>
    private void RebuildRelinkChoices()
    {
        _relinkAll.Clear();
        foreach (var s in _vfxSystems.Values
                     .Where(s => s.Emitters.Any(e => e.IsVisual))
                     .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            _relinkAll.Add(new VfxSystemItemViewModel { Hash = s.PathHash, Name = s.Name, EmitterCount = s.Emitters.Count(e => e.IsVisual) });
        ApplyRelinkFilter();
    }

    private void ApplyRelinkFilter()
    {
        var keep = RelinkFilter;
        RelinkChoices.Clear();
        foreach (var c in string.IsNullOrWhiteSpace(keep)
                     ? _relinkAll
                     : _relinkAll.Where(c => c.Name.Contains(keep, StringComparison.OrdinalIgnoreCase)))
            RelinkChoices.Add(c);
    }

    /// <summary>Point the picker at what the selected placement currently links to, WITHOUT recording an
    /// edit - assigning SelectedRelinkChoice fires its own handler, which compares against the authored
    /// hash and clears the edit when they match.</summary>
    private void SyncRelinkPicker(ParticlePlacementViewModel? node)
    {
        if (node is null) { SelectedRelinkChoice = null; return; }
        uint current = node.EditedSystemHash != 0 ? node.EditedSystemHash : node.Placement.SystemHash;
        SelectedRelinkChoice = _relinkAll.FirstOrDefault(c => c.Hash == current);
    }

    partial void OnSelectedParticleNodeChanged(ParticlePlacementViewModel? value)
    {
        SelectedParticleMarker = value?.CurrentPosition;
        RefreshParticleMoveFields(value);
        SyncRelinkPicker(value);   // M205
        if (value is { } p)
        {
            ShowParticles = true;
            // M55b: selection no longer moves the camera — use the Focus button/command instead
            // M50b: exclusive selection — a particle selection deselects meshes/props/probes
            _selection.Clear();
            if (SelectedPropTreeItem is not null) SelectedPropTreeItem = null;
            if (SelectedProbe is not null) SelectedProbe = null;
            GizmoPivot = p.CurrentPosition;   // M75: the gizmo now works on placements too
        }
        else if (_selection.IsEmpty && SelectedSound is null) GizmoPivot = null;
        RebuildParticlePlayback();   // M36: play the newly-selected system (or stop if none)
    }

    /// <summary>M50b: one material slot of the selected mesh (Unity Mesh-Renderer style).</summary>
    public sealed record MeshMaterialSlotViewModel(string Name, string Detail);

    [ObservableProperty] private IReadOnlyList<int>? _selectedSubmeshIndices;              // M50b: outline highlight
    [ObservableProperty] private IReadOnlyList<MeshMaterialSlotViewModel>? _selectedMeshMaterials;
    [ObservableProperty] private bool _hasSelectedMeshMaterials;
    [ObservableProperty] private bool _assetDataExpanded;   // M50b: Overview/Materials/Raw-BIN hidden until wanted

    /// <summary>Open a selected-mesh material in the full Materials editor (expands the asset-data area).</summary>
    [RelayCommand]
    private void EditSelectedMaterial(MeshMaterialSlotViewModel? slot)
    {
        if (slot is null) return;
        AssetDataExpanded = true;
        InspectorTab = 1;
        MaterialEditor.Search = slot.Name;
        MaterialEditor.AutoPreviewDiffuse(slot.Name);   // M50c: show the texture immediately
    }

    /// <summary>M195 (4.4): particles now resolve through the SAME controller path meshes already use
    /// (see UpdateSubmeshVisibility). 4,237 placements bind a VisibilityController that this ignored, so
    /// they stayed visible while the meshes around them switched. Placements use the same map-defined
    /// axes and controller graph as geometry.</summary>
    private bool IsParticleVisible(MapParticlePlacement particle, int? visibilityOverride = null) =>
        particle.VisibilityControllerHash == 0
            ? MapVisibility.VisibleForMask(visibilityOverride ?? particle.VisibilityFlags, _mapVisibility.Primary, CurrentPrimaryVisibilityBit)
            : (_visibilityResolver ??= new MapVisibilityResolver(_mapControllers, _mapVisibility))
                .IsVisible(visibilityOverride ?? particle.VisibilityFlags, particle.VisibilityControllerHash, CurrentVisibilitySelections);

    private bool IsSoundVisible(MapSoundPlacement sound, int? visibilityOverride = null) =>
        MapVisibility.VisibleForMask(visibilityOverride ?? sound.VisibilityFlags, _mapVisibility.Primary, CurrentPrimaryVisibilityBit);

    private void UpdateParticleMarkers() =>
        ParticleMarkers = (ShowParticles && MapContent.HasParticles)
            ? MapContent.AllParticles.Where(v => v.IsEditorVisible && !v.IsDisabled && !v.IsRemoved
                && IsParticleVisible(v.Placement, v.EffectiveVisibilityFlags)).Select(v => v.CurrentPosition).ToList() : null;

    // ---- M38: cubemap probes + animated props (placed characters) ----
    [ObservableProperty] private IReadOnlyList<MapCubemapProbe>? _currentModelProbes;
    [ObservableProperty] private IReadOnlyList<MapAnimatedProp>? _currentModelProps;
    [ObservableProperty] private IReadOnlyList<System.Numerics.Vector3>? _propMarkers;
    [ObservableProperty] private IReadOnlyList<System.Numerics.Vector3>? _probeMarkers;
    [ObservableProperty] private bool _showPlaceables = true;
    [ObservableProperty] private bool _playPropAnimations;   // M54: play prop idle animations in the viewport

    // ---- M55: sound placements (MapAudio) + bucket-grid overlay ----
    [ObservableProperty] private IReadOnlyList<MapSoundPlacement>? _currentModelSounds;
    [ObservableProperty] private IReadOnlyList<System.Numerics.Vector3>? _soundMarkers;
    [ObservableProperty] private bool _showBucketGrid;
    [ObservableProperty] private float[]? _bucketGridLines;

    partial void OnCurrentModelSoundsChanged(IReadOnlyList<MapSoundPlacement>? value)
    { MapContent.SetSounds(value ?? Array.Empty<MapSoundPlacement>()); UpdatePlaceableMarkers(); }

    // ---- M56: Wwise audio — banks, one-shot playback, positional map ambience ----
    public Services.SoundPlaybackService Sound { get; } = new();
    /// <summary>M138: wav/mp3/ogg → .wem via Wwise's own encoder (League ships Vorbis wems only).</summary>
    public Services.WemEncoder Encoder { get; } = new();
    /// <summary>M138: recovered Wwise event names (id → name), cached across sessions.</summary>
    public Services.WwiseNameIndex WwiseNames { get; private set; } = Services.WwiseNameIndex.Load();

    /// <summary>M138: rebuild the Wwise event-name index by harvesting name strings from every mounted
    /// .bin and matching their FNV-1 hashes against the ids in the mounted event banks.</summary>
    [RelayCommand]
    private async Task RebuildWwiseNames()
    {
        if (!ContentLoaded) { _log.Warn("Audio", "Open a project or WAD first."); return; }
        IsBuilding = true;
        var progress = BuildProgressSink();
        try
        {
            var result = await Task.Run(() =>
            {
                var wanted = new HashSet<uint>();
                var eventBanks = AssetEntries.Where(e => e.IsResolved
                    && e.Path.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase)).ToList();
                int i = 0;
                foreach (var e in eventBanks)
                {
                    if (++i % 25 == 0) progress.Report((0.35 * i / Math.Max(1, eventBanks.Count), $"Reading banks… {i}/{eventBanks.Count}"));
                    try
                    {
                        if (Formats.Audio.BnkFile.Parse(ReadAsset(e.PathHash)) is { HasHirc: true } b)
                            foreach (var id in b.Events.Keys) wanted.Add(id);
                    }
                    catch { }
                }

                var idx = new Services.WwiseNameIndex();
                var bins = AssetEntries.Where(e => e.IsResolved
                    && e.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)).ToList();
                i = 0;
                foreach (var e in bins)
                {
                    if (++i % 100 == 0) progress.Report((0.35 + 0.6 * i / Math.Max(1, bins.Count), $"Scanning bins… {i:n0}/{bins.Count:n0}"));
                    try
                    {
                        var strings = new List<string>();
                        Formats.Meta.BinStringHarvester.Collect(
                            Formats.Meta.SafeBinTree.Parse(ReadAsset(e.PathHash)), strings);
                        idx.Harvest(strings, wanted);
                    }
                    catch { }
                }
                progress.Report((0.97, "Deriving sibling events…"));
                int derived = idx.ExpandVerbs(wanted);
                return (Index: idx, Wanted: wanted.Count, Derived: derived);
            });

            result.Index.Merge(WwiseNames);   // keep anything a previous scan found
            WwiseNames = result.Index;
            WwiseNames.Save();
            _log.Success("Audio", $"Wwise names: {WwiseNames.Count:n0} of {result.Wanted:n0} event id(s) resolved "
                + $"({100.0 * WwiseNames.Count / Math.Max(1, result.Wanted):0.#}%, {result.Derived} derived from Play_/Stop_ siblings). Cached for next time.");
        }
        catch (Exception ex) { _log.Error("Audio", ex.Message); }
        finally { IsBuilding = false; }
    }
    private Formats.Audio.AudioBankSet? _mapAudioBanks;
    [ObservableProperty] private MapSoundViewModel? _selectedSound;
    [ObservableProperty] private bool _ambienceEnabled;
    [ObservableProperty] private string _audioStatus = "";
    private System.Numerics.Vector3 _lastCamPosForAudio;

    /// <summary>Load the map's Wwise banks (env/mus events + audio bnk/wpk under sounds/wwise matching
    /// mapN). Called from the map-load background task; cheap misses are fine.</summary>
    private void LoadMapAudioBanks(string mapgeoPath, IReadOnlyList<MapSoundPlacement> sounds)
    {
        _mapAudioBanks = null;
        AudioStatus = "";
        var m = System.Text.RegularExpressions.Regex.Match(mapgeoPath, @"map(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return;
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $"map{m.Groups[1].Value}" };
        var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "env_", "mus_" };
        foreach (var sound in sounds)
        {
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                         sound.EventName, @"_map(\d+)_", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                tags.Add($"map{match.Groups[1].Value}");
            if (sound.EventName.Contains("_Env_", StringComparison.OrdinalIgnoreCase)) families.Add("env_");
            if (sound.EventName.Contains("_Mus_", StringComparison.OrdinalIgnoreCase)) families.Add("mus_");
            if (sound.EventName.Contains("_Misc_", StringComparison.OrdinalIgnoreCase)) families.Add("misc_");
            if (sound.EventName.Contains("_Npc_", StringComparison.OrdinalIgnoreCase)) families.Add("npc_");
        }
        var set = new Formats.Audio.AudioBankSet();
        int banks = 0, packs = 0;
        foreach (var e in AssetEntries)
        {
            if (!e.IsResolved) continue;
            var p = e.Path;
            // Load only shared bank families referenced by this map. Map11 materials can carry
            // historical Map1/Map10 VFX-audio events while current assets use Map11.
            if (!p.Contains("sounds/wwise", StringComparison.OrdinalIgnoreCase)
                || !p.Contains("/sfx/shared/", StringComparison.OrdinalIgnoreCase)) continue;
            var file = Path.GetFileName(p);
            if (!families.Any(prefix => file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) continue;
            if (!file.Contains("_global_", StringComparison.OrdinalIgnoreCase)
                && !tags.Any(tag => file.Contains(tag, StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                if (p.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase))
                { if (Formats.Audio.BnkFile.Parse(ReadAsset(e.PathHash)) is { } b) { set.AddBank(b, e.PathHash, e.Path); banks++; } }
                else if (p.EndsWith(".wpk", StringComparison.OrdinalIgnoreCase))
                { if (Formats.Audio.WpkFile.Parse(ReadAsset(e.PathHash)) is { } w) { set.AddPack(w, e.PathHash, e.Path); packs++; } }
            }
            catch { /* skip broken/subchunked banks */ }
        }
        if (!set.IsEmpty)
        {
            _mapAudioBanks = set;
            _log.Info("Audio", $"{string.Join('/', tags.Order())}: {banks} bank(s) + {packs} wem pack(s) — {set.EventCount} event(s), {set.WemCount} wem(s)." +
                               (Sound.IsAvailable ? "" : " vgmstream-cli NOT found — playback disabled."));
        }
    }

    /// <summary>Resolve + decode + play one wem of the selected sound's event (one-shot).</summary>
    [RelayCommand]
    private void PlaySelectedSound()
    {
        if (SelectedSound is not { } snd) return;
        if (_mapAudioBanks is null) { AudioStatus = "No audio banks loaded for this map."; return; }
        if (!Sound.IsAvailable) { AudioStatus = "vgmstream-cli.exe not found (needed to decode Wwise Vorbis)."; return; }
        var wems = _mapAudioBanks.ResolveEvent(snd.EventName);
        if (wems.Count == 0) { AudioStatus = $"Event not found in the loaded banks: {snd.EventName}"; return; }
        var wemData = wems.Select(id => (Id: id, Data: _mapAudioBanks.GetWemData(id))).FirstOrDefault(x => x.Data is not null);
        if (wemData.Data is null) { AudioStatus = $"wem data missing ({wems.Count} candidate id(s))."; return; }
        var wav = Sound.DecodeToWav(wemData.Id, wemData.Data);
        if (wav is null) { AudioStatus = "Decode failed."; return; }
        Sound.PlayWav(wav, 1f, loop: false, tag: "oneshot");
        AudioStatus = $"Playing {snd.EventName} (wem {wemData.Id}, {wems.Count} candidate(s)).";
    }

    [RelayCommand]
    private void StopAllSounds() { Sound.StopAll(); AudioStatus = ""; }

    /// <summary>M57: replace the wem behind the selected sound's event with an imported .wem file, rebuild
    /// the owning bank/pack, validate it re-parses + decodes, and save it to the project override.</summary>
    [RelayCommand]
    private async Task ReplaceSelectedSoundWem()
    {
        if (SelectedSound is not { } snd || _mapAudioBanks is null) return;
        var wems = _mapAudioBanks.ResolveEvent(snd.EventName);
        var targetId = wems.FirstOrDefault(id => _mapAudioBanks.SourceOf(id) is not null);
        if (targetId == 0) { AudioStatus = "This event has no editable embedded wem in the loaded banks."; return; }
        if (_mapAudioBanks.SourceOf(targetId) is not { } src) return;
        if (!TryResolveEntry(src.PathHash, out var bankEntry)) { AudioStatus = "Bank asset not resolvable for override."; return; }
        if (!GuardEditable(bankEntry)) return;

        var file = await Dialogs.OpenFileAsync($"Replace wem {targetId} (.wem)",
            new Avalonia.Platform.Storage.FilePickerFileType("Wwise wem") { Patterns = new[] { "*.wem" } }, DialogService.All);
        if (file is null) return;
        if (!await EnsureProjectSavedAsync()) return;

        try
        {
            var newData = await File.ReadAllBytesAsync(file);
            // sanity: League wems are RIFF/WAVE
            if (newData.Length < 12 || newData[0] != (byte)'R' || newData[1] != (byte)'I' || newData[2] != (byte)'F' || newData[3] != (byte)'F')
            { AudioStatus = "Not a RIFF/WAVE .wem file. Convert to .wem first (e.g. via a Wwise tool)."; return; }

            var rebuilt = _mapAudioBanks.ReplaceWem(targetId, newData);
            if (rebuilt is not { } rb) { AudioStatus = "Rebuild failed (wem not embedded here)."; return; }

            // validate: the rebuilt bank/pack must re-parse and the new wem must decode
            bool reparse = src.Bnk is not null
                ? Formats.Audio.BnkFile.Parse(rb.Bytes)?.GetWemData(targetId) is not null
                : Formats.Audio.WpkFile.Parse(rb.Bytes)?.GetWemData(targetId) is not null;
            if (!reparse) { AudioStatus = "Rebuilt bank failed to re-parse — NOT saved."; return; }
            if (Sound.DecodeToWav(targetId, newData) is null)
                _log.Warn("Audio", "Imported wem didn't decode with vgmstream — saving anyway (it may still be valid in-game).");

            var dest = ProjectWorkspace.StoreOverrideBytes(Project, bankEntry.PathHash, rb.Bytes, Path.GetExtension(rb.Path));
            _overrides.Set(new ProjectAssetOverride
            {
                PathHash = bankEntry.PathHash,
                ResolvedPath = bankEntry.IsResolved ? bankEntry.Path : null,
                OverrideFile = dest,
                AddedUtc = DateTime.UtcNow.ToString("o"),
            });
            SetNodeStatus(bankEntry.PathHash, AssetStatus.Modified);
            Project.IsDirty = true;
            UpdateTitle();
            Sound.ClearCache(targetId);   // so Play uses the new audio
            AudioStatus = $"Replaced wem {targetId} in {Path.GetFileName(rb.Path)} ({rb.Bytes.Length:n0} B). Build Package will include it.";
            _log.Success("Audio", $"Replaced wem {targetId} for '{snd.EventName}' in {Path.GetFileName(rb.Path)} → override.");
        }
        catch (Exception ex) { _log.Error("Audio", ex.Message); AudioStatus = ex.Message; }
    }

    /// <summary>M70: load a legacy Riot Light.dat point-light table and render it as dynamic point lights.
    /// The lights are in the file's own map world space, so it lines up when the loaded map matches the
    /// Light.dat (e.g. the old Map1 file on classic SR geometry).</summary>
    [RelayCommand]
    private async Task LoadLightDat()
    {
        var file = await Dialogs.OpenFileAsync("Load Riot Light.dat (point lights)",
            new Avalonia.Platform.Storage.FilePickerFileType("Light.dat") { Patterns = new[] { "*.dat" } }, DialogService.All);
        if (file is null) return;
        try
        {
            var lights = LightDatFile.Parse(await File.ReadAllBytesAsync(file));
            if (lights.Count == 0) { _log.Warn("Lights", $"No point lights parsed from {Path.GetFileName(file)}."); return; }
            LightDatPath = file;              // M152: Save writes straight back here
            LoadEditableLights(lights);       // republishes DynamicLights + status
            ShowDynamicLights = true;
            _log.Success("Lights", $"Loaded {lights.Count} point light(s) from {Path.GetFileName(file)}. Toggle 'Lights' in the viewport toolbar.");
        }
        catch (Exception ex) { _log.Error("Lights", ex.Message); }
    }

    partial void OnAmbienceEnabledChanged(bool value)
    {
        if (!value) { Sound.StopAll(); return; }
        UpdateAmbience(_lastCamPosForAudio, force: true);
    }

    /// <summary>M56: positional ambience — loop the nearest sound placements with distance-based volume.
    /// Called from the viewport when the camera moves.</summary>
    public void UpdateAmbience(System.Numerics.Vector3 camPos, bool force = false)
    {
        _lastCamPosForAudio = camPos;
        if (!AmbienceEnabled || _mapAudioBanks is null || !Sound.IsAvailable) return;

        const int maxVoices = 6;
        var nearest = MapContent.Sounds
            .Select((vm, i) => (Vm: vm, Index: i))
            .Where(x => x.Vm.IsEditorVisible && !x.Vm.IsDisabled && !x.Vm.IsRemoved
                && IsSoundVisible(x.Vm.Sound, x.Vm.EffectiveVisibilityFlags))
            .Select(x => (Sound: x.Vm.Sound, x.Index, Dist: System.Numerics.Vector3.Distance(x.Vm.Position, camPos)))
            .Where(x => x.Dist < x.Sound.Radius)
            .OrderBy(x => x.Dist)
            .Take(maxVoices)
            .ToList();

        var wanted = new HashSet<string>(nearest.Select(x => $"amb:{x.Index}"));
        // stop voices out of range
        foreach (var s in _activeAmbience.ToList())
            if (!wanted.Contains(s)) { Sound.StopTag(s); _activeAmbience.Remove(s); }
        // start/adjust in-range voices
        foreach (var x in nearest)
        {
            string voiceTag = $"amb:{x.Index}";
            float vol = Math.Clamp(1f - x.Dist / Math.Max(1f, x.Sound.Radius), 0f, 1f);
            if (_activeAmbience.Contains(voiceTag))
            {
                if (Sound.IsTagPlaying(voiceTag)) Sound.SetTagVolume(voiceTag, vol);
                continue;
            }
            var wems = _mapAudioBanks.ResolveEvent(x.Sound.EventName);
            var wem = wems.Select(id => (Id: id, Data: _mapAudioBanks.GetWemData(id))).FirstOrDefault(w => w.Data is not null);
            if (wem.Data is null) continue;
            var wav = Sound.DecodeToWav(wem.Id, wem.Data);
            if (wav is null) continue;
            Sound.PlayWav(wav, vol, loop: x.Sound.Loop, tag: voiceTag);
            _activeAmbience.Add(voiceTag);
        }
    }
    private readonly HashSet<string> _activeAmbience = new();

    // M77b: the toolbar toggle is the ONLY control of the overlay — selection never shows or hides it.
    partial void OnShowBucketGridChanged(bool value) => RebuildBucketGridLines();

    /// <summary>M77: the loaded map has culling grids (drives the toolbar toggle/rebuild visibility).</summary>
    [ObservableProperty] private bool _hasBucketGrids;

    /// <summary>M77: regenerate every bucket grid from the map's CURRENT world-space triangles (uses the
    /// M58 builder — same rules the game data follows). Preview updates immediately; saving the map writes
    /// the regenerated grids into the mapgeo (the save path re-runs the builder over the final geometry).</summary>
    [RelayCommand]
    private async Task RebuildBucketGrids()
    {
        if (_currentMap is not { } map) { _log.Warn("BucketGrid", "Load a map first."); return; }
        Status = "Rebuilding bucket grids…";
        try
        {
            var grids = await Task.Run(() => MapBucketGridBuilder.Rebuild(map));
            var infos = grids.Select(g =>
            {
                var mp = new float[g.Vertices.Count * 3];
                for (int i = 0; i < g.Vertices.Count; i++)
                { mp[i * 3] = g.Vertices[i].X; mp[i * 3 + 1] = g.Vertices[i].Y; mp[i * 3 + 2] = g.Vertices[i].Z; }
                // Bucket-grid indices are PER-BUCKET LOCAL (BaseVertex + u16) — resolve to global for preview.
                var resolved = new List<int>(g.Indices.Count);
                foreach (var cell in g.Buckets)
                {
                    int faces = cell.InsideFaceCount + cell.StickingOutFaceCount;
                    for (int f = 0; f < faces; f++)
                    {
                        int i0 = (int)cell.StartIndex + f * 3;
                        if (i0 + 2 >= g.Indices.Count) break;
                        int a = (int)cell.BaseVertex + g.Indices[i0];
                        int b = (int)cell.BaseVertex + g.Indices[i0 + 1];
                        int c = (int)cell.BaseVertex + g.Indices[i0 + 2];
                        if (a >= g.Vertices.Count || b >= g.Vertices.Count || c >= g.Vertices.Count) continue;
                        resolved.Add(a); resolved.Add(b); resolved.Add(c);
                    }
                }
                return new MapBucketGridInfo(g.Key.ControllerHash, g.MinX, g.MinZ, g.MaxX, g.MaxZ,
                    g.BucketSizeX, g.BucketSizeZ, g.BucketsPerSide, g.BucketsPerSide,
                    false, g.Vertices.Count, g.Indices.Count, g.Key.RegionHash, mp, resolved.ToArray());
            }).ToList();
            map.BucketGrids = infos;
            MapContent.SetBucketGrids(infos);
            HasBucketGrids = infos.Count > 0;
            ShowBucketGrid = true;
            RebuildBucketGridLines();
            _log.Success("BucketGrid", $"Rebuilt {infos.Count} grid(s) from the current geometry — " +
                $"{infos.Sum(i => i.VertexCount):n0} baked vert(s) / {infos.Sum(i => i.IndexCount) / 3:n0} tri(s). " +
                "Saving the map writes them into the mapgeo.");
            Status = "Bucket grids rebuilt";
        }
        catch (Exception ex) { _log.Error("BucketGrid", ex.Message); Status = "Bucket grid rebuild failed"; }
    }

    /// <summary>M55b: explicitly frame the camera on the selected placeable (selection itself no longer
    /// moves the camera — Unity-style: select is passive, Focus is an action).</summary>
    [RelayCommand]
    private void FocusSelectedPlaceable()
    {
        if (SelectedParticleMarker is { } pos) ParticleFocusPoint = pos;
    }

    /// <summary>M55/M77b: bucket-grid overlay — the grid's COMPLETE baked scene mesh as 3D wireframe
    /// (every unique triangle edge; a bucket grid is a simplified bake of the map). No flat cell lines,
    /// no sampling. PERF: the array builds OFF the UI thread (a master grid holds 600k+ triangles) and
    /// uploads once; stale builds are dropped when the map/toggle changes mid-build.</summary>
    private int _bucketLinesBuildId;
    private async void RebuildBucketGridLines()
    {
        if (!ShowBucketGrid || _currentMap is not { } map || map.BucketGrids.Count == 0)
        { BucketGridLines = null; return; }
        int buildId = ++_bucketLinesBuildId;
        var grids = map.BucketGrids;
        var lines = await Task.Run(() => BuildBucketGridLineArray(grids));
        if (buildId != _bucketLinesBuildId || !ShowBucketGrid) return;   // superseded while building
        BucketGridLines = lines;
    }

    /// <summary>M77b: pos3+bary3 triangle soup (6 floats/vertex) — the viewport draws it with the
    /// barycentric wireframe shader, giving the full-mesh wireframe look at triangle-raster cost.</summary>
    private static float[] BuildBucketGridLineArray(IReadOnlyList<MapBucketGridInfo> grids)
    {
        long totalTris = 0;
        foreach (var g in grids)
            if (g.MeshIndices is { } gi) totalTris += gi.Length / 3;
        var verts = new float[totalTris * 3 * 6];
        int k = 0;
        foreach (var g in grids)
        {
            if (g is not { MeshPositions: { } pos, MeshIndices: { } idx }) continue;
            for (int t = 0; t + 2 < idx.Length; t += 3)
            for (int c = 0; c < 3; c++)
            {
                int v = idx[t + c];
                verts[k++] = pos[v * 3]; verts[k++] = pos[v * 3 + 1]; verts[k++] = pos[v * 3 + 2];
                verts[k++] = c == 0 ? 1f : 0f;
                verts[k++] = c == 1 ? 1f : 0f;
                verts[k++] = c == 2 ? 1f : 0f;
            }
        }
        return verts;
    }
    [ObservableProperty] private object? _selectedPropTreeItem;
    [ObservableProperty] private AnimatedPropViewModel? _selectedPropNode;
    [ObservableProperty] private CubemapProbeViewModel? _selectedProbe;
    [ObservableProperty] private string _selectedPlaceableInfo = "";
    public ObservableCollection<string> PropSkinChoices { get; } = new();

    partial void OnCurrentModelProbesChanged(IReadOnlyList<MapCubemapProbe>? value)
    { MapContent.SetProbes(value ?? Array.Empty<MapCubemapProbe>()); UpdatePlaceableMarkers(); }
    partial void OnCurrentModelPropsChanged(IReadOnlyList<MapAnimatedProp>? value)
    { MapContent.SetProps(value ?? Array.Empty<MapAnimatedProp>()); UpdatePlaceableMarkers(); _ = RefreshPropMeshesAsync(); }

    // ---- M41: render the placed prop meshes (SRU_Baron, dragons, camps…) at their placements ----
    [ObservableProperty] private bool _showPropMeshes;
    [ObservableProperty] private PropRenderSet? _currentPropMeshes;

    partial void OnShowPropMeshesChanged(bool value) => _ = RefreshPropMeshesAsync();

    private async System.Threading.Tasks.Task RefreshPropMeshesAsync()
    {
        if (!ShowPropMeshes || CurrentModelProps is not { Count: > 0 } props)
        {
            _propInstances = System.Array.Empty<PropInstanceData>();   // M79
            PublishAddedMeshPreview();   // keep any added meshes visible even with props off
            return;
        }
        var snapshot = MapContent.AllProps
            .Where(p => p.IsEditorVisible && !p.IsDisabled && !p.IsRemoved)
            .Select(p => p.Prop with { Skin = p.EffectiveSkin, VisibilityFlags = p.EffectiveVisibilityFlags })
            .ToList();
        var (set, resolved, failed) = await System.Threading.Tasks.Task.Run(() => BuildPropRenderSet(snapshot));
        if (!ShowPropMeshes) return;   // toggled off while decoding
        _propInstances = set?.Instances ?? (IReadOnlyList<PropInstanceData>)System.Array.Empty<PropInstanceData>();   // M79
        PublishAddedMeshPreview();      // props + added meshes combined
        _log.Info("Props", $"Rendering {resolved} prop mesh(es); {failed} couldn't be resolved (shown as markers).");
    }

    /// <summary>Decode each unique prop skin once (mesh + per-submesh diffuse) and place an instance per
    /// placement. Runs off the UI thread. Returns the set + resolved/failed counts (logged on return).</summary>
    private (PropRenderSet? set, int resolved, int failed) BuildPropRenderSet(IReadOnlyList<MapAnimatedProp> props)
    {
        var meshBySkin = new Dictionary<string, PropMesh?>(StringComparer.OrdinalIgnoreCase);
        var texByPath = new Dictionary<string, TextureImage?>(StringComparer.OrdinalIgnoreCase);
        var instances = new List<PropInstanceData>();
        int failed = 0;
        foreach (var p in props)
        {
            if (string.IsNullOrEmpty(p.Skin)) { failed++; continue; }
            if (!meshBySkin.TryGetValue(p.Skin, out var mesh))
                meshBySkin[p.Skin] = mesh = TryBuildPropMesh(p.Skin, texByPath);
            if (mesh is not null) instances.Add(new PropInstanceData(mesh, p.Transform));
            else failed++;
        }
        return (instances.Count > 0 ? new PropRenderSet(instances) : null, instances.Count, failed);
    }

    private PropMesh? TryBuildPropMesh(string skin, Dictionary<string, TextureImage?> texCache)
    {
        try
        {
            var binBytes = ReadAssetByPath("data/" + skin.ToLowerInvariant() + ".bin");
            if (binBytes is null) return null;
            var meshRef = SkinMeshExtractor.Extract(binBytes);
            if (meshRef?.SimpleSkin is not { } sknPath) return null;
            var sknBytes = ReadAssetByPath(sknPath);
            if (sknBytes is null) return null;

            var mesh = SkinnedMeshDecoder.Decode(sknBytes);
            var mat = ChampionMaterialResolver.Resolve(binBytes, ResolveBinName);
            TextureImage? Tex(string? path)
            {
                if (string.IsNullOrEmpty(path)) return null;
                if (texCache.TryGetValue(path, out var img)) return img;
                return texCache[path] = LoadTextureByPath(path);
            }
            var subs = mesh.SubMeshes
                .Select(s => new PropSubmesh(s.StartIndex, s.IndexCount, Tex(mat.For(s.Material) ?? meshRef.DefaultTexture)))
                .ToList();

            // M54: idle-animation payload — the character's skeleton + a best-match idle .anm, so the
            // viewport can play the ambient idles (Baron breathing, camps shuffling...).
            SkeletonAsset? skeleton = null;
            AnimationClip? idle = null;
            if (mesh.CanSkin && meshRef.Skeleton is { } sklPath)
            {
                try
                {
                    var sklBytes = ReadAssetByPath(sklPath);
                    if (sklBytes is not null) skeleton = SkeletonDecoder.Decode(sklBytes);
                    if (skeleton is not null) idle = TryFindIdleClip(skin);
                }
                catch { skeleton = null; idle = null; }
            }
            return new PropMesh(skin, mesh.Positions, mesh.Normals, mesh.Uvs, mesh.Indices, subs)
            { SknMesh = mesh, Skeleton = skeleton, IdleClip = idle };
        }
        catch { return null; }
    }

    /// <summary>M54: pick the best idle .anm for a prop skin ("characters/<name>/..."): prefer idle1/
    /// idle_base, then any idle. Null when the character ships no idle animation.</summary>
    private AnimationClip? TryFindIdleClip(string skin)
    {
        const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
        var parts = skin.ToLowerInvariant().Split('/');
        int ci = Array.IndexOf(parts, "characters");
        if (ci < 0 || ci + 1 >= parts.Length) return null;
        string marker = $"characters/{parts[ci + 1]}/";
        WadAssetEntry? best = null; int bestScore = 0;
        foreach (var e in AssetEntries)
        {
            if (!e.IsResolved || !e.Path.EndsWith(".anm", OIC) || !e.Path.Contains(marker, OIC)) continue;
            var n = Path.GetFileNameWithoutExtension(e.Path);
            int score = n.Contains("idle1", OIC) || n.Contains("idle_base", OIC) || n.Contains("idle01", OIC) ? 3
                : n.Contains("idle", OIC) ? 2 : 0;
            if (score > bestScore) { bestScore = score; best = e; }
        }
        if (best is null) return null;
        try { return AnimationDecoder.Decode(ReadAsset(best.PathHash), best.DisplayName); }
        catch { return null; }
    }
    partial void OnShowPlaceablesChanged(bool value) => UpdatePlaceableMarkers();

    // ---- M123: independent icon toggles - audio + mob icons no longer all-or-nothing ----
    [ObservableProperty] private bool _showSoundIcons = true;
    [ObservableProperty] private bool _showPropIcons = true;
    partial void OnShowSoundIconsChanged(bool value) => UpdatePlaceableMarkers();
    partial void OnShowPropIconsChanged(bool value) => UpdatePlaceableMarkers();

    private void UpdatePlaceableMarkers()
    {
        PropMarkers = (ShowPlaceables && ShowPropIcons && MapContent.HasProps) ? MapContent.AllProps
            .Where(p => p.IsEditorVisible && !p.IsDisabled && !p.IsRemoved).Select(p => p.Position).ToList() : null;
        ProbeMarkers = (ShowPlaceables && MapContent.HasProbes) ? MapContent.Probes
            .Where(p => p.IsEditorVisible && !p.IsDisabled && !p.IsRemoved).Select(p => p.Position).ToList() : null;
        SoundMarkers = (ShowPlaceables && ShowSoundIcons && MapContent.HasSounds)
            ? MapContent.Sounds.Where(s => s.IsEditorVisible && !s.IsDisabled && !s.IsRemoved
                && IsSoundVisible(s.Sound, s.EffectiveVisibilityFlags)).Select(s => s.Position).ToList() : null;   // M55
    }

    /// <summary>One refresh path for the eye, runtime Disable, and pending Delete controls shared by all
    /// Map Content leaves. The eye never dirties a file. Disable writes a zero visibility mask and can be
    /// restored; Delete is kept separate and removes the object only when map edits are saved.</summary>
    private void OnMapContentItemStateChanged(MapOutlinerItemViewModel item)
    {
        switch (item)
        {
            case ParticlePlacementViewModel p:
                if (p.IsDisabled != (p.EffectiveVisibilityFlags == 0))
                    p.EditedVisibilityFlags = p.IsDisabled ? 0 : (p.Placement.VisibilityFlags == 0 ? 255 : null);
                break;
            case AnimatedPropViewModel p:
                if (p.IsDisabled != (p.EffectiveVisibilityFlags == 0))
                    p.EditedVisibilityFlags = p.IsDisabled ? 0 : (p.Prop.VisibilityFlags == 0 ? 255 : null);
                break;
            case CubemapProbeViewModel p:
                if (p.IsDisabled != (p.EffectiveVisibilityFlags == 0))
                    p.EditedVisibilityFlags = p.IsDisabled ? 0 : (p.Probe.VisibilityFlags == 0 ? 255 : null);
                break;
            case MapSoundViewModel s:
                if (s.Sound.FromParticleSystem)
                {
                    var owner = MapContent.AllParticles.FirstOrDefault(p => p.Placement.Name == s.Sound.Name
                        && p.Placement.Transform == s.Sound.Transform);
                    if (owner is not null)
                    {
                        owner.EditedVisibilityFlags = s.EditedVisibilityFlags;
                        owner.IsDisabled = s.IsDisabled;
                        owner.IsRemoved = s.IsRemoved;
                    }
                    break;
                }
                if (s.IsDisabled != (s.EffectiveVisibilityFlags == 0))
                    s.EditedVisibilityFlags = s.IsDisabled ? 0 : (s.Sound.VisibilityFlags == 0 ? 255 : null);
                break;
            case MapPieceViewModel piece when _currentMap is { } map
                && map.Meshes.FirstOrDefault(m => m.Index == piece.MeshIndex) is { } mesh:
                if (piece.IsDisabled != (mesh.EffectiveVisibility == 0))
                    mesh.VisibilityEdit = piece.IsDisabled ? 0 : (mesh.VisibilityFlags == 0 ? 255 : null);
                HasMapMoves = MapGeoWriter.HasMoves(map.Meshes) || MapGeoLayerWriter.HasEdits(map.Meshes)
                    || MapContent.AllMapPieces.Any(x => x.IsRemoved);
                ApplyMapVisibility();
                break;
            case AddedMapMeshViewModel added:
                if (added.IsDisabled)
                {
                    if (added.VisibilityMask != 0) added.EnabledVisibilityMask = added.VisibilityMask;
                    added.VisibilityMask = 0;
                }
                else added.VisibilityMask = added.EnabledVisibilityMask;
                PublishAddedMeshPreview();
                break;
        }

        HasParticleMoves = MapContent.AllParticles.Any(v => v.HasEdits)
            || MapContent.Sounds.Any(v => v.HasEdits)
            || MapContent.AllProps.Any(v => v.HasEdits)
            || MapContent.Probes.Any(v => v.HasEdits);
        UpdateParticleMarkers();
        UpdatePlaceableMarkers();
        RebuildParticlePlayback();
        if (AmbienceEnabled) UpdateAmbience(_lastCamPosForAudio, force: true);
        if (item is AnimatedPropViewModel) _ = RefreshPropMeshesAsync();
        RefreshPlacementLayerEditor();
    }

    partial void OnSelectedPropTreeItemChanged(object? value)
    { if (value is AnimatedPropViewModel p) SelectedPropNode = p; }
    partial void OnSelectedPropNodeChanged(AnimatedPropViewModel? value)
    {
        PropSkinChoices.Clear();
        if (value is not { } p) return;
        var marker = $"characters/{p.Prop.CharacterName}/skins/";
        foreach (var path in AssetEntries.Where(e => e.IsResolved
                     && e.Path.Contains(marker, StringComparison.OrdinalIgnoreCase)
                     && e.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                 .Select(e => e.Path.StartsWith("data/", StringComparison.OrdinalIgnoreCase) ? e.Path[5..] : e.Path)
                 .Select(path => path[..^4])
                 .Append(p.Prop.Skin)
                 .Distinct(StringComparer.OrdinalIgnoreCase)
                 .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            PropSkinChoices.Add(path);
        SelectedProbe = null;
        _selection.Clear();                       // M50b: exclusive selection
        if (SelectedParticleTreeItem is not null) SelectedParticleTreeItem = null;
        if (SelectedParticleNode is not null) SelectedParticleNode = null;   // M76: viewport picks bypass the tree item
        if (SelectedSound is not null) SelectedSound = null;   // M56
        SelectedParticleMarker = p.Position;   // M55b: highlight only — camera stays (use Focus)
        SelectedPlaceableInfo = $"{p.Name}\n{p.Info}\n({p.Position.X:0}, {p.Position.Y:0}, {p.Position.Z:0})";
    }
    partial void OnSelectedProbeChanged(CubemapProbeViewModel? value)
    {
        if (value is not { } p) return;
        SelectedPropNode = null;
        _selection.Clear();                       // M50b: exclusive selection
        if (SelectedParticleTreeItem is not null) SelectedParticleTreeItem = null;
        if (SelectedParticleNode is not null) SelectedParticleNode = null;   // M76: viewport picks bypass the tree item
        if (SelectedSound is not null) SelectedSound = null;   // M56
        SelectedParticleMarker = p.Position;   // M55b: highlight only — camera stays (use Focus)
        SelectedPlaceableInfo = $"{p.Name}\ncubemap: {p.Info}\n({p.Position.X:0}, {p.Position.Y:0}, {p.Position.Z:0})";
    }

    // ---- Particle playback (M36) — simulate & render the selected placed system live in the viewport ----
    private static readonly IReadOnlyDictionary<uint, VfxSystemDefinition> EmptyVfx = new Dictionary<uint, VfxSystemDefinition>();
    private IReadOnlyDictionary<uint, VfxSystemDefinition> _vfxSystems = EmptyVfx;
    private readonly Dictionary<uint, IReadOnlyList<TextureImage?>> _vfxTextureCache = new();  // system hash -> sprites
    private readonly Dictionary<uint, IReadOnlyList<TextureImage?>> _vfxTextureMultCache = new();
    private readonly Dictionary<uint, IReadOnlyList<TextureImage?>> _vfxDistortionTextureCache = new();
    private readonly Dictionary<uint, IReadOnlyList<TextureImage?>> _vfxColorTextureCache = new();  // M68: particleColorTexture gradients
    [ObservableProperty] private bool _playParticlePreview;
    [ObservableProperty] private bool _playAllParticles;
    [ObservableProperty] private VfxPlayback? _currentParticlePlayback;

    partial void OnPlayParticlePreviewChanged(bool value) { if (value) PlayAllParticles = false; RebuildParticlePlayback(); }
    partial void OnPlayAllParticlesChanged(bool value) { if (value) PlayParticlePreview = false; RebuildParticlePlayback(); }

    /// <summary>Resolve (and cache) one sprite per emitter for a system; nulls → viewport soft-dot fallback.</summary>
    private IReadOnlyList<TextureImage?> ResolveSystemTextures(VfxSystemDefinition sys)
    {
        if (_vfxTextureCache.TryGetValue(sys.PathHash, out var cached)) return cached;
        var texs = new List<TextureImage?>(sys.Emitters.Count);
        foreach (var e in sys.Emitters)
            texs.Add(e.TexturePath is { } p ? LoadTextureByPath(p) : null);
        _vfxTextureCache[sys.PathHash] = texs;
        return texs;
    }

    private IReadOnlyList<TextureImage?> ResolveSystemMultTextures(VfxSystemDefinition sys)
    {
        if (_vfxTextureMultCache.TryGetValue(sys.PathHash, out var cached)) return cached;
        var texs = new List<TextureImage?>(sys.Emitters.Count);
        foreach (var e in sys.Emitters)
            texs.Add(e.TextureMultPath is { } p ? LoadTextureByPath(p) : null);
        _vfxTextureMultCache[sys.PathHash] = texs;
        return texs;
    }

    /// <summary>M174 (2.1): each emitter's alpha-erosion dissolve map, aligned to Emitters.</summary>
    private readonly Dictionary<uint, IReadOnlyList<TextureImage?>> _vfxErosionTextureCache = new();

    private IReadOnlyList<TextureImage?> ResolveSystemErosionTextures(VfxSystemDefinition sys)
    {
        if (_vfxErosionTextureCache.TryGetValue(sys.PathHash, out var cached)) return cached;
        var texs = new List<TextureImage?>(sys.Emitters.Count);
        foreach (var e in sys.Emitters)
            texs.Add(e.AlphaErosion?.MapPath is { } p ? LoadTextureByPath(p) : null);
        _vfxErosionTextureCache[sys.PathHash] = texs;
        return texs;
    }

    /// <summary>M175 (2.6): each emitter's palette gradient strip, aligned to Emitters.</summary>
    private readonly Dictionary<uint, IReadOnlyList<TextureImage?>> _vfxPaletteTextureCache = new();

    private IReadOnlyList<TextureImage?> ResolveSystemPaletteTextures(VfxSystemDefinition sys)
    {
        if (_vfxPaletteTextureCache.TryGetValue(sys.PathHash, out var cached)) return cached;
        var texs = new List<TextureImage?>(sys.Emitters.Count);
        foreach (var e in sys.Emitters)
            texs.Add(e.Palette?.TexturePath is { } p ? LoadTextureByPath(p) : null);
        _vfxPaletteTextureCache[sys.PathHash] = texs;
        return texs;
    }

    /// <summary>M181 (2.12): each emitter's reflection cubemap, aligned to Emitters. These are real DDS
    /// cubemaps (e.g. ASSETS/Shared/Particles/MissFortune_Bullet_CubeMap.dds) and go through the same
    /// decoder the M122 skybox uses, so face ordering is shared rather than reinvented.</summary>
    private readonly Dictionary<uint, IReadOnlyList<CubemapImage?>> _vfxReflectionCubeCache = new();

    private IReadOnlyList<CubemapImage?> ResolveSystemReflectionCubemaps(VfxSystemDefinition sys)
    {
        if (_vfxReflectionCubeCache.TryGetValue(sys.PathHash, out var cached)) return cached;
        var cubes = new List<CubemapImage?>(sys.Emitters.Count);
        foreach (var e in sys.Emitters)
        {
            CubemapImage? cm = null;
            if (e.Reflection?.MapPath is { Length: > 0 } path)
            {
                try
                {
                    var bytes = ReadAssetByPath(path);
                    if (bytes is not null) cm = CubemapDecoder.TryDecodeDds(bytes);
                }
                catch { cm = null; }   // subchunked/corrupt chunks throw inside the mount read
            }
            cubes.Add(cm);
        }
        _vfxReflectionCubeCache[sys.PathHash] = cubes;
        return cubes;
    }

    private IReadOnlyList<TextureImage?> ResolveSystemDistortionTextures(VfxSystemDefinition sys)
    {
        if (_vfxDistortionTextureCache.TryGetValue(sys.PathHash, out var cached)) return cached;
        var texs = new List<TextureImage?>(sys.Emitters.Count);
        foreach (var e in sys.Emitters)
            texs.Add(e.Distortion?.NormalMapTexturePath is { } p ? LoadTextureByPath(p) : null);
        _vfxDistortionTextureCache[sys.PathHash] = texs;
        return texs;
    }

    /// <summary>M68: resolve each emitter's particleColorTexture (the colour-over-life gradient the simulator
    /// samples on the CPU). Null when the emitter has none — it then keeps its birthColor/color curve.</summary>
    private IReadOnlyList<TextureImage?> ResolveSystemColorTextures(VfxSystemDefinition sys)
    {
        if (_vfxColorTextureCache.TryGetValue(sys.PathHash, out var cached)) return cached;
        var texs = new List<TextureImage?>(sys.Emitters.Count);
        foreach (var e in sys.Emitters)
            texs.Add(e.ParticleColorTexturePath is { } p ? LoadTextureByPath(p) : null);
        _vfxColorTextureCache[sys.PathHash] = texs;
        return texs;
    }

    /// <summary>M47: resolve each emitter's .scb/.sco mesh primitive (null when not a mesh emitter or
    /// the mesh doesn't resolve — those billboard as before). Cached per system.</summary>
    private readonly Dictionary<uint, IReadOnlyList<Formats.Meshes.StaticMeshData?>?> _vfxMeshCache = new();
    private IReadOnlyList<Formats.Meshes.StaticMeshData?>? ResolveSystemMeshes(VfxSystemDefinition sys)
    {
        if (_vfxMeshCache.TryGetValue(sys.PathHash, out var cached)) return cached;
        List<Formats.Meshes.StaticMeshData?>? meshes = null;
        for (int i = 0; i < sys.Emitters.Count; i++)
        {
            var e = sys.Emitters[i];
            if (!e.IsMeshPrimitive || string.IsNullOrEmpty(e.MeshPath)) continue;
            // Never let one broken mesh (subchunked chunk, missing file) kill the whole playback build —
            // that silently froze "Play All" at the previously-playing single system.
            byte[]? bytes;
            try { bytes = ReadAssetByPath(e.MeshPath); }
            catch { bytes = null; }
            Formats.Meshes.StaticMeshData? mesh = null;
            if (bytes is not null)
            {
                // M47b: skinned mesh primitives (butterflies/dragonflies, .skn) render in bind pose via
                // the same mesh-particle path (no per-particle wing animation yet); .scb/.sco are static.
                if (e.MeshPath.EndsWith(".skn", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var m = SkinnedMeshDecoder.Decode(bytes);
                        // M48: wing-flap — load the primitive's skeleton + idle animation so the viewport
                        // can CPU-skin the mesh per frame (falls back to bind pose when either is missing).
                        Formats.Meshes.VfxMeshAnimation? anim = null;
                        if (m.CanSkin && e.MeshSkeletonPath is { } sklP && e.MeshAnimationPath is { } anmP)
                        {
                            try
                            {
                                var sklB = ReadAssetByPath(sklP);
                                var anmB = ReadAssetByPath(anmP);
                                if (sklB is not null && anmB is not null)
                                    anim = new Formats.Meshes.VfxMeshAnimation(m,
                                        SkeletonDecoder.Decode(sklB),
                                        AnimationDecoder.Decode(anmB, Path.GetFileName(anmP)));
                            }
                            catch { /* bind pose fallback */ }
                        }
                        mesh = new Formats.Meshes.StaticMeshData(m.Positions, m.Uvs, m.Indices, Path.GetFileName(e.MeshPath))
                        { Animation = anim };
                    }
                    catch { /* keep billboard fallback */ }
                }
                else mesh = Formats.Meshes.StaticObjectDecoder.Decode(bytes, e.MeshPath);
            }
            if (mesh is null) continue;
            meshes ??= Enumerable.Repeat<Formats.Meshes.StaticMeshData?>(null, sys.Emitters.Count).ToList();
            meshes[i] = mesh;
        }
        return _vfxMeshCache[sys.PathHash] = meshes;
    }

    // ---- Champion-skin VFX (M37) — a loaded skin's effect library, played at the model origin ----
    public ObservableCollection<VfxSystemItemViewModel> ChampionVfxSystems { get; } = new();
    [ObservableProperty] private bool _hasChampionVfx;
    [ObservableProperty] private VfxSystemItemViewModel? _selectedChampionVfx;

    /// <summary>Populate the champion VFX list from a skin's parsed systems (visual systems only, sorted).</summary>
    private void SetChampionVfx(IReadOnlyDictionary<uint, VfxSystemDefinition> systems)
    {
        _vfxSystems = systems;
        _vfxTextureCache.Clear(); _vfxTextureMultCache.Clear(); _vfxDistortionTextureCache.Clear(); _vfxColorTextureCache.Clear(); _vfxMeshCache.Clear(); _vfxErosionTextureCache.Clear();
        _vfxPaletteTextureCache.Clear(); _vfxReflectionCubeCache.Clear();
        ChampionVfxSystems.Clear();
        foreach (var s in systems.Values
                     .Where(s => s.Emitters.Any(e => e.IsVisual))
                     .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            ChampionVfxSystems.Add(new VfxSystemItemViewModel { Hash = s.PathHash, Name = s.Name, EmitterCount = s.Emitters.Count(e => e.IsVisual) });
        HasChampionVfx = ChampionVfxSystems.Count > 0;
        SelectedChampionVfx = null;
    }

    partial void OnSelectedChampionVfxChanged(VfxSystemItemViewModel? value)
    {
        if (value is null || !_vfxSystems.TryGetValue(value.Hash, out var sys))
        {
            CurrentParticlePlayback = null;
            return;
        }
        // champion VFX are authored around the character root (origin); play one system there.
        CurrentParticlePlayback = new VfxPlayback(new[] { new VfxPlaybackItem(sys, System.Numerics.Vector3.Zero,
            ResolveSystemTextures(sys), ResolveSystemMeshes(sys), ResolveSystemMultTextures(sys), ResolveSystemDistortionTextures(sys),
            ResolveSystemColorTextures(sys), ResolveSystemErosionTextures(sys),
            ResolveSystemPaletteTextures(sys),
            emitterReflectionCubemaps: ResolveSystemReflectionCubemaps(sys)) });
        _log.Info("VFX", $"Playing '{sys.Name}' — {sys.Emitters.Count} emitter(s), {ResolveSystemTextures(sys).Count(t => t is not null)} sprite(s) resolved.");
    }

    [RelayCommand]
    private void StopChampionVfx() => SelectedChampionVfx = null;

    /// <summary>Rebuild the live playback request (M36): all visible placements, or just the selected one.</summary>
    private void RebuildParticlePlayback()
    {
        if (PlayAllParticles)
        {
            var items = new List<VfxPlaybackItem>();
            foreach (var v in MapContent.AllParticles)
            {
                if (!v.IsEditorVisible || v.IsDisabled || v.IsRemoved) continue;
                if (!IsParticleVisible(v.Placement, v.EffectiveVisibilityFlags)) continue;
                if (!_vfxSystems.TryGetValue(v.EffectiveSystemHash, out var s) || !s.Emitters.Any(e => e.IsVisual)) continue;
                items.Add(new VfxPlaybackItem(s, v.CurrentTransform, ResolveSystemTextures(s), ResolveSystemMeshes(s),
                    ResolveSystemMultTextures(s), ResolveSystemDistortionTextures(s), ResolveSystemColorTextures(s),
                    ResolveSystemErosionTextures(s), ResolveSystemPaletteTextures(s),
                    ColorModulate: v.EffectiveTint));   // M203 tint; M204 shows a pending re-tint live
            }
            CurrentParticlePlayback = items.Count > 0 ? new VfxPlayback(items, CullByCamera: true) : null;
            _log.Info("Particles", $"Playing all — {items.Count} layer-visible placement(s); viewport culling keeps only nearby on-screen systems active.");
            return;
        }

        if (!PlayParticlePreview || SelectedParticleNode is not { IsEditorVisible: true, IsDisabled: false, IsRemoved: false } node
            || !_vfxSystems.TryGetValue(node.EffectiveSystemHash, out var sys) || sys.Emitters.Count == 0)
        {
            CurrentParticlePlayback = null;
            return;
        }
        var texs = ResolveSystemTextures(sys);
        CurrentParticlePlayback = new VfxPlayback(new[] { new VfxPlaybackItem(sys, node.CurrentTransform, texs,
            ResolveSystemMeshes(sys), ResolveSystemMultTextures(sys), ResolveSystemDistortionTextures(sys),
            ResolveSystemColorTextures(sys), ResolveSystemErosionTextures(sys),
            ResolveSystemPaletteTextures(sys),
            EmitterReflectionCubemaps: ResolveSystemReflectionCubemaps(sys)) });
        _log.Info("Particles", $"Playing '{sys.Name}' — {sys.Emitters.Count} emitter(s), {texs.Count(t => t is not null)} sprite(s) resolved.");
    }

    // ---- Particle move (M35 adjustment) — reposition a placed particle, live + persisted to the mod ----
    [ObservableProperty] private string _particleMoveX = "0";
    [ObservableProperty] private string _particleMoveY = "0";
    [ObservableProperty] private string _particleMoveZ = "0";
    /// <summary>Dirty flag: at least one particle has been moved and can be saved to the mod.</summary>
    [ObservableProperty] private bool _hasParticleMoves;

    private void RefreshParticleMoveFields(ParticlePlacementViewModel? node)
    {
        var p = node?.CurrentPosition ?? System.Numerics.Vector3.Zero;
        ParticleMoveX = p.X.ToString("0.###", CultureInfo.InvariantCulture);
        ParticleMoveY = p.Y.ToString("0.###", CultureInfo.InvariantCulture);
        ParticleMoveZ = p.Z.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>Apply the numeric X/Y/Z as the selected particle's new absolute world position (live preview).</summary>
    [RelayCommand]
    private void ApplyParticleMove()
    {
        if (SelectedParticleNode is not { } node) return;
        if (!TryParseVector3(ParticleMoveX, ParticleMoveY, ParticleMoveZ, out var target))
        { _log.Warn("Particles", "Enter valid X/Y/Z numbers."); return; }
        node.Offset = target - node.Placement.Position;
        SelectedParticleMarker = node.CurrentPosition;
        UpdateParticleMarkers();
        HasParticleMoves = MapContent.AllParticles.Any(v => v.HasEdits);
        RebuildParticlePlayback();   // M36: follow the moved particle if it's playing
        _log.Info("Particles", $"Moved '{node.Name}' to ({target.X:0.#}, {target.Y:0.#}, {target.Z:0.#}).");
    }

    /// <summary>M206: duplicate the selected placement. The copy is created in the scene immediately -
    /// offset so it is not hidden inside its source - and written to the .bin on Save to Mod, where the
    /// writer deep-clones the original so the copy keeps every field ReyEngine does not model.</summary>
    [RelayCommand]
    private void DuplicateParticlePlacement()
    {
        if (SelectedParticleNode is not { } node) return;
        if (!node.Placement.Id.IsValid) { _log.Warn("Particles", "This placement has no identity in the bin and cannot be duplicated."); return; }
        if (node.IsNew) { _log.Warn("Particles", "Save the existing copy before duplicating it again."); return; }
        if (_currentMapEntry is null || !TryResolveMaterialsBin(_currentMapEntry.Path, out var binEntry)) return;

        uint key;
        try { key = MapPlaceableWriter.NewItemKey(SafeBinTree.Parse(GetAssetBytes(binEntry)), node.Placement.Id); }
        catch (Exception ex) { _log.Error("Particles", $"Could not mint a key for the copy: {ex.Message}"); return; }

        // Nudged so the copy is visible rather than z-fighting inside the original.
        var copy = new ParticlePlacementViewModel
        {
            Placement = node.Placement with { Id = new MapPlacementId(node.Placement.Id.ContainerHash, key) },
            CloneSource = node.Placement.Id,
            Offset = new System.Numerics.Vector3(100f, 0f, 0f),
            StateChanged = OnMapContentItemStateChanged,
        };
        MapContent.AddParticlePlacement(copy);
        SelectedParticleNode = copy;
        HasParticleMoves = true;
        UpdateParticleMarkers();
        RebuildParticlePlayback();
        _log.Info("Particles", $"Duplicated '{node.Placement.Name}'. Save to Mod writes it into the .bin.");
    }

    /// <summary>M204: resets EVERY pending edit on the placement, not only the transform - the button sits
    /// beside rename/tint/delete now, so "Reset" clearing only the move would be a trap.</summary>
    [RelayCommand]
    private void ResetParticleEdits()
    {
        if (SelectedParticleNode is not { } node) return;
        node.ResetEdits();
        RefreshParticleMoveFields(node);
        SelectedParticleMarker = node.CurrentPosition;
        GizmoPivot = node.CurrentPosition;
        UpdateParticleMarkers();
        HasParticleMoves = MapContent.AllParticles.Any(v => v.HasEdits);
        RebuildParticlePlayback();
    }

    // ---- M75: placement gizmo — the viewport gizmo drives particles (move/rotate/scale) and sounds (move).
    // Mirrors the mesh drag API; per-frame updates are silent, EndPlacementDrag logs + refreshes playback. ----

    [ObservableProperty] private AddedMapMeshViewModel? _selectedAddedMesh;   // M79

    /// <summary>True when the gizmo should operate on a placement (no mesh selected, placement is).</summary>
    public bool HasPlacementGizmoTarget => SelectedParticleNode is not null || SelectedSound is not null
                                           || SelectedAddedMesh is not null || SelectedLight is not null;   // M154

    /// <summary>Drag-start state for the active placement (sounds report identity rotation/scale).
    /// M154: a light has no offset model — it stores an absolute position, so it reports that as the
    /// "offset" and DragSelectedPlacementTo writes start+delta straight back as the new position.</summary>
    public (System.Numerics.Vector3 Offset, System.Numerics.Vector3 Rotation, System.Numerics.Vector3 Scale) PlacementDragStart =>
        SelectedParticleNode is { } p ? (p.Offset, p.RotationDegrees, p.Scale)
        : SelectedAddedMesh is { } a ? (a.Offset, a.RotationDegrees, a.Scale)
        : SelectedLight is { } l ? (l.Position, System.Numerics.Vector3.Zero, System.Numerics.Vector3.One)
        : (SelectedSound?.Offset ?? System.Numerics.Vector3.Zero, System.Numerics.Vector3.Zero, System.Numerics.Vector3.One);

    // M76: undo support — the whole drag is ONE step, captured at press, pushed at release.
    private object? _placementDragTarget;
    private PlacementTransformCommand.State _placementDragBefore;

    /// <summary>Called at gizmo-press on a placement: capture the before-state for the undo step.</summary>
    public void BeginPlacementDrag()
    {
        _placementDragTarget = (object?)SelectedParticleNode ?? (object?)SelectedSound
                               ?? (object?)SelectedAddedMesh ?? SelectedLight;   // M154
        if (_placementDragTarget is { } t) _placementDragBefore = PlacementTransformCommand.State.Capture(t);
    }

    /// <summary>M76: re-sync everything a placement transform touches (used by undo/redo too).</summary>
    private void RefreshPlacementVisuals(object target)
    {
        switch (target)
        {
            case ParticlePlacementViewModel p:
                SelectedParticleMarker = p.CurrentPosition;
                if (ReferenceEquals(p, SelectedParticleNode)) { GizmoPivot = p.CurrentPosition; RefreshParticleMoveFields(p); }
                UpdateParticleMarkers();
                RebuildParticlePlayback();
                break;
            case MapSoundViewModel s:
                if (ReferenceEquals(s, SelectedSound)) { SelectedParticleMarker = s.Position; GizmoPivot = s.Position; }
                UpdatePlaceableMarkers();
                break;
            case AddedMapMeshViewModel a:   // M79
                if (ReferenceEquals(a, SelectedAddedMesh)) GizmoPivot = a.PivotWorld;
                PublishAddedMeshPreview();
                break;
            case PointLightViewModel l:   // M154
                if (ReferenceEquals(l, SelectedLight)) GizmoPivot = l.Position;
                RepublishLights();
                break;
        }
        HasParticleMoves = MapContent.AllParticles.Any(v => v.HasEdits) || MapContent.Sounds.Any(v => v.IsMoved);
    }

    public void DragSelectedPlacementTo(System.Numerics.Vector3 absoluteOffset)
    {
        // M152: a selected point light is dragged like any other placement.
        if (SelectedLight is { } light)
        {
            light.MoveTo(absoluteOffset);
            GizmoPivot = light.Position;
            return;
        }
        if (SelectedParticleNode is { } p)
        {
            p.Offset = absoluteOffset;
            SelectedParticleMarker = p.CurrentPosition;
            GizmoPivot = p.CurrentPosition;
            RefreshParticleMoveFields(p);
            UpdateParticleMarkers();
        }
        else if (SelectedAddedMesh is { } a)   // M79
        {
            a.Offset = absoluteOffset;
            GizmoPivot = a.PivotWorld;
            PublishAddedMeshPreview();
        }
        else if (SelectedSound is { } s)
        {
            s.Offset = absoluteOffset;
            SelectedParticleMarker = s.Position;
            GizmoPivot = s.Position;
            UpdatePlaceableMarkers();
        }
    }

    /// <summary>Extra local rotation for the selected particle/added-mesh (sounds are point emitters — no-op).</summary>
    public void RotateSelectedPlacementTo(System.Numerics.Vector3 rotationDegrees)
    {
        if (SelectedParticleNode is { } p) p.RotationDegrees = rotationDegrees;
        else if (SelectedAddedMesh is { } a) { a.RotationDegrees = rotationDegrees; GizmoPivot = a.PivotWorld; PublishAddedMeshPreview(); }
    }

    /// <summary>Extra local scale for the selected particle/added-mesh (sounds are point emitters — no-op).</summary>
    public void ScaleSelectedPlacementTo(System.Numerics.Vector3 scale)
    {
        if (SelectedParticleNode is { } p) p.Scale = scale;
        else if (SelectedAddedMesh is { } a) { a.Scale = scale; GizmoPivot = a.PivotWorld; PublishAddedMeshPreview(); }
    }

    public void EndPlacementDrag()
    {
        HasParticleMoves = MapContent.AllParticles.Any(v => v.HasEdits) || MapContent.Sounds.Any(s => s.IsMoved);
        // M76: push the whole drag as ONE undo step (no-op when nothing actually changed).
        if (_placementDragTarget is { } target)
        {
            var after = PlacementTransformCommand.State.Capture(target);
            if (after != _placementDragBefore)
                UndoService.PushApplied(new PlacementTransformCommand(target, _placementDragBefore, after, _currentMap, RefreshPlacementVisuals));
            _placementDragTarget = null;
        }
        if (SelectedParticleNode is { } p)
        {
            RebuildParticlePlayback();   // live-preview the placement's new transform once, not per frame
            _log.Info("Particles", $"'{p.Name}' → pos ({p.CurrentPosition.X:0.#}, {p.CurrentPosition.Y:0.#}, {p.CurrentPosition.Z:0.#})" +
                (p.RotationDegrees != System.Numerics.Vector3.Zero ? $" · rot ({p.RotationDegrees.X:0.#}, {p.RotationDegrees.Y:0.#}, {p.RotationDegrees.Z:0.#})°" : "") +
                (p.Scale != System.Numerics.Vector3.One ? $" · scale ({p.Scale.X:0.##}, {p.Scale.Y:0.##}, {p.Scale.Z:0.##})" : ""));
        }
        else if (SelectedSound is { } s)
            _log.Info("Sounds", $"'{s.Name}' → ({s.Position.X:0.#}, {s.Position.Y:0.#}, {s.Position.Z:0.#}). Save Placement Edits writes it to the mod.");
        else if (SelectedAddedMesh is { } a)   // M79
            _log.Info("AddMesh", $"'{a.Name}' → ({a.Offset.X:0.#}, {a.Offset.Y:0.#}, {a.Offset.Z:0.#}). Save Map Edits appends it to the mapgeo.");
    }

    // ---- M79: add imported meshes to the map ----------------------------------------------------

    public bool HasAddedMeshes => MapContent.AddedMeshes.Count > 0;

    /// <summary>Import a mesh (.obj/.scb/.sco) and queue it to be appended to the loaded map. Placed at the
    /// current gizmo/camera focus, previewed as an overlay, and movable with the transform gizmo.</summary>
    [RelayCommand]
    private async Task AddMeshToMap()
    {
        if (_currentMap is null) { _log.Warn("AddMesh", "Open a map (.mapgeo) first."); return; }
        var file = await Dialogs.OpenFileAsync("Import mesh (.fbx / .glb / .gltf / .obj / .scb / .sco)",
            new Avalonia.Platform.Storage.FilePickerFileType("Mesh")
            { Patterns = new[] { "*.fbx", "*.glb", "*.gltf", "*.obj", "*.scb", "*.sco" } },
            DialogService.All);
        if (file is null) return;

        // M123: the dedicated import + setup window replaces the old direct-add flow.
        // M123b: new materials build from the shader catalogue, so it must be loaded.
        if (MaterialEditor.Catalog is null && MaterialEditor.SelectedShaderEnvironment is { } env)
            await LoadShaderCatalogAsync(env);
        if (MaterialEditor.Catalog is not { } cat)
        { _log.Warn("AddMesh", "No shader catalogue — pick a game environment in the Materials tab first."); return; }

        var vm = new AddMeshWindowViewModel
        {
            ExistingMaterials = MapMaterialNames,
            ShaderChoices = cat.Shaders
                .Where(sh => sh.Category is "StaticMesh" or "Environment")
                .Select(sh => sh.Name).ToList(),
        };
        vm.SetVisibilityLayers(_mapVisibility.Primary?.Layers ?? Array.Empty<VisibilityLayer>());
        vm.PickFile = async title => await Dialogs.OpenFileAsync(title,
            new Avalonia.Platform.Storage.FilePickerFileType("Mesh")
            { Patterns = new[] { "*.fbx", "*.glb", "*.gltf", "*.obj", "*.scb", "*.sco" } },
            DialogService.All);
        vm.Confirmed = plan => _ = ExecuteAddMeshPlanAsync(plan);
        vm.LoadFile(file);
        ShowAddMeshWindow?.Invoke(vm);
    }

    /// <summary>Wired by MainWindow — owns the Add Mesh window instance.</summary>
    public Action<AddMeshWindowViewModel>? ShowAddMeshWindow;

    [RelayCommand]
    private void OpenWorkshop()
    {
        string? final = GameReferenceLibrary.FindFinalDirectory(Project.GameDirectory);
        if (final is null)
        {
            _log.Error("Workshop", "Set a valid League game folder first (Project > Set Game Folder). The Workshop indexes DATA/FINAL.");
            return;
        }

        _workshopCatalog ??= new WorkshopCatalogService(_resolver.Database, ResolveBinName);
        var vm = new WorkshopViewModel(_workshopCatalog, final)
        {
            AddMaterial = ImportWorkshopMaterialAsync,
            AddParticle = ImportWorkshopParticleAsync,
        };
        ShowWorkshopWindow?.Invoke(vm);
        _ = vm.InitializeAsync();
    }

    public Action<WorkshopViewModel>? ShowWorkshopWindow;

    private async Task<string> ImportWorkshopMaterialAsync(WorkshopMaterialTemplate template, string newName)
    {
        if (_currentMapEntry is not { } mapEntry || _currentMap is null)
            throw new InvalidOperationException("Open the destination map before adding a material.");
        if (!TryResolveMaterialsBin(mapEntry.Path, out var binEntry))
            throw new InvalidOperationException("The open map has no companion materials .bin.");
        if (!await EnsureProjectSavedAsync())
            throw new InvalidOperationException("Save the project before adding Workshop content.");

        byte[] target = GetAssetBytes(binEntry);
        byte[] source = _workshopCatalog?.ReadBin(template.SourceBinHash, template.SourceWad)
            ?? throw new InvalidOperationException("The template source bin is no longer available. Rebuild the Workshop catalog.");
        byte[]? imported = MapMaterialFactory.ImportMaterial(target, source, template.MaterialHash,
            template.MaterialName, newName, out var error);
        if (imported is null) throw new InvalidOperationException(error ?? "The material could not be imported.");

        var staged = StageWorkshopAssets(template.TexturePaths, mapEntry);
        if (staged.Missing.Count > 0)
            throw new InvalidOperationException("Required texture(s) were not found in the installed patch: "
                + string.Join(", ", staged.Missing.Take(4)) + (staged.Missing.Count > 4 ? "..." : ""));
        if (!await SaveMapBinBytesAsync(binEntry, imported))
            throw new InvalidOperationException("The edited materials bin could not be saved.");

        FinishWorkshopMutation();
        await LoadMapGeoAsync(mapEntry);
        _log.Success("Workshop", $"Added material '{newName}' from {template.Shader} with {staged.Written} asset(s).");
        return $"Added '{newName}' to the current map. {staged.Written} texture asset(s) copied.";
    }

    private async Task<string> ImportWorkshopParticleAsync(WorkshopParticleTemplate template, string newName)
    {
        if (_currentMapEntry is not { } mapEntry || _currentMap is not { } map)
            throw new InvalidOperationException("Open the destination map before adding a particle.");
        if (!TryResolveMaterialsBin(mapEntry.Path, out var binEntry))
            throw new InvalidOperationException("The open map has no companion materials .bin.");
        if (!await EnsureProjectSavedAsync())
            throw new InvalidOperationException("Save the project before adding Workshop content.");

        byte[] target = GetAssetBytes(binEntry);
        var closure = _workshopCatalog?.ReadBinClosure(template.SourceBinHash, template.SourceWad)
            ?? Array.Empty<byte[]>();
        if (closure.Count == 0)
            throw new InvalidOperationException("The template source bins are no longer available. Rebuild the Workshop catalog.");

        var graph = BinObjectGraphImporter.Import(target, closure, new[] { template.SystemHash }, out var graphError)
            ?? throw new InvalidOperationException(graphError ?? "The particle object graph could not be imported.");
        var tree = SafeBinTree.Parse(graph.Bytes);
        var id = MapPlaceableWriter.NewParticleId(tree, HashAlgorithms.Fnv1a(newName));
        if (!id.IsValid)
            throw new InvalidOperationException("This map has no MapPlaceableContainer, so it cannot safely hold particle placements.");

        var transform = System.Numerics.Matrix4x4.Identity;
        transform.Translation = GizmoPivot ?? map.Center;
        var edit = new MapPlacementEdit(id)
        {
            CreateParticle = true,
            Name = newName,
            Transform = transform,
            SystemLink = template.SystemHash,
        };
        byte[] placed = MapPlaceableWriter.WriteEdits(graph.Bytes, new[] { edit }, out var placeError)
            ?? throw new InvalidOperationException(placeError ?? "The particle placement could not be created.");

        var staged = StageWorkshopAssets(graph.AssetPaths, mapEntry);
        if (staged.Missing.Count > 0)
            throw new InvalidOperationException("Required particle asset(s) were not found in the installed patch: "
                + string.Join(", ", staged.Missing.Take(4)) + (staged.Missing.Count > 4 ? "..." : ""));
        if (!await SaveMapBinBytesAsync(binEntry, placed))
            throw new InvalidOperationException("The edited materials bin could not be saved.");

        FinishWorkshopMutation();
        await LoadMapGeoAsync(mapEntry);
        var added = MapContent.AllParticles.FirstOrDefault(x => x.Placement.Id == id);
        if (added is not null) SelectedParticleNode = added;
        _log.Success("Workshop", $"Added particle '{newName}': {graph.ImportedObjects} object(s), {staged.Written} asset(s).");
        return $"Added '{newName}' at the viewport focus. {graph.ImportedObjects} linked object(s) and {staged.Written} asset(s) imported.";
    }

    private (int Written, IReadOnlyList<string> Missing) StageWorkshopAssets(
        IEnumerable<string> paths, WadAssetEntry destinationMap)
    {
        if (_workshopCatalog is null) return (0, paths.ToArray());
        var missing = new List<string>();
        var sources = new List<(string Path, byte[] Bytes)>();
        foreach (string raw in paths.Where(p => !string.IsNullOrWhiteSpace(p))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string path = raw.Trim().Replace('\\', '/').TrimStart('/');
            if (path.Length == 0 || path.Split('/').Any(part => part == "..")) { missing.Add(raw); continue; }
            byte[]? bytes = _workshopCatalog.ReadAsset(path);
            if (bytes is null) { missing.Add(path); continue; }
            sources.Add((path, bytes));
        }
        // Preflight the complete dependency set before touching the project. A failed import should not
        // leave half of a particle's textures behind as unexplained dead files.
        if (missing.Count > 0) return (0, missing);

        int written = 0;
        foreach (var (path, bytes) in sources)
        {
            ulong hash = HashAlgorithms.WadPath(path);

            if (Project.IsFolderProject && Project.RootPath is { } root)
            {
                string folder = RiotWadFolderName(destinationMap);
                string baseDir = Path.GetFullPath(Path.Combine(root, folder));
                string file = Path.GetFullPath(Path.Combine(baseDir, path.Replace('/', Path.DirectorySeparatorChar)));
                if (!file.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                { missing.Add(path); continue; }
                if (File.Exists(file)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllBytes(file, bytes);
                if (!Project.ProjectFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) Project.ProjectFolders.Add(folder);
                ClearShadowOverride(hash, Path.GetExtension(path));
                written++;
                continue;
            }

            if (_overrides.TryGet(hash, out var existing) && File.Exists(existing.OverrideFile)) continue;
            string stored = ProjectWorkspace.StoreOverrideBytes(Project, hash, bytes, Path.GetExtension(path));
            _overrides.Set(new ProjectAssetOverride
            {
                PathHash = hash,
                ResolvedPath = path,
                OverrideFile = stored,
                AddedUtc = DateTime.UtcNow.ToString("o"),
            });
            written++;
        }
        return (written, missing);
    }

    private void FinishWorkshopMutation()
    {
        Project.IsDirty = true;
        if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
        BuildMounts();
        BuildProjectTree();
        UpdateTitle();
    }

    /// <summary>M123: run the confirmed plan — create the new materials in the map's .materials.bin,
    /// bring imported textures into the project as plain DDS, then stage every included mesh.</summary>
    private async Task ExecuteAddMeshPlanAsync(AddMeshPlan plan)
    {
        if (_currentMap is not { } map || _currentMapEntry is not { } mapEntry) return;
        try
        {
            // 1) new materials (cloned templates), textures first so the clone can point at them
            var toCreate = plan.Materials.Where(m => m.CreateNew).ToList();
            if (toCreate.Count > 0)
            {
                if (!TryResolveMaterialsBin(mapEntry.Path, out var binEntry))
                { _log.Error("AddMesh", "No materials .bin found for this map — cannot create materials."); return; }
                var binBytes = GetAssetBytes(binEntry);
                if (binBytes is null) { _log.Error("AddMesh", "Could not read the materials .bin."); return; }
                _log.Info("AddMesh", $"Materials bin: {binEntry.Path} ({binEntry.SourceKind}, {binBytes.Length:n0} bytes).");

                foreach (var m in toCreate)
                {
                    string? diffusePath = null;
                    if (m.TextureBytes is not null)
                        diffusePath = SaveImportedTexture(m.TextureBytes, m.TextureFileNameHint ?? m.NewName!);

                    var def = MaterialEditor.Catalog?.Find(m.ShaderPath);
                    if (def is null) { _log.Error("AddMesh", $"Material '{m.NewName}': shader '{m.ShaderPath}' not in the catalogue."); return; }
                    var newBytes = MapMaterialFactory.CreateFromShader(binBytes, m.NewName!, def, out var err, diffusePath);
                    if (newBytes is null)
                    {
                        // dump the exact input so the failure is reproducible offline
                        try
                        {
                            var dump = Path.Combine(Path.GetTempPath(), "reyengine_addmesh_fail.bin");
                            File.WriteAllBytes(dump, binBytes);
                            _log.Error("AddMesh", $"Material '{m.NewName}': {err} — input dumped to {dump}");
                        }
                        catch { _log.Error("AddMesh", $"Material '{m.NewName}': {err}"); }
                        return;
                    }
                    binBytes = newBytes;
                    _log.Success("AddMesh", $"Material '{m.NewName}' built from shader {m.ShaderPath}"
                        + (diffusePath is not null ? $" with diffuse {diffusePath}" : "") + ".");
                }
                if (!await SaveMapBinBytesAsync(binEntry, binBytes))
                { _log.Error("AddMesh", "Could not save the materials .bin — meshes were NOT staged."); return; }
            }

            // 2) stage the meshes at the camera/gizmo focus with their chosen materials + layer mask
            var place = GizmoPivot ?? map.Center;
            int staged = 0;
            foreach (var mesh in plan.Meshes)
            {
                var (cmin, cmax) = BoundsOf(mesh.Positions);
                var center = (cmin + cmax) * 0.5f;
                var material = plan.MeshMaterialNames.TryGetValue(mesh.MaterialName, out var mn) && mn.Length > 0
                    ? mn : DefaultMapMaterial();
                var vm = new AddedMapMeshViewModel
                {
                    Name = mesh.Name,
                    Positions = mesh.Positions, Normals = mesh.Normals, Uvs = mesh.Uvs,
                    Indices = mesh.Indices, LocalCenter = center,
                    Material = material,
                    Offset = place - center,
                    VisibilityMask = plan.VisibilityMask,
                    EnabledVisibilityMask = plan.VisibilityMask,
                    StateChanged = OnMapContentItemStateChanged,
                };
                MapContent.AddedMeshes.Add(vm);
                staged++;
            }
            OnPropertyChanged(nameof(HasAddedMeshes));
            PublishAddedMeshPreview();
            if (MapContent.AddedMeshes.Count > 0)
                SelectedOutlinerItem = MapContent.AddedMeshes[^1];   // M123e: routes to selection -> gizmo
            _log.Success("AddMesh", $"Staged {staged} mesh(es) (layer mask 0b{Convert.ToString(plan.VisibilityMask & 0xFF, 2).PadLeft(8, '0')}). "
                + "Position them with the gizmo, then Save Map Edits.");
        }
        catch (Exception ex) { _log.Error("AddMesh", ex.Message); }
    }

    /// <summary>Decode a png/jpg blob and write it into the project folder as an uncompressed DDS.
    /// Returns the WAD path the material should reference, or null on failure.</summary>
    private string? SaveImportedTexture(byte[] imageBytes, string nameHint)
    {
        try
        {
            var mount = ProjectFolderMounts.FirstOrDefault();
            if (mount is null) { _log.Warn("AddMesh", "No project folder — imported texture skipped."); return null; }

            using var ms = new MemoryStream(imageBytes, writable: false);
            var bmp = new Avalonia.Media.Imaging.Bitmap(ms);
            int w = bmp.PixelSize.Width, h = bmp.PixelSize.Height;
            var bgra = new byte[w * h * 4];
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(bgra, System.Runtime.InteropServices.GCHandleType.Pinned);
            try { bmp.CopyPixels(new Avalonia.PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), bgra.Length, w * 4); }
            finally { handle.Free(); }
            for (int i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);

            var clean = new string(nameHint.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
            string rel = $"assets/maps/reyimported/{clean.ToLowerInvariant()}.dds";
            string dest = Path.Combine(mount.Location, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, MapMaterialFactory.WriteDds(w, h, bgra));
            _log.Success("AddMesh", $"Imported texture saved: {rel} ({w}x{h}).");
            return rel.ToUpperInvariant().StartsWith("ASSETS") ? "ASSETS" + rel[6..] : rel;
        }
        catch (Exception ex) { _log.Warn("AddMesh", $"Imported texture failed: {ex.Message}"); return null; }
    }

    [RelayCommand]
    private void RemoveAddedMesh(AddedMapMeshViewModel? vm)
    {
        if (vm is null) return;
        MapContent.AddedMeshes.Remove(vm);
        if (ReferenceEquals(SelectedAddedMesh, vm)) { SelectedAddedMesh = null; GizmoPivot = null; }
        OnPropertyChanged(nameof(HasAddedMeshes));
        PublishAddedMeshPreview();
        _log.Info("AddMesh", $"Removed '{vm.Name}' from the add queue.");
    }

    private (float[]? Pos, float[]? Nrm, float[]? Uv, int[]? Idx) ImportMeshFile(string file)
    {
        if (file.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
        {
            var m = Formats.Meshes.ObjMeshImporter.Import(File.ReadAllText(file), Path.GetFileName(file));
            return m is null ? default : (m.Positions, m.Normals, m.Uvs, m.Indices);
        }
        // .scb / .sco → triangle soup (no normals; the appender synthesises none, so pass null → flat up)
        var sm = Formats.Meshes.StaticObjectDecoder.Decode(File.ReadAllBytes(file), file);
        if (sm is null) return default;
        return (sm.Positions, null, sm.Uvs, System.Array.ConvertAll(sm.Indices, i => (int)i));
    }

    private static (System.Numerics.Vector3 Min, System.Numerics.Vector3 Max) BoundsOf(float[] pos)
    {
        var min = new System.Numerics.Vector3(float.MaxValue);
        var max = new System.Numerics.Vector3(float.MinValue);
        for (int i = 0; i + 2 < pos.Length; i += 3)
        {
            min = System.Numerics.Vector3.Min(min, new(pos[i], pos[i + 1], pos[i + 2]));
            max = System.Numerics.Vector3.Max(max, new(pos[i], pos[i + 1], pos[i + 2]));
        }
        return (min, max);
    }

    /// <summary>Pick a sensible default material for a new mesh: the first opaque map material, else the first.</summary>
    private string DefaultMapMaterial()
    {
        if (_currentMap is not { } map || map.Groups.Count == 0) return "";
        var opaque = map.Groups.FirstOrDefault(g => g.Material.Length > 0
            && _currentMapProfiles?.GetValueOrDefault(g.Material) is { RenderMode: MaterialRenderMode.Opaque });
        return (opaque ?? map.Groups.First(g => g.Material.Length > 0)).Material;
    }

    /// <summary>All map material names — for the inspector's material picker on an added mesh.</summary>
    public IReadOnlyList<string> MapMaterialNames =>
        _currentMap?.Groups.Select(g => g.Material).Where(m => m.Length > 0).Distinct().OrderBy(m => m).ToList()
        ?? (IReadOnlyList<string>)System.Array.Empty<string>();

    /// <summary>Publish the added meshes as a preview overlay (combined with the prop overlay).</summary>
    private void PublishAddedMeshPreview()
    {
        var instances = new List<PropInstanceData>(_propInstances);
        foreach (var a in MapContent.AddedMeshes)
        {
            if (!a.IsEditorVisible || a.IsDisabled || a.IsRemoved) continue;
            var mesh = new PropMesh(a.Name + "|" + a.Indices.Length,
                a.Positions, a.Normals, a.Uvs, System.Array.ConvertAll(a.Indices, i => (uint)i),
                new[] { new PropSubmesh(0, a.Indices.Length, null) });
            instances.Add(new PropInstanceData(mesh, a.Transform));
        }
        CurrentPropMeshes = instances.Count > 0 ? new PropRenderSet(instances) : null;
    }
    private IReadOnlyList<PropInstanceData> _propInstances = System.Array.Empty<PropInstanceData>();
    [ObservableProperty] private IReadOnlyList<ViewportMeshRenderer.SubmeshMaterial>? _currentModelSubmeshMaterials; // M32
    [ObservableProperty] private TextureImage? _currentGrassTint;                    // M78: map grass-tint texture
    [ObservableProperty] private System.Numerics.Vector4 _currentGrassTintRect;      // M78: minX, minZ, 1/spanX, 1/spanZ
    [ObservableProperty] private bool _hasFlowmapWater; // M44: current map has flowmap-river water → viewport animates it
    public ParticleEditorViewModel ParticleEditor { get; } = new(); // M46 Particle Editor
    [ObservableProperty] private bool _isParticleEditorActive;      // M46: overlay visible for the active tab
    [ObservableProperty] private double _currentLightmapScale = 1.0; // M45: MapSunProperties.lightMapColorScale
    [ObservableProperty] private MapSunProperties? _currentSunProperties;
    // M145: the fog toggle's visibility follows whichever map is loaded.
    partial void OnCurrentSunPropertiesChanged(MapSunProperties? value)
    {
        OnPropertyChanged(nameof(HasMapFog));
        if (!HasMapFog) ShowFog = false;   // don't carry a fog toggle onto a map that has none
    }
    [ObservableProperty] private AnimationClip? _currentAnimation;
    [ObservableProperty] private double _animationTime;
    /// <summary>M248 (phase 6, step 1): render the viewport with Direct3D 11 instead of OpenGL.
    ///
    /// <para>Off by default and deliberately reversible. The OpenGL path is the only reference for what the
    /// editor used to look like, so it stays until the D3D11 one is trusted - deleting it would remove the
    /// ability to A/B a regression, which is the whole point of having both.</para></summary>
    [ObservableProperty] private bool _useDx11Viewport;

    /// <summary>What the D3D11 surface is doing, for the status bar. Empty when it is not running.</summary>
    [ObservableProperty] private string _dx11ViewportStatus = "";

    /// <summary>M263: the frame cost line is now just the milliseconds, as asked. The draw/cull/unbound
    /// detail moves here and shows on hover - it is the diagnostic that found M229, M230, M255 and M261,
    /// so it is worth keeping reachable even when it is not worth staring at.</summary>
    [ObservableProperty] private string _dx11ViewportDetail = "";

    /// <summary>M263: drives the TIME constant. Pausing freezes the clock where it is rather than resetting
    /// it, so this holds a moment rather than jumping back to frame zero.</summary>
    [ObservableProperty] private bool _animationsPlaying = true;

    [ObservableProperty] private bool _showWireframe;
    [ObservableProperty] private bool _showBones;
    [ObservableProperty] private bool _showBounds;
    [ObservableProperty] private bool _cullBackfaces = true; // M34: respect per-material cullEnable by default (off = force all two-sided)
    [ObservableProperty] private bool _showLightmaps = true; // M69: baked lightmaps on by default; off = sun/sky fallback lighting
    // M70: legacy Riot dynamic point lights (Light.dat)
    [ObservableProperty] private bool _showDynamicLights;

    // M158: viewport lighting-mode preset. A convenience over the two flags above so the user can flip
    // between how a bake will look (Baked) and how the live editable lighting looks (Dynamic), plus a
    // debug view with both. -1 = "custom" (the individual toggles were flipped by hand, no preset owns
    // the current state). Setting a mode drives ShowLightmaps + ShowDynamicLights; flipping either flag
    // by hand resets the mode to custom rather than fighting the user.
    public const int LightingModeCustom = -1, LightingModeDynamic = 0, LightingModeBaked = 1, LightingModeCombined = 2;
    private bool _applyingLightingMode;
    [ObservableProperty] private int _lightingMode = LightingModeBaked;

    partial void OnLightingModeChanged(int value)
    {
        if (value < 0) return;   // custom: leave the flags as the user set them
        _applyingLightingMode = true;
        // Dynamic  = fallback sun/sky + editable point lights (no baked atlas) — the live authoring view.
        // Baked    = baked atlas only, point lights off — how the map ships after a bake.
        // Combined = both, a debug overlay to compare baked vs dynamic.
        ShowLightmaps = value != LightingModeDynamic;
        ShowDynamicLights = value != LightingModeBaked;
        _applyingLightingMode = false;
        OnPropertyChanged(nameof(IsLightingDynamic));
        OnPropertyChanged(nameof(IsLightingBaked));
        OnPropertyChanged(nameof(IsLightingCombined));
    }

    // Bindable one-per-mode flags for a segmented ToggleButton group in the toolbar.
    public bool IsLightingDynamic { get => LightingMode == LightingModeDynamic; set { if (value) LightingMode = LightingModeDynamic; } }
    public bool IsLightingBaked { get => LightingMode == LightingModeBaked; set { if (value) LightingMode = LightingModeBaked; } }
    public bool IsLightingCombined { get => LightingMode == LightingModeCombined; set { if (value) LightingMode = LightingModeCombined; } }

    partial void OnShowLightmapsChanged(bool value) => DropLightingPreset();
    partial void OnShowDynamicLightsChanged(bool value) => DropLightingPreset();
    private void DropLightingPreset()
    {
        if (_applyingLightingMode) return;
        if (LightingMode != LightingModeCustom)
        {
            LightingMode = LightingModeCustom;
            OnPropertyChanged(nameof(IsLightingDynamic));
            OnPropertyChanged(nameof(IsLightingBaked));
            OnPropertyChanged(nameof(IsLightingCombined));
        }
    }
    // M145: MapSunProperties distance fog. Off by default; only meaningful when the loaded map's sun
    // component authored a real fog range, which HasMapFog reflects so the toggle can hide itself.
    [ObservableProperty] private bool _showFog;
    public bool HasMapFog => CurrentSunProperties is { } s && s.TryGetFogRange(out _, out _);
    [ObservableProperty] private double _dynamicLightIntensity = 1.0;
    [ObservableProperty] private double _dynamicLightRadiusScale = 1.0;   // M71: global light-radius multiplier
    // M160: point-light falloff shape (0 = tight (1-t)^2, 1 = wide soft (1-t^2)^2). Kept in sync with
    // BakeSettings.FalloffSoftness so the Dynamic preview and the bake draw the same pools.
    [ObservableProperty] private double _lightFalloffSoftness = 0.6;
    [ObservableProperty] private double _dynamicLightPositionScale = 1.0; // M71: master light-position spread (XZ)
    [ObservableProperty] private double _dynamicLightScaleX = 1.0;        // M71: per-axis fine scale
    [ObservableProperty] private double _dynamicLightScaleZ = 1.0;
    [ObservableProperty] private double _dynamicLightOffsetX = 0.0;       // M71: world-space translate
    [ObservableProperty] private double _dynamicLightOffsetZ = 0.0;
    [ObservableProperty] private IReadOnlyList<PointLight>? _dynamicLights;
    [ObservableProperty] private string? _dynamicLightsStatus;
    [ObservableProperty] private bool _hasDynamicLights;

    // ---- M152: place and edit the point lights, then save the table back ----

    /// <summary>The editable light set — M153: this IS MapContent.Lights, so the outliner's "Lights"
    /// folder and the renderer stay one source of truth. DynamicLights (what the viewport draws) is
    /// republished from it on every change, so edits are live.</summary>
    public ObservableCollection<PointLightViewModel> EditableLights => MapContent.Lights;
    [ObservableProperty] private PointLightViewModel? _selectedLight;
    [ObservableProperty] private string? _lightDatPath;      // where Save writes back to
    public bool HasSelectedLight => SelectedLight is not null;

    partial void OnSelectedLightChanged(PointLightViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedLight));
        GizmoPivot = value?.Position;   // M75 gizmo drives the selected light
    }

    /// <summary>Rebuild the render list from the editable set (called after any add/edit/delete).</summary>
    public void RepublishLights()
    {
        DynamicLights = EditableLights.Select(l => l.ToPointLight()).ToList();
        HasDynamicLights = EditableLights.Count > 0;
        DynamicLightsStatus = EditableLights.Count == 0
            ? "no point lights"
            : $"{EditableLights.Count} point light(s)" + (LightDatPath is { } p ? $" — {Path.GetFileName(p)}" : " — unsaved");
        // M287: every light edit funnels through here - add, delete, duplicate, gizmo drag, inspector
        // field - so one capture here covers all of them without hooking each one.
        CaptureMapLighting();
    }

    /// <summary>M287: true while a map load or a restore is writing the lighting properties, so the change
    /// notifications that follow do NOT capture. Without it the reset that ApplySunProperties performs on
    /// every map open would be captured as the user's authored state and overwrite the very record the
    /// restore is about to read - the save would destroy itself, once per map load.</summary>
    private bool _applyingLighting;

    /// <summary>Copy the current lighting into the project record for the open map. In memory only: this
    /// runs on every gizmo drag and every slider tick, and writing JSON at that rate would be a file
    /// write per frame. The disk write happens on the discrete commands via PersistMapLighting, and any
    /// other project save picks this up because IsDirty is set here.</summary>
    private void CaptureMapLighting()
    {
        if (_applyingLighting || _currentMapEntry is not { } entry) return;

        var rec = Project.MapLighting.FirstOrDefault(r => r.PathHash == entry.PathHash);
        if (rec is null)
        {
            rec = new ReyEngine.Core.Projects.MapLightingRecord
            { PathHash = entry.PathHash, MapgeoPath = entry.Path };
            Project.MapLighting.Add(rec);
        }
        rec.MapgeoPath = entry.Path;

        rec.SunIntensity = SunIntensity;
        rec.SunColorR = SunColorR; rec.SunColorG = SunColorG; rec.SunColorB = SunColorB;
        rec.SkyIntensity = SkyIntensity;
        rec.SkyColorR = SkyColorR; rec.SkyColorG = SkyColorG; rec.SkyColorB = SkyColorB;
        rec.LightmapScale = CurrentLightmapScale;

        rec.LightIntensity = DynamicLightIntensity;
        rec.LightRadiusScale = DynamicLightRadiusScale;
        rec.FalloffSoftness = LightFalloffSoftness;
        rec.PositionScale = DynamicLightPositionScale;
        rec.ScaleX = DynamicLightScaleX;
        rec.ScaleZ = DynamicLightScaleZ;
        rec.OffsetX = DynamicLightOffsetX;
        rec.OffsetZ = DynamicLightOffsetZ;
        rec.LightDatPath = LightDatPath;

        rec.Lights = EditableLights.Select(l => new ReyEngine.Core.Projects.SavedPointLight
        {
            X = l.X, Y = l.Y, Z = l.Z,
            R = l.R, G = l.G, B = l.B,
            Radius = l.Radius, Intensity = l.Intensity, Name = l.Name,
        }).ToList();

        Project.IsDirty = true;
    }

    /// <summary>Capture and write. For the discrete edits - adding, deleting or importing lights - where
    /// losing the change to a crash would be worse than one JSON write.</summary>
    private void PersistMapLighting()
    {
        CaptureMapLighting();
        if (Project.ProjectFilePath is { } p) ReyProjectService.Save(Project, p);
    }

    /// <summary>M287: put the user's authored lighting back after a map load. MUST run after
    /// ApplySunProperties, which unconditionally resets sun/sky to the map's own values and SunIntensity to
    /// 1.0 - that reset is what made every edit look like it had never been made.</summary>
    private void RestoreMapLighting(Core.Assets.WadAssetEntry entry)
    {
        if (Project.MapLighting.FirstOrDefault(r => r.PathHash == entry.PathHash) is not { } rec) return;

        _applyingLighting = true;
        try
        {
            _suppressSunRebuild = true;
            SunIntensity = rec.SunIntensity;
            SunColorR = rec.SunColorR; SunColorG = rec.SunColorG; SunColorB = rec.SunColorB;
            SkyIntensity = rec.SkyIntensity;
            SkyColorR = rec.SkyColorR; SkyColorG = rec.SkyColorG; SkyColorB = rec.SkyColorB;
            _suppressSunRebuild = false;
            RebuildSun();

            CurrentLightmapScale = rec.LightmapScale;
            DynamicLightIntensity = rec.LightIntensity;
            DynamicLightRadiusScale = rec.LightRadiusScale;
            LightFalloffSoftness = rec.FalloffSoftness;
            DynamicLightPositionScale = rec.PositionScale;
            DynamicLightScaleX = rec.ScaleX;
            DynamicLightScaleZ = rec.ScaleZ;
            DynamicLightOffsetX = rec.OffsetX;
            DynamicLightOffsetZ = rec.OffsetZ;
            LightDatPath = rec.LightDatPath;

            EditableLights.Clear();
            foreach (var s in rec.Lights)
                EditableLights.Add(new PointLightViewModel(
                    PointLightViewModel.FromStored(s.X, s.Y, s.Z, s.R, s.G, s.B, s.Radius, s.Intensity), this)
                { Name = s.Name });
            SelectedLight = null;
        }
        finally { _applyingLighting = false; }

        RepublishLights();
        _log.Info("Lights", $"Restored this map's saved lighting — {rec.Lights.Count} point light(s)"
            + (rec.LightDatPath is { } p ? $", from {Path.GetFileName(p)}" : ""));
    }

    private void LoadEditableLights(IEnumerable<PointLight> lights)
    {
        EditableLights.Clear();
        int n = 1;
        foreach (var l in lights)
            EditableLights.Add(new PointLightViewModel(l, this) { Name = $"Light {n++}" });
        SelectedLight = null;
        RepublishLights();
        PersistMapLighting();   // M287: an import is a discrete edit worth a write
    }

    /// <summary>Add a light at the camera's focus so it lands in view rather than at the origin.</summary>
    [RelayCommand]
    private void AddLight()
    {
        var at = GizmoPivot ?? SelectedParticleMarker ?? System.Numerics.Vector3.Zero;
        var vm = new PointLightViewModel(new PointLight(at, new System.Numerics.Vector3(1f, 0.85f, 0.6f), 600f), this)
        { Name = $"Light {EditableLights.Count + 1}" };
        EditableLights.Add(vm);
        SelectedLight = vm;
        ShowDynamicLights = true;
        RepublishLights();
        PersistMapLighting();   // M287
        _log.Info("Lights", $"Added a point light at ({at.X:0}, {at.Y:0}, {at.Z:0}). Drag the gizmo to place it.");
    }

    /// <summary>M289: the lights picked in the Lighting window's table. The outliner still drives the
    /// single <see cref="SelectedLight"/> that the inspector edits; this is the separate, list-shaped
    /// selection that exists so a 374-light Light.dat can be pruned without 374 clicks.</summary>
    public ObservableCollection<PointLightViewModel> SelectedLights { get; } = new();

    /// <summary>Delete every light in the table selection, falling back to the single outliner selection
    /// so the button does the obvious thing whichever way the user picked a light.</summary>
    [RelayCommand]
    private void DeleteSelectedLights()
    {
        var doomed = SelectedLights.Count > 0
            ? SelectedLights.ToList()
            : SelectedLight is { } one ? new List<PointLightViewModel> { one } : new List<PointLightViewModel>();
        if (doomed.Count == 0) return;

        foreach (var l in doomed) EditableLights.Remove(l);
        SelectedLights.Clear();
        SelectedLight = null;
        RepublishLights();
        PersistMapLighting();
        _log.Info("Lights", $"Deleted {doomed.Count} point light(s); {EditableLights.Count} left.");
    }

    /// <summary>Empty the table. Separate from the multi-delete because "remove these six" and "throw the
    /// whole imported .dat away" are different intents, and making the second one reachable only by
    /// select-all is how people delete more than they meant to.</summary>
    [RelayCommand]
    private void ClearAllLights()
    {
        int n = EditableLights.Count;
        if (n == 0) return;
        EditableLights.Clear();
        SelectedLights.Clear();
        SelectedLight = null;
        RepublishLights();
        PersistMapLighting();
        _log.Info("Lights", $"Removed all {n} point light(s).");
    }

    [RelayCommand]
    private void DeleteLight()
    {
        if (SelectedLight is not { } l) return;
        EditableLights.Remove(l);
        SelectedLights.Remove(l);
        SelectedLight = null;
        RepublishLights();
        PersistMapLighting();   // M287
    }

    [RelayCommand]
    private void DuplicateLight()
    {
        if (SelectedLight is not { } l) return;
        var copy = new PointLightViewModel(l.ToPointLight(), this);
        copy.X += 100;   // offset so the copy is visibly separate
        EditableLights.Add(copy);
        SelectedLight = copy;
        RepublishLights();
        PersistMapLighting();   // M287
    }

    /// <summary>Write the table back out in Riot's Light.dat format.</summary>
    [RelayCommand]
    private async Task SaveLightDat()
    {
        if (EditableLights.Count == 0) { _log.Warn("Lights", "No lights to save."); return; }
        string? target = LightDatPath;
        if (target is null)
        {
            target = await Dialogs.SaveFileAsync("Save Light.dat", "Light.dat");
            if (target is null) return;
        }
        try
        {
            await File.WriteAllBytesAsync(target, LightDatFile.Write(EditableLights.Select(l => l.ToPointLight())));
            LightDatPath = target;
            RepublishLights();
            _log.Success("Lights", $"Saved {EditableLights.Count} point light(s) to {Path.GetFileName(target)}.");
        }
        catch (Exception ex) { _log.Error("Lights", $"Save failed: {ex.Message}"); }
    }

    [RelayCommand]
    private async Task SaveLightDatAs()
    {
        LightDatPath = null;
        await SaveLightDatCommand.ExecuteAsync(null);
    }

    // ---- M158: light baking ---------------------------------------------------------------------

    /// <summary>Set by the view so the ViewModel can open the (non-modal) Light Baking window.</summary>
    public Action? ShowLightBakeWindow { get; set; }

    /// <summary>M169: opens the Lighting window (was a 240px flyout on the viewport toolbar).</summary>
    public Action? ShowLightingWindow { get; set; }

    [RelayCommand]
    private void OpenLighting() => ShowLightingWindow?.Invoke();

    /// <summary>True when the loaded map actually has a lightmap layout to bake into. A lightmap-less
    /// mapgeo, or a legacy NVR map (which loads into MeshPreview, never into _currentMap), has nothing to
    /// re-light, so the command stays disabled.</summary>
    public bool CanBakeLighting => _currentMap is { } m && _currentMapEntry is not null
                                   && Formats.Baking.LightBaker.CanBakeExistingLayout(m);

    [RelayCommand]
    private void OpenLightBake()
    {
        // Deliberately NOT gated on CanBakeLighting: a map with no lightmap layout is exactly the case
        // where the window is most needed, because that is where a layout gets generated. Gating this on
        // "can already bake" made the layout generator unreachable on the maps that need it.
        if (!HasMapForLayout)
        {
            _log.Warn("Bake", "Open a mapgeo first.");
            return;
        }
        ShowLightBakeWindow?.Invoke();
    }

    /// <summary>Assemble the bake inputs from the current map + the live viewport lighting, so a bake
    /// reproduces exactly what the viewport shows. Returns null when nothing can be baked.</summary>
    public Services.LightBakeInputs? GatherBakeInputs(Formats.Baking.BakeSettings settings)
    {
        if (_currentMap is not { } map || _currentMapEntry is not { } entry) return null;
        if (!Formats.Baking.LightBaker.CanBakeExistingLayout(map)) return null;

        var lights = EditableLights
            .Select(l => l.ToPointLight())
            .Select(pl => new Formats.Baking.BakePointLight(pl.Position, pl.Color, pl.Radius, pl.Intensity))
            .ToList();

        var sun = CurrentSunProperties ?? _baseSun;
        var lighting = Services.LightBakeService.BuildLighting(
            sunDirectionTowardSun: sun.SunDirection,
            sunColor: new System.Numerics.Vector3(sun.SunColor.X, sun.SunColor.Y, sun.SunColor.Z),
            skyColor: new System.Numerics.Vector3(sun.SkyLightColor.X, sun.SkyLightColor.Y, sun.SkyLightColor.Z),
            skyScale: sun.SkyLightScale,
            lightMapColorScale: (float)CurrentLightmapScale,
            lights: lights,
            lightIntensity: (float)DynamicLightIntensity,
            lightRadiusScale: (float)DynamicLightRadiusScale,
            lightPositionScale: (float)DynamicLightPositionScale,
            lightPositionScaleXZ: new System.Numerics.Vector2((float)DynamicLightScaleX, (float)DynamicLightScaleZ),
            lightPositionOffset: new System.Numerics.Vector2((float)DynamicLightOffsetX, (float)DynamicLightOffsetZ),
            settings: settings);   // M168: the REAL settings — this used to be a throwaway default, so
                                   // SunShadows/PointLightShadows/FalloffSoftness ignored the UI entirely

        return new Services.LightBakeInputs
        {
            Map = map,
            MapgeoPath = entry.Path,
            Lighting = lighting,
            GroupLightmapEnabled = Services.LightBakeService.BuildGroupFlags(map, _currentMapProfiles),
            GroupOccluderEnabled = Services.LightBakeService.BuildOccluderFlags(map, _currentMapProfiles),
        };
    }

    /// <summary>M147: how many of the open map's meshes still lack a lightmap UV channel. Drives the
    /// layout panel: >0 means a layout can be generated for them, 0 means the map is fully covered.
    /// (HasLightmapUv reads Texcoord7 — see the M158 fix; it used to read the wrong channel.)</summary>
    public int MeshesWithoutLightmapUv => _currentMap?.Meshes.Count(m => !m.HasLightmapUv) ?? 0;

    /// <summary>M147: is a mapgeo open at all (the layout panel is meaningful only then).</summary>
    public bool HasMapForLayout => _currentMap is not null && _currentMapEntry is not null;

    /// <summary>M147: the open map's mesh count, for the layout panel's "N of M" summary.</summary>
    public int MapMeshCountForLayout => _currentMap?.Meshes.Count ?? 0;

    /// <summary>M147: a map is open and some of it has no lightmap UVs — a layout can be generated.</summary>
    public bool NeedsLightmapLayout => HasMapForLayout && MeshesWithoutLightmapUv > 0;

    /// <summary>M147: give the open map a lightmap layout — unwrap UV2, pack atlas regions, assign each
    /// mesh its BakedLight reference — then save the REWRITTEN mapgeo and reload it. Unlike baking, this
    /// rewrites geometry, so the result is validated by re-reading it before anything is saved.</summary>
    public async Task<Formats.Baking.LightmapLayoutResult?> GenerateLightmapLayoutAsync(Formats.Baking.BakeSettings settings)
    {
        if (_currentMap is null || _currentMapEntry is not { } entry || _currentMapBytes is null)
        { _log.Warn("Layout", "No map open."); return null; }

        var sourceBytes = _currentMapBytes;
        // M164: exclude VertexDeform foliage only. NO_BAKED_LIGHTING must NOT exclude a mesh here: on a
        // map with no lightmaps EVERY material carries it (all 183 on Map11/base_srx), because it
        // describes the map's current state, not a wish about future lightmaps. Excluding on it removed
        // every mesh, produced an empty layout, and the save gate then correctly rejected the result.
        // Generating a layout is precisely the act of giving these meshes lightmaps — so the macro is
        // CLEARED below instead, which is the "remove incompatible shader macros" step.
        var excludeMaterials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_currentMapProfiles is { } profs)
            foreach (var (name, p) in profs)
                if (p.IsVertexDeform) excludeMaterials.Add(name);

        // M314: DynamicEffect historically flowed through this builder automatically, but its new
        // lightmap cannot render in game without our experimental DX11 companion cache. Make the
        // geometry an explicit creator opt-in instead of silently committing to that second step.
        if (!settings.IncludeDynamicEffectMeshes)
        {
            if (!TryResolveMaterialsBin(entry.Path, out var layoutBinEntry))
            {
                _log.Error("Layout", "No materials.bin was found, so DynamicEffect meshes cannot be safely filtered.");
                return null;
            }
            try
            {
                var layoutDocument = Formats.Materials.MaterialDocument.Parse(
                    ReadAsset(layoutBinEntry.PathHash), ResolveBinName);
                foreach (var material in layoutDocument.Materials)
                    if (string.Equals(material.RenderShader ?? material.ShaderName,
                            Services.ExperimentalDynamicEffectShaderService.RenderShader,
                            StringComparison.OrdinalIgnoreCase))
                        excludeMaterials.Add(material.Name);
            }
            catch (Exception ex)
            {
                _log.Error("Layout", "DynamicEffect materials could not be identified for layout filtering: " + ex.Message);
                return null;
            }
        }

        string atlasFolder = settings.ResolveOutputFolder(entry.Path);
        int atlasStartIndex = NextGeneratedAtlasIndex(_currentMap, atlasFolder);
        var (result, bytes) = await Task.Run(() =>
        {
            // TryReadEditable refuses anything we cannot reproduce byte-for-byte, so we never rewrite a
            // mapgeo we don't fully understand.
            if (!Formats.MapGeo.MapGeoBinary.TryReadEditable(sourceBytes, out var map))
                return ((Formats.Baking.LightmapLayoutResult?)null, (byte[]?)null);

            var r = Formats.Baking.MapGeoLightmapBuilder.Build(map, new Formats.Baking.MapGeoLightmapBuilder.Settings
            {
                AtlasResolution = settings.AtlasResolution,
                TexelDensity = settings.TexelDensity,
                Padding = settings.Padding,
                AtlasStartIndex = atlasStartIndex,
                AtlasPathFormat = atlasFolder + "{0}.tex",
                // M163/M314: don't spend atlas space on moving VertexDeform foliage or on the
                // DynamicEffect materials the creator left opted out. Render-region meshes are skipped
                // by the builder's own default.
                ExcludeMaterials = excludeMaterials,
            });
            return (r, map.Write());
        });

        if (result is null || bytes is null)
        { _log.Error("Layout", "This mapgeo could not be safely rewritten (it does not round-trip byte-exactly)."); return null; }

        // Validate the rewrite BEFORE saving: it must decode again and actually carry the new layout.
        try
        {
            var check = await Task.Run(() => Formats.MapGeo.MapGeoDecoder.Decode(bytes));
            if (!Formats.Baking.LightBaker.CanBakeExistingLayout(check))
            { _log.Error("Layout", "The rewritten mapgeo decoded but carries no usable lightmap layout — not saved."); return null; }
        }
        catch (Exception ex)
        { _log.Error("Layout", $"The rewritten mapgeo failed to decode ({ex.Message}) — not saved."); return null; }

        WriteBakedAsset(entry.Path, bytes, ".mapgeo");
        int macrosCleared = ClearNoBakedLightingMacros(entry, result.LaidOutMaterials, settings);
        Project.IsDirty = true;
        if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
        if (Project.IsFolderProject) { BuildMounts(); BuildProjectTree(); }
        UpdateTitle();

        foreach (var w in result.Warnings.Take(5)) _log.Warn("Layout", w);
        if (result.Warnings.Count > 5) _log.Warn("Layout", $"(+{result.Warnings.Count - 5} more warnings)");
        _log.Success("Layout", $"Generated a lightmap layout: {result.MeshesLaidOut} mesh(es) over {result.AtlasCount} atlas(es) " +
                               $"from {result.GeometriesUnwrapped} unique geometries" +
                               (result.MeshesExcluded > 0 ? $", {result.MeshesExcluded} excluded (material filter / render regions)" : "") +
                               (result.MeshesSkipped > 0 ? $", {result.MeshesSkipped} skipped" : "") +
                               (macrosCleared > 0 ? $"; cleared NO_BAKED_LIGHTING on {macrosCleared} material(s)" : "") +
                               $". Mapgeo rewritten ({bytes.Length:n0} bytes) — now bake into it.");

        await LoadMapGeoAsync(entry);
        OnPropertyChanged(nameof(CanBakeLighting));
        OnPropertyChanged(nameof(NeedsLightmapLayout));
        OnPropertyChanged(nameof(MeshesWithoutLightmapUv));
        return result;
    }

    /// <summary>M314: incremental layout passes must not reuse atlas 0. This is what allows a creator
    /// to opt DynamicEffect meshes in after first generating a conservative static-only layout.</summary>
    private static int NextGeneratedAtlasIndex(Formats.MapGeo.MapGeoAsset map, string atlasFolder)
    {
        int next = 0;
        foreach (string path in map.Groups.Select(g => g.LightmapTexture))
        {
            if (string.IsNullOrWhiteSpace(path)
                || !path.StartsWith(atlasFolder, StringComparison.OrdinalIgnoreCase)) continue;
            string file = Path.GetFileName(path);
            int dot = file.IndexOf('.');
            string indexText = dot < 0 ? Path.GetFileNameWithoutExtension(file) : file[..dot];
            if (int.TryParse(indexText, out int index) && index >= next) next = index + 1;
        }
        return next;
    }

    /// <summary>
    /// M312 experimental: give lightmapped SRX_DynamicEffect map groups a custom DX11 permutation, stage
    /// it as a ShaderCache companion WAD, then clear NO_BAKED_LIGHTING only on materials the generated
    /// cache positively covers. Riot's installed cache is never edited.
    /// </summary>
    public async Task<string> EnableExperimentalDynamicEffectLightmapsAsync()
    {
        if (_currentMap is not { } map || _currentMapEntry is not { } mapEntry)
            return "Load a map first.";
        if (!Project.IsFolderProject || Project.RootPath is not { } root)
            return "Experimental shader-cache patches require a saved folder project.";
        if (!TryResolveMaterialsBin(mapEntry.Path, out var binEntry))
            return "No materials.bin was found alongside this mapgeo.";

        string? finalDir = GameReferenceLibrary.FindFinalDirectory(Project.GameDirectory);
        if (finalDir is null)
            return "Set the League game folder in Project Settings first.";

        byte[] originalBin;
        Formats.Materials.MaterialDocument document;
        try
        {
            originalBin = ReadAsset(binEntry.PathHash);
            document = Formats.Materials.MaterialDocument.Parse(originalBin, ResolveBinName);
        }
        catch (Exception ex) { return "Materials could not be read: " + ex.Message; }

        var lightmappedNames = map.Groups
            .Where(g => !string.IsNullOrWhiteSpace(g.LightmapTexture))
            .Select(g => g.Material)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = document.Materials
            .Where(m => lightmappedNames.Contains(m.Name)
                     && string.Equals(m.RenderShader ?? m.ShaderName,
                         Services.ExperimentalDynamicEffectShaderService.RenderShader,
                         StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0)
            return "No lightmapped SRX_DynamicEffect material was found on this map.";

        Services.ExperimentalDynamicEffectPatch patch;
        try
        {
            patch = await Task.Run(() =>
            {
                using var cache = Formats.Shaders.ShaderCacheReader.Open(finalDir, _resolver.Database, out var cacheError)
                    ?? throw new InvalidOperationException(cacheError);
                using var definitions = new DisposableShaderDefinitions(finalDir);
                return Services.ExperimentalDynamicEffectShaderService.Build(cache, definitions.Value, candidates);
            });
        }
        catch (Exception ex)
        {
            _log.Error("Shader", "Experimental DynamicEffect patch failed: " + ex.Message);
            return "Shader patch generation failed: " + ex.Message;
        }

        int cleared = 0;
        foreach (var material in candidates)
            if (patch.SupportedMaterials.Contains(material.Name)
                && material.RemoveMacro(Formats.Materials.MaterialBinding.MacroNoBakedLighting))
                cleared++;

        byte[]? changedBin = null;
        if (cleared > 0)
        {
            try
            {
                changedBin = document.Serialize();
                _ = Formats.Materials.MaterialDocument.Parse(changedBin, ResolveBinName);
            }
            catch (Exception ex) { return "The material rewrite did not validate: " + ex.Message; }
        }

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string backupRoot = Path.Combine(root, ".reyengine", "backups", "dynamic-effect-lightmap-" + stamp);
        string shaderFolder = Path.Combine(root, "ShaderCache.dx11");
        try
        {
            Directory.CreateDirectory(backupRoot);
            File.WriteAllBytes(Path.Combine(backupRoot, Path.GetFileName(binEntry.Path)), originalBin);

            // Persist the companion folder before touching the material. A failed save or partial shader
            // write then leaves the still-unlit material safe; the crash-sensitive macro is cleared last.
            if (!Project.ProjectFolders.Contains("ShaderCache.dx11", StringComparer.OrdinalIgnoreCase))
                Project.ProjectFolders.Add("ShaderCache.dx11");
            Project.IsDirty = true;
            if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);

            foreach (var asset in patch.Assets)
            {
                string destination = Path.Combine(shaderFolder,
                    asset.Path.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(destination))
                {
                    string backup = Path.Combine(backupRoot, "ShaderCache.dx11",
                        asset.Path.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(destination, backup, overwrite: true);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllBytes(destination, asset.Bytes);
            }

            if (changedBin is not null) WriteBakedAsset(binEntry.Path, changedBin, ".bin");
        }
        catch (Exception ex)
        {
            _log.Error("Shader", "Could not stage the experimental cache patch: " + ex.Message);
            return "Could not stage the shader patch: " + ex.Message;
        }

        try
        {
            BuildMounts();
            BuildProjectTree();
            UpdateTitle();
        }
        catch (Exception ex)
        {
            // The patch is already transactionally complete on disk. A remount failure must not imply
            // that it was rolled back; rebuilding/reopening the project will pick up the staged files.
            _log.Warn("Shader", "Shader patch was staged, but the project view did not refresh: " + ex.Message);
        }

        // The bake filter is built from the loaded material profiles. Remounting alone leaves those
        // profiles carrying the pre-patch NO_BAKED_LIGHTING value, so the UI can claim five referenced
        // atlases and the baker still excludes every triangle. Reload before the command returns; the
        // Light Baking window refresh that follows now sees the rewritten materials immediately.
        await LoadMapGeoAsync(mapEntry);

        string result = $"Experimental DynamicEffect lightmaps enabled on {patch.SupportedMaterials.Count:n0} material(s)"
                      + (cleared > 0 ? $"; removed NO_BAKED_LIGHTING from {cleared:n0}" : "; shader cache refreshed") + ". "
                      + $"Generated {patch.Detail} and staged ShaderCache.dx11.wad.client content. "
                      + "Build Package, install both WADs, and test in Practice Tool.";
        _log.Warn("Shader", result + $" Backup: {backupRoot}");
        return result;
    }

    /// <summary>ShaderPermutationIndex has a Dispose method for its LeagueToolkit WAD but predates the
    /// IDisposable interface. This tiny adapter keeps worker ownership explicit.</summary>
    private sealed class DisposableShaderDefinitions : IDisposable
    {
        public Formats.Materials.ShaderPermutationIndex Value { get; }
        public DisposableShaderDefinitions(string finalDir) => Value = new(finalDir);
        public void Dispose() => Value.Dispose();
    }

    /// <summary>M166: the shipped shader-permutation set, used to decide whether clearing a macro would
    /// leave the material asking for a shader the client cannot load. Built once per game directory.</summary>
    // ---- M249 (phase 6, step 2): hand the open map to the side-by-side D3D11 surface ----

    /// <summary>M252: the A/B diff result, into the console where it can be read and copied.</summary>
    public void LogRendererDiff(string text) => _log.Info("DX11", "renderer A/B diff" + System.Environment.NewLine + text);

    private Formats.Shaders.ShaderCacheReader? _dx11ShaderCache;
    private string? _dx11ShaderCacheDir;

    /// <summary>M266: the open shader cache, for the D3D11 surface's particle driver. Read-only and the
    /// minimum exposure that works: the driver takes its TEXTURES from the already-resolved VfxPlaybackItem
    /// lists, so it needs no asset reader at all - which is also what guarantees it resolves the same files
    /// the GL viewport does.</summary>
    public Formats.Shaders.ShaderCacheReader? Dx11ShaderCache => _dx11ShaderCache;

    /// <summary>M249: build the currently open map into <paramref name="renderer"/>. Returns a report, or a
    /// reason string when there is nothing to build - never null, because "the viewport is empty and I do
    /// not know why" is the state this whole phase exists to avoid.</summary>
    public string BuildDx11Scene(ReyEngine.Rendering.D3D11.ShaderPreviewRenderer renderer)
    {
        if (_currentMap is not { } map) return "No map open - the D3D11 surface has no scene to draw yet.";
        if (_currentMapEntry is not { } mapEntry) return "The open map has no WAD entry.";

        string? dir = GameReferenceLibrary.FindFinalDirectory(Project.GameDirectory);
        if (dir is null || !Directory.Exists(dir)) return "Game directory is not set, so the shader cache cannot be opened.";

        if (_dx11ShaderCache is null || !string.Equals(_dx11ShaderCacheDir, dir, StringComparison.OrdinalIgnoreCase))
        {
            _dx11ShaderCache = Formats.Shaders.ShaderCacheReader.Open(dir, _resolver.Database, out var cacheErr);
            _dx11ShaderCacheDir = dir;
            if (_dx11ShaderCache is null) return "ShaderCache.dx11.wad.client: " + (cacheErr ?? "not readable");
        }

        // The map's own materials.bin - the same sibling lookup the material editor uses, so the viewport
        // and the editor cannot disagree about which bin describes this map.
        if (!TryResolveMaterialsBin(mapEntry.Path, out var binEntry))
            return "No materials.bin alongside this mapgeo.";

        Formats.Materials.MaterialDocument doc;
        try { doc = Formats.Materials.MaterialDocument.Parse(GetAssetBytes(binEntry), ResolveBinName); }
        catch (Exception ex) { return $"{binEntry.DisplayName}: {ex.Message}"; }

        var result = Services.Dx11SceneBuilder.Commit(
            renderer,
            Services.Dx11SceneBuilder.Prepare(_dx11ShaderCache, ShaderPerms(), map, doc.Materials,
                TryReadAssetBytes, mapEntry.Path),
            AppInfo.DisplayVersion);

        _log.Info("DX11", $"viewport scene: {result.Materials} material(s), {result.Failed} unresolved, "
                          + $"{result.Slices} slice(s), {result.Textures} texture binding(s)");
        // M278: never log a failure COUNT on its own. This exact line read "0 material(s), 21 unresolved"
        // for an afternoon while the shader cache had simply been renamed underneath us, and it named
        // nothing that could be looked up.
        foreach (var why in result.Reasons) _log.Warn("DX11", "unresolved - " + why);
        return result.Report;
    }

    /// <summary>M250: the async form. The CPU half - mesh build, permutation resolution, and every texture
    /// decode - runs on a worker; only the D3D commit comes back to the UI thread. Map12/bloom spent 5.5 s
    /// in here synchronously, almost all of it decoding 2,860 texture bindings.</summary>
    public async Task<string> BuildDx11SceneAsync(ReyEngine.Rendering.D3D11.ShaderPreviewRenderer renderer)
    {
        if (_currentMap is not { } map) return "No map open - the D3D11 surface has no scene to draw yet.";
        if (_currentMapEntry is not { } mapEntry) return "The open map has no WAD entry.";

        string? dir = GameReferenceLibrary.FindFinalDirectory(Project.GameDirectory);
        if (dir is null || !Directory.Exists(dir)) return "Game directory is not set, so the shader cache cannot be opened.";

        if (_dx11ShaderCache is null || !string.Equals(_dx11ShaderCacheDir, dir, StringComparison.OrdinalIgnoreCase))
        {
            _dx11ShaderCache = Formats.Shaders.ShaderCacheReader.Open(dir, _resolver.Database, out var cacheErr);
            _dx11ShaderCacheDir = dir;
            if (_dx11ShaderCache is null) return "ShaderCache.dx11.wad.client: " + (cacheErr ?? "not readable");
        }

        if (!TryResolveMaterialsBin(mapEntry.Path, out var binEntry))
            return "No materials.bin alongside this mapgeo.";

        byte[] binBytes;
        try { binBytes = GetAssetBytes(binEntry); }
        catch (Exception ex) { return $"{binEntry.DisplayName}: {ex.Message}"; }

        var cache = _dx11ShaderCache;
        var perms = ShaderPerms();

        Services.Dx11SceneBuilder.PreparedScene? prepared = null;
        string? error = null;
        await Task.Run(() =>
        {
            try
            {
                var doc = Formats.Materials.MaterialDocument.Parse(binBytes, ResolveBinName);
                prepared = Services.Dx11SceneBuilder.Prepare(cache, perms, map, doc.Materials,
                    TryReadAssetBytes, mapEntry.Path);
            }
            catch (Exception ex) { error = ex.Message; }
        });

        if (prepared is null) return error ?? "the scene could not be prepared";

        var result = Services.Dx11SceneBuilder.Commit(renderer, prepared, AppInfo.DisplayVersion);
        _log.Info("DX11", $"viewport scene: {result.Materials} material(s), {result.Failed} unresolved, "
                          + $"{result.Slices} slice(s), {result.Textures} texture binding(s)");
        // M278: never log a failure COUNT on its own. This exact line read "0 material(s), 21 unresolved"
        // for an afternoon while the shader cache had simply been renamed underneath us, and it named
        // nothing that could be looked up.
        foreach (var why in result.Reasons) _log.Warn("DX11", "unresolved - " + why);
        return result.Report;
    }

    private Formats.Materials.ShaderPermutationIndex? _shaderPerms;
    private string? _shaderPermsDir;
    private Formats.Materials.ShaderPermutationIndex? ShaderPerms()
    {
        string? dir = string.IsNullOrEmpty(Project.GameDirectory) ? null
            : Path.Combine(Project.GameDirectory, "DATA", "FINAL");
        if (dir is null || !Directory.Exists(dir)) return null;
        if (_shaderPerms is null || !string.Equals(_shaderPermsDir, dir, StringComparison.OrdinalIgnoreCase))
        {
            _shaderPerms = new Formats.Materials.ShaderPermutationIndex(dir);
            _shaderPermsDir = dir;
        }
        return _shaderPerms;
    }

    /// <summary>M164: clear NO_BAKED_LIGHTING from the materials that just received a lightmap layout.
    /// Without this the whole exercise is inert: the meshes point at an atlas, but both the game and our
    /// own bake skip them because the macro says "ignore baked lighting". This is the "remove
    /// incompatible shader macros" half of preparing a mesh for baking. Returns how many were cleared.</summary>
    private int ClearNoBakedLightingMacros(WadAssetEntry mapEntry, IReadOnlyCollection<string> materials,
        Formats.Baking.BakeSettings settings)
    {
        if (materials.Count == 0) return 0;
        if (!TryResolveMaterialsBin(mapEntry.Path, out var binEntry)) 
        { _log.Warn("Layout", "No materials .bin found — NO_BAKED_LIGHTING could not be cleared, so the new atlases will not be sampled."); return 0; }

        try
        {
            var binBytes = ReadAsset(binEntry.PathHash);
            var doc = Formats.Materials.MaterialDocument.Parse(binBytes, ResolveBinName);
            var perms = ShaderPerms();
            bool canValidate = perms is not null && perms.IsAvailable;
            if (!canValidate)
                _log.Warn("Layout", "No shader cache found (set the game folder) — NO_BAKED_LIGHTING was left alone. " +
                                    "Clearing it blindly can ask the client for a shader permutation Riot never cooked.");

            int cleared = 0, refused = 0;
            foreach (var m in doc.Materials)
            {
                if (m.Name is not { } n || !materials.Contains(n)) continue;
                // M166: only clear where the resulting define set is one the game actually ships. On
                // Map11/base_srx 20 of 184 materials are NOT, and clearing them is what made the client
                // log "Unable to find correct hash for shader ... in wad" and fail to compile.
                if (!canValidate || !perms!.CanRemoveMacro(m, Formats.Materials.MaterialBinding.MacroNoBakedLighting)) { refused++; continue; }
                if (m.RemoveMacro(Formats.Materials.MaterialBinding.MacroNoBakedLighting)) cleared++;
            }
            if (refused > 0)
                _log.Info("Layout", $"{refused} material(s) keep NO_BAKED_LIGHTING — the game ships no shader " +
                                    "permutation for them without it, so they stay unlit rather than failing to render.");
            var binOut = cleared > 0 ? doc.Serialize() : binBytes;

            // M167: register the lightgrid we are about to bake. Without lightGridFileName nothing loads
            // it, so probe lighting for characters/effects/NO_BAKED_LIGHTING surfaces would stay dead.
            string gridPath = settings.ResolveOutputFolder(mapEntry.Path) + settings.LightGridFileName();
            var stamped = Formats.MapGeo.MapBakeProperties.Write(
                binOut, gridPath, settings.LightGridWidth, 0.5f, out var bakeResult);
            if (stamped is not null) { binOut = stamped; _log.Info("Layout", "MapBakeProperties: " + bakeResult.Detail); }
            else _log.Warn("Layout", "Could not write MapBakeProperties (" + bakeResult.Detail +
                                     ") — the baked lightgrid will not be loaded by the game.");

            if (cleared == 0 && stamped is null) return 0;
            WriteBakedAsset(binEntry.Path, binOut, ".bin");
            return cleared;
        }
        catch (Exception ex)
        {
            _log.Warn("Layout", $"Could not clear NO_BAKED_LIGHTING ({ex.Message}) — the new atlases will not be sampled until it is removed.");
            return 0;
        }
    }

    /// <summary>Build a bake service bound to the current project, or null when there is nowhere to write
    /// (an unsaved project). A folder project writes atlases to their real path; a saved single-WAD
    /// project uses the hashed override store.</summary>
    public Services.LightBakeService? MakeBakeService()
    {
        bool canWrite = (Project.IsFolderProject && Project.RootPath is not null)
                        || Project.OverridesDirectory is not null;
        return canWrite ? new Services.LightBakeService(WriteBakedAsset) : null;
    }

    /// <summary>M158: write a baked lightmap file where it BELONGS. For a folder project that means the
    /// asset's real path inside the map's project folder (…/Map12/assets/maps/lightmaps/…/0.tex) — the
    /// packer hashes folder files by their relative path, so this lands as the exact chunk the game
    /// reads, and it shows up in the project tree instead of as an opaque hash. A single-WAD project has
    /// no folder to place into, so it falls back to the hashed override store. Returns the path written.</summary>
    private string WriteBakedAsset(string assetPath, byte[] bytes, string ext)
    {
        ulong hash = HashAlgorithms.WadPath(assetPath);
        if (Project.IsFolderProject && Project.RootPath is { } root && _currentMapEntry is { } mapEntry)
        {
            // Stage under the SAME WAD folder the map itself lives in (Map12.wad.client → "Map12"): the
            // game loads a map's lightmaps from that same wad.
            string folderName = RiotWadFolderName(mapEntry);
            // A newly generated atlas has no Riot source hash yet. More subtly, after a rewritten
            // mapgeo is remounted its source chain can temporarily lack the original WAD as well; using
            // "Overrides" then puts only the last new atlas into Overrides.wad.client while its siblings
            // land in Map11. The map path is authoritative about its home shipping WAD.
            if (folderName == "Overrides" && MapNameFromAssetPath(mapEntry.Path) is { } mapName)
                folderName = mapName;
            string dest = Path.Combine(root, folderName, assetPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, bytes);
            if (folderName != "Overrides")
            {
                // Repair output created by the old fallback above. The correct copy is durable before
                // the stale loose fallback is removed, so an interrupted bake never loses the atlas.
                string stale = Path.Combine(root, "Overrides", assetPath.Replace('/', Path.DirectorySeparatorChar));
                if (!string.Equals(stale, dest, StringComparison.OrdinalIgnoreCase) && File.Exists(stale))
                    File.Delete(stale);
            }
            if (!Project.ProjectFolders.Contains(folderName, StringComparer.OrdinalIgnoreCase))
                Project.ProjectFolders.Add(folderName);
            // A hashed override (ProjectOverride, priority 0) outranks a folder file (ProjectFolder,
            // priority 1) for the same hash — so an atlas left in the override store by an EARLIER bake
            // (before atlases landed in the folder) would shadow this one and the viewport would keep
            // showing the stale bake no matter how we re-bake. Clear it so the folder file wins.
            ClearShadowOverride(hash, ext);
            return dest;
        }

        var overrideFile = ProjectWorkspace.StoreOverrideBytes(Project, hash, bytes, ext);
        _overrides.Set(new ProjectAssetOverride
        {
            PathHash = hash,
            ResolvedPath = assetPath,
            OverrideFile = overrideFile,
            AddedUtc = DateTime.UtcNow.ToString("o"),
        });
        return overrideFile;
    }

    /// <summary>Delete a hashed override that would shadow a folder-placed baked file (and its record),
    /// so the fresh folder file wins. Lightweight — the caller rebuilds mounts once after the whole bake.</summary>
    private void ClearShadowOverride(ulong hash, string ext)
    {
        try
        {
            if (Project.OverridesDirectory is { } dir)
            {
                var f = Path.Combine(dir, $"{hash:x16}{ext}");
                if (File.Exists(f)) File.Delete(f);
            }
        }
        catch { /* best-effort — a locked/absent override just stays, BuildMounts still favours nothing worse */ }
        _overrides.Remove(hash);
    }

    public void OnLightBakeFinished(Services.LightBakeResult result)
    {
        if (result.AtlasCount == 0)
            _log.Error("Bake", $"Baked 0 of {result.ReferencedAtlasCount} referenced atlas(es); "
                + $"{result.SkippedAtlasCount} had no material-eligible triangles. "
                + "For SRX_DynamicEffect, use Enable DynamicEffect Lightmaps (Experimental), wait for the map reload, then retry."
                + (result.WroteLightGrid ? " The lightgrid was written." : ""));
        else if (result.SkippedAtlasCount > 0)
            _log.Warn("Bake", result.OutputDescription
                + $" ({result.AtlasCount} baked, {result.SkippedAtlasCount} skipped). "
                + "Skipped atlases contain only materials or meshes that currently opt out of baked lighting.");
        else
            _log.Success("Bake", result.OutputDescription + $" ({result.AtlasCount} atlas(es)).");
        if (result.TotalBytes == 0) return;
        Project.IsDirty = true;
        if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
        // Re-index: a folder project just gained new files on disk that the mounts don't know about yet;
        // without a remount ReadAsset would still serve Riot's atlases and the viewport wouldn't change.
        if (Project.IsFolderProject)
        {
            BuildMounts();
            BuildProjectTree();
        }
        UpdateTitle();
        // Switch the viewport to Baked so the user immediately sees the freshly baked lighting (atlas on,
        // dynamic lights off) instead of the live authoring view they baked from.
        LightingMode = LightingModeBaked;
        // Re-read the map so the viewport samples the freshly baked atlases instead of Riot's.
        if (_currentMapEntry is { } e) _ = LoadMapGeoAsync(e);
    }

    // ==================================================================== M171: recolour textures

    // ============================================================ M172a: closest-hit ray index

    private Rendering.MeshRayIndex? _rayIndex;
    private MapGeoAsset? _rayIndexMap;
    private int _rayIndexRevision = -1;
    private readonly object _rayIndexLock = new();

    /// <summary>A BVH over the open map's triangles, built on demand and rebuilt whenever the geometry
    /// moves. Replaces the brute-force picker, which scanned all 909,993 Summoner's Rift triangles per
    /// ray: measured 11.13 ms against 1.28 µs here, over 3,000 rays returning bit-identical hits.
    ///
    /// The build costs ~1.15 s on that map, so <see cref="PrebuildRayIndex"/> starts it in the background
    /// as soon as a map loads; this accessor only ever pays it if something asks before that finishes.
    /// Invalidated by <c>MeshVerticesRevision</c> because transform edits mutate MapGeoAsset.Positions in
    /// place — a stale tree would silently pick the geometry's old location.</summary>
    private Rendering.MeshRayIndex? RayIndex
    {
        get
        {
            if (_currentMap is not { } map || map.Groups.Count == 0) return null;
            lock (_rayIndexLock)
            {
                if (_rayIndex is not null && ReferenceEquals(_rayIndexMap, map) && _rayIndexRevision == MeshVerticesRevision)
                    return _rayIndex;
                _rayIndex = BuildRayIndex(map, out _);
                _rayIndexMap = map;
                _rayIndexRevision = MeshVerticesRevision;
                return _rayIndex;
            }
        }
    }

    private static Rendering.MeshRayIndex BuildRayIndex(MapGeoAsset map, out long ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var subs = map.Groups.Select(g => (g.StartIndex, g.IndexCount)).ToList();
        var index = new Rendering.MeshRayIndex(map.Positions, map.Uvs, map.Indices, subs);
        sw.Stop();
        ms = sw.ElapsedMilliseconds;
        return index;
    }

    /// <summary>Warm the ray index off the UI thread right after a map loads, so the first click doesn't
    /// wear the build cost.</summary>
    private void PrebuildRayIndex(MapGeoAsset map, int revision)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var index = BuildRayIndex(map, out long ms);
                lock (_rayIndexLock)
                {
                    // Only publish if nothing changed underneath us while we were building.
                    if (!ReferenceEquals(_currentMap, map) || MeshVerticesRevision != revision) return;
                    _rayIndex = index;
                    _rayIndexMap = map;
                    _rayIndexRevision = revision;
                }
                _log.Info("Viewport", $"Ray index: {index.TriangleCount:n0} triangles in {ms:n0} ms.");
            }
            catch (Exception ex) { _log.Warn("Viewport", $"Ray index build failed ({ex.Message}) — picking falls back to a rebuild on first click."); }
        });
    }

    private void InvalidateRayIndex()
    {
        lock (_rayIndexLock) { _rayIndex = null; _rayIndexMap = null; _rayIndexRevision = -1; }
        // The paint session caches the index and the texture set; both are about to be different.
        _paintSession = null;
        _paintStrokeActive = false;
    }

    // ============================================================ M172c: paint on meshes

    /// <summary>Set by the view: pushes a painted rectangle to the GPU without a reload.</summary>
    public Action<TextureImage, Avalonia.PixelRect>? PushTextureRegion { get; set; }
    /// <summary>Set by the view: shows the brush footprint on the surface (null centre hides it).</summary>
    public Action<System.Numerics.Vector3?, System.Numerics.Vector3, float, float>? ShowBrushRing { get; set; }
    /// <summary>Set by the view: force a mip rebuild once a stroke has finished.</summary>
    public Action? RebuildTextureMips { get; set; }

    [ObservableProperty] private bool _isPaintMode;
    [ObservableProperty] private Avalonia.Media.Color _paintColor = Avalonia.Media.Color.FromRgb(200, 60, 40);
    /// <summary>Brush radius in WORLD units. Texel density varies by orders of magnitude between meshes,
    /// so a texel-sized brush would be a speck on the ground and swallow a prop whole.</summary>
    [ObservableProperty] private double _paintRadius = 120;
    [ObservableProperty] private double _paintHardness = 0.5;
    [ObservableProperty] private double _paintOpacity = 1.0;
    [ObservableProperty] private double _paintSeamBleed = 3;
    /// <summary>M173: mask rotation in DEGREES (radians in the brush itself — degrees is what a slider
    /// should show).</summary>
    [ObservableProperty] private double _paintMaskAngle;
    [ObservableProperty] private PaintBlendMode _paintBlendMode = PaintBlendMode.Normal;
    [ObservableProperty] private BrushMaskOption? _paintMask;
    [ObservableProperty] private string _paintStatus = "";
    [ObservableProperty] private string _paintHover = "";
    [ObservableProperty] private bool _paintHoverIsWarning;
    [ObservableProperty] private int _paintedTextureCount;

    /// <summary>Textures stacked over themselves badly enough that a stroke would show up in several
    /// unrelated places. Periph_Vista is the whole distant backdrop — 33.7% of the map's world area with
    /// its texture covered 5.04x over — so a single dab would appear five times across the horizon.</summary>
    private static readonly string[] PaintBlockedTextures = { "periph_vista" };

    /// <summary>M173: every blend mode, for the dropdown.</summary>
    public Array PaintBlendModes { get; } = Enum.GetValues<PaintBlendMode>();

    /// <summary>M173: the stencil library — "None" plus the generated built-ins, plus anything the user
    /// imports. Built-ins are procedural rather than bundled images: nothing to download, crisp at any
    /// size, and no third-party licence attached.</summary>
    public ObservableCollection<BrushMaskOption> BrushMasks { get; } = new(
        new[] { BrushMaskOption.None }.Concat(BrushMask.BuiltIn.Select(m => new BrushMaskOption(m))));

    /// <summary>Import any image as a brush stencil — PNG, TGA, DDS or a Riot .tex. Colour is folded to
    /// luminance and multiplied by alpha, so both a black-on-white stamp and an RGBA sprite work.</summary>
    [RelayCommand]
    private async Task LoadBrushMaskAsync()
    {
        var path = await Dialogs.OpenFileAsync("Load a brush mask",
            new Avalonia.Platform.Storage.FilePickerFileType("Image")
            { Patterns = new[] { "*.png", "*.tga", "*.dds", "*.tex", "*.jpg", "*.jpeg", "*.bmp" } });
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path);
            var img = await Task.Run(() => TextureDecoder.Decode(bytes));
            var mask = BrushMask.FromImage(Path.GetFileNameWithoutExtension(path), img);
            var option = new BrushMaskOption(mask);
            BrushMasks.Add(option);
            PaintMask = option;
            _log.Success("Paint", $"Brush mask '{mask.Name}' loaded ({mask.Width}x{mask.Height}).");
        }
        catch (Exception ex)
        {
            _log.Warn("Paint", $"Could not load that image as a mask: {ex.Message}");
            PaintStatus = "Could not load that image as a brush mask — see the console.";
        }
    }

    private MapPaintSession? _paintSession;
    private bool _paintStrokeActive;

    // Mouse moves arrive far faster than the screen refreshes — a gaming mouse can deliver 1000 events a
    // second, and each one used to run a full stroke step plus a GPU upload. Painting is coalesced to
    // roughly one step per frame instead; nothing is lost, because StrokeTo interpolates from wherever the
    // last dab landed, so a skipped event just becomes part of the next segment.
    private readonly System.Diagnostics.Stopwatch _paintClock = System.Diagnostics.Stopwatch.StartNew();
    private long _lastPaintMs;
    private const long PaintIntervalMs = 15;
    private (System.Numerics.Vector3 Origin, System.Numerics.Vector3 Dir)? _pendingPaintRay;
    private long _lastHoverMs;
    private const long HoverIntervalMs = 33;

    partial void OnIsPaintModeChanged(bool value)
    {
        if (!value)
        {
            _paintSession = null; PaintHover = "";
            ShowBrushRing?.Invoke(null, System.Numerics.Vector3.UnitY, 0f, 0f);
            return;
        }
        if (_currentMap is null)
        {
            IsPaintMode = false;
            _log.Warn("Paint", "Open a map (.mapgeo) first.");
            return;
        }
        PaintStatus = "Drag on the map to paint. Painting edits the texture, so every mesh that shares it changes too.";
    }

    /// <summary>Build (or reuse) the session bound to the current map and its live texture set.</summary>
    private MapPaintSession? EnsurePaintSession()
    {
        if (_currentMap is not { } map || RayIndex is not { } index) return null;
        if (CurrentModelTextures is not { } textures) return null;
        if (_paintSession is not null) return _paintSession;

        // Per-submesh texture path + material, alongside the per-submesh TextureImage the viewport already
        // uploaded. Painting through those instances is what makes a stroke appear without a reload.
        var paths = new string?[map.Groups.Count];
        var materials = new string[map.Groups.Count];
        for (int i = 0; i < map.Groups.Count; i++)
        {
            materials[i] = map.Groups[i].Material ?? "";
            paths[i] = _currentMaterialToTexture is { } m2t && m2t.TryGetValue(materials[i], out var p) ? p : null;
        }

        var blocked = paths.Where(p => p is not null)
            .Where(p => PaintBlockedTextures.Any(b => p!.Contains(b, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p!).Distinct().ToList();

        return _paintSession = new MapPaintSession(index, textures, paths, materials,
            CurrentModelSubmeshVisible, blocked);
    }

    /// <summary>Hover feedback: what would a stroke here change?</summary>
    public void PaintHoverAt(System.Numerics.Vector3 origin, System.Numerics.Vector3 dir)
    {
        if (!IsPaintMode || EnsurePaintSession() is not { } session)
        {
            PaintHover = "";
            ShowBrushRing?.Invoke(null, System.Numerics.Vector3.UnitY, 0f, 0f);
            return;
        }
        var d = System.Numerics.Vector3.Normalize(dir);

        // The ring follows the cursor every move — it is the cheap part (one BVH ray, ~1.3 us) and the
        // thing that has to feel instant. The badge text behind it is throttled, because updating a bound
        // string forces a layout pass and nobody reads it 500 times a second.
        var hit = session.Pick(origin, d);
        ShowBrushRing?.Invoke(hit?.Position, hit?.Normal ?? System.Numerics.Vector3.UnitY,
            (float)PaintRadius, (float)PaintHardness);

        long now = _paintClock.ElapsedMilliseconds;
        if (now - _lastHoverMs < HoverIntervalMs) return;
        _lastHoverMs = now;

        if (hit is null || session.Probe(origin, d) is not { } probe)
        {
            if (PaintHover.Length > 0) { PaintHover = ""; PaintHoverIsWarning = false; }
            return;
        }
        PaintHoverIsWarning = probe.Warning is not null;
        var text = $"{Path.GetFileName(probe.AssetPath)}  {probe.Width}x{probe.Height}"
                   + (probe.Warning is { } w ? "  —  " + w : "");
        if (text != PaintHover) PaintHover = text;
    }

    public void BeginPaintStroke(System.Numerics.Vector3 origin, System.Numerics.Vector3 dir)
    {
        if (!IsPaintMode || EnsurePaintSession() is not { } session) return;
        session.BeginStroke();
        _paintStrokeActive = true;
        _lastPaintMs = 0;                       // the first dab of a stroke always lands immediately
        _pendingPaintRay = null;
        PaintStrokeMove(origin, dir);
    }

    public void PaintStrokeMove(System.Numerics.Vector3 origin, System.Numerics.Vector3 dir)
    {
        if (!_paintStrokeActive) return;
        _pendingPaintRay = (origin, dir);
        long now = _paintClock.ElapsedMilliseconds;
        if (now - _lastPaintMs < PaintIntervalMs) return;   // coalesced — see _paintClock
        _lastPaintMs = now;
        FlushPaint();
    }

    private void FlushPaint()
    {
        if (_pendingPaintRay is not { } ray || _paintSession is not { } session) return;
        _pendingPaintRay = null;
        var (origin, dir) = ray;
        var d = System.Numerics.Vector3.Normalize(dir);
        if (session.Pick(origin, d) is not { } hit) return;
        ShowBrushRing?.Invoke(hit.Position, hit.Normal, (float)PaintRadius, (float)PaintHardness);

        var brush = CurrentBrush;

        foreach (var painted in session.StrokeTo(hit.Position, d, brush))
        {
            session.MarkPainted(painted.Image, painted.AssetPath);
            PushTextureRegion?.Invoke(painted.Image, new Avalonia.PixelRect(
                painted.Rect.MinX, painted.Rect.MinY, painted.Rect.Width, painted.Rect.Height));
        }
    }

    /// <summary>The brush the next dab will use, assembled from the palette.</summary>
    private ReyEngine.Core.Painting.PaintBrush CurrentBrush => new()
    {
        Color = new System.Numerics.Vector3(PaintColor.R / 255f, PaintColor.G / 255f, PaintColor.B / 255f),
        Radius = (float)PaintRadius,
        Hardness = (float)PaintHardness,
        Opacity = (float)PaintOpacity,
        SeamBleedTexels = (float)PaintSeamBleed,
        BlendMode = PaintBlendMode,
        Mask = PaintMask?.Mask,
        MaskAngle = (float)(PaintMaskAngle * Math.PI / 180.0),
    };

    public void EndPaintStroke()
    {
        if (!_paintStrokeActive || _paintSession is not { } session) return;
        FlushPaint();                 // the last mouse position may have been coalesced away
        _paintStrokeActive = false;
        RebuildTextureMips?.Invoke(); // mips are throttled mid-stroke; the finished result must be right
        if (session.EndStroke() is not { } record) return;

        UndoService.PushApplied(new PaintStrokeCommand(record, _currentMap!, RepaintAfterUndo));
        PaintedTextureCount = session.PaintedTextures.Count;
        HasUnsavedPaint = PaintedTextureCount > 0;
        PaintStatus = $"{PaintedTextureCount} texture(s) painted — not saved yet.";
    }

    /// <summary>Undo/redo changed texels behind the viewport's back; push the affected rectangles.</summary>
    private void RepaintAfterUndo(PaintStrokeRecord record)
    {
        foreach (var e in record.Entries)
            PushTextureRegion?.Invoke(e.Image, new Avalonia.PixelRect(
                e.Rect.MinX, e.Rect.MinY, e.Rect.Width, e.Rect.Height));
    }

    [ObservableProperty] private bool _hasUnsavedPaint;

    /// <summary>M172d: write every painted texture back as a real .tex.
    ///
    /// Encoding happens ONCE, here — never per stroke. The painted master is uncompressed RGBA the whole
    /// time it is being edited, so a session of hundreds of strokes still costs a single BC generation.
    /// The source pixel format is preserved: 160 of base_srx's 169 diffuse textures are BC1, and letting
    /// them default to BC3 would exactly double each one (2,796,228 -> 5,592,444 bytes).</summary>
    [RelayCommand]
    private async Task SavePaintedTextures()
    {
        if (_paintSession is not { } session) return;
        var painted = session.PaintedTextures;
        if (painted.Count == 0) { PaintStatus = "Nothing painted yet."; return; }
        if (!Project.IsFolderProject && Project.OverridesDirectory is null)
        {
            PaintStatus = "This project has nowhere to write to — save the project first.";
            return;
        }

        PaintStatus = $"Encoding {painted.Count} texture(s)…";
        int written = 0, failed = 0;
        long bytes = 0;
        var notes = new List<string>();

        await Task.Run(() =>
        {
            foreach (var (image, path) in painted)
            {
                try
                {
                    // Read the ORIGINAL only to learn its container shape — format and whether it had
                    // mips. The pixels come from the painted master, not from a re-decode.
                    var original = ReadRecolorBase(new RecolorTarget(HashAlgorithms.WadPath(path), path))
                                   ?? TryReadAssetBytes(HashAlgorithms.WadPath(path));
                    var format = original is not null ? TexWriter.DetectFormat(original) : null;
                    if (format is null)
                    {
                        failed++;
                        notes.Add($"{Path.GetFileName(path)}: could not read its original format — skipped rather than guessing.");
                        continue;
                    }
                    bool mips = original is not null && TextureRecolor.HasMips(original);
                    var texBytes = TexWriter.Write(image, format.Value, mips);
                    WriteRecoloredAsset(path, texBytes, ".tex");
                    written++;
                    bytes += texBytes.Length;
                }
                catch (Exception ex) { failed++; notes.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
            }
        });

        foreach (var n in notes) _log.Warn("Paint", n);
        _log.Success("Paint", $"{written:n0} painted texture(s) written ({bytes / 1048576.0:F1} MB)"
                              + (failed > 0 ? $", {failed} failed" : "") + ".");
        PaintStatus = $"Saved {written:n0} texture(s) ({bytes / 1048576.0:F1} MB)"
                      + (failed > 0 ? $", {failed} failed — see the console" : "") + ".";
        HasUnsavedPaint = failed > 0 && written == 0;

        Project.IsDirty = true;
        if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
        // Re-index so the project tree and the packer see the new files. Deliberately NOT reloading the
        // map: the viewport already shows the painted pixels, and a reload would throw away the session.
        if (Project.IsFolderProject) { BuildMounts(); BuildProjectTree(); }
        UpdateTitle();
    }

    public Action? ShowTextureRecolorWindow { get; set; }

    [RelayCommand]
    private void OpenTextureRecolor()
    {
        if (_currentMap is null) { _log.Warn("Recolor", "Open a map (.mapgeo) first — the tool recolours its surfaces, placed mobs / props and lightmaps."); return; }
        ShowTextureRecolorWindow?.Invoke();
    }

    /// <summary>
    /// <para>Every colour-bearing texture used by the open map, its placed mobs / animated props and its
    /// baked LIGHTMAP atlases, ranked by how many scene references each one has. Normal maps, masks and
    /// gradients stay excluded: those are data rather than colour, and hue-shifting them would corrupt
    /// the lighting instead of recolouring the map. Prop textures are resolved from the same placed skin
    /// bins and champion material bindings as the viewport, so Baron, dragons, camps and shopkeepers do
    /// not silently keep their original diffuse colour.</para>
    ///
    /// <para>Recolouring one tints the map's baked lighting rather than its surfaces — warming or
    /// cooling shadowed areas, for instance — which is a genuinely different effect from recolouring a
    /// diffuse texture and is why lightmap rows carry their own badge in the picker rather than being
    /// silently mixed in.</para>
    /// </summary>
    public IReadOnlyList<RecolorTargetViewModel> GatherRecolorTargets()
    {
        if (_currentMap is not { } map || _currentMapEntry is not { } mapEntry)
            return Array.Empty<RecolorTargetViewModel>();

        var recolored = Project.TextureRecolors.Select(r => r.PathHash).ToHashSet();
        var candidates = new Dictionary<ulong, RecolorCandidate>();

        void Add(string? path, int mapUses = 0, int propUses = 0, int lightmapUses = 0)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string clean = path.Replace('\\', '/').Trim();
            ulong hash = HashAlgorithms.WadPath(clean);
            if (!candidates.TryGetValue(hash, out var candidate))
                candidates[hash] = candidate = new RecolorCandidate(clean);
            candidate.MapUses += mapUses;
            candidate.PropUses += propUses;
            candidate.LightmapUses += lightmapUses;
        }

        // ---- diffuse: every texture a material actually samples ----
        // Guarded rather than an early return for the whole method — a map whose materials.bin fails to
        // resolve should still offer its lightmaps rather than nothing at all.
        if (TryResolveMaterialsBin(mapEntry.Path, out var binEntry))
        {
            var names = map.Groups.Select(g => g.Material).Where(m => m.Length > 0).Distinct().ToList();
            var (materialToTexture, _, _) = ResolveMapMaterials(binEntry, names);
            var byPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var tex in materialToTexture.Values)
                if (!string.IsNullOrEmpty(tex)) byPath[tex] = byPath.GetValueOrDefault(tex) + 1;

            foreach (var (path, uses) in byPath) Add(path, mapUses: uses);
        }

        // ---- mobs / animated props: diffuse textures from every placed character skin ----
        // Resolve each unique skin bin once. PropTextureCatalog also includes SkinMeshProperties.texture,
        // the fallback used by submeshes without a material override, and aggregates repeated placements.
        if (CurrentModelProps is { Count: > 0 } props)
        {
            var propTextures = PropTextureCatalog.Discover(
                props.GroupBy(prop => prop.Skin, StringComparer.OrdinalIgnoreCase)
                    .Select(group => new PropSkinUsage(group.Key, group.Count())),
                skin => ReadAssetByPath($"data/{skin.ToLowerInvariant()}.bin"),
                ResolveBinName);
            foreach (var texture in propTextures) Add(texture.AssetPath, propUses: texture.Placements);
        }

        // ---- lightmaps: every baked-light atlas the map references ----
        // The same enumeration LightBaker uses to decide what to re-bake, reused here so "every atlas
        // this map has" cannot drift into two different answers depending on which tool you opened.
        foreach (var atlas in Formats.Baking.LightBaker.EnumerateAtlases(map))
        {
            int uses = map.Groups.Count(g => string.Equals(g.LightmapTexture, atlas, StringComparison.OrdinalIgnoreCase));
            Add(atlas, lightmapUses: uses);
        }

        // Header triage happens once after all discovery passes. A texture shared by map geometry and a
        // prop becomes one row (MAP+PROP), one write and one project record rather than two competing rows.
        var list = new List<RecolorTargetViewModel>(candidates.Count);
        foreach (var (hash, candidate) in candidates)
        {
            var bytes = TryReadAssetBytes(hash);
            if (bytes is null || !TextureRecolor.IsSupported(bytes)) continue;
            var kind = candidate.LightmapUses > 0
                ? RecolorTargetKind.Lightmap
                : candidate.MapUses > 0 && candidate.PropUses > 0
                    ? RecolorTargetKind.MapAndPropDiffuse
                    : candidate.PropUses > 0 ? RecolorTargetKind.PropDiffuse : RecolorTargetKind.Diffuse;
            list.Add(new RecolorTargetViewModel
            {
                Target = new RecolorTarget(hash, candidate.Path),
                Name = Path.GetFileName(candidate.Path),
                Folder = Path.GetDirectoryName(candidate.Path)?.Replace('\\', '/') ?? "",
                Kind = kind,
                MapUses = candidate.MapUses,
                PropUses = candidate.PropUses,
                LightmapUses = candidate.LightmapUses,
                IsRecolored = recolored.Contains(hash),
            });
        }

        return list.OrderBy(t => t.Kind)
                   .ThenByDescending(t => t.UsedBy)
                   .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                   .ToList();
    }

    private sealed class RecolorCandidate(string path)
    {
        public string Path { get; } = path;
        public int MapUses { get; set; }
        public int PropUses { get; set; }
        public int LightmapUses { get; set; }
    }

    private byte[]? TryReadAssetBytes(ulong hash)
    {
        try { return ReadAsset(hash); } catch { return null; }
    }

    /// <summary>The PRISTINE bytes to recolour from — Riot's original, never the project's own recoloured
    /// copy. This is the whole reason the tool is safe to re-run: BC compression is lossy, so recolouring
    /// an already-recoloured texture would add a generation of loss on every pass.
    ///
    /// Riot's reference WAD is the normal source and costs nothing. When a project has no reference
    /// mounted (so <see cref="AssetMountService.ReadFallback"/> has nothing to give) the first recolour
    /// stashed a snapshot instead; that snapshot is used from then on.</summary>
    public byte[]? ReadRecolorBase(RecolorTarget target)
    {
        var record = Project.TextureRecolors.FirstOrDefault(r => r.PathHash == target.PathHash);
        if (record?.BaseSnapshot is { } snap && ResolveSnapshotPath(snap) is { } snapPath && File.Exists(snapPath))
            return File.ReadAllBytes(snapPath);

        if (_mounts?.ReadFallback(target.PathHash) is { } riot) return riot;

        // No reference and no snapshot: the project's own copy is the closest thing to an original we
        // have. Valid as a base only until we recolour over it — see CheckOutRecolorBase.
        return record is null ? TryReadAssetBytes(target.PathHash) : null;
    }

    /// <summary>What the RUN reads from — the same bytes as <see cref="ReadRecolorBase"/>, except that in
    /// the no-Riot-reference case it also stashes them before handing them over.
    ///
    /// That has to happen HERE and not after the run: once the recoloured file is written, the project's
    /// copy is no longer an original, so a snapshot taken afterwards would preserve the edit instead of
    /// the source and every later re-tune would compound BC loss.</summary>
    private byte[]? CheckOutRecolorBase(RecolorTarget target)
    {
        var bytes = ReadRecolorBase(target);
        if (bytes is null) return null;

        bool needsSnapshot = _mounts?.ReadFallback(target.PathHash) is null
                             && !Project.TextureRecolors.Any(r => r.PathHash == target.PathHash);
        if (needsSnapshot) _pendingSnapshots[target.PathHash] = SnapshotOriginal(target, bytes);
        return bytes;
    }

    /// <summary>Snapshots taken during the current run, keyed by hash — folded into the project records
    /// by <see cref="PersistRecolors"/> once the run succeeds.</summary>
    private readonly Dictionary<ulong, string?> _pendingSnapshots = new();

    private string? ResolveSnapshotPath(string relative) =>
        Project.WorkspaceDirectory is { } ws ? Path.Combine(ws, relative) : null;

    public Services.TextureRecolorService? MakeRecolorService()
    {
        bool canWrite = (Project.IsFolderProject && Project.RootPath is not null)
                        || Project.OverridesDirectory is not null;
        return canWrite ? new Services.TextureRecolorService(CheckOutRecolorBase, WriteRecoloredAsset) : null;
    }

    /// <summary>Actionable source-health message shown by the recolour tool before a large run.</summary>
    public string? GetRecolorSourceWarning()
    {
        var status = GameReferenceLibrary.Inspect(Project.GameDirectory);
        string? problem = status.IsValid ? null : status.Message;
        if (status.IsValid && status.FinalDirectory is { } final
            && _currentMapEntry is { } entry
            && MapNameFromAssetPath(entry.Path) is { } mapName)
        {
            string mapWad = Path.Combine(final, "Maps", "Shipping", mapName + ".wad.client");
            if (!File.Exists(mapWad))
                problem = $"The configured League folder does not contain the required {mapName}.wad.client.";
        }
        if (problem is null) return null;
        return problem + " Recolouring may fail for textures that only exist in Riot's WADs."
               + Environment.NewLine + "1. Open Project > Set Game Folder...."
               + Environment.NewLine + "2. Select the League of Legends\\Game folder that contains DATA\\FINAL."
               + Environment.NewLine + "3. Reopen the map, press Refresh here, and retry.";
    }

    /// <summary>Write a recoloured texture where it belongs. Unlike a baked lightmap — a brand-new file
    /// with no home of its own — a recoloured texture already exists in a Riot WAD, so it is staged under
    /// THAT wad's folder and replaces the chunk the game actually reads.</summary>
    private string WriteRecoloredAsset(string assetPath, byte[] bytes, string ext)
    {
        ulong hash = HashAlgorithms.WadPath(assetPath);
        if (Project.IsFolderProject && Project.RootPath is { } root)
        {
            string folderName = RiotWadFolderNameForHash(hash);
            string dest = Path.Combine(root, folderName, assetPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, bytes);
            if (!Project.ProjectFolders.Contains(folderName, StringComparer.OrdinalIgnoreCase))
                Project.ProjectFolders.Add(folderName);
            ClearShadowOverride(hash, ext);   // a stale hashed override would outrank the folder file
            return dest;
        }

        var overrideFile = ProjectWorkspace.StoreOverrideBytes(Project, hash, bytes, ext);
        _overrides.Set(new ProjectAssetOverride
        {
            PathHash = hash,
            ResolvedPath = assetPath,
            OverrideFile = overrideFile,
            AddedUtc = DateTime.UtcNow.ToString("o"),
        });
        return overrideFile;
    }

    /// <summary>Remember the sliders (not the pixels) for each recoloured texture, so re-opening the tool
    /// shows what was done and a later edit re-derives from the original instead of stacking on top.</summary>
    public void PersistRecolors(TextureAdjustment adjustment, IReadOnlyList<RecolorTarget> targets)
    {
        foreach (var t in targets)
        {
            var record = Project.TextureRecolors.FirstOrDefault(r => r.PathHash == t.PathHash);
            if (record is null)
            {
                record = new TextureRecolorRecord { PathHash = t.PathHash, AssetPath = t.AssetPath };
                // Only set when the run actually had to keep its own copy (no Riot reference mounted);
                // in the normal case this stays null and the project stays small.
                record.BaseSnapshot = _pendingSnapshots.GetValueOrDefault(t.PathHash);
                Project.TextureRecolors.Add(record);
            }
            record.AssetPath = t.AssetPath;
            record.HueDegrees = adjustment.HueDegrees;
            record.Saturation = adjustment.Saturation;
            record.Brightness = adjustment.Brightness;
            record.Contrast = adjustment.Contrast;
            record.InputBlack = adjustment.InputBlack;
            record.InputWhite = adjustment.InputWhite;
            record.Gamma = adjustment.Gamma;
            record.TintR = adjustment.TintR;
            record.TintG = adjustment.TintG;
            record.TintB = adjustment.TintB;
            record.Strength = adjustment.Strength;
        }
        _pendingSnapshots.Clear();
    }

    /// <summary>Stash the untouched original under the workspace, returning its workspace-relative path.
    /// Only used when nothing else can supply a pristine base.</summary>
    private string? SnapshotOriginal(RecolorTarget target, byte[] bytes)
    {
        try
        {
            if (Project.WorkspaceDirectory is not { } ws) return null;
            string rel = Path.Combine("recolor-base", $"{target.PathHash:x16}.tex");
            string full = Path.Combine(ws, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
            return rel;
        }
        catch (Exception ex)
        {
            _log.Warn("Recolor", $"Could not snapshot the original of {target.AssetPath} ({ex.Message}) — re-editing it will re-compress.");
            return null;
        }
    }

    /// <summary>Undo recolours: delete the project's copies so Riot's originals win again, and forget the
    /// saved sliders. Returns how many were restored.</summary>
    public int RevertRecolors(IReadOnlyList<RecolorTarget> targets)
    {
        int n = 0;
        foreach (var t in targets)
        {
            var record = Project.TextureRecolors.FirstOrDefault(r => r.PathHash == t.PathHash);
            try
            {
                if (Project.IsFolderProject && Project.RootPath is { } root)
                {
                    string dest = Path.Combine(root, RiotWadFolderNameForHash(t.PathHash),
                        t.AssetPath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(dest)) { File.Delete(dest); n++; }
                }
                ClearShadowOverride(t.PathHash, ".tex");
                if (record?.BaseSnapshot is { } snap && ResolveSnapshotPath(snap) is { } p && File.Exists(p))
                    File.Delete(p);
            }
            catch (Exception ex) { _log.Warn("Recolor", $"Could not restore {t.AssetPath}: {ex.Message}"); }
            if (record is not null) Project.TextureRecolors.Remove(record);
        }
        OnRecolorFinished(null);
        return n;
    }

    public void OnRecolorFinished(Services.RecolorRunResult? result)
    {
        if (result is not null)
        {
            string summary = $"{result.Written:n0} texture(s) recoloured"
                + (result.Skipped > 0 ? $", {result.Skipped:n0} skipped" : "")
                + (result.Failed > 0 ? $", {result.Failed:n0} failed" : "")
                + $" ({result.BytesWritten / 1048576.0:F1} MB).";
            if (result.Failed > 0) _log.Error("Recolor", summary);
            else _log.Success("Recolor", summary);
            if (result.MissingSources > 0)
                _log.Error("Recolor",
                    $"{result.MissingSources:n0} original texture source(s) could not be read. "
                    + "Fix: Project > Set Game Folder..., select the League of Legends\\Game folder containing DATA\\FINAL, "
                    + "then reopen the map, refresh Recolor Textures, and retry.");
            foreach (var note in result.Notes) _log.Warn("Recolor", note);
        }

        if (result is not null && result.Written == 0) return;
        Project.IsDirty = true;
        if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
        if (Project.IsFolderProject) { BuildMounts(); BuildProjectTree(); }
        UpdateTitle();
        // Re-read the map so the viewport paints with the new textures rather than the ones it cached.
        if (_currentMapEntry is { } e) _ = LoadMapGeoAsync(e);
    }
    [ObservableProperty] private bool _showLightMarkers = true;   // M71: show a glow icon at each light position
    // M71: manual lighting controls. Sun + sky feed the fallback lighting term (visible with lightmaps off or
    // on geometry without baked light); lightmap brightness scales the baked atlas. All initialise from the
    // map's MapSunProperties on load, then the user tweaks — darken the sky/lightmap so dynamic lights pop.
    [ObservableProperty] private double _sunIntensity = 1.0;
    [ObservableProperty] private double _sunColorR = 0.75;
    [ObservableProperty] private double _sunColorG = 0.75;
    [ObservableProperty] private double _sunColorB = 0.75;
    [ObservableProperty] private double _skyIntensity = 1.0;
    [ObservableProperty] private double _skyColorR = 0.35;
    [ObservableProperty] private double _skyColorG = 0.35;
    [ObservableProperty] private double _skyColorB = 0.35;
    [ObservableProperty] private bool _hasMaterialData;
    [ObservableProperty] private bool _hasInspectorBody;
    [ObservableProperty] private int _inspectorTab;
    [ObservableProperty] private int _previewMode; // 0 Basic · 1 RiotApprox · 2 Debug base · 3 Debug alpha · 4 Debug normal
    [ObservableProperty] private string _shaderDbStatus = "Riot shaders not scanned.";
    /// <summary>
    /// <para>M268: bumped whenever the open map is replaced or cleared. _currentMap is a plain field, so
    /// nothing observable fired on a map load - which is why the D3D11 viewport only ever built its scene
    /// on the toggle, and opening a different map while it was on left the PREVIOUS map on screen.</para>
    ///
    /// <para>A counter rather than exposing the map itself: the view needs to know THAT it changed, not
    /// what it changed to, and a counter cannot be accidentally held alive by a binding.</para>
    /// </summary>
    [ObservableProperty] private int _mapGeneration;

    /// <summary>
    /// <para>M269: the current selection as INDEX RANGES, for the D3D11 overlay.</para>
    ///
    /// <para>Ranges rather than material or slice indices, and that is not a style choice.
    /// Dx11SceneBuilder.MergeSlices sorts the map's groups by start index and merges adjacent ones, so the
    /// Nth D3D11 material is not the Nth mapgeo group. Handing the renderer material indices would
    /// highlight confidently and highlight the wrong mesh. A group's (StartIndex, IndexCount) is what
    /// mapgeo actually stores and survives the merge untouched.</para>
    /// </summary>
    /// <summary>
    /// <para>M270: the placement markers for the D3D11 viewport, colour-coded by type.</para>
    ///
    /// <para>Reads the same placement marker lists the GL viewport is bound to and applies the shared
    /// fitted-position formula to editable point lights.</para>
    ///
    /// <para>Size scales with camera distance because these mark a POSITION, not an object with a size -
    /// a fixed world size vanishes when you pull back over a 97,000-unit map and swallows the screen when
    /// you fly in.</para>
    /// </summary>
    private IReadOnlyList<System.Numerics.Vector3>? _dx11IconParticles, _dx11IconSounds, _dx11IconProps, _dx11IconProbes;
    private IReadOnlyList<PointLight>? _dx11IconLights;
    private float _dx11IconSize, _dx11IconSpread, _dx11IconScaleX, _dx11IconScaleZ, _dx11IconOffsetX, _dx11IconOffsetZ;
    private bool _dx11IconShowLights;
    private IReadOnlyList<(System.Numerics.Vector3 Pos, System.Numerics.Vector4 Color, float Size,
        ReyEngine.Rendering.D3D11.IconGlyph Glyph)> _dx11IconCache =
        Array.Empty<(System.Numerics.Vector3, System.Numerics.Vector4, float, ReyEngine.Rendering.D3D11.IconGlyph)>();

    public IReadOnlyList<(System.Numerics.Vector3 Pos, System.Numerics.Vector4 Color, float Size,
        ReyEngine.Rendering.D3D11.IconGlyph Glyph)> Dx11Icons(float cameraDistance)
    {
        float size = Math.Clamp(cameraDistance * 0.012f, 12f, 320f);
        float spread = (float)DynamicLightPositionScale;
        float scaleX = (float)DynamicLightScaleX, scaleZ = (float)DynamicLightScaleZ;
        float offsetX = (float)DynamicLightOffsetX, offsetZ = (float)DynamicLightOffsetZ;
        if (ReferenceEquals(_dx11IconParticles, ParticleMarkers)
            && ReferenceEquals(_dx11IconSounds, SoundMarkers)
            && ReferenceEquals(_dx11IconProps, PropMarkers)
            && ReferenceEquals(_dx11IconProbes, ProbeMarkers)
            && ReferenceEquals(_dx11IconLights, DynamicLights)
            && _dx11IconSize == size && _dx11IconShowLights == ShowLightMarkers
            && _dx11IconSpread == spread && _dx11IconScaleX == scaleX && _dx11IconScaleZ == scaleZ
            && _dx11IconOffsetX == offsetX && _dx11IconOffsetZ == offsetZ)
            return _dx11IconCache;

        _dx11IconParticles = ParticleMarkers; _dx11IconSounds = SoundMarkers;
        _dx11IconProps = PropMarkers; _dx11IconProbes = ProbeMarkers; _dx11IconLights = DynamicLights;
        _dx11IconSize = size; _dx11IconShowLights = ShowLightMarkers; _dx11IconSpread = spread;
        _dx11IconScaleX = scaleX; _dx11IconScaleZ = scaleZ; _dx11IconOffsetX = offsetX; _dx11IconOffsetZ = offsetZ;

        var outp = new List<(System.Numerics.Vector3, System.Numerics.Vector4, float,
            ReyEngine.Rendering.D3D11.IconGlyph)>();
        void Add(IReadOnlyList<System.Numerics.Vector3>? pts, System.Numerics.Vector4 colour,
                 ReyEngine.Rendering.D3D11.IconGlyph glyph)
        {
            if (pts is null) return;
            foreach (var p in pts) outp.Add((p, colour, size, glyph));
        }
        Add(ParticleMarkers, new System.Numerics.Vector4(1.00f, 0.42f, 0.78f, 0.85f),
            ReyEngine.Rendering.D3D11.IconGlyph.Particle);
        Add(SoundMarkers, new System.Numerics.Vector4(0.35f, 0.80f, 1.00f, 0.85f),
            ReyEngine.Rendering.D3D11.IconGlyph.Sound);
        Add(PropMarkers, new System.Numerics.Vector4(1.00f, 0.80f, 0.25f, 0.85f),
            ReyEngine.Rendering.D3D11.IconGlyph.Prop);
        Add(ProbeMarkers, new System.Numerics.Vector4(0.55f, 1.00f, 0.45f, 0.85f),
            ReyEngine.Rendering.D3D11.IconGlyph.Probe);
        if (ShowLightMarkers && DynamicLights is { Count: > 0 } lights)
        {
            var scaleXZ = new System.Numerics.Vector2(scaleX, scaleZ);
            var offset = new System.Numerics.Vector2(offsetX, offsetZ);
            foreach (var light in lights)
                outp.Add((Formats.Baking.BakeLighting.FitPosition(light.Position, spread, scaleXZ, offset),
                    new System.Numerics.Vector4(1.00f, 0.62f, 0.20f, 0.90f), size * 1.2f,
                    ReyEngine.Rendering.D3D11.IconGlyph.Light));
        }
        return _dx11IconCache = outp;
    }

    private IReadOnlyList<int>? _dx11HighlightSelection;
    private MapGeoAsset? _dx11HighlightMap;
    private IReadOnlyList<(int Start, int Count)> _dx11HighlightCache = Array.Empty<(int, int)>();

    public IReadOnlyList<(int Start, int Count)> Dx11HighlightRanges
    {
        get
        {
            if (SelectedSubmeshIndices is not { Count: > 0 } sel || _currentMap is not { } map)
                return Array.Empty<(int, int)>();
            if (ReferenceEquals(_dx11HighlightSelection, sel) && ReferenceEquals(_dx11HighlightMap, map))
                return _dx11HighlightCache;
            var ranges = new List<(int, int)>(sel.Count);
            foreach (int i in sel)
                if (i >= 0 && i < map.Groups.Count)
                    ranges.Add((map.Groups[i].StartIndex, map.Groups[i].IndexCount));
            _dx11HighlightSelection = sel; _dx11HighlightMap = map;
            return _dx11HighlightCache = ranges;
        }
    }

    private MapGeoAsset? _currentMap;
    private IReadOnlyDictionary<string, MaterialProfile>? _currentMapProfiles;
    private Dictionary<string, string>? _currentMaterialToTexture;   // M172c: material name -> diffuse .tex path // M34: material name → render-state profile
    // Map-only secondary layers. Flow water uses mask/gradient; terrain shader 0xe25b830f additionally reuses
    // emissive/matcap as top/extras. Keep them across ClearSecondaryTextures() just like baked lightmaps.
    private IReadOnlyList<TextureImage?>? _mapFlowMasks;
    private IReadOnlyList<TextureImage?>? _mapFlowGrads;
    private IReadOnlyList<TextureImage?>? _mapTerrainTops;
    private IReadOnlyList<TextureImage?>? _mapTerrainExtras;

    /// <summary>Republish map-only special material layers after ClearSecondaryTextures().</summary>
    private void PublishMapMaterialLayers()
    {
        CurrentModelMaskTextures = _mapFlowMasks;
        CurrentModelGradientTextures = _mapFlowGrads;
        CurrentModelEmissiveTextures = _mapTerrainTops;
        CurrentModelMatCapTextures = _mapTerrainExtras;
        HasFlowmapWater = CurrentModelSubmeshMaterials?.Any(m => m.IsFlowmap) == true;
    }
    private MapVisibilityControllers? _mapControllers;
    private MapVisibilityResolver? _visibilityResolver;
    private MapVisibilityDefinition _mapVisibility = MapVisibilityDefinition.Empty;
    private ShaderDatabase? _shaderDb;

    public MainWindowViewModel()
    {
        _log.AddSink(Console);
        _cullBackfaces = Settings.CullBackfacesDefault;   // M40: honor saved viewport default
        Project.GameDirectory = ReyProject.GuessGameDirectory();
        _log.Info("ReyEngine", "Editor started.");
        if (!string.IsNullOrEmpty(Project.GameDirectory))
            _log.Info("Project", $"Game directory: {Project.GameDirectory}");

        var db = _sync.LoadLocal(m => _log.Info("Hashes", m));
        _resolver = new WadPathResolver(db);
        if (db.WadCount + db.BinCount == 0)
            _log.Warn("Hashes", "No hash dictionary yet. Use Tools ▸ Sync Hashes to download from CommunityDragon.");

        Animation.ClipLoader = DecodeAnimation;
        Animation.ClipChanged = clip => CurrentAnimation = clip;
        Animation.TimeChanged = t => AnimationTime = t;

        UndoService.Changed += () =>
        {
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            OnPropertyChanged(nameof(UndoLabel));
            OnPropertyChanged(nameof(RedoLabel));
            UpdateTitle();
        };
        UndoService.Error += msg => _log.Warn("Undo", msg);

        _selection.Changed += OnMeshSelectionChanged;

        BinEditor.CopyHandler = Dialogs.CopyAsync;
        BinEditor.UndoService = UndoService;

        MaterialEditor.UndoService = UndoService;
        MaterialEditor.CopyHandler = Dialogs.CopyAsync;
        MaterialEditor.TextureExists = TextureExistsByPath;
        MaterialEditor.LoadThumbnail = LoadThumbnailByPath;
        MaterialEditor.OpenTexture = OpenTextureByPath;
        MaterialEditor.ReplaceTextureAsset = ReplaceTextureForSlot;
        MaterialEditor.ApplyToViewport = ApplyMaterialToViewport;
        MaterialEditor.SaveOverride = SaveMaterialOverride;
        MaterialEditor.RequestCatalog = LoadShaderCatalogAsync;   // M103
        InitShaderEnvironments();

        // M46 Particle Editor wiring
        ParticleEditor.ResolveTextures = ResolveSystemTextures;
        ParticleEditor.ResolveMultTextures = ResolveSystemMultTextures;
        ParticleEditor.ResolveDistortionTextures = ResolveSystemDistortionTextures;
        ParticleEditor.ResolveColorTextures = ResolveSystemColorTextures;   // M68: particleColorTexture gradient
        // M175: erosion and palette. Erosion shipped in M174 but was only ever handed to the champion-VFX
        // path, so the Particle Editor - the one surface built for looking at VFX - never applied it.
        ParticleEditor.ResolveErosionTextures = ResolveSystemErosionTextures;
        ParticleEditor.ResolvePaletteTextures = ResolveSystemPaletteTextures;
        ParticleEditor.ResolveReflectionCubemaps = ResolveSystemReflectionCubemaps;   // M181 (2.12)
        ParticleEditor.ResolveMeshes = ResolveSystemMeshes;   // M47: .scb/.sco mesh primitives

        // M55: model-preview window — its own animation clock (AnimationInspectorViewModel) + VFX resolvers
        MeshPreview.Animation.ClipLoader = DecodeAnimation;
        MeshPreview.LoadDummyMesh = () => Services.TargetDummyLoader.Get(Project.GameDirectory, _resolver,
            m => _log.Warn("Preview", m));   // M115: Riot's practice dummy from Map11.wad
        MeshPreview.LoadSkybox = LoadSkyboxAtAsync;   // M122: same catalogue, its own pick
        MeshPreview.ResolveTextures = ResolveSystemTextures;
        MeshPreview.ResolveDistortionTextures = ResolveSystemDistortionTextures;
        MeshPreview.ResolveColorTextures = ResolveSystemColorTextures;   // M68
        MeshPreview.ResolveErosionTextures = ResolveSystemErosionTextures;   // M175 (see above)
        MeshPreview.ResolvePaletteTextures = ResolveSystemPaletteTextures;
        MeshPreview.ResolveReflectionCubemaps = ResolveSystemReflectionCubemaps;   // M181 (2.12)
        MeshPreview.ResolveMeshes = ResolveSystemMeshes;
        MeshPreview.PlaySoundEvent = PlayPreviewSoundEvent;              // M90: clip SFX
        MeshPreview.StopSounds = () => Sound.StopTag("previewsfx");

        // M98: Map Bin Editor window
        MapBinEditor.Resolve = ResolveBinName;
        MapBinEditor.Info = m => _log.Info("MapBin", m);
        MapBinEditor.Warn = m => _log.Warn("MapBin", m);
        MapBinEditor.PickOldOriginal = () => Dialogs.OpenFileAsync(
            "Pick the OLD original .bin (from the patch your mod was made for)",
            new Avalonia.Platform.Storage.FilePickerFileType("League .bin") { Patterns = new[] { "*.bin" } },
            DialogService.All);
        MapBinEditor.ReadRiotOriginal = ReadRiotOriginalBytes;
        MapBinEditor.SaveBytes = SaveMapBinBytesAsync;
        ParticleEditor.ResolveBinName = ResolveBinName;   // M187 (3.1): field names instead of raw hashes
        ParticleEditor.Info = m => _log.Info("Particle", m);
        ParticleEditor.Error = m => _log.Error("Particle", m);
        ParticleEditor.MarkDocumentDirty = () => { }; // window has its own dirty state via Document.IsDirty
        ParticleEditor.LoadThumbnail = LoadThumbnailByPath;
        ParticleEditor.SaveOverrideAsync = SaveParticleOverride;
        ParticleEditor.OpenIssues = OpenParticleBinIssues;   // M125
        MaterialEditor.OpenIssues = OpenMaterialBinIssues;   // M125

        // M138: the wem encoder reuses vgmstream for input formats Media Foundation can't read
        Encoder.VgmstreamPath = Sound.VgmstreamPath;
        Encoder.ConsolePathSetting = Settings.WwiseConsolePath;
        Encoder.ProjectPathSetting = Settings.WwiseProjectPath;

        ContentBrowser.FileSelected = OpenAssetDocument;
        ContentBrowser.CanImportInto = f => TryComputeFolderDiskDir(f, out _);   // M107/M113: virtual folders materialize on write
        ContentBrowser.SelectionStateChanged = RaiseAssetCommandsCanExecute;          // M108
        ContentBrowser.ExtractMaterials = ExtractMaterialsForNode;
        ContentBrowser.MaterialSelected = OpenMaterialAsset;
        _thumbnails = new ThumbnailService(p =>
        {
            var img = LoadTextureByPath(p);
            return img is null ? null : BitmapFactory.FromRgbaThumbnail(img);
        });
        ContentBrowser.RequestThumbnails = nodes =>
        {
            foreach (var n in nodes) _thumbnails.Request(n.ThumbnailPath, bmp => n.Thumbnail = bmp);
        };
        MapContent.OpenMap = OpenAssetDocument;
        MapContent.ItemStateChanged = OnMapContentItemStateChanged;
        LoadRecentProjects(RecentProjects.Load());
    }

    /// <summary>Extract a material library's (.materials.bin / skin .bin) materials as virtual assets (M33).</summary>
    private IReadOnlyList<MaterialAssetViewModel> ExtractMaterialsForNode(AssetNodeViewModel node)
    {
        if (node.Entry is not { } e) return System.Array.Empty<MaterialAssetViewModel>();
        try
        {
            var mats = MaterialLibraryExtractor.Extract(GetAssetBytes(e), ResolveBinName);
            return mats.Select(m => new MaterialAssetViewModel(m, e, e.ReadOnly)).ToList();
        }
        catch (Exception ex)
        {
            _log.Warn("Material", $"Could not read materials from {e.DisplayName}: {ex.Message}");
            return System.Array.Empty<MaterialAssetViewModel>();
        }
    }

    /// <summary>Open a material virtual-asset in the Material Editor, filtered to the chosen material (M33).</summary>
    private async void OpenMaterialAsset(MaterialAssetViewModel material)
    {
        // Show the inspector body + its source-bin overview, then load the materials and reveal the tab.
        Inspector.ShowEntry(material.SourceEntry);
        Inspector.SetAssetStatus(material.ReadOnly ? "Read-only Riot material" : "Project material (editable)", null);
        HasInspectorBody = true;

        await LoadMaterialBinAsync(material.SourceEntry, alsoRawBin: false);
        if (!HasMaterialData)
        {
            _log.Warn("Material", $"'{material.FullName}': no editable materials resolved from {material.SourceBin}.");
            return;
        }
        MaterialEditor.Search = material.FullName; // filter the editor to the clicked material
        InspectorTab = 1;                          // the "Materials" tab
        _log.Info("Material", $"Opened '{material.FullName}' ({material.Profile}) from {material.SourceBin}" +
                              (material.ReadOnly ? " — read-only reference (Copy To Project to edit)." : "."));
    }

    // ---- Document / viewport tabs (M33) --------------------------------------------------------------

    public ObservableCollection<EditorDocument> Documents { get; } = new();
    [ObservableProperty] private EditorDocument? _activeDocument;
    private bool _restoringScene;

    /// <summary>A cached map viewport scene — lets a map tab restore fully (edits/selection/visibility) on
    /// re-activation instead of re-decoding, so it "stays loaded" while other assets are inspected.</summary>
    private sealed record MapScene(
        MapGeoAsset Map, byte[] MapBytes, WadAssetEntry Entry, MapVisibilityDefinition Visibility,
        MapVisibilityControllers? Controllers,
        MeshAsset Mesh, IReadOnlyList<TextureImage?>? Textures,
        IReadOnlyList<ViewportMeshRenderer.SubmeshMaterial>? Materials,
        IReadOnlyList<TextureImage?>? Lightmaps,
        IReadOnlyList<TextureImage?>? FlowMasks, IReadOnlyList<TextureImage?>? FlowGrads, // M44 flow-water
        IReadOnlyList<TextureImage?>? TerrainTops, IReadOnlyList<TextureImage?>? TerrainExtras,
        double LightmapScale, Formats.MapGeo.MapSunProperties? SunProps, // M45 sun properties
        IReadOnlyList<MapParticlePlacement>? Particles,
        IReadOnlyDictionary<uint, VfxSystemDefinition> VfxSystems,
        IReadOnlyList<MapCubemapProbe>? Probes, IReadOnlyList<MapAnimatedProp>? Props,
        IReadOnlyList<MapSoundPlacement>? Sounds,
        int[] VisibilityIndices, bool HasMoves, int[] SelectedMeshIndices,
        List<MapLayerGroupViewModel> LayerGroups, string MapName, List<MapPieceViewModel> Pieces);

    /// <summary>User opened an asset — create or focus its tab and activate it.</summary>
    private void OpenAssetDocument(AssetNodeViewModel? node)
    {
        if (node?.Entry is not { } entry) { SelectedNode = node; return; }
        var doc = Documents.FirstOrDefault(d => d.Key == entry.PathHash);
        if (doc is null)
        {
            var kind = EditorDocument.KindOf(entry.Type);
            // M46: dedicated particle bins (path mentions particles) open straight in the Particle Editor
            // WINDOW. Other VFX-bearing bins (skin bins, map materials.bin) keep their normal editor; use
            // Tools -> Open in Particle Editor for those.
            if (kind == DocumentKind.Bin && entry.IsResolved && entry.Path.Contains("particles", StringComparison.OrdinalIgnoreCase))
            {
                OpenParticleEditorFor(entry);
                return;
            }
            doc = new EditorDocument
            {
                Title = entry.DisplayName,
                Kind = kind,
                Key = entry.PathHash,
                Entry = entry,
            };
            Documents.Add(doc);
        }
        ActivateDocument(doc);
    }

    [RelayCommand]
    private void ActivateDocument(EditorDocument? doc)
    {
        if (doc is null) return;
        if (ReferenceEquals(ActiveDocument, doc)) return;

        CaptureActiveScene(); // snapshot the outgoing map (if any) so it restores later
        foreach (var d in Documents) d.IsActive = ReferenceEquals(d, doc);
        ActiveDocument = doc;

        var node = doc.Entry is { } e && _nodesByHash.TryGetValue(e.PathHash, out var n) ? n : null;

        if (doc.Scene is MapScene scene)
        {
            _restoringScene = true;
            try { SelectedNode = node; RestoreMapScene(scene); }
            finally { _restoringScene = false; }
        }
        else
        {
            SelectedNode = node; // triggers the normal load path (OnSelectedNodeChanged)
        }
    }

    /// <summary>M46: open a particle .bin in the Particle Editor WINDOW (separate top-level window;
    /// the main layout stays untouched).</summary>
    public Action? ShowParticleEditorWindow; // wired by MainWindow (owns the window instance)

    private async void OpenParticleEditorFor(WadAssetEntry entry)
    {
        try
        {
            var bytes = ReadAsset(entry.PathHash);
            bool editable = !entry.ReadOnly;
            // M197 (4.5): parse off the UI thread. The map VFX bins this milestone makes reachable are far
            // larger than a champion bin - map22.bin measures around 3 seconds - and that was a hard freeze.
            var resolveName = ParticleEditor.ResolveBinName;
            var (doc, defs) = await System.Threading.Tasks.Task.Run(
                () => ParticleEditorViewModel.Parse(bytes, resolveName));
            if (doc is null || !ParticleEditor.Load(entry, doc, defs, editable))
            {
                _log.Warn("Particle", $"{entry.DisplayName} contains no VFX systems.");
                return;
            }
            ShowParticleEditorWindow?.Invoke();
            _log.Info("Particle", $"Particle Editor: {entry.DisplayName} — {ParticleEditor.Systems.Count} system(s){(editable ? "" : " (read-only Riot reference)")}.");
        }
        catch (Exception ex) { _log.Error("Particle", ex.Message); }
    }

    /// <summary>M46 Tools menu: open the ACTIVE document's .bin in the Particle Editor window.</summary>
    [RelayCommand]
    private void OpenActiveInParticleEditor()
    {
        if (ActiveDocument?.Entry is not { } entry) { _log.Info("Particle", "Open a .bin document first."); return; }
        OpenParticleEditorFor(entry);
    }

    /// <summary>M46: save the edited particle .bin — in place for folder-project files, to the
    /// override workspace for wad-backed assets (mirrors SaveMaterialOverride).</summary>
    private async Task SaveParticleOverride()
    {
        if (ParticleEditor.Entry is not { } entry) { _log.Warn("Particle", "No particle .bin open."); return; }
        if (!GuardEditable(entry)) return;
        if (ParticleEditor.Document is not { } pdoc) return;
        if (!pdoc.IsDirty) { _log.Info("Particle", "No particle edits to save."); return; }
        if (!await EnsureProjectSavedAsync()) return;

        var bytes = pdoc.Serialize();
        try { _ = new LeagueToolkit.Core.Meta.BinTree(new MemoryStream(bytes, false)); }
        catch (Exception ex) { _log.Error("Particle", $"Edited particle .bin failed to re-parse — NOT saved: {ex.Message}"); return; }

        // M126: one save path for project bins — folder-project files are written IN PLACE (and any
        // stale shadow override dissolves); only wad-backed assets go to the override workspace.
        await SaveMapBinBytesAsync(entry, bytes);
    }

    /// <summary>M121: the Model Preview window closed — its document tabs go with it. Mesh and
    /// Texture tabs are exactly the kinds whose content lives in that window (M50 meshes, M118
    /// static objects, M120 images); Map/Bin tabs belong to the main viewport and stay.</summary>
    public void OnPreviewWindowClosed()
    {
        MeshPreview.OnWindowClosed();
        foreach (var doc in Documents.Where(d => d.Kind is DocumentKind.Mesh or DocumentKind.Texture).ToList())
            CloseDocument(doc);
    }

    [RelayCommand]
    private void CloseDocument(EditorDocument? doc)
    {
        if (doc is null) return;
        bool wasActive = ReferenceEquals(doc, ActiveDocument);
        if (doc.Scene is MapScene ms) UndoService.PurgeContext(ms.Map);
        doc.IsActive = false;
        Documents.Remove(doc);
        if (!wasActive) return;

        ActiveDocument = null; // so activating the next tab doesn't snapshot the dying scene
        var next = Documents.LastOrDefault();
        if (next is not null) ActivateDocument(next);
        else ClearViewport();
    }

    private void CaptureActiveScene()
    {
        if (ActiveDocument is { Kind: DocumentKind.Map }) ActiveDocument.Scene = CaptureMapScene();
    }

    /// <summary>Reflect a map's unsaved mesh edits as a dirty dot on its tab.</summary>
    partial void OnHasMapMovesChanged(bool value)
    {
        if (ActiveDocument is { Kind: DocumentKind.Map } d) d.IsDirty = value;
    }

    private MapScene? CaptureMapScene()
    {
        if (_currentMap is not { } map || _currentMapBytes is null || _currentMapEntry is not { } entry || CurrentMesh is not { } mesh)
            return null;
        return new MapScene(map, _currentMapBytes, entry, _mapVisibility, _mapControllers, mesh,
            CurrentModelTextures, CurrentModelSubmeshMaterials, CurrentModelLightmapTextures,
            _mapFlowMasks, _mapFlowGrads, _mapTerrainTops, _mapTerrainExtras,
            CurrentLightmapScale, CurrentSunProperties,
            CurrentModelParticles, _vfxSystems, CurrentModelProbes, CurrentModelProps,
            CurrentModelSounds,
            VisibilityAxes.Select(a => a.SelectedIndex).ToArray(), HasMapMoves,
            _selection.Items.Select(m => m.Index).ToArray(),
            MapContent.LayerGroups.ToList(), MapContent.MapName, MapContent.Pieces.ToList());
    }

    private void RestoreMapScene(MapScene s)
    {
        CurrentSkeleton = null; ShowBones = false;
        _currentMap = s.Map; _currentMapBytes = s.MapBytes; _currentMapEntry = s.Entry;
        MapGeneration++;
        InvalidateRayIndex();
        PrebuildRayIndex(s.Map, MeshVerticesRevision);   // M172a
        HasMapGeo = true;   // M79
        _mapVisibility = s.Visibility;
        _mapControllers = s.Controllers;
        _visibilityResolver = new MapVisibilityResolver(s.Controllers, s.Visibility);
        RebuildVisibilityAxes(s.Visibility, s.VisibilityIndices);
        CurrentMesh = s.Mesh;
        CurrentModelTextures = s.Textures;
        ClearSecondaryTextures();
        CurrentModelLightmapTextures = s.Lightmaps;
        _mapFlowMasks = s.FlowMasks; _mapFlowGrads = s.FlowGrads;
        _mapTerrainTops = s.TerrainTops; _mapTerrainExtras = s.TerrainExtras;
        CurrentModelSubmeshMaterials = s.Materials;
        PublishMapMaterialLayers();
        CurrentLightmapScale = s.LightmapScale; CurrentSunProperties = s.SunProps;       // M45
        CurrentModelParticles = s.Particles;
        _vfxSystems = s.VfxSystems;
        CurrentModelProbes = s.Probes;
        CurrentModelProps = s.Props;
        CurrentModelSounds = s.Sounds;                 // M55
        MapContent.SetBucketGrids(s.Map.BucketGrids);  // M55
        HasBucketGrids = s.Map.BucketGrids.Count > 0;  // M77
        RebuildBucketGridLines();
        SelectedParticleTreeItem = null;
        MapGeoInspector.Show(s.Map, s.Entry.Path);
        MapContent.SetLayerGroups(s.LayerGroups);
        MapContent.ShowMap(s.MapName, s.Pieces);
        HasMapMoves = s.HasMoves;
        Inspector.ShowEntry(s.Entry);
        HasInspectorBody = true;
        InspectorTab = 0;
        TryLoadMaterialBin(s.Entry, alsoRawBin: true);

        var meshes = s.SelectedMeshIndices
            .Select(i => s.Map.Meshes.FirstOrDefault(x => x.Index == i))
            .Where(m => m is not null).Select(m => m!).ToList();
        _selection.SetMany(meshes);
        ApplyMapVisibility();   // recompute the visibility array from the restored filters
        MeshVerticesRevision++; // re-upload possibly-edited vertices
        _log.Info("MapGeo", $"Restored map tab '{s.MapName}' ({s.Map.MeshCount:n0} meshes).");
    }

    /// <summary>Push the freshly-built asset tree into the Content Browser + Map Content panels.</summary>
    // ---- M122: skyboxes (map viewport + model preview share the catalogue) ----

    /// <summary>Combo labels: [None, Custom image..., ...discovered assets].</summary>
    public ObservableCollection<string> SkyboxOptions { get; } = new();
    private List<Services.SkyboxOption> _skyboxCatalog = new();
    [ObservableProperty] private int _selectedSkyboxIndex;
    [ObservableProperty] private Services.SkyboxSpec? _currentSkybox;

    private void RebuildSkyboxOptions()
    {
        _skyboxCatalog = Services.SkyboxCatalog.Discover(AssetEntries);
        SkyboxOptions.Clear();
        SkyboxOptions.Add("No skybox");
        SkyboxOptions.Add("Custom image…");
        foreach (var o in _skyboxCatalog) SkyboxOptions.Add(o.Label);
        SelectedSkyboxIndex = 0;
        MeshPreview.SetSkyboxOptions(SkyboxOptions);
        if (_skyboxCatalog.Count > 0)
            _log.Info("Skybox", $"{_skyboxCatalog.Count} skybox asset(s) discovered (cubemaps, domes, sky textures).");
    }

    partial void OnSelectedSkyboxIndexChanged(int value) => _ = ApplyMapSkyboxAsync(value);

    private async Task ApplyMapSkyboxAsync(int index)
    {
        CurrentSkybox = await LoadSkyboxAtAsync(index);
    }

    /// <summary>Decode the skybox behind one combo index (shared by both viewports). Index 0 = none,
    /// 1 = pick a custom image file, 2+ = the discovered catalogue.</summary>
    private async Task<Services.SkyboxSpec?> LoadSkyboxAtAsync(int index)
    {
        try
        {
            if (index <= 0) return null;
            if (index == 1)
            {
                var file = await Dialogs.OpenFileAsync("Choose a skybox image (png/jpg/tex/dds)", DialogService.All);
                if (file is null) return null;
                var custom = await Task.Run(() => Services.SkyboxCatalog.LoadCustomFile(file));
                if (custom is null) _log.Warn("Skybox", $"{Path.GetFileName(file)}: not a decodable image.");
                return custom;
            }
            int ci = index - 2;
            if (ci < 0 || ci >= _skyboxCatalog.Count) return null;
            var opt = _skyboxCatalog[ci];
            return await Task.Run(() =>
            {
                var bytes = ReadAsset(opt.Main.PathHash);
                switch (opt.Kind)
                {
                    case Services.SkyboxSourceKind.Cubemap:
                        var cm = CubemapDecoder.TryDecodeDds(bytes);
                        if (cm is not null) return new Services.SkyboxSpec(Cubemap: cm);
                        return new Services.SkyboxSpec(Equirect: TextureDecoder.Decode(bytes));
                    case Services.SkyboxSourceKind.Texture:
                        return new Services.SkyboxSpec(Equirect: TextureDecoder.Decode(bytes));
                    default:
                        TextureImage? tex = opt.PairedTexture is { } pt
                            ? TextureDecoder.Decode(ReadAsset(pt.PathHash)) : null;
                        if (opt.Main.Path.EndsWith(".skn", StringComparison.OrdinalIgnoreCase))
                        {
                            var skn = SkinnedMeshDecoder.Decode(bytes);
                            return new Services.SkyboxSpec(MeshPositions: skn.Positions, MeshUvs: skn.Uvs,
                                MeshIndices: skn.Indices, MeshTexture: tex);
                        }
                        var so = Formats.Meshes.StaticObjectDecoder.Decode(bytes, opt.Main.Path);
                        if (so is null) return null;
                        return new Services.SkyboxSpec(MeshPositions: so.Positions, MeshUvs: so.Uvs,
                            MeshIndices: so.Indices, MeshTexture: tex);
                }
            });
        }
        catch (Exception ex)
        {
            _log.Error("Skybox", ex.Message);
            return null;
        }
    }

    private void RefreshContentPanels()
    {
        ContentBrowser.SetRoots(RootNodes);
        var maps = _nodesByHash.Values
            .Where(n => n.Entry is { Type: AssetType.MapGeometry })
            .Where(n => !ProjectMode || n.Entry!.SourceKind != AssetSourceKind.RiotReference)
            .OrderBy(n => n.Entry!.Path, StringComparer.OrdinalIgnoreCase)
            .DistinctBy(n => n.Entry!.PathHash)
            .ToList();
        MapContent.SetMaps(maps);
        // M123e: tree rebuilds fire on every project-file save (Add Mesh writes the materials bin,
        // the watcher fires on any disk change) - clearing the outliner then guts an OPEN map's
        // panel mid-session. Only clear when no map is actually loaded.
        if (_currentMap is null) MapContent.ClearMap();
        RebuildSkyboxOptions();   // M122
    }

    // ---- Material editor: asset access helpers --------------------------

    private byte[]? ReadAssetByPath(string path)
    {
        if (!ContentLoaded || string.IsNullOrEmpty(path)) return null;
        var hash = HashAlgorithms.WadPath(path);
        return TryResolveEntry(hash, out _) ? ReadAsset(hash) : null;
    }

    private bool TextureExistsByPath(string path)
    {
        if (!ContentLoaded || string.IsNullOrEmpty(path)) return false;
        return TryResolveEntry(HashAlgorithms.WadPath(path), out _);
    }

    private TextureImage? LoadTextureByPath(string path)
    {
        try
        {
            var bytes = ReadAssetByPath(path);
            if (bytes is null) return null;
            return TextureDecoder.Decode(bytes);
        }
        catch { return null; }   // subchunked/corrupt chunks throw inside the mount read — never propagate
    }

    private Avalonia.Media.Imaging.Bitmap? LoadThumbnailByPath(string path)
    {
        var img = LoadTextureByPath(path);
        return img is null ? null : BitmapFactory.FromRgba(img);
    }

    private void OpenTextureByPath(string path)
    {
        if (!ContentLoaded) return;
        var hash = HashAlgorithms.WadPath(path);
        if (_nodesByHash.TryGetValue(hash, out var node)) SelectedNode = node;
        else _log.Warn("Material", $"Texture not found: {path}");
    }

    // ---- Animation ------------------------------------------------------

    private AnimationClip? DecodeAnimation(WadAssetEntry entry)
    {
        if (!ContentLoaded) return null;
        try { return AnimationDecoder.Decode(ReadAsset(entry.PathHash), entry.DisplayName); }
        catch (Exception ex) { _log.Error("Anim", $"{entry.DisplayName}: {ex.Message}"); return null; }
    }

    /// <summary>M85: gather the champion's submesh-visibility rules — initialSubmeshToHide from every
    /// skins/*.bin and per-clip show/hide lists from every animations/*.bin under the champ folder
    /// (keyed by .anm file name to match the preview's animation entries).</summary>
    private (IReadOnlyList<string> InitialHide, IReadOnlyDictionary<string, Formats.Skeletons.AnimClipInfo>? Clips,
             IReadOnlySet<string> OwnAnms)
        LoadSubmeshRules(WadAssetEntry skn)
    {
        var ownAnms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!skn.IsResolved) return (Array.Empty<string>(), null, ownAnms);
            var parts = skn.Path.Split('/');
            int ci = Array.FindIndex(parts, p => p.Equals("characters", StringComparison.OrdinalIgnoreCase));
            if (ci < 0 || ci + 1 >= parts.Length) return (Array.Empty<string>(), null, ownAnms);
            string champ = parts[ci + 1];
            const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
            string animDir = $"characters/{champ}/animations/";
            string skinDir = $"characters/{champ}/skins/";

            var hide = new List<string>();
            var clips = new Dictionary<string, Formats.Skeletons.AnimClipInfo>(StringComparer.OrdinalIgnoreCase);

            // M86: this skin's OWN animation graph first (named in the skin bin's dependency list) —
            // clips merge first-wins, and other skins' graphs carry other skins' effect keys.
            var skinBinPath = SkinPaths.BinPathForSkn(skn.Path);
            if (skinBinPath is not null && TryResolveEntry(HashAlgorithms.WadPath(skinBinPath), out var skinBinEntry)
                && VfxSystemResolver.ExtractDependencies(GetAssetBytes(skinBinEntry))
                    .FirstOrDefault(d => d.Contains("/animations/", OIC)) is { } graphPath
                && TryResolveEntry(HashAlgorithms.WadPath(graphPath), out var graphEntry))
                foreach (var c in Formats.Skeletons.ChampionAnimationData.ParseClips(GetAssetBytes(graphEntry), ResolveBinName))
                {
                    var file = Path.GetFileName(c.AnmPath.Replace('\\', '/'));
                    if (file.Length > 0 && !clips.ContainsKey(file)) clips[file] = c;
                    if (file.Length > 0) ownAnms.Add(file);   // M115: THIS skin's animation set
                }

            foreach (var e in AssetEntries)
            {
                if (!e.IsResolved || !e.Path.EndsWith(".bin", OIC)) continue;
                if (e.Path.Contains(animDir, OIC))
                {
                    foreach (var c in Formats.Skeletons.ChampionAnimationData.ParseClips(GetAssetBytes(e), ResolveBinName))
                    {
                        var file = Path.GetFileName(c.AnmPath.Replace('\\', '/'));
                        if (file.Length > 0 && !clips.ContainsKey(file)) clips[file] = c;
                    }
                }
                else if (e.Path.Contains(skinDir, OIC) && hide.Count == 0)
                    hide.AddRange(Formats.Skeletons.ChampionAnimationData.ParseInitialHide(GetAssetBytes(e)));
            }
            if (clips.Count > 0)
                _log.Info("Preview", $"{champ}: {clips.Count} named clip(s) with visibility data, initial-hide: {(hide.Count > 0 ? string.Join(' ', hide) : "(none)")}.");
            return (hide, clips.Count > 0 ? clips : null, ownAnms);
        }
        catch { return (Array.Empty<string>(), null, ownAnms); }
    }

    private IEnumerable<AnimationEntryViewModel> FindAnimations(WadAssetEntry skn, IReadOnlySet<string>? currentSkinAnms = null)
    {
        if (!ContentLoaded || !skn.IsResolved) return Enumerable.Empty<AnimationEntryViewModel>();
        var parts = skn.Path.Split('/');
        int ci = Array.FindIndex(parts, p => p.Equals("characters", StringComparison.OrdinalIgnoreCase));
        string champ = ci >= 0 && ci + 1 < parts.Length ? parts[ci + 1] : "";
        var marker = $"/characters/{champ}/";
        const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
        bool Match(string path, bool resolved) =>
            resolved && path.EndsWith(".anm", OIC) && (champ.Length == 0 || path.Contains(marker, OIC));

        // M115: which .anm files the LOADED skin's own animation graph references (green highlight);
        // when the graph didn't resolve, fall back to path matching against the skn's own skin folder.
        string sknGroup = AnimationEntryViewModel.GroupFromPath(skn.Path);
        bool IsCurrent(string path, string fileName) =>
            currentSkinAnms is { Count: > 0 }
                ? currentSkinAnms.Contains(fileName)
                : AnimationEntryViewModel.GroupFromPath(path) == sknGroup;

        AnimationEntryViewModel Make(WadAssetEntry e) => new(e)
        {
            SkinGroup = AnimationEntryViewModel.GroupFromPath(e.Path),
            IsCurrentSkin = IsCurrent(e.Path, Path.GetFileName(e.Path)),
        };

        var seen = new HashSet<ulong>();
        var list = new List<AnimationEntryViewModel>();
        foreach (var e in AssetEntries)
            if (Match(e.Path, e.IsResolved) && seen.Add(e.PathHash)) list.Add(Make(e));

        // If the mod doesn't ship this unit's animations, fall back to the original game WADs.
        if (list.Count == 0 && _mounts is not null)
            foreach (var fb in _mounts.Fallback)
                foreach (var a in fb.Enumerate())
                    if (Match(a.VirtualPath, a.IsResolved) && seen.Add(a.PathHash)) list.Add(Make(a.ToEntry()));

        // Loaded skin's clips first, then grouped by skin (Base, Skin 01…, Shared last), names within.
        static int GroupRank(string g) => g == "Base" ? 0 : g == "Shared" ? 999 : 1;
        return list
            .OrderByDescending(a => a.IsCurrentSkin)
            .ThenBy(a => GroupRank(a.SkinGroup))
            .ThenBy(a => a.SkinGroup, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    [RelayCommand]
    private async Task AssignAnimation()
    {
        if (CurrentMesh is not { CanSkin: true } || CurrentSkeleton is null)
        { _log.Warn("Anim", "Select a skinned champion (.skn with a skeleton) first."); return; }
        var anmType = new FilePickerFileType("Animation") { Patterns = new[] { "*.anm" } };
        var path = await Dialogs.OpenFileAsync("Assign animation (.anm)", anmType, DialogService.All);
        if (path is null) return;
        try
        {
            var clip = AnimationDecoder.Decode(File.ReadAllBytes(path), Path.GetFileName(path));
            Animation.SetExternalClip(clip);
            _log.Success("Anim", $"Assigned {Path.GetFileName(path)} ({clip.Duration:0.00}s, {clip.Fps:0.#} fps).");
        }
        catch (Exception ex) { _log.Error("Anim", ex.Message); }
    }

    // ---- WAD ------------------------------------------------------------

    [RelayCommand]
    private async Task OpenWad()
    {
        var path = await Dialogs.OpenFileAsync("Open WAD archive", DialogService.Wad, DialogService.All);
        if (path is not null) LoadWad(path);
    }

    public void LoadWad(string path)
    {
        try
        {
            _log.Info("WAD", $"Opening {Path.GetFileName(path)} …");
            _archive?.Dispose();
            _archive = WadArchive.Open(path, _resolver);
            Documents.Clear(); ActiveDocument = null;  // fresh source — old tabs are stale
            RebuildTree();
            ClearViewport();
            Inspector.Clear();
            UndoService.Clear(); // new inspection context = fresh history

            _mounts?.Dispose(); _mounts = null;
            ProjectMode = false; InspectionMode = true;
            _log.Success("WAD", $"Loaded {_archive.Entries.Count:n0} chunks; resolved {_archive.ResolvedCount:n0} paths.");
            _log.Info("WAD", "Single-WAD inspection mode — open a project folder (File ▸ Open Project Folder) to edit and build mods.");
            Status = $"{_archive.Name} — {_archive.Entries.Count:n0} entries · {_archive.ResolvedCount:n0} resolved";
            Title = $"ReyEngine — {_archive.Name}";
        }
        catch (Exception ex)
        {
            _log.Error("WAD", ex.Message);
        }
    }

    private void RebuildTree()
    {
        if (!ContentLoaded) return;
        var root = AssetTree.Build(_archive.Entries, _archive.Name);
        RootNodes.Clear();
        _nodesByHash.Clear();
        var rootVm = new AssetNodeViewModel(root);
        IndexNodes(rootVm);
        RootNodes.Add(rootVm);
        RefreshAllStatuses();
        RefreshContentPanels();
    }

    private void IndexNodes(AssetNodeViewModel node)
    {
        if (node.Entry is { } e) _nodesByHash[e.PathHash] = node;
        foreach (var c in node.Children) IndexNodes(c);
    }

    private void RefreshAllStatuses()
    {
        foreach (var ov in _overrides.All)
            if (_nodesByHash.TryGetValue(ov.PathHash, out var node)) node.Status = AssetStatus.Modified;
    }

    private void SetNodeStatus(ulong hash, AssetStatus status)
    {
        if (_nodesByHash.TryGetValue(hash, out var node)) node.Status = status;
    }

    /// <summary>Bytes for an asset — the project override if one exists, otherwise the WAD chunk.</summary>
    private byte[] GetAssetBytes(WadAssetEntry entry) => ReadAsset(entry.PathHash);

    [RelayCommand]
    private void ReloadWad()
    {
        if (_archive is null) { _log.Warn("WAD", "No archive is open."); return; }
        LoadWad(_archive.FilePath);
    }

    [RelayCommand(CanExecute = nameof(CanExportSelected))]
    private async Task ExportSelected()
    {
        var entry = ContextNode?.Entry;
        if (entry is null || !ContentLoaded) { _log.Warn("Export", "Select a file first."); return; }
        var outPath = await Dialogs.SaveFileAsync("Export asset", entry.DisplayName);
        if (outPath is null) return;
        try
        {
            File.WriteAllBytes(outPath, ReadAsset(entry.PathHash));
            _log.Success("Export", $"Wrote {outPath}");
        }
        catch (Exception ex) { _log.Error("Export", ex.Message); }
    }

    // ---- Hashes ---------------------------------------------------------

    [RelayCommand]
    private async Task SyncHashes()
    {
        try
        {
            Status = "Syncing CommunityDragon hashes…";
            _log.Info("Hashes", "Downloading CommunityDragon hashes…");
            var db = await Task.Run(() => _sync.SyncAsync(m => _log.Info("Hashes", m)));
            _resolver.Swap(db);
            ApplyHashesToOpenWad();
            Status = $"Hashes synced — {db.WadCount:n0} WAD + {db.BinCount:n0} bin";
        }
        catch (Exception ex)
        {
            _log.Error("Hashes", $"Sync failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ReloadLocalHashes()
    {
        var db = _sync.LoadLocal(m => _log.Info("Hashes", m));
        _resolver.Swap(db);
        ApplyHashesToOpenWad();
    }

    private void ApplyHashesToOpenWad()
    {
        if (!ContentLoaded) return;
        int resolved = _resolver.RefreshArchive(_archive);
        RebuildTree();
        _log.Success("Hashes", $"Resolved {resolved:n0} / {_archive.Entries.Count:n0} WAD paths.");
        Status = $"{_archive.Name} — {_archive.Entries.Count:n0} entries · {resolved:n0} resolved";
    }

    [RelayCommand]
    private void HashLookup()
    {
        if (string.IsNullOrWhiteSpace(HashInput)) { _log.Warn("Hash", "Type a path/string in the toolbar box."); return; }
        var s = HashInput.Trim();
        _log.Info("Hash", $"\"{s}\"");

        ulong wadHash = HashAlgorithms.WadPath(s);
        uint binHash = HashAlgorithms.Fnv1a(s);
        _log.Info("Hash", $"   xxhash64 (wad) = 0x{wadHash:x16}");
        _log.Info("Hash", $"   fnv1a    (bin) = 0x{binHash:x8}");
        _log.Info("Hash", $"   elf            = 0x{HashAlgorithms.Elf(s):x8}");

        LogCandidates("wad", _resolver.Database.WadCandidates(wadHash));
        LogCandidates("bin", _resolver.Database.BinCandidates(binHash));

        // If the user typed a raw hash, reverse-resolve it.
        var hex = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
        if (hex.Length == 16 && ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var h64))
            LogCandidates("wad↩", _resolver.Database.WadCandidates(h64));
        else if (hex.Length == 8 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var h32))
            LogCandidates("bin↩", _resolver.Database.BinCandidates(h32));
    }

    private void LogCandidates(string tag, IReadOnlyList<string> candidates)
    {
        if (candidates.Count == 0) return;
        if (candidates.Count == 1) { _log.Success("Hash", $"   {tag} → {candidates[0]}"); return; }
        _log.Warn("Hash", $"   {tag} CONFLICT ({candidates.Count} candidates):");
        foreach (var c in candidates) _log.Warn("Hash", $"      • {c}");
    }

    // ---- Selection / preview -------------------------------------------

    partial void OnSelectedNodeChanged(AssetNodeViewModel? value)
    {
        if (_restoringScene) return; // a document tab is restoring its cached scene — don't re-load
        var entry = value?.Entry;
        if (entry is null) return;

        // Unresolved chunks have no extension — sniff the type from magic bytes so
        // preview/decode still works before a hash sync (guard against huge chunks).
        if (entry.Type == AssetType.Unknown && _archive is not null && entry.UncompressedSize < 32 * 1024 * 1024)
        {
            try { entry.Type = AssetTypeDetector.FromMagic(ReadAsset(entry.PathHash)); }
            catch { /* leave Unknown */ }
        }

        Inspector.ShowEntry(entry);
        Inspector.SetPreview(null);
        bool modified = _overrides.Has(entry.PathHash);
        string source = !ProjectMode ? "WAD"
            : entry.SourceKind switch
            {
                AssetSourceKind.RiotReference => "Read-only Riot asset",
                AssetSourceKind.ProjectOverride => "Project override (editable)",
                _ => "Project asset (editable)",
            };
        Inspector.SetAssetStatus(
            modified ? $"Modified — {source}" : source,
            modified && _overrides.TryGet(entry.PathHash, out var ov) ? ov.OverrideFile : null);

        if (entry.Type is not AssetType.SkinnedMesh) ClearViewport();
        if (entry.Type != AssetType.Bin) BinEditor.Clear();
        HasInspectorBody = entry.Type is AssetType.SkinnedMesh or AssetType.StaticMesh or AssetType.MapGeometry or AssetType.Bin;
        InspectorTab = entry.Type == AssetType.Bin ? 2 : 0;
        if (!HasInspectorBody)
        {
            MaterialEditor.Clear();
            HasMaterialData = false;
        }

        switch (entry.Type)
        {
            case AssetType.Texture or AssetType.Dds:
                _ = TryPreviewTextureAsync(entry);
                break;
            case AssetType.SkinnedMesh:
                _ = LoadMeshPreviewAsync(entry);   // M50: separate model window — the map viewport stays untouched
                TryLoadMaterialBin(entry, alsoRawBin: true);
                break;
            case AssetType.StaticMesh:
                _ = LoadStaticMeshPreviewAsync(entry);   // M118: .scb/.sco in the model preview
                break;
            case AssetType.MapGeometry:
                _ = LoadMapGeoAsync(entry);
                TryLoadMaterialBin(entry, alsoRawBin: true);
                break;
            case AssetType.Bin:
                _ = LoadBinAsync(entry);
                TryLoadMaterialBin(entry, alsoRawBin: false);
                break;
        }
    }

    /// <summary>M118: open a static object (.scb binary / .sco ascii) in the Model Preview. These are
    /// the VFX mesh primitives (weapon swipes, rings, cylinders) — no skeleton, no textures of their
    /// own (the emitter supplies the sprite in a VFX context), and usually no normals, so normals are
    /// synthesized from the faces for lighting.</summary>
    private async Task LoadStaticMeshPreviewAsync(WadAssetEntry entry)
    {
        try
        {
            var mesh = await Task.Run(() =>
            {
                var data = StaticObjectDecoder.Decode(ReadAsset(entry.PathHash), entry.Path);
                if (data is null) return null;

                int vc = data.Positions.Length / 3;
                var normals = new float[data.Positions.Length];
                // accumulate face normals per vertex, then normalize — flat-ish but lightable
                for (int i = 0; i + 2 < data.Indices.Length; i += 3)
                {
                    int a = (int)data.Indices[i], b = (int)data.Indices[i + 1], d = (int)data.Indices[i + 2];
                    var pa = new System.Numerics.Vector3(data.Positions[a*3], data.Positions[a*3+1], data.Positions[a*3+2]);
                    var pb = new System.Numerics.Vector3(data.Positions[b*3], data.Positions[b*3+1], data.Positions[b*3+2]);
                    var pd = new System.Numerics.Vector3(data.Positions[d*3], data.Positions[d*3+1], data.Positions[d*3+2]);
                    var n = System.Numerics.Vector3.Cross(pb - pa, pd - pa);
                    foreach (var vi in new[] { a, b, d })
                    { normals[vi*3] += n.X; normals[vi*3+1] += n.Y; normals[vi*3+2] += n.Z; }
                }
                for (int i = 0; i < vc; i++)
                {
                    var n = new System.Numerics.Vector3(normals[i*3], normals[i*3+1], normals[i*3+2]);
                    if (n.LengthSquared() > 1e-12f) { n = System.Numerics.Vector3.Normalize(n); normals[i*3] = n.X; normals[i*3+1] = n.Y; normals[i*3+2] = n.Z; }
                    else normals[i*3+1] = 1f;   // degenerate vertex: point up
                }

                var min = new System.Numerics.Vector3(float.MaxValue); var max = new System.Numerics.Vector3(float.MinValue);
                for (int i = 0; i < vc; i++)
                {
                    var v = new System.Numerics.Vector3(data.Positions[i*3], data.Positions[i*3+1], data.Positions[i*3+2]);
                    min = System.Numerics.Vector3.Min(min, v); max = System.Numerics.Vector3.Max(max, v);
                }

                return new MeshAsset
                {
                    Positions = data.Positions,
                    Normals = normals,
                    Uvs = data.Uvs,
                    Indices = data.Indices,
                    SubMeshes = new[] { new SubMeshInfo(string.IsNullOrEmpty(data.Name) ? "(static mesh)" : data.Name, 0, data.Indices.Length, vc) },
                    VertexCount = vc,
                    BoundsMin = min,
                    BoundsMax = max,
                };
            });
            if (mesh is null) { _log.Warn("Mesh", $"{entry.DisplayName}: not a readable .scb/.sco static object."); return; }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                MeshPreview.Show(entry.DisplayName, mesh, skeleton: null, textures: null);
                MeshPreview.SetAnimations(Enumerable.Empty<AnimationEntryViewModel>());
                MeshPreview.SetVfx(new Dictionary<uint, ReyEngine.Formats.Vfx.VfxSystemDefinition>());
                MeshInspector.ShowMesh(mesh, null);
                ShowMeshPreviewWindow?.Invoke();
                _log.Success("Mesh", $"{entry.DisplayName}: {mesh.VertexCount:n0} verts, {mesh.TriangleCount:n0} tris (static object — untextured; VFX supply the sprite).");
            });
        }
        catch (Exception ex) { _log.Error("Mesh", $"{entry.DisplayName}: {ex.Message}"); }
    }

    // ---- Material editor: load + apply + save ---------------------------

    private string? ResolveBinName(uint h) => _resolver.Database.TryGetBinName(h, out var n) ? n : null;

    private WadAssetEntry? ResolveMaterialBin(WadAssetEntry entry)
    {
        if (!ContentLoaded) return null;
        if (entry.Type == AssetType.Bin) return entry;
        if (!entry.IsResolved) return null;
        string? binPath = entry.Type switch
        {
            AssetType.SkinnedMesh => SkinPaths.BinPathForSkn(entry.Path),
            AssetType.MapGeometry => MapGeoMaterialResolver.MaterialsBinPathFor(entry.Path),
            _ => null,
        };
        if (binPath is null) return null;
        return TryResolveEntry(HashAlgorithms.WadPath(binPath), out var be) ? be : null;
    }

    private void TryLoadMaterialBin(WadAssetEntry entry, bool alsoRawBin)
    {
        var binEntry = ResolveMaterialBin(entry);
        if (binEntry is null) { MaterialEditor.Clear(); HasMaterialData = false; return; }
        _ = LoadMaterialBinAsync(binEntry, alsoRawBin);
    }

    private async Task LoadMaterialBinAsync(WadAssetEntry binEntry, bool alsoRawBin)
    {
        if (!ContentLoaded) return;
        byte[] bytes;
        try { bytes = GetAssetBytes(binEntry); }
        catch (Exception ex) { _log.Warn("Material", $"{binEntry.DisplayName}: {ex.Message}"); return; }

        MaterialDocument? matDoc = null;
        BinEditorDocument? binDoc = null;
        await Task.Run(() =>
        {
            try { matDoc = MaterialDocument.Parse(bytes, ResolveBinName); } catch { matDoc = null; }
            if (alsoRawBin) { try { binDoc = BinEditorDocument.Parse(bytes, ResolveBinName); } catch { binDoc = null; } }
        });

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (alsoRawBin && binDoc is not null) BinEditor.Load(binDoc, binEntry);
            if (matDoc is not null && matDoc.Materials.Count > 0)
            {
                MaterialEditor.Load(matDoc, binEntry);
                HasMaterialData = true;
                // M50: the materials list lives in the Inspector's Materials tab now (the Content
                // Browser quick-list was removed) — jump straight to it for materials.bin selections.
                if (binEntry.Path.EndsWith(".materials.bin", StringComparison.OrdinalIgnoreCase))
                { InspectorTab = 1; AssetDataExpanded = true; }
                if (MaterialEditor.UnresolvedCount > 0)
                    _log.Warn("Material", $"{binEntry.DisplayName}: {matDoc.Materials.Count} material(s), {MaterialEditor.UnresolvedCount} texture path(s) unresolved in this WAD.");
                else
                    _log.Info("Material", $"{binEntry.DisplayName}: {matDoc.Materials.Count} material(s).");
                if (matDoc.Issues.Count > 0)   // M125
                    _log.Warn("Material", $"{binEntry.DisplayName}: {matDoc.Issues.Count} issue(s) repaired while reading — see the ⚠ banner in the Materials tab (affected materials are marked red).");
            }
            else { MaterialEditor.Clear(); HasMaterialData = false; }
        });
    }

    private void ApplyMaterialToViewport()
    {
        var bytes = MaterialEditor.Serialize();
        if (bytes is null) return;
        try
        {
            if (MaterialEditor.Kind == MaterialSourceKind.ChampionSkin && CurrentMesh is { } mesh)
            {
                var resolved = ChampionMaterialResolver.Resolve(bytes, ResolveBinName);
                CurrentModelTextures = BuildSubmeshTextures(mesh, resolved, "material preview");
            }
            else if (MaterialEditor.Kind == MaterialSourceKind.MapMaterials && _currentMap is { } map && CurrentMesh is not null)
            {
                var names = map.Groups.Select(g => g.Material).Where(m => m.Length > 0).Distinct().ToList();
                var m2t = MapGeoMaterialResolver.Resolve(bytes, names);
                var profiles = MaterialProfiles.ForMapMaterials(bytes, names, ResolveBinName);
                CurrentModelTextures = BuildMapTextures(map, m2t, profiles, names.Count, _currentMapEntry?.Path);
            }
            else { _log.Info("Material", "Nothing in the viewport to preview — select the matching .skn/.mapgeo."); return; }
            _log.Success("Material", "Applied material edits to the viewport (live).");
        }
        catch (Exception ex) { _log.Error("Material", $"Apply failed: {ex.Message}"); }
    }

    private async Task SaveMaterialOverride()
    {
        if (MaterialEditor.BinEntry is not { } binEntry) { _log.Warn("Material", "No material .bin open."); return; }
        if (!GuardEditable(binEntry)) return;
        if (!MaterialEditor.IsDirty) { _log.Info("Material", "No material edits to save."); return; }
        if (!await EnsureProjectSavedAsync()) return;

        var bytes = MaterialEditor.Serialize();
        if (bytes is null) return;
        try { _ = new LeagueToolkit.Core.Meta.BinTree(new MemoryStream(bytes, false)); }
        catch (Exception ex) { _log.Error("Material", $"Edited material .bin failed to re-parse — NOT saved: {ex.Message}"); return; }

        // M126: one save path for project bins — folder-project files are written IN PLACE (and any
        // stale shadow override dissolves); only wad-backed assets go to the override workspace.
        if (!await SaveMapBinBytesAsync(binEntry, bytes)) return;
        ApplyMaterialToViewport();
        UndoService.MarkSaved();
    }

    private async Task ReplaceTextureForSlot(TextureSlotViewModel slot)
    {
        if (!ContentLoaded) return;
        var path = slot.EditedPath;
        var hash = HashAlgorithms.WadPath(path);
        if (!TryResolveEntry(hash, out _)) { _log.Warn("Material", $"Texture not found — can't replace: {path}"); return; }
        if (!await EnsureProjectSavedAsync()) return;

        var file = await Dialogs.OpenFileAsync($"Replace texture {Path.GetFileName(path)} (.dds/.tex)", DialogService.All);
        if (file is null) return;
        try
        {
            var stored = ProjectWorkspace.StoreOverride(Project, hash, file);
            _overrides.Set(new ProjectAssetOverride
            {
                PathHash = hash,
                ResolvedPath = path,
                OverrideFile = stored,
                AddedUtc = DateTime.UtcNow.ToString("o"),
            });
            SetNodeStatus(hash, AssetStatus.Modified);
            Project.IsDirty = true;
            UpdateTitle();
            slot.RefreshResolved();
            ApplyMaterialToViewport();
            _log.Success("Material", $"Replaced texture {Path.GetFileName(path)} with {Path.GetFileName(file)} (raw). Build Package will include it.");
        }
        catch (Exception ex) { _log.Error("Material", ex.Message); }
    }

    private void ClearSecondaryTextures()
    {
        CurrentModelMaskTextures = null;
        CurrentModelGradientTextures = null;
        CurrentModelEmissiveTextures = null;
        CurrentModelMatCapTextures = null;
        CurrentModelMatCapMaskTextures = null;
        // NOTE: CurrentModelLightmapTextures is NOT reset here — like CurrentModelSubmeshMaterials it is
        // published by BuildMapTextures (which runs before the UI-thread ClearSecondaryTextures call) and
        // reset explicitly on the mesh/clear paths, so clearing it here would wipe a freshly-loaded map's.
    }

    private void ClearViewport()
    {
        CurrentMesh = null;
        CurrentSkeleton = null;
        if (_currentMap is { } clearedMap) UndoService.PurgeContext(clearedMap);
        _currentMap = null;
        InvalidateRayIndex();
        _currentMapProfiles = null;
        _mapVisibility = MapVisibilityDefinition.Empty;
        _mapControllers = null;
        _visibilityResolver = null;
        RebuildVisibilityAxes(_mapVisibility);
        VisibilityLayerBits.Clear();
        PlacementVisibilityLayerBits.Clear();
        HasPlacementLayerSelection = false;
        PlacementLayerSummary = "";
        LayerControllerChoices.Clear();
        _layerControllerHashes.Clear();
        _currentMapBytes = null;
        _currentMapEntry = null;
        MapGeneration++;
        OnPropertyChanged(nameof(CanBakeLighting));   // M158
        OnPropertyChanged(nameof(HasMapForLayout));  // M147
        OnPropertyChanged(nameof(MeshesWithoutLightmapUv));
        _selection.Clear();
        HasMapMoves = false;
        CurrentModelTextures = null;
        ClearSecondaryTextures();
        CurrentModelLightmapTextures = null;
        CurrentModelSubmeshMaterials = null;
        CurrentModelSubmeshVisible = null;
        HasFlowmapWater = false;
        _mapFlowMasks = null;
        _mapFlowGrads = null;
        _mapTerrainTops = null;
        _mapTerrainExtras = null;
        CurrentLightmapScale = 1.0;
        CurrentSunProperties = null;
        CurrentModelParticles = null;
        SelectedParticleTreeItem = null;
        ParticleMarkers = null;
        CurrentModelProbes = null;
        CurrentModelProps = null;
        CurrentModelSounds = null;                                        // M55
        Sound.StopAll(); _activeAmbience.Clear(); _mapAudioBanks = null;   // M56
        SelectedSound = null; AmbienceEnabled = false;
        BucketGridLines = null;
        MapContent.SetBucketGrids(Array.Empty<MapBucketGridInfo>());
        HasBucketGrids = false;   // M77
        CurrentPropMeshes = null;
        ShowPropMeshes = false;
        MapContent.AddedMeshes.Clear();                                    // M79
        SetMapContentSelection(Array.Empty<MapOutlinerItemViewModel>(), null);
        SelectedAddedMesh = null;
        HasMapGeo = false;
        _propInstances = System.Array.Empty<PropInstanceData>();
        OnPropertyChanged(nameof(HasAddedMeshes));
        PropMarkers = null;
        ProbeMarkers = null;
        SelectedPropTreeItem = null;
        SelectedPropNode = null;
        SelectedProbe = null;
        SelectedPlaceableInfo = "";
        PlayParticlePreview = false;
        PlayAllParticles = false;
        CurrentParticlePlayback = null;
        SelectedChampionVfx = null;
        ChampionVfxSystems.Clear();
        HasChampionVfx = false;
        _vfxSystems = EmptyVfx;
        _vfxTextureCache.Clear(); _vfxTextureMultCache.Clear(); _vfxDistortionTextureCache.Clear(); _vfxColorTextureCache.Clear(); _vfxMeshCache.Clear();
        CurrentAnimation = null;
        AnimationTime = 0;
        Animation.Clear();
        MeshInspector.Clear();
        MapGeoInspector.Clear();
    }

    // ---- Data-driven map visibility layers ---------------------------------------------------------

    public sealed partial class VisibilityAxisViewModel : ObservableObject
    {
        private readonly MainWindowViewModel _owner;
        public MapVisibilityAxis Axis { get; }
        public string Name => Axis.Name;
        public IReadOnlyList<string> Options { get; }
        [ObservableProperty] private int _selectedIndex;

        internal VisibilityAxisViewModel(MainWindowViewModel owner, MapVisibilityAxis axis)
        {
            _owner = owner;
            Axis = axis;
            Options = new[] { "All" }.Concat(axis.Layers.Select(l => l.Name)).ToList();
        }

        public int SelectedBit => SelectedIndex <= 0 || SelectedIndex > Axis.Layers.Count
            ? 0 : Axis.Layers[SelectedIndex - 1].Bit;

        partial void OnSelectedIndexChanged(int value)
        {
            if (!_owner._visibilityUiLoading) _owner.ApplyMapVisibility();
        }
    }

    public ObservableCollection<VisibilityAxisViewModel> VisibilityAxes { get; } = new();
    [ObservableProperty] private bool _hasVisibilityAxes;
    private bool _visibilityUiLoading;

    private IReadOnlyDictionary<uint, int> CurrentVisibilitySelections =>
        VisibilityAxes.ToDictionary(a => a.Axis.DefinitionFieldHash, a => a.SelectedBit);

    private int CurrentPrimaryVisibilityBit => VisibilityAxes.FirstOrDefault(a => a.Axis.IsPrimary)?.SelectedBit ?? 0;

    private void RebuildVisibilityAxes(MapVisibilityDefinition definition, IReadOnlyList<int>? selectedIndices = null)
    {
        _visibilityUiLoading = true;
        try
        {
            VisibilityAxes.Clear();
            for (int i = 0; i < definition.Axes.Count; i++)
            {
                var vm = new VisibilityAxisViewModel(this, definition.Axes[i]);
                vm.SelectedIndex = selectedIndices is not null && i < selectedIndices.Count ? selectedIndices[i] : 0;
                VisibilityAxes.Add(vm);
            }
            HasVisibilityAxes = VisibilityAxes.Count > 0;
        }
        finally { _visibilityUiLoading = false; }
    }

    /// <summary>Compute per-group visibility from the map-defined axes and push it to the viewport.</summary>
    // M104: render regions (mapgeo v18 renderRegionHash). Off hides every region-assigned mesh, leaving
    // the region-independent base geometry — the fastest way to see what a region is contributing.
    [ObservableProperty] private bool _renderRegionsEnabled = true;
    [ObservableProperty] private bool _hasRenderRegions;
    partial void OnRenderRegionsEnabledChanged(bool value) => ApplyMapVisibility();

    private void ApplyMapVisibility()
    {
        if (_currentMap is not { } map) { CurrentModelSubmeshVisible = null; return; }
        var selections = CurrentVisibilitySelections;
        var resolver = _visibilityResolver ??= new MapVisibilityResolver(_mapControllers, _mapVisibility);
        var regionOf = map.Meshes.ToDictionary(m => m.Index, m => m.RegionHash);
        // M105: pending layer edits preview live — the group snapshot keeps the FILE's values, so the
        // check reads the mesh's effective (edited) mask/controller when there is one.
        var meshByIdx = map.Meshes.ToDictionary(m => m.Index);
        var hiddenByUser = MapContent.AllMapPieces
            .Where(p => !p.IsEditorVisible || p.IsDisabled || p.IsRemoved)
            .Select(p => p.MeshIndex).ToHashSet();
        HasRenderRegions = regionOf.Values.Any(r => r != 0);
        var vis = new bool[map.Groups.Count];
        for (int i = 0; i < vis.Length; i++)
        {
            var g = map.Groups[i];
            int flags = g.VisibilityFlags;
            uint ctrl = g.ControllerHash;
            if (g.MeshIndex >= 0 && meshByIdx.TryGetValue(g.MeshIndex, out var src))
            { flags = src.EffectiveVisibility; ctrl = src.EffectiveController; }
            vis[i] = resolver.IsVisible(flags, ctrl, selections);
            if (hiddenByUser.Contains(g.MeshIndex)) vis[i] = false;
            if (vis[i] && !RenderRegionsEnabled && g.MeshIndex >= 0
                && regionOf.TryGetValue(g.MeshIndex, out var region) && region != 0)
                vis[i] = false;
        }
        CurrentModelSubmeshVisible = vis;
        UpdateParticleMarkers();
        UpdatePlaceableMarkers();
        RefreshMeshDetails();  // keep the inspector's mesh details + "why visible/hidden" in sync
        PruneSelectionToVisible(); // hidden (filtered-out) meshes must not stay selected/transformable
        if (PlayAllParticles) RebuildParticlePlayback();
        if (AmbienceEnabled) UpdateAmbience(_lastCamPosForAudio, force: true);
    }

    /// <summary>Visibility diagnostic for the primary-selected mesh under the current map filters.</summary>
    [ObservableProperty] private string _meshVisibilityReason = "";

    /// <summary>The full mesh-details inspector for the selected mapgeo mesh (M33).</summary>
    public MeshDetailsViewModel MeshDetails { get; } = new();

    /// <summary>M101: scope the Materials tab to the selected mesh/meshes; empty selection = show all.</summary>
    private void RefreshMaterialMeshFilter()
    {
        if (_currentMap is not { } map || _selection.Count == 0) { MaterialEditor.SetMeshFilter(null); return; }
        var indices = _selection.Items.Select(m => m.Index).ToHashSet();
        MaterialEditor.SetMeshFilter(map.Groups
            .Where(g => indices.Contains(g.MeshIndex) && !string.IsNullOrEmpty(g.Material))
            .Select(g => g.Material));
    }

    private void RefreshMeshDetails()
    {
        RefreshMaterialMeshFilter();
        RefreshLayerEditor();   // M105
        if (_selection.Primary is not { } m || _visibilityResolver is null)
        { MeshVisibilityReason = ""; MeshDetails.Clear(); return; }
        // M105: diagnose the EFFECTIVE (edited) values so the details row matches what the viewport shows
        var d = _visibilityResolver.Resolve(m.EffectiveVisibility, m.EffectiveController, CurrentVisibilitySelections);
        MeshVisibilityReason = d.Reason;
        if (_selection.Count == 1)
        {
            string? material = _currentMap?.Groups.FirstOrDefault(g => g.MeshIndex == m.Index)?.Material;
            string? source = _currentMapEntry is { } e ? Path.GetFileName(MapGeoMaterialResolver.MaterialsBinPathFor(e.Path)) : null;
            MaterialProfile? profile = material is not null ? _currentMapProfiles?.GetValueOrDefault(material) : null;
            MeshDetails.Load(m, material, source, d, profile);
        }
        else MeshDetails.Clear(); // multi-select uses the batch panel, not per-mesh details
    }

    // ---- M102/M105: editable layer system for the selected meshes ----

    public sealed partial class LayerBitViewModel : ObservableObject
    {
        private readonly MainWindowViewModel _owner;
        public string Name { get; }
        public int Bit { get; }
        [ObservableProperty] private bool _isOn;
        internal bool Loading;

        public LayerBitViewModel(MainWindowViewModel owner, string name, int bit)
        { _owner = owner; Name = name; Bit = bit; }

        partial void OnIsOnChanged(bool value)
        {
            if (!Loading) _owner.SetLayerBitOnSelection(Bit, value);
        }
    }

    /// <summary>The primary map-axis checkboxes (state mirrors the primary selected mesh).</summary>
    public ObservableCollection<LayerBitViewModel> VisibilityLayerBits { get; } = new();

    public sealed partial class PlacementLayerBitViewModel : ObservableObject
    {
        private readonly MainWindowViewModel _owner;
        public string Name { get; }
        public int Bit { get; }
        public bool Loading;
        [ObservableProperty] private bool _isOn;
        public PlacementLayerBitViewModel(MainWindowViewModel owner, string name, int bit)
        { _owner = owner; Name = name; Bit = bit; }
        partial void OnIsOnChanged(bool value)
        { if (!Loading) _owner.SetPlacementLayerBit(Bit, value); }
    }

    public ObservableCollection<PlacementLayerBitViewModel> PlacementVisibilityLayerBits { get; } = new();
    [ObservableProperty] private bool _hasPlacementLayerSelection;
    [ObservableProperty] private string _placementLayerSummary = "";

    private static int PlacementFlags(MapOutlinerItemViewModel item) => item switch
    {
        ParticlePlacementViewModel p => p.EffectiveVisibilityFlags,
        MapSoundViewModel s => s.EffectiveVisibilityFlags,
        AnimatedPropViewModel p => p.EffectiveVisibilityFlags,
        CubemapProbeViewModel p => p.EffectiveVisibilityFlags,
        _ => 255,
    };

    private IEnumerable<MapOutlinerItemViewModel> SelectedPlacementLeaves() => _mapContentSelection.Where(item =>
        item is ParticlePlacementViewModel or MapSoundViewModel or AnimatedPropViewModel or CubemapProbeViewModel);

    private static void SetPlacementFlags(MapOutlinerItemViewModel item, int? flags)
    {
        switch (item)
        {
            case ParticlePlacementViewModel p: p.EditedVisibilityFlags = flags; break;
            case MapSoundViewModel s: s.EditedVisibilityFlags = flags; break;
            case AnimatedPropViewModel p: p.EditedVisibilityFlags = flags; break;
            case CubemapProbeViewModel p: p.EditedVisibilityFlags = flags; break;
        }
    }

    private void RefreshPlacementLayerEditor()
    {
        // Some maps author mVisibilityFlags without a discoverable controller/layer-name table. Keep the
        // field editable there too: named bits come from the shipping map when available, raw bit names are
        // the lossless fallback rather than hiding the feature altogether.
        IReadOnlyList<VisibilityLayer> declared = _mapVisibility.Primary?.Layers is { Count: > 0 } named
            ? named
            : Enumerable.Range(0, 8).Select(i => new VisibilityLayer($"Bit {i}", 1 << i)).ToArray();
        if (!PlacementVisibilityLayerBits.Select(b => b.Bit).SequenceEqual(declared.Select(d => d.Bit)))
        {
            PlacementVisibilityLayerBits.Clear();
            foreach (var layer in declared)
                PlacementVisibilityLayerBits.Add(new PlacementLayerBitViewModel(this, layer.Name, layer.Bit));
        }
        var selected = SelectedPlacementLeaves().ToList();
        HasPlacementLayerSelection = selected.Count > 0;
        if (!HasPlacementLayerSelection) { PlacementLayerSummary = ""; return; }
        int flags = PlacementFlags(selected[^1]);
        foreach (var bit in PlacementVisibilityLayerBits)
        {
            bit.Loading = true;
            bit.IsOn = (flags & bit.Bit) != 0;
            bit.Loading = false;
        }
        PlacementLayerSummary = $"{MapVisibility.Label(flags, _mapVisibility.Primary)} · mask 0b{Convert.ToString(flags & 0xFF, 2).PadLeft(8, '0')}"
            + (selected.Count > 1 ? $" · applies to {selected.Count} selected objects" : "");
    }

    private void SetPlacementLayerBit(int bit, bool on)
    {
        foreach (var item in SelectedPlacementLeaves().ToList())
        {
            int flags = PlacementFlags(item);
            SetPlacementFlags(item, on ? flags | bit : flags & ~bit);
        }
        RefreshPlacementLayerEditor();
    }

    [RelayCommand]
    private void SetPlacementLayersAll()
    { foreach (var item in SelectedPlacementLeaves().ToList()) SetPlacementFlags(item, 255); RefreshPlacementLayerEditor(); }

    [RelayCommand]
    private void ResetPlacementLayerEdits()
    { foreach (var item in SelectedPlacementLeaves().ToList()) SetPlacementFlags(item, null); RefreshPlacementLayerEditor(); }

    /// <summary>Controller choices for the selected mesh — "None" + every controller in the map's bins.</summary>
    public ObservableCollection<string> LayerControllerChoices { get; } = new();
    private readonly List<uint> _layerControllerHashes = new();
    [ObservableProperty] private int _selectedLayerControllerIndex = -1;
    [ObservableProperty] private bool _meshBackfaceDisabled;
    [ObservableProperty] private bool _hasLayerSelection;
    [ObservableProperty] private string _layerSummary = "";
    private bool _layerUiLoading;

    /// <summary>Refill the layer card from the primary selection (called from RefreshMeshDetails).</summary>
    private void RefreshLayerEditor()
    {
        _layerUiLoading = true;
        try
        {
            var declared = _mapVisibility.Primary?.Layers ?? Array.Empty<VisibilityLayer>();
            if (!VisibilityLayerBits.Select(b => b.Bit).SequenceEqual(declared.Select(d => d.Bit)))
            {
                VisibilityLayerBits.Clear();
                foreach (var layer in declared)
                    VisibilityLayerBits.Add(new LayerBitViewModel(this, layer.Name, layer.Bit));
            }

            if (_selection.Primary is not { } m || _currentMap is null)
            { HasLayerSelection = false; LayerSummary = ""; return; }

            HasLayerSelection = true;
            int flags = m.EffectiveVisibility;
            foreach (var b in VisibilityLayerBits)
            {
                b.Loading = true;
                b.IsOn = (flags & b.Bit) != 0;
                b.Loading = false;
            }
            MeshBackfaceDisabled = m.EffectiveDisableBackface;

            // controller list (rebuilt when the map's controllers change)
            if (LayerControllerChoices.Count == 0 && _mapControllers is { } mc)
            {
                LayerControllerChoices.Add("None (always in layer system)");
                _layerControllerHashes.Clear();
                _layerControllerHashes.Add(0);
                foreach (var ci in mc.List())
                {
                    LayerControllerChoices.Add(ci.Label);
                    _layerControllerHashes.Add(ci.Hash);
                }
            }
            int idx = _layerControllerHashes.IndexOf(m.EffectiveController);
            SelectedLayerControllerIndex = idx;   // -1 = a controller the bins don't list; combo shows empty

            int selCount = _selection.Count;
            int edited = _currentMap.Meshes.Count(x => x.HasLayerEdit);
            LayerSummary = $"{MapVisibility.Label(flags, _mapVisibility.Primary)} · mask 0b{Convert.ToString(flags & 0xFF, 2).PadLeft(8, '0')}"
                           + (selCount > 1 ? $" · applies to {selCount} selected meshes" : "")
                           + (edited > 0 ? $" · {edited} unsaved layer edit(s)" : "");
        }
        finally { _layerUiLoading = false; }
    }

    /// <summary>Set/clear one primary visibility bit on every selected mesh (one undo step).</summary>
    internal void SetLayerBitOnSelection(int bit, bool on)
    {
        if (_layerUiLoading) return;
        string name = _mapVisibility.Primary?.Layers.FirstOrDefault(d => d.Bit == bit).Name ?? $"bit {bit}";
        ApplyLayerEdit($"{(on ? "Add to" : "Remove from")} {name} Layer", m =>
        {
            int flags = m.EffectiveVisibility;
            m.VisibilityEdit = on ? flags | bit : flags & ~bit;
        });
    }

    partial void OnSelectedLayerControllerIndexChanged(int value)
    {
        if (_layerUiLoading || value < 0 || value >= _layerControllerHashes.Count) return;
        uint hash = _layerControllerHashes[value];
        ApplyLayerEdit(hash == 0 ? "Clear Visibility Controller" : "Assign Visibility Controller",
            m => m.ControllerEdit = hash);
    }

    partial void OnMeshBackfaceDisabledChanged(bool value)
    {
        if (_layerUiLoading) return;
        ApplyLayerEdit(value ? "Disable Backface Culling" : "Enable Backface Culling",
            m => m.BackfaceEdit = value);
    }

    [RelayCommand]
    private void SetLayersAll() => ApplyLayerEdit("Show On All Layers", m => m.VisibilityEdit = 255);

    [RelayCommand]
    private void ResetLayerEdits() => ApplyLayerEdit("Reset Layer Edits", m =>
    { m.VisibilityEdit = null; m.ControllerEdit = null; m.BackfaceEdit = null; });

    /// <summary>Run one mutation over the selection as a single undoable command, then refresh.</summary>
    private void ApplyLayerEdit(string name, Action<MapGeoMesh> mutate)
    {
        if (_currentMap is not { } map || _selection.Count == 0) return;
        var entries = new List<(MapGeoMesh, MeshLayerCommand.State, MeshLayerCommand.State)>();
        foreach (var m in _selection.Items)
        {
            var before = MeshLayerCommand.State.Capture(m);
            mutate(m);
            entries.Add((m, before, MeshLayerCommand.State.Capture(m)));
        }
        UndoService.PushApplied(new MeshLayerCommand(name, map, entries, OnLayerEditApplied));
        OnLayerEditApplied();
    }

    private void OnLayerEditApplied()
    {
        if (_currentMap is { } map)
            HasMapMoves = MapGeoWriter.HasMoves(map.Meshes) || MapGeoLayerWriter.HasEdits(map.Meshes);
        ApplyMapVisibility();   // re-evaluates effective flags and refreshes the layer card via RefreshMeshDetails
    }

    /// <summary>Drop any selected meshes that the current visibility filter hides (a mesh is visible if
    /// at least one of its submesh groups is visible), so batch transforms never touch filtered geometry.</summary>
    private void PruneSelectionToVisible()
    {
        if (_selection.IsEmpty || _currentMap is not { } map || CurrentModelSubmeshVisible is not { } vis) return;
        var visibleMeshIndices = new HashSet<int>();
        int n = System.Math.Min(map.Groups.Count, vis.Count);
        for (int i = 0; i < n; i++)
            if (vis[i]) visibleMeshIndices.Add(map.Groups[i].MeshIndex);
        var keep = _selection.Items.Where(m => visibleMeshIndices.Contains(m.Index)).ToList();
        if (keep.Count != _selection.Count) _selection.SetMany(keep);
    }

    /// <summary>Tools ▸ Map Material Diagnostics — scan the loaded map's bins + mapgeo and write an honest
    /// report (classes, exposed vs unknown fields, lighting/lightmap/visibility signals) to
    /// <c>.reyengine/reports/materials_diagnostics_&lt;map&gt;.json</c> (M33).</summary>
    /// <summary>M170: verify every MapCubemapProbe points at a real DDS cubemap. A plain 2D texture
    /// bound where the engine expects 6 faces crashes the game at load — the fault that used to be
    /// blamed on wide WAD overlays (ltk-manager#305).</summary>
    [RelayCommand]
    private void CheckCubemapProbes()
    {
        if (CurrentModelProbes is not { Count: > 0 } probes)
        { _log.Warn("Cubemaps", "No cubemap probes in the loaded map (open a map's materials .bin first)."); return; }

        var issues = Formats.MapGeo.CubemapProbeValidator.Validate(probes, path =>
        {
            try { return ReadAssetByPath(path); } catch { return null; }
        });

        if (issues.Count == 0)
        { _log.Success("Cubemaps", $"All {probes.Count} cubemap probe(s) point at valid DDS cubemaps."); return; }

        _log.Error("Cubemaps", $"{issues.Count} of {probes.Count} cubemap probe(s) would fail to bind — this crashes the game at load:");
        foreach (var i in issues.Take(20))
            _log.Error("Cubemaps", $"   '{i.ProbeName}' -> {i.TexturePath}  {i.Problem}");
        if (issues.Count > 20) _log.Error("Cubemaps", $"   (+{issues.Count - 20} more)");
        _log.Info("Cubemaps", "Fix: re-export the texture as a DDS cubemap (6 square faces, DDSCAPS2_CUBEMAP set), " +
                              "or point the probe at one of Riot's existing cubemaps.");
    }

    [RelayCommand]
    private void MapMaterialDiagnostics()
    {
        if (_currentMap is null || _currentMapEntry is not { } entry || _currentMapBytes is null)
        { _log.Warn("Diagnostics", "Load a map first, then run Map Material Diagnostics."); return; }
        try
        {
            var dir = entry.Path[..(entry.Path.LastIndexOf('/') + 1)];
            var bins = new List<(string, byte[])>();
            foreach (var e in AssetEntries.Where(e => e.IsResolved
                         && e.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                         && e.Path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)))
            { try { bins.Add((e.Path, ReadAsset(e.PathHash))); } catch { /* skip unreadable */ } }

            var report = MapDiagnosticsReport.Build(entry.DisplayName, bins, _currentMapBytes, ResolveBinName);
            var safe = new string(entry.DisplayName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
            var path = Path.Combine(ProjectWorkspace.ReportsDir(Project), $"materials_diagnostics_{safe}.json");
            File.WriteAllText(path, report.ToJson());
            _log.Success("Diagnostics", $"Map diagnostics written: {path}");
            foreach (var f in report.LightmapFindings.Concat(report.LightingFindings)
                         .Concat(report.VisibilityFindings).Concat(report.PreviewFindings))
                _log.Info("Diagnostics", f);
        }
        catch (Exception ex) { _log.Error("Diagnostics", ex.Message); }
    }

    /// <summary>Read the shipping map's visibility axes, then index controller graphs from sibling bins.</summary>
    private void BuildMapVisibility(string mapgeoPath, MapGeoAsset map)
    {
        var dir = mapgeoPath[..(mapgeoPath.LastIndexOf('/') + 1)];
        var bins = new List<byte[]>();
        WadAssetEntry? primaryMaterials = TryResolveMaterialsBin(mapgeoPath, out var resolvedMaterials)
            ? resolvedMaterials : null;
        foreach (var e in AssetEntries.Where(e => e.IsResolved
                     && e.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                     && e.Path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)
                     && (primaryMaterials is null || e.PathHash != primaryMaterials.PathHash))
                 .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
        {
            try { bins.Add(ReadAsset(e.PathHash)); } catch { /* skip unreadable bins */ }
        }
        // The mapgeo's own effective .materials.bin is authoritative for duplicate controller object
        // hashes. Add it last because MapVisibilityControllers deliberately uses later-bin-wins merging.
        // This matters for old/custom rifts whose controller graphs differ from current Riot Map11.
        if (primaryMaterials is not null)
            try { bins.Add(ReadAsset(primaryMaterials.PathHash)); } catch { /* no controller data */ }
        byte[]? shippingBin = null;
        var match = System.Text.RegularExpressions.Regex.Match(mapgeoPath, @"/mapgeometry/map(?<id>\d+)/", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success)
            shippingBin = ReadAssetByPath($"data/maps/shipping/map{match.Groups["id"].Value}/map{match.Groups["id"].Value}.bin");
        _mapVisibility = MapVisibility.Parse(shippingBin, ResolveBinName);
        if (!_mapVisibility.HasAxes) _mapVisibility = MapVisibility.Infer(map.Meshes.Select(m => m.VisibilityFlags));

        _mapControllers = MapVisibilityControllers.Build(bins, _mapVisibility);
        _visibilityResolver = new MapVisibilityResolver(_mapControllers, _mapVisibility);
        RebuildVisibilityAxes(_mapVisibility);
        LayerControllerChoices.Clear();
        _layerControllerHashes.Clear();
        VisibilityLayerBits.Clear();

        string axes = _mapVisibility.HasAxes
            ? string.Join(", ", _mapVisibility.Axes.Select(a => $"{a.Name} ({a.Layers.Count} states, initial {a.InitialMask})"))
            : "none";
        _log.Info("MapGeo", $"Visibility axes: {axes}; {_mapControllers.Count} controller(s) from {bins.Count} sibling bin(s).");
    }

    /// <summary>Build the Map Content layer-group outline (Meshes → Layer Groups → mesh names).</summary>
    private void BuildMapLayerGroups(MapGeoAsset map)
    {
        var groups = map.Meshes
            .GroupBy(m => m.VisibilityFlags)
            .Select(g =>
            {
                var vm = new MapLayerGroupViewModel
                {
                    Name = $"{MapVisibility.Label(g.Key, _mapVisibility.Primary)} — {g.Count()} mesh(es)",
                    Bit = g.Key,
                };
                foreach (var mesh in g.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
                    vm.Meshes.Add(new MapPieceViewModel { Name = mesh.Name, Info = "", MeshIndex = mesh.Index });
                return vm;
            })
            .OrderByDescending(vm => vm.Meshes.Count)
            .ToList();
        MapContent.SetLayerGroups(groups);
    }

    // ---- Mesh move / rotate / scale (M25/M26) ---------------------------
    // "Position" shown/edited is the mesh's own pivot (local bbox center) + its offset — the world-space
    // location of the mesh's center, which stays meaningful independent of any applied rotation/scale.

    private byte[]? _currentMapBytes;
    private WadAssetEntry? _currentMapEntry;

    [ObservableProperty] private MapGeoMesh? _selectedMapMesh;
    [ObservableProperty] private object? _selectedTreeItem;
    [ObservableProperty] private string _meshMoveX = "0";
    [ObservableProperty] private string _meshMoveY = "0";
    [ObservableProperty] private string _meshMoveZ = "0";
    [ObservableProperty] private string _meshRotateX = "0";
    [ObservableProperty] private string _meshRotateY = "0";
    [ObservableProperty] private string _meshRotateZ = "0";
    [ObservableProperty] private string _meshScaleX = "1";
    [ObservableProperty] private string _meshScaleY = "1";
    [ObservableProperty] private string _meshScaleZ = "1";
    [ObservableProperty] private int _meshVerticesRevision;
    [ObservableProperty] private bool _hasMapMoves;
    [ObservableProperty] private bool _hasMapGeo;   // M79: a .mapgeo is loaded (enables Add Mesh to Map)

    // ---- Multi-selection + batch transform (M30) -----------------------
    private readonly SelectionSet<MapGeoMesh> _selection = new();
    private bool _syncingTreeSelection;   // reentrancy guard: tree<->selection sync must not recurse
    private readonly List<MapOutlinerItemViewModel> _mapContentSelection = new();
    private MapOutlinerItemViewModel? _mapContentAnchor;
    private bool _outlinerMultiSelecting;

    public IReadOnlyList<MapOutlinerItemViewModel> SelectedMapContentItems => _mapContentSelection;
    public bool HasMapContentSelection => _mapContentSelection.Count > 0;
    public string MapContentSelectionText => _mapContentSelection.Count switch
    {
        0 => "",
        1 => "1 selected",
        var count => $"{count} selected",
    };

    private void RaiseMapContentSelection()
    {
        OnPropertyChanged(nameof(SelectedMapContentItems));
        OnPropertyChanged(nameof(HasMapContentSelection));
        OnPropertyChanged(nameof(MapContentSelectionText));
        DeleteMapContentSelectionCommand.NotifyCanExecuteChanged();
        DisableMapContentSelectionCommand.NotifyCanExecuteChanged();
        EnableMapContentSelectionCommand.NotifyCanExecuteChanged();
        HideMapContentSelectionCommand.NotifyCanExecuteChanged();
        ShowMapContentSelectionCommand.NotifyCanExecuteChanged();
    }

    private void SetMapContentSelection(IEnumerable<MapOutlinerItemViewModel> items, MapOutlinerItemViewModel? anchor)
    {
        foreach (var old in _mapContentSelection) old.IsSelected = false;
        _mapContentSelection.Clear();
        foreach (var item in items.Distinct()) { item.IsSelected = true; _mapContentSelection.Add(item); }
        _mapContentAnchor = anchor;
        RaiseMapContentSelection();
        RefreshPlacementLayerEditor();
    }

    private List<MapOutlinerItemViewModel> FlatMapContentItems() =>
        MapContent.AllMapPieces.Cast<MapOutlinerItemViewModel>()
            .Concat(MapContent.AllParticles)
            .Concat(MapContent.AllProps)
            .Concat(MapContent.Probes)
            .Concat(MapContent.Sounds)
            .Concat(MapContent.AddedMeshes)
            .ToList();

    /// <summary>Ctrl/Shift selection owned by the outliner because Avalonia TreeView itself is single-select.</summary>
    public void SelectMapContentFromTree(MapOutlinerItemViewModel item, bool toggle, bool range)
    {
        var next = _mapContentSelection.ToList();
        if (range)
        {
            var flat = FlatMapContentItems();
            int to = flat.IndexOf(item);
            int from = _mapContentAnchor is null ? to : flat.IndexOf(_mapContentAnchor);
            if (to >= 0)
            {
                if (from < 0) from = to;
                next = flat.Skip(Math.Min(from, to)).Take(Math.Abs(to - from) + 1).ToList();
            }
        }
        else if (toggle)
        {
            if (!next.Remove(item)) next.Add(item);
            _mapContentAnchor = item;
        }
        else next = new List<MapOutlinerItemViewModel> { item };

        SetMapContentSelection(next, _mapContentAnchor ?? item);
        var selectedMeshes = _mapContentSelection.OfType<MapPieceViewModel>()
            .Select(p => _currentMap?.Meshes.FirstOrDefault(m => m.Index == p.MeshIndex))
            .Where(m => m is not null).Cast<MapGeoMesh>().ToList();
        _outlinerMultiSelecting = true;
        try
        {
            _selection.SetMany(selectedMeshes);
            SelectedOutlinerItem = item;
        }
        finally { _outlinerMultiSelecting = false; }
    }

    private bool CanEditMapContentSelection() => _mapContentSelection.Count > 0;

    [RelayCommand(CanExecute = nameof(CanEditMapContentSelection))]
    private void HideMapContentSelection()
    { foreach (var item in _mapContentSelection.ToList()) item.IsEditorVisible = false; }

    [RelayCommand(CanExecute = nameof(CanEditMapContentSelection))]
    private void ShowMapContentSelection()
    { foreach (var item in _mapContentSelection.ToList()) item.IsEditorVisible = true; }

    [RelayCommand(CanExecute = nameof(CanEditMapContentSelection))]
    private void DisableMapContentSelection()
    { foreach (var item in _mapContentSelection.ToList()) item.IsDisabled = true; }

    [RelayCommand(CanExecute = nameof(CanEditMapContentSelection))]
    private void EnableMapContentSelection()
    { foreach (var item in _mapContentSelection.ToList()) item.IsDisabled = false; }

    [RelayCommand(CanExecute = nameof(CanEditMapContentSelection))]
    private async Task DeleteMapContentSelection()
    {
        var selected = _mapContentSelection.ToList();
        if (selected.Count == 0) return;
        if (PromptOwner is not null && !await Views.PromptWindow.ConfirmAsync(PromptOwner, "Delete Map Objects",
                $"Mark {selected.Count} selected object(s) for deletion?\n\nThe viewport updates immediately. The map files are changed only when you save map edits.", "Delete"))
            return;

        foreach (var item in selected)
        {
            if (item is AddedMapMeshViewModel added) MapContent.AddedMeshes.Remove(added);
            else item.IsRemoved = true;
        }
        OnPropertyChanged(nameof(HasAddedMeshes));
        PublishAddedMeshPreview();
        ApplyMapVisibility();
        _log.Info("Map Content", $"Marked {selected.Count} object(s) for deletion. Save Map Content Edits to persist.");
    }

    [RelayCommand]
    private async Task SaveMapContentEdits()
    {
        if (HasMapMoves || MapContent.AllMapPieces.Any(p => p.IsRemoved) || MapContent.AddedMeshes.Count > 0)
            await SaveMeshMoves();
        if (HasParticleMoves) await SaveParticleMoves();
    }

    [ObservableProperty] private IReadOnlyList<(System.Numerics.Vector3 min, System.Numerics.Vector3 max)>? _selectionBoxes;
    [ObservableProperty] private System.Numerics.Vector3? _groupBoundsMin;
    [ObservableProperty] private System.Numerics.Vector3? _groupBoundsMax;
    [ObservableProperty] private System.Numerics.Vector3? _gizmoPivot;   // selection center = gizmo origin
    [ObservableProperty] private bool _isMultiSelect;                    // 2+ meshes → batch inspector
    [ObservableProperty] private bool _isSingleSelect;                   // exactly 1 → single-mesh inspector
    [ObservableProperty] private string _selectionStatus = "";          // e.g. "3 meshes selected"

    // Batch transform deltas — applied to the whole selection around its center (blank/identity = no-op).
    [ObservableProperty] private string _batchMoveX = "0";
    [ObservableProperty] private string _batchMoveY = "0";
    [ObservableProperty] private string _batchMoveZ = "0";
    [ObservableProperty] private string _batchRotateX = "0";
    [ObservableProperty] private string _batchRotateY = "0";
    [ObservableProperty] private string _batchRotateZ = "0";
    [ObservableProperty] private string _batchScaleX = "1";
    [ObservableProperty] private string _batchScaleY = "1";
    [ObservableProperty] private string _batchScaleZ = "1";

    /// <summary>M51: single selection over the unified hierarchy — routes by node type (mesh piece,
    /// particle placement, animated prop, probe). Folder/group clicks are ignored.</summary>
    [ObservableProperty] private object? _selectedOutlinerItem;
    partial void OnSelectedOutlinerItemChanged(object? value)
    {
        if (_syncingTreeSelection) return;
        if (!_outlinerMultiSelecting && value is MapOutlinerItemViewModel leaf)
            SetMapContentSelection(new[] { leaf }, leaf);
        switch (value)
        {
            case MapPieceViewModel { MeshIndex: >= 0 } p when _currentMap is { } map
                && map.Meshes.FirstOrDefault(x => x.Index == p.MeshIndex) is { } m:
                if (!_outlinerMultiSelecting) _selection.SetSingle(m);
                break;
            case ParticlePlacementViewModel pp:
                SelectedParticleNode = pp;
                break;
            case AnimatedPropViewModel ap:
                SelectedPropTreeItem = ap;
                break;
            case CubemapProbeViewModel pr:
                SelectedProbe = pr;
                break;
            case PointLightViewModel lt:   // M153: lights are scene objects now
                _selection.Clear();
                if (SelectedParticleTreeItem is not null) SelectedParticleTreeItem = null;
                if (SelectedParticleNode is not null) SelectedParticleNode = null;
                if (SelectedPropTreeItem is not null) SelectedPropTreeItem = null;
                if (SelectedSound is not null) SelectedSound = null;
                SelectedLight = lt;
                break;
            case MapSoundViewModel snd:   // M55
                _selection.Clear();
                if (SelectedParticleTreeItem is not null) SelectedParticleTreeItem = null;
                if (SelectedParticleNode is not null) SelectedParticleNode = null;   // M76: viewport picks bypass the tree item
                if (SelectedPropTreeItem is not null) SelectedPropTreeItem = null;
                if (SelectedProbe is not null) SelectedProbe = null;
                SelectedParticleMarker = snd.Position;   // M55b: highlight only — camera stays
                SelectedSound = snd;                      // M56: enables the SOUND card (Play button)
                SelectedPlaceableInfo = "";
                GizmoPivot = snd.Position;                // M75: sounds are gizmo-movable
                break;
            case AddedMapMeshViewModel am:   // M79: imported mesh queued for append — gizmo-movable
                _selection.Clear();
                if (SelectedParticleNode is not null) SelectedParticleNode = null;
                if (SelectedSound is not null) SelectedSound = null;
                SelectedAddedMesh = am;
                GizmoPivot = am.PivotWorld;
                SelectedPlaceableInfo = $"{am.Name}\n{am.Info}";
                OnPropertyChanged(nameof(MapMaterialNames));
                break;
            case BucketGridViewModel bg:  // M55/M77b: info only — the toolbar toggle controls visibility
                SelectedPlaceableInfo = $"{bg.Name}\n{bg.Info}";
                break;
        }
    }

    partial void OnSelectedTreeItemChanged(object? value)
    {
        if (_syncingTreeSelection) return; // sync is pushing the selection INTO the tree — don't loop back
        // Match by MapGeoMesh.Index (the env-mesh index), not list position — they diverge if any mesh
        // failed to decode. A plain tree click is a single-select (Ctrl+click toggling is handled separately).
        if (value is MapPieceViewModel { MeshIndex: >= 0 } p && _currentMap is { } map
            && map.Meshes.FirstOrDefault(x => x.Index == p.MeshIndex) is { } m)
            _selection.SetSingle(m);
        else if (value is null)
            _selection.Clear();
    }

    /// <summary>
    /// Blender/UE-style viewport click-selection: cast the pick ray at the map's visible triangles and
    /// select the nearest-hit mesh. Plain click = single-select; <paramref name="additive"/> (Ctrl) toggles
    /// the hit mesh in/out of the current set; an empty non-additive click clears the selection.
    /// </summary>
    /// <summary>M55: click-select ANY scene object — meshes (triangles) or placeable icon markers
    /// (particles/props/probes, ray-vs-sphere at the marker's world size); nearest hit wins.</summary>
    public void SelectAnyFromViewport(System.Numerics.Vector3 rayOrigin, System.Numerics.Vector3 rayDir, bool additive = false,
        Func<System.Numerics.Vector3, System.Numerics.Vector2?>? projectToScreen = null,
        System.Numerics.Vector2? clickScreenPx = null)
    {
        // M76 UE-style picking: placeable icons hit in SCREEN space first (within a pixel radius of the
        // drawn icon), so they're easy to click at ANY zoom — a distant marker no longer needs a
        // pixel-perfect ray. Nearest icon on screen wins; icons draw on top, so they beat mesh faces.
        if (!additive && projectToScreen is not null && clickScreenPx is { } px)
        {
            const float PickPixels = 18f;
            object? bestPx = null;
            float bestPxD = float.MaxValue;
            void TestPx(object node, System.Numerics.Vector3 pos)
            {
                if (projectToScreen(pos) is not { } s) return;
                float d = System.Numerics.Vector2.Distance(s, px);
                if (d <= PickPixels && d < bestPxD) { bestPxD = d; bestPx = node; }
            }
            if (ShowParticles && MapContent.HasParticles)
                foreach (var p in MapContent.AllParticles.Where(v => v.IsEditorVisible && !v.IsDisabled && !v.IsRemoved
                    && IsParticleVisible(v.Placement, v.EffectiveVisibilityFlags))) TestPx(p, p.CurrentPosition);
            if (ShowPlaceables && MapContent.HasProps)
                foreach (var p in MapContent.AllProps.Where(v => v.IsEditorVisible && !v.IsDisabled && !v.IsRemoved)) TestPx(p, p.Position);
            if (ShowPlaceables && MapContent.HasProbes)
                foreach (var p in MapContent.Probes.Where(v => v.IsEditorVisible && !v.IsDisabled && !v.IsRemoved)) TestPx(p, p.Position);
            if (ShowPlaceables && MapContent.HasSounds)
                foreach (var s in MapContent.Sounds.Where(v => v.IsEditorVisible && !v.IsDisabled && !v.IsRemoved
                    && IsSoundVisible(v.Sound, v.EffectiveVisibilityFlags))) TestPx(s, s.Position);
            // M123e: staged (not yet saved) meshes are click-selectable at their world center
            foreach (var a in MapContent.AddedMeshes.Where(v => v.IsEditorVisible && !v.IsDisabled && !v.IsRemoved)) TestPx(a, a.LocalCenter + a.Offset);
            if (bestPx is not null) { SelectedOutlinerItem = bestPx; return; }
        }

        rayDir = System.Numerics.Vector3.Normalize(rayDir);   // same t units for mesh + marker tests
        // mesh hit distance (float.MaxValue when none)
        float meshT = float.MaxValue;
        if (_currentMap is { } map0 && map0.Groups.Count > 0
            && RayIndex?.ClosestHit(rayOrigin, rayDir, CurrentModelSubmeshVisible) is { } meshHit)
            meshT = meshHit.Distance;

        // placeable markers: same size formula the viewport uses for the icons (Mesh.Radius-scaled)
        float radius = CurrentMesh is { } cm ? Math.Clamp(cm.Radius * 0.004f, 4f, 90f) * 1.6f : 40f;
        object? bestNode = null;
        float bestT = float.MaxValue;
        void Test(object node, System.Numerics.Vector3 pos)
        {
            var toC = pos - rayOrigin;
            float t = System.Numerics.Vector3.Dot(toC, rayDir);            // rayDir is normalized
            if (t <= 0f || t >= bestT) return;
            float d = (toC - rayDir * t).Length();                          // perpendicular distance
            if (d <= radius) { bestT = t; bestNode = node; }
        }
        if (ShowParticles && MapContent.HasParticles && !additive)
            foreach (var p in MapContent.AllParticles.Where(v => v.IsEditorVisible && !v.IsDisabled && !v.IsRemoved
                && IsParticleVisible(v.Placement, v.EffectiveVisibilityFlags))) Test(p, p.CurrentPosition);
        if (ShowPlaceables && MapContent.HasProps && !additive)
            foreach (var p in MapContent.AllProps.Where(v => v.IsEditorVisible && !v.IsDisabled && !v.IsRemoved)) Test(p, p.Position);
        if (ShowPlaceables && MapContent.HasProbes && !additive)
            foreach (var p in MapContent.Probes.Where(v => v.IsEditorVisible && !v.IsDisabled && !v.IsRemoved)) Test(p, p.Position);
        if (ShowPlaceables && MapContent.HasSounds && !additive)
            foreach (var s in MapContent.Sounds.Where(v => v.IsEditorVisible && !v.IsDisabled && !v.IsRemoved
                && IsSoundVisible(v.Sound, v.EffectiveVisibilityFlags))) Test(s, s.Position);   // M55/M60
        // M153: point lights pick like any other placement, so you can click one in the viewport.
        if (ShowDynamicLights && !additive)
            foreach (var l in MapContent.Lights) Test(l, l.Position);

        // nearest placeable beats a farther mesh face (icons draw on top, so this matches what you see)
        if (bestNode is not null && bestT < meshT)
        {
            SelectedOutlinerItem = bestNode;   // routes by type + highlights the hierarchy
            return;
        }
        SelectMeshFromViewport(rayOrigin, rayDir, additive);
    }

    public void SelectMeshFromViewport(System.Numerics.Vector3 rayOrigin, System.Numerics.Vector3 rayDir, bool additive = false)
    {
        if (_currentMap is not { } map || map.Groups.Count == 0) return;
        int hit = RayIndex?.ClosestHit(rayOrigin, rayDir, CurrentModelSubmeshVisible)?.Submesh ?? -1;
        if (hit < 0)
        {
            if (!additive)
            {
                _selection.Clear(); // empty click clears; Ctrl+empty keeps the set (UE/Blender)
                ClearPlaceableSelection(); // M76: an empty click also deselects particles/sounds/props/probes
            }
            return;
        }
        int meshIndex = map.Groups[hit].MeshIndex;
        var mesh = map.Meshes.FirstOrDefault(x => x.Index == meshIndex);
        if (mesh is null) return;
        if (additive) _selection.Toggle(mesh);
        else _selection.SetSingle(mesh);
        SetMapContentSelection(MapContent.AllMapPieces
            .Where(p => _selection.Items.Any(selected => selected.Index == p.MeshIndex)).ToList(),
            MapContent.AllMapPieces.FirstOrDefault(p => p.MeshIndex == _selection.Primary?.Index));
        var name = mesh.Name?.Length > 0 ? mesh.Name : $"#{meshIndex}";
        _log.Info("MapGeo", additive ? $"{(_selection.Contains(mesh) ? "Added" : "Removed")} '{name}' ({_selection.Count} selected)."
                                      : $"Selected '{name}' (viewport click).");
    }

    /// <summary>Ctrl+click on a Map Content tree row: toggle that mesh in/out of the selection.</summary>
    public void ToggleMeshSelectionFromTree(MapPieceViewModel piece)
    {
        if (_currentMap is not { } map || piece.MeshIndex < 0) return;
        if (map.Meshes.FirstOrDefault(x => x.Index == piece.MeshIndex) is { } m)
        {
            _selection.Toggle(m);
            SetMapContentSelection(MapContent.AllMapPieces
                .Where(p => _selection.Items.Any(selected => selected.Index == p.MeshIndex)).ToList(), piece);
        }
    }

    /// <summary>Central selection handler (raised by <see cref="SelectionSet{T}.Changed"/>): re-derive the
    /// primary mesh, single/multi flags, status text, tree highlight, and all viewport visuals.</summary>
    private void OnMeshSelectionChanged()
    {
        var primary = _selection.Primary;
        SelectedMapMesh = primary;
        IsMultiSelect = _selection.IsMulti;
        IsSingleSelect = _selection.Count == 1;
        SelectionStatus = _selection.Count switch { 0 => "", 1 => "1 mesh selected", var n => $"{n} meshes selected" };
        if (primary is not null) RefreshMeshTransformFields(primary);
        SyncTreeHighlight();
        RefreshSelectionVisuals();
        RefreshMeshDetails();
    }

    /// <summary>M76: deselect every placeable (particle/sound/prop/probe) — used when the user clicks
    /// empty space, so no stale placement keeps its gizmo/inspector alive (UE-style).</summary>
    private void ClearPlaceableSelection()
    {
        if (SelectedParticleTreeItem is not null) SelectedParticleTreeItem = null;
        if (SelectedParticleNode is not null) SelectedParticleNode = null;
        if (SelectedPropTreeItem is not null) SelectedPropTreeItem = null;
        if (SelectedProbe is not null) SelectedProbe = null;
        if (SelectedSound is not null) SelectedSound = null;
        if (SelectedAddedMesh is not null) SelectedAddedMesh = null;   // M79
        if (SelectedLight is not null) SelectedLight = null;           // M153
        SelectedParticleMarker = null;
        SelectedPlaceableInfo = "";
        if (_selection.IsEmpty) GizmoPivot = null;
        _syncingTreeSelection = true;
        SelectedOutlinerItem = null;   // drop the outliner row highlight too
        _syncingTreeSelection = false;
        SetMapContentSelection(Array.Empty<MapOutlinerItemViewModel>(), null);
    }

    /// <summary>Mirror the SelectionSet onto the tree: mark selected rows' <c>IsSelected</c>, and keep the
    /// TreeView's single SelectedItem pointed at the primary (guarded so it doesn't feed back).</summary>
    private void SyncTreeHighlight()
    {
        var selectedIndices = _selection.Items.Select(m => m.Index).ToHashSet();
        MapPieceViewModel? primaryPiece = null;
        foreach (var g in MapContent.LayerGroups)
            foreach (var piece in g.Meshes)
            {
                piece.IsSelected = piece.MeshIndex >= 0 && selectedIndices.Contains(piece.MeshIndex);
                if (piece.IsSelected && _selection.Primary is { } pm && piece.MeshIndex == pm.Index) primaryPiece = piece;
            }
        _syncingTreeSelection = true;
        SelectedTreeItem = primaryPiece; // scrolls/anchors the tree to the primary without re-triggering select
        SelectedOutlinerItem = primaryPiece; // M51: unified hierarchy mirrors the selection
        _syncingTreeSelection = false;
        if (!_outlinerMultiSelecting && _mapContentSelection.All(item => item is MapPieceViewModel))
            SetMapContentSelection(MapContent.AllMapPieces.Where(p => p.IsSelected).ToList(), primaryPiece);
    }

    /// <summary>Recompute the per-mesh selection highlight boxes (live vertex bounds), the combined group
    /// bounds, and the gizmo pivot (selection center). Call after selecting and after any vertex-moving edit.</summary>
    private void RefreshSelectionVisuals()
    {
        if (_selection.IsEmpty || _currentMap is not { } map)
        {
            SelectionBoxes = null; GroupBoundsMin = GroupBoundsMax = GizmoPivot = null;
            SelectedSubmeshIndices = null; SelectedMeshMaterials = null; HasSelectedMeshMaterials = false;
            return;
        }

        // M50b: a mesh selection is EXCLUSIVE — deselect placeables so the inspector doesn't keep
        // showing the previously-selected particle/prop/probe next to the mesh sections (Unity-style).
        // M76: also clear the DIRECTLY-set particle node (viewport icon picks bypass the tree item, so
        // clearing only SelectedParticleTreeItem left the particle selected under a new mesh selection).
        if (SelectedParticleNode is not null) SelectedParticleNode = null;
        if (SelectedAddedMesh is not null) SelectedAddedMesh = null;   // M79
        SelectedParticleMarker = null;
        if (SelectedParticleTreeItem is not null) SelectedParticleTreeItem = null;
        if (SelectedPropTreeItem is not null) SelectedPropTreeItem = null;
        if (SelectedProbe is not null) SelectedProbe = null;
        if (SelectedSound is not null) SelectedSound = null;   // M56
        SelectedPlaceableInfo = "";

        // M50b: outline highlight (mesh wireframe overlay) + the selection's assigned materials.
        var meshIdx = _selection.Items.Select(m => m.Index).ToHashSet();
        var subIdx = new List<int>();
        var mats = new List<MeshMaterialSlotViewModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < map.Groups.Count; i++)
        {
            if (!meshIdx.Contains(map.Groups[i].MeshIndex)) continue;
            subIdx.Add(i);
            var name = map.Groups[i].Material;
            if (name.Length > 0 && seen.Add(name))
                mats.Add(new MeshMaterialSlotViewModel(name,
                    _currentMapProfiles?.GetValueOrDefault(name)?.RenderStateSummary ?? ""));
        }
        SelectedSubmeshIndices = subIdx;
        SelectedMeshMaterials = mats;
        HasSelectedMeshMaterials = mats.Count > 0;
        var boxes = new List<(System.Numerics.Vector3 min, System.Numerics.Vector3 max)>(_selection.Count);
        var gmin = new System.Numerics.Vector3(float.MaxValue);
        var gmax = new System.Numerics.Vector3(float.MinValue);
        foreach (var m in _selection.Items)
        {
            if (m.VertexCount <= 0) continue;
            var min = new System.Numerics.Vector3(float.MaxValue);
            var max = new System.Numerics.Vector3(float.MinValue);
            int start = m.VertexStart * 3, end = (m.VertexStart + m.VertexCount) * 3;
            for (int i = start; i < end; i += 3)
            {
                var p = new System.Numerics.Vector3(map.Positions[i], map.Positions[i + 1], map.Positions[i + 2]);
                min = System.Numerics.Vector3.Min(min, p);
                max = System.Numerics.Vector3.Max(max, p);
            }
            boxes.Add((min, max));
            gmin = System.Numerics.Vector3.Min(gmin, min);
            gmax = System.Numerics.Vector3.Max(gmax, max);
        }
        if (boxes.Count == 0) { SelectionBoxes = null; GroupBoundsMin = GroupBoundsMax = GizmoPivot = null; return; }
        SelectionBoxes = null;   // M50b: selection reads as a mesh OUTLINE now, not AABB boxes
        // Group bounds box only makes sense for a multi-selection; a single mesh already has its highlight box.
        GroupBoundsMin = _selection.IsMulti ? gmin : null;
        GroupBoundsMax = _selection.IsMulti ? gmax : null;
        GizmoPivot = (gmin + gmax) * 0.5f; // selection center = combined bbox center
    }

    // Drag state captured at gizmo-press so the WHOLE drag is one undo step. For a multi-selection we
    // record every mesh's before-state and the primary's start offset (to derive the world delta).
    private (MapGeoMesh mesh, MeshTransformCommand.State before)[] _dragBefore = System.Array.Empty<(MapGeoMesh, MeshTransformCommand.State)>();
    private System.Numerics.Vector3 _dragStartPrimaryOffset;

    /// <summary>Called at gizmo-press: capture the transform(s) so the whole drag becomes ONE undo step.</summary>
    public void BeginMeshDrag()
    {
        _dragBefore = _selection.Items.Select(m => (m, MeshTransformCommand.State.Capture(m))).ToArray();
        _dragStartPrimaryOffset = _selection.Primary?.Offset ?? System.Numerics.Vector3.Zero;
    }

    /// <summary>Live-drag the selection to an absolute primary offset (called every pointer-move frame by
    /// the viewport's translate gizmo). Single mesh moves via its own offset; a multi-selection moves rigidly
    /// as a group (world delta applied through the GroupMatrix). Cheap + silent; <see cref="EndMeshDrag"/> logs.</summary>
    public void DragSelectedMeshTo(System.Numerics.Vector3 absoluteOffset)
    {
        if (_selection.Primary is not { } primary || _currentMap is not { } map) return;
        if (_selection.IsMulti)
        {
            // Restore all meshes to their drag-start state, then batch-translate by the total world delta —
            // absolute-from-start so repeated frames don't accumulate.
            var worldDelta = absoluteOffset - _dragStartPrimaryOffset;
            foreach (var (mesh, before) in _dragBefore) { before.ApplyTo(mesh); map.ApplyMeshTransform(mesh); }
            map.BatchTranslate(_selection.Items, worldDelta);
        }
        else
        {
            map.TranslateMesh(primary, absoluteOffset);
        }
        RefreshMeshTransformFields(primary);
        RefreshSelectionVisuals();
        MeshVerticesRevision++;
    }

    // ---- M42: transform gizmo mode / space / snap ----
    /// <summary>Active gizmo: 0 = Move, 1 = Rotate, 2 = Scale.</summary>
    [ObservableProperty] private int _transformMode;
    /// <summary>Gizmo axes follow the mesh's own rotation (Local) instead of world axes.</summary>
    [ObservableProperty] private bool _gizmoLocalSpace;
    [ObservableProperty] private bool _snapEnabled;

    public bool IsMoveMode => TransformMode == 0;
    public bool IsRotateMode => TransformMode == 1;
    public bool IsScaleMode => TransformMode == 2;
    public string GizmoSpaceLabel => GizmoLocalSpace ? "Local" : "World";

    // snap increments (world units / degrees / scale ratio)
    public const float MoveSnap = 100f, RotateSnap = 15f, ScaleSnap = 0.25f;
    public float ApplyMoveSnap(float v) => SnapEnabled ? MathF.Round(v / MoveSnap) * MoveSnap : v;
    public float ApplyRotateSnap(float v) => SnapEnabled ? MathF.Round(v / RotateSnap) * RotateSnap : v;
    public float ApplyScaleSnap(float v) => SnapEnabled ? MathF.Max(0.05f, MathF.Round(v / ScaleSnap) * ScaleSnap) : v;

    partial void OnTransformModeChanged(int value)
    {
        OnPropertyChanged(nameof(IsMoveMode));
        OnPropertyChanged(nameof(IsRotateMode));
        OnPropertyChanged(nameof(IsScaleMode));
        GizmoRevision++;
    }
    partial void OnGizmoLocalSpaceChanged(bool value) { OnPropertyChanged(nameof(GizmoSpaceLabel)); OnPropertyChanged(nameof(GizmoAxes)); GizmoRevision++; }

    /// <summary>Bumped whenever the gizmo's mode/space changes so the viewport rebuilds its handles.</summary>
    [ObservableProperty] private int _gizmoRevision;

    [RelayCommand] private void SetTransformMode(string mode) { if (int.TryParse(mode, out var m)) TransformMode = m; }
    [RelayCommand] private void ToggleGizmoSpace() => GizmoLocalSpace = !GizmoLocalSpace;

    /// <summary>Live rotate the selected mesh about its pivot (M42 gizmo). Single-select only.</summary>
    public void RotateSelectedMeshTo(System.Numerics.Vector3 rotationDegrees)
    {
        if (_selection.Primary is not { } primary || _currentMap is not { } map) return;
        map.RotateMesh(primary, rotationDegrees);
        RefreshMeshTransformFields(primary);
        RefreshSelectionVisuals();
        MeshVerticesRevision++;
    }

    /// <summary>Live scale the selected mesh about its pivot (M42 gizmo). Single-select only.</summary>
    public void ScaleSelectedMeshTo(System.Numerics.Vector3 scale)
    {
        if (_selection.Primary is not { } primary || _currentMap is not { } map) return;
        map.ScaleMesh(primary, scale);
        RefreshMeshTransformFields(primary);
        RefreshSelectionVisuals();
        MeshVerticesRevision++;
    }

    /// <summary>The selected mesh's current rotation/scale — the drag's start state for gizmo rotate/scale.</summary>
    public (System.Numerics.Vector3 rot, System.Numerics.Vector3 scale) SelectedMeshRotScale =>
        _selection.Primary is { } p ? (p.RotationDegrees, p.Scale) : (System.Numerics.Vector3.Zero, System.Numerics.Vector3.One);

    /// <summary>The selected mesh's local axes (its rotation applied to world X/Y/Z) for Local-space gizmo.</summary>
    public (System.Numerics.Vector3 x, System.Numerics.Vector3 y, System.Numerics.Vector3 z) SelectedMeshLocalAxes
    {
        get
        {
            if (!GizmoLocalSpace || _selection.Primary is not { } p)
                return (System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitY, System.Numerics.Vector3.UnitZ);
            var r = p.RotationDegrees * (MathF.PI / 180f);
            var q = System.Numerics.Quaternion.CreateFromYawPitchRoll(r.Y, r.X, r.Z);
            return (System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitX, q),
                    System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitY, q),
                    System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitZ, q));
        }
    }

    /// <summary>The three gizmo axis directions (world, or the selected mesh's local axes) for the viewport.</summary>
    public IReadOnlyList<System.Numerics.Vector3> GizmoAxes
    {
        get { var (x, y, z) = SelectedMeshLocalAxes; return new[] { x, y, z }; }
    }

    partial void OnGizmoPivotChanged(System.Numerics.Vector3? value) => OnPropertyChanged(nameof(GizmoAxes));

    public void EndMeshDrag()
    {
        if (_selection.Primary is not { } primary || _currentMap is not { } map || _dragBefore.Length == 0) return;
        string verb = TransformMode == 1 ? "Rotate" : TransformMode == 2 ? "Scale" : "Move";
        if (_selection.IsMulti)
        {
            var entries = _dragBefore.Select(b => (b.mesh, b.before, MeshTransformCommand.State.Capture(b.mesh)));
            var cmd = new BatchTransformCommand($"{verb} Meshes", map, entries, MakeBatchRefresh(map));
            if (cmd.HasChange) UndoService.PushApplied(cmd);
            _log.Info("MapGeo", $"{verb}d {_dragBefore.Length} meshes via gizmo.");
        }
        else
        {
            PushTransformCommand($"{verb} Mesh", map, primary, _dragBefore[0].before, MeshTransformCommand.State.Capture(primary));
            _log.Info("MapGeo", $"{verb}d '{primary.Name}' via gizmo.");
        }
        HasMapMoves = MapGeoWriter.HasMoves(map.Meshes) || MapGeoLayerWriter.HasEdits(map.Meshes);
    }

    private void RefreshMeshTransformFields(MapGeoMesh m)
    {
        var pos = m.Pivot + m.Offset;
        MeshMoveX = pos.X.ToString("0.###", CultureInfo.InvariantCulture);
        MeshMoveY = pos.Y.ToString("0.###", CultureInfo.InvariantCulture);
        MeshMoveZ = pos.Z.ToString("0.###", CultureInfo.InvariantCulture);
        MeshRotateX = m.RotationDegrees.X.ToString("0.###", CultureInfo.InvariantCulture);
        MeshRotateY = m.RotationDegrees.Y.ToString("0.###", CultureInfo.InvariantCulture);
        MeshRotateZ = m.RotationDegrees.Z.ToString("0.###", CultureInfo.InvariantCulture);
        MeshScaleX = m.Scale.X.ToString("0.###", CultureInfo.InvariantCulture);
        MeshScaleY = m.Scale.Y.ToString("0.###", CultureInfo.InvariantCulture);
        MeshScaleZ = m.Scale.Z.ToString("0.###", CultureInfo.InvariantCulture);
        SelectedMeshNormalsFlipped = m.FlipNormals;
    }

    private static bool TryParseVector3(string sx, string sy, string sz, out System.Numerics.Vector3 v)
    {
        v = default;
        if (!float.TryParse(sx, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !float.TryParse(sy, NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !float.TryParse(sz, NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            return false;
        v = new System.Numerics.Vector3(x, y, z);
        return true;
    }

    /// <summary>UI sync run after a transform command executes OR undoes (viewport, fields, highlight, dirty).</summary>
    private Action MakeTransformRefresh(MapGeoAsset map, MapGeoMesh mesh) => () =>
    {
        MeshVerticesRevision++;   // re-upload the edited vertices to the viewport (GL thread)
        if (ReferenceEquals(SelectedMapMesh, mesh))
        {
            RefreshMeshTransformFields(mesh);
            RefreshSelectionVisuals();
        }
        HasMapMoves = MapGeoWriter.HasMoves(map.Meshes) || MapGeoLayerWriter.HasEdits(map.Meshes);
    };

    /// <summary>UI sync run after a BATCH transform command executes OR undoes: re-upload vertices, refresh
    /// the primary's fields, recompute all selection visuals, and update the dirty flag.</summary>
    private Action MakeBatchRefresh(MapGeoAsset map) => () =>
    {
        MeshVerticesRevision++;
        if (SelectedMapMesh is { } primary) RefreshMeshTransformFields(primary);
        RefreshSelectionVisuals();
        HasMapMoves = MapGeoWriter.HasMoves(map.Meshes) || MapGeoLayerWriter.HasEdits(map.Meshes);
    };

    /// <summary>Run a batch operation on the whole selection as ONE undo step: capture every mesh's
    /// before-state, apply <paramref name="op"/>, then push a single <see cref="BatchTransformCommand"/>.</summary>
    private void RunBatch(string name, MapGeoAsset map, Action op)
    {
        var before = _selection.Items.Select(m => (mesh: m, state: MeshTransformCommand.State.Capture(m))).ToList();
        op();
        var entries = before.Select(b => (b.mesh, b.state, MeshTransformCommand.State.Capture(b.mesh)));
        var cmd = new BatchTransformCommand(name, map, entries, MakeBatchRefresh(map));
        if (!cmd.HasChange) return;
        UndoService.PushApplied(cmd);
        MeshVerticesRevision++;
        if (SelectedMapMesh is { } primary) RefreshMeshTransformFields(primary);
        RefreshSelectionVisuals();
        HasMapMoves = MapGeoWriter.HasMoves(map.Meshes) || MapGeoLayerWriter.HasEdits(map.Meshes);
    }

    /// <summary>Push an already-applied transform edit as one undo step (no-op if nothing changed).</summary>
    private void PushTransformCommand(string name, MapGeoAsset map, MapGeoMesh mesh,
        MeshTransformCommand.State before, MeshTransformCommand.State after)
    {
        if (before == after) return;
        UndoService.PushApplied(new MeshTransformCommand(name, map, mesh, before, after, MakeTransformRefresh(map, mesh)));
    }

    [RelayCommand]
    private void ApplyMeshMove()
    {
        if (SelectedMapMesh is not { } m || _currentMap is not { } map) return;
        if (!TryParseVector3(MeshMoveX, MeshMoveY, MeshMoveZ, out var target))
        { _log.Warn("MapGeo", "Enter valid position X/Y/Z numbers."); return; }
        if (!TryParseVector3(MeshRotateX, MeshRotateY, MeshRotateZ, out var rotation))
        { _log.Warn("MapGeo", "Enter valid rotation X/Y/Z numbers (degrees)."); return; }
        if (!TryParseVector3(MeshScaleX, MeshScaleY, MeshScaleZ, out var scale))
        { _log.Warn("MapGeo", "Enter valid scale X/Y/Z numbers."); return; }
        if (scale.X == 0 || scale.Y == 0 || scale.Z == 0)
        { _log.Warn("MapGeo", "Scale cannot be zero on any axis."); return; }

        var before = MeshTransformCommand.State.Capture(m);
        map.TranslateMesh(m, target - m.Pivot);
        map.RotateMesh(m, rotation);
        map.ScaleMesh(m, scale);
        PushTransformCommand("Transform Mesh", map, m, before, MeshTransformCommand.State.Capture(m));
        MeshVerticesRevision++;           // re-upload the edited vertices to the viewport
        RefreshSelectionVisuals();
        HasMapMoves = MapGeoWriter.HasMoves(map.Meshes) || MapGeoLayerWriter.HasEdits(map.Meshes);
        _log.Info("MapGeo", $"Transformed '{m.Name}': pos ({target.X:0.#}, {target.Y:0.#}, {target.Z:0.#}), " +
                            $"rot ({rotation.X:0.#}°, {rotation.Y:0.#}°, {rotation.Z:0.#}°), scale ({scale.X:0.##}, {scale.Y:0.##}, {scale.Z:0.##}).");
    }

    [RelayCommand]
    private void ResetMeshTransform()
    {
        if (SelectedMapMesh is not { } m || _currentMap is not { } map) return;
        var before = MeshTransformCommand.State.Capture(m);
        map.ResetMesh(m);
        PushTransformCommand("Reset Transform", map, m, before, MeshTransformCommand.State.Capture(m));
        RefreshMeshTransformFields(m);
        RefreshSelectionVisuals();
        MeshVerticesRevision++;
        HasMapMoves = MapGeoWriter.HasMoves(map.Meshes) || MapGeoLayerWriter.HasEdits(map.Meshes);
        _log.Info("MapGeo", $"Reset '{m.Name}' to its original transform.");
    }

    /// <summary>True when the selected mesh has its normals manually flipped (drives the toggle's checked state).</summary>
    [ObservableProperty] private bool _selectedMeshNormalsFlipped;

    /// <summary>M34: flip the selected mesh's vertex normals (live preview edit). Useful for meshes that a
    /// converter exported with inward-facing normals. Note: for two-sided (cullEnable=false) materials the
    /// two-sided lighting already lights both faces, so flipping there darkens rather than fixes.</summary>
    [RelayCommand]
    private void FlipMeshNormals()
    {
        if (SelectedMapMesh is not { } m || _currentMap is not { } map) return;
        map.SetFlipNormals(m, !m.FlipNormals);
        SelectedMeshNormalsFlipped = m.FlipNormals;
        MeshVerticesRevision++; // re-upload the flipped normals to the viewport (GL thread)
        RefreshMeshDetails();
        _log.Info("MapGeo", $"{(m.FlipNormals ? "Flipped" : "Restored")} normals on '{m.Name}'.");
    }

    // ---- Batch transform commands (M30) — operate on the whole selection around its center -------------

    private System.Numerics.Vector3 SelectionCenter() =>
        GizmoPivot ?? System.Numerics.Vector3.Zero; // gizmo pivot IS the live selection center

    [RelayCommand]
    private void ApplyBatchMove()
    {
        if (!_selection.IsMulti || _currentMap is not { } map) return;
        if (!TryParseVector3(BatchMoveX, BatchMoveY, BatchMoveZ, out var delta))
        { _log.Warn("MapGeo", "Enter valid batch move X/Y/Z numbers."); return; }
        if (delta == System.Numerics.Vector3.Zero) return;
        RunBatch("Batch Move", map, () => map.BatchTranslate(_selection.Items, delta));
        _log.Info("MapGeo", $"Moved {_selection.Count} meshes by ({delta.X:0.#}, {delta.Y:0.#}, {delta.Z:0.#}).");
    }

    [RelayCommand]
    private void ApplyBatchRotate()
    {
        if (!_selection.IsMulti || _currentMap is not { } map) return;
        if (!TryParseVector3(BatchRotateX, BatchRotateY, BatchRotateZ, out var euler))
        { _log.Warn("MapGeo", "Enter valid batch rotation X/Y/Z numbers (degrees)."); return; }
        if (euler == System.Numerics.Vector3.Zero) return;
        var center = SelectionCenter();
        RunBatch("Batch Rotate", map, () => map.BatchRotate(_selection.Items, euler, center));
        _log.Info("MapGeo", $"Rotated {_selection.Count} meshes by ({euler.X:0.#}°, {euler.Y:0.#}°, {euler.Z:0.#}°) about the selection center.");
    }

    [RelayCommand]
    private void ApplyBatchScale()
    {
        if (!_selection.IsMulti || _currentMap is not { } map) return;
        if (!TryParseVector3(BatchScaleX, BatchScaleY, BatchScaleZ, out var scale))
        { _log.Warn("MapGeo", "Enter valid batch scale X/Y/Z numbers."); return; }
        if (scale.X == 0 || scale.Y == 0 || scale.Z == 0)
        { _log.Warn("MapGeo", "Batch scale cannot be zero on any axis."); return; }
        if (scale == System.Numerics.Vector3.One) return;
        var center = SelectionCenter();
        RunBatch("Batch Scale", map, () => map.BatchScale(_selection.Items, scale, center));
        _log.Info("MapGeo", $"Scaled {_selection.Count} meshes by ({scale.X:0.##}, {scale.Y:0.##}, {scale.Z:0.##}) about the selection center.");
    }

    /// <summary>Reset every selected mesh to its original transform as one undo step.</summary>
    [RelayCommand]
    private void ResetSelected()
    {
        if (_currentMap is not { } map || _selection.IsEmpty) return;
        RunBatch("Reset Selected", map, () => { foreach (var m in _selection.Items) map.ResetMesh(m); });
        _log.Info("MapGeo", $"Reset {_selection.Count} selected mesh(es) to their original transforms.");
    }

    [RelayCommand]
    private void ClearSelection() => _selection.Clear();

    [RelayCommand]
    private async Task SaveMeshMoves()
    {
        // M80: never fail silently — say exactly which precondition is missing.
        if (_currentMap is not { } map || _currentMapBytes is null || _currentMapEntry is not { } entry)
        {
            _log.Warn("MapGeo", $"Cannot save: map={( _currentMap is null ? "none" : "ok")}, " +
                $"bytes={(_currentMapBytes is null ? "none" : "ok")}, entry={(_currentMapEntry is null ? "none" : "ok")}. Reload the map and try again.");
            return;
        }
        bool hasMoves = MapGeoWriter.HasMoves(map.Meshes);
        bool hasLayers = MapGeoLayerWriter.HasEdits(map.Meshes);
        var added = MapContent.AddedMeshes.ToList();
        var removedIndices = MapContent.AllMapPieces.Where(p => p.IsRemoved).Select(p => p.MeshIndex).Distinct().ToList();
        if (!hasMoves && !hasLayers && added.Count == 0 && removedIndices.Count == 0) { _log.Info("MapGeo", "No map edits to save."); return; }
        if (!GuardEditable(entry)) return;
        if (!await EnsureProjectSavedAsync()) return;

        try
        {
            byte[] bytes = _currentMapBytes;

            // 0) M105: layer/controller/backface edits FIRST — they don't touch the [bbox][transform]
            //    signatures, so the move patching that follows still locates every mesh.
            if (hasLayers)
            {
                var layered = MapGeoLayerWriter.TryWriteLayerEdits(bytes, map.Meshes, out var lErr);
                if (layered is null) { _log.Error("MapGeo", $"Could not save layer edits: {lErr}"); return; }
                bytes = layered;
            }

            // 1) mesh moves (rebuilds bucket grids for the moved geometry)
            if (hasMoves)
            {
                var moved = MapGeoWriter.TryWriteWithMoves(bytes, map.Meshes, out var mErr);
                if (moved is null) { _log.Error("MapGeo", $"Could not save mesh moves: {mErr}"); return; }
                bytes = moved;
            }

            // 2) append the imported meshes (surgical splice), then regenerate bucket grids over ALL
            //    triangles (new geometry included) so the game culls the added meshes correctly.
            if (added.Count > 0)
            {
                var newMeshes = added.Select(a => new NewMapMesh(
                    a.Material, a.Positions, a.Normals, a.Uvs,
                    System.Array.ConvertAll(a.Indices, i => (ushort)i), a.Transform)).ToList();
                var appended = MapGeoMeshAppender.Append(bytes, newMeshes, out var aErr);
                if (appended is null) { _log.Error("MapGeo", $"Could not append meshes: {aErr}"); return; }

                var reMap = await Task.Run(() => MapGeoDecoder.Decode(appended));

                // M123: the appended meshes are the LAST N — give them their chosen layer masks
                // before the grids bake per-face visibility from the mesh flags.
                bool anyMask = added.Any(a => a.VisibilityMask != 255);
                if (anyMask && reMap.Meshes.Count >= added.Count)
                {
                    for (int i = 0; i < added.Count; i++)
                        reMap.Meshes[reMap.Meshes.Count - added.Count + i].VisibilityEdit = added[i].VisibilityMask;
                    var layered = MapGeoLayerWriter.TryWriteLayerEdits(appended, reMap.Meshes, out var lErr);
                    if (layered is not null) { appended = layered; reMap = await Task.Run(() => MapGeoDecoder.Decode(appended)); }
                    else _log.Warn("MapGeo", $"Added-mesh layers not applied: {lErr}");
                }
                bytes = MapGeoWriter.WriteWithRegeneratedBucketGrids(appended, reMap);
            }

            // 2b) M105: bucket grids bake per-face visibility masks from the mesh flags, so layer-only
            //     saves must regenerate them too (moves/appends above already did).
            if (hasLayers && !hasMoves && added.Count == 0)
            {
                var reMap2 = await Task.Run(() => MapGeoDecoder.Decode(bytes));
                bytes = MapGeoWriter.WriteWithRegeneratedBucketGrids(bytes, reMap2);
            }

            // 3) Blender-style pending deletion: remove only the selected mesh records. Their buffers stay
            // in the file unreferenced so no surviving mesh ID has to be rewritten; bucket grids are then
            // rebuilt from the remaining environment meshes.
            if (removedIndices.Count > 0)
            {
                var stripped = MapGeoMeshRemover.Remove(bytes, removedIndices, out var removeError);
                if (stripped is null) { _log.Error("MapGeo", $"Could not remove selected meshes: {removeError}"); return; }
                var remainingMap = await Task.Run(() => MapGeoDecoder.Decode(stripped));
                bytes = MapGeoWriter.WriteWithRegeneratedBucketGrids(stripped, remainingMap);
            }

            var dest = ProjectWorkspace.StoreOverrideBytes(Project, entry.PathHash, bytes, ".mapgeo");
            _overrides.Set(new ProjectAssetOverride
            {
                PathHash = entry.PathHash,
                ResolvedPath = entry.IsResolved ? entry.Path : null,
                OverrideFile = dest,
                AddedUtc = DateTime.UtcNow.ToString("o"),
            });
            SetNodeStatus(entry.PathHash, AssetStatus.Modified);
            Project.IsDirty = true;
            UpdateTitle();
            UndoService.MarkSaved();
            int moves = map.Meshes.Count(x => x.IsMoved);
            int layers = map.Meshes.Count(x => x.HasLayerEdit);
            _log.Success("MapGeo", $"Saved {moves} mesh move(s) + {layers} layer edit(s) + {added.Count} added + {removedIndices.Count} deleted mesh(es) to override ({bytes.Length:n0} bytes). Build Package will include it. Reload the map to edit the resulting native geometry.");
        }
        catch (Exception ex) { _log.Error("MapGeo", ex.Message); }
    }

    /// <summary>Persist the moved particles into the map's .materials.bin override (M35).</summary>
    [RelayCommand]
    private async Task SaveParticleMoves()
    {
        if (_currentMapEntry is not { } mapEntry) return;
        var moved = MapContent.AllParticles.Where(v => v.HasEdits).ToList();
        // M199 (5.2): sounds derived FROM a particle system are no longer skipped. They used to be, because
        // they share the particle's transform BYTES and the old locator found placements by that signature -
        // so saving both collided. Identity is now (container, item key), which is unique, so the collision
        // cannot happen. A derived sound has no placement id of its own, though: it is a view of the
        // particle, so moving the particle is still what moves it, and only standalone MapAudio saves here.
        var movedSounds = MapContent.Sounds.Where(s => s.HasEdits && !s.Sound.FromParticleSystem).ToList();
        var editedProps = MapContent.AllProps.Where(p => p.HasEdits).ToList();
        var editedProbes = MapContent.Probes.Where(p => p.HasEdits).ToList();
        int derivedSounds = MapContent.Sounds.Count(s => s.HasEdits && s.Sound.FromParticleSystem);
        if (derivedSounds > 0)
            _log.Info("Sounds", $"{derivedSounds} moved sound(s) follow their particle system and are saved with it.");
        if (moved.Count == 0 && movedSounds.Count == 0 && editedProps.Count == 0 && editedProbes.Count == 0)
        { _log.Info("Map Content", "No placement edits to save."); return; }
        foreach (var prop in editedProps.Where(p => !string.IsNullOrWhiteSpace(p.EditedSkin)
                     && !p.EffectiveSkin.Equals(p.Prop.Skin, StringComparison.OrdinalIgnoreCase)))
        {
            string path = prop.EffectiveSkin.Replace('\\', '/').TrimStart('/');
            if (!path.StartsWith("data/", StringComparison.OrdinalIgnoreCase)) path = "data/" + path;
            if (!path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) path += ".bin";
            if (ReadAssetByPath(path) is null)
            {
                _log.Error("Props", $"Skin '{prop.EffectiveSkin}' does not resolve to an installed/project skin bin. Nothing was saved.");
                return;
            }
        }
        if (!TryResolveMaterialsBin(mapEntry.Path, out var binEntry)) { _log.Error("Particles", "No materials .bin to save into."); return; }
        if (!GuardEditable(binEntry)) return;
        if (!await EnsureProjectSavedAsync()) return;

        // M199 (5.2): edits are addressed by tree identity, not by a 64-byte transform signature. 1,450 of
        // 30,628 shipped placements share a matrix with a neighbour, so the old signature could patch the
        // wrong one; 2 have no transform and were unaddressable entirely.
        // M204: one edit per placement carrying every verb the user changed. Fields the user did not
        // touch stay null, which the writer reads as "leave this alone" - so a rename does not also
        // rewrite the transform.
        var placementEdits = moved
            .Where(v => v.Placement.Id.IsValid)
            .Select(v => new MapPlacementEdit(v.Placement.Id)
            {
                CloneOf = v.CloneSource,   // M206: null for an existing placement
                // A NEW placement always writes its transform - it has no authored one to preserve.
                Transform = v.IsMoved || v.IsNew ? v.CurrentTransform : null,
                Name = v.EditedName is { } n && n != v.Placement.Name ? n : null,
                ColorModulate = v.ParsedTint,
                SystemLink = v.EditedSystemHash != 0 ? v.EditedSystemHash : null,
                VisibilityFlags = v.EditedVisibilityFlags,
                Remove = v.IsRemoved,
            })
            .ToList();
        int unaddressable = moved.Count(v => !v.Placement.Id.IsValid);
        if (unaddressable > 0)
            _log.Warn("Particles", $"{unaddressable} moved particle(s) have no identity in the bin and were skipped.");


        // M202: sounds go through the SAME identity-addressed writer. Nothing uses the byte-signature
        // patcher any more, which matters because MapAudio placements are part of the same 1,450 that share
        // a transform with a neighbour - a standalone sound move could move the wrong thing too.
        placementEdits.AddRange(movedSounds
            .Where(s => s.Sound.Id.IsValid)
            .Select(s =>
            {
                var t = s.Sound.Transform;
                t.Translation = s.Position;
                return new MapPlacementEdit(s.Sound.Id)
                {
                    Transform = s.IsMoved ? t : null,
                    VisibilityFlags = s.EditedVisibilityFlags,
                    Remove = s.IsRemoved,
                };
            }));
        int unaddressableSounds = movedSounds.Count(s => !s.Sound.Id.IsValid);
        if (unaddressableSounds > 0)
            _log.Warn("Sounds", $"{unaddressableSounds} moved sound(s) have no identity in the bin and were skipped.");

        placementEdits.AddRange(editedProps.Where(p => p.Prop.Id.IsValid).Select(p => new MapPlacementEdit(p.Prop.Id)
        {
            Skin = !string.IsNullOrWhiteSpace(p.EditedSkin)
                && !p.EffectiveSkin.Equals(p.Prop.Skin, StringComparison.OrdinalIgnoreCase) ? p.EffectiveSkin : null,
            VisibilityFlags = p.EditedVisibilityFlags,
            Remove = p.IsRemoved,
        }));
        placementEdits.AddRange(editedProbes.Where(p => p.Probe.Id.IsValid).Select(p => new MapPlacementEdit(p.Probe.Id)
        {
            VisibilityFlags = p.EditedVisibilityFlags,
            Remove = p.IsRemoved,
        }));
        int unaddressableOthers = editedProps.Count(p => !p.Prop.Id.IsValid) + editedProbes.Count(p => !p.Probe.Id.IsValid);
        if (unaddressableOthers > 0)
            _log.Warn("Map Content", $"{unaddressableOthers} prop/probe placement(s) have no identity and were skipped.");

        var source = GetAssetBytes(binEntry);
        string? err = null;
        byte[]? bytes = placementEdits.Count > 0
            ? MapPlaceableWriter.WriteEdits(source, placementEdits, out err)
            : source;
        if (bytes is null) { _log.Error("Particles", $"Could not save placement edits: {err}"); return; }
        if (err is not null) _log.Warn("Particles", err);
        try
        {
            var dest = ProjectWorkspace.StoreOverrideBytes(Project, binEntry.PathHash, bytes, ".bin");
            _overrides.Set(new ProjectAssetOverride
            {
                PathHash = binEntry.PathHash,
                ResolvedPath = binEntry.IsResolved ? binEntry.Path : null,
                OverrideFile = dest,
                AddedUtc = DateTime.UtcNow.ToString("o"),
            });
            SetNodeStatus(binEntry.PathHash, AssetStatus.Modified);
            Project.IsDirty = true;
            UpdateTitle();
            HasParticleMoves = false;
            _log.Success("Map Content", $"Saved {placementEdits.Count} placement edit(s) to the materials.bin override. Build Package will include it.");
        }
        catch (Exception ex) { _log.Error("Particles", ex.Message); }
    }

    private async Task LoadBinAsync(WadAssetEntry entry)
    {
        if (!ContentLoaded) return;
        try
        {
            var doc = await Task.Run(() =>
                BinEditorDocument.Parse(ReadAsset(entry.PathHash),
                    h => _resolver.Database.TryGetBinName(h, out var n) ? n : null));

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                BinEditor.Load(doc, entry);
                _log.Info("Bin", $"{entry.DisplayName}: {doc.Roots.Count} object(s)" +
                                 (doc.Dependencies.Count > 0 ? $", {doc.Dependencies.Count} dependencies" : "") +
                                 " — primitive fields are editable.");
            });
        }
        catch (Exception ex)
        {
            _log.Error("Bin", $"{entry.DisplayName}: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SaveBinToOverride()
    {
        if (BinEditor.Entry is not { } entry) { _log.Warn("Bin", "No .bin open."); return; }
        if (!GuardEditable(entry)) return;
        if (!BinEditor.IsDirty) { _log.Info("Bin", "No applied edits to save."); return; }
        if (!await EnsureProjectSavedAsync()) return;

        var bytes = BinEditor.Serialize();
        if (bytes is null) return;

        // Validate the edited .bin re-parses before committing it to the override layer.
        try { _ = new LeagueToolkit.Core.Meta.BinTree(new MemoryStream(bytes, false)); }
        catch (Exception ex) { _log.Error("Bin", $"Edited .bin failed to re-parse — NOT saved: {ex.Message}"); return; }

        try
        {
            // M98c: folder-project files are edited in place — no shadow override
            if (TryWriteToProjectFile(entry, bytes, out var projectFile))
            {
                SetNodeStatus(entry.PathHash, AssetStatus.Modified);
                Project.IsDirty = true;
                UpdateTitle();
                UndoService.MarkSaved();
                _log.Success("Bin", $"Saved edited {entry.DisplayName} to {projectFile} ({bytes.Length:n0} bytes, re-parse OK).");
                return;
            }
            var dest = ProjectWorkspace.StoreOverrideBytes(Project, entry.PathHash, bytes, ".bin");
            _overrides.Set(new ProjectAssetOverride
            {
                PathHash = entry.PathHash,
                ResolvedPath = entry.IsResolved ? entry.Path : null,
                OverrideFile = dest,
                AddedUtc = DateTime.UtcNow.ToString("o"),
            });
            SetNodeStatus(entry.PathHash, AssetStatus.Modified);
            Inspector.SetAssetStatus("Modified — Project Override", dest);
            Project.IsDirty = true;
            UpdateTitle();
            UndoService.MarkSaved();
            _log.Success("Bin", $"Saved edited {entry.DisplayName} to project override ({bytes.Length:n0} bytes, re-parse OK). Build Package will include it.");
        }
        catch (Exception ex) { _log.Error("Bin", ex.Message); }
    }

    [RelayCommand]
    private async Task ExportEditedBin()
    {
        if (BinEditor.Entry is not { } entry) { _log.Warn("Bin", "No .bin open."); return; }
        var bytes = BinEditor.Serialize();
        if (bytes is null) return;
        var outPath = await Dialogs.SaveFileAsync("Export edited .bin", entry.DisplayName);
        if (outPath is null) return;
        try
        {
            await File.WriteAllBytesAsync(outPath, bytes);
            _log.Success("Bin", $"Exported edited {entry.DisplayName} → {outPath} ({bytes.Length:n0} bytes).");
        }
        catch (Exception ex) { _log.Error("Bin", ex.Message); }
    }

    private async Task TryPreviewTextureAsync(WadAssetEntry entry)
    {
        if (!ContentLoaded) return;
        try
        {
            var img = await Task.Run(() => TextureDecoder.Decode(GetAssetBytes(entry)));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // M120: ONE preview surface - the image shows in the Model Preview window (replacing
                // whatever it showed) instead of stacking a second preview card into the inspector.
                MeshPreview.ShowImage(entry.DisplayName, BitmapFactory.FromRgba(img),
                    $"{img.Width}×{img.Height} · {entry.Compression}");
                ShowMeshPreviewWindow?.Invoke();
                _log.Info("Preview", $"Decoded {entry.DisplayName} ({img.Width}×{img.Height}).");
            });
        }
        catch (Exception ex) { _log.Error("Preview", $"{entry.DisplayName}: {ex.Message}"); }
    }

    // ---- M50: model preview window (separate viewport; main viewport stays on the map) ----
    public MeshPreviewViewModel MeshPreview { get; } = new();
    public Action? ShowMeshPreviewWindow;   // wired by MainWindow (owns the window instance)

    /// <summary>M141: open a legacy (NVR) map folder — LEVELS/&lt;Map&gt; with Scene/room.nvr — as a
    /// standalone map in the Model Preview window. Reuses the M88/M89 NVR loader (mesh + per-submesh
    /// textures + lights); the preview auto-frames the whole map.</summary>
    [RelayCommand]
    private async Task OpenLegacyMap()
    {
        var folder = await Dialogs.OpenFolderAsync("Open a legacy NVR map folder (e.g. LEVELS/Map10)");
        if (folder is null) return;
        // Accept either the map folder or its Scene subfolder.
        if (!Services.MapPreviewLoader.IsNvrMapFolder(folder))
        {
            var parent = Path.GetDirectoryName(folder.TrimEnd('/', '\\'));
            if (parent is not null && Services.MapPreviewLoader.IsNvrMapFolder(parent)) folder = parent;
            else { _log.Warn("Map", $"No Scene/room.nvr in {folder} — pick a legacy LEVELS/MapN folder."); return; }
        }

        Status = "Loading legacy map…";
        try
        {
            var bg = await Task.Run(() => Services.MapPreviewLoader.Load(folder));
            MeshPreview.Show($"{bg.MapName} (legacy NVR map)", bg.Mesh, skeleton: null, textures: bg.SubmeshTextures);
            // M142.8: a legacy map IS the subject — drop any character-preview backdrop still attached from
            // an earlier skin preview, or both maps render at once (Map8 backdrop behind the Map10 subject).
            MeshPreview.SetBackground(null);
            MeshPreview.Materials = bg.SubmeshMaterials;   // M142: double-sided + alpha cutout + ground flags
            // M148: NVR levels come in two flavours and need different ground + lighting models.
            //   height-blend (Twisted Treeline): BLEND_MAP is the null_black placeholder and the real
            //     ground is a baked composite atlas; its statics carry usable baked vertex lighting.
            //   mask-blend (Dominion) / plain (Map4): a real four-blend BLEND_MAP or no blend at all,
            //     and near-black vertex colours that are mask/AO data — NOT lighting. Applying the
            //     height-blend model to those flattened Dominion's ground and washed out its lighting.
            bool heightBlend = bg.SubmeshLightmap is not null;
            MeshPreview.IsLegacyMap = true;
            MeshPreview.NvrHeightBlend = heightBlend;
            MeshPreview.LightmapTextures = bg.SubmeshLightmap;   // composite atlas (height-blend only)
            MeshPreview.GradientTextures = bg.SubmeshColor1;     // COLOR_MAP_1/2/3 feed both models
            MeshPreview.EmissiveTextures = bg.SubmeshColor2;
            MeshPreview.MatCapTextures = bg.SubmeshColor3;
            // Mask slot: the height-scale map for height-blend, else the four-blend BLEND_MAP.
            MeshPreview.MaskTextures = heightBlend ? bg.SubmeshMask : bg.SubmeshBlend;
            MeshPreview.NvrFourBlend = !heightBlend;
            MeshPreview.UseVertexLightmap = heightBlend;
            MeshPreview.NvrVertexLight = 0;         // M89 default — the baked term is opt-in per map
            MeshPreview.NvrBrightness = 0.55;
            // M149: light it with the level's OWN sun/ambient when it ships one (terrain.inibin / sun.ini).
            MeshPreview.NvrSun = bg.Sun;
            MeshPreview.NvrUseMapSun = true;
            // M142.2: Light.dat loaded but OFF by default — the composite already bakes the light pools
            // in, so the runtime lights double them up. Toggleable later if a map needs them.
            MeshPreview.BackgroundLights = bg.Lights;
            MeshPreview.BackgroundLightsEnabled = false;
            ShowMeshPreviewWindow?.Invoke();
            Status = $"Legacy map {bg.MapName} loaded.";
            _log.Success("Map", $"Legacy NVR map {bg.MapName}: {bg.MeshCount:n0} meshes, {bg.Mesh.VertexCount:n0} verts"
                + (bg.MissingTextures > 0 ? $", {bg.MissingTextures} texture(s) unresolved." : "."));
        }
        catch (Exception ex) { _log.Error("Map", $"Legacy map load failed: {ex.Message}"); Status = "Legacy map load failed."; }
    }

    private async Task LoadMeshPreviewAsync(WadAssetEntry entry)
    {
        if (!ContentLoaded) return;
        try
        {
            var (mesh, skeleton, textures, vfx) = await Task.Run(() =>
            {
                var m = SkinnedMeshDecoder.Decode(ReadAsset(entry.PathHash));
                var s = TryPairSkeleton(entry);
                var t = TryLoadPreviewDiffuse(entry, m);
                var v = TryLoadChampionVfxWithResources(entry);   // M55/M86: skin VFX library + resource map
                return (m, s, t, v);
            });
            // M85: game-accurate submesh visibility — skin bin initial-hide + animation-graph clip lists.
            var (initialHide, clipsByAnm, ownAnms) = LoadSubmeshRules(entry);
            await Task.Run(() => LoadChampionAudio(entry));   // M90: clip SFX banks

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                MeshPreview.Show(entry.DisplayName, mesh, skeleton, textures);
                MeshPreview.SetSubmeshRules(initialHide, clipsByAnm);
                MeshPreview.SetAnimations(mesh.CanSkin && skeleton is not null
                    ? FindAnimations(entry, ownAnms)
                    : Enumerable.Empty<AnimationEntryViewModel>());
                MeshPreview.SetVfx(vfx.systems, vfx.resourceMap);
                MeshPreview.SetVoiceEvents(TryLoadVoiceEvents(entry));   // M95c: authored VO lines
                MeshInspector.ShowMesh(mesh, skeleton);
                ShowMeshPreviewWindow?.Invoke();
                _log.Success("Mesh", $"{entry.DisplayName}: {mesh.VertexCount:n0} verts, {mesh.TriangleCount:n0} tris — model preview window.");
            });
            _ = ApplyPreviewBackgroundAsync();   // M88: stream in the NVR map backdrop (non-blocking)
        }
        catch (Exception ex) { _log.Error("Mesh", $"{entry.DisplayName}: {ex.Message}"); }
    }

    // M88: cache the last-loaded backdrop so re-previewing skins doesn't re-read the ~60 MB room.nvr.
    private Services.MapPreviewBackground? _previewBackground;
    private string? _previewBackgroundFolder;

    /// <summary>Load (or reuse) the configured NVR map backdrop and attach it to the preview window.
    /// Silent no-op when the feature is off or the folder isn't a legacy map.</summary>
    private async Task ApplyPreviewBackgroundAsync()
    {
        try
        {
            string folder = Settings.PreviewBackgroundMapFolder;
            if (!Settings.PreviewBackgroundEnabled || !Services.MapPreviewLoader.IsNvrMapFolder(folder))
            {
                await Dispatcher.UIThread.InvokeAsync(() => MeshPreview.SetBackground(null));
                return;
            }

            if (_previewBackground is null || !string.Equals(_previewBackgroundFolder, folder, StringComparison.OrdinalIgnoreCase))
            {
                _log.Info("Preview", $"Loading map backdrop from {Path.GetFileName(folder)}…");
                var bg = await Task.Run(() => Services.MapPreviewLoader.Load(folder));
                _previewBackground = bg;
                _previewBackgroundFolder = folder;
                _log.Success("Preview", $"Backdrop '{bg.MapName}': {bg.MeshCount:n0} meshes, {bg.Mesh.TriangleCount:n0} tris, {bg.Lights.Count} lights" +
                                        (bg.MissingTextures > 0 ? $" ({bg.MissingTextures} submesh(es) untextured)" : ""));
            }

            var loaded = _previewBackground;
            await Dispatcher.UIThread.InvokeAsync(() => MeshPreview.SetBackground(loaded));
        }
        catch (Exception ex) { _log.Error("Preview", $"Map backdrop: {ex.Message}"); }
    }

    /// <summary>Per-submesh diffuse textures for the model-preview window — NO side effects on the main
    /// viewport's texture/material state (unlike BuildSubmeshTextures, which publishes to it).</summary>
    private IReadOnlyList<TextureImage?>? TryLoadPreviewDiffuse(WadAssetEntry skn, MeshAsset mesh)
    {
        if (!ContentLoaded || !skn.IsResolved) return null;
        var binPath = SkinPaths.BinPathForSkn(skn.Path);
        if (binPath is null || !TryResolveEntry(HashAlgorithms.WadPath(binPath), out var binEntry)) return null;
        var resolved = ChampionMaterialResolver.Resolve(GetAssetBytes(binEntry), ResolveBinName);
        if (!resolved.HasAny) return null;
        var cache = new Dictionary<string, TextureImage?>(StringComparer.OrdinalIgnoreCase);
        var result = new TextureImage?[mesh.SubMeshes.Count];
        for (int i = 0; i < mesh.SubMeshes.Count; i++)
        {
            var p = resolved.For(mesh.SubMeshes[i].Material);
            if (string.IsNullOrEmpty(p)) continue;
            if (!cache.TryGetValue(p, out var img)) cache[p] = img = LoadTextureByPath(p);
            result[i] = img;
        }
        return result;
    }

    private async Task LoadMeshAsync(WadAssetEntry entry)
    {
        if (!ContentLoaded) return;
        try
        {
            var (mesh, skeleton, textures, vfx) = await Task.Run(() =>
            {
                var m = SkinnedMeshDecoder.Decode(ReadAsset(entry.PathHash));
                var s = TryPairSkeleton(entry);
                var t = TryLoadTextures(entry, m);
                var v = TryLoadChampionVfx(entry);
                return (m, s, t, v);
            });

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                CurrentMesh = mesh;
                CurrentSkeleton = skeleton;
                CurrentModelTextures = textures;
                SetChampionVfx(vfx);
                CurrentModelLightmapTextures = null; // champions/skinned meshes have no map baked lightmaps
                HasFlowmapWater = false;             // M44: only maps carry flowmap water
                if (textures is null) CurrentModelSubmeshMaterials = null; // flat mesh — no per-material data
                ShowBones = skeleton is not null;
                MeshInspector.ShowMesh(mesh, skeleton);
                Animation.SetSkeleton(skeleton?.BoneCount ?? 0);
                Animation.SetAnimations(mesh.CanSkin && skeleton is not null
                    ? FindAnimations(entry)
                    : Enumerable.Empty<AnimationEntryViewModel>());
                _log.Success("Mesh", $"{entry.DisplayName}: {mesh.VertexCount:n0} verts, {mesh.TriangleCount:n0} tris, {mesh.SubMeshes.Count} submesh(es)" +
                                     (skeleton is null ? "" : $", {skeleton.BoneCount} bones"));
            });
        }
        catch (Exception ex)
        {
            _log.Error("Mesh", $"{entry.DisplayName}: {ex.Message}");
            await Dispatcher.UIThread.InvokeAsync(ClearViewport);
        }
    }

    /// <summary>M124: reload the open map from its current bytes — after Save Map Edits (appended
    /// meshes become native geometry) or a materials change (staged meshes pick up their textures).
    /// Unsaved transforms, layer edits and staged meshes are lost, so it confirms first.</summary>
    [RelayCommand]
    private async Task ReloadMap()
    {
        if (_currentMapEntry is not { } entry) { _log.Warn("MapGeo", "No map open to reload."); return; }
        bool hasEdits = (_currentMap is { } m && (MapGeoWriter.HasMoves(m.Meshes) || MapGeoLayerWriter.HasEdits(m.Meshes)))
                        || MapContent.AddedMeshes.Count > 0;
        if (hasEdits && PromptOwner is not null
            && !await Views.PromptWindow.ConfirmAsync(PromptOwner, "Reload Map",
                "Reload discards unsaved mesh moves, layer edits and staged meshes." + (char)10 + (char)10 + "Save Map Edits first if you want to keep them.", "Reload"))
            return;

        MapContent.AddedMeshes.Clear();
        OnPropertyChanged(nameof(HasAddedMeshes));
        // drop the tab's cached scene so switching tabs can't restore the stale state
        if (Documents.FirstOrDefault(d => d.Key == entry.PathHash) is { } doc) doc.Scene = null;
        _log.Info("MapGeo", $"Reloading {entry.DisplayName}…");
        await LoadMapGeoAsync(entry);
    }

    private async Task LoadMapGeoAsync(WadAssetEntry entry)
    {
        if (!ContentLoaded) return;
        try
        {
            _log.Info("MapGeo", $"Decoding {entry.DisplayName} …");
            var rawMapBytes = ReadAsset(entry.PathHash);
            var (map, mesh, textures, sunProperties) = await Task.Run(() =>
            {
                var m = MapGeoDecoder.Decode(rawMapBytes);
                var meshAsset = new MeshAsset
                {
                    Positions = m.Positions,
                    Normals = m.Normals,
                    Uvs = m.Uvs,
                    Colors = m.Colors,
                    LightmapUvs = m.LightmapUvs,
                    BakedPaintUvs = m.BakedPaintUvs,
                    Indices = m.Indices,
                    VertexCount = m.VertexCount,
                    SubMeshes = m.Groups.Select(g => new SubMeshInfo(g.Material, g.StartIndex, g.IndexCount, 0)).ToList(),
                    BoundsMin = m.BoundsMin,
                    BoundsMax = m.BoundsMax,
                };
                var loaded = TryLoadMapTextures(entry, m);
                return (m, meshAsset, loaded.Textures, loaded.SunProperties);
            });

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                CurrentSkeleton = null;
                ShowBones = false;
                CurrentMesh = mesh;
                if (_currentMap is { } replacedMap) UndoService.PurgeContext(replacedMap); // stale transform commands
                _currentMap = map;
                InvalidateRayIndex();
                PrebuildRayIndex(map, MeshVerticesRevision);   // M172a: warm it so the first click is instant
                _currentMapBytes = rawMapBytes;
                _currentMapEntry = entry;
                MapGeneration++;
                HasMapGeo = true;   // M79
                OnPropertyChanged(nameof(CanBakeLighting));   // M158
                OnPropertyChanged(nameof(HasMapForLayout));  // M147
                OnPropertyChanged(nameof(MeshesWithoutLightmapUv));
        OnPropertyChanged(nameof(HasMapForLayout));  // M147
        OnPropertyChanged(nameof(MeshesWithoutLightmapUv));
                _selection.Clear();
                CurrentModelTextures = textures;
                ApplySunProperties(sunProperties);
                // M287: and then put back whatever the user authored for THIS map. ApplySunProperties has
                // just overwritten sun/sky with the map's own values and forced SunIntensity to 1.0, which
                // is correct as a starting point and wrong as a final answer once the project holds edits.
                RestoreMapLighting(entry);
                ClearSecondaryTextures(); // maps don't use champion secondary samplers
                PublishMapMaterialLayers(); // re-apply map special-material layers wiped above
                MapGeoInspector.Show(map, entry.Path);
                MapContent.SetBucketGrids(map.BucketGrids);   // M55: culling grid showcase
                HasBucketGrids = map.BucketGrids.Count > 0;   // M77
                RebuildBucketGridLines();
                MapContent.ShowMap(entry.DisplayName, map.Groups
                    .Select((g, i) => new MapPieceViewModel { Name = string.IsNullOrEmpty(g.Material) ? $"Mesh {i}" : g.Material, Info = $"{g.IndexCount / 3:n0} tris" })
                    .ToList());
                BuildMapVisibility(entry.Path, map);
                BuildMapLayerGroups(map);
                ApplyMapVisibility();    // ensure reset even if the index was already 0
                _log.Success("MapGeo", $"{entry.DisplayName}: v{map.Version}, {map.MeshCount:n0} meshes, {map.VertexCount:n0} verts, {map.TriangleCount:n0} tris, {map.MaterialCount} materials" +
                                       (map.Warnings.Count > 0 ? $", {map.Warnings.Count} warnings" : ""));
            });
        }
        catch (Exception ex)
        {
            _log.Error("MapGeo", $"{entry.DisplayName}: {ex.Message}");
            await Dispatcher.UIThread.InvokeAsync(ClearViewport);
        }
    }

    /// <summary>Resolve the map's materials .bin → per-group diffuse textures (shared instances for reuse).</summary>
    private (IReadOnlyList<TextureImage?>? Textures, Formats.MapGeo.MapSunProperties? SunProperties)
        TryLoadMapTextures(WadAssetEntry mapEntry, MapGeoAsset map)
    {
        if (!ContentLoaded || !mapEntry.IsResolved) return (null, null);

        if (!TryResolveMaterialsBin(mapEntry.Path, out var binEntry))
        {
            _log.Info("MapGeo", $"No materials .bin found for {mapEntry.DisplayName} — rendering flat.");
            return (null, null);
        }

        // M35: placed particle systems live in the same materials.bin (MapPlaceableContainer.items).
        // M36: the VfxSystemDefinitions they reference live in the same bin too — parse them for playback.
        try
        {
            var binBytes = GetAssetBytes(binEntry);
            _vfxSystems = VfxSystemResolver.ExtractAll(binBytes);
            RebuildRelinkChoices();   // M205: the re-link picker's candidate list
            var particles = MapParticleExtractor.Extract(binBytes, hash =>
                _vfxSystems.TryGetValue(hash, out var system) ? system.ParticlePath : ResolveBinName(hash));
            CurrentModelParticles = particles.Count > 0 ? particles : null;
            if (particles.Count > 0) _log.Info("MapGeo", $"{particles.Count:n0} placed particle system(s) ({particles.Select(p => p.SystemPath).Distinct().Count()} unique, {_vfxSystems.Count} definitions).");

            // M38: cubemap reflection probes + animated props (placed characters) from the same bin.
            // M55: + MapAudio sound placements (Wwise events at world positions).
            var (probes, props, directSounds) = MapPlaceableExtractor.Extract(binBytes);
            var particleSounds = MapParticleAudioExtractor.Extract(particles, _vfxSystems);
            var sounds = directSounds.Concat(particleSounds).ToList();
            CurrentModelProbes = probes.Count > 0 ? probes : null;
            CurrentModelProps = props.Count > 0 ? props : null;
            CurrentModelSounds = sounds.Count > 0 ? sounds : null;
            if (probes.Count > 0 || props.Count > 0 || sounds.Count > 0)
                _log.Info("MapGeo", $"{probes.Count} cubemap probe(s), {props.Count} animated prop(s) ({props.Select(p => p.CharacterName).Distinct().Count()} characters), {sounds.Count} sound placement(s).");
            LoadMapAudioBanks(binEntry.Path, sounds);   // M56/M60: direct MapAudio + VFX-carried map ambience
        }
        catch { CurrentModelParticles = null; _vfxSystems = EmptyVfx; CurrentModelProbes = null; CurrentModelProps = null; CurrentModelSounds = null; }

        var names = map.Groups.Select(g => g.Material).Where(m => m.Length > 0).Distinct().ToList();
        var (materialToTexture, profiles, sunProperties) = ResolveMapMaterials(binEntry, names);
        if (materialToTexture.Count == 0)
        {
            _log.Info("MapGeo", "Materials .bin didn't resolve any textures — rendering flat.");
            return (null, sunProperties);
        }
        _currentMaterialToTexture = materialToTexture;   // M172c: the paint session needs per-submesh paths
        return (BuildMapTextures(map, materialToTexture, profiles, names.Count, mapEntry.Path), sunProperties);
    }

    /// <summary>Resolve map material→texture (+ M32 profiles), falling back to the original game
    /// .materials.bin when the project's copy is broken (malformed .bin) or resolves nothing.</summary>
    private (Dictionary<string, string> textures, Dictionary<string, MaterialProfile> profiles,
        Formats.MapGeo.MapSunProperties? sunProperties) ResolveMapMaterials(WadAssetEntry binEntry, List<string> names)
    {
        try
        {
            var bytes = GetAssetBytes(binEntry);
            var r = MapGeoMaterialResolver.Resolve(bytes, names);
            if (r.Count > 0)
            {
                return (r, MaterialProfiles.ForMapMaterials(bytes, names, ResolveBinName),
                    Formats.MapGeo.MapLighting.EffectiveSun(bytes));
            }
        }
        catch (Exception ex) { _log.Warn("MapGeo", $"project materials.bin parse failed: {ex.Message}"); }

        var fb = _mounts?.ReadFallback(binEntry.PathHash);
        if (fb is not null)
        {
            try
            {
                var r = MapGeoMaterialResolver.Resolve(fb, names);
                if (r.Count > 0)
                {
                    _log.Info("MapGeo", "Used the original game materials.bin (the project's copy was broken/empty).");
                    return (r, MaterialProfiles.ForMapMaterials(fb, names, ResolveBinName),
                        Formats.MapGeo.MapLighting.EffectiveSun(fb));
                }
            }
            catch (Exception ex) { _log.Warn("MapGeo", $"game materials.bin parse failed: {ex.Message}"); }
        }
        return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, MaterialProfile>(StringComparer.OrdinalIgnoreCase), null);
    }

    /// <summary>M45: read the MapContainer's MapSunProperties component and publish what the renderer uses
    /// (lightMapColorScale — the game's baked-light multiplier, e.g. 2.0 on Map12 Bloom).</summary>
    private void ApplySunProperties(Formats.MapGeo.MapSunProperties? sun)
    {
        _baseSunAuthored = sun;   // M71: remembered so "Reset lighting" can restore the map's authored values
        // M71: keep the authored sun (direction + any HDR fields) as the base; the manual sliders replace only
        // colour/scale on top of it. When the map has no sun component, fall back to the renderer's own
        // defaults (dir/0.75 sun/0.35 sky) so nothing changes visually until the user touches a slider.
        _baseSun = sun ?? new MapSunProperties
        {
            SunDirection = new System.Numerics.Vector3(0.4f, 0.85f, 0.45f),
            SunColor = new System.Numerics.Vector4(0.75f, 0.75f, 0.75f, 1f),
            SkyLightColor = new System.Numerics.Vector4(0.35f, 0.35f, 0.35f, 1f),
            SkyLightScale = 1f,
        };
        // M287: this whole block RESETS the panel to the map's authored values. That is correct as a
        // starting point, but it must not be mistaken for something the user chose - so capture is
        // suppressed across it, or opening a map would immediately overwrite that map's saved record with
        // the reset and the restore below it would have nothing left to restore.
        _applyingLighting = true;
        try
        {
            _suppressSunRebuild = true;
            SunColorR = Clamp01(_baseSun.SunColor.X); SunColorG = Clamp01(_baseSun.SunColor.Y); SunColorB = Clamp01(_baseSun.SunColor.Z);
            SunIntensity = 1.0;
            SkyColorR = Clamp01(_baseSun.SkyLightColor.X); SkyColorG = Clamp01(_baseSun.SkyLightColor.Y); SkyColorB = Clamp01(_baseSun.SkyLightColor.Z);
            SkyIntensity = System.Math.Clamp(_baseSun.SkyLightScale, 0f, 8f);
            _suppressSunRebuild = false;
            RebuildSun();
            CurrentLightmapScale = sun?.LightMapColorScale ?? 1.0;
        }
        finally { _applyingLighting = false; }
        if (sun is not null)
            _log.Info("Map", $"MapSunProperties: lightMapColorScale={sun.LightMapColorScale:0.##}, " +
                             $"skyLightScale={sun.SkyLightScale:0.##}, sunColor=({sun.SunColor.X:0.##}, {sun.SunColor.Y:0.##}, {sun.SunColor.Z:0.##}), " +
                             $"fog {sun.FogStartAndEnd.X:0}..{sun.FogStartAndEnd.Y:0}");
    }

    // M71: base sun (map-authored or default); the sliders replace colour/scale on top of it.
    private MapSunProperties _baseSun = new()
    {
        SunDirection = new System.Numerics.Vector3(0.4f, 0.85f, 0.45f),
        SunColor = new System.Numerics.Vector4(0.75f, 0.75f, 0.75f, 1f),
        SkyLightColor = new System.Numerics.Vector4(0.35f, 0.35f, 0.35f, 1f),
        SkyLightScale = 1f,
    };
    private bool _suppressSunRebuild;
    private static double Clamp01(double v) => System.Math.Clamp(v, 0.0, 1.0);

    /// <summary>M71: fold the manual sun/sky sliders into CurrentSunProperties (bound to the viewport). Sun
    /// colour is scaled by its intensity; sky scale carries the sky intensity — exactly the two knobs the
    /// renderer's fallback term uses (col = base * encode(sky + sun * NdotL)).</summary>
    private void RebuildSun()
    {
        if (_suppressSunRebuild) return;
        CurrentSunProperties = _baseSun with
        {
            SunColor = new System.Numerics.Vector4((float)(SunColorR * SunIntensity), (float)(SunColorG * SunIntensity), (float)(SunColorB * SunIntensity), 1f),
            SkyLightColor = new System.Numerics.Vector4((float)SkyColorR, (float)SkyColorG, (float)SkyColorB, 1f),
            SkyLightScale = (float)SkyIntensity,
        };
        OnPropertyChanged(nameof(SunSwatch));
        OnPropertyChanged(nameof(SkySwatch));
        OnPropertyChanged(nameof(SunColorPick));   // M155
        OnPropertyChanged(nameof(SkyColorPick));
        // M287: the sun/sky sliders all funnel through here, so one capture covers the panel.
        CaptureMapLighting();
    }

    public Avalonia.Media.IBrush SunSwatch => Swatch(SunColorR * SunIntensity, SunColorG * SunIntensity, SunColorB * SunIntensity);
    public Avalonia.Media.IBrush SkySwatch => Swatch(SkyColorR * SkyIntensity, SkyColorG * SkyIntensity, SkyColorB * SkyIntensity);

    /// <summary>M155: sun/sky colour as a real Color so the lighting panel can use the picker instead of
    /// three sliders. These are the UNSCALED hues — the Intensity sliders stay separate, which is what
    /// makes a colour picker usable here (picking a hue shouldn't also change the brightness).</summary>
    public Avalonia.Media.Color SunColorPick
    {
        get => Col(SunColorR, SunColorG, SunColorB);
        set { _suppressSunRebuild = true; SunColorR = value.R / 255.0; SunColorG = value.G / 255.0; _suppressSunRebuild = false; SunColorB = value.B / 255.0; }
    }

    public Avalonia.Media.Color SkyColorPick
    {
        get => Col(SkyColorR, SkyColorG, SkyColorB);
        set { _suppressSunRebuild = true; SkyColorR = value.R / 255.0; SkyColorG = value.G / 255.0; _suppressSunRebuild = false; SkyColorB = value.B / 255.0; }
    }

    private static Avalonia.Media.Color Col(double r, double g, double b) => Avalonia.Media.Color.FromRgb(
        (byte)Math.Clamp(Math.Round(r * 255), 0, 255),
        (byte)Math.Clamp(Math.Round(g * 255), 0, 255),
        (byte)Math.Clamp(Math.Round(b * 255), 0, 255));
    private static Avalonia.Media.IBrush Swatch(double r, double g, double b) =>
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(B(r), B(g), B(b)));
    private static byte B(double v) => (byte)System.Math.Clamp(v * 255.0, 0, 255);

    partial void OnSunIntensityChanged(double value) => RebuildSun();
    partial void OnSunColorRChanged(double value) => RebuildSun();
    partial void OnSunColorGChanged(double value) => RebuildSun();
    partial void OnSunColorBChanged(double value) => RebuildSun();
    partial void OnSkyIntensityChanged(double value) => RebuildSun();
    partial void OnSkyColorRChanged(double value) => RebuildSun();
    partial void OnSkyColorGChanged(double value) => RebuildSun();
    partial void OnSkyColorBChanged(double value) => RebuildSun();

    /// <summary>M71: restore sun/sky/lightmap to the loaded map's authored values.</summary>
    [RelayCommand]
    private void ResetLighting() => ApplySunProperties(_baseSunAuthored);
    private Formats.MapGeo.MapSunProperties? _baseSunAuthored;

    /// <summary>
    /// Resolve a mapgeo's companion .materials.bin, tolerating renamed copies (a mod folder often holds
    /// "base_srx - Kopie.mapgeo" whose materials are still the original "base_srx.materials.bin").
    /// </summary>
    private bool TryResolveMaterialsBin(string mapgeoPath, out WadAssetEntry binEntry)
    {
        const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

        var direct = MapGeoMaterialResolver.MaterialsBinPathFor(mapgeoPath);
        if (TryResolveEntry(HashAlgorithms.WadPath(direct), out binEntry)) return true;

        int slash = direct.LastIndexOf('/');
        string dir = slash < 0 ? "" : direct[..(slash + 1)];
        string file = direct[dir.Length..];
        string stem = file.EndsWith(".materials.bin", OIC) ? file[..^".materials.bin".Length] : file;

        // Strip "copy" suffixes (Windows/Explorer in several languages) and retry — the stripped name
        // usually exists in the game fallback.
        string cleaned = StripCopySuffix(stem);
        if (!cleaned.Equals(stem, OIC) &&
            TryResolveEntry(HashAlgorithms.WadPath(dir + cleaned + ".materials.bin"), out binEntry)) return true;

        // Last resort: any sibling .materials.bin in the same folder of the loaded project.
        foreach (var e in AssetEntries)
            if (e.IsResolved && e.Path.EndsWith(".materials.bin", OIC))
            {
                int s = e.Path.LastIndexOf('/');
                var d = s < 0 ? "" : e.Path[..(s + 1)];
                if (d.Equals(dir, OIC)) { binEntry = e; return true; }
            }

        binEntry = null!;
        return false;
    }

    private static string StripCopySuffix(string name)
    {
        string[] suffixes = { " - Kopie", " - Copy", " - copia", " - copie", " - Copie", " copy", "_copy", " (1)", " (2)", " (3)" };
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var sfx in suffixes)
                if (name.EndsWith(sfx, StringComparison.OrdinalIgnoreCase)) { name = name[..^sfx.Length]; changed = true; }
        }
        return name;
    }

    /// <summary>Per-group diffuse textures from resolved map material→texture map (override-aware loads).
    /// Also publishes the per-group preview materials (UV transform + specular flag) from the profiles (M32).</summary>
    private IReadOnlyList<TextureImage?> BuildMapTextures(MapGeoAsset map, Dictionary<string, string> materialToTexture,
        Dictionary<string, MaterialProfile> profilesByName, int materialCount, string? mapGeoPath)
    {
        var cache = new Dictionary<string, TextureImage?>(StringComparer.OrdinalIgnoreCase);
        TextureImage? Load(string path)
        {
            if (cache.TryGetValue(path, out var hit)) return hit;
            return cache[path] = LoadTextureByPath(path);
        }

        _currentMapProfiles = profilesByName; // M34: cache for the mesh inspector's render-state rows

        var result = new TextureImage?[map.Groups.Count];
        var lightmaps = new TextureImage?[map.Groups.Count];
        var flowMaps = new TextureImage?[map.Groups.Count];    // slot 1: flow map or terrain RGB blend mask
        var flowNormals = new TextureImage?[map.Groups.Count]; // slot 2: flow normal or terrain middle layer
        var terrainTops = new TextureImage?[map.Groups.Count];   // slot 3 (emissive reused by terrain branch)
        var terrainExtras = new TextureImage?[map.Groups.Count]; // slot 4 (matcap reused by terrain branch)
        var submeshMats = new ViewportMeshRenderer.SubmeshMaterial[map.Groups.Count];
        var terrainWorldTransform = MapGeoMaterialResolver.TerrainBlendWorldTransformFor(map,
            profilesByName.Where(x => x.Value.TerrainWorldProjectedMask).Select(x => x.Key));
        // Per-mesh mirrored (negative-determinant) flag, for the two-sided/mirrored render state (M34).
        var mirroredByMesh = map.Meshes.ToDictionary(m => m.Index, m => m.IsMirrored);
        int lmGroups = 0, flowGroups = 0, terrainGroups = 0, bakedPaintGroups = 0;
        for (int i = 0; i < map.Groups.Count; i++)
        {
            var matName = map.Groups[i].Material;
            if (materialToTexture.TryGetValue(matName, out var path))
                result[i] = Load(path);
            if (profilesByName.TryGetValue(matName, out var prof))
            {
                submeshMats[i] = ToSubmeshMaterial(prof, terrainWorldTransform);
                LogUvTransform(prof, matName);

                // Shader 0xe25b830f: load the opaque terrain splat layers. Renderer slots are deliberately
                // reused because regular emissive/matcap effects are disabled inside the terrain branch.
                if (prof.IsTerrainBlend)
                {
                    if (!string.IsNullOrEmpty(prof.TerrainBottomPath)) result[i] = Load(prof.TerrainBottomPath);
                    string? terrainMask = prof.TerrainMaskPath;
                    if (prof.TerrainWorldProjectedMask && !string.IsNullOrEmpty(mapGeoPath))
                        terrainMask = MapGeoMaterialResolver.TerrainBlendTexturePathFor(mapGeoPath);
                    if (!string.IsNullOrEmpty(terrainMask)) flowMaps[i] = Load(terrainMask);
                    if (!string.IsNullOrEmpty(prof.TerrainMiddlePath)) flowNormals[i] = Load(prof.TerrainMiddlePath);
                    if (!string.IsNullOrEmpty(prof.TerrainTopPath)) terrainTops[i] = Load(prof.TerrainTopPath);
                    if (!string.IsNullOrEmpty(prof.TerrainExtrasPath)) terrainExtras[i] = Load(prof.TerrainExtrasPath);
                    terrainGroups++;
                }

                // M44 flowmap river water: load the Flow_Map + Flowing_Normal textures into the mask/gradient
                // slots the water shader samples (slots 1/2). Falls back to a flat animated look if missing.
                if (prof.IsFlowmap)
                {
                    if (!string.IsNullOrEmpty(prof.FlowMapPath)) flowMaps[i] = Load(prof.FlowMapPath);
                    if (!string.IsNullOrEmpty(prof.FlowNormalPath)) flowNormals[i] = Load(prof.FlowNormalPath);
                    flowGroups++;
                    if (flowGroups <= 3)   // M44 diagnostic: confirm detection + texture loads for the first few
                    {
                        // Channel histogram of the flow map (B = water mask, R = phase, G = flow) so the
                        // shader's channel mapping can be sanity-checked against the real texture values.
                        string gstat = "";
                        if (flowMaps[i] is { } fmImg && fmImg.Rgba.Length >= 4)
                        {
                            long cnt = 0, bHi = 0; double rSum = 0, gSum = 0, bSum = 0;
                            var px = fmImg.Rgba;
                            for (int o = 0; o + 2 < px.Length; o += 64)   // every 16th pixel
                            {
                                rSum += px[o]; gSum += px[o + 1]; bSum += px[o + 2]; cnt++;
                                if (px[o + 2] > 128) bHi++;
                            }
                            if (cnt > 0) gstat = $" R={rSum / cnt / 255.0:0.00} G={gSum / cnt / 255.0:0.00} " +
                                                 $"B={bSum / cnt / 255.0:0.00} (water {bHi * 100 / cnt}%)";
                        }
                        _log.Info("Water", $"flowmap '{matName}': flowMap={(flowMaps[i] is not null ? "OK" : "miss")} " +
                                           $"normal={(flowNormals[i] is not null ? "OK" : "miss")} " +
                                           $"speed={prof.FlowSpeed:0.###} alpha={prof.WaterAlpha:0.##}{gstat}");
                    }
                }
            }
            else submeshMats[i] = ViewportMeshRenderer.SubmeshMaterial.Default;

            if (mirroredByMesh.TryGetValue(map.Groups[i].MeshIndex, out var mir) && mir)
                submeshMats[i] = submeshMats[i] with { Mirrored = true };

            // M319/M320: DefaultEnv_Flat_BakedTerrain deliberately points its material sampler at
            // black.tex. The actual atlas is a per-MESH BAKED_DIFFUSE_TEXTURE override in mapgeo.
            // Its final UV is decoded separately from raw Texcoord7 and selected by UsesBakedPaint;
            // ordinary material UV transforms continue to operate on Texcoord0.
            var bakedPaintPath = map.Groups[i].BakedPaintTexture;
            if (!string.IsNullOrEmpty(bakedPaintPath))
            {
                var bakedPaint = Load(bakedPaintPath);
                if (bakedPaint is not null) result[i] = bakedPaint;
                submeshMats[i] = submeshMats[i] with
                {
                    UsesBakedPaint = true,
                };
                if (bakedPaint is not null) bakedPaintGroups++;
            }

            // Baked lightmap: the group's BakedLight atlas (mesh already carries the uv7*scale+bias UVs).
            var lmPath = map.Groups[i].LightmapTexture;
            if (!string.IsNullOrEmpty(lmPath)) { lightmaps[i] = Load(lmPath); if (lightmaps[i] is not null) lmGroups++; }
        }
        CurrentModelSubmeshMaterials = submeshMats;
        CurrentModelLightmapTextures = lmGroups > 0 ? lightmaps : null;

        // M78: any VertexDeform+USE_GRASS_TINT_MAP group → publish the map's world-space grass tint.
        int gtGroups = submeshMats.Count(m => m.UsesGrassTint);
        if (gtGroups > 0)
        {
            var gtPath = FindGrassTintTexturePath();
            CurrentGrassTint = gtPath is not null ? Load(gtPath) : null;
            CurrentGrassTintRect = new System.Numerics.Vector4(
                map.BoundsMin.X, map.BoundsMin.Z,
                1f / MathF.Max(1f, map.BoundsMax.X - map.BoundsMin.X),
                1f / MathF.Max(1f, map.BoundsMax.Z - map.BoundsMin.Z));
            _log.Info("GrassTint", gtPath is not null
                ? $"{gtGroups} grass-tint group(s) — {gtPath}"
                : $"{gtGroups} grass-tint group(s), but no grasstint texture found in the mounts.");
        }
        else CurrentGrassTint = null;
        // Stash map-only secondary layers. A later ClearSecondaryTextures() on the load path wipes the channels,
        // so the UI-thread load code republishes them from these fields.
        _mapFlowMasks = flowGroups + terrainGroups > 0 ? flowMaps : null;
        _mapFlowGrads = flowGroups + terrainGroups > 0 ? flowNormals : null;
        _mapTerrainTops = terrainGroups > 0 ? terrainTops : null;
        _mapTerrainExtras = terrainGroups > 0 ? terrainExtras : null;
        PublishMapMaterialLayers();

        int unique = cache.Values.Count(v => v is not null);
        int spec = submeshMats.Count(m => m.UsesSpecular);
        _log.Success("MapGeo", $"Loaded {unique} unique textures ({materialToTexture.Count}/{materialCount} materials resolved)" +
                               (spec > 0 ? $", {spec} group(s) with specular." : ".") +
                               (lmGroups > 0 ? $" {lmGroups} group(s) with baked lightmaps." : "") +
                               (flowGroups > 0 ? $" {flowGroups} flowmap-water group(s)." : "") +
                               (terrainGroups > 0 ? $" {terrainGroups} terrain-blend group(s)." : "") +
                               (bakedPaintGroups > 0 ? $" {bakedPaintGroups} baked-terrain group(s)." : ""));
        return result;
    }

    /// <summary>Find the skin .bin for a .skn, resolve per-submesh diffuse textures, decode them.</summary>
    private IReadOnlyList<TextureImage?>? TryLoadTextures(WadAssetEntry skn, MeshAsset mesh)
    {
        if (!ContentLoaded || !skn.IsResolved) return null;

        var binPath = SkinPaths.BinPathForSkn(skn.Path);
        if (binPath is null || !TryResolveEntry(HashAlgorithms.WadPath(binPath), out var binEntry))
        {
            _log.Info("Material", $"No skin .bin found for {skn.DisplayName} (flat shading).");
            return null;
        }
        var resolved = ChampionMaterialResolver.Resolve(GetAssetBytes(binEntry), ResolveBinName);
        if (!resolved.HasAny)
        {
            _log.Info("Material", $"No skin material found for {skn.DisplayName} (flat shading).");
            return null;
        }
        return BuildSubmeshTextures(mesh, resolved, skn.DisplayName);
    }

    /// <summary>Parse the champion skin's VFX library from its .bin (M37). Empty when there's no skin bin.</summary>
    private IReadOnlyDictionary<uint, VfxSystemDefinition> TryLoadChampionVfx(WadAssetEntry skn)
        => TryLoadChampionVfxWithResources(skn).systems;

    // ---- M90: champion SFX for the model preview (clip SoundEventData -> Wwise banks) ----
    private Formats.Audio.AudioBankSet? _previewAudioBanks;

    /// <summary>Load the champion's SFX banks (base + the previewed skin's own folder when it has one):
    /// sounds/wwise2016/sfx/characters/&lt;champ&gt;/skins/&lt;base|skinNN&gt;/*.bnk|.wpk.</summary>
    private void LoadChampionAudio(WadAssetEntry skn)
    {
        _previewAudioBanks = null;
        try
        {
            if (!skn.IsResolved) return;
            var parts = skn.Path.Split('/');
            int ci = Array.FindIndex(parts, p => p.Equals("characters", StringComparison.OrdinalIgnoreCase));
            if (ci < 0 || ci + 1 >= parts.Length) return;
            string champ = parts[ci + 1];
            // the skn's skin folder (skin03) — its banks override/extend base for newer skins
            string skinFolder = parts.FirstOrDefault(p => p.StartsWith("skin", StringComparison.OrdinalIgnoreCase)) ?? "";
            string marker = $"/sfx/characters/{champ}/skins/";
            const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

            var set = new Formats.Audio.AudioBankSet();
            int banks = 0, packs = 0;
            foreach (var e in AssetEntries)
            {
                if (!e.IsResolved) continue;
                var p = e.Path;
                int mi = p.IndexOf(marker, OIC);
                if (mi < 0) continue;
                string folder = p[(mi + marker.Length)..].Split('/')[0];
                if (!folder.Equals("base", OIC) && !folder.Equals(skinFolder, OIC)) continue;
                try
                {
                    if (p.EndsWith(".bnk", OIC))
                    { if (Formats.Audio.BnkFile.Parse(ReadAsset(e.PathHash)) is { } b) { set.AddBank(b, e.PathHash, p); banks++; } }
                    else if (p.EndsWith(".wpk", OIC))
                    { if (Formats.Audio.WpkFile.Parse(ReadAsset(e.PathHash)) is { } w) { set.AddPack(w, e.PathHash, p); packs++; } }
                }
                catch { /* skip broken banks */ }
            }
            // M95b: projects usually don't mount the champion's WAD at all (map projects, folder
            // projects), so the mount scan above finds nothing — fall back to the ORIGINAL
            // Champions/<Champ>.wad.client in the game install, like the mesh/texture fallback does.
            if (banks + packs == 0 && FindChampionWad(champ, locale: null) is { } mainWad)
            {
                int n = LoadBanksFromWadFile(set, mainWad, champ, skinFolder);
                if (n > 0) { banks += n; _log.Info("Audio", $"{champ}: SFX banks read from the original game WAD (not in project mounts)."); }
            }

            // M95: voice-over lives in the champion's LOCALE WAD (Aatrox.en_US.wad.client), which is
            // never mounted — open it directly from the game install and merge its VO banks so
            // Play_vo_ clip events (jokes, taunts, laughs) speak like in-game.
            int voBanks = FindChampionWad(champ, locale: "*") is { } voWad
                ? LoadBanksFromWadFile(set, voWad, champ, skinFolder) : 0;

            if (!set.IsEmpty)
            {
                _previewAudioBanks = set;
                _log.Info("Audio", $"{champ} SFX: {banks} bank(s) + {packs} pack(s)" +
                    (voBanks > 0 ? $", VO: {voBanks} bank(s)" : "") +
                    $" — {set.EventCount} event(s), {set.WemCount} wem(s).");
            }
        }
        catch { /* audio is optional */ }
    }

    /// <summary>M95c: the skin bin's authored VO event names (skinAudioProperties.bankUnits) — voice
    /// lines are triggered by game logic through these, never by animation clip events.</summary>
    private IReadOnlyList<string> TryLoadVoiceEvents(WadAssetEntry skn)
    {
        try
        {
            if (!skn.IsResolved) return Array.Empty<string>();
            var binPath = SkinPaths.BinPathForSkn(skn.Path);
            if (binPath is null || !TryResolveEntry(HashAlgorithms.WadPath(binPath), out var be)) return Array.Empty<string>();
            return Formats.Skeletons.ChampionAnimationData.ParseBankEvents(GetAssetBytes(be))
                .Where(e => e.StartsWith("Play_vo_", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>M95: locate a champion WAD in the game install. locale null → the main WAD;
    /// locale "*" → any locale companion (Aatrox.en_US.wad.client…), preferring en_US.</summary>
    private string? FindChampionWad(string champ, string? locale)
    {
        try
        {
            string? gameDir = !string.IsNullOrEmpty(Project.GameDirectory) && Directory.Exists(Project.GameDirectory)
                ? Project.GameDirectory
                : ReyEngine.Core.Projects.GameInstallLocator.Discover().FirstOrDefault()?.GameDirectory;
            if (gameDir is null) return null;
            string champsDir = Path.Combine(gameDir, "DATA", "FINAL", "Champions");
            if (!Directory.Exists(champsDir)) return null;

            if (locale is null)
            {
                string main = Path.Combine(champsDir, champ + ".wad.client");
                return File.Exists(main) ? main : null;
            }
            return Directory.EnumerateFiles(champsDir, $"{champ}.*.wad.client")
                .Where(f => System.Text.RegularExpressions.Regex.IsMatch(
                    Path.GetFileName(f), $@"^{System.Text.RegularExpressions.Regex.Escape(champ)}\.[a-z]{{2}}_[A-Z]{{2}}\.wad\.client$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                .OrderByDescending(f => f.Contains(".en_US.", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>M95: merge the base + previewed skin's audio banks from a WAD file on disk into
    /// <paramref name="set"/>. Returns the number of banks/packs added.</summary>
    private int LoadBanksFromWadFile(Formats.Audio.AudioBankSet set, string wadPath, string champ, string skinFolder)
    {
        try
        {
            const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
            string marker = $"/characters/{champ}/skins/";
            int added = 0;
            using var wad = ReyEngine.Core.Wad.WadArchive.Open(wadPath, _resolver.Database);
            foreach (var e in wad.Entries)
            {
                if (!e.IsResolved) continue;
                var p = e.Path;
                int mi = p.IndexOf(marker, OIC);
                if (mi < 0) continue;
                string folder = p[(mi + marker.Length)..].Split('/')[0];
                if (!folder.Equals("base", OIC) && !folder.Equals(skinFolder, OIC)) continue;
                try
                {
                    if (p.EndsWith(".bnk", OIC))
                    { if (Formats.Audio.BnkFile.Parse(wad.Extract(e)) is { } b) { set.AddBank(b, e.PathHash, p); added++; } }
                    else if (p.EndsWith(".wpk", OIC))
                    { if (Formats.Audio.WpkFile.Parse(wad.Extract(e)) is { } w) { set.AddPack(w, e.PathHash, p); added++; } }
                }
                catch { /* skip broken banks */ }
            }
            return added;
        }
        catch { return 0; }
    }

    /// <summary>M90: play one clip sound event (e.g. Play_sfx_Aatrox_Death3D_cast) through the champion banks.</summary>
    private void PlayPreviewSoundEvent(string eventName)
    {
        try
        {
            if (_previewAudioBanks is null || !Sound.IsAvailable) return;
            var wems = _previewAudioBanks.ResolveEvent(eventName);
            if (wems.Count == 0) return;
            var wem = wems.Select(id => (Id: id, Data: _previewAudioBanks.GetWemData(id))).FirstOrDefault(x => x.Data is not null);
            if (wem.Data is null) return;
            if (Sound.DecodeToWav(wem.Id, wem.Data) is { } wav)
                Sound.PlayWav(wav, 1f, loop: false, tag: "previewsfx");
        }
        catch { /* never let SFX break the preview */ }
    }

    /// <summary>M86: the skin's VFX library + its ResourceResolver map (effect key → object hash), which
    /// is how animation clip particle events reference their effects. The skin bin itself holds almost no
    /// VFX — the systems live in its linked dependency bins (the multi-skin "longname" bins), so the
    /// whole link chain is followed and merged.</summary>
    private (IReadOnlyDictionary<uint, VfxSystemDefinition> systems, IReadOnlyDictionary<uint, uint>? resourceMap)
        TryLoadChampionVfxWithResources(WadAssetEntry skn)
    {
        if (!ContentLoaded || !skn.IsResolved) return (EmptyVfx, null);
        var binPath = SkinPaths.BinPathForSkn(skn.Path);
        if (binPath is null || !TryResolveEntry(HashAlgorithms.WadPath(binPath), out var binEntry)) return (EmptyVfx, null);
        try
        {
            var systems = new Dictionary<uint, VfxSystemDefinition>();
            var resMap = new Dictionary<uint, uint>();
            var visited = new HashSet<ulong> { binEntry.PathHash };
            var queue = new Queue<WadAssetEntry>();
            queue.Enqueue(binEntry);
            int guard = 0;
            while (queue.Count > 0 && guard++ < 64)
            {
                byte[] bytes;
                try { bytes = GetAssetBytes(queue.Dequeue()); } catch { continue; }
                foreach (var (k, v) in VfxSystemResolver.ExtractAll(bytes)) systems.TryAdd(k, v);
                foreach (var (k, v) in VfxSystemResolver.ExtractResourceMap(bytes)) resMap.TryAdd(k, v);
                foreach (var dep in VfxSystemResolver.ExtractDependencies(bytes))
                {
                    var h = HashAlgorithms.WadPath(dep);
                    if (visited.Add(h) && TryResolveEntry(h, out var depEntry)) queue.Enqueue(depEntry);
                }
            }
            return (systems, resMap.Count > 0 ? resMap : null);
        }
        catch { return (EmptyVfx, null); }
    }

    /// <summary>Map a Formats <see cref="MaterialProfile"/> to the renderer's per-submesh material (M32).</summary>
    /// <summary>M78: locate the map's grass-tint texture (mGrassTintTexture — usually
    /// ASSETS/Maps/Info/&lt;map&gt;/GrassTint_*.tex). Mount glob, preferring the current map's folder and
    /// the base (shortest-named, no dragon suffix) texture — mirrors the MapgeoAddon fallback chain.</summary>
    private string? FindGrassTintTexturePath()
    {
        if (_mounts is null) return null;
        var candidates = _mounts.Assets
            .Where(a => a.IsResolved && a.VirtualPath.Contains("grasstint", StringComparison.OrdinalIgnoreCase))
            .Select(a => a.VirtualPath)
            .ToList();
        if (candidates.Count == 0) return null;
        string token = "";
        if (_currentMapEntry?.Path is { } mp)
            token = Path.GetFileName(Path.GetDirectoryName(mp.Replace('\\', '/')) ?? "") ?? "";
        return candidates
            .OrderByDescending(c => token.Length > 0 && c.Contains(token, StringComparison.OrdinalIgnoreCase))
            .ThenBy(c => c.Length)
            .First();
    }

    private static ViewportMeshRenderer.SubmeshMaterial ToSubmeshMaterial(MaterialProfile p,
        System.Numerics.Vector4 terrainWorldMaskTransform = default) =>
        new(p.UsesRim, p.UsesSpecular, p.UvScale, p.UvOffset, p.UvRotationDegrees,
            AlphaMode: p.RenderMode switch
            {
                MaterialRenderMode.Cutout => 1,
                MaterialRenderMode.Transparent => 2,
                MaterialRenderMode.TransparentCutout => 3,
                _ => 0,
            },
            DoubleSided: p.DoubleSided,
            Tint: p.Tint,
            TintTextured: p.TintTextured,
            AlphaCutoff: p.AlphaCutoff ?? 0.35f,
            ClampU: p.ClampU,
            ClampV: p.ClampV,
            IsFlowmap: p.IsFlowmap,
            FlowSpeed: p.FlowSpeed,
            FlowStrength: p.FlowStrength,
            FlowTile: p.FlowTile,
            ColorInside: p.ColorInside,
            ColorOutside: p.ColorOutside,
            WaterAlpha: p.WaterAlpha,
            IsTerrainBlend: p.IsTerrainBlend,
            TerrainBottomTiling: p.TerrainBottomTiling,
            TerrainMiddleTiling: p.TerrainMiddleTiling,
            TerrainTopTiling: p.TerrainTopTiling,
            TerrainExtrasTiling: p.TerrainExtrasTiling,
            TerrainWorldScale: p.TerrainWorldScale,
            TerrainMaskMultipliers: new System.Numerics.Vector3(
                p.TerrainRMaskMultiplier, p.TerrainGMaskMultiplier, p.TerrainBMaskMultiplier),
            TerrainWorldProjectedMask: p.TerrainWorldProjectedMask,
            TerrainWorldMaskTransform: terrainWorldMaskTransform,
            TerrainBlendPowers: p.TerrainBlendPowers,
            TerrainUseTop: p.TerrainUseTop,
            TerrainUseExtras: p.TerrainUseExtras,
            TerrainUseAlphaOverlay: p.TerrainUseAlphaOverlay,
            TerrainOverlayRange: p.TerrainOverlayRange,
            UsesGrassTint: p.UsesGrassTint,    // M78
            NoBakedLighting: p.NoBakedLighting,   // M150: shaderMacros NO_BAKED_LIGHTING
            DisableDepthFog: p.DisableDepthFog,   //           DISABLE_DEPTH_FOG
            SrcBlendFactor: p.SrcBlendFactor,
            DstBlendFactor: p.DstBlendFactor);

    private readonly HashSet<string> _loggedUvTransforms = new(StringComparer.Ordinal);

    /// <summary>Log the UV transform applied to a material once (spec: "log which UV transform was applied").</summary>
    private void LogUvTransform(MaterialProfile p, string label)
    {
        if (!p.HasUvTransform) return;
        var key = $"{label}|{p.UvScale}|{p.UvOffset}|{p.UvRotationDegrees}";
        if (!_loggedUvTransforms.Add(key)) return;
        _log.Info("Material", $"UV transform on '{label}': scale ({p.UvScale.X:0.###}, {p.UvScale.Y:0.###})" +
                              $" offset ({p.UvOffset.X:0.###}, {p.UvOffset.Y:0.###})" +
                              (p.UvRotationDegrees != 0 ? $" rot {p.UvRotationDegrees:0.#}°" : "") +
                              (p.UvScaleSource is not null ? $"  [from {p.UvScaleSource}]" : "") +
                              (p.UvOffsetSource is not null ? $"  [offset from {p.UvOffsetSource}]" : ""));
    }

    /// <summary>Per-submesh diffuse textures from the resolved champion material (override-aware loads).</summary>
    private IReadOnlyList<TextureImage?> BuildSubmeshTextures(MeshAsset mesh, ChampionMaterialResolver.Result material, string label)
    {
        var cache = new Dictionary<string, TextureImage?>(StringComparer.OrdinalIgnoreCase);
        TextureImage? Load(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (cache.TryGetValue(path, out var hit)) return hit;
            return cache[path] = LoadTextureByPath(path);
        }

        int n = mesh.SubMeshes.Count;
        var result = new TextureImage?[n];
        var masks = new TextureImage?[n];
        var grads = new TextureImage?[n];
        var emis = new TextureImage?[n];
        var matcaps = new TextureImage?[n];
        var matcapMasks = new TextureImage?[n];
        var submeshMats = new ViewportMeshRenderer.SubmeshMaterial[n];
        int loaded = 0, secondary = 0;
        for (int i = 0; i < n; i++)
        {
            var sub = mesh.SubMeshes[i].Material;
            var img = Load(material.For(sub));
            result[i] = img;
            if (img is not null) loaded++;
            masks[i] = Load(material.ForMask(sub));
            grads[i] = Load(material.ForGradient(sub));
            emis[i] = Load(material.ForEmissive(sub));
            matcaps[i] = Load(material.ForMatCap(sub));
            matcapMasks[i] = Load(material.ForMatCapMask(sub));
            if (masks[i] is not null || grads[i] is not null || emis[i] is not null || matcaps[i] is not null) secondary++;
            submeshMats[i] = ToSubmeshMaterial(material.Profile(sub));
            LogUvTransform(material.Profile(sub), sub);
        }
        CurrentModelSubmeshMaterials = submeshMats;
        HasFlowmapWater = false; // M44: champion skins never carry flowmap water
        // Publish the secondary layers (mask/gradient/emissive/matcap) for the RiotApprox preview.
        CurrentModelMaskTextures = material.SubmeshMask.Count > 0 || material.DefaultMask is not null ? masks : null;
        CurrentModelGradientTextures = material.SubmeshGradient.Count > 0 || material.DefaultGradient is not null ? grads : null;
        CurrentModelEmissiveTextures = material.SubmeshEmissive.Count > 0 || material.DefaultEmissive is not null ? emis : null;
        CurrentModelMatCapTextures = material.SubmeshMatCap.Count > 0 || material.DefaultMatCap is not null ? matcaps : null;
        CurrentModelMatCapMaskTextures = material.SubmeshMatCapMask.Count > 0 || material.DefaultMatCapMask is not null ? matcapMasks : null;

        int distinct = cache.Values.Count(v => v is not null);
        var extra = material.HasSecondary ? $", {secondary} with secondary samplers (mask/gradient/emissive)" : "";
        _log.Success("Material", $"Applied {loaded}/{n} submesh textures ({distinct} distinct{extra}) for {label}.");
        return result;
    }

    /// <summary>Find the matching .skl for a resolved .skn inside the same WAD.</summary>
    private SkeletonAsset? TryPairSkeleton(WadAssetEntry skn)
    {
        if (!ContentLoaded || !skn.IsResolved || !skn.Path.EndsWith(".skn", StringComparison.OrdinalIgnoreCase))
            return null;
        var sklPath = skn.Path[..^4] + ".skl";
        var hash = HashAlgorithms.WadPath(sklPath);
        if (!TryResolveEntry(hash, out var sklEntry)) return null;
        try { return SkeletonDecoder.Decode(ReadAsset(sklEntry.PathHash)); }
        catch { return null; }
    }

    [RelayCommand]
    private async Task AssignSkeleton()
    {
        if (SelectedNode?.Entry is not { Type: AssetType.SkinnedMesh }) { _log.Warn("Skeleton", "Select a .skn first."); return; }
        var sklType = new FilePickerFileType("Skeleton") { Patterns = new[] { "*.skl" } };
        var path = await Dialogs.OpenFileAsync("Assign skeleton (.skl)", sklType, DialogService.All);
        if (path is null) return;
        try
        {
            var skeleton = await Task.Run(() => SkeletonDecoder.Decode(File.ReadAllBytes(path)));
            CurrentSkeleton = skeleton;
            ShowBones = true;
            MeshInspector.SetSkeleton(skeleton);
            _log.Success("Skeleton", $"Assigned {Path.GetFileName(path)} ({skeleton.BoneCount} bones).");
        }
        catch (Exception ex) { _log.Error("Skeleton", ex.Message); }
    }

    // ---- Project ---------------------------------------------------------

    /// <summary>M73: the hash resolver, exposed so the New Project wizard can classify + extract WAD content.</summary>
    public WadPathResolver PathResolver => _resolver;

    /// <summary>M73: raised to open the template-based New Project wizard (handled by the window).</summary>
    public event Action? RequestNewProject;

    [RelayCommand]
    private void NewProject() => RequestNewProject?.Invoke();

    [RelayCommand]
    private async Task OpenProject()
    {
        var path = await Dialogs.OpenFileAsync("Open ReyEngine project", DialogService.Project, DialogService.All);
        if (path is null) return;
        try
        {
            var proj = ReyProjectService.Open(path);
            Project = proj;
            _overrides.LoadFrom(proj);
            if (proj.SourceWadPath is not null && File.Exists(proj.SourceWadPath)) LoadWad(proj.SourceWadPath);
            else _log.Warn("Project", "Source WAD not found — open it manually.");
            LoadRecentProjects(RecentProjects.Add(Path.GetDirectoryName(path) ?? path));
            _log.Success("Project", $"Opened '{proj.Name}' with {_overrides.Count} override(s).");
            UpdateTitle();
        }
        catch (Exception ex) { _log.Error("Project", ex.Message); }
    }

    [RelayCommand]
    private async Task SaveProject()
    {
        if (Project.ProjectFilePath is null) { await SaveProjectAs(); return; }
        _overrides.SaveTo(Project);
        ReyProjectService.Save(Project, Project.ProjectFilePath);
        UndoService.MarkSaved();
        _log.Success("Project", $"Saved {Project.ProjectFilePath}");
        UpdateTitle();
    }

    [RelayCommand]
    private async Task SaveProjectAs()
    {
        var suggested = (string.IsNullOrEmpty(Project.Name) ? "project" : Project.Name) + ReyProjectService.Extension;
        var path = await Dialogs.SaveFileAsync("Save project as", suggested);
        if (path is null) return;
        if (!path.EndsWith(ReyProjectService.Extension, StringComparison.OrdinalIgnoreCase)) path += ReyProjectService.Extension;
        _overrides.SaveTo(Project);
        ReyProjectService.Save(Project, path);
        _log.Success("Project", $"Saved {path}");
        UpdateTitle();
    }

    private async Task<bool> EnsureProjectSavedAsync()
    {
        // A folder project (opened via Open Project Folder / the M73 wizard) is ALREADY a real saved project
        // with a workspace on disk — overrides land under its .reyengine folder. No WAD/quick-project needed.
        if (Project.IsFolderProject && Project.ProjectFilePath is not null) return true;

        if (Project.SourceWadPath is null)
        {
            if (_archive is null) { _log.Warn("Project", "Open a WAD and create a project first."); return false; }
            // Legacy quick-project: inspecting a bare WAD and making the first edit — wrap the open WAD in a
            // project inline (the M73 wizard is for deliberate new projects, not this save-on-first-edit path).
            var proj = ReyProjectService.NewFromWad(_archive.FilePath);
            proj.GameDirectory = Project.GameDirectory;
            Project = proj;
            _overrides.Clear();
            RebuildTree();
            _log.Info("Project", $"Created quick project '{proj.Name}' from {Path.GetFileName(_archive.FilePath)} to hold your edits.");
            UpdateTitle();
        }
        if (Project.ProjectFilePath is null) await SaveProjectAs();
        return Project.ProjectFilePath is not null;
    }

    // ---- Import / replace / revert --------------------------------------

    [RelayCommand(CanExecute = nameof(CanReplaceSelected))]
    private async Task ReplaceSelected()
    {
        var entry = ContextNode?.Entry;
        if (entry is null) { _log.Warn("Project", "Select an asset to replace."); return; }
        if (!GuardEditable(entry)) return;
        if (!await EnsureProjectSavedAsync()) return;

        var file = await Dialogs.OpenFileAsync($"Replace {entry.DisplayName}", DialogService.All);
        if (file is null) return;
        try
        {
            var stored = ProjectWorkspace.StoreOverride(Project, entry.PathHash, file);
            _overrides.Set(new ProjectAssetOverride
            {
                PathHash = entry.PathHash,
                ResolvedPath = entry.IsResolved ? entry.Path : null,
                OverrideFile = stored,
                AddedUtc = DateTime.UtcNow.ToString("o"),
            });
            SetNodeStatus(entry.PathHash, AssetStatus.Modified);
            Project.IsDirty = true;
            UpdateTitle();
            OnSelectedNodeChanged(SelectedNode); // refresh preview/status from override
            _log.Success("Project", $"Replaced {entry.DisplayName} with {Path.GetFileName(file)}.");
        }
        catch (Exception ex) { _log.Error("Project", ex.Message); }
    }

    [RelayCommand(CanExecute = nameof(CanRevertSelected))]
    private void RevertSelected()
    {
        var entry = ContextNode?.Entry;
        if (entry is null || !_overrides.Has(entry.PathHash)) { _log.Warn("Project", "Selected asset is not modified."); return; }
        _overrides.Remove(entry.PathHash);
        SetNodeStatus(entry.PathHash, AssetStatus.Original);
        Project.IsDirty = true;
        UpdateTitle();
        OnSelectedNodeChanged(SelectedNode);
        _log.Success("Project", $"Reverted {entry.DisplayName} to original.");
    }

    [RelayCommand]
    private void ImportNewAsset() =>
        _log.Warn("Project", "Adding brand-new chunks isn't supported: WAD v3.4 stores a separate subchunk table that can't be safely relocated without risking corruption. Use Replace on an existing asset, or repoint a material to an existing texture path.");

    [RelayCommand]
    private async Task ExportModified()
    {
        var entry = ContextNode?.Entry;
        if (entry is null || !_overrides.TryGet(entry.PathHash, out var ov)) { _log.Warn("Export", "Selected asset has no override."); return; }
        var outPath = await Dialogs.SaveFileAsync("Export modified asset", Path.GetFileName(ov.OverrideFile));
        if (outPath is null) return;
        try { File.Copy(ov.OverrideFile, outPath, true); _log.Success("Export", $"Wrote {outPath}"); }
        catch (Exception ex) { _log.Error("Export", ex.Message); }
    }

    [RelayCommand(CanExecute = nameof(CanCopyEntryText))]
    private async Task CopyResolvedPath()
    {
        var entry = ContextNode?.Entry;
        if (entry is null) return;
        await Dialogs.CopyAsync(entry.Path);
        _log.Info("Clipboard", entry.Path);
    }

    [RelayCommand(CanExecute = nameof(CanCopyEntryText))]
    private async Task CopyHash()
    {
        var entry = ContextNode?.Entry;
        if (entry is null) return;
        var h = $"0x{entry.PathHash:x16}";
        await Dialogs.CopyAsync(h);
        _log.Info("Clipboard", h);
    }

    // ---- Build -----------------------------------------------------------

    // ---- Project folder mode (M11) --------------------------------------

    [RelayCommand]
    private async Task OpenProjectFolder()
    {
        var folder = await Dialogs.OpenFolderAsync("Open project folder");
        if (folder is not null) OpenProjectAt(folder);
    }

    // ---- M98: Map Bin Editor window ----
    public MapBinEditorViewModel MapBinEditor { get; } = new();
    public Action? ShowMapBinEditorWindow;

    /// <summary>M140: open a HUD layout bin (ClientStates/…/UIBase) in the visual HUD Editor.</summary>
    [RelayCommand]
    private void OpenInHudEditor(AssetNodeViewModel? node)
    {
        var entry = node?.Entry ?? ContextNode?.Entry;
        if (entry is null) { _log.Warn("Hud", "Select a HUD layout .bin (ClientStates/…/UIBase) first."); return; }
        try
        {
            var bytes = GetAssetBytes(entry);
            var doc = Formats.Hud.HudDocument.Parse(bytes, ResolveBinName);
            if (doc is null || doc.AllElements.Count == 0)
            { _log.Warn("Hud", $"{entry.DisplayName} isn't a HUD layout bin (no UiElement objects)."); return; }

            var vm = new HudEditorViewModel
            {
                ResolveAtlas = LoadThumbnailByPath,
                Info = m => _log.Info("Hud", m),
            };
            vm.Load(entry, doc);
            var win = new Views.HudEditorWindow { DataContext = vm };
            if (PromptOwner is not null) win.Show(PromptOwner); else win.Show();
            _log.Info("Hud", $"HUD Editor: {entry.DisplayName} — {doc.AllElements.Count} element(s), {doc.AtlasPaths.Count} atlas(es), reference {doc.ReferenceWidth}×{doc.ReferenceHeight}.");
        }
        catch (Exception ex) { _log.Error("Hud", $"{entry.DisplayName}: {ex.Message}"); }
    }

    /// <summary>M137: open a Wwise .bnk/.wpk in the Audio Bank Editor — play, replace, add, rename,
    /// delete and copy/paste its embedded sounds.</summary>
    [RelayCommand]
    private void OpenInAudioEditor(AssetNodeViewModel? node)
    {
        var entry = node?.Entry ?? ContextNode?.Entry;
        if (entry is null) { _log.Warn("Audio", "Select a .bnk or .wpk asset first."); return; }
        if (!entry.DisplayName.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase)
            && !entry.DisplayName.EndsWith(".wpk", StringComparison.OrdinalIgnoreCase))
        { _log.Warn("Audio", $"{entry.DisplayName} is not a Wwise bank or wem pack."); return; }

        try
        {
            var bytes = GetAssetBytes(entry);
            var vm = new AudioBankEditorViewModel
            {
                DecodeToWav = (id, data) => Sound.DecodeToWav(id, data),
                PlayWav = wav => Sound.PlayWav(wav, 1f, loop: false, tag: "bankedit"),
                StopAll = () => Sound.StopAll(),
                ClearDecodeCache = id => Sound.ClearCache(id),
                Info = m => _log.Info("Audio", m),
                Warn = m => _log.Warn("Audio", m),
                PickImportFile = title => Dialogs.OpenFileAsync(title,
                    new Avalonia.Platform.Storage.FilePickerFileType("Audio (wem / wav / mp3 / ogg)")
                    { Patterns = new[] { "*.wem", "*.wav", "*.mp3", "*.ogg", "*.flac", "*.m4a", "*.wma" } },
                    DialogService.All),
                ConvertToWem = path => { var d = Encoder.Convert(path, out var err); return (d, err); },
                ConverterAvailable = () => Encoder.IsAvailable,
                PickExportFile = suggested => Dialogs.SaveFileAsync("Export sound", suggested),
                PromptText = (title, initial) => PromptOwner is null
                    ? Task.FromResult<string?>(null)
                    : Views.PromptWindow.InputAsync(PromptOwner, title,
                        "Wwise identifies sounds by number — this id is what events reference.", initial),
                SaveAsync = async (doc, e, data) =>
                {
                    if (!GuardEditable(e)) return false;
                    if (!await EnsureProjectSavedAsync()) return false;
                    bool ok = await SaveMapBinBytesAsync(e, data);   // in-place for folder projects; override otherwise
                    if (ok) _log.Success("Audio", $"Saved {e.DisplayName} ({data.Length:n0} bytes, {doc.Entries.Count} sound(s)).");
                    return ok;
                },
            };

            if (!vm.Load(entry, bytes, null))
            { _log.Warn("Audio", $"{entry.DisplayName} isn't a readable Wwise bank/pack."); return; }
            if (vm.Document is { IsEditable: false, ReadOnlyReason: { } reason })
                _log.Warn("Audio", $"{entry.DisplayName}: {reason}");

            // "played by": reverse-resolve the sibling *_events.bnk so each sound shows what triggers it
            if (BuildUsedByLookup(entry) is { } lookup) vm.SetUsedByLookup(lookup);

            var win = new Views.AudioBankEditorWindow { DataContext = vm };
            if (PromptOwner is not null) win.Show(PromptOwner); else win.Show();
            _log.Info("Audio", $"Audio Bank Editor: {entry.DisplayName} — {vm.Document!.Entries.Count} sound(s)."
                + (Sound.IsAvailable ? "" : " vgmstream-cli NOT found — playback disabled."));
        }
        catch (Exception ex) { _log.Error("Audio", $"{entry.DisplayName}: {ex.Message}"); }
    }

    /// <summary>Map wem id → the event(s) that play it, read from the bank's sibling <c>*_events.bnk</c>
    /// (media and hierarchy ship as separate files). Event names aren't stored in the banks, so known
    /// names come from the loaded map's sound placements; anything else shows as its hex id.</summary>
    private Func<uint, string[]>? BuildUsedByLookup(WadAssetEntry mediaEntry)
    {
        try
        {
            var path = mediaEntry.Path;
            var eventsPath = path.Contains("_audio.", StringComparison.OrdinalIgnoreCase)
                ? System.Text.RegularExpressions.Regex.Replace(path, "_audio\\.(bnk|wpk)$", "_events.bnk",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                : null;
            if (eventsPath is null || !TryResolveEntry(HashAlgorithms.WadPath(eventsPath), out var evEntry)) return null;
            if (Formats.Audio.BnkFile.Parse(GetAssetBytes(evEntry)) is not { HasHirc: true } evBank) return null;

            var set = new Formats.Audio.AudioBankSet();
            set.AddBank(evBank, evEntry.PathHash, evEntry.Path);

            // known event names (hash -> name): the recovered index plus the map's placed sounds
            var names = new Dictionary<uint, string>();
            foreach (var s in MapContent.Sounds)
                names[Formats.Audio.WwiseHash.Fnv1(s.EventName)] = s.EventName;

            var reverse = new Dictionary<uint, List<string>>();
            foreach (var eventId in evBank.Events.Keys)
            {
                string label = names.TryGetValue(eventId, out var n) ? n : WwiseNames.Label(eventId);
                foreach (var wemId in set.ResolveEvent(eventId))
                {
                    if (!reverse.TryGetValue(wemId, out var list)) reverse[wemId] = list = new List<string>();
                    if (!list.Contains(label)) list.Add(label);
                }
            }
            _log.Info("Audio", $"Matched {System.IO.Path.GetFileName(eventsPath)}: {evBank.Events.Count} event(s) → {reverse.Count} sound(s).");
            return id => reverse.TryGetValue(id, out var l) ? l.ToArray() : Array.Empty<string>();
        }
        catch { return null; }
    }

    /// <summary>M98: right-click ▸ Open in Map Bin Editor — the fast structured editor for map*.bin.</summary>
    /// <summary>M197 (4.5): open any bin in the Particle Editor from the Content Browser. Until this, a map
    /// document only ever parsed VFX from the mapgeo's sibling materials.bin, so the systems in mapXX.bin and
    /// under maps/modespecificdata were unreachable in the app - thousands of them, with no placements to
    /// bring them into a scene.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenInParticleEditor))]
    private void OpenInParticleEditor(AssetNodeViewModel? node)
    {
        var entry = node?.Entry ?? ContextNode?.Entry;
        if (entry is null) { _log.Warn("Particle", "Select an asset first."); return; }
        OpenParticleEditorFor(entry);
    }

    [RelayCommand(CanExecute = nameof(CanOpenInMapBinEditor))]
    private void OpenInMapBinEditor(AssetNodeViewModel? node)
    {
        if (node?.Entry is not { } entry) { _log.Warn("MapBin", "Select a .bin asset first."); return; }
        if (!entry.DisplayName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        { _log.Warn("MapBin", $"{entry.DisplayName} is not a .bin file."); return; }
        try
        {
            var bytes = GetAssetBytes(entry);
            MapBinEditor.Load(entry, bytes);
            ShowMapBinEditorWindow?.Invoke();
        }
        catch (Exception ex) { _log.Error("MapBin", $"{entry.DisplayName}: {ex.Message}"); }
    }

    /// <summary>M98: the UNTOUCHED Riot bytes for an entry — read from the project's reference WADs
    /// directly (never through the mounts, which would return the project's own override).</summary>
    private byte[]? ReadRiotOriginalBytes(WadAssetEntry entry)
    {
        foreach (var wadPath in Project.ReferenceWads)
        {
            try
            {
                if (!File.Exists(wadPath)) continue;
                using var w = ReyEngine.Core.Wad.WadArchive.Open(wadPath, _resolver.Database);
                if (w.TryGetEntry(entry.PathHash, out var e)) return w.Extract(e);
            }
            catch { /* try the next reference */ }
        }
        // single-WAD mode: the open archive IS the Riot file
        try { if (_archive is not null && _archive.TryGetEntry(entry.PathHash, out var ae)) return _archive.Extract(ae); }
        catch { }
        return null;
    }

    /// <summary>M98: save Map Bin Editor output through the same guarded override path as the raw editor
    /// (re-parse check, override store, status + dirty bookkeeping).</summary>
    private async Task<bool> SaveMapBinBytesAsync(WadAssetEntry entry, byte[] bytes)
    {
        try { _ = Formats.Meta.SafeBinTree.Parse(bytes); }
        catch (Exception ex) { _log.Error("MapBin", $"Edited .bin failed to re-parse — NOT saved: {ex.Message}"); return false; }
        if (!await EnsureProjectSavedAsync()) return false;
        try
        {
            // M98c: folder-project files are edited in place — no shadow override
            if (TryWriteToProjectFile(entry, bytes, out var projectFile))
            {
                SetNodeStatus(entry.PathHash, AssetStatus.Modified);
                Project.IsDirty = true;
                UpdateTitle();
                _log.Success("MapBin", $"Saved {entry.DisplayName} to {projectFile} ({bytes.Length:n0} bytes, re-parse OK).");
                return true;
            }
            var dest = ProjectWorkspace.StoreOverrideBytes(Project, entry.PathHash, bytes, ".bin");
            _overrides.Set(new ProjectAssetOverride
            {
                PathHash = entry.PathHash,
                ResolvedPath = entry.IsResolved ? entry.Path : null,
                OverrideFile = dest,
                AddedUtc = DateTime.UtcNow.ToString("o"),
            });
            SetNodeStatus(entry.PathHash, AssetStatus.Modified);
            Project.IsDirty = true;
            UpdateTitle();
            _log.Success("MapBin", $"Saved {entry.DisplayName} to project override ({bytes.Length:n0} bytes, re-parse OK).");
            return true;
        }
        catch (Exception ex) { _log.Error("MapBin", ex.Message); return false; }
    }

    // ---- M125: Bin Issues window — repairs the tolerant reader applied, navigable + fixable ----

    /// <summary>Open the Bin Issues window for the materials document (map/champion .bin).</summary>
    private void OpenMaterialBinIssues()
    {
        if (MaterialEditor.BinEntry is not { } entry || MaterialEditor.Issues.Count == 0) return;
        var vm = new BinIssuesWindowViewModel
        {
            BinName = entry.DisplayName,
            RepairAsync = entry.ReadOnly ? null : async () =>
            {
                // The tolerantly-parsed tree IS the healed form — re-saving it writes a clean file.
                var bytes = MaterialEditor.Serialize();
                if (bytes is null || !await SaveMapBinBytesAsync(entry, bytes)) return false;
                await LoadMaterialBinAsync(entry, alsoRawBin: false);   // reload: the red marks clear
                return true;
            },
        };
        var group = new BinIssueGroupViewModel { BinName = entry.DisplayName };
        vm.Groups.Add(group);
        foreach (var i in MaterialEditor.Issues)
        {
            var mat = MaterialEditor.Materials.FirstOrDefault(m => m.Model.ObjectPathHash == i.ObjectPathHash);
            group.Rows.Add(new BinIssueRowViewModel
            {
                Kind = i.Kind,
                ObjectName = mat?.Name ?? ResolveBinName(i.ObjectPathHash) ?? $"0x{i.ObjectPathHash:x8}",
                ClassName = ResolveBinName(i.ObjectClassHash) ?? $"class 0x{i.ObjectClassHash:x8}",
                FieldName = i.FieldHash is { } fh ? ResolveBinName(fh) ?? $"0x{fh:x8}" : null,
                Message = i.Message,
                Suggestion = i.Suggestion,
                GoTo = mat is null ? null : () =>
                {
                    InspectorTab = 1;
                    AssetDataExpanded = true;
                    MaterialEditor.SetMeshFilter(null);      // the filter must not hide the target
                    MaterialEditor.OnlyUnresolved = false;
                    MaterialEditor.Search = mat.Name;        // narrows the list to the affected material
                },
            });
        }
        ShowBinIssuesWindow(vm);
    }

    /// <summary>Open the Bin Issues window for the particle document.</summary>
    private void OpenParticleBinIssues()
    {
        if (ParticleEditor.Entry is not { } entry || ParticleEditor.Document is not { } doc || doc.Issues.Count == 0) return;
        var vm = new BinIssuesWindowViewModel
        {
            BinName = entry.DisplayName,
            RepairAsync = entry.ReadOnly ? null : async () =>
            {
                var bytes = doc.Serialize();
                if (!await SaveMapBinBytesAsync(entry, bytes)) return false;
                ParticleEditor.Load(entry, bytes, editable: true);   // reload from the healed bytes
                return true;
            },
        };
        var group = new BinIssueGroupViewModel { BinName = entry.DisplayName };
        vm.Groups.Add(group);
        foreach (var i in doc.Issues)
        {
            var node = ParticleEditor.Systems.FirstOrDefault(s => s.Entry.PathHash == i.ObjectPathHash);
            group.Rows.Add(new BinIssueRowViewModel
            {
                Kind = i.Kind,
                ObjectName = node?.Name ?? ResolveBinName(i.ObjectPathHash) ?? $"0x{i.ObjectPathHash:x8}",
                ClassName = ResolveBinName(i.ObjectClassHash) ?? $"class 0x{i.ObjectClassHash:x8}",
                FieldName = i.FieldHash is { } fh ? ResolveBinName(fh) ?? $"0x{fh:x8}" : null,
                Message = i.Message,
                Suggestion = i.Suggestion,
                GoTo = node is null ? null : () =>
                {
                    ParticleEditor.SelectedSystem = node;
                    ShowParticleEditorWindow?.Invoke();
                },
            });
        }
        ShowBinIssuesWindow(vm);
    }

    private void ShowBinIssuesWindow(BinIssuesWindowViewModel vm)
    {
        var win = new Views.BinIssuesWindow { DataContext = vm };
        if (PromptOwner is not null) win.Show(PromptOwner);
        else win.Show();
    }

    /// <summary>M97: emulated-injection check — validate every project .bin against the merged view
    /// (project overrides + Riot originals, exactly what the game would mount) and report broken object
    /// links and missing asset references. The classic "mod crashes after patch" causes, found offline.</summary>
    [RelayCommand]
    private async Task ValidateProjectBins()
    {
        if (!ContentLoaded) { _log.Warn("Validate", "Open a project (or WAD) first."); return; }
        if (Project.RootPath is null || Project.ProjectFolders.Count == 0)
        { _log.Warn("Validate", "No project folders to validate — open a folder project."); return; }

        _log.Info("Validate", "Checking project .bins against the injected view (project overrides + Riot originals)…");
        var results = await Task.Run(() =>
        {
            // M127: per missing asset, hunt for an existing replacement — the base variant of a dead
            // skin-suffixed path (Riot vaults map skins; X.HA_CREPE.scb dies, X.scb stays), else any
            // mounted file with the same filename. Powers the one-click Fix in the issues window.
            string? FindAlternative(string missing)
            {
                if (Formats.Meta.BinAssetRepointer.BaseVariant(missing) is { } baseVar
                    && TryResolveEntry(HashAlgorithms.WadPath(baseVar), out _))
                    return baseVar;
                string fileName = Path.GetFileName(missing);
                foreach (var e in AssetEntries)
                    if (e.IsResolved && Path.GetFileName(e.Path).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                        return e.Path;
                return null;
            }

            var list = new List<(string Rel, Formats.Meta.BinValidationReport Report, Dictionary<string, string> Alts)>();
            foreach (var folder in Project.ProjectFolders)
            {
                string root = Path.Combine(Project.RootPath!, folder);
                if (!Directory.Exists(root)) continue;
                foreach (var file in Directory.EnumerateFiles(root, "*.bin", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                    if (!rel.Contains('/')) continue;   // loose unresolved-chunk dumps, not real bins
                    byte[] bytes;
                    try { bytes = File.ReadAllBytes(file); } catch { continue; }

                    // M126: validate what the game actually MOUNTS. A shadow override outranks the
                    // project file, so validating the raw file reported issues the user had already
                    // fixed (the fix lived in the override). Saves now dissolve shadows, but existing
                    // projects may still carry one — even record-less (the override mount scans its
                    // directory, project.json entries are optional) — prefer its bytes and say so.
                    string display = rel;
                    ulong relHash = HashAlgorithms.WadPath(rel);
                    string? shadowFile = null;
                    if (_overrides.TryGet(relHash, out var shadow) && File.Exists(shadow.OverrideFile))
                        shadowFile = shadow.OverrideFile;
                    else
                    {
                        try
                        {
                            var orphan = Path.Combine(ProjectWorkspace.OverridesDir(Project), $"{relHash:x16}.bin");
                            if (File.Exists(orphan)) shadowFile = orphan;
                        }
                        catch { }
                    }
                    if (shadowFile is not null && !string.Equals(shadowFile, file, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            bytes = File.ReadAllBytes(shadowFile);
                            display = $"{rel}  [an override shadows this file — save it once in ReyEngine to merge]";
                        }
                        catch { /* unreadable shadow: validate the project file */ }
                    }

                    // resolve this bin's dependency bins through the SAME merged view the game would see
                    var deps = new List<byte[]>();
                    foreach (var dep in Formats.Vfx.VfxSystemResolver.ExtractDependencies(bytes))
                        if (TryResolveEntry(HashAlgorithms.WadPath(dep), out var de))
                        { try { deps.Add(GetAssetBytes(de)); } catch { /* counted as missing-dependency */ } }

                    var report = Formats.Meta.BinValidator.Validate(display, bytes, deps,
                        p => TryResolveEntry(HashAlgorithms.WadPath(p), out _),
                        ResolveBinName,
                        h => ResolveBinName(h)?.StartsWith("Shaders/", StringComparison.OrdinalIgnoreCase) == true);

                    var alts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var i in report.Issues)
                        if (i.Category == "missing-asset" && !alts.ContainsKey(i.Detail)
                            && FindAlternative(i.Detail) is { } alt)
                            alts[i.Detail] = alt;
                    list.Add((rel, report, alts));
                }
            }

            // ---- M129: usage analysis — does the current game even LOAD each of these bins? ----
            // Old mods drag along skin*.bin files for characters the map no longer spawns, and
            // linked "skins_skin0_skin1_…" bins whose filename Riot has since changed (more skins
            // merged in). Those bins fail validation loudly but the game never requests them.
            var usage = new Dictionary<string, List<(string Kind, string Message, string Suggestion)>>(StringComparer.OrdinalIgnoreCase);

            // exact strings the CURRENT maps' shipping bins carry — spawn tables reference
            // characters by exact name string (verified on live map11.bin: 1,645 objects, all
            // character names present as plain strings)
            var mapNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (rel, _, _) in list)
            {
                var segs = rel.Split('/');
                for (int i = 0; i + 1 < segs.Length; i++)
                    if ((segs[i].Equals("mapgeometry", StringComparison.OrdinalIgnoreCase)
                         || segs[i].Equals("shipping", StringComparison.OrdinalIgnoreCase))
                        && segs[i + 1].StartsWith("map", StringComparison.OrdinalIgnoreCase))
                        mapNames.Add(segs[i + 1]);
            }
            var mapExact = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var mapPathStrings = new List<string>();
            foreach (var map in mapNames)
            {
                string prefix = $"data/maps/shipping/{map}/";
                foreach (var e in AssetEntries)
                {
                    if (!e.IsResolved
                        || !e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        || !e.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var strings = new List<string>();
                        Formats.Meta.BinStringHarvester.Collect(
                            Formats.Meta.SafeBinTree.Parse(ReadAsset(e.PathHash)), strings);
                        foreach (var s in strings)
                        {
                            mapExact.Add(s);
                            if (s.Contains('/')) mapPathStrings.Add(s.ToLowerInvariant());
                        }
                    }
                    catch { /* a broken shipping bin only weakens the analysis */ }
                }
            }

            bool RiotHasPath(ulong h) =>
                _mounts is not null
                && (_mounts.Mounts.Any(m => m.Kind == AssetSourceKind.RiotReference && m.Contains(h))
                    || _mounts.Fallback.Any(f => f.Contains(h)));

            foreach (var (rel, _, _) in list)
            {
                var findings = new List<(string, string, string)>();

                bool riotHas = RiotHasPath(HashAlgorithms.WadPath(rel));
                bool referenced = list.Any(o => !o.Rel.Equals(rel, StringComparison.OrdinalIgnoreCase)
                                                && o.Report.ReferencedPaths.Contains(rel));
                if (!riotHas && !referenced)
                    findings.Add(("unused-bin",
                        "The current game has no file at this path and nothing else in the project references it — the game never requests this bin.",
                        "Safe to delete (Delete .bin above). Typical for renamed linked bins: Riot merges more skins into 'skins_skin0_skin1_…' files and the filename changes each patch."));

                string? charName = null;
                var parts = rel.Split('/');
                for (int i = 0; i + 1 < parts.Length; i++)
                    if (parts[i].Equals("characters", StringComparison.OrdinalIgnoreCase)) { charName = parts[i + 1]; break; }
                if (charName is not null && mapExact.Count > 0
                    && !mapExact.Contains(charName)
                    && !mapPathStrings.Any(pth => pth.Contains($"characters/{charName.ToLowerInvariant()}/")))
                    findings.Add(("possibly-unused",
                        $"Character '{charName}' appears nowhere in the current map data ({string.Join(", ", mapNames)}) — the map no longer spawns it.",
                        "Probably a leftover from an older patch. If no other game mode needs it, Delete .bin above removes it from the mod."));

                if (findings.Count > 0) usage[rel] = findings;
            }

            return (list, usage);
        });
        var (reports, usage) = results;

        int bad = 0, issueCount = 0;
        foreach (var (_, r, _) in reports)
        {
            if (r.IsClean) continue;
            bad++; issueCount += r.Issues.Count;
            _log.Warn("Validate", $"{r.BinName}: {r.Issues.Count} issue(s)");
            foreach (var i in r.Issues.Take(8))
                _log.Warn("Validate", $"   [{i.Category}] {i.ObjectName} → {i.Detail}");
            if (r.Issues.Count > 8) _log.Warn("Validate", $"   … {r.Issues.Count - 8} more");
        }
        foreach (var (rel, findings) in usage)
            foreach (var f in findings)
                _log.Warn("Validate", $"{rel}: [{f.Kind}] {f.Message}");

        if (reports.Count == 0) { _log.Warn("Validate", "No .bin files found in the project folders."); return; }
        if (bad == 0 && usage.Count == 0)
        { _log.Success("Validate", $"All {reports.Count} project .bin(s) clean — every link and asset reference resolves in the injected view, and everything is still used by the current game."); return; }
        if (bad > 0)
            _log.Error("Validate", $"{bad}/{reports.Count} bin(s) have {issueCount} issue(s) — these would break in-game (details above).");
        if (usage.Count > 0)
            _log.Warn("Validate", $"{usage.Count} bin(s) look unused by the current game — see the issues window (they can be deleted there).");

        // M127: the issues are also a window now — navigable (Go To) and, where a replacement
        // exists, fixable in one click (repoint + save). No more hunting refs by hand.
        var vm = new BinIssuesWindowViewModel
        {
            BinName = $"Validate Project Bins — {bad} bin(s) with issues, {usage.Count} unused",
            Description = "Broken references the game would fail to load, checked against the injected view "
                + "(project overrides + Riot originals). Go To jumps to the object holding the reference; "
                + "where an existing replacement was found, Fix repoints every reference and saves the bin. "
                + "Bins marked unused are never requested by the current game — Delete .bin removes them. "
                + "Re-run Validate afterwards to confirm.",
        };
        foreach (var (rel, report, alts) in reports)
        {
            bool hasUsage = usage.TryGetValue(rel, out var findings);
            if (report.IsClean && !hasUsage) continue;
            bool haveEntry = TryResolveEntry(HashAlgorithms.WadPath(rel), out var binEntry);
            // M128: one group per bin, deletable — old mods often carry bins that are no longer
            // needed at all; dropping the file beats fixing its references one by one.
            var group = new BinIssueGroupViewModel
            {
                BinName = report.BinName,
                DeleteAsync = haveEntry ? async () =>
                {
                    if (PromptOwner is not null && !await Views.PromptWindow.ConfirmAsync(PromptOwner, "Delete Bin",
                        $"Delete {rel} from the project?\n\nThe mod stops overriding this file — the game will use Riot's original instead.", "Delete"))
                        return false;
                    return DeleteProjectBin(binEntry);
                } : null,
            };
            vm.Groups.Add(group);
            // M129: usage verdicts first — "this whole file is never loaded" outranks its detail issues
            if (hasUsage)
                foreach (var f in findings!)
                    group.Rows.Add(new BinIssueRowViewModel
                    {
                        Kind = f.Kind,
                        ObjectName = Path.GetFileName(rel),
                        ClassName = "file",
                        Message = f.Message,
                        Suggestion = f.Suggestion,
                    });
            foreach (var i in report.Issues)
            {
                string? alt = i.Category == "missing-asset" && alts.TryGetValue(i.Detail, out var a) ? a : null;
                group.Rows.Add(new BinIssueRowViewModel
                {
                    Kind = i.Category,
                    ObjectName = i.ObjectName,
                    ClassName = i.ObjectClassHash != 0
                        ? ResolveBinName(i.ObjectClassHash) ?? $"class 0x{i.ObjectClassHash:x8}"
                        : "file",
                    Message = i.Category switch
                    {
                        "missing-asset" => $"References {i.Detail} — it doesn't exist in the project or the game files; the game would fail to load it.",
                        "missing-dependency" => $"Dependency bin {i.Detail} doesn't exist in the injected view.",
                        _ => i.Detail,
                    },
                    Suggestion = i.Category switch
                    {
                        "missing-asset" when alt is not null =>
                            $"An existing file matches: {alt}",
                        "missing-asset" =>
                            "No replacement found automatically — bring the file into the project at exactly this path, or repoint the reference in the editor.",
                        "missing-link" =>
                            "The linked object exists in none of this bin's dependency bins — usually a stale link from an older patch.",
                        "missing-dependency" =>
                            "The game hard-requires listed dependencies. Bring the bin into the project, or remove the dependency entry.",
                        _ => "",
                    },
                    GoTo = haveEntry && i.ObjectPathHash != 0
                        ? () => _ = NavigateToBinObjectAsync(binEntry, i.ObjectPathHash, i.ObjectClassHash)
                        : null,
                    FixLabel = alt is not null ? $"🔧 Repoint to {alt}" : null,
                    FixAsync = haveEntry && alt is not null
                        ? () => RepointAssetRefAsync(binEntry, i.Detail, alt)
                        : null,
                });
            }
        }
        ShowBinIssuesWindow(vm);
    }

    /// <summary>M127: jump to a bin object in its natural editor — VFX systems open in the Particle
    /// Editor, everything else opens in the Map Bin Editor window with the object selected.
    /// M130: the Map Bin Editor replaced the old Materials-tab navigation — the issues window is owned
    /// by the main window, so the main window can never rise above it (the "Go To does nothing" bug);
    /// sibling windows can. It also handles EVERY object class, not just materials
    /// (SkinCharacterDataProperties in a skin bin has no material to search for).</summary>
    private Task NavigateToBinObjectAsync(WadAssetEntry entry, uint objHash, uint classHash)
    {
        if (classHash == HashAlgorithms.Fnv1a("VfxSystemDefinitionData"))
        {
            OpenParticleEditorFor(entry);
            if (ParticleEditor.Systems.FirstOrDefault(s => s.Entry.PathHash == objHash) is { } node)
                ParticleEditor.SelectedSystem = node;
            return Task.CompletedTask;
        }
        try
        {
            if (MapBinEditor.Entry?.PathHash != entry.PathHash)
                MapBinEditor.Load(entry, ReadAsset(entry.PathHash));
            ShowMapBinEditorWindow?.Invoke();
            if (!MapBinEditor.SelectObject(objHash))
                _log.Warn("Validate", $"Object 0x{objHash:x8} not found in {entry.DisplayName} — it may live in a dependency bin.");
        }
        catch (Exception ex) { _log.Error("Validate", $"{entry.DisplayName}: {ex.Message}"); }
        return Task.CompletedTask;
    }

    /// <summary>M127: replace every reference to a dead asset path with an existing one, then save the
    /// bin through the normal pipeline (in place for folder projects; shadows dissolve).</summary>
    private async Task<bool> RepointAssetRefAsync(WadAssetEntry entry, string fromPath, string toPath)
    {
        byte[] bytes;
        try { bytes = ReadAsset(entry.PathHash); }
        catch (Exception ex) { _log.Error("Validate", $"{entry.DisplayName}: {ex.Message}"); return false; }
        LeagueToolkit.Core.Meta.BinTree tree;
        try { tree = Formats.Meta.SafeBinTree.Parse(bytes); }
        catch (Exception ex) { _log.Error("Validate", $"{entry.DisplayName}: {ex.Message}"); return false; }

        int hits = Formats.Meta.BinAssetRepointer.Repoint(tree, fromPath, toPath);
        if (hits == 0) { _log.Warn("Validate", $"{entry.DisplayName}: no reference to {fromPath} found — already fixed?"); return false; }

        using var ms = new MemoryStream();
        tree.Write(ms);
        if (!await SaveMapBinBytesAsync(entry, ms.ToArray())) return false;
        _log.Success("Validate", $"{entry.DisplayName}: repointed {hits} reference(s) {fromPath} → {toPath}.");
        if (MaterialEditor.BinEntry?.PathHash == entry.PathHash)
            await LoadMaterialBinAsync(entry, alsoRawBin: false);   // refresh the open editor
        return true;
    }

    /// <summary>M128: remove a project bin entirely — the mod stops overriding it and the game falls
    /// back to Riot's original. Deletes the project file AND any shadow override, then rescans.</summary>
    private bool DeleteProjectBin(WadAssetEntry entry)
    {
        bool any = false;
        try
        {
            if (_mounts is not null && _mounts.TryGet(entry.PathHash, out var a))
                foreach (var src in new[] { a.Source }.Concat(a.AllSources).Distinct())
                    if (src is { Kind: AssetSourceKind.ProjectFolder or AssetSourceKind.ProjectOverride }
                        && src.TryGetFilePath(entry.PathHash, out var f) && File.Exists(f))
                    {
                        try { File.Delete(f); any = true; }
                        catch (Exception ex) { _log.Error("Validate", $"{f}: {ex.Message}"); }
                    }
            try
            {
                var orphan = Path.Combine(ProjectWorkspace.OverridesDir(Project), $"{entry.PathHash:x16}.bin");
                if (File.Exists(orphan)) { File.Delete(orphan); any = true; }
            }
            catch { }
            _overrides.Remove(entry.PathHash);
            if (!any) { _log.Warn("Validate", $"{entry.DisplayName}: no project file found to delete."); return false; }

            Project.IsDirty = true;
            if (MaterialEditor.BinEntry?.PathHash == entry.PathHash) { MaterialEditor.Clear(); HasMaterialData = false; }
            RefreshBrowser();
            _log.Success("Validate", $"Deleted {entry.DisplayName} from the project — the game will use the original file.");
            return true;
        }
        catch (Exception ex) { _log.Error("Validate", ex.Message); return false; }
    }

    /// <summary>M134: Overlay Footprint — how many game WADs will loaders have to patch for this mod?
    /// Shared-path assets (characters/items) exist in dozens of WADs; a texture-heavy map mod can force
    /// 200+ patches. That is an install-time and merge-complexity cost, NOT a crash cause — the crash it
    /// was blamed for is a non-cubemap texture behind a MapCubemapProbe (see CubemapProbeValidator).</summary>
    [RelayCommand]
    private async Task OverlayFootprint()
    {
        if (!ContentLoaded || Project.RootPath is null || Project.ProjectFolders.Count == 0)
        { _log.Warn("Footprint", "Open a folder project first."); return; }
        string gameFinal = Path.Combine(
            (Project.GameDirectory ?? "").Replace('/', Path.DirectorySeparatorChar), "DATA", "FINAL");
        if (!Directory.Exists(gameFinal))
        { _log.Warn("Footprint", "Game folder not set (Project ▸ Set Game Folder) — the analysis scans the game's WADs."); return; }

        IsBuilding = true;
        var progress = BuildProgressSink();
        try
        {
            var fp = await Task.Run(() =>
            {
                var files = new List<(ulong Hash, string RelPath, long Bytes)>();
                foreach (var f in Project.ProjectFolders)
                {
                    var root = Project.ResolveProjectPath(f);
                    if (!Directory.Exists(root)) continue;
                    foreach (var (hash, path) in Core.Build.WadPackService.EnumerateChunkFiles(root))
                    {
                        var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
                        files.Add((hash, rel, new FileInfo(path).Length));
                    }
                }
                return Core.Build.OverlayFootprintService.Analyze(files, gameFinal, progress);
            });
            _log.Info("Footprint", $"{fp.ProjectFiles:n0} file(s) → loaders patch {fp.TouchedWads} of {fp.GameWadsScanned} game WADs.");
            foreach (var s in fp.TopSources.Take(5))
                _log.Info("Footprint", $"   {s.Folder}: touches {s.WadsTouched} WAD(s) ({s.Files:n0} files, {s.Bytes / 1048576.0:0.0} MB)");
            var win = new Views.OverlayFootprintWindow
            { DataContext = OverlayFootprintWindowViewModel.From(fp) };
            if (PromptOwner is not null) win.Show(PromptOwner); else win.Show();
        }
        catch (Exception ex) { _log.Error("Footprint", ex.Message); }
        finally { IsBuilding = false; }
    }

    /// <summary>M136: Asset Usage — which project files can nothing ever load (dead: not shipped by
    /// any game wad, referenced by no project bin — deletable) and which belong to other content
    /// (outside-map: the wad fan-out drivers). Completes the trim workflow the Overlay Footprint opens.</summary>
    [RelayCommand]
    private async Task AssetUsage()
    {
        if (!ContentLoaded || Project.RootPath is null || Project.ProjectFolders.Count == 0)
        { _log.Warn("AssetUsage", "Open a folder project first."); return; }
        string gameFinal = Path.Combine(
            (Project.GameDirectory ?? "").Replace('/', Path.DirectorySeparatorChar), "DATA", "FINAL");
        if (!Directory.Exists(gameFinal))
        { _log.Warn("AssetUsage", "Game folder not set (Project ▸ Set Game Folder) — the analysis scans the game's WADs."); return; }

        IsBuilding = true;
        var progress = BuildProgressSink();
        try
        {
            var report = await Task.Run(() =>
            {
                var files = new List<(ulong Hash, string RelPath, string AbsPath, long Bytes)>();
                var mapNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in Project.ProjectFolders)
                {
                    var root = Project.ResolveProjectPath(f);
                    if (!Directory.Exists(root)) continue;
                    // The mount folder is NAMED after the wad it targets (cslol/fantome convention:
                    // "Map11" -> Map11.wad.client) — the reliable home-wad source even for
                    // assets-only projects that carry no data/maps paths at all.
                    var folderName = Path.GetFileName(root.TrimEnd('/', '\\'));
                    if (folderName.Length > 0 && folderName != ".") mapNames.Add(folderName);
                    foreach (var (hash, path) in Core.Build.WadPackService.EnumerateChunkFiles(root))
                    {
                        var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
                        files.Add((hash, rel, path, new FileInfo(path).Length));

                        var segs = rel.Split('/');
                        for (int i = 0; i + 1 < segs.Length; i++)
                            if ((segs[i].Equals("mapgeometry", StringComparison.OrdinalIgnoreCase)
                                 || segs[i].Equals("shipping", StringComparison.OrdinalIgnoreCase))
                                && segs[i + 1].StartsWith("map", StringComparison.OrdinalIgnoreCase))
                                mapNames.Add(segs[i + 1]);

                        if (rel.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) && rel.Contains('/'))
                        {
                            try
                            {
                                var strings = new List<string>();
                                Formats.Meta.BinStringHarvester.Collect(
                                    Formats.Meta.SafeBinTree.Parse(File.ReadAllBytes(path)), strings);
                                foreach (var s in strings)
                                    if (s.Contains('/') || s.Contains('\\')) referenced.Add(s.Replace('\\', '/'));
                            }
                            catch { /* a broken bin just contributes no references */ }
                        }
                    }
                }
                return Core.Build.AssetUsageService.Analyze(files, gameFinal, mapNames,
                    rel => referenced.Contains(rel), progress);
            });

            _log.Info("AssetUsage", $"{report.TotalFiles:n0} file(s): {report.Dead.Count:n0} dead ({report.DeadBytes / 1048576.0:0.0} MB), "
                + $"{report.OutsideMapFiles:n0} outside the map ({report.OutsideMapBytes / 1048576.0:0.0} MB), {report.MapScopedFiles:n0} map-scoped.");

            var vm = AssetUsageWindowViewModel.Build(report, async () =>
            {
                if (PromptOwner is null) return 0;
                if (!await Views.PromptWindow.ConfirmAsync(PromptOwner, "Delete Dead Files",
                    $"Delete {report.Dead.Count:n0} dead file(s) ({report.DeadBytes / 1048576.0:0.0} MB)?\n\nNo game wad ships these paths and no project bin references them — nothing can ever load them.",
                    "Delete"))
                    return 0;
                int n = 0;
                foreach (var d in report.Dead)
                {
                    try { File.Delete(d.AbsPath); n++; }
                    catch (Exception ex) { _log.Warn("AssetUsage", $"{d.RelPath}: {ex.Message}"); }
                }
                _log.Success("AssetUsage", $"Deleted {n:n0} dead file(s).");
                RefreshBrowser();
                return n;
            });
            var win = new Views.AssetUsageWindow { DataContext = vm };
            if (PromptOwner is not null) win.Show(PromptOwner); else win.Show();
        }
        catch (Exception ex) { _log.Error("AssetUsage", ex.Message); }
        finally { IsBuilding = false; }
    }

    /// <summary>M309: route every normal-map MapSkin slot and alias through another registered skin's
    /// safe environment fields. Runtime/server data stays with each slot; Map22/TFT is blocked.</summary>
    [RelayCommand]
    private async Task OpenMapSkinSwitcher()
    {
        if (!ProjectMode || Project.RootPath is null || _mounts is null)
        {
            _log.Warn("MapSkin", "Open an editable project first. Riot reference files are never modified directly.");
            return;
        }

        Status = "Scanning registered map skins...";
        try
        {
            var entries = AssetEntries
                .Where(entry => entry.IsResolved && TryParseShippingMapBin(entry.Path, out _))
                .DistinctBy(entry => entry.PathHash)
                .ToList();
            var maps = await Task.Run(() =>
            {
                var found = new List<MapSkinMapViewModel>();
                foreach (var entry in entries)
                {
                    if (!TryParseShippingMapBin(entry.Path, out int mapId)) continue;
                    try
                    {
                        var catalog = MapSkinSwitcher.ReadCatalog(ReadAsset(entry.PathHash), ResolveBinName);
                        if (MapSkinSwitcher.BlockReason(mapId, catalog.MapStringId) is { } blocked)
                        {
                            _log.Info("MapSkin", $"Map{mapId} excluded: {blocked}");
                            continue;
                        }
                        found.Add(new MapSkinMapViewModel
                        {
                            MapId = mapId,
                            ShippingBinEntry = entry,
                            Catalog = catalog,
                        });
                    }
                    catch (Exception ex) { _log.Warn("MapSkin", $"Could not inspect {entry.Path}: {ex.Message}"); }
                }
                return found;
            });
            if (maps.Count == 0)
            {
                Status = "No eligible shipping-map bins are mounted in this project.";
                _log.Warn("MapSkin", Status);
                return;
            }

            var vm = new MapSkinSwitcherViewModel(maps) { ApplySwap = ApplyMapSkinSwapAsync };
            var window = new Views.MapSkinSwitcherWindow { DataContext = vm };
            if (PromptOwner is not null) window.Show(PromptOwner); else window.Show();
            Status = $"Map Skin Switcher: {maps.Count} eligible map(s). TFT / Map22 excluded.";
        }
        catch (Exception ex)
        {
            Status = "Map Skin Switcher could not open.";
            _log.Error("MapSkin", ex.Message);
        }
    }

    private static bool TryParseShippingMapBin(string path, out int mapId)
    {
        mapId = 0;
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5
            || !parts[0].Equals("data", StringComparison.OrdinalIgnoreCase)
            || !parts[1].Equals("maps", StringComparison.OrdinalIgnoreCase)
            || !parts[2].Equals("shipping", StringComparison.OrdinalIgnoreCase)
            || !parts[3].StartsWith("map", StringComparison.OrdinalIgnoreCase)
            || !parts[4].Equals(parts[3] + ".bin", StringComparison.OrdinalIgnoreCase))
            return false;
        return int.TryParse(parts[3].AsSpan(3), out mapId);
    }

    private sealed record MapSkinPreflight(
        int SkinAssetCount,
        string? ContainerBin,
        int ShippingLinks,
        int ShippingAssets,
        int UnrelatedShippingIssues,
        int ContainerLinks,
        int ContainerAssets);

    private async Task<string> ApplyMapSkinSwapAsync(MapSkinApplyRequest request)
    {
        if (!ProjectMode || Project.RootPath is null || _mounts is null)
            throw new InvalidOperationException("The project was closed while the map-skin tool was open.");
        if (!TryResolveEntry(request.Map.ShippingBinEntry.PathHash, out var shippingEntry))
            throw new FileNotFoundException("The shipping map bin is no longer mounted.");
        if (!await EnsureProjectSavedAsync()) throw new InvalidOperationException("Save the project before creating the override.");

        byte[] original = ReadAsset(shippingEntry.PathHash);
        var swap = await Task.Run(() => MapSkinSwitcher.Switch(original, request.Map.MapId,
            request.Target.Info.PathHash, request.Source.Info.PathHash, ResolveBinName));

        string sourceContainerPath = MapSkinSwitcher.ContainerBinPath(swap.Source.MapContainerLink)
            ?? throw new InvalidDataException("The selected source skin has no materials container.");
        if (!TryResolveEntry(HashAlgorithms.WadPath(sourceContainerPath), out var sourceContainerEntry))
            throw new FileNotFoundException($"The source skin's map-container bin is missing: {sourceContainerPath}");
        byte[] sourceContainerOriginal = ReadAsset(sourceContainerEntry.PathHash);

        MapSkinContainerCompatibilityResult compatibility;
        string? targetContainerPath = MapSkinSwitcher.ContainerBinPath(swap.Target.MapContainerLink);
        if (targetContainerPath is not null)
        {
            if (!TryResolveEntry(HashAlgorithms.WadPath(targetContainerPath), out var targetContainerEntry))
                throw new FileNotFoundException($"The current/base skin's map-container bin is missing: {targetContainerPath}");
            byte[] targetContainerBytes = ReadAsset(targetContainerEntry.PathHash);
            compatibility = await Task.Run(() => MapSkinSwitcher.BuildCompatibleContainer(
                targetContainerBytes, sourceContainerOriginal));
        }
        else compatibility = new MapSkinContainerCompatibilityResult(sourceContainerOriginal, 0, 0);

        var preflight = await Task.Run(() => ValidateMapSkinSwap(shippingEntry.Path,
            swap, sourceContainerPath, compatibility.Bytes));

        string safeRoute = SanitizeFileName($"Map{request.Map.MapId}-{swap.Target.Name}-to-{swap.Source.Name}");
        string backupDir = Path.Combine(Project.RootPath, ".reyengine", "backups",
            $"map-skin-{safeRoute}-{DateTime.Now:yyyyMMdd-HHmmss}");
        string backupFile = Path.Combine(backupDir,
            shippingEntry.Path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
        File.WriteAllBytes(backupFile, original);
        string containerBackup = Path.Combine(backupDir,
            sourceContainerEntry.Path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(containerBackup)!);
        File.WriteAllBytes(containerBackup, sourceContainerOriginal);
        if (Project.ProjectFilePath is { } projectFile && File.Exists(projectFile))
            File.Copy(projectFile, Path.Combine(backupDir, "project.before-map-skin.json"), overwrite: true);

        if (!await SaveGeneratedMapSkinBinAsync(shippingEntry, swap.Bytes))
            throw new IOException("The validated shipping bin could not be saved to the project.");
        if (!await SaveGeneratedMapSkinBinAsync(sourceContainerEntry, compatibility.Bytes))
            throw new IOException("The gameplay-compatible source container could not be saved to the project.");
        _overrides.SaveTo(Project);
        ReyProjectService.Save(Project, Project.ProjectFilePath!);
        BuildMounts();
        BuildProjectTree();
        UpdateTitle();

        string reportDir = ProjectWorkspace.ReportsDir(Project);
        string reportFile = Path.Combine(reportDir, $"map-skin-{safeRoute}.txt");
        var lines = new List<string>
        {
            $"Project: {Project.Name}",
            $"UTC: {DateTime.UtcNow:O}",
            $"Map: Map{request.Map.MapId} ({request.Map.Catalog.MapStringId})",
            $"Requested base skin: {swap.Target.Name} [{swap.Target.ObjectPath}]",
            $"Previous base container: {swap.Target.MapContainerLink ?? "legacy/default"}",
            $"Source skin: {swap.Source.Name} [{swap.Source.ObjectPath}]",
            $"Source container: {swap.Source.MapContainerLink ?? "legacy/default"}",
            $"MapSkin definitions rerouted (registered + aliases): {swap.RoutedSkinHashes.Count:n0}",
            $"Changed environment-route properties: {swap.ChangedRouteProperties:n0}",
            $"Audio profile: {(swap.RoutedAudioSourceHash is null
                ? "no dedicated source profile"
                : swap.ChangedAudioProperties > 0
                    ? $"routed ({swap.ChangedAudioProperties:n0} properties)"
                    : "already routed")}",
            $"Server gameplay placeables matched: {compatibility.MatchedServerPlaceables:n0}",
            $"Server gameplay keys remapped: {compatibility.RemappedServerPlaceableKeys:n0}",
            $"Verified skin-level files: {preflight.SkinAssetCount:n0}",
            $"Verified container bin: {preflight.ContainerBin ?? "not used by this legacy skin"}",
            $"Shipping bin validation: {preflight.ShippingLinks:n0} links, {preflight.ShippingAssets:n0} assets, 0 routed-slot issues",
            $"Unchanged Riot objects with validator warnings: {preflight.UnrelatedShippingIssues:n0}",
            $"Container validation: {preflight.ContainerLinks:n0} links, {preflight.ContainerAssets:n0} assets, 0 issues",
            $"Backup: {backupDir}",
        };
        string? reportWarning = null;
        try { File.WriteAllLines(reportFile, lines); }
        catch (Exception ex)
        {
            reportWarning = $" The override is saved, but its report could not be written: {ex.Message}";
            _log.Warn("MapSkin", reportWarning);
        }

        string message = $"Ready: Map{request.Map.MapId}'s slots and aliases now use {swap.Source.Name}'s crash-safe environment"
            + (swap.RoutedAudioSourceHash is not null ? ", music and ambience. " : ". ")
            + $"Preserved {compatibility.MatchedServerPlaceables:n0} server gameplay identities. "
            + $"Verified {preflight.ShippingLinks + preflight.ContainerLinks:n0} links and "
            + $"{preflight.ShippingAssets + preflight.ContainerAssets:n0} asset references; backup written."
            + (reportWarning is null ? " Report written." : reportWarning);
        Status = message;
        _log.Success("MapSkin", message);
        return message;
    }

    private async Task<bool> SaveGeneratedMapSkinBinAsync(WadAssetEntry entry, byte[] bytes)
    {
        try { _ = SafeBinTree.Parse(bytes); }
        catch (Exception ex) { _log.Error("MapSkin", $"Generated {entry.DisplayName} failed to re-parse: {ex.Message}"); return false; }

        if (TryWriteToProjectFile(entry, bytes, out _))
        {
            Project.IsDirty = true;
            return true;
        }
        if (TryPlaceInProjectFolder(entry, bytes, out _))
        {
            Project.IsDirty = true;
            return true;
        }
        return await SaveMapBinBytesAsync(entry, bytes);
    }

    private MapSkinPreflight ValidateMapSkinSwap(string shippingPath, MapSkinSwapResult swap,
        string containerPath, byte[] containerBytes)
    {
        bool AssetExists(string path) => TryResolveEntry(HashAlgorithms.WadPath(path), out _);
        List<byte[]> Dependencies(byte[] bytes)
        {
            var result = new List<byte[]>();
            foreach (var dependency in VfxSystemResolver.ExtractDependencies(bytes))
            {
                if (!TryResolveEntry(HashAlgorithms.WadPath(dependency), out var entry))
                    throw new InvalidDataException($"Required dependency is not mounted: {dependency}");
                result.Add(ReadAsset(entry.PathHash));
            }
            return result;
        }
        bool LinkExempt(uint hash) => ResolveBinName(hash)?.StartsWith("Shaders/", StringComparison.OrdinalIgnoreCase) == true;

        var assetPaths = MapSkinSwitcher.AssetPaths(swap.ReferencedStrings);
        var missing = assetPaths.Where(path => !AssetExists(path)).ToList();
        if (missing.Count > 0)
            throw new InvalidDataException($"The selected skin is incomplete in this client: {missing.Count:n0} referenced file(s) are missing. First: {missing[0]}");

        var shippingReport = BinValidator.Validate(shippingPath, swap.Bytes, Dependencies(swap.Bytes),
            AssetExists, ResolveBinName, LinkExempt);
        var routedHashes = swap.RoutedSkinHashes.ToHashSet();
        if (swap.RoutedAudioTargetHash is { } audioHash) routedHashes.Add(audioHash);
        var routedIssues = shippingReport.Issues.Where(issue => routedHashes.Contains(issue.ObjectPathHash)).ToList();
        if (routedIssues.Count > 0)
            throw new InvalidDataException($"A routed map-skin slot failed injection validation: "
                + $"{routedIssues[0].Category}: {routedIssues[0].Detail}");

        var tree = SafeBinTree.Parse(containerBytes);
        uint containerHash = HashAlgorithms.Fnv1a(swap.Source.MapContainerLink!);
        if (!tree.Objects.TryGetValue(containerHash, out var container)
            || container.ClassHash != HashAlgorithms.Fnv1a("MapContainer"))
            throw new InvalidDataException($"{containerPath} does not contain the required MapContainer {swap.Source.MapContainerLink}.");

        var report = BinValidator.Validate(containerPath, containerBytes, Dependencies(containerBytes),
            AssetExists, ResolveBinName, LinkExempt);
        if (!report.IsClean)
            throw new InvalidDataException($"The source MapContainer failed injection validation: "
                + $"{report.Issues[0].Category}: {report.Issues[0].Detail}");
        int containerLinks = report.LinksChecked;
        int containerAssets = report.AssetRefsChecked;

        return new MapSkinPreflight(assetPaths.Count, containerPath, shippingReport.LinksChecked,
            shippingReport.AssetRefsChecked, shippingReport.Issues.Count, containerLinks, containerAssets);
    }

    /// <summary>M97c: rebase every project .bin from the patch the mod was built for onto the current
    /// patch (CommunityDragon old original + M97a three-way merge).</summary>
    [RelayCommand]
    private void OpenPatchUpdateWizard()
    {
        if (!ContentLoaded) { _log.Warn("PatchUpdate", "Open a project first."); return; }
        if (Project.RootPath is null || Project.ProjectFolders.Count == 0)
        { _log.Warn("PatchUpdate", "The wizard needs a folder project."); return; }

        var vm = new PatchUpdateWindowViewModel
        {
            ListPatches = Services.CommunityDragonClient.ListPatchesAsync,
            DownloadOld = (patch, rel) => Services.CommunityDragonClient.DownloadBinAsync(
                patch, rel, Services.CommunityDragonClient.DefaultCacheDir),
            ReadCurrentOriginal = ReadRiotOriginalBytes,
            ReadProjectBytes = ReadAsset,
            SaveBytes = SaveMapBinBytesAsync,
            RunValidate = ValidateProjectBins,
            Resolve = ResolveBinName,
        };
        string? backupDir = null;   // one folder per wizard run, created lazily
        vm.Backup = (rel, bytes) =>
        {
            try
            {
                backupDir ??= Path.Combine(Project.RootPath!, ".reyengine", "backups",
                    $"patch-update-{DateTime.Now:yyyyMMdd-HHmmss}");
                var dest = Path.Combine(backupDir, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.WriteAllBytes(dest, bytes);
                return dest;
            }
            catch (Exception ex) { _log.Warn("PatchUpdate", $"Backup of {rel} failed: {ex.Message}"); return null; }
        };

        foreach (var folder in Project.ProjectFolders)
        {
            string root = Path.Combine(Project.RootPath!, folder);
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*.bin", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (!rel.Contains('/')) continue;   // loose unresolved-chunk dumps, not real bins
                if (TryResolveEntry(HashAlgorithms.WadPath(rel), out var entry))
                    vm.Bins.Add(new PatchUpdateBinRowViewModel { Rel = rel, Entry = entry });
            }
        }
        if (vm.Bins.Count == 0) { _log.Warn("PatchUpdate", "No project .bin files found."); return; }

        var win = new Views.PatchUpdateWindow { DataContext = vm };
        if (PromptOwner is not null) win.Show(PromptOwner); else win.Show();
        _ = vm.InitAsync();
    }

    /// <summary>M94: convert a .fantome mod package into an editable folder project under
    /// Documents\ReyEngine Projects, then open it — lets users mod existing mods.</summary>
    [RelayCommand]
    private async Task ImportFantome()
    {
        var file = await Dialogs.OpenFileAsync("Import .fantome mod package",
            new Avalonia.Platform.Storage.FilePickerFileType("Fantome mod package") { Patterns = new[] { "*.fantome", "*.zip" } },
            DialogService.All);
        if (file is null) return;
        try
        {
            Status = "Importing .fantome…";
            Directory.CreateDirectory(ProjectsFolder);
            string? gameDir = !string.IsNullOrEmpty(Project.GameDirectory) && Directory.Exists(Project.GameDirectory)
                ? Project.GameDirectory
                : ReyEngine.Core.Projects.GameInstallLocator.Discover().FirstOrDefault()?.GameDirectory;
            var progress = new Progress<string>(m => Status = m);
            var result = await Task.Run(() => ReyEngine.Core.Projects.FantomeImporter.Import(
                file, ProjectsFolder, gameDir, _resolver, progress));
            _log.Success("Import", $"{result.ProjectName}: {result.Wads} WAD(s), {result.ExtractedFiles:n0} file(s) unpacked" +
                (result.RawFiles > 0 ? $" + {result.RawFiles} RAW file(s)" : "") +
                (result.FailedChunks > 0 ? $" ({result.FailedChunks} chunk(s) failed — usually subchunked textures)" : "") +
                $" → {result.RootPath}");
            OpenProjectAt(result.RootPath);   // also records it in Open Recent
        }
        catch (Exception ex) { _log.Error("Import", $"Fantome import failed: {ex.Message}"); }
    }

    [RelayCommand]
    private void OpenRecentProject(string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return;
        if (!Directory.Exists(folder)) { _log.Warn("Project", $"Folder no longer exists: {folder}"); return; }
        OpenProjectAt(folder);
    }

    private void OpenProjectAt(string folder)
    {
        try
        {
            var project = ReyProjectService.OpenFolder(folder);
            Project = project;
            _overrides.LoadFrom(project);
            _archive?.Dispose(); _archive = null;
            Documents.Clear(); ActiveDocument = null; // same path hash in another project is different content
            BuildMounts();
            BuildProjectTree();
            ClearViewport(); Inspector.Clear(); BinEditor.Clear(); MaterialEditor.Clear();
            UndoService.Clear(); // new project = fresh history
            ProjectMode = true; InspectionMode = false;
            HasMaterialData = false; HasInspectorBody = false;
            LoadCachedShaderDb();
            LoadRecentProjects(RecentProjects.Add(folder));
            UpdateTitle();
            Status = $"Project '{project.Name}' — {_mounts!.Count:n0} assets across {_mounts.Mounts.Count} mount(s)";
            _log.Success("Project", $"Opened '{project.Name}': {project.ProjectFolders.Count} folder(s), {project.ProjectWads.Count} WAD(s), {project.ReferenceWads.Count} Riot reference(s); {_mounts.Count:n0} assets mounted.");
            StartProjectWatchers();   // M100: auto-refresh the browser on external file changes
            if (project.ReferenceWads.Count == 0)
                _log.Info("Project", "No Riot references yet — add one via Project ▸ Manage Riot References to preview/copy source assets.");
        }
        catch (Exception ex) { _log.Error("Project", ex.Message); }
    }

    /// <summary>M133: the projects folder — the configured one (Settings ▸ General) when set,
    /// else Documents\ReyEngine Projects (which OneDrive may redirect). Created on demand.</summary>
    public string ProjectsFolder =>
        !string.IsNullOrWhiteSpace(Settings.ProjectsDirectory) ? Settings.ProjectsDirectory : DefaultProjectsFolder;

    public static string DefaultProjectsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ReyEngine Projects");

    private void LoadRecentProjects(IEnumerable<string> folders)
    {
        RecentProjectList.Clear();
        // M80: only list REAL project folders (a .reyengine/project.json inside) — the store accumulated
        // junk over time (unpacked-wad subfolders, the .reyengine dir itself, deleted paths).
        foreach (var f in folders)
            if (IsProjectFolder(f))
                RecentProjectList.Add(new RecentProjectViewModel(f, OpenRecentProject));

        // M80: also list everything in the canonical projects folder (wizard-created projects show up
        // even if they were never opened on this machine / the recents store was cleared).
        try
        {
            Directory.CreateDirectory(ProjectsFolder);
            foreach (var dir in Directory.EnumerateDirectories(ProjectsFolder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                if (IsProjectFolder(dir)
                    && !RecentProjectList.Any(r => string.Equals(r.Path, dir, StringComparison.OrdinalIgnoreCase)))
                    RecentProjectList.Add(new RecentProjectViewModel(dir, OpenRecentProject));
        }
        catch { /* projects folder unreadable — recents alone */ }

        OnPropertyChanged(nameof(HasRecentProjects));
    }

    private static bool IsProjectFolder(string dir) =>
        Directory.Exists(dir)
        && File.Exists(Path.Combine(dir, ReyProjectService.FolderMetaDir, ReyProjectService.FolderMetaFile))
        && !dir.TrimEnd('/', '\\').EndsWith(ReyProjectService.FolderMetaDir, StringComparison.OrdinalIgnoreCase);

    private void BuildMounts()
    {
        _mounts?.Dispose();
        _mounts = new AssetMountService();
        if (Project.OverridesDirectory is { } ov) _mounts.Add(new OverrideMount(ov, _resolver));
        foreach (var f in Project.ProjectFolders)
            try { _mounts.Add(new FolderMount(Project.ResolveProjectPath(f), _resolver, f == "." ? Project.Name : f)); }
            catch (Exception ex) { _log.Warn("Project", $"folder {f}: {ex.Message}"); }
        foreach (var w in Project.ProjectWads)
            try { _mounts.Add(new WadMount(WadArchive.Open(Project.ResolveProjectPath(w), _resolver), AssetSourceKind.ProjectWad, editable: true)); }
            catch (Exception ex) { _log.Warn("Project", $"WAD {w}: {ex.Message}"); }
        foreach (var r in Project.ReferenceWads)
            try { _mounts.Add(new WadMount(WadArchive.Open(r, _resolver), AssetSourceKind.RiotReference, editable: false, name: Path.GetFileName(r))); }
            catch (Exception ex) { _log.Warn("Project", $"reference {Path.GetFileName(r)}: {ex.Message}"); }

        AddGameFallback();
        _mounts.Rebuild();
    }

    /// <summary>Mount the original Riot game WADs as read-only fallback so missing assets resolve from the install.</summary>
    private void AddGameFallback()
    {
        if (_mounts is null) return;
        var mapNames = Project.ProjectFolders.Concat(Project.ProjectWads)
            .Select(p => p == "." ? Project.Name : Path.GetFileNameWithoutExtension(p).Replace(".wad", "", StringComparison.OrdinalIgnoreCase))
            .Append(Project.Name)
            // A project folder is not always named Map11. Recover the target WAD from resolved asset
            // paths such as data/maps/mapgeometry/map11/base_srx.mapgeo as well.
            .Concat(_mounts.Mounts
                .Where(m => m.Kind != AssetSourceKind.RiotReference)
                .SelectMany(m => m.Enumerate())
                .Select(a => MapNameFromAssetPath(a.VirtualPath))
                .OfType<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var status = GameReferenceLibrary.Inspect(Project.GameDirectory);
        if (!status.IsValid)
        {
            LogGameFallbackOnce("error", status.Message
                + " Missing skins, props, materials and textures will not resolve. "
                + "Fix: Project > Set Game Folder..., select the League of Legends\\Game folder containing DATA\\FINAL, then reopen the map.");
            return;
        }

        int mounted = 0;
        var discovered = GameReferenceLibrary.Discover(status.GameDirectory, mapNames);
        foreach (var wad in discovered)
        {
            if (Project.ReferenceWads.Contains(wad, StringComparer.OrdinalIgnoreCase)) continue;
            try
            {
                _mounts.AddFallback(new WadMount(WadArchive.Open(wad), AssetSourceKind.RiotReference,
                    editable: false, name: Path.GetFileName(wad)));
                mounted++;
            }
            catch (Exception ex) { _log.Warn("Project", $"game fallback {Path.GetFileName(wad)}: {ex.Message}"); }
        }

        var missingMapWads = mapNames
            .Where(n => System.Text.RegularExpressions.Regex.IsMatch(n, @"^map\d+$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .Where(n => !File.Exists(Path.Combine(status.FinalDirectory!, "Maps", "Shipping", n + ".wad.client")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (missingMapWads.Length > 0)
            LogGameFallbackOnce("error",
                $"The League folder was recognized, but required map WAD(s) are missing: {string.Join(", ", missingMapWads.Select(n => n + ".wad.client"))}. "
                + "The folder may point to an old or incomplete install; map recolouring and asset resolution can fail. "
                + "Update League or use Project > Set Game Folder... to select the active League of Legends\\Game folder, then reopen the map.");
        else if (discovered.Count > 0)
            LogGameFallbackOnce("info",
                $"Game asset folder verified: {status.GameDirectory}. {discovered.Count:n0} relevant WAD(s) found"
                + (mounted != discovered.Count
                    ? $", {mounted:n0} added as fallback (the rest are already explicit references)"
                    : " and mounted as read-only fallback") + ".");
        else
            LogGameFallbackOnce("error",
                $"The game folder is valid, but no relevant WADs were found under {status.FinalDirectory}. "
                + "Update League, then reopen the project. If the install moved, use Project > Set Game Folder....");
    }

    private static string? MapNameFromAssetPath(string path)
    {
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 1 < segments.Length; i++)
            if ((segments[i].Equals("mapgeometry", StringComparison.OrdinalIgnoreCase)
                 || segments[i].Equals("shipping", StringComparison.OrdinalIgnoreCase))
                && System.Text.RegularExpressions.Regex.IsMatch(segments[i + 1], @"^map\d+$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return segments[i + 1];
        return null;
    }

    private void LogGameFallbackOnce(string level, string message)
    {
        string key = level + "|" + message;
        if (string.Equals(_lastGameFallbackNotice, key, StringComparison.Ordinal)) return;
        _lastGameFallbackNotice = key;
        if (level == "error") _log.Error("Project", message);
        else _log.Info("Project", message);
    }

    private void BuildProjectTree()
    {
        if (_mounts is null) return;
        RootNodes.Clear();
        _nodesByHash.Clear();
        _thumbnails.Clear();

        var projectGroup = new AssetTreeNode { Name = "Project", IsFolder = true };
        foreach (var mount in _mounts.Mounts.Where(m => m.Kind != AssetSourceKind.RiotReference))
        {
            var entries = mount.Enumerate().Select(a => a.ToEntry()).ToList();
            // M110: a folder mount stays listed even with no files — it may hold only empty folders.
            var dirs = mount is FolderMount fm ? fm.Directories : (IReadOnlyList<string>)Array.Empty<string>();
            if (entries.Count == 0 && dirs.Count == 0) continue;
            var subtree = AssetTree.Build(entries, mount.Name);
            if (dirs.Count > 0) AssetTree.EnsureFolders(subtree, dirs);
            projectGroup.Children.Add(subtree);
        }

        var riotGroup = new AssetTreeNode { Name = "Riot References", IsFolder = true };
        foreach (var mount in _mounts.Mounts.Where(m => m.Kind == AssetSourceKind.RiotReference))
            riotGroup.Children.Add(AssetTree.Build(mount.Enumerate().Select(a => a.ToEntry()).ToList(), mount.Name));

        var projectVm = new AssetNodeViewModel(projectGroup);
        var riotVm = new AssetNodeViewModel(riotGroup);

        // M33: graft the project's materials in as virtual "ASSETS/<material path>" tree nodes so every
        // StaticMaterialDef in a .materials.bin / skin .bin is browsable (and openable) as a first-class
        // asset. Project mounts only — reference WADs hold far too many materials to extract eagerly.
        foreach (var mount in _mounts.Mounts.Where(m => m.Kind != AssetSourceKind.RiotReference))
        {
            var mountVm = projectVm.Children.FirstOrDefault(c => c.Name == mount.Name);
            if (mountVm is null) continue;
            InjectMaterialAssets(mountVm, mount.Enumerate().Select(a => a.ToEntry()).ToList(), readOnly: false);
        }

        RootNodes.Add(projectVm);
        if (riotGroup.Children.Count > 0) RootNodes.Add(riotVm);

        // Index Riot first, then Project, so a conflicted asset's *project* node wins status updates.
        IndexNodes(riotVm);
        IndexNodes(projectVm);
        RefreshAllStatuses();
        RefreshContentPanels();
    }

    /// <summary>Graft each material-library bin's materials into the tree as virtual "ASSETS/&lt;name&gt;" nodes (M33).</summary>
    private void InjectMaterialAssets(AssetNodeViewModel mountVm, IReadOnlyList<WadAssetEntry> entries, bool readOnly)
    {
        AssetNodeViewModel? assetsRoot = null;
        int count = 0;
        foreach (var e in entries.Where(x => x.IsResolved && MaterialLibraryExtractor.IsMaterialLibrary(x.Path)))
        {
            IReadOnlyList<Formats.Materials.MaterialSummary> mats;
            try { mats = MaterialLibraryExtractor.Extract(GetAssetBytes(e), ResolveBinName); }
            catch { continue; }
            if (mats.Count == 0) continue;

            assetsRoot ??= GetOrAddChildFolder(mountVm, "ASSETS");
            foreach (var m in mats)
            {
                var matVm = new MaterialAssetViewModel(m, e, readOnly);
                var parts = m.Name.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var folder = assetsRoot;
                for (int i = 0; i < parts.Length - 1; i++) folder = GetOrAddChildFolder(folder, parts[i]);
                folder.AddChild(AssetNodeViewModel.MaterialLeaf(matVm));
                count++;
            }
        }
        if (count > 0) _log.Info("Materials", $"{mountVm.Name}: exposed {count} material(s) as virtual assets under ASSETS/.");
    }

    private static AssetNodeViewModel GetOrAddChildFolder(AssetNodeViewModel parent, string name)
    {
        var existing = parent.Children.FirstOrDefault(c => c.IsFolder && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
        var f = AssetNodeViewModel.VirtualFolder(name);
        parent.AddChild(f);
        return f;
    }

    /// <summary>Re-enumerate the override mount after a save/copy so reads + conflicts reflect new files.</summary>
    private void RefreshOverrideMount()
    {
        if (_mounts is null) return;
        BuildMounts();
    }

    [RelayCommand]
    private async Task SetGameFolder()
    {
        var folder = await Dialogs.OpenFolderAsync("Select the League of Legends 'Game' folder (for reference fallback)");
        if (folder is null) return;
        var status = GameReferenceLibrary.Inspect(folder);
        if (!status.IsValid)
        {
            _log.Error("Project", status.Message
                + " Select the League of Legends\\Game folder containing DATA\\FINAL. The previous setting was kept.");
            return;
        }
        Project.GameDirectory = status.GameDirectory;
        var probe = GameReferenceLibrary.Discover(status.GameDirectory, Project.ProjectFolders.Append(Project.Name));
        _lastGameFallbackNotice = null;
        if (ProjectMode)
        {
            ReyProjectService.Save(Project, Project.ProjectFilePath!);
            BuildMounts(); BuildProjectTree();
        }
        _log.Success("Project", $"Game folder set and verified: {status.GameDirectory} — {probe.Count} reference WAD(s) available. Reopen the map before retrying Recolor Textures.");
    }

    // ---- Riot shader database (M18) -------------------------------------

    private string? ShaderCachePath =>
        Project.WorkspaceDirectory is { } w ? Path.Combine(w, "shader_cache.json") : null;

    private void LoadCachedShaderDb()
    {
        _shaderDb = ShaderCachePath is { } p ? ShaderCacheService.Load(p) : null;
        ShaderDbStatus = _shaderDb is { } d
            ? $"Riot shaders: {d.Shaders.Count:n0} ({d.VertexCount} VS · {d.PixelCount} PS), cached."
            : "Riot shaders not scanned — Tools ▸ Scan Riot Shaders.";
    }

    [RelayCommand]
    private async Task ScanRiotShaders()
    {
        var path = GameReferenceLibrary.FindShaderCache(Project.GameDirectory);
        if (path is null)
        {
            _log.Warn("Shader", "ShaderCache.dx11.wad.client not found — set the game folder in Project Settings first.");
            return;
        }
        _log.Info("Shader", $"Scanning {Path.GetFileName(path)} …");
        Status = "Scanning Riot shaders…";
        try
        {
            var db = await Task.Run(() =>
            {
                using var wad = WadArchive.Open(path, _resolver);
                return ShaderScanner.Scan(wad);
            });
            _shaderDb = db;
            if (ShaderCachePath is { } cp) { ShaderCacheService.Save(db, cp); }
            ShaderDbStatus = $"Riot shaders: {db.Shaders.Count:n0} ({db.VertexCount} VS · {db.PixelCount} PS), cached.";
            _log.Success("Shader", $"Scanned {db.Shaders.Count:n0} shaders ({db.VertexCount} vertex, {db.PixelCount} pixel). Cached to {(ShaderCachePath is null ? "(memory)" : ".reyengine/shader_cache.json")}.");
            Status = ShaderDbStatus;
        }
        catch (Exception ex) { _log.Error("Shader", ex.Message); }
    }

    // ---- M103: League shader catalogue (Live / PBE) ---------------------

    private readonly Dictionary<string, string> _shaderEnvironmentDirs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>List the installs whose shader list can be browsed: every discovered client, plus the
    /// project's own game directory when it isn't one of them.</summary>
    private void InitShaderEnvironments()
    {
        _shaderEnvironmentDirs.Clear();
        MaterialEditor.ShaderEnvironments.Clear();
        foreach (var install in GameInstallLocator.Discover())
            if (_shaderEnvironmentDirs.TryAdd(install.Platform, install.GameDirectory))
                MaterialEditor.ShaderEnvironments.Add(install.Platform);

        if (Project.GameDirectory is { Length: > 0 } gd
            && !_shaderEnvironmentDirs.Values.Any(d => string.Equals(d, gd, StringComparison.OrdinalIgnoreCase))
            && _shaderEnvironmentDirs.TryAdd("Project", gd))
            MaterialEditor.ShaderEnvironments.Add("Project");

        // Prefer the install the project actually targets, else the first one found.
        var preferred = _shaderEnvironmentDirs.FirstOrDefault(kv =>
            string.Equals(kv.Value, Project.GameDirectory, StringComparison.OrdinalIgnoreCase)).Key
            ?? MaterialEditor.ShaderEnvironments.FirstOrDefault();
        if (preferred is not null) MaterialEditor.SelectedShaderEnvironment = preferred;
    }

    private static string ShaderCatalogCachePath(string environment) =>
        Path.Combine(ReyEngine.Core.ReyPaths.DataRoot, "shader_catalogs", $"{environment}.json");

    /// <summary>Scan (or load from cache) one install's shader definitions for the Material Editor.</summary>
    private async Task LoadShaderCatalogAsync(string environment)
    {
        if (!_shaderEnvironmentDirs.TryGetValue(environment, out var gameDir))
        {
            MaterialEditor.SetCatalog(null);
            return;
        }
        var cachePath = ShaderCatalogCachePath(environment);
        var cached = await Task.Run(() => ShaderCatalogCache.Load(cachePath, gameDir));
        if (cached is not null) { MaterialEditor.SetCatalog(cached); return; }

        var wad = GameReferenceLibrary.FindGlobalWad(gameDir);
        if (wad is null)
        {
            MaterialEditor.SetCatalog(null);
            _log.Warn("Shader", $"{environment}: Global.wad.client not found under {gameDir} — no shader list.");
            return;
        }
        _log.Info("Shader", $"Reading {environment} shader definitions…");
        var catalog = await Task.Run(() =>
            ShaderCatalogLoader.Load(wad, gameDir, environment, _resolver, h => ResolveBinName(h)));
        if (catalog is not null)
        {
            await Task.Run(() => ShaderCatalogCache.Save(catalog, cachePath));
            _log.Success("Shader", $"{environment}: {catalog.Shaders.Count:n0} shader definitions loaded.");
        }
        else _log.Warn("Shader", $"{environment}: {ShaderCatalogLoader.ShaderBinPath} not readable.");
        MaterialEditor.SetCatalog(catalog);
    }

    [RelayCommand]
    private async Task ExportShaderDump()
    {
        var entry = SelectedNode?.Entry;
        if (entry is null || _archive is null && _mounts is null) { _log.Warn("Shader", "Select a shader (.dx11) asset first."); return; }
        // M277: the cache ships both ".dx11" and "-dx11" spellings (the 2026-07-29 patch renamed them all),
        // so testing only the dotted one rejects every shader asset in a current install.
        if (!entry.Path.Contains(".dx11", StringComparison.OrdinalIgnoreCase)
            && !entry.Path.Contains("-dx11", StringComparison.OrdinalIgnoreCase))
        { _log.Warn("Shader", "Selected asset isn't a shader (.dx11/-dx11)."); return; }
        var outPath = await Dialogs.SaveFileAsync("Export shader bytecode", entry.DisplayName);
        if (outPath is null) return;
        try { await File.WriteAllBytesAsync(outPath, ReadAsset(entry.PathHash)); _log.Success("Shader", $"Wrote {outPath}."); }
        catch (Exception ex) { _log.Error("Shader", ex.Message); }
    }

    [RelayCommand]
    private async Task SetOutputFolder()
    {
        var folder = await Dialogs.OpenFolderAsync("Select the build output folder");
        if (folder is null) return;
        if (BuildSafety.IsInsideGameInstall(folder)) { _log.Error("Project", "Refusing to set the output inside a Riot/League install folder."); return; }
        Project.OutputDirectory = folder;
        if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
        _log.Success("Project", $"Build output folder set: {folder}");
    }

    // ---- Project Settings dialog + .fantome export (M17) ----------------

    public event Action? RequestProjectSettings;

    [RelayCommand]
    private void OpenProjectSettings()
    {
        if (!ProjectMode) { _log.Warn("Project", "Open a project folder first."); return; }
        RequestProjectSettings?.Invoke();
    }

    // ---- Editor preferences (M40): keybinds + camera feel, persisted to %AppData%/ReyEngine ----
    public ReyEngine.Core.Settings.EditorSettings Settings { get; } = ReyEngine.Core.Settings.EditorSettings.Load();
    public event Action? RequestSettings;

    [RelayCommand]
    private void OpenSettings() => RequestSettings?.Invoke();

    /// <summary>Called by the view after the Preferences dialog is saved: persist + let the view re-apply.</summary>
    public void ApplyEditorSettings(SettingsViewModel vm)
    {
        Settings.CopyFrom(vm.ToSettings());
        Settings.Save();
        CullBackfaces = Settings.CullBackfacesDefault;
        _log.Success("Settings", "Preferences saved.");
        // M88: apply the preview backdrop change immediately if a model preview is already open.
        if (MeshPreview.Mesh is not null) _ = ApplyPreviewBackgroundAsync();
    }

    /// <summary>Called by the view after the settings dialog is saved.</summary>
    public void ApplyProjectSettings(ProjectSettingsViewModel vm)
    {
        vm.ApplyTo(Project);
        if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
        if (ProjectMode) { BuildMounts(); BuildProjectTree(); }
        _log.Success("Project", "Project settings saved.");
    }

    [RelayCommand]
    private async Task ExportFantome()
    {
        if (!ProjectMode || Project.RootPath is null) { _log.Warn("Export", "Open a project folder first."); return; }
        if (string.IsNullOrWhiteSpace(Project.ModAuthor))
            _log.Info("Export", "Tip: set the author / version / thumbnail in Project ▸ Project Settings for a complete package.");

        string name = Project.EffectiveModName;
        string author = string.IsNullOrWhiteSpace(Project.ModAuthor) ? "Unknown" : Project.ModAuthor!;
        var suggested = SanitizeFileName($"{name} by {author}.fantome");
        var outPath = await Dialogs.SaveFileAsync("Export .fantome", suggested);
        if (outPath is null) return;
        if (!outPath.EndsWith(".fantome", StringComparison.OrdinalIgnoreCase)) outPath += ".fantome";

        var thumb = LoadThumbnailPng(Project.ThumbnailPath);
        var meta = new FantomeMeta
        {
            Name = name,
            Author = author,
            Version = string.IsNullOrWhiteSpace(Project.ModVersion) ? "1.0.0" : Project.ModVersion,
            Description = Project.ModDescription ?? "",
            Heart = Project.ModHeart,
            Home = Project.ModHome,
        };

        IsBuilding = true; Status = "Exporting .fantome…";
        var progress = BuildProgressSink();
        try
        {
            await Task.Run(() =>
            {
                var buildRoot = Project.OutputDirectory ?? Path.Combine(Project.RootPath, "Build");
                if (BuildSafety.IsInsideGameInstall(buildRoot))
                    throw new InvalidOperationException("Build output is inside the game install — change it in Project Settings.");
                Directory.CreateDirectory(buildRoot);
                // M131: fresh build + bundle EXACTLY what it produced — stale wads lying in the
                // build folder from earlier project layouts used to sneak into the package.
                var wads = BuildProjectCore(buildRoot, progress);
                if (wads.Count == 0) throw new InvalidOperationException("No WAD was produced — the project has no packable content.");
                progress.Report((0.98, $"Zipping {Path.GetFileName(outPath)}…"));
                FantomeExporter.Export(meta, wads, thumb, outPath);
            });
            _log.Success("Export", $"Wrote {outPath} ({new FileInfo(outPath).Length / 1048576.0:0.0} MB) — {meta.Name} v{meta.Version} by {meta.Author}.");
            Status = $"Exported {Path.GetFileName(outPath)}";
        }
        catch (Exception ex) { _log.Error("Export", ex.Message); }
        finally { IsBuilding = false; }
    }

    private byte[]? LoadThumbnailPng(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            using var image = SixLabors.ImageSharp.Image.Load(path);
            using var ms = new MemoryStream();
            SixLabors.ImageSharp.ImageExtensions.SaveAsPng(image, ms);
            return ms.ToArray();
        }
        catch (Exception ex) { _log.Warn("Export", $"thumbnail: {ex.Message}"); return null; }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, ' ');
        return name.Trim();
    }

    [RelayCommand]
    private async Task ManageRiotReferences()
    {
        if (!ProjectMode) { _log.Warn("Project", "Open a project folder first."); return; }
        var path = await Dialogs.OpenFileAsync("Add Riot reference WAD", DialogService.Wad, DialogService.All);
        if (path is null) return;
        if (Project.ReferenceWads.Contains(path, StringComparer.OrdinalIgnoreCase)) { _log.Info("Project", "Reference already added."); return; }
        Project.ReferenceWads.Add(path);
        Project.IsDirty = true;
        ReyProjectService.Save(Project, Project.ProjectFilePath!);
        BuildMounts();
        BuildProjectTree();
        _log.Success("Project", $"Added Riot reference {Path.GetFileName(path)} — {_mounts!.Count:n0} assets now mounted.");
    }

    [RelayCommand(CanExecute = nameof(CanCopyAssetToProject))]
    private async Task CopyAssetToProject()
    {
        var nodes = ContextNodes.Where(n => !n.IsFolder && n.Entry is not null).ToList();
        if (nodes.Count == 0) { _log.Warn("Project", "Select an asset to copy."); return; }
        if (!ProjectMode || _mounts is null) { _log.Warn("Project", "Copy to Project needs an open project."); return; }

        // M107: one asset keeps the detailed per-file prompt.
        if (nodes.Count == 1) { await CopyOneAssetToProject(nodes[0], replaceExisting: null); return; }

        // A batch asks ONCE — a prompt per file is unusable on a large selection.
        int already = nodes.Count(HasProjectCopy);
        bool replaceExisting = false;
        if (already > 0)
        {
            if (PromptOwner is null) { _log.Info("Project", $"{already} of the selected asset(s) are already editable — skipping those."); }
            else
                replaceExisting = await Views.PromptWindow.ConfirmAsync(PromptOwner, "Replace Project Copies",
                    $"{already} of the {nodes.Count} selected asset(s) are already editable in the project.\n\n" +
                    $"Replace them with fresh copies of the ORIGINAL Riot files? Your edits in those files will be lost.\n\n" +
                    $"Cancel copies only the {nodes.Count - already} new one(s).", "Replace");
        }

        // The mount/tree rebuild is expensive, so it runs once for the whole batch, not per file.
        int copied = 0, skipped = 0;
        _copyBatch = true;
        try
        {
            foreach (var n in nodes)
                if (await CopyOneAssetToProject(n, replaceExisting)) copied++;
                else skipped++;
        }
        finally { _copyBatch = false; }

        Project.IsDirty = true;
        if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
        BuildMounts();
        BuildProjectTree();
        UpdateTitle();
        _log.Success("Project", $"Copied {copied} of {nodes.Count} asset(s) into the project"
                                + (skipped > 0 ? $" — {skipped} skipped (already editable, or no original found)." : "."));
    }

    /// <summary>True when this asset already has an editable copy on disk in the project.</summary>
    private bool HasProjectCopy(AssetNodeViewModel node)
    {
        if (node.Entry is not { } e || e.SourceKind == AssetSourceKind.RiotReference) return false;
        if (TryGetNodeFile(node, out var f) && File.Exists(f)) return true;
        return _overrides.TryGet(e.PathHash, out var ov) && File.Exists(ov.OverrideFile);
    }

    /// <summary>M107: set while a multi-asset copy runs — <see cref="FinishProjectCopy"/> then skips the
    /// per-file mount/tree rebuild, which the batch does once at the end instead.</summary>
    private bool _copyBatch;

    /// <param name="replaceExisting">null = ask (single-asset path); true/false = the batch already decided.</param>
    private async Task<bool> CopyOneAssetToProject(AssetNodeViewModel? srcNode, bool? replaceExisting)
    {
        var entry = srcNode?.Entry;
        if (entry is null) return false;

        // M98b: don't trust the node's SourceKind — deleting the project copy from the browser leaves the
        // mount index stale. Check whether the project copy actually EXISTS on disk; if it does, offer to
        // replace it with a fresh copy of the Riot original instead of refusing.
        if (entry.SourceKind != AssetSourceKind.RiotReference)
        {
            string? projectCopy = null;
            if (TryGetNodeFile(srcNode, out var nodeFile) && File.Exists(nodeFile)) projectCopy = nodeFile;
            else if (_overrides.TryGet(entry.PathHash, out var ov) && File.Exists(ov.OverrideFile)) projectCopy = ov.OverrideFile;

            if (projectCopy is not null)
            {
                if (replaceExisting == false) return false;   // batch chose to skip existing copies
                if (replaceExisting is null)
                {
                    if (PromptOwner is null) { _log.Info("Project", "Asset is already editable in the project."); return false; }
                    if (!await Views.PromptWindow.ConfirmAsync(PromptOwner, "Replace Project Copy",
                        $"'{entry.DisplayName}' is already editable in the project.\n\nReplace it with a fresh copy of the ORIGINAL Riot file? Your edits in this file will be lost.\n\n{projectCopy}", "Replace"))
                        return false;
                }
                var riot = ReadRiotOriginalBytes(entry);
                if (riot is null)
                { _log.Error("Project", $"{entry.DisplayName}: original Riot bytes not found (no reference WAD has this asset)."); return false; }
                try
                {
                    // M98d: a legacy hash-named override in a folder project MIGRATES to its real path
                    // on replace — the hash file and its record are removed.
                    bool isLegacyOverride = _overrides.TryGet(entry.PathHash, out var ovRec)
                        && string.Equals(ovRec.OverrideFile, projectCopy, StringComparison.OrdinalIgnoreCase);
                    if (isLegacyOverride && TryPlaceInProjectFolder(entry, riot, out var migrated))
                    {
                        _overrides.Remove(entry.PathHash);
                        try { File.Delete(projectCopy); } catch { }
                        FinishProjectCopy(entry, $"Migrated {entry.DisplayName} from the hash-named override to {migrated} (fresh Riot original, {riot.Length:n0} bytes).");
                        return true;
                    }
                    File.WriteAllBytes(projectCopy, riot);
                    Project.IsDirty = true;
                    RefreshOverrideMount();
                    BuildProjectTree();
                    UpdateTitle();
                    _log.Success("Project", $"Replaced project copy of {entry.DisplayName} with the Riot original ({riot.Length:n0} bytes).");
                }
                catch (Exception ex) { _log.Error("Project", $"{entry.DisplayName}: {ex.Message}"); return false; }
                return true;
            }

            // stale: the project copy is gone from disk — clean the dead override record and re-copy below
            if (_overrides.Has(entry.PathHash))
            {
                _overrides.Remove(entry.PathHash);
                _log.Info("Project", $"Stale override record for {entry.DisplayName} removed (file was deleted) — copying fresh.");
            }
        }

        try
        {
            // prefer the untouched Riot original as the copy source (the mounts may still serve stale bytes)
            var bytes = ReadRiotOriginalBytes(entry) ?? ReadAsset(entry.PathHash);

            // M98c: folder projects get the copy at its REAL path inside the per-WAD folder (cslol
            // layout — human-findable, editable, picked up by Build Package like any project file).
            // The hashed overrides dir remains only for single-WAD projects and unresolved chunks.
            if (TryPlaceInProjectFolder(entry, bytes, out var placed))
            {
                FinishProjectCopy(entry, $"Copied {entry.DisplayName} into the project at {placed} ({bytes.Length:n0} bytes). It is now editable.");
                return true;
            }

            var ext = Path.GetExtension(entry.IsResolved ? entry.Path : ".bin");
            var dest = ProjectWorkspace.StoreOverrideBytes(Project, entry.PathHash, bytes, string.IsNullOrEmpty(ext) ? ".bin" : ext);
            _overrides.Set(new ProjectAssetOverride
            {
                PathHash = entry.PathHash,
                ResolvedPath = entry.IsResolved ? entry.Path : null,
                OverrideFile = dest,
                AddedUtc = DateTime.UtcNow.ToString("o"),
            });
            Project.IsDirty = true;
            if (!_copyBatch)
            {
                RefreshOverrideMount();
                BuildProjectTree();
                if (_nodesByHash.TryGetValue(entry.PathHash, out var node)) SelectedNode = node;
                UpdateTitle();
            }
            _log.Success("Project", $"Copied {entry.DisplayName} into the project ({bytes.Length:n0} bytes). It is now editable.");
            return true;
        }
        catch (Exception ex) { _log.Error("Project", $"{entry.DisplayName}: {ex.Message}"); return false; }
    }

    /// <summary>M98c/d: the project-folder name a Riot asset should be staged under — the source WAD's
    /// base name (Map12.wad.client → "Map12"), or "Overrides" when the source WAD can't be determined.</summary>
    private string RiotWadFolderName(WadAssetEntry entry) => RiotWadFolderNameForHash(entry.PathHash);

    /// <summary>Which project folder does an asset belong in? The name of the Riot WAD it comes from
    /// (Map11.wad.client → "Map11"), so the packer puts it back into the same wad the game reads it from.
    /// "Overrides" when the asset has no Riot home at all.</summary>
    private string RiotWadFolderNameForHash(ulong pathHash)
    {
        if (_mounts is not null && _mounts.TryGet(pathHash, out var mounted))
        {
            var riotSrc = mounted.Source.Kind == AssetSourceKind.RiotReference ? mounted.Source
                : mounted.AllSources.FirstOrDefault(s => s.Kind == AssetSourceKind.RiotReference);
            if (riotSrc is not null)
            {
                var wadName = Path.GetFileName(riotSrc.Location);
                if (wadName.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase))
                    wadName = wadName[..^".wad.client".Length];
                foreach (var c in Path.GetInvalidFileNameChars()) wadName = wadName.Replace(c, '_');
                if (wadName.Length > 0) return wadName;
            }
        }
        return "Overrides";
    }

    /// <summary>M98c/d: write bytes to the asset's REAL path inside the per-WAD project folder
    /// (Map11.wad.client → Map11/data/…). False when this isn't a folder project or the path is
    /// unresolved — the caller falls back to the hashed override store.</summary>
    private bool TryPlaceInProjectFolder(WadAssetEntry entry, byte[] bytes, out string placedRelative)
    {
        placedRelative = "";
        if (!Project.IsFolderProject || !entry.IsResolved || Project.RootPath is null || _mounts is null) return false;

        string folderName = RiotWadFolderName(entry);
        string destFile = Path.Combine(Project.RootPath, folderName, entry.Path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
        File.WriteAllBytes(destFile, bytes);
        if (!Project.ProjectFolders.Contains(folderName, StringComparer.OrdinalIgnoreCase))
            Project.ProjectFolders.Add(folderName);
        placedRelative = $"{folderName}/{entry.Path}";
        return true;
    }

    /// <summary>M98c/d: shared bookkeeping after a folder-placement copy: persist, remount, reselect.</summary>
    private void FinishProjectCopy(WadAssetEntry entry, string successMessage)
    {
        Project.IsDirty = true;
        // M107: during a multi-asset copy the caller rebuilds once at the end.
        if (_copyBatch) { _log.Success("Project", successMessage); return; }
        if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
        BuildMounts();
        BuildProjectTree();
        if (_nodesByHash.TryGetValue(entry.PathHash, out var node)) SelectedNode = node;
        UpdateTitle();
        _log.Success("Project", successMessage);
    }

    /// <summary>M98c: when the asset's editable source is a real project-folder file, write edits to THAT
    /// file — creating a hashed override would shadow the folder copy and confuse everyone. False →
    /// caller falls back to the override store (single-WAD projects, unresolved chunks).</summary>
    private bool TryWriteToProjectFile(WadAssetEntry entry, byte[] bytes, out string file)
    {
        file = "";
        if (_mounts is null || !_mounts.TryGet(entry.PathHash, out var a)) return false;
        // M126: prefer the real project FILE over a shadow override. Overrides outrank folder files in
        // the mount order, so writing "the first editable source" kept updating the shadow while the
        // project file went stale — and the validator (reading files from disk) reported issues the
        // user had already fixed. The project file is the single source of truth; once it's written,
        // any shadow override of it is deleted so it can never mask an edit again.
        var sources = new[] { a.Source }.Concat(a.AllSources).Where(s => s is not null).Distinct().ToList();
        foreach (var kind in new[] { AssetSourceKind.ProjectFolder, AssetSourceKind.ProjectOverride })
            foreach (var src in sources)
            {
                if (src!.Kind != kind) continue;
                if (!src.TryGetFilePath(entry.PathHash, out file) || !File.Exists(file)) continue;
                File.WriteAllBytes(file, bytes);
                if (kind == AssetSourceKind.ProjectFolder) RemoveShadowOverride(entry, file);
                return true;
            }
        return false;
    }

    /// <summary>M126: dissolve a shadow override that duplicates a project file we just wrote in place.
    /// Rebuilds the mounts afterwards — the override mount indexes its directory, so the deleted file
    /// would otherwise still win reads for this hash. Handles record-less orphans too: the override
    /// mount is directory-scanned, so a shadow can exist with no entry in project.json.</summary>
    private void RemoveShadowOverride(WadAssetEntry entry, string projectFile)
    {
        var candidates = new List<string>();
        if (_overrides.TryGet(entry.PathHash, out var ov)) candidates.Add(ov.OverrideFile);
        try { candidates.Add(Path.Combine(ProjectWorkspace.OverridesDir(Project), $"{entry.PathHash:x16}.bin")); }
        catch { }

        bool removed = false;
        foreach (var f in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(f, projectFile, StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(f)) continue;
            try { File.Delete(f); removed = true; } catch { }
        }
        _overrides.Remove(entry.PathHash);
        if (!removed) return;
        Project.IsDirty = true;
        RefreshBrowser();
        _log.Info("Project", $"Removed the stale shadow override of {entry.DisplayName} — the project file is the single source of truth again.");
    }

    /// <summary>Block editing read-only Riot assets; suggest Copy to Project.</summary>
    private bool GuardEditable(WadAssetEntry? entry)
    {
        if (ProjectMode && entry is { SourceKind: AssetSourceKind.RiotReference })
        {
            _log.Warn("Project", $"'{entry.DisplayName}' is a read-only Riot asset. Right-click ▸ Copy Asset To Project to edit it.");
            return false;
        }
        return true;
    }

    // ---- M74: Explorer-style file operations (project folder mounts are real files on disk) ----

    /// <summary>The window that owns modal prompts (rename/confirm). Set by MainWindow.</summary>
    public Avalonia.Controls.Window? PromptOwner { get; set; }

    /// <summary>Re-scan the project's disk state (mounts are indexed once, so file ops re-run the scan).</summary>
    [RelayCommand]
    public void RefreshBrowser()
    {
        if (ProjectMode) { BuildMounts(); BuildProjectTree(); }
        else RebuildTree();
    }

    // ---- M100: auto-refresh — watch the project folder so external edits/adds/deletes show up ----
    private readonly List<FileSystemWatcher> _projectWatchers = new();
    private System.Threading.Timer? _watchDebounce;

    /// <summary>Watch the project root for file changes and refresh the browser automatically. Events are
    /// debounced (bulk copies fire hundreds) and marshalled to the UI thread.</summary>
    private void StartProjectWatchers()
    {
        StopProjectWatchers();
        if (!ProjectMode || Project.RootPath is null || !Directory.Exists(Project.RootPath)) return;
        try
        {
            var w = new FileSystemWatcher(Project.RootPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            FileSystemEventHandler onChange = (_, _) => ScheduleBrowserRefresh();
            w.Created += onChange; w.Deleted += onChange; w.Changed += onChange;
            w.Renamed += (_, _) => ScheduleBrowserRefresh();
            w.EnableRaisingEvents = true;
            _projectWatchers.Add(w);
        }
        catch { /* watching is a convenience — never block the project */ }
    }

    private void StopProjectWatchers()
    {
        foreach (var w in _projectWatchers) { try { w.EnableRaisingEvents = false; w.Dispose(); } catch { } }
        _projectWatchers.Clear();
    }

    private void ScheduleBrowserRefresh()
    {
        // .reyengine/ churn (project.json saves, reports) must not loop back into a refresh storm
        _watchDebounce?.Dispose();
        _watchDebounce = new System.Threading.Timer(_ =>
            Dispatcher.UIThread.Post(() =>
            {
                try { RefreshBrowser(); _log.Info("Files", "Project folder changed — browser refreshed."); }
                catch { }
            }), null, 600, System.Threading.Timeout.Infinite);
    }

    /// <summary>The real on-disk file behind a node (editable folder/override mounts only).</summary>
    private bool TryGetNodeFile(AssetNodeViewModel? node, out string filePath)
    {
        filePath = "";
        return node?.Entry is { ReadOnly: false } entry
            && _mounts is not null
            && _mounts.TryGetFilePath(entry.PathHash, out filePath, out _);
    }

    /// <summary>Map a Content Browser FOLDER node to its disk directory (editable FolderMounts only):
    /// climb to the mount subtree root under the "Project" group, then append the folder's path.</summary>
    private bool TryResolveFolderDiskDir(AssetNodeViewModel? folder, out string dir) =>
        TryComputeFolderDiskDir(folder, out dir) && Directory.Exists(dir);

    /// <summary>M113: map a folder node to its disk path under a project folder mount, whether or not it
    /// exists there yet. Walks ancestry NAMES instead of Model.FullPath, because the virtual material
    /// folders (ASSETS/… grafted from .materials.bin) have no Model — with the old check, creating a
    /// folder while standing in one silently fell back to the mount root.</summary>
    private bool TryComputeFolderDiskDir(AssetNodeViewModel? folder, out string dir)
    {
        dir = "";
        if (folder is not { IsFolder: true } || _mounts is null) return false;
        var parts = new List<string>();
        var node = folder;
        while (node.Parent is { } p && p.Parent is not null) { parts.Add(node.Name); node = p; }   // node = mount subtree root
        if (node.Parent is null || !string.Equals(node.Parent.Name, "Project", StringComparison.Ordinal)) return false;
        if (_mounts.Mounts.FirstOrDefault(m => m is FolderMount && m.Name == node.Name) is not FolderMount mount) return false;
        parts.Reverse();
        dir = parts.Count == 0 ? mount.Location : Path.Combine(mount.Location, Path.Combine(parts.ToArray()));
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanRenameAsset))]
    private async Task RenameAsset(AssetNodeViewModel? node)
    {
        if (node is null || PromptOwner is null) return;
        try
        {
            if (TryGetNodeFile(node, out var file))
            {
                var newName = await Views.PromptWindow.InputAsync(PromptOwner, "Rename",
                    $"Rename '{Path.GetFileName(file)}' — the asset's WAD path (and hash) changes with it.",
                    Path.GetFileName(file), "Rename");
                if (string.IsNullOrWhiteSpace(newName) || newName == Path.GetFileName(file)) return;
                var target = Path.Combine(Path.GetDirectoryName(file)!, newName.Trim());
                if (File.Exists(target)) { _log.Warn("Files", $"'{newName}' already exists here."); return; }
                File.Move(file, target);
                _log.Success("Files", $"Renamed {Path.GetFileName(file)} → {newName}.");
                RefreshBrowser();
            }
            else if (TryResolveFolderDiskDir(node, out var dir))
            {
                var newName = await Views.PromptWindow.InputAsync(PromptOwner, "Rename Folder",
                    $"Rename folder '{node.Name}' — every asset inside changes its WAD path (and hash).",
                    node.Name, "Rename");
                if (string.IsNullOrWhiteSpace(newName) || newName == node.Name) return;
                var target = Path.Combine(Path.GetDirectoryName(dir)!, newName.Trim());
                if (Directory.Exists(target)) { _log.Warn("Files", $"Folder '{newName}' already exists here."); return; }
                Directory.Move(dir, target);
                _log.Success("Files", $"Renamed folder {node.Name} → {newName}.");
                RefreshBrowser();
            }
            else _log.Warn("Files", "Only editable project files/folders can be renamed. Copy the asset to the project first.");
        }
        catch (Exception ex) { _log.Error("Files", ex.Message); }
    }

    // ---- M112: deleting like Explorer does ----
    // RemoveDirectory fails with ACCESS_DENIED when the directory — or anything inside it — carries the
    // ReadOnly attribute, which OneDrive-backed folders routinely do. Explorer strips it silently; .NET
    // does not, which is why "delete" failed here while Explorer succeeded on the same folder.

    private static void ClearReadOnly(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }
        catch { /* best effort — the delete below reports the real problem */ }
    }

    private static async Task ForceDeleteDirectoryAsync(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) ClearReadOnly(f);
        foreach (var d in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories)) ClearReadOnly(d);
        ClearReadOnly(dir);
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            // Cloud sync and virus scanners hold brief handles right after a write; one retry clears it.
            await Task.Delay(200);
            Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task ForceDeleteFileAsync(string file)
    {
        ClearReadOnly(file);
        try { File.Delete(file); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            await Task.Delay(200);
            File.Delete(file);
        }
    }

    /// <summary>Explain a delete failure in terms the user can act on.</summary>
    private string DeleteFailureHint(string path, Exception ex) =>
        ex is UnauthorizedAccessException or IOException
            ? $"{Path.GetFileName(path)}: {ex.Message} — it may be open in another program, or OneDrive/antivirus is holding it. Close it and try again."
            : $"{Path.GetFileName(path)}: {ex.Message}";

    [RelayCommand(CanExecute = nameof(CanDeleteAsset))]
    private async Task DeleteAsset(AssetNodeViewModel? node)
    {
        if (node is null || PromptOwner is null) return;
        try
        {
            if (node.Entry is { SourceKind: AssetSourceKind.ProjectOverride } ov)
            {
                if (!await Views.PromptWindow.ConfirmAsync(PromptOwner, "Delete Override",
                    $"Delete the project override for '{ov.DisplayName}'? The asset reverts to its original source.", "Delete"))
                    return;
                RevertSelectedFor(node);
                return;
            }
            if (TryGetNodeFile(node, out var file))
            {
                if (!await Views.PromptWindow.ConfirmAsync(PromptOwner, "Delete File",
                    $"Permanently delete '{Path.GetFileName(file)}' from the project folder?\n\n{file}", "Delete"))
                    return;
                await ForceDeleteFileAsync(file);
                _log.Success("Files", $"Deleted {Path.GetFileName(file)}.");
                RefreshBrowser();
            }
            else if (TryResolveFolderDiskDir(node, out var dir))
            {
                int n = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count();
                if (!await Views.PromptWindow.ConfirmAsync(PromptOwner, "Delete Folder",
                    $"Permanently delete folder '{node.Name}' and the {n:n0} file(s) inside?\n\n{dir}", "Delete"))
                    return;
                await ForceDeleteDirectoryAsync(dir);
                _log.Success("Files", $"Deleted folder {node.Name} ({n:n0} file(s)).");
                RefreshBrowser();
            }
            else _log.Warn("Files", "Only editable project files/folders can be deleted. Riot references are read-only.");
        }
        catch (Exception ex) { _log.Error("Files", DeleteFailureHint(node.Name, ex)); }
    }

    /// <summary>Revert a specific node's override (Delete on an override = revert to original).</summary>
    private void RevertSelectedFor(AssetNodeViewModel node)
    {
        // M100: point the command at this node without re-selecting it (SelectedNode reloads the preview).
        _contextOverride = node;
        try { if (RevertSelectedCommand.CanExecute(null)) RevertSelectedCommand.Execute(null); }
        finally { _contextOverride = null; }
    }

    /// <summary>M74: open any asset's raw bytes in the system text editor. Editable files open in place
    /// (external saves show up after a browser refresh); read-only assets open as a temp copy.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenInTextEditor))]
    private void OpenInTextEditor(AssetNodeViewModel? node)
    {
        if (node?.Entry is not { } entry) return;
        try
        {
            string file;
            if (TryGetNodeFile(node, out var real)) file = real;
            else
            {
                var bytes = GetAssetBytes(entry);
                if (bytes is null) { _log.Warn("Files", "Asset bytes not available."); return; }
                var dir = Path.Combine(Path.GetTempPath(), "ReyEngine", "TextView");
                Directory.CreateDirectory(dir);
                file = Path.Combine(dir, entry.DisplayName);
                File.WriteAllBytes(file, bytes);
                _log.Info("Files", $"'{entry.DisplayName}' is read-only — opened a temporary copy.");
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{file}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { _log.Error("Files", ex.Message); }
    }

    /// <summary>M74: show the asset's real file in Windows Explorer (editable file-backed assets).</summary>
    [RelayCommand(CanExecute = nameof(CanShowInExplorer))]
    private void ShowInExplorer(AssetNodeViewModel? node)
    {
        try
        {
            if (TryGetNodeFile(node, out var file))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
            else if (TryResolveFolderDiskDir(node, out var dir))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            else _log.Warn("Files", "This asset has no standalone file on disk (it lives inside a WAD archive).");
        }
        catch (Exception ex) { _log.Error("Files", ex.Message); }
    }

    /// <summary>M74: move an editable file node into a Content Browser folder (internal drag & drop).</summary>
    public void MoveAssetToFolder(AssetNodeViewModel item, AssetNodeViewModel targetFolder)
    {
        try
        {
            if (!TryGetNodeFile(item, out var file))
            { _log.Warn("Files", "Only editable project files can be moved. Copy the asset to the project first."); return; }
            if (!TryResolveFolderDiskDir(targetFolder, out var dir))
            { _log.Warn("Files", "Drop target must be a folder inside an editable project folder."); return; }
            var target = Path.Combine(dir, Path.GetFileName(file));
            if (string.Equals(target, file, StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(target)) { _log.Warn("Files", $"'{Path.GetFileName(file)}' already exists in that folder."); return; }
            File.Move(file, target);
            _log.Success("Files", $"Moved {Path.GetFileName(file)} → {targetFolder.Name}/ (its WAD path changed with it).");
            RefreshBrowser();
        }
        catch (Exception ex) { _log.Error("Files", ex.Message); }
    }

    /// <summary>M74: import external files (Explorer drag-drop) into a Content Browser folder.</summary>
    public void ImportExternalFiles(IReadOnlyList<string> files, AssetNodeViewModel? targetFolder)
    {
        if (!TryResolveFolderDiskDir(targetFolder, out var dir))
        { _log.Warn("Files", "Drop files onto a folder inside an editable project folder (e.g. one of your extracted WAD folders)."); return; }
        ImportExternalFilesTo(files, dir);
    }

    /// <summary>M109: import into a resolved directory (the Import command already picked the target).</summary>
    public void ImportExternalFilesTo(IReadOnlyList<string> files, string dir)
    {
        int copied = 0;
        foreach (var f in files)
        {
            try
            {
                if (File.Exists(f)) { File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), overwrite: true); copied++; }
                else if (Directory.Exists(f))
                {
                    foreach (var sub in Directory.EnumerateFiles(f, "*", SearchOption.AllDirectories))
                    {
                        var rel = Path.GetRelativePath(f, sub);
                        var target = Path.Combine(dir, Path.GetFileName(f), rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(sub, target, overwrite: true);
                        copied++;
                    }
                }
            }
            catch (Exception ex) { _log.Warn("Files", $"{Path.GetFileName(f)}: {ex.Message}"); }
        }
        if (copied > 0)
        {
            _log.Success("Files", $"Imported {copied} file(s) into {dir}");
            RefreshBrowser();
        }
    }

    // ---- M100: bulk operations on the Content Browser selection ---------
    // Single click selects, double click opens — so the context menu and the toolbar act on the
    // browser's SelectedItems rather than on "whatever happens to be open in the editor".

    /// <summary>Forces <see cref="ContextNode"/> for the duration of one internal call (see
    /// <see cref="RevertSelectedFor"/>, which drives a command against a specific node).</summary>
    private AssetNodeViewModel? _contextOverride;

    /// <summary>The node the single-asset commands act on: the Content Browser selection when there
    /// is one (right-clicking a tile selects it), otherwise whatever is open in the editor.</summary>
    private AssetNodeViewModel? ContextNode =>
        _contextOverride ?? (ContentBrowser.SelectedItems.Count > 0 ? ContentBrowser.SelectedItems[0] : SelectedNode);

    /// <summary>Every node a bulk operation should touch.</summary>
    private List<AssetNodeViewModel> ContextNodes =>
        ContentBrowser.SelectedItems.Count > 0
            ? ContentBrowser.SelectedItems.ToList()
            : SelectedNode is { } n ? new List<AssetNodeViewModel> { n } : new List<AssetNodeViewModel>();

    // ---- M108: context-menu gating ----
    // Every asset command declares when it applies, so the menu greys out what can't work here
    // instead of accepting the click and logging a refusal.

    /// <summary>Re-query the selection-dependent commands (called whenever the selection or folder changes).</summary>
    private void RaiseAssetCommandsCanExecute()
    {
        CopyAssetToProjectCommand.NotifyCanExecuteChanged();
        ReplaceSelectedCommand.NotifyCanExecuteChanged();
        RevertSelectedCommand.NotifyCanExecuteChanged();
        CopySelectionToCommand.NotifyCanExecuteChanged();
        MoveSelectionToCommand.NotifyCanExecuteChanged();
        DeleteSelectionCommand.NotifyCanExecuteChanged();
        ExportSelectedCommand.NotifyCanExecuteChanged();
        CopyResolvedPathCommand.NotifyCanExecuteChanged();
        CopyHashCommand.NotifyCanExecuteChanged();
        ImportFilesCommand.NotifyCanExecuteChanged();
        NewFolderCommand.NotifyCanExecuteChanged();
    }

    /// <summary>A file on disk we're allowed to move/rename/delete.</summary>
    private bool IsEditableFile(AssetNodeViewModel? n) => n is not null && TryGetNodeFile(n, out _);
    private bool IsEditableFolder(AssetNodeViewModel? n) => n is not null && TryResolveFolderDiskDir(n, out _);
    private bool IsOverride(AssetNodeViewModel? n) => n?.Entry is { SourceKind: AssetSourceKind.ProjectOverride };

    private bool CanCopyAssetToProject() => ProjectMode && _mounts is not null && ContextNodes.Any(n => n.Entry is not null);
    private bool CanReplaceSelected() => ProjectMode && ContextNode?.Entry is not null;
    private bool CanRevertSelected() => ContextNode?.Entry is { } e && _overrides.Has(e.PathHash);
    private bool CanCopySelectionTo() => ContextNodes.Any(n => !n.IsFolder && n.Entry is not null);
    private bool CanMoveSelectionTo() => ContextNodes.Any(IsEditableFile);
    private bool CanDeleteSelection() => ContextNodes.Any(n => IsEditableFile(n) || IsEditableFolder(n) || IsOverride(n));
    private bool CanExportSelected() => ContentLoaded && ContextNode?.Entry is not null;
    private bool CanCopyEntryText() => ContextNode?.Entry is not null;
    /// <summary>M109: enabled anywhere in the project — the command resolves a writable target itself.</summary>
    private bool CanImportFiles(AssetNodeViewModel? target) =>
        ProjectMode && (TryComputeFolderDiskDir(target, out _) || ContentBrowser.CanImportHere || ProjectFolderMounts.Count > 0);

    private bool CanRenameAsset(AssetNodeViewModel? node) => IsEditableFile(node) || IsEditableFolder(node);
    private bool CanDeleteAsset(AssetNodeViewModel? node) => IsEditableFile(node) || IsEditableFolder(node) || IsOverride(node);
    private bool CanShowInExplorer(AssetNodeViewModel? node) => IsEditableFile(node) || IsEditableFolder(node);
    private bool CanOpenInTextEditor(AssetNodeViewModel? node) => node?.Entry is not null;
    private bool CanOpenInMapBinEditor(AssetNodeViewModel? node) =>
        node?.Entry is { } e && e.DisplayName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);

    /// <summary>M197 (4.5): deliberately NOT gated on a ".bin" suffix, unlike the Map Bin Editor's command.
    /// 207 of the map VFX bins this exists to reach sit at extensionless paths
    /// (maps/modespecificdata/&lt;mode&gt;/&lt;spell&gt;/loadable), so a suffix test would hide exactly the
    /// assets the milestone is for. A non-VFX pick falls through to the existing "contains no VFX systems"
    /// warning, which is a cheap and clear failure.</summary>
    private bool CanOpenInParticleEditor(AssetNodeViewModel? node) => node?.Entry is not null;

    /// <summary>Editable folder mounts in the open project (the places we're allowed to write).</summary>
    private List<FolderMount> ProjectFolderMounts =>
        _mounts?.Mounts.OfType<FolderMount>().ToList() ?? new List<FolderMount>();

    /// <summary>
    /// M109: where Import / New Folder should write. The folder in view when it's writable, otherwise a
    /// project folder mount — so both work from anywhere in the project, including the tree root and
    /// while browsing read-only Riot References, instead of only deep inside a mount.
    /// </summary>
    /// <param name="target">The folder the user pointed at (a right-clicked tree node). Right-clicking
    /// in the TreeView doesn't select, so without this the command only ever saw the browser's current
    /// folder and silently created things in the mount root instead.</param>
    private async Task<string?> ResolveWriteTargetAsync(string action, AssetNodeViewModel? target = null)
    {
        // M113: materialize the directory when it only exists virtually so far (ASSETS/… from a
        // .materials.bin) — Explorer semantics: creating inside a path makes that path real.
        if (TryComputeFolderDiskDir(target, out var picked))
        { try { Directory.CreateDirectory(picked); return picked; } catch (Exception ex) { _log.Error("Files", ex.Message); return null; } }
        if (TryComputeFolderDiskDir(ContentBrowser.CurrentFolder, out var here))
        { try { Directory.CreateDirectory(here); return here; } catch (Exception ex) { _log.Error("Files", ex.Message); return null; } }

        var folders = ProjectFolderMounts;
        if (folders.Count == 0)
        {
            _log.Warn("Files", $"{action} needs an editable project folder — this project has none. "
                             + "Create a folder project, or use Copy Asset To Project to make one editable first.");
            return null;
        }
        if (folders.Count == 1)
        {
            _log.Info("Files", $"Not inside a writable folder — using {folders[0].Name}/.");
            return folders[0].Location;
        }
        if (PromptOwner is null) return folders[0].Location;

        // Several folder mounts and no obvious one: let the user say which rather than guessing, since
        // the folder becomes part of the asset's WAD path.
        var names = string.Join(", ", folders.Select(f => f.Name));
        var pick = await Views.PromptWindow.InputAsync(PromptOwner, $"{action} — choose a folder",
            $"You're not inside a writable project folder, so pick which one to use.\n\nAvailable: {names}",
            folders[0].Name, "Use");
        if (string.IsNullOrWhiteSpace(pick)) return null;
        var m = folders.FirstOrDefault(f => string.Equals(f.Name, pick.Trim(), StringComparison.OrdinalIgnoreCase));
        if (m is null) { _log.Warn("Files", $"'{pick.Trim()}' isn't one of: {names}"); return null; }
        return m.Location;
    }

    /// <summary>M108: create a subfolder in the project folder the browser is showing.</summary>
    [RelayCommand(CanExecute = nameof(CanImportFiles))]
    private async Task NewFolder(AssetNodeViewModel? target)
    {
        if (PromptOwner is null) return;
        if (await ResolveWriteTargetAsync("New Folder", target) is not { } parent) return;
        var name = await Views.PromptWindow.InputAsync(PromptOwner, "New Folder",
            $"Create a folder inside:\n{parent}\n\nIt becomes part of the asset's WAD path, so name it the way the game expects.",
            "NewFolder", "Create");
        if (string.IsNullOrWhiteSpace(name)) return;

        // Keep it a single folder name — a path here would silently create a tree somewhere else.
        name = name.Trim();
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        { _log.Warn("Files", $"'{name}' contains characters a folder name can't have."); return; }

        var created = Path.Combine(parent, name);
        if (Directory.Exists(created)) { _log.Warn("Files", $"'{name}' already exists here."); return; }
        try
        {
            Directory.CreateDirectory(created);
            _log.Success("Files", $"Created {created}");
            RefreshBrowser();
        }
        catch (Exception ex) { _log.Error("Files", ex.Message); }
    }

    /// <summary>Import external files into the folder the browser is showing.</summary>
    [RelayCommand(CanExecute = nameof(CanImportFiles))]
    private async Task ImportFiles(AssetNodeViewModel? target)
    {
        if (await ResolveWriteTargetAsync("Import", target) is not { } into) return;
        var files = await Dialogs.OpenFilesAsync("Import files into the project", DialogService.All);
        if (files.Count > 0) ImportExternalFilesTo(files, into);
    }

    /// <summary>Copy the selected assets out to a folder on disk. Works for read-only Riot references
    /// too (the bytes are read through the mounts), so it doubles as a bulk export.</summary>
    [RelayCommand(CanExecute = nameof(CanCopySelectionTo))]
    private async Task CopySelectionTo()
    {
        var nodes = ContextNodes.Where(n => !n.IsFolder && n.Entry is not null).ToList();
        if (nodes.Count == 0) { _log.Warn("Files", "Select one or more files first."); return; }
        var dir = await Dialogs.OpenFolderAsync($"Copy {nodes.Count} asset(s) to…");
        if (dir is null) return;
        int done = 0;
        foreach (var n in nodes)
        {
            try
            {
                File.WriteAllBytes(Path.Combine(dir, n.Entry!.DisplayName), GetAssetBytes(n.Entry));
                done++;
            }
            catch (Exception ex) { _log.Warn("Files", $"{n.Name}: {ex.Message}"); }
        }
        _log.Success("Files", $"Copied {done}/{nodes.Count} asset(s) → {dir}");
    }

    /// <summary>Move the selected project files to another folder. Read-only references can't move —
    /// copy them into the project first.</summary>
    [RelayCommand(CanExecute = nameof(CanMoveSelectionTo))]
    private async Task MoveSelectionTo()
    {
        var nodes = ContextNodes.Where(n => !n.IsFolder).ToList();
        if (nodes.Count == 0) { _log.Warn("Files", "Select one or more files first."); return; }
        var dir = await Dialogs.OpenFolderAsync($"Move {nodes.Count} file(s) to…");
        if (dir is null) return;
        int done = 0, skipped = 0;
        foreach (var n in nodes)
        {
            if (!TryGetNodeFile(n, out var file)) { skipped++; continue; }
            try
            {
                var target = Path.Combine(dir, Path.GetFileName(file));
                if (string.Equals(target, file, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(target)) { _log.Warn("Files", $"'{Path.GetFileName(file)}' already exists there — skipped."); skipped++; continue; }
                File.Move(file, target);
                done++;
            }
            catch (Exception ex) { _log.Warn("Files", $"{n.Name}: {ex.Message}"); skipped++; }
        }
        if (skipped > 0) _log.Warn("Files", $"{skipped} item(s) skipped — only editable project files can be moved.");
        if (done > 0) { _log.Success("Files", $"Moved {done} file(s) → {dir} (their WAD paths changed with them)."); RefreshBrowser(); }
    }

    /// <summary>Delete every selected asset — one confirmation for the whole batch. Overrides revert
    /// to their original instead of being removed from disk.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteSelection))]
    private async Task DeleteSelection()
    {
        var nodes = ContextNodes;
        if (nodes.Count == 0 || PromptOwner is null) { _log.Warn("Files", "Select something to delete first."); return; }
        if (nodes.Count == 1) { await DeleteAsset(nodes[0]); return; }   // single item keeps the detailed prompt

        if (!await Views.PromptWindow.ConfirmAsync(PromptOwner, "Delete Selection",
            $"Delete {nodes.Count} selected item(s)?\n\nProject files are removed from disk; overrides revert to their original.",
            "Delete"))
            return;

        int deleted = 0, reverted = 0, skipped = 0;
        foreach (var node in nodes)
        {
            try
            {
                if (node.Entry is { SourceKind: AssetSourceKind.ProjectOverride }) { RevertSelectedFor(node); reverted++; }
                else if (TryGetNodeFile(node, out var file)) { await ForceDeleteFileAsync(file); deleted++; }
                else if (TryResolveFolderDiskDir(node, out var dir)) { await ForceDeleteDirectoryAsync(dir); deleted++; }
                else skipped++;
            }
            catch (Exception ex) { _log.Warn("Files", DeleteFailureHint(node.Name, ex)); skipped++; }
        }
        if (skipped > 0) _log.Warn("Files", $"{skipped} item(s) skipped — Riot references are read-only.");
        _log.Success("Files", $"Deleted {deleted} item(s){(reverted > 0 ? $", reverted {reverted} override(s)" : "")}.");
        RefreshBrowser();
    }

    [RelayCommand]
    private async Task BuildProject()
    {
        if (_mounts is null || Project.RootPath is null) { _log.Warn("Build", "Open a project folder first."); return; }
        var buildRoot = Project.OutputDirectory ?? Path.Combine(Project.RootPath, "Build");
        if (BuildSafety.IsInsideGameInstall(buildRoot))
        { _log.Error("Build", "Refusing to build into a Riot/League install folder. Change the output directory in Project Settings."); return; }

        _overrides.SaveTo(Project);
        ReyProjectService.Save(Project, Project.ProjectFilePath!);
        Directory.CreateDirectory(buildRoot);
        _log.Info("Build", $"Building project '{Project.Name}' → {buildRoot}");
        IsBuilding = true; Status = "Building project…";
        var progress = BuildProgressSink();
        try
        {
            await Task.Run(() => BuildProjectCore(buildRoot, progress));
            Status = $"Built project to {buildRoot}";
            _log.Success("Build", $"Project build ready: {buildRoot}. Open it via File ▸ Open Project Folder to verify.");
        }
        catch (Exception ex) { _log.Error("Build", ex.Message); }
        finally { IsBuilding = false; }
    }

    /// <summary>M131: build the project into <paramref name="buildRoot"/>. Returns the wads this
    /// build produced — callers must bundle exactly these, never "whatever sits in the folder".</summary>
    private List<string> BuildProjectCore(string buildRoot, IProgress<(double Frac, string Stage)>? progress = null)
    {
        var overridesByHash = _overrides.All.ToDictionary(o => o.PathHash, o => o.OverrideFile);
        int wads = 0, staged = 0, files = 0, skipped = 0;
        var produced = new List<string>();

        // M131: a fresh build starts from a CLEAN slate — leftover staging from earlier builds kept
        // files the project has since deleted or renamed, and they leaked into every later package.
        var stagingRoot = Path.Combine(buildRoot, "staged");
        progress?.Report((0.02, "Cleaning old staging…"));
        if (Directory.Exists(stagingRoot))
        {
            try { Directory.Delete(stagingRoot, recursive: true); }
            catch (Exception ex) { _log.Warn("Build", $"Could not fully clean {stagingRoot}: {ex.Message} — close programs holding files there."); }
        }

        // Project WADs: safe in-place replace of existing chunks.
        foreach (var w in Project.ProjectWads)
        {
            var src = Project.ResolveProjectPath(w);
            if (!File.Exists(src)) { _log.Warn("Build", $"missing project WAD {w}"); continue; }
            progress?.Report((0.05, $"Repacking {Path.GetFileName(w)}…"));
            using var arc = WadArchive.Open(src, _resolver);
            var apply = new Dictionary<ulong, byte[]>();
            foreach (var (hash, file) in overridesByHash)
                if (arc.TryGetEntry(hash, out _) && File.Exists(file)) apply[hash] = File.ReadAllBytes(file);
            var outWad = Path.Combine(buildRoot, Path.GetFileName(w));
            var report = new BuildReport { OutputPath = outWad };
            WadRepackService.Repack(src, apply, outWad, report);
            foreach (var i in report.Issues) _log.Warn("Build", i.Message);
            _log.Info("Build", $"WAD {Path.GetFileName(w)}: {apply.Count} replaced → {Path.GetFileName(outWad)}");
            produced.Add(outWad);
            wads++;
        }

        // Project folders: stage (copy tree + apply overrides as files — new files are safe in folder format).
        var stagedFolders = new List<(string name, string dir)>();
        int folderIdx = 0;
        foreach (var f in Project.ProjectFolders)
        {
            var srcFolder = Project.ResolveProjectPath(f);
            var name = f == "." ? Project.Name : f.Replace('/', '_');
            var outFolder = Path.Combine(stagingRoot, name);
            folderIdx++;
            int idx = folderIdx;
            files += CopyTree(srcFolder, outFolder, buildRoot,
                n => progress?.Report((0.05 + 0.25 * (idx - 1 + Math.Min(1.0, n / 15000.0)) / Project.ProjectFolders.Count,
                    $"Staging {name}… ({n:n0} files)")));
            stagedFolders.Add((name, outFolder));
            staged++;
        }

        // Apply overrides into the first staged folder at their resolved path.
        // M131: the project FILE is the single source of truth (M126) — an override must never
        // clobber a file the project actually ships. Only overrides for paths the folder does NOT
        // provide (wad-source assets copied to the workspace) belong in the package.
        if (stagedFolders.Count > 0)
        {
            var outFolder = stagedFolders[0].dir;
            foreach (var ov in _overrides.All)
            {
                if (!File.Exists(ov.OverrideFile)) { skipped++; continue; }
                string rel = ov.ResolvedPath ?? $"0x{ov.PathHash:x16}{Path.GetExtension(ov.OverrideFile)}";
                var dest = Path.Combine(outFolder, rel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(dest))
                {
                    _log.Warn("Build", $"Stale shadow override IGNORED for {rel} — the project file wins. Save that asset once in ReyEngine to dissolve the shadow.");
                    skipped++;
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(ov.OverrideFile, dest, overwrite: false);
                files++;
            }
        }

        // Pack each staged folder into a distributable .wad.client.
        int packed = 0;
        int packIdx = 0;
        foreach (var (name, dir) in stagedFolders)
        {
            var outWad = Path.Combine(buildRoot, name + ".wad.client");
            packIdx++;
            int idx = packIdx;
            var packProgress = new Progress<float>(fr => progress?.Report(
                (0.30 + 0.65 * (idx - 1 + fr) / stagedFolders.Count, $"Packing {name}.wad.client… {fr:P0}")));
            WadPackReport pr;
            try { pr = WadPackService.Pack(dir, outWad, packProgress, knownTypesOnly: Project.PackKnownTypesOnly); }
            catch (Exception ex) { _log.Error("Build", $"Pack failed for {name}: {ex.Message}"); continue; }
            foreach (var w in pr.Warnings) _log.Warn("Build", w);
            if (pr.CleanedUnknown.Count > 0)   // M132
            {
                _log.Info("Build", $"{name}: cleaned {pr.CleanedUnknown.Count} unknown file(s) from the package (not game formats):");
                foreach (var c in pr.CleanedUnknown.Take(10)) _log.Info("Build", $"   skipped {c}");
                if (pr.CleanedUnknown.Count > 10) _log.Info("Build", $"   … {pr.CleanedUnknown.Count - 10} more");
            }
            if (pr.Success)
            {
                packed++;
                produced.Add(outWad);
                _log.Success("Build", $"Packed {name}.wad.client — {pr.Chunks:n0} chunks, {pr.InputBytes / 1048576.0:0.0}→{pr.OutputBytes / 1048576.0:0.0} MB. {pr.Validation}");
            }
            else _log.Error("Build", $"Pack didn't validate for {name} — the staged folder is at {dir}.");
        }

        // M131: stale wads from renamed/removed folders must not linger next to fresh output —
        // the fantome export used to bundle every *.wad.client it found.
        foreach (var w in Directory.GetFiles(buildRoot, "*.wad.client"))
            if (!produced.Contains(w, StringComparer.OrdinalIgnoreCase))
            {
                try { File.Delete(w); _log.Info("Build", $"Removed stale build output {Path.GetFileName(w)} (not part of this project anymore)."); }
                catch (Exception ex) { _log.Warn("Build", $"Stale {Path.GetFileName(w)} could not be removed: {ex.Message}"); }
            }

        progress?.Report((1.0, "Build finished."));
        _log.Info("Build", $"project WADs: {wads} · folders packed: {packed}/{staged} · files: {files:n0} · skipped: {skipped}");
        return produced;
    }

    private static int CopyTree(string src, string dst, string excludedRoot, Action<int>? onProgress = null)
    {
        int n = 0;
        string fullDst = Path.GetFullPath(dst);
        string fullExcluded = Path.GetFullPath(excludedRoot);
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(src));

        while (pending.Count > 0)
        {
            string dir = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(dir))
            {
                string fullChild = Path.GetFullPath(child);
                if (Path.GetFileName(child).Equals(".reyengine", StringComparison.OrdinalIgnoreCase)
                    || IsSameOrChild(fullChild, fullDst)
                    || IsSameOrChild(fullChild, fullExcluded))
                    continue;
                pending.Push(fullChild);
            }

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var rel = Path.GetRelativePath(src, file);
                var dest = Path.Combine(dst, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
                n++;
                if (n % 250 == 0) onProgress?.Invoke(n);
            }
        }
        onProgress?.Invoke(n);
        return n;
    }

    /// <summary>Upload only the mapgeo ranges affected by the current edit.</summary>
    public void UpdateDx11EditedMeshVertices(ReyEngine.Rendering.D3D11.ShaderPreviewRenderer renderer)
    {
        if (_currentMap is not { } map) return;
        if (_selection.Count == 0)
        {
            renderer.UpdateMeshVertices(map.Positions, map.Normals, 0, map.VertexCount);
            return;
        }
        foreach (var mesh in _selection.Items)
            renderer.UpdateMeshVertices(map.Positions, map.Normals, mesh.VertexStart, mesh.VertexCount);
    }

    private static bool IsSameOrChild(string path, string root)
    {
        string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return fullPath.Equals(fullRoot, comparison)
            || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
    }

    [RelayCommand]
    private async Task BuildPackage()
    {
        if (ProjectMode) { await BuildProject(); return; }
        if (!await EnsureProjectSavedAsync()) return;
        _overrides.SaveTo(Project);
        ReyProjectService.Save(Project, Project.ProjectFilePath!);

        string buildDir;
        try { buildDir = ProjectWorkspace.BuildDir(Project); }
        catch (Exception ex) { _log.Error("Build", ex.Message); return; }
        var outPath = Path.Combine(buildDir, Path.GetFileName(Project.SourceWadPath!));

        if (BuildSafety.IsInsideGameInstall(outPath))
        {
            _log.Error("Build", "Refusing to write the build into a Riot/League install folder. Change the project output directory.");
            return;
        }

        _log.Info("Build", $"Building '{Project.Name}' → {outPath}");
        if (_overrides.Count == 0) _log.Warn("Build", "No overrides — output will mirror the source WAD.");
        IsBuilding = true;
        Status = "Building package…";
        var sink = BuildProgressSink();
        var wadProgress = new Progress<float>(f => sink.Report((f, $"Repacking {Path.GetFileName(outPath)}… {f:P0}")));
        try
        {
            var report = await Task.Run(() => BuildPackageService.Build(Project, outPath, wadProgress, CancellationToken.None));
            LogBuildReport(report);
            Status = report.Success
                ? $"Built {Path.GetFileName(outPath)} — {report.OutputSize / 1024.0 / 1024.0:0.0} MB in {report.Duration.TotalSeconds:0.0}s"
                : "Build failed — see console.";
        }
        catch (Exception ex) { _log.Error("Build", ex.Message); }
        finally { IsBuilding = false; }
    }

    private void LogBuildReport(BuildReport r)
    {
        _log.Info("Build", $"chunks: {r.ChunksTotal:n0} total · {r.ChunksReplaced} replaced · {r.ChunksCopied:n0} copied · {r.ChunksFailed} failed");
        foreach (var issue in r.Issues)
        {
            switch (issue.Severity)
            {
                case BuildSeverity.Error: _log.Error("Build", issue.Message); break;
                case BuildSeverity.Warning: _log.Warn("Build", issue.Message); break;
                default: _log.Info("Build", issue.Message); break;
            }
        }
        if (!string.IsNullOrEmpty(r.Validation)) _log.Info("Build", r.Validation);
        if (r.Success) _log.Success("Build", $"Output ready: {r.OutputPath}  ({r.OutputSize / 1024.0 / 1024.0:0.0} MB). Open it via File ▸ Open WAD to verify.");
    }

    [RelayCommand]
    private void OpenBuildFolder()
    {
        try
        {
            var dir = ProjectWorkspace.BuildDir(Project);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex) { _log.Warn("Build", ex.Message); }
    }

    private void UpdateTitle()
    {
        var name = string.IsNullOrEmpty(Project.Name) ? "Untitled" : Project.Name;
        bool dirty = Project.IsDirty || UndoService.IsDirty;
        Title = $"ReyEngine — {name}{(dirty ? " *" : "")}" + (_archive is not null ? $" — {_archive.Name}" : "");
    }

    // ---- Misc commands --------------------------------------------------

    /// <summary>M210: the experimental DX11 shader preview. Isolated on purpose - it loads Riot's own
    /// compiled shaders on a real Direct3D 11 device to find out whether that route is viable, and applies
    /// nothing it learns to a map. Integration into the Material Editor is a later decision that depends on
    /// what this turns up.</summary>
    [RelayCommand]
    private void ShaderPreview()
    {
        string? dir = string.IsNullOrEmpty(Project.GameDirectory) ? null
            : Path.Combine(Project.GameDirectory, "DATA", "FINAL");

        // M213: hand the window the asset mounts so a real material can be loaded end to end - its
        // textures come out of the same VFS everything else reads, so project overrides win over Riot's
        // originals exactly as they do in the rest of the editor.
        var bins = AssetEntries
            .Where(e => e.IsResolved && e.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            .Select(e => (e.Path, e.PathHash))
            .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // M214: .skn and .mapgeo, so a whole character or map can be drawn with its real materials
        var scenes = AssetEntries
            .Where(e => e.IsResolved
                        && (e.Path.EndsWith(".skn", StringComparison.OrdinalIgnoreCase)
                            || e.Path.EndsWith(".mapgeo", StringComparison.OrdinalIgnoreCase)))
            .Select(e => (e.Path, e.PathHash))
            .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var vm = new ShaderPreviewViewModel(dir, _resolver.Database,
            readAsset: h => { try { return ReadAsset(h); } catch { return null; } },
            binAssets: bins,
            resolveBinName: h => _resolver.Database.TryGetBinName(h, out var n) ? n : null,
            sceneAssets: scenes);

        if (bins.Count == 0)
            _log.Info("Shader", "No .bin assets are mounted, so the Material tab will be empty. "
                                + "Open a project or a WAD first.");
        if (!vm.CacheAvailable)
            _log.Warn("Shader", "No shader cache found - the window opens, but there is nothing to load. "
                                + "Point the project at the game folder (the one containing DATA/FINAL).");

        var win = new Views.ShaderPreviewWindow { DataContext = vm };
        if (PromptOwner is not null) win.Show(PromptOwner); else win.Show();
        _log.Info("Shader", "DX11 Shader Preview opened (experimental).");
    }
    [RelayCommand] private void ClearConsole() => Console.Clear();

    [RelayCommand]
    private void Exit() =>
        (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
}

/// <summary>
/// M152: one editable Light.dat point light. Position/colour/radius are individually bindable so the
/// inspector can edit them, and every change republishes the render list so the viewport updates live.
/// Colour is edited as 0..255 (how the file stores it) while PointLight carries linear 0..1.
/// </summary>
public sealed partial class PointLightViewModel : ObservableObject
{
    private readonly MainWindowViewModel _owner;
    private bool _loading = true;

    [ObservableProperty] private double _x, _y, _z;
    [ObservableProperty] private double _r, _g, _b;      // 0..255, matching the file
    [ObservableProperty] private double _radius;
    [ObservableProperty] private double _intensity = 1;  // M153: this light's OWN strength
    [ObservableProperty] private string _name = "Light";

    public PointLightViewModel(PointLight light, MainWindowViewModel owner)
    {
        _owner = owner;
        _x = light.Position.X; _y = light.Position.Y; _z = light.Position.Z;
        _r = Math.Round(light.Color.X * 255); _g = Math.Round(light.Color.Y * 255); _b = Math.Round(light.Color.Z * 255);
        _radius = light.Radius;
        _intensity = light.Intensity;
        _loading = false;
    }

    public System.Numerics.Vector3 Position => new((float)X, (float)Y, (float)Z);

    public PointLight ToPointLight() => new(
        Position,
        new System.Numerics.Vector3((float)(R / 255.0), (float)(G / 255.0), (float)(B / 255.0)),
        (float)Math.Max(Radius, 0.01),    // Parse drops radius <= 0, so never produce one
        (float)Math.Max(Intensity, 0));

    /// <summary>M288: the exact INVERSE of <see cref="ToPointLight"/>, for rebuilding a light from the
    /// 0-255 components the project stores.
    ///
    /// <para>It exists because the constructor above takes a PointLight whose colour is 0-1 and scales it
    /// UP by 255. Handing that constructor stored 0-255 values therefore multiplies twice, and M287 did
    /// exactly that: every restored light came back 255x too bright, which only showed itself after a
    /// bake because that is what reloads the map and runs the restore. Placed next to its inverse so the
    /// two conversions cannot be read - or changed - separately again.</para></summary>
    public static PointLight FromStored(double x, double y, double z,
                                        double r, double g, double b, double radius, double intensity)
        => new(new System.Numerics.Vector3((float)x, (float)y, (float)z),
               new System.Numerics.Vector3((float)(r / 255.0), (float)(g / 255.0), (float)(b / 255.0)),
               (float)Math.Max(radius, 0.01),
               (float)Math.Max(intensity, 0));

    /// <summary>M154: the colour as a real Color, so the inspector can use a proper picker (spectrum +
    /// palette + hex) instead of three raw sliders. Backed by the same R/G/B the file stores.</summary>
    public Avalonia.Media.Color Color
    {
        get => Avalonia.Media.Color.FromRgb(Byte(R), Byte(G), Byte(B));
        set
        {
            if (value.R == Byte(R) && value.G == Byte(G) && value.B == Byte(B)) return;
            _loading = true;                      // one Changed() for the whole colour, not three
            R = value.R; G = value.G; B = value.B;
            _loading = false;
            Changed();
        }
    }

    private static byte Byte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

    /// <summary>Outliner label — position + radius, so lights are tellable apart at a glance.</summary>
    public string Label => $"{Name}  ({X:0}, {Y:0}, {Z:0})  r{Radius:0}";
    public string ColorHex => $"#{(int)Math.Clamp(R, 0, 255):X2}{(int)Math.Clamp(G, 0, 255):X2}{(int)Math.Clamp(B, 0, 255):X2}";
    public string Info => $"radius {Radius:0} · strength {Intensity:0.##}";

    /// <summary>Move from a viewport gizmo drag.</summary>
    public void MoveTo(System.Numerics.Vector3 p)
    {
        _loading = true;
        X = p.X; Y = p.Y; Z = p.Z;
        _loading = false;
        Changed();
    }

    partial void OnXChanged(double v) => Changed();
    partial void OnYChanged(double v) => Changed();
    partial void OnZChanged(double v) => Changed();
    partial void OnRChanged(double v) => Changed();
    partial void OnGChanged(double v) => Changed();
    partial void OnBChanged(double v) => Changed();
    partial void OnRadiusChanged(double v) => Changed();
    partial void OnIntensityChanged(double v) => Changed();
    partial void OnNameChanged(string v) => Changed();

    private void Changed()
    {
        if (_loading) return;
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(ColorHex));
        OnPropertyChanged(nameof(Color));
        OnPropertyChanged(nameof(Info));
        OnPropertyChanged(nameof(Position));
        _owner.RepublishLights();
    }
}
