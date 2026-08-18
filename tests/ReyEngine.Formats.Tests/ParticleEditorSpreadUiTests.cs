using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Formats.Particles;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M523: the spread rows as the editor actually presents them.
///
/// <para><see cref="ParticleSpreadRowTests"/> pins the document layer. This one goes through the view
/// model the cards bind to, because a row that exists in the document and never reaches a card is not
/// something the user can see or edit - and the card path builds its own module groups and curve-key
/// collections on top.</para>
///
/// <para>The fixture is a real converted troybin rather than a synthetic bin, so it also proves the end
/// to end route the user takes: legacy file -> converter -> editor.</para>
/// </summary>
public sealed class ParticleEditorSpreadUiTests
{
    private const string Corpus = @"K:\LeagueSandbox\League_Sandbox_Client\DATA\Particles";

    /// <summary>The editor, loaded with a converted legacy effect. Null when the corpus is absent.</summary>
    private static ParticleEditorViewModel? Editor(string legacyName)
    {
        string path = Path.Combine(Corpus, legacyName + ".troybin");
        if (!File.Exists(path)) return null;
        Assert.True(TroyBinFile.TryParse(File.ReadAllBytes(path), out var troy, out var error), error);

        var converted = TroyBinConverter.Convert(troy!, legacyName, "Particles/" + legacyName);
        var vm = new ParticleEditorViewModel { ResolveBinName = Name };
        Assert.True(vm.Load(new WadAssetEntry { Path = "particles.bin" }, converted.BinBytes, editable: true));
        vm.SelectedSystem = vm.Systems[0];
        return vm;
    }

    /// <summary>The host normally hands the editor its hash dictionary; without one every field shows as
    /// a bare hash, so the test supplies the names it asserts on.</summary>
    private static string? Name(uint hash)
    {
        foreach (string n in new[]
        {
            "emitterName", "rate", "particleLifetime", "birthVelocity", "birthScale0", "constantValue",
            "dynamics", "probabilityTables", "keyTimes", "keyValues", "times", "values",
            "fieldCollectionDefinition", "fieldDragDefinitions", "fieldAccelerationDefinitions",
            "fieldAttractionDefinitions", "fieldOrbitalDefinitions", "fieldNoiseDefinitions",
            "strength", "radius", "acceleration", "direction", "Position",
        })
            if (ReyEngine.Core.Hashing.HashAlgorithms.Fnv1a(n) == hash) return n;
        return null;
    }

    private static IEnumerable<ParticlePropertyRowViewModel> Rows(ParticleEditorViewModel vm, string emitter)
        => vm.Cards.Single(c => c.Name == emitter).Modules.SelectMany(m => m.Rows);

    [Fact]
    public void TheSpreadRowsReachTheCards()
    {
        var vm = Editor("FireTorch_Simple");
        if (vm is null) return;

        var rows = Rows(vm, "Flame").ToList();

        // particleLifetime has p-lifeP1..3 behind it - three keys of randomisation that the card showed
        // nothing of before, so the emitter read as "every particle lives exactly 2 seconds".
        var life = rows.Single(r => r.Prop.Name == "particleLifetime");
        var spread = rows.Single(r => r.Prop.Name == "spread" && r.Prop.Module == life.Prop.Module);
        Assert.Equal(3, spread.CurveKeys.Count);
        Assert.True(spread.HasCurve);
        Assert.True(spread.CanEditCurve);
    }

    [Fact]
    public void APerAxisSpreadIsLabelledOnTheCard()
    {
        var vm = Editor("FireTorch_Simple");
        if (vm is null) return;

        // birthVelocity randomises Y alone (*p-velYP1/YP2), and the label has to say WHICH axis or the
        // row is unreadable - the container is positional, so the name is the only clue.
        var names = Rows(vm, "Flame")
            .Where(r => r.Prop.Name.StartsWith("spread") && r.Prop.Module == "Birth")
            .Select(r => r.Prop.Name).ToList();
        Assert.Contains("spread Y", names);
    }

    [Fact]
    public void EditingASpreadKeyThroughTheCardMarksTheDocumentDirty()
    {
        var vm = Editor("FireTorch_Simple");
        if (vm is null) return;

        int dirtied = 0;
        vm.MarkDocumentDirty = () => dirtied++;

        var spread = Rows(vm, "Flame").First(r => r.Prop.Name.StartsWith("spread"));
        vm.SelectedProperty = spread;
        spread.CurveKeys[0].ValueText = "0.5";
        spread.CurveKeys[0].ApplyKeyCommand.Execute(null);

        // the same route a scalar edit takes: without it the edit lives only in the row and the host
        // never learns the document changed, so the save button stays dark
        Assert.Equal(1, dirtied);
        Assert.Null(spread.ErrorText);
        Assert.Equal(0.5f, spread.Prop.CurveChannels![0][0]);
    }

    [Fact]
    public void ASpreadRowSaysWhyItHasNoValueBox()
    {
        var vm = Editor("FireTorch_Simple");
        if (vm is null) return;

        var spread = Rows(vm, "Flame").First(r => r.Prop.Name.StartsWith("spread"));
        Assert.True(spread.IsReadOnly);

        // committing a scalar into a table is meaningless, and the message has to say that rather than
        // the old blanket "this property type isn't editable yet"
        vm.ApplyEditCommand.Execute(spread);
        Assert.Equal(spread.ReadOnlyReason, spread.ErrorText);
        Assert.Contains("probability", spread.ErrorText!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheForceFieldsAreOnTheCardsToo()
    {
        var vm = Editor("FireTorch_Simple");
        if (vm is null) return;

        // M522 writes these; the generic nested-struct expansion (M189) already renders them, and the
        // leaf values are editable. This test exists so a change to that expansion cannot quietly take
        // the whole subsystem off the card again.
        var rows = Rows(vm, "Flame").ToList();
        Assert.Contains(rows, r => r.Prop.Name == "fieldCollectionDefinition");
        var strength = rows.Single(r => r.Prop.Name == "strength");
        Assert.False(strength.IsReadOnly);
        Assert.Equal("0.1", strength.Prop.CurrentText);
    }
}
