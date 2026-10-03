using System.Text.Json.Nodes;

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

    /// <summary>Shown to the user in the manager, beside the switch. LTK Manager's workshop text
    /// (<c>mod.config.json</c> <c>layers[].description</c>); the .fantome layout has no such field.</summary>
    public string Description { get; set; } = "";

    /// <summary>
    /// WAD folder names (as in <c>ProjectFolders</c>) that ship in this layer.
    ///
    /// <para>A plain name is a folder's <i>leaf</i>, as it has been since M742: the layer claims every folder
    /// that ends in it. M816: a name with a path in it (<c>layers/winter/Map11</c>) is a whole
    /// <c>ProjectFolders</c> entry and claims that one folder alone - the way two folders of one WAD name, each in
    /// its own layer, are told apart. See <see cref="ReyProject.LayerOfFolder"/>.</para>
    /// </summary>
    public List<string> Folders { get; set; } = new();

    // ---- M816: what an imported LTK-layered .fantome carries per layer ----

    /// <summary>
    /// M816: the layer's name as a person reads it (<c>Layers.&lt;name&gt;.DisplayName</c> in a .fantome,
    /// <c>display_name</c> in LTK Manager's <c>mod.config.json</c>). Null when the layer has none; the .fantome
    /// export and Send to LTK Manager write it only when set.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// M816: the layer's string overrides - locale, then field, then replacement text - kept as the JSON the package
    /// held, so key order and every value come back as read. Null when the layer has none.
    /// </summary>
    public JsonObject? StringOverrides { get; set; }

    /// <summary>
    /// M816: which folder of <see cref="LtkProjectStore"/> holds this layer's imported GameData document and
    /// override files. Null when the layer imported none.
    ///
    /// <para>The key is the layer's name at the moment of import and stays what it was when the layer is renamed in
    /// Project Settings, so a rename cannot orphan the declarations. It is a directory name chosen by the importer
    /// (<see cref="LtkProjectStore.KeyFor"/>), never user text.</para>
    /// </summary>
    public string? DeclarationsKey { get; set; }
}
