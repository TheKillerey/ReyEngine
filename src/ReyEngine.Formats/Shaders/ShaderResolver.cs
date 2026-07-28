namespace ReyEngine.Formats.Shaders;

/// <summary>
/// <para>M241 (phase 1): resolve a shader name + define set to a <see cref="ShaderDescription"/> — one
/// place, instead of the four that each re-derived it.</para>
///
/// <para>Before this, resolving a stage meant: build the TOC path, read the TOC, call ResolvePermutation,
/// load the blob by index, and remember which define set you had used. The view model did it, the particle
/// playback did it, and several scratch harnesses did it, each keeping the pieces in separate locals. That
/// is how M235's probe ended up exercising a different code path from the app.</para>
/// </summary>
public sealed class ShaderResolver
{
    private readonly ShaderCacheReader _cache;

    public ShaderResolver(ShaderCacheReader cache) => _cache = cache;

    /// <summary>
    /// Resolve one stage. Follows the <c>_vs</c>/<c>_ps</c> partner rule when the named entry does not ship
    /// this stage, so the <c>assets/shaders/hlsl/</c> families resolve like everything else.
    /// </summary>
    public ShaderDescription? Resolve(
        string shaderName, DxbcStage stage,
        IReadOnlyDictionary<string, string>? macros = null,
        IReadOnlyDictionary<string, bool>? switches = null,
        IReadOnlyDictionary<string, string>? featureDefines = null,
        IReadOnlyDictionary<string, bool>? switchDefaults = null,
        IReadOnlySet<string>? forcedAbsent = null)
    {
        Explanation = "";
        var toc = _cache.ReadTocOrPartner(shaderName, stage, out var resolvedName);
        if (toc is null) { Explanation = $"no {stage} stage for '{shaderName}'"; return null; }

        var perm = ShaderCacheReader.ResolvePermutation(
            toc, macros, switches, featureDefines, switchDefaults, out var why, forcedAbsent: forcedAbsent);
        Explanation = why;
        if (perm is null) return null;

        var refl = _cache.LoadShader(ShaderCacheReader.TocPathFor(resolvedName, stage), perm.BlobIndex, out var err);
        if (refl is null) { Explanation = err ?? "bytecode would not load"; return null; }

        // The define set that PRODUCED this permutation where the reader recovered it, falling back to what
        // the caller asked for where it did not.
        //
        // ShaderPermutation.Defines is only filled by DescribePermutations, which enumerates the pool -
        // ResolvePermutation reaches a permutation by key and leaves it null. Null therefore means "not
        // recovered", NEVER "no defines", and treating it as the latter would label every resolved variant
        // as the base one. The permutation KEY is exact either way, which is what the cache is keyed on.
        var effective = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (perm.Defines is { } recovered)
        {
            foreach (var entry in recovered)
            {
                int eq = entry.IndexOf('=');
                if (eq > 0) effective[entry[..eq]] = entry[(eq + 1)..];
            }
        }
        else if (macros is not null)
        {
            foreach (var (k, v) in macros) effective[k] = v;
        }
        DefinesWereRecovered = perm.Defines is not null;

        return new ShaderDescription(resolvedName, stage, perm.Key, perm.BlobIndex, effective, refl);
    }

    /// <summary>Why the last resolve produced what it did, for the debug surface.</summary>
    public string Explanation { get; private set; } = "";

    /// <summary>False when the last description's Defines are the REQUESTED set rather than the recovered
    /// one - so a UI can say "as requested" instead of implying it read them back off the permutation.</summary>
    public bool DefinesWereRecovered { get; private set; }
}
