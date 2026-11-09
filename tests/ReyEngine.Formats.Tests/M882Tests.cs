using System.Numerics;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters.Simulation;
using ReyEngine.Formats.Vfx;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>M882: clip events keep their bone on a stage, beams aim at the dummy only when told to, and a clip-less root spell plays its child attack's clip.</summary>
public sealed class M882Tests(ITestOutputHelper output)
{
    [Fact]
    public void AClipEventIsAtTheDummyOnlyForATargetSystemWhileADummyExists()
    {
        // the Map11 stage: the CHARACTER stands far from the origin, the dummy beside it
        var dummy = new Vector3(7724f, 54f, 7158f);
        Assert.False(MeshPreviewViewModel.ClipEventIsAtDummy(dummy, "Kaisa_Base_Recall"));
        Assert.False(MeshPreviewViewModel.ClipEventIsAtDummy(dummy, "Kaisa_Base_Q_Hand"));
        Assert.True(MeshPreviewViewModel.ClipEventIsAtDummy(dummy, "Kayn_Base_R_tar_enemy"));
        Assert.False(MeshPreviewViewModel.ClipEventIsAtDummy(null, "Kayn_Base_R_tar_enemy"));
    }

    private static VfxPlaybackItem Item(string name)
    {
        var emitter = new VfxEmitterDefinition(name, VfxCurveF.Const(1), VfxCurveF.Const(0.25f),
            0.2f, 0, 0, true, false, 3, VfxCurve3.Const(new Vector3(50)), null,
            VfxCurve4.Const(Vector4.One), null, null, null, null, VfxCurve3.Const(Vector3.Zero),
            "assets/test/white.dds", Vector2.One, 1, false, false);
        return new VfxPlaybackItem(new VfxSystemDefinition(1, name, "", new[] { emitter }), Matrix4x4.Identity, new TextureImage?[1]);
    }

    [Fact]
    public void CharacterSemanticsReachTheChildrenAndNothingAimsAtTheDummyByDefault()
    {
        var child = Item("child");
        var grand = Item("grand");
        child = child with { EmitterChildren = new IReadOnlyList<VfxPlaybackItem>?[] { new[] { grand } } };
        var parent = Item("parent") with { EmitterChildren = new IReadOnlyList<VfxPlaybackItem>?[] { new[] { child }, null } };
        var made = MeshPreviewViewModel.CharacterItem(parent);
        var c = made.EmitterChildren![0]![0];
        Assert.True(made.CharacterSemantics && made.EmitSingleOnce);
        Assert.True(c.CharacterSemantics && c.EmitSingleOnce);
        Assert.True(c.EmitterChildren![0]![0].CharacterSemantics);
        Assert.Null(made.EmitterChildren![1]);
        Assert.False(made.AimAtDummy);   // a caster clip event's beam (Kai'Sa recall) keeps its authored offset
        Assert.Equal(made, MeshPreviewViewModel.CharacterItem(made));   // idempotent: republish compares both forms
    }

    // ---------------------------------------------------------------- generator, real data

    private const string Game = @"C:\Riot Games\League of Legends\Game";
    private static readonly string Champions = Path.Combine(Game, "DATA", "FINAL", "Champions");
    private static readonly Lazy<(HashDatabase Db, WadPathResolver Resolver)> Hashes = new(() =>
    {
        var db = new HashSyncService().LoadLocal(_ => { });
        return (db, new WadPathResolver(db));
    });

    private static ChampionSimDefinition Def(string champion)
    {
        var context = new ChampionSimContext
        {
            GameVersion = ChampionSimSource.ReadGameVersion(Game),
            ResolveBinName = h => Hashes.Value.Db.TryGetBinName(h, out var n) ? n : null,
            ResolveWadPath = h => Hashes.Value.Db.TryGetPath(h, out var p) ? p : null,
        };
        using var wad = WadArchive.Open(ChampionSimBatch.WadPathOf(Champions, champion), Hashes.Value.Resolver);
        return ChampionSimGenerator.Generate(wad, champion, context);
    }

    [Fact]
    public void AClipLessRootWithAChildAttackPlaysNoCastClipAndFollowsWithTheChildsAttack()
    {
        Assert.Equal("M893.1", ChampionSimDefinition.CurrentGeneratorVersion);
        if (!Directory.Exists(Champions)) { output.WriteLine("SKIPPED: no game install"); return; }

        var blitz = Def("Blitzcrank");
        var e = blitz.Abilities.Single(a => a.Key == "E");
        Assert.Null(e.AuthoredClipName);
        Assert.Null(e.Clip);
        Assert.NotNull(e.FollowUpAttack?.Clip);
        output.WriteLine($"Blitz E follow-up: {e.FollowUpAttack!.Name} clip {e.FollowUpAttack.ClipName}, cast {e.FollowUpAttack.CastSeconds}");

        // W authors Spell3 itself: untouched
        var w = blitz.Abilities.Single(a => a.Key == "W");
        Assert.Equal("Spell3", w.AuthoredClipName);
        Assert.NotNull(w.Clip);
        Assert.Null(w.FollowUpAttack);

        var script = SimulationPlanner.Plan(blitz, e, new SimStage(Vector3.Zero, 0f, new Vector3(0f, 0f, 500f)));
        var attack = Assert.Single(script.Steps, s => s.Kind == SimStepKind.Attack);
        Assert.Equal(e.FollowUpAttack.Name, attack.Name);
        Assert.Equal(e.FollowUpAttack.Clip, attack.Clip);
        Assert.DoesNotContain(script.Steps, s => s.Kind == SimStepKind.Clip);   // the root itself plays no cast clip
        Assert.True(attack.Time >= (e.CastSeconds ?? 0f));

        foreach (var (champ, key) in new[] { ("Camille", "Q"), ("Nasus", "Q") })
        {
            var a = Def(champ).Abilities.Single(x => x.Key == key);
            Assert.Null(a.Clip);
            Assert.NotNull(a.FollowUpAttack?.Clip);
        }
    }
}
