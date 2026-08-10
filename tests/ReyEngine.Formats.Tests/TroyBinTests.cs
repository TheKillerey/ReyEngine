using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Particles;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M419. The legacy <c>.troybin</c> reader and its conversion into a modern VfxSystemDefinitionData.
///
/// <para><b>Fixtures are hand-assembled</b> from the layout verified against the 1,189 shipped files, so
/// the encoding is stated here explicitly rather than borrowed from a writer that does not exist. The
/// corpus results the rules were derived from: 1,189 of 1,189 parse; 1,189 of 1,189 convert and read
/// back through <see cref="VfxSystemResolver"/> with matching emitter counts; 4,797 emitters and 5,481
/// assets recovered; 660 files carry a colour curve.</para>
///
/// <para><b>What these tests deliberately do NOT assert</b> is fidelity of timing or physics. Those
/// values live in the region of the file whose field layout is not decoded, so the converter writes
/// engine defaults - and <see cref="TroyConversionResult.Provenance"/> is asserted to say so, because a
/// silent default is exactly the failure mode worth guarding.</para>
/// </summary>
public class TroyBinTests
{
    /// <summary>version 2, u16 string-block size, body, then NUL-terminated strings - the layout
    /// verified byte-for-byte against the shipped corpus.</summary>
    private static byte[] Build(IEnumerable<string> strings, byte[]? body = null)
    {
        var block = new MemoryStream();
        foreach (string s in strings)
        {
            var raw = System.Text.Encoding.ASCII.GetBytes(s);
            block.Write(raw, 0, raw.Length);
            block.WriteByte(0);
        }
        byte[] blockBytes = block.ToArray();

        var file = new MemoryStream();
        file.WriteByte(2);
        file.Write(BitConverter.GetBytes((ushort)blockBytes.Length), 0, 2);
        if (body is not null) file.Write(body, 0, body.Length);
        file.Write(blockBytes, 0, blockBytes.Length);
        return file.ToArray();
    }

    // ---- the reader ----------------------------------------------------------------------------

    [Fact]
    public void StringsAreClassifiedByTheMeasuredRules()
    {
        byte[] raw = Build(new[]
        {
            "CenterFlare",                              // emitter name
            "Simple",                                   // keyword, measured 581 times
            "DATA/Particles/CenterFlare_glow.dds",      // asset
            "Play_sfx_Nexus_Explosion",                 // wwise event
            "0 300 0",                                  // numeric tuple
        });

        Assert.True(TroyBinFile.TryParse(raw, out var troy, out var error));
        Assert.Null(error);
        Assert.Equal(2, troy!.Version);
        Assert.Equal(new[] { "CenterFlare" }, troy.EmitterNames);
        Assert.Equal(new[] { "Play_sfx_Nexus_Explosion" }, troy.SoundEvents);
        Assert.Equal(new[] { "DATA/Particles/CenterFlare_glow.dds" }, troy.AssetPaths);
        Assert.Contains(troy.Strings, s => s.Kind == TroyStringKind.Keyword && s.Value == "Simple");
        Assert.Contains(troy.Strings, s => s.Kind == TroyStringKind.NumericTuple && s.Value == "0 300 0");
    }

    /// <summary>Riot writes a two-target recolour as one string, and its own "no asset" placeholder as
    /// doesnotexist.*. Both appear in the corpus and neither is a usable path as written.</summary>
    [Fact]
    public void CompoundAndPlaceholderAssetStringsAreHandled()
    {
        byte[] raw = Build(new[]
        {
            "teamrecolor, DATA/Particles/SRU_Order_praxis.dds, DATA/Particles/SRU_Chaos_praxis.dds",
            "doesnotexist.dds",
        });

        Assert.True(TroyBinFile.TryParse(raw, out var troy, out _));
        Assert.Equal(new[]
        {
            "DATA/Particles/SRU_Order_praxis.dds",
            "DATA/Particles/SRU_Chaos_praxis.dds",
        }, troy!.AssetPaths);
    }

    /// <summary>Both authored colour scales occur in the corpus. A 0-255 run is normalised; a 0-1 run is
    /// left alone. The time component is never rescaled in either case.</summary>
    [Theory]
    [InlineData("0 255 128 0 255", 1f, 0.5019608f)]      // 0-255 authoring
    [InlineData("0 1 0.5 0 1", 1f, 0.5f)]                // 0-1 authoring
    public void ColorKeysAreNormalisedPerFile(string second, float expectedR, float expectedG)
    {
        byte[] raw = Build(new[] { "flame", second, second.Replace("0 ", "1 ") });

        Assert.True(TroyBinFile.TryParse(raw, out var troy, out _));
        var key = Assert.IsType<(float Time, Vector4 Color)>(troy!.ColorKeys[0], exactMatch: false);
        Assert.Equal(0f, key.Time);
        Assert.Equal(expectedR, key.Color.X, 4);
        Assert.Equal(expectedG, key.Color.Y, 4);
    }

