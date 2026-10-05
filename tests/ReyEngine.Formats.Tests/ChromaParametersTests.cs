using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Materials;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M825: the Chroma Studio's recolour of a skin's colour PARAMETERS on synthetic bins - the rules, one at a time. These run anywhere;
/// <see cref="ChromaParametersRealDataTests"/> holds the same promises against Riot's own files and the real view models.
/// </summary>
public sealed class ChromaParametersTests : IDisposable
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static readonly Dictionary<uint, string> Known = new[]
    {
        "StaticMaterialDef", "skeleton", "simpleSkin", "materialOverride", "material", "texture", "fresnelColor", "reflectionFresnelColor",
    }.ToDictionary(H);
    private static string? Resolve(uint h) => Known.TryGetValue(h, out var n) ? n : null;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "reyengine-m825-" + Guid.NewGuid().ToString("N"));
    public ChromaParametersTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    // ===================================================================== builders

    private static BinTreeEmbedded Param(string name, BinTreeProperty? value) =>
        new(0, H("StaticMaterialShaderParamDef"), value is null
            ? new BinTreeProperty[] { new BinTreeString(H("name"), name) }
            : new BinTreeProperty[] { new BinTreeString(H("name"), name), value });

    private static BinTreeProperty V4(float x, float y, float z, float w) => new BinTreeVector4(H("value"), new Vector4(x, y, z, w));
    private static BinTreeProperty V3(float x, float y, float z) => new BinTreeVector3(H("value"), new Vector3(x, y, z));

    private static BinTreeObject Material(string path, params BinTreeEmbedded[] parameters) =>
        new(H(path), H("StaticMaterialDef"), new BinTreeProperty[]
        {
            new BinTreeString(H("name"), path),
            new BinTreeUnorderedContainer(H("paramValues"), BinPropertyType.Embedded, parameters),
        });

    private static BinTreeObject Skin(string path, BinTreeObject material, Vector4? reflectionFresnel = null) =>
        new(H(path), H("SkinCharacterDataProperties"), new BinTreeProperty[]
        {
            new BinTreeEmbedded(H("skinMeshProperties"), H("SkinMeshDataProperties"), new BinTreeProperty?[]
            {
                new BinTreeString(H("simpleSkin"), "assets/characters/zz/skins/base/zz.skn"),
                new BinTreeString(H("skeleton"), "assets/characters/zz/skins/base/zz.skl"),
                new BinTreeString(H("texture"), "assets/characters/zz/skins/base/zz_tx_cm.tex"),
                new BinTreeObjectLink(H("material"), material.PathHash),
                reflectionFresnel is { } r ? new BinTreeColor(H("reflectionFresnelColor"), new LeagueToolkit.Core.Primitives.Color(r.X, r.Y, r.Z, r.W)) : null,
            }.Where(p => p is not null).Select(p => p!)),
        });

    private static byte[] Bin(params BinTreeObject[] objects)
    {
        using var ms = new MemoryStream();
        new BinTree(objects, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    private const string MatPath = "Characters/Zz/Skins/Skin1/Materials/Zz_Skin1_Body_inst";

    /// <summary>A skin with one material carrying every kind of parameter the rules tell apart.</summary>
    private static byte[] RiotBin(Vector4? reflection = null)
    {
        var material = Material(MatPath,
            Param("TintColor", V4(1f, 0.25f, 0.1f, 1f)),                   // an ordinary colour
            Param("OutlineColor", V4(0.2f, 0.4f, 0.8f, 0.6f)),             // a colour with a real alpha
            Param("Glow_Color", V4(1.8f, 0.5f, 0.15f, 0f)),                // HDR: above 1
            Param("Rim_Color", V3(0.1f, 0.6f, 0.2f)),                      // a Vector3 colour
            Param("Dark_Color", V4(-0.15f, -0.15f, -0.05f, 1f)),           // negative: never touched
            Param("VColor_G_Mask_Discard_Size", V4(100f, 0f, 0f, 0f)),     // a number named after a colour
            Param("ColorFresnelSize", V4(0.4f, 0f, 0f, 0f)),               // another
            Param("Fresnel_Color_Intensity", V4(14.19f, 0.2f, 0f, 0f)),    // 'intensity' in the name
            Param("Bloom_Color", null),                                    // named, no value: an authored zero
            Param("Twin_Color", V4(1f, 0f, 0f, 1f)),                       // a name the material repeats...
            Param("Twin_Color", V4(0f, 0f, 1f, 1f)));                      // ...with another value
        return Bin(Skin("Characters/Zz/Skins/Skin1", material, reflection ?? new Vector4(0.9f, 0.3f, 0.1f, 1f)), material);
    }

    private static SkinColorParamKey Key(string name, int occurrence = 0, string material = MatPath) => new(H(material), name, occurrence);
    private static readonly ColorTransform Green = new() { HueShiftDegrees = 120f };

    private static IReadOnlyDictionary<string, SkinColorParam> Params(byte[] bin) =>
        SkinColorParameters.Read(bin, Resolve).GroupBy(p => p.Key.Name + (p.Key.Occurrence > 0 ? "#" + p.Key.Occurrence : "")).ToDictionary(g => g.Key, g => g.First());

    // ===================================================================== reading and the verdict

    [Fact]
    public void TheReaderKeysEveryColourParameterByMaterialNameAndOccurrence_AndSaysWhichOnesARecolourMayMove()
    {
        var read = Params(RiotBin());

        // M812's heuristic lists all of these (the name says colour, the value is a Vector4 / Color / Vector3)...
        foreach (string name in new[] { "TintColor", "OutlineColor", "Glow_Color", "Rim_Color", "Dark_Color", "VColor_G_Mask_Discard_Size", "ColorFresnelSize",
                                         "Fresnel_Color_Intensity", "Bloom_Color", "Twin_Color", "Twin_Color#1", "reflectionFresnelColor" })
            Assert.True(read.ContainsKey(name), name + " was not listed");

        // ...a repeated name gets its own key, and the skin's own block is material 0
        Assert.Equal(Key("Twin_Color", 0), read["Twin_Color"].Key);
        Assert.Equal(Key("Twin_Color", 1), read["Twin_Color#1"].Key);
        Assert.Equal(new SkinColorParamKey(0, "reflectionFresnelColor", 0), read["reflectionFresnelColor"].Key);
        Assert.Equal("Color", read["reflectionFresnelColor"].TypeName);

        // the verdict a WRITE needs
        foreach (string ok in new[] { "TintColor", "OutlineColor", "Glow_Color", "Rim_Color", "Twin_Color", "Twin_Color#1", "reflectionFresnelColor" })
            Assert.True(read[ok].Recolourable, ok + ": " + read[ok].Reason);
        Assert.False(read["Dark_Color"].Recolourable);
        Assert.Contains("negative", read["Dark_Color"].Reason);
        Assert.False(read["VColor_G_Mask_Discard_Size"].Recolourable);                 // 'mask' (and 'size')
        Assert.False(read["ColorFresnelSize"].Recolourable);                           // 'size'
        Assert.False(read["Fresnel_Color_Intensity"].Recolourable);                    // 'intensity'
        Assert.False(read["Bloom_Color"].Recolourable);                                // no value written
        Assert.True(read["Bloom_Color"].ValueOmitted);
    }

    [Theory]
    [InlineData("BloomColorFresnelSize", 1f, 0f, 0f, 0f, false)]       // a size
    [InlineData("Rim_Color_Strength", 1f, 0f, 0f, 0f, false)]          // a strength
    [InlineData("FresnelColor_Bias", 0.5f, 0f, 0f, 0f, false)]         // a bias
    [InlineData("TintRange", 2f, 0f, 0f, 0f, false)]                   // a range (and no colour word, but the reader never lists it)
    [InlineData("UI_ShapeMaskColor_SliceUV", 1f, 1f, 0f, 0f, false)]   // a mask
    [InlineData("Color_Fresnel", 1.35f, 0f, 0f, 0f, false)]            // no bad word, but a lone number in the first channel
    [InlineData("Metal_Color_R", 1f, 0f, 0f, 0f, false)]               // likewise
    [InlineData("TintColor", 1f, 1f, 1f, 1f, true)]                    // white is a colour (it just does not move)
    [InlineData("Bloom_Color", 0f, 0.13f, 0.12f, 0.44f, true)]
    [InlineData("VFX_ScrollTex_R_Tint", 1f, 0f, 0f, 1f, true)]         // pure red WITH an alpha is a colour, not a number
    [InlineData("tatto_Color", 1.8f, 0.5f, 0.15f, 0f, true)]           // HDR
    public void TheNameAndTheShapeDecideWhetherAVectorIsAColour(string name, float x, float y, float z, float w, bool expected)
    {
        var (ok, reason) = SkinColorParameters.Classify(name, "Vector4", new Vector4(x, y, z, w), false, false, null);
        Assert.True(expected == ok, $"{name} {x},{y},{z},{w}: {reason}");
        Assert.Equal(expected, reason.Length == 0);
    }

    [Fact]
    public void AColourTypedFieldIsAColourUnlessItsNameSaysOtherwise_AndNeverOneInALinkedBin()
    {
        Assert.True(SkinColorParameters.Classify("Whatever", "Color", new Vector4(1, 0, 0, 1), false, false, null).Ok);
        Assert.True(SkinColorParameters.Classify("Rim", "Vector3", new Vector4(0.5f, 0.5f, 0.5f, 1), false, false, null).Ok);   // a Vector3 has no room for a scalar
        Assert.False(SkinColorParameters.Classify("Size", "Vector3", new Vector4(0.5f, 0.5f, 0.5f, 1), false, false, null).Ok);   // but its name still says what it is
        var linked = SkinColorParameters.Classify("TintColor", "Vector4", new Vector4(1, 0.5f, 0.2f, 1), false, true, "DATA/Characters/Zz/Zz_Multi_Skins.bin");
        Assert.False(linked.Ok);
        Assert.Contains("Zz_Multi_Skins.bin", linked.Reason);
    }

    // ===================================================================== the rewrite

    [Fact]
    public void ARecolourIsRiotsValueThroughTheTransform_AlphaAndEverythingElseKept()
    {
        byte[] riot = RiotBin();
        var keys = new[] { Key("TintColor"), Key("OutlineColor"), Key("Rim_Color") };

        var rewrite = SkinColorParameters.Rewrite(riot, riot, Green, keys, Array.Empty<SkinColorParamKey>(), Resolve);
        Assert.NotNull(rewrite.Bytes);
        Assert.All(rewrite.Edits, e => Assert.Equal(SkinColorParamOutcome.Written, e.Outcome));

        var before = Params(riot);
        var after = Params(rewrite.Bytes!);
        foreach (string name in new[] { "TintColor", "OutlineColor" })
        {
            var expected = Green.Apply(before[name].Value);
            Assert.Equal(expected.X, after[name].Value.X, 5);
            Assert.Equal(expected.Y, after[name].Value.Y, 5);
            Assert.Equal(expected.Z, after[name].Value.Z, 5);
            Assert.Equal(before[name].Value.W, after[name].Value.W);                           // alpha is never changed
            Assert.NotEqual(before[name].Value, after[name].Value);
        }
        Assert.Equal(Green.Apply(before["Rim_Color"].Value).Y, after["Rim_Color"].Value.Y, 5);   // a Vector3 colour too

        // every parameter the recolour does not own is exactly as it was
        foreach (string name in new[] { "Glow_Color", "Dark_Color", "VColor_G_Mask_Discard_Size", "ColorFresnelSize", "Fresnel_Color_Intensity", "Twin_Color", "Twin_Color#1", "reflectionFresnelColor" })
            Assert.Equal(before[name].Value, after[name].Value);
        Assert.True(after["Bloom_Color"].ValueOmitted);                                       // a sparse encoding stays sparse
    }

    [Fact]
    public void RewritingTwiceNeverCompounds_ForTheSameTransformOrAnother()
    {
        byte[] riot = RiotBin();
        var keys = new[] { Key("TintColor"), Key("OutlineColor"), Key("Glow_Color") };
        var none = Array.Empty<SkinColorParamKey>();
        var other = new ColorTransform { HueShiftDegrees = 200f, Saturation = 0.8f, Brightness = 1.2f };

        byte[] once = SkinColorParameters.Rewrite(riot, riot, Green, keys, none, Resolve).Bytes!;
        // the same transform again, onto the bin it just made: nothing to write
        var again = SkinColorParameters.Rewrite(once, riot, Green, keys, none, Resolve);
        Assert.Null(again.Bytes);
        Assert.All(again.Edits, e => Assert.Equal(SkinColorParamOutcome.Unchanged, e.Outcome));

        // another transform onto the recoloured bin is the same bytes as that transform onto Riot's bin
        byte[] second = SkinColorParameters.Rewrite(once, riot, other, keys, none, Resolve).Bytes!;
        byte[] direct = SkinColorParameters.Rewrite(riot, riot, other, keys, none, Resolve).Bytes!;
        Assert.Equal(direct, second);

        // and many round trips end where one would
        byte[] bin = riot;
        foreach (var t in new[] { Green, other, Green, other, Green })
            bin = SkinColorParameters.Rewrite(bin, riot, t, keys, none, Resolve).Bytes ?? bin;
        Assert.Equal(SkinColorParameters.Rewrite(riot, riot, Green, keys, none, Resolve).Bytes, bin);
    }

    [Fact]
    public void AValueTheMaterialTabEditedIsKeptWhereTheRecolourDoesNotOwnIt_AndAlphaIsTheCurrentBins()
    {
        byte[] riot = RiotBin();
        // somebody changed Rim_Color and the ALPHA of TintColor in the project's copy
        var edited = MaterialDocument.Parse(riot, Resolve);
        var material = edited.Materials.Single(m => m.Name == MatPath);
        material.Parameters.Single(p => p.Name == "Rim_Color").TrySetColor(new Vector4(0.9f, 0.9f, 0.1f, 1f));
        material.Parameters.Single(p => p.Name == "TintColor").TrySetColor(new Vector4(1f, 0.25f, 0.1f, 0.5f));
        byte[] current = edited.Serialize();

        var rewrite = SkinColorParameters.Rewrite(current, riot, Green, new[] { Key("TintColor") }, Array.Empty<SkinColorParamKey>(), Resolve);
        var after = Params(rewrite.Bytes!);
        Assert.Equal(new Vector4(0.9f, 0.9f, 0.1f, 1f), after["Rim_Color"].Value);          // not owned: carried
        Assert.Equal(0.5f, after["TintColor"].Value.W);                                       // alpha: the current bin's own
        Assert.Equal(Green.Apply(new Vector4(1f, 0.25f, 0.1f, 1f)).Y, after["TintColor"].Value.Y, 5);
    }

    [Fact]
    public void HdrKeepsItsIntensity_AndANegativeColourStaysExactlyAsAuthored()
    {
        byte[] riot = RiotBin();
        var t = new ColorTransform { HueShiftDegrees = 90f };
        var rewrite = SkinColorParameters.Rewrite(riot, riot, t, new[] { Key("Glow_Color"), Key("Dark_Color") }, Array.Empty<SkinColorParamKey>(), Resolve);

        var glow = rewrite.Edits.Single(e => e.Key == Key("Glow_Color"));
        Assert.Equal(SkinColorParamOutcome.Written, glow.Outcome);
        Assert.Equal(1.8f, Math.Max(glow.After.X, Math.Max(glow.After.Y, glow.After.Z)), 4);   // the brightest channel keeps its 1.8
        Assert.Equal(0f, glow.After.W);                                                           // alpha 0 stays 0
        Assert.Equal(t.Apply(glow.Before), glow.After);

        // a negative colour is never offered, never written, and says so
        var dark = rewrite.Edits.Single(e => e.Key == Key("Dark_Color"));
        Assert.Equal(SkinColorParamOutcome.Skipped, dark.Outcome);
        Assert.Contains("negative", dark.Note);
        Assert.Equal(new Vector4(-0.15f, -0.15f, -0.05f, 1f), Params(rewrite.Bytes!)["Dark_Color"].Value);
    }

    [Fact]
    public void ANumberNamedAfterAColourIsNeverWritten_EvenWhenAskedFor()
    {
        byte[] riot = RiotBin();
        var asked = new[] { Key("VColor_G_Mask_Discard_Size"), Key("ColorFresnelSize"), Key("Fresnel_Color_Intensity"), Key("Bloom_Color"), Key("TintColor") };
        var rewrite = SkinColorParameters.Rewrite(riot, riot, Green, asked, Array.Empty<SkinColorParamKey>(), Resolve);

        Assert.Equal(1, rewrite.Written);
        foreach (var name in new[] { "VColor_G_Mask_Discard_Size", "ColorFresnelSize", "Fresnel_Color_Intensity", "Bloom_Color" })
            Assert.Equal(SkinColorParamOutcome.Skipped, rewrite.Edits.Single(e => e.Key == Key(name)).Outcome);
        var after = Params(rewrite.Bytes!);
        Assert.Equal(new Vector4(100f, 0f, 0f, 0f), after["VColor_G_Mask_Discard_Size"].Value);
        Assert.True(after["Bloom_Color"].ValueOmitted);
    }

    [Fact]
    public void ARepeatedNameIsToldApartByItsOccurrence()
    {
        byte[] riot = RiotBin();
        var rewrite = SkinColorParameters.Rewrite(riot, riot, Green, new[] { Key("Twin_Color", 1) }, Array.Empty<SkinColorParamKey>(), Resolve);
        var after = Params(rewrite.Bytes!);
        Assert.Equal(new Vector4(1f, 0f, 0f, 1f), after["Twin_Color"].Value);                 // the first: untouched
        Assert.NotEqual(new Vector4(0f, 0f, 1f, 1f), after["Twin_Color#1"].Value);            // the second: recoloured
        Assert.Equal(Green.Apply(new Vector4(0f, 0f, 1f, 1f)).X, after["Twin_Color#1"].Value.X, 5);
    }

    [Fact]
    public void TheSkinBlocksColourTypedFieldIsRecolouredAsAColour()
    {
        byte[] riot = RiotBin();
        var key = new SkinColorParamKey(0, "reflectionFresnelColor", 0);
        var rewrite = SkinColorParameters.Rewrite(riot, riot, Green, new[] { key }, Array.Empty<SkinColorParamKey>(), Resolve);
        var edit = Assert.Single(rewrite.Edits);
        Assert.Equal(SkinColorParamOutcome.Written, edit.Outcome);
        var expected = Green.Apply(new Vector4(0.9f, 0.3f, 0.1f, 1f));
        Assert.Equal(expected.X, edit.After.X, 2);        // a Color is stored in bytes: close, not bit-exact
        Assert.Equal(expected.Y, edit.After.Y, 2);
        Assert.Equal(1f, edit.After.W);
        Assert.Equal("Color", Params(rewrite.Bytes!)["reflectionFresnelColor"].TypeName);
    }

    [Fact]
    public void RestoreGivesRiotsValueBack_AndOnlyForTheKeysAsked()
    {
        byte[] riot = RiotBin();
        var keys = new[] { Key("TintColor"), Key("OutlineColor") };
        byte[] recoloured = SkinColorParameters.Rewrite(riot, riot, Green, keys, Array.Empty<SkinColorParamKey>(), Resolve).Bytes!;

        var restored = SkinColorParameters.Rewrite(recoloured, riot, ColorTransform.Identity, Array.Empty<SkinColorParamKey>(), new[] { Key("TintColor") }, Resolve);
        var after = Params(restored.Bytes!);
        var original = Params(riot);
        Assert.Equal(original["TintColor"].Value, after["TintColor"].Value);                  // back to Riot's
        Assert.NotEqual(original["OutlineColor"].Value, after["OutlineColor"].Value);         // not asked: still recoloured

        // everything restored is Riot's bin again, as a document
        var all = SkinColorParameters.Rewrite(recoloured, riot, ColorTransform.Identity, Array.Empty<SkinColorParamKey>(), keys, Resolve);
        var allParams = Params(all.Bytes!);
        foreach (var (name, p) in original) Assert.True(allParams[name].Value == p.Value, name);
    }

    [Fact]
    public void AnIdentityTransformWritesRiotsValuesAndAParameterNotInTheBinIsReported()
    {
        byte[] riot = RiotBin();
        var rewrite = SkinColorParameters.Rewrite(riot, riot, ColorTransform.Identity,
            new[] { Key("TintColor"), Key("NoSuchColor") }, Array.Empty<SkinColorParamKey>(), Resolve);
        Assert.Null(rewrite.Bytes);                                                            // nothing differs: no new bin
        Assert.Equal(SkinColorParamOutcome.Unchanged, rewrite.Edits.Single(e => e.Key == Key("TintColor")).Outcome);
        var missing = rewrite.Edits.Single(e => e.Key == Key("NoSuchColor"));
        Assert.Equal(SkinColorParamOutcome.Missing, missing.Outcome);
        Assert.Contains("Riot's skin bin has no such parameter", missing.Note);

        var none = SkinColorParameters.Rewrite(riot, riot, Green, Array.Empty<SkinColorParamKey>(), Array.Empty<SkinColorParamKey>(), Resolve);
        Assert.Null(none.Bytes);
        Assert.Empty(none.Edits);
    }

    [Fact]
    public void TheRewrittenBinIsAnOrdinaryBinThatParsesAgain_AndRewritingItIsStable()
    {
        byte[] riot = RiotBin();
        var keys = new[] { Key("TintColor"), Key("Glow_Color") };
        byte[] once = SkinColorParameters.Rewrite(riot, riot, Green, keys, Array.Empty<SkinColorParamKey>(), Resolve).Bytes!;
        _ = new BinTree(new MemoryStream(once, false));
        // the writer's own order is a fixed point: serialising the result again changes nothing
        Assert.Equal(once, MaterialDocument.Parse(once, Resolve).Serialize());
        Assert.Equal(riot.Length, once.Length);   // the edit moved no bytes in or out (floats replace floats)
    }

    [Fact]
    public void AColorTypedParameterIsClampedToTheRangeItsBytesHold_NeverWrappedOrOverflowed()
    {
        byte[] riot = RiotBin();   // the skin block's reflectionFresnelColor is (0.9, 0.3, 0.1, 1), a Color
        var key = new SkinColorParamKey(0, "reflectionFresnelColor", 0);
        var brighter = new ColorTransform { Brightness = 2f };

        var rewrite = SkinColorParameters.Rewrite(riot, riot, brighter, new[] { key }, Array.Empty<SkinColorParamKey>(), Resolve);

        var edit = Assert.Single(rewrite.Edits);
        Assert.Equal(SkinColorParamOutcome.Written, edit.Outcome);
        Assert.InRange(edit.After.X, 0.99f, 1f);                      // 0.9 * 2 saturates at 1: not 1.8 in a byte
        Assert.InRange(edit.After.Y, 0.58f, 0.62f);                   // 0.6, untouched by the clamp
        Assert.InRange(edit.After.Z, 0.18f, 0.22f);
        Assert.Equal(1f, edit.After.W);
        Assert.All(new[] { edit.After.X, edit.After.Y, edit.After.Z, edit.After.W }, c => Assert.InRange(c, 0f, 1f));
        // and a darkening to nothing does not go below zero
        var dark = SkinColorParameters.Rewrite(riot, riot, new ColorTransform { Brightness = 0f }, new[] { key }, Array.Empty<SkinColorParamKey>(), Resolve);
        Assert.All(new[] { dark.Edits.Single().After.X, dark.Edits.Single().After.Y, dark.Edits.Single().After.Z }, c => Assert.InRange(c, 0f, 1f));
        // a Vector4 is NOT clamped: it is a float, and HDR is what it is for
        var tint = SkinColorParameters.Rewrite(riot, riot, brighter, new[] { Key("OutlineColor") }, Array.Empty<SkinColorParamKey>(), Resolve);
        Assert.True(tint.Edits.Single().After.Z > 1f);
    }

    [Fact]
    public async Task ThePreviewOfAColorTypedParameterIsClampedToo()
    {
        var host = new FakeHost
        {
            Infos = new List<ChromaParameterInfo>
            {
                new(new SkinColorParamKey(0, "fresnelColor", 0), "(skin default texture)", "Color", new Vector4(0.9f, 0.3f, 0.1f, 1f), new Vector4(0.9f, 0.3f, 0.1f, 1f), true, "", false),
            },
        };
        var card = Card(host);
        var pushed = new List<ChromaParamPush>();
        card.PushChromaParamsGl = items => { pushed.Clear(); pushed.AddRange(items); };
        await Scan(card);
        card.ChromaBrightness = 2;
        await card.ChromaIdleAsync();
        var push = Assert.Single(pushed);
        Assert.All(push.Value, c => Assert.InRange(c, 0f, 1f));
        Assert.InRange(push.Value[0], 0.99f, 1f);
    }

    [Theory]
    [InlineData("Mask_Color", "Color")]
    [InlineData("Fresnel_Color_Intensity", "Vector3")]
    [InlineData("Dissolve_Color_Size", "Color")]
    public void TheNameTestAppliesToEveryType_NotOnlyToAVector4(string name, string type)
    {
        var (ok, reason) = SkinColorParameters.Classify(name, type, new Vector4(0.5f, 0.5f, 0.5f, 1f), false, false, null);
        Assert.False(ok);
        Assert.Contains("number or a mask", reason);
        Assert.True(SkinColorParameters.Classify("Rim_Color", type, new Vector4(0.5f, 0.2f, 0.1f, 1f), false, false, null).Ok);
    }

    [Fact]
    public void AParameterToGiveBackThatWasEditedSinceIsKept_UnlessTheRevertIsExplicit()
    {
        byte[] riot = RiotBin();
        var keys = new[] { Key("TintColor"), Key("OutlineColor") };
        byte[] recoloured = SkinColorParameters.Rewrite(riot, riot, Green, keys, Array.Empty<SkinColorParamKey>(), Resolve).Bytes!;
        // the Material tab changes TintColor afterwards
        var doc = MaterialDocument.Parse(recoloured, Resolve);
        Assert.True(doc.Materials.Single(m => m.Name == MatPath).Parameters.Single(p => p.Name == "TintColor").TrySetColor(new Vector4(0.1f, 0.2f, 0.3f, 1f)));
        byte[] edited = doc.Serialize();

        // giving both back under the recipe: OutlineColor goes, TintColor is kept as edited
        var gentle = SkinColorParameters.Rewrite(edited, riot, ColorTransform.Identity, Array.Empty<SkinColorParamKey>(), keys, Resolve, recipe: Green);
        Assert.Equal(SkinColorParamOutcome.Kept, gentle.Edits.Single(e => e.Key == Key("TintColor")).Outcome);
        Assert.Equal(SkinColorParamOutcome.Written, gentle.Edits.Single(e => e.Key == Key("OutlineColor")).Outcome);
        var after = Params(gentle.Bytes!);
        Assert.Equal(new Vector4(0.1f, 0.2f, 0.3f, 1f), after["TintColor"].Value);
        Assert.Equal(Params(riot)["OutlineColor"].Value, after["OutlineColor"].Value);

        // an explicit Revert (no recipe given) puts both back
        var forced = SkinColorParameters.Rewrite(edited, riot, ColorTransform.Identity, Array.Empty<SkinColorParamKey>(), keys, Resolve);
        Assert.Equal(Params(riot)["TintColor"].Value, Params(forced.Bytes!)["TintColor"].Value);

        // and what the recipe wrote is recognised as its own
        Assert.True(SkinColorParameters.HoldsRecipeValue(Params(recoloured)["TintColor"].Value, Params(riot)["TintColor"].Value, Green, false));
        Assert.False(SkinColorParameters.HoldsRecipeValue(new Vector4(0.1f, 0.2f, 0.3f, 1f), Params(riot)["TintColor"].Value, Green, false));
    }

    // ===================================================================== the record

    [Fact]
    public void TheRecipeRoundTripsThroughProjectJson_AndAnOldProjectLoadsAndSavesWithoutTheNewKey()
    {
        // an existing project (no parameter recipe) writes no new key and loads with none
        var plain = new ReyProject { Name = "plain" };
        string plainPath = Path.Combine(_dir, "plain.reyproj.json");
        ReyProjectService.Save(plain, plainPath);
        string plainJson = File.ReadAllText(plainPath);
        Assert.DoesNotContain("ChromaParameterRecolors", plainJson);
        Assert.Null(ReyProjectService.Open(plainPath).ChromaParameterRecolors);

        // a recipe survives a save and a load, field for field
        var project = new ReyProject { Name = "recipe" };
        var transform = new ColorTransform
        {
            HueShiftDegrees = 75f, Saturation = 1.25f, Brightness = 0.9f, ColorizeHueDegrees = 200f,
            HueSelection = new HueRange(10f, 40f, 15f), GreyProtection = new GreyProtection(0.08f, 0.06f), Strength = 0.8f,
        };
        project.ChromaParameterRecolors = new List<ChromaParameterRecord>
        {
            new()
            {
                ChromaSkin = "data/characters/zz/skins/skin1.bin", Transform = transform,
                Parameters = new List<ChromaParameterRef>
                {
                    new() { Material = H(MatPath), MaterialName = MatPath, Name = "TintColor", Occurrence = 0 },
                    new() { Material = 0, MaterialName = "(skin default texture)", Name = "reflectionFresnelColor", Occurrence = 0 },
                    new() { Material = H(MatPath), MaterialName = MatPath, Name = "Twin_Color", Occurrence = 1 },
                },
            },
        };
        string path = Path.Combine(_dir, "recipe.reyproj.json");
        ReyProjectService.Save(project, path);
        var loaded = ReyProjectService.Open(path);
        var record = Assert.Single(loaded.ChromaParameterRecolors!);
        Assert.Equal("data/characters/zz/skins/skin1.bin", record.ChromaSkin);
        Assert.Equal(transform, record.Transform);
        Assert.Equal(new[] { ("TintColor", H(MatPath), 0), ("reflectionFresnelColor", 0u, 0), ("Twin_Color", H(MatPath), 1) },
            record.Parameters.Select(p => (p.Name, p.Material, p.Occurrence)));

        // the text carries the names a person reading project.json looks for
        string json = File.ReadAllText(path);
        Assert.Contains("\"ChromaParameterRecolors\"", json);
        Assert.Contains("\"HueShiftDegrees\": 75", json);
        Assert.Contains("\"Occurrence\": 1", json);
    }

    [Fact]
    public void AnOlderBuildsProjectFileWithTextureRecordsAndNoParameterRecipeIsReadUnchanged()
    {
        var project = new ReyProject { Name = "old" };
        project.TextureRecolors.Add(new TextureRecolorRecord
        {
            PathHash = 0x1234, AssetPath = "assets/characters/zz/skins/skin1/zz_tx_cm.tex", Transform = Green, ChromaSkin = "data/characters/zz/skins/skin1.bin",
        });
        string path = Path.Combine(_dir, "old.reyproj.json");
        ReyProjectService.Save(project, path);
        string json = File.ReadAllText(path);
        Assert.DoesNotContain("ChromaParameterRecolors", json);
        var loaded = ReyProjectService.Open(path);
        Assert.Null(loaded.ChromaParameterRecolors);
        Assert.Equal(Green, Assert.Single(loaded.TextureRecolors).Transform);
    }

    // ===================================================================== the card, against a stand-in host

    private const string SkinBinPath = "data/characters/zz/skins/skin1.bin";

    private static ChromaParameterInfo Info(string name, Vector4 riot, bool recolourable = true, string reason = "", bool edited = false, uint material = 7, int occurrence = 0, Vector4? current = null) =>
        new(new SkinColorParamKey(material, name, occurrence), "Characters/Zz/Skins/Skin1/Materials/Body_inst", "Vector4", riot, current ?? riot, recolourable, reason, edited);

    private sealed class FakeHost
    {
        public List<ChromaParameterInfo> Infos = new();
        public string Problem = "";
        public ChromaSavedRecipe? Saved;
        public readonly List<(string Bin, ColorTransform Transform, List<ChromaParameterRef> Targets, List<ChromaParameterRef> Stale)> Saves = new();
        public readonly List<List<ChromaParameterRef>> Reverts = new();
        public Func<IReadOnlyList<ChromaParameterRef>, bool>? Settles;

        public void Wire(MeshPreviewViewModel card)
        {
            card.ReadChromaParameters = (_, _, _) => new ChromaParameterSnapshot(Infos, Problem);
            card.ReadChromaSaved = _ => Saved;
            card.ScanSkinColours = (_, _) => Task.FromResult(SkinColorInventory.Empty(SkinBinPath, "").WithNoWarnings());
            card.ChromaUiPost = a => { a(); return Task.CompletedTask; };
            card.UseDx11Preview = false;
            card.SaveChromaParameters = (bin, t, targets, stale) =>
            {
                Saves.Add((bin, t, targets.ToList(), stale.ToList()));
                var settled = targets.Select(x => new SkinColorParamKey(x.Material, x.Name, x.Occurrence)).ToList();
                return Task.FromResult(new ChromaParamSaveResult(targets.Count, 0, 0, stale.Count, settled, Array.Empty<string>()));
            };
            card.RevertChromaParameters = (_, refs) => { Reverts.Add(refs.ToList()); return Task.FromResult(refs.Count); };
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

    private static FakeHost TypicalHost() => new()
    {
        Infos = new List<ChromaParameterInfo>
        {
            Info("TintColor", new Vector4(1f, 0.25f, 0.1f, 1f)),
            Info("OutlineColor", new Vector4(0.2f, 0.4f, 0.8f, 0.6f)),
            Info("Glow_Color", new Vector4(1.8f, 0.5f, 0.15f, 0f)),
            Info("VColor_G_Mask_Discard_Size", new Vector4(100f, 0f, 0f, 0f), recolourable: false, reason: "Its name says 'mask'."),
            Info("Edited_Color", new Vector4(0.5f, 0.2f, 0.9f, 1f), edited: true, current: new Vector4(0.1f, 0.9f, 0.9f, 1f)),
        },
    };

    [Fact]
    public async Task TheListDefaultsToEveryColourTheRecolourMayMove_AndExplainsTheRest()
    {
        var host = TypicalHost();
        var card = Card(host);
        Assert.False(card.HasChromaParameters);                                // nothing is read until the scan
        await Scan(card);

        Assert.True(card.HasChromaParameters);
        Assert.True(card.HasChromaRecolourControls);
        var byName = card.ChromaParameters.ToDictionary(r => r.Key.Name);
        Assert.True(byName["TintColor"].IsIncluded);
        Assert.True(byName["OutlineColor"].IsIncluded);
        Assert.True(byName["Glow_Color"].IsIncluded);
        var mask = byName["VColor_G_Mask_Discard_Size"];
        Assert.False(mask.CanInclude);
        Assert.False(mask.IsIncluded);
        Assert.Contains("mask", mask.Note);
        var edited = byName["Edited_Color"];                                   // the project's value is somebody's edit: left off, and says so
        Assert.False(edited.IsIncluded);
        Assert.True(edited.IsEditedOutside);
        Assert.Contains("edited outside the recolour", edited.Note);
        Assert.False(card.ChromaRecolourDirty);                                // listing is not an edit
    }

    [Fact]
    public async Task TheSlidersMoveTheSwatches_AndAPendingParameterRecolourIsSavedWithItsTargets()
    {
        var host = TypicalHost();
        var card = Card(host);
        await Scan(card);
        var tint = card.ChromaParameters.Single(r => r.Key.Name == "TintColor");
        var before = tint.ResultBrush;

        card.ChromaHue = 120;
        Assert.True(card.ChromaRecolourDirty);
        Assert.True(card.HasPendingChromaRecolour);
        Assert.NotEqual(((Avalonia.Media.Immutable.ImmutableSolidColorBrush)before).Color, ((Avalonia.Media.Immutable.ImmutableSolidColorBrush)tint.ResultBrush).Color);
        Assert.Contains("colour parameter(s)", card.ChromaRecolourStatus);
        Assert.Equal(Avalonia.Media.Color.FromRgb(255, 64, 26), ((Avalonia.Media.Immutable.ImmutableSolidColorBrush)tint.OriginalBrush).Color);

        await card.SaveChromaRecolourNowAsync();

        var save = Assert.Single(host.Saves);
        Assert.Equal(SkinBinPath, save.Bin);
        Assert.Equal(120f, save.Transform.HueShiftDegrees);
        Assert.Equal(new[] { "Glow_Color", "OutlineColor", "TintColor" }, save.Targets.Select(t => t.Name).OrderBy(n => n));
        Assert.Empty(save.Stale);
        Assert.False(card.ChromaRecolourDirty);
        Assert.True(card.HasSavedChromaRecolour);
        Assert.StartsWith("Saved: 3 colour parameter(s) written", card.ChromaRecolourStatus);

        // nothing pending: a flush does nothing
        await card.SaveChromaRecolourNowAsync();
        Assert.Single(host.Saves);

        // a switch changes what a save writes: the parameter that left the recipe goes back to Riot's
        card.ChromaParameters.Single(r => r.Key.Name == "Glow_Color").IsIncluded = false;
        Assert.True(card.ChromaRecolourDirty);
        await card.SaveChromaRecolourNowAsync();
        Assert.Equal(2, host.Saves.Count);
        Assert.Equal("Glow_Color", Assert.Single(host.Saves[1].Stale).Name);
        Assert.Equal(2, host.Saves[1].Targets.Count);
    }

    [Fact]
    public async Task ASliderMoveThatOnlyReachesParametersNeverSavesATexture()
    {
        var host = TypicalHost();
        var card = Card(host);
        int textureSaves = 0;
        card.SaveChromaRecolour = (_, _, _, _) => { textureSaves++; return Task.FromResult(new ChromaSaveResult(0, 0, 0, 0, Array.Empty<ulong>(), Array.Empty<ulong>(), Array.Empty<string>())); };
        await Scan(card);
        card.ChromaHue = 60;
        await card.SaveChromaRecolourNowAsync();
        Assert.Equal(0, textureSaves);
        Assert.Single(host.Saves);
    }

    [Fact]
    public async Task NoChangeWithASavedRecipeSavesAsARevertOfTheParameters_AndRevertForgetsTheRecipe()
    {
        var host = TypicalHost();
        var card = Card(host);
        await Scan(card);
        card.ChromaHue = 60;
        await card.SaveChromaRecolourNowAsync();

        card.ResetChromaSlidersCommand.Execute(null);
        Assert.True(card.ChromaRecolourDirty);                                 // a saved recolour the sliders no longer say is pending
        await card.SaveChromaRecolourNowAsync();
        var save = host.Saves[1];
        Assert.True(save.Transform.IsIdentity);
        Assert.Empty(save.Targets);
        Assert.Equal(3, save.Stale.Count);
        Assert.False(card.HasSavedChromaRecolour);

        card.ChromaHue = 30;
        await card.SaveChromaRecolourNowAsync();
        Assert.True(card.RevertChromaRecolourCommand.CanExecute(null));
        await card.RevertChromaRecolourCommand.ExecuteAsync(null);
        var revert = Assert.Single(host.Reverts);
        Assert.Equal(3, revert.Count);
        Assert.False(card.HasSavedChromaRecolour);
        Assert.True(card.ChromaSettings.ToTransform().IsIdentity);
        Assert.False(card.ChromaRecolourDirty);
        Assert.Contains("3 colour parameter(s) put back", card.ChromaRecolourStatus);
    }

    [Fact]
    public async Task ARevertTheBinRefusesKeepsTheSavedRecipe_AndSaysSo_AndWorksOnceItCan()
    {
        var host = TypicalHost();
        var card = Card(host);
        await Scan(card);
        card.ChromaHue = 60;
        await card.SaveChromaRecolourNowAsync();
        var works = card.RevertChromaParameters!;
        card.RevertChromaParameters = (_, _) => throw new InvalidOperationException("the bin cannot be changed on top of the mod's GameData right now");

        await card.RevertChromaRecolourCommand.ExecuteAsync(null);

        Assert.True(card.HasSavedChromaRecolour);                                  // nothing was put back, so nothing is forgotten
        Assert.Contains("were not put back", card.ChromaRecolourStatus);
        Assert.Contains("GameData", card.ChromaRecolourStatus);
        Assert.Equal(60f, card.ChromaSettings.ToTransform().HueShiftDegrees);       // the sliders still say what is saved
        Assert.False(card.ChromaRecolourDirty);

        card.RevertChromaParameters = works;
        await card.RevertChromaRecolourCommand.ExecuteAsync(null);
        Assert.False(card.HasSavedChromaRecolour);
        Assert.Equal(3, Assert.Single(host.Reverts).Count);
    }

    [Fact]
    public async Task ASavedRecipeRestoresTheSlidersAndTheSwitches_AndDoesNotLookPending()
    {
        var host = TypicalHost();
        var transform = new ColorTransform { HueShiftDegrees = 75f, GreyProtection = new GreyProtection(0.08f, 0.06f) };
        var savedRefs = new List<ChromaParameterRef>
        {
            new() { Material = 7, MaterialName = "Characters/Zz/Skins/Skin1/Materials/Body_inst", Name = "TintColor" },
            new() { Material = 7, MaterialName = "Characters/Zz/Skins/Skin1/Materials/Body_inst", Name = "Glow_Color" },
        };
        host.Saved = new ChromaSavedRecipe(transform, Array.Empty<ChromaTarget>(), savedRefs, transform);
        var card = Card(host);

        Assert.True(card.HasSavedChromaRecolour);
        Assert.Equal(transform, card.ChromaSettings.ToTransform());            // the sliders come back
        Assert.False(card.ChromaRecolourDirty);
        await Scan(card);
        Assert.False(card.ChromaRecolourDirty, "a restored recipe must not look pending");
        Assert.Equal(new[] { "Glow_Color", "TintColor" }, card.ChromaParameters.Where(r => r.IsIncluded).Select(r => r.Key.Name).OrderBy(n => n));
        Assert.False(card.ChromaParameters.Single(r => r.Key.Name == "OutlineColor").IsIncluded);   // a recipe exists: only its parameters are on
    }

    [Fact]
    public async Task ARecipeSavedBeforeParametersExistedLoadsUnchanged_NotPendingAndWithTheParametersOff()
    {
        var host = TypicalHost();
        var transform = new ColorTransform { HueShiftDegrees = 75f };
        host.Saved = new ChromaSavedRecipe(transform, new[] { new ChromaTarget(0x1111, "assets/characters/zz/skins/skin1/zz_tx_cm.tex") });   // M824's shape: no parameters
        var card = Card(host);
        card.ReadChromaOriginal = _ => null;
        await Scan(card);
        Assert.False(card.ChromaRecolourDirty);
        Assert.DoesNotContain(card.ChromaParameters, r => r.IsIncluded);        // switching them on is the user's call
    }

    [Fact]
    public async Task ASavedParameterTheListDoesNotShowIsCarried_NeverRevertedByAFlush()
    {
        var host = TypicalHost();
        var transform = new ColorTransform { HueShiftDegrees = 75f };
        host.Saved = new ChromaSavedRecipe(transform, Array.Empty<ChromaTarget>(),
            new List<ChromaParameterRef>
            {
                new() { Material = 7, Name = "TintColor" },
                new() { Material = 99, MaterialName = "A_removed_material", Name = "Gone_Color" },     // the scan does not list it
            }, transform);
        var card = Card(host);
        await Scan(card);
        Assert.False(card.ChromaRecolourDirty, "an unlisted saved parameter made the card look pending");
        await card.SaveChromaRecolourNowAsync();
        Assert.Empty(host.Saves);

        card.ChromaHue = 120;
        await card.SaveChromaRecolourNowAsync();
        var save = Assert.Single(host.Saves);
        Assert.DoesNotContain(save.Stale, p => p.Name == "Gone_Color");         // not put back to Riot's
    }

    [Fact]
    public async Task APreviewIsRiotsValueThroughTheTransform_NeverCompounding_AndTakingAParameterOutDrawsItAsItWas()
    {
        var host = TypicalHost();
        var card = Card(host);
        var pushed = new List<ChromaParamPush>();
        card.PushChromaParamsGl = items => { pushed.Clear(); pushed.AddRange(items); };
        await Scan(card);

        card.ChromaHue = 120;
        await card.ChromaIdleAsync();
        Assert.Equal(new[] { "Glow_Color", "OutlineColor", "TintColor" }, pushed.Select(p => p.Key.Name).OrderBy(n => n));
        var t = card.ChromaSettings.ToTransform();
        foreach (var p in pushed)
        {
            var riot = host.Infos.Single(i => i.Key == p.Key).Riot;
            var expected = t.Apply(riot);
            Assert.Equal(new[] { expected.X, expected.Y, expected.Z, riot.W }, p.Value);   // alpha is the bin's
        }

        // dragging back and forth ends where one move would
        card.ChromaHue = 40; await card.ChromaIdleAsync();
        card.ChromaHue = 120; await card.ChromaIdleAsync();
        var tint = pushed.Single(p => p.Key.Name == "TintColor");
        var tintRiot = host.Infos.Single(i => i.Key.Name == "TintColor").Riot;
        Assert.Equal(t.Apply(tintRiot).X, tint.Value[0]);

        // taking one out of the set draws it as the project holds it
        card.ChromaParameters.Single(r => r.Key.Name == "Glow_Color").IsIncluded = false;
        await card.ChromaIdleAsync();
        var glow = Assert.Single(pushed, p => p.Key.Name == "Glow_Color");
        Assert.Equal(new[] { 1.8f, 0.5f, 0.15f, 0f }, glow.Value);

        // and back to no change: everything that was shown is drawn as it was
        card.ChromaHue = 0;
        await card.ChromaIdleAsync();
        Assert.Equal(new[] { "OutlineColor", "TintColor" }, pushed.Select(p => p.Key.Name).OrderBy(n => n));
        Assert.Equal(new[] { 1f, 0.25f, 0.1f, 1f }, pushed.Single(p => p.Key.Name == "TintColor").Value);
    }

    [Fact]
    public async Task HdrAndNegativeColoursAreHandledByThePreviewTheWayTheSaveHandlesThem()
    {
        var host = new FakeHost
        {
            Infos = new List<ChromaParameterInfo>
            {
                Info("tatto_Color", new Vector4(1.8f, 0.5f, 0.15f, 0f)),
                Info("Dark_Color", new Vector4(-0.15f, -0.15f, -0.05f, 1f), recolourable: false, reason: "A negative or non-finite colour: left exactly as authored."),
            },
        };
        var card = Card(host);
        var pushed = new List<ChromaParamPush>();
        card.PushChromaParamsGl = items => { pushed.Clear(); pushed.AddRange(items); };
        await Scan(card);
        card.ChromaHue = 90;
        await card.ChromaIdleAsync();
        var glow = Assert.Single(pushed);
        Assert.Equal("tatto_Color", glow.Key.Name);
        Assert.Equal(1.8f, glow.Value.Take(3).Max(), 3);                         // intensity kept
        Assert.Equal(0f, glow.Value[3]);
        Assert.False(card.ChromaParameters.Single(r => r.Key.Name == "Dark_Color").CanInclude);
    }

    [Fact]
    public async Task AHostThatCannotReadRiotsBinListsNothingAndSaysWhy()
    {
        var host = new FakeHost { Problem = "Riot's original of this skin bin cannot be read here" };
        var card = Card(host);
        await Scan(card);
        Assert.False(card.HasChromaParameters);
        Assert.True(card.HasChromaParameterProblem);
        Assert.Contains("cannot be read", card.ChromaParameterProblem);
        Assert.False(card.ChromaRecolourDirty);
    }
}

internal static class InventoryTestExtensions
{
    /// <summary>The empty inventory the stand-in scan hands the card.</summary>
    public static SkinColorInventory WithNoWarnings(this SkinColorInventory inventory) =>
        inventory with { Warnings = Array.Empty<string>() };
}
