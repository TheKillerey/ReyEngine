using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Skeletons;

/// <summary>One named clip from a champion's animation-graph bin (M85): the .anm it plays plus the
/// submeshes it shows/hides while playing (SubmeshVisibilityEventData — hashes or literal names).</summary>
/// <param name="ShowNames">M724: the clip-wide UNION of every visibility event's show list, kept because
/// callers predating the timeline read it. New code should use <paramref name="VisibilityEvents"/>, which
/// carries each event's own frame window.</param>
/// <param name="VisibilityEvents">M724: each SubmeshVisibilityEventData with the window it applies over,
/// in bin order. A clip that reveals a sword at frame 12 has two events, not one union.</param>
/// <param name="TickDuration">M724: the clip's authored <c>mTickDuration</c> - seconds per frame for THIS
/// clip, which outranks the .anm header's own rate when converting an event frame to a time. 0 when the
/// clip does not author one, in which case the .anm's fps is correct.</param>
public sealed record AnimClipInfo(string Name, string AnmPath,
    IReadOnlyList<string> ShowNames, IReadOnlyList<string> HideNames,
    IReadOnlyList<uint> ShowHashes, IReadOnlyList<uint> HideHashes,
    IReadOnlyList<AnimParticleEvent>? ParticleEvents = null,
    IReadOnlyList<AnimSoundEvent>? SoundEvents = null,
    IReadOnlyList<AnimVisibilityEvent>? VisibilityEvents = null,
    float TickDuration = 0f);

/// <summary>M90: one SoundEventData in a clip — a Wwise event name like Play_sfx_Aatrox_Death3D_cast.</summary>
public sealed record AnimSoundEvent(string SoundName, float StartFrame, bool IsLoop);

/// <summary>
/// M724: one SubmeshVisibilityEventData with the frames it covers.
///
/// <para>Riot authors these as a running edit, not as a per-frame picture: an event names the submeshes it
/// turns ON and the ones it turns OFF at its start frame, and that stands until another event says
/// otherwise. <see cref="EndFrame"/> is -1 when the event authors none, meaning "to the end of the clip".</para>
/// </summary>
public sealed record AnimVisibilityEvent(float StartFrame, float EndFrame,
    IReadOnlyList<string> ShowNames, IReadOnlyList<string> HideNames,
    IReadOnlyList<uint> ShowHashes, IReadOnlyList<uint> HideHashes);

/// <summary>M724: one entry of a ParticleEventData's <c>mParticleEventDataPairList</c> - the engine spawns
/// one effect per pair, not one per event. <paramref name="TargetBoneName"/> aims a beam's far end.</summary>
public sealed record AnimParticleSpawn(string BoneName, uint BoneHash,
    string TargetBoneName = "", uint TargetBoneHash = 0);

/// <summary>M86: one ParticleEventData in a clip — the VFX it spawns and the bone it rides.
/// BoneName is empty when the bin only stores a hash the hashtable can't resolve; BoneHash then
/// carries it, matched against skeleton joints by FNV1a or Elf of the joint name.</summary>
/// <param name="EndFrame">M724: <c>mEndFrame</c>, or -1 when unauthored. An effect with an end stops there
/// instead of emitting until the clip wraps.</param>
/// <param name="IsLoop">M724: <c>mIsLoop</c> - the effect restarts for as long as the event is live.</param>
/// <param name="IsKill">
/// M724: <c>mIsKillEvent</c> - this event STOPS a system rather than starting one.
///
/// <para><b>Absent means FALSE here, deliberately, against the schema.</b> The meta schema declares this
/// Bool with <c>default: true</c> (meta.db.json, <c>0x72a03ff8</c>), so a literal reading makes every event
/// that omits it a kill event. Measured over five champion wads - Aatrox, Riven, Viego, Jhin, Thresh -
/// ParticleEventData carries it <b>absent 3,746 times, explicitly false 726 times, and true ZERO times</b>.
/// Honouring the schema default would therefore suppress 3,746 of 4,472 champion particle events, i.e. most
/// of every champion's VFX. Riot writes the field explicitly when it means false and never writes true, so
/// the field is effectively inert on shipped data; we read it for mod bins that do author it. Do not
/// "correct" this to the schema default without re-running that census.</para>
/// </param>
/// <param name="Spawns">M724: every pair the event authors, in order. <paramref name="BoneName"/> /
/// <paramref name="BoneHash"/> mirror the first for callers written before the list existed.</param>
public sealed record AnimParticleEvent(string EffectName, uint EffectHash, string BoneName, float StartFrame,
    uint BoneHash = 0,
    float EndFrame = -1f,
    bool IsLoop = false,
    bool IsKill = false,
    IReadOnlyList<AnimParticleSpawn>? Spawns = null);

