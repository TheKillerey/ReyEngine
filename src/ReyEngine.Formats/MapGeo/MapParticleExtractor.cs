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
    uint SystemHash = 0, int VisibilityFlags = 255)
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
                result.Add(new MapParticlePlacement(name, transform.Translation, transform, system, group, systemHash, visibilityFlags));
            }
        }
        return result;
    }

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
