using ReyEngine.Core.Undo;

namespace ReyEngine.Formats.Materials;

/// <summary>
/// M645: the shared inspector's arithmetic for a selection of materials - what they have in common, and
/// the edits that write all of them as ONE undo step.
///
/// <para>Unity's rule, as chosen for this editor: a field shows its value when every selected material
/// agrees, "mixed" when they differ, and an edit writes every material that HAS the field. A material
/// without the sampler / parameter / switch / macro is left alone rather than given one - bulk editing
/// changes values, not structure. The edits apply live, the way the single-material rows do, and return
/// the composite that records them; only the materials that actually changed are in it, so an empty
/// step is never pushed. Pure over <see cref="MaterialBinding"/> so it is testable without a window.</para>
/// </summary>
public static class MaterialBulkEdit
{
    /// <summary>A text-valued field across the selection: the value every material that has it agrees on,
    /// or null when they differ.</summary>
    public sealed record Field(string Name, string? Shared, int PresentOn, int Of)
    {
        public bool Mixed => Shared is null;
        public bool OnAll => PresentOn == Of;
    }

    /// <summary>A boolean field across the selection: the state every material that has it agrees on, or
    /// null when they differ.</summary>
    public sealed record Toggle(string Name, bool? Shared, int PresentOn, int Of)
    {
        public bool Mixed => Shared is null;
        public bool OnAll => PresentOn == Of;
    }

    public sealed record Summary(
        int Count,
        string? Shader, bool ShaderMixed, int CanChangeShader,
        IReadOnlyList<Field> Samplers,
        IReadOnlyList<Field> Parameters,
        IReadOnlyList<Toggle> Switches,
        IReadOnlyList<Toggle> Macros,
        int CanEditRenderState,
        bool? Cull, bool? Blend,
        int? SrcBlend, int? DstBlend,
        int Dirty);

    public static Summary Summarize(IReadOnlyList<MaterialBinding> materials)
    {
        int n = materials.Count;
        var shaders = materials.Select(m => m.RenderShader ?? m.ShaderName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var samplers = Union(materials, m => m.Slots.Select(s => (s.SamplerName, s.Path)));
        var parameters = Union(materials, m => m.Parameters.Select(p => (p.Name, p.CurrentText)));
        var switches = UnionBool(materials, m => m.AllSwitches.Select(s => (s.Name, s.On)));
        var macros = UnionBool(materials, m => m.AllMacros.Select(x => (x.Name, x.On)));

        var stateful = materials.Where(m => m.CanEditRenderState).ToList();
        bool? cull = SharedBool(stateful.Select(m => m.GetPassBool("cullEnable", whenAbsent: true)));
        bool? blend = SharedBool(stateful.Select(m => m.GetPassBool("blendEnable", whenAbsent: false)));
        int? src = SharedInt(stateful.Select(m => m.GetPassU32("srcColorBlendFactor")));
        int? dst = SharedInt(stateful.Select(m => m.GetPassU32("dstColorBlendFactor")));

        return new Summary(n,
            shaders.Count == 1 ? shaders[0] : null, shaders.Count > 1, materials.Count(m => m.CanChangeShader),
            samplers, parameters, switches, macros,
            stateful.Count, cull, blend, src, dst,
            materials.Count(m => m.IsDirty));
    }

    private static IReadOnlyList<Field> Union(IReadOnlyList<MaterialBinding> materials,
        Func<MaterialBinding, IEnumerable<(string Name, string Value)>> pick)
    {
        var order = new List<string>();
        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in materials)
            foreach (var (name, value) in pick(m))
            {
                if (!values.TryGetValue(name, out var list)) { values[name] = list = new List<string>(); order.Add(name); }
                list.Add(value ?? "");
            }
        return order.Select(name =>
        {
            var list = values[name];
            var distinct = list.Distinct(StringComparer.Ordinal).ToList();
            return new Field(name, distinct.Count == 1 ? distinct[0] : null, list.Count, materials.Count);
        }).ToList();
    }

