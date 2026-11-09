using ReyEngine.Formats.Scripts;

namespace ReyEngine.Formats.Characters.Simulation;

/// <summary>
/// M891: what an ability's compiled scripts say, read through the game-wide file index. For the root spell and its child spells it reads
/// <c>data/characters/&lt;champion&gt;/spells/&lt;spell&gt;.preload</c> and <c>.luabin64</c>, then follows the helper spells the preload names (BBPreloadSpell) that have files of their own
/// in the same folder, breadth first, to a bounded depth. Everything returned is authored: names come from the preload text and from the property table of the luabin, never from a
/// naming rule. <c>Characters</c> are the units the ability's own scripts preload; <c>Spells</c> and <c>BuffEffects</c> follow the whole helper chain.
/// </summary>
public static class SimScriptData
{
    private const int MaxSpells = 64;
    private const int MaxDepth = 4;

    public sealed record Result(
        IReadOnlyList<string> Characters,
        IReadOnlyList<string> Spells,
        IReadOnlyList<BuffEffectSim> BuffEffects,
        IReadOnlyList<string> Files,
        IReadOnlyList<string> Issues);

    public static Result Read(string champion, IEnumerable<string> ownScripts, Func<string, string, byte[]?> readFile)
    {
        string folder = $"data/characters/{champion.ToLowerInvariant()}/spells/";
        string wad = champion + ".wad.client";
        var characters = new List<string>();
        var spells = new List<string>();
        var effects = new List<BuffEffectSim>();
        var files = new List<string>();
        var issues = new List<string>();
        var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Name, int Depth)>();
        foreach (var n in ownScripts)
            if (n.Length > 0 && own.Add(n) && seen.Add(n)) queue.Enqueue((n, 0));

        while (queue.Count > 0)
        {
            var (name, depth) = queue.Dequeue();
            string lower = name.ToLowerInvariant();

            string prePath = folder + lower + ".preload";
            if (readFile(prePath, wad) is { } preBytes)
            {
                files.Add(prePath);
                var pre = PreloadFile.Parse(preBytes);
                foreach (var note in pre.Notes) issues.Add($"{prePath}: {note}");
                foreach (var e in pre.Entries)
                {
                    if (e.Name.Length == 0) continue;
                    if (e.Kind == PreloadKind.Character)
                    {
                        // only the ability's OWN scripts (root and children): a helper spell's units belong to the helper (Kalista Q's chain reaches her passive's Thresh lantern)
                        if (depth > 0) continue;
                        if (!characters.Contains(e.Name, StringComparer.OrdinalIgnoreCase)) characters.Add(e.Name);
                    }
                    else if (e.Kind == PreloadKind.Spell && !own.Contains(e.Name))
                    {
                        if (!spells.Contains(e.Name, StringComparer.OrdinalIgnoreCase)) spells.Add(e.Name);
                        if (depth < MaxDepth && seen.Count < MaxSpells && seen.Add(e.Name)) queue.Enqueue((e.Name, depth + 1));
                    }
                }
            }

            string luaPath = folder + lower + ".luabin64";
            if (readFile(luaPath, wad) is { } luaBytes)
            {
                files.Add(luaPath);
                try
                {
                    var lua = LuaBin64File.Parse(luaBytes);
                    string? buff = lua.GetString("BuffName");
                    foreach (string suffix in new[] { "", "2", "3", "4" })
                    {
                        if (lua.GetString("AutoBuffActivateEffect" + suffix) is not { Length: > 0 } effect) continue;
                        string? bone = lua.GetString("AutoBuffActivateAttachBoneName" + suffix);
                        effects.Add(new BuffEffectSim(name, buff, effect, string.IsNullOrEmpty(bone) ? null : bone, luaPath));
                    }
                }
                catch (LuaBin64Exception ex) { issues.Add($"{luaPath}: {ex.Message}"); }
            }
        }
        return new Result(characters, spells, effects, files, issues);
    }
}
