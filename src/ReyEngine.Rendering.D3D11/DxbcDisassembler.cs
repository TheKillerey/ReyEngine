using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;

namespace ReyEngine.Rendering.D3D11;

/// <summary>
/// M829: D3DDisassemble over a DXBC blob - the assembly listing the Material Graph's "Shader Code" tab shows.
/// Riot ships compiled bytecode only (no HLSL source for the DX11 cache), so the disassembly is the real,
/// complete text that exists for a permutation. d3dcompiler_47.dll is a Windows system component; where it is
/// missing this returns false with the reason instead of an empty listing.
/// </summary>
public static unsafe class DxbcDisassembler
{
    public static bool TryDisassemble(byte[] bytecode, out string text, out string? error)
    {
        text = "";
        error = null;
        if (bytecode is null || bytecode.Length == 0) { error = "no bytecode"; return false; }

        ID3D10Blob* listing = null;
        int hr;
        try
        {
            var compiler = D3DCompiler.GetApi();
            fixed (byte* p = bytecode)
                hr = compiler.Disassemble(p, (nuint)bytecode.Length, 0u, (byte*)null, &listing);
        }
        catch (Exception ex)
        {
            error = "the HLSL compiler library is unavailable: " + ex.Message;
            return false;
        }

        if (hr < 0 || listing is null)
        {
            error = string.Format("D3DDisassemble failed 0x{0:X8}", hr);
            return false;
        }

        try
        {
            int len = (int)listing->GetBufferSize();
            // the listing is ASCII and NUL-terminated
            text = System.Text.Encoding.ASCII.GetString((byte*)listing->GetBufferPointer(), len).TrimEnd('\0');
            return true;
        }
        finally { listing->Release(); }
    }
}