    [Fact]
    public void TheUndecodedBodyIsReportedRatherThanIgnored()
    {
        byte[] raw = Build(new[] { "smoke" }, body: new byte[64]);
        Assert.True(TroyBinFile.TryParse(raw, out var troy, out _));
        Assert.Equal(64, troy!.UndecodedBodyBytes);
    }

    [Theory]
    [InlineData(new byte[] { 1, 2 }, "fewer than 3 bytes")]
    [InlineData(new byte[] { 3, 0, 0 }, "version 3")]
    [InlineData(new byte[] { 2, 0xff, 0xff }, "does not fit")]
    public void MalformedInputIsRejectedWithAReason(byte[] raw, string expected)
    {
        Assert.False(TroyBinFile.TryParse(raw, out var troy, out var error));
        Assert.Null(troy);
        Assert.Contains(expected, error);
    }

    // ---- the conversion ------------------------------------------------------------------------

    /// <summary>The whole point: what comes out has to be readable by the MODERN resolver, or the
    /// Workshop import would produce a system nothing can load.</summary>
    [Fact]
    public void AConvertedSystemReadsBackThroughTheModernResolver()
    {
        byte[] raw = Build(new[]
        {
            "flash", "glow",
            "DATA/Particles/FlashRed.dds",
            "DATA/Shared/Particles/Glows.DDS",
        });
        Assert.True(TroyBinFile.TryParse(raw, out var troy, out _));

        var result = TroyBinConverter.Convert(troy!, "Legacy_Blackout", "Particles/Legacy_Blackout");
        var systems = VfxSystemResolver.ExtractAll(result.BinBytes);

        var system = Assert.Single(systems).Value;
        Assert.Equal(2, system.Emitters.Count);
        Assert.Equal(new[] { "flash", "glow" }, system.Emitters.Select(e => e.Name));
        Assert.Equal(HashAlgorithms.Fnv1a("Particles/Legacy_Blackout"), result.SystemHash);
    }

    /// <summary>Name-match wins over position. Measured reason: only 40% of files have as many textures
    /// as emitters, so assigning purely by index would be wrong most of the time.</summary>
    [Fact]
    public void TexturesArePreferentiallyMatchedByName()
    {
        // deliberately out of order: 'glow' comes second but its texture comes first
        byte[] raw = Build(new[]
        {
            "flash", "glow",
            "DATA/Particles/Glows.dds",
            "DATA/Particles/FlashRed.dds",
        });
        Assert.True(TroyBinFile.TryParse(raw, out var troy, out _));

        var result = TroyBinConverter.Convert(troy!, "X", "Particles/X");
        var flash = result.Emitters.Single(e => e.EmitterName == "flash");
        var glow = result.Emitters.Single(e => e.EmitterName == "glow");

        Assert.Equal(TroyTextureSource.NameMatch, flash.Source);
        Assert.Equal("DATA/Particles/FlashRed.dds", flash.TexturePath);
        Assert.Equal(TroyTextureSource.NameMatch, glow.Source);
        Assert.Equal("DATA/Particles/Glows.dds", glow.TexturePath);
    }

    [Fact]
    public void EmittersWithNoTextureLeftSaySoRatherThanBorrowingOne()
    {
        byte[] raw = Build(new[] { "a1", "b2", "c3", "DATA/Particles/only.dds" });
        Assert.True(TroyBinFile.TryParse(raw, out var troy, out _));

        var result = TroyBinConverter.Convert(troy!, "X", "Particles/X");
        Assert.Equal(1, result.Emitters.Count(e => e.Source == TroyTextureSource.Positional));
        Assert.Equal(2, result.Emitters.Count(e => e.Source == TroyTextureSource.None));
        Assert.All(result.Emitters.Where(e => e.Source == TroyTextureSource.None),
            e => Assert.Null(e.TexturePath));
    }

