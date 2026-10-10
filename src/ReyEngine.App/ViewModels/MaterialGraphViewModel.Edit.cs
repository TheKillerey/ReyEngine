using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.App.Services;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Materials.Graph;
using ReyEngine.Formats.Shaders;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M830: the editing half of the Material Graph view model.
///
/// <para><b>One document.</b> The graph is built from the <see cref="MaterialBinding"/> that the Material Editor holds
/// (not from a second parse of the bin), and an edit is applied by the editor's own row view model, so the editor's
/// undo stack, dirty flag, live preview, auto-save and Save are what the graph uses. The graph keeps no value of its
/// own: after any change the editor reports (<see cref="MaterialEditorViewModel.ModelChanged"/>: an edit made here, a
/// Material-tab edit, an undo from the main window) it rebuilds from the binding and refreshes the open editors.</para>
///
/// <para><b>Read-only is a state, with a reason</b> (<see cref="EditNote"/>): no editor behind the window, a shader
/// shown on its own, a linked-bin material, a material the editor does not hold, or an editor that has loaded another
/// file since.</para>
/// </summary>
public sealed partial class MaterialGraphViewModel
{
    private MaterialEditorViewModel? _editor;
    private MaterialBindingViewModel? _editBinding;
    private MaterialBinding? _binding;
    private ShaderCacheReader? _cache;
    private ShaderPermutationIndex? _perms;
    private MaterialGraphShaderInfo? _info;
    private string _shaderSig = "";
    private string? _rowsFor;
    private string? _boundBin;

