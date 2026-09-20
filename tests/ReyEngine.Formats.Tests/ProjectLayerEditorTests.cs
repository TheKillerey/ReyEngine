using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Projects;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M744: the content-layer editor in Project Settings.
///
/// <para>Layers were reachable only by hand-editing project.json, which is how the first send went out
/// with none at all. These pin the rules the dialog enforces: a folder rides exactly one layer, a deleted
/// layer hands its folders back to base rather than taking them with it, and a name that cannot be a
/// folder is refused before it becomes <c>content/&lt;name&gt;/</c> in the sent mod.</para>
///
/// <para>The markup itself is checked by the headless UiProbe card, which opens the real window - an
/// Avalonia binding is resolved at runtime, so nothing here would notice a wrong one.</para>
/// </summary>
public sealed class ProjectLayerEditorTests
{
    private static ReyProject Project(params string[] folders)
    {
        var p = new ReyProject { RootPath = @"C:\Projects\Harrowing" };
        foreach (string f in folders) p.ProjectFolders.Add(f);
        return p;
    }

    private static ProjectSettingsViewModel Open(ReyProject p) => new(p, new DialogService());

    // ===================================================== what the dialog opens on

    [Fact]
    public void BaseIsAlwaysARowAndIsNotTheUsersToEditOrDelete()
    {
        var vm = Open(Project("Map453"));

        var b = Assert.Single(vm.Layers);
        Assert.True(b.IsBase);
        Assert.False(b.IsEditable);
        Assert.Equal(ProjectLayer.BaseLayer, b.Label);

        // removing it is refused rather than throwing, since the button that would do it is hidden
        vm.RemoveLayerCommand.Execute(b);
        Assert.Single(vm.Layers);
    }

    [Fact]
    public void EveryFolderShowsUpOnTheLayerThatClaimsIt()
    {
        var p = Project("Map453", "Malzahar", "Ahri");
        p.Layers.Add(new ProjectLayer { Name = "particle-fix", Priority = 10, Folders = { "Malzahar", "Ahri" } });
        var vm = Open(p);

        Assert.Equal(3, vm.FolderLayers.Count);
        Assert.True(vm.FolderLayers.Single(f => f.Folder == "Map453").Layer.IsBase);
        Assert.Equal("particle-fix", vm.FolderLayers.Single(f => f.Folder == "Ahri").Layer.Name);
        Assert.True(vm.HasFolders);
    }

    [Fact]
    public void AFolderNamedByNoLayerSitsInBase()
    {
        var vm = Open(Project("Map453"));
        Assert.True(vm.FolderLayers.Single().Layer.IsBase);
    }

    [Fact]
    public void AFolderIsListedByTheNameTheExporterShipsItUnder()
    {
        // ProjectFolders may hold a relative or nested entry; the exporter ships the leaf of the resolved
        // path, and that is the name a layer has to claim for LayerOf to find it.
        var vm = Open(Project(@"content\Map453"));
        Assert.Equal("Map453", vm.FolderLayers.Single().Folder);
    }

    // ===================================================== editing

    [Fact]
    public void ANewLayerIsOfferedToEveryFolderAtOnce()
    {
        var vm = Open(Project("Map453"));
        var row = vm.FolderLayers.Single();
        Assert.Single(row.Choices);

        vm.AddLayerCommand.Execute(null);

        // the combo's list IS the layer list, so there is nothing to refresh and nothing to get stale
        Assert.Same(vm.Layers, row.Choices);
        Assert.Equal(2, row.Choices.Count);
    }

    [Fact]
    public void ANewLayerIsAppliedOverTheOnesAlreadyThere()
    {
        var p = Project("Map453");
        p.Layers.Add(new ProjectLayer { Name = "particle-fix", Priority = 10 });
        var vm = Open(p);

        vm.AddLayerCommand.Execute(null);

        Assert.True(vm.Layers[2].Priority > vm.Layers[1].Priority);
        Assert.NotEqual(vm.Layers[1].Name, vm.Layers[2].Name);
    }

    [Fact]
    public void DeletingALayerHandsItsFoldersBackToBase()
    {
        var p = Project("Map453", "Malzahar");
        p.Layers.Add(new ProjectLayer { Name = "particle-fix", Folders = { "Malzahar" } });
        var vm = Open(p);

        vm.RemoveLayerCommand.Execute(vm.Layers[1]);

        // the folder must still ship - it just ships in base now
        Assert.Single(vm.Layers);
        Assert.Equal(2, vm.FolderLayers.Count);
        Assert.True(vm.FolderLayers.Single(f => f.Folder == "Malzahar").Layer.IsBase);
    }

