using System.Numerics;
using ReyEngine.App.ViewModels;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M204: the placement view model that backs the inspector's rename / tint / re-link / delete controls.
/// <see cref="MapPlaceableWriter"/> has supported these verbs since M199 and nothing could reach them.
///
/// <para>The property under test throughout is "untouched means untouched": a field the user did not edit
/// must stay null so the writer leaves it alone. Getting that wrong would turn a rename into a rename
/// plus a transform rewrite plus a tint reset.</para>
/// </summary>
public class PlacementEditVerbTests
{
    private static ParticlePlacementViewModel Vm(Vector4? authoredTint = null) => new()
    {
        Placement = new MapParticlePlacement(
            "SRUAP_Chaos_Inhibitor_runeTimer_mid1", new Vector3(1f, 2f, 3f),
            Matrix4x4.CreateTranslation(1f, 2f, 3f), "Maps/Particles/x", "grp",
            SystemHash: 0x1234u, ColorModulate: authoredTint,
            Id: new MapPlacementId(0x5000u, 0xAAAAu)),
    };

    [Fact]
    public void AFreshPlacementHasNoEdits()
    {
        var vm = Vm();
        Assert.False(vm.HasEdits);
        Assert.Null(vm.ParsedTint);
        Assert.False(vm.IsRemoved);
    }

    [Fact]
    public void RetypingTheSameNameIsNotAnEdit()
    {
        // Otherwise clicking into the box and out again would mark the whole map dirty.
        var vm = Vm();
        vm.EditedName = vm.Placement.Name;
        Assert.False(vm.HasEdits);
    }

    [Fact]
    public void RenamingIsAnEdit()
    {
        var vm = Vm();
        vm.EditedName = "renamed";
        Assert.True(vm.HasEdits);
    }

    [Theory]
    [InlineData("1, 0.97, 0.84, 0.6")]
    [InlineData("1,0.97,0.84,0.6")]
    [InlineData("  1 , 0.97 , 0.84 , 0.6  ")]
    public void ATintParsesRegardlessOfSpacing(string text)
    {
        var vm = Vm();
        vm.EditedTint = text;
        Assert.NotNull(vm.ParsedTint);
        var t = vm.ParsedTint!.Value;
        Assert.Equal(1f, t.X, 3);
        Assert.Equal(0.97f, t.Y, 3);
        Assert.Equal(0.84f, t.Z, 3);
        Assert.Equal(0.6f, t.W, 3);
        Assert.False(vm.TintIsInvalid);
        Assert.True(vm.HasEdits);
    }

    [Theory]
    [InlineData("1, 1, 1")]          // three components
    [InlineData("1, 1, 1, 1, 1")]    // five
    [InlineData("red")]
    [InlineData("1; 1; 1; 1")]
    public void ABadTintIsFlaggedRatherThanSilentlyDropped(string text)
    {
        var vm = Vm();
        vm.EditedTint = text;
        Assert.Null(vm.ParsedTint);
        Assert.True(vm.TintIsInvalid, "the inspector must be able to say the text is wrong");
        Assert.False(vm.HasEdits, "an unparseable tint must not count as a pending edit");
    }

    [Fact]
    public void TintParsingUsesInvariantCultureNotTheMachineLocale()
    {
        // This machine runs a German locale, where "0,5" is one half. Parsing that way would turn a
        // four-component tint into eight tokens.
        var vm = Vm();
        vm.EditedTint = "0.5, 0.25, 0.125, 1";
        Assert.Equal(0.5f, vm.ParsedTint!.Value.X, 3);
        Assert.Equal(0.25f, vm.ParsedTint!.Value.Y, 3);
    }

