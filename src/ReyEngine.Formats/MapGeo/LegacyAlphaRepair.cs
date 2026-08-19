using ReyEngine.Formats.Materials;

namespace ReyEngine.Formats.MapGeo;

/// <summary>What the repair decided for one material.</summary>
/// <param name="Discarded">Share of the diffuse an alpha test at the material's cutoff would throw
/// away. This is the number that makes the case: a surface losing half its pixels is not a cutout.</param>
public sealed record LegacyAlphaRepairRow(
    string Material, string Texture, LegacyAlphaKind Kind, double Discarded, bool Changed, string Reason);

public sealed record LegacyAlphaRepairResult(IReadOnlyList<LegacyAlphaRepairRow> Rows)
{
    public int Changed => Rows.Count(r => r.Changed);
    public int Examined => Rows.Count;

    /// <summary>One line a user can read without knowing any of this.</summary>
    public string Summary =>
        Changed == 0
            ? $"{Examined} material(s) examined; none were alpha-testing a surface they should not."
            : $"{Changed} of {Examined} material(s) moved off the alpha-tested shader - their diffuse alpha is "
              + "gloss or solid, not a cutout mask, so the test was discarding the surface.";
}

/// <summary>
/// Repairs an ALREADY PORTED map whose ordinary surfaces were given the alpha-tested shader (M528).
///
/// <para>M528 fixed the porter, which is the right place for it - but a map already ported carries the
/// damage in its materials.bin, and re-porting is not always what someone wants to do to a project they
/// have since edited. This applies the same measured rule to an existing document: a material still
/// pointed at <c>DefaultEnv_Flat_AlphaTest</c> whose diffuse alpha is NOT a cutout mask is moved to
/// <c>DefaultEnv_Flat</c>.</para>
///
/// <para>It changes the shader link and nothing else. The now-inert <c>AlphaTestValue</c> parameter is
/// deliberately left where it is: removing a parameter is a structural edit with its own risks, and a
/// cutoff on a shader that does not test changes no pixels.</para>
///
/// <para>Every decision is returned, including the ones that changed nothing, so the caller can show
/// its work rather than announce a number.</para>
/// </summary>
public static class LegacyAlphaRepair
{
    public const string AlphaTestedShader = "Shaders/StaticMesh/DefaultEnv_Flat_AlphaTest";
    public const string SolidShader = "Shaders/StaticMesh/DefaultEnv_Flat";

    /// <summary>
    /// Apply the rule to <paramref name="document"/> in place. The caller serializes if
    /// <see cref="LegacyAlphaRepairResult.Changed"/> is non-zero.
    /// </summary>
    /// <param name="classify">Diffuse texture path to its alpha kind and the share an alpha test at
    /// <paramref name="cutoff"/> would discard. Return null when the texture cannot be read - the
    /// material is then LEFT ALONE, because a repair that fires on a texture it could not open is a
    /// guess wearing a measurement's clothes.</param>
    public static LegacyAlphaRepairResult Repair(MaterialDocument document,
        Func<string, (LegacyAlphaKind Kind, double Discarded)?> classify, float cutoff = 0.35f)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(classify);

        var rows = new List<LegacyAlphaRepairRow>();
        foreach (var material in document.Materials)
        {
            if (!string.Equals(material.RenderShader, AlphaTestedShader, StringComparison.OrdinalIgnoreCase))
                continue;

            string texture = material.Slots
                .FirstOrDefault(s => s.SamplerName.Equals("DiffuseTexture", StringComparison.OrdinalIgnoreCase))
                ?.Path ?? "";

            if (texture.Length == 0)
            {
                rows.Add(new(material.Name, "", LegacyAlphaKind.Cutout, 0, false,
                    "no diffuse texture to judge by - left alone"));
                continue;
            }

            var alpha = classify(texture);
            if (alpha is null)
            {
                rows.Add(new(material.Name, texture, LegacyAlphaKind.Cutout, 0, false,
                    "texture could not be read - left alone"));
                continue;
            }

            var (kind, discarded) = alpha.Value;
            if (kind == LegacyAlphaKind.Cutout)
            {
                rows.Add(new(material.Name, texture, kind, discarded, false,
                    "a real cutout mask - the alpha test belongs here"));
                continue;
            }

            if (!material.CanChangeShader)
            {
                rows.Add(new(material.Name, texture, kind, discarded, false,
                    "no shader link to repoint - left alone"));
                continue;
            }

            bool ok = material.SetRenderShader(SolidShader);
            rows.Add(new(material.Name, texture, kind, discarded, ok,
                ok
                    ? kind == LegacyAlphaKind.Gradient
                        ? $"alpha is a gradient (gloss, not transparency) - the test was discarding {discarded:P0} of it"
                        : "alpha is solid - the test did nothing but could only ever remove pixels"
                    : "shader link refused the change"));
        }

        return new LegacyAlphaRepairResult(rows);
    }
}
