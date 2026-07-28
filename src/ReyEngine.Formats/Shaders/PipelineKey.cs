namespace ReyEngine.Formats.Shaders;

/// <summary>Which rendering backend a pipeline was built for. Only DX11 runs Riot's bytecode unmodified;
/// anything else implies a translation step, which is where both accuracy and update-compatibility are
/// spent.</summary>
public enum RenderBackend { D3D11, OpenGL }

/// <summary>
/// <para>M241 (phase 1): identity for a built pipeline, so one can be reused instead of rebuilt.</para>
///
/// <para>Two departures from the obvious key, both deliberate:</para>
///
/// <para><b>State is part of the key.</b> The same shader permutation drawn additively and drawn with
/// straight alpha is two different pipeline objects. Keying on the shader alone would hand back a pipeline
/// with the wrong blend - and since particle emitters within a single system routinely mix the two, that
/// is not a rare case.</para>
///
/// <para><b>GPU vendor is NOT part of the key.</b> The brief suggested it, and it belongs there only for
/// TRANSLATED shaders, where a translator might legitimately emit different code per vendor. For native
/// DXBC the driver keeps its own compiled-shader cache keyed on the bytecode itself, so adding vendor here
/// would only fragment ours and multiply the work on a machine that needs it least.</para>
///
/// <para><see cref="GameVersion"/> is strictly redundant against <see cref="BytecodeHash"/> for
/// correctness - different bytes give a different hash whatever the version says. It is kept because it is
/// what lets a cache be PRUNED on patch day, and what a user-facing "shaders rebuilt for patch 15.3"
/// message keys on.</para>
/// </summary>
public readonly record struct PipelineKey(
    string VertexShader,
    ulong VertexPermutation,
    string PixelShader,
    ulong PixelPermutation,
    ulong BytecodeHash,
    string GameVersion,
    RenderBackend Backend,
    StateDescription State)
{
    public static PipelineKey For(ShaderDescription vs, ShaderDescription ps,
        StateDescription state, string gameVersion, RenderBackend backend) =>
        new(vs.ShaderName, vs.PermutationKey, ps.ShaderName, ps.PermutationKey,
            HashBytecode(vs.Reflection.Bytecode, ps.Reflection.Bytecode),
            gameVersion, backend, state);

    /// <summary>FNV-1a over both stages. Not cryptographic - this only has to notice that Riot changed the
    /// blob under a name we already cached, which a patch does by rewriting the bytes.</summary>
    public static ulong HashBytecode(ReadOnlySpan<byte> vs, ReadOnlySpan<byte> ps)
    {
        const ulong offset = 14695981039346656037UL, prime = 1099511628211UL;
        ulong h = offset;
        foreach (byte b in vs) { h ^= b; h *= prime; }
        h ^= 0xFF; h *= prime;                       // separator, so (a,b) and (ab,"") differ
        foreach (byte b in ps) { h ^= b; h *= prime; }
        return h;
    }

    public override string ToString() =>
        $"{VertexShader.Split('/').LastOrDefault()}#{VertexPermutation:x8}"
        + $" + {PixelShader.Split('/').LastOrDefault()}#{PixelPermutation:x8}"
        + $" [{Backend}] {State}";
}
