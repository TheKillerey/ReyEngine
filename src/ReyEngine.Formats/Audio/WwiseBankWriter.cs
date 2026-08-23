using System.Text;

namespace ReyEngine.Formats.Audio;

/// <summary>One sound to put in a generated bank: an event name, the media id it should use, and the wem bytes.</summary>
public sealed record WwiseBankSound(string EventName, uint WemId, byte[] Wem);

/// <summary>A generated bank pair, in Riot's own "&lt;name&gt;_events.bnk / &lt;name&gt;_audio.bnk" shape.</summary>
public sealed record WwiseBankPair(
    string Name, string EventsFileName, string AudioFileName,
    byte[] EventsBank, byte[] AudioBank, IReadOnlyList<string> EventNames);

/// <summary>
/// M574: write a Wwise SoundBank pair the CURRENT League client can load.
///
/// <para>The legacy (2016) client's banks are BKHD <b>88</b>; current League is <b>145</b> (some 134), and
/// Wwise refuses a bank whose generator version does not match its reader. Legacy audio therefore cannot be
/// copied across — the media has to be re-hosted in a bank written at the current version.</para>
///
/// <para>The media itself needs no conversion: legacy ENV_Map1 is Wwise Vorbis 44.1kHz, the same codec
/// family current League uses, and all 16 reachable wems decode with vgmstream unchanged (measured M574).
/// So this writes a NEW v145 bank around the ORIGINAL wem bytes.</para>
///
/// <para>Every structural constant below was measured on shipped banks rather than assumed; the source
/// object is named at each one. The HIRC shape is the minimum that plays a sound:
/// Event → Action(Play) → Sound → ActorMixer → output bus.</para>
/// </summary>
public static class WwiseBankWriter
{
    /// <summary>Bank generator version current League reads. Live/PBE ship 145 with some older 134.</summary>
    public const uint Version = 145;

    /// <summary>
    /// Language id every shipped SFX bank carries (they are language-neutral; VO banks differ).
    /// Observed identical across all 529 banks in Map453.wad.client.
    /// </summary>
    private const uint SfxLanguageId = 0x17705D3E;

    /// <summary>BKHD's uAlignment/bDeviceAllocated word and Riot's project id, copied from shipped banks.</summary>
    private const uint HeaderAlignmentWord = 0x00000010;
    private const uint ProjectId = 250;

    /// <summary>
    /// The output bus Map453's own ambience routes through — reached from
    /// <c>Play_sfx_Env_Map453_Ambience_base</c> → Sound 0x135e6e5f → ActorMixer 0x3d812b61 → this id, which
    /// no map bank defines, so it comes from the always-loaded Init.bnk. Using it means ported ambience is
    /// mixed exactly like the map's own.
    /// </summary>
    public const uint AmbienceBusId = 0x1961115D;

    /// <summary>Riot 16-byte aligns embedded media inside DATA (verified: every shipped DIDX offset % 16 == 0).</summary>
    private const int MediaAlignment = 16;

    // ---- object templates, captured verbatim from shipped v145 banks ------------------------------
    // Only the fields named in the patch helpers below are changed; every other byte is Riot's, which is
    // what keeps the positioning/attenuation/bus behaviour of a real ambience sound.

    /// <summary>Sound 0x135e6e5f of mode_jade_sfx_events.bnk — the Map453 ambience bed.
    /// [0..3] plugin, [4] streamType, [5..8] wem id, [9..12] media size, [13] source bits, [23..26] parent.</summary>
    private const string SoundTemplateHex =
        "0100040000E9412E120625100000000000000000000000612B813D0002063A000080C100000000000000000000000C010100000000000000";

    /// <summary>ActorMixer 0x3d812b61 of the same bank. [5..8] output bus, [9..12] parent (0 = root),
    /// and the tail is [u32 childCount][u32 children].</summary>
    private const string MixerTemplateHex =
        "00000000005D116119000000000001070000BE42000308000000000004010000000800000000010000005F6E5E13";

    /// <summary>Play action 0x26dba663 of items_sightward_skin260_events.bnk.
    /// [0..1] action type 0x0403 (play, game-object scope), [2..5] target, [10..13] the bank that owns it.</summary>
    private const string ActionTemplateHex = "03047D3C832E0000000451DF7F6100000000";

