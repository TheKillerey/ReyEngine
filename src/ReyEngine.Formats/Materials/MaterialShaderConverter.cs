using ReyEngine.Formats.Shaders;

namespace ReyEngine.Formats.Materials;

/// <summary>The exact changes made by one bulk material-shader conversion.</summary>
public sealed record MaterialShaderConversionResult(
    int MatchedMaterials,
    int ConvertedMaterials,
    int ExistingCompatibleSamplers,
    int AddedSamplers,
    int PreservedTexturePaths,
    IReadOnlyList<MaterialBinding> ChangedMaterials,
    /// <summary>M430: parameters the TARGET shader does not declare, removed. Leaving them behind is a
    /// shape Riot never ships (0 of 31,954 materials) and it is what broke the Mantis test material.</summary>
    int DroppedParameters = 0,
    int AddedParameters = 0,
    /// <summary>Switches created because the target declares staticSwitches. Every one of the 27,295
    /// shipped materials on such a shader carries a switches container.</summary>
    int AddedSwitches = 0)
{
    public string Summary =>
        $"Converted {ConvertedMaterials:n0} of {MatchedMaterials:n0} material(s); "
        + $"kept {ExistingCompatibleSamplers:n0} matching sampler binding(s), added {AddedSamplers:n0} missing sampler(s)"
        + (PreservedTexturePaths > 0
            ? $", copied {PreservedTexturePaths:n0} compatible texture path(s) into renamed slots"
            : "")
        + (DroppedParameters > 0 ? $", dropped {DroppedParameters:n0} parameter(s) the target does not declare" : "")
        + (AddedParameters > 0 ? $", added {AddedParameters:n0} parameter(s) from the target shader" : "")
        + (AddedSwitches > 0 ? $", added {AddedSwitches:n0} switch(es)" : "")
        + ".";
}

/// <summary>
/// Changes the technique-pass shader for every matching material without rebuilding the material object.
/// Keeping that object is important: its sampler values, parameters, switches, macros and render state all
/// survive byte-for-byte. Missing target samplers are added from the shader definition; when a target uses a
/// different conventional name (for example Diffuse_Texture instead of DiffuseTexture), the compatible
/// authored texture path is copied instead of replacing it with the shader's generic default.
/// </summary>
public static class MaterialShaderConverter
{
    private enum TextureRole { Unknown, Diffuse, Normal, Mask, Emissive, Gradient, MatCap, MatCapMask }