    /// <summary>The nodes' editors for the selected node (empty when nothing there is editable).</summary>
    public ObservableCollection<GraphEditRow> EditRows { get; } = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ModeBadge)), NotifyPropertyChangedFor(nameof(SaveTip)), NotifyPropertyChangedFor(nameof(ApplyTip)),
     NotifyCanExecuteChangedFor(nameof(SaveCommand)), NotifyCanExecuteChangedFor(nameof(ApplyEditsCommand)),
     NotifyCanExecuteChangedFor(nameof(UndoCommand)), NotifyCanExecuteChangedFor(nameof(RedoCommand))]
    private bool _canEdit;

    // ---- M832: a Shader Graph takes over the toolbar (Apply / Save / Undo / Redo, Ctrl+Z / Ctrl+Y / Ctrl+S) while one is open ----
    private ShaderGraphViewModel? _sg;

    /// <summary>The Shader Graph editor the window owns (null until the window attaches it).</summary>
    public ShaderGraphViewModel? ShaderGraph => _sg;

    private bool SgActive => _sg is { IsOpen: true };

    /// <summary>The toolbar commands run when a material is editable OR a Shader Graph is open.</summary>
    public bool CanRun => CanEdit || SgActive;

    public void AttachShaderGraph(ShaderGraphViewModel sg)
    {
        _sg = sg;
        sg.StateChanged += OnShaderGraphState;
        sg.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ShaderGraphViewModel.Hlsl) or nameof(ShaderGraphViewModel.HlslNote))
            {
                OnPropertyChanged(nameof(ShaderCodeText));
                OnPropertyChanged(nameof(CodeNoteShown));
            }
            // the generated HLSL is shown next to the disassembly: a graph opening brings it up, closing goes back to the pixel listing
            if (e.PropertyName == nameof(ShaderGraphViewModel.IsOpen)) CodeStage = sg.IsOpen ? 2 : 0;
        };
    }

    private void OnShaderGraphState()
    {
        OnPropertyChanged(nameof(ShowDirty));
        OnPropertyChanged(nameof(SaveTip));
        OnPropertyChanged(nameof(ApplyTip));
        ApplyEditsCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(DirtyTip));
        if (SgActive) { EditStatus = _sg!.Status; _sgWasActive = true; }
        else if (_sgWasActive) { _sgWasActive = false; EditStatus = ""; }   // the last Shader Graph line does not outlive the graph
    }

    private bool _sgWasActive;

    /// <summary>The tooltip of the unsaved-edits marker: whose edits it counts.</summary>
    public string DirtyTip => SgActive
        ? "The Shader Graph has changes that are not saved yet. Save (Ctrl+S) writes it to the project."
        : "The Material Editor holds edits that are not saved yet - the same flag its Material tab shows. Closing this window keeps them pending in the editor.";

    /// <summary>Why the graph is read-only, or what editing it does. Always set.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SaveTip)), NotifyPropertyChangedFor(nameof(ApplyTip))]
    private string _editNote = NoEditorNote;

    /// <summary>The last thing Undo / Redo did or refused.</summary>
    [ObservableProperty] private string _editStatus = "";

    /// <summary>The Material Editor has unsaved edits (the same flag its Material tab shows).</summary>
    [ObservableProperty] private bool _isEditorDirty;

    public bool ShowDirty => SgActive ? _sg!.IsDirty : CanEdit && IsEditorDirty;
    partial void OnIsEditorDirtyChanged(bool value) => OnPropertyChanged(nameof(ShowDirty));
    partial void OnCanEditChanged(bool value) => OnPropertyChanged(nameof(ShowDirty));

    /// <summary>The canvas's corner label.</summary>
    public string ModeBadge => CanEdit ? "EDITABLE" : "READ ONLY";

    public string SaveTip => SgActive
        ? "Save the Shader Graph to the project (.reyengine/shadergraphs). Ctrl+S saves too. It is editor data: Build Package does not pack it."
        : CanEdit
        ? "Save the material file: the same command as the Material tab's Save To Override, so the project override, GameData and Copy To Project rules apply. Ctrl+S saves too."
        : EditNote;

    public string ApplyTip => SgActive
        ? "Compile the Shader Graph now and show it in the preview (it also recompiles by itself after each edit)."
        : CanEdit
        ? "Show the material in the main viewport now (the Material tab's Apply). Edits already reach it live."
        : EditNote;

    /// <summary>The binding the graph edits (the Material Editor's own), or null when it is read-only.</summary>
    public MaterialBinding? EditedBinding => CanEdit ? _binding : null;

    /// <summary>The editor the graph is attached to (null = read-only).</summary>
    public MaterialEditorViewModel? AttachedEditor => _editor;

    /// <summary>Raised after the graph rebuilt itself from an editor change - the preview can refresh.</summary>
    public event Action? Edited;

    // ============================================================================== session

    private void AttachEditor(MaterialBinding binding, MaterialEditorViewModel? editor, string readOnlyReason)
    {
        DetachEditor();
        if (editor is null) { SetReadOnly(readOnlyReason.Length > 0 ? readOnlyReason : NoEditorNote); return; }
        var vm = editor.Materials.FirstOrDefault(m => ReferenceEquals(m.Model, binding));
        if (vm is null)
        {
            SetReadOnly(readOnlyReason.Length > 0 ? readOnlyReason
                : "Read-only: this is not one of the materials the Material Editor holds, so an edit here would not reach its document.");
            return;
        }
        if (vm.IsLinked)
        {
            SetReadOnly($"Read-only: this material lives in the linked bin {vm.LinkedFromBin}, not in the file the Material Editor edits. "
                        + "Linked-bin materials are never written from here - open that bin to edit it.");
            return;
        }
        _editor = editor;
        _editBinding = vm;
        _boundBin = editor.BinEntry?.Path;
        editor.ModelChanged += OnEditorChanged;
        editor.PropertyChanged += OnEditorProperty;
        IsEditorDirty = editor.IsDirty;
        EditStatus = "";
        EditNote = $"Editing the Material Editor's copy of {LeafOf(binding.Name)}: the Material tab shows the same values, Undo (Ctrl+Z) takes an edit back and "
                   + "Save (Ctrl+S) writes the file the way the Material tab does.";
        CanEdit = true;
    }

    private void DetachEditor()
    {
        if (_editor is not null)
        {
            _editor.ModelChanged -= OnEditorChanged;
            _editor.PropertyChanged -= OnEditorProperty;
        }
        _editor = null;
        _editBinding = null;
        IsEditorDirty = false;
        CanEdit = false;
    }

    private void SetReadOnly(string reason)
    {
        CanEdit = false;
        EditNote = reason;
        EditStatus = "";
        EditRows.Clear();
        _rowsFor = null;
    }

    private void OnEditorProperty(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MaterialEditorViewModel.IsDirty) && _editor is not null) IsEditorDirty = _editor.IsDirty;
    }

    private bool _historyStep;

    private void OnEditorChanged()
    {
        if (_editor is null || _editBinding is null || _binding is null) return;
        if (!_historyStep) EditStatus = "";   // the line is about the last Undo / Redo; any other edit makes it stale
        if (!_editor.Materials.Contains(_editBinding))
        {
            // The editor loaded its file again. The SAME file (a refresh after a save, a repair): follow it to the material of
            // that name, keeping the selection. Another file, or none: what this graph holds is no longer the editor's document.
            if (string.Equals(_boundBin, _editor.BinEntry?.Path, StringComparison.OrdinalIgnoreCase)
                && _editor.Materials.FirstOrDefault(m => !m.IsLinked && m.Model.Name == _binding.Name) is { } again)
            {
                var editor = _editor;
                string? selected = SelectedNodeId;
                ShowMaterial(again.Model, _cache, _perms, editor);
                if (selected is not null && Graph?.Find(selected) is not null) SelectedNodeId = selected;
                EditStatus = "The Material Editor loaded this file again; the graph followed it.";
                Edited?.Invoke();
                return;
            }
            DetachEditor();
            SetReadOnly("Read-only: the Material Editor has loaded another file since this graph was opened. Open the material again with the "
                        + "Material Editor's Material Graph button.");
            if (Graph is not null) RefreshDetails();
            Edited?.Invoke();   // the window gives the row its saved binding back
            return;
        }
        IsEditorDirty = _editor.IsDirty;
        Refresh();
        Edited?.Invoke();
    }

    /// <summary>Rebuild the graph from the binding after a change, keeping the selection (and the canvas, which keeps its
    /// view for the same material). The shader is resolved again only when a switch, macro or shader changed.</summary>
    private void Refresh()
    {
        if (_binding is null || Graph is null) return;
        bool shaderChanged = ShaderSignature(_binding) != _shaderSig;
        DxbcShader? vs = null, ps = null;
        string note = "";
        MaterialGraph graph;
        if (shaderChanged) graph = BuildGraph(out vs, out ps, out note);
        else graph = MaterialGraphBuilder.Build(_binding, _info);

        Apply(graph, _binding, vs, ps, note, keepSelection: true, shaderChanged: shaderChanged);
    }

    // ============================================================================== details: the editors

    private void RefreshEditRows(GraphNode? node, bool keepRows)
    {
        string key = node?.Id ?? "(material)";
        var fresh = !CanEdit || _editBinding is null || _binding is null ? new List<GraphEditRow>() : BuildRows(node).ToList();
        // Node ids are positional: removing a sampler or undoing an add moves them. Rows (and the box being typed in) are reused only
        // while every one still wraps the very model object the node resolves to now.
        if (keepRows && key == _rowsFor && EditRows.Count > 0 && fresh.Count == EditRows.Count
            && fresh.Zip(EditRows).All(p => p.First.GetType() == p.Second.GetType() && Equals(p.First.Wrapped, p.Second.Wrapped)))
        {
            foreach (var r in EditRows) r.Refresh();
            return;
        }
        EditRows.Clear();
        _rowsFor = key;
        foreach (var r in fresh) EditRows.Add(r);
    }

    private static int IndexOf(string id, string prefix) =>
        id.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(id.AsSpan(prefix.Length), out int i) ? i : -1;

    private IEnumerable<GraphEditRow> BuildRows(GraphNode? node)
    {
        var vm = _editBinding!;
        var b = _binding!;
        if (node is null || node.Kind == GraphNodeKind.Output)
        {
            yield return new GraphRenderStateEditRow(vm);
            yield break;
        }

        switch (node.Kind)
        {
            case GraphNodeKind.Texture:
            {
                int i = IndexOf(node.Id, "tex:");
                var slot = i >= 0 && i < b.Slots.Count ? b.Slots[i] : null;
                var row = slot is null ? null : vm.Slots.FirstOrDefault(s => ReferenceEquals(s.Model, slot));
                if (row is not null) yield return new GraphTextureEditRow(row);
                else yield return new GraphNoteRow("Texture", node.State == GraphNodeState.ShaderDefault
                    ? "The material does not set this texture: it is the shader's own default. Add the sampler on the Material tab (Missing samplers) to give it a path."
                    : "This sampler has no editable row in the Material Editor.");
                break;
            }
            case GraphNodeKind.Scalar or GraphNodeKind.Vector or GraphNodeKind.Color:
            {
                int i = IndexOf(node.Id, "param:");
                var p = i >= 0 && i < b.Parameters.Count ? b.Parameters[i] : null;
                var row = p is null ? null : vm.Parameters.FirstOrDefault(s => ReferenceEquals(s.Model, p));
                if (row is null)
                    yield return new GraphNoteRow("Parameter", "This parameter is a submesh-name list: the Material tab edits it as a checklist, not here.");
                else if (!row.IsEditable)
                    yield return new GraphNoteRow(row.Name, "This parameter's type has no text form the editor can write (a matrix or a struct). It is shown, not edited.");
                else yield return new GraphParamEditRow(row, node.Kind == GraphNodeKind.Color);
                break;
            }
            case GraphNodeKind.Switch:
            {
                int i = IndexOf(node.Id, "sw:");
                var all = (b.CanEditSwitches ? b.AllSwitches : b.SwitchEntries).ToList();
                var sw = i >= 0 && i < all.Count ? all[i] : null;
                var row = sw is null ? null : vm.Switches.FirstOrDefault(s => ReferenceEquals(s.Model, sw));
                if (row is not null && b.CanEditSwitches) yield return new GraphSwitchEditRow(row);
                else yield return new GraphNoteRow("Switch", node.State == GraphNodeState.ShaderDefault
                    ? "The material does not author this switch, so the shader's default applies. Add it on the Material tab (Shader switches) to set it here."
                    : "This switch cannot be edited on this kind of material.");
                break;
            }
            case GraphNodeKind.Macro:
                yield return new GraphNoteRow("Define", "Shader macros are edited on the Material tab (each one checks that a compiled permutation exists for it).");
                break;
            case GraphNodeKind.Shader:
                yield return new GraphNoteRow("Shader", "The shader is chosen on the Material tab (it also adds the samplers the new shader declares).");
                break;
        }
    }

    /// <summary>Enter / focus lost in an editor box.</summary>
    public void CommitRow(GraphEditRow row) => row.Commit();

    /// <summary>Apply whatever is typed but not yet committed (a box commits on Enter or when it loses focus; a key binding fires before
    /// that). A row ignores an unchanged value, so this writes nothing when nothing was typed.</summary>
    public void CommitPending()
    {
        foreach (var r in EditRows.ToList()) r.Commit();
    }

    // ============================================================================== toolbar commands

    /// <summary>The Material tab's Apply: show the material in the main viewport now.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private void ApplyEdits()
    {
        if (SgActive) { _sg!.RequestCompile(immediate: true); return; }
        CommitPending(); _editor?.ApplyCommand.Execute(null);
    }

    /// <summary>The Material tab's Save To Override, unchanged: the editor's own save command, so the project file or override, the GameData declaration and the Copy To Project rules all apply.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Save()
    {
        if (SgActive) { _sg!.Commit(); await _sg.SaveAsync(); return; }
        if (_editor is null) return;
        CommitPending();
        await _editor.SaveCommand.ExecuteAsync(null);
        IsEditorDirty = _editor.IsDirty;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Undo()
    {
        if (SgActive) { _sg!.Undo(); return; }
        CommitPending();
        var us = _editor?.UndoService;
        if (us is null) { EditStatus = "There is no undo history to use."; return; }
        if (!us.CanUndo) { EditStatus = "Nothing to undo."; return; }
        if (!ReferenceEquals(us.UndoContext, _editor!.DocContext))
        {
            EditStatus = $"The last change on the undo stack ('{us.UndoName}') is not an edit of this material file - undo it from the main window (Edit menu).";
            return;
        }
        string name = us.UndoName ?? "";
        _historyStep = true;
        try { EditStatus = us.Undo() ? $"Undid: {name}" : $"Could not undo: {name}"; }
        finally { _historyStep = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Redo()
    {
        if (SgActive) { _sg!.Redo(); return; }
        CommitPending();
        var us = _editor?.UndoService;
        if (us is null) { EditStatus = "There is no undo history to use."; return; }
        if (!us.CanRedo) { EditStatus = "Nothing to redo."; return; }
        if (!ReferenceEquals(us.RedoContext, _editor!.DocContext))
        {
            EditStatus = $"The next redo ('{us.RedoName}') is not an edit of this material file - redo it from the main window (Edit menu).";
            return;
        }
        string name = us.RedoName ?? "";
        _historyStep = true;
        try { EditStatus = us.Redo() ? $"Redid: {name}" : $"Could not redo: {name}"; }
        finally { _historyStep = false; }
    }
}
