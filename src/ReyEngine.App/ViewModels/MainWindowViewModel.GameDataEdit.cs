using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Build;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.LtkGameData;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Particles;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M823: editing a bin the imported LTK GameData changes - saved as ONE declaration on top of the GameData, never as a bin.
///
/// <para><b>The problem.</b> The editor shows a bin the GameData targets as LTK installs it: the package's declarations applied over the game's copy (M819). Writing what it shows back as a
/// file would make LTK apply the declarations to their own output (<c>+list</c> duplicates, a clone that already exists, a <c>-list</c> that finds nothing) - which is why every write was refused.
/// The edit is therefore kept as what it is: the difference <c>diff(B, E)</c> between the bin LTK makes from the game and the package's declarations alone (<c>B</c>) and the bin the editor holds
/// (<c>E</c>), a module of literal values (<see cref="GameDataEditPlanner"/>) stored with the project beside the package's own (<see cref="LtkEditStore"/>), in the last layer that applies anything to the bin, after
/// that layer's imported modules. The preview applies it exactly where an export puts it, so the editor and LTK Manager show the same bin; an export and a Send write the package's document as it came, then these.</para>
///
/// <para><b>Where it is routed.</b> At the two choke points every editor saves a bin through - <see cref="SaveMapBinBytesAsync"/> and <see cref="TryWriteToProjectFile"/> - and in the guard
/// the editors start a save with (<see cref="GuardBinEdit"/>). Every other write of such a bin stays refused (<see cref="RefusesGameDataWrite"/>): a flow that writes whole files, stages other files before the
/// bin or keeps a record of the bin as it was, has no way to say that a declaration was refused at the end of it.</para>
///
/// <para><b>What cannot be expressed is refused, with the reason, and nothing is written.</b> A declaration removes no property, and an embedded struct may not change class; and whatever the diff writes is
/// applied to <c>B</c> with the apply engine and the preview's class schema before it is kept, and the result must hold the data of <c>E</c> (a property the schema cannot type is skipped by LTK, so an edit that adds one
/// where the schema is silent is refused here, not found out on the player's machine). The editor keeps its unsaved state. There is no fallback to a whole copy.</para>
///
/// <para><b>An editor that holds a parsed document is rebased, or refused.</b> <c>diff(B, E)</c> is only the person's edit when <c>E</c> was made from <c>B</c>. A document parsed while the preview was still working, or
/// before another editor saved the same bin, does not hold what the package's modules put in the bin: saved as it is, its diff would state the game's values again and remove the objects the package created, and the proof
/// would pass - the package's effect on that bin would be gone, silently (an auto-save can do it unattended). So an editor hands over the bytes it parsed its document from, and the save merges its edits onto the bin as it
/// is served NOW (<see cref="BinThreeWayMerge"/>), after the preview has settled; a bin it cannot be merged onto is refused (<see cref="RebaseForSave"/>, <see cref="SaveGameDataEditAsync"/>).</para>
///
/// <para><b>A save is made against ONE preview.</b> The mounts are rebuilt a moment after any file changes, and the plan is made off the UI thread: when the preview the plan was made against is not the editor's
/// any more by the time it is done (or another save changed the bin it was merged onto), nothing is written - the save settles and plans again. Several bins that are saved together (a placement) are proven before any is kept, and put
/// back if one fails (<see cref="SaveMapBinsTogetherAsync"/>).</para>
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>The chunks the project keeps an edit on top of the GameData for. Kept for the commands that ask about it often (UI thread); read from the store by the preview's worker and set when it settles.</summary>
    private HashSet<ulong> _gameDataEdited = new();

    /// <summary>What the last refusal of a chunk's bytes was, by chunk: the same bytes saved again (the auto-save's next tick) are refused with the same words, at once and without being said again.</summary>
    private readonly Dictionary<ulong, (ulong Bytes, string Reason)> _gameDataRefused = new();

    /// <summary>Test seam: run after a save has planned its declaration and before it is kept - where the file watcher's rebuild of the mounts can land in the editor. Null in the editor.</summary>
    internal Func<Task>? GameDataBeforeCommit = null;   // set only by tests (reflection): an explicit null keeps CS0649 quiet

    /// <summary>Test seam: run where a flow that holds the bytes it read waits for the person (a dialog), which can last for as long as they like - where the preview can be rebuilt, or another editor can save the bin. Null in the editor.</summary>
    internal Func<Task>? GameDataFlowPause = null;   // set only by tests (reflection)

    private Task GameDataFlowPaused() => GameDataFlowPause?.Invoke() ?? Task.CompletedTask;

    /// <summary>What the last refusal of an edit said, whole: the flows that save several bins as one unit say why the one that failed did.</summary>
    private string? _gameDataLastRefusal;

    /// <summary>The bytes a successful declaration save was handed (by identity) and the bin that is served after it, when the save changed nothing in them: they were the bin as served plus the editor's own edits, so the editor's document
    /// stands for what is served now and its next save can be rebased from it (<see cref="TakeServedAfterSave"/>).</summary>
    private (byte[] Saved, byte[] Served)? _gameDataSavedAsIs;

    /// <summary>How many times one save plans again because the preview it planned against was replaced meanwhile, before it says so and gives up.</summary>
    private const int GameDataPlanAttempts = 4;

    /// <summary>What a refusal of a flow that copies a bin into the project adds to <see cref="GameDataEditRefusal"/>: the way to change a bin the GameData changes.</summary>
    public const string GameDataEditInPlaceHint = "To change it, open it in an editor and save: the edit is kept as a declaration on top of the GameData.";

    // ============================================================================================ the guard

    /// <summary>
    /// Whether the editor has yet to apply the preview it holds: the preview is still being made, OR it is ready on its worker and the editor (the UI thread) has not run <see cref="ApplyGameDataSettled"/> yet. In that moment
    /// the preview says it is ready - it knows what it targets and what it serves - but a READ still answers what the mounts hold, without the package's changes: an editor that reads the bin then builds an edit from bytes the
    /// declarations were never applied to, whose difference from the bin the GameData makes would put the package's values back, and the proof would pass. The task of the preview (<see cref="_gameDataTask"/>) completes after the editor has applied it.
    /// </summary>
    private bool GameDataNotApplied => _gameData is not null && !_gameDataTask.IsCompleted;

    /// <summary>
    /// The guard of an editor whose save ends in <see cref="SaveMapBinBytesAsync"/> or <see cref="TryWriteToProjectFile"/>: <see cref="GuardEditable"/>, except that a bin the GameData changes is let through when its edit
    /// can be kept as a declaration (the preview works, and a layer holds the bin to follow). The save may still be refused, with the reason, when the edit cannot be expressed.
    /// </summary>
    internal bool GuardBinEdit(WadAssetEntry? entry)
    {
        if (entry is not null && IsBinEntry(entry) && IsGameDataTarget(entry.PathHash))
        {
            if (GameDataEditBlocker(entry, out _, out _) is not { } why) return true;
            RefuseGameDataEdit(entry, why);
            return false;
        }
        return GuardEditable(entry);
    }

    /// <summary><see cref="GuardBinEdit"/> for a flow that can wait: a preview that is still working is waited for (without blocking a thread, for at most <see cref="GameDataWriteWait"/>).</summary>
    internal async ValueTask<bool> GuardBinEditAsync(WadAssetEntry? entry)
    {
        if (entry is not null && IsBinEntry(entry) && !await SettleGameDataForEditAsync(entry))
        {
            _gameDataRefusals++;
            return false;
        }
        return GuardBinEdit(entry);
    }

    /// <summary>
    /// Waits for the preview when it has not finished - or the editor has not applied it yet (<see cref="GameDataNotApplied"/>) - and the bin depends on it: an edit of a target is the difference from the bin the preview makes, which does not
    /// exist until it has, and an editor reads that bin only once it is applied; and a bin the preview cannot yet say is a target or not (the names the same documents had over the same game are inherited at once, M819) waits for it to say.
    /// A bin that is known to be no target does NOT wait, however long the preview takes: what it is saved as does not depend on it. Returns false when the editor was changed while it waited (<see cref="WriteContext"/>).
    /// </summary>
    private async ValueTask<bool> SettleGameDataForEditAsync(WadAssetEntry entry)
    {
        // "not applied" is pending too: between the preview being ready and the editor applying it a read still answers the unchanged bytes (see GameDataNotApplied)
        bool waits = (GameDataNotApplied && IsGameDataTarget(entry.PathHash)) || GameDataWritePending(entry.PathHash, isBin: true);
        if (!waits) return true;
        var asked = WriteContextNow();
        await WaitForGameDataToSettleAsync("the bin is saved", GameDataWriteWait, holdsMapReload: false, untilApplied: true);
        if (IsStillTheContextOf(asked)) return true;
        _log.Warn("GameData", GameDataProjectChangedRefusal);
        Status = GameDataProjectChangedRefusal;
        _gameDataLastRefusal = GameDataProjectChangedRefusal;
        return false;
    }

    /// <summary>
    /// Why an edit of this bin cannot be kept on top of the GameData right now, or null: the preview must be ready (the bin an edit is the difference from is made by it), the declarations must name the bin, and the
    /// last layer that applies anything to it must keep a GameData document of its package for the edit to follow - with its edits usable (a file of edits that could not be read does not apply, and a new edit would follow nothing).
    /// </summary>
    private string? GameDataEditBlocker(WadAssetEntry entry, out GameDataPreview? preview, out ProjectLayer? layer)
    {
        preview = _gameData;
        layer = null;
        if (_mounts is null) return "no project is open";
        if (preview is null || preview.State != GameDataPreviewState.Ready)
        {
            return preview is { IsPending: true }
                ? GameDataPendingRefusal.TrimEnd('.')
                : $"the GameData preview {_gameDataUnavailable ?? "is not available"}: the bin an edit is the difference from is made by it, so nothing is written until it works again";
        }
        // ready on its worker, not applied by the editor yet: a read still answers the bytes the declarations were not applied to (GameDataNotApplied) - for the writers that cannot wait, this is the whole answer
        if (GameDataNotApplied) return GameDataPendingRefusal.TrimEnd('.');
        var target = preview.Overlay?.Plan().Target(entry.PathHash);
        if (target is null || target.Layers.Count == 0) return "no declaration of the package applies to it";
        string last = target.Layers[^1];
        layer = Project.Layers.FirstOrDefault(l => l.Name == last);
        if (layer is null || !LtkProjectStore.IsSafeKey(layer.DeclarationsKey))
            return $"layer '{last}', the last that applies anything to it, keeps no GameData document of its package, so an edit has nothing to follow";
        string layerName = layer.Name;
        if (preview.Setup?.Layers.FirstOrDefault(l => l.Name == layerName) is { EditsProblem: { } editsProblem })
            return $"the edits kept on top of layer '{layerName}' are not applied ({editsProblem}), so a new edit would have nothing to follow; make that file usable first";
        return null;
    }

    /// <summary>The refusal of an edit, said where the person sees it. The edit stays pending in its editor and nothing was written.</summary>
    private void RefuseGameDataEdit(WadAssetEntry entry, string reason, bool say = true)
    {
        _gameDataRefusals++;
        string text = $"'{entry.DisplayName}' cannot be saved on top of the mod's GameData: {reason.TrimEnd('.', ' ')}. Nothing was written; the edit stays pending.";
        if (say) _log.Warn("GameData", text);
        Status = text;
        _gameDataLastRefusal = text;
    }

    /// <summary>The editor was changed (another project, File &gt; Open WAD, another map) while a save waited: nothing is written.</summary>
    private bool RefuseProjectChanged()
    {
        _gameDataRefusals++;
        _log.Warn("GameData", GameDataProjectChangedRefusal);
        Status = GameDataProjectChangedRefusal;
        _gameDataLastRefusal = GameDataProjectChangedRefusal;
        return false;
    }

    // ============================================================================================ the save

    /// <summary>What a save planned against one preview: the bytes to keep (the editor's, merged onto the bin as it is served when the editor was stale), the declaration they come to, and what the merge did.</summary>
    private sealed record PlannedEdit(byte[] Edited, GameDataEditOutcome? Outcome, int LocalEdits, string? Refusal);

    /// <summary>
    /// A bin the editor saves, when the GameData changes it: kept as a declaration on top. Null when the bin is none of the GameData's (the caller saves it as ever); true when it was kept (or
    /// there was nothing to keep); false when it was refused, which has been said.
    /// </summary>
    /// <param name="openedFrom">The bytes an editor that holds a parsed document parsed it from (null for a flow that read the bin just now). When the bin served now is not those bytes, the editor's edits are merged onto it before the
    /// declaration is made - after the preview has settled, which is the moment the bin served is the one LTK would make.</param>
    /// <param name="channel">The log category of that merge.</param>
    /// <param name="holdsDocument">Whether the bytes are the serialized document of an editor. Such an editor that cannot say what it parsed its document from (<paramref name="openedFrom"/> is null) is refused for a bin the GameData
    /// changes: its document may be stale, and nothing can tell.</param>
    private async Task<bool?> SaveGameDataEditAsync(WadAssetEntry entry, byte[] bytes, byte[]? openedFrom = null, string channel = "Bin", bool holdsDocument = false)
    {
        if (_mounts is null || !IsBinEntry(entry)) return null;
        _gameDataSavedAsIs = null;   // what an earlier save noted was for its own editor, which has taken it by now
        var asked = WriteContextNow();
        for (int attempt = 1; ; attempt++)
        {
            if (!await SettleGameDataForEditAsync(entry)) { _gameDataRefusals++; return false; }
            if (!IsGameDataTarget(entry.PathHash))
            {
                if (attempt == 1) return null;
                RefuseGameDataEdit(entry, "the mod's GameData no longer names it");
                return false;
            }
            if (attempt == 1 && !await EnsureProjectSavedAsync()) return false;
            if (!IsStillTheContextOf(asked)) return RefuseProjectChanged();
            if (GameDataEditBlocker(entry, out var preview, out var layer) is { } why) { RefuseGameDataEdit(entry, why); return false; }
            if (holdsDocument && openedFrom is not { Length: > 0 })
            {
                RefuseGameDataEdit(entry, "this editor does not know which bin its document was read from, so what it holds cannot be told from what the mod's GameData put into the bin; reopen the bin");
                return false;
            }

            byte[]? current = null;
            if (openedFrom is not null)
            {
                try { current = ReadAsset(entry.PathHash); }
                catch (Exception ex) when (ex is not OperationCanceledException) { RefuseGameDataEdit(entry, $"the bin as the GameData makes it could not be read ({ex.Message})"); return false; }
            }
            ulong key = RefusalKey(bytes, current);
            if (RepeatedRefusal(entry, key) is { } again) { RefuseGameDataEdit(entry, again, say: false); return false; }

            var names = _declarationNames ?? new DeclarationNames(_resolver.Database);
            var work = await Task.Run(() => PlanWithRebase(preview!, entry, bytes, openedFrom, current, names));
            if (GameDataBeforeCommit is { } landsHere) await landsHere();

            if (!IsStillTheContextOf(asked)) return RefuseProjectChanged();
            // the plan was made against `preview`, and merged onto `current`: if the mounts were rebuilt meanwhile (the file watcher does it a moment after any file changes), or another save changed the bin, the plan
            // is about something that is not there any more. It is made again, against what is - not written, and not rolled back.
            if (!ReferenceEquals(_gameData, preview) || preview!.State != GameDataPreviewState.Ready || (current is not null && !IsServedBin(entry.PathHash, current)))
            {
                if (attempt < GameDataPlanAttempts) continue;
                RefuseGameDataEdit(entry, "the project was rebuilt, or the bin saved by another editor, again and again while the edit was being prepared; try saving it once more");
                return false;
            }

            if (work.Refusal is { } mergeRefusal)
            {
                _gameDataRefused[entry.PathHash] = (key, mergeRefusal);
                RefuseGameDataEdit(entry, mergeRefusal);
                return false;
            }
            if (work.LocalEdits > 0) LogRebase(entry, channel, work);
            bool? committed = CommitGameDataEdit(entry, work.Edited, preview, layer!, work.Outcome!, out _, key);
            if (committed is null)
            {
                // the preview stopped being the editor's between the check above and the commit: planned again, and never handed to the whole-file path, which a null from here would be
                if (attempt < GameDataPlanAttempts) continue;
                RefuseGameDataEdit(entry, "the project was rebuilt, or the bin saved by another editor, again and again while the edit was being prepared; try saving it once more");
                return false;
            }
            if (committed is true && holdsDocument && ReferenceEquals(work.Edited, bytes)) NoteSavedAsIs(entry, bytes);   // only an editor that holds a document has a base to move
            return committed;
        }
    }

    /// <summary>Remembers that the bytes of a save were kept as they were handed over (no merge changed them), and what is served now: the bin plus the editor's own edits, which is what its document stands for.</summary>
    private void NoteSavedAsIs(WadAssetEntry entry, byte[] saved)
    {
        try { _gameDataSavedAsIs = (saved, ReadAsset(entry.PathHash)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _gameDataSavedAsIs = null; }
    }

    /// <summary>
    /// After an editor's save: the bin as it is served now, when the save kept the editor's document as it was (<see cref="NoteSavedAsIs"/>) - the bytes its next save can be rebased from, so that the edits it has kept already are not
    /// taken for the mod's changes since (a second edit of the same property would be a conflict with its own first). Null when the save had to merge something in: the document lacks it, and the base it was parsed from stays (moving
    /// it forward to the merged bin would make the next save look like it deleted what the merge brought in, <c>MaterialEditorViewModel.BaseBytes</c>).
    /// </summary>
    /// <param name="saved">The very array the editor handed to the save.</param>
    private byte[]? TakeServedAfterSave(byte[] saved)
    {
        if (_gameDataSavedAsIs is not { } noted || !ReferenceEquals(noted.Saved, saved)) return null;
        _gameDataSavedAsIs = null;
        return noted.Served;
    }

    /// <summary>
    /// <see cref="SaveGameDataEditAsync"/> for the writers that cannot await (<see cref="TryWriteToProjectFile"/>): the same, on the calling thread - nothing can be replaced under it, so there is no second attempt. Null when the bin is
    /// none of the GameData's; true when it was kept. A refusal throws, as the low write paths do (their callers log what they are told): the message is the whole reason.
    /// </summary>
    /// <exception cref="InvalidOperationException">The edit cannot be kept on top of the GameData; nothing was written.</exception>
    private bool? SaveGameDataEdit(WadAssetEntry entry, byte[] bytes, out string where)
    {
        where = "";
        if (_mounts is null || !IsBinEntry(entry) || !IsGameDataTarget(entry.PathHash)) return null;
        if (GameDataEditBlocker(entry, out var preview, out var layer) is { } why) { RefuseGameDataEdit(entry, why); throw new InvalidOperationException(Status); }
        ulong key = RefusalKey(bytes, null);
        if (RepeatedRefusal(entry, key) is { } again) { RefuseGameDataEdit(entry, again, say: false); throw new InvalidOperationException(Status); }

        var names = _declarationNames ?? new DeclarationNames(_resolver.Database);
        var outcome = PlanGameDataEdit(preview!, entry, bytes, names);
        var kept = CommitGameDataEdit(entry, bytes, preview!, layer!, outcome, out where, key);
        if (kept is not true)
        {
            if (kept is null) RefuseGameDataEdit(entry, "the GameData preview changed while the edit was prepared");
            throw new InvalidOperationException(Status);
        }
        return true;
    }

    private static ulong HashOf(byte[] bytes) => System.IO.Hashing.XxHash64.HashToUInt64(bytes);

    /// <summary>What a refusal is remembered by: the bytes the editor handed over, and - when they are merged onto the bin served - the bin they were merged onto. The same pair always comes to the same answer.</summary>
    private static ulong RefusalKey(byte[] bytes, byte[]? current) => current is null ? HashOf(bytes) : HashOf(bytes) ^ (HashOf(current) * 0x9E3779B97F4A7C15UL);

    /// <summary>The words a refusal of this very key was given, or null: the auto-save tries again every few minutes with the bytes it was refused, and the answer cannot have changed.</summary>
    private string? RepeatedRefusal(WadAssetEntry entry, ulong key) =>
        _gameDataRefused.TryGetValue(entry.PathHash, out var known) && known.Bytes == key ? known.Reason : null;

    /// <summary>Whether the bin served for the chunk is still these bytes.</summary>
    private bool IsServedBin(ulong chunk, byte[] bytes)
    {
        try { return ReadAsset(chunk).AsSpan().SequenceEqual(bytes); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
    }

    /// <summary>
    /// The declaration of the edit: the editor's bytes merged onto the bin served when they were parsed from something else (<paramref name="openedFrom"/> is not <paramref name="current"/>), then the bin the package's GameData makes (<c>B</c>),
    /// the bin the editor holds (<c>E</c>) and the module that gives <c>E</c> from <c>B</c>, proven. Runs off the UI thread; it logs nothing.
    /// </summary>
    private PlannedEdit PlanWithRebase(GameDataPreview preview, WadAssetEntry entry, byte[] bytes, byte[]? openedFrom, byte[]? current, IDeclarationNames names)
    {
        byte[] edited = bytes;
        int local = 0;
        if (openedFrom is not null && current is not null && !current.AsSpan().SequenceEqual(openedFrom))
        {
            var merge = MergeOntoServed(openedFrom, bytes, current);
            if (merge.Refusal is not null) return merge;
            edited = merge.Edited;
            local = merge.LocalEdits;
        }
        return new PlannedEdit(edited, PlanGameDataEdit(preview, entry, edited, names), local, null);
    }

    /// <summary>
    /// The editor's edits (<paramref name="edited"/>, made from <paramref name="openedFrom"/>) merged onto the bin as it is served NOW (<paramref name="current"/>). A bin the mod's GameData changes does not take the editor's value where the
    /// mod's and the editor's DIFFER (<see cref="BinMergeReport.RealConflicts"/>): the person edited a property the GameData has changed since, and either value could be the wrong one - the save is refused, and the editor is opened again.
    /// A property both changed to the same value is no conflict.
    /// </summary>
    private PlannedEdit MergeOntoServed(byte[] openedFrom, byte[] edited, byte[] current)
    {
        try
        {
            var (merged, report) = BinThreeWayMerge.Merge(openedFrom, edited, current, ResolveBinName);
            if (report.RealConflicts.Count > 0) return new PlannedEdit(edited, null, 0, ConflictRefusal(report.RealConflicts));
            return new PlannedEdit(merged, null, report.ModAdded + report.ModModified + report.ModRemoved, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PlannedEdit(edited, null, 0,
                $"the bin changed since this editor read it (the mod's GameData was not in it yet, or another editor saved it) and the editor's edits could not be merged onto the bin as it is now ({ex.Message}); reopen the bin and make the edit again");
        }
    }

    private static string ConflictRefusal(IReadOnlyList<string> places) =>
        $"the mod's GameData changed {string.Join(", ", places.Take(3))}{(places.Count > 3 ? $" and {places.Count - 3:n0} more" : "")} since this editor opened it; reopen the editor and make the edit again";

    private void LogRebase(WadAssetEntry entry, string channel, PlannedEdit work) =>
        _log.Info(channel, $"{entry.DisplayName} changed underneath this editor (the mod's GameData was applied to it, or another editor saved it) - merged {work.LocalEdits:n0} local edit(s) onto the bin as it is now instead of overwriting it.");

    /// <summary>The declaration of the edit: the bin the package's GameData makes (<c>B</c>), the bin the editor holds (<c>E</c>), the module that gives <c>E</c> from <c>B</c>, proven. Runs off the UI thread where it can.</summary>
    private GameDataEditOutcome PlanGameDataEdit(GameDataPreview preview, WadAssetEntry entry, byte[] edited, IDeclarationNames names)
    {
        if (!preview.TryReadImportedOnly(entry.PathHash, out var imported, out var problem))
            return new GameDataEditOutcome(GameDataEditKind.Refused, $"the bin LTK makes of the package's GameData could not be read ({problem})");
        var options = preview.Setup?.Options ?? new GameDataOverlayOptions();
        string target = BinDeclarations.TargetOf(entry.IsResolved ? entry.Path : $"0x{entry.PathHash:x16}.bin", entry.PathHash);
        try
        {
            var outcome = GameDataEditPlanner.Plan(target, imported!, edited, names, options.Schema, options.Limits);
            // the edit is expressible; the class schema LTK Manager types it with is what cannot take it, and the setup says why
            return outcome.IsRefused && outcome.NeedsSchema && preview.Setup?.SchemaNote is { } note
                ? outcome with { Reason = $"{outcome.Reason}. The class schema is the cause: {note.TrimEnd('.')}" }
                : outcome;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new GameDataEditOutcome(GameDataEditKind.Refused, $"the edit could not be compared with the GameData's bin ({ex.Message})");
        }
    }

    /// <summary>What a chunk's edit was before a save touched it: the layer and the module text it lived in, or neither when there was none. Putting it back is exact (<see cref="RestoreGameDataEdit"/>).</summary>
    private readonly record struct GameDataEditMemento(ulong Chunk, ReyProject Project, ProjectLayer? Layer, string? ModuleText);

    /// <summary>What the project holds of a chunk's edit now. The project is part of it: the editor may be showing another one by the time it is put back.</summary>
    private GameDataEditMemento? TakeGameDataEditMemento(ulong chunk)
    {
        var project = Project;
        try
        {
            var previous = LtkEditStore.Find(project, chunk);
            return new GameDataEditMemento(chunk, project, previous?.Layer, previous?.Module.Text);
        }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>
    /// Puts a chunk's edit back as it was (<see cref="TakeGameDataEditMemento"/>), in the project it was taken from, and the preview, the markers and the commands with it - if that project is still the editor's: when it is not (another
    /// project was opened meanwhile) the files of the first are put back and nothing of the editor's state, which is the second's, is touched. When the preview cannot follow in place the mounts are rebuilt, as taking an edit away does.
    /// False when the store could not be written back.
    /// </summary>
    private bool RestoreGameDataEdit(GameDataEditMemento memento)
    {
        var (restored, followed) = RestoreGameDataEditCore(memento);
        if (restored && !followed && ReferenceEquals(Project, memento.Project)) RebuildAfterRestore();
        return restored;
    }

    private void RebuildAfterRestore()
    {
        BuildMounts();
        BuildProjectTree();
    }

    /// <returns>Whether the store was put back, and whether the preview followed in place (false also when the editor is on another project and there was nothing to follow).</returns>
    private (bool Restored, bool Followed) RestoreGameDataEditCore(GameDataEditMemento memento)
    {
        ulong chunk = memento.Chunk;
        var project = memento.Project;
        try
        {
            if (memento.Layer is not null && memento.ModuleText is not null) LtkEditStore.Set(project, memento.Layer, chunk, memento.ModuleText);
            else LtkEditStore.Remove(project, chunk);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            _log.Error("GameData", $"The previous edit of bin 0x{chunk:x16} could not be put back: {ex.Message}");
            return (false, false);
        }
        if (!ReferenceEquals(Project, project)) return (true, true);   // the editor has moved on: its preview, markers and commands are the other project's, and are not touched

        var servedBefore = _gameData?.Entries.Select(e => e.PathHash).ToHashSet();
        bool followed = RefreshGameDataAfterEdit(chunk, out _);
        if (memento.ModuleText is not null) _gameDataEdited.Add(chunk); else _gameDataEdited.Remove(chunk);
        RevertGameDataEditsCommand.NotifyCanExecuteChanged();
        if (followed && servedBefore is not null && _gameData is { } now && !servedBefore.SetEquals(now.Entries.Select(e => e.PathHash))) BuildProjectTree();
        SetNodeStatus(chunk, memento.ModuleText is not null ? AssetStatus.Modified : AssetStatus.Original);
        return (true, followed);
    }

    /// <summary>
    /// Keeps (or takes away) the edit and makes the preview serve it, on the UI thread. Then reads the bin back and requires it to be what the declaration was proven to give: if it is not, the edit as it
    /// was before is put back and the save is refused - an edit LTK and the editor would disagree about is not kept.
    /// </summary>
    /// <param name="refusalKey">What a refusal is remembered by (<see cref="RefusalKey"/>).</param>
    /// <returns>True when it was kept (or there was nothing to keep); false when it was refused, which has been said; null when the preview it was planned against is not the editor's any more - nothing was done, and the caller plans again.</returns>
    private bool? CommitGameDataEdit(WadAssetEntry entry, byte[] bytes, GameDataPreview preview, ProjectLayer layer, GameDataEditOutcome outcome, out string where, ulong refusalKey)
    {
        where = "";
        ulong chunk = entry.PathHash;
        if (outcome.Kind == GameDataEditKind.Refused)
        {
            _gameDataRefused[chunk] = (refusalKey, outcome.Reason!);
            RefuseGameDataEdit(entry, outcome.Reason!);
            return false;
        }
        // the refresh below serves the edit through the editor's preview: it must be the one the plan was made against, and ready - checked here, before anything is written, so that a rebuilt project is never a rollback
        if (!ReferenceEquals(_gameData, preview) || preview.State != GameDataPreviewState.Ready || _mounts is null) return null;
        _gameDataRefused.Remove(chunk);

        // what a caller that says where the bin went (the low writer) reports: the store is where the edit lives
        string store = Project.RootPath is null
            ? $"layer '{layer.Name}''s edits on top of the GameData"
            : $"{LtkEditStore.PathOf(Project.RootPath, layer.DeclarationsKey!)} (a declaration on top of the GameData)";

        GameDataEditMemento? before = TakeGameDataEditMemento(chunk);
        if (before is null) { RefuseGameDataEdit(entry, $"the edits kept on top of the GameData cannot be read ({LastStoreProblem(chunk)})"); return false; }
        var previous = before.Value;

        if (outcome.Kind == GameDataEditKind.Unchanged)
        {
            where = previous.ModuleText is null ? "no file - it is what the mod's GameData makes of it" : "the mod's GameData - the edit on top of it was taken away";
            if (previous.ModuleText is null)
            {
                _log.Info("GameData", $"{entry.DisplayName} is what the mod's GameData makes of it: there is no edit to keep.");
                Status = $"{entry.DisplayName} is what the mod's GameData makes of it: there is no edit to keep.";
                return true;
            }
        }
        else
        {
            where = store;
            // the very module the project already keeps, in the layer it is in, and the preview serves the bin it was proven to give: the same edit saved again (the auto-save's tick) changes nothing, so
            // nothing is written - which would wake the file watcher into rebuilding the mounts - and nothing is refreshed
            if (previous.Layer is not null && ReferenceEquals(previous.Layer, layer) && string.Equals(previous.ModuleText, outcome.ModuleText, StringComparison.Ordinal)
                && _gameDataEdited.Contains(chunk) && ServesWhatWasProven(preview, chunk, outcome))
            {
                Status = $"{entry.DisplayName} saved as a declaration on top of the mod's GameData (layer '{layer.Name}'): nothing changed since the last save.";
                return true;
            }
        }

        var servedBefore = preview.Entries.Select(e => e.PathHash).ToHashSet();
        try
        {
            if (outcome.Kind == GameDataEditKind.Declared) LtkEditStore.Set(Project, layer, chunk, outcome.ModuleText!);
            else LtkEditStore.Remove(Project, chunk);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            // the store puts its files back when a write fails, so the edit is as it was
            RefuseGameDataEdit(entry, $"the edit could not be stored ({ex.Message})");
            return false;
        }

        bool served = RefreshGameDataAfterEdit(chunk, out string? refreshProblem)
                      && (outcome.Kind != GameDataEditKind.Declared || ServesWhatWasProven(preview, chunk, outcome));
        if (!served)
        {
            // put the edit back as it was, and the preview with it
            bool restored = RestoreGameDataEdit(previous);
            RefuseGameDataEdit(entry, (refreshProblem ?? "LTK's engine and the editor would disagree about the bin this declaration makes")
                + (restored ? "" : "; the edit it had before could not be put back either, so check the project's edits file"));
            return false;
        }

        if (outcome.Kind == GameDataEditKind.Declared) _gameDataEdited.Add(chunk); else _gameDataEdited.Remove(chunk);
        RevertGameDataEditsCommand.NotifyCanExecuteChanged();
        if (!servedBefore.SetEquals(preview.Entries.Select(e => e.PathHash))) BuildProjectTree();   // a bin the GameData did not change before is listed as what is read now, or no longer
        SetNodeStatus(chunk, outcome.Kind == GameDataEditKind.Declared ? AssetStatus.Modified : AssetStatus.Original);
        Project.IsDirty = true;
        UpdateTitle();

        _log.Success("GameData", outcome.Kind == GameDataEditKind.Declared
            ? $"Saved {entry.DisplayName} as a declaration on top of the mod's GameData, in layer '{layer.Name}' after its imported modules: {outcome.Properties:n0} propert(ies), "
              + $"{outcome.ObjectsAdded:n0} object(s) added, {outcome.ObjectsRemoved:n0} removed, {outcome.LinksChanged:n0} link(s) changed. Export .fantome and Send to LTK Manager write it after the imported modules."
            : $"{entry.DisplayName} is what the mod's GameData makes of it again: the edit on top of it was taken away.");
        // a refusal of an earlier save is still on the status line: it is not the state of this bin any more
        Status = outcome.Kind == GameDataEditKind.Declared
            ? $"{entry.DisplayName} saved as a declaration on top of the mod's GameData (layer '{layer.Name}')."
            : $"{entry.DisplayName} is what the mod's GameData makes of it again.";
        return true;
    }

    /// <summary>Why the store could not be read for a chunk, for the one message that says so.</summary>
    private string LastStoreProblem(ulong chunk)
    {
        try { _ = LtkEditStore.Find(Project, chunk); return "unknown"; }
        catch (InvalidDataException ex) { return ex.Message; }
    }

    /// <summary>The preview, read back, serves the very bytes the engine was proven to make of the declaration.</summary>
    private static bool ServesWhatWasProven(GameDataPreview preview, ulong chunk, GameDataEditOutcome outcome)
    {
        if (!preview.TryRead(chunk, out var served)) return false;
        if (served.AsSpan().SequenceEqual(outcome.Expected)) return true;
        // not the same bytes: the same data is enough (the engine writes one form)
        try { return BinTreeEquivalence.FirstDifference(SafeBinTree.Parse(outcome.Expected!), SafeBinTree.Parse(served)) is null; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
    }

    /// <summary>
    /// The preview serves the declarations as the store holds them now, in place (<see cref="GameDataPreview.TryRefreshEdited"/>): nothing is pending in between, so what the saving flow reads next is the edit. When
    /// that cannot be done the mounts are rebuilt, which makes a new preview.
    /// </summary>
    private bool RefreshGameDataAfterEdit(ulong chunk, out string? problem)
    {
        problem = null;
        if (_gameData is not { State: GameDataPreviewState.Ready } preview || _mounts is not { } mounts)
        {
            problem = "the GameData preview is not ready";
            return false;
        }
        var layers = GameDataLayerInput.FromProject(Project);
        if (!preview.TryRefreshEdited(layers, chunk, out string? why))
        {
            problem = why;
            return false;
        }
        mounts.RefreshOverlay();
        // what the next preview of these documents over this game inherits, and what the editor is showing now: the preview that follows (the file watcher rebuilds the mounts) changes nothing the person sees
        _gameDataMemory?.RememberNamed(GameDataPreview.FingerprintOf(layers), _gameDataGameKey, preview.NamedChunks());
        _gameDataShownKey = GameDataKeyOf(preview);
        return true;
    }

    /// <summary>Whether a path is inside the project's LTK store (<c>.reyengine/ltk</c>): the GameData documents and the edits on top of them. No mount holds those files, so a change of one is nothing the browser shows.</summary>
    internal bool IsInLtkStore(string? path)
    {
        if (string.IsNullOrEmpty(path) || Project.RootPath is not { } root) return false;
        try
        {
            // the folder itself counts: writing a file below it is reported by the watcher for the folders on the way up too
            string store = Path.GetFullPath(LtkProjectStore.RootOf(root)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(full, store, StringComparison.OrdinalIgnoreCase) || full.StartsWith(store + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    // ============================================================================================ an editor that holds a parsed document

    /// <summary>
    /// For a save that cannot await (<see cref="TryWriteToProjectFile"/>): what an editor that holds a parsed document hands over, made fit for a bin the GameData changes. The editor parsed its document from
    /// <paramref name="openedFrom"/>; the bin served now may be another (it was opened before the preview was ready, or another editor saved), and the document does not hold what it adds. The editor's edits are merged
    /// onto the served bin; when that cannot be done - the editor does not know what it was opened from, or the merge fails - the save is refused rather than written from a stale document, which would remove the package's
    /// effect without a sound. A bin that is no target is handed back as it is (the legacy rebase, <see cref="RebaseOntoCurrent"/>, is the caller's own, and leaves a target alone). The async saves do the same inside
    /// <see cref="SaveGameDataEditAsync"/>, after the preview has settled.
    /// </summary>
    /// <returns>The bytes to save; null when the save must not go on (said already, and the editor keeps its unsaved state).</returns>
    private byte[]? RebaseForSave(WadAssetEntry entry, byte[] edited, byte[]? openedFrom, string channel)
    {
        if (_mounts is null || !IsBinEntry(entry) || !IsGameDataTarget(entry.PathHash)) return edited;
        if (GameDataNotApplied)
        {
            RefuseGameDataEdit(entry, GameDataPendingRefusal.TrimEnd('.'));   // a read answers the unchanged bytes until the editor has applied the preview: there is nothing to merge onto yet
            return null;
        }
        if (openedFrom is not { Length: > 0 })
        {
            RefuseGameDataEdit(entry, "this editor does not know which bin its document was read from, so what it holds cannot be told from what the mod's GameData put into the bin; reopen the bin");
            return null;
        }
        byte[] current;
        try { current = ReadAsset(entry.PathHash); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RefuseGameDataEdit(entry, $"the bin as the GameData makes it could not be read ({ex.Message})");
            return null;
        }
        if (current.AsSpan().SequenceEqual(openedFrom)) return edited;
        var work = MergeOntoServed(openedFrom, edited, current);
        if (work.Refusal is { } refusal) { RefuseGameDataEdit(entry, refusal); return null; }
        LogRebase(entry, channel, work);
        return work.Edited;
    }

    /// <summary>The Map Bin Editor's save: the bytes of its document, with the bytes it parsed it from, so that a document opened before the preview was ready (or before another editor saved) is merged, not written over.</summary>
    private Task<bool> SaveMapBinEditorBytesAsync(WadAssetEntry entry, byte[] bytes) =>
        SaveEditorBinBytesAsync(entry, bytes, MapBinEditor.BaseBytes, "MapBin");

    /// <summary>The Bin Issues window's repair of the material document: the same save as the Materials editor's, guarded and rebased.</summary>
    private async Task<bool> RepairMaterialBinAsync(WadAssetEntry entry, MaterialEditorViewModel editor)
    {
        if (!await GuardBinEditAsync(entry)) return false;
        // the window outlives the editor's document: the bytes the editor says its document was parsed from belong to the document it holds NOW, so they only stand for this bin's while it is still the one it holds
        if (IsGameDataTarget(entry.PathHash) && editor.BinEntry?.PathHash != entry.PathHash)
        {
            RefuseGameDataEdit(entry, "the Materials editor holds another bin now, so what it would repair is not this one; open the Bin Issues window again");
            return false;
        }
        if (RefusesStaleEditor(entry, editor.StaleReason)) return false;
        // The tolerantly-parsed tree IS the healed form - re-saving it writes a clean file.
        var bytes = editor.Serialize();
        if (bytes is null || !await SaveEditorBinBytesAsync(entry, bytes, editor.BaseBytes, "Material")) return false;
        await LoadMaterialBinAsync(entry, alsoRawBin: false);   // reload: the red marks clear
        return true;
    }

    /// <summary>The Bin Issues window's repair of the particle document, guarded and rebased like the Particle Editor's save; the editor is reloaded from the bin as it is now.</summary>
    private async Task<bool> RepairParticleBinAsync(WadAssetEntry entry, ParticleDocument document)
    {
        if (!await GuardBinEditAsync(entry)) return false;
        // as above: the editor's base bytes are the base of the document it holds now - the repair is of that one or of none
        if (IsGameDataTarget(entry.PathHash) && (ParticleEditor.Entry?.PathHash != entry.PathHash || !ReferenceEquals(ParticleEditor.Document, document)))
        {
            RefuseGameDataEdit(entry, "the Particle Editor holds another document now (it was loaded again since the window was opened), so what it would repair is not the one the window shows; open the Bin Issues window again");
            return false;
        }
        if (RefusesStaleEditor(entry, ParticleEditor.StaleReason)) return false;
        var bytes = document.Serialize();
        if (!await SaveEditorBinBytesAsync(entry, bytes, ParticleEditor.BaseBytes, "Particle")) return false;
        ParticleEditor.Load(entry, IsGameDataTarget(entry.PathHash) ? ReadAsset(entry.PathHash) : bytes, editable: true);   // reload from the healed bytes (a bin the GameData changes: as it is served now)
        return true;
    }

    // ============================================================================================ several bins, one unit

    /// <summary>
    /// Several bins saved as ONE unit - the two a placement ends in: all of them are kept or none is. The edits of the bins the GameData changes are declared and proven BEFORE anything is kept; they are then kept first (a
    /// declaration can be put back exactly) and the bins that are saved as files last; when one fails, what was kept is put back (<see cref="RestoreGameDataEdit"/>, and the previous bytes of a file). Nothing here changes how
    /// bins that are no target are saved: a unit of such bins is written in turn, as it always was.
    /// </summary>
    /// <returns>Null when every bin was saved; else why not, with nothing kept.</returns>
    private async Task<string?> SaveMapBinsTogetherAsync(IReadOnlyList<(WadAssetEntry Entry, byte[] Bytes)> bins)
    {
        var project = Project;   // what is put back is put back in THIS project, whichever the editor shows by then
        var declared = new List<(WadAssetEntry Entry, byte[] Bytes)>();
        var files = new List<(WadAssetEntry Entry, byte[] Bytes)>();
        foreach (var bin in bins)
            (_mounts is not null && IsBinEntry(bin.Entry) && IsGameDataTarget(bin.Entry.PathHash) ? declared : files).Add(bin);

        // proven first: a bin whose edit no declaration can keep refuses all of them, before the first is touched
        foreach (var (entry, bytes) in declared)
            if (await GameDataEditPreflightAsync(entry, bytes) is { } why)
                return why;

        var keptDeclarations = new List<GameDataEditMemento>();
        var writtenFiles = new List<(WadAssetEntry Entry, byte[] Before)>();
        async Task<string> PutBackAsync(string reason)
        {
            var notPutBack = new List<string>();
            bool rebuild = false;
            foreach (var memento in Enumerable.Reverse(keptDeclarations))
            {
                var (restored, followed) = RestoreGameDataEditCore(memento);
                if (!restored) notPutBack.Add($"the edit of bin 0x{memento.Chunk:x16} on top of the GameData");
                else if (!followed) rebuild = true;
            }
            if (rebuild && ReferenceEquals(Project, project)) RebuildAfterRestore();   // once, whatever the number of bins: the preview made again serves the store as it is now
            foreach (var (entry, before) in Enumerable.Reverse(writtenFiles))
            {
                if (!ReferenceEquals(Project, project)) notPutBack.Add($"{entry.DisplayName} of project '{project.Name}' (the project is not open any more)");
                else if (!await SaveMapBinBytesAsync(entry, before)) notPutBack.Add(entry.DisplayName);
            }
            return notPutBack.Count == 0 ? reason : $"{reason} These could NOT be put back: {string.Join(", ", notPutBack)}.";
        }
        string Failed(WadAssetEntry entry) =>
            (_gameDataLastRefusal is { } said ? said.TrimEnd() : $"'{entry.DisplayName}' could not be saved.") + " None of the bins saved with it was kept.";

        foreach (var (entry, bytes) in declared)
        {
            var before = TakeGameDataEditMemento(entry.PathHash);
            if (before is null) return await PutBackAsync($"'{entry.DisplayName}': the edits kept on top of the GameData cannot be read. None of the bins saved with it was kept.");
            _gameDataLastRefusal = null;
            if (!await SaveMapBinBytesAsync(entry, bytes)) return await PutBackAsync(Failed(entry));
            keptDeclarations.Add(before.Value);
        }
        foreach (var (entry, bytes) in files)
        {
            byte[]? before = null;
            try { before = ReadAsset(entry.PathHash); } catch (Exception ex) when (ex is not OperationCanceledException) { /* a bin with nothing to put back */ }
            _gameDataLastRefusal = null;
            if (!await SaveMapBinBytesAsync(entry, bytes)) return await PutBackAsync(Failed(entry));
            if (before is not null) writtenFiles.Add((entry, before));
        }
        return null;
    }

    // ============================================================================================ revert

    private bool CanRevertGameDataEdits() => ProjectMode && ContextNode?.Entry is { } entry && _gameDataEdited.Contains(entry.PathHash);

    /// <summary>
    /// Takes the edit kept on top of the GameData of the selected bin away: the bin is what the mod's GameData makes of the game's again. The other edits are as they were.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRevertGameDataEdits))]
    private void RevertGameDataEdits()
    {
        if (ContextNode?.Entry is not { } entry) { _log.Warn("GameData", "Select a bin first."); return; }
        if (!_gameDataEdited.Contains(entry.PathHash)) { _log.Info("GameData", $"{entry.DisplayName} has no edit on top of the mod's GameData."); return; }
        try
        {
            if (!LtkEditStore.Remove(Project, entry.PathHash)) { _log.Info("GameData", $"{entry.DisplayName} has no edit on top of the mod's GameData."); _gameDataEdited.Remove(entry.PathHash); return; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _log.Error("GameData", $"The edit of {entry.DisplayName} could not be taken away: {ex.Message}");
            return;
        }
        _gameDataEdited.Remove(entry.PathHash);
        _gameDataRefused.Remove(entry.PathHash);
        RevertGameDataEditsCommand.NotifyCanExecuteChanged();
        var servedBefore = _gameData?.Entries.Select(e => e.PathHash).ToHashSet();
        if (RefreshGameDataAfterEdit(entry.PathHash, out _))
        {
            if (servedBefore is not null && !servedBefore.SetEquals(_gameData!.Entries.Select(e => e.PathHash))) BuildProjectTree();
            InvalidateGameDataState();
            // the editors and the map that show the bin read it again - an explicit act, so a reload is what is wanted. The bin may be one the GameData no longer changes at all (the edit was all there was), which a preview
            // that serves nothing of it would not name: the reverted chunk is named here
            RefreshForGameDataChunks(_gameData!, new HashSet<ulong> { entry.PathHash });
        }
        else
        {
            BuildMounts();
            BuildProjectTree();
        }
        MarkEditorsStaleAfterRevert(entry.PathHash);
        SetNodeStatus(entry.PathHash, AssetStatus.Original);
        Project.IsDirty = true;
        UpdateTitle();
        _log.Success("GameData", $"Took away the edit on top of the mod's GameData of {entry.DisplayName}: it is what the GameData makes of the game's bin again.");
        Status = $"Took away the edit on top of the mod's GameData of {entry.DisplayName}.";
    }

    /// <summary>What an editor is told when the edits of its bin were reverted under it: its document holds them, and the next save - the auto-save's tick included - would declare them again.</summary>
    private const string RevertedEditorStale = "this bin's edits on top of the mod's GameData were reverted, and what this editor holds would put them back; reopen the editor";

    private const ulong StaleEditorKey = 0x57A1E0ED17UL;

    /// <summary>
    /// The editors that hold unsaved or already kept edits of the bin whose edits were just reverted are stale: they keep what they hold (the person may want it) but their saves are refused (<see cref="RefusesStaleEditor"/>) until the
    /// editor is opened again. An editor that holds nothing of its own was loaded again by the refresh that follows a revert.
    /// </summary>
    private void MarkEditorsStaleAfterRevert(ulong chunk)
    {
        void Say(string editor, WadAssetEntry entry) =>
            _log.Warn("GameData", $"The {editor} holds edits of {entry.DisplayName}, whose edits on top of the mod's GameData were just reverted: its saves are refused until it is opened again, so that they are not declared again.");
        if (ParticleEditor.Entry is { } particle && particle.PathHash == chunk && ParticleEditor.Document?.IsDirty == true) { ParticleEditor.MarkStale(RevertedEditorStale); Say("Particle Editor", particle); }
        if (MaterialEditor.BinEntry is { } materials && materials.PathHash == chunk && MaterialEditor.IsDirty) { MaterialEditor.MarkStale(RevertedEditorStale); Say("Materials editor", materials); }
        if (MeshPreview.MaterialEditor.BinEntry is { } skin && skin.PathHash == chunk && MeshPreview.MaterialEditor.IsDirty) { MeshPreview.MaterialEditor.MarkStale(RevertedEditorStale); Say("Character window's Materials editor", skin); }
    }

    /// <summary>A save of an editor that was marked stale: refused, and said once (the auto-save asks again every few minutes). Not for a bin the GameData no longer names - that one is saved as any other.</summary>
    /// <returns>True when the save is refused.</returns>
    private bool RefusesStaleEditor(WadAssetEntry entry, string? staleReason)
    {
        if (staleReason is null || !IsGameDataTarget(entry.PathHash)) return false;
        bool saidBefore = _gameDataRefused.TryGetValue(entry.PathHash, out var known) && known.Bytes == StaleEditorKey && known.Reason == staleReason;
        _gameDataRefused[entry.PathHash] = (StaleEditorKey, staleReason);
        RefuseGameDataEdit(entry, staleReason, say: !saidBefore);
        return true;
    }

    // ============================================================================================ the export's baseline

    /// <summary>
    /// The planner's baselines for an export or a send (<see cref="IDeclarationBaselines"/>): where a project bin that the imported GameData also targets stands in the install order. Null for a project that
    /// stores no GameData (nothing changes for it). Nothing is made until the planner meets a bin of the project that the game also has, and then once: the setup the preview runs on when it is ready, else
    /// the same one made here (the export runs off the UI thread and does not wait for the preview). A GameData that cannot be bound to the game leaves the planner with none, said in the log, and a project bin is
    /// then declared against the game's as ever.
    /// </summary>
    private IDeclarationBaselines? DeclarationBaselinesForExport()
    {
        if (!LtkProjectStore.HasGameData(Project)) return null;
        var preview = _gameData;
        var project = Project;
        // the worker reads the layers while the person may be editing them in Project Settings: it reads a copy taken here
        var snapshot = new ReyProject
        {
            Name = project.Name, RootPath = project.RootPath, GameDirectory = project.GameDirectory,
            Layers = project.Layers.Select(l => new ProjectLayer { Name = l.Name, Priority = l.Priority, DeclarationsKey = l.DeclarationsKey, Folders = l.Folders.ToList() }).ToList(),
        };
        TryFindGameFolder(project, out string? gameDirectory, out string? gameProblem);
        string? cachePath = GameDataIndexCachePath, schemaPath = GameDataSchemaPath ?? ReyPaths.MetaDbFile, managerSchemaPath = GameDataManagerSchemaPath ?? GameDataSetups.DefaultManagerSchemaPath;
        var factory = GameDataGameFactory;
        var buildOverride = GameDataBuildOverride;
        return new LazyDeclarationBaselines(() =>
        {
            try
            {
                if (preview is { State: GameDataPreviewState.Ready, Setup: { } ready })
                    return new GameDataDeclarationBaselines(ready.Layers, preview.NamedChunks(), ready.Game, ready.Options);
                var setup = GameDataSetups.ForProject(snapshot, gameDirectory, null, schemaPath, cachePath, null, factory, buildOverride, gameProblem, managerSchemaPath);
                var targets = new GameDataOverlay(setup.Layers, setup.Game, null, setup.Options).Plan().Targets.Select(t => t.Chunk).ToHashSet();
                return new GameDataDeclarationBaselines(setup.Layers, targets, setup.Game, setup.Options);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warn("Export", $"The planner could not tell which of the project's bins the mod's GameData also targets ({ex.Message}); they are declared against the game's bins.");
                return null;
            }
        });
    }

    /// <summary>The baselines, made on the first bin the planner asks about.</summary>
    private sealed class LazyDeclarationBaselines(Func<IDeclarationBaselines?> make) : IDeclarationBaselines
    {
        private readonly Lazy<IDeclarationBaselines?> _inner = new(make, LazyThreadSafetyMode.ExecutionAndPublication);

        public DeclarationBaseline? For(DeclarationFile file, ulong chunk, byte[] game, byte[] mod) => _inner.Value?.For(file, chunk, game, mod);
    }

    // ============================================================================================ the project's edits as the other flows read them

    /// <summary>
    /// <see cref="ThrowIfGameDataTargetAsync"/> for a flow whose last step is the save of one bin through <see cref="SaveMapBinBytesAsync"/>: a bin the GameData changes passes (that save is a declaration, proven by
    /// <see cref="GameDataEditPreflightAsync"/> once the flow has the bytes it will save) when its preview works; every other bin is asked as before.
    /// </summary>
    /// <exception cref="InvalidOperationException">The bin cannot be saved: it is changed by the GameData and its edit cannot be kept, or the editor changed while the preview was waited for.</exception>
    private async ValueTask ThrowIfNotGameDataEditableAsync(WadAssetEntry entry)
    {
        if (_mounts is not null && IsBinEntry(entry))
        {
            if (!await SettleGameDataForEditAsync(entry)) throw new InvalidOperationException(GameDataProjectChangedRefusal);
            if (IsGameDataTarget(entry.PathHash))
            {
                if (GameDataEditBlocker(entry, out _, out _) is not { } why) return;
                RefuseGameDataEdit(entry, why);
                throw new InvalidOperationException(Status);
            }
        }
        await ThrowIfGameDataTargetAsync(entry);
    }

    /// <summary>
    /// <see cref="PlacementWriteRefusalAsync"/> for a placement that stages no file first (a prop placed in the map, a character converted): the two bins it ends in - the map's materials.bin and its own bin - may be ones the
    /// GameData changes, because each is saved through <see cref="SaveMapBinBytesAsync"/>, as a declaration; they are refused only when no declaration can be kept (the preview does not work).
    /// </summary>
    private async Task<string?> PlacementEditRefusalAsync(WadAssetEntry mapEntry)
    {
        var bins = new List<WadAssetEntry>();
        if (TryResolveMaterialsBin(mapEntry.Path, out var materials)) bins.Add(materials);
        if (MapBinPathFor(mapEntry.Path) is { } mapBinPath && TryResolveEntry(HashAlgorithms.WadPath(mapBinPath), out var mapBin)) bins.Add(mapBin);
        foreach (var bin in bins)
        {
            if (!await SettleGameDataForEditAsync(bin)) return GameDataProjectChangedRefusal;
            if (IsGameDataTarget(bin.PathHash))
            {
                if (GameDataEditBlocker(bin, out _, out _) is { } why) return $"'{bin.DisplayName}' cannot be saved on top of the mod's GameData: {why.TrimEnd('.')}. Nothing was written.";
            }
            else if (GameDataWriteRefusal(bin.PathHash, bin.DisplayName, isBin: true) is { } other) return other;
        }
        return null;
    }

    /// <summary>
    /// For a flow that stages other files before it saves its bin: whether the bin's edit can be kept on top of the GameData, found out BEFORE anything is staged - the edit itself (the bin the flow
    /// is about to write) is declared and proven now, without being kept. Null when the bin is none of the GameData's or the edit can be kept; the reason when it cannot.
    /// </summary>
    private async Task<string?> GameDataEditPreflightAsync(WadAssetEntry entry, byte[] bytes)
    {
        if (_mounts is null || !IsBinEntry(entry)) return null;
        if (!await SettleGameDataForEditAsync(entry)) return GameDataProjectChangedRefusal;
        if (!IsGameDataTarget(entry.PathHash)) return null;
        if (GameDataEditBlocker(entry, out var preview, out _) is { } why) return $"'{entry.DisplayName}' cannot be saved on top of the mod's GameData: {why}.";
        var names = _declarationNames ?? new DeclarationNames(_resolver.Database);
        var outcome = await Task.Run(() => PlanGameDataEdit(preview!, entry, bytes, names));
        return outcome.IsRefused ? $"'{entry.DisplayName}' cannot be saved on top of the mod's GameData: {outcome.Reason}. Nothing was written." : null;
    }
}
