using Avalonia;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.ViewModels;
using ReyEngine.App.Views;
using ReyEngine.Core.Assets;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M718: the particle editor's curve pane became draggable.
///
/// <para>The control itself needs a window, so what it decides is kept in <see cref="CurveTransform"/>,
/// <see cref="CurveEditing"/> and <see cref="CurveKeyDrag"/> and pinned here. The one rule that makes this
/// its own milestone is the commit count: a drag moves a copy and commits ONCE, because the commit
/// re-serializes the whole bin. The headless UiProbe drives a real pointer drag through the view for the
/// wiring.</para>
///
/// <para>The fixture is synthetic, so these run on a machine without the troybin corpus that
/// <see cref="ParticleEditorSpreadUiTests"/> needs.</para>
/// </summary>
public sealed class ParticleCurveEditorTests
{
    private static uint H(string s) => ReyEngine.Core.Hashing.HashAlgorithms.Fnv1a(s);

    /// <summary>A one-emitter system whose <c>rate</c> runs over the keys (0,1) (0.5,4) (1,2).</summary>
    private static byte[] Bin()
    {
        var dynamics = new BinTreeStruct(H("dynamics"), H("VfxAnimatedFloatVariableData"), new BinTreeProperty[]
        {
            new BinTreeContainer(H("times"), BinPropertyType.F32,
                new BinTreeProperty[] { new BinTreeF32(0, 0f), new BinTreeF32(0, 0.5f), new BinTreeF32(0, 1f) }),
            new BinTreeContainer(H("values"), BinPropertyType.F32,
                new BinTreeProperty[] { new BinTreeF32(0, 1f), new BinTreeF32(0, 4f), new BinTreeF32(0, 2f) }),
        });
        var emitter = new BinTreeStruct(0, H("VfxEmitterDefinitionData"), new BinTreeProperty[]
        {
            new BinTreeString(H("emitterName"), "e"),
            new BinTreeEmbedded(H("rate"), H("ValueFloat"), new BinTreeProperty[]
            {
                new BinTreeF32(H("constantValue"), 1f),
                dynamics,
            }),
        });
        var system = new BinTreeObject(H("sys"), H("VfxSystemDefinitionData"), new BinTreeProperty[]
        {
            new BinTreeString(H("particleName"), "sys"),
            new BinTreeContainer(H("complexEmitterDefinitionData"), BinPropertyType.Struct,
                new BinTreeProperty[] { emitter }),
        });
        using var ms = new MemoryStream();
        new BinTree(new[] { system }, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    private static string? Name(uint hash)
    {
        foreach (string n in new[]
        {
            "emitterName", "rate", "constantValue", "dynamics", "times", "values",
            "particleName", "complexEmitterDefinitionData",
        })
            if (H(n) == hash) return n;
        return null;
    }

    /// <summary>The editor with the fixture loaded and the curve row selected, as the pane shows it.</summary>
    private static (ParticleEditorViewModel Vm, ParticlePropertyRowViewModel Row) Editor(bool editable)
    {
        var vm = new ParticleEditorViewModel { ResolveBinName = Name };
        Assert.True(vm.Load(new WadAssetEntry { Path = "particles.bin" }, Bin(), editable));
        vm.SelectedSystem = vm.Systems[0];
        var row = vm.Cards.SelectMany(c => c.Modules).SelectMany(m => m.Rows).Single(r => r.Prop.Name == "rate" && r.HasCurve);
        vm.SelectedProperty = row;
        return (vm, row);
    }

    private static readonly float[] Times = { 0f, 0.5f, 1f };
    private static readonly float[][] OneChannel = { new[] { 1f, 4f, 2f } };
    private static bool All(int _) => true;

    // ================================================================ the transform

    [Fact]
    public void TheFitPutsAKeyWhereTheOldPreviewDrewIt()
    {
        var xf = CurveTransform.Fit(Times, OneChannel, new Size(200, 100))!.Value;

        // CurvePreview's own arithmetic, written out: range 1..4 padded 8% each way, time 0..1 across
        float min = 1f - 3f * 0.08f, max = 4f + 3f * 0.08f;
        Assert.Equal(min, xf.Min, 5);
        Assert.Equal(max, xf.Max, 5);
        var p = xf.ToScreen(0.5f, 4f);
        Assert.Equal(100, p.X, 3);
        Assert.Equal(100 - (4f - min) / (max - min) * 100, p.Y, 3);

        // a flat curve still opens to a visible band, and a single key still gets a unit of time
        var flat = CurveTransform.Fit(new[] { 0.3f }, new[] { new[] { 2f } }, new Size(10, 10))!.Value;
        Assert.True(flat.Max - flat.Min > 0.9f);
        Assert.Equal(1f, flat.T1 - flat.T0, 5);
    }

    [Fact]
    public void ScreenAndCurveSpaceAreOneMappingBothWays()
    {
        var xf = CurveTransform.Fit(Times, OneChannel, new Size(317, 143))!.Value;
        foreach (var (t, v) in new[] { (0f, 1f), (0.25f, 3.3f), (1f, -2f) })
        {
            var (bt, bv) = xf.ToCurve(xf.ToScreen(t, v));
            Assert.Equal(t, bt, 4);
            Assert.Equal(v, bv, 4);
        }
    }

    [Fact]
    public void ZoomHoldsThePointUnderTheCursorAndPanFollowsThePointer()
    {
        var xf = CurveTransform.Fit(Times, OneChannel, new Size(200, 100))!.Value;
        var anchor = new Point(50, 30);
        var before = xf.ToCurve(anchor);

        var zoomed = xf.Zoom(anchor, 0.5, 0.5);
        var after = zoomed.ToCurve(anchor);
        Assert.Equal(before.Time, after.Time, 4);
        Assert.Equal(before.Value, after.Value, 4);
        Assert.Equal((xf.T1 - xf.T0) * 0.5f, zoomed.T1 - zoomed.T0, 4);

        // Ctrl zooms time alone: the value range must not move
        var timeOnly = xf.Zoom(anchor, 0.5, 1);
        Assert.Equal(xf.Min, timeOnly.Min);
        Assert.Equal(xf.Max, timeOnly.Max);

        // a key dragged 20px right by the pan lands 20px right on screen
        var key = xf.ToScreen(0.5f, 4f);
        var panned = xf.Pan(new Vector(20, -10)).ToScreen(0.5f, 4f);
        Assert.Equal(key.X + 20, panned.X, 3);
        Assert.Equal(key.Y - 10, panned.Y, 3);
    }

    // ================================================================ picking

    [Fact]
    public void AKeyIsPickedWhereItIsPaintedAndAHiddenChannelIsNot()
    {
        float[][] two = { new[] { 1f, 4f, 2f }, new[] { 1f, 4f, 3f } };   // channels meet at keys 0 and 1
        var xf = CurveTransform.Fit(Times, two, new Size(200, 100))!.Value;
        var shared = xf.ToScreen(0.5f, 4f);

        // painted last, so the one the user sees on top
        Assert.Equal((1, 1), CurveEditing.HitTest(xf, Times, two, All, shared + new Vector(3, 2)));
        // a hidden channel is not drawn with dots and must not be grabbed through the one that is
        Assert.Equal((1, 0), CurveEditing.HitTest(xf, Times, two, c => c == 0, shared));
        // off the dot is off the key
        Assert.Null(CurveEditing.HitTest(xf, Times, two, All, shared + new Vector(CurveEditing.PickRadius + 2, 0)));
    }

    // ================================================================ dragging

    [Fact]
    public void ADraggedKeyStopsAtItsNeighboursAndTheEndsStayInTheLifetime()
    {
        var mid = new CurveKeyDrag(Times, OneChannel, 1, 0);
        mid.MoveTo(0.9f, 5f);
        Assert.Equal(0.9f, mid.Time);
        mid.MoveTo(3f, 5f);
        Assert.Equal(1f, mid.Time);     // not past the last key: the keys would reorder
        mid.MoveTo(-3f, 5f);
        Assert.Equal(0f, mid.Time);
        Assert.Equal(5f, mid.Value);    // values are free - colours run past 1 in Riot's own data

        var first = new CurveKeyDrag(Times, OneChannel, 0, 0);
        first.MoveTo(-0.5f, 1f);
        Assert.Equal(0f, first.Time);
        var last = new CurveKeyDrag(Times, OneChannel, 2, 0);
        last.MoveTo(1.5f, 1f);
        Assert.Equal(1f, last.Time);

        // a key Riot already placed outside the lifetime is not pulled in just by being touched
        var outside = new CurveKeyDrag(new[] { 0f, 1.2f }, new[] { new[] { 0f, 1f } }, 1, 0);
        outside.MoveTo(1.3f, 1f);
        Assert.Equal(1.2f, outside.Time);

        // out-of-order keys give reversed bounds; that must not throw mid-drag
        var reversed = new CurveKeyDrag(new[] { 0.8f, 0.5f, 0.2f }, new[] { new[] { 0f, 0f, 0f } }, 1, 0);
        Assert.Null(Record.Exception(() => reversed.MoveTo(0.5f, 0f)));
    }

    [Fact]
    public void ADragMovesACopyAndAClickMovesNothing()
    {
        float[] times = (float[])Times.Clone();
        float[][] channels = { (float[])OneChannel[0].Clone() };
        var drag = new CurveKeyDrag(times, channels, 1, 0);
        Assert.False(drag.Moved);

        drag.MoveTo(0.6f, 7f);
        Assert.True(drag.Moved);
        Assert.Equal(new[] { 1f, 7f, 2f }, drag.Channels[0]);
        // the arrays the row handed over are untouched until the commit replaces them
        Assert.Equal(new[] { 0f, 0.5f, 1f }, times);
        Assert.Equal(new[] { 1f, 4f, 2f }, channels[0]);
        Assert.Equal(new[] { 7f }, drag.Components());
    }

    [Fact]
    public void ANewKeySitsOnTheLinesItWasNotAddedTo()
    {
        float[][] rgba = { new[] { 0f, 1f }, new[] { 1f, 0f }, new[] { 0.5f, 0.5f }, new[] { 1f, 1f } };
        float[] t = { 0f, 1f };
        var comps = CurveEditing.NewKey(t, rgba, 0.25f, channel: 0, value: 0.9f);
        Assert.Equal(new[] { 0.9f, 0.75f, 0.5f, 1f }, comps);

        Assert.Equal(1f, CurveEditing.Evaluate(Times, OneChannel[0], -1f));   // held flat past the ends
        Assert.Equal(2f, CurveEditing.Evaluate(Times, OneChannel[0], 9f));
        Assert.Equal(2.5f, CurveEditing.Evaluate(Times, OneChannel[0], 0.25f));

        var xf = CurveTransform.Fit(t, rgba, new Size(200, 100))!.Value;
        Assert.Equal(1, CurveEditing.NearestChannel(xf, t, rgba, All, xf.ToScreen(0.1f, 0.88f)));
        Assert.Equal(-1, CurveEditing.NearestChannel(xf, t, rgba, _ => false, new Point(1, 1)));
    }

    [Fact]
    public void TheInsertionIndexIsTheOneTheDocumentUses()
    {
        var (_, row) = Editor(editable: true);
        foreach (float at in new[] { 0f, 0.25f, 0.5f, 0.75f })
        {
            var before = row.Prop.CurveTimes!;
            int predicted = CurveEditing.InsertionIndex(before, at);
            row.AddKey(at, new[] { 9f });
            Assert.Null(row.ErrorText);
            Assert.Equal(at, row.Prop.CurveTimes![predicted]);
            Assert.Equal(9f, row.Prop.CurveChannels![0][predicted]);
        }
    }

    // ================================================================ the commit

    [Fact]
    public void ADragCommitsOnceNotOncePerMove()
    {
        var (vm, row) = Editor(editable: true);
        int dirtied = 0;
        vm.MarkDocumentDirty = () => dirtied++;

        // sixty pointer moves, a second of dragging at 60 Hz
        var drag = new CurveKeyDrag(row.CurveTimes!, row.CurveChannels!, 1, 0);
        for (int i = 1; i <= 60; i++) drag.MoveTo(0.5f + i * 0.004f, 4f - i * 0.05f);
        Assert.Equal(0, dirtied);
        Assert.Equal(4f, row.Prop.CurveChannels![0][1]);   // the document has not seen any of it

        row.SetKey(drag.Index, drag.Time, drag.Components());
        Assert.Equal(1, dirtied);
        Assert.Null(row.ErrorText);
        Assert.Equal(0.74f, row.Prop.CurveTimes![1], 4);
        Assert.Equal(1f, row.Prop.CurveChannels![0][1], 4);
        Assert.True(row.Prop.IsDirty);

        // and it survives serialization, which is where an edit either happened or did not
        var reread = ReyEngine.Formats.Particles.ParticleDocument.Parse(vm.Document!.Serialize(), Name)!;
        var after = reread.Systems[0].Emitters[0].Properties.Single(p => p.Name == "rate" && p.HasCurve);
        Assert.Equal(1f, after.CurveChannels![0][1], 4);
    }

    [Fact]
    public void TheTypedKeyListStillCommitsOnce()
    {
        // the same assertion ParticleEditorSpreadUiTests makes on the corpus, here without it: the list and
        // the graph are two routes to one EditCurve, and one user action must be one commit
        var (vm, row) = Editor(editable: true);
        int dirtied = 0;
        vm.MarkDocumentDirty = () => dirtied++;

        row.CurveKeys[1].ValueText = "3";
        row.CurveKeys[1].ApplyKeyCommand.Execute(null);
        Assert.Equal(1, dirtied);
        Assert.Equal(3f, row.Prop.CurveChannels![0][1]);

        row.DeleteKey(1);
        Assert.Equal(2, dirtied);
        Assert.Equal(2, row.CurveKeys.Count);
    }

    [Fact]
    public void AReadOnlyBinsGraphHasNothingToDragAndItsCommitIsRefused()
    {
        var (vm, row) = Editor(editable: false);
        int dirtied = 0;
        vm.MarkDocumentDirty = () => dirtied++;

        // the control is handed this as IsEditable, and draws rings that cannot be picked up
        Assert.False(row.CanEditCurve);

        // and should a commit get through anyway, the owner still refuses it - out loud
        row.SetKey(1, 0.5f, new[] { 9f });
        Assert.Equal(0, dirtied);
        Assert.Equal(4f, row.Prop.CurveChannels![0][1]);
        Assert.Contains("Read-only", row.ErrorText!);

        string? view = Source("src", "ReyEngine.App", "Views", "ParticleEditorView.axaml");
        if (view is null) return;
        Assert.Contains("IsEditable=\"{Binding SelectedProperty.CanEditCurve, FallbackValue=False}\"", view);
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
