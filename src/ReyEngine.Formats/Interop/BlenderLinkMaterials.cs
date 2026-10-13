using System.Globalization;
using ReyEngine.Formats.Materials;

namespace ReyEngine.Formats.Interop;

/// <summary>
/// M841: a map material as the Blender link describes it - the dict MapgeoAddon's
/// <c>project_manager.convert_bin_entry_to_material_dict</c> produces from a .materials.bin entry, so its
/// <c>material_loader.create_blender_material</c> consumes it unchanged, plus a <c>rey</c> block with what
/// only the editor knows (linked, editable, the Shader Graph number, the chunk links).
///
/// <para>Read from the editor's LIVE document, not from the file, so an edit made in ReyEngine and not yet
/// saved is what Blender sees.</para>
/// </summary>
public static class BlenderLinkMaterials
{
    /// <summary>Wire type of a texturePath field in MapgeoAddon's terms (bin type ids).</summary>
    public const int TypeString = 16, TypeWadChunkLink = 18;

    /// <summary>The REY_GRAPH macro (M833): a material drawn by a ReyEngine Shader Graph.</summary>
    public const string GraphMacro = "REY_GRAPH";

    /// <param name="used">Whether a mesh of the open map draws with it (after pending edits).</param>
    /// <param name="readOnlyReason">Why the whole bin cannot be edited from Blender, or null.</param>
    public static Dictionary<string, object?> Describe(MaterialBinding m, bool used, string? readOnlyReason)
    {
        ArgumentNullException.ThrowIfNull(m);
        string shader = m.RenderShader ?? m.ShaderName ?? "";

        var samplers = new List<Dictionary<string, object?>>();
        foreach (var s in m.Slots)
        {
            var d = new Dictionary<string, object?>
            {
                ["textureName"] = s.SamplerName,
                ["TextureName"] = s.SamplerName,
                ["texturePath"] = s.Path,
                ["texturePathType"] = s.IsWadChunkLink ? TypeWadChunkLink : TypeString,
                ["link"] = s.ChunkHash == 0 ? "" : $"0x{s.ChunkHash:x16}",
            };
            if (s.AddressU is { } u) d["addressU"] = u;
            if (s.AddressV is { } v) d["addressV"] = v;
            if (s.AddressW is { } w) d["addressW"] = w;
            samplers.Add(d);
        }

        var parameters = new List<Dictionary<string, object?>>();
        foreach (var p in m.Parameters)
        {
            var d = new Dictionary<string, object?> { ["name"] = p.Name, ["type"] = p.TypeName };
            if (p.TryGetVector4(out var v4)) d["value"] = new[] { v4.X, v4.Y, v4.Z, v4.W };
            else if (p.TryGetColor(out var c)) d["value"] = new[] { c.X, c.Y, c.Z, c.W };
            else d["text"] = p.CurrentText;
            parameters.Add(d);
        }

        var switchValues = new List<Dictionary<string, object?>>();
        var switches = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var sw in m.AllSwitches)
        {
            switchValues.Add(new Dictionary<string, object?> { ["name"] = sw.Name, ["on"] = sw.On });
            switches.TryAdd(sw.Name, sw.On);
        }

        var macros = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var mac in m.AllMacros) macros.TryAdd(mac.Name, mac.Value);

        bool blend = m.BlendEnable;
        bool cull = m.CullEnable ?? false;
        var pass = new Dictionary<string, object?>
        {
            ["shader"] = shader,
            ["blendEnable"] = blend,
            ["cullEnable"] = cull,
            ["srcColorBlendFactor"] = m.SrcBlendFactor >= 0 ? m.SrcBlendFactor : 1,
            ["dstColorBlendFactor"] = m.DstBlendFactor >= 0 ? m.DstBlendFactor : 0,
            ["srcAlphaBlendFactor"] = Factor(m, "srcAlphaBlendFactor", m.SrcBlendFactor >= 0 ? m.SrcBlendFactor : 1),
            ["dstAlphaBlendFactor"] = Factor(m, "dstAlphaBlendFactor", m.DstBlendFactor >= 0 ? m.DstBlendFactor : 0),
            ["writeMask"] = m.WriteMask,
            ["depthEnable"] = m.DepthEnable,
        };

        string? graph = macros.TryGetValue(GraphMacro, out var g) ? g : null;
        string? whyNot = readOnlyReason
            ?? (m.IsLinked ? $"it is defined in the linked bin {m.LinkedFromBin} - edit it there" : null)
            ?? (!m.IsStaticMaterialDef ? "it is not a StaticMaterialDef" : null);

        return new Dictionary<string, object?>
        {
            ["__type"] = "StaticMaterialDef",
            ["name"] = m.Name,
            ["type"] = 0,
            ["shader"] = shader,
            ["samplerValues"] = samplers,
            ["paramValues"] = parameters,
            ["switchValues"] = switchValues,
            ["switches"] = switches,
            ["shaderMacros"] = macros,
            ["techniques"] = new[] { new Dictionary<string, object?> { ["name"] = "normal", ["passes"] = new[] { pass } } },
            ["childTechniques"] = Array.Empty<object>(),
            ["blendEnable"] = blend,
            ["cullEnable"] = cull,
            ["rey"] = new Dictionary<string, object?>
            {
                ["used"] = used,
                ["linked"] = m.IsLinked,
                ["linked_from"] = m.LinkedFromBin,
                ["editable"] = whyNot is null,
                ["read_only_reason"] = whyNot,
                ["graph"] = graph,
                ["alpha_cutout"] = m.Profile.AlphaCutout,
                ["alpha_cutoff"] = m.Profile.AlphaCutoff,
                ["depth_write"] = m.WritesDepth,
                ["dirty"] = m.IsDirty,
            },
        };
    }

    private static int Factor(MaterialBinding m, string field, int fallback)
    {
        int v = m.GetPassU32(field);
        return v >= 0 ? v : fallback;
    }

    /// <summary>A parameter value from the wire: a JSON number list (1-4 entries) becomes the editor's own
    /// text form ("x, y, z, w"), which <see cref="MaterialParameter.Apply"/> parses for every numeric type.</summary>
    public static string ParameterText(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return string.Join(", ", values.Select(v => ((float)v).ToString("R", CultureInfo.InvariantCulture)));
    }
}
