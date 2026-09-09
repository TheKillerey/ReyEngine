using System.Numerics;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Skeletons;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.Vfx;

namespace ReyEngine.Formats.Tests;

public sealed class SpellVariantPreviewTests
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private static readonly Lazy<HashDatabase> Database = new(() => new HashSyncService().LoadLocal(_ => { }));
    private sealed record Skin(Dictionary<uint, VfxSystemDefinition> Systems,
        Dictionary<uint, uint> Resources, IReadOnlyList<AnimClipInfo> Clips, IReadOnlyList<AbilitySlot> Abilities);

    private static Skin? Read(string champion)
    {
        string file = Path.Combine(Final, "Champions", champion + ".wad.client");
        if (!File.Exists(file)) return null;
        var db = Database.Value;
        using var wad = WadArchive.Open(file, new WadPathResolver(db));
        string lower = champion.ToLowerInvariant();
        var systems = new Dictionary<uint, VfxSystemDefinition>();
        var resources = new Dictionary<uint, uint>();
        var queue = new Queue<ulong>();
        var visited = new HashSet<ulong>();
        queue.Enqueue(HashAlgorithms.WadPath($"data/characters/{lower}/skins/skin0.bin"));
        while (queue.TryDequeue(out var hash))
        {
            if (!visited.Add(hash) || !wad.TryGetEntry(hash, out _)) continue;
            var bytes = wad.Extract(hash);
            foreach (var (k, v) in VfxSystemResolver.ExtractAll(bytes)) systems.TryAdd(k, v);
            foreach (var (k, v) in VfxSystemResolver.ExtractResourceMap(bytes)) resources.TryAdd(k, v);
            foreach (var dep in VfxSystemResolver.ExtractDependencies(bytes)) queue.Enqueue(HashAlgorithms.WadPath(dep));
        }
        var clips = ChampionAnimationData.ParseClips(wad.Extract(HashAlgorithms.WadPath($"data/characters/{lower}/animations/skin0.bin")),
            h => db.TryGetBinName(h, out var n) ? n : null, h => db.TryGetPath(h, out var p) ? p : null);
        var abilities = ChampionSpellData.Read(wad.Extract(HashAlgorithms.WadPath(ChampionRecord.PathFor(lower))), champion);
        return new Skin(systems, resources, clips, abilities);
    }

    [Fact]
    public void AatroxCastsUseSeparateEffectsAndActualSwings()
    {
        if (Read("Aatrox") is not { } skin) return;
        var events = ChampionEventBuilder.Build(skin.Systems, skin.Clips);
        Assert.DoesNotContain(events, e => e.Name == "Q");
        for (int cast = 1; cast <= 3; cast++)
        {
            var ev = Assert.Single(events, e => e.Name == "Q" + cast);
            Assert.Equal($"aatrox_ground_q{cast}.anm", ev.ClipAnmFile);
            var names = ev.CasterSystems.Select(h => skin.Systems[h].Name).ToArray();
            Assert.Equal(3, names.Length);
            Assert.Contains($"Aatrox_Base_Q_cas{cast}", names);
            Assert.Contains($"Aatrox_Base_Q_Indicator_0{cast}", names);
            Assert.Contains($"Aatrox_Base_Q_Trail_0{cast}", names);
        }
        var actions = CharacterActions.Build(Array.Empty<string>(), skin.Clips);
        Assert.EndsWith("aatrox_ground_q1.anm", actions[0].Clip!.AnmPath);

        var vm = new MeshPreviewViewModel();
        vm.SetVfx(skin.Systems, skin.Resources);
        vm.SetActions(actions);
        for (int i = 0; i < 4; i++)
        {
            vm.SelectedAction = null;
            vm.SelectedAction = vm.Actions[0];
            Assert.Equal("Q" + (i % 3 + 1), vm.SelectedEvent?.Name);
        }
    }

    [Fact]
    public void BlitzcrankSpellRecordSelectsOneOutboundMissile()
    {
        if (Read("Blitzcrank") is not { } skin) return;
        var vm = new MeshPreviewViewModel();
        vm.SetVfx(skin.Systems, skin.Resources);
        vm.SetAbilities(skin.Abilities);
        var q = Assert.Single(vm.ChampionEvents, e => e.Name == "Q");
        uint missile = Assert.Single(q.MissileSystems);
        Assert.Equal("Blitzcrank_Base_Q_mis", skin.Systems[missile].Name);
        vm.SelectedEvent = q;
        var flying = Assert.Single(vm.Playback!.Items, i => i.System.PathHash == missile);
        Assert.Equal(missile, flying.System.PathHash);
        Assert.Equal(flying.WorldPos, flying.BeamTarget);
        Assert.Equal(3, vm.Playback.Items.Count);
        var returning = Assert.Single(vm.Playback.Items, i => i.System.Name.EndsWith("_return"));
        Assert.Equal(flying.EndTime, returning.StartDelay);
        Assert.Equal(flying.WorldPos, returning.TravelTo);
        Assert.NotNull(returning.System.Emitters.Single(e => e.Name == "cable").Beam);
    }

    [Theory]
    [InlineData(300, 25, -400)]
    [InlineData(0, 500, 0)]
    public void TetherMeshEndpointsStayAnchored(float x, float y, float z)
    {
        var source = new Vector3(20, 50, -60);
        var target = source + new Vector3(x, y, z);
        var model = VfxBeamMesh.Transform(source, target, new Vector2(-10, 10), new Vector2(50, 50));
        Assert.True(Vector3.Distance(source, Vector3.Transform(new Vector3(0, 0, -10), model)) < 0.001f);
        Assert.True(Vector3.Distance(target, Vector3.Transform(new Vector3(0, 0, 10), model)) < 0.001f);
        Assert.Equal(50f, Vector3.TransformNormal(Vector3.UnitX, model).Length(), 3);
    }
}
