using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M743: the jade map's champion projection sprites, switched off across the whole roster.
///
/// <para><b>What it fixes.</b> On Map453 every champion plays as its <c>Jade_&lt;Champion&gt;</c> variant -
/// the map's own <c>GameModeChampionList</c> names 68 of them - and those effects are old enough to carry
/// "projection" sprites: a flat additive quad laid on the ground under the effect. Malzahar's jade Q
/// carries <c>Proj_purple</c> at 800x800 where his modern Q's largest emitter is 200x450 and has no such
/// sprite at all. Over a ported map they spread across the terrain as hard-edged washes. The reporter had
/// already fixed the Map453 turret the same way, by disabling its <c>projected</c> emitter.</para>
///
/// <para><b>Why it is a command and not a script.</b> Riot rewrites these bins every patch, so the fix has
/// to be re-applied after each one. It reads the game's own champion wads, never the project's staged
/// copies, so running it twice gives the same answer as running it once.</para>
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>The layer the staged champion files ship in, so a user can switch the fix off in LTK
    /// Manager without losing the map (M742).</summary>
    public const string JadeProjectionLayer = "particle-fix";

    [RelayCommand]
    private async Task FixJadeChampionProjections()
    {
        if (!ProjectMode || Project.RootPath is not { } root)
        { _log.Warn("Jade VFX", "Open a folder project first - the fixed champion files are staged into it."); return; }

        string? gameDir = !string.IsNullOrEmpty(Project.GameDirectory) && Directory.Exists(Project.GameDirectory)
            ? Project.GameDirectory
            : Core.Projects.GameInstallLocator.Discover().FirstOrDefault()?.GameDirectory;
        string champsDir = gameDir is null ? "" : Path.Combine(gameDir, "DATA", "FINAL", "Champions");
        if (!Directory.Exists(champsDir))
        { _log.Error("Jade VFX", "No Champions folder in the game install - set the project's game directory."); return; }

        IsBuilding = true; Status = "Disabling jade projection sprites…";
        try
        {
            var result = await Task.Run(() => StageJadeProjectionFix(champsDir, root));

            foreach (string folder in result.Folders)
                if (!Project.ProjectFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                    Project.ProjectFolders.Add(folder);

            // One layer holds every champion folder, so the whole fix is one switch in LTK Manager.
            var layer = Project.Layers.FirstOrDefault(l =>
                string.Equals(l.Name, JadeProjectionLayer, StringComparison.OrdinalIgnoreCase));
            if (layer is null)
            {
                layer = new Core.Projects.ProjectLayer
                {
                    Name = JadeProjectionLayer,
                    Priority = 10,
                    Description = "Optional: disables the old jade champion projection sprites that spread "
                                + "across the map. Turn this off for Riot's original jade effects.",
                };
                Project.Layers.Add(layer);
            }
            foreach (string folder in result.Folders)
                if (!layer.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                    layer.Folders.Add(folder);

            if (Project.ProjectFilePath is { } pf) Core.Projects.ReyProjectService.Save(Project, pf);

            _log.Success("Jade VFX", $"{result.Emitters:n0} projection sprite(s) disabled across "
                + $"{result.Folders.Count} champion(s) in {result.Bins:n0} bin(s), staged into the "
                + $"'{JadeProjectionLayer}' layer. Send to LTK Manager to ship it.");
            if (result.TooLong > 0)
                _log.Warn("Jade VFX", $"{result.TooLong} bin(s) could not be staged: Riot's multi-skin names "
                    + "run past the 255 characters a file name may have, so a folder project cannot hold them.");
            Status = $"Jade projections: {result.Emitters:n0} disabled";
        }
        catch (Exception ex) { _log.Error("Jade VFX", ex.Message); }
        finally { IsBuilding = false; }
    }

    private sealed record JadeFixResult(List<string> Folders, int Bins, int Emitters, int TooLong);

    private JadeFixResult StageJadeProjectionFix(string champsDir, string projectRoot)
    {
        var resolver = new WadPathResolver(_sync.LoadLocal(_ => { }));
        var folders = new List<string>();
        int bins = 0, emitters = 0, tooLong = 0;

        foreach (string wadPath in Directory.EnumerateFiles(champsDir, "*.wad.client").OrderBy(p => p))
        {
            string champ = Path.GetFileName(wadPath).Split('.')[0];
            WadArchive wad;
            try { wad = WadArchive.Open(wadPath, resolver); } catch { continue; }
            using (wad)
            {
                bool any = false;
                foreach (var entry in wad.Entries.Where(e => e.IsResolved
                    && e.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                    && e.Path.Contains("/jade_", StringComparison.OrdinalIgnoreCase)))
                {
                    byte[] original;
                    try { original = wad.Extract(entry); } catch { continue; }
                    var fixedBytes = VfxProjectionEmitters.Disable(original, out var edits, out _);
                    if (fixedBytes is null || edits.Count == 0) continue;

                    string dest = Path.Combine(projectRoot, champ,
                        entry.Path.Replace('/', Path.DirectorySeparatorChar));
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        File.WriteAllBytes(dest, fixedBytes);
                    }
                    // A name past the 255-character ceiling surfaces as either of these.
                    catch (PathTooLongException) { tooLong++; continue; }
                    catch (IOException) { tooLong++; continue; }

                    bins++; emitters += edits.Count; any = true;
                }
                if (any) folders.Add(champ);
            }
        }
        return new JadeFixResult(folders, bins, emitters, tooLong);
    }
}
