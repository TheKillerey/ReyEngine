using System.Numerics;

namespace ReyEngine.Formats.Vfx;

/// <summary>M720: the engine's <c>ParticleSystem::BLEND_MODE</c>, the enum <c>blendMode</c> is written in.</summary>
public enum VfxBlendMode
{
    Add = 0,
    Alpha = 1,
    Subtract = 2,
    None = 3,
    AlphaAdd = 4,
    PremultipliedAlpha = 5,
    Min = 6,
    Max = 7,
    TargetAlpha = 8,
}

/// <summary>A blend factor, named the way both renderers' own enums name it.</summary>
public enum VfxBlendFactor { Zero, One, SrcAlpha, InvSrcAlpha, InvSrcColor, DestAlpha, InvDestAlpha }

/// <summary>The colour blend equation. The alpha equation is always Add - see <see cref="VfxBlendState"/>.</summary>
public enum VfxBlendOp { Add, Min, Max }

/// <summary>
/// M720: which blend table the renderers draw with. Pure data, passed in, so a test never has to write the
/// process-wide default and race another test class that reads it.
/// </summary>
/// <param name="EngineModes">The engine's enum, the CPU premultiply, the per-mode soft fade and draw-order
/// keys 3 and 4. False restores the pre-M720 table (1/3/4/5 additive, 2 and 3 decided by the sprite) for
/// an A/B - read when a system is built, so flipping it means rebuilding the playback.</param>
/// <param name="NoneWritesDepth">NONE writes depth. Its own switch because the reading is the mesh
/// path's, and a depth-writing particle is the one change here that can hide a different system.</param>
public readonly record struct VfxBlendOptions(bool EngineModes = true, bool NoneWritesDepth = true)
{
    public static VfxBlendOptions Engine => new(true, true);
    public static VfxBlendOptions Legacy => new(false, false);
}

/// <summary>
/// M720: the state one emitter blends with, in terms both renderers translate rather than restate.
///
/// <para><b>No particle state writes destination alpha, and that is deliberate.</b> The engine gives two
/// modes alpha lanes of their own, but both of our presentations composite the target's alpha - the
/// Direct3D 11 readback lands in a premultiplied bitmap and the OpenGL framebuffer is blitted into the
/// window - so any alpha short of one lets the window through (the reference renderer's 2.48, which it
/// fixed with an opaque buffer). Both renderers therefore mask alpha out of the write for every particle
/// draw, which also keeps it out of MIN and MAX, whose equations ignore the blend factors entirely.</para>
/// </summary>
public readonly record struct VfxBlendState(
    VfxBlendMode Mode,
    bool Enabled,
    VfxBlendFactor Src,
    VfxBlendFactor Dst,
    VfxBlendOp Op,
    bool WritesColor,
    bool WritesDepth,
    bool AddsToTarget,
    string Why)
{
    /// <summary>Never true: see the type's remarks. A property rather than an omission so it can be tested.</summary>
    public bool WritesAlpha => false;

    /// <summary>For build logs and frame reports: the mode's name and what it resolved to.</summary>
    public string Describe() =>
        !WritesColor ? $"{Mode} (writes no colour)"
        : !Enabled ? $"{Mode} (opaque{(WritesDepth ? ", writes depth" : "")})"
        : $"{Mode} ({Src}, {Dst}{(Op == VfxBlendOp.Add ? "" : ", " + Op)})";
}

/// <summary>
/// M720: how a particle emitter blends - ltk-manager's plan, section 3.2 (the enum), 2.19 (all nine modes
/// read), 2.17 (NONE writes depth), 2.49 (ADD and SUBTRACT premultiply on the CPU) and 2.11 (the blend rank
/// in the draw order).
///
/// <para><b>Why this replaces the table we had.</b> Ours read 1, 3, 4 and 5 as additive and let the
/// sprite decide 2 and 3. It came from our own renderer's screen (M117, M117c, M273), never the client,
/// and the engine's names for 1 and 3 are ALPHA and NONE. Four things agree with the engine instead: its
/// enum names; the schema's declared default of 0 (ADD), which Riot's writer omits - 0 is written zero
/// times in 1,581,956 emitters; our own M260 art census, which found 1% additive art under mode 1, opaque
/// art under 3, premultiplied art under 5 and flat colour holds under 8; and Riot's compiled particle
/// shaders, 2,553 permutations with no blend axis and no stage that weighs colour by alpha - so ADD's
/// ONE,ONE really does need the colour weighted on the CPU. The factor each mode indexes is the reference
/// renderer's inference (it says so), and for six of the nine modes no screen has shown it.</para>
///
/// <para><b>What moves.</b> 767,912 of 1,523,119 reachable emitters change state; 604,446 of them are
/// mode 1, which drew additive and draws straight alpha. Mode 4 (ALPHAADD, 755,207) is the one mode that
/// already matched.</para>
/// </summary>
public static class VfxBlend
{
    /// <summary>The host default, read when a system is built. Tests pass options explicitly instead.</summary>
    public static VfxBlendOptions Options { get; set; } = VfxBlendOptions.Engine;

