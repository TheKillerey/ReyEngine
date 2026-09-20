using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
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

    public bool IsEditable => !IsBase;

    /// <summary>What the combo box shows. Kept in step with <see cref="Name"/> so renaming a layer
    /// renames it everywhere it is offered, without rebuilding the list under the user's selection.</summary>
    public string Label => IsBase ? ProjectLayer.BaseLayer : (string.IsNullOrWhiteSpace(Name) ? "(unnamed)" : Name);

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(Label));

    public override string ToString() => Label;

    /// <summary>Why this layer cannot ship, or null. The name becomes a folder under <c>content/</c>, so
    /// it has to be usable as one.</summary>
    public string? Problem(IEnumerable<ProjectLayerRow> siblings)
    {
        if (IsBase) return null;
        string name = Name.Trim();
        if (name.Length == 0) return "A layer needs a name.";
        if (string.Equals(name, ProjectLayer.BaseLayer, StringComparison.OrdinalIgnoreCase))
            return $"'{ProjectLayer.BaseLayer}' is the layer everything unclaimed already ships in.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return $"'{name}' cannot be a folder name, and a layer ships as content/{name}/.";
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

    /// <summary>The WAD folder's name, as the exporter derives it - the leaf of the resolved path.</summary>
    public string Folder { get; }

    /// <summary>Shared with every other row, so adding or renaming a layer is offered here at once.</summary>
    public ObservableCollection<ProjectLayerRow> Choices { get; }
}
