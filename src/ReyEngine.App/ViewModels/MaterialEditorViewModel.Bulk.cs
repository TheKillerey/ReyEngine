using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core.Undo;
using ReyEngine.Formats.Materials;

namespace ReyEngine.App.ViewModels;

/// <summary>M645: one text field of the bulk inspector - a sampler path or a parameter value shared across
/// the picked materials.</summary>
public sealed partial class BulkFieldViewModel : ObservableObject
{
    private readonly MaterialEditorViewModel _owner;
    private string _lastShared = "";

    public BulkFieldViewModel(MaterialEditorViewModel owner, string name, bool isSampler)
    {
        _owner = owner; Name = name; IsSampler = isSampler;
    }

    public string Name { get; }
    public bool IsSampler { get; }

    /// <summary>The shared value, or "— mixed —" - shown as the box's watermark so what is typed is the edit.</summary>
    [ObservableProperty] private string _valueLabel = "";
    /// <summary>"on 3 of 5" when not every picked material has the field; empty when all do.</summary>
    [ObservableProperty] private string _presence = "";
    [ObservableProperty] private bool _mixed;
    [ObservableProperty] private string _editedText = "";
    [ObservableProperty] private string _error = "";
    public bool HasError => Error.Length > 0;
    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    internal void Update(MaterialBulkEdit.Field field)
    {
        Mixed = field.Mixed;
        ValueLabel = field.Mixed ? "— mixed —" : field.Shared ?? "";
        Presence = field.OnAll ? "" : $"on {field.PresentOn} of {field.Of}";
        // Follow the shared value unless the user has typed something else since.
        string shared = field.Shared ?? "";
        if (EditedText == _lastShared || EditedText.Length == 0) EditedText = shared;
        _lastShared = shared;
    }

    [RelayCommand] private void Apply() => _owner.ApplyBulkField(this);
}

/// <summary>M645: one boolean field of the bulk inspector - a feature switch or a shader macro.</summary>
public sealed partial class BulkToggleViewModel : ObservableObject
{
    private readonly MaterialEditorViewModel _owner;

    public BulkToggleViewModel(MaterialEditorViewModel owner, string name, bool isMacro)
    {
        _owner = owner; Name = name; IsMacro = isMacro;
    }

    public string Name { get; }
    public bool IsMacro { get; }
    [ObservableProperty] private string _stateLabel = "";

    internal void Update(MaterialBulkEdit.Toggle toggle)
    {
        string state = toggle.Mixed ? "— mixed —" : toggle.Shared == true ? "on" : "off";
        StateLabel = toggle.OnAll ? state : $"{state} · on {toggle.PresentOn} of {toggle.Of}";
    }

    [RelayCommand] private void TurnOn() => _owner.ApplyBulkToggle(this, true);
    [RelayCommand] private void TurnOff() => _owner.ApplyBulkToggle(this, false);
}

/// <summary>
/// M645: bulk editing of materials - the shared inspector for two or more picked materials.
///
/// <para>The editor had one selector and one detail pane, so changing a sampler on twelve materials was
/// twelve edits, and M335's shader replace was the one bulk verb and it acted on the WHOLE file. Now a
/// picker under the selector takes any number of the listed materials; two or more turn the detail pane
/// into this: every sampler, parameter, switch and macro the picked materials have, each showing the
/// value they agree on or "mixed", and each edit writing every picked material that has the field as one
/// undo step. Values, not structure: a material without the field is left alone. The arithmetic and the
/// commands are <see cref="MaterialBulkEdit"/> in Formats; this is the rows and the wiring. It lives in
/// the shared control, so the map inspector and the character window both get it.</para>
/// </summary>
public sealed partial class MaterialEditorViewModel
{
    public MaterialEditorViewModel()
    {
        BulkSelection.CollectionChanged += (_, _) => RefreshBulk();
    }

    /// <summary>The picked materials - the multi-select list's SelectedItems.</summary>
    public ObservableCollection<MaterialBindingViewModel> BulkSelection { get; } = new();

    /// <summary>Two or more picked: the bulk inspector shows and the single detail pane hides.</summary>
    [ObservableProperty] private bool _isBulk;
    [ObservableProperty] private bool _showBulkPicker;
    [ObservableProperty] private string _bulkPickerHeader = "SELECT SEVERAL";
    [ObservableProperty] private string _bulkStatus = "";

