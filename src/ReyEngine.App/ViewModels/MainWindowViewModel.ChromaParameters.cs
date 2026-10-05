using ReyEngine.App.Services;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.LtkGameData;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M825: Chroma Studio C3 - the host half of the Character window's colour PARAMETER recolour.
///
/// <para>The parameters live in the skin bin, which the Material tab's editor may hold open. So nothing here parses the editor's document
/// or writes a bin of its own: a save reads the bin the project SERVES now and Riot's untouched copy of it, changes only the values of
/// the parameters the recolour owns (<see cref="SkinColorParameters.Rewrite"/>: each one is Riot's value through the transform, so it
/// can never compound), and hands the whole bin to <see cref="SaveEditorBinBytesAsync"/> with the bytes it read as <c>openedFrom</c>.
/// That is the one path every bin editor saves through, so a bin the mod's GameData changes is kept as a declaration on top of it
/// (M823) and the M819 guards apply. A bin that moves while a save is prepared is read again and the values derived again - a bin the
/// GameData changes has the edit merged onto it by M823's path; any other is not merged, so it is never written over.</para>
///
/// <para>The Material tab: a document of this bin with unsaved edits is saved FIRST, through its own save, so its edits are in the bin
/// this reads; after the write the document is loaded again from the bin as served (selection and search kept), so the tab shows the
/// recoloured values and its next save starts from them. A recolour the card has only previewed never reaches the editor. A parameter the
/// recipe owns that the tab edited since is kept as it is, not overwritten, until the person switches it on again.</para>
///
/// <para>Where the bin goes: a bin the project already holds (a folder file or an override) is written where it is. A Riot bin the
/// project does not hold yet is first copied into the champion's WAD folder - Copy To Project of this one asset, as the textures'
/// writer places a texture - and then edited there, so Build Package and Export .fantome pack it. The recipe (transform and the
/// parameters it owns, never the values) goes in project.json beside the texture records.</para>
/// </summary>
public sealed partial class MainWindowViewModel
{
    private void WireChromaParameters()
    {
        MeshPreview.ReadChromaParameters = ReadChromaParameterSnapshot;
        MeshPreview.SaveChromaParameters = SaveChromaParametersAsync;
        MeshPreview.RevertChromaParameters = RevertChromaParametersAsync;
    }

    // ---- listing ----------------------------------------------------------------------------------------

    private static readonly IReadOnlyList<ChromaParameterInfo> NoParameters = Array.Empty<ChromaParameterInfo>();

    /// <summary>The skin bin's colour parameters with Riot's value beside the project's. On a worker, under the caller's reader lease. Never
    /// throws: a bin that cannot be read is an empty list with the reason. <paramref name="owned"/> and <paramref name="recipe"/> are the card's copies
    /// of the saved recipe, taken on the UI thread - this does not read the project's lists.</summary>
    private ChromaParameterSnapshot ReadChromaParameterSnapshot(string skinBin, IReadOnlySet<SkinColorParamKey> owned, ColorTransform recipe)
    {
        ulong hash = HashAlgorithms.WadPath(skinBin);
        byte[] current;
        try { current = ReadAsset(hash); }
        catch (Exception ex) { return new ChromaParameterSnapshot(NoParameters, $"{skinBin} could not be read: {ex.Message}"); }

        byte[]? riot = ReadChromaBaseBytes(hash, skinBin, out string? problem);
        if (riot is null)
            return new ChromaParameterSnapshot(NoParameters,
                $"The untouched skin bin cannot be read here ({problem ?? "no game folder or reference WAD is mounted"}), so its colour parameters cannot be recoloured: "
                + "a recolour is always derived from Riot's value, never from what the project holds.");

        IReadOnlyList<SkinColorParam> now, original;
        try
        {
            now = SkinColorParameters.Read(current, ResolveBinName, ResolveWadPath, ReadAssetByPath);
            original = SkinColorParameters.Read(riot, ResolveBinName, ResolveWadPath, ReadAssetByPath);
        }
        catch (Exception ex) { return new ChromaParameterSnapshot(NoParameters, $"The colour parameters of {skinBin} could not be read: {ex.Message}"); }

        // a bin the imported GameData changes: a material's parameters are a list of structs, which a declaration can express only with LTK's class schema
        bool gameData = skinBin.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) && IsGameDataTarget(hash);
        var held = new Dictionary<SkinColorParamKey, SkinColorParam>();
        foreach (var p in now) held.TryAdd(p.Key, p);

