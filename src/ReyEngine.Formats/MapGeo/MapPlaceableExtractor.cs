using System.Collections;
using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.Formats.MapGeo;

/// <summary>A placed cubemap reflection probe (M38): captures the environment for reflections at a point.</summary>
public sealed record MapCubemapProbe(string Name, Vector3 Position, Matrix4x4 Transform, string? CubemapPath,
    int VisibilityFlags = 255, bool HasVisibilityFlags = false, MapPlacementId Id = default)
{
    /// <summary>Leaf file name of the baked cubemap (for display), or null.</summary>
    public string? CubemapFile => CubemapPath is null ? null
        : CubemapPath.Contains('/') ? CubemapPath[(CubemapPath.LastIndexOf('/') + 1)..] : CubemapPath;
}

/// <summary>A placed character/animated prop (M38): a creature or prop with a character record + skin
/// (Baron, jungle camps, etc.) positioned on the map.</summary>
/// <summary>M55: a placed ambient sound (MapAudio, class 0xa783cfd5) — a Wwise event at a world position.
/// Map12-style maps place these directly (58 on Bloom); Map11 instead drives ambience via audio-emitter
/// VFX MapParticles + a painted-region tag registry (so it may have none).</summary>
public sealed record MapSoundPlacement(
    string Name, string EventName, Vector3 Position, Matrix4x4 Transform,
    float Radius = 4000f, int VisibilityFlags = 255, bool FromParticleSystem = false,
    bool Loop = true,
    /// <summary>M202: where this placement lives in the tree, so saving does not have to find it by its
    /// transform bytes. Default (invalid) for sounds DERIVED from a particle system - those are a view of
    /// the particle and have no MapAudio entry of their own, so the particle is what gets saved.</summary>
    MapPlacementId Id = default,
    bool HasVisibilityFlags = false,
    /// <summary>M802: the visibility controller of the particle this sound is DERIVED from (0 = none, and always 0
    /// for a direct MapAudio - none of Riot's carry one). A particle's sound follows its particle, so an event that
    /// gates the particle gates its ambience too.</summary>
    uint VisibilityControllerHash = 0);

