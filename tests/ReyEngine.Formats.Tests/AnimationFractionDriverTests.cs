using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M856: <c>FloatGraph(AnimationFraction)</c> drivers evaluate. Zyra's seed (skin52 Head) drives
/// DissolveBias with a FloatGraph over the fraction of one animation (1 at 0, 0 from 25%); the reader
/// used to stop at the inner driver, so the authored 0 stood and the surface drew nothing at rest -
/// 20 of the 57 drivers the audit could not evaluate were this shape.
/// </summary>
public sealed class AnimationFractionDriverTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);
    private static string? Resolve(uint h) => KnownNames.GetValueOrDefault(h);
    private static readonly Dictionary<uint, string> KnownNames = new[]
    {
        "DynamicMaterialDef", "DynamicMaterialParameterDef", "FloatGraphMaterialDriver", "ColorGraphMaterialDriver",
        "AnimationFractionDynamicMaterialFloatDriver", "VfxAnimatedFloatVariableData", "RemapFloatMaterialDriver",
        "death", "Respawn",
    }.ToDictionary(H, n => n);

    private static BinTreeStruct AnimationFraction(string? clip) => new(0, H("AnimationFractionDynamicMaterialFloatDriver"),
        clip is null ? Array.Empty<BinTreeProperty>() : new BinTreeProperty[] { new BinTreeHash(H("mAnimationName"), H(clip)) });

    private static BinTreeStruct Graph(BinTreeStruct inner, float[] times, float[] values) => new(0, H("FloatGraphMaterialDriver"), new BinTreeProperty[]
    {
        new BinTreeStruct(H("driver"), H("AnimationFractionDynamicMaterialFloatDriver"), inner.Properties.Values.ToArray()),
        new BinTreeStruct(H("graph"), H("VfxAnimatedFloatVariableData"), new BinTreeProperty[]
        {
            new BinTreeContainer(H("times"), BinPropertyType.F32, times.Select(t => (BinTreeProperty)new BinTreeF32(0, t)).ToArray()),
            new BinTreeContainer(H("values"), BinPropertyType.F32, values.Select(t => (BinTreeProperty)new BinTreeF32(0, t)).ToArray()),
        }),
    });

    private static MaterialDynamicParameter Param(BinTreeStruct driver)
    {
        var entry = new BinTreeStruct(0, H("DynamicMaterialParameterDef"), new BinTreeProperty[]
        {
            new BinTreeString(H("name"), "DissolveBias"),
            new BinTreeStruct(H("driver"), driver.ClassHash, driver.Properties.Values.ToArray()),
        });
        var def = new BinTreeStruct(H("dynamicMaterial"), H("DynamicMaterialDef"), new BinTreeProperty[]
        {
            new BinTreeContainer(H("parameters"), BinPropertyType.Struct, new BinTreeProperty[] { entry }),
        });
        return Assert.Single(MaterialDrivers.Parse(def, Resolve));
    }

    private static MaterialDynamicParameter DeathDissolve() =>
        Param(Graph(AnimationFraction("death"), new[] { 0f, 0.25f }, new[] { 1f, 0f }));

    [Fact]
    public void A_graph_over_animation_fraction_is_no_longer_unevaluated()
    {
        var p = DeathDissolve();
        Assert.NotNull(p.RestValue);
        Assert.DoesNotContain("not evaluated", p.RestReason);
    }

    /// <summary>No clip playing reads fraction 0 - the start of the graph, 1 here, so the surface draws.</summary>
    [Fact]
    public void With_no_clip_playing_the_fraction_is_zero()
    {
        Assert.Equal(1f, DeathDissolve().RestValue!.Value.X);
    }

    [Theory]
    [InlineData(0f, 1f)]
    [InlineData(0.125f, 0.5f)]
    [InlineData(0.25f, 0f)]
    [InlineData(1f, 0f)]
    public void The_playing_clips_fraction_feeds_the_graph(float fraction, float expected)
    {
        var state = MaterialDriverState.Rest.WithClip("Death", fraction);   // the name hash is case-insensitive
        Assert.Equal(expected, DeathDissolve().Evaluate(state).Value!.Value.X, 4);
    }

    [Fact]
    public void A_different_clip_playing_leaves_the_driver_at_zero()
    {
        var state = MaterialDriverState.Rest.WithClip("Respawn", 0.9f);
        Assert.Equal(1f, DeathDissolve().Evaluate(state).Value!.Value.X);
    }

    [Fact]
    public void A_driver_without_a_name_follows_whichever_clip_plays()
    {
        var p = Param(Graph(AnimationFraction(null), new[] { 0f, 1f }, new[] { 0f, 10f }));
        Assert.Equal(5f, p.Evaluate(MaterialDriverState.Rest.WithClip("Respawn", 0.5f)).Value!.Value.X, 4);
    }

    [Fact]
    public void The_clip_survives_the_other_state_edits()
    {
        var s = MaterialDriverState.Rest.WithClip("death", 0.2f).With("IsDead", true).WithSeconds(3f);
        Assert.Equal("death", s.ClipName);
        Assert.Equal(0.2f, s.ClipFraction);
    }

    [Fact]
    public void A_non_finite_fraction_reads_as_zero()
    {
        Assert.Equal(0f, MaterialDriverState.Rest.WithClip("death", float.NaN).ClipFraction);
        Assert.Equal(1f, MaterialDriverState.Rest.WithClip("death", 7f).ClipFraction);
    }

    /// <summary>The real asset: Zyra's seed Head. Skipped without the game install.</summary>
    [Fact]
    public void Zyra_seed_dissolve_bias_evaluates_at_rest()
    {
        const string wadPath = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Champions\Zyra.wad.client";
        if (!File.Exists(wadPath)) return;
        HashDatabase db;
        try { db = new HashSyncService().LoadLocal(_ => { }); } catch { return; }
        using var wad = WadArchive.Open(wadPath, new WadPathResolver(db));
        ulong h = HashAlgorithms.WadPath("data/characters/zyraseed/skins/skin52.bin");
        if (!wad.TryGetEntry(h, out _)) return;
        var doc = MaterialDocument.Parse(wad.Extract(h), x => db.TryGetBinName(x, out var n) ? n : null);
        var bias = doc.Materials.SelectMany(m => m.DynamicParameters)
            .First(p => p.Name == "DissolveBias" && p.Driver.Contains("AnimationFraction"));
        Assert.Equal(1f, bias.RestValue!.Value.X);
    }
}
