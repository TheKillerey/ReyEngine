using System.Numerics;

namespace ReyEngine.Formats.Particles;

/// <summary>
/// The five legacy force-field kinds (M520).
///
/// <para>The kind comes from the REFERENCE, not from the section: an emitter says
/// <c>field-drag-1="SparksDrag"</c> and the section <c>[SparksDrag]</c> itself carries no type marker.
/// That matters because the same field name means different things per kind - <c>f-accel</c> is a vec3
/// of acceleration on an acceleration field, and a scalar pull strength on an attraction field.</para>
///
/// <para>The modern engine kept all five, one for one, which is the strongest evidence that
/// <c>VfxFieldCollectionDefinitionData</c> is a direct descendant of this subsystem rather than a
/// redesign: <c>fieldAccelerationDefinitions</c>, <c>fieldAttractionDefinitions</c>,
/// <c>fieldDragDefinitions</c>, <c>fieldOrbitalDefinitions</c>, <c>fieldNoiseDefinitions</c>.</para>
/// </summary>
public enum TroyFieldKind
{
    Acceleration,
    Attraction,
    Drag,
    Orbital,
    Noise,
}

/// <summary>
/// One force field, read from its own section by key.
///
/// <para>Nulls mean absent, as everywhere else in this format. Which members are populated depends on
/// <see cref="Kind"/>; the rest stay null rather than being defaulted, so a converter can tell "the
/// author set drag to 0" apart from "this field has no drag".</para>
/// </summary>
/// <param name="Name">The section name, exactly as the referencing emitter spelled it.</param>
/// <param name="Acceleration"><c>f-accel</c> as a vec3 - acceleration fields.</param>
/// <param name="Strength"><c>f-accel</c> as a scalar - attraction fields pull toward
/// <paramref name="Position"/> at this rate.</param>
/// <param name="Direction"><c>f-direction</c> - the orbital axis.</param>
/// <param name="Position"><c>f-pos</c> - field centre, emitter-local unless
/// <paramref name="LocalSpace"/> says otherwise.</param>
/// <param name="Radius"><c>f-radius</c> - the field's reach.</param>
/// <param name="Drag"><c>f-drag</c> - drag coefficient. Riot's converter writes this as the drag
/// definition's <c>strength</c> (measured: DestroyedBuilding_idle SparksDrag f-drag=6 -> strength 6).</param>
/// <param name="Period"><c>f-period</c> - noise resample interval in seconds.</param>
/// <param name="VelocityDelta"><c>f-veldelta</c> - noise velocity kick.</param>
/// <param name="LocalSpace"><c>f-localspace</c>.</param>
public sealed record TroyForceField(
    string Name,
    TroyFieldKind Kind,
    Vector3? Acceleration = null,
    float? Strength = null,
    Vector3? Direction = null,
    Vector3? Position = null,
    float? Radius = null,
    float? Drag = null,
    float? Period = null,
    float? VelocityDelta = null,
    bool LocalSpace = false);

/// <summary>One entry of the <c>[System]</c> group list: an emitter, its quality tier and its
/// importance. The tier is what the game uses to drop emitters on low settings, so a converter that
/// ignores it silently promotes every "Low" emitter to always-on.</summary>
public sealed record TroyGroupPart(int Index, string Name, string? Type, string? Importance);

/// <summary>
/// The <c>[System]</c> section (M520).
///
/// <para>This is the section whose name was previously known only as the magic seed 0xAE671AB7. It is
/// literally <c>System</c>, and the emitter list is <c>System*GroupPart{n}</c> - confirmed by
/// reproducing the seed exactly: <c>sdbm("*grouppart", sdbm("system")) == 0xAE671AB7</c>.</para>
/// </summary>
public sealed record TroySystemInfo(IReadOnlyList<TroyGroupPart> Parts, bool SimulateEveryFrame)
{
    public static readonly TroySystemInfo Empty = new(Array.Empty<TroyGroupPart>(), false);
}
