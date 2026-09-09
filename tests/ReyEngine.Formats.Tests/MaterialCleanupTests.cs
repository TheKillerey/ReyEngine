using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M675: removing unused materials from the Materials window.
///
/// <para>Two halves. The Formats half is the plan: what the bin's object graph says is unreachable, what
/// a link keeps although no mesh draws it, and how the window's ticks narrow the removal. The App half is
/// the window: which rows it offers, and that it only reloads when the app reports a change.</para>
/// </summary>
public sealed class MaterialCleanupTests
{
    private static uint H(string value) => HashAlgorithms.Fnv1a(value);

    private static byte[] Write(params BinTreeObject[] objects)
    {
        using var stream = new MemoryStream();
        new BinTree(objects, Array.Empty<string>()).Write(stream);
        return stream.ToArray();
    }

    private static BinTreeObject Material(string name) =>
        new(H(name), H("StaticMaterialDef"), Array.Empty<BinTreeProperty>());

    const string Ground = "Maps/Test/Materials/Ground";
    const string Particle = "Maps/Test/Materials/Particle";
    const string OldBush = "Maps/Test/Materials/OldBush";
    const string OldRock = "Maps/Test/Materials/OldRock";

    private static byte[] Bin() => Write(
        Material(Ground), Material(Particle), Material(OldBush), Material(OldRock),
        new BinTreeObject(H("Maps/Test/Vfx"), H("VfxSystemDefinitionData"), new BinTreeProperty[]
        {
            new BinTreeObjectLink(H("material"), H(Particle)),
        }));

    // ---- the plan --------------------------------------------------------------------------------

    [Fact]
    public void ThePlanNamesTheOrphansAndCountsWhatALinkKeeps()
    {
        var plan = MapMaterialFactory.PlanUnusedStaticMaterials(Bin(), new[] { Ground }, null, out var error);

        Assert.Null(error);
        Assert.NotNull(plan);
        Assert.Equal(new[] { H(OldBush), H(OldRock) }.OrderBy(h => h), plan!.Remove);
        // Particle is drawn by no mesh, but the VFX system links it: kept, and said so
        Assert.Equal(new[] { H(Particle) }, plan.KeptByLink);
        Assert.Equal(4, plan.MaterialCount);
    }

    [Fact]
    public void TickedNamesNarrowTheRemovalAndAUsedOrLinkedTickIsRefused()
    {
        var only = new[] { OldBush, Ground, Particle };
        var plan = MapMaterialFactory.PlanUnusedStaticMaterials(Bin(), new[] { Ground }, only, out _);
        Assert.Equal(new[] { H(OldBush) }, plan!.Remove);

        var cleaned = MapMaterialFactory.RemoveUnusedStaticMaterials(Bin(), new[] { Ground },
            out int removed, out var error, only);
        Assert.Null(error);
        Assert.Equal(1, removed);
        var tree = SafeBinTree.Parse(cleaned!);
        Assert.False(tree.Objects.ContainsKey(H(OldBush)));
        Assert.True(tree.Objects.ContainsKey(H(OldRock)));    // not ticked: untouched
        Assert.True(tree.Objects.ContainsKey(H(Ground)));
        Assert.True(tree.Objects.ContainsKey(H(Particle)));
    }

    [Fact]
    public void WithoutTicksEveryUnreachableMaterialGoesAsBefore()
    {
        var cleaned = MapMaterialFactory.RemoveUnusedStaticMaterials(Bin(), new[] { Ground }, out int removed, out _);
        Assert.Equal(2, removed);
        var tree = SafeBinTree.Parse(cleaned!);
        Assert.Equal(new[] { H(Ground), H(Particle) }.OrderBy(h => h),
            tree.Objects.Where(o => o.Value.ClassHash == H("StaticMaterialDef")).Select(o => o.Key).OrderBy(h => h));
    }

