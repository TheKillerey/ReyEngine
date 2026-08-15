using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

public sealed class LegacyMapPorterTests
{
    /// <summary>
    /// M473: the correction the porter applies by default. Pinned so it cannot drift silently — every
    /// ported map's alignment depends on it, and a wrong value is only visible by loading a map and
    /// looking.
    ///
    /// <para>This previously asserted (600.406, -66.972, 293.744), derived as an initial pass plus a
    /// refinement. The user measured the full fix against a ported map and reported
    /// (+1000.834, -51.318, +499.388), which REPLACES that rather than stacking on it — the assumption is
    /// recorded on the constant itself. Kept as a hardcoded expectation rather than re-deriving it from
    /// intermediate vectors: the old form asserted the arithmetic that produced the number, which passes
    /// happily while the number is wrong for the map.</para>
    /// </summary>
    [Fact]
    public void LegacyPositionCorrectionIsTheMeasuredFullFix()
    {
        Assert.Equal(1000.834f, LegacyMapPorter.LegacyPositionCorrection.X, 3);
        Assert.Equal(-51.318f, LegacyMapPorter.LegacyPositionCorrection.Y, 3);
        Assert.Equal(499.388f, LegacyMapPorter.LegacyPositionCorrection.Z, 3);
    }

    /// <summary>The superseded value is kept so a map ported by an older build can be reconciled: the
    /// difference between the two is exactly the nudge such a map needs.</summary>
    [Fact]
    public void The_superseded_correction_is_retained_for_reconciling_old_ports()
    {
        var delta = LegacyMapPorter.LegacyPositionCorrection - LegacyMapPorter.LegacyPositionCorrectionPreM473;
        Assert.Equal(400.428f, delta.X, 3);
        Assert.Equal(15.654f, delta.Y, 3);
        Assert.Equal(205.644f, delta.Z, 3);
    }

    /// <summary>M473: the correction is a PARAMETER now, not only a constant. Ports made before this could
    /// not be realigned without editing source and rebuilding.</summary>
    [Fact]
    public void The_correction_can_be_overridden_per_port()
    {
        var m = typeof(LegacyMapPorter).GetMethod(nameof(LegacyMapPorter.ApplyImportedPositionCorrection));
        Assert.NotNull(m);
        var p = Assert.Single(m!.GetParameters(), x => x.Name == "correction");
        Assert.True(p.IsOptional, "the override must be optional so existing callers keep the default");
        Assert.Equal(typeof(System.Numerics.Vector3?), p.ParameterType);
    }

    [Fact]
    public void JadeContainerUsesOnlyItsDefaultEnvGameplayBushMaterial()
    {
        var materials = LegacyMapPorter.MapSpecificBushMaterials(
            "data/maps/mapgeometry/map453/jade_container.mapgeo");

        Assert.NotNull(materials);
        Assert.Equal("Maps/KitPieces/Jade/Base/Materials/Default/Jade_Foliage_Grass_AA_MAT",
            Assert.Single(materials!));
        Assert.Null(LegacyMapPorter.MapSpecificBushMaterials(
            "data/maps/mapgeometry/map11/base_srx.mapgeo"));
    }

    [Fact]
    public void AppliesUserShaderChoicesToEveryDetectedRole()
    {
        static LegacyMaterialPlan Plan(string name, LegacyMaterialRole role) => new(name, role, "old",
            new Dictionary<string, string>(), new Dictionary<string, System.Numerics.Vector4>(),
            new Dictionary<string, bool>(), new Dictionary<string, bool>());
        var source = new LegacyMapPortResult(Array.Empty<byte>(), Array.Empty<LegacyTextureCopy>(), new[]
        {
            Plan("normal", LegacyMaterialRole.Normal), Plan("decal", LegacyMaterialRole.Decal),
            Plan("grass", LegacyMaterialRole.Grass), Plan("terrain", LegacyMaterialRole.FourBlendTerrain),
        }, "room.nvr", "NVR", 4, 4, 0, 0, 4, Array.Empty<string>());

        var mapped = LegacyMapPorter.ApplyShaderOptions(source,
            new LegacyPortShaderOptions("normal_shader", "decal_shader", "grass_shader", "terrain_shader"));

        Assert.Equal("normal_shader", mapped.Materials.Single(m => m.Role == LegacyMaterialRole.Normal).Shader);
        Assert.Equal("decal_shader", mapped.Materials.Single(m => m.Role == LegacyMaterialRole.Decal).Shader);
        Assert.Equal("grass_shader", mapped.Materials.Single(m => m.Role == LegacyMaterialRole.Grass).Shader);
        Assert.Equal("terrain_shader", mapped.Materials.Single(m => m.Role == LegacyMaterialRole.FourBlendTerrain).Shader);
    }

