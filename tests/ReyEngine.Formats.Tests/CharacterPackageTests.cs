using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Meta;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>M696: the bins a new character is given have the shape of Riot's own decorative props (the
/// S3Yonkey on the jade map is the template, field for field and form for form), the editor's own readers
/// find the mesh, the skeleton, the texture and the clips in them, and a created placement is the one
/// every scenery character on the shipped maps has.</summary>
public sealed class CharacterPackageTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static readonly CharacterPackageSpec Spec = new(
        "MyGolem", "MyGolem.skn", "MyGolem.skl", "MyGolem_TX_CM.tex",
        new[]
        {
            new CharacterClipSpec("Idle1", "mygolem_idle1.anm", Loop: true),
            new CharacterClipSpec("Attack1", "mygolem_attack1.anm", Loop: false),
            new CharacterClipSpec("Death", "mygolem_death.anm", Loop: false),
        },
        SkinScale: 1.5f, SelfIllumination: 1f);

    /// <summary>Names for every hash the package writes, so the text form reads like ritobin's.</summary>
    private static readonly string[] KnownNames =
    {
        "CharacterRecord", "mCharacterName", "useableData", "flags", "SkinCharacterMetaDataProperties",
        "SkinCharacterDataProperties", "championSkinName", "skinAnimationProperties", "animationGraphData",
        "skinMeshProperties", "SkinMeshDataProperties", "skeleton", "simpleSkin", "texture", "skinScale",
        "selfIllumination", "emoteBuffbone", "godrayFXbone", "objectPath", "mClipDataMap", "AtomicClipData",
        "mFlags", "mTrackDataName", "mAnimationResourceData", "AnimationResourceData", "mAnimationFilePath",
        "mTrackDataMap", "TrackData", "Default", "Idle1", "Attack1", "Death",
        "Characters/MyGolem/CharacterRecords/Root", "Characters/MyGolem/Skins/Meta",
        "Characters/MyGolem/Skins/Skin0", "Characters/MyGolem/Animations/Skin0",
    };

    private static string? Name(uint hash) => KnownNames.FirstOrDefault(n => H(n) == hash);

    private static string Text(CharacterPackage package, string binPath, out BinTree tree)
    {
        var file = package.Bins.Single(b => b.Path == binPath);
        tree = new BinTree(new MemoryStream(file.Bytes, false));
        var wadPaths = package.AllPaths.ToDictionary(HashAlgorithms.WadPath, p => p);
        return RitobinText.Write(tree, Name, h => wadPaths.GetValueOrDefault(h)).Replace("\r\n", "\n").TrimEnd();
    }

    [Fact]
    public void TheNameBecomesAFolderAndAnObjectPathSoItIsChecked()
    {
        Assert.Null(CharacterPackageBuilder.ValidateName("Sru_Gromp_Prop2"));
        Assert.NotNull(CharacterPackageBuilder.ValidateName(""));
        Assert.NotNull(CharacterPackageBuilder.ValidateName("9lives"));
        Assert.NotNull(CharacterPackageBuilder.ValidateName("my golem"));
        Assert.NotNull(CharacterPackageBuilder.ValidateName("a/b"));
        Assert.NotNull(CharacterPackageBuilder.ValidateName(new string('x', 65)));
        Assert.Throws<ArgumentException>(() => CharacterPackageBuilder.Build(Spec with { Name = "no way" }));
        Assert.Throws<ArgumentException>(() => CharacterPackageBuilder.Build(Spec with { SkinFileName = "sub/dir.skn" }));
        Assert.Throws<ArgumentException>(() => CharacterPackageBuilder.Build(Spec with
        {
            Clips = new[] { new CharacterClipSpec("Idle1", "a.anm", true), new CharacterClipSpec("idle1", "b.anm", true) },
        }));
        Assert.Equal("Idle1", CharacterPackageBuilder.PickIdle(Spec.Clips));
        Assert.Equal("idle_bored", CharacterPackageBuilder.PickIdle(new[] { new CharacterClipSpec("Run", "r.anm", true), new CharacterClipSpec("idle_bored", "i.anm", true) }));
        Assert.Equal("Run", CharacterPackageBuilder.PickIdle(new[] { new CharacterClipSpec("Run", "r.anm", true) }));
        Assert.Null(CharacterPackageBuilder.PickIdle(Array.Empty<CharacterClipSpec>()));
    }

    [Fact]
    public void TheBinsHaveTheShapeOfRiotsDecorativeProps()
    {
        var package = CharacterPackageBuilder.Build(Spec);
        Assert.Equal("Characters/MyGolem/CharacterRecords/Root", package.CharacterRecord);
        Assert.Equal("Characters/MyGolem/Skins/Skin0", package.Skin);
        Assert.Equal("Idle1", package.IdleClip);
        Assert.Equal(new[] { "data/characters/mygolem/mygolem.bin", "data/characters/mygolem/skins/skin0.bin", "data/characters/mygolem/animations/skin0.bin" },
            package.Bins.Select(b => b.Path));
        Assert.Equal("assets/characters/mygolem/skins/base/mygolem.skn", package.SkinPath);
        Assert.Equal("assets/characters/mygolem/skins/base/mygolem.skl", package.SkeletonPath);
        Assert.Equal("assets/characters/mygolem/skins/base/mygolem_tx_cm.tex", package.TexturePath);
        Assert.Equal("assets/characters/mygolem/skins/base/animations/mygolem_idle1.anm", package.ClipPaths["Idle1"]);

        // the record: the Yonkey's two fields and the Meta object every character bin carries
        Assert.Equal("""
            #PROP_text

            type: string = "PROP"
            version: u32 = 3
            linked: list[string] = {
            }
            entries: map[hash,embed] = {
              "Characters/MyGolem/CharacterRecords/Root" = CharacterRecord {
                mCharacterName: string = "MyGolem"
                useableData: embed = useableData {
                  flags: u32 = 0
                }
              }
              "Characters/MyGolem/Skins/Meta" = SkinCharacterMetaDataProperties {
              }
            }
            """.Replace("\r\n", "\n").TrimEnd(), Text(package, "data/characters/mygolem/mygolem.bin", out _));

        // the skin: linked to the record and the graph; strings where Riot writes strings, a chunk link
        // where Riot writes one
        Assert.Equal("""
            #PROP_text

            type: string = "PROP"
            version: u32 = 3
            linked: list[string] = {
              "DATA/Characters/MyGolem/MyGolem.bin"
              "DATA/Characters/MyGolem/Animations/Skin0.bin"
            }
            entries: map[hash,embed] = {
              "Characters/MyGolem/Skins/Skin0" = SkinCharacterDataProperties {
                championSkinName: string = "MyGolem"
                skinAnimationProperties: embed = skinAnimationProperties {
                  animationGraphData: link = "Characters/MyGolem/Animations/Skin0"
                }
                skinMeshProperties: embed = SkinMeshDataProperties {
                  skeleton: string = "ASSETS/Characters/MyGolem/Skins/Base/MyGolem.skl"
                  simpleSkin: string = "ASSETS/Characters/MyGolem/Skins/Base/MyGolem.skn"
                  texture: file = "assets/characters/mygolem/skins/base/mygolem_tx_cm.tex"
                  skinScale: f32 = 1.5
                  selfIllumination: f32 = 1
                }
                emoteBuffbone: string = ""
                godrayFXbone: string = ""
                objectPath: hash = "Characters/MyGolem/Skins/Skin0"
              }
            }
            """.Replace("\r\n", "\n").TrimEnd(), Text(package, "data/characters/mygolem/skins/skin0.bin", out _));

        // the graph: pointer clips on the embedded Default track; only the looping clip is flagged
        Assert.Equal("""
            #PROP_text

            type: string = "PROP"
            version: u32 = 3
            linked: list[string] = {
            }
            entries: map[hash,embed] = {
              "Characters/MyGolem/Animations/Skin0" = animationGraphData {
                mClipDataMap: map[hash,pointer] = {
                  "Idle1" = AtomicClipData {
                    mFlags: u32 = 2
                    mTrackDataName: hash = "Default"
                    mAnimationResourceData: embed = AnimationResourceData {
                      mAnimationFilePath: file = "assets/characters/mygolem/skins/base/animations/mygolem_idle1.anm"
                    }
                  }
                  "Attack1" = AtomicClipData {
                    mTrackDataName: hash = "Default"
                    mAnimationResourceData: embed = AnimationResourceData {
                      mAnimationFilePath: file = "assets/characters/mygolem/skins/base/animations/mygolem_attack1.anm"
                    }
                  }
                  "Death" = AtomicClipData {
                    mTrackDataName: hash = "Default"
                    mAnimationResourceData: embed = AnimationResourceData {
                      mAnimationFilePath: file = "assets/characters/mygolem/skins/base/animations/mygolem_death.anm"
                    }
                  }
                }
                mTrackDataMap: map[hash,embed] = {
                  "Default" = TrackData {
                  }
                }
                objectPath: hash = "Characters/MyGolem/Animations/Skin0"
              }
            }
            """.Replace("\r\n", "\n").TrimEnd(), Text(package, "data/characters/mygolem/animations/skin0.bin", out var graph));

        // wire forms the text cannot show: the clip values are pointers (0x82), the track an embed (0x83)
        var graphObject = graph.Objects[H("Characters/MyGolem/Animations/Skin0")];
        var clips = (BinTreeMap)graphObject.Properties[H("mClipDataMap")];
        Assert.Equal(BinPropertyType.Struct, clips.ValueType);
        Assert.All(clips, e => Assert.Equal(BinPropertyType.Struct, e.Value.Type));
        Assert.Equal(BinPropertyType.Embedded, ((BinTreeMap)graphObject.Properties[H("mTrackDataMap")]).ValueType);

        // a scale of 1 is absent, as on every shipped skin that does not scale
        var unscaled = CharacterPackageBuilder.Build(Spec with { SkinScale = 1f });
        Assert.DoesNotContain("skinScale", Text(unscaled, "data/characters/mygolem/skins/skin0.bin", out _));
    }

    [Fact]
    public void TheEditorsOwnReadersFindTheMeshTheTextureAndTheClips()
    {
        var package = CharacterPackageBuilder.Build(Spec);
        var files = package.Bins.ToDictionary(b => b.Path, b => b.Bytes, StringComparer.OrdinalIgnoreCase);
        var wadPaths = package.AllPaths.ToDictionary(HashAlgorithms.WadPath, p => p);
        string? Wad(ulong h) => wadPaths.GetValueOrDefault(h);
        byte[]? Read(string path) => files.GetValueOrDefault(path.Replace('\\', '/').ToLowerInvariant());

        var skinBin = files["data/characters/mygolem/skins/skin0.bin"];
        var mesh = SkinMeshExtractor.Extract(skinBin, Wad);
        Assert.NotNull(mesh);
        Assert.Equal("ASSETS/Characters/MyGolem/Skins/Base/MyGolem.skn", mesh!.SimpleSkin);
        Assert.Equal("ASSETS/Characters/MyGolem/Skins/Base/MyGolem.skl", mesh.Skeleton);
        Assert.Equal("assets/characters/mygolem/skins/base/mygolem_tx_cm.tex", mesh.DefaultTexture);
        // the paths the skin names hash to the paths the package stages at
        Assert.Equal(HashAlgorithms.WadPath(package.SkinPath), BinTexturePath.HashOfReference(mesh.SimpleSkin!));
        Assert.Equal(HashAlgorithms.WadPath(package.SkeletonPath), BinTexturePath.HashOfReference(mesh.Skeleton!));

        // the clips come from the skin's OWN graph, the way the viewport resolves a placed mob's (M679)
        var clips = PropAnimations.ResolveClips(skinBin, Read, Name, Wad);
        Assert.Equal(new[] { "Idle1", "Attack1", "Death" }, clips.Select(PropAnimations.DisplayName));
        Assert.Equal(package.ClipPaths["Idle1"], PropAnimations.PickIdle(clips)!.AnmPath);
        Assert.Equal(package.ClipPaths["Death"], clips[2].AnmPath);

        // and the skin's scale reaches the material document the viewport reads it from
        var doc = Materials.MaterialDocument.Parse(skinBin, Name, Wad);
        Assert.Equal(1.5f, doc.SkinMesh?.SkinScale);
    }

    private const uint ContainerHash = 0x5000u;

    private static byte[] BinWithContainer()
    {
        var items = new BinTreeMap(H("items"), BinPropertyType.Hash, BinPropertyType.Struct, new[]
        {
            new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, 0xAAAAAAAAu),
                new BinTreeStruct(0, H("MapAudio"), new BinTreeProperty[]
                {
                    new BinTreeMatrix44(H("transform"), Matrix4x4.CreateTranslation(1f, 2f, 3f)),
                    new BinTreeString(H("name"), "Existing"),
                    new BinTreeString(H("eventName"), "Play_sfx_Existing"),
                })),
        });
        var container = new BinTreeObject(ContainerHash, H("MapPlaceableContainer"), new BinTreeProperty[] { items });
        using var ms = new MemoryStream();
        new BinTree(new[] { container }, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    [Fact]
    public void ACreatedPlacementIsTheSceneryCharacterEveryShippedMapHas()
    {
        byte[] bin = BinWithContainer();
        var id = MapPlaceableWriter.NewParticleId(new BinTree(new MemoryStream(bin, false)), H("MyGolem_1"));
        Assert.True(id.IsValid);
        var transform = Matrix4x4.CreateTranslation(761.5f, 133.2f, 453.3f);
        var edit = new MapPlacementEdit(id)
        {
            CreateCharacter = true,
            Name = "MyGolem_1",
            Transform = transform,
            CharacterRecord = "Characters/MyGolem/CharacterRecords/Root",
            Skin = "Characters/MyGolem/Skins/Skin0",
            IdleAnimation = "Idle1",
        };
        byte[]? written = MapPlaceableWriter.WriteEdits(bin, new[] { edit }, out var error);
        Assert.Null(error);
        Assert.NotNull(written);

        // exactly Riot's four fields in Riot's forms: measured on 119 scenery placements, the name is a
        // hash, Character a pointer, CharacterMesh an embed
        var tree = new BinTree(new MemoryStream(written!, false));
        var items = (BinTreeMap)tree.Objects[ContainerHash].Properties[H("items")];
        var item = (BinTreeStruct)items.Single(e => ((BinTreeHash)e.Key).Value == id.ItemKey).Value;
        Assert.Equal(0x9aa5b4bcu, item.ClassHash);
        Assert.Equal(new[] { H("transform"), H("name"), H("Character"), H("CharacterMesh") }, item.Properties.Keys);
        Assert.Equal(transform, ((BinTreeMatrix44)item.Properties[H("transform")]).Value);
        Assert.Equal(H("MyGolem_1"), ((BinTreeHash)item.Properties[H("name")]).Value);
        var character = item.Properties[H("Character")];
        Assert.Equal(BinPropertyType.Struct, character.Type);
        Assert.Equal(H("SkinCharacterGeComponentDef"), ((BinTreeStruct)character).ClassHash);
        Assert.Equal("Characters/MyGolem/CharacterRecords/Root", ((BinTreeString)((BinTreeStruct)character).Properties[H("CharacterRecord")]).Value);
        Assert.Equal("Characters/MyGolem/Skins/Skin0", ((BinTreeString)((BinTreeStruct)character).Properties[H("Skin")]).Value);
        var characterMesh = item.Properties[H("CharacterMesh")];
        Assert.Equal(BinPropertyType.Embedded, characterMesh.Type);
        Assert.Equal(H("CharacterMeshGeComponentDef"), ((BinTreeStruct)characterMesh).ClassHash);
        Assert.Equal("Idle1", ((BinTreeString)((BinTreeStruct)characterMesh).Properties[H("IdleAnimationName")]).Value);

        // the extractor that lists a map's props sees it as one, beside the untouched sound
        var (_, props, sounds) = MapPlaceableExtractor.Extract(written!);
        var prop = Assert.Single(props);
        Assert.Equal("Characters/MyGolem/CharacterRecords/Root", prop.CharacterRecord);
        Assert.Equal("Characters/MyGolem/Skins/Skin0", prop.Skin);
        Assert.Equal("MyGolem", prop.CharacterName);
        Assert.Equal(transform.Translation, prop.Position);
        Assert.Equal(id, prop.Id);
        Assert.Single(sounds);

        // a second creation under the same key, or one without a transform or a skin, is refused
        Assert.Null(MapPlaceableWriter.WriteEdits(written!, new[] { edit }, out _));
        Assert.Null(MapPlaceableWriter.WriteEdits(bin, new[] { edit with { Transform = null } }, out _));
        Assert.Null(MapPlaceableWriter.WriteEdits(bin, new[] { edit with { Skin = null } }, out _));
        Assert.Null(MapPlaceableWriter.WriteEdits(bin, new[] { edit with { CharacterRecord = "" } }, out _));

        // a RENAME keeps the class's own wire form - a hash here, a string on a particle or a sound
        var renamed = MapPlaceableWriter.WriteEdits(written!, new[] { new MapPlacementEdit(id) { Name = "MyGolem_renamed" } }, out error);
        Assert.Null(error);
        var renamedItem = (BinTreeStruct)((BinTreeMap)new BinTree(new MemoryStream(renamed!, false)).Objects[ContainerHash].Properties[H("items")])
            .Single(e => ((BinTreeHash)e.Key).Value == id.ItemKey).Value;
        Assert.Equal(H("MyGolem_renamed"), Assert.IsType<BinTreeHash>(renamedItem.Properties[H("name")]).Value);
        var renamedSound = MapPlaceableWriter.WriteEdits(written!, new[] { new MapPlacementEdit(new MapPlacementId(ContainerHash, 0xAAAAAAAAu)) { Name = "Still a string" } }, out error);
        Assert.Null(error);
        var soundItem = (BinTreeStruct)((BinTreeMap)new BinTree(new MemoryStream(renamedSound!, false)).Objects[ContainerHash].Properties[H("items")])
            .Single(e => ((BinTreeHash)e.Key).Value == 0xAAAAAAAAu).Value;
        Assert.Equal("Still a string", Assert.IsType<BinTreeString>(soundItem.Properties[H("name")]).Value);

        // the ordinary verbs still edit it afterwards: a moved prop keeps its hashed name and its idle
        var moved = MapPlaceableWriter.WriteEdits(written!, new[] { new MapPlacementEdit(id) { Transform = Matrix4x4.CreateTranslation(1, 1, 1) } }, out error);
        Assert.Null(error);
        var movedItem = (BinTreeStruct)((BinTreeMap)new BinTree(new MemoryStream(moved!, false)).Objects[ContainerHash].Properties[H("items")])
            .Single(e => ((BinTreeHash)e.Key).Value == id.ItemKey).Value;
        Assert.IsType<BinTreeHash>(movedItem.Properties[H("name")]);
        Assert.Equal(Vector3.One, ((BinTreeMatrix44)movedItem.Properties[H("transform")]).Value.Translation);
    }
}
