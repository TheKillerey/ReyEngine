namespace ReyEngine.Core.Projects;

/// <summary>
/// M742: one modpkg layer of a project - a named, separately switchable slice of the mod's content.
///
/// <para>LTK Manager applies layers in <see cref="Priority"/> order, lowest first, so a higher number wins
/// where two layers touch the same file. A user can disable any layer per profile, and a layer the profile
/// says nothing about is enabled.</para>
/// </summary>
public sealed class ProjectLayer
{
    /// <summary>The layer every project has, and where a folder claimed by nobody ships.</summary>
    public const string BaseLayer = "base";

    public string Name { get; set; } = "";

    /// <summary>Lowest first; a higher number is applied over a lower one.</summary>
    public int Priority { get; set; }

    /// <summary>Shown to the user in the manager, beside the switch.</summary>
    public string Description { get; set; } = "";

    /// <summary>WAD folder names (as in <c>ProjectFolders</c>) that ship in this layer.</summary>
    public List<string> Folders { get; set; } = new();
}
