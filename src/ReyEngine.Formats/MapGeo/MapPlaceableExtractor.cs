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
    bool HasVisibilityFlags = false);

/// <param name="IdleAnimation">M723: the clip the placement's <c>CharacterMesh</c> names, "" when it names
/// none. This - not a guess from the skin's graph - is what the game asks the animation graph for.</param>
/// <param name="PlaysIdle">M723: whether the placement carries <c>PlayIdleAnimation</c>. The schema
/// defaults it to false, so a prop without it stands in bind pose in-game however many clips its skin has.</param>
/// <param name="IsAnimatedPropClass">M747: read from Riot's <c>MapAnimatedProp</c> class (PropName + SkinID)
/// rather than from a scenery-character placement (a <c>Character</c> component). The record and skin are
/// then the paths that class implies: Characters/&lt;PropName&gt;/CharacterRecords/Root and
/// Characters/&lt;PropName&gt;/Skins/Skin&lt;SkinID&gt; - where 3,052 of the 3,058 shipped props find their skin.</param>
public sealed record MapAnimatedProp(string Name, Vector3 Position, Matrix4x4 Transform, string CharacterRecord, string Skin,
    int VisibilityFlags = 255, bool HasVisibilityFlags = false, MapPlacementId Id = default,
    string IdleAnimation = "", bool PlaysIdle = false, bool IsAnimatedPropClass = false)
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
                        visibility, hasVisibility, id, idle, plays, IsAnimatedPropClass: true));
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
                    particle.VisibilityFlags, true, sound.Loop));
            }
        }
        return sounds;
    }
}
