namespace ReyEngine.Core.Projects;

/// <summary>
/// M730: what the editor DID to a project bin, kept beside the bin so a Riot patch update can do it again to the
/// new original instead of carrying the old result across.
///
/// <para><b>Why a recipe and not the diff.</b> The patch updater rebases a bin as a three-way merge: the mod's
/// edits, read as diff(old original → mod), re-applied onto the new original. That is right for a hand edit and
/// wrong for a Map Skin Switcher run, because the switcher's edit is a RULE - "every map skin slot loads this
/// environment" - and a rule has consequences the diff never saw. Measured on the user's own Map Forcer: on 16.15
/// it routed all 36 slots to the Default environment; 16.17 added a 37th slot (Hall_Of_Legends), the merge carried
/// the 36 edits faithfully and left the new one alone, and the game loaded Hall of Legends. Doing the switch again
/// on the 16.17 original forces 37.</para>
///
/// <para>Stored as plain properties so it round-trips through project.json; the Formats layer owns the meaning
/// (MapSkinForceRecipe). One kind so far.</para>
/// </summary>
public sealed class BinRecipeRecord
{
    public const string ForceMapSkinKind = "ForceMapSkin";

    /// <summary>The bin's wad-relative path, e.g. <c>data/maps/shipping/map11/map11.bin</c>.</summary>
    public string Path { get; set; } = "";

    public string Kind { get; set; } = ForceMapSkinKind;

    public int MapId { get; set; }

    /// <summary>The current/base slot the switcher was run against - the one whose audio profile takes the
    /// source's. Null for a recipe inferred from a file whose audio was not routed: route every slot, touch no
    /// audio profile.</summary>
    public string? TargetSkin { get; set; }

    /// <summary>The slot whose environment every slot is forced to load.</summary>
    public string SourceSkin { get; set; } = "";

    /// <summary>The source's <c>mMapContainerLink</c> when recorded - a second way to find the slot if Riot
    /// renames it, and the reader's way to see what the recipe forces without opening a bin.</summary>
    public string? SourceContainerLink { get; set; }

    /// <summary>M649's opt-in: the source's turret/minion/nexus skins are carried to every slot too.</summary>
    public bool CarryCharacterSkins { get; set; }

    /// <summary>Base64 of a small bin holding the source MapSkin object (and its FeatureAudio object when it has
    /// one) as they were when the recipe was recorded. Used only when a patch no longer ships the source slot -
    /// Riot vaults seasonal map skins - so the mod keeps forcing the environment it ships.</summary>
    public string? SourceSnapshot { get; set; }

    /// <summary><c>switcher</c> when the Map Skin Switcher recorded it, <c>inferred</c> when the updater read it
    /// back out of a bin made before recipes existed.</summary>
    public string Origin { get; set; } = "";

    public string RecordedUtc { get; set; } = "";

    /// <summary>The Riot patch the bin was on when the recipe was recorded or inferred.</summary>
    public string? RecordedOnPatch { get; set; }

    public static BinRecipeRecord? Find(IEnumerable<BinRecipeRecord> records, string path) =>
        records.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>One recipe per bin path: a new record for a path replaces the old one.</summary>
    public static void Upsert(List<BinRecipeRecord> records, BinRecipeRecord record)
    {
        records.RemoveAll(r => string.Equals(r.Path, record.Path, StringComparison.OrdinalIgnoreCase));
        records.Add(record);
    }
}
