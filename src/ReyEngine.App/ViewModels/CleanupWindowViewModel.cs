using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core.Cleanup;

namespace ReyEngine.App.ViewModels;

/// <summary>One file (or empty folder) in the cleanup preview.</summary>
public sealed partial class CleanupRowViewModel : ObservableObject
{
    public required CleanupCandidate Candidate { get; init; }
    public required Action OnSelectionChanged { get; init; }

    [ObservableProperty] private bool _isSelected;
    partial void OnIsSelectedChanged(bool value) => OnSelectionChanged();

    public string RelPath => Candidate.RelPath;
    public string Folder => Candidate.Folder;
    public string TypeName => Candidate.Group == CleanupGroup.EmptyFolder
        ? "folder"
        : (System.IO.Path.GetExtension(Candidate.RelPath).TrimStart('.').ToLowerInvariant() is { Length: > 0 } e ? e : "—");
    public string Size => CleanupWindowViewModel.FormatBytes(Candidate.Bytes);
    public string Reason => Candidate.Reason;
    public long Bytes => Candidate.Bytes;
}

public sealed partial class CleanupGroupViewModel : ObservableObject
{
    public required string Title { get; init; }
    public required string Explanation { get; init; }
    public required CleanupGroup Group { get; init; }
    public ObservableCollection<CleanupRowViewModel> Rows { get; } = new();
    [ObservableProperty] private string _summary = "";
    public bool HasRows => Rows.Count > 0;
}

/// <summary>
/// M302: Tools ▸ Mod Health ▸ Cleanup Project. Scan, preview, select, then MOVE the selection into a
/// project-local recycle area that an Undo can empty back out. The window never deletes anything itself -
/// it owns the review step, and the host owns the file operations.
/// </summary>
public sealed partial class CleanupWindowViewModel : ObservableObject
{
    public required string ProjectPath { get; init; }

    /// <summary>Host hooks. (unused, riotIdentical, emptyFolders, progress) -> report.</summary>
    public Func<bool, bool, bool, IProgress<(double, string)>, Task<CleanupReport>>? ScanAsync { get; init; }
    /// <summary>Confirm + move the selection. Returns a status line, or null if the user backed out.</summary>
    public Func<IReadOnlyList<CleanupCandidate>, Task<string?>>? CleanAsync { get; init; }
    /// <summary>Undo the most recent run. Returns a status line, or null if there was nothing to undo.</summary>
    public Func<Task<string?>>? RestoreAsync { get; init; }

    public ObservableCollection<CleanupGroupViewModel> Groups { get; } = new();
    public ObservableCollection<string> TypeFilters { get; } = new() { "All types" };

    [ObservableProperty] private string _riotStatus = "Not scanned yet.";
    [ObservableProperty] private bool _riotAvailable;
    [ObservableProperty] private string _status = "Press Scan Project to build a preview. Nothing is changed by scanning.";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _hasScanned;
    [ObservableProperty] private bool _canRestore;

    [ObservableProperty] private bool _removeUnused = true;
    [ObservableProperty] private bool _removeRiotIdentical = true;
    [ObservableProperty] private bool _includeEmptyFolders;

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _selectedTypeFilter = "All types";
    [ObservableProperty] private string _selectionSummary = "Nothing selected.";
    [ObservableProperty] private string _notes = "";

    private readonly List<CleanupRowViewModel> _all = new();
    private bool _suppressCounts;

