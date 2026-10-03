using System.IO.Hashing;
using System.Numerics;
using System.Text;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.Formats.Characters;

/// <summary>
/// M812: what to scan and how to read the files.
/// </summary>
/// <param name="SkinBinPath">The skin bin, as a WAD path: <c>data/characters/lillia/skins/skin49.bin</c>.</param>
/// <param name="ReadAsset">Reads an asset by WAD path (or by the <c>0x...</c> form of a chunk link) and returns its
/// bytes, or null when it is not there. The host passes the same mount-aware reader every other preview read uses, so a
/// project override of a skin is what gets scanned. May be called from any thread. It must THROW, not return null, when
/// the file is there and cannot be read (a mount that failed, a chunk that will not decompress): the scan records a throw
/// as a warning on the inventory and goes on, so a failed read is never mistaken for a file that does not exist.</param>
/// <param name="ResolveName">Hash to bin field/class name (the hash database). Optional: the inventory classifies by
/// the names it needs and by their hashes, so an install without the dictionary still scans.</param>
/// <param name="ResolveWadPath">Chunk hash to path, to name a texture reference stored as a WadChunkLink.</param>
/// <param name="SkinNumbers">The champion's other skins to compare with; null probes <c>skin0 .. skin999</c> through
/// <paramref name="ReadAsset"/> (the highest shipped is 305). A listed skin that cannot be compared is a warning.</param>
/// <param name="SkinName">Display name of a skin number (the client's skins.json, <see cref="ClientNameCatalog"/>);
/// null or a null result labels it <c>skinN</c>.</param>
/// <param name="IncludeSharing">False skips the comparison with the other skins - the census does not need it.</param>
/// <param name="ExpectedSkinNumbers">The skins the host KNOWS the character has (the champion WAD's own listing, which the
/// Character browser reads). Probing finds what the reader can open; this says what it should have found, so a sibling
/// the reader missed is a warning instead of a shared item that quietly reads as unshared. Null: only what is found counts.</param>
public sealed record SkinColorRequest(
    string SkinBinPath,
    Func<string, byte[]?> ReadAsset,
    Func<uint, string?>? ResolveName = null,
    Func<ulong, string?>? ResolveWadPath = null,
    IReadOnlyList<int>? SkinNumbers = null,
    Func<int, string?>? SkinName = null,
    bool IncludeSharing = true,
    IReadOnlyList<int>? ExpectedSkinNumbers = null);

/// <summary>
/// M812: the Chroma Studio's read-only colour inventory - builds a <see cref="SkinColorInventory"/> for one champion
/// skin or chroma and marks every item shared or unshared with the champion's other skins.
///
/// <para><b>Pure and deterministic</b> over the bytes <see cref="SkinColorRequest.ReadAsset"/> returns: it opens no
/// archive, touches no setting and writes nothing, and the same bytes give the same inventory, in the same order.</para>
///
/// <para><b>Body.</b> <see cref="MaterialDocument.Parse"/> is the list - local and linked StaticMaterialDef samplers, the
/// skin block's own texture fields (<c>(skin default texture)</c>) and each materialOverride's inline texture. A
/// sampler is colour-bearing when its role is diffuse, emissive, gradient or matcap (the skin block's
/// <c>texture</c>, <c>emissiveTexture</c> and <c>reflectionMap</c> map to diffuse, emissive and reflection). Samplers are
/// classified BY NAME, because that is all the bin offers. M812 census of the local materials in the 15,306 champion
/// skin bins (root.bin included): 203 distinct sampler names over 79,858 entries; <see cref="TextureSlot"/>'s name rules
/// classify 94 of the names (54,506 entries) - 31,668 entries as colour and 22,838 as masks - and the other 25,352
/// entries (32%), among them noise, scroll, flipbook, distortion and RMA maps, are not classified. Unclassified samplers
/// are left out, not guessed, and reported in <see cref="SkinColorInventory.ExcludedSamplers"/>. One of the 94 is a
/// colour name that is not a colour picture: <c>EmissionR_DistortionG_Texture</c> (1,016 entries) packs an emission
/// mask in R and a distortion map in G, so hue-shifting it would corrupt both. A sampler whose name carries two channel
/// tags is excluded as channel-packed (<see cref="IsChannelPacked"/>).</para>
///
/// <para><b>Matcap and reflection are listed.</b> A matcap is an RGB picture that is added over the lit surface and a
/// reflection map is the picture reflected in it: both put colour on the body, and a recolour that skipped them would
/// leave a chrome or a gem in its old colour. Their masks are not listed, and neither is the skin block's
/// <c>glossTexture</c> (a specular data map).</para>
///
/// <para><b>Effects.</b> The systems THIS skin uses, which is not simply the systems its dependency closure holds. The
/// skin object names its ResourceResolver (<c>mResourceResolver</c>); every target of that resolver is one, and so is every
/// system the skin object links directly and every child of those, recursively - by object link, or by effect key through
/// that resolver. These are the skin's own, and are listed plainly. Measured over all 14,937 shipped skin bins: the linked
/// resolver is in the skin bin for 14,750, in a dependency bin for 37 (new champions, traps and totems), in none of the files
/// for 4, and 146 companion skins name none.</para>
///
/// <para><b>Inferred.</b> The only other construct that reaches a closure system is a <c>GearSkinUpgrade</c>
/// (<c>mGearData.mVFXResourceResolver</c> - Lillia's E, Aatrox's prestige wings, Lux's Elementalist forms): it sits in the
/// skin bin for 70 skins and in a shared bin for 66, and nothing in the skin object links to it. That a gear upgrade in the
/// closure is THIS skin's is an inference from how Riot groups the shared bins, so what only it reaches - and what only
/// those systems' children reach - is listed as <b>inferred</b> (<see cref="EffectSystemEntry.IsInferred"/>): 136 of the
/// 14,937 skins have any, 5,069 systems in all (Elementalist Lux, skin 7, 364 of 466). The preview resolves a child's key
/// through EVERY resolver in the closure, and a closure holds other skins' resolvers; the inventory does not, because a key
/// only another skin's resolver maps would pull in that skin's system. Measured over all 14,937 skin bins, not one reaches a
/// system only that way (Lillia 49, Lux 7 and Aatrox 0 included), so nothing is lost by leaving it out and nothing can leak
/// in. With these rules 44 of the 14,937 closures hold a system the skin does not reach - Blitzcrank skin 0 carries a
/// leftover Bard system, Mordekaiser skin 0 a <c>zzDELETE_ME_</c> one, Gnar skin 22 eight; they can be dead leftovers of any
/// skin, not necessarily another skin's - and the rest hold exactly what is listed.</para>
///
/// <para><b>Sharing.</b> The same "uses" sets are built for every <c>skinN.bin</c> of the character and an item is shared
/// when another skin's set holds it - a texture by chunk hash in ANY role (editing the file changes it for every
/// sampler), a system by object path hash. Parsed bins are cached by content fingerprint, so a second scan of the same
/// champion re-reads its files and re-parses only what changed; nothing is cached across more than a few characters.
/// "The same champion" is the same CHARACTER folder: a champion WAD also ships pets and forms (Tibbers, Elise's spider,
/// Lux's elements) as characters of their own, and those are not compared - a file one of them shares is not reported. Nor
/// are other champions, maps and the global assets: "unshared" means no OTHER SKIN OF THIS CHARACTER uses it, not that
/// nothing else does (<see cref="SkinColorInventory.IsOutsideCharacter"/> marks the files that live outside the
/// character's own folder).</para>
///
/// <para><b>Never a partial list that looks whole.</b> A dependency bin that is not there or would not parse, a bin that
/// parsed only in part, a sibling skin the reader could not open or parse, a sibling the host knows the character has and
/// the scan did not compare, a material the skin names that no file it links defines: each is a line in
/// <see cref="SkinColorInventory.Warnings"/>, and the number of skins compared with is always stated, zero included.</para>
///
/// <para><b>Material links.</b> A skin's <c>skinMeshProperties.material</c> and <c>materialOverride[].material</c> are object
/// links, and <see cref="MaterialDocument.Parse"/> resolves one in the skin bin, then in the skin bin's DIRECT dependencies
/// (and a host bin, which a scan has none of). A link it leaves unresolved produces no material, so its textures are silently
/// missing from the body list; the scan compares the links with the materials the parse produced and warns for each one that
/// is missing (<see cref="SkinColorInventory.UnresolvedMaterials"/>). Measured over all 14,937 shipped skin bins: 7,867 name a
/// material, with 25,022 links - 23,049 in the skin bin, 1,899 in a direct dependency, NONE only in a transitive dependency
/// (so following transitive dependencies would find nothing, and the scan keeps the parse's rule), and 74 in 62 skins that are
/// defined in no bin the skin links: Nunu's snowballs, Quinn's Valor, Nasus's Fury of the Sands, Irelia's blades, Orianna's ball,
/// Yuumi and Rammus - 66 of them in another character's skin bin of the same WAD (a bin the game loads beside the skin, which a
/// scan cannot know without guessing), 8 in no bin of the WAD.</para>
///
/// <para><b>Concurrency.</b> One scan at a time per scanner; a scan that is waiting for its turn observes its token, so a
/// skin switch frees it. The caches belong to the scanner and are only touched under that turn.</para>
///
/// <para><b>Known gaps.</b> Colour that is not in a texture or a listed field is not here: a beam's
/// <c>mAnimatedColorWithDistance</c> curve (a curve over world units, not life), <c>modulationFactor</c>, vertex colours
/// and the colours inside .scb/.sco meshes. Samplers the bin does not name as colour (see Body) are reported as excluded,
/// not guessed.</para>
/// </summary>
public sealed class SkinColorScanner
{
    private const int MaxClosureBins = 64;      // the host's own dependency walk stops at the same count
    private const int MaxCachedCharacters = 3;
    private const int MaxProbedSkinNumber = 999;
    private const int MaxListedProblems = 4;    // names in one warning line; the rest are counted

