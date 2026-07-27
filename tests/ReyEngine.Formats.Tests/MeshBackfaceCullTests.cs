using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M209 (tier 2.11). Mesh particles are backface-culled, and the front face is CLOCKWISE.
///
/// <para>The winding was decided in the app, not here: M182's probe said CCW, a second probe using an
/// occluding wall disagreed, and M208 replaced both with a three-way A/B over a real Riot mesh that does
/// not set the flag (<c>Aatrox_Skin33_Back_Turbine_mesh.scb</c>, a closed turbine). On a closed mesh the
/// correct winding is the one indistinguishable from no culling: <b>front=CW matched, front=CCW rendered
/// hollow.</b> A GL winding cannot be asserted without a GL context, so that half stays a manual result.</para>
///
/// <para>What these tests DO pin is the data claim the whole change rests on: <b>absent means culled</b>.
/// <c>disableBackfaceCull</c> is Bool and true in 100% of its occurrences, so by the omit-defaults rule its
/// default is false. Invert that reading and the effect is not subtle - it culls exactly the 247,882
/// mesh emitters that asked to keep both faces, and stops culling the 171,153 that did not.</para>
/// </summary>
public class MeshBackfaceCullTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    /// <summary>A system with two emitters: one that sets the flag and one that omits it, which is exactly
    /// the pair the renderer has to tell apart.</summary>
    private static byte[] Bin()
    {
        BinTreeProperty[] Emitter(string name, bool? flag)
        {
            var props = new List<BinTreeProperty>
            {
                new BinTreeString(H("emitterName"), name),
                // a texture path is what makes the emitter count as visual downstream
                new BinTreeString(H("texture"), "ASSETS/Test/p.dds"),
            };
            if (flag is { } f) props.Add(new BinTreeBool(H("disableBackfaceCull"), f));
            return props.ToArray();
        }

        var emitters = new BinTreeContainer(H("complexEmitterDefinitionData"), BinPropertyType.Struct, new BinTreeProperty[]
        {
            new BinTreeStruct(0, H("VfxEmitterDefinitionData"), Emitter("keeps_both_faces", true)),
            new BinTreeStruct(0, H("VfxEmitterDefinitionData"), Emitter("gets_culled", null)),
        });

        var tree = new BinTree(new[]
        {
            new BinTreeObject(0xC011u, H("VfxSystemDefinitionData"), new BinTreeProperty[]
            {
                new BinTreeString(H("particleName"), "cull_test"),
                emitters,
            }),
        }, Array.Empty<string>());

        using var ms = new MemoryStream();
        tree.Write(ms);
        return ms.ToArray();
    }

    private static VfxEmitterDefinition Emitter(string name)
    {
        var systems = VfxSystemResolver.ExtractAll(Bin());
        var all = systems.SelectMany(s => s.Value.Emitters).ToList();
        Assert.Equal(2, all.Count);
        return all.Single(e => e.Name == name);
    }

    [Fact]
    public void AnAuthoredFlagIsRead() =>
        Assert.True(Emitter("keeps_both_faces").DisableBackfaceCull,
            "the emitter authored disableBackfaceCull=true and must keep both faces");

    [Fact]
    public void AnAbsentFlagMeansCulled() =>
        Assert.False(Emitter("gets_culled").DisableBackfaceCull,
            "absent means the default, and the default is false - inverting this culls the 247,882 "
            + "emitters that asked to keep both faces and spares the 171,153 that did not");
}
