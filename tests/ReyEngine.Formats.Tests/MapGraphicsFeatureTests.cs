using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>M434: MapGraphicsFeature components in MapContainer.components[]. Measured context: 179 of 206
/// shipped map materials.bin declare MapLightingV2; the 27 that do not are the map11 family plus
/// map21/base. DefaultEnv_Flat_BakedTerrain occurs in 2 bins and both declare it.</summary>
public class MapGraphicsFeatureTests
{
    private static readonly uint ContainerCls = HashAlgorithms.Fnv1a("MapContainer");
    private static readonly uint ComponentsField = HashAlgorithms.Fnv1a("components");
    private static readonly uint SunCls = HashAlgorithms.Fnv1a("MapSunProperties");

    /// <summary>A minimal map bin: one MapContainer whose components[] holds MapSunProperties. Container
    /// elements carry no name hash on the wire, which is why these are built with NameHash 0.</summary>
    private static byte[] MapBin(params BinTreeProperty[] extraComponents)
    {
        var components = new List<BinTreeProperty>
        {
            new BinTreeStruct(0, SunCls, new BinTreeProperty[]
            {
                new BinTreeF32(HashAlgorithms.Fnv1a("lightMapColorScale"), 0.6f),
            }),
        };
        components.AddRange(extraComponents);

        var container = new BinTreeObject(HashAlgorithms.Fnv1a("TestMap"), ContainerCls, new BinTreeProperty[]
        {
            new BinTreeContainer(ComponentsField, BinPropertyType.Struct, components),
        });

        var tree = new BinTree(new[] { container }, Array.Empty<string>());
        var ms = new MemoryStream();
        tree.Write(ms);
        return ms.ToArray();
    }

    private static IReadOnlyList<BinTreeProperty> ComponentsOf(byte[] bin)
    {
        var tree = new BinTree(new MemoryStream(bin, false));
        var container = tree.Objects.Values.First(o => o.ClassHash == ContainerCls);
        return ((BinTreeContainer)container.Properties[ComponentsField]).Elements.ToList();
    }

    [Fact]
    public void A_bin_without_graphics_features_reports_none()
    {
        Assert.Empty(MapGraphicsFeatures.Read(MapBin()));
    }

    [Fact]
    public void Adding_lighting_v2_appends_a_component_and_keeps_the_others()
    {
        byte[] original = MapBin();

        byte[]? updated = MapGraphicsFeatures.Add(original, MapGraphicsFeatures.LightingV2, null, out var result);

        Assert.NotNull(updated);
        Assert.True(result.Changed);
        var components = ComponentsOf(updated!);
        Assert.Equal(2, components.Count);
        Assert.Contains(components, c => c is BinTreeStruct s && s.ClassHash == SunCls);
        Assert.Contains(components, c => c is BinTreeStruct s && s.ClassHash == MapGraphicsFeatures.LightingV2.Hash);
        Assert.Contains(MapGraphicsFeatures.LightingV2, MapGraphicsFeatures.Read(updated!));
    }

    /// <summary>map12/jade ships MapLightingV2 with zero properties, so the bare marker is the shipped
    /// form rather than a shortcut.</summary>
    [Fact]
    public void The_default_form_is_a_bare_marker_with_no_properties()
    {
        byte[]? updated = MapGraphicsFeatures.Add(MapBin(), MapGraphicsFeatures.LightingV2, null, out _);

        var added = ComponentsOf(updated!).OfType<BinTreeStruct>()
            .First(s => s.ClassHash == MapGraphicsFeatures.LightingV2.Hash);
        Assert.Empty(added.Properties);
    }

    /// <summary>The map21/ioniabase preset — the closest shipped analogue to a baked-terrain map.</summary>
    [Fact]
    public void The_ionia_preset_writes_the_two_measured_fields()
    {
        byte[]? updated = MapGraphicsFeatures.Add(MapBin(), MapGraphicsFeatures.LightingV2,
            MapGraphicsFeatures.IoniaBaseLightingV2Fields(), out _);

        var added = ComponentsOf(updated!).OfType<BinTreeStruct>()
            .First(s => s.ClassHash == MapGraphicsFeatures.LightingV2.Hash);
        Assert.Equal(2, added.Properties.Count);
        Assert.Equal(0.9f, ((BinTreeF32)added.Properties[HashAlgorithms.Fnv1a("MinimumEnvironmentColorContribution")]).Value);
        Assert.Equal(2000f, ((BinTreeF32)added.Properties[HashAlgorithms.Fnv1a("BounceLightFalloffDistance")]).Value);
    }

