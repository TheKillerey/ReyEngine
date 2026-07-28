using System.Text;

namespace ReyEngine.Formats.Shaders;

/// <summary>
/// <para>M241 (phase 1): one resolved shader stage, described independently of any rendering backend.</para>
///
/// <para><see cref="DxbcShader"/> already says what a blob DECLARES — its inputs, constant buffers and
/// resource bindings — and is backend-neutral. What it does not carry is PROVENANCE: which shader this is,
/// which permutation of it, and which define set produced that permutation. Until now that lived scattered
/// across the call sites that happened to resolve it, which is why the same four values were re-derived in
/// the view model, the playback engine and three separate scratch harnesses, and why a wrong one could not
/// be attributed to anything.</para>
///
/// <para>Bundling the two is what makes a pipeline cache key possible (see <see cref="PipelineKey"/>) and
/// what a second backend would consume. It deliberately holds no D3D types and no
/// <c>Silk.NET</c> reference.</para>
/// </summary>
public sealed record ShaderDescription(
    string ShaderName,
    DxbcStage Stage,
    ulong PermutationKey,
    uint BlobIndex,
    IReadOnlyDictionary<string, string> Defines,
    DxbcShader Reflection)
{
    /// <summary>The cache entry this came from, e.g. <c>…/quad_vs.vs.dx11</c>.</summary>
    public string TocPath => ShaderCacheReader.TocPathFor(ShaderName, Stage);

    /// <summary>Short name for UI, e.g. <c>quad_vs</c>.</summary>
    public string ShortName => ShaderName.Split('/').LastOrDefault() ?? ShaderName;

    /// <summary>The define set in the canonical order the permutation key is computed over, so two
    /// descriptions of the same variant produce the same string regardless of dictionary ordering.</summary>
    public string DefineSignature => Defines.Count == 0
        ? "(base)"
        : string.Join("+", Defines.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));

    /// <summary>Every vertex input the stage declares, as HLSL writes them. Empty for pixel stages.</summary>
    public IReadOnlyList<string> RequiredSemantics =>
        Reflection.Inputs.Where(i => i.SystemValueType == 0).Select(i => i.FullSemantic).ToList();

    /// <summary>Texture bind points the stage declares, by register.</summary>
    public IReadOnlyList<(uint Register, string Name)> TextureBindings =>
        Reflection.Textures.OrderBy(t => t.BindPoint).Select(t => (t.BindPoint, t.Name)).ToList();

    /// <summary>Constants the stage actually READS, which is the set worth reporting as unbound. A constant
    /// the compiler eliminated is declared but cannot affect the image.</summary>
    public IEnumerable<(string Buffer, DxbcConstant Constant)> UsedConstants =>
        Reflection.ConstantBuffers.SelectMany(cb => cb.Variables.Where(v => v.IsUsed).Select(v => (cb.Name, v)));

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{ShaderName} [{Stage}]  blob {BlobIndex}  key 0x{PermutationKey:x16}");
        sb.AppendLine($"   defines : {DefineSignature}");
        if (RequiredSemantics.Count > 0) sb.AppendLine($"   inputs  : {string.Join("  ", RequiredSemantics)}");
        foreach (var (reg, name) in TextureBindings) sb.AppendLine($"   t{reg,-3}    : {name}");
        foreach (var cb in Reflection.ConstantBuffers)
        {
            var used = cb.Variables.Where(v => v.IsUsed).ToList();
            if (used.Count == 0) continue;
            sb.AppendLine($"   cb b{cb.BindPoint} {cb.Name} ({cb.Size} B)");
            foreach (var v in used) sb.AppendLine($"        +{v.Offset,-4} {v.TypeName,-12} {v.Name}");
        }
        return sb.ToString();
    }
}