/// <param name="IdleAnimation">M723: the clip the placement's <c>CharacterMesh</c> names, "" when it names
/// none. This - not a guess from the skin's graph - is what the game asks the animation graph for.</param>
/// <param name="PlaysIdle">M723: whether the placement carries <c>PlayIdleAnimation</c>. The schema
/// defaults it to false, so a prop without it stands in bind pose in-game however many clips its skin has.</param>
/// <param name="IsAnimatedPropClass">M747: read from Riot's <c>MapAnimatedProp</c> class (PropName + SkinID)
/// rather than from a scenery-character placement (a <c>Character</c> component). The record and skin are
/// then the paths that class implies: Characters/&lt;PropName&gt;/CharacterRecords/Root and
/// Characters/&lt;PropName&gt;/Skins/Skin&lt;SkinID&gt; - where 3,052 of the 3,058 shipped props find their skin.</param>
/// <param name="AppearAfterSeconds">M748: the game-clock gate ReyEngine wrote on this placement, or null.</param>
public sealed record MapAnimatedProp(string Name, Vector3 Position, Matrix4x4 Transform, string CharacterRecord, string Skin,
    int VisibilityFlags = 255, bool HasVisibilityFlags = false, MapPlacementId Id = default,
    string IdleAnimation = "", bool PlaysIdle = false, bool IsAnimatedPropClass = false,
    float? AppearAfterSeconds = null)
{
    /// <summary>Short character identity, e.g. "SRU_Baron" from "Characters/SRU_Baron/CharacterRecords/Root".</summary>
    public string CharacterName
    {
        get
        {
            var parts = CharacterRecord.Split('/');
            int i = Array.FindIndex(parts, p => p.Equals("Characters", StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < parts.Length ? parts[i + 1] : (parts.Length > 0 ? parts[^1] : CharacterRecord);
        }
    }

    /// <summary>Skin leaf, e.g. "Skin0".</summary>
    public string SkinName => Skin.Contains('/') ? Skin[(Skin.LastIndexOf('/') + 1)..] : Skin;

    /// <summary>Display label combining character + skin.</summary>
    public string Display => SkinName.Equals("Skin0", StringComparison.OrdinalIgnoreCase) ? CharacterName : $"{CharacterName} / {SkinName}";
}

/// <summary>
/// M805: an esports sponsor banner - a <c>GdsMapObject</c> whose <c>extraInfo</c> holds a <c>GDSMapObjectBannerInfo</c>.
/// Summoner's Rift places 117 (chunk Maps/MapGeometry/SR/Chunks/Esports_Banners in base_srx; 117 in every Map11 skin that
/// has them), every one gated by the MapObjectESportSponsorBanners event (MutatorMapVisibilityController 0x11a9b55d).
///
/// <para><b>What stands there.</b> The object names no mesh. It is a LevelProp, named "LevelProp_" + a character + a
/// number ("LevelProp_Srx_Banner_Vertical1"), and that character's Skin0 - Srx_Banner_Hero, _Horizontal, _Vertical or
/// _VerticalThin, all four in Map11.wad - is drawn at <see cref="Transform"/>, the way a placed prop's skin is. Read from
/// the data rather than the client: every banner name resolves to a character the map WAD ships, and that character's
/// mesh under the transform lands on the object's authored boxMin/boxMax.</para>
///
/// <para><b>Sponsor art.</b> <see cref="BannerName"/> ("ORDER_MIDLANE_VERT_BANNER_1", from the linked EsportsBannerData)
/// is a slot: map11.bin's EsportsRotatingBannerConfiguration fills it at runtime, per league, with a texture under
/// assets/esports/sponsoredbanners/secret/ - all encrypted (M353). The banner skins' own flag material names
/// srx_banner_flags.tex, which is what the editor shows.</para>
/// </summary>
/// <param name="CharacterName">"Srx_Banner_Vertical" for "LevelProp_Srx_Banner_Vertical1" - see <see cref="CharacterOf"/>;
/// "" when the name implies none.</param>
/// <param name="Id">The container and item key the object lives under (read-only: nothing writes banners).</param>
public sealed record MapBannerProp(string Name, Vector3 Position, Matrix4x4 Transform, string CharacterName,
    string BannerName, uint VisibilityControllerHash, MapPlacementId Id = default)
{
    /// <summary>The skin drawn: Characters/&lt;CharacterName&gt;/Skins/Skin0, the one skin (beside Root) each banner
    /// character ships. "" when <see cref="CharacterName"/> is.</summary>
    public string Skin => CharacterName.Length == 0 ? "" : $"Characters/{CharacterName}/Skins/Skin0";

    /// <summary>The character a LevelProp names: the "LevelProp_" prefix and the trailing number dropped. "" for a name
    /// without the prefix, or with nothing left after it.</summary>
    public static string CharacterOf(string name)
    {
        const string prefix = "LevelProp_";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return "";
        return name[prefix.Length..].TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
    }
}

/// <summary>
/// Reads the remaining <c>MapPlaceableContainer.items</c> types beyond particles (M38): cubemap reflection
/// probes (<c>MapCubemapProbe</c>) and placed characters / animated props (identified structurally by a
/// <c>characterRecord</c> field). Never throws.
/// </summary>
public static class MapPlaceableExtractor
{
    private static readonly uint ContainerClass = HashAlgorithms.Fnv1a("MapPlaceableContainer");
    private static readonly uint CubemapProbeClass = HashAlgorithms.Fnv1a("MapCubemapProbe");
    private static readonly uint MapAudioClass = HashAlgorithms.Fnv1a("MapAudio");   // 0xa783cfd5
    private static readonly uint F_eventName = HashAlgorithms.Fnv1a("eventName");    // 0x2a078c9c (Wwise event)
    private static readonly uint F_transform = HashAlgorithms.Fnv1a("transform");
    private static readonly uint F_name = HashAlgorithms.Fnv1a("name");
    private static readonly uint F_characterRecord = HashAlgorithms.Fnv1a("characterRecord");
    private static readonly uint F_skin = HashAlgorithms.Fnv1a("skin");
    private static readonly uint F_characterMesh = HashAlgorithms.Fnv1a("CharacterMesh");
    private static readonly uint F_idleAnimationName = HashAlgorithms.Fnv1a("IdleAnimationName");
    private static readonly uint F_playIdleAnimation = HashAlgorithms.Fnv1a("PlayIdleAnimation");
    private static readonly uint F_visibilityFlags = HashAlgorithms.Fnv1a("mVisibilityFlags");
    private static readonly uint AnimatedPropClass = HashAlgorithms.Fnv1a("MapAnimatedProp");
    private static readonly uint F_propName = HashAlgorithms.Fnv1a("PropName");
    private static readonly uint F_skinId = HashAlgorithms.Fnv1a("SkinID");
    private static readonly uint F_cubemapTexture = 0xfe380acfu;   // texture path string on MapCubemapProbe

    /// <param name="resolveWadPath">M590: cubemapTexture became a WadChunkLink in 16.17.</param>
    public static (IReadOnlyList<MapCubemapProbe> Probes, IReadOnlyList<MapAnimatedProp> Props, IReadOnlyList<MapSoundPlacement> Sounds) Extract(byte[] materialsBin,
        Func<ulong, string?>? resolveWadPath = null)
    {
        var probes = new List<MapCubemapProbe>();
        var props = new List<MapAnimatedProp>();
        var sounds = new List<MapSoundPlacement>();
        BinTree bin;
        try { bin = SafeBinTree.Parse(materialsBin); }
        catch { return (probes, props, sounds); }

        foreach (var o in bin.Objects.Values)
        {
            if (o.ClassHash != ContainerClass) continue;
            if (Field(o.Properties, "items") is not IEnumerable en) continue;

            foreach (var it in en)
            {
                if (it.GetType().GetProperty("Value")?.GetValue(it) is not BinTreeStruct s) continue;
                if (s.ClassHash == 0) continue;   // null map entries (thousands per bin) — skip
                var transform = s.Properties.TryGetValue(F_transform, out var tp) && tp is BinTreeMatrix44 m ? m.Value : Matrix4x4.Identity;
                var id = new MapPlacementId(o.PathHash,
                    it.GetType().GetProperty("Key")?.GetValue(it) is BinTreeHash kh ? kh.Value : 0u);
                bool hasVisibility = s.Properties.TryGetValue(F_visibilityFlags, out var visibilityProperty);
                int visibility = visibilityProperty switch
                {
                    BinTreeU8 u8 => u8.Value,
                    BinTreeU16 u16 => u16.Value,
                    BinTreeU32 u32 => unchecked((int)u32.Value),
                    _ => 255,
                };

                if (s.ClassHash == CubemapProbeClass)
                {
                    probes.Add(new MapCubemapProbe(
                        NameOf(s), transform.Translation, transform,
                        Meta.BinTexturePath.Read(Get(s, F_cubemapTexture), resolveWadPath) is { Length: > 0 } cm ? cm : null,
                        visibility, hasVisibility, id));
                }
                else if (s.ClassHash == MapAudioClass)
                {
                    sounds.Add(new MapSoundPlacement(
                        NameOf(s),
                        (Get(s, F_eventName) as BinTreeString)?.Value ?? "",
                        transform.Translation, transform,
                        VisibilityFlags: visibility,
                        Id: id,
                        HasVisibilityFlags: hasVisibility));
                }
                else if (s.ClassHash == AnimatedPropClass)
                {
                    // M747: Riot's MapAnimatedProp - 3,058 on the shipped maps (SR's ducks, Noxtorra ...) that
                    // this reader skipped, because it recognised a prop only by a Character component.
                    string prop = (Get(s, F_propName) as BinTreeString)?.Value ?? "";
                    if (prop.Length == 0) continue;
                    uint skinId = (Get(s, F_skinId) as BinTreeU32)?.Value ?? 0;
                    string idle = (Get(s, F_idleAnimationName) as BinTreeString)?.Value ?? "";
                    bool plays = Get(s, F_playIdleAnimation) is BinTreeBool { Value: true };
                    props.Add(new MapAnimatedProp(NameOf(s), transform.Translation, transform,
                        $"Characters/{prop}/CharacterRecords/Root", $"Characters/{prop}/Skins/Skin{skinId}",
                        visibility, hasVisibility, id, idle, plays, IsAnimatedPropClass: true,
                        AppearAfterSeconds: MapPlaceableWriter.ReadAppearAfter(bin, s)));
                }
                else if (FindCharacterData(s) is ({ } cr, var skin))
                {
                    var (idle, plays) = FindCharacterMesh(s);
                    props.Add(new MapAnimatedProp(NameOf(s), transform.Translation, transform, cr, skin ?? "",
                        visibility, hasVisibility, id, idle, plays));
                }
            }
        }
        return (probes, props, sounds);
    }

    // M805: the esports banners (see MapBannerProp)
    private static readonly uint GdsMapObjectClass = HashAlgorithms.Fnv1a("GdsMapObject");             // 0xda9e5c0c
    private static readonly uint BannerInfoClass = HashAlgorithms.Fnv1a("GDSMapObjectBannerInfo");      // 0x69f67d4a
    private static readonly uint F_extraInfo = HashAlgorithms.Fnv1a("extraInfo");
    private static readonly uint F_bannerData = HashAlgorithms.Fnv1a("BannerData");
    private static readonly uint F_bannerName = HashAlgorithms.Fnv1a("bannerName");
    private static readonly uint F_visibilityController = HashAlgorithms.Fnv1a("VisibilityController");

    /// <summary>
    /// M805: the esports sponsor banners of a map's materials bin - every <c>GdsMapObject</c> whose <c>extraInfo</c>
    /// carries a <c>GDSMapObjectBannerInfo</c>. No other GdsMapObject is read: the spawn nodes (type 9) and the other
    /// LevelProps (the snails, gromp props, lizards ... of type 10) are left as they were. Never throws.
    /// <para><b>One banner per item key.</b> Bloom and the boba skins carry their 117 banners twice: in the chunk their
    /// MapContainer's <c>chunks</c> lists (a copy of their own, with two or three banners moved for that skin's terrain)
    /// and in the base chunk, which they do not list. Drawn twice, the moved ones would stand beside themselves and the
    /// rest z-fight. A key found in several containers is read from the one <c>chunks</c> lists, otherwise from the first.
    /// A container nothing lists is still read - every other placeable is read that way (ruby_sr ships its banners in one).</para>
    /// </summary>
    public static IReadOnlyList<MapBannerProp> ExtractBanners(byte[] materialsBin)
    {
        var banners = new List<MapBannerProp>();
        BinTree bin;
        try { bin = SafeBinTree.Parse(materialsBin); }
        catch { return banners; }

        // the containers a MapContainer builds the map from (chunks: map[hash,link])
        var listed = new HashSet<uint>();
        foreach (var o in bin.Objects.Values)
            if (Field(o.Properties, "chunks") is BinTreeMap chunks)
                foreach (var chunk in chunks)
                    if (chunk.Value is BinTreeObjectLink link) listed.Add(link.Value);

        var byKey = new Dictionary<uint, (int At, bool Listed)>();
        foreach (var o in bin.Objects.Values)
        {
            if (o.ClassHash != ContainerClass || Field(o.Properties, "items") is not BinTreeMap items) continue;
            bool containerListed = listed.Contains(o.PathHash);
            foreach (var kv in items)
            {
                if (kv.Value is not BinTreeStruct s || s.ClassHash != GdsMapObjectClass) continue;
                if (Get(s, F_extraInfo) is not BinTreeContainer extra
                    || extra.Elements.OfType<BinTreeStruct>().FirstOrDefault(e => e.ClassHash == BannerInfoClass) is not { } info)
                    continue;
                uint key = kv.Key is BinTreeHash kh ? kh.Value : 0u;
                if (byKey.TryGetValue(key, out var seen))
                {
                    if (seen.Listed || !containerListed) continue;            // keep the first copy, or the listed one
                    banners[seen.At] = ReadBanner(bin, o.PathHash, key, s, info); // the listed chunk's copy replaces an unlisted one
                    byKey[key] = (seen.At, true);
                    continue;
                }
                byKey[key] = (banners.Count, containerListed);
                banners.Add(ReadBanner(bin, o.PathHash, key, s, info));
            }
        }
        return banners;
    }

    private static MapBannerProp ReadBanner(BinTree bin, uint container, uint key, BinTreeStruct s, BinTreeStruct info)
    {
        var transform = Get(s, F_transform) is BinTreeMatrix44 m ? m.Value : Matrix4x4.Identity;
        string name = NameOf(s);
        // the slot name lives on the EsportsBannerData the info links, in the same bin
        string slot = Get(info, F_bannerData) is BinTreeObjectLink data && data.Value != 0
            && bin.Objects.TryGetValue(data.Value, out var dataObj)
            && dataObj.Properties.TryGetValue(F_bannerName, out var bn) && bn is BinTreeString bs ? bs.Value : "";
        return new MapBannerProp(name, transform.Translation, transform, MapBannerProp.CharacterOf(name), slot,
            Get(s, F_visibilityController) is BinTreeObjectLink vc ? vc.Value : 0u,
            new MapPlacementId(container, key));
    }

    /// <summary>Placed characters wrap their character record + skin in an embedded "character data" struct.
    /// Search the item's embedded structs for one carrying a <c>characterRecord</c> string.</summary>
    private static (string? CharacterRecord, string? Skin) FindCharacterData(BinTreeStruct s)
    {
        foreach (var (_, p) in s.Properties)
        {
            if (p is BinTreeStruct emb && emb.Properties.TryGetValue(F_characterRecord, out var crp) && crp is BinTreeString cr)
                return (cr.Value, (emb.Properties.TryGetValue(F_skin, out var sk) ? sk : null) is BinTreeString skin ? skin.Value : null);
        }
        return (null, null);
    }

    /// <summary>M723: the placement's <c>CharacterMesh</c> component - which clip it names and whether it
    /// asks for it to be played. Found by field rather than by class, the same way the record above is,
    /// so a placement whose component class changes between patches still reads.</summary>
    private static (string IdleAnimation, bool PlaysIdle) FindCharacterMesh(BinTreeStruct s)
    {
        if (s.Properties.GetValueOrDefault(F_characterMesh) is not BinTreeStruct mesh) return ("", false);
        string idle = mesh.Properties.GetValueOrDefault(F_idleAnimationName) is BinTreeString n ? n.Value : "";
        bool plays = mesh.Properties.GetValueOrDefault(F_playIdleAnimation) is BinTreeBool { Value: true };
        return (idle, plays);
    }

    private static string NameOf(BinTreeStruct s) => Get(s, F_name) switch
    {
        BinTreeString str => str.Value,
        BinTreeHash h => $"0x{h.Value:x8}",
        _ => "(unnamed)"
    };

    private static BinTreeProperty? Get(BinTreeStruct s, uint hash) => s.Properties.TryGetValue(hash, out var p) ? p : null;

    private static BinTreeProperty? Field(IReadOnlyDictionary<uint, BinTreeProperty> props, string name)
        => props.TryGetValue(HashAlgorithms.Fnv1a(name), out var p) ? p
         : props.TryGetValue(HashAlgorithms.Fnv1aRaw(name), out var q) ? q : null;
}

/// <summary>Map11 also authors positional audio as MapParticle placements whose VFX system carries
/// soundPersistentDefault/soundOnCreateDefault. Convert those systems into the same sound-placement
/// model as direct MapAudio objects so discovery, visibility, markers, and playback share one path.</summary>
public static class MapParticleAudioExtractor
{
    public static IReadOnlyList<MapSoundPlacement> Extract(
        IEnumerable<MapParticlePlacement> particles,
        IReadOnlyDictionary<uint, VfxSystemDefinition> systems)
    {
        var sounds = new List<MapSoundPlacement>();
        foreach (var particle in particles)
        {
            if (!systems.TryGetValue(particle.SystemHash, out var system)) continue;
            var events = new[]
            {
                (Name: system.PersistentSoundEventName, Loop: true),
                (Name: system.OnCreateSoundEventName, Loop: false),
            };
            foreach (var sound in events.Distinct())
            {
                if (string.IsNullOrWhiteSpace(sound.Name)) continue;
                sounds.Add(new MapSoundPlacement(
                    particle.Name, sound.Name, particle.Position, particle.Transform,
                    system.VisibilityRadius > 0f ? system.VisibilityRadius : 4000f,
                    particle.VisibilityFlags, true, sound.Loop,
                    VisibilityControllerHash: particle.VisibilityControllerHash));   // M802
            }
        }
        return sounds;
    }
}
