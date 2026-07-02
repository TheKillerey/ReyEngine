namespace ReyEngine.Formats.Materials;

/// <summary>One material object surfaced as a virtual asset (M33): its path/name, shader, preview profile,
/// texture-slot summary and counts, plus the diffuse path for a thumbnail. Read-only projection over a
/// <see cref="MaterialDocument"/> — the actual editing still goes through the Material Editor.</summary>
public sealed record MaterialSummary(
    string Name,
    string Shader,
    string ProfileLabel,
    string FeatureSummary,
    int SamplerCount,
    int ParameterCount,
    int SwitchCount,
    string? DiffusePath,
    IReadOnlyList<string> SamplerNames,
    IReadOnlyList<string> Submeshes,
    bool IsDefault)
{
    /// <summary>Short leaf name (the last path segment), for display.</summary>
    public string ShortName
    {
        get { int s = Name.LastIndexOfAny(new[] { '/', '\\' }); return s < 0 ? Name : Name[(s + 1)..]; }
    }
}

/// <summary>Extracts the material objects from a champion skin .bin or a map .materials.bin as a list of
/// <see cref="MaterialSummary"/> virtual assets. Never throws — returns what it can.</summary>
public static class MaterialLibraryExtractor
{
    public static IReadOnlyList<MaterialSummary> Extract(byte[] bin, Func<uint, string?> resolve)
    {
        var list = new List<MaterialSummary>();
        MaterialDocument doc;
        try { doc = MaterialDocument.Parse(bin, resolve); }
        catch { return list; }

        foreach (var b in doc.Materials)
        {
            // Only the real StaticMaterialDef objects are meaningful virtual assets; skip the champion
            // skin-default-texture / inline-override synthetic bindings (they have no editable sampler set).
            if (!b.IsStaticMaterialDef) continue;
            list.Add(new MaterialSummary(
                Name: b.Name,
                Shader: b.ShaderName,
                ProfileLabel: b.Profile.ProfileLabel,
                FeatureSummary: b.Profile.FeatureSummary,
                SamplerCount: b.Slots.Count,
                ParameterCount: b.Parameters.Count,
                SwitchCount: b.Switches.Count,
                DiffusePath: b.Diffuse?.Path,
                SamplerNames: b.Slots.Select(s => s.SamplerName).ToList(),
                Submeshes: b.Submeshes.ToList(),
                IsDefault: b.IsDefault));
        }
        return list;
    }

    /// <summary>True if a path is a material library we can expand (champion skin .bin or map .materials.bin).</summary>
    public static bool IsMaterialLibrary(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return path.EndsWith(".materials.bin", StringComparison.OrdinalIgnoreCase)
            || (path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                && path.Contains("/skins/", StringComparison.OrdinalIgnoreCase));
    }
}