    [Fact]
    public void EffectiveTintPrefersThePendingEditThenFallsBackToTheAuthoredOne()
    {
        var authored = new Vector4(1f, 1f, 1f, 0.5f);
        var vm = Vm(authored);
        Assert.Equal(authored, vm.EffectiveTint);        // nothing pending yet

        vm.EditedTint = "1, 0, 0, 1";
        Assert.Equal(new Vector4(1f, 0f, 0f, 1f), vm.EffectiveTint);

        vm.EditedTint = "nonsense";
        Assert.Equal(authored, vm.EffectiveTint);        // a bad edit must not blank the render
    }

    [Fact]
    public void MarkingForDeletionIsAnEdit()
    {
        var vm = Vm();
        vm.IsRemoved = true;
        Assert.True(vm.HasEdits);
    }

    [Fact]
    public void ResetClearsEveryVerbNotJustTheMove()
    {
        var vm = Vm();
        vm.Offset = new Vector3(10f, 0f, 0f);
        vm.EditedName = "renamed";
        vm.EditedTint = "1, 0, 0, 1";
        vm.EditedSystemHash = 0x9999u;
        vm.IsRemoved = true;
        Assert.True(vm.HasEdits);

        vm.ResetEdits();

        Assert.False(vm.HasEdits);
        Assert.False(vm.IsMoved);
        Assert.Null(vm.EditedName);
        Assert.Null(vm.ParsedTint);
        Assert.Equal(0u, vm.EditedSystemHash);
        Assert.False(vm.IsRemoved);
    }

    [Fact]
    public void RelinkingToADifferentSystemIsAnEdit()
    {
        var vm = Vm();
        vm.EditedSystemHash = 0x9999u;
        Assert.True(vm.IsRelinked);
        Assert.True(vm.HasEdits);
    }

    [Fact]
    public void RelinkingToTheSystemItAlreadyUsesIsNotAnEdit()
    {
        // The picker starts pointed at the current system, so opening the dropdown and re-choosing the
        // same entry must not dirty the map. MainWindowViewModel clears the hash for this case; the model
        // also refuses to call it a re-link if the hash is set to the authored one directly.
        var vm = Vm();
        vm.EditedSystemHash = vm.Placement.SystemHash;
        Assert.False(vm.IsRelinked);
    }

    [Fact]
    public void ARelinkProducesASystemLinkAndNothingElse()
    {
        var vm = Vm();
        vm.EditedSystemHash = 0xBEEFu;

        var edit = new MapPlacementEdit(vm.Placement.Id)
        {
            Transform = vm.IsMoved ? vm.CurrentTransform : null,
            Name = vm.EditedName is { } n && n != vm.Placement.Name ? n : null,
            ColorModulate = vm.ParsedTint,
            SystemLink = vm.EditedSystemHash != 0 ? vm.EditedSystemHash : null,
            Remove = vm.IsRemoved,
        };

        Assert.Equal(0xBEEFu, edit.SystemLink);
        Assert.Null(edit.Transform);
        Assert.Null(edit.Name);
        Assert.Null(edit.ColorModulate);
        Assert.False(edit.Remove);
    }

    [Fact]
    public void AnEditedPlacementProducesAWriterEditCarryingOnlyWhatChanged()
    {
        // The shape MainWindowViewModel builds: untouched verbs stay null so the writer skips them.
        var vm = Vm();
        vm.EditedName = "renamed";

        var edit = new MapPlacementEdit(vm.Placement.Id)
        {
            Transform = vm.IsMoved ? vm.CurrentTransform : null,
            Name = vm.EditedName is { } n && n != vm.Placement.Name ? n : null,
            ColorModulate = vm.ParsedTint,
            SystemLink = vm.EditedSystemHash != 0 ? vm.EditedSystemHash : null,
            Remove = vm.IsRemoved,
        };

        Assert.Equal("renamed", edit.Name);
        Assert.Null(edit.Transform);        // a rename must not rewrite the transform
        Assert.Null(edit.ColorModulate);
        Assert.Null(edit.SystemLink);
        Assert.False(edit.Remove);
    }
}