/// <summary>
/// M85: parses champion animation bins (data/characters/X/animations/skinN.bin) for named clips +
/// per-clip submesh visibility, and skin bins for initialSubmeshToHide. Never throws — empty on failure.
/// </summary>
public static class ChampionAnimationData
{
    private static readonly uint ClassAtomic = HashAlgorithms.Fnv1a("atomicClipData");
    private static readonly uint FClipDataMap = HashAlgorithms.Fnv1a("mClipDataMap");
    private static readonly uint FAnimRes = HashAlgorithms.Fnv1a("mAnimationResourceData");
    private static readonly uint FAnimPath = HashAlgorithms.Fnv1a("mAnimationFilePath");
    private static readonly uint FEventMap = HashAlgorithms.Fnv1a("mEventDataMap");
    private static readonly uint ClassSubmeshVis = HashAlgorithms.Fnv1a("SubmeshVisibilityEventData");
    private static readonly uint FShow = HashAlgorithms.Fnv1a("mShowSubmeshList");
    private static readonly uint FHide = HashAlgorithms.Fnv1a("mHideSubmeshList");
    private static readonly uint ClassParticleEvent = HashAlgorithms.Fnv1a("ParticleEventData");
    private static readonly uint ClassSoundEvent = HashAlgorithms.Fnv1a("SoundEventData");
    private static readonly uint FSoundName = HashAlgorithms.Fnv1a("mSoundName");
    private static readonly uint FIsLoop = HashAlgorithms.Fnv1a("mIsLoop");
    private static readonly uint FEffectKey = HashAlgorithms.Fnv1a("mEffectKey");
    /// <summary>M724: the name ParticleEventData actually declares. <c>mParticleName</c> below is kept as a
    /// second fallback but no animation class declares it, so it has never once matched.</summary>
    private static readonly uint FEffectName = HashAlgorithms.Fnv1a("mEffectName");
    private static readonly uint FParticleName = HashAlgorithms.Fnv1a("mParticleName");
    private static readonly uint FBoneName = HashAlgorithms.Fnv1a("mBoneName");
    private static readonly uint FTargetBoneName = HashAlgorithms.Fnv1a("mTargetBoneName");
    private static readonly uint FPairList = HashAlgorithms.Fnv1a("mParticleEventDataPairList");
    private static readonly uint FStartFrame = HashAlgorithms.Fnv1a("mStartFrame");
    private static readonly uint FEndFrame = HashAlgorithms.Fnv1a("mEndFrame");
    private static readonly uint FIsKillEvent = HashAlgorithms.Fnv1a("mIsKillEvent");
    private static readonly uint FTickDuration = HashAlgorithms.Fnv1a("mTickDuration");
    private static readonly uint FInitialHide = HashAlgorithms.Fnv1a("initialSubmeshToHide");
    private static readonly uint FSkinAudio = HashAlgorithms.Fnv1a("skinAudioProperties");
    private static readonly uint FBankUnits = HashAlgorithms.Fnv1a("bankUnits");
    private static readonly uint FBankEvents = HashAlgorithms.Fnv1a("events");

