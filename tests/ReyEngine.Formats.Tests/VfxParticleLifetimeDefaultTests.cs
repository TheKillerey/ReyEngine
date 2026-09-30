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
/// M803: an emitter that writes no <c>particleLifetime</c> lives the schema's default, 3 s - not the 1 s the resolver had
/// given it since M36. <c>VfxEmitterDefinitionData.particleLifetime</c> is <c>Embed&lt;ValueFloat&gt;</c> with default
/// <c>{ constantValue 3 }</c> on the one revision the client's class schema has ever had, and Riot's writer omits exactly that
/// value: of the 1,255,667 emitters in the installed game that write the field as a bare constant, none writes 3, while 1 is the
/// commonest value written (129,311). LTK Manager's engine reads the same 3.
///
/// <para>Only an ABSENT field changes. A lifetime the bin writes - 1, -1 (immortal), a keyed curve - reads exactly as before.
/// 43,384 visual emitters write none: 30,267 single particles, which now stay three seconds (Janna's Q tornado meshes no longer
/// vanish one second into the flight), and 13,117 continuous ones, whose live count and over-life curves stretch threefold.</para>
///
/// <para>The real-data tests read the installed game and the editor's own meta-class database, and say in the test output
/// whether they ran.</para>
/// </summary>
public sealed class VfxParticleLifetimeDefaultTests(ITestOutputHelper output)
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private const string JannaWad = Final + @"\Champions\Janna.wad.client";
    private const string JannaSkin2Bin = "data/characters/janna/skins/skin2.bin";
    /// <summary>Janna's Skin02 Q missile. Its tornado meshes are single particles on a 6 s emitter that write no
    /// particleLifetime.</summary>
    private const string JannaQMis = "Characters/Janna/Skins/Skin2/Particles/Janna_Skin02_Q_Mis";

    private const float Dt = 1f / 30f;

    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    // ------------------------------------------------------------------ fixtures

    private static BinTreeEmbedded ValueFloat(string field, float constant, float[]? times = null, float[]? values = null)
    {
        var p = new List<BinTreeProperty> { new BinTreeF32(H("constantValue"), constant) };
        if (times is not null && values is not null)
            p.Add(new BinTreeStruct(H("dynamics"), H("VfxAnimatedFloatVariableData"), new BinTreeProperty[]
            {
                new BinTreeContainer(H("times"), BinPropertyType.F32, times.Select(t => (BinTreeProperty)new BinTreeF32(0, t)).ToArray()),
                new BinTreeContainer(H("values"), BinPropertyType.F32, values.Select(v => (BinTreeProperty)new BinTreeF32(0, v)).ToArray()),
            }));
        return new BinTreeEmbedded(H(field), H("ValueFloat"), p);
    }

    private static BinTreeStruct Emitter(string name, BinTreeProperty? particleLifetime = null, bool single = false,
        float? rate = null, float? emitterLifetime = null)
    {
        var p = new List<BinTreeProperty>
        {
            new BinTreeString(H("emitterName"), name),
            new BinTreeString(H("texture"), "ASSETS/Test/dot.tex"),
        };
        if (particleLifetime is not null) p.Add(particleLifetime);
        if (rate is { } r) p.Add(ValueFloat("rate", r));
        if (emitterLifetime is { } life) p.Add(new BinTreeOptional(H("lifetime"), new BinTreeF32(0, life)));
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

    private static VfxParticleSimulator Sim(VfxSystemDefinition system)
    {
        var sim = new VfxParticleSimulator(seed: 1234);
        sim.SetSystem(system, Matrix4x4.Identity);
        return sim;
    }

    /// <summary>Live particle count of the first emitter after each of <paramref name="steps"/> steps of <see cref="Dt"/>.</summary>
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

    // ------------------------------------------------------------------ the resolver

    [Fact]
    public void AnEmitterThatWritesNoParticleLifetimeReadsTheSchemaDefaultThree()
    {
        var sys = Resolve(
            Emitter("absent"),
            Emitter("one", ValueFloat("particleLifetime", 1f)),
            Emitter("immortal", ValueFloat("particleLifetime", -1f)),
            Emitter("keyed", ValueFloat("particleLifetime", 1f, times: new[] { 0f, 1f }, values: new[] { 2f, 4f })));

        var absent = sys.Emitters.Single(e => e.Name == "absent").ParticleLifetime;
        Assert.Equal(3f, absent.Constant);
        Assert.Null(absent.Times);   // a constant, not a curve that happens to start at 3
        Assert.Equal(3f, absent.Sample(0.5f));
        Assert.Equal(3f, absent.SampleBirth(new Random(7)));

        // what a bin writes is read exactly as before
        Assert.Equal(1f, sys.Emitters.Single(e => e.Name == "one").ParticleLifetime.Constant);
        Assert.Equal(-1f, sys.Emitters.Single(e => e.Name == "immortal").ParticleLifetime.Constant);
        var keyed = sys.Emitters.Single(e => e.Name == "keyed").ParticleLifetime;
        Assert.Equal(new[] { 0f, 1f }, keyed.Times);
        Assert.Equal(2f, keyed.Sample(0f));
    }

    // ------------------------------------------------------------------ what the simulator does with it

    [Fact]
    public void ASingleParticleThatWritesNoLifetimeLivesThreeSeconds()
    {
        var sys = Resolve(Emitter("single", single: true, emitterLifetime: 10f));
        var counts = Counts(Sim(sys), (int)MathF.Round(4f / Dt));

        int died = counts.FindIndex(1, c => c == 0);
        Assert.Equal(1, counts[0]);
        Assert.InRange(died * Dt, 3f - 2 * Dt, 3f + 2 * Dt);   // three seconds, where the old default ended it at one
        Assert.All(counts.Take(died), c => Assert.Equal(1, c));
        Assert.All(counts.Skip(died), c => Assert.Equal(0, c));   // one burst on a finite emitter
    }

    [Fact]
    public void AContinuousEmitterThatWritesNoLifetimeHoldsRateTimesThree()
    {
        var sys = Resolve(Emitter("stream", rate: 2f));
        var counts = Counts(Sim(sys), (int)MathF.Round(6f / Dt));

        // two a second, each living three seconds: six at steady state, where the old default held two
        Assert.InRange(counts[^1], 5, 6);
        Assert.InRange(counts.Skip((int)MathF.Round(4f / Dt)).Max(), 5, 6);
        Assert.InRange(counts[(int)MathF.Round(2.9f / Dt)], 5, 6);   // nothing has died yet at 2.9 s
    }

    // ------------------------------------------------------------------ the real schema and the real game

    [Fact]
    public void TheEditorsOwnSchemaDeclaresThreeAtEveryBuildItCovers()
    {
        var latest = MetaClassDatabase.Load(ReyPaths.MetaDbFile);
        if (latest.IsEmpty || latest.Versions.Count == 0)
        {
            output.WriteLine($"SKIPPED: the meta-class database is not available at {ReyPaths.MetaDbFile}.");
            return;
        }
        var first = latest.Versions[0];
        var oldest = MetaClassDatabase.Load(ReyPaths.MetaDbFile, first.Build);
        foreach (var meta in new[] { oldest, latest })
        {
            Assert.True(meta.TryGetProperty(H("VfxEmitterDefinitionData"), H("particleLifetime"), out var life), $"build {meta.ResolvedBuild}");
            Assert.Equal("Embed", life.FieldType);
            Assert.True(life.TryGetReferencedClass(out uint valueClass));
            Assert.Equal(H("ValueFloat"), valueClass);
            Assert.NotNull(life.Default);
            using var json = JsonDocument.Parse(life.Default!);
            Assert.Equal(3f, json.RootElement.GetProperty("constantValue").GetSingle());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("dynamics").ValueKind);
            // the 3 is the field's own default: the value class alone would give 0
            Assert.True(meta.TryGetProperty(valueClass, H("constantValue"), out var constant));
            Assert.Equal("0.0", constant.Default);
        }

        // and the resolver's reading of an absent particleLifetime is that value
        Assert.Equal(3f, Resolve(Emitter("absent")).Emitters[0].ParticleLifetime.Constant);
        output.WriteLine($"RAN on the editor's meta database: particleLifetime Embed<ValueFloat> default {{constantValue 3}} at build "
            + $"{oldest.ResolvedBuild} ({first.Patch}) and {latest.ResolvedBuild} ({latest.Versions[^1].Patch}).");
    }

    [Fact]
    public void RiotsWriterNeverWritesTheDefaultThreeInJannasWad()
    {
        if (!File.Exists(JannaWad))
        {
            output.WriteLine("SKIPPED: the installed game's Champions/Janna.wad.client is not available.");
            return;
        }
        // Population: every VfxEmitterDefinitionData in every .bin of Janna.wad.client. The whole-game figure (0 of
        // 1,255,667 bare constants are 3) comes from the M803 census; this pins one WAD so a patch that starts writing
        // the default would be seen.
        long absent = 0, bareThree = 0, bareOne = 0, bare = 0;
        using (var wad = WadArchive.Open(JannaWad))
            foreach (var entry in wad.Entries)
            {
                byte[] bytes;
                try { bytes = wad.Extract(entry.PathHash); } catch { continue; }
                if (bytes.Length < 4 || bytes[0] != 'P' || bytes[1] != 'R' || bytes[2] != 'O' || bytes[3] != 'P') continue;
                BinTree tree;
                try { tree = SafeBinTree.Parse(bytes); } catch { continue; }
                foreach (var o in tree.Objects.Values.Where(o => o.ClassHash == H("VfxSystemDefinitionData")))
                    foreach (var c in o.Properties.Values.OfType<BinTreeContainer>())
                        foreach (var s in c.Elements.OfType<BinTreeStruct>().Where(s => s.ClassHash == H("VfxEmitterDefinitionData")))
                        {
                            if (!s.Properties.TryGetValue(H("particleLifetime"), out var life)) { absent++; continue; }
                            if (life is not BinTreeStruct value || value.Properties.ContainsKey(H("dynamics"))) continue;
                            if (!value.Properties.TryGetValue(H("constantValue"), out var cv) || cv is not BinTreeF32 f) continue;
                            bare++;
                            if (f.Value == 3f) bareThree++;
                            if (f.Value == 1f) bareOne++;
                        }
            }
        if (bare == 0)
        {
            output.WriteLine("SKIPPED: no VFX emitter was readable in Janna.wad.client.");
            return;
        }
        Assert.Equal(0L, bareThree);
        Assert.True(bareOne > 0, "1 s is written out, so it cannot be the value the writer omits");
        Assert.True(absent > 0);
        output.WriteLine($"RAN on the installed game: Janna.wad.client - {bare:n0} emitters write particleLifetime as a bare constant, "
            + $"{bareThree} of them 3 and {bareOne:n0} of them 1; {absent:n0} write none.");
    }

    [Fact]
    public void JannasQTornadoLastsItsFlightInsteadOfVanishingAfterOneSecond()
    {
        if (!File.Exists(JannaWad))
        {
            output.WriteLine("SKIPPED: the installed game's Champions/Janna.wad.client is not available.");
            return;
        }
        using var wad = WadArchive.Open(JannaWad);
        ulong h = HashAlgorithms.WadPath(JannaSkin2Bin);
        if (!wad.TryGetEntry(h, out _))
        {
            output.WriteLine($"SKIPPED: {JannaSkin2Bin} is not in Janna.wad.client (a patch moved it).");
            return;
        }
        var bin = wad.Extract(h);
        if (!VfxSystemResolver.ExtractAll(bin).TryGetValue(H(JannaQMis), out var sys))
        {
            output.WriteLine($"SKIPPED: {JannaQMis} is not in {JannaSkin2Bin} (a patch moved it).");
            return;
        }

        // the raw bin, in the resolver's order: which emitters write no particleLifetime
        var raw = SafeBinTree.Parse(bin).Objects[H(JannaQMis)].Properties.Values.OfType<BinTreeContainer>()
            .SelectMany(c => c.Elements).OfType<BinTreeStruct>().Where(s => s.ClassHash == H("VfxEmitterDefinitionData")).ToList();
        Assert.Equal(raw.Count, sys.Emitters.Count);
        // the tornado: visual single particles that write no particleLifetime, fire at once and run past 2.5 s
        var tornado = sys.Emitters.Where((e, i) => !raw[i].Properties.ContainsKey(H("particleLifetime")) && e.IsVisual
                && e.IsSingleParticle && e.TimeBeforeFirstEmission <= 0.1f && !e.HasVariableStartTime && e.ChanceToNotExist <= 0f
                && e.EmitterLifetime is > 2.5f)
            .ToList();
        Assert.True(tornado.Count >= 5, $"expected the tornado meshes, found {tornado.Count}");
        Assert.All(tornado, e => Assert.Equal(3f, e.ParticleLifetime.Constant));

        // the old 1 s runs beside it, so the assertion cannot pass on emitters that never spawn. Matched by reference:
        // emitter definitions are records, and two of them can be equal by value.
        var oldTornado = tornado.Select(e => e with { ParticleLifetime = VfxCurveF.Const(1f) }).ToList();
        var oldSys = sys with
        {
            Emitters = sys.Emitters.Select(e =>
            {
                int k = tornado.FindIndex(t => ReferenceEquals(t, e));
                return k >= 0 ? oldTornado[k] : e;
            }).ToList(),
        };
        var now = Alive(sys, tornado, 1.5f, 2.5f);
        var old = Alive(oldSys, oldTornado, 1.5f, 2.5f);
        Assert.Equal(tornado.Count, now[1.5f].Length);
        Assert.Equal(tornado.Count, old[1.5f].Length);
        Assert.All(now[1.5f], n => Assert.Equal(1, n));
        Assert.All(now[2.5f], n => Assert.Equal(1, n));
        Assert.All(old[1.5f], n => Assert.Equal(0, n));
        output.WriteLine($"RAN on the installed game: {JannaQMis} - {tornado.Count} tornado emitters without a particleLifetime "
            + $"({string.Join(", ", tornado.Select(e => e.Name))}); alive at 1.5 s / 2.5 s now {now[1.5f].Sum()} / {now[2.5f].Sum()}, "
            + $"at the old 1 s {old[1.5f].Sum()} / {old[2.5f].Sum()}.");
    }

    /// <summary>For each time in <paramref name="at"/>, the live particle count of each emitter in <paramref name="watched"/>
    /// (matched by reference, in the simulator's order).</summary>
    private static Dictionary<float, int[]> Alive(VfxSystemDefinition sys, List<VfxEmitterDefinition> watched, params float[] at)
    {
        var sim = Sim(sys);
        var states = sim.Emitters.Where(s => watched.Any(w => ReferenceEquals(w, s.Def))).ToList();
        var result = new Dictionary<float, int[]>();
        int tick = 0;
        foreach (float t in at.OrderBy(x => x))
        {
            for (; tick < (int)MathF.Round(t / Dt); tick++) sim.Update(Dt);
            result[t] = states.Select(s => s.ParticleCount).ToArray();
        }
        return result;
    }
}
