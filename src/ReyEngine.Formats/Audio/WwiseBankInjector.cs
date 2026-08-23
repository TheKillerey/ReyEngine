using System.Text;

namespace ReyEngine.Formats.Audio;

/// <summary>The rewritten pair plus what actually went in.</summary>
public sealed record WwiseInjectionResult(
    byte[] EventsBank, byte[] AudioBank, IReadOnlyList<string> AddedEvents, int SkippedExisting);

/// <summary>
/// M575: add sounds to a bank pair the map ALREADY loads, instead of shipping a new one.
///
/// <para>A map only plays banks its <c>MapAudioDataProperties.bankUnits</c> names, and that lives in
/// <c>data/maps/shipping/map&lt;N&gt;/map&lt;N&gt;.bin</c> — a file the mod would then have to carry and keep in
/// step with every patch. Extending a bank the map already declares avoids that entirely: the two audio
/// files are the only things the mod overrides, and audio banks change far less often than map bins.</para>
///
/// <para>Structurally this is the same objects <see cref="WwiseBankWriter"/> writes, appended to an
/// existing HIRC with its object count bumped. Everything already in either file is preserved byte for
/// byte — media through <see cref="AudioBankDocument"/> (M137, proven in game), sections by copy.</para>
/// </summary>
public static class WwiseBankInjector
{
    /// <summary>
    /// Append <paramref name="sounds"/> to the pair. <paramref name="mixerSeed"/> names the ActorMixer that
    /// will parent them, so re-running with the same seed re-uses the same id rather than growing a new
    /// mixer each time. Returns null with a reason on failure.
    /// </summary>
    public static WwiseInjectionResult? Append(
        byte[] eventsBank, byte[] audioBank, string mixerSeed,
        IReadOnlyList<WwiseBankSound> sounds, out string? error)
    {
        error = null;
        if (eventsBank is null || audioBank is null) { error = "A bank pair is required."; return null; }
        if (sounds is null || sounds.Count == 0) { error = "Nothing to add."; return null; }

        var events = BnkFile.Parse(eventsBank);
        var audio = BnkFile.Parse(audioBank);
        if (events is null) { error = "The events bank could not be read."; return null; }
        if (audio is null) { error = "The audio bank could not be read."; return null; }
        // The object layouts here were measured at 145 only. Refusing anything else is the difference
        // between "this did not work" and a bank that loads and behaves unpredictably.
        if (events.Version != WwiseBankWriter.Version || audio.Version != WwiseBankWriter.Version)
        {
            error = $"Only bank version {WwiseBankWriter.Version} can be extended "
                  + $"(events is {events.Version}, audio is {audio.Version}).";
            return null;
        }

        var media = AudioBankDocument.Parse(audioBank, "audio.bnk");
        if (media is null) { error = "The audio bank holds no media table."; return null; }
        if (!media.IsEditable) { error = media.ReadOnlyReason ?? "The audio bank is not safely editable."; return null; }

        uint mixerId = WwiseHash.Fnv1(mixerSeed + "/actormixer");
        var takenObjects = ObjectIds(eventsBank);
        var takenMedia = audio.Wems.Keys.ToHashSet();

        var added = new List<(WwiseBankSound Sound, uint SoundId, uint ActionId, uint EventId)>();
        int skipped = 0;
        foreach (var s in sounds)
        {
            if (s.Wem is not { Length: > 0 } || string.IsNullOrWhiteSpace(s.EventName)) { skipped++; continue; }
            uint eventId = WwiseHash.Fnv1(s.EventName);
            uint soundId = WwiseHash.Fnv1($"reyengine/sound/{s.EventName}");
            uint actionId = WwiseHash.Fnv1($"reyengine/action/{s.EventName}");
            // Re-running the port must not stack a second copy of everything, and it must never collide
            // with an object or a media id the bank already had.
            if (!takenMedia.Add(s.WemId)) { skipped++; continue; }
            if (!takenObjects.Add(eventId) || !takenObjects.Add(soundId) || !takenObjects.Add(actionId))
            { skipped++; continue; }
            if (!media.Add(s.WemId, s.Wem, out error)) return null;
            added.Add((s, soundId, actionId, eventId));
        }
        if (added.Count == 0) { error = "Every sound was already in the bank."; return null; }

        byte[] newAudio = media.Serialize();
        if (!media.Validate(newAudio, out error)) return null;

        // one mixer for the whole batch, unless a previous run already left ours in place
        bool mixerExists = takenObjects.Contains(mixerId);
        using var records = new MemoryStream();
        using (var w = new BinaryWriter(records, Encoding.ASCII, leaveOpen: true))
        {
            if (!mixerExists)
                WwiseBankWriter.WriteObject(w, WwiseBankWriter.HircActorMixer, mixerId,
                    WwiseBankWriter.Mixer(added.Select(a => a.SoundId).ToList()));
            foreach (var a in added)
                WwiseBankWriter.WriteObject(w, WwiseBankWriter.HircSound, a.SoundId,
                    WwiseBankWriter.Sound(a.Sound.WemId, a.Sound.Wem.Length, mixerId));
            foreach (var a in added)
                WwiseBankWriter.WriteObject(w, WwiseBankWriter.HircAction, a.ActionId,
                    WwiseBankWriter.Action(a.SoundId, events.BankId));
            foreach (var a in added)
                WwiseBankWriter.WriteObject(w, WwiseBankWriter.HircEvent, a.EventId,
                    WwiseBankWriter.Event(a.ActionId));
        }
        int newObjects = added.Count * 3 + (mixerExists ? 0 : 1);

        byte[]? newEvents = AppendToHirc(eventsBank, records.ToArray(), newObjects,
            mixerExists ? mixerId : 0, added.Select(a => a.SoundId).ToList(), out error);
        if (newEvents is null) return null;

        return new WwiseInjectionResult(newEvents, newAudio, added.Select(a => a.Sound.EventName).ToList(), skipped);
    }

