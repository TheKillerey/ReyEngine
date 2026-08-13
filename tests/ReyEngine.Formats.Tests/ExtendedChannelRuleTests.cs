using System.Numerics;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M444: the per-mesh channel block is 40 bytes by default and 48 when the submesh's material selects the
/// other reader. The mapgeo does NOT say which — the material does — so the reader has to be told.
///
/// <para>Riot ships 0 of 207 mapgeo files whose sibling bin mentions Mantis, so none of this can be
/// corpus-checked. What it IS checked against: the Map11 build the game loaded clean, which round-trips
/// byte-exact through <see cref="MapGeoBinary.Read(byte[], IReadOnlySet{string}?)"/> with the set and is
/// refused without it.</para>
/// </summary>
public class ExtendedChannelRuleTests
{
    private const string Plain = "Maps/KitPieces/SRS/Base/Materials/Default/Plain_MAT";
    private const string Extended = "Maps/KitPieces/SRS/Base/Materials/Default/Ground_A5_OrderBase_A_MAT";

    private static IReadOnlySet<string> Set(params string[] names) =>
        new HashSet<string>(names, StringComparer.Ordinal);

    /// <summary>
    /// A map of one-submesh meshes. Fixtures put the interesting mesh in the MIDDLE on purpose:
    /// <see cref="MapGeoBinary.Tail"/> is read as "everything left over", so a desync in the LAST mesh is
    /// absorbed by the tail and still round-trips byte-exact. A trailing mesh would make
    /// <see cref="MapGeoBinary.TryReadEditable"/> look like it had understood a layout it had not.
    /// </summary>
    private static MapGeoBinary MapOf(params string[] materials)
    {
        var map = new MapGeoBinary { Version = 18 };
        map.Declarations.Add(new MapGeoBinary.VertexDeclaration
        {
            Elements = { (MapGeoBinary.ElemPosition, MapGeoBinary.FmtXYZ_Float32) },
            Padding = new byte[8 * 14],
        });
        map.VertexBuffers.Add(new MapGeoBinary.VertexBuffer { Data = new byte[36], HasVisibility = true, Visibility = 0xFF });
        map.IndexBuffers.Add(new MapGeoBinary.IndexBuffer { Data = new byte[6], HasVisibility = true, Visibility = 0xFF });
        foreach (var m in materials) map.Meshes.Add(Mesh(m));
        map.Tail = new byte[8];
        return map;
    }

    private static MapGeoBinary.Mesh Mesh(string material) => new()
    {
        VertexCount = 3, VertexDeclarationBase = 0, VertexBufferIds = { 0 },
        IndexCount = 3, IndexBufferId = 0, Transform = Matrix4x4.Identity,
        HasVisibility = true, Visibility = 0xFF,
        HasRegionHash = true, HasVcHash = true, HasDisableBackface = true,
        HasLayerTransition = true, RenderFlagsIsUshort = true,
        Submeshes = { new MapGeoBinary.Submesh { Material = material } },
    };

    // ---- the size, which is the one thing the in-game bisect actually measured ----

    /// <summary>48 − 40 = 8, and 8 is exactly what had to be inserted to stop the client over-reading.
    /// An empty string (4 bytes of zero length) plus the 4-byte companion value is that 8.</summary>
    [Fact]
    public void The_empty_extended_entry_costs_exactly_eight_bytes()
    {
        int baseline = MapOf(Plain, Plain, Plain).Write().Length;

        var with = MapOf(Plain, Plain, Plain);
        with.Meshes[1].HasExtendedChannel = true;

        Assert.Equal(baseline + 8, with.Write().Length);
    }

    [Fact]
    public void A_mesh_with_an_extended_material_round_trips_byte_exact()
    {
        var map = MapOf(Plain, Extended, Plain);
        map.Meshes[1].HasExtendedChannel = true;
        byte[] bytes = map.Write();

        Assert.True(MapGeoBinary.TryReadEditable(bytes, out var back, Set(Extended)));
        Assert.False(back.Meshes[0].HasExtendedChannel);
        Assert.True(back.Meshes[1].HasExtendedChannel);
        Assert.False(back.Meshes[2].HasExtendedChannel);
    }

    /// <summary>The set has to be load-bearing. A reader that accepts the file either way would prove
    /// nothing about whether it understood the layout.</summary>
    [Fact]
    public void The_same_bytes_are_refused_without_the_material_set()
    {
        var map = MapOf(Plain, Extended, Plain);
        map.Meshes[1].HasExtendedChannel = true;
        byte[] bytes = map.Write();

        Assert.False(MapGeoBinary.TryReadEditable(bytes, out _));
    }

