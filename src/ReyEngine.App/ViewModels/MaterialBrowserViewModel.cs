using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Formats.Materials;

namespace ReyEngine.App.ViewModels;

/// <summary>One row in the browser, wrapping an audit result for display.</summary>
public sealed partial class MaterialRowViewModel : ObservableObject
{
    public MaterialRowViewModel(MaterialAuditRow row) { Row = row; }

    public MaterialAuditRow Row { get; }

    public string Name => Row.ShortName;
    public string FullName => Row.Name;
    public string Shader => Row.ShortShader.Length > 0 ? Row.ShortShader : "(none)";
    public int MeshCount => Row.MeshCount;
    public int TextureCount => Row.Textures.Count;
    public bool HasIssues => Row.HasIssues;
    public int IssueCount => Row.Findings.Count;

    /// <summary>The worst finding drives the badge, so a row that both breaks rendering and is merely
    /// untidy reads as the former.</summary>
    public string IssueLabel => Row.Worst is { } w ? Describe(w) : "";

    public string Tooltip => Row.Findings.Count == 0
        ? Row.Name
        : Row.Name + "\n\n" + string.Join("\n", Row.Findings.Select(f => $"• {Describe(f.Issue)}: {f.Detail}"));

    [ObservableProperty] private bool _isSelected;

    /// <summary>Short human labels. Deliberately plain: "not cooked" beats "UncookedPermutation" for
    /// someone scanning eighty rows.</summary>
    public static string Describe(MaterialIssue issue) => issue switch
    {
        MaterialIssue.MissingMaterial => "missing",
        MaterialIssue.NoShader => "no shader",
        MaterialIssue.UnresolvedTexture => "texture not found",
        MaterialIssue.UncookedPermutation => "not cooked",
        MaterialIssue.InertMacro => "macro ignored",
        MaterialIssue.NonNeutralTint => "tint",
        MaterialIssue.NoTextures => "no textures",
        MaterialIssue.Unused => "unused",
        _ => issue.ToString(),
    };
}

/// <summary>What the browser needs from the app. Supplied by the main view model so this type never
/// reaches into project or asset state itself.</summary>
public sealed record MaterialBrowserContext(
    string MapName,
    IReadOnlyList<MaterialAuditRow> Rows,
    Action<string>? OpenInEditor = null,
    Action<IReadOnlyList<string>>? SelectMeshesUsing = null,
    Action? Refresh = null);

/// <summary>
/// M503b: every material in the open map, with what uses it and what is wrong with it.
///
/// <para>Replaces hunting through a per-material property grid. The question it exists to answer is "which
/// of my 82 materials is wrong", which on a ported map is the difference between a five-minute fix and an
/// afternoon — see M486 through M502, every one of which started as a material that looked fine in
/// isolation.</para>
///
/// <para>Multi-select is load-bearing beyond this milestone: it is the selection M504's bulk apply will
/// act on.</para>
/// </summary>
public sealed partial class MaterialBrowserViewModel : ObservableObject
{
    private MaterialBrowserContext? _context;
    private readonly List<MaterialRowViewModel> _all = new();

    public ObservableCollection<MaterialRowViewModel> Rows { get; } = new();

    [ObservableProperty] private string _mapName = "No map open";
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _onlyIssues;
    [ObservableProperty] private bool _onlyUsed;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private MaterialRowViewModel? _selectedRow;

    /// <summary>Sort choices, in the order a person actually wants them: worst first, then biggest.</summary>
    public IReadOnlyList<string> SortModes { get; } = new[]
    {
        "Problems first", "Most meshes", "Name", "Shader",
    };
    [ObservableProperty] private int _sortMode;

    partial void OnFilterChanged(string value) => ApplyFilter();
    partial void OnOnlyIssuesChanged(bool value) => ApplyFilter();
    partial void OnOnlyUsedChanged(bool value) => ApplyFilter();
    partial void OnSortModeChanged(int value) => ApplyFilter();

    public void Load(MaterialBrowserContext context)
    {
        _context = context;
        MapName = context.MapName;
        _all.Clear();
        foreach (var row in context.Rows) _all.Add(new MaterialRowViewModel(row));
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string needle = Filter.Trim();
        IEnumerable<MaterialRowViewModel> q = _all;

        if (needle.Length > 0)
            q = q.Where(r =>
                r.FullName.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || r.Shader.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || r.Row.Textures.Any(t => t.Contains(needle, StringComparison.OrdinalIgnoreCase))
                // Searching the FINDINGS is the point of the box: "tint" or "cooked" should pull up
                // exactly the rows that have that problem.
                || r.Row.Findings.Any(f => MaterialRowViewModel.Describe(f.Issue)
                                            .Contains(needle, StringComparison.OrdinalIgnoreCase)
                                        || f.Detail.Contains(needle, StringComparison.OrdinalIgnoreCase)));

        if (OnlyIssues) q = q.Where(r => r.HasIssues);
        if (OnlyUsed) q = q.Where(r => r.MeshCount > 0);

        q = SortMode switch
        {
            1 => q.OrderByDescending(r => r.MeshCount).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
            2 => q.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
            3 => q.OrderBy(r => r.Shader, StringComparer.OrdinalIgnoreCase)
                  .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
            // Worst issue first, then the ones affecting the most geometry — the order you would triage in.
            _ => q.OrderBy(r => r.Row.Worst ?? (MaterialIssue)999)
                  .ThenByDescending(r => r.MeshCount)
                  .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        };

        Rows.Clear();
        foreach (var r in q) Rows.Add(r);

        int issues = _all.Count(r => r.HasIssues);
        Summary = _all.Count == 0
            ? "No materials."
            : $"{Rows.Count:n0} shown of {_all.Count:n0} · {issues:n0} with findings · "
            + $"{_all.Sum(r => r.MeshCount):n0} mesh uses";
    }

    /// <summary>Every checked row, or the highlighted one when nothing is checked — the selection M504's
    /// bulk apply will act on.</summary>
    public IReadOnlyList<MaterialAuditRow> Selection
    {
        get
        {
            var ticked = _all.Where(r => r.IsSelected).Select(r => r.Row).ToList();
            if (ticked.Count > 0) return ticked;
            return SelectedRow is { } one ? new[] { one.Row } : Array.Empty<MaterialAuditRow>();
        }
    }

    [RelayCommand]
    private void OpenInEditor()
    {
        if (SelectedRow is { } row) _context?.OpenInEditor?.Invoke(row.FullName);
    }

    /// <summary>Select the meshes that use the chosen materials, so "this one is wrong" becomes "and here
    /// it is in the viewport".</summary>
    [RelayCommand]
    private void SelectMeshes()
    {
        var names = Selection.Where(r => r.MeshCount > 0).Select(r => r.Name).ToList();
        if (names.Count > 0) _context?.SelectMeshesUsing?.Invoke(names);
    }

    [RelayCommand]
    private void Reload() => _context?.Refresh?.Invoke();

    [RelayCommand]
    private void SelectAllShown()
    {
        foreach (var r in Rows) r.IsSelected = true;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var r in _all) r.IsSelected = false;
    }
}
