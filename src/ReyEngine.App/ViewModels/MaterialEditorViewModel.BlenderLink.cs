using System;
using System.Linq;
using ReyEngine.Formats.Interop;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Materials.ShaderGraph;
using MacroSupport = ReyEngine.Formats.MapGeo.LegacyMapPorter.MacroSupport;

namespace ReyEngine.App.ViewModels;

/// <summary>M841: what one edit from the Blender link did - applied, nothing to change, or refused with a reason.</summary>
internal readonly record struct LinkEditOutcome(bool Applied, string? Refusal)
{
    public static readonly LinkEditOutcome Done = new(true, null);
    public static readonly LinkEditOutcome Unchanged = new(false, null);
    public static LinkEditOutcome Refused(string reason) => new(false, reason);
    public bool IsRefused => Refusal is not null;
}

/// <summary>
/// M841: edits from the Blender link, applied through the SAME row view models and commands the Materials
/// tab uses - so each is one undo step, the rows show it, the material turns dirty and the normal Save writes
/// it. Nothing here writes a field directly.
/// </summary>
public sealed partial class MaterialEditorViewModel
{
    /// <summary>Apply one edit.</summary>
    internal LinkEditOutcome ApplyLinkEdit(BridgeMaterialEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (_doc is null) return LinkEditOutcome.Refused("no materials.bin is open in ReyEngine's Materials tab");
        var vm = Materials.FirstOrDefault(m => string.Equals(m.Model.Name, edit.Material, StringComparison.Ordinal))
              ?? Materials.FirstOrDefault(m => string.Equals(m.Model.Name, edit.Material, StringComparison.OrdinalIgnoreCase));
        if (vm is null) return LinkEditOutcome.Refused($"this materials.bin has no material '{edit.Material}'");
        if (vm.IsLinked) return LinkEditOutcome.Refused($"it is defined in the linked bin {vm.LinkedFromBin} and is read-only here");
        if (!vm.Model.IsStaticMaterialDef) return LinkEditOutcome.Refused("it is not a StaticMaterialDef");

        return edit.Kind switch
        {
            "param" => SetParameter(vm, edit),
            "sampler" => SetSampler(vm, edit),
            "address" => SetAddress(vm, edit),
            "switch" => SetSwitch(vm, edit),
            "macro" => vm.SetMacroFromLink(edit.Name, edit.Remove ? null : edit.Text),
            _ => LinkEditOutcome.Refused($"unknown edit kind '{edit.Kind}' (param, sampler, address, switch, macro)"),
        };
    }

    /// <summary>M841: a value typed into a row of the tab and not applied yet. Loading another bin, or writing the
    /// row from Blender, would throw it away without a word.</summary>
    internal string? UncommittedRowEdit()
    {
        foreach (var m in Materials)
        {
            foreach (var p in m.Parameters)
                if (!string.Equals(p.EditedText, p.Model.CurrentText, StringComparison.Ordinal))
                    return $"{m.Name}: parameter {p.Name}";
            foreach (var s in m.Slots)
                if (!string.Equals(s.EditedPath, s.Model.Path, StringComparison.Ordinal))
                    return $"{m.Name}: sampler {s.SamplerName}";
        }
        return null;
    }

    private static LinkEditOutcome SetParameter(MaterialBindingViewModel vm, BridgeMaterialEdit edit)
    {
        var row = vm.Parameters.FirstOrDefault(p => string.Equals(p.Name, edit.Name, StringComparison.Ordinal));
        if (row is null) return LinkEditOutcome.Refused($"the material has no parameter '{edit.Name}' - add it in ReyEngine first");
        if (!row.IsEditable) return LinkEditOutcome.Refused($"'{edit.Name}' is a {row.TypeName}, which cannot be edited");
        if (!string.Equals(row.EditedText, row.Model.CurrentText, StringComparison.Ordinal))
            return LinkEditOutcome.Refused($"'{edit.Name}' has an unapplied value typed in ReyEngine; apply or revert it there first");

        string text;
        if (edit.Values is { Count: > 0 } values)
        {
            // The wire always carries up to four numbers; a Vector2 or a float takes as many as it has.
            int components = row.Model.TryGetVector4(out _) ? row.Model.CurrentText.Split(',').Length : values.Count;
            if (values.Count < components) return LinkEditOutcome.Refused($"'{edit.Name}' needs {components} number(s), got {values.Count}");
            text = BlenderLinkMaterials.ParameterText(values.Take(components).ToList());
        }
        else if (edit.Text is { } t) text = t;
        else return LinkEditOutcome.Refused("a param edit needs a value");

        string before = row.Model.CurrentText;
        row.EditedText = text;
        row.ApplyCommand.Execute(null);
        if (row.HasError)
        {
            string error = row.ErrorText;
            row.ResetFromModel();
            return LinkEditOutcome.Refused(error);
        }
        return string.Equals(before, row.Model.CurrentText, StringComparison.Ordinal) ? LinkEditOutcome.Unchanged : LinkEditOutcome.Done;
    }