    public bool NotBusy => !Busy;
    partial void OnBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(NotBusy));
        CleanSelectedCommand.NotifyCanExecuteChanged();   // Busy is part of CanClean, so it must re-ask
    }
    partial void OnSearchTextChanged(string value) => Regroup();
    partial void OnSelectedTypeFilterChanged(string value) => Regroup();

    /// <summary>Riot-identical scanning is only offered when a Riot reference is actually mounted;
    /// unused-file scanning always stays available.</summary>
    public bool RiotModeEnabled => RiotAvailable;
    partial void OnRiotAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(RiotModeEnabled));
        if (!value) RemoveRiotIdentical = false;
    }

    [RelayCommand]
    private async Task Scan()
    {
        if (ScanAsync is null || Busy) return;
        Busy = true;
        Status = "Scanning… no files are modified by this step.";
        try
        {
            var progress = new Progress<(double F, string S)>(p => { Progress = p.F; Status = p.S; });
            var report = await ScanAsync(RemoveUnused, RemoveRiotIdentical, IncludeEmptyFolders, progress);
            Apply(report);
        }
        catch (Exception ex) { Status = "Scan failed: " + ex.Message; }
        finally { Busy = false; Progress = 0; }
    }

    public void Apply(CleanupReport report)
    {
        RiotAvailable = report.RiotReferenceAvailable;
        RiotStatus = report.RiotStatus;
        Notes = string.Join("  ", report.Notes);

        _all.Clear();
        foreach (var c in report.Candidates)
            _all.Add(new CleanupRowViewModel
            {
                Candidate = c,
                OnSelectionChanged = UpdateCounts,
                IsSelected = c.SelectedByDefault,
            });

        var types = _all.Select(r => r.TypeName).Distinct().OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
        TypeFilters.Clear();
        TypeFilters.Add("All types");
        foreach (var t in types) TypeFilters.Add(t);
        _suppressCounts = true;
        SelectedTypeFilter = "All types";
        _suppressCounts = false;

        HasScanned = true;
        Regroup();
        Status = $"{report.FilesScanned:n0} file(s) scanned ({FormatBytes(report.BytesScanned)}). "
               + $"{report.Candidates.Count:n0} candidate(s) found. Review the selection, then Clean Selected Files.";
    }

    private void Regroup()
    {
        string q = SearchText.Trim();
        string type = SelectedTypeFilter;
        bool AllTypes = string.IsNullOrEmpty(type) || type == "All types";

        bool Match(CleanupRowViewModel r) =>
            (AllTypes || string.Equals(r.TypeName, type, StringComparison.OrdinalIgnoreCase))
            && (q.Length == 0 || r.RelPath.Contains(q, StringComparison.OrdinalIgnoreCase)
                              || r.Reason.Contains(q, StringComparison.OrdinalIgnoreCase));

        Groups.Clear();
        foreach (var g in new[] { CleanupGroup.Unused, CleanupGroup.IdenticalToRiot,
                                  CleanupGroup.EmptyFolder, CleanupGroup.Protected })
        {
            var rows = _all.Where(r => r.Candidate.Group == g && Match(r)).ToList();
            if (rows.Count == 0) continue;
            var vm = new CleanupGroupViewModel
            {
                Title = Title(g),
                Explanation = Explain(g),
                Group = g,
            };
            foreach (var r in rows) vm.Rows.Add(r);
            vm.Summary = $"{rows.Count:n0} item(s), {FormatBytes(rows.Sum(r => r.Bytes))}";
            Groups.Add(vm);
        }
        UpdateCounts();
    }

    private static string Title(CleanupGroup g) => g switch
    {
        CleanupGroup.Unused => "UNUSED FILES",
        CleanupGroup.IdenticalToRiot => "IDENTICAL TO RIOT",
        CleanupGroup.EmptyFolder => "EMPTY FOLDERS",
        _ => "PROTECTED OR UNCERTAIN",
    };

    private static string Explain(CleanupGroup g) => g switch
    {
        CleanupGroup.Unused => "No game WAD ships these paths and nothing in the project references them - "
                            + "by path, by name, by extension variant or by hash.",
        CleanupGroup.IdenticalToRiot => "These match the Riot original, so removing them makes the game fall "
                            + "back to Riot's copy and the mod behaves the same.",
        CleanupGroup.EmptyFolder => "Directories with no file at any depth.",
        _ => "Kept by project metadata, or not confidently judged. These are never selected for you - "
           + "tick one only if you know why it is safe.",
    };

    [RelayCommand]
    private void SelectAll() => SetAll(true);

    [RelayCommand]
    private void DeselectAll() => SetAll(false);

    private void SetAll(bool on)
    {
        _suppressCounts = true;
        // Only what is currently visible, and never the protected group - "select all" must not become a
        // way to arm the exact rows the scan said it was unsure about.
        foreach (var g in Groups)
        {
            if (g.Group == CleanupGroup.Protected) continue;
            foreach (var r in g.Rows) r.IsSelected = on;
        }
        _suppressCounts = false;
        UpdateCounts();
    }

    private void UpdateCounts()
    {
        if (_suppressCounts) return;
        var sel = _all.Where(r => r.IsSelected).ToList();
        long bytes = sel.Sum(r => r.Bytes);
        int protectedCount = sel.Count(r => r.Candidate.Group == CleanupGroup.Protected);
        SelectionSummary = sel.Count == 0
            ? "Nothing selected."
            : $"{sel.Count:n0} file(s) selected — {FormatBytes(bytes)} recoverable"
              + (protectedCount > 0 ? $"  ⚠ includes {protectedCount:n0} protected/uncertain item(s)" : "");
        CleanSelectedCommand.NotifyCanExecuteChanged();
    }

    private bool CanClean() => !Busy && _all.Any(r => r.IsSelected);

    [RelayCommand(CanExecute = nameof(CanClean))]
    private async Task CleanSelected()
    {
        if (CleanAsync is null) return;
        var picked = _all.Where(r => r.IsSelected).Select(r => r.Candidate).ToList();
        if (picked.Count == 0) return;

        Busy = true;
        try
        {
            var result = await CleanAsync(picked);
            if (result is null) { Status = "Cleanup cancelled — nothing was moved."; return; }
            Status = result;
            CanRestore = true;
            // Rescan so the window shows the project as it now is, and the user can see the result took.
            if (ScanAsync is not null)
            {
                var progress = new Progress<(double F, string S)>(p => Progress = p.F);
                Apply(await ScanAsync(RemoveUnused, RemoveRiotIdentical, IncludeEmptyFolders, progress));
                Status = result + "  Rescanned: " + (_all.Count == 0
                    ? "nothing left to clean."
                    : $"{_all.Count:n0} candidate(s) remain.");
            }
        }
        catch (Exception ex) { Status = "Cleanup failed: " + ex.Message; }
        finally { Busy = false; Progress = 0; }
    }

    [RelayCommand]
    private async Task Restore()
    {
        if (RestoreAsync is null || Busy) return;
        Busy = true;
        try
        {
            var msg = await RestoreAsync();
            Status = msg ?? "Nothing to restore.";
            if (msg is not null && ScanAsync is not null)
            {
                var progress = new Progress<(double F, string S)>(p => Progress = p.F);
                Apply(await ScanAsync(RemoveUnused, RemoveRiotIdentical, IncludeEmptyFolders, progress));
                Status = msg;
            }
        }
        catch (Exception ex) { Status = "Restore failed: " + ex.Message; }
        finally { Busy = false; Progress = 0; }
    }

    public static string FormatBytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.00} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{b / (double)(1L << 10):0.0} KB",
        _ => $"{b} B",
    };
}
