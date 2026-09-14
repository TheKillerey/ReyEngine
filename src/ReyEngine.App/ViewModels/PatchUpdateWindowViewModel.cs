using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.Meta;

namespace ReyEngine.App.ViewModels;

/// <summary>One project bin in the updater: include it and show its live transactional status.</summary>
public sealed partial class PatchUpdateBinRowViewModel : ObservableObject
{
    public required string Rel { get; init; }
    public required WadAssetEntry Entry { get; init; }
    public string ProjectRel { get; init; } = "";
    [ObservableProperty] private bool _include = true;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _detail = "";
    public bool HasDetail => Detail.Length > 0;
    partial void OnDetailChanged(string value) => OnPropertyChanged(nameof(HasDetail));
}

public sealed record PatchUpdateRunResult(
    int Merged, int Replaced, int Skipped, int Failed, int Conflicts, int Updated,
    bool RolledBack, bool RollbackFailed, bool ValidationFailed, string Summary)
{
    public bool Success => Failed == 0 && !RollbackFailed;
    public bool NeedsReview => Skipped > 0 || Conflicts > 0 || Failed > 0 || RollbackFailed || ValidationFailed;
}

/// <summary>
/// Rebases project bins from their recorded Riot patch onto the installed patch. Every output is prepared
/// before any write, every changed file is backed up, and a failed write rolls the batch back. This makes
/// the same engine safe for the manual wizard and the automatic on-project-open workflow.
/// </summary>
public sealed partial class PatchUpdateWindowViewModel : ObservableObject
{
    public ObservableCollection<string> Patches { get; } = new();
    [ObservableProperty] private string? _selectedPatch;
    [ObservableProperty] private string? _targetPatch;
    [ObservableProperty] private string _status = "Loading the patch list from CommunityDragon...";
    [ObservableProperty] private bool _running;
    [ObservableProperty] private bool _validateAfter = true;
    [ObservableProperty] private bool _patchesLoaded;
    [ObservableProperty] private int _retainedAssetCount;
    [ObservableProperty] private bool _runFinished;

    public ObservableCollection<PatchUpdateBinRowViewModel> Bins { get; } = new();
    public PatchUpdateRunResult? Result { get; private set; }
    public string? BackupDirectory { get; set; }

    public bool CanRun => PatchesLoaded && !Running && !RunFinished
        && SelectedPatch is { } baseline && TargetPatch is { } target
        && Patches.Contains(baseline) && RiotPatchVersionDetector.Compare(baseline, target) < 0;
    public string PatchRoute => SelectedPatch is null
        ? (TargetPatch is null ? "Patch baseline not selected" : $"Unknown -> {TargetPatch}")
        : TargetPatch is null ? SelectedPatch : $"{SelectedPatch} -> {TargetPatch}";
    public string RetainedAssetsText => RetainedAssetCount == 0
        ? "Every project asset in this update is eligible for semantic rebasing."
        : $"{RetainedAssetCount:n0} other project asset(s) remain authored replacements and will be repacked unchanged.";

    partial void OnRunningChanged(bool value) => OnPropertyChanged(nameof(CanRun));
    partial void OnPatchesLoadedChanged(bool value) => OnPropertyChanged(nameof(CanRun));
    partial void OnRunFinishedChanged(bool value) => OnPropertyChanged(nameof(CanRun));
    partial void OnSelectedPatchChanged(string? value)
    { OnPropertyChanged(nameof(CanRun)); OnPropertyChanged(nameof(PatchRoute)); }
    partial void OnTargetPatchChanged(string? value)
    { OnPropertyChanged(nameof(CanRun)); OnPropertyChanged(nameof(PatchRoute)); }
    partial void OnRetainedAssetCountChanged(int value) => OnPropertyChanged(nameof(RetainedAssetsText));

    // Host hooks wired by MainWindowViewModel.
    public Func<Task<IReadOnlyList<string>>>? ListPatches;
    public Func<string, string, Task<byte[]?>>? DownloadOld;
    public Func<WadAssetEntry, byte[]?>? ReadCurrentOriginal;
    public Func<ulong, byte[]>? ReadProjectBytes;
    public Func<WadAssetEntry, byte[], Task<bool>>? SaveBytes;
    public Func<PatchUpdateBinRowViewModel, byte[], string?>? Backup;
    public Func<Task>? RunValidate;
    public Func<uint, string?>? Resolve;
    public Func<PatchUpdateRunResult, Task>? RunCompleted;

    // M730: recipes. A bin the Map Skin Switcher made is not diffed across the patch: its recorded switch is done
    // again on the new original (BinRecipeRebase), so slots the patch added are forced too. A host that keeps
    // recipes sets RecipeFor; a bin without one that looks like a switch gets its recipe inferred and handed back.
    public Func<string, BinRecipeRecord?>? RecipeFor;
    public Action<string, BinRecipeRecord>? RecipeInferred;

