using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Formats.Meta;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M531: porting a legacy map's particles - the systems and where they stand.
///
/// <para>The load-bearing facts these pin down, each measured rather than assumed: a system's identity
/// is FNV-1a of its particlePath (not its name); placements need the SAME translation the geometry got
/// and no rotation; and a batch import has to reserve item keys as it goes or all but the first
/// placement is silently dropped.</para>
/// </summary>
public sealed class LegacyParticlePortTests
{
    private const string Sandbox = @"K:\LeagueSandbox\League_Sandbox_Client";
    private static string Dat => Path.Combine(Sandbox, @"LEVELS\Map2\Particles.dat");
    private static string[] Folders => new[]
    {
        Path.Combine(Sandbox, @"DATA\Particles"),
        Path.Combine(Sandbox, @"DATA\Shared\Particles"),
    };

    private static bool Available => File.Exists(Dat) && Directory.Exists(Folders[0]);

    /// <summary>The translation the user's Map453 port actually used, measured off its imported meshes.</summary>
    private static readonly Vector3 Shift = new(1000.834f, -51.318f, 499.388f);

    [Fact]
    public void APlacementIsMovedByTheSameTranslationTheGeometryGot()
    {
        // Legacy particle coords share the NVR's space, and the porter applies translation only - no
        // rotation, no scale, no axis swap. So this is the whole of the position fix.
        var set = LegacyParticlePlacements.Parse("a.troy 100 200 300 -2147483648 0 0 0\n");
        var plan = LegacyParticlePorter.Plan(set, _ => Troy(), new Vector3(10, -5, 2));

        var p = Assert.Single(plan.Placements);
        Assert.Equal(new Vector3(110, 195, 302), p.Transform.Translation);

        // and nothing else: the rest of the matrix is untouched identity
        Assert.Equal(Matrix4x4.Identity with { Translation = p.Transform.Translation }, p.Transform);
    }

    [Fact]
    public void AnUnresolvableSystemDropsItsPlacementsAndSaysSo()
    {
        // A placement whose system link points at nothing is a hard error at map load, so dropping it is
        // the safe direction - but silently dropping it is not.
        var set = LegacyParticlePlacements.Parse("missing.troy 0 0 0 -2147483648 0 0 0\n");
        var plan = LegacyParticlePorter.Plan(set, _ => null, Vector3.Zero);

        Assert.Empty(plan.Placements);
        Assert.Empty(plan.Systems);
        Assert.Contains(plan.Warnings, w => w.Contains("no .troybin found"));
    }

    [Fact]
    public void AnUnappliedOrientationIsReportedRatherThanQuietlyDropped()
    {
        var set = LegacyParticlePlacements.Parse("a.troy 0 0 0 -2147483648 0 -37 -37\n");
        var plan = LegacyParticlePorter.Plan(set, _ => Troy(), Vector3.Zero);

        Assert.Single(plan.Placements);
        Assert.Contains(plan.Warnings, w => w.Contains("orientation triple that is NOT applied"));
    }

    [Fact]
    public void EachDistinctSystemIsConvertedExactlyOnce()
    {
        var set = LegacyParticlePlacements.Parse(
            "Data\\Particles\\CANDLE.troy 1 0 0 -2147483648 0 0 0\n"
            + "Data\\Particles\\candle.troy 2 0 0 -2147483648 0 0 0\n"
            + "Data\\Particles\\OTHER.troy 3 0 0 -2147483648 0 0 0\n");

        int reads = 0;
        var plan = LegacyParticlePorter.Plan(set, _ => { reads++; return Troy(); }, Vector3.Zero);

        Assert.Equal(2, reads);                     // candle twice in the file, converted once
        Assert.Equal(2, plan.Systems.Count);
        Assert.Equal(3, plan.Placements.Count);
        Assert.Equal(2, plan.Systems.Sum(s => s.PlacementCount == 2 ? 1 : 0) + 1);
    }

    [Fact]
    public void TheSystemHashIsTheHashOfItsParticlePathNotItsName()
    {
        // Measured 22 of 22 in Riot's shipped jade.materials.bin: objectHash == Fnv1a(particlePath), and
        // 0 of 22 match Fnv1a(particleName). Getting this backwards makes every placement link dangle.
        var set = LegacyParticlePlacements.Parse("Data\\Particles\\CANDLE.troy 0 0 0 -2147483648 0 0 0\n");
        var plan = LegacyParticlePorter.Plan(set, _ => Troy(), Vector3.Zero);

        var system = Assert.Single(plan.Systems);
        Assert.Equal("Particles/CANDLE", system.ParticlePath);
        Assert.Equal(HashAlgorithms.Fnv1a(system.ParticlePath), system.SystemHash);
        Assert.NotEqual(HashAlgorithms.Fnv1a(system.Name), system.SystemHash);
    }

