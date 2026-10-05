using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.App.Services;
using ReyEngine.Core.Build;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.LtkGameData;
using static ReyEngine.Formats.Tests.OverlayKit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M826: the Chroma Studio's EFFECT colour recolour of a bin the imported LTK GameData changes. It saves through the same bin save path as every editor, so the GameData
/// guards apply: a colour inside a particle system is a value in a list of embedded structs, which a declaration can express only if it can address the struct - the test records
/// what the path does and what the card is told. A synthetic game and package (the bin is a skin that also holds one VFX system).
/// </summary>
public sealed partial class LtkEditFlowTests
{
    private static readonly Vector4 EffectOrange = new(1f, 0.45f, 0.05f, 0.8f);

    /// <summary>The bin the GameData names (it holds <c>Test/Obj/A</c>) that also carries a skin and one effect system.</summary>
    private static byte[] BinNamedByTheGameDataWithAnEffect()
    {
        var test = new BinTreeObject(Hash("Test/Obj/A"), Hash("TestClass"), new BinTreeProperty[]
        {
            new BinTreeContainer(Hash("tags"), BinPropertyType.String, new[] { (BinTreeProperty)new BinTreeString(0, "g1") }),
            new BinTreeI32(Hash("count"), 1),
        });
        var system = EffectColorsTests.SystemObject(EffectColorsTests.Sys, "Zz_Skin1_Q",
            EffectColorsTests.Emitter("Flame", EffectColorsTests.ValueColor("birthColor", EffectOrange, new[] { new Vector4(1f, 0f, 0f, 1f), new Vector4(0f, 0f, 1f, 0f) })));
        using var ms = new MemoryStream();
        new BinTree(new[] { test, system }, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    private static ChromaEffectColorRef EffectRef(string field = "birthColor") => new() { System = EffectColorsTests.Sys, Emitter = 0, Field = field, Bin = A, SystemName = "Zz_Skin1_Q" };

    private static IReadOnlyList<ChromaEffectColorRef> EffectRefs(params ChromaEffectColorRef[] refs) => refs;

    private static Vector4? BirthColor(byte[] bin) =>
        SkinEffectColors.Read(bin).Fields.Single(f => f.Key == new EffectColorKey(EffectColorsTests.Sys, 0, "birthColor")).Constant;

    [Fact]
    public async Task An_effect_colour_recolour_of_a_bin_the_GameData_changes_is_refused_with_its_reason_and_writes_nothing()
    {
        // The synthetic GameData of this fixture carries no schema, so a colour inside a particle system cannot be addressed by a declaration: the save is REFUSED. (A schema that can address it
        // would keep the recolour as a module in the layer that applies last; that outcome needs a real mod and is not what this fixture builds.)
        var project = Package(Game("g-chroma-fx", BinNamedByTheGameDataWithAnEffect()));
        var vm = await Open(project);
        Assert.Equal(new[] { "g1", "base", "fix" }, TagsOf(Read(vm, A), "Test/Obj/A"));
        var riot = BirthColor(Read(vm, A));
        var filesBefore = Files(project);
        var transform = new ColorTransform { HueShiftDegrees = 120f };

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await (Task<ChromaEffectSaveResult>)Call(vm, "SaveChromaEffectColorsAsync", A, transform, EffectRefs(EffectRef()), EffectRefs())!);

        Assert.Contains("cannot be saved on top of the mod's GameData", refused.Message);
        Assert.Equal(filesBefore, Files(project));                                       // nothing was written
        Assert.Null(project.ChromaEffectRecolors);                                       // and there is no recipe for a recolour that was not kept
        Assert.Equal(riot, BirthColor(Read(vm, A)));
        Assert.Equal(new[] { "g1", "base", "fix" }, TagsOf(Read(vm, A), "Test/Obj/A"));
    }

    [Fact]
    public async Task On_a_bin_the_GameData_changes_the_effect_fields_are_listed_recolourable_but_say_why_they_start_off()
    {
        var project = Package(Game("g-chroma-fx-list", BinNamedByTheGameDataWithAnEffect()));
        var vm = await Open(project);
        var systems = new List<EffectSystemEntry>
        {
            new(EffectColorsTests.Sys, "Zz_Skin1_Q", "particles/zz/q", new uint[] { 1 }, new[] { "effect key" }, A, Array.Empty<ReyEngine.Formats.Vfx.VfxColorEmitter>(), Array.Empty<string>()),
        };
        var snapshot = (ChromaEffectSnapshot)Call(vm, "ReadChromaEffectSnapshot", A, systems, new HashSet<EffectColorKey>(), ColorTransform.Identity)!;
        var field = Assert.Single(snapshot.Fields);
        Assert.True(field.Recolourable);
        Assert.NotNull(field.DefaultOffReason);
        Assert.Contains("GameData", field.DefaultOffReason);
        Assert.NotNull(snapshot.Preview);                                              // the live preview is prepared all the same
        Assert.Equal("", snapshot.Problem);
    }
}