    public async Task InitAsync()
    {
        if (ListPatches is null) return;
        try
        {
            Patches.Clear();
            foreach (var p in await ListPatches()) Patches.Add(p);
            PatchesLoaded = true;
            if (TargetPatch is null)
                Status = "The installed Riot patch could not be detected. No project files can be updated safely.";
            else if (SelectedPatch is { } baseline && !Patches.Contains(baseline))
                Status = $"The stored base patch {baseline} is not available on CommunityDragon. No project files were changed.";
            else if (SelectedPatch is { } selected && RiotPatchVersionDetector.Compare(selected, TargetPatch) >= 0)
                Status = $"Choose a project base patch older than the installed Riot patch {TargetPatch}.";
            else
                Status = $"{Patches.Count} patches available. Confirm the patch the mod was built for, then run.";
        }
        catch (Exception ex)
        {
            Status = $"Could not reach CommunityDragon: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task Run() => await RunUpdateAsync();

    private sealed record PendingWrite(PatchUpdateBinRowViewModel Row, byte[] Original, byte[] Updated,
        string SuccessStatus, string Detail);

    /// <summary>Run one atomic update batch and return a machine-readable result for automatic builds.</summary>
    public async Task<PatchUpdateRunResult?> RunUpdateAsync()
    {
        if (SelectedPatch is not { } patch || Running) return null;
        if (DownloadOld is null || ReadCurrentOriginal is null || ReadProjectBytes is null || SaveBytes is null)
            return null;
        Running = true;
        try
        {
            Result = null;
            RunFinished = false;
            var rows = Bins.Where(b => b.Include).ToList();
            int done = 0, merged = 0, replaced = 0, skipped = 0, failed = 0, conflicts = 0;
            var pending = new List<PendingWrite>();

            // Phase 1: network reads and merges only. A bad file cannot leave a partial update behind.
            foreach (var row in rows)
            {
                Status = $"Preparing {++done}/{rows.Count}: {row.Rel}";
                try
                {
                    row.Status = "checking old original...";
                    var old = await DownloadOld(patch, row.Rel);
                    if (old is null)
                    {
                        row.Status = "retained";
                        row.Detail = $"Patch {patch} has no file at this path on CommunityDragon - it is mod-only or did not exist then.";
                        skipped++;
                        continue;
                    }
                    var newBase = ReadCurrentOriginal(row.Entry);
                    if (newBase is null)
                    {
                        row.Status = "retained";
                        row.Detail = "The installed game has no file at this path. The authored replacement was retained; validation may flag it as unused.";
                        skipped++;
                        continue;
                    }
                    var mod = ReadProjectBytes(row.Entry.PathHash);

                    if (mod.AsSpan().SequenceEqual(old))
                    {
                        pending.Add(new(row, mod, newBase, "updated",
                            "The project file was byte-identical to the old Riot original, so it was replaced with the installed version."));
                        replaced++;
                        continue;
                    }

                    // M730: a bin with a recipe is re-DONE on the new original rather than diffed across.
                    string? recipeFailure = null;
                    if (ResolveRecipe(row.Rel, mod, old, patch) is { } recipe)
                    {
                        row.Status = "re-applying...";
                        try
                        {
                            var rebase = await Task.Run(() => BinRecipeRebase.Rebase(recipe.Recipe, old, mod, newBase, Resolve));
                            conflicts += rebase.Conflicts;
                            var recipeParts = new List<string>
                            {
                                (recipe.Inferred ? "Recipe read out of the file and remembered for next time: " : "Recorded recipe: ")
                                + recipe.Recipe.Describe() + ".",
                            };
                            recipeParts.AddRange(rebase.Lines);
                            string status = rebase.MergedRemainder ? "re-applied + merged" : "re-applied";
                            if (rebase.Conflicts > 0) status += $", {rebase.Conflicts} to review";
                            pending.Add(new(row, mod, rebase.Bytes, status, string.Join(Environment.NewLine, recipeParts)));
                            merged++;
                            continue;
                        }
                        catch (Exception ex)
                        {
                            // The structural merge still carries the old result; it just cannot force new slots.
                            recipeFailure = $"The recipe ({recipe.Recipe.Describe()}) could not be re-applied on this patch: {ex.Message} "
                                            + "The structural merge was used instead, which cannot force slots this patch added.";
                            conflicts++;
                        }
                    }

                    row.Status = "merging...";
                    var (mergedBytes, report) = await Task.Run(() => BinThreeWayMerge.Merge(old, mod, newBase, Resolve));
                    conflicts += report.Conflicts;
                    var parts = new List<string>
                    {
                        $"{report.ModAdded} added / {report.ModRemoved} removed / {report.ModModified} modified object(s) carried onto the installed patch ({report.NewBaseObjects} base objects)."
                    };
                    if (recipeFailure is not null) parts.Insert(0, recipeFailure);
                    parts.AddRange(report.ConflictDetails.Take(3));
                    if (report.ConflictDetails.Count > 3) parts.Add($"... {report.ConflictDetails.Count - 3} more conflict(s)");
                    pending.Add(new(row, mod, mergedBytes,
                        report.Conflicts > 0 ? $"merged, {report.Conflicts} conflict(s)" : "merged",
                        string.Join(Environment.NewLine, parts)));
                    merged++;
                }
                catch (Exception ex)
                {
                    row.Status = "failed";
                    row.Detail = ex.Message;
                    failed++;
                }
            }

            if (failed > 0)
            {
                Status = $"Update aborted during preparation: {failed} file(s) failed. No project files were changed.";
                return Result = new(merged, replaced, skipped, failed, conflicts, 0, false, false, false, Status);
            }

            // Phase 2: a complete backup must exist before the first write.
            Status = $"Backing up {pending.Count:n0} file(s)...";
            if (Backup is not null)
                foreach (var write in pending)
                    if (Backup(write.Row, write.Original) is null)
                    {
                        write.Row.Status = "backup failed";
                        write.Row.Detail = "The update was aborted before any writes because this file could not be backed up.";
                        Status = "Update aborted: a backup failed. No project files were changed.";
                        return Result = new(merged, replaced, skipped, 1, conflicts, 0, false, false, false, Status);
                    }

            // Phase 3: commit. Retain originals in memory so a failed save can restore prior writes.
            var applied = new List<PendingWrite>();
            foreach (var write in pending)
            {
                Status = $"Writing {applied.Count + 1}/{pending.Count}: {write.Row.Rel}";
                if (!await SaveBytes(write.Row.Entry, write.Updated))
                {
                    write.Row.Status = "save failed";
                    write.Row.Detail = "Save failed; files already written in this batch are being restored.";
                    bool rollbackFailed = false;
                    foreach (var prior in applied.AsEnumerable().Reverse())
                    {
                        if (!await SaveBytes(prior.Row.Entry, prior.Original)) rollbackFailed = true;
                        else prior.Row.Status = "rolled back";
                    }
                    Status = rollbackFailed
                        ? "Update failed and at least one rollback failed. Restore the files from the backup directory."
                        : "Update failed; every earlier write was rolled back. The project still targets the old patch.";
                    return Result = new(merged, replaced, skipped, 1, conflicts, applied.Count, true, rollbackFailed, false, Status);
                }
                write.Row.Status = write.SuccessStatus;
                write.Row.Detail = write.Detail;
                applied.Add(write);
            }

            Status = $"Done: {merged} merged, {replaced} updated, {skipped} retained, {conflicts} conflict(s)."
                + (BackupDirectory is { Length: > 0 } ? $" Backup: {BackupDirectory}" : "");
            bool validationFailed = false;
            if (ValidateAfter && RunValidate is not null && applied.Count > 0)
            {
                try { await RunValidate(); }
                catch (Exception ex)
                {
                    validationFailed = true;
                    Status += $" Validation could not complete: {ex.Message}";
                }
            }
            return Result = new(merged, replaced, skipped, 0, conflicts, applied.Count, false, false, validationFailed, Status);
        }
        finally
        {
            Running = false;
            RunFinished = Result?.Success == true;
            if (Result is not null && RunCompleted is not null) await RunCompleted(Result);
        }
    }

    private sealed record ResolvedRecipe(MapSkinForceRecipe Recipe, bool Inferred);

    /// <summary>M730: the recorded recipe for a bin, else one inferred from the bin and its old original when the
    /// bin is a shipping map bin. Only hosts that keep recipes get inference - it hands the recipe back through
    /// <see cref="RecipeInferred"/>, and a recipe nobody keeps would be re-inferred on every update.</summary>
    private ResolvedRecipe? ResolveRecipe(string rel, byte[] mod, byte[] old, string patch)
    {
        if (RecipeFor is null) return null;
        try
        {
            if (RecipeFor(rel) is { } record && record.Kind == BinRecipeRecord.ForceMapSkinKind)
                return new ResolvedRecipe(MapSkinForceRecipe.FromRecord(record), false);
            if (!MapSkinForceRecipe.IsShippingMapBinPath(rel)) return null;
            if (MapSkinForceRecipe.Infer(mod, old, Resolve) is not { } inferred) return null;
            RecipeInferred?.Invoke(rel, inferred.ToRecord(rel, patch, "inferred"));
            return new ResolvedRecipe(inferred, true);
        }
        catch (Exception)
        {
            return null;   // an unreadable recipe leaves the bin on the structural merge it always had
        }
    }

    [RelayCommand] private void IncludeAll() { foreach (var b in Bins) b.Include = true; }
    [RelayCommand] private void IncludeNone() { foreach (var b in Bins) b.Include = false; }
}