    /// <summary>All named clips in an animation bin. Clip names resolve via the bin-name resolver
    /// (mClipDataMap keys are FNV1a hashes of names like Idle1/Attack1).</summary>
    /// <param name="resolveWadPath">M590: 64-bit wad-path lookup. Patch 16.17 turned
    /// <c>mAnimationFilePath</c> into a WadChunkLink - measured 8,983 links and ZERO strings across
    /// a map wad and three champion wads - so without this every clip's .anm path reads as empty and
    /// the whole clip list comes back blank.</param>
    public static IReadOnlyList<AnimClipInfo> ParseClips(byte[] animationBin, Func<uint, string?> resolve,
        Func<ulong, string?>? resolveWadPath = null)
    {
        var clips = new List<AnimClipInfo>();
        try
        {
            var bin = SafeBinTree.Parse(animationBin);
            foreach (var o in bin.Objects.Values)
            {
                if (!o.Properties.TryGetValue(FClipDataMap, out var mapProp)
                    || mapProp is not System.Collections.IEnumerable mapEntries) continue;
                foreach (var kv in mapEntries)
                {
                    // map entries are KeyValuePair<BinTreeProperty, BinTreeProperty> — access via reflection
                    // (same pattern as MapParticleExtractor; the concrete pair type is awkward to name).
                    var kvType = kv.GetType();
                    if (kvType.GetProperty("Value")?.GetValue(kv) is not BinTreeStruct s || s.ClassHash != ClassAtomic) continue;
                    var key = kvType.GetProperty("Key")?.GetValue(kv);
                    uint nameHash = key switch { BinTreeHash kh => kh.Value, BinTreeU32 ku => ku.Value, _ => 0 };
                    string name = resolve(nameHash) ?? $"0x{nameHash:x8}";

                    string anm = "";
                    if (s.Properties.TryGetValue(FAnimRes, out var res) && res is BinTreeStruct rs
                        && rs.Properties.TryGetValue(FAnimPath, out var ap) && Meta.BinTexturePath.Is(ap))
                        anm = Meta.BinTexturePath.Read(ap, resolveWadPath);
                    if (anm.Length == 0) continue;

                    var showN = new List<string>(); var hideN = new List<string>();
                    var showH = new List<uint>(); var hideH = new List<uint>();
                    var particles = new List<AnimParticleEvent>();
                    var sounds = new List<AnimSoundEvent>();
                    var visibility = new List<AnimVisibilityEvent>();
                    if (s.Properties.TryGetValue(FEventMap, out var em) && em is System.Collections.IEnumerable events)
                        foreach (var ekv in events)
                        {
                            if (ekv.GetType().GetProperty("Value")?.GetValue(ekv) is not BinTreeStruct es) continue;
                            if (es.ClassHash == ClassSubmeshVis)
                            {
                                // M724: each event keeps its OWN lists and window. The clip-wide union below
                                // is still filled for the callers that predate the timeline, but a clip that
                                // swaps Sword_A for Sword_B at frame 12 is two edits, and unioning them shows
                                // both swords for the whole clip - which is what we used to draw.
                                var eShowN = new List<string>(); var eHideN = new List<string>();
                                var eShowH = new List<uint>(); var eHideH = new List<uint>();
                                Collect(es, FShow, eShowN, eShowH);
                                Collect(es, FHide, eHideN, eHideH);
                                visibility.Add(new AnimVisibilityEvent(Frame(es, FStartFrame, 0f), Frame(es, FEndFrame, -1f),
                                    eShowN, eHideN, eShowH, eHideH));
                                showN.AddRange(eShowN); hideN.AddRange(eHideN);
                                showH.AddRange(eShowH); hideH.AddRange(eHideH);
                            }
                            else if (es.ClassHash == ClassParticleEvent)
                            {
                                // M86: effect + bone can each be a string or a hash — capture both forms.
                                var (fxName, fxHash) = StringOrHash(es, FEffectKey, resolve);
                                if (fxName.Length == 0 && fxHash == 0) (fxName, fxHash) = StringOrHash(es, FEffectName, resolve);
                                if (fxName.Length == 0 && fxHash == 0) (fxName, fxHash) = StringOrHash(es, FParticleName, resolve);

                                // M724: every pair, in order - the engine spawns one effect per pair. The
                                // event may also name the bone itself, which the pair list then refines.
                                var (bone, boneHash) = StringOrHash(es, FBoneName, resolve);
                                var (tgt, tgtHash) = StringOrHash(es, FTargetBoneName, resolve);
                                var spawns = new List<AnimParticleSpawn>();
                                if (es.Properties.TryGetValue(FPairList, out var pl) && pl is BinTreeContainer pairs)
                                    foreach (var pe in pairs.Elements)
                                        if (pe is BinTreeStruct ps2)
                                        {
                                            var (pb, pbh) = StringOrHash(ps2, FBoneName, resolve);
                                            var (pt, pth) = StringOrHash(ps2, FTargetBoneName, resolve);
                                            // a pair that names no bone of its own inherits the event's
                                            if (pb.Length == 0 && pbh == 0) { pb = bone; pbh = boneHash; }
                                            if (pt.Length == 0 && pth == 0) { pt = tgt; pth = tgtHash; }
                                            spawns.Add(new AnimParticleSpawn(pb, pbh, pt, pth));
                                        }
                                // no pairs at all: the event itself is the one spawn (bone may be empty,
                                // which the viewer reads as "ride the character's own origin")
                                if (spawns.Count == 0) spawns.Add(new AnimParticleSpawn(bone, boneHash, tgt, tgtHash));
                                if (bone.Length == 0 && boneHash == 0) { bone = spawns[0].BoneName; boneHash = spawns[0].BoneHash; }

                                float start = Frame(es, FStartFrame, 0f);
                                float end = Frame(es, FEndFrame, -1f);
                                bool loop = es.Properties.TryGetValue(FIsLoop, out var pil) && pil is BinTreeBool pb2 && pb2.Value;
                                bool kill = es.Properties.TryGetValue(FIsKillEvent, out var ik) && ik is BinTreeBool kb && kb.Value;
                                if (fxName.Length > 0 || fxHash != 0)
                                    particles.Add(new AnimParticleEvent(fxName, fxHash, bone, start, boneHash,
                                        end, loop, kill, spawns));
                            }
                            else if (es.ClassHash == ClassSoundEvent)
                            {
                                // M90: Wwise event name (literal string) + optional frame/loop flags.
                                string snd = es.Properties.TryGetValue(FSoundName, out var sn) && sn is BinTreeString ss ? ss.Value : "";
                                float sStart = Frame(es, FStartFrame, 0f);
                                bool sLoop = es.Properties.TryGetValue(FIsLoop, out var sl) && sl is BinTreeBool sb && sb.Value;
                                if (snd.Length > 0) sounds.Add(new AnimSoundEvent(snd, sStart, sLoop));
                            }
                        }
                    // M724: events come out of a map, whose order is the bin's, not the timeline's.
                    visibility.Sort((a, b) => a.StartFrame.CompareTo(b.StartFrame));
                    clips.Add(new AnimClipInfo(name, anm, showN, hideN, showH, hideH,
                        particles.Count > 0 ? particles : null,
                        sounds.Count > 0 ? sounds : null,
                        visibility.Count > 0 ? visibility : null,
                        Frame(s, FTickDuration, 0f)));
                }
            }
        }
        catch { /* malformed bin — return what we have */ }
        return clips;
    }

