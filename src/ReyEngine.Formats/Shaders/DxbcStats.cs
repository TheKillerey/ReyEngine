using System.Buffers.Binary;

namespace ReyEngine.Formats.Shaders;

/// <summary>
/// M829: the compiler's own statistics for a shader (DXBC <c>STAT</c> chunk) plus a texture-fetch count taken
/// from the shader's own opcode stream.
///
/// <para><see cref="TextureSamples"/> is COUNTED from the SHEX token stream: every sample opcode (sample, sample_c,
/// sample_c_lz, sample_l, sample_d, sample_b). The STAT dword that looks like the same thing (dword 14) is only the
/// "normal" samples - sample plus sample_b - and reads 0 on the 39 vertex shaders tried that fetch with sample_l
/// (cloth, TFT flowmaps), so it is NOT used for the figure. <see cref="StatNormalSamples"/> keeps that dword so a
/// test can pin it against the opcode stream (it matched on 214 of 214 random Riot blobs).</para>
///
/// <para>The instruction count (dword 0) is the compiler's own number and runs a little HIGHER than a plain opcode
/// count on about half of the blobs, so it is labelled as the compiler's figure. A blob without a STAT chunk yields
/// <c>null</c> - never a guessed zero.</para>
/// </summary>
public sealed record DxbcStats(int Instructions, int TempRegisters, int TextureSamples, int StatNormalSamples)
{
    public static DxbcStats? Read(DxbcShader shader)
    {
        var bytes = shader.Bytecode;
        int statOff = -1, statSize = 0, shexOff = -1, shexSize = 0;
        foreach (var (tag, off, size) in DxbcReflection.Chunks(bytes))
        {
            if (tag == "STAT") { statOff = off; statSize = size; }
            else if (tag is "SHEX" or "SHDR") { shexOff = off; shexSize = size; }
        }
        if (statOff < 0 || statSize < 15 * 4 || shexOff < 0) return null;

        int At(int i) => (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(statOff + i * 4, 4)), int.MaxValue);
        return new DxbcStats(At(0), At(1), CountSampleOpcodes(bytes, shexOff, shexSize), At(14));
    }

    /// <summary>Opcodes 69..74 (sample, sample_c, sample_c_lz, sample_l, sample_d, sample_b) of an SHEX/SHDR chunk.</summary>
    public static int CountSampleOpcodes(byte[] bytes, int off, int size) => CountOpcodes(bytes, off, size, 69, 74, normalOnly: false);

    /// <summary>Only sample (69) and sample_b (74): what STAT dword 14 counts.</summary>
    public static int CountNormalSampleOpcodes(byte[] bytes, int off, int size) => CountOpcodes(bytes, off, size, 69, 74, normalOnly: true);

    private static int CountOpcodes(byte[] bytes, int off, int size, uint lo, uint hi, bool normalOnly)
    {
        int count = 0, i = 2, tokens = size / 4;
        while (i < tokens)
        {
            uint t = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(off + i * 4, 4));
            uint op = t & 0x7FF;
            int len = (int)((t >> 24) & 0x7F);
            if (op == 53)   // customdata: its length is the next dword
            {
                if (i + 1 >= tokens) break;
                len = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(off + (i + 1) * 4, 4));
            }
            if (len <= 0 || len > tokens) break;
            if (op >= lo && op <= hi && (!normalOnly || op is 69 or 74)) count++;
            i += len;
        }
        return count;
    }
}