    [ObservableProperty] private bool _bulkCanChangeShader;
    [ObservableProperty] private string _bulkShaderLabel = "";
    [ObservableProperty] private string _bulkShader = "";
    [ObservableProperty] private string _bulkPickShaderStatus = "";   // BulkShaderStatus is M335's whole-file replace

    public ObservableCollection<BulkFieldViewModel> BulkSamplers { get; } = new();
    public ObservableCollection<BulkFieldViewModel> BulkParameters { get; } = new();
    public ObservableCollection<BulkToggleViewModel> BulkSwitches { get; } = new();
    public ObservableCollection<BulkToggleViewModel> BulkMacros { get; } = new();
    public bool HasBulkSwitches => BulkSwitches.Count > 0;
    public bool HasBulkMacros => BulkMacros.Count > 0;

    [ObservableProperty] private bool _bulkCanEditRenderState;
    [ObservableProperty] private string _bulkCullLabel = "";
    [ObservableProperty] private string _bulkBlendLabel = "";
    [ObservableProperty] private string _bulkSrcLabel = "";
    [ObservableProperty] private string _bulkDstLabel = "";
    /// <summary>Index into <see cref="BulkBlendChoices"/>: 0 = keep (the resting state), 1 = absent, 2.. = factor 0..9.</summary>
    [ObservableProperty] private int _bulkSrcChoice;
    [ObservableProperty] private int _bulkDstChoice;
    public IReadOnlyList<string> BulkBlendChoices { get; } =
        new[] { "— keep as is —", "— not set (absent)" }.Concat(MaterialBindingViewModel.BlendFactorNames).ToArray();

    private bool _refreshingBulk;
    /// <summary>True while a bulk edit is being applied: the row syncs then leave the one NotifyChanged to
    /// CommitBulk. Outside it - undo and redo - each sync is the only notification there is.</summary>
    private bool _applyingBulk;

    private IReadOnlyList<MaterialBinding> BulkModels => BulkSelection.Select(v => v.Model).ToList();

    /// <summary>Re-derive the bulk inspector from the picked materials. Called when the pick changes, after
    /// every bulk edit, and after single edits (NotifyChanged) so the shared labels stay true.</summary>
    public void RefreshBulk()
    {
        if (_refreshingBulk) return;
        _refreshingBulk = true;
        try
        {
            for (int i = BulkSelection.Count - 1; i >= 0; i--)
                if (!Materials.Contains(BulkSelection[i])) BulkSelection.RemoveAt(i);

            IsBulk = BulkSelection.Count >= 2;
            BulkPickerHeader = BulkSelection.Count == 0 ? "SELECT SEVERAL" : $"SELECT SEVERAL — {BulkSelection.Count} picked";
            if (!IsBulk)
            {
                BulkStatus = ""; BulkSamplers.Clear(); BulkParameters.Clear(); BulkSwitches.Clear(); BulkMacros.Clear();
                OnPropertyChanged(nameof(HasBulkSwitches)); OnPropertyChanged(nameof(HasBulkMacros));
                return;
            }

            var summary = MaterialBulkEdit.Summarize(BulkModels);
            BulkStatus = $"{summary.Count} materials picked" + (summary.Dirty > 0 ? $" · {summary.Dirty} with unsaved edits" : "");
            BulkCanChangeShader = summary.CanChangeShader > 0;
            BulkShaderLabel = summary.ShaderMixed ? "— mixed —" : summary.Shader ?? "";
            if (BulkShader.Length == 0 || !summary.ShaderMixed) BulkShader = summary.ShaderMixed ? BulkShader : summary.Shader ?? "";

            SyncFields(BulkSamplers, summary.Samplers, isSampler: true);
            SyncFields(BulkParameters, summary.Parameters, isSampler: false);
            SyncToggles(BulkSwitches, summary.Switches, isMacro: false);
            SyncToggles(BulkMacros, summary.Macros, isMacro: true);
            OnPropertyChanged(nameof(HasBulkSwitches)); OnPropertyChanged(nameof(HasBulkMacros));

            BulkCanEditRenderState = summary.CanEditRenderState > 0;
            BulkCullLabel = Label(summary.Cull, "culling back faces", "two-sided", summary.CanEditRenderState, summary.Count);
            BulkBlendLabel = Label(summary.Blend, "alpha blending on", "opaque", summary.CanEditRenderState, summary.Count);
            BulkSrcLabel = FactorLabel(summary.SrcBlend);
            BulkDstLabel = FactorLabel(summary.DstBlend);
            BulkSrcChoice = 0; BulkDstChoice = 0;
        }
        finally { _refreshingBulk = false; }
    }