    [Fact]
    public void PlacementNamesAreUniquePerSystem()
    {
        // 103 candles in one map; identical names would be unreadable in the outliner.
        var set = LegacyParticlePlacements.Parse(string.Concat(
            Enumerable.Range(0, 5).Select(i => $"CANDLE.troy {i} 0 0 -2147483648 0 0 0\n")));
        var plan = LegacyParticlePorter.Plan(set, _ => Troy(), Vector3.Zero);

        Assert.Equal(5, plan.Placements.Select(p => p.Name).Distinct().Count());
        Assert.Equal("CANDLE_001", plan.Placements[0].Name);
    }

    [Fact]
    public void BatchIdAllocationHandsOutDistinctKeys()
    {
        // NewParticleId reads the keys the tree holds NOW, so a loop over it returns the same key every
        // time and a batch write keeps only the first placement.
        var tree = ContainerTree();

        var ids = MapPlaceableWriter.NewPlacementIds(tree, Enumerable.Repeat(7u, 500));

        Assert.Equal(500, ids.Count);
        Assert.Equal(500, ids.Select(i => i.ItemKey).Distinct().Count());
        Assert.All(ids, i => Assert.True(i.IsValid));
        Assert.DoesNotContain(0u, ids.Select(i => i.ItemKey));
    }

    [Fact]
    public void BatchIdAllocationReturnsNothingWhenTheMapHasNoContainer()
    {
        using var ms = new MemoryStream();
        new BinTree(Array.Empty<BinTreeObject>(), Array.Empty<string>()).Write(ms);
        Assert.Empty(MapPlaceableWriter.NewPlacementIds(SafeBinTree.Parse(ms.ToArray()), new[] { 1u }));
    }

    [Fact]
    public void TheRealMap2PortPlansCompletely()
    {
        if (!Available) return;

        var plan = LegacyParticlePorter.PlanFromDisk(Dat, Folders, Shift);

        Assert.Equal(554, plan.Placements.Count);
        Assert.Equal(16, plan.Systems.Count);

        // every placement resolved to a system that was actually converted
        Assert.All(plan.Placements, p => Assert.Contains(plan.Systems, s => s.SystemHash == p.SystemHash));
        Assert.Equal(16, plan.Systems.Select(s => s.SystemHash).Distinct().Count());

        // the counts the .dat actually has
        Assert.Equal(103, plan.Systems.Single(s => s.Name.Equals("CANDLE", StringComparison.OrdinalIgnoreCase)).PlacementCount);

        // M531's emitter-gap fix reaches this path: FireTorch_Med must arrive with all six.
        var torch = plan.Systems.Single(s => s.Name.Equals("FireTorch_Med", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(6, torch.Conversion.Emitters.Count);

        // no system converted to an empty effect, and every one produced a loadable bin
        Assert.All(plan.Systems, s => Assert.NotEmpty(s.Conversion.BinBytes));
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("no .troybin found"));
    }

    /// <summary>A minimal one-emitter .troybin, built the way the format actually is: version byte 2,
    /// a u16 string-block size, then the block. The body decodes to nothing, which is fine here - these
    /// tests are about the port's joins, not about conversion fidelity.</summary>
    private static byte[] Troy()
    {
        var block = System.Text.Encoding.ASCII.GetBytes("empty\0");
        var bytes = new byte[3 + block.Length];
        bytes[0] = 2;
        BitConverter.TryWriteBytes(bytes.AsSpan(1, 2), (ushort)block.Length);
        block.CopyTo(bytes, 3);
        return bytes;
    }

    /// <summary>A bin holding one empty MapPlaceableContainer - the thing a map needs before it can hold
    /// a placement at all.</summary>
    private static BinTree ContainerTree()
    {
        uint container = HashAlgorithms.Fnv1a("MapPlaceableContainer");
        var items = new BinTreeMap(HashAlgorithms.Fnv1a("items"),
            BinPropertyType.Hash, BinPropertyType.Struct,
            Array.Empty<KeyValuePair<BinTreeProperty, BinTreeProperty>>());

        using var ms = new MemoryStream();
        new BinTree(
            new[] { new BinTreeObject(0x1234u, container, new BinTreeProperty[] { items }) },
            Array.Empty<string>()).Write(ms);
        return SafeBinTree.Parse(ms.ToArray());
    }
}
