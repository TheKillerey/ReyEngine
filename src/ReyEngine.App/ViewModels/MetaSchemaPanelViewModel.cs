using System;
using System.Collections.Generic;
using System.Linq;
using ReyEngine.Core.Meta;

namespace ReyEngine.App.ViewModels;

/// <summary>M368: one property the class declares but the inspected object does not carry. Read-only - it
/// reports what the game falls back to, it does not add the field.</summary>
public sealed class MetaSchemaRowViewModel
{
    public MetaSchemaRowViewModel(MetaProperty p)
    {
        Name = p.HasName ? p.Name : $"0x{p.Hash:x8}";
        TypeName = p.KeyType.Length > 0 ? $"{p.FieldType}<{p.KeyType}>" : p.FieldType;
        // Raw JSON straight from the dump. Deliberately NOT reformatted into either editor's value syntax:
        // that would be a second, lossy type system over every bin type, for a reference display.
        DefaultText = p.Default ?? "(no default)";
        HasDefault = p.Default is not null;
    }

    public string Name { get; }
    public string TypeName { get; }
    public string DefaultText { get; }
    public bool HasDefault { get; }
}

/// <summary>
/// <para>M368: "what does this object NOT set, and what does the game use instead" - computed once here and
/// shown by BOTH the particle and material editors.</para>
///
/// <para>Shared rather than copied per editor on purpose. This codebase has now been bitten twice by
/// duplicated logic drifting apart (the dead sync/async scene-builder twins, and the ribbon extruder that
/// nearly got a second implementation), so the second consumer extracts rather than clones.</para>
///
/// <para><b>Absent is not the same as zero.</b> That is the whole point: VfxEmitterDefinitionData declares
/// 135 properties and 107 carry an authored default, so a field missing from a bin is almost always running
/// on a real value rather than nothing. Before this there was no way to see the difference.</para>
/// </summary>
public sealed class MetaSchemaPanelViewModel
{
    private MetaSchemaPanelViewModel() { }

    /// <summary>False when the meta database was never synced, the class is unknown to it, or it declares
    /// nothing. Consumers hide the panel entirely, so an editor with no meta database looks exactly as it
    /// did before this existed.</summary>
    public bool HasSchema { get; private init; }

    /// <summary>The resolved class name, so a header reading "0x45cd899f" becomes
    /// "VfxSystemDefinitionData".</summary>
    public string ClassDisplayName { get; private init; } = "";

    public int DeclaredCount { get; private init; }
    public int PresentCount { get; private init; }
    public IReadOnlyList<MetaSchemaRowViewModel> UnsetRows { get; private init; }
        = Array.Empty<MetaSchemaRowViewModel>();

    /// <summary>"42 of 135 fields authored - 93 on defaults".</summary>
    public string Summary { get; private init; } = "";

    /// <summary>The empty panel - what every consumer gets when there is no schema to show.</summary>
    public static readonly MetaSchemaPanelViewModel None = new();

    /// <summary>Build for one object. Never throws: the delegates come from an optional database, and a
    /// schema panel failing must never take an editor down with it.</summary>
    public static MetaSchemaPanelViewModel Build(
        uint classHash,
        IReadOnlyCollection<uint> presentPropertyHashes,
        Func<uint, IReadOnlyList<MetaProperty>>? declaredProperties,
        Func<uint, string?>? className)
    {
        if (classHash == 0 || declaredProperties is null) return None;

        IReadOnlyList<MetaProperty> declared;
        try { declared = declaredProperties(classHash); }
        catch { return None; }
        if (declared.Count == 0) return None;

        var present = presentPropertyHashes as HashSet<uint> ?? new HashSet<uint>(presentPropertyHashes);
        var absent = declared.Where(d => !present.Contains(d.Hash))
            .Select(d => new MetaSchemaRowViewModel(d))
            .ToList();

        string name;
        try { name = className?.Invoke(classHash) ?? $"0x{classHash:x8}"; }
        catch { name = $"0x{classHash:x8}"; }

        return new MetaSchemaPanelViewModel
        {
            HasSchema = true,
            ClassDisplayName = name,
            DeclaredCount = declared.Count,
            PresentCount = declared.Count - absent.Count,
            UnsetRows = absent,
            Summary = $"{declared.Count - absent.Count} of {declared.Count} fields authored "
                      + $"— {absent.Count} on defaults",
        };
    }
}