    private const int SoundWemIdOffset = 5;
    private const int SoundMediaSizeOffset = 9;
    private const int SoundParentOffset = 23;
    private const int MixerBusOffset = 5;
    private const int MixerChildTailBytes = 8;   // the template's [u32 count][u32 child]
    private const int ActionTargetOffset = 2;
    private const int ActionBankOffset = 10;

    /// <summary>HIRC object types. Public because they are format facts, not an implementation detail.</summary>
    public const byte HircSound = 2, HircAction = 3, HircEvent = 4, HircActorMixer = 7;

    /// <summary>
    /// Build the bank pair for <paramref name="bankName"/> (e.g. "ENV_LegacyPort_Map2_SFX"), hosting one
    /// event per sound. Returns null when there is nothing to write.
    /// </summary>
    /// <remarks>
    /// One event → one action → one sound → one wem. Wwise plays every action of an event at once, so
    /// grouping variations under a single event would layer them rather than pick one; a random pick needs
    /// a RandomOrSequenceContainer, which is not written here.
    /// </remarks>
    public static WwiseBankPair? Build(string bankName, IReadOnlyList<WwiseBankSound> sounds)
    {
        if (string.IsNullOrWhiteSpace(bankName) || sounds is null || sounds.Count == 0) return null;

        var kept = new List<WwiseBankSound>();
        var seenWem = new HashSet<uint>();
        var seenEvent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sounds)
        {
            if (s.Wem is not { Length: > 0 } || string.IsNullOrWhiteSpace(s.EventName)) continue;
            // A duplicate media id would put two different files under one id; a duplicate event name would
            // produce two HIRC objects with the same hash. Both are silent corruption, so drop the repeat.
            if (!seenWem.Add(s.WemId) || !seenEvent.Add(s.EventName)) continue;
            kept.Add(s);
        }
        if (kept.Count == 0) return null;

        string eventsStem = bankName + "_events";
        string audioStem = bankName + "_audio";
        uint eventsBankId = WwiseHash.Fnv1(eventsStem);
        uint audioBankId = WwiseHash.Fnv1(audioStem);
        uint mixerId = WwiseHash.Fnv1(bankName + "/actormixer");

