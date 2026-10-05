using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Vfx;
using LtColor = LeagueToolkit.Core.Primitives.Color;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M826: the effect colour recolour on bins this suite writes - the promises that are about structure rather than about Riot's data: an absent field is skipped, a constant that
/// is not there is not written, every key of a curve is transformed exactly, the mixers and data maps typed as colours are never touched, brightness and saturation act once per
/// particle (the product rule), nothing compounds, and the preview's working copy is what the save writes. Riot's own files are <see cref="ChromaEffectsRealDataTests"/>.
/// </summary>
public sealed class EffectColorsTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    // ---- building a system ------------------------------------------------------------------------------

    private static BinTreeEmbedded Table(params float[] values) => values.Length == 0 ? new(0, H("VfxProbabilityTableData"), Array.Empty<BinTreeProperty>()) : new(0, H("VfxProbabilityTableData"), new BinTreeProperty[]
    {
        new BinTreeContainer(H("keyTimes"), BinPropertyType.F32, values.Select((_, i) => (BinTreeProperty)new BinTreeF32(0, values.Length == 1 ? 0f : i / (float)(values.Length - 1))).ToArray()),
        new BinTreeContainer(H("keyValues"), BinPropertyType.F32, values.Select(v => (BinTreeProperty)new BinTreeF32(0, v)).ToArray()),
    });

    /// <summary>A <c>ValueColor</c>: an optional constant, optional keys (times 0..1), optional probability tables inside the dynamics.</summary>
    internal static BinTreeEmbedded ValueColor(string field, Vector4? constant, Vector4[]? keys = null, BinTreeEmbedded[]? tables = null, bool asColor = false)
    {
        var props = new List<BinTreeProperty>();
        BinTreeProperty One(Vector4 v) => asColor ? new BinTreeColor(0, new LtColor(v.X, v.Y, v.Z, v.W)) : new BinTreeVector4(0, v);
        if (constant is { } c) props.Add(asColor ? new BinTreeColor(H("constantValue"), new LtColor(c.X, c.Y, c.Z, c.W)) : new BinTreeVector4(H("constantValue"), c));
        if (keys is not null || tables is not null)
        {
            var dynamics = new List<BinTreeProperty>();
            if (keys is not null)
            {
                dynamics.Add(new BinTreeContainer(H("times"), BinPropertyType.F32, keys.Select((_, i) => (BinTreeProperty)new BinTreeF32(0, keys.Length == 1 ? 0f : i / (float)(keys.Length - 1))).ToArray()));
                dynamics.Add(new BinTreeContainer(H("values"), asColor ? BinPropertyType.Color : BinPropertyType.Vector4, keys.Select(One).ToArray()));
            }
            if (tables is not null) dynamics.Add(new BinTreeContainer(H("probabilityTables"), BinPropertyType.Embedded, tables.Cast<BinTreeProperty>().ToArray()));
            props.Add(new BinTreeEmbedded(H("dynamics"), H("VfxAnimatedColorVariableData"), dynamics));
        }
        return new BinTreeEmbedded(H(field), H("ValueColor"), props);
    }

    internal static BinTreeEmbedded Emitter(string name, params BinTreeProperty[] fields) =>
        new(0, H("VfxEmitterDefinitionData"), new BinTreeProperty[] { new BinTreeString(H("emitterName"), name) }.Concat(fields).ToArray());

    internal static BinTreeObject SystemObject(uint path, string name, params BinTreeEmbedded[] emitters) => new(path, H("VfxSystemDefinitionData"), new BinTreeProperty[]
    {
        new BinTreeString(H("particleName"), name),
        new BinTreeContainer(H("complexEmitterDefinitionData"), BinPropertyType.Embedded, emitters.Cast<BinTreeProperty>().ToArray()),
    });

    internal static byte[] Write(params BinTreeObject[] objects)
    {
        using var ms = new MemoryStream();
        new BinTree(objects, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    internal const uint Sys = 0xA1B2C3D4;

    private static readonly Vector4 Orange = new(1f, 0.45f, 0.05f, 0.8f);
    private static readonly Vector4 Teal = new(0.1f, 0.7f, 0.6f, 1f);

    /// <summary>One system, five emitters, each a case: colours and curves of every field, an absent field, a table-only struct, an HDR colour, the masks.</summary>
    internal static byte[] Sample() => Write(SystemObject(Sys, "Sample",
        // 0: birth (constant + curve + chromatic scatter), colour over life (curve, NO constant), linger, both fresnels, the masks and a cubemap
        Emitter("Everything",
            ValueColor("birthColor", Orange, new[] { new Vector4(1f, 0f, 0f, 1f), new Vector4(0f, 1f, 0f, 0.5f), new Vector4(0f, 0f, 1f, 0f) }, new[] { Table(0.5f, 1f), Table(), Table(1f) }),
            ValueColor("color", null, new[] { new Vector4(1f, 1f, 1f, 1f), new Vector4(1f, 1f, 1f, 0f) }),
            new BinTreeEmbedded(H("Linger"), H("VfxLingerDefinitionData"), new BinTreeProperty[] { ValueColor("SeparateLingerColor", Teal) }),
            new BinTreeEmbedded(H("reflectionDefinition"), H("VfxReflectionDefinitionData"), new BinTreeProperty[]
            {
                new BinTreeVector4(H("fresnelColor"), new Vector4(0.2f, 0.4f, 1f, 1f)),
                new BinTreeVector4(H("reflectionFresnelColor"), new Vector4(-0.2f, -0.2f, -0.2f, 1f)),
                new BinTreeString(H("reflectionMapTexture"), "ASSETS/Test/cube.dds"),
            }),
            new BinTreeEmbedded(H("paletteDefinition"), H("VfxPaletteDefinitionData"), new BinTreeProperty[]
            {
                new BinTreeVector4(H("palleteSrcMixColor"), new Vector4(0.3f, 0.59f, 0.11f, 0f)),
                new BinTreeString(H("paletteTexture"), "ASSETS/Test/palette.tex"),
            }),
            new BinTreeEmbedded(H("alphaErosionDefinition"), H("VfxAlphaErosionDefinitionData"), new BinTreeProperty[]
            {
                new BinTreeVector4(H("erosionMapChannelMixer"), new Vector4(1f, 0f, 0f, 0f)),
                new BinTreeString(H("erosionMapName"), "ASSETS/Test/erosion.tex"),
            })),
        // 1: no colour field at all: nothing to list, nothing to write
        Emitter("Plain", new BinTreeF32(H("rate"), 3f)),
        // 2: a colour struct that authors only a probability table: no colour value of its own
        Emitter("TableOnly", ValueColor("color", null, tables: new[] { Table(0.5f, 1f) })),
        // 3: an HDR colour and a Color-typed (bytes) one
        Emitter("Hdr", ValueColor("birthColor", new Vector4(255f, 128f, 0f, 255f)), ValueColor("color", new Vector4(0.9f, 0.2f, 0.4f, 1f), asColor: true)),
        // 4: the birth colour is white and the colour over life carries the colour; a distortion normal map beside
        Emitter("LifeCarries", ValueColor("birthColor", Vector4.One), ValueColor("color", null, new[] { new Vector4(0.9f, 0.1f, 0.1f, 1f), new Vector4(0.9f, 0.1f, 0.1f, 0f) }))));

    private static EffectColorScan ScanOf(byte[] bin) => SkinEffectColors.Read(bin);

    private static EffectColorKey Key(int emitter, string field) => new(Sys, emitter, field);

    private static EffectColorField FieldOf(byte[] bin, int emitter, string field) => ScanOf(bin).Fields.Single(f => f.Key == Key(emitter, field));

    private static readonly ColorTransform Hue120 = new() { HueShiftDegrees = 120f };

    // ======================================================================================== reading

    [Fact]
    public void OnlyTheFieldsAnEmitterAuthorsAreListed_AnAbsentFieldAndATableOnlyStructAreNot()
    {
        var scan = ScanOf(Sample());
        var keys = scan.Fields.Select(f => (f.Key.Emitter, f.Key.Field)).ToHashSet();
        Assert.Contains((0, "birthColor"), keys);
        Assert.Contains((0, "color"), keys);
        Assert.Contains((0, "Linger.SeparateLingerColor"), keys);
        Assert.Contains((0, "reflectionDefinition.fresnelColor"), keys);
        Assert.Contains((0, "reflectionDefinition.reflectionFresnelColor"), keys);
        Assert.DoesNotContain(keys, k => k.Emitter == 1);                       // authors nothing
        Assert.DoesNotContain(keys, k => k.Emitter == 2);                       // a struct that holds only a probability table has no colour to recolour
        Assert.Contains((3, "birthColor"), keys);
        Assert.Contains((3, "color"), keys);
        Assert.Equal(1, scan.Fields.Count(f => f.Key.Emitter == 4 && f.Key.Field == "color"));
    }

    [Fact]
    public void AFieldIsRead_AsAuthored_ConstantKeysShapeAndRandomisation()
    {
        var bin = Sample();
        var birth = FieldOf(bin, 0, "birthColor");
        Assert.Equal(Orange, birth.Constant);
        Assert.Equal(3, birth.Keys.Count);
        Assert.Equal(new Vector4(0f, 1f, 0f, 0.5f), birth.Keys[1]);
        Assert.False(birth.IsBare);
        Assert.Equal(EffectProbability.Chromatic, birth.Probability);          // a table on red and on blue, none on green

        var life = FieldOf(bin, 0, "color");
        Assert.Null(life.Constant);                                            // a curve that writes no constant is read as having none
        Assert.Equal(2, life.Keys.Count);
        Assert.Equal(EffectProbability.None, life.Probability);

        Assert.True(FieldOf(bin, 0, "reflectionDefinition.fresnelColor").IsBare);   // a bare Vector4, not a ValueColor struct
        Assert.True(FieldOf(bin, 3, "color").StoredAsBytes);
        Assert.Equal(EffectColorRole.Linger, FieldOf(bin, 0, "Linger.SeparateLingerColor").Key.Role);
    }

    [Fact]
    public void ARandomisationIsClassifiedByWhatItDoesToAHueShift()
    {
        BinTreeEmbedded[] None() => new[] { Table(), Table(), Table() };
        EffectProbability Of(BinTreeEmbedded[] tables) => FieldOf(Write(SystemObject(Sys, "p", Emitter("e", ValueColor("birthColor", Orange, new[] { Orange, Orange }, tables)))), 0, "birthColor").Probability;

        Assert.Equal(EffectProbability.None, Of(None()));
        Assert.Equal(EffectProbability.AlphaOnly, Of(new[] { Table(), Table(), Table(), Table(0.2f, 1f) }));
        Assert.Equal(EffectProbability.Uniform, Of(new[] { Table(0.5f, 1f), Table(0.5f, 1f), Table(0.5f, 1f) }));          // one scatter on every colour channel: commutes with a hue shift
        Assert.Equal(EffectProbability.Chromatic, Of(new[] { Table(0.5f, 1f), Table(0.5f, 1f), Table(0.4f, 1f) }));        // the tables differ
        Assert.Equal(EffectProbability.Chromatic, Of(new[] { Table(0.5f, 1f), Table(), Table() }));                          // only red is scattered
    }

    [Fact]
    public void TheMixersAndDataMapsTypedAsColoursAreNeverColourFields_AndAreListedAsLeftAlone()
    {
        var scan = ScanOf(Sample());
        var names = scan.Fields.Select(f => f.Key.Field).ToHashSet();
        foreach (string field in VfxColorReader.MaskFields) Assert.DoesNotContain(field, names);
        Assert.DoesNotContain("paletteDefinition.palleteSrcMixColor", names);
        Assert.DoesNotContain("alphaErosionDefinition.erosionMapChannelMixer", names);

        var left = scan.Excluded.Where(e => e.Emitter == 0).Select(e => e.Field).ToHashSet();
        Assert.Contains("paletteDefinition.palleteSrcMixColor", left);
        Assert.Contains("alphaErosionDefinition.erosionMapChannelMixer", left);
        Assert.Contains("alphaErosionDefinition.erosionMapName", left);
        Assert.Contains("reflectionDefinition.reflectionMapTexture", left);
        Assert.All(scan.Excluded, e => Assert.False(string.IsNullOrWhiteSpace(e.Reason)));
        Assert.Contains("cubemap", scan.Excluded.Single(e => e.Field == "reflectionDefinition.reflectionMapTexture").Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ANegativeColourIsNotRecolourable_AnHdrOneIs()
    {
        var bin = Sample();
        var negative = FieldOf(bin, 0, "reflectionDefinition.reflectionFresnelColor");
        Assert.False(negative.Recolourable);
        Assert.Contains("negative", negative.Reason);
        Assert.True(FieldOf(bin, 3, "birthColor").Recolourable);
    }

    [Fact]
    public void AnEmittersIdentityDoesNotDependOnTheOrderTheBinHoldsItsContainers()
    {
        // two emitter containers in one system, written in opposite property orders: the same emitter is the same key
        BinTreeObject Two(bool reversed)
        {
            var complex = new BinTreeContainer(H("complexEmitterDefinitionData"), BinPropertyType.Embedded, new BinTreeProperty[] { Emitter("c0", ValueColor("birthColor", Orange)) });
            var simple = new BinTreeContainer(H("simpleEmitterDefinitionData"), BinPropertyType.Embedded, new BinTreeProperty[] { Emitter("s0", ValueColor("birthColor", Teal)) });
            var props = new List<BinTreeProperty> { new BinTreeString(H("particleName"), "two") };
            if (reversed) { props.Add(simple); props.Add(complex); } else { props.Add(complex); props.Add(simple); }
            return new BinTreeObject(Sys, H("VfxSystemDefinitionData"), props);
        }
        var a = ScanOf(Write(Two(false))).Fields.ToDictionary(f => f.Key, f => f.EmitterName);
        var b = ScanOf(Write(Two(true))).Fields.ToDictionary(f => f.Key, f => f.EmitterName);
        Assert.Equal(a.OrderBy(kv => kv.Key.Emitter).Select(kv => (kv.Key, kv.Value)), b.OrderBy(kv => kv.Key.Emitter).Select(kv => (kv.Key, kv.Value)));
    }

    // ======================================================================================== writing

    private static EffectColorKey[] AllRecolourable(byte[] bin) => ScanOf(bin).Fields.Where(f => f.Recolourable).Select(f => f.Key).ToArray();

    [Fact]
    public void EveryConstantAndEveryKeyIsTheTransformOfRiotsValue_AlphaUntouched()
    {
        byte[] riot = Sample();
        var keys = AllRecolourable(riot);
        var rewrite = SkinEffectColors.Rewrite(riot, riot, Hue120, keys, Array.Empty<EffectColorKey>());
        Assert.NotNull(rewrite.Bytes);
        Assert.All(rewrite.Result.Edits, e => Assert.True(e.Outcome is EffectColorOutcome.Written or EffectColorOutcome.Unchanged, e.Key + " " + e.Outcome + " " + e.Note));

        var before = ScanOf(riot).Fields.ToDictionary(f => f.Key);
        var after = ScanOf(rewrite.Bytes!).Fields.ToDictionary(f => f.Key);
        var carriers = SkinEffectColors.Carriers(keys.Select(k => before[k]));
        int checkedValues = 0;
        foreach (var key in keys)
        {
            var tf = SkinEffectColors.TransformFor(key, Hue120, carriers);
            var from = before[key]; var now = after[key];
            Assert.Equal(from.Keys.Count, now.Keys.Count);
            Assert.Equal(from.Constant.HasValue, now.Constant.HasValue);
            foreach (var (original, got) in from.Values.Zip(now.Values))
            {
                var expected = tf.Apply(original);
                float tolerance = from.StoredAsBytes ? 1f / 255f : 0f;
                Assert.True(Math.Abs(expected.X - got.X) <= tolerance && Math.Abs(expected.Y - got.Y) <= tolerance && Math.Abs(expected.Z - got.Z) <= tolerance, $"{key}: expected {expected}, got {got}");
                Assert.Equal(original.W, got.W);
                checkedValues++;
            }
        }
        Assert.True(checkedValues >= 13, $"only {checkedValues} values were checked");
        Assert.NotEqual(before[Key(0, "birthColor")].Keys[0], after[Key(0, "birthColor")].Keys[0]);
        Assert.Equal(new Vector4(0f, 1f, 0f, 1f), after[Key(0, "birthColor")].Keys[0]);          // pure red +120 is pure green: exactly
    }

    [Fact]
    public void AConstantTheBinDoesNotWriteIsNotWritten_AndAnAbsentFieldIsNotCreated()
    {
        byte[] riot = Sample();
        var rewrite = SkinEffectColors.Rewrite(riot, riot, Hue120, AllRecolourable(riot), Array.Empty<EffectColorKey>());
        var tree = SafeBinTree.Parse(rewrite.Bytes!);
        var emitters = ((BinTreeContainer)tree.Objects[Sys].Properties[H("complexEmitterDefinitionData")]).Elements.Cast<BinTreeStruct>().ToList();

        // the colour over life of emitter 0 and emitter 4 wrote no constantValue and still writes none
        var life = (BinTreeStruct)emitters[0].Properties[H("color")];
        Assert.False(life.Properties.ContainsKey(H("constantValue")));
        Assert.False(((BinTreeStruct)emitters[4].Properties[H("color")]).Properties.ContainsKey(H("constantValue")));
        // the emitter that authors no colour has none now; the table-only struct is as it was
        Assert.False(emitters[1].Properties.ContainsKey(H("birthColor")));
        Assert.False(emitters[1].Properties.ContainsKey(H("color")));
        Assert.False(((BinTreeStruct)emitters[2].Properties[H("color")]).Properties.ContainsKey(H("constantValue")));
        Assert.False(((BinTreeStruct)emitters[2].Properties[H("color")]).Properties.ContainsKey(H("dynamics")) && ((BinTreeStruct)((BinTreeStruct)emitters[2].Properties[H("color")]).Properties[H("dynamics")]).Properties.ContainsKey(H("values")));
    }

    [Fact]
    public void AnAbsentOrMissingFieldIsSkippedByStructure_NotBecauseTheTransformHappensToDoNothing()
    {
        byte[] riot = Sample();
        // brightness is NOT a no-op on white: a recolour that wrote a field the emitter does not author would change the effect
        var brighter = new ColorTransform { Brightness = 2f };
        var absent = new[] { Key(1, "birthColor"), Key(2, "color"), Key(9, "birthColor") };
        var rewrite = SkinEffectColors.Rewrite(riot, riot, brighter, absent, Array.Empty<EffectColorKey>());
        Assert.Null(rewrite.Bytes);
        Assert.All(rewrite.Result.Edits, e => Assert.Equal(EffectColorOutcome.Missing, e.Outcome));
    }

    [Fact]
    public void ANegativeColourStaysExactlyAsAuthored_AnHdrColourKeepsItsIntensity_AColorIsClamped()
    {
        byte[] riot = Sample();
        var rewrite = SkinEffectColors.Rewrite(riot, riot, new ColorTransform { HueShiftDegrees = 90f },
            new[] { Key(0, "reflectionDefinition.reflectionFresnelColor"), Key(3, "birthColor"), Key(3, "color") }, Array.Empty<EffectColorKey>());
        Assert.Equal(EffectColorOutcome.Skipped, rewrite.Result.Edits.Single(e => e.Key.Field == "reflectionDefinition.reflectionFresnelColor").Outcome);
        var after = ScanOf(rewrite.Bytes!).Fields.ToDictionary(f => f.Key);
        Assert.Equal(new Vector4(-0.2f, -0.2f, -0.2f, 1f), after[Key(0, "reflectionDefinition.reflectionFresnelColor")].Constant);

        var hdr = after[Key(3, "birthColor")].Constant!.Value;
        Assert.Equal(255f, Math.Max(hdr.X, Math.Max(hdr.Y, hdr.Z)), 3);                  // the largest channel is still 255: the intensity survives
        Assert.Equal(255f, hdr.W);
        var bytes = after[Key(3, "color")].Constant!.Value;                               // a Color property: red, green and blue in 0..1
        Assert.All(new[] { bytes.X, bytes.Y, bytes.Z }, c => Assert.InRange(c, 0f, 1f));
    }

    [Fact]
    public void EverythingNotRecolouredIsRiots_TheMixersAndDataMapsByteForByte()
    {
        byte[] riot = Sample();
        var owned = AllRecolourable(riot);
        var rewrite = SkinEffectColors.Rewrite(riot, riot, new ColorTransform { HueShiftDegrees = 200f, Saturation = 1.2f, Brightness = 0.9f }, owned, Array.Empty<EffectColorKey>());
        var tree = SafeBinTree.Parse(rewrite.Bytes!);
        // put the owned fields back: the bin is Riot's, object for object
        SkinEffectColors.Apply(tree, SafeBinTree.Parse(riot), ColorTransform.Identity, Array.Empty<EffectColorKey>(), owned);
        Assert.Null(BinTreeEquivalence.FirstDifference(SafeBinTree.Parse(riot), tree));

        // and the mixers themselves, read straight from the written bytes
        var emitter = (BinTreeStruct)((BinTreeContainer)SafeBinTree.Parse(rewrite.Bytes!).Objects[Sys].Properties[H("complexEmitterDefinitionData")]).Elements[0];
        Assert.Equal(new Vector4(0.3f, 0.59f, 0.11f, 0f), ((BinTreeVector4)((BinTreeStruct)emitter.Properties[H("paletteDefinition")]).Properties[H("palleteSrcMixColor")]).Value);
        Assert.Equal(new Vector4(1f, 0f, 0f, 0f), ((BinTreeVector4)((BinTreeStruct)emitter.Properties[H("alphaErosionDefinition")]).Properties[H("erosionMapChannelMixer")]).Value);
    }

    [Fact]
    public void AnIdentityTransformWritesNothing_AndASecondRunWritesNothingMore()
    {
        byte[] riot = Sample();
        Assert.Null(SkinEffectColors.Rewrite(riot, riot, ColorTransform.Identity, AllRecolourable(riot), Array.Empty<EffectColorKey>()).Bytes);

        var first = SkinEffectColors.Rewrite(riot, riot, Hue120, AllRecolourable(riot), Array.Empty<EffectColorKey>());
        var again = SkinEffectColors.Rewrite(first.Bytes!, riot, Hue120, AllRecolourable(riot), Array.Empty<EffectColorKey>());
        Assert.Null(again.Bytes);                                                          // already as wanted
        Assert.All(again.Result.Edits, e => Assert.Equal(EffectColorOutcome.Unchanged, e.Outcome));
    }

    [Fact]
    public void ASecondTransformIsDerivedFromRiotsValue_NeverFromTheFirstRecolour()
    {
        byte[] riot = Sample();
        var keys = AllRecolourable(riot);
        var first = SkinEffectColors.Rewrite(riot, riot, Hue120, keys, Array.Empty<EffectColorKey>());
        var other = new ColorTransform { HueShiftDegrees = 200f, Saturation = 0.8f };
        var second = SkinEffectColors.Rewrite(first.Bytes!, riot, other, keys, Array.Empty<EffectColorKey>());
        var direct = SkinEffectColors.Rewrite(riot, riot, other, keys, Array.Empty<EffectColorKey>());
        Assert.Equal(ScanOf(direct.Bytes!).Fields.SelectMany(f => f.Values).ToList(), ScanOf(second.Bytes!).Fields.SelectMany(f => f.Values).ToList());
    }

    [Fact]
    public void AFieldGivenBackIsRiotsValue_UnlessSomebodyEditedItSince()
    {
        byte[] riot = Sample();
        var key = Key(0, "birthColor");
        var written = SkinEffectColors.Rewrite(riot, riot, Hue120, new[] { key }, Array.Empty<EffectColorKey>());

        // given back: Riot's value, the bin is Riot's data again
        var back = SkinEffectColors.Rewrite(written.Bytes!, riot, Hue120, Array.Empty<EffectColorKey>(), new[] { key }, recipe: Hue120, recipeKeys: new[] { key });
        Assert.Equal(EffectColorOutcome.Written, back.Result.Edits.Single().Outcome);
        Assert.Null(BinTreeEquivalence.FirstDifference(SafeBinTree.Parse(riot), SafeBinTree.Parse(back.Bytes!)));

        // edited in the Particle Editor since: the recipe no longer holds what it wrote, so the give-back leaves the edit
        var edited = SafeBinTree.Parse(written.Bytes!);
        SkinEffectColors.Apply(edited, SafeBinTree.Parse(riot), new ColorTransform { HueShiftDegrees = 33f }, new[] { key }, Array.Empty<EffectColorKey>());
        var kept = SkinEffectColors.Rewrite(SkinEffectColors.Serialize(edited), riot, Hue120, Array.Empty<EffectColorKey>(), new[] { key }, recipe: Hue120, recipeKeys: new[] { key });
        Assert.Equal(EffectColorOutcome.Kept, kept.Result.Edits.Single().Outcome);
        Assert.Null(kept.Bytes);

        // an explicit revert (no recipe) puts Riot's value back whatever it holds
        var forced = SkinEffectColors.Rewrite(SkinEffectColors.Serialize(edited), riot, Hue120, Array.Empty<EffectColorKey>(), new[] { key });
        Assert.Equal(EffectColorOutcome.Written, forced.Result.Edits.Single().Outcome);
    }

    [Fact]
    public void ABinThatChangedShape_IsLeftAloneWithAReason()
    {
        byte[] riot = Sample();
        // the project's copy lost a key of the birth colour curve (somebody edited it): not the same field any more
        var edited = Write(SystemObject(Sys, "Sample", Emitter("Everything", ValueColor("birthColor", Orange, new[] { Vector4.One, Vector4.Zero }))));
        var rewrite = SkinEffectColors.Rewrite(edited, riot, Hue120, new[] { Key(0, "birthColor") }, Array.Empty<EffectColorKey>());
        Assert.Null(rewrite.Bytes);
        Assert.Equal(EffectColorOutcome.Skipped, rewrite.Result.Edits.Single().Outcome);
        Assert.Contains("another shape", rewrite.Result.Edits.Single().Note);
    }

    // ======================================================================================== the product rule

    private static EffectColorField Field(int emitter, string field, float chroma) =>
        new(Key(emitter, field), "s", "e", false, false, Orange, Array.Empty<Vector4>(), EffectProbability.None, true, "", chroma);

    [Fact]
    public void BrightnessAndSaturationAreCarriedByOneFactorOfAnEmittersColour_TheMostColourfulOne()
    {
        // birth (1, 0.3, 0.03) x a white colour over life: the birth colour carries
        var carriers = SkinEffectColors.Carriers(new[] { Field(0, "birthColor", 0.97f), Field(0, "color", 0f) });
        Assert.Contains(Key(0, "birthColor"), carriers);
        Assert.DoesNotContain(Key(0, "color"), carriers);

        // a white birth colour under a coloured colour over life: the life colour carries
        carriers = SkinEffectColors.Carriers(new[] { Field(0, "birthColor", 0f), Field(0, "color", 0.8f) });
        Assert.Contains(Key(0, "color"), carriers);
        Assert.DoesNotContain(Key(0, "birthColor"), carriers);

        // a tie goes to the colour over life, which the linger colour replaces and so shares a slot with
        carriers = SkinEffectColors.Carriers(new[] { Field(0, "birthColor", 0.5f), Field(0, "color", 0.5f) });
        Assert.Contains(Key(0, "color"), carriers);
        Assert.DoesNotContain(Key(0, "birthColor"), carriers);
        carriers = SkinEffectColors.Carriers(new[] { Field(0, "birthColor", 0.1f), Field(0, "color", 0.2f), Field(0, "Linger.SeparateLingerColor", 0.2f) });
        Assert.Contains(Key(0, "color"), carriers);
        Assert.Contains(Key(0, "Linger.SeparateLingerColor"), carriers);                  // the linger colour replaces the colour over life: it carries with it

        // a lone factor carries; the fresnel colours are not factors of the particle's colour and each carries on its own
        carriers = SkinEffectColors.Carriers(new[] { Field(1, "birthColor", 0.4f), Field(1, "reflectionDefinition.fresnelColor", 0.3f), Field(1, "reflectionDefinition.reflectionFresnelColor", 0.3f) });
        Assert.Equal(3, carriers.Count);
        // an emitter is decided on its own
        carriers = SkinEffectColors.Carriers(new[] { Field(0, "birthColor", 0.9f), Field(0, "color", 0.1f), Field(1, "birthColor", 0.1f), Field(1, "color", 0.9f) });
        Assert.Contains(Key(0, "birthColor"), carriers);
        Assert.Contains(Key(1, "color"), carriers);
        Assert.Equal(2, carriers.Count);
    }

    [Fact]
    public void BrightnessIsAppliedOnceToAParticlesColour_NotOncePerFactor()
    {
        // birth (0.8, 0.2, 0.1) x colour over life white: brightness 1.5 must make the PRODUCT 1.5 times as bright, not 2.25 times
        var bright = new ColorTransform { Brightness = 1.5f };
        var birth = new Vector4(0.8f, 0.2f, 0.1f, 1f);
        byte[] riot = Write(SystemObject(Sys, "p", Emitter("e", ValueColor("birthColor", birth), ValueColor("color", Vector4.One))));
        var keys = new[] { Key(0, "birthColor"), Key(0, "color") };
        var after = ScanOf(SkinEffectColors.Rewrite(riot, riot, bright, keys, Array.Empty<EffectColorKey>()).Bytes!).Fields.ToDictionary(f => f.Key);

        var b = after[Key(0, "birthColor")].Constant!.Value;
        var l = after[Key(0, "color")].Constant!.Value;
        Assert.Equal(Vector4.One, l);                                                       // the white multiplier is still white
        var product = new Vector4(b.X * l.X, b.Y * l.Y, b.Z * l.Z, 1f);
        var wanted = new Vector4(birth.X * 1.5f, birth.Y * 1.5f, birth.Z * 1.5f, 1f);
        Assert.True(Math.Abs(product.X - wanted.X) < 1e-5f && Math.Abs(product.Y - wanted.Y) < 1e-5f && Math.Abs(product.Z - wanted.Z) < 1e-5f, $"{product} vs {wanted}");

        // the same transform applied to EVERY factor would have compounded: that is what the rule prevents
        var naive = bright.Apply(birth) * bright.Apply(Vector4.One);
        Assert.True(naive.X > product.X * 1.4f);
    }

    [Fact]
    public void HueActsOnEveryFactor_AndLeavesAWhiteFactorWhite()
    {
        var hueOnly = SkinEffectColors.HueOnly(new ColorTransform { HueShiftDegrees = 80f, Saturation = 1.7f, Brightness = 1.4f, Strength = 0.9f });
        Assert.Equal(1f, hueOnly.Saturation);
        Assert.Equal(1f, hueOnly.Brightness);
        Assert.Equal(80f, hueOnly.HueShiftDegrees);
        Assert.Equal(0.9f, hueOnly.Strength);
        Assert.Equal(Vector4.One, hueOnly.Apply(Vector4.One));                              // white stays white under every hue-only transform
        Assert.Equal(new Vector4(0.5f, 0.5f, 0.5f, 0.3f), hueOnly.Apply(new Vector4(0.5f, 0.5f, 0.5f, 0.3f)));
        // a texture takes the hue-only form too
        Assert.Equal(hueOnly, SkinEffectColors.TextureTransform(new ColorTransform { HueShiftDegrees = 80f, Saturation = 1.7f, Brightness = 1.4f, Strength = 0.9f }));
    }

    // ======================================================================================== the live preview

    [Fact]
    public void ThePreviewIsExactlyWhatSavingWouldPlay_AndLetsGoOfWhatIsNoLongerAskedFor()
    {
        byte[] riot = Sample();
        var set = new EffectColorWorkingSet(new[] { ("sample.bin", riot, riot) });
        var original = VfxSystemResolver.ExtractAll(riot)[Sys];
        var keys = AllRecolourable(riot);

        var preview = set.Apply(Hue120, keys, Array.Empty<EffectColorKey>());
        Assert.Contains(Sys, preview.Changed.Keys);
        // the definitions the card publishes are the ones the resolver reads from the bytes the save writes
        byte[] saved = SkinEffectColors.Rewrite(riot, riot, Hue120, keys, Array.Empty<EffectColorKey>()).Bytes!;
        var fromBytes = VfxSystemResolver.ExtractAll(saved)[Sys];
        Assert.Equal(fromBytes.Emitters.Count, preview.Changed[Sys].Emitters.Count);
        for (int i = 0; i < fromBytes.Emitters.Count; i++)
        {
            Assert.Equal(fromBytes.Emitters[i].BirthColor.Constant, preview.Changed[Sys].Emitters[i].BirthColor.Constant);
            Assert.Equal(fromBytes.Emitters[i].BirthColor.Values, preview.Changed[Sys].Emitters[i].BirthColor.Values);
            Assert.Equal(fromBytes.Emitters[i].ColorOverLife?.Values, preview.Changed[Sys].Emitters[i].ColorOverLife?.Values);
            Assert.Equal(fromBytes.Emitters[i].Reflection?.FresnelColor, preview.Changed[Sys].Emitters[i].Reflection?.FresnelColor);
        }
        Assert.NotEqual(original.Emitters[0].BirthColor.Constant, preview.Changed[Sys].Emitters[0].BirthColor.Constant);

        // asking for the same again changes nothing
        Assert.Empty(set.Apply(Hue120, keys, Array.Empty<EffectColorKey>()).Changed);
        // a field no longer asked for goes back to what the project held
        var oneLess = keys.Where(k => k != Key(0, "birthColor")).ToArray();
        var released = set.Apply(Hue120, oneLess, Array.Empty<EffectColorKey>());
        Assert.Equal(original.Emitters[0].BirthColor.Constant, released.Changed[Sys].Emitters[0].BirthColor.Constant);
        Assert.Equal(original.Emitters[0].BirthColor.Values, released.Changed[Sys].Emitters[0].BirthColor.Values);
        // no change at all: everything is the project's again
        var identity = set.Apply(ColorTransform.Identity, Array.Empty<EffectColorKey>(), Array.Empty<EffectColorKey>());
        Assert.Equal(original.Emitters[0].ColorOverLife?.Values, identity.Changed[Sys].Emitters[0].ColorOverLife?.Values);
        Assert.Equal(original.Emitters[3].BirthColor.Constant, identity.Changed[Sys].Emitters[3].BirthColor.Constant);
    }

    [Fact]
    public void RebasingThePreviewTakesTheProjectsNewBinAsTheBaseline()
    {
        byte[] riot = Sample();
        var set = new EffectColorWorkingSet(new[] { ("sample.bin", riot, riot) });
        var keys = AllRecolourable(riot);
        set.Apply(Hue120, keys, Array.Empty<EffectColorKey>());

        // the save wrote the recolour into the project: the baseline is that bin, and giving the fields up goes to Riot's only when asked
        byte[] saved = SkinEffectColors.Rewrite(riot, riot, Hue120, keys, Array.Empty<EffectColorKey>()).Bytes!;
        var touched = set.Rebase(_ => saved);
        Assert.Contains(Sys, touched);
        Assert.Empty(set.Apply(Hue120, keys, Array.Empty<EffectColorKey>()).Changed);       // as wanted already
        var back = set.Apply(ColorTransform.Identity, Array.Empty<EffectColorKey>(), keys);   // owned and no longer wanted: Riot's
        Assert.Equal(VfxSystemResolver.ExtractAll(riot)[Sys].Emitters[0].BirthColor.Constant, back.Changed[Sys].Emitters[0].BirthColor.Constant);
    }

    // ======================================================================================== the record

    [Fact]
    public void TheRecipeIsStoredGroupedBySystem_AndReadsBackAsTheSameFieldsWithNoValuesInIt()
    {
        var project = new ReyEngine.Core.Projects.ReyProject();
        var record = new ReyEngine.Core.Projects.ChromaEffectRecord
        {
            ChromaSkin = "data/characters/lillia/skins/skin49.bin",
            Transform = new ColorTransform { HueShiftDegrees = 120f, Saturation = 1.3f },
            Colors = new List<ReyEngine.Core.Projects.ChromaEffectColorRef>
            {
                new() { System = 0xAAAA0001, Emitter = 0, Field = "birthColor", Bin = "data/a.bin", SystemName = "A" },
                new() { System = 0xAAAA0001, Emitter = 2, Field = "Linger.SeparateLingerColor", Bin = "data/a.bin", SystemName = "A" },
                new() { System = 0xBBBB0002, Emitter = 1, Field = "color", Bin = "data/b.bin", SystemName = "B" },
            },
        };
        project.ChromaEffectRecolors = new List<ReyEngine.Core.Projects.ChromaEffectRecord> { record };
        string json = System.Text.Json.JsonSerializer.Serialize(project, new System.Text.Json.JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        Assert.Contains("\"Systems\"", json);
        Assert.Contains("0:birthColor", json);
        Assert.DoesNotContain("\"Colors\"", json);                                          // the flat view is not stored
        Assert.DoesNotContain("constantValue", json);

        var back = System.Text.Json.JsonSerializer.Deserialize<ReyEngine.Core.Projects.ReyProject>(json)!.ChromaEffectRecolors!.Single();
        Assert.Equal(record.Transform, back.Transform);
        Assert.Equal(2, back.Systems.Count);
        Assert.Equal(record.Colors.Select(c => (c.System, c.Emitter, c.Field, c.Bin)).OrderBy(x => x).ToList(), back.Colors.Select(c => (c.System, c.Emitter, c.Field, c.Bin)).OrderBy(x => x).ToList());

        // an older project.json has no new key at all
        var older = System.Text.Json.JsonSerializer.Serialize(new ReyEngine.Core.Projects.ReyProject(), new System.Text.Json.JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        Assert.DoesNotContain("ChromaEffectRecolors", older);
        Assert.DoesNotContain("ChromaPart", older);
    }
}
