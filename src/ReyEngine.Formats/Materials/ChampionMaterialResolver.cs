namespace ReyEngine.Formats.Materials;

/// <summary>
/// Resolves a champion skin .bin into per-submesh textures using the proper League material
/// system (StaticMaterialDef sampler values + materialOverride submesh bindings + the default
/// material). Returns the diffuse plus the secondary samplers (mask / gradient / emissive) that
/// the RiotApprox preview blends (M19).
/// </summary>
public static class ChampionMaterialResolver
{
    public sealed record Result(
        Dictionary<string, string> SubmeshDiffuse, string? DefaultDiffuse,
        Dictionary<string, string> SubmeshMask, string? DefaultMask,
        Dictionary<string, string> SubmeshGradient, string? DefaultGradient,
        Dictionary<string, string> SubmeshEmissive, string? DefaultEmissive,
        Dictionary<string, string> SubmeshMatCap, string? DefaultMatCap,
        Dictionary<string, string> SubmeshMatCapMask, string? DefaultMatCapMask,
        Dictionary<string, MaterialProfile> SubmeshProfile, MaterialProfile DefaultProfile)
    {
        /// <summary>Preview profile (features + UV transform) for a submesh — its own material, else the default (M32).</summary>
        public MaterialProfile Profile(string submesh) =>
            SubmeshProfile.TryGetValue(submesh, out var p) ? p : DefaultProfile;

        public bool HasAny => SubmeshDiffuse.Count > 0 || !string.IsNullOrEmpty(DefaultDiffuse);
        public bool HasSecondary =>
            SubmeshMask.Count > 0 || SubmeshGradient.Count > 0 || SubmeshEmissive.Count > 0 || SubmeshMatCap.Count > 0
            || !string.IsNullOrEmpty(DefaultMask) || !string.IsNullOrEmpty(DefaultGradient)
            || !string.IsNullOrEmpty(DefaultEmissive) || !string.IsNullOrEmpty(DefaultMatCap);

        private static string? Pick(Dictionary<string, string> map, string? def, string submesh) =>
            map.TryGetValue(submesh, out var p) ? p : def;

        /// <summary>Diffuse path for a submesh: its own material, else the base-mesh default.</summary>
        public string? For(string submesh) => Pick(SubmeshDiffuse, DefaultDiffuse, submesh);
        public string? ForMask(string submesh) => Pick(SubmeshMask, DefaultMask, submesh);
        public string? ForGradient(string submesh) => Pick(SubmeshGradient, DefaultGradient, submesh);
        public string? ForEmissive(string submesh) => Pick(SubmeshEmissive, DefaultEmissive, submesh);
        public string? ForMatCap(string submesh) => Pick(SubmeshMatCap, DefaultMatCap, submesh);
        public string? ForMatCapMask(string submesh) => Pick(SubmeshMatCapMask, DefaultMatCapMask, submesh);

        private static Dictionary<string, string> M() => new(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, MaterialProfile> P() => new(StringComparer.OrdinalIgnoreCase);
        public static Result Empty() =>
            new(M(), null, M(), null, M(), null, M(), null, M(), null, M(), null, P(), MaterialProfile.Default);
    }

    public static Result Resolve(byte[] skinBin, Func<uint, string?> resolve)
    {
        try
        {
            var doc = MaterialDocument.Parse(skinBin, resolve);
            return new Result(
                doc.SubmeshDiffuse(), doc.DefaultDiffusePath,
                doc.SubmeshSampler(b => b.Mask), doc.DefaultMaskPath,
                doc.SubmeshSampler(b => b.Gradient), doc.DefaultGradientPath,
                doc.SubmeshSampler(b => b.Emissive), doc.DefaultEmissivePath,
                doc.SubmeshSampler(b => b.MatCap), doc.DefaultMatCapPath,
                doc.SubmeshSampler(b => b.MatCapMask), doc.DefaultMatCapMaskPath,
                doc.SubmeshProfiles(), doc.DefaultProfile);
        }
        catch
        {
            return Result.Empty();
        }
    }
}