        return new WwiseBankPair(
            bankName, eventsStem + ".bnk", audioStem + ".bnk",
            BuildEventsBank(eventsBankId, mixerId, kept),
            BuildAudioBank(audioBankId, kept),
            kept.Select(k => k.EventName).ToList());
    }

    // ---- sections ---------------------------------------------------------------------------------

    private static void WriteSection(BinaryWriter w, string signature, byte[] body)
    {
        w.Write(Encoding.ASCII.GetBytes(signature));
        w.Write((uint)body.Length);
        w.Write(body);
    }

    /// <summary>
    /// BKHD. The 16 trailing bytes shipped banks carry are left zero: v134 banks ship a 28/32-byte BKHD with
    /// that area absent or zeroed and the client loads them, so it is not validated content.
    /// </summary>
    private static byte[] Header(uint bankId)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Version);
        w.Write(bankId);
        w.Write(SfxLanguageId);
        w.Write(HeaderAlignmentWord);
        w.Write(ProjectId);
        w.Write(0u);
        w.Write(new byte[16]);
        return ms.ToArray();
    }

    private static byte[] BuildAudioBank(uint bankId, IReadOnlyList<WwiseBankSound> sounds)
    {
        var offsets = new int[sounds.Count];
        int cursor = 0;
        for (int i = 0; i < sounds.Count; i++)
        {
            offsets[i] = cursor;
            cursor += sounds[i].Wem.Length;
            if (i + 1 < sounds.Count && cursor % MediaAlignment != 0)
                cursor += MediaAlignment - cursor % MediaAlignment;
        }

        using var didx = new MemoryStream();
        using (var dw = new BinaryWriter(didx, Encoding.ASCII, leaveOpen: true))
            for (int i = 0; i < sounds.Count; i++)
            { dw.Write(sounds[i].WemId); dw.Write(offsets[i]); dw.Write(sounds[i].Wem.Length); }

        var data = new byte[cursor];
        for (int i = 0; i < sounds.Count; i++) sounds[i].Wem.CopyTo(data, offsets[i]);

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        WriteSection(w, "BKHD", Header(bankId));
        WriteSection(w, "DIDX", didx.ToArray());
        WriteSection(w, "DATA", data);
        return ms.ToArray();
    }

    private static byte[] BuildEventsBank(uint bankId, uint mixerId, IReadOnlyList<WwiseBankSound> sounds)
    {
        var soundIds = new uint[sounds.Count];
        var actionIds = new uint[sounds.Count];
        for (int i = 0; i < sounds.Count; i++)
        {
            soundIds[i] = WwiseHash.Fnv1($"reyengine/sound/{sounds[i].EventName}");
            actionIds[i] = WwiseHash.Fnv1($"reyengine/action/{sounds[i].EventName}");
        }

        using var hirc = new MemoryStream();
        using (var h = new BinaryWriter(hirc, Encoding.ASCII, leaveOpen: true))
        {
            h.Write((uint)(1 + sounds.Count * 3));   // mixer + one sound/action/event per entry
            WriteObject(h, HircActorMixer, mixerId, Mixer(soundIds));
            for (int i = 0; i < sounds.Count; i++)
                WriteObject(h, HircSound, soundIds[i], Sound(sounds[i].WemId, sounds[i].Wem.Length, mixerId));
            for (int i = 0; i < sounds.Count; i++)
                WriteObject(h, HircAction, actionIds[i], Action(soundIds[i], bankId));
            for (int i = 0; i < sounds.Count; i++)
                WriteObject(h, HircEvent, WwiseHash.Fnv1(sounds[i].EventName), Event(actionIds[i]));
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        WriteSection(w, "BKHD", Header(bankId));
        WriteSection(w, "HIRC", hirc.ToArray());
        return ms.ToArray();
    }

    /// <summary>A HIRC record: type, size (the id counts toward it), id, payload.</summary>
    internal static void WriteObject(BinaryWriter w, byte type, uint id, byte[] payload)
    {
        w.Write(type);
        w.Write((uint)(payload.Length + 4));
        w.Write(id);
        w.Write(payload);
    }

    internal static byte[] Sound(uint wemId, int mediaSize, uint parentId)
    {
        var p = Convert.FromHexString(SoundTemplateHex);
        p[4] = 0;                                                          // in-memory, not streamed
        BitConverter.GetBytes(wemId).CopyTo(p, SoundWemIdOffset);
        BitConverter.GetBytes(mediaSize).CopyTo(p, SoundMediaSizeOffset);
        BitConverter.GetBytes(parentId).CopyTo(p, SoundParentOffset);
        return p;
    }

    internal static byte[] Mixer(IReadOnlyList<uint> childIds)
    {
        var t = Convert.FromHexString(MixerTemplateHex);
        var p = new byte[t.Length - MixerChildTailBytes + 4 + childIds.Count * 4];
        t.AsSpan(0, t.Length - MixerChildTailBytes).CopyTo(p);
        BitConverter.GetBytes(AmbienceBusId).CopyTo(p, MixerBusOffset);
        BitConverter.GetBytes(0u).CopyTo(p, 9);                            // root of its own hierarchy
        int tail = t.Length - MixerChildTailBytes;
        BitConverter.GetBytes((uint)childIds.Count).CopyTo(p, tail);
        for (int i = 0; i < childIds.Count; i++) BitConverter.GetBytes(childIds[i]).CopyTo(p, tail + 4 + i * 4);
        return p;
    }

    internal static byte[] Action(uint targetId, uint bankId)
    {
        var p = Convert.FromHexString(ActionTemplateHex);
        BitConverter.GetBytes(targetId).CopyTo(p, ActionTargetOffset);
        BitConverter.GetBytes(bankId).CopyTo(p, ActionBankOffset);
        return p;
    }

    /// <summary>An event with a single action. At v145 the action count is a u8 (it is a u32 at v88).</summary>
    internal static byte[] Event(uint actionId)
    {
        var p = new byte[5];
        p[0] = 1;
        BitConverter.GetBytes(actionId).CopyTo(p, 1);
        return p;
    }
}
