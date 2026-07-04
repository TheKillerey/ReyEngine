using System.Buffers.Binary;
using System.Numerics;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.MapGeo;

/// <summary>
/// Persists particle moves into a map's .materials.bin by surgically patching each moved MapParticle's
/// transform translation in place (M35). Each particle is located by the exact 64 bytes of its ORIGINAL
/// transform matrix (positions are effectively unique), then only the 12 translation bytes are overwritten —
/// the rest of the file stays byte-exact. Verifies the result still parses.
/// </summary>
public static class MapParticleWriter
{
    /// <summary>Patch the given moves (original transform → new world position). Returns new bytes, or null + reason.</summary>
    public static byte[]? WriteMoves(byte[] materialsBin, IReadOnlyList<(Matrix4x4 original, Vector3 newPos)> moves, out string? error)
    {
        error = null;
        if (moves.Count == 0) return materialsBin;

        var result = (byte[])materialsBin.Clone();
        var consumed = new HashSet<int>();
        int patched = 0, missing = 0;

        foreach (var (orig, newPos) in moves)
        {
            var sig = MatrixBytes(orig);
            int off = FindNext(result, sig, consumed);
            if (off < 0) { missing++; continue; }
            consumed.Add(off);
            WriteFloat(result, off + 48, newPos.X); // M41
            WriteFloat(result, off + 52, newPos.Y); // M42
            WriteFloat(result, off + 56, newPos.Z); // M43
            patched++;
        }

        if (patched == 0) { error = "could not locate any of the moved particles' transforms in the .bin."; return null; }
        if (missing > 0) error = $"{missing} of {moves.Count} particle transform(s) could not be located (patched {patched}).";

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
