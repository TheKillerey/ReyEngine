using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Lighting;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M901: a per-light strength in the Object inspector. A map's own point lights (MapDynamicPointLight in materials.bin) write
/// their intensityScale; a spotlight scales its Color (the game's spot shader reads nothing else). The real-bin facts come from
/// Project Rift V2's test46 materials.bin (168 point lights, 91 spotlights), read only - a missing file skips those facts.
/// </summary>
public sealed class LightStrengthTests
{
    private const string RealBin = @"D:\ProjectReyMaps\_ModUpdate\ProjectRiftV2\work\claude\variants\m25_nz.bin";
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static byte[]? Real() => File.Exists(RealBin) ? File.ReadAllBytes(RealBin) : null;

    private static byte[] SyntheticBin()
    {
        var items = new List<KeyValuePair<BinTreeProperty, BinTreeProperty>>();
        for (int i = 0; i < 3; i++)
        {
            var m = Matrix4x4.Identity; m.M41 = 100 * (i + 1); m.M42 = 50; m.M43 = 7;
            items.Add(new(new BinTreeHash(0, 0x3000u + (uint)i), new BinTreeStruct(0, H("MapDynamicPointLight"), new BinTreeProperty[]
            {
                new BinTreeMatrix44(H("transform"), m), new BinTreeString(H("name"), "L" + i),
                new BinTreeF32(H("intensityScale"), 1f + i), new BinTreeF32(H("radius"), 400f + i * 10),
                new BinTreeVector4(H("lightColor"), new Vector4(1, 0.5f, 0.25f, 1)),
                new BinTreeU8(H("mVisibilityFlags"), 255),   // a field the editor does not model
            })));
        }
        var map = new BinTreeMap(H("items"), BinPropertyType.Hash, BinPropertyType.Struct, items);
        var obj = new BinTreeObject(7u, H("MapPlaceableContainer"), new BinTreeProperty[] { map });
        var ms = new MemoryStream();
        new BinTree(new[] { obj }, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    /// <summary>The panel after a map open: one PointLightViewModel per placed light, as ImportPlacedLightsIntoPanel builds them.</summary>
    private static MainWindowViewModel PanelFrom(byte[] bin, out IReadOnlyList<PlacedPointLight> placed)
    {
        var vm = new MainWindowViewModel();
        placed = DynamicPointLights.ReadPlacements(bin);
        foreach (var p in placed)
            vm.EditableLights.Add(new PointLightViewModel(
                new PointLight(p.Light.Position, p.Light.Color, p.Light.Radius, p.Light.IntensityScale), vm) { Name = p.Light.Name });
        return vm;
    }

    private static Dictionary<(uint, uint), BinTreeStruct> Entries(byte[] bin)
    {
        var tree = SafeBinTree.Parse(bin);
        var d = new Dictionary<(uint, uint), BinTreeStruct>();
        foreach (var o in tree.Objects.Values)
            if (o.Properties.TryGetValue(H("items"), out var ip) && ip is BinTreeMap map)
                foreach (var kv in map)
                    if (kv.Key is BinTreeHash h && kv.Value is BinTreeStruct s) d[(o.PathHash, h.Value)] = s;
        return d;
    }

    // ------------------------------------------------------------------------------------------- the format layer

    [Fact]
    public void ReadPlacements_gives_each_light_its_container_and_item_key()
    {
        var found = DynamicPointLights.ReadPlacements(SyntheticBin());
        Assert.Equal(3, found.Count);
        Assert.Equal(new MapPlacementId(7u, 0x3001), found[1].Id);
        Assert.Equal(2f, found[1].Light.IntensityScale);
        Assert.Equal(DynamicPointLights.Read(SyntheticBin()), found.Select(f => f.Light).ToList());   // same lights as Read
    }

    [Fact]
    public void A_strength_edit_rewrites_intensityScale_only_and_leaves_every_other_byte_of_the_entry_alone()
    {
        byte[] bin = SyntheticBin();
        var target = DynamicPointLights.ReadPlacements(bin)[1];
        var saved = MapPlaceableWriter.WriteEdits(bin, new[] { new MapPlacementEdit(target.Id) { PointLightIntensity = 9.5f } }, out var err);
        Assert.True(saved is not null, err);

        var before = Entries(bin); var after = Entries(saved!);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var key in before.Keys)
        {
            if (key == (target.Id.ContainerHash, target.Id.ItemKey))
            {
                Assert.Equal(9.5f, ((BinTreeF32)after[key].Properties[H("intensityScale")]).Value);
                foreach (var (field, prop) in before[key].Properties.Where(p => p.Key != H("intensityScale")))
                    Assert.True(BinPropEquality.PropsEqual(prop, after[key].Properties[field]), $"field 0x{field:x8} changed");
                Assert.Equal(before[key].Properties.Count, after[key].Properties.Count);   // the unmodelled mVisibilityFlags survives
            }
            else Assert.True(BinPropEquality.PropsEqual(before[key], after[key]), "an untouched light changed");
        }
    }

    [Fact]
    public void A_strength_edit_is_refused_on_anything_but_a_point_light()
    {
        var bin = SyntheticBin();
        var tree = SafeBinTree.Parse(bin);
        // retarget the class of one entry to a spotlight and the light-only verb must not apply
        var items = (BinTreeMap)tree.Objects[7u].Properties[H("items")];
        var rebuilt = items.Select(kv => kv.Key is BinTreeHash h && h.Value == 0x3000u
            ? new KeyValuePair<BinTreeProperty, BinTreeProperty>(kv.Key, new BinTreeStruct(0, H("MapDynamicSpotlight"), ((BinTreeStruct)kv.Value).Properties.Values.ToList()))
            : new KeyValuePair<BinTreeProperty, BinTreeProperty>(kv.Key, kv.Value));
        tree.Objects[7u].Properties[H("items")] = new BinTreeMap(H("items"), BinPropertyType.Hash, BinPropertyType.Struct, rebuilt);
        var ms = new MemoryStream(); tree.Write(ms);
        var saved = MapPlaceableWriter.WriteEdits(ms.ToArray(), new[] { new MapPlacementEdit(new MapPlacementId(7u, 0x3000)) { PointLightIntensity = 3f } }, out var err);
        Assert.Null(saved);
        Assert.NotNull(err);
    }

    // ------------------------------------------------------------------------------------------- the view model: point light

    [Fact]
    public void A_placed_light_is_linked_and_a_strength_change_is_an_edit_that_clears_when_put_back()
    {
        byte[] bin = SyntheticBin();
        var vm = PanelFrom(bin, out var placed);
        Assert.Equal(3, vm.LinkPlacedLights(placed));
        Assert.All(vm.EditableLights, l => Assert.True(l.IsPlacedInMap));
        Assert.False(vm.MapContent.HasPlacementEdits);               // opening writes nothing
        Assert.False(vm.HasParticleMoves);

        var light = vm.EditableLights[2];
        vm.SelectedOutlinerItem = light;
        Assert.Same(light, vm.SelectedLight);
        light.Intensity = 12.5;                                      // what the slider or the box sets
        Assert.True(light.StrengthEdited);
        Assert.True(vm.MapContent.HasPlacementEdits);
        Assert.True(vm.HasParticleMoves);                            // Save to Mod is offered
        Assert.Equal(12.5f, vm.DynamicLights!.Single(l => l.Position.X == 300).Intensity);   // live in the viewport's light list
        Assert.Equal(1, vm.EditableLights.Count(l => l.StrengthEdited));

        light.Intensity = 3;                                         // back to authored (2 + 1)
        Assert.False(light.StrengthEdited);
        Assert.False(vm.HasParticleMoves);
    }

    [Fact]
    public void Linking_survives_a_restored_panel_whose_colours_were_rounded_and_whose_lights_were_reordered()
    {
        byte[] bin = SyntheticBin();
        var placed = DynamicPointLights.ReadPlacements(bin);
        var vm = new MainWindowViewModel();
        // what RestoreMapLighting builds: FromStored of the project record, here in reverse order
        foreach (var p in placed.Reverse())
            vm.EditableLights.Add(new PointLightViewModel(PointLightViewModel.FromStored(
                p.Light.Position.X, p.Light.Position.Y, p.Light.Position.Z, 255, 128, 64, p.Light.Radius, p.Light.IntensityScale), vm)
            { Name = p.Light.Name });
        Assert.Equal(3, vm.LinkPlacedLights(placed));
        foreach (var l in vm.EditableLights)
            Assert.Equal(placed.Single(p => p.Light.Name == l.Name).Id, l.PlacementId);
    }

    [Fact]
    public void A_light_dat_light_is_not_placed_and_edits_nothing_in_the_bin()
    {
        var vm = PanelFrom(SyntheticBin(), out var placed);
        var dat = new PointLightViewModel(new PointLight(new Vector3(9000, 0, 0), Vector3.One, 500f), vm) { Name = "dat" };
        vm.EditableLights.Add(dat);
        vm.LinkPlacedLights(placed);
        Assert.False(dat.IsPlacedInMap);
        dat.Intensity = 4;
        Assert.False(dat.StrengthEdited);
        Assert.False(vm.MapContent.HasPlacementEdits);
        Assert.Contains("Light.dat", dat.StrengthNote);
    }

    [Fact]
    public void The_slider_never_clamps_a_light_that_is_authored_brighter_than_its_range()
    {
        var vm = new MainWindowViewModel();
        var light = new PointLightViewModel(new PointLight(Vector3.Zero, Vector3.One, 500f, 1000f), vm);
        vm.EditableLights.Add(light);
        light.LinkToPlacement(new MapPlacementId(1, 1), 1000f);
        Assert.True(light.StrengthSliderMax >= 1000);
        Assert.Equal(1000, light.StrengthSlider);
        Assert.Equal(1000, light.Intensity);                         // reading the slider's value did not change the light
        light.Intensity = 100000;                                    // a typed value beyond the slider is kept
        Assert.Equal(100000, light.Intensity);
        Assert.Equal(light.StrengthSliderMax, light.StrengthSlider);
        Assert.Equal(100000, light.Intensity);
    }

    // ------------------------------------------------------------------------------------------- the view model: spotlight

    private static SpotlightViewModel Spot(Vector4 color) => SpotlightViewModel.Create(new DynamicSpotlight(
        new MapPlacementId(1, 2), "S", Matrix4x4.Identity, color, 60f, 800f, 10f, 1f, 0, "", 0, 255, false), false, null);

    [Fact]
    public void Spotlight_strength_is_the_largest_component_and_scaling_it_keeps_the_hue()
    {
        var s = Spot(new Vector4(1.447f, 0.7235f, 0.36175f, 1f));
        Assert.Equal(1.447, s.Strength, 4);
        Assert.False(s.HasEdits);

        s.Strength = 1.447;                                          // the same value: not an edit
        Assert.False(s.HasEdits);

        s.Strength = 4;
        Assert.Equal(4, s.Strength, 6);
        Assert.Equal(2, s.ColorG, 4); Assert.Equal(1, s.ColorB, 3);  // ratios 1 : 0.5 : 0.25 kept
        Assert.Equal(1f, s.Current.Color.W);
        Assert.True(s.HasEdits);

        s.Strength = 1.447;
        Assert.Equal(0.7235, s.ColorG, 4);
    }

    [Fact]
    public void A_spotlight_dragged_to_black_and_back_returns_in_its_own_colour_and_a_black_one_starts_white()
    {
        var s = Spot(new Vector4(1f, 0.5f, 0.25f, 1f));
        s.Strength = 0;
        Assert.Equal(0, s.ColorR); Assert.Equal(0, s.ColorG); Assert.Equal(0, s.ColorB);
        s.Strength = 2;
        Assert.Equal(2, s.ColorR, 6); Assert.Equal(1, s.ColorG, 6); Assert.Equal(0.5, s.ColorB, 6);

        var black = Spot(new Vector4(0, 0, 0, 1));
        black.Strength = 3;
        Assert.Equal(3, black.ColorR); Assert.Equal(3, black.ColorG); Assert.Equal(3, black.ColorB);

        var neg = Spot(Vector4.One);
        neg.Strength = -5;                                           // a negative clamps to zero
        Assert.Equal(0, neg.Strength);
    }

    [Fact]
    public void A_spotlight_strength_edit_reaches_the_renderer_and_saves_only_its_colour()
    {
        var vm = new MainWindowViewModel();
        var spot = new DynamicSpotlight(default, "S", new Matrix4x4(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 400, 0, 1),
            new Vector4(1f, 0.5f, 0.25f, 1f), 60f, 800f, 10f, 1f, 0, "", 0, 255, false);
        vm.MapContent.SetSpotlights(new[] { spot });
        vm.RepublishSpotlights();
        var before = vm.Dx11SpotLights![0].Color;

        vm.MapContent.Spotlights[0].Strength = 3;
        Assert.NotEqual(before, vm.Dx11SpotLights![0].Color);        // republished with the new colour
        Assert.Equal(before.X * 3, vm.Dx11SpotLights![0].Color.X, 4);
        Assert.Equal(before.Y * 3, vm.Dx11SpotLights![0].Color.Y, 4);

        var e = DynamicSpotlights.EditFor(spot, vm.MapContent.Spotlights[0].Current);
        Assert.NotNull(e);
        Assert.NotNull(e!.SpotColor);
        Assert.Null(e.Transform); Assert.Null(e.SpotFov); Assert.Null(e.SpotFar); Assert.Null(e.Name);
    }

    // ------------------------------------------------------------------------------------------- real Project Rift V2 test46 bin

    [Fact]
    public void Real_t46_bin_a_point_light_strength_save_changes_only_that_entry()
    {
        var bin = Real(); if (bin is null) return;
        var placed = DynamicPointLights.ReadPlacements(bin);
        Assert.Equal(168, placed.Count);
        Assert.Equal(91, DynamicSpotlights.Read(bin).Count);
        Assert.Equal(DynamicPointLights.Read(bin).Count, placed.Count);   // the id-bearing read sees the same 168

        var vm = PanelFrom(bin, out _);
        Assert.Equal(168, vm.LinkPlacedLights(placed));
        Assert.False(vm.MapContent.HasPlacementEdits);               // an unchanged map: nothing to save

        var pick = vm.EditableLights[57];
        double authored = pick.Intensity;
        pick.Intensity = authored + 2.5;
        var edited = vm.EditableLights.Where(l => l.StrengthEdited).ToList();
        Assert.Single(edited);
        var saved = MapPlaceableWriter.WriteEdits(bin,
            edited.Select(l => new MapPlacementEdit(l.PlacementId) { PointLightIntensity = l.EffectiveIntensity }).ToList(), out var err);
        Assert.True(saved is not null, err);

        var before = Entries(bin); var after = Entries(saved!);
        Assert.Equal(before.Count, after.Count);
        var key = (pick.PlacementId.ContainerHash, pick.PlacementId.ItemKey);
        Assert.Equal((float)(authored + 2.5), ((BinTreeF32)after[key].Properties[H("intensityScale")]).Value);
        foreach (var k in before.Keys.Where(k => k != key))
            Assert.True(BinPropEquality.PropsEqual(before[k], after[k]), $"entry 0x{k.Item2:x8} changed");
        foreach (var (field, prop) in before[key].Properties.Where(p => p.Key != H("intensityScale")))
            Assert.True(BinPropEquality.PropsEqual(prop, after[key].Properties[field]), $"field 0x{field:x8} of the edited light changed");

        // saved bin round-trips and the other 167 lights still read identically
        var back = DynamicPointLights.Read(saved!);
        Assert.Equal(168, back.Count);
        Assert.Equal(91, DynamicSpotlights.Read(saved!).Count);
        static List<DynamicSpotlight> ById(byte[] b) => DynamicSpotlights.Read(b).OrderBy(x => x.Id.ContainerHash).ThenBy(x => x.Id.ItemKey).ToList();
        Assert.Equal(ById(bin), ById(saved!));                       // (the rewrite may reorder the map; the content is identical)

        // after the save the bin IS the authored state
        pick.CommitStrength();
        Assert.False(vm.MapContent.HasPlacementEdits);
    }

    [Fact]
    public void Real_t46_bin_a_spotlight_strength_save_changes_only_that_spotlights_colour()
    {
        var bin = Real(); if (bin is null) return;
        var vm = new MainWindowViewModel();
        vm.MapContent.SetSpotlights(DynamicSpotlights.Read(bin));
        var pick = vm.MapContent.Spotlights[12];
        var authored = pick.Spot;
        Assert.False(vm.MapContent.HasPlacementEdits);

        pick.Strength = pick.Strength * 2 + 0.5;
        var edit = DynamicSpotlights.EditFor(authored, pick.Current);
        Assert.NotNull(edit);
        var saved = MapPlaceableWriter.WriteEdits(bin, new[] { edit! }, out var err);
        Assert.True(saved is not null, err);

        var before = Entries(bin); var after = Entries(saved!);
        var key = (authored.Id.ContainerHash, authored.Id.ItemKey);
        foreach (var k in before.Keys.Where(k => k != key))
            Assert.True(BinPropEquality.PropsEqual(before[k], after[k]), $"entry 0x{k.Item2:x8} changed");
        foreach (var (field, prop) in before[key].Properties.Where(p => p.Key != H("Color")))
            Assert.True(BinPropEquality.PropsEqual(prop, after[key].Properties[field]), $"field 0x{field:x8} changed");
        var oldColor = authored.Color; var newColor = ((BinTreeVector4)after[key].Properties[H("Color")]).Value;
        Assert.Equal(pick.Strength, Math.Max(newColor.X, Math.Max(newColor.Y, newColor.Z)), 4);
        Assert.Equal(oldColor.W, newColor.W);
        if (oldColor.X > 0 && oldColor.Y > 0)                        // the hue is unchanged
            Assert.Equal(oldColor.X / oldColor.Y, newColor.X / newColor.Y, 3);
    }

    [Fact]
    public void Real_t46_bin_an_unchanged_save_writes_nothing_and_the_bytes_are_untouched()
    {
        var bin = Real(); if (bin is null) return;
        var vm = PanelFrom(bin, out var placed);
        vm.LinkPlacedLights(placed);
        vm.MapContent.SetSpotlights(DynamicSpotlights.Read(bin));
        foreach (var s in vm.MapContent.Spotlights) { s.Strength = s.Strength; }   // touched with their own value
        // the same selection of edits SaveParticleMoves builds: none
        Assert.Empty(vm.EditableLights.Where(l => l.StrengthEdited));
        Assert.Empty(vm.MapContent.Spotlights.Where(s => s.HasEdits));
        Assert.False(vm.MapContent.HasPlacementEdits);
        Assert.Same(bin, MapPlaceableWriter.WriteEdits(bin, Array.Empty<MapPlacementEdit>(), out _));   // and no edits means the very same bytes
    }

    [Fact]
    public void Real_t46_bin_light_strengths_for_the_slider_range()
    {
        var bin = Real(); if (bin is null) return;
        var pl = DynamicPointLights.Read(bin);
        var sp = DynamicSpotlights.Read(bin);
        string note = $"points={pl.Count} intensity min={pl.Min(l => l.IntensityScale)} max={pl.Max(l => l.IntensityScale)} "
                    + $"distinct={pl.Select(l => l.IntensityScale).Distinct().Count()} | radius min={pl.Min(l => l.Radius)} max={pl.Max(l => l.Radius)} "
                    + $"| spots={sp.Count} colourMax min={sp.Min(s => Math.Max(s.Color.X, Math.Max(s.Color.Y, s.Color.Z)))} "
                    + $"max={sp.Max(s => Math.Max(s.Color.X, Math.Max(s.Color.Y, s.Color.Z)))}";
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "m901_t46_strengths.txt"), note);
        Assert.True(pl.All(l => float.IsFinite(l.IntensityScale)));
    }

    // ------------------------------------------------------------------------------------------- XAML bindings are runtime

    [Fact]
    public void The_inspector_binds_only_properties_that_exist_and_uses_no_literal_colours_in_the_new_rows()
    {
        Assert.NotNull(typeof(PointLightViewModel).GetProperty(nameof(PointLightViewModel.StrengthSlider)));
        Assert.NotNull(typeof(PointLightViewModel).GetProperty(nameof(PointLightViewModel.StrengthSliderMax)));
        Assert.NotNull(typeof(PointLightViewModel).GetProperty(nameof(PointLightViewModel.StrengthNote)));
        Assert.NotNull(typeof(PointLightViewModel).GetProperty(nameof(PointLightViewModel.Intensity)));
        Assert.NotNull(typeof(SpotlightViewModel).GetProperty(nameof(SpotlightViewModel.Strength)));
        Assert.NotNull(typeof(SpotlightViewModel).GetProperty(nameof(SpotlightViewModel.StrengthSlider)));

        string? text = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx")))
            { var p = Path.Combine(dir.FullName, "src", "ReyEngine.App", "Views", "SceneObjectInspectorView.axaml"); if (File.Exists(p)) text = File.ReadAllText(p); break; }
        if (text is null) return;
        Assert.Contains("SelectedLight.StrengthSlider", text);
        Assert.Contains("SelectedLight.StrengthSliderMax", text);
        Assert.Contains("SelectedLight.StrengthNote", text);
        Assert.Contains("SelectedSpotlight.Strength}", text);
        Assert.Contains("SelectedSpotlight.StrengthSlider", text);
    }
}

