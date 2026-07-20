using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Undo;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Shaders;

namespace ReyEngine.App.ViewModels;

public sealed partial class TextureSlotViewModel : ViewModelBase
{
    private readonly MaterialEditorViewModel _owner;
    public TextureSlot Model { get; }
    public MaterialBindingViewModel? Binding { get; set; }

    [ObservableProperty] private string _editedPath;
    [ObservableProperty] private bool _unresolved;
    [ObservableProperty] private Bitmap? _thumbnail;

    private string _lastApplied;   // the path currently applied to the live property (for undo capture)

    public TextureSlotViewModel(TextureSlot model, MaterialEditorViewModel owner)
    {
        Model = model;
        _owner = owner;
        _editedPath = model.Path;
        _lastApplied = model.Path;
        RefreshResolved();
    }

    public string SamplerName => Model.SamplerName;
    public bool IsDiffuse => Model.IsDiffuse;

    /// <summary>M103: the selected shader declares no sampler by this name, so the shader will ignore
    /// whatever is bound here. False when the shader isn't in the catalogue (nothing to check against).</summary>
    [ObservableProperty] private bool _notInShader;
    public bool IsDirty => Model.IsDirty;
    public bool CanRemove => Model.IsRemovable;
    public bool HasThumbnail => Thumbnail is not null;

    partial void OnThumbnailChanged(Bitmap? value) => OnPropertyChanged(nameof(HasThumbnail));
    public void RaiseDirty() => OnPropertyChanged(nameof(IsDirty));

    public void ResetFromModel()
    {
        EditedPath = Model.Path;
        _lastApplied = Model.Path;
        Thumbnail = null;
        RefreshResolved();
        RaiseDirty();
    }

    public void RefreshResolved() => Unresolved = !(_owner.TextureExists?.Invoke(EditedPath) ?? true);

    [RelayCommand]
    private void Apply()
    {
        var oldPath = _lastApplied;
        Model.SetPath(EditedPath.Trim());
        EditedPath = Model.Path;
        if (!string.Equals(EditedPath, oldPath, StringComparison.Ordinal))
            _owner.UndoService?.PushApplied(new TexturePathEditCommand(_owner.DocContext, Model, oldPath, EditedPath, SyncFromCommand));
        _lastApplied = EditedPath;
        RefreshResolved();
        Thumbnail = null;
        _owner.NotifyChanged();
    }

    /// <summary>Called by undo/redo after the command re-applied a path — sync the slot row UI.</summary>
    private void SyncFromCommand(string appliedPath)
    {
        EditedPath = appliedPath;
        _lastApplied = appliedPath;
        Thumbnail = null;
        RefreshResolved();
        RaiseDirty();
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
    // M96: toggle — clicking Preview again hides the thumbnail (it could only be shown before)
    private void Preview() => Thumbnail = Thumbnail is not null ? null : _owner.LoadThumbnail?.Invoke(EditedPath);

    [RelayCommand] private void Open() => _owner.OpenTexture?.Invoke(EditedPath);
    [RelayCommand] private async Task CopyPath() => await _owner.Copy(EditedPath);
    [RelayCommand] private async Task Replace() { if (_owner.ReplaceTextureAsset is { } r) await r(this); }
    [RelayCommand] private void Remove() => Binding?.RemoveSlot(this);
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
    public bool IsRemovable => Model.IsRemovable;   // M55

    /// <summary>M50c: colour-ish params (TintColor, Color_Inside…) show a live swatch preview.</summary>
    public bool IsColorLike =>
        Name.Contains("Color", StringComparison.OrdinalIgnoreCase) || Name.Contains("Tint", StringComparison.OrdinalIgnoreCase);

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
        try
        {
            var oldText = Model.CurrentText;   // canonical form (round-trippable)
            Model.Apply(EditedText);           // throws on invalid input — nothing is pushed then
            HasError = false; ErrorText = "";
            EditedText = Model.CurrentText;
            if (!string.Equals(EditedText, oldText, StringComparison.Ordinal))
                _owner.UndoService?.PushApplied(new MaterialParamEditCommand(_owner.DocContext, Model, oldText, EditedText, SyncFromCommand));
            _owner.NotifyChanged();
        }
        catch (Exception ex) { HasError = true; ErrorText = ex.Message; }
    }

