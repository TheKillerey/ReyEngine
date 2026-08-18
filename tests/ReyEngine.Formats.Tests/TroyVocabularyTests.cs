using ReyEngine.Formats.Particles;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M520: the legacy field vocabulary, recovered from a text/binary twin.
///
/// <para>The legacy particle corpus ships exactly one effect in both forms - <c>FireTorch_Simple.troy</c>
/// (the authored text) beside <c>FireTorch_Simple.troybin</c> (the shipped binary). That pair is a
/// Rosetta stone: it names the fields the binary only hashes, and it is self-verifying, because a wrong
/// name cannot hash onto a key that exists. 250 of the text's 257 assignments land on a real key, and
/// the 7 that do not are marked ";UNKNOWN_HASH" in the text itself.</para>
///
/// <para>The pair lives outside the repo, so the tests needing it skip when it is absent - the same
/// convention the map-file tests use. The hash tests below need nothing external and always run.</para>
/// </summary>
public sealed class TroyVocabularyTests
{
    private const string Corpus = @"K:\LeagueSandbox\League_Sandbox_Client\DATA\Particles";

    private static TroyBinFile? Load(string name)
    {
        string path = Path.Combine(Corpus, name + ".troybin");
        if (!File.Exists(path)) return null;
        Assert.True(TroyBinFile.TryParse(File.ReadAllBytes(path), out var t, out var error), error);
        return t;
    }

    private static Func<int, string?> Resolver(TroyBinFile t)
    {
        var offsets = t.Strings.ToDictionary(s => s.Offset, s => s.Value);
        return o => offsets.TryGetValue(o, out var v) ? v : null;
    }

    [Fact]
    public void TheEmitterListPrefixIsSystemGroupPart()
    {
        // The whole recovery in one line. The emitter chain used to carry 0xAE671AB7 as an unexplained
        // seed; it is sdbm("*grouppart") seeded with sdbm("system"). Pinning the NUMBER matters as much
        // as the spelling: if the latter ever drifts, every emitter in every legacy file stops being
        // found and the reader reports an empty system rather than failing.
        Assert.Equal(0xAE671AB7u, TroyHash.Sdbm("*grouppart", TroyHash.Sdbm("system")));
        Assert.Equal(0x0616933Au, TroyHash.EmitterNameKey(1));
        Assert.Equal(0x12C83B76u, TroyHash.EmitterNameKey(10));
    }

    [Fact]
    public void FieldKeysAreCaseInsensitiveOnBothHalves()
    {
        // Sections are spelled inconsistently in the wild: DestroyedBuilding_idle references
        // "SparksDrag" from one emitter while naming its own sections in lower case.
        Assert.Equal(TroyHash.FieldKey("Flame", TroyFields.Texture),
                     TroyHash.FieldKey("fLaMe", "*P-TEXTURE"));
    }

    [Fact]
    public void EveryFieldKindHasItsOwnLegacySpelling()
    {
        // orbit, not orbital. A mismatch here does not throw - it silently drops every field of that
        // kind, which surfaces as "the effect is just wrong" rather than as an error.
        Assert.Equal("*field-accel-1", TroyFields.FieldRef(TroyFieldKind.Acceleration, 1));
        Assert.Equal("*field-attract-1", TroyFields.FieldRef(TroyFieldKind.Attraction, 1));
        Assert.Equal("*field-drag-2", TroyFields.FieldRef(TroyFieldKind.Drag, 2));
        Assert.Equal("*field-orbit-1", TroyFields.FieldRef(TroyFieldKind.Orbital, 1));
        Assert.Equal("*field-noise-1", TroyFields.FieldRef(TroyFieldKind.Noise, 1));
        Assert.Equal(5, TroyFields.FieldKinds.Count);
    }

    [Fact]
    public void TheSystemSectionNamesEveryEmitterAndItsQualityTier()
    {
        var t = Load("FireTorch_Simple");
        if (t is null) return;

        Assert.True(t.HasDecodedBody);
        Assert.Equal(new[] { "FlameSparks", "Flame", "Flat", "FlameDark", "HeatHaze" },
            t.SystemInfo.Parts.Select(p => p.Name));
        Assert.All(t.SystemInfo.Parts, p => Assert.Equal("Simple", p.Type));

        // only the first part carries one, which is why importance is nullable rather than defaulted
        Assert.Equal("Low", t.SystemInfo.Parts[0].Importance);
        Assert.All(t.SystemInfo.Parts.Skip(1), p => Assert.Null(p.Importance));
        Assert.True(t.SystemInfo.SimulateEveryFrame);
    }

