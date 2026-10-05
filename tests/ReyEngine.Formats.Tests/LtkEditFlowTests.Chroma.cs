using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.Services;
using ReyEngine.Core.Build;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Diagnostics;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.LtkGameData;
using ReyEngine.Formats.Materials;
using static ReyEngine.Formats.Tests.OverlayKit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M825: the Chroma Studio's colour parameter recolour of a skin bin the imported LTK GameData changes. The recolour saves through the bin save path
/// like every editor, so it is kept as a declaration on top of the package's modules - never as a file - and a revert takes it away again. A
/// synthetic game and package (the bin is a skin with one material); the real files are <see cref="ChromaParametersRealDataTests"/>.
/// </summary>
public sealed partial class LtkEditFlowTests
{
    private const string SkinMaterial = "Characters/Zz/Skins/Skin1/Materials/Zz_Skin1_Body_inst";

    private static uint Hash(string s) => ReyEngine.Core.Hashing.HashAlgorithms.Fnv1a(s);

    private static BinTreeEmbedded ColourParam(string name, Vector4 value) =>
        new(0, Hash("StaticMaterialShaderParamDef"), new BinTreeProperty[] { new BinTreeString(Hash("name"), name), new BinTreeVector4(Hash("value"), value) });

    /// <summary>A bin the GameData names (it holds <c>Test/Obj/A</c>, the object the package's modules tag) that is also a skin: one material with
    /// colour parameters and a skin block pointing at it.</summary>
    private static byte[] SkinBinNamedByTheGameData()
    {
        var test = new BinTreeObject(Hash("Test/Obj/A"), Hash("TestClass"), new BinTreeProperty[]
        {
            new BinTreeContainer(Hash("tags"), BinPropertyType.String, new[] { (BinTreeProperty)new BinTreeString(0, "g1") }),
            new BinTreeI32(Hash("count"), 1),
        });
        var material = new BinTreeObject(Hash(SkinMaterial), Hash("StaticMaterialDef"), new BinTreeProperty[]
        {
            new BinTreeString(Hash("name"), SkinMaterial),
            new BinTreeUnorderedContainer(Hash("paramValues"), BinPropertyType.Embedded, new BinTreeProperty[]
            {
                ColourParam("TintColor", new Vector4(1f, 0.25f, 0.1f, 1f)),
                ColourParam("OutlineColor", new Vector4(0.2f, 0.4f, 0.8f, 0.6f)),
                ColourParam("VColor_G_Mask_Discard_Size", new Vector4(100f, 0f, 0f, 0f)),
            }),
        });
        var skin = new BinTreeObject(Hash("Characters/Zz/Skins/Skin1"), Hash("SkinCharacterDataProperties"), new BinTreeProperty[]
        {
            new BinTreeEmbedded(Hash("skinMeshProperties"), Hash("SkinMeshDataProperties"), new BinTreeProperty[]
            {
                new BinTreeString(Hash("simpleSkin"), "assets/characters/zz/skins/base/zz.skn"),
                new BinTreeString(Hash("skeleton"), "assets/characters/zz/skins/base/zz.skl"),
                new BinTreeObjectLink(Hash("material"), material.PathHash),
                new BinTreeColor(Hash("reflectionFresnelColor"), new LeagueToolkit.Core.Primitives.Color(0.9f, 0.3f, 0.1f, 1f)),
            }),
        });
        using var ms = new MemoryStream();
        new BinTree(new[] { test, skin, material }, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    private static IReadOnlyDictionary<string, Vector4> Colours(byte[] bin) =>
        SkinColorParameters.Read(bin, ChromaNames).ToDictionary(p => p.Key.Name, p => p.Value);

    private static readonly Dictionary<uint, string> ChromaKnown = new[] { "StaticMaterialDef", "skeleton", "simpleSkin", "material", "skinMeshProperties", "reflectionFresnelColor" }
        .ToDictionary(Hash);
    private static string? ChromaNames(uint h) => ChromaKnown.TryGetValue(h, out var n) ? n : null;

    private static ChromaParameterRef Ref(string name) => new() { Material = Hash(SkinMaterial), MaterialName = SkinMaterial, Name = name };

    private static ChromaParameterRef SkinBlockRef(string name) => new() { Material = 0, MaterialName = "(skin default texture)", Name = name };

    private static IReadOnlyList<ChromaParameterRef> Refs(params ChromaParameterRef[] refs) => refs;

    [Fact]
    public async Task A_colour_parameter_recolour_of_a_bin_the_GameData_changes_is_kept_as_a_declaration_and_a_revert_takes_it_away()
    {
        var project = Package(Game("g-chroma", SkinBinNamedByTheGameData()));
        var vm = await Open(project);
        var log = CaptureLog(vm);
        Assert.Equal(new[] { "g1", "base", "fix" }, TagsOf(Read(vm, A), "Test/Obj/A"));        // the package's modules are on the bin the editor serves
        var riot = Colours(Read(vm, A));
        var filesBefore = Files(project);
        var transform = new ColorTransform { HueShiftDegrees = 120f };
        var key = SkinBlockRef("reflectionFresnelColor");

        var result = await (Task<ChromaParamSaveResult>)Call(vm, "SaveChromaParametersAsync", A, transform, Refs(key), Refs())!;

        Assert.Equal(1, result.Written);
        // kept as a module in the layer that applies last, never as a file
        var module = Assert.Single(GameDataDocumentText.Read(File.ReadAllText(StoreFile(project, "fix"))).Modules);
        Assert.Equal(A, module.Target);
        Assert.Equal(filesBefore, Files(project));
        // the editor serves the package's tags AND the recolour, derived from the value the bin had
        byte[] served = Read(vm, A);
        Assert.Equal(new[] { "g1", "base", "fix" }, TagsOf(served, "Test/Obj/A"));
        var now = Colours(served);
        Assert.InRange(now["reflectionFresnelColor"].Y, transform.Apply(riot["reflectionFresnelColor"]).Y - 0.005f, transform.Apply(riot["reflectionFresnelColor"]).Y + 0.005f);   // a Color is stored in bytes
        Assert.Equal(riot["reflectionFresnelColor"].W, now["reflectionFresnelColor"].W);
        Assert.Equal(riot["TintColor"], now["TintColor"]);                                    // nothing else moved
        Assert.Contains(Lines(log, "GameData"), l => l.Level == LogLevel.Success && l.Message.StartsWith("Saved a.bin as a declaration on top of the mod's GameData", StringComparison.Ordinal));
        var record = Assert.Single(project.ChromaParameterRecolors!);
        Assert.Equal(A, record.ChromaSkin);
        Assert.Equal("reflectionFresnelColor", Assert.Single(record.Parameters).Name);

        // a second save of another transform is Riot's value through THAT transform, not a recolour of the recolour
        var other = new ColorTransform { HueShiftDegrees = 200f, Saturation = 0.8f };
        await (Task<ChromaParamSaveResult>)Call(vm, "SaveChromaParametersAsync", A, other, Refs(key), Refs())!;
        Assert.InRange(Colours(Read(vm, A))["reflectionFresnelColor"].X, other.Apply(riot["reflectionFresnelColor"]).X - 0.005f, other.Apply(riot["reflectionFresnelColor"]).X + 0.005f);
        Assert.Single(GameDataDocumentText.Read(File.ReadAllText(StoreFile(project, "fix"))).Modules);

        // revert: the bin is the package's again, the module goes, and so does the record
        int reverted = await (Task<int>)Call(vm, "RevertChromaParametersAsync", A, Refs(key))!;
        Assert.Equal(1, reverted);
        Assert.Equal(riot["reflectionFresnelColor"], Colours(Read(vm, A))["reflectionFresnelColor"]);
        Assert.Equal(new[] { "g1", "base", "fix" }, TagsOf(Read(vm, A), "Test/Obj/A"));
        Assert.False(File.Exists(StoreFile(project, "fix")) && GameDataDocumentText.Read(File.ReadAllText(StoreFile(project, "fix"))).Modules.Count > 0,
            "the revert left the recolour's module in the layer");
        Assert.Null(project.ChromaParameterRecolors);
        Assert.Equal(filesBefore, Files(project));
    }

    [Fact]
    public async Task A_recolour_the_declarations_cannot_express_is_refused_with_the_reason_and_nothing_is_written()
    {
        var project = Package(Game("g-chroma-refused", SkinBinNamedByTheGameData()));
        var vm = await Open(project);
        var riot = Colours(Read(vm, A));
        var filesBefore = Files(project);
        var transform = new ColorTransform { HueShiftDegrees = 120f };

        // a material's paramValues is a list of structs: LTK types it by the class schema, and the synthetic installation has none
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            (Task<ChromaParamSaveResult>)Call(vm, "SaveChromaParametersAsync", A, transform, Refs(Ref("TintColor")), Refs())!);

        Assert.Contains("cannot be saved on top of the mod's GameData", ex.Message);
        Assert.Contains("Nothing of the recolour was kept in the project", ex.Message);
        Assert.Equal(filesBefore, Files(project));
        Assert.False(File.Exists(StoreFile(project, "fix")));
        Assert.Null(project.ChromaParameterRecolors);                                          // no recipe for a recolour that was not kept
        Assert.Equal(riot["TintColor"], Colours(Read(vm, A))["TintColor"]);
    }
    [Fact]
    public async Task On_a_bin_the_GameData_changes_a_materials_parameters_start_off_so_a_flush_never_throws_by_default()
    {
        var project = Package(Game("g-chroma-default", SkinBinNamedByTheGameData()));
        var vm = await Open(project);
        var card = vm.MeshPreview;
        card.ChromaUiPost = a => { a(); return Task.CompletedTask; };
        card.UseDx11Preview = false;
        card.ScanSkinColours = (_, _) => Task.FromResult(SkinColorInventory.Empty(A, ""));
        card.SetChromaSkin(A);
        await card.ScanColoursCommand.ExecuteAsync(null);
        await card.ChromaIdleAsync();

        var rows = card.ChromaParameters.ToDictionary(r => r.Key.Name);
        Assert.False(rows["TintColor"].IsIncluded);                                     // a material's parameters: a declaration needs LTK's class schema
        Assert.True(rows["TintColor"].CanInclude);                                      // but they can be switched on
        Assert.Contains("GameData", rows["TintColor"].Note);
        Assert.False(rows["OutlineColor"].IsIncluded);
        Assert.True(rows["reflectionFresnelColor"].IsIncluded);                         // a property of the skin block is expressible
        Assert.False(rows["VColor_G_Mask_Discard_Size"].CanInclude);

        // the default set saves, and Ctrl+S / Build / Export flush it without a throw
        card.ChromaHue = 120;
        Assert.True(card.HasPendingChromaRecolour);
        await (Task)Call(vm, "SavePendingEditorEdits")!;
        Assert.False(card.ChromaRecolourDirty, card.ChromaRecolourStatus);

        // switching a refused one on is the person's choice: the save then says why, and the auto-save does not try the same state again
        rows["TintColor"].IsIncluded = true;
        card.ChromaHue = 130;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => (Task)Call(vm, "SavePendingEditorEdits")!);
        Assert.Contains("cannot be saved on top of the mod's GameData", ex.Message);
        Assert.True(card.ChromaRecolourDirty);
        Assert.False(card.ChromaAutoSaveDue);
        rows["TintColor"].IsIncluded = false;
        await (Task)Call(vm, "SavePendingEditorEdits")!;                                 // off again: the flush is clean
        Assert.False(card.ChromaRecolourDirty, card.ChromaRecolourStatus);
    }
}
