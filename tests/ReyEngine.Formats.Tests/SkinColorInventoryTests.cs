using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M812: the Chroma Studio's read-only colour inventory, on synthetic bins - the rules, one at a time.
///
/// <para>These run anywhere. <see cref="SkinColorInventoryRealDataTests"/> holds the same promises against Riot's own
/// files, which is the only place a rule like "a mask is never listed" can be shown to hold on data nobody here wrote.</para>
/// </summary>
public sealed class SkinColorInventoryTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    // ===================================================================== builders

    /// <summary>The names the app's dictionary knows - enough that MaterialDocument classifies a synthetic skin the way it
    /// classifies a shipped one.</summary>
    private static readonly Dictionary<uint, string> Known = new[]
    {
        "StaticMaterialDef", "skeleton", "simpleSkin", "materialOverride", "material", "texture", "glossTexture",
        "reflectionMap", "emissiveTexture", "selfIllumination", "reflectionFresnelColor", "fresnelColor",
    }.ToDictionary(H);
    private static string? Resolve(uint h) => Known.TryGetValue(h, out var n) ? n : null;

    private sealed class Files
    {
        public Dictionary<string, byte[]> Bins { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Paths whose read THROWS - a file that is there and cannot be read, which is not the same as a file that is not there.</summary>
        public HashSet<string> Faulty { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Called with the path before every read: a test can hold a scan in the middle of its reading, or cancel it there.</summary>
        public Action<string>? OnRead { get; set; }
        public int Reads { get; private set; }
        public byte[]? Read(string path)
        {
            Reads++;
            OnRead?.Invoke(path);
            if (Faulty.Contains(path)) throw new IOException("the disk said no: " + path);
            return Bins.GetValueOrDefault(path);
        }
        public Files Add(string path, params BinTreeObject[] objects) => Add(path, Array.Empty<string>(), objects);
        public Files Add(string path, string[] dependencies, params BinTreeObject[] objects)
        {
            using var ms = new MemoryStream();
            new BinTree(objects, dependencies).Write(ms);
            Bins[path] = ms.ToArray();
            return this;
        }
    }

    private static BinTreeEmbedded ValueColor(string field, Vector4? constant, params (float Time, Vector4 Value)[] keys)
    {
        var props = new List<BinTreeProperty>();
        if (constant is { } c) props.Add(new BinTreeVector4(H("constantValue"), c));
        if (keys.Length > 0)
            props.Add(new BinTreeEmbedded(H("dynamics"), H("VfxAnimatedColorVariableData"), new BinTreeProperty[]
            {
                new BinTreeContainer(H("times"), BinPropertyType.F32, keys.Select(k => (BinTreeProperty)new BinTreeF32(0, k.Time))),
                new BinTreeContainer(H("values"), BinPropertyType.Vector4, keys.Select(k => (BinTreeProperty)new BinTreeVector4(0, k.Value))),
            }));
        return new BinTreeEmbedded(H(field), H("ValueColor"), props);
    }

    private static BinTreeEmbedded Struct(string field, string cls, params BinTreeProperty[] props) => new(H(field), H(cls), props);
    private static BinTreeString Str(string field, string value) => new(H(field), value);

    private static BinTreeEmbedded Emitter(string name, params BinTreeProperty[] props) =>
        new(0, H("VfxEmitterDefinitionData"), new BinTreeProperty[] { Str("emitterName", name) }.Concat(props));

    private static BinTreeEmbedded Children(params (string? Key, BinTreeObject? Link)[] children) =>
        Struct("childParticleSetDefinition", "VfxChildParticleSetDefinitionData",
            new BinTreeContainer(H("childrenIdentifiers"), BinPropertyType.Embedded, children.Select(c =>
            {
                var props = new List<BinTreeProperty>();
                if (c.Key is { } key) props.Add(new BinTreeHash(H("effectKey"), H(key)));
                if (c.Link is { } link) props.Add(new BinTreeObjectLink(H("effect"), link.PathHash));
                return (BinTreeProperty)new BinTreeEmbedded(0, H("VfxChildIdentifier"), props);
            })));

    private static BinTreeObject Fx(string objectPath, string particleName, params BinTreeEmbedded[] emitters) =>
        new(H(objectPath), H("VfxSystemDefinitionData"), new BinTreeProperty[]
        {
            Str("particleName", particleName),
            Str("particlePath", "Particles/" + particleName),
            new BinTreeContainer(H("complexEmitterDefinitionData"), BinPropertyType.Embedded, emitters),
        });

    private static BinTreeMap ResourceMap(params (string Key, BinTreeObject Target)[] entries) =>
        new(H("resourceMap"), BinPropertyType.Hash, BinPropertyType.ObjectLink, entries.Select(e =>
            new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, H(e.Key)), new BinTreeObjectLink(0, e.Target.PathHash))));

    private static BinTreeObject Resolver(string path, params (string Key, BinTreeObject Target)[] entries) =>
        new(H(path), H("ResourceResolver"), new BinTreeProperty[] { ResourceMap(entries) });

    private static BinTreeObject GearUpgrade(string path, params (string Key, BinTreeObject Target)[] entries) =>
        new(H(path), H("GearSkinUpgrade"), new BinTreeProperty[]
        {
            Struct("mGearData", "GearSkinUpgradeData", Struct("mVFXResourceResolver", "ResourceResolver", ResourceMap(entries))),
        });

    private static BinTreeEmbedded Sampler(string name, string path) =>
        new(0, 0x0904b150, new BinTreeProperty[] { Str("TextureName", name), Str("texturePath", path) });

    private static BinTreeEmbedded Param(string name, BinTreeProperty? value) =>
        new(0, H("StaticMaterialShaderParamDef"), value is null
            ? new BinTreeProperty[] { Str("name", name) }
            : new BinTreeProperty[] { Str("name", name), value });

    private static BinTreeProperty V4(float x, float y, float z, float w) => new BinTreeVector4(H("value"), new Vector4(x, y, z, w));

    private static BinTreeObject Material(string path, (string Name, string Path)[] samplers, params BinTreeEmbedded[] parameters) =>
        new(H(path), H("StaticMaterialDef"), new BinTreeProperty[]
        {
            Str("name", path),
            new BinTreeUnorderedContainer(H("samplerValues"), BinPropertyType.Embedded, samplers.Select(s => (BinTreeProperty)Sampler(s.Name, s.Path))),
            new BinTreeUnorderedContainer(H("paramValues"), BinPropertyType.Embedded, parameters),
        });

    /// <summary>A skin object. <paramref name="resolver"/> null authors no <c>mResourceResolver</c> at all.</summary>
    private static BinTreeObject Skin(string path, BinTreeObject? resolver, BinTreeEmbedded? skinMesh = null) =>
        new(H(path), H("SkinCharacterDataProperties"), new BinTreeProperty?[]
        {
            Str("championSkinName", "Test"),
            skinMesh ?? SkinMesh("assets/characters/test/skins/base/test_tx_cm.tex"),
            resolver is null ? null : new BinTreeObjectLink(H("mResourceResolver"), resolver.PathHash),
        }.Where(p => p is not null).Select(p => p!));

    private static BinTreeEmbedded SkinMesh(string texture, params BinTreeProperty[] more) =>
        Struct("skinMeshProperties", "SkinMeshDataProperties",
            new BinTreeProperty[] { Str("simpleSkin", "assets/characters/test/skins/base/test.skn"), Str("skeleton", "assets/characters/test/skins/base/test.skl"), Str("texture", texture) }
                .Concat(more).ToArray());

    private static string SkinPath(int number) => $"data/characters/test/skins/skin{number}.bin";

    private static SkinColorInventory Scan(Files files, int skin = 0, SkinColorScanner? scanner = null, bool sharing = true,
        IReadOnlyList<int>? numbers = null, Func<int, string?>? names = null, IReadOnlyList<int>? expected = null,
        Func<ulong, string?>? wadPath = null, Func<uint, string?>? name = null) =>
        (scanner ?? new SkinColorScanner()).Scan(new SkinColorRequest(SkinPath(skin), files.Read, name ?? Resolve, wadPath, numbers, names, sharing, expected));

    // ===================================================================== the reader: which fields, and only those

    [Fact]
    public void TheReaderListsExactlyTheColourFieldsAndTheColourTextures_AndNeverAMask()
    {
        var kitchenSink = Emitter("everything",
            ValueColor("birthColor", new Vector4(1, 0.5f, 0.25f, 1)),
            ValueColor("color", new Vector4(1, 1, 1, 1), (0f, new Vector4(1, 1, 1, 0)), (1f, new Vector4(1, 1, 1, 1))),
            Struct("Linger", "VfxLingerDefinitionData", ValueColor("SeparateLingerColor", new Vector4(0, 0, 1, 1))),
            Struct("reflectionDefinition", "VfxReflectionDefinitionData",
                new BinTreeVector4(H("fresnelColor"), new Vector4(0.5f, 0.5f, 0, 1)),
                ValueColor("reflectionFresnelColor", new Vector4(1, 0, 1, 1)),
                Str("reflectionMapTexture", "assets/test/cube.dds")),
            Str("texture", "assets/test/sprite.tex"),
            Struct("textureMult", "VfxTextureMultDefinitionData", Str("textureMult", "assets/test/mult.tex")),
            Str("particleColorTexture", "assets/test/ramp.tex"),
            Struct("paletteDefinition", "VfxPaletteDefinitionData",
                Str("paletteTexture", "assets/test/palette.tex"),
                ValueColor("palleteSrcMixColor", new Vector4(1, 0, 0, 0))),                       // a channel mixer typed as a colour
            Struct("alphaErosionDefinition", "VfxAlphaErosionDefinitionData",
                Str("erosionMapName", "assets/test/erosion_mask.tex"),
                ValueColor("erosionMapChannelMixer", new Vector4(1, 0, 0, 0))),                    // the same, for erosion
            Struct("distortionDefinition", "VfxDistortionDefinitionData", Str("normalMapTexture", "assets/test/distort_normal.tex")),
            Str("falloffTexture", "assets/test/falloff.tex"),
            Str("glossTexture", "assets/test/gloss.tex"),
            Str("transitionTexture", "assets/test/transition.tex"),
            new BinTreeVector4(H("modulationFactor"), new Vector4(9, 9, 9, 9)));                   // a colour-typed field nobody listed

        var tree = new BinTree(new[] { Fx("Sys", "Sys", kitchenSink) }, Array.Empty<string>());
        var system = VfxColorReader.Read(tree.Objects.Values.Single());
        var emitter = Assert.Single(system.Emitters);

        Assert.Equal(VfxColorReader.ColorFields, emitter.Colors.Select(c => c.Field));
        Assert.Equal(VfxColorReader.TextureFields, emitter.Textures.Select(t => t.Field));
        Assert.Equal(
            new[] { "assets/test/sprite.tex", "assets/test/mult.tex", "assets/test/ramp.tex", "assets/test/palette.tex", "assets/test/cube.dds" },
            emitter.Textures.Select(t => t.Path));
        Assert.Equal(new[] { false, false, false, false, true }, emitter.Textures.Select(t => t.IsCubemap));

        // none of the masks reached the listing...
        foreach (var mask in new[] { "erosion_mask", "distort_normal", "falloff", "gloss", "transition" })
            Assert.DoesNotContain(emitter.Textures, t => t.Path.Contains(mask));
        Assert.DoesNotContain(emitter.Colors, c => c.Field.Contains("palleteSrcMixColor") || c.Field.Contains("erosionMapChannelMixer")
                                                   || c.Field.Contains("modulationFactor"));
        // ...yet the sharing set sees every one of them, because editing a file changes it in every role
        foreach (var path in new[] { "erosion_mask", "distort_normal", "falloff", "gloss", "transition", "sprite", "cube" })
            Assert.Contains(BinTexturePath.HashOfReference($"assets/test/{path}" + (path == "cube" ? ".dds" : ".tex")), system.AllTextureHashes);
    }

    [Fact]
    public void AFieldTheBinDoesNotAuthorIsNotListed()
    {
        var onlyBirth = Emitter("a", ValueColor("birthColor", new Vector4(1, 0, 0, 1)));
        var onlyTexture = Emitter("b", Str("texture", "assets/test/s.tex"));
        var emptyReflection = Emitter("c", Struct("reflectionDefinition", "VfxReflectionDefinitionData", new BinTreeF32(H("fresnel"), 2f)));
        var nothing = Emitter("d");
        var system = VfxColorReader.Read(new BinTree(new[] { Fx("Sys", "Sys", onlyBirth, onlyTexture, emptyReflection, nothing) }, Array.Empty<string>()).Objects.Values.Single());

        Assert.Equal(new[] { "birthColor" }, system.Emitters[0].Colors.Select(c => c.Field));
        Assert.Empty(system.Emitters[0].Textures);
        Assert.Empty(system.Emitters[1].Colors);                    // no birthColor is invented for it: the resolver's white is a playback default
        Assert.Single(system.Emitters[1].Textures);
        Assert.Empty(system.Emitters[2].Colors);                    // the fresnel exponent is a number, not a colour
        Assert.Empty(system.Emitters[2].Textures);
        Assert.Empty(system.Emitters[3].Colors);
        Assert.Equal(4, system.Emitters.Count);
        Assert.Equal(new[] { 0, 1, 2, 3 }, system.Emitters.Select(e => e.Index));
    }

    [Fact]
    public void AColourIsAConstantOrACurveWithItsKeyCount_AndHdrComponentsSurvive()
    {
        var emitter = Emitter("e",
            ValueColor("birthColor", new Vector4(4f, 2.5f, 0.5f, 1)),                                                   // above 1.0: HDR
            ValueColor("color", null, (0f, new Vector4(1, 0, 0, 0)), (0.5f, new Vector4(0, 1, 0, 1)), (1f, new Vector4(0, 0, 1, 0))), // a curve with no constantValue
            Struct("Linger", "VfxLingerDefinitionData", ValueColor("SeparateLingerColor", new Vector4(1, 1, 1, 1), (0f, new Vector4(1, 1, 1, 1)))),
            Struct("reflectionDefinition", "VfxReflectionDefinitionData", new BinTreeVector4(H("fresnelColor"), new Vector4(0.1f, 0.2f, 0.3f, 1))),
            Struct("birthColor2", "ValueColor"));
        var colors = VfxColorReader.Read(new BinTree(new[] { Fx("Sys", "Sys", emitter) }, Array.Empty<string>()).Objects.Values.Single())
            .Emitters.Single().Colors.ToDictionary(c => c.Field);

        var birth = colors["birthColor"];
        Assert.False(birth.IsCurve);
        Assert.Equal(0, birth.KeyCount);
        Assert.Equal(new Vector4(4f, 2.5f, 0.5f, 1), birth.Constant);

        var curve = colors["color"];
        Assert.True(curve.IsCurve);
        Assert.Equal(3, curve.KeyCount);
        Assert.Null(curve.Constant);                                                                                    // never invented
        Assert.Equal(new Vector4(1, 0, 0, 0), curve.First);
        Assert.Equal(new Vector4(0, 0, 1, 0), curve.Last);

        var linger = colors["Linger.SeparateLingerColor"];                                                              // both: the curve is what plays
        Assert.True(linger.IsCurve);
        Assert.Equal(1, linger.KeyCount);
        Assert.NotNull(linger.Constant);

        var fresnel = colors["reflectionDefinition.fresnelColor"];                                                      // a bare Vector4
        Assert.False(fresnel.IsCurve);
        Assert.Equal(new Vector4(0.1f, 0.2f, 0.3f, 1), fresnel.Constant);
        Assert.DoesNotContain("birthColor2", colors.Keys);
    }

    [Fact]
    public void AnEmptyValueColorIsListedAsTheDefaultItIs()
    {
        var emitter = Emitter("e", Struct("birthColor", "ValueColor"));
        var value = Assert.Single(VfxColorReader.Read(new BinTree(new[] { Fx("Sys", "Sys", emitter) }, Array.Empty<string>()).Objects.Values.Single())
            .Emitters.Single().Colors);
        Assert.Null(value.Constant);
        Assert.False(value.IsCurve);
        Assert.Contains("default", SkinColorInventory.Describe(value));
    }

    // ===================================================================== which systems

    [Fact]
    public void OnlyTheSystemsTheSkinUsesAreListed_NotThoseThatMerelySitInASharedBin()
    {
        var used = Fx("Used", "Used_Q", Emitter("e", Str("texture", "assets/test/used.tex")));
        var foreign = Fx("Foreign", "Other_Skin_Q", Emitter("e", Str("texture", "assets/test/foreign.tex")));
        var resolver = Resolver("Res", ("Test_Q", used));
        var files = new Files()
            .Add(SkinPath(0), new[] { "DATA/Characters/Test/Test_Multi_Skins_Skin0_Skins_Skin1.bin" }, Skin("Skin0", resolver), resolver)
            .Add("DATA/Characters/Test/Test_Multi_Skins_Skin0_Skins_Skin1.bin", used, foreign);

        var inventory = Scan(files, sharing: false);

        Assert.Equal(new[] { "Used_Q" }, inventory.Effects.Select(e => e.Name));
        Assert.Equal(new[] { H("Test_Q") }, inventory.Effects[0].EffectKeys);
        Assert.Contains(foreign.PathHash, inventory.Reach.ClosureSystems);                 // the closure holds it...
        Assert.Contains(used.PathHash, inventory.Reach.ClosureSystems);
        Assert.True(inventory.Reach.ClosureSystems.Count > inventory.Effects.Count);       // ...and the list does not
        Assert.All(inventory.Effects, e => Assert.Contains(e.PathHash, inventory.Reach.ClosureSystems));
        Assert.DoesNotContain(inventory.EffectTextures, t => t.Path.Contains("foreign"));
        Assert.Empty(inventory.Warnings);                                                  // every file the walk wanted was read
        Assert.Empty(inventory.Reach.UnavailableBins);
        Assert.All(inventory.Effects, e => Assert.False(e.IsInferred));                    // the resolver names them: not an inference
    }

    [Fact]
    public void ChildSystemsAreFollowedByKeyAndByLink_AndACycleTerminates()
    {
        // root -> (by key) B -> (by link) C -> (by key) root: a cycle through both kinds of reference
        var c = Fx("C", "C", Emitter("e", Str("texture", "assets/test/c.tex"), Children(("loop_root", null))));
        var b = Fx("B", "B", Emitter("e", Str("texture", "assets/test/b.tex"), Children((null, c))));
        var root = Fx("Root", "Root", Emitter("e", Str("texture", "assets/test/root.tex"), Children(("child_b", null))));
        var lonely = Fx("Lonely", "Lonely", Emitter("e", Str("texture", "assets/test/lonely.tex")));
        var resolver = Resolver("Res", ("root_fx", root), ("child_b", b), ("loop_root", root));
        var files = new Files()
            .Add(SkinPath(0), new[] { "DATA/Characters/Test/Systems.bin" }, Skin("Skin0", resolver), resolver)
            .Add("DATA/Characters/Test/Systems.bin", root, b, c, lonely);

        var inventory = Scan(files, sharing: false);

        Assert.Equal(new[] { "B", "C", "Root" }, inventory.Effects.Select(e => e.Name).Order());
        Assert.DoesNotContain(inventory.Effects, e => e.Name == "Lonely");                         // in the bin, referenced by nothing
        Assert.Contains(inventory.Effects.Single(e => e.Name == "C").ReachedBy, r => r == "child of B");
        Assert.Contains(inventory.Reach.ClosureSystems, h => h == lonely.PathHash);

        // B is a resolver target AND Root's child by key: the hop through the resolver is walked, and is on its record
        var viaKey = inventory.Effects.Single(e => e.Name == "B");
        Assert.Contains("effect key", viaKey.ReachedBy);
        Assert.Contains("child of Root", viaKey.ReachedBy);
        Assert.Contains(H("child_b"), viaKey.EffectKeys);
        Assert.All(inventory.Effects, e => Assert.False(e.IsInferred));
    }

    [Fact]
    public void AChildKeyOnlyAnotherSkinsResolverMaps_IsNotFollowed_ThoughTheClosureHoldsBoth()
    {
        // The preview resolves a child's key through EVERY resolver in the closure. A closure holds other skins' resolvers (a
        // chroma links its parent's), so a key only one of those maps would pull that skin's system in. Measured over all
        // 14,937 shipped skin bins that never happens - and the inventory leaves it out, so it can never start to.
        var root = Fx("Root", "Root", Emitter("e", Str("texture", "assets/test/root.tex"), Children(("their_key", null))));
        var theirs = Fx("Theirs", "Theirs_OtherSkin", Emitter("e", Str("texture", "assets/test/theirs.tex")));
        var mine = Resolver("Mine", ("root_fx", root));
        var other = Resolver("Other", ("their_key", theirs));              // the sibling's resolver, in the shared bin
        var files = new Files()
            .Add(SkinPath(0), new[] { "DATA/Characters/Test/Shared.bin" }, Skin("Skin0", mine), mine)
            .Add("DATA/Characters/Test/Shared.bin", root, theirs, other);

        var inventory = Scan(files, sharing: false);

        Assert.Equal(new[] { "Root" }, inventory.Effects.Select(e => e.Name));
        Assert.Contains(theirs.PathHash, inventory.Reach.ClosureSystems);
        Assert.DoesNotContain(inventory.EffectTextures, t => t.Path.Contains("theirs"));
        Assert.True(inventory.Reach.UnresolvedEntries >= 1);              // the key leads nowhere the skin can follow, and says so
        Assert.Contains(inventory.Notes, n => n.Contains("lead to no system"));
    }

    [Fact]
    public void WhatOnlyAGearUpgradeReachesIsInferred_ChildrenOfItToo_AndWhatTheResolverNamesIsNot()
    {
        var plain = Fx("Plain", "Plain", Emitter("e", Str("texture", "assets/test/plain.tex")));
        var both = Fx("Both", "Both", Emitter("e", Str("texture", "assets/test/both.tex")));
        var upChild = Fx("UpChild", "UpChild", Emitter("e", Str("texture", "assets/test/upchild.tex")));
        var viaKey = Fx("ViaKey", "ViaKey", Emitter("e", Str("texture", "assets/test/viakey.tex")));
        var up = Fx("Up", "Up", Emitter("e", Str("texture", "assets/test/up.tex"), Children((null, upChild))));        // a child by link
        var up2 = Fx("Up2", "Up2", Emitter("e", Str("texture", "assets/test/up2.tex"), Children(("gear_key", null)))); // a child by key
        var resolver = Resolver("Res", ("fx", plain), ("both", both));
        var gear = GearUpgrade("Gear", ("up", up), ("up2", up2), ("gear_key", viaKey), ("both_again", both));
        var files = new Files()
            .Add(SkinPath(0), new[] { "DATA/Characters/Test/Fx.bin" }, Skin("Skin0", resolver), resolver)
            .Add("DATA/Characters/Test/Fx.bin", plain, both, upChild, viaKey, up, up2, gear);

        var inventory = Scan(files, sharing: false);
        var byName = inventory.Effects.ToDictionary(e => e.Name);

        Assert.False(byName["Plain"].IsInferred);
        Assert.False(byName["Both"].IsInferred);                     // the gear names it too, but the skin's resolver got there first
        Assert.True(byName["Up"].IsInferred);
        Assert.True(byName["UpChild"].IsInferred);                   // only a child of an inferred system
        Assert.True(byName["Up2"].IsInferred);
        Assert.True(byName["ViaKey"].IsInferred);
        Assert.Contains("child of Up2", byName["ViaKey"].ReachedBy);  // the hop by key through the gear's own map is on its record
        Assert.Contains(H("gear_key"), byName["ViaKey"].EffectKeys);
        Assert.Equal(4, inventory.Effects.Count(e => e.IsInferred));
        Assert.Contains("(inferred)", inventory.Dump());
    }

    [Fact]
    public void TheResolverTheSkinNamesIsTheOneFollowed_WhereverTheClosureKeepsIt()
    {
        var mine = Fx("Mine", "Mine", Emitter("e", Str("texture", "assets/test/mine.tex")));
        var theirs = Fx("Theirs", "Theirs", Emitter("e", Str("texture", "assets/test/theirs.tex")));
        var myResolver = Resolver("MyRes", ("fx", mine));
        var otherResolver = Resolver("OtherRes", ("fx", theirs));
        var files = new Files()
            .Add(SkinPath(0), new[] { "DATA/Characters/Test/Shared.bin" }, Skin("Skin0", myResolver))                   // no resolver in the skin bin
            .Add("DATA/Characters/Test/Shared.bin", myResolver, otherResolver, mine, theirs);

        var inventory = Scan(files, sharing: false);

        Assert.Equal(new[] { "Mine" }, inventory.Effects.Select(e => e.Name));
        Assert.Contains("Shared.bin", inventory.Reach.ResolverSource);
        Assert.Equal(1, inventory.Reach.ResolverEntries);
    }

    [Fact]
    public void ASkinThatNamesNoResolverUsesNoEffects_AndSaysSo()
    {
        var system = Fx("S", "S", Emitter("e", Str("texture", "assets/test/s.tex")));
        var resolver = Resolver("Res", ("fx", system));
        var files = new Files().Add(SkinPath(0), Skin("Skin0", null), resolver, system);

        var inventory = Scan(files, sharing: false);

        Assert.Empty(inventory.Effects);
        Assert.Contains(inventory.Notes, n => n.Contains("names no ResourceResolver"));
        Assert.Contains(system.PathHash, inventory.Reach.ClosureSystems);
    }

    [Fact]
    public void AGearUpgradesResolverIsFollowedToo()
    {
        var plain = Fx("Plain", "Plain", Emitter("e", Str("texture", "assets/test/plain.tex")));
        var upgraded = Fx("Upgraded", "Upgraded", Emitter("e", Str("texture", "assets/test/upgraded.tex")));
        var resolver = Resolver("Res", ("fx", plain));
        var gear = GearUpgrade("Gear", ("fx_upgraded", upgraded));
        var files = new Files()
            .Add(SkinPath(0), new[] { "DATA/Characters/Test/Test_Multi_Skins_Skin0.bin" }, Skin("Skin0", resolver), resolver)
            .Add("DATA/Characters/Test/Test_Multi_Skins_Skin0.bin", plain, upgraded, gear);

        var inventory = Scan(files, sharing: false);

        Assert.Equal(new[] { "Plain", "Upgraded" }, inventory.Effects.Select(e => e.Name).Order());
        Assert.Contains("gear upgrade", inventory.Effects.Single(e => e.Name == "Upgraded").ReachedBy);
        Assert.Contains("effect key", inventory.Effects.Single(e => e.Name == "Plain").ReachedBy);
        Assert.True(inventory.Effects.Single(e => e.Name == "Upgraded").IsInferred);          // nothing the skin names reaches it
        Assert.False(inventory.Effects.Single(e => e.Name == "Plain").IsInferred);
    }

    // ===================================================================== sharing

    [Fact]
    public void AnItemAnotherSkinUsesIsSharedAndNamesIt_AndAPrivateItemNamesNobody()
    {
        var shared = Fx("Shared", "Shared_Q", Emitter("e", Str("texture", "assets/test/shared_sprite.tex")));
        var own = Fx("Own0", "Own0_Q", Emitter("e", Str("texture", "assets/test/own0_sprite.tex")));
        var other = Fx("Own2", "Own2_Q", Emitter("e", Str("texture", "assets/test/own2_sprite.tex")));
        var r0 = Resolver("R0", ("q", shared), ("w", own));
        var r1 = Resolver("R1", ("q", shared));
        var r2 = Resolver("R2", ("q", other));

        var files = new Files()
            .Add(SkinPath(0), new[] { "DATA/Characters/Test/Fx.bin" }, Skin("Skin0", r0,
                SkinMesh("assets/characters/test/skins/base/common.tex")), r0,
                Material("Mat0", new[] { ("Diffuse_Texture", "assets/characters/test/skins/base/private0.tex"), ("Main_Texture", "assets/characters/test/skins/base/maskedelsewhere.tex") }))
            .Add(SkinPath(1), new[] { "DATA/Characters/Test/Fx.bin" }, Skin("Skin1", r1,
                SkinMesh("assets/characters/test/skins/base/common.tex")), r1,
                // skin 1 samples the file skin 0 paints with as a MASK: editing it would still change skin 1
                Material("Mat1", new[] { ("Mask_Texture", "assets/characters/test/skins/base/maskedelsewhere.tex") }))
            .Add(SkinPath(2), new[] { "DATA/Characters/Test/Fx.bin" }, Skin("Skin2", r2,
                SkinMesh("assets/characters/test/skins/skin2/own.tex")), r2)
            .Add("DATA/Characters/Test/Fx.bin", shared, own, other);

        var inventory = Scan(files, names: n => n == 1 ? "Skin One" : null);

        Assert.Equal(new[] { "Skin One (skin1)", "skin2" }, inventory.ComparedSkins);
        Assert.True(inventory.SharingComputed);
        Assert.Empty(inventory.Warnings);

        var sharedSystem = inventory.Effects.Single(e => e.Name == "Shared_Q");
        Assert.Equal(new[] { "Skin One (skin1)" }, sharedSystem.SharedWith);                   // skin 2's resolver does not reach it
        Assert.Empty(inventory.Effects.Single(e => e.Name == "Own0_Q").SharedWith);

        var sharedTexture = inventory.EffectTextures.Single(t => t.Path.EndsWith("shared_sprite.tex"));
        Assert.Equal(new[] { "Skin One (skin1)" }, sharedTexture.SharedWith);
        Assert.Empty(inventory.EffectTextures.Single(t => t.Path.EndsWith("own0_sprite.tex")).SharedWith);

        var common = inventory.BodyTextures.Single(t => t.Path.EndsWith("common.tex"));
        Assert.Equal(new[] { "Skin One (skin1)" }, common.SharedWith);
        Assert.True(common.IsShared);
        Assert.Empty(inventory.BodyTextures.Single(t => t.Path.EndsWith("private0.tex")).SharedWith);
        // any role counts: skin 1 uses this file as a mask, and a recolour of it would reach skin 1
        Assert.Equal(new[] { "Skin One (skin1)" }, inventory.BodyTextures.Single(t => t.Path.EndsWith("maskedelsewhere.tex")).SharedWith);

        Assert.Single(inventory.Effects, e => e.IsShared);
        // the skin-default texture and the file skin 1 masks with, the shared sprite, the shared system
        Assert.Equal(4, inventory.SharedItemCount);
    }

    [Fact]
    public void SharingCanBeSwitchedOffOrLimitedToTheSkinsAsked()
    {
        var shared = Fx("Shared", "Shared_Q", Emitter("e", Str("texture", "assets/test/shared.tex")));
        var r = Resolver("R", ("q", shared));
        var files = new Files()
            .Add(SkinPath(0), new[] { "DATA/Characters/Test/Fx.bin" }, Skin("Skin0", r), r)
            .Add(SkinPath(1), new[] { "DATA/Characters/Test/Fx.bin" }, Skin("Skin1", r), r)
            .Add(SkinPath(2), new[] { "DATA/Characters/Test/Fx.bin" }, Skin("Skin2", r), r)
            .Add("DATA/Characters/Test/Fx.bin", shared);

        var off = Scan(files, sharing: false);
        Assert.Empty(off.ComparedSkins);
        Assert.False(off.SharingComputed);                            // "nothing compared" and "compared with nobody" are not the same
        Assert.Equal(0, off.SharedItemCount);
        Assert.Contains(off.Notes, n => n.Contains("Sharing was not computed"));

        var limited = Scan(files, numbers: new[] { 0, 2 });
        Assert.Equal(new[] { "skin2" }, limited.ComparedSkins);
        Assert.True(limited.SharingComputed);
        Assert.Equal(new[] { "skin2" }, limited.Effects.Single().SharedWith);
        // the caller narrowed the comparison, so a skin it left out is not "missing": the host's list of skin 1 raises nothing
        Assert.Empty(Scan(files, numbers: new[] { 0, 2 }, expected: new[] { 0, 1, 2 }).Warnings);

        var probed = Scan(files);
        Assert.Equal(new[] { "skin1", "skin2" }, probed.ComparedSkins);
        Assert.Equal(new[] { "skin1", "skin2" }, probed.Effects.Single().SharedWith);
    }

    // ===================================================================== the body

    [Fact]
    public void BodyTexturesAreClassifiedByName_AndEveryExclusionSaysWhy()
    {
        var material = Material("Characters/Test/Skins/Skin0/Materials/Body_inst", new[]
        {
            ("Diffuse_Texture", "assets/characters/test/skins/base/diffuse.tex"),
            ("Mask_Texture", "assets/characters/test/skins/base/mask.tex"),
            ("Normal_Texture", "assets/characters/test/skins/base/normal.tex"),
            ("MatCap_Tex", "assets/shared/materials/matcap/gold.tex"),
            ("MatCap_Mask", "assets/shared/materials/matcap/gold_mask.tex"),
            ("Gradient_Texture", "assets/characters/test/skins/base/gradient.tex"),
            ("EmissionR_DistortionG_Texture", "assets/characters/test/skins/base/emission.tex"),   // emission in R, distortion in G
            ("Glow_Texture", "assets/characters/test/skins/base/glow_plain.tex"),
            ("Noise_Texture", "assets/shared/noise.tex"),
            ("Color_Mask_Texture", "assets/characters/test/skins/base/color_mask.tex"),
        });
        var skinMesh = SkinMesh("assets/characters/test/skins/base/body_tx_cm.tex",
            Str("glossTexture", "assets/characters/test/skins/base/gloss.tex"),
            Str("reflectionMap", "assets/shared/reflect.tex"),
            Str("emissiveTexture", "assets/characters/test/skins/base/glow.tex"),
            new BinTreeObjectLink(H("material"), material.PathHash),
            new BinTreeContainer(H("materialOverride"), BinPropertyType.Embedded, new BinTreeProperty[]
            {
                new BinTreeEmbedded(0, H("SkinMeshDataProperties_MaterialOverride"), new BinTreeProperty[]
                {
                    Str("submesh", "Hair"), Str("texture", "assets/characters/test/skins/base/hair.tex"),
                }),
            }));
        var files = new Files().Add(SkinPath(0), Skin("Skin0", null, skinMesh), material);

        var inventory = Scan(files, sharing: false);

        Assert.Empty(inventory.UnresolvedMaterials);                // the skin block's material link is to an object in this very bin
        var roles = inventory.BodyTextures.ToDictionary(t => System.IO.Path.GetFileName(t.Path), t => t.Role);
        Assert.Equal(new Dictionary<string, BodyTextureRole>
        {
            ["body_tx_cm.tex"] = BodyTextureRole.Diffuse,        // the skin block's texture
            ["glow.tex"] = BodyTextureRole.Emissive,             // the skin block's emissiveTexture
            ["reflect.tex"] = BodyTextureRole.Reflection,        // the skin block's reflectionMap
            ["hair.tex"] = BodyTextureRole.Diffuse,              // an inline materialOverride texture
            ["diffuse.tex"] = BodyTextureRole.Diffuse,
            ["gradient.tex"] = BodyTextureRole.Gradient,
            ["glow_plain.tex"] = BodyTextureRole.Emissive,       // an emissive name that packs nothing is still colour
            ["gold.tex"] = BodyTextureRole.MatCap,
        }, roles);

        var excluded = inventory.ExcludedSamplers.ToDictionary(e => System.IO.Path.GetFileName(e.Path), e => e.Reason);
        Assert.Contains("mask", excluded["mask.tex"]);
        Assert.Contains("normal", excluded["normal.tex"]);
        Assert.Contains("channel-packed", excluded["emission.tex"]);  // "Emission" does not rescue a texture that is two data maps in one
        Assert.Contains("mask", excluded["gold_mask.tex"]);
        Assert.Contains("mask", excluded["color_mask.tex"]);          // "Color" in its name does not rescue a mask
        Assert.Contains("gloss", excluded["gloss.tex"]);
        Assert.Contains("not named as a colour map", excluded["noise.tex"]);
        // nothing is both listed and excluded
        Assert.Empty(roles.Keys.Intersect(excluded.Keys));

        // where each came from
        var hair = inventory.BodyTextures.Single(t => t.Path.EndsWith("hair.tex"));
        Assert.Equal("(inline override: Hair)", hair.Source);
        Assert.Equal("(skin default texture)", inventory.BodyTextures.Single(t => t.Path.EndsWith("body_tx_cm.tex")).Source);
        Assert.StartsWith("Body_inst (Diffuse_Texture)", inventory.BodyTextures.Single(t => t.Path.EndsWith("diffuse.tex")).Source);
        Assert.All(inventory.BodyTextures, t => Assert.False(t.IsLinked));
        Assert.All(inventory.BodyTextures, t => Assert.Equal(BinTexturePath.HashOfReference(t.Path), t.Hash));
    }

    [Fact]
    public void ATextureFromALinkedBinIsMarkedLinked()
    {
        var linked = Material("Characters/Test/Skins/Skin0/Materials/Glass_inst", new[] { ("Diffuse_Texture", "assets/characters/test/skins/base/glass.tex") });
        var skinMesh = SkinMesh("assets/characters/test/skins/base/body_tx_cm.tex",
            new BinTreeContainer(H("materialOverride"), BinPropertyType.Embedded, new BinTreeProperty[]
            {
                new BinTreeEmbedded(0, H("SkinMeshDataProperties_MaterialOverride"), new BinTreeProperty[]
                {
                    Str("submesh", "glass"), new BinTreeObjectLink(H("material"), linked.PathHash),
                }),
            }));
        var files = new Files()
            .Add(SkinPath(0), new[] { "DATA/Characters/Test/Test_Multi_Skins_Skin0.bin" }, Skin("Skin0", null, skinMesh))
            .Add("DATA/Characters/Test/Test_Multi_Skins_Skin0.bin", linked);

        var scanned = Scan(files, sharing: false);
        var glass = scanned.BodyTextures.Single(t => t.Path.EndsWith("glass.tex"));

        Assert.Empty(scanned.UnresolvedMaterials);                  // a link into a DIRECT dependency resolves
        Assert.Empty(scanned.Warnings);
        Assert.True(glass.IsLinked);
        Assert.Equal("DATA/Characters/Test/Test_Multi_Skins_Skin0.bin", glass.LinkedFromBin);
        Assert.False(Scan(files, sharing: false).BodyTextures.Single(t => t.Path.EndsWith("body_tx_cm.tex")).IsLinked);
    }

    private static BinTreeEmbedded MaterialOverrideOf(string submesh, uint material) =>
        new(0, H("SkinMeshDataProperties_MaterialOverride"), new BinTreeProperty[] { Str("submesh", submesh), new BinTreeObjectLink(H("material"), material) });

    [Fact]
    public void AMaterialTheSkinNamesThatNoFileItLinksDefinesIsAWarningForEachLink_AndOneInATransitiveDependencyIsOneToo()
    {
        // The material reader looks in the skin bin and the skin bin's DIRECT dependencies. A link it cannot resolve produces no
        // material, so the textures are silently missing from the body list - unless the scan compares the links with what came out.
        var inSkin = Material("Characters/Test/Skins/Skin0/Materials/Hair_inst", new[] { ("Diffuse_Texture", "assets/characters/test/skins/base/hair.tex") });
        var direct = Material("Characters/Test/Skins/Skin0/Materials/Cloth_inst", new[] { ("Diffuse_Texture", "assets/characters/test/skins/base/cloth.tex") });
        var deep = Material("Characters/Test/Skins/Skin0/Materials/Cape_inst", new[] { ("Diffuse_Texture", "assets/characters/test/skins/base/cape.tex") });
        uint missing = H("Characters/Test/Skins/Skin0/Materials/Missing_inst"), gone = H("Characters/Test/Skins/Skin0/Materials/Wing_inst");
        var skinMesh = SkinMesh("assets/characters/test/skins/base/body_tx_cm.tex",
            new BinTreeObjectLink(H("material"), missing),                                         // the base mesh's material: defined nowhere
            new BinTreeContainer(H("materialOverride"), BinPropertyType.Embedded, new BinTreeProperty[]
            {
                MaterialOverrideOf("Hair", inSkin.PathHash),                                        // in the skin bin
                MaterialOverrideOf("Cloth", direct.PathHash),                                       // in a direct dependency
                MaterialOverrideOf("Cape", deep.PathHash),                                          // in a dependency of a dependency
                MaterialOverrideOf("Wing", gone),                                                   // defined nowhere
            }));
        var files = new Files()
            .Add(SkinPath(0), new[] { "DATA/Characters/Test/Direct.bin" }, Skin("Skin0", null, skinMesh), inSkin)
            .Add("DATA/Characters/Test/Direct.bin", new[] { "DATA/Characters/Test/Deep.bin" }, direct)
            .Add("DATA/Characters/Test/Deep.bin", deep);

        var inventory = Scan(files, sharing: false, name: h => h == missing ? "Characters/Test/Skins/Skin0/Materials/Missing_inst" : Resolve(h));

        var paths = inventory.BodyTextures.Select(t => System.IO.Path.GetFileName(t.Path)).ToList();
        Assert.Contains("hair.tex", paths);                                // resolved: listed
        Assert.Contains("cloth.tex", paths);
        Assert.DoesNotContain("cape.tex", paths);                          // not resolved by the parse's rule: not listed...
        Assert.Equal(3, inventory.UnresolvedMaterials.Count);              // ...and said so, once per link
        Assert.StartsWith("the base mesh: Characters/Test/Skins/Skin0/Materials/Missing_inst (0x", inventory.UnresolvedMaterials[0]);
        Assert.Equal($"submesh \"Cape\": 0x{deep.PathHash:x8}", inventory.UnresolvedMaterials[1]);
        Assert.Equal($"submesh \"Wing\": 0x{gone:x8}", inventory.UnresolvedMaterials[2]);
        var warning = Assert.Single(inventory.Warnings);
        Assert.StartsWith("3 material link(s) of this skin lead to a material that is defined neither in its bin nor in a bin it links directly", warning);
        Assert.Contains("Cape", warning);
        Assert.Contains("those textures are not listed", warning);
    }

    [Fact]
    public void ASiblingWithAnUnresolvedMaterialIsComparedButSaysItsSharingMayBeUnderstated()
    {
        var shared = Fx("Shared", "Shared_Q", Emitter("e", Str("texture", "assets/test/shared.tex")));
        var r = Resolver("R", ("q", shared));
        var lost = H("Characters/Test/Skins/Skin1/Materials/Lost_inst");
        var skin1Mesh = SkinMesh("assets/characters/test/skins/skin1/own.tex",
            new BinTreeContainer(H("materialOverride"), BinPropertyType.Embedded, new BinTreeProperty[] { MaterialOverrideOf("Cape", lost) }));
        var files = new Files()
            .Add(SkinPath(0), new[] { "DATA/Characters/Test/Fx.bin" }, Skin("Skin0", r), r)
            .Add(SkinPath(1), new[] { "DATA/Characters/Test/Fx.bin" }, Skin("Skin1", r, skin1Mesh), r)
            .Add("DATA/Characters/Test/Fx.bin", shared);
        var scanner = new SkinColorScanner();

        var inventory = Scan(files, scanner: scanner);

        Assert.Empty(inventory.UnresolvedMaterials);                       // skin 0 is whole
        Assert.Equal(new[] { "skin1" }, inventory.ComparedSkins);
        var warning = Assert.Single(inventory.Warnings);
        Assert.StartsWith("skin1:", warning);
        Assert.Contains("1 material link(s) unresolved", warning);
        Assert.Contains("understated", warning);

        // from the cache, the same
        Assert.Single(Scan(files, scanner: scanner).Warnings);
        // and the sibling scanned ON ITS OWN reports the link itself
        var own = Scan(files, skin: 1, scanner: scanner);
        Assert.Single(own.UnresolvedMaterials);
        Assert.Contains("submesh \"Cape\"", own.UnresolvedMaterials[0]);
    }

    [Fact]
    public void ColourParametersFollowTheNameRule_AndTheRuleIsAHeuristicThatShowsTheValue()
    {
        var material = Material("Characters/Test/Skins/Skin0/Materials/Body_inst", Array.Empty<(string, string)>(),
            Param("TintColor", V4(1, 0.5f, 0.25f, 1)),
            Param("Bloom_Color", null),                                                       // names itself, writes no value: an authored zero
            Param("Rim_Color", new BinTreeVector3(H("value"), new Vector3(0.1f, 0.2f, 0.3f))),
            Param("LightIntensity", V4(2, 0, 0, 0)),                                          // "tint" only by accident of DistorTINTensity
            Param("Outline_Thickness", V4(3, 0, 0, 0)),
            Param("ColorBoost", new BinTreeF32(H("value"), 2f)),                              // a scalar cannot hold an RGB colour
            Param("Fresnel_Color_Intensity", V4(5, 0, 0, 0)));                                // a known false positive: it says "color"
        var skinMesh = SkinMesh("assets/characters/test/skins/base/body_tx_cm.tex",
            new BinTreeColor(H("reflectionFresnelColor"), new LeagueToolkit.Core.Primitives.Color(0f, 0f, 0f, 1f)),   // float components, as the bin reader hands them over
            new BinTreeF32(H("selfIllumination"), 0.7f));
        var files = new Files().Add(SkinPath(0), Skin("Skin0", null, skinMesh), material);

        var parameters = Scan(files, sharing: false).BodyParameters;
        var byName = parameters.ToDictionary(p => p.Name);

        Assert.Equal(new[] { "Bloom_Color", "Fresnel_Color_Intensity", "reflectionFresnelColor", "Rim_Color", "TintColor" },
            parameters.Select(p => p.Name).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(new Vector4(1, 0.5f, 0.25f, 1), byName["TintColor"].Value);
        Assert.True(byName["Bloom_Color"].ValueOmitted);
        Assert.Equal(Vector4.Zero, byName["Bloom_Color"].Value);
        Assert.Equal(new Vector4(0.1f, 0.2f, 0.3f, 1), byName["Rim_Color"].Value);
        Assert.Equal("(skin default texture)", byName["reflectionFresnelColor"].Material);   // the skin block's own colour
        Assert.Equal(new Vector4(0, 0, 0, 1), byName["reflectionFresnelColor"].Value);
        Assert.True(SkinColorScanner.IsColorName("MatCap_TintColor"));
        Assert.False(SkinColorScanner.IsColorName("TailDistortIntensity"));
    }

    [Fact]
    public void AScanWithoutTheDictionaryStillFindsTheSkinBlocksTexturesAndColours()
    {
        // no ResolveName at all: the skin block's field names arrive as 0x<hash>, as they would on an install whose dictionary is missing
        var skinMesh = SkinMesh("assets/characters/test/skins/base/body_tx_cm.tex",
            Str("glossTexture", "assets/characters/test/skins/base/gloss.tex"),
            Str("emissiveTexture", "assets/characters/test/skins/base/glow.tex"),
            new BinTreeColor(H("reflectionFresnelColor"), new LeagueToolkit.Core.Primitives.Color(0.5f, 0.25f, 0f, 1f)));
        var files = new Files().Add(SkinPath(0), Skin("Skin0", null, skinMesh));

        var inventory = new SkinColorScanner().Scan(new SkinColorRequest(SkinPath(0), files.Read, IncludeSharing: false));

        var roles = inventory.BodyTextures.ToDictionary(t => System.IO.Path.GetFileName(t.Path), t => t.Role);
        Assert.Equal(BodyTextureRole.Diffuse, roles["body_tx_cm.tex"]);
        Assert.Equal(BodyTextureRole.Emissive, roles["glow.tex"]);
        Assert.DoesNotContain("gloss.tex", roles.Keys);
        var color = Assert.Single(inventory.BodyParameters);
        Assert.Equal($"0x{H("reflectionFresnelColor"):x8}", color.Name);
        // a Color is 8 bits a channel in the bin, so 0.5 reads back as 127/255
        Assert.InRange(color.Value.X, 0.49f, 0.51f);
        Assert.InRange(color.Value.Y, 0.24f, 0.26f);
        Assert.Equal(new Vector2(0f, 1f), new Vector2(color.Value.Z, color.Value.W));
    }

    [Fact]
    public void AnUnnamedChunkLinkIsHashedAsItselfAndNotAsTheTextOf_0x()
    {
        const ulong link = 0x1122334455667788UL;
        var material = new BinTreeObject(H("Mat"), H("StaticMaterialDef"), new BinTreeProperty[]
        {
            Str("name", "Characters/Test/Skins/Skin0/Materials/Body_inst"),
            new BinTreeUnorderedContainer(H("samplerValues"), BinPropertyType.Embedded, new BinTreeProperty[]
            {
                new BinTreeEmbedded(0, 0x0904b150, new BinTreeProperty[] { Str("TextureName", "Diffuse_Texture"), new BinTreeWadChunkLink(H("texturePath"), link) }),
            }),
        });
        var files = new Files().Add(SkinPath(0), Skin("Skin0", null, SkinMesh("assets/characters/test/skins/base/body_tx_cm.tex",
            new BinTreeObjectLink(H("material"), material.PathHash))), material);

        var texture = Scan(files, sharing: false).BodyTextures.Single(t => t.Path.StartsWith("0x"));

        Assert.Equal("0x1122334455667788", texture.Path);
        Assert.Equal(link, texture.Hash);
        Assert.NotEqual(HashAlgorithms.WadPath(texture.Path), texture.Hash);   // what a blind WadPath would have addressed instead
    }

    [Theory]
    [InlineData("EmissionR_DistortionG_Texture", true)]     // emission in R, distortion in G: the one colour-classified name in the install that does this
    [InlineData("AlphaA_ColorB_Texture", true)]
    [InlineData("Tex_R_Tex_G", true)]
    [InlineData("Diffuse_Texture", false)]
    [InlineData("Glow_Texture", false)]
    [InlineData("Gradient_Texture", false)]                 // a G that starts a word is not a tag
    [InlineData("BloomGradient_Texture", false)]
    [InlineData("FX_ColorRamp", false)]
    [InlineData("Alt_Diffuse", false)]
    [InlineData("RMA_Texture", false)]                      // adjacent capitals are an abbreviation, not tags
    [InlineData("Color_RGB", false)]                        // RGB is a whole colour
    [InlineData("Noise_R", false)]                          // one tag alone is not packing (and none of the shipped colour names has one)
    [InlineData("Diffuse_Texture2", false)]
    [InlineData("", false)]
    public void ASamplerNamePacksChannelsOnlyWhenItCarriesTwoChannelTags(string name, bool packed) =>
        Assert.Equal(packed, SkinColorScanner.IsChannelPacked(name));

    [Theory]
    [InlineData("assets/characters/lillia/skins/skin49/body.tex", "lillia", false)]
    [InlineData("ASSETS/Characters/Lillia/Skins/Skin49/Body.tex", "lillia", false)]
    [InlineData("assets\\characters\\lillia\\x.tex", "lillia", false)]
    [InlineData("assets/shared/particles/x.tex", "lillia", true)]
    [InlineData("assets/characters/lux/skins/base/x.tex", "lillia", true)]      // another champion's file
    [InlineData("assets/characters/lilliafake/x.tex", "lillia", true)]          // a folder that merely starts with the name
    [InlineData("0x1122334455667788", "lillia", false)]                         // unnamed: no place to judge it by
    [InlineData("", "lillia", false)]
    public void AFileOutsideTheCharactersOwnFolderIsRecognised(string path, string folder, bool outside) =>
        Assert.Equal(outside, SkinColorInventory.IsOutsideCharacter(path, folder));

    // ===================================================================== the promises around it

    [Fact]
    public void TheSameBytesGiveTheSameInventory_AndTheCacheFollowsTheBytes()
    {
        var sys = Fx("S", "First_Name", Emitter("e", ValueColor("birthColor", new Vector4(1, 0, 0, 1)), Str("texture", "assets/test/s.tex")));
        var r = Resolver("R", ("fx", sys));
        Files Build(string particleName) => new Files()
            .Add(SkinPath(0), new[] { "DATA/Characters/Test/Fx.bin" }, Skin("Skin0", r), r)
            .Add(SkinPath(1), new[] { "DATA/Characters/Test/Fx.bin" }, Skin("Skin1", r), r)
            .Add("DATA/Characters/Test/Fx.bin", Fx("S", particleName, Emitter("e", ValueColor("birthColor", new Vector4(1, 0, 0, 1)), Str("texture", "assets/test/s.tex"))));

        var files = Build("First_Name");
        var scanner = new SkinColorScanner();
        string first = Scan(files, scanner: scanner).Dump();
        string again = Scan(files, scanner: scanner).Dump();          // from the cache
        string fresh = Scan(Build("First_Name")).Dump();              // from nothing
        Assert.Equal(first, again);
        Assert.Equal(first, fresh);
        Assert.Contains("First_Name", first);

        // the dependency changes underneath a warm cache and the scan sees it. (A system's NAME is not in any "uses" set, so this
        // proves the parse is redone, not that sharing follows it - ACachedSiblingIsReadAgainWhenABinItLinksChanges does that.)
        string changed = Scan(Build("Second_Name"), scanner: scanner).Dump();
        Assert.Contains("Second_Name", changed);
        Assert.DoesNotContain("First_Name", changed);

        scanner.Clear();
        Assert.Equal(changed, Scan(Build("Second_Name"), scanner: scanner).Dump());
    }

    [Fact]
    public void AnUnreadableSkinGivesAnEmptyInventoryWithAReason_AndCancellationIsHonoured()
    {
        var empty = Scan(new Files());
        Assert.Empty(empty.BodyTextures);
        Assert.Empty(empty.Effects);
        Assert.Contains(empty.Warnings, w => w.Contains("could not be read"));

        var garbage = new Files();
        garbage.Bins[SkinPath(0)] = new byte[] { 1, 2, 3, 4, 5 };
        var inventory = Scan(garbage, sharing: false);                // it must not throw, and it must not look like a skin with nothing in it
        Assert.Empty(inventory.Effects);
        Assert.Contains(inventory.Warnings, w => w.Contains("could not be parsed"));

        var faulty = new Files();
        faulty.Bins[SkinPath(0)] = new byte[] { 1 };
        faulty.Faulty.Add(SkinPath(0));                               // there, and unreadable: not the same as missing
        Assert.Contains(Scan(faulty).Warnings, w => w.Contains("could not be read") && w.Contains("IOException"));

        var sys = Fx("S", "S", Emitter("e", Str("texture", "assets/test/s.tex")));
        var r = Resolver("R", ("fx", sys));
        var files = new Files().Add(SkinPath(0), Skin("Skin0", r), r, sys);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            new SkinColorScanner().Scan(new SkinColorRequest(SkinPath(0), files.Read, Resolve), cts.Token));
    }

    [Fact]
    public void ADependencyBinThatIsMissingOrBrokenIsAWarning_AndTheScanGoesOnWithTheRest()
    {
        var good = Fx("Good", "Good_Q", Emitter("e", Str("texture", "assets/test/good.tex")));
        var r = Resolver("R", ("q", good));
        var files = new Files()
            .Add(SkinPath(0), new[]
            {
                "DATA/Characters/Test/Gone.bin", "DATA/Characters/Test/Garbage.bin", "DATA/Characters/Test/Faulty.bin", "DATA/Characters/Test/Good.bin",
            }, Skin("Skin0", r), r)
            .Add("DATA/Characters/Test/Good.bin", good);
        files.Bins["DATA/Characters/Test/Garbage.bin"] = new byte[] { 9, 9, 9, 9, 9, 9 };
        files.Bins["DATA/Characters/Test/Faulty.bin"] = new byte[] { 1 };
        files.Faulty.Add("DATA/Characters/Test/Faulty.bin");

        var inventory = Scan(files, sharing: false);

        Assert.Equal(new[] { "Good_Q" }, inventory.Effects.Select(e => e.Name));               // what could be read is listed
        var warning = Assert.Single(inventory.Warnings);
        Assert.Contains("3 dependency bin(s) of this skin were not available", warning);
        Assert.Contains("Gone.bin (not found)", warning);
        Assert.Contains("Garbage.bin (could not be parsed", warning);
        Assert.Contains("Faulty.bin (could not be read: IOException", warning);
        Assert.Contains("not listed", warning);
        Assert.Equal(3, inventory.Reach.UnavailableBins.Count);
    }

    [Fact]
    public void ASiblingTheScanCouldNotUseIsAWarning_NotSilentlyUnshared()
    {
        var shared = Fx("Shared", "Shared_Q", Emitter("e", Str("texture", "assets/test/shared.tex")));
        var r = Resolver("R", ("q", shared));
        var files = new Files()
            .Add(SkinPath(0), new[] { "DATA/Characters/Test/Fx.bin" }, Skin("Skin0", r), r)
            .Add(SkinPath(3), new[] { "DATA/Characters/Test/Fx.bin" }, Skin("Skin3", r), r)
            .Add("DATA/Characters/Test/Fx.bin", shared);
        files.Bins[SkinPath(1)] = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };                     // would not parse
        files.Bins[SkinPath(2)] = new byte[] { 1 };
        files.Faulty.Add(SkinPath(2));                                                       // would not read

        var inventory = Scan(files, expected: new[] { 0, 1, 2, 3, 4 });

        Assert.Equal(new[] { "skin3" }, inventory.ComparedSkins);
        Assert.True(inventory.SharingComputed);
        Assert.Equal(new[] { "skin3" }, inventory.Effects.Single().SharedWith);
        Assert.Contains(inventory.Warnings, w => w.StartsWith("skin1.bin could not be parsed"));
        Assert.Contains(inventory.Warnings, w => w.StartsWith("skin2.bin could not be read (IOException"));
        // skin 4 is listed by the character's WAD and the reader never found it
        Assert.Contains(inventory.Warnings, w => w == "skin4.bin is listed for this character but was not found, so it was not compared.");
        Assert.DoesNotContain(inventory.Warnings, w => w.Contains("skin0.bin"));              // the skin itself is not a sibling
        Assert.DoesNotContain(inventory.Warnings, w => w.Contains("skin3"));                  // the one that worked
        Assert.Equal(3, inventory.Warnings.Count);                                            // one line each, none twice
    }

    [Fact]
    public void NoSiblingIsStatedAsZero_AndAMissingOneTheHostKnowsAboutIsAWarning()
    {
        var files = new Files().Add(SkinPath(0), Skin("Skin0", null));

        var alone = Scan(files);
        Assert.True(alone.SharingComputed);
        Assert.Empty(alone.ComparedSkins);                    // compared with nobody is a fact...
        Assert.Empty(alone.Warnings);                         // ...when nobody was expected

        var expecting = Scan(files, expected: new[] { 0, 1, 2 });
        Assert.Empty(expecting.ComparedSkins);
        Assert.Equal(2, expecting.Warnings.Count);            // ...and a problem when two were
        Assert.All(expecting.Warnings, w => Assert.Contains("is listed for this character but was not found", w));
    }

    [Fact]
    public void ASiblingWhoseDependencyIsMissingSaysItsSharingMayBeUnderstated()
    {
        var shared = Fx("Shared", "Shared_Q", Emitter("e", Str("texture", "assets/test/shared.tex")));
        var r = Resolver("R", ("q", shared));
        var files = new Files()
            .Add(SkinPath(0), new[] { "DATA/Characters/Test/Fx.bin" }, Skin("Skin0", r), r)
            .Add(SkinPath(1), new[] { "DATA/Characters/Test/Fx.bin", "DATA/Characters/Test/Lost.bin" }, Skin("Skin1", r), r)
            .Add("DATA/Characters/Test/Fx.bin", shared);

        var inventory = Scan(files);

        Assert.Equal(new[] { "skin1" }, inventory.ComparedSkins);                             // it was read, so it was compared
        var warning = Assert.Single(inventory.Warnings);                                      // skin 0's own closure is whole
        Assert.StartsWith("skin1:", warning);
        Assert.Contains("Lost.bin (not found)", warning);
        Assert.Contains("understated", warning);

        // and the second scan, from the cache, still says so
        var scanner = new SkinColorScanner();
        Scan(files, scanner: scanner);
        Assert.Single(Scan(files, scanner: scanner).Warnings);
    }

    [Fact]
    public void ACachedSiblingIsReadAgainWhenABinItLinksChanges_SoWhatIsSharedFollowsIt()
    {
        const string shared = "DATA/Characters/Test/Shared.bin";
        // Skin 1 takes its resolver AND its cloth material from the shared bin, so editing that bin changes skin 1 without a
        // byte of skin 1's own file changing - which is what a cache keyed by the skin's own file would miss.
        Files Build(bool edited)
        {
            var s = Fx("S", "S_Q", Emitter("e", Str("texture", "assets/test/s.tex")));
            var s2 = Fx("S2", "S2_Q", Emitter("e", Str("texture", "assets/test/s2.tex")));
            var r0 = Resolver("R0", ("fx", s));
            var r1 = Resolver("R1", ("fx", edited ? s2 : s));
            var cloth = Material("Characters/Test/Skins/Skin1/Materials/Cloth_inst", new[]
            {
                ("Diffuse_Texture", edited ? "assets/characters/test/skins/base/elsewhere.tex" : "assets/characters/test/skins/base/common.tex"),
            });
            var skin1Mesh = SkinMesh("assets/characters/test/skins/skin1/own.tex",
                new BinTreeContainer(H("materialOverride"), BinPropertyType.Embedded, new BinTreeProperty[]
                {
                    new BinTreeEmbedded(0, H("SkinMeshDataProperties_MaterialOverride"), new BinTreeProperty[]
                    {
                        Str("submesh", "cloth"), new BinTreeObjectLink(H("material"), cloth.PathHash),
                    }),
                }));
            return new Files()
                .Add(SkinPath(0), new[] { shared }, Skin("Skin0", r0, SkinMesh("assets/characters/test/skins/base/common.tex")), r0)
                .Add(SkinPath(1), new[] { shared }, Skin("Skin1", r1, skin1Mesh))
                .Add(shared, s, s2, r1, cloth);
        }

        var scanner = new SkinColorScanner();
        var before = Scan(Build(edited: false), scanner: scanner);
        Assert.Equal(new[] { "skin1" }, before.Effects.Single(e => e.Name == "S_Q").SharedWith);                 // skin 1 plays S through R1
        Assert.Equal(new[] { "skin1" }, before.BodyTextures.Single(t => t.Path.EndsWith("common.tex")).SharedWith); // and paints its cloth with common.tex
        Assert.Equal(new[] { "skin1" }, before.EffectTextures.Single(t => t.Path.EndsWith("/s.tex")).SharedWith);
        Assert.Equal(3, before.SharedItemCount);

        // the shared bin is edited under a warm cache: R1 now plays S2, the cloth is a different file
        var after = Scan(Build(edited: true), scanner: scanner);
        Assert.Empty(after.Effects.Single(e => e.Name == "S_Q").SharedWith);
        Assert.Empty(after.BodyTextures.Single(t => t.Path.EndsWith("common.tex")).SharedWith);
        Assert.Empty(after.EffectTextures.Single(t => t.Path.EndsWith("/s.tex")).SharedWith);
        Assert.Equal(0, after.SharedItemCount);
    }

    [Fact]
    public void AResultDegradedByAFailedLinkedBinReadIsWarnedAboutAndNotKept_SoTheNextScanIsWhole()
    {
        const string shared = "DATA/Characters/Test/Shared.bin";
        var cloth = Material("Characters/Test/Skins/Skin1/Materials/Cloth_inst", new[] { ("Diffuse_Texture", "assets/characters/test/skins/base/common.tex") });
        var skin1Mesh = SkinMesh("assets/characters/test/skins/skin1/own.tex",
            new BinTreeContainer(H("materialOverride"), BinPropertyType.Embedded, new BinTreeProperty[]
            {
                new BinTreeEmbedded(0, H("SkinMeshDataProperties_MaterialOverride"), new BinTreeProperty[]
                {
                    Str("submesh", "cloth"), new BinTreeObjectLink(H("material"), cloth.PathHash),
                }),
            }));
        var files = new Files()
            .Add(SkinPath(0), new[] { shared }, Skin("Skin0", null, SkinMesh("assets/characters/test/skins/base/common.tex")))
            .Add(SkinPath(1), new[] { shared }, Skin("Skin1", null, skin1Mesh))
            .Add(shared, cloth);
        var scanner = new SkinColorScanner();

        // skin 1's material is read from the shared bin a SECOND time while its inventory is built; that read fails once
        int sharedReads = 0;
        files.OnRead = path => { if (path == shared && ++sharedReads == 2) throw new IOException("the disk blinked"); };
        var degraded = Scan(files, scanner: scanner);
        Assert.Equal(new[] { "skin1" }, degraded.ComparedSkins);
        Assert.Contains(degraded.Warnings, w => w.StartsWith("skin1:") && w.Contains("materials were not read in full") && w.Contains("understated"));
        Assert.Empty(degraded.BodyTextures.Single(t => t.Path.EndsWith("common.tex")).SharedWith);     // skin 1's cloth was never seen

        // the disk is fine again: the earlier, degraded result must not be what the cache hands back
        files.OnRead = null;
        var whole = Scan(files, scanner: scanner);
        Assert.Empty(whole.Warnings);
        Assert.Equal(new[] { "skin1" }, whole.BodyTextures.Single(t => t.Path.EndsWith("common.tex")).SharedWith);
    }

    [Fact]
    public async Task AScanQueuedBehindAnotherObservesItsTokenWhileItWaits()
    {
        var sys = Fx("S", "S", Emitter("e", Str("texture", "assets/test/s.tex")));
        var r = Resolver("R", ("fx", sys));
        var files = new Files().Add(SkinPath(0), new[] { "DATA/Characters/Test/Fx.bin" }, Skin("Skin0", r), r).Add("DATA/Characters/Test/Fx.bin", sys);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        files.OnRead = path => { if (path == SkinPath(0)) { entered.Set(); release.Wait(TimeSpan.FromSeconds(20)); } };
        var scanner = new SkinColorScanner();
        var request = new SkinColorRequest(SkinPath(0), files.Read, Resolve, IncludeSharing: false);

        // Dedicated threads, not Task.Run: both scans BLOCK (one in the read hook, one on the scanner's gate), and in a full
        // suite the shared pool can be saturated for longer than the 10 s below - the first full run with this test failed
        // with "never started reading" because the pool had no thread to give it, not because the scanner was wrong.
        static Task<T> OwnThread<T>(Func<T> work) =>
            Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var running = OwnThread(() => scanner.Scan(request));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the first scan never started reading");

        using var cts = new CancellationTokenSource();
        var queued = OwnThread(() => scanner.Scan(request, cts.Token));
        await Task.Delay(150);
        Assert.False(queued.IsCompleted);                          // it is waiting for its turn, not scanning
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(running.IsCompleted);                         // freed while the scan ahead of it still holds the scanner

        release.Set();
        Assert.Single((await running.WaitAsync(TimeSpan.FromSeconds(20))).Effects);
        files.OnRead = null;
        Assert.Single(scanner.Scan(request).Effects);              // and the scanner is usable again
    }

    [Fact]
    public void ACancelledFirstScanIsStillRegisteredForEviction_SoTheCacheStaysBounded()
    {
        string Bin(string character) => $"data/characters/{character}/skins/skin0.bin";
        var files = new Files();
        foreach (string character in new[] { "alpha", "beta", "gamma", "delta" }) files.Add(Bin(character), Skin("Skin0", null));
        var scanner = new SkinColorScanner();
        SkinColorInventory ScanOf(string character, CancellationToken ct = default) =>
            scanner.Scan(new SkinColorRequest(Bin(character), files.Read, Resolve), ct);

        // the first scan of "alpha" is cancelled while it reads the character's other skins - after it has filled its cache
        using var cts = new CancellationTokenSource();
        files.OnRead = path => { if (path.EndsWith("/skin1.bin", StringComparison.Ordinal)) cts.Cancel(); };
        Assert.ThrowsAny<OperationCanceledException>(() => ScanOf("alpha", cts.Token));
        files.OnRead = null;
        Assert.Equal(new[] { "alpha" }, scanner.CachedCharacters);          // it is on the books, though its scan never finished

        ScanOf("beta");
        ScanOf("gamma");
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, scanner.CachedCharacters);
        ScanOf("delta");
        Assert.Equal(new[] { "beta", "gamma", "delta" }, scanner.CachedCharacters);   // the aborted one was the oldest, and went

        // a scan that throws for another reason is registered the same way
        files.Faulty.Add(Bin("alpha"));
        Assert.Single(scanner.Scan(new SkinColorRequest(Bin("alpha"), files.Read, Resolve)).Warnings);   // unreadable skin: a warning, not a throw
        Assert.Equal(new[] { "gamma", "delta", "alpha" }, scanner.CachedCharacters);
    }

    [Fact]
    public void ANameTheDictionaryLearnsLaterIsUsedByTheNextScan_NotTheOneTheBinWasCachedWith()
    {
        // an emitter whose sprite is a chunk link nothing can name yet, in a system with no particleName
        const ulong link = 0x1122334455667788UL;
        var sys = new BinTreeObject(H("S"), H("VfxSystemDefinitionData"), new BinTreeProperty[]
        {
            new BinTreeContainer(H("complexEmitterDefinitionData"), BinPropertyType.Embedded, new BinTreeProperty[]
            {
                Emitter("e", new BinTreeWadChunkLink(H("texture"), link)),
            }),
        });
        var r = Resolver("R", ("fx", sys));
        var files = new Files().Add(SkinPath(0), new[] { "DATA/Characters/Test/Fx.bin" }, Skin("Skin0", r), r).Add("DATA/Characters/Test/Fx.bin", sys);
        var scanner = new SkinColorScanner();

        var before = Scan(files, scanner: scanner, sharing: false);
        Assert.Equal("0x1122334455667788", before.EffectTextures.Single().Path);
        Assert.StartsWith("0x", before.Effects.Single().Name);

        // a hash sync later the dictionary knows both; the bins did not change, so the parse is the cached one
        const string learned = "assets/characters/test/skins/base/learned.tex";
        const string learnedSystem = "Characters/Test/Skins/Skin0/Particles/S";
        var after = Scan(files, scanner: scanner, sharing: false, wadPath: h => h == link ? learned : null,
            name: h => h == H("S") ? learnedSystem : Resolve(h));

        Assert.Equal(learned, after.EffectTextures.Single().Path);
        Assert.Equal(link, after.EffectTextures.Single().Hash);                              // the chunk is the same one
        Assert.Equal(learned, after.Effects.Single().Emitters.Single().Textures.Single().Path);
        Assert.Equal(learnedSystem, after.Effects.Single().Name);
        // and a scan before the sync is not changed by a scan after it
        Assert.Equal("0x1122334455667788", Scan(files, scanner: scanner, sharing: false).EffectTextures.Single().Path);
    }

    [Fact]
    public void ThePathHelpersAndLabelsAreWhatTheCardAndTheBrowserSpeak()
    {
        Assert.True(SkinColorScanner.TryParseSkinPath("data/characters/lillia/skins/skin49.bin", out string folder, out int number));
        Assert.Equal(("lillia", 49), (folder, number));
        Assert.True(SkinColorScanner.TryParseSkinPath(@"DATA\Characters\Lillia\Skins\Skin0.bin", out folder, out number));
        Assert.Equal(("Lillia", 0), (folder, number));
        Assert.False(SkinColorScanner.TryParseSkinPath("data/characters/lillia/skins/root.bin", out _, out _));
        Assert.False(SkinColorScanner.TryParseSkinPath("data/characters/lillia/lillia.bin", out _, out _));
        Assert.False(SkinColorScanner.TryParseSkinPath("assets/x/skins/skin3.bin", out _, out _));

        Assert.Equal("Petals of Spring Lillia (skin46)", SkinColorScanner.SkinLabel(46, "Petals of Spring Lillia"));
        Assert.Equal("skin46", SkinColorScanner.SkinLabel(46, null));
        Assert.Equal("skin46", SkinColorScanner.SkinLabel(46, "  "));
    }
}
