using System.Numerics;

namespace ReyEngine.Formats.Vfx;

/// <summary>
/// A parsed League VFX system (M36) — one <c>VfxSystemDefinitionData</c> and its emitters, enough to
/// actually simulate/preview the particles (birth rate, lifetime, size/colour/velocity over life, texture,
/// flipbook, blend). This is a faithful-but-approximate model of Riot's runtime, not a 1:1 reproduction.
/// </summary>
public sealed record VfxSystemDefinition(
    uint PathHash,
    string Name,
    string ParticlePath,
    IReadOnlyList<VfxEmitterDefinition> Emitters);

/// <summary>One emitter inside a system. Curves are absolute-valued (sampled over normalised particle age 0..1).</summary>
public sealed record VfxEmitterDefinition(
    string Name,
    VfxCurveF Rate,                 // particles per second
    VfxCurveF ParticleLifetime,     // seconds a particle lives
    float? EmitterLifetime,         // emitter runtime; null = infinite (loops)
    float ParticleLinger,           // extra fade time after emitter stops
    float TimeBeforeFirstEmission,
    bool IsSingleParticle,          // burst of exactly one particle
    bool Disabled,
    int BlendMode,                  // 1 = additive (most VFX), else alpha
    VfxCurve3 BirthScale,           // ABSOLUTE size at birth (birthScale0), world units
    VfxCurve3? ScaleOverLife,       // scale0: normalised MULTIPLIER over age → effective size = BirthScale * this
    VfxCurve4 BirthColor,           // rgba at birth
    VfxCurve4? ColorOverLife,       // color: MULTIPLIER over age → effective colour = BirthColor * this (alpha usually fades)
    VfxCurve3? BirthVelocity,       // initial velocity
    VfxCurve3? Acceleration,        // worldAcceleration (gravity/wind)
    VfxCurve3? BirthRotationalVelocity,
    Vector3 EmitterPosition,        // offset of this emitter within the system
    string? TexturePath,            // particle sprite (.dds/.tex)
    Vector2 TexDiv,                 // flipbook grid (cols, rows); (1,1) = single frame
    int NumFrames,
    bool RandomStartFrame,
    bool IsMeshPrimitive)           // primitive is a mesh (we billboard it as a fallback)
{
    /// <summary>Does this emitter produce anything drawable (has a texture and isn't disabled)?</summary>
    public bool IsVisual => !Disabled && !string.IsNullOrEmpty(TexturePath);
}

/// <summary>A scalar value that is either constant or an animation curve over normalised age (0..1).</summary>
public readonly record struct VfxCurveF(float Constant, float[]? Times, float[]? Values)
{
    public float Sample(float t)
    {
        if (Times is null || Values is null || Times.Length == 0) return Constant;
        return VfxCurve.Interp(Times, Values, t, static (a, b, f) => a + (b - a) * f);
    }
    public static readonly VfxCurveF Zero = new(0f, null, null);
    public static VfxCurveF Const(float v) => new(v, null, null);
}

/// <summary>A Vector3 value that is either constant or an animation curve over normalised age.</summary>
public readonly record struct VfxCurve3(Vector3 Constant, float[]? Times, Vector3[]? Values)
{
    public Vector3 Sample(float t)
    {
        if (Times is null || Values is null || Times.Length == 0) return Constant;
        return VfxCurve.Interp(Times, Values, t, static (a, b, f) => Vector3.Lerp(a, b, f));
    }
    public static VfxCurve3 Const(Vector3 v) => new(v, null, null);
}

/// <summary>A Vector4/colour value that is either constant or an animation curve over normalised age.</summary>
public readonly record struct VfxCurve4(Vector4 Constant, float[]? Times, Vector4[]? Values)
{
    public Vector4 Sample(float t)
    {
        if (Times is null || Values is null || Times.Length == 0) return Constant;
        return VfxCurve.Interp(Times, Values, t, static (a, b, f) => Vector4.Lerp(a, b, f));
    }
    public static VfxCurve4 Const(Vector4 v) => new(v, null, null);
}

internal static class VfxCurve
{
    /// <summary>Piecewise-linear sample of (times,values) at t, clamped at both ends.</summary>
    public static T Interp<T>(float[] times, T[] values, float t, Func<T, T, float, T> lerp)
    {
        int n = Math.Min(times.Length, values.Length);
        if (n == 1 || t <= times[0]) return values[0];
        if (t >= times[n - 1]) return values[n - 1];
        for (int i = 1; i < n; i++)
        {
            if (t <= times[i])
            {
                float span = times[i] - times[i - 1];
                float f = span > 1e-6f ? (t - times[i - 1]) / span : 0f;
                return lerp(values[i - 1], values[i], f);
            }
        }
        return values[n - 1];
    }
}
