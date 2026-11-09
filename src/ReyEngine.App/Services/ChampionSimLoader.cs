using System;
using System.IO;
using System.Linq;
using ReyEngine.Core;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Characters.Simulation;
using ReyEngine.Formats.LtkGameData;

namespace ReyEngine.App.Services;

/// <summary>
/// M869: a champion's <see cref="ChampionSimDefinition"/> for the Character window: the cached JSON that <c>--simgen</c> (or an earlier
/// run of this loader) wrote, when it was made by this generator for this game build; otherwise generated in-process from the
/// champion WAD and written back. Synchronous and heavy on a miss (one WAD open, a VFX closure walk): call it off the UI thread.
/// </summary>
public static class ChampionSimLoader
{
    public sealed record Result(ChampionSimDefinition? Definition, bool FromCache, string? CachePath, string? Error);

    /// <param name="finalDirectory">The install's <c>DATA/FINAL</c> folder.</param>
    /// <param name="cacheRoot">Where <c>champion_sims/&lt;version&gt;/</c> lives; <see cref="ReyPaths.DataRoot"/> when null.</param>
    public static Result Load(string finalDirectory, string champion, IHashResolver resolver,
        Func<uint, string?> resolveBinName, Func<ulong, string?> resolveWadPath, string? cacheRoot = null)
    {
        try
        {
            string championsDirectory = Path.Combine(finalDirectory, "Champions");
            string? name = ChampionSimBatch.Roster(championsDirectory)
                .FirstOrDefault(n => n.Equals(champion, StringComparison.OrdinalIgnoreCase));
            if (name is null) return new Result(null, false, null, $"{champion} has no champion WAD in {championsDirectory}.");

            string version = ChampionSimSource.ReadGameVersion(finalDirectory);
            string dir = Path.Combine(cacheRoot ?? ReyPaths.DataRoot, "champion_sims", version);
            string file = Path.Combine(dir, name + ".json");

            if (File.Exists(file))
            {
                try
                {
                    var cached = ChampionSimJson.Read(File.ReadAllBytes(file));
                    if (cached.GeneratorVersion == ChampionSimDefinition.CurrentGeneratorVersion
                        && cached.SchemaVersion == ChampionSimDefinition.CurrentSchemaVersion
                        && cached.GameVersion == version)
                        return new Result(cached, true, file, null);
                }
                catch { /* an unreadable cache entry is regenerated below */ }
            }

            string? root = ClientNameCatalog.InstallRootFromChampions(championsDirectory);
            string? dataWad = ClientNameCatalog.FindDataWad(root ?? "");
            var client = ClientNameCatalog.Load(dataWad);
            using var abilities = ClientAbilityCatalog.Open(dataWad);
            var context = new ChampionSimContext
            {
                GameVersion = version,
                ResolveBinName = resolveBinName,
                ResolveWadPath = resolveWadPath,
                Client = client,
                ClientAbilities = abilities,
                ReadGameFile = GameFileIndex.GameDirectoryOf(finalDirectory) is { } gameDir ? GameFileIndex.Shared(gameDir).AsReader() : null,
            };
            ChampionSimDefinition def;
            using (var wad = WadArchive.Open(ChampionSimBatch.WadPathOf(championsDirectory, name), resolver))
                def = ChampionSimGenerator.Generate(wad, name, context);

            // best effort: a read-only data folder costs the cache, not the simulation
            try
            {
                Directory.CreateDirectory(dir);
                string temp = file + "." + Environment.ProcessId + ".tmp";
                File.WriteAllBytes(temp, ChampionSimJson.Write(def));
                File.Move(temp, file, overwrite: true);
            }
            catch { /* not cached */ }
            return new Result(def, false, file, null);
        }
        catch (Exception ex)
        {
            return new Result(null, false, null, ex.Message);
        }
    }
}
