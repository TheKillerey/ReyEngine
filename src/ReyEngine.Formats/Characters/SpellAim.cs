using System.Numerics;

namespace ReyEngine.Formats.Characters;

/// <summary>What a cast resolved to, from the spell's authored targeting kind and the cursor.</summary>
/// <param name="Kind">The authored targeting kind the plan was made from ("" when the record has none).</param>
/// <param name="Aim">The point the caster turns toward and the caster-side systems face.</param>
/// <param name="TravelTo">Where a missile flies to; null for a self-cast.</param>
/// <param name="HitAt">Where target-side systems play: the dummy when the cast reaches it, else the
/// aim point for a ground-targeted spell, else null (a skillshot that hit nothing).</param>
/// <param name="WalkTo">Non-null when the game would walk into range before casting - the point to walk
/// to, after which the plan is re-resolved from there.</param>
/// <param name="Note">One line for the status bar: what was decided and why.</param>
public sealed record CastPlan(
    string Kind,
    Vector3 Aim,
    Vector3? TravelTo,
    Vector3? HitAt,
    Vector3? WalkTo,
    string Note)
{
    public bool IsSelfCast => TravelTo is null && HitAt is null && WalkTo is null;
}

/// <summary>
/// M637: aiming a cast the way its <c>mTargetingTypeData</c> says, with the cursor as the only input the
/// player has - which is how the game aims every spell.
///
/// <para>The kinds are the 14 the roster authors (ChampionSpellDataTests censuses them). Three
/// families:</para>
/// <list type="bullet">
///   <item><b>Self / SelfAoe</b> - no aim at all; the cast happens at the caster.</item>
///   <item><b>Location, LocationClamped, Area, AreaClamped, TerrainLocation, TerrainType,
///     WallDetection, TargetOrLocation (without a target)</b> - the cursor's ground point. The Clamped
///     and Area kinds clamp it to the cast range; plain Location beyond range WALKS into range first,
///     which is what the game does with an out-of-range ground cast.</item>
///   <item><b>direction, DragDirection, Cone</b> - the direction from caster to cursor; the missile flies
///     the cast range along it (a range Riot authors as 25000 means "aim anywhere", and the flight then
///     takes <see cref="UnboundedFlight"/>). The dummy is hit when it lies within the missile's width of
///     that line, out to the flight distance.</item>
///   <item><b>Target</b> - needs a unit: the dummy when it is in range, else a walk toward it.</item>
/// </list>
///
/// <para>Range checks use <see cref="AbilitySlot.CastRange"/>, the number the game checks against, not the
/// display override (Ezreal Q: 1200 real, 1150 shown). Nothing here decides damage or whether a spell
/// "connects" in any gameplay sense - the hit test only picks where the hit VFX plays.</para>
/// </summary>
public static class SpellAim
{
    /// <summary>How far a direction cast with an unbounded authored range flies in the arena. Aatrox's Q
    /// authors 25000 because the cursor may be anywhere; his sweep is a fixed-size animation at his feet.
    /// One constant, so it is one line if a champion contradicts it on screen.</summary>
    public const float UnboundedFlight = 1000f;

    /// <summary>Half-width of a skillshot's hit lane when the spell authors no radius.</summary>
    public const float DefaultMissileHalfWidth = 65f;

