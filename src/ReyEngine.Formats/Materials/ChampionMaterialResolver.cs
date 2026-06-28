namespace ReyEngine.Formats.Materials;

/// <summary>
/// Resolves a champion skin .bin into per-submesh diffuse textures using the proper League material
/// system (StaticMaterialDef sampler values + materialOverride submesh bindings + the default
/// material). Replaces the old name-heuristic so every submesh gets its own material's diffuse
/// instead of all falling back to the single base texture.
/// </summary>
public static class ChampionMaterialResolver
{
    public sealed record Result(Dictionary<string, string> SubmeshDiffuse, string? DefaultDiffuse)
    {
        public bool HasAny => SubmeshDiffuse.Count > 0 || !string.IsNullOrEmpty(DefaultDiffuse);

        /// <summary>Diffuse path for a submesh: its own material, else the base-mesh default.</summary>
        public string? For(string submesh) =>
            SubmeshDiffuse.TryGetValue(submesh, out var p) ? p : DefaultDiffuse;
    }

    public static Result Resolve(byte[] skinBin, Func<uint, string?> resolve)
    {
        try
        {
            var doc = MaterialDocument.Parse(skinBin, resolve);
            return new Result(doc.SubmeshDiffuse(), doc.DefaultDiffusePath);
        }
        catch
        {
            return new Result(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), null);
        }
    }
}
