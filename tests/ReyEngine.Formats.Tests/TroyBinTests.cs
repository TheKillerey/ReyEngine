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
        Assert.Equal("ASSETS/Legacy/Particles/spark_core.tex", dds.TargetPath);
        Assert.True(dds.NeedsTexTranscode);

        // meshes move root with everything else but keep their format - the .tex container is for
        // textures only
        var scb = result.Assets.Single(a => a.SourcePath.EndsWith(".scb"));
        Assert.Equal("ASSETS/Legacy/Particles/mesh.scb", scb.TargetPath);
        Assert.False(scb.NeedsTexTranscode);

        var texture = Assert.IsType<BinTreeString>(FindEmitterProperty(result.BinBytes, "texture"));
        Assert.Equal("ASSETS/Legacy/Particles/spark_core.tex", texture.Value);
    }

    /// <summary>
    /// The first in-game test rendered a white quad. Cause: the converted system kept the authored
    /// <c>DATA/</c> root, and every one of the 2,381,099 texture references in shipped particle systems
    /// sits under <c>ASSETS/</c> - so the loader never resolved it and fell back to a default texture.
    /// The <c>Legacy/</c> segment is not decoration: a plain DATA -> ASSETS swap collides with 444
    /// shipped assets, and staging over those would replace them for the whole game.
    /// </summary>
    [Theory]
    [InlineData("DATA/Particles/FlashRed.dds", "ASSETS/Legacy/Particles/FlashRed.tex")]
    [InlineData("DATA/Shared/Particles/Black.DDS", "ASSETS/Legacy/Shared/Particles/Black.tex")]
    [InlineData("DATA/Particles/CrystalShield2.scb", "ASSETS/Legacy/Particles/CrystalShield2.scb")]
    public void AssetsAreRootedUnderAssetsLegacy(string legacy, string expected)
        => Assert.Equal(expected, TroyBinConverter.ToTargetPath(legacy));

    /// <summary>
    /// The legacy pixel shader multiplies two samplers - <c>TEXTURE * PARTICLE_COLOR_TEXTURE</c> - and
    /// Riot names the ramp with a <c>color-</c> prefix (695 of 4,822 references). Feeding a ramp to the
    /// modern <c>texture</c> field is the other half of the white-quad bug.
    /// </summary>
    [Fact]
    public void AColourRampGoesToParticleColorTextureNotTheDiffuse()
    {
        byte[] raw = Build(new[]
        {
            "crystal",
            "DATA/Particles/quartz32.DDS",
            "DATA/Particles/color-crystal32.DDS",
        });
        Assert.True(TroyBinFile.TryParse(raw, out var troy, out _));
        Assert.Equal(new[] { "DATA/Particles/quartz32.DDS" }, troy!.TexturePaths);
        Assert.Equal(new[] { "DATA/Particles/color-crystal32.DDS" }, troy.ColorRampPaths);

        var result = TroyBinConverter.Convert(troy, "X", "Particles/X");
        Assert.Equal("ASSETS/Legacy/Particles/quartz32.tex",
            Assert.IsType<BinTreeString>(FindEmitterProperty(result.BinBytes, "texture")).Value);
        Assert.Equal("ASSETS/Legacy/Particles/color-crystal32.tex",
            Assert.IsType<BinTreeString>(FindEmitterProperty(result.BinBytes, "particleColorTexture")).Value);

        // the ramp is still staged, or the reference would dangle
        Assert.Contains(result.Assets, a => a.SourcePath.Contains("color-crystal32"));
    }

    /// <summary>Which emitter a ramp belonged to lives in the undecoded body, so a file with several
    /// ramps gets none rather than an invented assignment.</summary>
    [Fact]
    public void SeveralRampsMeansNoRampIsAssigned()
    {
        byte[] raw = Build(new[]
        {
            "a1", "DATA/Particles/main.dds",
            "DATA/Particles/color-one.dds", "DATA/Particles/color-two.dds",
        });
        Assert.True(TroyBinFile.TryParse(raw, out var troy, out _));

        var result = TroyBinConverter.Convert(troy!, "X", "Particles/X");
        Assert.Null(FindEmitterProperty(result.BinBytes, "particleColorTexture"));
        // both are still staged so nothing is lost
        Assert.Equal(2, result.Assets.Count(a => a.SourcePath.Contains("color-")));
    }

    /// <summary>Written explicitly rather than left to the game's default for an absent field. M117
    /// established 1/3/4/5 as the additive family, and legacy sprites are additive glows on black.</summary>
    [Fact]
    public void BlendModeIsWrittenExplicitlyAsAdditive()
    {
        byte[] raw = Build(new[] { "glow", "DATA/Particles/glow.dds" });
        Assert.True(TroyBinFile.TryParse(raw, out var troy, out _));

        var result = TroyBinConverter.Convert(troy!, "X", "Particles/X");
        Assert.Equal(1, Assert.IsType<BinTreeU8>(FindEmitterProperty(result.BinBytes, "blendMode")).Value);
        Assert.Equal(1, VfxSystemResolver.ExtractAll(result.BinBytes).Values.Single().Emitters[0].BlendMode);
    }

    // ---- mesh particles ------------------------------------------------------------------------

    /// <summary>A mesh emitter must carry VfxPrimitiveMesh, not the billboard quad - the first in-game
    /// test showed mesh effects rendering as flat sprites. Shape copied from a shipped system:
    /// primitive = Struct VfxPrimitiveMesh { mMesh = EMBEDDED VfxMeshDefinitionData { mSimpleMeshName } }.</summary>
    [Fact]
    public void ASingleEmitterOwnsTheFilesMeshAndRendersItAsGeometry()
    {
        byte[] raw = Build(new[] { "shield", "DATA/Particles/CrystalShield2.scb", "DATA/Particles/quartz32.dds" });
        Assert.True(TroyBinFile.TryParse(raw, out var troy, out _));

        var result = TroyBinConverter.Convert(troy!, "X", "Particles/X");
        Assert.Equal("DATA/Particles/CrystalShield2.scb", Assert.Single(result.Emitters).MeshPath);
        Assert.Empty(result.UnboundMeshes);

        var prim = Assert.IsType<BinTreeStruct>(FindEmitterProperty(result.BinBytes, "primitive"));
        Assert.Equal(HashAlgorithms.Fnv1a("VfxPrimitiveMesh"), prim.ClassHash);
        var mesh = Assert.IsType<BinTreeEmbedded>(prim.Properties[HashAlgorithms.Fnv1a("mMesh")]);
        Assert.Equal("ASSETS/Legacy/Particles/CrystalShield2.scb",
            Assert.IsType<BinTreeString>(mesh.Properties[HashAlgorithms.Fnv1a("mSimpleMeshName")]).Value);

        // and the modern resolver agrees it is a mesh particle
        var emitter = VfxSystemResolver.ExtractAll(result.BinBytes).Values.Single().Emitters.Single();
        Assert.Equal("ASSETS/Legacy/Particles/CrystalShield2.scb", emitter.MeshPath);
    }

    [Fact]
    public void AMeshWhoseFilenameCarriesTheEmitterNameBindsToThatEmitter()
    {
        byte[] raw = Build(new[]
        {
            "GroundBurst", "BeamMesh", "Embers",
            "DATA/Particles/Global_SS_Smite_AoEStun_BeamMesh.scb",
        });
        Assert.True(TroyBinFile.TryParse(raw, out var troy, out _));

        var result = TroyBinConverter.Convert(troy!, "X", "Particles/X");
        Assert.Equal("DATA/Particles/Global_SS_Smite_AoEStun_BeamMesh.scb",
            result.Emitters.Single(e => e.EmitterName == "BeamMesh").MeshPath);
        Assert.All(result.Emitters.Where(e => e.EmitterName != "BeamMesh"), e => Assert.Null(e.MeshPath));
    }

    /// <summary>The common corpus shape is one mesh and several emitters (42 files have 4 emitters and
    /// 1 mesh). Which one owned it lives in the undecoded body, and binding the wrong emitter costs two
    /// errors, so an unmatchable mesh is reported instead of assigned.</summary>
    [Fact]
    public void AnUnmatchableMeshIsReportedRatherThanBoundToAGuess()
    {
        byte[] raw = Build(new[] { "aa1", "bb2", "cc3", "DATA/Particles/Something_Else.scb" });
        Assert.True(TroyBinFile.TryParse(raw, out var troy, out _));

        var result = TroyBinConverter.Convert(troy!, "X", "Particles/X");
        Assert.All(result.Emitters, e => Assert.Null(e.MeshPath));
        Assert.Equal("DATA/Particles/Something_Else.scb", Assert.Single(result.UnboundMeshes));
        Assert.Contains("could not be matched", result.Provenance);
        // still staged, so the user can bind it by hand
        Assert.Contains(result.Assets, a => a.SourcePath.EndsWith("Something_Else.scb"));
    }

    /// <summary>A skinned mesh names mMeshName + mMeshSkeletonName instead of mSimpleMeshName, and the
    /// .skl has to be staged or the primitive dangles. 58 corpus files are skinned.</summary>
    [Fact]
    public void ASkinnedMeshCarriesItsSkeleton()
    {
        byte[] raw = Build(new[]
        {
            "healthIcon",
            "DATA/Particles/HA_AP_healingIcon.skn",
            "DATA/Particles/HA_AP_healingIcon.skl",
        });
        Assert.True(TroyBinFile.TryParse(raw, out var troy, out _));

        var result = TroyBinConverter.Convert(troy!, "X", "Particles/X");
        var prim = Assert.IsType<BinTreeStruct>(FindEmitterProperty(result.BinBytes, "primitive"));
        var mesh = Assert.IsType<BinTreeEmbedded>(prim.Properties[HashAlgorithms.Fnv1a("mMesh")]);

        Assert.Equal("ASSETS/Legacy/Particles/HA_AP_healingIcon.skn",
            Assert.IsType<BinTreeString>(mesh.Properties[HashAlgorithms.Fnv1a("mMeshName")]).Value);
        Assert.Equal("ASSETS/Legacy/Particles/HA_AP_healingIcon.skl",
            Assert.IsType<BinTreeString>(mesh.Properties[HashAlgorithms.Fnv1a("mMeshSkeletonName")]).Value);
        Assert.Contains(result.Assets, a => a.SourcePath.EndsWith(".skl"));
    }

    /// <summary>mesh/trail/beam sit in the same positional role as Simple. Left in the name bucket they
    /// became phantom emitters - "mesh" appears in 7 files and all 7 reference a mesh asset.</summary>
    [Theory]
    [InlineData("mesh")]
    [InlineData("trail")]
    [InlineData("beam")]
    public void PrimitiveTypeKeywordsAreNotEmitterNames(string keyword)
    {
        byte[] raw = Build(new[] { "healthIcon", keyword, "Simple", "DATA/Particles/x.dds" });
        Assert.True(TroyBinFile.TryParse(raw, out var troy, out _));
        Assert.Equal(new[] { "healthIcon" }, troy!.EmitterNames);
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

    /// <summary>
    /// Every asset path written into the system must also appear in <c>Assets</c>. This is the
    /// invariant both the import and the M420 Workshop preview stand on: the import stages exactly the
    /// Assets list, and the preview resolves a converted system by aliasing those target paths back to
    /// their originals. A path referenced but not staged is a texture that is missing in game AND
    /// missing in the preview - the same white-quad failure, from the other direction.
    /// </summary>
    [Fact]
    public void EveryPathTheSystemReferencesIsAlsoStaged()
    {
        byte[] raw = Build(new[]
        {
            "crystal", "shield",
            "DATA/Particles/quartz32.DDS",
            "DATA/Particles/color-crystal32.DDS",
            "DATA/Particles/CrystalShield2.scb",
            "DATA/Particles/HA_AP_healingIcon.skn",
            "DATA/Particles/HA_AP_healingIcon.skl",
        });
        Assert.True(TroyBinFile.TryParse(raw, out var troy, out _));

        var result = TroyBinConverter.Convert(troy!, "X", "Particles/X");
        var staged = result.Assets.Select(a => a.TargetPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var tree = new BinTree(new MemoryStream(result.BinBytes, writable: false));
        foreach (string referenced in AssetStrings(tree.Objects.Values.Single().Properties.Values))
            Assert.Contains(referenced, staged);
    }

    /// <summary>Every string anywhere in the object that looks like an asset path.</summary>
    private static IEnumerable<string> AssetStrings(IEnumerable<BinTreeProperty> props)
    {
        foreach (var p in props)
        {
            switch (p)
            {
                case BinTreeString s when s.Value.Contains('/') && Path.HasExtension(s.Value):
                    yield return s.Value; break;
                case BinTreeContainer c:
                    foreach (var v in AssetStrings(c.Elements)) yield return v; break;
                case BinTreeStruct st:
                    foreach (var v in AssetStrings(st.Properties.Values)) yield return v; break;
            }
        }
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