    /// <summary>Called by undo/redo after the command re-applied a value — sync the row UI.</summary>
    private void SyncFromCommand(string appliedText)
    {
        EditedText = appliedText;
        HasError = false; ErrorText = "";
        RaiseDirty();
        _owner.NotifyChanged();
    }

    [RelayCommand]
    private void Revert() { Model.Revert(); ResetFromModel(); _owner.NotifyChanged(); }
}

/// <summary>M103: one editable shader feature switch (checkbox row).</summary>
public sealed partial class MaterialSwitchViewModel : ViewModelBase
{
    private readonly MaterialBindingViewModel _binding;
    private bool _initializing = true;
    public MaterialSwitch Model { get; }

    [ObservableProperty] private bool _isOn;

    public MaterialSwitchViewModel(MaterialSwitch model, MaterialBindingViewModel binding)
    {
        Model = model;
        _binding = binding;
        _isOn = model.On;
        _initializing = false;
    }

    public string Name => Model.Name;
    public bool IsDirty => Model.IsDirty;
    public void RaiseDirty() => OnPropertyChanged(nameof(IsDirty));

    partial void OnIsOnChanged(bool value)
    {
        if (_initializing) return;
        Model.SetOn(value);
        RaiseDirty();
        _binding.RaiseDirty();
        _binding.Owner?.NotifyChanged();
    }

    [RelayCommand]
    private void Remove() => _binding.RemoveSwitch(this);
}

public sealed partial class MaterialBindingViewModel : ViewModelBase
{
    public MaterialBinding Model { get; }
    public ObservableCollection<TextureSlotViewModel> Slots { get; } = new();
    public ObservableCollection<MaterialParameterViewModel> Parameters { get; } = new();
    /// <summary>M103: the material's shader feature switches, editable.</summary>
    public ObservableCollection<MaterialSwitchViewModel> Switches { get; } = new();

    [ObservableProperty] private bool _isVisible = true;

    public MaterialBindingViewModel(MaterialBinding model, MaterialEditorViewModel owner)
    {
        Model = model;
        Owner = owner;
        _editedShader = model.RenderShader ?? "";
        foreach (var s in model.Slots) Slots.Add(new TextureSlotViewModel(s, owner) { Binding = this });
        foreach (var p in model.Parameters) Parameters.Add(new MaterialParameterViewModel(p, owner));
        foreach (var w in model.AllSwitches) Switches.Add(new MaterialSwitchViewModel(w, this));   // M103
    }

    public string Name => Model.Name;
    public string ShaderName => Model.ShaderName;
    public string AssignedTo => Model.AssignedTo;

    // ---- M52: shader selector ----
    [ObservableProperty] private string _editedShader = "";
    [ObservableProperty] private string _shaderChangeStatus = "";
    public bool CanChangeShader => Model.CanChangeShader;
    public string CurrentShaderText => Model.RenderShader ?? Model.ShaderName;

    [RelayCommand]
    private void ApplyShader() => Owner?.ChangeShader(this, EditedShader);

    // ---- M103: what the SELECTED shader actually declares (from the game install's shader bin) ----

    /// <summary>The catalogue entry for this material's current shader; null when the shader isn't in
    /// the catalogue (an older/removed shader, or no install scanned yet).</summary>
    public LeagueShaderDef? ShaderDef { get; private set; }

    /// <summary>Samplers/parameters/switches the shader declares but this material doesn't carry — the
    /// "what else can I set on this shader" list, each addable with one click.</summary>
    public ObservableCollection<ShaderTextureDef> MissingSamplers { get; } = new();
    public ObservableCollection<ShaderParamDef> MissingParameters { get; } = new();
    public ObservableCollection<string> ShaderSwitches { get; } = new();

    public bool HasSwitches => Switches.Count > 0;
    public bool CanEditSwitches => Model.CanEditSwitches;

    /// <summary>Turn on a feature switch the shader declares but this material doesn't list.</summary>
    [RelayCommand]
    private void AddShaderSwitch(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var sw = Model.AddSwitch(name.Trim());
        if (sw is null) return;
        Switches.Add(new MaterialSwitchViewModel(sw, this));
        OnPropertyChanged(nameof(HasSwitches));
        RaiseDirty();
        Owner!.NotifyChanged();
        Owner!.RefreshShaderDefs();
    }

    public void RemoveSwitch(MaterialSwitchViewModel vm)
    {
        if (!Model.RemoveSwitch(vm.Model)) return;
        Switches.Remove(vm);
        OnPropertyChanged(nameof(HasSwitches));
        RaiseDirty();
        Owner!.NotifyChanged();
        Owner!.RefreshShaderDefs();
    }