    /// <summary>MaterialDocument shows a material the hash database cannot name as 0x........; the window
    /// hands that string back, and hashing it would match nothing.</summary>
    [Fact]
    public void AnUnresolvedNameIsItsHash()
    {
        const uint orphan = 0xDEADBEEFu;
        var bin = Write(Material(Ground),
            new BinTreeObject(orphan, H("StaticMaterialDef"), Array.Empty<BinTreeProperty>()));

        Assert.Equal(orphan, MapMaterialFactory.MaterialHash("0xDEADBEEF"));
        Assert.Equal(H(Ground), MapMaterialFactory.MaterialHash(Ground));

        var plan = MapMaterialFactory.PlanUnusedStaticMaterials(bin, new[] { Ground }, new[] { "0xdeadbeef" }, out _);
        Assert.Equal(new[] { orphan }, plan!.Remove);
    }

    // ---- the window ------------------------------------------------------------------------------

    private static MaterialAuditRow Row(string name, int meshes) => new(name, "Shaders/Env/Flat", meshes, meshes,
        Array.Empty<string>(),
        meshes == 0 ? new[] { new MaterialFinding(MaterialIssue.Unused, "no mesh in this map uses it") }
                    : Array.Empty<MaterialFinding>());

    private static MaterialBrowserViewModel Window(out List<IReadOnlyList<string>> asked, out int[] refreshed,
        bool changed = true, bool withCallback = true)
    {
        var got = new List<IReadOnlyList<string>>();
        var count = new int[1];
        var vm = new MaterialBrowserViewModel();
        vm.Load(new MaterialBrowserContext("x.materials.bin",
            new[] { Row("A/Used", 3), Row("A/Old1", 0), Row("A/Old2", 0) },
            Refresh: () => count[0]++,
            RemoveUnused: withCallback ? names => { got.Add(names); return Task.FromResult(changed); } : null));
        asked = got;
        refreshed = count;
        return vm;
    }

    [Fact]
    public async Task NothingTickedMeansEveryUnusedRow()
    {
        var vm = Window(out var asked, out var refreshed);
        Assert.Equal(2, vm.UnusedCandidateCount);
        Assert.True(vm.CanRemoveUnused);
        Assert.Equal("Remove unused (2)", vm.RemoveUnusedLabel);

        await vm.RemoveUnusedCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "A/Old1", "A/Old2" }, Assert.Single(asked));
        Assert.Equal(1, refreshed[0]);
    }

    [Fact]
    public async Task TicksNarrowToTheTickedUnusedRowsAndAUsedTickIsIgnored()
    {
        var vm = Window(out var asked, out _);
        vm.Rows.First(r => r.FullName == "A/Old1").IsSelected = true;
        vm.Rows.First(r => r.FullName == "A/Used").IsSelected = true;

        Assert.Equal(1, vm.UnusedCandidateCount);
        Assert.Equal("Remove unused (1 ticked)", vm.RemoveUnusedLabel);
        await vm.RemoveUnusedCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "A/Old1" }, Assert.Single(asked));
    }

    [Fact]
    public async Task ACancelledRemovalDoesNotReload()
    {
        var vm = Window(out _, out var refreshed, changed: false);
        await vm.RemoveUnusedCommand.ExecuteAsync(null);
        Assert.Equal(0, refreshed[0]);
    }

    [Fact]
    public void WithoutTheAppCallbackTheButtonIsOff()
    {
        var vm = Window(out _, out _, withCallback: false);
        Assert.Equal(2, vm.UnusedCandidateCount);
        Assert.False(vm.CanRemoveUnused);
    }

    [Fact]
    public void TheWindowAndTheAppAreWiredToTheCommand()
    {
        var axaml = Source("src", "ReyEngine.App", "Views", "MaterialBrowserWindow.axaml");
        var main = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        if (axaml is null || main is null) return;
        Assert.Contains("Command=\"{Binding RemoveUnusedCommand}\"", axaml);
        Assert.Contains("IsEnabled=\"{Binding CanRemoveUnused}\"", axaml);
        Assert.Contains("RemoveUnused: names => RemoveUnusedMaterialsAsync(", main);
        // the repair and the cleanup write the bin through the ONE helper
        Assert.Equal(2, main.Split("SaveMaterialsBin(binEntry, ").Length - 1);
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