    // MaterialDocument names its pseudo-bindings by these shaders; the real-data tests fail if they ever change
    private const string SkinBlockShader = "SkinMeshDataProperties";
    private const string InlineOverrideShader = "MaterialOverride";

    private static uint H(string s) => HashAlgorithms.Fnv1a(s);
    private static readonly uint SkinClass = H("SkinCharacterDataProperties");
    private static readonly uint ResolverClass = H("ResourceResolver");
    private static readonly uint FResolverLink = H("mResourceResolver");
    private static readonly uint FResourceMap = H("resourceMap");
    private static readonly uint FMResourceMap = H("mResourceMap");
    private static readonly uint GearUpgradeClass = H("GearSkinUpgrade");
    private static readonly uint FGearData = H("mGearData");
    private static readonly uint FGearResolver = H("mVFXResourceResolver");
    private static readonly uint FSkinMeshProperties = H("skinMeshProperties");
    private static readonly uint FMaterial = H("material");
    private static readonly uint FMaterialOverride = H("materialOverride");
    private static readonly uint FSubmesh = H("submesh");

    // One scan at a time. A SemaphoreSlim and not a lock, so a scan queued behind another can be cancelled while it waits
    // (the Character window cancels the old skin's scan the moment another skin opens, and a button that stayed disabled
    // until a minute-long scan of the old skin finished was the alternative).
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Guards _characters and _recent, which a diagnostic read (CachedCharacters) may touch while a scan holds _gate.
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, CharacterCache> _characters = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _recent = new();

    /// <summary>Forget every cached bin and every skin's "uses" set. The caches are validated by content on every
    /// scan, so this is only needed to release memory. Waits for a scan in progress.</summary>
    public void Clear()
    {
        _gate.Wait();
        try { lock (_cacheLock) { _characters.Clear(); _recent.Clear(); } }
        finally { _gate.Release(); }
    }

    /// <summary>The character folders whose parsed bins are cached right now, oldest first - for the tests and the probe.
    /// At most three.</summary>
    public IReadOnlyList<string> CachedCharacters
    {
        get { lock (_cacheLock) return _recent.ToArray(); }
    }

    /// <summary>How a skin is named in a "shared with" list: <c>Petals of Spring Lillia (skin46)</c>, or <c>skin46</c>
    /// when no display name is known.</summary>
    public static string SkinLabel(int number, string? displayName) =>
        string.IsNullOrWhiteSpace(displayName) ? $"skin{number}" : $"{displayName} (skin{number})";