    [ObservableProperty] private bool _hasShaderDef;
    [ObservableProperty] private string _shaderDefSummary = "";
    public bool HasMissingSamplers => MissingSamplers.Count > 0;
    public bool HasMissingParameters => MissingParameters.Count > 0;
    public bool HasShaderSwitches => ShaderSwitches.Count > 0;

    /// <summary>Recompute the shader-driven panel: which of the shader's samplers/params this material
    /// is missing, and which of its bindings the shader doesn't know about.</summary>
    public void RefreshShaderDef(ShaderCatalog? catalog)
    {
        ShaderDef = catalog?.Find(Model.RenderShader ?? Model.ShaderName);
        HasShaderDef = ShaderDef is not null;

        MissingSamplers.Clear();
        MissingParameters.Clear();
        ShaderSwitches.Clear();

        if (ShaderDef is null)
        {
            ShaderDefSummary = catalog is null
                ? ""
                : "This shader isn't in the selected environment's catalogue — it may be from another patch.";
            foreach (var s in Slots) s.NotInShader = false;   // nothing to validate against
        }
        else
        {
            ShaderDefSummary = ShaderDef.Summary;
            var bound = Slots.Select(s => s.SamplerName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var t in ShaderDef.Textures)
                if (!bound.Contains(t.Name)) MissingSamplers.Add(t);

            var set = Parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var pd in ShaderDef.Parameters)
                if (!set.Contains(pd.Name)) MissingParameters.Add(pd);

            var have = Switches.Select(w => w.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var w in ShaderDef.StaticSwitches)
                if (!have.Contains(w)) ShaderSwitches.Add(w);

            var declared = ShaderDef.Textures.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var s in Slots) s.NotInShader = !declared.Contains(s.SamplerName);
        }
        OnPropertyChanged(nameof(HasMissingSamplers));
        OnPropertyChanged(nameof(HasMissingParameters));
        OnPropertyChanged(nameof(HasShaderSwitches));
    }

    /// <summary>Bind a sampler the shader declares, pre-filled with the shader's own default texture.</summary>
    [RelayCommand]
    private void AddShaderSampler(ShaderTextureDef? def)
    {
        if (def is null) return;
        var slot = Model.AddSampler(def.Name, def.DefaultTexturePath);
        if (slot is null) return;
        Slots.Add(new TextureSlotViewModel(slot, Owner!) { Binding = this });
        RaiseDirty();
        Owner!.NotifyChanged();
        Owner!.RefreshShaderDefs();
    }

    /// <summary>Add a parameter the shader declares, pre-filled with the shader's default value.</summary>
    [RelayCommand]
    private void AddShaderParameter(ShaderParamDef? def)
    {
        if (def is null) return;
        var p = Model.AddParameter(def.Name);
        if (p is null) return;
        try { p.Apply(def.DefaultText); } catch { /* keep the cloned prototype value */ }
        Parameters.Add(new MaterialParameterViewModel(p, Owner!));
        OnPropertyChanged(nameof(HasParameters));
        RaiseDirty();
        Owner!.NotifyChanged();
        Owner!.RefreshShaderDefs();
    }

    /// <summary>Refresh shader-related display after ChangeShader swapped the pass link.</summary>
    public void RaiseShaderChanged()
    {
        OnPropertyChanged(nameof(CurrentShaderText));
        OnPropertyChanged(nameof(ShaderName));
        OnPropertyChanged(nameof(IsDirty));
    }
    public bool HasAssignment => !string.IsNullOrEmpty(Model.AssignedTo);
    public bool HasParameters => Parameters.Count > 0;
    public bool CanEditSamplers => Model.CanEditSamplers;
    public bool IsDirty => Model.IsDirty;

    // ---- M32 preview profile (features + UV transform) ----
    public string ProfileLabel => Model.Profile.ProfileLabel;
    public string FeatureSummary => Model.Profile.FeatureSummary;
    public bool UsesSpecular => Model.Profile.UsesSpecular;

    /// <summary>M34: compositing mode from the material's technique blend state (Opaque/Cutout/Transparent).</summary>
    public string RenderModeLabel => Model.Profile.RenderModeLabel;

    // ---- M34 render state (read-only; from the material's technique/pass) ----
    public string CullEnabledText => Model.Profile.CullEnabled ? "Yes (cull backfaces)" : "No (two-sided)";
    public string BlendEnabledText => Model.Profile.BlendEnabled ? "Yes" : "No";
    public string DepthWriteText => Model.Profile.DepthWrite ? "Yes" : "No (transparent)";
    public string AlphaCutoutText => Model.Profile.AlphaCutout ? $"Yes (cutoff {(Model.Profile.AlphaCutoff ?? 0.35f):0.##})" : "No";
    public string TwoSidedText => Model.Profile.TwoSided ? "Active" : "Off";

    /// <summary>Per-material UV transform display (scale/offset, and the source param name when known).</summary>
    public string UvTransformText
    {
        get
        {
            var p = Model.Profile;
            if (!p.HasUvTransform) return "UV: identity (scale 1,1 · offset 0,0)";
            var s = $"UV scale ({p.UvScale.X:0.###}, {p.UvScale.Y:0.###}) · offset ({p.UvOffset.X:0.###}, {p.UvOffset.Y:0.###})";
            if (p.UvRotationDegrees != 0f) s += $" · rot {p.UvRotationDegrees:0.#}°";
            return s;
        }
    }

    public bool HasUvSource => Model.Profile.HasUvTransform && (Model.Profile.UvScaleSource is not null || Model.Profile.UvOffsetSource is not null);
    public string UvSourceText
    {
        get
        {
            var p = Model.Profile;
            var parts = new List<string>();
            if (p.UvScaleSource is { } s) parts.Add($"scale ← {s}");
            if (p.UvOffsetSource is { } o) parts.Add($"offset ← {o}");
            return parts.Count > 0 ? string.Join(" · ", parts) : "";
        }
    }

    /// <summary>Warn when we couldn't map this material to a known preview profile (UV/features unresolved).</summary>
    public bool ProfileUnresolved => Model.Profile.Kind == PreviewProfileKind.Unknown;

    [RelayCommand]
    private void AddSampler()
    {
        var slot = Model.AddSampler("New_Texture", Model.Diffuse?.Path ?? "");
        if (slot is null) return;
        Slots.Add(new TextureSlotViewModel(slot, Owner!) { Binding = this });
        Owner!.UndoService?.PushApplied(new SamplerAddRemoveCommand(Owner.DocContext, Model, slot, isAdd: true, OnSamplerPresenceChanged));
        RaiseDirty();
        Owner!.NotifyChanged();
    }

    public void RemoveSlot(TextureSlotViewModel slot)
    {
        if (!Model.RemoveSampler(slot.Model)) return;
        Slots.Remove(slot);
        Owner!.UndoService?.PushApplied(new SamplerAddRemoveCommand(Owner.DocContext, Model, slot.Model, isAdd: false, OnSamplerPresenceChanged));
        RaiseDirty();
        Owner!.NotifyChanged();
    }

    /// <summary>Called by undo/redo after a sampler was re-added/removed — sync the slot rows.</summary>
    private void OnSamplerPresenceChanged(TextureSlot slot, bool nowPresent)
    {
        if (nowPresent)
        {
            if (!Slots.Any(s => ReferenceEquals(s.Model, slot)))
                Slots.Add(new TextureSlotViewModel(slot, Owner!) { Binding = this });
        }
        else
        {
            var vm = Slots.FirstOrDefault(s => ReferenceEquals(s.Model, slot));
            if (vm is not null) Slots.Remove(vm);
        }
        RaiseDirty();
        Owner!.NotifyChanged();
    }

    public void RaiseDirty()
    {
        OnPropertyChanged(nameof(IsDirty));
        foreach (var s in Slots) s.RaiseDirty();
        foreach (var p in Parameters) p.RaiseDirty();
        foreach (var w in Switches) w.RaiseDirty();
    }

    // ---- M55: parameter add/remove ----
    [ObservableProperty] private string _newParamName = "";

    [RelayCommand]
    private void AddParameter()
    {
        var name = NewParamName.Trim();
        if (name.Length == 0) return;
        var p = Model.AddParameter(name);
        if (p is null) return;   // no prototype param to clone the schema from
        Parameters.Add(new MaterialParameterViewModel(p, Owner!));
        NewParamName = "";
        OnPropertyChanged(nameof(HasParameters));
        RaiseDirty();
        Owner!.NotifyChanged();
    }

    [RelayCommand]
    private void RemoveParameter(MaterialParameterViewModel? pvm)
    {
        if (pvm is null || !Model.RemoveParameter(pvm.Model)) return;
        Parameters.Remove(pvm);
        OnPropertyChanged(nameof(HasParameters));
        RaiseDirty();
        Owner!.NotifyChanged();
    }

    public bool CanEditParameters => Model.CanEditParameters;

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

    public bool Matches(string search, bool onlyUnresolved, IReadOnlySet<string>? meshMaterials = null)
    {
        // M101: when a mesh is selected, only its own materials are listed (null = show all)
        if (meshMaterials is not null && !meshMaterials.Contains(Name)) return false;
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

    // ---- M52: shader selector — swap the pass shader + auto-add the samplers that shader uses ----
    /// <summary>Distinct shaders seen in the loaded document (the realistic choices for this map/skin).</summary>
    public ObservableCollection<string> KnownShaders { get; } = new();
    private readonly Dictionary<string, HashSet<string>> _shaderSamplers = new(StringComparer.OrdinalIgnoreCase);

    // ---- M103: the full League shader list, read from the selected game install ----

    /// <summary>Every shader the selected environment's client ships (null until one is scanned).</summary>
    public ShaderCatalog? Catalog { get; private set; }

    /// <summary>Installs the shader list can be read from — "Live", "PBE", … (filled by the host).</summary>
    public ObservableCollection<string> ShaderEnvironments { get; } = new();
    [ObservableProperty] private string? _selectedShaderEnvironment;
    [ObservableProperty] private string _catalogStatus = "No shader catalogue — pick a game environment.";
    public bool HasCatalog => Catalog is not null;

    /// <summary>Host hook: scan (or load the cache for) that environment and call <see cref="SetCatalog"/>.</summary>
    public Func<string, Task>? RequestCatalog { get; set; }

    partial void OnSelectedShaderEnvironmentChanged(string? value)
    {
        if (!string.IsNullOrEmpty(value) && RequestCatalog is { } req) _ = req(value);
    }

    public void SetCatalog(ShaderCatalog? catalog)
    {
        Catalog = catalog;
        OnPropertyChanged(nameof(HasCatalog));
        CatalogStatus = catalog is null
            ? "Shader catalogue unavailable for this install (Global.wad not found)."
            : $"{catalog.Environment}: {catalog.Shaders.Count:n0} shaders · {string.Join(", ", catalog.Categories)}";
        BuildShaderIndex();
        RefreshShaderDefs();
    }

    /// <summary>Re-evaluate every material against the catalogue (after a load or an environment switch).</summary>
    public void RefreshShaderDefs()
    {
        foreach (var m in Materials) m.RefreshShaderDef(Catalog);
    }

    /// <summary>Learn shader -> sampler-set from every material in the document (data-driven "required
    /// samplers": what materials using that shader actually bind), then union in the whole catalogue so
    /// the dropdown offers every shader the client has, not just the ones this file happens to use.</summary>
    private void BuildShaderIndex()
    {
        KnownShaders.Clear();
        _shaderSamplers.Clear();
        if (_doc is not null)
            foreach (var b in _doc.Materials)
            {
                if (string.IsNullOrEmpty(b.RenderShader) || b.RenderShader.StartsWith("0x")) continue;
                if (!_shaderSamplers.TryGetValue(b.RenderShader, out var set))
                    _shaderSamplers[b.RenderShader] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in b.Slots) set.Add(s.SamplerName);
            }

        var names = new SortedSet<string>(_shaderSamplers.Keys, StringComparer.OrdinalIgnoreCase);
        if (Catalog is not null)
            foreach (var sh in Catalog.Shaders) names.Add(sh.Name);
        foreach (var n in names) KnownShaders.Add(n);
    }

    /// <summary>Switch a material to another shader and add any sampler slots that the target shader's
    /// materials use but this one lacks (empty path — fill it in afterwards).</summary>
    public void ChangeShader(MaterialBindingViewModel vm, string shader)
    {
        if (string.IsNullOrWhiteSpace(shader)) return;
        if (!vm.Model.SetRenderShader(shader))
        {
            vm.ShaderChangeStatus = "This material has no technique pass — shader can't be changed.";
            return;
        }
        // The catalogue is authoritative (it lists what the shader actually declares, with the shader's
        // own default texture); the document-learned sampler set is the fallback when it isn't loaded.
        int added = 0;
        var def = Catalog?.Find(shader.Trim());
        if (def is not null)
        {
            foreach (var t in def.Textures)
                if (!vm.Model.Slots.Any(x => string.Equals(x.SamplerName, t.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    var slot = vm.Model.AddSampler(t.Name, t.DefaultTexturePath);
                    if (slot is not null) { vm.Slots.Add(new TextureSlotViewModel(slot, this) { Binding = vm }); added++; }
                }
        }
        else if (_shaderSamplers.TryGetValue(shader.Trim(), out var required))
        {
            foreach (var samplerName in required.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                if (!vm.Model.Slots.Any(x => string.Equals(x.SamplerName, samplerName, StringComparison.OrdinalIgnoreCase)))
                {
                    var slot = vm.Model.AddSampler(samplerName, "");
                    if (slot is not null) { vm.Slots.Add(new TextureSlotViewModel(slot, this) { Binding = vm }); added++; }
                }
        }
        vm.RaiseShaderChanged();
        vm.RefreshShaderDef(Catalog);
        IsDirty = _doc?.IsDirty ?? true;
        vm.ShaderChangeStatus = def is not null
            ? added > 0
                ? $"Shader set. Added {added} sampler slot(s) this shader declares, pre-filled with its default textures."
                : "Shader set. Every sampler this shader declares is already bound."
            : added > 0
                ? $"Shader set. Added {added} sampler slot(s) used by this shader — fill in their texture paths."
                : "Shader set. This shader isn't in the catalogue, so its sampler list couldn't be checked.";
    }

    /// <summary>M50c: auto-load the diffuse thumbnail of one material — used when the user opens a
    /// material from the selected mesh's MATERIALS card, so the texture preview shows immediately.</summary>
    public void AutoPreviewDiffuse(string materialName)
    {
        var m = Materials.FirstOrDefault(x => string.Equals(x.Name, materialName, StringComparison.OrdinalIgnoreCase));
        var slot = m?.Slots.FirstOrDefault(s => s.IsDiffuse) ?? m?.Slots.FirstOrDefault();
        if (slot is not null && slot.PreviewCommand.CanExecute(null)) slot.PreviewCommand.Execute(null);
    }

    // Wired by MainWindowViewModel.
    public Func<string, bool>? TextureExists { get; set; }
    public Func<string, Bitmap?>? LoadThumbnail { get; set; }
    public Func<string, Task>? CopyHandler { get; set; }
    public Action<string>? OpenTexture { get; set; }
    public Func<TextureSlotViewModel, Task>? ReplaceTextureAsset { get; set; }
    public Action? ApplyToViewport { get; set; }
    public Func<Task>? SaveOverride { get; set; }

    /// <summary>Global undo stack (set by MainWindowViewModel). Commands are tagged with the doc instance.</summary>
    public UndoRedoService? UndoService { get; set; }
    public object? DocContext => _doc;

    public void Load(MaterialDocument doc, WadAssetEntry binEntry)
    {
        if (_doc is not null) UndoService?.PurgeContext(_doc); // stale commands must never mutate a replaced doc
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
        BuildShaderIndex();   // M52: shader -> sampler-set map for the shader selector
        RefreshShaderDefs();  // M103: match each material against the catalogue
        UpdateUnresolved();
        Summary = $"{(Kind == MaterialSourceKind.ChampionSkin ? "Champion" : "Map")} — {Materials.Count} material(s)";
    }

    public void Clear()
    {
        if (_doc is not null) UndoService?.PurgeContext(_doc);
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

    // ---- M101: scope the list to the selected mesh's materials ----
    private HashSet<string>? _meshFilter;

    /// <summary>Show only these materials (the selected mesh's). Null/empty restores the full list.</summary>
    public void SetMeshFilter(IEnumerable<string>? materials)
    {
        var set = materials?.Where(m => !string.IsNullOrEmpty(m)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _meshFilter = set is { Count: > 0 } ? set : null;
        MeshFilterLabel = _meshFilter is null ? "" : $"Filtered to the selected mesh ({_meshFilter.Count} material(s)) — clear the selection to see all.";
        HasMeshFilter = _meshFilter is not null;
        ApplyFilter();
    }

    [ObservableProperty] private string _meshFilterLabel = "";
    [ObservableProperty] private bool _hasMeshFilter;

    private void ApplyFilter()
    {
        foreach (var m in Materials) m.IsVisible = m.Matches(Search, OnlyUnresolved, _meshFilter);
    }
}
