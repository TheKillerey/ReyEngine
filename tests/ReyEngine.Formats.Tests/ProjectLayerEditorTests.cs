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
/// folder is refused before it becomes <c>content/&lt;name&gt;/</c> in the sent mod. Since M814 a name the
/// .fantome layout cannot carry (<c>WAD_&lt;name&gt;/</c>: ASCII letters, digits, '-' and '_') is refused
/// here too, by the export's own rule.</para>
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
    // M814: a layer is also WAD_<name>/ in an exported .fantome, whose reader knows ASCII letters, digits, - and _ only
    [InlineData("Particle Fix")]
    [InlineData("caf\u00e9")]
    [InlineData("map.extras")]
    [InlineData("fix!")]
    [InlineData("\u00fcber")]
    public void ANameThatCannotBeAContentFolderIsRefused(string name)
    {
        var vm = Open(Project("Map453"));
        vm.AddLayerCommand.Execute(null);
        vm.Layers[1].Name = name;

        vm.SaveCommand.Execute(null);

        Assert.False(vm.Saved);
        Assert.True(vm.HasLayerError);
    }

    /// <summary>M814 review: the editor applies the .fantome export's own rule, in the export's own words, so a layer
    /// the editor accepts never fails Export later and the user is told what to change at the moment they can.</summary>
    [Theory]
    [InlineData("Particle Fix")]
    [InlineData("caf\u00e9")]
    [InlineData("map.extras")]
    [InlineData("BASE")]
    [InlineData("   ")]
    public void TheEditorRefusesALayerNameWithTheExportsOwnReason(string name)
    {
        var vm = Open(Project("Map453"));
        vm.AddLayerCommand.Execute(null);
        vm.Layers[1].Name = name;

        vm.SaveCommand.Execute(null);

        Assert.Equal(ReyEngine.Core.Build.FantomeLayers.NameProblem(name.Trim()), vm.LayerError);
        Assert.Equal(vm.LayerError, vm.Layers[1].Problem(vm.Layers));
    }

    [Theory]
    [InlineData("particle-fix")]
    [InlineData("Fix_2")]
    [InlineData("9")]
    [InlineData("a-b_c")]
    public void ANameBothThePackageAndTheFantomeCanCarrySaves(string name)
    {
        var vm = Open(Project("Map453"));
        vm.AddLayerCommand.Execute(null);
        vm.Layers[1].Name = name;

        vm.SaveCommand.Execute(null);

        Assert.True(vm.Saved, vm.LayerError);
        Assert.Null(vm.Layers[1].Problem(vm.Layers));
    }

    /// <summary>M814 review: the editor and Export .fantome cannot disagree about a name. Whatever the editor saves,
    /// the export's layer table accepts; whatever the editor refuses for a reason of spelling, the export refuses too.
    /// The one deliberate difference is the base layer, which the editor holds as a fixed row of its own (so a second
    /// layer cannot be called "base") and the export accepts when a project file declares it.</summary>
    [Theory]
    [InlineData("particle-fix")]
    [InlineData("Fix_2")]
    [InlineData("Particle Fix")]
    [InlineData("caf\u00e9")]
    [InlineData("map.extras")]
    [InlineData("map/extras")]
    [InlineData("\u00fcber")]
    [InlineData("x")]
    public void TheEditorAndTheFantomeExportAgreeAboutALayerName(string name)
    {
        var vm = Open(Project("Map453"));
        vm.AddLayerCommand.Execute(null);
        vm.Layers[1].Name = name;
        vm.SaveCommand.Execute(null);

        // the layer as ApplyTo would store it (trimmed), had the dialog let it through
        var project = new ReyProject { Name = "P", RootPath = @"C:\P" };
        project.Layers.Add(new ProjectLayer { Name = name.Trim(), Priority = 10 });

        if (vm.Saved) Assert.NotEmpty(ReyEngine.Core.Build.LtkProjectLayers.ForFantome(project));
        else Assert.Throws<InvalidOperationException>(() => ReyEngine.Core.Build.LtkProjectLayers.ForFantome(project));
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

    // ===================================================== M816: a project that imported an LTK-layered .fantome

    /// <summary>What the importer makes of Crauzer's Winter Rift plus a layer WAD of the same name as a base one.</summary>
    private static ReyProject Imported()
    {
        var p = Project("Map11", "layers/winter/Map11");
        p.Layers.Add(new ProjectLayer { Name = "base", Priority = 0, DeclarationsKey = "base" });
        p.Layers.Add(new ProjectLayer
        {
            Name = "winter", Priority = 3, DisplayName = "Winter", DeclarationsKey = "winter",
            Folders = { "layers/winter/Map11" },
            StringOverrides = System.Text.Json.Nodes.JsonNode.Parse("{\"en_us\":{\"b\":\"2\",\"a\":\"1\"}}")!.AsObject(),
        });
        return p;
    }

    [Fact]
    public void TwoFoldersOfOneWadNameAreTwoRowsEachOnItsOwnLayer()
    {
        var vm = Open(Imported());

        Assert.Equal(new[] { "Map11", "layers/winter/Map11" }, vm.FolderLayers.Select(f => f.Folder));
        Assert.True(vm.FolderLayers[0].Layer.IsBase);
        Assert.Equal("winter", vm.FolderLayers[1].Layer.Name);
    }

    [Fact]
    public void WhatAnImportGaveALayerSurvivesTheDialogAndARename()
    {
        var p = Imported();
        var vm = Open(p);
        var winter = vm.Layers.Single(l => l.Name == "winter");
        winter.Name = "snowdown";                                   // renamed in the dialog
        winter.Priority = 7;

        vm.ApplyTo(p);

        var layer = p.Layers.Single(l => l.Name == "snowdown");
        Assert.Equal(7, layer.Priority);
        Assert.Equal("Winter", layer.DisplayName);                  // the dialog neither shows nor edits these
        Assert.Equal("winter", layer.DeclarationsKey);              // the key is not the name: the stored declarations are still found
        Assert.Equal(new[] { "en_us" }, layer.StringOverrides!.Select(x => x.Key));
        Assert.Equal(new[] { "b", "a" }, layer.StringOverrides["en_us"]!.AsObject().Select(x => x.Key));
        Assert.Equal(new[] { "layers/winter/Map11" }, layer.Folders);                       // and its folder is still its own
        Assert.Equal("snowdown", p.LayerOfFolder("layers/winter/Map11"));
        Assert.Equal("base", p.LayerOfFolder("Map11"));
    }

    [Fact]
    public void TheBaseLayerAnImportGaveSomethingToKeepIsNotDroppedBySaving()
    {
        var p = Imported();
        var vm = Open(p);

        vm.ApplyTo(p);

        var kept = p.Layers.Single(l => l.Name == "base");
        Assert.Equal("base", kept.DeclarationsKey);
        Assert.Equal(new[] { "base", "winter" }, p.Layers.Select(l => l.Name));            // base first, as an import wrote it
    }

    [Fact]
    public void AFolderClaimedWholeIsWrittenBackWholeAndAFolderClaimedByLeafByLeaf()
    {
        var p = Imported();
        p.ProjectFolders.Add("mods/Ahri");
        p.Layers.Add(new ProjectLayer { Name = "fix", Priority = 5, Folders = { "Ahri" } });
        var vm = Open(p);

        Assert.Equal(new[] { "Map11", "layers/winter/Map11", "Ahri" }, vm.FolderLayers.Select(f => f.Folder));

        vm.ApplyTo(p);

        Assert.Equal(new[] { "layers/winter/Map11" }, p.Layers.Single(l => l.Name == "winter").Folders);
        Assert.Equal(new[] { "Ahri" }, p.Layers.Single(l => l.Name == "fix").Folders);
        Assert.Equal("fix", p.LayerOfFolder("mods/Ahri"));
    }

    [Fact]
    public void MovingAWholeClaimedFolderToAnotherLayerWorks()
    {
        var p = Imported();
        p.Layers.Add(new ProjectLayer { Name = "fix", Priority = 5 });
        var vm = Open(p);

        vm.FolderLayers.Single(f => f.Folder == "layers/winter/Map11").Layer = vm.Layers.Single(l => l.Name == "fix");
        vm.ApplyTo(p);

        Assert.Equal("fix", p.LayerOfFolder("layers/winter/Map11"));
        Assert.Empty(p.Layers.Single(l => l.Name == "winter").Folders);
        Assert.Equal(new[] { "layers/winter/Map11" }, p.Layers.Single(l => l.Name == "fix").Folders);
    }

    // ===================================================== review: a WAD folder an imported layer claims whole stays in a layer

    /// <summary>Released to base, <c>layers/winter/Map11</c> would be a folder no layer claims whole: it would lose the name of the WAD (the
    /// export wrote <c>WAD/layers_winter_Map11.wad.client</c>) and, were it given that name, sit beside the base WAD of the same name (a
    /// send merges the two trees). So it cannot be released: removing its layer, or moving its row to base, is refused with the way out.</summary>
    [Fact]
    public void ALayerThatHoldsAnImportedWadFolderCannotBeRemoved()
    {
        var vm = Open(Imported());
        var winter = vm.Layers.Single(l => l.Name == "winter");

        vm.RemoveLayerCommand.Execute(winter);

        Assert.Contains(winter, vm.Layers);                                                              // still there
        Assert.Same(winter, vm.FolderLayers.Single(f => f.Folder == "layers/winter/Map11").Layer);       // and so is its folder
        Assert.True(vm.HasLayerError);
        Assert.Contains("'layers/winter/Map11' of an imported .fantome, which cannot ship in base. Move it to another layer first.", vm.LayerError);
    }

    [Fact]
    public void OnceTheFolderIsInAnotherLayerTheLayerCanBeRemoved()
    {
        // a layer an import made of a WAD alone: no GameData, no string overrides (those are refused below, for good)
        var p = Project("Map11", "layers/winter/Map11");
        p.Layers.Add(new ProjectLayer { Name = "winter", Priority = 3, Folders = { "layers/winter/Map11" } });
        p.Layers.Add(new ProjectLayer { Name = "fix", Priority = 5 });
        var vm = Open(p);
        var winter = vm.Layers.Single(l => l.Name == "winter");

        vm.RemoveLayerCommand.Execute(winter);                                                           // refused while it holds the folder
        Assert.Contains(winter, vm.Layers);
        vm.FolderLayers.Single(f => f.Folder == "layers/winter/Map11").Layer = vm.Layers.Single(l => l.Name == "fix");
        vm.RemoveLayerCommand.Execute(winter);

        Assert.DoesNotContain(winter, vm.Layers);
        Assert.False(vm.HasLayerError);
        vm.ApplyTo(p);
        Assert.Equal("fix", p.LayerOfFolder("layers/winter/Map11"));
    }

    // ===================================================== review, round 3: a layer's GameData and string overrides are the LAYER's

    /// <summary>Save rebuilds the layer list from the rows, so a removed row takes its declarations key and its string overrides with it:
    /// an export and a send then leave them out, and the stored files stay behind unused. A layer of GameData alone (no WAD folder: the
    /// importer supports it) used to pass the guard that only looked at folders.</summary>
    [Fact]
    public void ALayerOfGameDataAloneCannotBeRemovedAndSaysWhatRemovingWouldDrop()
    {
        var p = Project("Map11");
        p.Layers.Add(new ProjectLayer { Name = "events", Priority = 2, DeclarationsKey = "events" });
        var vm = Open(p);
        var events = vm.Layers.Single(l => l.Name == "events");

        vm.RemoveLayerCommand.Execute(events);

        Assert.Contains(events, vm.Layers);
        Assert.True(vm.HasLayerError);
        Assert.Contains("Layer 'events' holds GameData from an imported .fantome", vm.LayerError);
        Assert.Contains("removing the layer would drop its declarations", vm.LayerError);
        Assert.DoesNotContain("string overrides", vm.LayerError);
        // and saving keeps the layer as it was
        vm.ApplyTo(p);
        var kept = Assert.Single(p.Layers);
        Assert.Equal(("events", "events"), (kept.Name, kept.DeclarationsKey));
    }

    [Fact]
    public void ALayerOfStringOverridesAloneCannotBeRemovedEither()
    {
        var p = Project("Map11");
        p.Layers.Add(new ProjectLayer
        {
            Name = "words", Priority = 2, StringOverrides = System.Text.Json.Nodes.JsonNode.Parse("{\"default\":{\"hello\":\"world\"}}")!.AsObject(),
        });
        var vm = Open(p);

        vm.RemoveLayerCommand.Execute(vm.Layers.Single(l => l.Name == "words"));

        Assert.Contains("Layer 'words' holds string overrides from an imported .fantome", vm.LayerError);
        Assert.Contains("would drop its string overrides", vm.LayerError);
        Assert.DoesNotContain("GameData", vm.LayerError);
        vm.ApplyTo(p);
        Assert.Equal(new[] { "default" }, Assert.Single(p.Layers).StringOverrides!.Select(x => x.Key));
    }

    [Fact]
    public void ALayerWithAFolderAndGameDataAndStringsGetsBothReasonsInOneMessage()
    {
        var vm = Open(Imported());                                                                       // winter: a WAD folder, GameData and string overrides

        vm.RemoveLayerCommand.Execute(vm.Layers.Single(l => l.Name == "winter"));

        Assert.Contains("'layers/winter/Map11' of an imported .fantome, which cannot ship in base. Move it to another layer first.", vm.LayerError);
        Assert.Contains("Layer 'winter' holds GameData and string overrides from an imported .fantome", vm.LayerError);
    }

    [Fact]
    public void ALayerThatOnlyHasADisplayNameOrWasAddedInTheDialogCanStillBeRemoved()
    {
        var p = Project("Map11");
        p.Layers.Add(new ProjectLayer { Name = "named", Priority = 2, DisplayName = "Only a label" });
        var vm = Open(p);
        vm.AddLayerCommand.Execute(null);

        vm.RemoveLayerCommand.Execute(vm.Layers.Single(l => l.Name == "named"));
        vm.RemoveLayerCommand.Execute(vm.Layers.Last());

        Assert.Equal(new[] { "base" }, vm.Layers.Select(l => l.Label));
        Assert.False(vm.HasLayerError);
    }

    // ===================================================== review, round 3: base is carried over without a folder list

    [Fact]
    public void TheBaseLayerIsCarriedOverWithoutTheFoldersAHandEditGaveIt()
    {
        // LayerOf takes the first layer that lists a folder, and base is first: a base that lists "Ahri" claims it ahead of the layer
        // the dialog assigns it to
        var p = Project("Map11", "Ahri");
        p.Layers.Add(new ProjectLayer { Name = "base", Priority = 4, Description = "The map itself", DeclarationsKey = "base", DisplayName = "Base", Folders = { "Ahri", "Map11" } });
        p.Layers.Add(new ProjectLayer { Name = "fix", Priority = 10, Folders = { "Ahri" } });
        Assert.Equal("base", p.LayerOf("Ahri"));                                                          // the shadowing: the dialog opens with Ahri in base
        var vm = Open(p);
        Assert.True(vm.FolderLayers.Single(f => f.Folder == "Ahri").Layer.IsBase);
        vm.FolderLayers.Single(f => f.Folder == "Ahri").Layer = vm.Layers.Single(l => l.Name == "fix");   // the person puts it where it belongs

        vm.ApplyTo(p);

        Assert.Equal("fix", p.LayerOf("Ahri"));                                                           // and it stays there after Save
        Assert.Equal("fix", p.LayerOfFolder("Ahri"));
        Assert.Equal("base", p.LayerOf("Map11"));                                                         // an unclaimed folder still ships in base
        var kept = p.Layers.Single(l => l.Name == "base");
        Assert.Empty(kept.Folders);
        Assert.Equal((4, "The map itself", "base", "Base"), (kept.Priority, kept.Description, kept.DeclarationsKey, kept.DisplayName));   // the rest is as it was
        Assert.Equal(new[] { "base", "fix" }, p.Layers.Select(l => l.Name));
    }

    [Fact]
    public void TheBaseLayerCarriedOverIsACopyAndTheOriginalIsLeftAlone()
    {
        var original = new ProjectLayer { Name = "base", Folders = { "Ahri" }, DeclarationsKey = "base" };
        var p = Project("Ahri");
        p.Layers.Add(original);
        var vm = Open(p);

        vm.ApplyTo(p);

        Assert.NotSame(original, p.Layers.Single(l => l.Name == "base"));
        Assert.Equal(new[] { "Ahri" }, original.Folders);
    }

    [Fact]
    public void AnImportedWadFolderMovedToBaseIsRefusedAtSave()
    {
        var p = Imported();
        var vm = Open(p);
        var row = vm.FolderLayers.Single(f => f.Folder == "layers/winter/Map11");
        var winter = row.Layer;
        row.Layer = vm.Layers[0];                                                                        // base

        vm.SaveCommand.Execute(null);

        Assert.False(vm.Saved);
        Assert.Contains("'layers/winter/Map11' came from a layer of an imported .fantome and cannot ship in base", vm.LayerError);

        row.Layer = winter;                                                                              // put back: nothing is wrong now
        vm.SaveCommand.Execute(null);
        Assert.True(vm.Saved, vm.LayerError);
    }

    [Fact]
    public void AFolderNoLayerClaimsWholeMayStillBeInBase()
    {
        var p = Imported();
        p.ProjectFolders.Add("Lux");
        var vm = Open(p);

        vm.SaveCommand.Execute(null);

        Assert.True(vm.Saved, vm.LayerError);
        Assert.True(vm.FolderLayers.Single(f => f.Folder == "Lux").Layer.IsBase);
    }

    [Fact]
    public void TwoFoldersOfOneWadInOneLayerAreRefusedAtSave()
    {
        var p = Imported();
        p.ProjectFolders.Add("layers/snow/Map11");
        p.Layers.Add(new ProjectLayer { Name = "snow", Priority = 4, Folders = { "layers/snow/Map11" } });
        var vm = Open(p);

        vm.FolderLayers.Single(f => f.Folder == "layers/snow/Map11").Layer = vm.Layers.Single(l => l.Name == "winter");     // both in winter now
        vm.SaveCommand.Execute(null);

        Assert.False(vm.Saved);
        Assert.Contains("'layers/winter/Map11' and 'layers/snow/Map11' would both ship as Map11.wad.client in layer 'winter'", vm.LayerError);

        vm.FolderLayers.Single(f => f.Folder == "layers/snow/Map11").Layer = vm.Layers.Single(l => l.Name == "snow");       // back apart
        vm.SaveCommand.Execute(null);
        Assert.True(vm.Saved, vm.LayerError);
    }

    [Fact]
    public void AProjectThatPredatesM816IsNeverRefusedForItsFolders()
    {
        // leaf claims, and two folders of one leaf in one layer (a send merges those, as it always has): the dialog never complained and does not now
        var p = Project("Map453", "mods/Ahri", "other/Ahri");
        p.Layers.Add(new ProjectLayer { Name = "fix", Priority = 10, Folders = { "Ahri" } });
        var vm = Open(p);

        vm.SaveCommand.Execute(null);

        Assert.True(vm.Saved, vm.LayerError);
    }

    [Fact]
    public void ALayerOfPreM816ProjectsStillGetsNoImportedFields()
    {
        var p = Project("Map453", "Malzahar");
        p.Layers.Add(new ProjectLayer { Name = "particle-fix", Priority = 10, Folders = { "Malzahar" } });
        var vm = Open(p);

        vm.ApplyTo(p);

        var layer = Assert.Single(p.Layers);
        Assert.Null(layer.DisplayName);
        Assert.Null(layer.StringOverrides);
        Assert.Null(layer.DeclarationsKey);
        Assert.Equal(new[] { "Malzahar" }, layer.Folders);
    }
}
