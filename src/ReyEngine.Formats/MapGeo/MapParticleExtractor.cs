using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.MapGeo;

/// <summary>One placed particle system on a map (M35): a <c>MapParticle</c> item inside a
/// <c>MapPlaceableContainer</c> — its world position, name, referenced VFX system and group.</summary>
public sealed record MapParticlePlacement(
    string Name, Vector3 Position, Matrix4x4 Transform, string SystemPath, string GroupName,
    uint SystemHash = 0, int VisibilityFlags = 255,
    // ---- M195 (tier 4.4): the 10 fields this extractor used to drop -------------------------------
    // EVERY optional is nullable on purpose. Five of these ship ONLY 'true' across the whole corpus, so
    // Riot's writer never records the other polarity and "absent" means the executable's default, which
    // is not knowable from the data. Collapsing absent to false would invent it.
    /// <summary>M195: the visibility controller this placement is bound to (0 = none). 4,237 placements
    /// carry one. This is the highest-value field of the ten: ReyEngine already decodes controllers for
    /// MESHES, and wiring particles to the same resolver is what makes them obey the baron-pit selector.</summary>
    uint VisibilityControllerHash = 0,
    /// <summary>13,165 placements, always true, Map11 only. Believed to be a graphics-quality cull, but
    /// nothing measured predicts which placements carry it - roughly 3,600 Map11 placements without it are
    /// ordinary decoration. Parsed and displayed; deliberately NOT used to hide anything.</summary>
    bool? EyeCandy = null,
    /// <summary>4,990 placements, always true, present on 98.9% of Map22/Map30 (TFT/Arena) placements and
    /// absent everywhere else. Its engine meaning is UNVERIFIED and it has no renderable effect in a
    /// single-instance viewport, so it is parsed for display only.</summary>
    bool? AllDimensions = null,
    /// <summary>1,370. The game shows these on a gameplay event rather than at load.</summary>
    bool? StartDisabled = null,
    /// <summary>1,159. One-shot bursts, largely elemental-rift transitions.</summary>
    bool? Transitional = null,
    /// <summary>172. Not identity on any of them: 52 tint-only, 72 alpha-only, 48 both.</summary>
    Vector4? ColorModulate = null,
    /// <summary>163, values {1,2,3}. Meaning UNKNOWN - an Order/Chaos reading fits the base-door shields
    /// but is broken by the river-shore placements.</summary>
    uint? VisibilityMode = null,
    /// <summary>7 placements. What it does to the transform is UNKNOWN.</summary>
    bool? AttachToCamera = null,
    /// <summary>3 placements. Read as a raw integer; a bitmask reading fits 80 of 81 samples, which is not
    /// enough to gate anything on.</summary>
    int? Quality = null,
    /// <summary>True when mVisibilityFlags was actually authored. 255 is ReyEngine's permissive substitute
    /// and is never authored (0 of 12,845), so without this the substitute is indistinguishable from data.</summary>
    bool HasVisibilityFlags = false)
{
    /// <summary>The VFX system's short name (leaf of the resolved path) for display.</summary>
    public string SystemName => SystemPath.Contains('/') ? SystemPath[(SystemPath.LastIndexOf('/') + 1)..] : SystemPath;
}

