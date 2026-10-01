using LeagueToolkit.Core.Meta;

namespace ReyEngine.Formats.Materials;

/// <summary>
/// M805: a bin the game has loaded BESIDE a skin, so a link in the skin bin can name one of its objects without the
/// skin bin listing it in <c>tree.Dependencies</c>. The case that needs it: a character placed on a map links
/// StaticMaterialDefs that live in the map's own shipping bin (<c>data/maps/shipping/mapNN/mapNN.bin</c>), which the
/// client has loaded with the map - Summoner's Rift's esports banner skins (Srx_Banner_*) name
/// <c>Maps/Shipping/Map11/Esports/Materials/...</c>, defined only in map11.bin.
/// <para>Parsed once by the caller and handed to <see cref="MaterialDocument.Parse"/>; read, never written - a
/// material found here is linked (read-only), exactly like one found through a dependency.</para>
/// </summary>
/// <param name="Path">The bin's WAD path, reported as where a material was linked from.</param>
/// <param name="Tree">The parsed bin.</param>
public sealed record LoadedBin(string Path, BinTree Tree);