    [Fact]
    public void Adding_the_same_feature_twice_is_refused_not_duplicated()
    {
        byte[]? once = MapGraphicsFeatures.Add(MapBin(), MapGraphicsFeatures.LightingV2, null, out _);

        byte[]? twice = MapGraphicsFeatures.Add(once!, MapGraphicsFeatures.LightingV2, null, out var result);

        Assert.Null(twice);
        Assert.False(result.Changed);
        Assert.Contains("already declared", result.Detail);
        Assert.Single(ComponentsOf(once!).OfType<BinTreeStruct>()
            .Where(s => s.ClassHash == MapGraphicsFeatures.LightingV2.Hash));
    }

    /// <summary>Adding fields to a feature already present updates it in place instead of appending a
    /// second copy.</summary>
    [Fact]
    public void Adding_fields_to_an_existing_feature_updates_it_in_place()
    {
        byte[]? once = MapGraphicsFeatures.Add(MapBin(), MapGraphicsFeatures.LightingV2, null, out _);

        byte[]? updated = MapGraphicsFeatures.Add(once!, MapGraphicsFeatures.LightingV2,
            MapGraphicsFeatures.IoniaBaseLightingV2Fields(), out var result);

        Assert.NotNull(updated);
        Assert.True(result.Changed);
        var all = ComponentsOf(updated!).OfType<BinTreeStruct>()
            .Where(s => s.ClassHash == MapGraphicsFeatures.LightingV2.Hash).ToList();
        Assert.Single(all);
        Assert.Equal(2, all[0].Properties.Count);
    }

    [Fact]
    public void Removing_a_feature_drops_only_that_component()
    {
        byte[]? withV2 = MapGraphicsFeatures.Add(MapBin(), MapGraphicsFeatures.LightingV2, null, out _);

        byte[]? removed = MapGraphicsFeatures.Remove(withV2!, MapGraphicsFeatures.LightingV2, out var result);

        Assert.NotNull(removed);
        Assert.True(result.Changed);
        Assert.Empty(MapGraphicsFeatures.Read(removed!));
        Assert.Single(ComponentsOf(removed!));                     // MapSunProperties survives
    }

    /// <summary>M414: Riot ships 0 empty containers in 33,645 materials and writing one crashes the game
    /// at map load. Removing the last component would produce exactly that, so it is refused.</summary>
    [Fact]
    public void Removing_the_last_component_is_refused_rather_than_emptying_the_container()
    {
        var lone = new BinTreeStruct(0, MapGraphicsFeatures.LightingV2.Hash, Array.Empty<BinTreeProperty>());
        var container = new BinTreeObject(HashAlgorithms.Fnv1a("TestMap"), ContainerCls, new BinTreeProperty[]
        {
            new BinTreeContainer(ComponentsField, BinPropertyType.Struct, new BinTreeProperty[] { lone }),
        });
        var ms = new MemoryStream();
        new BinTree(new[] { container }, Array.Empty<string>()).Write(ms);

        byte[]? removed = MapGraphicsFeatures.Remove(ms.ToArray(), MapGraphicsFeatures.LightingV2, out var result);

        Assert.Null(removed);
        Assert.False(result.Changed);
        Assert.Contains("empty", result.Detail);
    }

    [Fact]
    public void Removing_a_feature_that_is_not_there_reports_rather_than_rewriting()
    {
        byte[]? removed = MapGraphicsFeatures.Remove(MapBin(), MapGraphicsFeatures.Ssao, out var result);

        Assert.Null(removed);
        Assert.False(result.Changed);
        Assert.Contains("not declared", result.Detail);
    }

    [Fact]
    public void A_bin_with_no_map_container_is_reported_rather_than_corrupted()
    {
        var lonely = new BinTreeObject(1u, HashAlgorithms.Fnv1a("StaticMaterialDef"), Array.Empty<BinTreeProperty>());
        var ms = new MemoryStream();
        new BinTree(new[] { lonely }, Array.Empty<string>()).Write(ms);

        byte[]? updated = MapGraphicsFeatures.Add(ms.ToArray(), MapGraphicsFeatures.LightingV2, null, out var result);

        Assert.Null(updated);
        Assert.False(result.Changed);
        Assert.Contains("no MapContainer", result.Detail);
    }

