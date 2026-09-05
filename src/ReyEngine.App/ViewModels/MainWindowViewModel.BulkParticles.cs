using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M644: bulk editing of placed particles - the host half of the shared inspector.
///
/// <para>The outliner has multi-selected since M331 (Ctrl toggles, Shift ranges) and the selection bar
/// could hide, disable and delete a selection as a whole; but the PARTICLE card only ever edited the last
/// placement clicked, so re-pointing forty placements at another system was forty picks. Now two or more
/// selected particles show a PARTICLES card instead: a field shows its value when every placement agrees
/// and says "mixed" when they differ, and an edit writes all of them as one undo step. The arithmetic is
/// <see cref="ParticleBatch"/>; this file is the wiring to the selection, the picker and the viewport.</para>
/// </summary>
public sealed partial class MainWindowViewModel
{
    private readonly List<ParticlePlacementViewModel> _selectedParticles = new();

    /// <summary>The particles in the map-content selection, in selection order.</summary>
    public IReadOnlyList<ParticlePlacementViewModel> SelectedParticles => _selectedParticles;

    /// <summary>Two or more particles selected: the batch card shows and the single card hides.</summary>
    [ObservableProperty] private bool _isParticleMultiSelect;

    /// <summary>"3 particles selected · 2 systems · 1 with pending edits".</summary>
    [ObservableProperty] private string _particleBatchStatus = "";

    /// <summary>The system every selected placement links to, or null when they differ (the label says so).</summary>
    [ObservableProperty] private VfxSystemItemViewModel? _batchRelinkChoice;
    [ObservableProperty] private string _batchRelinkLabel = "";

    /// <summary>The tint text every selected placement shares, or "" when they differ or are all authored-default.</summary>
    [ObservableProperty] private string _batchTint = "";
    [ObservableProperty] private string _batchTintLabel = "";
    [ObservableProperty] private bool _batchTintInvalid;

    private bool _refreshingParticleBatch;

    /// <summary>The single PARTICLE card: one placement selected, and not as part of a batch.</summary>
    public bool ShowSingleParticleCard => SelectedParticleNode is not null && !IsParticleMultiSelect;

    partial void OnIsParticleMultiSelectChanged(bool value) => OnPropertyChanged(nameof(ShowSingleParticleCard));

    /// <summary>Re-derive the batch from the map-content selection. Called whenever that selection is set.</summary>
    private void RefreshParticleBatch()
    {
        _selectedParticles.Clear();
        _selectedParticles.AddRange(_mapContentSelection.OfType<ParticlePlacementViewModel>());
        OnPropertyChanged(nameof(SelectedParticles));
        IsParticleMultiSelect = _selectedParticles.Count >= 2;
        if (!IsParticleMultiSelect)
        {
            ParticleBatchStatus = ""; BatchRelinkLabel = ""; BatchTintLabel = ""; BatchTintInvalid = false;
            return;
        }

        var summary = ParticleBatch.Summarize(_selectedParticles);
        ParticleBatchStatus = summary.Status;
        _refreshingParticleBatch = true;
        try
        {
            BatchRelinkChoice = summary.SharedSystemHash is { } shared ? _relinkAll.FirstOrDefault(c => c.Hash == shared) : null;
            BatchRelinkLabel = summary.SharedSystemHash is { } h
                ? (_relinkAll.FirstOrDefault(c => c.Hash == h)?.Name ?? _selectedParticles[0].SystemName)
                : $"— mixed: {summary.SystemHashes.Count} systems —";
            BatchTint = summary.SharedTintText ?? "";
            BatchTintLabel = summary.TintMixed ? "— mixed —" : summary.SharedTintText is { Length: > 0 } ? "" : "authored (1, 1, 1, 1)";
            BatchTintInvalid = false;
        }
        finally { _refreshingParticleBatch = false; }
    }

    /// <summary>After a batch edit (or its undo): the save gate, the markers, the playback and the cards.</summary>
    private void AfterParticleBatch()
    {
        HasParticleMoves = MapContent.AllParticles.Any(v => v.HasEdits) || MapContent.Sounds.Any(s => s.IsMoved);
        UpdateParticleMarkers();
        RebuildParticlePlayback();
        if (SelectedParticleNode is { } node)
        {
            RefreshParticleMoveFields(node);
            SyncRelinkPicker(node);
            SelectedParticleMarker = node.CurrentPosition;
        }
        RefreshParticleBatch();
    }

    partial void OnBatchRelinkChoiceChanged(VfxSystemItemViewModel? value)
    {
        if (_refreshingParticleBatch || value is null || !IsParticleMultiSelect) return;
        var command = ParticleBatch.Relink(_selectedParticles.ToList(), value.Hash, _currentMap, AfterParticleBatch);
        AfterParticleBatch();
        if (command.Count == 0) return;
        UndoService.PushApplied(command);
        _log.Info("Particles", $"{command.Count} placement(s) re-linked to {value.Name}. Save to Mod writes it.");
    }

    [RelayCommand]
    private void ApplyBatchTint()
    {
        if (!IsParticleMultiSelect) return;
        if (!ParticleBatch.IsValidTint(BatchTint)) { BatchTintInvalid = true; return; }
        BatchTintInvalid = false;
        var command = ParticleBatch.Tint(_selectedParticles.ToList(), BatchTint, _currentMap, AfterParticleBatch);
        AfterParticleBatch();
        if (command.Count == 0) return;
        UndoService.PushApplied(command);
        _log.Info("Particles", string.IsNullOrWhiteSpace(BatchTint)
            ? $"Tint cleared on {command.Count} placement(s)."
            : $"Tint {BatchTint.Trim()} set on {command.Count} placement(s).");
    }

    [RelayCommand]
    private void ResetSelectedParticleEdits()
    {
        if (!IsParticleMultiSelect) return;
        var command = ParticleBatch.Reset(_selectedParticles.ToList(), _currentMap, AfterParticleBatch);
        AfterParticleBatch();
        if (command.Count == 0) { _log.Info("Particles", "No pending edits on the selection."); return; }
        UndoService.PushApplied(command);
        _log.Info("Particles", $"Pending edits discarded on {command.Count} placement(s).");
    }

    /// <summary>Select every placement of one system - the outliner group's button. The natural first step
    /// of "change the link on all of these".</summary>
    [RelayCommand]
    private void SelectParticleGroup(ParticleSystemGroupViewModel? group)
    {
        if (group is null || group.Placements.Count == 0) return;
        SelectMapContentItems(group.Placements);
    }

    /// <summary>A whole selection at once, the way <see cref="SelectMapContentFromTree"/> builds one click
    /// by click: the map-content list, the mesh selection (cleared - these are placements), and the
    /// outliner's primary, which is what plays the system and drives the single-placement fields.</summary>
    public void SelectMapContentItems(IEnumerable<MapOutlinerItemViewModel> items)
    {
        var list = items.Distinct().ToList();
        if (list.Count == 0) return;
        SetMapContentSelection(list, list[0]);
        _outlinerMultiSelecting = true;
        try
        {
            _selection.Clear();
            SelectedOutlinerItem = list[0];
        }
        finally { _outlinerMultiSelecting = false; }
    }
}