    public static CastPlan Plan(AbilitySlot? ability, Vector3 caster, Vector3 cursor, Vector3? dummy, float dummyRadius = 65f)
    {
        string kind = ability?.TargetingKind ?? "";
        float range = ability?.CastRange ?? 0f;
        bool unbounded = ability?.IsUnboundedRange ?? true;
        var toCursor = Flat(cursor - caster);
        float cursorDistance = toCursor.Length();

        switch (Normalise(kind))
        {
            case "self":
            case "selfaoe":
                return new CastPlan(kind, caster, null, dummy is { } d0 && Within(caster, d0, ability?.CastRadius ?? 0f, dummyRadius) ? d0 : null,
                    null, kind.Length == 0 ? "self cast" : $"{kind}: at the caster");

            case "direction":
            case "dragdirection":
            case "cone":
            {
                var dir = cursorDistance > 1e-3f ? toCursor / cursorDistance : new Vector3(0f, 0f, 1f);
                float flight = unbounded || range <= 0f ? UnboundedFlight : range;
                var end = caster + dir * flight;
                end.Y = cursor.Y;
                float halfWidth = ability is { CastRadius: > 0f } ? ability.CastRadius : DefaultMissileHalfWidth;
                Vector3? hit = dummy is { } d1 && OnLane(caster, dir, flight, d1, halfWidth + dummyRadius) ? d1 : null;
                string why = hit is null ? "no unit on the lane" : "hits the dummy";
                return new CastPlan(kind, end, end, hit, null,
                    $"{kind}: {flight:0} units along the aim, {why}" + (unbounded ? " (authored range unbounded)" : ""));
            }

            case "target":
            {
                if (dummy is not { } d2)
                    return new CastPlan(kind, cursor, null, null, null, $"{kind}: needs a unit - no dummy to target");
                float dist = Flat(d2 - caster).Length();
                if (!unbounded && range > 0f && dist > range)
                    return new CastPlan(kind, d2, null, null, StepInto(caster, d2, range),
                        $"{kind}: dummy {dist:0} away, range {range:0} - walking into range");
                return new CastPlan(kind, d2, d2, d2, null, $"{kind}: the dummy, {dist:0} away");
            }

            case "targetorlocation":
            {
                if (dummy is { } d3 && Within(cursor, d3, 0f, dummyRadius * 2f))
                {
                    float dist = Flat(d3 - caster).Length();
                    if (!unbounded && range > 0f && dist > range)
                        return new CastPlan(kind, d3, null, null, StepInto(caster, d3, range),
                            $"{kind}: dummy under the cursor, {dist:0} away, range {range:0} - walking into range");
                    return new CastPlan(kind, d3, d3, d3, null, $"{kind}: the dummy under the cursor");
                }
                goto case "locationclamped";
            }

            case "location":
            case "terrainlocation":
            case "terraintype":
            case "walldetection":
            {
                if (!unbounded && range > 0f && cursorDistance > range)
                    return new CastPlan(kind, cursor, null, null, StepInto(caster, cursor, range),
                        $"{kind}: {cursorDistance:0} away, range {range:0} - walking into range");
                Vector3? hit = dummy is { } d4 && Within(cursor, d4, ability?.CastRadius ?? 0f, dummyRadius) ? d4 : cursor;
                return new CastPlan(kind, cursor, cursor, hit, null, $"{kind}: at the cursor, {cursorDistance:0} away");
            }

            case "locationclamped":
            case "area":
            case "areaclamped":
            default:
            {
                var aim = cursor;
                string clamp = "";
                if (!unbounded && range > 0f && cursorDistance > range)
                {
                    aim = caster + toCursor / cursorDistance * range;
                    aim.Y = cursor.Y;
                    clamp = $", clamped from {cursorDistance:0} to {range:0}";
                }
                Vector3? hit = dummy is { } d5 && Within(aim, d5, ability?.CastRadius ?? 0f, dummyRadius) ? d5 : aim;
                string label = kind.Length == 0 ? "no targeting data - ground cast" : kind;
                return new CastPlan(kind, aim, aim, hit, null, $"{label}: at the cursor{clamp}");
            }
        }
    }

    /// <summary>The point on the caster-to-target line that is just inside the range.</summary>
    private static Vector3 StepInto(Vector3 caster, Vector3 target, float range)
    {
        var flat = Flat(target - caster);
        float dist = flat.Length();
        if (dist < 1e-3f) return caster;
        var p = caster + flat / dist * MathF.Max(0f, dist - range * 0.9f);
        p.Y = caster.Y;
        return p;
    }

    private static bool Within(Vector3 at, Vector3 unit, float radius, float unitRadius) =>
        Flat(unit - at).Length() <= radius + unitRadius;

    /// <summary>Is the unit within <paramref name="halfWidth"/> of the segment from the caster along
    /// <paramref name="dir"/> for <paramref name="length"/> units?</summary>
    private static bool OnLane(Vector3 caster, Vector3 dir, float length, Vector3 unit, float halfWidth)
    {
        var rel = Flat(unit - caster);
        float along = Vector3.Dot(rel, dir);
        if (along < 0f || along > length) return false;
        var across = rel - dir * along;
        return across.Length() <= halfWidth;
    }

    private static Vector3 Flat(Vector3 v) => new(v.X, 0f, v.Z);

    private static string Normalise(string kind) => kind.Trim().ToLowerInvariant();
}
