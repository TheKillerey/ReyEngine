using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M826: the Character window's EFFECTS RECOLOUR card against a stand-in host: the lists, the switches, the pending state, the save and revert the card asks of its host, and the live
/// preview it publishes - on a synthetic system (<see cref="EffectColorsTests.Sample"/>), so these run anywhere. Riot's own files and the real host are
/// <see cref="ChromaEffectsRealDataTests"/> and <see cref="ChromaEffectsDeviceTests"/>.
/// </summary>
public sealed class ChromaEffectsTests
{
    private const string SkinBinPath = "data/characters/zz/skins/skin1.bin";
    private const string BinPath = "data/characters/zz/zz_multi_skins.bin";
    private const string SpritePath = "ASSETS/Characters/Zz/Skins/Skin1/Particles/spark.tex";
    private const string SharedSpritePath = "ASSETS/Shared/Particles/glow.tex";
    private const string CubePath = "ASSETS/Shared/Particles/cube.dds";
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    // ===================================================================== the stand-in host

    /// <summary>A system with a birth colour (orange constant + a curve) and a colour over life (white curve), a disabled emitter, and a sprite per emitter.</summary>
    private static byte[] Bin(bool withTexture = true)
    {
        BinTreeProperty[] Sprite(string path) => withTexture ? new BinTreeProperty[] { new BinTreeString(H("texture"), path) } : Array.Empty<BinTreeProperty>();
        return EffectColorsTests.Write(
            EffectColorsTests.SystemObject(EffectColorsTests.Sys, "Zz_Skin1_Q",
                EffectColorsTests.Emitter("Flame", Sprite(SpritePath).Concat(new BinTreeProperty[]
                {
                    EffectColorsTests.ValueColor("birthColor", new Vector4(1f, 0.45f, 0.05f, 0.8f), new[] { new Vector4(1f, 0f, 0f, 1f), new Vector4(0f, 0f, 1f, 0f) }),
                    EffectColorsTests.ValueColor("color", null, new[] { Vector4.One, new Vector4(1f, 1f, 1f, 0f) }),
                }).ToArray()),
                EffectColorsTests.Emitter("Glow", Sprite(SharedSpritePath).Concat(new BinTreeProperty[]
                {
                    EffectColorsTests.ValueColor("birthColor", new Vector4(0.1f, 0.7f, 0.6f, 1f)),
                    new BinTreeBool(H("disabled"), true),
                }).ToArray())));
    }

    private static byte[] Tex() => TexWriter.Write(ChromaRecolourTestsAccess.Bands(32, 32), TexFormat.Bc1, mipmaps: true);

    private sealed class FakeHost
    {
        public byte[] Riot = Bin();
        public byte[]? Current;                                                                            // what the project serves when it is not Riot's (a saved recipe)
        public Func<EffectColorField, (bool Edited, bool OwnedEdited, string? DefaultOff)>? Flag;
        public readonly List<EffectExcludedField> ExtraExcluded = new();
        public string Problem = "";
        public IReadOnlyList<string> SharedWith = new[] { "Zz (skin2)", "Zz (skin3)" };
        public ChromaSavedRecipe? Saved;
        public bool WithPreview;
        public Func<byte[]?> Original = () => Tex();
        public readonly List<(string Bin, ColorTransform Transform, List<ChromaEffectColorRef> Targets, List<ChromaEffectColorRef> Stale)> ColorSaves = new();
        public readonly List<List<ChromaEffectColorRef>> ColorReverts = new();
        public readonly List<(ColorTransform Transform, List<ChromaTarget> Targets, List<ChromaTarget> Stale)> TextureSaves = new();
        public readonly List<List<ChromaTarget>> TextureReverts = new();
        public Func<List<ChromaEffectColorRef>, Exception?>? FailColors;
        public readonly List<(string Key, byte[] Rgba, int W, int H)> Pushed = new();

        public ChromaEffectSnapshot Snapshot(IReadOnlyList<EffectSystemEntry> systems)
        {
            var scan = SkinEffectColors.Read(Riot);
            var infos = systems.Select(s => new ChromaEffectSystemInfo(s.PathHash, s.Name, s.Bin, s.SharedWith, s.IsInferred, string.Join(", ", s.ReachedBy), s.ParticlePath)).ToList();
            var fields = scan.Fields.Select(f =>
            {
                var flag = Flag?.Invoke(f) ?? (false, false, null);
                return new ChromaEffectFieldInfo(f, BinPath, f.Recolourable, f.Reason, flag.Edited, flag.OwnedEdited, flag.DefaultOff);
            }).ToList();
            var preview = WithPreview ? new EffectColorWorkingSet(new[] { (BinPath, Current ?? Riot, Riot) }) : null;
            return new ChromaEffectSnapshot(infos, fields, scan.Excluded.Concat(ExtraExcluded).ToList(), Problem, preview);
        }

        public SkinColorInventory Inventory() => SkinColorInventory.Empty(SkinBinPath, "").WithNoWarnings() with
        {
            CharacterFolder = "zz",
            Effects = new[]
            {
                new EffectSystemEntry(EffectColorsTests.Sys, "Zz_Skin1_Q", "particles/zz/q", new uint[] { 1 }, new[] { "effect key" }, BinPath, Array.Empty<VfxColorEmitter>(), SharedWith),
            },
            EffectTextures = new[]
            {
                new EffectTextureEntry(SpritePath, BinTexturePath.HashOfReference(SpritePath), new[] { "Sprite" }, 1, false, SharedWith),
                new EffectTextureEntry(SharedSpritePath, BinTexturePath.HashOfReference(SharedSpritePath), new[] { "Sprite" }, 1, false, Array.Empty<string>()),
                new EffectTextureEntry(CubePath, BinTexturePath.HashOfReference(CubePath), new[] { "Reflection cubemap" }, 1, true, Array.Empty<string>()),
            },
        };

