using System.Numerics;
using System.Reflection;
using LeagueToolkit.Core.Environment;

namespace ReyEngine.Formats.MapGeo;

/// <summary>Writes a .mapgeo back out with user mesh moves baked into each mesh's transform.</summary>
public static class MapGeoWriter
{
    // EnvironmentAssetMesh.Transform is an init-only auto-property, so set its backing field directly.
    private static readonly FieldInfo? TransformField =
        typeof(EnvironmentAssetMesh).GetField("<Transform>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance);

    public static bool CanWriteBack => TransformField is not null;

    /// <summary>
    /// Re-open the original mapgeo, apply each moved mesh's world-space offset to its transform
    /// translation, and serialize. Returns the new mapgeo bytes (for the override store / build).
    /// </summary>
    public static byte[] WriteWithMoves(byte[] originalMapgeo, IEnumerable<MapGeoMesh> meshes)
    {
        if (TransformField is null) throw new InvalidOperationException("EnvironmentAssetMesh.Transform backing field not found.");

        using var input = new MemoryStream(originalMapgeo, writable: false);
        var env = new EnvironmentAsset(input);
        var list = env.Meshes;

        foreach (var m in meshes)
        {
            if (!m.IsMoved || m.Index < 0 || m.Index >= list.Count) continue;
            var em = list[m.Index];
            Matrix4x4 t = em.Transform;
            t.Translation += m.Offset; // vertices were baked as Transform*local; moving the origin moves them all
            TransformField.SetValue(em, t);
        }

        using var output = new MemoryStream();
        env.Write(output);
        return output.ToArray();
    }

    /// <summary>
    /// Write the moves AND validate the result reopens. Returns null if LeagueToolkit produced an
    /// unreadable mapgeo (its EnvironmentAsset.Write is currently lossy for these versions) so callers
    /// never persist a corrupt file. <paramref name="error"/> carries the reason on failure.
    /// </summary>
    public static byte[]? TryWriteWithMoves(byte[] originalMapgeo, IEnumerable<MapGeoMesh> meshes, out string? error)
    {
        try
        {
            var bytes = WriteWithMoves(originalMapgeo, meshes);
            using var verify = new MemoryStream(bytes, writable: false);
            _ = new EnvironmentAsset(verify); // throws if the written mapgeo is unreadable
            error = null;
            return bytes;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    public static bool HasMoves(IEnumerable<MapGeoMesh> meshes) => meshes.Any(m => m.IsMoved);
}
