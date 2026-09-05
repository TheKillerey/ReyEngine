using System.Numerics;
using System.Reflection;
using System.Text.RegularExpressions;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Undo;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M644: bulk editing of placed particles. The shared inspector's arithmetic is pure over the view
/// models, so it is asserted without a map: what a selection has in common, that an edit writes every
/// placement as ONE undo step, and that undo restores each one exactly.
/// </summary>
public sealed class ParticleBulkEditTests
{
    private static string? Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static ParticlePlacementViewModel Node(string name, uint system, Vector4? tint = null) => new()
    {
        Placement = new MapParticlePlacement(name, Vector3.Zero, Matrix4x4.Identity,
            "maps/particles/" + name, "group", SystemHash: system, ColorModulate: tint, Id: new MapPlacementId(1, 2)),
    };

    // ===================================================== shared vs mixed

    [Fact]
    public void ASelectionKnowsWhatItsPlacementsHaveInCommon()
    {
        var agree = ParticleBatch.Summarize(new[] { Node("a", 1), Node("b", 1) });
        Assert.Equal(1u, agree.SharedSystemHash);
        Assert.Single(agree.SystemHashes);
        Assert.Equal("", agree.SharedTintText);      // both authored-default
        Assert.False(agree.TintMixed);

        var differ = ParticleBatch.Summarize(new[] { Node("a", 1), Node("b", 2, new Vector4(1f, 1f, 1f, 0.5f)) });
        Assert.Null(differ.SharedSystemHash);
        Assert.Equal(2, differ.SystemHashes.Count);
        Assert.Null(differ.SharedTintText);
        Assert.True(differ.TintMixed);
        Assert.Contains("2 systems", differ.Status);

        // A pending edit counts through the EFFECTIVE values, the way the viewport draws them.
        var b = Node("b", 2);
        b.EditedSystemHash = 1;
        var edited = ParticleBatch.Summarize(new[] { Node("a", 1), b });
        Assert.Equal(1u, edited.SharedSystemHash);
        Assert.Equal(1, edited.Edited);
    }

    // ===================================================== one undo step

    [Fact]
    public void RelinkingWritesEveryPlacementAndUndoesAsOneStep()
    {
        var nodes = new[] { Node("a", 1), Node("b", 2), Node("c", 1) };
        int refreshed = 0;
        var undo = new UndoRedoService();

        var command = ParticleBatch.Relink(nodes, 9, context: null, refresh: () => refreshed++);
        Assert.All(nodes, n => Assert.Equal(9u, n.EffectiveSystemHash));
        Assert.All(nodes, n => Assert.True(n.IsRelinked));
        Assert.Equal(3, command.Count);
        undo.PushApplied(command);

        Assert.True(undo.Undo());
        Assert.Equal(new uint[] { 1, 2, 1 }, nodes.Select(n => n.EffectiveSystemHash));
        Assert.All(nodes, n => Assert.False(n.HasEdits));
        Assert.Equal(3, refreshed);   // once per placement put back

        Assert.True(undo.Redo());
        Assert.All(nodes, n => Assert.Equal(9u, n.EffectiveSystemHash));

        // Re-linking to a system a placement was AUTHORED with clears that placement's edit rather than
        // recording a no-op, exactly as the single picker does - and only the ones that changed are in
        // the step.
        var back = ParticleBatch.Relink(nodes, 1, null, () => { });
        Assert.Equal(0u, nodes[0].EditedSystemHash);
        Assert.Equal(1u, nodes[1].EditedSystemHash);
        Assert.Equal(0u, nodes[2].EditedSystemHash);
        Assert.Equal(3, back.Count);   // all three moved off 9

        var same = ParticleBatch.Relink(nodes, 1, null, () => { });
        Assert.Equal(0, same.Count);   // nothing changed: nothing to undo
    }

    [Fact]
    public void TintAppliesToEveryPlacementAndRefusesWhatItCannotParse()
    {
        Assert.True(ParticleBatch.IsValidTint(""));                    // empty = back to authored
        Assert.True(ParticleBatch.IsValidTint("1, 0.5, 0.25, 1"));
        Assert.False(ParticleBatch.IsValidTint("1, 0.5"));
        Assert.False(ParticleBatch.IsValidTint("a, b, c, d"));
        Assert.Equal("1, 0.5, 0.25, 1", ParticleBatch.TintText(new Vector4(1f, 0.5f, 0.25f, 1f)));   // InvariantCulture

        var nodes = new[] { Node("a", 1), Node("b", 1, new Vector4(0.5f, 0.5f, 0.5f, 1f)) };
        var command = ParticleBatch.Tint(nodes, " 1, 0.5, 0.25, 1 ", null, () => { });
        Assert.Equal(2, command.Count);
        Assert.All(nodes, n => Assert.Equal(new Vector4(1f, 0.5f, 0.25f, 1f), n.ParsedTint));
        Assert.All(nodes, n => Assert.Equal("1, 0.5, 0.25, 1", n.EditedTint));

        command.Undo();
        Assert.All(nodes, n => Assert.Null(n.EditedTint));
        Assert.Equal(new Vector4(0.5f, 0.5f, 0.5f, 1f), nodes[1].EffectiveTint);   // the authored tint again

        var clear = ParticleBatch.Tint(nodes, "", null, () => { });
        Assert.Equal(0, clear.Count);   // already authored-default: nothing changed
    }

