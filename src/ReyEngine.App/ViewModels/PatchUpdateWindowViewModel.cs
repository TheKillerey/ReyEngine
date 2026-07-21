using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core.Assets;
using ReyEngine.Formats.Meta;

namespace ReyEngine.App.ViewModels;

/// <summary>One project bin in the wizard: include it, watch its status move through the pipeline.</summary>
public sealed partial class PatchUpdateBinRowViewModel : ObservableObject
{
    public required string Rel { get; init; }
    public required WadAssetEntry Entry { get; init; }
    [ObservableProperty] private bool _include = true;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _detail = "";
    public bool HasDetail => Detail.Length > 0;
    partial void OnDetailChanged(string value) => OnPropertyChanged(nameof(HasDetail));
}

/// <summary>
/// M97c: the Patch Update wizard — rebase every project .bin from the patch the mod was built for
/// onto the current patch. Per bin: OLD original from CommunityDragon's patch archive, MOD = the
/// project's current bytes, NEW base = the untouched Riot original of the running game; three-way
/// merge (M97a) carries only the mod's actual edits onto the new base. Originals are backed up to
/// .reyengine/backups/ before anything is overwritten, and Validate (M97b/M127) runs at the end.
/// </summary>
public sealed partial class PatchUpdateWindowViewModel : ObservableObject
{
    public ObservableCollection<string> Patches { get; } = new();
    [ObservableProperty] private string? _selectedPatch;
    [ObservableProperty] private string _status = "Loading the patch list from CommunityDragon…";
    [ObservableProperty] private bool _running;
    [ObservableProperty] private bool _validateAfter = true;
    [ObservableProperty] private bool _patchesLoaded;

    public ObservableCollection<PatchUpdateBinRowViewModel> Bins { get; } = new();
    public bool CanRun => PatchesLoaded && !Running && SelectedPatch is not null;
    partial void OnRunningChanged(bool value) => OnPropertyChanged(nameof(CanRun));
    partial void OnPatchesLoadedChanged(bool value) => OnPropertyChanged(nameof(CanRun));
    partial void OnSelectedPatchChanged(string? value) => OnPropertyChanged(nameof(CanRun));

    // host hooks (wired by MainWindowViewModel)
    public Func<Task<IReadOnlyList<string>>>? ListPatches;
    public Func<string, string, Task<byte[]?>>? DownloadOld;          // (patch, rel) -> old original
    public Func<WadAssetEntry, byte[]?>? ReadCurrentOriginal;         // untouched Riot original, current patch
    public Func<ulong, byte[]>? ReadProjectBytes;                     // the mod's bytes (merged view)
    public Func<WadAssetEntry, byte[], Task<bool>>? SaveBytes;        // in-place project save
    public Func<string, byte[], string?>? Backup;                     // (rel, bytes) -> backup file
    public Func<Task>? RunValidate;
    public Func<uint, string?>? Resolve;

    public async Task InitAsync()
    {
        if (ListPatches is null) return;
        try
        {
            foreach (var p in await ListPatches()) Patches.Add(p);
            PatchesLoaded = true;
            Status = $"{Patches.Count} patches available. Pick the patch the mod was BUILT for (its last known working patch), then Run.";
        }
        catch (Exception ex)
        {
            Status = $"Could not reach CommunityDragon: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task Run()
    {
        if (SelectedPatch is not { } patch || Running) return;
        if (DownloadOld is null || ReadCurrentOriginal is null || ReadProjectBytes is null || SaveBytes is null) return;
        Running = true;
        try
        {
            var rows = Bins.Where(b => b.Include).ToList();
            int done = 0, merged = 0, replaced = 0, skipped = 0, failed = 0, conflicts = 0;
            foreach (var row in rows)
            {
                Status = $"Updating {++done}/{rows.Count}: {row.Rel}";
                try
                {
                    row.Status = "⏳ old original…";
                    var old = await DownloadOld(patch, row.Rel);
                    if (old is null)
                    {
                        row.Status = "− skipped";
                        row.Detail = $"Patch {patch} has no file at this path on CommunityDragon — a mod-only file (nothing to rebase), or the path didn't exist back then.";
                        skipped++;
                        continue;
                    }
                    var newBase = ReadCurrentOriginal(row.Entry);
                    if (newBase is null)
                    {
                        row.Status = "− skipped";
                        row.Detail = "The current game has no file at this path — likely unused now (Validate flags these; consider deleting the bin).";
                        skipped++;
                        continue;
                    }
                    var mod = ReadProjectBytes(row.Entry.PathHash);

                    Backup?.Invoke(row.Rel, mod);

                    if (mod.AsSpan().SequenceEqual(old))
                    {
                        // the mod ships this file VERBATIM from the old patch — no edits to carry,
                        // the correct rebase is simply the current original
                        if (!await SaveBytes(row.Entry, newBase)) { row.Status = "✗ failed"; row.Detail = "Save failed — see the console log."; failed++; continue; }
                        row.Status = "✓ replaced";
                        row.Detail = "The mod never edited this bin (byte-identical to the old original) — replaced with the current patch's version.";
                        replaced++;
                        continue;
                    }

                    row.Status = "⏳ merging…";
                    var (mergedBytes, report) = await Task.Run(() => BinThreeWayMerge.Merge(old, mod, newBase, Resolve));
                    if (!await SaveBytes(row.Entry, mergedBytes)) { row.Status = "✗ failed"; row.Detail = "Save failed — see the console log."; failed++; continue; }

                    conflicts += report.Conflicts;
                    row.Status = report.Conflicts > 0 ? $"⚠ merged, {report.Conflicts} conflict(s)" : "✓ merged";
                    var parts = new List<string>
                    {
                        $"{report.ModAdded} added / {report.ModRemoved} removed / {report.ModModified} modified object(s) carried onto the current patch ({report.NewBaseObjects} base objects)."
                    };
                    parts.AddRange(report.ConflictDetails.Take(3));
                    if (report.ConflictDetails.Count > 3) parts.Add($"… {report.ConflictDetails.Count - 3} more conflict(s)");
                    row.Detail = string.Join(Environment.NewLine, parts);
                    merged++;
                }
                catch (Exception ex)
                {
                    row.Status = "✗ failed";
                    row.Detail = ex.Message;
                    failed++;
                }
            }

            Status = $"Done: {merged} merged, {replaced} replaced, {skipped} skipped, {failed} failed"
                + (conflicts > 0 ? $", {conflicts} conflict(s) — mod version kept, check the details" : "")
                + ". Originals are in .reyengine/backups/.";

            if (ValidateAfter && RunValidate is not null && failed + merged + replaced > 0)
                await RunValidate();
        }
        finally { Running = false; }
    }

    [RelayCommand] private void IncludeAll() { foreach (var b in Bins) b.Include = true; }
    [RelayCommand] private void IncludeNone() { foreach (var b in Bins) b.Include = false; }
}
