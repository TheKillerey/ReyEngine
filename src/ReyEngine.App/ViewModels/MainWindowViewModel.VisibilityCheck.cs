using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ReyEngine.Core.Assets;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.App.ViewModels;

/// <summary>M849: one line of the map check under Visibility Layers.</summary>
public sealed class VisibilityFindingViewModel
{
    public VisibilityFindingViewModel(VisibilityFinding finding)
    {
        Finding = finding;
        Text = $"{(finding.Severity == VisibilityFindingSeverity.Error ? "Never visible" : finding.Severity == VisibilityFindingSeverity.Warning ? "Warning" : "Note")}: {finding.Message}";
    }

    public VisibilityFinding Finding { get; }
    public string Text { get; }
    public bool IsError => Finding.Severity == VisibilityFindingSeverity.Error;
    public bool IsNotError => !IsError;
}

/// <summary>
/// M849: map visibility controllers the way the game evaluates them, and the check that says where the map relies on something the
/// game does not do. A controller graph is a boolean tree (<see cref="MapVisibilityControllers.Evaluate"/>); a terrain controller with
/// the Base bit can never show, because the game has no Base terraform. The editor used to be kinder - it counted the Base bit and
/// read every ParentMode but 3 as a union - so a map could look right here and be empty in the game. The old reading stays behind
/// the "lenient" switch, off by default.
/// </summary>
public sealed partial class MainWindowViewModel
{
    public ObservableCollection<VisibilityFindingViewModel> VisibilityFindings { get; } = new();

    [NotifyPropertyChangedFor(nameof(ShowVisibilityLayers))]
    [ObservableProperty] private bool _hasVisibilityFindings;

    [ObservableProperty] private string _visibilityFindingsHeader = "";

    /// <summary>Evaluate controller graphs as the old editor did (a union of the parents, the Base bit always on) instead of as the
    /// game does. Session only, off by default: the editor must not be kinder than the game.</summary>
    [ObservableProperty] private bool _lenientVisibility;

    partial void OnLenientVisibilityChanged(bool value)
    {
        if (_mapControllers is null) return;
        _mapControllers.LenientEvaluation = value;
        if (!ReferenceEquals(_mapStartControllers, _mapControllers) && _mapStartControllers is not null) _mapStartControllers.LenientEvaluation = value;
        _log.Info("Visibility", value
            ? "Lenient controller evaluation ON: a graph is read as a union of its parents and the Base bit counts - NOT what the game does."
            : "Controller graphs are evaluated as the game does (ParentMode 0 ALL / 1 ANY / 2 ONE / 3 NONE; the Base bit never counts for a terrain controller).");
        ApplyMapVisibility();
    }

    /// <summary>Run the map check on the open map's controllers and show what it finds, without anyone having to search for it.</summary>
    private void RunVisibilityCheck(MapGeoAsset map, WadAssetEntry? materialsEntry)
    {
        VisibilityFindings.Clear();
        HasVisibilityFindings = false;
        VisibilityFindingsHeader = "";
        if (_mapControllers is not { Count: > 0 } controllers) return;

        MapVisibilityControllers? reference = null;
        try
        {
            // Riot's own copy of the map's controller bin, when the project mod overrides it: a Riot controller with other bits is named
            if (materialsEntry is not null && _mounts is { } mounts
                && mounts.SourcesOf(materialsEntry.PathHash).FirstOrDefault(m => m.Kind == AssetSourceKind.RiotReference) is { } riot)
                reference = MapVisibilityControllers.Build(new[] { riot.Read(materialsEntry.PathHash) }, _mapVisibility);
        }
        catch (Exception ex) { _log.Warn("Visibility", $"Riot's controller bin could not be read for the map check: {ex.Message}"); }

        var findings = MapVisibilityDiagnostics.Analyze(controllers, map.Meshes.Select(m => m.EffectiveController), reference, ResolveBinName);
        foreach (var f in findings) VisibilityFindings.Add(new VisibilityFindingViewModel(f));
        HasVisibilityFindings = findings.Count > 0;
        int errors = findings.Count(f => f.Severity == VisibilityFindingSeverity.Error);
        int warnings = findings.Count(f => f.Severity == VisibilityFindingSeverity.Warning);
        VisibilityFindingsHeader = findings.Count == 0 ? "" : $"MAP CHECK: {errors} never visible, {warnings} warning(s), {findings.Count - errors - warnings} note(s)";
        foreach (var f in findings)
            if (f.Severity == VisibilityFindingSeverity.Info) _log.Info("Visibility", f.Message);
            else _log.Warn("Visibility", f.Message);
    }
}
