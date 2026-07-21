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
    private readonly AssetOverrideStore _overrides = new();
    private readonly ReyEngine.App.Services.ThumbnailService _thumbnails; // Content Browser lazy thumbnails
    private readonly Dictionary<ulong, AssetNodeViewModel> _nodesByHash = new();

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
    partial void OnSelectedParticleNodeChanged(ParticlePlacementViewModel? value)
    {
        SelectedParticleMarker = value?.CurrentPosition;
        RefreshParticleMoveFields(value);
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

    private bool IsParticleVisible(MapParticlePlacement particle) =>
        MapVisibility.VisibleForDragon(particle.VisibilityFlags, CurrentDragonBit);

    private bool IsSoundVisible(MapSoundPlacement sound) =>
        MapVisibility.VisibleForDragon(sound.VisibilityFlags, CurrentDragonBit);

    private void UpdateParticleMarkers() =>
        ParticleMarkers = (ShowParticles && MapContent.HasParticles)
            ? MapContent.AllParticles.Where(v => IsParticleVisible(v.Placement)).Select(v => v.CurrentPosition).ToList() : null;

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
            DynamicLights = lights;
            HasDynamicLights = true;
            ShowDynamicLights = true;
            DynamicLightsStatus = $"{lights.Count} point light(s) — {Path.GetFileName(file)}";
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
        if (!AmbienceEnabled || _mapAudioBanks is null || !Sound.IsAvailable || CurrentModelSounds is not { } sounds) return;

        const int maxVoices = 6;
        var nearest = sounds
            .Where(IsSoundVisible)
            .Select((s, i) => (Sound: s, Index: i, Dist: System.Numerics.Vector3.Distance(s.Position, camPos)))
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
        var snapshot = props.ToList();
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
        PropMarkers = (ShowPlaceables && ShowPropIcons && MapContent.HasProps) ? MapContent.AllProps.Select(p => p.Position).ToList() : null;
        ProbeMarkers = (ShowPlaceables && MapContent.HasProbes) ? MapContent.Probes.Select(p => p.Position).ToList() : null;
        SoundMarkers = (ShowPlaceables && ShowSoundIcons && MapContent.HasSounds)
            ? MapContent.Sounds.Where(s => IsSoundVisible(s.Sound)).Select(s => s.Position).ToList() : null;   // M55
    }

    partial void OnSelectedPropTreeItemChanged(object? value)
    { if (value is AnimatedPropViewModel p) SelectedPropNode = p; }
    partial void OnSelectedPropNodeChanged(AnimatedPropViewModel? value)
    {
        if (value is not { } p) return;
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
        _vfxTextureCache.Clear(); _vfxTextureMultCache.Clear(); _vfxDistortionTextureCache.Clear(); _vfxColorTextureCache.Clear(); _vfxMeshCache.Clear();
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
            ResolveSystemColorTextures(sys)) });
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
                if (!IsParticleVisible(v.Placement)) continue;
                if (!_vfxSystems.TryGetValue(v.Placement.SystemHash, out var s) || !s.Emitters.Any(e => e.IsVisual)) continue;
                items.Add(new VfxPlaybackItem(s, v.CurrentTransform, ResolveSystemTextures(s), ResolveSystemMeshes(s),
                    ResolveSystemMultTextures(s), ResolveSystemDistortionTextures(s), ResolveSystemColorTextures(s)));
            }
            CurrentParticlePlayback = items.Count > 0 ? new VfxPlayback(items, CullByCamera: true) : null;
            _log.Info("Particles", $"Playing all — {items.Count} layer-visible placement(s); viewport culling keeps only nearby on-screen systems active.");
            return;
        }

        if (!PlayParticlePreview || SelectedParticleNode is not { } node
            || !_vfxSystems.TryGetValue(node.Placement.SystemHash, out var sys) || sys.Emitters.Count == 0)
        {
            CurrentParticlePlayback = null;
            return;
        }
        var texs = ResolveSystemTextures(sys);
        CurrentParticlePlayback = new VfxPlayback(new[] { new VfxPlaybackItem(sys, node.CurrentTransform, texs,
            ResolveSystemMeshes(sys), ResolveSystemMultTextures(sys), ResolveSystemDistortionTextures(sys),
            ResolveSystemColorTextures(sys)) });
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
        HasParticleMoves = MapContent.AllParticles.Any(v => v.IsMoved);
        RebuildParticlePlayback();   // M36: follow the moved particle if it's playing
        _log.Info("Particles", $"Moved '{node.Name}' to ({target.X:0.#}, {target.Y:0.#}, {target.Z:0.#}).");
    }

    [RelayCommand]
    private void ResetParticleMove()
    {
        if (SelectedParticleNode is not { } node) return;
        node.Offset = System.Numerics.Vector3.Zero;
        node.RotationDegrees = System.Numerics.Vector3.Zero;   // M75
        node.Scale = System.Numerics.Vector3.One;
        RefreshParticleMoveFields(node);
        SelectedParticleMarker = node.CurrentPosition;
        GizmoPivot = node.CurrentPosition;
        UpdateParticleMarkers();
        HasParticleMoves = MapContent.AllParticles.Any(v => v.IsMoved);
        RebuildParticlePlayback();
    }

    // ---- M75: placement gizmo — the viewport gizmo drives particles (move/rotate/scale) and sounds (move).
    // Mirrors the mesh drag API; per-frame updates are silent, EndPlacementDrag logs + refreshes playback. ----

    [ObservableProperty] private AddedMapMeshViewModel? _selectedAddedMesh;   // M79

    /// <summary>True when the gizmo should operate on a placement (no mesh selected, placement is).</summary>
    public bool HasPlacementGizmoTarget => SelectedParticleNode is not null || SelectedSound is not null || SelectedAddedMesh is not null;

    /// <summary>Drag-start state for the active placement (sounds report identity rotation/scale).</summary>
    public (System.Numerics.Vector3 Offset, System.Numerics.Vector3 Rotation, System.Numerics.Vector3 Scale) PlacementDragStart =>
        SelectedParticleNode is { } p ? (p.Offset, p.RotationDegrees, p.Scale)
        : SelectedAddedMesh is { } a ? (a.Offset, a.RotationDegrees, a.Scale)
        : (SelectedSound?.Offset ?? System.Numerics.Vector3.Zero, System.Numerics.Vector3.Zero, System.Numerics.Vector3.One);

    // M76: undo support — the whole drag is ONE step, captured at press, pushed at release.
    private object? _placementDragTarget;
    private PlacementTransformCommand.State _placementDragBefore;

    /// <summary>Called at gizmo-press on a placement: capture the before-state for the undo step.</summary>
    public void BeginPlacementDrag()
    {
        _placementDragTarget = (object?)SelectedParticleNode ?? (object?)SelectedSound ?? SelectedAddedMesh;
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
        }
        HasParticleMoves = MapContent.AllParticles.Any(v => v.IsMoved) || MapContent.Sounds.Any(v => v.IsMoved);
    }

    public void DragSelectedPlacementTo(System.Numerics.Vector3 absoluteOffset)
    {
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
        HasParticleMoves = MapContent.AllParticles.Any(v => v.IsMoved) || MapContent.Sounds.Any(s => s.IsMoved);
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
    [ObservableProperty] private AnimationClip? _currentAnimation;
    [ObservableProperty] private double _animationTime;
    [ObservableProperty] private bool _showWireframe;
    [ObservableProperty] private bool _showBones;
    [ObservableProperty] private bool _showBounds;
    [ObservableProperty] private bool _cullBackfaces = true; // M34: respect per-material cullEnable by default (off = force all two-sided)
    [ObservableProperty] private bool _showLightmaps = true; // M69: baked lightmaps on by default; off = sun/sky fallback lighting
    // M70: legacy Riot dynamic point lights (Light.dat)
    [ObservableProperty] private bool _showDynamicLights;
    [ObservableProperty] private double _dynamicLightIntensity = 1.0;
    [ObservableProperty] private double _dynamicLightRadiusScale = 1.0;   // M71: global light-radius multiplier
    [ObservableProperty] private double _dynamicLightPositionScale = 1.0; // M71: master light-position spread (XZ)
    [ObservableProperty] private double _dynamicLightScaleX = 1.0;        // M71: per-axis fine scale
    [ObservableProperty] private double _dynamicLightScaleZ = 1.0;
    [ObservableProperty] private double _dynamicLightOffsetX = 0.0;       // M71: world-space translate
    [ObservableProperty] private double _dynamicLightOffsetZ = 0.0;
    [ObservableProperty] private IReadOnlyList<PointLight>? _dynamicLights;
    [ObservableProperty] private string? _dynamicLightsStatus;
    [ObservableProperty] private bool _hasDynamicLights;
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
    private MapGeoAsset? _currentMap;
    private IReadOnlyDictionary<string, MaterialProfile>? _currentMapProfiles; // M34: material name → render-state profile
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
        ParticleEditor.ResolveMeshes = ResolveSystemMeshes;   // M47: .scb/.sco mesh primitives

        // M55: model-preview window — its own animation clock (AnimationInspectorViewModel) + VFX resolvers
        MeshPreview.Animation.ClipLoader = DecodeAnimation;
        MeshPreview.LoadDummyMesh = () => Services.TargetDummyLoader.Get(Project.GameDirectory, _resolver,
            m => _log.Warn("Preview", m));   // M115: Riot's practice dummy from Map11.wad
        MeshPreview.LoadSkybox = LoadSkyboxAtAsync;   // M122: same catalogue, its own pick
        MeshPreview.ResolveTextures = ResolveSystemTextures;
        MeshPreview.ResolveDistortionTextures = ResolveSystemDistortionTextures;
        MeshPreview.ResolveColorTextures = ResolveSystemColorTextures;   // M68
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
        ParticleEditor.Info = m => _log.Info("Particle", m);
        ParticleEditor.Error = m => _log.Error("Particle", m);
        ParticleEditor.MarkDocumentDirty = () => { }; // window has its own dirty state via Document.IsDirty
        ParticleEditor.LoadThumbnail = LoadThumbnailByPath;
        ParticleEditor.SaveOverrideAsync = SaveParticleOverride;
        ParticleEditor.OpenIssues = OpenParticleBinIssues;   // M125
        MaterialEditor.OpenIssues = OpenMaterialBinIssues;   // M125

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
        MapGeoAsset Map, byte[] MapBytes, WadAssetEntry Entry, MapVisibilityControllers? Controllers,
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
        int DragonIndex, int BaronIndex, bool HasMoves, int[] SelectedMeshIndices,
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

    private void OpenParticleEditorFor(WadAssetEntry entry)
    {
        try
        {
            var bytes = ReadAsset(entry.PathHash);
            bool editable = !entry.ReadOnly;
            if (!ParticleEditor.Load(entry, bytes, editable))
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
        return new MapScene(map, _currentMapBytes, entry, _mapControllers, mesh,
            CurrentModelTextures, CurrentModelSubmeshMaterials, CurrentModelLightmapTextures,
            _mapFlowMasks, _mapFlowGrads, _mapTerrainTops, _mapTerrainExtras,
            CurrentLightmapScale, CurrentSunProperties,
            CurrentModelParticles, _vfxSystems, CurrentModelProbes, CurrentModelProps,
            CurrentModelSounds,
            SelectedDragonIndex, SelectedBaronIndex, HasMapMoves,
            _selection.Items.Select(m => m.Index).ToArray(),
            MapContent.LayerGroups.ToList(), MapContent.MapName, MapContent.Pieces.ToList());
    }

    private void RestoreMapScene(MapScene s)
    {
        CurrentSkeleton = null; ShowBones = false;
        _currentMap = s.Map; _currentMapBytes = s.MapBytes; _currentMapEntry = s.Entry;
        HasMapGeo = true;   // M79
        _mapControllers = s.Controllers;
        _visibilityResolver = new MapVisibilityResolver(s.Controllers);
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
        SelectedDragonIndex = s.DragonIndex;
        SelectedBaronIndex = s.BaronIndex;
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
                CurrentModelTextures = BuildMapTextures(map, m2t, profiles, names.Count);
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
        _currentMapProfiles = null;
        _mapControllers = null;
        _visibilityResolver = null;
        _currentMapBytes = null;
        _currentMapEntry = null;
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

    // ---- Map dragon/baron visibility layers (M22) ----------------------

    public IReadOnlyList<string> DragonOptions { get; } =
        new[] { "All Layers" }.Concat(MapVisibility.Dragons.Select(d => d.Name)).ToList();
    public IReadOnlyList<string> BaronOptions { get; } =
        new[] { "All" }.Concat(MapVisibility.Barons.Select(b => b.Name)).ToList();

    [ObservableProperty] private int _selectedDragonIndex;
    [ObservableProperty] private int _selectedBaronIndex;

    partial void OnSelectedDragonIndexChanged(int value) => ApplyMapVisibility();
    partial void OnSelectedBaronIndexChanged(int value) => ApplyMapVisibility();

    /// <summary>Compute per-group visibility from the selected dragon + baron layers and push it to the viewport.</summary>
    // M104: render regions (mapgeo v18 renderRegionHash). Off hides every region-assigned mesh, leaving
    // the region-independent base geometry — the fastest way to see what a region is contributing.
    [ObservableProperty] private bool _renderRegionsEnabled = true;
    [ObservableProperty] private bool _hasRenderRegions;
    partial void OnRenderRegionsEnabledChanged(bool value) => ApplyMapVisibility();

    private void ApplyMapVisibility()
    {
        if (_currentMap is not { } map) { CurrentModelSubmeshVisible = null; return; }
        int dragonBit = SelectedDragonIndex <= 0 ? 0 : MapVisibility.Dragons[SelectedDragonIndex - 1].Bit;
        int baronBit = SelectedBaronIndex <= 0 ? 0 : MapVisibility.Barons[SelectedBaronIndex - 1].Bit;
        var resolver = _visibilityResolver ??= new MapVisibilityResolver(_mapControllers);
        var regionOf = map.Meshes.ToDictionary(m => m.Index, m => m.RegionHash);
        // M105: pending layer edits preview live — the group snapshot keeps the FILE's values, so the
        // check reads the mesh's effective (edited) mask/controller when there is one.
        var meshByIdx = map.Meshes.ToDictionary(m => m.Index);
        HasRenderRegions = regionOf.Values.Any(r => r != 0);
        var vis = new bool[map.Groups.Count];
        for (int i = 0; i < vis.Length; i++)
        {
            var g = map.Groups[i];
            int flags = g.VisibilityFlags;
            uint ctrl = g.ControllerHash;
            if (g.MeshIndex >= 0 && meshByIdx.TryGetValue(g.MeshIndex, out var src))
            { flags = src.EffectiveVisibility; ctrl = src.EffectiveController; }
            vis[i] = resolver.IsVisible(flags, ctrl, dragonBit, baronBit);
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

    /// <summary>Current dragon/baron bits from the selectors (0 = "All").</summary>
    private int CurrentDragonBit => SelectedDragonIndex <= 0 ? 0 : MapVisibility.Dragons[SelectedDragonIndex - 1].Bit;
    private int CurrentBaronBit => SelectedBaronIndex <= 0 ? 0 : MapVisibility.Barons[SelectedBaronIndex - 1].Bit;

    /// <summary>Visibility diagnostic for the primary-selected mesh under the current dragon/baron filters (M33).</summary>
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
        var d = _visibilityResolver.Resolve(m.EffectiveVisibility, m.EffectiveController, CurrentDragonBit, CurrentBaronBit);
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

    /// <summary>The 8 dragon-layer checkboxes (state mirrors the primary selected mesh).</summary>
    public ObservableCollection<LayerBitViewModel> DragonLayerBits { get; } = new();

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
            if (DragonLayerBits.Count == 0)
                foreach (var d in MapVisibility.Dragons)
                    DragonLayerBits.Add(new LayerBitViewModel(this, d.Name, d.Bit));

            if (_selection.Primary is not { } m || _currentMap is null)
            { HasLayerSelection = false; LayerSummary = ""; return; }

            HasLayerSelection = true;
            int flags = m.EffectiveVisibility;
            foreach (var b in DragonLayerBits)
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
            LayerSummary = $"{MapVisibility.DragonLabel(flags)} · mask 0b{Convert.ToString(flags & 0xFF, 2).PadLeft(8, '0')}"
                           + (selCount > 1 ? $" · applies to {selCount} selected meshes" : "")
                           + (edited > 0 ? $" · {edited} unsaved layer edit(s)" : "");
        }
        finally { _layerUiLoading = false; }
    }

    /// <summary>Set/clear one dragon bit on every selected mesh (one undo step).</summary>
    internal void SetLayerBitOnSelection(int bit, bool on)
    {
        if (_layerUiLoading) return;
        ApplyLayerEdit($"{(on ? "Add to" : "Remove from")} {MapVisibility.Dragons.First(d => d.Bit == bit).Name} Layer", m =>
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

    /// <summary>Index the baron/dragon visibility controllers from the map's sibling .bin files.</summary>
    private void BuildMapControllers(string mapgeoPath)
    {
        var dir = mapgeoPath[..(mapgeoPath.LastIndexOf('/') + 1)];
        var bins = new List<byte[]>();
        foreach (var e in AssetEntries.Where(e => e.IsResolved
                     && e.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                     && e.Path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)))
        {
            try { bins.Add(ReadAsset(e.PathHash)); } catch { /* skip unreadable bins */ }
        }
        _mapControllers = MapVisibilityControllers.Build(bins);
        _visibilityResolver = new MapVisibilityResolver(_mapControllers);
        if (_mapControllers.BaronControllerCount > 0)
            _log.Info("MapGeo", $"Baron visibility: {_mapControllers.Count} controllers ({_mapControllers.BaronControllerCount} baron) from {bins.Count} bin(s).");
        else
            _log.Info("MapGeo", $"No baron visibility controllers found in this map's bins — baron filter inactive (dragon layers still work).");
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
                    Name = $"{MapVisibility.DragonLabel(g.Key)} — {g.Count()} mesh(es)",
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
        switch (value)
        {
            case MapPieceViewModel { MeshIndex: >= 0 } p when _currentMap is { } map
                && map.Meshes.FirstOrDefault(x => x.Index == p.MeshIndex) is { } m:
                _selection.SetSingle(m);
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
                foreach (var p in MapContent.AllParticles.Where(v => IsParticleVisible(v.Placement))) TestPx(p, p.CurrentPosition);
            if (ShowPlaceables && MapContent.HasProps)
                foreach (var p in MapContent.AllProps) TestPx(p, p.Position);
            if (ShowPlaceables && MapContent.HasProbes)
                foreach (var p in MapContent.Probes) TestPx(p, p.Position);
            if (ShowPlaceables && MapContent.HasSounds)
                foreach (var s in MapContent.Sounds.Where(v => IsSoundVisible(v.Sound))) TestPx(s, s.Position);
            // M123e: staged (not yet saved) meshes are click-selectable at their world center
            foreach (var a in MapContent.AddedMeshes) TestPx(a, a.LocalCenter + a.Offset);
            if (bestPx is not null) { SelectedOutlinerItem = bestPx; return; }
        }

        rayDir = System.Numerics.Vector3.Normalize(rayDir);   // same t units for mesh + marker tests
        // mesh hit distance (float.MaxValue when none)
        float meshT = float.MaxValue;
        if (_currentMap is { } map0 && map0.Groups.Count > 0)
        {
            var subs = map0.Groups.Select(g => (g.StartIndex, g.IndexCount)).ToList();
            if (ViewportMeshPicker.PickSubmesh(map0.Positions, map0.Indices, subs,
                    CurrentModelSubmeshVisible, rayOrigin, rayDir, out var t) >= 0)
                meshT = t;
        }

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
            foreach (var p in MapContent.AllParticles.Where(v => IsParticleVisible(v.Placement))) Test(p, p.CurrentPosition);
        if (ShowPlaceables && MapContent.HasProps && !additive)
            foreach (var p in MapContent.AllProps) Test(p, p.Position);
        if (ShowPlaceables && MapContent.HasProbes && !additive)
            foreach (var p in MapContent.Probes) Test(p, p.Position);
        if (ShowPlaceables && MapContent.HasSounds && !additive)
            foreach (var s in MapContent.Sounds.Where(v => IsSoundVisible(v.Sound))) Test(s, s.Position);   // M55/M60

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
        var submeshes = map.Groups.Select(g => (g.StartIndex, g.IndexCount)).ToList();
        int hit = ViewportMeshPicker.PickSubmesh(map.Positions, map.Indices, submeshes,
            CurrentModelSubmeshVisible, rayOrigin, rayDir, out _);
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
        var name = mesh.Name?.Length > 0 ? mesh.Name : $"#{meshIndex}";
        _log.Info("MapGeo", additive ? $"{(_selection.Contains(mesh) ? "Added" : "Removed")} '{name}' ({_selection.Count} selected)."
                                      : $"Selected '{name}' (viewport click).");
    }

    /// <summary>Ctrl+click on a Map Content tree row: toggle that mesh in/out of the selection.</summary>
    public void ToggleMeshSelectionFromTree(MapPieceViewModel piece)
    {
        if (_currentMap is not { } map || piece.MeshIndex < 0) return;
        if (map.Meshes.FirstOrDefault(x => x.Index == piece.MeshIndex) is { } m) _selection.Toggle(m);
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
        SelectedParticleMarker = null;
        SelectedPlaceableInfo = "";
        if (_selection.IsEmpty) GizmoPivot = null;
        _syncingTreeSelection = true;
        SelectedOutlinerItem = null;   // drop the outliner row highlight too
        _syncingTreeSelection = false;
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
        if (!hasMoves && !hasLayers && added.Count == 0) { _log.Info("MapGeo", "No map edits to save."); return; }
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
                bool anyMask = added.Any(a => a.VisibilityMask is not 255 and not 0);
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
            _log.Success("MapGeo", $"Saved {moves} mesh move(s) + {layers} layer edit(s) + {added.Count} added mesh(es) to override ({bytes.Length:n0} bytes). Build Package will include it. Reload the map to edit the added meshes as native geometry.");
        }
        catch (Exception ex) { _log.Error("MapGeo", ex.Message); }
    }

    /// <summary>Persist the moved particles into the map's .materials.bin override (M35).</summary>
    [RelayCommand]
    private async Task SaveParticleMoves()
    {
        if (_currentMapEntry is not { } mapEntry) return;
        var moved = MapContent.AllParticles.Where(v => v.IsMoved).ToList();
        // M75: sounds derived FROM a particle system share the particle's transform bytes — patching them
        // independently would collide with the particle edit. Only standalone MapAudio placements save.
        var movedSounds = MapContent.Sounds.Where(s => s.IsMoved && !s.Sound.FromParticleSystem).ToList();
        int skippedSounds = MapContent.Sounds.Count(s => s.IsMoved && s.Sound.FromParticleSystem);
        if (skippedSounds > 0)
            _log.Warn("Sounds", $"{skippedSounds} moved sound(s) come from particle systems — move the particle instead; they were skipped.");
        if (moved.Count == 0 && movedSounds.Count == 0) { _log.Info("Particles", "No placement edits to save."); return; }
        if (!TryResolveMaterialsBin(mapEntry.Path, out var binEntry)) { _log.Error("Particles", "No materials .bin to save into."); return; }
        if (!GuardEditable(binEntry)) return;
        if (!await EnsureProjectSavedAsync()) return;

        // M75: full replacement transforms — position + rotation + scale for particles; position for sounds.
        var edits = moved.Select(v => (v.Placement.Transform, v.CurrentTransform)).ToList();
        edits.AddRange(movedSounds.Select(s =>
        {
            var t = s.Sound.Transform;
            t.Translation = s.Position;
            return (s.Sound.Transform, t);
        }));
        var bytes = MapParticleWriter.WriteTransforms(GetAssetBytes(binEntry), edits, out var err);
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
            _log.Success("Particles", $"Saved {moved.Count} particle edit(s) + {movedSounds.Count} sound move(s) to the materials.bin override. Build Package will include it.");
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
                _currentMapBytes = rawMapBytes;
                _currentMapEntry = entry;
                HasMapGeo = true;   // M79
                _selection.Clear();
                CurrentModelTextures = textures;
                ApplySunProperties(sunProperties);
                ClearSecondaryTextures(); // maps don't use champion secondary samplers
                PublishMapMaterialLayers(); // re-apply map special-material layers wiped above
                MapGeoInspector.Show(map, entry.Path);
                MapContent.SetBucketGrids(map.BucketGrids);   // M55: culling grid showcase
                HasBucketGrids = map.BucketGrids.Count > 0;   // M77
                RebuildBucketGridLines();
                MapContent.ShowMap(entry.DisplayName, map.Groups
                    .Select((g, i) => new MapPieceViewModel { Name = string.IsNullOrEmpty(g.Material) ? $"Mesh {i}" : g.Material, Info = $"{g.IndexCount / 3:n0} tris" })
                    .ToList());
                BuildMapLayerGroups(map);
                BuildMapControllers(entry.Path);
                SelectedBaronIndex = 0;
                SelectedDragonIndex = 0; // handlers call ApplyMapVisibility; _currentMap is already set
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
        return (BuildMapTextures(map, materialToTexture, profiles, names.Count), sunProperties);
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
                    Formats.MapGeo.MapSunProperties.Extract(bytes));
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
                        Formats.MapGeo.MapSunProperties.Extract(fb));
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
        _suppressSunRebuild = true;
        SunColorR = Clamp01(_baseSun.SunColor.X); SunColorG = Clamp01(_baseSun.SunColor.Y); SunColorB = Clamp01(_baseSun.SunColor.Z);
        SunIntensity = 1.0;
        SkyColorR = Clamp01(_baseSun.SkyLightColor.X); SkyColorG = Clamp01(_baseSun.SkyLightColor.Y); SkyColorB = Clamp01(_baseSun.SkyLightColor.Z);
        SkyIntensity = System.Math.Clamp(_baseSun.SkyLightScale, 0f, 8f);
        _suppressSunRebuild = false;
        RebuildSun();
        CurrentLightmapScale = sun?.LightMapColorScale ?? 1.0;
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
    }

    public Avalonia.Media.IBrush SunSwatch => Swatch(SunColorR * SunIntensity, SunColorG * SunIntensity, SunColorB * SunIntensity);
    public Avalonia.Media.IBrush SkySwatch => Swatch(SkyColorR * SkyIntensity, SkyColorG * SkyIntensity, SkyColorB * SkyIntensity);
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
        Dictionary<string, MaterialProfile> profilesByName, int materialCount)
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
        // Per-mesh mirrored (negative-determinant) flag, for the two-sided/mirrored render state (M34).
        var mirroredByMesh = map.Meshes.ToDictionary(m => m.Index, m => m.IsMirrored);
        int lmGroups = 0, flowGroups = 0, terrainGroups = 0;
        for (int i = 0; i < map.Groups.Count; i++)
        {
            var matName = map.Groups[i].Material;
            if (materialToTexture.TryGetValue(matName, out var path))
                result[i] = Load(path);
            if (profilesByName.TryGetValue(matName, out var prof))
            {
                submeshMats[i] = ToSubmeshMaterial(prof);
                LogUvTransform(prof, matName);

                // Shader 0xe25b830f: load the opaque terrain splat layers. Renderer slots are deliberately
                // reused because regular emissive/matcap effects are disabled inside the terrain branch.
                if (prof.IsTerrainBlend)
                {
                    if (!string.IsNullOrEmpty(prof.TerrainBottomPath)) result[i] = Load(prof.TerrainBottomPath);
                    if (!string.IsNullOrEmpty(prof.TerrainMaskPath)) flowMaps[i] = Load(prof.TerrainMaskPath);
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
                               (terrainGroups > 0 ? $" {terrainGroups} terrain-blend group(s)." : ""));
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

    private static ViewportMeshRenderer.SubmeshMaterial ToSubmeshMaterial(MaterialProfile p) =>
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
            UsesGrassTint: p.UsesGrassTint);   // M78

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

    /// <summary>M98: right-click ▸ Open in Map Bin Editor — the fast structured editor for map*.bin.</summary>
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
    /// Editor, everything else lands in the Materials tab filtered to the object.</summary>
    private async Task NavigateToBinObjectAsync(WadAssetEntry entry, uint objHash, uint classHash)
    {
        if (classHash == HashAlgorithms.Fnv1a("VfxSystemDefinitionData"))
        {
            OpenParticleEditorFor(entry);
            if (ParticleEditor.Systems.FirstOrDefault(s => s.Entry.PathHash == objHash) is { } node)
                ParticleEditor.SelectedSystem = node;
            return;
        }
        await LoadMaterialBinAsync(entry, alsoRawBin: false);
        InspectorTab = 1;
        AssetDataExpanded = true;
        MaterialEditor.SetMeshFilter(null);
        MaterialEditor.OnlyUnresolved = false;
        if (MaterialEditor.Materials.FirstOrDefault(m => m.Model.ObjectPathHash == objHash) is { } mat)
            MaterialEditor.Search = mat.Name;
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

    /// <summary>The canonical projects folder (Documents\ReyEngine Projects — follows OneDrive redirection,
    /// same default the New Project wizard uses). Created on demand.</summary>
    public static string ProjectsFolder =>
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
            .Append(Project.Name);

        int n = 0;
        foreach (var wad in GameReferenceLibrary.Discover(Project.GameDirectory, mapNames))
        {
            if (Project.ReferenceWads.Contains(wad, StringComparer.OrdinalIgnoreCase)) continue; // already an explicit reference
            try { _mounts.AddFallback(new WadMount(WadArchive.Open(wad), AssetSourceKind.RiotReference, editable: false, name: Path.GetFileName(wad))); n++; }
            catch (Exception ex) { _log.Warn("Project", $"game fallback {Path.GetFileName(wad)}: {ex.Message}"); }
        }
        if (n > 0)
            _log.Info("Project", $"Mounted {n} game WAD(s) as read-only fallback — missing skin bins/textures resolve from the original game files. (Set the game folder via Tools if assets are still missing.)");
        else if (string.IsNullOrEmpty(Project.GameDirectory))
            _log.Warn("Project", "No game folder configured — missing base assets (textures/skin bins not in the mod) won't resolve. Set it so ReyEngine can fall back to the original WADs.");
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
        Project.GameDirectory = folder;
        var probe = GameReferenceLibrary.Discover(folder, Project.ProjectFolders.Append(Project.Name));
        if (probe.Count == 0) { _log.Warn("Project", $"No game WADs found under {folder}. Pick the 'Game' folder (it should contain DATA/FINAL)."); return; }
        if (ProjectMode)
        {
            ReyProjectService.Save(Project, Project.ProjectFilePath!);
            BuildMounts(); BuildProjectTree();
        }
        _log.Success("Project", $"Game folder set: {folder} — {probe.Count} reference WAD(s) available as fallback. Reload the asset to apply.");
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
        if (!entry.Path.Contains(".dx11", StringComparison.OrdinalIgnoreCase)) { _log.Warn("Shader", "Selected asset isn't a shader (.dx11)."); return; }
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
        try
        {
            await Task.Run(() =>
            {
                var buildRoot = Project.OutputDirectory ?? Path.Combine(Project.RootPath, "Build");
                if (BuildSafety.IsInsideGameInstall(buildRoot))
                    throw new InvalidOperationException("Build output is inside the game install — change it in Project Settings.");
                Directory.CreateDirectory(buildRoot);
                BuildProjectCore(buildRoot);
                var wads = Directory.GetFiles(buildRoot, "*.wad.client").ToList();
                if (wads.Count == 0) throw new InvalidOperationException("No WAD was produced — the project has no packable content.");
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

    /// <summary>M98c/d: write bytes to the asset's REAL path inside the per-WAD project folder
    /// (Map11.wad.client → Map11/data/…). False when this isn't a folder project or the path is
    /// unresolved — the caller falls back to the hashed override store.</summary>
    private bool TryPlaceInProjectFolder(WadAssetEntry entry, byte[] bytes, out string placedRelative)
    {
        placedRelative = "";
        if (!Project.IsFolderProject || !entry.IsResolved || Project.RootPath is null || _mounts is null) return false;

        string folderName = "Overrides";
        if (_mounts.TryGet(entry.PathHash, out var mounted))
        {
            var riotSrc = mounted.Source.Kind == AssetSourceKind.RiotReference ? mounted.Source
                : mounted.AllSources.FirstOrDefault(s => s.Kind == AssetSourceKind.RiotReference);
            if (riotSrc is not null)
            {
                var wadName = Path.GetFileName(riotSrc.Location);
                if (wadName.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase))
                    wadName = wadName[..^".wad.client".Length];
                foreach (var c in Path.GetInvalidFileNameChars()) wadName = wadName.Replace(c, '_');
                if (wadName.Length > 0) folderName = wadName;
            }
        }

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
        try
        {
            await Task.Run(() => BuildProjectCore(buildRoot));
            Status = $"Built project to {buildRoot}";
            _log.Success("Build", $"Project build ready: {buildRoot}. Open it via File ▸ Open Project Folder to verify.");
        }
        catch (Exception ex) { _log.Error("Build", ex.Message); }
        finally { IsBuilding = false; }
    }

    private void BuildProjectCore(string buildRoot)
    {
        var overridesByHash = _overrides.All.ToDictionary(o => o.PathHash, o => o.OverrideFile);
        int wads = 0, staged = 0, files = 0, skipped = 0;

        // Project WADs: safe in-place replace of existing chunks.
        foreach (var w in Project.ProjectWads)
        {
            var src = Project.ResolveProjectPath(w);
            if (!File.Exists(src)) { _log.Warn("Build", $"missing project WAD {w}"); continue; }
            using var arc = WadArchive.Open(src, _resolver);
            var apply = new Dictionary<ulong, byte[]>();
            foreach (var (hash, file) in overridesByHash)
                if (arc.TryGetEntry(hash, out _) && File.Exists(file)) apply[hash] = File.ReadAllBytes(file);
            var outWad = Path.Combine(buildRoot, Path.GetFileName(w));
            var report = new BuildReport { OutputPath = outWad };
            WadRepackService.Repack(src, apply, outWad, report);
            foreach (var i in report.Issues) _log.Warn("Build", i.Message);
            _log.Info("Build", $"WAD {Path.GetFileName(w)}: {apply.Count} replaced → {Path.GetFileName(outWad)}");
            wads++;
        }

        // Project folders: stage (copy tree + apply overrides as files — new files are safe in folder format).
        var stagedFolders = new List<(string name, string dir)>();
        foreach (var f in Project.ProjectFolders)
        {
            var srcFolder = Project.ResolveProjectPath(f);
            var name = f == "." ? Project.Name : f.Replace('/', '_');
            var outFolder = Path.Combine(buildRoot, "staged", name);
            files += CopyTree(srcFolder, outFolder);
            stagedFolders.Add((name, outFolder));
            staged++;
        }

        // Apply overrides into the first staged folder at their resolved path.
        if (stagedFolders.Count > 0)
        {
            var outFolder = stagedFolders[0].dir;
            foreach (var ov in _overrides.All)
            {
                if (!File.Exists(ov.OverrideFile)) { skipped++; continue; }
                string rel = ov.ResolvedPath ?? $"0x{ov.PathHash:x16}{Path.GetExtension(ov.OverrideFile)}";
                var dest = Path.Combine(outFolder, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(ov.OverrideFile, dest, overwrite: true);
                files++;
            }
        }

        // Pack each staged folder into a distributable .wad.client.
        int packed = 0;
        foreach (var (name, dir) in stagedFolders)
        {
            var outWad = Path.Combine(buildRoot, name + ".wad.client");
            WadPackReport pr;
            try { pr = WadPackService.Pack(dir, outWad); }
            catch (Exception ex) { _log.Error("Build", $"Pack failed for {name}: {ex.Message}"); continue; }
            foreach (var w in pr.Warnings) _log.Warn("Build", w);
            if (pr.Success)
            {
                packed++;
                _log.Success("Build", $"Packed {name}.wad.client — {pr.Chunks:n0} chunks, {pr.InputBytes / 1048576.0:0.0}→{pr.OutputBytes / 1048576.0:0.0} MB. {pr.Validation}");
            }
            else _log.Error("Build", $"Pack didn't validate for {name} — the staged folder is at {dir}.");
        }

        _log.Info("Build", $"project WADs: {wads} · folders packed: {packed}/{staged} · files: {files:n0} · skipped: {skipped}");
    }

    private static int CopyTree(string src, string dst)
    {
        int n = 0;
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            if (rel.StartsWith(".reyengine", StringComparison.OrdinalIgnoreCase)) continue;
            var dest = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
            n++;
        }
        return n;
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
        try
        {
            var report = await Task.Run(() => BuildPackageService.Build(Project, outPath, null, CancellationToken.None));
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

    [RelayCommand] private void ShaderPreview() => _log.Warn("Shader", "Shader/material preview lands in a later milestone.");
    [RelayCommand] private void ClearConsole() => Console.Clear();

    [RelayCommand]
    private void Exit() =>
        (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
}
