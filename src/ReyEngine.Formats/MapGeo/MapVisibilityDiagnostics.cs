using System.Globalization;

namespace ReyEngine.Formats.MapGeo;

public enum VisibilityFindingSeverity { Info = 0, Warning = 1, Error = 2 }

public enum VisibilityFindingKind
{
    /// <summary>Content whose controller graph shows in no state of the map (e.g. an ALL with a Base-bit terrain controller).</summary>
    NeverVisible,
    /// <summary>A terrain controller whose mask has the Base bit: the game never has "Base" as a terraform, so it never shows.</summary>
    BaseBitTerrain,
    /// <summary>A controller that exists in Riot's own bin with other bits.</summary>
    NonRiotBits,
    /// <summary>An ALL (ParentMode 0 or absent) with more than two parents; Riot ships none.</summary>
    AllWithManyParents,
    /// <summary>A graph that reaches a class (or an object) this editor does not evaluate; it counts as visible.</summary>
    UnknownClass,
    /// <summary>A ParentMode the client does not define.</summary>
    InvalidParentMode,
    /// <summary>A graph that reaches itself.</summary>
    Cycle,
}

/// <summary>M849: one thing the map check found about the visibility controllers.</summary>
public sealed record VisibilityFinding(VisibilityFindingSeverity Severity, VisibilityFindingKind Kind, uint ControllerHash, string Message, int Users);