    /// <summary>Every HIRC object id in a bank.</summary>
    private static HashSet<uint> ObjectIds(byte[] bank)
    {
        var ids = new HashSet<uint>();
        WalkHirc(bank, (type, id, _, _) => ids.Add(id));
        return ids;
    }

    /// <summary>
    /// Rewrite the bank with <paramref name="records"/> appended to its HIRC.
    ///
    /// <para>When our mixer is already there from an earlier run, its child list is extended in place
    /// instead — an ActorMixer that does not list a child leaves that child orphaned and silent.</para>
    /// </summary>
    private static byte[]? AppendToHirc(
        byte[] bank, byte[] records, int newObjectCount,
        uint existingMixerId, IReadOnlyList<uint> newChildren, out string? error)
    {
        error = null;
        using var outMs = new MemoryStream();
        using var w = new BinaryWriter(outMs);
        bool wrote = false;

        using (var ms = new MemoryStream(bank, false))
        using (var r = new BinaryReader(ms))
            while (ms.Position + 8 <= ms.Length)
            {
                long secStart = ms.Position;
                string sig = Encoding.ASCII.GetString(r.ReadBytes(4));
                uint size = r.ReadUInt32();
                long end = ms.Position + size;
                if (end > ms.Length) { error = $"Section '{sig}' runs past the end of the bank."; return null; }

                if (sig != "HIRC")
                {
                    ms.Position = secStart;
                    w.Write(r.ReadBytes((int)(end - secStart)));
                    ms.Position = end;
                    continue;
                }

                uint count = r.ReadUInt32();
                byte[] body = bank.AsSpan((int)ms.Position, (int)(end - ms.Position)).ToArray();
                if (existingMixerId != 0)
                {
                    body = ExtendMixerChildren(body, existingMixerId, newChildren, out error);
                    if (body is null) return null;
                }
                w.Write(Encoding.ASCII.GetBytes("HIRC"));
                w.Write((uint)(4 + body.Length + records.Length));
                w.Write(count + (uint)newObjectCount);
                w.Write(body);
                w.Write(records);
                wrote = true;
                ms.Position = end;
            }

        if (!wrote) { error = "The events bank has no HIRC section to extend."; return null; }
        return outMs.ToArray();
    }

