using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Lighting;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Meta;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M900: MapDynamicSpotlight. Riot ships none, so the wire form is the point-light form plus schema-checked fields; the real
/// samples are Project Rift V2's t46 bins (91 spotlights written by apply_spotlights.py and tested in game on 16.22).
/// </summary>
public class DynamicSpotlightTests(ITestOutputHelper output)
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private const string T46 = @"D:\ProjectReyMaps\_ModUpdate\ProjectRiftV2\work\claude\t46\spotlights\out\m25_spot_nz.bin";

    private static BinTreeStruct Spot(string name, Vector3 pos, Matrix4x4? rot = null, float? fov = 70, float? far = 800,
        float? near = null, Vector4? color = null, bool unknown = false)
    {
        var m = rot ?? Matrix4x4.Identity;
        m.M41 = pos.X; m.M42 = pos.Y; m.M43 = pos.Z;
        var p = new List<BinTreeProperty> { new BinTreeMatrix44(H("transform"), m), new BinTreeString(H("name"), name) };
        if (fov is { } f) p.Add(new BinTreeF32(H("fov"), f));
        if (far is { } fa) p.Add(new BinTreeF32(H("far"), fa));
        if (near is { } n) p.Add(new BinTreeF32(H("Near"), n));
        if (color is { } c) p.Add(new BinTreeVector4(H("Color"), c));
        if (unknown) p.Add(new BinTreeF32(H("HdrScale"), 7f));   // a field the editor does not model
        return new BinTreeStruct(0, H("MapDynamicSpotlight"), p);
    }

    private static byte[] BinWith(params BinTreeProperty[] items)
    {
        var map = new BinTreeMap(H("items"), BinPropertyType.Hash, BinPropertyType.Struct,
            items.Select((p, i) => new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, 0x1000u + (uint)i), p)));
        var obj = new BinTreeObject(1u, H("MapPlaceableContainer"), new BinTreeProperty[] { map });
        var ms = new MemoryStream();
        new BinTree(new[] { obj }, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    [Fact]
    public void The_class_hash_is_the_one_the_schema_gives()
        => Assert.Equal(0x9063268eu, DynamicSpotlights.SpotlightClass);

    [Fact]
    public void A_spotlight_reads_with_its_authored_fields_and_the_schema_defaults_for_the_rest()
    {
        var bin = BinWith(Spot("S1", new Vector3(10, 20, 30), fov: 80, far: 900, near: null, color: new Vector4(1, 0.5f, 0.25f, 1)),
                          Spot("S2", Vector3.Zero, fov: null, far: null));
        var all = DynamicSpotlights.Read(bin);
        Assert.Equal(2, all.Count);
        var a = all[0];
        Assert.Equal("S1", a.Name);
        Assert.Equal(new Vector3(10, 20, 30), a.Position);
        Assert.Equal(80f, a.Fov); Assert.Equal(900f, a.Far);
        Assert.Equal(DynamicSpotlights.DefaultNear, a.Near);
        Assert.Equal(DynamicSpotlights.DefaultAspect, a.Aspect);
        Assert.Equal(new Vector4(1, 0.5f, 0.25f, 1), a.Color);
        Assert.Equal(new MapPlacementId(1u, 0x1000u), a.Id);
        Assert.Equal(DynamicSpotlights.DefaultFov, all[1].Fov);
        Assert.Equal(DynamicSpotlights.DefaultFar, all[1].Far);
        Assert.Equal(DynamicSpotlights.DefaultColor, all[1].Color);
    }

    [Fact]
    public void Garbage_reads_as_no_spotlights()
    {
        Assert.Empty(DynamicSpotlights.Read(Array.Empty<byte>()));
        Assert.Empty(DynamicSpotlights.Read(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void An_unchanged_list_returns_the_input_bytes_and_an_edit_touches_only_what_it_names()
    {
        var bin = BinWith(Spot("S1", new Vector3(1, 2, 3), unknown: true), Spot("S2", new Vector3(4, 5, 6)));
        var list = DynamicSpotlights.Read(bin).ToList();

        Assert.Same(bin, DynamicSpotlights.Write(bin, list, out var err0));   // nothing to do: not even re-serialised
        Assert.Null(err0);

        list[0] = list[0] with { Color = new Vector4(0, 1, 0, 1), Far = 1234f, Name = "Renamed" };
        var outBin = DynamicSpotlights.Write(bin, list, out var err);
        Assert.NotNull(outBin); Assert.Null(err);

        var back = DynamicSpotlights.Read(outBin!);
        Assert.Equal("Renamed", back[0].Name);
        Assert.Equal(1234f, back[0].Far);
        Assert.Equal(new Vector4(0, 1, 0, 1), back[0].Color);
        Assert.Equal(list[0].Fov, back[0].Fov);
        Assert.Equal(list[1], back[1] with { });      // the untouched neighbour reads identically

        // the unmodelled field survives the edit
        var tree = SafeBinTree.Parse(outBin!);
        var first = tree.Objects.Values.Single().Properties[H("items")] as BinTreeMap;
        var s = (BinTreeStruct)first!.First().Value;
        Assert.True(s.Properties.TryGetValue(H("HdrScale"), out var hdr) && hdr is BinTreeF32 { Value: 7f });
    }

    [Fact]
    public void Absent_fields_stay_absent_on_edit_and_create()
    {
        var bin = BinWith(Spot("S1", Vector3.Zero, near: null));
        var list = DynamicSpotlights.Read(bin).ToList();
        list.Add(new DynamicSpotlight(default, "New", Matrix4x4.CreateTranslation(5, 6, 7), new Vector4(1, 1, 1, 1), 60f, 500f,
            DynamicSpotlights.DefaultNear, DynamicSpotlights.DefaultAspect, 0, "", 0, 255, false));
        var outBin = DynamicSpotlights.Write(bin, list, out var err);
        Assert.NotNull(outBin); Assert.Null(err);

        var tree = SafeBinTree.Parse(outBin!);
        var items = (BinTreeMap)tree.Objects.Values.Single().Properties[H("items")];
        Assert.Equal(2, items.Count);
        foreach (var kv in items)
        {
            var props = ((BinTreeStruct)kv.Value).Properties;
            Assert.False(props.ContainsKey(H("Near")));
            Assert.False(props.ContainsKey(H("Aspect")));
            Assert.False(props.ContainsKey(H("ProjectedTexture")));
            Assert.False(props.ContainsKey(H("mVisibilityFlags")));
            Assert.False(props.ContainsKey(H("HdrScale")));
        }
        var made = DynamicSpotlights.Read(outBin!).Single(s => s.Name == "New");
        Assert.Equal(new Vector3(5, 6, 7), made.Position);
        Assert.True(made.Id.IsValid);
    }

    [Fact]
    public void A_cookie_is_written_as_the_wad_chunk_link_and_reads_back()
    {
        var bin = BinWith(Spot("S1", Vector3.Zero));
        var one = DynamicSpotlights.Read(bin).Single() with { CookiePath = "assets/maps/test/cookie.tex" };
        var outBin = DynamicSpotlights.Write(bin, new[] { one }, out var err);
        Assert.NotNull(outBin); Assert.Null(err);
        var back = DynamicSpotlights.Read(outBin!, h => h == HashAlgorithms.WadPath("assets/maps/test/cookie.tex") ? "assets/maps/test/cookie.tex" : null).Single();
        Assert.Equal(HashAlgorithms.WadPath("assets/maps/test/cookie.tex"), back.CookieHash);
        Assert.Equal("assets/maps/test/cookie.tex", back.CookiePath);

        var tree = SafeBinTree.Parse(outBin!);
        var s = (BinTreeStruct)((BinTreeMap)tree.Objects.Values.Single().Properties[H("items")]).First().Value;
        Assert.IsType<BinTreeWadChunkLink>(s.Properties[H("ProjectedTexture")]);

        // and clearing it removes the field
        var cleared = DynamicSpotlights.Write(outBin!, new[] { back with { CookiePath = "" } }, out _);
        var s2 = (BinTreeStruct)((BinTreeMap)SafeBinTree.Parse(cleared!).Objects.Values.Single().Properties[H("items")]).First().Value;
        Assert.False(s2.Properties.ContainsKey(H("ProjectedTexture")));
    }

    [Fact]
    public void A_spotlight_missing_from_the_list_is_removed_and_nothing_else_moves()
    {
        var bin = BinWith(Spot("S1", Vector3.Zero), Spot("S2", Vector3.One));
        var all = DynamicSpotlights.Read(bin);
        var outBin = DynamicSpotlights.Write(bin, new[] { all[1] }, out var err);
        Assert.NotNull(outBin); Assert.Null(err);
        var back = DynamicSpotlights.Read(outBin!).Single();
        Assert.Equal("S2", back.Name);
        Assert.Equal(all[1], back);
    }

    [Fact]
    public void The_light_shines_along_local_minus_Z()
    {
        var m = Matrix4x4.Identity;                                        // local Z = world +Z
        Assert.Equal(-Vector3.UnitZ, DynamicSpotlights.ForwardOf(m));
        // pointing straight down: local Z row = (0,1,0)
        var down = new Matrix4x4(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1);
        Assert.Equal(-Vector3.UnitY, DynamicSpotlights.ForwardOf(down));
    }

    private static Vector3 Project(in Matrix4x4 m, Vector3 p)
    {
        var c = Vector4.Transform(new Vector4(p, 1), m);                  // row-vector: [P,1] * M
        return new Vector3(c.X / c.W, c.Y / c.W, c.Z / c.W);
    }

    [Fact]
    public void The_projection_maps_the_cone_to_the_unit_square_and_rejects_what_is_behind()
    {
        var down = new Matrix4x4(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 100, 500, 100, 1);   // at (100,500,100), looking down
        var m = DynamicSpotlights.WorldToLight(down, 60f, 500f, 10f, 1f);

        var centre = Project(m, new Vector3(100, 0, 100));                 // 500 below, on the axis
        Assert.Equal(0.5f, centre.X, 1e-4f); Assert.Equal(0.5f, centre.Y, 1e-4f);
        Assert.InRange(centre.Z, 0f, 1f);

        float half = 400f * MathF.Tan(30f * MathF.PI / 180f);             // frustum half-width 400 below the light
        var edge = Project(m, new Vector3(100 + half * 0.99f, 100, 100));
        Assert.InRange(edge.X, 0.99f, 1f);
        var outside = Project(m, new Vector3(100 + half * 1.2f, 100, 100));
        Assert.True(outside.X > 1f || outside.Y < 0f || outside.Y > 1f, "outside the cone leaves [0,1]");

        Assert.True(Project(m, new Vector3(100, -100, 100)).Z > 1f, "past far (600 below) is beyond depth 1");
        Assert.True(Project(m, new Vector3(100, 800, 100)).Z > 1f, "BEHIND the light is rejected by the depth test too");
    }

    [Fact]
    public void The_frustum_wireframe_has_twelve_edges_and_the_axis_and_stays_finite()
    {
        var lines = DynamicSpotlights.BuildFrustumLines(Matrix4x4.CreateTranslation(1, 2, 3), 60f, 500f, 10f, 1f);
        Assert.Equal(13 * 6, lines.Length);
        Assert.All(lines, v => Assert.True(float.IsFinite(v)));
        Assert.Empty(DynamicSpotlights.BuildFrustumLines(Matrix4x4.Identity, 60f, 0f, 10f, 1f));
        var corners = DynamicSpotlights.FrustumCorners(Matrix4x4.Identity, 90f, 100f, 10f, 1f);
        Assert.Equal(-100f, corners[4].Z, 1e-3f);                          // far plane along -Z
        Assert.Equal(100f, MathF.Abs(corners[4].X), 1e-2f);                // 90 degrees: half-width equals distance
    }

    [Fact]
    public void A_real_t46_bin_round_trips_byte_equal_and_an_edit_changes_only_its_own_spotlight()
    {
        if (!File.Exists(T46)) { output.WriteLine("SKIPPED: the t46 sample bin is not on this machine"); return; }
        byte[] bytes = File.ReadAllBytes(T46);

        var all = DynamicSpotlights.Read(bytes);
        output.WriteLine($"t46: {all.Count} spotlights; first {all[0].Name} at {all[0].Position} fov {all[0].Fov} far {all[0].Far} "
                         + $"cookie 0x{all[0].CookieHash:x16}");
        Assert.True(all.Count >= 80, $"t46 writes 91 spotlights, found {all.Count}");
        Assert.All(all, s => { Assert.True(s.Id.IsValid); Assert.True(s.Far > 0); Assert.NotEqual(0ul, s.CookieHash); });

        // unchanged -> the very same bytes, no re-serialise at all
        Assert.Same(bytes, DynamicSpotlights.Write(bytes, all, out _));

        // a no-op through the full writer (re-serialise) is a faithful rewrite of the spotlights
        var edited = all.ToList();
        edited[3] = edited[3] with { Color = edited[3].Color * 0.5f, Fov = 75f };
        var outBin = DynamicSpotlights.Write(bytes, edited, out var err);
        Assert.True(outBin is not null, err);
        var back = DynamicSpotlights.Read(outBin!).ToDictionary(x => x.Id);   // the rewrite may reorder containers: compare by identity
        Assert.Equal(all.Count, back.Count);
        foreach (var before in all)
        {
            if (before.Id == edited[3].Id) { Assert.Equal(75f, back[before.Id].Fov); Assert.Equal(edited[3].Color, back[before.Id].Color); }
            else Assert.Equal(before, back[before.Id]);
        }

        // adding then deleting returns the same spotlights
        var added = all.Append(new DynamicSpotlight(default, "M900 test", Matrix4x4.CreateTranslation(1, 2, 3), Vector4.One, 60f, 500f,
            10f, 1f, 0, "", 0, 255, false)).ToList();
        var withNew = DynamicSpotlights.Write(bytes, added, out err);
        Assert.True(withNew is not null, err);
        var listed = DynamicSpotlights.Read(withNew!);
        Assert.Equal(all.Count + 1, listed.Count);
        var removed = DynamicSpotlights.Write(withNew!, listed.Where(s => s.Name != "M900 test"), out err);
        Assert.True(removed is not null, err);
        Assert.Equal(all.OrderBy(x => x.Id.ItemKey), DynamicSpotlights.Read(removed!).OrderBy(x => x.Id.ItemKey));
    }

    [Fact]
    public void A_spot_in_the_cluster_builder_is_a_28_uint_record_after_the_point_records_and_counted_in_the_header()
    {
        var down = new Matrix4x4(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 500, 0, 1);
        var proj = DynamicSpotlights.WorldToLight(down, 60f, 500f, 10f, 1f);
        var (bmin, bmax) = DynamicSpotlights.Bounds(down, 60f, 500f, 10f, 1f);
        var spot = new ClusterSpotLight(new Vector3(0, 500, 0), 500f, new Vector3(1, 0.5f, 0.25f), proj, new Vector4(1, 1, 0, 0), bmin, bmax);
        var point = ClusterLightBuilder.MakeLight(new Vector3(0, 10, 0), 100f, Vector3.One, 1f);

        var grid = ClusterLightBuilder.Build(new[] { point }, new Vector3(-600, 0, -600), new Vector3(600, 600, 600), spotLights: new[] { spot });
        Assert.Equal(2, grid.LightCount); Assert.Equal(1, grid.SpotLightCount);

        // find a cell holding both: the one under the apex
        uint cell = grid.Map.First(c => c != 0 && grid.Data[c * 4] == 1 && (grid.Data[c * 4 + 1] & 0xFFFF) == 1);
        uint pointCount = grid.Data[cell * 4] & 0xFFFF, spotCount = grid.Data[cell * 4 + 1] & 0xFFFF;
        Assert.Equal(1u, pointCount); Assert.Equal(1u, spotCount);
        int maskWords = (int)(grid.Data[cell * 4 + 3] >> 16);
        Assert.Equal(1, maskWords);
        int rec = (int)(cell + 1 + (uint)maskWords) * 4;
        rec += (int)pointCount * 8;                                        // the point record first
        Assert.Equal(1f / 500f, BitConverter.UInt32BitsToSingle(grid.Data[rec + 23]), 1e-9f);   // word 5 .w = 1 / far
        Assert.Equal(1f, BitConverter.UInt32BitsToSingle(grid.Data[rec + 24]), 1e-9f);          // word 6 = cookie scale
        Assert.Equal(proj.M11, BitConverter.UInt32BitsToSingle(grid.Data[rec]), 1e-9f);         // word 0 = row 0
        Assert.Equal(proj.M44, BitConverter.UInt32BitsToSingle(grid.Data[rec + 15]), 1e-9f);
    }
}