    /// <summary>The written byte as the enum. Anything outside 0..8 reads as ADD, the declared default -
    /// none is authored, so the fallback is moot, and the reference renderer falls back the same way.</summary>
    public static VfxBlendMode ModeOf(int blendMode) =>
        blendMode is >= 0 and <= 8 ? (VfxBlendMode)blendMode : VfxBlendMode.Add;

    /// <summary>One definition of a heat-haze emitter for both renderers: a distortion block that names a
    /// normal map. The OpenGL quad path tested the block alone and the Direct3D 11 recipe the map as well,
    /// so a block with no map drew as a warp in one viewport and as colour in the other.</summary>
    public static bool IsDistortion(VfxEmitterDefinition e) =>
        e.Distortion is { NormalMapTexturePath.Length: > 0 };

    /// <summary>The state an emitter draws with. <paramref name="textureHasAlpha"/> matters only to the
    /// legacy table, which let the sprite decide modes 2 and 3.</summary>
    public static VfxBlendState StateFor(VfxEmitterDefinition e, bool? textureHasAlpha, VfxBlendOptions options)
    {
        var mode = ModeOf(e.BlendMode);

        // 2.25: a warp is laid back over the frame under its own mask, whatever the emitter authors.
        if (IsDistortion(e))
            return new(mode, true, VfxBlendFactor.SrcAlpha, VfxBlendFactor.InvSrcAlpha, VfxBlendOp.Add,
                true, false, false, "distortion draws straight alpha whatever the mode (2.25)");

        if (!options.EngineModes)
        {
            // The pre-M720 table. An absent blendMode read as 1 then and reads as 0 now; 0 is never written,
            // so mapping it back is exact.
            int legacy = e.BlendMode == 0 ? 1 : e.BlendMode;
            return VfxShaderFlags.IsAdditive(legacy, textureHasAlpha)
                ? new(mode, true, VfxBlendFactor.SrcAlpha, VfxBlendFactor.One, VfxBlendOp.Add, true, false, true,
                    "legacy table: additive")
                : new(mode, true, VfxBlendFactor.SrcAlpha, VfxBlendFactor.InvSrcAlpha, VfxBlendOp.Add, true, false, false,
                    "legacy table: alpha");
        }

        // WriteAlphaOnly (249 emitters, all Vex): the engine writes no colour at all. This editor writes no
        // alpha either, so the draw shows nothing - which is the engine's colour result.
        if (e.Extras?.WriteAlphaOnly == true)
            return new(mode, true, VfxBlendFactor.Zero, VfxBlendFactor.One, VfxBlendOp.Add, false, false, false,
                "WriteAlphaOnly: the engine writes no colour");

        return mode switch
        {
            VfxBlendMode.Add => new(mode, true, VfxBlendFactor.One, VfxBlendFactor.One, VfxBlendOp.Add,
                true, false, true, "ADD: the colour whole, weighted by its alpha on the CPU (2.49)"),
            VfxBlendMode.Alpha => new(mode, true, VfxBlendFactor.SrcAlpha, VfxBlendFactor.InvSrcAlpha, VfxBlendOp.Add,
                true, false, false, "ALPHA: straight alpha"),
            VfxBlendMode.Subtract => new(mode, true, VfxBlendFactor.Zero, VfxBlendFactor.InvSrcColor, VfxBlendOp.Add,
                true, false, false, "SUBTRACT: a darken, dst * (1 - src), weighted on the CPU (2.19, 2.49)"),
            VfxBlendMode.None => new(mode, false, VfxBlendFactor.One, VfxBlendFactor.Zero, VfxBlendOp.Add,
                true, options.NoneWritesDepth, false, "NONE: opaque, and the one mode that writes depth (2.17)"),
            VfxBlendMode.AlphaAdd => new(mode, true, VfxBlendFactor.SrcAlpha, VfxBlendFactor.One, VfxBlendOp.Add,
                true, false, true, "ALPHAADD: the colour scaled by its alpha, added"),
            VfxBlendMode.PremultipliedAlpha => new(mode, true, VfxBlendFactor.One, VfxBlendFactor.InvSrcAlpha, VfxBlendOp.Add,
                true, false, false, "PREMULTIPLIEDALPHA"),
            VfxBlendMode.Min => new(mode, true, VfxBlendFactor.One, VfxBlendFactor.One, VfxBlendOp.Min,
                true, false, false, "MIN"),
            VfxBlendMode.Max => new(mode, true, VfxBlendFactor.One, VfxBlendFactor.One, VfxBlendOp.Max,
                true, false, false, "MAX"),
            // The engine's InvDestAlpha, DestAlpha reads a destination alpha this editor pins at 1, which is
            // (1 - 1) * src + 1 * dst: nothing. Drawn as that answer explicitly, so a viewport whose target
            // alpha has drifted below 1 cannot show a faint quad the other viewport does not.
            _ => new(mode, true, VfxBlendFactor.Zero, VfxBlendFactor.One, VfxBlendOp.Add,
                true, false, false, "TARGETALPHA: reads a destination alpha of 1 here, so draws nothing"),
        };
    }

