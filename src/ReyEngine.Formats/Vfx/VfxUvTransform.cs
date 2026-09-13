using System.Numerics;

namespace ReyEngine.Formats.Vfx;

/// <summary>
/// M719: one texture layer's uv transform - the numbers a particle's corner is carried through before its
/// flipbook cell is added. <see cref="BaseOf"/> and <see cref="MultOf"/> are the only places the fields are
/// gathered, so the two renderers cannot read different ones.
/// </summary>
public readonly record struct VfxUvLayer(
    Vector2 Offset,
    Vector2 BirthScrollRate,
    Vector2 IntegratedScrollRate,
    Vector2 EmitterScrollRate,
    bool ScrollClamp,
    Vector2 Scale,
    float RotationDegrees,
    float RotateRateDegrees,
    Vector2 Center,
    bool FlipU,
    bool FlipV)
{
    /// <summary>The base layer. particleUVRotateRate is integrated, which for the constant we read is the
    /// same as a rate, so the two rotate rates are one number here.</summary>
    public static VfxUvLayer BaseOf(VfxEmitterDefinition e) => new(
        e.UvOffset, e.UvScrollRate, e.UvScrollIntegrated, e.EmitterUvScrollRate, e.UvScrollClamp,
        e.UvScale, e.UvRotation, e.UvRotateRate + e.UvRotateIntegrated, e.UvTransformCenter,
        e.UvFlipU, e.UvFlipV);

    /// <summary>The textureMult layer's translation. Its scale, rotation and flips are not read yet and
    /// stand at the identity; an emitter with no multiplier gets the identity outright.</summary>
    public static VfxUvLayer MultOf(VfxEmitterDefinition e) => string.IsNullOrEmpty(e.TextureMultPath)
        ? default
        : new(e.TextureMultUvOffset, e.TextureMultUvScrollRate, e.TextureMultUvScrollIntegrated,
            e.TextureMultEmitterUvScrollRate, e.TextureMultUvScrollClamp, Vector2.One, 0f, 0f,
            new Vector2(0.5f, 0.5f), false, false);
}

/// <summary>
/// M719: the transform itself, from reading 2.11 and section 3.4 of ltk-manager's plan.
///
/// <para><b>The birth ramp is clamped or wrapped on its own.</b> <c>birthUVOffset + age *
/// birthUvScrollRate</c> is held to [-1, 1] under <c>uvScrollClamp</c> and otherwise wrapped into [0, 1).
/// The integrated scroll and <c>emitterUvScrollRate</c> times the system's clock are added after, and
/// nothing clamps them. The GL viewport had clamped the WHOLE coordinate to [0, 1] instead - scale,
/// rotation and every scroll with it - and the Direct3D 11 builder had applied the birth scroll alone.</para>
///
/// <para><b>A flip is a post-multiply</b> (2.13), so it lands after the translation and a flipped layer's
/// scroll reverses. The GL viewport flipped first.</para>
///
/// <para><b>The unit is the cell.</b> Riot's quad_vs computes <c>(col + u) / cols</c> from what the CPU
/// writes, and the reading clamps the ramp - offset and scroll together - to one cell either way, with no
/// factor of the grid anywhere. That is inferred from the reading and the shader, not measured against the
/// client. It moves birthUvScrollRate out of texture space on the 2,611 quads that author one over a grid.</para>
///
/// <para>Constants only: the resolver reads each field's constantValue, so a ramp authored through keys or
/// probability tables is not drawn per particle here.</para>
/// </summary>
public static class VfxUvTransform
{
    /// <summary>How far uvScrollClamp lets the birth ramp run, in cells either way.</summary>
    public const float RampReach = 1f;

    /// <summary>The birth ramp on one axis. In double so a long-lived particle's wrap does not stair-step.</summary>
    public static float Ramp(float offset, float rate, float age, bool clamp)
    {
        double ramp = offset + (double)rate * age;
        return (float)(clamp ? Math.Clamp(ramp, -RampReach, RampReach) : ramp - Math.Floor(ramp));
    }

    /// <summary>The whole translation, in cells: the ramp, then the two scrolls nothing clamps.</summary>
    public static Vector2 Translation(in VfxUvLayer layer, float age, float systemTime) => new(
        Ramp(layer.Offset.X, layer.BirthScrollRate.X, age, layer.ScrollClamp)
            + layer.IntegratedScrollRate.X * age + layer.EmitterScrollRate.X * systemTime,
        Ramp(layer.Offset.Y, layer.BirthScrollRate.Y, age, layer.ScrollClamp)
            + layer.IntegratedScrollRate.Y * age + layer.EmitterScrollRate.Y * systemTime);

    /// <summary>A corner (u, v) in [0, 1] carried to its coordinate inside the cell: scale and rotate about
    /// the centre, translate, then flip. A zero scale component reads as 1, as the GL upload always did.</summary>
    public static Vector2 Cell(in VfxUvLayer layer, float u, float v, float age, float systemTime)
    {
        var scale = new Vector2(layer.Scale.X == 0f ? 1f : layer.Scale.X, layer.Scale.Y == 0f ? 1f : layer.Scale.Y);
        var uv = (new Vector2(u, v) - layer.Center) * scale;
        float angle = (layer.RotationDegrees + layer.RotateRateDegrees * age) * (MathF.PI / 180f);
        if (MathF.Abs(angle) > 0.0001f)
        {
            float cs = MathF.Cos(angle), sn = MathF.Sin(angle);
            uv = new Vector2(uv.X * cs - uv.Y * sn, uv.X * sn + uv.Y * cs);
        }
        uv += layer.Center + Translation(layer, age, systemTime);
        if (layer.FlipU) uv.X = 1f - uv.X;
        if (layer.FlipV) uv.Y = 1f - uv.Y;
        return uv;
    }

    /// <summary>The same formula for the OpenGL quad vertex shader, which concatenates this constant.
    /// ASCII only: a non-ASCII byte compiles in C# and blanks the viewport at the driver.</summary>
    public const string Glsl = @"
float reyUvRamp(float ramp, int clampRamp) {
    return clampRamp != 0 ? clamp(ramp, -1.0, 1.0) : ramp - floor(ramp);
}
vec2 reyUvCell(vec2 corner, float age, float systemTime, vec2 offset, vec2 birthRate, vec2 integratedRate,
               vec2 emitterRate, int clampRamp, vec2 scale, float angle, vec2 center, vec2 flip) {
    vec2 uv = (corner - center) * scale;
    if (abs(angle) > 0.0001) {
        float cs = cos(angle);
        float sn = sin(angle);
        uv = vec2(uv.x * cs - uv.y * sn, uv.x * sn + uv.y * cs);
    }
    vec2 ramp = offset + birthRate * age;
    uv += center + vec2(reyUvRamp(ramp.x, clampRamp), reyUvRamp(ramp.y, clampRamp))
        + integratedRate * age + emitterRate * systemTime;
    if (flip.x > 0.5) uv.x = 1.0 - uv.x;
    if (flip.y > 0.5) uv.y = 1.0 - uv.y;
    return uv;
}
";
}
