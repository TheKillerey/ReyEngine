using ReyEngine.App.ViewModels;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M518: picking a material for an existing mesh has to stick.
///
/// <para>M517 shipped the picker and it could not be used: every selection snapped straight back. The
/// cause was not the binding — it was the side effect. Setting the material called NotifyMaterialsChanged,
/// which raises PropertyChanged for MapMaterialNames, which replaces the ComboBox's ItemsSource WHILE it is
/// processing the selection. The control re-resolves against the new list and reverts.</para>
///
/// <para>Measured on the headless platform rather than reasoned about: with the ItemsSource replaced the
/// view model stayed on the old material; without it the pick stuck. That probe lives in the scratchpad;
/// what is pinned here is the invariant it taught — which material a MESH uses is not which materials
/// EXIST, so a mesh edit must not disturb the list.</para>
/// </summary>
public sealed class MeshMaterialSelectionTests
{
    private static MapGeoMesh Mesh(params string[] fileMaterials) => new()
    {
        Index = 0,
        Name = "mesh0",
        VertexStart = 0,
        VertexCount = 0,
        Transform = System.Numerics.Matrix4x4.Identity,
        Pivot = System.Numerics.Vector3.Zero,
        Materials = fileMaterials,
    };

    [Fact]
    public void TheEffectiveMaterialFollowsThePendingEdit()
    {
        var mesh = Mesh("Original");
        Assert.Equal("Original", mesh.EffectiveMaterial);

        mesh.MaterialEdit = "Chosen";
        Assert.Equal("Chosen", mesh.EffectiveMaterial);

        mesh.MaterialEdit = null;
        Assert.Equal("Original", mesh.EffectiveMaterial);
    }

    [Fact]
    public void AMeshWithNoMaterialAtAllReportsEmptyRatherThanThrowing()
    {
        // Not every mesh in every mapgeo names one; the picker still has to render.
        var mesh = Mesh();
        Assert.Equal("", mesh.EffectiveMaterial);
        Assert.False(mesh.HasMaterialEdit);
    }

    [Fact]
    public void ReSelectingTheCurrentMaterialLeavesTheMapClean()
    {
        // The setter's guard. Without it, merely clicking through meshes in the picker would mark the map
        // dirty and the next save would rewrite the whole file.
        var mesh = Mesh("Original");
        mesh.MaterialEdit = "Original";
        Assert.False(mesh.HasMaterialEdit);
        Assert.False(MapGeoMaterialWriter.HasEdits(new[] { mesh }));
    }

    [Fact]
    public void UndoRestoresThePreviousPendingValueIncludingNone()
    {
        var mesh = Mesh("Original");
        int refreshes = 0;

        var first = new MeshMaterialCommand(null, mesh, null, "Chosen", () => refreshes++);
        first.Execute();
        Assert.Equal("Chosen", mesh.EffectiveMaterial);

        var second = new MeshMaterialCommand(null, mesh, "Chosen", "Another", () => refreshes++);
        second.Execute();
        Assert.Equal("Another", mesh.EffectiveMaterial);

        second.Undo();
        Assert.Equal("Chosen", mesh.EffectiveMaterial);
        first.Undo();
        Assert.Equal("Original", mesh.EffectiveMaterial);   // back to what the FILE names, not to ""
        Assert.False(mesh.HasMaterialEdit);
        Assert.Equal(4, refreshes);                          // both directions refresh the viewport
    }

    [Fact]
    public void TheCommandNamesWhichWayItGoes()
    {
        var mesh = Mesh("Original");
        Assert.Equal("Change Mesh Material", new MeshMaterialCommand(null, mesh, null, "Chosen", null).Name);
        Assert.Equal("Revert Mesh Material", new MeshMaterialCommand(null, mesh, "Chosen", null, null).Name);
    }
}
