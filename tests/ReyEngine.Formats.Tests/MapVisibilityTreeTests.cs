using System.Text.Json;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Interop;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M849: a visibility controller graph is a boolean tree, evaluated the way the client does (LeagueToolkit ADR-0064): ParentMode 0
/// ALL / 1 ANY / 2 ONE / 3 NONE, a child with no parents shows only in 0 and 3, a terrain leaf shows while its mask holds the rift in
/// play (the Base bit does NOT count), a baron-stage leaf while its stage is the pit's, a named controller shows its DefaultVisible.
/// The mesh mask rules of M771 stay as they were for content without a controller. The real-data checks (Riot Map11, the two
/// Old SR V3 packages) ran through a probe; these pin the rules.
/// </summary>
public sealed class MapVisibilityTreeTests
{
    private const uint Primary = 0xc406a533, BaronPit = 0xec733fe2, Child = 0xe21083b5, Named = 0xe07edfa4;
    private const uint FieldParents = 0x3044938a, FieldParentMode = 0xc9d3f06a, FieldPrimaryBits = 0x27639032, FieldBaronBits = 0x8bff8cdf;
    private static uint H(string v) => HashAlgorithms.Fnv1a(v);

    private static readonly MapVisibilityAxis Rift = new(MapVisibility.PrimaryAxisHash, "Map Visibility", 1, true, new[]
    {
        new VisibilityLayer("Base", 1), new VisibilityLayer("Infernal", 2), new VisibilityLayer("Mountain", 4), new VisibilityLayer("Ocean", 8),
    });
    private static readonly MapVisibilityAxis Pit = new(MapVisibility.BaronPitAxisHash, "Baron Pit", 1, false, new[]
    {
        new VisibilityLayer("Base", 1), new VisibilityLayer("Cup", 2), new VisibilityLayer("Tunnel", 4), new VisibilityLayer("Upgraded", 8),
    });
    private static readonly MapVisibilityDefinition Both = new(new[] { Rift, Pit });

    private const int Infernal = 2, Mountain = 4, Ocean = 8, Cup = 2, BasePit = 1;

    private static BinTreeObject Terrain(uint path, int bits) => new(path, Primary, new BinTreeProperty[] { new BinTreeU8(FieldPrimaryBits, (byte)bits) });
    private static BinTreeObject Stage(uint path, int bits) => new(path, BaronPit, new BinTreeProperty[] { new BinTreeU8(FieldBaronBits, (byte)bits) });
    private static BinTreeObject NamedSwitch(uint path, bool? defaultVisible) => new(path, Named,
        defaultVisible is { } v ? new BinTreeProperty[] { new BinTreeBool(H("DefaultVisible"), v) } : Array.Empty<BinTreeProperty>());

    private static BinTreeObject Combined(uint path, uint? mode, params uint[] parents)
    {
        var props = new List<BinTreeProperty>();
        if (parents.Length > 0)   // a bin holds no empty container
            props.Add(new BinTreeContainer(FieldParents, BinPropertyType.ObjectLink, parents.Select(p => (BinTreeProperty)new BinTreeObjectLink(0, p)).ToArray()));
        if (mode is { } m) props.Add(new BinTreeU32(FieldParentMode, m));
        return new BinTreeObject(path, Child, props);
    }

    private static byte[] Bin(params BinTreeObject[] objects)
    {
        using var stream = new MemoryStream();
        new BinTree(objects, Array.Empty<string>()).Write(stream);
        return stream.ToArray();
    }

    private static MapVisibilityControllers Build(params BinTreeObject[] objects) =>
        MapVisibilityControllers.Build(new[] { Bin(objects) }, Both);

    private static IReadOnlyDictionary<uint, int> Sel(int rift, int pit = 0) =>
        new Dictionary<uint, int> { [MapVisibility.PrimaryAxisHash] = rift, [MapVisibility.BaronPitAxisHash] = pit };

    private static bool Shows(MapVisibilityControllers c, uint controller, int rift, int pit = 0, int flags = 0) =>
        new MapVisibilityResolver(c, Both).IsVisible(flags, controller, Sel(rift, pit));

    // ---- ParentMode ------------------------------------------------------------------------------------------------------

