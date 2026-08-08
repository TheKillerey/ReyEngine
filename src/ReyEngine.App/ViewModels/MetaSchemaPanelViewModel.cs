using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core.Meta;
using ReyEngine.Formats.Meta;

namespace ReyEngine.App.ViewModels;

/// <summary>M368/M370: one property the class declares but the inspected object does not carry - and, when
/// the type allows it, a one-click add that writes the schema's own default.</summary>
public sealed partial class MetaSchemaRowViewModel : ObservableObject
{
    private readonly MetaSchemaPanelViewModel _panel;

    public MetaSchemaRowViewModel(MetaProperty p, MetaSchemaPanelViewModel panel)
    {
        _panel = panel;
        Prop = p;
        Name = p.HasName ? p.Name : $"0x{p.Hash:x8}";
        TypeName = p.KeyType.Length > 0 ? $"{p.FieldType}<{p.KeyType}>" : p.FieldType;
        // Raw JSON straight from the dump. Deliberately NOT reformatted into either editor's value syntax:
        // that would be a second, lossy type system over every bin type, for a reference display.
        DefaultText = p.Default ?? "(no default)";
        HasDefault = p.Default is not null;
        // The single source of truth for "can this be written", shared with the code that does the writing,
        // so the button can never be enabled for something the factory would then refuse.
        DeclineReason = MetaDefaultProperty.DeclineReason(p.FieldType, p.Default);
    }

    public MetaProperty Prop { get; }
    public string Name { get; }
    public string TypeName { get; }
    public string DefaultText { get; }
    public bool HasDefault { get; }

    /// <summary>Why this field cannot be added, or null when it can.</summary>
    public string? DeclineReason { get; }

    public bool CanAdd => DeclineReason is null && _panel.CanEdit && !Added;

    /// <summary>Set once the field has been written, so the row can show it and not offer it twice. The row
    /// is left in place rather than removed: it just moved from "absent" to "added", and seeing that happen
    /// is more useful than having it vanish.</summary>
    [ObservableProperty] private bool _added;

    [ObservableProperty] private string? _error;

    partial void OnAddedChanged(bool value) => OnPropertyChanged(nameof(CanAdd));

    /// <summary>Tooltip: the reason it is disabled, or what the click will do.</summary>
    public string AddTip => DeclineReason
        ?? (_panel.CanEdit
            ? $"Write {Name} = {DefaultText} into the bin. The value is the game's own default, so nothing "
              + "changes visually until you edit it."
            : "This document is read-only.");

    [RelayCommand]
    private void Add()
    {
        if (!CanAdd) return;
        var (ok, reason) = _panel.AddField(this);
        if (ok) { Added = true; Error = null; }
        else Error = reason;
    }
}

/// <summary>
/// <para>M368: "what does this object NOT set, and what does the game use instead" - computed once here and
/// shown by BOTH the particle and material editors. M370 makes each row writable.</para>
///
/// <para>Shared rather than copied per editor on purpose. This codebase has been bitten twice by duplicated
/// logic drifting apart (the dead sync/async scene-builder twins, and the ribbon extruder that nearly got a
/// second implementation), so the second consumer extracts rather than clones.</para>
///
/// <para><b>Absent is not the same as zero.</b> VfxEmitterDefinitionData declares 135 properties and 107
/// carry an authored default, so a field missing from a bin is almost always running on a real value rather
/// than nothing.</para>
/// </summary>
public sealed partial class MetaSchemaPanelViewModel : ObservableObject
{
    /// <summary>Writes one field onto the underlying bin object. Returns false plus a reason rather than
    /// throwing, because every refusal here is expected rather than exceptional.</summary>
    public delegate bool AddFieldHandler(uint nameHash, string fieldType, string? defaultJson, out string? reason);

    private AddFieldHandler? _add;

    private MetaSchemaPanelViewModel() { }

    public bool HasSchema { get; private init; }
    public string ClassDisplayName { get; private init; } = "";
    public int DeclaredCount { get; private init; }

    /// <summary>False for a read-only document, or when no writer was supplied. Rows then render as plain
    /// reference rows with no add button, which is what M368 shipped.</summary>
    public bool CanEdit { get; private init; }

    public ObservableCollection<MetaSchemaRowViewModel> UnsetRows { get; } = new();

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private int _presentCount;

    public static readonly MetaSchemaPanelViewModel None = new();

    /// <summary>Build for one object. Never throws: the delegates come from an optional database, and a
    /// schema panel failing must never take an editor down with it.</summary>
    public static MetaSchemaPanelViewModel Build(
        uint classHash,
        IReadOnlyCollection<uint> presentPropertyHashes,
        Func<uint, IReadOnlyList<MetaProperty>>? declaredProperties,
        Func<uint, string?>? className,
        AddFieldHandler? addField = null,
        bool canEdit = false)
    {
        if (classHash == 0 || declaredProperties is null) return None;

        IReadOnlyList<MetaProperty> declared;
        try { declared = declaredProperties(classHash); }
        catch { return None; }
        if (declared.Count == 0) return None;

        var present = presentPropertyHashes as HashSet<uint> ?? new HashSet<uint>(presentPropertyHashes);
        var absent = declared.Where(d => !present.Contains(d.Hash)).ToList();

        string name;
        try { name = className?.Invoke(classHash) ?? $"0x{classHash:x8}"; }
        catch { name = $"0x{classHash:x8}"; }

        var panel = new MetaSchemaPanelViewModel
        {
            HasSchema = true,
            ClassDisplayName = name,
            DeclaredCount = declared.Count,
            CanEdit = canEdit && addField is not null,
            _add = addField,
        };
        foreach (var d in absent) panel.UnsetRows.Add(new MetaSchemaRowViewModel(d, panel));
        panel.PresentCount = declared.Count - absent.Count;
        panel.RefreshSummary();
        return panel;
    }

    internal (bool Ok, string? Reason) AddField(MetaSchemaRowViewModel row)
    {
        if (_add is null) return (false, "This document is read-only.");
        var p = row.Prop;
        if (!_add(p.Hash, p.FieldType, p.Default, out string? reason)) return (false, reason);
        PresentCount++;
        RefreshSummary();
        return (true, null);
    }

    private void RefreshSummary()
    {
        int stillAbsent = UnsetRows.Count(r => !r.Added);
        Summary = $"{PresentCount} of {DeclaredCount} fields authored — {stillAbsent} on defaults";
    }
}
