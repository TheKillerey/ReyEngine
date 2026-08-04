using ReyEngine.Formats.Shaders;

namespace ReyEngine.Formats.Materials;

/// <summary>The exact changes made by one bulk material-shader conversion.</summary>
public sealed record MaterialShaderConversionResult(
    int MatchedMaterials,
    int ConvertedMaterials,
    int ExistingCompatibleSamplers,
    int AddedSamplers,
    int PreservedTexturePaths,
    IReadOnlyList<MaterialBinding> ChangedMaterials)
{
    public string Summary =>
        $"Converted {ConvertedMaterials:n0} of {MatchedMaterials:n0} material(s); "
        + $"kept {ExistingCompatibleSamplers:n0} matching sampler binding(s), added {AddedSamplers:n0} missing sampler(s)"
        + (PreservedTexturePaths > 0
            ? $", and copied {PreservedTexturePaths:n0} compatible texture path(s) into renamed slots."
            : ".");
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
            changed.Add(material);
        }

        return new MaterialShaderConversionResult(
            matches.Count, changed.Count, existing, added, preserved, changed);
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
