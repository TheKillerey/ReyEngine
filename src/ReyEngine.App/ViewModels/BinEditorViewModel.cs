using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Undo;
using ReyEngine.Formats.Meta;

namespace ReyEngine.App.ViewModels;

public sealed partial class EditableBinFieldViewModel : ViewModelBase
{
    private readonly BinEditorViewModel _owner;
    public EditableBinField Model { get; }
    public ObservableCollection<EditableBinFieldViewModel> Children { get; } = new();

    [ObservableProperty] private string _editedText;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private bool _isVisible = true;

    private string _lastApplied;   // the text currently applied to the live property (for undo capture)

    public EditableBinFieldViewModel(EditableBinField model, BinEditorViewModel owner)
    {
        Model = model;
        _owner = owner;
        _editedText = model.OriginalText;
        _lastApplied = model.OriginalText;
        foreach (var c in model.Children) Children.Add(new EditableBinFieldViewModel(c, owner));
    }

    public string Name => Model.Name;
    public string TypeName => Model.TypeName;
    public bool IsBranch => Model.IsBranch;
    public bool IsEditable => Model.IsEditable;
    public bool IsReadOnlyValue => !Model.IsBranch && !Model.IsEditable;
    public string OriginalText => Model.OriginalText;
    public string HashHex => $"0x{Model.NameHash:x8}";
    public string PathLabel => Model.PathLabel;

    [RelayCommand]
    private void Apply()
    {
        if (!IsEditable) return;
        try
        {
            var oldText = _lastApplied;
            Model.Apply(EditedText);   // throws on invalid input — nothing is pushed then
            HasError = false; ErrorText = "";
            if (!string.Equals(EditedText, oldText, StringComparison.Ordinal))
                _owner.UndoService?.PushApplied(new BinEditCommand(_owner.DocContext, Model, oldText, EditedText, SyncFromCommand));
            _lastApplied = EditedText;
            IsDirty = !string.Equals(EditedText, Model.OriginalText, StringComparison.Ordinal);
            _owner.NotifyChanged();
        }
        catch (Exception ex) { HasError = true; ErrorText = ex.Message; }
    }

    /// <summary>Called by undo/redo after the command re-applied a value — sync the row UI.</summary>
    private void SyncFromCommand(string appliedText)
    {
        EditedText = appliedText;
        _lastApplied = appliedText;
        IsDirty = !string.Equals(appliedText, Model.OriginalText, StringComparison.Ordinal);
        HasError = false; ErrorText = "";
        _owner.NotifyChanged();
    }

    [RelayCommand]
    private void Revert()
    {
        if (!IsEditable) return;
        RevertSilently();
        _owner.NotifyChanged();
    }

    public void RevertSilently()
    {
        if (IsEditable && IsDirty)
        {
            try { Model.Apply(Model.OriginalText); } catch { }
        }
        EditedText = Model.OriginalText;
        _lastApplied = Model.OriginalText;
        IsDirty = false; HasError = false; ErrorText = "";
        foreach (var c in Children) c.RevertSilently();
    }

    public bool AnyDirty() => IsDirty || Children.Any(c => c.AnyDirty());

    public bool ApplyFilter(string filter)
    {
        bool self = string.IsNullOrEmpty(filter)
            || Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || TypeName.Contains(filter, StringComparison.OrdinalIgnoreCase);
        bool anyChild = false;
        foreach (var c in Children) anyChild |= c.ApplyFilter(filter);
        IsVisible = self || anyChild;
        return IsVisible;
    }

    [RelayCommand] private async Task CopyPath() => await _owner.Copy(PathLabel);
    [RelayCommand] private async Task CopyHash() => await _owner.Copy(HashHex);
    [RelayCommand] private async Task CopyValue() => await _owner.Copy(EditedText);
}

/// <summary>Editor state for one .bin: the editable field tree + dirty tracking + serialize.</summary>
public sealed partial class BinEditorViewModel : ViewModelBase
{
    private BinEditorDocument? _doc;

    public WadAssetEntry? Entry { get; private set; }
    public ObservableCollection<EditableBinFieldViewModel> Roots { get; } = new();

    [ObservableProperty] private bool _hasBin;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _summary = "";

    public Func<string, Task>? CopyHandler { get; set; }

    /// <summary>Global undo stack (set by MainWindowViewModel). Commands are tagged with the doc instance.</summary>
    public UndoRedoService? UndoService { get; set; }
    public object? DocContext => _doc;

    public void Load(BinEditorDocument doc, WadAssetEntry entry)
    {
        if (_doc is not null) UndoService?.PurgeContext(_doc); // stale commands must never mutate a replaced doc
        _doc = doc;
        Entry = entry;
        Roots.Clear();
        foreach (var r in doc.Roots) Roots.Add(new EditableBinFieldViewModel(r, this));
        HasBin = Roots.Count > 0;
        IsDirty = false;
        Filter = "";
        Summary = $"{doc.Roots.Count} object(s)" + (doc.Dependencies.Count > 0 ? $", {doc.Dependencies.Count} dependencies" : "");
    }

    public void Clear()
    {
        if (_doc is not null) UndoService?.PurgeContext(_doc);
        _doc = null; Entry = null;
        Roots.Clear();
        HasBin = false; IsDirty = false; Filter = ""; Summary = "";
    }

    public byte[]? Serialize() => _doc?.Serialize();

    public void NotifyChanged() => IsDirty = Roots.Any(r => r.AnyDirty());

    [RelayCommand]
    private void RevertFile()
    {
        foreach (var r in Roots) r.RevertSilently();
        NotifyChanged();
    }

    partial void OnFilterChanged(string value)
    {
        foreach (var r in Roots) r.ApplyFilter(value);
    }

    public Task Copy(string text) => CopyHandler?.Invoke(text) ?? Task.CompletedTask;
}
