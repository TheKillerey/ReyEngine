using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core.Assets;
using ReyEngine.Formats.Materials;

namespace ReyEngine.App.ViewModels;

public sealed partial class TextureSlotViewModel : ViewModelBase
{
    private readonly MaterialEditorViewModel _owner;
    public TextureSlot Model { get; }

    [ObservableProperty] private string _editedPath;
    [ObservableProperty] private bool _unresolved;
    [ObservableProperty] private Bitmap? _thumbnail;

    public TextureSlotViewModel(TextureSlot model, MaterialEditorViewModel owner)
    {
        Model = model;
        _owner = owner;
        _editedPath = model.Path;
        RefreshResolved();
    }

    public string SamplerName => Model.SamplerName;
    public bool IsDiffuse => Model.IsDiffuse;
    public bool IsDirty => Model.IsDirty;
    public bool HasThumbnail => Thumbnail is not null;

    partial void OnThumbnailChanged(Bitmap? value) => OnPropertyChanged(nameof(HasThumbnail));
    public void RaiseDirty() => OnPropertyChanged(nameof(IsDirty));

    public void ResetFromModel()
    {
        EditedPath = Model.Path;
        Thumbnail = null;
        RefreshResolved();
        RaiseDirty();
    }

    public void RefreshResolved() => Unresolved = !(_owner.TextureExists?.Invoke(EditedPath) ?? true);

    [RelayCommand]
    private void Apply()
    {
        Model.SetPath(EditedPath.Trim());
        EditedPath = Model.Path;
        RefreshResolved();
        Thumbnail = null;
        _owner.NotifyChanged();
    }

    [RelayCommand]
    private void Revert()
    {
        Model.Revert();
        ResetFromModel();
        _owner.NotifyChanged();
    }

    [RelayCommand]
    private void Preview() => Thumbnail = _owner.LoadThumbnail?.Invoke(EditedPath);

    [RelayCommand] private void Open() => _owner.OpenTexture?.Invoke(EditedPath);
    [RelayCommand] private async Task CopyPath() => await _owner.Copy(EditedPath);
    [RelayCommand] private async Task Replace() { if (_owner.ReplaceTextureAsset is { } r) await r(this); }
}

public sealed partial class MaterialParameterViewModel : ViewModelBase
{
    private readonly MaterialEditorViewModel _owner;
    public MaterialParameter Model { get; }

    [ObservableProperty] private string _editedText;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _errorText = "";

    public MaterialParameterViewModel(MaterialParameter model, MaterialEditorViewModel owner)
    {
        Model = model;
        _owner = owner;
        _editedText = model.CurrentText;
    }

    public string Name => Model.Name;
    public string TypeName => Model.TypeName;
    public bool IsEditable => Model.IsEditable;
    public bool IsDirty => Model.IsDirty;

    public void RaiseDirty() => OnPropertyChanged(nameof(IsDirty));

    public void ResetFromModel()
    {
        EditedText = Model.CurrentText;
        HasError = false; ErrorText = "";
        RaiseDirty();
    }

    [RelayCommand]
    private void Apply()
    {
        try { Model.Apply(EditedText); HasError = false; ErrorText = ""; EditedText = Model.CurrentText; _owner.NotifyChanged(); }
        catch (Exception ex) { HasError = true; ErrorText = ex.Message; }
    }

    [RelayCommand]
    private void Revert() { Model.Revert(); ResetFromModel(); _owner.NotifyChanged(); }
}

public sealed partial class MaterialBindingViewModel : ViewModelBase
{
    public MaterialBinding Model { get; }
    public ObservableCollection<TextureSlotViewModel> Slots { get; } = new();
    public ObservableCollection<MaterialParameterViewModel> Parameters { get; } = new();

    [ObservableProperty] private bool _isVisible = true;

    public MaterialBindingViewModel(MaterialBinding model, MaterialEditorViewModel owner)
    {
        Model = model;
        foreach (var s in model.Slots) Slots.Add(new TextureSlotViewModel(s, owner));
        foreach (var p in model.Parameters) Parameters.Add(new MaterialParameterViewModel(p, owner));
    }

    public string Name => Model.Name;
    public string ShaderName => Model.ShaderName;
    public string AssignedTo => Model.AssignedTo;
    public bool HasAssignment => !string.IsNullOrEmpty(Model.AssignedTo);
    public bool HasParameters => Parameters.Count > 0;
    public bool IsDirty => Model.IsDirty;

    public void RaiseDirty()
    {
        OnPropertyChanged(nameof(IsDirty));
        foreach (var s in Slots) s.RaiseDirty();
        foreach (var p in Parameters) p.RaiseDirty();
    }

