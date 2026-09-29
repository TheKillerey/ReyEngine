using System.Numerics;

namespace ReyEngine.Formats.Vfx;

/// <summary>
/// M795: an emitter's own frame inside its system, from <c>scaleOverride</c> and <c>translationOverride</c>.
///
/// <para>Map22 darkstar_blackhole showed a dark ring wall round a purple dome in the middle of the board.
/// That is <c>TFT_Skybox_Darkstar_A</c>: seven mesh emitters on inward-facing sky domes, authored at
/// birthScale 0.2-0.27 with scaleOverride 17-18, and four of them with translationOverride (0, -6000, 0).
/// Both fields were parsed and never applied (M276 found the same on darkstar_supernova), so the sky drew
/// at 1/18 of its size - the stars dome (TFT_Skybox_BackgroundClouds.scb, 4,576 across) became a 915-unit
/// ring on the board - and the cloud layers sat 6,000 units above where they belong.</para>
///
/// <para><b>The reading.</b> The override is the emitter's transform in its system, applied as a TRS
/// matrix applies to a point: the emitter's space is scaled by scaleOverride - its particles' positions
/// (EmitterPosition, spawn offsets), their motion and their own size - and then moved by
/// translationOverride, which is NOT scaled. Why the positions scale with the size: the corpus authors
/// these effects at 1/16 - 1/18 and scales them up (697 map emitters, 536 of them at 16-18), and the small
/// offsets beside them only keep their meaning at scale - darkstar's Pink01/Pink02 are one mesh 10 units
/// apart and Glow is nudged by (2, 0, -10). Why the translation does not: (0, -6000, 0) is about one dome
/// radius in world units, and 17 times it (102,000) would put the clouds out of any view. LTK Manager's
/// engine turns the translation by the scaled frame instead; it records scaleOverride as authored on zero
/// objects of its dump, so it never met the case. Neither choice has been compared with the client.</para>
///
/// <para><c>rotationOverride</c> is still not applied (4,368 emitters; M793 lists it open).</para>
/// </summary>
public static class VfxEmitterOverride
{
    /// <summary>scaleOverride per axis, or (1, 1, 1) when the emitter authors none (or a non-finite one).</summary>
    public static Vector3 Scale(VfxEmitterDefinition e) =>
        e.Extras?.ScaleOverride is { } s && float.IsFinite(s.X) && float.IsFinite(s.Y) && float.IsFinite(s.Z) ? s : Vector3.One;

    /// <summary>translationOverride, or zero when the emitter authors none (or a non-finite one).</summary>
    public static Vector3 Translation(VfxEmitterDefinition e) =>
        e.Extras?.TranslationOverride is { } t && float.IsFinite(t.X) && float.IsFinite(t.Y) && float.IsFinite(t.Z) ? t : Vector3.Zero;

    /// <summary>Whether the emitter authors a frame other than the identity.</summary>
    public static bool IsAuthored(VfxEmitterDefinition e) => Scale(e) != Vector3.One || Translation(e) != Vector3.Zero;

    /// <summary>The emitter's frame in its system's space, in System.Numerics' row-vector order: a point p of
    /// the emitter's space lands at <c>p * scaleOverride + translationOverride</c>. Exactly the identity for an
    /// emitter that authors neither.</summary>
    public static Matrix4x4 Frame(VfxEmitterDefinition e) => IsAuthored(e)
        ? Matrix4x4.CreateScale(Scale(e)) * Matrix4x4.CreateTranslation(Translation(e))
        : Matrix4x4.Identity;
}
