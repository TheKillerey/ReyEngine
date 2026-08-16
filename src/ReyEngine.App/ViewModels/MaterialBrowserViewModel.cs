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

/// <summary>
/// M504: what the browser needs to run presets. The app owns the bin, the shader cache and the save path,
/// so all three stay behind these delegates.
/// </summary>
/// <param name="Capture">(material name, preset name) -> a snapshot, or null if that material is gone.</param>
/// <param name="Run">
/// (preset, material names, parts, write) -> one row per material. The SAME call does the preview and the
/// edit, with writing switched off or on — see <see cref="MaterialPresetApplier"/> for why that matters.
/// </param>
public sealed record MaterialPresetService(
    MaterialPresetLibrary Library,
    Action Save,
    Func<string, string, MaterialPreset?> Capture,
    Func<MaterialPreset, IReadOnlyList<string>, MaterialPresetParts, bool,
        IReadOnlyList<MaterialPresetPlanRow>> Run);

/// <summary>What the browser needs from the app. Supplied by the main view model so this type never
/// reaches into project or asset state itself.</summary>
public sealed record MaterialBrowserContext(
    string MapName,
    IReadOnlyList<MaterialAuditRow> Rows,
    Action<string>? OpenInEditor = null,
    Action<IReadOnlyList<string>>? SelectMeshesUsing = null,
    Action? Refresh = null,
    MaterialPresetService? Presets = null);

/// <summary>One material's line in the diff preview.</summary>
public sealed class MaterialPlanRowViewModel
{
    public MaterialPlanRowViewModel(MaterialPresetPlanRow row) { Row = row; }

    public MaterialPresetPlanRow Row { get; }

    public string Name { get { int i = Row.MaterialName.LastIndexOf('/'); return i >= 0 ? Row.MaterialName[(i + 1)..] : Row.MaterialName; } }
    public string Summary => Row.Summary;
    public bool HasBlockers => Row.HasBlockers;
    public bool HasChanges => Row.HasChanges;