    [RelayCommand]
    private async Task CopyName() => await Owner!.Copy(Name);

    [RelayCommand]
    private void Revert()
    {
        Model.Revert();
        foreach (var s in Slots) s.ResetFromModel();
        foreach (var p in Parameters) p.ResetFromModel();
        Owner!.NotifyChanged();
    }

    // Set by the owner after construction so the commands can reach it.
    public MaterialEditorViewModel? Owner { get; set; }

    public bool Matches(string search, bool onlyUnresolved)
    {
        if (onlyUnresolved && !Slots.Any(s => s.Unresolved)) return false;
        if (string.IsNullOrWhiteSpace(search)) return true;
        return Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || AssignedTo.Contains(search, StringComparison.OrdinalIgnoreCase)
            || ShaderName.Contains(search, StringComparison.OrdinalIgnoreCase)
            || Slots.Any(s => s.SamplerName.Contains(search, StringComparison.OrdinalIgnoreCase)
                              || s.EditedPath.Contains(search, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Material-centric editor for a champion skin .bin or a map .materials.bin. Edits texture-slot
/// paths + numeric params on a live BinTree (via <see cref="MaterialDocument"/>); Apply re-resolves
/// textures into the viewport live, Save writes the edited .bin into the project override layer.
/// </summary>
public sealed partial class MaterialEditorViewModel : ViewModelBase
{
    private MaterialDocument? _doc;

    public WadAssetEntry? BinEntry { get; private set; }
    public MaterialSourceKind Kind { get; private set; }
    public ObservableCollection<MaterialBindingViewModel> Materials { get; } = new();

    [ObservableProperty] private bool _hasMaterials;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _onlyUnresolved;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private int _unresolvedCount;

    public bool HasUnresolved => UnresolvedCount > 0;
    partial void OnUnresolvedCountChanged(int value) => OnPropertyChanged(nameof(HasUnresolved));

    // Wired by MainWindowViewModel.
    public Func<string, bool>? TextureExists { get; set; }
    public Func<string, Bitmap?>? LoadThumbnail { get; set; }
    public Func<string, Task>? CopyHandler { get; set; }
    public Action<string>? OpenTexture { get; set; }
    public Func<TextureSlotViewModel, Task>? ReplaceTextureAsset { get; set; }
    public Action? ApplyToViewport { get; set; }
    public Func<Task>? SaveOverride { get; set; }

    public void Load(MaterialDocument doc, WadAssetEntry binEntry)
    {
        _doc = doc;
        BinEntry = binEntry;
        Kind = doc.Kind;
        Materials.Clear();
        foreach (var m in doc.Materials)
        {
            var vm = new MaterialBindingViewModel(m, this) { Owner = this };
            Materials.Add(vm);
        }
        HasMaterials = Materials.Count > 0;
        IsDirty = false;
        Search = ""; OnlyUnresolved = false;
        UpdateUnresolved();
        Summary = $"{(Kind == MaterialSourceKind.ChampionSkin ? "Champion" : "Map")} — {Materials.Count} material(s)";
    }

    public void Clear()
    {
        _doc = null; BinEntry = null;
        Materials.Clear();
        HasMaterials = false; IsDirty = false; Search = ""; Summary = ""; UnresolvedCount = 0;
    }

    public byte[]? Serialize() => _doc?.Serialize();

    public void NotifyChanged()
    {
        IsDirty = _doc?.IsDirty ?? false;
        foreach (var m in Materials) m.RaiseDirty();
        UpdateUnresolved();
    }

    private void UpdateUnresolved()
    {
        foreach (var m in Materials) foreach (var s in m.Slots) s.RefreshResolved();
        UnresolvedCount = Materials.Sum(m => m.Slots.Count(s => s.Unresolved));
    }

    public Task Copy(string text) => CopyHandler?.Invoke(text) ?? Task.CompletedTask;

    [RelayCommand] private void Apply() => ApplyToViewport?.Invoke();
    [RelayCommand] private async Task Save() { if (SaveOverride is { } s) await s(); }

    [RelayCommand]
    private void RevertAll()
    {
        foreach (var m in Materials)
        {
            m.Model.Revert();
            foreach (var s in m.Slots) s.ResetFromModel();
            foreach (var p in m.Parameters) p.ResetFromModel();
        }
        NotifyChanged();
    }

    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnOnlyUnresolvedChanged(bool value) => ApplyFilter();

    private void ApplyFilter()
    {
        foreach (var m in Materials) m.IsVisible = m.Matches(Search, OnlyUnresolved);
    }
}
