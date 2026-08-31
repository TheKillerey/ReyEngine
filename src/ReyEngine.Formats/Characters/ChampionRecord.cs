using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Characters;

/// <summary>
/// M611: the champion's own record — <c>data/characters/&lt;name&gt;/&lt;name&gt;.bin</c>.
///
/// <para>Read for one thing: <c>spellNames</c>, the ORDERED list of a champion's four abilities. Position
/// is the slot — index 0 is Q, 3 is R — and position is the only thing that carries it. Riot names
/// abilities descriptively, so <c>DariusCleaveAbility</c> sits at index 0 and says nothing about being Q;
/// measured across the roster, 99 of 174 champions have at least one ability whose name gives no hint of
/// its slot. Anything that reads the letter out of the name is reading a coincidence.</para>
/// </summary>
public static class ChampionRecord
{
    private static readonly uint FSpellNames = HashAlgorithms.Fnv1a("spellNames");

    /// <summary>The champion record path for a character folder name.</summary>
    public static string PathFor(string characterName) =>
        $"data/characters/{characterName.ToLowerInvariant()}/{characterName.ToLowerInvariant()}.bin";

    /// <summary>The four ability names in slot order, or empty when the record has none. Measured: 173
    /// of 174 champions ship exactly four; the odd one out ships none at all.</summary>
    public static IReadOnlyList<string> SpellNames(byte[] recordBin)
    {
        try
        {
            var tree = new BinTree(new MemoryStream(recordBin));
            foreach (var o in tree.Objects.Values)
            {
                if (!o.Properties.TryGetValue(FSpellNames, out var property)) continue;
                if (property is not BinTreeContainer list) continue;

                var names = new List<string>();
                foreach (var element in list.Elements)
                    if (element is BinTreeString s) names.Add(s.Value);
                if (names.Count > 0) return names;
            }
        }
        catch
        {
            // A record that cannot be read costs the ability LABELS, not the ability list: the slots are
            // still Q/W/E/R and their clips are still found by name.
        }
        return Array.Empty<string>();
    }

    /// <summary>The readable half of a spell name. Riot writes "AhriQAbility/AhriQ" — the part before the
    /// slash is the ability record, the part after is the script. The record reads better.</summary>
    public static string Readable(string spellName)
    {
        if (string.IsNullOrWhiteSpace(spellName)) return "";
        int slash = spellName.IndexOf('/');
        string head = slash > 0 ? spellName[..slash] : spellName;
        return head.EndsWith("Ability", StringComparison.OrdinalIgnoreCase) && head.Length > "Ability".Length
            ? head[..^"Ability".Length]
            : head;
    }
}
