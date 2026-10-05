using System.Numerics;
using System.Text.RegularExpressions;
using ReyEngine.App.ViewModels;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M812: the Character window's CHROMA card - the view model's behaviour with a stand-in scan, and the markup and wiring
/// as source. The real card, bound to a real skin, is the UiProbe's "chroma" mode (headless Avalonia): a binding is
/// resolved at runtime, so neither these tests nor the build can see a bad one. The host half - the mounts the scan reads
/// through - is <see cref="ChromaHostTests"/>.
/// </summary>
public sealed class ChromaCardTests
{
    private const string Bin = "data/characters/lillia/skins/skin49.bin";
    private const string OtherBin = "data/characters/lillia/skins/skin46.bin";

    private static string? Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static MeshAsset TinyMesh() => new()
    {
        Positions = new float[9], Normals = new float[9], Uvs = new float[6], Indices = new uint[] { 0, 1, 2 },
        SubMeshes = new[] { new SubMeshInfo("body", 0, 3, 3) }, VertexCount = 3,
    };

    /// <summary>An inventory with one of everything: a shared, an unshared and an outside-the-character body texture, a colour
    /// parameter, a shared system with colour values and textures (one outside), an inferred system, an excluded sampler and a
    /// system of another skin in the closure.</summary>
    private static SkinColorInventory Inventory(string[]? sharers = null, string[]? warnings = null, bool sharingComputed = true,
        string[]? compared = null)
    {
        sharers ??= new[] { "Petals of Spring Lillia (skin46)", "skin47" };
        var bodyShared = new BodyTexture("assets/characters/lillia/skins/skin46/cloth.tex", 0x1111, BodyTextureRole.Diffuse,
            new[] { new BodyTextureUse("Characters/Lillia/Skins/Skin49/Materials/Cloth_inst", "Diffuse_Texture", new[] { "cloth" }, false) },
            false, null, sharers);
        var bodyOwn = new BodyTexture("assets/characters/lillia/skins/skin49/body.tex", 0x2222, BodyTextureRole.Diffuse,
            new[] { new BodyTextureUse("(skin default texture)", "texture", Array.Empty<string>(), true) }, false, null, Array.Empty<string>());
        var bodyOutside = new BodyTexture("assets/shared/materials/matcap/gold.tex", 0x3333, BodyTextureRole.MatCap,
            new[] { new BodyTextureUse("Characters/Lillia/Skins/Skin49/Materials/Gem_inst", "MatCap_Tex", new[] { "gem" }, false) },
            false, null, Array.Empty<string>());
        var tint = new BodyColorParameter("Characters/Lillia/Skins/Skin49/Materials/Cloth_inst", "TintColor", new Vector4(1, 0.5f, 0.25f, 1),
            "Vector4", false, false, null);
        var omitted = new BodyColorParameter("Characters/Lillia/Skins/Skin49/Materials/Cloth_inst", "Bloom_Color", Vector4.Zero,
            "Vector4", true, false, null);
        var sprite = new VfxColorTexture("texture", "Sprite", "ASSETS/Characters/Lillia/Skin46/Particles/spark.tex", 0xAAAA, false);
        var cube = new VfxColorTexture("reflectionDefinition.reflectionMapTexture", "Reflection cubemap", "ASSETS/Shared/cube.dds", 0xBBBB, true);
        var unnamed = new VfxColorTexture("particleColorTexture", "Colour lookup", "0x00000000000000cc", 0xCC, false);
        var sharedSystem = new EffectSystemEntry(0x10, "Lillia_Skin46_Q_Mis", "Particles/Q", new[] { 0xCAFEu }, new[] { "effect key" }, "multi.bin",
            new[]
            {
                new VfxColorEmitter(0, "core", false, new[]
                {
                    new VfxColorValue("birthColor", false, 0, new Vector4(1, 0, 0, 1), null, null, false),
                    new VfxColorValue("color", true, 3, null, new Vector4(1, 1, 1, 0), new Vector4(0, 0, 1, 0), true),
                }, new[] { sprite, cube, unnamed }),
                new VfxColorEmitter(1, "empty", true, Array.Empty<VfxColorValue>(), Array.Empty<VfxColorTexture>()),
            }, new[] { "Petals of Spring Lillia (skin46)" });
        var ownSystem = new EffectSystemEntry(0x20, "Lillia_Skin49_E_Mis", "Particles/E", Array.Empty<uint>(), new[] { "gear upgrade", "child of X" },
            "skin49.bin", Array.Empty<VfxColorEmitter>(), Array.Empty<string>(), IsInferred: true);
        compared ??= new[] { "Petals of Spring Lillia (skin46)", "skin47" };
        return new SkinColorInventory(Bin, "lillia", 49, "Petals of Spring Lillia (Rose Quartz) (skin49)",
            new[] { bodyShared, bodyOwn, bodyOutside }, new[] { tint, omitted },
            new[] { new ExcludedSampler("Cloth_inst", "Mask_Texture", "assets/m.tex", "mask") },
            new[] { sharedSystem, ownSystem },
            new[] { new EffectTextureEntry(sprite.Path, sprite.Hash, new[] { "Sprite" }, 1, false, new[] { "skin47" }),
                    new EffectTextureEntry(cube.Path, cube.Hash, new[] { "Reflection cubemap" }, 1, true, Array.Empty<string>()) },
            new EffectReach(new[] { Bin }, new HashSet<uint> { 0x10, 0x20, 0x30 }, "in the skin bin", 2, 0, Array.Empty<string>()),
            sharingComputed ? compared : Array.Empty<string>(), sharingComputed, new[] { "a note" }, warnings ?? Array.Empty<string>(),
            Array.Empty<string>());
    }

