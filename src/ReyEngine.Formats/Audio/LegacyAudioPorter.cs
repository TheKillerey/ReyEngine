namespace ReyEngine.Formats.Audio;

/// <summary>One legacy sound, ready to be re-hosted in a modern bank.</summary>
public sealed record LegacyAudioClip(
    string Family, uint LegacyEventId, uint LegacyWemId, uint WemId, string EventName, byte[] Wem, WemInfo Info)
{
    public int Bytes => Wem.Length;
}

/// <summary>The legacy audio found for one level, plus notes on what was skipped and why.</summary>
public sealed record LegacyAudioSet(
    string BankMapId, IReadOnlyList<LegacyAudioClip> Clips, IReadOnlyList<string> Notes)
{
    public bool IsEmpty => Clips.Count == 0;
}

/// <summary>
/// M574: collect a legacy (2016-client) level's Wwise audio so it can be re-hosted for current League.
///
/// <para>The legacy client stores no positional sound data — unlike particles, which have Particles.dat,
/// there is nothing in LEVELS/&lt;map&gt; that names a bank, an event or an ambience. Its Constants.var does
/// carry an <c>aud_FMOD*</c> block, but that predates the Wwise switch and holds placeholders
/// ("AmbientEvent", "ReverbPreset") or another map's name — it is dead config, not placement.</para>
///
/// <para>So a port cannot recover WHERE sounds were; only WHAT they were. This class answers the second
/// half: the level's bank families, their events, and the media each resolves to. Placement is authored.</para>
/// </summary>
public static class LegacyAudioPorter
{
    /// <summary>Where a legacy client keeps its shared banks, relative to the client root.</summary>
    public const string SharedBankDirectory = @"DATA\Sounds\Wwise\SFX\Shared";

    /// <summary>The bank families worth porting. MUS is deliberately absent — see <see cref="Read"/>.</summary>
    private static readonly string[] Families = { "ENV", "MISC", "NPC" };

