using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Formats.Lighting;

namespace ReyEngine.Formats.Tests;

/// <summary>M443: the analytic lightgrid, and the format corrections the disassembly forced.
///
/// <para>The rule that shapes all of this: a MISSING grid is completely graceful (the loader returns
/// false and every consumer null-checks), but a MALFORMED one is a silent crash — on version != 3 or a
/// zero dimension the loader still returns TRUE with a null data pointer, and the first ReadCell
/// dereferences it untested. So the builder refuses rather than emitting anything questionable.</para></summary>
public class NeutralLightGridTests
{
    private static LightGridFile Small(float world = 15000f) =>
        NeutralLightGrid.Build(world, world, 4, 4, 0.25f);

    [Fact]
    public void The_header_is_what_the_loader_requires()
    {
        var g = Small();

        Assert.Equal(3, g.Version);                       // != 3 registers a NULL grid and crashes on use
        Assert.True(g.Width >= 1 && g.Height >= 1);
        Assert.True(g.WorldSizeX >= 1f && g.WorldSizeZ >= 1f);
        Assert.Equal(0.25f, g.FullBrightScale);           // LIGHTGRID_SCALE.x = 0.25*4 = 1.0
        Assert.Equal(0.25f, g.CharacterFullBrightIntensity);
    }

    /// <summary>NaN PASSES the client's `comiss/ja` check (unordered) and then poisons every cell index,
    /// so it has to be rejected here.</summary>
    [Theory]
    [InlineData(float.NaN)] [InlineData(0f)] [InlineData(0.5f)] [InlineData(float.NegativeInfinity)]
    public void An_unusable_world_size_is_refused(float bad)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NeutralLightGrid.Build(bad, bad, 4, 4, 0.25f));
    }

    [Theory]
    [InlineData(0, 4)] [InlineData(4, 0)] [InlineData(-1, 4)]
    public void A_zero_dimension_is_refused(int w, int h)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NeutralLightGrid.Build(15000f, 15000f, w, h, 0.25f));
    }

    /// <summary>Level and shape are corpus-derived: +Y : horizontal : −Y = 1 : 0.30 : 0.20, and the
    /// sphere-average multiplier lands in the 0.300–0.330 band the three SR-scale grids converge on.</summary>
    [Fact]
    public void The_cube_matches_the_measured_shape_and_level()
    {
        var bytes = NeutralLightGrid.DirectionBytes;

        Assert.Equal(new byte[] { 60, 60, 200, 40, 60, 60 }, bytes);
        Assert.Equal(0.30, bytes[0] / (double)bytes[2], 2);      // horizontal : +Y
        Assert.Equal(0.20, bytes[3] / (double)bytes[2], 2);      // −Y : +Y

        double mean6 = bytes.Select(b => (double)b).Average();
        double eff = mean6 / 255.0 * 0.25 * 4.0;                 // FullBrightScale * 4 == 1.0
        Assert.InRange(eff, 0.300, 0.330);
    }

    /// <summary>The shader's mad_sat clamps to 1.0 — a cube that saturates renders characters flat and
    /// unshaded, which is exactly what the probe bake produced with auto-exposure off.</summary>
    [Fact]
    public void Nothing_saturates()
    {
        Assert.All(NeutralLightGrid.DirectionBytes, b => Assert.True(b < 255));
        Assert.True(NeutralLightGrid.DirectionBytes.Max() / 255f < 0.8f);
    }

    [Fact]
    public void Every_cell_is_identical_and_neutral_grey()
    {
        var g = Small();

        Assert.Equal(4 * 4 * LightGridFile.Directions, g.Samples.Length);
        for (int cell = 0; cell < 16; cell++)
            for (int d = 0; d < LightGridFile.Directions; d++)
            {
                var s = g.Samples[cell * LightGridFile.Directions + d];
                Assert.Equal(s.X, s.Y);
                Assert.Equal(s.Y, s.Z);
                Assert.Equal(NeutralLightGrid.DirectionBytes[d] / 255f, s.X, 3);
            }
    }

    /// <summary>The exact bytes the spec pins, in the engine's sample order and on-disk channel order.</summary>
    [Fact]
    public void The_cell_bytes_are_the_specified_ones()
    {
        byte[] data = Small().Write();

        var cell = data.AsSpan(LightGridFile.HeaderSize, LightGridFile.CellSize).ToArray();
        Assert.Equal(new byte[]
        {
            0x3C, 0x3C, 0x3C, 0xFF,   // +X
            0x3C, 0x3C, 0x3C, 0xFF,   // -X
            0xC8, 0xC8, 0xC8, 0xFF,   // +Y
            0x28, 0x28, 0x28, 0xFF,   // -Y
            0x3C, 0x3C, 0x3C, 0xFF,   // +Z
            0x3C, 0x3C, 0x3C, 0xFF,   // -Z
        }, cell);
    }

    [Fact]
    public void The_file_is_exactly_header_plus_cells()
    {
        byte[] data = NeutralLightGrid.Build(15000f, 15000f, 256, 256, 0.25f).Write();
        Assert.Equal(LightGridFile.HeaderSize + 256 * 256 * LightGridFile.CellSize, data.Length);
        Assert.Equal(1_572_896, data.Length);
    }

    /// <summary>On disk a cell is 0xAARRGGBB little-endian, so red is byte 2. Read/Write used to swap R
    /// and B; a round-trip could never catch it because it swaps twice, and every grid we write is grey.</summary>
    [Fact]
    public void Red_is_the_third_byte_on_disk()
    {
        var g = NeutralLightGrid.Build(15000f, 15000f, 1, 1, 0.25f);
        g.Samples[0] = new Vector3(1f, 0f, 0f);           // pure red in +X

        byte[] data = g.Write();

        Assert.Equal(0x00, data[LightGridFile.HeaderSize + 0]);   // B
        Assert.Equal(0x00, data[LightGridFile.HeaderSize + 1]);   // G
        Assert.Equal(0xFF, data[LightGridFile.HeaderSize + 2]);   // R
        Assert.Equal(1f, LightGridFile.Read(data).Samples[0].X, 2);
    }

    /// <summary>The client reads exactly w*h*24 and ignores trailing bytes; `==` rejected the two shipped
    /// grids that carry extra (map21/base +14,832 B, map30/arenavote +813 B).</summary>
    [Fact]
    public void A_longer_file_is_accepted_like_the_client_accepts_it()
    {
        byte[] data = Small().Write();
        byte[] padded = data.Concat(new byte[813]).ToArray();

        Assert.True(LightGridFile.LooksLikeLightGrid(padded));
        Assert.Equal(4, LightGridFile.Read(padded).Width);
    }

    [Fact]
    public void A_truncated_file_is_still_rejected()
    {
        byte[] data = Small().Write();
        Assert.False(LightGridFile.LooksLikeLightGrid(data.AsSpan(0, data.Length - 24).ToArray()));
    }

    // ---- the link string ----

    /// <summary>Rule measured byte-for-byte on 179 of 181 shipped bins.</summary>
    [Fact]
    public void The_file_name_follows_riots_rule()
    {
        Assert.Equal("ASSETS/Maps/Lightmaps/Maps/MapGeometry/Map11/Base_SRX/LightGrid.dat",
            NeutralLightGrid.FileNameFor("Maps/MapGeometry/Map11/Base_SRX"));
    }

    /// <summary>The fallback strips the data/ prefix and the extension. It does NOT recover Riot's
    /// casing — "MapGeometry" and "SRX" are not derivable from a lowercase path — and it does not pretend
    /// to; the name resolves to the same chunk key regardless, because WadPath lowercases before hashing.</summary>
    [Fact]
    public void The_fallback_path_strips_the_prefix_and_extension()
    {
        Assert.Equal("maps/mapgeometry/map11/base_srx",
            NeutralLightGrid.MapPathFromMapGeo("data/maps/mapgeometry/map11/base_srx.mapgeo"));
    }

    /// <summary>Whatever casing the bin authored is preserved verbatim - that is the whole point of
    /// reading it rather than deriving it.</summary>
    [Fact]
    public void The_authored_map_path_is_read_verbatim_from_the_bin()
    {
        uint container = ReyEngine.Core.Hashing.HashAlgorithms.Fnv1a("MapContainer");
        var obj = new BinTreeObject(1u, container, new BinTreeProperty[]
        {
            new BinTreeString(ReyEngine.Core.Hashing.HashAlgorithms.Fnv1a("mapPath"),
                "Maps/MapGeometry/Map11/Base_SRX"),
        });
        var ms = new MemoryStream();
        new BinTree(new[] { obj }, Array.Empty<string>()).Write(ms);

        Assert.Equal("Maps/MapGeometry/Map11/Base_SRX", NeutralLightGrid.MapPathFromBin(ms.ToArray()));
        Assert.Equal("ASSETS/Maps/Lightmaps/Maps/MapGeometry/Map11/Base_SRX/LightGrid.dat",
            NeutralLightGrid.FileNameFor(NeutralLightGrid.MapPathFromBin(ms.ToArray())!));
    }

    [Fact]
    public void A_bin_without_a_map_path_reports_null_rather_than_guessing()
    {
        Assert.Null(NeutralLightGrid.MapPathFromBin(Array.Empty<byte>()));
    }

    [Fact]
    public void The_basename_never_carries_a_theme_token()
    {
        Assert.EndsWith("/LightGrid.dat", NeutralLightGrid.FileNameFor("Maps/MapGeometry/Map12/Jade"));
    }
}
