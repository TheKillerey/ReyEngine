using System.Buffers.Binary;
using System.Numerics;
using LeagueToolkit.Core.Environment;

namespace ReyEngine.Formats.MapGeo;

/// <summary>
/// Persists mesh moves/rotations/scales into a .mapgeo by surgically patching each edited mesh's
/// transform (and bounding box) in place, leaving the rest of the file byte-exact. LeagueToolkit's
/// <c>EnvironmentAsset.Write</c> is lossy for these versions (it produces an unreadable file), so we
/// never re-serialize — instead we locate each mesh via its unique <c>[BoundingBox(24)][Transform(64)]</c>
/// byte signature (the AABB disambiguates the many identical/identity transforms) and overwrite both
/// blocks with the recomputed values.
/// </summary>
public static class MapGeoWriter
{
    public static bool CanWriteBack => true;
    public static bool HasMoves(IEnumerable<MapGeoMesh> meshes) => meshes.Any(m => m.IsMoved);

    /// <summary>Patch the edited meshes' transforms into <paramref name="originalMapgeo"/> and return new bytes.</summary>
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

        foreach (var (_, indices) in indicesBySig)
        {
            if (!indices.Any(byMove.Contains)) continue;       // nothing edited in this signature group
            var sig = sigByIndex[indices[0]];
            var occurrences = FindAll(result, sig);            // file order
            int n = Math.Min(indices.Count, occurrences.Count);
            for (int rank = 0; rank < n; rank++)
            {
                int meshIndex = indices[rank];                 // mesh order ↔ file order
                var mv = moves.FirstOrDefault(m => m.Index == meshIndex);
                if (mv is null) continue;

                var original = envMeshes[meshIndex].Transform;
                var box = envMeshes[meshIndex].BoundingBox;
                var (newTransform, newMin, newMax) = ComputeNew(original, box.Min, box.Max, mv);

                int off = occurrences[rank];
                WriteVector3(result, off + 0, newMin);
                WriteVector3(result, off + 12, newMax);
                WriteMatrix(result, off + 24, newTransform);
            }
        }
        return result;
    }

    /// <summary>
    /// New transform + AABB for a mesh given its accumulated edit. Derivation: baked vertices are
    /// <c>local * OriginalTransform</c>; the self transform wants <c>pivot + SR*(baked - pivot) + offset</c>,
    /// then the world-space GroupMatrix (batch ops) is applied on top: W(p) = self(p) * GroupMatrix.
    /// Splitting OriginalTransform into linear L0 + translation T0: self-transform's matrix =
    /// (L0*SR) with translation pivot + SR*(T0 - pivot) + offset; the final file transform is that * GroupMatrix.
    /// The same full transform applied to the original AABB corners gives the new (axis-aligned) bounding box.
    /// </summary>
    private static (Matrix4x4 transform, Vector3 min, Vector3 max) ComputeNew(
        Matrix4x4 original, Vector3 boxMin, Vector3 boxMax, MapGeoMesh mv)
    {
        var sr = mv.ScaleRotationMatrix;
        var group = mv.GroupMatrix;
        var pivot = mv.Pivot;
        var offset = mv.Offset;

        var linear = original with { Translation = Vector3.Zero };
        var selfLinear = linear * sr;
        var selfTranslation = pivot + Vector3.Transform(original.Translation - pivot, sr) + offset;
        var selfTransform = selfLinear with { Translation = selfTranslation };
        var newTransform = selfTransform * group; // apply the batch/group affine after the self transform

        Vector3 newMin = new(float.MaxValue), newMax = new(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                (i & 1) == 0 ? boxMin.X : boxMax.X,
                (i & 2) == 0 ? boxMin.Y : boxMax.Y,
                (i & 4) == 0 ? boxMin.Z : boxMax.Z);
            var self = pivot + Vector3.Transform(corner - pivot, sr) + offset;
            var moved = Vector3.Transform(self, group);
            newMin = Vector3.Min(newMin, moved);
            newMax = Vector3.Max(newMax, moved);
        }
        return (newTransform, newMin, newMax);
    }

    /// <summary>Patch the edits AND validate the result still reopens. Returns null + a reason on failure.</summary>
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
        WriteVector3(b, 0, box.Min);
        WriteVector3(b, 12, box.Max);
        WriteMatrix(b, 24, m.Transform);
        return b;
    }

    private static void WriteVector3(byte[] b, int offset, Vector3 v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(offset + 0, 4), v.X);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(offset + 4, 4), v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(offset + 8, 4), v.Z);
    }

    private static void WriteMatrix(byte[] b, int offset, Matrix4x4 t)
    {
        Span<float> f = stackalloc float[]
        {
            t.M11, t.M12, t.M13, t.M14, t.M21, t.M22, t.M23, t.M24,
            t.M31, t.M32, t.M33, t.M34, t.M41, t.M42, t.M43, t.M44,
        };
        for (int i = 0; i < f.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(offset + i * 4, 4), f[i]);
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