    /// <summary>Rewrite one ActorMixer's child list, growing its record. Null with a reason if not found.</summary>
    private static byte[]? ExtendMixerChildren(
        byte[] hircBody, uint mixerId, IReadOnlyList<uint> extraChildren, out string? error)
    {
        error = null;
        using var outMs = new MemoryStream();
        using var w = new BinaryWriter(outMs);
        bool found = false;
        int pos = 0;
        while (pos + 9 <= hircBody.Length)
        {
            byte type = hircBody[pos];
            uint size = BitConverter.ToUInt32(hircBody, pos + 1);
            uint id = BitConverter.ToUInt32(hircBody, pos + 5);
            int payloadStart = pos + 9, payloadLength = (int)size - 4;
            if (payloadLength < 0 || payloadStart + payloadLength > hircBody.Length)
            { error = "The events bank's HIRC is malformed."; return null; }

            if (type == WwiseBankWriter.HircActorMixer && id == mixerId)
            {
                var payload = hircBody.AsSpan(payloadStart, payloadLength).ToArray();
                // The child list is the record's tail: [u32 count][u32 ids]. Read the count by trying the
                // only position it can be at for this record's length.
                uint existing = 0;
                int listStart = -1;
                for (int candidate = payloadLength - 4; candidate >= 0; candidate -= 4)
                {
                    uint c = BitConverter.ToUInt32(payload, candidate);
                    if (candidate + 4 + c * 4 == payloadLength) { existing = c; listStart = candidate; break; }
                }
                if (listStart < 0) { error = "The mixer's child list could not be located."; return null; }

                var grown = new byte[listStart + 4 + (int)(existing + extraChildren.Count) * 4];
                payload.AsSpan(0, listStart + 4 + (int)existing * 4).CopyTo(grown);
                BitConverter.GetBytes(existing + (uint)extraChildren.Count).CopyTo(grown, listStart);
                for (int i = 0; i < extraChildren.Count; i++)
                    BitConverter.GetBytes(extraChildren[i]).CopyTo(grown, listStart + 4 + (int)(existing + i) * 4);

                w.Write(type);
                w.Write((uint)(grown.Length + 4));
                w.Write(id);
                w.Write(grown);
                found = true;
            }
            else
            {
                w.Write(hircBody.AsSpan(pos, 9 + payloadLength));
            }
            pos = payloadStart + payloadLength;
        }
        if (!found) { error = $"ActorMixer 0x{mixerId:x8} is not in this bank after all."; return null; }
        return outMs.ToArray();
    }

    /// <summary>Walk a bank's HIRC records.</summary>
    private static void WalkHirc(byte[] bank, Action<byte, uint, int, int> visit)
    {
        using var ms = new MemoryStream(bank, false);
        using var r = new BinaryReader(ms);
        while (ms.Position + 8 <= ms.Length)
        {
            string sig = Encoding.ASCII.GetString(r.ReadBytes(4));
            uint size = r.ReadUInt32();
            long end = ms.Position + size;
            if (end > ms.Length) break;
            if (sig == "HIRC")
            {
                uint count = r.ReadUInt32();
                for (uint i = 0; i < count && ms.Position < end; i++)
                {
                    byte type = r.ReadByte(); uint osize = r.ReadUInt32(); uint id = r.ReadUInt32();
                    int start = (int)ms.Position, length = (int)osize - 4;
                    if (length < 0 || start + length > bank.Length) break;
                    visit(type, id, start, length);
                    ms.Position = start + length;
                }
            }
            ms.Position = end;
        }
    }
}