    /// <summary>
    /// The gate's blind spot, pinned so nobody mistakes it for a guarantee: a desync inside the LAST mesh
    /// is swallowed by <see cref="MapGeoBinary.Tail"/>, which is read as "whatever is left". The bytes
    /// still round-trip, so <see cref="MapGeoBinary.TryReadEditable"/> returns true on a model whose final
    /// mesh is misparsed. Editing any OTHER mesh is still safe — the tail is re-emitted verbatim — but the
    /// last mesh's own channel and override fields are not to be trusted.
    /// </summary>
    [Fact]
    public void A_desync_in_the_final_mesh_is_absorbed_by_the_tail()
    {
        var map = MapOf(Plain, Extended);          // extended mesh LAST, deliberately
        map.Meshes[1].HasExtendedChannel = true;
        byte[] bytes = map.Write();

        Assert.True(MapGeoBinary.TryReadEditable(bytes, out var back));
        Assert.False(back.Meshes[1].HasExtendedChannel);       // misparsed, and the gate cannot tell
    }

    /// <summary>And the converse: a file with NO extended mesh must still read with the set present, or
    /// every ordinary mesh in a Mantis-using map would desync.</summary>
    [Fact]
    public void A_plain_file_still_reads_when_the_set_is_supplied()
    {
        byte[] bytes = MapOf(Plain, Plain, Plain).Write();

        Assert.True(MapGeoBinary.TryReadEditable(bytes, out var back, Set(Extended)));
        Assert.All(back.Meshes, m => Assert.False(m.HasExtendedChannel));
    }

    // ---- keeping the layout in step with material edits ----

    [Fact]
    public void Apply_adds_the_entry_when_a_mesh_uses_an_extended_material()
    {
        var map = MapOf(Plain, Extended, Plain);

        Assert.Equal(1, ExtendedChannelRule.Apply(map, Set(Extended)));
        Assert.False(map.Meshes[0].HasExtendedChannel);
        Assert.True(map.Meshes[1].HasExtendedChannel);
    }

    /// <summary>Reassigning a mesh away from Mantis has to REMOVE the entry. Leaving a stale one behind
    /// desyncs the client in the other direction, which is just as fatal and just as silent.</summary>
    [Fact]
    public void Apply_removes_the_entry_when_the_material_no_longer_selects_it()
    {
        var map = MapOf(Plain, Extended, Plain);
        map.Meshes[1].HasExtendedChannel = true;
        map.Meshes[1].ExtendedChannelTexture = "stale";
        map.Meshes[1].ExtendedChannelValue = 7;
        map.Meshes[1].Submeshes[0].Material = Plain;

        Assert.Equal(1, ExtendedChannelRule.Apply(map, Set(Extended)));
        Assert.False(map.Meshes[1].HasExtendedChannel);
        Assert.Equal("", map.Meshes[1].ExtendedChannelTexture);
        Assert.Equal(0u, map.Meshes[1].ExtendedChannelValue);
    }

    [Fact]
    public void Apply_is_idempotent()
    {
        var map = MapOf(Plain, Extended, Plain);

        Assert.Equal(1, ExtendedChannelRule.Apply(map, Set(Extended)));
        Assert.Equal(0, ExtendedChannelRule.Apply(map, Set(Extended)));
        Assert.Equal(0, ExtendedChannelRule.Apply(map, Set(Extended)));
    }

    [Fact]
    public void An_empty_set_leaves_every_mesh_on_the_default_reader()
    {
        var map = MapOf(Plain, Extended, Plain);

        Assert.Equal(0, ExtendedChannelRule.Apply(map, Set()));
        Assert.All(map.Meshes, m => Assert.False(m.HasExtendedChannel));
    }