/// <summary>
/// Reads the placed particle systems from a map's companion .materials.bin (they live in
/// <c>MapPlaceableContainer.items</c> alongside props/locators). Position comes from the item's
/// transform translation; the system link resolves to a Maps/Particles/... path. Never throws.
/// </summary>
public static class MapParticleExtractor
{
    public static IReadOnlyList<MapParticlePlacement> Extract(byte[] materialsBin, Func<uint, string?> resolve)
    {
        var result = new List<MapParticlePlacement>();
        BinTree bin;
        try { bin = SafeBinTree.Parse(materialsBin); }
        catch { return result; }

        foreach (var o in bin.Objects.Values)
        {
            if (!IsClass(o.ClassHash, "MapPlaceableContainer", resolve)) continue;
            if (Field(o.Properties, "items") is not { } items || items is not System.Collections.IEnumerable en) continue;

            foreach (var it in en)
            {
                // items is a map (hash -> struct); each entry exposes a Value property.
                if (it.GetType().GetProperty("Value")?.GetValue(it) is not BinTreeStruct s) continue;
                if (!IsClass(s.ClassHash, "MapParticle", resolve)) continue;

                var transform = Field(s.Properties, "transform") is BinTreeMatrix44 m ? m.Value : Matrix4x4.Identity;
                string name = (Field(s.Properties, "name") as BinTreeString)?.Value ?? "(particle)";
                string group = (Field(s.Properties, "groupName") as BinTreeString)?.Value ?? "";
                uint systemHash = Field(s.Properties, "system") is BinTreeObjectLink link ? link.Value : 0;
                int visibilityFlags = Field(s.Properties, "mVisibilityFlags") switch
                {
                    BinTreeU8 v => v.Value,
                    BinTreeU16 v => v.Value,
                    BinTreeU32 v => unchecked((int)v.Value),
                    BinTreeI32 v => v.Value,
                    _ => 255,
                };
                string system = systemHash != 0 ? resolve(systemHash) ?? $"0x{systemHash:x8}" : "";

                // M195 (4.4): the ten fields that used to be dropped. All reads are pattern matches with a
                // null fallback, so a surprising type can never throw out of the extractor.
                result.Add(new MapParticlePlacement(
                    name, transform.Translation, transform, system, group, systemHash, visibilityFlags,
                    VisibilityControllerHash: Field(s.Properties, "VisibilityController") is BinTreeObjectLink vc ? vc.Value : 0,
                    EyeCandy: Flag(s.Properties, "eyeCandy"),
                    AllDimensions: Flag(s.Properties, "AllDimensions"),
                    StartDisabled: Flag(s.Properties, "startDisabled"),
                    Transitional: Flag(s.Properties, "Transitional"),
                    ColorModulate: Field(s.Properties, "colorModulate") switch
                    {
                        BinTreeVector4 v4 => v4.Value,
                        BinTreeColor c => new Vector4(c.Value.R, c.Value.G, c.Value.B, c.Value.A),
                        _ => null,
                    },
                    VisibilityMode: Field(s.Properties, "visibilityMode") switch
                    {
                        BinTreeU32 u => u.Value,
                        BinTreeU8 b => b.Value,
                        _ => null,
                    },
                    AttachToCamera: Flag(s.Properties, "AttachToCamera"),
                    Quality: Field(s.Properties, "quality") is BinTreeI32 q ? q.Value : null,
                    HasVisibilityFlags: Field(s.Properties, "mVisibilityFlags") is not null));
            }
        }
        return result;
    }

    /// <summary>A bool that stays null when absent. Riot omits default-valued properties, so absent is NOT
    /// false - and for the five flags here that only ever ship 'true', the default is unknowable.</summary>
    private static bool? Flag(IReadOnlyDictionary<uint, BinTreeProperty> props, string name) =>
        Field(props, name) switch
        {
            BinTreeBool b => b.Value,
            BinTreeBitBool b => b.Value,
            _ => null,
        };

    private static bool IsClass(uint classHash, string name, Func<uint, string?> resolve) =>
        classHash == HashAlgorithms.Fnv1a(name)
        || classHash == HashAlgorithms.Fnv1aRaw(name)
        || string.Equals(resolve(classHash), name, StringComparison.OrdinalIgnoreCase);

    private static BinTreeProperty? Field(IReadOnlyDictionary<uint, BinTreeProperty> props, string name)
    {
        if (props.TryGetValue(HashAlgorithms.Fnv1a(name), out var p)) return p;
        if (props.TryGetValue(HashAlgorithms.Fnv1aRaw(name), out p)) return p;
        return null;
    }
}
