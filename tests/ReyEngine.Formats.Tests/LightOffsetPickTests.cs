using System.Numerics;
using System.Reflection;
using ReyEngine.App.ViewModels;
using ReyEngine.Formats.Lighting;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>M828: a point light with a fit offset (spread / scale / offset knobs) is DRAWN at the fitted position.
/// Click-pick and the gizmo used the stored position, so the bulb could not be clicked and the gizmo sat elsewhere.</summary>
public class LightOffsetPickTests
{
    private static (MainWindowViewModel Vm, PointLightViewModel Light) Rig(Vector3 stored)
    {
        var vm = new MainWindowViewModel();
        var light = new PointLightViewModel(new PointLight(stored, new Vector3(1f, 1f, 1f), 500f), vm);
        vm.EditableLights.Add(light);
        vm.RepublishLights();
        vm.ShowDynamicLights = true;
        vm.ShowLightMarkers = true;
        return (vm, light);
    }

    private static object? ClickIcon(MainWindowViewModel vm, Vector3 iconWorld)
    {
        vm.SelectedOutlinerItem = null;
        // a flat XY "screen": the pixel of a point is its X,Y; the ray goes straight down +Z through the icon
        vm.SelectAnyFromViewport(new Vector3(iconWorld.X, iconWorld.Y, -500f), Vector3.UnitZ, additive: false,
            projectToScreen: v => new Vector2(v.X, v.Y), clickScreenPx: new Vector2(iconWorld.X, iconWorld.Y));
        return vm.SelectedOutlinerItem;
    }

    private static object? ClickRay(MainWindowViewModel vm, Vector3 iconWorld)
    {
        vm.SelectedOutlinerItem = null;
        vm.SelectAnyFromViewport(new Vector3(iconWorld.X, iconWorld.Y, -500f), Vector3.UnitZ);
        return vm.SelectedOutlinerItem;
    }

    [Fact]
    public void A_light_with_an_offset_is_picked_by_its_icon_and_the_gizmo_sits_on_it()
    {
        var (vm, light) = Rig(new Vector3(100f, 30f, 200f));
        vm.DynamicLightOffsetX = 1000;
        vm.DynamicLightOffsetZ = -400;
        vm.DynamicLightPositionScale = 1.5;

        var icon = vm.Dx11Icons(1000f).Single(i => i.Glyph == ReyEngine.Core.Assets.ViewportIcon.Light).Pos;
        Assert.Equal(new Vector3(1150f, 30f, -100f), icon);
        Assert.Equal(icon, vm.LightWorldPosition(light));

        Assert.Same(light, ClickIcon(vm, icon));
        Assert.Equal(icon, vm.GizmoPivot);

        Assert.Same(light, ClickRay(vm, icon));
        // the stored (unfitted) spot is NOT where the light is
        Assert.Null(ClickRay(vm, light.Position));
    }

    [Fact]
    public void Dragging_the_gizmo_moves_the_light_by_exactly_the_drag()
    {
        var (vm, light) = Rig(new Vector3(100f, 30f, 200f));
        vm.DynamicLightOffsetX = 1000;
        vm.DynamicLightOffsetZ = -400;
        vm.DynamicLightPositionScale = 2.0;
        vm.SelectedLight = light;

        var (start, _, _) = vm.PlacementDragStart;
        Assert.Equal(vm.LightWorldPosition(light), start);

        var delta = new Vector3(50f, 10f, -70f);
        vm.DragSelectedPlacementTo(start + delta);
        Assert.Equal(start + delta, vm.LightWorldPosition(light));
        Assert.Equal(start + delta, vm.GizmoPivot);
    }

    [Fact]
    public void Changing_the_fit_knobs_keeps_the_gizmo_on_the_selected_light()
    {
        var (vm, light) = Rig(new Vector3(100f, 30f, 200f));
        vm.SelectedLight = light;
        vm.DynamicLightOffsetX = 500;
        Assert.Equal(vm.LightWorldPosition(light), vm.GizmoPivot);
    }

    [Fact]
    public void A_light_without_an_offset_behaves_as_before()
    {
        var (vm, light) = Rig(new Vector3(100f, 30f, 200f));
        Assert.Equal(light.Position, vm.LightWorldPosition(light));
        Assert.Same(light, ClickIcon(vm, light.Position));
        Assert.Equal(light.Position, vm.GizmoPivot);

        var (start, _, _) = vm.PlacementDragStart;
        Assert.Equal(light.Position, start);
        vm.DragSelectedPlacementTo(new Vector3(130f, 30f, 150f));
        Assert.Equal(new Vector3(130f, 30f, 150f), light.Position);
    }

    [Fact]
    public void A_new_light_appears_exactly_at_the_pivot_when_a_fit_is_active()
    {
        var (vm, _) = Rig(new Vector3(100f, 30f, 200f));
        vm.DynamicLightOffsetX = 1000;
        vm.DynamicLightOffsetZ = -400;
        vm.DynamicLightPositionScale = 2.0;
        var pivot = new Vector3(1500f, 40f, 300f);
        vm.GizmoPivot = pivot;
        vm.AddLightCommand.Execute(null);
        var added = Assert.IsType<PointLightViewModel>(vm.SelectedLight);
        Assert.Equal(pivot, vm.LightWorldPosition(added));
        Assert.Equal(pivot, vm.GizmoPivot);
    }
}
