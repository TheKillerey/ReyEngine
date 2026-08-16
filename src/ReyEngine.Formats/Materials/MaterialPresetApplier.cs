using MacroSupport = ReyEngine.Formats.MapGeo.LegacyMapPorter.MacroSupport;

namespace ReyEngine.Formats.Materials;

/// <summary>One field a preset will change on one material.</summary>
public sealed record MaterialPresetChange(string Field, string From, string To)
{
    public override string ToString() => $"{Field}: {From} -> {To}";
}

/// <summary>The result of planning (or performing) a preset application against one material.
/// <para><see cref="Changes"/> is what WILL happen, <see cref="Blockers"/> is what will NOT and why, and
/// <see cref="Notes"/> is what is being left alone. All three are shown before anything is written: the
/// whole point of M504 is that a bulk edit is previewed, because the last time material state was copied
/// wholesale (M482) it turned 75 meshes black and nothing said so until the game was launched.</para>
/// </summary>
public sealed record MaterialPresetPlanRow(
    string MaterialName,
    IReadOnlyList<MaterialPresetChange> Changes,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Notes)
{
    public bool HasChanges => Changes.Count > 0;
    public bool HasBlockers => Blockers.Count > 0;

    public string Summary => Changes.Count == 0
        ? (Blockers.Count > 0 ? "nothing applicable" : "already matches")
        : $"{Changes.Count} change(s)" + (Blockers.Count > 0 ? $", {Blockers.Count} skipped" : "");
}

/// <summary>
/// What the applier needs to look things up. Everything is optional and an absent lookup SKIPS its check
/// rather than guessing — the same rule as <see cref="MapMaterialAudit"/>.
/// </summary>
/// <param name="TextureExists">Whether a texture path resolves. Used for notes, never to block: the user
/// may well be about to add the file.</param>
/// <param name="MacroSupport">
/// (shader, switches, macro, value) -> what the shader cache says. The switch set is passed because the
/// permutation key combines every axis at once — asking about a macro without the switches that will be on
/// the material answers a different question, which is the mistake M486 and then M502 both made.
/// Null skips macro checking entirely and adds one note saying so.
/// </param>
public sealed record MaterialPresetContext(
    Func<string, bool>? TextureExists = null,
    Func<string, IReadOnlyDictionary<string, bool>, string, string, MacroSupport>? MacroSupport = null);

/// <summary>
/// M504: stamps a <see cref="MaterialPreset"/> onto materials, field by field, with a preview first.
///
/// <para><see cref="Plan"/> and <see cref="Apply"/> run the SAME code — one walk, with writing switched off
/// or on. That is deliberate and load-bearing: a preview that is computed by different code than the edit
/// is a preview that can lie, and this is the tool people will point at twenty materials at once.</para>
/// </summary>
public static class MaterialPresetApplier
{
    /// <summary>Dry run: what would change, what would be refused, what would be left alone.</summary>
    public static MaterialPresetPlanRow Plan(MaterialPreset preset, MaterialBinding target,
        MaterialPresetParts parts, MaterialPresetContext? context = null)
        => Walk(preset, target, parts, context, write: false);

    public static IReadOnlyList<MaterialPresetPlanRow> Plan(MaterialPreset preset,
        IEnumerable<MaterialBinding> targets, MaterialPresetParts parts, MaterialPresetContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        return targets.Select(t => Plan(preset, t, parts, context)).ToList();
    }

    /// <summary>Write the plan. Returns the same shape as <see cref="Plan"/>, describing what actually
    /// happened — a parameter whose text the target's value type rejects can only be discovered by trying,
    /// so the applied row is not always identical to the planned one.</summary>
    public static MaterialPresetPlanRow Apply(MaterialPreset preset, MaterialBinding target,
        MaterialPresetParts parts, MaterialPresetContext? context = null)
        => Walk(preset, target, parts, context, write: true);

    public static IReadOnlyList<MaterialPresetPlanRow> Apply(MaterialPreset preset,
        IEnumerable<MaterialBinding> targets, MaterialPresetParts parts, MaterialPresetContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        return targets.Select(t => Apply(preset, t, parts, context)).ToList();
    }

    private static MaterialPresetPlanRow Walk(MaterialPreset preset, MaterialBinding target,
        MaterialPresetParts parts, MaterialPresetContext? context, bool write)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(target);
        context ??= new MaterialPresetContext();

        var changes = new List<MaterialPresetChange>();
        var blockers = new List<string>();
        var notes = new List<string>();

        // The bindings that have no StaticMaterialDef behind them (the inline and skin-default ones) have
        // nowhere to write anything at all. Say it once instead of six times.
        if (!target.IsStaticMaterialDef)
        {
            blockers.Add("not a StaticMaterialDef — this binding has no bin object to write to");
            return new MaterialPresetPlanRow(target.Name, changes, blockers, notes);
        }