    [Fact]
    public void ResetDiscardsEveryPendingEditAndUndoBringsThemBack()
    {
        var nodes = new[] { Node("a", 1), Node("b", 2) };
        nodes[0].EditedSystemHash = 5;
        nodes[0].Offset = new Vector3(10f, 0f, 0f);
        nodes[1].EditedTint = "1, 1, 1, 0.5";
        nodes[1].IsRemoved = true;
        Assert.All(nodes, n => Assert.True(n.HasEdits));

        var command = ParticleBatch.Reset(nodes, null, () => { });
        Assert.Equal(2, command.Count);
        Assert.All(nodes, n => Assert.False(n.HasEdits));

        command.Undo();
        Assert.Equal(5u, nodes[0].EditedSystemHash);
        Assert.Equal(new Vector3(10f, 0f, 0f), nodes[0].Offset);
        Assert.Equal("1, 1, 1, 0.5", nodes[1].EditedTint);
        Assert.True(nodes[1].IsRemoved);
    }

    // ===================================================== the wiring is where it says it is

    [Fact]
    public void TheBatchCardBindsOnlyToPropertiesThatExist()
    {
        // SceneObjectInspectorView has no compiled bindings, so a misspelt path shows nothing and stays
        // green (memory: XAML bindings are runtime). Every binding in the card is checked by reflection.
        var xaml = Source("src", "ReyEngine.App", "Views", "SceneObjectInspectorView.axaml");
        if (xaml is null) return;
        int start = xaml.IndexOf("<!-- M644: PARTICLES (batch)", StringComparison.Ordinal);
        Assert.True(start >= 0, "the batch card is not in the inspector");
        int end = xaml.IndexOf("<!-- SOUND (MapAudio placement)", start, StringComparison.Ordinal);
        string card = xaml[start..end];

        // The card's own bindings resolve on the main view model; the picker's DisplayMemberBinding
        // resolves on the system row it displays.
        Type[] contexts = { typeof(MainWindowViewModel), typeof(VfxSystemItemViewModel) };
        var missing = new List<string>();
        foreach (Match m in Regex.Matches(card, @"\{Binding\s+!?([A-Za-z_][A-Za-z0-9_]*)"))
        {
            string path = m.Groups[1].Value;
            if (!contexts.Any(t => t.GetProperty(path, BindingFlags.Public | BindingFlags.Instance) is not null))
                missing.Add(path);
        }
        Assert.True(missing.Count == 0, "bound in the PARTICLES batch card but not on MainWindowViewModel: " + string.Join(", ", missing.Distinct()));

        // The single card yields to the batch card, rather than both showing for the last click.
        Assert.Contains("IsVisible=\"{Binding ShowSingleParticleCard}\"", xaml);
        Assert.Contains("IsVisible=\"{Binding IsParticleMultiSelect}\"", card);
    }

    [Fact]
    public void AWholeSystemCanBeSelectedFromTheOutliner()
    {
        var outliner = Source("src", "ReyEngine.App", "Views", "MapOutlinerView.axaml");
        if (outliner is null) return;
        Assert.Contains("SelectParticleGroupCommand", outliner);
        Assert.NotNull(typeof(MainWindowViewModel).GetProperty("SelectParticleGroupCommand"));
        Assert.NotNull(typeof(MainWindowViewModel).GetMethod("SelectMapContentItems"));
    }

    [Fact]
    public void TheSelectionDrivesTheBatch()
    {
        // The host derives the batch from the map-content selection on every selection change, and the
        // batch edits push ONE composite; both are pinned in source because a map is needed to run them.
        var main = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        var bulk = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.BulkParticles.cs");
        if (main is null || bulk is null) return;
        Assert.Contains("RefreshParticleBatch();", main);
        Assert.Contains("UndoService.PushApplied(command);", bulk);
        Assert.Contains("ParticleBatch.Relink(", bulk);
        Assert.Contains("ParticleBatch.Tint(", bulk);
        Assert.Contains("ParticleBatch.Reset(", bulk);
    }
}
