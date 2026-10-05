using ReyEngine.App.Services;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Meta;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M824: Chroma Studio C3 - the host half of the Character window's BODY RECOLOUR.
///
/// <para>The card holds the sliders, the list and the live preview (<see cref="MeshPreviewViewModel"/>); this holds the project.
/// Nothing here is a second pipeline: a save is <see cref="TextureRecolorService"/> over the Recolor Textures tool's own reader
/// (<see cref="CheckOutRecolorBase"/> - Riot's PRISTINE bytes, so the file is only ever one generation of BC loss from the
/// original, however often the sliders are moved) and writer (<see cref="WriteRecoloredAssetByHash"/>
/// - the champion's own WAD folder in a folder project, the override store otherwise), and the transform is recorded in the same
/// <see cref="TextureRecolorRecord"/> list, so a reload restores the sliders and Build Package / .fantome ship the files.</para>
///
/// <para>What differs from the map tool: a record belongs to a SKIN (<see cref="TextureRecolorRecord.ChromaSkin"/>), a chunk is
/// addressed by its hash (an unnamed chunk link has no path), and finishing a save does not reload a map.</para>
/// </summary>
public sealed partial class MainWindowViewModel
{
    private void WireChromaRecolour()
    {
        MeshPreview.ReadChromaOriginal = ReadChromaOriginalBytes;
        MeshPreview.SaveChromaRecolour = SaveChromaRecolourAsync;
        MeshPreview.RevertChromaRecolour = RevertChromaRecolour;
        MeshPreview.ReadChromaSaved = ReadChromaSavedRecipe;
        MeshPreview.ChromaSaveBlocker = ChromaSaveBlocker;
        MeshPreview.PushChromaGl = PushChromaToGl;
        MeshPreview.ChromaEdited = ScheduleAutoSave;   // arm the auto-save on the EDIT, as the material editor does
        MeshPreview.AcquireChromaReaders = () => AcquireReaderLease(_mounts, _archive);
        MeshPreview.IsChromaProjectEdited = IsChromaProjectEdited;
        WireChromaParameters();   // M825: the skin bin's colour parameters, saved through the bin save path
    }

    /// <summary>The pristine original of a body texture, read on a worker. The caller holds the readers' lease
    /// (<see cref="MeshPreviewViewModel.AcquireChromaReaders"/>), taken on the UI thread BEFORE the worker started - as the colour scan
    /// takes its own - so a rebuild of the mounts waits for the read instead of disposing what it is reading.</summary>
    private byte[]? ReadChromaOriginalBytes(ChromaTarget target) =>
        ReadRecolorBase(new RecolorTarget(target.Hash, target.Path));

    /// <summary>
    /// Is the project's copy of this texture something somebody else made: it serves a file of its own that differs from Riot's, and no
    /// recolour record accounts for it (a hand-edited or replaced texture)? Recolouring it would overwrite that edit, and drawing Riot's
    /// original over it in the preview would show a picture the project does not have - so the card leaves such a row off and says why.
    /// A copy identical to Riot's (Copy To Project) is not an edit. On a worker, under the caller's lease.
    /// </summary>
    private bool IsChromaProjectEdited(ChromaTarget target)
    {
        if (Project.TextureRecolors.Any(r => r.PathHash == target.Hash)) return false;   // a record accounts for it: ours, or the Recolor Textures tool's
        var mounts = _mounts;
        if (mounts is null || !mounts.TryGet(target.Hash, out var served) || served.Source.Kind == AssetSourceKind.RiotReference) return false;
        try
        {
            var mine = mounts.Read(target.Hash);
            var riot = mounts.ReadFallback(target.Hash);
            return mine is not null && (riot is null || !mine.AsSpan().SequenceEqual(riot));
        }
        catch { return false; }
    }

    /// <summary>Why nothing can be written, or null. A champion opened with no project at all is inspected from its WAD; a save then
    /// creates the quick project the other editors create (<see cref="EnsureProjectSavedAsync"/>).</summary>
    private string? ChromaSaveBlocker() =>
        _mounts is null && _archive is null ? "Open or create a project first: a recolour is saved into the project's files." : null;

