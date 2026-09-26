using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M778: VfxPrimitiveRay draws its own placement-oriented streak (Crepe_Brush_Smoke_Piltover_1's
/// RaySmoke2, and the census below) instead of falling back to a camera billboard.
/// </summary>
public sealed class VfxRayPrimitiveTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    /// <summary>One system with one emitter carrying the given <c>primitive</c> struct, through the real
    /// resolver a map/champion bin goes through.</summary>
    private static VfxEmitterDefinition ParseEmitter(string primitiveClass)
    {
        var primitive = new BinTreeStruct(H("primitive"), H(primitiveClass), System.Array.Empty<BinTreeProperty>());
        var emitter = new BinTreeStruct(0, H("VfxEmitterDefinitionData"), new BinTreeProperty[]
        {
            new BinTreeString(H("emitterName"), "RaySmoke2"),
            primitive,
        });
        var system = new BinTreeObject(H("sys"), H("VfxSystemDefinitionData"), new BinTreeProperty[]
        {
            new BinTreeString(H("particleName"), "sys"),
            new BinTreeContainer(H("complexEmitterDefinitionData"), BinPropertyType.Struct,
                new BinTreeProperty[] { emitter }),
        });
        using var ms = new MemoryStream();
        new BinTree(new[] { system }, Array.Empty<string>()).Write(ms);

        var systems = VfxSystemResolver.ExtractAll(ms.ToArray());
        return systems.Values.Single().Emitters.Single();
    }

    [Fact]
    public void TheResolverMarksAVfxPrimitiveRayEmitterAsRay()
    {
        var e = ParseEmitter("VfxPrimitiveRay");
        Assert.True(e.IsRay);
        Assert.False(e.IsMeshPrimitive);
        Assert.False(e.IsArbitraryQuad);
        Assert.Equal(H("VfxPrimitiveRay"), e.PrimitiveClass);
    }

    [Fact]
    public void OtherPrimitivesAreNotMarkedAsRay()
    {
        Assert.False(ParseEmitter("VfxPrimitiveArbitraryQuad").IsRay);
        Assert.False(ParseEmitter("VfxPrimitiveMesh").IsRay);
        Assert.False(ParseEmitter("VfxPrimitiveCameraTrail").IsRay);
    }

    // ===================================================== the census

    private const string Shipping = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping";

    /// <summary>How many emitters in the shipped map WADs use VfxPrimitiveRay. Gated on the game install -
    /// silently does nothing on a machine (or cloud agent) without it, exactly like the rest of this
    /// suite's real-asset tests.</summary>
    [Fact]
    public void CensusOfVfxPrimitiveRayAcrossTheShippedMapWads()
    {
        if (!Directory.Exists(Shipping)) return;

        int bins = 0, systems = 0, emitters = 0, rayEmitters = 0;
        var examples = new List<string>();
        foreach (string wadPath in Directory.EnumerateFiles(Shipping, "*.wad.client")
                     .Where(f => !Path.GetFileNameWithoutExtension(f).Contains('_')))
        {
            WadArchive wad;
            try { wad = WadArchive.Open(wadPath); } catch { continue; }
            using (wad)
                // No hash database/resolver here, so entry.Path/IsResolved are not usable to find ".bin"
                // files by name (BlendFactorGuardTests' census hits the same wall) - every entry is tried
                // instead, pre-filtered by the raw PROP magic every .bin container starts with.
                foreach (var entry in wad.Entries)
                {
                    byte[] bytes;
                    try { bytes = wad.Extract(entry.PathHash); } catch { continue; }
                    if (bytes.Length < 4 || bytes[0] != 'P' || bytes[1] != 'R' || bytes[2] != 'O' || bytes[3] != 'P') continue;
                    IReadOnlyDictionary<uint, VfxSystemDefinition> parsed;
                    try { parsed = VfxSystemResolver.ExtractAll(bytes); } catch { continue; }
                    if (parsed.Count == 0) continue;
                    bins++;
                    foreach (var sys in parsed.Values)
                    {
                        systems++;
                        foreach (var e in sys.Emitters)
                        {
                            emitters++;
                            if (!e.IsRay) continue;
                            rayEmitters++;
                            if (examples.Count < 10) examples.Add($"{Path.GetFileName(wadPath)} {sys.Name}/{e.Name}");
                        }
                    }
                }
        }
        if (emitters == 0) return;   // nothing resolvable - not a real install

        Console.WriteLine($"VfxPrimitiveRay census: {rayEmitters} of {emitters} emitters across {systems} systems "
                           + $"in {bins} VFX bins under {Shipping}");
        foreach (var ex in examples) Console.WriteLine("  e.g. " + ex);
        Assert.True(rayEmitters > 0, "expected at least one VfxPrimitiveRay emitter in the shipped maps");
    }
}
