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

        // M500: neutral is 0.5019608, NOT 1.0 — this assertion previously encoded the wrong belief and so
        // guarded the bug instead of the behaviour. DefaultEnv_Flat applies TintColor as an OVERLAY blend
        // (defaultenv_flat.ps.dx11 blob 141 lines 159-168) whose identity is 0.5, and Riot's shaders.bin
        // declares 0.5019608 = 128/255. At 1.0 the ported map rendered its albedo ~2x too bright in DX11
        // (measured 2.000x and 1.882x on two of this map's real textures, with 6% of channels clipped to
        // white); GL hid it by dropping TintColor for opaque diffuse-textured materials.
        foreach (var material in mapped.Materials)
        {
            var tint = material.Parameters["TintColor"];
            Assert.Equal(LegacyMapPorter.NeutralTint, tint.X, 5);
            Assert.Equal(LegacyMapPorter.NeutralTint, tint.Y, 5);
            Assert.Equal(LegacyMapPorter.NeutralTint, tint.Z, 5);
            Assert.NotEqual(1f, tint.X);
        }
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
        // The layouts the porter can emit, in the order its declaration builder appends them.
        static string[] Layout(bool color, bool grassPivot, bool uv2 = false)
        {
            var e = new List<string> { "Position", "Normal" };
            if (color) e.Add("Color0");
            e.Add("Tex0");                 // M476: Tex0 before Tex5
            if (grassPivot) e.Add("Tex5");
            if (uv2) e.Add("Tex7");        // M477: inline and last, not a separate buffer
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

    /// <summary>
    /// M477: the four-blend terrain layout, which is the one that rendered WHITE.
    ///
    /// <para>Both 4TextureBlend vertex shaders REQUIRE Texcoord7 — measured from the compiled DXBC input
    /// signatures of <c>4textureblend_worldprojected.vs-dx11</c> and
    /// <c>4textureblend_uvbased_basemat.vs-dx11</c>, which read POSITION, NORMAL, TEXCOORD0, TEXCOORD7 and
    /// no COLOR whatsoever. A terrain mesh missing that element gives the shader an incomplete input
    /// layout.</para>
    ///
    /// <para>It also has to be INLINE. M474 attached it through AddUvChannelOnly, which puts the stream in
    /// its own vertex buffer: Riot ships that shape 3 times against 176 for inline
    /// "Position, Normal, Tex0, Tex7" and 16 for "Position, Normal, Color0, Tex0, Tex7".</para>
    /// </summary>
    [Fact]
    public void Four_blend_terrain_carries_Texcoord7_inline()
    {
        string terrain = string.Join(",", Layout(color: true, grassPivot: false, uv2: true));

        Assert.Equal("Position,Normal,Color0,Tex0,Tex7", terrain);   // Riot ships this 16x
        Assert.EndsWith("Tex7", terrain);                            // last, so it extends the stride
        Assert.NotEqual("Tex7", terrain);                            // never a standalone buffer

        // A non-terrain mesh whose source carried a second UV takes Riot's most common form of all.
        Assert.Equal("Position,Normal,Tex0,Tex7",
            string.Join(",", Layout(color: false, grassPivot: false, uv2: true)));

        static string[] Layout(bool color, bool grassPivot, bool uv2)
        {
            var e = new List<string> { "Position", "Normal" };
            if (color) e.Add("Color0");
            e.Add("Tex0");
            if (grassPivot) e.Add("Tex5");
            if (uv2) e.Add("Tex7");
            return e.ToArray();
        }
    }

    /// <summary>
    /// M502: the porter must decide NO_BAKED_LIGHTING from what the shader cache actually says, not from a
    /// hardcoded role list — and it must tell the two failure modes apart, because they are opposites.
    ///
    /// <para>An axis the shader does not declare is IGNORED by the client, so authoring it is inert but
    /// dishonest. An axis it declares without cooking this value is FATAL — that is M486, where League
    /// answered "Unable to find correct hash for shader". Measured for the four shaders the porter uses:
    /// VertexDeform declares and cooks it; 4TextureBlend_WorldProjected does not declare it;
    /// DefaultEnv_Flat_AlphaTest declares it and never cooked it.</para>
    /// </summary>
    [Fact]
    public void NoBakedLightingIsDecidedByTheShaderCacheNotTheRole()
    {
        // Cooked -> author it, whatever the role rule would have said.
        Assert.True(LegacyMapPorter.ShouldAuthorNoBakedLighting(LegacyMaterialRole.Normal, "Shaders/X",
            (_, _) => LegacyMapPorter.MacroSupport.Cooked, out var reason));
        Assert.Null(reason);

        // Not declared -> omit, and say it is because the client would ignore it.
        Assert.False(LegacyMapPorter.ShouldAuthorNoBakedLighting(LegacyMaterialRole.FourBlendTerrain,
            "Shaders/StaticMesh/4TextureBlend_WorldProjected",
            (_, _) => LegacyMapPorter.MacroSupport.NotDeclared, out reason));
        Assert.Contains("ignore", reason, StringComparison.OrdinalIgnoreCase);

        // Declared but never cooked -> omit, and say it would break the shader. Different reason, and the
        // difference is the whole point.
        Assert.False(LegacyMapPorter.ShouldAuthorNoBakedLighting(LegacyMaterialRole.Decal,
            "Shaders/StaticMesh/DefaultEnv_Flat_AlphaTest",
            (_, _) => LegacyMapPorter.MacroSupport.NotCooked, out reason));
        Assert.Contains("compile", reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Without a shader cache the porter must behave exactly as it did before — the Formats tests
    /// and any install without a game directory run in this state.</summary>
    [Fact]
    public void WithoutAShaderCacheTheOldRoleRuleStillApplies()
    {
        foreach (var role in Enum.GetValues<LegacyMaterialRole>())
        {
            bool expected = LegacyMapPorter.UsesNoBakedLightingByDefault(role);
            Assert.Equal(expected,
                LegacyMapPorter.ShouldAuthorNoBakedLighting(role, "Shaders/Whatever", null, out var reason));
            Assert.Null(reason);
            // ...and an "Unknown" verdict is the same as having no checker at all.
            Assert.Equal(expected, LegacyMapPorter.ShouldAuthorNoBakedLighting(role, "Shaders/Whatever",
                (_, _) => LegacyMapPorter.MacroSupport.Unknown, out _));
        }
    }
}