    /// <summary>Components are written as Struct (0x82), which is what every shipped MapContainer uses —
    /// NOT the Embedded (0x83) form that material containers require (M416).</summary>
    [Fact]
    public void Components_are_written_as_struct_not_embedded()
    {
        byte[]? updated = MapGraphicsFeatures.Add(MapBin(), MapGraphicsFeatures.LightingV2, null, out _);

        var added = ComponentsOf(updated!).First(c =>
            c is BinTreeStruct s && s.ClassHash == MapGraphicsFeatures.LightingV2.Hash);
        Assert.IsNotType<BinTreeEmbedded>(added);
        Assert.IsType<BinTreeStruct>(added);
    }

    [Fact]
    public void The_family_covers_the_shader_bound_shared_resources()
    {
        var names = MapGraphicsFeatures.All.Select(f => f.Name).ToList();
        Assert.Contains("MapLightingV2", names);       // IBL_CUBEMAP
        Assert.Contains("MapTerrainPaint", names);     // TERRAIN_BLEND
        Assert.Contains("MapSSAO", names);             // SSAO_TEXTURE
        Assert.Contains("MapDynamicLighting", names);  // DYNAMIC_ENV_LIGHT_*
        Assert.Contains("MapGameplayTexture", names);  // GAMEPLAY_TEXTURE
        Assert.Contains("MapLightRegions", names);     // LightRegionInfo
        Assert.All(MapGraphicsFeatures.All, f => Assert.NotEqual(0u, f.Hash));
    }

    /// <summary>M435: 179 of 179 shipped maps declaring MapLightingV2 also set
    /// MapBakeProperties.lightGridFileName. Declaring V2 without one is a shape the corpus never
    /// produces, so the tool should say so.</summary>
    [Fact]
    public void Lighting_v2_without_a_lightgrid_is_flagged()
    {
        byte[] bin = MapBin();

        string? advice = MapGraphicsFeatures.PrerequisiteWarning(bin, MapGraphicsFeatures.LightingV2);

        Assert.NotNull(advice);
        Assert.Contains("179 of 179", advice);
        Assert.Contains("lightGridFileName", advice);
    }

