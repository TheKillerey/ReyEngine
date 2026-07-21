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
}

/// <summary>
/// M125: the Bin Issues window — every repair the tolerant reader applied while loading a malformed
/// .bin (duplicate fields/objects, unreadable tails). Each row names the object it lives in, offers
/// a jump to it, and the footer repairs the file in one click (any save writes the healed form).
/// </summary>
public sealed partial class BinIssuesWindowViewModel : ObservableObject
{
    public required string BinName { get; init; }
    public ObservableCollection<BinIssueRowViewModel> Rows { get; } = new();

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
