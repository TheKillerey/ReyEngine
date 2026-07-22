using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core.Build;

namespace ReyEngine.App.ViewModels;

public sealed record DeadAssetRowViewModel(string RelPath, string Size);

public sealed record OutsideMapRowViewModel(string Folder, int Files, string Size, int WadsTouched, string SampleWads);

/// <summary>
/// M136: the Asset Usage window — which project files can nothing ever load (DEAD: not shipped by
/// any game wad, referenced by no project bin), and which belong to other content entirely
/// (OUTSIDE MAP: champion/item/TFT paths — the wad fan-out drivers). Dead files are deletable
/// in one click; outside-map folders are the deliberate trim list.
/// </summary>
public sealed partial class AssetUsageWindowViewModel : ObservableObject
{
    public required string Summary { get; init; }
    public required string DeadSummary { get; init; }
    public required string OutsideSummary { get; init; }
    public required string MapScopedSummary { get; init; }

    public ObservableCollection<DeadAssetRowViewModel> Dead { get; } = new();
    public ObservableCollection<OutsideMapRowViewModel> Outside { get; } = new();
    public bool HasDead => Dead.Count > 0;
    public bool HasOutside => Outside.Count > 0;

    /// <summary>Host hook: confirm + delete all dead files, returns how many were removed.</summary>
    public Func<Task<int>>? DeleteDeadAsync { get; init; }
    public bool CanDeleteDead => DeleteDeadAsync is not null && Dead.Count > 0;

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _deadDeleted;
    public bool DeleteEnabled => !DeadDeleted;
    partial void OnDeadDeletedChanged(bool value) => OnPropertyChanged(nameof(DeleteEnabled));

    [RelayCommand]
    private async Task DeleteDead()
    {
        if (DeleteDeadAsync is null || DeadDeleted) return;
        int n;
        try { n = await DeleteDeadAsync(); }
        catch (Exception ex) { Status = $"Delete failed: {ex.Message}"; return; }
        if (n <= 0) return;   // cancelled
        DeadDeleted = true;
        Status = $"Deleted {n} dead file(s) from the project.";
    }

    public static AssetUsageWindowViewModel Build(AssetUsageReport r, Func<Task<int>>? deleteDead)
    {
        static string Mb(long b) => $"{b / 1048576.0:0.0} MB";
        var vm = new AssetUsageWindowViewModel
        {
            Summary = $"{r.TotalFiles:n0} packable file(s), {Mb(r.TotalBytes)} — map wad(s): {string.Join(", ", r.MapWads.DefaultIfEmpty("none found"))}",
            DeadSummary = $"{r.Dead.Count:n0} file(s), {Mb(r.DeadBytes)} — no game wad ships these paths and no project bin references them. Nothing can ever load them.",
            OutsideSummary = $"{r.OutsideMapFiles:n0} file(s), {Mb(r.OutsideMapBytes)} — these paths exist only in OTHER content's wads (champions/items/TFT). They are why loaders patch extra wads; keep only what you really want to recolor.",
            MapScopedSummary = $"{r.MapScopedFiles:n0} file(s), {Mb(r.MapScopedBytes)} belong to the map (or are new content the mod's bins reference).",
            DeleteDeadAsync = deleteDead,
        };
        foreach (var d in r.Dead.Take(400)) vm.Dead.Add(new DeadAssetRowViewModel(d.RelPath, Mb(d.Bytes)));
        foreach (var o in r.OutsideMap) vm.Outside.Add(new OutsideMapRowViewModel(o.Folder, o.Files, Mb(o.Bytes), o.WadsTouched, o.SampleWads));
        return vm;
    }
}
