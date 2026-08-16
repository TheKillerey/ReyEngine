using System.Numerics;

namespace ReyEngine.Formats.Materials;

/// <summary>What kind of problem a material has. Ordered roughly by how badly it shows in game.</summary>
public enum MaterialIssue
{
    /// <summary>A mesh names a material the bin does not contain. The mesh draws untextured — white.</summary>
    MissingMaterial,
    /// <summary>The material has no shader link, so nothing decides how to draw it.</summary>
    NoShader,
    /// <summary>A sampler points at a texture that could not be found.</summary>
    UnresolvedTexture,
    /// <summary>The macro set asks for a shader permutation the game never cooked — M486's crash.</summary>
    UncookedPermutation,
    /// <summary>A macro the shader does not declare as an axis. The client ignores it, so the material
    /// claims something the shader will not do.</summary>
    InertMacro,
    /// <summary>TintColor away from the shader's declared default. 1.0 on the DefaultEnv_Flat family is a
    /// ~2x brightener rather than "no tint" — M500.</summary>
    NonNeutralTint,
    /// <summary>The material has no sampler at all.</summary>
    NoTextures,
    /// <summary>In the bin, but no mesh in this map uses it.</summary>
    Unused,
}

/// <summary>One problem found on one material.</summary>
public sealed record MaterialFinding(MaterialIssue Issue, string Detail);

/// <summary>One row of the material browser.</summary>
public sealed record MaterialAuditRow(
    string Name,
    string Shader,
    int MeshCount,
    int SubmeshCount,
    IReadOnlyList<string> Textures,
    IReadOnlyList<MaterialFinding> Findings)
{
    public bool HasIssues => Findings.Count > 0;

    /// <summary>The worst issue on this row, for sorting and for the badge colour.</summary>
    public MaterialIssue? Worst =>
        Findings.Count == 0 ? null : Findings.Min(f => f.Issue);

    public string ShortName { get { int i = Name.LastIndexOf('/'); return i >= 0 ? Name[(i + 1)..] : Name; } }
    public string ShortShader { get { int i = Shader.LastIndexOf('/'); return i >= 0 ? Shader[(i + 1)..] : Shader; } }
}

/// <summary>What the audit needs to look things up. Everything is optional: a check with no way to answer
/// is SKIPPED rather than guessed, because a browser that invents warnings is worse than one that omits
/// them.</summary>
public sealed record MaterialAuditContext(
    Func<string, bool>? TextureExists = null,
    Func<MaterialBinding, string, bool>? CanSetMacro = null,
    Func<string, string, bool>? ShaderDeclaresMacro = null,
    Func<string, Vector4?>? ShaderTintDefault = null);

/// <summary>
/// M503: audits every material in a map's materials.bin against the meshes that use it.
///
/// <para>The question this answers is "which of my 82 materials is wrong", which until now was a hunt
/// through a property grid one material at a time. Every check here corresponds to something that actually
/// went wrong on a real ported map during M486-M502 — a mesh naming a material the bin lost, a macro the
/// game never cooked, a macro the shader ignores, a tint of 1.0 that doubles the albedo.</para>
///
/// <para>In Formats rather than the view model so it is testable: the app has no test project, and these
/// rules are exactly the kind that quietly rot.</para>
/// </summary>
public static class MapMaterialAudit
{
    /// <summary>Riot's declared TintColor default on the DefaultEnv_Flat family — the overlay identity.
    /// See LegacyMapPorter.NeutralTint for the disassembly this comes from.</summary>
    public const float NeutralTint = 0.5019608f;