    [Theory]
    // mode, rift in play, expected for parents [A(Infernal) , B(Infernal|Mountain), C(Ocean)]
    [InlineData(0u, Infernal, false)]   // ALL: C is off
    [InlineData(0u, Ocean, false)]      // ALL: A is off
    [InlineData(1u, Infernal, true)]    // ANY: A and B on
    [InlineData(1u, Mountain, true)]    // ANY: B on
    [InlineData(1u, Ocean, true)]       // ANY: C on
    [InlineData(2u, Infernal, false)]   // ONE: two on
    [InlineData(2u, Mountain, true)]    // ONE: only B
    [InlineData(2u, Ocean, true)]       // ONE: only C
    [InlineData(3u, Infernal, false)]   // NONE
    [InlineData(3u, Mountain, false)]
    [InlineData(3u, Ocean, false)]
    public void ParentMode_is_ALL_ANY_ONE_NONE_of_the_parents(uint mode, int rift, bool expected)
    {
        var c = Build(Terrain(0x11, Infernal), Terrain(0x12, Infernal | Mountain), Terrain(0x13, Ocean), Combined(0x20, mode, 0x11, 0x12, 0x13));
        Assert.Equal(expected, Shows(c, 0x20, rift));
    }

    [Fact]
    public void NONE_shows_when_no_parent_does_and_ALL_needs_every_parent()
    {
        var c = Build(Terrain(0x11, Infernal), Terrain(0x12, Mountain), Combined(0x20, 3, 0x11, 0x12), Combined(0x21, null, 0x11, 0x12), Combined(0x22, 0, 0x11, 0x11));
        Assert.True(Shows(c, 0x20, Ocean));                 // neither: NONE shows
        Assert.False(Shows(c, 0x20, Infernal));
        Assert.False(Shows(c, 0x21, Infernal));             // no ParentMode = ALL: Mountain is not on
        Assert.False(Shows(c, 0x21, Mountain));
        Assert.True(Shows(c, 0x22, Infernal));              // an explicit 0 is ALL too
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(0u, true)]
    [InlineData(1u, false)]
    [InlineData(2u, false)]
    [InlineData(3u, true)]
    public void A_child_without_parents_shows_only_in_ALL_and_NONE(uint? mode, bool expected)
    {
        var c = Build(Combined(0x30, mode));
        Assert.Equal(expected, Shows(c, 0x30, Infernal));
        Assert.Equal(expected, Shows(c, 0x30, MapVisibility.NoTerraform));
    }

    [Fact]
    public void A_nested_child_in_the_npe_1_form_is_NOR_of_the_rifts_and_something_else()
    {
        // data/maps/mapgeometry/sr/npe_1.materials.bin: [Child(NOR), X] - "before any terraform AND the Cup pit"
        var c = Build(Terrain(0x11, Infernal), Terrain(0x12, Mountain), Terrain(0x13, Ocean), Stage(0x14, Cup),
            Combined(0x40, 3, 0x11, 0x12, 0x13), Combined(0x41, null, 0x40, 0x14));
        Assert.True(Shows(c, 0x41, MapVisibility.NoTerraform, Cup));
        Assert.False(Shows(c, 0x41, MapVisibility.NoTerraform, BasePit));   // not the Cup
        Assert.False(Shows(c, 0x41, Infernal, Cup));                         // a rift is in play
        Assert.False(Shows(c, 0x41, Ocean, Cup));
        // the NOR alone is the "no terraform yet" controller Riot writes
        Assert.True(Shows(c, 0x40, MapVisibility.NoTerraform));
        Assert.True(Shows(c, 0x40, 1));
        Assert.False(Shows(c, 0x40, Mountain));
    }

    // ---- terrain leaves: the Base bit does not count -----------------------------------------------------------------------

    [Fact]
    public void A_terrain_controller_with_the_Base_bit_never_shows_and_so_does_an_ALL_that_needs_it()
    {
        // V3 Winter 16.21.0: 05c79a64 = [Cup, a12ee123 (mask 1), 9f32344a (mask 1)]
        var c = Build(Stage(0x50, Cup), Terrain(0xa12ee123, 1), Terrain(0x9f32344a, 1), Combined(0x05c79a64, null, 0x50, 0xa12ee123, 0x9f32344a));
        foreach (int rift in new[] { MapVisibility.NoTerraform, 1, Infernal, Mountain, Ocean })
        {
            Assert.False(Shows(c, 0xa12ee123, rift));
            Assert.False(Shows(c, 0x05c79a64, rift, Cup));
        }
        // "All" is show-everything: it does not hide the copy (the map check names it)
        Assert.True(Shows(c, 0x05c79a64, 0, 0));
    }

