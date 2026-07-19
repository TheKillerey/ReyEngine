using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;

namespace ReyEngine.Formats.Meta;

/// <summary>What one merge did: how much of the patch base survived and what the mod contributed.</summary>
public sealed record BinMergeReport(
    int NewBaseObjects, int ModAdded, int ModRemoved, int ModModified, int Conflicts,
    IReadOnlyList<string> ConflictDetails, IReadOnlyList<string> Notes)
{
    public bool HasModChanges => ModAdded + ModRemoved + ModModified > 0;
}

/// <summary>
/// M97: structural three-way merge for .bin files — the core of "update this mod to the current patch".
/// diff(oldBase → mod) is re-applied onto newBase at the OBJECT level, and inside objects the mod touched
/// at the PROPERTY level, so everything Riot added in the new patch survives and only the mod's actual
/// edits are carried over. Conflicts (both sides changed the same thing) resolve mod-wins and are
/// reported. Output is re-parsed before returning — a merge that doesn't round-trip throws.
/// </summary>
public static class BinThreeWayMerge
{
    public static (byte[] Merged, BinMergeReport Report) Merge(
        byte[] oldBase, byte[] mod, byte[] newBase, Func<uint, string?>? resolve = null)
    {
        var oldT = SafeBinTree.Parse(oldBase);
        var modT = SafeBinTree.Parse(mod);
        var newT = SafeBinTree.Parse(newBase);
        string R(uint h) => resolve?.Invoke(h) ?? $"0x{h:x8}";

        int added = 0, removed = 0, modified = 0, conflicts = 0;
        var conflictDetails = new List<string>();
        var notes = new List<string>();

        foreach (uint key in oldT.Objects.Keys.Union(modT.Objects.Keys).ToList())
        {
            bool inOld = oldT.Objects.TryGetValue(key, out var o);
            bool inMod = modT.Objects.TryGetValue(key, out var m);

            if (inOld && !inMod)
            {
                // mod deleted the object → delete it from the new base too
                if (newT.Objects.Remove(key)) removed++;
                else notes.Add($"object {R(key)} deleted by mod was already gone in the new patch");
            }
            else if (!inOld && inMod)
            {
                // mod added the object → carry it over (mod wins if the patch added the same hash)
                if (newT.Objects.ContainsKey(key) && !BinPropEquality.ObjectsEqual(newT.Objects[key], m!))
                { conflicts++; conflictDetails.Add($"object {R(key)}: added by both mod and patch — mod version kept"); }
                newT.Objects[key] = m!;
                added++;
            }
            else if (inOld && inMod && !BinPropEquality.ObjectsEqual(m!, o!))
            {
                // mod modified the object
                if (!newT.Objects.TryGetValue(key, out var n))
                {
                    // Riot removed it — restore the mod's version so the edit isn't silently lost
                    newT.Objects[key] = m;
                    modified++; conflicts++;
                    conflictDetails.Add($"object {R(key)}: edited by mod but removed by the patch — mod version restored");
                    continue;
                }
                if (n.ClassHash != m.ClassHash)
                {
                    // class changed between patches — property merge across classes is meaningless
                    newT.Objects[key] = m;
                    modified++; conflicts++;
                    conflictDetails.Add($"object {R(key)}: meta class changed in the patch — mod version kept whole");
                    continue;
                }

                bool anyChange = false;
                foreach (uint ph in o!.Properties.Keys.Union(m.Properties.Keys).ToList())
                {
                    bool pOld = o.Properties.TryGetValue(ph, out var po);
                    bool pMod = m.Properties.TryGetValue(ph, out var pm);

                    if (pOld && !pMod)
                    {
                        if (n.Properties.Remove(ph)) anyChange = true;   // mod deleted the property
                    }
                    else if (!pOld && pMod)
                    {
                        if (n.Properties.TryGetValue(ph, out var existing) && !BinPropEquality.PropsEqual(existing, pm))
                        { conflicts++; conflictDetails.Add($"{R(key)}.{R(ph)}: added by both mod and patch — mod value kept"); }
                        n.Properties[ph] = pm!;                          // mod added the property
                        anyChange = true;
                    }
                    else if (pOld && pMod && !BinPropEquality.PropsEqual(pm, po))
                    {
                        // mod changed the property; did the patch change it too?
                        bool patchChanged = !n.Properties.TryGetValue(ph, out var pn) || !BinPropEquality.PropsEqual(pn, po);
                        if (patchChanged)
                        { conflicts++; conflictDetails.Add($"{R(key)}.{R(ph)}: changed by both mod and patch — mod value kept"); }
                        n.Properties[ph] = pm;
                        anyChange = true;
                    }
                }
                if (anyChange) modified++;
            }
        }

        // dependencies: the patch's list wins; keep any extra bins the mod linked (custom VFX bins etc.)
        foreach (var dep in modT.Dependencies)
            if (!oldT.Dependencies.Contains(dep) && !newT.Dependencies.Contains(dep))
            { newT.Dependencies.Add(dep); notes.Add($"dependency added by mod kept: {dep}"); }

        using var ms = new MemoryStream();
        newT.Write(ms);
        byte[] bytes = ms.ToArray();
        _ = SafeBinTree.Parse(bytes);   // must round-trip or the merge is unusable

        return (bytes, new BinMergeReport(newT.Objects.Count, added, removed, modified, conflicts,
            conflictDetails, notes));
    }
}
