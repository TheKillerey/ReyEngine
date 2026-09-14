using LeagueToolkit.Core.Meta;

namespace ReyEngine.Formats.Meta;

/// <summary>What rebasing a recipe-made bin onto a new original produced.</summary>
/// <param name="Conflicts">Things a person should look at: the merge's real conflicts, plus one when the source
/// slot had to come from the recorded snapshot.</param>
/// <param name="MergedRemainder">The bin carried edits the recipe does not explain, and they were three-way
/// merged on top of the replayed original.</param>
public sealed record BinRecipeRebaseResult(
    byte[] Bytes,
    int Conflicts,
    bool MergedRemainder,
    IReadOnlyList<string> Lines,
    MapSkinReplayResult Replay);

/// <summary>
/// M730: the patch updater's path for a bin that has a recipe.
///
/// <para>The recipe is replayed on BOTH originals. If the project's bin is exactly the recipe replayed on the old
/// original, the result is the recipe replayed on the new one - nothing to merge, no conflicts, and slots the
/// patch added are forced like the rest. Otherwise the remainder, <c>diff(replay(old) → mod)</c>, is what the
/// user changed by hand beyond the recipe, and that alone is three-way merged onto the replayed new original.
/// A conflict reported here is therefore a real one: the recipe's own edits can no longer collide with a patch
/// that touched the same slots, because they are made fresh from that patch.</para>
/// </summary>
public static class BinRecipeRebase
{
    public static BinRecipeRebaseResult Rebase(MapSkinForceRecipe recipe, byte[] oldBase, byte[] mod, byte[] newBase,
        Func<uint, string?>? resolve)
    {
        var oldReplay = recipe.Replay(oldBase, resolve);
        var newReplay = recipe.Replay(newBase, resolve);

        var lines = new List<string>
        {
            $"Re-applied on the installed patch: {newReplay.Swap.RoutedSkinHashes.Count} slot(s) routed to {newReplay.SourceName}"
            + (recipe.CarryCharacterSkins && newReplay.Swap.CarriedCharacterSkins.Count > 0
                ? $", {newReplay.Swap.CarriedCharacterSkins.Count} character skin(s) carried" : "")
            + (newReplay.Swap.ChangedAudioProperties > 0 ? $", {newReplay.TargetName}'s audio profile routed" : "")
            + ".",
        };
        var oldNames = oldReplay.Catalog.Skins.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newNames = newReplay.Catalog.Skins.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = newReplay.Catalog.Skins.Where(s => !oldNames.Contains(s.Name)).Select(s => s.Name).ToList();
        var removed = oldReplay.Catalog.Skins.Where(s => !newNames.Contains(s.Name)).Select(s => s.Name).ToList();
        if (added.Count > 0)
            lines.Add($"{added.Count} slot(s) new in this patch forced as well: {string.Join(", ", added)}.");
        if (removed.Count > 0)
            lines.Add($"{removed.Count} slot(s) this patch no longer ships: {string.Join(", ", removed)}.");
        lines.AddRange(newReplay.Notes);
        int conflicts = newReplay.UsedSnapshot ? 1 : 0;

        var modTree = SafeBinTree.Parse(mod);
        var oldReplayTree = SafeBinTree.Parse(oldReplay.Bytes);
        if (TreesEqual(modTree, oldReplayTree))
            return new BinRecipeRebaseResult(newReplay.Bytes, conflicts, false, lines, newReplay);

        // The remainder is the user's, so it is carried the way every hand edit is carried.
        var (merged, report) = BinThreeWayMerge.Merge(oldReplay.Bytes, mod, newReplay.Bytes, resolve);
        var remainder = DifferingObjects(oldReplayTree, modTree, resolve);
        lines.Add($"Beyond the recipe the file carried {report.ModAdded} added / {report.ModRemoved} removed / "
                  + $"{report.ModModified} modified object(s); those were merged onto the re-applied bin"
                  + (remainder.Count > 0 ? $": {string.Join(", ", remainder.Take(6))}{(remainder.Count > 6 ? $", +{remainder.Count - 6} more" : "")}" : "")
                  + ".");
        lines.AddRange(report.ConflictDetails.Take(3));
        if (report.ConflictDetails.Count > 3) lines.Add($"... {report.ConflictDetails.Count - 3} more conflict(s)");
        return new BinRecipeRebaseResult(merged, conflicts + report.Conflicts, true, lines, newReplay);
    }

    private static bool TreesEqual(BinTree a, BinTree b)
    {
        if (a.Objects.Count != b.Objects.Count || !a.Dependencies.SequenceEqual(b.Dependencies)) return false;
        foreach (var (key, objectA) in a.Objects)
            if (!b.Objects.TryGetValue(key, out var objectB) || !BinPropEquality.ObjectsEqual(objectA, objectB)) return false;
        return true;
    }

    private static List<string> DifferingObjects(BinTree a, BinTree b, Func<uint, string?>? resolve)
    {
        string Name(uint h) => resolve?.Invoke(h) ?? $"0x{h:x8}";
        var names = new List<string>();
        foreach (uint key in a.Objects.Keys.Union(b.Objects.Keys))
        {
            bool inA = a.Objects.TryGetValue(key, out var objectA), inB = b.Objects.TryGetValue(key, out var objectB);
            if (inA && inB && BinPropEquality.ObjectsEqual(objectA!, objectB!)) continue;
            names.Add(Name(key) + (inA && !inB ? " (removed)" : !inA && inB ? " (added)" : ""));
        }
        return names;
    }
}
