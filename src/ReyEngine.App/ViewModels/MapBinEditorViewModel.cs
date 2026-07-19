using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core.Assets;
using ReyEngine.Formats.Meta;

namespace ReyEngine.App.ViewModels;

/// <summary>M98: one editable row in the Map Bin Editor — typed editing (checkbox for bools, live
/// colour swatch for *color* vectors, text for the rest) applied straight into the live BinTree.</summary>
public sealed partial class MapBinRowViewModel : ObservableObject
{
    private readonly EditableBinField _f;
    private readonly MapBinEditorViewModel _owner;
    private bool _initializing = true;

    public ObservableCollection<MapBinRowViewModel> Children { get; } = new();
    public string Name => _f.Name;
    public string TypeName => _f.TypeName;
    public bool IsBranch => _f.IsBranch;
    public bool IsBool => _f.IsEditable && _f.Kind == BinValueKind.Bool;
    public bool IsEditableText => _f.IsEditable && _f.Kind != BinValueKind.Bool;
    public bool IsColor => _f.Kind is BinValueKind.Vector3 or BinValueKind.Vector4
                           && Name.Contains("color", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _boolValue;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private IBrush? _swatch;

    public MapBinRowViewModel(EditableBinField f, MapBinEditorViewModel owner)
    {
        _f = f;
        _owner = owner;
        // M98e: display the property's CURRENT value — OriginalText is a parse-time snapshot and shows
        // stale data after an edit once the rows are rebuilt (reselecting the object, saving, …).
        string current = f.CurrentText(h => owner.Resolve?.Invoke(h));
        _text = current;
        _boolValue = current == "true";
        _isDirty = f.IsEditable && current != f.OriginalText;
        UpdateSwatch();
        foreach (var c in f.Children) Children.Add(new MapBinRowViewModel(c, owner));
        _initializing = false;
    }

    partial void OnTextChanged(string value)
    {
        if (_initializing || !IsEditableText) return;
        try
        {
            _f.Apply(value);
            HasError = false;
            IsDirty = value != _f.OriginalText;
            UpdateSwatch();
            _owner.NotifyDirty();
        }
        catch { HasError = true; }   // keep typing — invalid text just isn't committed
    }

    partial void OnBoolValueChanged(bool value)
    {
        if (_initializing || !IsBool) return;
        _f.Apply(value ? "true" : "false");
        IsDirty = (value ? "true" : "false") != _f.OriginalText;
        _owner.NotifyDirty();
    }

    private void UpdateSwatch()
    {
        if (!IsColor) return;
        try
        {
            var parts = Text.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) { Swatch = null; return; }
            byte C(string s) => (byte)Math.Clamp(float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture) * 255f, 0f, 255f);
            Swatch = new SolidColorBrush(Color.FromRgb(C(parts[0]), C(parts[1]), C(parts[2])));
        }
        catch { Swatch = null; }
    }
}

/// <summary>One object in the left list (grouped by meta class).</summary>
public sealed class MapBinObjectViewModel
{
    public required string Name { get; init; }
    public required string ClassName { get; init; }
    public required EditableBinField Root { get; init; }
}

public sealed partial class MapBinClassGroupViewModel : ObservableObject
{
    public required string ClassName { get; init; }
    public ObservableCollection<MapBinObjectViewModel> Objects { get; } = new();
    public string Display => $"{ClassName} ({Objects.Count})";
}

/// <summary>
/// M98: dedicated Map Bin Editor window (right-click a .bin ▸ Open in Map Bin Editor) — the fast path
/// for editing map*.bin / materials.bin: objects grouped by class with search, form-style typed editing,
/// one-click patch update (M97 three-way merge against the current Riot original) and save-to-project.
/// </summary>
public sealed partial class MapBinEditorViewModel : ObservableObject
{
    [ObservableProperty] private string _title = "No bin open";
    [ObservableProperty] private string _status = "Right-click a .bin in the Content Browser ▸ Open in Map Bin Editor.";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _hasDocument;
    [ObservableProperty] private MapBinObjectViewModel? _selectedObject;

    public ObservableCollection<MapBinClassGroupViewModel> Groups { get; } = new();
    public ObservableCollection<MapBinRowViewModel> Rows { get; } = new();

    public WadAssetEntry? Entry { get; private set; }
    private BinEditorDocument? _doc;
    private readonly List<MapBinObjectViewModel> _allObjects = new();

    // wired once by MainWindowViewModel
    public Func<uint, string?>? Resolve;
    public Func<WadAssetEntry, byte[], Task<bool>>? SaveBytes;
    public Func<Task<string?>>? PickOldOriginal;
    public Func<WadAssetEntry, byte[]?>? ReadRiotOriginal;
    public Action<string>? Info;
    public Action<string>? Warn;

