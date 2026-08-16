using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReyEngine.Formats.Materials;

/// <summary>Which parts of a preset to stamp onto a target. Opt-in per part, because the useful case is
/// almost never "make this material identical" — it is "give these twenty the same shader" or "give these
/// twenty the same tint".</summary>
[Flags]
public enum MaterialPresetParts
{
    None = 0,
    Shader = 1 << 0,
    Samplers = 1 << 1,
    Parameters = 1 << 2,
    Switches = 1 << 3,
    Macros = 1 << 4,
    RenderState = 1 << 5,
    All = Shader | Samplers | Parameters | Switches | Macros | RenderState,
}

/// <summary>One sampler in a preset. Address modes are nullable throughout for the same reason they are on
/// <see cref="TextureSlot"/>: absent is not zero, and 4,726 shipped samplers author no address field at
/// all (M490/M493).</summary>
public sealed record MaterialPresetSampler(
    string Name,
    string Path,
    int? AddressU = null,
    int? AddressV = null,
    int? AddressW = null);

/// <summary>One parameter in a preset, stored as the editor's own text form so the preset stays
/// type-agnostic — a vec4 on one material and a f32 on another both round-trip through
/// <see cref="MaterialParameter.Apply"/>.</summary>
public sealed record MaterialPresetParameter(string Name, string Text);

/// <summary>The first pass's editable render state. <see cref="CullEnable"/> is nullable because the field
/// is genuinely optional in the bin.</summary>
public sealed record MaterialPresetRenderState(
    bool BlendEnable,
    bool? CullEnable,
    int SrcBlendFactor,
    int DstBlendFactor);

/// <summary>
/// M504: a named snapshot of what makes a material look the way it does — shader, samplers, parameters,
/// switches, macros, render state — so it can be stamped onto other materials.
///
/// <para>Deliberately NOT a copy of the bin object. M482 copied a donor's <c>techniques</c> wholesale and
/// turned 75 meshes black; the lesson is that material state has to be transferred FIELD BY FIELD, with
/// each field checked against the target it is landing on. This record is the field list; the checking
/// lives in <see cref="MaterialPresetApplier"/>.</para>
/// </summary>
public sealed record MaterialPreset(
    string Name,
    string Shader,
    IReadOnlyList<MaterialPresetSampler> Samplers,
    IReadOnlyList<MaterialPresetParameter> Parameters,
    IReadOnlyDictionary<string, bool> Switches,
    IReadOnlyDictionary<string, string> Macros,
    MaterialPresetRenderState? RenderState = null,
    string? SourceMaterial = null,
    string? Note = null)
{
    public static MaterialPreset Empty(string name) => new(
        name, "", Array.Empty<MaterialPresetSampler>(), Array.Empty<MaterialPresetParameter>(),
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Snapshot a live material. Reads the CURRENT values, so a preset captured after editing
    /// carries the edits.</summary>
    public static MaterialPreset Capture(MaterialBinding material, string name)
    {
        ArgumentNullException.ThrowIfNull(material);

        var samplers = material.Slots
            .Select(s => new MaterialPresetSampler(s.SamplerName, s.Path, s.AddressU, s.AddressV, s.AddressW))
            .ToList();

        // Only parameters we can round-trip through the value editor. A read-only one (an unsupported
        // BIN type) would capture as text we could never apply, which is a preset that silently does less
        // than it claims.
        var parameters = material.Parameters
            .Where(p => p.IsEditable)
            .Select(p => new MaterialPresetParameter(p.Name, p.CurrentText))
            .ToList();

        var switches = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var (n, on) in material.Switches) switches[n] = on;

        var macros = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (n, v) in material.Macros) macros[n] = v;

        MaterialPresetRenderState? state = material.CanEditRenderState
            ? new MaterialPresetRenderState(material.BlendEnable, material.CullEnable,
                material.SrcBlendFactor, material.DstBlendFactor)
            : null;

        return new MaterialPreset(name, material.RenderShader ?? "", samplers, parameters, switches, macros,
            state, material.Name);
    }

    /// <summary>The parts this preset actually has something to say about — a preset captured from a
    /// material with no pass struct cannot offer render state, and offering the checkbox anyway is a
    /// promise it cannot keep.</summary>
    public MaterialPresetParts AvailableParts
    {
        get
        {
            var p = MaterialPresetParts.None;
            if (Shader.Length > 0) p |= MaterialPresetParts.Shader;
            if (Samplers.Count > 0) p |= MaterialPresetParts.Samplers;
            if (Parameters.Count > 0) p |= MaterialPresetParts.Parameters;
            if (Switches.Count > 0) p |= MaterialPresetParts.Switches;
            if (Macros.Count > 0) p |= MaterialPresetParts.Macros;
            if (RenderState is not null) p |= MaterialPresetParts.RenderState;
            return p;
        }
    }

    public string Summary =>
        $"{Short(Shader)} · {Samplers.Count} sampler(s) · {Parameters.Count} param(s) · "
        + $"{Switches.Count} switch(es) · {Macros.Count} macro(s)"
        + (RenderState is null ? "" : " · render state");

    private static string Short(string s)
    {
        if (s.Length == 0) return "(no shader)";
        int i = s.LastIndexOf('/');
        return i >= 0 ? s[(i + 1)..] : s;
    }
}

/// <summary>
/// The user's saved presets, as a plain JSON file. Kept next to the editor settings rather than inside a
/// project: a preset is a way of working, and the point of one is to reuse it on the NEXT map too.
/// </summary>
public sealed class MaterialPresetLibrary
{
    public List<MaterialPreset> Presets { get; set; } = new();

    [JsonIgnore]
    public static string DefaultPath => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "ReyEngine", "material-presets.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        // Preset names are user text and end up in a file the user may open; escaping every non-ASCII
        // character would make "Jade Terrain (grün)" unreadable.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static MaterialPresetLibrary Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<MaterialPresetLibrary>(File.ReadAllText(path)) ?? new();
        }
        catch { /* corrupt / unreadable - start empty rather than lose the session */ }
        return new();
    }

    public bool Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
            return true;
        }
        catch { return false; }
    }

    /// <summary>Add or replace by name (case-insensitive) — saving a preset twice should update it, not
    /// leave two rows that differ invisibly.</summary>
    public void Put(MaterialPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        int i = Presets.FindIndex(p => p.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) Presets[i] = preset;
        else Presets.Add(preset);
    }

    public bool Remove(string name) =>
        Presets.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;

    public MaterialPreset? Find(string name) =>
        Presets.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