    /// <summary>StateFor under the host default.</summary>
    public static VfxBlendState StateFor(VfxEmitterDefinition e, bool? textureHasAlpha = null) =>
        StateFor(e, textureHasAlpha, Options);

    /// <summary>
    /// 2.49: ADD and SUBTRACT weigh the colour by its own alpha on the CPU and write an alpha of one.
    ///
    /// <para>The simulator applies it BEFORE the particleColorTexture gradient, which we sample on the CPU
    /// but the engine samples per pixel inside quad_ps and mesh_ps - so the gradient's alpha must still reach
    /// the output alpha (and the alpha test) rather than scale the colour.</para>
    /// </summary>
    public static bool Premultiplies(VfxEmitterDefinition e, VfxBlendOptions options) =>
        options.EngineModes && !IsDistortion(e) && ModeOf(e.BlendMode) is VfxBlendMode.Add or VfxBlendMode.Subtract;

    /// <summary>The colour an ADD or SUBTRACT emitter's vertex carries: rgb weighted by alpha, alpha one.</summary>
    public static Vector4 Premultiply(Vector4 colour) =>
        new(colour.X * colour.W, colour.Y * colour.W, colour.Z * colour.W, 1f);

    /// <summary>2.11's third draw-order key: NONE first, then ADD and SUBTRACT, then the blended modes, then
    /// TARGETALPHA - the reference renderer's BLEND_RANK { 1, 2, 1, 0, 2, 2, 2, 2, 3 } indexed by the mode.</summary>
    public static int DrawRank(int blendMode) => ModeOf(blendMode) switch
    {
        VfxBlendMode.None => 0,
        VfxBlendMode.Add or VfxBlendMode.Subtract => 1,
        VfxBlendMode.TargetAlpha => 3,
        _ => 2,
    };

    /// <summary>
    /// cSoftParticleControl: which channel the soft fade lands in, (rgb keep, rgb fade, alpha keep, alpha
    /// fade). The bytecode takes it from the CPU and cannot say what feeds it.
    ///
    /// <para>For ADD and SUBTRACT it is forced: after the premultiply their alpha is one and ONE,ONE ignores
    /// it, so only an rgb fade can fade them. ALPHA and ALPHAADD fade alpha, which their factors weigh the
    /// colour by. PREMULTIPLIEDALPHA fades both. NONE, MIN, MAX and TARGETALPHA fade rgb, which the reference
    /// renderer calls the user's pick rather than a reading.</para>
    /// </summary>
    public static Vector4 SoftControl(VfxEmitterDefinition e, VfxBlendOptions options)
    {
        if (!options.EngineModes)
            return VfxShaderFlags.IsAdditive(e.BlendMode == 0 ? 1 : e.BlendMode)
                ? new Vector4(0f, 1f, 1f, 0f) : new Vector4(1f, 0f, 0f, 1f);
        return SoftControl(ModeOf(e.BlendMode));
    }

    public static Vector4 SoftControl(VfxBlendMode mode) => mode switch
    {
        VfxBlendMode.Alpha or VfxBlendMode.AlphaAdd => new Vector4(1f, 0f, 0f, 1f),
        VfxBlendMode.PremultipliedAlpha => new Vector4(0f, 1f, 0f, 1f),
        _ => new Vector4(0f, 1f, 1f, 0f),
    };
}