    [Fact]
    public void RenamingALayerRenamesItWhereItIsOffered()
    {
        var p = Project("Malzahar");
        p.Layers.Add(new ProjectLayer { Name = "particle-fix", Folders = { "Malzahar" } });
        var vm = Open(p);
        var row = vm.FolderLayers.Single();

        vm.Layers[1].Name = "vfx-fix";

        // the folder holds the LAYER, not its name, so a rename cannot orphan the selection
        Assert.Same(vm.Layers[1], row.Layer);
        Assert.Equal("vfx-fix", row.Layer.Label);
    }

    // ===================================================== what may be saved

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("base")]
    [InlineData("BASE")]
    [InlineData("map/extras")]
    [InlineData("map:extras")]
    public void ANameThatCannotBeAContentFolderIsRefused(string name)
    {
        var vm = Open(Project("Map453"));
        vm.AddLayerCommand.Execute(null);
        vm.Layers[1].Name = name;

        vm.SaveCommand.Execute(null);

        Assert.False(vm.Saved);
        Assert.True(vm.HasLayerError);
    }

    [Fact]
    public void TwoLayersWithTheSameNameAreRefused()
    {
        var vm = Open(Project("Map453"));
        vm.AddLayerCommand.Execute(null);
        vm.AddLayerCommand.Execute(null);
        vm.Layers[2].Name = vm.Layers[1].Name.ToUpperInvariant();

        vm.SaveCommand.Execute(null);

        Assert.False(vm.Saved);
        Assert.Contains("Two layers", vm.LayerError);
    }

    [Fact]
    public void AGoodNameSavesAndClearsTheComplaint()
    {
        var vm = Open(Project("Map453"));
        vm.AddLayerCommand.Execute(null);
        vm.Layers[1].Name = "base";
        vm.SaveCommand.Execute(null);
        Assert.True(vm.HasLayerError);

        vm.Layers[1].Name = "particle-fix";
        vm.SaveCommand.Execute(null);

        Assert.True(vm.Saved);
        Assert.False(vm.HasLayerError);
    }

    // ===================================================== writing back

    [Fact]
    public void SavingWritesTheLayersAndTheirFoldersOntoTheProject()
    {
        var p = Project("Map453", "Malzahar");
        var vm = Open(p);
        vm.AddLayerCommand.Execute(null);
        var layer = vm.Layers[1];
        layer.Name = "particle-fix";
        layer.Description = "Disables the jade projection sprites.";
        layer.Priority = 20;
        vm.FolderLayers.Single(f => f.Folder == "Malzahar").Layer = layer;

        vm.ApplyTo(p);

        var written = Assert.Single(p.Layers);
        Assert.Equal("particle-fix", written.Name);
        Assert.Equal(20, written.Priority);
        Assert.Equal("Disables the jade projection sprites.", written.Description);
        Assert.Equal(new[] { "Malzahar" }, written.Folders);

        // and the exporter reads it the same way round
        Assert.Equal("particle-fix", p.LayerOf("Malzahar"));
        Assert.Equal(ProjectLayer.BaseLayer, p.LayerOf("Map453"));
        Assert.Equal(new[] { ProjectLayer.BaseLayer, "particle-fix" },
            ReyEngine.Core.Build.LtkProjectLayers.Of(p).Select(l => l.Name).ToArray());
    }

    [Fact]
    public void BaseIsNeverWrittenAsALayerOfItsOwn()
    {
        // The exporter always declares base; writing it here too would let the user renumber the one
        // layer whose priority everything else is measured against.
        var p = Project("Map453");
        var vm = Open(p);
        vm.ApplyTo(p);
        Assert.Empty(p.Layers);
    }

    [Fact]
    public void ALayerWithNoFoldersYetIsKept()
    {
        // Someone may name a layer before moving anything into it; dropping it would discard their typing
        // without saying so.
        var p = Project("Map453");
        var vm = Open(p);
        vm.AddLayerCommand.Execute(null);
        vm.Layers[1].Name = "particle-fix";

        vm.ApplyTo(p);

        Assert.Equal("particle-fix", Assert.Single(p.Layers).Name);
        Assert.Empty(p.Layers[0].Folders);
    }

    [Fact]
    public void ReopeningTheDialogShowsWhatWasSaved()
    {
        var p = Project("Map453", "Malzahar");
        var first = Open(p);
        first.AddLayerCommand.Execute(null);
        first.Layers[1].Name = "particle-fix";
        first.FolderLayers.Single(f => f.Folder == "Malzahar").Layer = first.Layers[1];
        first.ApplyTo(p);

        var second = Open(p);

        Assert.Equal(2, second.Layers.Count);
        Assert.Equal("particle-fix", second.Layers[1].Name);
        Assert.Equal("particle-fix", second.FolderLayers.Single(f => f.Folder == "Malzahar").Layer.Name);
        Assert.True(second.FolderLayers.Single(f => f.Folder == "Map453").Layer.IsBase);
    }
}
