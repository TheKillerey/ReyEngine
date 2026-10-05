using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Build;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M824: Chroma Studio C3, BODY RECOLOUR - every promise that needs no game install: the sliders and their transform, the extended
/// project record and old project files, the texture pipeline with the M813 transform, the live preview's maths and routing, the card's
/// state machine, and the host end to end (a folder project, a champion WAD packed here, Apply &amp; Save, Ctrl+S, reload, export,
/// revert). <see cref="ChromaRecolourRealDataTests"/> holds the same flow on Riot's own Lillia.
/// </summary>
public sealed class ChromaRecolourTests : IDisposable
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "reyengine-m824u-" + Guid.NewGuid().ToString("N"));

    public ChromaRecolourTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    // ===================================================================== images and textures

    /// <summary>Four bands - red, green, blue, grey - so a hue shift moves three of them and leaves the fourth.</summary>
    private static TextureImage Bands(int w, int h, bool alphaRamp = false)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                (byte r, byte g, byte b) = (y * 4 / h) switch
                {
                    0 => ((byte)220, (byte)40, (byte)40),
                    1 => ((byte)40, (byte)200, (byte)60),
                    2 => ((byte)40, (byte)60, (byte)220),
                    _ => ((byte)130, (byte)130, (byte)130),
                };
                int i = (y * w + x) * 4;
                px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = alphaRamp ? (byte)(255 - x * 255 / Math.Max(1, w - 1)) : (byte)255;
            }
        return new TextureImage(w, h, px);
    }

    private static byte[] Bc1(int w = 64, int h = 64) => TexWriter.Write(Bands(w, h), TexFormat.Bc1, mipmaps: true);
    private static byte[] Bc3(int w = 32, int h = 32) => TexWriter.Write(Bands(w, h, alphaRamp: true), TexFormat.Bc3, mipmaps: true);

    private static double MeanAbsError(byte[] a, byte[] b, int channels = 3)
    {
        Assert.Equal(a.Length, b.Length);
        double sum = 0; long n = 0;
        for (int i = 0; i < a.Length; i += 4)
            for (int c = 0; c < channels; c++) { sum += Math.Abs(a[i + c] - b[i + c]); n++; }
        return sum / n;
    }

    // ===================================================================== the sliders

    [Fact]
    public void TheDefaultSlidersChangeNothing_AndTheGreyGuardIsOnAtTheMeasuredThreshold()
    {
        var d = ChromaRecolourSettings.Default;
        Assert.True(d.ToTransform().IsIdentity);
        Assert.Equal(0.08f, d.GreyThreshold);          // BC1/BC3 greys are ~1.5% off-grey: an exact-grey guard would almost never fire
        Assert.NotNull(d.ToTransform().GreyProtection);
        Assert.Null(d.ToTransform().ColorizeHueDegrees);
        Assert.Null(d.ToTransform().HueSelection);
    }

    [Fact]
    public void AGuardOfZeroThresholdAndZeroFeatherIsNoGuard()
    {
        var t = (ChromaRecolourSettings.Default with { GreyThreshold = 0f, GreyFeather = 0f }).ToTransform();
        Assert.Null(t.GreyProtection);
        // but a feather alone is still a guard: it is the far end of the threshold slider that switches it off
        Assert.NotNull((ChromaRecolourSettings.Default with { GreyThreshold = 0f, GreyFeather = 0.1f }).ToTransform().GreyProtection);
    }

    [Fact]
    public void TheSlidersAndTheTransformConvertBothWays()
    {
        foreach (var settings in new[]
        {
            ChromaRecolourSettings.Default,
            ChromaRecolourSettings.Default with { HueShift = -75f, Saturation = 1.4f, Brightness = 0.6f, Strength = 0.5f },
            ChromaRecolourSettings.Default with { Colorize = true, ColorizeHue = 275f, GreyThreshold = 0f, GreyFeather = 0f },
            ChromaRecolourSettings.Default with { RangeOn = true, RangeCenter = 350f, RangeWidth = 40f, RangeFeather = 10f, HueShift = 120f },
        })
        {
            var transform = settings.ToTransform();
            var back = ChromaRecolourSettings.From(transform).ToTransform();
            Assert.Equal(transform, back);
        }
    }

    // ===================================================================== the project record

    [Fact]
    public void TheRecordKeepsItsTransformAcrossTheProjectFile()
    {
        var transform = new ColorTransform
        {
            HueShiftDegrees = 33.5f, Saturation = 1.2f, Brightness = 0.9f, ColorizeHueDegrees = null,
            HueSelection = new HueRange(10f, 50f, 20f), GreyProtection = new GreyProtection(0.08f, 0.06f), Strength = 0.75f,
        };
        string file = Path.Combine(_dir, "project.json");
        var project = new ReyProject { Name = "t" };
        project.TextureRecolors.Add(new TextureRecolorRecord { PathHash = 0xABCDEF0123456789UL, AssetPath = "assets/a.tex", Transform = transform, ChromaSkin = "data/characters/x/skins/skin1.bin" });
        ReyProjectService.Save(project, file);

        var loaded = ReyProjectService.Open(file);
        var record = Assert.Single(loaded.TextureRecolors);
        Assert.Equal(transform, record.Transform);
        Assert.Equal("data/characters/x/skins/skin1.bin", record.ChromaSkin);
        Assert.Equal(0xABCDEF0123456789UL, record.PathHash);
        // M171's own fields are untouched by it
        Assert.Equal(1f, record.Saturation);
        Assert.Equal(0f, record.HueDegrees);
    }

    [Fact]
    public void AnOldProjectFileLoadsAsItWasAndWritesNoNewKeys()
    {
        const string old = """
            {
              "Name": "old",
              "TextureRecolors": [
                { "PathHash": 1234, "AssetPath": "assets/maps/x.tex", "BaseSnapshot": null,
                  "HueDegrees": 45, "Saturation": 1.1, "Brightness": 1, "Contrast": 1, "InputBlack": 0, "InputWhite": 1, "Gamma": 1,
                  "TintR": 1, "TintG": 1, "TintB": 1, "Strength": 0.5 }
              ]
            }
            """;
        var project = JsonSerializer.Deserialize<ReyProject>(old)!;
        var record = Assert.Single(project.TextureRecolors);
        Assert.Null(record.Transform);
        Assert.Null(record.ChromaSkin);
        Assert.Equal(45f, record.HueDegrees);
        Assert.Equal(0.5f, record.Strength);

        string file = Path.Combine(_dir, "old.json");
        ReyProjectService.Save(project, file);
        string written = File.ReadAllText(file);
        Assert.DoesNotContain("\"Transform\"", written);
        Assert.DoesNotContain("\"ChromaSkin\"", written);
        Assert.Contains("\"HueDegrees\": 45", written);
    }

    // ===================================================================== the texture pipeline with the M813 transform

    [Theory]
    [InlineData(TexFormat.Bc1)]
    [InlineData(TexFormat.Bc3)]
    public void TheTransformIsAppliedAndTheContainerFormatAndMipsAreKept(TexFormat format)
    {
        byte[] original = format == TexFormat.Bc1 ? Bc1() : Bc3();
        var transform = new ColorTransform { HueShiftDegrees = 120f, GreyProtection = new GreyProtection(0.08f, 0.06f) };

        var outcome = TextureRecolor.Apply(original, transform);
        Assert.True(outcome.Ok, outcome.Detail);
        var bytes = outcome.Bytes!;
        Assert.Equal(format, TexWriter.DetectFormat(bytes));
        Assert.Equal(TextureRecolor.HasMips(original), TextureRecolor.HasMips(bytes));
        Assert.Equal(original.Length, bytes.Length);

        var source = TextureDecoder.Decode(original);
        var expected = transform.Apply(source);
        var actual = TextureDecoder.Decode(bytes);
        Assert.Equal(source.Width, actual.Width);
        Assert.True(MeanAbsError(expected.Rgba, actual.Rgba) < 8.0, "the written texture is not the transform of the original");
        // alpha is never touched (BC3 keeps its ramp up to its own quantisation)
        Assert.True(MeanAbsError(source.Rgba.Select((b, i) => i % 4 == 3 ? b : (byte)0).ToArray(),
            actual.Rgba.Select((b, i) => i % 4 == 3 ? b : (byte)0).ToArray(), 4) < 4.0);
        // and the picture really moved
        Assert.True(MeanAbsError(source.Rgba, actual.Rgba) > 20.0);
    }

    [Fact]
    public void ATransformThatChangesNothingWritesNothing()
    {
        byte[] original = Bc1();
        Assert.Equal(RecolorSkip.NoChange, TextureRecolor.Apply(original, new ColorTransform()).Skip);

        // a range on a hue the texture does not have (the bands are red, green, blue and grey; yellow-orange 60 +- 5 selects none)
        var none = new ColorTransform { HueShiftDegrees = 90f, HueSelection = new HueRange(60f, 10f, 0f) };
        var outcome = TextureRecolor.Apply(original, none);
        Assert.False(outcome.Ok);
        Assert.Equal(RecolorSkip.NoChange, outcome.Skip);
    }

    [Fact]
    public void FormatsTheWriterCannotWriteAreSkippedWithAReason()
    {
        var dds = new byte[200]; dds[0] = (byte)'D'; dds[1] = (byte)'D'; dds[2] = (byte)'S'; dds[3] = (byte)' ';
        Assert.Equal(RecolorSkip.NotATexture, TextureRecolor.Apply(dds, new ColorTransform { HueShiftDegrees = 90f }).Skip);

        byte[] bc5 = Bc3();
        bc5[9] = 14;   // the pixel format Riot's BC5 normal maps carry
        var outcome = TextureRecolor.Apply(bc5, new ColorTransform { HueShiftDegrees = 90f });
        Assert.Equal(RecolorSkip.UnsupportedFormat, outcome.Skip);
        Assert.Null(outcome.Bytes);
    }

    [Fact]
    public async Task TheServiceRunsTheTransformAndReportsWhatItLeftAlone()
    {
        var written = new List<(RecolorTarget Target, int Length)>();
        var bc1 = Bc1();
        var white = TexWriter.Write(new TextureImage(8, 8, Enumerable.Repeat((byte)255, 8 * 8 * 4).ToArray()), TexFormat.Bc1, mipmaps: false);
        var service = TextureRecolorService.ForTargets(
            t => t.PathHash == 1 ? bc1 : white,
            (target, bytes, ext) => { written.Add((target, bytes.Length)); return "x"; });
        var targets = new[] { new RecolorTarget(1, "assets/a.tex"), new RecolorTarget(2, "0x0000000000000002") };

        var result = await service.RunAsync(targets, new ColorTransform { HueShiftDegrees = 90f });

        Assert.Equal(1, result.Written);
        Assert.Equal(1, result.Skipped);                       // white stays white
        Assert.Equal(0, result.Failed);
        Assert.Equal(1UL, Assert.Single(written).Target.PathHash);
        Assert.Equal(2UL, Assert.Single(result.SkippedTargets!).PathHash);
        Assert.Equal(1UL, Assert.Single(result.WrittenTargets).PathHash);
    }

    // ===================================================================== the live preview's maths

    [Fact]
    public void AnOriginalIsRecolouredIntoACopy_NeverCompounding()
    {
        var original = Bands(300, 200);   // large enough for the banded parallel pass
        var buffers = new ChromaPreviewBuffers();
        buffers.Set(7, original);
        var before = (byte[])original.Rgba.Clone();

        var a = new ColorTransform { HueShiftDegrees = 80f, GreyProtection = new GreyProtection(0.08f, 0.06f) };
        var b = new ColorTransform { Saturation = 0.5f, Brightness = 1.2f };

        var first = buffers.Render(7, a)!.ToArray();
        Assert.Equal(a.Apply(original).Rgba, first);                           // the banded pass equals the whole-image pass
        var viaB = buffers.Render(7, b)!.ToArray();
        Assert.Equal(b.Apply(original).Rgba, viaB);                           // after A, B is B of the ORIGINAL, not B of A
        Assert.Equal(first, buffers.Render(7, a)!.ToArray());                  // and A again is A again
        Assert.Equal(before, original.Rgba);                                  // the original is never written
        Assert.Equal(before, buffers.Render(7, ColorTransform.Identity));     // the identity draws the original back
        Assert.Null(buffers.Render(8, a));
    }

    // ===================================================================== the card

    private static SkinColorInventory Inventory(params BodyTexture[] textures) =>
        new("data/characters/zz/skins/skin1.bin", "zz", 1, "zz skin 1", textures, Array.Empty<BodyColorParameter>(),
            new[] { new ExcludedSampler("Mat_inst", "Mask_Texture", "assets/characters/zz/skins/skin1/mask.tex", "mask") },
            Array.Empty<EffectSystemEntry>(), Array.Empty<EffectTextureEntry>(), EffectReach.None,
            new[] { "skin0", "skin2" }, true, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

    private static BodyTexture Body(string path, BodyTextureRole role, params string[] sharedWith) =>
        new(path, BinTexturePath.HashOfReference(path), role,
            new[] { new BodyTextureUse("(skin default texture)", "texture", Array.Empty<string>(), true) }, false, null, sharedWith);

    private const string Bin1 = "data/characters/zz/skins/skin1.bin";
    private const string OwnDiffuse = "assets/characters/zz/skins/skin1/zz_tx_cm.tex";
    private const string OwnGlow = "assets/characters/zz/skins/skin1/zz_glow.tex";
    private const string OutsideMatcap = "assets/shared/matcap/gold.tex";
    private const string Cubemap = "assets/characters/zz/skins/skin1/zz_cube.dds";
    private const string Mask = "assets/characters/zz/skins/skin1/mask.tex";

    private static byte[] FakeDds() { var b = new byte[256]; b[0] = (byte)'D'; b[1] = (byte)'D'; b[2] = (byte)'S'; b[3] = (byte)' '; return b; }

    private Dictionary<ulong, byte[]> Files() => new()
    {
        [BinTexturePath.HashOfReference(OwnDiffuse)] = Bc1(64, 64),
        [BinTexturePath.HashOfReference(OwnGlow)] = Bc3(32, 32),
        [BinTexturePath.HashOfReference(OutsideMatcap)] = Bc1(16, 16),
        [BinTexturePath.HashOfReference(Cubemap)] = FakeDds(),
        [BinTexturePath.HashOfReference(Mask)] = Bc3(32, 32),
    };

    private static MeshPreviewViewModel Card(Dictionary<ulong, byte[]> files, SkinColorInventory inventory)
    {
        var card = new MeshPreviewViewModel
        {
            ReadChromaOriginal = t => files.GetValueOrDefault(t.Hash),
            ScanSkinColours = (_, _) => Task.FromResult(inventory),
            ChromaUiPost = a => { a(); return Task.CompletedTask; },
        };
        card.SetChromaSkin(Bin1);
        return card;
    }

    private static SkinColorInventory TypicalInventory() => Inventory(
        Body(OwnDiffuse, BodyTextureRole.Diffuse, "skin0 (Original)", "skin2"),
        Body(OwnGlow, BodyTextureRole.Emissive),
        Body(OutsideMatcap, BodyTextureRole.MatCap),
        Body(Cubemap, BodyTextureRole.Reflection));

    private static async Task Scan(MeshPreviewViewModel card)
    {
        await card.ScanColoursCommand.ExecuteAsync(null);
        await card.ChromaIdleAsync();
    }

    [Fact]
    public async Task TheListDefaultsToTheBodysOwnColourTexturesAndExplainsWhatItCannotDo()
    {
        var card = Card(Files(), TypicalInventory());
        Assert.False(card.HasChromaTextures);   // nothing is read until the scan
        await Scan(card);

        Assert.True(card.HasChromaTextures);
        var byName = card.ChromaTextures.ToDictionary(r => r.Name);
        Assert.True(byName["zz_tx_cm.tex"].IsIncluded);
        Assert.True(byName["zz_glow.tex"].IsIncluded);
        Assert.False(byName["gold.tex"].IsIncluded);                  // outside the character: also used elsewhere, opt-in
        Assert.True(byName["gold.tex"].IsOutside);

        var cube = byName["zz_cube.dds"];                              // the reflection cubemap: DDS, which the writer does not write
        Assert.False(cube.CanInclude);
        Assert.False(cube.IsIncluded);
        Assert.Contains("DDS", cube.Note);

        Assert.DoesNotContain(card.ChromaTextures, r => r.Name == "mask.tex");   // a mask is not listed until asked for
        card.ChromaShowExcluded = true;
        var mask = Assert.Single(card.ChromaTextures, r => r.Name == "mask.tex");
        Assert.True(mask.IsOptIn);
        Assert.False(mask.IsIncluded);
        Assert.Contains("mask", mask.Detail);
    }

    [Fact]
    public async Task SavingWarnsAboutEverySkinThatSharesAnIncludedTexture()
    {
        var card = Card(Files(), TypicalInventory());
        await Scan(card);
        Assert.Contains("also changes 2 other", card.ChromaShareWarning);
        Assert.Contains("skin0 (Original)", card.ChromaShareWarning);
        Assert.Contains("skin2", card.ChromaShareWarning);
        Assert.True(card.HasChromaShareWarning);

        // switch the shared one off and there is nothing to warn about; the outside one on brings its own word
        var shared = card.ChromaTextures.Single(r => r.Name == "zz_tx_cm.tex");
        shared.IsIncluded = false;
        Assert.False(card.HasChromaShareWarning);
        card.ChromaTextures.Single(r => r.Name == "gold.tex").IsIncluded = true;
        await card.ChromaIdleAsync();
        Assert.Contains("outside this character", card.ChromaShareWarning);
    }

    [Fact]
    public async Task ASliderPushesTheTransformOfTheOriginalToTheD3D11PoolKeysOfTheScene()
    {
        var files = Files();
        var card = Card(files, TypicalInventory());
        var diffuse = TextureDecoder.Decode(files[BinTexturePath.HashOfReference(OwnDiffuse)]);
        var glow = TextureDecoder.Decode(files[BinTexturePath.HashOfReference(OwnGlow)]);
        // the pool keys are the scene's lower-cased paths; an unnamed chunk is its 0x form
        var scene = new PreparedCharacterScene
        {
            Mesh = null!, Slices = new List<CharacterSlice>(),
            Textures = new Dictionary<string, TextureImage>(StringComparer.OrdinalIgnoreCase)
            {
                [OwnDiffuse.ToLowerInvariant()] = diffuse,
                [OwnGlow.ToLowerInvariant()] = glow,
            },
        };
        card.SetDx11Scene(scene, "");
        card.UseDx11Preview = true;
        var pushed = new Dictionary<string, byte[]>();
        card.PushChromaDx11 = items => { foreach (var (key, rgba, _, _) in items) pushed[key] = rgba.ToArray(); };
        await Scan(card);

        card.ChromaHue = 120;
        await card.ChromaIdleAsync();

        var transform = card.ChromaSettings.ToTransform();
        Assert.Equal(transform.Apply(diffuse).Rgba, pushed[OwnDiffuse.ToLowerInvariant()]);
        Assert.Equal(transform.Apply(glow).Rgba, pushed[OwnGlow.ToLowerInvariant()]);
        Assert.Equal(2, pushed.Count);                                  // the outside matcap is off, the cubemap cannot be

        // the slider back at no change draws the original texels
        card.ChromaHue = 0;
        await card.ChromaIdleAsync();
        Assert.Equal(diffuse.Rgba, pushed[OwnDiffuse.ToLowerInvariant()]);
    }

    [Fact]
    public async Task ATextureTheSceneDrawsAtAnotherSizeIsNotOverwritten()
    {
        var files = Files();
        var card = Card(files, TypicalInventory());
        var scene = new PreparedCharacterScene
        {
            Mesh = null!, Slices = new List<CharacterSlice>(),
            // the scene decoded a 8x8 image under this key: a pool texture of that size must not receive 64x64 texels
            Textures = new Dictionary<string, TextureImage>(StringComparer.OrdinalIgnoreCase)
            {
                [OwnDiffuse.ToLowerInvariant()] = new TextureImage(8, 8, new byte[8 * 8 * 4]),
            },
        };
        card.SetDx11Scene(scene, "");
        card.UseDx11Preview = true;
        int calls = 0;
        card.PushChromaDx11 = items => calls += items.Count;
        await Scan(card);
        card.ChromaHue = 120;
        await card.ChromaIdleAsync();
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task WithOpenGlOnTheGlHostGetsTheSameTexelsAndD3D11IsLeftAlone()
    {
        var files = Files();
        var card = Card(files, TypicalInventory());
        card.UseDx11Preview = false;
        var gl = new List<ChromaPushItem>();
        card.PushChromaGl = items => { gl.Clear(); gl.AddRange(items); };
        bool dx = false;
        card.PushChromaDx11 = _ => dx = true;
        await Scan(card);
        card.ChromaHue = 200;
        await card.ChromaIdleAsync();

        var transform = card.ChromaSettings.ToTransform();
        Assert.False(dx);
        Assert.Equal(2, gl.Count);
        foreach (var item in gl)
            Assert.Equal(transform.Apply(TextureDecoder.Decode(files[item.Hash])).Rgba, item.Rgba);
    }

    [Fact]
    public async Task ManyQuickSliderMovesEndOnTheLastOne()
    {
        var files = Files();
        var card = Card(files, TypicalInventory());
        card.UseDx11Preview = false;
        byte[]? last = null;
        card.PushChromaGl = items => last = items.First(i => i.Hash == BinTexturePath.HashOfReference(OwnDiffuse)).Rgba.ToArray();
        await Scan(card);
        for (int hue = 1; hue <= 90; hue++) card.ChromaHue = hue;
        await card.ChromaIdleAsync();
        Assert.Equal(card.ChromaSettings.ToTransform().Apply(TextureDecoder.Decode(files[BinTexturePath.HashOfReference(OwnDiffuse)])).Rgba, last);
    }

    [Fact]
    public async Task TakingATextureOutOfTheSetDrawsItsOriginalAgain()
    {
        var files = Files();
        var card = Card(files, TypicalInventory());
        card.UseDx11Preview = false;
        var latest = new Dictionary<ulong, byte[]>();
        card.PushChromaGl = items => { foreach (var i in items) latest[i.Hash] = i.Rgba.ToArray(); };
        await Scan(card);
        card.ChromaHue = 120;
        await card.ChromaIdleAsync();
        ulong glow = BinTexturePath.HashOfReference(OwnGlow);
        var original = TextureDecoder.Decode(files[glow]).Rgba;
        Assert.NotEqual(original, latest[glow]);

        card.ChromaTextures.Single(r => r.Hash == glow).IsIncluded = false;
        await card.ChromaIdleAsync();
        Assert.Equal(original, latest[glow]);
    }

    // ---- save, flush, revert: the state machine against stand-in hooks

    private sealed class FakeHost
    {
        public readonly List<(string Bin, ColorTransform Transform, IReadOnlyList<ChromaTarget> Targets, IReadOnlyList<ChromaTarget> Stale)> Saves = new();
        public readonly List<IReadOnlyList<ChromaTarget>> Reverts = new();
        public ChromaSavedRecipe? Saved;
        public string? Blocker;

        public void Wire(MeshPreviewViewModel card)
        {
            card.ChromaSaveBlocker = () => Blocker;
            card.ReadChromaSaved = _ => Saved;
            card.SaveChromaRecolour = (bin, t, targets, stale) =>
            {
                Saves.Add((bin, t, targets, stale));
                var hashes = targets.Select(x => x.Hash).ToList();
                return Task.FromResult(new ChromaSaveResult(targets.Count, 0, 0, stale.Count, hashes, hashes, Array.Empty<string>()));
            };
            card.RevertChromaRecolour = (_, targets) => { Reverts.Add(targets); return targets.Count; };
        }
    }

    [Fact]
    public async Task ApplyWritesOnceAndTheStateIsThenSaved_AndDirtyAgainWhenSomethingMoves()
    {
        var host = new FakeHost();
        var card = Card(Files(), TypicalInventory());
        host.Wire(card);
        card.UseDx11Preview = false;
        await Scan(card);

        Assert.False(card.ChromaRecolourDirty);
        Assert.False(card.HasPendingChromaRecolour);
        card.ChromaHue = 60;
        Assert.True(card.ChromaRecolourDirty);
        Assert.True(card.HasPendingChromaRecolour);
        Assert.True(card.ApplyChromaRecolourCommand.CanExecute(null));

        await card.SaveChromaRecolourNowAsync();

        var save = Assert.Single(host.Saves);
        Assert.Equal(Bin1, save.Bin);
        Assert.Equal(60f, save.Transform.HueShiftDegrees);
        Assert.Equal(new[] { OwnDiffuse, OwnGlow }.Select(BinTexturePath.HashOfReference).OrderBy(h => h), save.Targets.Select(t => t.Hash).OrderBy(h => h));
        Assert.Empty(save.Stale);
        Assert.False(card.ChromaRecolourDirty);
        Assert.True(card.HasSavedChromaRecolour);
        Assert.False(card.ApplyChromaRecolourCommand.CanExecute(null));
        Assert.StartsWith("Saved: 2 texture(s) written", card.ChromaRecolourStatus);

        // nothing pending: a flush does nothing
        await card.SaveChromaRecolourNowAsync();
        Assert.Single(host.Saves);

        // a switch changes what a save would write
        card.ChromaTextures.Single(r => r.Name == "zz_glow.tex").IsIncluded = false;
        Assert.True(card.ChromaRecolourDirty);
        await card.SaveChromaRecolourNowAsync();
        Assert.Equal(2, host.Saves.Count);
        Assert.Equal(BinTexturePath.HashOfReference(OwnGlow), Assert.Single(host.Saves[1].Stale).Hash);   // the glow goes back to Riot's
    }

    [Fact]
    public async Task NoChangeWithASavedRecipeSavesAsARevert_AndABlockedSaveThrowsSoAFlushKeepsTheEditPending()
    {
        var host = new FakeHost();
        var card = Card(Files(), TypicalInventory());
        host.Wire(card);
        card.UseDx11Preview = false;
        await Scan(card);
        card.ChromaHue = 60;
        await card.SaveChromaRecolourNowAsync();

        card.ResetChromaSlidersCommand.Execute(null);
        Assert.True(card.ChromaRecolourDirty);                     // a saved recolour that the sliders no longer say is pending
        await card.SaveChromaRecolourNowAsync();
        var save = host.Saves[1];
        Assert.True(save.Transform.IsIdentity);
        Assert.Empty(save.Targets);
        Assert.Equal(2, save.Stale.Count);
        Assert.False(card.ChromaRecolourDirty);
        Assert.False(card.HasSavedChromaRecolour);

        host.Blocker = "Open or create a project first.";
        card.ChromaHue = 30;
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(card.SaveChromaRecolourNowAsync);
        Assert.Contains("project", thrown.Message);
        Assert.True(card.ChromaRecolourDirty);                      // still pending
    }

    [Fact]
    public async Task RevertPutsTheSavedTexturesBackAndResetsTheSliders()
    {
        var host = new FakeHost();
        var files = Files();
        var card = Card(files, TypicalInventory());
        host.Wire(card);
        card.UseDx11Preview = false;
        var latest = new Dictionary<ulong, byte[]>();
        card.PushChromaGl = items => { foreach (var i in items) latest[i.Hash] = i.Rgba.ToArray(); };
        await Scan(card);
        card.ChromaHue = 100;
        await card.SaveChromaRecolourNowAsync();

        Assert.True(card.RevertChromaRecolourCommand.CanExecute(null));
        await card.RevertChromaRecolourCommand.ExecuteAsync(null);
        await card.ChromaIdleAsync();

        Assert.Equal(2, Assert.Single(host.Reverts).Count);
        Assert.False(card.HasSavedChromaRecolour);
        Assert.True(card.ChromaSettings.ToTransform().IsIdentity);
        Assert.False(card.ChromaRecolourDirty);
        Assert.False(card.RevertChromaRecolourCommand.CanExecute(null));
        // the renderer shows Riot's texels again
        ulong diffuse = BinTexturePath.HashOfReference(OwnDiffuse);
        Assert.Equal(TextureDecoder.Decode(files[diffuse]).Rgba, latest[diffuse]);
    }

    [Fact]
    public async Task ASkinWithASavedRecipeComesBackWithItsSlidersAndItsTextures()
    {
        var host = new FakeHost();
        var saved = new ColorTransform { HueShiftDegrees = 75f, Saturation = 1.3f, GreyProtection = new GreyProtection(0.08f, 0.06f) };
        host.Saved = new ChromaSavedRecipe(saved, new[] { new ChromaTarget(BinTexturePath.HashOfReference(OwnDiffuse), OwnDiffuse) });
        var card = new MeshPreviewViewModel
        {
            ReadChromaOriginal = t => Files().GetValueOrDefault(t.Hash),
            ScanSkinColours = (_, _) => Task.FromResult(TypicalInventory()),
            ChromaUiPost = a => { a(); return Task.CompletedTask; },
        };
        host.Wire(card);
        card.SetChromaSkin(Bin1);

        // before any scan the sliders are the saved ones and nothing looks pending
        Assert.Equal(saved, card.ChromaSettings.ToTransform());
        Assert.True(card.HasSavedChromaRecolour);
        Assert.False(card.ChromaRecolourDirty);

        await Scan(card);
        Assert.False(card.ChromaRecolourDirty);
        Assert.Equal(new[] { OwnDiffuse }, card.ChromaTextures.Where(r => r.IsIncluded).Select(r => r.Target.Path));   // the recipe's textures, not the defaults
    }

    [Fact]
    public async Task AnotherSkinForgetsTheRecolour_AndTheDroppedPendingOneIsLogged()
    {
        var card = Card(Files(), TypicalInventory());
        var log = new List<string>();
        card.LogDx11 = (c, m) => log.Add(c + ": " + m);
        card.UseDx11Preview = false;
        await Scan(card);
        card.ChromaHue = 45;
        Assert.True(card.HasPendingChromaRecolour);

        card.SetChromaSkin("data/characters/zz/skins/skin2.bin");

        Assert.False(card.ChromaRecolourDirty);
        Assert.True(card.ChromaSettings.ToTransform().IsIdentity);
        Assert.Empty(card.ChromaTextures);
        Assert.Contains(log, l => l.Contains("unsaved body recolour") && l.Contains("skin1.bin"));
    }

    // ===================================================================== the host end to end (a champion WAD packed here)

    private (string Install, string Wad, byte[] DiffuseTex, byte[] GlowTex, byte[] UnnamedTex) MakeChampion(bool glowOnlyInProject = false)
    {
        string install = Path.Combine(_dir, "game");
        string final = Path.Combine(install, "DATA", "FINAL");
        Directory.CreateDirectory(Path.Combine(final, "Champions"));
        File.WriteAllBytes(Path.Combine(final, "DATA.wad.client"), Array.Empty<byte>());

        var diffuse = Bc1(64, 64);
        var glow = Bc3(32, 32);
        var unnamed = Bc1(32, 32);
        string staging = Path.Combine(_dir, "staging");
        void Put(string rel, byte[] bytes)
        {
            string p = Path.Combine(staging, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, bytes);
        }
        Put("assets/characters/zz/skins/skin1/zz_tx_cm.tex", diffuse);
        if (!glowOnlyInProject) Put("assets/characters/zz/skins/skin1/zz_glow.tex", glow);
        Put($"{UnnamedHash:x16}.tex", unnamed);   // a chunk the dictionary cannot name: a loose <hash>.tex at the root

        Put("data/characters/zz/skins/skin1.bin", SkinBin(1, "assets/characters/zz/skins/skin1/zz_tx_cm.tex", OwnGlow));
        Put("data/characters/zz/skins/skin2.bin", SkinBin(2, "assets/characters/zz/skins/skin1/zz_tx_cm.tex", null, unnamed: true));
        string wad = Path.Combine(final, "Champions", "zz.wad.client");
        var report = WadPackService.Pack(staging, wad);
        Assert.True(report.Success, string.Join("; ", report.Warnings));
        return (install, wad, diffuse, glow, unnamed);
    }

    private const ulong UnnamedHash = 0x1234ABCD5678EF90UL;

    private static byte[] SkinBin(int number, string texture, string? glow, bool unnamed = false)
    {
        var mesh = new List<BinTreeProperty>
        {
            new BinTreeString(H("simpleSkin"), "assets/characters/zz/skins/base/zz.skn"),
            new BinTreeString(H("skeleton"), "assets/characters/zz/skins/base/zz.skl"),
        };
        mesh.Add(unnamed ? new BinTreeWadChunkLink(H("texture"), UnnamedHash) : new BinTreeString(H("texture"), texture));
        if (glow is not null) mesh.Add(new BinTreeString(H("emissiveTexture"), glow));
        var skin = new BinTreeObject(H($"Characters/zz/Skins/Skin{number}"), H("SkinCharacterDataProperties"), new BinTreeProperty[]
        {
            new BinTreeString(H("championSkinName"), "zz" + number),
            new BinTreeEmbedded(H("skinMeshProperties"), H("SkinMeshDataProperties"), mesh),
        });
        using var ms = new MemoryStream();
        new BinTree(new[] { skin }, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    private static object? Call(MainWindowViewModel vm, string name, params object?[] args) =>
        typeof(MainWindowViewModel).GetMethod(name, Private)!.Invoke(vm, args);

    private static Task Idle(MainWindowViewModel vm) => vm.MeshPreview.ChromaIdleAsync();

    /// <summary>An editor on a folder project in the temp folder, the champion's WAD open the way the Character window opens it.</summary>
    private (MainWindowViewModel Vm, ReyProject Project)? Editor(string install, string wad, string skinBin, string projectName = "proj")
    {
        string root = Path.Combine(_dir, projectName);
        Directory.CreateDirectory(root);
        var project = ReyProjectService.OpenFolder(root);
        project.GameDirectory = install;
        ReyProjectService.Save(project, project.ProjectFilePath!);
        return Attach(project, wad, skinBin);
    }

    private (MainWindowViewModel Vm, ReyProject Project)? Attach(ReyProject project, string wad, string skinBin)
    {
        var vm = new MainWindowViewModel { Project = project };
        vm.Settings.AutoSaveEdits = false;   // the machine's setting must not start a DispatcherTimer in a test
        if (Call(vm, "ResolveBinName", H("texture")) is null) return null;   // no hash dictionary here: the synthetic bins cannot be read as skins
        Call(vm, "BuildMounts");
        Assert.True((bool)Call(vm, "MakeCharacterWadReadable", wad)!);
        vm.MeshPreview.SetChromaSkin(skinBin);
        vm.MeshPreview.ChromaUiPost = a => { a(); return Task.CompletedTask; };
        vm.MeshPreview.UseDx11Preview = false;
        return (vm, project);
    }

    private static async Task ScanAsync(MainWindowViewModel vm)
    {
        await vm.MeshPreview.ScanColoursCommand.ExecuteAsync(null);
        await Idle(vm);
    }

    /// <summary>The host tests below return quietly where the hash dictionary is absent (the synthetic bins are skins only through
    /// it). On a machine that HAS the dictionary a silent return would look exactly like a pass, so this says which it is.</summary>
    [Fact]
    public void WhereTheHashDictionaryIsPresentTheHostTestsReallyRun()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null || !Directory.EnumerateFiles(Path.Combine(dir.FullName, "data", "hashes"), "*.txt").Any()) return;
        var champion = MakeChampion();
        Assert.NotNull(Editor(champion.Install, champion.Wad, Bin1));
    }

    [Fact]
    public async Task ASavedRecolourLandsInTheChampionsWadFolder_ReloadsAndShipsInTheFantome()
    {
        var champion = MakeChampion();
        var ed = Editor(champion.Install, champion.Wad, Bin1); if (ed is null) return; var (vm, project) = ed.Value;
        var card = vm.MeshPreview;
        await ScanAsync(vm);
        Assert.True(card.HasChromaTextures, string.Join(" | ", card.ChromaWarnings, card.ChromaSummary));
        var diffuseRow = card.ChromaTextures.Single(r => r.Name == "zz_tx_cm.tex");
        Assert.True(diffuseRow.IsIncluded && diffuseRow.CanInclude);

        card.ChromaHue = 120;
        await card.SaveChromaRecolourNowAsync();

        // the file: under the champion's WAD folder at the asset's own path, the original's format, the transform's texels
        string file = Path.Combine(project.RootPath!, "zz", "assets", "characters", "zz", "skins", "skin1", "zz_tx_cm.tex");
        Assert.True(File.Exists(file), "not under <project>/zz/: " + string.Join(", ", Directory.EnumerateFiles(project.RootPath!, "*.tex", SearchOption.AllDirectories)));
        var written = File.ReadAllBytes(file);
        Assert.Equal(TexFormat.Bc1, TexWriter.DetectFormat(written));
        var expected = card.ChromaSettings.ToTransform().Apply(TextureDecoder.Decode(champion.DiffuseTex));
        Assert.True(MeanAbsError(expected.Rgba, TextureDecoder.Decode(written).Rgba) < 8.0);
        Assert.Contains("zz", project.ProjectFolders);
        Assert.DoesNotContain("Overrides", project.ProjectFolders);

        // the record: the transform, the skin, M171's fields neutral
        var record = project.TextureRecolors.Single(r => r.PathHash == HashAlgorithms.WadPath("assets/characters/zz/skins/skin1/zz_tx_cm.tex"));
        Assert.Equal(card.ChromaSettings.ToTransform(), record.Transform);
        Assert.Equal(Bin1, record.ChromaSkin);
        Assert.Equal(1f, record.Saturation);

        // a second save with different sliders stays in the one folder, and is the transform of the ORIGINAL (never of the first save)
        card.ChromaHue = 200;
        await card.SaveChromaRecolourNowAsync();
        Assert.DoesNotContain("Overrides", project.ProjectFolders);
        Assert.Single(Directory.EnumerateFiles(project.RootPath!, "zz_tx_cm.tex", SearchOption.AllDirectories));
        var second = TextureDecoder.Decode(File.ReadAllBytes(file));
        var expected2 = card.ChromaSettings.ToTransform().Apply(TextureDecoder.Decode(champion.DiffuseTex));
        Assert.True(MeanAbsError(expected2.Rgba, second.Rgba) < 8.0, "the second save compounded on the first");

        // a reload: a new editor on the saved project file
        var reloaded = ReyProjectService.OpenFolder(project.RootPath!);
        var (vm2, _) = Attach(reloaded, champion.Wad, Bin1)!.Value;
        Assert.True(vm2.MeshPreview.HasSavedChromaRecolour);
        Assert.Equal(card.ChromaSettings.ToTransform(), vm2.MeshPreview.ChromaSettings.ToTransform());
        await ScanAsync(vm2);
        Assert.False(vm2.MeshPreview.ChromaRecolourDirty);
        Assert.Equal(File.ReadAllBytes(file), (byte[])Call(vm2, "ReadAsset", HashAlgorithms.WadPath("assets/characters/zz/skins/skin1/zz_tx_cm.tex"))!);   // the project serves the recoloured file
        Assert.Equal(champion.DiffuseTex, vm2.MeshPreview.ReadChromaOriginal!(diffuseRow.Target));                                                          // and Riot's original is still the base

        // the .fantome carries it byte for byte in the champion's WAD
        string fantome = Path.Combine(_dir, "zz.fantome");
        reloaded.OutputDirectory = Path.Combine(project.RootPath!, "Build");
        Directory.CreateDirectory(reloaded.OutputDirectory);
        typeof(MainWindowViewModel).GetMethod("ExportFantomeCore", Private)!.Invoke(vm2, new object?[]
        {
            fantome, new FantomeMeta { Name = "t", Author = "t", Version = "1.0.0", Description = "" }, null, reloaded.OutputDirectory,
            new NoProgress(), "Export", "nothing", "zip",
        });
        using (var zip = ZipFile.OpenRead(fantome))
        {
            var entry = zip.GetEntry("WAD/zz.wad.client") ?? throw new InvalidOperationException(string.Join(", ", zip.Entries.Select(e => e.FullName)));
            string wadPath = Path.Combine(_dir, "exported.wad.client");
            using (var s = entry.Open()) using (var f = File.Create(wadPath)) s.CopyTo(f);
            using var exported = WadArchive.Open(wadPath);
            Assert.True(exported.TryGetEntry(HashAlgorithms.WadPath("assets/characters/zz/skins/skin1/zz_tx_cm.tex"), out var chunk));
            Assert.Equal(File.ReadAllBytes(file), exported.Extract(chunk));
        }

        // revert: the file, the record and the saved state go; Riot's texture is what the project serves again
        await vm2.MeshPreview.RevertChromaRecolourCommand.ExecuteAsync(null);
        Assert.False(File.Exists(file));
        Assert.Empty(reloaded.TextureRecolors);
        Assert.Equal(champion.DiffuseTex, (byte[])Call(vm2, "ReadAsset", HashAlgorithms.WadPath("assets/characters/zz/skins/skin1/zz_tx_cm.tex"))!);
    }

    [Fact]
    public async Task AChunkTwoRiotWadsHoldIsWrittenToBothWadsFoldersAndRevertedFromBoth()
    {
        // Aatrox's base diffuse is in Aatrox.wad.client AND Shaders.wad.client, byte for byte, and the game reads whichever it mounts first:
        // a recolour in one of the two folders would be the right file in the wrong WAD for some players
        var champion = MakeChampion();
        string other = Path.Combine(Path.GetDirectoryName(champion.Wad)!, "Shared.wad.client");
        string staging = Path.Combine(_dir, "staging-shared");
        string tex = Path.Combine(staging, "assets", "characters", "zz", "skins", "skin1", "zz_tx_cm.tex");
        Directory.CreateDirectory(Path.GetDirectoryName(tex)!);
        File.WriteAllBytes(tex, champion.DiffuseTex);
        Assert.True(WadPackService.Pack(staging, other).Success);

        var ed = Editor(champion.Install, champion.Wad, Bin1); if (ed is null) return; var (vm, project) = ed.Value;
        Assert.True((bool)Call(vm, "MakeCharacterWadReadable", other)!);   // Shared.wad.client is mounted too; the champion's was opened first
        vm.MeshPreview.SetChromaSkin(Bin1);
        vm.MeshPreview.ChromaUiPost = a => { a(); return Task.CompletedTask; };
        vm.MeshPreview.UseDx11Preview = false;
        await ScanAsync(vm);
        vm.MeshPreview.ChromaHue = 100;
        await vm.MeshPreview.SaveChromaRecolourNowAsync();

        string rel = Path.Combine("assets", "characters", "zz", "skins", "skin1", "zz_tx_cm.tex");
        string a = Path.Combine(project.RootPath!, "zz", rel), b = Path.Combine(project.RootPath!, "Shared", rel);
        Assert.True(File.Exists(a) && File.Exists(b), "both WAD folders carry the recolour");
        Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
        Assert.NotEqual(champion.DiffuseTex, File.ReadAllBytes(a));
        var record = project.TextureRecolors.Single(r => r.PathHash == HashAlgorithms.WadPath("assets/characters/zz/skins/skin1/zz_tx_cm.tex"));
        Assert.Equal(new[] { "zz", "Shared" }.OrderBy(x => x), record.WadFolders!.OrderBy(x => x));

        // the .fantome has the chunk in BOTH wads, byte for byte
        string fantome = Path.Combine(_dir, "both.fantome");
        project.OutputDirectory = Path.Combine(project.RootPath!, "Build");
        Directory.CreateDirectory(project.OutputDirectory);
        typeof(MainWindowViewModel).GetMethod("ExportFantomeCore", Private)!.Invoke(vm, new object?[]
        {
            fantome, new FantomeMeta { Name = "t", Author = "t", Version = "1.0.0", Description = "" }, null, project.OutputDirectory,
            new NoProgress(), "Export", "nothing", "zip",
        });
        using (var zip = ZipFile.OpenRead(fantome))
            foreach (string wadName in new[] { "WAD/zz.wad.client", "WAD/Shared.wad.client" })
            {
                string path = Path.Combine(_dir, "out-" + Path.GetFileName(wadName));
                using (var s = zip.GetEntry(wadName)!.Open()) using (var f = File.Create(path)) s.CopyTo(f);
                using var exported = WadArchive.Open(path);
                Assert.True(exported.TryGetEntry(HashAlgorithms.WadPath("assets/characters/zz/skins/skin1/zz_tx_cm.tex"), out var chunk), wadName);
                Assert.Equal(File.ReadAllBytes(a), exported.Extract(chunk));
            }

        await vm.MeshPreview.RevertChromaRecolourCommand.ExecuteAsync(null);
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
        Assert.Empty(project.TextureRecolors);
    }

    [Fact]
    public async Task CtrlSFlushesAPendingRecolourThroughTheEditorsOwnSave()
    {
        var champion = MakeChampion();
        var ed = Editor(champion.Install, champion.Wad, Bin1); if (ed is null) return; var (vm, project) = ed.Value;
        await ScanAsync(vm);
        vm.MeshPreview.ChromaHue = 90;
        Assert.True(vm.MeshPreview.HasPendingChromaRecolour);

        await (Task)Call(vm, "SavePendingEditorEdits")!;   // what Ctrl+S, the auto-save and Export / Build Package all call first

        Assert.False(vm.MeshPreview.HasPendingChromaRecolour);
        Assert.True(File.Exists(Path.Combine(project.RootPath!, "zz", "assets", "characters", "zz", "skins", "skin1", "zz_tx_cm.tex")));
        Assert.NotEmpty(project.TextureRecolors);
        // and the project file on disk already knows (Ctrl+S then writes it again with the rest)
        Assert.Contains("\"HueShiftDegrees\": 90", File.ReadAllText(project.ProjectFilePath!));
    }

    [Fact]
    public async Task AnUnnamedChunkIsWrittenAsALooseHashFile_AndShipsUnderItsHash()
    {
        var champion = MakeChampion();
        var ed = Editor(champion.Install, champion.Wad, "data/characters/zz/skins/skin2.bin"); if (ed is null) return; var (vm, project) = ed.Value;
        await ScanAsync(vm);
        var row = card(vm).ChromaTextures.SingleOrDefault(r => r.Hash == UnnamedHash);
        Assert.NotNull(row);
        Assert.StartsWith("0x", row!.Target.Path);
        Assert.True(row.IsIncluded && row.CanInclude, row.Note);

        vm.MeshPreview.ChromaHue = 150;
        await vm.MeshPreview.SaveChromaRecolourNowAsync();

        // <project>/zz/<hash16>.tex: the form the mounts and the packer both read as that chunk
        string loose = Path.Combine(project.RootPath!, "zz", $"{UnnamedHash:x16}.tex");
        Assert.True(File.Exists(loose), string.Join(", ", Directory.EnumerateFiles(project.RootPath!, "*.tex", SearchOption.AllDirectories)));
        Assert.Equal(TexFormat.Bc1, TexWriter.DetectFormat(File.ReadAllBytes(loose)));
        Assert.Equal(File.ReadAllBytes(loose), (byte[])Call(vm, "ReadAsset", UnnamedHash)!);

        string wadOut = Path.Combine(_dir, "pack.wad.client");
        var pack = WadPackService.Pack(Path.Combine(project.RootPath!, "zz"), wadOut);
        Assert.True(pack.Success);
        using var packed = WadArchive.Open(wadOut);
        Assert.True(packed.TryGetEntry(UnnamedHash, out var chunk));
        Assert.Equal(File.ReadAllBytes(loose), packed.Extract(chunk));

        // revert finds the loose file again
        await vm.MeshPreview.RevertChromaRecolourCommand.ExecuteAsync(null);
        Assert.False(File.Exists(loose));
        Assert.Empty(project.TextureRecolors);

        static MeshPreviewViewModel card(MainWindowViewModel v) => v.MeshPreview;
    }

    [Fact]
    public async Task OneSkinsRevertLeavesAnotherSkinsRecordAlone()
    {
        var champion = MakeChampion();
        var ed = Editor(champion.Install, champion.Wad, Bin1); if (ed is null) return; var (vm, project) = ed.Value;
        await ScanAsync(vm);
        vm.MeshPreview.ChromaHue = 90;
        await vm.MeshPreview.SaveChromaRecolourNowAsync();
        ulong hash = HashAlgorithms.WadPath("assets/characters/zz/skins/skin1/zz_tx_cm.tex");

        // skin 2's recipe takes the shared texture over (it names it too), then skin 1's revert must not undo it
        var record = project.TextureRecolors.Single(r => r.PathHash == hash);
        record.ChromaSkin = "data/characters/zz/skins/skin2.bin";
        string file = Path.Combine(project.RootPath!, "zz", "assets", "characters", "zz", "skins", "skin1", "zz_tx_cm.tex");

        await vm.MeshPreview.RevertChromaRecolourCommand.ExecuteAsync(null);

        Assert.True(File.Exists(file));
        Assert.Contains(project.TextureRecolors, r => r.PathHash == hash);
    }

    [Fact]
    public void TheRecolorTexturesToolTakingOverARecordClearsTheChromaSide()
    {
        var vm = new MainWindowViewModel();
        vm.Settings.AutoSaveEdits = false;
        var record = new TextureRecolorRecord { PathHash = 99, AssetPath = "assets/a.tex", Transform = new ColorTransform { HueShiftDegrees = 10f }, ChromaSkin = Bin1, WadFolders = new List<string> { "a", "b" } };
        vm.Project.TextureRecolors.Add(record);

        vm.PersistRecolors(new TextureAdjustment { HueDegrees = 30f }, new[] { new RecolorTarget(99, "assets/a.tex") });

        Assert.Null(record.Transform);
        Assert.Null(record.ChromaSkin);
        Assert.Null(record.WadFolders);
        Assert.Equal(30f, record.HueDegrees);
    }

    // ===================================================================== review round: containment, carried textures, edits outside, flush

    private static (bool Ok, string File) TryRecolorFile(string root, string folder, ulong hash, string path)
    {
        var method = typeof(MainWindowViewModel).GetMethod("TryRecolorFile", BindingFlags.NonPublic | BindingFlags.Static)!;
        var args = new object?[] { root, folder, hash, path, ".tex", null };
        bool ok = (bool)method.Invoke(null, args)!;
        return (ok, (string?)args[5] ?? "");
    }

    [Fact]
    public void AWadFolderThatIsNotOnePlainFolderNameNeverBecomesAFilePath()
    {
        string root = Path.Combine(_dir, "contain");
        Directory.CreateDirectory(root);
        string rooted = Path.Combine(_dir, "elsewhere");
        foreach (string hostile in new[] { "..", "..\\outside", "../outside", "..\\..", rooted, "C:\\x", "a/b", "a\\b", "", " ", "zz.", "CON", "x:y" })
            Assert.False(TryRecolorFile(root, hostile, 1, "assets/a.tex").Ok, $"'{hostile}' was accepted as a WAD folder");

        var plain = TryRecolorFile(root, "Lillia", 1, "assets/a.tex");
        Assert.True(plain.Ok);
        Assert.StartsWith(Path.GetFullPath(root), plain.File);
        // an asset path that climbs out is refused too, and the loose form of an unnamed chunk lands at the folder's root
        Assert.False(TryRecolorFile(root, "Lillia", 1, "../../x.tex").Ok);
        Assert.Equal(Path.Combine(root, "Lillia", $"{0xABCUL:x16}.tex"), TryRecolorFile(root, "Lillia", 0xABC, "0x0000000000000abc").File);
    }

    [Fact]
    public async Task ARecordWhoseWadFoldersEscapeTheProjectDeletesNothingOutsideIt()
    {
        var champion = MakeChampion();
        var ed = Editor(champion.Install, champion.Wad, Bin1); if (ed is null) return; var (vm, project) = ed.Value;
        await ScanAsync(vm);
        vm.MeshPreview.ChromaHue = 90;
        await vm.MeshPreview.SaveChromaRecolourNowAsync();

        const string rel = "assets/characters/zz/skins/skin1/zz_tx_cm.tex";
        string[] decoys =
        {
            Path.Combine(_dir, "outside", rel.Replace('/', Path.DirectorySeparatorChar)),      // "..\outside" from the project root
            Path.Combine(_dir, "absolute", rel.Replace('/', Path.DirectorySeparatorChar)),     // a rooted folder name
        };
        foreach (string decoy in decoys) { Directory.CreateDirectory(Path.GetDirectoryName(decoy)!); File.WriteAllBytes(decoy, new byte[] { 1, 2, 3 }); }
        var record = project.TextureRecolors.Single(r => r.PathHash == HashAlgorithms.WadPath(rel));
        record.WadFolders = new List<string> { "..\\outside", Path.Combine(_dir, "absolute"), "zz" };
        string real = Path.Combine(project.RootPath!, "zz", rel.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(real));

        await vm.MeshPreview.RevertChromaRecolourCommand.ExecuteAsync(null);

        Assert.All(decoys, d => Assert.True(File.Exists(d), "a file outside the project was deleted: " + d));
        Assert.False(File.Exists(real));        // the plain folder name was honoured
        Assert.Empty(project.TextureRecolors);
    }

    // ---- saved textures the scan does not list or cannot read are carried, never reverted by a flush

    private static ChromaSavedRecipe SavedBoth() => new(new ColorTransform { HueShiftDegrees = 75f, GreyProtection = new GreyProtection(0.08f, 0.06f) },
        new[] { new ChromaTarget(BinTexturePath.HashOfReference(OwnDiffuse), OwnDiffuse), new ChromaTarget(BinTexturePath.HashOfReference(OwnGlow), OwnGlow) });

    private static MeshPreviewViewModel SavedCard(FakeHost host, Func<ChromaTarget, byte[]?> read, SkinColorInventory inventory)
    {
        host.Saved = SavedBoth();
        var card = new MeshPreviewViewModel
        {
            ReadChromaOriginal = read,
            ScanSkinColours = (_, _) => Task.FromResult(inventory),
            ChromaUiPost = a => { a(); return Task.CompletedTask; },
        };
        host.Wire(card);
        card.UseDx11Preview = false;
        card.SetChromaSkin(Bin1);
        return card;
    }

    [Fact]
    public async Task ASavedTextureTheScanDoesNotListIsKeptWhenAnythingIsFlushed()
    {
        var host = new FakeHost();
        var files = Files();
        var card = SavedCard(host, t => files.GetValueOrDefault(t.Hash), Inventory(Body(OwnDiffuse, BodyTextureRole.Diffuse)));   // the glow is not in the scan
        await Scan(card);

        Assert.DoesNotContain(card.ChromaTextures, r => r.Name == "zz_glow.tex");
        Assert.False(card.ChromaRecolourDirty, "an unlisted saved texture made the card look pending");
        await card.SaveChromaRecolourNowAsync();       // nothing pending: a flush does nothing
        Assert.Empty(host.Saves);

        card.ChromaHue = 120;                           // a real edit
        await card.SaveChromaRecolourNowAsync();
        var save = Assert.Single(host.Saves);
        Assert.Equal(new[] { BinTexturePath.HashOfReference(OwnDiffuse) }, save.Targets.Select(t => t.Hash));
        Assert.Empty(save.Stale);                       // the glow is not put back to Riot's
        Assert.True(card.HasSavedChromaRecolour);
        card.ChromaHue = 130;
        await card.SaveChromaRecolourNowAsync();        // and it is still carried the time after
        Assert.Empty(host.Saves[1].Stale);

        // only an explicit "no recolour" takes it off
        card.ResetChromaSlidersCommand.Execute(null);
        await card.SaveChromaRecolourNowAsync();
        Assert.Equal(2, host.Saves[2].Stale.Count);
    }

    [Fact]
    public async Task ASavedTextureWhoseOriginalCannotBeReadIsKeptAndNeverMakesTheCardPending()
    {
        var host = new FakeHost();
        var files = Files();
        ulong glow = BinTexturePath.HashOfReference(OwnGlow);
        var card = SavedCard(host, t => t.Hash == glow ? null : files.GetValueOrDefault(t.Hash), TypicalInventory());
        await Scan(card);

        var row = card.ChromaTextures.Single(r => r.Hash == glow);
        Assert.False(row.CanInclude);
        Assert.False(row.IsIncluded);
        Assert.False(card.ChromaRecolourDirty, "a classification failure made the card pending");
        Assert.False(card.HasPendingChromaRecolour);

        card.ChromaHue = 10;
        await card.SaveChromaRecolourNowAsync();
        var save = Assert.Single(host.Saves);
        Assert.DoesNotContain(save.Targets, t => t.Hash == glow);
        Assert.Empty(save.Stale);

        // a texture that CAN be included and is switched off on purpose does go back
        card.ChromaTextures.Single(r => r.Name == "zz_tx_cm.tex").IsIncluded = false;
        await card.SaveChromaRecolourNowAsync();
        Assert.Equal(BinTexturePath.HashOfReference(OwnDiffuse), Assert.Single(host.Saves[1].Stale).Hash);
    }

    // ---- a save that fails is not retried by every auto-save tick

    [Fact]
    public async Task AutoSaveWaitsForAChangeAfterASaveFailed_AManualSaveAlwaysTries()
    {
        var host = new FakeHost();
        var card = Card(Files(), TypicalInventory());
        host.Wire(card);
        int tries = 0;
        card.SaveChromaRecolour = (_, _, _, _) => { tries++; throw new IOException("disk full"); };
        card.UseDx11Preview = false;
        await Scan(card);
        card.ChromaHue = 40;
        Assert.True(card.ChromaAutoSaveDue);

        await Assert.ThrowsAsync<IOException>(card.SaveChromaRecolourNowAsync);
        Assert.True(card.HasPendingChromaRecolour);        // still pending: Ctrl+S tries again
        Assert.False(card.ChromaAutoSaveDue);              // the auto-save does not, until something changes
        await Assert.ThrowsAsync<IOException>(card.SaveChromaRecolourNowAsync);
        Assert.Equal(2, tries);

        card.ChromaHue = 41;
        Assert.True(card.ChromaAutoSaveDue);
    }

    [Fact]
    public async Task APartlyFailedSaveStaysPendingAndAutoSaveBacksOffToo()
    {
        var host = new FakeHost();
        var card = Card(Files(), TypicalInventory());
        host.Wire(card);
        card.SaveChromaRecolour = (_, _, targets, _) => Task.FromResult(new ChromaSaveResult(1, 0, 1, 0,
            new[] { targets[0].Hash }, new[] { targets[0].Hash }, new[] { "zz_glow.tex: write failed" }));
        card.UseDx11Preview = false;
        await Scan(card);
        card.ChromaHue = 40;
        await card.SaveChromaRecolourNowAsync();

        Assert.True(card.ChromaRecolourDirty);             // the texture that failed is not part of the saved state
        Assert.False(card.ChromaAutoSaveDue);
    }

    // ---- the host: edits outside the recolour, textures only the project holds, a pending recolour on a model load

    [Fact]
    public async Task AProjectTextureSomebodyEditedIsNotDrawnOverNotRecolouredAndNotReplacedUnlessAsked()
    {
        var champion = MakeChampion();
        var ed = Editor(champion.Install, champion.Wad, Bin1); if (ed is null) return; var (vm, project) = ed.Value;
        // the user's own version of the diffuse, in the project, with no record
        var edited = TexWriter.Write(Bands(64, 64, alphaRamp: true), TexFormat.Bc3, mipmaps: true);
        string file = Path.Combine(project.RootPath!, "zz", "assets", "characters", "zz", "skins", "skin1", "zz_tx_cm.tex");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, edited);
        project.ProjectFolders.Add("zz");
        Call(vm, "BuildMounts");

        var card = vm.MeshPreview;
        var pushes = new List<ChromaPushItem>();
        card.PushChromaGl = items => pushes.AddRange(items);
        await ScanAsync(vm);

        var diffuse = card.ChromaTextures.Single(r => r.Name == "zz_tx_cm.tex");
        Assert.True(diffuse.IsEditedOutside);
        Assert.False(diffuse.IsIncluded);
        Assert.Contains("edited outside the recolour", diffuse.Note);
        Assert.False(card.ChromaRecolourDirty);
        Assert.Empty(pushes);                                  // nothing was drawn: the scan did not paint Riot's original over the edit
        Assert.True(card.ChromaTextures.Single(r => r.Name == "zz_glow.tex").IsIncluded);

        card.ChromaHue = 100;
        await card.SaveChromaRecolourNowAsync();
        Assert.Equal(edited, File.ReadAllBytes(file));         // the edit is untouched
        Assert.DoesNotContain(pushes, p => p.Hash == diffuse.Hash);
        Assert.Single(project.TextureRecolors);                // only the glow

        // asked for, it warns and then replaces the edit with the recolour of Riot's original
        diffuse.IsIncluded = true;
        await card.ChromaIdleAsync();
        Assert.Contains("edited outside the recolour", card.ChromaShareWarning);
        await card.SaveChromaRecolourNowAsync();
        Assert.NotEqual(edited, File.ReadAllBytes(file));
        Assert.Equal(TexFormat.Bc1, TexWriter.DetectFormat(File.ReadAllBytes(file)));
        Assert.Equal(2, project.TextureRecolors.Count);
    }

    [Fact]
    public async Task ACopyOfRiotsTextureInTheProjectIsNotAnEdit()
    {
        var champion = MakeChampion();
        var ed = Editor(champion.Install, champion.Wad, Bin1); if (ed is null) return; var (vm, project) = ed.Value;
        string file = Path.Combine(project.RootPath!, "zz", "assets", "characters", "zz", "skins", "skin1", "zz_tx_cm.tex");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, champion.DiffuseTex);          // Copy To Project: identical to Riot's
        project.ProjectFolders.Add("zz");
        Call(vm, "BuildMounts");
        await ScanAsync(vm);
        var row = vm.MeshPreview.ChromaTextures.Single(r => r.Name == "zz_tx_cm.tex");
        Assert.False(row.IsEditedOutside);
        Assert.True(row.IsIncluded);
    }

    [Fact]
    public async Task ATextureOnlyTheProjectHoldsIsRefusedWithAReasonAndWritesNoShadowedOverride()
    {
        var champion = MakeChampion(glowOnlyInProject: true);
        var ed = Editor(champion.Install, champion.Wad, Bin1); if (ed is null) return; var (vm, project) = ed.Value;
        string only = Path.Combine(project.RootPath!, "Other", "assets", "characters", "zz", "skins", "skin1", "zz_glow.tex");
        Directory.CreateDirectory(Path.GetDirectoryName(only)!);
        File.WriteAllBytes(only, champion.GlowTex);
        project.ProjectFolders.Add("Other");
        Call(vm, "BuildMounts");
        await ScanAsync(vm);

        var card = vm.MeshPreview;
        var glow = card.ChromaTextures.Single(r => r.Name == "zz_glow.tex");
        Assert.True(glow.IsEditedOutside);                      // not Riot's: Riot has no such file
        glow.IsIncluded = true;
        card.ChromaHue = 80;
        await card.ChromaIdleAsync();
        await card.SaveChromaRecolourNowAsync();                // the diffuse is written; the glow is refused

        Assert.Contains("exists only in the project", card.ChromaRecolourStatus);
        Assert.Equal(champion.GlowTex, File.ReadAllBytes(only));
        Assert.DoesNotContain(project.TextureRecolors, r => r.PathHash == glow.Hash);
        Assert.True(card.ChromaRecolourDirty);                  // not saved: still pending
        Assert.False(card.ChromaAutoSaveDue);                   // and not retried every tick
        string overrides = Project_OverridesDir(project);
        Assert.False(Directory.Exists(overrides) && Directory.EnumerateFiles(overrides, "*.tex").Any(), "a shadowed override was written");
    }

    private static string Project_OverridesDir(ReyProject project) => project.OverridesDirectory ?? Path.Combine(project.RootPath!, ".reyengine", "overrides");

    [Fact]
    public async Task APendingRecolourIsSavedBeforeTheWindowLoadsAnotherModel_AndAFailedSaveKeepsTheModelWhereItIs()
    {
        var champion = MakeChampion();
        var ed = Editor(champion.Install, champion.Wad, Bin1); if (ed is null) return; var (vm, project) = ed.Value;
        await ScanAsync(vm);
        var card = vm.MeshPreview;
        var flush = typeof(MainWindowViewModel).GetMethod("FlushPendingChromaAsync", Private)!;

        card.ChromaHue = 60;
        Assert.True(card.HasPendingChromaRecolour);
        Assert.True(await (Task<bool>)flush.Invoke(vm, null)!);
        Assert.False(card.HasPendingChromaRecolour);
        Assert.NotEmpty(project.TextureRecolors);

        // a save that fails stops the load: the recolour is not dropped behind the user's back
        var real = card.SaveChromaRecolour!;
        card.SaveChromaRecolour = (_, _, _, _) => throw new IOException("disk full");
        card.ChromaHue = 61;
        Assert.False(await (Task<bool>)flush.Invoke(vm, null)!);
        Assert.True(card.HasPendingChromaRecolour);
        card.SaveChromaRecolour = real;

        // with no project to write into the recolour was only a preview: the load goes on
        var bare = new MainWindowViewModel();
        bare.Settings.AutoSaveEdits = false;
        int calls = 0;
        bare.MeshPreview.ReadChromaOriginal = t => Files().GetValueOrDefault(t.Hash);
        bare.MeshPreview.ScanSkinColours = (_, _) => Task.FromResult(TypicalInventory());
        bare.MeshPreview.ChromaUiPost = a => { a(); return Task.CompletedTask; };
        bare.MeshPreview.UseDx11Preview = false;
        var baseSave = bare.MeshPreview.SaveChromaRecolour!;
        bare.MeshPreview.SaveChromaRecolour = (a, b, c, d) => { calls++; return baseSave(a, b, c, d); };
        bare.MeshPreview.SetChromaSkin(Bin1);
        await bare.MeshPreview.ScanColoursCommand.ExecuteAsync(null);
        await bare.MeshPreview.ChromaIdleAsync();
        bare.MeshPreview.ChromaHue = 20;
        Assert.True(bare.MeshPreview.HasPendingChromaRecolour);
        Assert.True(await (Task<bool>)flush.Invoke(bare, null)!);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task AMultiFolderWriteThatFailsPartwayPutsBackWhatItAlreadyWrote()
    {
        var champion = MakeChampion();
        string other = Path.Combine(Path.GetDirectoryName(champion.Wad)!, "Shared.wad.client");
        string staging = Path.Combine(_dir, "staging-shared2");
        string tex = Path.Combine(staging, "assets", "characters", "zz", "skins", "skin1", "zz_tx_cm.tex");
        Directory.CreateDirectory(Path.GetDirectoryName(tex)!);
        File.WriteAllBytes(tex, champion.DiffuseTex);
        Assert.True(WadPackService.Pack(staging, other).Success);

        var ed = Editor(champion.Install, champion.Wad, Bin1); if (ed is null) return; var (vm, project) = ed.Value;
        Assert.True((bool)Call(vm, "MakeCharacterWadReadable", other)!);
        vm.MeshPreview.SetChromaSkin(Bin1);
        vm.MeshPreview.ChromaUiPost = a => { a(); return Task.CompletedTask; };
        vm.MeshPreview.UseDx11Preview = false;
        await ScanAsync(vm);

        // the "zz" destination is a DIRECTORY, so writing it throws - after "Shared", the folder tried first, was written
        string rel = Path.Combine("assets", "characters", "zz", "skins", "skin1", "zz_tx_cm.tex");
        Directory.CreateDirectory(Path.Combine(project.RootPath!, "zz", rel));
        vm.MeshPreview.ChromaTextures.Single(r => r.Name == "zz_glow.tex").IsIncluded = false;
        vm.MeshPreview.ChromaHue = 70;
        await Assert.ThrowsAsync<InvalidOperationException>(vm.MeshPreview.SaveChromaRecolourNowAsync);

        Assert.False(File.Exists(Path.Combine(project.RootPath!, "Shared", rel)), "the copy written before the failure was left behind");
        Assert.Empty(project.TextureRecolors);
    }

    [Fact]
    public async Task AnotherSpellingOfTheSameChunkIsRemovedWhenTheRecordMovesToIt()
    {
        var champion = MakeChampion();
        var ed = Editor(champion.Install, champion.Wad, "data/characters/zz/skins/skin2.bin"); if (ed is null) return; var (vm, project) = ed.Value;
        await ScanAsync(vm);
        vm.MeshPreview.ChromaHue = 150;
        await vm.MeshPreview.SaveChromaRecolourNowAsync();
        string loose = Path.Combine(project.RootPath!, "zz", $"{UnnamedHash:x16}.tex");
        Assert.True(File.Exists(loose));

        // the dictionary learns the chunk's name: the record's spelling changes, and the loose copy must not stay beside the named one
        const string named = "assets/characters/zz/skins/skin2/zz_unnamed.tex";
        var pending = (System.Collections.Concurrent.ConcurrentDictionary<ulong, List<string>>)typeof(MainWindowViewModel).GetField("_pendingChromaFolders", Private)!.GetValue(vm)!;
        pending[UnnamedHash] = new List<string> { "zz" };
        Call(vm, "PersistChromaRecords", "data/characters/zz/skins/skin2.bin", vm.MeshPreview.ChromaSettings.ToTransform(),
            (IReadOnlyList<RecolorTarget>)new[] { new RecolorTarget(UnnamedHash, named) });

        Assert.False(File.Exists(loose), "the loose <hash>.tex stayed beside the named spelling");
        Assert.Equal(named, project.TextureRecolors.Single().AssetPath);
    }

    // ---- the old tool on a map project (regression: its write and revert are what they were)

    [Fact]
    public void TheRecolorTexturesToolStillWritesUnderTheRiotWadFolderAndRevertsOnlyWhatItRecorded()
    {
        string riot = Path.Combine(_dir, "riot-map");
        string texPath = "assets/maps/t/tex.tex";
        string src = Path.Combine(riot, texPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        File.WriteAllBytes(src, Bc1());
        string refWad = Path.Combine(_dir, "Map11.wad.client");
        Assert.True(WadPackService.Pack(riot, refWad).Success);
        string root = Path.Combine(_dir, "mapproject");
        var project = new ReyProject { Name = "map", RootPath = root, ReferenceWads = { refWad } };
        Directory.CreateDirectory(root);
        project.ProjectFilePath = Path.Combine(root, ".reyengine", "project.json");
        var vm = new MainWindowViewModel { Project = project };
        vm.Settings.AutoSaveEdits = false;
        Call(vm, "BuildMounts");

        byte[] recoloured = TextureRecolor.Apply(Bc1(), new TextureAdjustment { HueDegrees = 30f }).Bytes!;
        string written = (string)Call(vm, "WriteRecoloredAsset", texPath, recoloured, ".tex")!;
        string expected = Path.Combine(root, "Map11", "assets", "maps", "t", "tex.tex");
        Assert.Equal(expected, written);
        Assert.Contains("Map11", project.ProjectFolders);
        var target = new RecolorTarget(HashAlgorithms.WadPath(texPath), texPath);
        vm.PersistRecolors(new TextureAdjustment { HueDegrees = 30f }, new[] { target });
        Assert.Equal(30f, project.TextureRecolors.Single().HueDegrees);
        Assert.Null(project.TextureRecolors.Single().Transform);

        Assert.Equal(1, vm.RevertRecolors(new[] { target }));
        Assert.False(File.Exists(expected));
        Assert.Empty(project.TextureRecolors);

        // a project file with no record behind it is not the tool's to delete, even where the mounts say the chunk lives
        string stray = Path.Combine(root, "Other", "assets", "maps", "t", "tex.tex");
        Directory.CreateDirectory(Path.GetDirectoryName(stray)!);
        File.WriteAllBytes(stray, new byte[] { 9, 9, 9 });
        project.ProjectFolders.Add("Other");
        Call(vm, "BuildMounts");
        vm.RevertRecolors(new[] { target });
        Assert.True(File.Exists(stray), "RevertRecolors deleted a file no record accounts for");
    }

    private sealed class NoProgress : IProgress<(double Frac, string Stage)>
    {
        public void Report((double Frac, string Stage) value) { }
    }
}