    [Fact]
    public void Before_terraform_is_its_own_choice_apart_from_All_and_picking_Base_means_the_same_state()
    {
        var c = Build(Terrain(0x11, Infernal), Terrain(0x12, 1), Combined(0x20, 3, 0x11));   // NONE of the Infernal rift
        var resolver = new MapVisibilityResolver(c, Both);
        // All: every state is on offer; a rift-only controller shows
        Assert.True(resolver.IsVisible(0, 0x11, Sel(0)));
        // before terraform: no rift in play
        Assert.False(resolver.IsVisible(0, 0x11, Sel(MapVisibility.NoTerraform)));
        Assert.True(resolver.IsVisible(0, 0x20, Sel(MapVisibility.NoTerraform)));
        Assert.False(resolver.IsVisible(0, 0x12, Sel(MapVisibility.NoTerraform)));
        Assert.False(resolver.IsVisible(0, 0x12, Sel(1)));                                    // Base picked = the same state
        var d = resolver.Resolve(0, 0x11, Sel(MapVisibility.NoTerraform));
        Assert.Contains(MapVisibility.NoTerraformLabel, d.FilterSummary);
        Assert.Contains(MapVisibility.NoTerraformLabel, d.Reason);
    }

    [Fact]
    public void The_lenient_switch_brings_back_the_old_reading_Base_bit_counts_and_the_parents_are_a_union()
    {
        var c = Build(Terrain(0x11, Infernal), Terrain(0x12, 1), Combined(0x20, null, 0x11, 0x12));
        Assert.False(c.LenientEvaluation);
        Assert.False(Shows(c, 0x12, MapVisibility.NoTerraform));
        Assert.False(Shows(c, 0x20, Infernal));
        c.LenientEvaluation = true;
        Assert.True(Shows(c, 0x12, 1));                                // the Base bit counted
        Assert.True(Shows(c, 0x12, MapVisibility.NoTerraform));
        Assert.True(Shows(c, 0x20, Infernal));                         // no mode read as a union
        c.LenientEvaluation = false;
        Assert.False(Shows(c, 0x12, MapVisibility.NoTerraform));
    }

    // ---- baron-stage and named leaves ----------------------------------------------------------------------------------------

    [Fact]
    public void A_baron_stage_leaf_shows_in_its_stage_and_the_Base_stage_is_before_the_baron()
    {
        var c = Build(Stage(0x60, BasePit), Stage(0x61, Cup), Combined(0x62, 1, 0x60, 0x61));
        Assert.True(Shows(c, 0x60, MapVisibility.NoTerraform, BasePit));
        Assert.False(Shows(c, 0x60, MapVisibility.NoTerraform, Cup));
        Assert.True(Shows(c, 0x61, Infernal, Cup));
        Assert.False(Shows(c, 0x61, Infernal, 4));
        Assert.True(Shows(c, 0x62, Mountain, Cup));
        Assert.False(Shows(c, 0x62, Mountain, 8));
    }

    [Fact]
    public void A_named_controller_shows_its_DefaultVisible_and_the_class_default_is_true()
    {
        var c = Build(NamedSwitch(0x70, null), NamedSwitch(0x71, true), NamedSwitch(0x72, false), Combined(0x73, null, 0x70, 0x72), Combined(0x74, 1, 0x70, 0x72));
        Assert.True(Shows(c, 0x70, MapVisibility.NoTerraform));
        Assert.True(Shows(c, 0x71, MapVisibility.NoTerraform));
        Assert.False(Shows(c, 0x72, MapVisibility.NoTerraform));
        Assert.False(Shows(c, 0x73, MapVisibility.NoTerraform));
        Assert.True(Shows(c, 0x74, MapVisibility.NoTerraform));
        Assert.Equal("Named", c.List().Single(i => i.Hash == 0x72).Kind);
    }