    /// <summary>Only 10 of those 179 set RmaStaticLightGridTexturePath, so it must NOT be demanded.</summary>
    [Fact]
    public void The_rma_lightgrid_texture_is_not_demanded()
    {
        string? advice = MapGraphicsFeatures.PrerequisiteWarning(MapBin(), MapGraphicsFeatures.LightingV2);

        Assert.DoesNotContain("required", advice!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOT needed", advice!);
    }

    [Fact]
    public void A_map_that_already_has_a_lightgrid_is_not_flagged()
    {
        byte[] withGrid = MapBakeProperties.Write(MapBin(), "ASSETS/Maps/Lightmaps/Test/LightGrid.dat",
            256, 0.5f, out var wrote)!;
        Assert.True(wrote.Written);

        Assert.Null(MapGraphicsFeatures.PrerequisiteWarning(withGrid, MapGraphicsFeatures.LightingV2));
    }

    /// <summary>The advisory is specific to MapLightingV2 - no other feature has a measured prerequisite.</summary>
    [Fact]
    public void Other_features_carry_no_prerequisite()
    {
        Assert.Null(MapGraphicsFeatures.PrerequisiteWarning(MapBin(), MapGraphicsFeatures.TerrainPaint));
        Assert.Null(MapGraphicsFeatures.PrerequisiteWarning(MapBin(), MapGraphicsFeatures.Ssao));
    }

    // ---- M436: never-shipped guard + field editing ----

    /// <summary>MapGameplayTexture appears in 0 of 206 shipped map bins, and adding it as a bare marker
    /// is CONFIRMED to break the client with Missing sampler "AlphaMask" — its Red/Green/Blue/Alpha
    /// Channel fields are Links defaulting to "0x0".</summary>
    [Fact]
    public void A_never_shipped_feature_is_flagged_before_anything_else()
    {
        string? advice = MapGraphicsFeatures.PrerequisiteWarning(MapBin(), MapGraphicsFeatures.GameplayTexture);

        Assert.NotNull(advice);
        Assert.Contains("0 of the 206", advice);
        Assert.Contains("AlphaMask", advice);
        Assert.Contains("AlphaChannel", advice);
    }

    [Fact]
    public void The_shipped_counts_are_the_measured_ones()
    {
        Assert.Equal(180, MapGraphicsFeatures.LightingV2.ShippedMapCount);
        Assert.Equal(29, MapGraphicsFeatures.TerrainPaint.ShippedMapCount);
        Assert.Equal(1, MapGraphicsFeatures.Ssao.ShippedMapCount);
        Assert.Equal(1, MapGraphicsFeatures.Clouds.ShippedMapCount);
        Assert.True(MapGraphicsFeatures.GameplayTexture.IsUnshipped);
        Assert.True(MapGraphicsFeatures.DynamicLighting.IsUnshipped);
        Assert.True(MapGraphicsFeatures.LightRegions.IsUnshipped);
        Assert.False(MapGraphicsFeatures.LightingV2.IsUnshipped);
    }

    /// <summary>Link, container, embed and map fields must NOT be offered for editing — inventing
    /// content for them is how MapGameplayTexture ended up with a null AlphaChannel.</summary>
    [Fact]
    public void Only_scalar_field_types_are_editable()
    {
        foreach (var t in new[] { "Bool", "U8", "U16", "U32", "I32", "F32", "String", "Vec2", "Vec3", "Vec4" })
            Assert.True(MapGraphicsFeatureSettings.IsEditable(t), t);
        foreach (var t in new[] { "Link", "Embed", "Container", "List", "List2", "Map", "Struct", "Optional" })
            Assert.False(MapGraphicsFeatureSettings.IsEditable(t), t);
    }

    [Fact]
    public void Setting_a_float_field_writes_it_and_reading_back_shows_it()
    {
        byte[] withV2 = MapGraphicsFeatures.Add(MapBin(), MapGraphicsFeatures.LightingV2, null, out _)!;
        uint field = HashAlgorithms.Fnv1a("BounceLightFalloffDistance");

        byte[]? updated = MapGraphicsFeatureSettings.SetField(
            withV2, MapGraphicsFeatures.LightingV2, field, "F32", "2500", out var result);

        Assert.NotNull(updated);
        Assert.True(result.Changed);
        var added = ComponentsOf(updated!).OfType<BinTreeStruct>()
            .First(s => s.ClassHash == MapGraphicsFeatures.LightingV2.Hash);
        Assert.Equal(2500f, ((BinTreeF32)added.Properties[field]).Value);
    }

    /// <summary>German locale: a float must parse invariantly, or "2.5" becomes 25.</summary>
    [Fact]
    public void Floats_parse_invariantly_regardless_of_locale()
    {
        var prior = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            byte[] withV2 = MapGraphicsFeatures.Add(MapBin(), MapGraphicsFeatures.LightingV2, null, out _)!;
            uint field = HashAlgorithms.Fnv1a("MinimumEnvironmentColorContribution");

            byte[]? updated = MapGraphicsFeatureSettings.SetField(
                withV2, MapGraphicsFeatures.LightingV2, field, "F32", "0.9", out _);

            var added = ComponentsOf(updated!).OfType<BinTreeStruct>()
                .First(s => s.ClassHash == MapGraphicsFeatures.LightingV2.Hash);
            Assert.Equal(0.9f, ((BinTreeF32)added.Properties[field]).Value, 5);
        }
        finally { System.Threading.Thread.CurrentThread.CurrentCulture = prior; }
    }

    /// <summary>Empty CLEARS the field. That is not the same as zero — an absent field takes the
    /// engine's default, which is what map12/jade relies on.</summary>
    [Fact]
    public void An_empty_value_clears_the_field_rather_than_zeroing_it()
    {
        byte[] withV2 = MapGraphicsFeatures.Add(MapBin(), MapGraphicsFeatures.LightingV2,
            MapGraphicsFeatures.IoniaBaseLightingV2Fields(), out _)!;
        uint field = HashAlgorithms.Fnv1a("BounceLightFalloffDistance");

        byte[]? updated = MapGraphicsFeatureSettings.SetField(
            withV2, MapGraphicsFeatures.LightingV2, field, "F32", "", out var result);

        Assert.NotNull(updated);
        Assert.True(result.Changed);
        var added = ComponentsOf(updated!).OfType<BinTreeStruct>()
            .First(s => s.ClassHash == MapGraphicsFeatures.LightingV2.Hash);
        Assert.False(added.Properties.ContainsKey(field));
        Assert.Single(added.Properties);          // the other ionia field survives
    }