    /// <summary><c>data/characters/&lt;folder&gt;/skins/skin&lt;N&gt;.bin</c> split into its folder and number; false for
    /// any other shape (<c>root.bin</c> included).</summary>
    public static bool TryParseSkinPath(string path, out string folder, out int number)
    {
        folder = ""; number = -1;
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5 || !parts[0].Equals("data", StringComparison.OrdinalIgnoreCase)
            || !parts[1].Equals("characters", StringComparison.OrdinalIgnoreCase)
            || !parts[3].Equals("skins", StringComparison.OrdinalIgnoreCase)) return false;
        string file = parts[4];
        if (!file.StartsWith("skin", StringComparison.OrdinalIgnoreCase) || !file.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) return false;
        if (!int.TryParse(file.AsSpan(4, file.Length - 8), out number) || number < 0) return false;
        folder = parts[2];
        return true;
    }

    /// <summary>Scan one skin. Blocks; run it off the UI thread. Cancellation is checked while waiting for the scanner, between
    /// bins and between skins.</summary>
    public SkinColorInventory Scan(SkinColorRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _gate.Wait(cancellationToken);
        try { return ScanLocked(request, cancellationToken); }
        finally { _gate.Release(); }
    }

    private SkinColorInventory ScanLocked(SkinColorRequest request, CancellationToken cancellationToken)
    {
        string skinPath = request.SkinBinPath.Replace('\\', '/');
        bool champion = TryParseSkinPath(skinPath, out string folder, out int number);
        var cache = champion ? CacheFor(folder) : new CharacterCache();
        var scan = new ScanRun(request, cache, cancellationToken);

        var plan = scan.Plan(skinPath);
        if (plan is null) return SkinColorInventory.Empty(skinPath, scan.DescribeFailure(skinPath));

        var notes = new List<string>();
        var warnings = new List<string>();
        var analysis = scan.Analyse(plan, detailed: true, notes);
        var uses = analysis.ToUses();
        // a result that was degraded by a failed read of a linked bin is not kept: the failure may be gone by the next scan, and the
        // closure's fingerprint (which covers the bins the walk loaded) would not tell
        if (champion && uses.Problems.Materials is null) cache.Uses[skinPath] = (plan.Fingerprint, uses);
        warnings.AddRange(uses.Problems.DescribeForSelf());

        // ---- the other skins -----------------------------------------------------------------
        var compared = new List<string>();
        var comparedNumbers = new HashSet<int>();
        var textureSharers = new Dictionary<ulong, List<string>>();
        var systemSharers = new Dictionary<uint, List<string>>();
        bool sharing = false;
        if (!request.IncludeSharing) notes.Add("Sharing was not computed.");
        else if (!champion) notes.Add("Sharing was not computed: this is not a data/characters/<name>/skins/skinN.bin path.");
        else
        {
            sharing = true;
            bool probing = request.SkinNumbers is null;
            var candidates = probing
                ? Enumerable.Range(0, MaxProbedSkinNumber + 1)
                : request.SkinNumbers!.Where(n => n >= 0).Distinct().Order();
            var notCompared = new SortedDictionary<int, string>();   // sibling number -> why there is no comparison with it at all
            var understated = new SortedDictionary<int, string>();   // sibling number -> why the comparison with it may miss things

            foreach (int other in candidates)
            {
                if (other == number) continue;
                cancellationToken.ThrowIfCancellationRequested();
                string otherPath = $"data/characters/{folder}/skins/skin{other}.bin";
                var otherUses = scan.UsesOf(otherPath);
                if (otherUses is null)
                {
                    // a probe that finds nothing at skin417.bin is the normal case; one that finds a file it cannot use is not,
                    // and neither is a skin the caller named that is not there
                    var (status, detail) = scan.StatusOf(otherPath);
                    if (status != BinStatus.NotFound || !probing)
                        notCompared[other] = $"skin{other}.bin {Reason(status, detail)}, so it was not compared.";
                    continue;
                }
                string label = SkinLabel(other, request.SkinName?.Invoke(other));
                compared.Add(label);
                comparedNumbers.Add(other);
                if (otherUses.Problems.Any) understated[other] = $"{label}: {otherUses.Problems.DescribeForSibling()}, so what it shares may be understated.";
                foreach (ulong texture in otherUses.Textures)
                    if (uses.Textures.Contains(texture)) Append(textureSharers, texture, label);
                foreach (uint system in otherUses.Systems)
                    if (uses.Systems.Contains(system)) Append(systemSharers, system, label);
            }

            // what the host knows the character has, against what was compared
            foreach (int want in (request.ExpectedSkinNumbers ?? Array.Empty<int>()).Where(n => n >= 0).Distinct().Order())
            {
                if (want == number || comparedNumbers.Contains(want) || notCompared.ContainsKey(want)) continue;
                if (probing ? want > MaxProbedSkinNumber : !request.SkinNumbers!.Contains(want)) continue;   // never tried: the caller narrowed the comparison
                var (status, detail) = scan.StatusOf($"data/characters/{folder}/skins/skin{want}.bin");
                notCompared[want] = $"skin{want}.bin is listed for this character but {Reason(status, detail)}, so it was not compared.";
            }

            // the skins with NO comparison come first - they are the ones whose items may read as unshared - and each list is capped
            AddCapped(warnings, notCompared.Values, MaxListedProblems + 2, "skin(s) were not compared");
            AddCapped(warnings, understated.Values, MaxListedProblems, "other skin(s) were read only in part");
        }

        return analysis.Build(skinPath, folder, number, SkinLabel(number, request.SkinName?.Invoke(number)),
            textureSharers, systemSharers, compared, sharing, notes, warnings);
    }

    private static void AddCapped(List<string> into, IReadOnlyCollection<string> lines, int cap, string what)
    {
        into.AddRange(lines.Take(cap));
        if (lines.Count > cap) into.Add($"... and {lines.Count - cap} more {what}.");
    }

    private static void Append<TKey>(Dictionary<TKey, List<string>> map, TKey key, string label) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<string>();
        list.Add(label);
    }

    /// <summary>The cache of one character. Registered in <c>_recent</c> HERE, when it is created or asked for again, and not
    /// when its scan succeeds: a scan that is cancelled or throws has already filled it, and a cache nothing registered
    /// could never be evicted.</summary>
    private CharacterCache CacheFor(string folder)
    {
        lock (_cacheLock)
        {
            if (!_characters.TryGetValue(folder, out var cache)) _characters[folder] = cache = new CharacterCache();
            _recent.RemoveAll(f => f.Equals(folder, StringComparison.OrdinalIgnoreCase));
            _recent.Add(folder);
            while (_recent.Count > MaxCachedCharacters)
            {
                _characters.Remove(_recent[0]);
                _recent.RemoveAt(0);
            }
            return cache;
        }
    }

    // ============================================================ what is cached ====================

    /// <summary>One bin, parsed once: what the inventory needs and nothing else, so a champion's worth of bins is
    /// kilobytes and not the trees.</summary>
    private sealed class BinIndex
    {
        public required ulong Fingerprint { get; init; }
        public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();
        public Dictionary<uint, ResolverObject> Resolvers { get; } = new();
        /// <summary>The resolvers embedded in <c>GearSkinUpgrade</c> objects (<c>mGearData.mVFXResourceResolver</c>).</summary>
        public List<ResolverObject> GearResolvers { get; } = new();
        public Dictionary<uint, VfxColorSystem> Systems { get; } = new();
        public SkinObject? Skin { get; set; }
        /// <summary>The materials the bin's skin block names - its base mesh's and each submesh override's - in the order
        /// <see cref="MaterialDocument.Parse"/> meets them. Empty for a bin with no skin block.</summary>
        public IReadOnlyList<MaterialLink> MaterialLinks { get; set; } = Array.Empty<MaterialLink>();
        /// <summary>Set when the bin would not parse at all.</summary>
        public string? ParseError { get; init; }
        /// <summary>Objects the tolerant parser had to give up on (<see cref="SafeBinTree.IsLossy"/>): the bin is there and
        /// usable, but not everything in it was read.</summary>
        public int UnreadableObjects { get; init; }
    }

    /// <summary>A material the skin block names: <paramref name="Submesh"/> null for the base mesh's.</summary>
    private sealed record MaterialLink(string? Submesh, uint Hash);

    private sealed class ResolverObject
    {
        public required uint PathHash { get; init; }
        /// <summary>In file order; a repeated key keeps its LAST value, as <see cref="VfxSystemResolver.ExtractResourceMap"/> does.</summary>
        public Dictionary<uint, uint> Map { get; } = new();
    }

    private sealed class SkinObject
    {
        public uint ResolverLink { get; init; }
        public HashSet<uint> ObjectLinks { get; } = new();
    }

    /// <summary>What was not whole about reading one skin: the same facts are reported for the scanned skin ("3 dependency
    /// bins of this skin...") and for a sibling ("skin5: 3 dependency bins...").</summary>
    private sealed record SkinProblems(IReadOnlyList<string> Unavailable, IReadOnlyList<string> Partial, bool WalkCut, string? Materials,
        IReadOnlyList<string> UnresolvedMaterials)
    {
        public static readonly SkinProblems None = new(Array.Empty<string>(), Array.Empty<string>(), false, null, Array.Empty<string>());
        public bool Any => Unavailable.Count > 0 || Partial.Count > 0 || WalkCut || Materials is not null || UnresolvedMaterials.Count > 0;

        public IEnumerable<string> DescribeForSelf()
        {
            if (Unavailable.Count > 0)
                yield return $"{Unavailable.Count} dependency bin(s) of this skin were not available: {Names(Unavailable)}. Effects and materials in them are not listed.";
            if (Partial.Count > 0)
                yield return $"{Partial.Count} bin(s) could be read only in part: {Names(Partial)}. What they hold is listed only in part.";
            if (WalkCut)
                yield return $"The dependency walk stopped at {MaxClosureBins} bins; the bins after that were not read.";
            if (Materials is not null)
                yield return "The skin's materials could not be read in full: " + Materials;
            if (UnresolvedMaterials.Count > 0)
                yield return $"{UnresolvedMaterials.Count} material link(s) of this skin lead to a material that is defined neither in its bin nor in a bin it links directly, "
                             + $"so those textures are not listed: {Names(UnresolvedMaterials)}.";
        }

        public string DescribeForSibling()
        {
            var parts = new List<string>();
            if (Unavailable.Count > 0) parts.Add($"{Unavailable.Count} dependency bin(s) not available ({Names(Unavailable)})");
            if (Partial.Count > 0) parts.Add($"{Partial.Count} bin(s) read only in part ({Names(Partial)})");
            if (WalkCut) parts.Add("the dependency walk was cut short");
            if (Materials is not null) parts.Add("its materials were not read in full");
            if (UnresolvedMaterials.Count > 0) parts.Add($"{UnresolvedMaterials.Count} material link(s) unresolved ({Names(UnresolvedMaterials)})");
            return string.Join("; ", parts);
        }

        private static string Names(IReadOnlyList<string> items) =>
            string.Join("; ", items.Take(MaxListedProblems)) + (items.Count > MaxListedProblems ? $"; and {items.Count - MaxListedProblems} more" : "");
    }

    private sealed record SkinUses(HashSet<ulong> Textures, HashSet<uint> Systems, SkinProblems Problems);

    private sealed class CharacterCache
    {
        public Dictionary<string, BinIndex> Bins { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, (ulong Fingerprint, SkinUses Uses)> Uses { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private enum BinStatus { Ok, NotFound, Empty, Unreadable, Unparsed }

    /// <summary>Why a bin is not in the walk, as the end of a sentence: <c>skin12.bin could not be read (IOException: ...)</c>.</summary>
    private static string Reason(BinStatus status, string? detail) => status switch
    {
        BinStatus.NotFound => "was not found",
        BinStatus.Empty => "is empty",
        BinStatus.Unreadable => "could not be read (" + detail + ")",
        BinStatus.Unparsed => "could not be parsed" + (string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")"),
        _ => "was not compared",
    };

    /// <summary>The same, for a parenthesis after a name: <c>Fx.bin (not found)</c>.</summary>
    private static string ShortReason(BinStatus status, string? detail) => status switch
    {
        BinStatus.NotFound => "not found",
        BinStatus.Empty => "empty",
        BinStatus.Unreadable => "could not be read: " + detail,
        BinStatus.Unparsed => "could not be parsed",
        _ => "not compared",
    };

    private sealed record LoadedBin(string Path, BinIndex Index, byte[]? Bytes);

    private sealed class ClosurePlan
    {
        public required List<LoadedBin> Bins { get; init; }
        public required ulong Fingerprint { get; init; }
        public required SkinProblems Problems { get; init; }
        public LoadedBin Skin => Bins[0];
    }

    // ============================================================ one scan ==========================

    private sealed class ScanRun
    {
        private readonly SkinColorRequest _request;
        private readonly CharacterCache _cache;
        private readonly CancellationToken _ct;
        private readonly Dictionary<string, LoadedBin?> _loaded = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (BinStatus Status, string? Detail)> _status = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _materialReadFaults = new();
        private readonly Func<uint, string?> _name;

        public ScanRun(SkinColorRequest request, CharacterCache cache, CancellationToken ct)
        {
            _request = request; _cache = cache; _ct = ct;
            _name = request.ResolveName ?? (_ => null);
        }

        /// <summary>The reader, with a throw kept apart from a miss.</summary>
        private (byte[]? Bytes, string? Error) TryRead(string path)
        {
            try { return (_request.ReadAsset(path), null); }
            catch (OperationCanceledException) when (_ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return (null, $"{ex.GetType().Name}: {Tidy(ex.Message)}"); }
        }

        /// <summary>The reader MaterialDocument uses for the bins its materials link. A throw is not hidden: it is counted, and
        /// the scan says its materials were not read in full.</summary>
        private byte[]? ReadForMaterials(string path)
        {
            var (bytes, error) = TryRead(path);
            if (error is not null) _materialReadFaults.Add($"{FileName(path)}: {error}");
            return bytes;
        }

        public (BinStatus Status, string? Detail) StatusOf(string path) =>
            _status.TryGetValue(path, out var known) ? known : (BinStatus.NotFound, null);

        public string DescribeFailure(string skinPath)
        {
            var (status, detail) = StatusOf(skinPath);
            return status switch
            {
                BinStatus.Unreadable => $"The skin bin could not be read ({detail}), so nothing was scanned.",
                BinStatus.Unparsed => $"The skin bin could not be parsed ({detail}), so nothing was scanned.",
                BinStatus.Empty => "The skin bin could not be read: it is empty, so nothing was scanned.",
                _ => "The skin bin could not be read: the reader does not have it, so nothing was scanned.",
            };
        }

        private LoadedBin? Fail(string path, BinStatus status, string? detail)
        {
            _loaded[path] = null;
            _status[path] = (status, detail);
            return null;
        }

        /// <summary>Read, fingerprint and (when new or changed) parse one bin. A bin is read once per scan however many
        /// skins link it. Null when it is not there, would not read or would not parse; <see cref="StatusOf"/> says which.</summary>
        private LoadedBin? Load(string path, bool keepBytes)
        {
            _ct.ThrowIfCancellationRequested();
            if (_loaded.TryGetValue(path, out var known) && (known is null || !keepBytes || known.Bytes is not null)) return known;

            var (bytes, error) = TryRead(path);
            if (error is not null) return Fail(path, BinStatus.Unreadable, error);
            if (bytes is null) return Fail(path, BinStatus.NotFound, null);
            if (bytes.Length == 0) return Fail(path, BinStatus.Empty, null);

            ulong fingerprint = XxHash64.HashToUInt64(bytes) ^ (ulong)bytes.Length * 0x9E3779B97F4A7C15UL;
            if (!_cache.Bins.TryGetValue(path, out var index) || index.Fingerprint != fingerprint)
                _cache.Bins[path] = index = BuildIndex(bytes, fingerprint, _request.ResolveWadPath);
            if (index.ParseError is not null) return Fail(path, BinStatus.Unparsed, index.ParseError);

            var loaded = new LoadedBin(path, index, keepBytes ? bytes : null);
            _loaded[path] = loaded;
            _status[path] = (BinStatus.Ok, null);
            return loaded;
        }

        /// <summary>The skin bin and everything it links, in the host's walk order (breadth first, 64 bins at most).
        /// Null when the skin bin itself cannot be read.</summary>
        public ClosurePlan? Plan(string skinPath)
        {
            var first = Load(skinPath, keepBytes: true);
            if (first is null) return null;

            var bins = new List<LoadedBin> { first };
            var unavailable = new List<string>();
            var visited = new HashSet<ulong> { HashAlgorithms.WadPath(skinPath) };
            var queue = new Queue<string>();
            void Enqueue(LoadedBin bin)
            {
                foreach (string dependency in bin.Index.Dependencies)
                    if (!string.IsNullOrWhiteSpace(dependency) && visited.Add(HashAlgorithms.WadPath(dependency)))
                        queue.Enqueue(dependency);
            }
            Enqueue(first);
            bool cut = false;
            while (queue.Count > 0)
            {
                if (bins.Count >= MaxClosureBins) { cut = true; break; }
                string dependency = queue.Dequeue();
                var bin = Load(dependency, keepBytes: false);
                if (bin is null)
                {
                    var (status, detail) = StatusOf(dependency);
                    unavailable.Add($"{FileName(dependency)} ({ShortReason(status, detail)})");
                    continue;
                }
                bins.Add(bin);
                Enqueue(bin);
            }

            // the closure's identity: every bin's content, in walk order
            ulong fingerprint = 17;
            foreach (var bin in bins)
                fingerprint = (fingerprint * 31 + bin.Index.Fingerprint) * 31 + HashAlgorithms.WadPath(bin.Path);

            var partial = bins.Where(b => b.Index.UnreadableObjects > 0)
                .Select(b => $"{FileName(b.Path)} ({b.Index.UnreadableObjects} object(s) unreadable)").ToList();
            return new ClosurePlan
            {
                Bins = bins, Fingerprint = fingerprint,
                Problems = new SkinProblems(unavailable, partial, cut, null, Array.Empty<string>()),
            };
        }

        /// <summary>What another skin uses, from the cache when its closure has not changed since. Null when its bin cannot be
        /// used; <see cref="StatusOf"/> says why.</summary>
        public SkinUses? UsesOf(string skinPath)
        {
            var plan = Plan(skinPath);
            if (plan is null) return null;
            if (_cache.Uses.TryGetValue(skinPath, out var cached) && cached.Fingerprint == plan.Fingerprint) return cached.Uses;
            var uses = Analyse(plan, detailed: false, new List<string>()).ToUses();
            if (uses.Problems.Materials is null) _cache.Uses[skinPath] = (plan.Fingerprint, uses);   // see ScanLocked
            return uses;
        }

        // -------------------------------------------------------------- analysis --------------

        public Analysis Analyse(ClosurePlan plan, bool detailed, List<string> notes)
        {
            var reach = Reach(plan, detailed);
            MaterialDocument? document = null;
            string? materials = null;
            if (plan.Skin.Bytes is { } skinBytes)
            {
                _materialReadFaults.Clear();
                try { document = MaterialDocument.Parse(skinBytes, _name, _request.ResolveWadPath, ReadForMaterials); }
                catch (Exception ex) { materials = ex.Message; }
                if (materials is null && _materialReadFaults.Count > 0)
                    materials = $"{_materialReadFaults.Count} linked bin(s) failed to read ({string.Join("; ", _materialReadFaults.Take(MaxListedProblems))})";
            }

            // The links the skin block names, against the materials the parse produced: a link it could not resolve produced none,
            // and nothing else says so (see the class remarks, "Material links").
            var unresolvedMaterials = new List<string>();
            if (document is not null && plan.Skin.Index.MaterialLinks.Count > 0)
            {
                var produced = document.Materials.Select(m => m.ObjectPathHash).Where(h => h != 0).ToHashSet();
                foreach (var link in plan.Skin.Index.MaterialLinks)
                    if (!produced.Contains(link.Hash))
                    {
                        string where = link.Submesh is null ? "the base mesh" : $"submesh \"{link.Submesh}\"";
                        string hash = $"0x{link.Hash:x8}";
                        unresolvedMaterials.Add(_name(link.Hash) is { Length: > 0 } named ? $"{where}: {named} ({hash})" : $"{where}: {hash}");
                    }
            }
            if (detailed && reach.ResolverNote is { } resolverNote) notes.Add(resolverNote);
            if (detailed && reach.UnresolvedEntries > 0)
                notes.Add($"{reach.UnresolvedEntries} effect key(s) or link(s) lead to no system in this skin's files (the key maps nothing, or the system is in no file the scan can read), so they cannot be listed.");
            return new Analysis(reach, document, plan.Problems with { Materials = materials, UnresolvedMaterials = unresolvedMaterials }, _request);
        }

        // -------------------------------------------------------------- effects --------------

        private ReachResult Reach(ClosurePlan plan, bool detailed)
        {
            // every system object in the closure, first one wins (the skin's own bin first)
            var systems = new Dictionary<uint, (VfxColorSystem System, string Bin)>();
            foreach (var bin in plan.Bins)
                foreach (var (hash, system) in bin.Index.Systems) systems.TryAdd(hash, (system, bin.Path));

            // the skin's resolver: the object the skin names, wherever the closure keeps it
            var skin = plan.Skin.Index.Skin;
            ResolverObject? resolver = null;
            string? note = null;
            string source;
            if (skin is null) { source = "no skin object in the skin bin"; note = "This bin holds no skin object, so none of its effect keys resolve."; }
            else if (skin.ResolverLink == 0) { source = "the skin names no ResourceResolver"; note = "This skin names no ResourceResolver, so none of its effect keys resolve."; }
            else
            {
                source = "mResourceResolver not found in the closure";
                foreach (var bin in plan.Bins)
                    if (bin.Index.Resolvers.TryGetValue(skin.ResolverLink, out resolver))
                    {
                        source = ReferenceEquals(bin, plan.Skin) ? "in the skin bin" : "in " + FileName(bin.Path);
                        break;
                    }
                if (resolver is null) note = "The skin's ResourceResolver is in none of its files, so none of its effect keys resolve.";
            }

            // A child that names an effect KEY plays whatever that key maps to - through the skin's own resolver, or through a
            // gear upgrade's. Every target of both is already a seed below, so a hop by key adds a system's key and reason and
            // never a system. What is NOT consulted is any OTHER resolver in the closure, which the preview's merged map does:
            // a closure holds the resolvers of other skins too (a chroma's links its parent's), and a key that only one of those
            // maps would pull in a sibling skin's system. Measured over all 14,937 shipped skin bins, no skin reaches a system
            // only that way - so nothing is lost by leaving it out, and nothing can leak in.
            var gearResolvers = plan.Bins.SelectMany(b => b.Index.GearResolvers).ToList();
            var gearMap = new Dictionary<uint, uint>();
            foreach (var gear in gearResolvers) foreach (var (k, v) in gear.Map) gearMap.TryAdd(k, v);

            // Two walks over the same graph. The first follows only what the skin itself names; whatever the second adds is
            // reached through a gear upgrade, which is an inference and is listed as one.
            (Dictionary<uint, ReachedSystem> Reached, List<ReachedSystem> Order, int Unresolved) Walk(bool inferences)
            {
                var reached = new Dictionary<uint, ReachedSystem>();
                var order = new List<ReachedSystem>();
                var queue = new Queue<ReachedSystem>();
                int unresolved = 0;

                ReachedSystem? Hit(uint hash, string reason, uint? key)
                {
                    if (!systems.TryGetValue(hash, out var found)) return null;
                    if (!reached.TryGetValue(hash, out var item))
                    {
                        item = new ReachedSystem(found.System, found.Bin);
                        reached[hash] = item; order.Add(item); queue.Enqueue(item);
                    }
                    if (key is { } k && !item.Keys.Contains(k)) item.Keys.Add(k);
                    if (!item.Reasons.Contains(reason)) item.Reasons.Add(reason);
                    return item;
                }

                // 1. every target of the resolver
                if (resolver is not null)
                    foreach (var (key, target) in resolver.Map)
                        if (Hit(target, "effect key", key) is null) unresolved++;

                // 1b. and of every gear upgrade in the closure. A GearSkinUpgrade carries a resolver of its own
                //     (mGearData.mVFXResourceResolver) for the effects an upgraded gear plays - Lillia's E, Aatrox's
                //     prestige wings. Nothing in the skin object links to it. Measured over all 14,937 shipped skin bins,
                //     it is the ONE construct, in the skin bin (70 skins) or a shared bin (66), that reaches a system the
                //     linked resolver does not. That a GearSkinUpgrade in the closure is THIS skin's is an inference from how
                //     Riot groups the shared bins (an object sits in the closure of the skins that reference it); the 44
                //     closures that still hold an unused system show the grouping is not perfect, so it stays an inference.
                if (inferences)
                    foreach (var gear in gearResolvers)
                        foreach (var (key, target) in gear.Map)
                            if (Hit(target, "gear upgrade", key) is null) unresolved++;

                // 2. a system the skin object links directly
                if (skin is not null)
                    foreach (uint link in skin.ObjectLinks.Order())
                        if (link != skin.ResolverLink) Hit(link, "linked by the skin", null);

                // 3. children, recursively - a visited set is the whole cycle guard
                while (queue.Count > 0)
                {
                    _ct.ThrowIfCancellationRequested();
                    var parent = queue.Dequeue();
                    foreach (var child in parent.System.Children)
                    {
                        ReachedSystem? hit = null;
                        string reason = "child of " + parent.System.Name;
                        if (child.EffectLink != 0) hit = Hit(child.EffectLink, reason, null);
                        if (hit is null && child.EffectKey != 0)
                        {
                            uint key = child.EffectKey;
                            if (resolver is not null && resolver.Map.TryGetValue(key, out var own)) hit = Hit(own, reason, key);
                            else if (inferences && gearMap.TryGetValue(key, out var viaGear)) hit = Hit(viaGear, reason, key);
                            // a key nothing maps is a child nothing can play; it is counted, not worth a note per child
                        }
                        if (hit is null) unresolved++;
                    }
                }
                return (reached, order, unresolved);
            }

            var strong = Walk(inferences: false);
            var full = Walk(inferences: true);
            foreach (var item in full.Order) item.Inferred = !strong.Reached.ContainsKey(item.System.PathHash);

            // idle effects: the same targets, named by bone - only worth the second parse of the skin bin when we report
            if (detailed && plan.Skin.Bytes is { } skinBytes && resolver is not null)
                foreach (var idle in SkinIdleEffects.Read(skinBytes))
                    if (resolver.Map.TryGetValue(idle.EffectKey, out var idleTarget) && full.Reached.TryGetValue(idleTarget, out var item))
                    {
                        string reason = idle.BoneName.Length > 0 ? $"idle effect on {idle.BoneName}" : "idle effect";
                        if (!item.Reasons.Contains(reason)) item.Reasons.Add(reason);
                    }

            return new ReachResult(full.Order, systems.Keys.ToHashSet(), source, note,
                resolver?.Map.Count ?? 0, full.Unresolved, plan.Bins.Select(b => b.Path).ToList());
        }
    }

    private sealed class ReachedSystem
    {
        public ReachedSystem(VfxColorSystem system, string bin) { System = system; Bin = bin; }
        public VfxColorSystem System { get; }
        public string Bin { get; }
        public List<uint> Keys { get; } = new();
        public List<string> Reasons { get; } = new();
        /// <summary>Not reached by anything the skin itself names - see <see cref="EffectSystemEntry.IsInferred"/>.</summary>
        public bool Inferred { get; set; }
    }

    private sealed record ReachResult(List<ReachedSystem> Systems, HashSet<uint> ClosureSystems, string ResolverSource,
        string? ResolverNote, int ResolverEntries, int UnresolvedEntries, List<string> ClosureBins);

    // ============================================================ one skin, analysed =================

    private sealed class Analysis
    {
        private readonly ReachResult _reach;
        private readonly MaterialDocument? _document;
        private readonly SkinProblems _problems;
        private readonly SkinColorRequest _request;

        public Analysis(ReachResult reach, MaterialDocument? document, SkinProblems problems, SkinColorRequest request)
        {
            _reach = reach; _document = document; _problems = problems; _request = request;
        }

        /// <summary>Every texture the skin references in any role, and every system it uses.</summary>
        public SkinUses ToUses()
        {
            var textures = new HashSet<ulong>();
            if (_document is not null)
                foreach (var material in _document.Materials)
                    foreach (var slot in material.Slots)
                        if (!string.IsNullOrWhiteSpace(slot.Path)) textures.Add(BinTexturePath.HashOfReference(slot.Path));
            var systems = new HashSet<uint>();
            foreach (var system in _reach.Systems)
            {
                systems.Add(system.System.PathHash);
                foreach (ulong texture in system.System.AllTextureHashes) textures.Add(texture);
            }
            return new SkinUses(textures, systems, _problems);
        }

        public SkinColorInventory Build(string skinPath, string folder, int number, string label,
            Dictionary<ulong, List<string>> textureSharers, Dictionary<uint, List<string>> systemSharers,
            List<string> compared, bool sharingComputed, List<string> notes, List<string> warnings)
        {
            IReadOnlyList<string> Sharers(ulong hash) =>
                textureSharers.TryGetValue(hash, out var list) ? list : Array.Empty<string>();

            // ---- body ----
            var textures = new Dictionary<ulong, BodyTextureBuilder>();
            var excluded = new List<ExcludedSampler>();
            var parameters = new List<BodyColorParameter>();
            if (_document is not null)
                foreach (var material in _document.Materials)
                {
                    foreach (var slot in material.Slots)
                    {
                        string path = slot.Path;
                        if (string.IsNullOrWhiteSpace(path)) continue;
                        var role = Classify(material, slot, out string reason);
                        if (role is null) { excluded.Add(new ExcludedSampler(material.Name, slot.SamplerName, path, reason)); continue; }

                        ulong hash = BinTexturePath.HashOfReference(path);
                        if (!textures.TryGetValue(hash, out var builder)) textures[hash] = builder = new BodyTextureBuilder(path, hash, role.Value);
                        builder.Uses.Add(new BodyTextureUse(material.Name, slot.SamplerName, material.Submeshes, material.IsDefault));
                        if (material.IsLinked) builder.LinkedFrom ??= material.LinkedFromBin; else builder.HasLocalUse = true;
                    }
                    foreach (var parameter in material.Parameters)
                    {
                        if (!IsColorName(parameter.Name) || !parameter.TryGetColor(out Vector4 value)) continue;
                        parameters.Add(new BodyColorParameter(material.Name, parameter.Name, value, parameter.TypeName,
                            parameter.IsValueOmitted, material.IsLinked, material.LinkedFromBin));
                    }
                }

            var body = textures.Values
                .OrderBy(t => t.Role).ThenBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
                .Select(t => new BodyTexture(t.Path, t.Hash, t.Role, t.Uses, !t.HasLocalUse, t.HasLocalUse ? null : t.LinkedFrom, Sharers(t.Hash)))
                .ToList();

            // ---- effects ----
            // The parsed systems are cached by the bin's content, and their texture names were resolved when the bin was parsed:
            // after a hash sync a cached 0x... reference would stay unnamed. So the display names are resolved HERE, from the
            // dictionary as it is now, onto copies - the cache keeps only what the bin says.
            var effects = new List<EffectSystemEntry>();
            var effectTextures = new Dictionary<ulong, EffectTextureBuilder>();
            string DisplayName(ReachedSystem s) =>
                s.System.Name.StartsWith("0x", StringComparison.Ordinal) && _request.ResolveName?.Invoke(s.System.PathHash) is { Length: > 0 } named
                    ? named
                    : s.System.Name;
            foreach (var reached in _reach.Systems.OrderBy(DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.System.PathHash))
            {
                var system = reached.System;
                var emitters = _request.ResolveWadPath is { } resolve ? system.Emitters.Select(e => Rename(e, resolve)).ToList() : system.Emitters;
                effects.Add(new EffectSystemEntry(system.PathHash, DisplayName(reached), system.ParticlePath, reached.Keys.ToArray(),
                    reached.Reasons.ToArray(), reached.Bin, emitters,
                    systemSharers.TryGetValue(system.PathHash, out var shared) ? shared : Array.Empty<string>(), reached.Inferred));
                foreach (var emitter in emitters)
                    foreach (var texture in emitter.Textures)
                    {
                        if (!effectTextures.TryGetValue(texture.Hash, out var builder))
                            effectTextures[texture.Hash] = builder = new EffectTextureBuilder(texture.Path, texture.Hash);
                        builder.Systems.Add(system.PathHash);
                        if (!builder.Roles.Contains(texture.Role)) builder.Roles.Add(texture.Role);
                        builder.IsCubemap |= texture.IsCubemap;
                    }
            }
            var effectTextureList = effectTextures.Values
                .OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
                .Select(t => new EffectTextureEntry(t.Path, t.Hash, t.Roles, t.Systems.Count, t.IsCubemap, Sharers(t.Hash)))
                .ToList();

            var reach = new EffectReach(_reach.ClosureBins, _reach.ClosureSystems, _reach.ResolverSource,
                _reach.ResolverEntries, _reach.UnresolvedEntries, _problems.Unavailable);

            return new SkinColorInventory(skinPath, folder, number, label, body,
                parameters.OrderBy(p => p.Material, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                excluded.OrderBy(e => e.Material, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Sampler, StringComparer.OrdinalIgnoreCase).ToList(),
                effects, effectTextureList, reach, compared, sharingComputed, notes, warnings, _problems.UnresolvedMaterials);
        }

        /// <summary>A copy of the emitter whose unnamed (<c>0x...</c>) texture references are named from the dictionary as it
        /// is now; the emitter itself when nothing changes.</summary>
        private static VfxColorEmitter Rename(VfxColorEmitter emitter, Func<ulong, string?> resolve)
        {
            List<VfxColorTexture>? renamed = null;
            for (int i = 0; i < emitter.Textures.Count; i++)
            {
                var texture = emitter.Textures[i];
                if (!texture.Path.StartsWith("0x", StringComparison.Ordinal) || resolve(texture.Hash) is not { Length: > 0 } name) continue;
                renamed ??= emitter.Textures.ToList();
                renamed[i] = texture with { Path = name };
            }
            return renamed is null ? emitter : emitter with { Textures = renamed };
        }

        private sealed class BodyTextureBuilder
        {
            public BodyTextureBuilder(string path, ulong hash, BodyTextureRole role) { Path = path; Hash = hash; Role = role; }
            public string Path { get; }
            public ulong Hash { get; }
            public BodyTextureRole Role { get; }
            public List<BodyTextureUse> Uses { get; } = new();
            public bool HasLocalUse { get; set; }
            public string? LinkedFrom { get; set; }
        }

        private sealed class EffectTextureBuilder
        {
            public EffectTextureBuilder(string path, ulong hash) { Path = path; Hash = hash; }
            public string Path { get; }
            public ulong Hash { get; }
            public List<string> Roles { get; } = new();
            public HashSet<uint> Systems { get; } = new();
            public bool IsCubemap { get; set; }
        }
    }

    // ============================================================ body classification ================

    private static readonly Dictionary<string, BodyTextureRole?> SkinBlockFields = BuildSkinBlockFields();

    private static Dictionary<string, BodyTextureRole?> BuildSkinBlockFields()
    {
        // MaterialDocument names a skin-block slot by its field name, or by 0x<hash> when the dictionary does not
        // know it - so both spellings are keyed, and the scan does not depend on the dictionary for these four.
        var map = new Dictionary<string, BodyTextureRole?>(StringComparer.OrdinalIgnoreCase);
        void Add(string field, BodyTextureRole? role) { map[field] = role; map[$"0x{HashAlgorithms.Fnv1a(field):x8}"] = role; }
        Add("texture", BodyTextureRole.Diffuse);
        Add("emissiveTexture", BodyTextureRole.Emissive);
        Add("reflectionMap", BodyTextureRole.Reflection);
        Add("glossTexture", null);   // a specular gloss data map
        return map;
    }

    /// <summary>The role of one sampler, or null with the reason it is left out. By NAME - see the class remarks.</summary>
    internal static BodyTextureRole? Classify(MaterialBinding material, TextureSlot slot, out string reason)
    {
        string name = slot.SamplerName;
        reason = "";

        if (material.ShaderName == SkinBlockShader)
        {
            if (SkinBlockFields.TryGetValue(name, out var role))
            {
                if (role is null) reason = "gloss map: specular data, not colour";
                return role;
            }
            reason = "a skin-block texture field that is not a known colour map";
            return null;
        }
        if (material.ShaderName == InlineOverrideShader)
            return name.Equals("texture", StringComparison.OrdinalIgnoreCase) || name == $"0x{HashAlgorithms.Fnv1a("texture"):x8}"
                ? BodyTextureRole.Diffuse
                : Excluded("an inline override field that is not the texture", out reason);

        // a sampler whose own name says mask is one, whatever else its name says ("Color_Mask_Texture")
        if (name.Contains("Mask", StringComparison.OrdinalIgnoreCase)) return Excluded("mask", out reason);
        // a name that packs separate channels ("EmissionR_DistortionG_Texture") is a data map whatever its first word says
        if (IsChannelPacked(name))
            return Excluded("channel-packed data map: its name packs separate channels, so it is not one colour picture", out reason);
        if (slot.IsNormal) return Excluded("normal map", out reason);
        if (slot.IsEmissive) return BodyTextureRole.Emissive;
        if (slot.IsGradient) return BodyTextureRole.Gradient;
        if (slot.IsMatCap) return BodyTextureRole.MatCap;
        if (slot.IsDiffuse) return BodyTextureRole.Diffuse;
        return Excluded("not named as a colour map (data, noise, scroll or lookup texture)", out reason);

        static BodyTextureRole? Excluded(string why, out string reason) { reason = why; return null; }
    }

    /// <summary>
    /// Does a sampler's name pack channels - two or more channel tags, a tag being a single <c>R</c>, <c>G</c>, <c>B</c> or
    /// <c>A</c> that ends a word (<c>EmissionR_DistortionG_Texture</c>: emission in R, distortion in G)? Such a texture is a
    /// bundle of data maps, not a picture: a hue shift would mix the channels and corrupt both. Measured over the 203
    /// distinct sampler names of the shipped champion skins, this one name is both packed and colour-classified, and it is
    /// on 1,016 entries. Adjacent letters (<c>RGB</c>, <c>RGBA</c>, <c>RMA</c>) are not tags: they name a whole colour or an
    /// abbreviation. A single tag (<c>Mask_R</c>) is not enough on its own - none of the 94 classified names has one.
    /// </summary>
    public static bool IsChannelPacked(string samplerName)
    {
        int tags = 0;
        for (int i = 0; i < samplerName.Length; i++)
        {
            char c = samplerName[i];
            if (c is not ('R' or 'G' or 'B' or 'A')) continue;
            bool startsWord = i == 0 || !char.IsLetter(samplerName[i - 1]) || char.IsLower(samplerName[i - 1]);
            bool endsWord = i + 1 == samplerName.Length || !char.IsLetter(samplerName[i + 1]);
            if (startsWord && endsWord) tags++;
        }
        return tags >= 2;
    }

    /// <summary>The colour-parameter heuristic: the name contains color, colour or tint. See <see cref="BodyColorParameter"/>.
    /// One accident is filtered because it is certain: "tint" also spells itself across the end of "Distort" and the
    /// start of "Intensity", so <c>LightIntensity</c> and <c>DistortIntensity</c> (18 distinct names in the installed
    /// champion skins) would match; the word "intensity" is cut out before the tint test. The rest of the false
    /// positives (<c>Fresnel_Color_Intensity</c>, <c>ColorFresnelSize</c>) really do say colour in their names and stay.</summary>
    public static bool IsColorName(string name) =>
        name.Contains("color", StringComparison.OrdinalIgnoreCase)
        || name.Contains("colour", StringComparison.OrdinalIgnoreCase)
        || name.Replace("intensity", "", StringComparison.OrdinalIgnoreCase).Contains("tint", StringComparison.OrdinalIgnoreCase)
        || SkinBlockColorHashNames.Contains(name);

    /// <summary>The skin block's own two colour fields as <see cref="MaterialDocument"/> spells them when the hash
    /// dictionary cannot name them (<c>0x...</c>), so a scan without the dictionary still lists them.</summary>
    private static readonly HashSet<string> SkinBlockColorHashNames = new(StringComparer.OrdinalIgnoreCase)
    {
        $"0x{HashAlgorithms.Fnv1a("fresnelColor"):x8}", $"0x{HashAlgorithms.Fnv1a("reflectionFresnelColor"):x8}",
    };

    // ============================================================ one bin, indexed ===================

    private static BinIndex BuildIndex(byte[] bytes, ulong fingerprint, Func<ulong, string?>? resolveWadPath)
    {
        BinTree tree;
        try { tree = SafeBinTree.Parse(bytes); }
        catch (Exception ex) { return new BinIndex { Fingerprint = fingerprint, ParseError = $"{ex.GetType().Name}: {Tidy(ex.Message)}" }; }

        var index = new BinIndex
        {
            Fingerprint = fingerprint,
            Dependencies = tree.Dependencies.ToArray(),
            UnreadableObjects = SafeBinTree.IsLossy(tree, out var loss) && loss is not null ? loss.ObjectsAffected : 0,
        };
        foreach (var o in tree.Objects.Values)
        {
            if (o.ClassHash == VfxColorReader.SystemClass)
            {
                try { index.Systems[o.PathHash] = VfxColorReader.Read(o, resolveWadPath); }
                catch { /* a malformed system is skipped, as the resolver skips it */ }
            }
            else if (o.ClassHash == ResolverClass) index.Resolvers[o.PathHash] = ReadResolver(o.PathHash, o.Properties);
            else if (o.ClassHash == GearUpgradeClass && ReadGearResolver(o) is { } gear) index.GearResolvers.Add(gear);
            else if (o.ClassHash == SkinClass && index.Skin is null) index.Skin = ReadSkinObject(o);
        }
        // the same object MaterialDocument.Parse takes the skin block from: the first one that has one
        foreach (var o in tree.Objects.Values)
            if (o.Properties.TryGetValue(FSkinMeshProperties, out var block) && block is BinTreeStruct skinMesh)
            {
                index.MaterialLinks = ReadMaterialLinks(skinMesh);
                break;
            }
        return index;
    }

    /// <summary>The base mesh's material and every submesh override's, as <see cref="MaterialDocument.Parse"/> collects them to
    /// resolve: object links only, a zero link and an override without a submesh name ignored.</summary>
    private static List<MaterialLink> ReadMaterialLinks(BinTreeStruct skinMesh)
    {
        var links = new List<MaterialLink>();
        if (skinMesh.Properties.TryGetValue(FMaterial, out var baseMaterial) && baseMaterial is BinTreeObjectLink { Value: not 0 } baseLink)
            links.Add(new MaterialLink(null, baseLink.Value));
        if (skinMesh.Properties.TryGetValue(FMaterialOverride, out var overrides) && overrides is BinTreeContainer container)
            foreach (var element in container.Elements.OfType<BinTreeStruct>())
                if (element.Properties.TryGetValue(FSubmesh, out var submesh) && submesh is BinTreeString { Value.Length: > 0 } name
                    && element.Properties.TryGetValue(FMaterial, out var material) && material is BinTreeObjectLink { Value: not 0 } link)
                    links.Add(new MaterialLink(name.Value, link.Value));
        return links;
    }

    private static ResolverObject ReadResolver(uint pathHash, IReadOnlyDictionary<uint, BinTreeProperty> properties)
    {
        var resolver = new ResolverObject { PathHash = pathHash };
        if (!properties.TryGetValue(FResourceMap, out var property) && !properties.TryGetValue(FMResourceMap, out property)) return resolver;
        if (property is not BinTreeMap map) return resolver;
        foreach (var entry in map)
        {
            uint key = entry.Key switch { BinTreeHash h => h.Value, BinTreeU32 u => u.Value, _ => 0u };
            uint target = entry.Value switch { BinTreeObjectLink l => l.Value, BinTreeHash h => h.Value, BinTreeU32 u => u.Value, _ => 0u };
            if (key != 0 && target != 0) resolver.Map[key] = target;
        }
        return resolver;
    }

    /// <summary>A <c>GearSkinUpgrade</c>'s own resolver, <c>mGearData.mVFXResourceResolver.resourceMap</c> - the effects an
    /// upgraded gear plays (Lillia's E, Aatrox's prestige wings). Null when the object carries none.</summary>
    private static ResolverObject? ReadGearResolver(BinTreeObject o) =>
        o.Properties.TryGetValue(FGearData, out var data) && data is BinTreeStruct gear
        && gear.Properties.TryGetValue(FGearResolver, out var resolver) && resolver is BinTreeStruct embedded
            ? ReadResolver(o.PathHash, embedded.Properties)
            : null;

    private static SkinObject ReadSkinObject(BinTreeObject o)
    {
        var skin = new SkinObject
        {
            ResolverLink = o.Properties.TryGetValue(FResolverLink, out var link) && link is BinTreeObjectLink l ? l.Value : 0u,
        };
        foreach (var property in o.Properties.Values) CollectObjectLinks(property, skin.ObjectLinks, 0);
        return skin;
    }

    private static void CollectObjectLinks(BinTreeProperty property, HashSet<uint> into, int depth)
    {
        if (depth > 12) return;
        switch (property)
        {
            case BinTreeObjectLink { Value: not 0 } link: into.Add(link.Value); break;
            case BinTreeStruct s: foreach (var child in s.Properties.Values) CollectObjectLinks(child, into, depth + 1); break;
            case BinTreeContainer c when c.Elements.Count > 0 && c.Elements[0] is BinTreeStruct or BinTreeContainer or BinTreeObjectLink or BinTreeOptional or BinTreeMap:
                foreach (var child in c.Elements) CollectObjectLinks(child, into, depth + 1);
                break;
            case BinTreeOptional { Value: { } inner }: CollectObjectLinks(inner, into, depth + 1); break;
            case BinTreeMap m:
                foreach (var kv in m) { CollectObjectLinks(kv.Key, into, depth + 1); CollectObjectLinks(kv.Value, into, depth + 1); }
                break;
        }
    }

    /// <summary>An exception message for a warning line: one line, no control characters (a parser that met garbage quotes it), short.</summary>
    private static string Tidy(string text)
    {
        var sb = new StringBuilder();
        foreach (char c in text)
        {
            if (sb.Length >= 100) { sb.Append("..."); break; }
            sb.Append(char.IsControl(c) ? '?' : c);
        }
        return sb.ToString();
    }

    private static string FileName(string path)
    {
        int cut = path.LastIndexOf('/');
        return cut >= 0 ? path[(cut + 1)..] : path;
    }
}
