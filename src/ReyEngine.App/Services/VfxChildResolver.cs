using System;
using System.Collections.Generic;
using ReyEngine.App.ViewModels;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.App.Services;

/// <summary>
/// <para>M854: how a system's child sets become playable items, shared by every window that previews a system - the
/// Character window (<c>MeshPreviewViewModel</c>) and the Particle Editor resolve through this one function, so a
/// child that plays in one plays in the other.</para>
///
/// <para>A child is named either by <c>effectKey</c> - a key in the namespace of the skin's
/// <c>ResourceResolver.resourceMap</c>, hence the indirection - or by <c>effect</c>, a direct object link. All three
/// readings are tried, in the order the Character window always used: key through the map, link, key as a system hash.
/// Unresolved children are dropped, the common case for the ~30% of keys that live only in bins the caller did not
/// load.</para>
///
/// <para><b>Termination.</b> Nothing in the data rules out a child chain reaching back to its own parent, so two
/// guards apply: a hard depth cap (<see cref="MaxChildDepth"/>) and an ancestor check - a child that is already on the
/// path from the root is not resolved again. 292 chains in the installed game go three or more levels deep, which is
/// why the cap is 4 and not the 2 it was.</para>
/// </summary>
public static class VfxChildResolver
{
    /// <summary>Levels of children below the played system. The root is depth 0; a child built at depth
    /// <c>MaxChildDepth</c> has no children of its own resolved.</summary>
    public const int MaxChildDepth = 4;

    /// <summary>Resolve one system's per-emitter child lists, aligned to its AUTHORED emitters; null when no emitter
    /// has a resolvable child. <paramref name="build"/> turns a child definition into an item at the given depth and
    /// is expected to call back into this method for the child's own children.</summary>
    /// <param name="chain">System hashes on the path from the root to <paramref name="system"/>, caller-owned and
    /// empty for the root. This method adds <paramref name="system"/> for the duration of the call.</param>
    public static IReadOnlyList<IReadOnlyList<VfxPlaybackItem>?>? Resolve(
        VfxSystemDefinition system, int depth,
        Func<uint, VfxSystemDefinition?> systemByHash,
        Func<uint, uint?> hashOfKey,
        Func<VfxSystemDefinition, int, VfxPlaybackItem> build,
        ISet<uint> chain)
    {
        if (depth >= MaxChildDepth) return null;
        bool added = chain.Add(system.PathHash);
        try
        {
            List<IReadOnlyList<VfxPlaybackItem>?>? perEmitter = null;
            for (int i = 0; i < system.Emitters.Count; i++)
            {
                var set = system.Emitters[i].Children;
                List<VfxPlaybackItem>? items = null;
                if (set is not null)
                    foreach (var id in set.Children)
                    {
                        var child = Find(id, systemByHash, hashOfKey);
                        if (child is null) continue;
                        // an ancestor of this very chain: building it again would only repeat the chain to the cap
                        if (chain.Contains(child.PathHash)) continue;
                        (items ??= new()).Add(build(child, depth + 1));
                    }
                if (items is not null)
                {
                    perEmitter ??= new List<IReadOnlyList<VfxPlaybackItem>?>(new IReadOnlyList<VfxPlaybackItem>?[system.Emitters.Count]);
                    perEmitter[i] = items;
                }
            }
            return perEmitter;
        }
        finally { if (added) chain.Remove(system.PathHash); }
    }

    /// <summary>The system a child identifier names, or null.</summary>
    public static VfxSystemDefinition? Find(VfxChildIdentifier id,
        Func<uint, VfxSystemDefinition?> systemByHash, Func<uint, uint?> hashOfKey)
    {
        VfxSystemDefinition? child = null;
        if (id.EffectKey != 0 && hashOfKey(id.EffectKey) is { } mapped) child = systemByHash(mapped);
        if (child is null && id.EffectLink != 0) child = systemByHash(id.EffectLink);
        if (child is null && id.EffectKey != 0) child = systemByHash(id.EffectKey);
        return child;
    }
}