    /// <summary>
    /// Find the legacy shared-bank folder for a client root, or for any path inside one
    /// (the porter is handed a LEVELS\&lt;map&gt; directory, not the client root).
    /// </summary>
    public static string? FindSharedBankDirectory(string anyPathInsideClient)
    {
        var dir = new DirectoryInfo(anyPathInsideClient);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, SharedBankDirectory);
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Which <c>*_Map&lt;N&gt;_SFX</c> family serves a level folder.
    ///
    /// <para>Usually the level's own number. Classic Summoner's Rift is the exception: LEVELS\Map2 has no
    /// ENV_Map2 bank, and its audio lives under Map1. That is not a guess — current League's Map453 (the
    /// classic-SR map) declares a <c>MUS_Map1</c> bank unit with <c>mus_map01_*</c> events, and 53 of the 63
    /// media ids reachable from the legacy NPC_Map1 bank appear verbatim in Map453's shipped mode_jade bank.
    /// Riot treats Map1 as classic SR, so Map2 borrows it.</para>
    /// </summary>
    public static string ResolveBankMapId(string levelFolderName, string sharedBankDirectory, IList<string>? notes = null)
    {
        var m = System.Text.RegularExpressions.Regex.Match(levelFolderName, @"map\s*(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        string own = m.Success ? "Map" + m.Groups[1].Value : "Map1";
        if (File.Exists(Path.Combine(sharedBankDirectory, $"ENV_{own}_SFX_audio.bnk"))) return own;

        notes?.Add($"No ENV_{own} bank in the legacy client; using Map1 (Riot's own classic-Summoner's-Rift "
                   + "bank id, as Map453 shows by shipping MUS_Map1).");
        return "Map1";
    }

    /// <summary>
    /// Read every clip the level's banks can play.
    /// </summary>
    /// <param name="anyPathInsideClient">A path inside the legacy client (its LEVELS\&lt;map&gt; folder will do).</param>
    /// <param name="levelFolderName">The level folder's name, e.g. "Map2".</param>
    /// <param name="targetSlug">Slug used to namespace generated ids and event names.</param>
    /// <param name="mediaAlreadyInGame">
    /// Media ids current League still ships. Those clips are skipped: Riot re-encoded much of this audio
    /// into modern banks keeping the original ids, so re-hosting them would ship a second copy of a sound
    /// the client already has.
    /// </param>
    /// <remarks>
    /// MUS is not ported. Current League still ships MUS_Map1 (audio.wpk + events.bnk) at version 145, so
    /// the classic music needs no conversion — it only needs declaring.
    /// </remarks>
    public static LegacyAudioSet Read(
        string anyPathInsideClient, string levelFolderName, string targetSlug,
        IReadOnlySet<uint>? mediaAlreadyInGame = null)
    {
        var notes = new List<string>();
        string? shared = FindSharedBankDirectory(anyPathInsideClient);
        if (shared is null)
        {
            notes.Add($@"No {SharedBankDirectory} folder above ""{anyPathInsideClient}"" — no audio to port.");
            return new LegacyAudioSet("", Array.Empty<LegacyAudioClip>(), notes);
        }

        string mapId = ResolveBankMapId(levelFolderName, shared, notes);
        var clips = new List<LegacyAudioClip>();
        int skippedPresent = 0;

        foreach (string family in Families)
        {
            var set = new AudioBankSet();
            bool any = false;
            foreach (string file in new[]
            {
                $"{family}_{mapId}_SFX_events.bnk",
                $"{family}_{mapId}_SFX_audio.bnk",
                // The map bank's Sound objects name their source bank, and for ENV/NPC that is regularly the
                // Global one (its STID lists ENV_Global_SFX_audio). Without it half the media is unreachable.
                $"{family}_Global_SFX_audio.bnk",
            })
            {
                string path = Path.Combine(shared, file);
                if (!File.Exists(path)) continue;
                if (BnkFile.Parse(File.ReadAllBytes(path)) is not { } bnk) { notes.Add($"{file}: unreadable."); continue; }
                set.AddBank(bnk, 0, file);
                any = true;
            }
            string pack = Path.Combine(shared, $"{family}_{mapId}_SFX_audio.wpk");
            if (File.Exists(pack) && WpkFile.Parse(File.ReadAllBytes(pack)) is { } wpk)
            { set.AddPack(wpk, 0, Path.GetFileName(pack)); any = true; }
            if (!any) continue;

            // Ordered by media id so re-running the port produces the same names, and the same map, every time.
            var found = new SortedDictionary<uint, uint>();   // wem id -> the event it came from
            foreach (uint ev in set.AllEventIds())
                foreach (uint wem in set.ResolveEvent(ev))
                    found.TryAdd(wem, ev);

            int index = 0;
            foreach (var (legacyWem, legacyEvent) in found)
            {
                byte[]? data = set.GetWemData(legacyWem);
                if (data is null || data.Length == 0) continue;
                if (mediaAlreadyInGame?.Contains(legacyWem) == true) { skippedPresent++; continue; }

                index++;
                string eventName = $"Play_sfx_Env_LegacyPort_{targetSlug}_{family}_{index:00}";
                // A fresh media id, namespaced by target and source id. The originals are real Wwise ids and
                // some of them are STILL IN USE by current League (Riot kept them when re-encoding), so
                // reusing one would put two different files under a single id.
                uint wemId = WwiseHash.Fnv1($"reyengine/{targetSlug}/{family}/{legacyWem}");
                clips.Add(new LegacyAudioClip(family, legacyEvent, legacyWem, wemId, eventName, data, WemHeader.Read(data)));
            }
        }

        if (skippedPresent > 0)
            notes.Add($"{skippedPresent} clip(s) skipped: current League already ships that media.");
        if (clips.Count > 0)
        {
            var odd = clips.Where(c => c.Info.Codec != "Vorbis").ToList();
            if (odd.Count > 0)
                notes.Add($"{odd.Count} clip(s) are not Wwise Vorbis ({string.Join(", ", odd.Select(o => o.Info.Codec).Distinct())}) "
                          + "— kept, but they are the ones to check first if something does not play.");
        }
        notes.Add("Music is not ported: current League still ships MUS_Map1 at bank version 145.");
        return new LegacyAudioSet(mapId, clips, notes);
    }

    /// <summary>
    /// Build the modern bank pair for a set of clips. <paramref name="bankName"/> becomes the file stem, so
    /// it must match the paths written into the map's MapAudioDataProperties.
    /// </summary>
    public static WwiseBankPair? BuildBanks(string bankName, LegacyAudioSet set)
        => WwiseBankWriter.Build(bankName,
            set.Clips.Select(c => new WwiseBankSound(c.EventName, c.WemId, c.Wem)).ToList());
}