    [Fact]
    public void An_unknown_class_in_a_graph_counts_as_visible_as_before_and_the_check_names_it()
    {
        var other = new BinTreeObject(0x80, 0x6b863734, Array.Empty<BinTreeProperty>());
        var c = Build(other, Terrain(0x11, Infernal), Combined(0x81, null, 0x80, 0x11), Combined(0x82, null, 0x99999999, 0x11));
        Assert.True(Shows(c, 0x81, Infernal));
        Assert.False(Shows(c, 0x81, Ocean));
        Assert.True(Shows(c, 0x82, Infernal));

        var findings = MapVisibilityDiagnostics.Analyze(c, new uint[] { 0x81, 0x82 });
        var unknown = findings.Where(f => f.Kind == VisibilityFindingKind.UnknownClass).ToList();
        Assert.Equal(2, unknown.Count);
        Assert.Contains(unknown, f => f.ControllerHash == 0x80 && f.Message.Contains("0x6b863734"));
        Assert.Contains(unknown, f => f.ControllerHash == 0x99999999 && f.Message.Contains("none of the map's bins"));
    }

    [Fact]
    public void A_cycle_is_cut_and_reported()
    {
        var c = Build(Combined(0x90, 1, 0x91), Combined(0x91, 1, 0x90, 0x11), Terrain(0x11, Infernal));
        Assert.True(Shows(c, 0x90, Infernal));    // finishes; the repeated node counts as visible
        Assert.Contains(MapVisibilityDiagnostics.Analyze(c, new uint[] { 0x90 }), f => f.Kind == VisibilityFindingKind.Cycle);
    }

    // ---- what stays: mesh masks, board stages, other axes ----------------------------------------------------------------------

    [Theory]
    [InlineData(255, true)]    // all layers
    [InlineData(1, true)]      // Base alone
    [InlineData(25, true)]     // Base, Ocean, Cloud: Base still means "also before the terraform"
    [InlineData(2, false)]     // Infernal only
    [InlineData(8, false)]
    public void A_mesh_without_a_controller_keeps_the_M771_mask_rule_before_terraform(int flags, bool expected)
    {
        var resolver = new MapVisibilityResolver(Build(), Both);
        Assert.Equal(expected, resolver.IsVisible(flags, 0, Sel(MapVisibility.NoTerraform)));
        Assert.Equal(expected, resolver.IsVisible(flags, 0, Sel(1)));
        Assert.True(resolver.IsVisible(flags, 0, Sel(0)));
    }

    [Fact]
    public void A_controller_that_reaches_only_the_pit_leaves_the_primary_axis_to_the_mesh_mask()
    {
        var c = Build(Stage(0x61, Cup));
        var resolver = new MapVisibilityResolver(c, Both);
        Assert.True(resolver.IsVisible(255, 0x61, Sel(Infernal, Cup)));
        Assert.False(resolver.IsVisible(4, 0x61, Sel(Infernal, Cup)));      // the mesh mask is Mountain-only: still decides
        Assert.False(resolver.IsVisible(255, 0x61, Sel(Infernal, 4)));      // the controller says not this pit
    }

    [Fact]
    public void A_board_stage_mask_is_the_whole_primary_state_for_a_controller()
    {
        var c = Build(Terrain(0x11, 1), Terrain(0x12, 4));
        var resolver = new MapVisibilityResolver(c, Both);
        Assert.False(resolver.IsVisible(0, 0x11, Sel(0), stageMask: 4));    // no initial mask folded in
        Assert.True(resolver.IsVisible(0, 0x12, Sel(0), stageMask: 4));
    }

    [Fact]
    public void All_on_one_axis_stands_for_every_state_of_it()
    {
        var c = Build(Terrain(0x11, Infernal), Stage(0x61, Cup), Combined(0x20, null, 0x11, 0x61));
        var resolver = new MapVisibilityResolver(c, Both);
        Assert.True(resolver.IsVisible(0, 0x20, Sel(Infernal, 0)));          // some pit shows it
        Assert.True(resolver.IsVisible(0, 0x20, Sel(0, Cup)));               // some rift shows it
        Assert.False(resolver.IsVisible(0, 0x20, Sel(Mountain, 0)));         // no pit helps: the rift does not
        Assert.False(resolver.IsVisible(0, 0x20, Sel(0, BasePit)));
        Assert.True(resolver.IsVisible(0, 0x20, Sel(0, 0)));                 // everything on All shows everything
    }