    private static Task<SkinColorInventory> Done(SkinColorInventory inventory) => Task.FromResult(inventory);

    // ===================================================================== the view model

    [Fact]
    public void OpeningASkinReadsNothing_TheCardWaitsForTheButton()
    {
        var vm = new MeshPreviewViewModel();
        int calls = 0;
        vm.ScanSkinColours = (_, _) => { calls++; return Done(Inventory()); };

        vm.SetChromaSkin(Bin);

        Assert.Equal(0, calls);                                   // nothing runs on its own
        Assert.True(vm.HasChromaCard);
        Assert.False(vm.HasChromaResult);
        Assert.Empty(vm.ChromaBodyRows);
        Assert.Empty(vm.ChromaEffectRows);
        Assert.Contains("Scan colours", vm.ChromaSummary);
        Assert.Equal("skin49.bin of lillia", vm.ChromaSkinLabel);
        Assert.True(vm.ScanColoursCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheHookIsCalledOnTheCallersThread_SoTheHostCanTakeItsReaders_AndTheCardWaitsForItsTask()
    {
        var vm = new MeshPreviewViewModel();
        string? scannedBin = null;
        int hookThread = -1;
        var finish = new TaskCompletionSource<SkinColorInventory>();
        vm.ScanSkinColours = (bin, _) =>
        {
            scannedBin = bin;
            hookThread = System.Environment.CurrentManagedThreadId;
            return finish.Task;
        };
        vm.SetChromaSkin(Bin);

        var scan = vm.ScanColoursCommand.ExecuteAsync(null);
        // the hook ran HERE, before the command returned control: the part of it that must happen on the UI thread did
        Assert.Equal(System.Environment.CurrentManagedThreadId, hookThread);
        Assert.False(scan.IsCompleted);                           // and the card waits for the task the hook handed back
        Assert.True(vm.ChromaScanning);
        Assert.False(vm.ScanColoursCommand.CanExecute(null));     // one scan at a time
        finish.SetResult(Inventory());
        await scan;

        Assert.Equal(Bin, scannedBin);
        Assert.False(vm.ChromaScanning);
        Assert.True(vm.HasChromaResult);
        var inventory = vm.ChromaInventory!;
        Assert.Equal(inventory.Summary, vm.ChromaSummary);
        Assert.Contains("3 body textures", vm.ChromaSummary);
        Assert.Contains("2 effect systems", vm.ChromaSummary);
        Assert.Contains("Petals of Spring Lillia (Rose Quartz) (skin49)", vm.ChromaSkinLabel);

        // BODY: three textures, two parameters, in the inventory's order
        Assert.Equal(new[] { "cloth.tex", "body.tex", "gold.tex", "Cloth_inst . TintColor", "Cloth_inst . Bloom_Color" }, vm.ChromaBodyRows.Select(r => r.Title));
        Assert.True(vm.ChromaBodyRows[0].IsShared);
        Assert.False(vm.ChromaBodyRows[1].IsShared);
        Assert.Contains("(skin46)", vm.ChromaBodyRows[0].SharedTip);
        Assert.Contains("of this character", vm.ChromaBodyRows[0].SharedTip);
        Assert.Equal("", vm.ChromaBodyRows[1].SharedTip);
        Assert.Contains("no value written", vm.ChromaBodyRows[4].Detail);
        Assert.Contains("(1, 0.5, 0.25, 1)", vm.ChromaBodyRows[3].Detail);

        // EFFECTS: system, then its emitters with something to say, then their values and textures
        var effects = vm.ChromaEffectRows;
        Assert.Equal("Lillia_Skin46_Q_Mis", effects[0].Title);
        Assert.True(effects[0].IsHeader);
        Assert.True(effects[0].IsShared);
        Assert.Contains("core", effects.Select(r => r.Title));
        Assert.DoesNotContain(effects, r => r.Title.StartsWith("empty"));             // nothing colour-bearing: no row
        Assert.Contains(effects, r => r.Title == "Birth colour" && r.Detail == "constant (1, 0, 0, 1)");
        Assert.Contains(effects, r => r.Title == "Colour over life" && r.Detail.StartsWith("curve, 3 keys") && r.Detail.Contains("randomised"));
        var sprite = effects.Single(r => r.Title == "Sprite");
        Assert.Equal("spark.tex", sprite.Detail);
        Assert.True(sprite.IsShared);                                                 // a texture's badge comes from the per-file list
        Assert.Equal(new[] { "skin47" }, sprite.SharedWith);
        Assert.False(effects.Single(r => r.Title == "Reflection cubemap").IsShared);
        Assert.Equal(2, sprite.Level);
        var own = effects.Single(r => r.Title.StartsWith("Lillia_Skin49_E_Mis"));
        Assert.False(own.IsShared);
        Assert.Contains("gear upgrade", own.Detail);
    }

    [Fact]
    public async Task WarningsHaveABlockOfTheirOwn_AndTheNotesStateTheComparisonEvenAtZero_AndSayWhatSharedMeans()
    {
        var vm = new MeshPreviewViewModel { ScanSkinColours = (_, _) => Done(Inventory(warnings: new[] { "skin3.bin could not be read (IOException: nope), so it was not compared." }, compared: Array.Empty<string>())) };
        vm.SetChromaSkin(Bin);
        Assert.False(vm.HasChromaWarnings);
        await vm.ScanColoursCommand.ExecuteAsync(null);

        // what could not be read is not a line among the notes: it has its own block, whose heading says what it means for the lists
        Assert.True(vm.HasChromaWarnings);
        Assert.Equal("Not everything could be read, so the lists may be incomplete:\n- skin3.bin could not be read (IOException: nope), so it was not compared.",
            vm.ChromaWarnings);
        Assert.DoesNotContain("could not be read", vm.ChromaNotes);

        var lines = vm.ChromaNotes.Split('\n');
        Assert.Equal("Compared with 0 other skin(s) of this character.", lines[0]);                                   // zero is stated, not left out
        Assert.Contains(lines, l => l.StartsWith("SHARED means another skin of this character uses the same file or system."));
        Assert.Contains("Companions in the same WAD, other champions, maps and global assets are not compared", vm.ChromaNotes);
        Assert.Contains("a note", lines);                                                                                // the inventory's own notes follow
        Assert.Contains("1 sampler(s) - 1 mask", vm.ChromaNotes);
        // the closure holds 3 systems, 2 are listed: the third is not reached, which is not the same as "belongs to another skin"
        Assert.Contains("1 effect system(s) in this skin's files are not reached by this skin and are not listed", vm.ChromaNotes);
        Assert.DoesNotContain("belong to other skins", vm.ChromaNotes);
        Assert.Contains("1 effect system(s) are marked (inferred)", vm.ChromaNotes);

        // the card moving on takes the warnings with it
        vm.SetChromaSkin(Bin);
        Assert.False(vm.HasChromaWarnings);
        Assert.Equal("", vm.ChromaWarnings);

        // sharing that was never computed says neither: there was nothing to compare with
        vm.ScanSkinColours = (_, _) => Done(Inventory(sharingComputed: false));
        await vm.ScanColoursCommand.ExecuteAsync(null);
        Assert.DoesNotContain("Compared with", vm.ChromaNotes);
        Assert.DoesNotContain("SHARED means", vm.ChromaNotes);
        Assert.False(vm.HasChromaWarnings);                                                   // and a clean scan has no warning block at all
    }

    [Fact]
    public async Task AFileOutsideTheCharactersFolderIsMarked_AndOneInsideOrUnnamedIsNot()
    {
        var vm = new MeshPreviewViewModel { ScanSkinColours = (_, _) => Done(Inventory()) };
        vm.SetChromaSkin(Bin);
        await vm.ScanColoursCommand.ExecuteAsync(null);

        var gold = vm.ChromaBodyRows.Single(r => r.Title == "gold.tex");
        Assert.True(gold.IsOutside);
        Assert.Equal("OUTSIDE", gold.OutsideBadge);
        Assert.Contains("Outside this character, not compared", gold.OutsideTip);
        Assert.Contains("assets/characters/lillia/", gold.OutsideTip);
        Assert.Contains("outside this character, not compared", gold.Detail);       // the line says it too, without a hover
        Assert.False(vm.ChromaBodyRows.Single(r => r.Title == "cloth.tex").IsOutside);   // skin46's folder is still Lillia's own
        Assert.False(vm.ChromaBodyRows.Single(r => r.Title == "body.tex").IsOutside);
        Assert.All(vm.ChromaBodyRows.Where(r => r.Tag == "param"), r => Assert.False(r.IsOutside));

        var effects = vm.ChromaEffectRows;
        Assert.True(effects.Single(r => r.Title == "Reflection cubemap").IsOutside);   // ASSETS/Shared/cube.dds
        Assert.False(effects.Single(r => r.Title == "Sprite").IsOutside);
        Assert.False(effects.Single(r => r.Title == "Colour lookup").IsOutside);        // a 0x... reference has no known place: it is not guessed
        Assert.False(effects[0].IsOutside);                                              // a system row is not a file
    }

    [Fact]
    public async Task AnInferredSystemIsTitledSo_AndItsTooltipSaysWhy()
    {
        var vm = new MeshPreviewViewModel { ScanSkinColours = (_, _) => Done(Inventory()) };
        vm.SetChromaSkin(Bin);
        await vm.ScanColoursCommand.ExecuteAsync(null);

        var inferred = vm.ChromaEffectRows.Single(r => r.Title == "Lillia_Skin49_E_Mis (inferred)");
        Assert.True(inferred.IsHeader);
        Assert.Contains("Inferred.", inferred.Tip);
        Assert.Contains("gear upgrade", inferred.Tip);
        Assert.DoesNotContain(vm.ChromaEffectRows, r => r.Title == "Lillia_Skin46_Q_Mis (inferred)");   // the resolver names this one
        Assert.DoesNotContain("Inferred.", vm.ChromaEffectRows[0].Tip);
    }

    [Fact]
    public async Task AnotherSkinOrAPropClearsTheCard_AndCancelsAScanInFlight()
    {
        var vm = new MeshPreviewViewModel();
        var started = new TaskCompletionSource();
        CancellationToken seen = default;
        vm.ScanSkinColours = (_, token) => Task.Run(() =>
        {
            seen = token;
            started.SetResult();
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            token.ThrowIfCancellationRequested();
            return Inventory();
        });
        vm.SetChromaSkin(Bin);
        var running = vm.ScanColoursCommand.ExecuteAsync(null);
        await started.Task;
        Assert.True(vm.ChromaScanning);
        Assert.False(vm.ScanColoursCommand.CanExecute(null));                         // one scan at a time

        vm.Show("a prop", TinyMesh(), null, null);                                    // the window moves on to something else
        await running;

        Assert.True(seen.IsCancellationRequested);
        Assert.False(vm.HasChromaCard);
        Assert.False(vm.ChromaScanning);
        Assert.False(vm.HasChromaResult);
        Assert.Empty(vm.ChromaBodyRows);

        // and a skin after a result starts from empty too
        vm.ScanSkinColours = (_, _) => Done(Inventory());
        vm.SetChromaSkin(Bin);
        await vm.ScanColoursCommand.ExecuteAsync(null);
        Assert.True(vm.HasChromaResult);
        vm.SetChromaSkin(OtherBin);
        Assert.False(vm.HasChromaResult);
        Assert.Empty(vm.ChromaEffectRows);
        Assert.Equal("skin46.bin of lillia", vm.ChromaSkinLabel);
        Assert.Null(vm.ChromaInventory);
    }

    [Fact]
    public async Task AScanThatIgnoresItsTokenAndFinishesAfterAnotherSkinOpenedDoesNotLandOnIt()
    {
        var vm = new MeshPreviewViewModel();
        var finish = new TaskCompletionSource<SkinColorInventory>();
        vm.ScanSkinColours = (_, _) => finish.Task;                    // never looks at the token it was given
        vm.SetChromaSkin(Bin);
        var running = vm.ScanColoursCommand.ExecuteAsync(null);
        Assert.True(vm.ChromaScanning);

        vm.SetChromaSkin(OtherBin);                                    // another skin: the card starts from empty
        Assert.False(vm.ChromaScanning);
        finish.SetResult(Inventory());                                 // the old scan finishes anyway
        await running;

        Assert.False(vm.HasChromaResult);
        Assert.Null(vm.ChromaInventory);
        Assert.Empty(vm.ChromaBodyRows);
        Assert.Empty(vm.ChromaEffectRows);
        Assert.Contains("Scan colours", vm.ChromaSummary);             // still the new skin's invitation
        Assert.Equal("skin46.bin of lillia", vm.ChromaSkinLabel);
        Assert.False(vm.ChromaScanning);
        Assert.True(vm.ScanColoursCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheSameSkinOpenedAgainIsANewCard_AndTheOldScansResultDoesNotLandOnIt()
    {
        // the case the bin path cannot tell apart: the card moved on and came back to the SAME skin, so only the token knows
        var vm = new MeshPreviewViewModel();
        var finish = new TaskCompletionSource<SkinColorInventory>();
        vm.ScanSkinColours = (_, _) => finish.Task;
        vm.SetChromaSkin(Bin);
        var running = vm.ScanColoursCommand.ExecuteAsync(null);

        vm.SetChromaSkin(Bin);                                         // the skin is opened again
        finish.SetResult(Inventory());
        await running;

        Assert.False(vm.HasChromaResult);
        Assert.Null(vm.ChromaInventory);
        Assert.Empty(vm.ChromaBodyRows);
        Assert.Contains("Scan colours", vm.ChromaSummary);
    }

    [Fact]
    public async Task ALateFailureOfAScanTheCardMovedOnFromDoesNotOverwriteTheNewSkinsSummary()
    {
        var vm = new MeshPreviewViewModel();
        var finish = new TaskCompletionSource<SkinColorInventory>();
        vm.ScanSkinColours = (_, _) => finish.Task;
        vm.SetChromaSkin(Bin);
        var running = vm.ScanColoursCommand.ExecuteAsync(null);

        vm.SetChromaSkin(OtherBin);
        finish.SetException(new InvalidOperationException("late boom"));   // not a cancellation: an ordinary failure, but too late
        await running;

        Assert.DoesNotContain("late boom", vm.ChromaSummary);
        Assert.DoesNotContain("failed", vm.ChromaSummary);
        Assert.Contains("Scan colours", vm.ChromaSummary);
        Assert.False(vm.ChromaScanning);
    }

    [Fact]
    public async Task AnOldScansEndDoesNotDisturbTheScanThatReplacedIt()
    {
        var vm = new MeshPreviewViewModel();
        var first = new TaskCompletionSource<SkinColorInventory>();
        var second = new TaskCompletionSource<SkinColorInventory>();
        int call = 0;
        vm.ScanSkinColours = (_, _) => ++call == 1 ? first.Task : second.Task;
        vm.SetChromaSkin(Bin);
        var oldScan = vm.ScanColoursCommand.ExecuteAsync(null);

        vm.SetChromaSkin(OtherBin);
        var newScan = vm.ScanColoursCommand.ExecuteAsync(null);
        Assert.True(vm.ChromaScanning);

        first.SetResult(Inventory());                                  // the old one ends first
        await oldScan;
        Assert.True(vm.ChromaScanning);                                // the new one is still running, and still the card's
        Assert.False(vm.HasChromaResult);

        second.SetResult(Inventory());
        await newScan;
        Assert.False(vm.ChromaScanning);
        Assert.True(vm.HasChromaResult);
    }

    [Fact]
    public async Task AScanThatThrowsLeavesAMessage_NotACrash()
    {
        var vm = new MeshPreviewViewModel { ScanSkinColours = (_, _) => throw new InvalidOperationException("boom") };
        vm.SetChromaSkin(Bin);

        await vm.ScanColoursCommand.ExecuteAsync(null);

        Assert.False(vm.ChromaScanning);
        Assert.False(vm.HasChromaResult);
        Assert.Contains("boom", vm.ChromaSummary);

        vm.ScanSkinColours = (_, _) => Task.FromException<SkinColorInventory>(new IOException("disk"));    // the same, from the task
        await vm.ScanColoursCommand.ExecuteAsync(null);
        Assert.False(vm.ChromaScanning);
        Assert.Contains("disk", vm.ChromaSummary);
    }

    [Fact]
    public void TheChromaTabLeavesWhenItsCardDoes_AndTheOtherTabsKeepTheirNumbers()
    {
        Assert.Equal(new[] { 0, 1, 2, 3 }, new[] { MeshPreviewViewModel.MaterialTab, MeshPreviewViewModel.AnimateTab, MeshPreviewViewModel.PlayTab, MeshPreviewViewModel.SceneTab });
        Assert.Equal(4, MeshPreviewViewModel.ChromaTab);

        var vm = new MeshPreviewViewModel { ScanSkinColours = (_, _) => Done(Inventory()) };
        vm.SetChromaSkin(Bin);
        vm.PanelTab = MeshPreviewViewModel.ChromaTab;
        vm.SetChromaSkin(null);                                                       // a hidden tab must not stay selected
        Assert.Equal(MeshPreviewViewModel.AnimateTab, vm.PanelTab);
        Assert.False(vm.ScanColoursCommand.CanExecute(null));
    }

    [Fact]
    public void TheSharedBadgeCountsTheSkinsAndTheTooltipNamesThem_NotAHundredOfThem()
    {
        var many = Enumerable.Range(1, 40).Select(n => $"skin{n}").ToArray();
        var row = new ChromaRowViewModel("gradient_test_01.tex", sharedWith: many);

        Assert.True(row.IsShared);
        Assert.Equal("SHARED x40", row.SharedBadge);
        Assert.Contains("40 other skins", row.SharedTip);
        Assert.Contains("skin1,", row.SharedTip);
        Assert.Contains($"skin{ChromaRowViewModel.MaxNamedSharers}", row.SharedTip);
        Assert.DoesNotContain($"skin{ChromaRowViewModel.MaxNamedSharers + 1},", row.SharedTip);
        Assert.Contains($"and {40 - ChromaRowViewModel.MaxNamedSharers} more", row.SharedTip);

        var one = new ChromaRowViewModel("x.tex", sharedWith: new[] { "skin46" });
        Assert.Equal("SHARED", one.SharedBadge);
        Assert.Contains("1 other skin of this character:", one.SharedTip);
        Assert.False(new ChromaRowViewModel("y.tex").IsShared);
        Assert.False(new ChromaRowViewModel("y.tex").IsOutside);
        Assert.Equal(new Avalonia.Thickness(28, 0, 0, 0), new ChromaRowViewModel("z", level: 2).Indent);
    }

    // ===================================================================== the markup and the wiring, as source

    [Fact]
    public void TheChromaTabIsTheLastOfFiveAndBindsOnlyWhatTheViewModelHas()
    {
        if (Source("src", "ReyEngine.App", "Views", "MeshPreviewWindow.axaml") is not { } xaml) return;

        int tabs = xaml.IndexOf("<TabControl SelectedIndex=\"{Binding PanelTab}\"", StringComparison.Ordinal);
        Assert.True(tabs >= 0);
        string tabControl = xaml[tabs..xaml.IndexOf("</TabControl>", tabs, StringComparison.Ordinal)];
        Assert.Equal(5, Regex.Matches(tabControl, "<TabItem ").Count);
        int chroma = tabControl.IndexOf("<TabItem FontSize=\"12\" IsVisible=\"{Binding HasChromaCard}\">", StringComparison.Ordinal);
        Assert.True(chroma >= 0, "no Chroma TabItem");
        Assert.Equal(chroma, tabControl.LastIndexOf("<TabItem ", StringComparison.Ordinal));   // last, so the other four keep their indices

        string card = tabControl[chroma..];
        foreach (string needle in new[]
                 {
                     "Text=\"CHROMA\"", "Content=\"Scan colours\"", "Command=\"{Binding ScanColoursCommand}\"",
                     "{Binding ChromaSkinLabel}", "{Binding ChromaSummary}", "{Binding ChromaNotes}", "{Binding HasChromaNotes}",
                     "{Binding ChromaWarnings}", "{Binding HasChromaWarnings}", "Foreground=\"{DynamicResource ReyWarningBrush}\"",
                     "Text=\"BODY\"", "Text=\"EFFECTS\"", "ItemsSource=\"{Binding ChromaBodyRows}\"", "ItemsSource=\"{Binding ChromaEffectRows}\"",
                     "x:DataType=\"vm:ChromaRowViewModel\"", "IsVisible=\"{Binding IsShared}\"", "ToolTip.Tip=\"{Binding SharedTip}\"",
                     "Text=\"{Binding SharedBadge}\"", "Classes=\"badge warn\"", "Classes.chromaHeader=\"{Binding IsHeader}\"",
                     "IsVisible=\"{Binding IsOutside}\"", "ToolTip.Tip=\"{Binding OutsideTip}\"", "Text=\"{Binding OutsideBadge}\"", "Classes=\"badge outline\"",
                 })
            Assert.Contains(needle, card);
        // in both inventory lists (M812) and in the BODY RECOLOUR texture list above them (M824)
        Assert.Equal(3, Regex.Matches(card, "Text=\"\\{Binding OutsideBadge\\}\"").Count);
        Assert.Equal(3, Regex.Matches(card, "Text=\"\\{Binding SharedBadge\\}\"").Count);

        // theme brushes only - no literal colour anywhere in the card
        Assert.DoesNotMatch("=\"#[0-9A-Fa-f]{3,8}\"", card);
        Assert.Contains("DynamicResource ReyTextDimBrush", card);

        // every {Binding X} names a real member of the view model or of the row
        var members = typeof(MeshPreviewViewModel).GetProperties().Select(p => p.Name)
            .Concat(typeof(ChromaRowViewModel).GetProperties().Select(p => p.Name))
            .Concat(typeof(ChromaTextureRowViewModel).GetProperties().Select(p => p.Name))   // M824: the recolour's texture rows
            .Concat(typeof(ChromaParameterRowViewModel).GetProperties().Select(p => p.Name)).ToHashSet();   // M825: and its colour parameter rows
        foreach (Match m in Regex.Matches(card, @"\{Binding (\w+)\}"))
            Assert.True(members.Contains(m.Groups[1].Value), $"{{Binding {m.Groups[1].Value}}} is not a property of the view model or the row");
        Assert.NotNull(typeof(MeshPreviewViewModel).GetProperty("ScanColoursCommand"));

        // the header style the rows ask for exists in the theme
        Assert.Contains("TextBlock.chromaHeader", Source("src", "ReyEngine.App", "Themes", "ReyTheme.axaml"));
    }

    [Fact]
    public void TheScanIsWiredOnDemandOnly_AndTheHostHalfWritesNothing()
    {
        if (Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs") is not { } main
            || Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.ChromaStudio.cs") is not { } host
            || Source("src", "ReyEngine.App", "ViewModels", "MeshPreviewViewModel.Chroma.cs") is not { } card
            || Source("src", "ReyEngine.App", "ViewModels", "MeshPreviewViewModel.cs") is not { } preview) return;

        // the hook is wired once, and loading a skin only tells the card WHICH bin - it never scans
        Assert.Contains("MeshPreview.ScanSkinColours = ScanSkinColours;", main);
        Assert.Contains("MeshPreview.SetChromaSkin(binPath);", main);
        Assert.DoesNotContain("ScanSkinColours(", main);                                // nothing here CALLS it: the card does, on the button
        Assert.DoesNotContain("_skinColors.Scan(", main);
        Assert.Contains("SetChromaSkin(null);", preview);                                // Show() turns the card off for every other subject

        // the host half and the card are read-only: no asset, project file or setting is written from either
        foreach (string text in new[] { host, card })
            foreach (string write in new[]
                     {
                         "StoreOverrideBytes", "SaveMapBinBytesAsync", "TryWriteToProjectFile", "File.Write", "WriteAllBytes", "WriteAllText",
                         "ProjectService.Save", "Settings.Save", "EditorSettings.Save", "_overrides.Set",
                     })
                Assert.DoesNotContain(write, text);

        // the worker reads through the readers taken on the UI thread, never through the live fields
        Assert.Contains("AcquireReaderLease(mounts, archive)", host);
        Assert.Contains("long inputs = Interlocked.Read(ref _mapThumbnailInputs);", host);   // a scan that straddled a change in place is run again
        Assert.Contains("if (attempt >= MaxScanAttempts) return SkinColorInventory.Empty(", host);
        Assert.Contains("ReadAssetFrom(mounts, archive, hash)", host);
        Assert.Contains("Task.Run(() => _skinColors.Scan(request, cancellationToken), cancellationToken)", host);
        Assert.DoesNotContain("ReadAssetByPath(", host);
        Assert.DoesNotContain("ReadAsset(", host.Replace("ReadAssetFrom(", ""));
    }

    [Fact]
    public void TheOpenChampionWadIsMountedAgainByEveryRebuild_AndForgottenWhenTheMountsAreDropped()
    {
        if (Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs") is not { } main
            || Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.Characters.cs") is not { } characters) return;

        // BuildMounts: after the game fallback, before the index is built; and the M807 lines at its top are untouched
        Assert.Matches(@"AddGameFallback\(\);\r?\n\s*RemountCharacterWads\(\);[^\n]*\r?\n\s*_mounts\.Rebuild\(\);", main);
        // LoadWad drops the mounts, so what the Character window mounted on them is forgotten with them
        Assert.Matches(@"_mounts = null;[^\n]*\r?\n\s*ProjectMode = false; InspectionMode = true;\r?\n\s*_openChampionWad = null;", main);
        // "already mounted" is asked of the mounts, not of a list that remembers having asked - and there is one WAD to remember, not a list
        Assert.Contains("if (IsMountedAsFallback(_mounts, wadPath))", characters);
        Assert.DoesNotContain("_characterWads", characters);
        Assert.DoesNotContain("_characterWads", main);
        // the remount: one WAD, only while the window has it open, only from the install the project points at
        int remount = characters.IndexOf("private void RemountCharacterWads()", StringComparison.Ordinal);
        Assert.True(remount >= 0, "RemountCharacterWads moved - re-point this guard");
        string body = characters[remount..];
        Assert.Contains("CharacterWindowHasChampionOpen(open.OpenedAt)", body);
        Assert.Contains("GameReferenceLibrary.FindFinalDirectory(Project.GameDirectory)", body);
        Assert.Contains("!IsUnder(open.Wad, final)", body);
        Assert.DoesNotContain("foreach", body[..body.IndexOf("private static bool SamePath", StringComparison.Ordinal)]);   // no loop over champions
    }

    [Fact]
    public void SyncingHashesMarksTheLiveMountsAsChangedBeforeAndAfterRebuildingThem()
    {
        if (Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs") is not { } main) return;
        int start = main.IndexOf("private void ApplyHashesToOpenWad()", StringComparison.Ordinal);
        Assert.True(start >= 0, "ApplyHashesToOpenWad moved - re-point this guard");
        string body = main[start..main.IndexOf("private void HashLookup()", start, StringComparison.Ordinal)];

        // the mount branch: marked, then the work in a try, and marked again in its finally - so an exception cannot leave the version short
        int first = body.IndexOf("NoteMapThumbnailInputsChanged();", StringComparison.Ordinal);
        int rebuild = body.IndexOf("mounts.Rebuild();", StringComparison.Ordinal);
        int last = body.IndexOf("finally { NoteMapThumbnailInputsChanged(); }", StringComparison.Ordinal);
        Assert.True(first >= 0 && first < rebuild && rebuild < last, "the Rebuild is not between the two marks");
    }
}