    public void Load(WadAssetEntry entry, byte[] bytes)
    {
        Entry = entry;
        _doc = BinEditorDocument.Parse(bytes, h => Resolve?.Invoke(h));
        _allObjects.Clear();
        foreach (var root in _doc.Roots)
            _allObjects.Add(new MapBinObjectViewModel
            {
                Name = Resolve?.Invoke(root.NameHash) ?? $"0x{root.NameHash:x8}",
                ClassName = root.Name,
                Root = root,
            });
        Title = entry.DisplayName;
        IsDirty = false;
        HasDocument = true;
        RebuildGroups();
        Status = $"{_allObjects.Count} object(s) · {Groups.Count} class(es) · {_doc.Dependencies.Count} dependenc(ies)";
        // the classes map modders touch most, front and centre
        SelectedObject = _allObjects.FirstOrDefault(o => o.ClassName.Contains("SunProperties", StringComparison.OrdinalIgnoreCase))
                      ?? _allObjects.FirstOrDefault();
    }

    private void RebuildGroups()
    {
        Groups.Clear();
        string q = Search.Trim();
        foreach (var byClass in _allObjects
                     .Where(o => q.Length == 0
                                 || o.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                 || o.ClassName.Contains(q, StringComparison.OrdinalIgnoreCase))
                     .GroupBy(o => o.ClassName)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var group = new MapBinClassGroupViewModel { ClassName = byClass.Key };
            foreach (var o in byClass.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase))
                group.Objects.Add(o);
            Groups.Add(group);
        }
    }

    partial void OnSearchChanged(string value) => RebuildGroups();

    partial void OnSelectedObjectChanged(MapBinObjectViewModel? value)
    {
        Rows.Clear();
        if (value is null) return;
        foreach (var c in value.Root.Children)
            Rows.Add(new MapBinRowViewModel(c, this));
    }

    internal void NotifyDirty() => IsDirty = true;

    [RelayCommand]
    private async Task Save()
    {
        if (_doc is null || Entry is null || SaveBytes is null) return;
        byte[] bytes;
        try { bytes = _doc.Serialize(); }
        catch (Exception ex) { Status = $"Serialize failed: {ex.Message}"; return; }
        if (await SaveBytes(Entry, bytes))
        {
            // M98e: re-baseline from the saved bytes so every row shows the saved value as its new
            // original (dirty dots clear); keep the user's place in the object list.
            uint keep = SelectedObject?.Root.NameHash ?? 0;
            Load(Entry, bytes);
            if (keep != 0 && _allObjects.FirstOrDefault(o => o.Root.NameHash == keep) is { } again)
                SelectedObject = again;
            IsDirty = false;
            Status = $"Saved to project ({bytes.Length:n0} bytes).";
        }
    }

    /// <summary>M97/M98: three-way patch update for THIS bin — old original picked by the user, the new
    /// base read from the untouched Riot files, the mod side is the current editor state (edits included).</summary>
    [RelayCommand]
    private async Task UpdateFromOldPatch()
    {
        if (_doc is null || Entry is null || PickOldOriginal is null) return;
        var oldPath = await PickOldOriginal();
        if (oldPath is null) return;
        try
        {
            byte[] oldBytes = File.ReadAllBytes(oldPath);
            byte[]? riotNew = ReadRiotOriginal?.Invoke(Entry);
            if (riotNew is null)
            {
                Status = "Current Riot original not found — the project needs its reference WAD (or open from a game WAD).";
                Warn?.Invoke(Status);
                return;
            }
            byte[] mod = _doc.Serialize();
            var (merged, rep) = BinThreeWayMerge.Merge(oldBytes, mod, riotNew, Resolve);
            Load(Entry, merged);
            IsDirty = true;
            Status = $"Patch update: {rep.ModAdded} added · {rep.ModModified} modified · {rep.ModRemoved} removed · " +
                     $"{rep.Conflicts} conflict(s) → mod kept. Save To Project to keep it.";
            Info?.Invoke($"{Entry.DisplayName}: {Status}");
            foreach (var c in rep.ConflictDetails.Take(20)) Warn?.Invoke("  conflict: " + c);
            if (rep.ConflictDetails.Count > 20) Warn?.Invoke($"  … {rep.ConflictDetails.Count - 20} more conflicts");
        }
        catch (Exception ex)
        {
            Status = $"Patch update failed: {ex.Message}";
            Warn?.Invoke(Status);
        }
    }
}
