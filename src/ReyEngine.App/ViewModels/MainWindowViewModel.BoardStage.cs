using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M797: the TFT "Board stage" selector - a Map22 board shown in a game state (level 1, level 7 ...) exactly as its
/// own <c>MapBehavior</c>s define it, instead of the editor's additive reading of the map's starting mask.
///
/// <para><b>Why.</b> dawnbringernightbringer's level 7 is visibility mask 20 WITHOUT the base bit 64, but the
/// additive rule of <see cref="MapVisibility.VisibleForMask"/> keeps Map22's initial 67 on beside whatever layer is
/// picked, so level-1 and level-7 content always showed together - a white level-1 cloud vortex round the level-7
/// board. A stage instead IS the mask (<see cref="MapVisibility.VisibleForStage"/>), plus the particle switches
/// and the lighting volume the mask turns on.</para>
///
/// <para><b>Off unless chosen.</b> "Start" - the default - changes nothing. A map with no stage behaviour (Summoner's
/// Rift, Map12, Arena, every board that only has events) has no picker at all, and none of the code below runs.</para>
///
/// <para><b>Not emulated, and said so in the tooltip and the log:</b> the lighting-volume and fog fades, prop
/// animations, sounds, and the stencil masks of the stage-transition materials.</para>
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>The picker for the open board: one item on a TFT board, none on any other map. An items control
    /// over a list of one, like <see cref="VisibilityAxes"/>, so a new board gets a new combo instead of a
    /// swapped item source.</summary>
    public ObservableCollection<BoardStageViewModel> BoardStageSelectors { get; } = new();

    /// <summary>True while the open board has a picker. The view binds the items control's visibility to it: an
    /// EMPTY items control is still a visible child, and its StackPanel would put a spacing gap under the
    /// section on every map that has no stages.</summary>
    [ObservableProperty] private bool _hasBoardStages;

    /// <summary>The stage the picker is on; null is "Start", the map as it loads.</summary>
    private MapBoardStage? _activeBoardStage;

    /// <summary>What a picker last put on screen - the sun it published and the "Baked brightness" beside it - and
    /// whether that lighting is a stage's rather than the board's own. What is on screen NOW is compared with it
    /// before a stage replaces it: a hand adjustment (the "Baked brightness" slider is not captured anywhere else)
    /// is never silently lost, and nothing that merely equals the board's authored sun can make a stage skip its
    /// own lighting.</summary>
    public sealed record StageLighting(MapSunProperties? Sun, double LightmapScale, bool FromStage);

    /// <summary>What a map tab keeps of its picker: the picker, WHERE it was, what it said, and the lighting it had
    /// put on screen. The picker itself is live - a skinned-mesh tab leaves the map loaded (OnSelectedNodeChanged
    /// does not clear the viewport for it), so the picker can be moved while the tab is away - which is why the
    /// selection is snapshotted rather than read back from it.</summary>
    public sealed record BoardStageSnapshot(BoardStageViewModel Picker, int SelectedIndex, string Summary, StageLighting? Lighting);

    public sealed partial class BoardStageViewModel : ObservableObject
    {
        private readonly MainWindowViewModel _owner;
        private bool _quiet;

        public MapBoardStageSet Set { get; }

        /// <summary>The lighting the board opened with (what "Start" restores).</summary>
        public MapSunProperties? StartSun { get; }

        /// <summary>"Start", then one entry per stage.</summary>
        public IReadOnlyList<string> Options { get; }

        [ObservableProperty] private int _selectedIndex;

        [NotifyPropertyChangedFor(nameof(HasSummary))]
        [ObservableProperty] private string _summary = "";

        public bool HasSummary => Summary.Length > 0;

        /// <summary>The lighting this picker last put on screen, or found there when the board opened.</summary>
        internal StageLighting? Lighting { get; set; }

        /// <summary>The chosen stage; null while the picker is on "Start".</summary>
        public MapBoardStage? Selected =>
            SelectedIndex >= 1 && SelectedIndex <= Set.Stages.Count ? Set.Stages[SelectedIndex - 1] : null;

        internal BoardStageViewModel(MainWindowViewModel owner, MapBoardStageSet set, MapSunProperties? startSun)
        {
            _owner = owner;
            Set = set;
            StartSun = startSun;
            Options = new[] { "Start" }.Concat(set.Stages.Select(s => s.Label)).ToList();
        }

        partial void OnSelectedIndexChanged(int value)
        {
            if (!_quiet) _owner.ApplyBoardStage(this);
        }

        /// <summary>Move the picker without applying anything: the caller has already done, or is about to do, the
        /// work the change would have triggered.</summary>
        internal void SelectQuietly(int index)
        {
            _quiet = true;
            try { SelectedIndex = index; }
            finally { _quiet = false; }
        }
    }

    /// <summary>Publish the open board's stages, or none. Runs when a map loads (and when one is closed).</summary>
    private void SetBoardStages(MapBoardStageSet set)
    {
        _activeBoardStage = null;
        BoardStageSelectors.Clear();
        HasBoardStages = false;
        if (!set.HasStages) return;

        // _baseSunAuthored is the lighting the load just published: what "Start" has to restore. What is on
        // screen right now - the map's own sun, or the project's saved lighting restored over it - is what the
        // picker starts from.
        BoardStageSelectors.Add(new BoardStageViewModel(this, set, _baseSunAuthored)
        {
            Lighting = new StageLighting(CurrentSunProperties, CurrentLightmapScale, FromStage: false),
        });
        HasBoardStages = true;
        _log.Info("Stage", $"{set.Stages.Count} board stage(s) from this board's MapBehaviors: "
            + string.Join(", ", set.Stages.Select(s => $"{s.Label} = mask {s.Mask}"))
            + ". Pick one under Visibility Layers > Board stage; \"Start\" is the map as it loads.");
    }

    private BoardStageSnapshot? SnapshotBoardStage() =>
        BoardStageSelectors.FirstOrDefault() is { } p ? new BoardStageSnapshot(p, p.SelectedIndex, p.Summary, p.Lighting) : null;

    /// <summary>A restored map tab: put the picker back where the tab left it - the stage, the summary and the
    /// lighting bookkeeping from the snapshot, not from the live picker, which may have been moved since.</summary>
    private void RestoreBoardStage(BoardStageSnapshot? snapshot)
    {
        BoardStageSelectors.Clear();
        _activeBoardStage = null;
        HasBoardStages = false;
        if (snapshot is null) return;

        var picker = snapshot.Picker;
        picker.SelectQuietly(snapshot.SelectedIndex);
        picker.Summary = snapshot.Summary;
        picker.Lighting = snapshot.Lighting;
        _activeBoardStage = picker.Selected;
        BoardStageSelectors.Add(picker);
        HasBoardStages = true;
    }

    /// <summary>The visibility mask of the stage that is on, or null. Read by every map-visibility decision.</summary>
    private int? CurrentStageMask => _activeBoardStage?.Mask;

    /// <summary>Is content with these mapgeo flags shown? The stage's exact rule while one is on, otherwise the
    /// primary axis's own (unchanged) rule for the layer picked in Map Visibility.</summary>
    private bool MaskVisible(int flags) =>
        _activeBoardStage is { } stage
            ? MapVisibility.VisibleForStage(flags, stage.Mask)
            : MapVisibility.VisibleForMask(flags, _mapVisibility.Primary, CurrentPrimaryVisibilityBit);

    /// <summary>Did the stage switch this named placement OFF (its last <c>MapActionToggleMapParticle</c>)?</summary>
    private bool StageSwitchesOff(string placementName) =>
        _activeBoardStage is { } stage && stage.ParticleToggles.TryGetValue(placementName, out bool shown) && !shown;

    /// <summary>Did the stage switch this named placement ON? For a <c>startDisabled</c> placement that is what lets it
    /// play (M784 kept them off because nothing had turned them on).</summary>
    private bool StageSwitchesOn(string placementName) =>
        _activeBoardStage is { } stage && stage.ParticleToggles.TryGetValue(placementName, out bool shown) && shown;

    /// <summary>The picker moved: make the board show that stage.</summary>
    private void ApplyBoardStage(BoardStageViewModel picker)
    {
        var stage = picker.Selected;
        _activeBoardStage = stage;

        // The stage IS the primary axis's state, so a layer left picked in Map Visibility steps aside (back to All)
        // rather than contradict it on screen.
        if (stage is not null && VisibilityAxes.FirstOrDefault(a => a.Axis.IsPrimary) is { SelectedIndex: not 0 } primary)
        {
            bool was = _visibilityUiLoading;
            _visibilityUiLoading = true;
            try { primary.SelectedIndex = 0; }
            finally { _visibilityUiLoading = was; }
        }

        // The lighting first: the summary and the log say what it actually did, not what the stage asked for.
        var lighting = ApplyBoardStageLighting(picker, stage);
        picker.Summary = DescribeBoardStage(stage, lighting, brief: true);
        ApplyMapVisibility();   // meshes, particle markers and Play All, sounds, the inspector's why-hidden line
        _log.Info("Stage", DescribeBoardStage(stage, lighting, brief: false));
    }

    /// <summary>Choosing a layer in Map Visibility while a board stage is on hands the mask back to that layer.</summary>
    private void LeaveBoardStageFor(VisibilityAxisViewModel axis)
    {
        // <= 0, not == 0: SelectedBit reads any index below 1 as "All", and a ComboBox can push -1
        if (_activeBoardStage is null || !axis.Axis.IsPrimary || axis.SelectedIndex <= 0) return;
        if (BoardStageSelectors.FirstOrDefault() is not { } picker) return;

        picker.SelectQuietly(0);
        _activeBoardStage = null;
        picker.Summary = "";
        var lighting = ApplyBoardStageLighting(picker, null);
        string layer = axis.SelectedIndex < axis.Options.Count ? axis.Options[axis.SelectedIndex] : "a layer";
        _log.Info("Stage", $"Board stage back to Start: Map Visibility '{layer}' now decides the layers."
            + (lighting.Kind is StageLightingKind.KeptAdjusted or StageLightingKind.KeptSaved ? " Lighting: " + lighting.Text + "." : ""));
    }

    /// <summary>What <see cref="ApplyBoardStageLighting"/> did.</summary>
    private enum StageLightingKind
    {
        /// <summary>No map entry to light (a bare view model): nothing was touched.</summary>
        NoMap,
        /// <summary>The stage's lighting, or Start's, was applied.</summary>
        Applied,
        /// <summary>What is on screen was changed by hand since the picker put it there - left alone.</summary>
        KeptAdjusted,
        /// <summary>The project saves lighting for this map - left alone.</summary>
        KeptSaved,
    }

    private readonly record struct StageLightingResult(StageLightingKind Kind, MapLightingVolume? Volume)
    {
        public string Text => Kind switch
        {
            StageLightingKind.Applied when Volume is not null => $"lighting volume '{Volume.Name}'",
            StageLightingKind.Applied => "the board's own sun",
            StageLightingKind.KeptAdjusted => "lighting left as you adjusted it (Reset lighting, then pick the stage again, takes the stage's)",
            StageLightingKind.KeptSaved => "lighting left as saved in the project",
            _ => "lighting unchanged",
        };
    }

    /// <summary>
    /// Light the board with the stage's lighting - the volume its mask turns on, laid over what Start has in every
    /// field a volume does not model (<see cref="MapBoardStageSet.SunFor"/>) - or with what the board opened with
    /// for "Start". The fades the behaviour would play into it are not emulated: the board simply shows the state
    /// the fade ends on.
    ///
    /// <para><b>Lighting somebody set wins, and the result says so.</b> Two things count: what is on screen no longer
    /// being what this picker put there (a slider, or the "Baked brightness" box, which nothing else captures), and
    /// lighting saved for the map in the project - the same rule <see cref="RestoreMapLighting"/> follows when the
    /// map opens. The apply itself is the map-open one, so picking a stage never counts as an edit and never
    /// dirties the project: <see cref="ApplySunProperties"/> runs under its own capture guard (M287), and the
    /// wrapper below puts back an OUTER one on the way out - <c>LoadMapGeoAsync</c> holds it across the whole open
    /// (M515) and <see cref="ApplySunProperties"/> clears it when it finishes, so a stage picked while another map
    /// is still opening would otherwise switch the loader's suppression off.</para>
    ///
    /// <para>There is no shortcut for "the sun equals what the map authored": that comparison read state that goes
    /// stale across tabs (another board's load moves it) and left one board's stage lighting under "Start".</para>
    /// </summary>
    private StageLightingResult ApplyBoardStageLighting(BoardStageViewModel picker, MapBoardStage? stage)
    {
        var volume = stage is null ? null : picker.Set.VolumeFor(stage);
        if (_currentMapEntry is not { } entry) return new(StageLightingKind.NoMap, volume);

        if (picker.Lighting is { } put
            && (!Equals(CurrentSunProperties, put.Sun) || CurrentLightmapScale != put.LightmapScale))
            return new(StageLightingKind.KeptAdjusted, volume);

        if (Project.MapLighting.FirstOrDefault(r => r.PathHash == entry.PathHash) is { } record
            && !MapLightingArtefact.LooksLikeUntouchedFallback(record))
            return new(StageLightingKind.KeptSaved, volume);

        var sun = stage is null ? picker.StartSun : picker.Set.SunFor(stage, picker.StartSun);
        bool wasApplying = _applyingLighting;
        _applyingLighting = true;
        try { ApplySunProperties(sun); }
        finally { _applyingLighting = wasApplying; }
        picker.Lighting = new StageLighting(CurrentSunProperties, CurrentLightmapScale, FromStage: stage is not null);
        return new(StageLightingKind.Applied, volume);
    }

    /// <summary>
    /// "Save sun &amp; sky to map" writes the panel into the board's GLOBAL <c>MapSunProperties</c>. While a stage's
    /// lighting is on screen the panel holds a lighting VOLUME's values on top of the board's, and saving would write
    /// them into the global sun - the volume's nine fields over the board's, plus whatever a volume-lit view leaves at
    /// the schema default (fogEnabled = false becomes true, the shadow radius resets to 0). So the save is refused,
    /// with the way out spelled out. Returns true when it refused.
    /// </summary>
    private bool RefuseSunSaveForBoardStage()
    {
        if (BoardStageSelectors.FirstOrDefault() is not { Lighting.FromStage: true }) return false;
        _log.Warn("Lighting", "Switch Board stage to Start before saving the sun to the map - a stage shows its lighting "
            + "volume, not the board's own sun, and saving it would overwrite the board's global sun with the volume's.");
        return true;
    }

    /// <summary>One line for the log, a shorter one for the panel.</summary>
    private string DescribeBoardStage(MapBoardStage? stage, StageLightingResult lighting, bool brief)
    {
        bool kept = lighting.Kind is StageLightingKind.KeptAdjusted or StageLightingKind.KeptSaved;
        if (stage is null)
            return brief ? "" : "Board stage: Start - the map as it loads (the map's starting mask, no scripted switches)"
                + (kept ? "; " + lighting.Text : "") + ".";

        var axis = _mapVisibility.Primary;
        string layers = MapVisibility.Label(stage.Mask, axis);
        var off = stage.ParticleToggles.Where(t => !t.Value).Select(t => t.Key).ToList();
        var on = stage.ParticleToggles.Where(t => t.Value).Select(t => t.Key).ToList();

        if (brief)
            return $"Mask {stage.Mask} ({layers}); {off.Count} particle(s) off, {on.Count} on; {lighting.Text}.";

        static string Names(List<string> names) => names.Count == 0 ? "-"
            : string.Join(", ", names.Take(6)) + (names.Count > 6 ? $" (+{names.Count - 6} more)" : "");
        return $"Board stage '{stage.Label}' [{string.Join(" > ", stage.Chain)}]: visibility mask {stage.Mask} ({layers}); "
            + $"particles switched off: {Names(off)}; switched on: {Names(on)}; {lighting.Text}. "
            + $"Not emulated: {stage.UnappliedActionCount} action(s) of the chain (prop animations, sounds, lighting and fog fades) and the stencil masks.";
    }
}
