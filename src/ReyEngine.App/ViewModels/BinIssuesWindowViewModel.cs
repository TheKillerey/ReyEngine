using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ReyEngine.App.ViewModels;

/// <summary>One repaired problem, resolved to display names, with optional navigation to the object.</summary>
public sealed partial class BinIssueRowViewModel : ObservableObject
{
    public required string Kind { get; init; }
    public required string ObjectName { get; init; }
    public required string ClassName { get; init; }
    public string? FieldName { get; init; }
    public bool HasField => !string.IsNullOrEmpty(FieldName);
    public required string Message { get; init; }
    public required string Suggestion { get; init; }

    /// <summary>Jumps to the object in its editor (materials list / particle tree); null when the
    /// object isn't shown anywhere (e.g. a class ReyEngine doesn't list).</summary>
    public Action? GoTo { get; init; }
    public bool CanGoTo => GoTo is not null;
    [RelayCommand] private void Navigate() => GoTo?.Invoke();

    // ---- M127: per-row one-click fix (e.g. repoint a dead skin-variant ref to its base file) ----
    public string? FixLabel { get; init; }
    public Func<Task<bool>>? FixAsync { get; init; }
    public bool HasFix => FixAsync is not null;
    [ObservableProperty] private bool _fixApplied;
    public bool FixEnabled => !FixApplied;
    partial void OnFixAppliedChanged(bool value) => OnPropertyChanged(nameof(FixEnabled));
    [ObservableProperty] private string _fixStatus = "";

    [RelayCommand]
    private async Task ApplyFix()
    {
        if (FixAsync is null || FixApplied) return;
        FixStatus = "Fixing…";
        bool ok = false;
        try { ok = await FixAsync(); }
        catch (Exception ex) { FixStatus = $"Fix failed: {ex.Message}"; return; }
        FixApplied = ok;
        FixStatus = ok ? "Fixed — saved to the project." : "Fix failed — see the console log.";
    }
}

/// <summary>M128: one .bin's issues as a group — header with the file name, its rows, and an
/// optional Delete (drop the bin from the project entirely; the game falls back to the original).</summary>
public sealed partial class BinIssueGroupViewModel : ObservableObject
{
    public required string BinName { get; init; }
    public ObservableCollection<BinIssueRowViewModel> Rows { get; } = new();
    public string CountLabel => $"{Rows.Count} issue(s)";

    /// <summary>Host hook: delete this bin from the project (confirm + file + shadow override).
    /// Null when deleting makes no sense (the bin is open in an editor).</summary>
    public Func<Task<bool>>? DeleteAsync { get; init; }
    public bool CanDelete => DeleteAsync is not null;
    [ObservableProperty] private bool _deleted;
    public bool NotDeleted => !Deleted;
    partial void OnDeletedChanged(bool value) => OnPropertyChanged(nameof(NotDeleted));
    [ObservableProperty] private string _deleteStatus = "";
    public bool HasDeleteStatus => DeleteStatus.Length > 0;
    partial void OnDeleteStatusChanged(string value) => OnPropertyChanged(nameof(HasDeleteStatus));

    [RelayCommand]
    private async Task Delete()
    {
        if (DeleteAsync is null || Deleted) return;
        bool ok;
        try { ok = await DeleteAsync(); }
        catch (Exception ex) { DeleteStatus = $"Delete failed: {ex.Message}"; return; }
        if (!ok) return;   // cancelled, or failed (logged by the host)
        Deleted = true;
        DeleteStatus = "Deleted from the project — the game will use the original file instead.";
    }
}

/// <summary>
/// M125: the Bin Issues window — every repair the tolerant reader applied while loading a malformed
/// .bin (duplicate fields/objects, unreadable tails). Each row names the object it lives in, offers
/// a jump to it, and the footer repairs the file in one click (any save writes the healed form).
/// M127 reuses it for validation results; M128 groups the rows per .bin file with per-bin Delete.
/// </summary>
public sealed partial class BinIssuesWindowViewModel : ObservableObject
{
    public required string BinName { get; init; }

    /// <summary>Intro text under the title — defaults to the tolerant-repair story (M125); the
    /// validation window (M127) sets its own.</summary>
    public string Description { get; init; } =
        "These problems were found (and worked around) while reading this bin. The file still loads in ReyEngine, "
        + "but strict tools reject it outright — the classic 'an item with the same key has already been added' error. "
        + "Affected entries are marked red in the materials list and the particle tree.";

    public ObservableCollection<BinIssueGroupViewModel> Groups { get; } = new();

    /// <summary>Host hook: save the tolerantly-parsed (= already healed) tree back through the normal
    /// save pipeline. Null when the source is a read-only Riot reference.</summary>
    public Func<Task<bool>>? RepairAsync { get; init; }
    public bool CanRepair => RepairAsync is not null;

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _repaired;
    public bool RepairEnabled => !Repaired;
    partial void OnRepairedChanged(bool value) => OnPropertyChanged(nameof(RepairEnabled));

    [RelayCommand]
    private async Task Repair()
    {
        if (RepairAsync is null || Repaired) return;
        Status = "Repairing…";
        bool ok = false;
        try { ok = await RepairAsync(); }
        catch (Exception ex) { Status = $"Repair failed: {ex.Message}"; return; }
        Repaired = ok;
        Status = ok
            ? "Repaired — the file was rewritten without the problems above."
            : "Repair failed — see the console log.";
    }
}
