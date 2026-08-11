namespace ReyEngine.Formats.Particles;

/// <summary>
/// The per-particle randomisation attached to a legacy field (M426).
///
/// <para><b>This is what makes a converted effect three-dimensional.</b> A legacy value such as
/// <c>*p-vel = &lt;0,50,0&gt;</c> is not the velocity every particle gets - it is a base that each
/// particle multiplies by a value drawn from a probability table. Written without the table, every
/// particle receives the identical velocity from the identical spawn point and the effect collapses to
/// a single line. That is exactly what "the old system shows flat particles, modern is fine" looked
/// like: the modern systems carry their tables and the converted ones did not.</para>
///
/// <para><b>Encoding.</b> Each table key is a <c>(probability, multiplier)</c> pair stored under a
/// numbered suffix - <c>*p-velYP1</c>, <c>*p-velYP2</c>, ... An unlettered suffix (<c>*p-scaleP1</c>)
/// applies to every component; a lettered one (<c>*p-scaleXP1</c>) applies to that axis alone. Measured
/// on <c>firetorch_purple</c>: <c>*p-velYP1 = (0.0, 1)</c> and <c>*p-velYP2 = (1.0, 3)</c>, so its
/// upward speed is uniformly random between one and three times the base.</para>
///
/// <para>Corpus frequency: <c>*p-scaleXP1</c> 1,138 emitters, <c>*p-offsetXP1</c> 828,
/// <c>*p-velYP1</c> 593. Keys live in section 8 (2xu8 tenths), 9 (2xf32) or the string block.</para>
/// </summary>
public sealed record TroyProbability(
    IReadOnlyList<(float Probability, float Multiplier)> Uniform,
    IReadOnlyList<(float Probability, float Multiplier)> X,
    IReadOnlyList<(float Probability, float Multiplier)> Y,
    IReadOnlyList<(float Probability, float Multiplier)> Z)
{
    public bool IsEmpty => Uniform.Count == 0 && X.Count == 0 && Y.Count == 0 && Z.Count == 0;

    /// <summary>The table for one axis, falling back to the uniform one. Returns empty when the field
    /// carries no randomisation at all, in which case no dynamics should be written.</summary>
    public IReadOnlyList<(float Probability, float Multiplier)> ForAxis(int axis)
    {
        var specific = axis switch { 0 => X, 1 => Y, 2 => Z, _ => Array.Empty<(float, float)>() };
        return specific.Count > 0 ? specific : Uniform;
    }
}
