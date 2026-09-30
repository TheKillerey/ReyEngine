using System.Numerics;
using System.Text.Json;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Meta;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.Vfx;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M801: an emitter that writes no <c>rate</c> emits at the schema's default, 0 - not the 10 particles/s the
/// resolver had given it since M36. <c>VfxEmitterDefinitionData.rate</c> is <c>Embed&lt;ValueFloat&gt;</c> with
/// default <c>{ constantValue 0 }</c> on every revision of the client's class schema, and Riot's writer omits it
/// (0 of 1,783,300 visual emitters in the installed game write a constant-0 rate).
///
/// <para>What rate 0 DOES is the second half. A single-particle emitter never read the rate and still spawns its
/// one particle. A continuous emitter at rate 0 emits ONE particle at its first emission - LTK Manager's engine
/// reading, not verified in game - and never another; a positive rate keeps the timing it always had. The 406
/// continuous no-rate visual emitters in the game are attached meshes, flashes, shockwaves and immortal particles:
/// one each, where the old default streamed them and a bare 0 would have erased them.</para>
///
/// <para>The real-data tests read the installed game and the editor's own meta-class database, and say so in the
/// test output when either is missing.</para>
/// </summary>
public sealed class VfxRateDefaultTests(ITestOutputHelper output)
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private const string OlafWad = Final + @"\Champions\Olaf.wad.client";
    private const string OlafBin = "data/characters/jade_olaf/jade_olaf_multi_skins_skin0_skins_skin2_skins_skin6.bin";
    /// <summary>All four of its emitters are visual, continuous and write no rate: blank under a bare 0.</summary>
    private const string OlafRBuf = "Characters/Jade_Olaf/Skins/Skin0/Particles/Jade_Olaf_Base_R_buf";

    private const float Dt = 1f / 30f;

    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    // ------------------------------------------------------------------ fixtures

    private static BinTreeStruct Emitter(string name, BinTreeProperty? rate = null, bool single = false)
    {
        var p = new List<BinTreeProperty>
        {
            new BinTreeString(H("emitterName"), name),
            new BinTreeString(H("texture"), "ASSETS/Test/dot.tex"),
        };
        if (rate is not null) p.Add(rate);
        if (single) p.Add(new BinTreeBitBool(H("isSingleParticle"), true));
        return new BinTreeStruct(0, H("VfxEmitterDefinitionData"), p);
    }

    private static VfxSystemDefinition Resolve(params BinTreeStruct[] emitters)
    {
        var system = new BinTreeObject(H("sys"), H("VfxSystemDefinitionData"), new BinTreeProperty[]
        {
            new BinTreeString(H("particleName"), "sys"),
            new BinTreeContainer(H("complexEmitterDefinitionData"), BinPropertyType.Struct, emitters.Cast<BinTreeProperty>().ToArray()),
        });
        using var ms = new MemoryStream();
        new BinTree(new[] { system }, Array.Empty<string>()).Write(ms);
        return VfxSystemResolver.ExtractAll(ms.ToArray()).Values.Single();
    }

    private static VfxSystemDefinition System(float rate, bool single = false, float timeBeforeFirstEmission = 0f,
        float particleLifetime = 10f) => new(
        PathHash: 1, Name: "sys", ParticlePath: "",
        Emitters: new[]
        {
            new VfxEmitterDefinition(
                Name: "e",
                Rate: VfxCurveF.Const(rate),
                ParticleLifetime: VfxCurveF.Const(particleLifetime),
                EmitterLifetime: null,
                ParticleLinger: 0f,
                TimeBeforeFirstEmission: timeBeforeFirstEmission,
                IsSingleParticle: single,
                Disabled: false,
                BlendMode: 0,
                BirthScale: VfxCurve3.Const(new Vector3(20f, 20f, 20f)),
                ScaleOverLife: null,
                BirthColor: VfxCurve4.Const(Vector4.One),
                ColorOverLife: null,
                BirthVelocity: null,
                Acceleration: null,
                BirthRotationalVelocity: null,
                EmitterPosition: VfxCurve3.Const(Vector3.Zero),
                TexturePath: "ASSETS/Test/dot.tex",
                TexDiv: Vector2.One,
                NumFrames: 1,
                RandomStartFrame: false,
                IsMeshPrimitive: false),
        });

    /// <summary>Live particle count after each of <paramref name="steps"/> steps of <see cref="Dt"/>.</summary>
    private static List<int> Counts(VfxParticleSimulator sim, int steps)
    {
        var counts = new List<int>(steps);
        for (int i = 0; i < steps; i++)
        {
            sim.Update(Dt);
            counts.Add(sim.Emitters[0].ParticleCount);
        }
        return counts;
    }

    private static VfxParticleSimulator Sim(VfxSystemDefinition system)
    {
        var sim = new VfxParticleSimulator(seed: 1234);
        sim.SetSystem(system, Matrix4x4.Identity);
        return sim;
    }

    // ------------------------------------------------------------------ the resolver

    [Fact]
    public void AnEmitterThatWritesNoRateReadsTheSchemaDefaultZero()
    {
        var sys = Resolve(
            Emitter("absent"),
            Emitter("authored", new BinTreeEmbedded(H("rate"), H("ValueFloat"), new BinTreeProperty[] { new BinTreeF32(H("constantValue"), 12.5f) })),
            // the value struct written with nothing in it: its own constantValue default is 0 too
            Emitter("empty", new BinTreeEmbedded(H("rate"), H("ValueFloat"), Array.Empty<BinTreeProperty>())));

        var absent = sys.Emitters.Single(e => e.Name == "absent").Rate;
        Assert.Equal(0f, absent.Constant);
        Assert.Null(absent.Times);   // a constant, not a curve that happens to start at 0
        Assert.Equal(0f, absent.Sample(0.5f));
        Assert.Equal(12.5f, sys.Emitters.Single(e => e.Name == "authored").Rate.Constant);
        Assert.Equal(0f, sys.Emitters.Single(e => e.Name == "empty").Rate.Constant);
    }

    // ------------------------------------------------------------------ what rate 0 emits

    [Fact]
    public void AContinuousEmitterAtRateZeroEmitsOneParticleAtItsFirstEmissionAndNeverAnother()
    {
        var sim = Sim(System(rate: 0f, timeBeforeFirstEmission: 0.5f));

        var counts = Counts(sim, (int)(5f / Dt));   // five seconds, the particle lives ten
        int first = counts.FindIndex(c => c > 0);
        Assert.InRange(first * Dt, 0.5f - 2 * Dt, 0.5f + Dt);   // waits for timeBeforeFirstEmission
        Assert.All(counts.Skip(first), c => Assert.Equal(1, c));   // one, and only one, for the rest of the run

        // a restart is a new run with its own first emission
        sim.Reset();
        var again = Counts(sim, (int)(1f / Dt));
        Assert.Equal(1, again[^1]);
        Assert.Equal(1, again.Max());
    }

    [Fact]
    public void ASingleParticleEmitterThatWritesNoRateStillSpawnsItsParticle()
    {
        var sys = Resolve(Emitter("single", single: true));
        Assert.True(sys.Emitters[0].IsSingleParticle);
        Assert.Equal(0f, sys.Emitters[0].Rate.Constant);

        var sim = Sim(sys with { Emitters = new[] { sys.Emitters[0] with { ParticleLifetime = VfxCurveF.Const(10f) } } });
        var counts = Counts(sim, (int)(3f / Dt));
        Assert.Equal(1, counts[0]);
        Assert.All(counts, c => Assert.Equal(1, c));
    }

    [Fact]
    public void APositiveRateKeepsItsTiming()
    {
        // The engine reading that gives rate 0 its first particle also moves every positive rate's first particle
        // forward to the first emission. That is held for game evidence; this pins that M801 did not do it.
        var counts = Counts(Sim(System(rate: 10f)), (int)MathF.Round(1f / Dt));
        Assert.Equal(0, counts[0]);                         // the accumulator's first particle is 1/rate away
        Assert.InRange(counts[^1], 9, 10);                  // ten per second
        Assert.Equal(1, counts[(int)MathF.Round(0.1f / Dt)]);   // the first one lands at 0.1 s
    }

    // ------------------------------------------------------------------ the real schema and the real game

    [Fact]
    public void TheEditorsOwnSchemaDeclaresZero()
    {
        var meta = MetaClassDatabase.Load(ReyPaths.MetaDbFile);
        if (meta.IsEmpty)
        {
            output.WriteLine($"SKIPPED: the meta-class database is not available at {ReyPaths.MetaDbFile}.");
            return;
        }
        Assert.True(meta.TryGetProperty(H("VfxEmitterDefinitionData"), H("rate"), out var rate));
        Assert.Equal("Embed", rate.FieldType);
        Assert.True(rate.TryGetReferencedClass(out uint valueClass));
        Assert.Equal(H("ValueFloat"), valueClass);
        Assert.NotNull(rate.Default);
        using var json = JsonDocument.Parse(rate.Default!);
        float declared = json.RootElement.GetProperty("constantValue").GetSingle();
        Assert.Equal(0f, declared);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("dynamics").ValueKind);
        Assert.True(meta.TryGetProperty(valueClass, H("constantValue"), out var constant));
        Assert.Equal("0.0", constant.Default);

        // and the resolver's reading of an absent rate is that value
        Assert.Equal(declared, Resolve(Emitter("absent")).Emitters[0].Rate.Constant);
        output.WriteLine($"RAN on the editor's meta database (build {meta.ResolvedBuild}): rate {rate.FieldType}<ValueFloat> default {rate.Default}.");
    }

    [Fact]
    public void JadeOlafsRBuffDrawsOneParticlePerEmitterInsteadOfAStream()
    {
        if (!File.Exists(OlafWad))
        {
            output.WriteLine("SKIPPED: the installed game's Champions/Olaf.wad.client is not available.");
            return;
        }
        using var wad = WadArchive.Open(OlafWad);
        ulong h = HashAlgorithms.WadPath(OlafBin);
        if (!wad.TryGetEntry(h, out _))
        {
            output.WriteLine($"SKIPPED: {OlafBin} is not in Olaf.wad.client (a patch moved it).");
            return;
        }
        var bin = wad.Extract(h);
        if (!VfxSystemResolver.ExtractAll(bin).TryGetValue(H(OlafRBuf), out var sys))
        {
            output.WriteLine($"SKIPPED: {OlafRBuf} is not in {OlafBin} (a patch moved it).");
            return;
        }

        // the raw bin: four emitters, none of which writes a rate
        var raw = SafeBinTree.Parse(bin).Objects[H(OlafRBuf)].Properties.Values.OfType<BinTreeContainer>()
            .SelectMany(c => c.Elements).OfType<BinTreeStruct>().Where(s => s.ClassHash == H("VfxEmitterDefinitionData")).ToList();
        Assert.Equal(4, raw.Count);
        Assert.All(raw, s => Assert.False(s.Properties.ContainsKey(H("rate"))));

        // the resolver: all four visual, continuous, at the schema's constant 0
        Assert.Equal(4, sys.Emitters.Count);
        Assert.All(sys.Emitters, e =>
        {
            Assert.True(e.IsVisual, e.Name);
            Assert.False(e.IsSingleParticle, e.Name);
            Assert.Equal(0f, e.Rate.Constant);
            Assert.Null(e.Rate.Times);
        });

        // the simulator: never more than one particle per emitter, and the two longer-lived ones are there from the
        // first step. The old default is run beside it so the assertion cannot pass on an emitter that never runs.
        var now = RunMax(sys, out var afterFirstStep);
        var old = RunMax(sys with { Emitters = sys.Emitters.Select(e => e with { Rate = VfxCurveF.Const(10f) }).ToList() }, out _);
        Assert.All(now, kv => Assert.InRange(kv.Value, 0, 1));
        Assert.Equal(1, afterFirstStep["Basic"]);
        Assert.Equal(1, afterFirstStep["Shockwaves"]);
        Assert.True(old["Basic"] > 1 && old["Shockwaves"] > 1, "the M36 default of 10/s streamed them");
        output.WriteLine($"RAN on the installed game: {OlafRBuf} - most live per emitter now "
            + string.Join(", ", now.Select(kv => $"{kv.Key} {kv.Value}")) + "; at the old 10/s "
            + string.Join(", ", old.Select(kv => $"{kv.Key} {kv.Value}")) + ".");
    }

    /// <summary>Three seconds of the system; the most particles each emitter had alive at once, and its count after
    /// the first step.</summary>
    private static Dictionary<string, int> RunMax(VfxSystemDefinition sys, out Dictionary<string, int> afterFirstStep)
    {
        var sim = Sim(sys);
        var max = sim.Emitters.ToDictionary(e => e.Def.Name, _ => 0);
        afterFirstStep = new Dictionary<string, int>();
        for (int i = 0; i < (int)(3f / Dt); i++)
        {
            sim.Update(Dt);
            foreach (var e in sim.Emitters)
            {
                max[e.Def.Name] = Math.Max(max[e.Def.Name], e.ParticleCount);
                if (i == 0) afterFirstStep[e.Def.Name] = e.ParticleCount;
            }
        }
        return max;
    }
}