        public void Wire(MeshPreviewViewModel card)
        {
            card.ScanSkinColours = (_, _) => Task.FromResult(Inventory());
            card.ReadChromaEffects = (_, systems, _, _) => Snapshot(systems);
            card.ReadChromaSaved = _ => Saved;
            card.ReadChromaOriginal = _ => Original();
            card.ReadChromaBin = _ => Riot;
            card.ChromaUiPost = a => { a(); return Task.CompletedTask; };
            card.UseDx11Preview = false;
            card.SaveChromaEffectColors = (bin, t, targets, stale) =>
            {
                ColorSaves.Add((bin, t, targets.ToList(), stale.ToList()));
                if (FailColors?.Invoke(targets.ToList()) is { } ex) throw ex;
                return Task.FromResult(new ChromaEffectSaveResult(targets.Count, 0, 0, stale.Count, targets.Select(x => new EffectColorKey(x.System, x.Emitter, x.Field)).ToList(), Array.Empty<string>()));
            };
            card.RevertChromaEffectColors = (_, refs) => { ColorReverts.Add(refs.ToList()); return Task.FromResult(refs.Count); };
            card.SaveChromaEffectTextures = (_, t, targets, stale) =>
            {
                TextureSaves.Add((t, targets.ToList(), stale.ToList()));
                return Task.FromResult(new ChromaSaveResult(targets.Count, 0, 0, stale.Count, targets.Select(x => x.Hash).ToList(), targets.Select(x => x.Hash).ToList(), Array.Empty<string>()));
            };
            card.RevertChromaEffectTextures = (_, targets) => { TextureReverts.Add(targets.ToList()); return targets.Count; };
            card.PushChromaDx11 = items => { foreach (var (key, rgba, w, h) in items) Pushed.Add((key, rgba, w, h)); };
        }
    }

    private static MeshPreviewViewModel Card(FakeHost host)
    {
        var card = new MeshPreviewViewModel();
        host.Wire(card);
        card.SetChromaSkin(SkinBinPath);
        return card;
    }

    private static async Task Scan(MeshPreviewViewModel card)
    {
        await card.ScanColoursCommand.ExecuteAsync(null);
        await card.ChromaIdleAsync();
    }

    private static async Task<MeshPreviewViewModel> Scanned(FakeHost host)
    {
        var card = Card(host);
        await Scan(card);
        return card;
    }

    private static async Task EffectsOn(MeshPreviewViewModel card)
    {
        card.SelectAllEffectColorsCommand.Execute(null);
        card.SelectAllEffectTexturesCommand.Execute(null);
        await card.ChromaIdleAsync();
    }

    // ===================================================================== the lists

    [Fact]
    public async Task TheEffectsAreListedAndStartSwitchedOff_SoTheSlidersTouchTheBodyUntilThePersonAsks()
    {
        var card = await Scanned(new FakeHost());
        Assert.True(card.HasChromaEffects);
        Assert.True(card.HasChromaEffectTextures);
        Assert.True(card.HasChromaRecolourControls);
        var system = Assert.Single(card.ChromaEffectSystems);
        Assert.Equal("Zz_Skin1_Q", system.Name);
        Assert.True(system.IsShared);
        Assert.Equal("SHARED x2", system.SharedBadge);
        Assert.False(system.IsIncluded);
        Assert.All(card.ChromaEffectTextures, r => Assert.False(r.IsIncluded));
        Assert.False(card.ChromaRecolourDirty);

        // a slider move with the effects off writes and publishes nothing for them
        card.ChromaHue = 120;
        await card.ChromaIdleAsync();
        Assert.False(card.ChromaRecolourDirty);                                 // nothing is switched on in this stand-in's body, and the effects are off: nothing to write
        Assert.Empty(card.ChromaEffectSystems.SelectMany(s => s.Fields).Where(f => f.IsIncluded));
        Assert.Null(card.Playback);
    }