    private static string Label(bool? shared, string on, string off, int stateful, int of)
    {
        string state = shared is null ? "— mixed —" : shared.Value ? on : off;
        return stateful == of ? state : $"{state} · {stateful} of {of} have a pass";
    }

    private static string FactorLabel(int? shared) => shared switch
    {
        null => "— mixed —",
        < 0 => "not set (absent)",
        int f when f < MaterialBindingViewModel.BlendFactorNames.Count => MaterialBindingViewModel.BlendFactorNames[f],
        int f => f.ToString(),
    };

    private void SyncFields(ObservableCollection<BulkFieldViewModel> rows, IReadOnlyList<MaterialBulkEdit.Field> fields, bool isSampler)
    {
        for (int i = rows.Count - 1; i >= 0; i--)
            if (!fields.Any(f => f.Name.Equals(rows[i].Name, StringComparison.OrdinalIgnoreCase))) rows.RemoveAt(i);
        for (int i = 0; i < fields.Count; i++)
        {
            var row = rows.FirstOrDefault(r => r.Name.Equals(fields[i].Name, StringComparison.OrdinalIgnoreCase));
            if (row is null) { row = new BulkFieldViewModel(this, fields[i].Name, isSampler); rows.Insert(Math.Min(i, rows.Count), row); }
            row.Update(fields[i]);
        }
    }

    private void SyncToggles(ObservableCollection<BulkToggleViewModel> rows, IReadOnlyList<MaterialBulkEdit.Toggle> toggles, bool isMacro)
    {
        for (int i = rows.Count - 1; i >= 0; i--)
            if (!toggles.Any(t => t.Name.Equals(rows[i].Name, StringComparison.OrdinalIgnoreCase))) rows.RemoveAt(i);
        for (int i = 0; i < toggles.Count; i++)
        {
            var row = rows.FirstOrDefault(r => r.Name.Equals(toggles[i].Name, StringComparison.OrdinalIgnoreCase));
            if (row is null) { row = new BulkToggleViewModel(this, toggles[i].Name, isMacro); rows.Insert(Math.Min(i, rows.Count), row); }
            row.Update(toggles[i]);
        }
    }

    // ===================================================== picking

    [RelayCommand]
    private void SelectAllShown()
    {
        _refreshingBulk = true;
        try { foreach (var m in FilteredMaterials) if (!BulkSelection.Contains(m)) BulkSelection.Add(m); }
        finally { _refreshingBulk = false; }
        ShowBulkPicker = true;
        RefreshBulk();
    }

    [RelayCommand] private void ClearBulk() => BulkSelection.Clear();

    [RelayCommand]
    private void RevertBulk()
    {
        foreach (var vm in BulkSelection.ToList()) vm.RevertCommand.Execute(null);
        NotifyChanged();
        RefreshBulk();
        BulkStatus = "Picked materials reverted to what the file authored.";
    }

    // ===================================================== the edits

    /// <summary>Row sync after a slot changed under the bulk editor or its undo.</summary>
    private void SyncSlotRow(TextureSlot slot)
    {
        foreach (var m in Materials)
            foreach (var s in m.Slots)
                if (ReferenceEquals(s.Model, slot)) { s.ResetFromModel(); m.RaiseDirty(); }
        if (!_applyingBulk) NotifyChanged();
    }

    private void SyncParameterRow(MaterialParameter parameter)
    {
        foreach (var m in Materials)
            foreach (var p in m.Parameters)
                if (ReferenceEquals(p.Model, parameter)) { p.ResetFromModel(); m.RaiseDirty(); }
        if (!_applyingBulk) NotifyChanged();
    }

    private void SyncTogglesFromModels()
    {
        foreach (var m in BulkSelection)
        {
            foreach (var sw in m.Switches) sw.SyncFromModel();
            foreach (var macro in m.Macros) macro.SyncFromModel();
            m.RaiseDirty();
        }
        if (!_applyingBulk) NotifyChanged();
    }

    private void SyncRenderStateFromModels()
    {
        foreach (var m in BulkSelection)
        {
            m.LoadRenderState();
            Reclassify(m);
            m.RaiseRenderStateText();
            m.RaiseDirty();
        }
        if (!_applyingBulk) NotifyChanged();
    }

    /// <summary>Record the step (when it changed anything), and let every listener know: dirty state, the
    /// material ball, the live preview, auto-save - the same path a single edit takes.</summary>
    private void CommitBulk(CompositeCommand command)
    {
        if (command.Count > 0) UndoService?.PushApplied(command);
        NotifyChanged();
        RefreshBulk();
        BulkStatus = command.Count > 0
            ? $"{command.Name}: {command.Count} changed. One undo step."
            : "Nothing changed - every picked material already had that value.";
    }

