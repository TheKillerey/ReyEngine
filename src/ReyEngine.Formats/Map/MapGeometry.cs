using System.Numerics;

namespace ReyEngine.Formats.Map;

/// <summary>
/// Placeholder interface for .mapgeo so the renderer and UI can reference it now.
/// Full decoding lands in M4 — keeping this stable avoids a refactor then.
/// </summary>
public interface IMapGeometryAsset
{
    int MeshCount { get; }
    Vector3 BoundsMin { get; }
    Vector3 BoundsMax { get; }
}

public static class MapGeoDecoder
{
    public static bool IsImplemented => false;

    public static IMapGeometryAsset Decode(byte[] data) =>
        throw new NotSupportedException("MAPGEO decoding lands in M4.");
}
