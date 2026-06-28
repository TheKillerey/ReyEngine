using System.Diagnostics;
using LeagueToolkit.Core.Wad;
using ReyEngine.Core.Projects;

namespace ReyEngine.Core.Build;

public static class BuildPackageService
{
    public static BuildReport Build(ReyProject project, string outputPath,
        IProgress<float>? progress = null, CancellationToken ct = default)
    {
        var report = new BuildReport { OutputPath = outputPath };
        var sw = Stopwatch.StartNew();

        if (string.IsNullOrEmpty(project.SourceWadPath) || !File.Exists(project.SourceWadPath))
        {
            report.Add(BuildSeverity.Error, "Source WAD not found.");
            return report;
        }

        // Gather override bytes.
        var overrideBytes = new Dictionary<ulong, byte[]>();
        foreach (var ov in project.Overrides)
        {
            if (!File.Exists(ov.OverrideFile))
            {
                report.Add(BuildSeverity.Error, $"Override file missing: {ov.OverrideFile}");
                continue;
            }
            try { overrideBytes[ov.PathHash] = File.ReadAllBytes(ov.OverrideFile); }
            catch (Exception ex) { report.Add(BuildSeverity.Error, $"Could not read override {Path.GetFileName(ov.OverrideFile)}: {ex.Message}"); }
        }

        try
        {
            WadRepackService.Repack(project.SourceWadPath, overrideBytes, outputPath, report, progress, ct);
        }
        catch (OperationCanceledException)
        {
            report.Add(BuildSeverity.Warning, "Build cancelled.");
            return report;
        }
        catch (Exception ex)
        {
            report.Add(BuildSeverity.Error, $"Repack failed: {ex.Message}");
            return report;
        }

        Validate(outputPath, overrideBytes.Keys, report);
        report.Duration = sw.Elapsed;
        return report;
    }

    private static void Validate(string outputPath, IEnumerable<ulong> modified, BuildReport report)
    {
        try
        {
            using var wad = new WadFile(outputPath);
            int missing = 0;
            foreach (var hash in modified)
                if (!wad.Chunks.ContainsKey(hash)) { missing++; report.Add(BuildSeverity.Error, $"Modified chunk 0x{hash:x16} missing after build."); }

            // Best-effort: decompress one modified chunk to prove the data is readable.
            foreach (var hash in modified)
            {
                if (!wad.Chunks.TryGetValue(hash, out var chunk)) continue;
                using var _ = wad.LoadChunkDecompressed(chunk);
                break;
            }

            report.Validation = $"Reopened OK — {wad.Chunks.Count:n0} chunks" + (missing == 0 ? ", all modified chunks present." : $", {missing} modified chunk(s) MISSING.");
        }
        catch (Exception ex)
        {
            report.Add(BuildSeverity.Error, $"Built WAD failed to reopen: {ex.Message}");
        }
    }
}