    /// <param name="materials">Parsed materials.bin for the open map.</param>
    /// <param name="usageByMaterial">Material name -> (mesh count, submesh count), from the map's groups.</param>
    /// <param name="context">Optional lookups; anything null simply skips its check.</param>
    public static IReadOnlyList<MaterialAuditRow> Audit(
        IReadOnlyList<MaterialBinding> materials,
        IReadOnlyDictionary<string, (int Meshes, int Submeshes)> usageByMaterial,
        MaterialAuditContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(materials);
        ArgumentNullException.ThrowIfNull(usageByMaterial);
        context ??= new MaterialAuditContext();

        var rows = new List<MaterialAuditRow>(materials.Count + 8);
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in materials)
        {
            present.Add(m.Name);
            var findings = new List<MaterialFinding>();

            usageByMaterial.TryGetValue(m.Name, out var usage);
            string shader = m.RenderShader ?? "";

            if (shader.Length == 0)
                findings.Add(new MaterialFinding(MaterialIssue.NoShader,
                    "no technique/pass shader link — nothing decides how this draws"));

            if (m.Slots.Count == 0)
                findings.Add(new MaterialFinding(MaterialIssue.NoTextures, "no sampler is bound"));

            if (context.TextureExists is { } exists)
                foreach (var slot in m.Slots)
                {
                    if (string.IsNullOrWhiteSpace(slot.Path)) continue;
                    if (!exists(slot.Path))
                        findings.Add(new MaterialFinding(MaterialIssue.UnresolvedTexture,
                            $"{slot.SamplerName}: {slot.Path}"));
                }

            // Macro checks. Split deliberately: a macro the shader never declares is IGNORED by the client
            // (inert but misleading), while one it declares without cooking this value makes the client fail
            // to compile the shader. M502 measured both on this very map.
            if (shader.Length > 0)
                foreach (var (macro, value) in m.Macros)
                {
                    if (value is not ("1" or "true")) continue;
                    if (context.ShaderDeclaresMacro is { } declares && !declares(shader, macro))
                    {
                        findings.Add(new MaterialFinding(MaterialIssue.InertMacro,
                            $"{macro} is not a permutation axis of {Short(shader)} — the client ignores it"));
                        continue;                       // no point asking whether an ignored macro is cooked
                    }
                    if (context.CanSetMacro is { } can && !can(m, macro))
                        findings.Add(new MaterialFinding(MaterialIssue.UncookedPermutation,
                            $"{macro}: {Short(shader)} ships no cooked permutation for it"));
                }

            if (context.ShaderTintDefault is { } tintDefault && shader.Length > 0)
            {
                var authored = m.Parameters.FirstOrDefault(p =>
                    p.Name.Equals("TintColor", StringComparison.OrdinalIgnoreCase));
                if (authored is not null && authored.TryGetVector4(out var tint)
                    && tintDefault(shader) is { } expected
                    && !Near(tint.X, expected.X) )
                {
                    findings.Add(new MaterialFinding(MaterialIssue.NonNeutralTint,
                        $"TintColor {tint.X:0.###} vs the shader's default {expected.X:0.###}"
                        + (Near(tint.X, 1f) ? " — 1.0 is an overlay BRIGHTENER, not neutral" : "")));
                }
            }

            if (usage.Meshes == 0)
                findings.Add(new MaterialFinding(MaterialIssue.Unused, "no mesh in this map uses it"));

            rows.Add(new MaterialAuditRow(m.Name, shader, usage.Meshes, usage.Submeshes,
                m.Slots.Select(s => s.Path).Where(p => !string.IsNullOrWhiteSpace(p)).ToList(),
                findings));
        }

        // Materials the MESHES ask for that the bin does not have. These are the white meshes, and they are
        // invisible if you only ever look at the list of materials that exist.
        foreach (var (name, usage) in usageByMaterial)
        {
            if (present.Contains(name) || string.IsNullOrWhiteSpace(name)) continue;
            rows.Add(new MaterialAuditRow(name, "", usage.Meshes, usage.Submeshes,
                Array.Empty<string>(),
                new[]
                {
                    new MaterialFinding(MaterialIssue.MissingMaterial,
                        $"{usage.Meshes} mesh(es) name this material, but the bin does not contain it"),
                }));
        }

        return rows;
    }

    /// <summary>Mesh and submesh counts per material name, from a decoded map's groups.</summary>
    public static Dictionary<string, (int Meshes, int Submeshes)> UsageFrom(
        IEnumerable<(string Material, int MeshIndex)> groups)
    {
        var meshes = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        var submeshes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (material, meshIndex) in groups)
        {
            if (string.IsNullOrWhiteSpace(material)) continue;
            if (!meshes.TryGetValue(material, out var set)) meshes[material] = set = new HashSet<int>();
            if (meshIndex >= 0) set.Add(meshIndex);
            submeshes[material] = submeshes.GetValueOrDefault(material) + 1;
        }
        return meshes.ToDictionary(k => k.Key, k => (k.Value.Count, submeshes.GetValueOrDefault(k.Key)),
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool Near(float a, float b) => MathF.Abs(a - b) <= 0.01f;
    private static string Short(string s) { int i = s.LastIndexOf('/'); return i >= 0 ? s[(i + 1)..] : s; }
}
