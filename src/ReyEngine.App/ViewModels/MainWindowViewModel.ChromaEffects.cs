using ReyEngine.App.Services;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Particles;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M826: Chroma Studio C4 - the host half of the Character window's EFFECTS RECOLOUR.
///
/// <para>The skin's effects are VfxSystemDefinitionData objects in the skin bin and in the dependency bins it links (the champion's shared Multi_Skins bins, its
/// root bin...). Their colour VALUES (<c>birthColor</c>, <c>color</c>, the linger and fresnel colours) are rewritten in those bins, their colour TEXTURES through
/// the body recolour's texture pipeline. Nothing here is a second pipeline: a bin is read as the project serves it and as Riot ships it
/// (<see cref="ReadChromaBaseBytes"/>), <see cref="SkinEffectColors.Rewrite"/> derives every changed value from Riot's (so a value can never compound), and the
/// whole bin goes through <see cref="WriteChromaSkinBinAsync"/> - M825's write: <see cref="SaveEditorBinBytesAsync"/> with the bytes it read as <c>openedFrom</c>
/// (the M819 guards, M823's declarations), a Riot bin the project does not hold first copied into the champion's WAD folder, the copy remembered so a revert can
/// take it away again.</para>
///
/// <para>The Particle Editor edits the same bins: a document of the bin with unsaved edits is saved FIRST through its own save, so those edits are in the bin this reads,
/// and after the write the editor's document is read again from the bin as served (the selected system kept), so its next save starts from the recolour.</para>
/// </summary>
public sealed partial class MainWindowViewModel
{
    private void WireChromaEffects()
    {
        MeshPreview.ReadChromaEffects = ReadChromaEffectSnapshot;
        MeshPreview.SaveChromaEffectColors = SaveChromaEffectColorsAsync;
        MeshPreview.RevertChromaEffectColors = RevertChromaEffectColorsAsync;
        MeshPreview.SaveChromaEffectTextures = SaveChromaEffectTexturesAsync;
        MeshPreview.RevertChromaEffectTextures = RevertChromaEffectTextures;
    }

    private static EffectColorKey EffectKeyOf(ChromaEffectColorRef r) => new(r.System, r.Emitter, r.Field);

    private ChromaEffectRecord? ChromaEffectRecordOf(string skinBin) =>
        Project.ChromaEffectRecolors?.FirstOrDefault(r => string.Equals(r.ChromaSkin, skinBin, StringComparison.OrdinalIgnoreCase));

