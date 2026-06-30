using System.Buffers.Binary;
using System.Numerics;
using LeagueToolkit.Core.Environment;

namespace ReyEngine.Formats.MapGeo;

/// <summary>
/// Persists mesh moves into a .mapgeo by surgically patching each moved mesh's transform translation
/// in place, leaving the rest of the file byte-exact. LeagueToolkit's <c>EnvironmentAsset.Write</c> is
/// lossy for these versions (it produces an unreadable file), so we never re-serialize — instead we
/// locate each transform via its unique <c>[BoundingBox(24)][Transform(64)]</c> byte signature (the AABB
/// disambiguates the many identity transforms) and overwrite the 12 translation bytes.
/// </summary>
public static class MapGeoWriter
{
    public static bool CanWriteBack => true;
    public static bool HasMoves(IEnumerable<MapGeoMesh> meshes) => meshes.Any(m => m.IsMoved);

    /// <summary>Patch the moved meshes' transforms into <paramref name="originalMapgeo"/> and return new bytes.</summary>
    public static byte[] WriteWithMoves(byte[] originalMapgeo, IReadOnlyList<MapGeoMesh> meshes)
    {
        var moves = meshes.Where(m => m.IsMoved).ToList();
        if (moves.Count == 0) return originalMapgeo;

        using var ms = new MemoryStream(originalMapgeo, writable: false);
        var env = new EnvironmentAsset(ms);
        var envMeshes = env.Meshes;

        var result = (byte[])originalMapgeo.Clone();

        // Build the 88-byte [bbox][transform] signature for every env mesh, group identical signatures so
        // exact-duplicate meshes can be matched to file occurrences in order.
        var byMove = new HashSet<int>(moves.Select(m => m.Index));
        var sigByIndex = new Dictionary<int, byte[]>();
        var indicesBySig = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < envMeshes.Count; i++)
        {
            var sig = BuildSignature(envMeshes[i]);
            sigByIndex[i] = sig;
            var key = Convert.ToBase64String(sig);
            if (!indicesBySig.TryGetValue(key, out var list)) { list = new List<int>(); indicesBySig[key] = list; }
            list.Add(i);
        }

        foreach (var (key, indices) in indicesBySig)
        {
            if (!indices.Any(byMove.Contains)) continue;       // nothing moved in this signature group
            var sig = sigByIndex[indices[0]];
            var occurrences = FindAll(result, sig);            // file order
            int n = Math.Min(indices.Count, occurrences.Count);
            for (int rank = 0; rank < n; rank++)
            {
                int meshIndex = indices[rank];                 // mesh order ↔ file order
                var mv = moves.FirstOrDefault(m => m.Index == meshIndex);
                if (mv is null) continue;
                // translation = the M41/M42/M43 floats: 24 (bbox) + 48 (matrix row 4) into the signature.
                int t = occurrences[rank] + 24 + 48;
                var newT = envMeshes[meshIndex].Transform.Translation + mv.Offset;
                BinaryPrimitives.WriteSingleLittleEndian(result.AsSpan(t + 0, 4), newT.X);
                BinaryPrimitives.WriteSingleLittleEndian(result.AsSpan(t + 4, 4), newT.Y);
                BinaryPrimitives.WriteSingleLittleEndian(result.AsSpan(t + 8, 4), newT.Z);
            }
        }
        return result;
    }

    /// <summary>Patch the moves AND validate the result still reopens. Returns null + a reason on failure.</summary>
    public static byte[]? TryWriteWithMoves(byte[] originalMapgeo, IReadOnlyList<MapGeoMesh> meshes, out string? error)
    {
        try
        {
            var bytes = WriteWithMoves(originalMapgeo, meshes);
            using var verify = new MemoryStream(bytes, writable: false);
            _ = new EnvironmentAsset(verify); // throws if the patched mapgeo is unreadable
            error = null;
            return bytes;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    private static byte[] BuildSignature(EnvironmentAssetMesh m)
    {
        var b = new byte[88];
        var box = m.BoundingBox;
        var t = m.Transform;
        Span<float> f = stackalloc float[]
        {
            box.Min.X, box.Min.Y, box.Min.Z, box.Max.X, box.Max.Y, box.Max.Z,
            t.M11, t.M12, t.M13, t.M14, t.M21, t.M22, t.M23, t.M24,
            t.M31, t.M32, t.M33, t.M34, t.M41, t.M42, t.M43, t.M44,
        };
        for (int i = 0; i < f.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(i * 4, 4), f[i]);
        return b;
    }

    private static List<int> FindAll(byte[] hay, byte[] needle)
    {
        var hits = new List<int>();
        int i = 0;
        while (i <= hay.Length - needle.Length)
        {
            int found = IndexOf(hay, needle, i);
            if (found < 0) break;
            hits.Add(found);
            i = found + 1;
        }
        return hits;
    }

    private static int IndexOf(byte[] hay, byte[] needle, int from)
    {
        for (int i = from; i <= hay.Length - needle.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
                if (hay[i + j] != needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }
}