    [Fact]
    public void A_value_that_does_not_parse_is_refused_rather_than_written()
    {
        byte[] withV2 = MapGraphicsFeatures.Add(MapBin(), MapGraphicsFeatures.LightingV2, null, out _)!;

        byte[]? updated = MapGraphicsFeatureSettings.SetField(withV2, MapGraphicsFeatures.LightingV2,
            HashAlgorithms.Fnv1a("BounceLightFalloffDistance"), "F32", "not-a-number", out var result);

        Assert.Null(updated);
        Assert.False(result.Changed);
        Assert.Contains("not a number", result.Detail);
    }

    [Fact]
    public void A_link_field_is_refused_rather_than_invented()
    {
        byte[] withGt = MapGraphicsFeatures.Add(MapBin(), MapGraphicsFeatures.GameplayTexture, null, out _)!;

        byte[]? updated = MapGraphicsFeatureSettings.SetField(withGt, MapGraphicsFeatures.GameplayTexture,
            HashAlgorithms.Fnv1a("AlphaChannel"), "Link", "0x1234", out var result);

        Assert.Null(updated);
        Assert.False(result.Changed);
        Assert.Contains("will not invent", result.Detail);
    }

    [Fact]
    public void Setting_a_field_on_an_undeclared_feature_is_reported()
    {
        byte[]? updated = MapGraphicsFeatureSettings.SetField(MapBin(), MapGraphicsFeatures.Ssao,
            HashAlgorithms.Fnv1a("whatever"), "F32", "1", out var result);

        Assert.Null(updated);
        Assert.Contains("not declared", result.Detail);
    }

    /// <summary>Without a meta database there are no field descriptors — and the editor must offer
    /// nothing rather than guess names.</summary>
    [Fact]
    public void Describe_without_a_meta_database_offers_no_fields()
    {
        byte[] withV2 = MapGraphicsFeatures.Add(MapBin(), MapGraphicsFeatures.LightingV2, null, out _)!;

        Assert.Empty(MapGraphicsFeatureSettings.Describe(withV2, MapGraphicsFeatures.LightingV2, null));
    }

    // ---- M438: fill from a shipped map ----

    /// <summary>Only the four features Riot actually ships have something to copy. The other five have
    /// no source, and offering an invented one is what broke MapGameplayTexture.</summary>
    [Fact]
    public void Only_features_riot_ships_have_a_preset_source()
    {
        Assert.NotNull(MapGraphicsFeaturePresets.SourceMap(MapGraphicsFeatures.LightingV2));
        Assert.NotNull(MapGraphicsFeaturePresets.SourceMap(MapGraphicsFeatures.TerrainPaint));
        Assert.NotNull(MapGraphicsFeaturePresets.SourceMap(MapGraphicsFeatures.Ssao));
        Assert.NotNull(MapGraphicsFeaturePresets.SourceMap(MapGraphicsFeatures.Clouds));

        foreach (var f in MapGraphicsFeatures.All.Where(f => f.IsUnshipped))
            Assert.Null(MapGraphicsFeaturePresets.SourceMap(f));
    }

    [Fact]
    public void Extracting_from_a_bin_that_lacks_the_feature_returns_null()
    {
        Assert.Null(MapGraphicsFeaturePresets.Extract(MapBin(), MapGraphicsFeatures.Ssao));
    }

