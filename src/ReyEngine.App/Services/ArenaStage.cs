using System;
using System.Numerics;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.App.Services;

/// <summary>
/// M868: where the Character window stands the champion and the target dummy on a loaded map.
///
/// <para>Deterministic on purpose. The arena used to put the champion at <see cref="ArenaLoader.SpawnFor"/>
/// (inside the blue fountain) facing +Z with the dummy 400 units to the side, so every run of every
/// champion started somewhere slightly different relative to the camera and nothing about it was
/// chosen. The automated simulation that replaces manual control needs one fixed, open, walkable stage.</para>
///
/// <para><b>Summoner's Rift (Map11), measured off <c>assets/maps/navgrid/map11/aipath.aimesh_ngrid</c>
/// (295 x 296 cells of 50 units, origin (-1.10, 32.76)):</b> the champion stands on cell (147, 142),
/// world (7374, 7158), just south-west of the mid-lane / river junction. The search was: every cell whose
/// 17 x 17 neighbourhood (radius 8 cells = 400 units) carries none of the impassable bits
/// (<see cref="NavGridPath.ImpassableMask"/>), none of bush (0x0001), the team-restricted bits
/// (0x0004, 0x0008, 0x0400, 0x0800, 0x1000) or the wall-outline bit (0x0040), with a ground-height spread
/// of at most 6 units, AND whose dummy cell 500 units along the mid-lane diagonal has the same clear
/// disc; of those 504 candidates, the one nearest the map centre whose nearest not-walkable cell is within
/// 700 units. Result: ground height 54.3 (neighbourhood 49.0 .. 54.9), nearest terrain wall 559 units away
/// to the south (direction (0.18, -0.98)), so a wall is reachable for a later wall-jump scenario while
/// the 400-unit disc around both unit positions is open, flat, un-bushed ground, thousands of units from either
/// fountain (the base is in the corners of the grid).</para>
///
/// <para>Facing is yaw pi/4: <c>forward = (sin yaw, 0, cos yaw)</c> = (0.707, 0, 0.707), up the mid lane
/// toward the red side. The dummy is 500 units straight ahead (the brief's 400..600), at
/// (7727.5, 7511.5), checked walkable and clear by the same test. A test re-runs this evidence against the
/// real navgrid and skips when the install is missing.</para>
///
/// <para>Any other map keeps the old rule: the fountain spawn, the dummy 400 units along +X, facing it.</para>
/// </summary>
public sealed record ArenaStage(Vector3 Champion, float Yaw, Vector3 Dummy)
{
    /// <summary>Summoner's Rift stage cell centre (world X, Z). See the type remarks for the evidence.</summary>
    public const float Map11X = 7374f, Map11Z = 7158f;

    /// <summary>Facing up the mid lane: forward = (sin, 0, cos) of this = the (1, 0, 1) diagonal.</summary>
    public const float Map11Yaw = MathF.PI / 4f;

    /// <summary>How far in front of the champion the dummy stands.</summary>
    public const float DummyDistance = 500f;

    /// <summary>Used when there is no map at all: the origin, facing +Z, the dummy where the preview
    /// has always put it.</summary>
    public static ArenaStage Origin { get; } = new(Vector3.Zero, 0f, new Vector3(350f, 0f, 0f));

    public static ArenaStage For(string mapKey, NavGrid? nav, Vector3 boundsMin, Vector3 boundsMax)
    {
        if (string.Equals(mapKey, "Map11", StringComparison.OrdinalIgnoreCase) && nav is { HasFlags: true })
            return Place(nav, new Vector3(Map11X, 0f, Map11Z), Map11Yaw, DummyDistance);

        var spawn = ArenaLoader.SpawnFor(nav, boundsMin, boundsMax);
        return Place(nav, spawn, MathF.PI / 2f, 400f);   // toward +X, where the old rule put the dummy
    }

    private static ArenaStage Place(NavGrid? nav, Vector3 champion, float yaw, float distance)
    {
        var forward = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
        var dummy = champion + forward * distance;
        if (nav is not null)
        {
            champion = NavGridPath.SnapToWalkable(nav, champion, maxRadiusCells: 20);
            dummy = NavGridPath.SnapToWalkable(nav, dummy, maxRadiusCells: 20);
            champion.Y = NavGridPath.GroundHeight(nav, champion);
            dummy.Y = NavGridPath.GroundHeight(nav, dummy);
        }
        return new ArenaStage(champion, yaw, dummy);
    }
}
