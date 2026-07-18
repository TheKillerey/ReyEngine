using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Skeletons;

/// <summary>One named clip from a champion's animation-graph bin (M85): the .anm it plays plus the
/// submeshes it shows/hides while playing (SubmeshVisibilityEventData — hashes or literal names).</summary>
public sealed record AnimClipInfo(string Name, string AnmPath,
    IReadOnlyList<string> ShowNames, IReadOnlyList<string> HideNames,
    IReadOnlyList<uint> ShowHashes, IReadOnlyList<uint> HideHashes,
    IReadOnlyList<AnimParticleEvent>? ParticleEvents = null);

/// <summary>M86: one ParticleEventData in a clip — the VFX it spawns and the bone it rides.
/// BoneName is empty when the bin only stores a hash the hashtable can't resolve; BoneHash then
/// carries it, matched against skeleton joints by FNV1a or Elf of the joint name.</summary>
public sealed record AnimParticleEvent(string EffectName, uint EffectHash, string BoneName, float StartFrame, uint BoneHash = 0);

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
    private static readonly uint FEffectKey = HashAlgorithms.Fnv1a("mEffectKey");
    private static readonly uint FParticleName = HashAlgorithms.Fnv1a("mParticleName");
    private static readonly uint FBoneName = HashAlgorithms.Fnv1a("mBoneName");
    private static readonly uint FPairList = HashAlgorithms.Fnv1a("mParticleEventDataPairList");
    private static readonly uint FStartFrame = HashAlgorithms.Fnv1a("mStartFrame");
    private static readonly uint FInitialHide = HashAlgorithms.Fnv1a("initialSubmeshToHide");

    /// <summary>All named clips in an animation bin. Clip names resolve via the bin-name resolver
    /// (mClipDataMap keys are FNV1a hashes of names like Idle1/Attack1).</summary>
    public static IReadOnlyList<AnimClipInfo> ParseClips(byte[] animationBin, Func<uint, string?> resolve)
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
                        && rs.Properties.TryGetValue(FAnimPath, out var ap) && ap is BinTreeString aps)
                        anm = aps.Value;
                    if (anm.Length == 0) continue;

                    var showN = new List<string>(); var hideN = new List<string>();
                    var showH = new List<uint>(); var hideH = new List<uint>();
                    var particles = new List<AnimParticleEvent>();
                    if (s.Properties.TryGetValue(FEventMap, out var em) && em is System.Collections.IEnumerable events)
                        foreach (var ekv in events)
                        {
                            if (ekv.GetType().GetProperty("Value")?.GetValue(ekv) is not BinTreeStruct es) continue;
                            if (es.ClassHash == ClassSubmeshVis)
                            {
                                Collect(es, FShow, showN, showH);
                                Collect(es, FHide, hideN, hideH);
                            }
                            else if (es.ClassHash == ClassParticleEvent)
                            {
                                // M86: effect + bone can each be a string or a hash — capture both forms.
                                var (fxName, fxHash) = StringOrHash(es, FEffectKey, resolve);
                                if (fxName.Length == 0 && fxHash == 0) (fxName, fxHash) = StringOrHash(es, FParticleName, resolve);
                                var (bone, boneHash) = StringOrHash(es, FBoneName, resolve);
                                // real data nests the bone inside mParticleEventDataPairList entries,
                                // usually as an unresolvable hash — keep the hash even without a name.
                                if (bone.Length == 0 && boneHash == 0
                                    && es.Properties.TryGetValue(FPairList, out var pl)
                                    && pl is BinTreeContainer pairs)
                                    foreach (var pe in pairs.Elements)
                                        if (pe is BinTreeStruct ps2)
                                        {
                                            (bone, boneHash) = StringOrHash(ps2, FBoneName, resolve);
                                            if (bone.Length > 0 || boneHash != 0) break;
                                        }
                                float start = es.Properties.TryGetValue(FStartFrame, out var sf) && sf is BinTreeF32 f ? f.Value : 0f;
                                if (fxName.Length > 0 || fxHash != 0)
                                    particles.Add(new AnimParticleEvent(fxName, fxHash, bone, start, boneHash));
                            }
                        }
                    clips.Add(new AnimClipInfo(name, anm, showN, hideN, showH, hideH,
                        particles.Count > 0 ? particles : null));
                }
            }
        }
        catch { /* malformed bin — return what we have */ }
        return clips;
    }

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
                    return s.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
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