    [Fact]
    public void ForceFieldsAreReadWithTheKindTheReferenceGaveThem()
    {
        var t = Load("FireTorch_Simple");
        if (t is null) return;

        // [FlameAttract] f-accel=100, f-pos=0.0 50.0 0.0, f-radius=200
        var attract = t.ForceFields.Single(f => f.Name == "FlameAttract");
        Assert.Equal(TroyFieldKind.Attraction, attract.Kind);
        Assert.Equal(100f, attract.Strength);
        Assert.Equal(new System.Numerics.Vector3(0, 50, 0), attract.Position);
        Assert.Equal(200f, attract.Radius);
        // f-accel is a SCALAR on an attraction field, so the vec3 reading must stay empty
        Assert.Null(attract.Acceleration);

        // [FlameAccel] f-accel=0.0 300.0 0.0 - same field name, a vec3 this time
        var accel = t.ForceFields.Single(f => f.Name == "FlameAccel");
        Assert.Equal(TroyFieldKind.Acceleration, accel.Kind);
        Assert.Equal(new System.Numerics.Vector3(0, 300, 0), accel.Acceleration);
        Assert.Null(accel.Strength);

        // [FlameOrbit] f-direction=0.0 1.0 0.0
        Assert.Equal(new System.Numerics.Vector3(0, 1, 0),
            t.ForceFields.Single(f => f.Name == "FlameOrbit").Direction);

        // [FlameDrag] f-drag=0.1 - tenths, not the integer 1
        Assert.Equal(0.1f, t.ForceFields.Single(f => f.Name == "FlameDrag").Drag!.Value, 5);
    }

    [Fact]
    public void EmittersPointAtTheFieldsTheyUse()
    {
        var t = Load("FireTorch_Simple");
        if (t is null) return;

        var flame = t.Emitters.Single(e => e.Name == "Flame");
        Assert.Equal(new[] { "FlameAccel", "FlameAttract", "FlameDrag", "FlameOrbit" },
            flame.FieldReferences);

        // [Flat] declares none, and that has to read as empty rather than null-and-crash
        Assert.Empty(t.Emitters.Single(e => e.Name == "Flat").FieldReferences!);

        // one field referenced by two emitters is stored once
        Assert.Single(t.ForceFields.Where(f => f.Name == "FlameAccel"));
    }

    [Fact]
    public void PassAndRenderModeSurvive()
    {
        var t = Load("FireTorch_Simple");
        if (t is null) return;

        Assert.Equal(1, t.Emitters.Single(e => e.Name == "Flame").Pass);
        Assert.Equal(-1, t.Emitters.Single(e => e.Name == "FlameDark").Pass);   // drawn behind
        Assert.Equal(1, t.Emitters.Single(e => e.Name == "FlameDark").RenderMode);
        Assert.Equal(0, t.Emitters.Single(e => e.Name == "Flat").RenderMode);
    }

    [Fact]
    public void NumbersStoredAsTextAreStillNumbers()
    {
        var t = Load("FireTorch_Simple");
        if (t is null) return;

        // [Flame] e-rate=100 lives in the string section. Without a resolver the reader reports it as
        // ABSENT, and absent reads downstream as "the author wanted the default" - 92,523 values across
        // the corpus, among them 8,756 emission rates.
        uint rate = TroyHash.FieldKey("Flame", TroyFields.EmitterRate);
        Assert.False(t.Sections!.TryGetScalar(rate, out _));
        Assert.True(t.Sections.TryGetScalar(rate, Resolver(t), out float value));
        Assert.Equal(100f, value);
    }

    [Fact]
    public void AProbabilityKeyIsNotMistakenForAScalar()
    {
        var t = Load("FireTorch_Simple");
        if (t is null) return;

        // [Flame] e-rotation1P1 = "0.0 30" is two tokens. Reading the first as the whole value is how a
        // curve silently collapses into a constant, so the scalar reader has to refuse it outright.
        uint key = TroyHash.FieldKey("Flame", TroyFields.EmitRotation(1) + "P1");
        Assert.True(t.Sections!.ByKey.ContainsKey(key));
        Assert.False(t.Sections.TryGetScalar(key, Resolver(t), out _));

        Assert.True(t.Sections.TryGetVector2(key, Resolver(t), out float a, out float b));
        Assert.Equal(0f, a);
        Assert.Equal(30f, b);
    }

    [Fact]
    public void EActiveIsLeftRawBecauseItIsNotTheFlagItLooksLike()
    {
        var t = Load("FireTorch_Simple");
        if (t is null) return;

        // Measured over the corpus: present on 270 emitters, never 0, and 192 of them hold something
        // that is neither 0 nor 1. This test exists to stop it quietly becoming a bool again.
        Assert.Equal(1f, t.Emitters.Single(e => e.Name == "FlameSparks").EmitterActive);
    }

    [Fact]
    public void TheWholeCorpusStillParses()
    {
        if (!Directory.Exists(Corpus)) return;

        int files = 0, parsed = 0, decoded = 0;
        foreach (var path in Directory.EnumerateFiles(Corpus, "*.troybin"))
        {
            files++;
            if (!TroyBinFile.TryParse(File.ReadAllBytes(path), out var t, out _) || t is null) continue;
            parsed++;
            if (t.HasDecodedBody) decoded++;
        }

        // 5,851 files, every one parsed, 5,447 with a body that decodes to the exact final byte. Stated
        // as ratios so a differently-sized install still exercises the same invariant.
        Assert.True(files > 0);
        Assert.Equal(files, parsed);
        Assert.True(decoded >= files * 0.9, $"only {decoded}/{files} bodies decoded");
    }
}