/// <summary>
/// M849: the map check of visibility controller graphs - what the game would do differently from what the author meant. The
/// rules come from the client's (LeagueToolkit ADR-0064) and from what Riot ships: no Base-bit terrain controller, ALLs with
/// exactly two parents, nesting only as [Child(NOR), X].
/// </summary>
public static class MapVisibilityDiagnostics
{
    /// <param name="controllers">The map's controller graphs.</param>
    /// <param name="users">One entry per mesh / placement that carries a controller (the controller hash; repeats count).
    /// Null or empty: the check covers every controller the bins hold, without counting users.</param>
    /// <param name="riotReference">Riot's own copy of the same bin(s), when available: a controller with the same hash but
    /// other bits is reported.</param>
    /// <param name="className">Optional hash -> name, for the class of an unknown controller.</param>
    public static IReadOnlyList<VisibilityFinding> Analyze(MapVisibilityControllers controllers, IEnumerable<uint>? users = null,
        MapVisibilityControllers? riotReference = null, Func<uint, string?>? className = null)
    {
        var counts = new Dictionary<uint, int>();
        if (users is not null)
            foreach (uint h in users)
                if (h != 0) counts[h] = counts.GetValueOrDefault(h) + 1;
        bool whole = counts.Count == 0;
        var roots = whole ? controllers.ControllerHashes.OrderBy(h => h).ToList() : counts.Keys.OrderBy(h => h).ToList();

        var findings = new List<VisibilityFinding>();
        var seenNodeFindings = new HashSet<(VisibilityFindingKind, uint)>();
        // how many users each node reaches (a user reaches every node under its controller)
        var nodeUsers = new Dictionary<uint, int>();
        foreach (uint root in roots)
        {
            int n = whole ? 0 : counts[root];
            foreach (uint node in controllers.Analyze(root).Nodes) nodeUsers[node] = nodeUsers.GetValueOrDefault(node) + n;
        }

        foreach (uint root in roots)
        {
            if (!controllers.Contains(root)) continue;
            int n = whole ? 0 : counts[root];
            var info = controllers.Analyze(root);

            // events are a yes/no the match decides: a graph that some event setting shows is not dead
            if (info.Axes.Count > 0 && !controllers.CanShow(root, new Dictionary<uint, int>(), null, freeEvents: true))
                findings.Add(new VisibilityFinding(VisibilityFindingSeverity.Error, VisibilityFindingKind.NeverVisible, root,
                    $"Controller 0x{root:x8} never shows in any state of the map" + Reached(controllers, root) + Users(n) + ".", n));

            foreach (var node in info.Unknowns)
                if (seenNodeFindings.Add((VisibilityFindingKind.UnknownClass, node.Hash)))
                    findings.Add(new VisibilityFinding(VisibilityFindingSeverity.Info, VisibilityFindingKind.UnknownClass, node.Hash,
                        node.Dangling
                            ? $"Controller 0x{node.Hash:x8} (reached from 0x{root:x8}) is in none of the map's bins; it counts as visible."
                            : $"Controller 0x{node.Hash:x8} (reached from 0x{root:x8}) is class {ClassName(node.ClassHash, className)}, which the editor does not evaluate; it counts as visible.",
                        nodeUsers.GetValueOrDefault(node.Hash)));
            foreach (var node in info.ManyParentAlls)
                if (seenNodeFindings.Add((VisibilityFindingKind.AllWithManyParents, node.Hash)))
                    findings.Add(new VisibilityFinding(VisibilityFindingSeverity.Warning, VisibilityFindingKind.AllWithManyParents, node.Hash,
                        $"Controller 0x{node.Hash:x8} is an ALL of {node.Parents.Count} parents; Riot ships only ALLs of exactly two, whether the client handles more is not known.",
                        nodeUsers.GetValueOrDefault(node.Hash)));
            foreach (var node in info.InvalidModes)
                if (seenNodeFindings.Add((VisibilityFindingKind.InvalidParentMode, node.Hash)))
                    findings.Add(new VisibilityFinding(VisibilityFindingSeverity.Warning, VisibilityFindingKind.InvalidParentMode, node.Hash,
                        $"Controller 0x{node.Hash:x8} has ParentMode {node.ParentMode}, which the client does not define (0 ALL, 1 ANY, 2 ONE, 3 NONE); it is read as ALL.",
                        nodeUsers.GetValueOrDefault(node.Hash)));
            if (info.HasCycle && seenNodeFindings.Add((VisibilityFindingKind.Cycle, root)))
                findings.Add(new VisibilityFinding(VisibilityFindingSeverity.Warning, VisibilityFindingKind.Cycle, root,
                    $"Controller 0x{root:x8} reaches itself through its parents.", n));
        }

        // terrain leaves with the Base bit, and leaves whose bits differ from Riot's
        var primary = controllers.PrimaryAxis;
        int baseBit = MapVisibility.IsSingleStateInitial(primary) ? primary!.InitialMask : 1;
        foreach (uint node in nodeUsers.Keys.OrderBy(h => h))
        {
            var cn = controllers.Node(node);
            if (cn.Kind != ControllerKind.Terrain) continue;
            if ((cn.Bits & baseBit) != 0)
                findings.Add(new VisibilityFinding(VisibilityFindingSeverity.Warning, VisibilityFindingKind.BaseBitTerrain, node,
                    $"Terrain controller 0x{node:x8} has the Base bit (mask {cn.Bits}); the game never has Base as a terraform, so it never shows (Riot ships none)."
                    + (riotReference is not null && riotReference.Contains(node) ? " It is a Riot controller." : ""),
                    nodeUsers[node]));
        }
        if (riotReference is not null)
            foreach (uint node in nodeUsers.Keys.OrderBy(h => h))
            {
                if (!riotReference.Contains(node)) continue;
                var cn = controllers.Node(node);
                var rn = riotReference.Node(node);
                if (cn.Kind != rn.Kind || cn.Kind is not (ControllerKind.Terrain or ControllerKind.BaronStage) || cn.Bits == rn.Bits) continue;
                findings.Add(new VisibilityFinding(VisibilityFindingSeverity.Warning, VisibilityFindingKind.NonRiotBits, node,
                    $"Controller 0x{node:x8} is Riot's, but its mask is {cn.Bits} here and {rn.Bits} in Riot's bin.", nodeUsers[node]));
            }

        return findings
            .OrderByDescending(f => f.Severity).ThenBy(f => f.Kind).ThenBy(f => f.ControllerHash)
            .ToList();
    }

    private static string ClassName(uint classHash, Func<uint, string?>? className) =>
        className?.Invoke(classHash) is { Length: > 0 } name ? $"{name} (0x{classHash:x8})" : $"0x{classHash:x8}";

    private static string Users(int n) => n > 0 ? $" ({n.ToString(CultureInfo.InvariantCulture)} mesh/placement(s) use it)" : "";

    private static string Reached(MapVisibilityControllers controllers, uint root)
    {
        var info = controllers.Analyze(root);
        var parts = new List<string>();
        foreach (uint h in info.Nodes.OrderBy(h => h))
        {
            var n = controllers.Node(h);
            if (n.Kind == ControllerKind.Terrain && (n.Bits & 1) != 0) parts.Add($"terrain 0x{h:x8} (mask {n.Bits})");
        }
        return parts.Count == 0 ? "" : " - it needs " + string.Join(" and ", parts);
    }
}
