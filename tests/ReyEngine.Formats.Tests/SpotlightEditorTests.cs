using System.Numerics;
using System.Reflection;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Lighting;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M900: the spotlight as an editor object - outliner selection, gizmo move / turn and undo, the edit model, the save edits and
/// the viewport data (icon, frustum, packed light). View-model level; the real window and the D3D11 draw are the device tests'.
/// </summary>
public sealed class SpotlightEditorTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static DynamicSpotlight Spot(string name, Vector3 at, MapPlacementId id = default) => new(
        id, name, new Matrix4x4(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, at.X, at.Y, at.Z, 1),
        new Vector4(1f, 0.5f, 0.25f, 1f), 60f, 800f, 10f, 1f, 0, "", 0, 255, false);

    private static byte[] BinWith(params (string Name, Vector3 At)[] spots)
    {
        var items = new List<KeyValuePair<BinTreeProperty, BinTreeProperty>>();
        uint key = 0x2000;
        foreach (var (name, at) in spots)
        {
            var m = new Matrix4x4(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, at.X, at.Y, at.Z, 1);
            items.Add(new(new BinTreeHash(0, key++), new BinTreeStruct(0, H("MapDynamicSpotlight"), new BinTreeProperty[]
            {
                new BinTreeMatrix44(H("transform"), m), new BinTreeString(H("name"), name),
                new BinTreeF32(H("far"), 800f), new BinTreeVector4(H("Color"), new Vector4(1, 0.5f, 0.25f, 1)),
            })));
        }
        var map = new BinTreeMap(H("items"), BinPropertyType.Hash, BinPropertyType.Struct, items);
        var obj = new BinTreeObject(7u, H("MapPlaceableContainer"), new BinTreeProperty[] { map });
        var ms = new MemoryStream();
        new BinTree(new[] { obj }, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    private static T Call<T>(MainWindowViewModel vm, string method, params object[] args) =>
        (T)typeof(MainWindowViewModel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .Invoke(vm, args)!;

    private static void CallVoid(MainWindowViewModel vm, string method, params object[] args) =>
        typeof(MainWindowViewModel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .Invoke(vm, args);

    // ----------------------------------------------------------------------------------------------- the edit model

    [Fact]
    public void An_untouched_spotlight_has_no_edits_and_each_field_edit_is_one()
    {
        var content = new MapContentViewModel();
        content.SetSpotlights(new[] { Spot("S", new Vector3(10, 500, 20), new MapPlacementId(1, 2)) });
        var s = content.Spotlights.Single();
        Assert.False(s.HasEdits); Assert.False(content.HasPlacementEdits);

        s.Far = 1200; Assert.True(s.HasEdits); Assert.True(content.HasPlacementEdits);
        Assert.Equal(1200f, s.Current.Far);
        s.Far = 800; Assert.False(s.HasEdits);                         // back to authored: nothing to save

        s.Offset = new Vector3(5, 0, 0);
        Assert.True(s.IsMoved);
        Assert.Equal(new Vector3(15, 500, 20), s.CurrentTransform.Translation);
        s.ResetEdits();
        Assert.False(s.HasEdits); Assert.Equal(new Vector3(10, 500, 20), s.Position);
    }

    [Fact]
    public void A_turn_composes_into_the_authored_transform_and_keeps_the_position()
    {
        var s = SpotlightViewModel.Create(Spot("S", new Vector3(10, 500, 20)), false, null);
        var before = DynamicSpotlights.ForwardOf(s.CurrentTransform);
        Assert.Equal(-Vector3.UnitY, before);                           // straight down
        s.RotationDegrees = new Vector3(0, 0, 90);                      // roll about the local axis it was built on
        var after = DynamicSpotlights.ForwardOf(s.CurrentTransform);
        Assert.Equal(new Vector3(10, 500, 20), s.CurrentTransform.Translation);
        Assert.True(s.HasEdits);
        Assert.True(Vector3.Distance(before, after) > 0.5f || s.CurrentTransform != s.Spot.Transform);
    }

    [Fact]
    public void A_disabled_or_deleted_spotlight_is_not_published_to_the_renderer_or_the_icons()
    {
        var vm = new MainWindowViewModel();
        vm.MapContent.SetSpotlights(new[] { Spot("A", new Vector3(0, 400, 0), new MapPlacementId(1, 1)), Spot("B", new Vector3(500, 400, 0), new MapPlacementId(1, 2)) });
        vm.RepublishSpotlights();
        Assert.Equal(2, vm.Dx11SpotLights!.Count);
        Assert.Equal(2, vm.SpotlightMarkers!.Count);

        vm.MapContent.Spotlights[0].EditedVisibilityFlags = 0;          // Disable: writes a zero mask, the light goes out
        Assert.Single(vm.Dx11SpotLights!);
        vm.MapContent.Spotlights[1].IsRemoved = true;                   // pending delete
        Assert.Null(vm.Dx11SpotLights);
        Assert.Empty(vm.SpotlightMarkers ?? Array.Empty<Vector3>());
    }

    // ----------------------------------------------------------------------------------------------- selection and gizmo

    [Fact]
    public void Selecting_a_spotlight_makes_it_the_gizmo_target_and_focus_point_and_others_deselect_it()
    {
        var vm = new MainWindowViewModel();
        vm.MapContent.SetSpotlights(new[] { Spot("A", new Vector3(100, 400, 300)) });
        var s = vm.MapContent.Spotlights[0];

        vm.SelectedOutlinerItem = s;
        Assert.Same(s, vm.SelectedSpotlight);
        Assert.True(vm.HasPlacementGizmoTarget);
        Assert.Equal(new Vector3(100, 400, 300), vm.GizmoPivot);
        Assert.Equal(new Vector3(100, 400, 300), vm.SelectedFocusPoint());
        Assert.NotNull(vm.SpotlightFrustumLines);                       // the wireframe is drawn while selected
        Assert.Equal(13 * 6, vm.SpotlightFrustumLines!.Length);
        Assert.Same(vm.SpotlightFrustumLines, vm.Dx11LightRangeLines()); // and rides the D3D11 line buffer

        var other = new PointLightViewModel(new PointLight(Vector3.Zero, Vector3.One, 500f), vm);
        vm.EditableLights.Add(other);
        vm.SelectedOutlinerItem = other;
        Assert.Null(vm.SelectedSpotlight);
        Assert.Null(vm.SpotlightFrustumLines);
    }

    [Fact]
    public void The_gizmo_moves_and_turns_a_spotlight_and_the_drag_is_one_undo_step()
    {
        var vm = new MainWindowViewModel();
        vm.MapContent.SetSpotlights(new[] { Spot("A", new Vector3(100, 400, 300)) });
        var s = vm.MapContent.Spotlights[0];
        vm.SelectedOutlinerItem = s;

        vm.BeginPlacementDrag();
        vm.DragSelectedPlacementTo(new Vector3(50, 0, -20));
        vm.RotateSelectedPlacementTo(new Vector3(30, 0, 0));
        vm.EndPlacementDrag();

        Assert.Equal(new Vector3(150, 400, 280), s.Position);
        Assert.Equal(new Vector3(30, 0, 0), s.RotationDegrees);
        Assert.True(vm.HasParticleMoves);                               // Save to Mod is offered
        Assert.Equal(new Vector3(150, 400, 280), vm.GizmoPivot);

        Assert.True(vm.UndoService.CanUndo);
        vm.UndoService.Undo();
        Assert.Equal(new Vector3(100, 400, 300), s.Position);
        Assert.Equal(Vector3.Zero, s.RotationDegrees);
        Assert.False(s.HasEdits);
    }

    [Fact]
    public void The_spotlight_icon_is_drawn_for_each_visible_spotlight_with_its_own_glyph()
    {
        var vm = new MainWindowViewModel();
        vm.MapContent.SetSpotlights(new[] { Spot("A", new Vector3(1, 2, 3)), Spot("B", new Vector3(4, 5, 6)) });
        vm.RepublishSpotlights();
        var icons = vm.Dx11Icons(2000f);
        Assert.Equal(2, icons.Count(i => i.Glyph == ViewportIcon.Spotlight));
        Assert.Contains(icons, i => i.Glyph == ViewportIcon.Spotlight && i.Pos == new Vector3(1, 2, 3));
        vm.ShowLightMarkers = false;                                    // the same switch as the point-light icons
        Assert.DoesNotContain(vm.Dx11Icons(2000f), i => i.Glyph == ViewportIcon.Spotlight);
    }

    // ----------------------------------------------------------------------------------------------- saving

    [Fact]
    public void Save_edits_for_an_edit_a_new_light_and_a_delete_land_in_the_bin_and_the_list_reloads_clean()
    {
        var vm = new MainWindowViewModel();
        byte[] bin = BinWith(("keep", new Vector3(0, 400, 0)), ("edit", new Vector3(500, 400, 0)), ("gone", new Vector3(900, 400, 0)));
        vm.MapContent.SetSpotlights(DynamicSpotlights.Read(bin));
        var byName = vm.MapContent.Spotlights.ToDictionary(x => x.Name);

        byName["edit"].ColorR = 2.5;                                    // HDR: above 1 stays above 1
        byName["edit"].Offset = new Vector3(0, 0, 100);
        byName["gone"].IsRemoved = true;
        var fresh = SpotlightViewModel.Create(Spot("fresh", new Vector3(7, 8, 9)), true, null);
        vm.MapContent.AddSpotlight(fresh);

        var edited = vm.MapContent.Spotlights.Where(x => x.HasEdits && !(x.IsNew && x.IsRemoved)).ToList();
        Assert.DoesNotContain(byName["keep"], edited);
        var edits = Call<List<MapPlacementEdit>>(vm, "BuildSpotlightEdits", bin, edited);
        Assert.Equal(3, edits.Count);
        Assert.True(fresh.Id.IsValid);                                  // the identity is minted at save, in the spotlights' own container

        var saved = MapPlaceableWriter.WriteEdits(bin, edits, out var err);
        Assert.True(saved is not null, err);
        var back = DynamicSpotlights.Read(saved!).ToDictionary(x => x.Name);
        Assert.Equal(new[] { "edit", "fresh", "keep" }, back.Keys.Order().ToArray());
        Assert.Equal(2.5f, back["edit"].Color.X);
        Assert.Equal(new Vector3(500, 400, 100), back["edit"].Position);
        Assert.Equal(new Vector3(7, 8, 9), back["fresh"].Position);
        Assert.Equal(DynamicSpotlights.Read(bin).Single(x => x.Name == "keep"), back["keep"]);   // the untouched one is identical

        // after a save the saved bin is the authored state: nothing is left to save, so a second save cannot double-create
        CallVoid(vm, "ReloadSpotlightsAfterSave", saved!);
        Assert.Equal(3, vm.MapContent.Spotlights.Count);
        Assert.All(vm.MapContent.Spotlights, x => Assert.False(x.HasEdits));
        Assert.False(vm.MapContent.HasPlacementEdits);
    }

    [Fact]
    public void A_new_spotlight_that_is_deleted_before_saving_writes_nothing()
    {
        var vm = new MainWindowViewModel();
        var fresh = SpotlightViewModel.Create(Spot("fresh", Vector3.Zero), true, null);
        vm.MapContent.AddSpotlight(fresh);
        Assert.True(vm.MapContent.HasPlacementEdits);
        fresh.IsRemoved = true;
        Assert.False(vm.MapContent.HasPlacementEdits);
    }

    // ----------------------------------------------------------------------------------------------- review fixes

    [Fact]
    public void An_empty_click_deselects_a_spotlight_and_a_mesh_click_takes_the_gizmo_back_from_a_spotlight_and_a_point_light()
    {
        var vm = new MainWindowViewModel();
        vm.MapContent.SetSpotlights(new[] { Spot("A", new Vector3(100, 400, 300)) });
        vm.SelectedOutlinerItem = vm.MapContent.Spotlights[0];
        Assert.NotNull(vm.SelectedSpotlight);
        CallVoid(vm, "ClearPlaceableSelection");                       // what an empty click does
        Assert.Null(vm.SelectedSpotlight);
        Assert.Null(vm.SpotlightFrustumLines);
        Assert.False(vm.HasPlacementGizmoTarget);

        // a mesh selection while a spotlight / a point light is selected
        var map = new MapGeoAsset
        {
            Positions = new float[9], Normals = new float[9], Uvs = new float[6], Indices = new uint[] { 0, 1, 2 },
            Groups = new[] { new MapGeoGroup("G", 0, 3, VisibilityFlags: 255) },
        };
        typeof(MainWindowViewModel).GetField("_currentMap", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, map);
        var selection = (ReyEngine.Core.Selection.SelectionSet<MapGeoMesh>)typeof(MainWindowViewModel)
            .GetField("_selection", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;
        var mesh = new MapGeoMesh { Index = 0, Name = "wall", VertexStart = 0, VertexCount = 3, Transform = Matrix4x4.Identity, Pivot = Vector3.Zero };

        vm.SelectedOutlinerItem = vm.MapContent.Spotlights[0];
        Assert.NotNull(vm.SelectedSpotlight);
        selection.SetSingle(mesh);
        Assert.Null(vm.SelectedSpotlight);
        Assert.False(vm.HasSelectedSpotlight);

        var light = new PointLightViewModel(new PointLight(Vector3.Zero, Vector3.One, 500f), vm);   // the same gap existed for point lights
        vm.EditableLights.Add(light);
        selection.Clear();
        vm.SelectedOutlinerItem = light;
        Assert.Same(light, vm.SelectedLight);
        selection.SetSingle(mesh);
        Assert.Null(vm.SelectedLight);
    }

    [Fact]
    public void A_new_spotlight_with_nowhere_to_go_aborts_the_save_instead_of_being_dropped()
    {
        var vm = new MainWindowViewModel();
        var empty = new MemoryStream();
        new BinTree(Array.Empty<BinTreeObject>(), Array.Empty<string>()).Write(empty);   // no placeable container at all
        var fresh = SpotlightViewModel.Create(Spot("fresh", Vector3.Zero), true, null);
        vm.MapContent.AddSpotlight(fresh);
        var ex = Assert.Throws<TargetInvocationException>(() => Call<List<MapPlacementEdit>>(vm, "BuildSpotlightEdits", empty.ToArray(), new List<SpotlightViewModel> { fresh }));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("Nothing was written", ex.InnerException!.Message);
        Assert.Contains(fresh, vm.MapContent.Spotlights);              // still there, still pending
        Assert.True(vm.MapContent.HasPlacementEdits);
    }

    [Fact]
    public void A_reload_after_save_drops_the_undo_entries_of_the_replaced_spotlights()
    {
        var vm = new MainWindowViewModel();
        byte[] bin = BinWith(("A", new Vector3(0, 400, 0)));
        vm.MapContent.SetSpotlights(DynamicSpotlights.Read(bin));
        vm.SelectedOutlinerItem = vm.MapContent.Spotlights[0];
        vm.BeginPlacementDrag();
        vm.DragSelectedPlacementTo(new Vector3(10, 0, 0));
        vm.EndPlacementDrag();
        Assert.True(vm.UndoService.CanUndo);
        CallVoid(vm, "ReloadSpotlightsAfterSave", bin);
        Assert.False(vm.UndoService.CanUndo);
    }

    [Fact]
    public void A_cookie_that_failed_to_decode_is_not_remembered_as_one()
    {
        var vm = new MainWindowViewModel();
        var spot = Spot("A", new Vector3(0, 400, 0)) with { CookieHash = 0xABCDEFUL, CookiePath = "0xabcdef" };
        vm.MapContent.SetSpotlights(new[] { spot });
        vm.RepublishSpotlights();
        var cache = (System.Collections.IDictionary)typeof(MainWindowViewModel).GetField("_spotCookieCache", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;
        Assert.Empty(cache);                                            // a miss is retried later, never a cached null
        Assert.Single(vm.Dx11SpotLights!);                              // and the light is still there, with a white cookie
    }

    [Fact]
    public void Dense_points_do_not_push_a_spotlight_out_of_a_cell()
    {
        var down = new Matrix4x4(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 500, 0, 1);
        var (bmin, bmax) = DynamicSpotlights.Bounds(down, 60f, 500f, 10f, 1f);
        var spot = new ClusterSpotLight(new Vector3(0, 500, 0), 500f, Vector3.One, DynamicSpotlights.WorldToLight(down, 60f, 500f, 10f, 1f), new Vector4(0, 0, .5f, .5f), bmin, bmax);
        var points = Enumerable.Range(0, 100).Select(_ => ClusterLightBuilder.MakeLight(new Vector3(0, 10, 0), 100f, Vector3.One, 1f)).ToList();
        var grid = ClusterLightBuilder.Build(points, new Vector3(-600, 0, -600), new Vector3(600, 600, 600), spotLights: new[] { spot });
        Assert.Equal(1, grid.SpotLightCount);
        uint cell = grid.Map.First(c => c != 0 && (grid.Data[c * 4 + 1] & 0xFFFF) == 1 && (grid.Data[c * 4] & 0xFFFF) > 0);
        Assert.Equal((uint)ClusterLightBuilder.DefaultMaxLightsPerCell, grid.Data[cell * 4] & 0xFFFF);   // the points hit their own cap
        Assert.Equal(1u, grid.Data[cell * 4 + 1] & 0xFFFF);                                                // the spotlight is still in
    }

    [Fact]
    public void The_spotlight_wiring_exists_in_the_host_the_inspector_and_the_outliner()
    {
        string? Src(params string[] parts)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx")))
                { var p = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()); return File.Exists(p) ? File.ReadAllText(p) : null; }
            return null;
        }
        var outliner = Src("src", "ReyEngine.App", "Views", "MapOutlinerView.axaml");
        var inspector = Src("src", "ReyEngine.App", "Views", "SceneObjectInspectorView.axaml");
        var window = Src("src", "ReyEngine.App", "Views", "MainWindow.axaml");
        if (outliner is null || inspector is null || window is null) return;
        Assert.Contains("vm:SpotlightViewModel", outliner);
        Assert.Contains("SelectedSpotlight.Far", inspector);
        Assert.Contains("SelectedSpotlight.Cookie", inspector);
        Assert.Contains("SpotlightMarkers=\"{Binding SpotlightMarkers}\"", window);
        string card = inspector[inspector.IndexOf("M900: SPOTLIGHT", StringComparison.Ordinal)..].Split("M79: ADDED MESH")[0];
        Assert.DoesNotMatch("\"#[0-9a-fA-F]{3,8}\"", card);   // no literal colours in the new card (theme rule)
    }
}
