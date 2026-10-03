using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using ReyEngine.Core.Build;
using ReyEngine.Core.Projects;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M744: one row of the content-layer editor - a layer the mod ships as a separately switchable slice.
///
/// <para>"base" is a row like any other so the user can see where unclaimed folders go, but it is not
/// theirs to rename, renumber or delete: the exporter always declares it and it is the fallback
/// <see cref="ReyProject.LayerOf"/> returns.</para>
/// </summary>
public sealed partial class ProjectLayerRow : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private int _priority;
    [ObservableProperty] private string _description = "";

    /// <summary>True for the one row standing for <see cref="ProjectLayer.BaseLayer"/>.</summary>
    public bool IsBase { get; init; }

    /// <summary>M816: the project layer this row was made from, null for a layer added in the dialog. The dialog edits a layer's name,
    /// priority, description and folders; what an imported .fantome gave it - its display name, string overrides and the key to
    /// its stored GameData - is not the dialog's to show, and is carried over to the layer it writes back.</summary>
    public ProjectLayer? Source { get; init; }

    public bool IsEditable => !IsBase;

    /// <summary>What the combo box shows. Kept in step with <see cref="Name"/> so renaming a layer
    /// renames it everywhere it is offered, without rebuilding the list under the user's selection.</summary>
    public string Label => IsBase ? ProjectLayer.BaseLayer : (string.IsNullOrWhiteSpace(Name) ? "(unnamed)" : Name);

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(Label));

    public override string ToString() => Label;

    /// <summary>Why this layer cannot ship, or null.
    ///
    /// <para>A layer ships as <c>content/&lt;name&gt;/</c> in a mod sent to LTK Manager and, since M814, as
    /// <c>WAD_&lt;name&gt;/</c> in an exported .fantome. The name has to be one both can carry, and the .fantome's
    /// rule is the stricter - ASCII letters, digits, '-' and '_' (<see cref="FantomeLayers.NameProblem"/>) - so
    /// that is the rule here: the editor refuses at Save what Export .fantome would refuse later, with the same
    /// words, and the user is never handed a layer called "Particle Fix" that only fails at export time.</para></summary>
    public string? Problem(IEnumerable<ProjectLayerRow> siblings)
    {
        if (IsBase) return null;
        string name = Name.Trim();
        if (FantomeLayers.NameProblem(name) is { } problem) return problem;
        if (siblings.Any(o => !ReferenceEquals(o, this) && !o.IsBase
                && string.Equals(o.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
            return $"Two layers are called '{name}'.";
        return null;
    }
}

/// <summary>M744: one WAD folder of the project and the layer it ships in. A folder rides exactly one
/// layer, which is what <see cref="ReyProject.LayerOf"/> assumes, so this is a single choice and not a
/// set of checkboxes.</summary>
public sealed partial class FolderLayerRow : ObservableObject
{
    [ObservableProperty] private ProjectLayerRow _layer;

    public FolderLayerRow(string folder, ProjectLayerRow layer, ObservableCollection<ProjectLayerRow> choices)
    {
        Folder = folder;
        _layer = layer;
        Choices = choices;
    }

    /// <summary>The WAD folder's name, as the exporter derives it - the leaf of the resolved path.
    /// M816: a folder a layer claims WHOLE (an imported layer's WAD, <c>layers/winter/Map11</c>) is listed, and written back, by its whole entry.</summary>
    public string Folder { get; }

    /// <summary>M816 review: whether <see cref="Folder"/> is a whole entry with a path in it (<see cref="ReyProject.IsClaimedWhole"/>): only those are
    /// listed with a '/', since a leaf name has none.</summary>
    public bool IsWholeClaim => Folder.Contains('/');

    /// <summary>The name the WAD goes by: the last part of <see cref="Folder"/>.</summary>
    public string Leaf => Folder[(Folder.LastIndexOf('/') + 1)..];

    /// <summary>Shared with every other row, so adding or renaming a layer is offered here at once.</summary>
    public ObservableCollection<ProjectLayerRow> Choices { get; }
}