    /// <summary>Nested embeds and containers are the whole point — they are what a type tuple cannot
    /// build and what MetaDefaultProperty rightly refuses.</summary>
    [Fact]
    public void A_nested_embed_is_copied_whole()
    {
        uint settings = HashAlgorithms.Fnv1a("settings");
        var renderer = new BinTreeEmbedded(HashAlgorithms.Fnv1a("MapSSAORenderer"), 0xce8f4190u, new BinTreeProperty[]
        {
            new BinTreeEmbedded(settings, 0x502b0c72u, new BinTreeProperty[]
            {
                new BinTreeF32(HashAlgorithms.Fnv1a("SampleRadius"), 75f),
            }),
        });
        var source = MapGraphicsFeatures.Add(MapBin(), MapGraphicsFeatures.Ssao,
            new BinTreeProperty[] { renderer }, out _)!;

        var got = MapGraphicsFeaturePresets.Extract(source, MapGraphicsFeatures.Ssao);

        Assert.NotNull(got);
        Assert.Single(got!.Fields);
        Assert.Empty(got.Dropped);
        var copied = Assert.IsAssignableFrom<BinTreeStruct>(got.Fields[0]);
        var inner = Assert.IsAssignableFrom<BinTreeStruct>(copied.Properties[settings]);
        Assert.Equal(75f, ((BinTreeF32)inner.Properties[HashAlgorithms.Fnv1a("SampleRadius")]).Value);
    }

    /// <summary>An ObjectLink's value is an id in the SOURCE bin; copying the number would point at
    /// nothing here. Dropped and reported, never carried across.</summary>
    [Fact]
    public void Link_fields_are_dropped_and_reported()
    {
        uint alpha = HashAlgorithms.Fnv1a("AlphaChannel");
        var source = MapGraphicsFeatures.Add(MapBin(), MapGraphicsFeatures.GameplayTexture,
            new BinTreeProperty[]
            {
                new BinTreeObjectLink(alpha, 0x1234u),
                new BinTreeU8(HashAlgorithms.Fnv1a("TextureResolution"), 2),
            }, out _)!;

        var got = MapGraphicsFeaturePresets.Extract(source, MapGraphicsFeatures.GameplayTexture,
            h => h == alpha ? "AlphaChannel" : null);

        Assert.NotNull(got);
        Assert.Single(got!.Fields);                       // only the U8 survives
        Assert.Contains("AlphaChannel", got.Dropped);
        Assert.DoesNotContain(got.Fields, f => f.NameHash == alpha);
    }

    /// <summary>The copy must not share nodes with the source tree, or editing one would mutate the
    /// other.</summary>
    [Fact]
    public void The_copy_is_deep_and_shares_nothing_with_the_source()
    {
        var source = MapGraphicsFeatures.Add(MapBin(), MapGraphicsFeatures.LightingV2,
            MapGraphicsFeatures.IoniaBaseLightingV2Fields(), out _)!;

        var a = MapGraphicsFeaturePresets.Extract(source, MapGraphicsFeatures.LightingV2)!;
        var b = MapGraphicsFeaturePresets.Extract(source, MapGraphicsFeatures.LightingV2)!;

        Assert.Equal(a.Fields.Count, b.Fields.Count);
        for (int i = 0; i < a.Fields.Count; i++)
            Assert.NotSame(a.Fields[i], b.Fields[i]);
    }

    // ---- M439: authoring MapGameplayTexture's samplers and channels ----

    private static byte[] MapWithGameplayTexture() =>
        MapGraphicsFeatures.Add(MapBin(), MapGraphicsFeatures.GameplayTexture, null, out _)!;

    /// <summary>The texture goes in the sampler list, which is an embedded struct of two strings and
    /// needs no link at all — GameplayTextureChannel has no texture field.</summary>
    [Fact]
    public void A_sampler_carries_the_texture_path_without_any_link()
    {
        byte[]? updated = MapGameplayTextureBuilder.SetSampler(
            MapWithGameplayTexture(), "Base", "ASSETS/Maps/Gameplay/Test.tex", out var result);

        Assert.NotNull(updated);
        Assert.True(result.Changed);
        var samplers = MapGameplayTextureBuilder.Samplers(updated!);
        Assert.Single(samplers);
        Assert.Equal("Base", samplers[0].Name);
        Assert.Equal("ASSETS/Maps/Gameplay/Test.tex", samplers[0].TexturePath);
    }

    [Fact]
    public void Setting_the_same_sampler_name_replaces_rather_than_duplicates()
    {
        byte[] once = MapGameplayTextureBuilder.SetSampler(
            MapWithGameplayTexture(), "Base", "ASSETS/A.tex", out _)!;

        byte[]? twice = MapGameplayTextureBuilder.SetSampler(once, "Base", "ASSETS/B.tex", out _);

        var samplers = MapGameplayTextureBuilder.Samplers(twice!);
        Assert.Single(samplers);
        Assert.Equal("ASSETS/B.tex", samplers[0].TexturePath);
    }