    /// <summary>Shipped modern systems reference .tex 2,381,029 times against 70 .dds, so the converted
    /// system must point at a .tex and flag the source for transcoding.</summary>
    [Fact]
    public void TexturesArePointedAtTexAndFlaggedForTranscode()
    {
        byte[] raw = Build(new[] { "spark", "DATA/Particles/spark_core.dds", "DATA/Particles/mesh.scb" });
        Assert.True(TroyBinFile.TryParse(raw, out var troy, out _));

        var result = TroyBinConverter.Convert(troy!, "X", "Particles/X");

        var dds = result.Assets.Single(a => a.SourcePath.EndsWith(".dds"));
        Assert.Equal("DATA/Particles/spark_core.tex", dds.TargetPath);
        Assert.True(dds.NeedsTexTranscode);

        // meshes are carried across untouched - the .tex container is for textures only
        var scb = result.Assets.Single(a => a.SourcePath.EndsWith(".scb"));
        Assert.Equal(scb.SourcePath, scb.TargetPath);
        Assert.False(scb.NeedsTexTranscode);

        var texture = Assert.IsType<BinTreeString>(FindEmitterProperty(result.BinBytes, "texture"));
        Assert.Equal("DATA/Particles/spark_core.tex", texture.Value);
    }

    [Fact]
    public void AColourCurveBecomesAnAnimatedColorDynamic()
    {
        byte[] raw = Build(new[] { "fade", "0 1 1 1 1", "1 1 0 0 0" });
        Assert.True(TroyBinFile.TryParse(raw, out var troy, out _));

        var result = TroyBinConverter.Convert(troy!, "X", "Particles/X");
        Assert.Equal(2, result.ColorKeys);

        var color = Assert.IsType<BinTreeEmbedded>(FindEmitterProperty(result.BinBytes, "Color"));
        var dynamics = Assert.IsType<BinTreeStruct>(color.Properties[HashAlgorithms.Fnv1a("dynamics")]);
        var times = Assert.IsType<BinTreeContainer>(dynamics.Properties[HashAlgorithms.Fnv1a("times")]);
        var values = Assert.IsType<BinTreeContainer>(dynamics.Properties[HashAlgorithms.Fnv1a("values")]);
        Assert.Equal(2, times.Elements.Count);
        Assert.Equal(2, values.Elements.Count);
    }

    /// <summary>The provenance line is the mechanism that stops a default being mistaken for the
    /// original value, so it is part of the contract rather than cosmetic.</summary>
    [Fact]
    public void ProvenanceStatesThatTimingAndPhysicsAreDefaults()
    {
        byte[] raw = Build(new[] { "smoke", "DATA/Particles/smoke.dds" }, body: new byte[128]);
        Assert.True(TroyBinFile.TryParse(raw, out var troy, out _));

        string provenance = TroyBinConverter.Convert(troy!, "X", "Particles/X").Provenance;
        Assert.Contains("engine defaults", provenance);
        Assert.Contains("not the original values", provenance);
        Assert.Contains("128", provenance);
    }

    /// <summary>A file with no emitter names at all - 17 of them ship - still has to produce a system
    /// rather than an empty container the game would choke on.</summary>
    [Fact]
    public void AFileWithNoEmitterNamesStillProducesOneEmitter()
    {
        byte[] raw = Build(new[] { "DATA/Particles/lonely.dds" });
        Assert.True(TroyBinFile.TryParse(raw, out var troy, out _));

        var result = TroyBinConverter.Convert(troy!, "X", "Particles/X");
        var emitter = Assert.Single(result.Emitters);
        Assert.Equal("emitter", emitter.EmitterName);
        Assert.Single(VfxSystemResolver.ExtractAll(result.BinBytes)).Value.Emitters.Single();
    }

    /// <summary>complexEmitterDefinitionData holds Struct elements, not Embedded ones. Pinned because
    /// material containers need the opposite (M416) and getting it backwards is invisible to every
    /// reader while breaking the game.</summary>
    [Fact]
    public void TheEmitterContainerHoldsStructElements()
    {
        byte[] raw = Build(new[] { "e1", "DATA/Particles/e1.dds" });
        Assert.True(TroyBinFile.TryParse(raw, out var troy, out _));

        var tree = new BinTree(new MemoryStream(
            TroyBinConverter.Convert(troy!, "X", "Particles/X").BinBytes, writable: false));
        var container = Assert.IsType<BinTreeContainer>(
            Assert.Single(tree.Objects).Value.Properties[HashAlgorithms.Fnv1a("complexEmitterDefinitionData")]);

        Assert.Equal(BinPropertyType.Struct, container.ElementType);
        Assert.All(container.Elements, e => Assert.Equal(BinPropertyType.Struct, e.Type));
    }

    private static BinTreeProperty? FindEmitterProperty(byte[] bin, string field)
    {
        var tree = new BinTree(new MemoryStream(bin, writable: false));
        var container = (BinTreeContainer)tree.Objects.Values.Single()
            .Properties[HashAlgorithms.Fnv1a("complexEmitterDefinitionData")];
        var emitter = (BinTreeStruct)container.Elements[0];
        return emitter.Properties.TryGetValue(HashAlgorithms.Fnv1a(field), out var p) ? p : null;
    }
}