    [Fact]
    public async Task TheLeftAloneListSaysWhatIsNeverTouched_AndWhy()
    {
        var host = new FakeHost { Riot = Bin() };
        var card = await Scanned(host);
        // the sample system authors no mixers: nothing excluded; add them and the lines appear
        Assert.Empty(card.ChromaEffectExcluded);
        var rows = (IReadOnlyList<ChromaRowViewModel>)typeof(MeshPreviewViewModel).GetMethod("BuildEffectExcludedRows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, new object[]
        { new List<EffectExcludedField>
        {
            new EffectExcludedField(1, 0, "S", "E", "paletteDefinition.palleteSrcMixColor", "(0.3, 0.59, 0.11, 0)", "A channel mixer typed as a colour."),
            new EffectExcludedField(1, 1, "S", "E2", "paletteDefinition.palleteSrcMixColor", "(1, 0, 0, 0)", "A channel mixer typed as a colour."),
            new EffectExcludedField(1, 0, "S", "E", "alphaErosionDefinition.erosionMapName", "assets/e.tex", "A data map."),
        } })!;
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Title == "Palette channel mixer x2" && r.Detail.Contains("mixer"));
        Assert.Contains(rows, r => r.Title == "Erosion maps x1");
    }

    [Fact]
    public async Task ATextureTheBodyDrawsTooAndACubemapAreListedWithTheReasonAndCannotBeSwitchedOn_AFileOutsideTheCharacterIsMarked()
    {
        var host = new FakeHost();
        var card = await Scanned(host);
        var byName = card.ChromaEffectTextures.ToDictionary(r => r.Name);
        var cube = byName["cube.dds"];
        Assert.False(cube.CanInclude);
        Assert.Contains("cubemap", cube.Note, StringComparison.OrdinalIgnoreCase);
        Assert.False(cube.IsIncluded);
        var shared = byName["glow.tex"];                                        // under assets/shared/: outside this character
        Assert.True(shared.IsOutside);
        var own = byName["spark.tex"];
        Assert.False(own.IsOutside);
        Assert.True(own.CanInclude);

        card.SelectAllEffectTexturesCommand.Execute(null);
        Assert.True(own.IsIncluded);
        Assert.False(shared.IsIncluded);                                        // a bulk switch never reaches a file outside the character's folder
        shared.IsIncluded = true;                                               // one by one it does
        Assert.True(shared.IsIncluded);
    }

    [Fact]
    public async Task ADisabledEmittersFieldsAreMarkedOnTheirRow_AndAllColoursIncludesThemLikeAnyOther()
    {
        var card = await Scanned(new FakeHost());
        var system = card.ChromaEffectSystems.Single();
        system.IsExpanded = true;
        card.SelectAllEffectColorsCommand.Execute(null);
        Assert.All(system.Fields.Where(f => f.CanInclude), f => Assert.True(f.IsIncluded));     // "all" is all
        var glow = system.Fields.Single(f => f.Name.StartsWith("Glow"));
        Assert.Contains("(disabled)", glow.Name);                                                // the game draws nothing from it, and the row says so
    }

    // ===================================================================== the switches

    [Fact]
    public async Task ASystemSwitchSetsAllItsFields_AFieldSwitchMovesTheSystemSwitch_AndTheStateDoesNotDependOnTheFieldsBeingListed()
    {
        var host = new FakeHost();
        var card = await Scanned(host);
        var system = card.ChromaEffectSystems.Single();
        Assert.Empty(system.Fields);                                            // not created until expanded

        system.IsIncluded = true;                                               // the person ticks the system, never opening it
        card.ChromaHue = 90;
        await card.ChromaIdleAsync();
        Assert.True(card.ChromaRecolourDirty);
        await card.SaveChromaRecolourNowAsync();
        Assert.Single(host.ColorSaves);
        Assert.Equal(system.FieldInfos.Count(f => f.Recolourable), host.ColorSaves[0].Targets.Count);   // every field of the ticked system, though its rows were never made

        system.IsExpanded = true;
        Assert.Equal(system.FieldInfos.Count, system.Fields.Count);
        Assert.All(system.Fields.Where(f => f.CanInclude), f => Assert.True(f.IsIncluded));  // the rows read the state, however late they are made

        var one = system.Fields.First(f => f.CanInclude);
        one.IsIncluded = false;
        Assert.True(system.IsIncluded);                                         // the others are still on
        foreach (var f in system.Fields.Where(f => f.CanInclude)) f.IsIncluded = false;
        Assert.False(system.IsIncluded);                                        // the last one off turns the system off
        system.IsIncluded = true;
        Assert.All(system.Fields.Where(f => f.CanInclude), f => Assert.True(f.IsIncluded));
    }

    // ===================================================================== pending, save, revert

    [Fact]
    public async Task SwitchingTheEffectsOnWithTheSlidersAtNoChangeIsNotPending_MovingAnySliderIs()
    {
        var host = new FakeHost();
        var card = await Scanned(host);
        await EffectsOn(card);
        Assert.False(card.ChromaRecolourDirty);
        Assert.False(card.HasPendingChromaRecolour);
        card.ChromaHue = 45;
        Assert.True(card.HasPendingChromaRecolour);
        Assert.Contains("effect colour", card.ChromaRecolourStatus);
        Assert.True(card.ApplyChromaRecolourCommand.CanExecute(null));
    }

    [Fact]
    public async Task ApplyAndSaveHandsTheHostTheIncludedFieldsAndTextures_AndTheCardCountsThemSaved()
    {
        var host = new FakeHost();
        var card = await Scanned(host);
        await EffectsOn(card);
        card.ChromaHue = 120;
        card.ChromaBrightness = 1.2;
        var transform = card.ChromaSettings.ToTransform();
        await card.SaveChromaRecolourNowAsync();

        var save = Assert.Single(host.ColorSaves);
        Assert.Equal(BinPath, save.Targets.Select(t => t.Bin).Distinct().Single());
        Assert.Equal(transform, save.Transform);                                 // the FULL transform: the host derives the hue-only form for the textures
        var wanted = SkinEffectColors.Read(host.Riot).Fields.Where(f => f.Recolourable).Select(f => f.Key).ToHashSet();
        Assert.Equal(wanted, save.Targets.Select(t => new EffectColorKey(t.System, t.Emitter, t.Field)).ToHashSet());
        Assert.Empty(save.Stale);
        var textures = Assert.Single(host.TextureSaves);
        Assert.Equal(transform, textures.Transform);
        Assert.Equal(new[] { BinTexturePath.HashOfReference(SpritePath) }, textures.Targets.Select(t => t.Hash));   // the character's own sprite; not the shared one, not the cubemap

        Assert.False(card.ChromaRecolourDirty, card.ChromaRecolourStatus);
        Assert.True(card.HasSavedChromaRecolour);
        Assert.Contains("Saved:", card.ChromaRecolourStatus);
        Assert.Contains("effect colour(s) written", card.ChromaRecolourStatus);
    }

    [Fact]
    public async Task AFieldSwitchedOffAfterASaveIsGivenBack_AndATextureSwitchedOffToo()
    {
        var host = new FakeHost();
        var card = await Scanned(host);
        await EffectsOn(card);
        card.ChromaHue = 120;
        await card.SaveChromaRecolourNowAsync();
        var system = card.ChromaEffectSystems.Single();
        system.IsExpanded = true;
        var drop = system.Fields.First(f => f.CanInclude);
        drop.IsIncluded = false;
        card.ChromaEffectTextures.First(t => t.CanInclude && t.IsIncluded).IsIncluded = false;
        Assert.True(card.ChromaRecolourDirty);
        await card.SaveChromaRecolourNowAsync();

        var second = host.ColorSaves.Last();
        Assert.Contains(second.Stale, s => s.Field == drop.Key.Field && s.Emitter == drop.Key.Emitter);   // the earlier recipe's field, no longer wanted, goes back to Riot's
        Assert.DoesNotContain(second.Targets, s => s.Field == drop.Key.Field && s.Emitter == drop.Key.Emitter);
        Assert.NotEmpty(host.TextureSaves.Last().Stale);
        Assert.False(card.ChromaRecolourDirty, card.ChromaRecolourStatus);
    }

    [Fact]
    public async Task ASaveThatFailsKeepsTheEditPending_AndTheAutoSaveDoesNotTryTheSameStateAgain()
    {
        var host = new FakeHost { FailColors = _ => new InvalidOperationException("the bin cannot be changed on top of the mod's GameData right now") };
        var card = await Scanned(host);
        await EffectsOn(card);
        card.ChromaHue = 100;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => card.SaveChromaRecolourNowAsync());
        Assert.Contains("GameData", ex.Message);
        Assert.True(card.ChromaRecolourDirty);
        Assert.False(card.ChromaAutoSaveDue);                                   // the same state would fail the same way
        card.ChromaHue = 101;
        Assert.True(card.ChromaAutoSaveDue);                                    // something changed: worth a try
    }

    [Fact]
    public async Task AFieldTheScanDoesNotListStaysOwned_NeitherATargetNorStale()
    {
        var host = new FakeHost();
        var gone = new ChromaEffectColorRef { System = 0xDEAD0001, Emitter = 3, Field = "birthColor", Bin = "data/characters/zz/gone.bin", SystemName = "Gone" };
        host.Saved = new ChromaSavedRecipe(new ColorTransform { HueShiftDegrees = 60f }, Array.Empty<ChromaTarget>(), null, null,
            new ChromaSavedEffects(new ColorTransform { HueShiftDegrees = 60f }, new[] { gone }, Array.Empty<ChromaTarget>()));
        var card = await Scanned(host);
        Assert.True(card.HasSavedChromaRecolour);
        Assert.False(card.ChromaRecolourDirty, "an unlisted saved field made the card look pending");
        Assert.Equal(60.0, card.ChromaHue);                                      // the sliders come back from the recipe

        // the saved recipe is a recipe: the effects the scan lists start OFF (only what it owns is on)
        Assert.All(card.ChromaEffectSystems, s => Assert.False(s.IsIncluded));

        await EffectsOn(card);
        card.ChromaHue = 61;
        await card.SaveChromaRecolourNowAsync();
        var save = host.ColorSaves.Last();
        Assert.DoesNotContain(save.Targets, t => t.System == gone.System);
        Assert.DoesNotContain(save.Stale, t => t.System == gone.System);         // an absent switch is not a switched-off one
    }

    [Fact]
    public async Task ASavedRecipeComesBackWithItsSwitches_AndRevertGivesEverythingBack()
    {
        var host = new FakeHost();
        var card = await Scanned(host);
        await EffectsOn(card);
        card.ChromaHue = 120;
        await card.SaveChromaRecolourNowAsync();
        var saved = host.ColorSaves.Last().Targets;
        var savedTextures = host.TextureSaves.Last().Targets;

        // reopen: a card on the same skin whose host holds that recipe
        var transform = card.ChromaSettings.ToTransform();
        var host2 = new FakeHost { Saved = new ChromaSavedRecipe(transform, Array.Empty<ChromaTarget>(), null, null, new ChromaSavedEffects(transform, saved, savedTextures)) };
        var card2 = await Scanned(host2);
        Assert.True(card2.HasSavedChromaRecolour);
        Assert.False(card2.ChromaRecolourDirty);
        Assert.Equal(120.0, card2.ChromaHue);
        var owned = card2.ChromaEffectSystems.Single().FieldInfos.Where(f => saved.Any(s => s.Emitter == f.Key.Emitter && s.Field == f.Key.Field)).ToList();
        Assert.NotEmpty(owned);
        Assert.True(card2.ChromaEffectSystems.Single().IsIncluded);
        Assert.Contains(card2.ChromaEffectTextures, t => t.IsIncluded);

        Assert.True(card2.RevertChromaRecolourCommand.CanExecute(null));
        await card2.RevertChromaRecolourCommand.ExecuteAsync(null);
        Assert.Equal(saved.Count, Assert.Single(host2.ColorReverts).Count);
        Assert.Equal(savedTextures.Count, Assert.Single(host2.TextureReverts).Count);
        Assert.False(card2.HasSavedChromaRecolour);
        Assert.True(card2.ChromaSettings.ToTransform().IsIdentity);
        Assert.Contains("effect colour", card2.ChromaRecolourStatus);
    }

    [Fact]
    public async Task AnEffectTextureTheBodyDrawsIsNotSwitchedOnHere_SoOneFileNeverHasTwoRecipes()
    {
        var host = new FakeHost();
        var card = Card(host);
        // the body lists the sprite as one of its textures too
        card.ScanSkinColours = (_, _) => Task.FromResult(host.Inventory() with
        {
            BodyTextures = new[] { new BodyTexture(SpritePath, BinTexturePath.HashOfReference(SpritePath), BodyTextureRole.Diffuse, Array.Empty<BodyTextureUse>(), false, null, Array.Empty<string>()) },
        });
        await Scan(card);
        var row = card.ChromaEffectTextures.Single(r => r.Name == "spark.tex");
        Assert.False(row.CanInclude);
        Assert.Contains("body", row.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheShareWarningNamesTheOtherSkinsOfTheSwitchedOnEffects_AndSaysTheRecolourIsInPlace()
    {
        var host = new FakeHost();
        var card = await Scanned(host);
        Assert.Equal("", card.ChromaEffectShareWarning);
        await EffectsOn(card);
        Assert.True(card.HasChromaEffectShareWarning);
        Assert.Contains("also changes 2 other skin(s)", card.ChromaEffectShareWarning);
        Assert.Contains("Zz (skin2)", card.ChromaEffectShareWarning);
        Assert.Contains("in place", card.ChromaEffectShareWarning);

        card.SelectNoEffectColorsCommand.Execute(null);
        card.SelectNoEffectTexturesCommand.Execute(null);
        Assert.Equal("", card.ChromaEffectShareWarning);
        card.ChromaEffectTextures.Single(r => r.Name == "glow.tex").IsIncluded = true;                 // outside the character: its own sentence
        Assert.Contains("outside this character's own folder", card.ChromaEffectShareWarning);
    }

    [Fact]
    public async Task AProblemTheHostReportsIsShownBesideTheList()
    {
        var card = await Scanned(new FakeHost { Problem = "The untouched zz_multi_skins.bin cannot be read here (no reference WAD is mounted)." });
        Assert.True(card.HasChromaEffectProblem);
        Assert.Contains("cannot be read", card.ChromaEffectProblem);
    }

    // ===================================================================== the live preview

    [Fact]
    public async Task TheSlidersRepublishThePlayingEffectWithTheRecolouredDefinitionAndTexture_AndNoChangeGivesTheOriginalsBack()
    {
        var host = new FakeHost { WithPreview = true };
        var card = Card(host);
        card.UseDx11Preview = true;                                              // the pool push is the D3D11 route
        var defs = VfxSystemResolver.ExtractAll(host.Riot);
        card.SetVfx(defs, new Dictionary<uint, uint>());
        var original = defs[EffectColorsTests.Sys];
        var sprite = new TextureImage(32, 32, TextureDecoder.Decode(Tex()).Rgba);
        var shared = new TextureImage(32, 32, TextureDecoder.Decode(Tex()).Rgba);
        var item = new VfxPlaybackItem(original, Vector3.Zero, new TextureImage?[] { sprite, shared }) { Seed = 1 };
        card.Playback = new VfxPlayback(new[] { item });
        await Scan(card);
        await EffectsOn(card);
        VfxPlayback? first = card.Playback;

        card.ChromaHue = 120;
        await card.ChromaIdleAsync();
        var now = card.Playback!;
        Assert.NotSame(first, now);                                              // republished
        var shown = Assert.Single(now.Items);
        Assert.NotSame(original, shown.System);
        // exactly the definition saving writes
        var written = SkinEffectColors.Rewrite(host.Riot, host.Riot, card.ChromaSettings.ToTransform(),
            SkinEffectColors.Read(host.Riot).Fields.Where(f => f.Recolourable).Select(f => f.Key).ToList(), Array.Empty<EffectColorKey>());
        var fromBytes = VfxSystemResolver.ExtractAll(written.Bytes!)[EffectColorsTests.Sys];
        Assert.Equal(fromBytes.Emitters[0].BirthColor.Constant, shown.System.Emitters[0].BirthColor.Constant);
        Assert.Equal(fromBytes.Emitters[0].BirthColor.Values, shown.System.Emitters[0].BirthColor.Values);
        Assert.NotEqual(original.Emitters[0].BirthColor.Values, shown.System.Emitters[0].BirthColor.Values);
        // the disabled emitter was switched on by "all colours", so it moved too
        Assert.Equal(fromBytes.Emitters[1].BirthColor.Constant, shown.System.Emitters[1].BirthColor.Constant);

        // the sprite is the hue-only transform of the ORIGINAL, a copy (the host's cached image is never written)
        var textureTransform = SkinEffectColors.TextureTransform(card.ChromaSettings.ToTransform());
        var wanted = textureTransform.Apply(TextureDecoder.Decode(Tex())).Rgba;
        Assert.NotSame(sprite, shown.EmitterTextures[0]);
        Assert.Equal(wanted, shown.EmitterTextures[0]!.Rgba);
        Assert.Equal(TextureDecoder.Decode(Tex()).Rgba, sprite.Rgba);
        Assert.Same(shared, shown.EmitterTextures[1]);                           // the shared sprite is outside the character: still off
        // the D3D11 pool is told, under the key the particle pipeline binds the sprite with
        Assert.Contains(host.Pushed, p => p.Key == SpritePath.ToLowerInvariant() && p.Rgba.SequenceEqual(wanted));

        // a second position is derived from the originals again: nothing compounds
        card.ChromaHue = 200;
        await card.ChromaIdleAsync();
        var second = card.Playback!.Items[0];
        var direct = SkinEffectColors.Rewrite(host.Riot, host.Riot, card.ChromaSettings.ToTransform(),
            SkinEffectColors.Read(host.Riot).Fields.Where(f => f.Recolourable).Select(f => f.Key).ToList(), Array.Empty<EffectColorKey>());
        Assert.Equal(VfxSystemResolver.ExtractAll(direct.Bytes!)[EffectColorsTests.Sys].Emitters[0].BirthColor.Values, second.System.Emitters[0].BirthColor.Values);
        Assert.Equal(SkinEffectColors.TextureTransform(card.ChromaSettings.ToTransform()).Apply(TextureDecoder.Decode(Tex())).Rgba, second.EmitterTextures[0]!.Rgba);

        // no change: the playing effect is the skin's own again - its definition and its texture
        card.ChromaHue = 0;
        await card.ChromaIdleAsync();
        var back = card.Playback!.Items[0];
        Assert.Equal(original.Emitters[0].BirthColor.Values, back.System.Emitters[0].BirthColor.Values);
        Assert.Equal(original.Emitters[1].BirthColor.Constant, back.System.Emitters[1].BirthColor.Constant);
        Assert.Equal(TextureDecoder.Decode(Tex()).Rgba, back.EmitterTextures[0]!.Rgba);
        Assert.Contains(host.Pushed, p => p.Key == SpritePath.ToLowerInvariant() && p.Rgba.SequenceEqual(TextureDecoder.Decode(Tex()).Rgba));
    }

    [Fact]
    public async Task AnItemBuiltAfterTheRecolourIsRecolouredToo_AndAFieldSwitchedOffReturnsToTheSkinsOwnColour()
    {
        var host = new FakeHost { WithPreview = true };
        var card = Card(host);
        var defs = VfxSystemResolver.ExtractAll(host.Riot);
        card.SetVfx(defs, new Dictionary<uint, uint>());
        await Scan(card);
        await EffectsOn(card);
        card.ChromaHue = 120;
        await card.ChromaIdleAsync();

        // the card's own picker builds the item from the definitions it holds now
        card.SelectedVfx = card.VfxSystems.Single();
        var item = card.Playback!.Items.Single();
        Assert.NotEqual(defs[EffectColorsTests.Sys].Emitters[0].BirthColor.Values, item.System.Emitters[0].BirthColor.Values);

        // the first field off: that colour is the skin's again, the others stay recoloured
        var system = card.ChromaEffectSystems.Single();
        system.IsExpanded = true;
        var birth = system.Fields.Single(f => f.Key.Emitter == 0 && f.Key.Field == "birthColor");
        birth.IsIncluded = false;
        await card.ChromaIdleAsync();
        var now = card.Playback!.Items.Single().System;
        Assert.Equal(defs[EffectColorsTests.Sys].Emitters[0].BirthColor.Values, now.Emitters[0].BirthColor.Values);
        Assert.Equal(defs[EffectColorsTests.Sys].Emitters[0].BirthColor.Constant, now.Emitters[0].BirthColor.Constant);
        Assert.NotEqual(defs[EffectColorsTests.Sys].Emitters[1].BirthColor.Constant, now.Emitters[1].BirthColor.Constant);
    }
    // ===================================================================== review fixes (M826 round 2)

    private static int OriginalsRemembered(MeshPreviewViewModel card) =>
        (int)typeof(MeshPreviewViewModel).GetProperty("EffectOriginalCount", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(card)!;

    [Fact]
    public async Task ManySliderTicksDoNotGrowWhatThePreviewRemembersOfTheOriginals()
    {
        var host = new FakeHost { WithPreview = true };
        var card = Card(host);
        var defs = VfxSystemResolver.ExtractAll(host.Riot);
        card.SetVfx(defs, new Dictionary<uint, uint>());
        var sprite = new TextureImage(32, 32, TextureDecoder.Decode(Tex()).Rgba);
        card.Playback = new VfxPlayback(new[] { new VfxPlaybackItem(defs[EffectColorsTests.Sys], Vector3.Zero, new TextureImage?[] { sprite, null }) { Seed = 1 } });
        await Scan(card);
        await EffectsOn(card);
        for (int i = 1; i <= 60; i++)
        {
            card.ChromaHue = i * 2;             // 60 slider positions: each makes a new recoloured copy of the sprite
            await card.ChromaIdleAsync();
        }
        Assert.NotSame(sprite, card.Playback!.Items[0].EmitterTextures[0]);   // the last copy is on the playing item

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        // each position's copy was dropped by the item that held it, and the card's map held nothing but the weak key: it does not keep sixty copies of the sprite alive
        Assert.True(OriginalsRemembered(card) <= 2, $"the card still remembers {OriginalsRemembered(card)} recoloured copies");

        // and what it does remember still gives the original back
        card.ChromaHue = 0;
        await card.ChromaIdleAsync();
        Assert.Equal(sprite.Rgba, card.Playback!.Items[0].EmitterTextures[0]!.Rgba);     // no change: the sprite's own pixels (a copy of them, as the preview draws a recolour off the screen)
    }

    [Fact]
    public async Task NothingIsCopiedOrPushedForATextureNoPlayingItemDraws_AnItemPlayedLaterAsksForIt()
    {
        var host = new FakeHost { WithPreview = true };
        var card = Card(host);
        card.UseDx11Preview = true;
        var queue = new List<Action>();
        card.ChromaUiPost = a => { lock (queue) queue.Add(a); return Task.CompletedTask; };   // the UI thread runs these when the test says so, as the dispatcher would
        void Drain()
        {
            List<Action> todo;
            lock (queue) { todo = queue.ToList(); queue.Clear(); }
            foreach (var a in todo) a();
        }
        var defs = VfxSystemResolver.ExtractAll(host.Riot);
        card.SetVfx(defs, new Dictionary<uint, uint>());
        var sprite = new TextureImage(32, 32, TextureDecoder.Decode(Tex()).Rgba);
        card.ResolveTextures = _ => new TextureImage?[] { sprite, null };
        await Scan(card);
        await EffectsOn(card);
        Drain();

        card.ChromaHue = 120;                                                    // nothing plays: the recolour of the sprite is not made for anybody
        await card.ChromaIdleAsync();
        Drain();
        Assert.Empty(host.Pushed);
        Assert.Null(card.Playback);

        card.SelectedVfx = card.VfxSystems.Single();                             // an item is built that draws the sprite: it asks, once published
        Drain();
        await card.ChromaIdleAsync();
        Drain();
        var item = Assert.Single(card.Playback!.Items);
        Assert.NotSame(sprite, item.EmitterTextures[0]);
        var wanted = SkinEffectColors.TextureTransform(card.ChromaSettings.ToTransform()).Apply(TextureDecoder.Decode(Tex())).Rgba;
        Assert.Equal(wanted, item.EmitterTextures[0]!.Rgba);
        Assert.Contains(host.Pushed, p => p.Key == SpritePath.ToLowerInvariant() && p.Rgba.SequenceEqual(wanted));
        Assert.DoesNotContain(host.Pushed, p => p.Key == SharedSpritePath.ToLowerInvariant());   // the shared sprite is not switched on
    }

    [Fact]
    public async Task AMoveOfBrightnessAloneLeavesTheTexturePartEmpty_SoTheHueRecolouredTexturesAreGivenBack_AndNothingStaysPending()
    {
        var host = new FakeHost();
        var card = await Scanned(host);
        await EffectsOn(card);
        card.ChromaHue = 120;
        await card.SaveChromaRecolourNowAsync();
        var hueSave = Assert.Single(host.TextureSaves);
        Assert.Single(hueSave.Targets);                                          // the sprite took the hue

        card.ChromaHue = 0;                                                      // hue back, brightness only: the textures take the hue and nothing else
        card.ChromaBrightness = 1.3;
        Assert.True(SkinEffectColors.TextureTransform(card.ChromaSettings.ToTransform()).IsIdentity);
        Assert.True(card.ChromaRecolourDirty);                                   // the saved texture recolour is no longer what the sliders say
        await card.SaveChromaRecolourNowAsync();

        var second = host.TextureSaves.Last();
        Assert.Empty(second.Targets);
        Assert.Equal(new[] { BinTexturePath.HashOfReference(SpritePath) }, second.Stale.Select(t => t.Hash));   // the hue recolour of the sprite is given back, not orphaned
        Assert.Equal(card.ChromaSettings.ToTransform(), host.ColorSaves.Last().Transform);
        Assert.NotEmpty(host.ColorSaves.Last().Targets);                         // the colour values do carry the brightness
        Assert.False(card.ChromaRecolourDirty, card.ChromaRecolourStatus);       // and the card settles instead of staying pending forever
        Assert.False(card.ChromaAutoSaveDue);
    }

    private static Func<EffectColorField, (bool, bool, string?)> Flags(bool ownedEdited = false) => f =>
        f.Key.Emitter == 0 && f.Key.Field == "birthColor" ? (true, false, null)                                  // the project's value was edited outside the recolour
        : f.Key.Emitter == 1 && f.Key.Field == "birthColor" ? (false, false, "This bin is changed by the imported mod's GameData: off until you switch it on.")
        : ownedEdited && f.Key.Emitter == 0 && f.Key.Field == "color" ? (false, true, null)                      // the Particle Editor changed an owned value since
        : (false, false, null);

    [Fact]
    public async Task AllColoursAndASystemSwitchLeaveEditedAndDefaultOffFieldsAlone_TheyAreSwitchedOnByHand_AndTheWarningSaysTheEditIsReplaced()
    {
        var host = new FakeHost { Flag = Flags() };
        var card = await Scanned(host);
        var system = card.ChromaEffectSystems.Single();
        system.IsExpanded = true;
        ChromaEffectFieldRowViewModel Row(int emitter, string field) => system.Fields.Single(f => f.Key.Emitter == emitter && f.Key.Field == field);

        card.SelectAllEffectColorsCommand.Execute(null);
        Assert.False(Row(0, "birthColor").IsIncluded);                           // edited outside: never swept up by All colours
        Assert.False(Row(1, "birthColor").IsIncluded);                           // left off by default: nor this
        Assert.True(Row(0, "color").IsIncluded);                                 // the unedited ones are
        Assert.True(system.IsIncluded);
        Assert.DoesNotContain("effect colour value(s) were edited", card.ChromaEffectShareWarning);

        system.IsIncluded = false;                                               // the system switch: off takes everything...
        Assert.All(system.Fields, f => Assert.False(f.IsIncluded));
        system.IsIncluded = true;                                                // ...on takes only what nobody changed
        Assert.False(Row(0, "birthColor").IsIncluded);
        Assert.False(Row(1, "birthColor").IsIncluded);
        Assert.True(Row(0, "color").IsIncluded);

        card.ChromaHue = 90;
        await card.SaveChromaRecolourNowAsync();
        var targets = host.ColorSaves.Last().Targets;
        Assert.DoesNotContain(targets, t => t.Emitter == 0 && t.Field == "birthColor");
        Assert.DoesNotContain(targets, t => t.Emitter == 1 && t.Field == "birthColor");

        Row(0, "birthColor").IsIncluded = true;                                  // by hand: that is the person choosing to replace the edit
        Assert.Contains("1 effect colour value(s) were edited", card.ChromaEffectShareWarning);
        Assert.Contains("replaces that edit", card.ChromaEffectShareWarning);
        card.SelectNoEffectColorsCommand.Execute(null);                          // none takes everything
        Assert.All(system.Fields, f => Assert.False(f.IsIncluded));
        Assert.False(card.ChromaEffectShareWarning.Contains("effect colour value(s) were edited"));
    }

    [Fact]
    public async Task AnOwnedFieldTheParticleEditorChangedIsNotSweptUpByAllColoursEither()
    {
        var host = new FakeHost { Flag = Flags(ownedEdited: true) };
        var riot = SkinEffectColors.Read(host.Riot).Fields;
        var refs = riot.Where(f => f.Recolourable && f.Key.Emitter == 0 && f.Key.Field == "color")
            .Select(f => new ChromaEffectColorRef { System = f.Key.System, Emitter = f.Key.Emitter, Field = f.Key.Field, Bin = BinPath, SystemName = f.SystemName, EmitterName = f.EmitterName }).ToList();
        var t0 = new ColorTransform { HueShiftDegrees = 60f };
        host.Saved = new ChromaSavedRecipe(t0, Array.Empty<ChromaTarget>(), null, null, new ChromaSavedEffects(t0, refs, Array.Empty<ChromaTarget>()));
        var card = await Scanned(host);
        var system = card.ChromaEffectSystems.Single();
        system.IsExpanded = true;
        var owned = system.Fields.Single(f => f.Key.Emitter == 0 && f.Key.Field == "color");
        Assert.False(owned.IsIncluded);                                           // kept as edited

        card.SelectAllEffectColorsCommand.Execute(null);
        Assert.False(owned.IsIncluded, "All colours replaced the Particle Editor's edit");
        system.IsIncluded = false;
        system.IsIncluded = true;
        Assert.False(owned.IsIncluded, "the system switch replaced the Particle Editor's edit");

        owned.IsIncluded = true;                                                  // switched on by hand: replaced on purpose, and the warning says so
        Assert.Contains("effect colour value(s) were edited", card.ChromaEffectShareWarning);
        Assert.True(card.ChromaRecolourDirty);                                    // a change of state even though the sliders and the set did not change
    }

    [Fact]
    public async Task ThePreviewOfASavedRecipeGivesAFieldSwitchedOffBackToRiotsValue_AsSavingWouldWriteIt_AndSoDoesResettingTheSliders()
    {
        var host = new FakeHost { WithPreview = true };
        var riot = host.Riot;
        var t0 = new ColorTransform { HueShiftDegrees = 60f };
        var scan = SkinEffectColors.Read(riot);
        var keys = scan.Fields.Where(f => f.Recolourable).Select(f => f.Key).ToList();
        var current = SkinEffectColors.Rewrite(riot, riot, t0, keys, Array.Empty<EffectColorKey>()).Bytes!;     // the project holds the saved recolour
        host.Current = current;
        var refs = scan.Fields.Where(f => f.Recolourable)
            .Select(f => new ChromaEffectColorRef { System = f.Key.System, Emitter = f.Key.Emitter, Field = f.Key.Field, Bin = BinPath, SystemName = f.SystemName, EmitterName = f.EmitterName }).ToList();
        host.Saved = new ChromaSavedRecipe(t0, Array.Empty<ChromaTarget>(), null, null, new ChromaSavedEffects(t0, refs, Array.Empty<ChromaTarget>()));

        var card = Card(host);
        var skinDefs = VfxSystemResolver.ExtractAll(current);                    // what the skin plays: the project's colours
        var riotDefs = VfxSystemResolver.ExtractAll(riot);
        card.SetVfx(skinDefs, new Dictionary<uint, uint>());
        card.Playback = new VfxPlayback(new[] { new VfxPlaybackItem(skinDefs[EffectColorsTests.Sys], Vector3.Zero, new TextureImage?[2]) { Seed = 1 } });
        await Scan(card);
        Assert.Equal(60.0, card.ChromaHue);

        card.ChromaHue = 200;                                                     // the saved fields follow the sliders
        await card.ChromaIdleAsync();
        var system = card.ChromaEffectSystems.Single();
        system.IsExpanded = true;
        var off = system.Fields.Single(f => f.Key.Emitter == 0 && f.Key.Field == "birthColor");
        Assert.True(off.IsIncluded);
        Assert.NotEqual(skinDefs[EffectColorsTests.Sys].Emitters[0].BirthColor.Values, card.Playback!.Items[0].System.Emitters[0].BirthColor.Values);

        off.IsIncluded = false;                                                   // switched off: saving gives it back to Riot's, so that is what plays
        await card.ChromaIdleAsync();
        var t1 = card.ChromaSettings.ToTransform();
        var included = keys.Where(k => k != off.Key).ToList();
        var saved = VfxSystemResolver.ExtractAll(SkinEffectColors.Rewrite(current, riot, t1, included, new[] { off.Key }, t0, keys).Bytes!)[EffectColorsTests.Sys];
        var shown = card.Playback!.Items[0].System;
        Assert.Equal(riotDefs[EffectColorsTests.Sys].Emitters[0].BirthColor.Values, saved.Emitters[0].BirthColor.Values);   // (what the save writes is Riot's)
        Assert.Equal(saved.Emitters[0].BirthColor.Values, shown.Emitters[0].BirthColor.Values);
        Assert.Equal(saved.Emitters[0].BirthColor.Constant, shown.Emitters[0].BirthColor.Constant);
        Assert.Equal(saved.Emitters[1].BirthColor.Constant, shown.Emitters[1].BirthColor.Constant);
        Assert.Equal(saved.Emitters[0].ColorOverLife!.Value.Values, shown.Emitters[0].ColorOverLife!.Value.Values);

        card.ChromaHue = 0;                                                       // reset: every saved field goes back to Riot's
        await card.ChromaIdleAsync();
        var reset = card.Playback!.Items[0].System;
        Assert.Equal(riotDefs[EffectColorsTests.Sys].Emitters[0].BirthColor.Values, reset.Emitters[0].BirthColor.Values);
        Assert.Equal(riotDefs[EffectColorsTests.Sys].Emitters[0].ColorOverLife!.Value.Values, reset.Emitters[0].ColorOverLife!.Value.Values);
        Assert.Equal(riotDefs[EffectColorsTests.Sys].Emitters[1].BirthColor.Constant, reset.Emitters[1].BirthColor.Constant);
    }

    [Fact]
    public async Task AColourTextureThatIsAlsoReadAsADataMapIsListedAsUnchangeable_WithTheReason()
    {
        var host = new FakeHost();
        host.ExtraExcluded.Add(new EffectExcludedField(EffectColorsTests.Sys, 0, "Zz_Skin1_Q", "Flame", "falloffTexture", SpritePath, "A data map, not a colour."));
        var card = await Scanned(host);
        var row = card.ChromaEffectTextures.Single(r => r.Name == "spark.tex");
        Assert.False(row.CanInclude);
        Assert.Contains("data map", row.Note);
        Assert.Contains("falloff", row.Note);
        card.SelectAllEffectTexturesCommand.Execute(null);
        Assert.False(row.IsIncluded);
        Assert.Empty(card.ChromaEffectTextures.Where(r => r.IsIncluded && r.Name == "spark.tex"));
    }

    [Fact]
    public void TheRecordKeepsTheEmitterNamesAndTheTransformOfABinAnInterruptedSaveLeftBehind()
    {
        var record = new ChromaEffectRecord { ChromaSkin = SkinBinPath, Transform = new ColorTransform { HueShiftDegrees = 60f } };
        record.Colors = new List<ChromaEffectColorRef>
        {
            new() { System = 7, Emitter = 2, Field = "birthColor", Bin = "a.bin", SystemName = "S", EmitterName = "Flame" },
            new() { System = 7, Emitter = 3, Field = "color", Bin = "a.bin", SystemName = "S", EmitterName = "Glow" },
        };
        var json = System.Text.Json.JsonSerializer.Serialize(record);
        var back = System.Text.Json.JsonSerializer.Deserialize<ChromaEffectRecord>(json)!;
        Assert.Equal(new[] { "Flame", "Glow" }, back.Colors.Select(c => c.EmitterName));
        Assert.Null(back.BinTransforms);
        Assert.Equal(record.Transform, back.TransformOf("a.bin"));

        record.BinTransforms = new Dictionary<string, ColorTransform> { ["b.bin"] = new ColorTransform { HueShiftDegrees = 99f } };
        back = System.Text.Json.JsonSerializer.Deserialize<ChromaEffectRecord>(System.Text.Json.JsonSerializer.Serialize(record))!;
        Assert.Equal(99f, back.TransformOf("B.BIN").HueShiftDegrees);
        Assert.Equal(60f, back.TransformOf("a.bin").HueShiftDegrees);
    }
}

/// <summary>Shared fixture builders of the Chroma tests that live in another class.</summary>
internal static class ChromaRecolourTestsAccess
{
    public static TextureImage Bands(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                (byte r, byte g, byte b) = (y * 4 / h) switch { 0 => ((byte)220, (byte)40, (byte)40), 1 => ((byte)40, (byte)200, (byte)60), 2 => ((byte)40, (byte)60, (byte)220), _ => ((byte)130, (byte)130, (byte)130) };
                int i = (y * w + x) * 4;
                px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = 255;
            }
        return new TextureImage(w, h, px);
    }
}
