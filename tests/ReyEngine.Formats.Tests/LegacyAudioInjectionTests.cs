using System.Numerics;
using System.Text;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Audio;
using ReyEngine.App.ViewModels;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M575: putting the ported audio somewhere the game will actually load it.
///
/// <para>A .bnk dropped into a WAD does nothing on its own — a map plays only the banks its
/// <c>MapAudioDataProperties</c> names. Rather than ship (and then maintain) a modified copy of the map
/// bin, the port extends a bank the map already declares. That makes "everything already in those banks
/// still works" the property that matters most here, so it is what most of these assert.</para>
/// </summary>
public sealed class LegacyAudioInjectionTests
{
    private const string Map453Wad =
        @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping\Map453.wad.client";

    private static byte[] FakeWem(int payload, ushort channels = 1)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(4 + 8 + 16 + 8 + payload);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((ushort)0xFFFF); w.Write(channels); w.Write(44100); w.Write(88200); w.Write((ushort)2); w.Write((ushort)16);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(payload);
        for (int i = 0; i < payload; i++) w.Write((byte)(i & 0xFF));
        return ms.ToArray();
    }

    private static List<WwiseBankSound> Batch(string prefix, int n, uint firstId) =>
        Enumerable.Range(0, n)
            .Select(i => new WwiseBankSound($"Play_{prefix}_{i:00}", firstId + (uint)i, FakeWem(80 + i * 5)))
            .ToList();

    private static AudioBankSet Reader(byte[] events, byte[] audio)
    {
        var set = new AudioBankSet();
        set.AddBank(BnkFile.Parse(events)!, 0, "e");
        set.AddBank(BnkFile.Parse(audio)!, 0, "a");
        return set;
    }

    private static int CountObjects(byte[] bank, byte type)
    {
        int found = 0;
        using var ms = new MemoryStream(bank, false);
        using var r = new BinaryReader(ms);
        while (ms.Position + 8 <= ms.Length)
        {
            string sig = Encoding.ASCII.GetString(r.ReadBytes(4));
            uint size = r.ReadUInt32();
            long end = ms.Position + size;
            if (sig == "HIRC")
            {
                uint count = r.ReadUInt32();
                for (uint i = 0; i < count && ms.Position < end; i++)
                {
                    byte t = r.ReadByte(); uint osize = r.ReadUInt32(); r.ReadUInt32();
                    if (t == type) found++;
                    ms.Position += osize - 4;
                }
            }
            ms.Position = end;
        }
        return found;
    }

    [Fact]
    public void AddedSoundsPlayAndTheOnesAlreadyThereAreUntouched()
    {
        var original = Batch("existing", 3, 500);
        var pair = WwiseBankWriter.Build("ENV_Host_SFX", original)!;
        var extra = Batch("added", 4, 900);

        var result = WwiseBankInjector.Append(pair.EventsBank, pair.AudioBank, "test", extra, out string? error);
        Assert.True(result is not null, error);
        Assert.Equal(4, result!.AddedEvents.Count);

        var after = Reader(result.EventsBank, result.AudioBank);
        foreach (var s in original.Concat(extra))
        {
            var wems = after.ResolveEvent(s.EventName);
            Assert.True(wems.Count == 1, $"{s.EventName} resolved to {wems.Count}");
            Assert.Equal(s.Wem, after.GetWemData(s.WemId));
        }
    }

    [Fact]
    public void RunningTheSameImportTwiceAddsNothing()
    {
        // A re-port must not stack a second copy of every sound into the bank it already extended.
        var pair = WwiseBankWriter.Build("ENV_Host_SFX", Batch("existing", 2, 500))!;
        var extra = Batch("added", 3, 900);
        var once = WwiseBankInjector.Append(pair.EventsBank, pair.AudioBank, "test", extra, out _)!;
        var twice = WwiseBankInjector.Append(once.EventsBank, once.AudioBank, "test", extra, out string? error);

        Assert.Null(twice);
        Assert.Contains("already", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(5, BnkFile.Parse(once.AudioBank)!.Wems.Count);
    }

    [Fact]
    public void ASecondBatchJoinsTheMixerAlreadyThereInsteadOfMakingANewOne()
    {
        // An ActorMixer that does not list a child leaves that child orphaned and silent, and two mixers
        // under one seed would mean the second batch's sounds hang off a mixer nothing else references.
        var pair = WwiseBankWriter.Build("ENV_Host_SFX", Batch("existing", 2, 500))!;
        var first = WwiseBankInjector.Append(pair.EventsBank, pair.AudioBank, "test", Batch("a", 3, 900), out _)!;
        var second = WwiseBankInjector.Append(first.EventsBank, first.AudioBank, "test", Batch("b", 2, 950), out string? error);
        Assert.True(second is not null, error);

        // one from the original Build, one from the injector's seed - and no third
        Assert.Equal(2, CountObjects(second!.EventsBank, WwiseBankWriter.HircActorMixer));

        var after = Reader(second.EventsBank, second.AudioBank);
        foreach (var s in Batch("a", 3, 900).Concat(Batch("b", 2, 950)))
            Assert.Single(after.ResolveEvent(s.EventName));

        // every sound is claimed by some mixer's child list
        var mixerChildren = new HashSet<uint>();
        var sounds = new HashSet<uint>();
        using (var ms = new MemoryStream(second.EventsBank, false))
        using (var r = new BinaryReader(ms))
            while (ms.Position + 8 <= ms.Length)
            {
                string sig = Encoding.ASCII.GetString(r.ReadBytes(4));
                uint size = r.ReadUInt32();
                long end = ms.Position + size;
                if (sig == "HIRC")
                {
                    uint count = r.ReadUInt32();
                    for (uint i = 0; i < count && ms.Position < end; i++)
                    {
                        byte t = r.ReadByte(); uint osize = r.ReadUInt32(); uint id = r.ReadUInt32();
                        int start = (int)ms.Position, len = (int)osize - 4;
                        var payload = second.EventsBank.AsSpan(start, len).ToArray();
                        if (t == WwiseBankWriter.HircSound) sounds.Add(id);
                        if (t == WwiseBankWriter.HircActorMixer)
                            for (int c = payload.Length - 4; c >= 0; c -= 4)
                            {
                                uint n = BitConverter.ToUInt32(payload, c);
                                if (c + 4 + n * 4 != payload.Length) continue;
                                for (int k = 0; k < n; k++)
                                    mixerChildren.Add(BitConverter.ToUInt32(payload, c + 4 + k * 4));
                                break;
                            }
                        ms.Position = start + len;
                    }
                }
                ms.Position = end;
            }
        Assert.Equal(sounds, mixerChildren.Intersect(sounds).ToHashSet());
        Assert.All(sounds, s => Assert.Contains(s, mixerChildren));
    }

    [Fact]
    public void ABankAtAnotherVersionIsRefusedRatherThanGuessedAt()
    {
        // The object offsets here were measured at 145 only. Writing them into a v88 or v134 bank would
        // produce a file that loads and behaves unpredictably, which is worse than a clear refusal.
        var pair = WwiseBankWriter.Build("ENV_Host_SFX", Batch("existing", 2, 500))!;
        var downgraded = (byte[])pair.EventsBank.Clone();
        BitConverter.GetBytes(134u).CopyTo(downgraded, 8);

        Assert.Null(WwiseBankInjector.Append(downgraded, pair.AudioBank, "test", Batch("a", 1, 900), out string? error));
        Assert.Contains("145", error);
    }

    // ---- MapAudio placements -----------------------------------------------------------------

    private static uint H(string s) => HashAlgorithms.Fnv1a(s);
    private const uint ContainerHash = 0x5000u;

    private static byte[] BinWithContainer()
    {
        var items = new BinTreeMap(H("items"), BinPropertyType.Hash, BinPropertyType.Struct,
            new[]
            {
                new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                    new BinTreeHash(0, 0xAAAAAAAAu),
                    new BinTreeStruct(0, H("MapAudio"), new BinTreeProperty[]
                    {
                        new BinTreeMatrix44(H("transform"), Matrix4x4.CreateTranslation(1f, 2f, 3f)),
                        new BinTreeString(H("name"), "Existing"),
                        new BinTreeString(H("eventName"), "Play_sfx_Existing"),
                    })),
            });
        var container = new BinTreeObject(ContainerHash, H("MapPlaceableContainer"), new BinTreeProperty[] { items });
        using var ms = new MemoryStream();
        new BinTree(new[] { container }, Array.Empty<string>()).Write(ms);
        return ms.ToArray();
    }

    [Fact]
    public void APlacedSoundCarriesExactlyTheThreeFieldsRiotWrites()
    {
        // Measured on Map453's eight water emitters: transform, name, EventName and nothing else. An
        // invented extra field is a divergence from shipped data with no evidence behind it.
        byte[] bin = BinWithContainer();
        var id = new MapPlacementId(ContainerHash, 0xBEEF0001u);
        var transform = Matrix4x4.CreateTranslation(7000f, -50f, 9000f);

        byte[]? outBytes = MapPlaceableWriter.WriteEdits(bin, new[]
        {
            new MapPlacementEdit(id)
            {
                CreateSound = true, Name = "LegacyPort_map2_Ambience",
                EventName = "Play_sfx_Env_LegacyPort_map2_ENV_11", Transform = transform,
            },
        }, out string? error);
        Assert.True(outBytes is not null, error);

        var sounds = MapPlaceableExtractor.Extract(outBytes!).Sounds;
        Assert.Equal(2, sounds.Count);
        var added = Assert.Single(sounds, s => s.Name == "LegacyPort_map2_Ambience");
        Assert.Equal("Play_sfx_Env_LegacyPort_map2_ENV_11", added.EventName);
        Assert.Equal(transform.Translation, added.Position);

        var tree = new BinTree(new MemoryStream(outBytes!, false));
        var item = ((BinTreeMap)tree.Objects[ContainerHash].Properties[H("items")])
            .First(e => e.Key is BinTreeHash k && k.Value == id.ItemKey).Value;
        var s2 = Assert.IsType<BinTreeStruct>(item);
        Assert.Equal(H("MapAudio"), s2.ClassHash);
        Assert.Equal(3, s2.Properties.Count);
    }

    [Fact]
    public void AddingASoundLeavesTheOneAlreadyThereAlone()
    {
        byte[] bin = BinWithContainer();
        byte[]? outBytes = MapPlaceableWriter.WriteEdits(bin, new[]
        {
            new MapPlacementEdit(new MapPlacementId(ContainerHash, 0xBEEF0002u))
            {
                CreateSound = true, EventName = "Play_sfx_New", Transform = Matrix4x4.Identity,
            },
        }, out string? error);
        Assert.True(outBytes is not null, error);

        var existing = Assert.Single(MapPlaceableExtractor.Extract(outBytes!).Sounds, s => s.Name == "Existing");
        Assert.Equal("Play_sfx_Existing", existing.EventName);
        Assert.Equal(new Vector3(1f, 2f, 3f), existing.Position);
    }

    [Fact]
    public void ASoundWithNoEventOrNoPlaceIsRefused()
    {
        byte[] bin = BinWithContainer();
        Assert.Null(MapPlaceableWriter.WriteEdits(bin, new[]
        {
            new MapPlacementEdit(new MapPlacementId(ContainerHash, 0xBEEF0003u))
            { CreateSound = true, Transform = Matrix4x4.Identity },        // no event name
        }, out _));
        Assert.Null(MapPlaceableWriter.WriteEdits(bin, new[]
        {
            new MapPlacementEdit(new MapPlacementId(ContainerHash, 0xBEEF0004u))
            { CreateSound = true, EventName = "Play_sfx_New" },            // no transform
        }, out _));
    }

    [Fact]
    public void AnExistingSoundCanBeRepointedAtADifferentEvent()
    {
        byte[] bin = BinWithContainer();
        byte[]? outBytes = MapPlaceableWriter.WriteEdits(bin, new[]
        {
            new MapPlacementEdit(new MapPlacementId(ContainerHash, 0xAAAAAAAAu)) { EventName = "Play_sfx_Other" },
        }, out string? error);
        Assert.True(outBytes is not null, error);
        Assert.Equal("Play_sfx_Other", Assert.Single(MapPlaceableExtractor.Extract(outBytes!).Sounds).EventName);
    }


    // ---- the port dialog's switch ----------------------------------------------------------------

    private static LegacyMapPortResult EmptyPortResult() => new(
        Array.Empty<byte>(), Array.Empty<LegacyTextureCopy>(), Array.Empty<LegacyMaterialPlan>(),
        "room.nvr", "NVR", 0, 0, 0, 0, 0, Array.Empty<string>());

    [Fact]
    public void TheImportSoundsSwitchReachesTheSelectionThePortActsOn()
    {
        // The switch, the view model property and the record field are three separate places; a break in
        // any one of them is a checkbox that does nothing and no error anywhere.
        var destination = new LegacyDestinationContentSummary(0, 0, 0, 0, 0, 0, 0, 0);
        var vm = new LegacyMapPortWindowViewModel(EmptyPortResult(), new[] { "DefaultEnv" }, destination);
        Assert.True(vm.ImportLegacySounds);        // on by default: the ambience is the point of the port
        // Confirm refuses while any role has no shader, which has nothing to do with audio.
        foreach (var row in vm.ShaderRows) row.SelectedShader = "DefaultEnv";

        LegacyMapPortShaderSelection? captured = null;
        vm.Confirmed = s => captured = s;
        vm.ImportLegacySounds = false;
        vm.ConfirmCommand.Execute(null);
        Assert.NotNull(captured);
        Assert.False(captured!.ImportLegacySounds);

        vm.ImportLegacySounds = true;
        vm.ConfirmCommand.Execute(null);
        Assert.True(captured!.ImportLegacySounds);
    }

    [Fact]
    public void TheDialogMarkupBindsToPropertiesThatExist()
    {
        // XAML bindings are resolved at RUNTIME: a name that does not exist compiles, passes every test,
        // and then silently does nothing in the dialog. Checking the two names this milestone added is
        // cheap next to finding out from a user that the checkbox is inert.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return;
        string xaml = Path.Combine(dir.FullName, "src", "ReyEngine.App", "Views", "LegacyMapPortWindow.axaml");
        if (!File.Exists(xaml)) return;

        string markup = File.ReadAllText(xaml);
        Assert.Contains("{Binding ImportLegacySounds}", markup);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                     markup, @"IsChecked=""\{Binding (\w+)\}"""))
            Assert.True(typeof(LegacyMapPortWindowViewModel).GetProperty(m.Groups[1].Value) is not null,
                $"the dialog binds IsChecked to '{m.Groups[1].Value}', which the view model does not have");
    }

    // ---- against the shipped game, when it is installed ---------------------------------------

    [Fact]
    public void TheMapsOwnBankDeclarationIsReadableAndNamesAMapSpecificHost()
    {
        if (!File.Exists(Map453Wad)) return;
        using var wad = ReyEngine.Core.Wad.WadArchive.Open(Map453Wad);
        ulong hash = HashAlgorithms.WadPath("data/maps/shipping/map453/map453.bin");
        if (!wad.TryGetEntry(hash, out _)) return;

        var units = MapAudioDeclaration.Read(wad.Extract(hash));
        Assert.NotEmpty(units);
        Assert.Contains(units, u => u.BankPaths.Any(p => p.Contains("MODE_Jade_SFX", StringComparison.OrdinalIgnoreCase)));

        int Media(string path)
        {
            ulong h = HashAlgorithms.WadPath(path.ToLowerInvariant());
            if (!wad.TryGetEntry(h, out _)) return -1;
            try { return BnkFile.Parse(wad.Extract(h))?.Wems.Count ?? -1; } catch { return -1; }
        }
        var host = MapAudioDeclaration.HostCandidates(units, Media).FirstOrDefault();
        Assert.NotNull(host.Events);
        // A bank shared with every other map must never win: extending it would push this map's ambience
        // into whatever else the mod is loaded alongside.
        Assert.DoesNotContain("global", host.Audio, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gameplay", host.Audio, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExtendingTheRealMapBankKeepsEveryEventTheGameShipped()
    {
        if (!File.Exists(Map453Wad)) return;
        using var wad = ReyEngine.Core.Wad.WadArchive.Open(Map453Wad);
        ulong eventsHash = HashAlgorithms.WadPath("assets/sounds/wwise2016/sfx/shared/mode_jade_sfx_events.bnk");
        ulong audioHash = HashAlgorithms.WadPath("assets/sounds/wwise2016/sfx/shared/mode_jade_sfx_audio.bnk");
        if (!wad.TryGetEntry(eventsHash, out _) || !wad.TryGetEntry(audioHash, out _)) return;

        byte[] events0 = wad.Extract(eventsHash), audio0 = wad.Extract(audioHash);
        var before = Reader(events0, audio0);
        var shipped = BnkFile.Parse(events0)!;

        var extra = Batch("legacyport", 5, 4_000_000_000);
        var result = WwiseBankInjector.Append(events0, audio0, "map2", extra, out string? error);
        Assert.True(result is not null, error);

        var after = Reader(result!.EventsBank, result.AudioBank);
        foreach (uint ev in shipped.Events.Keys)
        {
            var w0 = before.ResolveEvent(ev);
            Assert.Equal(w0, after.ResolveEvent(ev));
            foreach (uint id in w0)
                Assert.Equal(before.GetWemData(id), after.GetWemData(id));
        }
        foreach (var s in extra) Assert.Equal(s.Wem, after.GetWemData(s.WemId));
    }
}