    // ---- the map check ------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_map_check_names_never_visible_graphs_Base_bit_terrain_wrong_Riot_bits_and_ALLs_of_many_parents()
    {
        var c = Build(Stage(0x50, Cup), Terrain(0xa12ee123, 1), Terrain(0x9f32344a, 1), Terrain(0x11, Infernal),
            Combined(0x05c79a64, null, 0x50, 0xa12ee123, 0x9f32344a), Combined(0x20, null, 0x50, 0x11));
        var riot = Build(Terrain(0x9f32344a, Infernal));

        var findings = MapVisibilityDiagnostics.Analyze(c, new uint[] { 0x05c79a64, 0x05c79a64, 0x20 }, riot);

        var never = Assert.Single(findings, f => f.Kind == VisibilityFindingKind.NeverVisible);
        Assert.Equal(0x05c79a64u, never.ControllerHash);
        Assert.Equal(2, never.Users);
        Assert.Equal(VisibilityFindingSeverity.Error, never.Severity);
        Assert.Equal(new uint[] { 0x9f32344a, 0xa12ee123 }, findings.Where(f => f.Kind == VisibilityFindingKind.BaseBitTerrain).Select(f => f.ControllerHash).OrderBy(h => h));
        var wrong = Assert.Single(findings, f => f.Kind == VisibilityFindingKind.NonRiotBits);
        Assert.Equal(0x9f32344au, wrong.ControllerHash);
        Assert.Contains("2 in Riot's bin", wrong.Message);
        Assert.Equal(0x05c79a64u, Assert.Single(findings, f => f.Kind == VisibilityFindingKind.AllWithManyParents).ControllerHash);
        Assert.DoesNotContain(findings, f => f.ControllerHash == 0x20);      // the two-parent ALL on a real rift is fine
    }

    [Fact]
    public void The_map_check_has_nothing_to_say_about_riot_shaped_graphs()
    {
        var c = Build(Terrain(0x11, Infernal), Terrain(0x12, Mountain), Stage(0x61, Cup), Stage(0x62, BasePit),
            Combined(0x40, 3, 0x11, 0x12), Combined(0x41, null, 0x40, 0x61), Combined(0x42, 1, 0x62, 0x61));
        Assert.Empty(MapVisibilityDiagnostics.Analyze(c, new uint[] { 0x40, 0x41, 0x42, 0x11 }));
    }

    // ---- Blender ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Blender_can_send_before_terraform_for_the_primary_axis_only()
    {
        using var ok = JsonDocument.Parse("{\"selection\":{\"axes\":{\"0x9e019715\":-1,\"0xd31ac6ce\":2}}}");
        Assert.True(BlenderLinkVisibility.TryParseSelection(ok.RootElement, Both, Array.Empty<string>(), null, out var selection, out _, out _));
        Assert.Equal(MapVisibility.NoTerraform, selection.Axes[MapVisibility.PrimaryAxisHash]);

        using var bad = JsonDocument.Parse("{\"selection\":{\"axes\":{\"0xd31ac6ce\":-1}}}");
        Assert.False(BlenderLinkVisibility.TryParseSelection(bad.RootElement, Both, Array.Empty<string>(), null, out _, out _, out string? error));
        Assert.Contains("no layer with bit -1", error);

        var model = BlenderLinkVisibility.Describe(Both, Array.Empty<string>(), false, null, 0, selection);
        var options = ((IEnumerable<Dictionary<string, object?>>)((IEnumerable<Dictionary<string, object?>>)model["axes"]!).First()["options"]!).ToList();
        Assert.Equal(MapVisibility.NoTerraformLabel, options[^1]["label"]);
        Assert.Equal(MapVisibility.NoTerraform, options[^1]["bit"]);
    }

    [Fact]
    public void The_shared_evaluator_draws_the_tree_for_the_editor_and_for_Blender()
    {
        // one mesh per controller; the evaluator is the function both use
        var c = Build(Terrain(0x11, Infernal), Stage(0x61, Cup), Combined(0x20, null, 0x11, 0x61), Combined(0x21, 3, 0x11));
        var resolver = new MapVisibilityResolver(c, Both);
        var sel = new VisibilitySelection(Sel(Infernal, Cup), new HashSet<string>(), null, true);
        Assert.True(MapVisibilityEvaluator.Visible(resolver, 0, 0x20, 0, sel));
        Assert.False(MapVisibilityEvaluator.Visible(resolver, 0, 0x21, 0, sel));
        var before = sel with { Axes = Sel(MapVisibility.NoTerraform, Cup) };
        Assert.False(MapVisibilityEvaluator.Visible(resolver, 0, 0x20, 0, before));
        Assert.True(MapVisibilityEvaluator.Visible(resolver, 0, 0x21, 0, before));
    }
}
