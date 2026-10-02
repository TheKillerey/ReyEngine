using System.Numerics;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Rendering;

namespace ReyEngine.App.Services;

/// <summary>
/// <para>M808: where on a map the editor's camera goes when the map is opened - the area the Content Browser's picture of it
/// shows, as a box in world space. It is the same box <see cref="MapThumbnailCamera.TryBox"/> finds for the picture (the
/// triangles the map's start state draws, outermost 2% trimmed, as tall as a tenth of its longer side), so the viewport does not
/// frame the mapgeo's own bounds - those include the sky bowl, the clouds and out-of-bounds decor, and put the playable map
/// at the far end of a very long lens.</para>
///
/// <para>Immutable and a class, not a record: every open makes a new one, so a map reopened with an unchanged box is still told
/// apart from the open before it.</para>
/// </summary>
public sealed class MapOpenFrame
{
    public MapOpenFrame(Vector3 boxMin, Vector3 boxMax)
    {
        BoxMin = boxMin;
        BoxMax = boxMax;
    }

    /// <summary>The box, in WORLD space (the mapgeo's own coordinates): X is not yet mirrored into the viewport's display space.</summary>
    public Vector3 BoxMin { get; }
    public Vector3 BoxMax { get; }
}

/// <summary>
/// <para>M808: puts the editor viewport's <see cref="OrbitCamera"/> where a map's picture in the Content Browser looks from -
/// the SAME framing rule (<see cref="MapThumbnailCamera.TryFrameBox"/>), not a copy of it: the same view direction (from above
/// and behind, a 56 degree pitch) and the same fit (the box fills 92% of the frame, centred), solved for the viewport's own
/// field of view and the aspect of the viewport's own size instead of the picture's 40 degrees and 384 by 268.</para>
///
/// <para>The camera lives in the viewport's MIRRORED display space (world X negated), which the framing rule already works in:
/// the target it returns is a display-space point, the orbit angles reproduce its direction, and the eye lands exactly where
/// the thumbnail's eye would for the same box, field of view and aspect.</para>
/// </summary>
public static class MapViewCamera
{
    /// <summary>The box of the geometry in <paramref name="map"/> that <paramref name="groupVisible"/> says the start state
    /// draws (a group beyond the list is visible, as in the picture), by <see cref="MapThumbnailCamera"/>'s own sampling and trim.
    /// Null when no triangle is left to frame.</summary>
    public static MapOpenFrame? FrameOf(MapGeoAsset map, IReadOnlyList<bool>? groupVisible)
    {
        ArgumentNullException.ThrowIfNull(map);
        var ranges = new List<(int Start, int Count)>(map.Groups.Count);
        for (int i = 0; i < map.Groups.Count; i++)
            if (groupVisible is null || i >= groupVisible.Count || groupVisible[i])
                ranges.Add((map.Groups[i].StartIndex, map.Groups[i].IndexCount));

        var centroids = MapThumbnailCamera.SampleCentroids(map.Positions, map.Indices, ranges);
        return MapThumbnailCamera.TryBox(centroids, out var min, out var max) ? new MapOpenFrame(min, max) : null;
    }

    /// <summary>Place <paramref name="camera"/> on <paramref name="frame"/> for a viewport of <paramref name="aspect"/> (width over
    /// height) and the camera's own <see cref="OrbitCamera.FieldOfView"/>: target, distance, yaw and pitch. Nothing else of the
    /// camera is touched - the fly speed, the field of view and the clip planes stay as they are. False (and the camera left
    /// alone) when the box cannot be fitted.</summary>
    public static bool TryApply(OrbitCamera camera, MapOpenFrame frame, float aspect)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(frame);
        if (!MapThumbnailCamera.TryFrameBox(frame.BoxMin, frame.BoxMax, aspect, camera.FieldOfView, out var fit)) return false;

        camera.Target = fit.Target;
        camera.Distance = Math.Clamp(fit.Distance, 1f, 100000f);   // the range the wheel zooms in
        camera.Yaw = MapThumbnailCamera.OrbitYaw;
        camera.Pitch = MapThumbnailCamera.OrbitPitch;
        return true;
    }
}
