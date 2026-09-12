using System.Numerics;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M709, the second of the ltk-manager readings ported back: an emitter carrying <c>isGroundLayer</c> draws
/// in a display list of its own that runs BEFORE the default one, and <c>pass</c> orders emitters inside a
/// list only. Both renderers used to sort on <c>pass</c> alone, so a ground-layer emitter authored high
/// drew on top of the geometry effects it belongs under.
///
/// <para>The screen case that settled it is pinned here as data, the way the reference renderer pins it in
/// its own suite: the 31 emitters of <c>AurelionSol_Skin11_E_ExecuteZone_ChildParticle</c>, read out of the
/// shipped bin, where <c>BG_BrighterInterior5</c> at pass 599 drew over rocks at passes 102 and 103.</para>
/// </summary>
public sealed class VfxDrawOrderTests
{
    private static string? Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static VfxEmitterDefinition Emitter(string name, int pass, bool? ground, int stencilMode = 0) => new(
        Name: name,
        Rate: VfxCurveF.Const(10f),
        ParticleLifetime: VfxCurveF.Const(1f),
        EmitterLifetime: null,
        ParticleLinger: 0f,
        TimeBeforeFirstEmission: 0f,
        IsSingleParticle: false,
        Disabled: false,
        BlendMode: 1,
        BirthScale: VfxCurve3.Const(new Vector3(10f, 10f, 10f)),
        ScaleOverLife: null,
        BirthColor: VfxCurve4.Const(Vector4.One),
        ColorOverLife: null,
        BirthVelocity: null,
        Acceleration: null,
        BirthRotationalVelocity: null,
        EmitterPosition: VfxCurve3.Const(Vector3.Zero),
        TexturePath: "ASSETS/Test/p.dds",
        TexDiv: new Vector2(1f, 1f),
        NumFrames: 1,
        RandomStartFrame: false,
        IsMeshPrimitive: false,
        Pass: pass,
        StencilMode: stencilMode,
        Extras: ground is null ? null : new VfxEmitterExtras { IsGroundLayer = ground });

    private static List<string> Order(IEnumerable<VfxEmitterDefinition> emitters, bool groundFirst = true) =>
        emitters.OrderBy(e => VfxDrawOrder.KeyFor(e, groundFirst)).Select(e => e.Name ?? "").ToList();

    // ================================================================ the key

    [Fact]
    public void TheGroundLayerOutranksThePass()
    {
        var emitters = new[]
        {
            Emitter("rock", 102, null),
            Emitter("glow", 599, true),
            Emitter("dust", 900, null),
            Emitter("crack", -300, true),
        };

        // a ground emitter authored 497 passes LATER than the rock still draws under it
        Assert.Equal(new[] { "crack", "glow", "rock", "dust" }, Order(emitters));

        // pass still orders inside each of the two groups, which is the half of the rule that did not change
        Assert.Equal(0, VfxDrawOrder.KeyFor(Emitter("x", 599, true)).Layer);
        Assert.Equal(1, VfxDrawOrder.KeyFor(Emitter("x", -300, null)).Layer);
        Assert.Equal(599, VfxDrawOrder.KeyFor(Emitter("x", 599, true)).Pass);
    }

    [Fact]
    public void AbsenceIsTheOnlyFalseThereIs()
    {
        // the field is a bool? the corpus writes ONLY when true, so an emitter with no Extras at all, and
        // one whose Extras omits the flag, are both "not ground". Reading it as `?? true` would promote
        // every emitter in the game.
        Assert.False(VfxDrawOrder.IsGroundLayer(Emitter("none", 0, null)));
        Assert.False(VfxDrawOrder.IsGroundLayer(Emitter("absent", 0, ground: false)));
        Assert.True(VfxDrawOrder.IsGroundLayer(Emitter("ground", 0, ground: true)));
    }

    [Fact]
    public void AStencilEmitterIsNeverPromoted()
    {
        // The exclusion, and it is load-bearing. Our renderer emulates Riot's stencil masking by drawing a
        // mode-1 WRITER before the mode-2/3 testers that read its mask. Promoting a tester past its writer
        // makes it test against a mask nobody has written, and a mode-2 emitter then draws nothing at all.
        // Measured: 19 systems in the installed game would lose 49 emitters that way.
        Assert.False(VfxDrawOrder.IsGroundLayer(Emitter("tester", 50, ground: true, stencilMode: 2)));
        Assert.False(VfxDrawOrder.IsGroundLayer(Emitter("writer", -9999, ground: true, stencilMode: 1)));
        Assert.True(VfxDrawOrder.IsGroundLayer(Emitter("plain", 50, ground: true, stencilMode: 0)));

        // the shape the census found on LeeSin_Skin31_W_shield_self: a ground-layer mode-2 tester at pass
        // 50 and its writer, not ground, far below it. The writer must still come first.
        var emitters = new[]
        {
            Emitter("cloud", 50, ground: true, stencilMode: 2),
            Emitter("STENCIL_MASK", -9999, ground: null, stencilMode: 1),
        };
        Assert.Equal(new[] { "STENCIL_MASK", "cloud" }, Order(emitters));
    }

    [Fact]
    public void TheKeyCanBeTurnedOff()
    {
        var emitters = new[] { Emitter("rock", 102, null), Emitter("glow", 599, true) };
        Assert.Equal(new[] { "glow", "rock" }, Order(emitters));
        Assert.Equal(new[] { "rock", "glow" }, Order(emitters, groundFirst: false));
        // and it is on by default, because the engine's order is the one worth drawing
        Assert.True(VfxDrawOrder.GroundLayerFirst);
    }

