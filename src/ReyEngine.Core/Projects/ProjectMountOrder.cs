using ReyEngine.Core.Build;

namespace ReyEngine.Core.Projects;

/// <summary>M819: one <c>ProjectFolders</c> entry as the mounts will take it: the layer it ships in, and the rank it holds among the project's folders.</summary>
/// <param name="Entry">The entry, as <see cref="ReyProject.ProjectFolders"/> spells it.</param>
/// <param name="Layer">The layer the folder ships in (<see cref="ReyProject.LayerOfFolder"/>): <see cref="ProjectLayer.BaseLayer"/> when no layer claims it.</param>
/// <param name="Precedence">Its rank (see <see cref="ProjectMountOrder"/>); lower wins. 0 for every folder of a project that has no layers.</param>
/// <param name="IsRaw">Whether it is the <c>RAW</c> folder an import keeps a package's <c>RAW/</c> files in.</param>
public sealed record ProjectFolderRank(string Entry, string Layer, int Precedence, bool IsRaw);

/// <summary>
/// M819: the order a project's WAD folders shadow one another in, as LTK Manager installs a mod whose files are in several layers (<c>ltk_overlay</c>, <c>builder/metadata.rs</c>).
///
/// <para>LTK reads the layers in apply order - the base layer first, then by ascending priority, then by name as a person reads it - and puts each layer's files in ONE table keyed by the chunk's
/// path hash, so a later layer's copy REPLACES an earlier one's: the higher-priority layer wins over base. Files in a <c>RAW</c> folder are collected after every layer, so they win over all of
/// them. The mounts are first-added-wins, so a folder's rank is the negative of its layer's place in the apply order (the layer applied last ranks first), and <c>RAW</c> ranks above every layer.</para>
///
/// <para><b>Only a project that has layers is ranked.</b> A project whose <see cref="ReyProject.Layers"/> is empty - every project made before layers, and every project that never named one -
/// gets 0 for every folder, which is the order its folders are listed in, as ever. In a project that has layers, a folder no layer claims is in <c>base</c>, so a project whose layers
/// claim nothing ranks as before too (the one exception being a folder called <c>RAW</c>, which an import writes and a person has no reason to).</para>
/// </summary>
public static class ProjectMountOrder
{
    /// <summary>The name of the folder an import keeps a package's <c>RAW/</c> files in.</summary>
    public const string RawFolder = "RAW";

    /// <summary>Every <c>ProjectFolders</c> entry of the project, in the order listed, with its layer and rank.</summary>
    public static IReadOnlyList<ProjectFolderRank> Rank(ReyProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var ranks = new List<ProjectFolderRank>(project.ProjectFolders.Count);
        bool layered = project.Layers.Count > 0;
        var place = layered ? ApplyOrderOf(project) : null;
        foreach (string entry in project.ProjectFolders)
        {
            // a project with no layers: nothing is asked of the entry at all - every folder is base, ranked 0, in the order listed
            if (!layered)
            {
                ranks.Add(new ProjectFolderRank(entry, ProjectLayer.BaseLayer, 0, false));
                continue;
            }
            string layer = project.LayerOfFolder(entry);
            bool raw = string.Equals(ReyProject.NormalizeFolderEntry(entry), RawFolder, StringComparison.OrdinalIgnoreCase);
            int precedence = raw ? int.MinValue : -(place!.TryGetValue(layer, out int at) ? at : 0);
            ranks.Add(new ProjectFolderRank(entry, layer, precedence, raw));
        }
        return ranks;
    }

    /// <summary>The layers of the project in the order LTK applies them: the place of each by name, the base layer first (0).</summary>
    private static Dictionary<string, int> ApplyOrderOf(ReyProject project)
    {
        var order = project.Layers
            .Where(l => !FantomeLayers.IsBase(l.Name))
            .OrderBy(l => l.Priority)
            .ThenBy(l => l.Name, Comparer<string>.Create(FantomeLayers.NaturalCompare))
            .ToList();
        var place = new Dictionary<string, int>(StringComparer.Ordinal) { [ProjectLayer.BaseLayer] = 0 };
        for (int i = 0; i < order.Count; i++) place[order[i].Name] = i + 1;
        return place;
    }
}
