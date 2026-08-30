using ReyEngine.Core.Wad;

namespace ReyEngine.Formats.Characters;

/// <summary>What a character is to the champion it ships with.</summary>
public enum CharacterKind
{
    /// <summary>The champion itself — the character whose folder matches the WAD.</summary>
    Champion,
    /// <summary>A pet, summon, trap or second form shipped in the same WAD: annietibbers, elisespider,
    /// gnarbig, azirsoldier. They are full characters with their own skins, meshes and animations.</summary>
    Companion,
    /// <summary>A <c>jade_</c> variant — the Arena-mode double of a character.</summary>
    Jade,
}

/// <summary>One skin, before its bin has been read: just enough to list it and go and get it.</summary>
public sealed record CharacterSkinRef(int Number, string BinPath)
{
    /// <summary>root.bin carries a champion's shared defaults and has no mesh of its own.</summary>
    public bool IsRoot => Number < 0;
}

public sealed record CharacterEntry(string Name, CharacterKind Kind, IReadOnlyList<CharacterSkinRef> Skins);

/// <summary>One champion WAD and the characters inside it.</summary>
public sealed record ChampionPackage(string Name, string WadPath)
{
    public string FolderName => Name.ToLowerInvariant();
}

/// <summary>
/// M609: which characters exist, and which skins each one has.
///
/// <para>Everything before this was addressed by path: to look at a champion you opened its WAD, expanded
/// a tree and found a .skn by eye. That works when you already know what you are looking for, which is
/// the one case a character editor cannot assume.</para>
///
/// <para>Listing is deliberately split in two. The champion list comes from WAD file names and touches no
/// archive at all; the characters and skins inside one champion come from that archive's resolved paths
/// and parse no bins. Only a skin you actually select gets read (<see cref="CharacterSkinReader"/>) —
/// Ahri alone ships 96 skin bins across 2 characters, and parsing them to draw a list would be seconds of
/// work to answer a question nobody asked.</para>
/// </summary>
public static class CharacterCatalog
{
    /// <summary>Every champion WAD in a <c>DATA/FINAL/Champions</c> folder, by name. Locale WADs
    /// (<c>Ahri.en_US.wad.client</c>) are excluded: they hold voice-over and no characters at all.</summary>
    public static IReadOnlyList<ChampionPackage> Champions(string championsDirectory)
    {
        if (!Directory.Exists(championsDirectory)) return Array.Empty<ChampionPackage>();

        var packages = new List<ChampionPackage>();
        foreach (string file in Directory.EnumerateFiles(championsDirectory, "*.wad.client"))
        {
            string name = Path.GetFileName(file);
            name = name[..^".wad.client".Length];
            if (name.Contains('.')) continue;     // Ahri.en_US -> voice-over only
            packages.Add(new ChampionPackage(name, file));
        }
        packages.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return packages;
    }

    /// <summary>The characters inside one open archive, and the skins each one has. Path scan only.
    ///
    /// <para>The archive must have been opened WITH a hash resolver: this reads resolved paths, and an
    /// archive whose chunks are still 0x-named lists nothing. "No characters" therefore means either an
    /// archive with no characters or an archive with no dictionary, and the caller is the one that knows
    /// which.</para></summary>
    /// <param name="championName">The WAD's champion name, used to tell the champion apart from the pets
    /// that ship beside it. Pass null to mark every character a companion.</param>
    public static IReadOnlyList<CharacterEntry> Characters(WadArchive archive, string? championName)
    {
        var skinsByCharacter = new Dictionary<string, List<CharacterSkinRef>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in archive.Entries)
        {
            if (!entry.IsResolved) continue;
            string path = entry.Path.Replace('\\', '/');
            if (!path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) continue;

            // data/characters/<name>/skins/<file>.bin — and only that shape. A champion WAD also holds
            // data/characters/<name>/<name>.bin and animations/*.bin, which are not skins.
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 5) continue;
            if (!parts[0].Equals("data", StringComparison.OrdinalIgnoreCase)) continue;
            if (!parts[1].Equals("characters", StringComparison.OrdinalIgnoreCase)) continue;
            if (!parts[3].Equals("skins", StringComparison.OrdinalIgnoreCase)) continue;

            if (!skinsByCharacter.TryGetValue(parts[2], out var list))
                skinsByCharacter[parts[2]] = list = new List<CharacterSkinRef>();
            list.Add(new CharacterSkinRef(CharacterSkinReader.NumberFromBinPath(path), path));
        }

        var characters = new List<CharacterEntry>();
        foreach (var (name, skins) in skinsByCharacter)
        {
            skins.Sort((a, b) => a.Number.CompareTo(b.Number));
            characters.Add(new CharacterEntry(name, KindOf(name, championName), skins));
        }

        // Champion first, then jade doubles last: the companions are what you are most likely to want
        // after the champion itself, and the jade variants are duplicates of everything above them.
        characters.Sort((a, b) => a.Kind != b.Kind
            ? a.Kind.CompareTo(b.Kind)
            : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return characters;
    }

    public static CharacterKind KindOf(string characterName, string? championName)
    {
        if (characterName.StartsWith("jade_", StringComparison.OrdinalIgnoreCase)) return CharacterKind.Jade;
        return championName is not null && characterName.Equals(championName, StringComparison.OrdinalIgnoreCase)
            ? CharacterKind.Champion
            : CharacterKind.Companion;
    }
}
