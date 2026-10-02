using System.ComponentModel;
using System.Numerics;
using System.Reflection;
using ReyEngine.App.ViewModels;
using ReyEngine.App.Views;
using ReyEngine.Core.Selection;
using ReyEngine.Formats.Lighting;
using ReyEngine.Formats.MapGeo;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M810: the camera goes to what is selected - the Focus button of every Object card and the F key - for either renderer.
///
/// <para>The Focus command set <c>ParticleFocusPoint</c>, which the GL control applied on its next render. Under Direct3D 11 - the
/// default - that control is hidden and never renders, so every Focus button did nothing (measured in the real window, ten requests out
/// of ten). It also read the highlight marker, which a point light or an added mesh never sets, and the same point twice raised no
/// change. These pin the view model half (the point of each kind of selection, a request that is raised every time), the control half
/// (<see cref="ViewportControl.FocusOnPoint"/> is the one implementation, and mirrors the point into the camera's display space) and the
/// wiring between them; the real window moving the real camera on Direct3D 11 is the headless probe's.</para>
/// </summary>
public sealed class FocusSelectionTests(ITestOutputHelper output)
{
    private static string? Source(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) continue;
            string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            return File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : null;
        }
        return null;
    }

    private static readonly Vector3 Where = new(4173f, 39f, 8580f);

    // one of each kind of selectable placeable, at Where
    private static ParticlePlacementViewModel Particle(Vector3 at, Vector3 offset = default) => new()
    {
        Placement = new MapParticlePlacement("Amb", at, Matrix4x4.CreateTranslation(at), "maps/particles/amb", "group"),
        Offset = offset,
    };

    private static MapSoundViewModel Sound(Vector3 at, Vector3 offset = default) => new()
    {
        Sound = new MapSoundPlacement("Wind", "Play_wind", at, Matrix4x4.CreateTranslation(at)),
        Offset = offset,
    };

    private static AnimatedPropViewModel Prop(Vector3 at) => new()
    {
        Prop = new MapAnimatedProp("Golem_1", at, Matrix4x4.CreateTranslation(at),
            "Characters/Golem/CharacterRecords/Root", "Characters/Golem/Skins/Skin0"),
    };

    private static CubemapProbeViewModel Probe(Vector3 at) => new()
    {
        Probe = new MapCubemapProbe("Probe", at, Matrix4x4.CreateTranslation(at), "assets/maps/probe.dds"),
    };

    private static AddedMapMeshViewModel AddedMesh(Vector3 offset) => new()
    {
        Name = "Added",
        Positions = new float[] { 0, 0, 0, 200, 0, 0, 0, 0, 200 },
        Normals = new float[] { 0, 1, 0, 0, 1, 0, 0, 1, 0 },
        Uvs = new float[] { 0, 0, 1, 0, 0, 1 },
        Indices = new[] { 0, 1, 2 },
        LocalCenter = new Vector3(66f, 0f, 66f),
        Offset = offset,
    };

    private static void SelectInOutliner(MainWindowViewModel vm, object item) => vm.SelectedOutlinerItem = item;   // what a click on the row does

    // ================================================================================================ the point of each selection

    [Fact]
    public void A_selected_particle_is_focused_at_its_current_position_edits_included()
    {
        var vm = new MainWindowViewModel();
        var node = Particle(Where, offset: new Vector3(100f, 0f, -50f));
        SelectInOutliner(vm, node);

        var expected = Where + new Vector3(100f, 0f, -50f);
        Assert.Equal(expected, vm.SelectedFocusPoint());
        Assert.True(vm.RequestFocusOnSelection());
        Assert.Equal(expected, vm.FocusRequestPoint);
    }

    [Fact]
    public void A_selected_sound_is_focused_where_it_sounds()
    {
        var vm = new MainWindowViewModel();
        var sound = Sound(Where, offset: new Vector3(0f, 25f, 0f));
        SelectInOutliner(vm, sound);

        Assert.Equal(Where + new Vector3(0f, 25f, 0f), vm.SelectedFocusPoint());
        Assert.True(vm.RequestFocusOnSelection());
        Assert.Equal(Where + new Vector3(0f, 25f, 0f), vm.FocusRequestPoint);
    }

    [Fact]
    public void A_selected_point_light_is_focused_at_the_light_not_at_a_highlight_it_never_set()
    {
        var vm = new MainWindowViewModel();
        var light = new PointLightViewModel(new PointLight(Where, new Vector3(1f, 0.9f, 0.7f), 800f), vm);
        vm.EditableLights.Add(light);
        SelectInOutliner(vm, light);

        // the old command read this, and a light selection never sets it
        Assert.Null(vm.SelectedParticleMarker);
        Assert.Equal(Where, vm.SelectedFocusPoint());
        Assert.True(vm.RequestFocusOnSelection());
        Assert.Equal(Where, vm.FocusRequestPoint);
    }

    [Fact]
    public void A_selected_added_mesh_is_focused_at_its_pivot_under_its_own_transform()
    {
        var vm = new MainWindowViewModel();
        var mesh = AddedMesh(offset: Where);
        vm.MapContent.AddedMeshes.Add(mesh);
        SelectInOutliner(vm, mesh);

        Assert.Null(vm.SelectedParticleMarker);
        Assert.Equal(mesh.PivotWorld, vm.SelectedFocusPoint());
        Assert.Equal(Where + new Vector3(66f, 0f, 66f), mesh.PivotWorld);
        Assert.True(vm.RequestFocusOnSelection());
        Assert.Equal(mesh.PivotWorld, vm.FocusRequestPoint);
    }

    [Fact]
    public void A_selected_prop_is_focused_where_it_stands_moves_included()
    {
        var vm = new MainWindowViewModel();
        var prop = Prop(Where);
        prop.Offset = new Vector3(0f, 0f, 300f);
        vm.SelectedPropNode = prop;

        Assert.Equal(prop.Position, vm.SelectedFocusPoint());
        Assert.Equal(Where + new Vector3(0f, 0f, 300f), prop.Position);
        Assert.True(vm.RequestFocusOnSelection());
        Assert.Equal(prop.Position, vm.FocusRequestPoint);
    }

    [Fact]
    public void A_selected_reflection_probe_is_focused_at_the_probe()
    {
        var vm = new MainWindowViewModel();
        var probe = Probe(Where);
        SelectInOutliner(vm, probe);

        Assert.Equal(Where, vm.SelectedFocusPoint());
        Assert.True(vm.RequestFocusOnSelection());
        Assert.Equal(Where, vm.FocusRequestPoint);
    }

    [Fact]
    public void A_selected_map_mesh_is_focused_at_the_centre_of_the_selection()
    {
        var vm = new MainWindowViewModel();
        var selection = (SelectionSet<MapGeoMesh>)typeof(MainWindowViewModel)
            .GetField("_selection", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;
        selection.SetSingle(new MapGeoMesh
        {
            Index = 0, Name = "wall", VertexStart = 0, VertexCount = 3, Transform = Matrix4x4.Identity, Pivot = Vector3.Zero,
        });
        vm.GizmoPivot = Where;     // the selection's bounding-box centre, which RefreshSelectionVisuals keeps there

        Assert.Equal(Where, vm.SelectedFocusPoint());
        Assert.True(vm.RequestFocusOnSelection());
        Assert.Equal(Where, vm.FocusRequestPoint);
    }

    // ================================================================================================ the request

    [Fact]
    public void The_point_is_the_selection_and_not_the_highlight_marker_left_by_the_one_before()
    {
        var vm = new MainWindowViewModel();
        var sound = Sound(new Vector3(9854f, 275f, 10643f));
        SelectInOutliner(vm, sound);
        Assert.Equal(sound.Position, vm.SelectedParticleMarker);          // the sound set the marker...

        var mesh = AddedMesh(offset: Where);
        vm.MapContent.AddedMeshes.Add(mesh);
        SelectInOutliner(vm, mesh);                                        // ...and the next row's click does not touch it
        Assert.Equal(sound.Position, vm.SelectedParticleMarker);

        Assert.Equal(mesh.PivotWorld, vm.SelectedFocusPoint());            // the old command would have gone to the sound
        Assert.True(vm.RequestFocusOnSelection());
        Assert.Equal(mesh.PivotWorld, vm.FocusRequestPoint);
    }

    [Fact]
    public void Every_request_is_raised_also_for_the_same_selection_twice()
    {
        var vm = new MainWindowViewModel();
        SelectInOutliner(vm, Particle(Where));
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainWindowViewModel.FocusRequest)) raised.Add(e.PropertyName); };

        Assert.True(vm.RequestFocusOnSelection());
        Assert.True(vm.RequestFocusOnSelection());      // the camera was taken away in between: asking again has to work
        Assert.True(vm.RequestFocusOnSelection());

        Assert.Equal(3, raised.Count);
        Assert.Equal(3, vm.FocusRequest);
    }

    [Fact]
    public void The_command_the_Focus_buttons_bind_asks_for_the_focus()
    {
        var vm = new MainWindowViewModel();
        var light = new PointLightViewModel(new PointLight(Where, Vector3.One, 500f), vm);
        vm.EditableLights.Add(light);
        SelectInOutliner(vm, light);

        Assert.True(vm.FocusSelectedPlaceableCommand.CanExecute(null));
        vm.FocusSelectedPlaceableCommand.Execute(null);

        Assert.Equal(1, vm.FocusRequest);
        Assert.Equal(Where, vm.FocusRequestPoint);
    }

    [Fact]
    public void With_nothing_selected_there_is_nothing_to_focus_and_no_request_is_raised()
    {
        var vm = new MainWindowViewModel();
        Assert.Null(vm.SelectedFocusPoint());
        Assert.False(vm.RequestFocusOnSelection());
        vm.FocusSelectedPlaceableCommand.Execute(null);     // a Focus button on an info-only card (a bucket grid): nothing, quietly
        Assert.Equal(0, vm.FocusRequest);

        // a selection that is cleared again is nothing again, whatever the highlight marker still holds
        SelectInOutliner(vm, Particle(Where));
        Assert.NotNull(vm.SelectedFocusPoint());
        vm.SelectedParticleNode = null;
        vm.SelectedOutlinerItem = null;
        Assert.Null(vm.SelectedFocusPoint());
        Assert.False(vm.RequestFocusOnSelection());
    }

    [Fact]
    public void The_focus_point_is_not_a_property_the_gl_control_watches_any_more()
    {
        Assert.Null(typeof(MainWindowViewModel).GetProperty("ParticleFocusPoint"));
    }

    // ================================================================================================ the control

    private static ViewportControl? Control()
    {
        try { return new ViewportControl(); }
        catch (Exception ex) { output_skip = "SKIPPED: a ViewportControl cannot be built without a platform here - " + ex.GetType().Name; return null; }
    }
    private static string? output_skip;

    [Fact]
    public void Focusing_a_point_puts_it_in_the_middle_of_the_screen_through_the_renderers_mirror()
    {
        var control = Control();
        if (control is null) { output.WriteLine(output_skip!); return; }

        control.Camera.Restore(new ReyEngine.Rendering.OrbitCameraPose(new Vector3(-7345f, 187f, 6509f), 17934f, MathF.PI, 0.98f, 936f, 4319010f));
        control.FocusOnPoint(Where);

        // both renderers draw Scale(-1,1,1) * view * projection: the world point must land on the optical axis
        float aspect = 1.44f;
        var clip = Vector4.Transform(new Vector4(Where, 1f), Matrix4x4.CreateScale(-1f, 1f, 1f) * control.Camera.View * control.Camera.Projection(aspect));
        Assert.True(clip.W > 0f);
        Assert.InRange(clip.X / clip.W, -1e-3f, 1e-3f);
        Assert.InRange(clip.Y / clip.W, -1e-3f, 1e-3f);
        // and the target is the point in DISPLAY space - the unmirrored point would look at the wrong place
        Assert.Equal(new Vector3(-Where.X, Where.Y, Where.Z), control.Camera.Target);
    }

    [Theory]
    [InlineData(17934f, 2500f)]    // from the map overview: close in
    [InlineData(100f, 400f)]       // too close to see the thing: backs off to a working distance
    [InlineData(1000f, 1000f)]     // already near: only recentred
    public void Focus_keeps_a_working_distance_and_leaves_the_users_camera_settings_alone(float distance, float expected)
    {
        var control = Control();
        if (control is null) { output.WriteLine(output_skip!); return; }
        control.Camera.FlySpeed = 4321f;
        control.Camera.FieldOfView = 0.9f;
        control.Camera.Distance = distance;
        float yaw = control.Camera.Yaw, pitch = control.Camera.Pitch;

        control.FocusOnPoint(Where);

        Assert.Equal(expected, control.Camera.Distance);
        Assert.Equal(5f, control.Camera.Near);
        Assert.Equal(200000f, control.Camera.Far);
        Assert.Equal(4321f, control.Camera.FlySpeed);
        Assert.Equal(0.9f, control.Camera.FieldOfView);
        Assert.Equal(yaw, control.Camera.Yaw);       // it recentres: the user's view direction stays
        Assert.Equal(pitch, control.Camera.Pitch);
    }

    // ================================================================================================ the wiring

    [Fact]
    public void The_mesh_preview_window_moves_the_shared_camera_for_its_arena_focus_when_direct3d_11_is_drawing()
    {
        // The arena puts the camera on the champion at the spawn and keeps it there while he walks, through MeshPreviewViewModel.FocusPoint.
        // The GL control applies that on its next render; with the window's Direct3D 11 toggle on it is hidden and never renders (measured in
        // the real window: the camera stayed at the origin for every FocusPoint). GL keeps the binding it has always had.
        string? dx11 = Source("src", "ReyEngine.App", "Views", "MeshPreviewWindow.Dx11.cs");
        string? xaml = Source("src", "ReyEngine.App", "Views", "MeshPreviewWindow.axaml");
        if (dx11 is null || xaml is null) { output.WriteLine("SKIPPED: sources not found"); return; }

        Assert.Contains("else if (e.PropertyName is nameof(MeshPreviewViewModel.FocusPoint) && vm.UseDx11Preview && vm.FocusPoint is { } focus)\n                PreviewViewport.FocusOnPoint(focus);", dx11);
        Assert.Contains("FocusPoint=\"{Binding FocusPoint}\"", xaml);      // the GL path is untouched
    }

    [Fact]
    public void The_window_answers_a_request_by_moving_the_shared_camera_and_F_asks_for_the_selection_first()
    {
        string? window = Source("src", "ReyEngine.App", "Views", "MainWindow.axaml.cs");
        string? xaml = Source("src", "ReyEngine.App", "Views", "MainWindow.axaml");
        string? control = Source("src", "ReyEngine.App", "Views", "ViewportControl.cs");
        string? inspector = Source("src", "ReyEngine.App", "Views", "SceneObjectInspectorView.axaml");
        string? vm = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        if (window is null || xaml is null || control is null || inspector is null || vm is null) { output.WriteLine("SKIPPED: sources not found"); return; }

        // the window moves the camera for either renderer: the request, then the one implementation, then a redraw of whichever draws
        Assert.Contains("else if (e.PropertyName == nameof(MainWindowViewModel.FocusRequest)) FocusViewportOnRequest(vm);", window);
        int handler = window.IndexOf("private void FocusViewportOnRequest(MainWindowViewModel vm)", StringComparison.Ordinal);
        Assert.True(handler > 0);
        string body = window.Substring(handler, 320);
        Assert.Contains("Viewport.FocusOnPoint(vm.FocusRequestPoint);", body);
        Assert.Contains("RedrawViewport(vm);", body);

        // F: the selection when there is one, the map (M808) when there is none; the Frame button stays frame-all
        Assert.Contains("if (e.Key == _kFocus) { FocusSelectionOrFrame(); return; }", window);
        Assert.Contains("if (DataContext is MainWindowViewModel vm && vm.RequestFocusOnSelection()) return;\n        FrameViewport();", window);
        int frameClick = window.IndexOf("private void OnFrameClick(", StringComparison.Ordinal);
        Assert.Contains("FrameViewport();", window.Substring(frameClick, 200));
        Assert.DoesNotContain("RequestFocusOnSelection", window.Substring(frameClick, 200));

        // the GL control is no longer asked to do it for the main viewport: one path, not two that both fire under GL
        Assert.DoesNotContain("ParticleFocusPoint", xaml);
        Assert.DoesNotContain("ParticleFocusPoint", vm);
        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(inspector, "Command=\"\\{Binding FocusSelectedPlaceableCommand\\}\"").Count);

        // ...and the control keeps what other hosts (the Mesh Preview window's arena) still use under GL
        Assert.Contains("public void FocusOnPoint(Vector3 p)", control);
        Assert.Contains("if (_pendingFocus is { } fp) { FocusOnPoint(fp); _pendingFocus = null; }", control);
        Assert.Contains("else if (change.Property == FocusPointProperty && FocusPoint is { } fp)", control);
    }
}
