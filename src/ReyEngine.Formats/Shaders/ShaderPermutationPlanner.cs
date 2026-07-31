namespace ReyEngine.Formats.Shaders;

/// <summary>One complete define set considered by the shader cache, including which values came from
/// axes the material and shader definition do not pin.</summary>
public sealed record ShaderDefineCandidate(
    IReadOnlyList<string> Defines,
    IReadOnlyList<string> InferredDefines)
{
    public ulong Key => ShaderCacheReader.PermutationKey(Defines);
}

/// <summary>
/// Enumerates the same search space as <see cref="ShaderCacheReader.ResolvePermutation"/>, but keeps every
/// complete define set. The experimental shader-cache patch uses this to cover runtime-injected variants
/// without guessing which graphics-quality path the client will request.
/// </summary>
public static class ShaderPermutationPlanner
{
    public static IReadOnlyList<ShaderDefineCandidate> EnumerateCandidates(
        ShaderStageToc toc,
        IReadOnlyDictionary<string, string>? macros,
        IReadOnlyDictionary<string, bool>? switches,
        IReadOnlyDictionary<string, string>? featureDefines,
        IReadOnlyDictionary<string, bool>? switchDefaults,
        out string explanation,
        long maxCombinations = 2_000_000,
        IReadOnlySet<string>? forcedAbsent = null)
    {
        var fixedParts = new List<string>();
        var freeAxes = new List<(string Name, List<string?> Options)>();

        foreach (var (name, valuesReadOnly) in toc.Axes)
        {
            var values = valuesReadOnly.ToList();
            if (forcedAbsent is not null && forcedAbsent.Contains(name)) continue;

            if (macros is not null && macros.TryGetValue(name, out var macroValue))
            {
                if (!values.Contains(macroValue))
                {
                    explanation = $"material macro {name}={macroValue} was never cooked";
                    return Array.Empty<ShaderDefineCandidate>();
                }
                fixedParts.Add(name + "=" + macroValue);
            }
            else if (switches is not null && switches.TryGetValue(name, out bool switchValue))
            {
                if (!TryAddBool(name, switchValue, values, fixedParts, out explanation))
                    return Array.Empty<ShaderDefineCandidate>();
            }
            else if (featureDefines is not null && featureDefines.TryGetValue(name, out var featureValue))
            {
                if (!values.Contains(featureValue))
                {
                    explanation = $"shader featureDefine {name}={featureValue} was never cooked";
                    return Array.Empty<ShaderDefineCandidate>();
                }
                fixedParts.Add(name + "=" + featureValue);
            }
            else if (switchDefaults is not null && switchDefaults.TryGetValue(name, out bool defaultValue))
            {
                if (!TryAddBool(name, defaultValue, values, fixedParts, out explanation))
                    return Array.Empty<ShaderDefineCandidate>();
            }
            else
            {
                var options = new List<string?> { null };
                options.AddRange(values);
                freeAxes.Add((name, options));
            }
        }

        long combinations = 1;
        foreach (var axis in freeAxes)
        {
            if (combinations > maxCombinations / axis.Options.Count)
            {
                explanation = $"{freeAxes.Count} unconstrained axes exceed the {maxCombinations:n0} combination limit";
                return Array.Empty<ShaderDefineCandidate>();
            }
            combinations *= axis.Options.Count;
        }

        var result = new List<ShaderDefineCandidate>(checked((int)combinations));
        for (long combination = 0; combination < combinations; combination++)
        {
            long remainder = combination;
            var parts = new List<string>(fixedParts);
            var inferred = new List<string>();
            foreach (var (name, options) in freeAxes)
            {
                int selected = (int)(remainder % options.Count);
                remainder /= options.Count;
                if (options[selected] is not { } value) continue;
                string define = name + "=" + value;
                parts.Add(define);
                inferred.Add(define);
            }
            parts.Sort(StringComparer.Ordinal);
            inferred.Sort(StringComparer.Ordinal);
            result.Add(new ShaderDefineCandidate(parts, inferred));
        }

        explanation = $"enumerated {result.Count:n0} complete define set(s) across {freeAxes.Count} unconstrained axes";
        return result;
    }

    private static bool TryAddBool(string name, bool enabled, IReadOnlyCollection<string> values,
        List<string> fixedParts, out string explanation)
    {
        string value = enabled ? "1" : "0";
        if (values.Contains(value))
        {
            fixedParts.Add(name + "=" + value);
            explanation = "";
            return true;
        }
        if (!enabled)
        {
            // Presence-only axes encode false by omitting the define.
            explanation = "";
            return true;
        }
        explanation = $"switch {name}=1 was never cooked";
        return false;
    }
}
