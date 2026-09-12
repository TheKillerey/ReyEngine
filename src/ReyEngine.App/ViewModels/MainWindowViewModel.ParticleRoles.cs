using System;
using System.Collections.Generic;
using System.Linq;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.App.ViewModels;

public partial class MainWindowViewModel
{
    /// <summary>M713: what the game says each system in the open particle bin is for, by system hash.
    /// Rebuilt when a different bin is opened, because it is derived entirely from that bin's champion.</summary>
    private IReadOnlyDictionary<uint, VfxSystemLink> _particleRoles =
        new Dictionary<uint, VfxSystemLink>();

    /// <summary>
    /// M713: read the game's own statement about every VFX system a champion owns.
    ///
    /// <para><b>When this is available, plainly.</b> Only from a champion. Of the six routes that open the
    /// particle editor, every one passes a <c>WadAssetEntry</c> and nothing else, and the champion has to
    /// be read out of that entry's path - <c>data/characters/&lt;champ&gt;/…</c>. A map's mapXX.bin, the
    /// modespecificdata bins and a workshop loose file have no champion at all, so they get nothing and
    /// fall back to the name. That is not a gap to close later: those systems are placed by the map or
    /// spawned by a parent, and no spell record names them.</para>
    ///
    /// <para><b>Three bins, and this is why it runs off the UI thread.</b> The spell objects are in the
    /// champion's root bin. The effect key they carry is skin-independent, so every skin bin's
    /// <c>ResourceResolver.resourceMap</c> has to be read to turn those keys into the systems THAT skin
    /// points at - a champion with sixty skins is sixty small bins. The systems themselves are usually in
    /// a third file again, a shared <c>_multi_skins_</c> dependency bin, which is often the very bin the
    /// editor has open.</para>
    ///
    /// <para>Failures are silent by design: a champion whose bins are not mounted, a bin that will not
    /// parse, a skin with no resolver. Each one costs the reader the authored answer and leaves them the
    /// name guess, which is what they had before.</para>
    /// </summary>
    private IReadOnlyDictionary<uint, VfxSystemLink> BuildParticleRoles(string? binPath)
    {
        var empty = new Dictionary<uint, VfxSystemLink>();
        if (ChampionOf(binPath) is not { Length: > 0 } champion) return empty;

        byte[]? recordBin = null;
        if (TryResolveEntry(HashAlgorithms.WadPath(ChampionRecord.PathFor(champion)), out var record))
            try { recordBin = ReadAsset(record.PathHash); } catch { }

        // Every skin bin of this champion: each carries one resource map, and a key one skin suppresses
        // another may still map.
        string prefix = $"data/characters/{champion}/skins/";
        var skinBins = AssetEntries
            .Where(e => e.IsResolved
                        && e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && e.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (recordBin is null && skinBins.Count == 0) return empty;

        var fromSpells = recordBin is not null
            ? VfxSystemRoles.FromSpells(recordBin, champion)
            : new Dictionary<uint, VfxSystemLink>();

        var bySystem = new Dictionary<uint, VfxSystemLink>();
        foreach (var skin in skinBins)
        {
            byte[] bytes;
            try { bytes = ReadAsset(skin.PathHash); } catch { continue; }

            IReadOnlyDictionary<uint, uint> map;
            try { map = VfxSystemResolver.ExtractResourceMap(bytes); } catch { continue; }
            if (map.Count == 0) continue;

            // The spell record is the stronger statement and goes in first, so a system that is both a
            // missile and an idle attachment reads as the missile.
            VfxSystemRoles.Resolve(fromSpells, map, bySystem);
            VfxSystemRoles.Resolve(VfxSystemRoles.FromSkin(bytes, ResolveBinName), map, bySystem);
        }
        return bySystem;
    }

    /// <summary>The champion a bin belongs to, or null when it belongs to none - a map bin, a loose file,
    /// anything outside <c>data/characters/</c>.</summary>
    public static string? ChampionOf(string? binPath)
    {
        if (string.IsNullOrWhiteSpace(binPath)) return null;
        var parts = binPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 1 < parts.Length; i++)
            if (parts[i].Equals("characters", StringComparison.OrdinalIgnoreCase))
                return parts[i + 1].ToLowerInvariant();
        return null;
    }
}