    [Fact]
    public void EqualKeysKeepTheAuthoredOrder()
    {
        // the engine's last key is the emitter's own authored index, and a stable sort gives us that for
        // free. It only holds while every caller uses OrderBy over a list still in authored order.
        var emitters = new[]
        {
            Emitter("second", 0, null), Emitter("third", 0, null), Emitter("first", -1, null),
        };
        Assert.Equal(new[] { "first", "second", "third" }, Order(emitters));
    }

    // ================================================================ the case that settled it

    [Fact]
    public void TheRocksDrawOverTheInteriorGlow()
    {
        // AurelionSol_Skin11_E_ExecuteZone_ChildParticle, all 31 emitters in file order, read out of the
        // shipped champion bin: name, pass, ground layer. None of them carries a stencil mode.
        var authored = new (string Name, int Pass, bool Ground)[]
        {
            ("Ring_Alpha1", 10, true), ("StarSpiral_1", -1, true), ("StarSpiral_2", -1, true),
            ("StarSpiral_3", -1, true), ("Glow1", 10, true), ("BG_BrighterInterior1", -19, true),
            ("AOE_Light", 0, false), ("Praxis", 900, false), ("Star1_ChildParticle", 0, false),
            ("Star2_ChildParticle", 0, false), ("Star4", 0, false), ("StarBG_1_Top", 200, false),
            ("StarBg_2", 10, false), ("StarBG_3", 6, false), ("StarBG_4", 200, false),
            ("StarBg_5", 10, false), ("StarBG_6", 6, false), ("StarBG_7", 200, false),
            ("StarBg_1_Core", 199, false), ("StarBG_8", 6, false), ("groundcrack", -300, true),
            ("BG_BrighterInterior4", -60, true), ("BG_BrighterInterior5", 599, true), ("flash", 101, false),
            ("cloud", 0, false), ("Ring_Alpha7", 15, true), ("Glow2", 99, true),
            ("REFLECTION_SPHERE2", 102, false), ("cloud_Lightning", 0, false),
            ("cloud_Lightning1", 0, false), ("REFLECTION_SPHERE4", 103, false),
        };
        var emitters = authored.Select(a => Emitter(a.Name, a.Pass, a.Ground ? true : null)).ToList();

        var before = Order(emitters, groundFirst: false);
        var after = Order(emitters);

        // the reported artefact: the interior glow drew LAST of the three, i.e. over both rocks
        Assert.True(before.IndexOf("BG_BrighterInterior5") > before.IndexOf("REFLECTION_SPHERE2"));
        Assert.True(before.IndexOf("BG_BrighterInterior5") > before.IndexOf("REFLECTION_SPHERE4"));

        // and after: the rocks draw over it, which is what the game shows
        Assert.True(after.IndexOf("BG_BrighterInterior5") < after.IndexOf("REFLECTION_SPHERE2"));
        Assert.True(after.IndexOf("BG_BrighterInterior5") < after.IndexOf("REFLECTION_SPHERE4"));

        // the whole ground block comes first, in its own pass order, and nothing else is inside it
        Assert.Equal(11, authored.Count(a => a.Ground));
        Assert.Equal(
            new[] { "groundcrack", "BG_BrighterInterior4", "BG_BrighterInterior1", "StarSpiral_1",
                    "StarSpiral_2", "StarSpiral_3", "Ring_Alpha1", "Glow1", "Ring_Alpha7", "Glow2",
                    "BG_BrighterInterior5" },
            after.Take(11));
        // and the default list keeps the order it always had
        Assert.Equal(before.Where(n => !authored.Single(a => a.Name == n).Ground),
                     after.Skip(11));
    }

    // ================================================================ both hosts read the one key

    [Fact]
    public void AllThreeHostsUseTheSharedKey()
    {
        // one key, three hosts, so the viewports cannot drift into disagreeing about layering
        string? gl = Source("src", "ReyEngine.Rendering", "Vfx", "VfxParticleRenderer.cs");
        Assert.NotNull(gl);
        Assert.Contains("OrderBy(static e => ReyEngine.Formats.Vfx.VfxDrawOrder.KeyFor(e.Def))", gl);

        // the particle editor's own Direct3D 11 preview registered one material per emitter in authored
        // order and so had no draw order at all before M709
        string? preview = Source("src", "ReyEngine.App", "Services", "D3D11ParticlePlayback.cs");
        Assert.NotNull(preview);
        Assert.Contains("VfxDrawOrder.KeyFor(_sim.Emitters[i].Def)", preview);

        // the map viewport reached the key in M710, once its registration became the ordered seam. Before
        // that its slice sort ran AFTER every material was registered, so it reached the quad budget and
        // never the picture - see MapParticleDrawOrderTests.
        string? map = Source("src", "ReyEngine.App", "Services", "D3D11MapParticles.cs");
        Assert.NotNull(map);
        Assert.Contains("_pending.OrderBy(static e => VfxDrawOrder.KeyFor(e.Def))", map);
    }

    [Fact]
    public void TheFieldIsNoLongerFiledOrBadgedAsUnused()
    {
        // it decides the picture now, so the editor must not go on saying the viewport will not change
        Assert.DoesNotContain("isGroundLayer", VfxParkedEmitterFields.Names);
        Assert.Null(VfxPreviewCoverage.IgnoredNote(Core.Hashing.HashAlgorithms.Fnv1a("isGroundLayer")));
        // un-badging needs the resolver to declare the hash as a constant, because the coverage set is
        // built by reflecting over those fields and cannot see a hash computed inside a method
        Assert.True(VfxPreviewCoverage.IsParsed(Core.Hashing.HashAlgorithms.Fnv1a("isGroundLayer")));
    }
}