    /// <summary>The recipe project.json holds for this skin bin: the transform and the textures it was applied to.</summary>
    private ChromaSavedRecipe? ReadChromaSavedRecipe(string skinBin)
    {
        var mine = Project.TextureRecolors
            .Where(r => r.Transform is not null && string.Equals(r.ChromaSkin, skinBin, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var parameters = ChromaParameterRecordOf(skinBin);   // M825
        if (mine.Count == 0 && parameters is null) return null;
        return new ChromaSavedRecipe(mine.Count > 0 ? mine[0].Transform! : parameters!.Transform,
            mine.Select(r => new ChromaTarget(r.PathHash, r.AssetPath)).ToList(),
            parameters?.Parameters.ToList(), parameters?.Transform);
    }

    /// <summary>
    /// Write the recolour: each target re-derived from its pristine original with <paramref name="transform"/>, TEX BC1/BC3 into the
    /// project; the textures of this skin's earlier recipe that are not in it any more put back to Riot's; the records; then the
    /// project file and the mounts, so the next read - the preview's scene, an export - sees the files.
    /// </summary>
    private async Task<ChromaSaveResult> SaveChromaRecolourAsync(string skinBin, ColorTransform transform,
        IReadOnlyList<ChromaTarget> targets, IReadOnlyList<ChromaTarget> stale)
    {
        if (!await EnsureProjectSavedAsync())
            throw new InvalidOperationException("The project has to be saved before a recolour can be written into it.");
        var service = MakeChromaRecolorService()
            ?? throw new InvalidOperationException("This project has nowhere to write a recoloured texture (no project folder and no overrides folder).");

        int reverted = stale.Count > 0 ? RevertChromaCore(skinBin, stale) : 0;

        RecolorRunResult run;
        if (targets.Count == 0 || transform.IsIdentity)
            run = new RecolorRunResult(0, 0, 0, 0, 0, Array.Empty<string>(), Array.Empty<RecolorTarget>(), Array.Empty<RecolorTarget>());
        else
        {
            var recolorTargets = targets.Select(t => new RecolorTarget(t.Hash, t.Path)).ToList();
            using var lease = AcquireReaderLease(_mounts, _archive);
            run = await service.RunAsync(recolorTargets, transform);
        }

        // back on the UI thread: the records, then the project
        var written = run.WrittenTargets;
        PersistChromaRecords(skinBin, transform, written);

        // a texture this transform leaves exactly as it was needs no file: one an earlier recipe wrote goes back to Riot's
        var skipped = run.SkippedTargets ?? Array.Empty<RecolorTarget>();
        reverted += RevertChromaCore(skinBin, skipped.Select(t => new ChromaTarget(t.PathHash, t.AssetPath)).ToList());

        if (written.Count > 0 || reverted > 0) FinishChromaProject();   // nothing written and nothing put back: nothing for the project file or the mounts to learn

        var result = new ChromaSaveResult(run.Written, run.Skipped, run.Failed, reverted,
            written.Select(t => t.PathHash).ToList(), written.Concat(skipped).Select(t => t.PathHash).Distinct().ToList(), run.Notes);
        var line = $"Body recolour of {skinBin}: {result.Summary} ({run.BytesWritten / 1048576.0:F1} MB).";
        if (run.Failed > 0 || run.MissingSources > 0) _log.Error("Chroma", line + (run.Notes.Count > 0 ? " " + run.Notes[0] : ""));
        else _log.Success("Chroma", line);
        return result;
    }

    /// <summary>The service the Chroma Studio saves with, or null when the project has nowhere to write (the same test the Recolor
    /// Textures tool makes). Unlike that tool's, its writer is told the chunk hash.</summary>
    private TextureRecolorService? MakeChromaRecolorService()
    {
        bool canWrite = (Project.IsFolderProject && Project.RootPath is not null) || Project.OverridesDirectory is not null;
        return canWrite
            ? TextureRecolorService.ForTargets(CheckOutRecolorBase, WriteChromaTexture)
            : null;
    }

    /// <summary>The WAD folders a recoloured chunk is written to, filled by the writer (on the service's worker) and read by
    /// <see cref="PersistChromaRecords"/> once the run is over.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, List<string>> _pendingChromaFolders = new();

    /// <summary>
    /// Every Riot WAD that holds this chunk, by the name of the folder that stands for it in a project (<c>Lillia.wad.client</c> is
    /// <c>Lillia</c>), the WAD the Character window opened first. A texture is normally in one WAD - the champion's - but not always:
    /// Aatrox's base diffuse is also in <c>Shaders.wad.client</c>, identical to the byte. The game reads whichever copy it mounts first and
    /// nothing here says which, so a recolour written to one folder would be the right file in the wrong WAD for half the players. Empty
    /// when no Riot WAD is mounted that holds it.
    /// </summary>
    private List<string> RiotWadFoldersHolding(ulong hash)
    {
        var folders = new List<string>();
        void Add(IAssetMount mount)
        {
            if (mount.Kind != AssetSourceKind.RiotReference || !mount.Contains(hash)) return;
            var name = Path.GetFileName(mount.Location);
            if (name.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase)) name = name[..^".wad.client".Length];
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            if (name.Length > 0 && !folders.Contains(name, StringComparer.OrdinalIgnoreCase)) folders.Add(name);
        }
        if (_mounts is null) return folders;
        if (_openChampionWad is { } open)
            foreach (var mount in _mounts.Fallback.Concat(_mounts.Mounts))
                if (string.Equals(mount.Location, open.Wad, StringComparison.OrdinalIgnoreCase)) Add(mount);
        foreach (var mount in _mounts.Mounts) Add(mount);
        foreach (var mount in _mounts.Fallback) Add(mount);
        return folders;
    }

    /// <summary>
    /// The Chroma Studio's writer: <see cref="WriteRecoloredAssetByHash"/> for every WAD that holds the chunk (see
    /// <see cref="RiotWadFoldersHolding"/>) in a folder project, the single override file otherwise. A chunk no mounted Riot WAD holds is
    /// written where the Recolor Textures tool would write it.
    /// </summary>
    private string WriteChromaTexture(RecolorTarget target, byte[] bytes, string ext)
    {
        if (!(Project.IsFolderProject && Project.RootPath is { } root))
            return WriteRecoloredAssetByHash(target.PathHash, target.AssetPath, bytes, ext);

        var folders = RiotWadFoldersHolding(target.PathHash);
        if (folders.Count == 0)
        {
            // No Riot WAD holds it: only the project does. The hash-named override this would fall back to ranks BELOW the project file that
            // serves the chunk, so the save would say "written" and nothing on screen or in the package would change. Say so instead.
            if (_mounts is not null && _mounts.TryGet(target.PathHash, out var served) && served.Source.Kind != AssetSourceKind.RiotReference)
                throw new InvalidOperationException(
                    $"{Path.GetFileName(target.AssetPath)} exists only in the project ({served.Source.Name}), so a recolour written beside it would be shadowed by it. "
                    + "Recolour that file in place with the Recolor Textures tool, or copy Riot's original into the project first.");
            folders.Add(RiotWadFolderNameForHash(target.PathHash));
        }

        // every destination is proven before the first byte is written, and a write that fails partway puts back what it already wrote
        var plan = new List<(string Folder, string Dest)>();
        foreach (string folder in folders)
        {
            if (!TryRecolorFile(root, folder, target.PathHash, target.AssetPath, ext, out string dest))
                throw new InvalidDataException($"'{target.AssetPath}' is not a path a project file can have.");   // M819: below the project folder, proven
            plan.Add((folder, dest));
        }
        var undo = new List<(string Dest, byte[]? Before)>();
        try
        {
            foreach (var (_, dest) in plan)
            {
                byte[]? before = File.Exists(dest) ? File.ReadAllBytes(dest) : null;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.WriteAllBytes(dest, bytes);
                undo.Add((dest, before));
            }
        }
        catch
        {
            foreach (var (dest, before) in undo)
                try { if (before is null) File.Delete(dest); else File.WriteAllBytes(dest, before); } catch { /* best effort: the original failure is the one to report */ }
            throw;
        }
        foreach (var (folder, _) in plan)
            if (!Project.ProjectFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) Project.ProjectFolders.Add(folder);
        ClearShadowOverride(target.PathHash, ext);   // a stale hashed override would outrank the folder files
        _pendingChromaFolders[target.PathHash] = folders;
        return plan[0].Dest;
    }

    /// <summary>One record per recoloured chunk, carrying the transform and the skin. A record the Recolor Textures tool made for
    /// the same chunk is taken over (its M171 fields go back to neutral: they describe an edit that no longer stands).</summary>
    private void PersistChromaRecords(string skinBin, ColorTransform transform, IReadOnlyList<RecolorTarget> written)
    {
        foreach (var t in written)
        {
            var record = Project.TextureRecolors.FirstOrDefault(r => r.PathHash == t.PathHash);
            bool existed = record is not null;
            if (record is null)
            {
                record = new TextureRecolorRecord { PathHash = t.PathHash, AssetPath = t.AssetPath };
                // only when the run had to keep its own copy of the original (no Riot reference mounted)
                record.BaseSnapshot = _pendingSnapshots.GetValueOrDefault(t.PathHash);
                Project.TextureRecolors.Add(record);
            }
            // the record wrote this chunk under another spelling before (a name the dictionary has learned since, or lost): that file is
            // now a second copy of the chunk in the same WAD and the packer would ship both
            if (existed) RemoveStaleChromaForm(record, t, _pendingChromaFolders.GetValueOrDefault(t.PathHash));
            record.AssetPath = t.AssetPath;
            record.HueDegrees = 0f; record.Saturation = 1f; record.Brightness = 1f; record.Contrast = 1f;
            record.InputBlack = 0f; record.InputWhite = 1f; record.Gamma = 1f;
            record.TintR = 1f; record.TintG = 1f; record.TintB = 1f; record.Strength = 1f;
            record.Transform = transform;
            record.ChromaSkin = skinBin;
            record.WadFolders = _pendingChromaFolders.TryGetValue(t.PathHash, out var wrote) && wrote.Count > 1 ? wrote : null;
        }
        _pendingChromaFolders.Clear();
        _pendingSnapshots.Clear();
    }

    /// <summary>Delete the file an earlier write of this record made under a DIFFERENT spelling of the same chunk (the named path against the
    /// loose <c>&lt;hash&gt;.tex</c>), in the folders it was written to and the ones written now. The file just written is never touched.</summary>
    private void RemoveStaleChromaForm(TextureRecolorRecord record, RecolorTarget now, IReadOnlyList<string>? foldersNow)
    {
        if (!Project.IsFolderProject || Project.RootPath is not { } root) return;
        if (string.Equals(record.AssetPath, now.AssetPath, StringComparison.OrdinalIgnoreCase)) return;   // same spelling
        var folders = (record.WadFolders ?? new List<string>()).Concat(foldersNow ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (folders.Count == 0) folders = RiotWadFoldersHolding(now.PathHash);
        foreach (string folder in folders)
        {
            if (!TryRecolorFile(root, folder, now.PathHash, record.AssetPath, ".tex", out string old)) continue;
            if (TryRecolorFile(root, folder, now.PathHash, now.AssetPath, ".tex", out string current)
                && string.Equals(old, current, StringComparison.OrdinalIgnoreCase)) continue;
            try { if (File.Exists(old)) File.Delete(old); }
            catch (Exception ex) { _log.Warn("Chroma", $"Could not remove the earlier copy {old}: {ex.Message}"); }
        }
    }

    /// <summary>Put textures back to Riot's - the ones this skin's recipe wrote, and only those: a chunk another skin's recipe (or the
    /// Recolor Textures tool) has taken over since is not this skin's to undo. Returns how many records went.</summary>
    private int RevertChromaCore(string skinBin, IReadOnlyList<ChromaTarget> targets)
    {
        var mine = new List<RecolorTarget>();
        foreach (var t in targets)
        {
            var record = Project.TextureRecolors.FirstOrDefault(r => r.PathHash == t.Hash);
            if (record?.Transform is null || !string.Equals(record.ChromaSkin, skinBin, StringComparison.OrdinalIgnoreCase)) continue;
            string assetPath = record.AssetPath.Length > 0 ? record.AssetPath : t.Path;
            mine.Add(new RecolorTarget(t.Hash, assetPath));
            // the copies in the other WADs that hold the chunk (the core removes the one in the folder it works out from the mounts)
            if (record.WadFolders is { Count: > 0 } folders && Project.IsFolderProject && Project.RootPath is { } root)
                foreach (string folder in folders)
                    try
                    {
                        if (!TryRecolorFile(root, folder, t.Hash, assetPath, ".tex", out string file))
                            _log.Warn("Chroma", $"The record names a WAD folder that is not a plain folder name of this project ('{folder}'); it was left alone.");
                        else if (File.Exists(file)) File.Delete(file);
                    }
                    catch (Exception ex) { _log.Warn("Chroma", $"Could not restore {assetPath} in {folder}: {ex.Message}"); }
        }
        if (mine.Count == 0) return 0;
        RevertRecolorsCore(mine);
        return mine.Count;
    }

    /// <summary>
    /// Save a pending body recolour before the Character window moves on to another skin or to something that is not a champion: loading a
    /// model replaces the card, and a recolour that was only pending would go with it. True when the load may go on. With no project to
    /// write into the recolour was a preview only and is dropped (logged by the card); a save that FAILS stops the load, with the reason,
    /// because dropping it then would lose an edit the user believes is kept.
    /// </summary>
    private async Task<bool> FlushPendingChromaAsync()
    {
        if (!MeshPreview.HasPendingChromaRecolour) return true;
        if (ChromaSaveBlocker() is not null) return true;
        try { await MeshPreview.SaveChromaRecolourNowAsync(); return true; }
        catch (Exception ex)
        {
            _log.Error("Chroma", $"The pending body recolour could not be saved ({ex.Message}), so the window stays on this skin. Apply, Revert or Reset sliders first.");
            return false;
        }
    }

    /// <summary>The Revert button: this skin's saved recolour back to Riot's, the project saved, the mounts rebuilt.</summary>
    private int RevertChromaRecolour(string skinBin, IReadOnlyList<ChromaTarget> targets)
    {
        int n = RevertChromaCore(skinBin, targets);
        FinishChromaProject();
        _log.Info("Chroma", $"Body recolour of {skinBin}: {n} texture(s) put back to the original.");
        return n;
    }

    /// <summary>The project file and the mounts after a write: what <see cref="OnRecolorFinished"/> does, without reloading a map
    /// the Character window has nothing to do with.</summary>
    private void FinishChromaProject()
    {
        Project.IsDirty = true;
        _overrides.SaveTo(Project);
        if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
        if (Project.IsFolderProject) { BuildMounts(); BuildProjectTree(); }
        else RefreshOverrideMount();
        UpdateTitle();
    }

    // ---- the GL viewport ----------------------------------------------------------------------------------

    private sealed record ChromaGlMap(IReadOnlyList<TextureImage?> Images, Dictionary<ulong, List<TextureImage>> ByHash);

    /// <summary>Copy recoloured texels into the GL images that draw those chunks and queue the upload. The images are the GL
    /// viewport's own decode (never shared with the D3D11 scene or the main viewport), changed in place the way the paint tool
    /// changes the map's.</summary>
    private void PushChromaToGl(IReadOnlyList<ChromaPushItem> items)
    {
        if (MeshPreview.Textures is not { Count: > 0 } images || MeshPreview.Mesh is not { } mesh) return;
        if (MeshPreview.ChromaGlImageMap is not ChromaGlMap map || !ReferenceEquals(map.Images, images))
            MeshPreview.ChromaGlImageMap = map = new ChromaGlMap(images, ChromaGlImagesByHash(images, mesh));

        bool any = false;
        foreach (var item in items)
        {
            if (!map.ByHash.TryGetValue(item.Hash, out var drawn)) continue;
            foreach (var image in drawn)
            {
                if (image.Width != item.Width || image.Height != item.Height || image.Rgba.Length != item.Rgba.Length) continue;
                Buffer.BlockCopy(item.Rgba, 0, image.Rgba, 0, item.Rgba.Length);
                MeshPreview.QueueGlTextureUpdate?.Invoke(image, new Avalonia.PixelRect(0, 0, image.Width, image.Height));
                any = true;
            }
        }
        if (any) MeshPreview.RebuildGlTextureMips?.Invoke();
    }

    /// <summary>Which GL image draws which chunk: the diffuse of each submesh, resolved from the skin bin the way the GL preview
    /// resolved it (<see cref="ResolveSubmeshDiffuse"/>), so the two cannot disagree about a submesh's texture. The GL preview draws
    /// the diffuse only; a chunk that is not one submesh's diffuse (an emissive, a gradient) has nothing to update there.</summary>
    private Dictionary<ulong, List<TextureImage>> ChromaGlImagesByHash(IReadOnlyList<TextureImage?> images, MeshAsset mesh)
    {
        var map = new Dictionary<ulong, List<TextureImage>>();
        try
        {
            if (_previewSkn is not { } skn) return map;
            string? binPath = Formats.Meta.SkinPaths.PreviewBinPath(_previewSkinBin, skn.IsResolved ? skn.Path : null);
            if (binPath is null || !TryResolveEntry(HashAlgorithms.WadPath(binPath), out var binEntry)) return map;
            var editor = MeshPreview.MaterialEditor;
            byte[]? bytes = editor.BinEntry is { } open && open.PathHash == binEntry.PathHash ? editor.Serialize() : null;
            var resolved = ChampionMaterialResolver.Resolve(bytes ?? GetAssetBytes(binEntry), ResolveBinName, ResolveWadPath, ReadAssetByPath);
            for (int i = 0; i < mesh.SubMeshes.Count && i < images.Count; i++)
            {
                if (images[i] is not { } image) continue;
                string? path = resolved.For(mesh.SubMeshes[i].Material);
                if (string.IsNullOrEmpty(path)) continue;
                ulong hash = BinTexturePath.HashOfReference(path);
                if (!map.TryGetValue(hash, out var list)) map[hash] = list = new List<TextureImage>();
                if (!list.Any(x => ReferenceEquals(x, image))) list.Add(image);
            }
        }
        catch (Exception ex) { _log.Warn("Chroma", "GL preview: " + ex.Message); }
        return map;
    }
}
