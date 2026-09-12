using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.MapGeo;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>M699: a placed prop moves. It was the one placement the gizmo could not touch - you could
/// import a character, place it, and then not put it where it belongs - so it gains the same offset /
/// rotation / scale model a particle has, the same undo step, and the same transform verb in the writer.</summary>
public sealed class PropMoveTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static AnimatedPropViewModel Prop(Matrix4x4? authored = null)
    {
        var transform = authored ?? Matrix4x4.CreateTranslation(100f, 20f, 300f);
        return new AnimatedPropViewModel
        {
            Prop = new MapAnimatedProp("Golem_1", transform.Translation, transform,
                "Characters/Golem/CharacterRecords/Root", "Characters/Golem/Skins/Skin0",
                VisibilityFlags: 255, HasVisibilityFlags: false, Id: new MapPlacementId(0x5000u, 0xBEEFu)),
        };
    }

    [Fact]
    public void AnUntouchedPropIsNotAnEdit()
    {
        var prop = Prop();
        Assert.False(prop.IsMoved);
        Assert.False(prop.HasEdits);
        Assert.Equal(new Vector3(100f, 20f, 300f), prop.Position);
        Assert.Equal(prop.Prop.Transform, prop.CurrentTransform);
    }

    [Fact]
    public void MovingRotatingAndScalingComposeTheWayAParticleDoes()
    {
        var prop = Prop();
        prop.Offset = new Vector3(10f, -5f, 0f);
        Assert.True(prop.IsMoved);
        Assert.True(prop.HasEdits);
        Assert.Equal(new Vector3(110f, 15f, 300f), prop.CurrentPosition);
        Assert.Equal(new Vector3(110f, 15f, 300f), prop.Position);          // markers and picking follow it
        Assert.Equal(new Vector3(110f, 15f, 300f), prop.CurrentTransform.Translation);

        // the extra rotation and scale compose in the placement's LOCAL space, under the authored
        // transform, and the translation is overridden - the same rule the particle placement uses
        prop.RotationDegrees = new Vector3(0f, 90f, 0f);
        prop.Scale = new Vector3(2f, 2f, 2f);
        var composed = prop.CurrentTransform;
        var expected = Matrix4x4.CreateScale(prop.Scale)
                     * Matrix4x4.CreateFromYawPitchRoll(MathF.PI / 2f, 0f, 0f)
                     * prop.Prop.Transform;
        expected.Translation = prop.CurrentPosition;
        Assert.Equal(expected, composed);
        // a 2x scale really is in the matrix, not only in the translation
        Assert.Equal(2f, new Vector3(composed.M11, composed.M12, composed.M13).Length(), 3);

        // back to nothing is back to no edit
        prop.Offset = Vector3.Zero; prop.RotationDegrees = Vector3.Zero; prop.Scale = Vector3.One;
        Assert.False(prop.IsMoved);
        Assert.False(prop.HasEdits);
        Assert.Equal(prop.Prop.Transform, prop.CurrentTransform);
    }

    [Fact]
    public void TheDragIsOneUndoStep()
    {
        var prop = Prop();
        var before = PlacementTransformCommand.State.Capture(prop);
        prop.Offset = new Vector3(50f, 0f, 50f);
        prop.RotationDegrees = new Vector3(0f, 45f, 0f);
        prop.Scale = new Vector3(1.5f, 1.5f, 1.5f);
        var after = PlacementTransformCommand.State.Capture(prop);
        Assert.NotEqual(before, after);

        before.ApplyTo(prop);
        Assert.False(prop.IsMoved);
        Assert.Equal(Vector3.Zero, prop.Offset);
        Assert.Equal(Vector3.One, prop.Scale);

        after.ApplyTo(prop);
        Assert.Equal(new Vector3(50f, 0f, 50f), prop.Offset);
        Assert.Equal(new Vector3(0f, 45f, 0f), prop.RotationDegrees);
        Assert.Equal(new Vector3(1.5f, 1.5f, 1.5f), prop.Scale);
    }

    [Fact]
    public void TheMovedTransformReachesTheBinAndNothingElseDoes()
    {
        // a container holding one scenery character, as the writer creates it
        var id = new MapPlacementId(0x5000u, 0xBEEFu);
        var authored = Matrix4x4.CreateTranslation(100f, 20f, 300f);
        var item = new BinTreeStruct(0, 0x9aa5b4bcu, new BinTreeProperty[]
        {
            new BinTreeMatrix44(H("transform"), authored),
            new BinTreeHash(H("name"), H("Golem_1")),
            new BinTreeStruct(H("Character"), H("SkinCharacterGeComponentDef"), new BinTreeProperty[]
            {
                new BinTreeString(H("CharacterRecord"), "Characters/Golem/CharacterRecords/Root"),
                new BinTreeString(H("Skin"), "Characters/Golem/Skins/Skin0"),
            }),
            new BinTreeEmbedded(H("CharacterMesh"), H("CharacterMeshGeComponentDef"), new BinTreeProperty[]
            {
                new BinTreeString(H("IdleAnimationName"), "Idle1"),
            }),
        });
        var items = new BinTreeMap(H("items"), BinPropertyType.Hash, BinPropertyType.Struct,
            new[] { new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, id.ItemKey), item) });
        using var ms = new MemoryStream();
        new BinTree(new[] { new BinTreeObject(id.ContainerHash, H("MapPlaceableContainer"), new BinTreeProperty[] { items }) },
            Array.Empty<string>()).Write(ms);
        byte[] bin = ms.ToArray();

        var prop = Prop(authored);
        prop.Offset = new Vector3(-40f, 5f, 12f);

        byte[]? written = MapPlaceableWriter.WriteEdits(bin, new[]
        {
            new MapPlacementEdit(id) { Transform = prop.IsMoved ? prop.CurrentTransform : null },
        }, out var error);
        Assert.Null(error);
        Assert.NotNull(written);

        var moved = (BinTreeStruct)((BinTreeMap)new BinTree(new MemoryStream(written!, false))
            .Objects[id.ContainerHash].Properties[H("items")])
            .Single(e => ((BinTreeHash)e.Key).Value == id.ItemKey).Value;
        Assert.Equal(new Vector3(60f, 25f, 312f), ((BinTreeMatrix44)moved.Properties[H("transform")]).Value.Translation);
        // and only the transform moved: the name, the character and the idle are the ones it was created with
        Assert.Equal(H("Golem_1"), ((BinTreeHash)moved.Properties[H("name")]).Value);
        Assert.Equal("Characters/Golem/Skins/Skin0",
            ((BinTreeString)((BinTreeStruct)moved.Properties[H("Character")]).Properties[H("Skin")]).Value);
        Assert.Equal("Idle1",
            ((BinTreeString)((BinTreeStruct)moved.Properties[H("CharacterMesh")]).Properties[H("IdleAnimationName")]).Value);

        // an unmoved prop writes no transform at all, so the authored one is left exactly as Riot wrote it
        var still = Prop(authored);
        Assert.Null(new MapPlacementEdit(id) { Transform = still.IsMoved ? still.CurrentTransform : null }.Transform);
    }

    [Fact]
    public void TheGizmoAndTheViewportAreWiredToProps()
    {
        var host = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        if (host is null) return;
        // the gizmo has a target, a start state, an undo capture and a drag for a selected prop
        Assert.Contains("|| SelectedPropNode is not null;", host);
        Assert.Contains(": SelectedPropNode is { } r ? (r.Offset, r.RotationDegrees, r.Scale)", host);
        Assert.Contains("?? SelectedPropNode;", host);
        Assert.Contains("else if (SelectedPropNode is { } r)", host);
        Assert.Contains("GizmoPivot = p.CurrentPosition;        // M699: the gizmo drives props too", host);
        // the drag moves the already-decoded instances rather than decoding every mesh again
        Assert.Contains("private void RefreshPropInstanceTransforms()", host);
        Assert.Contains("PropInstanceData.Place(_propInstances[i].Mesh, _propInstanceOwners[i].CurrentTransform)", host);
        Assert.Contains("_propInstanceOwners = owners.Select(i => visible[i]).ToList();", host);
        // and the save carries the moved matrix
        Assert.Contains("Transform = p.IsMoved ? p.CurrentTransform : null,", host);
    }

    private static string? Source(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        return null;
    }
}
