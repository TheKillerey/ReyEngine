using System.Globalization;
using System.Numerics;
using System.Text;
using ReyEngine.Formats.Shaders;

namespace ReyEngine.Formats.Materials;

public sealed record ShaderMaterialSetupApplyResult(
    int Parameters, int Switches, int Macros, int RemovedObsoleteValues)
{
    public string Summary =>
        $"Applied {Parameters:n0} parameter(s), {Switches:n0} switch(es) and {Macros:n0} macro(s)"
        + (RemovedObsoleteValues == 0 ? "." : $"; removed {RemovedObsoleteValues:n0} setting(s) from the previous shader.");
}

/// <summary>Captures and applies real StaticMaterialDef state independently of texture paths. The Workshop
/// uses the canonical signature to count identical setups; the Material Editor applies the winning setup
/// without replacing the user's diffuse/normal/mask assets.</summary>
public static class ShaderMaterialSetups
{
    public static ShaderMaterialSetup Capture(MaterialBinding material) => new(
        material.Parameters.Where(p => p.TryGetVector4(out _)).ToDictionary(
            p => p.Name, p => { p.TryGetVector4(out var value); return value; }, StringComparer.OrdinalIgnoreCase),
        material.AllSwitches.ToDictionary(s => s.Name, s => s.On, StringComparer.OrdinalIgnoreCase),
        material.AllMacros.ToDictionary(m => m.Name, m => m.Value, StringComparer.OrdinalIgnoreCase),
        material.BlendEnable, material.CullEnable, material.SrcBlendFactor, material.DstBlendFactor)
    { ExampleMaterial = material.Name };

    public static string CanonicalSignature(ShaderMaterialSetup setup)
    {
        var text = new StringBuilder();
        text.Append(setup.BlendEnable ? '1' : '0').Append('|')
            .Append(setup.CullEnable is null ? 'x' : setup.CullEnable.Value ? '1' : '0').Append('|')
            .Append(setup.SourceBlendFactor).Append('|').Append(setup.DestinationBlendFactor);
        foreach (var pair in setup.Parameters.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var v = pair.Value;
            text.Append("|p:").Append(pair.Key.ToLowerInvariant()).Append('=')
                .Append(BitConverter.SingleToInt32Bits(v.X).ToString("x8", CultureInfo.InvariantCulture)).Append(',')
                .Append(BitConverter.SingleToInt32Bits(v.Y).ToString("x8", CultureInfo.InvariantCulture)).Append(',')
                .Append(BitConverter.SingleToInt32Bits(v.Z).ToString("x8", CultureInfo.InvariantCulture)).Append(',')
                .Append(BitConverter.SingleToInt32Bits(v.W).ToString("x8", CultureInfo.InvariantCulture));
        }
        foreach (var pair in setup.Switches.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            text.Append("|s:").Append(pair.Key.ToLowerInvariant()).Append('=').Append(pair.Value ? '1' : '0');
        foreach (var pair in setup.Macros.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            text.Append("|m:").Append(pair.Key.ToLowerInvariant()).Append('=').Append(pair.Value);
        return text.ToString();
    }

    public static ShaderMaterialSetupApplyResult Apply(
        MaterialBinding material, LeagueShaderDef shader, ShaderMaterialSetup setup)
    {
        var declaredParameters = shader.Parameters.Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        int removed = 0, parameters = 0, switches = 0, macros = 0;

        // A parameter from the previous shader is harmless to the GPU but misleading in the editor and
        // can be mistaken for a supported control, so remove it when the new shader does not declare it.
        foreach (var parameter in material.Parameters
                     .Where(p => p.TryGetVector4(out _) && !declaredParameters.Contains(p.Name)).ToList())
            if (material.RemoveParameter(parameter)) removed++;

        foreach (var (name, value) in setup.Parameters)
            if (declaredParameters.Contains(name) && material.SetVectorParameter(name, value) is not null)
                parameters++;

        var featureResult = ApplyFeaturesAndRenderState(material, setup);
        switches += featureResult.Switches;
        macros += featureResult.Macros;
        removed += featureResult.Removed;

        return new ShaderMaterialSetupApplyResult(parameters, switches, macros, removed);
    }

    /// <summary>Restore an earlier captured setup exactly. This is used by the editor's one-step bulk undo;
    /// unlike <see cref="Apply"/>, its parameter allow-list is the snapshot itself rather than shaders.bin.</summary>
    public static void Restore(MaterialBinding material, ShaderMaterialSetup snapshot)
    {
        foreach (var parameter in material.Parameters
                     .Where(p => p.TryGetVector4(out _) && !snapshot.Parameters.ContainsKey(p.Name)).ToList())
            material.RemoveParameter(parameter);
        foreach (var (name, value) in snapshot.Parameters)
            material.SetVectorParameter(name, value);
        ApplyFeaturesAndRenderState(material, snapshot);
    }

    private static (int Switches, int Macros, int Removed) ApplyFeaturesAndRenderState(
        MaterialBinding material, ShaderMaterialSetup setup)
    {
        int removed = 0, switches = 0, macros = 0;
        foreach (var old in material.AllSwitches.ToList())
            if (!setup.Switches.ContainsKey(old.Name) && material.RemoveSwitch(old)) removed++;
        foreach (var (name, on) in setup.Switches)
        {
            var entry = material.AllSwitches.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        ?? material.AddSwitch(name);
            if (entry is null) continue;
            entry.SetOn(on);
            switches++;
        }

        foreach (var old in material.AllMacros.ToList())
            if (!setup.Macros.ContainsKey(old.Name) && material.RemoveMacro(old.Name)) removed++;
        foreach (var (name, value) in setup.Macros)
            if (material.SetMacroValue(name, value) is not null) macros++;

        material.SetPassBool("blendEnable", setup.BlendEnable);
        if (setup.CullEnable is { } cull) material.SetPassBool("cullEnable", cull);
        else material.RemovePassProperty("cullEnable");
        if (setup.SourceBlendFactor >= 0)
            material.SetPassU32("srcColorBlendFactor", (uint)setup.SourceBlendFactor);
        else material.RemovePassProperty("srcColorBlendFactor");
        if (setup.DestinationBlendFactor >= 0)
            material.SetPassU32("dstColorBlendFactor", (uint)setup.DestinationBlendFactor);
        else material.RemovePassProperty("dstColorBlendFactor");

        return (switches, macros, removed);
    }
}
