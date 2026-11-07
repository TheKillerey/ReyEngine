using ReyEngine.Formats.Skeletons;

namespace ReyEngine.Formats.Characters.Simulation;

/// <summary>The clip file(s) the player plays for an ability and why: <see cref="Source"/> is "authored" (Riot's name, resolved in the loaded skin's own
/// graph), "authored-composite" (the same through a sequencer/selector/parametric/condition entry), "definition" (the base skin's file from the
/// definition) or "composite" (the spell composite's own clip). <see cref="Chain"/> holds every file in play order; the first is <see cref="File"/>.</summary>
public sealed record SimClipChoice(string File, string Source, IReadOnlyList<string> Chain);

/// <summary>
/// M873: which clip the player plays for an ability. Riot's <c>mAnimationName</c> is authoritative even when it looks swapped (Amumu's W and E
/// author <c>Crit</c>, Bard's W and E author <c>Spell3</c> and <c>Spell2</c>); the composite's slot-name guess (<c>spell1..4</c> prefixes) is
/// the LAST resort, not the first.
///
/// <para>The order, most to least authored: (1) the authored name resolved in the LOADED skin's own animation graph (atomic clip, or the first
/// clip a composite entry plays - see <see cref="AnimationClipGraph"/>); (2) the definition's base-skin file, when the skin has no clip of that
/// name; (3) the composite's own clip. A clip of another skin's graph is never chosen in step 1, because the resolution looks at this skin's graph only.
/// An ability that authors no clip name resolves the graph name of the definition's clip the same way, so the definition, the planner's timing and the player
/// agree on one clip.</para>
/// </summary>
public static class SimClipChooser
{
    /// <param name="ability">The definition's ability.</param>
    /// <param name="ownClips">Atomic clips of the loaded skin's own graph.</param>
    /// <param name="ownNodes">Composite entries of the loaded skin's own graph.</param>
    /// <param name="hasFile">Whether an .anm of this file name is loaded in the animation list.</param>
    /// <param name="compositeFile">The spell composite's own clip file (this skin's), or null.</param>
    public static SimClipChoice? Choose(AbilitySim ability, IEnumerable<AnimClipInfo> ownClips, IEnumerable<AnimClipNode> ownNodes,
        Func<string?, bool> hasFile, string? compositeFile)
    {
        string? definitionFile = FileOf(ability.Clip?.AnmPath);
        bool authored = ability.AuthoredClipName is { Length: > 0 };

        // 1. the clip NAME in this skin's own graph: Riot's authored name, or - for an ability that authors none - the graph name of the clip the
        //    definition chose (a skin that has its own version of that clip plays its own)
        string? name = authored ? ability.AuthoredClipName : ability.Clip?.Name;
        if (name is { Length: > 0 } && AnimationClipGraph.Resolve(name, ownClips, ownNodes) is { } r)
        {
            var files = r.Chain.Select(c => FileOf(c.AnmPath)).Where(f => f is not null && hasFile(f)).Select(f => f!).ToList();
            // the FIRST clip of the chain must be the one that is loaded: otherwise the second would play under the first's label
            if (files.Count > 0 && FileOf(r.Chain[0].AnmPath) is { } head && string.Equals(head, files[0], StringComparison.OrdinalIgnoreCase))
                return new SimClipChoice(files[0], !authored ? "definition" : r.IsComposite ? "authored-composite" : "authored", files);
        }

        // 2. the definition's base-skin file, when this skin's graph has no clip of that name
        if (definitionFile is not null && hasFile(definitionFile))
        {
            var chain = ability.ClipChain is { Count: > 1 } cc
                ? cc.Select(c => FileOf(c.AnmPath)).Where(f => f is not null && hasFile(f)).Select(f => f!).ToList()
                : new List<string>();
            if (chain.Count == 0) chain.Add(definitionFile);
            return new SimClipChoice(chain[0], "definition", chain);
        }

        // 3. the spell composite's own clip
        return compositeFile is null ? null : new SimClipChoice(compositeFile, "composite", new[] { compositeFile });
    }

    public static string? FileOf(string? anmPath) =>
        string.IsNullOrWhiteSpace(anmPath) ? null : Path.GetFileName(anmPath.Replace('\\', '/'));
}