    public static MaterialShaderConversionResult Convert(
        IEnumerable<MaterialBinding> materials,
        string sourceShader,
        string targetShader,
        LeagueShaderDef? targetDefinition,
        IEnumerable<string>? learnedTargetSamplers = null)
    {
        string source = sourceShader.Trim();
        string target = targetShader.Trim();
        if (source.Length == 0) throw new ArgumentException("Source shader is required.", nameof(sourceShader));
        if (target.Length == 0) throw new ArgumentException("Target shader is required.", nameof(targetShader));
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase))
            return new MaterialShaderConversionResult(0, 0, 0, 0, 0, Array.Empty<MaterialBinding>());

        var targetSamplers = targetDefinition is not null
            ? targetDefinition.Textures
            : (learnedTargetSamplers ?? Array.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => new ShaderTextureDef(name, ""))
                .ToList();

        var matches = materials.Where(m => m.CanChangeShader
                && string.Equals(m.RenderShader, source, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var changed = new List<MaterialBinding>(matches.Count);
        int existing = 0, added = 0, preserved = 0;
        int droppedParams = 0, addedParams = 0, addedSwitches = 0;

        foreach (var material in matches)
        {
            if (!material.CanChangeShader || !material.SetRenderShader(target)) continue;

            // Snapshot before adding anything so one new alias cannot become the source for another.
            var authoredSlots = material.Slots.ToArray();
            foreach (var sampler in targetSamplers)
            {
                if (material.Slots.Any(s =>
                        s.SamplerName.Equals(sampler.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    existing++;
                    continue;
                }

                string? compatible = FindCompatibleTexturePath(authoredSlots, sampler.Name);
                string path = !string.IsNullOrWhiteSpace(compatible)
                    ? compatible
                    : sampler.DefaultTexturePath;
                if (material.AddSampler(sampler.Name, path) is null) continue;
                added++;
                if (!string.IsNullOrWhiteSpace(compatible)) preserved++;
            }

            ReconcileParameters(material, targetDefinition, ref droppedParams, ref addedParams);
            ReconcileSwitches(material, targetDefinition, ref addedSwitches);
            changed.Add(material);
        }

        return new MaterialShaderConversionResult(
            matches.Count, changed.Count, existing, added, preserved, changed,
            droppedParams, addedParams, addedSwitches);
    }

    /// <summary>
    /// M430: bring <c>paramValues</c> in line with the target shader.
    ///
    /// <para>Converting a material used to keep its parameters byte-for-byte, which strands the SOURCE
    /// shader's parameters on a target that never declares them. Measured over every shipped WAD: of
    /// <b>31,954</b> materials that set at least one paramValue, <b>0</b> set one their shader does not
    /// declare. That is the same 0-of-N standard that identified the Map453 defects, and it is exactly
    /// what the Mantis_Env_Baked_PBR test material hit - a leftover <c>TintColor</c> from
    /// DefaultEnv_Flat on a shader whose 39 parameters do not include it.</para>
    ///
    /// <para>Only runs when the target definition is known. Without it there is nothing to reconcile
    /// against and silently dropping parameters would be worse than leaving them.</para>
    /// </summary>
    private static void ReconcileParameters(
        MaterialBinding material, LeagueShaderDef? target, ref int dropped, ref int addedParams)
    {
        if (target is null || !material.CanEditParameters) return;
        var declared = new HashSet<string>(target.Parameters.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var p in material.Parameters.ToArray())
            if (!declared.Contains(p.Name) && material.RemoveParameter(p)) dropped++;

        var present = new HashSet<string>(material.Parameters.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var pd in target.Parameters)
        {
            if (present.Contains(pd.Name)) continue;
            // the shader's own default, so the material starts from what Riot authored rather than zero
            if (material.SetVectorParameter(pd.Name, new System.Numerics.Vector4(pd.X, pd.Y, pd.Z, pd.W)) is not null)
                addedParams++;
        }
    }

    /// <summary>
    /// M430: a material on a shader that declares staticSwitches always carries a <c>switches</c>
    /// container - <b>27,295 of 27,295</b> shipped materials, with zero exceptions. The Mantis test
    /// material had none, because the conversion path never created one.
    /// </summary>
    private static void ReconcileSwitches(MaterialBinding material, LeagueShaderDef? target, ref int addedSwitches)
    {
        if (target is null || target.StaticSwitches.Count == 0 || !material.CanEditSwitches) return;
        var present = new HashSet<string>(material.Switches.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (string name in target.StaticSwitches)
        {
            if (present.Contains(name)) continue;
            if (material.AddSwitch(name) is not null) addedSwitches++;
        }
    }

    /// <summary>Find an authored texture serving the same conventional role as a target sampler.</summary>
    public static string? FindCompatibleTexturePath(
        IEnumerable<TextureSlot> sourceSlots, string targetSamplerName)
    {
        var slots = sourceSlots.ToList();
        var exact = slots.FirstOrDefault(s =>
            s.SamplerName.Equals(targetSamplerName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact.Path;

        TextureRole role = Role(targetSamplerName);
        if (role == TextureRole.Unknown) return null;
        return slots.FirstOrDefault(s => Role(s.SamplerName) == role
                                      && !string.IsNullOrWhiteSpace(s.Path))?.Path;
    }

    private static TextureRole Role(string name)
    {
        string n = name.Replace("_", "", StringComparison.Ordinal)
                       .Replace("-", "", StringComparison.Ordinal)
                       .ToLowerInvariant();
        return n switch
        {
            "matcapmask" or "matcapmasktex" or "matcapmasktexture" => TextureRole.MatCapMask,
            "matcap" or "matcaptex" or "matcaptexture" => TextureRole.MatCap,
            "normal" or "normaltex" or "normalmap" or "normaltexture"
                or "nrm" or "nrmtex" or "nrmmap" or "nrmtexture" or "normalnm" => TextureRole.Normal,
            "emission" or "emissiontex" or "emissiontexture"
                or "emissive" or "emissivetex" or "emissivetexture"
                or "glow" or "glowtex" or "glowtexture"
                or "illum" or "illumtex" or "illumtexture" => TextureRole.Emissive,
            "gradient" or "gradienttex" or "gradienttexture"
                or "gredient" or "gredienttex" or "gredienttexture" => TextureRole.Gradient,
            "mask" or "masktex" or "maskmap" or "masktexture"
                or "colormask" or "colormasktex" or "colormasktexture"
                or "opacitymask" or "opacitymasktex" or "opacitymasktexture" => TextureRole.Mask,
            "diffuse" or "diffusetex" or "diffusemap" or "diffusetexture"
                or "albedo" or "albedotex" or "albedomap" or "albedotexture"
                or "basecolor" or "basecolortex" or "basecolormap" or "basecolortexture"
                or "main" or "maintex" or "maintexture" => TextureRole.Diffuse,
            _ => TextureRole.Unknown,
        };
    }
}
