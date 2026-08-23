using System.Text;
using ReyEngine.Formats.Audio;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M574: porting a legacy client's Wwise audio into a bank current League can load.
///
/// <para>Almost everything here is a contract the GAME enforces silently: a bank id that is not FNV-1 of
/// its file stem, an event id that is not FNV-1 of its name, a Sound with no parent, media that is not
/// 16-byte aligned. None of those throw — they just produce a bank that loads and never makes a sound,
/// which is indistinguishable from "the mod is broken". So each one is asserted rather than eyeballed.</para>
/// </summary>
public sealed class LegacyAudioPortTests
{
    private const string LegacyShared =
        @"K:\LeagueSandbox\League_Sandbox_Client\DATA\Sounds\Wwise\SFX\Shared";
    private const string LegacyLevel = @"K:\LeagueSandbox\League_Sandbox_Client\LEVELS\Map2";

    /// <summary>A wem the writer can pack: a RIFF header vgmstream would recognise plus filler.</summary>
    private static byte[] FakeWem(int payload, ushort tag = 0xFFFF, ushort channels = 1, int rate = 44100)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(4 + 8 + 16 + 8 + payload);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write(tag); w.Write(channels); w.Write(rate); w.Write(rate * 2); w.Write((ushort)2); w.Write((ushort)16);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(payload);
        for (int i = 0; i < payload; i++) w.Write((byte)(i & 0xFF));
        return ms.ToArray();
    }

    private static List<WwiseBankSound> Sample(int n) =>
        Enumerable.Range(1, n)
            .Select(i => new WwiseBankSound($"Play_sfx_Env_Test_{i:00}", (uint)(1000 + i), FakeWem(100 + i * 7)))
            .ToList();

    /// <summary>HIRC objects of a generated bank, as (type, id, payload).</summary>
    private static List<(byte Type, uint Id, byte[] Payload)> Hirc(byte[] bank)
    {
        var objects = new List<(byte, uint, byte[])>();
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
                    byte type = r.ReadByte(); uint osize = r.ReadUInt32(); uint id = r.ReadUInt32();
                    int start = (int)ms.Position, len = (int)osize - 4;
                    objects.Add((type, id, bank.AsSpan(start, len).ToArray()));
                    ms.Position = start + len;
                }
            }
            ms.Position = end;
        }
        return objects;
    }

    [Fact]
    public void TheGeneratedPairIsReadableAtTheVersionCurrentLeagueUses()
    {
        var pair = WwiseBankWriter.Build("ENV_Test_SFX", Sample(4));
        Assert.NotNull(pair);
        var events = BnkFile.Parse(pair!.EventsBank);
        var audio = BnkFile.Parse(pair.AudioBank);
        Assert.NotNull(events);
        Assert.NotNull(audio);
        Assert.Equal(WwiseBankWriter.Version, events!.Version);
        Assert.Equal(WwiseBankWriter.Version, audio!.Version);
        Assert.Equal(4, events.Events.Count);
        Assert.Equal(4, events.Sounds.Count);
        Assert.Equal(4, audio.Wems.Count);
        // the media belongs in the audio bank and the graph in the events bank, as Riot splits them
        Assert.Empty(events.Wems);
        Assert.Empty(audio.Events);
    }

    [Fact]
    public void EveryEventResolvesToItsOwnMediaByteForByte()
    {
        var sounds = Sample(6);
        var pair = WwiseBankWriter.Build("ENV_Test_SFX", sounds)!;
        var set = new AudioBankSet();
        set.AddBank(BnkFile.Parse(pair.EventsBank)!, 0, pair.EventsFileName);
        set.AddBank(BnkFile.Parse(pair.AudioBank)!, 0, pair.AudioFileName);

        foreach (var s in sounds)
        {
            var wems = set.ResolveEvent(s.EventName);
            Assert.True(wems.Count == 1, $"{s.EventName} resolved to {wems.Count} media");
            Assert.Equal(s.WemId, wems[0]);
            Assert.Equal(s.Wem, set.GetWemData(s.WemId));
        }
    }

    [Fact]
    public void BankIdsAreTheHashOfTheFileStem()
    {
        // Verified on all 529 banks in Map453.wad.client: the BKHD id is FNV-1 of the file name without
        // its extension. Wwise finds a bank by that id, so a wrong one is a bank nothing can reference.
        var pair = WwiseBankWriter.Build("ENV_Test_SFX", Sample(2))!;
        Assert.Equal(WwiseHash.Fnv1("ENV_Test_SFX_events"), BnkFile.Parse(pair.EventsBank)!.BankId);
        Assert.Equal(WwiseHash.Fnv1("ENV_Test_SFX_audio"), BnkFile.Parse(pair.AudioBank)!.BankId);
        Assert.Equal("ENV_Test_SFX_events.bnk", pair.EventsFileName);
        Assert.Equal("ENV_Test_SFX_audio.bnk", pair.AudioFileName);
    }

    [Fact]
    public void EventIdsAreTheHashOfTheEventName()
    {
        // The map bin refers to sounds by NAME; the bank stores only the hash. If these disagree the
        // placement silently plays nothing.
        var sounds = Sample(3);
        var pair = WwiseBankWriter.Build("ENV_Test_SFX", sounds)!;
        var events = BnkFile.Parse(pair.EventsBank)!;
        foreach (var s in sounds)
            Assert.True(events.Events.ContainsKey(WwiseHash.Fnv1(s.EventName)), $"{s.EventName} is not in the bank");
    }

    [Fact]
    public void EverySoundHangsOffTheMixerAndTheMixerListsThemAll()
    {
        // A Sound with no parent has no output bus and cannot be heard, and a mixer that does not list a
        // child leaves that child orphaned. Both load without complaint.
        var pair = WwiseBankWriter.Build("ENV_Test_SFX", Sample(5))!;
        var objects = Hirc(pair.EventsBank);
        var mixer = Assert.Single(objects, o => o.Type == 7);
        var soundObjects = objects.Where(o => o.Type == 2).ToList();
        Assert.Equal(5, soundObjects.Count);

        foreach (var s in soundObjects)
            Assert.Equal(mixer.Id, BitConverter.ToUInt32(s.Payload, 23));

        Assert.Equal(WwiseBankWriter.AmbienceBusId, BitConverter.ToUInt32(mixer.Payload, 5));
        Assert.Equal(0u, BitConverter.ToUInt32(mixer.Payload, 9));   // the mixer is its own root

        int tail = mixer.Payload.Length - 4 - soundObjects.Count * 4;
        Assert.Equal((uint)soundObjects.Count, BitConverter.ToUInt32(mixer.Payload, tail));
        var children = Enumerable.Range(0, soundObjects.Count)
            .Select(i => BitConverter.ToUInt32(mixer.Payload, tail + 4 + i * 4)).ToHashSet();
        Assert.Equal(soundObjects.Select(s => s.Id).ToHashSet(), children);
    }

    [Fact]
    public void EachActionPlaysItsOwnSoundAndNamesTheBankThatOwnsIt()
    {
        var pair = WwiseBankWriter.Build("ENV_Test_SFX", Sample(4))!;
        var objects = Hirc(pair.EventsBank);
        uint bankId = BnkFile.Parse(pair.EventsBank)!.BankId;
        var soundIds = objects.Where(o => o.Type == 2).Select(o => o.Id).ToHashSet();

        var actions = objects.Where(o => o.Type == 3).ToList();
        Assert.Equal(4, actions.Count);
        foreach (var a in actions)
        {
            Assert.Equal(0x0403, BitConverter.ToUInt16(a.Payload, 0));       // play, game-object scope
            Assert.Contains(BitConverter.ToUInt32(a.Payload, 2), soundIds);
            Assert.Equal(bankId, BitConverter.ToUInt32(a.Payload, 10));
        }
        Assert.Equal(4, actions.Select(a => BitConverter.ToUInt32(a.Payload, 2)).Distinct().Count());
    }

    [Fact]
    public void MediaStaysSixteenByteAlignedInsideData()
    {
        // Riot 16-aligns every DIDX offset; Wwise wants aligned media for memory-mapped playback.
        var pair = WwiseBankWriter.Build("ENV_Test_SFX", Sample(7))!;
        var audio = BnkFile.Parse(pair.AudioBank)!;
        Assert.All(audio.Wems.Values, e => Assert.Equal(0, e.Offset % 16));
    }

    [Fact]
    public void RepeatedEventNamesAndMediaIdsAreDroppedNotDuplicated()
    {
        // Two HIRC objects under one id, or two files under one media id, is silent corruption.
        var wem = FakeWem(64);
        var pair = WwiseBankWriter.Build("ENV_Test_SFX", new List<WwiseBankSound>
        {
            new("Play_a", 1, wem),
            new("play_A", 2, wem),      // same name, different case
            new("Play_b", 1, wem),      // same media id
            new("Play_c", 3, wem),
        })!;
        Assert.Equal(new[] { "Play_a", "Play_c" }, pair.EventNames);
        Assert.Equal(2, BnkFile.Parse(pair.AudioBank)!.Wems.Count);
    }

    [Fact]
    public void NothingToWriteProducesNoBank()
    {
        Assert.Null(WwiseBankWriter.Build("ENV_Test_SFX", Array.Empty<WwiseBankSound>()));
        Assert.Null(WwiseBankWriter.Build("", Sample(2)));
        Assert.Null(WwiseBankWriter.Build("ENV_Test_SFX", new[] { new WwiseBankSound("x", 1, Array.Empty<byte>()) }));
    }

    // ---- against the real legacy client, when it is on this machine -----------------------------

    [Fact]
    public void LegacyEventsResolveToMediaAtBankVersion88()
    {
        // The regression behind "the legacy banks have no sounds": at v88 an Event's action count is a u32,
        // and reading it as a u8 assembles the action id from the wrong four bytes, so every event
        // resolved to nothing at all.
        string path = Path.Combine(LegacyShared, "ENV_Map1_SFX_events.bnk");
        if (!File.Exists(path)) return;
        var bnk = BnkFile.Parse(File.ReadAllBytes(path));
        Assert.NotNull(bnk);
        Assert.Equal(88u, bnk!.Version);
        Assert.NotEmpty(bnk.Events);
        foreach (var (id, actions) in bnk.Events)
        {
            Assert.NotEmpty(actions);
            Assert.All(actions, a => Assert.True(bnk.ActionTargets.ContainsKey(a),
                $"event 0x{id:x8} points at action 0x{a:x8}, which the bank does not define"));
        }
    }

    [Fact]
    public void ClassicSummonersRiftFallsBackToTheMap1BankFamily()
    {
        if (!Directory.Exists(LegacyShared)) return;
        var notes = new List<string>();
        Assert.Equal("Map1", LegacyAudioPorter.ResolveBankMapId("Map2", LegacyShared, notes));
        Assert.NotEmpty(notes);                                                    // and it says so
        Assert.Equal("Map1", LegacyAudioPorter.ResolveBankMapId("Map1", LegacyShared));
        Assert.Equal("Map12", LegacyAudioPorter.ResolveBankMapId("Map12", LegacyShared));
    }

    [Fact]
    public void TheRealLegacyLevelPortsIntoAReadableBank()
    {
        if (!Directory.Exists(LegacyLevel)) return;
        var set = LegacyAudioPorter.Read(LegacyLevel, "Map2", "map2");
        Assert.NotEmpty(set.Clips);
        Assert.All(set.Clips, c => Assert.True(c.Bytes > 0));
        Assert.Equal(set.Clips.Count, set.Clips.Select(c => c.WemId).Distinct().Count());
        Assert.Equal(set.Clips.Count, set.Clips.Select(c => c.EventName).Distinct().Count());

        var pair = LegacyAudioPorter.BuildBanks("ENV_LegacyPort_Map2_SFX", set);
        Assert.NotNull(pair);
        var reader = new AudioBankSet();
        reader.AddBank(BnkFile.Parse(pair!.EventsBank)!, 0, pair.EventsFileName);
        reader.AddBank(BnkFile.Parse(pair.AudioBank)!, 0, pair.AudioFileName);
        foreach (var c in set.Clips)
            Assert.Equal(c.Wem, reader.GetWemData(c.WemId));
    }

    [Fact]
    public void SkippingMediaTheGameAlreadyShipsLeavesTheRestIntact()
    {
        if (!Directory.Exists(LegacyLevel)) return;
        var all = LegacyAudioPorter.Read(LegacyLevel, "Map2", "map2");
        var drop = all.Clips.Take(3).Select(c => c.LegacyWemId).ToHashSet();
        var trimmed = LegacyAudioPorter.Read(LegacyLevel, "Map2", "map2", drop);

        Assert.Equal(all.Clips.Count - drop.Count, trimmed.Clips.Count);
        Assert.DoesNotContain(trimmed.Clips, c => drop.Contains(c.LegacyWemId));
        Assert.Contains(trimmed.Notes, n => n.Contains("already ships"));
    }

    [Fact]
    public void PortingIsDeterministic()
    {
        // The names go into the map's bin. If a re-port renumbered them, every placement the user made
        // would point at an event that no longer exists.
        if (!Directory.Exists(LegacyLevel)) return;
        var a = LegacyAudioPorter.Read(LegacyLevel, "Map2", "map2");
        var b = LegacyAudioPorter.Read(LegacyLevel, "Map2", "map2");
        Assert.Equal(a.Clips.Select(c => c.EventName), b.Clips.Select(c => c.EventName));
        Assert.Equal(a.Clips.Select(c => c.WemId), b.Clips.Select(c => c.WemId));
    }
}
