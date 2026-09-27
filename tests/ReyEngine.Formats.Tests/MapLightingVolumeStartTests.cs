using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M785: a bin whose lighting volumes all sit at ONE transform - every Map22 arena skin that has two - lights
/// its scene with the volume that is on at the start, chosen by the map's InitialVisibilityMask. Before this
/// such bins fell back to the global MapSunProperties (M207), and anniversary.mapgeo rendered at
/// lightMapColorScale 1 with the dim slanted global sun where its flag-64 volume authors 2 and an overhead
/// sun: the cake top came out at about half the game's brightness.
/// </summary>
public sealed class MapLightingVolumeStartTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static readonly Matrix4x4 Board = new(
        2300f, 0f, 0f, 0f,
        0f, 3500f, 0f, 0f,
        0f, 0f, 2500f, 0f,
        2000f, 0f, 2000f, 1f);

    private static BinTreeStruct Volume(string name, byte flags, float lightMapColorScale, Matrix4x4 transform) =>
        new(0, H("MapLightingVolume"), new BinTreeProperty[]
        {
            new BinTreeMatrix44(H("transform"), transform),
            new BinTreeString(H("name"), name),
            new BinTreeU8(H("mVisibilityFlags"), flags),
            new BinTreeF32(H("lightMapColorScale"), lightMapColorScale),
        });

    /// <summary>A materials bin shaped like anniversary's: a global sun that authors no lightMapColorScale,
    /// plus the given volumes, each in its own MapPlaceableContainer as Riot ships them.</summary>
    private static byte[] Bin(params BinTreeStruct[] volumes)
    {
        var objects = new List<BinTreeObject>();
        var sun = new BinTreeStruct(0, H("MapSunProperties"), new BinTreeProperty[]
        {
            new BinTreeVector4(H("sunColor"), new Vector4(0.549f, 0.506f, 0.431f, 1f)),
        });
        objects.Add(new BinTreeObject(1u, H("MapContainer"), new BinTreeProperty[]
        {
            new BinTreeContainer(H("components"), BinPropertyType.Struct, new BinTreeProperty[] { sun }),
        }));
        uint key = 0x100u;
        foreach (var v in volumes)
        {
            var items = new BinTreeMap(H("items"), BinPropertyType.Hash, BinPropertyType.Struct, new[]
            {
                new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, key), v),
            });
            objects.Add(new BinTreeObject(0x5000u + key, H("MapPlaceableContainer"), new BinTreeProperty[] { items }));
            key++;
        }
        using var ms = new MemoryStream();
        new BinTree(objects, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    [Fact]
    public void CoLocatedVolumesLightTheSceneWithTheOneOnAtTheStart()
    {
        var bin = Bin(Volume("LightingVolume1", 64, 2f, Board), Volume("LightingVolume2", 8, 1.5f, Board));

        // Map22's InitialVisibilityMask is 67: bit 6 ("base") is on, bit 3 (Stage2) is not.
        var sun = MapLighting.EffectiveSun(bin, () => 67);

        Assert.NotNull(sun);
        Assert.Equal(2f, sun!.LightMapColorScale);
    }

    [Fact]
    public void WithoutTheMaskTheM207RuleStands()
    {
        var bin = Bin(Volume("LightingVolume1", 64, 2f, Board), Volume("LightingVolume2", 8, 1.5f, Board));

        // Every caller that passes no mask (the recolour scan, the fallback bin before M785) keeps the global sun.
        Assert.Equal(1f, MapLighting.EffectiveSun(bin)!.LightMapColorScale);
    }

    [Fact]
    public void TwoVolumesOnAtOnceStayOnTheGlobalSun()
    {
        // lunarrevel2025's shape: 76 and 58 both share a bit with 67, and which one wins is not known.
        var bin = Bin(Volume("LightingVolume1", 76, 2f, Board), Volume("LightingVolume2", 58, 2f, Board));

        Assert.Equal(1f, MapLighting.EffectiveSun(bin, () => 67)!.LightMapColorScale);
    }

    [Fact]
    public void VolumesAtDifferentPlacesNeverAskForTheMask()
    {
        var moved = Board with { M41 = 9000f };
        var bin = Bin(Volume("LightingVolume1", 64, 2f, Board), Volume("LightingVolume2", 8, 1.5f, moved));
        int asked = 0;

        var sun = MapLighting.EffectiveSun(bin, () => { asked++; return 67; });

        // M207's extent question can pick the wrong VOLUME here, so these are still left on the global sun -
        // and the half-second shipping-bin parse behind the mask is never paid for.
        Assert.Equal(1f, sun!.LightMapColorScale);
        Assert.Equal(0, asked);
    }

    [Fact]
    public void ALoneVolumeStillWinsWithoutAskingForTheMask()
    {
        var bin = Bin(Volume("LightingVolume1", 8, 2f, Board));
        int asked = 0;

        var sun = MapLighting.EffectiveSun(bin, () => { asked++; return 67; });

        Assert.Equal(2f, sun!.LightMapColorScale);   // M207, unchanged: a lone volume lights the scene
        Assert.Equal(0, asked);
    }

    [Fact]
    public void NoMaskOrAnEmptyMaskDecidesNothing()
    {
        var volumes = MapLighting.Extract(
            Bin(Volume("LightingVolume1", 64, 2f, Board), Volume("LightingVolume2", 8, 1.5f, Board)), null).Volumes;

        Assert.Null(MapLighting.ActiveAtStart(volumes, () => null));
        Assert.Null(MapLighting.ActiveAtStart(volumes, () => 0));
        Assert.Equal("LightingVolume2", MapLighting.ActiveAtStart(volumes, () => 8)!.Name);
    }

    /// <summary>The shipped data the report was about, read-only. anniversary lights its level-1 board with
    /// LightingVolume1 (lightMapColorScale 2, overhead sun 0.765/0.731/0.645); 7yanniversary - which the
    /// user did not report - moves the same way to its flag-72 volume (1.25). Summoner's Rift has no volumes
    /// and never parses its shipping bin for this.</summary>
    [Fact]
    public void ShippedBoardsResolveToTheirStartVolume()
    {
        const string Shipping = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping";
        string map22 = Path.Combine(Shipping, "Map22.wad.client");
        string map11 = Path.Combine(Shipping, "Map11.wad.client");
        if (!File.Exists(map22) || !File.Exists(map11)) return;

        var resolver = new WadPathResolver(new HashSyncService().LoadLocal(_ => { }));
        using var tft = WadArchive.Open(map22, resolver);
        using var sr = WadArchive.Open(map11, resolver);
        byte[]? Read(WadArchive wad, string path)
        {
            ulong h = HashAlgorithms.WadPath(path);
            return wad.TryGetEntry(h, out _) ? wad.Extract(h) : null;
        }
        int asks = 0;
        int? Mask22() { asks++; return MapVisibility.Parse(Read(tft, "data/maps/shipping/map22/map22.bin")).Primary?.InitialMask; }

        var anniversary = Read(tft, "data/maps/mapgeometry/map22/anniversary.materials.bin");
        var seventh = Read(tft, "data/maps/mapgeometry/map22/7yanniversary.materials.bin");
        var baseSrx = Read(sr, "data/maps/mapgeometry/map11/base_srx.materials.bin");
        if (anniversary is null || seventh is null || baseSrx is null) return;

        var a = MapLighting.EffectiveSun(anniversary, Mask22)!;
        Assert.Equal(2f, a.LightMapColorScale);
        Assert.Equal(new Vector3(0f, 1f, 0f), a.SunDirection);
        Assert.Equal(0.7647059f, a.SunColor.X, 5);
        Assert.Equal(1f, MapLighting.EffectiveSun(anniversary)!.LightMapColorScale);   // the old reading

        Assert.Equal(1.25f, MapLighting.EffectiveSun(seventh, Mask22)!.LightMapColorScale);
        Assert.Equal(2, asks);

        var srSun = MapLighting.EffectiveSun(baseSrx, () => { asks++; return 1; });
        Assert.Equal(MapLighting.EffectiveSun(baseSrx), srSun);
        Assert.Equal(2, asks);
    }
}