    [Theory]
    [InlineData("Mantis_Env_Baked_PBR", true)]
    [InlineData("mantis_env_baked_pbr", true)]
    [InlineData("ENV_Lit_Baked", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_shader_family_is_matched_case_insensitively(string? shader, bool expected) =>
        Assert.Equal(expected, ExtendedChannelRule.IsExtendedShader(shader));

    /// <summary>An unparseable bin must yield an EMPTY set, not throw — empty reproduces the default
    /// reader, which is right for every file Riot ships.</summary>
    [Fact]
    public void An_unreadable_materials_bin_yields_an_empty_set()
    {
        Assert.Empty(ExtendedChannelRule.From(new byte[] { 1, 2, 3 }, _ => null));
        Assert.Empty(ExtendedChannelRule.From(Array.Empty<byte>(), _ => null));
    }

    // ---- the baked-paint override, the other half of what the game needed ----

    /// <summary>The header table names the sampler once for the whole file; a mesh override cites its
    /// index. Measured on Map21/IoniaBase, whose table is [0] BAKED_DIFFUSE_TEXTURE,
    /// [1] BAKED_DIFFUSE_TEXTURE_ALPHA.</summary>
    [Fact]
    public void Setting_an_override_appends_the_sampler_once_and_reuses_its_index()
    {
        var map = MapOf(Plain, Extended, Plain);

        map.SetTextureOverride(map.Meshes[0], "BAKED_DIFFUSE_TEXTURE", "a.tex", Vector2.One, Vector2.Zero);
        map.SetTextureOverride(map.Meshes[1], "BAKED_DIFFUSE_TEXTURE", "b.tex", Vector2.One, Vector2.Zero);

        Assert.Single(map.ShaderOverrides);
        Assert.Equal(0, map.ShaderOverrides[0].Index);
        Assert.Equal("BAKED_DIFFUSE_TEXTURE", map.ShaderOverrides[0].Name);
        Assert.Equal("a.tex", map.Meshes[0].TextureOverrides.Single().Name);
        Assert.Equal("b.tex", map.Meshes[1].TextureOverrides.Single().Name);
        Assert.Equal(0, map.Meshes[1].TextureOverrides.Single().Index);
    }

    [Fact]
    public void A_second_sampler_gets_the_next_index()
    {
        var map = MapOf(Plain, Extended, Plain);

        map.SetTextureOverride(map.Meshes[0], "BAKED_DIFFUSE_TEXTURE", "a.tex", Vector2.One, Vector2.Zero);
        map.SetTextureOverride(map.Meshes[0], "BAKED_DIFFUSE_TEXTURE_ALPHA", "b.tex", Vector2.One, Vector2.Zero);

        Assert.Equal(new[] { 0, 1 }, map.ShaderOverrides.Select(o => o.Index));
        Assert.Equal(2, map.Meshes[0].TextureOverrides.Count);
    }

    /// <summary>Re-pointing the same slot must REPLACE, not accumulate — a duplicate index is a shape no
    /// shipped file has.</summary>
    [Fact]
    public void Re_pointing_the_same_slot_replaces_rather_than_duplicates()
    {
        var map = MapOf(Plain, Extended, Plain);

        map.SetTextureOverride(map.Meshes[0], "BAKED_DIFFUSE_TEXTURE", "a.tex", Vector2.One, Vector2.Zero);
        map.SetTextureOverride(map.Meshes[0], "BAKED_DIFFUSE_TEXTURE", "c.tex", new Vector2(0.5f), new Vector2(0.25f));

        Assert.Single(map.Meshes[0].TextureOverrides);
        Assert.Equal("c.tex", map.Meshes[0].TextureOverrides[0].Name);
        Assert.Equal(new Vector2(0.5f), map.Meshes[0].BakedPaintScale);
        Assert.Equal(new Vector2(0.25f), map.Meshes[0].BakedPaintBias);
    }

    [Fact]
    public void An_absent_sampler_reports_minus_one_rather_than_inventing_a_slot()
    {
        var map = MapOf(Plain, Plain, Plain);

        Assert.Equal(-1, map.ShaderOverrideIndex("BAKED_DIFFUSE_TEXTURE"));
        Assert.Empty(map.ShaderOverrides);
    }

    /// <summary>The whole point: a mesh carrying both the extended entry and a baked-paint override has to
    /// survive a full write/read cycle unchanged, because that combination is what the game accepted.</summary>
    [Fact]
    public void The_extended_entry_and_the_override_survive_together()
    {
        var map = MapOf(Plain, Extended, Plain);
        ExtendedChannelRule.Apply(map, Set(Extended));
        map.SetTextureOverride(map.Meshes[1], "BAKED_DIFFUSE_TEXTURE",
            "ASSETS/Shared/Materials/Cobble_moss_Albedo.tex", Vector2.One, Vector2.Zero);
        byte[] bytes = map.Write();

        Assert.True(MapGeoBinary.TryReadEditable(bytes, out var back, Set(Extended)));
        var mesh = back.Meshes[1];
        Assert.True(mesh.HasExtendedChannel);
        Assert.Equal("ASSETS/Shared/Materials/Cobble_moss_Albedo.tex", mesh.TextureOverrides.Single().Name);
        Assert.Equal(Vector2.One, mesh.BakedPaintScale);
        Assert.Equal(Vector2.Zero, mesh.BakedPaintBias);
        Assert.Equal("BAKED_DIFFUSE_TEXTURE", back.ShaderOverrides.Single().Name);
    }
}