    private ChromaSavedEffects? ReadChromaSavedEffects(string skinBin)
    {
        var record = ChromaEffectRecordOf(skinBin);
        var textures = Project.TextureRecolors
            .Where(r => r.Transform is not null && r.ChromaPart == TextureRecolorRecord.EffectsPart && string.Equals(r.ChromaSkin, skinBin, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (record is null && textures.Count == 0) return null;
        return new ChromaSavedEffects(record?.Transform ?? textures[0].Transform!, record?.Colors.ToList() ?? new List<ChromaEffectColorRef>(),
            textures.Select(r => new ChromaTarget(r.PathHash, r.AssetPath)).ToList());
    }

    // ---- listing ----------------------------------------------------------------------------------------

    /// <summary>The effect colour fields of the systems the skin uses, Riot's value beside the project's, and the working set the live preview evaluates. On a worker, under
    /// the caller's reader lease. Never throws: a bin that cannot be read is a line in <see cref="ChromaEffectSnapshot.Problem"/>.</summary>
    private ChromaEffectSnapshot ReadChromaEffectSnapshot(string skinBin, IReadOnlyList<EffectSystemEntry> systems, IReadOnlySet<EffectColorKey> owned, ColorTransform recipe)
    {
        var infos = systems.Select(s => new ChromaEffectSystemInfo(s.PathHash, s.Name, s.Bin, s.SharedWith, s.IsInferred, string.Join(", ", s.ReachedBy), s.ParticlePath)).ToList();
        var fields = new List<ChromaEffectFieldInfo>();
        var excluded = new List<EffectExcludedField>();
        var problems = new List<string>();
        var bins = new List<(string Bin, byte[] Current, byte[] Riot)>();
        var wanted = new Dictionary<string, ISet<uint>>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in systems.GroupBy(s => s.Bin, StringComparer.OrdinalIgnoreCase))
        {
            string bin = group.Key;
            ulong hash = HashAlgorithms.WadPath(bin);
            var ids = group.Select(s => s.PathHash).ToHashSet();
            byte[] current;
            try { current = ReadAsset(hash); }
            catch (Exception ex) { problems.Add($"{bin} could not be read: {ex.Message}"); continue; }
            byte[]? riot = ReadChromaBaseBytes(hash, bin, out string? why);
            if (riot is null)
            {
                problems.Add($"The untouched {Path.GetFileName(bin)} cannot be read here ({why ?? "no game folder or reference WAD is mounted"}), so its effect colours cannot be recoloured: "
                             + "a recolour is always derived from Riot's value, never from what the project holds.");
                continue;
            }

            EffectColorScan now, original;
            try
            {
                now = SkinEffectColors.Read(current, ResolveBinName, ids, includeExcluded: false);
                original = SkinEffectColors.Read(riot, ResolveBinName, ids);
            }
            catch (Exception ex) { problems.Add($"The effect colours of {bin} could not be read: {ex.Message}"); continue; }

            bool gameData = IsGameDataTarget(hash);
            var held = new Dictionary<EffectColorKey, EffectColorField>();
            foreach (var f in now.Fields) held.TryAdd(f.Key, f);
            var recipeCarriers = SkinEffectColors.Carriers(original.Fields.Where(f => owned.Contains(f.Key) && f.Recolourable));
            var savedRecord = ChromaEffectRecordOf(skinBin);
            var binRecipe = savedRecord is { BinTransforms: not null } ? savedRecord.TransformOf(bin) : recipe;   // an interrupted multi-bin save left this bin at another transform
            var recordedNames = savedRecord?.Systems.Where(s => string.Equals(s.Bin, bin, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var o in original.Fields)
            {
                if (!held.TryGetValue(o.Key, out var h))
                {
                    fields.Add(new ChromaEffectFieldInfo(o, bin, false, "The project's bin no longer has this field.", false, false, null));
                    continue;
                }
                string? renamed = RenamedEmitter(recordedNames, o);
                bool ok = o.Recolourable && h.Recolourable && o.Keys.Count == h.Keys.Count && (o.Constant is null) == (h.Constant is null) && renamed is null;
                string reason = !o.Recolourable ? o.Reason
                    : !h.Recolourable ? "In the project's bin: " + h.Reason
                    : renamed is not null ? renamed
                    : !ok ? "The project's bin holds this field in another shape than Riot's (keys added or removed): left as it is." : "";
                bool isOwned = owned.Contains(o.Key);
                bool same = o.Values.SequenceEqual(h.Values, SameRgbComparer.Instance);
                bool edited = ok && !isOwned && !same;
                bool ownedEdited = ok && isOwned && !SkinEffectColors.HoldsRecipeValue(h, o, SkinEffectColors.TransformFor(o.Key, binRecipe, recipeCarriers));
                string? defaultOff = ok && gameData
                    ? "This bin is changed by the imported mod's GameData: a colour inside a particle system can be kept on top of it only if the declaration can address it, and when it cannot "
                      + "the save is refused. Off until you switch it on."
                    : null;
                fields.Add(new ChromaEffectFieldInfo(o, bin, ok, reason, edited, ownedEdited, defaultOff));
            }
            excluded.AddRange(original.Excluded);
            bins.Add((bin, current, riot));
            wanted[bin] = ids;
        }

        EffectColorWorkingSet? preview = null;
        if (bins.Count > 0)
        {
            try { preview = new EffectColorWorkingSet(bins, wanted); }
            catch (Exception ex) { problems.Add("The live preview of the effect colours could not be prepared: " + ex.Message); }
        }
        return new ChromaEffectSnapshot(infos, fields, excluded, string.Join("\n", problems), preview);
    }

    /// <summary>The recorded name of the emitter this field was recoloured in, when Riot's bin now calls that ordinal something else: a patch inserted or removed an emitter, and the
    /// ordinal the recipe holds would point at another one. Null when the emitter still is the one (or the recipe never recorded a name).</summary>
    private static string? RenamedEmitter(IReadOnlyList<ChromaEffectSystemRef>? recorded, EffectColorField riot)
    {
        if (recorded is null) return null;
        foreach (var s in recorded)
            if (s.System == riot.Key.System && s.EmitterNames is { } names && names.TryGetValue(riot.Key.Emitter, out var was)
                && s.Fields.Any(f => f.StartsWith(riot.Key.Emitter + ":", StringComparison.Ordinal)) && !string.Equals(was, riot.EmitterName, StringComparison.Ordinal))
                return $"Riot's bin changed since the recolour: emitter {riot.Key.Emitter} was called '{was}' and is now '{riot.EmitterName}', so the recipe may point at another emitter. Left as it is.";
        return null;
    }

    /// <summary>The keys of this save whose emitter Riot's bin no longer has under the name the recipe recorded (see <see cref="RenamedEmitter"/>), with the line that says so. Only keys the
    /// recipe owns can be affected: a key nobody recoloured yet has no recorded name.</summary>
    private Dictionary<EffectColorKey, string> RenamedEmitters(byte[] riot, string bin, IReadOnlyList<ChromaEffectColorRef> recipeBin,
        HashSet<EffectColorKey> recolourKeys, HashSet<EffectColorKey> restoreKeys)
    {
        var result = new Dictionary<EffectColorKey, string>();
        var recorded = recipeBin.Where(c => c.EmitterName.Length > 0).ToList();
        if (recorded.Count == 0) return result;
        var systems = new ChromaEffectRecord { Colors = recorded }.Systems;
        var ids = recorded.Select(c => c.System).ToHashSet();
        foreach (var field in SkinEffectColors.Read(riot, ResolveBinName, ids, includeExcluded: false).Fields)
            if ((recolourKeys.Contains(field.Key) || restoreKeys.Contains(field.Key)) && RenamedEmitter(systems, field) is { } line) result[field.Key] = line;
        return result;
    }

    private sealed class SameRgbComparer : IEqualityComparer<System.Numerics.Vector4>
    {
        public static readonly SameRgbComparer Instance = new();
        public bool Equals(System.Numerics.Vector4 a, System.Numerics.Vector4 b) => SkinEffectColors.SameRgb(a, b);
        public int GetHashCode(System.Numerics.Vector4 v) => v.GetHashCode();
    }

    // ---- saving and reverting -----------------------------------------------------------------------------

    /// <summary>Apply &amp; Save, Ctrl+S, the auto-save and Export / Build Package: the effect colours into their bins. The fields in <paramref name="targets"/> become Riot's
    /// value through <paramref name="transform"/>; those in <paramref name="stale"/> (the earlier recipe's, no longer wanted) go back to Riot's - unless the Particle Editor
    /// edited them since the recolour, which is then kept.</summary>
    private async Task<ChromaEffectSaveResult> SaveChromaEffectColorsAsync(string skinBin, ColorTransform transform,
        IReadOnlyList<ChromaEffectColorRef> targets, IReadOnlyList<ChromaEffectColorRef> stale)
    {
        if (!await EnsureProjectSavedAsync())
            throw new InvalidOperationException("The project has to be saved before a recolour can be written into it.");
        bool recolour = !transform.IsIdentity && targets.Count > 0;
        return await ApplyChromaEffectColorsAsync(skinBin, transform,
            recolour ? targets : Array.Empty<ChromaEffectColorRef>(), recolour ? stale : stale.Concat(targets).ToList(), force: false);
    }

    /// <summary>Revert: these fields back to Riot's, whatever they hold now, and the skin's record gone.</summary>
    private async Task<int> RevertChromaEffectColorsAsync(string skinBin, IReadOnlyList<ChromaEffectColorRef> colors)
    {
        var result = await ApplyChromaEffectColorsAsync(skinBin, ColorTransform.Identity, Array.Empty<ChromaEffectColorRef>(), colors, force: true);
        return result.Reverted;
    }

    private async Task<ChromaEffectSaveResult> ApplyChromaEffectColorsAsync(string skinBin, ColorTransform transform,
        IReadOnlyList<ChromaEffectColorRef> recolour, IReadOnlyList<ChromaEffectColorRef> restore, bool force)
    {
        var settledAll = new List<EffectColorKey>();
        var edits = new List<EffectColorEdit>();
        int kept = 0;

        // the recipe as it stood BEFORE this save, read once: every bin is judged against it (a restore key is "kept" when the project's value is no longer what THAT recipe wrote),
        // however many bins this save has already rewritten. The record itself moves bin by bin, and its transform only after the last bin succeeded.
        var before = force ? null : ChromaEffectRecordOf(skinBin);
        var recipeRefs = before?.Colors ?? new List<ChromaEffectColorRef>();
        var recipeTransforms = before is null ? null : new Dictionary<string, ColorTransform>(StringComparer.OrdinalIgnoreCase);
        if (before is not null)
            foreach (var b in recipeRefs.Select(r => r.Bin).Distinct(StringComparer.OrdinalIgnoreCase)) recipeTransforms![b] = before.TransformOf(b);

        foreach (var group in recolour.Concat(restore).GroupBy(r => r.Bin, StringComparer.OrdinalIgnoreCase))
        {
            string bin = group.Key;
            ulong hash = HashAlgorithms.WadPath(bin);
            if (!TryResolveEntry(hash, out var entry))
                throw new InvalidOperationException($"{bin} is neither in the project nor in the game files that are open.");

            var recolourRefs = recolour.Where(r => string.Equals(r.Bin, bin, StringComparison.OrdinalIgnoreCase)).ToList();
            var restoreRefs = restore.Where(r => string.Equals(r.Bin, bin, StringComparison.OrdinalIgnoreCase)).ToList();
            var recolourKeys = recolourRefs.Select(EffectKeyOf).ToHashSet();
            var restoreKeys = restoreRefs.Select(EffectKeyOf).ToHashSet();
            var recipeBin = recipeRefs.Where(c => string.Equals(c.Bin, bin, StringComparison.OrdinalIgnoreCase)).ToList();

            // 1. the editors that may hold this bin with edits the project does not have yet are saved first, through their own saves
            await SettleOtherBinEditorsAsync(entry);

            ColorTransform? recipe = recipeTransforms is not null && recipeTransforms.TryGetValue(bin, out var recipeTransform) ? recipeTransform : before?.Transform;
            var recipeKeys = before is null ? null : recipeBin.Select(EffectKeyOf).ToList();

            // 2. Riot's value to derive from and the bin as served now - read again, and the values derived again, when the bin moved while the write was prepared
            byte[] riot, current;
            EffectColorRewrite rewrite;
            Dictionary<EffectColorKey, string> renamed;
            string? placed = null;
            for (int attempt = 1; ; attempt++)
            {
                riot = ReadChromaBaseBytes(hash, bin, out string? why)
                    ?? throw new InvalidOperationException($"The untouched {Path.GetFileName(bin)} cannot be read ({why ?? "no game folder or reference WAD is mounted"}), so the effect colours cannot be re-derived. Nothing was written.");
                current = ReadAsset(hash);
                renamed = RenamedEmitters(riot, bin, recipeBin, recolourKeys, restoreKeys);   // emitters Riot's bin has renumbered since the recipe was made are not touched
                rewrite = SkinEffectColors.Rewrite(current, riot, transform, recolourKeys.Except(renamed.Keys).ToHashSet(), restoreKeys.Except(renamed.Keys).ToHashSet(), recipe, recipeKeys);

                // 3. the write, only when a value changed
                if (rewrite.Bytes is not { } bytes || bytes.AsSpan().SequenceEqual(current)) break;
                var written = await WriteChromaSkinBinAsync(entry, riot, current, bytes);
                placed ??= written.PlacedFile ?? PlacedByTheParameterRecolour(skinBin, bin);
                if (written.Moved)
                {
                    if (attempt >= 3) throw new InvalidOperationException($"{Path.GetFileName(bin)} was changed by something else again and again while the recolour was being written. Nothing was written; try again.");
                    continue;
                }
                EnsureMountsServe(hash, bytes);
                ReloadBinEditorsAfterWrite(entry, current);
                break;
            }

            recolourKeys.ExceptWith(renamed.Keys);   // a field left alone stays in the record as it was
            restoreKeys.ExceptWith(renamed.Keys);

            // 4. the recipe, bin by bin: a later bin that fails must not leave an earlier bin's recolour without an owner
            var settled = rewrite.Result.Edits.Where(e => e.Outcome is EffectColorOutcome.Written or EffectColorOutcome.Unchanged).Select(e => e.Key).ToList();
            var owned = recolourRefs.Where(r => settled.Contains(EffectKeyOf(r))).ToList();
            bool binGone = PersistChromaEffectRecord(skinBin, transform, bin, owned, recolourKeys, restoreKeys, placed, out string? placedFile);
            if (binGone && placedFile is not null) RemovePlacedSkinBin(entry, riot, placedFile);
            else if (binGone) DropRestoredOverride(entry, riot);

            edits.AddRange(rewrite.Result.Edits);
            edits.AddRange(renamed.Select(r => new EffectColorEdit(r.Key, EffectColorOutcome.Skipped, r.Value)));
            settledAll.AddRange(settled.Where(recolourKeys.Contains));
            kept += rewrite.Result.Edits.Count(e => e.Outcome == EffectColorOutcome.Kept);
        }
        SyncChromaEffectRecord(skinBin, transform, completed: true);   // every bin is through: the record's transform moves now, and no bin is left at another

        var restoreAll = restore.Select(EffectKeyOf).ToHashSet();
        var recolourAll = recolour.Select(EffectKeyOf).ToHashSet();
        int reverted = edits.Count(e => restoreAll.Contains(e.Key) && !recolourAll.Contains(e.Key) && e.Outcome is EffectColorOutcome.Written or EffectColorOutcome.Unchanged);
        var notes = edits.Where(e => e.Note.Length > 0).Select(e => $"{e.Key.Field}: {e.Note}").ToList();
        var result = new ChromaEffectSaveResult(
            edits.Count(e => e.Outcome == EffectColorOutcome.Written && recolourAll.Contains(e.Key)),
            edits.Count(e => e.Outcome == EffectColorOutcome.Unchanged && recolourAll.Contains(e.Key)),
            edits.Count(e => e.Outcome is EffectColorOutcome.Skipped or EffectColorOutcome.Missing),
            reverted, settledAll, notes);
        string line = $"Effect colours of {skinBin}: {result.Summary}" + (kept > 0 ? $", {kept} edited since and kept as they are" : "") + ".";
        if (result.Skipped > 0) _log.Warn("Chroma", line + (notes.Count > 0 ? " " + notes[0] : ""));
        else _log.Success("Chroma", line);
        return result;
    }

    /// <summary>The Materials tab of the Character window and the Particle Editor may hold a document of the bin about to be written. One with edits the project does not
    /// have yet is saved first, through its own save, so the recolour is written on top of those edits rather than beside them; one that cannot be saved stops the recolour
    /// (it must not overwrite them).</summary>
    private async Task SettleOtherBinEditorsAsync(WadAssetEntry entry)
    {
        ulong hash = entry.PathHash;
        var material = MeshPreview.MaterialEditor;
        if (material.BinEntry is { } open && open.PathHash == hash && material.IsDirty && HoldsUnsavedMaterialEdits(material, hash))
        {
            await SaveCharacterMaterialOverride();
            EnsureMountsServe(hash, material.Serialize());
            if (HoldsUnsavedMaterialEdits(material, hash))
                throw new InvalidOperationException("The Material tab holds unsaved edits to this bin that could not be saved, and a recolour must not overwrite them. Save or revert them there first.");
        }
        if (ParticleEditor.Entry is { } particle && particle.PathHash == hash && ParticleEditor.Document is { IsDirty: true } && HoldsUnsavedParticleEdits(hash))
        {
            await SaveParticleOverride();
            if (ParticleEditor.Document is not null && HoldsUnsavedParticleEdits(hash))
                throw new InvalidOperationException("The Particle Editor holds unsaved edits to this bin that could not be saved, and a recolour must not overwrite them. Save or revert them there first.");
        }
    }

    /// <summary>The Particle Editor's document of this bin differs, as data, from the bin the project serves: edits it has not saved. A document that cannot be serialised
    /// counts as holding some (the recolour then refuses rather than risk overwriting them).</summary>
    private bool HoldsUnsavedParticleEdits(ulong hash)
    {
        try { return ParticleEditor.Document is { } doc && !ServesBinData(hash, doc.Serialize()); }
        catch { return true; }
    }

    /// <summary>After the bin was written: the Particle Editor's document of it is read again from the bin as served (the selected system kept), so what it shows is the
    /// recolour and its next save starts from it; the Character window's Materials tab likewise (<see cref="ReloadCharacterMaterialEditor"/>). A document that was edited while the
    /// write was awaited keeps its edits.</summary>
    private void ReloadBinEditorsAfterWrite(WadAssetEntry entry, byte[] before)
    {
        if (MeshPreview.MaterialEditor.BinEntry is { } open && open.PathHash == entry.PathHash) ReloadCharacterMaterialEditor(entry, before);
        if (ParticleEditor.Entry is not { } particle || particle.PathHash != entry.PathHash || ParticleEditor.Document is not { } doc) return;
        try
        {
            if (doc.IsDirty && !SameBinData(doc.Serialize(), before))
            {
                _log.Info("Chroma", "The Particle Editor changed while the recolour was saved, so it keeps its document; reopen the bin there to see the recoloured values.");
                return;
            }
            uint selected = ParticleEditor.SelectedSystem?.Entry.PathHash ?? 0;
            bool editable = ParticleEditor.IsEditable;
            ParticleEditor.Load(particle, ReadAsset(entry.PathHash), editable);
            if (selected != 0)
                ParticleEditor.SelectedSystem = ParticleEditor.Systems.FirstOrDefault(s => s.Entry.PathHash == selected) ?? ParticleEditor.SelectedSystem;
        }
        catch (Exception ex) { _log.Warn("Chroma", $"The Particle Editor could not be refreshed with the recoloured bin ({ex.Message}); reopen it there to see the new values."); }
    }

    /// <summary>The skin bin is changed by two parts of one recolour - its colour parameters (M825) and the effect systems that live in it - and whichever wrote first copied it into the
    /// project. The other part adopts that copy, so whichever gives its last colour back finds the bin Riot's again and takes the copy out (never one that differs from Riot's). Null when the
    /// parameter recipe placed no copy of this bin.</summary>
    private string? PlacedByTheParameterRecolour(string skinBin, string bin)
    {
        string? file = ChromaParameterRecordOf(skinBin)?.PlacedFile;
        return file is not null && file.Replace('\\', '/').EndsWith("/" + bin.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) ? file : null;
    }

    /// <summary>A bin whose name no file can have is kept as a hash-named project override; once its last colour is given back it is Riot's data again, and the override is taken out
    /// (only while it still is Riot's data: an override somebody edited since stays).</summary>
    private void DropRestoredOverride(WadAssetEntry entry, byte[] riot)
    {
        try
        {
            if (!_overrides.TryGet(entry.PathHash, out var ov) || !File.Exists(ov.OverrideFile)) return;
            if (!SameBinData(File.ReadAllBytes(ov.OverrideFile), riot)) return;
            File.Delete(ov.OverrideFile);
            _overrides.Remove(entry.PathHash);
            _overrides.SaveTo(Project);
            Project.IsDirty = true;
            BuildMounts();
            BuildProjectTree();
            _log.Info("Chroma", $"Removed the project override of {entry.DisplayName}: with the effect colours given back it is Riot's bin again.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.Warn("Chroma", $"Could not remove the project override of {entry.DisplayName}: {ex.Message}"); }
    }

    // ---- the recipe -----------------------------------------------------------------------------------------

    /// <summary>
    /// The recipe in project.json for ONE bin of the skin's effects: the fields of that bin the record kept (the ones this save neither recoloured nor gave back) plus the ones
    /// just recoloured, and the file the recolour placed, once. Returns whether the bin has no owned field left, and the file it had placed. The record itself is
    /// completed by <see cref="SyncChromaEffectRecord"/>.
    /// </summary>
    private bool PersistChromaEffectRecord(string skinBin, ColorTransform transform, string bin, IReadOnlyList<ChromaEffectColorRef> owned,
        HashSet<EffectColorKey> recolourKeys, HashSet<EffectColorKey> restoreKeys, string? placedNow, out string? placedFile)
    {
        var existing = ChromaEffectRecordOf(skinBin);
        placedFile = placedNow ?? existing?.PlacedBins?.FirstOrDefault(p => string.Equals(p.Bin, bin, StringComparison.OrdinalIgnoreCase))?.File;
        var others = existing?.Colors.Where(c => !string.Equals(c.Bin, bin, StringComparison.OrdinalIgnoreCase)).ToList() ?? new List<ChromaEffectColorRef>();
        var kept = existing?.Colors.Where(c => string.Equals(c.Bin, bin, StringComparison.OrdinalIgnoreCase)
                                               && !recolourKeys.Contains(EffectKeyOf(c)) && !restoreKeys.Contains(EffectKeyOf(c))).ToList() ?? new List<ChromaEffectColorRef>();
        var mine = kept.Concat(owned).ToList();
        if (existing is null && mine.Count == 0 && placedNow is null) return false;

        var list = Project.ChromaEffectRecolors ??= new List<ChromaEffectRecord>();
        if (existing is not null) list.Remove(existing);
        var placedBins = (existing?.PlacedBins ?? new List<ChromaPlacedBin>()).Where(p => !string.Equals(p.Bin, bin, StringComparison.OrdinalIgnoreCase)).ToList();
        bool gone = mine.Count == 0;
        if (!gone && placedFile is not null) placedBins.Add(new ChromaPlacedBin { Bin = bin, File = placedFile });

        // The record has ONE transform, and it moves only when the whole save is through (SyncChromaEffectRecord). Until then a bin this save recoloured at another transform is
        // remembered on its own, so a save that stops after this bin leaves the record telling the truth about what each bin holds.
        var binTransforms = existing?.BinTransforms is { } had ? new Dictionary<string, ColorTransform>(had, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, ColorTransform>(StringComparer.OrdinalIgnoreCase);
        var recordTransform = existing?.Transform ?? transform;
        if (gone) binTransforms.Remove(bin);
        else if (owned.Count > 0 && !transform.Equals(recordTransform)) binTransforms[bin] = transform;
        else if (owned.Count > 0) binTransforms.Remove(bin);
        list.Add(new ChromaEffectRecord
        {
            ChromaSkin = skinBin,
            Transform = recordTransform,
            Colors = others.Concat(mine).ToList(),
            PlacedBins = placedBins.Count > 0 ? placedBins : null,
            BinTransforms = binTransforms.Count > 0 ? binTransforms : null,
        });
        Project.IsDirty = true;
        _overrides.SaveTo(Project);
        if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);   // per bin: a later bin that fails must not leave this one unowned
        return gone;
    }

    /// <summary>Settle the skin's effect record after a part of the recolour was saved: removed when it owns no colour and no texture, given the saved transform otherwise, and
    /// the list back to null with the last record (so a project that never recoloured an effect has no new key). Then the project file.</summary>
    private void SyncChromaEffectRecord(string skinBin, ColorTransform? transform, bool completed = false)
    {
        var record = ChromaEffectRecordOf(skinBin);
        if (completed && record is not null) record.BinTransforms = null;   // every bin of the save is through: all of them are at the transform below
        bool textures = Project.TextureRecolors.Any(r => r.Transform is not null && r.ChromaPart == TextureRecolorRecord.EffectsPart
                                                         && string.Equals(r.ChromaSkin, skinBin, StringComparison.OrdinalIgnoreCase));
        if (record is not null)
        {
            if (record.Colors.Count == 0 && !textures) Project.ChromaEffectRecolors!.Remove(record);
            else if (transform is not null && !transform.IsIdentity) record.Transform = transform;
        }
        else if (textures && transform is not null && !transform.IsIdentity)
        {
            (Project.ChromaEffectRecolors ??= new List<ChromaEffectRecord>()).Add(new ChromaEffectRecord { ChromaSkin = skinBin, Transform = transform });
        }
        if (Project.ChromaEffectRecolors is { Count: 0 }) Project.ChromaEffectRecolors = null;
        Project.IsDirty = true;
        _overrides.SaveTo(Project);
        if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
        UpdateTitle();
    }

    // ---- the effect textures --------------------------------------------------------------------------------

    /// <summary>The effect textures through the body recolour's texture pipeline (TEX BC1/BC3 re-derived from Riot's pristine bytes, the WAD folder rules, the records), told apart
    /// by the record's part. <paramref name="transform"/> is the sliders' transform: a texture takes its hue-only form (<see cref="SkinEffectColors.TextureTransform"/>).</summary>
    private async Task<ChromaSaveResult> SaveChromaEffectTexturesAsync(string skinBin, ColorTransform transform,
        IReadOnlyList<ChromaTarget> targets, IReadOnlyList<ChromaTarget> stale)
    {
        var result = await SaveChromaTexturesCoreAsync(skinBin, SkinEffectColors.TextureTransform(transform), targets, stale, TextureRecolorRecord.EffectsPart);
        SyncChromaEffectRecord(skinBin, targets.Count > 0 ? transform : null);
        return result;
    }

    private int RevertChromaEffectTextures(string skinBin, IReadOnlyList<ChromaTarget> targets)
    {
        int n = RevertChromaPartCore(skinBin, targets, TextureRecolorRecord.EffectsPart);
        FinishChromaProject();
        SyncChromaEffectRecord(skinBin, null);
        _log.Info("Chroma", $"Effect textures of {skinBin}: {n} put back to the original.");
        return n;
    }
}