    /// <summary>Changes, refusals and left-alones in one block — the refusals first, because a bulk edit
    /// that silently did less than asked is the failure this window exists to prevent.</summary>
    public string Detail => string.Join("\n",
        Row.Blockers.Select(b => "REFUSED  " + b)
           .Concat(Row.Changes.Select(c => "   " + c))
           .Concat(Row.Notes.Select(n => "note     " + n)));
}

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
        foreach (var row in context.Rows)
        {
            var vm = new MaterialRowViewModel(row);
            // Ticking a row changes what Apply would act on, so the preview it was computed from is no
            // longer the preview of what will happen. Drop it rather than let a stale diff be applied.
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MaterialRowViewModel.IsSelected)) ClearPreview();
            };
            _all.Add(vm);
        }
        ApplyFilter();
        LoadPresets();
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

    // ---- M504: presets + bulk apply ---------------------------------------------------------------

    public ObservableCollection<MaterialPreset> Presets { get; } = new();
    public ObservableCollection<MaterialPlanRowViewModel> Preview { get; } = new();

    [ObservableProperty] private MaterialPreset? _selectedPreset;
    [ObservableProperty] private string _newPresetName = "";
    [ObservableProperty] private string _presetStatus = "";

    [ObservableProperty] private bool _partShader;
    [ObservableProperty] private bool _partSamplers;
    [ObservableProperty] private bool _partParameters;
    [ObservableProperty] private bool _partSwitches;
    [ObservableProperty] private bool _partMacros;
    [ObservableProperty] private bool _partRenderState;

    public bool PresetsAvailable => _context?.Presets is not null;

    /// <summary>A part can only be ticked when the chosen preset has something to say about it — an empty
    /// part that silently does nothing is indistinguishable from one that failed.</summary>
    public bool CanPickShader => Offers(MaterialPresetParts.Shader);
    public bool CanPickSamplers => Offers(MaterialPresetParts.Samplers);
    public bool CanPickParameters => Offers(MaterialPresetParts.Parameters);
    public bool CanPickSwitches => Offers(MaterialPresetParts.Switches);
    public bool CanPickMacros => Offers(MaterialPresetParts.Macros);
    public bool CanPickRenderState => Offers(MaterialPresetParts.RenderState);

    private bool Offers(MaterialPresetParts part) => SelectedPreset?.AvailableParts.HasFlag(part) == true;

    public MaterialPresetParts ChosenParts =>
        (PartShader ? MaterialPresetParts.Shader : 0)
        | (PartSamplers ? MaterialPresetParts.Samplers : 0)
        | (PartParameters ? MaterialPresetParts.Parameters : 0)
        | (PartSwitches ? MaterialPresetParts.Switches : 0)
        | (PartMacros ? MaterialPresetParts.Macros : 0)
        | (PartRenderState ? MaterialPresetParts.RenderState : 0);

    /// <summary>Apply is only ever reachable through a preview — the diff on screen must be the diff that
    /// gets written, and any change to the preset, the parts or the ticked rows invalidates it.</summary>
    public bool CanApplyPreset => Preview.Count > 0 && Preview.Any(r => r.HasChanges);

    public string PresetDetail => SelectedPreset is { } p
        ? p.Summary + (p.SourceMaterial is { } s ? $"\ncaptured from {s}" : "")
        : "No preset selected.";

    partial void OnSelectedPresetChanged(MaterialPreset? value)
    {
        ClearPreview();
        // Default to what the preset offers, MINUS the shader and the macros. Those two are the ones that
        // decide which compiled permutation the client looks up, and getting that wrong is the M482/M486
        // class of failure — a map that loads and renders nothing. Copying them stays a deliberate act.
        PartShader = false;
        PartMacros = false;
        PartSamplers = CanPickSamplers;
        PartParameters = CanPickParameters;
        PartSwitches = CanPickSwitches;
        PartRenderState = CanPickRenderState;
        OnPropertyChanged(nameof(CanPickShader));
        OnPropertyChanged(nameof(CanPickSamplers));
        OnPropertyChanged(nameof(CanPickParameters));
        OnPropertyChanged(nameof(CanPickSwitches));
        OnPropertyChanged(nameof(CanPickMacros));
        OnPropertyChanged(nameof(CanPickRenderState));
        OnPropertyChanged(nameof(PresetDetail));
    }

    partial void OnPartShaderChanged(bool value) => ClearPreview();
    partial void OnPartSamplersChanged(bool value) => ClearPreview();
    partial void OnPartParametersChanged(bool value) => ClearPreview();
    partial void OnPartSwitchesChanged(bool value) => ClearPreview();
    partial void OnPartMacrosChanged(bool value) => ClearPreview();
    partial void OnPartRenderStateChanged(bool value) => ClearPreview();
    partial void OnSelectedRowChanged(MaterialRowViewModel? value) => ClearPreview();

    private void ClearPreview()
    {
        if (Preview.Count == 0) return;
        Preview.Clear();
        OnPropertyChanged(nameof(CanApplyPreset));
    }

    private void LoadPresets()
    {
        Presets.Clear();
        if (_context?.Presets is not { } service) { OnPropertyChanged(nameof(PresetsAvailable)); return; }
        foreach (var p in service.Library.Presets.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            Presets.Add(p);
        OnPropertyChanged(nameof(PresetsAvailable));
        PresetStatus = Presets.Count == 0
            ? "No presets yet — highlight a material that looks right and capture it."
            : $"{Presets.Count} preset(s).";
    }

    /// <summary>Snapshot the highlighted material. The HIGHLIGHTED one, not the ticked ones: a preset has
    /// exactly one source, and taking it from a multi-selection would have to pick one silently.</summary>
    [RelayCommand]
    private void CapturePreset()
    {
        if (_context?.Presets is not { } service) return;
        if (SelectedRow is not { } row)
        { PresetStatus = "Highlight the material to capture first."; return; }

        string name = NewPresetName.Trim();
        if (name.Length == 0) name = row.Name;

        var preset = service.Capture(row.FullName, name);
        if (preset is null)
        { PresetStatus = $"Could not read '{row.Name}' from the bin."; return; }

        service.Library.Put(preset);
        service.Save();
        LoadPresets();
        SelectedPreset = Presets.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        NewPresetName = "";
        PresetStatus = $"Captured '{name}' from {row.Name}.";
    }

    [RelayCommand]
    private void DeletePreset()
    {
        if (_context?.Presets is not { } service || SelectedPreset is not { } preset) return;
        service.Library.Remove(preset.Name);
        service.Save();
        SelectedPreset = null;
        LoadPresets();
        PresetStatus = $"Deleted '{preset.Name}'.";
    }

    [RelayCommand]
    private void PreviewPreset()
    {
        Preview.Clear();
        OnPropertyChanged(nameof(CanApplyPreset));
        if (_context?.Presets is not { } service || SelectedPreset is not { } preset) return;

        var names = Selection.Select(r => r.Name).ToList();
        if (names.Count == 0) { PresetStatus = "Tick the materials to change first."; return; }
        if (ChosenParts == MaterialPresetParts.None) { PresetStatus = "Pick at least one part to copy."; return; }

        var rows = service.Run(preset, names, ChosenParts, false);
        foreach (var r in rows) Preview.Add(new MaterialPlanRowViewModel(r));
        OnPropertyChanged(nameof(CanApplyPreset));

        int changing = rows.Count(r => r.HasChanges);
        int refused = rows.Count(r => r.HasBlockers);
        PresetStatus = changing == 0
            ? $"Nothing to change on {names.Count:n0} material(s)."
            : $"{changing:n0} of {names.Count:n0} would change"
              + (refused > 0 ? $", {refused:n0} with refusals" : "") + ".";
    }

    [RelayCommand]
    private void ApplyPreset()
    {
        if (_context?.Presets is not { } service || SelectedPreset is not { } preset) return;
        if (!CanApplyPreset) { PresetStatus = "Preview the changes first."; return; }

        var names = Preview.Select(r => r.Row.MaterialName).ToList();
        var rows = service.Run(preset, names, ChosenParts, true);

        Preview.Clear();
        foreach (var r in rows) Preview.Add(new MaterialPlanRowViewModel(r));
        OnPropertyChanged(nameof(CanApplyPreset));

        int changed = rows.Count(r => r.HasChanges);
        int refused = rows.Count(r => r.HasBlockers);
        // Deliberately NOT auto-reloading: Reload re-runs Load(), which would wipe the report below before
        // it has been read. The list above is stale until then, and saying so is better than hiding it.
        PresetStatus = $"Applied to {changed:n0} material(s)"
            + (refused > 0 ? $", {refused:n0} with refusals" : "") + "."
            + (changed > 0 ? " Hit ↻ Reload to re-run the checks." : "");
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
