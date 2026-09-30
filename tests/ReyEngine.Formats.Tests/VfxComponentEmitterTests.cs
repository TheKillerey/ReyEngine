using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Particles;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.D3D11;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M800: Riot's component-based ("Shimmer") VFX format, which ReyEngine does not simulate. HoL_26_CubeGrid and
/// HoL_26_CubeGrid_02 (Map11) are the two systems in the game that use it, and the editor drew nothing for them
/// without saying why - "they are not loading". The resolver now COUNTS the format (it never draws it), the
/// raw-tree coverage notes stop claiming the game will use what the client ignores, and the same wording feeds
/// the system card, the D3D11 status line and the map log.
///
/// <para>The fixtures are synthetic so every branch of the wording can be reached; the real-data tests read the
/// installed game's Map11 <c>base_srx.materials.bin</c> and say so in the test output when they do not run.</para>
/// </summary>
public sealed class VfxComponentEmitterTests(ITestOutputHelper output)
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private const string Map11 = Final + @"\Maps\Shipping\Map11.wad.client";
    private const string BaseSrx = "data/maps/mapgeometry/map11/base_srx.materials.bin";

    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private const string Head = "This system uses Riot's component (Shimmer) VFX format, added in 16.16. ReyEngine does not simulate it.";

    // ------------------------------------------------------------------ fixtures

    internal static BinTreeStruct Components() => new(H("VfxComponents"), H("VfxComponents"), new BinTreeProperty[]
    {
        new BinTreeStruct(H("LifetimeComponent"), H("VfxLifetimeComponent"), new BinTreeProperty[] { new BinTreeF32(H("lifetime"), 1f) }),
    });

    /// <summary>A classic VfxEmitterDefinitionData. <paramref name="components"/> copies the block onto it, the
    /// way Riot's 16.16 Map11 data does; <paramref name="texture"/> gives it a classic payload.</summary>
    internal static BinTreeStruct Classic(string name, bool? disabled = null, bool components = false, string? texture = null)
    {
        var p = new List<BinTreeProperty> { new BinTreeString(H("emitterName"), name) };
        if (disabled is { } d) p.Add(new BinTreeBool(H("disabled"), d));
        if (texture is not null) p.Add(new BinTreeString(H("texture"), texture));
        if (components) p.Add(Components());
        return new BinTreeStruct(0, H("VfxEmitterDefinitionData"), p);
    }

    internal static BinTreeStruct Shimmer(string name, bool? disabled, string? classHash = null)
    {
        var p = new List<BinTreeProperty> { new BinTreeString(H("emitterName"), name), Components() };
        if (disabled is { } d) p.Add(new BinTreeBool(H("disabled"), d));
        return new BinTreeStruct(0, H(classHash ?? "VfxShimmerEmitterDefinitionData"), p);
    }

    internal static byte[] Bin(IEnumerable<BinTreeStruct>? classic, IEnumerable<BinTreeStruct>? shimmer)
    {
        var props = new List<BinTreeProperty> { new BinTreeString(H("particleName"), "sys") };
        if (classic is not null)
            props.Add(new BinTreeContainer(H("complexEmitterDefinitionData"), BinPropertyType.Struct,
                classic.Cast<BinTreeProperty>().ToArray()));
        if (shimmer is not null)
            props.Add(new BinTreeContainer(H("ShimmerEmitterDefinitionData"), BinPropertyType.Struct,
                shimmer.Cast<BinTreeProperty>().ToArray()));
        var system = new BinTreeObject(H("sys"), H("VfxSystemDefinitionData"), props);
        using var ms = new MemoryStream();
        new BinTree(new[] { system }, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    internal static VfxSystemDefinition Resolve(IEnumerable<BinTreeStruct>? classic, IEnumerable<BinTreeStruct>? shimmer) =>
        VfxSystemResolver.ExtractAll(Bin(classic, shimmer)).Values.Single();

    // ------------------------------------------------------------------ the hashes

    [Fact]
    public void TheFormatsHashesAreTheOnesTheCensusMeasured()
    {
        // read off Riot's own bins by the M800 diagnosis: the system field, the emitter class, the block
        Assert.Equal(0xeb0aabebu, VfxComponentFormat.ShimmerListField);
        Assert.Equal(0x94be93d1u, VfxComponentFormat.ShimmerEmitterClass);
        Assert.Equal(H("VfxComponents"), VfxComponentFormat.ComponentsField);
    }

    // ------------------------------------------------------------------ the resolver counts

    [Fact]
    public void TheResolverCountsShimmerEntriesAndComponentOnlyClassicEntriesWithoutChangingWhatItReads()
    {
        var def = Resolve(
            classic: new[]
            {
                Classic("plain", texture: "ASSETS/t.dds"),
                Classic("comp1", disabled: true, components: true),
                Classic("comp2", components: true),
                Classic("hybrid", components: true, texture: "ASSETS/t.dds"),   // a classic payload AND the block
            },
            shimmer: new[] { Shimmer("s1", true), Shimmer("s2", true), Shimmer("s3", null) });

        var c = Assert.IsType<VfxComponentEmitters>(def.ComponentEmitters);
        Assert.Equal(3, c.ShimmerEmitters);
        Assert.Equal(2, c.ShimmerEmittersDisabled);   // an absent `disabled` is enabled
        Assert.Equal(1, c.ShimmerEmittersEnabled);
        // comp1 and comp2 carry nothing but the block; "hybrid" still draws through its classic payload
        Assert.Equal(2, c.ClassicComponentOnlyEmitters);
        Assert.True(c.Any);

        // and what the resolver reads is exactly what it always read: the classic entries, in file order.
        // Shimmer entries are counted, never turned into emitters - that is the whole of "without changing
        // what draws".
        Assert.Equal(new[] { "plain", "comp1", "comp2", "hybrid" }, def.Emitters.Select(e => e.Name).ToArray());
        Assert.Equal(new[] { true, false, false, true }, def.Emitters.Select(e => e.IsVisual).ToArray());
    }

    [Fact]
    public void ASystemWithoutTheFormatCarriesNoCounts()
    {
        Assert.Null(Resolve(new[] { Classic("plain", texture: "ASSETS/t.dds") }, shimmer: null).ComponentEmitters);
        // a classic entry that has a payload of its own is a classic emitter that happens to carry the block
        Assert.Null(Resolve(new[] { Classic("hybrid", components: true, texture: "ASSETS/t.dds") }, shimmer: null).ComponentEmitters);
        // no emitters at all: a stub
        Assert.Null(Resolve(classic: null, shimmer: null).ComponentEmitters);
    }

    [Fact]
    public void TheListFieldAndTheEmitterClassEachIdentifyAShimmerEntry()
    {
        // a future patch that adds a sibling class to the list must still be counted as a Shimmer entry ...
        var byField = Resolve(classic: null, shimmer: new[] { Shimmer("a", true, classHash: "VfxShimmerSomethingElse") });
        Assert.Equal(new VfxComponentEmitters(1, 1, 0), byField.ComponentEmitters);

        // ... and the Shimmer class found under a container with another name is one too
        var props = new List<BinTreeProperty>
        {
            new BinTreeString(H("particleName"), "sys"),
            new BinTreeContainer(H("someOtherEmitterList"), BinPropertyType.Struct, new BinTreeProperty[] { Shimmer("b", false) }),
        };
        var system = new BinTreeObject(H("sys"), H("VfxSystemDefinitionData"), props);
        using var ms = new MemoryStream();
        new BinTree(new[] { system }, Array.Empty<string>()).Write(ms);
        var byClass = VfxSystemResolver.ExtractAll(ms.ToArray()).Values.Single();
        Assert.Equal(new VfxComponentEmitters(1, 0, 0), byClass.ComponentEmitters);
        Assert.Empty(byClass.Emitters);
    }

    // ------------------------------------------------------------------ the wording

    [Fact]
    public void TheNoteSaysWhyAllShippedEmittersDrawNothing()
    {
        // the wording the user asked for, N = 3
        Assert.Equal(Head + " All 3 shipped Shimmer emitters here are disabled, and the client ignores the "
                     + "VfxComponents block on classic emitters, so the game draws nothing from them either.",
            new VfxComponentEmitters(3, 3, 3).Note);

        // one emitter: "All 1 shipped Shimmer emitters" is not a sentence
        Assert.Equal(Head + " The only shipped Shimmer emitter here is disabled, and the client ignores the "
                     + "VfxComponents block on classic emitters, so the game draws nothing from them either.",
            new VfxComponentEmitters(1, 1, 1).Note);
    }

    [Fact]
    public void TheNoteOnlyMentionsWhatTheSystemActuallyHas()
    {
        Assert.Equal(Head + " All 2 shipped Shimmer emitters here are disabled, so the game draws nothing from them either.",
            new VfxComponentEmitters(2, 2, 0).Note);
        Assert.Equal(Head + " The client ignores the VfxComponents block on classic emitters, so the game draws nothing from them either.",
            new VfxComponentEmitters(0, 0, 4).Note);
        Assert.Equal("", new VfxComponentEmitters(0, 0, 0).Note);
        Assert.False(new VfxComponentEmitters(0, 0, 0).Any);
    }

    [Fact]
    public void AnEnabledShimmerEmitterIsNotClaimedToDrawNothing()
    {
        // a mod can enable one, and then "the game draws nothing" is no longer something the data supports
        var one = new VfxComponentEmitters(3, 2, 1).Note;
        Assert.Equal(Head + " 1 of 3 Shimmer emitters here is enabled, so the game may draw it and ReyEngine cannot. "
                     + "The client ignores the VfxComponents block on classic emitters.", one);
        Assert.Equal(Head + " 2 of 3 Shimmer emitters here are enabled, so the game may draw them and ReyEngine cannot.",
            new VfxComponentEmitters(3, 1, 0).Note);
        Assert.DoesNotContain("draws nothing", one);
    }

    // ------------------------------------------------------------------ "Nothing to draw"

    [Fact]
    public void ASystemAuthoredInTheComponentFormatSaysNothingToDrawAndWhy()
    {
        var def = Resolve(
            classic: new[] { Classic("comp", components: true) },
            shimmer: new[] { Shimmer("s", true) });
        Assert.True(VfxNothingToDraw.Applies(def));
        Assert.Equal("Nothing to draw - component (Shimmer) VFX is not simulated; "
                     + "the game draws nothing from it either.", VfxNothingToDraw.Status(def));

        // enabled: the game may draw it, and the status no longer says otherwise
        var live = Resolve(classic: null, shimmer: new[] { Shimmer("s", false) });
        Assert.Equal("Nothing to draw - component (Shimmer) VFX is not simulated; "
                     + "the game may draw it.", VfxNothingToDraw.Status(live));
    }

    [Fact]
    public void OtherSystemsWithNothingToDrawGetTheirOwnReason()
    {
        Assert.Equal("Nothing to draw - this system has no emitters.",
            VfxNothingToDraw.Status(Resolve(classic: null, shimmer: null)));
        Assert.Equal("Nothing to draw - every emitter of this system is disabled.",
            VfxNothingToDraw.Status(Resolve(new[] { Classic("off", disabled: true, texture: "ASSETS/t.dds") }, shimmer: null)));
        Assert.Equal("Nothing to draw - no enabled emitter names a texture or a mesh.",
            VfxNothingToDraw.Status(Resolve(new[] { Classic("bare") }, shimmer: null)));
    }

    [Fact]
    public void ASystemThatDrawsSomethingHasNoNothingToDrawStatus()
    {
        var def = Resolve(
            classic: new[] { Classic("plain", texture: "ASSETS/t.dds"), Classic("comp", components: true) },
            shimmer: new[] { Shimmer("s", true) });
        Assert.False(VfxNothingToDraw.Applies(def));
        Assert.Null(VfxNothingToDraw.Status(def));
        Assert.NotNull(def.ComponentEmitters);   // the note still applies to its component half
    }

    // ------------------------------------------------------------------ the raw tree stays, with honest notes

    [Fact]
    public void TheRawTreeRowsOfTheFormatCarryTheCorrectedNotesAndStayEditable()
    {
        var names = new Dictionary<uint, string>
        {
            [VfxComponentFormat.ShimmerListField] = "ShimmerEmitterDefinitionData",
            [VfxComponentFormat.ComponentsField] = "VfxComponents",
            [H("emitterName")] = "emitterName",
            [H("disabled")] = "disabled",
        };
        var doc = ParticleDocument.Parse(
            Bin(new[] { Classic("comp", components: true) }, new[] { Shimmer("s", true) }), h => names.GetValueOrDefault(h))!;
        var system = doc.Systems.Single();

        // the Shimmer list is a system row, and every row under it is flagged with the list's note
        var list = system.Properties.Single(p => p.Name == "ShimmerEmitterDefinitionData");
        Assert.Equal(VfxPreviewCoverage.IgnoredNote(VfxComponentFormat.ShimmerListField), list.PreviewNote);
        var under = system.Properties.SkipWhile(p => !ReferenceEquals(p, list)).Skip(1).TakeWhile(p => p.Depth > 0).ToList();
        Assert.NotEmpty(under);
        Assert.All(under, p => Assert.Equal(list.PreviewNote, p.PreviewNote));
        // "kept editable as today": the flag inside the Shimmer emitter is an ordinary, writable bool row
        var disabled = under.Single(p => p.Name == "disabled");
        Assert.False(disabled.IsReadOnly);
        Assert.Equal("True", disabled.CurrentText, ignoreCase: true);

        // the classic entry's block is noted as ignored by the client, not as something the game will use
        var block = doc.Systems.Single().Emitters.Single().Properties.Single(p => p.Name == "VfxComponents");
        Assert.Equal(VfxPreviewCoverage.IgnoredNote(VfxComponentFormat.ComponentsField), block.PreviewNote);
        Assert.Contains("ignores", block.PreviewNote);
    }

    // ------------------------------------------------------------------ the real Map11 systems

    private static (WadArchive Wad, HashDatabase Db)? OpenMap11()
    {
        if (!File.Exists(Map11)) return null;
        HashDatabase db;
        try { db = new HashSyncService().LoadLocal(_ => { }); }
        catch { return null; }
        return (WadArchive.Open(Map11, new WadPathResolver(db)), db);
    }

    [Fact]
    public void TheTwoRealMap11SystemsAreCountedAndHaveNothingToDraw()
    {
        if (OpenMap11() is not { } opened)
        {
            output.WriteLine("SKIPPED: the installed game's Map11.wad.client (or the hash tables) is not available.");
            return;
        }
        using var wad = opened.Wad;
        ulong h = HashAlgorithms.WadPath(BaseSrx);
        if (!wad.TryGetEntry(h, out _))
        {
            output.WriteLine("SKIPPED: base_srx.materials.bin is not in Map11.wad.client.");
            return;
        }

        var all = VfxSystemResolver.ExtractAll(wad.Extract(h));
        var grid = all.Values.Single(s => s.Name == "HoL_26_CubeGrid");
        var grid02 = all.Values.Single(s => s.Name == "HoL_26_CubeGrid_02");

        // CubeGrid: three Shimmer emitters (all disabled) beside three classic entries that carry the block
        // and nothing else; CubeGrid_02: one of each. Structure first, then the conclusion.
        Assert.Equal(new VfxComponentEmitters(3, 3, 3), grid.ComponentEmitters);
        Assert.Equal(new VfxComponentEmitters(1, 1, 1), grid02.ComponentEmitters);
        Assert.Equal(3, grid.Emitters.Count);
        Assert.Single(grid02.Emitters);

        // the picture the user saw: not one emitter of either is visual, so nothing is drawn
        Assert.All(grid.Emitters.Concat(grid02.Emitters), e => Assert.False(e.IsVisual, e.Name));
        Assert.True(VfxNothingToDraw.Applies(grid));
        Assert.True(VfxNothingToDraw.Applies(grid02));
        Assert.Contains("All 3 shipped Shimmer emitters here are disabled", grid.ComponentEmitters!.Note);
        Assert.Contains("The only shipped Shimmer emitter here is disabled", grid02.ComponentEmitters!.Note);

        // and no other system in this bin is authored in the format
        Assert.Equal(2, all.Values.Count(s => s.ComponentEmitters is not null));
        output.WriteLine($"RAN on the installed game: {all.Count} systems in base_srx.materials.bin; "
            + $"HoL_26_CubeGrid {grid.ComponentEmitters}, HoL_26_CubeGrid_02 {grid02.ComponentEmitters}.");
    }

    [Fact]
    public void TheEditorSelectsARealSystemAndShowsTheNoteOnItsSystemCardOnly()
    {
        if (OpenMap11() is not { } opened)
        {
            output.WriteLine("SKIPPED: the installed game's Map11.wad.client (or the hash tables) is not available.");
            return;
        }
        using var wad = opened.Wad;
        ulong h = HashAlgorithms.WadPath(BaseSrx);
        if (!wad.TryGetEntry(h, out _))
        {
            output.WriteLine("SKIPPED: base_srx.materials.bin is not in Map11.wad.client.");
            return;
        }

        var vm = new ParticleEditorViewModel();
        Assert.True(vm.Load(new WadAssetEntry { Path = BaseSrx }, wad.Extract(h), editable: false));

        vm.SelectedSystem = vm.Systems.Single(s => s.Name == "HoL_26_CubeGrid");
        var systemCard = vm.Cards[0];
        Assert.True(systemCard.IsSystemCard);
        Assert.Equal(new VfxComponentEmitters(3, 3, 3).Note, systemCard.ComponentNote);
        Assert.True(systemCard.HasComponentNote);
        Assert.All(vm.Cards.Skip(1), c => Assert.Null(c.ComponentNote));   // the note is the system's, once
        Assert.Equal(3, vm.Cards.Count - 1);

        // D3D11 reports "no shader loaded" for this system; the editor says what is true instead
        Assert.Equal("Nothing to draw - component (Shimmer) VFX is not simulated; the game "
                     + "draws nothing from it either.",
            vm.Dx11RenderFailedStatus(ShaderPreviewRenderer.NoShaderLoaded));

        // a system that does draw is untouched: no note, and the renderer's own words
        ParticleSystemNodeViewModel? drawing = null;
        foreach (var node in vm.Systems.ToList())
        {
            vm.SelectedSystem = node;
            if (vm.Playback is { } playback && !VfxNothingToDraw.Applies(playback.Items[0].System)) { drawing = node; break; }
        }
        Assert.NotNull(drawing);
        Assert.Null(vm.Cards[0].ComponentNote);
        Assert.False(vm.HasComponentNote);
        Assert.Equal("render failed: no shader loaded", vm.Dx11RenderFailedStatus(ShaderPreviewRenderer.NoShaderLoaded));
        output.WriteLine($"RAN on the installed game: system card note on HoL_26_CubeGrid, none on '{drawing!.Name}'.");
    }
}