    [Fact]
    public void AlphaTestRolesDoNotAuthorUnavailableNoBakePermutation()
    {
        Assert.False(LegacyMapPorter.UsesNoBakedLightingByDefault(LegacyMaterialRole.Decal));
        Assert.False(LegacyMapPorter.UsesNoBakedLightingByDefault(LegacyMaterialRole.Normal));
        Assert.True(LegacyMapPorter.UsesNoBakedLightingByDefault(LegacyMaterialRole.Grass));
        Assert.True(LegacyMapPorter.UsesNoBakedLightingByDefault(LegacyMaterialRole.FourBlendTerrain));
    }

    [Fact]
    public void ShaderMappingAuthorsNeutralTintAndUsefulAlphaCutoffs()
    {
        static LegacyMaterialPlan Plan(string name, LegacyMaterialRole role) => new(name, role, "old",
            new Dictionary<string, string>(), new Dictionary<string, System.Numerics.Vector4>(),
            new Dictionary<string, bool>(), new Dictionary<string, bool>());
        var source = new LegacyMapPortResult(Array.Empty<byte>(), Array.Empty<LegacyTextureCopy>(), new[]
        {
            Plan("normal", LegacyMaterialRole.Normal), Plan("decal", LegacyMaterialRole.Decal),
            Plan("grass", LegacyMaterialRole.Grass), Plan("terrain", LegacyMaterialRole.FourBlendTerrain),
        }, "room.nvr", "NVR", 4, 4, 0, 0, 4, Array.Empty<string>());

        var mapped = LegacyMapPorter.ApplyShaderOptions(source, LegacyPortShaderOptions.Defaults);

        foreach (var material in mapped.Materials)
            Assert.Equal(System.Numerics.Vector4.One, material.Parameters["TintColor"]);
        Assert.Equal(0.35f, mapped.Materials.Single(m => m.Role == LegacyMaterialRole.Normal)
            .Parameters["AlphaTestValue"].X);
        Assert.Equal(0.005f, mapped.Materials.Single(m => m.Role == LegacyMaterialRole.Decal)
            .Parameters["AlphaTestValue"].X);
        Assert.Equal(0.01f, mapped.Materials.Single(m => m.Role == LegacyMaterialRole.FourBlendTerrain)
            .Parameters["WS_Multiplier"].X);
        Assert.DoesNotContain("NO_BAKED_LIGHTING",
            mapped.Materials.Single(m => m.Role == LegacyMaterialRole.Normal).Macros.Keys);
    }

    /// <summary>
    /// M476: the porter's vertex-element ORDER must be one Riot actually ships, because element order IS
    /// the byte layout of the vertex buffer.
    ///
    /// <para>The porter emitted Texcoord5 before Texcoord0. Censused over 206 shipped mapgeos and 27
    /// distinct orders, Riot ships "Position, Normal, Color0, Tex0, Tex5" 28 times and the reversed form
    /// ZERO times. Nothing in ReyEngine could see it: our reader is order-driven, so strides matched,
    /// buffer sizes matched, bounds were sane and no coordinate was NaN — the file round-tripped through
    /// our own code perfectly and only the game disagreed, rendering spikes.</para>
    ///
    /// <para>Asserted against the constants rather than a rebuilt port, because a port needs an NVR
    /// fixture that cannot be committed. This pins the ORDERING RULE the writer follows.</para>
    /// </summary>
    [Fact]
    public void The_ported_vertex_layout_uses_an_order_Riot_ships()
    {
        // The four layouts the porter can emit, in the order its declaration builder appends them.
        static string[] Layout(bool color, bool grassPivot)
        {
            var e = new List<string> { "Position", "Normal" };
            if (color) e.Add("Color0");
            e.Add("Tex0");                 // M476: Tex0 before Tex5
            if (grassPivot) e.Add("Tex5");
            return e.ToArray();
        }

        // Orders measured in shipped map WADs. The reversed grass form is deliberately absent.
        var shipped = new HashSet<string>(StringComparer.Ordinal)
        {
            "Position,Normal,Tex0",
            "Position,Normal,Color0,Tex0",
            "Position,Normal,Color0,Tex0,Tex5",
        };

        Assert.Contains(string.Join(",", Layout(false, false)), shipped);
        Assert.Contains(string.Join(",", Layout(true, false)), shipped);
        Assert.Contains(string.Join(",", Layout(true, true)), shipped);

        // And the form that caused the bug is NOT what the builder produces any more.
        Assert.DoesNotContain("Tex5,Tex0", string.Join(",", Layout(true, true)));
    }
}
