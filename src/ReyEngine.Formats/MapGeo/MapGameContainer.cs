using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.MapGeo;

/// <summary>M819: the map the game loads for a shipping map's <c>Default</c> skin: which container, and so which geometry and materials.</summary>
/// <param name="SkinName">The skin that decides it (<c>Default</c>).</param>
/// <param name="ContainerLink">The skin's <c>mMapContainerLink</c>, as the bin spells it (<c>Maps/MapGeometry/Map11/Milkshake_SRS</c>).</param>
/// <param name="GeometryPath">The container's mapgeo: <c>data/Maps/MapGeometry/Map11/Milkshake_SRS.mapgeo</c>.</param>
/// <param name="MaterialsBinPath">The container's bin, which holds its <c>MapContainer</c> and its materials (<see cref="MapSkinSwitcher.ContainerBinPath"/>).</param>
public sealed record MapGameContainer(string SkinName, string ContainerLink, string GeometryPath, string MaterialsBinPath)
{
    /// <summary>The skin the game's server selects first, and the one a map skin mod rewires to change what loads.</summary>
    public const string DefaultSkin = "Default";

    /// <summary>
    /// The container the game loads for the <see cref="DefaultSkin"/> of a shipping map bin, or null when the bin has no such skin, or the skin has no container (it loads the map's legacy geometry).
    ///
    /// <para>The client resolves a map in steps: the bin's active <c>MapSkin</c> names a container (<c>mMapContainerLink</c>), the container is the <c>MapContainer</c> object of the bin of that name
    /// (<c>data/&lt;link&gt;.materials.bin</c>, the rule the Map Skin Switcher keeps), and its geometry is the mapgeo beside it. So a mod that rewrites one skin's link changes the whole map without touching a
    /// mapgeo - and the editor, which shows the mapgeo it is asked to open, must be told which that is. The reading is <see cref="MapStateData"/>'s (it never throws on a modded bin); the paths are the
    /// switcher's.</para>
    /// </summary>
    /// <param name="shippingBin">The bytes of <c>data/maps/shipping/mapNN/mapNN.bin</c> - as the game would read it, overlaid when a mod's GameData changes it.</param>
    public static MapGameContainer? Resolve(byte[] shippingBin, Func<uint, string?>? resolveName = null)
    {
        var skin = MapStateData.Parse(shippingBin, resolveName).Skins.FirstOrDefault(s =>
            string.Equals(s.SkinName, DefaultSkin, StringComparison.OrdinalIgnoreCase) && s.MapContainerLink is { Length: > 0 });
        if (skin?.MapContainerLink is not { } link) return null;
        string? bin = MapSkinSwitcher.ContainerBinPath(link);
        if (bin is null) return null;
        string clean = link.Replace('\\', '/').Trim('/');
        if (clean.StartsWith("data/", StringComparison.OrdinalIgnoreCase)) clean = clean[5..];
        return new MapGameContainer(skin.SkinName!, link, $"data/{clean}.mapgeo", bin);
    }

    /// <summary>The stem of the container's mapgeo (<c>milkshake_srs</c>), the name a mapgeo is told by.</summary>
    public string Stem => System.IO.Path.GetFileNameWithoutExtension(GeometryPath);

    /// <summary>Whether <paramref name="mapgeoPath"/> is the container's mapgeo: the same file, whatever the casing of its path.</summary>
    public bool IsGeometry(string mapgeoPath) =>
        HashAlgorithms.WadPath(mapgeoPath) == HashAlgorithms.WadPath(GeometryPath);
}