        var list = new List<ChromaParameterInfo>(original.Count);
        foreach (var o in original)
        {
            if (!held.TryGetValue(o.Key, out var h))
            {
                list.Add(new ChromaParameterInfo(o.Key, o.MaterialName, o.TypeName, o.Value, o.Value, false,
                    "The project's skin bin no longer has this parameter.", false, o.DrivenBy));
                continue;
            }
            bool ok = o.Recolourable && h.Recolourable;
            string reason = !o.Recolourable ? o.Reason : (!h.Recolourable ? "In the project's bin: " + h.Reason : "");
            bool isOwned = owned.Contains(o.Key);
            bool edited = ok && !isOwned && !SkinColorParameters.SameRgb(h.Value, o.Value);
            // the recipe owns it, but the project no longer holds what the recipe wrote: somebody edited it since (in the Material tab)
            bool ownedEdited = ok && isOwned && !SkinColorParameters.HoldsRecipeValue(h.Value, o.Value, recipe, o.TypeName == "Color");
            string? defaultOff = ok && gameData && o.Key.Material != 0
                ? "This skin bin is changed by the imported mod's GameData: a material's parameters can be kept on top of it only with LTK's class schema, "
                  + "and without it the save is refused. Off until you switch it on."
                : null;
            list.Add(new ChromaParameterInfo(o.Key, o.MaterialName, o.TypeName, o.Value, h.Value, ok, reason, edited, o.DrivenBy ?? h.DrivenBy, ownedEdited, defaultOff));
        }
        return new ChromaParameterSnapshot(list);
    }

    /// <summary>
    /// The skin bin as it was before any recolour of ours: what every value is derived from. Riot's own bin, read from the reference WADs, never from
    /// what the project serves - except for a bin the imported LTK GameData changes: there the untouched bin is the one LTK makes of the package's
    /// GameData (the game's bin with the package's modules on it, without the edits made on top - <see cref="GameDataPreview.TryReadImportedOnly"/>), because
    /// the package's own changes to a parameter are the mod's and not ours to undo. Null with a reason when it cannot be read.
    /// </summary>
    private byte[]? ReadChromaBaseBytes(ulong hash, string path, out string? problem)
    {
        problem = null;
        try
        {
            if (path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) && IsGameDataTarget(hash))
            {
                if (GameData is { } preview && preview.TryReadImportedOnly(hash, out var made, out problem)) return made;
                problem ??= "the GameData preview is not available";
                return null;
            }
            return ReadRiotOriginalBytes(new WadAssetEntry { PathHash = hash, Path = path });
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { problem = ex.Message; return null; }
    }

    private ChromaParameterRecord? ChromaParameterRecordOf(string skinBin) =>
        Project.ChromaParameterRecolors?.FirstOrDefault(r => string.Equals(r.ChromaSkin, skinBin, StringComparison.OrdinalIgnoreCase));

    // ---- saving and reverting -----------------------------------------------------------------------------

    private static SkinColorParamKey KeyOfRef(ChromaParameterRef r) => new(r.Material, r.Name, r.Occurrence);

    /// <summary>Apply &amp; Save, Ctrl+S, the auto-save and Export / Build Package: the recolour's parameters into the skin bin. The parameters
    /// in <paramref name="targets"/> become Riot's value through <paramref name="transform"/>; those in <paramref name="stale"/> (the earlier
    /// recipe's, no longer wanted) go back to Riot's - unless the Material tab edited them since the recolour, which is then kept.</summary>
    private async Task<ChromaParamSaveResult> SaveChromaParametersAsync(string skinBin, ColorTransform transform,
        IReadOnlyList<ChromaParameterRef> targets, IReadOnlyList<ChromaParameterRef> stale)
    {
        if (!await EnsureProjectSavedAsync())
            throw new InvalidOperationException("The project has to be saved before a recolour can be written into it.");
        // an identity transform changes nothing: whatever the recipe owned goes back to Riot's and nothing is owned afterwards
        bool recolour = !transform.IsIdentity && targets.Count > 0;
        return await ApplyChromaParameterEditAsync(skinBin, transform,
            recolour ? targets : Array.Empty<ChromaParameterRef>(), recolour ? stale : stale.Concat(targets).ToList(), force: false);
    }

    /// <summary>Revert: these parameters back to Riot's, whatever they hold now, and the skin's record gone.</summary>
    private async Task<int> RevertChromaParametersAsync(string skinBin, IReadOnlyList<ChromaParameterRef> parameters)
    {
        var result = await ApplyChromaParameterEditAsync(skinBin, ColorTransform.Identity, Array.Empty<ChromaParameterRef>(), parameters, force: true);
        return result.Reverted;
    }

    /// <param name="force">Put the <paramref name="restore"/> parameters back even where the bin no longer holds what the recipe wrote (Revert is explicit).</param>
    private async Task<ChromaParamSaveResult> ApplyChromaParameterEditAsync(string skinBin, ColorTransform transform,
        IReadOnlyList<ChromaParameterRef> recolour, IReadOnlyList<ChromaParameterRef> restore, bool force)
    {
        ulong hash = HashAlgorithms.WadPath(skinBin);
        if (!TryResolveEntry(hash, out var entry))
            throw new InvalidOperationException($"{skinBin} is neither in the project nor in the game files that are open.");

        // 1. the Material tab: a document of this very bin that holds edits the project does not have yet is saved first, through its own save, so
        //    those edits are in the bin read below and the recolour is written on top of them rather than beside them. (IsDirty says the document
        //    differs from what it was opened with, which stays true after a save: the bin as served is what tells saved from unsaved.)
        var editor = MeshPreview.MaterialEditor;
        bool sameBin = editor.BinEntry is { } open && open.PathHash == hash;
        if (sameBin && editor.IsDirty && HoldsUnsavedMaterialEdits(editor, hash))
        {
            await SaveCharacterMaterialOverride();
            EnsureMountsServe(hash, editor.Serialize());   // the editor stores an override, which the mounts show only once they are rebuilt
            if (HoldsUnsavedMaterialEdits(editor, hash))
                throw new InvalidOperationException("The Material tab holds unsaved edits to this skin bin that could not be saved, and a recolour must not overwrite them. Save or revert them there first.");
        }

        var recolourKeys = recolour.Select(KeyOfRef).ToHashSet();
        var restoreKeys = restore.Select(KeyOfRef).ToHashSet();
        var record = ChromaParameterRecordOf(skinBin);
        // an explicit Revert puts everything back; any other save gives back only what still holds what the recipe wrote
        ColorTransform? recipe = force ? null : record?.Transform;

        // 2. Riot's value to derive from, and the bin as the project serves it now - read again, and the values derived again, when the bin moved
        //    while the write was being prepared (a guard, a copy into the project, the editor's own save): nothing is written over a bin that is not
        //    the one the values were derived into
        byte[] riot, current;
        SkinColorParamRewrite rewrite;
        string? placed = null;
        for (int attempt = 1; ; attempt++)
        {
            riot = ReadChromaBaseBytes(hash, skinBin, out string? why)
                ?? throw new InvalidOperationException($"The untouched skin bin cannot be read ({why ?? "no game folder or reference WAD is mounted"}), so the colour parameters cannot be re-derived. Nothing was written.");
            current = ReadAsset(hash);
            rewrite = SkinColorParameters.Rewrite(current, riot, transform, recolourKeys, restoreKeys, ResolveBinName, ResolveWadPath, recipe);

            // 3. the write, only when a value changed
            if (rewrite.Bytes is not { } bytes || bytes.AsSpan().SequenceEqual(current)) break;
            var written = await WriteChromaSkinBinAsync(entry, riot, current, bytes);
            placed ??= written.PlacedFile;   // a copy a first attempt placed is still ours when the second one writes
            if (written.Moved)
            {
                if (attempt >= 3) throw new InvalidOperationException("The skin bin was changed by something else again and again while the recolour was being written. Nothing was written; try again.");
                continue;
            }
            EnsureMountsServe(hash, bytes);   // a bin stored as an override is served only once the mounts are rebuilt (M824's textures do the same after a write)
            if (sameBin) ReloadCharacterMaterialEditor(entry, current);
            break;
        }

        // 4. the recipe
        var settled = rewrite.Edits
            .Where(e => e.Outcome is SkinColorParamOutcome.Written or SkinColorParamOutcome.Unchanged)
            .Select(e => e.Key).ToList();
        var owned = recolour.Where(r => settled.Contains(KeyOfRef(r))).ToList();
        bool recordGone = PersistChromaParameterRecord(skinBin, transform, owned, recolourKeys, restoreKeys, placed, out string? placedFile);
        if (recordGone && placedFile is not null) RemovePlacedSkinBin(entry, riot, placedFile);

        // what the card is told: Settled is the parameters the recipe OWNS now (the ones it recoloured), never the ones it gave back
        var ownedKeys = settled.Where(recolourKeys.Contains).ToList();
        int reverted = rewrite.Edits.Count(e => restoreKeys.Contains(e.Key) && !recolourKeys.Contains(e.Key)
                                                && e.Outcome is SkinColorParamOutcome.Written or SkinColorParamOutcome.Unchanged);
        int kept = rewrite.Edits.Count(e => e.Outcome == SkinColorParamOutcome.Kept);
        var notes = rewrite.Edits.Where(e => e.Note.Length > 0).Select(e => $"{e.Key.Name}: {e.Note}").ToList();
        var result = new ChromaParamSaveResult(
            rewrite.Edits.Count(e => e.Outcome == SkinColorParamOutcome.Written && recolourKeys.Contains(e.Key)),
            rewrite.Edits.Count(e => e.Outcome == SkinColorParamOutcome.Unchanged && recolourKeys.Contains(e.Key)),
            rewrite.Edits.Count(e => e.Outcome is SkinColorParamOutcome.Skipped or SkinColorParamOutcome.Missing),
            reverted, ownedKeys, notes);
        string line = $"Colour parameters of {skinBin}: {result.Summary}" + (kept > 0 ? $", {kept} edited since and kept as they are" : "") + ".";
        if (result.Skipped > 0) _log.Warn("Chroma", line + (notes.Count > 0 ? " " + notes[0] : ""));
        else _log.Success("Chroma", line);
        return result;
    }

    /// <summary>What a write did: the bin moved under it (nothing was written), or it was written and the project file the recolour copied in for it, if any.</summary>
    private readonly record struct ChromaBinWrite(bool Moved, string? PlacedFile);

    /// <summary>Put the recoloured bin into the project through the bin save path. A bin the mod's GameData changes goes as a declaration (its
    /// guard first); any other Riot bin the project does not hold yet is copied into the champion's WAD folder first.</summary>
    private async Task<ChromaBinWrite> WriteChromaSkinBinAsync(WadAssetEntry entry, byte[] riot, byte[] current, byte[] edited)
    {
        string? placed = null;
        if (IsBinEntry(entry) && IsGameDataTarget(entry.PathHash))
        {
            if (!await GuardBinEditAsync(entry))
                throw new InvalidOperationException($"'{entry.DisplayName}' cannot be changed on top of the mod's GameData right now: {Status}");
        }
        else placed = PlaceChromaSkinBinInProject(entry, riot);

        // the awaits above are where something else can save the bin; a non-GameData save does not merge (the GameData path does), so the values are derived again
        try { if (!ReadAsset(entry.PathHash).AsSpan().SequenceEqual(current)) return new ChromaBinWrite(true, placed); }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw new InvalidOperationException($"'{entry.DisplayName}' could not be read again before it was written: {ex.Message}"); }

        if (!await SaveEditorBinBytesAsync(entry, edited, current, "Chroma", holdsDocument: false))
            throw new InvalidOperationException($"'{entry.DisplayName}' could not be saved ({Status}). Nothing of the recolour was kept in the project.");
        return new ChromaBinWrite(false, placed);
    }

    /// <summary>Copy To Project of the one skin bin, when the project does not hold it: Riot's bytes into the champion's WAD folder of a folder
    /// project (a non-folder project stores the edit as the override). Whatever the project already holds is left alone. Returns the project file
    /// (relative to the project folder) it placed, or null when it placed none.</summary>
    private string? PlaceChromaSkinBinInProject(WadAssetEntry entry, byte[] riot)
    {
        if (_mounts is null || !Project.IsFolderProject) return null;
        if (_mounts.TryGet(entry.PathHash, out var served) && served.Source.Kind != AssetSourceKind.RiotReference) return null;   // the project has its own copy
        if (_overrides.Has(entry.PathHash)) return null;
        string placed;
        try
        {
            // M826: a dependency bin can carry a name no file can have - Ahri's Multi_Skins bin is 430 characters, past the 255 a file name may hold. It is placed as the
            // loose <hash>.bin at the WAD folder's root, which the packer reads as that chunk (the form an unnamed texture takes), and which the mounts serve like any project file.
            // (An override would not do: Build Package and .fantome pack a folder project's FILES, and an override is not one.)
            bool fileCannotHaveThatName = entry.Path.Replace('\\', '/').Split('/').Any(segment => segment.Length > 250);
            if (fileCannotHaveThatName || !TryPlaceInProjectFolder(entry, riot, out placed))
                if (!TryPlaceByHash(entry, riot, out placed)) return null;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            _log.Info("Chroma", $"{entry.DisplayName} could not be copied into the project under its name ({ex.Message.TrimEnd('.')}): it is placed under its hash.");
            if (!TryPlaceByHash(entry, riot, out placed)) return null;
        }
        Project.IsDirty = true;
        _overrides.SaveTo(Project);
        if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
        BuildMounts();
        BuildProjectTree();
        _log.Info("Chroma", $"Copied {entry.DisplayName} into the project ({placed}) before changing its colour parameters.");
        return placed;
    }

    /// <summary>The loose <c>&lt;hash&gt;.bin</c> form of Copy To Project at the champion's WAD folder: for a chunk whose path cannot be a file name.</summary>
    private bool TryPlaceByHash(WadAssetEntry entry, byte[] bytes, out string placedRelative)
    {
        placedRelative = "";
        if (!Project.IsFolderProject || Project.RootPath is null) return false;
        string folderName = RiotWadFolderName(entry);
        if (!AssetPathSafety.IsSafeFileName(folderName)) return false;
        string fileName = $"{entry.PathHash:x16}.bin";
        if (!AssetPathSafety.TryCombineUnder(Path.Combine(Project.RootPath, folderName), fileName, out string dest)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllBytes(dest, bytes);
        if (!Project.ProjectFolders.Contains(folderName, StringComparer.OrdinalIgnoreCase)) Project.ProjectFolders.Add(folderName);
        placedRelative = $"{folderName}/{fileName}";
        return true;
    }

    /// <summary>The recipe gave its last parameter back: the copy of the skin bin the recolour placed in the project is removed again - if it is still Riot's data
    /// (somebody's later edit of it stays). Only the file the record names, and only below the project folder.</summary>
    private void RemovePlacedSkinBin(WadAssetEntry entry, byte[] riot, string placedFile)
    {
        try
        {
            if (!Project.IsFolderProject || Project.RootPath is not { } root
                || !AssetPathSafety.TryCombineUnder(root, placedFile, out string file) || !File.Exists(file)) return;
            if (BinTreeEquivalence.FirstDifference(SafeBinTree.Parse(riot), SafeBinTree.Parse(File.ReadAllBytes(file))) is not null)
            {
                _log.Info("Chroma", $"{placedFile} was changed since the recolour copied it into the project, so it stays.");
                return;
            }
            File.Delete(file);
            Project.IsDirty = true;
            BuildMounts();
            BuildProjectTree();
            _log.Info("Chroma", $"Removed {placedFile}: with the colour parameters given back it was Riot's skin bin again.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.Warn("Chroma", $"Could not remove the copy of {entry.DisplayName} the recolour placed in the project: {ex.Message}"); }
    }

    /// <summary>Whether the Material tab's document of this bin differs, as data, from the bin the project serves: edits it has not saved. A document
    /// that cannot be serialised or compared counts as holding some (the recolour then refuses rather than risk overwriting them).</summary>
    private bool HoldsUnsavedMaterialEdits(MaterialEditorViewModel editor, ulong hash)
    {
        try { return editor.Serialize() is { } edited && !ServesBinData(hash, edited); }
        catch { return true; }
    }

    /// <summary>Two bins hold the same data: byte for byte, or the same objects and properties (the writer orders them its own way).</summary>
    private static bool SameBinData(byte[] a, byte[] b) =>
        a.AsSpan().SequenceEqual(b) || BinTreeEquivalence.FirstDifference(SafeBinTree.Parse(a), SafeBinTree.Parse(b)) is null;

    /// <summary>The project serves this bin's data as <paramref name="expected"/>.</summary>
    private bool ServesBinData(ulong hash, byte[] expected)
    {
        try { return SameBinData(ReadAsset(hash), expected); }
        catch { return false; }
    }

    /// <summary>After a bin was saved: if the mounts do not serve it yet (a bin stored in the override store shows only after they are rebuilt), rebuild them.</summary>
    private void EnsureMountsServe(ulong hash, byte[]? saved)
    {
        if (saved is null || ServesBinData(hash, saved)) return;
        BuildMounts();
        BuildProjectTree();
    }

    /// <summary>
    /// After the bin was written: the Material tab's document is read again from the bin as served, so the tab shows the recoloured values and a later
    /// Material-tab save and scene rebuild start from them. The selected material and the search stay. Not done when the document has changed since
    /// <paramref name="before"/> (the bin it was equal to): edits typed while the save was awaited are not thrown away - the tab keeps them and its own
    /// save merges them onto the recoloured bin. Afterwards the preview is built again from the document, so that a scene the editor's own save started
    /// before the recolour cannot land after it and show the old colours.
    /// </summary>
    private void ReloadCharacterMaterialEditor(WadAssetEntry entry, byte[] before)
    {
        var editor = MeshPreview.MaterialEditor;
        if (editor.BinEntry is not { } open || open.PathHash != entry.PathHash) return;
        try
        {
            if (editor.IsDirty && editor.Serialize() is { } now && !SameBinData(now, before))
            {
                _log.Info("Chroma", "The Material tab changed while the recolour was saved, so it keeps its document; its next save merges onto the recoloured bin.");
                return;
            }
            string? selectedName = editor.SelectedMaterial?.Name;
            uint selectedHash = editor.SelectedMaterial?.Model.ObjectPathHash ?? 0;
            string search = editor.Search;
            bool onlyUnresolved = editor.OnlyUnresolved;

            byte[] served = ReadAsset(entry.PathHash);
            var doc = MaterialDocument.Parse(served, ResolveBinName, ResolveWadPath, ReadAssetByPath);
            editor.Load(doc, open, served);

            if (search.Length > 0) editor.Search = search;
            if (onlyUnresolved) editor.OnlyUnresolved = true;
            if (selectedName is not null)
                editor.SelectedMaterial = editor.FilteredMaterials.FirstOrDefault(m => m.Name == selectedName && m.Model.ObjectPathHash == selectedHash) ?? editor.SelectedMaterial;
            ApplyCharacterMaterialsToPreview();
        }
        catch (Exception ex) { _log.Warn("Chroma", $"The Material tab could not be refreshed with the recoloured bin ({ex.Message}); reopen the skin to see the new values there."); }
    }

    /// <summary>
    /// The recipe in project.json: one record per skin bin. What it keeps: the parameters it owned that this save neither recoloured nor gave back
    /// (the carried ones - a parameter the card has no switch for stays owned, and keeps its Revert after a reload) plus the ones just recoloured;
    /// the file the recolour placed, once. The record goes when nothing is left, and the list goes back to null with the last record, so a project that
    /// never recoloured a parameter has no new key. Returns whether the record is gone, and the file it had placed.
    /// </summary>
    private bool PersistChromaParameterRecord(string skinBin, ColorTransform transform, IReadOnlyList<ChromaParameterRef> owned,
        HashSet<SkinColorParamKey> recolourKeys, HashSet<SkinColorParamKey> restoreKeys, string? placedNow, out string? placedFile)
    {
        var existing = ChromaParameterRecordOf(skinBin);
        var kept = existing?.Parameters.Where(p => !recolourKeys.Contains(KeyOfRef(p)) && !restoreKeys.Contains(KeyOfRef(p))).ToList() ?? new List<ChromaParameterRef>();
        var parameters = kept.Concat(owned).ToList();
        placedFile = placedNow ?? existing?.PlacedFile;
        if (existing is null && parameters.Count == 0) return false;

        var list = Project.ChromaParameterRecolors;
        list?.Remove(existing!);
        bool gone = parameters.Count == 0;
        if (!gone)
        {
            list ??= Project.ChromaParameterRecolors = new List<ChromaParameterRecord>();
            list.Add(new ChromaParameterRecord
            {
                ChromaSkin = skinBin,
                Transform = owned.Count > 0 ? transform : existing?.Transform ?? transform,
                Parameters = parameters,
                PlacedFile = placedFile,
            });
        }
        if (list is { Count: 0 }) Project.ChromaParameterRecolors = null;
        Project.IsDirty = true;
        _overrides.SaveTo(Project);
        if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
        UpdateTitle();
        return gone;
    }
}