    [Fact]
    public void A_sampler_without_a_texture_path_is_refused()
    {
        byte[]? updated = MapGameplayTextureBuilder.SetSampler(
            MapWithGameplayTexture(), "Base", "  ", out var result);

        Assert.Null(updated);
        Assert.Contains("needs a texture path", result.Detail);
    }

    /// <summary>All four slots start null — which is exactly the state that makes the client fail with
    /// Missing sampler "AlphaMask".</summary>
    [Fact]
    public void A_bare_component_reports_all_four_slots_unlinked()
    {
        var unlinked = MapGameplayTextureBuilder.UnlinkedSlots(MapWithGameplayTexture());

        Assert.Equal(4, unlinked.Count);
        Assert.Contains(MapGameplayTextureBuilder.Slot.Alpha, unlinked);
    }

    /// <summary>A link stores the target's path hash, so the channel must exist as a real object in THIS
    /// bin. That is why a link cannot be set by typing a number, and why copying between bins drops it.</summary>
    [Fact]
    public void Setting_a_channel_creates_the_object_and_links_it()
    {
        byte[]? updated = MapGameplayTextureBuilder.SetChannel(
            MapWithGameplayTexture(), MapGameplayTextureBuilder.Slot.Alpha, "Grass", null, out var result);

        Assert.NotNull(updated);
        Assert.True(result.Changed);

        var tree = new BinTree(new MemoryStream(updated!, false));
        var channel = tree.Objects.Values.FirstOrDefault(o => o.ClassHash == MapGameplayTextureBuilder.ChannelClass);
        Assert.NotNull(channel);
        Assert.Equal("Grass", ((BinTreeString)channel!.Properties[MapGameplayTextureBuilder.FieldChannelName]).Value);

        var component = ComponentsOf(updated!).OfType<BinTreeStruct>()
            .First(s => s.ClassHash == MapGraphicsFeatures.GameplayTexture.Hash);
        var link = Assert.IsType<BinTreeObjectLink>(
            component.Properties[MapGameplayTextureBuilder.SlotField(MapGameplayTextureBuilder.Slot.Alpha)]);
        Assert.Equal(channel.PathHash, link.Value);
        Assert.DoesNotContain(MapGameplayTextureBuilder.Slot.Alpha, MapGameplayTextureBuilder.UnlinkedSlots(updated!));
    }

    /// <summary>Re-running with the same name must reuse the id rather than leave an orphan behind.</summary>
    [Fact]
    public void Setting_the_same_channel_twice_does_not_mint_a_second_object()
    {
        byte[] once = MapGameplayTextureBuilder.SetChannel(
            MapWithGameplayTexture(), MapGameplayTextureBuilder.Slot.Red, "Grass", null, out _)!;

        byte[] twice = MapGameplayTextureBuilder.SetChannel(
            once, MapGameplayTextureBuilder.Slot.Red, "Grass", null, out _)!;

        var tree = new BinTree(new MemoryStream(twice, false));
        Assert.Single(tree.Objects.Values.Where(o => o.ClassHash == MapGameplayTextureBuilder.ChannelClass));
    }

    [Fact]
    public void All_four_slots_can_be_linked_to_distinct_channels()
    {
        byte[] bin = MapWithGameplayTexture();
        foreach (var slot in Enum.GetValues<MapGameplayTextureBuilder.Slot>())
            bin = MapGameplayTextureBuilder.SetChannel(bin, slot, "Ch" + slot, null, out _)!;

        Assert.Empty(MapGameplayTextureBuilder.UnlinkedSlots(bin));
        var tree = new BinTree(new MemoryStream(bin, false));
        Assert.Equal(4, tree.Objects.Values.Count(o => o.ClassHash == MapGameplayTextureBuilder.ChannelClass));
    }

    [Fact]
    public void Authoring_without_the_component_present_is_reported()
    {
        byte[]? updated = MapGameplayTextureBuilder.SetSampler(MapBin(), "Base", "ASSETS/A.tex", out var result);

        Assert.Null(updated);
        Assert.Contains("declares no MapGameplayTexture", result.Detail);
    }
}
