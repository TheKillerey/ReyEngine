namespace ReyEngine.Formats.Shaders;

/// <summary>How the fixed-function blend stage combines a fragment with what is already in the target.</summary>
public enum BlendKind
{
    /// <summary>No blending — the fragment replaces the target.</summary>
    Opaque,
    /// <summary>src·srcA + dst·(1−srcA). Straight (non-premultiplied) alpha.</summary>
    Alpha,
    /// <summary>src·srcA + dst. Cannot darken, so additive art is authored black-bordered.</summary>
    Additive,
}

/// <summary>
/// <para>M241 (phase 1): the render state a draw needs, described independently of any backend.</para>
///
/// <para>This exists as a SEPARATE description from <see cref="ShaderDescription"/> because Riot's shaders
/// do not contain render state and cannot be asked for it. Measured: <c>data/shaders/shaders.bin</c> holds
/// 347 <c>CustomShaderDef</c> objects whose full field-hash census yields 17 distinct field names, none
/// blend-related, and <c>particlesystem/quad_ps</c> declares no blend state either. DXBC carries the
/// program and its bindings; the pipeline state object lives in the client executable.</para>
///
/// <para>So a pipeline is (shader description) + (state description), resolved from two different sources
/// and combined at build time. Modelling state as part of the shader would describe something that does
/// not exist, and would make any cache keyed on it wrong.</para>
/// </summary>
public readonly record struct StateDescription(
    BlendKind Blend,
    bool DepthTest,
    bool DepthWrite,
    bool CullBackFaces,
    /// <summary>Alpha-test cutoff in 0..1. Zero discards nothing. Note this value also SELECTS the
    /// ALPHA_TEST permutation, so it belongs to both descriptions - the shader half decides whether the
    /// discard exists, this half decides where it cuts.</summary>
    float AlphaRef)
{
    /// <summary>
    /// Opaque scene geometry: map meshes and champion skins. Depth on, and back-face culling OFF because
    /// League's art is authored single-sided and a lot of it - capes, foliage cards - is meant to be seen
    /// from behind. Confirmed against the live game (M240).
    /// </summary>
    public static StateDescription Geometry => new(BlendKind.Alpha, true, true, false, 0f);

    /// <summary>
    /// Particles. Depth TEST off, because additively blended sprites must not occlude one another - with
    /// it on, a sprite spawning nearer the camera punches a hole in the ones behind it. Depth WRITE is off
    /// for the same reason. Confirmed against the live game (M240).
    /// </summary>
    public static StateDescription Particle(BlendKind blend, float alphaRef = 0f) =>
        new(blend, false, false, false, alphaRef);

    /// <summary>
    /// <para>Riot's <c>blendMode</c> integer. <b>This mapping is a guess</b> and is deliberately the same
    /// guess both renderers already make, so the two cannot disagree with each other while both being
    /// wrong about the client.</para>
    ///
    /// <para>The real table is not in shipped data - <c>shaders.bin</c> has no blend-related field on any
    /// of its 347 shader definitions - so it lives in the executable and would take a frame capture to
    /// settle. Modes 6, 7 and 8 (258 emitters) fall off the end of this list rather than being decided,
    /// and an absent blendMode defaults to 1 for 110,540 emitters (7.9%).</para>
    /// </summary>
    public static BlendKind BlendFromRiotMode(int blendMode) =>
        blendMode is 1 or 3 or 4 or 5 ? BlendKind.Additive : BlendKind.Alpha;

    /// <summary>True when this state is one the mapping above actually decided, rather than falling off
    /// the end of it. Lets a caller flag a draw as approximate instead of rendering it silently wrong.</summary>
    public static bool IsBlendModeUnderstood(int blendMode) => blendMode is >= 0 and <= 5;

    public override string ToString() =>
        $"{Blend}, depth {(DepthTest ? "test" : "off")}{(DepthWrite ? "+write" : "")}, "
        + $"cull {(CullBackFaces ? "back" : "none")}{(AlphaRef > 0 ? $", alphaRef {AlphaRef:F3}" : "")}";
}
