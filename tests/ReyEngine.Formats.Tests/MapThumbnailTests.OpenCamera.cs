using System.Numerics;
using System.Reflection;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.App.Views;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Meshes;
using ReyEngine.Rendering;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M808: the camera a map is OPENED with in the viewport is the camera its Content Browser picture is drawn from - the same box, the
/// same view direction and the same fit (<see cref="MapThumbnailCamera"/> is the one rule), solved for the viewport's own field of
/// view and size. These are the maths (no GPU), the wiring, and the map tab that keeps its camera; the real window opening real
/// maps is the headless probe's.
/// </summary>
public sealed partial class MapThumbnailTests
{
    private static readonly float ThumbFov = MapThumbnailCamera.FovDegrees * MathF.PI / 180f;
    private const float ThumbAspect = (float)MapThumbnailKey.RenderWidth / MapThumbnailKey.RenderHeight;

    /// <summary>Centroids of a playable area: a flat grid of triangles over x0..x1, z0..z1 at height <paramref name="y"/>.</summary>
    private static List<Vector3> AreaPoints(float x0, float x1, float z0, float z1, float y, int steps = 40)
    {
        var points = new List<Vector3>();
        for (int i = 0; i < steps; i++)
            for (int j = 0; j < steps; j++)
                points.Add(new Vector3(x0 + (x1 - x0) * (i + 0.5f) / steps, y, z0 + (z1 - z0) * (j + 0.5f) / steps));
        return points;
    }