    internal void ApplyBulkField(BulkFieldViewModel row)
    {
        var models = BulkModels;
        if (models.Count < 2) return;
        _applyingBulk = true;
        try
        {
            var command = row.IsSampler
                ? MaterialBulkEdit.SetSamplerPath(models, row.Name, row.EditedText.Trim(), DocContext, SyncSlotRow)
                : MaterialBulkEdit.SetParameter(models, row.Name, row.EditedText.Trim(), DocContext, SyncParameterRow);
            row.Error = "";
            CommitBulk(command);
        }
        catch (Exception ex)
        {
            row.Error = ex.Message;
            Warn?.Invoke($"Bulk {row.Name}: {ex.Message}");
            NotifyChanged();   // the rolled-back rows, and the dirty state, are current again
        }
        finally { _applyingBulk = false; }
    }

    internal void ApplyBulkToggle(BulkToggleViewModel row, bool on)
    {
        var models = BulkModels;
        if (models.Count < 2) return;
        _applyingBulk = true;
        try
        {
            var command = row.IsMacro
                ? MaterialBulkEdit.SetMacro(models, row.Name, on, DocContext, SyncTogglesFromModels)
                : MaterialBulkEdit.SetSwitch(models, row.Name, on, DocContext, SyncTogglesFromModels);
            CommitBulk(command);
        }
        finally { _applyingBulk = false; }
    }

    private void ApplyBulk(Func<CompositeCommand> edit)
    {
        if (!IsBulk) return;
        _applyingBulk = true;
        try { CommitBulk(edit()); }
        finally { _applyingBulk = false; }
    }

    [RelayCommand]
    private void SetBulkCull(string on) =>
        ApplyBulk(() => MaterialBulkEdit.SetPassBool(BulkModels, "cullEnable", on == "on", whenAbsent: true, DocContext, SyncRenderStateFromModels));

    [RelayCommand]
    private void SetBulkBlend(string on) =>
        ApplyBulk(() => MaterialBulkEdit.SetPassBool(BulkModels, "blendEnable", on == "on", whenAbsent: false, DocContext, SyncRenderStateFromModels));

    partial void OnBulkSrcChoiceChanged(int value)
    {
        if (_refreshingBulk || value <= 0) return;
        ApplyBulk(() => MaterialBulkEdit.SetBlendFactor(BulkModels, "src", value - 2, DocContext, SyncRenderStateFromModels));
    }

    partial void OnBulkDstChoiceChanged(int value)
    {
        if (_refreshingBulk || value <= 0) return;
        ApplyBulk(() => MaterialBulkEdit.SetBlendFactor(BulkModels, "dst", value - 2, DocContext, SyncRenderStateFromModels));
    }

    /// <summary>The shader on every picked material that has a technique pass, through the same per-material
    /// path the single row uses (so the catalogue's sampler slots are added the same way), recorded as one
    /// step with M335's command.</summary>
    [RelayCommand]
    private void ApplyBulkShader()
    {
        string shader = BulkShader.Trim();
        if (shader.Length == 0 || !IsBulk) return;
        var entries = new List<BulkMaterialShaderCommand.Entry>();
        foreach (var vm in BulkSelection.Where(v => v.Model.CanChangeShader).ToList())
        {
            string source = vm.Model.RenderShader ?? "";
            if (source.Equals(shader, StringComparison.OrdinalIgnoreCase)) continue;
            var before = vm.Model.Slots.ToHashSet(ReferenceEqualityComparer.Instance);
            ChangeShader(vm, shader);
            var added = vm.Model.Slots.Where(s => !before.Contains(s)).ToList();
            entries.Add(new BulkMaterialShaderCommand.Entry(vm.Model, source, shader, added));
        }

        void Refresh()
        {
            foreach (var vm in BulkSelection) vm.SynchronizeConvertedMaterial(Catalog);
            BuildShaderIndex();
            RefreshShaderDefs();
            NotifyChanged();
            RefreshBulk();
        }
        if (entries.Count > 0) UndoService?.PushApplied(new BulkMaterialShaderCommand(entries, DocContext, Refresh));
        Refresh();
        BulkPickShaderStatus = entries.Count > 0
            ? $"Shader set on {entries.Count} material(s). One undo step."
            : "Every picked material already uses that shader, or has no technique pass to change.";
    }
}
