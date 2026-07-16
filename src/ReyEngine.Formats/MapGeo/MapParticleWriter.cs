using System.Buffers.Binary;
using System.Linq;
using System.Numerics;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.MapGeo;

/// <summary>
/// Persists placement edits into a map's .materials.bin by surgically patching each moved placeable's
/// transform in place (M35 particles; M75 full transforms + sounds). Each placement is located by the
/// exact 64 bytes of its ORIGINAL transform matrix (positions are effectively unique) — the rest of the
/// file stays byte-exact. Verifies the result still parses. Works for ANY placeable that stores a 4x4
/// transform (MapParticle, MapAudio, ...), since the locator is the raw matrix signature.
/// </summary>
public static class MapParticleWriter
{
    /// <summary>Patch the given moves (original transform → new world position). Returns new bytes, or null + reason.</summary>
    public static byte[]? WriteMoves(byte[] materialsBin, IReadOnlyList<(Matrix4x4 original, Vector3 newPos)> moves, out string? error)
    {
        return WriteTransforms(materialsBin,
            moves.Select(m => { var t = m.original; t.Translation = m.newPos; return (m.original, t); }).ToList(),
            out error);
    }

    /// <summary>M75: patch full replacement transforms (position + rotation + scale). Each edit's original
    /// matrix is the 64-byte locator; all 64 bytes are overwritten with the new matrix.</summary>
    public static byte[]? WriteTransforms(byte[] materialsBin, IReadOnlyList<(Matrix4x4 original, Matrix4x4 replacement)> edits, out string? error)
    {
        error = null;
        if (edits.Count == 0) return materialsBin;

        var result = (byte[])materialsBin.Clone();
        var consumed = new HashSet<int>();
        int patched = 0, missing = 0;

        foreach (var (orig, replacement) in edits)
        {
            var sig = MatrixBytes(orig);
            int off = FindNext(result, sig, consumed);
            if (off < 0) { missing++; continue; }
            consumed.Add(off);
            var repl = MatrixBytes(replacement);
            Array.Copy(repl, 0, result, off, 64);
            patched++;
        }

        if (patched == 0) { error = "could not locate any of the edited placements' transforms in the .bin."; return null; }
        if (missing > 0) error = $"{missing} of {edits.Count} placement transform(s) could not be located (patched {patched}).";

        // validate the patched bin still parses
        try { _ = SafeBinTree.Parse(result); }
        catch (Exception ex) { error = $"patched .bin no longer parses: {ex.Message}"; return null; }
        return result;
    }

    private static byte[] MatrixBytes(Matrix4x4 t)
    {
        var b = new byte[64];
        Span<float> f = stackalloc float[]
        {
            t.M11, t.M12, t.M13, t.M14, t.M21, t.M22, t.M23, t.M24,
            t.M31, t.M32, t.M33, t.M34, t.M41, t.M42, t.M43, t.M44,
        };
        for (int i = 0; i < 16; i++) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(i * 4, 4), f[i]);
        return b;
    }

    private static void WriteFloat(byte[] b, int off, float v) => BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(off, 4), v);

    private static int FindNext(byte[] hay, byte[] needle, HashSet<int> skip)
    {
        for (int i = 0; i + needle.Length <= hay.Length; i++)
        {
            if (skip.Contains(i)) continue;
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
                if (hay[i + j] != needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }
}