    private static void AssertNear(Vector3 expected, Vector3 actual, float tolerance, string what) =>
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"{what}: expected {expected}, got {actual}");

    /// <summary>Where a WORLD point lands on screen (NDC) in the viewport: the mirror first, then the camera - the order both
    /// renderers apply them in (<c>Scale(-1,1,1) * view * projection</c>).</summary>
    private static Vector2 Ndc(OrbitCamera camera, float aspect, Vector3 world)
    {
        var clip = Vector4.Transform(new Vector4(world, 1f),
            Matrix4x4.CreateScale(-1f, 1f, 1f) * camera.View * camera.Projection(aspect));
        Assert.True(clip.W > 0, $"{world} is behind the camera");
        return new Vector2(clip.X / clip.W, clip.Y / clip.W);
    }

    private static IEnumerable<Vector3> Corners(MapOpenFrame f)
    {
        for (int i = 0; i < 8; i++)
            yield return new Vector3((i & 1) == 0 ? f.BoxMin.X : f.BoxMax.X, (i & 2) == 0 ? f.BoxMin.Y : f.BoxMax.Y, (i & 4) == 0 ? f.BoxMin.Z : f.BoxMax.Z);
    }

    private static MapOpenFrame BoxOf(IReadOnlyList<Vector3> points)
    {
        Assert.True(MapThumbnailCamera.TryBox(points, out var min, out var max));
        return new MapOpenFrame(min, max);
    }

    // ================================================================================================ the one rule

    [Fact]
    public void At_the_thumbnails_own_lens_the_viewport_camera_is_exactly_the_thumbnails_camera()
    {
        var points = AreaPoints(1000f, 9000f, 500f, 7500f, y: 30f);
        Assert.True(MapThumbnailCamera.TryFrame(points, ThumbAspect, out var thumb));
        var camera = new OrbitCamera { FieldOfView = ThumbFov };

        Assert.True(MapViewCamera.TryApply(camera, BoxOf(points), ThumbAspect));

        AssertNear(thumb.Target, camera.Target, 0.05f, "target");
        AssertNear(thumb.Eye, camera.Position, 0.1f, "eye");
        Assert.Equal(thumb.Distance, camera.Distance, 0.05);
        // the view matrix the renderers use, element by element
        var a = thumb.View; var b = camera.View;
        foreach (var (x, y) in new[] { (a.M11, b.M11), (a.M13, b.M13), (a.M22, b.M22), (a.M23, b.M23), (a.M32, b.M32), (a.M33, b.M33), (a.M41, b.M41), (a.M42, b.M42), (a.M43, b.M43) })
            Assert.True(MathF.Abs(x - y) <= 0.05f + MathF.Abs(x) * 1e-4f, $"view element {x} vs {y}");
    }

    [Theory]
    [InlineData(0.7853982f, 1.38f)]    // the editor camera's own 45 degrees, in the viewport as the probe's window lays it out
    [InlineData(0.7853982f, 1.78f)]
    [InlineData(0.7853982f, 2.40f)]
    [InlineData(0.7853982f, 0.70f)]    // a tall, narrow viewport
    [InlineData(1.0471976f, 1.78f)]    // and a wider lens
    public void The_viewports_lens_changes_how_the_box_is_fitted_and_nothing_else(float fov, float aspect)
    {
        var points = AreaPoints(1000f, 9000f, 500f, 7500f, y: 30f);
        var frame = BoxOf(points);
        var camera = new OrbitCamera { FieldOfView = fov };

        Assert.True(MapViewCamera.TryApply(camera, frame, aspect));

        // the same side, the same pitch: the thumbnail's view direction to the last digit
        var direction = Vector3.Normalize(camera.Position - camera.Target);
        AssertNear(MapThumbnailCamera.Direction, direction, 1e-5f, "view direction");
        Assert.Equal(MathF.Atan2(MapThumbnailCamera.Direction.X, MapThumbnailCamera.Direction.Z), camera.Yaw, 4);
        Assert.InRange(camera.Pitch * 180f / MathF.PI, 56.2f, 56.4f);   // "~56 degrees"

        // the fit slid the target across the screen only: it is still on the plane through the box's centre facing the camera
        var displayCentre = new Vector3(-(frame.BoxMin.X + frame.BoxMax.X) * 0.5f, (frame.BoxMin.Y + frame.BoxMax.Y) * 0.5f, (frame.BoxMin.Z + frame.BoxMax.Z) * 0.5f);
        Assert.True(MathF.Abs(Vector3.Dot(camera.Target - displayCentre, MapThumbnailCamera.Direction)) <= 0.5f,
            "the target left the plane the box is centred on");

        // and the box, as the viewport draws it, fills the frame without leaving it - centred
        var ndc = Corners(frame).Select(c => Ndc(camera, aspect, c)).ToList();
        float extent = ndc.Max(p => MathF.Max(MathF.Abs(p.X), MathF.Abs(p.Y)));
        Assert.InRange(extent, 0.88f, 0.9601f);
        Assert.InRange((ndc.Min(p => p.X) + ndc.Max(p => p.X)) * 0.5f, -0.03f, 0.03f);
        Assert.InRange((ndc.Min(p => p.Y) + ndc.Max(p => p.Y)) * 0.5f, -0.03f, 0.03f);
    }

    [Fact]
    public void The_camera_is_in_mirrored_space_so_a_box_far_from_the_origin_is_centred_and_an_unmirrored_target_is_not()
    {
        var frame = BoxOf(AreaPoints(11000f, 14500f, 2000f, 6000f, y: 100f));      // a corner of a big map: x is far from 0
        var camera = new OrbitCamera();

        Assert.True(MapViewCamera.TryApply(camera, frame, 1.6f));

        Assert.True(camera.Target.X < -11000f && camera.Target.X > -14500f, $"the target is in display space, X negated: {camera.Target}");
        var centre = new Vector3((frame.BoxMin.X + frame.BoxMax.X) * 0.5f, frame.BoxMin.Y, (frame.BoxMin.Z + frame.BoxMax.Z) * 0.5f);
        var onScreen = Ndc(camera, 1.6f, centre);
        Assert.InRange(MathF.Abs(onScreen.X), 0f, 0.1f);

        // the mistake this guards against: a target taken from the unmirrored bounds (what FrameCamera did) is nowhere near
        var wrong = new OrbitCamera { Target = new Vector3(centre.X, centre.Y, centre.Z), Distance = camera.Distance, Yaw = camera.Yaw, Pitch = camera.Pitch };
        Assert.True(MathF.Abs(Ndc(wrong, 1.6f, centre).X) > 0.5f, "an unmirrored target would look the wrong way");
    }

    [Fact]
    public void A_box_that_cannot_be_fitted_leaves_the_camera_exactly_as_it_was()
    {
        var frame = BoxOf(AreaPoints(0f, 4000f, 0f, 4000f, y: 0f));
        var camera = new OrbitCamera { Target = new Vector3(5, 6, 7), Distance = 321f, Yaw = 0.1f, Pitch = 0.2f };
        var before = camera.Pose;

        Assert.False(MapViewCamera.TryApply(camera, frame, 0f));
        Assert.False(MapViewCamera.TryApply(camera, frame, float.NaN));
        Assert.False(MapViewCamera.TryApply(new OrbitCamera { FieldOfView = 0f }, frame, 1.5f));
        Assert.Equal(before, camera.Pose);
    }

    [Fact]
    public void Placing_the_camera_touches_its_place_and_nothing_of_how_it_behaves()
    {
        var camera = new OrbitCamera { FlySpeed = 1234f, FieldOfView = 0.9f, Near = 7f, Far = 55555f };

        Assert.True(MapViewCamera.TryApply(camera, BoxOf(AreaPoints(0f, 9000f, 0f, 9000f, y: 0f)), 1.7f));

        Assert.Equal(1234f, camera.FlySpeed);
        Assert.Equal(0.9f, camera.FieldOfView);
        Assert.Equal(7f, camera.Near);                // the clip planes follow the whole mesh, which only the viewport knows
        Assert.Equal(55555f, camera.Far);
    }

    // ================================================================================================ the area

    private static MapGeoAsset TwoPartMap(out Vector3 areaMin, out Vector3 areaMax, out float skyHeight)
    {
        // group 0: the playable area, a flat 20 x 20 grid of quads over x 2000..12000, z 1000..9000 at y = 40;
        // group 1: a sky bowl, one huge quad 90000 units across at y = skyHeight
        var positions = new List<float>();
        var indices = new List<uint>();
        const int n = 20;
        for (int i = 0; i <= n; i++)
            for (int j = 0; j <= n; j++)
            { positions.Add(2000f + 10000f * i / n); positions.Add(40f); positions.Add(1000f + 8000f * j / n); }
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                uint a = (uint)(i * (n + 1) + j), b = a + 1, c = (uint)((i + 1) * (n + 1) + j), d = c + 1;
                indices.AddRange(new[] { a, b, c, b, d, c });
            }
        int areaCount = indices.Count;
        skyHeight = 60000f;
        uint s = (uint)(positions.Count / 3);
        foreach (var (x, z) in new[] { (-40000f, -40000f), (50000f, -40000f), (-40000f, 50000f), (50000f, 50000f) })
        { positions.Add(x); positions.Add(skyHeight); positions.Add(z); }
        indices.AddRange(new[] { s, s + 1, s + 2, s + 1, s + 3, s + 2 });

        areaMin = new Vector3(2000f, 40f, 1000f);
        areaMax = new Vector3(12000f, 40f + 10000f * MapThumbnailCamera.HeightShare, 9000f);
        return new MapGeoAsset
        {
            Positions = positions.ToArray(), Normals = new float[positions.Count], Uvs = new float[positions.Count / 3 * 2],
            Indices = indices.ToArray(),
            Groups = new[]
            {
                new MapGeoGroup("area", 0, areaCount, VisibilityFlags: 255),
                new MapGeoGroup("sky", areaCount, 6, VisibilityFlags: 255),
            },
        };
    }

    [Fact]
    public void The_area_is_where_the_drawn_triangles_are_not_the_sky_bowl_the_mapgeo_also_holds()
    {
        var map = TwoPartMap(out var areaMin, out var areaMax, out float skyHeight);

        var frame = MapViewCamera.FrameOf(map, new[] { true, true })!;       // both drawn: 800 triangles against 2
        Assert.NotNull(frame);
        AssertNear(areaMin, frame.BoxMin, 900f, "box minimum");               // a trimmed percentile of a grid: within a cell or two
        AssertNear(new Vector3(areaMax.X, areaMin.Y, areaMax.Z), new Vector3(frame.BoxMax.X, areaMin.Y, frame.BoxMax.Z), 900f, "box maximum");

        // what the viewport framed before: the sphere around every vertex, the bowl included
        float minX = map.Positions.Where((_, i) => i % 3 == 0).Min(), maxX = map.Positions.Where((_, i) => i % 3 == 0).Max();
        float minZ = map.Positions.Where((_, i) => i % 3 == 2).Min(), maxZ = map.Positions.Where((_, i) => i % 3 == 2).Max();
        float radius = new Vector3(maxX - minX, skyHeight - 40f, maxZ - minZ).Length() * 0.5f;
        float oldDistance = radius / MathF.Sin(MathF.PI / 4f * 0.5f) * 1.25f;
        var camera = new OrbitCamera();
        Assert.True(MapViewCamera.TryApply(camera, frame, 1.6f));
        Assert.True(camera.Distance < oldDistance / 3f, $"{camera.Distance:0} against the {oldDistance:0} the whole mesh asked for");

        // a bowl that is the only thing drawn is framed, though: the rule does not decide what is in the map
        var bowl = MapViewCamera.FrameOf(map, new[] { false, true });
        Assert.NotNull(bowl);
    }

    [Fact]
    public void Only_the_groups_the_start_state_draws_are_framed_and_a_map_with_none_has_nothing_to_frame()
    {
        var map = TwoPartMap(out var areaMin, out _, out float skyHeight);

        var hiddenArea = MapViewCamera.FrameOf(map, new[] { false, true })!;
        Assert.True(hiddenArea.BoxMin.Y > skyHeight - 1f, "only the bowl is left to frame");
        Assert.Null(MapViewCamera.FrameOf(map, new[] { false, false }));
        Assert.NotNull(MapViewCamera.FrameOf(map, null));                       // no list: everything draws
        Assert.NotNull(MapViewCamera.FrameOf(map, new[] { true }));             // a group past the list is visible, as in the picture
    }

    [Fact]
    public void Resolving_over_controllers_that_are_already_built_is_the_same_start_state()
    {
        var mutator = new BinTreeObject(Hall, HashAlgorithms.Fnv1a("MutatorMapVisibilityController"),
            new BinTreeProperty[] { new BinTreeString(HashAlgorithms.Fnv1a("MutatorName"), "SR_Hall_Of_Legends") });
        var map = MapOf((255, 0), (255, Hall), (1, 0), (2, 0));
        var definition = new MapVisibilityDefinition(new[] { Rift });
        var bins = new[] { Bin(mutator) };

        var viaBins = MapStartState.Resolve(map, definition, bins, boardStage: null);
        var viaBuilt = MapStartState.ResolveWith(map, definition, MapVisibilityControllers.Build(bins, definition), boardStage: null);
        var withoutControllers = MapStartState.ResolveWith(map, definition, null, boardStage: null);

        Assert.Equal(viaBins.GroupVisible, viaBuilt.GroupVisible);
        Assert.Equal(new[] { true, false, true, false }, viaBuilt.GroupVisible);          // the event-only group is off
        Assert.True(withoutControllers.GroupVisible[1], "no controllers, no event gate - the caller owns that choice");

        var board = MapStartState.ResolveWith(MapOf((4, 0), (8, 0), (64, 0), (255, 0)), new MapVisibilityDefinition(new[] { Board }), null, Stage(20));
        Assert.Equal(new[] { true, false, false, true }, board.GroupVisible);              // a TFT board is framed in its first stage
    }

    // ================================================================================================ the control

    [Fact]
    public void The_viewport_control_frames_an_open_map_on_its_area_and_anything_else_on_its_bounds_as_before()
    {
        var map = TwoPartMap(out _, out _, out _);
        var mesh = new MeshAsset
        {
            Positions = map.Positions, Normals = map.Normals, Uvs = map.Uvs, Indices = map.Indices,
            SubMeshes = new List<SubMeshInfo>(), VertexCount = map.Positions.Length / 3,
            BoundsMin = new Vector3(-40000f, 40f, -40000f), BoundsMax = new Vector3(50000f, 60000f, 50000f),
        };
        ViewportControl control;
        try { control = new ViewportControl(); }
        catch (Exception ex) { output.WriteLine("SKIPPED: a ViewportControl cannot be built without a platform here - " + ex.GetType().Name); return; }
        control.Mesh = mesh;

        // no frame: the whole mesh's bounding sphere, exactly as it was
        float radius = mesh.Radius;
        float oldDist = radius / MathF.Sin(control.Camera.FieldOfView * 0.5f) * 1.25f;
        Assert.True(control.FrameCamera(1400, 800));
        AssertNear(mesh.Center, control.Camera.Target, 0.01f, "bounds centre");
        Assert.Equal(Math.Clamp(oldDist, 5f, 100000f), control.Camera.Distance, 0.01);
        Assert.Equal(MathF.Max(oldDist * 0.01f, 0.05f), control.Camera.Near, 0.001);
        Assert.Equal(oldDist * 40f + radius * 20f, control.Camera.Far, 1.0);

        // with the area of the open map: its camera, and the SAME clip planes as before (the bowl has to stay inside them)
        var frame = MapViewCamera.FrameOf(map, new[] { true, true })!;
        control.MapFrame = frame;
        control.Camera.FlySpeed = 4321f;
        Assert.True(control.FrameCamera(1400, 800));
        var expected = new OrbitCamera();
        Assert.True(MapViewCamera.TryApply(expected, frame, 1400f / 800f));
        AssertNear(expected.Target, control.Camera.Target, 0.01f, "area target");
        Assert.Equal(expected.Distance, control.Camera.Distance, 0.01);
        Assert.Equal(expected.Yaw, control.Camera.Yaw, 5);
        Assert.Equal(expected.Pitch, control.Camera.Pitch, 5);
        Assert.Equal(MathF.Max(oldDist * 0.01f, 0.05f), control.Camera.Near, 0.001);
        Assert.Equal(oldDist * 40f + radius * 20f, control.Camera.Far, 1.0);
        Assert.Equal(4321f, control.Camera.FlySpeed);                                      // the fly speed is the user's

        // a viewport with no size yet falls back to the bounds rather than dividing by it
        control.Camera.Distance = 1f;
        Assert.True(control.FrameCamera(0, 0));
        Assert.Equal(Math.Clamp(oldDist, 5f, 100000f), control.Camera.Distance, 0.01);
    }

    // ================================================================================================ opening, reloading, coming back

    [Fact]
    public void A_camera_pose_is_a_place_and_putting_it_back_changes_nothing_about_how_the_camera_behaves()
    {
        var camera = new OrbitCamera { Target = new Vector3(1, 2, 3), Distance = 800f, Yaw = 0.3f, Pitch = 0.9f, Near = 4f, Far = 77777f, FlySpeed = 999f, FieldOfView = 0.8f };
        var pose = camera.Pose;

        camera.Target = Vector3.Zero; camera.Distance = 5f; camera.Yaw = 2f; camera.Pitch = -1f; camera.Near = 1f; camera.Far = 1f;
        camera.FlySpeed = 123f; camera.FieldOfView = 1.1f;
        camera.Restore(pose);

        Assert.Equal(pose, camera.Pose);
        Assert.Equal(123f, camera.FlySpeed);              // the user's settings are not part of a place
        Assert.Equal(1.1f, camera.FieldOfView);
    }

    [Fact]
    public void A_map_tab_that_comes_back_gets_its_camera_and_its_frame_back_and_is_not_framed_again()
    {
        var vm = new MainWindowViewModel();
        var map = TwoPartMap(out _, out _, out _);
        var entry = new WadAssetEntry { Path = "data/maps/mapgeometry/map22/test.mapgeo", PathHash = 4242, IsResolved = true, Type = AssetType.MapGeometry };
        void Set(string field, object? value) =>
            typeof(MainWindowViewModel).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, value);
        Set("_currentMap", map); Set("_currentMapBytes", new byte[] { 1, 2, 3 }); Set("_currentMapEntry", entry);
        vm.CurrentMesh = new MeshAsset
        {
            Positions = map.Positions, Normals = map.Normals, Uvs = map.Uvs, Indices = map.Indices,
            SubMeshes = new List<SubMeshInfo>(), VertexCount = map.Positions.Length / 3,
        };
        var tabsFrame = MapViewCamera.FrameOf(map, null)!;
        vm.CurrentMapFrame = tabsFrame;
        var tabsPose = new OrbitCameraPose(new Vector3(-5000, 30, 4000), 21000f, MathF.PI, 0.98f, 52f, 600000f);
        OrbitCameraPose? restored = null;
        vm.CaptureCameraPose = () => tabsPose;
        vm.RestoreCameraPose = pose => restored = pose;

        // another tab takes the viewport: the map's scene is captured with the camera it was left with
        var scene = typeof(MainWindowViewModel).GetMethod("CaptureMapScene", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, null);
        Assert.NotNull(scene);
        vm.CurrentMapFrame = new MapOpenFrame(Vector3.Zero, new Vector3(100, 10, 100));       // the other map's area
        int requests = vm.MapFrameRequest;

        // and it comes back
        try { typeof(MainWindowViewModel).GetMethod("RestoreMapScene", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, new[] { scene }); }
        catch (TargetInvocationException ex) { output.WriteLine("restore threw after the camera was handled? " + ex.InnerException); throw; }

        Assert.Equal(tabsPose, restored);                          // the camera is where the tab left it
        Assert.Same(tabsFrame, vm.CurrentMapFrame);                // and Frame frames this map again, not the other one
        Assert.Equal(requests, vm.MapFrameRequest);                // nothing asked the window to frame it
    }

    [Fact]
    public void Opening_a_map_asks_for_a_frame_and_reloading_it_or_returning_to_it_does_not_and_the_window_places_the_camera()
    {
        string? vm = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        string? window = Source("src", "ReyEngine.App", "Views", "MainWindow.axaml.cs");
        string? control = Source("src", "ReyEngine.App", "Views", "ViewportControl.cs");
        string? xaml = Source("src", "ReyEngine.App", "Views", "MainWindow.axaml");
        if (vm is null || window is null || control is null || xaml is null) { output.WriteLine("SKIPPED: sources not found"); return; }

        // LoadMapGeoAsync: the previous map is read BEFORE this one is assigned, and only a different map asks for the frame
        int previous = vm.IndexOf("var previousEntry = _currentMapEntry;", StringComparison.Ordinal);
        int assign = vm.IndexOf("_currentMapEntry = entry;", previous, StringComparison.Ordinal);
        Assert.True(previous > 0 && assign > previous, "the previous map is read before the new one is stored");
        Assert.Contains("if (openFrame is not null && (previousEntry is null || previousEntry.PathHash != entry.PathHash)) MapFrameRequest++;", vm);
        Assert.Contains("CurrentMapFrame = openFrame;", vm);
        // the start state the picture is drawn from, not the editor's "All layers"
        string? openCamera = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.MapOpenCamera.cs");
        Assert.NotNull(openCamera);
        Assert.Contains("MapStartState.ResolveWith(map, _mapStartDefinition ?? MapVisibilityDefinition.Empty, _mapStartControllers,", openCamera);
        Assert.Contains("_mapStartDefinition = parsedVisibility;", vm);       // the shipping bin's own definition, before any axis is inferred
        // a tab: the scene carries the frame and the camera; restoring hands them back and never bumps the request
        Assert.Contains("CurrentMapFrame, CaptureCameraPose?.Invoke());", vm);
        int restore = vm.IndexOf("private void RestoreMapScene(MapScene s)", StringComparison.Ordinal);
        string restoreBody = vm.Substring(restore, vm.IndexOf("/// <summary>Push the freshly-built asset tree", restore, StringComparison.Ordinal) - restore);
        Assert.Contains("CurrentMapFrame = s.Frame;", restoreBody);
        Assert.Contains("RestoreCameraPose?.Invoke(camera)", restoreBody);
        Assert.DoesNotContain("MapFrameRequest", restoreBody);
        Assert.Contains("CurrentMapFrame = null;   // M808", vm);   // a cleared viewport has nothing to frame

        // the window: it owns the camera and the size of the area both renderers share
        Assert.Contains("else if (e.PropertyName == nameof(MainWindowViewModel.MapFrameRequest)) FrameViewportForOpenedMap(vm);", window);
        Assert.Contains("cameraHost.CaptureCameraPose = () => Viewport.Camera.Pose;", window);
        Assert.Contains("Viewport.Camera.Restore(pose);", window);
        Assert.Contains("var size = ViewportInput.Bounds;", window);
        Assert.Contains("Viewport.FrameCamera(size.Width, size.Height);", window);
        Assert.Contains("ViewportInput.SizeChanged +=", window);              // a map opened before the first layout is framed when there is one
        Assert.Contains("if (e.Key == _kFocus) { FocusSelectionOrFrame(); return; }", window);   // M810: the selection first, this when there is none
        Assert.Contains("if (DataContext is MainWindowViewModel vm && vm.RequestFocusOnSelection()) return;\n        FrameViewport();", window.Replace("\r\n", "\n"));
        Assert.DoesNotContain("Viewport.FocusSelected()", window);
        Assert.Contains("FrameViewport();   // M808", window);                // the Frame button, which under Direct3D 11 asked a hidden control

        // the GL control: told the area; a map it was told about is not re-framed by every upload of its mesh
        Assert.Contains("MapFrame=\"{Binding CurrentMapFrame}\"", xaml);
        Assert.Contains("_needFrame = MapFrame is null;", control);
        Assert.Contains("public bool FrameCamera(double width, double height)", control);
    }
}