        string currentShader = target.RenderShader ?? "";
        bool wantShader = parts.HasFlag(MaterialPresetParts.Shader)
                          && preset.Shader.Length > 0
                          && !preset.Shader.Equals(currentShader, StringComparison.OrdinalIgnoreCase);

        // ---- macro safety, resolved BEFORE anything is written ------------------------------------
        //
        // Macros and the shader are one decision, not two: the permutation key the client looks up is the
        // shader plus every define at once. Changing the shader under a macro the new shader never cooked is
        // M486 exactly ("Unable to find correct hash for shader ... Failed to compile shader"), and it is
        // invisible until the map is loaded in game.
        bool replacingMacros = parts.HasFlag(MaterialPresetParts.Macros);
        string targetShader = wantShader ? preset.Shader : currentShader;

        // The switch set the material will HAVE when the macros are resolved, not the one it has now.
        var effectiveSwitches = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var (n, on) in target.Switches) effectiveSwitches[n] = on;
        if (parts.HasFlag(MaterialPresetParts.Switches) && target.CanEditSwitches)
            foreach (var (n, on) in preset.Switches) effectiveSwitches[n] = on;

        var ask = context.MacroSupport;
        if (ask is null && (replacingMacros ? preset.Macros.Count > 0 : target.Macros.Count > 0))
            notes.Add("macro safety not checked — no shader cache available");

        if (wantShader && !replacingMacros && ask is not null)
        {
            // Macros the target KEEPS, judged against the shader it is about to get.
            foreach (var (macro, value) in target.Macros)
            {
                var support = ask(targetShader, effectiveSwitches, macro, value);
                if (support == MacroSupport.NotCooked)
                {
                    blockers.Add($"shader: {Short(preset.Shader)} ships no cooked permutation for this "
                               + $"material's {macro}={value} — the client would fail to compile it. "
                               + "Clear the macro first, or include Macros in the preset.");
                    wantShader = false;
                    targetShader = currentShader;
                    break;
                }
                if (support == MacroSupport.NotDeclared)
                    notes.Add($"{macro} becomes inert on {Short(preset.Shader)} — it is not a permutation "
                            + "axis there, so the client will ignore it");
            }
        }

        // ---- shader --------------------------------------------------------------------------------
        if (parts.HasFlag(MaterialPresetParts.Shader) && preset.Shader.Length > 0)
        {
            if (!target.CanChangeShader)
                blockers.Add("shader: this material has no technique pass to point at a shader");
            else if (wantShader)
            {
                changes.Add(new MaterialPresetChange("shader",
                    currentShader.Length > 0 ? Short(currentShader) : "(none)", Short(preset.Shader)));
                if (write) target.SetRenderShader(preset.Shader);
            }
        }

        // ---- samplers ------------------------------------------------------------------------------
        //
        // Additive: a sampler the target has and the preset does not is LEFT, not removed. Unbinding a
        // texture has no way to be safer than doing nothing, and a preset captured from a simpler material
        // would otherwise strip masks and normal maps off everything it touched.
        if (parts.HasFlag(MaterialPresetParts.Samplers) && preset.Samplers.Count > 0)
        {
            if (!target.CanEditSamplers)
                blockers.Add("samplers: this material cannot carry a sampler container");
            else
                foreach (var s in preset.Samplers)
                {
                    var slot = target.Slots.FirstOrDefault(x =>
                        x.SamplerName.Equals(s.Name, StringComparison.OrdinalIgnoreCase));

                    if (context.TextureExists is { } exists && s.Path.Length > 0 && !exists(s.Path))
                        notes.Add($"{s.Name}: {s.Path} does not resolve — the material will draw untextured "
                                + "until the file is there");

                    if (slot is null)
                    {
                        changes.Add(new MaterialPresetChange($"sampler:{s.Name}", "(absent)", s.Path));
                        if (write)
                        {
                            slot = target.AddSampler(s.Name, s.Path);
                            if (slot is null)
                            {
                                changes.RemoveAt(changes.Count - 1);
                                blockers.Add($"sampler:{s.Name}: could not be added to this material");
                                continue;
                            }
                            WriteAddresses(slot, s, changes, notes, compare: false);
                        }
                        else
                        {
                            // The addresses land with the sampler; listing them separately on an ADD would
                            // read as three extra edits to something that does not exist yet.
                        }
                        continue;
                    }

                    if (!string.Equals(slot.Path, s.Path, StringComparison.Ordinal))
                    {
                        changes.Add(new MaterialPresetChange($"sampler:{s.Name}", slot.Path, s.Path));
                        if (write) slot.SetPath(s.Path);
                    }
                    WriteAddresses(slot, s, changes, notes, compare: true, write: write);
                }

            foreach (var extra in target.Slots)
                if (!preset.Samplers.Any(s => s.Name.Equals(extra.SamplerName, StringComparison.OrdinalIgnoreCase)))
                    notes.Add($"{extra.SamplerName} is not in the preset and is kept as it is");
        }

        // ---- parameters ----------------------------------------------------------------------------
        if (parts.HasFlag(MaterialPresetParts.Parameters) && preset.Parameters.Count > 0)
        {
            if (!target.CanEditParameters)
                blockers.Add("parameters: this material cannot carry a parameter container");
            else
                foreach (var p in preset.Parameters)
                {
                    var existing = target.Parameters.FirstOrDefault(x =>
                        x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase));

                    if (existing is not null)
                    {
                        if (string.Equals(existing.CurrentText, p.Text, StringComparison.Ordinal)) continue;
                        if (!existing.IsEditable)
                        {
                            blockers.Add($"param:{p.Name}: this material stores it in a type the value "
                                       + "editor cannot write");
                            continue;
                        }
                        changes.Add(new MaterialPresetChange($"param:{p.Name}", existing.CurrentText, p.Text));
                        if (write)
                        {
                            try { existing.Apply(p.Text); }
                            catch (Exception ex)
                            {
                                changes.RemoveAt(changes.Count - 1);
                                blockers.Add($"param:{p.Name}: '{p.Text}' is not valid for this material's "
                                           + $"{existing.TypeName} ({ex.Message})");
                            }
                        }
                        continue;
                    }

                    changes.Add(new MaterialPresetChange($"param:{p.Name}", "(absent)", p.Text));
                    if (!write) continue;

                    // A new parameter is cloned from whatever this material already has, so its VALUE TYPE
                    // may not match the preset's. Trying is the only way to find out; a failure is undone
                    // rather than left as a parameter holding someone else's number.
                    var added = target.AddParameter(p.Name);
                    if (added is null)
                    {
                        changes.RemoveAt(changes.Count - 1);
                        blockers.Add($"param:{p.Name}: could not be added to this material");
                        continue;
                    }
                    try { added.Apply(p.Text); }
                    catch (Exception ex)
                    {
                        target.RemoveParameter(added);
                        changes.RemoveAt(changes.Count - 1);
                        blockers.Add($"param:{p.Name}: '{p.Text}' is not valid for the {added.TypeName} this "
                                   + $"material would store it as ({ex.Message})");
                    }
                }
        }

        // ---- switches ------------------------------------------------------------------------------
        if (parts.HasFlag(MaterialPresetParts.Switches) && preset.Switches.Count > 0)
        {
            if (!target.CanEditSwitches)
                blockers.Add("switches: this material cannot carry a switch container");
            else
                foreach (var (name, on) in preset.Switches)
                {
                    var sw = target.AllSwitches.FirstOrDefault(x =>
                        x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (sw is not null)
                    {
                        if (sw.On == on) continue;
                        changes.Add(new MaterialPresetChange($"switch:{name}", Onward(sw.On), Onward(on)));
                        if (write) sw.SetOn(on);
                        continue;
                    }

                    // An ABSENT switch is the shader's own default, which is not necessarily off — so it is
                    // added with the preset's value explicitly either way, rather than assuming that "off"
                    // means "leave it out".
                    changes.Add(new MaterialPresetChange($"switch:{name}", "(absent)", Onward(on)));
                    if (!write) continue;
                    var made = target.AddSwitch(name);
                    if (made is null)
                    {
                        changes.RemoveAt(changes.Count - 1);
                        blockers.Add($"switch:{name}: could not be added to this material");
                        continue;
                    }
                    made.SetOn(on);
                }
        }

        // ---- macros --------------------------------------------------------------------------------
        //
        // Macros REPLACE, unlike samplers and parameters. They are the field that actually breaks maps, so
        // a preset has to be able to clear a bad one — and removing a macro is safe in a way that removing
        // a texture is not: absent means "the shader's cooked default", which is by definition a
        // permutation the game ships.
        if (replacingMacros)
        {
            if (!target.CanEditMacros)
                blockers.Add("macros: this material cannot carry a shaderMacros map");
            else
            {
                foreach (var (macro, value) in preset.Macros)
                {
                    string current = target.Macros.TryGetValue(macro, out var v) ? v : "";
                    if (string.Equals(current, value, StringComparison.Ordinal)) continue;

                    if (ask is not null && targetShader.Length > 0)
                    {
                        var support = ask(targetShader, effectiveSwitches, macro, value);
                        if (support == MacroSupport.NotCooked)
                        {
                            blockers.Add($"macro:{macro}={value}: {Short(targetShader)} declares the axis but "
                                       + "ships no cooked permutation for it — writing it would make the "
                                       + "client fail to compile the shader (M486)");
                            continue;
                        }
                        if (support == MacroSupport.NotDeclared)
                        {
                            notes.Add($"macro:{macro} skipped — not a permutation axis of "
                                    + $"{Short(targetShader)}, so the client would ignore it");
                            continue;
                        }
                        if (support == MacroSupport.Unknown)
                            notes.Add($"macro:{macro} could not be verified against {Short(targetShader)}");
                    }

                    changes.Add(new MaterialPresetChange($"macro:{macro}",
                        current.Length > 0 ? current : "(absent)", value));
                    if (write) target.SetMacroValue(macro, value);
                }

                foreach (var macro in target.Macros.Keys.ToList())
                {
                    if (preset.Macros.ContainsKey(macro)) continue;
                    changes.Add(new MaterialPresetChange($"macro:{macro}",
                        target.Macros[macro], "(removed)"));
                    if (write) target.RemoveMacro(macro);
                }
            }
        }

        // ---- render state --------------------------------------------------------------------------
        if (parts.HasFlag(MaterialPresetParts.RenderState) && preset.RenderState is { } rs)
        {
            if (!target.CanEditRenderState)
                blockers.Add("render state: this material has no pass struct to write to");
            else
            {
                if (target.BlendEnable != rs.BlendEnable)
                {
                    changes.Add(new MaterialPresetChange("state:blendEnable",
                        Onward(target.BlendEnable), Onward(rs.BlendEnable)));
                    if (write) target.SetPassBool("blendEnable", rs.BlendEnable);
                }

                if (target.CullEnable != rs.CullEnable)
                {
                    changes.Add(new MaterialPresetChange("state:cullEnable",
                        target.CullEnable is { } c ? Onward(c) : "(absent)",
                        rs.CullEnable is { } n ? Onward(n) : "(absent)"));
                    if (write)
                    {
                        if (rs.CullEnable is { } value) target.SetPassBool("cullEnable", value);
                        else target.RemovePassProperty("cullEnable");
                    }
                }

                ApplyBlendFactor(target, "srcColorBlendFactor", target.SrcBlendFactor, rs.SrcBlendFactor,
                    changes, write);
                ApplyBlendFactor(target, "dstColorBlendFactor", target.DstBlendFactor, rs.DstBlendFactor,
                    changes, write);
            }
        }

        return new MaterialPresetPlanRow(target.Name, changes, blockers, notes);
    }

    private static void ApplyBlendFactor(MaterialBinding target, string field, int current, int wanted,
        List<MaterialPresetChange> changes, bool write)
    {
        if (current == wanted) return;
        changes.Add(new MaterialPresetChange($"state:{field}",
            current < 0 ? "(absent)" : current.ToString(), wanted < 0 ? "(absent)" : wanted.ToString()));
        if (!write) return;
        if (wanted < 0) target.RemovePassProperty(field);
        else target.SetPassU32(field, (uint)wanted);
    }

    /// <summary>Address modes on an existing slot. Absent stays distinct from 0 all the way through — see
    /// <see cref="TextureSlot.AddressU"/> for why that matters in the bytes.</summary>
    private static void WriteAddresses(TextureSlot slot, MaterialPresetSampler s,
        List<MaterialPresetChange> changes, List<string> notes, bool compare, bool write = true)
    {
        if (!slot.CanEditAddress)
        {
            if (compare && (s.AddressU is not null || s.AddressV is not null || s.AddressW is not null))
                notes.Add($"{s.Name}: address modes cannot be written on this slot");
            return;
        }

        Axis(TextureSlot.AddressAxis.U, slot.AddressU, s.AddressU);
        Axis(TextureSlot.AddressAxis.V, slot.AddressV, s.AddressV);
        Axis(TextureSlot.AddressAxis.W, slot.AddressW, s.AddressW);

        void Axis(TextureSlot.AddressAxis axis, int? current, int? wanted)
        {
            if (compare && current == wanted) return;
            if (compare)
                changes.Add(new MaterialPresetChange($"sampler:{s.Name}.address{axis}",
                    current?.ToString() ?? "(absent)", wanted?.ToString() ?? "(absent)"));
            if (write) slot.SetAddress(axis, wanted);
        }
    }

    private static string Onward(bool on) => on ? "on" : "off";

    private static string Short(string s)
    {
        if (s.Length == 0) return "(none)";
        int i = s.LastIndexOf('/');
        return i >= 0 ? s[(i + 1)..] : s;
    }
}
