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

    /// <summary>
    /// The table for one axis, in the slot Riot's own converter puts it.
    ///
    /// <para><b>An unlettered table goes to X ALONE, not to all three</b> (M523). Replicating it across
    /// the axes looks harmless and is not: each axis then rolls INDEPENDENTLY, so a particle authored to
    /// scale uniformly comes out 1.0 x 1.5 x 1.2. Measured against Riot's conversion over the paired
    /// systems, 17 of 156 birthScale0 fields disagreed in exactly this way - Riot writes [K,-,-] where
    /// the fallback wrote [K,K,K] - and every other axis shape already agreed.</para>
    ///
    /// <para>Returns empty when that axis carries no randomisation, which is the signal to write an
    /// empty table rather than none: the container is read by INDEX, so a missing element would shift
    /// Y into X.</para>
    /// </summary>
    public IReadOnlyList<(float Probability, float Multiplier)> ForAxis(int axis) => axis switch
    {
        0 => X.Count > 0 ? X : Uniform,
        1 => Y,
        2 => Z,
        _ => Array.Empty<(float, float)>(),
    };
}

/// <summary>
/// One emitter-space rotation: spin the spawn offset and birth velocity by a random angle about an
/// axis (M427).
///
/// <para><b>This is the missing three-dimensionality.</b> A legacy <c>*p-offset</c> of
/// <c>(30,0,0)</c> is a RADIUS along X, not a box extent. On its own it puts every particle on a line;
/// rotated 0-360 degrees about Y it becomes a full cylinder. Measured on <c>firetorch_purple</c>, every
/// emitter carries <c>*e-rotation2-axis = (0,1,0)</c> with a table reaching <c>360</c>, plus a smaller
/// tilt about X. Without these the whole effect stays in one plane, which is precisely how it
/// rendered.</para>
///
/// <para>Corpus: <c>*e-rotation1</c> on 931 emitters, <c>*e-rotation2</c> on 458, with unit axes
/// <c>(0,1,0)</c>, <c>(1,0,0)</c> and <c>(0,0,1)</c>.</para>
/// </summary>
public sealed record TroyEmitRotation(
    System.Numerics.Vector3 Axis,
    float Angle,
    IReadOnlyList<(float Probability, float Multiplier)> Table);