    private LinkEditOutcome SetSampler(MaterialBindingViewModel vm, BridgeMaterialEdit edit)
    {
        var slot = vm.Slots.FirstOrDefault(s => string.Equals(s.SamplerName, edit.Name, StringComparison.Ordinal));
        if (slot is null) return LinkEditOutcome.Refused($"the material has no sampler '{edit.Name}' - add it in ReyEngine first");
        if (edit.Text is not { Length: > 0 } raw) return LinkEditOutcome.Refused("a sampler edit needs a texture path");
        if (!string.Equals(slot.EditedPath, slot.Model.Path, StringComparison.Ordinal))
            return LinkEditOutcome.Refused($"'{edit.Name}' has an unapplied path typed in ReyEngine; apply or revert it there first");
        string path = raw.Trim();
        if (string.Equals(path, slot.Model.Path, StringComparison.OrdinalIgnoreCase)) return LinkEditOutcome.Unchanged;
        // the same test the row's red mark makes - asked BEFORE writing, so a path nothing answers never lands
        if (TextureExists is { } exists && !exists(path))
            return LinkEditOutcome.Refused($"'{path}' is neither in the project nor in the game");
        slot.EditedPath = path;
        slot.ApplyCommand.Execute(null);
        return slot.Unresolved
            ? LinkEditOutcome.Refused($"'{path}' does not resolve after writing it")
            : LinkEditOutcome.Done;
    }

    private static LinkEditOutcome SetAddress(MaterialBindingViewModel vm, BridgeMaterialEdit edit)
    {
        var slot = vm.Slots.FirstOrDefault(s => string.Equals(s.SamplerName, edit.Name, StringComparison.Ordinal));
        if (slot is null) return LinkEditOutcome.Refused($"the material has no sampler '{edit.Name}'");
        if (!slot.CanEditAddress) return LinkEditOutcome.Refused($"the sampler '{edit.Name}' has no address fields to edit");
        if (edit.Address.Count == 0) return LinkEditOutcome.Refused("an address edit names no axis (u, v, w)");
        foreach (var (axis, value) in edit.Address)
            if (value is > 2) return LinkEditOutcome.Refused($"address mode {value} is not 0 (Wrap), 1 (Clamp) or 2 (Mirror)");

        // The rows' own index form: 0 = not set, 1 = Wrap, 2 = Clamp, 3 = Mirror. Setting it pushes the
        // SamplerAddressEditCommand, exactly as the drop-down does.
        static int Index(int? mode) => mode is { } m ? m + 1 : 0;
        bool changed = false;
        foreach (var (axis, value) in edit.Address)
        {
            switch (axis)
            {
                case 'u': changed |= slot.Model.AddressU != value; slot.AddressUIndex = Index(value); break;
                case 'v': changed |= slot.Model.AddressV != value; slot.AddressVIndex = Index(value); break;
                case 'w': changed |= slot.Model.AddressW != value; slot.AddressWIndex = Index(value); break;
            }
        }
        return changed ? LinkEditOutcome.Done : LinkEditOutcome.Unchanged;
    }

    private static LinkEditOutcome SetSwitch(MaterialBindingViewModel vm, BridgeMaterialEdit edit)
    {
        var row = vm.Switches.FirstOrDefault(s => string.Equals(s.Name, edit.Name, StringComparison.Ordinal));
        if (row is null) return LinkEditOutcome.Refused($"the material has no switch '{edit.Name}' - add it in ReyEngine first");
        if (edit.On is not { } on) return LinkEditOutcome.Refused("a switch edit needs on: true or false");
        if (row.IsOn == on) return LinkEditOutcome.Unchanged;
        row.IsOn = on;
        return LinkEditOutcome.Done;
    }
}

public sealed partial class MaterialBindingViewModel
{
    /// <summary>
    /// M841: a shaderMacros define from the Blender link. REY_GRAPH goes through the graph assignment (one undo
    /// step that also brings the graph's custom parameters and textures, M834/M835); any other define is one
    /// <see cref="MacroPresenceCommand"/>, and a value the game ships no cooked shader for is refused (M486).
    /// </summary>
    internal LinkEditOutcome SetMacroFromLink(string name, string? value)
    {
        if (Owner is null) return LinkEditOutcome.Refused("the material is not in an editor");
        if (!CanEditMacros) return LinkEditOutcome.Refused("the material's macros cannot be edited");

        string? old = Model.AllMacros.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.Ordinal))?.Value;
        if (old == value) return LinkEditOutcome.Unchanged;

        if (SgShip.IsGraphMacro(name))
        {
            if (value is not null && !SgShipValidator.TryParseNumber(value, out _))
                return LinkEditOutcome.Refused($"{name} holds a Shader Graph NUMBER, not '{value}'");
            if (value is not null && !SgBase.IsSupported(Model.RenderShader ?? ""))
                return LinkEditOutcome.Refused($"{Model.RenderShader} is not a base shader a Shader Graph supports");
            PushGraphMacro(value);
            return LinkEditOutcome.Done;
        }

        if (value is not null && SupportFor(name, value) == MacroSupport.NotCooked)
            return LinkEditOutcome.Refused($"the game ships no cooked {Model.RenderShader} with {name}={value}; writing it would make the map fail to load");

        var cmd = new MacroPresenceCommand(Owner.DocContext, Model, name, old, value, RebuildMacros);
        cmd.Execute();
        if (value is not null && !Model.AllMacros.Any(m => m.Name == name && m.Value == value))
            return LinkEditOutcome.Refused($"{name} could not be written");
        Owner.UndoService?.PushApplied(cmd);
        RaiseDirty();
        Owner.NotifyChanged();
        return LinkEditOutcome.Done;
    }
}