    /// <summary>M724: a frame/seconds scalar. Riot authors these as F32, but a whole-numbered frame is
    /// sometimes written as an integer, and reading only F32 silently turned those into the default.</summary>
    private static float Frame(BinTreeStruct s, uint field, float fallback) =>
        s.Properties.TryGetValue(field, out var p)
            ? p switch
            {
                BinTreeF32 f => f.Value,
                BinTreeU32 u => u.Value,
                BinTreeI32 i => i.Value,
                BinTreeU16 u16 => u16.Value,
                BinTreeI16 i16 => i16.Value,
                _ => fallback,
            }
            : fallback;

    /// <summary>
    /// M724: the one splitter for <c>initialSubmeshToHide</c> and friends.
    ///
    /// <para>Riot writes the list space-separated, but comma- and semicolon-separated ones exist in
    /// hand-edited mod bins. Four readers used to split it and only two of them accepted commas, so the
    /// other two produced names with a trailing comma that matched no submesh - the hide silently did
    /// nothing. One splitter, every caller.</para>
    /// </summary>
    public static string[] SplitSubmeshList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(new[] { ' ', ',', ';', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Read a field that may be a literal string or a hash; resolve hashes to names when possible.</summary>
    private static (string Name, uint Hash) StringOrHash(BinTreeStruct s, uint field, Func<uint, string?> resolve)
    {
        if (!s.Properties.TryGetValue(field, out var p)) return ("", 0);
        return p switch
        {
            BinTreeString str => (str.Value, 0u),
            BinTreeHash h => (resolve(h.Value) ?? "", h.Value),
            BinTreeU32 u => (resolve(u.Value) ?? "", u.Value),
            _ => ("", 0u),
        };
    }

    private static void Collect(BinTreeStruct s, uint field, List<string> names, List<uint> hashes)
    {
        if (!s.Properties.TryGetValue(field, out var p) || p is not BinTreeContainer c) return;
        foreach (var el in c.Elements)
            switch (el)
            {
                case BinTreeString str: names.Add(str.Value); break;
                case BinTreeHash h: hashes.Add(h.Value); break;
                case BinTreeU32 u: hashes.Add(u.Value); break;
            }
    }

    /// <summary>The skin bin's initialSubmeshToHide list (space/comma-separated submesh names).</summary>
    public static IReadOnlyList<string> ParseInitialHide(byte[] skinBin)
    {
        try
        {
            var bin = SafeBinTree.Parse(skinBin);
            foreach (var o in bin.Objects.Values)
                if (FindString(o.Properties, FInitialHide) is { Length: > 0 } s)
                    return SplitSubmeshList(s);
        }
        catch { }
        return Array.Empty<string>();
    }

    private static string? FindString(IReadOnlyDictionary<uint, BinTreeProperty> props, uint field)
    {
        if (props.TryGetValue(field, out var p) && p is BinTreeString s) return s.Value;
        foreach (var v in props.Values)   // initialSubmeshToHide nests inside skinMeshProperties
            if (v is BinTreeStruct st && FindString(st.Properties, field) is { } inner) return inner;
        return null;
    }

    /// <summary>M95c: every authored Wwise event name from the skin bin's skinAudioProperties.bankUnits —
    /// the real trigger names the game uses (Play_vo_/Play_sfx_…). Animation clips don't carry VO events;
    /// voice lines only exist here. Empty on failure.</summary>
    public static IReadOnlyList<string> ParseBankEvents(byte[] skinBin)
    {
        var result = new List<string>();
        try
        {
            var bin = SafeBinTree.Parse(skinBin);
            foreach (var o in bin.Objects.Values) FindBankEvents(o.Properties, result);
        }
        catch { }
        return result;
    }

    private static void FindBankEvents(IReadOnlyDictionary<uint, BinTreeProperty> props, List<string> events)
    {
        if (props.TryGetValue(FSkinAudio, out var sa) && sa is BinTreeStruct sas
            && sas.Properties.TryGetValue(FBankUnits, out var bu) && bu is BinTreeContainer units)
            foreach (var u in units.Elements)
                if (u is BinTreeStruct us && us.Properties.TryGetValue(FBankEvents, out var ev) && ev is BinTreeContainer evc)
                    foreach (var el in evc.Elements)
                        if (el is BinTreeString s && s.Value.Length > 0) events.Add(s.Value);
        foreach (var v in props.Values)   // skinAudioProperties nests inside SkinCharacterDataProperties
            if (v is BinTreeStruct st) FindBankEvents(st.Properties, events);
    }

    /// <summary>Does a show/hide entry refer to this submesh? Names match case-insensitively; hashes
    /// match the ELF-lowercase hash (League's submesh/joint name hash) or FNV1a as fallback.</summary>
    public static bool Matches(string submeshName, IReadOnlyList<string> names, IReadOnlyList<uint> hashes)
    {
        foreach (var n in names)
            if (string.Equals(n, submeshName, StringComparison.OrdinalIgnoreCase)) return true;
        if (hashes.Count > 0)
        {
            uint elf = ElfLower(submeshName);
            uint fnv = HashAlgorithms.Fnv1a(submeshName);
            foreach (var h in hashes)
                if (h == elf || h == fnv) return true;
        }
        return false;
    }

    private static uint ElfLower(string s)
    {
        uint h = 0;
        foreach (char c in s.ToLowerInvariant())
        {
            h = (h << 4) + (byte)c;
            uint t = h & 0xF0000000;
            if (t != 0) h ^= t >> 24;
            h &= ~t;
        }
        return h;
    }
}