    private static IReadOnlyList<Toggle> UnionBool(IReadOnlyList<MaterialBinding> materials,
        Func<MaterialBinding, IEnumerable<(string Name, bool On)>> pick)
    {
        var order = new List<string>();
        var values = new Dictionary<string, List<bool>>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in materials)
            foreach (var (name, on) in pick(m))
            {
                if (!values.TryGetValue(name, out var list)) { values[name] = list = new List<bool>(); order.Add(name); }
                list.Add(on);
            }
        return order.Select(name =>
        {
            var list = values[name];
            return new Toggle(name, SharedBool(list), list.Count, materials.Count);
        }).ToList();
    }

    private static bool? SharedBool(IEnumerable<bool> values)
    {
        var d = values.Distinct().ToList();
        return d.Count == 1 ? d[0] : null;
    }

    private static int? SharedInt(IEnumerable<int> values)
    {
        var d = values.Distinct().ToList();
        return d.Count == 1 ? d[0] : null;
    }

    // ===================================================== the edits

    /// <summary>Write <paramref name="path"/> into every material's sampler of that name. Materials without
    /// the sampler are left alone.</summary>
    public static CompositeCommand SetSamplerPath(IReadOnlyList<MaterialBinding> materials, string samplerName, string path,
        object? context, Action<TextureSlot>? onApplied)
    {
        var commands = new List<IEditorCommand>();
        foreach (var m in materials)
            foreach (var slot in m.Slots.Where(s => s.SamplerName.Equals(samplerName, StringComparison.OrdinalIgnoreCase)))
            {
                string old = slot.Path;
                if (string.Equals(old, path, StringComparison.Ordinal)) continue;
                slot.SetPath(path);
                onApplied?.Invoke(slot);
                commands.Add(new TexturePathEditCommand(context, slot, old, path, _ => onApplied?.Invoke(slot)));
            }
        return new CompositeCommand($"Set {samplerName} on {materials.Count} Materials", commands, context);
    }

    /// <summary>Write <paramref name="text"/> into every material's parameter of that name. A value one of
    /// them will not take (wrong shape for its type) rolls the whole batch back and throws, so the
    /// selection is never left half-edited.</summary>
    public static CompositeCommand SetParameter(IReadOnlyList<MaterialBinding> materials, string name, string text,
        object? context, Action<MaterialParameter>? onApplied)
    {
        var commands = new List<IEditorCommand>();
        foreach (var m in materials)
            foreach (var p in m.Parameters.Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && x.IsEditable))
            {
                string old = p.CurrentText;
                try { p.Apply(text); }
                catch (Exception ex)
                {
                    for (int i = commands.Count - 1; i >= 0; i--) commands[i].Undo();
                    throw new InvalidOperationException($"{m.Name} / {name}: {ex.Message}", ex);
                }
                string now = p.CurrentText;
                if (string.Equals(old, now, StringComparison.Ordinal)) continue;
                onApplied?.Invoke(p);
                commands.Add(new MaterialParamEditCommand(context, p, old, now, _ => onApplied?.Invoke(p)));
            }
        return new CompositeCommand($"Set {name} on {materials.Count} Materials", commands, context);
    }

    /// <summary>Turn a feature switch on or off on every material that has it.</summary>
    public static CompositeCommand SetSwitch(IReadOnlyList<MaterialBinding> materials, string name, bool on,
        object? context, Action? onApplied)
    {
        var commands = new List<IEditorCommand>();
        foreach (var m in materials)
            foreach (var sw in m.AllSwitches.Where(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                if (sw.On == on) continue;
                sw.SetOn(on);
                commands.Add(new SwitchEditCommand(context, sw, !on, on, onApplied));
            }
        onApplied?.Invoke();
        return new CompositeCommand($"{(on ? "Enable" : "Disable")} {name} on {materials.Count} Materials", commands, context);
    }

    /// <summary>Set a shader macro to 1 or 0 on every material that HAS it. Absent stays absent - that is a
    /// different state (the shader's own default), and adding a define is a structural decision the single
    /// row makes with the permutation verdict in view.</summary>
    public static CompositeCommand SetMacro(IReadOnlyList<MaterialBinding> materials, string name, bool on,
        object? context, Action? onApplied)
    {
        var commands = new List<IEditorCommand>();
        foreach (var m in materials)
        {
            var macro = m.AllMacros.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (macro is null || macro.On == on) continue;
            string old = macro.Value;
            string value = on ? "1" : "0";
            if (m.SetMacroValue(name, value) is null) continue;
            commands.Add(new MacroEditCommand(context, m, name, old, value, onApplied));
        }
        onApplied?.Invoke();
        return new CompositeCommand($"{(on ? "Enable" : "Disable")} {name} on {materials.Count} Materials", commands, context);
    }

    /// <summary>pass.cullEnable / pass.blendEnable on every material with a technique pass.</summary>
    public static CompositeCommand SetPassBool(IReadOnlyList<MaterialBinding> materials, string field, bool value, bool whenAbsent,
        object? context, Action? onApplied)
    {
        var commands = new List<IEditorCommand>();
        foreach (var m in materials.Where(x => x.CanEditRenderState))
        {
            bool old = m.GetPassBool(field, whenAbsent);
            if (old == value) continue;
            if (!m.SetPassBool(field, value)) continue;
            commands.Add(new PassBoolEditCommand(context, m, field, old, value, onApplied));
        }
        onApplied?.Invoke();
        return new CompositeCommand($"Set {field} on {materials.Count} Materials", commands, context);
    }

    /// <summary>One half of the blend equation on every material with a pass; -1 REMOVES the field (absent is
    /// not 0 - M511). Colour and alpha move together, as every shipped material authors them (M415).</summary>
    public static CompositeCommand SetBlendFactor(IReadOnlyList<MaterialBinding> materials, string half, int factor,
        object? context, Action? onApplied)
    {
        var commands = new List<IEditorCommand>();
        foreach (var m in materials.Where(x => x.CanEditRenderState))
        {
            int old = m.GetPassU32(half + "ColorBlendFactor");
            if (old == factor) continue;
            BlendFactorEditCommand.Write(m, half, factor);
            commands.Add(new BlendFactorEditCommand(context, m, half, old, factor, onApplied));
        }
        onApplied?.Invoke();
        return new CompositeCommand($"Set {half} blend on {materials.Count} Materials", commands, context);
    }
}
