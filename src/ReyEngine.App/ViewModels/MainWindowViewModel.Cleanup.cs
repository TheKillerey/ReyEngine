using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Build;
using ReyEngine.Core.Cleanup;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M302: Tools ▸ Mod Health ▸ Cleanup Project — the host side. Owns the file operations and the wiring
/// into the project's existing systems (mounts, hash resolver, bin parser, Content Browser); the window
/// owns only the review step.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private string? _lastCleanupRunId;

    [RelayCommand]
    private void CleanupProject()
    {
        if (!ContentLoaded || Project.RootPath is null || Project.ProjectFolders.Count == 0)
        { _log.Warn("Cleanup", "Open a folder project first."); return; }

        var vm = new CleanupWindowViewModel
        {
            ProjectPath = Project.RootPath,
            ScanAsync = (unused, riot, empties, progress) =>
                Task.Run(() => ScanForCleanup(unused, riot, empties, progress)),
            CleanAsync = RunCleanupAsync,
            RestoreAsync = RestoreCleanupAsync,
        };
        // Tell the window up front whether Riot comparison is even possible, so the option is disabled
        // before the first scan rather than silently doing nothing.
        vm.RiotAvailable = RiotReferenceReader() is not null;
        vm.RiotStatus = vm.RiotAvailable
            ? "Riot reference mounted — identical-file detection available."
            : "No Riot reference available (set the game folder via Project ▸ Set Game Folder). "
            + "Unused-file scanning still works.";
        if (!vm.RiotAvailable) vm.RemoveRiotIdentical = false;
        vm.CanRestore = CleanupExecutor.ListRuns(Project.RootPath).Any(r => !r.Restored);

        var win = new Views.CleanupWindow { DataContext = vm };
        if (PromptOwner is not null) win.Show(PromptOwner); else win.Show();
    }

    /// <summary>A reader for the ORIGINAL Riot bytes behind a hash, or null when no Riot source is
    /// mounted. Deliberately bypasses the project mounts: the question is what the game would fall back
    /// to, not what the project currently resolves.</summary>
    private Func<ulong, byte[]?>? RiotReferenceReader()
    {
        if (_mounts is null) return null;
        var riot = _mounts.Mounts.Where(m => m.Kind == AssetSourceKind.RiotReference)
                                 .Concat(_mounts.Fallback)
                                 .ToList();
        if (riot.Count == 0) return null;
        return hash =>
        {
            foreach (var m in riot)
                if (m.Contains(hash))
                { try { return m.Read(hash); } catch { return null; } }
            return null;
        };
    }

    private CleanupReport ScanForCleanup(bool unused, bool riotIdentical, bool empties,
        IProgress<(double, string)> progress)
    {
        string root = Project.RootPath!;
        var folders = Project.ProjectFolders
            .Select(f => (Name: f == "." ? Project.Name : f, Root: Project.ResolveProjectPath(f)))
            .Where(t => Directory.Exists(t.Root))
            .ToList();

        // ---- one reference index, built from the project's own bins ----
        progress.Report((0.02, "Indexing project references…"));
        var index = new ProjectReferenceIndex();
        foreach (var (_, folderRoot) in folders)
            foreach (var (_, path) in WadPackService.EnumerateChunkFiles(folderRoot))
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                try
                {
                    if (ext == ".bin") index.AddBin(File.ReadAllBytes(path));
                    // Meshes and map geometry carry material/texture names inline - the indirect channel
                    // a bin-only scan is blind to.
                    else if (ext is ".mapgeo" or ".skn" or ".scb" or ".sco")
                        index.AddAssetNames(File.ReadAllBytes(path));
                }
                catch { }
            }

        // Project metadata points at assets by hash and by path; both count as references, and the files
        // it names outright (thumbnail, recolour snapshots) are protected rather than merely referenced.
        var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var protectedHashes = new HashSet<ulong>();
        foreach (var o in Project.Overrides)
        {
            protectedHashes.Add(o.PathHash);
            if (o.ResolvedPath is { Length: > 0 } rp) index.AddReference(rp);
            if (o.OverrideFile.Length > 0) protectedPaths.Add(o.OverrideFile.Replace('\\', '/'));
        }
        foreach (var r in Project.TextureRecolors)
        {
            protectedHashes.Add(r.PathHash);
            index.AddReference(r.AssetPath);
            if (!string.IsNullOrWhiteSpace(r.BaseSnapshot)) protectedPaths.Add(r.BaseSnapshot!.Replace('\\', '/'));
        }
        foreach (var l in Project.MapLighting) { protectedHashes.Add(l.PathHash); index.AddReference(l.MapgeoPath); }
        if (Project.ThumbnailPath is { Length: > 0 } thumb) protectedPaths.Add(thumb.Replace('\\', '/'));

        _log.Info("Cleanup", $"Reference index: {index.BinsRead:n0} bin(s) read, {index.BinsFailed} unreadable, "
                           + $"{index.NotBins} not bins; {index.PathCount:n0} path(s), {index.HashCount:n0} hash(es).");
        if (!index.IsComplete)
            _log.Warn("Cleanup", $"{index.BinsFailed:n0} bin(s) could not be parsed — references they hold are "
                               + "invisible, so unused results are reported as uncertain.");

        // ---- what the installed game ships (a path the game knows is never unused) ----
        progress.Report((0.06, "Indexing game WADs…"));
        var gameHashes = new HashSet<ulong>();
        // Resolve the install the same way the mount system does. Rebuilding "DATA/FINAL" by hand finds
        // nothing on two of the three supported layouts, and an empty game index is exactly what turns
        // real Riot content into a false "unused" verdict.
        string? gameFinal = GameReferenceLibrary.FindFinalDirectory(Project.GameDirectory);
        if (gameFinal is not null)
            foreach (var wad in Directory.EnumerateFiles(gameFinal, "*.wad.client", SearchOption.AllDirectories))
                foreach (var h in ReadWadTocHashes(wad)) gameHashes.Add(h);
        _log.Info("Cleanup", gameFinal is null
            ? "No game install resolved — unused results will be reported as uncertain."
            : $"Game index: {gameHashes.Count:n0} path hash(es) from {gameFinal}.");

        var options = new CleanupScanOptions
        {
            ProjectRoot = root,
            Folders = folders,
            References = index,
            ScanUnused = unused,
            ScanRiotIdentical = riotIdentical,
            IncludeEmptyFolders = empties,
            GameWadHashes = gameHashes,
            ReadRiot = RiotReferenceReader(),
            ProjectWadCopies = ProjectWadCopiesOf,
            ContentEquivalent = ContentEquivalent,
            ProtectedRelPaths = protectedPaths,
            ProtectedHashes = protectedHashes,
            ReferencesComplete = index.IsComplete,
            ReferenceGapReason = index.IsComplete ? "" : $"{index.BinsFailed:n0} unparseable bin(s)",
        };
        return CleanupScanner.Scan(options, progress);
    }

    private int ProjectWadCopiesOf(ulong hash) =>
        _mounts?.Mounts.Count(m => m.Kind == AssetSourceKind.ProjectWad && m.Contains(hash)) ?? 0;

    /// <summary>Format-aware equality where one exists. Only .bin qualifies today: two bins can differ
    /// byte for byte (property order, string-table layout) and still describe exactly the same objects,
    /// and M97's structural comparer already knows how to tell. Everything else returns null so the
    /// scanner falls back to exact bytes.</summary>
    private static bool? ContentEquivalent(string ext, byte[] mine, byte[] riot)
    {
        if (!ext.Equals("bin", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var a = SafeBinTree.Parse(mine);
            var b = SafeBinTree.Parse(riot);
            if (a.Objects.Count != b.Objects.Count) return false;
            foreach (var (key, obj) in a.Objects)
                if (!b.Objects.TryGetValue(key, out var other) || !BinPropEquality.ObjectsEqual(obj, other))
                    return false;
            return true;
        }
        catch { return null; }   // unparseable -> no verdict, let raw bytes decide
    }

    private async Task<string?> RunCleanupAsync(IReadOnlyList<CleanupCandidate> selected)
    {
        if (Project.RootPath is null || PromptOwner is null) return null;

        long bytes = selected.Sum(c => c.Bytes);
        int uncertain = selected.Count(c => c.Group == CleanupGroup.Protected);
        string warn = uncertain > 0
            ? $"\n\nWARNING: {uncertain:n0} of these are protected or uncertain — the scan could not prove they are safe."
            : "";
        if (!await Views.PromptWindow.ConfirmAsync(PromptOwner, "Clean Selected Files",
                $"Move {selected.Count:n0} item(s) ({CleanupWindowViewModel.FormatBytes(bytes)}) out of the project?\n\n"
                + "They are moved into .reyengine/cleanup/ inside the project, not deleted — "
                + "Undo Last Cleanup puts them back." + warn,
                "Clean"))
            return null;

        string runId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var result = await Task.Run(() =>
            CleanupExecutor.Run(Project.RootPath!, runId, selected, BuildProgressSink2()));
        _lastCleanupRunId = runId;

        foreach (var line in result.Log.Take(20)) _log.Warn("Cleanup", line);
        _log.Success("Cleanup", $"Moved {result.Moved:n0} file(s) ({CleanupWindowViewModel.FormatBytes(result.BytesFreed)}) "
                              + $"to .reyengine/cleanup/{runId}. {result.Failed:n0} failed, {result.FoldersRemoved:n0} folder(s) removed.");

        string validation = await Task.Run(() => ValidateAfterCleanup());
        RefreshAfterCleanup();

        return $"Moved {result.Moved:n0} item(s), {CleanupWindowViewModel.FormatBytes(result.BytesFreed)} recovered"
             + (result.Failed > 0 ? $" ({result.Failed:n0} failed — see the log)" : "")
             + $". Backup: .reyengine/cleanup/{runId}. {validation}";
    }

    private async Task<string?> RestoreCleanupAsync()
    {
        if (Project.RootPath is null) return null;
        string? runId = _lastCleanupRunId
            ?? CleanupExecutor.ListRuns(Project.RootPath).FirstOrDefault(r => !r.Restored)?.Id;
        if (runId is null) { _log.Warn("Cleanup", "No cleanup run to restore."); return null; }

        var (ok, bad, log) = await Task.Run(() => CleanupExecutor.Restore(Project.RootPath!, runId));
        foreach (var line in log.Take(20)) _log.Warn("Cleanup", line);
        _log.Success("Cleanup", $"Restored {ok:n0} file(s) from {runId}" + (bad > 0 ? $", {bad:n0} could not be put back." : "."));
        RefreshAfterCleanup();
        return $"Restored {ok:n0} file(s) from {runId}" + (bad > 0 ? $" — {bad:n0} could not be put back (see the log)." : ".");
    }

    /// <summary>Rebuild the mounts and the Content Browser so the project view matches the disk again.</summary>
    private void RefreshAfterCleanup()
    {
        try
        {
            BuildMounts();
            BuildProjectTree();
            RefreshBrowser();
        }
        catch (Exception ex) { _log.Warn("Cleanup", "Refresh after cleanup failed: " + ex.Message); }
    }

    /// <summary>Post-cleanup validation: can everything the project's bins point at still be resolved,
    /// and would a package build still find its inputs? Reports rather than throws — a cleanup that has
    /// already happened must not be followed by a crash.</summary>
    private string ValidateAfterCleanup()
    {
        try
        {
            if (Project.RootPath is null) return "";
            int missing = 0, checkedRefs = 0;
            var seen = new HashSet<ulong>();
            foreach (var f in Project.ProjectFolders)
            {
                var root = Project.ResolveProjectPath(f);
                if (!Directory.Exists(root)) continue;
                foreach (var (_, path) in WadPackService.EnumerateChunkFiles(root))
                {
                    if (!path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) continue;
                    List<string> strings = new();
                    try { BinStringHarvester.Collect(SafeBinTree.Parse(File.ReadAllBytes(path)), strings); }
                    catch { continue; }
                    foreach (var s in strings)
                    {
                        if (s.Length < 5 || !s.Contains('/') || !s.Contains('.')) continue;
                        ulong h = HashAlgorithms.WadPath(s.Replace('\\', '/'));
                        if (!seen.Add(h)) continue;
                        checkedRefs++;
                        if (_mounts?.Has(h) != true) missing++;
                    }
                }
            }
            // A package build needs at least one input folder that still has files in it.
            bool buildable = Project.ProjectFolders
                .Select(Project.ResolveProjectPath)
                .Any(r => Directory.Exists(r) && Directory.EnumerateFiles(r, "*", SearchOption.AllDirectories).Any());

            string verdict = missing == 0
                ? $"Validation: all {checkedRefs:n0} bin reference(s) still resolve."
                : $"Validation: {missing:n0} of {checkedRefs:n0} bin reference(s) no longer resolve — check the log.";
            if (!buildable) verdict += " WARNING: no project folder has any files left, so a Fantome export would be empty.";
            if (missing > 0)
                _log.Warn("Cleanup", $"{missing:n0} bin reference(s) do not resolve after cleanup. "
                                   + "If this is unexpected, use Undo Last Cleanup.");
            return verdict;
        }
        catch (Exception ex) { return "Validation could not run: " + ex.Message; }
    }

    private IProgress<(double, string)> BuildProgressSink2()
    {
        var inner = BuildProgressSink();
        return new Progress<(double F, string S)>(t => inner.Report((t.F, t.S)));
    }

    private static IEnumerable<ulong> ReadWadTocHashes(string wadPath)
    {
        FileStream fs;
        try { fs = File.OpenRead(wadPath); } catch { yield break; }
        using (fs)
        {
            var header = new byte[4 + 256 + 8 + 4];
            if (fs.Read(header, 0, header.Length) != header.Length) yield break;
            if (header[0] != 'R' || header[1] != 'W' || header[2] != 3) yield break;
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(268));
            if (count == 0 || count > 4_000_000) yield break;
            var table = new byte[count * 32L];
            if (fs.Read(table, 0, table.Length) != table.Length) yield break;
            for (int i = 0; i < count; i++)
                yield return BinaryPrimitives.ReadUInt64LittleEndian(table.AsSpan(i * 32));
        }
    }
}
